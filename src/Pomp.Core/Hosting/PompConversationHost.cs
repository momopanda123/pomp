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
using Pomp.Features.ModelShare;
using Pomp.Internal;
using Pomp.Tools;
using Pomp.Ui;

namespace Pomp.Hosting
{
    /// <summary>One UI with independent backend-owned conversations. Plans address their owner explicitly.</summary>
    public sealed partial class PompConversationHost : IPompSession, IEditorSessionContextSource
    {
        private readonly object _gate = new object();
        private readonly IPompSettings _settings;
        private readonly IPompHostEnvironment _environment;
        private readonly IVsAdapterFactory _adapters;
        private readonly IPompLogger _logger;
        private readonly Func<ICustomToolRegistry> _registry;
        private readonly IOmpProcessLauncher? _launcher;
        private readonly PompSessionOptions _options;
        private readonly ConversationTabStore _tabs;
        private readonly SerialQueue _tabCommands;
        private readonly Dictionary<IConversationSession, Channel> _channels = new Dictionary<IConversationSession, Channel>();
        private IUiChannel? _page;
        private IConversationSession _active;
        private string _root = "";
        private bool _disposed;
        private long _tabsPublication;
        // The backend last made active in a real workspace; a workspace that returns after "no solution" starts with it.
        private string _selectedBackend = "omp";
        // Editor features create the host before the chat is shown: solution events start nothing until a start was requested.
        private bool _startRequested;
        // What the host's own origin check found for _originRoot; the first native child of that root tells the user.
        private WorkspaceOrigin.Outcome? _origin;
        private string _originRoot = "";
        private CancellationTokenSource _workspaceEpoch = new CancellationTokenSource();
        private CancellationTokenSource? _workspaceDebounce;
        // Every tab's engine starts from the same saved accounts and model servers: one change makes all of them stale.
        private readonly CredentialEpoch _credentialEpoch = new CredentialEpoch();

        public PompConversationHost(IPompSettings settings, IPompHostEnvironment environment, IVsAdapterFactory adapters,
            IPompLogger logger, Func<ICustomToolRegistry> registry, IOmpProcessLauncher? launcher = null)
            : this(settings, environment, adapters, logger, registry, launcher, (PompSessionOptions?)null) { }

        /// <summary>Like the main constructor, with this PC's model sharing for the Accounts tab (owned and disposed by the caller).</summary>
        public PompConversationHost(IPompSettings settings, IPompHostEnvironment environment, IVsAdapterFactory adapters,
            IPompLogger logger, Func<ICustomToolRegistry> registry, IOmpProcessLauncher? launcher, ModelShareService? modelShare)
            : this(settings, environment, adapters, logger, registry, launcher, modelShare == null ? null : new PompSessionOptions { ModelShare = modelShare }) { }

        internal PompConversationHost(IPompSettings settings, IPompHostEnvironment environment, IVsAdapterFactory adapters,
            IPompLogger logger, Func<ICustomToolRegistry> registry, IOmpProcessLauncher? launcher, PompSessionOptions? options)
        {
            _settings = settings ?? throw new ArgumentNullException(nameof(settings));
            _environment = environment ?? throw new ArgumentNullException(nameof(environment));
            _adapters = adapters ?? throw new ArgumentNullException(nameof(adapters));
            _logger = logger ?? NullPompLogger.Instance;
            _registry = registry ?? throw new ArgumentNullException(nameof(registry));
            _launcher = launcher;
            _options = options ?? new PompSessionOptions();
            _tabs = new ConversationTabStore(environment.DataDirectory, _logger);
            _tabCommands = new SerialQueue(_logger);
            _credentialEpoch.Advanced += OnCredentialsAdvanced;
            _credentialEpoch.ModelListsChanged += OnModelListsChanged;
            _active = CreateChild();
            try { _adapters.Solution.SolutionChanged += OnSolutionChanged; }
            catch (Exception ex) { _logger.Warn("Could not subscribe to workspace changes: " + ex.Message); }
        }

        public IConversationSession ActiveSession { get { lock (_gate) return _active; } }
        public EditorSessionContext EditorSessionContext => ActiveSession.EditorSessionContext;
        /// <summary>Invalidates optional editor capabilities even when the previous conversation offered none.</summary>
        public event EventHandler? ActiveSessionChanged;

