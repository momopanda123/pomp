using System;
using System.ComponentModel.Composition;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using Microsoft.VisualStudio;
using Microsoft.VisualStudio.Language.Intellisense;
using Microsoft.VisualStudio.Language.Intellisense.AsyncCompletion;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Shell.Interop;
using Microsoft.VisualStudio.Text;
using Microsoft.VisualStudio.Text.Editor;
using Microsoft.VisualStudio.Text.Operations;
using Microsoft.VisualStudio.Utilities;
using Pomp.Adapters;
using Pomp.Diagnostics;
using Pomp.Features;
using Pomp.Hosting;
using Pomp.VisualStudio.Composition;
using Pomp.VisualStudio.Adapters;
using Task = System.Threading.Tasks.Task;

namespace Pomp.VisualStudio.Editor
{
    [Export(typeof(IWpfTextViewCreationListener))]
    [ContentType("code")]
    [TextViewRole(PredefinedTextViewRoles.Editable)]
    internal sealed class InlineEditorFeature : IWpfTextViewCreationListener
    {
        private static WeakReference<ViewController>? lastFocused;
        /// <summary>The POMP prompt at the selection of the last focused editor; false when there is none (or "edit" cannot run there).</summary>
        public static bool TryShowInlinePrompt(PromptAction main = PromptAction.Ask)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            if (lastFocused == null || !lastFocused.TryGetTarget(out var controller)) return false;
            return controller.OpenPrompt(main);
        }

