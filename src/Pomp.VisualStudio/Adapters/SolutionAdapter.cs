using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.VisualStudio;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Threading;
using Microsoft.VisualStudio.Shell.Interop;
using Pomp.Diagnostics;
using Pomp.Adapters;
using Pomp.VisualStudio.Interop;
using Task = System.Threading.Tasks.Task;

namespace Pomp.VisualStudio.Adapters
{
    /// <summary>
    /// Solution/project graph (design: SolutionAdapter). Reads go through <see cref="IVsHierarchy"/> (fast, works for legacy
    /// and SDK-style projects alike). New files are created without overwrite and registered through the project system.
    /// </summary>
    internal sealed partial class SolutionAdapter : ISolutionAdapter, IProjectFilePreparationAdapter, IDisposable
    {
        private static readonly Guid SolutionFolderTypeGuid = new Guid("66A26720-8FB5-11D2-AA7E-00C04F688DDE");

        private const int MaxVisitedNodes = 50000;

        private readonly VsAdapterContext context;
        private SolutionEventSink? sink;
        private uint eventsCookie;
        private long solutionGeneration;
        private volatile bool disposed;

        public SolutionAdapter(VsAdapterContext context)
        {
            this.context = context;
        }

        /// <inheritdoc />
        public event EventHandler? SolutionChanged;

        /// <summary>Starts listening for solution/folder open and close (keeps the sink referenced for the COM connection).</summary>
        public async Task InitializeAsync(CancellationToken cancellationToken)
        {
            await context.Jtf.SwitchToMainThreadAsync(cancellationToken);

            sink = new SolutionEventSink(this);
            ErrorHandler.ThrowOnFailure(context.Solution.AdviseSolutionEvents(sink, out eventsCookie));
        }

        public void Dispose()
        {
            disposed = true;
            Interlocked.Increment(ref solutionGeneration);
            var cookie = eventsCookie;
            eventsCookie = 0;
            sink = null;
            if (cookie == 0)
            {
                return;
            }

            _ = context.Jtf.RunAsync(() => UnadviseAsync(cookie));
        }

        private async Task UnadviseAsync(uint cookie)
        {
            try
            {
                await context.Jtf.SwitchToMainThreadAsync();
                context.Solution.UnadviseSolutionEvents(cookie);
            }
            catch (Exception)
            {
                // Shutting down.
            }
        }

        /// <summary>
        /// Name, solution file and root only: one <c>IVsSolution.GetSolutionInfo</c> call on the UI thread and string handling,
        /// no project enumeration, no property reads and no file-system access. Cheap enough for the 2-second state poll.
        /// </summary>
        public async Task<SolutionInfo?> GetSolutionLocationAsync(CancellationToken cancellationToken)
        {
            await context.Jtf.SwitchToMainThreadAsync(cancellationToken);

            var location = SolutionLocation.Resolve(context.Solution);
            return location == null ? null : CreateInfo(location);
        }

        public async Task<SolutionInfo?> GetSolutionInfoAsync(CancellationToken cancellationToken)
        {
            await context.Jtf.SwitchToMainThreadAsync(cancellationToken);

            var location = SolutionLocation.Resolve(context.Solution);
            if (location == null)
            {
                return null;
            }

            var info = CreateInfo(location);

            foreach (var entry in EnumerateProjects(cancellationToken))
            {
                info.Projects.Add(new ProjectInfo
                {
                    Name = entry.Name,
                    Path = entry.Path,
                    Kind = entry.Kind,
                    TargetFrameworks = entry.IsSolutionFolder ? null : ReadTargetFrameworks(entry.Hierarchy),
                });
            }

            return info;
        }

        private static SolutionInfo CreateInfo(SolutionLocation location)
        {
            return new SolutionInfo
            {
                Name = location.Name,
                SolutionPath = location.SolutionFile,
                RootDirectory = location.RootDirectory,
            };
        }

        public async Task<IReadOnlyList<SolutionNode>> QueryAsync(SolutionQuery query, CancellationToken cancellationToken)
        {
            query ??= new SolutionQuery();
            await context.Jtf.SwitchToMainThreadAsync(cancellationToken);

            var location = SolutionLocation.Resolve(context.Solution);
            if (location == null)
            {
                return Array.Empty<SolutionNode>();
            }

            var depth = Math.Max(1, query.Depth);
            var matcher = NameMatcher.Create(query.Pattern);

            var root = new SolutionNode
            {
                Name = location.Name,
                Kind = location.IsFolderWorkspace ? "folder" : "solution",
                Path = location.SolutionFile ?? location.RootDirectory,
                Children = new List<SolutionNode>(),
            };

            var visited = 0;
            if (location.IsFolderWorkspace)
            {
                // "Open folder" workspaces have no project hierarchy; walk the directory instead.
                await TaskScheduler.Default;
                AddDirectoryChildren(location.RootDirectory, root, depth, ref visited, cancellationToken);
            }
            else
            {
                BuildSolutionTree(root, depth, ref visited, cancellationToken);
            }

            if (matcher != null)
            {
                Prune(root, matcher);
            }

            var remaining = Math.Max(1, query.MaxResults);
            Limit(root, ref remaining);
            return new[] { root };
        }

