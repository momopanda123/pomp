using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;
using Pomp.Adapters;
using Pomp.Bridge;
using Pomp.Diagnostics;
using Pomp.Features;
using Pomp.Internal;
using Pomp.Tools;
using Pomp.Ui;

namespace Pomp.Hosting
{
    /// <summary>One UI and one independent omp engine per open tab. The selected engine owns every
    /// conversation-scoped UI command and projection; inactive engines retain their transcripts and pending requests.</summary>
    public sealed class PompConversationHost : IPompSession, IEditorSessionContextSource, IKnowledgeEditor
    {
        private readonly object _gate = new object();
        private readonly IPompSettings _settings;
        private readonly IPompHostEnvironment _environment;
        private readonly IVsAdapterFactory _adapters;
        private readonly IPompLogger _logger;
        private readonly Func<ICustomToolRegistry> _registry;
        private readonly IOmpProcessLauncher? _launcher;
        private readonly PompSessionOptions? _options;
        private readonly ConversationTabStore _tabs;
        private readonly SerialQueue _tabCommands;
        private readonly Dictionary<PompSession, Channel> _channels = new Dictionary<PompSession, Channel>();
        private IUiChannel? _page;
        private PompSession _active;
        private string _root = "";
        private bool _disposed;
        private CancellationTokenSource _workspaceEpoch = new CancellationTokenSource();
        private CancellationTokenSource? _workspaceDebounce;

        public PompConversationHost(IPompSettings settings, IPompHostEnvironment environment, IVsAdapterFactory adapters,
            IPompLogger logger, Func<ICustomToolRegistry> registry, IOmpProcessLauncher? launcher = null)
            : this(settings, environment, adapters, logger, registry, launcher, null) { }

        internal PompConversationHost(IPompSettings settings, IPompHostEnvironment environment, IVsAdapterFactory adapters,
            IPompLogger logger, Func<ICustomToolRegistry> registry, IOmpProcessLauncher? launcher, PompSessionOptions? options)
        {
            _settings = settings ?? throw new ArgumentNullException(nameof(settings));
            _environment = environment ?? throw new ArgumentNullException(nameof(environment));
            _adapters = adapters ?? throw new ArgumentNullException(nameof(adapters));
            _logger = logger ?? NullPompLogger.Instance;
            _registry = registry ?? throw new ArgumentNullException(nameof(registry));
            _launcher = launcher;
            _options = options;
            _tabs = new ConversationTabStore(environment.DataDirectory, _logger);
            _tabCommands = new SerialQueue(_logger);
            _active = CreateChild();
            try { _adapters.Solution.SolutionChanged += OnSolutionChanged; }
            catch (Exception ex) { _logger.Warn("Could not subscribe to workspace changes: " + ex.Message); }
        }

        public PompSession ActiveSession { get { lock (_gate) return _active; } }
        public IOmpProcessBridge Bridge => ActiveSession.Bridge;
        public EditorSessionContext EditorSessionContext => ActiveSession.EditorSessionContext;
        public event EventHandler? KnowledgeChanged;
        public IReadOnlyList<KnowledgeTagInfo> ListKnowledge() => ActiveSession.ListKnowledge();
        public Task AddKnowledgeLinesAsync(string id, KnowledgeDraft draft, CancellationToken token) => ActiveSession.AddKnowledgeLinesAsync(id, draft, token);
        public void OpenKnowledgeDraft(KnowledgeDraft draft) => ActiveSession.OpenKnowledgeDraft(draft);
        public IReadOnlyList<KnowledgeLineMark> KnowledgeLines(string path) => ActiveSession.KnowledgeLines(path);

        private PompSession CreateChild()
        {
            var session = new PompSession(_settings, _environment, _adapters, _registry(), _logger, _launcher, _options, managedWorkspace: true);
            session.KnowledgeChanged += OnKnowledgeChanged;
            session.ConversationStateChanged += OnConversationStateChanged;
            _channels.Add(session, new Channel(this, session));
            return session;
        }

