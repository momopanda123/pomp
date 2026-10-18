using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;
using Pomp.Adapters;
using Pomp.Hosting;
using Pomp.Permissions;

namespace Pomp.Tools
{
    /// <summary>Discovery and runs both require approval: the test platform can build and execute project code.</summary>
    internal sealed class ManageTestsTool : HostToolBase
    {
        internal const int MaxListedTests = 2000;
        internal const int MaxReportedResults = 100;
        internal const int MaxOutputCharacters = 60000;
        internal static readonly string[] Actions = { "list", "run" };

        public ManageTestsTool(HostToolContext context)
            : base(context, CreateDefinition())
        {
        }

        private static HostToolDefinition CreateDefinition()
        {
            return new HostToolDefinition
            {
                Name = HostToolNames.ManageTests,
                Label = "Tests",
                Description =
                    "List tests grouped by loaded test project, or run tests and inspect failure messages and stack traces. " +
                    "Listing requires approval because discovery can build the project and load test code. " +
                    "Running requires approval because it builds and executes project code. " +
                    "For list, filter is a case-insensitive substring of the fully qualified name; for run it is a .NET test filter expression. " +
                    "A completed run with failing tests is a normal result. Runs are sequential and the timeout covers the whole run. " +
                    "Neither action starts while a Visual Studio build is running.",
                Parameters = new SchemaBuilder()
                    .Enum("action", "list or run.", Actions, required: true)
                    .String("project", "Unique test project name or full project path (default: every loaded test project).")
                    .String("filter", "list: name substring; run: .NET test filter expression, e.g. FullyQualifiedName~Parser.")
                    .Integer("timeoutSeconds", "run: whole-run time limit in seconds.", 10, 3600, 600)
                    .Build(),
                LoadMode = EssentialLoadMode,
            };
        }

        public override PermissionKind ResolvePermission(HostToolInvocation invocation)
        {
            var action = ToolArguments.PeekEnum(invocation?.Arguments, "action", Actions);
            return action == "list" || action == "run" ? PermissionKind.TestRun : PermissionKind.None;
        }

        public override Task<PermissionRequest?> DescribePermissionAsync(HostToolInvocation invocation, CancellationToken cancellationToken)
        {
            var args = ReadArguments(invocation?.Arguments, out var action, out var project, out var filter, out var timeout);
            if (args.HasProblems || (action != "run" && action != "list"))
            {
                return Task.FromResult<PermissionRequest?>(null);
            }

            var scope = PompLanguage.Pick("프로젝트: ", "Project: ") + (project == null ? PompLanguage.Pick("모든 테스트 프로젝트", "all test projects") : DisplayText(project, 24576));
            if (action == "list")
            {
                return Task.FromResult<PermissionRequest?>(NewPermissionRequest(
                    PermissionKind.TestRun, PompLanguage.Pick("테스트를 검색할까요?", "Discover tests?"),
                    scope + "\n" + PompLanguage.Pick("검색은 프로젝트를 빌드하고 테스트 어댑터 코드를 실행할 수 있어요.", "Discovery can build projects and run test adapter code."),
                    project ?? PompLanguage.Pick("모든 테스트 프로젝트", "all test projects")));
            }

            var message = scope +
                "\n" + PompLanguage.Pick("필터: ", "Filter: ") + (filter == null ? PompLanguage.Pick("없음", "none") : DisplayText(filter, 24576)) +
                "\n" + PompLanguage.Pick("제한 시간: " + timeout.ToString(CultureInfo.InvariantCulture) + "초", "Timeout: " + timeout.ToString(CultureInfo.InvariantCulture) + " seconds") +
                "\n" + PompLanguage.Pick("테스트와 빌드 대상이 프로그램을 실행하고 파일을 쓸 수 있어요.", "Tests and build targets can run programs and write files.");
            return Task.FromResult<PermissionRequest?>(NewPermissionRequest(
                PermissionKind.TestRun, PompLanguage.Pick("테스트를 실행할까요?", "Run tests?"), message, project ?? PompLanguage.Pick("모든 테스트 프로젝트", "all test projects")));
        }