        public async Task<ProjectItemResult> AddFileAsync(string projectName, string relativePath, string? initialContent, CancellationToken cancellationToken)
        {
            using var prepared = await PrepareAddFileAsync(projectName, relativePath, initialContent, cancellationToken).ConfigureAwait(false);
            return await prepared.CommitAsync(cancellationToken).ConfigureAwait(false);
        }

        public async Task<IPreparedProjectFile> PrepareAddFileAsync(string projectName, string relativePath, string? initialContent, CancellationToken cancellationToken)
        {
            if (string.IsNullOrWhiteSpace(projectName))
            {
                throw new ArgumentException("A target project name or full project path is required.");
            }

            await context.Jtf.SwitchToMainThreadAsync(cancellationToken);
            if (disposed || eventsCookie == 0)
            {
                throw new InvalidOperationException("Solution change tracking is not available; project additions are disabled.");
            }

            var location = SolutionLocation.Resolve(context.Solution);
            if (location == null || location.IsFolderWorkspace)
            {
                throw new ArgumentException("Open a solution with a loaded C# or VB project; folder workspaces are not supported for project additions.");
            }

            var entry = FindAddProject(projectName, cancellationToken);
            RequireAddProject(entry);
            ErrorHandler.ThrowOnFailure(context.Solution.GetGuidOfProject(entry.Hierarchy, out var projectGuid));
            if (projectGuid == Guid.Empty)
            {
                throw new ArgumentException("The project system did not provide a stable project identity.");
            }

            var projectPath = VsAdapterContext.NormalizePath(entry.Path)!;
            var root = VsAdapterContext.NormalizePath(location.RootDirectory)!;
            var generation = Interlocked.Read(ref solutionGeneration);
            var hierarchyIdentity = GetHierarchyIdentity(entry.Hierarchy);
            var content = initialContent ?? string.Empty;

            await TaskScheduler.Default;
            cancellationToken.ThrowIfCancellationRequested();
            var bytes = ProjectFileCreation.Encode(content);
            var target = ProjectFileCreation.Validate(root, projectPath, relativePath, requireMissing: true);
            var plan = new PreparedProjectFile(this, entry.Name, projectPath, relativePath, target, content, bytes,
                root, location.SolutionFile!, generation, projectGuid, hierarchyIdentity, entry.Hierarchy);
            try
            {
                await context.Jtf.SwitchToMainThreadAsync(cancellationToken);
                var project = ValidateAddProject(plan, entry.Hierarchy, cancellationToken);
                if (IsProjectMember(entry.Hierarchy, project, target))
                {
                    throw new ArgumentException("The target is already included in the project, even though its disk file is missing: '" + target + "'.");
                }

                return plan;
            }
            catch
            {
                plan.Dispose();
                throw;
            }
        }

        private ProjectEntry FindAddProject(string selector, CancellationToken cancellationToken)
        {
            ThreadHelper.ThrowIfNotOnUIThread();

            var projects = EnumerateProjects(cancellationToken);
            var matches = projects.Where(p =>
                string.Equals(p.Name, selector, StringComparison.OrdinalIgnoreCase)
                || (p.Path != null && (VsAdapterContext.SamePath(p.Path, selector)
                    || string.Equals(Path.GetFileNameWithoutExtension(p.Path), selector, StringComparison.OrdinalIgnoreCase))))
                .ToList();
            if (matches.Count == 0)
            {
                throw new ArgumentException("No loaded project matches '" + selector + "'.");
            }

            if (matches.Count > 1)
            {
                throw new ArgumentException("The project name '" + selector + "' is ambiguous; use the full project file path from query_solution.");
            }

            return matches[0];
        }

        private static void RequireAddProject(ProjectEntry entry)
        {
            ThreadHelper.ThrowIfNotOnUIThread();

            var extension = Path.GetExtension(entry.Path ?? string.Empty);
            if (entry.IsSolutionFolder
                || !VsAdapterContext.IsFilePath(entry.Path)
                || (!string.Equals(extension, ".csproj", StringComparison.OrdinalIgnoreCase)
                    && !string.Equals(extension, ".vbproj", StringComparison.OrdinalIgnoreCase))
                || !(entry.Hierarchy is IVsProject)
                || !(entry.Hierarchy is IVsBuildPropertyStorage))
            {
                throw new ArgumentException("This project type does not support safe file additions in this build; only loaded C# and VB MSBuild projects are supported.");
            }
        }

        private IVsProject ValidateAddProject(PreparedProjectFile plan, IVsHierarchy hierarchy, CancellationToken cancellationToken)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            cancellationToken.ThrowIfCancellationRequested();

