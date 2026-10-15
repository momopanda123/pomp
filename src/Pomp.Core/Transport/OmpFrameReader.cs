using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Pomp.Json;

namespace Pomp.Transport
{
    /// <summary>
    /// Turns omp's stdout into logical frames: NDJSON line splitting, JSON parsing and (after protocol v2 was negotiated)
    /// <c>rpc_chunk</c> reassembly. Nothing here is fatal: a malformed line, an oversized line or a broken chunk sequence
    /// is reported through the callback, skipped, and reading continues with the next line.
    /// </summary>
    internal sealed class OmpFrameReader
    {
        private readonly NdjsonLineReader _lines;
        private readonly Action<string>? _onProblem;
        private RpcChunkReassembler _reassembler = new RpcChunkReassembler();
        private bool _protocolV2;

        public OmpFrameReader(Stream stream, Action<string>? onProblem = null, int maxLineBytes = NdjsonLineReader.DefaultMaxLineBytes)
        {
            _lines = new NdjsonLineReader(stream, maxLineBytes, strictUtf8: true, truncateOversized: false);
            _onProblem = onProblem;
        }

        /// <summary>Number of lines/sequences skipped so far.</summary>
        public int SkippedCount { get; private set; }

        public bool IsProtocolV2 => _protocolV2;

        /// <summary>Switches on chunk reassembly using the limits advertised in the <c>ready</c> frame. Call it right after the negotiation response was read.</summary>
        public void EnableProtocolV2(long? maxFrameBytes, long? maxReassembledBytes)
        {
            _reassembler = new RpcChunkReassembler(maxFrameBytes, maxReassembledBytes);
            _protocolV2 = true;
        }

        /// <summary>Returns the next logical frame, or null when the stream ended.</summary>
        public async Task<JObject?> ReadFrameAsync(CancellationToken cancellationToken)
        {
            while (true)
            {
                var line = await _lines.ReadLineAsync(cancellationToken).ConfigureAwait(false);
                switch (line.Kind)
                {
                    case NdjsonLineKind.EndOfStream:
                        if (_reassembler.HasPending)
                        {
                            _reassembler.Reset();
                            Skip("stdout ended inside an rpc_chunk sequence; the partial frame was dropped");
                        }

                        return null;
                    case NdjsonLineKind.Oversized:
                        Skip("dropped an oversized stdout line (" + line.ByteCount + " bytes)");
                        continue;
                    case NdjsonLineKind.InvalidUtf8:
                        Skip("dropped a stdout line that is not valid UTF-8 (" + line.ByteCount + " bytes)");
                        continue;
                }

                JObject obj;
                try
                {
                    obj = PompJson.ParseObject(line.Text);
                }
                catch (JsonException ex)
                {
                    Skip("skipped a malformed stdout line (" + line.ByteCount + " bytes): " + ex.Message + " | " + Preview(line.Text));
                    continue;
                }

                if (RpcChunkReassembler.IsChunk(obj) && !_protocolV2)
                {
                    Skip("dropped an rpc_chunk frame received before protocol v2 was negotiated");
                    continue;
                }

                try
                {
                    var logical = _reassembler.Push(obj);
                    if (logical != null)
                    {
                        return logical;
                    }
                }
                catch (RpcProtocolException ex)
                {
                    // A non-chunk frame that interrupted a sequence is itself valid: deliver it after dropping the partial sequence.
                    var interrupting = !RpcChunkReassembler.IsChunk(obj);
                    _reassembler.Reset();
                    Skip("rpc chunk protocol violation: " + ex.Message);
                    if (interrupting)
                    {
                        return obj;
                    }
                }
                catch (JsonException ex)
                {
                    Skip("skipped a reassembled frame that is not valid JSON: " + ex.Message);
                }
            }
        }

        private void Skip(string message)
        {
            SkippedCount++;
            try
            {
                _onProblem?.Invoke(message);
            }
            catch
            {
                // Reporting must never break reading.
            }
        }

        private static string Preview(string text) => text.Length <= 120 ? text : text.Substring(0, 120) + "...";
    }
}
