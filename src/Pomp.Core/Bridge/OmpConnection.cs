using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Pomp.Diagnostics;
using Pomp.Hosting;
using Pomp.Internal;
using Pomp.Protocol;
using Pomp.Tools;
using Pomp.Transport;

namespace Pomp.Bridge
{
    /// <summary>How a connection to one omp process ended.</summary>
    internal sealed class ConnectionEnd
    {
        /// <summary>Stderr lines quoted in the description (start-up errors usually span several lines; the last one is rarely the cause).</summary>
        internal const int DescribedStderrLines = 5;

        private const int MaxDescribedStderrChars = 800;

        /// <summary>omp's start-up error when no model/provider is configured.</summary>
        private const string NoModelsMarker = "No models available";

        public ConnectionEnd(int? exitCode, IReadOnlyList<string> stderrTail, string? killReason = null)
        {
            ExitCode = exitCode;
            StderrTail = stderrTail ?? Array.Empty<string>();
            KillReason = killReason;
        }

        public int? ExitCode { get; }

        /// <summary>The last non-empty stderr lines, oldest first.</summary>
        public IReadOnlyList<string> StderrTail { get; }

        /// <summary>Why the host killed the process (it stopped responding or reading its input); null when omp ended on its own or was stopped.</summary>
        public string? KillReason { get; }

        public string Describe()
        {
            var stderr = SummarizeStderr(StderrTail);
            var text = new StringBuilder();
            if (!string.IsNullOrEmpty(KillReason))
            {
                // The exit code of a killed process says nothing.
                text.Append(PompLanguage.Pick("연결이 중지됐어요(", "The connection was stopped (")).Append(KillReason).Append(')');
                if (stderr.Length > 0)
                {
                    text.Append(PompLanguage.Pick(". 마지막 오류: ", ". Last error: ")).Append(stderr);
                }
            }
            else
            {
                text.Append(PompLanguage.Pick("연결이 끊어졌어요", "The connection ended"));
                if (ExitCode.HasValue)
                {
                    text.Append(PompLanguage.Pick(" (코드 ", " (code ")).Append(ExitCode.Value).Append(')');
                }

                if (stderr.Length > 0)
                {
                    text.Append(": ").Append(stderr);
                }
            }

            if (MentionsMissingModel(StderrTail))
            {
                text.Append(PompLanguage.Pick(". 연결된 모델 공급자가 없어요. 계정 탭에서 로그인하거나 API 키를 추가한 뒤 다시 시작하세요.", ". No model provider is connected. Sign in or add an API key in Accounts, then restart."));
            }

            return text.ToString();
        }

        /// <summary>The last <see cref="DescribedStderrLines"/> non-empty lines joined with " | " (bounded).</summary>
        internal static string SummarizeStderr(IReadOnlyList<string> lines)
        {
            var shown = lines
                .Select(line => line?.Trim() ?? string.Empty)
                .Where(line => line.Length > 0)
                .ToList();
            if (shown.Count > DescribedStderrLines)
            {
                shown = shown.GetRange(shown.Count - DescribedStderrLines, DescribedStderrLines);
            }

            var summary = string.Join(" | ", shown);
            return summary.Length <= MaxDescribedStderrChars ? summary : summary.Substring(summary.Length - MaxDescribedStderrChars);
        }

        internal static bool MentionsMissingModel(IReadOnlyList<string> lines) =>
            lines.Any(line => line != null && line.IndexOf(NoModelsMarker, StringComparison.OrdinalIgnoreCase) >= 0);

        /// <summary>omp's start-up refusal of an unknown <c>--model</c>: <c>Model "provider/id" not found</c>.</summary>
        internal static bool MentionsUnknownModel(IReadOnlyList<string> lines) =>
            lines.Any(line => line != null && line.TrimStart().StartsWith("Model \"", StringComparison.Ordinal) && line.IndexOf("\" not found", StringComparison.Ordinal) > 0);
    }

