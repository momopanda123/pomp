using System;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Pomp.Adapters;
using Pomp.Hosting;
using Pomp.Internal;
using Pomp.Protocol;
using Pomp.Ui;
namespace Pomp.Features
{
    /// <summary>Observes actual omp task agents and supports negotiated, generation-bound controls; never spawns jobs or invents progress percentages.</summary>
    internal sealed class BackgroundAgents : IDisposable
    {
        public const string StateMessage = "workspace/backgroundState", CommandMessage = "workspace/background";
        private readonly Func<JObject, CancellationToken, Task<ResponseFrame>> rpc;
        private readonly IPompHostEnvironment environment;
        private readonly UiHub ui;
        private readonly object gate = new object();
        private readonly SemaphoreSlim operation = new SemaphoreSlim(1, 1);
        private readonly Timer timer;
        private readonly CancellationTokenSource lifetime = new CancellationTokenSource();
        private CancellationTokenSource generation = new CancellationTokenSource();
        private string session = "", root = "", status = "disconnected", message = "";
        private JArray jobs = new JArray();
        private bool connected, subscribed, disposed, controlPending;
        private int controlVersion;
        private string controlMessage = "", controlRequestId = "";
        private readonly System.Collections.Generic.List<JObject> endedJobs = new System.Collections.Generic.List<JObject>();
        private readonly System.Collections.Generic.HashSet<string> pendingEnded = new System.Collections.Generic.HashSet<string>(StringComparer.Ordinal);
        private readonly SemaphoreSlim controls = new SemaphoreSlim(1, 1);
        private readonly System.Collections.Generic.HashSet<string> detailedJobs = new System.Collections.Generic.HashSet<string>(StringComparer.Ordinal);
        private readonly System.Collections.Generic.Dictionary<string, int> detailAttempts = new System.Collections.Generic.Dictionary<string, int>(StringComparer.Ordinal);
        private int scheduled;
        private bool notificationBaseline;
        private string lastSummary = "";
        private string? currentTurnTeam, currentTurnPrompt;
        private readonly System.Collections.Generic.Dictionary<string, string?> jobPrompts = new System.Collections.Generic.Dictionary<string, string?>(StringComparer.Ordinal);
        public void SetCurrentTurnPrompt(string? promptId) { lock (gate) currentTurnPrompt = SafePromptId(promptId); }
        /// <summary>Stopping one member through POMP's extension command (docs/agent-teams.md §14); set by the session, which knows the engine's commands.</summary>
        public Func<string, CancellationToken, Task>? StopMember { get; set; }
        private bool stopAvailable;
        /// <summary>Whether the engine currently lists the stop command; called outside the session's state lock.</summary>
        public void SetStopAvailable(bool value) { lock (gate) { if (disposed || stopAvailable == value) return; stopAvailable = value; } Publish(); }
        private static bool Stoppable(string? status) => status is "running" or "pending" or "queued" or "inProgress";
        private static string? SafePromptId(string? value) => string.IsNullOrWhiteSpace(value) || value!.Length > 128 || value.Any(char.IsControl) ? null : value;
        private void AssignJobPrompt(JObject job)
        {
            var id = (string)job["id"]!;
            if (!jobPrompts.TryGetValue(id, out var prompt)) { prompt = jobPrompts.Count < 1000 ? currentTurnPrompt : null; if (jobPrompts.Count < 1000) jobPrompts[id] = prompt; }
            job["promptId"] = prompt == null ? JValue.CreateNull() : new JValue(prompt);
        }
        private readonly System.Collections.Generic.HashSet<string> observedJobs = new System.Collections.Generic.HashSet<string>(StringComparer.Ordinal);
        /// <summary>Called outside gate with a detached, allowlisted job snapshot; new ids are reported once per engine session.</summary>
        public Action<string, JObject, bool>? JobObserved { get; set; }
        private void ReportJobs(string sessionId, System.Collections.Generic.IEnumerable<JObject> values, bool allowNew)
        {
            var reports = new System.Collections.Generic.List<(JObject job, bool fresh)>();
            lock (gate)
            {
                if (disposed || !connected || session != sessionId) return;
                foreach (var job in values) reports.Add(((JObject)job.DeepClone(), observedJobs.Add((string)job["id"]!) && allowNew));
            }
            foreach (var report in reports) JobObserved?.Invoke(sessionId, report.job, report.fresh);
        }
        private readonly System.Collections.Generic.Dictionary<string, string> jobTeams = new System.Collections.Generic.Dictionary<string, string>(StringComparer.Ordinal);
        public BackgroundAgents(Func<JObject, CancellationToken, Task<ResponseFrame>> rpc, IPompHostEnvironment environment, UiHub ui)
        {
            this.rpc = rpc; this.environment = environment; this.ui = ui;
            timer = new Timer(_ => { Interlocked.Exchange(ref scheduled, 0); RefreshAsync().Observe(); }, null, Timeout.Infinite, Timeout.Infinite);
        }
        public void SetCurrentTurnTeam(string? team)
        {
            lock (gate) { currentTurnTeam = string.IsNullOrWhiteSpace(team) ? null : team!.Trim(); }
        }
        public string? CurrentTurnTeam
        {
            get { lock (gate) return currentTurnTeam; }
        }
        private string? GetTeamForJob(string id)
        {
            lock (gate)
            {
                if (jobTeams.TryGetValue(id, out var t)) return t;
                if (!string.IsNullOrEmpty(currentTurnTeam))
                {
                    jobTeams[id] = currentTurnTeam!;
                    return currentTurnTeam;
                }
                return null;
            }
        }
        public static JArray Project(JArray values, bool includeControls = false) => Project(values, includeControls, null);
        /// <summary>The jobs a list holds, by their JSON size: no count limit, and the archive file (512 KiB) always takes them.</summary>
        private const int MaxJobsBytes = 448 * 1024;
        private static long Size(JToken token) => System.Text.Encoding.UTF8.GetByteCount(token.ToString(Formatting.None)) + 1;
        /// <summary>The oldest entries (the front) leave until the list fits <see cref="MaxJobsBytes"/>; each one leaving is passed to <paramref name="leaving"/>.</summary>
        private static void TrimOldest<T>(System.Collections.Generic.IList<T> list, Action<T>? leaving = null) where T : JToken
        {
            long total = 0; foreach (var item in list) total += Size(item);
            while (list.Count > 0 && total > MaxJobsBytes) { total -= Size(list[0]); leaving?.Invoke(list[0]); list.RemoveAt(0); }
        }
        public static JArray Project(JArray values, bool includeControls, Func<string, string?>? getTeam) => Project(values, includeControls, getTeam, out _);
        /// <summary>The jobs as the page shows them, in the engine's order while they fit <see cref="MaxJobsBytes"/>; <paramref name="dropped"/> counts the rest.</summary>
        public static JArray Project(JArray values, bool includeControls, Func<string, string?>? getTeam, out int dropped)
        {
            var result = new JArray(); long total = 0; dropped = 0;
            foreach (var value in values.OfType<JObject>())
            {
                var id = value["id"]?.Type == JTokenType.String ? (string?)value["id"] : null; if (string.IsNullOrWhiteSpace(id) || id!.Length > 256 || id.Any(char.IsControl) || result.Any(j => (string?)j["id"] == id)) continue;
                string B(string key, int cap) { var text = value[key]?.Type == JTokenType.String ? (string?)value[key] ?? "" : ""; return text.Length > cap ? text.Substring(0, cap) : text; }
                var job = new JObject { ["id"] = id, ["agent"] = B("agent",120), ["description"] = B("description",500), ["status"] = B("status",40), ["lastUpdate"] = long.TryParse(value["lastUpdate"]?.ToString(), out var stamp) && stamp >= 0 && stamp <= 9007199254740991 ? (long?)stamp : null };
                var final = B("result", 8000); job["result"] = final.Length == 0 ? JValue.CreateNull() : new JValue(final);
                var prompt = SafePromptId(B("promptId", 129)); job["promptId"] = prompt == null ? JValue.CreateNull() : new JValue(prompt);
                var control = value["pompControl"] as JObject;
                var controlState = (string?)control?["state"]; var controlGeneration = (string?)control?["generation"];
                if (includeControls && control?["cooperative"]?.Type == JTokenType.Boolean && (bool)control["cooperative"]! &&
                    (controlState == "running" || controlState == "pauseRequested") && Guid.TryParse(controlGeneration, out _))
                    job["control"] = new JObject { ["generation"] = controlGeneration, ["state"] = controlState, ["cooperative"] = true };

                var parentTool = B("parentToolCallId", 256);
                if (!string.IsNullOrEmpty(parentTool) && !parentTool.Any(char.IsControl))
                    job["parentToolCallId"] = parentTool;

                var teamVal = B("team", 40);
                if (string.IsNullOrEmpty(teamVal) && getTeam != null) teamVal = getTeam(id) ?? "";
                if (!string.IsNullOrEmpty(teamVal) && !teamVal.Any(char.IsControl))
                    job["team"] = teamVal;

                if (value["progress"] is JObject p)
                {
                    // Engine data: only plain strings are read (an object where a name is expected must not fail the whole list).
                    static string? Text(JToken? token) => token is JValue { Type: JTokenType.String } text ? (string?)text : null;
                    var progressObj = new JObject();
                    progressObj["toolCount"] = long.TryParse(p["toolCount"]?.ToString(), out var tc) && tc >= 0 ? tc : 0;
                    progressObj["tokens"] = long.TryParse(p["tokens"]?.ToString(), out var tok) && tok >= 0 ? (long?)tok : null;
                    progressObj["cost"] = double.TryParse(p["cost"]?.ToString(), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var c) && c >= 0 ? (double?)c : null;
                    progressObj["durationMs"] = long.TryParse(p["durationMs"]?.ToString(), out var dur) && dur >= 0 ? (long?)dur : null;

                    var toolsArray = new JArray();
                    if (p["recentTools"] is JArray rtools)
                    {
                        foreach (var t in rtools.Take(5))
                        {
                            string? toolName = null;
                            toolName = Text(t) ?? (t is JObject tobj ? Text(tobj["tool"]) ?? Text(tobj["name"]) : null);
                            if (!string.IsNullOrWhiteSpace(toolName) && !toolName!.Any(char.IsControl))
                                toolsArray.Add(toolName!.Length > 80 ? toolName!.Substring(0, 80) : toolName);
                        }
                    }
                    progressObj["recentTools"] = toolsArray;

                    var resolvedModel = Text(p["resolvedModel"]) ?? Text(p["model"]);
                    if (!string.IsNullOrWhiteSpace(resolvedModel) && !resolvedModel!.Any(char.IsControl))
                        progressObj["resolvedModel"] = resolvedModel!.Length > 120 ? resolvedModel!.Substring(0, 120) : resolvedModel;

                    var thinkingLevel = Text(p["thinkingLevel"]) ?? Text(p["resolvedThinkingLevel"]);
                    if (!string.IsNullOrWhiteSpace(thinkingLevel) && !thinkingLevel!.Any(char.IsControl))
                        progressObj["thinkingLevel"] = thinkingLevel!.Length > 40 ? thinkingLevel!.Substring(0, 40) : thinkingLevel;

                    job["progress"] = progressObj;
                }

                var size = Size(job);
                if (dropped > 0 || total + size > MaxJobsBytes) { dropped++; continue; }
                total += size; result.Add(job);
            }
            return result;
        }
        public async Task InitializeAsync()
        {
            try
            {
                var workspace = await environment.GetWorkspaceRootAsync(lifetime.Token).ConfigureAwait(false);
                lock (gate) { if (!disposed && !connected && root.Length == 0) root = workspace; }
                Schedule();
            }
            catch (OperationCanceledException) { }
        }
        public void SetSession(string workspaceRoot, string sessionId)
        {
            lock (gate)
            {
                if (disposed || (root == workspaceRoot && session == sessionId)) return;
                generation.Cancel(); generation.Dispose(); generation = new CancellationTokenSource();
                root = workspaceRoot; session = sessionId; jobs = new JArray(); endedJobs.Clear(); pendingEnded.Clear(); detailedJobs.Clear(); detailAttempts.Clear(); controlVersion = 0; controlMessage = ""; controlRequestId = ""; notificationBaseline = false; lastSummary = ""; status = connected ? "loading" : "disconnected"; message = ""; currentTurnTeam = null; jobTeams.Clear(); observedJobs.Clear(); jobPrompts.Clear(); currentTurnPrompt = null;
            }
            ResetNativeStatus();
            Schedule();
        }
        public void Connect() { lock (gate) { if (disposed || connected) return; generation.Cancel(); generation.Dispose(); generation = new CancellationTokenSource(); connected = true; subscribed = false; controlVersion = 0; jobs = new JArray(); endedJobs.Clear(); pendingEnded.Clear(); detailedJobs.Clear(); detailAttempts.Clear(); observedJobs.Clear(); jobTeams.Clear(); jobPrompts.Clear(); currentTurnPrompt = null; notificationBaseline = false; lastSummary = ""; } Schedule(); }
        public void Disconnect()
        {
            lock (gate) { connected = false; subscribed = false; controlVersion = 0; pendingEnded.Clear(); generation.Cancel(); generation.Dispose(); generation = new CancellationTokenSource(); status = "archived"; message = PompLanguage.Pick("연결이 끊겼어요.", "Disconnected."); }
            ResetNativeStatus();
            Publish();
        }
        /// <summary>Takes POMP's text off the IDE status bar (an empty summary clears only text POMP itself put there).</summary>
        private void ResetNativeStatus()
        {
            if (!(environment is IBackgroundTaskNotifications notifications)) return;
            CancellationToken token; lock (gate) { if (disposed) return; token = generation.Token; lastSummary = ""; }
            notifications.UpdateBackgroundTasksAsync(string.Empty, string.Empty, token).Observe();
        }
        private static string DescribeRunning(int running, int observed)
        {
            if (observed == 0) return string.Empty;
            return PompLanguage.IsKorean
                ? "POMP: 에이전트 " + running + "개 실행 중 (관찰 " + observed + "개)"
                : "POMP: " + running + " agent(s) reported running, " + observed + " observed";
        }
        public void ObserveLifecycle(JObject payload)
        {
            var eventState = (string?)payload["status"]; var id = (string?)payload["id"];
            if (string.IsNullOrWhiteSpace(id) || id!.Length > 256 || id.Any(char.IsControl)) return;
            string current; JObject? shown;
            lock (gate)
            {
                if (disposed || !connected || session.Length == 0) return;
                current = session;
                if (eventState == "started")
                {
                    endedJobs.RemoveAll(job => (string?)job["id"] == id); pendingEnded.Remove(id); detailedJobs.Remove(id); detailAttempts.Remove(id);
                    if (!string.IsNullOrEmpty(currentTurnTeam)) jobTeams[id] = currentTurnTeam!;
                }
                else if (eventState != "completed" && eventState != "failed" && eventState != "aborted" && eventState != "cancelled") return;
                var prior = jobs.OfType<JObject>().FirstOrDefault(j => (string?)j["id"] == id);
                var merged = prior == null ? new JObject() : (JObject)prior.DeepClone();
                merged.Merge(payload, new JsonMergeSettings { MergeArrayHandling = MergeArrayHandling.Replace });
                shown = Project(new JArray(merged), false, GetTeamForJob).OfType<JObject>().FirstOrDefault(); if (shown == null) return;
                AssignJobPrompt(shown);
                if (eventState == "started") shown["status"] = "running";
                else
                {
                    var terminal = endedJobs.FirstOrDefault(job => (string?)job["id"] == id);
                    if (terminal == null) { pendingEnded.Add(id); endedJobs.Add((JObject)shown.DeepClone()); }
                    else endedJobs[endedJobs.IndexOf(terminal)] = (JObject)shown.DeepClone();
                    TrimOldest(endedJobs, old => pendingEnded.Remove((string)old["id"]!));
                }
                prior?.Remove(); jobs.Add(shown.DeepClone()); TrimOldest(jobs);
            }
            ReportJobs(current, new[] { shown }, eventState == "started"); Publish(); Schedule();
        }
        public void ObserveProgress(JObject payload)
        {
            string current; JObject? shown;
            lock (gate)
            {
                if (disposed || !connected) return; current = session;
                var id = (string?)payload["id"] ?? (string?)payload["subagentId"];
                var prior = jobs.OfType<JObject>().FirstOrDefault(j => (string?)j["id"] == id);
                if (prior == null) { Schedule(); return; }
                var merged = (JObject)prior.DeepClone();
                var progress = merged["progress"] as JObject ?? new JObject();
                progress.Merge(payload["progress"] as JObject ?? payload, new JsonMergeSettings { MergeArrayHandling = MergeArrayHandling.Replace });
                merged["progress"] = progress;
                shown = Project(new JArray(merged), false, GetTeamForJob).OfType<JObject>().FirstOrDefault();
                if (shown != null)
                {
                    prior.Replace(shown.DeepClone());
                    var terminal = endedJobs.FirstOrDefault(job => (string?)job["id"] == id);
                    if (terminal != null) endedJobs[endedJobs.IndexOf(terminal)] = (JObject)shown.DeepClone();
                }
            }
            if (shown != null) { ReportJobs(current, new[] { shown }, false); Publish(); }
            Schedule();
        }
        public async Task HandleAsync(JObject args)
        {
            var action = (string?)args["action"];
            if (action == "refresh") { Schedule(); return; }
            if (action != "pause" && action != "resume" && action != "cancel" && action != "stop") return;
            if (!await controls.WaitAsync(0).ConfigureAwait(false)) return;
            CancellationToken token; string currentSession;
            lock (gate) { token = generation.Token; currentSession = session; }
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(token, lifetime.Token); linked.CancelAfter(TimeSpan.FromSeconds(15));
            try
            {
                string id, identity, agent = "";
                var stop = action == "stop";
                lock (gate)
                {
                    // The request id first: a refused request is answered too, instead of leaving the page to time out.
                    var requestId = (string?)args["requestId"] ?? "";
                    if (requestId.Length > 128) throw new ArgumentException(PompLanguage.Pick("제어 요청 ID가 너무 길어요.", "Control request ID is too long."));
                    controlRequestId = requestId;
                    var available = stop ? stopAvailable && StopMember != null : controlVersion == 1;
                    if (!connected || status != "live" || !available || (string?)args["sessionId"] != session || !string.Equals((string?)args["root"], root, StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException(PompLanguage.Pick("지금은 제어할 수 없거나 대화가 바뀌었어요.", "Controls are unavailable or the conversation changed."));
                    id = (string?)args["id"] ?? ""; identity = (string?)args["generation"] ?? "";
                    var job = jobs.OfType<JObject>().FirstOrDefault(row => (string?)row["id"] == id);
                    if (stop)
                    {
                        // The extension checks again in the engine (running, started by this conversation); this only spares a pointless call.
                        if (job == null || !Stoppable((string?)job["status"])) throw new InvalidOperationException(PompLanguage.Pick("이 구성원은 실행 중이 아니에요.", "This member is not running."));
                        agent = (string?)job["agent"] ?? id;
                    }
                    else if (job?["control"] == null || (string?)job["control"]?["generation"] != identity) throw new InvalidOperationException(PompLanguage.Pick("작업 세대가 바뀌었어요.", "Task generation changed."));
                    controlPending = true; controlMessage = stop ? PompLanguage.Pick("구성원 " + agent + "을(를) 멈추고 있어요", "Stopping member " + agent) : PompLanguage.Pick(action + " 요청을 보내고 있어요", "Sending " + action);
                }
                Publish();
                if (stop)
                {
                    await StopMember!(id, linked.Token).ConfigureAwait(false);
                    lock (gate) { linked.Token.ThrowIfCancellationRequested(); controlMessage = PompLanguage.Pick("구성원 " + agent + "만 멈췄어요.", "Stopped member " + agent + " only."); }
                    return;
                }
                var response = await rpc(new JObject { ["type"] = "pomp_control_subagent", ["sessionId"] = currentSession, ["subagentId"] = id, ["generation"] = identity, ["action"] = action }, linked.Token).WithCancellation(linked.Token).ConfigureAwait(false);
                linked.Token.ThrowIfCancellationRequested();
                if (!response.Success) throw new InvalidOperationException(response.Error ?? PompLanguage.Pick("작업 제어 요청이 거부됐어요.", "The task control request was rejected."));
                if ((string?)response.Data?["subagentId"] != id || (string?)response.Data?["generation"] != identity || (string?)response.Data?["action"] != action) throw new InvalidOperationException(PompLanguage.Pick("제어 응답이 요청한 작업과 일치하지 않아요. 상태는 확인되지 않았어요.", "The control reply did not match the requested task; state is unconfirmed."));
                lock (gate) { linked.Token.ThrowIfCancellationRequested(); controlMessage = action == "cancel" ? PompLanguage.Pick("취소를 요청했어요.", "Cancellation requested.") : action == "pause" ? PompLanguage.Pick("일시 중지를 요청했어요.", "Pause requested.") : PompLanguage.Pick("재개 응답을 받았어요.", "Resume acknowledged."); }
            }
            catch (OperationCanceledException) { lock (gate) { if (!token.IsCancellationRequested) controlMessage = PompLanguage.Pick("제어 응답 시간이 초과됐어요. 결과는 확인되지 않았어요.", "Control response timed out. The outcome is unconfirmed."); } }
            catch (Exception ex) { lock (gate) { if (!token.IsCancellationRequested) controlMessage = ex.Message.Length > 3000 ? ex.Message.Substring(0,3000) : ex.Message; } }
            finally { lock (gate) controlPending = false; controls.Release(); Publish(); Schedule(); }
        }
        public void Schedule()
        {
            lock (gate) { if (disposed) return; }
            if (Interlocked.Exchange(ref scheduled,1) != 0) return;
            try { timer.Change(1000, Timeout.Infinite); } catch (ObjectDisposedException) { }
        }
        public JObject View() { lock (gate) return new JObject { ["root"] = root, ["sessionId"] = session, ["state"] = status, ["message"] = message, ["controlVersion"] = controlVersion, ["stopAvailable"] = stopAvailable, ["controlPending"] = controlPending, ["controlMessage"] = controlMessage, ["controlRequestId"] = controlRequestId, ["jobs"] = jobs.DeepClone() }; }
        public void Publish() => ui.Post(StateMessage, View(), UiDelivery.State);
        private string FilePath(string workspace) => ProjectData.File(workspace, "background-agents.json", Path.Combine(environment.DataDirectory, "background-agents", ContentHash.Compute(workspace) + ".json"));
        /// <summary>
        /// Takes the first job snapshot (with the progress subscription) before a prompt goes out. Jobs in that baseline are never
        /// attributed to a turn, so an agent a prompt starts must come after it to be reported as new and remembered for its
        /// partner tab or team. Bounded: when the engine is slow the prompt goes out without it, as before.
        /// </summary>
        public async Task EnsureBaselineAsync(TimeSpan budget, CancellationToken token)
        {
            using var limit = CancellationTokenSource.CreateLinkedTokenSource(token, lifetime.Token);
            limit.CancelAfter(budget);
            try
            {
                for (var attempt = 0; attempt < 3; attempt++)
                {
                    lock (gate) if (disposed || !connected || root.Length == 0 || notificationBaseline) return;
                    // The connect-time refresh may be running: wait for it, then take one here if it did not set the baseline.
                    await operation.WaitAsync(limit.Token).ConfigureAwait(false); operation.Release();
                    lock (gate) if (disposed || !connected || notificationBaseline) return;
                    await RefreshAsync().WithCancellation(limit.Token).ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException) { /* the caller's own checks decide what a cancellation means for its prompt */ }
        }
        public async Task RefreshAsync()
        {
            if (lifetime.IsCancellationRequested) return;
            if (!await operation.WaitAsync(0).ConfigureAwait(false)) { Schedule(); return; }
            CancellationToken token; string workspace, currentSession; bool live;
            lock (gate) { token = generation.Token; workspace = root; currentSession = session; live = connected; }
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(token, lifetime.Token);
            try
            {
                if (string.IsNullOrWhiteSpace(workspace)) return;
                if (!live)
                {
                    var cached = await Task.Run(() => Load(workspace), linked.Token).ConfigureAwait(false); linked.Token.ThrowIfCancellationRequested();
                    lock (gate) { linked.Token.ThrowIfCancellationRequested(); jobs = cached; status = "archived"; message = PompLanguage.Pick("이전 기록", "Earlier record"); }
                    return;
                }
                bool subscribe; lock (gate) subscribe = !subscribed;
                if (subscribe)
                {
                    var result = await rpc(new JObject { ["type"] = "set_subagent_subscription", ["level"] = "progress" }, linked.Token).ConfigureAwait(false);
                    if (!result.Success) throw new InvalidOperationException(result.Error ?? PompLanguage.Pick("작업 진행 상황 구독을 사용할 수 없어요.", "Task progress subscription is unavailable."));
                    lock (gate) { linked.Token.ThrowIfCancellationRequested(); subscribed = true; }
                }
                var response = await rpc(new JObject { ["type"] = "get_subagents" }, linked.Token).ConfigureAwait(false);
                if (!response.Success || !(response.Data?["subagents"] is JArray values)) throw new InvalidOperationException(response.Error ?? PompLanguage.Pick("에이전트 작업 스냅샷을 사용할 수 없어요.", "Agent task snapshots are unavailable."));
                var version = (int?)response.Data?["pompControlVersion"] == 1 ? 1 : 0;
                var projected = Project(values, version == 1, GetTeamForJob, out var dropped); linked.Token.ThrowIfCancellationRequested();
                lock (gate)
                {
                    linked.Token.ThrowIfCancellationRequested(); controlVersion = version;
                    foreach (var job in projected.OfType<JObject>()) AssignJobPrompt(job);
                    foreach (var ended in endedJobs.AsEnumerable().Reverse())
                    {
                        var old = projected.FirstOrDefault(job => (string?)job["id"] == (string?)ended["id"]); old?.Remove();
                        if (projected.Sum(job => Size(job)) + Size(ended) <= MaxJobsBytes) projected.Add(ended.DeepClone());
                    }
                }
                var terminalJobs = projected.OfType<JObject>().Where(j => (string?)j["status"] is "completed" or "failed" or "aborted" or "cancelled").ToArray();
                var pendingDetails = terminalJobs.Where(j => { lock (gate) return !detailedJobs.Contains((string)j["id"]!) && (!detailAttempts.TryGetValue((string)j["id"]!, out var attempts) || attempts < 3); }).ToArray();
                foreach (var job in pendingDetails.Take(5))
                {
                    linked.Token.ThrowIfCancellationRequested();
                    lock (gate) { var id = (string)job["id"]!; detailAttempts[id] = detailAttempts.TryGetValue(id, out var count) ? count + 1 : 1; }
                    try
                    {
                        using var detailLimit = CancellationTokenSource.CreateLinkedTokenSource(linked.Token); detailLimit.CancelAfter(TimeSpan.FromSeconds(5));
                        var detail = await rpc(new JObject { ["type"] = "get_subagent_messages", ["subagentId"] = job["id"] }, detailLimit.Token).ConfigureAwait(false);
                        if (!detail.Success || detail.Data?["messages"] is not JArray messages) continue;
                        var final = MemberTranscript.Final(messages);
                        if (final.result != null) job["result"] = final.result;
                        if (final.tokens.HasValue)
                        {
                            var progress = job["progress"] as JObject ?? new JObject(); job["progress"] = progress; progress["tokens"] = final.tokens.Value;
                        }
                        lock (gate)
                        {
                            linked.Token.ThrowIfCancellationRequested();
                            if (final.result != null || final.tokens.HasValue || messages.Count > 0) detailedJobs.Add((string)job["id"]!);
                            var ended = endedJobs.FirstOrDefault(j => (string?)j["id"] == (string?)job["id"]);
                            if (ended != null) endedJobs[endedJobs.IndexOf(ended)] = (JObject)job.DeepClone();
                            else { endedJobs.Add((JObject)job.DeepClone()); TrimOldest(endedJobs); }
                        }
                    }
                    catch (OperationCanceledException) when (!linked.IsCancellationRequested) { }
                    catch (Exception ex) when (!(ex is OperationCanceledException)) { /* Keep reported progress when final messages are unavailable. */ }
                }
                lock (gate) if (pendingDetails.Any(j => !detailedJobs.Contains((string)j["id"]!) && (!detailAttempts.TryGetValue((string)j["id"]!, out var attempts) || attempts < 3)) || pendingDetails.Length > 5) Schedule();
                string? notice = null; string summary; bool changed; bool allowNew;
                lock (gate) allowNew = notificationBaseline;
                lock (gate)
                {
                    linked.Token.ThrowIfCancellationRequested();
                    bool Terminal(string value) => value == "completed" || value == "failed" || value == "cancelled" || value == "aborted";
                    var ended = notificationBaseline ? projected.OfType<JObject>().Where(job => Terminal((string?)job["status"] ?? "") && jobs.Any(old => (string?)old["id"] == (string?)job["id"] && !Terminal((string?)old["status"] ?? ""))).ToArray() : Array.Empty<JObject>();
                    var endedCount = ended.Select(job => (string)job["id"]!).Concat(pendingEnded).Distinct(StringComparer.Ordinal).Count(); pendingEnded.Clear();
                    if (endedCount > 0) notice = PompLanguage.IsKorean
                        ? "POMP: 에이전트 작업 " + endedCount + "개가 끝났어요."
                        : "POMP: " + endedCount + " agent task(s) ended.";
                    var running = projected.Count(job => (string?)job["status"] == "running");
                    summary = DescribeRunning(running, projected.Count);
                    changed = summary != lastSummary; lastSummary = summary; notificationBaseline = true;
                }

                lock (gate) { linked.Token.ThrowIfCancellationRequested(); jobs = projected; status = "live"; message = dropped > 0 ? PompLanguage.Pick(dropped + "개 작업은 목록이 너무 커서 표시하지 않아요.", dropped + " tasks are not shown: the list is too large.") : ""; }
                ReportJobs(currentSession, projected.OfType<JObject>(), allowNew);
                if ((changed || notice != null) && environment is IBackgroundTaskNotifications notifications)
                {
                    try { await notifications.UpdateBackgroundTasksAsync(summary, notice, token).ConfigureAwait(false); }
                    catch (Exception ex) when (!(ex is OperationCanceledException)) { /* Native presentation must not turn a fresh snapshot into an RPC failure. */ }
                }
                try { await Task.Run(() => Save(workspace, currentSession, projected), linked.Token).ConfigureAwait(false); }
                catch (Exception ex) when (!(ex is OperationCanceledException)) { lock (gate) { if (!linked.IsCancellationRequested) message += PompLanguage.Pick(" · 메타데이터 저장 실패 · " + ex.Message, " · Metadata save failed · " + ex.Message); } }
            }
            catch (OperationCanceledException) { }
            catch (Exception ex) { lock (gate) { if (!linked.IsCancellationRequested) { status = "unavailable"; message = ex.Message; } } }
            finally { operation.Release(); if (!lifetime.IsCancellationRequested) Publish(); }
        }
        private void Save(string workspace, string currentSession, JArray values)
        {
            var file = FilePath(workspace); CheckpointStore.CheckDirectPath(file); Directory.CreateDirectory(Path.GetDirectoryName(file)!);
            var text = new JObject { ["version"] = 1, ["root"] = workspace, ["sessionId"] = currentSession, ["savedUtc"] = DateTimeOffset.UtcNow.ToString("O"), ["jobs"] = Project(values, false, GetTeamForJob) }.ToString(Formatting.None);
            if (System.Text.Encoding.UTF8.GetByteCount(text) > 512 * 1024) throw new InvalidOperationException(PompLanguage.Pick("백그라운드 메타데이터가 너무 커요.", "Background metadata is too large."));
            AtomicFile.WriteAllText(file, text);
        }
        public async Task ForgetSessionAsync(string workspace, string sessionId)
        {
            await operation.WaitAsync().ConfigureAwait(false);
            try
            {
                var file = FilePath(workspace); CheckpointStore.CheckDirectPath(file);
                if (!File.Exists(file)) return;
                using var stream = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                if (stream.Length > 512 * 1024) throw new InvalidDataException();
                using var text = new StreamReader(stream); using var reader = new JsonTextReader(text) { MaxDepth = 6 };
                var data = JObject.Load(reader);
                if ((string?)data["sessionId"] == sessionId)
                    AtomicFile.WriteAllText(file, new JObject { ["version"] = 1, ["root"] = workspace, ["jobs"] = new JArray() }.ToString(Formatting.None));
            }
            finally { operation.Release(); }
        }
        private JArray Load(string workspace)
        {
            var file = FilePath(workspace); CheckpointStore.CheckDirectPath(file); if (!File.Exists(file)) return new JArray();
            using var stream = new FileStream(file,FileMode.Open,FileAccess.Read,FileShare.ReadWrite|FileShare.Delete);
            if (stream.Length > 512 * 1024) throw new InvalidOperationException(PompLanguage.Pick("보관된 백그라운드 메타데이터가 너무 커요.", "Archived background metadata is too large."));
            using var text = new StreamReader(stream); using var reader = new JsonTextReader(text) { MaxDepth = 6 }; var data = JObject.Load(reader);
            if ((int?)data["version"] != 1 || (string?)data["root"] != workspace || !(data["jobs"] is JArray values)) throw new InvalidOperationException(PompLanguage.Pick("보관된 백그라운드 메타데이터가 올바르지 않아요.", "Invalid archived background metadata."));
            return Project(values);
        }
        public void Dispose() { lock (gate) { if (disposed) return; disposed = true; lifetime.Cancel(); generation.Cancel(); timer.Dispose(); } }
    }
}
