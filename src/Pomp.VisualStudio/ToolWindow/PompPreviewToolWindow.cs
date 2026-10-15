using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading.Tasks;
using Task = System.Threading.Tasks.Task;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Threading;
using Microsoft.VisualStudio.PlatformUI;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Threading;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.Wpf;
using Pomp.Diagnostics;
using Pomp.Features;
using Pomp.Hosting;
using Pomp.VisualStudio.Logging;

namespace Pomp.VisualStudio.ToolWindow
{
    /// <summary>
    /// The script-free preview of one workspace .svg/.html file (docs/ui-protocol.md, Preview). The page's WebView2 runs no script,
    /// talks to nothing (no web messages, host objects, downloads, new windows, permissions) and loads only the one file, which the
    /// host serves itself with a CSP; every other request and navigation is refused. One tab per file.
    /// </summary>
    [Guid("6A0F3C51-2E7B-4D8A-9C1F-5B4E8D2A7F60")]
    public sealed class PompPreviewToolWindow : ToolWindowPane
    {
        // Never resolved by DNS: every request to it is answered by WebResourceRequested, and anything else is refused.
        private const string Origin = "https://pomp-preview.invalid";
        private const string Csp = "default-src 'none'; style-src 'unsafe-inline'; img-src data:; font-src data:; script-src 'none'; "
                                 + "object-src 'none'; frame-src 'none'; form-action 'none'; base-uri 'none'";
        private static readonly int[] Zooms = { 50, 75, 100, 125, 150, 200 };

        private readonly Grid host = new Grid();
        private readonly TextBlock pathText = new TextBlock { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 12, 0), TextTrimming = TextTrimming.CharacterEllipsis };
        private readonly TextBlock errorText = new TextBlock { Margin = new Thickness(8, 4, 8, 4), TextWrapping = TextWrapping.Wrap, Visibility = Visibility.Collapsed };
        // Index 0 light, 1 dark / 0 full width, 1 the 390 px phone width.
        private readonly ComboBox themeBox = Choice(PompLanguage.Pick("테마", "Theme"), PompLanguage.Pick("라이트", "Light"), PompLanguage.Pick("다크", "Dark"));
        private readonly ComboBox widthBox = Choice(PompLanguage.Pick("너비", "Width"), PompLanguage.Pick("전체", "Full"), "390");
        private readonly ComboBox zoomBox = Choice(PompLanguage.Pick("확대", "Zoom"), Array.ConvertAll(Zooms, zoom => zoom + "%"));
        private WebView2? view;
        private JoinableTask? creating;
        private FileSystemWatcher? watcher;
        private DispatcherTimer? redrawTimer;
        private VsPompLogger? logger;
        private AsyncPackage? package;
        private string file = string.Empty;
        private string root = string.Empty;
        private string documentUrl = string.Empty;
        private bool dark;
        private int version;

        public PompPreviewToolWindow() : base(null)
        {
            Caption = PompLanguage.Pick("미리보기", "Preview");
            var panel = new DockPanel();
            panel.SetResourceReference(Panel.BackgroundProperty, VsBrushes.ToolWindowBackgroundKey);
            var bar = new WrapPanel { Margin = new Thickness(6, 4, 6, 4) };
            pathText.SetResourceReference(TextBlock.ForegroundProperty, VsBrushes.ToolWindowTextKey);
            errorText.SetResourceReference(TextBlock.ForegroundProperty, VsBrushes.ToolWindowTextKey);
            bar.Children.Add(pathText);
            themeBox.SelectionChanged += (_, __) => { ThreadHelper.ThrowIfNotOnUIThread(); SetTheme(themeBox.SelectedIndex == 1); };
            widthBox.SelectionChanged += (_, __) => SetNarrow(widthBox.SelectedIndex == 1);
            bar.Children.Add(themeBox);
            bar.Children.Add(widthBox);
            zoomBox.SelectedIndex = Array.IndexOf(Zooms, 100);
            zoomBox.SelectionChanged += (_, __) => { if (view != null && zoomBox.SelectedIndex >= 0) view.ZoomFactor = Zooms[zoomBox.SelectedIndex] / 100.0; };
            bar.Children.Add(zoomBox);
            bar.Children.Add(Button(PompLanguage.Pick("다시 그리기", "Redraw"), Redraw));
            bar.Children.Add(Button(PompLanguage.Pick("VS에서 열기", "Open in VS"), OpenInEditor));
            DockPanel.SetDock(bar, Dock.Top);
            DockPanel.SetDock(errorText, Dock.Top);
            panel.Children.Add(bar);
            panel.Children.Add(errorText);
            panel.Children.Add(host);
            Content = panel;
            VSColorTheme.ThemeChanged += OnThemeChanged;
        }

        /// <summary>The file this tab shows; empty until <see cref="ShowAsync"/>.</summary>
        internal string File => file;

