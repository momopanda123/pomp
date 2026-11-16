using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;
using Pomp.Bridge;
using Pomp.Diagnostics;
using Pomp.Features;
using Pomp.Internal;
using Pomp.Protocol;
using Pomp.Ui;

namespace Pomp.Hosting
{
    // Explicit steering reaches the running agent without starting an independent turn.
    public sealed partial class PompSession
    {
        private enum SteerOutcome { Sent, NotRunning, Failed }

        /// <summary>
        /// The steers the current run took, oldest first (their <c>chat/steered</c> ids): Stop must take them back from omp's queue (a
        /// plain abort would run them) and marks the ones it withdrew as not delivered.
        /// </summary>
        private readonly List<string> _steersSent = new List<string>();

        /// <summary>Steering commands are message wrappers, not commands for the running engine to execute.</summary>
        private static string SteerText(string? text)
        {
            var value = (text ?? string.Empty).TrimStart();
            if ((value.StartsWith("/steer", StringComparison.Ordinal) || value.StartsWith("/stear", StringComparison.Ordinal))
                && (value.Length == 6 || char.IsWhiteSpace(value[6]))) return value.Substring(6).Trim();
            return value;
        }

        /// <summary>Goes to this conversation itself, with message text rather than an engine slash command.</summary>
        private static bool CanSteer(ChatSendPayload send)
        {
            if (!string.IsNullOrWhiteSpace(send.Team) || !string.IsNullOrWhiteSpace(send.Agent) || !string.IsNullOrWhiteSpace(send.AgentInstance)) return false;
            var text = SteerText(send.Text);
            return !text.StartsWith("/", StringComparison.Ordinal) && (text.Length > 0 || send.Attachments?.Count > 0 || send.RequestFiles?.Count > 0);
        }

        private bool IsSteerableLocked(long generation) =>
            generation == _chatStopGeneration && (_turns.Count > 0 || _info.IsStreaming) && _bridge.State == BridgeState.Running && !_sessionSwitchUncertain;

        /// <summary>chat/steer: to the running agent; otherwise use ordinary send/queue handling.</summary>
        private async Task HandleChatSteerAsync(ChatSendPayload send, long generation)
        {
            if (send.RequestFiles?.Count > 0)
            {
                try { ValidateQueuedSend(send); }
                catch (Exception ex)
                {
                    _ui.Post(UiMessageTypes.Error, new UiErrorInfo { Code = "queue_rejected", ClientMessageId = send.ClientMessageId, Message = ex.Message, Retryable = false }, UiDelivery.State);
                    return;
                }
            }
            if (CanSteer(send) && await SteerAsync(send, generation).ConfigureAwait(false) != SteerOutcome.NotRunning) return;
            await HandleChatSendAsync(send, generation).ConfigureAwait(false);
        }

        /// <summary>Queue's send-now action: steer the selected item into the running agent.</summary>
        private async Task SteerQueuedAsync(string requestId, string? id)
        {
            QueuedChat item;
            long generation;
            string? session;
            try
            {
                lock (_stateLock)
                {
                    item = _chatQueue.FirstOrDefault(q => q.Id == id) ?? throw new ArgumentException(PompLanguage.Pick("대기열 항목을 찾을 수 없어요.", "Queue item was not found."));
                    if (!CanSteer(item.Send)) throw new InvalidOperationException(PompLanguage.Pick("대상을 고른 메시지나 명령은 지금 보낼 수 없어요.", "A message with a chosen recipient or a command cannot be sent now."));
                    if (_queueSession != _info.SessionId) throw new InvalidOperationException(PompLanguage.Pick("이 대기열은 다른 대화의 항목이에요.", "This queue belongs to another conversation."));
                    item.Send.Text = SteerText(item.Send.Text);
                    generation = _chatStopGeneration;
                    session = _queueSession;
                    _chatQueue.Remove(item);
                    // Out of the queue but not in a turn yet: its sender must not read that as "removed".
                    if (item.Relay != null) item.Relay.State = RelayState.Admitting;
                }
            }
            catch (Exception ex) { Reply(UiMessageTypes.ChatQueueReply, requestId, ex.Message); return; }

            PublishChatQueue();
            Reply(UiMessageTypes.ChatQueueReply, requestId, null);
            var send = CopySend(item.Send);
            send.ClientMessageId = (item.Send.ClientMessageId?.StartsWith(HookMessagePrefix, StringComparison.Ordinal) == true ? HookMessagePrefix : "queue-") + Guid.NewGuid().ToString("N");
            switch (await SteerAsync(send, generation).ConfigureAwait(false))
            {
                case SteerOutcome.Sent:
                    // The running turn took it: that turn's end is its end.
                    if (item.Relay != null) AttachRelayToRunningTurn(item.Relay);
                    break;
                case SteerOutcome.NotRunning:
                    // Nothing to steer. While background work still holds the engine the item waits again, first in line.
                    await HandleChatSendAsync(send, generation, expectedSession: session, bypassQueue: true, capturedPartner: item.Partner, queueFirstWhenBusy: true, relay: item.Relay).ConfigureAwait(false);
                    if (item.Relay != null) SettleRelayAdmission(item.Relay);
                    break;
                case SteerOutcome.Failed:
                    // Not delivered: the item goes back to the front of the queue (unless the conversation changed meanwhile).
                    lock (_stateLock)
                    {
                        if (_chatQueue.Count == 0) _queueSession = session;
                        if (_queueSession == session)
                        {
                            _chatQueue.Insert(0, item);
                            if (item.Relay != null) item.Relay.State = RelayState.Queued;
                        }
                    }

                    PublishChatQueue();
                    if (item.Relay != null) SettleRelayAdmission(item.Relay);
                    break;
            }
        }

