using System;
using System.Collections.Generic;
using Microsoft.VisualStudio;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Shell.Interop;
using Microsoft.VisualStudio.Text;
using Microsoft.VisualStudio.Text.Editor;
using Microsoft.VisualStudio.TextManager.Interop;

namespace Pomp.VisualStudio.Adapters
{
    /// <summary>A document that has an editor window frame.</summary>
    internal sealed class OpenDocument
    {
        public OpenDocument(string path, IVsWindowFrame frame, object? docData)
        {
            Path = path;
            Frame = frame;
            DocData = docData;
        }

        /// <summary>Full path (the document moniker).</summary>
        public string Path { get; }

        public IVsWindowFrame Frame { get; }

        /// <summary>The frame's DocData object (text buffer, designer model, ...); may be null.</summary>
        public object? DocData { get; }
    }

    /// <summary>Queries against the document windows and the Running Document Table. UI thread only.</summary>
    internal static class DocumentTable
    {
        /// <summary>Documents that currently have a window frame in the editor, one entry per file.</summary>
        public static IReadOnlyList<OpenDocument> EnumerateOpen(VsAdapterContext context)
        {
            ThreadHelper.ThrowIfNotOnUIThread();

            var result = new List<OpenDocument>();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            if (ErrorHandler.Failed(context.UiShell.GetDocumentWindowEnum(out var frames)) || frames == null)
            {
                return result;
            }

            var buffer = new IVsWindowFrame[1];
            while (frames.Next(1, buffer, out var fetched) == VSConstants.S_OK && fetched == 1)
            {
                var frame = buffer[0];
                if (frame == null)
                {
                    continue;
                }

                if (ErrorHandler.Failed(frame.GetProperty((int)__VSFPROPID.VSFPROPID_pszMkDocument, out object monikerValue))
                    || !(monikerValue is string moniker)
                    || !VsAdapterContext.IsFilePath(moniker))
                {
                    continue;
                }

                var path = VsAdapterContext.NormalizePath(moniker);
                if (path == null || !seen.Add(path))
                {
                    continue;
                }

                frame.GetProperty((int)__VSFPROPID.VSFPROPID_DocData, out object docData);
                result.Add(new OpenDocument(path, frame, docData));
            }

            return result;
        }

        /// <summary>The window frame showing <paramref name="path"/>, or null when the file is not open in an editor.</summary>
        public static IVsWindowFrame? FindFrame(VsAdapterContext context, string path)
        {
            ThreadHelper.ThrowIfNotOnUIThread();

            return VsShellUtilities.IsDocumentOpen(context.Package, path, Guid.Empty, out _, out _, out var frame) && frame != null
                ? frame
                : null;
        }

        /// <summary>The editor buffer behind <paramref name="path"/> if the Running Document Table holds a text buffer for it.</summary>
        public static ITextBuffer? TryGetTextBuffer(VsAdapterContext context, string path)
        {
            ThreadHelper.ThrowIfNotOnUIThread();

            var rdt = context.Rdt;
            if (!rdt.IsMonikerValid(path))
            {
                return null;
            }

            var cookie = rdt.GetDocumentCookie(path);
            if (cookie == VSConstants.VSCOOKIE_NIL)
            {
                return null;
            }

            object data = rdt.GetDocumentData(cookie);
            return TryGetTextBuffer(context, data);
        }

        /// <summary>Converts a document data object (VS text buffer, buffer provider or editor buffer) to an <see cref="ITextBuffer"/>.</summary>
        public static ITextBuffer? TryGetTextBuffer(VsAdapterContext context, object? docData)
        {
            ThreadHelper.ThrowIfNotOnUIThread();

            switch (docData)
            {
                case null:
                    return null;
                case ITextBuffer textBuffer:
                    return textBuffer;
                case IVsTextBuffer vsTextBuffer:
                    return context.EditorAdapters.GetDocumentBuffer(vsTextBuffer);
                case IVsTextBufferProvider provider when ErrorHandler.Succeeded(provider.GetTextBuffer(out var lines)) && lines != null:
                    return context.EditorAdapters.GetDocumentBuffer(lines);
                default:
                    return null;
            }
        }

        /// <summary>The <see cref="IVsTextLines"/> of a document data object (used for markers), or null.</summary>
        public static IVsTextLines? TryGetTextLines(object? docData)
        {
            ThreadHelper.ThrowIfNotOnUIThread();

            switch (docData)
            {
                case IVsTextLines lines:
                    return lines;
                case IVsTextBufferProvider provider when ErrorHandler.Succeeded(provider.GetTextBuffer(out var providedLines)):
                    return providedLines;
                default:
                    return null;
            }
        }

        /// <summary>True when the document has unsaved changes; null when the Running Document Table does not know the file.</summary>
        public static bool? IsDirty(VsAdapterContext context, string path)
        {
            ThreadHelper.ThrowIfNotOnUIThread();

            var rdt = context.Rdt;
            if (!rdt.IsMonikerValid(path))
            {
                return null;
            }

            var cookie = rdt.GetDocumentCookie(path);
            return cookie == VSConstants.VSCOOKIE_NIL ? (bool?)null : rdt.IsDocumentDirty(cookie);
        }

        /// <summary>The WPF text view of a window frame, or null for non-text editors.</summary>
        public static IWpfTextView? TryGetWpfTextView(VsAdapterContext context, IVsWindowFrame frame)
        {
            ThreadHelper.ThrowIfNotOnUIThread();

            var vsTextView = VsShellUtilities.GetTextView(frame);
            return vsTextView == null ? null : context.EditorAdapters.GetWpfTextView(vsTextView);
        }
    }
}