        /// <summary>Shows <paramref name="fullPath"/> (checked by <see cref="FilePreview.Resolve"/>); a second call for the same file redraws.</summary>
        internal async Task ShowAsync(AsyncPackage owner, VsPompLogger log, string dataDirectory, string fullPath, string workspaceRoot)
        {
            await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
            package = owner;
            logger = log;
            if (view != null && string.Equals(file, fullPath, StringComparison.OrdinalIgnoreCase))
            {
                Redraw();
                return;
            }

            file = fullPath;
            root = workspaceRoot;
            Caption = PompLanguage.Pick("미리보기: ", "Preview: ") + Path.GetFileName(fullPath);
            pathText.Text = RelativePath(workspaceRoot, fullPath);
            pathText.ToolTip = fullPath;
            dark = IsDark(ThemeBridge.GetBackgroundColor());
            themeBox.SelectedIndex = dark ? 1 : 0;
            widthBox.SelectedIndex = 0;
            documentUrl = Origin + "/" + Guid.NewGuid().ToString("N") + "/" + Uri.EscapeDataString(Path.GetFileName(fullPath));
            if (view == null)
            {
                // A second open while the view is still being made waits for that one instead of making another.
                creating ??= owner.JoinableTaskFactory.RunAsync(() => CreateViewAsync(dataDirectory));
                await creating;
            }

            Watch(owner.JoinableTaskFactory);
            Redraw();
        }

        private async Task CreateViewAsync(string dataDirectory)
        {
            await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
            // A data folder of its own: the preview never shares cookies, storage or a browser process with the chat page.
            var folder = Path.Combine(dataDirectory, "WebView2", "Preview");
            Directory.CreateDirectory(folder);
            var environment = await CoreWebView2Environment.CreateAsync(null, folder);
            var created = new WebView2 { DefaultBackgroundColor = ThemeBridge.GetBackgroundColor() };
            host.Children.Add(created);
            await created.EnsureCoreWebView2Async(environment);
            var core = created.CoreWebView2;
            var settings = core.Settings;
            settings.IsScriptEnabled = false;
            settings.IsWebMessageEnabled = false;
            settings.AreHostObjectsAllowed = false;
            settings.AreDevToolsEnabled = false;
            settings.AreDefaultContextMenusEnabled = false;
            settings.AreDefaultScriptDialogsEnabled = false;
            settings.AreBrowserAcceleratorKeysEnabled = false;
            settings.IsStatusBarEnabled = false;
            settings.IsZoomControlEnabled = false;
            settings.IsPasswordAutosaveEnabled = false;
            settings.IsGeneralAutofillEnabled = false;
            settings.IsSwipeNavigationEnabled = false;
            core.AddWebResourceRequestedFilter("*", CoreWebView2WebResourceContext.All);
            core.WebResourceRequested += OnResourceRequested;
            core.NavigationStarting += (_, e) => { if (!IsDocument(e.Uri)) e.Cancel = true; };
            core.FrameNavigationStarting += (_, e) => e.Cancel = true;
            core.NewWindowRequested += (_, e) => e.Handled = true;
            core.DownloadStarting += (_, e) => { e.Cancel = true; e.Handled = true; };
            core.PermissionRequested += (_, e) => e.State = CoreWebView2PermissionState.Deny;
            core.NavigationCompleted += (_, e) =>
            {
                // A refusal the file request already showed (missing, too large, outside the workspace) is more precise than this.
                if (!e.IsSuccess && errorText.Visibility != Visibility.Visible) ShowError(PompLanguage.Pick("파일을 그리지 못했어요.", "The file could not be drawn."));
            };
            view = created;
            SetNarrow(widthBox.SelectedIndex == 1);
            if (zoomBox.SelectedIndex >= 0) created.ZoomFactor = Zooms[zoomBox.SelectedIndex] / 100.0;
        }

        private bool IsDocument(string uri) =>
            documentUrl.Length > 0 && uri.StartsWith(documentUrl, StringComparison.Ordinal) && (uri.Length == documentUrl.Length || uri[documentUrl.Length] == '?');

        private void OnResourceRequested(object sender, CoreWebView2WebResourceRequestedEventArgs e)
        {
            var environment = view?.CoreWebView2?.Environment;
            if (environment == null) return;
            if (!IsDocument(e.Request.Uri))
            {
                // Images, styles, fonts and anything else the file points at: nothing outside the file is loaded.
                e.Response = environment.CreateWebResourceResponse(null, 403, "Forbidden", "Content-Type: text/plain");
                return;
            }

            try
            {
                // Checked again on every draw: the file may have grown, moved behind a link or left the workspace since the tab opened.
                var checkedFile = FilePreview.Resolve(root, file);
                var text = Encoding.UTF8.GetString(System.IO.File.ReadAllBytes(checkedFile));
                var svg = string.Equals(Path.GetExtension(checkedFile), ".svg", StringComparison.OrdinalIgnoreCase);
                var body = new MemoryStream(Encoding.UTF8.GetBytes(FilePreview.WithTheme(text, svg ? "svg" : "html", dark ? "dark" : "light")));
                var headers = "Content-Type: " + (svg ? "image/svg+xml" : "text/html") + "; charset=utf-8\r\n"
                            + "Content-Security-Policy: " + Csp + "\r\n"
                            + "X-Content-Type-Options: nosniff\r\nCache-Control: no-store\r\nReferrer-Policy: no-referrer";
                e.Response = environment.CreateWebResourceResponse(body, 200, "OK", headers);
                HideError();
            }
            catch (Exception ex) when (ex is InvalidOperationException || ex is IOException || ex is UnauthorizedAccessException)
            {
                ShowError(ex is InvalidOperationException ? ex.Message : PompLanguage.Pick("파일을 읽을 수 없어요.", "The file cannot be read."));
                e.Response = environment.CreateWebResourceResponse(null, 404, "Not Found", "Content-Type: text/plain");
            }
        }

