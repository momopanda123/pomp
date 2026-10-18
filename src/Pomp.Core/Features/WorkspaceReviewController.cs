using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;
using Pomp.Hosting;
using Pomp.Permissions;
using Pomp.Tools;
using Pomp.Ui;

namespace Pomp.Features
{
    /// <summary>One bounded model proposal, reviewed in the UI. Staging is read-only; apply goes through the host gate.</summary>
    internal sealed partial class WorkspaceReviewController : IDisposable
    {
        public const string StateMessage = "workspace/reviewState";
        public const string HistoryStateMessage = "workspace/historyState";
        public const string HistoryCommandMessage = "workspace/history";
        public const string CommandMessage = "workspace/review";
        public const string ToolName = "propose_workspace_edits";
        private readonly IWorkspaceFeatureAdapters adapters;
        private readonly IPompHostEnvironment environment;
        private readonly IPermissionGate permissions;
        private readonly UiHub ui;
        private readonly SemaphoreSlim operation = new SemaphoreSlim(1, 1);
        private readonly object gate = new object();
        private readonly CancellationTokenSource lifetime = new CancellationTokenSource();
        private CancellationTokenSource? active;
        private ShadowWorkspace? shadow;
        private Dictionary<string, LineDiff> diffs = new Dictionary<string, LineDiff>(StringComparer.OrdinalIgnoreCase);
        private Dictionary<string, HashSet<int>> selections = new Dictionary<string, HashSet<int>>(StringComparer.OrdinalIgnoreCase);
        private string id = string.Empty, root = string.Empty, status = "empty", message = string.Empty;
        private ShadowValidation? validation;
        private WorkspaceApplyResult? result;

        public WorkspaceReviewController(IWorkspaceFeatureAdapters adapters, IPompHostEnvironment environment, IPermissionGate permissions, UiHub ui)
        { this.adapters = adapters; this.environment = environment; this.permissions = permissions; this.ui = ui; }
        public IHostTool CreateTool() => new ProposalTool(this);
        public JObject View()
        {
            lock (gate)
            {
                return new JObject {
                    ["id"] = id, ["revision"] = shadow?.Revision ?? 0, ["status"] = status, ["message"] = message, ["origin"] = proposalOrigin,
                    ["canApply"] = status == "ready" && shadow?.CanApply == true,
                    ["nativeDiffAvailable"] = environment is IWorkspaceDiffPresenter,
                    ["files"] = new JArray(diffs.Select(pair => new JObject {
                        ["path"] = pair.Key, ["coarse"] = pair.Value.IsCoarse,
                        ["hunks"] = new JArray(pair.Value.Hunks.Select(h => new JObject {
                            ["id"] = h.Id, ["oldLine"] = h.OldLine, ["newLine"] = h.NewLine,
                            ["before"] = h.Before, ["after"] = h.After, ["selected"] = selections[pair.Key].Contains(h.Id) })) })),
                    ["diagnostics"] = new JArray((validation?.Diagnostics ?? Array.Empty<ShadowDiagnostic>()).Select(d => new JObject {
                        ["message"] = d.Message, ["isError"] = d.IsError, ["path"] = d.Path, ["line"] = d.Line })),
                    ["outcomes"] = new JArray((result?.Files ?? Array.Empty<WorkspaceFileOutcome>()).Select(f => new JObject {
                        ["path"] = f.Path, ["state"] = f.State.ToString(), ["error"] = f.Error })) };
            }
        }
        internal Func<string?>? AdmissionCheck { get; set; }
        internal Action<JObject>? StateObserved { get; set; }
        public void Publish() { var view = View(); ui.Post(StateMessage, view, UiDelivery.State); StateObserved?.Invoke(view); }
        private void SetStatus(string value, string text = "") { lock (gate) { status = value; message = text; } Publish(); }

