using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.VisualStudio;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Shell.Interop;
using Microsoft.VisualStudio.Text;
using Microsoft.VisualStudio.Threading;
using Pomp.Diagnostics;
using Pomp.Features;
using Pomp.Tools;
using Pomp.VisualStudio.Editor;
using Pomp.VisualStudio.Interop;
using Task = System.Threading.Tasks.Task;

namespace Pomp.VisualStudio.Adapters
{
    // Live for omp's own file tools: they write the file on disk, and the editor shows the difference to the text right before the call
    // like a host edit. Before the write the open document is held (Visual Studio ignores its file changes, so it neither reloads it nor
    // asks about the outside change). After it, when the held buffer still holds the text before, the difference is applied to it as a
    // POMP edit (earlier marks move with the text, Undo and Revert work) and the file is saved, which only writes back what is already on
    // disk. When the editor already loaded the new text (or the file was not open), the difference is recorded on the loaded text.
    internal sealed partial class TextBufferAdapter
    {
        /// <summary>A held document: its file change control, until the tool's result arrives or two minutes pass.</summary>
        private sealed class DiskEditHold
        {
            public DiskEditHold(object docData, int generation) { DocData = docData; Generation = generation; }
            public readonly object DocData;
            public readonly int Generation;
        }

        private static readonly TimeSpan MaxHold = TimeSpan.FromMinutes(2);
        private readonly Dictionary<string, DiskEditHold> holds = new Dictionary<string, DiskEditHold>(StringComparer.OrdinalIgnoreCase);
        private int holdGeneration;

        public async Task HoldDiskEditAsync(IReadOnlyList<string> paths, CancellationToken cancellationToken)
        {
            await context.Jtf.SwitchToMainThreadAsync(cancellationToken);
            var settings = Composition.PompServices.Current?.Settings;
            // Without Live and bookmarks the editor reacts to the tool's write as to any outside change.
            if (settings == null || (!settings.AppliedLiveFollowEnabled && !settings.AppliedLiveBookmarksEnabled)) return;
            foreach (var path in paths)
            {
                var fullPath = VsAdapterContext.NormalizePath(path);
                if (fullPath == null || holds.ContainsKey(fullPath) || !context.Rdt.IsMonikerValid(fullPath)) continue;
                var cookie = context.Rdt.GetDocumentCookie(fullPath);
                if (cookie == VSConstants.VSCOOKIE_NIL || !(context.Rdt.GetDocumentData(cookie) is IVsDocDataFileChangeControl control)) continue;
                if (ErrorHandler.Failed(control.IgnoreFileChanges(1))) continue;
                var hold = new DiskEditHold(control, ++holdGeneration);
                holds[fullPath] = hold;
                ReleaseLaterAsync(fullPath, hold.Generation).Forget(context.Log, "ending a disk edit hold");
            }
        }

        /// <summary>A hold whose tool never reported back (denied, blocked, the process ended) ends on its own.</summary>
        private async Task ReleaseLaterAsync(string fullPath, int generation)
        {
            await Task.Delay(MaxHold).ConfigureAwait(false);
            await context.Jtf.SwitchToMainThreadAsync();
            if (holds.TryGetValue(fullPath, out var hold) && hold.Generation == generation) Release(fullPath);
        }

        public async Task ShowDiskEditAsync(string path, string? before, bool applied, CancellationToken cancellationToken)
        {
            var fullPath = VsAdapterContext.NormalizePath(path);
            if (fullPath == null) return;
            await context.Jtf.SwitchToMainThreadAsync(cancellationToken);
            try
            {
                if (applied) await ShowAsync(fullPath, before, cancellationToken);
            }
            finally
            {
                await context.Jtf.SwitchToMainThreadAsync();
                Release(fullPath);
            }
        }

