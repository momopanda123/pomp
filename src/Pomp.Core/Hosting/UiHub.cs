using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;
using Pomp.Diagnostics;
using Pomp.Internal;
using Pomp.Permissions;
using Pomp.Ui;

namespace Pomp.Hosting
{
    /// <summary>How a host → UI message behaves while the page is not ready yet.</summary>
    internal enum UiDelivery
    {
        /// <summary>Part of the conversation: queued (bounded) until <c>ui/ready</c>, then replayed in order.</summary>
        Transcript,

        /// <summary>A state snapshot (bridge state, session info, dialogs): dropped while not ready, because the snapshot sent on <c>ui/ready</c> covers it.</summary>
        State,
    }

    /// <summary>The user's answer to an omp dialog.</summary>
    internal sealed class DialogAnswer
    {
        public string? Value { get; set; }

        public bool? Confirmed { get; set; }

        public bool Cancelled { get; set; }
    }

    /// <summary>
    /// Binds the session to one chat UI channel: an ordered outbox (messages are posted one after the other, so the page sees them
    /// in the order they were produced), the <c>ui/ready</c> handshake (snapshot + re-posted pending requests), the bookkeeping of
    /// pending permission/dialog requests, and routing of UI → host messages. It is also the <see cref="IPermissionPrompt"/>.
    /// Locking rule: nothing here calls out (channel, handlers) while holding the lock; the snapshot provider runs under the
    /// lock, so callers must never invoke the hub while holding a lock the provider needs.
    /// </summary>
    internal sealed class UiHub : IPermissionPrompt, IDisposable
    {
        private const int MaxPreReadyMessages = 2000;

        // Essential messages must not silently disappear: overflow faults delivery and denies pending approvals.
        private const int MaxOutboxMessages = 10000;
        private const long MaxQueueBytes = 8 * 1024 * 1024;
        private const int MaxMessageBytes = 2 * 1024 * 1024;
        private const int MaxPendingRequests = 128;

        // Pending approvals and dialogs are replayed on every ui/ready together with the snapshot, so together they must stay well below the queue budget.
        private const long MaxPendingBytes = MaxQueueBytes / 2;
        private const int MaxMergedCharacters = 65536;

        // The session's chat/* log replayed after every ui/ready: with the pending requests it must still fit the outbox.
        private const long MaxTranscriptBytes = 3 * 1024 * 1024;
        private const int MaxTranscriptMessages = 3000;
        private readonly List<UiMessage> _transcript = new List<UiMessage>();
        private readonly List<long> _transcriptSizes = new List<long>();
        private long _transcriptBytes;
        private bool _transcriptTruncated;

        // After trimming dropped the turn that is still running, its remaining messages are skipped up to the next turn start.
        private bool _transcriptResyncing;
        private long _outboxBytes;
        private long _preReadyBytes;
        private long _pendingBytes;

        // Cancelled when the delivery epoch ends: a post of an old epoch that reaches the channel late is skipped there, never shown
        // after the new epoch's snapshot.
        private CancellationTokenSource _generationCts = new CancellationTokenSource();

        // Bytes charged for the last queued message, the only one later messages can be folded into.
        private long _outboxTailBytes;
        private long _preReadyTailBytes;
        private long _drainGeneration;
        private long _scheduledGeneration = -1;
        private bool _deliveryFailed;

        private readonly IPompLogger _logger;
        private readonly object _lock = new object();
        private readonly Queue<UiMessage> _outbox = new Queue<UiMessage>();
        private UiMessage? _outboxTail;
        private int _droppedWhileFull;
        private readonly List<UiMessage> _preReady = new List<UiMessage>();
        private readonly Dictionary<string, PendingPermission> _permissions = new Dictionary<string, PendingPermission>(StringComparer.Ordinal);
        private readonly Dictionary<string, PendingDialog> _dialogs = new Dictionary<string, PendingDialog>(StringComparer.Ordinal);
        private IUiChannel? _channel;
        private bool _ready;
        private bool _draining;
        private long _sequence;

        // Stopwatch timestamp of the last Attach; the first ui/ready after it measures how long the page took to come up.
        private long _attachedAt;
        private bool _awaitingFirstReady;

        public UiHub(IPompLogger logger)
        {
            _logger = logger;
        }

        /// <summary>Produces the messages sent right after <c>ui/ready</c> (host/hello, bridge/state, session/info).</summary>
        public Func<IEnumerable<UiMessage>>? SnapshotProvider { get; set; }

