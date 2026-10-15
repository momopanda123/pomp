using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Linq;
using Newtonsoft.Json.Linq;

namespace Pomp.Tools
{
    /// <summary>
    /// Typed, forgiving reader for the arguments object of a host tool call. Problems are collected (not thrown) so the
    /// model gets every mistake in one error message. JSON <c>null</c> counts as "not provided" because strict-schema
    /// providers send omitted optional parameters as null. Unknown names are rejected except <c>_</c>-prefixed
    /// meta fields that omp may attach (for example an intent field).
    /// </summary>
    internal sealed class ToolArguments
    {
        private readonly JObject _arguments;
        private readonly List<string> _problems = new List<string>();

        public ToolArguments(JObject? arguments, params string[] knownNames)
        {
            _arguments = arguments ?? new JObject();

            List<string>? unknown = null;
            foreach (var property in _arguments.Properties())
            {
                if (property.Name.Length > 0 && property.Name[0] == '_')
                {
                    continue;
                }

                if (Array.IndexOf(knownNames, property.Name) < 0)
                {
                    (unknown ?? (unknown = new List<string>())).Add(property.Name);
                }
            }

            if (unknown != null)
            {
                var valid = knownNames.Length == 0 ? "(none)" : string.Join(", ", knownNames);
                _problems.Add("unknown argument(s) " + string.Join(", ", unknown.Select(u => "'" + u + "'")) + "; valid arguments: " + valid);
            }
        }

        public bool HasProblems => _problems.Count > 0;

        public void AddProblem(string problem) => _problems.Add(problem);

        /// <summary>When any problem was recorded, returns the single error result describing all of them.</summary>
        public bool TryGetError(string toolName, [NotNullWhen(true)] out HostToolResult? error)
        {
            if (_problems.Count == 0)
            {
                error = null;
                return false;
            }

            error = HostToolResult.Error(toolName + ": invalid arguments - " + string.Join("; ", _problems) + ".");
            return true;
        }

        /// <summary>Optional string; absent, null and blank values all read as null.</summary>
        public string? OptionalString(string name, bool trim = true)
        {
            var token = Get(name);
            if (token == null)
            {
                return null;
            }

            if (token.Type != JTokenType.String)
            {
                _problems.Add("'" + name + "' must be a string");
                return null;
            }

            var value = token.Value<string>() ?? string.Empty;
            if (trim)
            {
                value = value.Trim();
            }

            return value.Length == 0 ? null : value;
        }

        /// <summary>Required non-blank string; records a problem and returns an empty string when missing.</summary>
        public string RequiredString(string name, bool trim = true)
        {
            var token = Get(name);
            if (token == null)
            {
                _problems.Add("'" + name + "' is required");
                return string.Empty;
            }

            if (token.Type != JTokenType.String)
            {
                _problems.Add("'" + name + "' must be a string");
                return string.Empty;
            }

            var value = token.Value<string>() ?? string.Empty;
            if (trim)
            {
                value = value.Trim();
            }

            if (value.Length == 0)
            {
                _problems.Add("'" + name + "' must not be empty");
            }

            return value;
        }

        /// <summary>Optional integer in [min, max]. Integral floats and numeric strings are accepted.</summary>
        public int? OptionalInt(string name, int min, int max)
        {
            var token = Get(name);
            if (token == null)
            {
                return null;
            }

            if (!TryReadInteger(token, out var value))
            {
                _problems.Add("'" + name + "' must be " + IntegerDescription(min, max));
                return null;
            }

            if (value < min || value > max)
            {
                _problems.Add("'" + name + "' must be " + IntegerDescription(min, max) + " (got " + value.ToString(CultureInfo.InvariantCulture) + ")");
                return null;
            }

            return (int)value;
        }

        /// <summary>
        /// Optional list of non-blank strings (trimmed). A single string is accepted as a one-item list; absent, null and an
        /// empty array read as null.
        /// </summary>
        public IReadOnlyList<string>? OptionalStringArray(string name, int maxItems)
        {
            var token = Get(name);
            if (token == null)
            {
                return null;
            }

            var items = token.Type == JTokenType.Array ? ((JArray)token).ToList() : new List<JToken> { token };
            var values = new List<string>();
            foreach (var item in items)
            {
                if (item.Type != JTokenType.String)
                {
                    _problems.Add("'" + name + "' must be an array of strings");
                    return null;
                }

                var value = (item.Value<string>() ?? string.Empty).Trim();
                if (value.Length == 0)
                {
                    _problems.Add("'" + name + "' must not contain empty strings");
                    return null;
                }

                values.Add(value);
            }

            if (values.Count > maxItems)
            {
                _problems.Add("'" + name + "' may contain at most " + maxItems.ToString(CultureInfo.InvariantCulture) + " items");
                return null;
            }

            return values.Count == 0 ? null : values;
        }

