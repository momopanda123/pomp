using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using Microsoft.VisualStudio;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Shell.Interop;
using Microsoft.VisualStudio.Text;
using Microsoft.VisualStudio.TextManager.Interop;
using Pomp.VisualStudio.Adapters;
using Pomp.VisualStudio.Interop;

namespace Pomp.VisualStudio.Editor
{
    /// <summary>Owns native Bookmarks-window entries, never editor commands or anonymous glyphs.</summary>
    internal sealed class LiveBookmarkManager : IDisposable
    {
        private readonly VsAdapterContext context;
        private readonly List<OwnedBookmark> owned = new List<OwnedBookmark>();
        private readonly Dictionary<string, OwnedFolder> folders = new Dictionary<string, OwnedFolder>(StringComparer.Ordinal);
        private INativeBookmarkManager? manager;
        private bool disposed;

        public LiveBookmarkManager(VsAdapterContext context)
        {
            this.context = context ?? throw new ArgumentNullException(nameof(context));
        }

        /// <summary>
        /// Adds one POMP bookmark per (request, file) inside the conversation's POMP folder. Never removes anything:
        /// POMP bookmarks stay until the user deletes them or runs <see cref="Cleanup"/>.
        /// </summary>
        public void Mark(string path, ITextBuffer buffer, int oneBasedLine, string conversation, string taskId, string title)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            if (disposed) throw new ObjectDisposedException(nameof(LiveBookmarkManager));
            if (buffer == null) throw new ArgumentNullException(nameof(buffer));
            if (string.IsNullOrEmpty(conversation)) throw new ArgumentException("A native bookmark requires a conversation.", nameof(conversation));
            if (string.IsNullOrWhiteSpace(taskId)) throw new ArgumentException("A native bookmark requires an authoritative task identity.", nameof(taskId));
            var fullPath = VsAdapterContext.NormalizePath(path);
            if (fullPath == null) throw new ArgumentException("A native bookmark requires an absolute document path.", nameof(path));
            if (oneBasedLine < 1 || oneBasedLine > buffer.CurrentSnapshot.LineCount)
                throw new ArgumentOutOfRangeException(nameof(oneBasedLine));

            var lines = context.EditorAdapters.GetBufferAdapter(buffer) as IVsTextLines;
            if (lines == null) throw new NotSupportedException("This editor has no native text buffer for Bookmarks.");
            var existing = Enumerate(lines);
            // One owned entry per conversation/request/file, and never two owned entries on one line of a conversation.
            // Native CreateItem creates a separate marker without toggling any user's bookmark.
            var span = new TextSpan[1];
            foreach (var entry in owned)
            {
                if (entry.Conversation != conversation || !SameIdentity(entry.Lines, lines) || !Contains(existing, entry.Marker)) continue;
                if (entry.TaskId == taskId) return;
                ErrorHandler.ThrowOnFailure(entry.Marker.GetCurrentSpan(span));
                if (span[0].iStartLine == oneBasedLine - 1) return;
            }

