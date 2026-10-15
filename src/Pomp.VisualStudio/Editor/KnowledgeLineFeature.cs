using System;
using System.Collections.Generic;
using System.ComponentModel.Composition;
using System.Globalization;
using System.Threading.Tasks;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Text;
using Microsoft.VisualStudio.Text.Editor;
using Microsoft.VisualStudio.Threading;
using Microsoft.VisualStudio.Utilities;
using Pomp.Diagnostics;
using Pomp.Features;
using Pomp.VisualStudio.Adapters;
using Pomp.VisualStudio.Composition;
using Pomp.VisualStudio.Interop;
using Pomp.VisualStudio.Options;

namespace Pomp.VisualStudio.Editor
{
    /// <summary>
    /// Colors the lines of the knowledge pieces that are anchored to a document with their knowledge's color: a faint band across the
    /// editor behind the text of each line (Tools > Options > Pomp > Knowledge line colors). The store says where the lines were last
    /// seen; the editor finds them again by their text, follows later edits with tracking spans and draws nothing for lines it cannot find.
    /// </summary>
    [Export(typeof(IWpfTextViewCreationListener))]
    [ContentType("text")]
    [TextViewRole(PredefinedTextViewRoles.Document)]
    internal sealed class KnowledgeLineFeature : IWpfTextViewCreationListener
    {
        internal const string LayerName = "Pomp.KnowledgeLines";
        [Export(typeof(AdornmentLayerDefinition))]
        [Name(LayerName)]
        [Order(After = PredefinedAdornmentLayers.Selection, Before = PredefinedAdornmentLayers.Text)]
        public AdornmentLayerDefinition Layer = null!;
        [Import] public ITextDocumentFactoryService Documents = null!;
        public void TextViewCreated(IWpfTextView textView) => _ = new ViewController(textView, Documents);

        /// <summary>Buffers (and selections) larger than this are left alone, like the other editor features do.</summary>
        private const int MaxChars = 500000;

        /// <summary>The lines a knowledge piece is made from: the selected lines of the view, or its caret line when nothing is selected (see <see cref="KnowledgeLineSpans.Read"/>).</summary>
        internal static KnowledgeDraft ReadLines(ITextView view, string fullPath)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            var selection = view.Selection.IsEmpty ? (SnapshotSpan?)null : view.Selection.StreamSelectionSpan.SnapshotSpan;
            return KnowledgeLineSpans.Read(view.TextSnapshot, view.Caret.Position.BufferPosition, selection, fullPath, MaxChars);
        }

        private sealed class ViewController
        {
            /// <summary>Opacity of a band (0.18): low enough for the text to stay readable on light and dark themes.</summary>
            private const byte Alpha = 46;
            private const int MaxMarks = 500;
            private static readonly Dictionary<int, Brush> BrushCache = new Dictionary<int, Brush>();

            private readonly IWpfTextView view;
            private readonly ITextDocumentFactoryService documents;
            private readonly IAdornmentLayer layer;
            private readonly DispatcherTimer refresh, relocate;
            private IKnowledgeEditor? editor;
            private PompOptionsPage? settings;
            private ITextDocument? document;
            private List<Entry> entries = new List<Entry>();
            private int generation;
            private bool closed, stale = true;
            private static bool Enabled => PompServices.Current?.Settings is IKnowledgeSettings knowledge && knowledge.KnowledgeLineColors;

            public ViewController(IWpfTextView view, ITextDocumentFactoryService documents)
            {
                this.view = view; this.documents = documents; layer = view.GetAdornmentLayer(LayerName);
                refresh = new DispatcherTimer(DispatcherPriority.Background, view.VisualElement.Dispatcher) { Interval = TimeSpan.FromMilliseconds(250) };
                relocate = new DispatcherTimer(DispatcherPriority.Background, view.VisualElement.Dispatcher) { Interval = TimeSpan.FromMilliseconds(600) };
                refresh.Tick += OnRefresh; relocate.Tick += OnRelocate;
                view.LayoutChanged += OnLayout; view.ViewportWidthChanged += OnViewport; view.TextBuffer.Changed += OnTextChanged; view.GotAggregateFocus += OnFocus; view.Closed += OnClosed;
                Schedule();
            }

            private void Schedule() { if (closed) return; refresh.Stop(); refresh.Start(); }

            /// <summary>The knowledge or the settings changed (any thread): read the document's pieces again.</summary>
            private void OnSourceChanged(object? sender, EventArgs args)
            {
                // The package's factory tracks the task (joined when the package closes); a failure is logged, not lost.
                var services = PompServices.Current;
                if (services == null || closed) return;
                services.JoinableTaskFactory.RunAsync(async () => { await services.JoinableTaskFactory.SwitchToMainThreadAsync(); if (!closed) { stale = true; Schedule(); } })
                    .Task.Forget(services.Logger, "Refreshing the knowledge line colors");
            }

