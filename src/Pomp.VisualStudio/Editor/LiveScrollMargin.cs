using System;
using System.Collections.Generic;
using System.ComponentModel.Composition;
using System.Windows;
using System.Windows.Media;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Text;
using Microsoft.VisualStudio.Text.Editor;
using Microsoft.VisualStudio.Utilities;
using Pomp.VisualStudio.Composition;

namespace Pomp.VisualStudio.Editor
{
    /// <summary>Live change marks on the vertical scrollbar, next to VS's own change-tracking column.</summary>
    [Export(typeof(IWpfTextViewMarginProvider))]
    [Name(LiveScrollMargin.MarginName)]
    [Order(After = PredefinedMarginNames.OverviewChangeTracking, Before = PredefinedMarginNames.OverviewMark)]
    [MarginContainer(PredefinedMarginNames.VerticalScrollBar)]
    [ContentType("text")]
    [TextViewRole(PredefinedTextViewRoles.Document)]
    internal sealed class LiveScrollMarginProvider : IWpfTextViewMarginProvider
    {
        public IWpfTextViewMargin CreateMargin(IWpfTextViewHost wpfTextViewHost, IWpfTextViewMargin marginContainer)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            return marginContainer is IVerticalScrollBar scrollBar ? new LiveScrollMargin(wpfTextViewHost.TextView, scrollBar) : null!;
        }
    }

    internal sealed class LiveScrollMargin : FrameworkElement, IWpfTextViewMargin
    {
        internal const string MarginName = "Pomp.Live.ScrollMarks";
        private const double MarkWidth = 3;
        private const double MinMarkHeight = 3;
        private static readonly HashSet<LiveScrollMargin> Margins = new HashSet<LiveScrollMargin>();
        private static readonly Brush Added = Frozen(Color.FromRgb(40, 180, 80));
        private static readonly Brush Deleted = Frozen(Color.FromRgb(220, 65, 65));
        private readonly IWpfTextView view;
        private readonly IVerticalScrollBar scrollBar;
        private bool disposed;

        public LiveScrollMargin(IWpfTextView view, IVerticalScrollBar scrollBar)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            this.view = view;
            this.scrollBar = scrollBar;
            Width = MarkWidth;
            ClipToBounds = true;
            IsHitTestVisible = false;
            Margins.Add(this);
            scrollBar.TrackSpanChanged += OnRedraw;
            view.LayoutChanged += OnLayout;
            // The margin host disposes margins on close; this also covers hosts that skip it.
            view.Closed += OnClosed;
            Refresh();
        }

        public FrameworkElement VisualElement => this;
        public double MarginSize => ActualWidth;
        public bool Enabled => Visibility == Visibility.Visible;
        public ITextViewMargin? GetTextViewMargin(string marginName) => string.Equals(marginName, MarginName, StringComparison.OrdinalIgnoreCase) ? this : null;

        /// <summary>Called whenever the Live service changed (entries, settings, session): see <see cref="LiveEditFeature.RefreshViews"/>.</summary>
        internal static void RefreshAll()
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            foreach (var margin in Margins) margin.Refresh();
        }

        private static Brush Frozen(Color color) { var brush = new SolidColorBrush(color); brush.Freeze(); return brush; }

        private static EditorFollowService? VisibleService()
        {
            var services = PompServices.Current;
            var service = services?.EditorFollow;
            return service != null && service.Enabled && services!.Settings.AppliedLiveChangesVisible ? service : null;
        }

        private void Refresh()
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            if (disposed || view.IsClosed) return;
            // Collapsed when Live is off or hidden so VS's own scrollbar columns keep their usual width.
            Visibility = VisibleService() == null ? Visibility.Collapsed : Visibility.Visible;
            InvalidateVisual();
        }

        private void OnRedraw(object? sender, EventArgs args) { if (!disposed && Visibility == Visibility.Visible) InvalidateVisual(); }
        // Buffer edits, elisions and wrapping change the scroll map; layout follows each of them.
        private void OnLayout(object? sender, TextViewLayoutChangedEventArgs args) => OnRedraw(sender, args);
        private void OnClosed(object? sender, EventArgs args) { ThreadHelper.ThrowIfNotOnUIThread(); Dispose(); }

        protected override void OnRender(DrawingContext drawingContext)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            base.OnRender(drawingContext);
            var service = VisibleService();
            if (disposed || view.IsClosed || service == null) return;
            var snapshot = view.TextSnapshot;
            // The map follows the view's snapshot; mid-edit, wait for the layout that catches up with the buffer.
            if (snapshot != view.TextBuffer.CurrentSnapshot) return;
            foreach (var entry in service.GetChanges(view.TextBuffer))
            {
                if (entry.Start < 0 || entry.Start > snapshot.Length) continue;
                double top, bottom;
                if (entry.After.Length == 0)
                {
                    var y = scrollBar.GetYCoordinateOfBufferPosition(new SnapshotPoint(snapshot, entry.Start));
                    top = y - MinMarkHeight / 2; bottom = y + MinMarkHeight / 2;
                }
                else
                {
                    var first = snapshot.GetLineFromPosition(entry.Start);
                    var last = snapshot.GetLineFromPosition(Math.Min(snapshot.Length, Math.Max(entry.Start, entry.End - 1)));
                    top = scrollBar.GetYCoordinateOfBufferPosition(first.Start);
                    bottom = last.LineNumber + 1 < snapshot.LineCount
                        ? scrollBar.GetYCoordinateOfBufferPosition(snapshot.GetLineFromLineNumber(last.LineNumber + 1).Start)
                        : scrollBar.TrackSpanBottom;
                    bottom = Math.Max(bottom, top + MinMarkHeight);
                }
                drawingContext.DrawRectangle(entry.After.Length == 0 ? Deleted : Added, null, new Rect(0, top, MarkWidth, bottom - top));
            }
        }

        public void Dispose()
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            if (disposed) return;
            disposed = true;
            Margins.Remove(this);
            scrollBar.TrackSpanChanged -= OnRedraw;
            view.LayoutChanged -= OnLayout;
            view.Closed -= OnClosed;
        }
    }
}
