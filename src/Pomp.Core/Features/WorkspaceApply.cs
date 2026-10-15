using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace Pomp.Features
{
    public enum WorkspaceFileState { Unchanged, Applied, RolledBack, Conflict, Failed }
    public sealed class WorkspaceFileOutcome
    {
        public WorkspaceFileOutcome(string path, WorkspaceFileState state, string? error = null)
        { Path = path; State = state; Error = error; }
        public string Path { get; }
        public WorkspaceFileState State { get; }
        public string? Error { get; }
    }
    public sealed class WorkspaceApplyResult
    {
        public WorkspaceApplyResult(IEnumerable<WorkspaceFileOutcome> files, string? error = null)
        { Files = files.ToList().AsReadOnly(); Error = error; }
        public IReadOnlyList<WorkspaceFileOutcome> Files { get; }
        public string? Error { get; }
        public bool Succeeded => Error == null && Files.Count > 0 && Files.All(f => f.State == WorkspaceFileState.Applied || f.State == WorkspaceFileState.Unchanged);
        public bool HasPartialChanges => !Succeeded && Files.Any(f => f.State == WorkspaceFileState.Applied || f.State == WorkspaceFileState.Conflict);
    }
    public interface IWorkspaceEditApplier
    {
        /// <summary>Caller must obtain approval first. Revalidate every source; attempt rollback on failure and report any conflict honestly.</summary>
        Task<WorkspaceApplyResult> ApplyAsync(ShadowSnapshot snapshot, string workspaceRoot, CancellationToken cancellationToken);
    }
    /// <summary>Optional feature capabilities implemented by the VS adapter factory.</summary>
    public interface IWorkspaceFeatureAdapters
    {
        Task<ShadowFile> CaptureForReviewAsync(string workspaceRoot, string path, string expectedHash, string after, CancellationToken cancellationToken);
        IShadowValidator ShadowValidator { get; }
        IWorkspaceEditApplier WorkspaceEdits { get; }
    }
    /// <summary>One file whose current text may differ from its original, listed without reading contents.</summary>
    public sealed class EditorChangeEntry
    {
        public EditorChangeEntry(string path, bool unsaved) { Path = path; Unsaved = unsaved; }
        public string Path { get; }
        /// <summary>The open editor buffer differs from the file on disk.</summary>
        public bool Unsaved { get; }
    }
    /// <summary>Exact original and current text of one candidate file: current is the open buffer if any, else the disk file; original is git HEAD when tracked, else the saved disk file.</summary>
    public sealed class EditorChangeText
    {
        public EditorChangeText(string path, string original, string current) { Path = path; Original = original; Current = current; }
        public string Path { get; }
        public string Original { get; }
        public string Current { get; }
    }
    /// <summary>Optional: the user's own edits in the IDE as review proposals. Implemented by the VS adapter factory; the review controller degrades to "unavailable" without it.</summary>
    public interface IEditorChangeSource
    {
        /// <summary>All candidate files in the workspace, unsorted. Path-only plus dirty flags; must stay quick with many changed files (no content reads). May include files whose texts turn out equal; <see cref="ReadAsync"/> drops those. Empty when nothing changed; never null.</summary>
        Task<IReadOnlyList<EditorChangeEntry>> ListAsync(string workspaceRoot, CancellationToken cancellationToken);
        /// <summary>Exact texts for up to ten listed paths. Silently skips deleted files, files without an original and files whose current text equals the original; throws when a remaining file cannot be read safely (too large, undecodable). Never null.</summary>
        Task<IReadOnlyList<EditorChangeText>> ReadAsync(string workspaceRoot, IReadOnlyList<string> paths, CancellationToken cancellationToken);
    }
}