        private async Task ShowAsync(string fullPath, string? before, CancellationToken cancellationToken)
        {
            var services = Composition.PompServices.Current;
            if (services == null || (!services.Settings.AppliedLiveFollowEnabled && !services.Settings.AppliedLiveBookmarksEnabled)) return;
            var root = await context.GetWorkspaceRootAsync(cancellationToken);

            // File I/O and the diff stay off the UI thread.
            await TaskScheduler.Default;
            if (!new PathGuard(root).TryResolve(fullPath, out var file, out _) || !File.Exists(file)) return;
            var after = ReadDiskText(file);
            // The extension read the file as UTF-8; the editor's text has no byte order mark.
            var old = (before ?? string.Empty).TrimStart('\uFEFF');
            if (old == after) return;
            var diff = LineDiff.Create(old, after, cancellationToken);

            await context.Jtf.SwitchToMainThreadAsync(cancellationToken);
            try
            {
                var buffer = DocumentTable.TryGetTextBuffer(context, file);
                if (buffer == null)
                {
                    OpenEditTarget(file);
                    buffer = DocumentTable.TryGetTextBuffer(context, file);
                    if (buffer == null) return;
                }
                var snapshot = buffer.CurrentSnapshot;
                var text = snapshot.GetText();
                if (text == old && TryCaptureOpenBuffer(file)?.IsDirty != true)
                {
                    ApplyDiskEdit(file, buffer, snapshot, diff, services);
                    return;
                }
                if (text == after)
                {
                    services.RefreshEditorSession();
                    services.EditorFollow.RecordLoaded(file, buffer, Loaded(diff, snapshot));
                    return;
                }
                context.Log.Debug("Live: " + file + " holds neither the text before nor after the tool's edit; its change is not shown.");
            }
            catch (Exception ex) when (!(ex is OperationCanceledException))
            {
                context.LogUnexpected("Showing the disk edit of '" + file + "'", ex);
            }
        }

        /// <summary>
        /// Ends a hold: the file change service forgets the tool's write and the document reacts to file changes again. A clean buffer
        /// that still differs from the disk (the change was not brought in) is reloaded, so the editor never keeps the old text.
        /// </summary>
        private void Release(string fullPath)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            if (!holds.TryGetValue(fullPath, out var hold)) return;
            holds.Remove(fullPath);
            try
            {
                context.GetService<IVsFileChangeEx>(typeof(SVsFileChangeEx)).SyncFile(fullPath);
                if (hold.DocData is IVsDocDataFileChangeControl control) control.IgnoreFileChanges(0);
                var buffer = DocumentTable.TryGetTextBuffer(context, fullPath);
                if (buffer != null && hold.DocData is IVsPersistDocData persist && TryCaptureOpenBuffer(fullPath)?.IsDirty != true
                    && File.Exists(fullPath) && buffer.CurrentSnapshot.GetText() != ReadDiskText(fullPath))
                    ErrorHandler.ThrowOnFailure(persist.ReloadDocData(0));
            }
            catch (Exception ex) { context.LogUnexpected("Ending the disk edit hold of '" + fullPath + "'", ex); }
        }

        /// <summary>Brings the tool's change into the buffer (which still holds the text before it) as one POMP edit, then saves.</summary>
        private void ApplyDiskEdit(string file, ITextBuffer buffer, ITextSnapshot before, LineDiff diff, Composition.PompServices services)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            var history = context.UndoManagers.GetTextBufferUndoManager(buffer).TextBufferUndoHistory;
            using (var undo = history.CreateTransaction("Pomp edit"))
            {
                using (var edit = buffer.CreateEdit(EditOptions.None, null, PompEditTag))
                {
                    foreach (var hunk in diff.Hunks) edit.Replace(hunk.Start, hunk.Length, hunk.After);
                    if (edit.HasFailedChanges) { edit.Cancel(); return; }
                    edit.Apply();
                }
                undo.Complete();
            }
            if (ReferenceEquals(buffer.CurrentSnapshot, before)) return;
            // The disk already holds this text: saving leaves the document clean, so Visual Studio neither reloads it nor asks to.
            var rdt = context.GetService<IVsRunningDocumentTable>(typeof(SVsRunningDocumentTable));
            var cookie = context.Rdt.GetDocumentCookie(file);
            services.EditorFollow.QuietSave = buffer;
            try
            {
                if (cookie != VSConstants.VSCOOKIE_NIL)
                    ErrorHandler.ThrowOnFailure(rdt.SaveDocuments((uint)__VSRDTSAVEOPTIONS.RDTSAVEOPT_SaveIfDirty, null, VSConstants.VSITEMID_NIL, cookie));
            }
            catch (Exception ex) { context.LogUnexpected("Saving '" + file + "' after bringing in the tool's edit", ex); }
            finally { services.EditorFollow.QuietSave = null; }
            NotifyApplied(file, buffer, before);
        }

        /// <summary>The diff's hunks as Live changes in the coordinates of <paramref name="snapshot"/>, the text after the edit.</summary>
        private static IReadOnlyList<LiveChange> Loaded(LineDiff diff, ITextSnapshot snapshot)
        {
            var changes = new List<LiveChange>(diff.Hunks.Count);
            var shift = 0;
            foreach (var hunk in diff.Hunks)
            {
                var change = new LiveChange { Start = hunk.Start + shift, Length = hunk.After.Length, Before = hunk.Before, After = hunk.After };
                change.CaptureContext(snapshot);
                changes.Add(change);
                shift += hunk.After.Length - hunk.Length;
            }
            return changes;
        }
    }
}
