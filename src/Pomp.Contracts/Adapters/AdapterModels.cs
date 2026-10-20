using System;
using System.Collections.Generic;

namespace Pomp.Adapters
{
    // Plain DTOs exchanged between host tools and Visual Studio adapters.
    // Lines and columns are 1-based everywhere; paths are absolute and fully qualified.

    public sealed class TextPosition
    {
        public int Line { get; set; }

        public int Column { get; set; }
    }

    public sealed class ActiveDocumentInfo
    {
        public string Path { get; set; } = string.Empty;

        /// <summary>VS content type / language, e.g. "CSharp".</summary>
        public string? Language { get; set; }

        public TextPosition Caret { get; set; } = new TextPosition();

        public TextPosition? SelectionStart { get; set; }

        public TextPosition? SelectionEnd { get; set; }

        public string? SelectedText { get; set; }

        public int LineCount { get; set; }

        /// <summary>The editor buffer differs from the file on disk.</summary>
        public bool IsDirty { get; set; }
    }

    /// <summary>Content of a file as the user sees it: the open editor buffer first, otherwise the disk file.</summary>
    public sealed class BufferSnapshot
    {
        public string Path { get; set; } = string.Empty;

        /// <summary>Full text with the buffer's own line endings.</summary>
        public string Text { get; set; } = string.Empty;

        /// <summary>
        /// SHA-256 (lower-case hex) of the UTF-8 text with every "\r\n" and every lone "\r" replaced by "\n"; used for stale detection.
        /// Line numbers of <see cref="TextLineEdit"/> follow the same model: only "\r\n", "\r" and "\n" end a line.
        /// </summary>
        public string ContentHash { get; set; } = string.Empty;

        public bool IsOpenInEditor { get; set; }

        public bool IsDirty { get; set; }

        /// <summary>Dominant line ending of the buffer ("\r\n" or "\n").</summary>
        public string LineEnding { get; set; } = "\r\n";
    }

    /// <summary>Replace original lines [StartLine..EndLine] (inclusive) with <see cref="NewLines"/>.</summary>
    public sealed class TextLineEdit
    {
        public int StartLine { get; set; }

        /// <summary>Use StartLine - 1 for a pure insertion before StartLine.</summary>
        public int EndLine { get; set; }

        public List<string> NewLines { get; set; } = new List<string>();
    }

    public sealed class BufferEditRequest
    {
        public string Path { get; set; } = string.Empty;

        /// <summary>The <see cref="BufferSnapshot.ContentHash"/> the edits were computed against.</summary>
        public string ExpectedContentHash { get; set; } = string.Empty;

        /// <summary>Non-overlapping edits expressed against the original snapshot.</summary>
        public List<TextLineEdit> Edits { get; set; } = new List<TextLineEdit>();

        /// <summary>
        /// Optional <see cref="BufferSnapshot.ContentHash"/> the edited text must have. When set, the adapter computes the result of
        /// its plan before touching the buffer and changes nothing if it differs: an approved preview is exactly what gets applied.
        /// </summary>
        public string? ExpectedResultContentHash { get; set; }

        /// <summary>
        /// Optional workspace root the edit was approved under. When set, the adapter changes nothing unless it is still the current
        /// workspace root at the moment the buffer is edited (a solution switch while the edit waited for the UI thread).
        /// </summary>
        public string? ExpectedWorkspaceRoot { get; set; }

        /// <summary>Caption of the single undo transaction.</summary>
        public string UndoDescription { get; set; } = "Pomp edit";
    }

    public sealed class BufferEditResult
    {
        public bool Applied { get; set; }

        /// <summary>The buffer changed since the snapshot; nothing was applied.</summary>
        public bool Stale { get; set; }

        public string? NewContentHash { get; set; }

        public string? Error { get; set; }
    }

    public sealed class CommandExecutionResult
    {
        public bool Succeeded { get; set; }

        public string? Error { get; set; }
    }

