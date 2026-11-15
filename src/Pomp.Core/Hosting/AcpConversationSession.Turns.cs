using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Pomp.AgentClients;
using Pomp.Diagnostics;
using Pomp.Internal;
using Pomp.Ui;

namespace Pomp.Hosting
{
    internal sealed partial class AcpConversationSession
    {
        private sealed class Turn
        {
            internal string Id = "";
            internal AcpConnection Connection = null!;
            internal long Generation;
            internal int Pending;
            internal bool Started, Cancelled, Failed;
            internal string? Error;
            internal string StopReason = "end_turn";
            internal CancellationTokenSource Admission = null!;
        }
        private sealed class QueuedInput
        {
            internal string Id = Guid.NewGuid().ToString("N");
            internal string CreatedAt = DateTime.UtcNow.ToString("O", CultureInfo.InvariantCulture);
            internal ChatSendPayload Send = null!;
        }
        private sealed class Approval
        {
            internal JObject Payload = null!;
            internal HashSet<string> Choices = null!;
            internal TaskCompletionSource<JToken> Completion = new TaskCompletionSource<JToken>(TaskCreationOptions.RunContinuationsAsynchronously);
        }
        private readonly List<QueuedInput> _queue = new List<QueuedInput>();
        private readonly Dictionary<string, Approval> _permissions = new Dictionary<string, Approval>(StringComparer.Ordinal);
        private Turn? _turn;
        private bool _queuePaused, _queueScheduled;
        private string? _queueReason;
        private long _stopGeneration;

        private string NextId(string kind) => kind + "-" + Interlocked.Increment(ref _sequence).ToString(CultureInfo.InvariantCulture);
        private void RequireReadyLocked()
        {
            ThrowIfDisposed();
            if (!_ready || _frozen || _connection == null || _agentSessionId == null)
                throw new InvalidOperationException(_error ?? PompLanguage.Pick("ACP 대화가 아직 준비되지 않았어요.", "The ACP conversation is not ready."));
        }
        private static void ValidateInput(ChatSendPayload send)
        {
            if (string.IsNullOrWhiteSpace(send.Text) && (send.Attachments == null || send.Attachments.Count == 0)) throw new ArgumentException(PompLanguage.Pick("메시지가 비어 있어요.", "The message is empty."));
            if (send.Text.Length > 500000 || send.Attachments?.Count > 100 || send.Attachments?.Any(a => string.IsNullOrEmpty(a.Path) || a.Path.Length > 4096 || a.Path.Any(char.IsControl)) == true)
                throw new ArgumentException(PompLanguage.Pick("메시지나 첨부가 입력 한도를 넘어요.", "The message or its attachments exceed the input limits."));
            if (!string.IsNullOrEmpty(send.Agent) || !string.IsNullOrEmpty(send.Team) || !string.IsNullOrEmpty(send.AgentInstance))
                throw new NotSupportedException(PompLanguage.Pick("ACP 대화는 OMP 에이전트·팀 대상으로 보낼 수 없어요.", "ACP conversations cannot target OMP agents or teams."));
        }
        private void RejectInput(ChatSendPayload send, Exception ex) => _ui.Post(UiMessageTypes.Error,
            new UiErrorInfo { Code = "queue_rejected", ClientMessageId = send.ClientMessageId, Message = ex.Message, Retryable = false }, UiDelivery.State);

