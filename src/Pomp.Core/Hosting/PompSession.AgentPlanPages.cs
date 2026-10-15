using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Pomp.Internal;
using Pomp.Ui;

namespace Pomp.Hosting
{
    // The agent's plan past the first 200 tasks of session/info: the host keeps the engine's whole plan from the last get_state and serves
    // it page by page (workspace/agentPlanPage, no engine round trip); the user changes it row by row (workspace/agentPlanEdit,
    // PompSession.AgentPlan.cs). A page and an edit name the revision of the plan they were made against.
    public sealed partial class PompSession
    {
        private const int PlanPageDefaultSize = 20, PlanPageMaxSize = 100, PlanQueryMaxChars = 200;

        // The engine's whole plan as the last applied get_state reported it (_stateLock); a snapshot is never changed once it is stored.
        private PlanSnapshot? _enginePlan;

        /// <summary>
        /// Short stable hash of the engine's whole plan (SHA-256 of the compact JSON of the raw array, first 16 hex digits). Null for no
        /// plan and for an empty one: an edit of such a plan names no revision.
        /// </summary>
        internal static string? PlanRevisionOf(JToken? raw)
        {
            if (!(raw is JArray phases) || phases.Count == 0) return null;
            using (var sha = SHA256.Create())
                return BitConverter.ToString(sha.ComputeHash(Encoding.UTF8.GetBytes(phases.ToString(Formatting.None))), 0, 8).Replace("-", "").ToLowerInvariant();
        }

        /// <summary>One task as a page lists it; the indexes address the engine's whole plan.</summary>
        private sealed class PlanRow
        {
            public int PhaseIndex, TaskIndex;
            public string PhaseName = "", Content = "", Status = "";
            public string? Blocker;
        }

        private sealed class PlanPhaseRow
        {
            public int Index, TaskCount;
            public string Name = "";
        }

        /// <summary>The engine's whole plan as one get_state reported it: the raw copy, its revision and the rows every page is cut from.</summary>
        private sealed class PlanSnapshot
        {
            public static readonly PlanSnapshot Empty = new PlanSnapshot(new JArray(), null);

            public readonly JArray Raw;
            public readonly string? Revision;
            public readonly List<PlanPhaseRow> Phases = new List<PlanPhaseRow>();
            public readonly List<PlanRow> Rows = new List<PlanRow>();

            /// <summary>Tasks that are not completed or abandoned.</summary>
            public readonly int OpenCount;

            /// <summary>Whether the strict editor reader (no count caps) takes the plan exactly as it is; read on first use.</summary>
            public readonly Lazy<bool> Editable;

            private PlanSnapshot(JArray raw, string? revision)
            {
                Raw = raw; Revision = revision;
                Editable = new Lazy<bool>(() => { try { ReadPlan(raw, userEdit: false, capped: false); return true; } catch (ArgumentException) { return false; } });
                // The normalization of session/info (display limits, known statuses only), over the whole plan; indexes are raw positions.
                for (var p = 0; p < raw.Count; p++)
                {
                    if (!(raw[p] is JObject phase)) continue;
                    var shown = new PlanPhaseRow { Index = p, Name = LimitPlanText(phase.GetString("name") ?? "", MaxPlanNameChars) };
                    Phases.Add(shown);
                    if (!(phase["tasks"] is JArray tasks)) continue;
                    for (var t = 0; t < tasks.Count; t++)
                    {
                        if (!(tasks[t] is JObject task)) continue;
                        var status = task.GetString("status");
                        if (status == null || !PlanStatuses.Contains(status)) continue;
                        var blocker = task.GetString("blocker");
                        Rows.Add(new PlanRow
                        {
                            PhaseIndex = p, TaskIndex = t, PhaseName = shown.Name, Status = status,
                            Content = LimitPlanText(task.GetString("content") ?? "", MaxPlanContentChars),
                            Blocker = blocker == null ? null : LimitPlanText(blocker, MaxPlanBlockerChars),
                        });
                        shown.TaskCount++;
                        if (status != "completed" && status != "abandoned") OpenCount++;
                    }
                }
            }

            /// <summary>Every task with a known status.</summary>
            public int TaskCount => Rows.Count;