            private void OnFileAction(object? sender, TextDocumentFileActionEventArgs args)
            {
                // A reload replaces the text and a rename moves the file: the pieces are looked up and located again.
                if ((args.FileActionType & (FileActionTypes.DocumentRenamed | FileActionTypes.ContentLoadedFromDisk)) != 0) OnSourceChanged(sender, EventArgs.Empty);
            }

            private void OnLayout(object sender, TextViewLayoutChangedEventArgs args)
            {
                if (closed) return;
                if (entries.Count > 0) Render();
                else if (stale && PompServices.Current != null) Schedule();
            }

            private void OnFocus(object? sender, EventArgs args) { if (!closed && stale && PompServices.Current != null) Schedule(); }

            private void OnViewport(object? sender, EventArgs args) { if (!closed && entries.Count > 0) Render(); }

            private void OnTextChanged(object sender, TextContentChangedEventArgs args) { if (!closed && entries.Count > 0) { relocate.Stop(); relocate.Start(); } }

            /// <summary>Follows the services of the moment: they appear when the package loads.</summary>
            private void Bind(PompServices? services)
            {
                var nextEditor = services?.KnowledgeEditor;
                var nextSettings = services?.Settings;
                if (!ReferenceEquals(editor, nextEditor))
                {
                    if (editor != null) editor.KnowledgeChanged -= OnSourceChanged;
                    editor = nextEditor;
                    if (editor != null) editor.KnowledgeChanged += OnSourceChanged;
                }
                if (!ReferenceEquals(settings, nextSettings))
                {
                    if (settings != null) settings.Changed -= OnSourceChanged;
                    settings = nextSettings;
                    if (settings != null) settings.Changed += OnSourceChanged;
                }
            }

            private bool TryGetPath(out string path)
            {
                path = string.Empty;
                if (document == null)
                {
                    if (!documents.TryGetTextDocument(view.TextBuffer, out var found)) return false;
                    document = found; found.FileActionOccurred += OnFileAction;
                }
                var full = VsAdapterContext.IsFilePath(document.FilePath) ? VsAdapterContext.NormalizePath(document.FilePath) : null;
                if (full == null) return false;
                path = full;
                return true;
            }

            /// <summary>Reads the document's anchored pieces from the knowledge (off the UI thread), then finds their lines in the editor.</summary>
            private void OnRefresh(object? sender, EventArgs args)
            {
                refresh.Stop();
                if (closed) return;
                var services = PompServices.Current;
                Bind(services);
                // Without services (the package is not loaded yet) the view stays stale: its next layout tries again.
                if (services == null) return;
                stale = false;
                var source = editor;
                if (source == null || !Enabled || view.TextSnapshot.Length > MaxChars || !TryGetPath(out var path)) { Clear(); return; }
                var token = ++generation;
                services.JoinableTaskFactory.RunAsync(async () =>
                {
                    IReadOnlyList<KnowledgeLineMark> marks;
                    try
                    {
                        await TaskScheduler.Default;
                        marks = source.KnowledgeLines(path);
                    }
                    catch (Exception ex)
                    {
                        services.Logger.Debug("Knowledge line colors unavailable: " + ex.Message);
                        marks = Array.Empty<KnowledgeLineMark>();
                    }
                    await services.JoinableTaskFactory.SwitchToMainThreadAsync();
                    if (!closed && token == generation) Locate(marks);
                }).Task.Forget(services.Logger, "Reading the knowledge lines of a document");
            }

            /// <summary>Finds the lines of every piece in the buffer as it is now and draws them.</summary>
            private void Locate(IReadOnlyList<KnowledgeLineMark> marks)
            {
                if (!Enabled) { Clear(); return; }
                var snapshot = view.TextSnapshot;
                var next = new List<Entry>();
                if (snapshot.Length <= MaxChars)
                {
                    var matcher = CreateMatcher(snapshot);
                    foreach (var mark in marks)
                    {
                        if (next.Count >= MaxMarks) break;
                        var brush = BrushFor(mark.Color);
                        if (brush == null) continue;
                        var range = matcher.Find(mark);
                        // A piece that is not found stays on the list: it may come back (an undo, a paste) and is looked for after edits.
                        next.Add(new Entry(mark, brush, range == null ? null : KnowledgeLineSpans.Track(snapshot, range.Value)));
                    }
                }
                entries = next;
                Render();
            }

            /// <summary>After edits: pieces whose lines are gone (or were never found) are looked for again; the others keep following their lines.</summary>
            private void OnRelocate(object? sender, EventArgs args)
            {
                relocate.Stop();
                if (closed || !Enabled) return;
                var snapshot = view.TextSnapshot;
                if (snapshot.Length > MaxChars) { Clear(); return; }
                KnowledgeLineMatcher? matcher = null;
                var changed = false;
                foreach (var entry in entries)
                {
                    if (entry.Span != null && !entry.Span.GetSpan(snapshot).IsEmpty) continue;
                    matcher ??= CreateMatcher(snapshot);
                    var range = matcher.Find(entry.Mark);
                    var span = range == null ? null : KnowledgeLineSpans.Track(snapshot, range.Value);
                    if (span == null && entry.Span == null) continue;
                    entry.Span = span;
                    changed = true;
                }
                if (changed) Render();
            }