        /// <summary>
        /// Send to the existing child session, not as a request for the main model to relay. RPC acknowledges
        /// admission to that child; its subsequent run is independent of (and may outlive) the main turn.
        /// </summary>
        private async Task<bool> HandleDirectSubagentSendAsync(ChatSendPayload send, long generation, CancellationToken requestToken, string? expectedSession)
        {
            var promptId = "subagent-" + Interlocked.Increment(ref _promptCounter).ToString(System.Globalization.CultureInfo.InvariantCulture);
            var text = send.Text ?? string.Empty;
            var attachments = (send.Attachments ?? new List<ChatAttachment>())
                .Where(a => !string.IsNullOrWhiteSpace(a?.Path)).Select(a => a.Path).ToList();
            EditorContextResult? editorContext = null;

            var status = "error";
            UiErrorInfo? failure = null;
            try
            {
                ValidateQueuedSend(send);
                if (!string.IsNullOrWhiteSpace(send.Team) || !string.IsNullOrWhiteSpace(send.Agent) || send.Main == true)
                    throw new InvalidOperationException(PompLanguage.Pick("보낼 대상이 너무 많아요.", "Too many targets."));
                RefuseComputerCommand(text);
                CancellationToken epoch;
                OmpConnection? connection;
                string? session;
                lock (_stateLock)
                {
                    if (generation != _chatStopGeneration || _sessionSwitchUncertain || _bridge.State != BridgeState.Running)
                        throw new InvalidOperationException(PompLanguage.Pick("활성 대화가 확인되지 않았어요.", "The active conversation is not confirmed."));
                    session = _info.SessionId;
                    if (string.IsNullOrEmpty(session) || (expectedSession != null && expectedSession != session))
                        throw new InvalidOperationException(PompLanguage.Pick("대화가 바뀌었어요.", "Conversation changed."));
                    epoch = _epoch.Token;
                    connection = _epochConnection;
                }
                using var sending = CancellationTokenSource.CreateLinkedTokenSource(requestToken, _disposeCts.Token, epoch);
                // Captured context is displayed only after the child accepts the message.
                if (send.IncludeEditorContext && !(send.RequestFiles?.Count > 0))
                {
                    try
                    {
                        var contextTask = _contextBuilder.BuildAsync(sending.Token);
                        contextTask.Observe();
                        editorContext = await contextTask.WithCancellation(sending.Token).ConfigureAwait(false);
                    }
                    catch (Exception ex) when (!(ex is OperationCanceledException)) { _logger.Warn("Could not build the editor context: " + ex.Message); }
                }

                var message = await ComposeMessageAsync(editorContext?.Text, text, attachments, sending.Token, send.RequestFiles, childSession: true).ConfigureAwait(false);
                ValidateAgentInstance(send.AgentInstance!);
                var command = OmpCommands.SteerSubagent(send.AgentInstance!, message);
                if (Pomp.Transport.NdjsonWriter.Encode(command).Length > MaxPromptFrameBytes - 128)
                    throw new InvalidOperationException(PompLanguage.Pick("메시지가 너무 커요.", "Message is too large."));
                lock (_stateLock)
                {
                    if (generation != _chatStopGeneration || session != _info.SessionId || _sessionSwitchUncertain
                        || !ReferenceEquals(connection, _epochConnection) || _bridge.State != BridgeState.Running)
                        throw new InvalidOperationException(PompLanguage.Pick("대화가 바뀌었어요.", "Conversation changed."));
                }
                var response = await connection!.SendCommandAsync(command, _options.PromptTimeout, sending.Token).ConfigureAwait(false);
                if (!response.Success)
                    throw new InvalidOperationException(response.Error ?? PompLanguage.Pick("에이전트가 메시지를 거부했어요.", "The agent refused the message."));
                status = "completed";
                _ui.Post(UiMessageTypes.ChatSubagentAccepted, new ChatSubagentAcceptedPayload
                {
                    MessageId = promptId, ClientMessageId = send.ClientMessageId, Text = text,
                    AgentInstance = send.AgentInstance!, EditorContext = editorContext?.EditorContext,
                    Attachments = attachments.Count == 0 ? null : attachments.Select(path => new ChatAttachment { Path = path }).ToList(),
                    RequestFiles = PromptDisplay.CopyRequestFiles(send.RequestFiles),
                });
                return true;
            }
            catch (OperationCanceledException)
            {
                status = "aborted";
                return false;
            }
            catch (TimeoutException ex)
            {
                _logger.Warn("The direct subagent message was not acknowledged: " + ex.Message);
                failure = new UiErrorInfo { Code = "subagent_unconfirmed", Message = PompLanguage.Pick("에이전트가 메시지를 받았는지 확인되지 않았어요.", "It is not known whether the agent received the message."), Retryable = false };
                return false;
            }
            catch (Exception ex)
            {
                _logger.Warn("Direct subagent message failed: " + ex.Message);
                failure = new UiErrorInfo { Code = "subagent_rejected", Message = ex.Message, Retryable = false };
                return false;
            }
            finally
            {
                if (status != "completed")
                {
                    _ui.Post(UiMessageTypes.ChatTurnStarted, new ChatTurnStartedPayload
                    {
                        PromptId = promptId, ClientMessageId = send.ClientMessageId, Text = text,
                        AgentInstance = send.AgentInstance, EditorContext = editorContext?.EditorContext,
                        Attachments = attachments.Count == 0 ? null : attachments.Select(path => new ChatAttachment { Path = path }).ToList(),
                        RequestFiles = PromptDisplay.CopyRequestFiles(send.RequestFiles),
                    });
                    _ui.Post(UiMessageTypes.ChatTurnEnd, new ChatTurnEndPayload { PromptId = promptId, Status = status, Error = failure });
                }
            }
        }

