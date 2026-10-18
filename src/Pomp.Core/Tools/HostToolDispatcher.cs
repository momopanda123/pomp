using System;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Pomp.Diagnostics;
using Pomp.Hosting;
using Pomp.Permissions;
using Pomp.Protocol;

namespace Pomp.Tools
{
    /// <summary>Raised after a host tool call finished (its result frame was handed to the sender, or was suppressed because the call was cancelled).</summary>
    public sealed class HostToolCompletedEventArgs : EventArgs
    {
        public HostToolCompletedEventArgs(string hostCallId, string toolCallId, string toolName, bool isError, bool cancelled, TimeSpan duration, TimeSpan executionDuration, JObject? details = null)
        {
            HostCallId = hostCallId;
            ToolCallId = toolCallId;
            ToolName = toolName;
            IsError = isError;
            Cancelled = cancelled;
            Duration = duration;
            ExecutionDuration = executionDuration;
            Details = details;
        }

        /// <summary>The structured details of the result sent to omp (null when cancelled or when the tool gave none).</summary>
        public JObject? Details { get; }

        public string HostCallId { get; }

        public string ToolCallId { get; }

        public string ToolName { get; }

        public bool IsError { get; }

        /// <summary>The call was cancelled by omp (<c>host_tool_cancel</c>) or by a session shutdown; no result frame was sent.</summary>
        public bool Cancelled { get; }

        /// <summary>From receipt of the call to the finished result, including any permission prompt.</summary>
        public TimeSpan Duration { get; }

        /// <summary>Time spent inside the tool's <c>ExecuteAsync</c> only (zero when it never ran).</summary>
        public TimeSpan ExecutionDuration { get; }
    }

    /// <summary>
    /// Executes <c>host_tool_call</c> frames against the <see cref="ICustomToolRegistry"/>: unknown tool → error result;
    /// permission gate for anything but <see cref="PermissionKind.None"/> (denied → error result); one cancellation source
    /// per host call id (<c>host_tool_cancel</c> or session end); progress reports become <c>host_tool_update</c>; exceptions
    /// become error results. Calls run concurrently; every call ends in exactly one <c>host_tool_result</c> unless it was cancelled.
    /// </summary>
    public sealed class HostToolDispatcher : IDisposable
    {
        private readonly ICustomToolRegistry _registry;
        private readonly IPermissionGate _gate;
        private readonly Func<JObject, CancellationToken, Task> _sendFrame;
        private readonly IPompLogger _logger;
        private readonly ConcurrentDictionary<CallKey, ActiveCall> _active = new ConcurrentDictionary<CallKey, ActiveCall>();
        private readonly CancellationTokenSource _disposed = new CancellationTokenSource();

        /// <param name="registry">Where tools are looked up.</param>
        /// <param name="gate">Permission gate consulted before tools that need approval.</param>
        /// <param name="sendFrame">Writes a frame to omp (<c>host_tool_update</c> / <c>host_tool_result</c>).</param>
        /// <param name="logger">Log sink.</param>
        public HostToolDispatcher(ICustomToolRegistry registry, IPermissionGate gate, Func<JObject, CancellationToken, Task> sendFrame, IPompLogger? logger = null)
        {
            _registry = registry ?? throw new ArgumentNullException(nameof(registry));
            _gate = gate ?? throw new ArgumentNullException(nameof(gate));
            _sendFrame = sendFrame ?? throw new ArgumentNullException(nameof(sendFrame));
            _logger = logger ?? NullPompLogger.Instance;
        }

        public event EventHandler<HostToolCompletedEventArgs>? Completed;

        /// <summary>Optional workflow admission check, repeated after approval before execution.</summary>
        internal Func<HostToolInvocation, PermissionKind, string?>? AdmissionCheck { get; set; }

        /// <summary>Number of calls currently in flight.</summary>
        public int ActiveCount => _active.Count;

        /// <summary>Runs one call to completion. Failures of the tool are reported to omp, never thrown.</summary>
        public Task HandleCallAsync(HostToolCallFrame call) => HandleCallAsync(call, null, null);