        // This queue is held only during admission/context capture, never for the model's long-running prompt response.
        private async Task<bool> AdmitAsync(ChatSendPayload send, bool steer, bool bypassQueue = false, long? expectedStopGeneration = null)
        {
            Turn? turn = null;
            bool steering = false;
            QueuedInput? queued = null;
            string session = "";
            CancellationToken epoch = default;
            try
            {
                ValidateInput(send);
                lock (_sync)
                {
                    RequireReadyLocked();
                    if (expectedStopGeneration.HasValue && expectedStopGeneration.Value != _stopGeneration) throw new OperationCanceledException();
                    if (!steer && !bypassQueue && (_turn != null || _queue.Count > 0))
                    {
                        queued = new QueuedInput { Send = send };
                        _queue.Add(queued);
                        if (QueueBytesLocked() > 1800 * 1024) { _queue.Remove(queued); throw new InvalidOperationException(PompLanguage.Pick("대기열이 너무 커요.", "The queue is too large.")); }
                    }
                    else
                    {
                        if (_turn?.Cancelled == true) throw new InvalidOperationException(PompLanguage.Pick("중지 확인을 기다리고 있어요. 일반 입력은 대기열에 추가할 수 있어요.", "Waiting for cancellation to finish. Ordinary input can still be queued."));
                        steering = _turn != null;
                        if (_turn == null)
                        {
                            _turn = new Turn { Id = NextId("acp-turn"), Connection = _connection!, Generation = _generation,
                                Admission = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token, _epoch.Token) };
                            // Only a fresh submission resumes: "Send now" on one retained item leaves the others where the user stopped them.
                            if (_queue.Count == 0) { _queuePaused = false; _queueReason = null; }
                        }
                        turn = _turn; turn.Pending++;
                        session = _agentSessionId!; epoch = _epoch.Token;
                    }
                }
                if (queued != null)
                {
                    _ui.Post(UiMessageTypes.ChatQueued, new ChatQueuedPayload { ClientMessageId = send.ClientMessageId, ItemId = queued.Id }, UiDelivery.State);
                    PublishState(); ScheduleQueue(); return true;
                }
                PublishState();
                var context = send.IncludeEditorContext ? await _contextBuilder.BuildAsync(turn!.Admission.Token).ConfigureAwait(false) : null;
                var prompt = await BuildPromptAsync(send, context, turn!.Admission.Token).ConfigureAwait(false);
                lock (_sync)
                {
                    turn.Admission.Token.ThrowIfCancellationRequested();
                    RequireConnection(turn.Connection, turn.Generation);
                    if (!ReferenceEquals(turn, _turn) || session != _agentSessionId) throw new OperationCanceledException(PompLanguage.Pick("메시지를 보내기 전에 대화가 바뀌었어요.", "The conversation changed before the message was sent."));
                    if (!steering) { ResetMessageProjectionLocked(); turn.Started = true; }
                }
                if (steering)
                    _ui.Post(UiMessageTypes.ChatSteered, new ChatSteeredPayload { SteerId = NextId("acp-steer"), ClientMessageId = send.ClientMessageId,
                        Text = send.Text, Attachments = send.Attachments, EditorContext = context?.EditorContext });
                else
                    _ui.Post(UiMessageTypes.ChatTurnStarted, new ChatTurnStartedPayload { PromptId = turn.Id, ClientMessageId = send.ClientMessageId,
                        Text = send.Text, Attachments = send.Attachments, EditorContext = context?.EditorContext, Main = true });
                // The connection enqueues this write before returning. A later Stop cannot overtake this prompt.
                var response = turn.Connection.CallAsync("session/prompt", new JObject { ["sessionId"] = session, ["prompt"] = prompt }, epoch);
                CompletePromptAsync(turn, response).Forget(_logger, "ACP prompt completion");
                return true;
            }
            catch (Exception ex)
            {
                RejectInput(send, ex is OperationCanceledException ? new InvalidOperationException(PompLanguage.Pick("중지되거나 대화가 바뀌어 보내지 않았어요.", "The message was not sent because it was stopped or the conversation changed.")) : ex);
                // A steer that never reached the agent is only reported as not sent; the running turn is not its failure.
                if (turn != null) FinishPrompt(turn, null, ex, notSent: steering);
                return false;
            }
        }

        private async Task CompletePromptAsync(Turn turn, Task<JToken> response)
        {
            JToken? result = null; Exception? failure = null;
            try { result = await response.ConfigureAwait(false); }
            catch (Exception ex) { failure = ex; }
            if (failure is AcpRpcException rpc && rpc.Code == -32000) SetFailure(failure);
            FinishPrompt(turn, result, failure);
        }