        /// <summary>UI → host messages that are not handled by the hub itself (chat/send, chat/stop, session/new, bridge/restart, log/open, settings/open).</summary>
        public event EventHandler<UiMessage>? CommandReceived;

        /// <summary>
        /// The first <c>ui/ready</c> of an attached channel, with the time since <see cref="Attach"/>: the tool window attaches
        /// before WebView2 starts, so this is the chat's first render time. Raised outside the lock.
        /// </summary>
        public event EventHandler<TimeSpan>? PageReady;

        public bool HasChannel
        {
            get
            {
                lock (_lock)
                {
                    return _channel != null;
                }
            }
        }

        public bool IsReady
        {
            get
            {
                lock (_lock)
                {
                    return _channel != null && _ready && !_deliveryFailed;
                }
            }
        }

        public int PendingRequestCount
        {
            get
            {
                lock (_lock)
                {
                    return _permissions.Count + _dialogs.Count;
                }
            }
        }

        public void Attach(IUiChannel channel)
        {
            if (channel == null)
            {
                throw new ArgumentNullException(nameof(channel));
            }

            IUiChannel? previous;
            CancellationTokenSource stale;
            lock (_lock)
            {
                previous = _channel;
                if (ReferenceEquals(previous, channel))
                {
                    return;
                }

                _channel = channel;
                _ready = false;
                _deliveryFailed = false;
                _attachedAt = Stopwatch.GetTimestamp();
                _awaitingFirstReady = true;
                stale = NextGenerationLocked();
                _draining = false;
                ClearOutboxLocked();
                _preReady.Clear();
                _preReadyBytes = 0;
            }

            CancelGeneration(stale);

            if (previous != null)
            {
                previous.MessageReceived -= OnMessageReceived;
            }

            channel.MessageReceived += OnMessageReceived;
            if (channel.IsReady)
            {
                HandleReady(channel);
            }
        }

        public void Detach(IUiChannel channel)
        {
            CancellationTokenSource stale;
            lock (_lock)
            {
                if (!ReferenceEquals(_channel, channel))
                {
                    return;
                }

                _channel = null;
                _ready = false;
                stale = NextGenerationLocked();
                _draining = false;
                ClearOutboxLocked();
                _preReady.Clear();
                _preReadyBytes = 0;
            }

            CancelGeneration(stale);
            channel.MessageReceived -= OnMessageReceived;
        }

        public void Dispose()
        {
            IUiChannel? channel;
            var permissions = new List<PendingPermission>();
            var dialogs = new List<PendingDialog>();
            CancellationTokenSource stale;
            lock (_lock)
            {
                channel = _channel;
                _channel = null;
                _ready = false;
                stale = NextGenerationLocked();
                _draining = false;
                ClearOutboxLocked();
                _preReady.Clear();
                _preReadyBytes = 0;
                permissions.AddRange(_permissions.Values);
                dialogs.AddRange(_dialogs.Values);
            }

            CancelGeneration(stale);

            if (channel != null)
            {
                channel.MessageReceived -= OnMessageReceived;
            }

            foreach (var pending in permissions)
            {
                pending.Completion.TrySetCanceled();
            }

            foreach (var pending in dialogs)
            {
                pending.Completion.TrySetCanceled();
            }
        }

        /// <summary>Queues a host → UI message.</summary>
        public void Post(string type, object? payload, UiDelivery delivery = UiDelivery.Transcript)
        {
            UiMessage message;
            try
            {
                message = new UiMessage(type, payload);
            }
            catch (Exception ex)
            {
                _logger.Error("Could not serialize the UI message '" + type + "'", ex);
                return;
            }

            bool startDrain;
            lock (_lock)
            {
                if (delivery == UiDelivery.Transcript)
                {
                    // Logged whether or not a page is attached and ready: the next ui/ready replays it.
                    LogTranscriptLocked(message);
                }

                startDrain = EnqueueLocked(message, delivery);
            }

            if (startDrain)
            {
                StartDrain();
            }
        }

        /// <summary>Forgets the replayable transcript (a new omp session started: the page clears its transcript too).</summary>
        public void ClearTranscript()
        {
            lock (_lock)
            {
                _transcript.Clear();
                _transcriptSizes.Clear();
                _transcriptBytes = 0;
                _transcriptTruncated = false;
                _transcriptResyncing = false;
            }
        }

