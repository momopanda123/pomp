using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Pomp.Adapters;
using Pomp.Hosting;
using Pomp.Internal;
using Pomp.Permissions;
using Pomp.Tools;
using Pomp.Ui;

namespace Pomp.Features
{
    /// <summary>A bounded host-owned workflow journal. Phase transitions are not inferred from model prose.</summary>
    internal sealed class ComposerWorkflow : IHostTool
    {
        public const string ToolName = "composer_workflow", CommandMessage = "workspace/composer", StateMessage = "workspace/composerState";
        private readonly IPompHostEnvironment environment;
        private readonly UiHub ui;
        private readonly Func<string> currentSession;
        private readonly Func<bool> unfinished;
        private readonly Func<int> activeCalls;
        private readonly Func<bool> foregroundBusy;
        private readonly object gate = new object();
        private string root = "", session = "", id = "", phase = "empty", task = "", message = "", proposal = "";
        private long revision;
        private string? runtimeFile;
        private bool runtimeGuardRequired, runtimeGuardEnabled;
        private bool paused, discovery, applied, verified;
        private JArray plan = new JArray(), outcomes = new JArray();
        private readonly HashSet<string> pendingJobs = new HashSet<string>();
        private readonly HashSet<string> executionCalls = new HashSet<string>();
        private readonly Dictionary<string, string> observedCalls = new Dictionary<string, string>();
        public ComposerWorkflow(IPompHostEnvironment environment, UiHub ui, Func<string> currentSession, Func<bool> unfinished, Func<int>? activeCalls = null, Func<bool>? foregroundBusy = null)
        { this.environment = environment; this.ui = ui; this.currentSession = currentSession; this.unfinished = unfinished; this.activeCalls = activeCalls ?? (() => 0); this.foregroundBusy = foregroundBusy ?? (() => false); }
        public HostToolDefinition Definition { get; } = new HostToolDefinition {
            Name = ToolName, Label = "Manage POMP Workflow workflow", LoadMode = "essential",
            Description = "POMP-owned Planning/Discovery/Editing/AwaitingReview/Verification/Done state machine. Start with a task; submit a bounded plan; perform real discovery with the VS tools; enter editing; propose_workspace_edits then WAIT for user validation/application. After application perform read-only review_source, inspect actual evidence, then finish. Every mutation after start requires current id/revision. status is read-only. User pause cannot be resumed by this tool. Done means this source-edit batch was applied and inspected, not runtime tests passed. Multiple batches use another editing transition. Workflow journal stores task/plan and outcome metadata locally, not source bodies.",
            Parameters = JObject.Parse("{\"type\":\"object\",\"properties\":{\"action\":{\"type\":\"string\",\"enum\":[\"status\",\"start\",\"plan\",\"editing\",\"finish\"]},\"id\":{\"type\":\"string\"},\"revision\":{\"type\":\"integer\"},\"task\":{\"type\":\"string\",\"maxLength\":16000},\"steps\":{\"type\":\"array\",\"maxItems\":32,\"items\":{\"type\":\"string\",\"maxLength\":500}}},\"required\":[\"action\"],\"additionalProperties\":false}") };
        public PermissionKind ResolvePermission(HostToolInvocation invocation) => PermissionKind.None;
        public Task<PermissionRequest?> DescribePermissionAsync(HostToolInvocation invocation, CancellationToken token) => Task.FromResult<PermissionRequest?>(null);
        private bool Active => id.Length > 0 && phase != "done" && phase != "cancelled";
        private JObject ViewLocked() => new JObject { ["root"] = root, ["sessionId"] = session, ["id"] = id, ["revision"] = revision,
            ["phase"] = phase, ["paused"] = paused, ["task"] = task, ["steps"] = plan.DeepClone(), ["message"] = message,
            ["proposalId"] = proposal, ["outcomes"] = outcomes.DeepClone(), ["discoveryObserved"] = discovery, ["applied"] = applied, ["verificationObserved"] = verified };
        public JObject View() { lock (gate) return ViewLocked(); }
        public void Publish() => ui.Post(StateMessage, View(), UiDelivery.State);
        internal void ConfigureRuntimeGuard(string directory, bool enabled)
        {
            lock (gate)
            {
                runtimeGuardRequired = true; runtimeGuardEnabled = enabled;
                runtimeFile = enabled ? Path.Combine(directory, "composer-guard.json") : null;
                if (enabled) WriteRuntimeLocked();
            }
        }
        private void WriteRuntimeLocked()
        {
            if (runtimeFile == null) return;
            CheckpointStore.CheckDirectPath(runtimeFile); Directory.CreateDirectory(Path.GetDirectoryName(runtimeFile)!);
            AtomicFile.WriteAllText(runtimeFile, new JObject { ["version"] = 1, ["active"] = Active, ["paused"] = paused, ["phase"] = phase, ["root"] = root }.ToString(Formatting.None));
        }
        private string Journal => ProjectData.File(root, "composer-workflows.json", Path.Combine(environment.DataDirectory, "composer-workflows", ContentHash.Compute(root) + ".json"));
        private bool SaveLocked()
        {
            if (root.Length == 0 || id.Length == 0) { WriteRuntimeLocked(); return true; }
            try
            {
                var journal = Journal;
                CheckpointStore.CheckDirectPath(journal); Directory.CreateDirectory(Path.GetDirectoryName(journal)!);
                var data = ViewLocked(); data["version"] = 1;
                WriteRuntimeLocked();
                AtomicFile.WriteAllText(journal, data.ToString(Formatting.None));
                return true;
            }
            catch (Exception ex) { paused = true; message = PompLanguage.Pick("작업 기록을 저장하지 못해 흐름을 멈췄어요: ", "Workflow paused: the journal could not be saved: ") + ex.Message.Substring(0, Math.Min(ex.Message.Length, 500)); try { WriteRuntimeLocked(); } catch { runtimeGuardEnabled = false; } return false; }
        }
        private void LoadLocked(string workspace)
        {
            if (root == workspace) return;
            root = workspace; id = session = task = proposal = message = ""; phase = "empty"; revision = 0;
            paused = discovery = applied = verified = false; plan = new JArray(); outcomes = new JArray(); observedCalls.Clear(); pendingJobs.Clear(); executionCalls.Clear();
            try
            {
                WriteRuntimeLocked();
                var journal = Journal;
                CheckpointStore.CheckDirectPath(journal); if (!File.Exists(journal)) return;
                using var stream = new FileStream(journal, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                if (stream.Length > 128 * 1024) throw new InvalidDataException("Workflow journal exceeds its size limit.");
                using var reader = new JsonTextReader(new StreamReader(stream)) { MaxDepth = 8 };
                var data = JObject.Load(reader);
                if ((int?)data["version"] != 1 || (string?)data["root"] != root || !Guid.TryParseExact((string?)data["id"], "N", out _) ||
                    data["task"]?.Type != JTokenType.String || ((string)data["task"]!).Length > 16000 || !(data["steps"] is JArray steps) || !ValidPlan(steps))
                    throw new InvalidDataException("Malformed workflow journal.");
                id = (string)data["id"]!; task = (string)data["task"]!; plan = (JArray)steps.DeepClone();
                if (data["outcomes"] is JArray saved)
                    outcomes = new JArray(saved.OfType<JObject>().Where(o => o["path"]?.Type == JTokenType.String && ((string)o["path"]!).Length <= 4096 && o["state"]?.Type == JTokenType.String && ((string)o["state"]!).Length <= 64).Select(o => new JObject { ["path"] = (string?)o["path"], ["state"] = (string?)o["state"] }));
                // Stored completion is history, never executable authority or reusable verification evidence.
                phase = (string?)data["phase"] == "done" ? "done" : (string?)data["phase"] == "cancelled" ? "cancelled" : "planning"; paused = phase == "planning"; revision = 1; session = currentSession();
                // Only an unfinished workflow is shown again (paused at planning); a finished or cancelled one stays history, so it gets no notice.
                if (phase == "planning") message = PompLanguage.Pick("작업·계획·파일 결과를 복원했어요. 이전 승인과 검증은 복원하지 않았어요.", "Recovered the task, plan and file outcomes. Prior approvals and verification were not restored.");
                WriteRuntimeLocked();
            }
            catch (Exception ex) { message = PompLanguage.Pick("작업 기록을 읽지 못했어요: ", "Workflow journal unavailable: ") + ex.Message.Substring(0, Math.Min(ex.Message.Length, 500)); paused = true; }
        }
        private static bool ValidPlan(JArray steps) => steps.Count <= 32 && steps.All(s => s.Type == JTokenType.String && !string.IsNullOrWhiteSpace((string?)s) && ((string)s!).Length <= 500);
        public async Task RefreshAsync(CancellationToken token)
        {
            var workspace = await environment.GetWorkspaceRootAsync(token).ConfigureAwait(false);
            lock (gate) LoadLocked(workspace); Publish();
        }
        public async Task<HostToolResult> ExecuteAsync(HostToolInvocation invocation, CancellationToken token)
        {
            var workspace = await environment.GetWorkspaceRootAsync(token).ConfigureAwait(false);
            lock (gate)
            {
                LoadLocked(workspace);
                try
                {
                    var args = invocation.Arguments; var action = (string?)args["action"];
                    if (action == "status") return ResultLocked();
                    if (action == "start")
                    {
                        if (runtimeGuardRequired && !runtimeGuardEnabled) throw new InvalidOperationException(PompLanguage.Pick("POMP Workflow에 필요한 구성 요소가 로드되지 않았어요.", "A component POMP Workflow needs is not loaded."));
                        if (unfinished() || activeCalls() > 1) throw new InvalidOperationException("Wait for current terminal/agent work before starting POMP Workflow.");
                        if (Active) throw new InvalidOperationException("Finish or cancel the current workflow before starting another.");
                        var text = (string?)args["task"];
                        if (string.IsNullOrWhiteSpace(text) || text!.Length > 16000 || string.IsNullOrWhiteSpace(currentSession())) throw new ArgumentException("A connected session and task of 1–16,000 characters are required.");
                        id = Guid.NewGuid().ToString("N"); revision = 1; task = text; session = currentSession(); phase = "planning";
                        paused = discovery = applied = verified = false; proposal = message = ""; plan = new JArray(); outcomes = new JArray(); observedCalls.Clear(); pendingJobs.Clear(); executionCalls.Clear();
                    }
                    else
                    {
                        MatchLocked(args);
                        if (paused || session != currentSession()) throw new InvalidOperationException("Workflow is paused or belongs to another conversation. Resume explicitly in the UI.");
                        if (action == "plan")
                        {
                            if (phase != "planning" || !(args["steps"] is JArray steps) || steps.Count == 0 || !ValidPlan(steps)) throw new InvalidOperationException("Submit 1–32 bounded steps during planning.");
                            plan = (JArray)steps.DeepClone(); phase = "discovery";
                        }
                        else if (action == "editing")
                        {
                            if ((phase != "discovery" || !discovery) && phase != "verification") throw new InvalidOperationException("Observe successful source discovery before editing, or finish reviewing the prior applied batch.");
                            phase = "editing"; verified = false; proposal = "";
                        }
                        else if (action == "finish")
                        {
                            if (phase != "verification" || !applied || !verified || pendingJobs.Count > 0 || unfinished() || activeCalls() > 1) throw new InvalidOperationException("Finish requires an applied batch, observed post-apply source review and no pending terminal/agent work.");
                            phase = "done"; message = PompLanguage.Pick("워크플로를 마쳤어요.", "Workflow completed.");
                        }
                        else throw new ArgumentException("Unknown POMP Workflow action.");
                        revision++;
                    }
                    SaveLocked(); Publish(); return paused ? HostToolResult.Error(message) : ResultLocked();
                }
                catch (Exception ex) { return HostToolResult.Error(ex.Message); }
            }
        }
        private HostToolResult ResultLocked() { var data = ViewLocked(); return HostToolResult.Text(data.ToString(Formatting.None), data); }
        private void MatchLocked(JObject args)
        {
            if (!Active || (string?)args["id"] != id || args["revision"]?.Type != JTokenType.Integer || (long?)args["revision"] != revision)
                throw new InvalidOperationException(PompLanguage.Pick("워크플로 id나 revision이 현재와 달라요.", "The workflow id or revision does not match the current one."));
        }
        // GUI save path only. Creates or edits the task/plan journal; never issues a model request.
        // Save pauses the workflow; the user resumes and continues explicitly in chat.
        private void SaveUi(JObject args)
        {
            if (foregroundBusy()) throw new InvalidOperationException(PompLanguage.Pick("대화가 응답 중이에요.", "A conversation turn is in progress."));
            var text = (string?)args["task"];
            if (string.IsNullOrWhiteSpace(text) || text!.Length > 16000) throw new ArgumentException(PompLanguage.Pick("작업 내용이 비어 있거나 너무 길어요.", "The task is empty or too long."));
            if (args["steps"] is not JArray steps || !ValidPlan(steps)) throw new InvalidOperationException(PompLanguage.Pick("단계 형식이 올바르지 않아요.", "The steps are invalid."));
            var editor = currentSession();
            if (string.IsNullOrWhiteSpace(editor) || (string?)args["sessionId"] != editor) throw new InvalidOperationException(PompLanguage.Pick("대화가 바뀌었어요.", "Conversation changed."));
            if ((string?)args["id"] != id || args["revision"]?.Type != JTokenType.Integer || (long?)args["revision"] != revision)
                throw new InvalidOperationException(PompLanguage.Pick("워크플로가 바뀌었어요.", "Workflow changed."));
            if (!Active)
            {
                if (runtimeGuardRequired && !runtimeGuardEnabled) throw new InvalidOperationException(PompLanguage.Pick("POMP Workflow에 필요한 구성 요소가 로드되지 않았어요.", "A component POMP Workflow needs is not loaded."));
                if (unfinished() || activeCalls() > 0) throw new InvalidOperationException(PompLanguage.Pick("터미널·에이전트 작업이 진행 중이에요.", "Terminal or agent work is in progress."));
                id = Guid.NewGuid().ToString("N"); revision = 0; task = text; session = editor;
                plan = (JArray)steps.DeepClone(); outcomes = new JArray(); observedCalls.Clear(); pendingJobs.Clear(); executionCalls.Clear();
                phase = steps.Count > 0 ? "discovery" : "planning";
                paused = true; discovery = applied = verified = false; proposal = "";
                message = PompLanguage.Pick("작업을 저장했어요.", "Task saved.");
                return;
            }
            if ((string?)args["id"] != id || args["revision"]?.Type != JTokenType.Integer || (long?)args["revision"] != revision)
                throw new InvalidOperationException(PompLanguage.Pick("워크플로가 바뀌었어요.", "Workflow changed."));
            if (session != editor) throw new InvalidOperationException(PompLanguage.Pick("다른 대화의 워크플로예요.", "The workflow belongs to another conversation."));
            if (phase == "awaitingReview") throw new InvalidOperationException(PompLanguage.Pick("검토를 기다리는 제안이 있어요.", "A proposal is awaiting review."));
            if (phase != "planning" && phase != "discovery" && phase != "editing") throw new InvalidOperationException(PompLanguage.Pick("이 단계에서는 편집할 수 없어요.", "Editing is not allowed in this phase."));
            if (unfinished() || activeCalls() > 0) throw new InvalidOperationException(PompLanguage.Pick("터미널·에이전트 작업이 진행 중이에요.", "Terminal or agent work is in progress."));
            task = text; plan = (JArray)steps.DeepClone();
            phase = steps.Count > 0 ? "discovery" : "planning";
            discovery = applied = verified = false; proposal = ""; observedCalls.Clear(); pendingJobs.Clear(); executionCalls.Clear();
            paused = true;
            message = PompLanguage.Pick("작업을 저장했어요. 이전 탐색·검증 결과는 지웠어요.", "Task saved. Prior discovery and verification were discarded.");
        }
        // GUI start from a saved workflow (Tools › 지침 › 워크플로): a new, unpaused run with the saved task/plan. The page sends the chat
        // message that continues it; this step itself never issues a model request.
        private void StartUi(JObject args)
        {
            if (foregroundBusy()) throw new InvalidOperationException(PompLanguage.Pick("대화가 응답 중이에요.", "A conversation turn is in progress."));
            if (Active) throw new InvalidOperationException(PompLanguage.Pick("이미 진행 중인 워크플로가 있어요.", "A workflow is already active."));
            var text = (string?)args["task"];
            if (string.IsNullOrWhiteSpace(text) || text!.Length > 16000) throw new ArgumentException(PompLanguage.Pick("작업 내용이 비어 있거나 너무 길어요.", "The task is empty or too long."));
            if (args["steps"] is not JArray steps || !ValidPlan(steps)) throw new InvalidOperationException(PompLanguage.Pick("단계 형식이 올바르지 않아요.", "The steps are invalid."));
            var editor = currentSession();
            if (string.IsNullOrWhiteSpace(editor) || (string?)args["sessionId"] != editor) throw new InvalidOperationException(PompLanguage.Pick("대화가 바뀌었어요.", "Conversation changed."));
            if ((string?)args["id"] != id || args["revision"]?.Type != JTokenType.Integer || (long?)args["revision"] != revision)
                throw new InvalidOperationException(PompLanguage.Pick("워크플로가 바뀌었어요.", "Workflow changed."));
            if (runtimeGuardRequired && !runtimeGuardEnabled) throw new InvalidOperationException(PompLanguage.Pick("POMP Workflow에 필요한 구성 요소가 로드되지 않았어요.", "A component POMP Workflow needs is not loaded."));
            if (unfinished() || activeCalls() > 0) throw new InvalidOperationException(PompLanguage.Pick("터미널·에이전트 작업이 진행 중이에요.", "Terminal or agent work is in progress."));
            id = Guid.NewGuid().ToString("N"); revision = 0; task = text; session = editor;
            plan = (JArray)steps.DeepClone(); outcomes = new JArray(); observedCalls.Clear(); pendingJobs.Clear(); executionCalls.Clear();
            phase = steps.Count > 0 ? "discovery" : "planning";
            paused = discovery = applied = verified = false; proposal = "";
            message = PompLanguage.Pick("저장된 워크플로에서 시작했어요.", "Started from a saved workflow.");
        }
        public async Task HandleAsync(JObject args, CancellationToken token)
        {
            string? controlError = null;
            var workspace = await environment.GetWorkspaceRootAsync(token).ConfigureAwait(false);
            lock (gate)
            {
                LoadLocked(workspace);
                try
                {
                    if ((string?)args["action"] == "refresh") { PublishControl(args); return; }
                    if ((string?)args["root"] != root) throw new InvalidOperationException(PompLanguage.Pick("작업 영역이 바뀌었어요.", "Workspace changed."));
                    if ((string?)args["action"] == "save") { SaveUi(args); revision++; if (!SaveLocked()) controlError = message; }
                    else if ((string?)args["action"] == "start") { StartUi(args); revision++; if (!SaveLocked()) controlError = message; }
                    else
                    {
                        MatchLocked(args);
                        switch ((string?)args["action"])
                        {
                            case "pause": paused = true; message = PompLanguage.Pick("흐름을 일시정지했어요.", "Workflow paused."); break;
                            case "resume":
                                if (unfinished() || activeCalls() > 0) throw new InvalidOperationException(PompLanguage.Pick("터미널·에이전트 작업이 진행 중이에요.", "Terminal or agent work is in progress."));
                                if (session != currentSession()) { session = currentSession(); phase = "planning"; discovery = applied = verified = false; proposal = ""; observedCalls.Clear(); pendingJobs.Clear(); executionCalls.Clear(); }
                                if (string.IsNullOrWhiteSpace(session)) throw new InvalidOperationException(PompLanguage.Pick("연결된 대화가 없어요.", "No conversation is connected."));
                                phase = "planning"; discovery = verified = false; proposal = ""; observedCalls.Clear(); pendingJobs.Clear(); executionCalls.Clear(); paused = false; message = PompLanguage.Pick("흐름을 재개했어요.", "Workflow resumed."); break;
                            case "cancel": phase = "cancelled"; paused = false; message = PompLanguage.Pick("흐름을 취소했어요.", "Workflow cancelled."); break;
                            default: throw new ArgumentException("Unknown workflow control.");
                        }
                        revision++; SaveLocked();
                    }
                }
                catch (Exception ex) { controlError = message = ex.Message; }
            }
            PublishControl(args, controlError);
        }
        private void PublishControl(JObject args, string? error = null) { var view = View(); view["controlRequestId"] = args["requestId"]?.Type == JTokenType.String ? ((string)args["requestId"]!).Substring(0, Math.Min(128, ((string)args["requestId"]!).Length)) : null; view["controlError"] = error; ui.Post(StateMessage, view, UiDelivery.State); }
        public void ObserveInteractive(JObject terminal)
        {
            lock (gate)
            {
                if (!Active || phase != "verification" || (string?)terminal["root"] != root || (string?)terminal["sessionId"] != session || string.IsNullOrEmpty((string?)terminal["terminalId"])) return;
                var key = "pty:" + (string?)terminal["terminalId"]; var changed = false;
                var state = (string?)terminal["state"];
                if (new[] { "starting", "running", "closing" }.Contains(state)) changed = pendingJobs.Add(key);
                else if (state == "exited" || state == "failed")
                {
                    if (!pendingJobs.Remove(key)) return;
                    changed = true;
                    if (state != "exited" || (int?)terminal["exitCode"] != 0 || !string.IsNullOrEmpty((string?)terminal["error"]))
                    { phase = "editing"; verified = false; message = PompLanguage.Pick("대화형 검증이 정상적으로 끝나지 않았어요.", "Interactive verification did not finish cleanly."); }
                }
                if (!changed) return;
                revision++; SaveLocked();
            }
            Publish();
        }
        public void Interrupt(string reason)
        {
            lock (gate) { if (!Active) return; paused = true; message = reason; revision++; observedCalls.Clear(); pendingJobs.Clear(); executionCalls.Clear(); SaveLocked(); }
            Publish();
        }
        public string? CheckApproval(PermissionKind kind)
        {
            lock (gate)
            {
                if (!Active) return null;
                if (paused || session != currentSession()) return "POMP Workflow is paused; no new tool approval is admitted.";
                if ((kind == PermissionKind.ProcessExec || kind == PermissionKind.TestRun) && phase == "verification") return null;
                return "POMP Workflow requires shadow proposals for edits. This tool approval is blocked during the current phase.";
            }
        }
        public string? CheckReview() { lock (gate) return Active && (paused || session != currentSession() || (phase != "editing" && phase != "awaitingReview")) ? "POMP Workflow is paused or not in a reviewable phase. Resume the workflow before staging, validating or applying." : null; }
        public string? CheckTool(HostToolInvocation invocation, PermissionKind kind)
        {
            lock (gate)
            {
                if (!Active || invocation.ToolName == ToolName) return null;
                if (paused || session != currentSession()) return "POMP Workflow is paused. Resume or cancel it in the workflow panel before dispatching VS tools.";
                if (invocation.ToolName == WorkspaceReviewController.ToolName && phase != "editing") return "Enter POMP Workflow editing phase before staging a proposal.";
                if (kind != PermissionKind.None && kind != PermissionKind.TestRun && kind != PermissionKind.ProcessExec)
                    return "Use POMP Workflow shadow proposals for edits; direct mutating VS tools are blocked during this workflow.";
                if ((kind == PermissionKind.ProcessExec || kind == PermissionKind.TestRun || invocation.ToolName == "run_terminal") && phase != "verification")
                    return "Execution is only admitted during POMP Workflow verification, with its separate approval.";
                if (phase == "verification" && invocation.ToolName == "review_source" && !string.IsNullOrWhiteSpace((string?)invocation.Arguments["path"])) return "Review the whole workspace after applying a POMP Workflow batch; a single-file scan cannot complete this phase.";
                if (kind == PermissionKind.ProcessExec || kind == PermissionKind.TestRun) executionCalls.Add(invocation.HostCallId);
                if (observedCalls.Count < 256) observedCalls[invocation.HostCallId] = id;
                return null;
            }
        }
        public void Observe(HostToolCompletedEventArgs result)
        {
            lock (gate)
            {
                if (!observedCalls.TryGetValue(result.HostCallId, out var run)) return;
                observedCalls.Remove(result.HostCallId);
                var wasExecution = executionCalls.Remove(result.HostCallId);
                if (!Active || run != id || session != currentSession()) return;
                if (result.Cancelled || result.IsError)
                {
                    if (wasExecution) { phase = "editing"; verified = false; message = PompLanguage.Pick("검증 도구가 실패했거나 취소됐어요.", "A verification tool failed or was cancelled."); revision++; SaveLocked(); Publish(); }
                    return;
                }
                if (phase == "discovery" && (result.ToolName == "search_codebase" || result.ToolName == "read_buffer" || result.ToolName == "build_codebase_context")) discovery = true;
                else if (phase == "editing" && result.ToolName == WorkspaceReviewController.ToolName && result.Details?["staged"]?.Value<bool>() == true)
                { proposal = (string?)result.Details["proposalId"] ?? ""; phase = "awaitingReview"; message = PompLanguage.Pick("변경 검토와 적용을 기다려요.", "Waiting for review and application."); }
                else if (phase == "verification" && result.ToolName == "review_source")
                {
                    verified = (string?)result.Details?["root"] == root && (string?)result.Details?["status"] == "ready" && result.Details?["resultLimited"]?.Value<bool>() != true && result.Details?["findings"] is JArray findings && !findings.Any(f => string.Equals((string?)f["severity"], "error", StringComparison.OrdinalIgnoreCase));
                    message = verified ? PompLanguage.Pick("적용 후 소스 검토에서 오류가 없었어요.", "The post-apply source review reported no errors.")
                        : PompLanguage.Pick("소스 검토가 끝나지 않았거나 오래됐거나 오류가 있어요.", "The source review is incomplete, stale or reports errors.");
                }
                else if (phase == "verification" && result.ToolName == InteractiveTerminal.ToolName && result.Details is JObject terminal)
                {
                    var terminalId = "pty:" + (string?)terminal["terminalId"];
                    if (new[] { "starting", "running", "closing" }.Contains((string?)terminal["state"])) pendingJobs.Add(terminalId);
                    else if ((string?)terminal["state"] == "exited" && (int?)terminal["exitCode"] == 0 && string.IsNullOrEmpty((string?)terminal["error"])) pendingJobs.Remove(terminalId);
                    else if ((string?)terminal["state"] == "failed" || (string?)terminal["state"] == "exited")
                    { pendingJobs.Remove(terminalId); phase = "editing"; verified = false; message = PompLanguage.Pick("대화형 검증이 정상적으로 끝나지 않았어요.", "Interactive verification did not finish cleanly."); }
                }
                else if (phase == "verification" && result.ToolName == "run_terminal" && result.Details?["jobId"]?.Type == JTokenType.String) pendingJobs.Add((string)result.Details["jobId"]!);
                else if (phase == "verification" && result.ToolName == "terminal_status" && result.Details?["jobs"] is JArray jobs)
                {
                    foreach (var job in jobs)
                    {
                        var jobId = (string?)job["id"];
                        if (jobId == null || !pendingJobs.Contains(jobId) || !string.Equals((string?)job["root"], root, StringComparison.OrdinalIgnoreCase)) continue;
                        if ((string?)job["state"] == "exited" && (int?)job["exitCode"] == 0) pendingJobs.Remove(jobId);
                        else if (new[] { "failed", "cancelled", "timedOut" }.Contains((string?)job["state"]) || (job["exitCode"]?.Type == JTokenType.Integer && (int)job["exitCode"]! != 0))
                        { phase = "editing"; verified = false; pendingJobs.Remove(jobId); message = PompLanguage.Pick("검증 작업이 실패했거나 취소됐어요.", "The verification job failed or was cancelled."); }
                    }
                }
                else return;
                revision++; SaveLocked();
            }
            Publish();
        }
        public void ObserveReview(JObject review)
        {
            lock (gate)
            {
                if (!Active || proposal.Length == 0) return;
                if ((string?)review["status"] == "empty") { phase = "editing"; proposal = ""; message = PompLanguage.Pick("제안을 버렸어요.", "Proposal discarded."); revision++; SaveLocked(); Publish(); return; }
                if ((string?)review["id"] != proposal) return;
                if ((string?)review["status"] == "done")
                { phase = "verification"; applied = true; verified = false; message = PompLanguage.Pick("선택한 변경을 적용했어요.", "Selected changes applied."); }
                else if ((string?)review["status"] == "failed")
                { paused = true; message = PompLanguage.Pick("검토·적용에 실패했어요.", "Review/application failed."); }
                else return;
                proposal = ""; outcomes = new JArray((review["outcomes"] as JArray ?? new JArray()).Select(o => new JObject { ["path"] = o["path"], ["state"] = o["state"] }));
                revision++; SaveLocked();
            }
            Publish();
        }
    }
}