        private void FinishPrompt(Turn turn, JToken? result, Exception? failure, bool notSent = false)
        {
            bool finished;
            string status;
            List<UiMessage> output = new List<UiMessage>();
            lock (_sync)
            {
                if (!ReferenceEquals(_turn, turn)) return;
                if (failure != null)
                {
                    if (!turn.Cancelled && !notSent) { turn.Failed = true; turn.Error = failure.Message; }
                }
                else
                {
                    var reason = (string?)result?["stopReason"];
                    if (string.IsNullOrEmpty(reason)) { turn.Failed = true; turn.Error = PompLanguage.Pick("ACP가 응답이 어떻게 끝났는지 알려 주지 않았어요.", "ACP did not confirm how the prompt ended."); }
                    else if (reason == "cancelled") turn.Cancelled = true;
                    else if (reason != "end_turn") { turn.StopReason = reason!; turn.Failed = true; turn.Error = PompLanguage.Pick("Antigravity가 멈췄어요: ", "Antigravity stopped: ") + reason; }
                }
                turn.Pending--;
                finished = turn.Pending == 0;
                if (!finished) return;
                status = turn.Cancelled ? "aborted" : turn.Failed ? "error" : "completed";
                FinishMessagesLocked(output, turn.StopReason, turn.Error);
                foreach (var tool in _toolStates.Values.Where(t => !t.Ended))
                {
                    tool.Ended = true;
                    output.Add(new UiMessage(UiMessageTypes.ChatToolEnd, new ChatToolEndPayload { ToolCallId = tool.UiId, ToolName = ToolName(tool.Data),
                        IsError = true, ResultText = turn.Cancelled ? PompLanguage.Pick("취소됨", "Cancelled") : PompLanguage.Pick("도구 결과가 끝나기 전에 응답이 끝났어요.", "The turn ended without a completed tool result.") }));
                }
                _turn = null;
                if (turn.Failed || turn.Cancelled) { _queuePaused = true; _queueReason = turn.Error ?? PompLanguage.Pick("사용자가 작업을 중지했어요.", "The user stopped the turn."); }
                if (turn.Started) output.Add(new UiMessage(UiMessageTypes.ChatTurnEnd, new ChatTurnEndPayload { PromptId = turn.Id, Status = status,
                    Error = turn.Error == null ? null : new UiErrorInfo { Code = "acpPrompt", Message = turn.Error, Retryable = false } }));
            }
            turn.Admission.Dispose();
            CancelPermissions();
            PostTranscript(output);
            PublishState();
            ScheduleQueue();
        }

        private void FailCurrentTurn(string message, bool aborted)
        {
            Turn? turn;
            lock (_sync)
            {
                turn = _turn;
                if (turn == null) return;
                turn.Cancelled = aborted; turn.Failed = !aborted; turn.Error = message;
                // The transport is already lost/owned by teardown; every outstanding request is terminal.
                turn.Pending = 1;
            }
            CancelAdmission(turn);
            FinishPrompt(turn, null, new InvalidOperationException(message));
        }
        private static void CancelAdmission(Turn? turn)
        {
            try { turn?.Admission.Cancel(); }
            catch (ObjectDisposedException) { } // A concurrently completed turn already released admission.
        }

        private Task StopAsync()
        {
            Turn? turn;
            string? id;
            AcpConnection? connection;
            lock (_sync)
            {
                ++_stopGeneration;
                _queuePaused = true; _queueReason = PompLanguage.Pick("사용자가 작업을 중지했어요.", "The user stopped the turn.");
                turn = _turn; id = _agentSessionId; connection = _connection;
                if (turn != null) turn.Cancelled = true;
            }
            CancelAdmission(turn);
            CancelPermissions();
            PublishState();
            // Admission cancellation is immediate. The cancel write waits only for the short admission lane, never inference.
            return _commands.Enqueue(async () =>
            {
                if (connection == null || id == null || turn == null) return;
                lock (_sync) if (!ReferenceEquals(connection, _connection) || id != _agentSessionId) return;
                try { await connection.NotifyAsync("session/cancel", new JObject { ["sessionId"] = id }, _lifetime.Token).ConfigureAwait(false); }
                catch (Exception ex)
                {
                    Notice("error", PompLanguage.Pick("중지 요청을 전달하지 못했어요. 연결을 다시 시작해 주세요. ", "Cancellation could not be delivered. Restart the connection. ") + ex.Message);
                }
            });
        }

