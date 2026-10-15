using System;
using System.Globalization;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;

namespace Pomp.Tools
{
    /// <summary>Read-only, bounded tail of a built-in or custom Output pane.</summary>
    internal sealed class ReadOutputWindowTool : HostToolBase
    {
        internal const int MaxOutputCharacters = 60000;

        public ReadOutputWindowTool(HostToolContext context)
            : base(context, CreateDefinition())
        {
        }

        private static HostToolDefinition CreateDefinition()
        {
            return new HostToolDefinition
            {
                Name = HostToolNames.ReadOutputWindow,
                Label = "Read Output Window",
                Description =
                    "Read the last lines of a Visual Studio Output window pane to summarize build/debug logs. Read-only; does not build, debug, clear or activate panes. " +
                    "Built-in English pane names Build, Debug and General work in localized Visual Studio; custom panes use their displayed name. " +
                    "Default: Build, last 200 lines. Output is also bounded to the last 60000 characters.",
                Parameters = new SchemaBuilder()
                    .String("pane", "Pane name, e.g. Build, Debug, General, Tests, Source Control - Git (default: Build).")
                    .Integer("maxLines", "Maximum number of lines from the end of the pane.", 1, 2000, 200)
                    .Build(),
                LoadMode = EssentialLoadMode,
            };
        }

        protected override async Task<HostToolResult> ExecuteCoreAsync(HostToolInvocation invocation, CancellationToken cancellationToken)
        {
            var args = new ToolArguments(invocation.Arguments, "pane", "maxLines");
            var pane = args.OptionalString("pane") ?? "Build";
            var maxLines = args.OptionalInt("maxLines", 1, 2000) ?? 200;
            if (pane.Length > 256 || pane.IndexOfAny(new[] { '\r', '\n', '\0' }) >= 0)
            {
                args.AddProblem("'pane' must be a single-line pane name of at most 256 characters, with no NUL");
            }

            if (args.TryGetError(Name, out var invalid))
            {
                return invalid;
            }

            var text = await Adapters.Output.ReadPaneAsync(pane, maxLines, cancellationToken).ConfigureAwait(false);
            if (text == null)
            {
                return HostToolResult.Text("Output pane '" + pane + "' does not exist. Typical pane names: Build, Debug, General, Tests, Source Control - Git, Pomp. "
                    + "A pane may appear only after its feature has produced output; custom panes use their displayed name.",
                    new JObject { ["pane"] = pane, ["exists"] = false });
            }

            text = TextUtil.NormalizeLineEndings(text);
            var truncated = text.Length > MaxOutputCharacters;
            if (truncated)
            {
                var start = text.Length - MaxOutputCharacters;
                var boundary = text.IndexOf('\n', start);
                // Keep recent complete lines when possible. A single huge final line is necessarily a partial tail.
                if (boundary >= start && boundary < text.Length - 1)
                {
                    start = boundary + 1;
                }
                else if (start < text.Length && char.IsLowSurrogate(text[start]) && start > 0 && char.IsHighSurrogate(text[start - 1]))
                {
                    start++;
                }

                text = "[earlier output truncated at 60000 characters]\n" + text.Substring(start);
            }

            var summary = text.Length == 0 ? "Output pane '" + pane + "' is empty."
                : "Output pane '" + pane + "' (last " + maxLines.ToString(CultureInfo.InvariantCulture) + " lines at most):\n" + text;
            return HostToolResult.Text(summary, new JObject { ["pane"] = pane, ["exists"] = true, ["maxLines"] = maxLines, ["truncated"] = truncated });
        }
    }
}
