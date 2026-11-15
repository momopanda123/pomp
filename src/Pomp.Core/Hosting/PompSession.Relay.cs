using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Pomp.Bridge;
using Pomp.Diagnostics;
using Pomp.Features;
using Pomp.Internal;
using Pomp.Protocol;
using Pomp.Ui;

namespace Pomp.Hosting
{
    /// <summary>The conversation host's part of a message to another agent or team (docs/architecture.md §8.3).</summary>
    internal interface IPartnerRelayHost
    {
        /// <summary>Why nothing can be delivered now (no workspace, the workspace is changing); null when it can.</summary>
        string? Refusal(PompSession source);

        /// <summary>Takes the message over and returns at once; progress and the outcome come back through <see cref="PompSession.ApplyRelayUpdate"/>.</summary>
        void Deliver(PompSession source, PartnerRelay relay);

        /// <summary>Where a delivery of this window that has not ended stands; null when the host does not know it (any more).</summary>
        RelayUpdate? StatusOf(string relayId);

        /// <summary>The outcomes that arrived while their conversation was not shown (each once).</summary>
        IReadOnlyList<RelayUpdate> TakeBacklog(string root, string sessionId);
    }

    /// <summary>One message on its way to another partner's conversation tab.</summary>
    internal sealed class PartnerRelay
    {
        public string Id { get; set; } = "";
        public ConversationPartnerPayload Target { get; set; } = new ConversationPartnerPayload();
        /// <summary>The message as that tab gets it: no target (the tab's own partner answers), the user's text, files and editor-context choice.</summary>
        public ChatSendPayload Send { get; set; } = new ChatSendPayload();
        /// <summary>An event rule sent it: its turn must not forward again.</summary>
        public bool FromHook { get; set; }
        public string SourceSession { get; set; } = "";
        public string Root { get; set; } = "";
        /// <summary>The run-history entry the sending conversation made for it.</summary>
        public string? RunId { get; set; }
    }

    /// <summary>A change of a relayed message; the last one ("done", "failed", "stopped") carries the outcome.</summary>
    internal sealed class RelayUpdate
    {
        public string RelayId { get; set; } = "";
        public ConversationPartnerPayload Target { get; set; } = new ConversationPartnerPayload();
        public string Status { get; set; } = "";
        public string? Detail { get; set; }
        public string? Text { get; set; }
        public bool Truncated { get; set; }
        public string? Error { get; set; }
        /// <summary>The recipient's conversation, once the message reached it.</summary>
        public string? SessionId { get; set; }
        public string? RunId { get; set; }
        public bool FromHook { get; set; }
        public bool Terminal => Status == "done" || Status == "failed" || Status == "stopped";
    }

    internal sealed class RelayChangedEventArgs : EventArgs
    {
        public RelayChangedEventArgs(RelayUpdate update) { Update = update; }
        public RelayUpdate Update { get; }
    }

    // A message to another agent or team is not run by the conversation it was sent from: the host delivers it to that partner's own
    // conversation tab, where it is an ordinary message. The sending conversation shows the message with the recipient and a status, and
    // one entry with the outcome and the final answer when the work there ends. Both are kept in the engine's conversation as
    // "pomp-relay" custom messages (extensions/relay.ts), written only while the engine is idle.
    public sealed partial class PompSession
    {
        internal const string RelayCustomType = "pomp-relay";
        private const string RelayCommand = "pomp-relay";
        private const string RelayResultPrefix = "pomp-relay-result:";
        /// <summary>The relayed part of a final answer, like the other relayed texts (a member's result, an advisor's note).</summary>
        internal const int RelayTextLimit = 8000;
        private const int MaxRelayRecords = 64;
        private const int MaxRelayRecordAttempts = 3;

        /// <summary>The page's own commands (slashCommands.ts BUILTIN_COMMANDS and its aliases): typed text that starts with one is a command.</summary>
        private static readonly HashSet<string> PageCommandNames = new HashSet<string>(StringComparer.Ordinal)
        {
            "new", "sessions", "rename", "model", "thinking", "effort", "permissions", "compact", "accounts", "login", "usage", "work",
            "agents", "computer", "agent", "team", "goal", "steer", "btw", "help", "pomp-compact", "stear",
        };