        private IConversationSession CreateChild(string backend = "omp")
        {
            IConversationSession session = backend == "omp"
                ? new PompSession(_settings, _environment, _adapters, _registry(), _logger, _launcher, _options, managedWorkspace: true, credentialEpoch: _credentialEpoch)
                : backend == "antigravity-acp"
                    ? new AcpConversationSession(_settings, _environment, _adapters, _logger, _options)
                    : throw new ArgumentException(PompLanguage.Pick("알 수 없는 대화 연결 방식이에요.", "Unknown conversation backend."));
            session.ConversationStateChanged += OnConversationStateChanged;
            if (session is PompSession native) { native.RelayHost = this; native.RelayChanged += OnRelayChanged; }
            _channels.Add(session, new Channel(this, session));
            return session;
        }

        private void OnConversationStateChanged(object? sender, EventArgs e)
        {
            if (sender is IConversationSession owner && owner.BackendId != "omp" && owner.ConversationId is string id
                && !string.IsNullOrEmpty(owner.ConversationRoot))
            {
                try
                {
                    lock (_gate)
                        if (!_disposed && _channels.ContainsKey(owner)) _tabs.ObserveBackend(owner.ConversationRoot, id, owner.BackendId);
                }
                catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException || ex is ArgumentException)
                { _logger.Warn("Backend conversation ownership could not be saved: " + ex.Message); }
            }
            PublishTabs();
            if (!(sender is PompSession session)) return;
            // A background tab whose run just ended may still have the old accounts and model servers.
            if (!ReferenceEquals(session, ActiveSession)) session.RestartIfCredentialsStale();
            // The outcome of a message this conversation sent to another tab may have arrived while it was not shown.
            if (HasRelayBacklog()) session.PullRelayBacklog();
        }

        /// <summary>
        /// Saved keys, model servers or sign-ins changed in the selected tab: every tab shows whether its own engine needs a restart, and
        /// the other tabs' idle engines restart right away (a busy one when its run ends), so each tab's model list has the change.
        /// </summary>
        private void OnCredentialsAdvanced(object? sender, EventArgs e)
        {
            PompSession[] sessions; IConversationSession active;
            lock (_gate) { if (_disposed) return; sessions = _channels.Keys.OfType<PompSession>().ToArray(); active = _active; }
            Task.Run(() =>
            {
                foreach (var session in sessions)
                {
                    session.PublishSetupState();
                    if (!ReferenceEquals(session, active)) session.RestartIfCredentialsStale();
                }
            }).Forget(_logger, "apply the changed accounts to the other tabs");
        }

        /// <summary>A sign-in in one tab is live for every engine (they read the same omp profile): the other tabs ask theirs for the list again.</summary>
        private void OnModelListsChanged(object? sender, EventArgs e)
        {
            PompSession[] sessions;
            lock (_gate) { if (_disposed) return; sessions = _channels.Keys.OfType<PompSession>().ToArray(); }
            Task.Run(() => { foreach (var session in sessions) session.ForgetModelList(sender); }).Forget(_logger, "refresh the other tabs' model lists");
        }

        public void AttachUi(IUiChannel channel)
        {
            if (channel == null) throw new ArgumentNullException(nameof(channel));
            IConversationSession active; Channel proxy;
            lock (_gate)
            {
                if (_disposed) throw new ObjectDisposedException(nameof(PompConversationHost));
                if (ReferenceEquals(_page, channel)) return;
                if (_page != null) _page.MessageReceived -= Receive;
                _page = channel;
                channel.MessageReceived += Receive;
                active = _active; proxy = _channels[active];
                proxy.DeferReady();
            }
            active.DetachUi(proxy);
            active.AttachUi(proxy);
            ReleaseReadyAsync(active, proxy, channel).Forget(_logger, "attached backend readiness");
        }