        private void Redraw()
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            var core = view?.CoreWebView2;
            if (core == null || documentUrl.Length == 0) return;
            core.Profile.PreferredColorScheme = dark ? CoreWebView2PreferredColorScheme.Dark : CoreWebView2PreferredColorScheme.Light;
            core.Navigate(documentUrl + "?v=" + (++version));
        }

        private void SetTheme(bool toDark)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            if (dark == toDark) return;
            dark = toDark;
            Redraw();
        }

        // A VS theme change carries over: the empty area takes the new background and the file is drawn in the matching theme again.
        private void OnThemeChanged(ThemeChangedEventArgs e)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            var background = ThemeBridge.GetBackgroundColor();
            if (view != null) view.DefaultBackgroundColor = background;
            themeBox.SelectedIndex = IsDark(background) ? 1 : 0;
        }

        private void SetNarrow(bool narrow)
        {
            if (view == null) return;
            view.Width = narrow ? 390 : double.NaN;
            view.HorizontalAlignment = narrow ? HorizontalAlignment.Center : HorizontalAlignment.Stretch;
        }

        private void OpenInEditor()
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            if (package == null || file.Length == 0) return;
            try { VsShellUtilities.OpenDocument(package, file); }
            catch (Exception ex) { logger?.Warn("Opening the previewed file in the editor failed: " + ex.Message); }
        }

        private void Watch(JoinableTaskFactory tasks)
        {
            watcher?.Dispose();
            watcher = null;
            if (redrawTimer == null)
            {
                redrawTimer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(300) };
                redrawTimer.Tick += (_, __) => { ThreadHelper.ThrowIfNotOnUIThread(); redrawTimer.Stop(); Redraw(); };
            }

            try
            {
                var created = new FileSystemWatcher(Path.GetDirectoryName(file)!, Path.GetFileName(file))
                {
                    NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.Size | NotifyFilters.FileName,
                };
                void Changed() => _ = tasks.RunAsync(async () =>
                {
                    await tasks.SwitchToMainThreadAsync();
                    redrawTimer?.Stop();
                    redrawTimer?.Start();
                });
                created.Changed += (_, __) => Changed();
                created.Created += (_, __) => Changed();
                created.Renamed += (_, __) => Changed();
                created.EnableRaisingEvents = true;
                watcher = created;
            }
            catch (Exception ex) when (ex is ArgumentException || ex is IOException)
            {
                logger?.Warn("The preview cannot follow changes of " + Path.GetFileName(file) + ": " + ex.Message);
            }
        }

        private void ShowError(string message)
        {
            errorText.Text = message;
            errorText.Visibility = Visibility.Visible;
        }

        private void HideError() => errorText.Visibility = Visibility.Collapsed;

        private static string RelativePath(string workspace, string path)
        {
            var prefix = workspace.TrimEnd('\\', '/') + "\\";
            return path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) ? path.Substring(prefix.Length).Replace('\\', '/') : path;
        }

        private static bool IsDark(System.Drawing.Color color) => (0.299 * color.R + 0.587 * color.G + 0.114 * color.B) < 128;

        /// <summary>A drop-down of fixed choices in the VS theme; <paramref name="name"/> is read only by screen readers.</summary>
        private static ComboBox Choice(string name, params string[] items)
        {
            var box = new ComboBox { MinWidth = 72, Margin = new Thickness(2) };
            box.SetResourceReference(FrameworkElement.StyleProperty, VsResourceKeys.ComboBoxStyleKey);
            AutomationProperties.SetName(box, name);
            foreach (var item in items) box.Items.Add(item);
            return box;
        }

        private static Button Button(string text, Action action)
        {
            var button = new Button { Content = text, Margin = new Thickness(2), Padding = new Thickness(8, 2, 8, 2) };
            button.SetResourceReference(FrameworkElement.StyleProperty, VsResourceKeys.ButtonStyleKey);
            button.Click += (_, __) => action();
            return button;
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                VSColorTheme.ThemeChanged -= OnThemeChanged;
                watcher?.Dispose();
                watcher = null;
                redrawTimer?.Stop();
                view?.Dispose();
                view = null;
            }

            base.Dispose(disposing);
        }
    }
}
