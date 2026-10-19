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
    // Steering (docs/architecture.md "Steer"): a message for the agent that is working now, delivered at its next step. It never
    // registers a turn: the running turn ends as usual.
    public sealed partial class PompSession
    {
        private enum SteerOutcome { Sent, NotRunning, Failed }

        /// <summary>A steer was admitted during the current run: Stop must take it back from omp's queue (a plain abort would run it).</summary>
        private bool _steerSent;

        /// <summary>Goes to this conversation itself (no chosen team, agent or instance) and is no slash command, which steering does not run.</summary>
        private static bool CanSteer(ChatSendPayload send) =>
            string.IsNullOrWhiteSpace(send.Team) && string.IsNullOrWhiteSpace(send.Agent) && string.IsNullOrWhiteSpace(send.AgentInstance)
            && !(send.Text ?? string.Empty).TrimStart().StartsWith("/", StringComparison.Ordinal);

        private bool IsSteerableLocked(long generation) =>
            generation == _chatStopGeneration && (_turns.Count > 0 || _info.IsStreaming) && _bridge.State == BridgeState.Running && !_sessionSwitchUncertain;

        /// <summary>chat/steer: to the running agent now; with nothing running (or a message steering cannot carry) an ordinary send.</summary>
        private async Task HandleChatSteerAsync(ChatSendPayload send, long generation)
        {
            send = CopySend(send);
            if (CanSteer(send) && await SteerAsync(send, generation).ConfigureAwait(false) != SteerOutcome.NotRunning) return;
            await HandleChatSendAsync(send, generation).ConfigureAwait(false);
        }

        /// <summary>The queue's steer action: the item leaves the queue and goes to the running agent now, or is sent normally when nothing runs.</summary>
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
                    _chatQueue.Remove(item);
                    generation = _chatStopGeneration;
                    session = _queueSession;
                }
            }
            catch (Exception ex) { Reply(UiMessageTypes.ChatQueueReply, requestId, ex.Message); return; }

            PublishChatQueue();
            Reply(UiMessageTypes.ChatQueueReply, requestId, null);
            var send = CopySend(item.Send);
            send.ClientMessageId = (item.Send.ClientMessageId?.StartsWith(HookMessagePrefix, StringComparison.Ordinal) == true ? HookMessagePrefix : "queue-") + Guid.NewGuid().ToString("N");
            switch (await SteerAsync(send, generation).ConfigureAwait(false))
            {
                case SteerOutcome.NotRunning:
                    await HandleChatSendAsync(send, generation, expectedSession: session, bypassQueue: true, capturedPartner: item.Partner).ConfigureAwait(false);
                    break;
                case SteerOutcome.Failed:
                    // Not delivered: the item goes back to the front of the queue (unless the conversation changed meanwhile).
                    lock (_stateLock)
                    {
                        if (_chatQueue.Count == 0) _queueSession = session;
                        if (_queueSession == session) _chatQueue.Insert(0, item);
                    }

                    PublishChatQueue();
                    break;
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
            if (string.IsNullOrWhiteSpace(text) && attachments.Count == 0) return SteerOutcome.Failed;
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
                EditorContextResult? editorContext = null;
                if (send.IncludeEditorContext)
                {
                    try
                    {
                        var contextTask = _contextBuilder.BuildAsync(sending.Token);
                        contextTask.Observe();
                        editorContext = await contextTask.WithCancellation(sending.Token).ConfigureAwait(false);
                    }
                    catch (Exception ex) when (!(ex is OperationCanceledException)) { _logger.Warn("Could not build the editor context: " + ex.Message); }
                }

                var message = await ComposeMessageAsync(editorContext?.Text, text, attachments, sending.Token).ConfigureAwait(false);
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

                bool running;
                lock (_stateLock)
                {
                    running = _turns.Count > 0 || _info.IsStreaming;
                    if (running) _steerSent = true;
                }

                if (!running)
                {
                    var removed = await connection.SendCommandAsync(OmpCommands.RemoveQueuedMessage(message, "steering"), TimeSpan.FromSeconds(15), sending.Token).ConfigureAwait(false);
                    if (removed.Success && removed.Data.GetBool("removed") == true) return SteerOutcome.NotRunning;
                    _logger.Info("A steered message reached omp after its run ended; omp handles it.");
                }

                _ui.Post(UiMessageTypes.ChatSteered, new ChatSteeredPayload
                {
                    SteerId = "steer-" + Interlocked.Increment(ref _promptCounter).ToString(System.Globalization.CultureInfo.InvariantCulture),
                    ClientMessageId = string.IsNullOrEmpty(send.ClientMessageId) ? null : send.ClientMessageId,
                    Text = text,
                    Attachments = attachments.Count == 0 ? null : attachments.Select(path => new ChatAttachment { Path = path }).ToList(),
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

        /// <summary>True once (and clears it) when this run took a steer, so Stop withdraws omp's queue instead of a plain abort.</summary>
        private bool TakeSteerSent()
        {
            lock (_stateLock)
            {
                var sent = _steerSent;
                _steerSent = false;
                return sent;
            }
        }
    }
}
