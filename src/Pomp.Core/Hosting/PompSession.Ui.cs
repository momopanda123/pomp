using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Pomp.Bridge;
using Pomp.Diagnostics;
using Pomp.Internal;
using Pomp.Protocol;
using Pomp.Ui;
using Pomp.Features;
using Newtonsoft.Json.Linq;

namespace Pomp.Hosting
{
    // UI → host: chat commands (docs/ui-protocol.md "UI → Host").
    public sealed partial class PompSession
    {
        /// <summary>Largest serialized prompt command sent to omp: its RPC transport asks clients to keep one inbound command within 1 MiB.</summary>
        internal const int MaxPromptFrameBytes = 1024 * 1024 - 2048;

        private long _promptCounter;
        private long _chatStopGeneration;
        private CancellationTokenSource? _sendingPrompt;

        /// <summary>Called on the channel's thread: returns immediately; order-sensitive commands run one after the other on a queue.</summary>
        private void OnUiCommand(object? sender, UiMessage message)
        {
            switch (message.Type)
            {
                case UiMessageTypes.ComposerPredict:
                    _ = PredictComposerAsync(message.Payload ?? new JObject());
                    break;
                case UiMessageTypes.SetupCommand:
                    // Setup remains responsive while chat commands wait for omp.
                    _providerSetup.HandleAsync(message.Payload ?? new JObject()).Forget(_logger, "engine setup");
                    break;
                case ComposerAutoContinue.CommandMessage:
                    _composerAuto?.Handle(message.Payload ?? new JObject());
                    break;
                case GoalCommandMessage:
                    // On the chat command queue: the idle check and the send cannot interleave with another message.
                    var goal = message.Payload ?? new JObject();
                    lock (_stateLock)
                    {
                        var goalGeneration = _chatStopGeneration;
                        _commands.Enqueue(() => HandleGoalAsync(goal, goalGeneration));
                    }
                    break;
                case ComposerWorkflow.CommandMessage:
                    if ((string?)message.Payload?["action"] != "refresh") _composerAuto?.Stop(PompLanguage.Pick("워크플로 제어가 바뀌었어요. 자동 이어가기가 꺼졌어요.", "Workflow control changed. Automatic continuation is off."));
                    _composerWorkflow?.HandleAsync(message.Payload ?? new JObject(), _disposeCts.Token).Forget(_logger, "composer control");
                    break;
                case CodeSearchTool.CommandMessage:
                    _codeSearch?.HandleAsync(message.Payload ?? new JObject(), _disposeCts.Token).Forget(_logger, "code index");
                    break;
                case UiMessageTypes.AdvisorsCommand:
                    _advisors.HandleAsync(message.Payload ?? new JObject(), _disposeCts.Token).Forget(_logger, "advisor control");
                    break;
                case UiMessageTypes.ProblemsCommand:
                    _problems.HandleAsync(message.Payload ?? new JObject(), _disposeCts.Token).Forget(_logger, "problems");
                    break;
                case InteractiveTerminal.CommandMessage:
                    _interactiveTerminal.HandleAsync(message.Payload ?? new JObject(), _disposeCts.Token).Forget(_logger, "interactive terminal");
                    break;
                case TerminalJobs.CommandMessage:
                    _terminalJobs.Handle(message.Payload ?? new JObject());
                    break;
                case "workspace/countDraftTokens":
                    CountDraftTokensAsync(message.Payload ?? new JObject()).Forget(_logger, "draft token count");
                    break;
                case WorkspaceReviewController.HistoryCommandMessage:
                    _workspaceReview?.HandleHistoryAsync(message.Payload ?? new JObject(), _disposeCts.Token).Forget(_logger, "workspace history");
                    break;
                case WorkspaceReviewController.CommandMessage:
                    _workspaceReview?.HandleAsync(message.Payload ?? new JObject(), _disposeCts.Token).Forget(_logger, "workspace review");
                    break;
                case IsolationResults.CommandMessage:
                    _isolationResults?.HandleAsync(message.Payload ?? new JObject()).Forget(_logger, "isolation results");
                    break;
                case UiMessageTypes.ChatSend:
                    _composerAuto?.Stop(PompLanguage.Pick("채팅을 직접 입력해서 이전 워크플로의 자동 이어가기가 꺼졌어요.", "Automatic continuation of the previous workflow was turned off by manual chat input."));
                    var send = message.PayloadAs<ChatSendPayload>();
                    if (send != null)
                    {
                        lock (_stateLock)
                        {
                            var generation = _chatStopGeneration;
                            var wasBusy = _turns.Count > 0 || _info.IsStreaming || _chatQueue.Count > 0;
                            _commands.Enqueue(() => HandleChatSendAsync(send, generation, forceQueue: wasBusy));
                        }
                    }

                    break;
                case UiMessageTypes.ChatSteer:
                    var steer = message.PayloadAs<ChatSendPayload>();
                    if (steer != null)
                    {
                        long steerGeneration;
                        lock (_stateLock) steerGeneration = _chatStopGeneration;
                        // On the chat queue like a send: a send just before it is admitted first, so its run is the one steered.
                        _commands.Enqueue(() => HandleChatSteerAsync(steer, steerGeneration));
                    }

                    break;
                case UiMessageTypes.BtwAsk:
                    // Beside the chat queue: a side question never waits for (or holds up) the conversation's own commands.
                    HandleBtwAskAsync(message.Payload ?? new JObject()).Forget(_logger, "side question");
                    break;
                case UiMessageTypes.BtwCancel:
                    HandleBtwCancelAsync(message.Payload ?? new JObject()).Forget(_logger, "side question cancel");
                    break;
                case UiMessageTypes.ChatStop:
                    _composerAuto?.Stop(PompLanguage.Pick("사용자가 대화를 중지했어요.", "User stopped the conversation."));
                    CancellationTokenSource? sending;
                    lock (_stateLock)
                    {
                        _chatStopGeneration++;
                        _queuePaused = true;
                        sending = _sendingPrompt;
                        // Keep abort after any frame already being written, but release the preceding response wait now.
                        _commands.Enqueue(HandleChatStopAsync);
                    }

                    PauseChatQueue(PompLanguage.Pick("사용자가 대화를 중지했어요.", "Conversation stopped by the user."));
                    Cancel(sending);
                    break;
                case UiMessageTypes.ConversationTab:
                    _commands.Enqueue(() => HandleTabAsync(message.Payload ?? new JObject()));
                    break;
                case UiMessageTypes.ChatQueue:
                    _commands.Enqueue(() => HandleQueueAsync(message.Payload ?? new JObject()));
                    break;
                case UiMessageTypes.AgentPlan:
                    // On the chat queue: no prompt can be admitted between the idle check and the engine edit.
                    _commands.Enqueue(() => HandleAgentPlanAsync(message.Payload ?? new JObject()));
                    break;
                case UiMessageTypes.AgentPlanPage:
                    // From the cached plan (no engine round trip): not held behind a running reply, and off the page's thread.
                    Task.Run(() => HandleAgentPlanPage(message.Payload ?? new JObject())).Forget(_logger, "plan page");
                    break;
                case UiMessageTypes.AgentPlanEdit:
                    // On the chat queue like the whole-plan edit: no prompt can be admitted between the idle check and the engine edit.
                    _commands.Enqueue(() => HandleAgentPlanEditAsync(message.Payload ?? new JObject()));
                    break;
                case UiMessageTypes.ToolsCommand:
                    QueueResourceCommand(message, UiMessageTypes.ToolsReply, HandleToolsAsync);
                    break;
                case UiMessageTypes.SkillsCommand:
                    QueueResourceCommand(message, UiMessageTypes.SkillsReply, HandleSkillsAsync);
                    break;
                case UiMessageTypes.McpCommand:
                    QueueResourceCommand(message, UiMessageTypes.McpReply, HandleMcpAsync);
                    break;
                case UiMessageTypes.CatalogCommand:
                    QueueResourceCommand(message, UiMessageTypes.CatalogReply, HandleCatalogAsync);
                    break;
                case UiMessageTypes.RulesCommand:
                    QueueResourceCommand(message, UiMessageTypes.RulesReply, HandleRulesAsync);
                    break;
                case UiMessageTypes.WorkflowsCommand:
                    QueueResourceCommand(message, UiMessageTypes.WorkflowsReply, HandleWorkflowsAsync);
                    break;
                case UiMessageTypes.HooksCommand:
                    QueueResourceCommand(message, UiMessageTypes.HooksReply, HandleHooksAsync);
                    break;
                case UiMessageTypes.PreviewOpen:
                    // Reads one local file and opens a VS tab: no model request, so not held behind a running reply.
                    var preview = message.Payload ?? new JObject();
                    Task.Run(() => HandlePreviewOpenAsync(preview)).Forget(_logger, "preview");
                    break;
                case UiMessageTypes.KnowledgeCommand:
                    // Local file edits only (no model request): not held behind a running reply, and off the page's thread.
                    var knowledge = message.Payload ?? new JObject();
                    Task.Run(() => HandleKnowledgeAsync(knowledge)).Forget(_logger, "knowledge");
                    break;
                case UiMessageTypes.AgentHooksCommand:
                    var hooks = message.Payload ?? new JObject();
                    Task.Run(() => HandleAgentHooksAsync(hooks)).Forget(_logger, "event rules");
                    break;
                case UiMessageTypes.AgentsHistory:
                    _commands.Enqueue(() => HandleRunHistoryAsync(message.Payload ?? new JObject()));
                    break;
                case UiMessageTypes.AgentsCommand:
                    HandleAgentsCommandAsync(message.Payload ?? new JObject()).Forget(_logger, "agents");
                    break;
                case UiMessageTypes.ComputerSet:
                    // Through the command queue: the toggle is an omp prompt (/computer on|off) and must not interleave with a chat send.
                    var computer = message.Payload ?? new JObject();
                    lock (_stateLock) _commands.Enqueue(() => HandleComputerSetAsync(computer));
                    break;
                case "workspace/snippet":
                    _commands.Enqueue(() => HandleSnippetAsync(message.Payload ?? new JObject()));
                    break;
                case ProjectHookController.CommandMessage:
                    _projectHooks?.Handle(message.Payload ?? new JObject());
                    break;
                case BackgroundAgents.CommandMessage:
                    _backgroundAgents.HandleAsync(message.Payload ?? new JObject()).Forget(_logger, "background control");
                    break;
                case "feedback/command":
                    _commands.Enqueue(() => HandleFeedbackAsync(message.Payload ?? new JObject()));
                    break;
                case "feedback/image":
                    HandleFeedbackImageAsync(message.Payload ?? new JObject()).Forget(_logger, "feedback image");
                    break;
                case "permission/rules":
                    _commands.Enqueue(() => HandlePermissionRulesAsync(message.Payload ?? new JObject()));
                    break;
                case UiMessageTypes.PermissionMode:
                    // Not queued: the handler refuses while a reply runs, and a change restarts omp (which pauses the queue anyway).
                    HandlePermissionModeAsync(message.Payload ?? new JObject()).Forget(_logger, "permission mode");
                    break;
                case UiMessageTypes.WorkspaceTrust:
                    // Not queued behind chat commands: it is what lets omp start at all.
                    HandleWorkspaceTrustAsync(message.Payload ?? new JObject()).Forget(_logger, "workspace trust");
                    break;
                case "session/recover":
                    _commands.Enqueue(RecoverSessionAsync);
                    break;
                case UiMessageTypes.SessionHistory:
                    if ((string?)message.Payload?["action"] == "delete") _composerAuto?.Stop(PompLanguage.Pick("대화 삭제를 요청했어요.", "Conversation deletion requested."));
                    if ((string?)message.Payload?["action"] == "switch") _composerAuto?.Stop(PompLanguage.Pick("대화 전환을 요청했어요.", "Conversation switch requested."));
                    _commands.Enqueue(() => HandleRecentSessionsAsync(message.Payload ?? new JObject()));
                    break;
                case UiMessageTypes.SessionFast:
                    var fast = message.PayloadAs<SessionFastPayload>();
                    if (fast != null)
                    {
                        HandleFastModeAsync(fast).Forget(_logger, "fast mode");
                    }

                    break;
                case UiMessageTypes.SessionNew:
                    _composerAuto?.Stop(PompLanguage.Pick("새 대화를 요청했어요.", "New conversation requested."));
                    var requestId = message.PayloadAs<SessionNewPayload>()?.RequestId;
                    _commands.Enqueue(() => HandleNewInTabAsync(requestId));
                    break;
                case UiMessageTypes.BridgeRestart:
                    _composerAuto?.Stop(PompLanguage.Pick("다시 시작을 요청했어요.", "Restart requested."));
                    // Not queued behind chat commands: a restart can take a while (graceful stop, handshake, retries).
                    RestartAsync(_disposeCts.Token).Forget(_logger, "restart");
                    break;
                case UiMessageTypes.LogOpen:
                    Invoke(_environment.ShowLog, "show the log");
                    break;
                case UiMessageTypes.SettingsOpen:
                    Invoke(_environment.ShowSettings, "show the settings");
                    break;
                case UiMessageTypes.WorkspaceFindFiles:
                    // Not queued behind chat commands: the @-mention list must stay responsive while a prompt is sent.
                    var find = message.PayloadAs<WorkspaceFindFilesPayload>();
                    if (find != null)
                    {
                        FindWorkspaceFilesAsync(find).Forget(_logger, "workspace file search");
                    }

                    break;
                case UiMessageTypes.WorkspaceCountBasketTokens:
                    HandleCountBasketTokensAsync(message.Payload ?? new JObject()).Forget(_logger, "basket tokens");
                    break;
                case UiMessageTypes.WorkspacePickFiles:
                    var pick = message.PayloadAs<WorkspacePickFilesPayload>();
                    if (pick != null)
                    {
                        PickWorkspaceFilesAsync(pick).Forget(_logger, "file picker");
                    }

                    break;
                default:
                    _logger.Debug("Ignoring the UI message '" + message.Type + "'.");
                    break;
            }
        }

