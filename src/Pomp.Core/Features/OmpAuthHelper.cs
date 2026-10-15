using System;
using System.Globalization;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;
using Pomp.Bridge;
using Pomp.Diagnostics;
using Pomp.Internal;
using Pomp.Protocol;

namespace Pomp.Features
{
    /// <summary>The auth helper omp process could not be started or did not become ready (the process, not a command, failed).</summary>
    internal sealed class AuthHelperStartException : Exception
    {
        public AuthHelperStartException(string message, Exception inner) : base(message, inner) { }
    }

    /// <summary>
    /// The host-owned omp process that signs in to model providers without a terminal (docs/ui-protocol.md "Sign-in without a
    /// terminal"). omp can only start its RPC mode once a model is usable, so this process is started with a placeholder key that
    /// never leaves the machine: <c>OPENAI_BASE_URL</c> points at a closed loopback port and the helper never prompts a model. It
    /// shares the user's omp profile, so a credential it stores is the one the chat engine uses, and it writes no session or
    /// settings. It is started on demand, runs one command at a time, exits after an idle period (never during a command, so
    /// not during a sign-in), when disposed and when the caller stops it (before the engine restarts after a first sign-in).
    /// </summary>
    internal sealed class OmpAuthHelper : IDisposable
    {
        /// <summary>The placeholder that lets omp's RPC mode start without credentials (command line only).</summary>
        internal const string PlaceholderApiKey = "pomp-setup-placeholder";

        /// <summary>A closed loopback port: nothing can reach OpenAI with the placeholder key.</summary>
        internal const string ClosedBaseUrl = "http://127.0.0.1:9/v1";

        /// <summary>The helper's complete argument list (no <c>--cwd</c>, no overlay, no extension: it only handles auth commands).</summary>
        internal static readonly string[] Arguments =
        {
            "--mode", "rpc", "--no-session", "--no-extensions", "--no-skills", "--no-rules",
            "--model", "openai/gpt", "--api-key", PlaceholderApiKey,
        };

        private static readonly TimeSpan StopGrace = TimeSpan.FromSeconds(3);

        private readonly IOmpProcessLauncher _launcher;
        private readonly Func<OmpLaunchOptions> _launchFactory;
        private readonly OmpBridgeOptions _bridgeOptions;
        private readonly IPompLogger _logger;
        private readonly TimeSpan _idleTimeout;
        private readonly TimeSpan _startTimeout;
        private readonly SemaphoreSlim _gate = new SemaphoreSlim(1, 1);
        private readonly object _lock = new object();
        private readonly Timer _idle;
        private OmpConnection? _connection;
        private Action<JObject>? _uiRequestHandler;
        private long _commandCounter;
        private int _disposed;

        /// <param name="launchFactory">Builds the launch (resolves omp, prepares the working folder and environment); called for every start and
        /// may throw a <see cref="ProviderSetupException"/> that is passed on unchanged.</param>
        /// <param name="idleTimeout">How long the process may live without a command in flight.</param>
        /// <param name="startTimeout">How long a start may take until omp's ready frame.</param>
        public OmpAuthHelper(IOmpProcessLauncher launcher, Func<OmpLaunchOptions> launchFactory, OmpBridgeOptions bridgeOptions, IPompLogger logger, TimeSpan idleTimeout, TimeSpan startTimeout)
        {
            _launcher = launcher; _launchFactory = launchFactory; _bridgeOptions = bridgeOptions; _logger = logger;
            _idleTimeout = idleTimeout; _startTimeout = startTimeout;
            _idle = new Timer(OnIdle, null, Timeout.Infinite, Timeout.Infinite);
        }

        /// <summary>Receives omp's <c>extension_ui_request</c> frames (on the reader thread; it must not block). Without a handler every
        /// dialog is declined so omp never waits for an answer nobody gives.</summary>
        public void SetUiRequestHandler(Action<JObject>? handler) => Volatile.Write(ref _uiRequestHandler, handler);

