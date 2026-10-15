using System;
using System.Threading;
using System.Threading.Tasks;

namespace Pomp.Permissions
{
    /// <summary>Kinds of sensitive actions that need user approval (design: 권한 모델).</summary>
    public enum PermissionKind
    {
        /// <summary>Read-only; never prompts.</summary>
        None,
        FileWrite,
        ProjectChange,
        ProcessExec,
        DebuggerControl,
        BreakpointChange,
        BookmarkChange,
        PackageChange,
        VcsWrite,
        TestRun,
        Other,
    }

    /// <summary>Where a permission request originates.</summary>
    public enum PermissionSource
    {
        /// <summary>A pomp host tool (the host gates it before executing).</summary>
        HostTool,

        /// <summary>omp's own approval prompt for one of its built-in tools (bash, edit, write, ...).</summary>
        OmpApproval,
    }

    public enum PermissionDecision
    {
        /// <summary>Allow this call only.</summary>
        Allow,

        /// <summary>Allow this tool until the chat session ends (kept in memory only).</summary>
        AllowForSession,

        /// <summary>Allow this tool from now on; persisted in settings and revocable from the options page.</summary>
        AlwaysAllow,

        Deny,
    }

    /// <summary>Content of the approval dialog.</summary>
    public sealed class PermissionRequest
    {
        public string RequestId { get; set; } = Guid.NewGuid().ToString("N");

        public PermissionSource Source { get; set; }

        public PermissionKind Kind { get; set; } = PermissionKind.Other;

        /// <summary>Tool name as known to omp (host tool name or omp built-in tool name).</summary>
        public string ToolName { get; set; } = string.Empty;

        /// <summary>Short headline, e.g. "Write file".</summary>
        public string Title { get; set; } = string.Empty;

        /// <summary>Details shown under the title (command line, impact, omp prompt text...).</summary>
        public string? Message { get; set; }

        public System.Collections.Generic.List<PermissionTask>? Tasks { get; set; }

        /// <summary>Main target (file path, project name, package id...).</summary>
        public string? Target { get; set; }

        /// <summary>Optional unified diff preview for file edits.</summary>
        public string? Diff { get; set; }

        /// <summary>When set, the request resolves to <see cref="PermissionDecision.Deny"/> after this delay.</summary>
        public TimeSpan? Timeout { get; set; }

        /// <summary>
        /// False when the decision must be made for this call alone: it is never answered from, or stored as, a session or
        /// "always allow" rule (omp prompts it shows regardless of rules: a <c>Reason:</c> line, e.g. for dangerous commands, or
        /// a <c>Provider safety checks:</c> section).
        /// </summary>
        public bool CanRemember { get; set; } = true;

        /// <summary>Key used for session/persistent "always allow" memory, e.g. "host:build_solution" or "omp:bash".</summary>
        public string PolicyKey => (Source == PermissionSource.HostTool ? "host:" : "omp:") + ToolName;
    }

    public sealed class PermissionTask
    {
        public string Agent { get; set; } = string.Empty;
        public string Task { get; set; } = string.Empty;
    }

    public sealed class PermissionResponse
    {
        public PermissionResponse(PermissionDecision decision, string? note = null)
        {
            Decision = decision;
            Note = note;
        }

        private PermissionResponse(string systemReason)
        {
            Decision = PermissionDecision.Deny;
            SystemReason = systemReason;
        }

        public PermissionDecision Decision { get; }

        /// <summary>
        /// Optional free text from the user. Forwarded to the model when a host tool is denied; omp's own approval prompts have
        /// no channel for it, so there it is only logged.
        /// </summary>
        public string? Note { get; }

        /// <summary>
        /// Set when the host denied the request without an answer from the user (cancelled, timed out, the chat UI could not
        /// show it...): why. It is never presented as the user's words.
        /// </summary>
        public string? SystemReason { get; }

        public bool IsAllowed => Decision == PermissionDecision.Allow
                                 || Decision == PermissionDecision.AllowForSession
                                 || Decision == PermissionDecision.AlwaysAllow;

        /// <summary>The user denied the request, optionally with a note.</summary>
        public static PermissionResponse Denied(string? note = null) => new PermissionResponse(PermissionDecision.Deny, note);

        /// <summary>The host denied the request itself; the user gave no answer.</summary>
        public static PermissionResponse DeniedBySystem(string reason) => new PermissionResponse(reason ?? "The request was denied by POMP.");
    }

    /// <summary>Shows the approval dialog to the user (implemented on top of the WebView UI channel).</summary>
    public interface IPermissionPrompt
    {
        Task<PermissionResponse> PromptAsync(PermissionRequest request, CancellationToken cancellationToken);
    }

    /// <summary>
    /// Permission gate: answers from session memory / persisted "always allow" rules when possible,
    /// otherwise asks through <see cref="IPermissionPrompt"/>. Cancellation or timeout resolves to deny.
    /// </summary>
    public interface IPermissionGate
    {
        Task<PermissionResponse> RequestAsync(PermissionRequest request, CancellationToken cancellationToken);

        /// <summary>Forgets <see cref="PermissionDecision.AllowForSession"/> grants (new chat session).</summary>
        void ResetSession();
    }
}
