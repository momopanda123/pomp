using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Pomp.Bridge;
using Pomp.Diagnostics;
using Pomp.Internal;
using Pomp.Json;
using Pomp.Transport;

namespace Pomp.AgentClients
{
    /// <summary>A JSON-RPC error. Error data and request payloads are deliberately not included.</summary>
    internal sealed class AcpRpcException : Exception
    {
        public AcpRpcException(int code, string message) : base(message)
        {
            Code = code;
        }

        public int Code { get; }
    }

    /// <summary>
    /// Owns one already-started ACP process and its JSON-RPC NDJSON channel. Initialization is the caller's job.
    /// Notifications are delivered in wire order; handlers must not block. Server requests run independently
    /// of the reader, including handlers that block before returning their task.
    /// </summary>
    internal sealed class AcpConnection : IDisposable
    {
        private static readonly TimeSpan FrameWriteTimeout = TimeSpan.FromSeconds(30);
        private static readonly TimeSpan ExitDrainTimeout = TimeSpan.FromSeconds(2);
        private readonly IOmpProcess _process;
        private readonly IPompLogger _logger;
        private readonly Stream _input;
        private readonly NdjsonLineReader _stdout;
        private readonly NdjsonLineReader _stderr;
        private readonly object _stateGate = new object();
        private Task _writeTail = Task.CompletedTask;
        private readonly CancellationTokenSource _stop = new CancellationTokenSource();
        private readonly Dictionary<string, TaskCompletionSource<JToken>> _pending = new Dictionary<string, TaskCompletionSource<JToken>>(StringComparer.Ordinal);
        private readonly Dictionary<string, InboundRequest> _inbound = new Dictionary<string, InboundRequest>(StringComparer.Ordinal);
        private readonly TaskCompletionSource<bool> _completion = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource<int?> _exit = new TaskCompletionSource<int?>(TaskCreationOptions.RunContinuationsAsynchronously);
        private Exception? _terminalError;
        private long _nextId;

        public AcpConnection(IOmpProcess process, IPompLogger logger)
        {
            _process = process ?? throw new ArgumentNullException(nameof(process));
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
            _input = process.StandardInput;
            _stdout = new NdjsonLineReader(process.StandardOutput);
            _stderr = new NdjsonLineReader(process.StandardError, 64 * 1024, strictUtf8: false, truncateOversized: true);
            _process.Exited += OnProcessExited;
            if (_process.HasExited) OnProcessExited(_process, EventArgs.Empty);
            var stdoutTask = Task.Run(ReadStdoutAsync);
            Task.Run(ReadStderrAsync).Observe();
            WatchExitAsync(stdoutTask).Observe();
        }

        public event Action<JObject>? Notification;

        public Func<string, JObject, CancellationToken, Task<JToken>>? RequestHandler { get; set; }

        /// <summary>Faults on channel loss; explicit disposal completes normally. All outstanding calls settle first.</summary>
        public Task Completion => _completion.Task;

        /// <summary>
        /// Cancels the local waiter without reusing its id or truncating an in-flight frame. Session cancellation is
        /// a separate ACP operation; callers can explicitly send session/cancel or the optional $/cancel_request.
        /// </summary>
        public async Task<JToken> CallAsync(string method, JObject parameters, CancellationToken token)
        {
            ValidateCall(method, parameters);
            token.ThrowIfCancellationRequested();
            var id = "pomp-" + Interlocked.Increment(ref _nextId).ToString(CultureInfo.InvariantCulture);
            var key = "s:" + id;
            var pending = new TaskCompletionSource<JToken>(TaskCreationOptions.RunContinuationsAsynchronously);
            lock (_stateGate)
            {
                ThrowIfEnded();
                _pending.Add(key, pending);
            }

            using (token.Register(() => CancelPending(key, token)))
            {
                SendCallAsync(key, NewMessage(method, parameters, id), token).Observe();
                return await pending.Task.ConfigureAwait(false);
            }
        }

        public async Task NotifyAsync(string method, JObject parameters, CancellationToken token)
        {
            ValidateCall(method, parameters);
            token.ThrowIfCancellationRequested();
            var write = WriteFrameAsync(NewMessage(method, parameters), token);
            // A cancelled caller stops waiting, but a byte copy that already started must finish or kill the channel.
            write.Observe();
            await write.WithCancellation(token).ConfigureAwait(false);
        }

