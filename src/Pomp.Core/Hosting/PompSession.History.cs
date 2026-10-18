using System;
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
        private string _recordedSession = "";
        private string? _sessionFile;
        private string _sessionFolderRoot = "";
        private volatile bool _sessionSwitchUncertain;
        private string _uncertainSessionPath = "";
        private OmpConnection? _uncertainSessionConnection;

        /// <summary>The start of omp's error when none of a session's saved models resolves any more (omp 18.6.3 and later).</summary>
        private const string ModelNotRestoredPrefix = "Could not restore model ";

        /// <summary>
        /// switch_session to <paramref name="path"/>. omp 18.6.3 refuses a session whose saved models it cannot resolve (removed from the
        /// catalog, provider signed out); then the switch is sent again bound to the model the engine uses now, and a notice says so. The
        /// engine is asked for that model: the host's copy can still name the unavailable one (a restart right after signing out of its
        /// provider), and binding to it fails again with "Model not found".
        /// </summary>
        private async Task<ResponseFrame> SwitchSessionAsync(OmpConnection connection, string path, CancellationToken token)
        {
            var response = await connection.SendCommandAsync(new JObject { ["type"] = "switch_session", ["sessionPath"] = path }, TimeSpan.FromSeconds(30), token).ConfigureAwait(false);
            var error = response.Success ? null : response.Error;
            if (error == null || !error.StartsWith(ModelNotRestoredPrefix, StringComparison.Ordinal)) return response;
            var saved = error.Substring(ModelNotRestoredPrefix.Length).Trim();
            var cut = 0;
            while (cut < saved.Length && !char.IsWhiteSpace(saved[cut]) && saved[cut] != ',') cut++;
            saved = saved.Substring(0, cut).TrimEnd('.');
            var model = await EngineModelAsync(connection, token).ConfigureAwait(false);
            if (model == null) { lock (_stateLock) model = _info.Model; }
            if (model == null || string.IsNullOrEmpty(model.Provider) || string.IsNullOrEmpty(model.Id)) return response;
            var retry = await connection.SendCommandAsync(new JObject { ["type"] = "switch_session", ["sessionPath"] = path, ["provider"] = model.Provider, ["modelId"] = model.Id },
                TimeSpan.FromSeconds(30), token).ConfigureAwait(false);
            if (retry.Success && retry.Data.GetBool("cancelled") != true)
            {
                var current = model.Provider + "/" + model.Id;
                PostNotice("info", saved.Length > 0 && saved.Length <= 200
                    ? PompLanguage.Pick($"저장된 모델 {saved}을(를) 쓸 수 없어 {current}(으)로 열었어요.", $"The saved model {saved} is unavailable; the conversation uses {current}.")
                    : PompLanguage.Pick($"저장된 모델을 쓸 수 없어 {current}(으)로 열었어요.", $"The saved model is unavailable; the conversation uses {current}."));
            }
            return retry;
        }

        /// <summary>The model the engine uses now (get_state), or null when it cannot be read.</summary>
        private static async Task<ModelInfoPayload?> EngineModelAsync(OmpConnection connection, CancellationToken token)
        {
            try
            {
                var reply = await connection.SendCommandAsync(OmpCommands.GetState(), TimeSpan.FromSeconds(10), token).ConfigureAwait(false);
                if (!reply.Success || reply.Data is not JObject state || state["model"] is not JObject model) return null;
                var provider = model.GetString("provider"); var id = model.GetString("id");
                return string.IsNullOrEmpty(provider) || string.IsNullOrEmpty(id) ? null : new ModelInfoPayload { Provider = provider!, Id = id! };
            }
            catch (Exception ex) when (!(ex is OperationCanceledException)) { return null; }
        }

        private void RememberSession(JObject state)
        {
            string root;
            lock (_stateLock) root = _workingDirectory;
            var path = state.GetString("sessionFile"); var id = state.GetString("sessionId"); var name = state.GetString("sessionName") ?? "";
            if (string.IsNullOrEmpty(root) || string.IsNullOrEmpty(path) || string.IsNullOrEmpty(id)) return;
            var key = root + "\n" + path + "\n" + name;
            lock (_stateLock) if (_recordedSession == key) return;
            _sessionReferences.Enqueue(() => Task.Run(() => {
                try { if (new RecentSessionStore(_environment.DataDirectory, root).Remember(id!, path!, name)) { lock (_stateLock) _recordedSession = key; } }
                catch (Exception ex) { _logger.Warn("Recent session reference was not saved: " + ex.Message); }
            }));
        }

        private async Task HandleRecentSessionsAsync(JObject args)
        {
            var requestId = args.GetString("requestId"); var action = args.GetString("action") ?? "refresh"; var switched = false; string? pageSession = null;
            string knownRoot; lock (_stateLock) knownRoot = _workingDirectory;
            var answer = new JObject { ["requestId"] = requestId, ["root"] = knownRoot, ["entries"] = new JArray() };
            RecentSessionStore? store = null;
            try
            {
                if (action != "refresh" && action != "switch" && action != "messages" && action != "preview" && action != "rename" && action != "compact" && action != "delete") throw new ArgumentException(PompLanguage.Pick("알 수 없는 세션 기록 명령이에요.", "Unknown session-history command."));
                string? name = null; string? instructions = null;
                if (action == "rename")
                {
                    name = args.GetString("name");
                    if (name == null || name.Any(char.IsControl) || (name = name.Trim()).Length < 1 || name.Length > 120) throw new ArgumentException(PompLanguage.Pick("세션 이름이 올바르지 않아요.", "The session name is invalid."));
                }
                if (action == "compact")
                {
                    if (args["instructions"] != null && args["instructions"]!.Type != JTokenType.Null && args["instructions"]!.Type != JTokenType.String) throw new ArgumentException(PompLanguage.Pick("컨텍스트 압축 지침 형식이 올바르지 않아요.", "The compaction instructions are invalid."));
                    instructions = args.GetString("instructions");
                    if (instructions != null && instructions.Length > 2000) throw new ArgumentException(PompLanguage.Pick("컨텍스트 압축 지침이 너무 길어요.", "The compaction instructions are too long."));
                }
                var root = await _environment.GetWorkspaceRootAsync(_disposeCts.Token).ConfigureAwait(false);
                store = new RecentSessionStore(_environment.DataDirectory, root);
                answer["root"] = root;
                bool engineWorkspaceMatches;
                lock (_stateLock)
                {
                    engineWorkspaceMatches = string.Equals(NormalizePath(root), NormalizePath(_workingDirectory), StringComparison.OrdinalIgnoreCase);
                    if (!string.Equals(NormalizePath(root), NormalizePath(_sessionFolderRoot), StringComparison.OrdinalIgnoreCase))
                    {
                        _sessionFile = null; _sessionFolderRoot = "";
                    }
                }
                if (!engineWorkspaceMatches && action != "refresh" && action != "preview") throw new InvalidOperationException(PompLanguage.Pick("작업 영역이 바뀌었어요.", "Workspace changed."));
                OmpConnection? connection; CancellationToken epoch;
                lock (_stateLock) { connection = _epochConnection; epoch = _epoch.Token; }
                using var token = CancellationTokenSource.CreateLinkedTokenSource(epoch, _disposeCts.Token);
                if (_recoveryStatus == "restoring") throw new InvalidOperationException(PompLanguage.Pick("대화를 복원하고 있어요.", "Conversation recovery is in progress."));
                if (_recoveryStatus == "failed" && action != "refresh" && action != "preview" && action != "switch") throw new InvalidOperationException(PompLanguage.Pick("대화가 복원되지 않았어요.", "The conversation is not recovered."));
                if (_sessionSwitchUncertain && _recoveryStatus == "ready" && action != "preview")
                {
                    if (connection == null || _bridge.State != BridgeState.Running) throw new InvalidOperationException(PompLanguage.Pick("POMP가 연결되지 않았어요.", "POMP is not connected."));
                    var recovered = await connection.SendCommandAsync(OmpCommands.GetState(), TimeSpan.FromSeconds(10), token.Token).ConfigureAwait(false);
                    if (!recovered.Success || !(recovered.Data is JObject recoveredState)) throw new InvalidOperationException(PompLanguage.Pick("활성 세션이 아직 확실하지 않아요.", "The active session is still uncertain."));
                    // Judge the switch only once the engine is settled: right after it, omp may still be loading or draining work.
                    recoveredState = await WaitForSettledStateAsync(connection, recoveredState, token.Token, "Session switch check").ConfigureAwait(false);
                    if (recoveredState.GetBool("isSettled") != true || (ReferenceEquals(_uncertainSessionConnection, connection) && !string.Equals(recoveredState.GetString("sessionFile"), _uncertainSessionPath, StringComparison.OrdinalIgnoreCase)))
                        throw new InvalidOperationException(PompLanguage.Pick("요청한 세션 전환은 확인되지 않았어요.", "The requested session switch is not confirmed."));
                    token.Token.ThrowIfCancellationRequested();
                    if (!ApplyState(recoveredState, currentLocked: () => ReferenceEquals(connection, _epochConnection), confirmSession: true))
                        throw new OperationCanceledException(token.Token);
                    _gate.ResetSession(); _ui.ClearTranscript();
                    _ui.Post(UiMessageTypes.SessionNewResult, new SessionNewResultPayload { Status = "completed" });
                    try { await RestoreTranscriptAsync(connection, token.Token).ConfigureAwait(false); }
                    catch (Exception ex) when (!(ex is OperationCanceledException)) { _logger.Warn("Could not restore the reconciled session's transcript: " + ex.Message); }
                }
                if (action != "preview" && engineWorkspaceMatches && _bridge.State == BridgeState.Running && connection != null)
                {
                    long revision; lock (_stateLock) revision = _streamingRevision;
                    var latest = await connection.SendCommandAsync(OmpCommands.GetState(), TimeSpan.FromSeconds(10), token.Token).ConfigureAwait(false);
                    if (!latest.Success || !(latest.Data is JObject latestState)) throw new InvalidOperationException(PompLanguage.Pick("현재 세션 상태를 확인할 수 없어요.", "Cannot establish the current session state."));
                    token.Token.ThrowIfCancellationRequested();
                    // A reply of a connection that was replaced meanwhile must not overwrite the new connection's state.
                    if (!ApplyState(latestState, revision, currentLocked: () => ReferenceEquals(connection, _epochConnection)) && !ReferenceEquals(connection, _epochConnection))
                        throw new OperationCanceledException(token.Token);
                    pageSession = latestState.GetString("sessionId");
                }
                else if (action != "refresh" && action != "preview") throw new InvalidOperationException(PompLanguage.Pick("POMP가 연결되지 않았어요.", "POMP is not connected."));

                if (action == "switch" || action == "messages" || action == "compact" || action == "delete")
                {
                    if (action == "messages" && args.GetString("sessionId") != pageSession) throw new InvalidOperationException(PompLanguage.Pick("세션이 바뀌었어요.", "Session changed."));
                    lock (_stateLock) if (_info.IsStreaming || _info.HasPendingAsyncWork || _turns.Count > 0) throw new InvalidOperationException(PompLanguage.Pick("현재 세션이나 백그라운드 작업이 진행 중이에요.", "The current session or background work is still running."));
                    if (_ui.PendingRequestCount > 0) throw new InvalidOperationException(PompLanguage.Pick("대기 중인 승인이나 질문이 있어요.", "An approval or question is pending."));
                    if (_bridge.State != BridgeState.Running) throw new InvalidOperationException(PompLanguage.Pick("POMP가 연결되지 않았어요.", "POMP is not connected."));
                }
                if (action == "switch")
                {
                    if (_terminalJobs.HasUnfinishedWork || _interactiveTerminal.HasUnfinishedWork) throw new InvalidOperationException(PompLanguage.Pick("실행 중인 터미널 작업이 있어요.", "A terminal job is still running."));
                    string? activeFile; lock (_stateLock) activeFile = _sessionFile;
                    var path = await Task.Run(() => store.Resolve(args.GetString("root") ?? "", args.GetString("id") ?? "", activeFile), token.Token).ConfigureAwait(false);
                    _uncertainSessionPath = path; _uncertainSessionConnection = connection; _sessionSwitchUncertain = true;
                    var response = await SwitchSessionAsync(connection!, path, token.Token).ConfigureAwait(false);
                    if (!response.Success || response.Data.GetBool("cancelled") == true) { _sessionSwitchUncertain = _recoveryStatus != "ready"; throw new InvalidOperationException(response.Error ?? PompLanguage.Pick("세션 전환이 취소됐어요.", "The session switch was cancelled.")); }
                    token.Token.ThrowIfCancellationRequested();
                    var current = await connection!.SendCommandAsync(OmpCommands.GetState(), TimeSpan.FromSeconds(10), token.Token).ConfigureAwait(false);
                    if (!current.Success || current.Data is not JObject currentState || !SameSessionPath(currentState.GetString("sessionFile"), path) || string.IsNullOrEmpty(currentState.GetString("sessionId")))
                        throw new InvalidOperationException(PompLanguage.Pick("선택한 세션 상태를 확인하지 못했어요.", "Could not confirm the selected session state."));
                    token.Token.ThrowIfCancellationRequested();
                    if (!ApplyState(currentState, acceptRecovery: true, currentLocked: () => ReferenceEquals(connection, _epochConnection), confirmSession: true))
                        throw new OperationCanceledException(token.Token);
                    pageSession = currentState.GetString("sessionId");
                    lock (_stateLock) _recordedSession = "";
                    switched = true; _gate.ResetSession(); EndAllTurns("aborted", null); _ui.ClearTranscript();
                    _ui.Post(UiMessageTypes.SessionNewResult, new SessionNewResultPayload { RequestId = requestId, Status = "completed" });
                    try { await RestoreTranscriptAsync(connection, token.Token).ConfigureAwait(false); }
                    catch (Exception ex) when (!(ex is OperationCanceledException)) { _logger.Warn("Could not restore the switched session's transcript: " + ex.Message); }
                }
                if (action == "delete")
                    await DeleteRecentSessionAsync(store, args, root, connection!, token.Token).ConfigureAwait(false);
                if (action == "rename" || action == "compact")
                {
                    var command = action == "rename" ? new JObject { ["type"] = "set_session_name", ["name"] = name } : OmpCommands.Compact(instructions);
                    var response = await connection!.SendCommandAsync(command, action == "compact" ? TimeSpan.FromMinutes(5) : TimeSpan.FromSeconds(15), token.Token).ConfigureAwait(false);
                    if (!response.Success) throw new InvalidOperationException(response.Error ?? PompLanguage.Pick("현재 세션을 업데이트하지 못했어요.", "Could not update the current session."));
                    var current = await connection.SendCommandAsync(OmpCommands.GetState(), TimeSpan.FromSeconds(10), token.Token).ConfigureAwait(false);
                    if (!current.Success || current.Data is not JObject currentState) throw new InvalidOperationException(PompLanguage.Pick("현재 세션 상태를 새로고침하지 못했어요.", "Could not refresh the current session state."));
                    token.Token.ThrowIfCancellationRequested();
                    ApplyState(currentState, currentLocked: () => ReferenceEquals(connection, _epochConnection));
                }
                if (action == "switch" || action == "messages")
                {
                    var cursor = args.GetString("cursor");
                    if (cursor != null && cursor.Length > 2048) throw new ArgumentException(PompLanguage.Pick("기록 커서가 올바르지 않아요.", "The history cursor is invalid."));
                    var pageCommand = new JObject { ["type"] = "get_messages_page", ["limit"] = 40 };
                    if (action == "messages" && cursor != null) pageCommand["cursor"] = cursor;
                    var page = await connection!.SendCommandAsync(pageCommand, TimeSpan.FromSeconds(20), token.Token).ConfigureAwait(false);
                    if (!page.Success) throw new InvalidOperationException(page.Error ?? PompLanguage.Pick("세션 메시지를 읽지 못했어요.", "Could not read session messages."));
                    var texts = new JArray(); var remaining = 100000;
                    foreach (var message in (page.Data?["messages"] as JArray ?? new JArray()).OfType<JObject>())
                    {
                        var role = message.GetString("role"); if (role != "user" && role != "assistant") continue;
                        var content = message["content"]; var text = content?.Type == JTokenType.String ? (string?)content : string.Join("\n", (content as JArray ?? new JArray()).OfType<JObject>().Select(p => p.AnswerText()).Where(t => t != null));
                        if (role == "user") text = PromptDisplay.UserText(text);
                        var length = Math.Min(remaining, Math.Min(text?.Length ?? 0, 12000));
                        texts.Add(new JObject { ["role"] = role, ["text"] = (text ?? "").Substring(0,length), ["truncated"] = length < (text?.Length ?? 0) }); remaining -= length;
                    }
                    answer["messages"] = texts; answer["nextCursor"] = page.Data?["nextCursor"]; answer["totalMessages"] = page.Data?["totalMessages"];
                    answer["sessionId"] = pageSession;
                }
            }
            catch (Exception ex)
            {
                if (action == "delete") answer["error"] = ex is SessionDeletionException ? ex.Message : PompLanguage.Pick("대화를 삭제하지 못했어요.", "Could not delete the conversation.");
                else answer["error"] = (_sessionSwitchUncertain ? PompLanguage.Pick("세션 전환 결과가 확실하지 않아요. ", "Session switch outcome is uncertain. ") : "") + (switched ? PompLanguage.Pick("세션은 바뀌었지만 기록을 불러오지 못했어요: ", "Session changed, but history could not be loaded: ") : "") + ex.Message;
                if (action == "switch" && !switched) _ui.Post(UiMessageTypes.SessionNewResult, new SessionNewResultPayload { RequestId = requestId, Status = "failed", Message = ex.Message });
            }
            // File enumeration/parsing (and reference writes) must not hold the chat/Stop command queue hostage.
            PublishRecentSessionsAsync(store, answer, action == "preview" ? args.GetString("id") ?? "" : null).Forget(_logger, "session history listing");
        }

        private async Task PublishRecentSessionsAsync(RecentSessionStore? store, JObject answer, string? previewId)
        {
            try
            {
                string? path, id, name;
                lock (_stateLock)
                {
                    var sameRoot = string.Equals(NormalizePath(answer.GetString("root")), NormalizePath(_sessionFolderRoot), StringComparison.OrdinalIgnoreCase);
                    path = sameRoot ? _sessionFile : null; id = sameRoot ? _info.SessionId : null; name = sameRoot ? _info.SessionName : null;
                }
                if (!string.IsNullOrEmpty(id))
                {
                    var current = new JObject { ["sessionId"] = id };
                    if (!string.IsNullOrEmpty(name)) current["name"] = name;
                    answer["current"] = current;
                }
                await _sessionReferences.Enqueue(() => Task.CompletedTask).ConfigureAwait(false);
                if (store != null)
                {
                    var listing = await Task.Run(() => store.List(path, id, name), _disposeCts.Token).ConfigureAwait(false);
                    answer["root"] = listing["root"];
                    var listedRoot = (string?)listing["root"] ?? "";
                    var partners = _tabs.All(listedRoot);
                    var entries = (listing["entries"] as JArray ?? new JArray()).OfType<JObject>().ToList();
                    foreach (var entry in entries)
                    {
                        var partner = (string?)entry["sessionId"] is string sessionId && partners.TryGetValue(sessionId, out var chosen) ? chosen : null;
                        entry["partner"] = partner == null || partner.Kind == "main" ? JValue.CreateNull() : Pomp.Json.PompJson.ToJObject(partner);
                    }
                    if (previewId != null && answer["error"] == null)
                        answer["preview"] = await Task.Run(() => store.Preview(previewId, path), _disposeCts.Token).ConfigureAwait(false);
                    // No count limit: every conversation while the message fits the UI hub; past it the oldest are counted, never dropped silently.
                    var hidden = StateBudget.Fit(entries, StateBudget.Bytes(new UiMessage(UiMessageTypes.SessionHistoryState, answer)) + 2048);
                    answer["entries"] = new JArray(entries);
                    if (hidden > 0) answer["hidden"] = hidden;
                }
            }
            catch (Exception ex) { answer["error"] = (answer.GetString("error") is string error ? error + " " : "") + ex.Message; }
            _ui.Post(UiMessageTypes.SessionHistoryState, answer, UiDelivery.State);
        }
    }
}
