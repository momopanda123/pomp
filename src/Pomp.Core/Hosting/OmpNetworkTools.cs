using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using Newtonsoft.Json.Linq;

namespace Pomp.Hosting
{
    /// <summary>
    /// omp's built-in tools that reach the network by design. The design lets the user control omp's network access (allowed by
    /// default, always logged): with <see cref="IPompSettings.BlockOmpNetworkTools"/> the host denies these tools in the config
    /// overlay and the pomp omp extension blocks the same calls, including <c>read</c> of an http(s) URL. This is not a sandbox:
    /// bash, eval, MCP servers and the model provider itself still use the network.
    /// </summary>
    internal static class OmpNetworkTools
    {
        /// <summary>Names denied in the overlay when blocked. Names a given omp version does not have are harmless there.</summary>
        public static readonly IReadOnlyList<string> Names = new[] { "web_search", "fetch", "web_fetch", "browser", "github" };

        private static readonly string[] TargetFields = { "url", "uri", "query", "q", "path", "repo", "repository" };

        /// <summary>What a tool call reaches on the network (a URL or search text), or null when it is not a network call.</summary>
        public static string? Describe(string toolName, JObject? args)
        {
            if (string.IsNullOrEmpty(toolName))
            {
                return null;
            }

            if (Names.Contains(toolName, StringComparer.Ordinal))
            {
                var target = TargetFields.Select(field => args?[field]).FirstOrDefault(value => value?.Type == JTokenType.String && ((string?)value)!.Length > 0);
                return target == null ? "(no target given)" : OneLine((string)target!);
            }

            if (string.Equals(toolName, "read", StringComparison.Ordinal) && args != null)
            {
                // The same tool reads files; only a URL argument is a network call.
                foreach (var property in args.Properties())
                {
                    if (property.Value.Type == JTokenType.String && IsWebUrl((string?)property.Value))
                    {
                        return OneLine((string)property.Value!);
                    }
                }
            }

            return null;
        }

        /// <summary>Model-chosen text goes to the log: one line, no control characters, bounded.</summary>
        private static string OneLine(string text)
        {
            const int MaxChars = 300;
            var line = new StringBuilder(Math.Min(text.Length, MaxChars) + 3);
            foreach (var c in text)
            {
                if (line.Length == MaxChars)
                {
                    line.Append("...");
                    break;
                }

                line.Append(char.IsControl(c) || char.GetUnicodeCategory(c) == System.Globalization.UnicodeCategory.LineSeparator || char.GetUnicodeCategory(c) == System.Globalization.UnicodeCategory.ParagraphSeparator ? ' ' : c);
            }

            return line.ToString();
        }

        internal static bool IsWebUrl(string? text) =>
            text != null
            && (text.StartsWith("http://", StringComparison.OrdinalIgnoreCase) || text.StartsWith("https://", StringComparison.OrdinalIgnoreCase));
    }
}
