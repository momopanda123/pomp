using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;
using Pomp.Adapters;

namespace Pomp.Tools
{
    /// <summary>
    /// <c>list_breakpoints</c>: one line per breakpoint,
    /// <c>location enabled|disabled [binding=..] [condition="..."] [hitCount=N] [hits=N]</c>.
    /// </summary>
    internal sealed class ListBreakpointsTool : HostToolBase
    {
        internal const int MaxListed = 200;

        private const int MaxConditionLength = 200;

        public ListBreakpointsTool(HostToolContext context)
            : base(context, CreateDefinition())
        {
        }

        private static HostToolDefinition CreateDefinition()
        {
            return new HostToolDefinition
            {
                Name = HostToolNames.ListBreakpoints,
                Label = "List Breakpoints",
                Description =
                    "List the debugger breakpoints in Visual Studio, one per line: `file:line enabled|disabled [binding=state] [condition=\"...\"] [hitCount=N] [hits=N]` " +
                    "(function breakpoints show `fn Name`). Use it to see what the user has set before debugging, or to find out why a breakpoint is not hit " +
                    "(binding `unbound` or `error` means it has no code location). Optionally filter by file or to enabled breakpoints only. Read-only.",
                Parameters = new SchemaBuilder()
                    .String("file", "Only breakpoints in this file (absolute or relative to the workspace root).")
                    .Boolean("enabledOnly", "Only enabled breakpoints.", false)
                    .Build(),
                LoadMode = EssentialLoadMode,
            };
        }

        protected override async Task<HostToolResult> ExecuteCoreAsync(HostToolInvocation invocation, CancellationToken cancellationToken)
        {
            var args = new ToolArguments(invocation.Arguments, "file", "enabledOnly");
            var file = args.OptionalString("file");
            var enabledOnly = args.OptionalBool("enabledOnly") ?? false;
            if (args.TryGetError(Name, out var invalid))
            {
                return invalid;
            }

            var guard = await Context.TryGetPathGuardAsync(cancellationToken).ConfigureAwait(false);
            string? fileFilter = null;
            if (file != null)
            {
                if (guard == null)
                {
                    return Fail("no workspace root is available to resolve the file filter.");
                }

                if (!guard.TryResolve(file, out var fullPath, out var pathError))
                {
                    return Fail(pathError);
                }

                fileFilter = fullPath;
            }

            var breakpoints = await Adapters.Debugger
                .ListBreakpointsAsync(new BreakpointFilter { File = fileFilter, EnabledOnly = enabledOnly }, cancellationToken)
                .ConfigureAwait(false);
            var list = (breakpoints ?? new List<BreakpointInfo>()).Where(b => b != null).ToList();
            var display = HostToolContext.DisplayFunc(guard);

            var builder = new StringBuilder();
            if (list.Count == 0)
            {
                builder.Append("No breakpoints");
                if (file != null)
                {
                    builder.Append(" in ").Append(display(fileFilter!));
                }

                builder.Append(enabledOnly ? " (enabled only)." : ".");
                return HostToolResult.Text(builder.ToString(), new JObject { ["count"] = 0, ["enabled"] = 0 });
            }

            var enabledCount = list.Count(b => b.Enabled);
            builder.Append("Breakpoints: ").Append(list.Count).Append(" (").Append(enabledCount).Append(" enabled)");
            foreach (var breakpoint in list.Take(MaxListed))
            {
                builder.Append('\n').Append(FormatBreakpoint(breakpoint, display));
            }

            if (list.Count > MaxListed)
            {
                builder.Append("\n[").Append(list.Count - MaxListed).Append(" more breakpoint(s) not shown; filter with file or enabledOnly]");
            }

            return HostToolResult.Text(builder.ToString(), new JObject { ["count"] = list.Count, ["enabled"] = enabledCount });
        }

        internal static string FormatBreakpoint(BreakpointInfo breakpoint, Func<string, string> display)
        {
            var builder = new StringBuilder();
            var hasFile = !string.IsNullOrWhiteSpace(breakpoint.File);
            if (hasFile)
            {
                builder.Append(display(breakpoint.File!.Trim()));
                if (breakpoint.Line.HasValue && breakpoint.Line.Value > 0)
                {
                    builder.Append(':').Append(breakpoint.Line.Value);
                }
            }

            if (!string.IsNullOrWhiteSpace(breakpoint.FunctionName))
            {
                builder.Append(hasFile ? " fn " : "fn ").Append(breakpoint.FunctionName!.Trim());
            }
            else if (!hasFile)
            {
                builder.Append("(unknown location)");
            }

            builder.Append(breakpoint.Enabled ? " enabled" : " disabled");
            if (!string.IsNullOrWhiteSpace(breakpoint.BindingState))
            {
                builder.Append(" binding=").Append(breakpoint.BindingState!.Trim());
            }

            if (!string.IsNullOrWhiteSpace(breakpoint.Condition))
            {
                builder.Append(" condition=\"").Append(TextUtil.ToSingleLine(breakpoint.Condition, MaxConditionLength)).Append('"');
            }

            if (breakpoint.HitCountTarget.HasValue)
            {
                builder.Append(" hitCount=").Append(breakpoint.HitCountTarget.Value);
            }

            if (breakpoint.CurrentHits > 0)
            {
                builder.Append(" hits=").Append(breakpoint.CurrentHits);
            }

            return builder.ToString();
        }
    }
}
