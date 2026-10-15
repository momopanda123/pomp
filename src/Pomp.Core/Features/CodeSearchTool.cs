using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;
using Pomp.Hosting;
using Pomp.Permissions;
using Pomp.Tools;
using Pomp.Ui;
using Pomp.Internal;

namespace Pomp.Features
{
    internal sealed class CodeSearchTool : IHostTool, IDisposable
    {
        public const string StateMessage = "workspace/indexState", CommandMessage = "workspace/index";
        private readonly ICodeWorkspaceSource source;
        private readonly IPompHostEnvironment environment;
        private readonly IPompSettings? settings;
        private readonly UiHub? ui;
        private readonly LocalVectorIndex vectors;
        private readonly object configurationGate = new object();
        private readonly SemaphoreSlim operation = new SemaphoreSlim(1, 1);
        private readonly CancellationTokenSource lifetime = new CancellationTokenSource();
        private string identity = string.Empty, configuration = string.Empty, queryNotice = string.Empty;
        // The index page's own notice about a refresh (localized). Never copied into a search result: queryNotice is the model's (English).
        private string refreshNotice = string.Empty;
        private int chunkCount, manualRebuildPending, warmActive, automaticRebuildPending;
        private CancellationTokenSource? rebuildCancellation;
        private CodeIndex? index;
        private readonly Timer refreshTimer;
        public CodeSearchTool(ICodeWorkspaceSource source, IPompHostEnvironment environment, IPompSettings? settings = null, UiHub? ui = null, Func<string, string, ILocalEmbeddingClient>? embeddingClient = null)
        {
            this.source = source; this.environment = environment; this.settings = settings; this.ui = ui;
            refreshTimer = new Timer(_ => WarmAsync(Volatile.Read(ref automaticRebuildPending) != 0, lifetime.Token).Observe(), null, Timeout.Infinite, Timeout.Infinite);
            if (source is IObservableCodeWorkspaceSource observable) observable.Changed += OnSourceChanged;
            configuration = Configuration();
            vectors = new LocalVectorIndex(environment.DataDirectory, embeddingClient); vectors.Changed += OnVectorChanged;
            if (settings != null) settings.Changed += OnSettingsChanged;
        }
        public HostToolDefinition Definition { get; } = new HostToolDefinition {
            Name = "search_codebase", Label = "Search live codebase", LoadMode = "essential",
            Description = "Search loaded live source using cached Roslyn symbols/syntax, BM25 and optionally user-configured loopback embeddings. Default is lexical-only; no hidden remote embedding provider. Includes unsaved source, exact path/line citations and contentHash. Bounded coverage/mode are disclosed. Read current full source with read_buffer before editing; snippets are not complete files.",
            Parameters = JObject.Parse("{\"type\":\"object\",\"properties\":{\"query\":{\"type\":\"string\",\"minLength\":1,\"maxLength\":2000},\"limit\":{\"type\":\"integer\",\"minimum\":1,\"maximum\":20}},\"required\":[\"query\"],\"additionalProperties\":false}") };
        public PermissionKind ResolvePermission(HostToolInvocation invocation) => PermissionKind.None;
        public Task<PermissionRequest?> DescribePermissionAsync(HostToolInvocation invocation, CancellationToken cancellationToken) => Task.FromResult<PermissionRequest?>(null);
        private void OnSourceChanged(object? sender, EventArgs args) { try { if (!lifetime.IsCancellationRequested) refreshTimer.Change(750, Timeout.Infinite); } catch (ObjectDisposedException) { } }
        private void OnVectorChanged(object? sender, EventArgs args) => Publish();
        private LocalEmbeddingConfiguration ReadConfiguration() => (settings as ILocalEmbeddingSettings)?.EmbeddingConfiguration ?? new LocalEmbeddingConfiguration(false, "", "");
        private string Configuration() => ReadConfiguration().Key;
        private void OnSettingsChanged(object? sender, EventArgs args)
        {
            lock (configurationGate)
            {
                if (configuration == Configuration()) return;
                vectors.Disable(PompLanguage.Pick("임베딩 설정이 바뀌었어요.", "Embedding settings changed.")); configuration = Configuration();
                Interlocked.Exchange(ref automaticRebuildPending, 1);
            }
            OnSourceChanged(this, EventArgs.Empty); Publish();
        }
        public void Publish() { if (lifetime.IsCancellationRequested) return; ui?.Post(StateMessage, new JObject { ["state"] = vectors.State, ["message"] = vectors.Message, ["queryNotice"] = refreshNotice.Length > 0 ? refreshNotice : queryNotice,
            ["refreshing"] = Volatile.Read(ref manualRebuildPending) != 0 || Volatile.Read(ref warmActive) != 0, ["chunks"] = chunkCount, ["ready"] = vectors.Ready, ["total"] = vectors.Total, ["enabled"] = ReadConfiguration().Enabled }, UiDelivery.State); }
        public async Task HandleAsync(JObject args, CancellationToken token)
        {
            var action = (string?)args["action"];
            if (action == "cancel") { try { rebuildCancellation?.Cancel(); } catch (ObjectDisposedException) { } vectors.Cancel(); }
            else if (action == "rebuild")
            {
                if (Interlocked.Exchange(ref manualRebuildPending, 1) != 0) { Publish(); return; }
                using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(token, lifetime.Token);
                rebuildCancellation = cancellation; Publish();
                try { await WarmAsync(true, cancellation.Token).ConfigureAwait(false); }
                catch (OperationCanceledException) { refreshNotice = PompLanguage.Pick("인덱스 새로고침이 취소됐어요.", "Index refresh cancelled."); }
                finally { rebuildCancellation = null; Interlocked.Exchange(ref manualRebuildPending, 0); }
            }
            Publish();
        }
        public async Task WarmAsync(bool rebuild, CancellationToken token)
        {
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(token, lifetime.Token);
            if (rebuild) await operation.WaitAsync(linked.Token).ConfigureAwait(false);
            else if (!await operation.WaitAsync(0, linked.Token).ConfigureAwait(false)) return;
            Interlocked.Exchange(ref warmActive, 1); Publish();
            try
            {
                var root = await environment.GetWorkspaceRootAsync(linked.Token).ConfigureAwait(false);
                var snapshot = await source.CaptureAsync(root, linked.Token).ConfigureAwait(false);
                Update(root, snapshot, rebuild, linked.Token); queryNotice = ""; refreshNotice = "";
            }
            catch (OperationCanceledException) { }
            catch (Exception ex) { refreshNotice = PompLanguage.Pick("인덱스 새로고침에 실패했어요: " + ex.Message, "Index refresh failed: " + ex.Message); }
            finally { Interlocked.Exchange(ref warmActive, 0); operation.Release(); Publish(); }
        }
        private void Update(string root, CodeIndexSnapshot snapshot, bool rebuild, CancellationToken token)
        {
            if (index == null || identity != snapshot.Identity) { index = new CodeIndex(snapshot, token); identity = snapshot.Identity; }
            chunkCount = snapshot.Chunks.Count;
            lock (configurationGate)
            {
                var local = ReadConfiguration();
                rebuild |= Interlocked.Exchange(ref automaticRebuildPending, 0) != 0;
                if (local.Enabled)
                {
                    configuration = local.Key;
                    try { vectors.Ensure(root, snapshot, local.Endpoint, local.Model, rebuild); }
                    catch (Exception ex) { vectors.Disable(PompLanguage.Pick("로컬 임베딩 설정이 올바르지 않아요: ", "Invalid local embedding configuration: ") + ex.Message); }
                }
                else { configuration = local.Key; if (vectors.State != "disabled") vectors.Disable(); }
            }
        }

