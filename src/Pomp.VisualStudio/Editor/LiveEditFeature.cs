using System;
using System.Collections.Generic;
using System.ComponentModel.Composition;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Text;
using Microsoft.VisualStudio.Text.Editor;
using Microsoft.VisualStudio.Utilities;
using Pomp.VisualStudio.Composition;
using Pomp.VisualStudio.Options;

namespace Pomp.VisualStudio.Editor
{
    [Export(typeof(IWpfTextViewCreationListener))]
    [ContentType("text")]
    [TextViewRole(PredefinedTextViewRoles.Document)]
    internal sealed class LiveEditFeature : IWpfTextViewCreationListener
    {
        private const string BackgroundLayer = "Pomp.Live.Background";
        private const string ControlsLayer = "Pomp.Live.Controls";
        [Export(typeof(AdornmentLayerDefinition)), Name(BackgroundLayer)]
        [Order(After = PredefinedAdornmentLayers.Selection, Before = PredefinedAdornmentLayers.Text)]
        public AdornmentLayerDefinition Background = null!;
        [Export(typeof(AdornmentLayerDefinition)), Name(ControlsLayer)]
        [Order(After = PredefinedAdornmentLayers.Text)]
        public AdornmentLayerDefinition Controls = null!;
        private static readonly HashSet<ViewController> Views = new HashSet<ViewController>();
        public void TextViewCreated(IWpfTextView textView) { ThreadHelper.ThrowIfNotOnUIThread(); _ = new ViewController(textView); }

        /// <summary>The user is typing in, or has focus in, an editor: Live does not navigate then. A selection left in a background tab does not count.</summary>
        internal static bool EditorInteractionActive
        {
            get
            {
                foreach (var item in Views)
                    if (!item.View.IsClosed && (item.Composing || item.View.HasAggregateFocus || item.View.VisualElement.IsKeyboardFocusWithin)) return true;
                return false;
            }
        }

        /// <summary>Whether a view of <paramref name="buffer"/> holds a selection, which moving the caret would destroy.</summary>
        internal static bool HasSelection(ITextBuffer buffer)
        {
            foreach (var item in Views)
                if (!item.View.IsClosed && item.View.TextBuffer == buffer && !item.View.Selection.IsEmpty) return true;
            return false;
        }

        /// <summary>Escape in <paramref name="view"/> (from its command filter): closes an open comparison; true when one was open.</summary>
        internal static bool CloseComparison(ITextView view)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            foreach (var item in Views)
                if (item.View == view && item.CloseOpenPopup()) return true;
            return false;
        }

