using System;
using System.ComponentModel.Composition;
using Microsoft.VisualStudio;
using Microsoft.VisualStudio.Editor;
using Microsoft.VisualStudio.OLE.Interop;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Text.Editor;
using Microsoft.VisualStudio.TextManager.Interop;
using Microsoft.VisualStudio.Utilities;

namespace Pomp.VisualStudio.Editor
{
    /// <summary>
    /// The accept keys of the inline completion (Tab: all, Ctrl+Right: next word, Escape: dismiss), and Escape for an open Live
    /// comparison. Visual Studio turns these keys into editor commands (Edit.InsertTab, Edit.WordNext, Edit.SelectionCancel) before WPF
    /// key events reach the text view — in VS 2019 Tab inserted an indent next to a visible suggestion — so they are taken from the
    /// view's command chain. A filter added here runs before the editor's own handlers (and Roslyn's); every other command, and these
    /// when nothing of POMP shows, pass on unchanged. All text views: Live marks are shown in any text file.
    /// </summary>
    [Export(typeof(IVsTextViewCreationListener))]
    [ContentType("text")]
    [TextViewRole(PredefinedTextViewRoles.Editable)]
    internal sealed class InlineCompletionKeys : IVsTextViewCreationListener
    {
        [Import] public IVsEditorAdaptersFactoryService EditorAdapters = null!;

        public void VsTextViewCreated(IVsTextView textViewAdapter)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            var view = EditorAdapters.GetWpfTextView(textViewAdapter);
            if (view == null) return;
            var filter = new Filter(view);
            if (ErrorHandler.Succeeded(textViewAdapter.AddCommandFilter(filter, out var next)) && next != null) filter.Next = next;
        }

        private sealed class Filter : IOleCommandTarget
        {
            private readonly IWpfTextView view;

            public Filter(IWpfTextView view) { this.view = view; }

            public IOleCommandTarget? Next { get; set; }

            public int Exec(ref Guid pguidCmdGroup, uint nCmdID, uint nCmdexecopt, IntPtr pvaIn, IntPtr pvaOut)
            {
                ThreadHelper.ThrowIfNotOnUIThread();
                if (pguidCmdGroup == VSConstants.VSStd2K && (VSConstants.VSStd2KCmdID)nCmdID == VSConstants.VSStd2KCmdID.CANCEL && LiveEditFeature.CloseComparison(view)) return VSConstants.S_OK;
                if (pguidCmdGroup == VSConstants.VSStd2K && InlineEditorFeature.HandleEditorCommand(view, (VSConstants.VSStd2KCmdID)nCmdID)) return VSConstants.S_OK;
                return Next?.Exec(ref pguidCmdGroup, nCmdID, nCmdexecopt, pvaIn, pvaOut) ?? (int)Constants.OLECMDERR_E_NOTSUPPORTED;
            }

            public int QueryStatus(ref Guid pguidCmdGroup, uint cCmds, OLECMD[] prgCmds, IntPtr pCmdText)
            {
                ThreadHelper.ThrowIfNotOnUIThread();
                return Next?.QueryStatus(ref pguidCmdGroup, cCmds, prgCmds, pCmdText) ?? (int)Constants.OLECMDERR_E_NOTSUPPORTED;
            }
        }
    }
}
