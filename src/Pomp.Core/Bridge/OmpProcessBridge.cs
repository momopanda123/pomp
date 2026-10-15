using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;
using Pomp.Diagnostics;
using Pomp.Hosting;
using Pomp.Internal;
using Pomp.Protocol;
using Pomp.Tools;

namespace Pomp.Bridge
{
    /// <summary>
    /// Owns the omp <c>--mode rpc</c> child process: spawn, handshake (ready, protocol v2, host tools, state), command/response
    /// correlation, crash detection and back-off restarts. The lifecycle runs as one background "cycle" per start:
    /// <c>Starting → Handshaking → Running</c>; when the process ends unexpectedly the cycle goes through
    /// <c>Reconnecting</c> (back-off 1s, 2s, 4s... capped at 30s) and starts again, until three consecutive failures
    /// (a start that never reached <c>Running</c>, or a crash within a minute of reaching it) end in <c>Failed</c>.
    /// Events are raised on background threads, never while an internal lock is held.
    /// </summary>
    public sealed class OmpProcessBridge : IOmpProcessBridge
    {
        private readonly Func<CancellationToken, Task<OmpLaunchOptions>> _optionsProvider;
        private readonly IOmpProcessLauncher _launcher;
        private readonly ICustomToolRegistry _registry;
        private readonly IPompLogger _logger;
        private readonly OmpBridgeOptions _options;

        private readonly object _gate = new object();
        private readonly System.Runtime.CompilerServices.ConditionalWeakTable<OmpFrame, OmpConnection> _frameOrigins =
            new System.Runtime.CompilerServices.ConditionalWeakTable<OmpFrame, OmpConnection>();
        private readonly SemaphoreSlim _lifecycle = new SemaphoreSlim(1, 1);
        private readonly object _eventLock = new object();
        private readonly Queue<BridgeStateChangedEventArgs> _stateEvents = new Queue<BridgeStateChangedEventArgs>();
        private bool _raisingStateEvents;

        private BridgeState _state = BridgeState.Stopped;
        private OmpHandshakeInfo? _handshake;
        private OmpConnection? _connection;
        private Cycle? _cycle;

        // Starts that arrived while a cycle that was already running reconnects: completed by the next Running, Failed or stop.
        private TaskCompletionSource<bool>? _nextRunning;
        private long _commandCounter;
        private long _cycleCounter;
        private int _disposed;

        /// <param name="optionsProvider">Called for every start attempt (also automatic restarts), so a restart picks up a new working directory or new settings. It may throw to report an unrecoverable problem (e.g. omp not found): the bridge then goes straight to <see cref="BridgeState.Failed"/> without retrying.</param>
        /// <param name="launcher">Creates the process.</param>
        /// <param name="registry">Host tools offered to omp during the handshake.</param>
        /// <param name="logger">Log sink.</param>
        public OmpProcessBridge(Func<CancellationToken, Task<OmpLaunchOptions>> optionsProvider, IOmpProcessLauncher launcher, ICustomToolRegistry registry, IPompLogger logger)
            : this(optionsProvider, launcher, registry, logger, new OmpBridgeOptions())
        {
        }

        /// <summary>Convenience overload for a synchronous options provider.</summary>
        public OmpProcessBridge(Func<OmpLaunchOptions> optionsProvider, IOmpProcessLauncher launcher, ICustomToolRegistry registry, IPompLogger logger)
            : this(WrapSync(optionsProvider), launcher, registry, logger, new OmpBridgeOptions())
        {
        }

        internal OmpProcessBridge(
            Func<CancellationToken, Task<OmpLaunchOptions>> optionsProvider,
            IOmpProcessLauncher launcher,
            ICustomToolRegistry registry,
            IPompLogger logger,
            OmpBridgeOptions options)
        {
            _optionsProvider = optionsProvider ?? throw new ArgumentNullException(nameof(optionsProvider));
            _launcher = launcher ?? throw new ArgumentNullException(nameof(launcher));
            _registry = registry ?? throw new ArgumentNullException(nameof(registry));
            _logger = logger ?? NullPompLogger.Instance;
            _options = options ?? throw new ArgumentNullException(nameof(options));
        }