            var location = SolutionLocation.Resolve(context.Solution);
            if (disposed || eventsCookie == 0 || Interlocked.Read(ref solutionGeneration) != plan.Generation
                || location == null || location.IsFolderWorkspace
                || !VsAdapterContext.SamePath(location.RootDirectory, plan.WorkspaceRoot)
                || !VsAdapterContext.SamePath(location.SolutionFile, plan.SolutionPath))
            {
                throw new ArgumentException("The solution changed after preparation; request approval again. Nothing further was changed.");
            }

            var entry = FindAddProject(plan.ProjectPath, cancellationToken);
            RequireAddProject(entry);
            ErrorHandler.ThrowOnFailure(context.Solution.GetGuidOfProject(entry.Hierarchy, out var projectGuid));
            if (projectGuid != plan.ProjectGuid || GetHierarchyIdentity(entry.Hierarchy) != plan.HierarchyIdentity
                || GetHierarchyIdentity(hierarchy) != plan.HierarchyIdentity
                || !VsAdapterContext.SamePath(entry.Path, plan.ProjectPath)
                || !string.Equals(entry.Name, plan.ProjectName, StringComparison.Ordinal))
            {
                throw new ArgumentException("The approved project was unloaded, replaced, renamed or moved; request approval again. Nothing further was changed.");
            }

            return (IVsProject)entry.Hierarchy;
        }

        private void ValidateAddGeneration(PreparedProjectFile plan)
        {
            if (disposed || Interlocked.Read(ref solutionGeneration) != plan.Generation)
            {
                throw new ArgumentException("The solution or project changed after approval; no further disk mutations were made. Request approval again.");
            }
        }

        private static IntPtr GetHierarchyIdentity(IVsHierarchy hierarchy)
        {
            ThreadHelper.ThrowIfNotOnUIThread();

            var identity = Marshal.GetIUnknownForObject(hierarchy);
            try
            {
                return identity;
            }
            finally
            {
                // The plan retains the RCW, not this temporary unmanaged reference.
                Marshal.Release(identity);
            }
        }

        private static bool IsProjectMember(IVsHierarchy hierarchy, IVsProject project, string target)
        {
            ThreadHelper.ThrowIfNotOnUIThread();

            var priority = new VSDOCUMENTPRIORITY[1];
            ErrorHandler.ThrowOnFailure(project.IsDocumentInProject(target, out var found, priority, out var item));
            return found != 0 && item != VSConstants.VSITEMID_NIL && item != VSConstants.VSITEMID_ROOT
                && VsAdapterContext.SamePath(HierarchyHelper.GetMkDocument(hierarchy, item), target)
                && priority[0] != VSDOCUMENTPRIORITY.DP_NonMember
                && !(HierarchyHelper.GetProperty(hierarchy, item, __VSHPROPID.VSHPROPID_IsNonMemberItem) is bool nonMember && nonMember);
        }

        private async Task<ProjectItemResult> CommitAddFileAsync(PreparedProjectFile plan, IVsHierarchy hierarchy, CancellationToken cancellationToken)
        {
            var state = new ProjectFileCreation.CreationState();
            try
            {
                await context.Jtf.SwitchToMainThreadAsync(cancellationToken);
                var project = ValidateAddProject(plan, hierarchy, cancellationToken);
                if (IsProjectMember(hierarchy, project, plan.Path))
                {
                    throw new ArgumentException("The target is already included in the project; nothing was created.");
                }

                await TaskScheduler.Default;
                ProjectFileCreation.Validate(plan.WorkspaceRoot, plan.ProjectPath, plan.RelativePath, requireMissing: true);
                await context.Jtf.SwitchToMainThreadAsync(cancellationToken);
                ValidateAddProject(plan, hierarchy, cancellationToken);
                await TaskScheduler.Default;
                await ProjectFileCreation.CreateAsync(plan.WorkspaceRoot, plan.ProjectPath, plan.RelativePath,
                    plan.Path, plan.Bytes, state, () => ValidateAddGeneration(plan), cancellationToken).ConfigureAwait(false);

                await context.Jtf.SwitchToMainThreadAsync(cancellationToken);
                project = ValidateAddProject(plan, hierarchy, cancellationToken);
                // CPS may have already included the file via its globs; AddItem otherwise handles explicit includes.
                if (!IsProjectMember(hierarchy, project, plan.Path))
                {
                    var added = new[] { VSADDRESULT.ADDRESULT_Failure };
                    // Link the already-created path: OPENFILE may copy a subfolder file into the project root.
                    var hr = project.AddItem(VSConstants.VSITEMID_ROOT, VSADDITEMOPERATION.VSADDITEMOP_LINKTOFILE,
                        plan.RelativePath, 1, new[] { plan.Path }, IntPtr.Zero, added);
                    ErrorHandler.ThrowOnFailure(hr);
                    if (added[0] != VSADDRESULT.ADDRESULT_Success)
                    {
                        throw new InvalidOperationException(added[0] == VSADDRESULT.ADDRESULT_Cancel
                            ? "The project system cancelled file registration."
                            : "The project system rejected file registration.");
                    }
                }

                ValidateAddProject(plan, hierarchy, cancellationToken);
                if (!IsProjectMember(hierarchy, project, plan.Path))
                {
                    throw new InvalidOperationException("The project system did not confirm the new file at its approved path.");
                }

                await TaskScheduler.Default;
                ProjectFileCreation.Validate(plan.WorkspaceRoot, plan.ProjectPath, plan.RelativePath, requireMissing: false);
                await context.Jtf.SwitchToMainThreadAsync(cancellationToken);
                project = ValidateAddProject(plan, hierarchy, cancellationToken);

                // Checked again after the last await: the success reported must still be true when it is reported.
                if (!IsProjectMember(hierarchy, project, plan.Path))
                {
                    throw new InvalidOperationException("The new file left the project while its registration was being confirmed.");
                }

                return new ProjectItemResult { Succeeded = true, Path = plan.Path };
            }
            catch (Exception ex)
            {
                if (!(ex is ArgumentException) && !(ex is OperationCanceledException && cancellationToken.IsCancellationRequested))
                {
                    context.LogUnexpected("SolutionAdapter.AddFileAsync", ex);
                }

                var reason = ex is OperationCanceledException ? "File addition was cancelled."
                    : "File addition failed: " + ex.Message;
                return new ProjectItemResult { Succeeded = false, Path = plan.Path, Error = reason + state.DescribeLeftovers(plan.Path) };
            }
            finally
            {
                await TaskScheduler.Default;
                try
                {
                    state.Dispose();
                }
                catch (Exception ex)
                {
                    context.LogUnexpected("SolutionAdapter.AddFileAsync.Close", ex);
                }
            }
        }