        public void Dispose() => End(new ObjectDisposedException(nameof(AcpConnection)), disposed: true);

        private static void ValidateCall(string method, JObject parameters)
        {
            if (string.IsNullOrEmpty(method)) throw new ArgumentException("An ACP method is required.", nameof(method));
            if (parameters == null) throw new ArgumentNullException(nameof(parameters));
        }

        private static JObject NewMessage(string method, JObject parameters, string? id = null)
        {
            var message = new JObject { ["jsonrpc"] = "2.0", ["method"] = method, ["params"] = parameters };
            if (id != null) message["id"] = id;
            return message;
        }

        private async Task SendCallAsync(string key, JObject message, CancellationToken token)
        {
            try
            {
                await WriteFrameAsync(message, token).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                lock (_stateGate)
                {
                    if (_pending.TryGetValue(key, out var pending))
                    {
                        _pending.Remove(key);
                        if (ex is OperationCanceledException && token.IsCancellationRequested) pending.TrySetCanceled(token);
                        else pending.TrySetException(ex);
                    }
                }
            }
        }

        private void CancelPending(string key, CancellationToken token)
        {
            lock (_stateGate)
            {
                if (_pending.TryGetValue(key, out var pending))
                {
                    _pending.Remove(key);
                    pending.TrySetCanceled(token);
                }
            }
        }

        private Task WriteFrameAsync(JObject message, CancellationToken token)
        {
            var released = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            Task predecessor;
            lock (_stateGate)
            {
                predecessor = _writeTail;
                _writeTail = released.Task;
            }
            return WriteQueuedFrameAsync(predecessor, released, message, token);
        }

        private async Task WriteQueuedFrameAsync(Task predecessor, TaskCompletionSource<bool> released, JObject message, CancellationToken token)
        {
            try
            {
                // Enrol synchronously, in invocation order. A cancelled queued frame must not release its
                // successor ahead of a still-writing predecessor (notably prompt followed by session/cancel).
                await predecessor.WithCancellation(_stop.Token).ConfigureAwait(false);
                ThrowIfEnded();
                token.ThrowIfCancellationRequested();
                // Reuse native UTF-8/line-separator encoding, but enforce ACP's unchunked frame bound.
                var bytes = NdjsonWriter.Encode(message);
                if (bytes.Length - 1 > NdjsonLineReader.DefaultMaxLineBytes)
                    throw new ArgumentException("The ACP frame exceeds the 8 MiB limit.", nameof(message));
                token.ThrowIfCancellationRequested();
                using (var deadline = CancellationTokenSource.CreateLinkedTokenSource(_stop.Token))
                {
                    deadline.CancelAfter(FrameWriteTimeout);
                    var write = WriteBytesAsync(bytes);
                    write.Observe();
                    try
                    {
                        await write.WithCancellation(deadline.Token).ConfigureAwait(false);
                    }
                    catch (OperationCanceledException) when (_stop.IsCancellationRequested)
                    {
                        ThrowIfEnded();
                        throw;
                    }
                    catch (Exception)
                    {
                        // Even an error after a partial copy makes the stream unusable. Never retry that frame.
                        var error = new IOException("The ACP input stream failed or stopped accepting a complete frame.");
                        End(error);
                        throw error;
                    }
                }
            }
            catch (OperationCanceledException) when (_stop.IsCancellationRequested)
            {
                ThrowIfEnded();
                throw;
            }
            finally
            {
                released.TrySetResult(true);
            }
        }

        private async Task WriteBytesAsync(byte[] bytes)
        {
            await _input.WriteAsync(bytes, 0, bytes.Length, CancellationToken.None).ConfigureAwait(false);
            await _input.FlushAsync(CancellationToken.None).ConfigureAwait(false);
        }

        private async Task ReadStdoutAsync()
        {
            try
            {
                while (!_stop.IsCancellationRequested)
                {
                    var line = await _stdout.ReadLineAsync(_stop.Token).ConfigureAwait(false);
                    if (line.Kind == NdjsonLineKind.EndOfStream)
                    {
                        End(new EndOfStreamException("The ACP agent closed its output stream."));
                        return;
                    }
                    if (line.Kind != NdjsonLineKind.Line)
                        throw new IOException("The ACP agent sent an oversized or invalid UTF-8 frame.");
                    HandleFrame(PompJson.ParseObject(line.Text));
                }
            }
            catch (OperationCanceledException) when (_stop.IsCancellationRequested) { }
            catch (Exception)
            {
                // JsonReaderException can quote arbitrary configuration/auth content; never expose its text or inner error.
                End(new IOException("The ACP output stream failed or contained an invalid JSON-RPC frame."));
            }
        }

