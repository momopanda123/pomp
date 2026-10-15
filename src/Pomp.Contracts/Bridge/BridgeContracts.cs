using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;
using Pomp.Protocol;

namespace Pomp.Bridge
{
    /// <summary>Lifecycle of the omp child process as seen by the bridge (design: 재연결 상태 머신).</summary>
    public enum BridgeState
    {
        Stopped,
        Starting,

        /// <summary>Process is up; waiting for <c>ready</c>, negotiating protocol v2, registering host tools.</summary>
        Handshaking,
        Running,

        /// <summary>The process died unexpectedly; a restart is scheduled with exponential back-off.</summary>
        Reconnecting,

        /// <summary>Consecutive restart failures exhausted; needs an explicit user action (restart / view log).</summary>
        Failed,
        Stopping,
    }

    public sealed class BridgeStateChangedEventArgs : EventArgs
    {
        public BridgeStateChangedEventArgs(BridgeState state, string? message = null, int attempt = 0, TimeSpan? nextRetryIn = null, string? reason = null)
        {
            State = state;
            Message = message;
            Attempt = attempt;
            NextRetryIn = nextRetryIn;
            Reason = reason;
        }

        public BridgeState State { get; }

        /// <summary>Human readable reason (exit code, error text...).</summary>
        public string? Message { get; }

        /// <summary>Consecutive failure count while reconnecting.</summary>
        public int Attempt { get; }

        public TimeSpan? NextRetryIn { get; }

        /// <summary>Machine-readable cause of <see cref="BridgeState.Failed"/> (<see cref="BridgeFailureReasons"/>); null otherwise.</summary>
        public string? Reason { get; }
    }

    /// <summary>Values of <see cref="BridgeStateChangedEventArgs.Reason"/> / <c>bridge/state.reason</c>.</summary>
    public static class BridgeFailureReasons
    {
        /// <summary>omp has no model provider (no login, no API key): it is not retried; the UI offers the engine setup.</summary>
        public const string SetupRequired = "setupRequired";

        /// <summary>
        /// The workspace has engine configuration (settings, MCP servers, tool code...) the user has not trusted yet: omp was not started,
        /// and is not retried; the chat shows what would run and lets the user trust it (<c>workspace/trustState</c>).
        /// </summary>
        public const string WorkspaceTrustRequired = "workspaceTrustRequired";

        /// <summary>omp refused the <c>--model</c> it was started with (not in its model list any more): the host starts it again without it.</summary>
        public const string ModelNotFound = "modelNotFound";
    }

    public sealed class OmpFrameEventArgs : EventArgs
    {
        public OmpFrameEventArgs(OmpFrame frame)
        {
            Frame = frame;
        }

        public OmpFrame Frame { get; }
    }

    /// <summary>Result of a successful handshake.</summary>
    public sealed class OmpHandshakeInfo
    {
        public int ProtocolVersion { get; set; } = 1;

        /// <summary>Host tool names as registered by omp (<c>set_host_tools</c> response).</summary>
        public IReadOnlyList<string> RegisteredHostTools { get; set; } = Array.Empty<string>();

        /// <summary>Raw <c>get_state</c> payload (model, thinking level, session, context usage...).</summary>
        public JObject? State { get; set; }
    }

    /// <summary>Everything needed to spawn one omp process.</summary>
    public sealed class OmpLaunchOptions
    {
        public string ExecutablePath { get; set; } = string.Empty;

        /// <summary>Arguments without the executable; quoted by the launcher.</summary>
        public IList<string> Arguments { get; } = new List<string>();

        /// <summary>Solution root (design: 작업 디렉터리 = 솔루션 루트).</summary>
        public string WorkingDirectory { get; set; } = string.Empty;

        /// <summary>Extra environment variables (POMP_*, omp settings).</summary>
        public IDictionary<string, string> Environment { get; } = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        public TimeSpan HandshakeTimeout { get; set; } = TimeSpan.FromSeconds(30);
    }

    /// <summary>A running omp process (abstracted for tests; the real one kills the whole process tree).</summary>
    public interface IOmpProcess : IDisposable
    {
        int Id { get; }

        /// <summary>UTF-8 NDJSON commands go here.</summary>
        Stream StandardInput { get; }

        /// <summary>UTF-8 NDJSON frames come from here.</summary>
        Stream StandardOutput { get; }

        /// <summary>Diagnostics only (logged, never parsed as protocol).</summary>
        Stream StandardError { get; }

        bool HasExited { get; }

        int? ExitCode { get; }

        event EventHandler? Exited;

        /// <summary>Force-terminates the process and its children.</summary>
        void Kill();
    }

    public interface IOmpProcessLauncher
    {
        IOmpProcess Start(OmpLaunchOptions options);
    }

    /// <summary>
    /// Owns the omp <c>--mode rpc</c> child process (design: OmpProcessBridge): spawn, NDJSON framing,
    /// handshake, command/response correlation, crash detection and back-off restarts.
    /// Events are raised on background threads; consumers marshal to the UI thread themselves.
    /// </summary>
    public interface IOmpProcessBridge : IDisposable
    {
        BridgeState State { get; }

        /// <summary>Set while <see cref="BridgeState.Running"/>.</summary>
        OmpHandshakeInfo? Handshake { get; }

        event EventHandler<BridgeStateChangedEventArgs>? StateChanged;

        /// <summary>Every frame except <c>response</c> frames (those complete <see cref="SendCommandAsync"/>).</summary>
        event EventHandler<OmpFrameEventArgs>? FrameReceived;

        /// <summary>Lines omp writes to stderr.</summary>
        event EventHandler<string>? DiagnosticReceived;

        /// <summary>Spawns omp and completes after the handshake (or throws).</summary>
        Task StartAsync(CancellationToken cancellationToken);

        /// <summary>Closes stdin, waits briefly for a clean exit, then kills when <paramref name="force"/> or on timeout.</summary>
        Task StopAsync(bool force, CancellationToken cancellationToken);

        /// <summary>Stop + start, resetting the failure counter (user "restart" action).</summary>
        Task RestartAsync(CancellationToken cancellationToken);

        /// <summary>Sends a command, assigning an id when missing, and awaits its <c>response</c> frame.</summary>
        Task<ResponseFrame> SendCommandAsync(JObject command, TimeSpan? timeout, CancellationToken cancellationToken);

        /// <summary>Sends a frame that has no response (extension_ui_response, host_tool_result...).</summary>
        Task SendFrameAsync(JObject frame, CancellationToken cancellationToken);
    }
}
