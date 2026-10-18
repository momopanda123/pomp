using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;
using Pomp.Adapters;
using Pomp.Diagnostics;
using Pomp.Hosting;
using Pomp.Internal;
using Pomp.Permissions;
using Pomp.Ui;

namespace Pomp.Features
{
    /// <summary>
    /// Results of isolated team members (docs/architecture.md §8.11). omp keeps each one as a git patch next to the member's session
    /// file and never applies it (<c>task.isolation.apply: false</c>). A result waits here and is shown in the review view; files
    /// change only when the user applies it through the approval card, and an applied result is a checkpoint in the change
    /// history like any reviewed edit. Strict throughout: a patch that does not fit exactly is shown with its reason, never forced.
    /// </summary>
    internal sealed class IsolationResults : IDisposable
    {
        public const string StateMessage = "workspace/isolationState";
        public const string CommandMessage = "workspace/isolation";
        /// <summary>The patches held in memory (each one at most GitPatch.MaxBytes): results have no count limit; the oldest applied or failed ones make room first.</summary>
        private const long MaxHeldBytes = 64L * 1024 * 1024;
        private const int MaxFileChars = 1_000_000;
        private const int ApprovalDiffChars = 200_000;
        private static readonly UTF8Encoding StrictUtf8 = new UTF8Encoding(false, true);

        private sealed class Item
        {
            public string Id = Guid.NewGuid().ToString("N");
            public string Session = "", Agent = "", Job = "", PatchPath = "", Root = "", RepoRoot = "", PatchText = "", Status = "pending", Message = "";
            public DateTimeOffset CreatedAt = DateTimeOffset.UtcNow;
            public IReadOnlyList<GitPatch.FileChange> Changes = Array.Empty<GitPatch.FileChange>();
        }

        private sealed class PlannedFile
        {
            public string Path = "", Display = "", Before = "", After = "";
            public GitPatch.FileChange Change = new GitPatch.FileChange();
            public bool Existed, Bom;
        }

        private readonly ITextBufferAdapter buffers;
        private readonly IPompHostEnvironment environment;
        private readonly IPermissionGate permissions;
        private readonly UiHub ui;
        private readonly IPompLogger logger;
        private readonly Action<string, string> notice;
        private readonly Func<string, Task> openFile;
        private readonly object gate = new object();
        private readonly object publishGate = new object();
        private readonly List<Item> items = new List<Item>();
        private readonly HashSet<string> seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        private readonly SemaphoreSlim applying = new SemaphoreSlim(1, 1);
        private readonly CancellationTokenSource lifetime = new CancellationTokenSource();
        private CancellationTokenSource intake = new CancellationTokenSource();
        private string workspace = "", session = "";

        public void SetContext(string root, string sessionId)
        {
            lock (publishGate)
            {
                CancellationTokenSource old;
                lock (gate)
                {
                    if (string.Equals(workspace, root, StringComparison.OrdinalIgnoreCase) && session == sessionId) return;
                    workspace = root; session = sessionId; old = intake; intake = new CancellationTokenSource();
                    items.Clear(); seen.Clear();
                }
                old.Cancel();
                Publish();
            }
        }

        /// <summary>How long a patch may take to appear after its member ended (omp writes it about a second later).</summary>
        internal TimeSpan PatchWait { get; set; } = TimeSpan.FromSeconds(20);

        /// <summary>After an applied result: the change history view refreshes.</summary>
        internal Func<CancellationToken, Task>? Applied { get; set; }

        public IsolationResults(ITextBufferAdapter buffers, IPompHostEnvironment environment, IPermissionGate permissions, UiHub ui, IPompLogger logger,
            Action<string, string> notice, Func<string, Task> openFile)
        {
            this.buffers = buffers; this.environment = environment; this.permissions = permissions; this.ui = ui; this.logger = logger;
            this.notice = notice; this.openFile = openFile;
        }