        private void HandleFrame(JObject frame)
        {
            if ((string?)frame["jsonrpc"] != "2.0") throw new IOException("Invalid JSON-RPC version.");
            var methodToken = frame["method"];
            if (methodToken != null)
            {
                if (methodToken.Type != JTokenType.String || string.IsNullOrEmpty((string?)methodToken)
                    || frame.Property("result") != null || frame.Property("error") != null)
                    throw new IOException("Invalid JSON-RPC method.");
                var method = (string)methodToken!;
                var id = frame["id"];
                if (id != null)
                {
                    var key = IdKey(id);
                    if (frame["params"] != null && !(frame["params"] is JObject))
                    {
                        WriteFrameAsync(ErrorResponse(id, -32602, "Expected object parameters."), _stop.Token).Observe();
                        return;
                    }
                    var request = new InboundRequest();
                    lock (_stateGate)
                    {
                        if (_terminalError != null) { request.Dispose(); return; }
                        if (_inbound.ContainsKey(key))
                        {
                            request.Dispose();
                            throw new IOException("The ACP agent reused an outstanding request id.");
                        }
                        _inbound.Add(key, request);
                    }
                    var handler = RequestHandler;
                    Task.Run(() => HandleRequestAsync(key, id, method, (JObject?)frame["params"] ?? new JObject(), request, handler)).Observe();
                    return;
                }

                if (method == "$/cancel_request")
                {
                    var cancelledId = (frame["params"] as JObject)?["requestId"];
                    if (cancelledId == null) return;
                    InboundRequest? request;
                    lock (_stateGate) _inbound.TryGetValue(IdKey(cancelledId), out request);
                    if (request != null) Task.Run(request.Cancel).Observe();
                    return;
                }

                try { Notification?.Invoke(frame); }
                catch (Exception) { Log(PompLogLevel.Warning, "ACP notification handler failed."); }
                return;
            }

            var responseKey = IdKey(frame["id"]);
            var hasResult = frame.Property("result") != null;
            var hasError = frame.Property("error") != null;
            if (hasResult == hasError) throw new IOException("Invalid JSON-RPC response.");
            AcpRpcException? error = null;
            if (hasError)
            {
                var details = frame["error"] as JObject;
                if (details == null || details["code"]?.Type != JTokenType.Integer || details["message"]?.Type != JTokenType.String)
                    throw new IOException("Invalid JSON-RPC error.");
                error = new AcpRpcException((int)details["code"]!, (string)details["message"]!);
            }

            lock (_stateGate)
            {
                if (!_pending.TryGetValue(responseKey, out var pending)) return; // Cancelled/late responses never become notifications.
                _pending.Remove(responseKey);
                if (error != null) pending.TrySetException(error);
                else pending.TrySetResult(frame["result"]!);
            }
        }

        private async Task HandleRequestAsync(string key, JToken id, string method, JObject parameters,
            InboundRequest request, Func<string, JObject, CancellationToken, Task<JToken>>? handler)
        {
            try
            {
                JObject response;
                try
                {
                    request.Token.ThrowIfCancellationRequested();
                    if (handler == null) throw new AcpRpcException(-32601, "Method not found.");
                    var work = handler(method, parameters, request.Token);
                    work.Observe();
                    var result = await work.WithCancellation(request.Token).ConfigureAwait(false);
                    request.Token.ThrowIfCancellationRequested();
                    response = new JObject { ["jsonrpc"] = "2.0", ["id"] = id, ["result"] = result ?? JValue.CreateNull() };
                }
                catch (OperationCanceledException) when (request.Token.IsCancellationRequested)
                {
                    response = ErrorResponse(id, -32800, "Request cancelled.");
                }
                catch (AcpRpcException ex)
                {
                    response = ErrorResponse(id, ex.Code, ex.Message);
                }
                catch (Exception)
                {
                    Log(PompLogLevel.Warning, "ACP client request handler failed.");
                    response = ErrorResponse(id, -32603, "Client request handler failed.");
                }

                await WriteFrameAsync(response, _stop.Token).ConfigureAwait(false);
            }
            catch (Exception)
            {
                // Writes close the connection on I/O failure. An oversized handler result also cannot be silently dropped.
                End(new IOException("The ACP client could not send a server-request response."));
            }
            finally
            {
                lock (_stateGate) _inbound.Remove(key);
                request.Dispose();
            }
        }