        /// <summary>Set by the conversation host for each of its conversations; a conversation without one cannot reach another tab.</summary>
        internal IPartnerRelayHost? RelayHost { get; set; }

        /// <summary>A message this conversation received from another one changed (it waits, works, ended).</summary>
        internal event EventHandler<RelayChangedEventArgs>? RelayChanged;

        private enum RelayState { Admitting, Queued, Running }

        /// <summary>A message another conversation sent here, from its admission to the end of its turn.</summary>
        private sealed class InboundRelay
        {
            public InboundRelay(PartnerRelay relay) { Id = relay.Id; SourceSession = relay.SourceSession; RunId = relay.RunId; }
            public string Id { get; }
            public string SourceSession { get; }
            public string? RunId { get; }
            public RelayState State { get; set; } = RelayState.Admitting;
            /// <summary>The host said "delivered" when it handed the message over.</summary>
            public string LastStatus { get; set; } = "delivered";
            public string? LastDetail { get; set; }
            /// <summary>Why this conversation did not take the message.</summary>
            public string? Refusal { get; set; }
            public bool Done { get; set; }
        }

        /// <summary>A sent message or an outcome the engine's conversation does not hold yet.</summary>
        private sealed class RelayRecord
        {
            public string Session { get; set; } = "";
            public JObject Details { get; set; } = new JObject();
            public int Attempts { get; set; }
        }

        private sealed class RelayTicket
        {
            public RelayTicket(OmpConnection origin) { Origin = origin; }
            public OmpConnection Origin { get; }
            public TaskCompletionSource<JObject> Completion { get; } = new TaskCompletionSource<JObject>(TaskCreationOptions.RunContinuationsAsynchronously);
        }

        private readonly Dictionary<string, InboundRelay> _inboundRelays = new Dictionary<string, InboundRelay>(StringComparer.Ordinal);
        /// <summary>The relayed turns by their prompt id, kept after the turn so a member that ends later still counts for the sender's run entry.</summary>
        private readonly Dictionary<string, InboundRelay> _relayRuns = new Dictionary<string, InboundRelay>(StringComparer.Ordinal);
        private readonly List<RelayRecord> _relayRecords = new List<RelayRecord>();
        private readonly ConcurrentDictionary<string, RelayTicket> _relayTickets = new ConcurrentDictionary<string, RelayTicket>(StringComparer.Ordinal);
        private bool _relayFlushScheduled;

        // ---- the sending conversation -------------------------------------------------------------------------------------

        /// <summary>
        /// The other agent or team <paramref name="send"/> is for, or null when this conversation takes it. This conversation's own
        /// partner as the target means this conversation: the target is taken off and the message goes the usual way.
        /// </summary>
        private ConversationPartnerPayload? RelayTarget(ChatSendPayload send)
        {
            if (send.Main == true || !string.IsNullOrWhiteSpace(send.AgentInstance)) return null;
            var team = Target(send.Team); var agent = Target(send.Agent);
            if ((team == null) == (agent == null)) return null;
            ConversationPartnerPayload partner;
            lock (_stateLock) partner = CopyPartner(_partner);
            if (team != null)
            {
                if (partner.Kind == "team" && string.Equals(partner.Id, team, StringComparison.OrdinalIgnoreCase)) { send.Team = null; return null; }
                return new ConversationPartnerPayload { Kind = "team", Id = team };
            }
            if (partner.Kind == "agent" && string.Equals(partner.Id, agent, StringComparison.Ordinal)) { send.Agent = null; return null; }
            return new ConversationPartnerPayload { Kind = "agent", Id = agent };
        }