    /// <summary>
    /// One live omp process: NDJSON reader loops for stdout/stderr, the serialized writer for stdin, command/response
    /// correlation and the handshake. All failure paths converge in <see cref="Completion"/>, which completes once the process
    /// has ended (exit observed and stdout drained) and never faults.
    /// </summary>
    internal sealed class OmpConnection : IDisposable
    {
        private const int StderrTailLines = 20;
        private static readonly TimeSpan MaxFiniteTimeout = TimeSpan.FromDays(20);

        private readonly IOmpProcess _process;
        private readonly OmpBridgeOptions _options;
        private readonly IPompLogger _logger;
        private readonly Func<string> _nextCommandId;
        private readonly Action<OmpFrame> _onFrame;
        private readonly Action<string> _onDiagnostic;
        private readonly NdjsonWriter _writer;
        private readonly OmpFrameReader _reader;
        private readonly ConcurrentDictionary<string, TaskCompletionSource<ResponseFrame>> _pending =
            new ConcurrentDictionary<string, TaskCompletionSource<ResponseFrame>>(StringComparer.Ordinal);
        private readonly TaskCompletionSource<ReadyFrame> _ready = new TaskCompletionSource<ReadyFrame>(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource<int?> _exit = new TaskCompletionSource<int?>(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly object _stderrLock = new object();
        private readonly Queue<string> _stderrTail = new Queue<string>();
        private readonly CancellationTokenSource _readCts = new CancellationTokenSource();
        private ReadyFrame? _readyFrame;
        private Task _stdoutTask = Task.CompletedTask;
        private Task _stderrTask = Task.CompletedTask;
        private Task<ConnectionEnd>? _completion;
        private string? _killReason;
        // Stopwatch timestamp of the last complete stdout frame (Interlocked: the x86 host cannot write a long atomically).
        private long _lastOutput;
        private int _started;
        private int _ended;
        private int _killed;
        private int _disposed;

        public OmpConnection(
            IOmpProcess process,
            OmpBridgeOptions options,
            IPompLogger logger,
            Func<string> nextCommandId,
            Action<OmpFrame> onFrame,
            Action<string> onDiagnostic)
        {
            _process = process;
            _options = options;
            _logger = logger;
            _nextCommandId = nextCommandId;
            _onFrame = onFrame;
            _onDiagnostic = onDiagnostic;
            _writer = new NdjsonWriter(process.StandardInput);
            _reader = new OmpFrameReader(process.StandardOutput, message => _logger.Warn("omp stdout: " + message), options.MaxLineBytes);
            _process.Exited += OnProcessExited;
            if (_process.HasExited)
            {
                _exit.TrySetResult(_process.ExitCode);
            }
        }

        public int ProcessId => _process.Id;

        /// <summary>Negotiated protocol version (1 until v2 was agreed).</summary>
        public int ProtocolVersion => _reader.IsProtocolV2 ? 2 : 1;

        public bool HasEnded => Volatile.Read(ref _ended) == 1;

        /// <summary>The process ended or is being killed: nothing it asked for can be answered any more.</summary>
        internal bool IsClosed => HasEnded || Volatile.Read(ref _killed) == 1;

        /// <summary>Completes when the process ended; never faults.</summary>
        public Task<ConnectionEnd> Completion => Volatile.Read(ref _completion) ?? throw new InvalidOperationException("The connection was not started.");

        /// <summary>Time since omp last wrote a complete frame to stdout (since <see cref="Start"/> when it wrote none yet).</summary>
        internal TimeSpan SinceLastOutput
        {
            get
            {
                var elapsed = Stopwatch.GetTimestamp() - Interlocked.Read(ref _lastOutput);
                return TimeSpan.FromTicks((long)(elapsed * ((double)TimeSpan.TicksPerSecond / Stopwatch.Frequency)));
            }
        }

        /// <summary>The most recent stderr lines (at most 20), oldest first.</summary>
        public IReadOnlyList<string> StderrTail
        {
            get
            {
                lock (_stderrLock)
                {
                    return _stderrTail.ToArray();
                }
            }
        }

        /// <summary>Starts the stdout/stderr reader loops.</summary>
        public void Start()
        {
            if (Interlocked.Exchange(ref _started, 1) == 1)
            {
                throw new InvalidOperationException("The connection was already started.");
            }

            Interlocked.Exchange(ref _lastOutput, Stopwatch.GetTimestamp());
            _stdoutTask = Task.Run(ReadStdoutAsync);
            _stderrTask = Task.Run(ReadStderrAsync);
            Volatile.Write(ref _completion, Task.Run(WatchAsync));
        }

        /// <summary>Ready frame → protocol v2 → host tools → state (all within <paramref name="timeout"/>).</summary>
        public async Task<OmpHandshakeInfo> HandshakeAsync(IReadOnlyList<HostToolDefinition> tools, TimeSpan timeout, CancellationToken cancellationToken)
        {
            if (timeout <= TimeSpan.Zero)
            {
                timeout = TimeSpan.FromSeconds(30);
            }

            using (var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken))
            {
                linked.CancelAfter(timeout);
                var token = linked.Token;
                var step = "the ready frame";
                try
                {
                    var ready = await _ready.Task.WithCancellation(token).ConfigureAwait(false);

                    var protocolVersion = 1;
                    if (ready.SupportedProtocolVersions.Contains(2))
                    {
                        step = "protocol negotiation";
                        var negotiated = await SendCommandAsync(OmpCommands.NegotiateProtocol(2), Timeout.InfiniteTimeSpan, token).ConfigureAwait(false);
                        if (negotiated.Success && ReadInt(negotiated.Data, "protocolVersion") == 2)
                        {
                            protocolVersion = 2;
                        }
                        else
                        {
                            _logger.Warn("omp refused protocol v2 (" + (negotiated.Error ?? "no reason") + "); staying on protocol v1 (oversized frames are truncated by omp).");
                        }
                    }

                    step = "VS tool registration";
                    var toolsResponse = await SendCommandAsync(OmpCommands.SetHostTools(tools), Timeout.InfiniteTimeSpan, token).ConfigureAwait(false);
                    if (!toolsResponse.Success)
                    {
                        throw new OmpBridgeException(PompLanguage.Pick("VS 도구를 등록하지 못했어요: ", "VS tools could not be registered: ") + (toolsResponse.Error ?? PompLanguage.Pick("알 수 없는 오류", "unknown error")));
                    }

                    var registered = ReadStringList(toolsResponse.Data, "toolNames");

                    step = "the session state";
                    var stateResponse = await SendCommandAsync(OmpCommands.GetState(), Timeout.InfiniteTimeSpan, token).ConfigureAwait(false);
                    JObject? state = null;
                    if (stateResponse.Success)
                    {
                        state = stateResponse.Data as JObject;
                    }
                    else
                    {
                        _logger.Warn("get_state failed during the handshake: " + stateResponse.Error);
                    }

                    return new OmpHandshakeInfo { ProtocolVersion = protocolVersion, RegisteredHostTools = registered, State = state };
                }
                catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
                {
                    throw new OmpBridgeException(
                        PompLanguage.Pick("연결을 시작하지 못했어요 (" + timeout.TotalSeconds.ToString("0.#") + "초 안에 응답이 없어요)", "The connection could not be started (" + timeout.TotalSeconds.ToString("0.#") + "s waiting for " + step + ")") + DescribeStderr());
                }
            }
        }

        /// <summary>
        /// Waits for the <c>ready</c> frame and nothing else (no protocol negotiation, host tools or state): enough for a short-lived
        /// helper process that is only sent a few small RPC commands.
        /// </summary>
        public async Task WaitReadyAsync(TimeSpan timeout, CancellationToken cancellationToken)
        {
            if (timeout <= TimeSpan.Zero)
            {
                timeout = TimeSpan.FromSeconds(30);
            }

            using (var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken))
            {
                linked.CancelAfter(timeout);
                try
                {
                    await _ready.Task.WithCancellation(linked.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
                {
                    throw new OmpBridgeException(PompLanguage.Pick("연결을 시작하지 못했어요 (", "The connection could not be started (") + timeout.TotalSeconds.ToString("0.#") + PompLanguage.Pick("초 안에 준비되지 않았어요)", "s without a ready signal)") + DescribeStderr());
                }
            }
        }

        /// <summary>Sends a command (assigning an id when missing) and awaits the response frame with the same id.</summary>
        public async Task<ResponseFrame> SendCommandAsync(JObject command, TimeSpan timeout, CancellationToken cancellationToken)
        {
            if (command == null)
            {
                throw new ArgumentNullException(nameof(command));
            }

            var commandType = (string?)command["type"] ?? "command";
            var id = EnsureId(command);
            var completion = new TaskCompletionSource<ResponseFrame>(TaskCreationOptions.RunContinuationsAsynchronously);
            if (!_pending.TryAdd(id, completion))
            {
                throw new InvalidOperationException("A command with id '" + id + "' is already waiting for its response.");
            }

            try
            {
                using (var deadline = new CancellationTokenSource())
                using (var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, deadline.Token))
                {
                    if (timeout != Timeout.InfiniteTimeSpan && timeout < MaxFiniteTimeout)
                    {
                        deadline.CancelAfter(timeout);
                    }

                    ThrowIfEnded();
                    var write = WriteAsync(command, linked.Token);
                    try
                    {
                        // The deadline includes the writer gate and byte copy, not only the response wait: the caller gets its timeout on
                        // time. The write itself is left to finish: a frame still waiting at the gate is dropped (the gate wait is
                        // cancelled, nothing is written), and a byte copy in progress is never cut (that would break the NDJSON stream).
                        await write.WithCancellation(deadline.Token).ConfigureAwait(false);
                    }
                    catch (OperationCanceledException) when (deadline.IsCancellationRequested)
                    {
                        // A missed deadline does not end the connection: a busy engine catches up and answers later (an unmatched reply),
                        // and one that really stopped reading its input is stopped by the frame write watchdog (FrameWriteTimeout) of
                        // the write still running. Ending it here let a short background command (a 5 s rules refresh) restart an engine
                        // that was only slow for a few seconds, e.g. while Windows Update was installing.
                        write.Observe();
                        throw;
                    }

                    return await completion.Task.WithCancellation(linked.Token).ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                throw new TimeoutException(PompLanguage.Pick("'" + commandType + "'에 대한 응답이 " + timeout.TotalSeconds.ToString("0.#") + "초 안에 오지 않았어요.", "'" + commandType + "' was not answered within " + timeout.TotalSeconds.ToString("0.#") + "s."));
            }
            finally
            {
                // Only this command's own waiter: once answered, its id may already belong to a newer command.
                ((ICollection<KeyValuePair<string, TaskCompletionSource<ResponseFrame>>>)_pending)
                    .Remove(new KeyValuePair<string, TaskCompletionSource<ResponseFrame>>(id, completion));
            }
        }

        /// <summary>Writes a frame that has no response (extension_ui_response, host_tool_result...).</summary>
        public async Task SendFrameAsync(JObject frame, CancellationToken cancellationToken)
        {
            if (frame == null)
            {
                throw new ArgumentNullException(nameof(frame));
            }

            ThrowIfEnded();
            await WriteAsync(frame, cancellationToken).ConfigureAwait(false);
        }

        /// <summary>Closes stdin, keeps draining stdout for up to <paramref name="grace"/>, then kills the process.</summary>
        public async Task StopGracefullyAsync(TimeSpan grace)
        {
            var completion = Volatile.Read(ref _completion);
            if (completion == null)
            {
                // Published to the bridge but not started, or Start has not created its watcher yet (a stop raced the start):
                // there is nothing to drain.
                Kill();
                return;
            }

            if (!await _writer.CloseAsync(TimeSpan.FromMilliseconds(500)).ConfigureAwait(false))
            {
                _logger.Warn("Could not close omp's stdin (a write is blocked); the process will be killed after the grace period.");
            }

            if (!await AsyncUtil.WaitForAsync(completion, grace).ConfigureAwait(false))
            {
                _logger.Warn("omp did not exit within " + grace.TotalSeconds.ToString("0.#") + "s after stdin was closed; killing it.");
                Kill();
                await AsyncUtil.WaitForAsync(completion, TimeSpan.FromSeconds(5)).ConfigureAwait(false);
            }
        }

        /// <summary>Force-terminates the process (and its children).</summary>
        /// <param name="reason">Why omp is killed although it did not ask to end (reported by <see cref="ConnectionEnd.Describe"/>); only the first kill counts.</param>
        public void Kill(string? reason = null)
        {
            if (Interlocked.Exchange(ref _killed, 1) == 1)
            {
                return;
            }

            // Before the process can end: WatchAsync reads it after the exit.
            Volatile.Write(ref _killReason, reason);

            // Frames still waiting for the writer must not reach a process that is being killed (or anything reusing its pipe).
            _writer.Abandon();
            try
            {
                _process.Kill();
            }
            catch (Exception ex)
            {
                _logger.Debug("Kill failed: " + ex.Message);
            }
        }

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 1)
            {
                return;
            }

            Kill();
            _process.Exited -= OnProcessExited;
            _readCts.Cancel();
            FailAll(new OmpBridgeException(PompLanguage.Pick("연결이 닫혔어요.", "The connection was closed.")));
            try
            {
                _process.Dispose();
            }
            catch (Exception ex)
            {
                _logger.Debug("Process dispose failed: " + ex.Message);
            }
        }

