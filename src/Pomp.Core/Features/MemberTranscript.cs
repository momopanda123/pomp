using System;
using System.Linq;
using System.Text.RegularExpressions;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Pomp.Internal;

namespace Pomp.Features
{
    internal static class MemberTranscript
    {
        private static readonly Regex Reminders = new Regex(@"<system-reminder\b[^>]*>.*?(?:</system-reminder\s*>|\z)", RegexOptions.Singleline | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        private static string? String(JToken? token) => token?.Type == JTokenType.String ? (string?)token : null;
        internal static string Clean(string text) => Reminders.Replace(text, string.Empty).Trim();
        internal static string Text(JObject message)
        {
            var content = message["content"] as JArray;
            var text = content == null ? (message["content"]?.Type == JTokenType.String ? (string?)message["content"] : String(message["text"])) ?? ""
                : string.Join("\n", content.OfType<JObject>().Select(b => b.AnswerText()).Where(t => t != null));
            text = Clean(text);
            if (string.IsNullOrWhiteSpace(text) && String(message["role"]) == "assistant")
            {
                var submitted = content?.OfType<JObject>().LastOrDefault(b => String(b["type"]) == "toolCall" && String(b["name"]) == "yield" && b["arguments"] is JObject args && args["type"]?.Type != JTokenType.Array);
                text = Data((submitted?["arguments"] as JObject)?["data"]) ?? "";
            }
            if (String(message["role"]) is "toolResult" or "tool" && String(message["toolName"]) == "yield" && (message["details"] as JObject)?["type"]?.Type != JTokenType.Array)
                text = Data((message["details"] as JObject)?["data"]) ?? text;
            return Clean(text);
        }
        private static string? Data(JToken? value) => value == null || value.Type == JTokenType.Null ? null : value.Type == JTokenType.String ? (string?)value : value.ToString(Formatting.Indented);
        internal static (string? result, long? tokens) Final(JArray messages)
        {
            string? result = null; long? tokens = null;
            foreach (var message in messages.OfType<JObject>())
            {
                var role = String(message["role"]);
                if (role == "assistant")
                {
                    var text = Text(message); if (!string.IsNullOrWhiteSpace(text)) result = text;
                    var usage = message["usage"] as JObject;
                    if (usage?["totalTokens"]?.Type == JTokenType.Integer && long.TryParse(usage["totalTokens"]!.ToString(), out var total) && total >= 0)
                        tokens = (tokens ?? 0) > long.MaxValue - total ? long.MaxValue : (tokens ?? 0) + total;
                }
                else if (role is "toolResult" or "tool" && String(message["toolName"]) == "yield" && (message["details"] as JObject)?["type"]?.Type != JTokenType.Array)
                {
                    var data = Data((message["details"] as JObject)?["data"]);
                    if (!string.IsNullOrWhiteSpace(data)) result = Clean(data!);
                }
            }
            return (result == null ? null : result.Substring(0, Math.Min(8000, result.Length)), tokens);
        }
    }
}