        public void DetachUi(IUiChannel channel)
        {
            IConversationSession active; Channel proxy;
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
            if (message.Type == UiMessageTypes.UiReady)
            {
                IConversationSession owner; Channel channel; IUiChannel page;
                lock (_gate)
                {
                    owner = _active; channel = _channels[owner]; page = _page!;
                    if (channel.ReadyDeferred) return;
                }
                ReleaseReadyAsync(owner, channel, page).Forget(_logger, "backend readiness");
                return;
            }
            if (message.Type == "conversation/backend" || message.Type == UiMessageTypes.SessionNew)
            {
                var owner = ActiveSession;
                _tabCommands.Enqueue(() => HandleBackendAsync(message, owner));
                return;
            }
            if (message.Type == "acp/command")
            {
                var id = (string?)message.Payload?["sessionId"];
                if (id == null && (string?)message.Payload?["action"] == "permission")
                {
                    Reply("acp/reply", (string?)message.Payload?["requestId"] ?? "", PompLanguage.Pick("승인 응답에 어느 ACP 대화인지가 빠졌어요.", "A permission reply must identify its ACP conversation."));
                    return;
                }
                IConversationSession? owner;
                Channel? channel;
                lock (_gate)
                {
                    owner = _active.BackendId == "antigravity-acp" && (id == null || _active.ConversationId == id) ? _active : null;
                    channel = owner == null ? null : _channels[owner];
                }
                if (channel == null) Reply("acp/reply", (string?)message.Payload?["requestId"] ?? "", PompLanguage.Pick("이 ACP 대화는 더 이상 선택되어 있지 않아요.", "The ACP conversation is no longer selected."));
                else channel.Deliver(message);
                return;
            }
            if (message.Type == UiMessageTypes.AgentPlan || message.Type == UiMessageTypes.AgentPlanPage ||
                message.Type == UiMessageTypes.AgentPlanEdit || message.Type == UiMessageTypes.AgentPlanAction)
            {
                RoutePlanCommand(message);
                return;
            }
            if (message.Type == UiMessageTypes.ConversationTab)
            {
                _tabCommands.Enqueue(() => HandleTabAsync(message.Payload ?? new JObject()));
                return;
            }
            if (message.Type == UiMessageTypes.SessionHistory && (string?)message.Payload?["action"] == "switch")
            {
                var owner = ActiveSession;
                _tabCommands.Enqueue(() => HandleOpenHistoryTabAsync(message.Payload ?? new JObject(), owner));
                return;
            }
            if (message.Type == UiMessageTypes.SessionHistory && (string?)message.Payload?["action"] == "delete")
            {
                var owner = ActiveSession;
                _tabCommands.Enqueue(async () =>
                {
                    // Keep tab open/close/history switches serialized until recycling has finished.
                    await owner.RunHistoryCommandAsync(message.Payload ?? new JObject(), (id, path) =>
                    {
                        IConversationSession[] sessions;
                        lock (_gate) sessions = _channels.Keys.Where(session => !ReferenceEquals(session, owner)).ToArray();
                        return sessions.Any(session => session.ConversationId == id ||
                            string.Equals(session.ConversationFile, path, StringComparison.OrdinalIgnoreCase));
                    }).ConfigureAwait(false);
                });
                return;
            }
            if (message.Type == UiMessageTypes.PermissionMode
                && Pomp.Protocol.OmpApprovalModeExtensions.TryParseUiValue((string?)message.Payload?["mode"], out var wantedMode)
                && wantedMode != _settings.ApprovalMode)
            {
                IConversationSession[] others; IUiChannel? target;
                lock (_gate) { if (_disposed) return; others = _channels.Keys.Where(session => !ReferenceEquals(session, _active)).ToArray(); target = _page; }
                if (others.Any(session => session.ConversationRunning))
                {
                    // The setting restarts every engine: a reply running in another tab must not be cut off by this one.
                    target?.PostAsync(new UiMessage(UiMessageTypes.PermissionModeResult, new PermissionModeResultPayload
                    {
                        RequestId = (string?)message.Payload?["requestId"] ?? "", Ok = false, Mode = _settings.ApprovalMode,
                        Error = PompLanguage.Pick("다른 대화가 응답 중이에요.", "Another conversation is replying."),
                    })).Forget(_logger, "permission mode refusal");
                    return;
                }
            }
            IConversationSession active; Channel proxy;
            lock (_gate) { if (_disposed) return; active = _active; proxy = _channels[active]; }
            proxy.Deliver(message);
        }
        private void OnSolutionChanged(object? sender, EventArgs e)
        {
            CancellationTokenSource old, next, debounce;
            CancellationTokenSource? pending;
            IConversationSession[] sessions;
            lock (_gate)
            {
                if (_disposed || !_startRequested) return;
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
                    var realRoot = await _environment.GetWorkspaceRootAsync(epoch).ConfigureAwait(false);
                    var hasWorkspace = IsRealWorkspace(realRoot);
                    IConversationSession[] old; IConversationSession previous, replacement; IConversationSession? created = null;
                    IUiChannel? page; Channel oldProxy, newProxy;
                    lock (_gate)
                    {
                        epoch.ThrowIfCancellationRequested();
                        if (SameRoot(root, _root) && SameRoot(_active.ConversationRoot, root))
                        {
                            foreach (var session in _channels.Keys) session.UnfreezeWorkspace();
                            PublishBackendStateAsync().Forget(_logger, "workspace backend readiness");
                            return;
                        }
                        old = _channels.Keys.ToArray();
                    }
                    // Before any store of this root is read, and never under _gate.
                    await ReconcileOriginAsync(root, epoch).ConfigureAwait(false);
                    try
                    {
                        lock (_gate)
                        {
                            epoch.ThrowIfCancellationRequested();
                            // The folder used when nothing is open is never a project for ACP; a real one returns to the user's backend.
                            replacement = created = CreateChild(hasWorkspace ? _selectedBackend : "omp");
                        }
                        var saved = _tabs.Read(root);
                        var savedId = replacement.BackendId == "omp" ? saved.Main
                            : saved.Conversations.LastOrDefault(entry => entry.Backend == replacement.BackendId && entry.Open)?.SessionId;
                        var savedFile = replacement.BackendId == "omp" && savedId != null ? SavedFileOrNull(root, savedId) : null;
                        if (replacement.BackendId == "omp" && savedFile == null) savedId = null;
                        replacement.InitializeConversation(root, savedId, savedFile, null);
                        HandOrigin(replacement, root);
                        // ACP setup/authentication failures must be visible on its selected page.
                        if (replacement.BackendId == "omp") await replacement.EnsureStartedAsync(epoch).ConfigureAwait(false);
                        lock (_gate)
                        {
                            epoch.ThrowIfCancellationRequested();
                            if (_disposed) throw new ObjectDisposedException(nameof(PompConversationHost));
                            // The page can attach, detach or reload while the new engine starts.
                            // Bind the replacement to the page present at commit, not the one startup saw.
                            previous = _active;
                            page = _page; oldProxy = _channels[previous]; newProxy = _channels[replacement];
                            if (page != null)
                            {
                                newProxy.DeferReady();
                                replacement.AttachUi(newProxy);
                            }
                            _root = root; _active = replacement;
                        }
                    }
                    catch { if (created != null) RemoveChild(created); throw; }
                    ActiveSessionChanged?.Invoke(this, EventArgs.Empty);
                    if (page != null)
                    {
                        previous.DetachUi(oldProxy);
                        try
                        {
                            await PublishBackendStateAsync().ConfigureAwait(false);
                            await page.PostAsync(new UiMessage(UiMessageTypes.SessionNewResult, new SessionNewResultPayload { Status = "completed" })).ConfigureAwait(false);
                        }
                        catch (Exception ex) { _logger.Warn("Could not announce the workspace conversation: " + ex.Message); }
                        finally { newProxy.AllowReady(); }
                    }
                    foreach (var session in old) RemoveChild(session);
                    PublishTabs();
                    if (replacement.BackendId != "omp")
                    {
                        try { await replacement.EnsureStartedAsync(epoch).ConfigureAwait(false); }
                        catch (Exception ex) when (!(ex is OperationCanceledException)) { _logger.Warn("The workspace backend could not start: " + ex.Message); }
                    }
                }).ConfigureAwait(false);
            }
            catch (OperationCanceledException) { }
            catch (Exception ex) { _logger.Warn("Changing the conversation workspace failed: " + ex.Message); }
        }
        private async Task HandleOpenHistoryTabAsync(JObject args, IConversationSession current)
        {
            string root;
            lock (_gate) { root = _root; }
            var handle = (string?)args["id"];
            if (handle?.StartsWith("antigravity-acp:", StringComparison.Ordinal) == true
                || (string?)args["backend"] == "antigravity-acp")
            {
                var requestId = (string?)args["requestId"] ?? "";
                try
                {
                    if (handle == null || !handle.StartsWith("antigravity-acp:", StringComparison.Ordinal)
                        || !SameRoot((string?)args["root"] ?? "", root))
                        throw new InvalidOperationException(PompLanguage.Pick("ACP 대화 기록의 작업 영역이나 대화가 바뀌었어요.", "The ACP history workspace or identity changed."));
                    if (current is not AcpConversationSession acp)
                        throw new InvalidOperationException(PompLanguage.Pick("기록을 연 ACP 대화가 더 이상 선택되어 있지 않아요.", "The ACP history owner is no longer selected."));
                    handle = acp.ResolveHistoryId(args);
                    await ActivateAsync(root, handle, null, requestId, "antigravity-acp").ConfigureAwait(false);
                    await ActiveSession.RunHistoryCommandAsync(new JObject { ["action"] = "refresh", ["requestId"] = requestId }).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    Reply(UiMessageTypes.SessionNewResult, requestId, ex.Message);
                    await current.RunHistoryCommandAsync(new JObject { ["action"] = "refresh", ["requestId"] = requestId }).ConfigureAwait(false);
                }
                return;
            }
            if (current.BackendId != "omp")
            {
                await current.RunHistoryCommandAsync(args).ConfigureAwait(false);
                return;
            }
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
        private static bool SameRoot(string a, string b)
        {
            if (!Path.IsPathRooted(a) || !Path.IsPathRooted(b)) return false;
            try { return string.Equals(Path.GetFullPath(a).TrimEnd('\\', '/'), Path.GetFullPath(b).TrimEnd('\\', '/'), StringComparison.OrdinalIgnoreCase); }
            catch (Exception ex) when (ex is ArgumentException || ex is NotSupportedException || ex is PathTooLongException) { return false; }
        }
        /// <summary>The folder the environment reports when nothing is open is POMP's own fallback, never a project for ACP.</summary>
        private bool IsRealWorkspace(string? path) =>
            !string.IsNullOrWhiteSpace(path) && Path.IsPathRooted(path) && Directory.Exists(path)
            && !SameRoot(path!, Path.Combine(_environment.DataDirectory, "workspace"));

        /// <summary>
        /// Data recorded for another folder is rebased before the host reads any store of <paramref name="root"/>
        /// (the engine start of a child would be too late for the saved-conversation lookup). A failure only costs the saved conversation.
        /// </summary>
        private async Task ReconcileOriginAsync(string root, CancellationToken token)
        {
            try
            {
                var origin = await WorkspaceOrigin.ReconcileAsync(root, _environment as IPompRecycleBin, _logger, token).ConfigureAwait(false);
                if (origin.Change != WorkspaceOrigin.Change.None) lock (_gate) { _origin = origin; _originRoot = root; }
            }
            catch (Exception ex) when (!(ex is OperationCanceledException))
            { _logger.Warn("Could not check where this workspace's POMP data came from: " + ex.Message); }
        }

        /// <summary>The native child that starts in a reconciled root reports the change to the user, as its own check would have.</summary>
        private void HandOrigin(IConversationSession child, string root)
        {
            if (!(child is PompSession native)) return;
            WorkspaceOrigin.Outcome origin;
            lock (_gate)
            {
                if (_origin == null || !SameRoot(_originRoot, root)) return;
                origin = _origin; _origin = null;
            }
            native.AdoptWorkspaceOrigin(origin);
        }

        /// <summary>
        /// Start, workspace switch and backend selection treat an unreadable session store as "nothing saved".
        /// A tab activation keeps the error, because a null there forgets the tab.
        /// </summary>
        private string? SavedFileOrNull(string root, string id)
        {
            try { return SavedFile(root, id); }
            catch (Exception ex) { _logger.Warn("The saved conversation could not be read: " + ex.Message); return null; }
        }

        public async Task EnsureStartedAsync(CancellationToken token)
        {
            bool first;
            lock (_gate)
            {
                if (_disposed) throw new ObjectDisposedException(nameof(PompConversationHost));
                _startRequested = true; first = _root.Length == 0;
            }
            var root = await WorkspaceRootAsync(token).ConfigureAwait(false);
            // Before the first store read of this root, and never under _gate.
            if (first) await ReconcileOriginAsync(root, token).ConfigureAwait(false);
            IConversationSession? initialized = null;
            lock (_gate)
            {
                if (_disposed) throw new ObjectDisposedException(nameof(PompConversationHost));
                if (_root.Length == 0)
                {
                    var id = _tabs.Read(root).Main;
                    var file = id == null ? null : SavedFileOrNull(root, id);
                    _active.InitializeConversation(root, file == null ? null : id, file, null);
                    _root = root; initialized = _active;
                }
            }
            if (initialized != null) HandOrigin(initialized, root);
            await ActiveSession.EnsureStartedAsync(token).ConfigureAwait(false);
            await PublishBackendStateAsync().ConfigureAwait(false);
        }

        public async Task RestartAsync(CancellationToken token)
        {
            PompSession[] sessions;
            lock (_gate) { if (_disposed) throw new ObjectDisposedException(nameof(PompConversationHost)); sessions = _channels.Keys.OfType<PompSession>().ToArray(); }
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
                if (root.Length == 0) throw new InvalidOperationException(PompLanguage.Pick("연결된 작업 영역이 없어요.", "No workspace is connected."));
                var current = ActiveSession;
                var snapshot = _tabs.Read(root);
                if (action == "open")
                {
                    var partner = args["partner"]?.ToObject<ConversationPartnerPayload>() ?? throw new ArgumentException(PompLanguage.Pick("대화 상대가 비어 있어요.", "The conversation partner is empty."));
                    if (partner.Kind != "agent" && partner.Kind != "team") throw new ArgumentException(PompLanguage.Pick("대화 상대가 올바르지 않아요.", "The conversation partner is invalid."));
                    if (current is not PompSession native) throw new InvalidOperationException(PompLanguage.Pick("에이전트·팀 대화는 OMP 연결에서만 열 수 있어요.", "Agent and team conversations require the OMP backend."));
                    native.CheckConversationPartner(partner);
                    var id = _tabs.OpenSession(root, partner) ?? MostRecent(root, partner);
                    if (id == current.ConversationId) throw new InvalidOperationException(PompLanguage.Pick("이미 열려 있는 탭이에요.", "That tab is already open."));
                    await ActivateAsync(root, id, partner, requestId).ConfigureAwait(false);
                }
                else if (action == "activate")
                {
                    var id = (string?)args["sessionId"];
                    ConversationPartnerPayload? partner = null;
                    if (id != null)
                    {
                        var entry = snapshot.Conversations.FirstOrDefault(e => e.Open && e.SessionId == id)
                            ?? throw new InvalidOperationException(PompLanguage.Pick("탭을 찾을 수 없어요.", "The tab was not found."));
                        partner = entry.Partner;
                    }
                    else id = snapshot.Main;
                    if (id == current.ConversationId || (id == null && current.BackendId == "omp" && _tabs.Get(root, current.ConversationId).Kind == "main"))
                        throw new InvalidOperationException(PompLanguage.Pick("이미 열려 있는 탭이에요.", "That tab is already open."));
                    await ActivateAsync(root, id, partner, requestId, id == null ? "omp" : _tabs.Backend(root, id),
                        existing: id == null ? UnstartedNative(root) : null, selectBeforeStart: id == null && partner == null).ConfigureAwait(false);
                }
                else if (action == "close")
                {
                    var id = (string?)args["sessionId"];
                    if (id == null || !snapshot.Conversations.Any(e => e.Open && e.SessionId == id)) throw new InvalidOperationException(PompLanguage.Pick("탭을 찾을 수 없어요.", "The tab was not found."));
                    var target = Find(root, id);
                    if (current.ConversationId == id)
                    {
                        switching = true;
                        await ActivateAsync(root, snapshot.Main, null, requestId, existing: snapshot.Main == null ? UnstartedNative(root) : null,
                            selectBeforeStart: snapshot.Main == null).ConfigureAwait(false);
                    }
                    if (target != null && !ReferenceEquals(target, ActiveSession)) RemoveChild(target);
                    _tabs.Close(root, id);
                    PublishTabs();
                    if (!switching) Reply(UiMessageTypes.ConversationTabReply, requestId, null);
                }
                else throw new ArgumentException(PompLanguage.Pick("알 수 없는 탭 명령이에요.", "Unknown tab command."));
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

        private IConversationSession? Find(string root, string id)
        {
            lock (_gate) return _channels.Keys.FirstOrDefault(session => SameRoot(session.ConversationRoot, root) && session.ConversationId == id);
        }

        private async Task ActivateAsync(string root, string? id, ConversationPartnerPayload? partner, string? requestId,
            string backend = "omp", IConversationSession? existing = null, bool selectBeforeStart = false)
        {
            CancellationToken epoch;
            lock (_gate) { if (_disposed) throw new ObjectDisposedException(nameof(PompConversationHost)); epoch = _workspaceEpoch.Token; }
            var (destination, created) = await OpenChildAsync(root, id, partner, backend, existing, selectBeforeStart, epoch).ConfigureAwait(false);
            if (epoch.IsCancellationRequested)
            {
                if (created) RemoveChild(destination);
                epoch.ThrowIfCancellationRequested();
            }
            IConversationSession previous; IUiChannel? page; Channel oldProxy, newProxy;
            var realWorkspace = IsRealWorkspace(root);
            try
            {
                if (destination.ConversationId is string destinationId)
                {
                    if (backend == "omp" && partner != null) _tabs.Observe(root, destinationId, created ? partner : null);
                    else if (backend != "omp") _tabs.ObserveBackend(root, destinationId, backend);
                }
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
                    if (realWorkspace) _selectedBackend = backend;
                }
            }
            catch
            {
                if (created) RemoveChild(destination);
                throw;
            }
            if (!ReferenceEquals(previous, destination)) ActiveSessionChanged?.Invoke(this, EventArgs.Empty);
            if (page != null)
            {
                if (!ReferenceEquals(previous, destination)) previous.DetachUi(oldProxy);
                try
                {
                    await PublishBackendStateAsync().ConfigureAwait(false);
                    await page.PostAsync(new UiMessage(UiMessageTypes.SessionNewResult, new SessionNewResultPayload { RequestId = requestId, Status = "completed" })).ConfigureAwait(false);
                }
                catch (Exception ex) { _logger.Warn("Could not announce the active conversation: " + ex.Message); }
                finally { newProxy.AllowReady(); }
            }
            PublishTabs();
            if (selectBeforeStart)
            {
                // Keep setup/error UI reachable when authentication or startup fails after selection.
                try { await destination.EnsureStartedAsync(epoch).ConfigureAwait(false); }
                catch (Exception ex) when (!(ex is OperationCanceledException)) { _logger.Warn("The selected backend could not start: " + ex.Message); }
            }
            // The tab left behind may still run with accounts or model servers from before a change.
            if (previous is PompSession native) native.RestartIfCredentialsStale();
        }

        /// <summary>
        /// The live conversation <paramref name="id"/> (or <paramref name="existing"/>), else a new child started for it and, with
        /// <paramref name="partner"/>, for that agent or team. The child is not selected and gets no page: a conversation can work in the
        /// background. With <paramref name="startLater"/> the caller starts the engine after selecting it.
        /// </summary>
        private async Task<(IConversationSession Session, bool Created)> OpenChildAsync(string root, string? id, ConversationPartnerPayload? partner,
            string backend, IConversationSession? existing, bool startLater, CancellationToken epoch)
        {
            IConversationSession? destination = existing ?? (id == null ? null : Find(root, id));
            epoch.ThrowIfCancellationRequested();
            if (backend == "antigravity-acp")
            {
                var realRoot = await _environment.GetWorkspaceRootAsync(epoch).ConfigureAwait(false);
                if (!IsRealWorkspace(realRoot) || !SameRoot(realRoot!, root))
                    throw new InvalidOperationException(PompLanguage.Pick("Antigravity ACP를 시작하려면 먼저 솔루션이나 폴더를 열어 주세요.", "Open a real workspace before starting Antigravity ACP."));
                if (id != null && !id.StartsWith("antigravity-acp:", StringComparison.Ordinal))
                    throw new InvalidOperationException(PompLanguage.Pick("ACP 대화 식별자가 올바르지 않아요.", "Invalid ACP conversation identity."));
            }
            if (destination != null && destination.BackendId != backend)
                throw new InvalidOperationException(PompLanguage.Pick("대화의 연결 방식은 바꿀 수 없어요.", "A conversation cannot change backend."));
            if (destination != null) return (destination, false);
            var file = backend == "omp" && id != null ? SavedFile(root, id) : null;
            if (backend == "omp" && id != null && file == null) { _tabs.Remove(root, id); id = null; }
            lock (_gate) destination = CreateChild(backend);
            try
            {
                destination.InitializeConversation(root, id, file, partner);
                HandOrigin(destination, root);
                if (!startLater)
                {
                    await destination.EnsureStartedAsync(epoch).ConfigureAwait(false);
                    await destination.WaitForConversationAsync(epoch).ConfigureAwait(false);
                    epoch.ThrowIfCancellationRequested();
                    if (id != null && destination.ConversationId != id) throw new InvalidOperationException(PompLanguage.Pick("요청한 대화를 복원하지 못했어요.", "The requested conversation was not restored."));
                }
            }
            catch { RemoveChild(destination); throw; }
            return (destination, true);
        }

        private void RemoveChild(IConversationSession child)
        {
            Channel? proxy;
            lock (_gate) { if (!_channels.TryGetValue(child, out proxy)) return; _channels.Remove(child); }
            child.DetachUi(proxy);
            child.ConversationStateChanged -= OnConversationStateChanged;
            if (child is PompSession native) { native.RelayChanged -= OnRelayChanged; StopRelaysTo(native); }
            child.Dispose();
        }

        private void Reply(string type, string? id, string? error)
        {
            IUiChannel? page; lock (_gate) page = _page;
            if (page == null) return;
            var payload = type == UiMessageTypes.SessionNewResult
                ? new UiMessage(type, new SessionNewResultPayload { RequestId = id, Status = error == null ? "completed" : "failed", Message = error })
                : new UiMessage(type, new UiCommandReplyPayload { RequestId = id ?? "", Ok = error == null, Error = error });
            _ = page.PostAsync(payload);
        }

        private void RoutePlanCommand(UiMessage message)
        {
            IUiChannel? page; string root; CancellationToken epoch;
            lock (_gate) { page = _page; root = _root; epoch = _workspaceEpoch.Token; }
            if (page == null) return;
            void Respond(UiMessage result)
            {
                lock (_gate)
                    if (_disposed || epoch.IsCancellationRequested || !ReferenceEquals(page, _page)) return;
                page.PostAsync(result).Forget(_logger, "plan reply");
            }
            void Refuse(string error) => Respond(new UiMessage(
                message.Type == UiMessageTypes.AgentPlanPage ? UiMessageTypes.AgentPlanPageResult : UiMessageTypes.AgentPlanResult,
                new UiCommandReplyPayload { RequestId = (string?)message.Payload?["requestId"] ?? "", Ok = false, Error = error }));
            var requestedRoot = (string?)message.Payload?["root"];
            var id = (string?)message.Payload?["sessionId"];
            if (epoch.IsCancellationRequested || string.IsNullOrEmpty(requestedRoot) || !SameRoot(requestedRoot!, root) || string.IsNullOrEmpty(id))
            {
                Refuse(PompLanguage.Pick("작업 계획의 대화나 작업 영역이 바뀌었어요.", "The work plan's conversation or workspace changed."));
                return;
            }
            var owner = Find(root, id!);
            if (owner == null)
            {
                Refuse(PompLanguage.Pick("작업 계획의 대화가 닫혔어요.", "The work plan's conversation is closed."));
                return;
            }
            async Task Dispatch()
            {
                try { await owner.RunPlanCommandAsync(message, Respond).ConfigureAwait(false); }
                catch (Exception ex) { Refuse(ex.Message); }
            }
            Dispatch().Forget(_logger, "conversation plan command");
        }

        private void PublishTabs()
        {
            IUiChannel? page; IConversationSession active; IConversationSession[] sessions; string root; long publication;
            lock (_gate) { page = _page; active = _active; sessions = _channels.Keys.ToArray(); root = _root; publication = ++_tabsPublication; }
            if (page == null || root.Length == 0) return;
            var metadata = _tabs.Read(root);
            var state = active.ConversationTabs;
            state.Backend = active.BackendId;
            state.Main = metadata.Main;
            state.SessionId = active.ConversationId;
            state.Tabs = metadata.Conversations.Where(entry => entry.Open).Select(entry =>
            {
                var nativeTab = state.Tabs.FirstOrDefault(tab => tab.SessionId == entry.SessionId);
                return new ConversationTabPayload { SessionId = entry.SessionId, Backend = entry.Backend, Partner = entry.Partner,
                    Title = entry.Backend == "antigravity-acp" ? "Antigravity" : nativeTab?.Title };
            }).ToList();
            var live = sessions.Where(session => SameRoot(session.ConversationRoot, root)).ToArray();
            var running = new HashSet<string>(live.Where(session => session.ConversationRunning).Select(session => session.ConversationId).OfType<string>(), StringComparer.Ordinal);
            // An approval or question of a tab that is not on screen: the tab strip marks it.
            var waiting = new HashSet<string>(live.Where(session => session.ConversationWaiting).Select(session => session.ConversationId).OfType<string>(), StringComparer.Ordinal);
            state.MainRunning = state.Main != null && running.Contains(state.Main);
            state.MainWaiting = state.Main != null && waiting.Contains(state.Main);
            foreach (var tab in state.Tabs) { tab.IsRunning = running.Contains(tab.SessionId); tab.Waiting = waiting.Contains(tab.SessionId); }
            state.Plans = sessions.Where(session => SameRoot(session.ConversationRoot, root))
                .Select(session => session.ConversationPlan).OfType<ConversationPlanPayload>().ToList();
            lock (_gate)
            {
                // Concurrent engine updates must not publish an older projection after a newer one.
                if (_disposed || publication != _tabsPublication || !ReferenceEquals(_page, page)
                    || !ReferenceEquals(_active, active) || !SameRoot(root, _root)) return;
                _ = page.PostAsync(new UiMessage(UiMessageTypes.ConversationTabsState, state));
            }
        }

        private Task PostFromChildAsync(IConversationSession session, UiMessage message, CancellationToken token)
        {
            IUiChannel? page;
            lock (_gate) page = !_disposed && ReferenceEquals(session, _active) ? _page : null;
            if (message.Type == UiMessageTypes.ConversationTabsState) { PublishTabs(); return Task.CompletedTask; }
            return page == null ? Task.CompletedTask : page.PostAsync(message, token);
        }

        public void Dispose()
        {
            IConversationSession[] children;
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
            _credentialEpoch.Advanced -= OnCredentialsAdvanced;
            _credentialEpoch.ModelListsChanged -= OnModelListsChanged;
            try { _adapters.Solution.SolutionChanged -= OnSolutionChanged; }
            catch (Exception ex) { _logger.Warn("Could not unsubscribe from workspace changes: " + ex.Message); }
            _workspaceDebounce?.Dispose();
            _workspaceEpoch.Dispose();
            foreach (var child in children) RemoveChild(child);
        }

        private sealed class Channel : IUiChannel
        {
            private readonly PompConversationHost _owner;
            private readonly IConversationSession _session;
            private bool _deferReady;
            public Channel(PompConversationHost owner, IConversationSession session) { _owner = owner; _session = session; }
            public bool IsReady { get { lock (_owner._gate) return !_deferReady && _owner._page?.IsReady == true; } }
            public bool ReadyDeferred { get { lock (_owner._gate) return _deferReady; } }
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
