using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using EnvDTE;
using EnvDTE80;
using Microsoft.VisualStudio.ComponentModelHost;
using Microsoft.VisualStudio.Editor;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Shell.Interop;
using Microsoft.VisualStudio.Text;
using Microsoft.VisualStudio.TextManager.Interop;
using Pomp.Diagnostics;
using Pomp.Features;
using Pomp.Hosting;
using Pomp.Ui;
using Pomp.VisualStudio.Adapters;
using Pomp.VisualStudio.Composition;
using Pomp.VisualStudio.Editor;
using Pomp.VisualStudio.Interop;
using Pomp.VisualStudio.ToolWindow;
using Task = System.Threading.Tasks.Task;

namespace Pomp.VisualStudio.Commands
{
    /// <summary>What the Pomp commands do; used by the menu commands and by the chat page's Alt+J shortcut.</summary>
    internal sealed class PompCommandActions
    {
        private readonly PompPackage package;
        private readonly PompServices services;

        public PompCommandActions(PompPackage package, PompServices services)
        {
            this.package = package;
            this.services = services;
        }

        /// <summary>Shows (creating if needed) the chat tool window and optionally moves focus into it.</summary>
        public async Task ShowChatAsync(bool focus)
        {
            await services.JoinableTaskFactory.SwitchToMainThreadAsync(package.DisposalToken);

            var window = await package.ShowToolWindowAsync(typeof(PompChatToolWindow), 0, true, package.DisposalToken);
            if (window == null)
            {
                throw new InvalidOperationException("The POMP tool window could not be created.");
            }

            if (window.Frame is IVsWindowFrame frame)
            {
                frame.Show();
            }

            if (focus && window is PompChatToolWindow chat)
            {
                chat.Control.FocusChat();
            }
        }

        /// <summary>Editor to chat when the editor (or anything else) has focus; chat to editor when the chat has it.</summary>
        public async Task ToggleFocusAsync()
        {
            await services.JoinableTaskFactory.SwitchToMainThreadAsync(package.DisposalToken);

            var existing = await package.FindToolWindowAsync(typeof(PompChatToolWindow), 0, false, package.DisposalToken);
            if (existing is PompChatToolWindow chat && await chat.Control.IsChatFocusedAsync())
            {
                await FocusEditorAsync();
                return;
            }

            await ShowChatAsync(focus: true);
        }

        /// <summary>Starts a fresh omp session by sending <c>session/new</c> to the session as if the page had.</summary>
        public async Task NewSessionAsync()
        {
            await ShowChatAsync(focus: false);
            await services.JoinableTaskFactory.SwitchToMainThreadAsync(package.DisposalToken);

            if (await package.FindToolWindowAsync(typeof(PompChatToolWindow), 0, false, package.DisposalToken) is PompChatToolWindow chat)
            {
                chat.Control.RequestNewSession();
            }
        }

        /// <summary>
        /// The POMP prompt at the selection with "ask in chat" as its main action (its other action proposes an edit, unavailable where
        /// the code cannot be edited). Without a selection the question's code is selected first (<see cref="CodeScopeSelection"/>).
        /// Without an editor to show it in the chat opens with the editor context switched on, for the user's own question.
        /// </summary>
        public async Task ShowInlineEditAsync()
        {
            await services.JoinableTaskFactory.SwitchToMainThreadAsync(package.DisposalToken);
            var view = InlineEditorFeature.LastFocusedView;
            if (view != null) await SelectQuestionScopeAsync(view);
            if (InlineEditorFeature.TryShowInlinePrompt(PromptAction.Ask)) return;
            await PrefillChatAsync(new ComposerPrefillPayload { IncludeEditorContext = true });
        }

        // ---- the code editor's POMP menu ----------------------------------------------------------------------------------

        /// <summary>Asks the chat to explain the selected code; the message is sent at once with the editor context.</summary>
        public Task ExplainCodeAsync() => SendCodeRequestAsync(
            "에디터에서 선택한 코드를 설명해 줘. 무엇을 하는지, 입력과 결과, 주의할 점을 짧게 정리해 줘.",
            "Explain the code selected in the editor: what it does, its inputs and results, and anything to watch out for.");

