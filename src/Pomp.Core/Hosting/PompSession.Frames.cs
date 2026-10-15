using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;
using Pomp.Bridge;
using Pomp.Diagnostics;
using Pomp.Internal;
using Pomp.Json;
using Pomp.Permissions;
using Pomp.Protocol;
using Pomp.Tools;
using Pomp.Ui;

namespace Pomp.Hosting
{
    // omp → host: frame routing, translation into the UI protocol (docs/ui-protocol.md) and the bookkeeping behind it.
    public sealed partial class PompSession
    {
        private const int ToolResultDisplayChars = 4000;
        private const int ToolArgumentStringChars = 1500;

        private readonly Dictionary<string, TurnState> _turns = new Dictionary<string, TurnState>(StringComparer.Ordinal);
        private readonly Dictionary<string, AssistantMessageState> _assistantMessages = new Dictionary<string, AssistantMessageState>(StringComparer.Ordinal);
        private readonly Dictionary<string, ToolState> _toolStarts = new Dictionary<string, ToolState>(StringComparer.Ordinal);
        private readonly Dictionary<string, OmpRequestState> _ompRequests = new Dictionary<string, OmpRequestState>(StringComparer.Ordinal);
        private string? _currentAssistantId;
        private long _localMessageCounter;

        private void OnFrameReceived(object? sender, OmpFrameEventArgs e)
        {
            var frame = e.Frame;
            try
            {
                switch (frame.Type)
                {
                    case "pomp_terminal_state":
                        _interactiveTerminal.Observe(frame.Json);
                        break;
                    case "subagent_lifecycle":
                    case "subagent_progress":
                        var jobOrigin = _bridge.OriginOf(frame);
                        lock (_stateLock) if (jobOrigin != null && (jobOrigin.IsClosed || !ReferenceEquals(jobOrigin, _epochConnection))) break;
                        if (frame.Json["payload"] is JObject jobPayload)
                        {
                            if (frame.Type == "subagent_lifecycle")
                            {
                                string root, session;
                                lock (_stateLock) { if (Volatile.Read(ref _workspaceSwitching) == 1) break; root = _workingDirectory; session = _info.SessionId ?? ""; }
                                _backgroundAgents.ObserveLifecycle(jobPayload); _isolationResults?.Observe(jobPayload, root, session);
                            }
                            else _backgroundAgents.ObserveProgress(jobPayload);
                        }
                        else _backgroundAgents.Schedule();
                        break;
                    case OmpFrameTypes.AgentStart:
                        SetStreaming(true, _bridge.OriginOf(frame));
                        break;
                    case "turn_end":
                        _taskApprovalDetails.Clear();
                        break;
                    case OmpFrameTypes.AgentEnd:
                        HandleAgentEnd(frame);
                        break;
                    case OmpFrameTypes.MessageStart:
                        HandleMessageStart(frame);
                        break;
                    case OmpFrameTypes.MessageUpdate:
                        HandleMessageUpdate(frame);
                        break;
                    case OmpFrameTypes.MessageEnd:
                        HandleMessageEnd(frame);
                        break;
                    case OmpFrameTypes.ToolExecutionStart:
                        HandleToolStart(frame);
                        break;
                    case OmpFrameTypes.ToolExecutionUpdate:
                        HandleToolUpdate(frame);
                        break;
                    case OmpFrameTypes.ToolExecutionEnd:
                        HandleToolEnd(frame);
                        break;
                    case OmpFrameTypes.PromptResult:
                        HandlePromptResult(frame);
                        break;
                    case OmpFrameTypes.AvailableCommandsUpdate:
                        ObserveCommands(frame);
                        break;
                    case OmpFrameTypes.SessionSettled:
                        RequestInfoRefresh();
                        SignalSessionSettled();
                        break;
                    case OmpFrameTypes.ExtensionUiRequest:
                        HandleExtensionUiRequest(frame);
                        break;
                    case OmpFrameTypes.HostToolCall:
                        HandleHostToolCall(frame);
                        break;
                    case OmpFrameTypes.HostToolCancel:
                        _dispatcher.HandleCancel(frame.As<HostToolCancelFrame>(), _bridge.OriginOf(frame));
                        break;
                    case OmpFrameTypes.HostUriRequest:
                        HandleHostUriRequest(frame);
                        break;
                    case OmpFrameTypes.ExtensionError:
                        HandleExtensionError(frame);
                        break;
                    case OmpFrameTypes.Notice:
                        var noticeLevel = NormalizeLevel(frame.Json.GetString("level"));
                        var noticeText = frame.Json.GetString("message") ?? string.Empty;
                        if (IsEngineHousekeepingNotice(noticeLevel, noticeText))
                        {
                            // e.g. "xd://: mounted installed_skills, run_terminal" when omp maps the host tools at start-up.
                            _logger.Debug("omp notice: " + noticeText);
                            break;
                        }

                        PostNotice(noticeLevel, noticeText);
                        // omp 18.5: when another omp process owns the session file, the next write moves this conversation to a sibling
                        // file with a new session id; read the state again so the conversation list and the session file follow it.
                        if (frame.Json.GetString("source") == "session-persistence") RequestInfoRefresh();
                        break;
                    case OmpFrameTypes.CommandOutput:
                        // The answer to the page's computer toggle is shown by the toggle, not as a chat notice.
                        if (!ObserveMcpOutput(frame) && !ObserveCommandOutput(frame) && !ObserveContextOutput(frame)) PostNotice("info", frame.Json.GetString("text") ?? string.Empty, "pre");
                        break;
                    case OmpFrameTypes.Response:
                        HandleStrayResponse(frame);
                        break;
                    case OmpFrameTypes.ModelChanged:
                    case OmpFrameTypes.ThinkingLevelChanged:
                    case OmpFrameTypes.SessionInfoUpdate:
                    case OmpFrameTypes.ConfigUpdate:
                        RequestInfoRefresh();
                        break;
                    case OmpFrameTypes.AutoCompactionStart:
                        PostNotice("info", PompLanguage.Pick("컨텍스트 압축 중", "Compacting context"));
                        break;
                    case OmpFrameTypes.AutoRetryStart:
                        PostNotice("info", PompLanguage.Pick("공급자 연결 재시도 중", "Retrying provider connection"));
                        break;
                    default:
                        // turn_start/turn_end, subagent frames, host_uri_cancel...: nothing to show.
                        break;
                }
            }
            catch (Exception ex)
            {
                _logger.Error("Failed to handle the omp frame '" + frame.Type + "'", ex);
            }
        }

        // ---- agent / messages ----------------------------------------------------------------------------------------

        private void HandleAgentEnd(OmpFrame frame)
        {
            // Only a terminal agent_end completes a run: retries, compaction continuations and reminders keep it going.
            if (frame.Json.GetBool("isTerminal") == false)
            {
                return;
            }

            _taskApprovalDetails.Clear();
            SetStreaming(false, _bridge.OriginOf(frame));
            RequestInfoRefresh();
            // The context meter's breakdown changes with every turn (docs/ui-protocol.md "session/info").
            ScheduleContextRefresh();
            // So do the advisors' context use and spend.
            _advisors.Refresh();
        }

