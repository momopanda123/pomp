using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;
using Pomp.Adapters;
using Pomp.Diagnostics;
using Pomp.Hosting;
using Pomp.Permissions;

namespace Pomp.Tools
{
    /// <summary>Removes an approved, identity-bound project item, optionally recycling the disk file.</summary>
    internal sealed class RemoveProjectFileTool : ProjectItemChangeTool
    {
        public RemoveProjectFileTool(HostToolContext context)
            : base(context, CreateDefinition(), rename: false)
        {
        }

        private static HostToolDefinition CreateDefinition()
        {
            return new HostToolDefinition
            {
                Name = HostToolNames.RemoveProjectFile,
                Label = "Remove Project File",
                Description = "Remove one file from exactly one loaded C# or VB MSBuild project after approval of its exact target. " +
                    "By default the file stays on disk. With deleteFile=true, move it to the Recycle Bin; permanent deletion is never a fallback. " +
                    "Linked files outside the project folder, grouped/dependent items and dirty documents are refused. " +
                    "The file and project identities are rechecked after approval. A failed recycle may leave the file on disk but already removed from the project; inspect the reported state before retrying.",
                Parameters = new SchemaBuilder()
                    .String("path", "Workspace-relative or absolute path of a file that belongs to a loaded project.", required: true)
                    .Boolean("deleteFile", "Also delete the file (to the recycle bin where possible) instead of only removing it from the project.", false)
                    .Build(),
                LoadMode = EssentialLoadMode,
            };
        }
    }

    /// <summary>Shared frozen-plan lifecycle for the two project-item mutation tools.</summary>
    internal abstract class ProjectItemChangeTool : HostToolBase, IPreparedHostTool
    {
        private readonly bool rename;

        protected ProjectItemChangeTool(HostToolContext context, HostToolDefinition definition, bool rename)
            : base(context, definition)
        {
            this.rename = rename;
        }

        public override PermissionKind ResolvePermission(HostToolInvocation invocation) => PermissionKind.ProjectChange;

