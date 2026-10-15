using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using EnvDTE;
using Microsoft.VisualStudio;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Shell.Interop;
using Microsoft.VisualStudio.Text;
using Microsoft.VisualStudio.Text.Editor;
using Microsoft.VisualStudio.TextManager.Interop;
using Microsoft.VisualStudio.Threading;
using Pomp.Diagnostics;
using Pomp.Adapters;
using Task = System.Threading.Tasks.Task;

namespace Pomp.VisualStudio.Adapters
{
    /// <summary>Active editor, open documents and command execution (design: DteAdapter).</summary>
    internal sealed class EditorAdapter : IEditorAdapter
    {
        /// <summary>Selections longer than this are cut (the prompt does not need megabytes).</summary>
        private const int MaxSelectedTextChars = 20000;

        private readonly VsAdapterContext context;

        public EditorAdapter(VsAdapterContext context)
        {
            this.context = context;
        }

        public async Task<ActiveDocumentInfo?> GetActiveDocumentAsync(CancellationToken cancellationToken)
        {
            await context.Jtf.SwitchToMainThreadAsync(cancellationToken);
            return ReadActiveDocument();
        }

        public async Task<IReadOnlyList<OpenDocumentInfo>> GetOpenDocumentsAsync(CancellationToken cancellationToken)
        {
            await context.Jtf.SwitchToMainThreadAsync(cancellationToken);

            var result = new List<OpenDocumentInfo>();
            foreach (var document in DocumentTable.EnumerateOpen(context))
            {
                cancellationToken.ThrowIfCancellationRequested();
                result.Add(new OpenDocumentInfo { Path = document.Path, IsDirty = IsDirty(document) });
            }

            return result;
        }

        public Task<bool> OpenDocumentAsync(string path, int? line, CancellationToken cancellationToken) =>
            OpenDocumentAsync(context, path, line, cancellationToken);

        /// <summary>
        /// Opens a file in the default editor. The existence check runs off the UI thread (a UNC path can block for the SMB
        /// timeout); only the open itself runs on the UI thread. False when the path is invalid, the file is missing or it did not open.
        /// </summary>
        internal static async Task<bool> OpenDocumentAsync(VsAdapterContext context, string path, int? line, CancellationToken cancellationToken)
        {
            var fullPath = VsAdapterContext.NormalizePath(path);
            if (fullPath == null)
            {
                return false;
            }

            await TaskScheduler.Default;
            cancellationToken.ThrowIfCancellationRequested();

            if (!File.Exists(fullPath))
            {
                return false;
            }

            await context.Jtf.SwitchToMainThreadAsync(cancellationToken);
            return OpenDocumentOnUiThread(context, fullPath, line);
        }

        public async Task<CommandExecutionResult> ExecuteCommandAsync(string commandName, string? arguments, CancellationToken cancellationToken)
        {
            await context.Jtf.SwitchToMainThreadAsync(cancellationToken);

            if (string.IsNullOrWhiteSpace(commandName))
            {
                return new CommandExecutionResult { Succeeded = false, Error = "The command name is empty." };
            }

            try
            {
                var dte = context.Dte;

                Command command;
                try
                {
                    command = dte.Commands.Item(commandName, -1);
                }
                catch (Exception ex) when (ex is ArgumentException || ex is System.Runtime.InteropServices.COMException)
                {
                    return new CommandExecutionResult { Succeeded = false, Error = "Unknown Visual Studio command '" + commandName + "'." };
                }

                if (!command.IsAvailable)
                {
                    return new CommandExecutionResult
                    {
                        Succeeded = false,
                        Error = "The command '" + commandName + "' is not available in the current context (open the right window or document first).",
                    };
                }

                dte.ExecuteCommand(commandName, arguments ?? string.Empty);
                return new CommandExecutionResult { Succeeded = true };
            }
            catch (Exception ex) when (!(ex is OperationCanceledException))
            {
                context.LogUnexpected("Executing the command '" + commandName + "'", ex);
                return new CommandExecutionResult { Succeeded = false, Error = ex.Message };
            }
        }