        internal static void RefreshViews()
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            foreach (var item in Views)
                if (!ReferenceEquals(item.service, PompServices.Current?.EditorFollow)) item.Refresh();
            LiveScrollMargin.RefreshAll();
        }

        private sealed class ViewController
        {
            private static readonly Brush Green = Frozen(Color.FromArgb(44, 40, 180, 80));
            private static readonly Brush GreenForeground = Frozen(Color.FromRgb(40, 180, 80));
            private static readonly Brush Red = Frozen(Color.FromRgb(190, 55, 55));
            private static readonly Brush RedBackground = Frozen(Color.FromArgb(40, 220, 65, 65));
            private readonly IAdornmentLayer background;
            private readonly IAdornmentLayer controls;
            private readonly List<Popup> popups = new List<Popup>();
            internal EditorFollowService? service;
            private bool rendering;
            private Style? buttonStyle;
            private Color? buttonBackground;
            internal readonly IWpfTextView View;
            internal bool Composing;

            public ViewController(IWpfTextView view)
            {
                ThreadHelper.ThrowIfNotOnUIThread();
                View = view;
                background = view.GetAdornmentLayer(BackgroundLayer);
                controls = view.GetAdornmentLayer(ControlsLayer);
                Views.Add(this);
                view.LayoutChanged += OnLayout;
                view.ViewportWidthChanged += OnViewport;
                view.GotAggregateFocus += OnFocus;
                view.Closed += OnClosed;
                TextCompositionManager.AddPreviewTextInputStartHandler(view.VisualElement, OnCompositionStart);
                TextCompositionManager.AddPreviewTextInputHandler(view.VisualElement, OnCompositionEnd);
                Refresh();
            }
            private static Brush Frozen(Color color) { var brush = new SolidColorBrush(color); brush.Freeze(); return brush; }
            private void OnCompositionStart(object sender, TextCompositionEventArgs args) { Composing = true; }
            private void OnCompositionEnd(object sender, TextCompositionEventArgs args) { Composing = false; }
            private void OnLayout(object sender, TextViewLayoutChangedEventArgs args) { ThreadHelper.ThrowIfNotOnUIThread(); Refresh(); }
            private void OnViewport(object? sender, EventArgs args) { ThreadHelper.ThrowIfNotOnUIThread(); Refresh(); }
            // A click on a + or Revert button in an unfocused view moves focus first. Redrawing then would replace the pressed button
            // before mouse-up and lose the click, so a focus that arrives with the left button down skips the redraw; the click's own
            // edit (or the next layout) redraws.
            private void OnFocus(object? sender, EventArgs args)
            {
                ThreadHelper.ThrowIfNotOnUIThread();
                if (Mouse.LeftButton != MouseButtonState.Pressed) Refresh();
            }
            private void OnChanged(object? sender, EventArgs args) { ThreadHelper.ThrowIfNotOnUIThread(); Refresh(); }

            internal void Refresh()
            {
                ThreadHelper.ThrowIfNotOnUIThread();
                if (View.IsClosed || rendering) return;
                var next = PompServices.Current?.EditorFollow;
                if (service != next)
                {
                    if (service != null) service.Changed -= OnChanged;
                    service = next;
                    if (service != null) service.Changed += OnChanged;
                }
                Render();
            }

            internal bool CloseOpenPopup()
            {
                ThreadHelper.ThrowIfNotOnUIThread();
                var closed = false;
                foreach (var popup in popups)
                    if (popup.IsOpen) { popup.IsOpen = false; closed = true; }
                return closed;
            }

            private void Clear()
            {
                foreach (var popup in popups) popup.IsOpen = false;
                popups.Clear();
                background.RemoveAllAdornments();
                controls.RemoveAllAdornments();
                MarkColumn()?.Children.Clear();
            }

            /// <summary>The view's left mark column (created by its margin provider), or null.</summary>
            private LiveMarkMargin? MarkColumn() => View.Properties.TryGetProperty(typeof(LiveMarkMargin), out LiveMarkMargin margin) ? margin : null;

            private void Render()
            {
                ThreadHelper.ThrowIfNotOnUIThread();
                if (View.InLayout) return;
                rendering = true;
                try
                {
                    Clear();
                    var current = service;
                    var settings = PompServices.Current?.Settings;
                    var lines = View.TextViewLines;
                    var column = MarkColumn();
                    // Hidden changes keep being tracked by the service; only the drawing stops.
                    if (current == null || !current.Enabled || settings == null || !settings.AppliedLiveChangesVisible
                        || lines == null || lines.Count == 0 || View.ViewportWidth <= 0) { column?.Reset(0); return; }
                    var snapshot = View.TextSnapshot;
                    if (lines.FormattedSpan.Snapshot != snapshot) return;
                    var color = (View.Background as SolidColorBrush)?.Color;
                    if (buttonStyle == null || buttonBackground != color)
                    {
                        buttonBackground = color;
                        buttonStyle = InlineEditPrompt.ButtonStyle(new Thickness(0));
                    }
                    // Two independent parts (Options): the line background of added/replaced lines, and the +/− marks. The mark on a
                    // change's first line opens the comparison and Revert; without the background, the change's other lines get a "+".
                    var drawBackground = settings.AppliedLiveChangeBackground;
                    var drawSymbols = settings.AppliedLiveChangeSymbols;
                    // Options › mark position "left": the marks go in a column left of the code (shown only then, so the code keeps its
                    // place otherwise).
                    var left = drawSymbols && column != null && settings.AppliedLiveChangeMarkPosition == LiveChangeMarkPosition.Left;
                    var lineHeight = View.LineHeight;
                    column?.Reset(left ? Math.Ceiling(lineHeight) + 6 : 0);
                    if (!drawBackground && !drawSymbols) return;
                    foreach (var entry in current.GetChanges(View.TextBuffer))
                    {
                        if (entry.Start < 0 || entry.Start > snapshot.Length) continue;
                        var valid = entry.Matches(snapshot);
                        var firstLine = snapshot.GetLineNumberFromPosition(entry.Start);
                        var lastLine = snapshot.GetLineNumberFromPosition(Math.Min(snapshot.Length, Math.Max(entry.Start, entry.End - 1)));
                        var badgeDrawn = false;
                        foreach (var line in lines)
                        {
                            var number = snapshot.GetLineNumberFromPosition(line.Start.Position);
                            if (number < firstLine || number > lastLine) continue;
                            if (valid && entry.After.Length > 0 && drawBackground)
                            {
                                var band = new Border { Background = Green, Width = View.ViewportWidth, Height = line.TextHeight, IsHitTestVisible = false };
                                Canvas.SetLeft(band, View.ViewportLeft); Canvas.SetTop(band, line.TextTop);
                                background.AddAdornment(AdornmentPositioningBehavior.TextRelative, line.ExtentIncludingLineBreak, entry, band, null);
                            }
                            if (!drawSymbols) continue;
                            // The first line's mark already says "+", so it gets no second one.
                            if (valid && entry.After.Length > 0 && badgeDrawn && !drawBackground)
                            {
                                var symbol = MarkGlyph(true, GreenForeground, line.TextHeight);
                                symbol.IsHitTestVisible = false;
                                AutomationProperties.SetName(symbol, Pomp.Hosting.PompLanguage.Pick("POMP가 추가하거나 바꾼 줄", "POMP added or replaced line"));
                                if (left) { symbol.Margin = new Thickness((Math.Ceiling(lineHeight) + 4 - symbol.Width) / 2, (line.TextHeight - symbol.Height) / 2, 0, 0); column!.Place(symbol, line.TextTop); }
                                else
                                {
                                    Canvas.SetLeft(symbol, line.TextRight + 4); Canvas.SetTop(symbol, line.TextTop + (line.TextHeight - symbol.Height) / 2);
                                    controls.AddAdornment(AdornmentPositioningBehavior.TextRelative, line.ExtentIncludingLineBreak, entry, symbol, null);
                                }
                            }
                            if (!badgeDrawn)
                            {
                                AddControls(current, entry, valid, line.Start, line.TextTop, line.TextRight, line.TextHeight, left ? column : null);
                                badgeDrawn = true;
                            }
                        }
                    }
                }
                finally { rendering = false; }
            }

            /// <summary>
            /// A "+" or "−" drawn as strokes, centred in a square of about half the line height: exactly in the middle of its button,
            /// whatever the font's ascent and descent (a text glyph sits above the middle).
            /// </summary>
            private static FrameworkElement MarkGlyph(bool plus, Brush brush, double lineHeight)
            {
                var size = Math.Max(5, Math.Round(lineHeight * 0.5));
                var half = size / 2;
                var geometry = new GeometryGroup();
                geometry.Children.Add(new LineGeometry(new Point(0, half), new Point(size, half)));
                if (plus) geometry.Children.Add(new LineGeometry(new Point(half, 0), new Point(half, size)));
                geometry.Freeze();
                return new System.Windows.Shapes.Path
                {
                    Data = geometry, Stroke = brush, StrokeThickness = Math.Max(1.5, Math.Round(size / 5)), Width = size, Height = size,
                    SnapsToDevicePixels = true, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center,
                };
            }

            // One control per change: the "+" / "−" mark. Clicking it opens the before/after comparison, which also holds Revert.
            private void AddControls(EditorFollowService current, LiveChange entry, bool valid, SnapshotPoint point, double top, double right, double height,
                LiveMarkMargin? column)
            {
                ThreadHelper.ThrowIfNotOnUIThread();
                var kind = entry.Before.Length == 0 ? Pomp.Hosting.PompLanguage.Pick("추가", "addition")
                    : entry.After.Length == 0 ? Pomp.Hosting.PompLanguage.Pick("삭제", "deletion") : Pomp.Hosting.PompLanguage.Pick("변경", "replacement");
                // The mark fits the code line exactly: as tall as the line, square, its sign drawn in the middle.
                // A conflict reads "POMP 충돌" after the line; the narrow left column has room for a "!" only (the tooltip says why).
                FrameworkElement label = !valid
                    ? new TextBlock { Text = column != null ? "!" : Pomp.Hosting.PompLanguage.Pick("POMP 충돌", "POMP conflict"), Foreground = View.FormattedLineSource.DefaultTextProperties.ForegroundBrush, FontWeight = column != null ? FontWeights.Bold : FontWeights.Normal, VerticalAlignment = VerticalAlignment.Center, Margin = column != null ? new Thickness(0) : new Thickness(4, 0, 4, 0) }
                    // Additions and replacements both leave POMP text in the buffer: one green "+"; the popup shows the preimage.
                    : MarkGlyph(entry.After.Length > 0, entry.After.Length > 0 ? GreenForeground : Red, height);
                var info = new Button
                {
                    Content = label, Style = buttonStyle, Height = height, MinWidth = height, VerticalContentAlignment = VerticalAlignment.Center,
                    HorizontalContentAlignment = HorizontalAlignment.Center, Background = View.Background, Opacity = 0.95,
                    // Over the editor the I-beam would show; the mark is clicked, not typed into.
                    Cursor = Cursors.Hand,
                    // The keyboard stays in the editor, whose command filter closes the comparison on Escape (a focused mark would take it).
                    Focusable = false,
                    ToolTip = Pomp.Hosting.PompLanguage.Pick("POMP 변경: 눌러서 이전·이후 비교와 원복", "POMP change: click to compare and revert")
                        + (!valid ? Pomp.Hosting.PompLanguage.Pick(" (직접 고쳐서 원복할 수 없음)", " (edited independently; cannot revert)") : string.Empty)
                };
                AutomationProperties.SetName(info, "POMP " + kind + (valid
                    ? Pomp.Hosting.PompLanguage.Pick(": 비교와 원복 열기", ": show comparison and revert")
                    : Pomp.Hosting.PompLanguage.Pick(": 직접 고쳐서 원복할 수 없음", ": edited independently, cannot revert")));
                var popup = CreatePopup(current, info, entry, kind, valid);
                // While the popup is open the mark takes no clicks: a click on it is then an outside click that closes the popup
                // (StaysOpen=false), instead of reaching the button and opening the popup again.
                popup.Opened += (sender, args) => info.IsHitTestVisible = false;
                popup.Closed += (sender, args) => info.IsHitTestVisible = true;
                // The press puts the focus in this editor (the click came from the chat or another view): its Escape then reaches the
                // filter. The focus handler skips its redraw while the button is down, so the mark survives until the click.
                info.PreviewMouseLeftButtonDown += (sender, args) => { if (!View.HasAggregateFocus) View.VisualElement.Focus(); };
                info.Click += (sender, args) => { args.Handled = true; popup.IsOpen = true; };
                popups.Add(popup);
                info.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
                if (column != null)
                {
                    // Left column: the mark scrolls with the lines, not sideways with the text.
                    info.Margin = new Thickness(Math.Max(0, (column.Width - 2 - height) / 2), 0, 0, 0);
                    column.Place(info, top);
                    return;
                }
                // Never overlay code when it fills the viewport: the mark stays after the text and follows horizontal scrolling.
                Canvas.SetLeft(info, Math.Max(View.ViewportLeft + 4, right + 12)); Canvas.SetTop(info, top);
                controls.AddAdornment(AdornmentPositioningBehavior.TextRelative, new SnapshotSpan(point, 0), entry, info, null);
            }

            private Popup CreatePopup(EditorFollowService current, Button target, LiveChange entry, string kind, bool valid)
            {
                var stack = new StackPanel();
                stack.Children.Add(new TextBlock
                {
                    Text = "POMP " + kind + (valid
                        ? Pomp.Hosting.PompLanguage.Pick(" · 이 대화에서 처음 고치기 전과 비교", " · compared with before this conversation first changed it")
                        : Pomp.Hosting.PompLanguage.Pick(" · 충돌: 기록된 내용이라 지금 편집기 내용과 달라요", " · conflict: recorded text, not the current buffer")),
                    Margin = new Thickness(6), Foreground = View.FormattedLineSource.DefaultTextProperties.ForegroundBrush
                });
                // Before and after always side by side in the same frame of surrounding lines, so an addition (nothing before) or a
                // deletion (nothing after) reads as a comparison too. A conflicted change has no trustworthy surroundings: none shown.
                var above = string.Empty; var below = string.Empty;
                if (valid)
                {
                    var snapshot = View.TextSnapshot;
                    var first = snapshot.GetLineFromPosition(entry.Start).LineNumber;
                    var from = snapshot.GetLineFromLineNumber(Math.Max(0, first - ContextLines)).Start.Position;
                    above = snapshot.GetText(from, entry.Start - from);
                    var endLine = snapshot.GetLineFromPosition(entry.End);
                    var last = Math.Min(snapshot.LineCount - 1, endLine.LineNumber + (entry.End == endLine.Start.Position && entry.Length > 0 ? ContextLines - 1 : ContextLines));
                    below = snapshot.GetText(entry.End, Math.Max(0, snapshot.GetLineFromLineNumber(last).End.Position - entry.End));
                }
                var before = Section(Pomp.Hosting.PompLanguage.Pick("이전 (변경 전)", "Before"), Red, RedBackground, above, entry.Before, below, RedBackground, Red,
                    Pomp.Hosting.PompLanguage.Pick("(없던 내용 — 새로 추가됨)", "(nothing here — added)"), Pomp.Hosting.PompLanguage.Pick("POMP 변경 전 내용, 읽기 전용", "Text before the POMP change, read only"));
                var after = Section(Pomp.Hosting.PompLanguage.Pick("이후 (변경 후)", "After"), GreenForeground, Green, above, entry.After, below, Green,
                    View.FormattedLineSource.DefaultTextProperties.ForegroundBrush, Pomp.Hosting.PompLanguage.Pick("(삭제됨)", "(deleted)"), Pomp.Hosting.PompLanguage.Pick("POMP 변경 후 내용, 읽기 전용", "Text after the POMP change, read only"));
                // Options › Live comparison layout: stacked or side by side. Each side is its own framed box with a coloured title bar and
                // a gap between them, so the two never read as one block of code.
                var horizontal = PompServices.Current?.Settings.AppliedLiveCompareLayout == LiveCompareLayout.Horizontal;
                if (horizontal)
                {
                    // Both sides one width, the wider side's natural width (a shared size group, sized by the live layout at the real DPI
                    // and text mode), capped so that a long line scrolls inside its box.
                    var grid = new Grid { Margin = new Thickness(6, 0, 6, 0) };
                    Grid.SetIsSharedSizeScope(grid, true);
                    grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto, SharedSizeGroup = "Side" });
                    grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(8) });
                    grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto, SharedSizeGroup = "Side" });
                    before.MaxWidth = MaxSideWidth; after.MaxWidth = MaxSideWidth;
                    Grid.SetColumn(before, 0); Grid.SetColumn(after, 2);
                    grid.Children.Add(before); grid.Children.Add(after);
                    stack.Children.Add(grid);
                }
                else
                {
                    before.Margin = new Thickness(6, 0, 6, 0);
                    after.Margin = new Thickness(6, 8, 6, 0);
                    stack.Children.Add(before); stack.Children.Add(after);
                }
                var popup = new Popup
                {
                    PlacementTarget = target, Placement = PlacementMode.Bottom, StaysOpen = false, AllowsTransparency = true,
                    Child = new Border { Child = stack, Background = View.Background, BorderBrush = View.FormattedLineSource.DefaultTextProperties.ForegroundBrush,
                        BorderThickness = new Thickness(1), MaxWidth = horizontal ? 2 * MaxSideWidth + 40 : 750 }
                };
                var revert = new Button
                {
                    Content = Pomp.Hosting.PompLanguage.Pick("원복", "Revert"), Style = buttonStyle, Padding = new Thickness(8, 1, 8, 1), Margin = new Thickness(6),
                    HorizontalAlignment = HorizontalAlignment.Left, IsEnabled = valid,
                    ToolTip = valid
                        ? Pomp.Hosting.PompLanguage.Pick("이 구간만 이 대화에서 처음 고치기 전으로 되돌려요. 저장하지 않고, 실행 취소로 되살릴 수 있어요.", "Revert only this range to before this conversation first changed it. Not saved; Undo restores it.")
                        : Pomp.Hosting.PompLanguage.Pick("이 구간을 직접 고쳐서 원복할 수 없어요.", "This range was edited independently and cannot be reverted.")
                };
                AutomationProperties.SetName(revert, Pomp.Hosting.PompLanguage.Pick("POMP " + kind + " 원복", "Revert POMP " + kind));
                ToolTipService.SetShowOnDisabled(revert, true);
                revert.Click += (sender, args) => { ThreadHelper.ThrowIfNotOnUIThread(); popup.IsOpen = false; current.Revert(View.TextBuffer, entry); args.Handled = true; };
                stack.Children.Add(revert);
                // Escape closes it also when the focus went into it (the Revert button); in the editor its command filter does.
                popup.PreviewKeyDown += (sender, args) => { if (args.Key == Key.Escape) { popup.IsOpen = false; args.Handled = true; } };
                return popup;
            }

            /// <summary>Lines of unchanged code shown above and below a change in its comparison.</summary>
            private const int ContextLines = 2;

            /// <summary>The widest a side of the side-by-side comparison gets; longer lines scroll inside it.</summary>
            private const double MaxSideWidth = 600;

            /// <summary>
            /// One side of the comparison in its own frame: a title bar in the side's colour, then the surrounding lines as they are, the
            /// changed lines highlighted and a placeholder line where this side has no text. Lines never wrap; a wide one scrolls.
            /// </summary>
            private FrameworkElement Section(string header, Brush accent, Brush headerBackground, string above, string changed, string below,
                Brush changedBackground, Brush changedForeground, string emptyText, string automationName)
            {
                var text = View.FormattedLineSource.DefaultTextProperties;
                var rows = new StackPanel { Margin = new Thickness(0, 2, 0, 2) };
                TextBlock Row(string value, Brush foreground, Brush background, bool italic = false) => new TextBlock
                {
                    Text = value.Length == 0 ? " " : value, Foreground = foreground, Background = background, FontFamily = text.Typeface.FontFamily,
                    FontSize = text.FontRenderingEmSize, FontStyle = italic ? FontStyles.Italic : FontStyles.Normal, TextWrapping = TextWrapping.NoWrap,
                    Padding = new Thickness(6, 0, 6, 0),
                };
                int from = above.Length, to = from + changed.Length;
                var placeholderDone = changed.Length > 0;
                void Placeholder() { rows.Children.Add(Row(emptyText, changedForeground, changedBackground, italic: true)); placeholderDone = true; }
                foreach (var line in SplitLines(above + changed + below))
                {
                    if (!placeholderDone && line.Start >= from) Placeholder();
                    var isChanged = changed.Length > 0 && line.Start < to && from < line.Next;
                    rows.Children.Add(Row(line.Text, isChanged ? changedForeground : text.ForegroundBrush, isChanged ? changedBackground : Brushes.Transparent));
                }
                if (!placeholderDone) Placeholder();
                var scroll = new ScrollViewer
                {
                    Content = rows, MaxHeight = 240, HorizontalScrollBarVisibility = ScrollBarVisibility.Auto, VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                };
                // Highlighted rows span the whole box even when every line is short.
                rows.SetBinding(FrameworkElement.MinWidthProperty, new System.Windows.Data.Binding(nameof(ScrollViewer.ViewportWidth)) { Source = scroll });
                AutomationProperties.SetName(scroll, automationName);
                var title = new Border
                {
                    Background = headerBackground, BorderBrush = accent, BorderThickness = new Thickness(0, 0, 0, 1), Padding = new Thickness(6, 2, 6, 2),
                    Child = new TextBlock { Text = header, Foreground = accent, FontWeight = FontWeights.Bold },
                };
                var body = new DockPanel();
                DockPanel.SetDock(title, Dock.Top);
                body.Children.Add(title);
                body.Children.Add(scroll);
                return new Border { BorderBrush = accent, BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(2), Child = body };
            }

            /// <summary>
            /// The lines of <paramref name="text"/> with where each starts and where the next one starts (after its line break). A final line
            /// break opens no empty line of its own.
            /// </summary>
            private static List<(string Text, int Start, int Next)> SplitLines(string text)
            {
                var lines = new List<(string, int, int)>();
                var start = 0;
                while (start < text.Length)
                {
                    var end = text.IndexOfAny(new[] { '\r', '\n' }, start);
                    if (end < 0) { lines.Add((text.Substring(start), start, text.Length)); break; }
                    var next = end + (text[end] == '\r' && end + 1 < text.Length && text[end + 1] == '\n' ? 2 : 1);
                    lines.Add((text.Substring(start, end - start), start, next));
                    start = next;
                }
                return lines;
            }

            private void OnClosed(object sender, EventArgs args)
            {
                Views.Remove(this);
                View.LayoutChanged -= OnLayout; View.ViewportWidthChanged -= OnViewport; View.GotAggregateFocus -= OnFocus; View.Closed -= OnClosed;
                TextCompositionManager.RemovePreviewTextInputStartHandler(View.VisualElement, OnCompositionStart);
                TextCompositionManager.RemovePreviewTextInputHandler(View.VisualElement, OnCompositionEnd);
                if (service != null) service.Changed -= OnChanged;
                service = null; Clear();
            }
        }
    }
}
