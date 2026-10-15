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
    /// <c>read_error_list</c>: one line per Error List item (<c>severity code file(line,col): message [project]</c>).
    /// For severity "all" the adapter is asked severity by severity (errors first) so that a cut-off list never
    /// drops errors in favor of warnings; every answer is filtered by severity again, so an adapter that ignores the
    /// filter cannot produce duplicates.
    /// </summary>
    internal sealed class ReadErrorListTool : HostToolBase
    {
        internal const int DefaultMaxItems = 100;
        internal const int MaxMaxItems = 500;

        private static readonly string[] SeverityValues = { "error", "warning", "message", "all" };

        public ReadErrorListTool(HostToolContext context)
            : base(context, CreateDefinition())
        {
        }

        private static HostToolDefinition CreateDefinition()
        {
            return new HostToolDefinition
            {
                Name = HostToolNames.ReadErrorList,
                Label = "Read Error List",
                Description =
                    "Read Visual Studio's Error List (compiler and analyzer diagnostics from builds and live analysis). " +
                    "One line per item: `severity code file(line,col): message [project]`, errors first; file paths are relative to the workspace root. " +
                    "Use it after edits or a build to see what is broken, or when the user asks about errors or warnings. " +
                    "Nothing is built or changed; call build_solution to refresh the diagnostics.",
                Parameters = new SchemaBuilder()
                    .Enum("severity", "Which items to return: error, warning, message, or all.", SeverityValues, "all")
                    .String("project", "Only items of this project (project name).")
                    .Integer("maxItems", "Maximum number of items returned.", 1, MaxMaxItems, DefaultMaxItems)
                    .Build(),
                LoadMode = EssentialLoadMode,
            };
        }

        protected override async Task<HostToolResult> ExecuteCoreAsync(HostToolInvocation invocation, CancellationToken cancellationToken)
        {
            var args = new ToolArguments(invocation.Arguments, "severity", "project", "maxItems");
            var severityName = args.OptionalEnum("severity", SeverityValues) ?? "all";
            var project = args.OptionalString("project");
            var maxItems = args.OptionalInt("maxItems", 1, MaxMaxItems) ?? DefaultMaxItems;
            if (args.TryGetError(Name, out var invalid))
            {
                return invalid;
            }

            var severities = SeveritiesFor(severityName);
            var collected = new List<ErrorListItem>();
            var more = false;
            foreach (var severity in severities)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var remaining = maxItems - collected.Count;
                var filter = new ErrorListFilter
                {
                    Severities = new List<ErrorSeverity> { severity },
                    Project = project,
                    MaxItems = remaining + 1,
                };

                var chunk = await Adapters.ErrorList.ReadAsync(filter, cancellationToken).ConfigureAwait(false);
                var matching = (chunk ?? new List<ErrorListItem>()).Where(i => i != null && i.Severity == severity).ToList();
                if (matching.Count > remaining)
                {
                    more = true;
                    collected.AddRange(matching.Take(remaining));
                    break;
                }

                collected.AddRange(matching);
            }

            var guard = await Context.TryGetPathGuardAsync(cancellationToken).ConfigureAwait(false);
            var display = HostToolContext.DisplayFunc(guard);

            var errors = collected.Count(i => i.Severity == ErrorSeverity.Error);
            var warnings = collected.Count(i => i.Severity == ErrorSeverity.Warning);
            var messages = collected.Count(i => i.Severity == ErrorSeverity.Message);

            var builder = new StringBuilder();
            if (collected.Count == 0)
            {
                builder.Append("No ").Append(EmptyLabel(severityName)).Append(" in the Error List");
                if (project != null)
                {
                    builder.Append(" for project '").Append(project).Append('\'');
                }

                builder.Append('.');
            }
            else
            {
                builder.Append("Error List: ").Append(Count(collected.Count, "item")).Append(" (").Append(Summary(errors, warnings, messages)).Append(')');
                foreach (var item in collected)
                {
                    builder.Append('\n').Append(DiagnosticFormat.FormatItem(item, display));
                }

                if (more)
                {
                    builder.Append("\n[showing the first ").Append(maxItems).Append(" items; more exist - raise maxItems or narrow with severity/project]");
                }
            }

            var details = new JObject
            {
                ["errors"] = errors,
                ["warnings"] = warnings,
                ["messages"] = messages,
                ["truncated"] = more,
            };
            return HostToolResult.Text(builder.ToString(), details);
        }

        private static IReadOnlyList<ErrorSeverity> SeveritiesFor(string name)
        {
            switch (name)
            {
                case "error":
                    return new[] { ErrorSeverity.Error };
                case "warning":
                    return new[] { ErrorSeverity.Warning };
                case "message":
                    return new[] { ErrorSeverity.Message };
                default:
                    return new[] { ErrorSeverity.Error, ErrorSeverity.Warning, ErrorSeverity.Message };
            }
        }

        private static string EmptyLabel(string severityName)
        {
            switch (severityName)
            {
                case "error":
                    return "errors";
                case "warning":
                    return "warnings";
                case "message":
                    return "messages";
                default:
                    return "items";
            }
        }

        private static string Summary(int errors, int warnings, int messages)
        {
            var parts = new List<string>();
            if (errors > 0)
            {
                parts.Add(Count(errors, "error"));
            }

            if (warnings > 0)
            {
                parts.Add(Count(warnings, "warning"));
            }

            if (messages > 0)
            {
                parts.Add(Count(messages, "message"));
            }

            return string.Join(", ", parts);
        }

        private static string Count(int count, string noun) => count + " " + noun + (count == 1 ? string.Empty : "s");
    }
}
