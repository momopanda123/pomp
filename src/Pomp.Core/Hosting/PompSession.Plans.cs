using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;
using Pomp.Bridge;
using Pomp.Internal;
using Pomp.Ui;

namespace Pomp.Hosting
{
    public sealed partial class PompSession
    {
        internal ConversationPlanPayload? ConversationPlan
        {
            get
            {
                ConversationPlanPayload plan;
                lock (_stateLock)
                {
                    if (string.IsNullOrEmpty(_info.SessionId)) return null;
                    plan = new ConversationPlanPayload
                    {
                        SessionId = _info.SessionId!, Title = _info.SessionName ?? "", Partner = CopyPartner(_partner),
                        Ready = Volatile.Read(ref _disposed) == 0 && Volatile.Read(ref _workspaceSwitching) == 0 &&
                            _bridge.State == BridgeState.Running && _recoveryStatus == "ready" && !_sessionSwitchUncertain,
                        IsStreaming = _info.IsStreaming || _turns.Count > 0, HasPendingAsyncWork = _info.HasPendingAsyncWork,
                        PlanTaskCount = _info.PlanTaskCount, PlanOpenCount = _info.PlanOpenCount, PlanRevision = _info.PlanRevision,
                    };
                }
                if (string.IsNullOrWhiteSpace(plan.Title)) plan.Title = plan.Partner.Id ?? PompLanguage.Pick("새 대화", "New conversation");
                return plan;
            }
        }

        // Replies use the requesting page, not this engine's possibly detached chat channel.
        internal Task RunPlanCommandAsync(UiMessage message, Action<UiMessage> respond)
        {
            var args = message.Payload ?? new JObject();
            void Failure(Exception ex)
            {
                var type = message.Type == UiMessageTypes.AgentPlanPage ? UiMessageTypes.AgentPlanPageResult : UiMessageTypes.AgentPlanResult;
                respond(new UiMessage(type, new UiCommandReplyPayload { RequestId = args.GetString("requestId") ?? "", Ok = false, Error = PlanErrorText(ex) }));
            }
            async Task Execute()
            {
                try
                {
                    RequirePlanScope(args);
                    switch (message.Type)
                    {
                        case UiMessageTypes.AgentPlanPage: HandleAgentPlanPage(args, respond); break;
                        case UiMessageTypes.AgentPlan: await HandleAgentPlanAsync(args, respond).ConfigureAwait(false); break;
                        case UiMessageTypes.AgentPlanEdit: await HandleAgentPlanEditAsync(args, respond).ConfigureAwait(false); break;
                        case UiMessageTypes.AgentPlanAction:
                            await HandlePlanActionAsync(args).ConfigureAwait(false);
                            PostPlanResult(args.GetString("requestId"), null, respond);
                            break;
                        default: throw new ArgumentException("Unknown plan command.");
                    }
                }
                catch (Exception ex) { Failure(ex); }
            }
            if (message.Type == UiMessageTypes.AgentPlanPage) return Task.Run(Execute);
            if (message.Type == UiMessageTypes.AgentPlanAction && args.GetString("action") == "stop")
            {
                try
                {
                    RequirePlanScope(args);
                    RequirePlanEditable(args.GetString("sessionId") ?? "", whileWorking: true);
                    // Like chat/stop, release an outstanding prompt response immediately; Execute rechecks identity in the queue.
                    return QueueChatStop(Execute);
                }
                catch (Exception ex) { Failure(ex); return Task.CompletedTask; }
            }
            return _commands.Enqueue(Execute);
        }

        private void RequirePlanScope(JObject args)
        {
            lock (_stateLock)
            {
                if (Volatile.Read(ref _disposed) != 0 || Volatile.Read(ref _workspaceSwitching) != 0 ||
                    string.IsNullOrEmpty(args.GetString("root")) ||
                    !string.Equals(args.GetString("root")!.TrimEnd('\\', '/'), _workingDirectory.TrimEnd('\\', '/'), StringComparison.OrdinalIgnoreCase) ||
                    string.IsNullOrEmpty(args.GetString("sessionId")) || args.GetString("sessionId") != _info.SessionId)
                    throw new InvalidOperationException(PompLanguage.Pick("작업 계획의 대화나 작업 영역이 바뀌었어요.", "The work plan's conversation or workspace changed."));
            }
        }

        private async Task HandlePlanActionAsync(JObject args)
        {
            var session = args.GetString("sessionId") ?? "";
            RequirePlanEditable(session, whileWorking: true);
            var action = args.GetString("action");
            if (action == "stop")
            {
                if (!await HandleChatStopAsync().ConfigureAwait(false))
                    throw new InvalidOperationException(PompLanguage.Pick("선택한 대화의 작업을 중지하지 못했어요.", "Could not stop the selected conversation's work."));
                return;
            }
            string text;
            if (action == "continue")
                text = PompLanguage.Pick("현재 작업 계획을 확인하고 미완료 단계부터 이어서 진행해 주세요. 기존 변경과 검증 결과를 먼저 확인하고, 완료하지 않은 작업을 완료했다고 보고하지 마세요.", "Review the current work plan and continue from the unfinished step. Check existing changes and verification evidence first; do not report unverified work as complete.");
            else if (action == "run")
            {
                var task = args.GetString("task");
                if (string.IsNullOrWhiteSpace(task)) throw new ArgumentException(PompLanguage.Pick("작업 내용이 비어 있어요.", "The task is empty."));
                var instruction = args.GetBool("marked") == true
                    ? PompLanguage.Pick("작업 계획에서 아래 작업을 지금 진행해 주세요. 계획에 진행 중으로 표시해 두었어요. 기존 변경과 검증 결과를 먼저 확인하고, 완료하지 않은 작업을 완료했다고 보고하지 마세요.", "Work on the task below from the work plan now; it is marked in progress in the plan. Check existing changes and verification evidence first; do not report unverified work as complete.")
                    : PompLanguage.Pick("지금 단계를 마치면 작업 계획의 아래 작업으로 넘어가 주세요. todo 도구의 start로 이 작업을 진행 중으로 바꾼 뒤 진행해 주세요. 기존 변경과 검증 결과를 먼저 확인하고, 완료하지 않은 작업을 완료했다고 보고하지 마세요.", "When you finish the current step, switch to the task below from the work plan: mark it in progress with the todo tool (start), then work on it. Check existing changes and verification evidence first; do not report unverified work as complete.");
                text = instruction + "\n\n" + task;
            }
            else throw new ArgumentException("Unknown plan action.");
            _goalRunner?.Stop(PompLanguage.Pick("작업 계획에서 직접 실행해서 목표 실행을 중지했어요.", "Goal execution stopped because work was started from the plan."));
            var send = new ChatSendPayload { ClientMessageId = "plan-" + args.GetString("requestId"), Text = text, IncludeEditorContext = false };
            long generation;
            lock (_stateLock) generation = _chatStopGeneration;
            var accepted = await HandleChatSendAsync(send, generation, expectedSession: session).ConfigureAwait(false);
            // Busy sends are admitted to this conversation's existing queue instead of starting another turn.
            lock (_stateLock) accepted |= _chatQueue.Any(item => item.Send.ClientMessageId == send.ClientMessageId);
            if (!accepted) throw new InvalidOperationException(PompLanguage.Pick("선택한 대화에 작업 요청을 보내지 못했어요.", "The work request could not be sent to the selected conversation."));
        }
    }
}