        /// <summary>The agent or team as the roster has it; throws with the reason when a message cannot go to it.</summary>
        private ConversationPartnerPayload CheckRelayTarget(ConversationPartnerPayload target)
        {
            if (target.Kind == "agent")
            {
                var name = target.Id ?? "";
                var role = GetAgentRoster().ListRoles().FirstOrDefault(r => string.Equals(r.Name, name, StringComparison.Ordinal) && !r.Shadowed && string.IsNullOrEmpty(r.Error));
                if (role == null || !AgentRoster.IsValidName(name))
                    throw new InvalidOperationException(PompLanguage.Pick($"에이전트 '{name}'을(를) 찾을 수 없어요.", $"Agent '{name}' was not found."));
                RefuseInReviewOnlyWorkspace();
                return new ConversationPartnerPayload { Kind = "agent", Id = role.Name };
            }

            var team = ResolveTeam(target.Id ?? "");
            if (team.Isolation)
            {
                // Isolated members work in Git work trees (docs/architecture.md §8.11).
                string root; lock (_stateLock) root = _workingDirectory;
                if (FindGitRoot(root) == null)
                    throw new InvalidOperationException(PompLanguage.Pick("작업 영역이 Git 저장소가 아니에요.", "The workspace is not a Git repository."));
            }
            RefuseInReviewOnlyWorkspace();
            return new ConversationPartnerPayload { Kind = "team", Id = team.Id };
        }

        /// <summary>
        /// A command is run by the conversation it is typed in: text whose first word is a command this conversation knows (the engine's,
        /// an extension's, a skill, POMP's own or the page's) is not sent to another partner. Any other text goes as it is.
        /// </summary>
        private void RefuseRelayCommand(string text)
        {
            var match = SlashToken.Match(text);
            if (!match.Success) return;
            var name = match.Groups[2].Value;
            var known = PompCommandNames.ContainsKey(name) || PageCommandNames.Contains(name);
            if (!known)
            {
                JArray rows;
                lock (_stateLock) rows = _availableCommands;
                known = rows.OfType<JObject>().Any(row => { var listed = row.GetString("name"); return listed == name || listed == "skill:" + name; });
            }
            if (known)
                throw new InvalidOperationException(PompLanguage.Pick("명령은 다른 대화 상대에게 보낼 수 없어요. 그 상대의 탭에서 실행해 주세요.", "A command cannot be sent to another conversation partner. Run it in that partner's tab."));
        }

        private string RelayTargetTitle(ConversationPartnerPayload target)
        {
            var roster = GetAgentRoster();
            var title = target.Kind == "team"
                ? roster.ListTeams().FirstOrDefault(t => t.Id == target.Id && !t.Shadowed)?.Title
                : roster.ListRoles().FirstOrDefault(r => r.Name == target.Id && !r.Shadowed)?.Title;
            return string.IsNullOrWhiteSpace(title) ? target.Id ?? "" : title!;
        }

