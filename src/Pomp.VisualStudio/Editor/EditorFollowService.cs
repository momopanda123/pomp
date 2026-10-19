using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Windows.Threading;
using Microsoft.VisualStudio;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Shell.Interop;
using Microsoft.VisualStudio.Text;
using Pomp.VisualStudio.Adapters;
using Pomp.Diagnostics;
using Pomp.VisualStudio.Composition;
using Pomp.VisualStudio.Interop;
using Pomp.VisualStudio.Options;

namespace Pomp.VisualStudio.Editor
{
    /// <summary>UI-thread, session-scoped provenance and host navigation. Only Record commits agent provenance.</summary>
    internal sealed class EditorFollowService : IDisposable
    {
        private static readonly object RevertTag = new object();
        private static PropertyInfo? frameViewProperty;
        private static MethodInfo? showInFrontMethod;
        private readonly PompServices services;
        private readonly VsAdapterContext context;
        private readonly LiveBookmarkManager bookmarks;
        private readonly DispatcherTimer follow;
        private readonly Dictionary<ITextBuffer, BufferState> buffers = new Dictionary<ITextBuffer, BufferState>();
        private string session = string.Empty;
        private string task = string.Empty;
        private string title = string.Empty;
        /// <summary>Per conversation, the request (task) whose edits the marks show now: "last editing request only" drops older ones.</summary>
        private readonly Dictionary<string, string> markedTask = new Dictionary<string, string>(StringComparer.Ordinal);
        private string? pendingPath;
        private ITrackingPoint? pendingPoint;
        private ITextBuffer? pendingBuffer;
        private bool disposed;
        private bool bookmarksEnabled;
        public event EventHandler? Changed;
        /// <summary>Set while POMP saves a file whose disk text an omp tool wrote (TextBufferAdapter.ShowDiskEditAsync): that save keeps the marks.</summary>
        internal ITextBuffer? QuietSave;
        internal bool Enabled => !disposed && services.Settings.AppliedLiveFollowEnabled;

        public EditorFollowService(PompServices services)
        {
            this.services = services;
            ThreadHelper.ThrowIfNotOnUIThread();
            context = new VsAdapterContext(services.Package, services.JoinableTaskFactory, services.Logger);
            bookmarks = new LiveBookmarkManager(context);
            bookmarksEnabled = services.Settings.AppliedLiveBookmarksEnabled;
            follow = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(400) };
            follow.Tick += OnFollow;
            services.Settings.Changed += OnSettings;
        }