        /// <summary>Messages of the replayable transcript (tests and diagnostics).</summary>
        internal int TranscriptCount
        {
            get
            {
                lock (_lock)
                {
                    return _transcript.Count;
                }
            }
        }

        private static bool IsTranscriptType(string type) => type.StartsWith("chat/", StringComparison.Ordinal);

        private static UiMessage Clone(UiMessage message) => new UiMessage(message.Type, message.Payload?.DeepClone(), message.Id);

        /// <summary>Appends a copy of a chat/* message to the replay log (lock held); consecutive deltas/updates fold like in the outbox.</summary>
        private void LogTranscriptLocked(UiMessage message)
        {
            if (!IsTranscriptType(message.Type))
            {
                return;
            }

            if (_transcriptResyncing)
            {
                if (message.Type != UiMessageTypes.ChatTurnStarted)
                {
                    return;
                }

                _transcriptResyncing = false;
            }

            // A copy: the outbox folds later messages into its own tail, which must not change the logged one (or the reverse).
            var copy = Clone(message);
            var last = _transcript.Count - 1;
            if (last >= 0 && TryMerge(_transcript[last], copy))
            {
                var merged = MessageBytes(_transcript[last]);
                _transcriptBytes += merged - _transcriptSizes[last];
                _transcriptSizes[last] = merged;
            }
            else
            {
                var bytes = MessageBytes(copy);
                _transcript.Add(copy);
                _transcriptSizes.Add(bytes);
                _transcriptBytes += bytes;
            }

            TrimTranscriptLocked();
        }

        /// <summary>Drops the oldest turns until the log fits again; the log always starts at a turn start (never mid-turn).</summary>
        private void TrimTranscriptLocked()
        {
            if (_transcriptBytes <= MaxTranscriptBytes && _transcript.Count <= MaxTranscriptMessages)
            {
                return;
            }

            var remove = 0;
            long removed = 0;
            while (remove < _transcript.Count
                   && (_transcriptBytes - removed > MaxTranscriptBytes || _transcript.Count - remove > MaxTranscriptMessages))
            {
                removed += _transcriptSizes[remove++];
            }

            while (remove < _transcript.Count && _transcript[remove].Type != UiMessageTypes.ChatTurnStarted)
            {
                removed += _transcriptSizes[remove++];
            }

            // Nothing left means the running turn itself went: skip the rest of it rather than log a turn without its start.
            _transcriptResyncing = remove == _transcript.Count;
            _transcript.RemoveRange(0, remove);
            _transcriptSizes.RemoveRange(0, remove);
            _transcriptBytes -= removed;
            _transcriptTruncated = true;
        }

        /// <summary>Shows an approval dialog and waits for the answer. Cancelling the token closes the dialog (<c>permission/dismiss</c>) and throws.</summary>
        public async Task<PermissionResponse> PromptAsync(PermissionRequest request, CancellationToken cancellationToken)
        {
            if (request == null)
            {
                throw new ArgumentNullException(nameof(request));
            }

            var payload = new PermissionRequestPayload
            {
                RequestId = request.RequestId,
                Source = request.Source,
                Kind = request.Kind == PermissionKind.None ? PermissionKind.Other : request.Kind,
                ToolName = request.ToolName,
                Title = request.Title,
                Message = request.Message,
                Tasks = request.Tasks,
                Target = request.Target,
                Diff = request.Diff,
                TimeoutMs = request.Timeout.HasValue ? (int?)Math.Min(int.MaxValue, (long)request.Timeout.Value.TotalMilliseconds) : null,
                CanRemember = request.CanRemember,
            };
            var message = new UiMessage(UiMessageTypes.PermissionRequest, payload);
            var pending = new PendingPermission(message, request.CanRemember) { Bytes = MessageBytes(message) };
            var startDrain = false;
            lock (_lock)
            {
                if (!CanAcceptPendingLocked(pending.Bytes))
                {
                    return PermissionResponse.DeniedBySystem("The chat UI cannot accept another approval request.");
                }

                if (_permissions.ContainsKey(request.RequestId))
                {
                    throw new InvalidOperationException("A permission request with id '" + request.RequestId + "' is already pending.");
                }

                pending.Sequence = ++_sequence;
                _permissions.Add(request.RequestId, pending);
                _pendingBytes += pending.Bytes;
                // Not ready yet: the request is replayed from the pending table on ui/ready.
                startDrain = EnqueueLocked(message, UiDelivery.State);
            }

            if (startDrain)
            {
                StartDrain();
            }

            var started = DateTime.UtcNow;
            using (cancellationToken.Register(() => pending.Completion.TrySetCanceled()))
            {
                try
                {
                    return await pending.Completion.Task.ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    var timedOut = request.Timeout.HasValue && DateTime.UtcNow - started >= request.Timeout.Value - TimeSpan.FromMilliseconds(50);
                    // Removed before the dismissal is queued: a ui/ready in between must neither replay the question nor drop
                    // the dismissal with the old outbox.
                    RemovePermission(request.RequestId, pending);
                    Dismiss(request.RequestId, isPermission: true, timedOut ? "timeout" : "cancelled");
                    throw;
                }
                finally
                {
                    RemovePermission(request.RequestId, pending);
                }
            }
        }

