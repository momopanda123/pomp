using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;
using Pomp.Adapters;
using Pomp.Hosting;
using Pomp.Tools;
using Pomp.Ui;

namespace Pomp.Features
{
    /// <summary>Current evidence-backed agent findings and live IDE diagnostics, independent of review scans.</summary>
    internal sealed class ProblemsController : IDisposable
    {
        private readonly IVsAdapterFactory adapters;
        private readonly IPompHostEnvironment environment;
        private readonly CodeReviewController? review;
        private readonly UiHub ui;
        private readonly object gate = new object();
        private readonly SemaphoreSlim operation = new SemaphoreSlim(1, 1);
        private readonly CancellationTokenSource lifetime = new CancellationTokenSource();
        private readonly Timer debounce;
        private readonly IProjectEventSource? events;
        private readonly IObservableCodeWorkspaceSource? source;
        private ProblemsStatePayload state = new ProblemsStatePayload();
        private Dictionary<string, string[]> agentIds = new Dictionary<string, string[]>();
        private long epoch;
        private bool disposed;
        internal TimeSpan DebounceDelay { get; set; } = TimeSpan.FromSeconds(1);

        public ProblemsController(IVsAdapterFactory adapters, IPompHostEnvironment environment, CodeReviewController? review, UiHub ui)
        {
            this.adapters = adapters; this.environment = environment; this.review = review; this.ui = ui;
            debounce = new Timer(_ => RefreshObservedAsync(), null, Timeout.Infinite, Timeout.Infinite);
            if (adapters.ErrorList is IObservableErrorListAdapter errors) errors.Changed += OnChanged;
            if (review != null) review.Changed += OnChanged;
            events = (adapters as IProjectEventAdapters)?.ProjectEvents;
            if (events != null) events.Occurred += OnProjectEvent;
            source = (adapters as ICodeIndexAdapters)?.CodeSource as IObservableCodeWorkspaceSource;
            if (source != null) source.Changed += OnChanged;
            adapters.Solution.SolutionChanged += OnWorkspaceChanged;
        }
        private void OnProjectEvent(object? sender, ProjectEvent args) => Schedule();
        private void OnChanged(object? sender, EventArgs args) => Schedule();
        private void OnWorkspaceChanged(object? sender, EventArgs args)
        {
            lock (gate) { if (disposed) return; epoch++; state = new ProblemsStatePayload(); agentIds.Clear(); }
            Schedule(TimeSpan.Zero);
        }
        private void Schedule(TimeSpan? delay = null)
        {
            lock (gate) if (!disposed) debounce.Change(delay ?? DebounceDelay, Timeout.InfiniteTimeSpan);
        }
        private async void RefreshObservedAsync()
        {
            try { await RefreshAsync(lifetime.Token).ConfigureAwait(false); }
            catch (OperationCanceledException) { }
            catch { /* A later explicit refresh reports an error; background diagnostics never enter chat. */ }
        }
        internal static string ProblemId(string source, string code, string path, int line, string message) =>
            ContentHash.Compute(source + "\n" + code + "\n" + path + "\n" + line.ToString(CultureInfo.InvariantCulture) + "\n" + message);
        private static string Bound(string? value, int limit) => value == null ? "" : value.Length <= limit ? value : value.Substring(0, limit);
        private sealed class WorkspacePaths
        {
            public WorkspacePaths(string root)
            {
                var full = Path.GetFullPath(root);
                Root = full.Length == (Path.GetPathRoot(full) ?? "").Length ? full : full.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            }
            public string Root { get; }
            public bool TryResolve(string? path, out string full, out string error)
            {
                full = ""; error = "";
                try
                {
                    if (path is null || string.IsNullOrWhiteSpace(path) || path.Any(char.IsControl) || path.StartsWith(@"\\", StringComparison.Ordinal) || path.StartsWith("//", StringComparison.Ordinal)) return false;
                    var colon = path.IndexOf(':');
                    if (colon >= 0 && (colon != 1 || path.Length < 3 || !char.IsLetter(path[0]) || path[2] != '\\' && path[2] != '/' || path.IndexOf(':', 2) >= 0)) return false;
                    if (Path.DirectorySeparatorChar == '\\' && Path.IsPathRooted(path) && colon != 1) return false;
                    full = Path.GetFullPath(Path.IsPathRooted(path) ? path : Path.Combine(Root, path));
                    var prefix = Root.EndsWith(Path.DirectorySeparatorChar.ToString(), StringComparison.Ordinal) ? Root : Root + Path.DirectorySeparatorChar;
                    if (!full.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) || string.Equals(full, Root, StringComparison.OrdinalIgnoreCase)) return false;
                    CheckpointStore.CheckDirectPath(full); return true;
                }
                catch (Exception) { full = ""; return false; }
            }
        }
        private static string RelativePath(WorkspacePaths guard, string? path)
        {
            if (string.IsNullOrWhiteSpace(path)) return "";
            if (!guard.TryResolve(path, out var full, out _)) return "";
            return full.Length > guard.Root.Length ? full.Substring(guard.Root.Length).TrimStart('\\', '/').Replace('\\', '/') : "";
        }
        private static List<ProblemItemPayload> Sort(IEnumerable<ProblemItemPayload> items) => items
            .GroupBy(item => item.Id, StringComparer.Ordinal).Select(group => group.First())
            .OrderBy(item => item.Severity == "error" ? 0 : 1).ThenBy(item => item.Path, StringComparer.OrdinalIgnoreCase)
            .ThenBy(item => item.Line).ThenBy(item => item.Code, StringComparer.Ordinal).ThenBy(item => item.Id, StringComparer.Ordinal).ToList();

