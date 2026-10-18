using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using Microsoft.VisualStudio.PlatformUI;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Threading;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.Wpf;
using Newtonsoft.Json.Linq;
using Pomp.Diagnostics;
using Pomp.Ui;
using Pomp.VisualStudio.Composition;
using Pomp.VisualStudio.Interop;
using Pomp.VisualStudio.Options;
using Task = System.Threading.Tasks.Task;

namespace Pomp.VisualStudio.ToolWindow
{
    /// <summary>
    /// WPF content of the POMP tool window, built in code: hosts the WebView2 that renders the chat UI from the
    /// extension's <c>WebUI</c> folder (virtual host https://pomp.example) or, when WebView2 cannot start, a fallback
    /// panel explaining why (with the runtime download link).
    /// <para>
    /// The omp session starts only once the chat page itself has loaded (<c>NavigationCompleted</c> of the own start page, see
    /// <see cref="OnNavigationCompleted"/>): a machine without a working WebView2, or a page that fails to load, does not run an agent
    /// nobody can talk to. The fallback's Retry goes through the same path.
    /// </para>
    /// </summary>
    internal sealed class PompChatControl : UserControl, IDisposable
    {
        /// <summary>Virtual host name mapped to the WebUI folder; the only origin the page may navigate to.</summary>
        public const string VirtualHost = "pomp.example";

        private const string StartUrl = "https://" + VirtualHost + "/index.html";
        private const int MaxBrowserRestarts = 3;

        /// <summary>How long a document of the own origin may take to send <c>ui/ready</c> before the fallback panel replaces it.</summary>
        private static readonly TimeSpan ReadyTimeout = TimeSpan.FromSeconds(20);

        /// <summary>A page that stayed up this long is a healthy one: browser crashes before it do not count against the next one.</summary>
        private static readonly TimeSpan StableSession = TimeSpan.FromSeconds(60);

        /// <summary>A renderer reported unresponsive gets this long to recover on its own before it is probed and, if need be, reloaded.</summary>
        private static readonly TimeSpan UnresponsiveGrace = TimeSpan.FromSeconds(10);

        private static readonly TimeSpan UnresponsiveProbeTimeout = TimeSpan.FromSeconds(3);

        /// <summary>The focus probe of the toggle must not wait for a hung renderer.</summary>
        private static readonly TimeSpan FocusProbeTimeout = TimeSpan.FromMilliseconds(500);

        // The editor <-> chat focus toggle inside the page is the page's own key handler (host/hello.shortcuts.toggleFocus, the same
        // chord as the IDE binding), which posts WebViewUiChannel.ToggleFocusMessageType; nothing is injected for it.

        // Puts the caret into the chat composer (the page marks it data-pomp-composer; falls back to the first text input).
        // The composer only when it is on screen: on another tab (Tools › Knowledge with the editor's lines) the page keeps its own focus.
        private const string FocusComposerScript =
            "(function(){var el=document.querySelector('[data-pomp-composer]');" +
            "if(el&&el.getClientRects().length){el.focus();}else{window.focus();}})();";

        private readonly PompServices services;
        private readonly JoinableTaskFactory joinableTaskFactory;
        private readonly IPompLogger logger;
        private readonly Grid root = new Grid();
        private readonly WebViewUiChannel channel;
        private readonly OcclusionClip occlusionClip;
        private ThemeBridge? themeBridge;
        private WebView2? webView;
        private FrameworkElement? fallbackPanel;
        private CancellationTokenSource? readyWatchdog;
        private ulong lastOwnNavigationId;
        private DateTime pageReadyUtc = DateTime.MinValue;
        private int initializationGeneration;
        private bool initializationStarted;
        private bool initializing;
        private bool recoveryScheduled;
        private bool disposed;
        private bool focusWhenReady;
        private int browserRestarts;

        public PompChatControl(PompServices services)
        {
            ThreadHelper.ThrowIfNotOnUIThread();

            this.services = services;
            joinableTaskFactory = services.JoinableTaskFactory;
            logger = services.Logger;

            root.SetResourceReference(Panel.BackgroundProperty, EnvironmentColors.ToolWindowBackgroundBrushKey);
            Content = root;
            Focusable = true;

            channel = new WebViewUiChannel(joinableTaskFactory, logger, VirtualHost);
            channel.ToggleFocusRequested += OnToggleFocusRequested;
            channel.PasteFilesRequested += OnPasteFilesRequested;
            channel.FilesDropped += OnFilesDropped;
            channel.PageReady += OnPageReady;
            channel.EditorFollowRequested += OnEditorFollowRequested;
            channel.StatusRequested += OnStatusRequested;
            channel.LiveFailed += OnLiveFailed;
            channel.ObserveSession = ObserveEditorSession;
            services.Settings.Changed += OnEditorFollowSettingsChanged;

            services.Session.AttachUi(channel);
            Loaded += OnLoaded;
            IsVisibleChanged += OnIsVisibleChanged;

            // An auto-hide pane sliding out over the chat (Server Explorer, Toolbox) would otherwise be painted under it.
            occlusionClip = new OcclusionClip(joinableTaskFactory, logger, () => webView?.Handle ?? IntPtr.Zero);
        }

