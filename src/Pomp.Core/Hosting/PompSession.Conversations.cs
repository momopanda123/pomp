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
using Pomp.Ui;

namespace Pomp.Hosting
{
    public sealed partial class PompSession
    {
        private ConversationPartnerStore _partners = null!;
        private ConversationPartnerPayload _partner = new ConversationPartnerPayload();
        private string? _partnerSession, _partnerInstance, _partnerMessage;
        // Agent name → the instance its partner tab talks to in this conversation, so switching tabs back and forth keeps each
        // agent's own context (docs/agent-teams.md §8.2). Engine jobs do not outlive the conversation or the engine.
        private readonly Dictionary<string, string> _partnerInstances = new Dictionary<string, string>(StringComparer.Ordinal);
        private readonly Dictionary<string, Dictionary<string, string>> _teamInstances = new Dictionary<string, Dictionary<string, string>>(StringComparer.Ordinal);
        private readonly HashSet<string> _unavailableTeams = new HashSet<string>(StringComparer.Ordinal);
        private OmpConnection? _teamInstancesConnection;
        private AgentRunHistory? _runHistory;
        private string _runHistoryRoot = "";
        private readonly List<QueuedChat> _chatQueue = new List<QueuedChat>();
        private string? _queueSession, _queueReason;
        private bool _queuePaused, _queueDispatchScheduled;
        private static ConversationPartnerPayload CopyPartner(ConversationPartnerPayload value) => new ConversationPartnerPayload { Kind = value.Kind, Id = value.Id };
        private static string? Target(string? value) => string.IsNullOrWhiteSpace(value) ? null : value!.Trim();
        private static ChatSendPayload CopySend(ChatSendPayload value) => new ChatSendPayload { ClientMessageId = value.ClientMessageId, Text = value.Text ?? "", IncludeEditorContext = value.IncludeEditorContext, Team = Target(value.Team), Agent = Target(value.Agent), AgentInstance = Target(value.AgentInstance), Main = value.Main == true ? true : null, Attachments = value.Attachments?.Where(a => a != null).Select(a => new ChatAttachment { Path = a.Path }).ToList() };
        private ConversationPartnerPayload ValidatePartner(ConversationPartnerPayload value)
        {
            if (value.Kind == "main") return new ConversationPartnerPayload();
            var roster = GetAgentRoster();
            if (value.Kind == "agent" && roster.ListRoles().Any(r => r.Name == value.Id && !r.Shadowed && string.IsNullOrEmpty(r.Error))) return CopyPartner(value);
            if (value.Kind == "team" && roster.ListTeams().Any(t => t.Id == value.Id && !t.Shadowed && string.IsNullOrEmpty(t.Error))) return CopyPartner(value);
            throw new InvalidOperationException(PompLanguage.Pick("대화 상대를 찾을 수 없어요.", "Conversation partner was not found."));
        }
        private ConversationPartnerStatePayload PartnerView()
        {
            ConversationPartnerStatePayload state;
            lock (_stateLock) state = new ConversationPartnerStatePayload { SessionId = _partnerSession, Partner = CopyPartner(_partner), Instance = _partnerInstance, Message = _partnerMessage };
            if (state.Partner.Kind == "agent") state.Title = GetAgentRoster().ListRoles().FirstOrDefault(r => r.Name == state.Partner.Id && !r.Shadowed && string.IsNullOrEmpty(r.Error))?.Title ?? state.Partner.Id;
            if (state.Partner.Kind == "team") state.Title = GetAgentRoster().ListTeams().FirstOrDefault(t => t.Id == state.Partner.Id && !t.Shadowed && string.IsNullOrEmpty(t.Error))?.Title ?? state.Partner.Id;
            return state;
        }
        private void PublishPartner() => _ui.Post(UiMessageTypes.ConversationPartnerState, PartnerView(), UiDelivery.State);
        private Task HandlePartnerAsync(JObject args)
        {
            var requestId = (string?)args["requestId"] ?? "";
            try
            {
                var value = args["partner"] is JObject raw ? Pomp.Json.PompJson.ToObject<ConversationPartnerPayload>(raw) : null;
                if (value == null) throw new ArgumentException(PompLanguage.Pick("대화 상대가 비어 있어요.", "The conversation partner is empty."));
                value = ValidatePartner(value); string session, root;
                lock (_stateLock) { session = _info.SessionId ?? ""; root = _workingDirectory; }
                if (session.Length == 0) throw new InvalidOperationException(PompLanguage.Pick("대화가 연결되지 않았어요.", "No conversation is connected."));
                _partners.Set(root, session, value);
                lock (_stateLock)
                {
                    if (_partnerSession != session) { _partnerInstances.Clear(); _teamInstances.Clear(); _unavailableTeams.Clear(); }
                    SyncTeamInstancesLocked();
                    _partner = value; _partnerSession = session; _partnerMessage = null;
                    _partnerInstance = value.Kind == "agent" && _partnerInstances.TryGetValue(value.Id!, out var known) ? known : null;
                }
                PublishPartner(); Reply(UiMessageTypes.ConversationPartnerReply, requestId, null);
                HandleRecentSessionsAsync(new JObject { ["action"] = "refresh" }).Forget(_logger, "partner session list");
            }
            catch (Exception ex) { Reply(UiMessageTypes.ConversationPartnerReply, requestId, ex.Message); }
            return Task.CompletedTask;
        }
        // Job ids belong to one engine; retain only team ids so the next partner send can explain a restart.
        private void SyncTeamInstancesLocked()
        {
            if (ReferenceEquals(_teamInstancesConnection, _epochConnection) && _teamInstancesConnection?.IsClosed != true) return;
            foreach (var team in _teamInstances.Where(t => t.Value.Count > 0)) _unavailableTeams.Add(team.Key);
            _teamInstances.Clear(); _teamInstancesConnection = _epochConnection;
        }
        private void ConversationObserved(string? sessionId)
        {
            string root; bool changed, rootChanged, hadSession;
            lock (_stateLock) { SyncTeamInstancesLocked(); hadSession = _partnerSession != null; changed = _partnerSession != sessionId; root = _workingDirectory; rootChanged = root != _runHistoryRoot; }
            if (rootChanged && root.Length > 0)
            {
                var history = new AgentRunHistory(_environment.DataDirectory, root, _logger);
                lock (_stateLock) { _runHistory = history; _runHistoryRoot = root; }
                PublishRunHistory();
            }
            if (!changed) return;
            var value = _partners.Get(root, sessionId);
            lock (_stateLock) { _partnerSession = sessionId; _partner = value; _partnerInstance = null; _partnerInstances.Clear(); _teamInstances.Clear(); _unavailableTeams.Clear(); _partnerMessage = null; }
            PauseChatQueue(PompLanguage.Pick("대화가 바뀌었어요.", "Conversation changed."), onlyIfItems: !hadSession);
            PublishPartner();
        }
        private ConversationPartnerPayload? DefaultPartner(ChatSendPayload send)
        {
            // An explicit target, or an explicit "POMP itself" (page actions of the main conversation), overrides the partner.
            if (send.Main == true || !string.IsNullOrWhiteSpace(send.Team) || !string.IsNullOrWhiteSpace(send.Agent) || !string.IsNullOrWhiteSpace(send.AgentInstance)) return null;
            lock (_stateLock) return CopyPartner(_partner);
        }
        private void ResolvePartnerTarget(ChatSendPayload send, ConversationPartnerPayload? partner)
        {
            if (partner == null || partner.Kind == "main") return;
            partner = ValidatePartner(partner);
            send.Team = null; send.Agent = null; send.AgentInstance = null;
            if (partner.Kind == "team") { send.Team = partner.Id; return; }
            string? instance, session; bool samePartner;
            lock (_stateLock) { samePartner = _partner.Kind == partner.Kind && _partner.Id == partner.Id; instance = samePartner ? _partnerInstance : null; session = _info.SessionId; }
            var jobs = _backgroundAgents.View();
            if (instance != null && (string?)jobs["sessionId"] == session && jobs["jobs"] is JArray list && list.Any(j => (string?)j["id"] == instance)) send.AgentInstance = instance;
            else
            {
                send.Agent = partner.Id;
                if (instance != null)
                {
                    lock (_stateLock) { _partnerInstance = null; _partnerInstances.Remove(partner.Id!); _partnerMessage = PompLanguage.Pick("이전 에이전트 인스턴스를 찾을 수 없어 새 에이전트로 이어서 시작해요.", "The previous agent instance is unavailable; starting a new agent instance."); }
                    PublishPartner();
                }
            }
        }
        private string TeamContinuation(AgentTeam team)
        {
            // Isolated members cannot be resumed later (docs/agent-teams.md §13): an isolation team always starts new ones, in Git only.
            if (team.Isolation)
            {
                string root; lock (_stateLock) { root = _workingDirectory; _teamInstances.Remove(team.Id); }
                if (FindGitRoot(root) == null)
                    throw new InvalidOperationException(PompLanguage.Pick("작업 영역이 Git 저장소가 아니에요.", "The workspace is not a Git repository."));
                return " --new";
            }

            var view = _backgroundAgents.View(); bool partnerChanged = false; string continuation;
            lock (_stateLock)
            {
                SyncTeamInstancesLocked(); _teamInstances.TryGetValue(team.Id, out var members);
                var hadMembers = members?.Count > 0 || _unavailableTeams.Contains(team.Id);
                var jobs = (string?)view["sessionId"] == _info.SessionId ? (view["jobs"] as JArray)?.OfType<JObject>().Select(j => (string?)j["id"]).ToArray() : null;
                var available = (members ?? new Dictionary<string, string>()).Where(m => team.Members.Contains(m.Key) && AgentRoster.IsValidName(m.Key) && !m.Key.Any(char.IsControl) && AgentInstanceId.IsMatch(m.Value) && !m.Value.Any(char.IsControl) && jobs?.Contains(m.Value) == true).Take(6).ToArray();
                if (members != null) { members.Clear(); foreach (var member in available) members[member.Key] = member.Value; }
                // Always one marker, so a user message that itself starts with "--continue" is never read as continuation metadata.
                continuation = available.Length == 0 ? " --new" : " --continue " + string.Join(",", available.Select(m => m.Key + "=" + m.Value));
                if (available.Length == 0 && hadMembers && _partner.Kind == "team" && _partner.Id == team.Id)
                {
                    _partnerMessage = PompLanguage.Pick("이전 구성원을 찾을 수 없어 새로 시작해요", "The previous members are unavailable; starting the team again"); partnerChanged = true;
                }
                _unavailableTeams.Remove(team.Id);
            }
            if (partnerChanged) PublishPartner();
            return continuation;
        }
        private ChatQueueStatePayload QueueView()
        {
            lock (_stateLock) return new ChatQueueStatePayload { SessionId = _chatQueue.Count == 0 ? null : _queueSession, Paused = _queuePaused, Reason = _queueReason, Items = _chatQueue.Select(q => new ChatQueueItemPayload { Id = q.Id, Text = q.Send.Text, IncludeEditorContext = q.Send.IncludeEditorContext, Attachments = CopySend(q.Send).Attachments, Team = q.Send.Team ?? (q.Partner?.Kind == "team" ? q.Partner.Id : null), Agent = q.Send.Agent ?? (q.Partner?.Kind == "agent" ? q.Partner.Id : null), AgentInstance = q.Send.AgentInstance, CreatedAt = q.CreatedAt }).ToList() };
        }
        private int QueueBytes() => System.Text.Encoding.UTF8.GetByteCount(Pomp.Json.PompJson.Serialize(QueueView()));
        private void PublishChatQueue() => _ui.Post(UiMessageTypes.ChatQueueState, QueueView(), UiDelivery.State);
        private void PauseChatQueue(string reason, bool onlyIfItems = false)
        {
            lock (_stateLock) { if (onlyIfItems && _chatQueue.Count == 0) return; _queuePaused = true; _queueReason = reason; }
            PublishChatQueue();
        }
        private static void ValidateQueuedSend(ChatSendPayload send)
        {
            if (string.IsNullOrWhiteSpace(send.Text) && (send.Attachments == null || send.Attachments.Count == 0)) throw new ArgumentException(PompLanguage.Pick("메시지가 비어 있어요.", "Message is empty."));
            if (send.Attachments?.Any(a => a.Path == null || a.Path.Length > 4096 || a.Path.Any(char.IsControl)) == true) throw new ArgumentException(PompLanguage.Pick("첨부 파일 경로가 올바르지 않아요.", "An attachment path is invalid."));
            // The attached paths go into the same prompt, so their number is bounded only by the frame size.
            var paths = send.Attachments?.Select(a => "- " + a.Path).ToList() ?? new List<string>();
            var message = paths.Count == 0 ? send.Text : PromptDisplay.AttachmentsHeader + "\n" + string.Join("\n", paths) + "\n\n" + send.Text;
            var probe = new JObject { ["type"] = "prompt", ["id"] = "prompt-queue", ["message"] = message };
            if (Pomp.Transport.NdjsonWriter.Encode(probe).Length > MaxPromptFrameBytes) throw new ArgumentException(PompLanguage.Pick("메시지가 너무 커요.", "Message is too large."));
            if (new[] { send.Team, send.Agent, send.AgentInstance }.Count(t => !string.IsNullOrWhiteSpace(t)) > 1) throw new ArgumentException(PompLanguage.Pick("보낼 대상이 너무 많아요.", "Too many targets."));
        }
        // Called under the send decision's state lock; Stop must not race a first-item pause reset.
        private QueuedChat EnqueueChatLocked(ChatSendPayload send, ConversationPartnerPayload? partner, long generation, bool allowResume = true)
        {
            ValidateQueuedSend(send);
            if (_sessionSwitchUncertain || _recoveryStatus != "ready" || _bridge.State != BridgeState.Running)
                throw new InvalidOperationException(PompLanguage.Pick("활성 대화가 확인되지 않았어요.", "The active conversation is not confirmed."));
            // No count limit: the queue is bounded by its size (QueueBytes below).
            var empty = _chatQueue.Count == 0;
            if (empty) _queueSession = _info.SessionId;
            if (_queueSession != _info.SessionId) throw new InvalidOperationException(PompLanguage.Pick("이 대기열은 다른 대화의 항목이에요.", "This queue belongs to another conversation."));
            var item = new QueuedChat { Id = Guid.NewGuid().ToString("N"), Send = CopySend(send), Partner = partner == null ? null : CopyPartner(partner) };
            _chatQueue.Add(item);
            if (QueueBytes() > 1800 * 1024) { _chatQueue.Remove(item); throw new InvalidOperationException(PompLanguage.Pick("대기열이 너무 커요.", "The queue is too large.")); }
            if (empty && allowResume && generation == _chatStopGeneration) { _queuePaused = false; _queueReason = null; }
            return item;
        }
        private void PublishQueuedChat(ChatSendPayload send, QueuedChat item)
        {
            _ui.Post(UiMessageTypes.ChatQueued, new ChatQueuedPayload { ClientMessageId = send.ClientMessageId, ItemId = item.Id }, UiDelivery.State);
            PublishChatQueue();
        }
        private Task HandleQueueAsync(JObject args)
        {
            var requestId = (string?)args["requestId"] ?? "";
            try
            {
                var command = Pomp.Json.PompJson.ToObject<ChatQueueCommandPayload>(args) ?? throw new ArgumentException(PompLanguage.Pick("대기열 명령 형식이 올바르지 않아요.", "The queue command is invalid."));
                lock (_stateLock)
                {
                    var item = _chatQueue.FirstOrDefault(q => q.Id == command.Id);
                    switch (command.Action)
                    {
                        case "clear": _chatQueue.Clear(); _queueSession = null; break;
                        case "pause": _queuePaused = true; _queueReason = PompLanguage.Pick("사용자가 대기열을 일시정지했어요.", "Queue paused by the user."); break;
                        case "resume":
                            if (_sessionSwitchUncertain || _recoveryStatus != "ready" || _bridge.State != BridgeState.Running) throw new InvalidOperationException(PompLanguage.Pick("대화가 복원되지 않았어요.", "The conversation is not recovered."));
                            if (_chatQueue.Count > 0 && _queueSession != _info.SessionId) throw new InvalidOperationException(PompLanguage.Pick("이 대기열은 다른 대화의 항목이에요.", "This queue belongs to another conversation."));
                            _queuePaused = false; _queueReason = null; break;
                        case "edit":
                            if (item == null || command.Text == null) throw new ArgumentException(PompLanguage.Pick("대기열 항목이나 글이 올바르지 않아요.", "The queue item or text is invalid."));
                            var candidate = CopySend(item.Send); candidate.Text = command.Text; ValidateQueuedSend(candidate);
                            var previous = item.Send; item.Send = candidate;
                            if (QueueBytes() > 1800 * 1024) { item.Send = previous; throw new InvalidOperationException(PompLanguage.Pick("대기열이 너무 커요.", "The queue is too large.")); }
                            break;
                        case "remove":
                            if (item == null) throw new ArgumentException(PompLanguage.Pick("대기열 항목을 찾을 수 없어요.", "Queue item was not found."));
                            _chatQueue.Remove(item); break;
                        case "move":
                            if (item == null || !command.Index.HasValue || command.Index < 0 || command.Index >= _chatQueue.Count) throw new ArgumentException(PompLanguage.Pick("대기열 위치가 올바르지 않아요.", "The queue position is invalid."));
                            _chatQueue.Remove(item); _chatQueue.Insert(command.Index.Value, item); break;
                        default: throw new ArgumentException(PompLanguage.Pick("알 수 없는 대기열 명령이에요.", "Unknown queue command."));
                    }
                }
                PublishChatQueue(); Reply(UiMessageTypes.ChatQueueReply, requestId, null); ScheduleQueueDispatch();
            }
            catch (Exception ex) { Reply(UiMessageTypes.ChatQueueReply, requestId, ex.Message); }
            return Task.CompletedTask;
        }
        private void ScheduleQueueDispatch()
        {
            lock (_stateLock)
            {
                if (_queueDispatchScheduled || _queuePaused || _chatQueue.Count == 0 || _queueSession != _info.SessionId || _turns.Count > 0 || _info.IsStreaming || _bridge.State != BridgeState.Running || _sessionSwitchUncertain) return;
                _queueDispatchScheduled = true;
                _commands.Enqueue(async () =>
                {
                    QueuedChat? next = null; long generation;
                    lock (_stateLock)
                    {
                        _queueDispatchScheduled = false; generation = _chatStopGeneration;
                        if (!_queuePaused && _chatQueue.Count > 0 && _queueSession == _info.SessionId && _turns.Count == 0 && !_info.IsStreaming && _bridge.State == BridgeState.Running && !_sessionSwitchUncertain) next = _chatQueue[0];
                    }
                    if (next == null) return;
                    // A message an event rule queued keeps its marker: its turn must never forward again (PompSession.AgentHooks.cs).
                    var send = CopySend(next.Send);
                    send.ClientMessageId = (next.Send.ClientMessageId?.StartsWith(HookMessagePrefix, StringComparison.Ordinal) == true ? HookMessagePrefix : "queue-") + Guid.NewGuid().ToString("N");
                    await HandleChatSendAsync(send, generation, expectedSession: _queueSession, bypassQueue: true, capturedPartner: next.Partner, queuedItemId: next.Id).ConfigureAwait(false);
                });
            }
        }
        private AgentRunHistory? RunHistory() { lock (_stateLock) return _runHistory; }
        private void PublishRunHistory() => _ui.Post(UiMessageTypes.AgentsHistoryState, RunHistory()?.View() ?? new AgentsHistoryStatePayload(), UiDelivery.State);
        private async Task HandleRunHistoryAsync(JObject args)
        {
            var requestId = (string?)args["requestId"] ?? "";
            try
            {
                var command = Pomp.Json.PompJson.ToObject<AgentsHistoryCommandPayload>(args) ?? throw new ArgumentException(PompLanguage.Pick("기록 명령 형식이 올바르지 않아요.", "The history command is invalid."));
                var history = RunHistory() ?? throw new InvalidOperationException(PompLanguage.Pick("작업 영역이 연결되지 않았어요.", "No workspace is connected."));
                if (command.Action == "refresh") { PublishRunHistory(); Reply(UiMessageTypes.AgentsHistoryReply, requestId, null); return; }
                var entry = history.Command(command);
                if (command.Action == "rerun")
                {
                    var send = new ChatSendPayload { ClientMessageId = "history-" + Guid.NewGuid().ToString("N"), Text = entry.Text, IncludeEditorContext = false, Team = entry.Target.Kind == "team" ? entry.Target.Id : null, Agent = entry.Target.Kind == "agent" ? entry.Target.Id : null, AgentInstance = entry.Target.Kind == "dm" ? entry.Target.Id : null };
                    long generation; QueuedChat? queued;
                    lock (_stateLock) { generation = _chatStopGeneration; queued = _turns.Count > 0 || _info.IsStreaming || _chatQueue.Count > 0 ? EnqueueChatLocked(send, null, generation) : null; }
                    if (queued != null) { PublishQueuedChat(send, queued); ScheduleQueueDispatch(); }
                    else if (!await HandleChatSendAsync(send, generation).ConfigureAwait(false)) throw new InvalidOperationException(PompLanguage.Pick("실행 기록을 다시 보내지 못했어요.", "Run history could not be sent again."));
                }
                PublishRunHistory(); Reply(UiMessageTypes.AgentsHistoryReply, requestId, null);
            }
            catch (Exception ex) { Reply(UiMessageTypes.AgentsHistoryReply, requestId, ex.Message); }
        }
        private void BeginRun(TurnState turn, ChatSendPayload send, ConversationPartnerPayload? partner)
        {
            var kind = send.Team != null ? "team" : send.Agent != null ? "agent" : send.AgentInstance != null ? "dm" : null; if (kind == null) return;
            var id = send.Team ?? send.Agent ?? send.AgentInstance!;
            var roster = GetAgentRoster(); var team = kind == "team" ? roster.ListTeams().FirstOrDefault(t => string.Equals(t.Id, id, StringComparison.OrdinalIgnoreCase) && !t.Shadowed && string.IsNullOrEmpty(t.Error)) : null;
            var title = kind == "team" ? team?.Title : kind == "agent" ? roster.ListRoles().FirstOrDefault(r => r.Name == id && !r.Shadowed)?.Title : (_backgroundAgents.View()["jobs"] as JArray)?.OfType<JObject>().FirstOrDefault(j => (string?)j["id"] == id)?["agent"]?.ToString();
            var history = RunHistory();
            string session;
            lock (_stateLock)
            {
                SyncTeamInstancesLocked(); session = _info.SessionId ?? ""; turn.PartnerAgent = partner?.Kind == "agent" && send.Agent != null ? partner.Id : null;
                turn.Team = team?.Id; turn.TeamMembers = team == null ? null : new HashSet<string>(team.Members, StringComparer.Ordinal); turn.TeamConnection = _epochConnection;
            }
            if (history == null) return;
            try
            {
                var runId = history.Begin(session, new AgentRunTargetPayload { Kind = kind, Id = id, Title = title ?? id }, send.Text, turn.PromptId);
                bool active;
                lock (_stateLock) { turn.History = history; turn.RunId = runId; active = _turns.ContainsKey(turn.PromptId); }
                if (!active) history.End(runId, "error");
                PublishRunHistory();
            }
            catch (Exception ex) { _logger.Warn("Run history could not be started: " + ex.Message); }
        }
        private void ObserveRunJob(string session, JObject job, bool fresh)
        {
            TurnState? turn; AgentRunHistory? history; bool partnerChanged = false; bool duringHookTurn;
            lock (_stateLock)
            {
                if (_info.SessionId != session) return;
                SyncTeamInstancesLocked(); turn = _turns.Values.FirstOrDefault(); history = turn?.History ?? _runHistory;
                // The job's own turn when known (it may have ended already), else the turn running now.
                var ownPrompt = (string?)job["promptId"];
                duringHookTurn = !string.IsNullOrEmpty(ownPrompt) && _turns.TryGetValue(ownPrompt!, out var own)
                    ? IsHookTurn(own)
                    : (!string.IsNullOrEmpty(ownPrompt) && _hookPrompts.Contains(ownPrompt!)) || (turn != null && IsHookTurn(turn));
                if (fresh && _turns.TryGetValue((string?)job["promptId"] ?? "", out var teamTurn) && teamTurn.Team != null && teamTurn.TeamMembers != null && ReferenceEquals(teamTurn.TeamConnection, _teamInstancesConnection) && _teamInstancesConnection?.IsClosed != true)
                {
                    var role = (string?)job["agent"] ?? ""; var id = (string?)job["id"] ?? "";
                    if (teamTurn.TeamMembers.Contains(role) && AgentRoster.IsValidName(role) && !role.Any(char.IsControl) && AgentInstanceId.IsMatch(id) && !id.Any(char.IsControl))
                    {
                        if (!_teamInstances.TryGetValue(teamTurn.Team, out var members)) _teamInstances[teamTurn.Team] = members = new Dictionary<string, string>(StringComparer.Ordinal);
                        members[role] = id; _unavailableTeams.Remove(teamTurn.Team);
                        if (_partner.Kind == "team" && _partner.Id == teamTurn.Team && _partnerMessage != null) { _partnerMessage = null; partnerChanged = true; }
                    }
                }
                if (fresh && turn?.PartnerAgent != null && !turn.PartnerJobCaptured && turn.PartnerAgent == (string?)job["agent"] && AgentInstanceId.IsMatch((string?)job["id"] ?? ""))
                {
                    // Remembered for that agent even if the user switched tabs meanwhile; the active tab adopts it only if it is that agent.
                    turn.PartnerJobCaptured = true; _partnerInstances[turn.PartnerAgent] = (string)job["id"]!;
                    if (_partner.Kind == "agent" && _partner.Id == turn.PartnerAgent && _partnerInstance == null) { _partnerInstance = (string?)job["id"]; _partnerMessage = null; partnerChanged = true; }
                }
            }
            try { if (history?.Observe(session, turn?.RunId, job, fresh) == true) PublishRunHistory(); }
            catch (Exception ex) { _logger.Warn("Run history progress could not be saved: " + ex.Message); }
            if (partnerChanged) PublishPartner();
            ObserveHookJob(job, fresh, duringHookTurn);
        }
        private void Reply(string type, string requestId, string? error) => _ui.Post(type, new UiCommandReplyPayload { RequestId = requestId, Ok = error == null, Error = error }, UiDelivery.State);
        private sealed class QueuedChat
        {
            public string Id { get; set; } = "";
            public ChatSendPayload Send { get; set; } = new ChatSendPayload();
            public ConversationPartnerPayload? Partner { get; set; }
            public string CreatedAt { get; } = DateTimeOffset.UtcNow.ToString("O");
        }
    }
}