        /// <summary>Asks the chat to review the selected code without changing it; sent at once with the editor context.</summary>
        public Task ReviewCodeAsync() => SendCodeRequestAsync(
            "에디터에서 선택한 코드를 리뷰해 줘. 버그, 예외 처리, 보안, 성능 문제를 근거와 함께 알려 주고, 코드는 직접 바꾸지 마.",
            "Review the code selected in the editor: point out bugs, error handling, security and performance problems with reasons. Do not change the code.");

        /// <summary>Asks the chat to fix the selected code as a review proposal; sent at once with the editor context.</summary>
        public Task FixCodeAsync() => SendCodeRequestAsync(
            "에디터에서 선택한 코드의 문제를 찾아 고쳐 줘. 원인을 먼저 짧게 설명하고, 바꾼 내용은 내가 검토할 수 있게 변경 제안으로 올려 줘.",
            "Find and fix the problem in the code selected in the editor. Briefly explain the cause first, and stage the change as a proposal for me to review.");

        /// <summary>Asks the chat to write unit tests for the selected code as a review proposal; sent at once with the editor context.</summary>
        public Task WriteTestsAsync() => SendCodeRequestAsync(
            "에디터에서 선택한 코드의 단위 테스트를 작성해 줘. 이 솔루션의 테스트 프레임워크와 기존 테스트 위치·스타일을 따르고, 정상·경계·오류 경우를 다뤄 줘. 바꾼 내용은 내가 검토할 수 있게 변경 제안으로 올려 줘.",
            "Write unit tests for the code selected in the editor. Follow this solution's test framework and the existing test locations and style, and cover normal, edge and error cases. Stage the change as a proposal for me to review.");

        /// <summary>One of the code requests: the question's code is selected when nothing is, then the request is sent with the editor context.</summary>
        private async Task SendCodeRequestAsync(string korean, string english)
        {
            var view = await ActiveViewAsync();
            await SelectQuestionScopeAsync(view);
            // Nothing could be selected (a blank line between members, no editor): the request names the code at the caret instead of
            // a selection that does not exist.
            var text = PompLanguage.Pick(korean, english);
            if (view == null || view.IsClosed || view.Selection.IsEmpty)
                text = text.Replace("에디터에서 선택한 코드", "에디터 커서 위치의 코드").Replace("the code selected in the editor", "the code at the editor's caret");
            await PrefillChatAsync(new ComposerPrefillPayload { Text = text, IncludeEditorContext = true, Send = true });
        }

        /// <summary>Selects the code a question is about when the editor has no selection (<see cref="CodeScopeSelection"/>); never fails the command.</summary>
        private async Task SelectQuestionScopeAsync(Microsoft.VisualStudio.Text.Editor.IWpfTextView? view)
        {
            if (view == null) return;
            await services.JoinableTaskFactory.SwitchToMainThreadAsync(package.DisposalToken);
            if (!(await package.GetServiceAsync(typeof(SComponentModel)) is IComponentModel components)) return;
            try { await CodeScopeSelection.SelectAsync(view, components, services.JoinableTaskFactory, package.DisposalToken); }
            catch (Exception ex) when (!(ex is OperationCanceledException)) { services.Logger.Debug("Selecting the question's code failed: " + ex.Message); }
            await services.JoinableTaskFactory.SwitchToMainThreadAsync(package.DisposalToken);
        }

        /// <summary>A question from the POMP prompt: into the chat input with the editor context on, sent at once when <paramref name="send"/>.</summary>
        public Task AskInChatAsync(string text, bool send) => PrefillChatAsync(new ComposerPrefillPayload { Text = text, IncludeEditorContext = true, Send = send });

        /// <summary>The editor menu's "to agent or team": the chat input with that one-message send target and the editor context on.</summary>
        public Task SendToTargetAsync(string kind, string id) => PrefillChatAsync(new ComposerPrefillPayload { IncludeEditorContext = true, Target = new ComposerPrefillTarget { Kind = kind, Id = id } });

        /// <summary>The editor menu's "show all": the chat's send-target picker, with the editor context on.</summary>
        public Task PickTargetAsync() => PrefillChatAsync(new ComposerPrefillPayload { IncludeEditorContext = true, PickTarget = true });