        /// <summary>
        /// Sends one <c>steer</c> to the agent running now, composed like a chat message (editor context, attachment paths). The run is
        /// checked under the state lock right before the command; when it ended before omp took the message, the message is taken back
        /// from omp's steering queue (so it cannot wait there for an unrelated later run) and the caller sends it normally.
        /// </summary>
        private async Task<SteerOutcome> SteerAsync(ChatSendPayload send, long generation)
        {
            var text = send.Text ?? string.Empty;
            var attachments = (send.Attachments ?? new List<ChatAttachment>()).Where(a => !string.IsNullOrWhiteSpace(a?.Path)).Select(a => a.Path).ToList();
            if (string.IsNullOrWhiteSpace(text) && attachments.Count == 0 && !(send.RequestFiles?.Count > 0)) return SteerOutcome.Failed;
            OmpConnection? connection;
            CancellationToken epoch;
            lock (_stateLock)
            {
                if (!IsSteerableLocked(generation)) return SteerOutcome.NotRunning;
                epoch = _epoch.Token;
                connection = _epochConnection;
            }

            using var sending = CancellationTokenSource.CreateLinkedTokenSource(_disposeCts.Token, epoch);
            try
            {
                ValidateQueuedSend(send);
                EditorContextResult? editorContext = null;
                if (send.IncludeEditorContext && !(send.RequestFiles?.Count > 0))
                {
                    try
                    {
                        var contextTask = _contextBuilder.BuildAsync(sending.Token);
                        contextTask.Observe();
                        editorContext = await contextTask.WithCancellation(sending.Token).ConfigureAwait(false);
                    }
                    catch (Exception ex) when (!(ex is OperationCanceledException)) { _logger.Warn("Could not build the editor context: " + ex.Message); }
                }

                var message = await ComposeMessageAsync(editorContext?.Text, text, attachments, sending.Token, send.RequestFiles).ConfigureAwait(false);
                var command = OmpCommands.Steer(message);
                if (Pomp.Transport.NdjsonWriter.Encode(command).Length > MaxPromptFrameBytes)
                {
                    PostNotice("error", PompLanguage.Pick("메시지가 너무 커서 작업 중인 에이전트에게 보내지 못했어요.", "The message is too large to send to the working agent."));
                    return SteerOutcome.Failed;
                }

                lock (_stateLock)
                {
                    if (!IsSteerableLocked(generation) || !ReferenceEquals(connection, _epochConnection)) return SteerOutcome.NotRunning;
                }

                var response = await connection!.SendCommandAsync(command, _options.PromptTimeout, sending.Token).ConfigureAwait(false);
                if (!response.Success)
                {
                    PostNotice("warning", PompLanguage.Pick("작업 중인 에이전트에게 보내지 못했어요: ", "Could not send to the working agent: ") + response.Error);
                    return SteerOutcome.Failed;
                }

                var steerId = "steer-" + Interlocked.Increment(ref _promptCounter).ToString(System.Globalization.CultureInfo.InvariantCulture);
                bool running;
                lock (_stateLock)
                {
                    running = _turns.Count > 0 || _info.IsStreaming;
                    if (running) _steersSent.Add(steerId);
                }

                if (!running)
                {
                    var removed = await connection.SendCommandAsync(OmpCommands.RemoveQueuedMessage(message, "steering"), TimeSpan.FromSeconds(15), sending.Token).ConfigureAwait(false);
                    if (removed.Success && removed.Data.GetBool("removed") == true) return SteerOutcome.NotRunning;
                    _logger.Info("A steered message reached omp after its run ended; omp handles it.");
                }

                _ui.Post(UiMessageTypes.ChatSteered, new ChatSteeredPayload
                {
                    SteerId = steerId,
                    ClientMessageId = string.IsNullOrEmpty(send.ClientMessageId) ? null : send.ClientMessageId,
                    Text = text,
                    Attachments = attachments.Count == 0 ? null : attachments.Select(path => new ChatAttachment { Path = path }).ToList(),
                    RequestFiles = PromptDisplay.CopyRequestFiles(send.RequestFiles),
                    EditorContext = editorContext?.EditorContext,
                });
                return SteerOutcome.Sent;
            }
            catch (OperationCanceledException) when (!_disposeCts.IsCancellationRequested)
            {
                PostNotice("warning", PompLanguage.Pick("연결이 끊겨 작업 중인 에이전트에게 보내지 못했어요.", "The connection was lost before the message reached the working agent."));
                return SteerOutcome.Failed;
            }
            catch (TimeoutException ex)
            {
                // The frame was written: omp may still take it, so it is not resent.
                _logger.Warn("omp did not acknowledge the steer: " + ex.Message);
                PostNotice("warning", PompLanguage.Pick("작업 중인 에이전트가 메시지를 받았는지 확인되지 않았어요.", "The working agent did not confirm the message in time."));
                return SteerOutcome.Sent;
            }
            catch (Exception ex) when (!(ex is OperationCanceledException))
            {
                _logger.Warn("Steering failed: " + ex.Message);
                PostNotice("warning", PompLanguage.Pick("작업 중인 에이전트에게 보내지 못했어요: ", "Could not send to the working agent: ") + ex.Message);
                return SteerOutcome.Failed;
            }
        }

        /// <summary>The steers this run took, oldest first (and clears them): Stop withdraws omp's queue instead of a plain abort when any.</summary>
        private List<string> TakeSteersSent()
        {
            lock (_stateLock)
            {
                var sent = _steersSent.ToList();
                _steersSent.Clear();
                return sent;
            }
        }
    }
}