            private static KnowledgeLineMatcher CreateMatcher(ITextSnapshot snapshot) => new KnowledgeLineMatcher(snapshot.LineCount, number => snapshot.GetLineFromLineNumber(number).GetText());

            private static Brush? BrushFor(string? color)
            {
                if (color == null || color.Length != 7 || color[0] != '#' || !int.TryParse(color.Substring(1), NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out var rgb)) return null;
                lock (BrushCache)
                {
                    if (!BrushCache.TryGetValue(rgb, out var brush))
                    {
                        brush = new SolidColorBrush(Color.FromArgb(Alpha, (byte)(rgb >> 16), (byte)(rgb >> 8), (byte)rgb));
                        brush.Freeze();
                        BrushCache[rgb] = brush;
                    }
                    return brush;
                }
            }

            private void Clear() { generation++; entries = new List<Entry>(); layer.RemoveAllAdornments(); }

            /// <summary>
            /// One band across the viewport behind the text of each view line of every found range. Lines that follow each other form one band;
            /// the space a line transform adds under a line (code captions) is not covered.
            /// </summary>
            private void Render()
            {
                if (closed || view.InLayout) return;
                layer.RemoveAllAdornments();
                var lines = view.TextViewLines;
                if (entries.Count == 0 || !Enabled || lines == null || lines.Count == 0) return;
                // Lines of an older snapshot (the text changed and the view has not laid out yet): its next layout draws.
                var snapshot = lines.FormattedSpan.Snapshot;
                var left = view.ViewportLeft;
                var width = view.ViewportWidth;
                if (snapshot != view.TextSnapshot || snapshot.Length > MaxChars || width <= 0) return;
                var numbers = new int[lines.Count];
                for (var i = 0; i < numbers.Length; i++) numbers[i] = snapshot.GetLineNumberFromPosition(lines[i].Start.Position);
                foreach (var entry in entries)
                {
                    if (entry.Span == null) continue;
                    var span = entry.Span.GetSpan(snapshot);
                    if (span.IsEmpty) continue;
                    var covered = KnowledgeLineSpans.LinesOf(snapshot, span.Span);
                    SnapshotPoint? runStart = null;
                    SnapshotPoint runEnd = default;
                    double top = 0, bottom = 0;
                    for (var i = 0; i < numbers.Length; i++)
                    {
                        var line = lines[i];
                        var inside = numbers[i] >= covered.First && numbers[i] <= covered.Last;
                        if (inside && runStart != null && Math.Abs(line.TextTop - bottom) < 0.5) { bottom = line.TextBottom; runEnd = line.EndIncludingLineBreak; continue; }
                        if (runStart != null) AddBand(entry, runStart.Value, runEnd, top, bottom, left, width);
                        runStart = null;
                        if (!inside) continue;
                        runStart = line.Start; runEnd = line.EndIncludingLineBreak; top = line.TextTop; bottom = line.TextBottom;
                    }
                    if (runStart != null) AddBand(entry, runStart.Value, runEnd, top, bottom, left, width);
                }
            }

            private void AddBand(Entry entry, SnapshotPoint start, SnapshotPoint end, double top, double bottom, double left, double width)
            {
                if (bottom <= top) return;
                var band = new Border { Width = width, Height = bottom - top, Background = entry.Brush, IsHitTestVisible = false };
                Canvas.SetLeft(band, left); Canvas.SetTop(band, top);
                layer.AddAdornment(AdornmentPositioningBehavior.TextRelative, new SnapshotSpan(start, end), entry, band, null);
            }

            private void OnClosed(object sender, EventArgs args)
            {
                if (closed) return; closed = true;
                refresh.Stop(); relocate.Stop(); refresh.Tick -= OnRefresh; relocate.Tick -= OnRelocate;
                view.LayoutChanged -= OnLayout; view.ViewportWidthChanged -= OnViewport; view.TextBuffer.Changed -= OnTextChanged; view.GotAggregateFocus -= OnFocus; view.Closed -= OnClosed;
                if (editor != null) editor.KnowledgeChanged -= OnSourceChanged;
                if (settings != null) settings.Changed -= OnSourceChanged;
                if (document != null) document.FileActionOccurred -= OnFileAction;
                editor = null; settings = null; document = null;
                entries = new List<Entry>(); layer.RemoveAllAdornments();
            }

            /// <summary>One piece in this document: its color and the lines it sits on now.</summary>
            private sealed class Entry
            {
                public Entry(KnowledgeLineMark mark, Brush brush, ITrackingSpan? span) { Mark = mark; Brush = brush; Span = span; }
                public KnowledgeLineMark Mark { get; }
                public Brush Brush { get; }
                /// <summary>Follows edits; null while the lines are not found.</summary>
                public ITrackingSpan? Span { get; set; }
            }
        }
    }
}
