using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows.Media.Imaging;
using Microsoft.VisualStudio.Imaging.Interop;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Shell.Interop;
using Pomp.Diagnostics;
using Pomp.VisualStudio.Composition;

namespace Pomp.VisualStudio.ToolWindow
{
    /// <summary>State handed from <see cref="PompPackage"/> (background) to the tool window pane (UI thread).</summary>
    public sealed class PompToolWindowState
    {
        internal PompToolWindowState(PompServices services)
        {
            Services = services;
        }

        internal PompServices Services { get; }
    }

    /// <summary>The "POMP" tool window (View &gt; Other Windows &gt; POMP, Tools &gt; POMP &gt; Open POMP).</summary>
    [Guid(PompGuids.ToolWindowGuidString)]
    public sealed class PompChatToolWindow : ToolWindowPane
    {
        private readonly PompChatControl control;

        /// <summary>Created by <see cref="PompPackage"/> through the async tool window factory.</summary>
        public PompChatToolWindow(PompToolWindowState state)
            : base(null)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            Caption = "POMP";
            BitmapImageMoniker = TabIcon(state.Services);
            control = new PompChatControl(state.Services);
            Content = control;
        }

        // The image service drops a custom image once its handle is collected, so the handle lives as long as VS.
        private static IImageHandle? tabIcon;

        /// <summary>The tab icon: Resources\PompIcon.png from the extension folder. No icon when it cannot be loaded.</summary>
        private static ImageMoniker TabIcon(PompServices services)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            if (tabIcon == null)
            {
                try
                {
                    var image = new BitmapImage();
                    image.BeginInit();
                    image.UriSource = new Uri(Path.Combine(services.Environment.ExtensionDirectory, "Resources", "PompIcon.png"));
                    image.CacheOption = BitmapCacheOption.OnLoad;
                    image.EndInit();
                    image.Freeze();
                    if (Microsoft.VisualStudio.Shell.Package.GetGlobalService(typeof(SVsImageService)) is IVsManagedImageService images)
                    {
                        tabIcon = images.AddCustomImage(image, canTheme: false);
                    }
                }
                catch (Exception ex)
                {
                    services.Logger.Warn("The POMP tab icon could not be loaded: " + ex.Message);
                }
            }

            return tabIcon?.Moniker ?? default;
        }

        internal PompChatControl Control => control;

        /// <inheritdoc />
        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                control.Dispose();
            }

            base.Dispose(disposing);
        }
    }
}