        /// <summary>
        /// WebView2 renders in a native child window (an HwndHost). Visual Studio moves tool window content between hosts (tab
        /// groups, auto-hide slide-outs, floating frames), so a hidden chat must hide that window itself: otherwise it keeps
        /// drawing over the pane shown in its place. Hidden also tells WebView2 to stop rendering.
        /// </summary>
        private void OnIsVisibleChanged(object sender, DependencyPropertyChangedEventArgs e)
        {
            var view = webView;
            if (view == null || disposed)
            {
                return;
            }

            var visible = (bool)e.NewValue;
            view.Visibility = visible ? Visibility.Visible : Visibility.Hidden;
            NativeMethods.SetChildWindowVisible(view.Handle, visible);
            if (visible)
            {
                // Re-place the child window in the (possibly new) host before it is painted.
                view.InvalidateMeasure();
                view.UpdateLayout();
                occlusionClip.Invalidate();
            }
        }

        /// <summary>Asks the session for a fresh conversation, exactly as the page's "new session" action does.</summary>
        public void RequestNewSession() => channel.InjectFromHost(new UiMessage(UiMessageTypes.SessionNew));

        /// <summary>Synchronous WPF/native focus ownership for editor operations, without a renderer round trip.</summary>
        internal bool HasKeyboardFocus
        {
            get
            {
                ThreadHelper.ThrowIfNotOnUIThread();
                return !disposed && IsVisible && (IsKeyboardFocusWithin || (webView != null
                    && (webView.IsKeyboardFocusWithin || webView.IsFocused || NativeMethods.IsFocusWithin(webView.Handle))));
            }
        }

        /// <summary>True when keyboard focus is inside the chat (the WebView or the fallback panel).</summary>
        public async Task<bool> IsChatFocusedAsync()
        {
            await joinableTaskFactory.SwitchToMainThreadAsync();

            if (disposed)
            {
                return false;
            }

            if (IsKeyboardFocusWithin)
            {
                return true;
            }

            var view = webView;
            if (view == null)
            {
                return false;
            }

            if (view.IsKeyboardFocusWithin || view.IsFocused)
            {
                return true;
            }

            var hwnd = view.Handle;
            if (hwnd != IntPtr.Zero && NativeMethods.IsFocusWithin(hwnd))
            {
                return true;
            }

            try
            {
                var core = view.CoreWebView2;
                if (core != null && IsVisible)
                {
                    var probe = core.ExecuteScriptAsync("document.hasFocus()");
                    if (await Task.WhenAny(probe, Task.Delay(FocusProbeTimeout)) != probe)
                    {
                        // A hung renderer must not block the toggle (this probe runs on every editor -> chat toggle):
                        // count it as "not focused" and observe the late result.
                        probe.Forget(logger, "Probing the chat focus");
                        return false;
                    }

                    var hasFocus = await probe;
                    return string.Equals(hasFocus, "true", StringComparison.Ordinal);
                }
            }
            catch (Exception ex)
            {
                logger.Debug("document.hasFocus() probe failed: " + ex.Message);
            }

            return false;
        }

        /// <summary>Moves keyboard focus into the chat and (when the page is up) into its composer.</summary>
        public void FocusChat()
        {
            ThreadHelper.ThrowIfNotOnUIThread();

            if (disposed)
            {
                return;
            }

            var view = webView;
            if (view != null && view.CoreWebView2 != null && channel.IsReady)
            {
                view.Focus();
                view.CoreWebView2.ExecuteScriptAsync(FocusComposerScript).Forget(logger, "Focusing the chat composer");
                return;
            }

            // The page is not up yet: focus the control now and the composer as soon as the page reports ready.
            focusWhenReady = true;
            Focus();
        }

        private void OnPageReady(object? sender, EventArgs e)
        {
            ThreadHelper.ThrowIfNotOnUIThread();

            CancelReadyWatchdog();
            pageReadyUtc = DateTime.UtcNow;
            PublishEditorFollowAsync().Forget(logger, "Publishing Live editor settings");

            if (focusWhenReady)
            {
                focusWhenReady = false;
                FocusChat();
            }
        }

        private void ObserveEditorSession(UiMessage message)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            services.RefreshEditorSession();
        }

        private void OnEditorFollowSettingsChanged(object? sender, EventArgs e)
        {
            if (!disposed) PublishEditorFollowAsync().Forget(logger, "Publishing Live editor settings");
        }

        private Task PublishEditorFollowAsync(string? requestId = null, string? error = null)
        {
            var settings = services.Settings;
            return channel.PostAsync(new UiMessage("host/editorFollow", new
            {
                live = settings.AppliedLiveFollowEnabled,
                bookmarks = settings.AppliedLiveBookmarksEnabled,
                requestId,
                error,
            }));
        }

        private void OnEditorFollowRequested(object? sender, UiMessage message) =>
            services.JoinableTaskFactory.RunAsync(() => HandleEditorFollowAsync(message)).Task.Forget(logger, "Handling Live editor settings");