        /// <summary>
        /// Hands a message for another agent or team to the host. This conversation starts no turn, queues nothing and its engine gets
        /// nothing: the transcript shows the message with its recipient, and later the outcome. False (with the reason told to the page,
        /// which keeps the draft) when it cannot be sent.
        /// </summary>
        private bool RelayOut(ChatSendPayload send, ConversationPartnerPayload target, string? queuedItemId)
        {
            if (queuedItemId != null)
            {
                // An item of this conversation's queue: it leaves the queue here, whether or not it can be delivered.
                lock (_stateLock) _chatQueue.RemoveAll(item => item.Id == queuedItemId);
                PublishChatQueue();
            }

            string session, root; IPartnerRelayHost host;
            try
            {
                ValidateQueuedSend(send);
                RefuseRelayCommand(send.Text ?? "");
                target = CheckRelayTarget(target);
                if (WorkspaceSwitchBlock() is string block) throw new InvalidOperationException(block);
                host = RelayHost ?? throw new InvalidOperationException(PompLanguage.Pick("이 연결에서는 다른 대화 상대의 탭으로 보낼 수 없어요.", "This connection cannot send to another partner's tab."));
                if (host.Refusal(this) is string refusal) throw new InvalidOperationException(refusal);
                lock (_stateLock)
                {
                    session = _info.SessionId ?? ""; root = _workingDirectory;
                    if (session.Length == 0 || _sessionSwitchUncertain)
                        throw new InvalidOperationException(PompLanguage.Pick("활성 대화가 확인되지 않았어요.", "The active conversation is not confirmed."));
                }
            }
            catch (Exception ex)
            {
                _ui.Post(UiMessageTypes.Error, new UiErrorInfo { Code = "queue_rejected", ClientMessageId = send.ClientMessageId, Message = ex.Message, Retryable = false }, UiDelivery.State);
                return false;
            }

            var relayId = "relay-" + Guid.NewGuid().ToString("N");
            var fromHook = send.ClientMessageId?.StartsWith(HookMessagePrefix, StringComparison.Ordinal) == true;
            var text = send.Text ?? "";
            var attachments = (send.Attachments ?? new List<ChatAttachment>()).Where(a => !string.IsNullOrWhiteSpace(a?.Path)).Select(a => a.Path).ToList();
            // One run-history entry per relayed message, made here: its status follows the delivery, and a rerun sends it again.
            string? runId = null;
            try
            {
                runId = RunHistory()?.Begin(session, new AgentRunTargetPayload { Kind = target.Kind, Id = target.Id ?? "", Title = RelayTargetTitle(target) }, text, relayId);
                if (runId != null) PublishRunHistory();
            }
            catch (Exception ex) { _logger.Warn("Run history could not be started: " + ex.Message); }

            _ui.Post(UiMessageTypes.ChatRelaySent, new ChatRelaySentPayload
            {
                RelayId = relayId, ClientMessageId = string.IsNullOrEmpty(send.ClientMessageId) ? null : send.ClientMessageId, Text = text,
                Attachments = attachments.Count == 0 ? null : attachments.Select(path => new ChatAttachment { Path = path }).ToList(),
                Target = CopyPartner(target), Status = "sending",
            });
            AddRelayRecord(session, new JObject
            {
                ["v"] = 1, ["kind"] = "sent", ["relayId"] = relayId, ["target"] = new JObject { ["kind"] = target.Kind, ["id"] = target.Id },
                ["text"] = text.Length > RestoredTextLimit ? text.Substring(0, RestoredTextLimit) : text,
                ["attachments"] = attachments.Count == 0 ? null : new JArray(attachments.Take(50).ToArray<object>()),
                ["at"] = DateTimeOffset.UtcNow.ToString("O"),
            });
            host.Deliver(this, new PartnerRelay
            {
                Id = relayId, Target = CopyPartner(target), FromHook = fromHook, SourceSession = session, Root = root, RunId = runId,
                Send = new ChatSendPayload
                {
                    ClientMessageId = (fromHook ? HookMessagePrefix : "") + relayId, Text = text, IncludeEditorContext = send.IncludeEditorContext,
                    Attachments = attachments.Count == 0 ? null : attachments.Select(path => new ChatAttachment { Path = path }).ToList(),
                },
            });
            return true;
        }

        /// <summary>The conversation is restored and shown as it is: a relay entry posted now stays in its transcript.</summary>
        private bool RelayReadyLocked(string session) =>
            _info.SessionId == session && !_sessionSwitchUncertain && _recoveryStatus == "ready" && _managedInitialTranscriptReady;

        /// <summary>
        /// The host's news about a message this conversation sent. False when this is not that conversation now (or it is still being
        /// restored): the host keeps an outcome until the conversation is shown again.
        /// </summary>
        internal bool ApplyRelayUpdate(string sourceSession, RelayUpdate update)
        {
            lock (_stateLock)
                if (!RelayReadyLocked(sourceSession)) return false;
            PostRelayUpdate(sourceSession, update);
            return true;
        }