        /// <summary>Optional boolean. The strings "true" and "false" are accepted too.</summary>
        public bool? OptionalBool(string name)
        {
            var token = Get(name);
            if (token == null)
            {
                return null;
            }

            if (token.Type == JTokenType.Boolean)
            {
                return token.Value<bool>();
            }

            if (token.Type == JTokenType.String)
            {
                var text = (token.Value<string>() ?? string.Empty).Trim();
                if (string.Equals(text, "true", StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }

                if (string.Equals(text, "false", StringComparison.OrdinalIgnoreCase))
                {
                    return false;
                }
            }

            _problems.Add("'" + name + "' must be a boolean");
            return null;
        }

        /// <summary>Optional enum value matched case-insensitively; returns the canonical spelling from <paramref name="allowed"/>.</summary>
        public string? OptionalEnum(string name, IReadOnlyList<string> allowed)
        {
            var token = Get(name);
            if (token == null)
            {
                return null;
            }

            return ReadEnum(name, token, allowed);
        }

        /// <summary>Required enum value; records a problem and returns null when missing or not allowed.</summary>
        public string? RequiredEnum(string name, IReadOnlyList<string> allowed)
        {
            var token = Get(name);
            if (token == null)
            {
                _problems.Add("'" + name + "' is required (one of: " + string.Join(", ", allowed) + ")");
                return null;
            }

            return ReadEnum(name, token, allowed);
        }

        /// <summary>Reads a string enum argument without recording problems (used by permission resolution).</summary>
        public static string? PeekEnum(JObject? arguments, string name, IReadOnlyList<string> allowed)
        {
            if (arguments == null || !arguments.TryGetValue(name, StringComparison.Ordinal, out var token) || token.Type != JTokenType.String)
            {
                return null;
            }

            var text = (token.Value<string>() ?? string.Empty).Trim();
            return allowed.FirstOrDefault(a => string.Equals(a, text, StringComparison.OrdinalIgnoreCase));
        }

        /// <summary>Reads a string argument without recording problems (used by permission descriptions).</summary>
        public static string? PeekString(JObject? arguments, string name)
        {
            if (arguments == null || !arguments.TryGetValue(name, StringComparison.Ordinal, out var token) || token.Type != JTokenType.String)
            {
                return null;
            }

            return token.Value<string>();
        }

        private string? ReadEnum(string name, JToken token, IReadOnlyList<string> allowed)
        {
            if (token.Type == JTokenType.String)
            {
                var text = (token.Value<string>() ?? string.Empty).Trim();
                var match = allowed.FirstOrDefault(a => string.Equals(a, text, StringComparison.OrdinalIgnoreCase));
                if (match != null)
                {
                    return match;
                }
            }

            _problems.Add("'" + name + "' must be one of: " + string.Join(", ", allowed));
            return null;
        }

        private JToken? Get(string name)
        {
            if (!_arguments.TryGetValue(name, StringComparison.Ordinal, out var token))
            {
                return null;
            }

            return token.Type == JTokenType.Null || token.Type == JTokenType.Undefined ? null : token;
        }

        private static bool TryReadInteger(JToken token, out long value)
        {
            value = 0;
            try
            {
                switch (token.Type)
                {
                    case JTokenType.Integer:
                        value = token.Value<long>();
                        return true;
                    case JTokenType.Float:
                        var number = token.Value<double>();
                        if (double.IsNaN(number) || double.IsInfinity(number) || number != Math.Floor(number) || Math.Abs(number) > long.MaxValue / 2)
                        {
                            return false;
                        }

                        value = (long)number;
                        return true;
                    case JTokenType.String:
                        return long.TryParse((token.Value<string>() ?? string.Empty).Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out value);
                    default:
                        return false;
                }
            }
            catch (OverflowException)
            {
                return false;
            }
            catch (FormatException)
            {
                return false;
            }
            catch (InvalidCastException)
            {
                return false;
            }
        }

        private static string IntegerDescription(int min, int max)
        {
            if (max == int.MaxValue)
            {
                return "an integer >= " + min.ToString(CultureInfo.InvariantCulture);
            }

            return "an integer between " + min.ToString(CultureInfo.InvariantCulture) + " and " + max.ToString(CultureInfo.InvariantCulture);
        }
    }
}
