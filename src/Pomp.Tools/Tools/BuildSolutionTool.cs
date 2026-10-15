using System;
using System.Collections.Generic;
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
    /// <summary>
    /// <c>build_solution</c>: builds the solution or one project through the Visual Studio build system and waits.
    /// A failed build is a normal result (not a tool error): the model needs the diagnostics to react.
    /// Requires approval: project build targets can execute arbitrary commands.
    /// </summary>
    internal sealed class BuildSolutionTool : HostToolBase
    {
        internal const int MaxErrorsShown = 30;

        private const int MaxSummaryLength = 300;

        public BuildSolutionTool(HostToolContext context)
            : base(context, CreateDefinition())
        {
        }

        public override PermissionKind ResolvePermission(HostToolInvocation invocation) => PermissionKind.ProcessExec;

        public override Task<PermissionRequest?> DescribePermissionAsync(HostToolInvocation invocation, CancellationToken cancellationToken)
        {
            var args = new ToolArguments(invocation.Arguments, "project", "configuration");
            var project = args.OptionalString("project");
            var configuration = args.OptionalString("configuration");
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
                    "Returns `Build succeeded` or `Build FAILED` with counts, the build summary and the first " + MaxErrorsShown + " errors as " +
                    "`severity code file(line,col): message [project]` lines. Use it to verify edits; call read_error_list for the complete diagnostic list afterwards. " +
                    "A failed build is a normal result, not a tool error.",
                Parameters = new SchemaBuilder()
                    .String("project", "Name of the project to build (see query_solution). Default: the whole solution.")
                    .String("configuration", "Solution configuration such as Debug or Release, optionally with a platform (Debug|x64). Default: the active configuration and platform.")
                    .Build(),
                LoadMode = EssentialLoadMode,
            };
        }

        protected override async Task<HostToolResult> ExecuteCoreAsync(HostToolInvocation invocation, CancellationToken cancellationToken)
        {
            var args = new ToolArguments(invocation.Arguments, "project", "configuration");
            var project = args.OptionalString("project");
            var configuration = args.OptionalString("configuration");
            if (args.TryGetError(Name, out var invalid))
            {
                return invalid;
            }

            ReportProgress(invocation, "Building" + TextUtil.Ellipsis);
            var result = await Adapters.Build
                .BuildAsync(new BuildRequest { Project = project, Configuration = configuration }, cancellationToken)
                .ConfigureAwait(false);
            if (result == null)
            {
                return Fail("the build adapter returned no result.");
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
    }
}
