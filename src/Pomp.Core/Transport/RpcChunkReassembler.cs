using System;
using System.Collections.Generic;
using System.Text;
using Newtonsoft.Json.Linq;
using Pomp.Json;

namespace Pomp.Transport
{
    /// <summary>The peer violated the RPC framing rules (chunk metadata, ordering, limits...).</summary>
    internal sealed class RpcProtocolException : Exception
    {
        public RpcProtocolException(string message)
            : base(message)
        {
        }
    }

    /// <summary>
    /// Reassembles protocol v2 <c>rpc_chunk</c> sequences. A port of omp's <c>RpcFrameDecoder</c>
    /// (packages/coding-agent/src/modes/rpc/rpc-frame.ts) with the same validation rules: chunk metadata
    /// (chunkId/index/count/byteLength), strict base64, no interleaving, declared/received length agreement,
    /// the advertised reassembly ceiling and strict UTF-8. Violations throw <see cref="RpcProtocolException"/>;
    /// the caller decides whether to reset and continue.
    /// </summary>
    internal sealed class RpcChunkReassembler
    {
        /// <summary>Maximum raw bytes of one chunk (omp's RPC_CHUNK_PAYLOAD_BYTES).</summary>
        public const int MaxChunkPayloadBytes = 256 * 1024;

        public const long DefaultMaxFrameBytes = 1024 * 1024;

        public const long DefaultMaxReassembledBytes = 64L * 1024 * 1024;

        /// <summary>Upper bound applied to an advertised reassembly ceiling (guards against absurd values).</summary>
        public const long AbsoluteMaxReassembledBytes = 512L * 1024 * 1024;

        private const long MaxSafeInteger = 9007199254740991;
        private const int MaxChunkIdLength = 128;

        private static readonly UTF8Encoding StrictUtf8 = new UTF8Encoding(false, true);

        private readonly long _minByteLength;
        private readonly long _maxByteLength;
        private readonly int _maxCount;
        private Pending? _pending;

        /// <param name="maxFrameBytes">The physical frame limit from the ready frame: only logical frames of at least this size are chunked.</param>
        /// <param name="maxReassembledBytes">The reassembly ceiling from the ready frame.</param>
        public RpcChunkReassembler(long? maxFrameBytes = null, long? maxReassembledBytes = null)
        {
            _minByteLength = maxFrameBytes is long f && f > 0 ? f : DefaultMaxFrameBytes;
            var ceiling = maxReassembledBytes is long r && r > 0 ? r : DefaultMaxReassembledBytes;
            _maxByteLength = Math.Min(ceiling, AbsoluteMaxReassembledBytes);
            _maxCount = (int)Math.Min(int.MaxValue, (_maxByteLength + MaxChunkPayloadBytes - 1) / MaxChunkPayloadBytes);
        }

        /// <summary>True while a chunk sequence is in progress.</summary>
        public bool HasPending => _pending != null;

        /// <summary>Drops a partially received sequence.</summary>
        public void Reset() => _pending = null;

        /// <summary>
        /// Feeds one parsed physical frame. Returns the frame itself when it is not a chunk, the reassembled logical
        /// frame when a sequence completes, or null while more chunks are needed.
        /// </summary>
        public JObject? Push(JObject frame)
        {
            if (!IsChunk(frame))
            {
                if (_pending != null)
                {
                    throw new RpcProtocolException("rpc chunk sequence interrupted");
                }

                return frame;
            }

            var chunkId = frame["chunkId"];
            if (chunkId == null
                || chunkId.Type != JTokenType.String
                || !TryGetSafeInteger(frame["index"], out var index)
                || !TryGetSafeInteger(frame["count"], out var count)
                || !TryGetSafeInteger(frame["byteLength"], out var byteLength))
            {
                throw new RpcProtocolException("invalid rpc chunk metadata");
            }

            var id = (string?)chunkId ?? string.Empty;
            if (id.Length == 0
                || id.Length > MaxChunkIdLength
                || index < 0
                || count < 2
                || count > _maxCount
                || index >= count
                || byteLength < _minByteLength
                || byteLength > _maxByteLength)
            {
                throw new RpcProtocolException("invalid rpc chunk metadata");
            }

            var bytes = DecodeBase64Strict(frame["data"]);
            if (bytes.Length > MaxChunkPayloadBytes)
            {
                throw new RpcProtocolException("rpc chunk payload exceeds the transport limit");
            }

            if (_pending == null)
            {
                if (index != 0)
                {
                    throw new RpcProtocolException("rpc chunk sequence must start at index 0");
                }

                _pending = new Pending(id, (int)count, byteLength);
            }

            var pending = _pending;
            if (pending.ChunkId != id || pending.Count != count || pending.ByteLength != byteLength || pending.NextIndex != index)
            {
                throw new RpcProtocolException("rpc chunk sequence mismatch");
            }

            pending.Chunks.Add(bytes);
            pending.ReceivedBytes += bytes.Length;
            pending.NextIndex++;
            if (pending.ReceivedBytes > pending.ByteLength)
            {
                throw new RpcProtocolException("rpc chunk sequence exceeds declared length");
            }

            if (pending.NextIndex < pending.Count)
            {
                return null;
            }

            if (pending.ReceivedBytes != pending.ByteLength)
            {
                throw new RpcProtocolException("rpc chunk sequence length mismatch");
            }

            _pending = null;
            var all = new byte[pending.ByteLength];
            var offset = 0;
            foreach (var chunk in pending.Chunks)
            {
                Buffer.BlockCopy(chunk, 0, all, offset, chunk.Length);
                offset += chunk.Length;
            }

            string decoded;
            try
            {
                decoded = StrictUtf8.GetString(all);
            }
            catch (DecoderFallbackException)
            {
                throw new RpcProtocolException("rpc frame is not valid UTF-8");
            }

            // A JSON syntax error surfaces as a JsonException: the sequence itself was consistent.
            return PompJson.ParseObject(decoded);
        }

