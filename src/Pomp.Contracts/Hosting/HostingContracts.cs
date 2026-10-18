using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Pomp.Bridge;
using Pomp.Protocol;
using Pomp.Ui;

namespace Pomp.Hosting
{
    /// <summary>User settings (VS options page). Implementations raise <see cref="Changed"/> after a save.</summary>
    public interface IPompSettings
    {
        /// <summary>Explicit omp executable; highest priority in path resolution (settings → bundled → PATH).</summary>
        string? OmpExecutablePath { get; }

        OmpApprovalMode ApprovalMode { get; }

        /// <summary>Value for omp <c>--model</c> (id or role); null keeps omp's default.</summary>
        string? Model { get; }

        /// <summary>Value for omp <c>--thinking</c>; null keeps omp's default.</summary>
        string? ThinkingLevel { get; }

        /// <summary>Extra raw command-line arguments appended to the omp launch.</summary>
        string? ExtraArguments { get; }

        /// <summary>Launch with <c>--mode rpc-ui</c> (routes tool UI such as the ask tool) instead of <c>--mode rpc</c>.</summary>
        bool UseRpcUiMode { get; }

        /// <summary>Load pomp's omp-side extension/hook/skill package.</summary>
        bool LoadOmpExtensions { get; }

        /// <summary>
        /// Let omp also load the extensions and hooks it discovers itself: the user's omp profile, installed omp plugins and the
        /// workspace's <c>.omp</c> folder. When false, omp starts with <c>--no-extensions</c> (pomp's own package still loads
        /// through <c>-e</c>): discovered extensions run unsandboxed inside omp, so a cloned repository could otherwise run code
        /// as soon as the chat window opens.
        /// </summary>
        bool LoadDiscoveredOmpExtensions { get; }

        /// <summary>
        /// Block omp's network tools (web search, URL reads, GitHub, browser): denied in the config overlay and blocked by pomp's
        /// omp extension. Off by default (the design: network access allowed, every such call logged). Not a sandbox: bash, eval,
        /// MCP servers and the model provider still reach the network.
        /// </summary>
        bool BlockOmpNetworkTools { get; }

        /// <summary>
        /// After source edits, the omp extension's verification gate continues the turn once with a nudge to build or run the
        /// relevant tests and report the result. On by default; the static "evidence before completion" guidance applies either way.
        /// </summary>
        bool VerifyAfterEdits { get; }

        bool IncludeEditorContextByDefault { get; }

        bool ComposerPredictions { get; }

        /// <summary>Persisted "always allow" policy keys (see <see cref="Permissions.PermissionRequest.PolicyKey"/>).</summary>
        IReadOnlyCollection<string> AlwaysAllowedPolicyKeys { get; }

        void AddAlwaysAllowed(string policyKey);

        void RemoveAlwaysAllowed(string policyKey);

        event EventHandler? Changed;
    }

    /// <summary>Settings that carry the chat page's keyboard shortcuts (Tools > Options > Pomp > Shortcuts); a host without them gets the defaults.</summary>
    public interface IUiShortcutSettings
    {
        /// <summary>Normalized: every chord valid or replaced by its default.</summary>
        UiShortcutsPayload Shortcuts { get; }
    }

    /// <summary>Settings that size the editor context a message carries (Tools > Options > Pomp > Chat); a host without them gets <see cref="EditorContextLimits.Default"/>.</summary>
    public interface IEditorContextSettings
    {
        EditorContextLimits EditorContextLimits { get; }
    }

    /// <summary>
    /// How much of the editor the <c>&lt;vs_context&gt;</c> block carries. Immutable; every value is clamped to its range here, so any source
    /// (the options page, a hand-edited registry value) gives a usable block.
    /// </summary>
    public sealed class EditorContextLimits
    {
        public const int MinLines = 0, MaxLines = 1000;
        public const int MinLargeFileThreshold = 100, MaxLargeFileThreshold = 10000000;
        public const int MinSelectedChars = 0, MaxSelectedChars = 20000; // the editor adapter reads at most 20000 selected characters
        public const int MinLineChars = 20, MaxLineChars = 10000;
        public const int MinWindowChars = 0, MaxWindowChars = 100000;
        public const int MinErrors = 0, MaxErrors = 100;
        public const int MinTimeoutSeconds = 1, MaxTimeoutSeconds = 10;

        public static readonly EditorContextLimits Default = new EditorContextLimits();

        public EditorContextLimits(int windowLines = 30, int largeFileWindowLines = 100, int largeFileThreshold = 10000, int selectedChars = 2000,
            int lineChars = 300, int windowChars = 8000, int errors = 10, int timeoutSeconds = 2)
        {
            WindowLines = Clamp(windowLines, MinLines, MaxLines);
            LargeFileWindowLines = Clamp(largeFileWindowLines, MinLines, MaxLines);
            LargeFileThreshold = Clamp(largeFileThreshold, MinLargeFileThreshold, MaxLargeFileThreshold);
            SelectedChars = Clamp(selectedChars, MinSelectedChars, MaxSelectedChars);
            LineChars = Clamp(lineChars, MinLineChars, MaxLineChars);
            WindowChars = Clamp(windowChars, MinWindowChars, MaxWindowChars);
            Errors = Clamp(errors, MinErrors, MaxErrors);
            Timeout = TimeSpan.FromSeconds(Clamp(timeoutSeconds, MinTimeoutSeconds, MaxTimeoutSeconds));
        }