        protected override async Task<HostToolResult> ExecuteCoreAsync(HostToolInvocation invocation, CancellationToken cancellationToken)
        {
            var args = ReadArguments(invocation.Arguments, out var action, out var project, out var filter, out var timeout);
            if (args.TryGetError(Name, out var invalid))
            {
                return invalid;
            }

            if (action == "list")
            {
                ReportProgress(invocation, "Discovering tests");
                var listed = await Adapters.Tests.ListTestsAsync(project, cancellationToken).ConfigureAwait(false);
                return listed == null ? Fail("the test adapter returned no list.") : FormatList(listed, filter);
            }

            ReportProgress(invocation, "Running tests in " + (project == null ? "all test projects" : "project '" + DisplayText(project, 1000) + "'"));
            var runTask = Adapters.Tests.RunTestsAsync(new TestRunRequest
            {
                ProjectName = project,
                Filter = filter,
                TimeoutSeconds = timeout,
            }, cancellationToken);
            while (!runTask.IsCompleted)
            {
                if (await Task.WhenAny(runTask, Task.Delay(TimeSpan.FromSeconds(5))).ConfigureAwait(false) != runTask)
                {
                    ReportProgress(invocation, cancellationToken.IsCancellationRequested
                        ? "Stopping the test runner and its child processes"
                        : "Tests are still running");
                }
            }

            var result = await runTask.ConfigureAwait(false);
            ReportProgress(invocation, "Test run finished.");
            return result == null ? Fail("the test adapter returned no run result.") : FormatRun(result);
        }

        private static ToolArguments ReadArguments(JObject? arguments, out string? action, out string? project, out string? filter, out int timeout)
        {
            var args = new ToolArguments(arguments, "action", "project", "filter", "timeoutSeconds");
            action = args.RequiredEnum("action", Actions);
            project = args.OptionalString("project");
            filter = args.OptionalString("filter");
            timeout = args.OptionalInt("timeoutSeconds", 10, 3600) ?? 600;
            ValidateText(args, "project", project);
            ValidateText(args, "filter", filter);
            return args;
        }

        private static void ValidateText(ToolArguments args, string name, string? value)
        {
            if (value != null && (value.Length > 4096 || value.Any(char.IsControl)))
            {
                args.AddProblem("'" + name + "' must be at most 4096 characters and contain no control characters");
            }
        }

        private static HostToolResult FormatList(TestListResult result, string? filter)
        {
            var matching = (result.Tests ?? new List<TestCaseInfo>())
                .Where(t => t != null && (filter == null || (t.FullyQualifiedName ?? string.Empty).IndexOf(filter, StringComparison.OrdinalIgnoreCase) >= 0))
                .ToList();
            var groups = matching.GroupBy(t => t.Project ?? string.Empty, StringComparer.OrdinalIgnoreCase).ToList();
            var errors = (result.Errors ?? new List<string>()).Where(e => !string.IsNullOrWhiteSpace(e)).ToList();
            var builder = new StringBuilder("Tests: ").Append(matching.Count).Append(" in ").Append(groups.Count).Append(" project(s).");
            if (filter != null)
            {
                builder.Append(" Filter: '").Append(DisplayText(filter, 1000)).Append("'.");
            }

            var shown = 0;
            foreach (var group in groups)
            {
                if (shown >= MaxListedTests || builder.Length >= MaxOutputCharacters - 8000)
                {
                    break;
                }

                builder.Append('\n').Append(DisplayText(string.IsNullOrWhiteSpace(group.Key) ? "(unknown project)" : group.Key, 1000));
                foreach (var test in group.OrderBy(t => t.FullyQualifiedName, StringComparer.OrdinalIgnoreCase))
                {
                    if (shown >= MaxListedTests || builder.Length >= MaxOutputCharacters - 8000)
                    {
                        break;
                    }

                    builder.Append("\n  ").Append(DisplayText(test.FullyQualifiedName, 1200));
                    shown++;
                }
            }

            if (shown < matching.Count)
            {
                builder.Append("\n[").Append(matching.Count - shown).Append(" more test(s) not shown; narrow with project or filter]");
            }

            foreach (var error in errors.Take(10))
            {
                builder.Append("\nDiscovery: ").Append(DisplayText(error, 500));
            }

            if (errors.Count > 10)
            {
                builder.Append("\n[").Append(errors.Count - 10).Append(" more discovery error(s) not shown]");
            }

            var details = new JObject
            {
                ["tests"] = matching.Count,
                ["projects"] = groups.Count,
                ["shown"] = shown,
                ["errors"] = errors.Count,
                ["truncated"] = shown < matching.Count,
            };
            return new HostToolResult(new[] { HostToolContent.FromText(builder.ToString()) }, matching.Count == 0 && errors.Count > 0, details);
        }

