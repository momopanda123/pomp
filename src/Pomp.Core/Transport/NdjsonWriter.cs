using System;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Pomp.Hosting;

namespace Pomp.Transport
{
    /// <summary>
    /// Writes one JSON object per line (UTF-8 without BOM, <c>\n</c> terminator) to omp's stdin. Writes are serialized
    /// with a semaphore and each frame is flushed. Cancellation is only honoured while waiting for the turn to write:
    /// once a frame is on its way it is completed, because abandoning it would leave a partial line in the pipe.
    /// </summary>
    internal sealed class NdjsonWriter
    {
        private static readonly UTF8Encoding Utf8NoBom = new UTF8Encoding(false);

        private readonly Stream _stream;
        private readonly SemaphoreSlim _gate = new SemaphoreSlim(1, 1);
        private volatile bool _closed;

        public NdjsonWriter(Stream stream)
        {
            _stream = stream ?? throw new ArgumentNullException(nameof(stream));
        }

        private static readonly string LineSeparator = ((char)0x2028).ToString();
        private static readonly string ParagraphSeparator = ((char)0x2029).ToString();

        /// <summary>
        /// Serializes a frame to its wire form: compact JSON followed by a single <c>\n</c>. U+2028/U+2029 are written as escapes:
        /// they are legal inside JSON strings but some line splitters (JavaScript's) treat them as line breaks, which would cut a frame in two.
        /// </summary>
        public static byte[] Encode(JObject frame)
        {
            var json = frame.ToString(Formatting.None);
            if (json.IndexOf(LineSeparator, StringComparison.Ordinal) >= 0 || json.IndexOf(ParagraphSeparator, StringComparison.Ordinal) >= 0)
            {
                json = json.Replace(LineSeparator, "\\u2028").Replace(ParagraphSeparator, "\\u2029");
            }

            var byteCount = Utf8NoBom.GetByteCount(json);
            var bytes = new byte[byteCount + 1];
            Utf8NoBom.GetBytes(json, 0, json.Length, bytes, 0);
            bytes[byteCount] = (byte)'\n';
            return bytes;
        }

        /// <summary>Writes and flushes one frame; returns the number of bytes written.</summary>
        public async Task<int> WriteAsync(JObject frame, CancellationToken cancellationToken)
        {
            var bytes = Encode(frame);
            await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                if (_closed)
                {
                    throw new IOException(PompLanguage.Pick("연결의 입력이 닫혔어요.", "The connection input is closed."));
                }

                await _stream.WriteAsync(bytes, 0, bytes.Length, CancellationToken.None).ConfigureAwait(false);
                await _stream.FlushAsync(CancellationToken.None).ConfigureAwait(false);
                return bytes.Length;
            }
            finally
            {
                _gate.Release();
            }
        }

        /// <summary>
        /// Refuses every write that has not started yet (the process is being killed); a write already copying bytes finishes or fails
        /// on its own. Does not wait for the gate and does not close the stream.
        /// </summary>
        public void Abandon() => _closed = true;

        /// <summary>
        /// Closes the stream (omp sees EOF and shuts down). Waits up to <paramref name="timeout"/> for an in-flight
        /// write; returns false when the stream could not be closed in time (a blocked writer).
        /// </summary>
        public async Task<bool> CloseAsync(TimeSpan timeout)
        {
            if (_closed)
            {
                return true;
            }

            if (!await _gate.WaitAsync(timeout).ConfigureAwait(false))
            {
                return false;
            }

            try
            {
                _closed = true;
                try
                {
                    _stream.Dispose();
                }
                catch (Exception ex) when (ex is IOException || ex is ObjectDisposedException || ex is InvalidOperationException)
                {
                    // Already gone: the process exited.
                }

                return true;
            }
            finally
            {
                _gate.Release();
            }
        }
    }
}
