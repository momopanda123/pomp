using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;
using Pomp.Adapters;
using Pomp.Diagnostics;
using Pomp.Hosting;
using Pomp.Permissions;

namespace Pomp.Tools
{
    /// <summary>
    /// <c>build_solution</c>: builds the solution or one project through the Visual Studio build system and waits.
    /// A failed build is a normal result (not a tool error): the model needs the diagnostics to react. Nothing built (no solution,
    /// debugging, a build Visual Studio did not start) and a time-out are tool errors that say why.
    /// Requires approval: project build targets can execute arbitrary commands.
    /// </summary>
    internal sealed class BuildSolutionTool : HostToolBase
    {
        internal const int MaxErrorsShown = 30;
        internal const int DefaultTimeoutSeconds = 600;

        private const int MaxSummaryLength = 300;
        private const int MinTimeoutSeconds = 10;
        private const int MaxTimeoutSeconds = 3600;

        private static readonly TimeSpan ProgressInterval = TimeSpan.FromSeconds(5);

        /// <summary>
        /// How long past the time limit the adapter may take to cancel its build and answer. omp never times out a host tool, so
        /// past this the call ends without the adapter: Visual Studio is not responding, typically because of a modal dialog.
        /// </summary>
        private static readonly TimeSpan ResponseGrace = TimeSpan.FromSeconds(20);

        public BuildSolutionTool(HostToolContext context)
            : base(context, CreateDefinition())
        {
        }

        public override PermissionKind ResolvePermission(HostToolInvocation invocation) => PermissionKind.ProcessExec;

        public override Task<PermissionRequest?> DescribePermissionAsync(HostToolInvocation invocation, CancellationToken cancellationToken)
        {
            ReadArguments(invocation.Arguments, out var project, out var configuration, out _);
            var target = project ?? PompLanguage.Pick("솔루션 전체", "the whole solution");
            var message = new StringBuilder();
            if (project != null)
            {
                message.Append(PompLanguage.Pick("프로젝트: ", "Project: ")).Append(project).Append('\n');
            }

            if (configuration != null)
            {
                message.Append(PompLanguage.Pick("구성: ", "Configuration: ")).Append(configuration).Append('\n');
            }

            message.Append(PompLanguage.Pick("빌드 대상이 프로그램을 실행하고 파일을 쓸 수 있어요.", "Build targets can run programs and write files."));
            return Task.FromResult<PermissionRequest?>(NewPermissionRequest(
                PermissionKind.ProcessExec,
                project == null
                    ? PompLanguage.Pick("솔루션 전체를 빌드할까요?", "Build the whole solution?")
                    : PompLanguage.Pick("프로젝트를 빌드할까요?", "Build the project?"),
                message.ToString(),
                target));
        }

        private static HostToolDefinition CreateDefinition()
        {
            return new HostToolDefinition
            {
                Name = HostToolNames.BuildSolution,
                Label = "Build",
                Description =
                    "Build the whole solution, or one project, with Visual Studio's build system and wait until it finishes (this can take minutes on large solutions). " +
                    "A build already running in Visual Studio is waited for first. " +
                    "Returns `Build succeeded` or `Build FAILED` with counts, the build summary and the first " + MaxErrorsShown + " errors as " +
                    "`severity code file(line,col): message [project]` lines. Use it to verify edits; call read_error_list for the complete diagnostic list afterwards. " +
                    "A failed build is a normal result, not a tool error. An error result means nothing was built or the build was not followed to its end " +
                    "(no solution, a debug session, the time limit) and says why; when the time limit runs out, a build this call started is cancelled.",
                Parameters = new SchemaBuilder()
                    .String("project", "Name of the project to build (see query_solution). Default: the whole solution.")
                    .String("configuration", "Solution configuration such as Debug or Release, optionally with a platform (Debug|x64). Default: the active configuration and platform.")
                    .Integer("timeoutSeconds", "Time limit in seconds for waiting for a running build and building together.", MinTimeoutSeconds, MaxTimeoutSeconds, DefaultTimeoutSeconds)
                    .Build(),
                LoadMode = EssentialLoadMode,
            };
        }