        private static JObject ErrorResponse(JToken id, int code, string message) => new JObject
        {
            ["jsonrpc"] = "2.0", ["id"] = id,
            ["error"] = new JObject { ["code"] = code, ["message"] = message },
        };

        private static string IdKey(JToken? id)
        {
            if (id?.Type == JTokenType.String) return "s:" + (string?)id;
            if (id?.Type == JTokenType.Integer) return "n:" + id.ToString(Formatting.None);
            throw new IOException("Invalid ACP request id.");
        }

        private async Task ReadStderrAsync()
        {
            try
            {
                while (!_stop.IsCancellationRequested)
                {
                    var line = await _stderr.ReadLineAsync(_stop.Token).ConfigureAwait(false);
                    if (line.Kind == NdjsonLineKind.EndOfStream) return;
                    if (line.Kind == NdjsonLineKind.Line)
                        Log(PompLogLevel.Debug, "ACP stderr: " + line.Text + (line.Truncated ? " ...[truncated]" : string.Empty));
                }
            }
            catch (OperationCanceledException) when (_stop.IsCancellationRequested) { }
            catch (Exception) { Log(PompLogLevel.Debug, "ACP stderr closed."); }
        }

        private void OnProcessExited(object? sender, EventArgs args)
        {
            int? code = null;
            try { code = _process.ExitCode; }
            catch (Exception) { }
            _exit.TrySetResult(code);
        }

        private async Task WatchExitAsync(Task stdoutTask)
        {
            try
            {
                var code = await _exit.Task.WithCancellation(_stop.Token).ConfigureAwait(false);
                // Drain already-written responses, but a descendant holding stdout open must not strand the connection.
                await AsyncUtil.WaitForAsync(stdoutTask, ExitDrainTimeout).ConfigureAwait(false);
                End(new IOException("The ACP agent exited" + (code.HasValue ? " (code " + code.Value.ToString(CultureInfo.InvariantCulture) + ")" : string.Empty) + "."));
            }
            catch (OperationCanceledException) when (_stop.IsCancellationRequested) { }
        }

        private void ThrowIfEnded()
        {
            var error = Volatile.Read(ref _terminalError);
            if (error != null) throw error;
        }

        private void End(Exception error, bool disposed = false)
        {
            List<InboundRequest> inbound;
            lock (_stateGate)
            {
                if (_terminalError != null) return;
                Volatile.Write(ref _terminalError, error);
                foreach (var pending in _pending.Values) pending.TrySetException(error);
                _pending.Clear();
                inbound = new List<InboundRequest>(_inbound.Values);
                _inbound.Clear();
            }

            _process.Exited -= OnProcessExited;
            _stop.Cancel();
            // User cancellation callbacks are not allowed to hold up the reader or process-tree teardown.
            foreach (var request in inbound) Task.Run(request.Cancel).Observe();
            try { _process.Kill(); }
            catch (Exception) { Log(PompLogLevel.Warning, "ACP process termination failed."); }
            try { _process.Dispose(); }
            catch (Exception) { Log(PompLogLevel.Warning, "ACP process disposal failed."); }
            if (disposed) _completion.TrySetResult(true);
            else _completion.TrySetException(error);
        }

        private void Log(PompLogLevel level, string message)
        {
            try { _logger.Log(level, message); }
            catch (Exception) { /* A diagnostic sink must not strand RPC waiters. */ }
        }

        private sealed class InboundRequest : IDisposable
        {
            private readonly object _gate = new object();
            private readonly CancellationTokenSource _source = new CancellationTokenSource();
            private bool _disposed;

            public InboundRequest() { Token = _source.Token; }
            public CancellationToken Token { get; }

            public void Cancel()
            {
                lock (_gate)
                {
                    if (_disposed) return;
                    try { _source.Cancel(); }
                    catch (AggregateException) { /* Consumer callbacks cannot undo cancellation. */ }
                }
            }

            public void Dispose()
            {
                lock (_gate)
                {
                    if (_disposed) return;
                    _disposed = true;
                    _source.Dispose();
                }
            }
        }
    }
}