        /// <summary>
        /// Opens the (already normalized, existing) file <paramref name="fullPath"/> in the default editor and moves the caret to a
        /// 1-based line. UI thread only; no file-system access here.
        /// </summary>
        internal static bool OpenDocumentOnUiThread(VsAdapterContext context, string fullPath, int? line)
        {
            ThreadHelper.ThrowIfNotOnUIThread();

            try
            {
                VsShellUtilities.OpenDocument(context.Package, fullPath, Guid.Empty, out _, out _, out var frame, out var textView);
                if (frame == null)
                {
                    return false;
                }

                // Show without stealing keyboard focus from the chat the user is typing in.
                frame.ShowNoActivate();

                if (line.HasValue && line.Value > 0)
                {
                    var view = textView ?? VsShellUtilities.GetTextView(frame);
                    if (view != null)
                    {
                        var zeroBasedLine = line.Value - 1;
                        view.SetCaretPos(zeroBasedLine, 0);
                        view.CenterLines(zeroBasedLine, 1);
                    }
                }

                return true;
            }
            catch (Exception ex)
            {
                context.LogUnexpected("Opening '" + fullPath + "'", ex);
                return false;
            }
        }

        private static bool IsDirtyDocData(object? docData)
        {
            ThreadHelper.ThrowIfNotOnUIThread();

            return docData is IVsPersistDocData persist
                   && ErrorHandler.Succeeded(persist.IsDocDataDirty(out var dirty))
                   && dirty != 0;
        }

        private bool IsDirty(OpenDocument document)
        {
            ThreadHelper.ThrowIfNotOnUIThread();

            return DocumentTable.IsDirty(context, document.Path) ?? IsDirtyDocData(document.DocData);
        }

        private ActiveDocumentInfo? ReadActiveDocument()
        {
            ThreadHelper.ThrowIfNotOnUIThread();

            var dte = context.Dte;
            Document? document;
            try
            {
                document = dte.ActiveDocument;
            }
            catch (System.Runtime.InteropServices.COMException)
            {
                return null;
            }

            if (document == null)
            {
                return null;
            }

            var path = VsAdapterContext.IsFilePath(document.FullName) ? VsAdapterContext.NormalizePath(document.FullName) : null;
            if (path == null)
            {
                // Untitled or non-file document (e.g. a diff view): not addressable by path.
                return null;
            }

            var info = new ActiveDocumentInfo
            {
                Path = path,
                Language = SafeLanguage(document),
                IsDirty = !document.Saved,
            };

            var view = TryGetTextView(path);
            var documentBuffer = DocumentTable.TryGetTextBuffer(context, path);
            if (view != null && documentBuffer != null)
            {
                FillFromView(info, view, documentBuffer);
            }
            else
            {
                FillFromDte(info, document);
            }

            return info;
        }

        private static string? SafeLanguage(Document document)
        {
            ThreadHelper.ThrowIfNotOnUIThread();

            try
            {
                var language = document.Language;
                return string.IsNullOrEmpty(language) ? null : language;
            }
            catch (System.Runtime.InteropServices.COMException)
            {
                return null;
            }
        }

        /// <summary>The text view of the active document: the last active text view if it shows that file, else the file's frame.</summary>
        private IWpfTextView? TryGetTextView(string path)
        {
            ThreadHelper.ThrowIfNotOnUIThread();

            try
            {
                // fMustHaveFocus = 0: the chat tool window usually has focus while the agent asks, the editor's last view is what we want.
                if (ErrorHandler.Succeeded(context.TextManager.GetActiveView(0, null, out var vsView)) && vsView != null)
                {
                    var wpfView = context.EditorAdapters.GetWpfTextView(vsView);
                    if (wpfView != null && ShowsFile(wpfView, path))
                    {
                        return wpfView;
                    }
                }

                var frame = DocumentTable.FindFrame(context, path);
                return frame == null ? null : DocumentTable.TryGetWpfTextView(context, frame);
            }
            catch (Exception ex) when (!(ex is OperationCanceledException))
            {
                context.LogUnexpected("Resolving the active text view", ex);
                return null;
            }
        }

        private bool ShowsFile(IWpfTextView view, string path)
        {
            ThreadHelper.ThrowIfNotOnUIThread();

            var documentBuffer = DocumentTable.TryGetTextBuffer(context, path);
            if (documentBuffer == null)
            {
                return false;
            }

            if (view.BufferGraph.GetTextBuffers(buffer => buffer == documentBuffer).Count > 0)
            {
                return true;
            }

            return false;
        }