        private async Task HandleEditorFollowAsync(UiMessage message)
        {
            var payload = message.Payload;
            string? requestId = null;
            try
            {
                await services.JoinableTaskFactory.SwitchToMainThreadAsync();
                requestId = payload?["requestId"]?.Type == JTokenType.String ? (string?)payload["requestId"] : null;
                if (payload == null || string.IsNullOrWhiteSpace(requestId))
                    throw new ArgumentException("A Live editor request needs a requestId.");
                var action = (string?)payload["action"];
                if (action == "set")
                {
                    if (payload["live"] != null && payload["live"]!.Type != JTokenType.Boolean
                        || payload["bookmarks"] != null && payload["bookmarks"]!.Type != JTokenType.Boolean)
                        throw new ArgumentException("Live and bookmarks must be booleans.");
                    if (payload["live"] == null && payload["bookmarks"] == null)
                        throw new ArgumentException("A Live editor setting is required.");
                    services.Settings.SetLiveEditorFeatures((bool?)payload["live"], (bool?)payload["bookmarks"]);
                }
                else throw new ArgumentException("Unknown Live editor action.");
                await PublishEditorFollowAsync(requestId);
            }
            catch (Exception ex)
            {
                logger.Error("Changing Live editor settings failed.", ex);
                try { await PublishEditorFollowAsync(requestId, ex.Message); }
                catch (Exception postError) { logger.Error("Reporting the Live editor error failed.", postError); }
            }
        }

        public void Dispose()
        {
            if (disposed)
            {
                return;
            }

            disposed = true;
            Loaded -= OnLoaded;
            IsVisibleChanged -= OnIsVisibleChanged;
            occlusionClip.Dispose();
            channel.ToggleFocusRequested -= OnToggleFocusRequested;
            channel.PasteFilesRequested -= OnPasteFilesRequested;
            channel.FilesDropped -= OnFilesDropped;
            channel.PageReady -= OnPageReady;
            channel.EditorFollowRequested -= OnEditorFollowRequested;
            channel.StatusRequested -= OnStatusRequested;
            channel.LiveFailed -= OnLiveFailed;
            channel.ObserveSession = null;
            services.Settings.Changed -= OnEditorFollowSettingsChanged;
            CancelReadyWatchdog();

            try
            {
                services.Session.DetachUi(channel);
            }
            catch (Exception ex)
            {
                logger.Warn("Detaching the chat channel failed.", ex);
            }

            themeBridge?.Dispose();
            themeBridge = null;
            channel.Dispose();
            DisposeWebView();
        }

        private void OnLoaded(object sender, RoutedEventArgs e)
        {
            ThreadHelper.ThrowIfNotOnUIThread();

            if (initializationStarted || disposed)
            {
                return;
            }

            initializationStarted = true;

            // The omp process is started by OnNavigationCompleted once the chat page has loaded (design: it starts when the
            // window opens, but there is no point in running an agent when the chat cannot be shown).
            StartInitialization();
        }

        private void StartInitialization()
        {
            if (initializing || disposed)
            {
                return;
            }

            initializing = true;
            var generation = ++initializationGeneration;
            _ = joinableTaskFactory.RunAsync(async () =>
            {
                try
                {
                    await joinableTaskFactory.SwitchToMainThreadAsync();
                    await InitializeWebViewAsync(generation);
                }
                catch (Exception ex)
                {
                    if (generation != initializationGeneration)
                    {
                        // Superseded by a recreated WebView2: its failure (usually "disposed") is not the user's problem.
                        logger.Debug("A superseded chat UI initialisation ended: " + ex.Message);
                    }
                    else
                    {
                        logger.Error("Initializing the chat UI failed.", ex);
                        ShowFallback(
                            "The chat UI could not start",
                            "An unexpected error occurred while starting the WebView2 chat UI.",
                            ex.Message,
                            showDownload: false);
                    }
                }
                finally
                {
                    if (generation == initializationGeneration)
                    {
                        initializing = false;
                    }
                }
            });
        }