        /// <summary>Removes <paramref name="handler"/> unless a newer one replaced it meanwhile.</summary>
        public void ClearUiRequestHandler(Action<JObject> handler) => Interlocked.CompareExchange(ref _uiRequestHandler, null, handler);

        /// <summary>
        /// Sends one RPC command, starting the helper when needed. Commands run one at a time; a command that does not finish in
        /// <paramref name="timeout"/> (counted from the moment it may start) ends the process: a helper that stopped answering is not reused.
        /// </summary>
        public async Task<ResponseFrame> SendAsync(JObject command, TimeSpan timeout, CancellationToken cancellationToken)
        {
            if (!await _gate.WaitAsync(timeout, cancellationToken).ConfigureAwait(false))
            {
                throw new TimeoutException("The sign-in helper is busy.");
            }

            try
            {
                ThrowIfDisposed();
                DisarmIdle();
                var connection = await EnsureConnectionAsync(cancellationToken).ConfigureAwait(false);
                try
                {
                    return await connection.SendCommandAsync(command, timeout, cancellationToken).ConfigureAwait(false);
                }
                catch (TimeoutException)
                {
                    Kill("a command timed out");
                    throw;
                }
            }
            finally
            {
                ArmIdle();
                _gate.Release();
            }
        }

        /// <summary>Writes a frame that has no response (an <c>extension_ui_response</c>); only while the process runs.</summary>
        public Task SendFrameAsync(JObject frame, CancellationToken cancellationToken)
        {
            OmpConnection? connection;
            lock (_lock) connection = _connection;
            if (connection == null || connection.IsClosed) throw new OmpBridgeException("The sign-in helper is not running.");
            return connection.SendFrameAsync(frame, cancellationToken);
        }

        /// <summary>Ends the process at once (cancel, changed credentials); the command in flight fails.</summary>
        public void Kill(string reason)
        {
            var connection = TakeConnection();
            if (connection == null) return;
            DisarmIdle();
            _logger.Info("Stopping the sign-in helper (" + reason + ").");
            EndNow(connection);
        }

