using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;
using Pomp.Adapters;
using Pomp.Bridge;
using Pomp.Diagnostics;
using Pomp.Internal;
using Pomp.Json;
using Pomp.Permissions;
using Pomp.Protocol;
using Pomp.Tools;
using Pomp.Ui;
using Pomp.Features;

namespace Pomp.Hosting
{
    /// <summary>Tunables of <see cref="PompSession"/> that tests shrink; the defaults are the production values.</summary>
    internal sealed class PompSessionOptions
    {
        public OmpBridgeOptions Bridge { get; set; } = new OmpBridgeOptions();

        /// <summary>Id of the host process used for <c>POMP_STATE_DIR</c>; defaults to the current process.</summary>
        public int? HostProcessId { get; set; }

        public TimeSpan StateRefreshInterval { get; set; } = TimeSpan.FromSeconds(2);

        public TimeSpan SolutionChangeDebounce { get; set; } = TimeSpan.FromMilliseconds(750);

        public TimeSpan AdapterTimeout { get; set; } = TimeSpan.FromSeconds(2);

        public TimeSpan PromptTimeout { get; set; } = TimeSpan.FromSeconds(30);

        /// <summary>After a successful abort, how long an idle omp may take to report the stopped turns before the host ends them.</summary>
        public TimeSpan StopSettleDelay { get; set; } = TimeSpan.FromSeconds(5);

        /// <summary>Executable lookup; tests inject one that does not depend on the machine.</summary>
        public OmpExecutableResolver? Resolver { get; set; }

        /// <summary>Usage metrics sink (tests); by default the session keeps <see cref="UsageMetrics.FileName"/> in the data folder.</summary>
        public UsageMetrics? Metrics { get; set; }

        /// <summary>Interactive (console) login process factory; tests never open a real console.</summary>
        public Func<ProcessStartInfo, IProviderLoginProcess>? StartProviderLogin { get; set; }

        /// <summary>Opens a sign-in page in the default browser (only ever called with an absolute http(s) address); tests record instead.</summary>
        public Action<string>? OpenUrl { get; set; }

        /// <summary>How long the auth helper omp (RPC sign-in) lives without a command in flight; a running sign-in is never idle.</summary>
        public TimeSpan AuthHelperIdleTimeout { get; set; } = TimeSpan.FromMinutes(2);

        /// <summary>How long the auth helper may take to start (until omp's ready frame).</summary>
        public TimeSpan AuthHelperStartTimeout { get; set; } = TimeSpan.FromSeconds(30);

        /// <summary>Time limit of <c>get_login_providers</c> on the auth helper.</summary>
        public TimeSpan AuthHelperProvidersTimeout { get; set; } = TimeSpan.FromSeconds(30);

        /// <summary>Time limit of one RPC sign-in; the helper is ended when it expires.</summary>
        public TimeSpan AuthHelperLoginTimeout { get; set; } = TimeSpan.FromMinutes(15);

        /// <summary>Time limit of one <c>omp usage --json</c> run.</summary>
        public TimeSpan UsageTimeout { get; set; } = TimeSpan.FromSeconds(45);

        /// <summary>
        /// Runs <c>omp usage --json</c> and returns its output (tests); by default it is started through the session's process launcher with
        /// <see cref="UsageTimeout"/> and a 2 MiB output limit.
        /// </summary>
        public Func<OmpLaunchOptions, CancellationToken, Task<string>>? RunUsage { get; set; }

        /// <summary>User profile home directory for agent roles (tests); defaults to user profile.</summary>
        public string? UserHome { get; set; }

        /// <summary>User teams directory (tests); defaults to %APPDATA%\Pomp\teams.</summary>
        public string? UserTeamsDirectory { get; set; }
    }

    /// <summary>
    /// The chat session orchestrator (design Layer 2): owns one omp process bridge and connects it with the chat UI, the host tool
    /// dispatcher, the permission gate and the Visual Studio adapters. It launches omp with the arguments of architecture.md §4, keeps
    /// <c>vs-state.json</c> current, translates omp frames into the UI protocol (docs/ui-protocol.md) and UI messages into omp
    /// commands, and answers omp's approval prompts and extension dialogs through the UI.
    /// Construct it once per Visual Studio instance, after the host tools were registered in <paramref name="registry"/>.
    /// </summary>
    public sealed partial class PompSession : IPompSession
    {
        private readonly IPompSettings _settings;
        private readonly IOmpProcessLauncher _processLauncher;
        private readonly IPompHostEnvironment _environment;
        private readonly IVsAdapterFactory _adapters;
        private readonly ICustomToolRegistry _registry;
        private readonly IPompLogger _logger;
        private readonly PompSessionOptions _options;
        private readonly OmpProcessBridge _bridge;
        private readonly UiHub _ui;
        private readonly ProviderCredentialStore _providerCredentials;
        private readonly ProviderSetup _providerSetup;
        private readonly WorkspaceReviewController? _workspaceReview;
        private readonly ComposerWorkflow? _composerWorkflow;
        private readonly ComposerAutoContinue? _composerAuto;
        private readonly TerminalJobs _terminalJobs;
        private readonly InteractiveTerminal _interactiveTerminal;
        private readonly BackgroundAgents _backgroundAgents;
        private readonly ProjectHookController? _projectHooks;
        /// <summary>IDE save/build events for routine event rules (docs/architecture.md §8.10); separate from the .pomp/hooks scripts.</summary>
        private readonly IProjectEventSource? _projectEvents;
        private readonly CodeReviewController? _codeReview;
        private readonly ProblemsController _problems;
        private readonly AdvisorController _advisors;
        private readonly CodeSearchTool? _codeSearch;
        private readonly PermissionGate _gate;
        private readonly HostToolDispatcher _dispatcher;
        private readonly EditorContextBuilder _contextBuilder;
        private readonly VsStateWriter _vsState;
        private readonly OmpExecutableResolver _resolver;
        private readonly SerialQueue _commands;
        private readonly SerialQueue _sessionReferences;
        private readonly CancellationTokenSource _disposeCts = new CancellationTokenSource();
        private readonly int _hostProcessId;
        private readonly UsageMetrics _metrics;
        private readonly bool _ownsMetrics;

