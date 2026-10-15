using System;
using System.Collections.Generic;
using System.Globalization;
using System.Threading;
using System.Windows;
using System.Windows.Media;
using Microsoft.VisualStudio.PlatformUI;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Threading;
using Microsoft.Web.WebView2.Core;
using Pomp.Diagnostics;
using Pomp.Json;
using Pomp.Ui;
using Pomp.VisualStudio.Interop;
using DrawingColor = System.Drawing.Color;

namespace Pomp.VisualStudio.ToolWindow
{
    /// <summary>
    /// Maps the Visual Studio color theme to the CSS custom properties of docs/ui-protocol.md and posts
    /// <c>theme/changed</c> when the page is ready and whenever the VS theme changes. UI thread only.
    /// <para>
    /// The page only learns the theme after it announced <c>ui/ready</c>, which is after its first paint. To avoid a flash of the
    /// page's dark defaults under a light theme, the current theme is also registered as a "document created" script of the
    /// WebView2 (<see cref="AttachAsync"/>, before the first navigation) and replaced whenever the VS theme changes.
    /// </para>
    /// </summary>
    internal sealed class ThemeBridge : IDisposable
    {
        private readonly WebViewUiChannel channel;
        private readonly IPompLogger logger;
        private readonly JoinableTaskFactory joinableTaskFactory;
        private readonly SemaphoreSlim scriptGate = new SemaphoreSlim(1, 1);
        private CoreWebView2? core;
        private string? initialScriptId;
        private bool started;
        private bool disposed;

        public ThemeBridge(WebViewUiChannel channel, IPompLogger logger, JoinableTaskFactory joinableTaskFactory)
        {
            this.channel = channel;
            this.logger = logger;
            this.joinableTaskFactory = joinableTaskFactory;
        }

        /// <summary>Subscribes to page-ready and VS theme changes.</summary>
        public void Start()
        {
            ThreadHelper.ThrowIfNotOnUIThread();

            if (started || disposed)
            {
                return;
            }

            started = true;
            channel.PageReady += OnPageReady;
            VSColorTheme.ThemeChanged += OnThemeChanged;
        }

        /// <summary>
        /// Binds the bridge to a new WebView2 and registers the current theme as its initial-theme script. Call it after the core was
        /// initialised and BEFORE the first navigation, so the very first document already carries the VS colors. A failure only
        /// costs the early theme (the page falls back to <c>theme/changed</c>), so it is logged and never thrown.
        /// </summary>
        public async System.Threading.Tasks.Task AttachAsync(CoreWebView2 webView)
        {
            await joinableTaskFactory.SwitchToMainThreadAsync();

            core = webView;
            initialScriptId = null;
            await RefreshInitialScriptAsync();
        }

        /// <summary>Forgets the WebView2 (it is being disposed); its scripts go away with it.</summary>
        public void Detach()
        {
            core = null;
            initialScriptId = null;
        }

        public void Dispose()
        {
            if (disposed)
            {
                return;
            }

            disposed = true;
            core = null;
            channel.PageReady -= OnPageReady;
            VSColorTheme.ThemeChanged -= OnThemeChanged;
        }

        /// <summary>(Re)registers the initial-theme script on the current WebView2 with the current VS colors, then removes the previous one.</summary>
        private async System.Threading.Tasks.Task RefreshInitialScriptAsync()
        {
            try
            {
                await joinableTaskFactory.SwitchToMainThreadAsync();
                await scriptGate.WaitAsync();
                try
                {
                    var webView = core;
                    if (webView == null || disposed)
                    {
                        return;
                    }

                    var script = BuildInitialThemeScript(BuildPayload());
                    var previous = initialScriptId;
                    var id = await webView.AddScriptToExecuteOnDocumentCreatedAsync(script);
                    if (!ReferenceEquals(core, webView))
                    {
                        // The WebView2 was replaced or disposed while the script was being added: nothing to keep.
                        return;
                    }

                    initialScriptId = id;
                    if (previous != null)
                    {
                        webView.RemoveScriptToExecuteOnDocumentCreated(previous);
                    }
                }
                finally
                {
                    scriptGate.Release();
                }
            }
            catch (Exception ex)
            {
                logger.Warn("Registering the initial theme script failed; the page themes itself after ui/ready.", ex);
            }
        }

