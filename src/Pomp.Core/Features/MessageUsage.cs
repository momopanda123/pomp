using System;
using Newtonsoft.Json.Linq;
namespace Pomp.Features
{
    /// <summary>
    /// The token count of one model call, defined exactly as the engine defines the tokens of an agent run (the "tokens" the Agents tab
    /// shows for a member): input + output + cache writes. Cache reads are left out on purpose: with prompt caching every call reads the
    /// whole cached context again, so summing them says nothing about work done. When a provider reports only a total (none of the three),
    /// that total is used.
    /// </summary>
    internal static class MessageUsage
    {
        private const double Limit = 1e12;
        private static double Field(JObject usage, params string[] names)
        {
            foreach (var name in names)
            {
                var value = usage[name];
                if (value == null || (value.Type != JTokenType.Integer && value.Type != JTokenType.Float)) continue;
                var number = (double)value;
                if (double.IsNaN(number) || double.IsInfinity(number) || number <= 0) return 0;
                return Math.Min(number, Limit);
            }
            return 0;
        }
        /// <summary>The tokens of the <c>usage</c> object of an assistant message; 0 when it is missing or has no usable numbers.</summary>
        public static long Tokens(JToken? usage)
        {
            if (usage is not JObject fields) return 0;
            var counted = Field(fields, "input", "input_tokens", "inputTokens") + Field(fields, "output", "output_tokens", "outputTokens") + Field(fields, "cacheWrite", "cache_write", "cacheWriteTokens");
            if (counted <= 0) counted = Field(fields, "totalTokens", "total_tokens");
            return (long)Math.Min(counted, Limit);
        }
    }
}
