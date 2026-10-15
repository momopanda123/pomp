using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using EnvDTE;
using Microsoft.VisualStudio;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Shell.Interop;
using Microsoft.VisualStudio.Threading;
using Pomp.Adapters;
using Pomp.VisualStudio.Interop;
using Task = System.Threading.Tasks.Task;

namespace Pomp.VisualStudio.Adapters
{
    /// <summary>Identity-bound project removal/rename. Project files are changed only by the Visual Studio project system.</summary>
    internal sealed partial class SolutionAdapter : IProjectItemChangePreparationAdapter
    {
        public async Task<ProjectItemResult> RemoveFileAsync(string path, CancellationToken cancellationToken)
        {
            using var prepared = await PrepareRemoveFileAsync(path, deleteFromDisk: false, cancellationToken).ConfigureAwait(false);
            return await prepared.CommitAsync(cancellationToken).ConfigureAwait(false);
        }

        public async Task<ProjectItemResult> RenameFileAsync(string path, string newFileName, CancellationToken cancellationToken)
        {
            using var prepared = await PrepareRenameFileAsync(path, newFileName, cancellationToken).ConfigureAwait(false);
            return await prepared.CommitAsync(cancellationToken).ConfigureAwait(false);
        }

        public Task<IPreparedProjectItemChange> PrepareRemoveFileAsync(string path, bool deleteFromDisk, CancellationToken cancellationToken) =>
            PrepareItemChangeAsync(path, null, deleteFromDisk, cancellationToken);

        public Task<IPreparedProjectItemChange> PrepareRenameFileAsync(string path, string newFileName, CancellationToken cancellationToken)
        {
            ProjectItemChangeSafety.ValidateFileName(newFileName);
            return PrepareItemChangeAsync(path, newFileName, deleteFromDisk: false, cancellationToken);
        }

        private async Task<IPreparedProjectItemChange> PrepareItemChangeAsync(string path, string? newName, bool deleteFromDisk, CancellationToken cancellationToken)
        {
            await context.Jtf.SwitchToMainThreadAsync(cancellationToken);
            if (disposed || eventsCookie == 0)
            {
                throw new InvalidOperationException("Solution change tracking is not available; project item changes are disabled.");
            }

            var location = SolutionLocation.Resolve(context.Solution);
            if (location == null || location.IsFolderWorkspace)
            {
                throw new ArgumentException("Open a solution with a loaded C# or VB MSBuild project; folder workspaces do not support project item changes.");
            }

            var root = VsAdapterContext.NormalizePath(location.RootDirectory)!;
            var solutionPath = VsAdapterContext.NormalizePath(location.SolutionFile)!;
            var generation = Interlocked.Read(ref solutionGeneration);
            await TaskScheduler.Default;
            cancellationToken.ThrowIfCancellationRequested();
            var source = ProjectItemChangeSafety.ResolveExisting(root, path);

            await context.Jtf.SwitchToMainThreadAsync(cancellationToken);
            RequireItemChangeSolution(root, solutionPath, generation, cancellationToken);
            var matches = FindItemOwners(source, cancellationToken);
            if (matches.Count != 1)
            {
                throw new ArgumentException(matches.Count == 0
                    ? "The file is not an included item of a loaded project: '" + source + "'."
                    : "The file belongs to more than one loaded project; the owning project is ambiguous. Nothing was changed.");
            }

            var match = matches[0];
            RequireItemChangeProject(match.Entry);
            var projectPath = VsAdapterContext.NormalizePath(match.Entry.Path)!;
            var item = RequireSimpleProjectItem(match.Entry.Hierarchy, match.ItemId, source);
            RequireUnchangedDocuments(source, null);
            ErrorHandler.ThrowOnFailure(context.Solution.GetGuidOfProject(match.Entry.Hierarchy, out var projectGuid));
            if (projectGuid == Guid.Empty)
            {
                throw new ArgumentException("The project system did not provide a stable project identity.");
            }

            var hierarchyIdentity = GetHierarchyIdentity(match.Entry.Hierarchy);
            var itemIdentity = GetProjectItemIdentity(item);
            await TaskScheduler.Default;
            cancellationToken.ThrowIfCancellationRequested();
            var newPath = ProjectItemChangeSafety.ValidateProjectPaths(root, projectPath, source, newName);
            ProjectItemChangeSafety.FileIdentity fileIdentity;
            using (var held = ProjectItemChangeSafety.Hold(root, projectPath, source, newName, null, allowDelete: false))
            {
                fileIdentity = held.Identity;
            }

            var plan = new PreparedItemChange(this, match.Entry.Hierarchy, item, match.Entry.Name, projectPath, source, newName, newPath,
                deleteFromDisk, root, solutionPath, generation, projectGuid, hierarchyIdentity, match.ItemId, itemIdentity, fileIdentity);
            try
            {
                await context.Jtf.SwitchToMainThreadAsync(cancellationToken);
                ValidateOriginalItem(plan, match.Entry.Hierarchy, cancellationToken);
                return plan;
            }
            catch
            {
                plan.Dispose();
                throw;
            }
        }

