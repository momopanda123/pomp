using System;
using System.Threading;
using Newtonsoft.Json.Linq;
using Pomp.Bridge;
using Pomp.Protocol;
using Task = System.Threading.Tasks.Task;

namespace Pomp.VisualStudio.Composition
{
    /// <summary>Inert <see cref="IOmpProcessBridge"/> for <see cref="PlaceholderSession"/>: always stopped, cannot start.</summary>
    internal sealed class StoppedOmpBridge : IOmpProcessBridge
    {
        private const string Reason = "The omp bridge is not wired yet (placeholder session).";

        public BridgeState State => BridgeState.Stopped;

        public OmpHandshakeInfo? Handshake => null;

        public event EventHandler<BridgeStateChangedEventArgs>? StateChanged
        {
            add { }
            remove { }
        }

        public event EventHandler<OmpFrameEventArgs>? FrameReceived
        {
            add { }
            remove { }
        }

        public event EventHandler<string>? DiagnosticReceived
        {
            add { }
            remove { }
        }

        public Task StartAsync(CancellationToken cancellationToken) => throw new InvalidOperationException(Reason);

        public Task StopAsync(bool force, CancellationToken cancellationToken) => Task.CompletedTask;

        public Task RestartAsync(CancellationToken cancellationToken) => throw new InvalidOperationException(Reason);

        public System.Threading.Tasks.Task<ResponseFrame> SendCommandAsync(JObject command, TimeSpan? timeout, CancellationToken cancellationToken) =>
            throw new InvalidOperationException(Reason);

        public Task SendFrameAsync(JObject frame, CancellationToken cancellationToken) => throw new InvalidOperationException(Reason);

        public void Dispose()
        {
        }
    }
}