        /// <summary>Shows a relayed message's state or outcome in this conversation, whose current conversation is <paramref name="sourceSession"/>.</summary>
        private void PostRelayUpdate(string sourceSession, RelayUpdate update)
        {
            if (!update.Terminal)
            {
                _ui.Post(UiMessageTypes.ChatRelayStatus, new ChatRelayStatusPayload { RelayId = update.RelayId, Status = update.Status, Detail = update.Detail, SessionId = update.SessionId });
                return;
            }

            var text = string.IsNullOrWhiteSpace(update.Text) ? null : update.Text;
            _ui.Post(UiMessageTypes.ChatRelayResult, new ChatRelayResultPayload
            {
                RelayId = update.RelayId, Target = CopyPartner(update.Target), Status = update.Status, Text = text,
                Truncated = update.Truncated ? true : (bool?)null, Error = update.Error ?? update.Detail, SessionId = update.SessionId,
            });
            AddRelayRecord(sourceSession, new JObject
            {
                ["v"] = 1, ["kind"] = "result", ["relayId"] = update.RelayId, ["target"] = new JObject { ["kind"] = update.Target.Kind, ["id"] = update.Target.Id },
                ["status"] = update.Status, ["text"] = text, ["truncated"] = update.Truncated ? true : (bool?)null,
                ["error"] = update.Error ?? update.Detail, ["sessionId"] = update.SessionId, ["at"] = DateTimeOffset.UtcNow.ToString("O"),
            });
            if (update.RunId != null)
            {
                try { RunHistory()?.End(update.RunId, update.Status == "done" ? "completed" : update.Status == "stopped" ? "aborted" : "error"); PublishRunHistory(); }
                catch (Exception ex) { _logger.Warn("Run history could not be saved: " + ex.Message); }
            }
            // The run ended in another tab, but the event rules of the conversation that asked for it see its end, as they did when the
            // run was this conversation's own. A message that never reached the other tab ran nothing.
            if (update.Status == "done" || update.SessionId != null)
            {
                var done = update.Status == "done"; var team = update.Target.Kind == "team";
                RaiseHookEvent(team ? (done ? "team.completed" : "team.failed") : (done ? "agent.completed" : "agent.failed"), team ? "team" : "agent", update.Target.Id ?? "",
                    text ?? (team ? "Team run " : "Agent run ") + (done ? "completed" : update.Status == "stopped" ? "aborted" : "error") + ".", update.RelayId + ":" + update.Status, update.FromHook);
            }
        }

        /// <summary>Shows the outcomes that arrived while this conversation was not shown.</summary>
        internal void PullRelayBacklog()
        {
            string session, root; var host = RelayHost;
            if (host == null) return;
            lock (_stateLock)
            {
                session = _info.SessionId ?? ""; root = _workingDirectory;
                if (session.Length == 0 || !RelayReadyLocked(session)) return;
            }
            foreach (var update in host.TakeBacklog(root, session)) PostRelayUpdate(session, update);
        }

        // ---- kept in the engine's conversation ----------------------------------------------------------------------------

        private void AddRelayRecord(string session, JObject details)
        {
            lock (_stateLock)
            {
                _relayRecords.Add(new RelayRecord { Session = session, Details = details });
                if (_relayRecords.Count > MaxRelayRecords)
                {
                    _relayRecords.RemoveAt(0);
                    _logger.Warn("A relayed message's record was dropped before the engine could keep it.");
                }
            }
            ScheduleRelayFlush();
        }

        private bool RelayCommandAvailableLocked() => _availableCommands.OfType<JObject>().Any(row => row.GetString("name") == RelayCommand);

        /// <summary>The engine can take a record now without it reaching a running agent: connected, restored and idle.</summary>
        private bool RelayFlushReadyLocked() =>
            !ChatRunBusyLocked() && _bridge.State == BridgeState.Running && !_sessionSwitchUncertain && _recoveryStatus == "ready"
            && _epochConnection != null && RelayCommandAvailableLocked();

        /// <summary>Writes the waiting records once the engine is idle; a no-op while it works (the end of the run comes back here).</summary>
        private void ScheduleRelayFlush()
        {
            lock (_stateLock)
            {
                if (_relayFlushScheduled || _relayRecords.Count == 0 || !RelayFlushReadyLocked() || !_relayRecords.Any(record => record.Session == _info.SessionId)) return;
                _relayFlushScheduled = true;
            }
            _commands.Enqueue(async () =>
            {
                lock (_stateLock) _relayFlushScheduled = false;
                await FlushRelayRecordsAsync(null, _disposeCts.Token).ConfigureAwait(false);
            });
        }