        private void RequireItemChangeSolution(string workspaceRoot, string solutionPath, long generation, CancellationToken cancellationToken)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            cancellationToken.ThrowIfCancellationRequested();
            var location = SolutionLocation.Resolve(context.Solution);
            if (disposed || eventsCookie == 0 || Interlocked.Read(ref solutionGeneration) != generation
                || location == null || location.IsFolderWorkspace || !VsAdapterContext.SamePath(location.RootDirectory, workspaceRoot)
                || !VsAdapterContext.SamePath(location.SolutionFile, solutionPath))
            {
                throw new ArgumentException("The solution changed after preparation; request approval again. Nothing further was changed.");
            }
        }

        private static void RequireItemChangeProject(ProjectEntry entry)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            var extension = Path.GetExtension(entry.Path ?? string.Empty);
            if (entry.IsSolutionFolder || !VsAdapterContext.IsFilePath(entry.Path)
                || (!string.Equals(extension, ".csproj", StringComparison.OrdinalIgnoreCase) && !string.Equals(extension, ".vbproj", StringComparison.OrdinalIgnoreCase))
                || !(entry.Hierarchy is IVsProject) || !(entry.Hierarchy is IVsBuildPropertyStorage))
            {
                throw new ArgumentException("Only loaded C# and VB MSBuild projects support safe project file removal/rename in this build.");
            }
        }

        private List<ProjectItemOwner> FindItemOwners(string path, CancellationToken cancellationToken)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            var matches = new List<ProjectItemOwner>();
            foreach (var entry in EnumerateProjects(cancellationToken))
            {
                if (entry.IsSolutionFolder || !(entry.Hierarchy is IVsProject project)) continue;
                var priority = new VSDOCUMENTPRIORITY[1];
                ErrorHandler.ThrowOnFailure(project.IsDocumentInProject(path, out var found, priority, out var itemId));
                if (found != 0 && itemId != VSConstants.VSITEMID_ROOT && itemId != VSConstants.VSITEMID_NIL
                    && priority[0] != VSDOCUMENTPRIORITY.DP_NonMember
                    && !(HierarchyHelper.GetProperty(entry.Hierarchy, itemId, __VSHPROPID.VSHPROPID_IsNonMemberItem) is bool nonMember && nonMember)
                    && VsAdapterContext.SamePath(HierarchyHelper.GetMkDocument(entry.Hierarchy, itemId), path))
                {
                    matches.Add(new ProjectItemOwner(entry, itemId));
                }
            }

            return matches;
        }

        private IVsProject ValidateItemChangeProject(PreparedItemChange plan, IVsHierarchy hierarchy, CancellationToken cancellationToken)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            RequireItemChangeSolution(plan.WorkspaceRoot, plan.SolutionPath, plan.Generation, cancellationToken);
            var entry = FindAddProject(plan.ProjectPath, cancellationToken);
            RequireItemChangeProject(entry);
            ErrorHandler.ThrowOnFailure(context.Solution.GetGuidOfProject(entry.Hierarchy, out var guid));
            if (guid != plan.ProjectGuid || GetHierarchyIdentity(entry.Hierarchy) != plan.HierarchyIdentity
                || GetHierarchyIdentity(hierarchy) != plan.HierarchyIdentity || !VsAdapterContext.SamePath(entry.Path, plan.ProjectPath)
                || !string.Equals(entry.Name, plan.ProjectName, StringComparison.Ordinal))
            {
                throw new ArgumentException("The approved project was unloaded, replaced, renamed or moved; request approval again. Nothing further was changed.");
            }

            return (IVsProject)entry.Hierarchy;
        }

        private IVsProject ValidateOriginalItem(PreparedItemChange plan, IVsHierarchy hierarchy, CancellationToken cancellationToken)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            var project = ValidateItemChangeProject(plan, hierarchy, cancellationToken);
            var matches = FindItemOwners(plan.Path, cancellationToken);
            if (matches.Count != 1 || matches[0].ItemId != plan.ItemId || GetHierarchyIdentity(matches[0].Entry.Hierarchy) != plan.HierarchyIdentity
                || !VsAdapterContext.SamePath(HierarchyHelper.GetMkDocument(hierarchy, plan.ItemId), plan.Path))
            {
                throw new ArgumentException("The approved item left its project, was replaced or became ambiguous; request approval again. Nothing further was changed.");
            }

            var currentItem = RequireSimpleProjectItem(hierarchy, plan.ItemId, plan.Path);
            if (GetProjectItemIdentity(currentItem) != plan.ItemIdentity)
            {
                throw new ArgumentException("The approved project item automation object was replaced; request approval again. Nothing further was changed.");
            }

            if (plan.NewPath != null && FindItemOwners(plan.NewPath, cancellationToken).Count > 0)
            {
                throw new ArgumentException("The rename destination is already included in a loaded project; nothing was changed.");
            }

            RequireUnchangedDocuments(plan.Path, plan.NewPath);
            return project;
        }

        private static IntPtr GetProjectItemIdentity(ProjectItem item)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            var identity = Marshal.GetIUnknownForObject(item);
            try
            {
                return identity;
            }
            finally
            {
                // Keep the ProjectItem RCW in the plan; release only this temporary native reference.
                Marshal.Release(identity);
            }
        }

        private static ProjectItem RequireSimpleProjectItem(IVsHierarchy hierarchy, uint itemId, string path)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            var type = HierarchyHelper.GetGuid(hierarchy, itemId, __VSHPROPID.VSHPROPID_TypeGuid);
            var child = HierarchyHelper.GetItemId(hierarchy, itemId, __VSHPROPID.VSHPROPID_FirstChild);
            var parent = HierarchyHelper.GetItemId(hierarchy, itemId, __VSHPROPID.VSHPROPID_Parent);
            if (type != VSConstants.GUID_ItemType_PhysicalFile || child != VSConstants.VSITEMID_NIL
                || (parent != VSConstants.VSITEMID_NIL && parent != VSConstants.VSITEMID_ROOT
                    && HierarchyHelper.GetGuid(hierarchy, parent, __VSHPROPID.VSHPROPID_TypeGuid) == VSConstants.GUID_ItemType_PhysicalFile))
            {
                throw new ArgumentException("Only single physical file items are supported; grouped, dependent and virtual project items are refused to avoid collateral changes.");
            }

            if (!(HierarchyHelper.GetProperty(hierarchy, itemId, __VSHPROPID.VSHPROPID_ExtObject) is ProjectItem item)
                || item.FileCount != 1 || !VsAdapterContext.SamePath(item.FileNames[1], path) || (item.ProjectItems != null && item.ProjectItems.Count != 0))
            {
                throw new ArgumentException("The project system did not expose a single-file DTE ProjectItem; grouped or unsupported items cannot be changed safely.");
            }

            return item;
        }

        private void RequireUnchangedDocuments(string path, string? destination)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            if (DocumentTable.IsDirty(context, path) == true)
            {
                throw new ArgumentException("The file has unsaved changes in Visual Studio. Ask the user to save or discard them before preparing a project item change.");
            }

            if (destination != null && DocumentTable.IsDirty(context, destination).HasValue)
            {
                throw new ArgumentException("The rename destination already has a document in Visual Studio; close that document before requesting a new rename approval.");
            }
        }

        private void ValidateItemChangeGeneration(PreparedItemChange plan)
        {
            if (disposed || Interlocked.Read(ref solutionGeneration) != plan.Generation)
            {
                throw new ArgumentException("The solution or project changed after approval; no further disk mutations were made. Request approval again.");
            }
        }

        private async Task<ProjectItemResult> CommitItemChangeAsync(PreparedItemChange plan, IVsHierarchy hierarchy, ProjectItem item, CancellationToken cancellationToken)
        {
            ProjectItemChangeSafety.HeldFile? held = null;
            var attempted = false;
            var removed = false;
            var recycled = false;
            try
            {
                await context.Jtf.SwitchToMainThreadAsync(cancellationToken);
                ValidateOriginalItem(plan, hierarchy, cancellationToken);
                await TaskScheduler.Default;
                cancellationToken.ThrowIfCancellationRequested();
                ValidateItemChangeGeneration(plan);
                // Removal pins against deletion even when recycle was requested: RemoveItem must leave the disk file alone.
                held = ProjectItemChangeSafety.Hold(plan.WorkspaceRoot, plan.ProjectPath, plan.Path, plan.NewName, plan.FileIdentity, allowDelete: plan.NewPath != null);
                await context.Jtf.SwitchToMainThreadAsync(cancellationToken);
                var project = ValidateOriginalItem(plan, hierarchy, cancellationToken);
                // Handle-only metadata checks (no content reads) on the UI thread after the await; writes were denied by Hold.
                held.VerifyUnchanged(plan.Path, plan.FileIdentity);

                attempted = true;
                if (plan.NewPath != null)
                {
                    // No direct disk rename: the project system updates MSBuild items and open-document monikers together.
                    item.Name = plan.NewName!;
                    VerifyProjectRename(plan, hierarchy, item, cancellationToken);
                    await TaskScheduler.Default;
                    ValidateItemChangeGeneration(plan);
                    ProjectItemChangeSafety.VerifyRenamed(plan.WorkspaceRoot, plan.ProjectPath, plan.Path, plan.NewPath, plan.FileIdentity, held);
                    await context.Jtf.SwitchToMainThreadAsync(cancellationToken);
                    VerifyProjectRename(plan, hierarchy, item, cancellationToken);
                    held.VerifyUnchanged(plan.NewPath, plan.FileIdentity);
                    return new ProjectItemResult { Succeeded = true, Path = plan.NewPath };
                }

                if (project is IVsProject2 project2)
                {
                    ErrorHandler.ThrowOnFailure(project2.RemoveItem(0, plan.ItemId, out var result));
                    if (result == 0) throw new InvalidOperationException("The project system cancelled or rejected file removal.");
                }
                else
                {
                    // Remove (not Delete): DTE Delete may permanently delete the source file.
                    item.Remove();
                }

                removed = true;
                VerifyProjectRemoval(plan, hierarchy, cancellationToken);
                await TaskScheduler.Default;
                ValidateItemChangeGeneration(plan);
                ProjectItemChangeSafety.Revalidate(plan.WorkspaceRoot, plan.ProjectPath, plan.Path, null, plan.FileIdentity, held);
                if (plan.DeletesFile)
                {
                    held.AllowDeletion(plan.Path, plan.FileIdentity);
                    // All VS identities/membership are checked again after the disk await, immediately before handing off to the shell.
                    await context.Jtf.SwitchToMainThreadAsync(cancellationToken);
                    VerifyProjectRemoval(plan, hierarchy, cancellationToken);
                    RequireUnchangedDocuments(plan.Path, null);
                    await TaskScheduler.Default;
                    ValidateItemChangeGeneration(plan);
                    await ProjectItemRecycleBin.RecycleAsync(plan.Path, () =>
                    {
                        ValidateItemChangeGeneration(plan);
                        ProjectItemChangeSafety.Revalidate(plan.WorkspaceRoot, plan.ProjectPath, plan.Path, null, plan.FileIdentity, held);
                    }, cancellationToken).ConfigureAwait(false);
                    recycled = true;
                    ValidateItemChangeGeneration(plan);
                    if (ProjectItemChangeSafety.Exists(plan.Path))
                    {
                        throw new IOException("The approved file was recycled, but an entry now exists at the original path; it was left untouched.");
                    }
                }

                await context.Jtf.SwitchToMainThreadAsync(cancellationToken);
                VerifyProjectRemoval(plan, hierarchy, cancellationToken);
                if (!plan.DeletesFile) held.VerifyUnchanged(plan.Path, plan.FileIdentity);
                return new ProjectItemResult { Succeeded = true, Path = plan.Path };
            }
            catch (Exception ex)
            {
                if (!(ex is ArgumentException) && !(ex is OperationCanceledException && cancellationToken.IsCancellationRequested))
                {
                    context.LogUnexpected(plan.NewPath == null ? "SolutionAdapter.RemoveFileAsync" : "SolutionAdapter.RenameFileAsync", ex);
                }

                var reason = ex is OperationCanceledException ? "Project item change was cancelled." : "Project item change failed: " + ex.Message;
                var membership = " Project membership could not be verified.";
                if (attempted)
                {
                    try
                    {
                        await context.Jtf.SwitchToMainThreadAsync(CancellationToken.None);
                        var project = ValidateItemChangeProject(plan, hierarchy, CancellationToken.None);
                        membership = " Project source membership: " + (IsProjectMember(hierarchy, project, plan.Path) ? "included" : "not included") + ".";
                        if (plan.NewPath != null)
                        {
                            membership += " Project destination membership: " + (IsProjectMember(hierarchy, project, plan.NewPath) ? "included" : "not included") + ".";
                        }
                    }
                    catch (Exception)
                    {
                        // A changed/unloaded solution cannot safely be queried through the captured hierarchy.
                    }
                }

                await TaskScheduler.Default;
                var diskState = DescribeItemChangeDisk(plan.Path, plan.NewPath);
                var state = !attempted ? " No project-system mutation was attempted."
                    : plan.NewPath != null ? " A project-system rename was attempted; no automatic rollback was attempted."
                    : " Project removal " + (removed ? "completed" : "was attempted") + (recycled ? "; the approved file was recycled." : "; the file was not confirmed recycled.");
                return new ProjectItemResult
                {
                    Succeeded = false, Path = plan.NewPath ?? plan.Path,
                    Error = reason + state + (attempted ? membership : string.Empty) + diskState + " Inspect the file(s) and project before retrying.",
                };
            }
            finally
            {
                await TaskScheduler.Default;
                held?.Dispose();
            }
        }

        private void VerifyProjectRemoval(PreparedItemChange plan, IVsHierarchy hierarchy, CancellationToken cancellationToken)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            ValidateItemChangeProject(plan, hierarchy, cancellationToken);
            if (FindItemOwners(plan.Path, cancellationToken).Count != 0)
            {
                throw new InvalidOperationException("The project system did not confirm removal; the source is still included in a loaded project.");
            }
        }

        private void VerifyProjectRename(PreparedItemChange plan, IVsHierarchy hierarchy, ProjectItem item, CancellationToken cancellationToken)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            ValidateItemChangeProject(plan, hierarchy, cancellationToken);
            var matches = FindItemOwners(plan.NewPath!, cancellationToken);
            if (matches.Count != 1 || GetHierarchyIdentity(matches[0].Entry.Hierarchy) != plan.HierarchyIdentity
                || FindItemOwners(plan.Path, cancellationToken).Count != 0 || GetProjectItemIdentity(item) != plan.ItemIdentity
                || GetProjectItemIdentity(RequireSimpleProjectItem(hierarchy, matches[0].ItemId, plan.NewPath!)) != plan.ItemIdentity
                || item.FileCount != 1 || !VsAdapterContext.SamePath(item.FileNames[1], plan.NewPath))
            {
                throw new InvalidOperationException("The project system did not confirm the rename at its approved destination; disk and project names may differ.");
            }
        }

        private static string DescribeItemChangeDisk(string path, string? newPath)
        {
            try
            {
                return " Disk source: " + (ProjectItemChangeSafety.Exists(path) ? "exists" : "missing") + " ('" + path + "')."
                    + (newPath == null ? string.Empty : " Disk destination: " + (ProjectItemChangeSafety.Exists(newPath) ? "exists" : "missing") + " ('" + newPath + "').");
            }
            catch (Exception)
            {
                return " Disk state could not be verified.";
            }
        }

        private sealed class ProjectItemOwner
        {
            public ProjectItemOwner(ProjectEntry entry, uint itemId) { Entry = entry; ItemId = itemId; }
            public ProjectEntry Entry { get; }
            public uint ItemId { get; }
        }

        private sealed class PreparedItemChange : IPreparedProjectItemChange
        {
            private readonly object gate = new object();
            private SolutionAdapter? owner;
            private IVsHierarchy? hierarchy;
            private ProjectItem? item;
            private bool committed;

            public PreparedItemChange(SolutionAdapter owner, IVsHierarchy hierarchy, ProjectItem item, string projectName, string projectPath,
                string path, string? newName, string? newPath, bool deletesFile, string workspaceRoot, string solutionPath,
                long generation, Guid projectGuid, IntPtr hierarchyIdentity, uint itemId, IntPtr itemIdentity, ProjectItemChangeSafety.FileIdentity fileIdentity)
            {
                this.owner = owner;
                this.hierarchy = hierarchy;
                this.item = item;
                ProjectName = projectName; ProjectPath = projectPath; Path = path; NewName = newName; NewPath = newPath; DeletesFile = deletesFile;
                WorkspaceRoot = workspaceRoot; SolutionPath = solutionPath; Generation = generation; ProjectGuid = projectGuid;
                HierarchyIdentity = hierarchyIdentity; ItemId = itemId; ItemIdentity = itemIdentity; FileIdentity = fileIdentity;
            }

            public string ProjectName { get; }
            public string ProjectPath { get; }
            public string Path { get; }
            public string? NewPath { get; }
            public bool DeletesFile { get; }
            public string? NewName { get; }
            public string WorkspaceRoot { get; }
            public string SolutionPath { get; }
            public long Generation { get; }
            public Guid ProjectGuid { get; }
            public IntPtr HierarchyIdentity { get; }
            public uint ItemId { get; }
            public IntPtr ItemIdentity { get; }
            public ProjectItemChangeSafety.FileIdentity FileIdentity { get; }

            public Task<ProjectItemResult> CommitAsync(CancellationToken cancellationToken)
            {
                lock (gate)
                {
                    if (owner == null || hierarchy == null || item == null || committed)
                    {
                        return Task.FromResult(new ProjectItemResult { Succeeded = false, Path = NewPath ?? Path,
                            Error = "The prepared project item change is disposed or was already executed; request new approval." });
                    }

                    committed = true;
                    return owner.CommitItemChangeAsync(this, hierarchy, item, cancellationToken);
                }
            }

            public void Dispose()
            {
                lock (gate)
                {
                    owner = null; hierarchy = null; item = null;
                }
            }
        }
    }
}