        private async Task InitializeWebViewAsync(int generation)
        {
            bool Superseded() => disposed || generation != initializationGeneration;

            await joinableTaskFactory.SwitchToMainThreadAsync();
            if (Superseded())
            {
                return;
            }

            RemoveFallback();

            var environment = services.Environment;
            WebView2Locator.PrepareLoader(logger, environment.ExtensionDirectory);

            var probe = WebView2Locator.Probe();
            if (probe.Error != null)
            {
                logger.Error("WebView2 could not be initialised.", probe.Error);
                ShowFallback(
                    "WebView2 could not be loaded",
                    "The WebView2 loader failed to initialise (" + probe.Error.GetType().Name + ").",
                    probe.Error.Message,
                    showDownload: true);
                return;
            }

            if (!probe.RuntimeInstalled)
            {
                logger.Error("The WebView2 Runtime is not installed; the chat UI cannot start. Download: " + WebView2Locator.RuntimeDownloadUrl);
                ShowFallback(
                    "WebView2 Runtime is not installed",
                    "Pomp's chat runs in Microsoft Edge WebView2. Install the Evergreen WebView2 Runtime, then choose Retry.",
                    null,
                    showDownload: true);
                return;
            }

            logger.Info("WebView2 Runtime " + probe.RuntimeVersion + " found.");

            if (WebView2Locator.IsRuntimeTooOld(probe.RuntimeVersion))
            {
                logger.Error("The WebView2 Runtime " + probe.RuntimeVersion + " is older than the required " + WebView2Locator.MinimumRuntimeVersion
                             + "; the chat UI is not loaded. Download: " + WebView2Locator.RuntimeDownloadUrl);
                ShowFallback(
                    "The WebView2 Runtime is too old",
                    "Pomp's chat needs WebView2 Runtime " + WebView2Locator.MinimumRuntimeVersion + " or newer. Update the Evergreen WebView2 Runtime, then choose Retry.",
                    "Installed version: " + probe.RuntimeVersion,
                    showDownload: true);
                return;
            }

            var webUiFolder = Path.Combine(environment.ExtensionDirectory, "WebUI");
            var indexFile = Path.Combine(webUiFolder, "index.html");
            var userDataFolder = WebView2Locator.GetUserDataFolder(environment.DataDirectory, environment.VsMajorVersion);

            // File-system work off the UI thread (the data folder can sit on a redirected profile).
            await TaskScheduler.Default;
            var bundlePresent = File.Exists(indexFile);
            if (bundlePresent)
            {
                Directory.CreateDirectory(userDataFolder);
            }

            await joinableTaskFactory.SwitchToMainThreadAsync();
            if (Superseded())
            {
                return;
            }

            if (!bundlePresent)
            {
                logger.Error("The chat UI bundle is missing: " + indexFile);
                ShowFallback(
                    "The chat UI files are missing",
                    "The extension's WebUI folder does not contain index.html. Reinstalling the extension should fix this.",
                    webUiFolder,
                    showDownload: false);
                return;
            }

            var webViewEnvironment = await CoreWebView2Environment.CreateAsync(null, userDataFolder);
            if (Superseded())
            {
                return;
            }

            var view = new WebView2
            {
                DefaultBackgroundColor = ThemeBridge.GetBackgroundColor(),
                // A pane restored hidden (another tab, an auto-hide group) must not show its native window (OnIsVisibleChanged).
                Visibility = IsVisible ? Visibility.Visible : Visibility.Hidden,
            };
            webView = view;
            root.Children.Add(view);

            await view.EnsureCoreWebView2Async(webViewEnvironment);
            if (Superseded())
            {
                return;
            }

            var core = view.CoreWebView2;
            await ConfigureCoreAsync(core, webUiFolder, generation);
            if (Superseded())
            {
                return;
            }

            channel.Attach(core);
            if (themeBridge == null)
            {
                themeBridge = new ThemeBridge(channel, logger, joinableTaskFactory);
                themeBridge.Start();
            }

            // The current VS theme becomes a document-created script of this WebView2, so the first paint already has its colors.
            await themeBridge.AttachAsync(core);
            if (Superseded())
            {
                return;
            }

            // The agent is not started here: the page has not loaded yet. OnNavigationCompleted starts it once it did.
            core.Navigate(StartUrl);
            logger.Info("Chat UI navigating to " + StartUrl + " (user data: " + userDataFolder + ").");
        }

        private async Task ConfigureCoreAsync(CoreWebView2 core, string webUiFolder, int generation)
        {
            await joinableTaskFactory.SwitchToMainThreadAsync();

            var settings = core.Settings;
#if DEBUG
            const bool developerTools = true;
#else
            const bool developerTools = false;
#endif
            settings.AreDevToolsEnabled = developerTools;
            // On in every build, but OnContextMenuRequested leaves only the editing commands: the chat is a tool window, not a web
            // page (no back, reload, save, print or inspect; developer builds still open the developer tools with F12).
            settings.AreDefaultContextMenusEnabled = true;
            settings.AreBrowserAcceleratorKeysEnabled = developerTools;
            settings.IsStatusBarEnabled = false;
            settings.IsPasswordAutosaveEnabled = false;
            settings.IsGeneralAutofillEnabled = false;
            settings.IsSwipeNavigationEnabled = false;

            core.SetVirtualHostNameToFolderMapping(VirtualHost, webUiFolder, CoreWebView2HostResourceAccessKind.DenyCors);

            core.NavigationStarting += OnNavigationStarting;

            // Tagged with the initialisation generation this core belongs to: a late event of a core that was replaced since must not
            // start the agent (see OnNavigationCompleted).
            core.NavigationCompleted += (sender, e) => OnNavigationCompleted(generation, sender, e);
            core.FrameNavigationStarting += OnFrameNavigationStarting;
            core.NewWindowRequested += OnNewWindowRequested;
            core.PermissionRequested += OnPermissionRequested;
            core.DownloadStarting += OnDownloadStarting;
            core.ContextMenuRequested += OnContextMenuRequested;
            core.ProcessFailed += OnProcessFailed;

        }

        // ---- navigation policy -----------------------------------------------------------------------

