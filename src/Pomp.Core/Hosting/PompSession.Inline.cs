using System;
using System.Collections.Concurrent;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Pomp.Bridge;
using Pomp.Diagnostics;
using Pomp.Features;
using Pomp.Protocol;
using Pomp.Ui;

namespace Pomp.Hosting
{
    public sealed partial class PompSession : IInlineAssistant
    {
        private const string InlineResultPrefix = "pomp-inline-result:";
        private readonly SemaphoreSlim inlineSlot = new SemaphoreSlim(1, 1);
        private readonly ConcurrentDictionary<string, InlineTicket> inlineTickets = new ConcurrentDictionary<string, InlineTicket>();

        public async Task<string> CompleteInlineAsync(InlineRequest request, CancellationToken cancellationToken)
        {
            if (request == null) throw new ArgumentNullException(nameof(request));
            var mode = request.IsCaption ? "caption" : request.IsEdit ? "edit" : "complete";
            // The engine side gives up first (completion: the option, default 10 s; caption 12 s; edit 45 s); the host waits a little longer for its answer.
            var completionSeconds = Math.Max(InlineCompletionLimits.MinSeconds, Math.Min(InlineCompletionLimits.MaxSeconds,
                (_settings as IInlineModelSettings)?.InlineCompletionTimeoutSeconds ?? InlineCompletionLimits.DefaultSeconds));
            var wait = TimeSpan.FromSeconds(mode == "edit" ? 50 : mode == "caption" ? 16 : completionSeconds + 4);
            var spec = EditorModelSetting.EngineSpec((_settings as IInlineModelSettings)?.InlineModel);
            // A request that finds another one running waits for it: the editor cancels the older waiting ones as the user types, so
            // the newest suggestion is asked as soon as the engine is free (the running one cannot be stopped, it ends within its limit).
            var reply = await SendInlineAsync(mode, request, spec, wait, waitForSlot: true, cancellationToken, engineTimeoutMs: mode == "complete" ? completionSeconds * 1000 : (int?)null).ConfigureAwait(false);
            return reply?.Text ?? string.Empty;
        }

        /// <summary>Which model the engine means by <paramref name="spec"/> (null = the chat model) right now; no model is called.</summary>
        internal async Task<JObject?> ResolveEditorModelAsync(string? spec, CancellationToken cancellationToken)
        {
            var reply = await SendInlineAsync("resolve", new InlineRequest(), spec, TimeSpan.FromSeconds(10), waitForSlot: true, cancellationToken).ConfigureAwait(false);
            return reply?.Model;
        }

