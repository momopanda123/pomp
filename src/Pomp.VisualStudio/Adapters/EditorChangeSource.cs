using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.VisualStudio.Threading;
using Pomp.Adapters;
using Pomp.Features;
using Pomp.Hosting;
using Pomp.Tools;
using Task = System.Threading.Tasks.Task;

namespace Pomp.VisualStudio.Adapters
{
    /// <summary>
    /// The user's own edits as review candidates: open documents whose buffer differs from the saved file, and files git reports as
    /// modified against HEAD. Current text is what the editor shows (buffer, or the file read the way VS reads it); original is the
    /// HEAD version checked out with the work tree's filters, or the saved file when HEAD has none.
    /// </summary>
    internal sealed class EditorChangeSource : IEditorChangeSource
    {
        private const int MaxChars = 500000;
        private readonly VsAdapterContext context;
        private readonly GitAdapter git;
        private readonly TextBufferAdapter buffers;

        public EditorChangeSource(VsAdapterContext context, GitAdapter git, TextBufferAdapter buffers)
        {
            this.context = context;
            this.git = git;
            this.buffers = buffers;
        }

        public async Task<IReadOnlyList<EditorChangeEntry>> ListAsync(string workspaceRoot, CancellationToken cancellationToken)
        {
            var guard = new PathGuard(workspaceRoot);
            var entries = new Dictionary<string, EditorChangeEntry>(StringComparer.OrdinalIgnoreCase);
            foreach (var path in await UnsavedAsync(guard, workspaceRoot, cancellationToken).ConfigureAwait(false)) entries[path] = new EditorChangeEntry(path, true);
            await TaskScheduler.Default;
            var repository = await git.OpenRepositoryAsync(cancellationToken).ConfigureAwait(false);
            if (repository == null) return entries.Values.ToList();
            var status = await repository.GetStatusAsync(cancellationToken).ConfigureAwait(false);
            foreach (var change in status.Changes)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (!IsModified(change)) continue;
                var full = Path.GetFullPath(Path.Combine(repository.RepositoryRoot, change.Path.Replace('/', Path.DirectorySeparatorChar)));
                if (entries.ContainsKey(full) || !guard.IsInsideRoot(full) || !File.Exists(full)) continue;
                entries[full] = new EditorChangeEntry(full, false);
            }

            return entries.Values.ToList();
        }

        public async Task<IReadOnlyList<EditorChangeText>> ReadAsync(string workspaceRoot, IReadOnlyList<string> paths, CancellationToken cancellationToken)
        {
            await TaskScheduler.Default;
            var guard = new PathGuard(workspaceRoot);
            var resolved = paths.Select(path => guard.Resolve(path)).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            var unsaved = new HashSet<string>(await UnsavedAsync(guard, workspaceRoot, cancellationToken).ConfigureAwait(false), StringComparer.OrdinalIgnoreCase);
            await TaskScheduler.Default;
            var repository = await git.OpenRepositoryAsync(cancellationToken).ConfigureAwait(false);
            var result = new List<EditorChangeText>();
            foreach (var path in resolved)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (!File.Exists(path)) continue;
                var snapshot = await buffers.ReadAsync(path, cancellationToken).ConfigureAwait(false);
                if (snapshot == null) continue;
                var current = snapshot.Text;
                string? original = repository != null && repository.Contains(path) ? await repository.ReadHeadTextAsync(path, cancellationToken).ConfigureAwait(false) : null;
                if (original == null && unsaved.Contains(path)) original = TextBufferAdapter.ReadDiskText(path);
                if (original == null || original == current) continue;
                if (original.Length > MaxChars || current.Length > MaxChars)
                    throw new InvalidOperationException(PompLanguage.Pick("파일이 너무 커요: ", "The file is too large: ") + Path.GetFileName(path));
                // git decodes HEAD as UTF-8: a file in another encoding would compare as changed everywhere.
                if (original.IndexOf('�') >= 0 && current.IndexOf('�') < 0)
                    throw new InvalidOperationException(PompLanguage.Pick("UTF-8이 아닌 파일이에요: ", "The file is not UTF-8: ") + Path.GetFileName(path));
                result.Add(new EditorChangeText(path, original, current));
            }

            return result;
        }

        // Modified in the index or the work tree; added, deleted, renamed and copied files have no single HEAD original here.
        private static bool IsModified(GitFileChange change) =>
            (change.IndexStatus == 'M' || change.WorkTreeStatus == 'M' || change.IndexStatus == 'T' || change.WorkTreeStatus == 'T')
            && change.IndexStatus != 'A' && change.IndexStatus != 'D' && change.WorkTreeStatus != 'D' && change.IndexStatus != 'R' && change.IndexStatus != 'C';

        /// <summary>Open documents inside the workspace whose text buffer has unsaved changes.</summary>
        private async Task<IReadOnlyList<string>> UnsavedAsync(PathGuard guard, string workspaceRoot, CancellationToken cancellationToken)
        {
            await context.Jtf.SwitchToMainThreadAsync(cancellationToken);
            if (!context.IsCurrentWorkspaceRoot(workspaceRoot)) throw new InvalidOperationException(PompLanguage.Pick("작업 영역이 바뀌었어요.", "The workspace changed."));
            return DocumentTable.EnumerateOpen(context)
                .Where(document => guard.IsInsideRoot(document.Path) && DocumentTable.IsDirty(context, document.Path) == true && DocumentTable.TryGetTextBuffer(context, document.DocData) != null)
                .Select(document => document.Path).ToList();
        }
    }
}