        private static bool IsOwnOrigin(string? uriText)
        {
            return Uri.TryCreate(uriText, UriKind.Absolute, out var uri)
                   && uri.Scheme == Uri.UriSchemeHttps && uri.IsDefaultPort && string.IsNullOrEmpty(uri.UserInfo)
                   && string.Equals(uri.Host, VirtualHost, StringComparison.OrdinalIgnoreCase);
        }

        private void OnNavigationStarting(object? sender, CoreWebView2NavigationStartingEventArgs e)
        {
            try
            {
                ThreadHelper.ThrowIfNotOnUIThread();

                if (IsOwnOrigin(e.Uri))
                {
                    // A document of the chat page is loading (first load, reload after a renderer crash): remember which navigation
                    // it is (a failure of exactly this one replaces the page by the fallback) and expect ui/ready in time.
                    lastOwnNavigationId = e.NavigationId;
                    StartReadyWatchdog();
                    return;
                }

                e.Cancel = true;
                logger.Info("Blocked navigation to " + e.Uri);
                if (e.IsUserInitiated)
                {
                    OpenExternal(e.Uri);
                }
            }
            catch (Exception ex)
            {
                logger.Warn("Handling a navigation of the chat page failed.", ex);
            }
        }

        /// <summary>
        /// End of a navigation of the chat page. A successful load of the last own-origin navigation starts the omp session
        /// (<see cref="PompServices.StartSession"/> is idempotent while omp runs or after it failed, so a page reloaded after a renderer
        /// crash simply lands here again); a failed load (not a navigation cancelled by the policy above) shows the fallback panel.
        /// Events of a WebView2 that is no longer the current one are ignored.
        /// </summary>
        private void OnNavigationCompleted(int generation, object? sender, CoreWebView2NavigationCompletedEventArgs e)
        {
            try
            {
                ThreadHelper.ThrowIfNotOnUIThread();

                if (disposed || IsStaleCore(generation, sender) || e.NavigationId != lastOwnNavigationId)
                {
                    return;
                }

                if (e.IsSuccess)
                {
                    logger.Info("The chat page loaded (navigation " + e.NavigationId + ").");
                    services.StartSession();
                    return;
                }

                if (e.WebErrorStatus == CoreWebView2WebErrorStatus.OperationCanceled)
                {
                    return;
                }

                logger.Error("The chat page failed to load (" + e.WebErrorStatus + ").");
                ShowFallback(
                    "The chat page could not be loaded",
                    "Pomp could not load its chat UI. Choose Retry, or Show log for details.",
                    "Load status: " + e.WebErrorStatus,
                    showDownload: false);
            }
            catch (Exception ex)
            {
                logger.Warn("Handling the end of a chat page navigation failed.", ex);
            }
        }

        /// <summary>
        /// True when an event comes from a WebView2 that is not this control's current one any more: the initialisation it belongs to was
        /// superseded (recreated, Retry), or the WebView was disposed or replaced by the fallback panel since the event was queued.
        /// </summary>
        private bool IsStaleCore(int generation, object? sender)
        {
            var current = webView;
            return generation != initializationGeneration || current == null || !ReferenceEquals(current.CoreWebView2, sender);
        }

        private void OnFrameNavigationStarting(object? sender, CoreWebView2NavigationStartingEventArgs e)
        {
            try
            {
                if (!IsOwnOrigin(e.Uri) && !string.Equals(e.Uri, "about:blank", StringComparison.OrdinalIgnoreCase))
                {
                    e.Cancel = true;
                    logger.Info("Blocked frame navigation to " + e.Uri);
                }
            }
            catch (Exception ex)
            {
                logger.Warn("Handling a frame navigation of the chat page failed.", ex);
            }
        }

        private void OnNewWindowRequested(object? sender, CoreWebView2NewWindowRequestedEventArgs e)
        {
            try
            {
                e.Handled = true;
                if (e.IsUserInitiated)
                {
                    OpenExternal(e.Uri);
                }
            }
            catch (Exception ex)
            {
                logger.Warn("Handling a new-window request of the chat page failed.", ex);
            }
        }

        private void OnPermissionRequested(object? sender, CoreWebView2PermissionRequestedEventArgs e)
        {
            // The chat UI needs no camera, microphone, geolocation or notification access.
            e.State = CoreWebView2PermissionState.Deny;
        }

        private void OnDownloadStarting(object? sender, CoreWebView2DownloadStartingEventArgs e)
        {
            e.Cancel = true;
            logger.Info("Blocked a download requested by the chat page.");
        }

        /// <summary>The browser's own menu entries that make sense in a tool window: editing a text field or copying a selection.</summary>
        private static readonly HashSet<string> EditingMenuItems = new HashSet<string>(StringComparer.Ordinal)
        {
            "undo", "redo", "cut", "copy", "paste", "pasteAndMatchStyle", "selectAll",
        };