        private string DescribeStderr()
        {
            var tail = StderrTail;
            var summary = ConnectionEnd.SummarizeStderr(tail);
            if (summary.Length == 0)
            {
                return string.Empty;
            }

            return PompLanguage.Pick(" (오류: ", " (stderr: ") + summary + ")"
                + (ConnectionEnd.MentionsMissingModel(tail) ? PompLanguage.Pick(". 연결된 모델 공급자가 없어요. 계정 탭에서 로그인하거나 API 키를 추가하세요.", ". No model provider is connected. Sign in or add an API key in Accounts.") : string.Empty);
        }

        private string EnsureId(JObject command)
        {
            var existing = command["id"];
            if (existing != null && existing.Type != JTokenType.Null)
            {
                var text = existing.Type == JTokenType.String ? (string?)existing : existing.ToString(Formatting.None);
                if (!string.IsNullOrEmpty(text))
                {
                    if (existing.Type != JTokenType.String)
                    {
                        command["id"] = text;
                    }

                    return text!;
                }
            }

            var id = _nextCommandId();
            command["id"] = id;
            return id;
        }

        private void ThrowIfEnded()
        {
            if (HasEnded || Volatile.Read(ref _killed) == 1)
            {
                throw new OmpBridgeException(PompLanguage.Pick("연결되어 있지 않아요(프로세스가 끝났어요).", "Not connected (the process ended)."));
            }
        }