        private readonly object _stateLock = new object();
        // Guards _lastHelloJson only (the JSON of the host/hello last built for the page); never held while posting.
        private readonly object _helloLock = new object();
        private string? _lastHelloJson;
        private BridgeStatePayload _bridgeState = new BridgeStatePayload { State = BridgeState.Stopped };
        private SessionInfoPayload _info = new SessionInfoPayload();
        private string _infoJson = string.Empty;
        private CancellationTokenSource _epoch = new CancellationTokenSource();
        private OmpConnection? _epochConnection;
        private readonly WorkspaceTrust _workspaceTrust;
        private string _workingDirectory = string.Empty;
        private bool _staleStateCleaned;
        private bool _refreshRunning;
        private bool _refreshQueued;

        // Bumped by every streaming lifecycle event (_stateLock): a get_state reply older than one of them does not decide IsStreaming.
        private long _streamingRevision;
        private Timer? _solutionTimer;
        private int _disposed;

        // Set when a solution/folder opens or closes, cleared when omp (re)starts or the root turned out unchanged: in between, the
        // running omp still belongs to the previous workspace while host tools would act on the newly opened one.
        private int _workspaceSwitching;

        /// <param name="settings">User settings (options page).</param>
        /// <param name="environment">Facts about the IDE/extension; provides the workspace root.</param>
        /// <param name="adapters">Visual Studio adapters (editor context, <c>vs-state.json</c>, solution change notifications).</param>
        /// <param name="registry">Host tools, already filled by the composition root; they are offered to omp during every handshake.</param>
        /// <param name="logger">Log sink.</param>
        /// <param name="launcher">Process launcher; defaults to <see cref="SystemOmpProcessLauncher"/>.</param>
        public PompSession(
            IPompSettings settings,
            IPompHostEnvironment environment,
            IVsAdapterFactory adapters,
            ICustomToolRegistry registry,
            IPompLogger logger,
            IOmpProcessLauncher? launcher = null)
            : this(settings, environment, adapters, registry, logger, launcher, null)
        {
        }

