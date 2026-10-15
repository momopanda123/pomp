using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace Pomp.Adapters
{
    // Visual Studio adapter layer (design Layer 3). Implementations live in the VSIX and MUST switch to
    // the UI thread (JoinableTaskFactory.SwitchToMainThreadAsync) before touching DTE/COM/IVs* APIs.
    // They are safe to call from any thread and must never block the UI thread (.Result/.Wait()).

    /// <summary>Active editor and generic command execution (design: DteAdapter).</summary>
    public interface IEditorAdapter
    {
        Task<ActiveDocumentInfo?> GetActiveDocumentAsync(CancellationToken cancellationToken);

        /// <summary>Documents currently open in the editor with their dirty state.</summary>
        Task<IReadOnlyList<OpenDocumentInfo>> GetOpenDocumentsAsync(CancellationToken cancellationToken);

        Task<bool> OpenDocumentAsync(string path, int? line, CancellationToken cancellationToken);

        /// <summary>Runs a VS command by name (DTE.ExecuteCommand), e.g. "Edit.FormatDocument".</summary>
        Task<CommandExecutionResult> ExecuteCommandAsync(string commandName, string? arguments, CancellationToken cancellationToken);
    }

    /// <summary>Buffer reads that see unsaved changes, and validated edits (design: TextBufferAdapter).</summary>
    public interface ITextBufferAdapter
    {
        /// <summary>Returns the editor buffer if the file is open (even if dirty), otherwise the disk content; null if missing.</summary>
        Task<BufferSnapshot?> ReadAsync(string path, CancellationToken cancellationToken);

        /// <summary>
        /// Applies edits only when the current content hash equals <see cref="BufferEditRequest.ExpectedContentHash"/>
        /// (otherwise returns <see cref="BufferEditResult.Stale"/>), as one undo transaction. Opens the file in the
        /// editor when it is not open yet.
        /// </summary>
        Task<BufferEditResult> ApplyEditsAsync(BufferEditRequest request, CancellationToken cancellationToken);
    }

    /// <summary>Solution/project graph and project-system mutations (design: SolutionAdapter).</summary>
    public interface ISolutionAdapter
    {
        /// <summary>Null when no solution or folder is open.</summary>
        Task<SolutionInfo?> GetSolutionInfoAsync(CancellationToken cancellationToken);

        /// <summary>
        /// Name, solution file and root only: no project enumeration and no project property reads, so it is cheap enough to poll.
        /// <see cref="SolutionInfo.Projects"/> stays empty. Null when no solution or folder is open.
        /// </summary>
        Task<SolutionInfo?> GetSolutionLocationAsync(CancellationToken cancellationToken);

        Task<IReadOnlyList<SolutionNode>> QueryAsync(SolutionQuery query, CancellationToken cancellationToken);

        /// <summary>Adds a file through the project system (never by editing the project file directly).</summary>
        Task<ProjectItemResult> AddFileAsync(string projectName, string relativePath, string? initialContent, CancellationToken cancellationToken);

        Task<ProjectItemResult> RemoveFileAsync(string path, CancellationToken cancellationToken);

        Task<ProjectItemResult> RenameFileAsync(string path, string newFileName, CancellationToken cancellationToken);

        Task<IReadOnlyDictionary<string, string?>> GetMsBuildPropertiesAsync(string projectName, IReadOnlyList<string> propertyNames, CancellationToken cancellationToken);

        Task<IReadOnlyList<PackageInfo>> ListPackagesAsync(string? projectName, CancellationToken cancellationToken);

        /// <summary>Raised (on any thread) after a solution/folder is opened or closed.</summary>
        event EventHandler? SolutionChanged;
    }

    /// <summary>Optional project-system capability that binds a new-file preview to a loaded project instance.</summary>
    public interface IProjectFilePreparationAdapter
    {
        /// <summary>Validates without mutation and captures solution generation, hierarchy identity, path and exact content.</summary>
        Task<IPreparedProjectFile> PrepareAddFileAsync(string projectName, string relativePath, string? initialContent, CancellationToken cancellationToken);
    }

    /// <summary>An adapter-owned frozen plan; implementations keep COM identity private and use it only on the UI thread.</summary>
    public interface IPreparedProjectFile : IDisposable
    {
        string ProjectName { get; }

        string ProjectPath { get; }

        string Path { get; }

        string Content { get; }

        /// <summary>Revalidates the captured solution/project identity and destination; may be invoked at most once.</summary>
        Task<ProjectItemResult> CommitAsync(CancellationToken cancellationToken);
    }

    /// <summary>
    /// Optional project-system capability for approved removal and renaming of project items (the prepared-change contract of
    /// <see cref="IProjectFilePreparationAdapter"/>): preparation only validates, the returned plan commits at most once.
    /// </summary>
    public interface IProjectItemChangePreparationAdapter
    {
        /// <summary>
        /// Validates without mutation that <paramref name="path"/> is an item of exactly one loaded project inside the workspace and
        /// captures that project's identity and the file's identity. With <paramref name="deleteFromDisk"/> the commit also deletes the
        /// file (to the recycle bin where the project system supports it); otherwise it only removes the item from the project.
        /// </summary>
        Task<IPreparedProjectItemChange> PrepareRemoveFileAsync(string path, bool deleteFromDisk, CancellationToken cancellationToken);

        /// <summary>Validates without mutation: <paramref name="newFileName"/> is a plain file name in the same folder that does not exist yet.</summary>
        Task<IPreparedProjectItemChange> PrepareRenameFileAsync(string path, string newFileName, CancellationToken cancellationToken);
    }

    /// <summary>An adapter-owned frozen removal or rename; COM identity stays private and is used only on the UI thread.</summary>
    public interface IPreparedProjectItemChange : IDisposable
    {
        string ProjectName { get; }

        string ProjectPath { get; }

        /// <summary>The existing item's full path.</summary>
        string Path { get; }

        /// <summary>The full path after a rename; null for a removal.</summary>
        string? NewPath { get; }

        /// <summary>True when the commit deletes the file itself, not only the project item.</summary>
        bool DeletesFile { get; }

        /// <summary>Revalidates the captured solution/project/file identity; may be invoked at most once.</summary>
        Task<ProjectItemResult> CommitAsync(CancellationToken cancellationToken);
    }

    /// <summary>Optional NuGet capability of the solution adapter (Visual Studio's NuGet services; approval is the caller's job).</summary>
    public interface IPackageManagementAdapter
    {
        /// <summary>Installs <paramref name="packageId"/>, or updates it when it is installed; null <paramref name="version"/> = latest stable.</summary>
        Task<PackageOperationResult> InstallPackageAsync(string projectName, string packageId, string? version, CancellationToken cancellationToken);

        Task<PackageOperationResult> UninstallPackageAsync(string projectName, string packageId, CancellationToken cancellationToken);
    }

    /// <summary>Prepares a package operation without changing a project or downloading packages.</summary>
    public interface IPackageChangePreparationAdapter
    {
        Task<IPreparedPackageChange> PreparePackageChangeAsync(string projectName, string action,
            string packageId, string? version, CancellationToken cancellationToken);
    }

    /// <summary>Identity-bound, single-use package change. The caller obtains approval before committing.</summary>
    public interface IPreparedPackageChange : IDisposable
    {
        string ProjectName { get; }
        string ProjectPath { get; }
        string PackageId { get; }
        string Action { get; }
        string? Version { get; }
        string? CurrentVersion { get; }
        Task<PackageOperationResult> CommitAsync(CancellationToken cancellationToken);
    }

    /// <summary>Breakpoints and debugger control (design: DebuggerAdapter, COM STA only).</summary>
    public interface IDebuggerAdapter
    {
        Task<IReadOnlyList<BreakpointInfo>> ListBreakpointsAsync(BreakpointFilter? filter, CancellationToken cancellationToken);

        Task<BreakpointInfo> AddBreakpointAsync(BreakpointSpec spec, CancellationToken cancellationToken);

        /// <summary>Removes the breakpoints at <paramref name="file"/>:<paramref name="line"/>; false when there were none.</summary>
        Task<bool> RemoveBreakpointAsync(string file, int line, CancellationToken cancellationToken);

        /// <summary>Enables or disables the breakpoints at <paramref name="file"/>:<paramref name="line"/>; false when there were none.</summary>
        Task<bool> SetBreakpointEnabledAsync(string file, int line, bool enabled, CancellationToken cancellationToken);

        Task<DebugState> GetDebugStateAsync(int maxFrames, bool includeLocals, CancellationToken cancellationToken);

        Task<DebugState> ControlAsync(DebugAction action, CancellationToken cancellationToken);
    }

    /// <summary>Bookmarks (design: BookmarkAdapter).</summary>
    public interface IBookmarkAdapter
    {
        Task<IReadOnlyList<BookmarkInfo>> ListAsync(CancellationToken cancellationToken);

        Task<bool> NavigateAsync(BookmarkInfo bookmark, CancellationToken cancellationToken);

        Task<bool> AddAsync(string file, int line, CancellationToken cancellationToken);
    }

    /// <summary>Error List window.</summary>
    public interface IErrorListAdapter
    {
        Task<IReadOnlyList<ErrorListItem>> ReadAsync(ErrorListFilter filter, CancellationToken cancellationToken);
    }

    /// <summary>Optional live Error List change signals; subscribers debounce their reads.</summary>
    public interface IObservableErrorListAdapter : IErrorListAdapter
    {
        event EventHandler? Changed;
    }

    /// <summary>Solution/project builds.</summary>
    public interface IBuildAdapter
    {
        /// <summary>Starts a build and completes when it finishes (without blocking the UI thread).</summary>
        Task<BuildResult> BuildAsync(BuildRequest request, CancellationToken cancellationToken);
    }

    /// <summary>Output window panes.</summary>
    public interface IOutputWindowAdapter
    {
        /// <summary>Last <paramref name="maxLines"/> lines of a pane (e.g. "Build", "Debug"); null if the pane does not exist.</summary>
        Task<string?> ReadPaneAsync(string paneName, int maxLines, CancellationToken cancellationToken);
    }

    /// <summary>Git in the workspace repository (the git command line; commits and pushes are approved by the caller).</summary>
    public interface IGitAdapter
    {
        /// <summary>Branch, upstream and changed files; <see cref="GitStatus.IsRepository"/> is false outside a repository.</summary>
        Task<GitStatus> GetStatusAsync(CancellationToken cancellationToken);

        /// <summary>A unified diff (bounded by <see cref="GitDiffRequest.MaxChars"/>).</summary>
        Task<GitDiffResult> GetDiffAsync(GitDiffRequest request, CancellationToken cancellationToken);

        Task<GitCommandResult> CommitAsync(GitCommitRequest request, CancellationToken cancellationToken);

        Task<GitCommandResult> PushAsync(GitPushRequest request, CancellationToken cancellationToken);
    }

    /// <summary>Test discovery and runs of the solution's test projects (runs are approved by the caller).</summary>
    public interface ITestAdapter
    {
        Task<TestListResult> ListTestsAsync(string? projectName, CancellationToken cancellationToken);

        Task<TestRunResult> RunTestsAsync(TestRunRequest request, CancellationToken cancellationToken);
    }

    /// <summary>Single access point to all adapters (design: 어댑터 팩토리).</summary>
    public interface IVsAdapterFactory
    {
        IEditorAdapter Editor { get; }

        ITextBufferAdapter TextBuffers { get; }

        ISolutionAdapter Solution { get; }

        IDebuggerAdapter Debugger { get; }

        IBookmarkAdapter Bookmarks { get; }

        IErrorListAdapter ErrorList { get; }

        IBuildAdapter Build { get; }

        IOutputWindowAdapter Output { get; }

        IGitAdapter Git { get; }

        ITestAdapter Tests { get; }
    }
}
