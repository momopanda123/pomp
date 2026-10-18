using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Threading;
using Pomp.Diagnostics;
using Pomp.Features;
using Pomp.Hosting;
using Pomp.Protocol;
using Pomp.Tools;
using Pomp.VisualStudio.Adapters;
using Pomp.VisualStudio.Commands;
using Pomp.VisualStudio.Hosting;
using Pomp.VisualStudio.Interop;
using Pomp.VisualStudio.Logging;
using Pomp.VisualStudio.Editor;
using Pomp.VisualStudio.Options;
using Task = System.Threading.Tasks.Task;

namespace Pomp.VisualStudio.Composition
{
    /// <summary>
    /// Composition root of the Visual Studio layer: owns the logger, settings, host environment, adapters and the chat
    /// session, and keeps the session in step with solution and settings changes.
    /// <para>
    /// The ONE place that decides which <see cref="IPompSession"/> runs is <see cref="CreateSession"/>.
    /// </para>
    /// </summary>
    internal sealed class PompServices : IDisposable
    {
        private static PompServices? currentServices;
        public static PompServices? Current => Volatile.Read(ref currentServices);

        private static readonly TimeSpan RestartDebounce = TimeSpan.FromMilliseconds(750);

        private readonly object gate = new object();
        private IPompSession? session;
        private bool sessionStartRequested;
        private CancellationTokenSource? restartDebounce;
        private LaunchSettings? requestedLaunchSettings;
        private bool restartPending;
        private bool disposed;

        private PompServices(
            PompPackage package,
            VsPompLogger logger,
            PompOptionsPage settings,
            VsHostEnvironment environment,
            VsAdapterFactory adapters)
        {
            Package = package;
            JoinableTaskFactory = package.JoinableTaskFactory;
            Logger = logger;
            Settings = settings;
            Environment = environment;
            Adapters = adapters;
            Commands = new PompCommandActions(package, this);
            EditorFollow = new EditorFollowService(this);
        }

        public PompPackage Package { get; }

        public JoinableTaskFactory JoinableTaskFactory { get; }

        public VsPompLogger Logger { get; }

        /// <summary>The options page instance; persisted values are always current.</summary>
        public PompOptionsPage Settings { get; }

        public VsHostEnvironment Environment { get; }

        /// <summary>All Visual Studio adapters (design Layer 3); hand these to the host tool catalog.</summary>
        public VsAdapterFactory Adapters { get; }

        /// <summary>Command implementations shared by the menu commands and the chat page's shortcut.</summary>
        public PompCommandActions Commands { get; }

        /// <summary>Host-only edit traces, navigation and owned bookmarks; never invokes the model.</summary>
        public EditorFollowService EditorFollow { get; }

        /// <summary>Reads the host's current epoch, not a potentially delayed WebView projection.</summary>
        public void RefreshEditorSession()
        {
            Microsoft.VisualStudio.Shell.ThreadHelper.ThrowIfNotOnUIThread();
            IEditorSessionContextSource? source;
            lock (gate) source = session as IEditorSessionContextSource;
            if (source == null) return;
            var state = source.EditorSessionContext;
            EditorFollow.SetSession(state.WorkspaceRoot, state.SessionId, state.IsStreaming, state.TaskId, state.SessionTitle);
        }

        /// <summary>The chat session (created on first use, on the UI thread).</summary>
        public IPompSession Session
        {
            get
            {
                lock (gate)
                {
                    if (disposed)
                    {
                        throw new ObjectDisposedException(nameof(PompServices));
                    }

                    return session ??= CreateSession();
                }
            }
        }

        /// <summary>The knowledge of the open workspace for the editor (menu, line colors): the session's, or null when it offers none or the services are disposed.</summary>
        public IKnowledgeEditor? KnowledgeEditor
        {
            get
            {
                try
                {
                    return Session as IKnowledgeEditor;
                }
                catch (ObjectDisposedException)
                {
                    return null;
                }
            }
        }