        /// <summary>
        /// Script for <c>AddScriptToExecuteOnDocumentCreatedAsync</c>: sets the theme variables, <c>data-theme</c> and
        /// <c>color-scheme</c> on the root element as soon as it exists (the script runs before the document has a root element,
        /// so a MutationObserver waits for it), i.e. before the first paint. Same variables the page applies for <c>theme/changed</c>.
        /// </summary>
        internal static string BuildInitialThemeScript(ThemePayload payload)
        {
            var json = PompJson.Serialize(payload);
            return "(function(t){"
                   + "function a(){var r=document.documentElement;if(!r){return false;}"
                   + "var s=r.style,c=t.colors||{};"
                   + "for(var k in c){if(Object.prototype.hasOwnProperty.call(c,k)&&k.indexOf('--')===0&&typeof c[k]==='string'){s.setProperty(k,c[k]);}}"
                   + "var kind=t.kind==='light'?'light':'dark';"
                   + "r.setAttribute('data-theme',kind);s.colorScheme=kind;"
                   + "if(t.fontFamily){s.setProperty('--pomp-font-family',t.fontFamily);}"
                   + "if(t.fontSizePx>0){s.setProperty('--pomp-font-size',Math.min(Math.max(t.fontSizePx,10),24)+'px');}"
                   + "return true;}"
                   + "if(!a()){var o=new MutationObserver(function(){if(a()){o.disconnect();}});o.observe(document,{childList:true});}"
                   + "})(" + json + ");";
        }

        /// <summary>Tool window background, used as the WebView2 default background to avoid a white flash.</summary>
        public static DrawingColor GetBackgroundColor()
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            return VSColorTheme.GetThemedColor(EnvironmentColors.ToolWindowBackgroundColorKey);
        }

        /// <summary>Builds the payload of <c>theme/changed</c> from the current VS theme.</summary>
        public static ThemePayload BuildPayload()
        {
            ThreadHelper.ThrowIfNotOnUIThread();

            var bg = VSColorTheme.GetThemedColor(EnvironmentColors.ToolWindowBackgroundColorKey);
            var fg = VSColorTheme.GetThemedColor(EnvironmentColors.ToolWindowTextColorKey);
            var dark = Luminance(bg) < 0.5;

            var border = VSColorTheme.GetThemedColor(EnvironmentColors.ToolWindowBorderColorKey);
            var inputBg = VSColorTheme.GetThemedColor(CommonControlsColors.TextBoxBackgroundColorKey);
            var inputFg = VSColorTheme.GetThemedColor(CommonControlsColors.TextBoxTextColorKey);
            var inputBorder = VSColorTheme.GetThemedColor(CommonControlsColors.TextBoxBorderColorKey);
            var focusBorder = VSColorTheme.GetThemedColor(CommonControlsColors.TextBoxBorderFocusedColorKey);
            var accent = VSColorTheme.GetThemedColor(EnvironmentColors.FileTabSelectedBackgroundColorKey);
            var accentText = VSColorTheme.GetThemedColor(EnvironmentColors.FileTabSelectedTextColorKey);
            var secondaryBg = VSColorTheme.GetThemedColor(CommonControlsColors.ButtonColorKey);
            var secondaryFg = VSColorTheme.GetThemedColor(CommonControlsColors.ButtonTextColorKey);
            var link = VSColorTheme.GetThemedColor(EnvironmentColors.PanelHyperlinkColorKey);
            var scrollbar = VSColorTheme.GetThemedColor(EnvironmentColors.ScrollBarThumbBackgroundColorKey);

            // Semantic colors follow the dark/light kind rather than the (theme specific) shell palette.
            var error = dark ? Rgb(0xf4, 0x87, 0x71) : Rgb(0xe5, 0x14, 0x00);
            var warning = dark ? Rgb(0xcc, 0xa7, 0x00) : Rgb(0xbf, 0x88, 0x03);
            var success = dark ? Rgb(0x89, 0xd1, 0x85) : Rgb(0x38, 0x8a, 0x34);
            var diffAdd = dark ? Rgba(0x2e, 0xa0, 0x43, 0.25) : Rgba(0x2e, 0xa0, 0x43, 0.18);
            var diffDel = dark ? Rgba(0xf8, 0x51, 0x49, 0.25) : Rgba(0xf8, 0x51, 0x49, 0.16);

            var colors = new Dictionary<string, string>
            {
                ["--pomp-bg"] = Css(bg),
                ["--pomp-fg"] = Css(fg),
                ["--pomp-muted-fg"] = Css(Blend(fg, bg, 0.40)),
                ["--pomp-border"] = Css(border),
                ["--pomp-panel-bg"] = Css(Blend(bg, fg, dark ? 0.05 : 0.04)),
                ["--pomp-input-bg"] = Css(inputBg),
                ["--pomp-input-fg"] = Css(inputFg),
                ["--pomp-input-border"] = Css(inputBorder),
                ["--pomp-focus-border"] = Css(focusBorder),
                ["--pomp-button-bg"] = Css(accent),
                ["--pomp-button-fg"] = Css(accentText),
                ["--pomp-button-hover-bg"] = Css(Blend(accent, dark ? Rgb(255, 255, 255) : Rgb(0, 0, 0), 0.15)),
                ["--pomp-button-secondary-bg"] = Css(secondaryBg),
                ["--pomp-button-secondary-fg"] = Css(secondaryFg),
                ["--pomp-link"] = Css(link),
                ["--pomp-accent"] = Css(accent),
                ["--pomp-error"] = Css(error),
                ["--pomp-warning"] = Css(warning),
                ["--pomp-success"] = Css(success),
                ["--pomp-code-bg"] = Css(Blend(bg, fg, dark ? 0.07 : 0.05)),
                ["--pomp-selection-bg"] = Css(WithAlpha(accent, 0.40)),
                ["--pomp-scrollbar"] = Css(WithAlpha(scrollbar, 0.70)),
                ["--pomp-diff-add-bg"] = Css(diffAdd),
                ["--pomp-diff-del-bg"] = Css(diffDel),
            };

            var payload = new ThemePayload { Kind = dark ? "dark" : "light", Colors = colors };
            ReadEnvironmentFont(payload);
            return payload;
        }