        private void OnContextMenuRequested(object? sender, CoreWebView2ContextMenuRequestedEventArgs e)
        {
            try
            {
                var items = e.MenuItems;
                for (var i = items.Count - 1; i >= 0; i--)
                {
                    if (items[i].Kind != CoreWebView2ContextMenuItemKind.Separator && !EditingMenuItems.Contains(items[i].Name))
                    {
                        items.RemoveAt(i);
                    }
                }

                // Separators left at either end or next to each other.
                for (var i = items.Count - 1; i >= 0; i--)
                {
                    if (items[i].Kind == CoreWebView2ContextMenuItemKind.Separator
                        && (i == 0 || i == items.Count - 1 || items[i - 1].Kind == CoreWebView2ContextMenuItemKind.Separator))
                    {
                        items.RemoveAt(i);
                    }
                }

                // Nothing to edit or copy here: no menu at all (the page shows its own where it has one).
                e.Handled = items.Count == 0;
            }
            catch (Exception ex)
            {
                e.Handled = true;
                logger.Warn("Filtering the chat page's context menu failed; the menu was suppressed.", ex);
            }
        }

        // ---- ui/ready watchdog -----------------------------------------------------------------------

        /// <summary>(Re)starts the wait for <c>ui/ready</c> of the document that is loading now; the fallback panel replaces the page when it never comes.</summary>
        private void StartReadyWatchdog()
        {
            ThreadHelper.ThrowIfNotOnUIThread();

            CancelReadyWatchdog();

            var source = new CancellationTokenSource();
            readyWatchdog = source;
            var token = source.Token;
            var watchedView = webView;

            _ = joinableTaskFactory.RunAsync(async () =>
            {
                try
                {
                    await Task.Delay(ReadyTimeout, token);
                    await joinableTaskFactory.SwitchToMainThreadAsync(token);

                    if (disposed || token.IsCancellationRequested || channel.IsReady || !ReferenceEquals(webView, watchedView))
                    {
                        return;
                    }

                    logger.Error("The chat page did not report ready within " + ReadyTimeout.TotalSeconds.ToString("0") + " seconds.");
                    ShowFallback(
                        "The chat UI did not start",
                        "The chat page did not become ready within " + ReadyTimeout.TotalSeconds.ToString("0") + " seconds. Choose Retry, or Show log for details.",
                        null,
                        showDownload: false);
                }
                catch (OperationCanceledException)
                {
                    // Ready arrived, the page was replaced or the control was disposed.
                }
                catch (Exception ex)
                {
                    logger.Warn("The chat page ready watchdog failed.", ex);
                }
            });
        }

        private void CancelReadyWatchdog()
        {
            // Cancelled but not disposed on purpose: the watchdog task may still be registering on the token.
            var source = readyWatchdog;
            readyWatchdog = null;
            if (source != null)
            {
                try
                {
                    source.Cancel();
                }
                catch (ObjectDisposedException)
                {
                }
            }
        }

        // ---- browser process failures ----------------------------------------------------------------

        private void OnProcessFailed(object? sender, CoreWebView2ProcessFailedEventArgs e)
        {
            try
            {
                ThreadHelper.ThrowIfNotOnUIThread();

                logger.Error($"WebView2 process failed: {e.ProcessFailedKind} (exit code {e.ExitCode}).");
                if (disposed)
                {
                    return;
                }

                switch (e.ProcessFailedKind)
                {
                    case CoreWebView2ProcessFailedKind.BrowserProcessExited:
                        // A long healthy stretch resets the count: only crashes in quick succession lead to the manual fallback.
                        if (pageReadyUtc != DateTime.MinValue && DateTime.UtcNow - pageReadyUtc >= StableSession)
                        {
                            browserRestarts = 0;
                        }

                        if (++browserRestarts <= MaxBrowserRestarts)
                        {
                            // Not from inside the event: recreating disposes the control that is raising it.
                            RunOnUiThreadAfterEvent("Recreating the WebView2", RecreateWebView);
                        }
                        else
                        {
                            ShowFallback(
                                "The WebView2 browser process keeps exiting",
                                "The chat UI stopped several times. Choose Retry to start it again.",
                                "Process failure: " + e.ProcessFailedKind,
                                showDownload: false);
                        }

                        break;

                    case CoreWebView2ProcessFailedKind.RenderProcessExited:
                        {
                            // The renderer is gone: reloading is the documented recovery, and there is nothing to wait for.
                            var core = sender as CoreWebView2;
                            RunOnUiThreadAfterEvent("Reloading the chat page after its renderer exited", () => core?.Reload());
                            break;
                        }

                    case CoreWebView2ProcessFailedKind.RenderProcessUnresponsive:
                        ScheduleUnresponsiveRecovery(sender as CoreWebView2);
                        break;
                }
            }
            catch (Exception ex)
            {
                logger.Warn("Handling a WebView2 process failure failed.", ex);
            }
        }

        /// <summary>Runs <paramref name="action"/> on the UI thread after the current WebView2 event has returned.</summary>
        private void RunOnUiThreadAfterEvent(string what, Action action)
        {
            _ = joinableTaskFactory.RunAsync(async () =>
            {
                try
                {
#if VS2017
                    // Microsoft.VisualStudio.Threading 15.8 (VS 2017) has no alwaysYield overload.
                    await System.Threading.Tasks.Task.Yield();
                    await joinableTaskFactory.SwitchToMainThreadAsync();
#else
                    await joinableTaskFactory.SwitchToMainThreadAsync(alwaysYield: true);
#endif
                    if (!disposed)
                    {
                        action();
                    }
                }
                catch (Exception ex)
                {
                    logger.Warn(what + " failed.", ex);
                }
            });
        }