        protected override async Task<HostToolResult> ExecuteCoreAsync(HostToolInvocation invocation, CancellationToken cancellationToken)
        {
            var args = ReadArguments(invocation.Arguments, out var project, out var configuration, out var timeoutSeconds);
            if (args.TryGetError(Name, out var invalid))
            {
                return invalid;
            }

            var status = new BuildStatus(invocation);
            status.Report("Building");
            var limit = TimeSpan.FromSeconds(timeoutSeconds);
            var giveUp = limit + ResponseGrace;
            var clock = Stopwatch.StartNew();
            BuildResult? result;
            using (var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken))
            {
                timeout.CancelAfter(limit);
                var build = Adapters.Build.BuildAsync(new BuildRequest { Project = project, Configuration = configuration, Progress = status }, timeout.Token);
                while (!build.IsCompleted && !cancellationToken.IsCancellationRequested)
                {
                    var left = giveUp - clock.Elapsed;
                    if (left <= TimeSpan.Zero)
                    {
                        break;
                    }

                    await Task.WhenAny(build, Task.Delay(left < ProgressInterval ? left : ProgressInterval, cancellationToken)).ConfigureAwait(false);
                    if (!build.IsCompleted && !cancellationToken.IsCancellationRequested)
                    {
                        status.ReportElapsed(clock.Elapsed);
                    }
                }

                if (!build.IsCompleted)
                {
                    ObserveAbandoned(build);
                    cancellationToken.ThrowIfCancellationRequested();
                    return Fail("Visual Studio did not answer within " + Seconds(giveUp) + " seconds (last status: " + status.Latest + "). " +
                                "Check Visual Studio for a dialog waiting for an answer before building again.");
                }

                try
                {
                    result = await build.ConfigureAwait(false);
                }
                catch (OperationCanceledException ex) when (timeout.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
                {
                    return Fail("no result within the time limit of " + timeoutSeconds.ToString(CultureInfo.InvariantCulture) + " seconds. " + ex.Message);
                }
            }

            if (result == null)
            {
                return Fail("the build adapter returned no result.");
            }

            if (result.Error != null)
            {
                return Fail(result.Error);
            }

            var guard = await Context.TryGetPathGuardAsync(cancellationToken).ConfigureAwait(false);
            var display = HostToolContext.DisplayFunc(guard);

            var diagnostics = (IEnumerable<ErrorListItem>?)result.Errors ?? new List<ErrorListItem>();
            var errors = diagnostics.Where(i => i != null && i.Severity == ErrorSeverity.Error).ToList();
            // The adapter caps the list; its totals count everything the Error List held after the build.
            var errorCount = Math.Max(result.ErrorCount, errors.Count);
            var warningCount = Math.Max(result.WarningCount, diagnostics.Count(i => i != null && i.Severity == ErrorSeverity.Warning));

            var builder = new StringBuilder("Build");
            if (project != null)
            {
                builder.Append(" of '").Append(project).Append('\'');
            }

            if (configuration != null)
            {
                builder.Append(" (").Append(configuration).Append(')');
            }

            if (result.Succeeded)
            {
                builder.Append(" succeeded.");
                if (warningCount > 0)
                {
                    builder.Append(' ').Append(warningCount).Append(warningCount == 1 ? " warning." : " warnings.");
                }
            }
            else
            {
                var facts = new List<string>();
                if (result.FailedProjects > 0)
                {
                    facts.Add(result.FailedProjects + (result.FailedProjects == 1 ? " project failed" : " projects failed"));
                }

                facts.Add(errorCount + (errorCount == 1 ? " error" : " errors"));
                if (warningCount > 0)
                {
                    facts.Add(warningCount + (warningCount == 1 ? " warning" : " warnings"));
                }

                builder.Append(" FAILED: ").Append(string.Join(", ", facts)).Append('.');
            }

            var summary = TextUtil.ToSingleLine(result.Summary, MaxSummaryLength);
            if (summary.Length > 0)
            {
                builder.Append('\n').Append(summary);
            }

            var shown = errors.Take(MaxErrorsShown).ToList();
            foreach (var item in shown)
            {
                builder.Append('\n').Append(DiagnosticFormat.FormatItem(item, display));
            }

            if (errorCount > shown.Count)
            {
                builder.Append("\n[").Append(errorCount - shown.Count).Append(" more error(s) not shown; call read_error_list for the full list]");
            }

            var details = new JObject
            {
                ["succeeded"] = result.Succeeded,
                ["failedProjects"] = result.FailedProjects,
                ["errors"] = errorCount,
                ["warnings"] = warningCount,
            };
            return HostToolResult.Text(builder.ToString(), details);
        }

        private static ToolArguments ReadArguments(JObject? arguments, out string? project, out string? configuration, out int timeoutSeconds)
        {
            var args = new ToolArguments(arguments, "project", "configuration", "timeoutSeconds");
            project = args.OptionalString("project");
            configuration = args.OptionalString("configuration");
            timeoutSeconds = args.OptionalInt("timeoutSeconds", MinTimeoutSeconds, MaxTimeoutSeconds) ?? DefaultTimeoutSeconds;
            return args;
        }

        private static string Seconds(TimeSpan span) => ((int)span.TotalSeconds).ToString(CultureInfo.InvariantCulture);

        /// <summary>Observes the adapter task this call stopped waiting for; a later failure other than cancellation is logged.</summary>
        private void ObserveAbandoned(Task task)
        {
            task.ContinueWith(
                t =>
                {
                    var failure = t.Exception?.GetBaseException();
                    if (failure != null && !(failure is OperationCanceledException))
                    {
                        Context.Logger.Warn("build_solution: the build call it stopped waiting for failed later: " + ToolErrors.ShortMessage(failure), failure);
                    }
                },
                CancellationToken.None,
                TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously,
                TaskScheduler.Default);
        }

        /// <summary>Forwards the adapter's status lines as progress and keeps the latest one for a time-out message.</summary>
        private sealed class BuildStatus : IProgress<string>
        {
            private readonly object _gate = new object();
            private readonly HostToolInvocation _invocation;
            private string _latest = string.Empty;

            public BuildStatus(HostToolInvocation invocation)
            {
                _invocation = invocation;
            }

            public string Latest
            {
                get
                {
                    lock (_gate)
                    {
                        return _latest;
                    }
                }
            }

            public void Report(string value)
            {
                if (string.IsNullOrEmpty(value))
                {
                    return;
                }

                lock (_gate)
                {
                    _latest = value;
                    ReportProgress(_invocation, value);
                }
            }

            /// <summary>Repeats the latest status with the time spent so far.</summary>
            public void ReportElapsed(TimeSpan elapsed)
            {
                lock (_gate)
                {
                    ReportProgress(_invocation, _latest + " (" + Seconds(elapsed) + " s)");
                }
            }
        }
    }
}