        /// <inheritdoc />
        public event EventHandler<BridgeStateChangedEventArgs>? StateChanged;

        /// <inheritdoc />
        public event EventHandler<OmpFrameEventArgs>? FrameReceived;

        /// <inheritdoc />
        public event EventHandler<string>? DiagnosticReceived;

        /// <summary>
        /// A new omp process was spawned. Raised synchronously on the bridge's own thread, after the process exists and before its
        /// first frame can be read, so per-process state (pending dialogs, tool calls) can be scoped to it without races.
        /// </summary>
        internal event EventHandler? ConnectionStarted;

        /// <summary>The omp process of the last <see cref="ConnectionStarted"/> is gone (after its last frame was delivered).</summary>
        internal event EventHandler<ConnectionEndedEventArgs>? ConnectionEnded;

        /// <inheritdoc />
        public BridgeState State
        {
            get
            {
                lock (_gate)
                {
                    return _state;
                }
            }
        }

        /// <inheritdoc />
        public OmpHandshakeInfo? Handshake
        {
            get
            {
                lock (_gate)
                {
                    return _handshake;
                }
            }
        }

        /// <summary>
        /// Starts omp and completes when the handshake succeeded. Automatic retries happen inside this call: it throws an
        /// <see cref="OmpBridgeException"/> only when the bridge gave up (<see cref="BridgeState.Failed"/>), and
        /// <see cref="OperationCanceledException"/> when <see cref="StopAsync"/> intervened or the token was cancelled
        /// (a cancelled token only abandons the wait; the start keeps going until stopped).
        /// </summary>
        public async Task StartAsync(CancellationToken cancellationToken)
        {
            ThrowIfDisposed();
            Task wait;
            await _lifecycle.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                await FinishPendingStopAsync(cancellationToken).ConfigureAwait(false);
                wait = StartLocked();
            }
            finally
            {
                _lifecycle.Release();
            }

            await wait.WithCancellation(cancellationToken).ConfigureAwait(false);
        }

        /// <summary>
        /// A stop whose caller stopped waiting leaves the bridge in Stopping while its cycle still tears down and then raises
        /// ConnectionEnded. A new cycle must not overlap it: that late end would cancel the new process's approvals and tool calls.
        /// </summary>
        private async Task FinishPendingStopAsync(CancellationToken cancellationToken)
        {
            Cycle? stopping;
            lock (_gate)
            {
                stopping = _state == BridgeState.Stopping ? _cycle : null;
            }

            var task = stopping?.Task;
            if (stopping == null || task == null)
            {
                return;
            }

            await task.WithCancellation(cancellationToken).ConfigureAwait(false);
            FinishStop(stopping);
        }

