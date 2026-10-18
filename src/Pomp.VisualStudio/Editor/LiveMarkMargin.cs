using System;
using System.ComponentModel.Composition;
using System.Windows;
using System.Windows.Controls;
using Microsoft.VisualStudio.Text.Editor;
using Microsoft.VisualStudio.Utilities;

namespace Pomp.VisualStudio.Editor
{
    /// <summary>The column left of the code that holds the Live +/− marks when Options › Live change mark position is "left".</summary>
    [Export(typeof(IWpfTextViewMarginProvider))]
    [Name(LiveMarkMargin.MarginName)]
    [Order(After = PredefinedMarginNames.LineNumber)]
    [MarginContainer(PredefinedMarginNames.Left)]
    [ContentType("text")]
    [TextViewRole(PredefinedTextViewRoles.Document)]
    internal sealed class LiveMarkMarginProvider : IWpfTextViewMarginProvider
    {
        public IWpfTextViewMargin CreateMargin(IWpfTextViewHost wpfTextViewHost, IWpfTextViewMargin marginContainer) =>
            wpfTextViewHost.TextView.Properties.GetOrCreateSingletonProperty(() => new LiveMarkMargin(wpfTextViewHost.TextView));
    }

    /// <summary>
    /// A plain canvas the view's Live controller fills on every redraw (it owns the marks, their popups and their positions); the
    /// margin only shows or hides itself. Shown only while marks go on the left, so the code never shifts otherwise.
    /// </summary>
    internal sealed class LiveMarkMargin : Canvas, IWpfTextViewMargin
    {
        internal const string MarginName = "Pomp.Live.Marks";
        private readonly IWpfTextView view;

        public LiveMarkMargin(IWpfTextView view)
        {
            this.view = view;
            ClipToBounds = true;
            Visibility = Visibility.Collapsed;
            view.Closed += OnClosed;
        }

        public FrameworkElement VisualElement => this;
        public double MarginSize => ActualWidth;
        public bool Enabled => Visibility == Visibility.Visible;
        public ITextViewMargin? GetTextViewMargin(string marginName) => string.Equals(marginName, MarginName, StringComparison.OrdinalIgnoreCase) ? this : null;

        /// <summary>Empties the column and shows it <paramref name="width"/> wide, or hides it when the width is 0.</summary>
        internal void Reset(double width)
        {
            Children.Clear();
            Width = width;
            Visibility = width > 0 ? Visibility.Visible : Visibility.Collapsed;
        }

        /// <summary>Places a mark at the top of a line, in the view's coordinates (the margin scrolls with the view, not with the text).</summary>
        internal void Place(FrameworkElement element, double lineTop)
        {
            SetLeft(element, 1);
            SetTop(element, lineTop - view.ViewportTop);
            Children.Add(element);
        }

        private void OnClosed(object? sender, EventArgs args) => Dispose();

        public void Dispose()
        {
            view.Closed -= OnClosed;
            Children.Clear();
        }
    }
}