        private static void ReadEnvironmentFont(ThemePayload payload)
        {
            try
            {
                var application = Application.Current;
                if (application == null)
                {
                    return;
                }

                if (application.TryFindResource(VsFonts.EnvironmentFontFamilyKey) is FontFamily family && !string.IsNullOrEmpty(family.Source))
                {
                    payload.FontFamily = "\"" + family.Source + "\", \"Segoe UI\", system-ui, sans-serif";
                }

                if (application.TryFindResource(VsFonts.EnvironmentFontSizeKey) is double size && size > 0)
                {
                    payload.FontSizePx = Math.Round(size, 2);
                }
            }
            catch (Exception)
            {
                // Fonts are cosmetic; the page has its own defaults.
            }
        }

        private void OnPageReady(object? sender, EventArgs e)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            PostTheme();
        }

        private void OnThemeChanged(ThemeChangedEventArgs e)
        {
            ThreadHelper.ThrowIfNotOnUIThread();

            // The next document to load (a reload, a recreated WebView) must start with the new colors too.
            _ = joinableTaskFactory.RunAsync(RefreshInitialScriptAsync);

            if (channel.IsReady)
            {
                PostTheme();
            }
        }

        private void PostTheme()
        {
            ThreadHelper.ThrowIfNotOnUIThread();

            try
            {
                var message = new UiMessage(UiMessageTypes.ThemeChanged, BuildPayload());
                channel.PostAsync(message).Forget(logger, "Posting theme/changed");
            }
            catch (Exception ex)
            {
                logger.Warn("Could not build or post the theme.", ex);
            }
        }

        // ---- color math ------------------------------------------------------------------------------

        private static DrawingColor Rgb(int r, int g, int b) => DrawingColor.FromArgb(255, r, g, b);

        private static DrawingColor Rgba(int r, int g, int b, double alpha) => DrawingColor.FromArgb((int)Math.Round(alpha * 255), r, g, b);

        private static DrawingColor WithAlpha(DrawingColor color, double alpha) => DrawingColor.FromArgb((int)Math.Round(alpha * 255), color.R, color.G, color.B);

        /// <summary>Linear mix: 0 = <paramref name="from"/>, 1 = <paramref name="to"/> (opaque result).</summary>
        private static DrawingColor Blend(DrawingColor from, DrawingColor to, double amount)
        {
            int Mix(int a, int b) => (int)Math.Round(a + ((b - a) * amount));
            return DrawingColor.FromArgb(255, Mix(from.R, to.R), Mix(from.G, to.G), Mix(from.B, to.B));
        }

        /// <summary>Perceived brightness 0..1 (Rec. 709 weights on the encoded values).</summary>
        private static double Luminance(DrawingColor color) => ((0.2126 * color.R) + (0.7152 * color.G) + (0.0722 * color.B)) / 255.0;

        private static string Css(DrawingColor color)
        {
            if (color.A == 255)
            {
                return "#" + color.R.ToString("x2", CultureInfo.InvariantCulture)
                           + color.G.ToString("x2", CultureInfo.InvariantCulture)
                           + color.B.ToString("x2", CultureInfo.InvariantCulture);
            }

            return string.Format(
                CultureInfo.InvariantCulture,
                "rgba({0},{1},{2},{3:0.###})",
                color.R,
                color.G,
                color.B,
                color.A / 255.0);
        }
    }
}