        /// <summary>Creates the services. Must run on (or switches to) the UI thread.</summary>
        public static async Task<PompServices> CreateAsync(PompPackage package, CancellationToken cancellationToken)
        {
            var joinableTaskFactory = package.JoinableTaskFactory;
            await joinableTaskFactory.SwitchToMainThreadAsync(cancellationToken);

            var logger = new VsPompLogger(joinableTaskFactory, VsHostEnvironment.ResolveDataDirectory());
            var settings = (PompOptionsPage)package.GetDialogPage(typeof(PompOptionsPage));
            var environment = new VsHostEnvironment(package, joinableTaskFactory, logger);
            var adapters = await VsAdapterFactory.CreateAsync(package, joinableTaskFactory, logger, cancellationToken);

            var services = new PompServices(package, logger, settings, environment, adapters);
            services.SubscribeLifecycleEvents();
            Volatile.Write(ref currentServices, services);

            logger.Info($"Pomp {environment.HostVersion} loaded in Visual Studio {environment.VsVersion} ({(System.Environment.Is64BitProcess ? "x64" : "x86")}). Extension folder: {environment.ExtensionDirectory}");
            return services;
        }

        /// <summary>
        /// Lazily starts omp once the chat page has loaded (design: process start deferred until the window is shown; the chat window
        /// calls this when its start page finished loading, which includes the reload after a renderer crash and the fallback panel's
        /// Retry). Idempotent while omp runs or after it failed. Failures are logged; the UI shows the bridge state.
        /// </summary>
        public void StartSession()
        {
            _ = JoinableTaskFactory.RunAsync(async () =>
            {
                try
                {
                    await JoinableTaskFactory.SwitchToMainThreadAsync();
                    IPompSession current;
                    lock (gate)
                    {
                        if (disposed)
                        {
                            return;
                        }

                        sessionStartRequested = true;
                        current = session ??= CreateSession();

                        // The launch settings this first start asks for: the baseline the next Apply is compared with (see OnSettingsChanged).
                        requestedLaunchSettings ??= LaunchSettings.Capture(Settings);
                    }

                    await current.EnsureStartedAsync(Package.DisposalToken);
                }
                catch (OperationCanceledException)
                {
                    // Shutting down.
                }
                catch (Exception ex)
                {
                    Logger.Error("Starting the agent session failed.", ex);
                }
            });
        }

        public void Dispose()
        {
            Interlocked.CompareExchange(ref currentServices, null, this);
            IPompSession? current;
            CancellationTokenSource? pendingRestart;
            lock (gate)
            {
                if (disposed)
                {
                    return;
                }

                disposed = true;
                current = session;
                session = null;
                pendingRestart = restartDebounce;
                restartDebounce = null;
            }

            Settings.Changed -= OnSettingsChanged;

            pendingRestart?.Cancel();
            pendingRestart?.Dispose();

            // A failure here (a view redraw during shutdown) must not skip stopping omp below.
            try
            {
                JoinableTaskFactory.Run(async () =>
                {
                    await JoinableTaskFactory.SwitchToMainThreadAsync();
                    EditorFollow.Dispose();
                });
            }
            catch (Exception ex)
            {
                Logger.Warn("Disposing the Live editor features failed.", ex);
            }

            try
            {
                // The real session's Dispose also stops the omp process tree (no zombies when VS exits).
                current?.Dispose();
            }
            catch (Exception ex)
            {
                Logger.Warn("Disposing the agent session failed.", ex);
            }

            Adapters.Dispose();
            Logger.Info("Pomp services disposed.");
            Logger.Dispose();
        }

        /// <summary>Creates one registry and session per IDE instance; the tool window attaches its UI separately.</summary>
        private IPompSession CreateSession()
        {
            var registry = new CustomToolRegistry();

            // The logger lets the tool layer record unexpected adapter failures (COMException...) in the ActivityLog.
            foreach (var tool in HostToolCatalog.CreateDefault(Adapters, Environment, Logger))
            {
                registry.Register(tool);
            }

            return new PompSession(Settings, Environment, Adapters, registry, Logger);
        }

