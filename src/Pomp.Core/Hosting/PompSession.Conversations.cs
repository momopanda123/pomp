using System;
using System.Collections.Generic;
using System.IO;
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
    public sealed partial class PompSession
    {
        private ConversationTabStore _tabs = null!;
        // The current conversation's partner (docs/architecture.md §8.8): main for a POMP conversation, else the agent or team whose own conversation it is.
        private ConversationPartnerPayload _partner = new ConversationPartnerPayload();
        private string? _partnerSession, _partnerMessage;
        // The partner a conversation/tab "open" is starting a conversation for: the next conversation the engine reports becomes that partner's.
        private ConversationPartnerPayload? _newConversationPartner;
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
        private ConversationTabsStatePayload TabsView()
        {
            ConversationTabsStatePayload state; string root;
            lock (_stateLock) { root = _workingDirectory; state = new ConversationTabsStatePayload { SessionId = _partnerSession, Partner = CopyPartner(_partner), Message = _partnerMessage }; }
            var tabs = _tabs.Read(root);
            var roster = GetAgentRoster(); var roles = roster.ListRoles(); var teams = roster.ListTeams();
            string? Title(ConversationPartnerPayload partner) => partner.Kind == "agent" ? roles.FirstOrDefault(r => r.Name == partner.Id && !r.Shadowed && string.IsNullOrEmpty(r.Error))?.Title ?? partner.Id
                : partner.Kind == "team" ? teams.FirstOrDefault(t => t.Id == partner.Id && !t.Shadowed && string.IsNullOrEmpty(t.Error))?.Title ?? partner.Id : null;
            state.Title = Title(state.Partner); state.Main = tabs.Main;
            state.Tabs = tabs.Conversations.Where(e => e.Open).Select(e => new ConversationTabPayload { SessionId = e.SessionId, Partner = CopyPartner(e.Partner), Title = Title(e.Partner) }).ToList();
            return state;
        }
        private void PublishTabs() => _ui.Post(UiMessageTypes.ConversationTabsState, TabsView(), UiDelivery.State);
        /// <summary>
        /// conversation/tab (docs/ui-protocol.md "Conversation tabs"): every tab is its own conversation, so opening, activating and closing the
        /// current tab switch the engine's conversation and answer with one <c>session/newResult</c>; closing another tab answers <c>conversation/tabReply</c>.
        /// </summary>
        private async Task HandleTabAsync(JObject args)
        {
            var command = Pomp.Json.PompJson.ToObject<ConversationTabCommandPayload>(args) ?? new ConversationTabCommandPayload();
            var requestId = command.RequestId ?? ""; var switching = command.Action != "close";
            try
            {
                string root, current;
                lock (_stateLock) { root = _workingDirectory; current = _info.SessionId ?? ""; }
                if (current.Length == 0) throw new InvalidOperationException(PompLanguage.Pick("대화가 연결되지 않았어요.", "No conversation is connected."));
                switch (command.Action)
                {
                    case "open":
                        var partner = command.Partner == null ? null : ValidatePartner(command.Partner);
                        if (partner == null || partner.Kind == "main") throw new ArgumentException(PompLanguage.Pick("대화 상대가 비어 있어요.", "The conversation partner is empty."));
                        var open = _tabs.OpenSession(root, partner);
                        if (open == current) throw new InvalidOperationException(PompLanguage.Pick("이미 열려 있는 탭이에요.", "That tab is already open."));
                        await SwitchToTabAsync(requestId, root, open, partner).ConfigureAwait(false);
                        break;
                    case "activate":
                        var tabs = _tabs.Read(root);
                        var tab = command.SessionId == null ? null : tabs.Conversations.FirstOrDefault(e => e.Open && e.SessionId == command.SessionId)
                            ?? throw new InvalidOperationException(PompLanguage.Pick("탭을 찾을 수 없어요.", "The tab was not found."));
                        var target = tab?.SessionId ?? tabs.Main;
                        if (target == current) throw new InvalidOperationException(PompLanguage.Pick("이미 열려 있는 탭이에요.", "That tab is already open."));
                        if (target == null) await StartConversationAsync(requestId, null).ConfigureAwait(false);
                        else await SwitchToTabAsync(requestId, root, target, tab?.Partner).ConfigureAwait(false);
                        break;
                    case "close":
                        if (string.IsNullOrEmpty(command.SessionId)) throw new ArgumentException(PompLanguage.Pick("닫을 탭이 없어요.", "No tab to close."));
                        // Closing the current tab returns to the Default tab: check that the engine can switch before the tab goes.
                        if (command.SessionId == current) { switching = true; RequireIdleForTabSwitch(); }
                        _tabs.Close(root, command.SessionId!); PublishTabs();
                        if (!switching) { Reply(UiMessageTypes.ConversationTabReply, requestId, null); break; }
                        var main = _tabs.Read(root).Main;
                        if (main == null) await StartConversationAsync(requestId, null).ConfigureAwait(false);
                        else await SwitchToTabAsync(requestId, root, main, null).ConfigureAwait(false);
                        break;
                    default: throw new ArgumentException(PompLanguage.Pick("알 수 없는 탭 명령이에요.", "Unknown tab command."));
                }
            }
            catch (Exception ex) when (!(ex is OperationCanceledException && _disposeCts.IsCancellationRequested))
            {
                if (switching) _ui.Post(UiMessageTypes.SessionNewResult, new SessionNewResultPayload { RequestId = requestId, Status = "failed", Message = ex.Message });
                else Reply(UiMessageTypes.ConversationTabReply, requestId, ex.Message);
            }
        }
        // One engine: another conversation only while nothing runs (a new conversation would abort the reply; a switch refuses anyway).
        private void RequireIdleForTabSwitch()
        {
            lock (_stateLock)
                if (_info.IsStreaming || _info.HasPendingAsyncWork || _turns.Count > 0)
                    throw new InvalidOperationException(PompLanguage.Pick("응답이 끝나거나 중지한 뒤에 탭을 바꿀 수 있어요.", "Finish or stop the reply before switching tabs."));
            if (_ui.PendingRequestCount > 0) throw new InvalidOperationException(PompLanguage.Pick("대기 중인 승인이나 질문이 있어요.", "An approval or question is pending."));
        }
        /// <summary>A new conversation for <paramref name="partner"/> (null: a POMP conversation, the Default tab's).</summary>
        private async Task StartConversationAsync(string requestId, ConversationPartnerPayload? partner)
        {
            RequireIdleForTabSwitch();
            _composerAuto?.Stop(PompLanguage.Pick("대화 전환을 요청했어요.", "Conversation switch requested."));
            lock (_stateLock) _newConversationPartner = partner == null ? null : CopyPartner(partner);
            try { await HandleNewSessionAsync(requestId).ConfigureAwait(false); }
            finally { lock (_stateLock) _newConversationPartner = null; }
        }
        /// <summary>
        /// session/new (the header's +, /new) and the new conversation after deleting the current one: a new conversation in the current tab,
        /// for its agent or team (a POMP conversation in the Default tab, or when that agent or team no longer exists).
        /// </summary>
        private async Task HandleNewInTabAsync(string? requestId)
        {
            ConversationPartnerPayload partner; lock (_stateLock) partner = CopyPartner(_partner);
            ConversationPartnerPayload? assign;
            try { assign = partner.Kind == "main" ? null : ValidatePartner(partner); }
            catch (InvalidOperationException) { assign = null; }
            lock (_stateLock) _newConversationPartner = assign;
            try { await HandleNewSessionAsync(requestId).ConfigureAwait(false); }
            finally { lock (_stateLock) _newConversationPartner = null; }
        }
        /// <summary>
        /// Switches to a tab's conversation like the conversation list does; without <paramref name="sessionId"/> (a closed tab opened again) to the
        /// partner's most recently changed conversation. A conversation the engine never wrote (no message was sent in it) cannot be switched to,
        /// and a partner without conversations has none: the tab starts a new conversation for its partner instead.
        /// </summary>
        private async Task SwitchToTabAsync(string requestId, string root, string? sessionId, ConversationPartnerPayload? partner)
        {
            RequireIdleForTabSwitch();
            var workspace = await _environment.GetWorkspaceRootAsync(_disposeCts.Token).ConfigureAwait(false);
            var store = new RecentSessionStore(_environment.DataDirectory, workspace);
            string? file, id, name; lock (_stateLock) { file = _sessionFile; id = _info.SessionId; name = _info.SessionName; }
            var listing = await Task.Run(() => store.List(file, id, name), _disposeCts.Token).ConfigureAwait(false);
            var entries = (listing["entries"] as JArray)?.OfType<JObject>().ToList() ?? new List<JObject>();
            if (sessionId == null && partner != null)
            {
                // The list is newest first.
                var partners = _tabs.All(root);
                sessionId = entries.Select(e => (string?)e["sessionId"]).FirstOrDefault(s => s != null && s != id && partners.TryGetValue(s, out var p) && p.Kind == partner.Kind && p.Id == partner.Id);
            }
            var handle = sessionId == null ? null : entries.FirstOrDefault(e => (string?)e["sessionId"] == sessionId)?.GetString("id");
            if (handle == null)
            {
                if (sessionId != null) _tabs.Remove(root, sessionId);
                await StartConversationAsync(requestId, partner).ConfigureAwait(false); return;
            }
            _composerAuto?.Stop(PompLanguage.Pick("대화 전환을 요청했어요.", "Conversation switch requested."));
            await HandleRecentSessionsAsync(new JObject { ["action"] = "switch", ["requestId"] = requestId, ["root"] = listing["root"], ["id"] = handle }).ConfigureAwait(false);
        }
        /// <summary>
        /// After every change of conversation: the engine extension applies the partner's model and thinking level (an agent conversation) or
        /// gives a POMP conversation back the model it had (extensions/partner.ts). A conversation that just got its partner also gets the role's tools.
        /// </summary>
        private async Task ApplyConversationPartnerAsync(string session, bool assigned)
        {
            OmpConnection? connection;
            lock (_stateLock)
            {
                if (_info.SessionId != session || _sessionSwitchUncertain || _recoveryStatus != "ready" || _turns.Count > 0 || _info.IsStreaming || _bridge.State != BridgeState.Running) return;
                connection = _epochConnection;
            }
            if (connection == null) return;
            try
            {
                await RequireEngineCommandsAsync(connection, _disposeCts.Token, "pomp-partner").ConfigureAwait(false);
                var response = await connection.SendCommandAsync(OmpCommands.Prompt(assigned ? "/pomp-partner new" : "/pomp-partner"), TimeSpan.FromSeconds(15), _disposeCts.Token).ConfigureAwait(false);
                if (!response.Success) _logger.Warn("The conversation partner could not be applied: " + response.Error);
                if (assigned) await SendToolsAsync("reset").ConfigureAwait(false);
            }
            catch (Exception ex) when (!(ex is OperationCanceledException)) { _logger.Warn("The conversation partner could not be applied: " + ex.Message); }
            RequestInfoRefresh();
        }
        // Job ids belong to one engine; retain only team ids so the next team send can explain a restart.
        private void SyncTeamInstancesLocked()
        {
            if (ReferenceEquals(_teamInstancesConnection, _epochConnection) && _teamInstancesConnection?.IsClosed != true) return;
            foreach (var team in _teamInstances.Where(t => t.Value.Count > 0)) _unavailableTeams.Add(team.Key);
            _teamInstances.Clear(); _teamInstancesConnection = _epochConnection;
        }
        private void ConversationObserved(string? sessionId)
        {
            string root; bool changed, rootChanged, hadSession; ConversationPartnerPayload? assign = null;
            lock (_stateLock)
            {
                SyncTeamInstancesLocked(); hadSession = _partnerSession != null; changed = _partnerSession != sessionId; root = _workingDirectory; rootChanged = root != _runHistoryRoot;
                if (changed && !string.IsNullOrEmpty(sessionId)) { assign = _newConversationPartner; _newConversationPartner = null; }
            }
            if (rootChanged && root.Length > 0)
            {
                var history = new AgentRunHistory(_environment.DataDirectory, root, _logger);
                lock (_stateLock) { _runHistory = history; _runHistoryRoot = root; }
                PublishRunHistory();
            }
            if (!changed) return;
            ConversationPartnerPayload value;
            try { value = string.IsNullOrEmpty(sessionId) ? new ConversationPartnerPayload() : _tabs.Observe(root, sessionId!, assign); }
            catch (Exception ex) when (ex is ArgumentException || ex is IOException || ex is UnauthorizedAccessException) { _logger.Warn("Chat tabs could not be saved: " + ex.Message); value = assign ?? new ConversationPartnerPayload(); }
            lock (_stateLock) { _partnerSession = sessionId; _partner = value; _teamInstances.Clear(); _unavailableTeams.Clear(); _partnerMessage = null; }
            PauseChatQueue(PompLanguage.Pick("대화가 바뀌었어요.", "Conversation changed."), onlyIfItems: !hadSession);
            PublishTabs();
            if (!string.IsNullOrEmpty(sessionId)) _commands.Enqueue(() => ApplyConversationPartnerAsync(sessionId!, assign != null));
        }
        private ConversationPartnerPayload? DefaultPartner(ChatSendPayload send)
        {
            // An explicit target, or an explicit "this conversation itself" (page actions), overrides the partner.
            if (send.Main == true || !string.IsNullOrWhiteSpace(send.Team) || !string.IsNullOrWhiteSpace(send.Agent) || !string.IsNullOrWhiteSpace(send.AgentInstance)) return null;
            lock (_stateLock) return CopyPartner(_partner);
        }
        private void ResolvePartnerTarget(ChatSendPayload send, ConversationPartnerPayload? partner)
        {
            if (partner == null || partner.Kind == "main") return;
            partner = ValidatePartner(partner);
            send.Team = null; send.Agent = null; send.AgentInstance = null;
            // A team conversation sends every message to its team. An agent conversation is that agent itself (the engine gives the
            // conversation the role's persona), so the message goes as typed: no relay and no subagent.
            if (partner.Kind == "team") send.Team = partner.Id;
        }
        private string TeamContinuation(AgentTeam team)
        {
            // Isolated members cannot be resumed later (docs/architecture.md §8.11): an isolation team always starts new ones, in Git only.
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
            if (partnerChanged) PublishTabs();
            return continuation;
        }
        private ChatQueueStatePayload QueueView()
        {
            lock (_stateLock) return new ChatQueueStatePayload { SessionId = _chatQueue.Count == 0 ? null : _queueSession, Paused = _queuePaused, Reason = _queueReason, Items = _chatQueue.Select(q => new ChatQueueItemPayload { Id = q.Id, Text = q.Send.Text, IncludeEditorContext = q.Send.IncludeEditorContext, Attachments = CopySend(q.Send).Attachments, Team = q.Send.Team ?? (q.Partner?.Kind == "team" ? q.Partner.Id : null), Agent = q.Send.Agent ?? (q.Partner?.Kind == "agent" ? q.Partner.Id : null), AgentInstance = q.Send.AgentInstance, CreatedAt = q.CreatedAt, CanSteer = CanSteer(q.Send) }).ToList() };
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
                // Sends now: the item goes to the running agent (or, with nothing running, starts a turn) instead of waiting its turn.
                if (command.Action == "steer") return SteerQueuedAsync(requestId, command.Id);
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
        private void BeginRun(TurnState turn, ChatSendPayload send)
        {
            var kind = send.Team != null ? "team" : send.Agent != null ? "agent" : send.AgentInstance != null ? "dm" : null; if (kind == null) return;
            var id = send.Team ?? send.Agent ?? send.AgentInstance!;
            var roster = GetAgentRoster(); var team = kind == "team" ? roster.ListTeams().FirstOrDefault(t => string.Equals(t.Id, id, StringComparison.OrdinalIgnoreCase) && !t.Shadowed && string.IsNullOrEmpty(t.Error)) : null;
            var title = kind == "team" ? team?.Title : kind == "agent" ? roster.ListRoles().FirstOrDefault(r => r.Name == id && !r.Shadowed)?.Title : (_backgroundAgents.View()["jobs"] as JArray)?.OfType<JObject>().FirstOrDefault(j => (string?)j["id"] == id)?["agent"]?.ToString();
            var history = RunHistory();
            string session;
            lock (_stateLock)
            {
                SyncTeamInstancesLocked(); session = _info.SessionId ?? "";
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
            }
            try { if (history?.Observe(session, turn?.RunId, job, fresh) == true) PublishRunHistory(); }
            catch (Exception ex) { _logger.Warn("Run history progress could not be saved: " + ex.Message); }
            if (partnerChanged) PublishTabs();
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