    public sealed class ProjectInfo
    {
        public string Name { get; set; } = string.Empty;

        /// <summary>Full path of the project file.</summary>
        public string? Path { get; set; }

        /// <summary>Project kind/type, e.g. "C#", "C++", "Solution Folder".</summary>
        public string? Kind { get; set; }

        public string? TargetFrameworks { get; set; }
    }

    public sealed class SolutionInfo
    {
        public string? Name { get; set; }

        /// <summary>Full path of the .sln/.slnx file; null for "open folder" workspaces.</summary>
        public string? SolutionPath { get; set; }

        /// <summary>Workspace root used as omp's working directory.</summary>
        public string RootDirectory { get; set; } = string.Empty;

        public List<ProjectInfo> Projects { get; set; } = new List<ProjectInfo>();
    }

    public sealed class SolutionQuery
    {
        /// <summary>Case-insensitive substring or glob (*, ?) matched against item names/paths; null lists everything.</summary>
        public string? Pattern { get; set; }

        /// <summary>Tree depth below the solution node (1 = projects only).</summary>
        public int Depth { get; set; } = 3;

        public int MaxResults { get; set; } = 500;
    }

    public sealed class SolutionNode
    {
        public string Name { get; set; } = string.Empty;

        /// <summary>"solution", "project", "folder" or "file".</summary>
        public string Kind { get; set; } = "file";

        public string? Path { get; set; }

        public string? ProjectName { get; set; }

        public List<SolutionNode>? Children { get; set; }
    }

    public sealed class ProjectItemResult
    {
        public bool Succeeded { get; set; }

        public string? Path { get; set; }

        public string? Error { get; set; }
    }

    public sealed class PackageInfo
    {
        public string Project { get; set; } = string.Empty;

        public string Id { get; set; } = string.Empty;

        public string? Version { get; set; }
    }

    public sealed class BreakpointFilter
    {
        public string? File { get; set; }

        public bool EnabledOnly { get; set; }
    }

    public sealed class BreakpointSpec
    {
        public string? File { get; set; }

        public int? Line { get; set; }

        /// <summary>For function breakpoints.</summary>
        public string? FunctionName { get; set; }

        /// <summary>Expression; the breakpoint only fires when it is true (or, with <see cref="ConditionWhenChanged"/>, when its value changed).</summary>
        public string? Condition { get; set; }

        public bool ConditionWhenChanged { get; set; }

        public int? HitCount { get; set; }

        public BreakpointHitCountMode HitCountMode { get; set; }

        /// <summary>Tracepoint: when set, a hit writes this message to the Output window instead of breaking (unless <see cref="BreakWhenLogged"/>).</summary>
        public string? LogMessage { get; set; }

        public bool BreakWhenLogged { get; set; }
    }

    public enum BreakpointHitCountMode
    {
        /// <summary>Break when the hit count equals <see cref="BreakpointSpec.HitCount"/>.</summary>
        Equal,
        GreaterOrEqual,

        /// <summary>Break on every multiple of <see cref="BreakpointSpec.HitCount"/>.</summary>
        Multiple,
    }

    public sealed class PackageOperationResult
    {
        public bool Succeeded { get; set; }

        public string Project { get; set; } = string.Empty;

        public string PackageId { get; set; } = string.Empty;

        /// <summary>The version installed afterwards (null after an uninstall or a failure).</summary>
        public string? Version { get; set; }

        public string? Error { get; set; }
    }

    public sealed class BreakpointInfo
    {
        public string? File { get; set; }

        public int? Line { get; set; }

        public string? FunctionName { get; set; }

        public string? Condition { get; set; }

        public int? HitCountTarget { get; set; }

        public int CurrentHits { get; set; }

        public bool Enabled { get; set; }

        /// <summary>Binding state reported by the debugger, e.g. "bound", "unbound", "error".</summary>
        public string? BindingState { get; set; }
    }

    public enum DebugMode
    {
        Design,
        Run,
        Break,
    }