        /// <summary>
        /// Gives the current conversation's waiting records to the engine, oldest first. On the command queue (no prompt can go out in
        /// between); <paramref name="beforePrompt"/> is the connection of a prompt about to be sent, so the records come before it.
        /// Never throws: a record that cannot be written now stays for the next idle moment.
        /// </summary>
        private async Task FlushRelayRecordsAsync(OmpConnection? beforePrompt, CancellationToken token)
        {
            try
            {
                while (!token.IsCancellationRequested)
                {
                    RelayRecord? record; OmpConnection? origin; string? session;
                    lock (_stateLock)
                    {
                        session = _info.SessionId; origin = _epochConnection;
                        if (beforePrompt != null ? !ReferenceEquals(origin, beforePrompt) || !RelayCommandAvailableLocked() : !RelayFlushReadyLocked()) return;
                        record = _relayRecords.FirstOrDefault(item => item.Session == session);
                    }
                    if (record == null || origin == null || session == null) return;
                    var outcome = await SendRelayRecordAsync(origin, session, record, token).ConfigureAwait(false);
                    lock (_stateLock)
                    {
                        if (outcome == "ok" || (outcome != "busy" && ++record.Attempts >= MaxRelayRecordAttempts)) _relayRecords.Remove(record);
                    }
                    if (outcome != "ok") return;
                }
            }
            catch (OperationCanceledException) { }
            catch (Exception ex) { _logger.Warn("A relayed message's record was not kept: " + ex.Message); }
        }

        /// <summary>"ok", "busy" (the engine works: later) or the reason it was not written.</summary>
        private async Task<string> SendRelayRecordAsync(OmpConnection origin, string session, RelayRecord record, CancellationToken token)
        {
            var request = Guid.NewGuid().ToString("N");
            var encoded = Convert.ToBase64String(Encoding.UTF8.GetBytes(new JObject { ["sessionId"] = session, ["record"] = record.Details }.ToString(Formatting.None)));
            var prompt = OmpCommands.Prompt("/" + RelayCommand + " " + encoded + " --request " + request);
            if (Pomp.Transport.NdjsonWriter.Encode(prompt).Length > MaxPromptFrameBytes) return "too-large";
            var ticket = new RelayTicket(origin);
            _relayTickets[request] = ticket;
            try
            {
                // An extension command runs at once and starts no turn; it refuses while the agent works.
                var response = await origin.SendCommandAsync(prompt, TimeSpan.FromSeconds(10), token).ConfigureAwait(false);
                if (!response.Success) return "refused";
                if (!await AsyncUtil.WaitForAsync(ticket.Completion.Task, TimeSpan.FromSeconds(5)).WithCancellation(token).ConfigureAwait(false)) return "no-answer";
                var result = await ticket.Completion.Task.ConfigureAwait(false);
                return (bool?)result["ok"] == true ? "ok" : (string?)result["error"] == "busy" ? "busy" : "refused";
            }
            catch (TimeoutException) { return "no-answer"; }
            finally { _relayTickets.TryRemove(request, out _); }
        }

        private bool HandleRelayNotification(string? message, OmpConnection? origin)
        {
            if (message == null || !message.StartsWith(RelayResultPrefix, StringComparison.Ordinal)) return false;
            // Malformed and late answers are consumed too: none belongs in the transcript.
            if (message.Length > 4000) return true;
            try
            {
                var result = JObject.Parse(Encoding.UTF8.GetString(Convert.FromBase64String(message.Substring(RelayResultPrefix.Length))));
                var request = result["request"]?.Type == JTokenType.String ? (string?)result["request"] : null;
                if (request != null && _relayTickets.TryGetValue(request, out var ticket) && ReferenceEquals(ticket.Origin, origin)) ticket.Completion.TrySetResult(result);
            }
            catch (Exception ex) when (ex is FormatException || ex is JsonException || ex is InvalidCastException)
            { _logger.Debug("Ignored a malformed relay record answer."); }
            return true;
        }

        private static ConversationPartnerPayload? RelayRecordTarget(JObject details)
        {
            var target = details["target"] as JObject;
            var kind = target.GetString("kind"); var id = target.GetString("id");
            return (kind == "agent" || kind == "team") && !string.IsNullOrEmpty(id) && id!.Length <= 128 && !id.Any(char.IsControl)
                ? new ConversationPartnerPayload { Kind = kind!, Id = id } : null;
        }

        private static string? RelayRecordId(JObject? details)
        {
            var id = details.GetString("relayId");
            return string.IsNullOrEmpty(id) || id!.Length > 128 || id.Any(char.IsControl) ? null : id;
        }