        /// <summary>Shows an omp dialog (select/confirm/input/editor) and waits for the answer. Cancelling the token closes it (<c>dialog/dismiss</c>) and throws.</summary>
        public async Task<DialogAnswer> RequestDialogAsync(DialogRequestPayload payload, CancellationToken cancellationToken)
        {
            if (payload == null)
            {
                throw new ArgumentNullException(nameof(payload));
            }

            var message = new UiMessage(UiMessageTypes.DialogRequest, payload);
            var pending = new PendingDialog(message) { Bytes = MessageBytes(message) };
            var startDrain = false;
            lock (_lock)
            {
                if (!CanAcceptPendingLocked(pending.Bytes))
                {
                    return new DialogAnswer { Cancelled = true };
                }

                if (_dialogs.ContainsKey(payload.RequestId))
                {
                    throw new InvalidOperationException("A dialog with id '" + payload.RequestId + "' is already pending.");
                }

                pending.Sequence = ++_sequence;
                _dialogs.Add(payload.RequestId, pending);
                _pendingBytes += pending.Bytes;
                startDrain = EnqueueLocked(message, UiDelivery.State);
            }

            if (startDrain)
            {
                StartDrain();
            }

            using (cancellationToken.Register(() => pending.Completion.TrySetCanceled()))
            {
                try
                {
                    return await pending.Completion.Task.ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    RemoveDialog(payload.RequestId, pending);
                    Dismiss(payload.RequestId, isPermission: false, "cancelled");
                    throw;
                }
                finally
                {
                    RemoveDialog(payload.RequestId, pending);
                }
            }
        }

        /// <summary>Removes this request's own entry only: a delivery failure may have cleared it and a new request may reuse the id.</summary>
        private void RemovePermission(string requestId, PendingPermission pending)
        {
            lock (_lock)
            {
                if (_permissions.TryGetValue(requestId, out var current) && ReferenceEquals(current, pending))
                {
                    _permissions.Remove(requestId);
                    _pendingBytes -= pending.Bytes;
                }
            }
        }

        private void RemoveDialog(string requestId, PendingDialog pending)
        {
            lock (_lock)
            {
                if (_dialogs.TryGetValue(requestId, out var current) && ReferenceEquals(current, pending))
                {
                    _dialogs.Remove(requestId);
                    _pendingBytes -= pending.Bytes;
                }
            }
        }

        private void Dismiss(string requestId, bool isPermission, string reason)
        {
            Post(
                isPermission ? UiMessageTypes.PermissionDismiss : UiMessageTypes.DialogDismiss,
                new DismissPayload { RequestId = requestId, Reason = reason },
                UiDelivery.State);
        }

        // ---- UI → host -------------------------------------------------------------------------------------------

        private void OnMessageReceived(object? sender, UiMessage message)
        {
            var channel = sender as IUiChannel;
            lock (_lock)
            {
                if (channel == null || !ReferenceEquals(_channel, channel))
                {
                    return;
                }
            }

            try
            {
                switch (message.Type)
                {
                    case UiMessageTypes.UiReady:
                        HandleReady(channel);
                        break;
                    case UiMessageTypes.UiLog:
                        HandleLog(message);
                        break;
                    case UiMessageTypes.PermissionRespond:
                        HandlePermissionRespond(message);
                        break;
                    case UiMessageTypes.DialogRespond:
                        HandleDialogRespond(message);
                        break;
                    default:
                        CommandReceived?.Invoke(this, message);
                        break;
                }
            }
            catch (Exception ex)
            {
                _logger.Error("Failed to handle the UI message '" + message.Type + "'", ex);
            }
        }