        private sealed class PreparedProjectFile : IPreparedProjectFile
        {
            private readonly object gate = new object();
            private SolutionAdapter? owner;
            private IVsHierarchy? hierarchy;
            private bool committed;

            public PreparedProjectFile(SolutionAdapter owner, string projectName, string projectPath, string relativePath,
                string path, string content, byte[] bytes, string workspaceRoot, string solutionPath,
                long generation, Guid projectGuid, IntPtr hierarchyIdentity, IVsHierarchy hierarchy)
            {
                this.owner = owner;
                this.hierarchy = hierarchy;
                ProjectName = projectName;
                ProjectPath = projectPath;
                RelativePath = relativePath;
                Path = path;
                Content = content;
                Bytes = bytes;
                WorkspaceRoot = workspaceRoot;
                SolutionPath = solutionPath;
                Generation = generation;
                ProjectGuid = projectGuid;
                HierarchyIdentity = hierarchyIdentity;
            }

            public string ProjectName { get; }
            public string ProjectPath { get; }
            public string Path { get; }
            public string Content { get; }
            public string RelativePath { get; }
            public byte[] Bytes { get; }
            public string WorkspaceRoot { get; }
            public string SolutionPath { get; }
            public long Generation { get; }
            public Guid ProjectGuid { get; }
            public IntPtr HierarchyIdentity { get; }

            public Task<ProjectItemResult> CommitAsync(CancellationToken cancellationToken)
            {
                lock (gate)
                {
                    if (owner == null || hierarchy == null || committed)
                    {
                        return Task.FromResult(new ProjectItemResult { Succeeded = false, Path = Path,
                            Error = "The prepared file addition is disposed or was already executed; request new approval." });
                    }

                    committed = true;
                    return owner.CommitAddFileAsync(this, hierarchy, cancellationToken);
                }
            }

            public void Dispose()
            {
                lock (gate)
                {
                    owner = null;
                    hierarchy = null;
                }
            }
        }

        // RemoveFileAsync/RenameFileAsync: SolutionAdapter.ProjectItems.cs. ListPackagesAsync: SolutionAdapter.Packages.cs.

        /// <remarks>
        /// Values are what the project system reports through IVsBuildPropertyStorage (evaluated for MSBuild based projects); missing
        /// properties map to null. An unknown or ambiguous project, or one without MSBuild properties, is an <see cref="ArgumentException"/>.
        /// </remarks>
        public async Task<IReadOnlyDictionary<string, string?>> GetMsBuildPropertiesAsync(string projectName, IReadOnlyList<string> propertyNames, CancellationToken cancellationToken)
        {
            await context.Jtf.SwitchToMainThreadAsync(cancellationToken);

            var entry = FindUniqueProject(projectName, cancellationToken);
            if (!(entry.Hierarchy is IVsBuildPropertyStorage))
            {
                throw new ArgumentException("Project '" + entry.Name + "' does not expose MSBuild properties.");
            }

            var result = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
            foreach (var name in propertyNames ?? Array.Empty<string>())
            {
                result[name] = ReadProperty(entry.Hierarchy, name);
            }

            return result;
        }

