using System;
using Newtonsoft.Json.Linq;
namespace Pomp.Features
{
    /// <summary>Conservative tool-body allowance from an engine-reported context estimate, not billing accuracy.</summary>
    internal sealed class ContextBudget
    {
        private ContextBudget(long tokens, long window, bool autoCompaction) { Tokens = tokens; Window = window; AutoCompaction = autoCompaction; }
        public long Tokens { get; }
        public long Window { get; }
        public bool AutoCompaction { get; }
        public long Reserve => Math.Min(64000, Math.Max(4096, Window / 5));
        public long Remaining => Math.Max(0, Window - Tokens - Reserve);
        public static ContextBudget? FromState(JObject state, string expectedSession)
        {
            if (string.IsNullOrEmpty(expectedSession) || (string?)state["sessionId"] != expectedSession || !(state["contextUsage"] is JObject usage) || usage["tokens"]?.Type != JTokenType.Integer || usage["contextWindow"]?.Type != JTokenType.Integer) return null;
            var tokens = (long)usage["tokens"]!; var window = (long)usage["contextWindow"]!;
            if (tokens < 0 || tokens > 1000000000 || window < 4096 || window > 1000000000) return null;
            return new ContextBudget(tokens, window, state["autoCompactionEnabled"]?.Type == JTokenType.Boolean && (bool)state["autoCompactionEnabled"]!);
        }
        public int LimitCharacters(int requested)
        {
            if (requested < 4096 || requested > 128000) throw new ArgumentOutOfRangeException(nameof(requested));
            // Four bytes per UTF-16 character over-budgets serialized text, while reserving substantial reply/envelope room.
            var available = Remaining / 4;
            if (available < 4096) throw new InvalidOperationException("Too little estimated model context remains for a source outline. Compact the conversation or choose a larger authenticated model, then retry. POMP has not sent or discarded source content.");
            return (int)Math.Min(requested, available);
        }
        public JObject Describe(int requested, int allowed) => new JObject {
            ["mode"] = "estimate-with-conservative-text-allowance", ["observedTokens"] = Tokens, ["contextWindow"] = Window,
            ["reservedTokens"] = Reserve, ["remainingTokens"] = Remaining, ["requestedCharacters"] = requested, ["allowedCharacters"] = allowed,
            ["autoCompactionEnabled"] = AutoCompaction,
            ["limitations"] = "A point-in-time estimate, not exact full provider-request/billing tokens. Tool/body compression preserves omitted counts. Provider envelopes, subsequent tool results and concurrently changing context can differ. Existing auto-compaction settings are observed, never changed by this tool." };
    }
}