        private void HandleReady(IUiChannel channel)
        {
            bool startDrain;
            CancellationTokenSource stale;
            TimeSpan? firstReady = null;
            lock (_lock)
            {
                if (!ReferenceEquals(_channel, channel))
                {
                    return;
                }

                _ready = true;
                if (_awaitingFirstReady)
                {
                    _awaitingFirstReady = false;
                    firstReady = TimeSpan.FromTicks((long)((Stopwatch.GetTimestamp() - _attachedAt) * ((double)TimeSpan.TicksPerSecond / Stopwatch.Frequency)));
                }
                // A ready handshake replaces the delivery epoch, including any blocked old drain.
                var lostDelivery = _deliveryFailed;
                _deliveryFailed = false;
                stale = NextGenerationLocked();
                _draining = false;
                ClearOutboxLocked();
                if (lostDelivery)
                {
                    AppendOutboxLocked(new UiMessage(UiMessageTypes.Error, new
                    {
                        code = "ui_delivery_failed", message = PompLanguage.Pick("채팅 전달에 실패했어요. 대기 중이던 승인은 거부되었어요.", "Chat delivery failed. Pending approvals were denied."), retryable = false,
                    }));
                }

                var snapshot = SnapshotProvider?.Invoke();
                if (snapshot != null)
                {
                    foreach (var message in snapshot)
                    {
                        AppendOutboxLocked(message);
                    }
                }

                // The whole session transcript again (a reloaded page starts empty; a recovered one rebuilds after replayBegin).
                // Copies: live messages fold into the outbox tail, which must not change the log.
                if (_transcript.Count > 0 || _transcriptTruncated)
                {
                    AppendOutboxLocked(new UiMessage(UiMessageTypes.ChatReplayBegin, new ChatReplayBeginPayload { Truncated = _transcriptTruncated }));
                    foreach (var logged in _transcript)
                    {
                        AppendOutboxLocked(Clone(logged));
                    }

                    AppendOutboxLocked(new UiMessage(UiMessageTypes.ChatReplayEnd, new JObject()));
                }

                // Only questions someone still waits for: a finished waiter removes its entry right after, without another dismissal.
                var pending = new List<KeyValuePair<long, UiMessage>>();
                foreach (var permission in _permissions.Values)
                {
                    if (!permission.Completion.Task.IsCompleted)
                    {
                        pending.Add(new KeyValuePair<long, UiMessage>(permission.Sequence, permission.Message));
                    }
                }

                foreach (var dialog in _dialogs.Values)
                {
                    if (!dialog.Completion.Task.IsCompleted)
                    {
                        pending.Add(new KeyValuePair<long, UiMessage>(dialog.Sequence, dialog.Message));
                    }
                }

                pending.Sort((a, b) => a.Key.CompareTo(b.Key));
                foreach (var item in pending)
                {
                    AppendOutboxLocked(item.Value);
                }

                // Never chat/* here: those posted before ready are part of the replayed transcript above.
                foreach (var message in _preReady.ToArray())
                {
                    AppendOutboxLocked(message);
                }

                _preReady.Clear();
                _preReadyBytes = 0;
                startDrain = !_draining && _outbox.Count > 0;
                if (startDrain)
                {
                    _draining = true;
                }
            }

            // Synchronously, before the channel posts anything else: an old epoch's message that reaches it late is skipped.
            CancelGeneration(stale);
            if (startDrain)
            {
                StartDrain();
            }

            if (firstReady.HasValue)
            {
                try
                {
                    PageReady?.Invoke(this, firstReady.Value);
                }
                catch (Exception ex)
                {
                    _logger.Warn("A page-ready handler failed: " + ex.Message);
                }
            }
        }

        /// <summary>Starts a new delivery epoch (lock held). Cancel the returned source of the old epoch once the lock is released.</summary>
        private CancellationTokenSource NextGenerationLocked()
        {
            _drainGeneration++;
            var stale = _generationCts;
            _generationCts = new CancellationTokenSource();
            return stale;
        }

        private void CancelGeneration(CancellationTokenSource stale)
        {
            try
            {
                stale.Cancel();
            }
            catch (Exception ex) when (!(ex is OutOfMemoryException))
            {
                _logger.Debug("Cancelling an old UI delivery epoch failed: " + ex.Message);
            }
        }