        internal PompSession(
            IPompSettings settings,
            IPompHostEnvironment environment,
            IVsAdapterFactory adapters,
            ICustomToolRegistry registry,
            IPompLogger logger,
            IOmpProcessLauncher? launcher,
            PompSessionOptions? options)
        {
            _settings = settings ?? throw new ArgumentNullException(nameof(settings));
            _environment = environment ?? throw new ArgumentNullException(nameof(environment));
            _adapters = adapters ?? throw new ArgumentNullException(nameof(adapters));
            _registry = registry ?? throw new ArgumentNullException(nameof(registry));
            _logger = logger ?? NullPompLogger.Instance;
            _options = options ?? new PompSessionOptions();
            _resolver = _options.Resolver ?? new OmpExecutableResolver();
            _hostProcessId = _options.HostProcessId ?? CurrentProcessId();

            _ownsMetrics = _options.Metrics == null;
            _metrics = _options.Metrics ?? new UsageMetrics(MetricsPath(environment), _logger);
            _commands = new SerialQueue(_logger);
            _sessionReferences = new SerialQueue(_logger);
            _ui = new UiHub(_logger);
            // "Always allow" is remembered per workspace: the root the running omp was started in.
            _gate = new PermissionGate(settings, _ui, _logger, _metrics, () => Volatile.Read(ref _workingDirectory));
            _workspaceTrust = new WorkspaceTrust(environment.DataDirectory, ToolResourceFiles.Home(_options.UserHome), _logger);
            if (adapters is IInstalledSkillAdapters skillAdapters) { _installedSkills = new InstalledSkills(skillAdapters.InstalledSkills); registry.Register(_installedSkills); }
            _terminalJobs = new TerminalJobs(environment, _ui);
            _terminalJobs.ResolveSessionId = invocation => { lock (_stateLock) return !_sessionSwitchUncertain && (invocation.Origin == null || ReferenceEquals(invocation.Origin, _epochConnection)) ? _info.SessionId ?? "" : ""; };
            registry.Register(_terminalJobs.RunTool()); registry.Register(_terminalJobs.StatusTool());
            // POMP's own shortcuts and settings for the model (read-only), so "which key / where is the setting" has an answer.
            registry.Register(new PompSettingsTool(settings, environment));
            if (adapters is IWorkspaceFeatureAdapters featureAdapters)
            {
                _workspaceReview = new WorkspaceReviewController(featureAdapters, environment, _gate, _ui);
                registry.Register(_workspaceReview.CreateTool());
                _isolationResults = CreateIsolationResults(adapters, environment);
                if (adapters is IIsolatedExecutionAdapters isolated) registry.Register(new IsolatedExecutionTool(featureAdapters, isolated.IsolatedRunner, environment, () => (settings as IIsolatedExecutionSettings)?.EnableExperimentalIsolation == true));
                _composerWorkflow = new ComposerWorkflow(environment, _ui, () => { lock (_stateLock) return _sessionSwitchUncertain ? "" : _info.SessionId ?? ""; }, () => { lock (_stateLock) return _terminalJobs.HasUnfinishedWork || (_interactiveTerminal?.HasUnfinishedWork ?? false) || _info.HasPendingAsyncWork; }, () => _dispatcher?.ActiveCount ?? 0, () => { lock (_stateLock) return _turns.Count > 0 || _info.IsStreaming || _sessionSwitchUncertain; });
                registry.Register(_composerWorkflow);
                _workspaceReview.StateObserved = _composerWorkflow.ObserveReview;
                _workspaceReview.AdmissionCheck = _composerWorkflow.CheckReview;
            }
            if (adapters is ICodeIndexAdapters codeAdapters)
            {
                _codeSearch = new CodeSearchTool(codeAdapters.CodeSource, environment, settings, _ui);
                registry.Register(_codeSearch);
                registry.Register(new ContextOutlineTool(codeAdapters.CodeSource, environment, ReadContextBudgetAsync));
                registry.Register(new CodeSummaryTool(codeAdapters.CodeSource, environment));
            }
            if (adapters is ICodeReviewAdapters reviewAdapters)
            {
                _codeReview = new CodeReviewController(reviewAdapters.CodeReview, environment, _ui);
                registry.Register(_codeReview.ReviewTool()); registry.Register(_codeReview.ReportTool());
            }
            _problems = new ProblemsController(adapters, environment, _codeReview, _ui);
            _advisors = new AdvisorController(_ui, adapters.Editor, _workspaceTrust, () => _settings.LoadDiscoveredOmpExtensions, CaptureAdvisorContext, SendAdvisorAsync,
                Path.Combine(Path.GetFullPath(_environment.DataDirectory), "exports"));
            // The sign-in helper and `omp usage` start through the same launcher as the engine (tests substitute it for all three).
            var processLauncher = launcher ?? new SystemOmpProcessLauncher(_logger);
            _processLauncher = processLauncher;
            _bridge = new OmpProcessBridge(
                BuildLaunchOptionsAsync,
                processLauncher,
                registry,
                _logger,
                _options.Bridge);
            _providerCredentials = new ProviderCredentialStore(environment.DataDirectory, _logger);
            _providerSetup = new ProviderSetup(_providerCredentials, settings, environment, _resolver, processLauncher, _options, _ui,
                () => _bridge.State, (command, token) => _bridge.SendCommandAsync(command, TimeSpan.FromSeconds(15), token),
                CurrentSetupModel, CurrentSetupThinking, RequestInfoRefresh, () => RestartAsync(_disposeCts.Token), _logger, _disposeCts.Token)
            {
                ResolveEditorModel = ResolveEditorModelAsync,
            };
            _interactiveTerminal = new InteractiveTerminal((command, token) => _bridge.SendCommandAsync(command, TimeSpan.FromSeconds(10), token), environment, _ui, _gate,
                () => (settings as IInteractiveTerminalSettings)?.EnableInteractiveTerminal == true,
                () => { lock (_stateLock) { if (_sessionSwitchUncertain) return "Conversation switch is unconfirmed."; } return _composerWorkflow?.CheckApproval(Pomp.Permissions.PermissionKind.ProcessExec); });
            registry.Register(_interactiveTerminal);
            if (_composerWorkflow != null) _interactiveTerminal.StateObserved = _composerWorkflow.ObserveInteractive;
            _backgroundAgents = new BackgroundAgents((command, token) => _bridge.SendCommandAsync(command, TimeSpan.FromSeconds(10), token), environment, _ui);
            _backgroundAgents.JobObserved = ObserveRunJob;
            _backgroundAgents.StopMember = StopMemberAsync;
            _partners = new ConversationPartnerStore(environment.DataDirectory, _logger);
            registry.Register(new CaseFeedbackTool(this));
            registry.Register(new KnowledgeProposalTool(this));
            _dispatcher = new HostToolDispatcher(registry, _gate, (frame, token) => _bridge.SendFrameAsync(frame, token), _logger);
            _dispatcher.AdmissionCheck = (invocation, kind) => WorkspaceSwitchBlock() ?? ProjectExecutionPolicy.CheckTool(_workingDirectory, invocation.ToolName, invocation.Arguments) ?? ProjectExecutionPolicy.CheckHostPermission(_workingDirectory, kind) ?? _composerWorkflow?.CheckTool(invocation, kind);
            _contextBuilder = new EditorContextBuilder(adapters, _logger, CurrentEditorContextLimits);
            _vsState = new VsStateWriter(adapters, _logger, _options.AdapterTimeout);
            _vsState.StateObserved = RefreshActiveRules;

            if (adapters is IProjectEventAdapters events)
            {
                _projectHooks = new ProjectHookController(events.ProjectEvents, environment, _gate, _terminalJobs, _ui, () => { lock (_stateLock) return _info.IsStreaming || _info.HasPendingAsyncWork || _sessionSwitchUncertain; });
                _projectEvents = events.ProjectEvents;
                _projectEvents.Occurred += OnProjectEventForRules;
            }
            if (_composerWorkflow != null)
            {
                _composerAuto = new ComposerAutoContinue(_composerWorkflow.View, ComposerContinuationReady, QueueComposerContinuationAsync, _ui, usage: ConversationTokens, notice: (level, text) => PostNotice(level, text));
                // The model can ask for a goal run; the page's confirmation card decides (docs/ui-protocol.md "Goal runs").
                registry.Register(new GoalRequestTool(this));
            }
            _ui.SnapshotProvider = BuildSnapshot;
            _ui.CommandReceived += OnUiCommand;
            _ui.PageReady += OnPageReady;
            _dispatcher.Completed += OnHostToolCompleted;
            _bridge.StateChanged += OnBridgeStateChanged;
            _bridge.FrameReceived += OnFrameReceived;
            _bridge.DiagnosticReceived += OnDiagnostic;
            _bridge.ConnectionStarted += OnConnectionStarted;
            _bridge.ConnectionEnded += OnConnectionEnded;
            PompLanguage.Changed += OnHelloSettingsChanged;
            _settings.Changed += OnHelloSettingsChanged;
            try
            {
                _adapters.Solution.SolutionChanged += OnSolutionChanged;
            }
            catch (Exception ex)
            {
                _logger.Warn("Could not subscribe to solution changes: " + ex.Message);
            }
        }