        private async Task WriteAsync(JObject frame, CancellationToken cancellationToken)
        {
            // The byte copy itself cannot be cancelled (a half written frame would break the NDJSON stream), so a write that never
            // finishes is bounded here: omp stopped reading its input, and this connection must not be used again.
            var write = _writer.WriteAsync(frame, cancellationToken);
            var deadline = _options.FrameWriteTimeout;
            if (deadline > TimeSpan.Zero && deadline < MaxFiniteTimeout && !write.IsCompleted)
            {
                using (var timer = new CancellationTokenSource())
                {
                    var finished = await Task.WhenAny(write, Task.Delay(deadline, timer.Token)).ConfigureAwait(false);
                    timer.Cancel();
                    if (finished != write)
                    {
                        write.Observe();
                        var message = PompLanguage.Pick("입력이 " + deadline.TotalSeconds.ToString("0.#") + "초 동안 읽히지 않았어요", "its input was not read for " + deadline.TotalSeconds.ToString("0.#") + "s");
                        _logger.Error(message + "; stopping this omp process.");
                        Kill(message);
                        throw new OmpBridgeException(message + ".");
                    }
                }
            }

            try
            {
                await write.ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is IOException || ex is ObjectDisposedException || ex is InvalidOperationException || ex is NotSupportedException)
            {
                throw new OmpBridgeException(PompLanguage.Pick("연결에 쓰지 못했어요: ", "Could not write to the connection: ") + ex.Message, ex);
            }
        }

