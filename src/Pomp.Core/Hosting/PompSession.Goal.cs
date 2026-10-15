using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;
using Pomp.Bridge;
using Pomp.Features;
using Pomp.Internal;
using Pomp.Json;
using Pomp.Permissions;
using Pomp.Protocol;
using Pomp.Tools;
using Pomp.Ui;

namespace Pomp.Hosting
{
    // Goal runs (docs/ui-protocol.md "Goal runs"): /goal starts a POMP Workflow and arms automatic continuation for it, optionally with a
    // token budget. The page shows a confirmation card first (typed /goal, or the model's request_goal_run); only its answer starts anything.
    public sealed partial class PompSession
    {
        internal const string GoalCommandMessage = "workspace/goal", GoalStateMessage = "workspace/goalState", GoalRequestMessage = "workspace/goalRequest";
        private const int MaxGoalChars = 16000;

        /// <summary>The conversation's model tokens so far, summed over the finished assistant messages the engine reported (see <see cref="MessageUsage"/>). Only ever grows.</summary>
        private long _usageTokens;
        private long ConversationTokens() => Interlocked.Read(ref _usageTokens);

        /// <summary>Adds the tokens of one finished assistant message, when it came from the running engine process.</summary>
        private void CountUsage(OmpFrame frame, JObject? message)
        {
            var tokens = MessageUsage.Tokens(message?["usage"]);
            if (tokens <= 0) return;
            var origin = _bridge.OriginOf(frame);
            lock (_stateLock) if (origin != null && (origin.IsClosed || !ReferenceEquals(origin, _epochConnection))) return;
            Interlocked.Add(ref _usageTokens, tokens);
        }

        // The card the model asked for (request_goal_run), kept so a page that reloads still shows it; cleared by the user's answer.
        private readonly object _goalLock = new object();
        private JObject? _goalRequest;

        private void PostGoalState(string requestId, bool ok, string stage, string? error = null) =>
            _ui.Post(GoalStateMessage, new JObject { ["requestId"] = requestId, ["ok"] = ok, ["stage"] = stage, ["error"] = error }, UiDelivery.State);

        /// <summary>The card the model asked for, for a page that just (re)loaded; nothing once it was answered or the conversation changed. Called under the hub's lock: takes only its own short locks.</summary>
        private IEnumerable<UiMessage> GoalRequestSnapshot()
        {
            JObject? request; string? session;
            lock (_goalLock) request = _goalRequest;
            lock (_stateLock) session = _info.SessionId;
            if (request != null && (string?)request["sessionId"] == session) yield return new UiMessage(GoalRequestMessage, request);
        }

        private void ClearGoalRequest(string? id)
        {
            lock (_goalLock) if (_goalRequest != null && (string.IsNullOrEmpty(id) || (string?)_goalRequest["requestId"] == id)) _goalRequest = null;
        }

        /// <summary>workspace/goal: <c>start</c> (the page's card was confirmed) or <c>cancel</c> (it was declined). Runs on the chat command queue, so the idle check and the send cannot interleave with another message.</summary>
        private async Task HandleGoalAsync(JObject args, long generation)
        {
            var requestId = args.GetString("requestId") ?? "";
            if (requestId.Length > 128) return;
            string? error = null;
            try
            {
                var action = args.GetString("action");
                if (action == "cancel") { ClearGoalRequest(args.GetString("fromRequest")); PostGoalState(requestId, true, "cancelled"); return; }
                if (action != "start") throw new ArgumentException(PompLanguage.Pick("알 수 없는 목표 명령이에요.", "Unknown goal command."));
                await StartGoalAsync(args, generation).ConfigureAwait(false);
                ClearGoalRequest(args.GetString("fromRequest"));
            }
            catch (OperationCanceledException) when (_disposeCts.IsCancellationRequested) { return; }
            catch (Exception ex) { error = ex.Message; }
            if (error == null) PostGoalState(requestId, true, "started");
            else PostGoalState(requestId, false, "refused", error);
        }