        /// <summary>One <c>/pomp-inline</c> request; null when another one is running and <paramref name="waitForSlot"/> is false.</summary>
        private async Task<InlineReply?> SendInlineAsync(string mode, InlineRequest request, string? model, TimeSpan resultTimeout, bool waitForSlot, CancellationToken cancellationToken, string? expectedSession = null, int? engineTimeoutMs = null)
        {
            cancellationToken.ThrowIfCancellationRequested();
            CheckInlineAdmission();
            // The engine connection and conversation the request was made for: a wait never moves it to a restarted engine or another session.
            OmpConnection? origin; string? ownerSession;
            lock (_stateLock)
            {
                origin = _epochConnection; ownerSession = _info.SessionId;
                if (expectedSession != null && expectedSession != ownerSession)
                    throw new InlineErrorException("conversation-changed", InlineError("conversation-changed"));
            }
            if (mode == "prompt" && !_settings.ComposerPredictions) throw new InlineErrorException("disabled", "disabled");
            if (!await inlineSlot.WaitAsync(waitForSlot ? TimeSpan.FromSeconds(60) : TimeSpan.Zero, cancellationToken).ConfigureAwait(false))
            {
                if (waitForSlot) throw new TimeoutException(PompLanguage.Pick("다른 편집기 요청이 실행 중이에요.", "Another editor request is still running."));
                return null;
            }

            try
            {
                // What allowed the request may have changed while it waited (a chat reply or an approval started meanwhile).
                CheckInlineAdmission();
                if (mode == "prompt" && !_settings.ComposerPredictions) throw new InlineErrorException("disabled", "disabled");
                lock (_stateLock)
                {
                    if (!ReferenceEquals(origin, _epochConnection) || ownerSession != _info.SessionId)
                        throw new OperationCanceledException(PompLanguage.Pick("편집기 요청을 기다리는 동안 연결이나 대화가 바뀌었어요.", "The connection or the conversation changed while the editor request waited."), cancellationToken);
                }
            }
            catch
            {
                inlineSlot.Release();
                throw;
            }

            var id = Guid.NewGuid().ToString("N");
            var ticket = new InlineTicket(origin);
            inlineTickets[id] = ticket;
            var started = System.Diagnostics.Stopwatch.StartNew();
            try
            {
                await RequireEngineCommandsAsync(origin, cancellationToken, "pomp-permissions", "pomp-inline").ConfigureAwait(false);
                cancellationToken.ThrowIfCancellationRequested();
                lock (_stateLock) if (_sessionSwitchUncertain || string.IsNullOrEmpty(ownerSession) || ownerSession != _info.SessionId) throw new OperationCanceledException(PompLanguage.Pick("편집기 요청 전에 대화가 바뀌었어요.", "Conversation changed before inline inference."), cancellationToken);
                if (mode == "prompt" && !_settings.ComposerPredictions) throw new InlineErrorException("disabled", "disabled");
                var payload = new JObject { ["id"] = id, ["sessionId"] = ownerSession, ["mode"] = mode,
                    ["prefix"] = Tail(request.Prefix, 12000), ["selection"] = Bound(request.Selection, 24000),
                    ["suffix"] = Bound(request.Suffix, 4000), ["instruction"] = Bound(request.Instruction, 2000),
                    ["language"] = Bound(request.Language, 100) };
                if (!string.IsNullOrWhiteSpace(model)) payload["model"] = Bound(model!, 512);
                if (engineTimeoutMs != null) payload["timeoutMs"] = engineTimeoutMs.Value;
                var message = "/pomp-inline " + Convert.ToBase64String(Encoding.UTF8.GetBytes(payload.ToString(Formatting.None)));
                // This registered extension command calls the editor model directly without agent tools.
                // Do not abort the main agent when a suggestion becomes stale. Keep the one-request slot
                // until this command returns (the extension itself has a bounded provider timeout).
                var response = await origin!.SendCommandAsync(new JObject { ["type"] = "prompt", ["message"] = message },
                    TimeSpan.FromSeconds(12), _disposeCts.Token).ConfigureAwait(false);
                if (!response.Success) throw new InvalidOperationException(PompLanguage.Pick("편집기 요청을 거부했어요.", "The inline request was rejected."));
                var timeout = Task.Delay(resultTimeout, _disposeCts.Token);
                if (await Task.WhenAny(ticket.Completion.Task, timeout).ConfigureAwait(false) != ticket.Completion.Task)
                    throw new TimeoutException(PompLanguage.Pick("보조 모델 요청 시간이 초과됐어요.", "Inline assistance timed out."));
                cancellationToken.ThrowIfCancellationRequested();
                var result = await ticket.Completion.Task.ConfigureAwait(false);
                if (mode != "resolve")
                    _logger.Debug("Editor " + mode + " answered by " + ((string?)result.Model?["provider"] ?? "?") + "/" + ((string?)result.Model?["id"] ?? "?") + " in " + started.ElapsedMilliseconds + " ms.");
                lock (_stateLock) if (_sessionSwitchUncertain || ownerSession != _info.SessionId || !ReferenceEquals(origin, _epochConnection)) throw new OperationCanceledException(PompLanguage.Pick("편집기 요청 중에 대화가 바뀌었어요.", "Conversation changed during inline inference."), cancellationToken);
                return result;
            }
            finally { inlineTickets.TryRemove(id, out _); inlineSlot.Release(); }
        }

        public async Task StageInlineEditAsync(string path, string beforeHash, string after, CancellationToken cancellationToken)
        {
            if (_workspaceReview == null) throw new InvalidOperationException(PompLanguage.Pick("변경 검토를 사용할 수 없어요.", "Workspace review is unavailable."));
            var result = await _workspaceReview.StageAsync(new JObject { ["files"] = new JArray(new JObject {
                ["path"] = path, ["beforeHash"] = beforeHash, ["after"] = after }) }, cancellationToken).ConfigureAwait(false);
            if (result.IsError) throw new InvalidOperationException(result.Content[0].Text);
        }

        public async Task StageInlineSnapshotAsync(string path, string before, string after, CancellationToken cancellationToken)
        {
            if (before.Length > 500000 || after.Length > 500000) throw new ArgumentException(PompLanguage.Pick("편집기 변경 내용이 너무 커요.", "The inline snapshot is too large."));
            if (_workspaceReview == null) throw new InvalidOperationException(PompLanguage.Pick("변경 검토를 사용할 수 없어요.", "Workspace review is unavailable."));
            var root = await _environment.GetWorkspaceRootAsync(cancellationToken).ConfigureAwait(false);
            var result = await _workspaceReview.StageCapturedAsync(root, new ShadowFile(path, before, after), cancellationToken).ConfigureAwait(false);
            if (result.IsError) throw new InvalidOperationException(result.Content[0].Text);
            // The user asked for this edit from the editor: show its proposal at once (the page opens the review view).
            OpenWorkView("review");
        }