        private void SubscribeLifecycleEvents()
        {
            // PompSession owns solution-root changes. Subscribing here too would restart omp twice.
            Settings.Changed += OnSettingsChanged;
        }

        /// <summary>
        /// Raised (UI thread) after the options dialog applied. Visual Studio calls Apply for every page that was opened, changed or not,
        /// and most settings do not affect a running omp (approvals, editor-context default): only a change in one of the
        /// launch settings restarts the session, because a restart ends the running turn and the conversation.
        /// <para>
        /// A change is detected against the launch settings most recently REQUESTED (the first start, or the last Apply that
        /// differed), not against an assumed state of the running process: omp is launched with the live settings at every start
        /// (the automatic retry, a manual restart and a solution change launch it too), so the process may already be ahead of any
        /// cached baseline. A change stores itself as the new request and marks a restart as pending; the debounced restart then runs
        /// unconditionally. An Apply that undoes a change inside the debounce window (A to B to A) thus restarts once, redundantly,
        /// instead of risking that the process keeps B.
        /// </para>
        /// </summary>
        private void OnSettingsChanged(object? sender, EventArgs e)
        {
            var applied = LaunchSettings.Capture(Settings);
            bool launchChanged;
            lock (gate)
            {
                // No request yet = omp was never started; the first start reads the current values anyway.
                launchChanged = requestedLaunchSettings != null && !requestedLaunchSettings.Equals(applied);
                if (launchChanged)
                {
                    requestedLaunchSettings = applied;
                    restartPending = true;
                }
            }

            if (!launchChanged)
            {
                Logger.Debug("Settings applied; none of the omp launch settings changed, so omp keeps running.");
                return;
            }

            ScheduleRestart("a launch setting changed");
        }

        /// <summary>
        /// (Re)starts the debounce of a pending restart: every further change moves the restart back, and once the settings stayed quiet
        /// for <see cref="RestartDebounce"/> the session is restarted, if it was started.
        /// </summary>
        private void ScheduleRestart(string reason)
        {
            CancellationTokenSource cts;
            lock (gate)
            {
                if (disposed || session == null || !sessionStartRequested)
                {
                    return;
                }

                restartDebounce?.Cancel();
                restartDebounce?.Dispose();
                cts = restartDebounce = new CancellationTokenSource();
            }

            var token = cts.Token;
            _ = JoinableTaskFactory.RunAsync(async () =>
            {
                try
                {
                    await Task.Delay(RestartDebounce, token);
                    await JoinableTaskFactory.SwitchToMainThreadAsync(token);

                    // A pending restart is unconditional: whatever the running process was launched with, the next launch reads the live
                    // settings, and nothing is compared with an earlier baseline here (that comparison is what could wrongly skip it).
                    IPompSession? current;
                    bool restart;
                    lock (gate)
                    {
                        current = disposed ? null : session;
                        restart = restartPending;
                        restartPending = false;
                    }

                    if (current != null && restart)
                    {
                        Logger.Info("Restarting the agent session because " + reason + ".");
                        await current.RestartAsync(Package.DisposalToken);
                    }
                }
                catch (OperationCanceledException)
                {
                    // Superseded by a newer change, or shutting down.
                }
                catch (Exception ex)
                {
                    Logger.Error("Restarting the agent session failed.", ex);
                }
            });
        }