        public async Task<HostToolResult> ExecuteAsync(HostToolInvocation invocation, CancellationToken cancellationToken)
        {
            var query = (string?)invocation.Arguments["query"];
            if (string.IsNullOrWhiteSpace(query) || query!.Length > 2000) return HostToolResult.Error("Use a query between 1 and 2000 characters.");
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, lifetime.Token);
            await operation.WaitAsync(linked.Token).ConfigureAwait(false);
            try
            {
                var root = await environment.GetWorkspaceRootAsync(linked.Token).ConfigureAwait(false);
                var snapshot = await source.CaptureAsync(root, linked.Token).ConfigureAwait(false);
                Update(root, snapshot, false, linked.Token);
                if (!string.Equals(root, await environment.GetWorkspaceRootAsync(linked.Token).ConfigureAwait(false), StringComparison.OrdinalIgnoreCase))
                    return HostToolResult.Error("The workspace changed during indexing; retry in the current solution.");
                var limit = Math.Max(1, Math.Min(20, (int?)invocation.Arguments["limit"] ?? 8));
                var hits = index!.Search(query, 20, linked.Token); var mode = "local-symbol-bm25"; queryNotice = "";
                if (vectors.State == "ready" && vectors.Ready > 0 && ReadConfiguration().Enabled && configuration == Configuration())
                {
                    try { hits = await vectors.SearchAsync(query, snapshot.Chunks, hits, limit, linked.Token).ConfigureAwait(false); mode = vectors.State == "ready" && configuration == Configuration() ? "local-hybrid" : "local-symbol-bm25"; }
                    catch (Exception ex) when (!linked.IsCancellationRequested) { queryNotice = "Local query embedding unavailable; showing lexical results. " + ex.Message; }
                }
                linked.Token.ThrowIfCancellationRequested();
                var limited = hits.Take(limit).ToArray();
                var bounded = limited.TakeWhile((hit, i) => limited.Take(i + 1).Sum(h => h.Chunk.Text.Length) <= 100000).ToArray();
                var notices = snapshot.Notices.Concat(new[] { vectors.Message }).Where(text => text.Length > 0);
                var output = new JObject { ["mode"] = mode, ["embeddingQueryNotice"] = queryNotice, ["outputLimited"] = bounded.Length < limited.Length, ["indexedChunks"] = snapshot.Chunks.Count,
                    ["notices"] = new JArray(notices),
                    ["hits"] = new JArray(bounded.Select(h => new JObject { ["path"] = h.Chunk.Path, ["startLine"] = h.Chunk.StartLine,
                        ["endLine"] = h.Chunk.EndLine, ["symbol"] = h.Chunk.Symbol, ["contentHash"] = h.Chunk.Hash,
                        ["score"] = Math.Round(h.Score, 5), ["text"] = h.Chunk.Text })) };
                return HostToolResult.Text(output.ToString(), output);
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex) { return HostToolResult.Error("Codebase search unavailable: " + ex.Message); }
            finally { operation.Release(); Publish(); }
        }
        public void Dispose() { lifetime.Cancel(); refreshTimer.Dispose(); if (source is IObservableCodeWorkspaceSource observable) observable.Changed -= OnSourceChanged; vectors.Changed -= OnVectorChanged; vectors.Dispose(); if (settings != null) settings.Changed -= OnSettingsChanged; }
    }
}