        /// <summary>Adds the active file to the chat's context basket.</summary>
        public async Task AddFileToContextAsync()
        {
            var file = await ActiveFileAsync();
            if (file == null)
            {
                await SetStatusAsync(PompLanguage.Pick("POMP: 저장된 파일이 열린 편집기가 없어요.", "POMP: no editor with a saved file is active."));
                return;
            }

            await PrefillChatAsync(new ComposerPrefillPayload { Files = new List<ComposerPrefillFile> { file } });
        }

        /// <summary>
        /// The editor menu's "add to knowledge": the chat window with Tools › Knowledge's add form open and the selected lines (the caret line
        /// when nothing is selected) as the piece; there the person picks the knowledge by name or names a new one.
        /// </summary>
        public async Task AddToKnowledgeAsync()
        {
            if (!(services.KnowledgeEditor is IKnowledgeEditor editor)) return;
            var lines = await ReadKnowledgeLinesAsync();
            if (lines == null) return;

            await ShowChatAsync(focus: true);
            await services.JoinableTaskFactory.SwitchToMainThreadAsync(package.DisposalToken);
            editor.OpenKnowledgeDraft(lines);
        }

        /// <summary>The lines for a knowledge piece (see <see cref="ActiveLinesAsync"/>); null when there are none, with the reason in the status bar.</summary>
        private async Task<KnowledgeDraft?> ReadKnowledgeLinesAsync()
        {
            try
            {
                var lines = await ActiveLinesAsync();
                if (lines == null) await SetStatusAsync(PompLanguage.Pick("POMP: 저장된 파일이 열린 편집기가 없어요.", "POMP: no editor with a saved file is active."));
                return lines;
            }
            catch (InvalidOperationException ex)
            {
                await SetStatusAsync("POMP: " + FirstLine(ex.Message));
                return null;
            }
        }

        /// <summary>
        /// The selected lines of the active text editor, or its caret line when nothing is selected, with their text and the document's full
        /// path (a selection that ends at column 1 leaves that line out, as the chat's editor context does). Null without an editor of a file.
        /// </summary>
        private async Task<KnowledgeDraft?> ActiveLinesAsync()
        {
            var active = await ActiveFileViewAndPathAsync();
            await services.JoinableTaskFactory.SwitchToMainThreadAsync(package.DisposalToken);
            return active == null ? null : KnowledgeLineFeature.ReadLines(active.Value.View, active.Value.Path);
        }

        /// <summary>The active text editor and the full path of its file; null without an editor of a file (on the UI thread).</summary>
        private async Task<(Microsoft.VisualStudio.Text.Editor.IWpfTextView View, string Path)?> ActiveFileViewAndPathAsync()
        {
            var view = await ActiveViewAsync();
            if (view == null || !(await package.GetServiceAsync(typeof(SComponentModel)) is IComponentModel components)
                || !components.GetService<ITextDocumentFactoryService>().TryGetTextDocument(view.TextBuffer, out var document)) return null;
            var path = VsAdapterContext.IsFilePath(document.FilePath) ? VsAdapterContext.NormalizePath(document.FilePath) : null;
            if (path == null) return null;
            return (view, path);
        }

        /// <summary>The last active text editor (the one the editor menu was opened in), or null (on the UI thread).</summary>
        private async Task<Microsoft.VisualStudio.Text.Editor.IWpfTextView?> ActiveViewAsync()
        {
            await services.JoinableTaskFactory.SwitchToMainThreadAsync(package.DisposalToken);
            if (!(await package.GetServiceAsync(typeof(SVsTextManager)) is IVsTextManager textManager)
                || !(await package.GetServiceAsync(typeof(SComponentModel)) is IComponentModel components)) return null;
            // fMustHaveFocus = 0: the last active text view is the editor the menu was opened in.
            if (Microsoft.VisualStudio.ErrorHandler.Failed(textManager.GetActiveView(0, null, out var native)) || native == null) return null;
            var view = components.GetService<IVsEditorAdaptersFactoryService>().GetWpfTextView(native);
            return view == null || view.IsClosed ? null : view;
        }

        private static string FirstLine(string message)
        {
            var end = message.IndexOfAny(new[] { '\r', '\n' });
            return end >= 0 ? message.Substring(0, end) : message;
        }