        /// <inheritdoc />
        public IOmpProcessBridge Bridge => _bridge;

        /// <inheritdoc />
        public void AttachUi(IUiChannel channel)
        {
            ThrowIfDisposed();
            _ui.Attach(channel);
        }

        /// <inheritdoc />
        public void DetachUi(IUiChannel channel) => _ui.Detach(channel);

        /// <inheritdoc />
        public async Task EnsureStartedAsync(CancellationToken cancellationToken)
        {
            ThrowIfDisposed();
            switch (_bridge.State)
            {
                case BridgeState.Running:
                case BridgeState.Failed:
                case BridgeState.Stopping:
                    // Failed needs an explicit restart (the UI offers one); nothing to wait for otherwise.
                    return;
            }

            try
            {
                await _bridge.StartAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (OmpBridgeException ex)
            {
                // Already reported through bridge/state and the log (a refusal such as an untrusted workspace, once as information).
                if (ex.Reason == null) _logger.Warn("omp did not start: " + ex.Message);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                // The bridge was stopped while we waited.
            }
        }

        /// <inheritdoc />
        public async Task RestartAsync(CancellationToken cancellationToken)
        {
            ThrowIfDisposed();
            PauseChatQueue(PompLanguage.Pick("다시 시작했어요.", "Restarted."));
            try
            {
                await _bridge.RestartAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (OmpBridgeException ex)
            {
                if (ex.Reason == null) _logger.Warn("omp did not restart: " + ex.Message);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
            }
        }

        /// <inheritdoc />
        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 1)
            {
                return;
            }

            try
            {
                _adapters.Solution.SolutionChanged -= OnSolutionChanged;
            }
            catch (Exception)
            {
                // Best effort.
            }

            Timer? solutionTimer;
            lock (_stateLock)
            {
                solutionTimer = _solutionTimer;
                _solutionTimer = null;
            }

            solutionTimer?.Dispose();
            DisposeKnowledgeTimer();
            _bridge.StateChanged -= OnBridgeStateChanged;
            _bridge.FrameReceived -= OnFrameReceived;
            _bridge.DiagnosticReceived -= OnDiagnostic;
            _bridge.ConnectionStarted -= OnConnectionStarted;
            _bridge.ConnectionEnded -= OnConnectionEnded;
            PompLanguage.Changed -= OnHelloSettingsChanged;
            _settings.Changed -= OnHelloSettingsChanged;
            _ui.CommandReceived -= OnUiCommand;
            _ui.PageReady -= OnPageReady;

            Cancel(_disposeCts);
            CancelComposerPrediction();
            _taskApprovalDetails.Clear();
            _providerSetup.Dispose();
            CancelEpoch();
            _composerAuto?.Dispose();
            _workspaceReview?.Dispose();
            _isolationResults?.Dispose();
            _projectHooks?.Dispose();
            if (_projectEvents != null) _projectEvents.Occurred -= OnProjectEventForRules;
            _terminalJobs.Dispose();
            _interactiveTerminal.Dispose();
            _backgroundAgents.Dispose();
            _problems.Dispose();
            _advisors.Dispose();
            _codeReview?.Dispose();
            _codeSearch?.Dispose();
            _dispatcher.Dispose();
            _dispatcher.Completed -= OnHostToolCompleted;
            _bridge.Dispose();
            _ui.Dispose();
            lock (_knowledgeStateFileLock) _vsState.DeleteDirectory();
            _vsState.Dispose();
            if (_ownsMetrics)
            {
                _metrics.Dispose();
                _logger.Info(_metrics.Describe());
            }
        }

        private static string? MetricsPath(IPompHostEnvironment environment)
        {
            try
            {
                var directory = environment.DataDirectory;
                return string.IsNullOrWhiteSpace(directory) ? null : Path.Combine(Path.GetFullPath(directory), UsageMetrics.FileName);
            }
            catch (Exception ex) when (ex is ArgumentException || ex is NotSupportedException || ex is PathTooLongException || ex is System.Security.SecurityException)
            {
                return null;
            }
        }

        private void OnPageReady(object? sender, TimeSpan elapsed)
        {
            _providerSetup.Publish();
            _workspaceReview?.Publish();
            _isolationResults?.Publish();
            _composerWorkflow?.RefreshAsync(_disposeCts.Token).Forget(_logger, "composer state");
            _composerAuto?.Publish();
            _terminalJobs.Publish();
            _interactiveTerminal.Publish();
            _projectHooks?.Publish();
            _backgroundAgents.Publish(); _backgroundAgents.InitializeAsync().Forget(_logger, "background task metadata");
            _problems.RefreshAsync(_disposeCts.Token).Forget(_logger, "problems snapshot");
            _advisors.Publish();
            _codeSearch?.Publish();
            _codeSearch?.WarmAsync(false, _disposeCts.Token).Forget(_logger, "warm code index");
            _metrics.Time("ui.pageReady", elapsed);
            _logger.Info("The chat page was ready " + (long)elapsed.TotalMilliseconds + " ms after the tool window attached it.");
        }

        /// <summary>Host tool metrics, including per-file outcomes of buffer edits (the design's edit-accuracy metric).</summary>
        private void OnHostToolCompleted(object? sender, HostToolCompletedEventArgs e)
        {
            _composerWorkflow?.Observe(e);
            if (e.Cancelled)
            {
                _metrics.Count("tool." + e.ToolName + ".cancelled");
                return;
            }

            _metrics.Count("tool." + e.ToolName + (e.IsError ? ".error" : ".ok"));
            _metrics.Time("tool." + e.ToolName, e.ExecutionDuration);
            if (e.ToolName == HostToolNames.EditBufferHashline && e.Details?["files"] is JArray files)
            {
                foreach (var file in files.OfType<JObject>())
                {
                    if (file["status"]?.Type == JTokenType.String)
                    {
                        _metrics.Count("edit." + (string?)file["status"]);
                    }
                }
            }
        }