            var nativeManager = GetManager();
            var automation = context.GetService<EnvDTE.IVsExtensibility>(typeof(EnvDTE.IVsExtensibility));
            var rdt = (IVsRunningDocumentTable)context.Rdt;
            var cookie = context.Rdt.GetDocumentCookie(fullPath);
            if (cookie == VSConstants.VSCOOKIE_NIL)
                throw new InvalidOperationException("The bookmark document is not registered in the running document table.");
            var registeredLines = DocumentTable.TryGetTextLines((object)context.Rdt.GetDocumentData(cookie));
            if (registeredLines == null || !SameIdentity(registeredLines, lines))
                throw new InvalidOperationException("The bookmark buffer does not match the registered document data.");
            // A read lock retains docdata, not a visible editor window or an edit lock. Thus closing/reopening
            // a view cannot replace the marker identity we own; normal save-on-last-edit-lock behavior remains.
            // https://learn.microsoft.com/visualstudio/extensibility/internals/running-document-table
            ErrorHandler.ThrowOnFailure(rdt.LockDocument((uint)_VSRDTFLAGS.RDT_ReadLock, cookie));
            // Native folder creation otherwise starts label editing when the Bookmarks pane exists.
            // Its implementation explicitly honors this public automation scope; never activate the pane.
            var automationEntered = false;
            var keepLock = false;
            try
            {
                automation.EnterAutomationFunction();
                automationEntered = true;
                var folder = GetFolder(nativeManager, conversation, title, out var newFolder);
                // The conversation got (or changed) its title after the folder was created.
                if (!newFolder) Relabel(folder, FolderText(title, folder.Created));
                ErrorHandler.ThrowOnFailure(nativeManager.CreateItem(oneBasedLine - 1, lines, fullPath, -1, out var item));
                if (item == null) throw new InvalidOperationException("The native bookmark manager returned no item.");
                IVsTextLineMarker? created = null;
                foreach (var marker in Enumerate(lines))
                {
                    if (Contains(existing, marker)) continue;
                    ErrorHandler.ThrowOnFailure(marker.GetType(out var markerType));
                    if (markerType != (int)MARKERTYPE.MARKER_BOOKMARK) continue;
                    if (created != null)
                        throw new InvalidOperationException("Native bookmark creation produced ambiguous marker identities; no user marker was removed.");
                    created = marker;
                }
                if (created == null)
                    throw new InvalidOperationException("The native Bookmarks item has no newly created marker identity.");
                var entry = new OwnedBookmark(lines, created, item, cookie, conversation, taskId);
                owned.Add(entry);
                keepLock = true;
                // Keep ownership even if naming fails, so Cleanup can still remove the newly created entry.
                ErrorHandler.ThrowOnFailure(item.SetText("pomp:" + Path.GetFileName(fullPath) + ":" + oneBasedLine.ToString(CultureInfo.InvariantCulture)));
                // put_Text changes the model, not the tree's copied label. Moving the new folder
                // past our new root item refreshes that label without selecting/renaming in the UI.
                if (newFolder) ErrorHandler.ThrowOnFailure(nativeManager.MoveItem(folder.Item, item, 0));
                ErrorHandler.ThrowOnFailure(nativeManager.MoveItem(item, folder.Item, 1));
            }
            finally
            {
                try { if (automationEntered) automation.ExitAutomationFunction(); }
                finally
                {
                    if (!keepLock) ErrorHandler.ThrowOnFailure(rdt.UnlockDocument((uint)_VSRDTFLAGS.RDT_ReadLock, cookie));
                }
            }
        }

        /// <summary>Folder label: the conversation title, or the folder's creation time while the conversation has none.</summary>
        private static string FolderText(string title, DateTime created)
        {
            var text = (title ?? string.Empty).Replace('\r', ' ').Replace('\n', ' ').Trim();
            if (text.Length > 60) text = text.Substring(0, 59) + "…";
            if (text.Length == 0)
                text = Pomp.Hosting.PompLanguage.Pick("대화 ", "Conversation ") + created.ToString("MM-dd HH:mm", CultureInfo.InvariantCulture);
            return FolderPrefix + text;
        }

        private const string FolderPrefix = "POMP · ";

        private OwnedFolder GetFolder(INativeBookmarkManager nativeManager, string conversation, string title, out bool created)
        {
            created = false;
            if (folders.TryGetValue(conversation, out var existing) && HasLiveOwnedChild(existing.Item, conversation)) return existing;
            // CBookmarkManager::CreateItem / CBookmarkItem::InitItem: null buffer means a real folder
            // (type 1), not a document item. -1 requests an unused native ID, never a user's existing ID.
            ErrorHandler.ThrowOnFailure(nativeManager.CreateItem(0, null, null, -1, out var item));
            if (item == null) throw new InvalidOperationException("Visual Studio returned no native bookmark folder.");
            ErrorHandler.ThrowOnFailure(item.GetItemType(out var type));
            if (type != 1) throw new InvalidOperationException("Visual Studio did not create a native bookmark folder.");
            var folder = new OwnedFolder(item, DateTime.Now);
            folder.Text = FolderText(title, folder.Created);
            ErrorHandler.ThrowOnFailure(item.SetText(folder.Text));
            // CreateItem follows the current native item; explicitly keep conversation folders at the root.
            ErrorHandler.ThrowOnFailure(nativeManager.MoveItem(item, null, 0));
            created = true;
            folders[conversation] = folder;
            return folder;
        }

