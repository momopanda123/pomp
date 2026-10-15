using Newtonsoft.Json;
using Newtonsoft.Json.Converters;
using Newtonsoft.Json.Linq;
using Newtonsoft.Json.Serialization;

namespace Pomp.Json
{
    /// <summary>
    /// JSON conventions shared by the omp wire protocol and the WebView UI protocol:
    /// camelCase names, enums as camelCase strings, nulls omitted, no indentation (NDJSON safe).
    /// </summary>
    public static class PompJson
    {
        public static readonly JsonSerializerSettings Settings = CreateSettings();

        // JsonSerializer is mutable and is not safe to share between the RPC, tool and UI threads.
        public static JsonSerializer Serializer => JsonSerializer.Create(Settings);

        public static string Serialize(object value) => JsonConvert.SerializeObject(value, Formatting.None, Settings);

        public static T? Deserialize<T>(string json) => JsonConvert.DeserializeObject<T>(json, Settings);

        public static JObject ToJObject(object value) => JObject.FromObject(value, Serializer);

        public static T? ToObject<T>(JToken? token) => token == null || token.Type == JTokenType.Null ? default : token.ToObject<T>(Serializer);

        /// <summary>
        /// Parses one JSON object (an NDJSON line, a WebView message) with the depth limit and date handling
        /// used everywhere in pomp. Throws <see cref="JsonException"/> on malformed input or a non-object root.
        /// </summary>
        public static JObject ParseObject(string json)
        {
            using (var reader = new JsonTextReader(new System.IO.StringReader(json)))
            {
                reader.MaxDepth = MaxDepth;
                reader.DateParseHandling = DateParseHandling.None;
                var token = JToken.ReadFrom(reader);
                while (reader.Read())
                {
                    if (reader.TokenType != JsonToken.Comment)
                    {
                        throw new JsonReaderException("Unexpected content after the JSON object.");
                    }
                }

                return token as JObject ?? throw new JsonReaderException("Expected a JSON object.");
            }
        }

        /// <summary>Maximum nesting depth accepted when parsing (guards against stack exhaustion).</summary>
        public const int MaxDepth = 128;

        private static JsonSerializerSettings CreateSettings()
        {
            var settings = new JsonSerializerSettings
            {
                ContractResolver = new CamelCasePropertyNamesContractResolver(),
                NullValueHandling = NullValueHandling.Ignore,
                Formatting = Formatting.None,
                MaxDepth = MaxDepth,
                // omp payloads may contain ISO timestamps; keep them as strings.
                DateParseHandling = DateParseHandling.None,
            };
            settings.Converters.Add(new StringEnumConverter(new CamelCaseNamingStrategy()));
            return settings;
        }
    }
}
