using System;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Text;
using Microsoft.VisualStudio.Text.Differencing;
using Microsoft.VisualStudio.Utilities;
namespace Pomp.VisualStudio.ToolWindow
{
    /// <summary>One ephemeral, read-only native comparison; no temporary files or live source buffers.</summary>
    [Guid("905DEDD5-A5BA-45D7-BB39-97FD163CE0DF")]
    public sealed class PompDiffToolWindow : ToolWindowPane
    {
        private IWpfDifferenceViewer? viewer;
        private IDifferenceBuffer? difference;
        public PompDiffToolWindow() : base(null) { Caption = "POMP review snapshot"; Content = new TextBlock { Text = "Open a selected proposal from POMP Review changes.", Margin = new Thickness(12) }; }
        internal void ShowSnapshot(ITextBufferFactoryService buffers, IContentTypeRegistryService types, IDifferenceBufferFactoryService differences, IWpfDifferenceViewerFactoryService viewers, string path, string before, string after, long revision)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            Release();
            var contentType = types.GetContentType("text");
            var left = buffers.CreateTextBuffer(before, contentType); var right = buffers.CreateTextBuffer(after, contentType);
            difference = differences.CreateDifferenceBuffer(left, right, new StringDifferenceOptions(), true, true, true);
            try
            {
                viewer = viewers.CreateDifferenceView(difference);
                viewer.ViewMode = DifferenceViewMode.SideBySide;
                var panel = new DockPanel();
                panel.SetResourceReference(Panel.BackgroundProperty, VsBrushes.ToolWindowBackgroundKey);
                var header = new StackPanel { Margin = new Thickness(6) };
                var label = new TextBlock { Text = path + " · frozen revision " + revision + "\nRead-only selected proposal snapshot. Edit, validate and approve in POMP Review changes.", TextWrapping = TextWrapping.Wrap };
                label.SetResourceReference(TextBlock.ForegroundProperty, VsBrushes.ToolWindowTextKey); header.Children.Add(label);
                var buttons = new StackPanel { Orientation = Orientation.Horizontal };
                void Button(string text, Action action) { var button = new System.Windows.Controls.Button { Content = text, Margin = new Thickness(2), Padding = new Thickness(6,2,6,2) }; button.Click += (_, __) => action(); buttons.Children.Add(button); }
                Button("Side by side", () => { if (viewer != null) viewer.ViewMode = DifferenceViewMode.SideBySide; });
                Button("Inline", () => { if (viewer != null) viewer.ViewMode = DifferenceViewMode.Inline; });
                Button("Previous change", () => viewer?.ScrollToPreviousChange(true));
                Button("Next change", () => viewer?.ScrollToNextChange(true));
                Button("Clear snapshot", Release);
                header.Children.Add(buttons); DockPanel.SetDock(header, Dock.Top); panel.Children.Add(header); panel.Children.Add(viewer.VisualElement);
                Content = panel;
            }
            catch { Release(); throw; }
        }
        private void Release()
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            Content = null;
            if (viewer != null && !viewer.IsClosed) viewer.Close(); viewer = null;
            (difference as IDisposable)?.Dispose(); difference = null;
        }
        protected override void Dispose(bool disposing) { ThreadHelper.ThrowIfNotOnUIThread(); if (disposing) Release(); base.Dispose(disposing); }
    }
}