        private ChatQueueStatePayload QueueLocked() => new ChatQueueStatePayload
        {
            SessionId = PublicId(_agentSessionId), Paused = _queuePaused, Reason = _queueReason,
            Items = _queue.Select(q => new ChatQueueItemPayload { Id = q.Id, Text = q.Send.Text, Attachments = q.Send.Attachments,
                IncludeEditorContext = q.Send.IncludeEditorContext, CreatedAt = q.CreatedAt, CanSteer = true }).ToList(),
        };
        private int QueueBytesLocked() => Encoding.UTF8.GetByteCount(Pomp.Json.PompJson.Serialize(QueueLocked()));

        private async Task QueueCommandAsync(JObject args, long expectedStopGeneration)
        {
            var requestId = (string?)args["requestId"];
            try
            {
                QueuedInput? steer = null;
                lock (_sync)
                {
                    if (((string?)args["action"] == "resume" || (string?)args["action"] == "steer") && expectedStopGeneration != _stopGeneration)
                        throw new OperationCanceledException(PompLanguage.Pick("중지해서 대기열 명령을 실행하지 않았어요.", "The queue action was superseded by Stop."));
                    var item = _queue.FirstOrDefault(q => q.Id == (string?)args["id"]);
                    switch ((string?)args["action"])
                    {
                        case "clear": _queue.Clear(); break;
                        case "pause": _queuePaused = true; _queueReason = PompLanguage.Pick("대기열을 일시정지했어요.", "Queue paused."); break;
                        case "resume": RequireReadyLocked(); _queuePaused = false; _queueReason = null; break;
                        case "remove": if (item == null) throw new ArgumentException(PompLanguage.Pick("대기열 항목을 찾을 수 없어요.", "Queue item not found.")); _queue.Remove(item); break;
                        case "move":
                            var index = (int?)args["index"];
                            if (item == null || index == null || index < 0 || index >= _queue.Count) throw new ArgumentException(PompLanguage.Pick("대기열 위치가 올바르지 않아요.", "Invalid queue position."));
                            _queue.Remove(item); _queue.Insert(index.Value, item); break;
                        case "edit":
                            if (item == null || args["text"]?.Type != JTokenType.String) throw new ArgumentException(PompLanguage.Pick("대기열 수정 내용이 올바르지 않아요.", "Invalid queue edit."));
                            var previous = item.Send.Text; item.Send.Text = (string)args["text"]!;
                            try { ValidateInput(item.Send); if (QueueBytesLocked() > 1800 * 1024) throw new ArgumentException(PompLanguage.Pick("대기열이 너무 커요.", "The queue is too large.")); }
                            catch { item.Send.Text = previous; throw; }
                            break;
                        case "steer":
                            RequireReadyLocked();
                            if (item == null || _turn?.Cancelled == true) throw new InvalidOperationException(PompLanguage.Pick("지금은 이 대기 메시지를 바로 보낼 수 없어요.", "The queued message cannot be steered now."));
                            steer = item; break;
                        default: throw new ArgumentException(PompLanguage.Pick("알 수 없는 대기열 명령이에요.", "Unknown queue action."));
                    }
                }
                if (steer != null)
                {
                    steer.Send.ClientMessageId = "queue-" + steer.Id;
                    if (!await AdmitAsync(steer.Send, steer: true, bypassQueue: true, expectedStopGeneration: expectedStopGeneration).ConfigureAwait(false))
                        throw new InvalidOperationException(PompLanguage.Pick("대기 메시지를 보내지 못해 대기열에 그대로 있어요.", "The queued message was not sent and remains in the queue."));
                    // With nothing retained any more, a pause would only hold back what the user sends next.
                    lock (_sync) { _queue.Remove(steer); if (_queue.Count == 0) { _queuePaused = false; _queueReason = null; } }
                }
                PublishState(); Reply(UiMessageTypes.ChatQueueReply, requestId, null); ScheduleQueue();
            }
            catch (Exception ex) { Reply(UiMessageTypes.ChatQueueReply, requestId, ex.Message); }
        }