        /// <summary>True when the frame is an <c>rpc_chunk</c>.</summary>
        public static bool IsChunk(JObject frame) => frame["type"] is JValue type && type.Type == JTokenType.String && (string?)type == "rpc_chunk";

        private static bool TryGetSafeInteger(JToken? token, out long value)
        {
            value = 0;
            if (token == null)
            {
                return false;
            }

            switch (token.Type)
            {
                case JTokenType.Integer:
                    try
                    {
                        value = token.Value<long>();
                    }
                    catch (OverflowException)
                    {
                        return false;
                    }

                    return value >= -MaxSafeInteger && value <= MaxSafeInteger;
                case JTokenType.Float:
                    var number = token.Value<double>();
                    if (double.IsNaN(number) || double.IsInfinity(number) || Math.Floor(number) != number || Math.Abs(number) > MaxSafeInteger)
                    {
                        return false;
                    }

                    value = (long)number;
                    return true;
                default:
                    return false;
            }
        }

        private static byte[] DecodeBase64Strict(JToken? token)
        {
            if (token == null || token.Type != JTokenType.String)
            {
                throw new RpcProtocolException("invalid rpc chunk data");
            }

            var data = (string?)token ?? string.Empty;
            if (data.Length == 0 || data.Length % 4 != 0)
            {
                throw new RpcProtocolException("invalid rpc chunk data");
            }

            var padding = 0;
            if (data[data.Length - 1] == '=')
            {
                padding = data[data.Length - 2] == '=' ? 2 : 1;
            }

            var bodyLength = data.Length - padding;
            for (var i = 0; i < bodyLength; i++)
            {
                if (Base64Value(data[i]) < 0)
                {
                    throw new RpcProtocolException("invalid rpc chunk data");
                }
            }

            // Canonical encoding: the unused low bits of the last data character must be zero
            // (the reference decoder re-encodes and compares).
            if (padding > 0)
            {
                var last = Base64Value(data[bodyLength - 1]);
                var unusedMask = padding == 2 ? 0x0F : 0x03;
                if ((last & unusedMask) != 0)
                {
                    throw new RpcProtocolException("invalid rpc chunk data");
                }
            }

            try
            {
                return Convert.FromBase64String(data);
            }
            catch (FormatException)
            {
                throw new RpcProtocolException("invalid rpc chunk data");
            }
        }

        private static int Base64Value(char c)
        {
            if (c >= 'A' && c <= 'Z')
            {
                return c - 'A';
            }

            if (c >= 'a' && c <= 'z')
            {
                return c - 'a' + 26;
            }

            if (c >= '0' && c <= '9')
            {
                return c - '0' + 52;
            }

            if (c == '+')
            {
                return 62;
            }

            return c == '/' ? 63 : -1;
        }

        private sealed class Pending
        {
            public Pending(string chunkId, int count, long byteLength)
            {
                ChunkId = chunkId;
                Count = count;
                ByteLength = byteLength;
            }

            public string ChunkId { get; }

            public int Count { get; }

            public long ByteLength { get; }

            public int NextIndex { get; set; }

            public long ReceivedBytes { get; set; }

            public List<byte[]> Chunks { get; } = new List<byte[]>();
        }
    }
}
