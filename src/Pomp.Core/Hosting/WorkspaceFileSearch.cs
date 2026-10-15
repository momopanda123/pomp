using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Pomp.Adapters;
using Pomp.Diagnostics;
using Pomp.Internal;
using Pomp.Ui;

namespace Pomp.Hosting
{
    /// <summary>
    /// Files for the chat UI's @-mention list (workspace/findFiles): open documents first, then the solution's project items, and
    /// when those do not fill the list, a bounded walk of the workspace folder that skips build output and tool folders.
    /// </summary>
    internal static class WorkspaceFileSearch
    {
        internal const int DefaultLimit = 30;
        internal const int MaxLimit = 100;
        private const int MaxScannedEntries = 20000;
        private static readonly TimeSpan AdapterTimeout = TimeSpan.FromSeconds(3);
        private static readonly TimeSpan WalkBudget = TimeSpan.FromMilliseconds(1500);

        private static readonly HashSet<string> SkippedDirectories = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "bin", "obj", ".git", ".vs", ".svn", ".hg", ".idea", "node_modules", "packages", "TestResults", ".agents",
        };

        public static async Task<WorkspaceFilesPayload> FindAsync(
            IVsAdapterFactory adapters, string? root, string query, int limit, IPompLogger logger, CancellationToken cancellationToken)
        {
            query = (query ?? string.Empty).Trim().Replace('\\', '/');
            limit = Math.Max(1, Math.Min(MaxLimit, limit));
            var candidates = new Dictionary<string, Candidate>(StringComparer.OrdinalIgnoreCase);

            var open = await AdapterGuard.TryRunAsync(adapters.Editor.GetOpenDocumentsAsync, AdapterTimeout, logger, "mention search: open documents", cancellationToken)
                .ConfigureAwait(false);
            if (open.Succeeded && open.Value != null)
            {
                foreach (var document in open.Value)
                {
                    Add(candidates, root, document?.Path, isOpen: true);
                }
            }

            var solutionQuery = new SolutionQuery { Pattern = query.Length == 0 ? null : query, Depth = 20, MaxResults = 2000 };
            var nodes = await AdapterGuard.TryRunAsync(ct => adapters.Solution.QueryAsync(solutionQuery, ct), AdapterTimeout, logger, "mention search: solution", cancellationToken)
                .ConfigureAwait(false);
            if (nodes.Succeeded && nodes.Value != null)
            {
                foreach (var file in Flatten(nodes.Value))
                {
                    Add(candidates, root, file, isOpen: false);
                }
            }

            var matches = Rank(candidates.Values, query);
            if (matches.Count < limit && !string.IsNullOrEmpty(root))
            {
                // A folder without a solution, or files that are not project items (README, configs...).
                await Task.Run(() => Walk(candidates, root!, query, cancellationToken), cancellationToken).ConfigureAwait(false);
                matches = Rank(candidates.Values, query);
            }

            return new WorkspaceFilesPayload
            {
                Files = matches.Take(limit).Select(c => new WorkspaceFileEntry { Path = c.Display, FullPath = c.FullPath }).ToList(),
                Truncated = matches.Count > limit,
            };
        }

        private static IEnumerable<string?> Flatten(IEnumerable<SolutionNode> nodes)
        {
            var stack = new Stack<SolutionNode>(nodes.Where(n => n != null).Reverse());
            while (stack.Count > 0)
            {
                var node = stack.Pop();
                if (string.Equals(node.Kind, "file", StringComparison.OrdinalIgnoreCase))
                {
                    yield return node.Path;
                }

                if (node.Children != null)
                {
                    for (var i = node.Children.Count - 1; i >= 0; i--)
                    {
                        if (node.Children[i] != null)
                        {
                            stack.Push(node.Children[i]);
                        }
                    }
                }
            }
        }