        private async Task ReadStdoutAsync()
        {
            try
            {
                while (true)
                {
                    var json = await _reader.ReadFrameAsync(_readCts.Token).ConfigureAwait(false);
                    if (json == null)
                    {
                        break;
                    }

                    Interlocked.Exchange(ref _lastOutput, Stopwatch.GetTimestamp());
                    try
                    {
                        HandleFrame(json);
                    }
                    catch (Exception ex)
                    {
                        // Never let one odd frame end the reader loop (a non-string "type" must not throw here either).
                        _logger.Error("Failed to process an omp frame (" + (json["type"]?.Type == JTokenType.String ? (string?)json["type"] : "?") + ")", ex);
                    }
                }
            }
            catch (OperationCanceledException)
            {
                // Disposed while reading.
            }
            catch (Exception ex) when (ex is IOException || ex is ObjectDisposedException || ex is InvalidOperationException)
            {
                _logger.Debug("omp stdout closed: " + ex.Message);
            }
            catch (Exception ex)
            {
                _logger.Error("Unexpected error while reading omp stdout", ex);
            }
        }

        private async Task ReadStderrAsync()
        {
            try
            {
                var lines = new NdjsonLineReader(_process.StandardError, 64 * 1024, strictUtf8: false, truncateOversized: true);
                while (true)
                {
                    var line = await lines.ReadLineAsync(_readCts.Token).ConfigureAwait(false);
                    if (line.Kind == NdjsonLineKind.EndOfStream)
                    {
                        break;
                    }

                    if (line.Kind != NdjsonLineKind.Line)
                    {
                        continue;
                    }

                    var text = line.Truncated ? line.Text + " ...[truncated]" : line.Text;
                    lock (_stderrLock)
                    {
                        _stderrTail.Enqueue(text);
                        while (_stderrTail.Count > StderrTailLines)
                        {
                            _stderrTail.Dequeue();
                        }
                    }

                    try
                    {
                        _onDiagnostic(text);
                    }
                    catch (Exception ex)
                    {
                        _logger.Warn("Diagnostic handler failed: " + ex.Message);
                    }
                }
            }
            catch (OperationCanceledException)
            {
            }
            catch (Exception ex) when (ex is IOException || ex is ObjectDisposedException || ex is InvalidOperationException)
            {
                _logger.Debug("omp stderr closed: " + ex.Message);
            }
            catch (Exception ex)
            {
                _logger.Error("Unexpected error while reading omp stderr", ex);
            }
        }