        /// <summary>Relabels the conversation's POMP folder when the conversation gets or changes its title.</summary>
        public void Retitle(string conversation, string title)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            if (disposed || !folders.TryGetValue(conversation, out var folder)) return;
            var text = FolderText(title, folder.Created);
            // Only a folder still proven ours (a live owned child) is renamed; never one the user deleted or rebuilt.
            if (folder.Text == text || !HasLiveOwnedChild(folder.Item, conversation)) return;
            var automation = context.GetService<EnvDTE.IVsExtensibility>(typeof(EnvDTE.IVsExtensibility));
            automation.EnterAutomationFunction();
            try { Relabel(folder, text); }
            finally { automation.ExitAutomationFunction(); }
        }

        private static void Relabel(OwnedFolder folder, string text)
        {
            if (folder.Text == text) return;
            ErrorHandler.ThrowOnFailure(folder.Item.SetText(text));
            folder.Text = text;
            // put_Text changes the model; the Bookmarks tree keeps its own copy of the label. Update that label in place
            // (no move, so the folder keeps its position). Without a Bookmarks tree yet, it is built from the model later.
            ErrorHandler.ThrowOnFailure(folder.Item.GetItemHandle(out var handle));
            var tree = NativeMethods.FindTreeViewContaining(handle);
            if (tree != IntPtr.Zero) NativeMethods.SetTreeItemText(tree, handle, text);
        }

        private bool HasLiveOwnedChild(INativeBookmarkItem folder, string conversation)
        {
            // A user can delete/reorganize our folder. Only reuse it when a live owned child proves membership.
            // The native getters return BORROWED pointers (no AddRef); do not marshal them as owned COM outs.
            ErrorHandler.ThrowOnFailure(folder.GetObjList(out var pointer));
            while (pointer != IntPtr.Zero)
            {
                var child = (INativeBookmarkItem)Marshal.GetObjectForIUnknown(pointer);
                foreach (var entry in owned)
                    if (entry.Conversation == conversation && SameIdentity(entry.Item, child) && Contains(Enumerate(entry.Lines), entry.Marker)) return true;
                ErrorHandler.ThrowOnFailure(child.GetNext(out pointer));
            }
            return false;
        }

        /// <summary>
        /// Removes the bookmarks POMP added in this Visual Studio session, identified by marker COM identity (never by line,
        /// name or ID), then deletes those of its folders that are now empty. Returns how many bookmarks were removed.
        /// User bookmarks, user folders and anything the user put into a POMP folder stay.
        /// </summary>
        public int Cleanup()
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            if (disposed) throw new ObjectDisposedException(nameof(LiveBookmarkManager));
            List<Exception>? failures = null;
            var removed = 0;
            for (var index = owned.Count - 1; index >= 0; index--)
            {
                var entry = owned[index];
                try
                {
                    // If a user removed/replaced our entry, its replacement is not ours even at the same line.
                    if (!entry.Removed && Contains(Enumerate(entry.Lines), entry.Marker))
                    {
                        ErrorHandler.ThrowOnFailure(GetManager().RemoveItem(entry.Marker));
                        if (Contains(Enumerate(entry.Lines), entry.Marker))
                            throw new InvalidOperationException("The native bookmark manager did not remove the owned marker.");
                        removed++;
                    }
                    entry.Removed = true;
                    ErrorHandler.ThrowOnFailure(((IVsRunningDocumentTable)context.Rdt).UnlockDocument((uint)_VSRDTFLAGS.RDT_ReadLock, entry.Cookie));
                    owned.RemoveAt(index);
                }
                catch (Exception ex)
                {
                    context.LogUnexpected("Removing a POMP native bookmark", ex);
                    (failures ??= new List<Exception>()).Add(ex);
                }
            }
            try { DeleteEmptyFolders(); }
            catch (Exception ex)
            {
                context.LogUnexpected("Deleting empty POMP bookmark folders", ex);
                (failures ??= new List<Exception>()).Add(ex);
            }
            if (failures != null)
                throw new AggregateException(removed + " POMP bookmarks were removed; some could not be and remain owned for retry.", failures);
            return removed;
        }

        // Bookmarks tool window (persistence slot) and its "Delete" command (stdidcmd.h: VSStd2K cmdidDeleteBookmark).
        // The bookmark manager interface has no folder removal; the window's own Delete command is the supported path.
        private static readonly Guid BookmarkWindow = new Guid("A0C5197D-0AC7-4B63-97CD-8872A789D233");
        private const uint DeleteBookmarkCommand = 561;

        /// <summary>
        /// Deletes owned folders that have no items left, through the Bookmarks window's Delete command. The folder is selected
        /// in the tree first and the command only runs when the selection is exactly that folder, so nothing else can be
        /// deleted; the previous selection is restored. A Bookmarks window that was not on screen is hidden again and keyboard
        /// focus goes back to the window that had it (creating the window shows and activates it).
        /// </summary>
        private void DeleteEmptyFolders()
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            if (folders.Count == 0) return;
            var shell = context.GetService<IVsUIShell>(typeof(SVsUIShell));
            var slot = BookmarkWindow;
            var hadFrame = shell.FindToolWindow((uint)__VSFINDTOOLWIN.FTW_fFindFirst, ref slot, out var frame) == VSConstants.S_OK && frame != null;
            var wasVisible = hadFrame && frame!.IsVisible() == VSConstants.S_OK;
            var focus = NativeMethods.GetFocusedWindow();
            try
            {
                // The tree (and the folder items in it) exists only once the window is created.
                if (!hadFrame) ErrorHandler.ThrowOnFailure(shell.FindToolWindow((uint)__VSFINDTOOLWIN.FTW_fForceCreate, ref slot, out frame));
                if (frame == null) throw new InvalidOperationException("Visual Studio has no Bookmarks window.");
                DeleteEmptyFolders(frame);
            }
            finally
            {
                if (!wasVisible && frame != null && frame.IsVisible() == VSConstants.S_OK) ErrorHandler.ThrowOnFailure(frame.Hide());
                NativeMethods.RestoreFocus(focus);
            }
        }

        private void DeleteEmptyFolders(IVsWindowFrame frame)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            ErrorHandler.ThrowOnFailure(frame.GetProperty((int)__VSFPROPID.VSFPROPID_DocView, out var view));
            var target = view as Microsoft.VisualStudio.OLE.Interop.IOleCommandTarget
                ?? throw new NotSupportedException("The Bookmarks window does not accept commands.");
            foreach (var pair in new List<KeyValuePair<string, OwnedFolder>>(folders))
            {
                var folder = pair.Value.Item;
                ErrorHandler.ThrowOnFailure(folder.GetObjList(out var child));
                if (child != IntPtr.Zero) continue; // the user's own items keep the folder
                ErrorHandler.ThrowOnFailure(folder.GetItemHandle(out var handle));
                var tree = NativeMethods.FindTreeViewContaining(handle);
                if (tree == IntPtr.Zero)
                {
                    // Not in the Bookmarks tree any more: the user already deleted it.
                    folders.Remove(pair.Key);
                    continue;
                }
                var previous = NativeMethods.GetTreeSelection(tree);
                NativeMethods.SelectTreeItem(tree, handle);
                if (NativeMethods.GetTreeSelection(tree) != handle)
                    throw new InvalidOperationException("The empty POMP folder could not be selected; nothing was deleted.");
                var group = VSConstants.VSStd2K;
                ErrorHandler.ThrowOnFailure(target.Exec(ref group, DeleteBookmarkCommand, 0, IntPtr.Zero, IntPtr.Zero));
                if (NativeMethods.TreeContains(tree, handle))
                    throw new InvalidOperationException("The Bookmarks window did not delete the empty POMP folder.");
                if (previous != IntPtr.Zero && previous != handle && NativeMethods.TreeContains(tree, previous))
                    NativeMethods.SelectTreeItem(tree, previous);
                folders.Remove(pair.Key);
            }
        }

        /// <summary>Releases document locks and COM references only; the bookmarks themselves stay in Visual Studio.</summary>
        public void Dispose()
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            if (disposed) return;
            foreach (var entry in owned)
            {
                try
                {
                    ErrorHandler.ThrowOnFailure(((IVsRunningDocumentTable)context.Rdt).UnlockDocument((uint)_VSRDTFLAGS.RDT_ReadLock, entry.Cookie));
                }
                catch (Exception ex)
                {
                    context.LogUnexpected("Releasing a POMP bookmark document lock during shutdown", ex);
                }
            }
            owned.Clear();
            folders.Clear();
            manager = null;
            disposed = true;
        }

        private INativeBookmarkManager GetManager()
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            if (manager != null) return manager;
            // Internal COM integration is deliberately bounded. These IIDs and vtable prefixes are present in
            // installed VS 15.9, 16.11 and 18 editor implementations. The same interface identity is required
            // on VS 17; newer hosts must be inspected before extending this bound. No command fallback.
            if (!Version.TryParse(context.Dte.Version, out var version) || version.Major < 15 || version.Major > 18)
                throw new NotSupportedException("Native POMP bookmarks require Visual Studio 2017 through 2026.");
            var textManager = context.TextManager as INativePrivateTextManager;
            if (textManager == null)
                throw new NotSupportedException("This Visual Studio host does not expose the native Bookmarks manager interface.");
            ErrorHandler.ThrowOnFailure(textManager.GetBookmarkManager(out var result));
            manager = result ?? throw new InvalidOperationException("Visual Studio returned no native Bookmarks manager.");
            return manager;
        }

        private static List<IVsTextLineMarker> Enumerate(IVsTextLines lines)
        {
            const uint flags = (uint)(ENUMMARKERFLAGS.EM_ENTIREBUFFER | ENUMMARKERFLAGS.EM_INCLUDEINVISIBLE | ENUMMARKERFLAGS.EM_ALLTYPES);
            ErrorHandler.ThrowOnFailure(lines.EnumMarkers(0, 0, 0, 0, (int)MARKERTYPE.MARKER_BOOKMARK, flags, out var enumerator));
            if (enumerator == null) throw new InvalidOperationException("Visual Studio returned no native bookmark enumeration.");
            ErrorHandler.ThrowOnFailure(enumerator.GetCount(out var count));
            var markers = new List<IVsTextLineMarker>(count);
            for (var index = 0; index < count; index++)
            {
                ErrorHandler.ThrowOnFailure(enumerator.Next(out var marker));
                if (marker == null) throw new InvalidOperationException("Visual Studio returned an incomplete bookmark enumeration.");
                markers.Add(marker);
            }
            return markers;
        }

        private static bool Contains(List<IVsTextLineMarker> markers, IVsTextLineMarker candidate)
        {
            foreach (var marker in markers)
            {
                if (SameIdentity(marker, candidate)) return true;
            }
            return false;
        }

        private static bool SameIdentity(object first, object second)
        {
            if (ReferenceEquals(first, second)) return true;
            var firstIdentity = Marshal.GetIUnknownForObject(first);
            try
            {
                var secondIdentity = Marshal.GetIUnknownForObject(second);
                try { return firstIdentity == secondIdentity; }
                finally { Marshal.Release(secondIdentity); }
            }
            finally { Marshal.Release(firstIdentity); }
        }

        private sealed class OwnedBookmark
        {
            public OwnedBookmark(IVsTextLines lines, IVsTextLineMarker marker, INativeBookmarkItem item, uint cookie, string conversation, string taskId)
            {
                Lines = lines;
                Marker = marker;
                Item = item;
                Cookie = cookie;
                Conversation = conversation;
                TaskId = taskId;
            }
            public IVsTextLines Lines { get; }
            public IVsTextLineMarker Marker { get; }
            public INativeBookmarkItem Item { get; }
            public uint Cookie { get; }
            public string Conversation { get; }
            public string TaskId { get; }
            public bool Removed { get; set; }
        }

        private sealed class OwnedFolder
        {
            public OwnedFolder(INativeBookmarkItem item, DateTime created)
            {
                Item = item;
                Created = created;
            }
            public INativeBookmarkItem Item { get; }
            public DateTime Created { get; }
            public string Text { get; set; } = string.Empty;
        }

        // IUnknown prefixes inspected in VS 15.9, 16.11 and 18 Editor.Implementation metadata.
        // Native msenv public symbols/disassembly additionally establish folder creation and borrowed
        // GetObjList/GetNext outputs; the managed editor metadata incorrectly declares those by-value.
        [ComImport, Guid("F584B25E-AC79-48AC-933F-CF285F8882A4"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface INativePrivateTextManager
        {
            [PreserveSig] int GetBookmarkManager([MarshalAs(UnmanagedType.Interface)] out INativeBookmarkManager manager);
        }

        [ComImport, Guid("D9F127B8-5D54-418c-9399-E3A2D850D983"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface INativeBookmarkManager
        {
            [PreserveSig] int CreateItem(int zeroBasedLine, [MarshalAs(UnmanagedType.Interface)] IVsTextLines? buffer,
                [MarshalAs(UnmanagedType.LPWStr)] string? moniker, int bookmarkId,
                [MarshalAs(UnmanagedType.Interface)] out INativeBookmarkItem item);
            [PreserveSig] int RemoveItem([MarshalAs(UnmanagedType.Interface)] IVsTextLineMarker marker);
            [PreserveSig] int ResetCurBookmark();
            [PreserveSig] int WriteUserOptions([MarshalAs(UnmanagedType.Interface)] Microsoft.VisualStudio.OLE.Interop.IStream stream);
            [PreserveSig] int ReadUserOptions([MarshalAs(UnmanagedType.Interface)] Microsoft.VisualStudio.OLE.Interop.IStream stream);
            [PreserveSig] int MoveItem([MarshalAs(UnmanagedType.Interface)] INativeBookmarkItem item,
                [MarshalAs(UnmanagedType.Interface)] INativeBookmarkItem? after, int intoFolder);
        }

        [ComImport, Guid("5B854029-17C5-4fb9-897A-16B40055EF51"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface INativeBookmarkItem
        {
            [PreserveSig] int SetItemHandle(IntPtr handle);
            [PreserveSig] int GetItemHandle(out IntPtr handle);
            [PreserveSig] int NavigateTo();
            [PreserveSig] int GetItemType(out int type);
            [PreserveSig] int GetMarker([MarshalAs(UnmanagedType.Interface)] IVsTextLineMarker marker);
            [PreserveSig] int GetID(out int id);
            [PreserveSig] int GetObjList(out IntPtr borrowedItem);
            [PreserveSig] int GetNext(out IntPtr borrowedItem);
            [PreserveSig] int GetChecked(out int value);
            [PreserveSig] int GetText([MarshalAs(UnmanagedType.BStr)] out string name);
            [PreserveSig] int SetText([MarshalAs(UnmanagedType.BStr)] string name);
        }
    }
}