        /// <summary>
        /// A renderer reported unresponsive may just be busy: wait a while, then probe it with a trivial script and reload the page only
        /// when it still does not answer.
        /// </summary>
        private void ScheduleUnresponsiveRecovery(CoreWebView2? core)
        {
            if (core == null || recoveryScheduled)
            {
                return;
            }

            recoveryScheduled = true;
            var watchedView = webView;
            _ = joinableTaskFactory.RunAsync(async () =>
            {
                try
                {
                    await Task.Delay(UnresponsiveGrace);
                    await joinableTaskFactory.SwitchToMainThreadAsync();

                    if (disposed || !ReferenceEquals(webView, watchedView))
                    {
                        return;
                    }

                    var probe = core.ExecuteScriptAsync("1");
                    if (await Task.WhenAny(probe, Task.Delay(UnresponsiveProbeTimeout)) == probe)
                    {
                        logger.Info("The chat page answers again after being reported unresponsive; not reloading it.");
                        return;
                    }

                    probe.Forget(logger, "Probing the unresponsive chat page");
                    logger.Warn("The chat page is still unresponsive " + UnresponsiveGrace.TotalSeconds.ToString("0") + " seconds after it was reported; reloading it.");
                    core.Reload();
                }
                catch (Exception ex)
                {
                    logger.Warn("Recovering the unresponsive chat page failed.", ex);
                }
                finally
                {
                    recoveryScheduled = false;
                }
            });
        }

        private void RecreateWebView()
        {
            ThreadHelper.ThrowIfNotOnUIThread();

            // An initialisation that is still in flight belongs to the WebView2 that is going away: invalidate it.
            initializationGeneration++;
            pageReadyUtc = DateTime.MinValue;
            DisposeWebView();
            initializing = false;
            StartInitialization();
        }

        private static void OpenExternal(string? uriText)
        {
            if (!Uri.TryCreate(uriText, UriKind.Absolute, out var uri)
                || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
            {
                return;
            }

            try
            {
                Process.Start(new ProcessStartInfo(uri.AbsoluteUri) { UseShellExecute = true })?.Dispose();
            }
            catch (Exception)
            {
                // No handler registered / blocked by policy: nothing sensible to do for a link click.
            }
        }

        private void OnToggleFocusRequested(object? sender, EventArgs e)
        {
            _ = joinableTaskFactory.RunAsync(async () =>
            {
                try
                {
                    await services.Commands.ToggleFocusAsync();
                }
                catch (Exception ex)
                {
                    logger.Warn("Toggling focus from the chat page failed.", ex);
                }
            });
        }

        private void OnPasteFilesRequested(object? sender, EventArgs e)
        {
            _ = joinableTaskFactory.RunAsync(async () =>
            {
                try
                {
                    await services.Commands.PasteIntoBasketAsync();
                }
                catch (Exception ex)
                {
                    logger.Warn("Pasting files into the chat input failed.", ex);
                }
            });
        }

        private void OnFilesDropped(object? sender, IReadOnlyList<string> paths)
        {
            _ = joinableTaskFactory.RunAsync(async () =>
            {
                try
                {
                    await services.Commands.DropIntoBasketAsync(paths);
                }
                catch (Exception ex)
                {
                    logger.Warn("Adding the files dropped on the chat page failed.", ex);
                }
            });
        }

        /// <summary>Longest page notice shown in the status bar (one line; the log keeps the same text).</summary>
        private const int MaxStatusChars = 300;
        private string? lastStatus;

        /// <summary>
        /// A notice of the page (a control action that failed, a warning about the draft) goes to the VS status bar, where VS shows its
        /// own messages, instead of text squeezed between the chat's controls. Errors and warnings also go to the POMP log.
        /// </summary>
        private void OnStatusRequested(object? sender, UiMessage message)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            var payload = message.Payload;
            if (payload?["text"]?.Type != JTokenType.String) return;
            var level = payload["level"]?.Type == JTokenType.String ? (string?)payload["level"] : null;
            ShowStatus((string)payload["text"]!, level);
        }

