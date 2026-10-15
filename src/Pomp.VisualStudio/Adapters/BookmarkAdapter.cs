using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.VisualStudio;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.TextManager.Interop;
using Pomp.Adapters;
using Task = System.Threading.Tasks.Task;

namespace Pomp.VisualStudio.Adapters
{
    /// <summary>
    /// Bookmarks use MARKER_BOOKMARK on open text buffers. Visual Studio has no public bookmark-writing API,
    /// so adding a missing bookmark uses Edit.ToggleBookmark after checking the markers of that exact buffer.
    /// </summary>
    internal sealed class BookmarkAdapter : IBookmarkAdapter
    {
        private readonly VsAdapterContext context;

        public BookmarkAdapter(VsAdapterContext context)
        {
            this.context = context;
        }

        public async Task<IReadOnlyList<BookmarkInfo>> ListAsync(CancellationToken cancellationToken)
        {
            await context.Jtf.SwitchToMainThreadAsync(cancellationToken);

            var bookmarks = new List<BookmarkInfo>();
            foreach (var document in DocumentTable.EnumerateOpen(context))
            {
                cancellationToken.ThrowIfCancellationRequested();

                var lines = DocumentTable.TryGetTextLines(document.DocData);
                if (lines == null)
                {
                    continue;
                }

                try
                {
                    AddBookmarks(lines, document.Path, bookmarks);
                }
                catch (Exception ex) when (!(ex is OperationCanceledException))
                {
                    context.LogUnexpected("Reading the bookmarks of '" + document.Path + "'", ex);
                }
            }

            bookmarks.Sort((a, b) =>
            {
                var byFile = string.Compare(a.File, b.File, StringComparison.OrdinalIgnoreCase);
                return byFile != 0 ? byFile : a.Line.CompareTo(b.Line);
            });
            return bookmarks;
        }

        public async Task<bool> NavigateAsync(BookmarkInfo bookmark, CancellationToken cancellationToken)
        {
            if (bookmark == null)
            {
                throw new ArgumentNullException(nameof(bookmark));
            }

            var file = VsAdapterContext.NormalizePath(bookmark.File);
            var line = bookmark.Line;
            if (file == null || line < 1 || !await EditorAdapter.OpenDocumentAsync(context, file, null, cancellationToken))
            {
                return false;
            }

            await context.Jtf.SwitchToMainThreadAsync(cancellationToken);
            var frame = DocumentTable.FindFrame(context, file);
            var view = frame == null ? null : VsShellUtilities.GetTextView(frame);
            if (view == null || !LineExists(view, line))
            {
                return false;
            }

            MoveCaret(view, line);
            return true;
        }