        private void HandleMessageStart(OmpFrame frame)
        {
            var messageId = frame.Json.GetString("messageId");
            if (!IsAssistantMessage(frame.Json, messageId))
            {
                return;
            }

            var id = messageId ?? NewLocalMessageId();
            lock (_stateLock)
            {
                _currentAssistantId = id;
            }

            EnsureAssistantStarted(id);
        }

        private void HandleMessageUpdate(OmpFrame frame)
        {
            var messageId = frame.Json.GetString("messageId") ?? CurrentAssistantId();
            if (!IsAssistantMessage(frame.Json, messageId))
            {
                return;
            }

            var update = frame.Json.GetObject("assistantMessageEvent");
            string kind;
            switch (update.GetString("type"))
            {
                case "text_delta":
                    kind = "text";
                    break;
                case "thinking_delta":
                    kind = "thinking";
                    break;
                default:
                    return;
            }

            var delta = update.GetString("delta");
            if (string.IsNullOrEmpty(delta) || messageId == null)
            {
                return;
            }

            EnsureAssistantStarted(messageId);
            lock (_stateLock)
            {
                if (_assistantMessages.TryGetValue(messageId, out var state))
                {
                    state.SawDelta = true;
                }
            }

            _ui.Post(UiMessageTypes.ChatAssistantDelta, new ChatAssistantDeltaPayload { MessageId = messageId, Kind = kind, Delta = delta! });
        }

        private void HandleMessageEnd(OmpFrame frame)
        {
            if (PostAdvisorNotes(frame.Json.GetObject("message")))
            {
                return;
            }

            var messageId = frame.Json.GetString("messageId") ?? CurrentAssistantId();
            if (!IsAssistantMessage(frame.Json, messageId) || messageId == null)
            {
                return;
            }

            var message = frame.Json.GetObject("message");
            CountUsage(frame, message);
            EnsureAssistantStarted(messageId);

            AssistantMessageState? state;
            lock (_stateLock)
            {
                _assistantMessages.TryGetValue(messageId, out state);
                _assistantMessages.Remove(messageId);
                if (_currentAssistantId == messageId)
                {
                    _currentAssistantId = null;
                }
            }

            if (state != null && !state.SawDelta)
            {
                // Nothing was streamed (a provider without deltas): deliver the finished content in one go.
                PostFinalContent(messageId, message);
            }

            _ui.Post(
                UiMessageTypes.ChatAssistantEnd,
                new ChatAssistantEndPayload
                {
                    MessageId = messageId,
                    StopReason = message.GetString("stopReason"),
                    ErrorMessage = message.GetString("errorMessage"),
                });

            // Each finished model call changes the context usage: keep the status bar meter current during long tool loops.
            RequestInfoRefresh();
        }

        private void PostFinalContent(string messageId, JObject? message)
        {
            if (message?["content"] is JArray content)
            {
                foreach (var block in content)
                {
                    var type = block.GetString("type");
                    string? text;
                    string kind;
                    if (type == "thinking")
                    {
                        text = block.GetString("thinking") ?? block.GetString("text");
                        kind = "thinking";
                    }
                    else if (type == "text")
                    {
                        text = block.GetString("text");
                        kind = "text";
                    }
                    else
                    {
                        continue;
                    }

                    if (!string.IsNullOrEmpty(text))
                    {
                        _ui.Post(UiMessageTypes.ChatAssistantDelta, new ChatAssistantDeltaPayload { MessageId = messageId, Kind = kind, Delta = text! });
                    }
                }
            }
            else if (message?["content"] is JValue value && value.Type == JTokenType.String && !string.IsNullOrEmpty((string?)value))
            {
                _ui.Post(UiMessageTypes.ChatAssistantDelta, new ChatAssistantDeltaPayload { MessageId = messageId, Kind = "text", Delta = (string)value! });
            }
        }

        /// <summary>
        /// The notes advisors give the chat agent (the engine's "advisor" custom message, shown to the user): posted as one card of the
        /// chat. True for such a message.
        /// </summary>
        private bool PostAdvisorNotes(JObject? message)
        {
            if (message?.GetString("role") != "custom" || message.GetString("customType") != "advisor") return false;
            if (message["display"]?.Type == JTokenType.Boolean && !(bool)message["display"]!) return true;
            var payload = AdvisorNotesPayload(message);
            if (payload.Notes.Count > 0) _ui.Post(UiMessageTypes.ChatAdvisorNotes, payload);
            return true;
        }

        /// <summary>The notes of an "advisor" custom message, bounded (at most 32 notes of 8,000 characters).</summary>
        internal static ChatAdvisorNotesPayload AdvisorNotesPayload(JObject message)
        {
            var payload = new ChatAdvisorNotesPayload();
            foreach (var row in ((message["details"] as JObject)?["notes"] as JArray ?? new JArray()).OfType<JObject>().Take(32))
            {
                var note = row.GetString("note")?.Trim();
                if (string.IsNullOrEmpty(note)) continue;
                var advisor = row.GetString("advisor") ?? string.Empty;
                var severity = row.GetString("severity");
                payload.Notes.Add(new ChatAdvisorNotePayload
                {
                    Advisor = advisor.Length > 200 ? advisor.Substring(0, 200) : advisor,
                    Severity = severity == null ? null : severity.Length > 32 ? severity.Substring(0, 32) : severity,
                    Note = note!.Length > 8000 ? note.Substring(0, 8000) + "…" : note,
                });
            }
            return payload;
        }

        /// <summary>Only assistant messages reach the UI; user echoes, tool results and custom messages (role "custom") never do.</summary>
        private bool IsAssistantMessage(JObject frame, string? messageId)
        {
            var role = frame.GetObject("message").GetString("role");
            if (role != null)
            {
                return string.Equals(role, "assistant", StringComparison.Ordinal);
            }

            if (messageId == null)
            {
                return false;
            }

            lock (_stateLock)
            {
                return _assistantMessages.ContainsKey(messageId);
            }
        }

        private void EnsureAssistantStarted(string messageId)
        {
            bool isNew;
            lock (_stateLock)
            {
                isNew = !_assistantMessages.ContainsKey(messageId);
                if (isNew)
                {
                    _assistantMessages[messageId] = new AssistantMessageState();
                }
            }

            if (isNew)
            {
                _ui.Post(UiMessageTypes.ChatAssistantStart, new ChatAssistantStartPayload { MessageId = messageId });
            }
        }

        private string? CurrentAssistantId()
        {
            lock (_stateLock)
            {
                return _currentAssistantId;
            }
        }

        private string NewLocalMessageId() => "local-" + Interlocked.Increment(ref _localMessageCounter).ToString(CultureInfo.InvariantCulture);

        // ---- tools -----------------------------------------------------------------------------------------------------

        private void HandleToolStart(OmpFrame frame)
        {
            var toolCallId = frame.Json.GetString("toolCallId");
            var toolName = frame.Json.GetString("toolName");
            if (string.IsNullOrEmpty(toolCallId) || string.IsNullOrEmpty(toolName))
            {
                return;
            }

            lock (_stateLock)
            {
                _toolStarts[toolCallId!] = new ToolState(toolName!);
            }

            var args = frame.Json.GetObject("args") ?? frame.Json.GetObject("arguments");
            var network = OmpNetworkTools.Describe(toolName!, args);
            if (network != null)
            {
                // The design: omp's network access is allowed by default and logged; the option blocks the known network tools.
                _metrics.Count("network." + toolName);
                _logger.Info("omp network tool call: " + toolName + " " + network
                    + (_settings.BlockOmpNetworkTools ? " (network tools are blocked by the pomp option; omp should refuse the call)" : string.Empty));
            }

            _ui.Post(
                UiMessageTypes.ChatToolStart,
                new ChatToolStartPayload
                {
                    ToolCallId = toolCallId!,
                    ToolName = toolName!,
                    Args = args == null ? null : JsonExtensions.Compact(args, ToolArgumentStringChars),
                    IsHostTool = _registry.TryGet(toolName!, out _),
                });
        }