        public async Task ToggleInlineCompletionsAsync()
        {
            await services.JoinableTaskFactory.SwitchToMainThreadAsync(package.DisposalToken);
            var on = !services.Settings.InlineCompletionsEnabled;
            services.Settings.SetEditorFeatures(inlineCompletions: on);
            await SetStatusAsync(on
                ? PompLanguage.Pick("POMP: 인라인 자동완성을 켰어요.", "POMP: inline completions are on.")
                : PompLanguage.Pick("POMP: 인라인 자동완성을 껐어요.", "POMP: inline completions are off."));
        }

        public async Task ToggleCodeCaptionsAsync()
        {
            await services.JoinableTaskFactory.SwitchToMainThreadAsync(package.DisposalToken);
            var on = !services.Settings.CodeCaptionsEnabled;
            services.Settings.SetEditorFeatures(codeCaptions: on);
            await SetStatusAsync(on
                ? PompLanguage.Pick("POMP: 코드 캡션을 켰어요.", "POMP: code captions are on.")
                : PompLanguage.Pick("POMP: 코드 캡션을 껐어요.", "POMP: code captions are off."));
        }

        public async Task ToggleLiveFollowAsync()
        {
            await services.JoinableTaskFactory.SwitchToMainThreadAsync(package.DisposalToken);
            var on = !services.Settings.AppliedLiveFollowEnabled;
            services.Settings.SetLiveEditorFeatures(live: on);
            await SetStatusAsync(on
                ? PompLanguage.Pick("POMP: Live 변경 따라가기를 켰어요.", "POMP: Live edit following is on.")
                : PompLanguage.Pick("POMP: Live 변경 따라가기를 껐어요.", "POMP: Live edit following is off."));
        }

        public async Task ToggleLiveBookmarksAsync()
        {
            await services.JoinableTaskFactory.SwitchToMainThreadAsync(package.DisposalToken);
            var on = !services.Settings.AppliedLiveBookmarksEnabled;
            services.Settings.SetLiveEditorFeatures(bookmarks: on);
            await SetStatusAsync(on
                ? PompLanguage.Pick("POMP: 변경 북마크를 켰어요.", "POMP: edit bookmarks are on.")
                : PompLanguage.Pick("POMP: 변경 북마크를 껐어요.", "POMP: edit bookmarks are off."));
        }

        /// <summary>Hides or shows the Live change display; hiding never clears change traces.</summary>
        public async Task ToggleLiveChangesAsync()
        {
            await services.JoinableTaskFactory.SwitchToMainThreadAsync(package.DisposalToken);
            var on = !services.Settings.AppliedLiveChangesVisible;
            services.Settings.SetLiveEditorFeatures(changesVisible: on);
            await SetStatusAsync(on
                ? PompLanguage.Pick("POMP: Live 변경 내용을 표시해요.", "POMP: Live changes are shown.")
                : PompLanguage.Pick("POMP: Live 변경 내용을 숨겼어요. 변경 기록은 그대로예요.", "POMP: Live changes are hidden; the change record is kept."));
        }

        /// <summary>Drops this conversation's Live change marks in every file (the code stays as it is) and reports how many.</summary>
        public async Task ClearLiveChangesAsync()
        {
            await services.JoinableTaskFactory.SwitchToMainThreadAsync(package.DisposalToken);
            var cleared = services.EditorFollow.ClearChanges();
            await SetStatusAsync(PompLanguage.Pick("POMP: 변경 내용 표시 " + cleared + "개를 지웠어요. 코드는 그대로예요.", "POMP: cleared " + cleared + " Live change mark(s); the code is unchanged."));
        }

        /// <summary>Removes POMP's bookmarks (the user's own are kept) and reports the count or the failure in the status bar.</summary>
        public async Task CleanupPompBookmarksAsync()
        {
            await services.JoinableTaskFactory.SwitchToMainThreadAsync(package.DisposalToken);
            string message;
            try
            {
                var removed = services.EditorFollow.CleanupPompBookmarks();
                message = PompLanguage.Pick("POMP: 책갈피 " + removed + "개를 정리했어요.", "POMP: removed " + removed + " POMP bookmark(s).");
            }
            catch (Exception ex)
            {
                services.Logger.Error("Cleaning up POMP bookmarks failed.", ex);
                message = PompLanguage.Pick("POMP: 책갈피 정리 실패 - " + ex.Message, "POMP: bookmark cleanup failed - " + ex.Message);
            }
            await SetStatusAsync(message);
        }