        private void HandleLog(UiMessage message)
        {
            var log = message.PayloadAs<UiLogPayload>();
            if (log == null)
            {
                return;
            }

            PompLogLevel level;
            switch ((log.Level ?? "info").ToLowerInvariant())
            {
                case "debug":
                    level = PompLogLevel.Debug;
                    break;
                case "warning":
                case "warn":
                    level = PompLogLevel.Warning;
                    break;
                case "error":
                    level = PompLogLevel.Error;
                    break;
                default:
                    level = PompLogLevel.Info;
                    break;
            }

            _logger.Log(level, "[ui] " + log.Message);
        }

        private void HandlePermissionRespond(UiMessage message)
        {
            var id = message.Payload?["requestId"];
            var requestId = id?.Type == Newtonsoft.Json.Linq.JTokenType.String ? (string?)id : null;
            if (string.IsNullOrEmpty(requestId))
            {
                return;
            }

            PendingPermission? pending;
            lock (_lock)
            {
                // Answered now: taken out at once, so a ui/ready before the waiter resumes cannot replay it as a ghost dialog.
                if (_permissions.TryGetValue(requestId!, out pending))
                {
                    _permissions.Remove(requestId!);
                    _pendingBytes -= pending.Bytes;
                }
            }

            if (pending == null)
            {
                _logger.Debug("Ignoring a permission response for the unknown request '" + requestId + "'.");
                return;
            }

            // Validate before deserialization: missing, numeric and unknown enum values all deny immediately.
            var choice = message.Payload?["decision"];
            var namedChoice = choice?.Type == Newtonsoft.Json.Linq.JTokenType.String ? (string?)choice : null;
            PermissionDecision decision;
            switch (namedChoice)
            {
                case "allow": decision = PermissionDecision.Allow; break;
                case "allowForSession": decision = PermissionDecision.AllowForSession; break;
                case "alwaysAllow": decision = PermissionDecision.AlwaysAllow; break;
                case "deny": decision = PermissionDecision.Deny; break;
                default:
                    pending.Completion.TrySetResult(PermissionResponse.DeniedBySystem("The chat UI sent an invalid permission response."));
                    return;
            }

            if (!pending.CanRemember && (decision == PermissionDecision.AllowForSession || decision == PermissionDecision.AlwaysAllow))
            {
                // The dialog offered no "remember" choice for this request: a remembered answer counts for this call only.
                decision = PermissionDecision.Allow;
            }

            var note = message.Payload?["note"];
            pending.Completion.TrySetResult(new PermissionResponse(decision,
                note?.Type == Newtonsoft.Json.Linq.JTokenType.String ? (string?)note : null));
        }

        private void HandleDialogRespond(UiMessage message)
        {
            var response = message.PayloadAs<DialogRespondPayload>();
            if (response == null || string.IsNullOrEmpty(response.RequestId))
            {
                return;
            }

            PendingDialog? pending;
            lock (_lock)
            {
                if (_dialogs.TryGetValue(response.RequestId, out pending))
                {
                    _dialogs.Remove(response.RequestId);
                    _pendingBytes -= pending.Bytes;
                }
            }

            if (pending == null)
            {
                _logger.Debug("Ignoring a dialog response for the unknown request '" + response.RequestId + "'.");
                return;
            }

            pending.Completion.TrySetResult(new DialogAnswer
            {
                Value = response.Value,
                Confirmed = response.Confirmed,
                Cancelled = response.Cancelled == true,
            });
        }

        // ---- outbox ----------------------------------------------------------------------------------------------