        public JObject View(string? requestId = null)
        {
            lock (gate)
            {
                var view = new JObject
                {
                    ["requestId"] = requestId,
                    ["items"] = new JArray(items.Select(item => new JObject
                    {
                        ["id"] = item.Id, ["root"] = item.Root, ["agent"] = item.Agent, ["job"] = item.Job, ["status"] = item.Status, ["message"] = item.Message,
                        ["createdAt"] = item.CreatedAt.ToString("O"), ["patchPath"] = item.PatchPath,
                        ["files"] = new JArray(item.Changes.Select(change => new JObject
                        {
                            ["path"] = change.Path, ["kind"] = change.Kind.ToString().ToLowerInvariant(),
                            ["added"] = change.Hunks.Sum(h => h.Lines.Count(l => l.Key == '+')), ["removed"] = change.Hunks.Sum(h => h.Lines.Count(l => l.Key == '-')),
                        })),
                    })),
                };
                // No count limit: the newest results are sent while the message fits the UI hub (StateBudget); `hidden` counts the rest.
                var list = (JArray)view["items"]!; var all = list.ToList(); list.Clear();
                long total = StateBudget.Bytes(new UiMessage(StateMessage, view)) + 2048; var hidden = 0;
                for (var index = all.Count - 1; index >= 0; index--)
                {
                    var size = StateBudget.Bytes(all[index]) + 1;
                    if (hidden == 0 && total + size <= StateBudget.Limit) { list.Insert(0, all[index]); total += size; } else hidden++;
                }
                if (hidden > 0) view["hidden"] = hidden;
                return view;
            }
        }

        public void Publish(string? requestId = null) { lock (publishGate) ui.Post(StateMessage, View(requestId), UiDelivery.State); }
        private void NoticeFor(Item item, string level, string text)
        {
            lock (publishGate)
            {
                lock (gate) if (!string.Equals(workspace, item.Root, StringComparison.OrdinalIgnoreCase) || session != item.Session || intake.IsCancellationRequested) return;
                notice(level, text);
            }
        }

        /// <summary>
        /// A <c>subagent_lifecycle</c> payload. When a member ends, its patch (if it ran isolated) is picked up off this thread;
        /// a member that was not isolated has none, and nothing happens.
        /// </summary>
        public void Observe(JObject payload, string root, string sessionId)
        {
            var status = (string?)payload["status"];
            if (status is not ("completed" or "failed" or "aborted" or "cancelled")) return;
            var sessionFile = payload["sessionFile"]?.Type == JTokenType.String ? (string?)payload["sessionFile"] : null;
            if (sessionFile == null || sessionFile.Length > 2048 || sessionFile.Any(char.IsControl) || !Path.IsPathRooted(sessionFile)
                || !sessionFile.EndsWith(".jsonl", StringComparison.OrdinalIgnoreCase)) return;
            var agent = Bounded((string?)payload["agent"], 80); var job = Bounded((string?)payload["id"], 256);
            var patch = sessionFile.Substring(0, sessionFile.Length - ".jsonl".Length) + ".patch";
            CancellationToken token;
            lock (gate)
            {
                if (root.Length == 0 || !string.Equals(workspace, root, StringComparison.OrdinalIgnoreCase) || session != sessionId) return;
                token = intake.Token;
            }
            Task.Run(() => IntakeAsync(patch, agent, job, root, sessionId, token)).Forget(logger, "isolation result");
        }

        internal async Task IntakeAsync(string patchPath, string agent, string job, CancellationToken token)
        {
            var root = await environment.GetWorkspaceRootAsync(token).ConfigureAwait(false);
            SetContext(root, "");
            CancellationToken pending; lock (gate) pending = intake.Token;
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(token, pending, lifetime.Token);
            await IntakeAsync(patchPath, agent, job, root, "", linked.Token).ConfigureAwait(false);
        }