        /// <summary>Lines shown on each side of the caret.</summary>
        public int WindowLines { get; }

        /// <summary>Lines on each side of the caret in a file of at least <see cref="LargeFileThreshold"/> lines.</summary>
        public int LargeFileWindowLines { get; }

        public int LargeFileThreshold { get; }

        /// <summary>Characters of the selected text (0 = the selected text is left out; its range is still named).</summary>
        public int SelectedChars { get; }

        /// <summary>Characters of one line around the caret; a longer line is cut.</summary>
        public int LineChars { get; }

        /// <summary>Characters of all the lines around the caret together (0 = no lines).</summary>
        public int WindowChars { get; }

        /// <summary>Error List errors (0 = none).</summary>
        public int Errors { get; }

        /// <summary>How long each Visual Studio query of the block may take before it is left out.</summary>
        public TimeSpan Timeout { get; }

        private static int Clamp(int value, int min, int max) => value < min ? min : value > max ? max : value;
    }

    /// <summary>
    /// Settings that carry the keyboard shortcuts of POMP's Visual Studio commands (Tools > Options > Pomp > Shortcuts); the VSIX applies
    /// them to the IDE's key bindings, so the options page is the one place the keys live.
    /// </summary>
    public interface IEditorShortcutSettings
    {
        /// <summary>Normalized: every chord valid (Ctrl or Alt, or an F key) or replaced by its default; empty = no key.</summary>
        EditorShortcutsPayload EditorShortcuts { get; }
    }

    /// <summary>
    /// Settings whose approval mode the chat page may change (the composer's permission-mode chip): the one setting is applied and
    /// saved at once and <see cref="IPompSettings.Changed"/> is raised, so the host restarts omp with the new launch flag.
    /// </summary>
    public interface IApprovalModeWriter
    {
        Task SetApprovalModeAsync(OmpApprovalMode mode, CancellationToken cancellationToken);
    }

    /// <summary>Facts about the hosting IDE and the installed extension, provided by the VSIX.</summary>
    public interface IPompHostEnvironment
    {
        /// <summary>pomp version (extension version).</summary>
        string HostVersion { get; }

        /// <summary>Visual Studio version, e.g. "17.14.5" or "16.11.40".</summary>
        string VsVersion { get; }

        /// <summary>Install folder of the extension (contains WebUI/, omp-extension/ and optionally omp/omp.exe).</summary>
        string ExtensionDirectory { get; }

        /// <summary>Per-user writable folder, %LOCALAPPDATA%\Pomp (logs, omp overlay/state files, WebView2 data).</summary>
        string DataDirectory { get; }

        /// <summary>Solution/folder root used as omp's working directory; a fallback folder when nothing is open.</summary>
        Task<string> GetWorkspaceRootAsync(CancellationToken cancellationToken);

        /// <summary>Opens the pomp log (output pane or file).</summary>
        void ShowLog();

        /// <summary>Opens the options page.</summary>
        void ShowSettings();
    }

    /// <summary>Optional host capability: recycle local files and folders, never fall back to permanent deletion.</summary>
    public interface IPompRecycleBin
    {
        Task RecycleAsync(IReadOnlyList<string> paths, CancellationToken cancellationToken);
    }

    /// <summary>Optional capability of an <see cref="IPompHostEnvironment"/>: a native file dialog (the chat UI's attach button).</summary>
    public interface IPompFilePicker
    {
        /// <summary>Shows a multi-select open-file dialog; full paths, or an empty list when the user closed it.</summary>
        Task<IReadOnlyList<string>> PickFilesAsync(string? initialDirectory, CancellationToken cancellationToken);
    }

    /// <summary>
    /// One chat session bound to one omp process: owns the bridge, the host tool dispatcher, the permission gate
    /// and the UI translation (design Layer 2). Created by the VSIX composition root; UI-agnostic.
    /// </summary>
    public interface IPompSession : IDisposable
    {
        IOmpProcessBridge Bridge { get; }

        /// <summary>Binds the chat UI (a tool window's WebView). Replaces any previously attached channel.</summary>
        void AttachUi(IUiChannel channel);

        void DetachUi(IUiChannel channel);

        /// <summary>Starts omp if it is not running yet (lazy start when the tool window opens).</summary>
        Task EnsureStartedAsync(CancellationToken cancellationToken);

        /// <summary>Restarts omp, e.g. after the solution changed (new working directory) or settings changed.</summary>
        Task RestartAsync(CancellationToken cancellationToken);
    }
}