        private async Task StartGoalAsync(JObject args, long generation)
        {
            var text = (args.GetString("text") ?? "").Trim();
            if (text.Length == 0 || text.Length > MaxGoalChars) throw new ArgumentException(PompLanguage.Pick("목표가 비어 있거나 너무 길어요.", "The goal is empty or too long."));
            var budget = ComposerAutoContinue.ReadTokenBudget(args["tokenBudget"]);
            if (_composerAuto == null || _composerWorkflow == null) throw new InvalidOperationException(PompLanguage.Pick("목표 실행을 쓸 수 없어요.", "Goal runs are not available."));
            var root = await TryGetWorkspaceRootAsync().ConfigureAwait(false);
            if (string.IsNullOrWhiteSpace(root)) throw new InvalidOperationException(PompLanguage.Pick("열린 작업 영역이 없어요.", "No workspace is open."));
            string session; OmpConnection? connection;
            lock (_stateLock) { session = _sessionSwitchUncertain ? "" : _info.SessionId ?? ""; connection = _epochConnection; }
            if (string.IsNullOrWhiteSpace(session)) throw new InvalidOperationException(PompLanguage.Pick("연결된 대화가 없어요.", "No conversation is connected."));
            var wantedRoot = args.GetString("root"); var wantedSession = args.GetString("sessionId");
            if (!string.IsNullOrEmpty(wantedRoot) && !string.Equals(NormalizePath(wantedRoot), NormalizePath(root), StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException(PompLanguage.Pick("작업 영역이 바뀌었어요.", "Workspace changed."));
            if (!string.IsNullOrEmpty(wantedSession) && wantedSession != session) throw new InvalidOperationException(PompLanguage.Pick("대화가 바뀌었어요.", "Conversation changed."));
            // The same readiness automatic continuation needs before it sends: idle conversation, no pending question, no unfinished terminal work.
            if (!ComposerContinuationReady()) throw new InvalidOperationException(PompLanguage.Pick("대화가 응답 중이거나 작업이 진행 중이에요.", "A reply or other work is in progress."));
            await RequireEngineCommandsAsync(connection, _disposeCts.Token, "pomp-workflow").ConfigureAwait(false);
            var refusal = _composerAuto.BeginGoal(root!, session, budget);
            if (refusal != null) throw new InvalidOperationException(refusal);
            bool accepted;
            try
            {
                // Typed like the user would: "/goal <text>" reaches the engine as "/pomp-workflow <text>" (docs/ui-protocol.md, slash commands).
                accepted = await HandleChatSendAsync(new ChatSendPayload { Text = "/goal " + text, ClientMessageId = "goal-" + Guid.NewGuid().ToString("N"), IncludeEditorContext = false },
                    generation, default, session, bypassQueue: true, capturedPartner: new ConversationPartnerPayload(), engineText: "/pomp-workflow " + text).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _composerAuto.CancelGoal(ex.Message);
                throw;
            }
            if (!accepted)
            {
                var reason = PompLanguage.Pick("목표를 보내지 못했어요.", "The goal could not be sent.");
                _composerAuto.CancelGoal(reason);
                throw new InvalidOperationException(reason);
            }
            _composerAuto.GoalSent();
        }

        /// <summary>The model's request for a goal run: shows the same confirmation card as typing /goal, and starts nothing by itself.</summary>
        private sealed class GoalRequestTool : IHostTool
        {
            private readonly PompSession owner;
            public GoalRequestTool(PompSession owner) { this.owner = owner; }
            public HostToolDefinition Definition { get; } = new HostToolDefinition
            {
                Name = "request_goal_run", Label = "Ask the user to start a goal run", LoadMode = "essential",
                Description = "Ask the user to start a POMP goal run: an autonomous workflow on the goal you describe that continues by itself for up to six follow-up runs, optionally within a token budget. Use it when the user asks you to keep working on a larger goal on its own, or agrees to it. It only shows the goal, with an optional token budget, in a confirmation card (start / cancel); nothing starts unless the user confirms. After calling it, end your reply and wait: if the user confirms, the goal run starts as the next message. Never call it again for the same goal, and do not begin the goal's work yourself in this reply.",
                Parameters = JObject.Parse("{\"type\":\"object\",\"properties\":{\"goal\":{\"type\":\"string\",\"minLength\":1,\"maxLength\":16000,\"description\":\"The goal in the user's words: what must be true when it is done.\"},\"tokenBudget\":{\"type\":\"integer\",\"minimum\":10000,\"maximum\":50000000,\"description\":\"Optional token budget for the whole run; omit when the user gave none.\"}},\"required\":[\"goal\"],\"additionalProperties\":false}"),
            };
            public PermissionKind ResolvePermission(HostToolInvocation invocation) => PermissionKind.None;
            public Task<PermissionRequest?> DescribePermissionAsync(HostToolInvocation invocation, CancellationToken token) => Task.FromResult<PermissionRequest?>(null);
            public async Task<HostToolResult> ExecuteAsync(HostToolInvocation invocation, CancellationToken token)
            {
                try
                {
                    var args = invocation.Arguments;
                    if (args.Properties().Any(p => p.Name is not ("goal" or "tokenBudget"))) throw new ArgumentException("Unknown field.");
                    var goal = args["goal"]?.Type == JTokenType.String ? ((string)args["goal"]!).Trim() : "";
                    if (goal.Length == 0 || goal.Length > MaxGoalChars) throw new ArgumentException("The goal must be 1-16,000 characters.");
                    var budget = ComposerAutoContinue.ReadTokenBudget(args["tokenBudget"]);
                    var connection = invocation.Origin as OmpConnection;
                    string root, session;
                    lock (owner._stateLock)
                    {
                        root = owner._workingDirectory; session = owner._sessionSwitchUncertain ? "" : owner._info.SessionId ?? "";
                        if (string.IsNullOrEmpty(session) || owner._recoveryStatus != "ready" || owner._bridge.State != BridgeState.Running) throw new InvalidOperationException("The conversation is not ready.");
                        if (connection == null || connection.IsClosed || !ReferenceEquals(connection, owner._epochConnection)) throw new InvalidOperationException("The caller connection is no longer current.");
                        // Like the other cards: only while exactly one conversation turn is running (the one that asked).
                        if (owner._turns.Count != 1) throw new InvalidOperationException("Ask for a goal run during one active conversation turn.");
                    }
                    // The engine extension checks that only the main conversation calls this tool; no model-supplied caller field is trusted.
                    await owner.RequireEngineCommandsAsync(connection, token, "pomp-feedback-guard", "pomp-workflow").ConfigureAwait(false);
                    if (owner._composerWorkflow == null || owner._composerAuto == null) throw new InvalidOperationException("Goal runs are not available.");
                    var view = owner._composerWorkflow.View();
                    if (!string.IsNullOrEmpty((string?)view["id"]) && !new[] { "empty", "done", "cancelled" }.Contains((string?)view["phase"])) throw new InvalidOperationException("A POMP Workflow is already active; a goal run cannot start now.");
                    if (owner._composerAuto.GoalPending) throw new InvalidOperationException("A goal run is already starting.");
                    if (!owner._ui.IsReady) throw new InvalidOperationException("The chat window is not open, so the user cannot be asked.");
                    var request = new JObject { ["requestId"] = "goal-" + Guid.NewGuid().ToString("N"), ["root"] = root, ["sessionId"] = session, ["text"] = goal };
                    if (budget != null) request["tokenBudget"] = budget;
                    lock (owner._goalLock) owner._goalRequest = request;
                    owner._ui.Post(GoalRequestMessage, request, UiDelivery.State);
                    return HostToolResult.Text("The goal was shown to the user as a confirmation card with start and cancel. Nothing has started. End your reply now and wait: if the user confirms, the goal run starts as the next message; if they cancel, carry on as they say.", new JObject { ["requestId"] = request["requestId"] });
                }
                catch (Exception ex) when (!(ex is OperationCanceledException)) { return HostToolResult.Error(ex.Message); }
            }
        }
    }
}