        /// <summary>The bubble of a kept "sent" record; null when the record is not one.</summary>
        private static ChatRelaySentPayload? RelaySentFromRecord(JObject? details)
        {
            var id = RelayRecordId(details);
            if (id == null || details.GetString("kind") != "sent" || RelayRecordTarget(details!) is not ConversationPartnerPayload target) return null;
            var text = details.GetString("text") ?? "";
            var files = (details!["attachments"] as JArray)?.Where(item => item.Type == JTokenType.String).Select(item => (string)item!)
                .Where(path => path.Length > 0 && path.Length <= 4096 && !path.Any(char.IsControl)).Take(50).Select(path => new ChatAttachment { Path = path }).ToList();
            return new ChatRelaySentPayload
            {
                RelayId = id, Text = text.Length > RestoredTextLimit ? text.Substring(0, RestoredTextLimit) : text, Target = target,
                Attachments = files == null || files.Count == 0 ? null : files, Status = "unknown",
            };
        }

        /// <summary>The outcome entry of a kept "result" record; null when the record is not one.</summary>
        private static ChatRelayResultPayload? RelayResultFromRecord(JObject? details)
        {
            var id = RelayRecordId(details);
            var status = details.GetString("status");
            if (id == null || details.GetString("kind") != "result" || RelayRecordTarget(details!) is not ConversationPartnerPayload target
                || (status != "done" && status != "failed" && status != "stopped")) return null;
            var text = details.GetString("text"); var error = details.GetString("error"); var session = details.GetString("sessionId");
            return new ChatRelayResultPayload
            {
                RelayId = id, Target = target, Status = status!,
                Text = string.IsNullOrWhiteSpace(text) ? null : text!.Length > RelayTextLimit ? text.Substring(0, RelayTextLimit) : text,
                Truncated = details!["truncated"]?.Type == JTokenType.Boolean && (bool)details["truncated"]! ? true : (bool?)null,
                Error = string.IsNullOrWhiteSpace(error) ? null : TextUtil.Truncate(error!, 2000),
                SessionId = string.IsNullOrEmpty(session) || session!.Length > 256 || session.Any(char.IsControl) ? null : session,
            };
        }

        /// <summary>
        /// After a restored transcript: the records the engine does not hold yet (so they were not restored) are shown at the end, a sent
        /// message that is still on its way gets its present state, and the outcomes that arrived meanwhile follow.
        /// </summary>
        private void PostUnsavedRelayRecords(string session, ISet<string> restoredSent, ISet<string> restoredResults)
        {
            List<JObject> waiting;
            lock (_stateLock) waiting = _relayRecords.Where(record => record.Session == session).Select(record => record.Details).ToList();
            var results = new HashSet<string>(waiting.Select(RelayResultFromRecord).OfType<ChatRelayResultPayload>().Select(result => result.RelayId), StringComparer.Ordinal);
            foreach (var details in waiting)
            {
                if (RelaySentFromRecord(details) is ChatRelaySentPayload sent && !restoredSent.Contains(sent.RelayId))
                {
                    if (!results.Contains(sent.RelayId)) ApplyLiveRelayState(sent);
                    _ui.Post(UiMessageTypes.ChatRelaySent, sent);
                }
                else if (RelayResultFromRecord(details) is ChatRelayResultPayload result && !restoredResults.Contains(result.RelayId))
                    _ui.Post(UiMessageTypes.ChatRelayResult, result);
            }
        }

        /// <summary>A restored sent message without a kept outcome: what the host knows of it now, else "unknown".</summary>
        private void ApplyLiveRelayState(ChatRelaySentPayload sent)
        {
            var live = RelayHost?.StatusOf(sent.RelayId);
            if (live == null) return;
            sent.Status = live.Status; sent.Detail = live.Detail; sent.SessionId = live.SessionId;
        }

        // ---- the receiving conversation -----------------------------------------------------------------------------------

        /// <summary>
        /// A message another conversation sent to this one's partner: taken like a message typed here (this tab's partner, editor
        /// context, files, its queue while it is busy). Its states and its end are reported through <see cref="RelayChanged"/>.
        /// </summary>
        internal void AcceptRelay(PartnerRelay relay)
        {
            var inbound = new InboundRelay(relay);
            lock (_stateLock) _inboundRelays[relay.Id] = inbound;
            _goalRunner?.Stop(PompLanguage.Pick("채팅을 직접 입력해서 목표 실행을 중지했어요.", "Goal execution stopped by manual chat input."));
            QueueChatInput(relay.Send, steer: false, inbound);
        }