        public async Task<bool> AddAsync(string file, int line, CancellationToken cancellationToken)
        {
            var fullPath = VsAdapterContext.NormalizePath(file);
            if (fullPath == null || line < 1 || !await EditorAdapter.OpenDocumentAsync(context, fullPath, null, cancellationToken))
            {
                return false;
            }

            await context.Jtf.SwitchToMainThreadAsync(cancellationToken);

            var frame = DocumentTable.FindFrame(context, fullPath);
            var view = frame == null ? null : VsShellUtilities.GetTextView(frame);
            if (view == null || !LineExists(view, line))
            {
                return false;
            }

            ErrorHandler.ThrowOnFailure(view.GetBuffer(out var lines));
            if (lines == null)
            {
                return false;
            }

            // Enumeration errors must propagate, not be mistaken for "no bookmark" and toggle an existing one off.
            if (HasBookmark(lines, fullPath, line))
            {
                MoveCaret(view, line);
                return true;
            }

            cancellationToken.ThrowIfCancellationRequested();
            // DTE.ExecuteCommand routes through the active command target. ShowNoActivate is insufficient: activate
            // this document and explicitly focus its view so the command cannot toggle another document's bookmark.
            ErrorHandler.ThrowOnFailure(frame!.Show());
            ErrorHandler.ThrowOnFailure(view.SendExplicitFocus());
            MoveCaret(view, line);

            var dte = context.Dte;
            var document = dte.ActiveDocument;
            if (document == null || !VsAdapterContext.SamePath(document.FullName, fullPath))
            {
                throw new InvalidOperationException("The requested document did not become active; no bookmark was toggled.");
            }

            var command = dte.Commands.Item("Edit.ToggleBookmark", -1);
            if (!command.IsAvailable)
            {
                throw new InvalidOperationException("Edit.ToggleBookmark is not available in the requested editor.");
            }

            // Activating an editor can invoke other extensions. Recheck the caret and markers immediately before
            // toggling, with no asynchronous gap between the check and command execution.
            MoveCaret(view, line);
            if (HasBookmark(lines, fullPath, line))
            {
                return true;
            }

            document = dte.ActiveDocument;
            if (document == null || !VsAdapterContext.SamePath(document.FullName, fullPath))
            {
                throw new InvalidOperationException("The active document changed before the bookmark command; no bookmark was toggled.");
            }

            ErrorHandler.ThrowOnFailure(view.GetCaretPos(out var caretLine, out _));
            if (caretLine != line - 1)
            {
                throw new InvalidOperationException("The editor caret changed before the bookmark command; no bookmark was toggled.");
            }

            cancellationToken.ThrowIfCancellationRequested();
            dte.ExecuteCommand("Edit.ToggleBookmark", string.Empty);
            return HasBookmark(lines, fullPath, line);
        }

        private static bool LineExists(IVsTextView view, int line)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            ErrorHandler.ThrowOnFailure(view.GetBuffer(out var lines));
            if (lines == null)
            {
                return false;
            }

            ErrorHandler.ThrowOnFailure(lines.GetLineCount(out var count));
            return line <= count;
        }

        private static void MoveCaret(IVsTextView view, int line)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            var zeroBasedLine = line - 1;
            // Collapse an existing multiline selection: ToggleBookmark otherwise acts on the selected lines.
            ErrorHandler.ThrowOnFailure(view.SetSelection(zeroBasedLine, 0, zeroBasedLine, 0));
            ErrorHandler.ThrowOnFailure(view.SetCaretPos(zeroBasedLine, 0));
            ErrorHandler.ThrowOnFailure(view.CenterLines(zeroBasedLine, 1));
        }

        private static bool HasBookmark(IVsTextLines lines, string path, int line)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            var bookmarks = new List<BookmarkInfo>();
            AddBookmarks(lines, path, bookmarks);
            return bookmarks.Exists(b => b.Line == line);
        }

        private static void AddBookmarks(IVsTextLines lines, string path, List<BookmarkInfo> into)
        {
            ThreadHelper.ThrowIfNotOnUIThread();

            const uint flags = (uint)(ENUMMARKERFLAGS.EM_ENTIREBUFFER | ENUMMARKERFLAGS.EM_INCLUDEINVISIBLE);
            ErrorHandler.ThrowOnFailure(lines.EnumMarkers(0, 0, 0, 0, (int)MARKERTYPE.MARKER_BOOKMARK, flags, out var markers));
            if (markers == null)
            {
                throw new InvalidOperationException("The editor returned no bookmark marker enumerator.");
            }

            ErrorHandler.ThrowOnFailure(markers.GetCount(out var count));
            var span = new TextSpan[1];
            for (var index = 0; index < count; index++)
            {
                ErrorHandler.ThrowOnFailure(markers.Next(out var marker));
                if (marker == null)
                {
                    throw new InvalidOperationException("The editor returned an incomplete bookmark marker enumeration.");
                }

                ErrorHandler.ThrowOnFailure(marker.GetCurrentSpan(span));
                // TextSpan lines are 0-based.
                into.Add(new BookmarkInfo { File = path, Line = span[0].iStartLine + 1 });
            }
        }
    }
}