        private void HandleToolUpdate(OmpFrame frame)
        {
            var toolCallId = frame.Json.GetString("toolCallId");
            if (string.IsNullOrEmpty(toolCallId))
            {
                return;
            }

            var text = JsonExtensions.ExtractText(frame.Json["partialResult"]);
            if (text.Length == 0)
            {
                return;
            }

            _ui.Post(UiMessageTypes.ChatToolUpdate, new ChatToolUpdatePayload { ToolCallId = toolCallId!, Text = TextUtil.Truncate(text, ToolResultDisplayChars) });
        }

        private void HandleToolEnd(OmpFrame frame)
        {
            _taskApprovalDetails.End(frame.Json.GetString("toolCallId") ?? "");
            var toolCallId = frame.Json.GetString("toolCallId");
            if (string.IsNullOrEmpty(toolCallId))
            {
                return;
            }

            ToolState? state;
            lock (_stateLock)
            {
                _toolStarts.TryGetValue(toolCallId!, out state);
                _toolStarts.Remove(toolCallId!);
            }

            var result = frame.Json["result"];
            var isError = frame.Json.GetBool("isError") ?? (result as JObject).GetBool("isError") ?? false;
            var name = frame.Json.GetString("toolName") ?? state?.Name;
            if (!string.IsNullOrEmpty(name) && !_registry.TryGet(name!, out _))
            {
                // omp's own tools (host tools are counted by the dispatcher, with their details).
                _metrics.Count("tool." + name + (isError ? ".error" : ".ok"));
                if (state != null)
                {
                    _metrics.Time("tool." + name, TimeSpan.FromMilliseconds(state.ElapsedMilliseconds));
                }
            }

            var text = JsonExtensions.ExtractText(result);
            _ui.Post(
                UiMessageTypes.ChatToolEnd,
                new ChatToolEndPayload
                {
                    ToolCallId = toolCallId!,
                    ToolName = frame.Json.GetString("toolName") ?? state?.Name ?? string.Empty,
                    IsError = isError,
                    ResultText = text.Length == 0 ? null : TextUtil.Truncate(text, ToolResultDisplayChars),
                    DurationMs = state?.ElapsedMilliseconds,
                });
        }

        // ---- turns -----------------------------------------------------------------------------------------------------

        private void HandlePromptResult(OmpFrame frame)
        {
            var result = frame.As<PromptResultFrame>();
            if (string.IsNullOrEmpty(result.Id))
            {
                return;
            }

            var status = NormalizeStatus(result.Status);
            UiErrorInfo? error = null;
            if (status == "error")
            {
                error = new UiErrorInfo
                {
                    Code = result.Error?.HttpStatus is int http ? "http_" + http.ToString(CultureInfo.InvariantCulture) : "provider_error",
                    Message = string.IsNullOrWhiteSpace(result.Error?.Message) ? PompLanguage.Pick("이번 응답 처리가 실패했어요.", "The turn failed.") : result.Error!.Message,
                    Retryable = result.Error?.Retryable ?? false,
                };
            }

            EndTurn(result.Id!, status, error);
            RequestInfoRefresh();
        }

        private static string NormalizeStatus(string? status)
        {
            switch (status)
            {
                case "aborted":
                    return "aborted";
                case "error":
                    return "error";
                default:
                    return "completed";
            }
        }

        /// <summary>A response that no command was waiting for: a late error for an accepted prompt, or a "parse" failure.</summary>
        private void HandleStrayResponse(OmpFrame frame)
        {
            var response = frame.As<ResponseFrame>();
            if (response.Success)
            {
                _logger.Debug("Unmatched omp response for '" + response.Command + "' (id " + response.Id + ").");
                return;
            }

            _logger.Warn("omp command '" + response.Command + "' failed: " + response.Error + (response.Code != null ? " (" + response.Code + ")" : string.Empty));
            if (!string.IsNullOrEmpty(response.Id)
                && (response.Command == OmpCommandTypes.Prompt || response.Command == OmpCommandTypes.AbortAndPrompt))
            {
                // "prompt" is acknowledged first and may fail afterwards while it is scheduled: that ends the turn.
                EndTurn(
                    response.Id!,
                    "error",
                    new UiErrorInfo { Code = response.Code ?? "prompt_failed", Message = response.Error ?? PompLanguage.Pick("요청을 실행하지 못했어요.", "The request could not be run."), Retryable = false });
            }
        }

        private void EndTurn(string promptId, string status, UiErrorInfo? error)
        {
            var end = new ChatTurnEndPayload { PromptId = promptId, Status = status, Error = error };
            Pomp.Features.AgentRunHistory? history; string? runId; string? team; bool fromHook;
            lock (_stateLock)
            {
                if (!_turns.TryGetValue(promptId, out var turn))
                {
                    return;
                }

                _turns.Remove(promptId);
                team = turn.Team; fromHook = IsHookTurn(turn);
                RememberHookTurnLocked(turn);
                // Pause before releasing the state lock: an idle refresh must not dispatch behind a failed turn.
                if (status != "completed") _queuePaused = true;
                history = turn.History; runId = turn.RunId;
                _backgroundAgents.SetCurrentTurnTeam(null);
                _backgroundAgents.SetCurrentTurnPrompt(null);
                _metrics.Count("turn." + status);
                _metrics.Time("turn", turn.Elapsed.Elapsed);
                if (!turn.StartPosted)
                {
                    // The sender has not announced this turn yet (e.g. omp exited meanwhile): it posts this end after the start,
                    // so the page never sees an end before its start.
                    turn.PendingEnd = end;
                    return;
                }
            }

            _ui.Post(UiMessageTypes.ChatTurnEnd, end);
            if (history != null && runId != null)
            {
                try { history.End(runId, status); PublishRunHistory(); }
                catch (Exception ex) { _logger.Warn("Run history could not be saved: " + ex.Message); }
            }
            if (status != "completed") PauseChatQueue(status == "aborted"
                ? PompLanguage.Pick("응답이 중지됐어요.", "Turn aborted.")
                : PompLanguage.Pick("응답에 오류가 발생했어요.", "Turn failed."));
            else ScheduleQueueDispatch();
            if (team != null) RaiseHookEvent(status == "completed" ? "team.completed" : "team.failed", "team", team, "Team run " + status + ".", promptId, fromHook);
        }

        private void EndAllTurns(string status, UiErrorInfo? error)
        {
            _taskApprovalDetails.Clear();
            List<string> ids;
            lock (_stateLock)
            {
                ids = _turns.Keys.ToList();
            }

            foreach (var id in ids)
            {
                EndTurn(id, status, error);
            }
        }