        private void HandleFrame(JObject json)
        {
            var frame = new OmpFrame(json);
            switch (frame.Type)
            {
                case OmpFrameTypes.Ready when _readyFrame == null:
                    _readyFrame = SafeAs<ReadyFrame>(frame) ?? new ReadyFrame();
                    _ready.TrySetResult(_readyFrame);
                    return;
                case OmpFrameTypes.Response:
                    HandleResponse(frame);
                    return;
            }

            _onFrame(frame);
        }

        private void HandleResponse(OmpFrame frame)
        {
            var response = SafeAs<ResponseFrame>(frame);
            if (response == null)
            {
                _onFrame(frame);
                return;
            }

            if (response.Success
                && response.Command == OmpCommandTypes.NegotiateProtocol
                && ReadInt(response.Data, "protocolVersion") == 2)
            {
                // omp encodes everything after this response with v2 framing: switch before reading the next line.
                var ready = _readyFrame;
                _reader.EnableProtocolV2(ready?.MaxFrameBytes, ready?.MaxReassembledFrameBytes);
            }

            if (!string.IsNullOrEmpty(response.Id) && _pending.TryRemove(response.Id!, out var pending))
            {
                pending.TrySetResult(response);
                return;
            }

            // Late error for an already answered prompt, a "parse" failure without id, or an answer to a timed-out call.
            _onFrame(frame);
        }