        private void OnKnowledgeChanged(object? sender, EventArgs e) => KnowledgeChanged?.Invoke(this, e);
        private void OnConversationStateChanged(object? sender, EventArgs e) => PublishTabs();

        public void AttachUi(IUiChannel channel)
        {
            if (channel == null) throw new ArgumentNullException(nameof(channel));
            PompSession active; Channel proxy;
            lock (_gate)
            {
                if (_disposed) throw new ObjectDisposedException(nameof(PompConversationHost));
                if (ReferenceEquals(_page, channel)) return;
                if (_page != null) _page.MessageReceived -= Receive;
                _page = channel;
                channel.MessageReceived += Receive;
                active = _active; proxy = _channels[active];
            }
            active.DetachUi(proxy);
            active.AttachUi(proxy);
        }

        public void DetachUi(IUiChannel channel)
        {
            PompSession active; Channel proxy;
            lock (_gate)
            {
                if (!ReferenceEquals(_page, channel)) return;
                _page = null; channel.MessageReceived -= Receive;
                active = _active; proxy = _channels[active];
            }
            active.DetachUi(proxy);
        }

        private void Receive(object? sender, UiMessage message)
        {
            lock (_gate) if (_disposed || !ReferenceEquals(sender, _page)) return;
            if (message.Type == UiMessageTypes.ConversationTab)
            {
                _tabCommands.Enqueue(() => HandleTabAsync(message.Payload ?? new JObject()));
                return;
            }
            if (message.Type == UiMessageTypes.SessionHistory && (string?)message.Payload?["action"] == "switch")
            {
                _tabCommands.Enqueue(() => HandleOpenHistoryTabAsync(message.Payload ?? new JObject()));
                return;
            }
            if (message.Type == UiMessageTypes.SessionHistory && (string?)message.Payload?["action"] == "delete")
            {
                _tabCommands.Enqueue(async () =>
                {
                    var owner = ActiveSession;
                    // Keep tab open/close/history switches serialized until recycling has finished.
                    await owner.RunHistoryCommandAsync(message.Payload ?? new JObject(), (id, path) =>
                    {
                        PompSession[] sessions;
                        lock (_gate) sessions = _channels.Keys.Where(session => !ReferenceEquals(session, owner)).ToArray();
                        return sessions.Any(session => session.ConversationId == id ||
                            string.Equals(session.ConversationFile, path, StringComparison.OrdinalIgnoreCase));
                    }).ConfigureAwait(false);
                });
                return;
            }
            PompSession active; Channel proxy;
            lock (_gate) { if (_disposed) return; active = _active; proxy = _channels[active]; }
            proxy.Deliver(message);
        }
        private void OnSolutionChanged(object? sender, EventArgs e)
        {
            CancellationTokenSource old, next, debounce;
            CancellationTokenSource? pending;
            PompSession[] sessions;
            lock (_gate)
            {
                if (_disposed) return;
                old = _workspaceEpoch; _workspaceEpoch = next = new CancellationTokenSource();
                pending = _workspaceDebounce;
                debounce = _workspaceDebounce = new CancellationTokenSource();
                sessions = _channels.Keys.ToArray();
            }
            old.Cancel(); old.Dispose(); pending?.Cancel(); pending?.Dispose();
            foreach (var session in sessions) session.FreezeWorkspace();
            _ = SettleWorkspaceAsync(next.Token, debounce.Token);
        }