        private void OnLiveFailed(object? sender, Exception ex)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            ShowStatus(Pomp.Hosting.PompLanguage.Pick("Live 변경 표시를 갱신하지 못했어요: ", "Could not update the Live change display: ") + ex.Message, null);
        }

        /// <summary>One line, no control characters, at most <see cref="MaxStatusChars"/>; the log gets an error or warning only when it changed.</summary>
        private void ShowStatus(string raw, string? level)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            var chars = raw.ToCharArray();
            for (var i = 0; i < chars.Length; i++) if (char.IsControl(chars[i]) || chars[i] == '\u2028' || chars[i] == '\u2029') chars[i] = ' ';
            var text = string.Join(" ", new string(chars).Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries));
            if (text.Length == 0) return;
            if (text.Length > MaxStatusChars)
            {
                var cut = MaxStatusChars;
                if (char.IsHighSurrogate(text[cut - 1])) cut--;
                text = text.Substring(0, cut);
            }
            if (text != lastStatus)
            {
                lastStatus = text;
                if (level == "error") logger.Warn("Chat page: " + text);
                else if (level == "warning") logger.Info("Chat page: " + text);
            }
            try
            {
                if (Package.GetGlobalService(typeof(Microsoft.VisualStudio.Shell.Interop.SVsStatusbar)) is Microsoft.VisualStudio.Shell.Interop.IVsStatusbar statusbar)
                    statusbar.SetText("POMP: " + text);
            }
            catch (Exception ex)
            {
                logger.Warn("Showing a chat page notice in the status bar failed.", ex);
            }
        }

        // ---- fallback panel --------------------------------------------------------------------------

        /// <summary>
        /// Replaces the chat page by the explanation panel (Retry / Show log, plus the runtime download link when relevant).
        /// Always yields to the UI thread first: it is called from inside WebView2 events, and the panel disposes the WebView2.
        /// </summary>
        private void ShowFallback(string title, string message, string? details, bool showDownload)
        {
            _ = joinableTaskFactory.RunAsync(async () =>
            {
                try
                {
#if VS2017
                    // Microsoft.VisualStudio.Threading 15.8 (VS 2017) has no alwaysYield overload.
                    await System.Threading.Tasks.Task.Yield();
                    await joinableTaskFactory.SwitchToMainThreadAsync();
#else
                    await joinableTaskFactory.SwitchToMainThreadAsync(alwaysYield: true);
#endif
                    if (disposed)
                    {
                        return;
                    }

                    RemoveFallback();
                    DisposeWebView();
                    BuildFallbackPanel(title, message, details, showDownload);
                }
                catch (Exception ex)
                {
                    logger.Warn("Showing the chat fallback panel failed.", ex);
                }
            });
        }

        private void BuildFallbackPanel(string title, string message, string? details, bool showDownload)
        {
            ThreadHelper.ThrowIfNotOnUIThread();

            var panel = new StackPanel
            {
                Margin = new Thickness(16),
                MaxWidth = 480,
                VerticalAlignment = VerticalAlignment.Center,
                HorizontalAlignment = HorizontalAlignment.Center,
            };

            var heading = new TextBlock { Text = title, FontSize = 15, FontWeight = FontWeights.SemiBold, TextWrapping = TextWrapping.Wrap };
            heading.SetResourceReference(TextBlock.ForegroundProperty, EnvironmentColors.ToolWindowTextBrushKey);
            panel.Children.Add(heading);

            var body = new TextBlock { Text = message, Margin = new Thickness(0, 8, 0, 0), TextWrapping = TextWrapping.Wrap };
            body.SetResourceReference(TextBlock.ForegroundProperty, EnvironmentColors.ToolWindowTextBrushKey);
            panel.Children.Add(body);

            if (!string.IsNullOrWhiteSpace(details))
            {
                var detail = new TextBlock { Text = details, Margin = new Thickness(0, 8, 0, 0), TextWrapping = TextWrapping.Wrap, Opacity = 0.7 };
                detail.SetResourceReference(TextBlock.ForegroundProperty, EnvironmentColors.ToolWindowTextBrushKey);
                panel.Children.Add(detail);
            }

            var links = new TextBlock { Margin = new Thickness(0, 12, 0, 0), TextWrapping = TextWrapping.Wrap };
            if (showDownload)
            {
                links.Inlines.Add(CreateLink("Download the WebView2 Runtime", () => OpenExternal(WebView2Locator.RuntimeDownloadUrl)));
                links.Inlines.Add(new Run("   "));
            }

            links.Inlines.Add(CreateLink("Retry", () =>
            {
                RemoveFallback();
                StartInitialization();
            }));
            links.Inlines.Add(new Run("   "));
            links.Inlines.Add(CreateLink("Show log", () => services.Environment.ShowLog()));
            panel.Children.Add(links);

            fallbackPanel = panel;
            root.Children.Add(panel);
        }

        private static Hyperlink CreateLink(string text, Action onClick)
        {
            var link = new Hyperlink(new Run(text));
            link.SetResourceReference(TextElement.ForegroundProperty, EnvironmentColors.PanelHyperlinkBrushKey);
            link.Click += (sender, args) => onClick();
            return link;
        }

        private void RemoveFallback()
        {
            if (fallbackPanel != null)
            {
                root.Children.Remove(fallbackPanel);
                fallbackPanel = null;
            }
        }

        private void DisposeWebView()
        {
            CancelReadyWatchdog();
            channel.Detach();
            themeBridge?.Detach();
            lastOwnNavigationId = 0;
            var view = webView;
            webView = null;
            if (view == null)
            {
                return;
            }

            try
            {
                root.Children.Remove(view);
                view.Dispose();
            }
            catch (Exception ex)
            {
                logger.Warn("Disposing the WebView2 control failed.", ex);
            }
        }
    }
}
