using System;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace Pomp.Transport
{
    internal enum NdjsonLineKind
    {
        /// <summary>A complete, non-blank line (text without the terminator).</summary>
        Line,

        /// <summary>A line longer than the limit; its bytes were dropped.</summary>
        Oversized,

        /// <summary>A line that is not valid UTF-8; its bytes were dropped.</summary>
        InvalidUtf8,

        EndOfStream,
    }

    internal readonly struct NdjsonLine
    {
        public NdjsonLine(NdjsonLineKind kind, string text, long byteCount, bool truncated = false)
        {
            Kind = kind;
            Text = text;
            ByteCount = byteCount;
            Truncated = truncated;
        }

        public NdjsonLineKind Kind { get; }

        /// <summary>Line text (only for <see cref="NdjsonLineKind.Line"/>).</summary>
        public string Text { get; }

        /// <summary>Raw size of the line in bytes (for <see cref="NdjsonLineKind.Oversized"/> the full size that was dropped).</summary>
        public long ByteCount { get; }

        /// <summary>True when the line exceeded the limit and only its beginning is in <see cref="Text"/> (truncate mode).</summary>
        public bool Truncated { get; }
    }

    /// <summary>
    /// Splits a byte stream into lines on <c>\n</c> (a trailing <c>\r</c> is tolerated). Works on bytes, so UTF-8
    /// sequences split across reads are decoded correctly; blank lines are skipped; a line above the limit is dropped
    /// (or truncated) without buffering it, so a runaway producer cannot exhaust memory.
    /// </summary>
    internal sealed class NdjsonLineReader
    {
        public const int DefaultMaxLineBytes = 8 * 1024 * 1024;

        private readonly Stream _stream;
        private readonly byte[] _buffer;
        private readonly int _maxLineBytes;
        private readonly bool _truncateOversized;
        private readonly Encoding _encoding;
        private readonly MemoryStream _pending = new MemoryStream();
        private int _position;
        private int _length;
        private bool _endOfStream;
        private bool _atStart = true;
        private bool _overflowed;
        private long _overflowBytes;

        /// <param name="stream">Byte source.</param>
        /// <param name="maxLineBytes">Longest accepted line (excluding the terminator).</param>
        /// <param name="strictUtf8">Invalid UTF-8 rejects the line (protocol frames); otherwise it is replaced with U+FFFD (diagnostics).</param>
        /// <param name="truncateOversized">Keep the first <paramref name="maxLineBytes"/> of an oversized line instead of dropping it.</param>
        /// <param name="bufferSize">Read buffer size.</param>
        public NdjsonLineReader(Stream stream, int maxLineBytes = DefaultMaxLineBytes, bool strictUtf8 = true, bool truncateOversized = false, int bufferSize = 32 * 1024)
        {
            if (maxLineBytes < 1)
            {
                throw new ArgumentOutOfRangeException(nameof(maxLineBytes));
            }

            _stream = stream ?? throw new ArgumentNullException(nameof(stream));
            _maxLineBytes = maxLineBytes;
            _truncateOversized = truncateOversized;
            _encoding = new UTF8Encoding(false, strictUtf8);
            _buffer = new byte[Math.Max(16, bufferSize)];
        }

        /// <summary>Returns the next line, or a record with kind <see cref="NdjsonLineKind.EndOfStream"/> at EOF.</summary>
        public async Task<NdjsonLine> ReadLineAsync(CancellationToken cancellationToken)
        {
            while (true)
            {
                if (_position >= _length)
                {
                    if (_endOfStream)
                    {
                        return EndOfStream;
                    }

                    _length = await _stream.ReadAsync(_buffer, 0, _buffer.Length, cancellationToken).ConfigureAwait(false);
                    _position = 0;
                    if (_length == 0)
                    {
                        _endOfStream = true;
                        if (_overflowed || _pending.Length > 0)
                        {
                            // An unterminated last line (the producer died mid-write) is still delivered.
                            var partial = CompleteAccumulatedLine();
                            if (partial.HasValue)
                            {
                                return partial.Value;
                            }
                        }

                        return EndOfStream;
                    }
                }

                var newline = Array.IndexOf(_buffer, (byte)'\n', _position, _length - _position);
                var segmentEnd = newline < 0 ? _length : newline;
                var segmentLength = segmentEnd - _position;

                if (newline >= 0 && _pending.Length == 0 && !_overflowed && segmentLength <= _maxLineBytes)
                {
                    // Fast path: the whole line sits inside the read buffer.
                    var start = _position;
                    _position = newline + 1;
                    var line = Decode(_buffer, start, segmentLength, truncated: false, totalBytes: segmentLength);
                    if (line.HasValue)
                    {
                        return line.Value;
                    }

                    continue;
                }

                Accumulate(_buffer, _position, segmentLength);
                _position = newline < 0 ? _length : newline + 1;
                if (newline >= 0)
                {
                    var line = CompleteAccumulatedLine();
                    if (line.HasValue)
                    {
                        return line.Value;
                    }
                }
            }
        }

        private static NdjsonLine EndOfStream => new NdjsonLine(NdjsonLineKind.EndOfStream, string.Empty, 0);

        private void Accumulate(byte[] source, int offset, int count)
        {
            if (_overflowed)
            {
                _overflowBytes += count;
                return;
            }

            if (_pending.Length + count <= _maxLineBytes)
            {
                _pending.Write(source, offset, count);
                return;
            }

            _overflowed = true;
            if (_truncateOversized)
            {
                var room = _maxLineBytes - (int)_pending.Length;
                if (room > 0)
                {
                    _pending.Write(source, offset, room);
                }

                _overflowBytes = count - Math.Max(room, 0);
            }
            else
            {
                _overflowBytes = _pending.Length + count;
                _pending.SetLength(0);
            }
        }

        private NdjsonLine? CompleteAccumulatedLine()
        {
            try
            {
                if (_overflowed)
                {
                    if (!_truncateOversized)
                    {
                        return new NdjsonLine(NdjsonLineKind.Oversized, string.Empty, _overflowBytes);
                    }

                    var total = _pending.Length + _overflowBytes;
                    return Decode(_pending.GetBuffer(), 0, (int)_pending.Length, truncated: true, totalBytes: total);
                }

                return Decode(_pending.GetBuffer(), 0, (int)_pending.Length, truncated: false, totalBytes: _pending.Length);
            }
            finally
            {
                _pending.SetLength(0);
                if (_pending.Capacity > 1024 * 1024)
                {
                    _pending.Capacity = 64 * 1024;
                }

                _overflowed = false;
                _overflowBytes = 0;
            }
        }

        private NdjsonLine? Decode(byte[] source, int start, int count, bool truncated, long totalBytes)
        {
            while (count > 0 && source[start + count - 1] == (byte)'\r')
            {
                count--;
            }

            if (_atStart)
            {
                _atStart = false;
                if (count >= 3 && source[start] == 0xEF && source[start + 1] == 0xBB && source[start + 2] == 0xBF)
                {
                    start += 3;
                    count -= 3;
                }
            }

            if (IsBlank(source, start, count))
            {
                return null;
            }

            try
            {
                return new NdjsonLine(NdjsonLineKind.Line, _encoding.GetString(source, start, count), totalBytes, truncated);
            }
            catch (DecoderFallbackException)
            {
                return new NdjsonLine(NdjsonLineKind.InvalidUtf8, string.Empty, totalBytes);
            }
        }

        private static bool IsBlank(byte[] source, int start, int count)
        {
            for (var i = 0; i < count; i++)
            {
                var b = source[start + i];
                if (b != (byte)' ' && b != (byte)'\t' && b != (byte)'\r')
                {
                    return false;
                }
            }

            return true;
        }
    }
}