        private static void FillFromView(ActiveDocumentInfo info, IWpfTextView view, ITextBuffer documentBuffer)
        {
            var snapshot = documentBuffer.CurrentSnapshot;
            info.LineCount = snapshot.LineCount;

            var caret = ToDocumentPoint(view, documentBuffer, view.Caret.Position.BufferPosition);
            info.Caret = ToPosition(caret ?? new SnapshotPoint(snapshot, 0));

            if (view.Selection.IsEmpty)
            {
                return;
            }

            var selection = view.Selection.StreamSelectionSpan.SnapshotSpan;
            var start = ToDocumentPoint(view, documentBuffer, selection.Start);
            var end = ToDocumentPoint(view, documentBuffer, selection.End);
            if (start == null || end == null)
            {
                return;
            }

            info.SelectionStart = ToPosition(start.Value);
            info.SelectionEnd = ToPosition(end.Value);

            if (start.Value.Snapshot == end.Value.Snapshot)
            {
                var from = Math.Min(start.Value.Position, end.Value.Position);
                var length = Math.Abs(end.Value.Position - start.Value.Position);
                var take = Math.Min(length, MaxSelectedTextChars);
                var text = start.Value.Snapshot.GetText(from, take);
                info.SelectedText = length > take ? text + "... [truncated " + (length - take) + " chars]" : text;
            }
        }

        private static SnapshotPoint? ToDocumentPoint(IWpfTextView view, ITextBuffer documentBuffer, SnapshotPoint viewPoint)
        {
            if (viewPoint.Snapshot.TextBuffer == documentBuffer)
            {
                return viewPoint;
            }

            // Projection buffers (Razor, HTML with embedded languages): map down to the buffer that backs the file.
            return view.BufferGraph.MapDownToBuffer(viewPoint, PointTrackingMode.Positive, documentBuffer, PositionAffinity.Successor);
        }

        /// <summary>
        /// 1-based line and column of <paramref name="point"/> in the host tools' line model: only CR, LF and CRLF end a line. The
        /// editor also breaks lines at U+0085, U+2028 and U+2029, so its own line numbers would not match the rows read_buffer shows.
        /// </summary>
        private static TextPosition ToPosition(SnapshotPoint point)
        {
            var snapshot = point.Snapshot;
            var containing = point.GetContainingLine();
            var line = 1;
            var lineStart = 0;
            for (var number = 0; number < containing.LineNumber; number++)
            {
                var previous = snapshot.GetLineFromLineNumber(number);
                var breakChar = snapshot[previous.End.Position];
                if (breakChar == '\r' || breakChar == '\n')
                {
                    line++;
                    lineStart = previous.EndIncludingLineBreak.Position;
                }
            }

            return new TextPosition { Line = line, Column = (point.Position - lineStart) + 1 };
        }

        /// <summary>
        /// <paramref name="point"/> of a DTE text document in the same CR/LF-only line model as <see cref="ToPosition(SnapshotPoint)"/>
        /// (DTE's own line numbers also count U+0085, U+2028 and U+2029 as line breaks).
        /// </summary>
        private static TextPosition ToPosition(TextDocument document, TextPoint point)
        {
            ThreadHelper.ThrowIfNotOnUIThread();

            var before = document.StartPoint.CreateEditPoint().GetText(point);
            var line = 1;
            var lineStart = 0;
            for (var i = 0; i < before.Length; i++)
            {
                var c = before[i];
                if (c != '\r' && c != '\n')
                {
                    continue;
                }

                if (c == '\r' && i + 1 < before.Length && before[i + 1] == '\n')
                {
                    i++;
                }

                line++;
                lineStart = i + 1;
            }

            return new TextPosition { Line = line, Column = before.Length - lineStart + 1 };
        }

        private static void FillFromDte(ActiveDocumentInfo info, Document document)
        {
            ThreadHelper.ThrowIfNotOnUIThread();

            try
            {
                if (!(document.Object("TextDocument") is TextDocument textDocument))
                {
                    return;
                }

                var selection = textDocument.Selection;
                info.Caret = ToPosition(textDocument, selection.ActivePoint);
                info.LineCount = textDocument.EndPoint.Line;

                if (!selection.IsEmpty)
                {
                    info.SelectionStart = ToPosition(textDocument, selection.TopPoint);
                    info.SelectionEnd = ToPosition(textDocument, selection.BottomPoint);
                    info.SelectedText = TextContent.Truncate(selection.Text, MaxSelectedTextChars);
                }
            }
            catch (System.Runtime.InteropServices.COMException)
            {
                // Non-text document: caret and selection stay at their defaults.
            }
        }
    }
}