        private async Task IntakeAsync(string patchPath, string agent, string job, string root, string sessionId, CancellationToken token)
        {
            var deadline = DateTime.UtcNow + PatchWait;
            while (!File.Exists(patchPath))
            {
                if (DateTime.UtcNow >= deadline) return;
                await Task.Delay(250, token).ConfigureAwait(false);
            }

            lock (gate) { token.ThrowIfCancellationRequested(); if (workspace != root || session != sessionId) return; if (seen.Count >= 500) seen.Clear(); if (!seen.Add(Path.GetFullPath(patchPath))) return; }
            var item = new Item { Root = root, Session = sessionId, Agent = agent, Job = job, PatchPath = patchPath };
            try
            {
                // omp writes the file just before; a short retry covers a writer that still holds it.
                item.PatchText = await ReadPatchAsync(patchPath, token).ConfigureAwait(false);
                if (item.PatchText.Trim().Length == 0)
                {
                    NoticeFor(item, "info", PompLanguage.Pick(agent + " · 변경 없음", agent + " · No changes"));
                    return;
                }

                item.RepoRoot = PompSession.FindGitRoot(item.Root) ?? throw new InvalidDataException(PompLanguage.Pick("작업 영역이 Git 저장소가 아니에요.", "The workspace is not a Git repository."));
                item.Changes = GitPatch.Parse(item.PatchText);
                if (item.Changes.Count == 0) { NoticeFor(item, "info", PompLanguage.Pick(agent + " · 텍스트 변경 없음", agent + " · No text changes")); return; }
                foreach (var change in item.Changes) FullPath(item, change);
            }
            catch (Exception ex) when (!(ex is OperationCanceledException))
            {
                item.Status = "blocked"; item.Message = ex.Message;
            }

            bool full;
            lock (gate)
            {
                token.ThrowIfCancellationRequested();
                if (workspace != root || session != sessionId) return;
                // Applied and failed results stay for reference until discarded; when the held patches would pass MaxHeldBytes the oldest of them go first.
                long Held() => items.Sum(i => (long)i.PatchText.Length * sizeof(char));
                var need = (long)item.PatchText.Length * sizeof(char);
                foreach (var old in items.Where(i => i.Status is "applied" or "failed").ToList()) { if (Held() + need <= MaxHeldBytes) break; items.Remove(old); }
                full = Held() + need > MaxHeldBytes;
                if (!full) items.Add(item);
            }

            if (full)
            {
                NoticeFor(item, "warning", PompLanguage.Pick(agent + " · 격리 결과를 둘 공간이 없어요", agent + " · No room for the isolation result"));
                return;
            }

            Publish();
            NoticeFor(item, item.Status == "blocked" ? "warning" : "info", item.Status == "blocked"
                ? PompLanguage.Pick(agent + " · 격리 결과 준비 실패 · " + item.Message, agent + " · Isolation result failed · " + item.Message)
                : PompLanguage.Pick(agent + " · 검토 대기 · 파일 " + item.Changes.Count + "개", agent + " · Awaiting review · " + item.Changes.Count + " files"));
        }