        // ---- launch ----------------------------------------------------------------------------------------------

        /// <summary>Everything needed for one omp start; called by the bridge for every attempt (also automatic restarts).</summary>
        private async Task<OmpLaunchOptions> BuildLaunchOptionsAsync(CancellationToken cancellationToken)
        {
            var resolution = await ResolveEngineAsync(cancellationToken).ConfigureAwait(false);

            if (resolution.ConfiguredPathProblem != null)
            {
                _logger.Warn(resolution.ConfiguredPathProblem + " Using " + resolution.Path + " (" + resolution.Source + ") instead.");
            }

            var dataDirectory = Path.GetFullPath(_environment.DataDirectory);
            var root = await ResolveWorkspaceRootAsync(dataDirectory, cancellationToken).ConfigureAwait(false);
            // omp applies no project trust (docs/architecture.md "작업 영역 신뢰"): engine configuration in the workspace that the user
            // has not trusted as it is now keeps omp from starting there; the chat shows what would run.
            var trust = _workspaceTrust.Evaluate(root, _settings.LoadDiscoveredOmpExtensions);
            PublishTrust(trust);
            if (trust.Required && !trust.Trusted)
                throw new OmpBridgeException(PompLanguage.Pick("이 작업 영역의 설정을 신뢰하지 않았어요.", "This workspace's configuration is not trusted."), BridgeFailureReasons.WorkspaceTrustRequired);

            // A .pomp copied from another project keeps only its settings; a moved project keeps its history (WorkspaceOrigin).
            try { ReportWorkspaceOrigin(await WorkspaceOrigin.ReconcileAsync(root, _environment as IPompRecycleBin, _logger, cancellationToken).ConfigureAwait(false)); }
            catch (Exception ex) when (!(ex is OperationCanceledException)) { _logger.Warn("Could not check where this workspace's POMP data came from: " + ex.Message); }

            // omp treats any --config overlay problem as a hard startup error: the file must exist (absolute path) before the spawn.
            var overlayPath = Path.Combine(dataDirectory, "omp", "pomp-overlay.yml");
            var hostTools = _registry.GetDefinitions().Select(definition => definition.Name).ToList();
            var blockNetwork = _settings.BlockOmpNetworkTools;

            var stateDirectory = Path.Combine(dataDirectory, "state", _hostProcessId.ToString(CultureInfo.InvariantCulture));
            try
            {
                // The state file only feeds omp's pre-edit guard: failing to provide it must not stop omp from starting.
                CleanStaleStateDirectories(Path.Combine(dataDirectory, "state"));
                _vsState.SetDirectory(stateDirectory);
                await _vsState.WriteAsync(true, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (!(ex is OperationCanceledException))
            {
                _logger.Warn("Could not prepare vs-state.json: " + ex.Message);
            }

            var extensionArguments = _settings.LoadOmpExtensions
                ? OmpExtensionArguments.Build(_environment.ExtensionDirectory ?? string.Empty)
                : Array.Empty<string>();
            // A project policy is enforced inside omp by the extension's guard: never start without it for such a workspace.
            if (ProjectExecutionPolicy.Exists(root) && (extensionArguments.Count == 0 || !OmpExtensionArguments.ContainsModule(_environment.ExtensionDirectory, "./extensions/permissions.ts")))
                throw new OmpBridgeException(PompLanguage.Pick("이 작업 영역의 .pomp/permissions.json을 적용할 수 없어요.", "The .pomp/permissions.json of this workspace cannot be enforced."));
            var composerGuardInstalled = extensionArguments.Count > 0 && OmpExtensionArguments.ContainsModule(_environment.ExtensionDirectory, "./extensions/composer-guard.ts");
            _composerWorkflow?.ConfigureRuntimeGuard(stateDirectory, composerGuardInstalled);
            // Subagents cannot ask for approval: without the extension's guard keeping them away, neither computer nor browser use runs.
            var computerGuardInstalled = extensionArguments.Count > 0 && OmpExtensionArguments.ContainsModule(_environment.ExtensionDirectory, "./extensions/computer-guard.ts");
            var computer = PrepareComputerUse(stateDirectory, computerGuardInstalled, blockNetwork);
            // Isolated tasks only with the extension that limits them to isolation teams (docs/architecture.md §8.11).
            var worktreeBase = extensionArguments.Count > 0 && OmpExtensionArguments.ContainsModule(_environment.ExtensionDirectory, "./extensions/teams.ts") ? ProjectData.Folder(root, "wt") : null;
            // The conversations live in the workspace's .pomp folder; the ones omp kept in its own folder move there first.
            var sessionDirectory = ProjectData.Sessions(root, ToolResourceFiles.Home(_options.UserHome), out var movedSessions);
            if (movedSessions != null)
            {
                try { new RecentSessionStore(dataDirectory, root).Relocate(movedSessions, sessionDirectory); }
                catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException || ex is InvalidOperationException) { _logger.Warn("Could not update the remembered conversations: " + ex.Message); }
            }
            // The project's roles reach omp as a copy in a plugin folder (never .pomp itself, whose skills and commands omp would load too).
            string? pluginDirectory = null;
            try { pluginDirectory = new AgentRoster(root, _options.UserHome, _options.UserTeamsDirectory).SyncEngineAgents(); }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException) { _logger.Warn("Could not prepare the project roles for the engine: " + ex.Message); }
            WriteOverlay(overlayPath, root, hostTools, blockNetwork ? OmpNetworkTools.Names : null, computer, _settings.ApprovalMode == OmpApprovalMode.Yolo, worktreeBase);
            // omp loads the .env of the folder it is started in (before --cwd moves it into the workspace): start it in POMP's own
            // folder so a repository's .env can never set provider endpoints, proxies or keys for the engine.
            var launchDirectory = Path.Combine(dataDirectory, "workspace");
            Directory.CreateDirectory(launchDirectory);
            var launch = new OmpLaunchOptions { ExecutablePath = resolution.Path!, WorkingDirectory = launchDirectory };
            lock (_stateLock) _launchedApprovalMode = _settings.ApprovalMode;
            foreach (var argument in OmpLaunchArguments.Build(
                _settings.UseRpcUiMode,
                root,
                _settings.ApprovalMode,
                overlayPath,
                _settings.LoadDiscoveredOmpExtensions,
                extensionArguments,
                LaunchModel(),
                _settings.ThinkingLevel,
                ArgumentSplitter.Split(_settings.ExtraArguments),
                sessionDirectory,
                pluginDirectory))
            {
                launch.Arguments.Add(argument);
            }

            // The store emits only allowlisted API-key names; host-owned POMP_* values are assigned afterwards.
            foreach (var pair in _providerCredentials.GetLaunchEnvironment())
            {
                if (!pair.Key.StartsWith("POMP_", StringComparison.OrdinalIgnoreCase)) launch.Environment[pair.Key] = pair.Value;
            }

            // The model servers the user saved in the account tab; the POMP extension registers them (extensions/local-models.ts).
            var localServers = _providerCredentials.LocalServersLaunchValue();
            if (localServers != null) launch.Environment["POMP_LOCAL_MODELS"] = localServers;
            launch.Environment["POMP_HOST"] = "1";
            launch.Environment["POMP_HOST_VERSION"] = _environment.HostVersion ?? string.Empty;
            launch.Environment["POMP_VS_VERSION"] = _environment.VsVersion ?? string.Empty;
            launch.Environment["POMP_SOLUTION_ROOT"] = root;
            launch.Environment["POMP_STATE_DIR"] = stateDirectory;
            // Isolated members work in copies under this folder; the permission guard accepts exactly those as their working directory.
            if (worktreeBase != null) launch.Environment["POMP_WORKTREE_BASE"] = worktreeBase;
            if (_composerWorkflow != null && composerGuardInstalled) launch.Environment["POMP_COMPOSER_GUARD"] = "1";

            // The omp-side package only recommends host tools that this host really offers (the same list as set_host_tools).
            launch.Environment["POMP_HOST_TOOLS"] = string.Join(",", hostTools);

            // Read by the omp extension's network guard, which also blocks read of an http(s) URL (no per-tool policy can).
            launch.Environment["POMP_NETWORK"] = blockNetwork ? "block" : "allow";

            // Read by the extension's computer guard: with browser control on, subagents never get Eval (they would drive it unasked).
            launch.Environment["POMP_BROWSER"] = computer.Browser ? "1" : "0";

            // Read by the extension's verification gate: with the "Verify after edits" option off, the gate never nudges.
            launch.Environment["POMP_VERIFY_GATE"] = _settings.VerifyAfterEdits ? "1" : "0";

            // Read by the extension's command output (VS status, guard), which shows in the chat in the POMP language.
            launch.Environment["POMP_LANGUAGE"] = PompLanguage.IsKorean ? "ko" : "en";

            lock (_stateLock)
            {
                if (!string.Equals(NormalizePath(root), NormalizePath(_workingDirectory), StringComparison.OrdinalIgnoreCase))
                {
                    _sessionFile = null;
                    _sessionFolderRoot = "";
                }
                _workingDirectory = root;
            }

            PublishKnowledgeChanged();
            // After the root is current: the role memory the extension reads belongs to this workspace (docs/architecture.md §8.9).
            PrepareRoleMemory(stateDirectory);
            return launch;
        }