        /// <summary>Must be called with the lock held. Returns true when the caller has to start the drain loop.</summary>
        private bool EnqueueLocked(UiMessage message, UiDelivery delivery)
        {
            if (_channel == null || _deliveryFailed) return false;

            var bytes = MessageBytes(message);
            if (!_ready)
            {
                // chat/* messages are replayed from the transcript log on ui/ready, so only the other transcript messages wait here.
                if (delivery == UiDelivery.Transcript && !IsTranscriptType(message.Type))
                {
                    if (bytes > MaxMessageBytes || _preReadyBytes + bytes > MaxQueueBytes || _preReady.Count >= MaxPreReadyMessages)
                    {
                        FailDeliveryLocked("The chat page's pre-ready queue overflowed.");
                        return false;
                    }
                    if (_preReady.Count > 0 && TryMerge(_preReady[_preReady.Count - 1], message))
                    {
                        // Charge what the merged message costs now (a replaced tool update may even be smaller than before).
                        var merged = MessageBytes(_preReady[_preReady.Count - 1]);
                        _preReadyBytes += merged - _preReadyTailBytes;
                        _preReadyTailBytes = merged;
                        return false;
                    }
                    _preReady.Add(message);
                    _preReadyBytes += bytes;
                    _preReadyTailBytes = bytes;
                }
                return false;
            }

            if (bytes > MaxMessageBytes || _outboxBytes + bytes > MaxQueueBytes || _outbox.Count >= MaxOutboxMessages)
            {
                if (message.Type == UiMessageTypes.ChatToolUpdate || message.Type == UiMessageTypes.ChatNotice)
                {
                    _droppedWhileFull++;
                }
                else
                {
                    FailDeliveryLocked("The chat page's outbound queue overflowed.");
                }
                return false;
            }
            if (TryMerge(_outboxTail, message))
            {
                // Only the tail can grow, so re-measuring it keeps the byte count exact however many updates are folded.
                var merged = MessageBytes(_outboxTail!);
                _outboxBytes += merged - _outboxTailBytes;
                _outboxTailBytes = merged;
                return false;
            }

            AppendOutboxLocked(message);
            if (_draining)
            {
                return false;
            }

            _draining = true;
            return true;
        }

        private void StartDrain()
        {
            lock (_lock)
            {
                if (_scheduledGeneration == _drainGeneration) return;
                var generation = _drainGeneration;
                _scheduledGeneration = generation;
                Task.Run(() => DrainAsync(generation)).Forget(_logger, "UI outbox");
            }
        }

        private async Task DrainAsync(long generation)
        {
            while (true)
            {
                UiMessage? message = null;
                IUiChannel? channel = null;
                var epoch = CancellationToken.None;
                int dropped;
                lock (_lock)
                {
                    if (generation != _drainGeneration) return;
                    epoch = _generationCts.Token;
                    dropped = _droppedWhileFull;
                    _droppedWhileFull = 0;
                    if (_outbox.Count == 0 || _channel == null || !_ready)
                    {
                        _draining = false;
                        _scheduledGeneration = -1;
                    }
                    else
                    {
                        message = _outbox.Dequeue();
                        _outboxBytes = _outbox.Count == 0 ? 0 : Math.Max(0, _outboxBytes - MessageBytes(message));
                        if (ReferenceEquals(message, _outboxTail))
                        {
                            _outboxTail = null;
                            _outboxTailBytes = 0;
                        }

                        channel = _channel;
                    }
                }

                if (dropped > 0)
                {
                    _logger.Warn(dropped + " progress/notice message(s) for the chat page were dropped because it was not keeping up.");
                }

                if (message == null || channel == null)
                {
                    return;
                }

                try
                {
                    using (var deadline = CancellationTokenSource.CreateLinkedTokenSource(epoch))
                    {
                        deadline.CancelAfter(TimeSpan.FromSeconds(30));
                        await channel.PostAsync(message, deadline.Token).WithCancellation(deadline.Token).ConfigureAwait(false);
                    }
                }
                catch (OperationCanceledException) when (epoch.IsCancellationRequested)
                {
                    // The epoch ended (ready, re-attach, detach): its messages are replaced by the next epoch's snapshot.
                    return;
                }
                catch (Exception ex)
                {
                    _logger.Warn("Posting '" + message.Type + "' to the UI failed: " + ex.Message);
                    lock (_lock)
                    {
                        if (generation == _drainGeneration && ReferenceEquals(channel, _channel))
                        {
                            FailDeliveryLocked("The chat channel failed; pending approvals were denied.");
                            _draining = false;
                            _scheduledGeneration = -1;
                        }
                    }
                    return;
                }
            }
        }

        private void AppendOutboxLocked(UiMessage message)
        {
            if (_deliveryFailed) return;
            var bytes = MessageBytes(message);
            if (bytes > MaxMessageBytes || _outboxBytes + bytes > MaxQueueBytes || _outbox.Count >= MaxOutboxMessages)
            {
                FailDeliveryLocked("The chat page's outbound queue overflowed.");
                return;
            }
            _outbox.Enqueue(message);
            _outboxBytes += bytes;
            _outboxTail = message;
            _outboxTailBytes = bytes;
        }