        public async Task<IPreparedHostToolCall> PrepareAsync(HostToolInvocation invocation, CancellationToken cancellationToken)
        {
            if (invocation == null)
            {
                throw new ToolException("No invocation was provided.");
            }

            cancellationToken.ThrowIfCancellationRequested();
            var arguments = new ToolArguments(invocation.Arguments, rename ? new[] { "path", "newName" } : new[] { "path", "deleteFile" });
            var path = arguments.RequiredString("path", trim: false);
            var newName = rename ? arguments.RequiredString("newName", trim: false) : null;
            var deletesFile = !rename && (arguments.OptionalBool("deleteFile") ?? false);
            if (arguments.TryGetError(Name, out var invalid))
            {
                throw new ToolException(invalid.Content[0].Text!);
            }

            if (string.IsNullOrWhiteSpace(path) || path.Trim().Length != path.Length)
            {
                throw new ToolException("The file path must not be blank or start or end with whitespace.");
            }

            if (rename)
            {
                ValidatePlainFileName(newName!);
            }

            var guard = await Context.GetPathGuardAsync(cancellationToken).ConfigureAwait(false);
            var target = await Task.Run(() => guard.Resolve(path), cancellationToken).ConfigureAwait(false);
            var expectedNewPath = rename ? await Task.Run(() => guard.Resolve(Path.Combine(Path.GetDirectoryName(target)!, newName!)), cancellationToken).ConfigureAwait(false) : null;
            if (target.Length > 259 || (expectedNewPath != null && expectedNewPath.Length > 259))
            {
                throw new ToolException("Project file paths must not exceed 259 characters (Visual Studio MAX_PATH limit).");
            }

            if (rename && string.Equals(target, expectedNewPath, StringComparison.OrdinalIgnoreCase))
            {
                throw new ToolException("The new file name must differ from the existing name; case-only renames are not supported safely in this build.");
            }

            if (!(Adapters.Solution is IProjectItemChangePreparationAdapter preparation))
            {
                throw new ToolException("The Visual Studio project adapter does not support safe, prepared file removal or renaming in this build.");
            }

            var plan = rename
                ? await preparation.PrepareRenameFileAsync(target, newName!, cancellationToken).ConfigureAwait(false)
                : await preparation.PrepareRemoveFileAsync(target, deletesFile, cancellationToken).ConfigureAwait(false);
            if (plan == null)
            {
                throw new ToolException("The project adapter returned no prepared item change.");
            }

            try
            {
                var projectName = plan.ProjectName;
                var projectPath = plan.ProjectPath;
                await Task.Run(() =>
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    if (string.IsNullOrWhiteSpace(projectName) || !Path.IsPathRooted(projectPath) || !Path.IsPathRooted(plan.Path)
                        || (plan.NewPath != null && !Path.IsPathRooted(plan.NewPath)))
                    {
                        throw new ToolException("The project adapter returned no full project or file identity; nothing was approved.");
                    }

                    guard.Resolve(projectPath);
                    var reportedTarget = guard.Resolve(plan.Path);
                    var reportedNewPath = plan.NewPath == null ? null : guard.Resolve(plan.NewPath);
                    if (!string.Equals(reportedTarget, target, StringComparison.OrdinalIgnoreCase)
                        || !string.Equals(reportedNewPath, expectedNewPath, StringComparison.OrdinalIgnoreCase)
                        || plan.DeletesFile != deletesFile)
                    {
                        throw new ToolException("The project adapter changed the requested target or operation; nothing was approved.");
                    }
                }, cancellationToken).ConfigureAwait(false);

                var details = PompLanguage.Pick("프로젝트: ", "Project: ") + projectName + " (" + projectPath + ")"
                    + PompLanguage.Pick("\n파일: ", "\nFile: ") + guard.ToDisplayPath(target);
                string title;
                if (rename)
                {
                    title = PompLanguage.Pick("프로젝트 파일 이름을 바꿀까요?", "Rename the project file?");
                    details += PompLanguage.Pick("\n새 이름: ", "\nNew name: ") + newName
                        + PompLanguage.Pick("\n대상 경로: ", "\nDestination: ") + expectedNewPath;
                }
                else
                {
                    title = deletesFile
                        ? PompLanguage.Pick("프로젝트에서 파일을 제거하고 휴지통으로 옮길까요?", "Remove the file from the project and move it to the Recycle Bin?")
                        : PompLanguage.Pick("프로젝트에서 파일을 제거할까요?", "Remove the file from the project?");
                    details += PompLanguage.Pick("\n디스크의 파일: ", "\nFile on disk: ")
                        + (deletesFile ? PompLanguage.Pick("휴지통으로 이동", "moved to the Recycle Bin") : PompLanguage.Pick("그대로 유지", "kept"));
                }

                var request = NewPermissionRequest(PermissionKind.ProjectChange, title, details, target);
                return new PreparedCall(this, plan, request, guard.Root, projectName, target, expectedNewPath, deletesFile);
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

        private static void ValidatePlainFileName(string name)
        {
            if (string.IsNullOrWhiteSpace(name) || name.Trim().Length != name.Length || name == "." || name == ".."
                || name.EndsWith(".", StringComparison.Ordinal) || name.IndexOfAny(new[] { '/', '\\', ':', '<', '>', '"', '|', '*', '?' }) >= 0)
            {
                throw new ToolException("newName must be a plain file name without separators, invalid characters, leading/trailing whitespace or a trailing dot.");
            }

            foreach (var character in name)
            {
                if (character < ' ')
                {
                    throw new ToolException("newName must not contain control characters.");
                }
            }

            // Reserved device names and bidirectional controls are checked by the workspace PathGuard before any adapter call.
        }

        private sealed class PreparedCall : IPreparedHostToolCall
        {
            private readonly object gate = new object();
            private readonly ProjectItemChangeTool tool;
            private readonly string workspaceRoot;
            private readonly string projectName;
            private readonly string path;
            private readonly string? newPath;
            private readonly bool deletesFile;
            private IPreparedProjectItemChange? plan;
            private bool executed;

            public PreparedCall(ProjectItemChangeTool tool, IPreparedProjectItemChange plan, PermissionRequest permission,
                string workspaceRoot, string projectName, string path, string? newPath, bool deletesFile)
            {
                this.tool = tool;
                this.plan = plan;
                this.workspaceRoot = workspaceRoot;
                this.projectName = projectName;
                this.path = path;
                this.newPath = newPath;
                this.deletesFile = deletesFile;
                Permission = permission;
            }

            public PermissionRequest Permission { get; }

            public async Task<HostToolResult> ExecuteAsync(CancellationToken cancellationToken)
            {
                IPreparedProjectItemChange prepared;
                lock (gate)
                {
                    if (plan == null || executed)
                    {
                        return tool.Fail("The prepared item change is disposed or was already executed; request new approval.");
                    }

                    executed = true;
                    prepared = plan;
                    // Ownership moves to this execution; concurrent Dispose cannot invalidate an in-flight plan.
                    plan = null;
                }

                try
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var currentGuard = await tool.Context.GetPathGuardAsync(cancellationToken).ConfigureAwait(false);
                    if (!string.Equals(currentGuard.Root, workspaceRoot, StringComparison.OrdinalIgnoreCase))
                    {
                        return tool.Fail("The workspace changed after approval; nothing was changed. Request approval again.");
                    }

                    var result = await prepared.CommitAsync(cancellationToken).ConfigureAwait(false);
                    if (result == null)
                    {
                        return tool.Fail("The project adapter returned no result." + InspectionMessage());
                    }

                    if (!result.Succeeded)
                    {
                        return tool.Fail((result.Error ?? "The project item change failed; partial changes may remain.") + InspectionMessage());
                    }

                    var expectedOutput = newPath ?? path;
                    if (!string.Equals(result.Path, expectedOutput, StringComparison.OrdinalIgnoreCase))
                    {
                        return tool.Fail("The project adapter reported a different output path '" + result.Path + "'." + InspectionMessage());
                    }

                    var text = newPath != null
                        ? "Renamed '" + currentGuard.ToDisplayPath(path) + "' to '" + currentGuard.ToDisplayPath(newPath) + "' in project '" + projectName + "'."
                        : "Removed '" + currentGuard.ToDisplayPath(path) + "' from project '" + projectName + "'"
                            + (deletesFile ? " and moved the file to the Recycle Bin." : "; the file stays on disk.");
                    if (!string.IsNullOrWhiteSpace(result.Error))
                    {
                        text += " Note: " + result.Error;
                    }

                    return HostToolResult.Text(text, new JObject
                    {
                        ["succeeded"] = true, ["path"] = path, ["newPath"] = newPath, ["project"] = projectName, ["deletedFile"] = deletesFile,
                    });
                }
                catch (Exception ex)
                {
                    return tool.Fail(ToolErrors.ShortMessage(ex) + InspectionMessage());
                }
                finally
                {
                    try
                    {
                        prepared.Dispose();
                    }
                    catch (Exception ex)
                    {
                        // Cleanup must not hide a mutation's result; the dispatcher also treats disposal as best effort.
                        tool.Context.Logger.Warn("Could not release the prepared project item change: " + ToolErrors.ShortMessage(ex), ex);
                    }
                }
            }

            private string InspectionMessage() => " Approved target: '" + path + "'" + (newPath == null ? "." : "; destination: '" + newPath + "'.")
                + " Inspect the file and project before retrying; partial changes may remain.";

            public void Dispose()
            {
                IPreparedProjectItemChange? pending;
                lock (gate)
                {
                    pending = plan;
                    plan = null;
                }

                pending?.Dispose();
            }
        }
    }
}