        private void ScheduleQueue()
        {
            lock (_sync)
            {
                if (_queueScheduled || _disposed || !_ready || _frozen || _queuePaused || _turn != null || _queue.Count == 0) return;
                _queueScheduled = true;
            }
            _commands.Enqueue(async () =>
            {
                QueuedInput? item = null;
                long stopGeneration;
                lock (_sync)
                {
                    _queueScheduled = false;
                    stopGeneration = _stopGeneration;
                    if (!_disposed && _ready && !_frozen && !_queuePaused && _turn == null && _queue.Count > 0) item = _queue[0];
                }
                if (item == null) return;
                item.Send.ClientMessageId = "queue-" + item.Id;
                var admitted = await AdmitAsync(item.Send, steer: false, bypassQueue: true, expectedStopGeneration: stopGeneration).ConfigureAwait(false);
                lock (_sync)
                {
                    if (admitted) _queue.Remove(item);
                    else { _queuePaused = true; _queueReason = PompLanguage.Pick("대기 메시지를 보내지 못했어요.", "The queued message could not be sent."); }
                }
                PublishState();
            });
        }

        private async Task<JToken> HandleServerRequestAsync(AcpConnection connection, long generation, string method, JObject parameters, CancellationToken token)
        {
            if (method != "session/request_permission") throw new AcpRpcException(-32601, "Unsupported client capability: " + method);
            Approval approval;
            string id = NextId("acp-approval");
            lock (_sync)
            {
                if (_disposed || _frozen || !ReferenceEquals(connection, _connection) || generation != _generation ||
                    (string?)parameters["sessionId"] != _agentSessionId || _turn == null || _turn.Cancelled || _replaying)
                    return CancelledOutcome();
                var options = parameters["options"] as JArray;
                if (parameters["toolCall"] is not JObject tool || options == null || options.Count == 0 || _permissions.Count >= 128)
                    return CancelledOutcome();
                var choices = options.OfType<JObject>().Select(o => (string?)o["optionId"]).Where(o => !string.IsNullOrEmpty(o)).Cast<string>().ToArray();
                if (choices.Length != options.Count || choices.Distinct(StringComparer.Ordinal).Count() != choices.Length) return CancelledOutcome();
                approval = new Approval { Choices = new HashSet<string>(choices, StringComparer.Ordinal), Payload = new JObject
                {
                    ["requestId"] = id, ["toolCall"] = tool.DeepClone(), ["options"] = options.DeepClone(),
                } };
                var bytes = Encoding.UTF8.GetByteCount(approval.Payload.ToString(Formatting.None));
                if (bytes + _permissions.Values.Sum(p => Encoding.UTF8.GetByteCount(p.Payload.ToString(Formatting.None))) > 1024 * 1024)
                    return CancelledOutcome();
                _permissions.Add(id, approval);
            }
            PublishState();
            using var cancellation = token.Register(() => approval.Completion.TrySetResult(CancelledOutcome()));
            try { return await approval.Completion.Task.ConfigureAwait(false); }
            finally
            {
                lock (_sync) _permissions.Remove(id);
                PublishState();
            }
        }
        private static JObject CancelledOutcome() => new JObject { ["outcome"] = new JObject { ["outcome"] = "cancelled" } };
        private void CancelPermissions()
        {
            Approval[] pending;
            lock (_sync) { pending = _permissions.Values.ToArray(); _permissions.Clear(); }
            foreach (var approval in pending) approval.Completion.TrySetResult(CancelledOutcome());
        }