    public enum DebugAction
    {
        Start,
        Stop,
        Break,
        Continue,
        StepInto,
        StepOver,
        StepOut,
    }

    public sealed class StackFrameInfo
    {
        public string FunctionName { get; set; } = string.Empty;

        public string? Module { get; set; }

        public string? File { get; set; }

        public int? Line { get; set; }
    }

    public sealed class VariableInfo
    {
        public string Name { get; set; } = string.Empty;

        public string? Type { get; set; }

        public string? Value { get; set; }
    }

    public sealed class DebugState
    {
        public DebugMode Mode { get; set; }

        public string? ProcessName { get; set; }

        public string? LastBreakReason { get; set; }

        public List<StackFrameInfo> CallStack { get; set; } = new List<StackFrameInfo>();

        public List<VariableInfo> Locals { get; set; } = new List<VariableInfo>();
    }

    public sealed class BookmarkInfo
    {
        public string File { get; set; } = string.Empty;

        public int Line { get; set; }

        public string? Name { get; set; }
    }

    public sealed class GitStatus
    {
        public bool IsRepository { get; set; }

        public string? RepositoryRoot { get; set; }

        /// <summary>Null when HEAD is detached.</summary>
        public string? Branch { get; set; }

        public string? HeadCommit { get; set; }

        public string? Upstream { get; set; }

        public int Ahead { get; set; }

        public int Behind { get; set; }

        public List<GitFileChange> Changes { get; set; } = new List<GitFileChange>();
    }

    public sealed class GitFileChange
    {
        /// <summary>Repository-relative path with forward slashes.</summary>
        public string Path { get; set; } = string.Empty;

        /// <summary>The source path of a rename or copy.</summary>
        public string? OriginalPath { get; set; }

        /// <summary>Porcelain status letters: index (staged) and work tree, e.g. 'M', 'A', 'D', 'R', '?' (untracked), ' ' (unchanged).</summary>
        public char IndexStatus { get; set; } = ' ';

        public char WorkTreeStatus { get; set; } = ' ';
    }

    public sealed class GitDiffRequest
    {
        /// <summary>Workspace-relative or absolute path inside the repository; null = every change.</summary>
        public string? Path { get; set; }

        /// <summary>The staged changes (index against HEAD) instead of the work tree against the index.</summary>
        public bool Staged { get; set; }

        public int MaxChars { get; set; } = 60000;
    }

    public sealed class GitDiffResult
    {
        public string Diff { get; set; } = string.Empty;

        public bool Truncated { get; set; }
    }

    public sealed class GitCommitRequest
    {
        public string Message { get; set; } = string.Empty;

        /// <summary>Files to stage before committing (workspace-relative or absolute); null or empty = commit what is staged.</summary>
        public List<string>? Paths { get; set; }

        /// <summary>Stage every tracked and untracked change first (git add -A) instead of <see cref="Paths"/>.</summary>
        public bool StageAll { get; set; }
    }

    public sealed class GitPushRequest
    {
        /// <summary>Null = the branch's upstream remote (or "origin").</summary>
        public string? Remote { get; set; }

        /// <summary>Null = the current branch.</summary>
        public string? Branch { get; set; }

        public bool SetUpstream { get; set; }
    }

    public sealed class GitCommandResult
    {
        public bool Succeeded { get; set; }

        public int ExitCode { get; set; }

        /// <summary>Combined, bounded stdout/stderr of git.</summary>
        public string Output { get; set; } = string.Empty;

        public string? Error { get; set; }

        /// <summary>The new commit (commits only).</summary>
        public string? CommitId { get; set; }
    }

    public sealed class TestCaseInfo
    {
        public string Project { get; set; } = string.Empty;

        public string FullyQualifiedName { get; set; } = string.Empty;
    }

    public sealed class TestListResult
    {
        public List<TestCaseInfo> Tests { get; set; } = new List<TestCaseInfo>();

