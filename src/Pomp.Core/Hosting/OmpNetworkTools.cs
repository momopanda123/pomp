using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using Newtonsoft.Json.Linq;

namespace Pomp.Hosting
{
    /// <summary>
    /// omp's built-in tools that reach the network by design. The design lets the user control omp's network access (allowed by
    /// default, always logged): with <see cref="IPompSettings.BlockOmpNetworkTools"/> the host denies these tools in the config
    /// overlay and the pomp omp extension blocks the same calls, including <c>read</c> of a web address or a remote internal URL. This is not a sandbox:
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
                // The same tool reads files; only a web address or a remote internal URL is a network call.
                foreach (var property in args.Properties())
                {
                    if (property.Value.Type == JTokenType.String && (IsWebUrl((string?)property.Value) || IsRemoteReadUrl((string?)property.Value)))
                    {
                        return OneLine(((string)property.Value!).Trim());
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

        // The same two rules as the engine guard (Pomp.OmpExtension lib/tool-targets.ts): what omp's read fetches as a web page
        // (http(s)://, the single-slash spelling it repairs, a bare www. host), and its internal URLs answered by another machine.
        private static readonly Regex WebUrl = new Regex(@"\A(?:https?://?|www\.)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        private static readonly Regex RemoteReadUrl = new Regex(@"\A(?:issue|pr|mr|ssh)://", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

        internal static bool IsWebUrl(string? text) => text != null && WebUrl.IsMatch(text.Trim());

        internal static bool IsRemoteReadUrl(string? text) => text != null && RemoteReadUrl.IsMatch(text.Trim());
    }
}