        private void ClearOutboxLocked()
        {
            _outbox.Clear();
            _outboxTail = null;
            _outboxBytes = 0;
            _outboxTailBytes = 0;
        }

        private static long MessageBytes(UiMessage message) =>
            System.Text.Encoding.UTF8.GetByteCount(Pomp.Json.PompJson.Serialize(message));

        private bool CanAcceptPendingLocked(long bytes) =>
            !_deliveryFailed
            && _permissions.Count + _dialogs.Count < MaxPendingRequests
            && bytes <= MaxMessageBytes
            && _pendingBytes + bytes <= MaxPendingBytes;

        private void FailDeliveryLocked(string reason)
        {
            if (_deliveryFailed) return;
            _deliveryFailed = true;
            ClearOutboxLocked();
            _preReady.Clear();
            _preReadyBytes = 0;
            // These completion sources run continuations asynchronously, so no caller code executes under the lock.
            foreach (var pending in _permissions.Values) pending.Completion.TrySetResult(PermissionResponse.DeniedBySystem(reason));
            foreach (var pending in _dialogs.Values) pending.Completion.TrySetResult(new DialogAnswer { Cancelled = true });
            _permissions.Clear();
            _dialogs.Clear();
            _pendingBytes = 0;
            var error = new UiMessage(UiMessageTypes.Error, new { code = "ui_delivery_failed", message = reason + " Reload the chat page; the transcript may be incomplete.", retryable = false });
            _outbox.Enqueue(error);
            _outboxTail = error;
            _outboxBytes = MessageBytes(error);
            _outboxTailBytes = _outboxBytes;
            Task.Run(() => _logger.Warn(reason)).Forget(_logger, "UI delivery failure");
            if (_ready && !_draining)
            {
                _draining = true;
                var generation = _drainGeneration;
                _scheduledGeneration = generation;
                Task.Run(() => DrainAsync(generation)).Forget(_logger, "UI failure notification");
            }
        }

        /// <summary>
        /// Folds <paramref name="next"/> into <paramref name="queued"/> (a message not yet handed to the channel) when that keeps
        /// the page's result identical: text of the same assistant message and kind is appended, a tool's progress text replaced.
        /// </summary>
        private static bool TryMerge(UiMessage? queued, UiMessage next)
        {
            if (queued == null || queued.Type != next.Type || queued.Payload == null || next.Payload == null)
            {
                return false;
            }

            switch (next.Type)
            {
                case UiMessageTypes.ChatAssistantDelta:
                    if ((string?)queued.Payload["messageId"] != (string?)next.Payload["messageId"] || (string?)queued.Payload["kind"] != (string?)next.Payload["kind"])
                    {
                        return false;
                    }

                    if (((string?)queued.Payload["delta"] ?? string.Empty).Length + ((string?)next.Payload["delta"] ?? string.Empty).Length > MaxMergedCharacters) return false;
                    queued.Payload["delta"] = (string?)queued.Payload["delta"] + (string?)next.Payload["delta"];
                    return true;
                case UiMessageTypes.ChatToolUpdate:
                    if ((string?)queued.Payload["toolCallId"] != (string?)next.Payload["toolCallId"])
                    {
                        return false;
                    }

                    queued.Payload["text"] = next.Payload["text"]?.DeepClone();
                    return true;
                default:
                    return false;
            }
        }

        private sealed class PendingPermission
        {
            public PendingPermission(UiMessage message, bool canRemember)
            {
                Message = message;
                CanRemember = canRemember;
            }

            public UiMessage Message { get; }

            public bool CanRemember { get; }

            public long Sequence { get; set; }

            /// <summary>Serialized UTF-8 size, counted against the pending budget while the request is in the table.</summary>
            public long Bytes { get; set; }

            public TaskCompletionSource<PermissionResponse> Completion { get; } =
                new TaskCompletionSource<PermissionResponse>(TaskCreationOptions.RunContinuationsAsynchronously);
        }

        private sealed class PendingDialog
        {
            public PendingDialog(UiMessage message)
            {
                Message = message;
            }

            public UiMessage Message { get; }

            public long Sequence { get; set; }

            /// <summary>Serialized UTF-8 size, counted against the pending budget while the dialog is in the table.</summary>
            public long Bytes { get; set; }

            public TaskCompletionSource<DialogAnswer> Completion { get; } =
                new TaskCompletionSource<DialogAnswer>(TaskCreationOptions.RunContinuationsAsynchronously);
        }
    }
}