        /// <summary>workspace/findFiles → workspace/files (the @-mention list).</summary>
        private CancellationTokenSource? _mentionSearch;
        private async Task FindWorkspaceFilesAsync(WorkspaceFindFilesPayload request)
        {
            using var search = CancellationTokenSource.CreateLinkedTokenSource(_disposeCts.Token);
            var previous = Interlocked.Exchange(ref _mentionSearch, search); Cancel(previous);
            WorkspaceFilesPayload answer;
            try
            {
                var root = await TryGetWorkspaceRootAsync().ConfigureAwait(false);
                var query = (request.Query ?? string.Empty).Trim();
                if (query.StartsWith("#", StringComparison.Ordinal))
                {
                    if (query.Length < 2) answer = new WorkspaceFilesPayload { Error = PompLanguage.Pick("검색어가 비어 있어요.", "The query is empty.") };
                    else if (!_registry.TryGet("search_codebase", out var codeSearch)) answer = new WorkspaceFilesPayload { Error = PompLanguage.Pick("실시간 코드 검색을 사용할 수 없어요.", "Live code search is unavailable.") };
                    else
                    {
                        var limit = Math.Max(1, Math.Min(20, request.Limit ?? 20));
                        var result = await codeSearch.ExecuteAsync(new Pomp.Tools.HostToolInvocation("mention-" + request.RequestId, "mention", "search_codebase", new JObject { ["query"] = query.Substring(1), ["limit"] = limit }), search.Token).ConfigureAwait(false);
                        if (result.IsError) answer = new WorkspaceFilesPayload { Error = string.Join(" ", result.Content.Select(c => c.Text)) };
                        else
                        {
                            var hits = result.Details?["hits"] as JArray ?? new JArray();
                            answer = new WorkspaceFilesPayload { Error = (string?)result.Details?["embeddingQueryNotice"], Files = hits.OfType<JObject>().Select(hit => new WorkspaceFileEntry { FullPath = (string?)hit["path"] ?? "", Path = root == null ? (string?)hit["path"] ?? "" : WorkspaceFileSearch.Relative(root, (string?)hit["path"] ?? ""), Kind = string.IsNullOrWhiteSpace((string?)hit["symbol"]) ? "code" : "symbol", Symbol = (string?)hit["symbol"], Line = (int?)hit["startLine"] }).ToList(), Truncated = hits.Count >= limit || (bool?)result.Details?["outputLimited"] == true };
                        }
                    }
                }
                else if (query.StartsWith("folder:", StringComparison.OrdinalIgnoreCase)) answer = await WorkspaceReferenceSearch.FindAsync(root, query.Substring(7), "folder", request.Limit ?? WorkspaceFileSearch.DefaultLimit, search.Token).ConfigureAwait(false);
                else if (query.StartsWith("doc:", StringComparison.OrdinalIgnoreCase)) answer = await WorkspaceReferenceSearch.FindAsync(root, query.Substring(4), "document", request.Limit ?? WorkspaceFileSearch.DefaultLimit, search.Token).ConfigureAwait(false);
                else answer = await WorkspaceFileSearch.FindAsync(_adapters, root, query, request.Limit ?? WorkspaceFileSearch.DefaultLimit, _logger, search.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (search.IsCancellationRequested) { return; }
            catch (Exception ex)
            {
                _logger.Warn("The workspace search failed: " + ex.Message);
                answer = new WorkspaceFilesPayload { Error = PompLanguage.Pick("작업 영역 검색이 실패했어요: ", "The workspace search failed: ") + ex.Message };
            }
            finally { Interlocked.CompareExchange(ref _mentionSearch, null, search); }
            if (search.IsCancellationRequested) return;
            answer.RequestId = request.RequestId ?? string.Empty;
            _ui.Post(UiMessageTypes.WorkspaceFiles, answer, UiDelivery.State);
        }

        /// <summary>workspace/pickFiles → a native multi-select file dialog → workspace/files.</summary>
        private async Task PickWorkspaceFilesAsync(WorkspacePickFilesPayload request)
        {
            var answer = new WorkspaceFilesPayload { RequestId = request.RequestId ?? string.Empty };
            try
            {
                if (!(_environment is IPompFilePicker picker))
                {
                    answer.Error = PompLanguage.Pick("파일을 첨부할 수 없어요.", "Attaching files is not available.");
                }
                else
                {
                    var root = await TryGetWorkspaceRootAsync().ConfigureAwait(false);
                    var files = await picker.PickFilesAsync(root, PompLanguage.Pick("POMP 메시지에 첨부할 파일", "Attach files to the POMP message"), null, true, _disposeCts.Token).ConfigureAwait(false) ?? Array.Empty<string>();
                    answer.Files = files
                        .Where(path => !string.IsNullOrWhiteSpace(path))
                        .Distinct(StringComparer.OrdinalIgnoreCase)
                        .Select(path => new WorkspaceFileEntry { FullPath = path, Path = root == null ? path : WorkspaceFileSearch.Relative(root, path) })
                        .ToList();
                }
            }
            catch (OperationCanceledException) when (_disposeCts.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                _logger.Warn("The file dialog failed: " + ex.Message);
                answer.Error = PompLanguage.Pick("파일 선택 대화 상자에서 오류가 발생했어요: ", "The file dialog failed: ") + ex.Message;
            }

            _ui.Post(UiMessageTypes.WorkspaceFiles, answer, UiDelivery.State);
        }

        private async Task<string?> TryGetWorkspaceRootAsync()
        {
            try
            {
                return await _environment.GetWorkspaceRootAsync(_disposeCts.Token).ConfigureAwait(false);
            }
            catch (Exception ex) when (!(ex is OperationCanceledException))
            {
                _logger.Debug("No workspace root for the file search: " + ex.Message);
                return null;
            }
        }

        private void Invoke(Action action, string what)
        {
            try
            {
                action();
            }
            catch (Exception ex)
            {
                _logger.Error("Could not " + what, ex);
            }
        }

        /// <summary>chat/send: (editor context + attachments +) text → <c>prompt</c>; the turn ends with <c>prompt_result</c>.</summary>
        private async Task<bool> HandleChatSendAsync(ChatSendPayload send, long generation, CancellationToken requestToken = default, string? expectedSession = null, bool bypassQueue = false, ConversationPartnerPayload? capturedPartner = null, string? queuedItemId = null, bool forceQueue = false, string? engineText = null)
        {
            if (requestToken.IsCancellationRequested) return false;
            var accepted = false;
            var text = send.Text ?? string.Empty;
            var attachments = (send.Attachments ?? new List<ChatAttachment>())
                .Where(a => !string.IsNullOrWhiteSpace(a?.Path))
                .Select(a => a.Path)
                .ToList();
            if (string.IsNullOrWhiteSpace(text) && attachments.Count == 0)
            {
                return false;
            }

            send = CopySend(send);
            var partner = capturedPartner ?? DefaultPartner(send);
            QueuedChat? queued = null;
            try
            {
                lock (_stateLock)
                    if (!bypassQueue && (forceQueue || _turns.Count > 0 || _info.IsStreaming || _chatQueue.Count > 0 || generation != _chatStopGeneration)) queued = EnqueueChatLocked(send, partner, generation);
            }
            catch (Exception ex) { _ui.Post(UiMessageTypes.Error, new UiErrorInfo { Code = "queue_rejected", Message = ex.Message, Retryable = false }, UiDelivery.State); return false; }
            if (queued != null)
            {
                PublishQueuedChat(send, queued); ScheduleQueueDispatch(); return false;
            }
            Exception? partnerError = null;
            try { ResolvePartnerTarget(send, partner); }
            catch (Exception ex) { partnerError = ex; }

            // Agents this prompt starts must come after the first job snapshot (taken once per connection and conversation, before
            // the turn is registered): only then are they reported as new and remembered for a team continuation.
            await _backgroundAgents.EnsureBaselineAsync(TimeSpan.FromSeconds(3), requestToken).ConfigureAwait(false);
            if (requestToken.IsCancellationRequested) return false;

            // Bound to the omp process that is current now: if it ends while the message is being composed, the message must not
            // reach its replacement (the turn was already reported as ended).
            CancellationToken epoch;
            Pomp.Bridge.OmpConnection? connection;
            lock (_stateLock)
            {
                epoch = _epoch.Token;
                connection = _epochConnection;
            }
            using var sending = CancellationTokenSource.CreateLinkedTokenSource(_disposeCts.Token, epoch, requestToken);
            bool stopped;
            bool busy;
            var promptId = "prompt-" + Interlocked.Increment(ref _promptCounter).ToString(CultureInfo.InvariantCulture);
            var targetKind = !string.IsNullOrWhiteSpace(send.Team) ? "team" : !string.IsNullOrWhiteSpace(send.Agent) ? "agent" : !string.IsNullOrWhiteSpace(send.AgentInstance) ? "dm" : "main";
            var targetId = (send.Team ?? send.Agent ?? send.AgentInstance ?? "").Trim();
            var turn = new TurnState(promptId, send.ClientMessageId)
            {
                FeedbackTarget = new AgentRunTargetPayload { Kind = targetKind, Id = targetId, Title = targetKind == "main" ? "POMP" : targetId.Substring(0, Math.Min(40, targetId.Length)) },
            };
            try
            {
                lock (_stateLock)
                {
                    if (queuedItemId != null)
                    {
                        var item = _chatQueue.FirstOrDefault(q => q.Id == queuedItemId);
                        if (item == null || _queuePaused || generation != _chatStopGeneration || _queueSession != _info.SessionId || _turns.Count > 0 || _info.IsStreaming || _bridge.State != BridgeState.Running || _sessionSwitchUncertain) return false;
                        _chatQueue.Remove(item);
                    }
                    stopped = generation != _chatStopGeneration;
                    // One message at a time: omp keeps queued messages across an abort, so queuing inside omp would defeat Stop.
                    busy = _turns.Count > 0 || _info.IsStreaming;
                    if (busy) queued = EnqueueChatLocked(send, partner, generation, allowResume: !stopped);
                    else { _sendingPrompt = sending; _turns[promptId] = turn; _backgroundAgents.SetCurrentTurnPrompt(promptId); }
                }
            }
            catch (Exception ex) { _ui.Post(UiMessageTypes.Error, new UiErrorInfo { Code = "queue_rejected", Message = ex.Message, Retryable = false }, UiDelivery.State); return false; }
            if (queuedItemId != null) PublishChatQueue();
            if (busy)
            {
                PublishQueuedChat(send, queued!); ScheduleQueueDispatch(); return false;
            }

            if (stopped)
            {
                sending.Cancel();
            }

            // Gather once, after admission and before announcing the turn, so the bubble names the exact editor snapshot sent.
            // This courtesy must not bypass the ordinary cancellation/epoch handling below or its start-before-end guarantee.
            EditorContextResult? editorContext = null;
            if (!stopped && !sending.IsCancellationRequested && send.IncludeEditorContext && !text.TrimStart().StartsWith("/", StringComparison.Ordinal))
            {
                try
                {
                    var contextTask = _contextBuilder.BuildAsync(sending.Token);
                    contextTask.Observe(); // An uncooperative IDE adapter may finish after Stop has released this wait.
                    editorContext = await contextTask.WithCancellation(sending.Token).ConfigureAwait(false);
                    if (sending.IsCancellationRequested) editorContext = null;
                }
                catch (OperationCanceledException) { /* The turn is still announced, then ended by the existing handling below. */ }
                catch (Exception ex) { _logger.Warn("Could not build the editor context: " + ex.Message); }
            }

            // Posted first (and before any event of this prompt can arrive), so the UI sees turnStarted → assistant... → turnEnd.
            // The user's input as typed rides along, so a replayed transcript (page reload) can show the user bubble again.
            _ui.Post(UiMessageTypes.ChatTurnStarted, new ChatTurnStartedPayload
            {
                PromptId = promptId,
                ClientMessageId = send.ClientMessageId,
                Text = text,
                Attachments = attachments.Count == 0 ? null : attachments.Select(path => new ChatAttachment { Path = path }).ToList(),
                EditorContext = editorContext?.EditorContext,
                Team = string.IsNullOrWhiteSpace(send.Team) ? null : send.Team!.Trim(),
                Agent = string.IsNullOrWhiteSpace(send.Agent) ? null : send.Agent!.Trim(),
                AgentInstance = string.IsNullOrWhiteSpace(send.AgentInstance) ? null : send.AgentInstance!.Trim(),
                Main = string.IsNullOrWhiteSpace(send.Team) && string.IsNullOrWhiteSpace(send.Agent) && string.IsNullOrWhiteSpace(send.AgentInstance) ? true : null,
            });

            ChatTurnEndPayload? endedEarly;
            lock (_stateLock)
            {
                turn.StartPosted = true;
                endedEarly = turn.PendingEnd;
            }

            if (endedEarly != null)
            {
                // The turn already ended (omp exited) between its registration and its announcement: report that, send nothing.
                _ui.Post(UiMessageTypes.ChatTurnEnd, endedEarly);
                lock (_stateLock)
                {
                    _sendingPrompt = null;
                }

                return false;
            }

            try
            {
                if (partnerError != null) throw partnerError;
                if (_sessionSwitchUncertain) throw new InvalidOperationException(PompLanguage.Pick("세션 전환 후 활성 대화를 확실히 알 수 없어요.", "The active conversation is uncertain after a session switch."));
                var state = _bridge.State;
                if (state != BridgeState.Running)
                {
                    throw new OmpBridgeException(PompLanguage.Pick("연결되어 있지 않아요(상태: " + state + ").", "Not connected (state: " + state + ")."));
                }

                sending.Token.ThrowIfCancellationRequested();
                // Computer use is switched by the page (computer/set), never by a typed command the host would not see.
                RefuseComputerCommand(text);

                // engineText: the host's own commands (a goal run) say exactly what the engine gets; the bubble keeps what the user typed.
                var message = engineText ?? await ComposeMessageAsync(editorContext?.Text, text, attachments, sending.Token).ConfigureAwait(false);
                sending.Token.ThrowIfCancellationRequested();
                // A message for a team or one agent goes through the extension's /pomp-team, /pomp-agent or /pomp-dm (docs/architecture.md §8.2).
                message = await ApplyTargetAsync(send, message, sending.Token).ConfigureAwait(false);
                sending.Token.ThrowIfCancellationRequested();
                // An agent conversation answers as the agent only with the engine extension that gives it the role (extensions/partner.ts).
                if (partner?.Kind == "agent") await RequireEngineCommandsAsync(connection, sending.Token, "pomp-partner").ConfigureAwait(false);

                // omp reads each command as one line and asks clients to stay within its physical frame limit (1 MiB).
                var probe = OmpCommands.Prompt(message, null);
                probe["id"] = promptId;
                var size = Pomp.Transport.NdjsonWriter.Encode(probe).Length;
                if (size > MaxPromptFrameBytes)
                {
                    EndTurn(
                        promptId,
                        "error",
                        new UiErrorInfo
                        {
                            Code = "message_too_large",
                            Message = PompLanguage.Pick("메시지가 너무 커요.", "The message is too large."),
                            Retryable = false,
                        });
                    return false;
                }

                lock (_stateLock) if (expectedSession != null && _info.SessionId != expectedSession) throw new InvalidOperationException("POMP Workflow conversation changed before dispatch.");
                BeginRun(turn, send);
                var response = await SendPromptCommandAsync(connection, promptId, message, sending.Token).ConfigureAwait(false);
                sending.Token.ThrowIfCancellationRequested();
                if (!response.Success)
                {
                    EndTurn(
                        promptId,
                        "error",
                        new UiErrorInfo { Code = response.Code ?? "prompt_rejected", Message = response.Error ?? PompLanguage.Pick("메시지를 거부했어요.", "The message was rejected."), Retryable = false });
                    return false;
                }

                accepted = true;
                // A prompt that finished locally (a slash command) never gets a prompt_result: the response is its completion.
                if (response.Data.GetBool("agentInvoked") == false)
                {
                    EndTurn(promptId, "completed", null);
                    RequestInfoRefresh();
                }
            }
            catch (OperationCanceledException) when (epoch.IsCancellationRequested && !_disposeCts.IsCancellationRequested)
            {
                // omp ended (or was replaced) before it took the message. Usually the turn has been ended already.
                EndTurn(
                    promptId,
                    "error",
                    new UiErrorInfo { Code = "connection_lost", Message = PompLanguage.Pick("메시지를 보내기 전에 연결이 끊겼어요.", "The connection was lost before the message was sent."), Retryable = true });
            }
            catch (OperationCanceledException)
            {
                EndTurn(promptId, "aborted", null);
            }
            catch (TimeoutException ex)
            {
                // The frame was written: omp may still accept it late, so a blind resend could run the message twice.
                _logger.Warn("omp did not acknowledge the message: " + ex.Message);
                EndTurn(
                    promptId,
                    "error",
                    new UiErrorInfo
                    {
                        Code = "timeout",
                        Message = PompLanguage.Pick("제한 시간 안에 메시지 수신이 확인되지 않았어요.", "The message was not acknowledged in time."),
                        Retryable = false,
                    });
            }
            catch (Exception ex)
            {
                _logger.Warn("Sending the message failed: " + ex.Message);
                EndTurn(promptId, "error", new UiErrorInfo { Code = "send_failed", Message = ex.Message, Retryable = true });
            }
            finally
            {
                lock (_stateLock)
                {
                    _sendingPrompt = null;
                }
            }
            return accepted;
        }

        /// <summary>
        /// Sends one prompt without a streaming behaviour: messages are never queued inside omp, because omp keeps queued
        /// messages across <c>abort</c> (Stop would not remove them). If omp is unexpectedly busy it rejects the prompt, which ends
        /// the turn with that error.
        /// </summary>
        private async Task<ResponseFrame> SendPromptCommandAsync(Pomp.Bridge.OmpConnection? connection, string promptId, string message, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            await RequireEngineCommandsAsync(connection, cancellationToken, "pomp-permissions").ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            var command = OmpCommands.Prompt(message, null);
            command["id"] = promptId; // known before the response so prompt_result can never race ahead of the bookkeeping
            // Never look up the bridge's newest connection after composition; send only to this epoch's process.
            return await connection!.SendCommandAsync(command, _options.PromptTimeout, cancellationToken).ConfigureAwait(false);
        }

        /// <summary>
        /// Confirms that omp loaded the named POMP extension commands before inference. The policy guard (<c>pomp-permissions</c>)
        /// is required only in a workspace with <c>.pomp/permissions.json</c>; without one the normal approvals apply and no guard is needed.
        /// </summary>
        private async Task RequireEngineCommandsAsync(OmpConnection? connection, CancellationToken token, params string[] required)
        {
            token.ThrowIfCancellationRequested();
            if(connection==null)throw new OmpBridgeException(PompLanguage.Pick("이 요청을 시작했을 때 연결이 없었어요.", "There was no connection when this request started."));
            string root;
            lock(_stateLock)root=_workingDirectory;
            if(!ProjectExecutionPolicy.Exists(root))required=required.Where(name=>name!="pomp-permissions").ToArray();
            if(required.Length==0)return;
            ResponseFrame response;
            try { response=await connection.SendCommandAsync(new JObject { ["type"] = "get_available_commands" },TimeSpan.FromSeconds(5),token).ConfigureAwait(false); }
            catch(TimeoutException ex){throw new InvalidOperationException(PompLanguage.Pick("필요한 POMP 구성 요소가 로드되지 않았어요. 메시지는 보내지 않았어요.", "A required POMP component is not loaded. The message was not sent."),ex);}
            if(!response.Success || response.Data?["commands"] is not JArray commands || commands.Count>4096 || required.Any(name=>!commands.OfType<JObject>().Any(command=>(string?)command["name"]==name && (string?)command["source"]=="extension")))
                throw new InvalidOperationException(PompLanguage.Pick("필요한 POMP 구성 요소가 로드되지 않았어요. 메시지는 보내지 않았어요.", "A required POMP component is not loaded. The message was not sent."));
            lock(_stateLock)if(!ReferenceEquals(connection,_epochConnection))throw new OperationCanceledException(PompLanguage.Pick("확인하는 동안 연결이 바뀌었어요.", "The connection changed during the check."),token);
        }

        /// <summary>
        /// The prompt text: the <c>&lt;vs_context&gt;</c> block (design: addAdditionalContext) and attached file paths before the user's
        /// words. A slash command stays at the start because omp parses it there; the attached paths follow its own text (<see cref="ComposeSlashCommand"/>).
        /// </summary>
        private Task<string> ComposeMessageAsync(string? editorContext, string text, IReadOnlyList<string> attachments, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (text.TrimStart().StartsWith("/", StringComparison.Ordinal))
            {
                return Task.FromResult(ComposeSlashCommand(text, attachments));
            }

            var parts = new List<string>();
            if (!string.IsNullOrEmpty(editorContext))
            {
                parts.Add(editorContext!);
            }

            if (attachments.Count > 0)
            {
                parts.Add(PromptDisplay.AttachmentsHeader(CanReadBuffer()) + "\n" + string.Join("\n", attachments.Select(path => "- " + path)));
            }

            if (text.Length > 0)
            {
                parts.Add(text);
            }

            return Task.FromResult(string.Join("\n\n", parts));
        }

        /// <summary>
        /// chat/stop → <c>abort</c>. The turn ends when omp reports <c>prompt_result</c> with status aborted; if omp is idle and
        /// still has not reported it after <see cref="PompSessionOptions.StopSettleDelay"/>, the turn is ended here so the chat
        /// never stays blocked.
        /// </summary>
        private async Task HandleChatStopAsync()
        {
            _projectHooks?.CancelCurrent();
            _terminalJobs.CancelAll();
            await _interactiveTerminal.StopAsync(_disposeCts.Token).ConfigureAwait(false);
            List<string> open;
            lock (_stateLock)
            {
                open = _turns.Keys.ToList();
            }

            // omp's approval prompts have no abort signal of their own: unblock whatever it still waits for.
            CancelPendingOmpRequests();
            try
            {
                // A steer omp still holds would start a new run after a plain abort: take it back with the rest of omp's queue.
                var steers = TakeSteersSent();
                var withdraw = steers.Count > 0;
                var response = await _bridge.SendCommandAsync(withdraw ? OmpCommands.AbortAndRestoreQueue() : OmpCommands.Abort(), TimeSpan.FromSeconds(15), _disposeCts.Token).ConfigureAwait(false);
                if (!response.Success)
                {
                    PostNotice("warning", PompLanguage.Pick("이번 응답을 중단하지 못했어요: ", "The response could not be stopped: ") + response.Error);
                    return;
                }

                var withdrawn = Math.Min((response.Data?["steering"] as JArray)?.Count ?? 0, steers.Count);
                if (withdraw && withdrawn > 0)
                {
                    // omp delivers steers in order, so the ones it still held are the newest.
                    _ui.Post(UiMessageTypes.ChatSteerWithdrawn, new ChatSteerWithdrawnPayload { SteerIds = steers.Skip(steers.Count - withdrawn).ToList() });
                    PostNotice("info", PompLanguage.Pick("작업 중인 에이전트에게 아직 전달되지 않은 메시지 " + withdrawn + "개는 보내지 않았어요.", withdrawn + " message(s) not yet delivered to the working agent were not sent."));
                }
            }
            catch (Exception ex) when (!(ex is OperationCanceledException))
            {
                _logger.Warn("Abort failed: " + ex.Message);
                PostNotice("warning", PompLanguage.Pick("이번 응답 처리를 중지하지 못했어요: ", "Could not stop the turn: ") + ex.Message);
                return;
            }

            if (open.Count > 0)
            {
                SettleStoppedTurnsAsync(open).Forget(_logger, "stop settle");
            }
        }

        private async Task SettleStoppedTurnsAsync(IReadOnlyList<string> promptIds)
        {
            // omp normally reports a stopped prompt itself (prompt_result "aborted"); this ends the turns it never reports (a
            // message it parked). While omp still works (a slow abort, a background job it woke up for) the turns stay open and
            // are checked again; once it is idle it gets one more delay to report them.
            var sawStreaming = false;
            while (true)
            {
                await Task.Delay(_options.StopSettleDelay, _disposeCts.Token).ConfigureAwait(false);
                if (!HasOpenTurn(promptIds))
                {
                    return;
                }

                if (IsStreamingNow())
                {
                    sawStreaming = true;
                    continue;
                }

                if (!sawStreaming)
                {
                    break;
                }

                sawStreaming = false;
            }

            foreach (var promptId in promptIds)
            {
                EndTurn(promptId, "aborted", null);
            }
        }

        private bool HasOpenTurn(IReadOnlyList<string> promptIds)
        {
            lock (_stateLock)
            {
                return promptIds.Any(_turns.ContainsKey);
            }
        }

        /// <summary>session/new → <c>new_session</c>; the permission session grants are forgotten.</summary>
        /// <summary>
        /// session/new → <c>new_session</c>; the permission session grants are forgotten. Answers with exactly one
        /// <c>session/newResult</c>: the page keeps its transcript until it reads "completed".
        /// </summary>
        private async Task HandleNewSessionAsync(string? requestId)
        {
            // omp's new_session first aborts the run, which waits for any approval prompt omp is blocked on: release them first.
            CancelPendingOmpRequests();
            string status;
            string? message = null;
            bool wasUncertain; OmpConnection? connection; CancellationToken epoch;
            lock (_stateLock) { wasUncertain = _sessionSwitchUncertain; connection = _epochConnection; epoch = _epoch.Token; }
            var requested = false;
            try
            {
                // Not connected comes first: with the engine down (e.g. waiting for workspace trust) that is the reason, not what it left behind.
                if (connection == null || _bridge.State != BridgeState.Running) throw new InvalidOperationException(PompLanguage.Pick("POMP가 연결되지 않았어요.", "POMP is not connected."));
                if (_interactiveTerminal.HasUnfinishedWork) throw new InvalidOperationException(PompLanguage.Pick("대화형 터미널이 아직 끝나지 않았어요.", "The interactive terminal has not finished."));
                if (_sessionSwitchUncertain && _recoveryStatus != "failed") throw new InvalidOperationException(PompLanguage.Pick("세션 전환 결과가 확실하지 않아요.", "The session switch outcome is uncertain."));
                if (ApprovalRestartPending()) throw new InvalidOperationException(PompLanguage.Pick("권한 모드를 적용하는 중이에요.", "The permission mode is being applied."));
                lock (_stateLock)
                {
                    epoch.ThrowIfCancellationRequested();
                    if (!ReferenceEquals(connection, _epochConnection)) throw new OperationCanceledException(epoch);
                    _sessionSwitchUncertain = true; requested = true;
                }
                var response = await connection.SendCommandAsync(OmpCommands.NewSession(), TimeSpan.FromSeconds(30), epoch).ConfigureAwait(false);
                epoch.ThrowIfCancellationRequested();
                if (!response.Success)
                {
                    _sessionSwitchUncertain = wasUncertain;
                    status = "failed";
                    message = PompLanguage.Pick("새 세션을 시작하지 못했어요: ", "Could not start a new session: ") + response.Error;
                }
                else if (response.Data.GetBool("cancelled") == true)
                {
                    _sessionSwitchUncertain = wasUncertain;
                    status = "cancelled";
                    message = PompLanguage.Pick("확장이 새 세션을 취소했어요.", "An extension cancelled the new session.");
                }
                else
                {
                    var current = await connection.SendCommandAsync(OmpCommands.GetState(), TimeSpan.FromSeconds(15), epoch).ConfigureAwait(false);
                    if (!current.Success || current.Data is not JObject currentState || string.IsNullOrEmpty(currentState.GetString("sessionId"))) throw new InvalidOperationException(PompLanguage.Pick("새 대화를 확인하지 못했어요.", "Could not confirm the new conversation."));
                    epoch.ThrowIfCancellationRequested();
                    if (!ApplyState(currentState, acceptRecovery: true, currentLocked: () => ReferenceEquals(connection, _epochConnection), confirmSession: true))
                        throw new OperationCanceledException(epoch);
                    _gate.ResetSession();
                    EndAllTurns("aborted", null);
                    // The page clears its transcript on "completed"; a later reload must not bring the old conversation back.
                    _ui.ClearTranscript();
                    status = "completed";
                }
            }
            catch (OperationCanceledException) when (_disposeCts.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                _logger.Warn("new_session failed: " + ex.Message);
                if (requested)
                    PublishRecovery("failed", PompLanguage.Pick("새 대화를 확인하지 못했어요.", "The new conversation could not be confirmed."),
                        () => !epoch.IsCancellationRequested && ReferenceEquals(connection, _epochConnection) && _sessionSwitchUncertain && _bridge.State == BridgeState.Running);
                status = "failed";
                message = PompLanguage.Pick("새 세션을 시작하지 못했어요: ", "Could not start a new session: ") + ex.Message;
            }

            _ui.Post(UiMessageTypes.SessionNewResult, new SessionNewResultPayload { RequestId = requestId, Status = status, Message = message });
        }
    }
}