        private async Task HandleAcpCommandAsync(JObject args)
        {
            var request = (string?)args["requestId"];
            try
            {
                var action = (string?)args["action"];
                if (action == "permission")
                {
                    lock (_sync)
                    {
                        RequireReadyLocked();
                        if ((string?)args["sessionId"] != PublicId(_agentSessionId) || _turn?.Cancelled != false ||
                            !_permissions.TryGetValue((string?)args["permissionRequestId"] ?? "", out var approval) ||
                            !approval.Choices.Contains((string?)args["optionId"] ?? ""))
                            throw new InvalidOperationException(PompLanguage.Pick("이 승인 요청은 끝났거나 다른 대화의 요청이에요.", "This approval has expired or belongs to another conversation."));
                        _permissions.Remove((string)args["permissionRequestId"]!);
                        if (!approval.Completion.TrySetResult(new JObject { ["outcome"] = new JObject { ["outcome"] = "selected", ["optionId"] = args["optionId"]!.DeepClone() } }))
                            throw new InvalidOperationException(PompLanguage.Pick("이 승인 요청은 이미 처리되었어요.", "This approval has already settled."));
                    }
                }
                else if (action == "set-config")
                {
                    AcpConnection connection; string session; long generation; JObject parameters;
                    lock (_sync)
                    {
                        RequireReadyLocked();
                        if ((string?)args["sessionId"] != PublicId(_agentSessionId)) throw new InvalidOperationException(PompLanguage.Pick("이 설정은 다른 대화의 것이에요.", "The configuration belongs to another conversation."));
                        var option = _configOptions.OfType<JObject>().FirstOrDefault(o => (string?)o["id"] == (string?)args["configId"])
                            ?? throw new ArgumentException(PompLanguage.Pick("알 수 없는 ACP 설정 항목이에요.", "Unknown ACP configuration option."));
                        var value = args["value"] ?? throw new ArgumentException(PompLanguage.Pick("설정 값이 필요해요.", "A configuration value is required."));
                        var boolean = (string?)option["type"] == "boolean";
                        if (boolean ? value.Type != JTokenType.Boolean : (string?)option["type"] != "select" || !ConfigValues(option).Any(o => JToken.DeepEquals(o["value"], value)))
                            throw new ArgumentException(PompLanguage.Pick("공식 CLI가 제공하지 않은 값이에요.", "The value was not offered by the official CLI."));
                        connection = _connection!; session = _agentSessionId!; generation = _generation;
                        parameters = new JObject { ["sessionId"] = session, ["configId"] = option["id"]!.DeepClone(), ["value"] = value.DeepClone() };
                        if (boolean) parameters["type"] = "boolean";
                    }
                    var result = await MetadataAsync(connection, "session/set_config_option", parameters, _lifetime.Token).ConfigureAwait(false);
                    lock (_sync) { RequireConnection(connection, generation); if (_agentSessionId != session) throw new OperationCanceledException(); ApplyConfigurationLocked(result); }
                }
                else if (action == "authenticate")
                {
                    AcpConnection connection; string method; long generation;
                    lock (_sync)
                    {
                        ThrowIfDisposed();
                        if (_connection == null || !_initialized || _frozen || _turn != null) throw new InvalidOperationException(PompLanguage.Pick("로그인하기 전에 공식 CLI에 연결해 주세요.", "Connect to the official CLI before signing in."));
                        if (args["sessionId"] != null && (string?)args["sessionId"] != PublicId(_agentSessionId)) throw new InvalidOperationException(PompLanguage.Pick("대화가 바뀌었어요.", "The conversation changed."));
                        method = (string?)args["methodId"] ?? "";
                        if (!_authMethods.OfType<JObject>().Any(a => (string?)a["id"] == method)) throw new ArgumentException(PompLanguage.Pick("CLI가 제공하지 않은 로그인 방법이에요.", "The CLI did not offer this authentication method."));
                        connection = _connection; generation = _generation;
                    }
                    await MetadataAsync(connection, "authenticate", new JObject { ["methodId"] = method }, _lifetime.Token, _options.AuthHelperLoginTimeout).ConfigureAwait(false);
                    lock (_sync) { RequireConnection(connection, generation); _authRequired = false; _error = null; }
                    await EnsureStartedAsync(_lifetime.Token).ConfigureAwait(false);
                }
                else throw new ArgumentException(PompLanguage.Pick("알 수 없는 ACP 명령이에요.", "Unknown ACP action."));
                PublishState(); Reply(UiMessageTypes.AcpReply, request, null);
            }
            catch (Exception ex)
            {
                Reply(UiMessageTypes.AcpReply, request, ex.Message);
            }
        }
    }
}