        public async Task HandleAsync(JObject args)
        {
            var action = (string?)args["action"] ?? "refresh"; var id = (string?)args["id"] ?? "";
            var requestId = args["requestId"]?.Type == JTokenType.String ? Bounded((string?)args["requestId"], 128) : null;
            Item? selected = null;
            try
            {
                if (action == "refresh") return;
                var root = await environment.GetWorkspaceRootAsync(lifetime.Token).ConfigureAwait(false);
                if (!string.Equals((string?)args["root"], root, StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException(PompLanguage.Pick("작업 영역이 바뀌었어요.", "The workspace changed."));
                Item item; CancellationToken pending;
                lock (gate)
                {
                    item = selected = items.FirstOrDefault(i => i.Id == id) ?? throw new InvalidOperationException(PompLanguage.Pick("격리 결과가 더 이상 없어요.", "The isolated result no longer exists."));
                    pending = intake.Token;
                }
                switch (action)
                {
                    case "discard":
                        lock (gate) { if (item.Status == "applying") throw new InvalidOperationException(PompLanguage.Pick("적용 중인 결과는 버릴 수 없어요.", "A result being applied cannot be discarded.")); items.Remove(item); }
                        break;
                    case "openPatch":
                        await openFile(item.PatchPath).ConfigureAwait(false);
                        break;
                    case "apply":
                        using (var linked = CancellationTokenSource.CreateLinkedTokenSource(pending, lifetime.Token))
                            await ApplyAsync(item, root, linked.Token).ConfigureAwait(false);
                        break;
                    default: throw new ArgumentException(PompLanguage.Pick("알 수 없는 격리 결과 동작이에요.", "Unknown isolated result action."));
                }
            }
            catch (OperationCanceledException) { }
            catch (Exception ex)
            {
                lock (gate) { var item = items.FirstOrDefault(i => i.Id == id); if (item != null && item.Status != "applied") { if (item.Status == "applying") item.Status = "pending"; item.Message = ex.Message; } }
                if (selected == null) notice("warning", ex.Message); else NoticeFor(selected, "warning", ex.Message);
            }
            finally { Publish(requestId); }
        }

        private async Task ApplyAsync(Item item, string root, CancellationToken token)
        {
            if (!await applying.WaitAsync(0, token).ConfigureAwait(false)) throw new InvalidOperationException(PompLanguage.Pick("다른 격리 결과를 적용하는 중이에요.", "Another isolated result is being applied."));
            try
            {
                lock (gate)
                {
                    token.ThrowIfCancellationRequested();
                    if (!items.Contains(item) || !string.Equals(workspace, item.Root, StringComparison.OrdinalIgnoreCase) || session != item.Session) throw new OperationCanceledException(token);
                    if (item.Status != "pending") throw new InvalidOperationException(item.Status == "blocked" ? item.Message : PompLanguage.Pick("이 결과는 적용할 수 없는 상태예요.", "This result cannot be applied now."));
                    if (!string.Equals(item.Root, root, StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException(PompLanguage.Pick("다른 작업 영역의 결과예요.", "This result belongs to another workspace."));
                    item.Status = "applying"; item.Message = "";
                }

                Publish();
                var plan = await PlanAsync(item, token).ConfigureAwait(false);
                var policyArgs = new JObject { ["files"] = new JArray(plan.Select(f => new JObject { ["path"] = f.Path })) };
                ProjectExecutionPolicy.RequireTool(root, WorkspaceReviewController.ToolName, policyArgs);
                var created = plan.Count(f => !f.Existed); var deleted = plan.Count(f => f.Change.Kind == GitPatch.ChangeKind.Delete);
                var answer = await permissions.RequestAsync(new PermissionRequest
                {
                    Source = PermissionSource.HostTool, Kind = PermissionKind.FileWrite, ToolName = WorkspaceReviewController.ToolName, Target = root, CanRemember = false,
                    Title = PompLanguage.Pick("구성원 " + item.Agent + "의 격리 결과를 적용할까요?", "Apply member " + item.Agent + "'s isolated result?"),
                    Message = PompLanguage.Pick("파일 " + plan.Count + "개 · 추가 " + created + "개 · 삭제 " + deleted + "개",
                        plan.Count + " files · " + created + " added · " + deleted + " deleted"),
                    Diff = item.PatchText.Length > ApprovalDiffChars ? item.PatchText.Substring(0, ApprovalDiffChars) : item.PatchText,
                }, token).ConfigureAwait(false);
                if (!answer.IsAllowed)
                {
                    lock (gate) { item.Status = "pending"; item.Message = PompLanguage.Pick("적용이 승인되지 않았어요.", "Application was not approved."); }
                    return;
                }

                // The files may have changed while the card was open: plan again and require the same starting point.
                var again = await PlanAsync(item, token).ConfigureAwait(false);
                if (again.Count != plan.Count || again.Zip(plan, (a, b) => a.Before == b.Before && a.Existed == b.Existed).Any(same => !same))
                    throw new InvalidOperationException(PompLanguage.Pick("승인하는 동안 파일이 바뀌었어요.", "Files changed while the approval was open."));
                if (!string.Equals(root, await environment.GetWorkspaceRootAsync(token).ConfigureAwait(false), StringComparison.OrdinalIgnoreCase))
                    throw new InvalidOperationException(PompLanguage.Pick("승인 후 작업 영역이 바뀌었어요.", "The workspace changed after approval."));
                ProjectExecutionPolicy.RequireTool(root, WorkspaceReviewController.ToolName, policyArgs);

                var history = new CheckpointStore(environment.DataDirectory, root);
                var checkpoint = history.Prepare(new ShadowSnapshot(0, again.Select(f => new ShadowFile(f.Path, f.Before, f.After)).ToList().AsReadOnly()));
                token.ThrowIfCancellationRequested();
                var result = Write(again);
                string warning = "";
                try { history.Complete(checkpoint, result); } catch (Exception ex) { warning = PompLanguage.Pick(" 체크포인트 결과를 기록하지 못했어요: " + ex.Message, " The checkpoint outcome could not be recorded: " + ex.Message); }
                lock (gate)
                {
                    item.Status = result.Succeeded ? "applied" : "failed";
                    item.Message = (result.Succeeded ? PompLanguage.Pick("적용했어요.", "Applied.") : result.Error ?? PompLanguage.Pick("적용하지 못했어요. 바꾼 파일은 되돌렸어요.", "Not applied; written files were restored.")) + warning;
                }

                if (Applied != null) await Applied(token).ConfigureAwait(false);
            }
            finally { applying.Release(); }
        }

        /// <summary>What applying the result would write now: unsaved editor changes and files that no longer fit refuse it.</summary>
        private async Task<List<PlannedFile>> PlanAsync(Item item, CancellationToken token)
        {
            var plan = new List<PlannedFile>();
            foreach (var change in item.Changes)
            {
                var full = FullPath(item, change);
                CheckpointStore.CheckDirectPath(full);
                var buffer = await buffers.ReadAsync(full, token).ConfigureAwait(false);
                if (buffer?.IsDirty == true) throw new InvalidOperationException(PompLanguage.Pick("저장하지 않은 변경이 있는 파일이에요: " + change.Path, "This file has unsaved changes: " + change.Path));
                var planned = new PlannedFile { Path = full, Display = change.Path, Change = change, Existed = File.Exists(full) };
                if (planned.Existed)
                {
                    var bytes = await Task.Run(() => File.ReadAllBytes(full), token).ConfigureAwait(false);
                    planned.Bom = bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF;
                    try { planned.Before = StrictUtf8.GetString(bytes, planned.Bom ? 3 : 0, bytes.Length - (planned.Bom ? 3 : 0)); }
                    catch (DecoderFallbackException) { throw new InvalidOperationException(PompLanguage.Pick("UTF-8이 아닌 파일은 적용할 수 없어요: " + change.Path, "Files that are not UTF-8 cannot be applied: " + change.Path)); }
                }

                if (change.Kind == GitPatch.ChangeKind.Create && planned.Existed) throw new InvalidOperationException(PompLanguage.Pick("새로 만들 파일이 이미 있어요: " + change.Path, "The file to create already exists: " + change.Path));
                if (change.Kind != GitPatch.ChangeKind.Create && !planned.Existed) throw new InvalidOperationException(PompLanguage.Pick("바꿀 파일이 없어요: " + change.Path, "The file to change does not exist: " + change.Path));
                planned.After = GitPatch.Apply(change, planned.Before);
                if (planned.Before.Length > MaxFileChars || planned.After.Length > MaxFileChars) throw new InvalidOperationException(PompLanguage.Pick("파일이 너무 커요: " + change.Path, "The file is too large: " + change.Path));
                plan.Add(planned);
            }

            return plan;
        }

        /// <summary>Writes every file or none: a failure restores what was already written (and removes created files).</summary>
        private WorkspaceApplyResult Write(IReadOnlyList<PlannedFile> plan)
        {
            var done = new List<PlannedFile>();
            try
            {
                foreach (var file in plan)
                {
                    CheckpointStore.CheckDirectPath(file.Path);
                    if (file.Change.Kind == GitPatch.ChangeKind.Delete) File.Delete(file.Path);
                    else AtomicFile.WriteAllText(file.Path, file.After, file.Bom);
                    done.Add(file);
                }

                return new WorkspaceApplyResult(plan.Select(f => new WorkspaceFileOutcome(f.Path, WorkspaceFileState.Applied)));
            }
            catch (Exception ex)
            {
                var outcomes = new List<WorkspaceFileOutcome>();
                foreach (var file in done.AsEnumerable().Reverse())
                {
                    try
                    {
                        if (file.Existed) AtomicFile.WriteAllText(file.Path, file.Before, file.Bom); else File.Delete(file.Path);
                        outcomes.Add(new WorkspaceFileOutcome(file.Path, WorkspaceFileState.RolledBack));
                    }
                    catch (Exception rollback) { outcomes.Add(new WorkspaceFileOutcome(file.Path, WorkspaceFileState.Conflict, rollback.Message)); }
                }

                logger.Warn("Isolated result was not applied: " + ex.Message);
                return new WorkspaceApplyResult(outcomes, ex.Message);
            }
        }

        /// <summary>A patch path (repository-relative) as a file inside the workspace; anything else refuses the result.</summary>
        private static string FullPath(Item item, GitPatch.FileChange change)
        {
            var full = Path.GetFullPath(Path.Combine(item.RepoRoot, change.Path.Replace('/', Path.DirectorySeparatorChar)));
            var root = Path.GetFullPath(item.Root).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
            if (!full.StartsWith(root, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException(PompLanguage.Pick("작업 영역 밖의 파일을 바꾸는 결과예요: " + change.Path, "The result changes a file outside the workspace: " + change.Path));
            return full;
        }

        private static async Task<string> ReadPatchAsync(string path, CancellationToken token)
        {
            for (var attempt = 0; ; attempt++)
            {
                try
                {
                    CheckpointStore.CheckDirectPath(path);
                    var info = new FileInfo(path);
                    if (info.Length > GitPatch.MaxBytes) throw new InvalidDataException(PompLanguage.Pick("패치가 너무 커요.", "The patch is too large."));
                    return StrictUtf8.GetString(File.ReadAllBytes(path));
                }
                catch (IOException) when (attempt < 5) { await Task.Delay(200, token).ConfigureAwait(false); }
                catch (DecoderFallbackException) { throw new InvalidDataException(PompLanguage.Pick("패치가 UTF-8 텍스트가 아니에요.", "The patch is not UTF-8 text.")); }
            }
        }

        private static string Bounded(string? value, int max) => string.IsNullOrEmpty(value) ? "" : new string(value!.Where(c => !char.IsControl(c)).Take(max).ToArray());

        public void Dispose() { lifetime.Cancel(); CancellationTokenSource pending; lock (gate) pending = intake; pending.Cancel(); }
    }
}