        /// <summary>Closes the helper's stdin and waits for it to exit (killing it after a short grace): before the engine restarts.</summary>
        public async Task StopAsync()
        {
            var connection = TakeConnection();
            if (connection == null) return;
            DisarmIdle();
            await StopGracefullyAsync(connection).ConfigureAwait(false);
        }

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 1) return;
            try { _idle.Dispose(); }
            catch (ObjectDisposedException) { }
            var connection = TakeConnection();
            if (connection != null) EndNow(connection);
        }

        private async Task<OmpConnection> EnsureConnectionAsync(CancellationToken cancellationToken)
        {
            lock (_lock)
            {
                if (_connection != null && !_connection.IsClosed) return _connection;
            }

            var dead = TakeConnection();
            if (dead != null) EndNow(dead);

            // Creating the launch may throw a message meant for the user (omp not found): not wrapped.
            var launch = _launchFactory();
            OmpConnection connection;
            try
            {
                var process = _launcher.Start(launch);
                connection = new OmpConnection(process, _bridgeOptions, _logger, NextCommandId, OnFrame, OnDiagnostic);
            }
            catch (Exception ex) when (!(ex is OperationCanceledException))
            {
                _logger.Warn("The sign-in helper could not be started: " + ex.GetType().Name);
                throw new AuthHelperStartException("The sign-in helper could not be started.", ex);
            }

            // Visible to Kill/Dispose from now on, so a cancel during the start does not leave a process behind.
            lock (_lock) _connection = connection;
            try
            {
                connection.Start();
                await connection.WaitReadyAsync(_startTimeout, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                lock (_lock)
                {
                    if (ReferenceEquals(_connection, connection)) _connection = null;
                }

                EndNow(connection);
                if (ex is OperationCanceledException) throw;
                // The stderr text may carry a sign-in URL: only its kind is logged.
                _logger.Warn("The sign-in helper did not become ready: " + ex.GetType().Name);
                throw new AuthHelperStartException("The sign-in helper did not become ready.", ex);
            }

            _logger.Info("Started the sign-in helper (pid " + connection.ProcessId.ToString(CultureInfo.InvariantCulture) + ").");
            return connection;
        }

        private OmpConnection? TakeConnection()
        {
            lock (_lock)
            {
                var connection = _connection;
                _connection = null;
                return connection;
            }
        }

        private void EndNow(OmpConnection connection)
        {
            try { connection.Kill(); }
            catch (Exception ex) { _logger.Debug("Killing the sign-in helper failed: " + ex.GetType().Name); }
            try { connection.Dispose(); }
            catch (Exception ex) { _logger.Debug("Disposing the sign-in helper failed: " + ex.GetType().Name); }
        }

        private async Task StopGracefullyAsync(OmpConnection connection)
        {
            try { await connection.StopGracefullyAsync(StopGrace).ConfigureAwait(false); }
            catch (Exception ex) { _logger.Debug("Stopping the sign-in helper failed: " + ex.GetType().Name); }
            finally { EndNow(connection); }
        }

        private void ArmIdle()
        {
            if (Volatile.Read(ref _disposed) == 1) return;
            lock (_lock)
            {
                if (_connection == null) return;
            }

            try { _idle.Change(_idleTimeout, Timeout.InfiniteTimeSpan); }
            catch (ObjectDisposedException) { }
        }

        private void DisarmIdle()
        {
            try { _idle.Change(Timeout.Infinite, Timeout.Infinite); }
            catch (ObjectDisposedException) { }
        }

        private void OnIdle(object? state) => IdleStopAsync().Forget(_logger, "sign-in helper idle exit");

        private async Task IdleStopAsync()
        {
            // A command in flight owns the gate and re-arms the timer when it ends.
            if (!await _gate.WaitAsync(0).ConfigureAwait(false)) return;
            try
            {
                if (Volatile.Read(ref _disposed) == 1) return;
                var connection = TakeConnection();
                if (connection == null) return;
                _logger.Info("Stopping the idle sign-in helper.");
                await StopGracefullyAsync(connection).ConfigureAwait(false);
            }
            finally { _gate.Release(); }
        }

        private void OnFrame(OmpFrame frame)
        {
            if (frame.Type != OmpFrameTypes.ExtensionUiRequest) return;
            var handler = Volatile.Read(ref _uiRequestHandler);
            if (handler != null)
            {
                try { handler(frame.Json); }
                catch (Exception ex) { _logger.Warn("Handling a sign-in request of omp failed: " + ex.GetType().Name); }
                return;
            }

            DeclineDialog(frame.Json);
        }

        /// <summary>Nobody can answer: release omp from a dialog it waits on (it would otherwise wait for its own timeout).</summary>
        private void DeclineDialog(JObject request)
        {
            var method = request.GetString("method");
            var id = request.GetString("id");
            if (string.IsNullOrEmpty(id) || (method != OmpUiMethods.Select && method != OmpUiMethods.Confirm && method != OmpUiMethods.Input && method != OmpUiMethods.Editor)) return;
            try { SendFrameAsync(OmpCommands.ExtensionUiCancelled(id!), CancellationToken.None).Forget(_logger, "decline a sign-in dialog"); }
            catch (OmpBridgeException) { /* The helper ended meanwhile. */ }
        }

        // stderr may contain sign-in URLs: it is not logged.
        private static void OnDiagnostic(string line) { }

        private string NextCommandId() => "auth-" + Interlocked.Increment(ref _commandCounter).ToString(CultureInfo.InvariantCulture);

        private void ThrowIfDisposed()
        {
            if (Volatile.Read(ref _disposed) == 1) throw new ObjectDisposedException(nameof(OmpAuthHelper));
        }
    }
}