        private static void Walk(Dictionary<string, Candidate> candidates, string root, string query, CancellationToken cancellationToken)
        {
            var watch = Stopwatch.StartNew();
            var scanned = 0;
            var pending = new Queue<string>();
            pending.Enqueue(root);
            while (pending.Count > 0 && scanned < MaxScannedEntries && watch.Elapsed < WalkBudget)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var directory = pending.Dequeue();
                IEnumerable<string> entries;
                try
                {
                    entries = Directory.EnumerateFileSystemEntries(directory).Take(Math.Max(0, MaxScannedEntries - scanned)).ToList();
                }
                catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException || ex is System.Security.SecurityException)
                {
                    continue;
                }

                foreach (var entry in entries)
                {
                    if (++scanned > MaxScannedEntries)
                    {
                        break;
                    }

                    FileAttributes attributes;
                    try
                    {
                        attributes = File.GetAttributes(entry);
                    }
                    catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
                    {
                        continue;
                    }

                    if ((attributes & (FileAttributes.ReparsePoint | FileAttributes.Hidden | FileAttributes.System)) != 0)
                    {
                        continue;
                    }

                    if ((attributes & FileAttributes.Directory) != 0)
                    {
                        // .pomp/local holds POMP's own data (conversations, checkpoints, work trees); the rest of .pomp is the project's.
                        if (!SkippedDirectories.Contains(Path.GetFileName(entry)) && !Pomp.Features.ProjectData.IsLocalFolder(entry))
                        {
                            pending.Enqueue(entry);
                        }

                        continue;
                    }

                    if (query.Length == 0 || Relative(root, entry).IndexOf(query, StringComparison.OrdinalIgnoreCase) >= 0)
                    {
                        Add(candidates, root, entry, isOpen: false);
                    }
                }
            }
        }

        private static void Add(Dictionary<string, Candidate> candidates, string? root, string? path, bool isOpen)
        {
            if (string.IsNullOrWhiteSpace(path) || !Path.IsPathRooted(path) || path!.IndexOf("://", StringComparison.Ordinal) >= 0)
            {
                return;
            }

            string full;
            try
            {
                full = Path.GetFullPath(path);
            }
            catch (Exception ex) when (ex is ArgumentException || ex is NotSupportedException || ex is PathTooLongException)
            {
                return;
            }

            if (candidates.TryGetValue(full, out var existing))
            {
                existing.IsOpen |= isOpen;
                return;
            }

            candidates[full] = new Candidate(full, root == null ? full : Relative(root, full), isOpen);
        }

        /// <summary>Workspace-relative with forward slashes when inside <paramref name="root"/>, otherwise the full path.</summary>
        internal static string Relative(string root, string full)
        {
            var prefix = root.Replace('\\', '/').TrimEnd('/') + "/";
            var normalized = full.Replace('\\', '/');
            return normalized.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) ? normalized.Substring(prefix.Length) : full;
        }

        /// <summary>Best first: file name starts with the query, then contains it, then only the path does; open documents lead each group.</summary>
        private static List<Candidate> Rank(IEnumerable<Candidate> candidates, string query)
        {
            return candidates
                .Select(c => new { Candidate = c, Score = Score(c, query) })
                .Where(x => x.Score >= 0)
                .OrderBy(x => x.Score)
                .ThenBy(x => x.Candidate.IsOpen ? 0 : 1)
                .ThenBy(x => x.Candidate.Display.Length)
                .ThenBy(x => x.Candidate.Display, StringComparer.OrdinalIgnoreCase)
                .Select(x => x.Candidate)
                .ToList();
        }

        private static int Score(Candidate candidate, string query)
        {
            if (query.Length == 0)
            {
                return candidate.IsOpen ? 0 : 1;
            }

            var name = Path.GetFileName(candidate.FullPath);
            if (name.StartsWith(query, StringComparison.OrdinalIgnoreCase))
            {
                return 0;
            }

            if (name.IndexOf(query, StringComparison.OrdinalIgnoreCase) >= 0)
            {
                return 1;
            }

            return candidate.Display.IndexOf(query, StringComparison.OrdinalIgnoreCase) >= 0 ? 2 : -1;
        }

        private sealed class Candidate
        {
            public Candidate(string fullPath, string display, bool isOpen)
            {
                FullPath = fullPath;
                Display = display;
                IsOpen = isOpen;
            }

            public string FullPath { get; }

            public string Display { get; }

            public bool IsOpen { get; set; }
        }
    }
}