        private static HostToolResult FormatRun(TestRunResult result)
        {
            var builder = new StringBuilder("Test run: ").Append(result.Total).Append(" total, ").Append(result.Passed)
                .Append(" passed, ").Append(result.Failed).Append(" failed, ").Append(result.Skipped).Append(" skipped.");
            if (result.TimedOut)
            {
                builder.Append(" Timed out or cancelled; results may be incomplete.");
            }

            if (!string.IsNullOrWhiteSpace(result.Error))
            {
                builder.Append("\nRunner error: ").Append(DisplayText(result.Error, 3000));
            }

            var outcomes = (result.Results ?? new List<TestOutcomeInfo>()).Where(t => t != null).OrderBy(t => IsFailure(t.Outcome) ? 0 : 1).ToList();
            var shown = 0;
            foreach (var test in outcomes.Take(MaxReportedResults))
            {
                if (builder.Length >= MaxOutputCharacters - 16000)
                {
                    break;
                }

                builder.Append('\n').Append(DisplayText(test.Outcome, 80)).Append(' ').Append(DisplayText(test.Name, 1200))
                    .Append(" [").Append(DisplayText(test.Project, 600)).Append(']');
                if (test.DurationMs.HasValue && !double.IsNaN(test.DurationMs.Value) && !double.IsInfinity(test.DurationMs.Value))
                {
                    builder.Append(" (").Append(test.DurationMs.Value.ToString("0.###", CultureInfo.InvariantCulture)).Append(" ms)");
                }

                if (!string.IsNullOrWhiteSpace(test.Message))
                {
                    builder.Append("\n  Message: ").Append(DisplayText(test.Message, 1500, multiline: true));
                }

                if (!string.IsNullOrWhiteSpace(test.StackTrace))
                {
                    builder.Append("\n  Stack trace: ").Append(DisplayText(test.StackTrace, 2500, multiline: true));
                }

                shown++;
            }

            var failuresShown = outcomes.Take(shown).Count(t => IsFailure(t.Outcome));
            if (outcomes.Count > shown || result.Failed > failuresShown)
            {
                builder.Append("\n[additional results not shown; ").Append(Math.Max(0, result.Failed - failuresShown)).Append(" failure(s) omitted]");
            }

            if (!string.IsNullOrWhiteSpace(result.OutputTail))
            {
                builder.Append("\nRunner output (tail):\n").Append(DisplayText(LastLines(result.OutputTail!, 80), 8000, multiline: true));
            }

            var details = new JObject
            {
                ["total"] = result.Total,
                ["passed"] = result.Passed,
                ["failed"] = result.Failed,
                ["skipped"] = result.Skipped,
                ["timedOut"] = result.TimedOut,
                ["shown"] = shown,
            };
            return new HostToolResult(new[] { HostToolContent.FromText(builder.ToString()) }, result.TimedOut || !string.IsNullOrWhiteSpace(result.Error), details);
        }

        private static bool IsFailure(string? outcome) => !string.Equals(outcome, "Passed", StringComparison.OrdinalIgnoreCase)
            && !string.Equals(outcome, "Skipped", StringComparison.OrdinalIgnoreCase)
            && !string.Equals(outcome, "NotExecuted", StringComparison.OrdinalIgnoreCase);

        private static string LastLines(string text, int maximum)
        {
            var lines = TextUtil.NormalizeLineEndings(text).Split('\n');
            return string.Join("\n", lines.Skip(Math.Max(0, lines.Length - maximum)));
        }

        // Preserve arbitrary test/project names unambiguously, without terminal controls or non-ASCII model-facing output.
        private static string DisplayText(string? text, int maximum, bool multiline = false)
        {
            var builder = new StringBuilder();
            foreach (var c in text ?? string.Empty)
            {
                string part;
                if (c == '\r')
                {
                    continue;
                }

                if (c == '\n' || c == '\t')
                {
                    part = c == '\n' && multiline ? "\n  " : " ";
                }
                else if (c >= ' ' && c < 127)
                {
                    part = c.ToString();
                }
                else
                {
                    part = "\\u" + ((int)c).ToString("X4", CultureInfo.InvariantCulture);
                }

                if (builder.Length + part.Length > maximum) break;

                builder.Append(part);
            }

            return builder.ToString();
        }
    }
}
