using System;
using System.Globalization;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;
using Pomp.Adapters;
using Pomp.Hosting;
using Pomp.Permissions;

namespace Pomp.Tools
{
    /// <summary>Creates a new file through a frozen approval plan, then registers it with the Visual Studio project system.</summary>
    internal sealed class AddProjectFileTool : HostToolBase, IPreparedHostTool
    {
        private const int MaxContentBytes = 1024 * 1024;
        private static readonly Encoding ContentEncoding = new UTF8Encoding(false, true);

        public AddProjectFileTool(HostToolContext context)
            : base(context, CreateDefinition())
        {
        }

        private static HostToolDefinition CreateDefinition()
        {
            return new HostToolDefinition
            {
                Name = HostToolNames.AddProjectFile,
                Label = "Add Project File",
                Description =
                    "Create a NEW UTF-8 file and add it through Visual Studio's project system after approval of its exact content. " +
                    "Only loaded C# and VB MSBuild projects are supported. Existing files are never overwritten. " +
                    "Use a unique project name or the full project file path from query_solution; path must be relative to that project directory, without '.' or '..' segments. " +
                    "Initial content is written unchanged as UTF-8 without BOM (maximum 1 MiB). If project registration fails, a created file or directories may remain; the error reports them for inspection.",
                Parameters = new SchemaBuilder()
                    .String("project", "Unique project name or full project file path (see query_solution).", required: true, minLength: 1)
                    .String("path", "New file path relative to the project directory; no traversal or absolute paths.", required: true, minLength: 1)
                    .String("content", "Exact initial UTF-8 text. Default: empty file; maximum 1 MiB encoded.")
                    .Build(),
                LoadMode = EssentialLoadMode,
            };
        }

        public override PermissionKind ResolvePermission(HostToolInvocation invocation) => PermissionKind.ProjectChange;

        public async Task<IPreparedHostToolCall> PrepareAsync(HostToolInvocation invocation, CancellationToken cancellationToken)
        {
            if (invocation == null)
            {
                throw new ToolException("No invocation was provided.");
            }

            cancellationToken.ThrowIfCancellationRequested();
            var arguments = new ToolArguments(invocation.Arguments, "project", "path", "content");
            var project = arguments.RequiredString("project");
            var relativePath = arguments.RequiredString("path", trim: false);
            var content = arguments.OptionalString("content", trim: false) ?? string.Empty;
            if (arguments.TryGetError(Name, out var invalid))
            {
                throw new ToolException(invalid.Content[0].Text!);
            }

            ValidateRelativePath(relativePath);
            var contentBytes = ContentEncoding.GetByteCount(content);
            if (contentBytes > MaxContentBytes)
            {
                throw new ToolException("Initial file content must not exceed 1 MiB of UTF-8 text.");
            }

            if (!(Adapters.Solution is IProjectFilePreparationAdapter preparation))
            {
                throw new ToolException("The Visual Studio project adapter does not support safe, prepared file additions in this build.");
            }

            var guard = await Context.GetPathGuardAsync(cancellationToken).ConfigureAwait(false);
            var plan = await preparation.PrepareAddFileAsync(project, relativePath, content, cancellationToken).ConfigureAwait(false);
            if (plan == null)
            {
                throw new ToolException("The project adapter returned no prepared file addition.");
            }

            try
            {
                var projectName = plan.ProjectName;
                var projectPath = plan.ProjectPath;
                var target = plan.Path;
                if (string.IsNullOrWhiteSpace(projectName) || !string.Equals(plan.Content, content, StringComparison.Ordinal))
                {
                    throw new ToolException("The project adapter changed the requested content or returned no project identity; nothing was approved.");
                }

                await Task.Run(() =>
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var resolvedProject = guard.Resolve(projectPath);
                    var projectGuard = new PathGuard(Path.GetDirectoryName(resolvedProject)!);
                    var expectedTarget = projectGuard.Resolve(relativePath);
                    var resolvedTarget = guard.Resolve(target);
                    if (!string.Equals(resolvedTarget, expectedTarget, StringComparison.OrdinalIgnoreCase))
                    {
                        throw new ToolException("The project adapter resolved a different target path; nothing was approved.");
                    }
                }, cancellationToken).ConfigureAwait(false);

                var size = contentBytes.ToString(CultureInfo.InvariantCulture);
                var request = NewPermissionRequest(PermissionKind.ProjectChange, PompLanguage.Pick("프로젝트에 파일을 추가할까요?", "Add a file to the project?"),
                    PompLanguage.Pick(
                        "프로젝트: " + projectName + " (" + projectPath + ")\n파일: " + target + "\n크기: " + size + "바이트\n새 파일을 만들고 프로젝트에 등록해요.",
                        "Project: " + projectName + " (" + projectPath + ")\nFile: " + target + "\nSize: " + size + " bytes\nCreates the new file and registers it in the project."),
                    target);
                request.Diff = CreateDiff(guard.ToDisplayPath(target), content);
                return new PreparedCall(this, plan, request, guard.Root, projectName, target);
            }
            catch
            {
                plan.Dispose();
                throw;
            }
        }