            /// <summary>The snapshot of <paramref name="raw"/>: <paramref name="previous"/> itself when it is the same plan (its hash is the identity).</summary>
            public static PlanSnapshot Of(JArray raw, PlanSnapshot? previous)
            {
                var revision = PlanRevisionOf(raw);
                return previous != null && previous.Revision == revision && previous.Raw.Count == raw.Count ? previous : new PlanSnapshot((JArray)raw.DeepClone(), revision);
            }
        }

        /// <summary>workspace/agentPlanPage: one page of the whole plan, from the snapshot of the last get_state.</summary>
        private void HandleAgentPlanPage(JObject args)
        {
            var requestId = args.GetString("requestId");
            JObject reply;
            try { reply = PlanPage(requestId, args); }
            catch (Exception ex) { reply = new JObject { ["requestId"] = requestId, ["ok"] = false, ["error"] = TextUtil.Truncate(ex.Message, 500) }; }
            _ui.Post(UiMessageTypes.AgentPlanPageResult, reply, UiDelivery.State);
        }

        private JObject PlanPage(string? requestId, JObject args)
        {
            var session = args.GetString("sessionId") ?? "";
            PlanSnapshot? held;
            lock (_stateLock)
            {
                if (string.IsNullOrEmpty(session) || session != _info.SessionId) throw new InvalidOperationException(PompLanguage.Pick("대화가 바뀌었어요.", "The conversation changed."));
                held = _enginePlan;
            }

            var plan = held ?? PlanSnapshot.Empty;
            var size = (int)Math.Max(1, Math.Min(PlanPageMaxSize, args.GetLong("size") ?? PlanPageDefaultSize));
            var query = (args.GetString("query") ?? "").Trim();
            if (query.Length > PlanQueryMaxChars) query = query.Substring(0, PlanQueryMaxChars);
            // A task matches by its content, its blocker or the name of its phase.
            var rows = query.Length == 0 ? plan.Rows : plan.Rows.Where(row => PlanHas(row.Content, query) || PlanHas(row.Blocker, query) || PlanHas(row.PhaseName, query)).ToList();
            var page = (int)Math.Max(0, Math.Min(rows.Count == 0 ? 0 : (rows.Count - 1) / size, args.GetLong("page") ?? 0));
            var reply = new JObject
            {
                ["requestId"] = requestId, ["ok"] = true, ["revision"] = plan.Revision,
                // Read outside the state lock: the hub's lock is never taken inside it.
                ["editable"] = plan.Editable.Value && PlanEditBlock(session, null, out _) == null,
                ["page"] = page, ["size"] = size, ["total"] = rows.Count,
                ["phases"] = new JArray(plan.Phases.Select(phase => new JObject { ["index"] = phase.Index, ["name"] = phase.Name, ["taskCount"] = phase.TaskCount })),
                ["rows"] = new JArray(rows.Skip(page * size).Take(size).Select(row =>
                {
                    var item = new JObject { ["phase"] = row.PhaseIndex, ["task"] = row.TaskIndex, ["content"] = row.Content, ["status"] = row.Status };
                    if (row.Blocker != null) item["blocker"] = row.Blocker;
                    return item;
                })),
            };
            // Only the phase list is unbounded: the page channel drops a message above 2 MiB with everything queued behind it.
            if (plan.Phases.Count > 2000 && Encoding.UTF8.GetByteCount(reply.ToString(Formatting.None)) > 1536 * 1024) throw new InvalidOperationException(PompLanguage.Pick("계획이 너무 커서 표시할 수 없어요.", "The plan is too large to show."));
            return reply;
        }

        private static bool PlanHas(string? text, string query) => text != null && text.IndexOf(query, StringComparison.OrdinalIgnoreCase) >= 0;

        /// <summary>One change to the whole plan, read and checked here (texts, status, indexes being integers); the indexes are checked against the plan in <see cref="Apply"/>.</summary>
        private sealed class PlanEdit
        {
            public string Op = "";
            public int PhaseIndex, TaskIndex;
            public string? Name, Content, Status, Blocker;

            /// <summary>A blocker was sent: <see cref="Blocker"/> is its new text, or null when the empty text removed it.</summary>
            public bool BlockerGiven;