        /// <summary>Shows the chat and fills its input (the page gets it now, or with its ready snapshot when the window was just opened).</summary>
        private async Task PrefillChatAsync(ComposerPrefillPayload prefill)
        {
            await ShowChatAsync(focus: true);
            if (services.Session is PompSession session) session.PrefillComposer(prefill);
        }

        /// <summary>
        /// Ctrl+V of files or an image in the chat input: the clipboard's files (copied in Explorer or Solution Explorer) or its image (a
        /// screenshot, a copied picture) go into the context basket; the session saves an image as a PNG in <c>.pomp/local/clipboard</c>.
        /// </summary>
        public async Task PasteIntoBasketAsync()
        {
            await services.JoinableTaskFactory.SwitchToMainThreadAsync(package.DisposalToken);
            var paths = new List<string>(); byte[]? png = null;
            try
            {
                if (System.Windows.Clipboard.ContainsFileDropList())
                {
                    foreach (var path in System.Windows.Clipboard.GetFileDropList()) if (!string.IsNullOrEmpty(path)) paths.Add(path!);
                }
                else if (System.Windows.Clipboard.GetData("PNG") is MemoryStream data)
                {
                    png = data.ToArray(); // the image as the source program encoded it (transparency kept)
                }
                else if (System.Windows.Clipboard.ContainsImage() && System.Windows.Clipboard.GetImage() is System.Windows.Media.Imaging.BitmapSource image)
                {
                    var encoder = new System.Windows.Media.Imaging.PngBitmapEncoder();
                    encoder.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(image));
                    using var stream = new MemoryStream();
                    encoder.Save(stream);
                    png = stream.ToArray();
                }
            }
            catch (Exception ex) when (ex is System.Runtime.InteropServices.ExternalException || ex is InvalidOperationException || ex is NotSupportedException)
            {
                services.Logger.Warn("The clipboard could not be read: " + ex.Message);
                return;
            }
            if ((paths.Count > 0 || png != null) && services.Session is PompSession session) await session.PasteIntoBasketAsync(paths, png, package.DisposalToken);
        }

        /// <summary>Files dragged from Explorer onto the chat page: into the context basket, like pasted files.</summary>
        public async Task DropIntoBasketAsync(IReadOnlyList<string> paths)
        {
            if (services.Session is PompSession session) await session.PasteIntoBasketAsync(paths, null, package.DisposalToken);
        }

        /// <summary>The active document as a context-basket file (workspace-relative path with forward slashes when inside the workspace).</summary>
        private async Task<ComposerPrefillFile?> ActiveFileAsync()
        {
            await services.JoinableTaskFactory.SwitchToMainThreadAsync(package.DisposalToken);
            var dte = await package.GetServiceAsync(typeof(SDTE)) as DTE2;
            string? path;
            try { path = dte?.ActiveDocument?.FullName; }
            catch (Exception ex) when (ex is System.Runtime.InteropServices.COMException || ex is ArgumentException) { path = null; }
            if (string.IsNullOrEmpty(path) || !File.Exists(path)) return null;
            var full = Path.GetFullPath(path);
            var root = await services.Environment.GetWorkspaceRootAsync(package.DisposalToken);
            var prefix = string.IsNullOrEmpty(root) ? null : Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
            var relative = prefix != null && full.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) ? full.Substring(prefix.Length).Replace('\\', '/') : full;
            return new ComposerPrefillFile { Path = relative, FullPath = full };
        }

        private async Task SetStatusAsync(string text)
        {
            await services.JoinableTaskFactory.SwitchToMainThreadAsync(package.DisposalToken);
            if (await package.GetServiceAsync(typeof(SVsStatusbar)) is IVsStatusbar statusbar) statusbar.SetText(text);
        }

        private async Task FocusEditorAsync()
        {
            await services.JoinableTaskFactory.SwitchToMainThreadAsync(package.DisposalToken);

            var dte = await package.GetServiceAsync(typeof(SDTE)) as DTE2;
            if (dte == null)
            {
                return;
            }

            try
            {
                var document = dte.ActiveDocument;
                if (document != null)
                {
                    document.Activate();
                    return;
                }

                // No document is open: hand focus back to the main window instead of leaving it in the chat.
                dte.MainWindow.Activate();
            }
            catch (Exception ex)
            {
                services.Logger.Warn("Returning focus to the editor failed.", ex);
            }
        }
    }
}