        public override async Task<PermissionRequest?> DescribePermissionAsync(HostToolInvocation invocation, CancellationToken cancellationToken)
        {
            using var prepared = await PrepareAsync(invocation, cancellationToken).ConfigureAwait(false);
            return prepared.Permission;
        }

        protected override Task<HostToolResult> ExecuteCoreAsync(HostToolInvocation invocation, CancellationToken cancellationToken) =>
            Task.FromResult(Fail("A prepared approval is required; direct execution is disabled. Request a new approved call."));

        private static void ValidateRelativePath(string path)
        {
            if (string.IsNullOrWhiteSpace(path) || Path.IsPathRooted(path) || path.IndexOf(':') >= 0)
            {
                throw new ToolException("The new file path must be relative to the project directory.");
            }

            foreach (var segment in path.Replace('\\', '/').Split('/'))
            {
                if (segment.Length == 0 || segment == "." || segment == "..")
                {
                    throw new ToolException("Empty, '.' and '..' path segments are not allowed.");
                }
            }
        }

        private static string CreateDiff(string path, string content)
        {
            var diff = new StringBuilder("new file mode 100644\n--- /dev/null\n+++ b/").Append(path).Append('\n');
            if (content.Length == 0)
            {
                return diff.Append("@@ -0,0 +0,0 @@\n").ToString();
            }

            var lines = content.Split('\n');
            var hasFinalNewline = content.EndsWith("\n", StringComparison.Ordinal);
            var count = hasFinalNewline ? lines.Length - 1 : lines.Length;
            diff.Append("@@ -0,0 +1,").Append(count.ToString(CultureInfo.InvariantCulture)).Append(" @@\n");
            for (var index = 0; index < count; index++)
            {
                diff.Append('+').Append(lines[index]).Append('\n');
            }

            if (!hasFinalNewline)
            {
                diff.Append("\\ No newline at end of file\n");
            }

            return diff.ToString();
        }

        private sealed class PreparedCall : IPreparedHostToolCall
        {
            private readonly object gate = new object();
            private readonly AddProjectFileTool tool;
            private readonly string workspaceRoot;
            private readonly string projectName;
            private readonly string target;
            private IPreparedProjectFile? plan;
            private bool executed;

            public PreparedCall(AddProjectFileTool tool, IPreparedProjectFile plan, PermissionRequest permission,
                string workspaceRoot, string projectName, string target)
            {
                this.tool = tool;
                this.plan = plan;
                this.workspaceRoot = workspaceRoot;
                this.projectName = projectName;
                this.target = target;
                Permission = permission;
            }

            public PermissionRequest Permission { get; }

            public async Task<HostToolResult> ExecuteAsync(CancellationToken cancellationToken)
            {
                IPreparedProjectFile prepared;
                lock (gate)
                {
                    if (plan == null || executed)
                    {
                        return tool.Fail("The prepared file addition is disposed or was already executed; request new approval.");
                    }

                    executed = true;
                    prepared = plan;
                }

                try
                {
                    var currentGuard = await tool.Context.GetPathGuardAsync(cancellationToken).ConfigureAwait(false);
                    if (!string.Equals(currentGuard.Root, workspaceRoot, StringComparison.OrdinalIgnoreCase))
                    {
                        return tool.Fail("The workspace changed after approval; nothing was created. Request approval again.");
                    }

                    var result = await prepared.CommitAsync(cancellationToken).ConfigureAwait(false);
                    if (result == null)
                    {
                        return tool.Fail("The project adapter returned no result. Inspect '" + target + "' and the project before retrying; partial changes may remain.");
                    }

                    if (!result.Succeeded)
                    {
                        return tool.Fail((result.Error ?? "Project registration failed; partial changes may remain.") + " Approved target: '" + target + "'.");
                    }

                    if (!string.Equals(result.Path, target, StringComparison.OrdinalIgnoreCase))
                    {
                        return tool.Fail("The project adapter reported a different output path. Inspect '" + target + "' and '" + result.Path + "' before retrying; partial changes may remain.");
                    }

                    return HostToolResult.Text("Created '" + currentGuard.ToDisplayPath(target) + "' and added it to project '" + projectName + "'.",
                        new JObject { ["succeeded"] = true, ["path"] = target, ["project"] = projectName });
                }
                catch (Exception ex)
                {
                    return tool.Fail(ToolErrors.ShortMessage(ex) + " Inspect '" + target + "' and the project before retrying; partial changes may remain.");
                }
            }

            public void Dispose()
            {
                lock (gate)
                {
                    plan?.Dispose();
                    plan = null;
                }
            }
        }
    }
}
