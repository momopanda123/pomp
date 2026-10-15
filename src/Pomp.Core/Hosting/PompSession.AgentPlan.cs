using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;
using Pomp.Bridge;
using Pomp.Diagnostics;
using Pomp.Internal;
using Pomp.Protocol;
using Pomp.Ui;

namespace Pomp.Hosting
{
    // The user's edit of the agent's current plan (omp todo phases). omp (18.4.3 through 18.8.6) keeps a set_todos edit in memory only: the
    // next todo tool call builds on it (and records it), while a restart, session switch or compaction re-reads the plan from
    // the conversation. The page states this; the host only guarantees that it never overwrites a plan the user has not seen
    // (the whole-plan edit names the copy it was made from, the per-row edit the revision of the plan it was made against).
    public sealed partial class PompSession
    {
        private const int MaxPlanPhases = 20;
        private const int MaxPlanTasks = 200;
        private const int MaxPlanNameChars = 200;
        private const int MaxPlanContentChars = 600;
        private const int MaxPlanBlockerChars = 300;

        private static readonly HashSet<string> PlanStatuses = new HashSet<string>(StringComparer.Ordinal) { "pending", "in_progress", "completed", "abandoned", "blocked" };

        private async Task HandleAgentPlanAsync(JObject args)
        {
            var requestId = args.GetString("requestId");
            string? error = null;
            try
            {
                var session = args.GetString("sessionId") ?? "";
                var seen = ReadPlan(args["base"], userEdit: false);
                var phases = ReadPlan(args["phases"], userEdit: true);
                var connection = RequirePlanEditable(session);
                var epoch = CurrentEpochToken();

                // Only an exact copy of the plan the page showed may be replaced: the model could have updated it since.
                var state = await ReadIdlePlanStateAsync(connection, epoch, session).ConfigureAwait(false);
                JArray engine;
                try { engine = PlanJson(ReadPlan(state["todoPhases"] ?? new JArray(), userEdit: false)); }
                catch (ArgumentException) { throw new InvalidOperationException(PompLanguage.Pick("현재 계획에 화면에서 그대로 편집할 수 없는 항목이 있어요.", "The current plan has entries this editor cannot keep intact.")); }
                if (!JToken.DeepEquals(engine, PlanJson(seen))) throw new InvalidOperationException(PompLanguage.Pick("그 사이 계획이 바뀌었어요.", "The plan changed meanwhile."));

                await SetPlanAsync(session, connection, epoch, PlanJson(phases)).ConfigureAwait(false);
                _logger.Info("Agent plan edited by the user: " + phases.Count + " phase(s), " + phases.Sum(phase => phase.Tasks.Count) + " task(s).");
                await RefreshAfterPlanSetAsync(connection, epoch).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (_disposeCts.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                error = PlanErrorText(ex);
            }

            PostPlanResult(requestId, error);
        }

        /// <summary>
        /// One change to the whole plan (workspace/agentPlanEdit): the page names the revision of the plan it showed; any other plan
        /// is refused. Then like <see cref="HandleAgentPlanAsync"/>: the engine's plan is read strictly (no count caps here), changed
        /// and set back, and the new state is applied before the answer.
        /// </summary>
        private async Task HandleAgentPlanEditAsync(JObject args)
        {
            var requestId = args.GetString("requestId");
            string? error = null;
            try
            {
                var session = args.GetString("sessionId") ?? "";
                var edit = PlanEdit.Parse(args);
                var connection = RequirePlanEditable(session);
                var epoch = CurrentEpochToken();

                var state = await ReadIdlePlanStateAsync(connection, epoch, session).ConfigureAwait(false);
                var engine = state["todoPhases"] as JArray ?? new JArray();
                var seen = args.GetString("revision");
                if (PlanRevisionOf(engine) != (string.IsNullOrEmpty(seen) ? null : seen)) throw new InvalidOperationException(PompLanguage.Pick("그 사이 계획이 바뀌었어요.", "The plan changed meanwhile."));
                List<AgentPlanPhasePayload> phases;
                try { phases = ReadPlan(engine, userEdit: false, capped: false); }
                catch (ArgumentException) { throw new InvalidOperationException(PompLanguage.Pick("현재 계획에 화면에서 그대로 편집할 수 없는 항목이 있어요.", "The current plan has entries this editor cannot keep intact.")); }
                edit.Apply(phases);

                await SetPlanAsync(session, connection, epoch, PlanJson(phases)).ConfigureAwait(false);
                _logger.Info("Agent plan edited by the user: " + edit.Op + ", " + phases.Count + " phase(s), " + phases.Sum(phase => phase.Tasks.Count) + " task(s).");
                await RefreshAfterPlanSetAsync(connection, epoch).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (_disposeCts.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                error = PlanErrorText(ex);
            }

            PostPlanResult(requestId, error);
        }

        private static string PlanErrorText(Exception ex) =>
            ex is OperationCanceledException ? PompLanguage.Pick("연결이 바뀌어 계획을 저장하지 못했어요.", "The connection changed; the plan was not saved.") : TextUtil.Truncate(ex.Message, 500);

        private void PostPlanResult(string? requestId, string? error)
        {
            var reply = new JObject { ["requestId"] = requestId, ["ok"] = error == null };
            if (error != null) reply["error"] = error;
            _ui.Post(UiMessageTypes.AgentPlanResult, reply, UiDelivery.State);
        }

        /// <summary>The engine's state for a plan edit; refused while the agent is working or when it is another conversation.</summary>
        private static async Task<JObject> ReadIdlePlanStateAsync(OmpConnection connection, CancellationToken epoch, string session)
        {
            var current = await connection.SendCommandAsync(OmpCommands.GetState(), TimeSpan.FromSeconds(10), epoch).ConfigureAwait(false);
            if (!current.Success || current.Data is not JObject state) throw new InvalidOperationException(PompLanguage.Pick("현재 작업 계획을 읽지 못했어요.", "Could not read the current work plan."));
            if (state.GetBool("isStreaming") == true || state.GetBool("hasPendingAsyncWork") == true || state.GetBool("isSettled") == false)
                throw new InvalidOperationException(PompLanguage.Pick("에이전트가 작업 중이에요.", "The agent is working."));
            if (state.GetString("sessionId") != session) throw new InvalidOperationException(PompLanguage.Pick("대화가 바뀌었어요.", "The conversation changed."));
            return state;
        }

        /// <summary>set_todos with <paramref name="plan"/>, only while the plan is still editable through <paramref name="connection"/>.</summary>
        private async Task SetPlanAsync(string session, OmpConnection connection, CancellationToken epoch, JArray plan)
        {
            // The engine's RPC transport asks for one inbound command within about 1 MiB: a bigger plan is not sent at all.
            var command = OmpCommands.SetTodos(plan);
            if (Pomp.Transport.NdjsonWriter.Encode(command).Length > MaxPromptFrameBytes) throw new InvalidOperationException(PompLanguage.Pick("계획이 너무 커서 저장할 수 없어요.", "The plan is too large to save."));
            RequirePlanEditable(session, connection);
            var response = await connection.SendCommandAsync(command, TimeSpan.FromSeconds(10), epoch).ConfigureAwait(false);
            if (!response.Success) throw new InvalidOperationException(PompLanguage.Pick("계획 변경이 적용되지 않았어요: ", "The plan change was not applied: ") + TextUtil.Truncate(response.Error ?? "", 300));
        }

        /// <summary>Reads the state after a set_todos (the plan the engine now holds) and applies it.</summary>
        private async Task RefreshAfterPlanSetAsync(OmpConnection connection, CancellationToken epoch)
        {
            long revision; lock (_stateLock) revision = _streamingRevision;
            var after = await connection.SendCommandAsync(OmpCommands.GetState(), TimeSpan.FromSeconds(10), epoch).ConfigureAwait(false);
            // With the revision, an agent_start handled after this reply keeps its streaming state.
            if (after.Success && after.Data is JObject afterState) ApplyState(afterState, revision, currentLocked: () => ReferenceEquals(connection, _epochConnection));
            else RequestInfoRefresh();
        }

        /// <summary>The current connection when the plan may be edited now; otherwise why not.</summary>
        private OmpConnection RequirePlanEditable(string session, OmpConnection? expected = null)
        {
            var reason = PlanEditBlock(session, expected, out var connection);
            if (reason != null) throw new InvalidOperationException(reason);
            return connection!;
        }

        /// <summary>Why the plan cannot be edited now, or null (then <paramref name="connection"/> is the one to edit through).</summary>
        private string? PlanEditBlock(string session, OmpConnection? expected, out OmpConnection? connection)
        {
            connection = null;
            // Read before the state lock: the hub's lock is never taken inside it.
            if (_ui.PendingRequestCount > 0) return PompLanguage.Pick("대기 중인 승인이나 질문이 있어요.", "An approval or question is pending.");
            if (_dispatcher.ActiveCount > 0) return PompLanguage.Pick("도구가 실행 중이에요.", "A tool is running.");
            lock (_stateLock)
            {
                if (string.IsNullOrEmpty(session) || session != _info.SessionId || _sessionSwitchUncertain || _recoveryStatus != "ready")
                    return PompLanguage.Pick("대화가 바뀌었거나 복원 중이에요.", "The conversation changed or is recovering.");
                if (_bridge.State != BridgeState.Running || _epochConnection == null || (expected != null && !ReferenceEquals(expected, _epochConnection)))
                    return PompLanguage.Pick("POMP가 연결되지 않았어요.", "POMP is not connected.");
                if (_turns.Count > 0 || _info.IsStreaming || _info.HasPendingAsyncWork)
                    return PompLanguage.Pick("에이전트가 작업 중이에요.", "The agent is working.");
                if (_chatQueue.Count > 0 && !_queuePaused)
                    return PompLanguage.Pick("대기열의 메시지가 곧 실행돼요.", "Queued messages are about to run.");
                connection = _epochConnection;
                return null;
            }
        }

        /// <summary>
        /// Strict plan reader: the display bounds and statuses, no control characters, no unknown fields. Without
        /// <paramref name="userEdit"/> the plan is kept exactly (the engine's plan, or the copy the page showed); a user edit also
        /// needs a non-blank name and content and at least one task per phase, and an empty blocker means none. Without
        /// <paramref name="capped"/> there is no limit on the number of phases and tasks (the per-row edits).
        /// </summary>
        private static List<AgentPlanPhasePayload> ReadPlan(JToken? token, bool userEdit, bool capped = true)
        {
            if (token is not JArray rows || (capped && rows.Count > MaxPlanPhases)) throw new ArgumentException(PompLanguage.Pick("단계가 너무 많아요.", "Too many phases."));
            var phases = new List<AgentPlanPhasePayload>(); var total = 0;
            foreach (var row in rows)
            {
                if (row is not JObject phase || phase.Properties().Any(p => p.Name != "name" && p.Name != "tasks") || phase["name"]?.Type != JTokenType.String || phase["tasks"] is not JArray tasks)
                    throw new ArgumentException(PompLanguage.Pick("계획 단계 형식이 올바르지 않아요.", "The plan phase is invalid."));
                var name = PlanText((string)phase["name"]!, MaxPlanNameChars, required: userEdit);
                if (tasks.Count == 0 && userEdit) throw new ArgumentException(PompLanguage.Pick("단계가 비어 있어요.", "A phase is empty."));
                var shown = new AgentPlanPhasePayload { Name = name };
                foreach (var item in tasks)
                {
                    if (capped && ++total > MaxPlanTasks) throw new ArgumentException(PompLanguage.Pick("작업이 너무 많아요.", "Too many tasks."));
                    if (item is not JObject task || task.Properties().Any(p => p.Name != "content" && p.Name != "status" && p.Name != "blocker")
                        || task["content"]?.Type != JTokenType.String || task["status"]?.Type != JTokenType.String
                        || (task["blocker"] != null && task["blocker"]!.Type != JTokenType.String && task["blocker"]!.Type != JTokenType.Null))
                        throw new ArgumentException(PompLanguage.Pick("작업 형식이 올바르지 않아요.", "The plan task is invalid."));
                    var status = (string)task["status"]!;
                    if (!PlanStatuses.Contains(status)) throw new ArgumentException(PompLanguage.Pick("알 수 없는 작업 상태예요.", "Unknown task status."));
                    var blocker = task["blocker"]?.Type == JTokenType.String ? PlanText((string)task["blocker"]!, MaxPlanBlockerChars, required: false) : null;
                    shown.Tasks.Add(new AgentPlanTaskPayload { Content = PlanText((string)task["content"]!, MaxPlanContentChars, required: userEdit), Status = status, Blocker = userEdit && string.IsNullOrEmpty(blocker) ? null : blocker });
                }

                phases.Add(shown);
            }

            return phases;
        }

        private static string PlanText(string text, int limit, bool required)
        {
            if (text.Length > limit || text.Any(char.IsControl) || (required && text.Trim().Length == 0))
                throw new ArgumentException(PompLanguage.Pick("계획 문구가 올바르지 않아요.", "The plan text is invalid."));
            return text;
        }

        /// <summary>The engine's own shape (omp drops unknown fields and omits a missing blocker).</summary>
        private static JArray PlanJson(IEnumerable<AgentPlanPhasePayload> phases) => new JArray(phases.Select(phase => new JObject
        {
            ["name"] = phase.Name,
            ["tasks"] = new JArray(phase.Tasks.Select(task =>
            {
                var row = new JObject { ["content"] = task.Content, ["status"] = task.Status };
                if (task.Blocker != null) row["blocker"] = task.Blocker;
                return row;
            })),
        }));
    }
}
