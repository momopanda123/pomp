using System.Collections.Generic;
using Newtonsoft.Json.Linq;

namespace Pomp.Tools
{
    /// <summary>Builds the small JSON Schema objects used as host tool <c>parameters</c> (always closed objects).</summary>
    internal sealed class SchemaBuilder
    {
        private readonly JObject _properties = new JObject();
        private readonly List<string> _required = new List<string>();

        public SchemaBuilder String(string name, string description, bool required = false, int? minLength = null)
        {
            var schema = new JObject { ["type"] = "string", ["description"] = description };
            if (minLength.HasValue)
            {
                schema["minLength"] = minLength.Value;
            }

            return Add(name, schema, required);
        }

        public SchemaBuilder Integer(string name, string description, int minimum, int? maximum = null, int? defaultValue = null, bool required = false)
        {
            var schema = new JObject { ["type"] = "integer", ["description"] = description, ["minimum"] = minimum };
            if (maximum.HasValue)
            {
                schema["maximum"] = maximum.Value;
            }

            if (defaultValue.HasValue)
            {
                schema["default"] = defaultValue.Value;
            }

            return Add(name, schema, required);
        }

        public SchemaBuilder Boolean(string name, string description, bool defaultValue)
        {
            var schema = new JObject { ["type"] = "boolean", ["description"] = description, ["default"] = defaultValue };
            return Add(name, schema, false);
        }

        public SchemaBuilder StringArray(string name, string description, int maxItems, bool required = false)
        {
            var schema = new JObject
            {
                ["type"] = "array",
                ["description"] = description,
                ["items"] = new JObject { ["type"] = "string" },
                ["maxItems"] = maxItems,
            };
            return Add(name, schema, required);
        }

        public SchemaBuilder Enum(string name, string description, IEnumerable<string> values, string? defaultValue = null, bool required = false)
        {
            var schema = new JObject { ["type"] = "string", ["description"] = description, ["enum"] = new JArray(values) };
            if (defaultValue != null)
            {
                schema["default"] = defaultValue;
            }

            return Add(name, schema, required);
        }

        public JObject Build()
        {
            var schema = new JObject
            {
                ["type"] = "object",
                ["properties"] = _properties.DeepClone(),
            };

            if (_required.Count > 0)
            {
                var required = new JArray();
                foreach (var name in _required)
                {
                    required.Add(name);
                }

                schema["required"] = required;
            }

            schema["additionalProperties"] = false;
            return schema;
        }

        private SchemaBuilder Add(string name, JObject schema, bool required)
        {
            _properties[name] = schema;
            if (required)
            {
                _required.Add(name);
            }

            return this;
        }
    }
}