        /// <summary>The process is gone: close whatever the UI still shows as running.</summary>
        private void ResetStreamingState(string reason)
        {
            List<string> assistantIds;
            List<KeyValuePair<string, ToolState>> tools;
            lock (_stateLock)
            {
                assistantIds = _assistantMessages.Keys.ToList();
                tools = _toolStarts.ToList();
                _assistantMessages.Clear();
                _toolStarts.Clear();
                _currentAssistantId = null;
            }

            foreach (var id in assistantIds)
            {
                _ui.Post(UiMessageTypes.ChatAssistantEnd, new ChatAssistantEndPayload { MessageId = id, StopReason = "aborted", ErrorMessage = reason });
            }

            foreach (var tool in tools)
            {
                _ui.Post(
                    UiMessageTypes.ChatToolEnd,
                    new ChatToolEndPayload
                    {
                        ToolCallId = tool.Key,
                        ToolName = tool.Value.Name,
                        IsError = true,
                        ResultText = reason,
                        DurationMs = tool.Value.ElapsedMilliseconds,
                    });
            }

            SetStreaming(false);
        }

        // ---- session info ------------------------------------------------------------------------------------------------

        private bool IsStreamingNow()
        {
            lock (_stateLock)
            {
                return _info.IsStreaming;
            }
        }

        /// <param name="streaming">The new state.</param>
        /// <param name="origin">The connection whose frame reported it, if any. Checked under the lock that the connection-ended
        /// reset also takes: once that process is closed its late events change nothing.</param>
        private void SetStreaming(bool streaming, Pomp.Bridge.OmpConnection? origin = null)
        {
            SessionInfoPayload updated;
            lock (_stateLock)
            {
                if (origin != null && origin.IsClosed)
                {
                    return;
                }

                _streamingRevision++;
                if (_info.IsStreaming == streaming)
                {
                    return;
                }

                updated = CloneInfo(_info);
                updated.IsStreaming = streaming;
                _info = updated;
                _infoJson = PompJson.Serialize(updated);
            }

            _ui.Post(UiMessageTypes.SessionInfo, updated, UiDelivery.State);
            if (!streaming) ScheduleQueueDispatch();
        }

        private static string LimitPlanText(string text, int limit) => text.Length <= limit ? text : text.Substring(0, limit) + "…";

        /// <summary>
        /// Applies a <c>get_state</c> payload and posts <c>session/info</c> when something changed; false when it was not applied.
        /// With <paramref name="streamingRevision"/> (taken when the request was sent), its <c>isStreaming</c> is ignored if an
        /// agent_start/agent_end was handled since: that event is newer than the reply. <paramref name="currentLocked"/> is checked
        /// under the state lock in the same section that commits the snapshot. <paramref name="confirmSession"/> also ends an
        /// uncertain switch and, with <paramref name="acceptRecovery"/>, records this conversation as the recovered one. Never call
        /// this (or post to the page) while holding the state lock: the page's ready handler takes that lock while holding the hub's.
        /// </summary>
        private bool ApplyState(JObject state, long? streamingRevision = null, bool acceptRecovery = false, Func<bool>? currentLocked = null, bool confirmSession = false)
        {
            lock (_stateLock) if ((!acceptRecovery && _recoveryStatus != "ready") || (currentLocked != null && !currentLocked())) return false;
            ModelInfoPayload? model = null;
            var modelObject = state.GetObject("model");
            if (modelObject != null)
            {
                var modelId = modelObject.GetString("id");
                if (!string.IsNullOrEmpty(modelId))
                {
                    model = new ModelInfoPayload
                    {
                        Provider = modelObject.GetString("provider") ?? string.Empty,
                        Id = modelId!,
                        Name = modelObject.GetString("name"),
                        ContextWindow = modelObject.GetLong("contextWindow") is long capacity && capacity > 0 && capacity <= 1000000000 ? capacity : (long?)null,
                    };
                }
            }

            ContextUsagePayload? usage = null;
            var usageObject = state.GetObject("contextUsage");
            if (usageObject != null)
            {
                var tokens = usageObject.GetLong("tokens");
                var window = usageObject.GetLong("contextWindow");
                if (tokens.HasValue && window.HasValue && window.Value > 0)
                {
                    usage = new ContextUsagePayload
                    {
                        Tokens = tokens.Value,
                        ContextWindow = window.Value,
                        Percent = usageObject.GetDouble("percent") ?? (tokens.Value * 100.0 / window.Value),
                        Breakdown = ContextBreakdownFor(state.GetString("sessionId")),
                    };
                }
            }

            FastModePayload? fastMode = null;
            if (modelObject != null)
            {
                var fastEnabled = state.GetBool("fastModeEnabled") ?? false;
                fastMode = new FastModePayload
                {
                    // Enabled implies available: the user must always be able to turn it off again.
                    Available = fastEnabled || HasServiceTierFamily(modelObject),
                    Enabled = fastEnabled,
                    Active = state.GetBool("fastModeActive") ?? false,
                };
            }

            var phases = new List<AgentPlanPhasePayload>();
            var planTruncated = false; var taskCount = 0;
            if (state["todoPhases"] is JArray rawPhases)
            {
                foreach (var phase in rawPhases.OfType<JObject>())
                {
                    if (phases.Count >= 20 || taskCount >= 200) { planTruncated = true; break; }
                    if (!(phase["tasks"] is JArray tasks)) continue;
                    var shown = new AgentPlanPhasePayload { Name = LimitPlanText(phase.GetString("name") ?? "", 200) };
                    foreach (var task in tasks.OfType<JObject>())
                    {
                        if (taskCount >= 200) { planTruncated = true; break; }
                        var status = task.GetString("status");
                        if (status != "pending" && status != "in_progress" && status != "completed" && status != "abandoned" && status != "blocked") continue;
                        var blocker = task.GetString("blocker");
                        shown.Tasks.Add(new AgentPlanTaskPayload { Content = LimitPlanText(task.GetString("content") ?? "", 600), Status = status!, Blocker = blocker == null ? null : LimitPlanText(blocker, MaxPlanBlockerChars) }); taskCount++;
                    }
                    phases.Add(shown);
                }
            }

            // The whole plan (the page above holds the first 20 phases / 200 tasks), kept for the paged plan view and its row edits.
            PlanSnapshot? previousPlan; lock (_stateLock) previousPlan = _enginePlan;
            var plan = state["todoPhases"] is JArray wholePlan ? PlanSnapshot.Of(wholePlan, previousPlan) : null;

            SessionInfoPayload updated;
            bool modelChanged, thinkingChanged, changed;
            lock (_stateLock)
            {
                if ((!acceptRecovery && _recoveryStatus != "ready") || (currentLocked != null && !currentLocked())) return false;
                modelChanged = _info.Model?.Provider != model?.Provider || _info.Model?.Id != model?.Id;
                thinkingChanged = !string.Equals(_info.ThinkingLevel, state.GetString("thinkingLevel"), StringComparison.Ordinal);
                _sessionFile = state.GetString("sessionFile");
                _sessionFolderRoot = _workingDirectory;
                var recoveryMessage = _info.RecoveryMessage;
                if (confirmSession)
                {
                    _sessionSwitchUncertain = false;
                    if (acceptRecovery)
                    {
                        _recoveryStatus = "ready"; recoveryMessage = null;
                        _recoveryFile = _sessionFile; _recoverySession = state.GetString("sessionId"); _recoveryRoot = _workingDirectory;
                    }
                }
                _taskApprovalDetails.SetSession(state.GetString("sessionId") ?? "");
                _enginePlan = plan;
                updated = new SessionInfoPayload
                {
                    RecoveryStatus = _recoveryStatus,
                    RecoveryMessage = recoveryMessage,
                    Model = model,
                    ThinkingLevel = state.GetString("thinkingLevel"),
                    SessionId = state.GetString("sessionId"),
                    SessionName = state.GetString("sessionName"),
                    TodoPhases = phases, PlanTruncated = planTruncated, HasPendingAsyncWork = state.GetBool("hasPendingAsyncWork") ?? false,
                    PlanTaskCount = plan?.TaskCount ?? 0, PlanOpenCount = plan?.OpenCount ?? 0, PlanRevision = plan?.Revision,
                    ContextUsage = usage,
                    ThreeTurnCompaction = state.GetObject("pompCompaction") is JObject policy && policy.GetLong("keepRecentTurns") == 3 && policy.GetString("automaticMethod") == "soft",
                    IsStreaming = streamingRevision.HasValue && streamingRevision.Value != _streamingRevision
                        ? _info.IsStreaming
                        : state.GetBool("isStreaming") ?? _info.IsStreaming,
                    WorkingDirectory = string.IsNullOrEmpty(_workingDirectory) ? null : _workingDirectory,
                    FastMode = fastMode,
                };
                // Event-rule forwarding belongs to one conversation: any confirmed change of conversation turns it off.
                if (!string.IsNullOrEmpty(updated.SessionId)) ResetHooksIfSessionChangedLocked(updated.SessionId!);
                var json = PompJson.Serialize(updated);
                changed = json != _infoJson;
                if (changed)
                {
                    _info = updated;
                    _infoJson = json;
                }
            }

            // These publish to the page, so they run after the state lock is released.
            RememberSession(state);
            // A conversation the breakdown was not measured for yet (first state, new or switched session): measure it once idle.
            if (!string.IsNullOrEmpty(state.GetString("sessionId")) && !ContextMeasured(state.GetString("sessionId")) && state.GetBool("isStreaming") != true) ScheduleContextRefresh();
            ObserveAdvisorContext();
            if (modelChanged) _advisors.Refresh();
            _backgroundAgents.SetSession(_workingDirectory, state.GetString("sessionId") ?? "");
            if (Volatile.Read(ref _workspaceSwitching) == 0) _isolationResults?.SetContext(_workingDirectory, state.GetString("sessionId") ?? "");
            _interactiveTerminal.SetSession(_workingDirectory, state.GetString("sessionId") ?? "");
            _interactiveTerminal.ProbeAsync(_disposeCts.Token).Forget(_logger, "terminal capability");
            _projectHooks?.SetSession(_workingDirectory, state.GetString("sessionId") ?? "");
            if (!changed)
            {
                return true;
            }

            _ui.Post(UiMessageTypes.SessionInfo, updated, UiDelivery.State);
            // The engine setup shows the model and the thinking level (and re-reads the levels a model offers).
            // Provider publication reads the host state under its own publication lock. Do not invert
            // that order when recovery applies a snapshot while holding the host state lock.
            if (modelChanged || thinkingChanged)
                Task.Run(() => _providerSetup.SessionInfoChanged(modelChanged)).Forget(_logger, "provider session refresh");
            ComputerSessionObserved(updated.SessionId);
            ResourceSessionObserved(updated.SessionId);
            ConversationObserved(updated.SessionId);
            KnowledgeSessionObserved(updated.SessionId);
            if (!updated.IsStreaming) ScheduleQueueDispatch();
            return true;
        }