        private async Task SettleWorkspaceAsync(CancellationToken epoch, CancellationToken debounce)
        {
            try
            {
                await Task.Delay(TimeSpan.FromMilliseconds(750), debounce).ConfigureAwait(false);
                await _tabCommands.Enqueue(async () =>
                {
                    var root = await WorkspaceRootAsync(epoch).ConfigureAwait(false);
                    PompSession[] old; PompSession previous, replacement; IUiChannel? page; Channel oldProxy, newProxy;
                    lock (_gate)
                    {
                        epoch.ThrowIfCancellationRequested();
                        if (SameRoot(root, _root) && SameRoot(_active.ConversationRoot, root))
                        {
                            foreach (var session in _channels.Keys) session.UnfreezeWorkspace();
                            return;
                        }
                        old = _channels.Keys.ToArray();
                        previous = _active;
                        replacement = CreateChild();
                        page = _page; oldProxy = _channels[previous]; newProxy = _channels[replacement];
                    }
                    replacement.InitializeConversation(root, null, null, null);
                    try
                    {
                        await replacement.EnsureStartedAsync(epoch).ConfigureAwait(false);
                        if (page != null)
                        {
                            newProxy.DeferReady();
                            replacement.AttachUi(newProxy);
                        }
                        lock (_gate)
                        {
                            epoch.ThrowIfCancellationRequested();
                            _root = root; _active = replacement;
                        }
                    }
                    catch { RemoveChild(replacement); throw; }
                    if (page != null)
                    {
                        previous.DetachUi(oldProxy);
                        try { await page.PostAsync(new UiMessage(UiMessageTypes.SessionNewResult, new SessionNewResultPayload { Status = "completed" })).ConfigureAwait(false); }
                        catch (Exception ex) { _logger.Warn("Could not announce the workspace conversation: " + ex.Message); }
                        finally { newProxy.AllowReady(); }
                    }
                    foreach (var session in old) RemoveChild(session);
                    KnowledgeChanged?.Invoke(this, EventArgs.Empty);
                    PublishTabs();
                }).ConfigureAwait(false);
            }
            catch (OperationCanceledException) { }
            catch (Exception ex) { _logger.Warn("Changing the conversation workspace failed: " + ex.Message); }
        }
        private async Task HandleOpenHistoryTabAsync(JObject args)
        {
            string root;
            PompSession current;
            lock (_gate) { root = _root; current = _active; }
            var handle = (string?)args["id"];
            if (handle != null && args["root"]?.Type == JTokenType.String && SameRoot((string)args["root"]!, root))
            {
                var store = new RecentSessionStore(_environment.DataDirectory, root);
                try
                {
                    // Resolve validates the opaque handle and workspace before it is used to select a tab.
                    store.Resolve(root, handle, null);
                    var entry = (store.List(null, null, null)["entries"] as JArray)?.OfType<JObject>()
                        .FirstOrDefault(item => (string?)item["id"] == handle);
                    var id = (string?)entry?["sessionId"];
                    if (id != null)
                    {
                        var tabs = _tabs.Read(root);
                        var partner = _tabs.Get(root, id);
                        var ownerId = partner.Kind == "main" ? tabs.Main : _tabs.OpenSession(root, partner);
                        var requestId = (string?)args["requestId"] ?? "";
                        try
                        {
                            if (ownerId == id || ownerId == null)
                            {
                                if (id != current.ConversationId)
                                    await ActivateAsync(root, id, partner.Kind == "main" ? null : partner, requestId).ConfigureAwait(false);
                                else
                                    await current.RunHistoryCommandAsync(args).ConfigureAwait(false);
                                if (id != current.ConversationId)
                                    await ActiveSession.RunHistoryCommandAsync(new JObject { ["action"] = "refresh", ["requestId"] = requestId }).ConfigureAwait(false);
                            }
                            else
                            {
                                // Archived history replaces the conversation of its own tab, never the
                                // selected tab's engine. A busy owner defers this switch on its own queue.
                                if (ownerId != current.ConversationId)
                                    await ActivateAsync(root, ownerId, partner.Kind == "main" ? null : partner, null).ConfigureAwait(false);
                                await ActiveSession.RunHistoryCommandAsync(args).ConfigureAwait(false);
                            }
                        }
                        catch (Exception ex)
                        {
                            Reply(UiMessageTypes.SessionNewResult, requestId, ex.Message);
                            await ActiveSession.RunHistoryCommandAsync(new JObject { ["action"] = "refresh", ["requestId"] = requestId }).ConfigureAwait(false);
                        }
                        return;
                    }
                }
                catch (Exception ex) when (ex is ArgumentException || ex is InvalidOperationException || ex is IOException) { }
            }
            await current.RunHistoryCommandAsync(args).ConfigureAwait(false);
        }