        /// <summary>
        /// The settings that change how omp is launched (compared as a snapshot of the most recently requested launch settings).
        /// The always-allowed permission keys and the editor-context default are deliberately absent: they never need a new process.
        /// </summary>
        private sealed class LaunchSettings : IEquatable<LaunchSettings>
        {
            private readonly string? ompExecutablePath;
            private readonly OmpApprovalMode approvalMode;
            private readonly string? model;
            private readonly string? thinkingLevel;
            private readonly string? extraArguments;
            private readonly bool useRpcUiMode;
            private readonly bool loadOmpExtensions;
            private readonly bool loadDiscoveredOmpExtensions;
            private readonly bool blockOmpNetworkTools;
            private readonly bool verifyAfterEdits;
            private readonly ComputerUsePolicy computerPolicy;
            private readonly bool browserControl;
            private readonly bool computerConfirmReads;

            private LaunchSettings(IPompSettings settings)
            {
                ompExecutablePath = settings.OmpExecutablePath;
                approvalMode = settings.ApprovalMode;
                model = settings.Model;
                thinkingLevel = settings.ThinkingLevel;
                extraArguments = settings.ExtraArguments;
                useRpcUiMode = settings.UseRpcUiMode;
                loadOmpExtensions = settings.LoadOmpExtensions;
                loadDiscoveredOmpExtensions = settings.LoadDiscoveredOmpExtensions;
                blockOmpNetworkTools = settings.BlockOmpNetworkTools;
                verifyAfterEdits = settings.VerifyAfterEdits;
                // Computer use lives in omp's config overlay and its start-up state (docs/architecture.md §8.5).
                var computer = (settings as IComputerUseSettings)?.ComputerUse ?? ComputerUseOptions.Default;
                computerPolicy = computer.Policy;
                browserControl = computer.Browser;
                computerConfirmReads = computer.ConfirmReads;
            }

            public static LaunchSettings Capture(IPompSettings settings) => new LaunchSettings(settings);

            public bool Equals(LaunchSettings? other)
            {
                return other != null
                       && string.Equals(ompExecutablePath, other.ompExecutablePath, StringComparison.Ordinal)
                       && approvalMode == other.approvalMode
                       && string.Equals(model, other.model, StringComparison.Ordinal)
                       && string.Equals(thinkingLevel, other.thinkingLevel, StringComparison.Ordinal)
                       && string.Equals(extraArguments, other.extraArguments, StringComparison.Ordinal)
                       && useRpcUiMode == other.useRpcUiMode
                       && loadOmpExtensions == other.loadOmpExtensions
                       && loadDiscoveredOmpExtensions == other.loadDiscoveredOmpExtensions
                       && blockOmpNetworkTools == other.blockOmpNetworkTools
                       && verifyAfterEdits == other.verifyAfterEdits
                       && computerPolicy == other.computerPolicy
                       && browserControl == other.browserControl
                       && computerConfirmReads == other.computerConfirmReads;
            }

            public override bool Equals(object? obj) => Equals(obj as LaunchSettings);

            public override int GetHashCode()
            {
                unchecked
                {
                    var hash = 17;
                    hash = (hash * 31) + (ompExecutablePath?.GetHashCode() ?? 0);
                    hash = (hash * 31) + (int)approvalMode;
                    hash = (hash * 31) + (model?.GetHashCode() ?? 0);
                    hash = (hash * 31) + (thinkingLevel?.GetHashCode() ?? 0);
                    hash = (hash * 31) + (extraArguments?.GetHashCode() ?? 0);
                    hash = (hash * 31) + (useRpcUiMode ? 1 : 0);
                    hash = (hash * 31) + (loadOmpExtensions ? 1 : 0);
                    hash = (hash * 31) + (loadDiscoveredOmpExtensions ? 1 : 0);
                    hash = (hash * 31) + (blockOmpNetworkTools ? 1 : 0);
                    hash = (hash * 31) + (verifyAfterEdits ? 1 : 0);
                    hash = (hash * 31) + (int)computerPolicy;
                    hash = (hash * 31) + (browserControl ? 1 : 0);
                    hash = (hash * 31) + (computerConfirmReads ? 1 : 0);
                    return hash;
                }
            }
        }
    }
}