        private T? SafeAs<T>(OmpFrame frame)
            where T : class
        {
            try
            {
                return frame.As<T>();
            }
            catch (Exception ex) when (ex is JsonException || ex is InvalidCastException || ex is FormatException)
            {
                _logger.Warn("Ignoring a '" + frame.Type + "' frame with an unexpected shape: " + ex.Message);
                return null;
            }
        }

        private void OnProcessExited(object? sender, EventArgs e)
        {
            int? code = null;
            try
            {
                code = _process.ExitCode;
            }
            catch (Exception)
            {
                // Handle already closed.
            }

            _exit.TrySetResult(code);
        }

        private async Task<ConnectionEnd> WatchAsync()
        {
            var exit = _exit.Task;
            var first = await Task.WhenAny(_stdoutTask, exit).ConfigureAwait(false);
            if (first == _stdoutTask)
            {
                // stdout closed: the exit should follow immediately; a process that keeps running without a channel is useless.
                if (!await AsyncUtil.WaitForAsync(exit, _options.ExitGrace).ConfigureAwait(false))
                {
                    _logger.Warn("omp closed its stdout but is still running; killing it.");
                    Kill();
                    await AsyncUtil.WaitForAsync(exit, TimeSpan.FromSeconds(5)).ConfigureAwait(false);
                }
            }
            else
            {
                // The process exited: give the reader a moment to drain what is left in the pipe.
                await AsyncUtil.WaitForAsync(_stdoutTask, _options.ExitGrace).ConfigureAwait(false);
            }

            await AsyncUtil.WaitForAsync(_stderrTask, TimeSpan.FromSeconds(1)).ConfigureAwait(false);

            int? exitCode = null;
            if (exit.Status == TaskStatus.RanToCompletion)
            {
                exitCode = exit.Result;
            }
            else
            {
                try
                {
                    exitCode = _process.ExitCode;
                }
                catch (Exception)
                {
                    // Still running or gone.
                }
            }

            var end = new ConnectionEnd(exitCode, StderrTail, Volatile.Read(ref _killReason));
            Interlocked.Exchange(ref _ended, 1);
            FailAll(new OmpBridgeException(end.Describe()));
            return end;
        }

        private void FailAll(Exception exception)
        {
            _ready.TrySetException(exception);
            foreach (var key in _pending.Keys)
            {
                if (_pending.TryRemove(key, out var pending))
                {
                    pending.TrySetException(exception);
                }
            }
        }

        private static int? ReadInt(JToken? data, string name)
        {
            var value = data?[name];
            if (value == null || (value.Type != JTokenType.Integer && value.Type != JTokenType.Float))
            {
                return null;
            }

            return (int)value;
        }

        private static IReadOnlyList<string> ReadStringList(JToken? data, string name)
        {
            var names = new List<string>();
            if (data?[name] is JArray array)
            {
                foreach (var item in array)
                {
                    var text = item.Type == JTokenType.String ? (string?)item : null;
                    if (!string.IsNullOrEmpty(text))
                    {
                        names.Add(text!);
                    }
                }
            }

            return names;
        }
    }
}