        private async Task<string> WorkspaceRootAsync(CancellationToken token)
        {
            var root = await _environment.GetWorkspaceRootAsync(token).ConfigureAwait(false);
            return !string.IsNullOrWhiteSpace(root) && Directory.Exists(root) ? root! : Path.Combine(_environment.DataDirectory, "workspace");
        }
        private static bool SameRoot(string a, string b) =>
            string.Equals(a.TrimEnd('\\', '/'), b.TrimEnd('\\', '/'), StringComparison.OrdinalIgnoreCase);

        public async Task EnsureStartedAsync(CancellationToken token)
        {
            var root = await WorkspaceRootAsync(token).ConfigureAwait(false);
            lock (_gate) { if (_disposed) throw new ObjectDisposedException(nameof(PompConversationHost)); if (_root.Length == 0) _root = root; }
            await ActiveSession.EnsureStartedAsync(token).ConfigureAwait(false);
        }

        public async Task RestartAsync(CancellationToken token)
        {
            PompSession[] sessions;
            lock (_gate) { if (_disposed) throw new ObjectDisposedException(nameof(PompConversationHost)); sessions = _channels.Keys.ToArray(); }
            // Settings affect all engines, not just the selected conversation. Every restart restores its own session.
            foreach (var session in sessions) await session.RestartAsync(token).ConfigureAwait(false);
            PublishTabs();
        }

        private async Task HandleTabAsync(JObject args)
        {
            var requestId = (string?)args["requestId"] ?? "";
            var action = (string?)args["action"];
            var switching = action != "close";
            try
            {
                string root;
                lock (_gate) { if (_disposed) return; root = _root; }
                if (root.Length == 0) throw new InvalidOperationException("No workspace is connected.");
                var current = ActiveSession;
                var snapshot = _tabs.Read(root);
                if (action == "open")
                {
                    var partner = args["partner"]?.ToObject<ConversationPartnerPayload>() ?? throw new ArgumentException("The conversation partner is empty.");
                    if (partner.Kind != "agent" && partner.Kind != "team") throw new ArgumentException("The conversation partner is invalid.");
                    current.CheckConversationPartner(partner);
                    var id = _tabs.OpenSession(root, partner) ?? MostRecent(root, partner);
                    if (id == current.ConversationId) throw new InvalidOperationException("That tab is already open.");
                    await ActivateAsync(root, id, partner, requestId).ConfigureAwait(false);
                }
                else if (action == "activate")
                {
                    var id = (string?)args["sessionId"];
                    ConversationPartnerPayload? partner = null;
                    if (id != null)
                    {
                        var entry = snapshot.Conversations.FirstOrDefault(e => e.Open && e.SessionId == id)
                            ?? throw new InvalidOperationException("The tab was not found.");
                        partner = entry.Partner;
                    }
                    else id = snapshot.Main;
                    if (id == current.ConversationId || (id == null && _tabs.Get(root, current.ConversationId).Kind == "main"))
                        throw new InvalidOperationException("That tab is already open.");
                    await ActivateAsync(root, id, partner, requestId).ConfigureAwait(false);
                }
                else if (action == "close")
                {
                    var id = (string?)args["sessionId"];
                    if (id == null || !snapshot.Conversations.Any(e => e.Open && e.SessionId == id)) throw new InvalidOperationException("The tab was not found.");
                    var target = Find(root, id);
                    if (current.ConversationId == id)
                    {
                        switching = true;
                        await ActivateAsync(root, snapshot.Main, null, requestId).ConfigureAwait(false);
                    }
                    _tabs.Close(root, id);
                    if (target != null && !ReferenceEquals(target, ActiveSession)) RemoveChild(target);
                    PublishTabs();
                    if (!switching) Reply(UiMessageTypes.ConversationTabReply, requestId, null);
                }
                else throw new ArgumentException("Unknown tab command.");
            }
            catch (Exception ex)
            {
                if (switching) Reply(UiMessageTypes.SessionNewResult, requestId, ex.Message);
                else Reply(UiMessageTypes.ConversationTabReply, requestId, ex.Message);
            }
        }