        /// <summary>Refreshes <c>session/info</c> from <c>get_state</c> (coalesced: bursts of triggers cause one extra refresh at most).</summary>
        private void RequestInfoRefresh()
        {
            lock (_stateLock)
            {
                if (_refreshRunning)
                {
                    _refreshQueued = true;
                    return;
                }

                _refreshRunning = true;
            }

            Task.Run(RefreshLoopAsync).Forget(_logger, "session info refresh");
        }

        private async Task RefreshLoopAsync()
        {
            while (true)
            {
                try
                {
                    if (_bridge.State == BridgeState.Running && Volatile.Read(ref _disposed) == 0)
                    {
                        long revision; OmpConnection? connection; CancellationToken token;
                        lock (_stateLock)
                        {
                            revision = _streamingRevision; connection = _epochConnection; token = _epoch.Token;
                        }

                        if (connection != null)
                        {
                            var response = await connection.SendCommandAsync(OmpCommands.GetState(), TimeSpan.FromSeconds(15), token).ConfigureAwait(false);
                            if (response.Success && response.Data is JObject state)
                                ApplyState(state, revision, currentLocked: () => !token.IsCancellationRequested && ReferenceEquals(connection, _epochConnection));
                        }
                    }
                }
                catch (Exception ex)
                {
                    _logger.Debug("Refreshing the session info failed: " + ex.Message);
                }

                lock (_stateLock)
                {
                    if (!_refreshQueued)
                    {
                        _refreshRunning = false;
                        return;
                    }

                    _refreshQueued = false;
                }
            }
        }

        private static SessionInfoPayload CloneInfo(SessionInfoPayload source) => new SessionInfoPayload
        {
            RecoveryStatus = source.RecoveryStatus,
            RecoveryMessage = source.RecoveryMessage,
            Model = source.Model,
            ThinkingLevel = source.ThinkingLevel,
            SessionId = source.SessionId,
            SessionName = source.SessionName,
            TodoPhases = source.TodoPhases, PlanTruncated = source.PlanTruncated, HasPendingAsyncWork = source.HasPendingAsyncWork,
            PlanTaskCount = source.PlanTaskCount, PlanOpenCount = source.PlanOpenCount, PlanRevision = source.PlanRevision,
            ContextUsage = source.ContextUsage,
            ThreeTurnCompaction = source.ThreeTurnCompaction,
            IsStreaming = source.IsStreaming,
            WorkingDirectory = source.WorkingDirectory,
            FastMode = source.FastMode,
        };

        /// <summary>
        /// Whether omp's fast mode (/fast, set_fast_mode) can switch this model: its provider family has a priority service tier.
        /// Mirrors omp 18.8's service-tier families (openai, anthropic, google, and openrouter models of those classes). A Codex
        /// model whose discovered tiers lack "priority" has no fast mode, and Fireworks and GitHub Copilot are never inferred as
        /// openai-class. An unknown shape says no; omp still has the last word (set_fast_mode fails for a model without a tier).
        /// </summary>
        internal static bool HasServiceTierFamily(JObject model)
        {
            var provider = model.GetString("provider") ?? string.Empty;
            var api = model.GetString("api") ?? string.Empty;
            if (api == "openai-codex-responses" && model["serviceTiers"] is JArray tiers && tiers.Count > 0
                && !tiers.Any(tier => tier.Type == JTokenType.String && (string?)tier == "priority"))
                return false;
            if (provider == "fireworks" || provider == "github-copilot") return api == "anthropic-messages";
            var identityClass = model.GetObject("identity")?.GetString("class");
            switch (provider)
            {
                case "openai":
                case "openai-codex":
                case "google":
                case "google-vertex":
                    return true;
                case "openrouter":
                    return identityClass == "anthropic" || identityClass == "gemini" || identityClass == "openai";
            }

            return api == "anthropic-messages" || (identityClass == "openai" && api.StartsWith("openai", StringComparison.Ordinal));
        }