        /// <summary>
        /// The one loaded project (not a solution folder) whose name, file name or full path is <paramref name="selector"/>; none or
        /// several matches are an <see cref="ArgumentException"/> (duplicate names need the full project path from query_solution).
        /// </summary>
        private ProjectEntry FindUniqueProject(string selector, CancellationToken cancellationToken)
        {
            ThreadHelper.ThrowIfNotOnUIThread();

            var matches = EnumerateProjects(cancellationToken)
                .Where(p => !p.IsSolutionFolder
                    && (string.Equals(p.Name, selector, StringComparison.OrdinalIgnoreCase)
                        || (p.Path != null && (VsAdapterContext.SamePath(p.Path, selector)
                            || string.Equals(Path.GetFileNameWithoutExtension(p.Path), selector, StringComparison.OrdinalIgnoreCase)))))
                .ToList();
            if (matches.Count == 0)
            {
                throw new ArgumentException("No loaded project matches '" + selector + "'.");
            }

            if (matches.Count > 1)
            {
                throw new ArgumentException("The project name '" + selector + "' is ambiguous; use the full project file path from query_solution.");
            }

            return matches[0];
        }

        // ---- solution enumeration --------------------------------------------------------------------

        private List<ProjectEntry> EnumerateProjects(CancellationToken cancellationToken)
        {
            ThreadHelper.ThrowIfNotOnUIThread();

            var entries = new List<ProjectEntry>();
            var none = Guid.Empty;
            if (ErrorHandler.Failed(context.Solution.GetProjectEnum((uint)__VSENUMPROJFLAGS.EPF_LOADEDINSOLUTION, ref none, out var enumeration))
                || enumeration == null)
            {
                return entries;
            }

            var buffer = new IVsHierarchy[1];
            while (enumeration.Next(1, buffer, out var fetched) == VSConstants.S_OK && fetched == 1)
            {
                cancellationToken.ThrowIfCancellationRequested();

                var hierarchy = buffer[0];
                if (hierarchy == null)
                {
                    continue;
                }

                var name = HierarchyHelper.GetString(hierarchy, VSConstants.VSITEMID_ROOT, __VSHPROPID.VSHPROPID_Name);
                if (string.IsNullOrEmpty(name))
                {
                    continue;
                }

                var typeGuid = HierarchyHelper.GetGuid(hierarchy, VSConstants.VSITEMID_ROOT, __VSHPROPID.VSHPROPID_TypeGuid);
                var isFolder = typeGuid == SolutionFolderTypeGuid;
                var path = isFolder ? null : HierarchyHelper.GetMkDocument(hierarchy, VSConstants.VSITEMID_ROOT);
                entries.Add(new ProjectEntry(hierarchy, name!, path, isFolder ? "Solution Folder" : DescribeKind(path, typeGuid), isFolder));
            }

            return entries;
        }

        private void BuildSolutionTree(SolutionNode root, int depth, ref int visited, CancellationToken cancellationToken)
        {
            ThreadHelper.ThrowIfNotOnUIThread();

            var entries = EnumerateProjects(cancellationToken);
            var nodes = new Dictionary<IVsHierarchy, SolutionNode>(ReferenceComparer<IVsHierarchy>.Instance);

            foreach (var entry in entries)
            {
                nodes[entry.Hierarchy] = new SolutionNode
                {
                    Name = entry.Name,
                    Kind = entry.IsSolutionFolder ? "folder" : "project",
                    Path = entry.Path,
                    ProjectName = entry.IsSolutionFolder ? null : entry.Name,
                    Children = new List<SolutionNode>(),
                };
            }

            // Link solution folders and their nested projects (parent hierarchy), everything else hangs off the solution.
            foreach (var entry in entries)
            {
                var node = nodes[entry.Hierarchy];
                var parent = HierarchyHelper.GetProperty(entry.Hierarchy, VSConstants.VSITEMID_ROOT, __VSHPROPID.VSHPROPID_ParentHierarchy) as IVsHierarchy;
                if (parent != null && nodes.TryGetValue(parent, out var parentNode))
                {
                    parentNode.Children!.Add(node);
                }
                else
                {
                    root.Children!.Add(node);
                }
            }

            // Project contents, honoring the depth below the solution node (1 = projects only).
            foreach (var entry in entries.Where(e => !e.IsSolutionFolder))
            {
                var levelOfProject = NestingLevel(entry.Hierarchy, nodes);
                var remainingDepth = depth - levelOfProject;
                if (remainingDepth > 0)
                {
                    AddItems(entry.Hierarchy, VSConstants.VSITEMID_ROOT, nodes[entry.Hierarchy], entry.Name, remainingDepth, ref visited, cancellationToken);
                }
            }

            TrimDepth(root, depth);
        }