        private string? MostRecent(string root, ConversationPartnerPayload partner)
        {
            var entries = new RecentSessionStore(_environment.DataDirectory, root).List(null, null, null)["entries"] as JArray;
            var partners = _tabs.All(root);
            return entries?.OfType<JObject>().Select(entry => (string?)entry["sessionId"])
                .FirstOrDefault(id => id != null && partners.TryGetValue(id, out var value) && value.Kind == partner.Kind && value.Id == partner.Id);
        }

        private string? SavedFile(string root, string id)
        {
            var store = new RecentSessionStore(_environment.DataDirectory, root);
            var entries = store.List(null, null, null)["entries"] as JArray;
            var handle = entries?.OfType<JObject>().FirstOrDefault(entry => (string?)entry["sessionId"] == id)?["id"]?.ToString();
            return handle == null ? null : store.Resolve(root, handle, null);
        }

        private PompSession? Find(string root, string id)
        {
            lock (_gate) return _channels.Keys.FirstOrDefault(session => SameRoot(session.ConversationRoot, root) && session.ConversationId == id);
        }

        private async Task ActivateAsync(string root, string? id, ConversationPartnerPayload? partner, string? requestId)
        {
            PompSession? destination = id == null ? null : Find(root, id);
            var created = false;
            CancellationToken epoch;
            lock (_gate) { if (_disposed) throw new ObjectDisposedException(nameof(PompConversationHost)); epoch = _workspaceEpoch.Token; }
            epoch.ThrowIfCancellationRequested();
            if (destination == null)
            {
                var file = id == null ? null : SavedFile(root, id);
                if (id != null && file == null) { _tabs.Remove(root, id); id = null; }
                lock (_gate) destination = CreateChild();
                created = true;
                destination.InitializeConversation(root, id, file, partner);
                try
                {
                    await destination.EnsureStartedAsync(epoch).ConfigureAwait(false);
                    await destination.WaitForConversationAsync(epoch).ConfigureAwait(false);
                    epoch.ThrowIfCancellationRequested();
                    if (id != null && destination.ConversationId != id) throw new InvalidOperationException("The requested conversation was not restored.");
                }
                catch { RemoveChild(destination); throw; }
            }
            if (epoch.IsCancellationRequested)
            {
                if (created) RemoveChild(destination);
                epoch.ThrowIfCancellationRequested();
            }
            PompSession previous; IUiChannel? page; Channel oldProxy, newProxy;
            try
            {
                if (id != null && partner != null) _tabs.Observe(root, id, null);
                lock (_gate)
                {
                    if (_disposed) throw new ObjectDisposedException(nameof(PompConversationHost));
                    epoch.ThrowIfCancellationRequested();
                    previous = _active; page = _page; oldProxy = _channels[previous]; newProxy = _channels[destination];
                    if (page != null)
                    {
                        newProxy.DeferReady();
                        destination.AttachUi(newProxy);
                    }
                    _active = destination;
                }
            }
            catch
            {
                if (created) RemoveChild(destination);
                throw;
            }
            if (page != null)
            {
                previous.DetachUi(oldProxy);
                try { await page.PostAsync(new UiMessage(UiMessageTypes.SessionNewResult, new SessionNewResultPayload { RequestId = requestId, Status = "completed" })).ConfigureAwait(false); }
                catch (Exception ex) { _logger.Warn("Could not announce the active conversation: " + ex.Message); }
                finally { newProxy.AllowReady(); }
            }
            KnowledgeChanged?.Invoke(this, EventArgs.Empty);
            PublishTabs();
        }