        public void SetSession(string workspace, string sessionId, bool streaming, string taskId, string sessionTitle)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            if (disposed) return;
            var next = string.IsNullOrEmpty(sessionId) ? string.Empty : (VsAdapterContext.NormalizePath(workspace) ?? workspace) + "\n" + sessionId;
            task = taskId ?? string.Empty;
            var nextTitle = sessionTitle ?? string.Empty;
            var switched = session != next;
            if (switched)
            {
                // POMP bookmarks of the previous conversation stay; only the user or "Clean up POMP bookmarks" removes them.
                StopFollow();
                session = next;
            }
            else if (title == nextTitle) return;
            title = nextTitle;
            // A conversation that got (or changed) its title, now or while another one was shown, renames its folder.
            if (session.Length > 0) BookmarkAction(() => bookmarks.Retitle(session, title), "renaming the conversation's bookmark folder");
            if (switched) NotifyChanged();
        }

        public void Record(string path, ITextBuffer buffer, ITextSnapshot before, ITextSnapshot after)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            if (disposed || before == after || (!Enabled && !bookmarksEnabled)) return;
            if (session.Length == 0) { services.Logger.Warn("Live edit provenance unavailable: no active conversation."); return; }
            if (before.TextBuffer != buffer || after.TextBuffer != buffer || after != buffer.CurrentSnapshot || before.Version.Next != after.Version)
            {
                services.Logger.Warn("Live edit provenance skipped because the buffer changed independently: " + path);
                return;
            }
            if (!buffers.ContainsKey(buffer)) RebindReopenedBuffer(buffer, before);
            if (!buffers.TryGetValue(buffer, out var state))
            {
                state = new BufferState(path, buffer, services.Settings);
                buffers.Add(buffer, state);
                state.Save(before);
                Attach(state);
            }
            if (!state.History.TryGetValue(before.Version.VersionNumber, out var previous))
                throw new InvalidOperationException("The live edit baseline is no longer available.");
            previous.Sessions.TryGetValue(session, out var old);
            old = DropEarlierRequest(old);
            var changes = before.Version.Changes;
            if (changes.Count == 0) return;
            if (!state.History.ContainsKey(after.Version.VersionNumber))
            {
                var moved = new Dictionary<string, List<LiveChange>>(StringComparer.Ordinal);
                foreach (var pair in state.Sessions) moved[pair.Key] = LiveChangeTracker.Move(pair.Value, before, after, changes);
                state.Sessions = moved;
            }
            var next = LiveChangeTracker.Record(old ?? new List<LiveChange>(), before, after, changes);
            state.Sessions[session] = next;
            state.Save(after);
            Committed(path, buffer, after, changes.Min(c => c.NewPosition));
        }

        /// <summary>
        /// The buffer already holds a change POMP did not apply itself (an omp file tool wrote the file and the editor loaded it):
        /// <paramref name="changes"/> (positions in the current text, each with its text before) become this conversation's change, as
        /// <see cref="Record"/> would have made it. Earlier marks they overlap give way to them.
        /// </summary>
        public void RecordLoaded(string path, ITextBuffer buffer, IReadOnlyList<LiveChange> changes)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            if (disposed || changes.Count == 0 || (!Enabled && !bookmarksEnabled)) return;
            if (session.Length == 0) { services.Logger.Warn("Live edit provenance unavailable: no active conversation."); return; }
            var current = buffer.CurrentSnapshot;
            if (!buffers.ContainsKey(buffer)) RebindReopenedBuffer(buffer, current);
            if (!buffers.TryGetValue(buffer, out var state))
            {
                state = new BufferState(path, buffer, services.Settings);
                buffers.Add(buffer, state);
                Attach(state);
            }
            state.Sessions.TryGetValue(session, out var old);
            old = DropEarlierRequest(old);
            var next = (old ?? new List<LiveChange>()).Where(e => !changes.Any(c => LiveChangeTracker.Touches(c.Start, c.End, e.Start, e.End))).ToList();
            next.AddRange(changes);
            next.Sort((a, b) => a.Start.CompareTo(b.Start));
            state.Sessions[session] = next;
            state.Save(current);
            Committed(path, buffer, current, changes.Min(c => c.Start));
        }

        /// <summary>With "last editing request only", a new request's first edit in this conversation drops the earlier request's marks in every file.</summary>
        private List<LiveChange>? DropEarlierRequest(List<LiveChange>? old)
        {
            if (services.Settings.AppliedLiveChangeRetention != LiveChangeRetention.LastRequest || task.Length == 0) return old;
            if (markedTask.TryGetValue(session, out var shown) && shown != task)
            {
                var owner = session;
                foreach (var other in buffers.Values) Drop(other, name => name == owner);
                old = null;
            }
            markedTask[session] = task;
            return old;
        }

        /// <summary>A recorded change: its bookmark, the follow to its first line, the views redrawn.</summary>
        private void Committed(string path, ITextBuffer buffer, ITextSnapshot after, int first)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            var line = after.GetLineNumberFromPosition(Math.Min(first, after.Length)) + 1;
            if (bookmarksEnabled)
            {
                if (task.Length > 0) BookmarkAction(() => bookmarks.Mark(path, buffer, line, session, task, title), "marking " + path);
                else services.Logger.Warn("Live bookmark provenance unavailable: no authoritative active task.");
            }
            if (Enabled)
            {
                pendingPath = path; pendingBuffer = buffer;
                pendingPoint = after.CreateTrackingPoint(Math.Min(first, after.Length), PointTrackingMode.Negative);
                follow.Stop(); follow.Start();
            }
            NotifyChanged();
        }

        internal IReadOnlyList<LiveChange> GetChanges(ITextBuffer buffer)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            if (!disposed && !buffers.ContainsKey(buffer)) RebindReopenedBuffer(buffer);
            if (!disposed && session.Length > 0 && buffers.TryGetValue(buffer, out var state) && state.Sessions.TryGetValue(session, out var entries)) return entries;
            return Array.Empty<LiveChange>();
        }

        private void RebindReopenedBuffer(ITextBuffer buffer, ITextSnapshot? baseline = null)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            if (!context.TextDocuments.TryGetTextDocument(buffer, out var document)) return;
            var previous = buffers.Values.FirstOrDefault(s => VsAdapterContext.SamePath(s.Path, document.FilePath));
            if (previous == null) return;
            var old = previous.Buffer.CurrentSnapshot;
            var current = baseline ?? buffer.CurrentSnapshot;
            var identical = old.Length == current.Length;
            for (var i = 0; identical && i < old.Length; i++) identical = old[i] == current[i];
            var replacement = new BufferState(document.FilePath, buffer, services.Settings);
            if (identical) replacement.Sessions = new Dictionary<string, List<LiveChange>>(previous.Sessions, StringComparer.Ordinal);
            else services.Logger.Warn("POMP Live cleared stale ranges after the reopened document changed: " + document.FilePath);
            Detach(previous);
            buffers.Remove(previous.Buffer);
            buffers.Add(buffer, replacement);
            replacement.Save(current);
            Attach(replacement);
        }

        private void OnBufferChanged(object sender, TextContentChangedEventArgs args)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            if (disposed || !(sender is ITextBuffer buffer) || !buffers.TryGetValue(buffer, out var state)) return;
            // VS's reiterated version identifies the exact historical text on Undo/Redo. Never infer undo from equal substrings.
            state.Save(args.Before);
            if (args.After.Version.ReiteratedVersionNumber != args.After.Version.VersionNumber
                && state.History.TryGetValue(args.After.Version.ReiteratedVersionNumber, out var restored))
                state.Sessions = new Dictionary<string, List<LiveChange>>(restored.Sessions, StringComparer.Ordinal);
            else
            {
                var moved = new Dictionary<string, List<LiveChange>>(StringComparer.Ordinal);
                foreach (var pair in state.Sessions) moved[pair.Key] = LiveChangeTracker.Move(pair.Value, args.Before, args.After, args.Changes);
                state.Sessions = moved;
            }
            state.Save(args.After);
            NotifyChanged();
        }

        internal bool Revert(ITextBuffer buffer, LiveChange entry)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            if (disposed || !buffers.TryGetValue(buffer, out var state) || !state.Sessions.TryGetValue(session, out var entries)) return false;
            var index = entries.IndexOf(entry);
            var validatedSnapshot = buffer.CurrentSnapshot;
            var ownerSession = session;
            if (index < 0 || !entry.Matches(validatedSnapshot))
            {
                Status(Pomp.Hosting.PompLanguage.Pick("POMP 변경을 원복할 수 없어요: 기록된 구간이나 주변 내용이 바뀌었어요.", "Cannot revert the POMP change: the recorded range or its surroundings changed."));
                return false;
            }
            try
            {
                using (var undo = context.UndoManagers.GetTextBufferUndoManager(buffer).TextBufferUndoHistory.CreateTransaction(Pomp.Hosting.PompLanguage.Pick("POMP 변경 원복", "Revert POMP change")))
                {
                    var completed = false;
                    try
                    {
                        if (buffer.CurrentSnapshot != validatedSnapshot) throw new InvalidOperationException("The buffer changed before the revert.");
                        using (var edit = buffer.CreateEdit(EditOptions.None, null, RevertTag))
                        {
                            if (!edit.Replace(entry.Start, entry.Length, entry.Before) || edit.HasFailedChanges)
                                throw new InvalidOperationException("The changed range is read-only.");
                            var after = edit.Apply();
                            if (edit.Canceled) throw new InvalidOperationException("The editor canceled the revert.");
                            if (buffer.CurrentSnapshot != after) throw new InvalidOperationException("Another editor callback changed the reverted buffer.");
                            var remaining = new List<LiveChange>(state.Sessions[ownerSession]);
                            remaining.RemoveAt(index);
                            state.Sessions[ownerSession] = remaining;
                            state.Save(after);
                            undo.Complete();
                            completed = true;
                        }
                    }
                    catch
                    {
                        // Never automatically roll back an independent callback's edits while handling a revert error.
                        if (!completed && buffer.CurrentSnapshot != validatedSnapshot) undo.Complete();
                        throw;
                    }
                }
                Status(Pomp.Hosting.PompLanguage.Pick("POMP 변경을 이 대화에서 처음 고치기 전으로 되돌렸어요. 저장하지 않았고, 실행 취소로 되살릴 수 있어요.",
                    "POMP change reverted to before this conversation first changed it. Not saved; Undo restores it."));
                NotifyChanged();
                return true;
            }
            catch (Exception ex)
            {
                context.LogUnexpected("Reverting a POMP change", ex);
                Status(Pomp.Hosting.PompLanguage.Pick("POMP 변경을 원복하지 못했어요: ", "Could not revert the POMP change: ") + ex.Message);
                return false;
            }
        }

        /// <summary>Removes the bookmarks POMP added in this Visual Studio session (user bookmarks stay); returns how many.</summary>
        public int CleanupPompBookmarks()
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            if (disposed) throw new ObjectDisposedException(nameof(EditorFollowService));
            return bookmarks.Cleanup();
        }

        /// <summary>
        /// The editor menu's Clear Changes: drops this conversation's marks in every file (Undo does not bring them back). The text is
        /// not touched. Returns how many changes were dropped.
        /// </summary>
        public int ClearChanges()
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            if (disposed || session.Length == 0) return 0;
            var owner = session;
            var count = 0;
            foreach (var state in buffers.Values)
                if (state.Sessions.TryGetValue(owner, out var entries)) count += entries.Count;
            foreach (var state in buffers.Values) Drop(state, name => name == owner);
            markedTask.Remove(owner);
            NotifyChanged();
            return count;
        }

        /// <summary>Forgets marks of the matching conversations in one file, also in its Undo checkpoints so Undo does not revive them.</summary>
        private static void Drop(BufferState state, Func<string, bool> which)
        {
            foreach (var name in state.Sessions.Keys.Where(which).ToList()) state.Sessions.Remove(name);
            foreach (var checkpoint in state.History.Values)
                foreach (var name in checkpoint.Sessions.Keys.Where(which).ToList()) checkpoint.Sessions.Remove(name);
        }

        private void Attach(BufferState state)
        {
            state.Buffer.Changed += OnBufferChanged;
            if (state.Document != null) state.Document.FileActionOccurred += OnFileAction;
        }

        private void Detach(BufferState state)
        {
            state.Buffer.Changed -= OnBufferChanged;
            if (state.Document != null) state.Document.FileActionOccurred -= OnFileAction;
        }

        /// <summary>"Until the file is saved": saving a file drops its marks (every conversation's; the saved text is the accepted one).</summary>
        private void OnFileAction(object sender, TextDocumentFileActionEventArgs args)
        {
            if ((args.FileActionType & FileActionTypes.ContentSavedToDisk) == 0 || !(sender is ITextDocument document) || ReferenceEquals(document.TextBuffer, QuietSave)) return;
            services.JoinableTaskFactory.RunAsync(async () =>
            {
                await services.JoinableTaskFactory.SwitchToMainThreadAsync();
                if (disposed || services.Settings.AppliedLiveChangeRetention != LiveChangeRetention.UntilSaved
                    || !buffers.TryGetValue(document.TextBuffer, out var state) || state.Sessions.Count == 0) return;
                Drop(state, _ => true);
                NotifyChanged();
            }).Task.Forget(services.Logger, "Dropping Live marks of a saved file");
        }

        private void OnSettings(object? sender, EventArgs args)
        {
            services.JoinableTaskFactory.RunAsync(async () =>
            {
                await services.JoinableTaskFactory.SwitchToMainThreadAsync();
                if (disposed) return;
                if (!Enabled) StopFollow();
                var enabled = services.Settings.AppliedLiveBookmarksEnabled;
                // Turning bookmarks off only stops new ones (existing POMP bookmarks are the user's to delete);
                // turning them on marks later edits and never recreates bookmarks the user removed.
                bookmarksEnabled = enabled;
                NotifyChanged();
            }).Task.Forget(services.Logger, "Updating Live editor features");
        }

        private void NotifyChanged()
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            LiveEditFeature.RefreshViews();
            Changed?.Invoke(this, EventArgs.Empty);
        }

        private void OnFollow(object? sender, EventArgs args)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            follow.Stop();
            var path = pendingPath; var buffer = pendingBuffer; var point = pendingPoint;
            pendingPath = null; pendingBuffer = null; pendingPoint = null;
            if (disposed || !Enabled || path == null || buffer == null || point == null || LiveEditFeature.EditorInteractionActive) return;
            var chat = services.Package.FindToolWindow(typeof(ToolWindow.PompChatToolWindow), 0, false) as ToolWindow.PompChatToolWindow;
            var restoreChat = chat?.Control.HasKeyboardFocus == true;
            try
            {
                var frame = OpenDocumentNoActivate(context, path);
                if (LiveEditFeature.EditorInteractionActive) return;
                ErrorHandler.ThrowOnFailure(frame.ShowNoActivate());
                // ShowNoActivate deliberately leaves tabbed documents behind the selected tab.
                // Select the shell view without Activate/Show, which would steal WebView keyboard focus.
                // These public managed members exist in the VS2017 and current shell; no private COM ABI.
                frameViewProperty ??= frame.GetType().GetProperty("FrameView")
                    ?? throw new NotSupportedException("The shell does not expose nonactivating document selection.");
                var shellView = frameViewProperty.GetValue(frame)
                    ?? throw new InvalidOperationException("The document has no shell view.");
                showInFrontMethod ??= shellView.GetType().GetMethod("ShowInFront", Type.EmptyTypes)
                    ?? throw new NotSupportedException("The shell does not support nonactivating document selection.");
                if (!(showInFrontMethod.Invoke(shellView, null) is true))
                    throw new InvalidOperationException("The shell refused to select the document.");
                var view = VsShellUtilities.GetTextView(frame);
                if (view == null) throw new InvalidOperationException("The document has no text view.");
                var line = point.GetPoint(buffer.CurrentSnapshot).GetContainingLine().LineNumber;
                // A selection in the edited file is the user's: bring the edit into view but leave caret and selection alone.
                if (!LiveEditFeature.HasSelection(buffer)) ErrorHandler.ThrowOnFailure(view.SetCaretPos(line, 0));
                ErrorHandler.ThrowOnFailure(view.CenterLines(line, 1));
                Status(Pomp.Hosting.PompLanguage.Pick("POMP Live: " + Path.GetFileName(path) + " " + (line + 1) + "번째 줄을 POMP가 고쳐서 열었어요.",
                    "POMP Live opened " + Path.GetFileName(path) + " because POMP edited line " + (line + 1) + "."));
            }
            catch (Exception ex) { context.LogUnexpected("Following a POMP edit", ex); Status(Pomp.Hosting.PompLanguage.Pick("POMP Live: 고친 파일을 열지 못했어요: ", "POMP Live could not show the edited file: ") + ex.Message); }
            finally
            {
                // Selecting an already-open shell tab can activate its editor despite ShowInFront.
                // Restore only the chat that owned focus before this synchronous operation.
                if (restoreChat && chat != null)
                {
                    // Restore the shell's active pane as well as WebView focus; otherwise the shell
                    // can subsequently restore focus to the newly selected document tab. This runs in a timer tick:
                    // a frame that is closing (or a shell shutting down) must not turn into an unhandled exception.
                    try
                    {
                        if (chat.Frame is IVsWindowFrame chatFrame) ErrorHandler.ThrowOnFailure(chatFrame.Show());
                        chat.Control.FocusChat();
                    }
                    catch (Exception ex) { context.LogUnexpected("Returning focus to the chat after a POMP edit", ex); }
                }
            }
        }

        /// <summary>Loads docdata without Show/Activate; callers opt into showing only after interaction checks.</summary>
        internal static IVsWindowFrame OpenDocumentNoActivate(VsAdapterContext context, string path, bool show = false)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            var opener = context.GetService<IVsUIShellOpenDocument>(typeof(SVsUIShellOpenDocument));
            var logicalView = VSConstants.LOGVIEWID_TextView;
            ErrorHandler.ThrowOnFailure(opener.OpenDocumentViaProject(path, ref logicalView, out _, out _, out _, out var frame));
            if (frame == null) throw new InvalidOperationException("Visual Studio did not create an editor for " + path);
            if (show) ErrorHandler.ThrowOnFailure(frame.ShowNoActivate());
            return frame;
        }

        private void BookmarkAction(Action action, string operation)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            try { action(); }
            catch (Exception ex)
            {
                context.LogUnexpected("POMP Live " + operation, ex);
                Status(Pomp.Hosting.PompLanguage.Pick("POMP 책갈피를 처리하지 못했어요: ", "POMP bookmarks failed: ") + ex.Message);
            }
        }

        private void Status(string message)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            try { ErrorHandler.ThrowOnFailure(context.GetService<IVsStatusbar>(typeof(SVsStatusbar)).SetText(message)); }
            catch (Exception ex) { context.LogUnexpected("Updating POMP Live status", ex); }
        }

        private void StopFollow() { follow.Stop(); pendingPath = null; pendingBuffer = null; pendingPoint = null; }
        public void Dispose()
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            if (disposed) return;
            disposed = true;
            StopFollow(); follow.Tick -= OnFollow;
            services.Settings.Changed -= OnSettings;
            foreach (var state in buffers.Values) Detach(state);
            buffers.Clear();
            BookmarkAction(bookmarks.Dispose, "disposing owned bookmarks");
            NotifyChanged();
            Changed = null;
        }

        private sealed class BufferState
        {
            // Undo/Redo restores the marks of a historical version. Only the newest versions are kept (Options › Editor › "Live change
            // history per file"); undoing further back moves the current marks instead, so a long-edited buffer stays bounded.
            private readonly PompOptionsPage settings;
            public BufferState(string path, ITextBuffer buffer, PompOptionsPage settings)
            {
                Path = path; Buffer = buffer; this.settings = settings;
                buffer.Properties.TryGetProperty(typeof(ITextDocument), out Document);
            }
            public readonly string Path;
            public readonly ITextBuffer Buffer;
            /// <summary>The file's document (its save events), when the buffer has one.</summary>
            public readonly ITextDocument? Document;
            public Dictionary<string, List<LiveChange>> Sessions = new Dictionary<string, List<LiveChange>>(StringComparer.Ordinal);
            public readonly Dictionary<int, Checkpoint> History = new Dictionary<int, Checkpoint>();
            public void Save(ITextSnapshot snapshot)
            {
                History[snapshot.Version.VersionNumber] = new Checkpoint(Sessions);
                // A lowered limit trims the excess on the next save; the version just saved is always the newest and stays.
                var limit = settings.AppliedLiveHistoryLimit;
                while (History.Count > limit) History.Remove(History.Keys.Min());
            }
        }
        private sealed class Checkpoint
        {
            public Checkpoint(Dictionary<string, List<LiveChange>> sessions) { Sessions = new Dictionary<string, List<LiveChange>>(sessions, StringComparer.Ordinal); }
            public readonly Dictionary<string, List<LiveChange>> Sessions;
        }
    }
}