        /// <inheritdoc />
        public async Task StopAsync(bool force, CancellationToken cancellationToken)
        {
            await _lifecycle.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                await StopLockedAsync(force, cancellationToken).ConfigureAwait(false);
            }
            finally
            {
                _lifecycle.Release();
            }
        }

        /// <inheritdoc />
        public async Task RestartAsync(CancellationToken cancellationToken)
        {
            ThrowIfDisposed();
            Task wait;
            await _lifecycle.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                await StopLockedAsync(false, cancellationToken).ConfigureAwait(false);
                wait = BeginCycle();
            }
            finally
            {
                _lifecycle.Release();
            }

            await wait.WithCancellation(cancellationToken).ConfigureAwait(false);
        }

        /// <inheritdoc />
        public Task<ResponseFrame> SendCommandAsync(JObject command, TimeSpan? timeout, CancellationToken cancellationToken)
        {
            var connection = GetConnectionForSend();
            return connection.SendCommandAsync(command, timeout ?? _options.DefaultCommandTimeout, cancellationToken);
        }

        /// <inheritdoc />
        public Task SendFrameAsync(JObject frame, CancellationToken cancellationToken)
        {
            var connection = GetConnectionForSend();
            return connection.SendFrameAsync(frame, cancellationToken);
        }

        /// <summary>Kills omp immediately. Does not wait and does not throw.</summary>
        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 1)
            {
                return;
            }

            Cycle? cycle;
            OmpConnection? connection;
            lock (_gate)
            {
                cycle = _cycle;
                connection = _connection;
                if (cycle != null)
                {
                    cycle.StopRequested = true;
                }

                if (_state != BridgeState.Stopped)
                {
                    ApplyTransitionLocked(BridgeState.Stopped, "The bridge was disposed.", 0, null);
                }
            }

            DrainStateEvents();
            if (cycle != null)
            {
                CancelCycle(cycle);
            }

            connection?.Kill();
            cycle?.StartTcs.TrySetCanceled();
        }

        // ---- lifecycle --------------------------------------------------------------------------------------------

        private Task StartLocked()
        {
            lock (_gate)
            {
                switch (_state)
                {
                    case BridgeState.Running:
                        return Task.CompletedTask;
                    case BridgeState.Starting:
                    case BridgeState.Handshaking:
                    case BridgeState.Reconnecting:
                        if (_cycle != null)
                        {
                            // A cycle whose start completed before it crashed has no connection now: wait for this reconnect.
                            return _cycle.StartTcs.Task.IsCompleted ? NextRunningLocked() : _cycle.StartTcs.Task;
                        }

                        break;
                }
            }

            // Stopped or Failed: a start is an explicit user action, so the failure counter starts from zero.
            return BeginCycle();
        }

        /// <summary>Completes on the next <see cref="BridgeState.Running"/>, faults on Failed, is cancelled by a stop (lock held).</summary>
        private Task NextRunningLocked()
        {
            if (_nextRunning == null)
            {
                _nextRunning = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            }

            return _nextRunning.Task;
        }

        private Task BeginCycle()
        {
            var cycle = new Cycle(Interlocked.Increment(ref _cycleCounter));
            lock (_gate)
            {
                // Dispose does not take the lifecycle lock: a start that was waiting for it must not launch an orphaned omp.
                if (Volatile.Read(ref _disposed) == 1)
                {
                    throw new ObjectDisposedException(nameof(OmpProcessBridge));
                }

                _cycle = cycle;
                ApplyTransitionLocked(BridgeState.Starting, null, 0, null);
            }

            DrainStateEvents();
            cycle.Task = Task.Run(() => RunCycleAsync(cycle));
            return cycle.StartTcs.Task;
        }

        private async Task StopLockedAsync(bool force, CancellationToken cancellationToken)
        {
            Cycle? cycle;
            OmpConnection? connection;
            bool wasFailed;
            lock (_gate)
            {
                cycle = _cycle;
                connection = _connection;
                if (cycle == null || _state == BridgeState.Stopped)
                {
                    return;
                }

                wasFailed = _state == BridgeState.Failed;
                cycle.StopRequested = true;
                if (!wasFailed)
                {
                    ApplyTransitionLocked(BridgeState.Stopping, null, 0, null);
                }
            }

            DrainStateEvents();

            if (connection != null)
            {
                if (force)
                {
                    connection.Kill();
                }
                else
                {
                    try
                    {
                        await connection.StopGracefullyAsync(_options.GracefulStopTimeout).ConfigureAwait(false);
                    }
                    catch (Exception ex)
                    {
                        // The cycle below must still be cancelled and the bridge reach Stopped.
                        _logger.Warn("Stopping omp gracefully failed (" + ex.Message + "); killing it.");
                        connection.Kill();
                    }
                }
            }

            CancelCycle(cycle);
            var task = cycle.Task;
            if (task != null)
            {
                try
                {
                    await task.WithCancellation(cancellationToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    // The caller stopped waiting, but the cycle still ends (its token is cancelled): then the bridge is Stopped.
                    task.ContinueWith(_ => FinishStop(cycle), CancellationToken.None, TaskContinuationOptions.None, TaskScheduler.Default)
                        .Forget(_logger, "omp stop");
                    throw;
                }
            }

            FinishStop(cycle);
        }

        private void CancelCycle(Cycle cycle)
        {
            try
            {
                cycle.Cts.Cancel();
            }
            catch (ObjectDisposedException)
            {
            }
            catch (AggregateException ex)
            {
                // A callback registered on the cycle token threw (all of them still ran): the stop must still complete.
                _logger.Warn("A cancellation callback of the omp start failed: " + ex.InnerException?.Message);
            }
        }

        private void FinishStop(Cycle cycle)
        {
            lock (_gate)
            {
                if (ReferenceEquals(_cycle, cycle) && _state != BridgeState.Stopped)
                {
                    ApplyTransitionLocked(BridgeState.Stopped, null, 0, null);
                }
            }

            DrainStateEvents();
        }

        internal OmpConnection CaptureConnection()
        {
            lock (_gate)
            {
                return _connection ?? throw new OmpBridgeException(PompLanguage.Pick("연결되어 있지 않아요.", "Not connected."));
            }
        }

        private OmpConnection GetConnectionForSend()
        {
            ThrowIfDisposed();
            OmpConnection? connection;
            BridgeState state;
            lock (_gate)
            {
                connection = _connection;
                state = _state;
            }

            if (connection == null || connection.HasEnded || (state != BridgeState.Running && state != BridgeState.Handshaking))
            {
                throw new OmpBridgeException(PompLanguage.Pick("연결되어 있지 않아요(상태: " + state + ").", "Not connected (state: " + state + ")."));
            }

            return connection;
        }

        private void ThrowIfDisposed()
        {
            if (Volatile.Read(ref _disposed) == 1)
            {
                throw new ObjectDisposedException(nameof(OmpProcessBridge));
            }
        }

        // ---- the start/restart cycle -------------------------------------------------------------------------------

        private async Task RunCycleAsync(Cycle cycle)
        {
            var failures = 0;
            var lastMessage = string.Empty;
            try
            {
                while (!cycle.StopRequested)
                {
                    if (failures > 0
                        && !TryTransition(cycle, BridgeState.Starting, lastMessage, failures, null))
                    {
                        break;
                    }

                    var outcome = await RunAttemptAsync(cycle).ConfigureAwait(false);
                    if (cycle.StopRequested || outcome.IsCancelled)
                    {
                        break;
                    }

                    lastMessage = outcome.Message;
                    if (outcome.Fatal)
                    {
                        failures = _options.MaxConsecutiveFailures;
                    }
                    else if (outcome.ReachedRunning && outcome.RanFor >= _options.StableRunThreshold)
                    {
                        // A long healthy run resets the counter; this crash is the first failure of a new series.
                        failures = 1;
                    }
                    else
                    {
                        failures++;
                    }

                    if (failures >= _options.MaxConsecutiveFailures)
                    {
                        var message = outcome.Fatal
                            ? outcome.Message
                            : PompLanguage.Pick("연결을 유지하지 못했어요 (연속 " + failures + "회 실패). 마지막 오류: ", "The connection could not be kept up (" + failures + " failures in a row). Last error: ") + outcome.Message;
                        // A deliberate refusal with its own UI (e.g. an untrusted workspace) is logged once, as information.
                        var refusal = outcome.Fatal ? outcome.Reason : null;
                        if (refusal != null) _logger.Info("omp was not started: " + message);
                        else _logger.Error(message);
                        if (TryTransition(cycle, BridgeState.Failed, message, failures, null, refusal))
                        {
                            cycle.StartTcs.TrySetException(refusal != null ? new OmpBridgeException(message, refusal) : new OmpBridgeException(message));
                        }
                        else
                        {
                            cycle.StartTcs.TrySetCanceled();
                        }

                        return;
                    }

                    var delay = _options.ComputeBackoff(failures);
                    _logger.Warn(lastMessage + " - restarting in " + delay.TotalSeconds.ToString("0.#") + "s (failure " + failures + " of " + _options.MaxConsecutiveFailures + ").");
                    if (!TryTransition(cycle, BridgeState.Reconnecting, lastMessage, failures, delay))
                    {
                        break;
                    }

                    try
                    {
                        await Task.Delay(delay, cycle.Cts.Token).ConfigureAwait(false);
                    }
                    catch (OperationCanceledException)
                    {
                        break;
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.Error("The omp bridge cycle failed unexpectedly", ex);
                if (TryTransition(cycle, BridgeState.Failed, "Unexpected bridge error: " + ex.Message, failures, null))
                {
                    cycle.StartTcs.TrySetException(new OmpBridgeException("Unexpected bridge error: " + ex.Message, ex));
                }
                else
                {
                    cycle.StartTcs.TrySetCanceled();
                }

                return;
            }

            // Stop requested (or the bridge was disposed) while the cycle was still trying.
            cycle.StartTcs.TrySetCanceled();
        }

        private async Task<AttemptOutcome> RunAttemptAsync(Cycle cycle)
        {
            var token = cycle.Cts.Token;
            OmpConnection? connection = null;
            var reachedRunning = false;
            var announced = false;
            var started = false;
            try
            {
                OmpLaunchOptions options;
                try
                {
                    options = await _optionsProvider(token).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (token.IsCancellationRequested)
                {
                    return AttemptOutcome.Cancelled;
                }
                catch (Exception ex)
                {
                    // A deliberate refusal with its own UI (e.g. an untrusted workspace) is not an error worth a stack trace (logged by the cycle).
                    if (!(ex is OmpBridgeException refused && refused.Reason != null)) _logger.Error("Could not prepare the omp launch", ex);
                    return AttemptOutcome.Unrecoverable(ex.Message, (ex as OmpBridgeException)?.Reason);
                }

                var tools = _registry.GetDefinitions();

                IOmpProcess process;
                try
                {
                    process = _launcher.Start(options);
                }
                catch (Exception ex) when (!(ex is OperationCanceledException))
                {
                    var missing = IsFileNotFound(ex) || ex is ArgumentException;
                    var text = missing
                        ? PompLanguage.Pick("시작하지 못했어요: '" + options.ExecutablePath + "'를 찾을 수 없거나 실행할 수 없어요 (", "Could not start: '" + options.ExecutablePath + "' was not found or cannot be run (") + ex.Message + ")"
                        : PompLanguage.Pick("시작하지 못했어요: ", "Could not start: ") + ex.Message;
                    _logger.Error(text, ex);
                    return missing ? AttemptOutcome.Unrecoverable(text) : AttemptOutcome.Failed(text);
                }

                // Frames remember the connection that read them, so replies can go back to that omp process only (OriginOf).
                OmpConnection? origin = null;
                connection = new OmpConnection(process, _options, _logger, NextCommandId, frame => RaiseFrame(frame, origin), RaiseDiagnostic);
                origin = connection;
                var registered = false;
                lock (_gate)
                {
                    if (!cycle.StopRequested)
                    {
                        _connection = connection;
                        registered = true;
                    }
                }

                if (!registered)
                {
                    connection.Kill();
                    return AttemptOutcome.Cancelled;
                }

                // Synchronous notification, strictly before the first frame can arrive: consumers scope per-process state to it.
                announced = true;
                RaiseConnectionStarted();
                connection.Start();
                started = true;
                if (!TryTransition(cycle, BridgeState.Handshaking, null, 0, null))
                {
                    connection.Kill();
                    return AttemptOutcome.Cancelled;
                }

                OmpHandshakeInfo info;
                try
                {
                    info = await connection.HandshakeAsync(tools, options.HandshakeTimeout, token).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (token.IsCancellationRequested)
                {
                    connection.Kill();
                    return AttemptOutcome.Cancelled;
                }
                catch (Exception ex)
                {
                    connection.Kill();
                    var text = PompLanguage.Pick("연결을 시작하지 못했어요: ", "The connection could not be started: ") + ex.Message;
                    _logger.Warn(text);

                    // No login / API key: every retry would fail the same way, so the user is sent to the setup instead.
                    if (ConnectionEnd.MentionsMissingModel(connection.StderrTail) || ConnectionEnd.MentionsMissingModel(new[] { ex.Message }))
                    {
                        return AttemptOutcome.Unrecoverable(
                            PompLanguage.Pick("연결된 모델 공급자가 없어요. 계정 탭에서 로그인하거나 API 키를 추가하세요. (", "No model provider is connected. Sign in or add an API key in Accounts. (") + text + ")",
                            BridgeFailureReasons.SetupRequired);
                    }

                    // The model the engine was started with is gone (catalog change, provider signed out): retrying the same arguments fails.
                    if (ConnectionEnd.MentionsUnknownModel(connection.StderrTail))
                        return AttemptOutcome.Unrecoverable(PompLanguage.Pick("설정된 모델을 쓸 수 없어요. (", "The configured model is not available. (") + text + ")", BridgeFailureReasons.ModelNotFound);

                    return AttemptOutcome.Failed(text);
                }

                var running = false;
                lock (_gate)
                {
                    if (!cycle.StopRequested && ReferenceEquals(_cycle, cycle))
                    {
                        _handshake = info;
                        ApplyTransitionLocked(BridgeState.Running, null, 0, null);
                        running = true;
                    }
                }

                DrainStateEvents();
                if (!running)
                {
                    connection.Kill();
                    return AttemptOutcome.Cancelled;
                }

                _logger.Info("omp is running (pid " + connection.ProcessId + ", protocol v" + info.ProtocolVersion + ", " + info.RegisteredHostTools.Count + " host tools).");
                var missingTools = tools.Select(tool => tool.Name).Where(name => !info.RegisteredHostTools.Contains(name)).ToList();
                if (missingTools.Count > 0)
                {
                    _logger.Warn("omp did not register the host tool(s) " + string.Join(", ", missingTools) + "; the model cannot call them.");
                }

                reachedRunning = true;
                var stopwatch = Stopwatch.StartNew();
                cycle.StartTcs.TrySetResult(true);

                var heartbeat = CancellationTokenSource.CreateLinkedTokenSource(token);
                var monitor = HeartbeatAsync(connection, heartbeat.Token);
                var end = await connection.Completion.ConfigureAwait(false);
                heartbeat.Cancel();
                // Disposed only after the loop saw the cancellation (it may still be inside a command).
                monitor.ContinueWith(_ => heartbeat.Dispose(), CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default).Forget(_logger, "omp heartbeat");
                return AttemptOutcome.Ended(reachedRunning, stopwatch.Elapsed, end.Describe());
            }
            finally
            {
                lock (_gate)
                {
                    if (connection != null && ReferenceEquals(_connection, connection))
                    {
                        _connection = null;
                    }
                }

                if (connection != null)
                {
                    if (!connection.HasEnded)
                    {
                        connection.Kill();
                    }

                    if (announced)
                    {
                        var message = PompLanguage.Pick("연결이 닫혔어요.", "The connection was closed.");
                        if (started && await AsyncUtil.WaitForAsync(connection.Completion, TimeSpan.FromSeconds(3)).ConfigureAwait(false)
                            && connection.Completion.Status == TaskStatus.RanToCompletion)
                        {
                            message = connection.Completion.Result.Describe();
                        }

                        RaiseConnectionEnded(new ConnectionEndedEventArgs(message, cycle.StopRequested));
                    }

                    connection.Dispose();
                }
            }
        }

        /// <summary>
        /// Heartbeat of a running omp. Output proves omp is alive; after <see cref="OmpBridgeOptions.HeartbeatInterval"/> without
        /// any, omp is asked for its state. When <see cref="OmpBridgeOptions.HeartbeatMisses"/> requests in a row stay unanswered
        /// and nothing else arrives either, omp counts as hung: it is killed with that reason and the usual crash recovery
        /// (back-off restarts, <see cref="BridgeState.Failed"/> after repeated failures) takes over. Never throws.
        /// </summary>
        private async Task HeartbeatAsync(OmpConnection connection, CancellationToken cancellationToken)
        {
            var interval = _options.HeartbeatInterval;
            if (interval <= TimeSpan.Zero || _options.HeartbeatMisses < 1)
            {
                return;
            }

            var misses = 0;
            try
            {
                while (!connection.IsClosed)
                {
                    var quiet = connection.SinceLastOutput;
                    if (quiet < interval)
                    {
                        misses = 0;
                        await Task.Delay(interval - quiet, cancellationToken).ConfigureAwait(false);
                        continue;
                    }

                    try
                    {
                        // Any answer counts, even a failure: omp still reads its input and answers.
                        await connection.SendCommandAsync(OmpCommands.GetState(), _options.HeartbeatTimeout, cancellationToken).ConfigureAwait(false);
                        misses = 0;
                    }
                    catch (TimeoutException)
                    {
                        if (connection.SinceLastOutput < interval)
                        {
                            misses = 0; // Other output arrived meanwhile.
                            continue;
                        }

                        misses++;
                        if (misses >= _options.HeartbeatMisses)
                        {
                            var reason = PompLanguage.Pick("응답이 없어요: " + Seconds(connection.SinceLastOutput) + " 동안 출력이 없고 " + misses + "번의 상태 요청에 답이 없어요", "it stopped responding: no output for " + Seconds(connection.SinceLastOutput) + " and " + misses + " unanswered state requests");
                            _logger.Error("omp " + reason + "; stopping this omp process so that it can be restarted.");
                            connection.Kill(reason);
                            return;
                        }

                        _logger.Warn("omp did not answer a heartbeat within " + Seconds(_options.HeartbeatTimeout) + " (" + misses + " of " + _options.HeartbeatMisses + " before a restart).");
                    }
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
            }
            catch (OmpBridgeException)
            {
                // The connection ended meanwhile; the cycle reports how.
            }
            catch (Exception ex)
            {
                _logger.Warn("The omp heartbeat stopped: " + ex.Message);
            }
        }

        private static string Seconds(TimeSpan span) => ((long)span.TotalSeconds).ToString(System.Globalization.CultureInfo.InvariantCulture) + "s";

        private static bool IsFileNotFound(Exception exception)
        {
            if (exception is FileNotFoundException || exception is DirectoryNotFoundException)
            {
                return true;
            }

            // ERROR_FILE_NOT_FOUND (2) / ERROR_PATH_NOT_FOUND (3) from CreateProcess.
            return exception is Win32Exception win32 && (win32.NativeErrorCode == 2 || win32.NativeErrorCode == 3);
        }

        // ---- state machine + events -------------------------------------------------------------------------------

        private bool TryTransition(Cycle cycle, BridgeState state, string? message, int attempt, TimeSpan? nextRetryIn, string? reason = null)
        {
            lock (_gate)
            {
                if (cycle.StopRequested || !ReferenceEquals(_cycle, cycle))
                {
                    return false;
                }

                ApplyTransitionLocked(state, message, attempt, nextRetryIn, reason);
            }

            DrainStateEvents();
            return true;
        }

        /// <summary>Must be called with <see cref="_gate"/> held; the caller drains the event queue after releasing the lock.</summary>
        private void ApplyTransitionLocked(BridgeState state, string? message, int attempt, TimeSpan? nextRetryIn, string? reason = null)
        {
            _state = state;
            if (state != BridgeState.Running)
            {
                _handshake = null;
            }

            var waiting = _nextRunning;
            if (waiting != null && state != BridgeState.Starting && state != BridgeState.Handshaking && state != BridgeState.Reconnecting)
            {
                _nextRunning = null;
                if (state == BridgeState.Running)
                {
                    waiting.TrySetResult(true);
                }
                else if (state == BridgeState.Failed)
                {
                    waiting.TrySetException(new OmpBridgeException(message ?? PompLanguage.Pick("시작하지 못했어요.", "Could not start.")));
                }
                else
                {
                    waiting.TrySetCanceled(); // Stopping or Stopped: a stop intervened.
                }
            }

            lock (_eventLock)
            {
                _stateEvents.Enqueue(new BridgeStateChangedEventArgs(state, message, attempt, nextRetryIn, reason));
            }
        }

        private void DrainStateEvents()
        {
            lock (_eventLock)
            {
                if (_raisingStateEvents)
                {
                    return;
                }

                _raisingStateEvents = true;
            }

            while (true)
            {
                BridgeStateChangedEventArgs next;
                lock (_eventLock)
                {
                    if (_stateEvents.Count == 0)
                    {
                        _raisingStateEvents = false;
                        return;
                    }

                    next = _stateEvents.Dequeue();
                }

                var handlers = StateChanged;
                if (handlers == null)
                {
                    continue;
                }

                foreach (var handler in handlers.GetInvocationList())
                {
                    try
                    {
                        ((EventHandler<BridgeStateChangedEventArgs>)handler)(this, next);
                    }
                    catch (Exception ex)
                    {
                        _logger.Error("A StateChanged handler failed", ex);
                    }
                }
            }
        }

        /// <summary>
        /// The connection (omp process) that read <paramref name="frame"/>; null for a frame this bridge did not raise. A reply to a
        /// request of omp (host tool result, dialog answer...) must go to exactly this connection: after a restart the request is
        /// gone, and the new process may reuse its id.
        /// </summary>
        internal OmpConnection? OriginOf(OmpFrame frame) =>
            frame != null && _frameOrigins.TryGetValue(frame, out var origin) ? origin : null;

        private void RaiseFrame(OmpFrame frame, OmpConnection? origin)
        {
            // Lines still buffered from a process that ended or is being killed are dropped: consumers have already treated it
            // as gone (ConnectionEnded may have reset the session), and a late agent_start or request would bring it back.
            if (origin != null && origin.IsClosed)
            {
                return;
            }

            if (origin != null)
            {
                _frameOrigins.Add(frame, origin);
            }

            var handlers = FrameReceived;
            if (handlers == null)
            {
                return;
            }

            var args = new OmpFrameEventArgs(frame);
            foreach (var handler in handlers.GetInvocationList())
            {
                if (origin != null && origin.IsClosed)
                {
                    return;
                }

                try
                {
                    ((EventHandler<OmpFrameEventArgs>)handler)(this, args);
                }
                catch (Exception ex)
                {
                    _logger.Error("A FrameReceived handler failed (" + frame.Type + ")", ex);
                }
            }
        }

        private void RaiseConnectionStarted()
        {
            try
            {
                ConnectionStarted?.Invoke(this, EventArgs.Empty);
            }
            catch (Exception ex)
            {
                _logger.Error("A ConnectionStarted handler failed", ex);
            }
        }

        private void RaiseConnectionEnded(ConnectionEndedEventArgs args)
        {
            try
            {
                ConnectionEnded?.Invoke(this, args);
            }
            catch (Exception ex)
            {
                _logger.Error("A ConnectionEnded handler failed", ex);
            }
        }

        private void RaiseDiagnostic(string line)
        {
            _logger.Debug("omp: " + line);
            var handlers = DiagnosticReceived;
            if (handlers == null)
            {
                return;
            }

            foreach (var handler in handlers.GetInvocationList())
            {
                try
                {
                    ((EventHandler<string>)handler)(this, line);
                }
                catch (Exception ex)
                {
                    _logger.Error("A DiagnosticReceived handler failed", ex);
                }
            }
        }

        private string NextCommandId() => "pomp-" + Interlocked.Increment(ref _commandCounter);

        private static Func<CancellationToken, Task<OmpLaunchOptions>> WrapSync(Func<OmpLaunchOptions> provider)
        {
            if (provider == null)
            {
                throw new ArgumentNullException(nameof(provider));
            }

            return _ => Task.FromResult(provider());
        }

        private sealed class Cycle
        {
            public Cycle(long id)
            {
                Id = id;
            }

            public long Id { get; }

            public CancellationTokenSource Cts { get; } = new CancellationTokenSource();

            public TaskCompletionSource<bool> StartTcs { get; } = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

            public Task? Task { get; set; }

            /// <summary>Set (under the gate) by stop/dispose; the cycle then ends without touching the state.</summary>
            public volatile bool StopRequested;
        }

        private readonly struct AttemptOutcome
        {
            private AttemptOutcome(bool cancelled, bool fatal, bool reachedRunning, TimeSpan ranFor, string message, string? reason = null)
            {
                IsCancelled = cancelled;
                Fatal = fatal;
                ReachedRunning = reachedRunning;
                RanFor = ranFor;
                Message = message;
                Reason = reason;
            }

            public bool IsCancelled { get; }

            public bool Fatal { get; }

            public bool ReachedRunning { get; }

            public TimeSpan RanFor { get; }

            public string Message { get; }

            /// <summary><see cref="BridgeFailureReasons"/> value of a fatal outcome, when it has one.</summary>
            public string? Reason { get; }

            public static AttemptOutcome Cancelled => new AttemptOutcome(true, false, false, TimeSpan.Zero, string.Empty);

            public static AttemptOutcome Failed(string message) => new AttemptOutcome(false, false, false, TimeSpan.Zero, message);

            public static AttemptOutcome Unrecoverable(string message, string? reason = null) => new AttemptOutcome(false, true, false, TimeSpan.Zero, message, reason);

            public static AttemptOutcome Ended(bool reachedRunning, TimeSpan ranFor, string message) => new AttemptOutcome(false, false, reachedRunning, ranFor, message);
        }
    }
}
