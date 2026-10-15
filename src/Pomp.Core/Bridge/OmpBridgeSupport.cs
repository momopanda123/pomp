using System;
using Pomp.Transport;

namespace Pomp.Bridge
{
    /// <summary>The omp bridge could not carry out a request: omp is not running, the process ended, the handshake failed...</summary>
    public sealed class OmpBridgeException : Exception
    {
        public OmpBridgeException(string message)
            : base(message)
        {
        }

        public OmpBridgeException(string message, Exception innerException)
            : base(message, innerException)
        {
        }

        /// <summary>A refusal the UI reacts to specifically (<see cref="BridgeFailureReasons"/>); not retried.</summary>
        public OmpBridgeException(string message, string reason)
            : base(message)
        {
            Reason = reason;
        }

        /// <summary>Set for a launch refusal with its own UI (<see cref="BridgeFailureReasons"/>), null otherwise.</summary>
        public string? Reason { get; }
    }

    /// <summary>Details of an ended omp process (see <see cref="OmpProcessBridge"/>'s internal <c>ConnectionEnded</c> event).</summary>
    internal sealed class ConnectionEndedEventArgs : EventArgs
    {
        public ConnectionEndedEventArgs(string message, bool stoppedByHost)
        {
            Message = message;
            StoppedByHost = stoppedByHost;
        }

        /// <summary>Exit code and last stderr line, e.g. "omp exited with code 3: boom".</summary>
        public string Message { get; }

        /// <summary>The bridge itself stopped the process (stop/restart/dispose), as opposed to a crash.</summary>
        public bool StoppedByHost { get; }
    }

    /// <summary>
    /// Tunables of <see cref="OmpProcessBridge"/>. The defaults are the design values; tests shrink the delays.
    /// </summary>
    internal sealed class OmpBridgeOptions
    {
        /// <summary>Timeout of <see cref="Pomp.Bridge.IOmpProcessBridge.SendCommandAsync"/> when the caller passes none.</summary>
        public TimeSpan DefaultCommandTimeout { get; set; } = TimeSpan.FromSeconds(30);

        /// <summary>
        /// How long writing one frame to omp's stdin may take. A write that does not finish means omp stopped reading its input;
        /// the frame may be half written, so the connection is killed (and restarted by the usual recovery) instead of being reused.
        /// </summary>
        public TimeSpan FrameWriteTimeout { get; set; } = TimeSpan.FromSeconds(30);

        /// <summary>
        /// Heartbeat of a running omp: after this long without any output on stdout the bridge asks omp for its state
        /// (<c>get_state</c>); streaming output keeps it quiet. Zero or negative disables the heartbeat.
        /// </summary>
        public TimeSpan HeartbeatInterval { get; set; } = TimeSpan.FromSeconds(30);

        /// <summary>How long one heartbeat waits for omp's answer (not shorter than <see cref="FrameWriteTimeout"/>: the wait includes the writer).</summary>
        public TimeSpan HeartbeatTimeout { get; set; } = TimeSpan.FromSeconds(30);

        /// <summary>Unanswered heartbeats in a row, with no other output in between, after which omp counts as hung and is restarted.</summary>
        public int HeartbeatMisses { get; set; } = 3;

        /// <summary>Consecutive failed start attempts / early crashes before the bridge gives up (<see cref="BridgeState.Failed"/>).</summary>
        public int MaxConsecutiveFailures { get; set; } = 3;

        /// <summary>First back-off delay; doubles for every further failure.</summary>
        public TimeSpan BackoffBase { get; set; } = TimeSpan.FromSeconds(1);

        public TimeSpan BackoffCap { get; set; } = TimeSpan.FromSeconds(30);

        /// <summary>Time in <see cref="BridgeState.Running"/> after which the failure counter is considered reset.</summary>
        public TimeSpan StableRunThreshold { get; set; } = TimeSpan.FromSeconds(60);

        /// <summary>How long a graceful stop waits for omp to exit after stdin was closed.</summary>
        public TimeSpan GracefulStopTimeout { get; set; } = TimeSpan.FromSeconds(3);

        /// <summary>Grace after stdout EOF (waiting for the exit) or after the exit (draining stdout).</summary>
        public TimeSpan ExitGrace { get; set; } = TimeSpan.FromSeconds(2);

        public int MaxLineBytes { get; set; } = NdjsonLineReader.DefaultMaxLineBytes;

        /// <summary>Delay before restart attempt number <paramref name="failures"/> + 1: 1s, 2s, 4s, 8s, 16s, then the cap.</summary>
        public TimeSpan ComputeBackoff(int failures)
        {
            if (failures < 1)
            {
                return TimeSpan.Zero;
            }

            var factor = 1L << Math.Min(failures - 1, 20);
            var ticks = BackoffBase.Ticks * factor;
            if (ticks < 0 || ticks > BackoffCap.Ticks)
            {
                ticks = BackoffCap.Ticks;
            }

            return TimeSpan.FromTicks(ticks);
        }
    }
}
