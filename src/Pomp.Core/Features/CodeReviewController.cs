using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Pomp.Hosting;
using Pomp.Permissions;
using Pomp.Tools;
using Pomp.Ui;
using Pomp.Internal;
using Pomp.Adapters;
namespace Pomp.Features
{
    internal sealed class CodeReviewController : IDisposable
    {
        private readonly ICodeReviewSource source;
        private readonly IPompHostEnvironment environment;
        private readonly SemaphoreSlim operation = new SemaphoreSlim(1, 1);
        private readonly object gate = new object();
        private readonly CancellationTokenSource lifetime = new CancellationTokenSource();
        private string id = "", root = "", status = "empty", message = "";
        private List<ReviewFinding> findings = new List<ReviewFinding>();
        private string[] notices = Array.Empty<string>();
        // Who is about to report (POMP's extension names the agent of each report_review_findings call just before it arrives), by tool call.
        private readonly Dictionary<string, string> reporters = new Dictionary<string, string>(StringComparer.Ordinal);
        private readonly Queue<string> reporterOrder = new Queue<string>();
        private static readonly System.Text.RegularExpressions.Regex ReporterName = new System.Text.RegularExpressions.Regex("^[a-z0-9][a-z0-9._-]{0,63}$", System.Text.RegularExpressions.RegexOptions.CultureInvariant);
        public event EventHandler? Changed;
        public CodeReviewController(ICodeReviewSource source, IPompHostEnvironment environment, UiHub ui) { this.source = source; this.environment = environment; }
        public IHostTool ReviewTool() => new Tool(this, false);
        public IHostTool ReportTool() => new Tool(this, true);
        public JObject View()
        {
            lock (gate) return new JObject { ["id"] = id, ["root"] = root, ["status"] = status, ["message"] = message,
                ["notices"] = new JArray(notices),
                ["findings"] = new JArray(findings.Select(f => new JObject { ["id"] = f.Id, ["origin"] = f.Origin, ["severity"] = f.Severity, ["code"] = f.Code,
                    ["message"] = f.Message, ["path"] = f.Path, ["line"] = f.Line, ["sourceHash"] = f.SourceHash, ["evidence"] = f.Evidence, ["reporter"] = f.Reporter, ["acknowledged"] = false })) };
        }
        private void Publish() => Changed?.Invoke(this, EventArgs.Empty);
        /// <summary>The agent of a coming report_review_findings call ("main" or a role name); the last 64 are kept until their call comes.</summary>
        internal void NoteReporter(string toolCallId, string reporter)
        {
            if (string.IsNullOrEmpty(toolCallId) || toolCallId.Length > 256 || !ReporterName.IsMatch(reporter)) return;
            lock (gate)
            {
                if (!reporters.ContainsKey(toolCallId)) reporterOrder.Enqueue(toolCallId);
                reporters[toolCallId] = reporter;
                while (reporterOrder.Count > 64) reporters.Remove(reporterOrder.Dequeue());
            }
        }
        private string TakeReporter(string toolCallId)
        {
            lock (gate) return reporters.TryGetValue(toolCallId, out var reporter) && reporters.Remove(toolCallId) ? reporter : "";
        }
        internal ReviewFinding[] AgentFindings(string workspace)
        {
            lock (gate) return string.Equals(root, workspace, StringComparison.OrdinalIgnoreCase) ? findings.Where(f => f.Origin == "agent").ToArray() : Array.Empty<ReviewFinding>();
        }
        internal async Task<bool> DismissAgentAsync(string workspace, string findingId, CancellationToken token)
        {
            await operation.WaitAsync(token).ConfigureAwait(false);
            try
            {
                if (!string.Equals(workspace, await environment.GetWorkspaceRootAsync(token).ConfigureAwait(false), StringComparison.OrdinalIgnoreCase)) return false;
                lock (gate)
                {
                    if (!string.Equals(root, workspace, StringComparison.OrdinalIgnoreCase)) return false;
                    if (findings.RemoveAll(f => f.Origin == "agent" && f.Id == findingId) == 0) return false;
                }
                Save(); Publish(); return true;
            }
            finally { operation.Release(); }
        }
        public async Task<HostToolResult> ScanAsync(string? path, CancellationToken token)
        {
            if (!await operation.WaitAsync(0, token).ConfigureAwait(false)) return HostToolResult.Error("A review operation is already running.");
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(token, lifetime.Token); linked.CancelAfter(TimeSpan.FromSeconds(120));
            try
            {
                var workspace = await environment.GetWorkspaceRootAsync(linked.Token).ConfigureAwait(false);
                lock (gate) { status = "reviewing"; message = "Reading syntax and existing IDE diagnostics; no build is started."; }
                Publish();
                var result = await source.ReviewAsync(workspace, path, linked.Token).ConfigureAwait(false);
                if (!string.Equals(workspace, await environment.GetWorkspaceRootAsync(linked.Token).ConfigureAwait(false), StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException(PompLanguage.Pick("검토 중 작업 영역 변경", "Workspace changed during review"));
                lock (gate)
                {
                    root = workspace; id = Guid.NewGuid().ToString("N"); status = "ready"; findings = result.Findings.Where(f => !string.IsNullOrWhiteSpace(f.Message) || !string.IsNullOrWhiteSpace(f.Code)).Take(200).ToList(); notices = result.Notices.Take(20).ToArray(); message = "Review results are available. Agent findings need independent confirmation.";
                }
                Publish(); Save();
                var state = View(); state.Remove("history"); state.Remove("prior"); state.Remove("viewedId");
                var shown = (JArray)state["findings"]!;
                while (state.ToString().Length > 100000 && shown.Count > 0) shown.Last!.Remove();
                state["resultLimited"] = shown.Count < result.Findings.Count;
                return HostToolResult.Text(state.ToString(), state);
            }
            catch (OperationCanceledException) { lock (gate) { status = "cancelled"; message = "Review cancelled or timed out; previous results are not a new successful review."; } Publish(); return HostToolResult.Error(message); }
            catch (Exception ex) { lock (gate) { status = "failed"; message = ex.Message; } Publish(); return HostToolResult.Error(ex.Message); }
            finally { operation.Release(); }
        }
        private async Task<HostToolResult> ReportAsync(JObject args, string toolCallId, CancellationToken token)
        {
            var reporter = TakeReporter(toolCallId);
            if (!await operation.WaitAsync(0, token).ConfigureAwait(false)) return HostToolResult.Error("Wait for the current review operation.");
            try
            {
                string workspace, reviewId; lock (gate) { workspace = root; reviewId = id; }
                if ((string?)args["reviewId"] != reviewId || reviewId.Length == 0 || status != "ready") return HostToolResult.Error("Use the ID from the current successful review_source result.");
                if (!(args["findings"] is JArray supplied) || supplied.Count == 0 || supplied.Count > 20) return HostToolResult.Error("Report one to twenty evidence-backed findings.");
                var added = new List<ReviewFinding>();
                foreach (var item in supplied)
                {
                    var file = (string?)item["path"] ?? ""; var hash = (string?)item["sourceHash"] ?? ""; var line = (int?)item["line"] ?? 0;
                    var evidence = (string?)item["evidence"] ?? ""; var description = (string?)item["message"] ?? ""; var severity = (string?)item["severity"] ?? "suggestion";
                    if (description.Length == 0 || description.Length > 1000 || evidence.Length == 0 || evidence.Length > 500 || !new[] { "error", "warning", "suggestion" }.Contains(severity)) return HostToolResult.Error("Invalid finding bounds or severity.");
                    if (!await source.VerifyEvidenceAsync(workspace, file, hash, line, evidence, token).ConfigureAwait(false)) return HostToolResult.Error("Evidence does not match the current source hash and cited lines: " + file);
                    added.Add(new ReviewFinding("agent", severity, "agent-review", description, file, line, hash, evidence, reporter));
                }
                if (!string.Equals(workspace, await environment.GetWorkspaceRootAsync(token).ConfigureAwait(false), StringComparison.OrdinalIgnoreCase)) return HostToolResult.Error("Workspace changed during evidence verification.");
                lock (gate)
                {
                    if (findings.Count + added.Count > 200) return HostToolResult.Error("Review finding budget reached.");
                    foreach (var finding in added) if (!findings.Any(f => f.Id == finding.Id)) findings.Add(finding);
                }
                Publish(); Save(); return HostToolResult.Text("Agent findings were recorded with matching source evidence. They are hypotheses, not compiler-proven defects; no code was changed.");
            }
            catch (Exception ex) { return HostToolResult.Error(ex.Message); }
            finally { operation.Release(); }
        }
        internal async Task LoadSnapshotAsync(CancellationToken token)
        {
            if (!await operation.WaitAsync(0, token).ConfigureAwait(false)) return;
            try
            {
                var workspace = await environment.GetWorkspaceRootAsync(token).ConfigureAwait(false);
                lock (gate)
                {
                    if (id.Length > 0 && string.Equals(root, workspace, StringComparison.OrdinalIgnoreCase)) return;
                    root = workspace; id = ""; findings.Clear(); notices = Array.Empty<string>(); status = "empty"; message = "";
                }
                var file = SnapshotPath(workspace);
                if (!File.Exists(file)) return;
                var data = await Task.Run(() => {
                    CheckpointStore.CheckDirectPath(file);
                    if (new FileInfo(file).Length > 2 * 1024 * 1024) throw new InvalidOperationException(PompLanguage.Pick("저장된 검토가 너무 커요.", "Saved review is too large."));
                    using var stream = File.OpenRead(file);
                    using var reader = new Newtonsoft.Json.JsonTextReader(new StreamReader(stream)) { MaxDepth = 12 };
                    return JObject.Load(reader);
                }, token).ConfigureAwait(false);
                if (!string.Equals((string?)data["root"], workspace, StringComparison.OrdinalIgnoreCase) || !Guid.TryParseExact((string?)data["id"], "N", out _) || !(data["findings"] is JArray saved) || saved.Count > 200) throw new InvalidOperationException(PompLanguage.Pick("저장된 검토의 ID나 스키마가 올바르지 않아요.", "Saved review identity or schema is invalid."));
                var restored = new List<ReviewFinding>();
                foreach (var row in saved)
                {
                    var origin = (string?)row["origin"] ?? ""; var severity = (string?)row["severity"] ?? "";
                    var path = (string?)row["path"] ?? ""; var description = (string?)row["message"] ?? ""; var evidence = (string?)row["evidence"] ?? "";
                    if (!new[] { "syntax", "ide", "agent" }.Contains(origin) || !new[] { "error", "warning", "suggestion" }.Contains(severity) || path.Length > 4096 || description.Length > 1001 || evidence.Length > 500) throw new InvalidOperationException(PompLanguage.Pick("저장된 검토 항목이 올바르지 않아요.", "Saved review entry is invalid."));
                    var code = (string?)row["code"] ?? ""; var sourceHash = (string?)row["sourceHash"] ?? ""; var line = (int?)row["line"] ?? 0;
                    var reporter = row["reporter"]?.Type == JTokenType.String ? (string)row["reporter"]! : "";
                    if (reporter.Length > 0 && !ReporterName.IsMatch(reporter)) reporter = "";
                    if (code.Length > 100 || (sourceHash.Length != 0 && (sourceHash.Length != 64 || !sourceHash.All(Uri.IsHexDigit))) || line < 0 || line > 10000000) throw new InvalidOperationException(PompLanguage.Pick("저장된 검토의 소스 메타데이터가 올바르지 않아요.", "Saved review source metadata is invalid."));
                    if (string.IsNullOrWhiteSpace(description) && string.IsNullOrWhiteSpace(code)) continue;
                    var finding = new ReviewFinding(origin, severity, code, description, path, line, sourceHash, evidence, reporter);
                    if ((string?)row["id"] != finding.Id) throw new InvalidOperationException(PompLanguage.Pick("저장된 검토 발견 항목의 ID가 일치하지 않아요.", "Saved review finding identity mismatch."));
                    restored.Add(finding);
                }
                if (!string.Equals(workspace, await environment.GetWorkspaceRootAsync(token).ConfigureAwait(false), StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException(PompLanguage.Pick("검토 스냅샷을 복원하는 동안 작업 영역이 바뀌었어요.", "Workspace changed while restoring the review snapshot."));
                lock (gate)
                {
                    id = (string)data["id"]!; findings = restored;
                    notices = new[] { PompLanguage.Pick("로컬 검토 스냅샷을 복원했어요.", "Restored local review snapshot.") };
                    status = "archived"; message = PompLanguage.Pick("이전 검토 스냅샷이에요.", "Previous review snapshot.");
                }
            }
            catch (Exception ex) { lock (gate) { status = "failed"; message = PompLanguage.Pick("검토 스냅샷을 복원하지 못했어요: " + ex.Message, "Could not restore review snapshot: " + ex.Message); } }
            finally { operation.Release(); }
        }
        private string SnapshotPath(string workspace) => ProjectData.File(workspace, "reviews.json", Path.Combine(environment.DataDirectory, "reviews", ContentHash.Compute(Path.DirectorySeparatorChar == '\\' ? workspace.ToLowerInvariant() : workspace) + ".json"));

        private void Save()
        {
            try { var file = SnapshotPath(root); CheckpointStore.CheckDirectPath(file); var state = View(); state.Remove("prior"); AtomicFile.WriteAllText(file, state.ToString()); }
            catch (Exception ex) { lock (gate) message = PompLanguage.Pick("검토 저장 실패 · ", "Review save failed · ") + ex.Message; Publish(); }
        }
        public void Dispose() { lifetime.Cancel(); }
        private sealed class Tool : IHostTool
        {
            private readonly CodeReviewController owner; private readonly bool report;
            public Tool(CodeReviewController owner, bool report)
            {
                this.owner = owner; this.report = report;
                Definition = new HostToolDefinition { Name = report ? "report_review_findings" : "review_source", Label = report ? "Report code-review findings" : "Review source diagnostics",
                    Description = report ? "Add agent hypotheses to the current review with exact read_buffer sourceHash, line and quoted evidence. Evidence is checked against live source; a matching quote does not prove the bug. No file edits. Fixes must use the normal reviewed edit workflow." : "Review loaded source syntax plus existing IDE Error List diagnostics, optionally limited to a path. Read-only: no build or explicit analyzer/generator execution. Returns reviewId and severity/path/line findings; IDE diagnostics may be stale and zero findings is not a correctness guarantee.",
                    Parameters = report ? JObject.Parse("{\"type\":\"object\",\"properties\":{\"reviewId\":{\"type\":\"string\"},\"findings\":{\"type\":\"array\",\"items\":{\"type\":\"object\",\"properties\":{\"path\":{\"type\":\"string\"},\"sourceHash\":{\"type\":\"string\"},\"line\":{\"type\":\"integer\"},\"evidence\":{\"type\":\"string\"},\"message\":{\"type\":\"string\"},\"severity\":{\"type\":\"string\",\"enum\":[\"error\",\"warning\",\"suggestion\"]}},\"required\":[\"path\",\"sourceHash\",\"line\",\"evidence\",\"message\"]}}},\"required\":[\"reviewId\",\"findings\"],\"additionalProperties\":false}") : JObject.Parse("{\"type\":\"object\",\"properties\":{\"path\":{\"type\":\"string\"}},\"additionalProperties\":false}") };
            }
            public HostToolDefinition Definition { get; }
            public PermissionKind ResolvePermission(HostToolInvocation invocation) => PermissionKind.None;
            public Task<PermissionRequest?> DescribePermissionAsync(HostToolInvocation invocation, CancellationToken token) => Task.FromResult<PermissionRequest?>(null);
            public Task<HostToolResult> ExecuteAsync(HostToolInvocation invocation, CancellationToken token) => report ? owner.ReportAsync(invocation.Arguments, invocation.ToolCallId, token) : owner.ScanAsync((string?)invocation.Arguments["path"], token);
        }
    }
}