        /// <summary>After the message went through admission: one that was neither queued nor started was not taken.</summary>
        private void SettleRelayAdmission(InboundRelay relay)
        {
            bool refused;
            lock (_stateLock) refused = !relay.Done && relay.State == RelayState.Admitting;
            if (refused)
                FinishInboundRelay(relay, "failed", relay.Refusal ?? PompLanguage.Pick("받는 대화가 메시지를 받지 못했어요.", "That conversation did not take the message."), null);
            else PublishInboundRelays();
        }

        /// <summary>The queue's "send now" gave the message to the running agent: it ends with that turn (or at once, when none is left).</summary>
        private void AttachRelayToRunningTurn(InboundRelay relay)
        {
            bool attached;
            lock (_stateLock)
            {
                var turn = _turns.Values.FirstOrDefault();
                attached = turn != null;
                if (turn != null) { turn.Relays.Add(relay); relay.State = RelayState.Running; }
            }
            if (attached) PublishInboundRelays(); else FinishInboundRelay(relay, "done", null, null);
        }

        private void FinishInboundRelay(InboundRelay relay, string status, string? error, string? text)
        {
            string? session;
            lock (_stateLock)
            {
                if (relay.Done) return;
                relay.Done = true; _inboundRelays.Remove(relay.Id); session = _info.SessionId;
            }
            var truncated = text != null && text.Length > RelayTextLimit;
            RelayChanged?.Invoke(this, new RelayChangedEventArgs(new RelayUpdate
            {
                RelayId = relay.Id, Status = status, Error = status == "done" ? null : error, SessionId = session,
                Text = string.IsNullOrWhiteSpace(text) ? null : truncated ? text!.Substring(0, RelayTextLimit) : text, Truncated = truncated,
            }));
        }

        /// <summary>
        /// Tells the senders where their messages stand here, when that changed: waiting in this tab's queue ("delivered"), held there
        /// ("waiting": the queue is paused), running ("working") or running with an approval or question open ("waiting"). A message
        /// that left the queue without starting (removed, cleared) ends as "stopped".
        /// </summary>
        private void PublishInboundRelays()
        {
            // Read before the state lock: the hub's lock is never taken inside it.
            var asking = _ui.PendingRequestCount > 0;
            List<RelayUpdate>? changes = null; List<InboundRelay>? gone = null;
            lock (_stateLock)
            {
                if (_inboundRelays.Count == 0) return;
                foreach (var relay in _inboundRelays.Values)
                {
                    if (relay.Done || relay.State == RelayState.Admitting) continue;
                    string status; string? detail = null;
                    if (relay.State == RelayState.Running) status = asking ? "waiting" : "working";
                    else if (!_chatQueue.Any(item => ReferenceEquals(item.Relay, relay))) { (gone ??= new List<InboundRelay>()).Add(relay); continue; }
                    else
                    {
                        var held = _queuePaused || _queueSession != _info.SessionId;
                        status = held ? "waiting" : "delivered";
                        if (held) detail = _queueReason ?? PompLanguage.Pick("받는 탭의 대기열이 멈춰 있어요.", "That tab's queue is paused.");
                    }
                    if (relay.LastStatus == status && relay.LastDetail == detail) continue;
                    relay.LastStatus = status; relay.LastDetail = detail;
                    (changes ??= new List<RelayUpdate>()).Add(new RelayUpdate { RelayId = relay.Id, Status = status, Detail = detail, SessionId = _info.SessionId });
                }
            }
            if (gone != null)
                foreach (var relay in gone)
                    FinishInboundRelay(relay, "stopped", PompLanguage.Pick("받는 탭의 대기열에서 지워졌어요.", "It was removed from that tab's queue."), null);
            if (changes != null)
                foreach (var change in changes) RelayChanged?.Invoke(this, new RelayChangedEventArgs(change));
        }

        /// <summary>An approval or question was raised or answered: the tab strip and the senders of relayed messages hear that this conversation waits.</summary>
        private void OnPendingRequestsChanged(object? sender, EventArgs e)
        {
            if (Volatile.Read(ref _disposed) == 1) return;
            NotifyConversationChanged();
            PublishInboundRelays();
        }
    }
}