        public async Task HandleAsync(JObject args, CancellationToken cancellationToken)
        {
            var action = args["action"]?.Value<string>();
            if (action == "refresh") { Publish(); return; }
            // The editor-changes list and staging carry no proposal id (they create it); every other action below binds to the open one.
            if (action == "editorChanges" || action == "fromEditor") { await HandleEditorChangesAsync(args, action, cancellationToken).ConfigureAwait(false); return; }
            var refused = false;
            lock (gate)
            {
                if ((string?)args["id"] != id || id.Length == 0) { Publish(); return; }
                if (action == "discard")
                {
                    // Edits may already sit in editor buffers while applying: the apply runs to its end and reports its outcome; only then can the proposal go.
                    if (status == "applying") { message = PompLanguage.Pick("변경을 적용하는 중에는 제안을 버릴 수 없어요.", "The proposal cannot be discarded while changes are being applied."); refused = true; }
                    else { try { active?.Cancel(); } catch (ObjectDisposedException) { } }
                }
            }
            if (refused) { Publish(); return; }
            if (action == "discard") await operation.WaitAsync(cancellationToken).ConfigureAwait(false);
            else if (!await operation.WaitAsync(0, cancellationToken).ConfigureAwait(false)) { Publish(); return; }
            var recordNote = string.Empty; // Appended to the review message when the checkpoint of an interrupted apply could not be closed.
            try
            {
                ShadowWorkspace current; string currentRoot;
                lock (gate)
                {
                    if (shadow == null || (string?)args["id"] != id) return;
                    if (action == "discard") { shadow = null; diffs.Clear(); selections.Clear(); id = string.Empty; validation = null; result = null; status = "empty"; message = string.Empty; proposalOrigin = "agent"; editorCurrents = null; return; }
                    if (status == "done" || (long?)args["revision"] != shadow.Revision) return;
                    current = shadow; currentRoot = root;
                }
                if (action == "nativeDiff")
                {
                    try
                    {
                        if (!(environment is IWorkspaceDiffPresenter presenter)) throw new InvalidOperationException(PompLanguage.Pick("네이티브 diff를 사용할 수 없어요.", "Native diff is unavailable."));
                        var path = (string?)args["path"]; string before, after;
                        lock (gate)
                        {
                            if (path == null || !diffs.TryGetValue(path, out var diff)) return;
                            before = diff.Before; after = diff.ApplySelection(selections[path]);
                        }
                        using var compare = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, lifetime.Token);
                        lock (gate) active = compare;
                        if (!string.Equals(currentRoot, await environment.GetWorkspaceRootAsync(compare.Token).ConfigureAwait(false), StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException(PompLanguage.Pick("작업 영역이 바뀌어서 네이티브 미리보기를 열지 않았어요.", "Workspace changed; native preview was not opened."));
                        await presenter.ShowWorkspaceDiffAsync(path, before, after, current.Revision, compare.Token).ConfigureAwait(false);
                    }
                    catch (Exception ex) { lock (gate) message = PompLanguage.Pick("네이티브 diff 미리보기: " + ex.Message, "Native diff preview: " + ex.Message); }
                    return;
                }
                if (action == "select")
                {
                    var path = (string?)args["path"];
                    if (path == null || !(args["selected"] is JArray selected) || selected.Any(x => x.Type != JTokenType.Integer)) return;
                    var ids = selected.Select(x => x.Value<int>()).ToArray();
                    lock (gate)
                    {
                        current.Select(path, ids); selections[path] = new HashSet<int>(ids); validation = null; result = null; status = "reviewing";
                    }
                    return;
                }
                if (action == "amend")
                {
                    var path = (string?)args["path"];
                    if (path == null || args["hunkId"]?.Type != JTokenType.Integer || args["after"]?.Type != JTokenType.String) return;
                    var replacement = (string)args["after"]!;
                    if (replacement.Length > 24000 || replacement.IndexOf('\0') >= 0) throw new ArgumentException(PompLanguage.Pick("수정한 변경 덩어리가 올바르지 않아요.", "The edited hunk is invalid."));
                    LineDiff original;
                    lock (gate) { if (!diffs.TryGetValue(path, out original!)) return; }
                    var hunkId = (int)args["hunkId"]!;
                    // A browser textarea normalizes line breaks; preserve this source file's first newline convention.
                    var newline = System.Text.RegularExpressions.Regex.Match(original.Before, "\\r\\n|\\r|\\n").Value;
                    if (newline.Length > 0) replacement = System.Text.RegularExpressions.Regex.Replace(replacement, "\\r\\n|\\r|\\n", _ => newline);
                    using var amendCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, lifetime.Token);
                    lock (gate) active = amendCancellation;
                    var amended = await Task.Run(() => original.WithHunkReplacement(hunkId, replacement, amendCancellation.Token), amendCancellation.Token).ConfigureAwait(false);
                    if (ReferenceEquals(amended, original)) return;
                    if (amended.After.Length > 500000) throw new InvalidOperationException(PompLanguage.Pick("수정한 소스가 너무 커요.", "The edited source is too large."));
                    var previewCharacters = diffs.Where(p => !string.Equals(p.Key, path, StringComparison.OrdinalIgnoreCase)).Sum(p => p.Value.Hunks.Sum(h => (long)h.Before.Length + h.After.Length + 150))
                        + amended.Hunks.Sum(h => (long)h.Before.Length + h.After.Length + 150);
                    if (previewCharacters > 180000) throw new InvalidOperationException(PompLanguage.Pick("수정한 제안이 너무 커요.", "The edited proposal is too large."));
                    amendCancellation.Token.ThrowIfCancellationRequested();
                    lock (gate)
                    {
                        current.StagePrepared(path, amended); current.Select(path, Array.Empty<int>());
                        diffs[path] = amended; selections[path] = new HashSet<int>(); validation = null; result = null; status = "reviewing";
                        message = PompLanguage.Pick("제안을 수정했어요. 이 파일의 선택이 초기화됐어요.", "Proposal edited. This file's selections were reset.");
                    }
                    return;
                }
                if (action != "validate" && action != "apply") return;
                var admissionError = AdmissionCheck?.Invoke(); if (admissionError != null) throw new InvalidOperationException(admissionError);
                using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, lifetime.Token);
                lock (gate) active = linked;
                if (action == "validate")
                {
                    var validator = adapters.ShadowValidator;
                    if ((bool?)args["allowSourceGenerators"] == true)
                    {
                        if (!(validator is ISourceGeneratorShadowValidator configurable)) throw new InvalidOperationException(PompLanguage.Pick("소스 생성기 검증을 명시적으로 제어할 수 없어요.", "Source-generator validation cannot be controlled explicitly."));
                        ProjectExecutionPolicy.ThrowIfIsolationRequired(currentRoot); ProjectExecutionPolicy.RequireTool(currentRoot, "validate_workspace", new JObject());
                        SetStatus("approving");
                        var approval = await permissions.RequestAsync(new PermissionRequest { Kind = PermissionKind.ProcessExec, Source = PermissionSource.HostTool, ToolName = "validate_workspace", CanRemember = false,
                            Title = PompLanguage.Pick("검증 중에 프로젝트 소스 생성기를 실행할까요?", "Run project source generators during validation?"), Target = currentRoot,
                            Message = PompLanguage.Pick("프로젝트와 패키지의 소스 생성기 코드가 샌드박스 없이 실행돼요.", "Source-generator code from the project and its packages runs without a sandbox.") }, linked.Token).ConfigureAwait(false);
                        if (!approval.IsAllowed) { SetStatus("reviewing", PompLanguage.Pick("소스 생성기 검증이 승인되지 않았어요.", "Source-generator validation was not approved.")); return; }
                        if (!string.Equals(currentRoot, await environment.GetWorkspaceRootAsync(linked.Token).ConfigureAwait(false), StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException(PompLanguage.Pick("소스 생성기 승인 후 작업 영역이 바뀌었어요.", "Workspace changed after generator approval."));
                        ProjectExecutionPolicy.ThrowIfIsolationRequired(currentRoot); ProjectExecutionPolicy.RequireTool(currentRoot, "validate_workspace", new JObject());
                        validator = configurable.WithSourceGenerators(true);
                    }
                    SetStatus("validating");
                    var checkedResult = await current.ValidateAsync(ValidatorFor(validator), linked.Token).ConfigureAwait(false);
                    lock (gate) validation = checkedResult;
                    SetStatus(current.CanApply ? "ready" : "reviewing", current.CanApply ? PompLanguage.Pick("이 선택 항목의 검사를 통과했어요.", "Checks passed for this selection.") : PompLanguage.Pick("현재 선택 항목이 검증을 통과하지 않았어요.", "The current selection has not passed validation."));
                    return;
                }
                var snapshot = ApplySnapshot(current);
                // An editor proposal with every hunk selected changes nothing: the editor already holds the result.
                if (snapshot.Files.Count == 0) { SetStatus("done", PompLanguage.Pick("선택한 변경이 이미 편집기에 있어요.", "The editor already holds the selected changes.")); return; }
                var policyArgs = new JObject { ["files"] = new JArray(snapshot.Files.Select(file => new JObject { ["path"] = file.Path })) };
                ProjectExecutionPolicy.RequireTool(currentRoot, ToolName, policyArgs);
                var request = new PermissionRequest { Source = PermissionSource.HostTool, Kind = PermissionKind.FileWrite,
                    ToolName = ToolName, Title = PompLanguage.Pick("검토한 작업 영역 변경을 적용할까요?", "Apply reviewed workspace changes?"), Target = currentRoot, CanRemember = false,
                    Message = PompLanguage.Pick("검토한 소스 파일 " + snapshot.Files.Count + "개를 편집기 버퍼에 적용해요.", "Applies " + snapshot.Files.Count + " reviewed source file(s) to the editor buffers."),
                    Diff = ApprovalDiff(snapshot) };
                SetStatus("approving");
                var answer = await permissions.RequestAsync(request, linked.Token).ConfigureAwait(false);
                linked.Token.ThrowIfCancellationRequested();
                if (!answer.IsAllowed) { SetStatus("ready", PompLanguage.Pick("적용이 승인되지 않았어요.", "Application was not approved.")); return; }
                if (snapshot.Revision != current.Revision || !string.Equals(await environment.GetWorkspaceRootAsync(linked.Token).ConfigureAwait(false), currentRoot, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidOperationException(PompLanguage.Pick("승인 후 작업 영역이나 검토가 바뀌었어요.", "Workspace or review changed after approval."));
                admissionError = AdmissionCheck?.Invoke(); if (admissionError != null) throw new InvalidOperationException(admissionError);
                ProjectExecutionPolicy.RequireTool(currentRoot, ToolName, policyArgs);
                // Commit point: the status flips under the lock a discard cancels under. A discard that came first has cancelled this apply before anything is prepared; every later one is refused until the apply ends.
                lock (gate) { linked.Token.ThrowIfCancellationRequested(); status = "applying"; message = string.Empty; }
                Publish();
                var history = new CheckpointStore(environment.DataDirectory, currentRoot);
                var checkpointId = history.Prepare(snapshot); // Fail before mutation if a recoverable snapshot cannot be stored.
                WorkspaceApplyResult applied;
                try { applied = await adapters.WorkspaceEdits.ApplyAsync(snapshot, currentRoot, linked.Token).ConfigureAwait(false); }
                catch (Exception ex)
                {
                    // No result will ever close the prepared record, and history cannot delete a record that still reads "prepared": close it as failed, then rethrow so the review state reports the original error.
                    recordNote = await CloseInterruptedAsync(history, checkpointId, ex, cancellationToken).ConfigureAwait(false);
                    throw;
                }
                lock (gate) result = applied;
                string historyWarning = string.Empty;
                try { history.Complete(checkpointId, applied); } catch (Exception ex) { historyWarning = PompLanguage.Pick(" 체크포인트 결과를 기록하지 못했어요: " + ex.Message, " Checkpoint outcome could not be recorded: " + ex.Message); }
                SetStatus(applied.Succeeded ? "done" : "failed", (applied.Succeeded ? PompLanguage.Pick("선택한 변경을 편집기 버퍼에 적용했어요.", "Selected changes applied to editor buffers.") : applied.Error ?? PompLanguage.Pick("적용에 실패했어요.", "Application failed.")) + historyWarning);
                // The outcome is final now, so a discard may arrive and cancel `linked`: the list goes out under a token only a closing session cancels.
                using (var closing = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, lifetime.Token)) await PublishHistoryAsync(closing.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) { SetStatus("reviewing", PompLanguage.Pick("작업이 취소됐어요.", "Operation cancelled.") + recordNote); }
            catch (Exception ex) { SetStatus("failed", ex.Message + recordNote); }
            finally { lock (gate) active = null; operation.Release(); Publish(); }
        }

        /// <summary>The apply threw or was cancelled after its checkpoint was prepared: complete the record as failed and refresh the history list. Returns a note for the review message when the record could not be closed.</summary>
        private async Task<string> CloseInterruptedAsync(CheckpointStore history, string checkpointId, Exception cause, CancellationToken cancellationToken)
        {
            var note = string.Empty;
            var text = cause is OperationCanceledException
                ? PompLanguage.Pick("적용이 취소돼 파일별 결과를 받지 못했어요.", "The apply was cancelled before it reported per-file results.")
                : PompLanguage.Pick("적용이 오류로 중단돼 파일별 결과를 받지 못했어요: " + cause.Message, "The apply stopped with an error before it reported per-file results: " + cause.Message);
            try { history.Complete(checkpointId, new WorkspaceApplyResult(Array.Empty<WorkspaceFileOutcome>(), text)); }
            catch (Exception ex) { note = PompLanguage.Pick(" 체크포인트 결과를 기록하지 못했어요: " + ex.Message, " Checkpoint outcome could not be recorded: " + ex.Message); }
            using (var closing = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, lifetime.Token)) await PublishHistoryAsync(closing.Token).ConfigureAwait(false);
            return note;
        }

        internal Task<HostToolResult> StageCapturedAsync(string workspaceRoot, ShadowFile captured, CancellationToken token) => ProposeAsync(
            new JObject { ["files"] = new JArray(new JObject { ["path"] = captured.Path, ["beforeHash"] = captured.BeforeHash, ["after"] = captured.After }) }, token, workspaceRoot,
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { [captured.Path] = captured.Before });

        internal Task<HostToolResult> StageAsync(JObject args, CancellationToken cancellationToken) => ProposeAsync(args, cancellationToken);

        private async Task<HostToolResult> ProposeAsync(JObject args, CancellationToken cancellationToken, string? expectedRoot = null, IReadOnlyDictionary<string, string>? exactSources = null)
        {
            lifetime.Token.ThrowIfCancellationRequested();
            var admissionError = AdmissionCheck?.Invoke(); if (admissionError != null) return HostToolResult.Error(admissionError);
            if (args.Properties().Any(p => p.Name != "files" && !p.Name.StartsWith("_", StringComparison.Ordinal)))
                return HostToolResult.Error("Only the files argument is supported.");
            if (!(args["files"] is JArray files) || files.Count == 0 || files.Count > 10)
                return HostToolResult.Error("Provide between one and ten source file proposals.");
            if (!await operation.WaitAsync(0, cancellationToken).ConfigureAwait(false)) return HostToolResult.Error("Another review operation is in progress.");
            try
            {
                lock (gate) if (shadow != null && status != "done") return HostToolResult.Error("A proposal is already awaiting review. Finish or discard it first.");
                var workspaceRoot = await environment.GetWorkspaceRootAsync(cancellationToken).ConfigureAwait(false);
                if (expectedRoot != null && !string.Equals(expectedRoot, workspaceRoot, StringComparison.OrdinalIgnoreCase)) return HostToolResult.Error("Workspace changed during restore preparation.");
                var candidate = new ShadowWorkspace();
                var candidateDiffs = new Dictionary<string, LineDiff>(StringComparer.OrdinalIgnoreCase);
                var candidateSelections = new Dictionary<string, HashSet<int>>(StringComparer.OrdinalIgnoreCase);
                long previewCharacters = 0;
                foreach (var item in files)
                {
                    if (!(item is JObject file) || file.Properties().Any(p => p.Name != "path" && p.Name != "beforeHash" && p.Name != "after") ||
                        file["path"]?.Type != JTokenType.String || file["beforeHash"]?.Type != JTokenType.String || file["after"]?.Type != JTokenType.String)
                        return HostToolResult.Error("Each file needs path, beforeHash and after strings, with no other properties.");
                    var after = file["after"]!.Value<string>()!;
                    if (after.Length > 500000 || after.IndexOf('\0') >= 0) return HostToolResult.Error("A proposed source must be text of at most 500,000 characters.");
                    var captured = WithSourceNewlines(await adapters.CaptureForReviewAsync(workspaceRoot, file["path"]!.Value<string>()!, file["beforeHash"]!.Value<string>()!, after, cancellationToken).ConfigureAwait(false));
                    if (exactSources != null && (!exactSources.TryGetValue(captured.Path, out var exact) || !string.Equals(exact, captured.Before, StringComparison.Ordinal))) return HostToolResult.Error("Source changed after its exact snapshot was captured.");
                    if (candidateDiffs.ContainsKey(captured.Path)) return HostToolResult.Error("A file was proposed more than once.");
                    var diff = candidate.Stage(captured, cancellationToken);
                    previewCharacters += diff.Hunks.Sum(h => (long)h.Before.Length + h.After.Length + 150);
                    if (previewCharacters > 180000) return HostToolResult.Error("The complete review is too large. Propose fewer or smaller edits.");
                    candidateDiffs.Add(captured.Path, diff); candidateSelections.Add(captured.Path, new HashSet<int>(diff.Hunks.Select(h => h.Id)));
                }
                if (candidate.Snapshot().Files.Count == 0) return HostToolResult.Error("The proposal makes no changes.");
                cancellationToken.ThrowIfCancellationRequested();
                if (!string.Equals(workspaceRoot, await environment.GetWorkspaceRootAsync(cancellationToken).ConfigureAwait(false), StringComparison.OrdinalIgnoreCase)) return HostToolResult.Error("Workspace changed during proposal preparation.");
                cancellationToken.ThrowIfCancellationRequested();
                lock (gate)
                {
                    shadow = candidate; diffs = candidateDiffs; selections = candidateSelections; root = workspaceRoot;
                    id = Guid.NewGuid().ToString("N"); status = "reviewing"; message = string.Empty; validation = null; result = null;
                    proposalOrigin = "agent"; editorCurrents = null;
                }
                Publish();
                return HostToolResult.Text("Proposal " + id + " is staged for user review. No files or buffers have changed. The user selects lines, validates the shadow compilation and approves application in POMP.", new JObject { ["proposalId"] = id, ["staged"] = true });
            }
            catch (Exception ex) { return HostToolResult.Error("Could not stage workspace edits: " + ex.Message); }
            finally { operation.Release(); }
        }

        /// <summary>The proposed text with the source file's own line breaks: a model writes "\n" into a CRLF file, which would turn every line into a hunk.</summary>
        private static ShadowFile WithSourceNewlines(ShadowFile file)
        {
            var newline = System.Text.RegularExpressions.Regex.Match(file.Before, "\r\n|\r|\n").Value;
            if (newline.Length == 0) return file;
            var after = System.Text.RegularExpressions.Regex.Replace(file.After, "\r\n|\r|\n", _ => newline);
            return string.Equals(after, file.After, StringComparison.Ordinal) ? file : new ShadowFile(file.Path, file.Before, after);
        }

        private static string ApprovalDiff(ShadowSnapshot snapshot)
        {
            var text = new StringBuilder();
            foreach (var file in snapshot.Files)
            {
                var diff = LineDiff.Create(file.Before, file.After);
                text.Append("--- ").Append(file.Path).Append("\n+++ ").Append(file.Path).Append('\n');
                foreach (var h in diff.Hunks)
                    text.Append("@@ original line ").Append(h.OldLine).Append(" @@\n-").Append(Escape(h.Before)).Append("\n+").Append(Escape(h.After)).Append('\n');
            }
            if (text.Length > 200000) throw new InvalidOperationException(PompLanguage.Pick("승인 미리보기가 너무 커요.", "The approval preview is too large."));
            return text.ToString();
        }
        private static string Escape(string value) => value.Replace("\\", "\\\\").Replace("\r", "\\r").Replace("\n", "\\n\n").Replace("\u0085", "\\u0085").Replace("\u2028", "\\u2028").Replace("\u2029", "\\u2029");
        public void Dispose() { lifetime.Cancel(); lock (gate) { try { active?.Cancel(); } catch (ObjectDisposedException) { } } }

        private sealed class ProposalTool : IHostTool
        {
            private readonly WorkspaceReviewController owner;
            public ProposalTool(WorkspaceReviewController owner) { this.owner = owner; }
            public HostToolDefinition Definition { get; } = new HostToolDefinition {
                Name = ToolName, Label = "Propose workspace edits", LoadMode = "essential",
                Description = "Stage up to ten existing source-file edits in an in-memory shadow workspace without changing live files. Read each with read_buffer first and supply its full content hash. The user reviews line selections, validates with Roslyn and approves apply in the POMP review panel. Do not claim staged changes were applied. Only one active proposal is supported.",
                Parameters = JObject.Parse("{\"type\":\"object\",\"properties\":{\"files\":{\"type\":\"array\",\"items\":{\"type\":\"object\",\"properties\":{\"path\":{\"type\":\"string\"},\"beforeHash\":{\"type\":\"string\"},\"after\":{\"type\":\"string\"}},\"required\":[\"path\",\"beforeHash\",\"after\"],\"additionalProperties\":false}}},\"required\":[\"files\"],\"additionalProperties\":false}") };
            public PermissionKind ResolvePermission(HostToolInvocation invocation) => PermissionKind.None;
            public Task<PermissionRequest?> DescribePermissionAsync(HostToolInvocation invocation, CancellationToken cancellationToken) => Task.FromResult<PermissionRequest?>(null);
            public Task<HostToolResult> ExecuteAsync(HostToolInvocation invocation, CancellationToken cancellationToken) => owner.ProposeAsync(invocation.Arguments, cancellationToken);
        }
    }
}