        /// <summary>Depth of a project below the solution node: 1 for a top-level project, +1 per enclosing solution folder.</summary>
        private static int NestingLevel(IVsHierarchy hierarchy, Dictionary<IVsHierarchy, SolutionNode> nodes)
        {
            ThreadHelper.ThrowIfNotOnUIThread();

            var level = 1;
            var current = hierarchy;
            for (var guard = 0; guard < 32; guard++)
            {
                var parent = HierarchyHelper.GetProperty(current, VSConstants.VSITEMID_ROOT, __VSHPROPID.VSHPROPID_ParentHierarchy) as IVsHierarchy;
                if (parent == null || !nodes.ContainsKey(parent))
                {
                    break;
                }

                level++;
                current = parent;
            }

            return level;
        }

        private static void TrimDepth(SolutionNode node, int depthLeft)
        {
            if (node.Children == null)
            {
                return;
            }

            if (depthLeft <= 1)
            {
                // Children of this node would be at depth + 1.
                foreach (var child in node.Children)
                {
                    child.Children = null;
                }

                return;
            }

            foreach (var child in node.Children)
            {
                TrimDepth(child, depthLeft - 1);
            }
        }

        private void AddItems(IVsHierarchy hierarchy, uint parentItemId, SolutionNode parentNode, string projectName, int depthLeft, ref int visited, CancellationToken cancellationToken)
        {
            ThreadHelper.ThrowIfNotOnUIThread();

            if (depthLeft <= 0)
            {
                return;
            }

            for (var item = HierarchyHelper.GetItemId(hierarchy, parentItemId, __VSHPROPID.VSHPROPID_FirstVisibleChild);
                 item != VSConstants.VSITEMID_NIL;
                 item = HierarchyHelper.GetItemId(hierarchy, item, __VSHPROPID.VSHPROPID_NextVisibleSibling))
            {
                if (++visited > MaxVisitedNodes)
                {
                    return;
                }

                if ((visited & 0xFF) == 0)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                }

                var kind = ClassifyItem(hierarchy, item);
                if (kind == null)
                {
                    continue;
                }

                var name = HierarchyHelper.GetString(hierarchy, item, __VSHPROPID.VSHPROPID_Name);
                if (string.IsNullOrEmpty(name))
                {
                    continue;
                }

                var node = new SolutionNode
                {
                    Name = name!,
                    Kind = kind,
                    Path = HierarchyHelper.GetMkDocument(hierarchy, item),
                    ProjectName = projectName,
                    Children = new List<SolutionNode>(),
                };

                parentNode.Children!.Add(node);
                AddItems(hierarchy, item, node, projectName, depthLeft - 1, ref visited, cancellationToken);
            }
        }

        /// <summary>"file" / "folder" for physical items; null for virtual nodes (Dependencies, References...) that are not part of the source tree.</summary>
        private static string? ClassifyItem(IVsHierarchy hierarchy, uint item)
        {
            ThreadHelper.ThrowIfNotOnUIThread();

            var typeGuid = HierarchyHelper.GetGuid(hierarchy, item, __VSHPROPID.VSHPROPID_TypeGuid);
            if (typeGuid == VSConstants.GUID_ItemType_PhysicalFile)
            {
                return "file";
            }

            if (typeGuid == VSConstants.GUID_ItemType_PhysicalFolder)
            {
                return "folder";
            }

            return null;
        }

        private static string DescribeKind(string? path, Guid? typeGuid)
        {
            var extension = string.IsNullOrEmpty(path) ? string.Empty : Path.GetExtension(path).ToLowerInvariant();
            switch (extension)
            {
                case ".csproj":
                    return "C#";
                case ".vbproj":
                    return "VB";
                case ".fsproj":
                    return "F#";
                case ".vcxproj":
                case ".vcproj":
                    return "C++";
                case ".shproj":
                    return "Shared Project";
                case ".wapproj":
                    return "Packaging";
                case ".sqlproj":
                    return "SQL";
                case ".esproj":
                case ".njsproj":
                case ".jsproj":
                    return "JavaScript";
                case ".pyproj":
                    return "Python";
                case ".dcproj":
                    return "Docker Compose";
                default:
                    return extension.Length > 1 ? extension.Substring(1) : (typeGuid?.ToString("B") ?? "Project");
            }
        }

        // ---- folder workspaces -----------------------------------------------------------------------

        private static readonly HashSet<string> IgnoredDirectoryNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            ".git", ".vs", ".idea", "node_modules", "bin", "obj", ".agents", ".agent",
        };

        private static void AddDirectoryChildren(string directory, SolutionNode parent, int depthLeft, ref int visited, CancellationToken cancellationToken)
        {
            if (depthLeft <= 0)
            {
                return;
            }

            IEnumerable<string> entries;
            try
            {
                entries = Directory.EnumerateFileSystemEntries(directory).OrderBy(e => e, StringComparer.OrdinalIgnoreCase).ToList();
            }
            catch (Exception ex) when (ex is UnauthorizedAccessException || ex is IOException)
            {
                return;
            }

            foreach (var entry in entries.Where(e => Directory.Exists(e)).Concat(entries.Where(e => !Directory.Exists(e))))
            {
                if (++visited > MaxVisitedNodes)
                {
                    return;
                }

                cancellationToken.ThrowIfCancellationRequested();

                var name = Path.GetFileName(entry);
                var isDirectory = Directory.Exists(entry);
                // .pomp/local is POMP's own data (conversations, checkpoints, work trees), not part of the project.
                if (isDirectory && (IgnoredDirectoryNames.Contains(name) || (string.Equals(name, "local", StringComparison.OrdinalIgnoreCase) && string.Equals(Path.GetFileName(directory), ".pomp", StringComparison.OrdinalIgnoreCase))))
                {
                    continue;
                }

                var node = new SolutionNode
                {
                    Name = name,
                    Kind = isDirectory ? "folder" : "file",
                    Path = entry,
                    Children = new List<SolutionNode>(),
                };
                parent.Children!.Add(node);

                // Show the link itself, but never walk through junctions/symlinks. They may
                // escape the workspace or form a cycle even when the requested depth is bounded.
                if (isDirectory && WorkspaceDirectoryPolicy.CanTraverse(entry))
                {
                    AddDirectoryChildren(entry, node, depthLeft - 1, ref visited, cancellationToken);
                }
            }
        }

        // ---- pattern filtering and limiting ----------------------------------------------------------

        private static bool Prune(SolutionNode node, NameMatcher matcher)
        {
            var selfMatches = node.Kind != "solution" && matcher.IsMatch(node.Name, node.Path);
            var keptChildren = new List<SolutionNode>();
            if (node.Children != null)
            {
                foreach (var child in node.Children)
                {
                    if (Prune(child, matcher))
                    {
                        keptChildren.Add(child);
                    }
                }
            }

            node.Children = node.Children == null ? null : keptChildren;
            return selfMatches || keptChildren.Count > 0 || node.Kind == "solution";
        }

        /// <summary>Keeps at most <paramref name="remaining"/> nodes below the root (pre-order); the root itself is free.</summary>
        private static void Limit(SolutionNode node, ref int remaining)
        {
            if (node.Children == null)
            {
                return;
            }

            var kept = new List<SolutionNode>();
            foreach (var child in node.Children)
            {
                if (remaining <= 0)
                {
                    break;
                }

                remaining--;
                kept.Add(child);
                Limit(child, ref remaining);
            }

            node.Children = kept;
        }

        // ---- properties ------------------------------------------------------------------------------

        private ProjectEntry? FindProject(string? projectName, CancellationToken cancellationToken)
        {
            ThreadHelper.ThrowIfNotOnUIThread();

            var projects = EnumerateProjects(cancellationToken).Where(p => !p.IsSolutionFolder).ToList();
            if (string.IsNullOrWhiteSpace(projectName))
            {
                return projects.FirstOrDefault();
            }

            return projects.FirstOrDefault(p => string.Equals(p.Name, projectName, StringComparison.OrdinalIgnoreCase))
                   ?? projects.FirstOrDefault(p => p.Path != null && string.Equals(Path.GetFileNameWithoutExtension(p.Path), projectName, StringComparison.OrdinalIgnoreCase))
                   ?? projects.FirstOrDefault(p => p.Path != null && VsAdapterContext.SamePath(p.Path, projectName));
        }

        private static string? ReadProperty(IVsHierarchy hierarchy, string name)
        {
            ThreadHelper.ThrowIfNotOnUIThread();

            if (hierarchy is IVsBuildPropertyStorage storage
                && ErrorHandler.Succeeded(storage.GetPropertyValue(name, null, (uint)_PersistStorageType.PST_PROJECT_FILE, out var value))
                && !string.IsNullOrEmpty(value))
            {
                return value;
            }

            return null;
        }

        private static string? ReadTargetFrameworks(IVsHierarchy hierarchy)
        {
            ThreadHelper.ThrowIfNotOnUIThread();

            return ReadProperty(hierarchy, "TargetFrameworks")
                   ?? ReadProperty(hierarchy, "TargetFramework")
                   ?? ReadProperty(hierarchy, "TargetFrameworkVersion");
        }

        // ---- events ----------------------------------------------------------------------------------

        private void RaiseSolutionChanged()
        {
            Interlocked.Increment(ref solutionGeneration);
            var handlers = SolutionChanged;
            if (handlers == null)
            {
                return;
            }

            foreach (var handler in handlers.GetInvocationList())
            {
                try
                {
                    ((EventHandler)handler)(this, EventArgs.Empty);
                }
                catch (Exception ex)
                {
                    context.Log.Warn("A SolutionChanged handler failed.", ex);
                }
            }
        }

        private sealed class SolutionEventSink : IVsSolutionEvents, IVsSolutionEvents7
        {
            private readonly SolutionAdapter owner;

            public SolutionEventSink(SolutionAdapter owner)
            {
                this.owner = owner;
            }

            public int OnAfterOpenSolution(object pUnkReserved, int fNewSolution)
            {
                owner.RaiseSolutionChanged();
                return VSConstants.S_OK;
            }

            public int OnAfterCloseSolution(object pUnkReserved)
            {
                owner.RaiseSolutionChanged();
                return VSConstants.S_OK;
            }

            public void OnAfterOpenFolder(string folderPath) => owner.RaiseSolutionChanged();

            public void OnAfterCloseFolder(string folderPath) => owner.RaiseSolutionChanged();

            public int OnAfterOpenProject(IVsHierarchy pHierarchy, int fAdded) => VSConstants.S_OK;

            public int OnQueryCloseProject(IVsHierarchy pHierarchy, int fRemoving, ref int pfCancel) => VSConstants.S_OK;

            public int OnBeforeCloseProject(IVsHierarchy pHierarchy, int fRemoved)
            {
                Interlocked.Increment(ref owner.solutionGeneration);
                return VSConstants.S_OK;
            }

            public int OnAfterLoadProject(IVsHierarchy pStubHierarchy, IVsHierarchy pRealHierarchy) => VSConstants.S_OK;

            public int OnQueryUnloadProject(IVsHierarchy pRealHierarchy, ref int pfCancel) => VSConstants.S_OK;

            public int OnBeforeUnloadProject(IVsHierarchy pRealHierarchy, IVsHierarchy pStubHierarchy)
            {
                Interlocked.Increment(ref owner.solutionGeneration);
                return VSConstants.S_OK;
            }

            public int OnQueryCloseSolution(object pUnkReserved, ref int pfCancel) => VSConstants.S_OK;

            public int OnBeforeCloseSolution(object pUnkReserved)
            {
                Interlocked.Increment(ref owner.solutionGeneration);
                return VSConstants.S_OK;
            }

            public void OnQueryCloseFolder(string folderPath, ref int pfCancel)
            {
            }

            public void OnBeforeCloseFolder(string folderPath)
            {
            }

            public void OnAfterLoadAllDeferredProjects()
            {
            }
        }

        private sealed class ProjectEntry
        {
            public ProjectEntry(IVsHierarchy hierarchy, string name, string? path, string kind, bool isSolutionFolder)
            {
                Hierarchy = hierarchy;
                Name = name;
                Path = path;
                Kind = kind;
                IsSolutionFolder = isSolutionFolder;
            }

            public IVsHierarchy Hierarchy { get; }

            public string Name { get; }

            public string? Path { get; }

            public string Kind { get; }

            public bool IsSolutionFolder { get; }
        }

        private sealed class ReferenceComparer<T> : IEqualityComparer<T>
            where T : class
        {
            public static readonly ReferenceComparer<T> Instance = new ReferenceComparer<T>();

            // This comparer tracks COM hierarchy instances, never Roslyn ISymbol semantic equality.
#pragma warning disable RS1024
            public bool Equals(T? x, T? y) => ReferenceEquals(x, y);

            public int GetHashCode(T obj) => RuntimeHelpers.GetHashCode(obj);
#pragma warning restore RS1024
        }

        /// <summary>Case-insensitive substring or glob (<c>*</c>, <c>?</c>) matcher over names and paths.</summary>
        private sealed class NameMatcher
        {
            private readonly string? substring;
            private readonly Regex? regex;

            private NameMatcher(string? substring, Regex? regex)
            {
                this.substring = substring;
                this.regex = regex;
            }

            public static NameMatcher? Create(string? pattern)
            {
                if (string.IsNullOrWhiteSpace(pattern))
                {
                    return null;
                }

                var normalized = pattern!.Trim().Replace('\\', '/');
                if (normalized.IndexOfAny(new[] { '*', '?' }) < 0)
                {
                    return new NameMatcher(normalized, null);
                }

                var expression = new StringBuilder("^");
                foreach (var ch in normalized)
                {
                    switch (ch)
                    {
                        case '*':
                            expression.Append(".*");
                            break;
                        case '?':
                            expression.Append('.');
                            break;
                        default:
                            expression.Append(Regex.Escape(ch.ToString()));
                            break;
                    }
                }

                expression.Append('$');
                return new NameMatcher(null, new Regex(expression.ToString(), RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1)));
            }

            public bool IsMatch(string? name, string? path)
            {
                var normalizedPath = path?.Replace('\\', '/');
                if (regex != null)
                {
                    return (name != null && regex.IsMatch(name)) || (normalizedPath != null && regex.IsMatch(normalizedPath));
                }

                return (name != null && name.IndexOf(substring!, StringComparison.OrdinalIgnoreCase) >= 0)
                       || (normalizedPath != null && normalizedPath.IndexOf(substring!, StringComparison.OrdinalIgnoreCase) >= 0);
            }
        }
    }
}