        /// <summary>Runs one call to completion. Failures of the tool are reported to omp, never thrown.</summary>
        /// <param name="call">The call.</param>
        /// <param name="origin">Identity of the omp connection that sent the call: ids of different connections never collide.</param>
        /// <param name="reply">
        /// Sends this call's <c>host_tool_update</c>/<c>host_tool_result</c> frames instead of the dispatcher's sender; bind it to the
        /// originating connection so a late result never reaches a restarted omp process.
        /// </param>
        public async Task HandleCallAsync(HostToolCallFrame call, object? origin, Func<JObject, CancellationToken, Task>? reply)
        {
            if (call == null)
            {
                throw new ArgumentNullException(nameof(call));
            }

            var send = reply ?? _sendFrame;
            var key = new CallKey(origin, call.Id);
            using (var cts = CancellationTokenSource.CreateLinkedTokenSource(_disposed.Token))
            {
                var active = new ActiveCall(cts, send, _logger, call.Id);
                if (!_active.TryAdd(key, active))
                {
                    _logger.Warn("Ignoring host_tool_call with duplicate id '" + call.Id + "'.");
                    return;
                }

                // Registered (so a host_tool_cancel that follows finds it); now leave the caller's thread, which is omp's stdout
                // reader: permission checks and the tool's synchronous start must not hold up the following frames.
                await Task.Yield();

                var total = Stopwatch.StartNew();
                var execution = new Stopwatch();
                HostToolResult? result;
                try
                {
                    result = await RunAsync(call, origin, active, execution, cts.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (cts.IsCancellationRequested)
                {
                    result = null;
                }
                catch (Exception ex)
                {
                    _logger.Error("Host tool '" + call.ToolName + "' failed", ex);
                    result = HostToolResult.Error("VS tool '" + call.ToolName + "' failed: " + ex.Message);
                }

                var cancelled = false;
                var isError = result?.IsError ?? false;
                try
                {
                    await active.Progress.CloseAsync().ConfigureAwait(false);
                    cancelled = cts.IsCancellationRequested;
                    if (result != null && !cancelled)
                    {
                        try
                        {
                            await send(OmpCommands.HostToolResult(call.Id, result), CancellationToken.None).ConfigureAwait(false);
                        }
                        catch (Exception ex)
                        {
                            _logger.Warn("Could not deliver the result of host tool '" + call.ToolName + "': " + ex.Message);
                        }
                    }
                }
                finally
                {
                    // Only after the result was handed over: a repeated id cannot start a second execution in the meantime.
                    _active.TryRemove(key, out _);
                }

                total.Stop();
                _logger.Debug("Host tool '" + call.ToolName + "' " + (cancelled ? "cancelled" : isError ? "failed" : "finished") + " in " + total.ElapsedMilliseconds + " ms.");
                RaiseCompleted(new HostToolCompletedEventArgs(call.Id, call.ToolCallId, call.ToolName, isError, cancelled, total.Elapsed, execution.Elapsed, cancelled ? null : result?.Details));
            }
        }

        /// <summary>Handles <c>host_tool_cancel</c>: cancels the running call (and a pending approval dialog); omp already gave up on it.</summary>
        public void HandleCancel(HostToolCancelFrame cancel) => HandleCancel(cancel, null);

        /// <summary>Handles <c>host_tool_cancel</c> of the omp connection <paramref name="origin"/> (see <see cref="HandleCallAsync(HostToolCallFrame, object?, Func{JObject, CancellationToken, Task}?)"/>).</summary>
        public void HandleCancel(HostToolCancelFrame cancel, object? origin)
        {
            if (cancel == null)
            {
                throw new ArgumentNullException(nameof(cancel));
            }

            if (_active.TryGetValue(new CallKey(origin, cancel.TargetId), out var active))
            {
                active.Cancel();
            }
        }

        /// <summary>Cancels every call in flight (session end, omp exit, restart).</summary>
        public void CancelAll()
        {
            foreach (var active in _active.Values)
            {
                active.Cancel();
            }
        }

        public void Dispose()
        {
            try
            {
                _disposed.Cancel();
            }
            catch (ObjectDisposedException)
            {
            }
            catch (AggregateException ex)
            {
                _logger.Warn("A host tool cancellation callback failed: " + ex.InnerException?.Message);
            }
        }

        private async Task<HostToolResult?> RunAsync(HostToolCallFrame call, object? origin, ActiveCall active, Stopwatch execution, CancellationToken cancellationToken)
        {
            if (!_registry.TryGet(call.ToolName, out var tool))
            {
                return HostToolResult.Error("VS tool \"" + call.ToolName + "\" is not registered.");
            }

            var invocation = new HostToolInvocation(call.Id, call.ToolCallId, call.ToolName, call.Arguments ?? new JObject(), active.Progress, origin);

            PermissionKind kind;
            try
            {
                kind = tool.ResolvePermission(invocation);
            }
            catch (Exception ex)
            {
                _logger.Error("Could not resolve the permission of host tool '" + call.ToolName + "'", ex);
                return HostToolResult.Error("VS tool '" + call.ToolName + "' could not determine its permission requirement: " + ex.Message);
            }

            var admissionError = AdmissionCheck?.Invoke(invocation, kind);
            if (admissionError != null) { _logger.Warn("Host tool policy denied "+invocation.ToolName+": "+admissionError); return HostToolResult.Error(admissionError); }

            IPreparedHostToolCall? prepared = null;
            try
            {
                if (kind != PermissionKind.None && tool is IPreparedHostTool preparedTool)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    prepared = await preparedTool.PrepareAsync(invocation, cancellationToken).ConfigureAwait(false);
                    if (prepared == null || kind == PermissionKind.None)
                    {
                        return HostToolResult.Error("VS tool '" + call.ToolName + "' returned no approvable prepared change.");
                    }
                }

                return await ApproveAndExecuteAsync(tool, invocation, kind, prepared, execution, cancellationToken).ConfigureAwait(false);
            }
            finally
            {
                // Cleanup is best effort: it must not hide an applied mutation's result or cancellation.
                try
                {
                    prepared?.Dispose();
                }
                catch (Exception ex)
                {
                    _logger.Warn("Could not release the prepared call for '" + call.ToolName + "': " + ex.Message);
                }
            }
        }

        private async Task<HostToolResult?> ApproveAndExecuteAsync(IHostTool tool, HostToolInvocation invocation, PermissionKind kind,
            IPreparedHostToolCall? prepared, Stopwatch execution, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (kind != PermissionKind.None)
            {
                PermissionRequest request;
                if (prepared != null)
                {
                    request = prepared.Permission;
                    if (request == null)
                    {
                        return HostToolResult.Error("VS tool '" + invocation.ToolName + "' returned no prepared permission request.");
                    }

                    request.Source = PermissionSource.HostTool;
                    request.ToolName = invocation.ToolName;
                    request.Kind = kind;
                    if (string.IsNullOrEmpty(request.Title)) request.Title = "Allow " + invocation.ToolName + "?";
                }
                else
                {
                    request = await DescribeAsync(tool, invocation, kind, cancellationToken).ConfigureAwait(false);
                }

                var response = await _gate.RequestAsync(request, cancellationToken).ConfigureAwait(false);
                cancellationToken.ThrowIfCancellationRequested();
                if (response == null || !response.IsAllowed)
                {
                    return HostToolResult.Error(DescribeDenial(response));
                }
            }

            var admissionError = AdmissionCheck?.Invoke(invocation, kind);
            if (admissionError != null) { _logger.Warn("Host tool policy denied "+invocation.ToolName+": "+admissionError); return HostToolResult.Error(admissionError); }
            execution.Start();
            try
            {
                cancellationToken.ThrowIfCancellationRequested();
                var result = prepared != null
                    ? await prepared.ExecuteAsync(cancellationToken).ConfigureAwait(false)
                    : await tool.ExecuteAsync(invocation, cancellationToken).ConfigureAwait(false);
                return result ?? HostToolResult.Error("VS tool '" + invocation.ToolName + "' returned no result.");
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (OperationCanceledException)
            {
                return HostToolResult.Error("VS tool '" + invocation.ToolName + "' was cancelled.");
            }
            catch (Exception ex)
            {
                _logger.Error("Host tool '" + invocation.ToolName + "' threw", ex);
                return HostToolResult.Error("VS tool '" + invocation.ToolName + "' failed: " + ex.Message);
            }
            finally
            {
                execution.Stop();
            }
        }

        /// <summary>What the model is told about a denial: only the user's own note is quoted as theirs.</summary>
        private static string DescribeDenial(PermissionResponse? response)
        {
            if (response == null || response.SystemReason != null)
            {
                var reason = response?.SystemReason?.Trim();
                return "Permission was not granted: POMP denied the request without an answer from the user"
                    + (string.IsNullOrEmpty(reason) ? "." : " (" + reason + ")");
            }

            var text = "Permission denied by the user.";
            if (!string.IsNullOrWhiteSpace(response.Note))
            {
                text += " Note from the user: " + response.Note!.Trim();
            }

            return text;
        }

        private async Task<PermissionRequest> DescribeAsync(IHostTool tool, HostToolInvocation invocation, PermissionKind kind, CancellationToken cancellationToken)
        {
            PermissionRequest? request = null;
            try
            {
                request = await tool.DescribePermissionAsync(invocation, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                _logger.Warn("Host tool '" + invocation.ToolName + "' could not describe its permission request; using a generic prompt: " + ex.Message);
            }

            if (request == null)
            {
                var label = string.IsNullOrWhiteSpace(tool.Definition.Label) ? invocation.ToolName : tool.Definition.Label;
                request = new PermissionRequest
                {
                    Title = "Allow " + label + "?",
                    Message = PompLanguage.Pick("POMP이 VS 도구 '" + invocation.ToolName + "'(" + kind + ")을(를) 실행하려고 해요.\n", "POMP wants to run the VS tool '" + invocation.ToolName + "' (" + kind + ").\n") + Preview(invocation.Arguments, 1500),
                };
            }

            // Source and tool name form the policy key of remembered grants: never let a tool describe itself as another one.
            request.Source = PermissionSource.HostTool;
            request.ToolName = invocation.ToolName;

            if (request.Kind == PermissionKind.Other || request.Kind == PermissionKind.None)
            {
                request.Kind = kind;
            }

            if (string.IsNullOrEmpty(request.Title))
            {
                request.Title = "Allow " + invocation.ToolName + "?";
            }

            return request;
        }

        private static string Preview(JObject arguments, int maxChars)
        {
            var text = arguments.ToString(Formatting.None);
            return text.Length <= maxChars ? text : text.Substring(0, maxChars);
        }

        private void RaiseCompleted(HostToolCompletedEventArgs args)
        {
            var handlers = Completed;
            if (handlers == null)
            {
                return;
            }

            foreach (var handler in handlers.GetInvocationList())
            {
                try
                {
                    ((EventHandler<HostToolCompletedEventArgs>)handler)(this, args);
                }
                catch (Exception ex)
                {
                    _logger.Warn("A Completed handler failed: " + ex.Message);
                }
            }
        }

        /// <summary>A call id within the omp connection that sent it (reference identity; null for callers that do not track origins).</summary>
        private readonly struct CallKey : IEquatable<CallKey>
        {
            private readonly object? _origin;
            private readonly string _id;

            public CallKey(object? origin, string id)
            {
                _origin = origin;
                _id = id ?? string.Empty;
            }

            public bool Equals(CallKey other) => ReferenceEquals(_origin, other._origin) && string.Equals(_id, other._id, StringComparison.Ordinal);

            public override bool Equals(object? obj) => obj is CallKey other && Equals(other);

            public override int GetHashCode() =>
                unchecked(((_origin == null ? 0 : System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(_origin)) * 397) ^ StringComparer.Ordinal.GetHashCode(_id));
        }

        private sealed class ActiveCall
        {
            private readonly CancellationTokenSource _cts;
            private readonly IPompLogger _logger;

            public ActiveCall(CancellationTokenSource cts, Func<JObject, CancellationToken, Task> send, IPompLogger logger, string hostCallId)
            {
                _cts = cts;
                _logger = logger;
                Progress = new ProgressSink(send, logger, hostCallId);
            }

            public ProgressSink Progress { get; }

            public void Cancel()
            {
                try
                {
                    _cts.Cancel();
                }
                catch (ObjectDisposedException)
                {
                }
                catch (AggregateException ex)
                {
                    // A tool's cancellation callback threw; every callback still ran, and CancelAll must go on to the next call.
                    _logger.Warn("A host tool cancellation callback failed: " + ex.InnerException?.Message);
                }
            }
        }

        /// <summary>
        /// Turns <see cref="IProgress{T}.Report"/> into <c>host_tool_update</c> frames, in order, latest text wins when the tool
        /// reports faster than the pipe drains. <see cref="CloseAsync"/> flushes so the result frame always comes last.
        /// </summary>
        private sealed class ProgressSink : IProgress<string>
        {
            private readonly Func<JObject, CancellationToken, Task> _send;
            private readonly IPompLogger _logger;
            private readonly string _hostCallId;
            private readonly object _lock = new object();
            private string? _latest;
            private Task _pump = Task.CompletedTask;
            private bool _pumping;
            private bool _closed;

            public ProgressSink(Func<JObject, CancellationToken, Task> send, IPompLogger logger, string hostCallId)
            {
                _send = send;
                _logger = logger;
                _hostCallId = hostCallId;
            }

            public void Report(string value)
            {
                if (value == null)
                {
                    return;
                }

                lock (_lock)
                {
                    if (_closed)
                    {
                        return;
                    }

                    _latest = value;
                    if (_pumping)
                    {
                        return;
                    }

                    _pumping = true;
                    _pump = Task.Run(PumpAsync);
                }
            }

            public Task CloseAsync()
            {
                lock (_lock)
                {
                    _closed = true;
                    return _pump;
                }
            }

            private async Task PumpAsync()
            {
                while (true)
                {
                    string text;
                    lock (_lock)
                    {
                        if (_latest == null)
                        {
                            _pumping = false;
                            return;
                        }

                        text = _latest;
                        _latest = null;
                    }

                    try
                    {
                        await _send(OmpCommands.HostToolUpdate(_hostCallId, text), CancellationToken.None).ConfigureAwait(false);
                    }
                    catch (Exception ex)
                    {
                        _logger.Debug("Could not deliver a host tool progress update: " + ex.Message);
                    }
                }
            }
        }
    }
}