            public static PlanEdit Parse(JObject args)
            {
                var edit = new PlanEdit { Op = args.GetString("op") ?? "" };
                switch (edit.Op)
                {
                    case "addTask": case "updateTask": case "deleteTask": case "addPhase": case "renamePhase": case "deletePhase": break;
                    default: throw new ArgumentException(PompLanguage.Pick("알 수 없는 계획 편집이에요.", "Unknown plan edit."));
                }

                if (edit.Op != "addPhase") edit.PhaseIndex = ReadIndex(args, "phase");
                if (edit.Op == "updateTask" || edit.Op == "deleteTask") edit.TaskIndex = ReadIndex(args, "task");
                if (edit.Op == "addPhase" || edit.Op == "renamePhase") edit.Name = ReadText(args, "name", MaxPlanNameChars, present: true, blankOk: false);
                if (edit.Op == "addTask" || edit.Op == "addPhase" || edit.Op == "updateTask")
                {
                    edit.Content = ReadText(args, "content", MaxPlanContentChars, present: edit.Op != "updateTask", blankOk: false);
                    var status = args["status"];
                    if (status != null && status.Type != JTokenType.Null)
                    {
                        var text = status.Type == JTokenType.String ? (string?)status : null;
                        if (text == null || !PlanStatuses.Contains(text)) throw new ArgumentException(PompLanguage.Pick("알 수 없는 작업 상태예요.", "Unknown task status."));
                        edit.Status = text;
                    }

                    // The empty text removes the blocker.
                    var blocker = ReadText(args, "blocker", MaxPlanBlockerChars, present: false, blankOk: true);
                    edit.BlockerGiven = blocker != null; edit.Blocker = string.IsNullOrEmpty(blocker) ? null : blocker;
                    if (edit.Op == "updateTask" && edit.Content == null && edit.Status == null && !edit.BlockerGiven) throw new ArgumentException(PompLanguage.Pick("바꿀 내용이 없어요.", "Nothing to change."));
                }

                return edit;
            }

            /// <summary>Applies the change to <paramref name="phases"/>, the plan as the engine holds it; deleting the last task of a phase removes the phase.</summary>
            public void Apply(List<AgentPlanPhasePayload> phases)
            {
                if (Op == "addPhase")
                {
                    phases.Add(new AgentPlanPhasePayload { Name = Name!, Tasks = { NewTask() } });
                    return;
                }

                if (PhaseIndex >= phases.Count) throw Missing();
                var phase = phases[PhaseIndex];
                switch (Op)
                {
                    case "renamePhase": phase.Name = Name!; break;
                    case "deletePhase": phases.RemoveAt(PhaseIndex); break;
                    case "addTask": phase.Tasks.Add(NewTask()); break;
                    default:
                        if (TaskIndex >= phase.Tasks.Count) throw Missing();
                        if (Op == "deleteTask")
                        {
                            phase.Tasks.RemoveAt(TaskIndex);
                            if (phase.Tasks.Count == 0) phases.RemoveAt(PhaseIndex);
                            break;
                        }

                        var task = phase.Tasks[TaskIndex];
                        if (Content != null) task.Content = Content;
                        if (Status != null) task.Status = Status;
                        if (BlockerGiven) task.Blocker = Blocker;
                        break;
                }
            }

            private AgentPlanTaskPayload NewTask() => new AgentPlanTaskPayload { Content = Content!, Status = Status ?? "pending", Blocker = Blocker };

            private static ArgumentException Missing() => new ArgumentException(PompLanguage.Pick("계획에 없는 단계나 작업이에요.", "That phase or task is not in the plan."));

            private static int ReadIndex(JObject args, string key)
            {
                var index = args[key]?.Type == JTokenType.Integer ? args.GetLong(key) : null;
                if (index == null || index < 0 || index > int.MaxValue) throw Missing();
                return (int)index.Value;
            }

            /// <summary>
            /// A text field: null when absent (an error when <paramref name="present"/> says it must be there), else a string within the
            /// limit without control characters and, unless <paramref name="blankOk"/>, not blank.
            /// </summary>
            private static string? ReadText(JObject args, string key, int limit, bool present, bool blankOk)
            {
                var token = args[key];
                if (token == null || token.Type == JTokenType.Null)
                {
                    if (present) throw BadText();
                    return null;
                }

                if (token.Type != JTokenType.String) throw BadText();
                return PlanText((string?)token ?? "", limit, required: !blankOk);
            }

            private static ArgumentException BadText() => new ArgumentException(PompLanguage.Pick("계획 문구가 올바르지 않아요.", "The plan text is invalid."));
        }
    }
}