        private void RemoveChild(PompSession child)
        {
            Channel? proxy;
            lock (_gate) { if (!_channels.TryGetValue(child, out proxy)) return; _channels.Remove(child); }
            child.DetachUi(proxy);
            child.ConversationStateChanged -= OnConversationStateChanged;
            child.KnowledgeChanged -= OnKnowledgeChanged;
            child.Dispose();
        }

        private void Reply(string type, string id, string? error)
        {
            IUiChannel? page; lock (_gate) page = _page;
            if (page == null) return;
            var payload = type == UiMessageTypes.SessionNewResult
                ? new UiMessage(type, new SessionNewResultPayload { RequestId = id, Status = "failed", Message = error })
                : new UiMessage(type, new UiCommandReplyPayload { RequestId = id, Ok = error == null, Error = error });
            _ = page.PostAsync(payload);
        }

        private void PublishTabs()
        {
            IUiChannel? page; PompSession active; PompSession[] sessions; string root;
            lock (_gate) { page = _page; active = _active; sessions = _channels.Keys.ToArray(); root = _root; }
            if (page == null || root.Length == 0) return;
            var state = active.ConversationTabs;
            var running = new HashSet<string>(sessions.Where(session => SameRoot(session.ConversationRoot, root) && session.ConversationRunning)
                .Select(session => session.ConversationId).OfType<string>(), StringComparer.Ordinal);
            state.MainRunning = state.Main != null && running.Contains(state.Main);
            foreach (var tab in state.Tabs) tab.IsRunning = running.Contains(tab.SessionId);
            _ = page.PostAsync(new UiMessage(UiMessageTypes.ConversationTabsState, state));
        }

        private Task PostFromChildAsync(PompSession session, UiMessage message, CancellationToken token)
        {
            IUiChannel? page;
            lock (_gate) page = !_disposed && ReferenceEquals(session, _active) ? _page : null;
            if (message.Type == UiMessageTypes.ConversationTabsState) { PublishTabs(); return Task.CompletedTask; }
            return page == null ? Task.CompletedTask : page.PostAsync(message, token);
        }

        public void Dispose()
        {
            PompSession[] children;
            lock (_gate)
            {
                if (_disposed) return;
                _workspaceEpoch.Cancel();
                _workspaceDebounce?.Cancel();
                _disposed = true;
                if (_page != null) _page.MessageReceived -= Receive;
                _page = null;
                children = _channels.Keys.ToArray();
            }
            try { _adapters.Solution.SolutionChanged -= OnSolutionChanged; }
            catch (Exception ex) { _logger.Warn("Could not unsubscribe from workspace changes: " + ex.Message); }
            _workspaceDebounce?.Dispose();
            _workspaceEpoch.Dispose();
            foreach (var child in children) RemoveChild(child);
        }

        private sealed class Channel : IUiChannel
        {
            private readonly PompConversationHost _owner;
            private readonly PompSession _session;
            private bool _deferReady;
            public Channel(PompConversationHost owner, PompSession session) { _owner = owner; _session = session; }
            public bool IsReady { get { lock (_owner._gate) return !_deferReady && _owner._page?.IsReady == true; } }
            public event EventHandler<UiMessage>? MessageReceived;
            public void DeferReady() { lock (_owner._gate) _deferReady = true; }
            public void AllowReady()
            {
                bool ready;
                lock (_owner._gate)
                {
                    _deferReady = false;
                    ready = ReferenceEquals(_owner._active, _session) && _owner._page?.IsReady == true;
                }
                if (ready) Deliver(new UiMessage(UiMessageTypes.UiReady, new JObject()));
            }
            public void Deliver(UiMessage message)
            {
                lock (_owner._gate) if (_deferReady && message.Type == UiMessageTypes.UiReady) return;
                MessageReceived?.Invoke(this, message);
            }
            public Task PostAsync(UiMessage message, CancellationToken token = default) => _owner.PostFromChildAsync(_session, message, token);
        }
    }
}