        /// <summary>An editor command of this view (from InlineCompletionKeys): true when the inline completion took it.</summary>
        internal static bool HandleEditorCommand(ITextView view, VSConstants.VSStd2KCmdID command)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            return view.Properties.TryGetProperty(typeof(ViewController), out ViewController controller) && controller.HandleCommand(command);
        }

        internal const string LayerName = "Pomp.InlineSuggestions";
        [Export(typeof(AdornmentLayerDefinition))]
        [Name(LayerName)]
        [Order(After = PredefinedAdornmentLayers.Selection, Before = PredefinedAdornmentLayers.Text)]
        public AdornmentLayerDefinition Layer = null!;
        [Import] public ITextDocumentFactoryService Documents = null!;
        [Import] public ITextBufferUndoManagerProvider UndoManagers = null!;
        [Import] public ICompletionBroker Completion = null!;
        [Import] public IAsyncCompletionBroker AsyncCompletion = null!;
        public void TextViewCreated(IWpfTextView textView) => _ = new ViewController(textView, Documents, UndoManagers, Completion, AsyncCompletion);

        private sealed class ViewController
        {
            private readonly IWpfTextView view;
            private readonly ITextDocumentFactoryService documents;
            private readonly ITextBufferUndoManagerProvider undoManagers;
            private readonly ICompletionBroker completion;
            private readonly IAsyncCompletionBroker asyncCompletion;
            private readonly IAdornmentLayer layer;
            private readonly DispatcherTimer debounce;
            private CancellationTokenSource? pending;
            private CancellationTokenSource? editRequest;
            private string suggestion = string.Empty;
            private ITextSnapshot? suggestionSnapshot;
            private int suggestionPosition;
            private bool applying, composing, closed, prompting;

            public ViewController(IWpfTextView view, ITextDocumentFactoryService documents, ITextBufferUndoManagerProvider undoManagers,
                ICompletionBroker completion, IAsyncCompletionBroker asyncCompletion)
            {
                this.view = view; this.documents = documents; this.undoManagers = undoManagers; this.completion = completion; this.asyncCompletion = asyncCompletion;
                layer = view.GetAdornmentLayer(LayerName);
                debounce = new DispatcherTimer(DispatcherPriority.Background, view.VisualElement.Dispatcher) { Interval = TimeSpan.FromMilliseconds(320) };
                debounce.Tick += OnTimer;
                view.TextBuffer.Changed += OnChanged;
                view.Caret.PositionChanged += OnCaret;
                view.LayoutChanged += OnLayout;
                view.LostAggregateFocus += OnLostFocus;
                view.GotAggregateFocus += OnGotFocus;
                if (view.HasAggregateFocus) lastFocused = new WeakReference<ViewController>(this);
                view.Properties.AddProperty(typeof(ViewController), this);
                view.Closed += OnClosed;
                view.VisualElement.PreviewKeyDown += OnKeyDown;
                TextCompositionManager.AddPreviewTextInputStartHandler(view.VisualElement, OnCompositionStart);
                TextCompositionManager.AddPreviewTextInputHandler(view.VisualElement, OnCompositionEnd);
            }
            private bool OtherCompletionActive() => completion.IsCompletionActive(view) || asyncCompletion.IsCompletionActive(view);

            /// <summary>Tab accepts the whole suggestion, Ctrl+Right its next word; Escape drops it and still reaches the editor.</summary>
            internal bool HandleCommand(VSConstants.VSStd2KCmdID command)
            {
                ThreadHelper.ThrowIfNotOnUIThread();
                if (suggestion.Length == 0 || composing || closed) return false;
                switch (command)
                {
                    case VSConstants.VSStd2KCmdID.TAB: return !OtherCompletionActive() && Accept(all: true);
                    case VSConstants.VSStd2KCmdID.WORDNEXT: return !OtherCompletionActive() && Accept(all: false);
                    case VSConstants.VSStd2KCmdID.CANCEL: Clear(); CancelPending(); return false;
                    default: return false;
                }
            }
            private void OnCompositionStart(object sender, TextCompositionEventArgs e) { composing = true; Clear(); debounce.Stop(); }
            private void OnCompositionEnd(object sender, TextCompositionEventArgs e) { composing = false; Schedule(); }
            private void OnChanged(object sender, TextContentChangedEventArgs e) { if (!applying) { Clear(); CancelPending(); Schedule(); } }
            private void OnCaret(object sender, CaretPositionChangedEventArgs e) { if (!applying) { Clear(); CancelPending(); } }
            private void OnLayout(object sender, TextViewLayoutChangedEventArgs e) { if (!applying && suggestionSnapshot != null && (suggestionSnapshot != view.TextSnapshot || e.VerticalTranslation || e.HorizontalTranslation)) Clear(); }
            private void OnGotFocus(object sender, EventArgs e) => lastFocused = new WeakReference<ViewController>(this);
            internal bool OpenPrompt(PromptAction main) { ThreadHelper.ThrowIfNotOnUIThread(); if (closed) return false; return ShowPrompt(main); }
            private void OnLostFocus(object sender, EventArgs e) { Clear(); debounce.Stop(); CancelPending(); }
            private void Schedule()
            {
                debounce.Stop();
                if (!closed && !composing && !prompting && view.HasAggregateFocus && view.Selection.IsEmpty && PompServices.Current?.Settings.InlineCompletionsEnabled == true)
                    debounce.Start();
            }
            private void OnTimer(object? sender, EventArgs e)
            {
                debounce.Stop();
                var services = PompServices.Current;
                if (services == null || closed) return;
                if (composing || !view.HasAggregateFocus || !view.Selection.IsEmpty || view.GetMultiSelectionBroker().HasMultipleSelections || OtherCompletionActive())
                {
                    services.Logger.Debug("Inline suggestion not requested: " + (composing ? "IME composition" : !view.HasAggregateFocus ? "no focus" : OtherCompletionActive() ? "IntelliSense is open" : "a selection"));
                    return;
                }
                var snapshot = view.TextSnapshot;
                if (snapshot.Length > 2_000_000) return;
                var position = view.Caret.Position.BufferPosition.Position;
                var request = new InlineRequest { Prefix = snapshot.GetText(Math.Max(0, position - 3200), Math.Min(position, 3200)),
                    Suffix = snapshot.GetText(position, Math.Min(800, snapshot.Length - position)), Language = view.TextBuffer.ContentType.TypeName };
                CancelPending(); pending = new CancellationTokenSource(); var token = pending.Token;
                _ = services.JoinableTaskFactory.RunAsync(async () =>
                {
                    try
                    {
                        var text = await ((IInlineAssistant)services.Session).CompleteInlineAsync(request, token).ConfigureAwait(false);
                        await services.JoinableTaskFactory.SwitchToMainThreadAsync(token);
                        if (closed || composing || !view.HasAggregateFocus || view.TextSnapshot != snapshot || view.Caret.Position.BufferPosition.Position != position || OtherCompletionActive())
                        { services.Logger.Debug("Inline suggestion dropped: the editor changed while it was prepared."); return; }
                        if (string.IsNullOrEmpty(text)) { services.Logger.Debug("Inline suggestion: the model proposed nothing."); return; }
                        text = text.Replace("\r\n", "\n").Replace('\r', '\n');
                        var eol = TextContent.DetectLineEnding(snapshot.GetText(0, Math.Min(snapshot.Length, 8000)));
                        text = text.Replace("\n", eol);
                        suggestion = text.Length <= 2000 ? text : text.Substring(0, 2000);
                        suggestionSnapshot = snapshot; suggestionPosition = position; Render();
                    }
                    catch (OperationCanceledException) { }
                    catch (Exception ex) { services.Logger.Debug("Inline suggestion unavailable: " + ex.Message); }
                });
            }
            private void Render()
            {
                ThreadHelper.ThrowIfNotOnUIThread();
                layer.RemoveAdornmentsByTag(this);
                if (suggestion.Length == 0 || suggestionSnapshot != view.TextSnapshot) return;
                var point = new SnapshotPoint(view.TextSnapshot, suggestionPosition);
                var line = view.GetTextViewLineContainingBufferPosition(point);
                var bounds = line.GetCharacterBounds(point);
                var ghost = new TextBlock { Text = suggestion, IsHitTestVisible = false, Opacity = .6, Foreground = view.FormattedLineSource.DefaultTextProperties.ForegroundBrush,
                    FontFamily = view.FormattedLineSource.DefaultTextProperties.Typeface.FontFamily, FontSize = view.FormattedLineSource.DefaultTextProperties.FontRenderingEmSize };
                Canvas.SetLeft(ghost, bounds.Left); Canvas.SetTop(ghost, line.TextTop);
                layer.AddAdornment(AdornmentPositioningBehavior.TextRelative, new SnapshotSpan(point, 0), this, ghost, null);
            }
            private void Clear() { suggestion = string.Empty; suggestionSnapshot = null; layer.RemoveAdornmentsByTag(this); }
            private void CancelPending() { try { pending?.Cancel(); } catch (ObjectDisposedException) { } pending?.Dispose(); pending = null; }
            private void OnKeyDown(object sender, KeyEventArgs e)
            {
                ThreadHelper.ThrowIfNotOnUIThread();
                if (composing || e.Key == Key.ImeProcessed) return;
                if (e.Key == Key.Escape) { Clear(); CancelPending(); return; }
                if (suggestion.Length == 0 || OtherCompletionActive()) return;
                var all = e.Key == Key.Tab && Keyboard.Modifiers == ModifierKeys.None;
                var word = e.Key == Key.Right && Keyboard.Modifiers == ModifierKeys.Control;
                if ((all || word) && Accept(all)) e.Handled = true;
            }
            private bool Accept(bool all)
            {
                ThreadHelper.ThrowIfNotOnUIThread();
                if (suggestionSnapshot != view.TextSnapshot || suggestionPosition != view.Caret.Position.BufferPosition.Position || !view.Selection.IsEmpty || view.GetMultiSelectionBroker().HasMultipleSelections)
                { Clear(); return false; }
                var text = all ? suggestion : Regex.Match(suggestion, @"^\s*(?:[\p{L}\p{N}_]+|[^\s\p{L}\p{N}_])").Value;
                if (text.Length == 0) return false;
                applying = true;
                try
                {
                    using var undo = undoManagers.GetTextBufferUndoManager(view.TextBuffer).TextBufferUndoHistory.CreateTransaction("Pomp inline completion");
                    using var edit = view.TextBuffer.CreateEdit();
                    if (!edit.Insert(suggestionPosition, text)) return false;
                    edit.Apply();
                    if (edit.Canceled) return false;
                    undo.Complete();
                    suggestion = suggestion.Substring(text.Length); suggestionPosition += text.Length; suggestionSnapshot = view.TextSnapshot;
                    view.Caret.MoveTo(new SnapshotPoint(view.TextSnapshot, Math.Min(suggestionPosition, view.TextSnapshot.Length)));
                    Render(); return true;
                }
                catch (Exception ex) { PompServices.Current?.Logger.Warn("Inline completion could not be applied.", ex); Clear(); return false; }
                finally { applying = false; }
            }
            private bool ShowPrompt(PromptAction main)
            {
                ThreadHelper.ThrowIfNotOnUIThread();
                var services = PompServices.Current;
                if (services == null || closed) return false;
                var snapshot = view.TextSnapshot;
                var spans = view.GetMultiSelectionBroker().AllSelections.Select(selection => selection.Extent.SnapshotSpan).ToArray();
                if (spans.Length == 0) spans = new[] { new SnapshotSpan(snapshot, view.Caret.Position.BufferPosition.Position, 0) };
                // A question only needs the editor context; an edit needs a saved document and bounded selections.
                ITextDocument? document = null;
                var canEdit = documents.TryGetTextDocument(view.TextBuffer, out document) && snapshot.Length <= 500000 && spans.Length <= 5 && spans.All(s => s.Length <= 24000);
                if (main == PromptAction.Edit && !canEdit) return false;
                Clear(); CancelPending(); debounce.Stop();
                var point = view.Caret.Position.BufferPosition; var line = view.GetTextViewLineContainingBufferPosition(point); var bounds = line.GetCharacterBounds(point);
                var anchor = view.VisualElement.PointToScreen(new Point(Math.Max(0, Math.Min(bounds.Left - view.ViewportLeft, view.ViewportWidth)),
                    Math.Max(0, Math.Min(line.Bottom - view.ViewportTop, view.ViewportHeight))));
                prompting = true;
                PromptResult? result;
                try { result = InlineEditPrompt.Ask(view, anchor, main, canEdit); }
                finally { prompting = false; }
                view.VisualElement.Focus();
                if (result == null || closed) return true;
                if (result.Action != PromptAction.Edit)
                {
                    // The chat collects the editor context when the message is sent: the selection is still the one the prompt was about.
                    var text = result.Text; var send = result.Action == PromptAction.Ask;
                    _ = services.JoinableTaskFactory.RunAsync(async () =>
                    {
                        try { await services.Commands.AskInChatAsync(text, send); }
                        catch (Exception ex) { services.Logger.Warn("The question could not be passed to the chat.", ex); }
                    });
                    return true;
                }

                var instruction = result.Text;
                if (document == null) return true;
                if (view.TextSnapshot != snapshot) { SetStatus(PompLanguage.Pick("POMP: 코드가 바뀌어 질문을 취소했어요.", "POMP: the code changed, so the question was cancelled.")); return true; }
                CancelEdit(); editRequest = new CancellationTokenSource(); var token = editRequest.Token;
                SetStatus(PompLanguage.Pick("POMP: 질문의 수정 제안을 준비하는 중", "POMP: preparing an edit proposal for the question"));
                _ = services.JoinableTaskFactory.RunAsync(async () =>
                {
                    try
                    {
                        using var linked = CancellationTokenSource.CreateLinkedTokenSource(token, services.Package.DisposalToken);
                        var source = snapshot.GetText(); var after = source;
                        var eol = TextContent.DetectLineEnding(source.Substring(0, Math.Min(source.Length, 8000)));
                        foreach (var span in spans.OrderByDescending(s => s.Start.Position))
                        {
                            var start = span.Start.Position; var end = span.End.Position;
                            var replacement = await ((IInlineAssistant)services.Session).CompleteInlineAsync(new InlineRequest {
                                IsEdit = true, Prefix = source.Substring(Math.Max(0, start - 12000), Math.Min(start, 12000)), Selection = span.GetText(),
                                Suffix = source.Substring(end, Math.Min(4000, source.Length - end)), Instruction = instruction, Language = snapshot.ContentType.TypeName }, linked.Token).ConfigureAwait(false);
                            await services.JoinableTaskFactory.SwitchToMainThreadAsync(linked.Token);
                            if (closed || view.TextSnapshot != snapshot) throw new OperationCanceledException("The source changed.");
                            after = after.Substring(0, start) + InlineEditText.FitReplacement(replacement, span.GetText(), eol) + after.Substring(end);
                        }
                        await ((IInlineAssistant)services.Session).StageInlineSnapshotAsync(document.FilePath, source, after, linked.Token).ConfigureAwait(false);
                        await services.JoinableTaskFactory.SwitchToMainThreadAsync(linked.Token);
                        SetStatus(PompLanguage.Pick("POMP: 질문의 수정 제안을 검토 화면에 열었어요.", "POMP: the edit proposal for the question is open for review."));
                        await services.Commands.ShowChatAsync(focus: true);
                    }
                    catch (Exception ex)
                    {
                        await services.JoinableTaskFactory.SwitchToMainThreadAsync();
                        if (ex is OperationCanceledException) SetStatus(PompLanguage.Pick("POMP: 질문을 취소했어요.", "POMP: the question was cancelled."));
                        else
                        {
                            services.Logger.Warn("The inline edit failed.", ex);
                            SetStatus(PompLanguage.Pick("POMP: 질문 실패 — ", "POMP: the question failed — ") + ex.Message);
                        }
                    }
                });
                return true;
            }
            private void CancelEdit() { try { editRequest?.Cancel(); } catch (ObjectDisposedException) { } editRequest?.Dispose(); editRequest = null; }
            private static void SetStatus(string text)
            {
                ThreadHelper.ThrowIfNotOnUIThread();
                if (Package.GetGlobalService(typeof(SVsStatusbar)) is IVsStatusbar statusbar) statusbar.SetText(text);
            }
            private void OnClosed(object sender, EventArgs e)
            {
                closed = true; debounce.Stop(); debounce.Tick -= OnTimer; CancelPending(); CancelEdit(); Clear();
                view.TextBuffer.Changed -= OnChanged; view.Caret.PositionChanged -= OnCaret; view.LayoutChanged -= OnLayout;
                view.LostAggregateFocus -= OnLostFocus; view.GotAggregateFocus -= OnGotFocus; view.Closed -= OnClosed; view.Properties.RemoveProperty(typeof(ViewController)); view.VisualElement.PreviewKeyDown -= OnKeyDown;
                TextCompositionManager.RemovePreviewTextInputStartHandler(view.VisualElement, OnCompositionStart);
                TextCompositionManager.RemovePreviewTextInputHandler(view.VisualElement, OnCompositionEnd);
            }
        }
    }
}