        private bool HandleInlineNotification(string? message, OmpConnection? origin)
        {
            if (HandleTokenCountNotification(message, origin)) return true;
            if (message == null || !message.StartsWith(InlineResultPrefix, StringComparison.Ordinal)) return false;
            // Also consume late results: they must not leak code into the chat transcript.
            if (message.Length > 200000) return true;
            try
            {
                var payload = JObject.Parse(Encoding.UTF8.GetString(Convert.FromBase64String(message.Substring(InlineResultPrefix.Length))));
                var id = (string?)payload["id"];
                if (id == null || !inlineTickets.TryGetValue(id, out var ticket) || !ReferenceEquals(ticket.Origin, origin)) return true;
                var error = (string?)payload["error"];
                var text = (string?)payload["text"] ?? string.Empty;
                if (error != null) ticket.Completion.TrySetException(new InlineErrorException(InlineErrorCode(error), InlineError(error)));
                else if (text.Length > 40000 || text.IndexOf('\0') >= 0) ticket.Completion.TrySetException(new InlineErrorException("invalid-result", PompLanguage.Pick("편집기 결과가 올바르지 않아요.", "The inline result is invalid.")));
                else ticket.Completion.TrySetResult(new InlineReply(text, InlineModelOf(payload["model"] as JObject)));
            }
            catch (Exception ex) when (ex is FormatException || ex is JsonException || ex is InvalidCastException)
            { _logger.Debug("Ignored a malformed inline result."); }
            return true;
        }
        private sealed class InlineErrorException : InvalidOperationException
        {
            public InlineErrorException(string code, string message) : base(message) { Code = code; }
            public string Code { get; }
        }

        private static string InlineErrorCode(string error)
        {
            switch (error)
            {
                case "conversation-changed": case "Conversation changed before inline inference. Request a fresh suggestion.": return "conversation-changed";
                case "invalid-model": case "Invalid inline model selection.": return "invalid-model";
                case "model-unavailable": case "The editor model is unavailable.": case "The editor model is unavailable. Choose another one in POMP (Accounts > Editor model).": return "model-unavailable";
                case "timed-out": case "Inline model request timed out.": return "timed-out";
                case "invalid-result": return "invalid-result";
                default: return "request-failed";
            }
        }

        private static string InlineError(string error)
        {
            switch (error)
            {
                case "conversation-changed": case "Conversation changed before inline inference. Request a fresh suggestion.":
                    return PompLanguage.Pick("편집기 요청 전에 대화가 바뀌었어요.", "The conversation changed before the editor request.");
                case "invalid-model": case "Invalid inline model selection.":
                    return PompLanguage.Pick("보조 모델이 올바르지 않아요.", "The helper model selection is invalid.");
                case "model-unavailable": case "The editor model is unavailable.": case "The editor model is unavailable. Choose another one in POMP (Accounts > Editor model).":
                    return PompLanguage.Pick("보조 모델을 사용할 수 없어요.", "The helper model is unavailable.");
                case "timed-out": case "Inline model request timed out.":
                    return PompLanguage.Pick("보조 모델 요청 시간이 초과됐어요.", "The helper model request timed out.");
                case "invalid-result":
                    return PompLanguage.Pick("편집기 결과가 올바르지 않아요.", "The editor result is invalid.");
                default:
                    return PompLanguage.Pick("보조 모델 요청에 실패했어요.", "The helper model request failed.");
            }
        }

        private sealed class InlineBusyException : InvalidOperationException
        {
            public InlineBusyException(string message) : base(message) { }
        }

        /// <summary>Editor requests go to the engine only while the chat is idle: they share its connection and conversation.</summary>
        private void CheckInlineAdmission()
        {
            if (_sessionSwitchUncertain) throw new InlineErrorException("conversation-changed", PompLanguage.Pick("현재 대화가 확인되지 않았어요.", "The active conversation is uncertain after a session switch."));
            if (!_settings.LoadOmpExtensions || _bridge.State != BridgeState.Running)
                throw new InlineErrorException("disconnected", PompLanguage.Pick("POMP에 연결되어 있지 않거나 확장이 꺼져 있어요.", "POMP is not connected or its extensions are off."));
            if (_ui.PendingRequestCount > 0) throw new InlineBusyException(PompLanguage.Pick("승인이나 질문이 대기 중이에요.", "An approval or question is pending."));
            lock (_stateLock) if (_info.IsStreaming) throw new InlineBusyException(PompLanguage.Pick("에이전트가 응답 중이에요.", "The main agent is busy."));
        }

        /// <summary>The model the engine used (<c>{ provider, id, name? }</c>), only when its parts are plain bounded text.</summary>
        private static JObject? InlineModelOf(JObject? model)
        {
            static string? Part(JToken? token) => token?.Type == JTokenType.String && ((string)token!).Length is > 0 and <= 200 && !((string)token!).Any(char.IsControl) ? (string)token! : null;
            var provider = Part(model?["provider"]);
            var id = Part(model?["id"]);
            if (provider == null || id == null) return null;
            var result = new JObject { ["provider"] = provider, ["id"] = id };
            var name = Part(model!["name"]);
            if (name != null) result["name"] = name;
            return result;
        }

        private static string Bound(string text, int max) => text.Length <= max ? text : text.Substring(0, max);
        private static string Tail(string text, int max) => text.Length <= max ? text : text.Substring(text.Length - max);
        private sealed class InlineTicket
        {
            public InlineTicket(OmpConnection? origin) { Origin = origin; }
            public OmpConnection? Origin { get; }
            public TaskCompletionSource<InlineReply> Completion { get; } = new TaskCompletionSource<InlineReply>(TaskCreationOptions.RunContinuationsAsynchronously);
        }

        private sealed class InlineReply
        {
            public InlineReply(string text, JObject? model) { Text = text; Model = model; }
            public string Text { get; }
            public JObject? Model { get; }
        }
    }
}