        private HostHelloPayload BuildHello() => new HostHelloPayload
        {
            Version = _environment.HostVersion ?? string.Empty,
            VsVersion = _environment.VsVersion,
            Locale = PompLanguage.Locale,
            IncludeEditorContextByDefault = _settings.IncludeEditorContextByDefault,
            ComposerPredictions = _settings.ComposerPredictions,
            ApprovalMode = _settings.ApprovalMode,
            Shortcuts = (_settings as IUiShortcutSettings)?.Shortcuts ?? new UiShortcutsPayload(),
        };

        /// <summary>A host/hello that is about to be sent: remembers its JSON, so a later identical one is not sent again.</summary>
        private HostHelloPayload BuildHelloToSend()
        {
            var hello = BuildHello();
            var json = PompJson.Serialize(hello);
            lock (_helloLock)
            {
                _lastHelloJson = json;
            }

            return hello;
        }

        /// <summary>The language or another setting was changed in the options: the page gets a new host/hello when it differs from the last one sent.</summary>
        private void OnHelloSettingsChanged(object? sender, EventArgs e)
        {
            if (!_settings.ComposerPredictions) CancelComposerPrediction();
            _interactiveTerminal.Publish();
            _interactiveTerminal.ProbeAsync(_disposeCts.Token).Forget(_logger, "terminal capability");
            PostHelloIfChanged();
        }

        private void PostHelloIfChanged()
        {
            if (Volatile.Read(ref _disposed) != 0)
            {
                return;
            }

            var hello = BuildHello();
            var json = PompJson.Serialize(hello);
            lock (_helloLock)
            {
                if (string.Equals(json, _lastHelloJson, StringComparison.Ordinal))
                {
                    return;
                }

                _lastHelloJson = json;
            }

            _ui.Post(UiMessageTypes.HostHello, hello, UiDelivery.State);
        }

        /// <summary>The messages sent to a page that just became ready: host/hello, the current bridge state and session info.</summary>
        private IEnumerable<UiMessage> BuildSnapshot()
        {
            BridgeStatePayload bridge;
            SessionInfoPayload info;
            EngineInfoPayload? engine;
            lock (_stateLock)
            {
                bridge = _bridgeState;
                info = _info;
                engine = _engineInfo;
            }

            return new[]
            {
                new UiMessage(UiMessageTypes.HostHello, BuildHelloToSend()),
                new UiMessage(UiMessageTypes.BridgeState, bridge),
                new UiMessage(UiMessageTypes.SessionInfo, info),
                new UiMessage(UiMessageTypes.CommandsState, CommandsSnapshot()),
            }.Concat(engine != null ? new[] { new UiMessage(UiMessageTypes.EngineInfo, engine) } : Array.Empty<UiMessage>())
                .Append(new UiMessage(UiMessageTypes.ToolsState, ToolsView()))
                .Append(new UiMessage(UiMessageTypes.SkillsState, SkillsView()))
                .Append(new UiMessage(UiMessageTypes.McpState, McpView()))
                .Append(new UiMessage(UiMessageTypes.CatalogState, CatalogView()))
                .Append(new UiMessage(UiMessageTypes.RulesState, RulesView()))
                .Append(new UiMessage(UiMessageTypes.RulesActive, ActiveRulesView()))
                .Append(new UiMessage(UiMessageTypes.WorkflowsState, WorkflowsView()))
                .Append(new UiMessage(UiMessageTypes.HooksState, HooksView()))
                .Append(ComputerStateMessage())
                .Append(AgentsStateMessage())
                .Append(new UiMessage(UiMessageTypes.ConversationPartnerState, PartnerView()))
                .Append(new UiMessage(UiMessageTypes.ChatQueueState, QueueView()))
                .Append(new UiMessage(UiMessageTypes.AgentsHistoryState, RunHistory()?.View() ?? new AgentsHistoryStatePayload()))
                .Concat(GoalRequestSnapshot())
                .Concat(TakePendingUiRequests());
        }

        // ---- notices -------------------------------------------------------------------------------------------------------

        private void PostNotice(string level, string message, string? format = null)
        {
            message = StripTerminalEscapes(message);
            if (string.IsNullOrWhiteSpace(message))
            {
                return;
            }

            _ui.Post(UiMessageTypes.ChatNotice, new ChatNoticePayload { Level = level, Message = message, Format = format });
        }

        private static readonly System.Text.RegularExpressions.Regex TerminalEscape = new System.Text.RegularExpressions.Regex(
            @"\x1B(?:\[[0-?]*[ -/]*[@-~]|\][^\x07\x1B]*(?:\x07|\x1B\\)|[@-Z\\-_])", System.Text.RegularExpressions.RegexOptions.CultureInvariant);

        /// <summary>omp formats command output for a terminal: colour/cursor escape sequences mean nothing in the chat page.</summary>
        internal static string StripTerminalEscapes(string text) =>
            text.IndexOf('\u001B') < 0 ? text : TerminalEscape.Replace(text, string.Empty);

        /// <summary>
        /// Informational notices about omp's own plumbing (URI-scheme mounts of the host tools at start-up) that mean nothing to
        /// the user; they go to the log. Warnings and errors are always shown.
        /// </summary>
        internal static bool IsEngineHousekeepingNotice(string level, string message) =>
            level == "info" && System.Text.RegularExpressions.Regex.IsMatch(message, @"\A[a-z][a-z0-9+.-]{0,31}://: (?:unmounted|mounted|mounting|unmounting) ", System.Text.RegularExpressions.RegexOptions.CultureInvariant);

        private static string NormalizeLevel(string? level)
        {
            switch (level)
            {
                case "warning":
                case "warn":
                    return "warning";
                case "error":
                    return "error";
                default:
                    return "info";
            }
        }

        private void HandleExtensionError(OmpFrame frame)
        {
            var frameData = frame.As<ExtensionErrorFrame>();
            string? name = null;
            if (!string.IsNullOrEmpty(frameData.ExtensionPath))
            {
                try
                {
                    name = Path.GetFileName(frameData.ExtensionPath!.TrimEnd('\\', '/'));
                }
                catch (ArgumentException)
                {
                    name = frameData.ExtensionPath;
                }
            }

            var message = "omp extension" + (string.IsNullOrEmpty(name) ? string.Empty : " '" + name + "'")
                + (string.IsNullOrEmpty(frameData.Event) ? string.Empty : " (" + frameData.Event + ")")
                + " failed: " + (frameData.Error ?? "unknown error");
            _logger.Warn(message);
            var pageMessage = PompLanguage.Pick(
                "확장" + (string.IsNullOrEmpty(name) ? string.Empty : " '" + name + "'")
                + (string.IsNullOrEmpty(frameData.Event) ? string.Empty : " (" + frameData.Event + ")")
                + "에서 오류가 발생했어요: " + (frameData.Error ?? "알 수 없는 오류"),
                "Extension" + (string.IsNullOrEmpty(name) ? string.Empty : " '" + name + "'")
                + (string.IsNullOrEmpty(frameData.Event) ? string.Empty : " (" + frameData.Event + ")")
                + " failed: " + (frameData.Error ?? "unknown error"));
            PostNotice("warning", pageMessage);
        }