        /// <summary>Test projects that could not be listed (build failure, unsupported project type...), with the reason.</summary>
        public List<string> Errors { get; set; } = new List<string>();
    }

    public sealed class TestRunRequest
    {
        /// <summary>Null = every test project of the solution.</summary>
        public string? ProjectName { get; set; }

        /// <summary>A test filter expression (dotnet test --filter syntax), e.g. "FullyQualifiedName~Parser".</summary>
        public string? Filter { get; set; }

        public int TimeoutSeconds { get; set; } = 600;
    }

    public sealed class TestOutcomeInfo
    {
        public string Project { get; set; } = string.Empty;

        public string Name { get; set; } = string.Empty;

        /// <summary>"Passed", "Failed", "Skipped" or "NotExecuted".</summary>
        public string Outcome { get; set; } = string.Empty;

        public string? Message { get; set; }

        public string? StackTrace { get; set; }

        public double? DurationMs { get; set; }
    }

    public sealed class TestRunResult
    {
        public int Total { get; set; }

        public int Passed { get; set; }

        public int Failed { get; set; }

        public int Skipped { get; set; }

        public bool TimedOut { get; set; }

        /// <summary>Failed (and, bounded, other notable) results.</summary>
        public List<TestOutcomeInfo> Results { get; set; } = new List<TestOutcomeInfo>();

        /// <summary>Why the run could not start or finish (build failure, no test projects...); null on a completed run.</summary>
        public string? Error { get; set; }

        /// <summary>The last lines of the runner output.</summary>
        public string? OutputTail { get; set; }
    }

    public enum ErrorSeverity
    {
        Error,
        Warning,
        Message,
    }

    public sealed class ErrorListFilter
    {
        public bool OrderByLocation { get; set; }
        public string? WorkspaceRoot { get; set; }
        /// <summary>Severities to include; null or empty means all.</summary>
        public List<ErrorSeverity>? Severities { get; set; }

        public string? Project { get; set; }

        public int MaxItems { get; set; } = 200;
    }

    public sealed class ErrorListItem
    {
        public ErrorSeverity Severity { get; set; }

        public string? Code { get; set; }

        public string Description { get; set; } = string.Empty;

        public string? File { get; set; }

        public int? Line { get; set; }

        public int? Column { get; set; }

        public string? Project { get; set; }
    }

    public sealed class BuildRequest
    {
        /// <summary>Project name to build; null builds the whole solution.</summary>
        public string? Project { get; set; }

        /// <summary>
        /// Solution configuration, e.g. "Debug", or a configuration/platform pair such as "Release|x64"; null keeps the active one.
        /// Without a platform the active solution platform is kept.
        /// </summary>
        public string? Configuration { get; set; }

        /// <summary>Optional status lines while the call waits (for a build that was already running, for the build to start, for it to end).</summary>
        public IProgress<string>? Progress { get; set; }
    }

    public sealed class BuildResult
    {
        public bool Succeeded { get; set; }

        public int FailedProjects { get; set; }

        public string Summary { get; set; } = string.Empty;

        /// <summary>Diagnostics after the build, errors first; capped, so use the counts below for totals.</summary>
        public List<ErrorListItem> Errors { get; set; } = new List<ErrorListItem>();

        /// <summary>Total errors in the Error List after the build (not limited by the cap on <see cref="Errors"/>).</summary>
        public int ErrorCount { get; set; }

        /// <summary>Total warnings in the Error List after the build (not limited by the cap on <see cref="Errors"/>).</summary>
        public int WarningCount { get; set; }

        /// <summary>
        /// Set when nothing was built (no solution, debugging in progress, Visual Studio did not start the build, invalid request):
        /// the reason, for the model. The other members carry no outcome then.
        /// </summary>
        public string? Error { get; set; }
    }

    public sealed class OpenDocumentInfo
    {
        public string Path { get; set; } = string.Empty;

        public bool IsDirty { get; set; }
    }
}
