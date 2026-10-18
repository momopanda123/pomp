using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Text;
using Microsoft.VisualStudio.Text.Operations;
using Microsoft.VisualStudio.Threading;
using Pomp.Features;
using Pomp.Tools;
using Task = System.Threading.Tasks.Task;

namespace Pomp.VisualStudio.Adapters
{
    internal sealed partial class TextBufferAdapter : IWorkspaceEditApplier
    {
        public async Task<WorkspaceApplyResult> ApplyAsync(ShadowSnapshot snapshot, string workspaceRoot, CancellationToken cancellationToken)
        {
            if (snapshot == null || snapshot.Files.Count == 0 || snapshot.Files.Count > ShadowWorkspace.MaxFiles)
                throw new ArgumentException("A bounded, non-empty shadow snapshot is required.");
            await TaskScheduler.Default;
            var guard = new PathGuard(workspaceRoot);
            var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var prepared = new List<WorkspaceBufferEdit>();
            foreach (var file in snapshot.Files)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var path = guard.Resolve(file.Path);
                if (!paths.Add(path)) throw new ArgumentException("Duplicate workspace edit target.");
                if (!File.Exists(path)) throw new ArgumentException("Shadow application currently requires an existing source document: " + path);
                prepared.Add(new WorkspaceBufferEdit(file, path, LineDiff.Create(file.Before, file.After, cancellationToken)));
            }
            await context.Jtf.SwitchToMainThreadAsync(cancellationToken);
            var outcomes = new List<WorkspaceFileOutcome>();
            string? error = null;
            try
            {
                if (!context.IsCurrentWorkspaceRoot(workspaceRoot)) throw new InvalidOperationException("Workspace changed after approval.");
                var buffers = new HashSet<ITextBuffer>();
                foreach (var entry in prepared)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var buffer = DocumentTable.TryGetTextBuffer(context, entry.Path);
                    if (buffer == null)
                    {
                        OpenEditTarget(entry.Path);
                        buffer = DocumentTable.TryGetTextBuffer(context, entry.Path);
                    }
                    if (buffer == null || !buffer.CheckEditAccess()) throw new InvalidOperationException("Source is not editable: " + entry.Path);
                    if (!buffers.Add(buffer)) throw new InvalidOperationException("Two paths refer to the same editor buffer.");
                    if (!string.Equals(buffer.CurrentSnapshot.GetText(), entry.File.Before, StringComparison.Ordinal))
                        throw new InvalidOperationException("Source changed since review: " + entry.Path);
                    entry.Buffer = buffer;
                    entry.BeforeSnapshot = buffer.CurrentSnapshot;
                }
                // All targets are open and checked before the first change. Keep their undo transactions
                // open until every Apply succeeds, so a normal failure can cancel the entire batch.
                foreach (var entry in prepared)
                {
                    entry.Undo = context.UndoManagers.GetTextBufferUndoManager(entry.Buffer!).TextBufferUndoHistory.CreateTransaction("Pomp workspace edit");
                    entry.Edit = entry.Buffer!.CreateEdit(EditOptions.None, null, PompEditTag);
                    foreach (var hunk in entry.Diff.Hunks)
                        if (!entry.Edit.Replace(hunk.Start, hunk.Length, hunk.After)) throw new InvalidOperationException("A changed range is read-only: " + entry.Path);
                    if (entry.Edit.HasFailedChanges) throw new InvalidOperationException("A changed range could not be prepared: " + entry.Path);
                }
                foreach (var entry in prepared)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    if (!context.IsCurrentWorkspaceRoot(workspaceRoot) || entry.Buffer!.CurrentSnapshot.GetText() != entry.File.Before)
                        throw new InvalidOperationException("Workspace/source changed while applying the batch.");
                    entry.Edit!.Apply();
                    if (entry.Edit.Canceled || entry.Buffer.CurrentSnapshot.GetText() != entry.File.After)
                        throw new InvalidOperationException("Visual Studio did not apply the reviewed result: " + entry.Path);
                }
                foreach (var entry in prepared)
                {
                    if (entry.Buffer!.CurrentSnapshot.GetText() != entry.File.After)
                        throw new InvalidOperationException("An editor callback changed a previously applied source.");
                }
                foreach (var entry in prepared) { entry.Undo!.Complete(); entry.Completed = true; }
                outcomes.AddRange(prepared.Select(e => new WorkspaceFileOutcome(e.Path, WorkspaceFileState.Applied)));
            }
            catch (Exception ex)
            {
                error = ex is OperationCanceledException ? "Workspace application was cancelled." : ex.Message;
                // Release uncommitted ITextEdits before trying to undo anything.
                foreach (var entry in prepared) { try { entry.Edit?.Dispose(); } catch { } entry.Edit = null; }
                foreach (var entry in prepared.AsEnumerable().Reverse())
                {
                    try
                    {
                        var current = entry.Buffer?.CurrentSnapshot.GetText();
                        if (current != null && current != entry.File.Before && current != entry.File.After)
                        {
                            // Never overwrite an unrelated callback/user change just to report atomicity.
                            if (entry.Undo != null && !entry.Completed) { entry.Undo.Complete(); entry.Completed = true; }
                            outcomes.Add(new WorkspaceFileOutcome(entry.Path, WorkspaceFileState.Conflict, "Source changed independently; automatic rollback refused."));
                            continue;
                        }
                        if (entry.Undo != null)
                        {
                            if (!entry.Completed) entry.Undo.Cancel();
                            else if (current != entry.File.Before)
                            {
                                if (!ReferenceEquals(entry.Undo.History.UndoStack.FirstOrDefault(), entry.Undo))
                                    throw new InvalidOperationException("The undo stack changed independently.");
                                entry.Undo.History.Undo(1);
                            }
                        }
                        var restored = entry.Buffer == null || entry.Buffer.CurrentSnapshot.GetText() == entry.File.Before;
                        outcomes.Add(new WorkspaceFileOutcome(entry.Path, restored ? WorkspaceFileState.RolledBack : WorkspaceFileState.Applied,
                            restored ? null : "Automatic rollback did not restore the original text."));
                    }
                    catch (Exception rollbackError)
                    {
                        outcomes.Add(new WorkspaceFileOutcome(entry.Path, WorkspaceFileState.Conflict, "Rollback could not be confirmed: " + rollbackError.Message));
                    }
                }
            }
            finally
            {
                foreach (var entry in prepared)
                {
                    try { entry.Edit?.Dispose(); } catch (Exception ex) { context.LogUnexpected("Disposing workspace edit", ex); }
                    try { entry.Undo?.Dispose(); } catch (Exception ex) { context.LogUnexpected("Disposing workspace undo", ex); }
                }
            }
            if (error == null)
                foreach (var entry in prepared)
                    NotifyApplied(entry.Path, entry.Buffer!, entry.BeforeSnapshot!);
            return new WorkspaceApplyResult(outcomes, error);
        }

        private sealed class WorkspaceBufferEdit
        {
            public WorkspaceBufferEdit(ShadowFile file, string path, LineDiff diff) { File = file; Path = path; Diff = diff; }
            public ShadowFile File { get; }
            public string Path { get; }
            public LineDiff Diff { get; }
            public ITextBuffer? Buffer;
            public ITextSnapshot? BeforeSnapshot;
            public ITextEdit? Edit;
            public ITextUndoTransaction? Undo;
            public bool Completed;
        }
    }
}