        // ---- host tools / host URIs ---------------------------------------------------------------------------------------

        private void HandleHostToolCall(OmpFrame frame)
        {
            var origin = _bridge.OriginOf(frame);
            HostToolCallFrame call;
            try
            {
                call = frame.As<HostToolCallFrame>();
            }
            catch (Exception ex) when (ex is Newtonsoft.Json.JsonException || ex is InvalidCastException)
            {
                // Never leave omp waiting for a result that cannot be produced.
                _logger.Warn("Malformed host_tool_call frame: " + ex.Message);
                var id = frame.Json.GetString("id");
                if (!string.IsNullOrEmpty(id))
                {
                    SendFrameQuietlyAsync(OmpCommands.HostToolResult(id!, HostToolResult.Error("The host_tool_call frame was malformed: " + ex.Message)), origin)
                        .Forget(_logger, "host_tool_result");
                }

                return;
            }

            if (origin != null && origin.IsClosed)
            {
                // A frame still buffered from an omp process that has ended: its result could never be delivered, and a tool must
                // not run (or ask for approval) on behalf of a process that is gone.
                _logger.Debug("Ignoring host_tool_call '" + call.ToolName + "' of an omp process that has ended.");
                return;
            }

            Func<JObject, CancellationToken, Task>? reply = null;
            if (origin != null)
            {
                reply = (result, token) => origin.SendFrameAsync(result, token);
            }

            _dispatcher.HandleCallAsync(call, origin, reply).Forget(_logger, "host tool '" + call.ToolName + "'");
        }

        /// <summary>pomp registers no host URI schemes, so every request is answered with an error (omp never asks in practice).</summary>
        private void HandleHostUriRequest(OmpFrame frame)
        {
            var id = frame.Json.GetString("id");
            if (string.IsNullOrEmpty(id))
            {
                return;
            }

            var reply = new JObject
            {
                ["type"] = OmpCommandTypes.HostUriResult,
                ["id"] = id,
                ["isError"] = true,
                ["error"] = "pomp does not register any tool URI schemes.",
            };
            SendFrameQuietlyAsync(reply, _bridge.OriginOf(frame)).Forget(_logger, "host_uri_result");
        }

        /// <summary>
        /// Sends a reply to a request of omp. With an <paramref name="origin"/> it goes to that omp process only: once that process has
        /// ended the reply is dropped rather than reaching a restarted process, which may even reuse the request id.
        /// </summary>
        private async Task SendFrameQuietlyAsync(JObject frame, Pomp.Bridge.OmpConnection? origin)
        {
            try
            {
                if (origin != null)
                {
                    await origin.SendFrameAsync(frame, _disposeCts.Token).ConfigureAwait(false);
                }
                else
                {
                    await _bridge.SendFrameAsync(frame, _disposeCts.Token).ConfigureAwait(false);
                }
            }
            catch (Exception ex) when (!(ex is OperationCanceledException))
            {
                _logger.Debug("Could not send '" + ((string?)frame["type"] ?? "frame") + "' to omp: " + ex.Message);
            }
        }

        // ---- extension UI (dialogs and tool approvals) ----------------------------------------------------------------------

        private void HandleExtensionUiRequest(OmpFrame frame)
        {
            var origin = _bridge.OriginOf(frame);
            ExtensionUiRequestFrame request;
            try
            {
                request = frame.As<ExtensionUiRequestFrame>();
            }
            catch (Exception ex) when (ex is Newtonsoft.Json.JsonException || ex is InvalidCastException)
            {
                // A dialog we cannot show would leave omp waiting: answer it as cancelled.
                _logger.Warn("Malformed extension_ui_request frame: " + ex.Message);
                var id = frame.Json.GetString("id");
                var method = frame.Json.GetString("method");
                if (!string.IsNullOrEmpty(id)
                    && (method == OmpUiMethods.Select || method == OmpUiMethods.Confirm || method == OmpUiMethods.Input || method == OmpUiMethods.Editor))
                {
                    SendFrameQuietlyAsync(OmpCommands.ExtensionUiCancelled(id!), origin).Forget(_logger, "extension_ui_response");
                }

                return;
            }

            switch (request.Method)
            {
                case OmpUiMethods.Select:
                case OmpUiMethods.Confirm:
                case OmpUiMethods.Input:
                case OmpUiMethods.Editor:
                    if (origin != null && origin.IsClosed)
                    {
                        // Asked by an omp process that has ended: no answer can reach it, so the user is not asked.
                        return;
                    }

                    var permission = OmpApprovalMapper.TryMap(request);
                    if (permission != null)
                    {
                        if (permission.ToolName == "task")
                        {
                            lock (_stateLock) permission.Tasks = _taskApprovalDetails.Match(_info.SessionId ?? "", request.Title ?? "");
                        }
                        HandleApprovalAsync(request, permission, origin).Forget(_logger, "omp approval '" + permission.ToolName + "'");
                    }
                    else
                    {
                        HandleDialogAsync(request, frame.Json, origin).Forget(_logger, "omp dialog '" + request.Method + "'");
                    }

                    break;
                case OmpUiMethods.Cancel:
                    CancelOmpRequest(frame.Json.GetString("targetId"), origin);
                    break;
                case OmpUiMethods.Notify:
                    if (IsEngineHousekeepingNotice(NormalizeLevel(frame.Json.GetString("notifyType")), request.Message ?? "")) break;
                    if (HandleTaskDetailsNotification(request.Message, origin) || HandleActiveRulesNotification(request.Message, origin) || HandleToolsNotification(request.Message, origin) || HandleInlineNotification(request.Message, origin) || HandleMemberStopNotification(request.Message, origin) || HandleAdvisorNotification(request.Message, origin) || HandleReviewReporterNotification(request.Message, origin)) break;
                    PostNotice(NormalizeLevel(frame.Json.GetString("notifyType")), request.Message ?? string.Empty);
                    break;
                default:
                    // setStatus, setWidget, setTitle, set_editor_text, open_url: presentation only, no place for them in the chat UI.
                    _logger.Debug("Ignoring the omp UI method '" + request.Method + "'.");
                    break;
            }
        }

        /// <summary>omp's tool-approval prompt: ask the gate (which may answer from its rules) and reply Approve/Deny.</summary>
        private async Task HandleApprovalAsync(ExtensionUiRequestFrame request, PermissionRequest permission, Pomp.Bridge.OmpConnection? origin)
        {
            using (var cts = CancellationTokenSource.CreateLinkedTokenSource(CurrentEpochToken(), _disposeCts.Token))
            {
                var state = RegisterOmpRequest(request.Id, cts, origin);
                PermissionResponse response;
                try
                {
                    var workflowBlock = WorkspaceSwitchBlock() ?? Pomp.Features.ProjectExecutionPolicy.CheckHostPermission(_workingDirectory, permission.Kind) ?? _composerWorkflow?.CheckApproval(permission.Kind);
                    response = workflowBlock == null ? await _gate.RequestAsync(permission, cts.Token).ConfigureAwait(false) : PermissionResponse.DeniedBySystem(workflowBlock);
                    workflowBlock = WorkspaceSwitchBlock() ?? Pomp.Features.ProjectExecutionPolicy.CheckHostPermission(_workingDirectory, permission.Kind) ?? _composerWorkflow?.CheckApproval(permission.Kind);
                    if (response.IsAllowed && workflowBlock != null) response = PermissionResponse.DeniedBySystem(workflowBlock);
                }
                finally
                {
                    UnregisterOmpRequest(request.Id, state);
                }

                if (state.CancelledByOmp)
                {
                    // omp withdrew the question: no answer is expected.
                    return;
                }

                if (!response.IsAllowed && response.SystemReason != null)
                {
                    _logger.Info("'" + permission.ToolName + "' was denied without an answer from the user: " + response.SystemReason);
                }
                else if (!response.IsAllowed && !string.IsNullOrWhiteSpace(response.Note))
                {
                    _logger.Info("The user denied '" + permission.ToolName + "' with the note: " + response.Note);
                }

                var value = response.IsAllowed ? OmpApprovalPrompt.Approve : OmpApprovalPrompt.Deny;
                await SendFrameQuietlyAsync(OmpCommands.ExtensionUiValue(request.Id, value), origin).ConfigureAwait(false);
            }
        }