        private async Task<string> ResolveWorkspaceRootAsync(string dataDirectory, CancellationToken cancellationToken)
        {
            string? root = null;
            try
            {
                root = await _environment.GetWorkspaceRootAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (!(ex is OperationCanceledException))
            {
                _logger.Warn("Could not determine the workspace root: " + ex.Message);
            }

            if (!string.IsNullOrWhiteSpace(root) && Directory.Exists(root))
            {
                return root!;
            }

            // An empty folder of pomp's own, never the user profile: omp's reads are approved without asking.
            var fallback = Path.Combine(dataDirectory, "workspace");
            Directory.CreateDirectory(fallback);
            _logger.Warn("The workspace root '" + root + "' is not usable; starting omp in '" + fallback + "'.");
            return fallback;
        }

        /// <summary>Removes state folders left behind by Visual Studio processes that no longer exist (crash, kill).</summary>
        private void CleanStaleStateDirectories(string stateRoot)
        {
            if (_staleStateCleaned)
            {
                return;
            }

            _staleStateCleaned = true;
            try
            {
                if (!Directory.Exists(stateRoot))
                {
                    return;
                }

                foreach (var directory in Directory.GetDirectories(stateRoot))
                {
                    if (!int.TryParse(Path.GetFileName(directory), NumberStyles.None, CultureInfo.InvariantCulture, out var pid) || pid == _hostProcessId)
                    {
                        continue;
                    }

                    if (IsProcessRunning(pid))
                    {
                        continue;
                    }

                    try
                    {
                        Directory.Delete(directory, true);
                    }
                    catch (Exception ex)
                    {
                        _logger.Debug("Could not delete the stale state folder '" + directory + "': " + ex.Message);
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.Debug("Stale state folder cleanup failed: " + ex.Message);
            }
        }

        private static bool IsProcessRunning(int pid)
        {
            try
            {
                using (var process = Process.GetProcessById(pid))
                {
                    return !process.HasExited;
                }
            }
            catch (ArgumentException)
            {
                return false;
            }
            catch (Exception)
            {
                // Access denied etc.: assume it is alive and leave the folder alone.
                return true;
            }
        }

        private static int CurrentProcessId()
        {
            using (var process = Process.GetCurrentProcess())
            {
                return process.Id;
            }
        }

        // ---- bridge state ------------------------------------------------------------------------------------------

        private void OnBridgeStateChanged(object? sender, BridgeStateChangedEventArgs e)
        {
            BridgeStatePayload payload;
            lock (_stateLock)
            {
                payload = new BridgeStatePayload
                {
                    State = e.State,
                    Message = e.Message,
                    Attempt = e.Attempt > 0 ? (int?)e.Attempt : null,
                    NextRetryMs = e.NextRetryIn.HasValue ? (int?)Math.Min(int.MaxValue, (long)e.NextRetryIn.Value.TotalMilliseconds) : null,
                    Reason = e.Reason,
                };
                _bridgeState = payload;
            }

            _ui.Post(UiMessageTypes.BridgeState, payload, UiDelivery.State);

            if (e.State == BridgeState.Running)
            {
                OnRunning();
            }
            ObserveAdvisorContext();
            _providerSetup.BridgeStateChanged(e.State);
            ComputerBridgeStateChanged(e.State);
            ResourceBridgeStateChanged(e.State);
            MainInstructionsBridgeStateChanged(e.State);
            if (e.State == BridgeState.Failed && e.Reason == BridgeFailureReasons.ModelNotFound) StartWithoutSavedModel();
        }

        // The configured model omp refused at start-up; launches leave it out until the setting names another model.
        private string? _refusedModel;

        private string? LaunchModel()
        {
            var model = _settings.Model;
            lock (_stateLock) return model != null && model == _refusedModel ? null : model;
        }

        /// <summary>omp exits at start-up when its --model is no longer in its list: start once more with omp's own default model.</summary>
        private void StartWithoutSavedModel()
        {
            var model = _settings.Model;
            lock (_stateLock)
            {
                if (string.IsNullOrWhiteSpace(model) || model == _refusedModel) return;
                _refusedModel = model;
            }
            PostNotice("warning", PompLanguage.Pick($"저장된 모델 {model!.Trim()}을(를) 찾을 수 없어 기본 모델로 시작해요.", $"The saved model {model!.Trim()} was not found; starting with the default model."));
            Task.Run(() => _bridge.StartAsync(CancellationToken.None)).Forget(_logger, "start without the saved model");
        }

        private JObject? CurrentSetupModel()
        {
            lock (_stateLock)
            {
                var model = _info.Model;
                return model == null ? null : new JObject { ["provider"] = model.Provider, ["id"] = model.Id };
            }
        }

        private string? CurrentSetupThinking()
        {
            lock (_stateLock)
            {
                return _info.ThinkingLevel;
            }
        }

        private void OnRunning()
        {
            PublishRunningCommands();
            _backgroundAgents.Connect();
            _interactiveTerminal.Connect();
            _interactiveTerminal.ProbeAsync(_disposeCts.Token).Forget(_logger, "terminal capability");
            _vsState.Start(_options.StateRefreshInterval);
            _commands.Enqueue(RecoverSessionAsync);
        }

        /// <summary>
        /// A new omp process exists: everything it asks of the user belongs to a fresh epoch (raised before its first frame). It is a
        /// new omp session too (crash recovery, restart, other workspace), so "allow for this session" grants end here.
        /// </summary>
        private void OnConnectionStarted(object? sender, EventArgs e)
        {
            _metrics.Count("omp.started");
            _gate.ResetSession();
            Interlocked.Exchange(ref _workspaceSwitching, 0);
            BeginEpoch();
        }

        /// <summary>Why a tool call or approval must not run now: the open solution changed and omp has not restarted in it yet.</summary>
        private string? WorkspaceSwitchBlock() => Volatile.Read(ref _workspaceSwitching) == 1
            ? PompLanguage.Pick("작업 영역이 바뀌는 중이에요.", "The workspace is changing.")
            : null;

        /// <summary>The omp process went away: everything that depended on it is finished.</summary>
        private void OnConnectionEnded(object? sender, ConnectionEndedEventArgs e)
        {
            _composerWorkflow?.Interrupt(PompLanguage.Pick("연결이 끊겼어요.", "Disconnected."));
            if (!e.StoppedByHost)
            {
                _metrics.Count("omp.lost");
            }

            _vsState.Stop();
            _composerAuto?.Stop(PompLanguage.Pick("연결이 끊겼어요. 자동 이어가기가 꺼졌어요.", "Disconnected. Automatic continuation is off."));
            PauseChatQueue(PompLanguage.Pick("연결이 끊겼어요.", "Disconnected."));
            _backgroundAgents.Disconnect();
            _interactiveTerminal.Disconnect();
            _projectHooks?.SetSession(_workingDirectory, "");
            CancelEpoch();
            ClearCommands();
            _dispatcher.CancelAll();

            // Close the in-flight messages and tool cards first: the UI finalizes whatever is still open when the turn ends.
            ResetStreamingState(e.StoppedByHost ? PompLanguage.Pick("연결이 중지됐어요", "The connection was stopped") : e.Message);
            EndAllTurns(
                e.StoppedByHost ? "aborted" : "error",
                e.StoppedByHost ? null : new UiErrorInfo { Code = "connection_lost", Message = e.Message, Retryable = true });
        }

        private void OnDiagnostic(object? sender, string line)
        {
            // Already logged (at debug level) by the bridge; nothing else to do with stderr.
        }

        private BridgeStatePayload CurrentBridgeState()
        {
            lock (_stateLock)
            {
                return _bridgeState;
            }
        }

        // ---- epoch: everything asked of the user by the current omp process is cancelled when that process ends -------

        private void BeginEpoch()
        {
            var connection = _bridge.CaptureConnection();
            CancellationTokenSource? old;
            SessionInfoPayload info;
            lock (_stateLock)
            {
                old = _epoch;
                _epoch = new CancellationTokenSource();
                _epochConnection = connection;
                _availableCommands = new JArray();
                _commandCatalogRevision++;
                BeginSessionRecoveryLocked();
                _sessionFile = null;
                _recordedSession = "";
                // A new engine connection never resumes automatic forwarding of event rules.
                _hookAutoForward = false;
                info = _info;
            }

            // Not under the state lock: the page's ready handler takes that lock while holding the hub's.
            _ui.Post(UiMessageTypes.SessionInfo, info, UiDelivery.State);
            ObserveAdvisorContext();
            Cancel(old);
        }

        private void CancelEpoch()
        {
            CancellationTokenSource old;
            lock (_stateLock)
            {
                old = _epoch;
            }

            Cancel(old);
        }

        private CancellationToken CurrentEpochToken()
        {
            lock (_stateLock)
            {
                return _epoch.Token;
            }
        }

        private void Cancel(CancellationTokenSource? source)
        {
            try
            {
                source?.Cancel();
            }
            catch (ObjectDisposedException)
            {
            }
            catch (AggregateException ex)
            {
                // A callback registered on the token threw (all of them still ran): the cleanup that follows must still happen.
                _logger.Warn("A cancellation callback failed: " + ex.InnerException?.Message);
            }
        }

        // ---- solution changes --------------------------------------------------------------------------------------

        private void OnSolutionChanged(object? sender, EventArgs e)
        {
            _composerAuto?.Stop(PompLanguage.Pick("작업 영역이 바뀌었어요. 자동 이어가기가 꺼졌어요.", "Workspace changed. Automatic continuation is off."));
            _composerWorkflow?.Interrupt(PompLanguage.Pick("작업 영역이 바뀌었어요.", "Workspace changed."));
            if (Volatile.Read(ref _disposed) == 1)
            {
                return;
            }

            // Until omp restarts in the new root (or the root proves unchanged), nothing the previous workspace's omp asks may run
            // and its "this session" grants end now: host tools resolve the root per call and would act on the new workspace.
            Interlocked.Exchange(ref _workspaceSwitching, 1);
            _isolationResults?.SetContext("", "");
            ClearActiveRules();
            _gate.ResetSession();

            lock (_stateLock)
            {
                if (_solutionTimer == null)
                {
                    _solutionTimer = new Timer(_ => OnSolutionChangeSettled(), null, _options.SolutionChangeDebounce, Timeout.InfiniteTimeSpan);
                }
                else
                {
                    _solutionTimer.Change(_options.SolutionChangeDebounce, Timeout.InfiniteTimeSpan);
                }
            }
        }

        private void OnSolutionChangeSettled()
        {
            RestartForNewWorkspaceAsync().Forget(_logger, "solution change restart");
        }

        private async Task RestartForNewWorkspaceAsync()
        {
            if (Volatile.Read(ref _disposed) == 1)
            {
                return;
            }

            var state = _bridge.State;
            if (state == BridgeState.Stopped || state == BridgeState.Stopping)
            {
                // omp was never started (tool window not opened) or was stopped on purpose: the next start picks up the new root.
                return;
            }

            string root;
            try
            {
                root = await _environment.GetWorkspaceRootAsync(_disposeCts.Token).ConfigureAwait(false);
            }
            catch (Exception ex) when (!(ex is OperationCanceledException))
            {
                _logger.Warn("Could not determine the new workspace root: " + ex.Message);
                return;
            }

            string current;
            lock (_stateLock)
            {
                current = _workingDirectory;
            }

            if (string.Equals(NormalizePath(root), NormalizePath(current), StringComparison.OrdinalIgnoreCase))
            {
                _logger.Debug("The solution changed but the workspace root is the same; omp keeps running.");
                Interlocked.Exchange(ref _workspaceSwitching, 0);
                _isolationResults?.SetContext(current, _info.SessionId ?? "");
                lock (_stateLock) InvalidateActiveRulesLocked();
                RefreshActiveRules();
                return;
            }

            _logger.Info("The workspace root changed ('" + current + "' -> '" + root + "'); restarting omp.");
            await RestartAsync(_disposeCts.Token).ConfigureAwait(false);
            PublishAgentsState();
            PublishKnowledgeChanged();
        }

        private static string NormalizePath(string? path)
        {
            if (string.IsNullOrWhiteSpace(path))
            {
                return string.Empty;
            }

            try
            {
                return Path.GetFullPath(path!).TrimEnd('\\', '/');
            }
            catch (Exception)
            {
                return path!.TrimEnd('\\', '/');
            }
        }

        private void ThrowIfDisposed()
        {
            if (Volatile.Read(ref _disposed) == 1)
            {
                throw new ObjectDisposedException(nameof(PompSession));
            }
        }
    }

    /// <summary>Runs work items one after the other, in the order they were queued (UI commands must not overtake each other).</summary>
    internal sealed class SerialQueue
    {
        private readonly IPompLogger _logger;
        private readonly object _lock = new object();
        private Task _tail = Task.CompletedTask;

        public SerialQueue(IPompLogger logger)
        {
            _logger = logger;
        }

        public Task Enqueue(Func<Task> work)
        {
            lock (_lock)
            {
                _tail = _tail.ContinueWith(_ => RunAsync(work), CancellationToken.None, TaskContinuationOptions.None, TaskScheduler.Default).Unwrap();
                return _tail;
            }
        }

        private async Task RunAsync(Func<Task> work)
        {
            try
            {
                await work().ConfigureAwait(false);
            }
            catch (Exception ex) when (!(ex is OperationCanceledException))
            {
                _logger.Error("A queued UI command failed", ex);
            }
        }
    }
}
