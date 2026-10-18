using System.Text;
using Newtonsoft.Json.Linq;

namespace Pomp.Internal
{
    /// <summary>Forgiving accessors for omp frames (the wire shapes vary between omp versions; a missing or mistyped field is simply absent).</summary>
    internal static class JsonExtensions
    {
        public static string? GetString(this JToken? token, string name)
        {
            var value = token?[name];
            return value != null && value.Type == JTokenType.String ? (string?)value : null;
        }

        public static JObject? GetObject(this JToken? token, string name) => token?[name] as JObject;

        public static JArray? GetArray(this JToken? token, string name) => token?[name] as JArray;

        public static bool? GetBool(this JToken? token, string name)
        {
            var value = token?[name];
            return value != null && value.Type == JTokenType.Boolean ? (bool?)value : null;
        }

        public static long? GetLong(this JToken? token, string name)
        {
            var value = token?[name];
            if (value == null)
            {
                return null;
            }

            switch (value.Type)
            {
                case JTokenType.Integer:
                    return (long)value;
                case JTokenType.Float:
                    var number = (double)value;
                    return double.IsNaN(number) || double.IsInfinity(number) ? (long?)null : (long)number;
                default:
                    return null;
            }
        }

        public static double? GetDouble(this JToken? token, string name)
        {
            var value = token?[name];
            return value != null && (value.Type == JTokenType.Integer || value.Type == JTokenType.Float) ? (double?)value : null;
        }

        /// <summary>
        /// The answer text of one message content block: a <c>text</c> block's text, or a <c>thinking</c> block omp marks with
        /// <c>narration: true</c> (omp 18.12+: prose the model addressed to the user that the provider returned as thinking, which
        /// the RPC contract says to present as answer text). Null for every other block.
        /// </summary>
        public static string? AnswerText(this JToken? block)
        {
            if (!(block is JObject value)) return null;
            switch (value.GetString("type"))
            {
                case "text":
                    return value.GetString("text");
                case "thinking":
                    return value.IsNarration() ? value.GetString("thinking") ?? value.GetString("text") : null;
                default:
                    return null;
            }
        }

        /// <summary>True for a <c>thinking</c> block omp marks as narration (see <see cref="AnswerText"/>).</summary>
        public static bool IsNarration(this JToken? block) => block is JObject value && value.GetString("type") == "thinking" && value.GetBool("narration") == true;

        /// <summary>
        /// Concatenates the text of an omp <c>AgentToolResult</c> (<c>{ content: [{ type: "text", text }...] }</c>); a plain string
        /// is returned as is. Returns an empty string when there is no text.
        /// </summary>
        public static string ExtractText(JToken? result)
        {
            if (result == null || result.Type == JTokenType.Null)
            {
                return string.Empty;
            }

            if (result.Type == JTokenType.String)
            {
                return (string?)result ?? string.Empty;
            }

            var text = new StringBuilder();
            if (result.GetArray("content") is JArray content)
            {
                foreach (var block in content)
                {
                    if (block.Type == JTokenType.String)
                    {
                        Append(text, (string?)block);
                    }
                    else if (block.GetString("type") == "text")
                    {
                        Append(text, block.GetString("text"));
                    }
                }
            }

            return text.ToString();
        }

        /// <summary>Deep copy of an object in which every string longer than <paramref name="maxStringLength"/> is shortened (tool arguments can carry whole files).</summary>
        public static JObject Compact(JObject source, int maxStringLength)
        {
            return (JObject)CompactToken(source, maxStringLength, 0);
        }

        private static JToken CompactToken(JToken token, int maxStringLength, int depth)
        {
            switch (token.Type)
            {
                case JTokenType.Object:
                    var copy = new JObject();
                    foreach (var property in ((JObject)token).Properties())
                    {
                        copy[property.Name] = depth >= 16 ? JValue.CreateString("[too deep]") : CompactToken(property.Value, maxStringLength, depth + 1);
                    }

                    return copy;
                case JTokenType.Array:
                    var array = new JArray();
                    foreach (var item in (JArray)token)
                    {
                        array.Add(depth >= 16 ? JValue.CreateString("[too deep]") : CompactToken(item, maxStringLength, depth + 1));
                    }

                    return array;
                case JTokenType.String:
                    var text = (string?)token ?? string.Empty;
                    return text.Length <= maxStringLength
                        ? new JValue(text)
                        : new JValue(text.Substring(0, maxStringLength) + " [+" + (text.Length - maxStringLength) + " chars]");
                default:
                    return token.DeepClone();
            }
        }

        private static void Append(StringBuilder builder, string? text)
        {
            if (string.IsNullOrEmpty(text))
            {
                return;
            }

            if (builder.Length > 0)
            {
                builder.Append('\n');
            }

            builder.Append(text);
        }
    }

    internal static class TextUtil
    {
        /// <summary>Shortens <paramref name="text"/> to at most <paramref name="maxChars"/> characters plus a marker telling how much was cut.</summary>
        public static string Truncate(string? text, int maxChars)
        {
            if (string.IsNullOrEmpty(text) || text!.Length <= maxChars)
            {
                return text ?? string.Empty;
            }

            var cut = text.Length - maxChars;
            // Do not split a surrogate pair.
            var length = maxChars;
            if (length > 0 && char.IsHighSurrogate(text[length - 1]))
            {
                length--;
            }

            return text.Substring(0, length) + "\n...[" + cut + " more characters]";
        }
    }
}