        /// <summary>A select/confirm/input/editor dialog of an omp extension: show it in the UI and forward the answer.</summary>
        private async Task HandleDialogAsync(ExtensionUiRequestFrame request, JObject json, Pomp.Bridge.OmpConnection? origin)
        {
            if (request.Timeout is int expired && expired <= 0)
            {
                // omp times such a dialog out at once (a zero-delay timer) and forgets it without a cancel: nothing to show or answer.
                return;
            }

            var payload = new DialogRequestPayload
            {
                RequestId = request.Id,
                Method = request.Method,
                Title = request.Title,
                Message = request.Message,
                Options = request.Options,
                Placeholder = request.Placeholder,
                DefaultValue = json.GetString("prefill") ?? json.GetString("defaultValue"),
                TimeoutMs = request.Timeout,
            };

            using (var cts = CancellationTokenSource.CreateLinkedTokenSource(CurrentEpochToken(), _disposeCts.Token))
            {
                var timeout = request.Timeout is int ms && ms > 0 ? TimeSpan.FromMilliseconds(ms) : (TimeSpan?)null;
                if (timeout.HasValue)
                {
                    cts.CancelAfter(timeout.Value);
                }

                var state = RegisterOmpRequest(request.Id, cts, origin);
                var started = Stopwatch.StartNew();
                DialogAnswer answer;
                var timedOut = false;
                try
                {
                    answer = await _ui.RequestDialogAsync(payload, cts.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    timedOut = timeout.HasValue && started.Elapsed >= timeout.Value - TimeSpan.FromMilliseconds(50) && !state.CancelledByOmp;
                    answer = new DialogAnswer { Cancelled = true };
                }
                finally
                {
                    UnregisterOmpRequest(request.Id, state);
                }

                if (state.CancelledByOmp)
                {
                    return;
                }

                JObject reply;
                if (answer.Cancelled)
                {
                    reply = OmpCommands.ExtensionUiCancelled(request.Id, timedOut);
                }
                else if (request.Method == OmpUiMethods.Confirm)
                {
                    reply = OmpCommands.ExtensionUiConfirmed(request.Id, answer.Confirmed ?? false);
                }
                else
                {
                    reply = OmpCommands.ExtensionUiValue(request.Id, answer.Value ?? string.Empty);
                }

                await SendFrameQuietlyAsync(reply, origin).ConfigureAwait(false);
            }
        }

        private OmpRequestState RegisterOmpRequest(string id, CancellationTokenSource cts, Pomp.Bridge.OmpConnection? origin)
        {
            var state = new OmpRequestState(cts, origin);
            lock (_stateLock)
            {
                _ompRequests[id] = state;
            }

            return state;
        }

        private void UnregisterOmpRequest(string id, OmpRequestState state)
        {
            lock (_stateLock)
            {
                // A restarted omp may reuse the id while this request unwinds: remove only this request's own entry.
                if (_ompRequests.TryGetValue(id, out var current) && ReferenceEquals(current, state))
                {
                    _ompRequests.Remove(id);
                }
            }
        }

        /// <summary>omp cancelled a pending dialog (<c>extension_ui_request</c> with method <c>cancel</c>): close it in the UI, send no answer.</summary>
        private void CancelOmpRequest(string? targetId, Pomp.Bridge.OmpConnection? origin)
        {
            if (string.IsNullOrEmpty(targetId))
            {
                return;
            }

            OmpRequestState? state;
            lock (_stateLock)
            {
                _ompRequests.TryGetValue(targetId!, out state);
            }

            if (state == null || (origin != null && state.Origin != null && !ReferenceEquals(origin, state.Origin)))
            {
                return;
            }

            state.CancelledByOmp = true;
            Cancel(state.Cts);
        }

        /// <summary>The user pressed Stop: dialogs omp still waits for are answered as cancelled/denied so its tool calls unwind.</summary>
        private void CancelPendingOmpRequests()
        {
            List<OmpRequestState> pending;
            lock (_stateLock)
            {
                pending = _ompRequests.Values.ToList();
            }

            foreach (var state in pending)
            {
                Cancel(state.Cts);
            }
        }

        // ---- state records -----------------------------------------------------------------------------------------------

        private sealed class TurnState
        {
            public TurnState(string promptId, string? clientMessageId)
            {
                PromptId = promptId;
                ClientMessageId = clientMessageId;
            }

            public string PromptId { get; }

            public string? ClientMessageId { get; }
            public Pomp.Features.AgentRunHistory? History { get; set; }
            public string? RunId { get; set; }
            public AgentRunTargetPayload? FeedbackTarget { get; set; }
            public string? PartnerAgent { get; set; }
            public bool PartnerJobCaptured { get; set; }
            public string? Team { get; set; }
            public HashSet<string>? TeamMembers { get; set; }
            public OmpConnection? TeamConnection { get; set; }

            /// <summary>Running since the turn was created (the turn-duration metric).</summary>
            public System.Diagnostics.Stopwatch Elapsed { get; } = System.Diagnostics.Stopwatch.StartNew();

            /// <summary>Set (under the state lock) once chat/turnStarted was posted; until then an end is kept in <see cref="PendingEnd"/>.</summary>
            public bool StartPosted { get; set; }

            /// <summary>An end that happened before the start was posted; the sender posts it right after the start.</summary>
            public ChatTurnEndPayload? PendingEnd { get; set; }
        }

        private sealed class AssistantMessageState
        {
            public bool SawDelta { get; set; }
        }

        private sealed class ToolState
        {
            private readonly Stopwatch _stopwatch = Stopwatch.StartNew();

            public ToolState(string name)
            {
                Name = name;
            }

            public string Name { get; }

            public long ElapsedMilliseconds => _stopwatch.ElapsedMilliseconds;
        }

        private sealed class OmpRequestState
        {
            public OmpRequestState(CancellationTokenSource cts, Pomp.Bridge.OmpConnection? origin)
            {
                Cts = cts;
                Origin = origin;
            }

            public CancellationTokenSource Cts { get; }

            /// <summary>The omp process that asked; only its own <c>cancel</c> may withdraw the request.</summary>
            public Pomp.Bridge.OmpConnection? Origin { get; }

            public volatile bool CancelledByOmp;
        }
    }
}