        public async Task RefreshAsync(CancellationToken token)
        {
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(token, lifetime.Token);
            await operation.WaitAsync(linked.Token).ConfigureAwait(false);
            try { await RefreshLockedAsync(linked.Token).ConfigureAwait(false); }
            finally { operation.Release(); }
        }
        private async Task RefreshLockedAsync(CancellationToken token)
        {
            long captured; lock (gate) { if (disposed) return; captured = epoch; }
            var root = "";
            try
            {
                root = await environment.GetWorkspaceRootAsync(token).ConfigureAwait(false);
                if (review != null) await review.LoadSnapshotAsync(token).ConfigureAwait(false);
                var compiler = new List<ProblemItemPayload>();
                var agents = new List<ProblemItemPayload>();
                var ids = new Dictionary<string, List<string>>();
                if (!string.IsNullOrWhiteSpace(root))
                {
                    var guard = new WorkspacePaths(root);
                    var rows = await adapters.ErrorList.ReadAsync(new ErrorListFilter { Severities = new List<ErrorSeverity> { ErrorSeverity.Error, ErrorSeverity.Warning }, MaxItems = 0, OrderByLocation = true, WorkspaceRoot = root }, token).ConfigureAwait(false);
                    foreach (var row in rows)
                    {
                        if (row.Severity != ErrorSeverity.Error && row.Severity != ErrorSeverity.Warning || string.IsNullOrWhiteSpace(row.Description) && string.IsNullOrWhiteSpace(row.Code)) continue;
                        var path = RelativePath(guard, row.File);
                        if (!string.IsNullOrWhiteSpace(row.File) && path.Length == 0) continue;
                        var item = new ProblemItemPayload { Source = "compiler", Severity = row.Severity == ErrorSeverity.Error ? "error" : "warning", Code = Bound(row.Code, 100), Message = Bound(row.Description, 3000), Path = path,
                            Line = Math.Max(0, Math.Min(10000000, row.Line ?? 0)), Column = row.Column > 0 ? Math.Min(10000000, row.Column.Value) : (int?)null, Project = string.IsNullOrEmpty(row.Project) ? null : Bound(row.Project, 200) };
                        item.Id = ProblemId(item.Source, item.Code, item.Path, item.Line, item.Message); compiler.Add(item);
                    }
                    foreach (var finding in review?.AgentFindings(root) ?? Array.Empty<ReviewFinding>())
                    {
                        var path = RelativePath(guard, finding.Path);
                        if (path.Length == 0 || string.IsNullOrWhiteSpace(finding.Message) && string.IsNullOrWhiteSpace(finding.Code)) continue;
                        // Suggestions remain suggestions in the model API; the problem list has only error/warning categories.
                        var item = new ProblemItemPayload { Source = "agent", Severity = finding.Severity == "error" ? "error" : "warning", Code = finding.Code, Message = finding.Message, Path = path, Line = finding.Line,
                            Evidence = string.IsNullOrWhiteSpace(finding.Evidence) ? null : Bound(finding.Evidence, 4000), Reporter = string.IsNullOrEmpty(finding.Reporter) ? null : finding.Reporter };
                        item.Id = ProblemId(item.Source, item.Code, item.Path, item.Line, item.Message); agents.Add(item);
                        if (!ids.TryGetValue(item.Id, out var matched)) ids[item.Id] = matched = new List<string>();
                        matched.Add(finding.Id);
                    }
                }
                compiler = Sort(compiler); agents = Sort(agents);
                var next = new ProblemsStatePayload { Root = root, Items = Sort(compiler.Concat(agents)), UpdatedUtc = DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture) };
                // No count limit: every problem is sent while the message fits the UI hub (StateBudget); `truncated` says that some were left out.
                next.Truncated = StateBudget.Fit(next.Items, StateBudget.Bytes(new UiMessage(UiMessageTypes.ProblemsState, new ProblemsStatePayload { Root = root, Truncated = true, UpdatedUtc = next.UpdatedUtc })) + 2048) > 0;
                if (!string.Equals(root, await environment.GetWorkspaceRootAsync(token).ConfigureAwait(false), StringComparison.OrdinalIgnoreCase)) { Schedule(TimeSpan.Zero); return; }
                token.ThrowIfCancellationRequested();
                lock (gate)
                {
                    if (disposed || captured != epoch) { Schedule(TimeSpan.Zero); return; }
                    state = next; agentIds = ids.ToDictionary(pair => pair.Key, pair => pair.Value.ToArray());
                    ui.Post(UiMessageTypes.ProblemsState, next, UiDelivery.State);
                }
            }
            catch
            {
                lock (gate)
                {
                    if (!disposed && captured == epoch && !token.IsCancellationRequested)
                    {
                        state = new ProblemsStatePayload { Root = root, UpdatedUtc = DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture) };
                        agentIds.Clear(); ui.Post(UiMessageTypes.ProblemsState, state, UiDelivery.State);
                    }
                }
                throw;
            }
        }
        private sealed class ProblemException : InvalidOperationException { public ProblemException(string message) : base(message) { } }
        private static string Error(string ko, string en) => PompLanguage.Pick(ko, en);
        public async Task HandleAsync(JObject args, CancellationToken token)
        {
            if (args["requestId"]?.Type != JTokenType.String) return;
            var requestId = (string)args["requestId"]!;
            if (requestId.Length == 0 || requestId.Length > 128 || requestId.Any(char.IsControl)) return;
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(token, lifetime.Token);
            var reply = new ProblemsReplyPayload { RequestId = requestId };
            await operation.WaitAsync(linked.Token).ConfigureAwait(false);
            try
            {
                var action = args["action"]?.Type == JTokenType.String ? (string?)args["action"] : null;
                if (action != "refresh" && action != "open" && action != "dismiss") throw new ProblemException(Error("알 수 없는 문제 작업이에요.", "Unknown problem action."));
                if (action != "refresh")
                {
                    if (args["id"]?.Type != JTokenType.String || ((string)args["id"]!).Length != 64) throw new ProblemException(Error("문제 식별자가 올바르지 않아요.", "Invalid problem identity."));
                    ProblemItemPayload? item; string root; string[] mapped; long captured;
                    lock (gate) { root = state.Root; captured = epoch; item = state.Items.FirstOrDefault(row => row.Id == (string?)args["id"]); mapped = item != null && agentIds.TryGetValue(item.Id, out var saved) ? saved : Array.Empty<string>(); }
                    if (item == null) throw new ProblemException(Error("문제를 찾지 못했어요.", "Problem not found."));
                    if (!string.Equals(root, await environment.GetWorkspaceRootAsync(linked.Token).ConfigureAwait(false), StringComparison.OrdinalIgnoreCase)) throw new ProblemException(Error("작업 영역이 바뀌었어요.", "Workspace changed."));
                    lock (gate) if (captured != epoch) throw new ProblemException(Error("작업 영역이 바뀌었어요.", "Workspace changed."));
                    if (action == "dismiss")
                    {
                        if (item.Source != "agent" || review == null) throw new ProblemException(Error("컴파일러 문제는 삭제할 수 없어요.", "Compiler problems cannot be dismissed."));
                        var removed = false;
                        foreach (var finding in mapped) removed |= await review.DismissAgentAsync(root, finding, linked.Token).ConfigureAwait(false);
                        if (!removed) throw new ProblemException(Error("문제가 바뀌었어요.", "Problem changed."));
                    }
                    else
                    {
                        if (item.Path.Length == 0 || !new WorkspacePaths(root).TryResolve(item.Path, out var file, out _) || !await adapters.Editor.OpenDocumentAsync(file, Math.Max(1, item.Line), linked.Token).ConfigureAwait(false))
                            throw new ProblemException(Error("문제의 소스를 열지 못했어요.", "Could not open the problem source."));
                    }
                }
                reply.Ok = true;
            }
            catch (ProblemException ex) { reply.Error = ex.Message; }
            catch (Exception) { reply.Error = Error("문제 작업에 실패했어요.", "The problem operation failed."); }
            finally
            {
                try { await RefreshLockedAsync(linked.Token).ConfigureAwait(false); }
                catch (Exception) { reply.Ok = false; reply.Error = Error("문제 목록을 읽지 못했어요.", "Could not read the problem list."); }
                ui.Post(UiMessageTypes.ProblemsReply, reply, UiDelivery.State); operation.Release();
            }
        }
        public void Dispose()
        {
            lock (gate) { disposed = true; epoch++; }
            lifetime.Cancel(); debounce.Dispose();
            if (adapters.ErrorList is IObservableErrorListAdapter errors) errors.Changed -= OnChanged;
            if (review != null) review.Changed -= OnChanged;
            if (events != null) events.Occurred -= OnProjectEvent;
            if (source != null) source.Changed -= OnChanged;
            adapters.Solution.SolutionChanged -= OnWorkspaceChanged;
        }
    }
}
