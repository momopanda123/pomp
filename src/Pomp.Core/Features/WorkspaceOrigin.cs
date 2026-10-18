using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Pomp.Adapters;
using Pomp.Diagnostics;
using Pomp.Hosting;
using Pomp.Internal;

namespace Pomp.Features
{
    /// <summary>
    /// Which workspace a <c>.pomp/local</c> belongs to (<c>.pomp/local/workspace.json</c>), and what happens when it is opened at
    /// another root. Root-stamped stores refuse data of another root and omp sessions record their cwd, so a <c>.pomp</c> copied
    /// from another project would break those stores and could even resume a conversation in the other project.
    /// <list type="bullet">
    /// <item>Copied (the recorded root still has its own <c>.pomp/local</c>): the records of work done there (conversations,
    /// checkpoints, presets, caches, isolation copies, ...) go to the Recycle Bin. Settings the user made stay: the shared
    /// <c>.pomp</c> files, knowledge, role pictures and automation rules.</item>
    /// <item>Moved or renamed (the recorded root has no <c>.pomp/local</c> any more, or is this very folder by another path):
    /// root stamps and paths under the old root are rewritten to the new one, so the history stays. omp's own session files
    /// are left alone: omp adopts a recorded cwd only while it exists, so it runs in the new root. The old root is remembered
    /// (<see cref="IsCurrentOrPrevious"/>) for the checks that compare a session's cwd with the workspace.</item>
    /// </list>
    /// </summary>
    internal static class WorkspaceOrigin
    {
        internal const string MarkerName = "workspace.json";
        private const int MaxPreviousRoots = 8;
        private const long MaxRebaseBytes = 16L * 1024 * 1024;

        // Settings the user made: they make sense in any workspace. Everything else in .pomp/local is a record of work in the
        // workspace it was made in, so a store added later is dropped on copy unless it is listed here.
        private static readonly string[] KeptOnCopy = { ".gitignore", MarkerName, "knowledge.json", "avatars.json", "agent-hooks.json", "engine-plugin" };

        // Stores that stamp the workspace root or hold absolute paths under it.
        private static readonly string[] RebasedFiles = { "recent-sessions.json", "context-presets.json", "reviews.json", "background-agents.json",
            "code-summaries.json", "composer-workflows.json", "terminal-jobs.json" };
        private const string CheckpointFolder = "checkpoints";
        private const string IsolationFolder = "wt";

        internal enum Change { None, Copied, Moved }

        internal sealed class Outcome
        {
            public Outcome(Change change, string? from, string? quarantine = null, bool recycled = false)
            {
                Change = change; From = from; Quarantine = quarantine; Recycled = recycled;
            }
            public Change Change { get; }
            /// <summary>The root the data was recorded for.</summary>
            public string? From { get; }
            /// <summary>The folder the dropped records were gathered in; gone when <see cref="Recycled"/>.</summary>
            public string? Quarantine { get; }
            public bool Recycled { get; }
        }

        private static StringComparison Comparison => Path.DirectorySeparatorChar == '\\' ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;

        private static string Normalize(string path) => Path.GetFullPath(path).TrimEnd('\\', '/');

        private static bool SamePath(string first, string second) => string.Equals(Normalize(first), Normalize(second), Comparison);

        /// <summary>
        /// Checks the marker of <paramref name="root"/>'s <c>.pomp/local</c> and, when it was recorded for another root, drops or
        /// rebases its data as described on the class. Runs before any store of the workspace is used.
        /// </summary>
        internal static async Task<Outcome> ReconcileAsync(string root, IPompRecycleBin? recycleBin, IPompLogger logger, CancellationToken token)
        {
            var local = ProjectData.Prepare(root);
            if (!Directory.Exists(local)) return new Outcome(Change.None, null);
            var marker = Path.Combine(local, MarkerName);
            Outcome outcome;
            string? quarantine = null;
            using (var lease = await AcquireAsync(marker + ".lock", token).ConfigureAwait(false))
            {
                List<string> previous;
                string? recorded;
                try { recorded = ReadMarker(marker, out previous); }
                catch (JsonException) { recorded = null; previous = new List<string>(); }
                recorded ??= RecordedRoot(local);
                if (recorded == null || SamePath(recorded, root))
                {
                    if (recorded == null || !File.Exists(marker)) WriteMarker(marker, root, previous);
                    return new Outcome(Change.None, null);
                }
                if (Directory.Exists(ProjectData.LocalFolder(recorded)) && !SameFolder(local, ProjectData.LocalFolder(recorded)))
                {
                    quarantine = Gather(local, recorded, name => !KeptOnCopy.Contains(name, StringComparer.OrdinalIgnoreCase) && !IsKeptLock(name));
                    // The copy's earlier roots are the other project's history, not this one's.
                    WriteMarker(marker, root, Array.Empty<string>());
                    outcome = new Outcome(Change.Copied, recorded, quarantine);
                }
                else
                {
                    Rebase(local, recorded, root, logger);
                    // omp's isolation copies borrow the repository's objects by absolute path; they are transient leftovers anyway.
                    quarantine = Gather(local, recorded, name => string.Equals(name, IsolationFolder, StringComparison.OrdinalIgnoreCase));
                    var roots = new List<string> { Normalize(recorded) };
                    roots.AddRange(previous.Where(p => !roots.Any(r => SamePath(r, p)) && !SamePath(p, root)));
                    WriteMarker(marker, root, roots.Take(MaxPreviousRoots));
                    outcome = new Outcome(Change.Moved, recorded, quarantine);
                }
            }
            if (quarantine == null || recycleBin == null) return outcome;
            try
            {
                await recycleBin.RecycleAsync(new[] { quarantine }, token).ConfigureAwait(false);
                return new Outcome(outcome.Change, outcome.From, quarantine, recycled: true);
            }
            catch (Exception ex) when (!(ex is OperationCanceledException))
            {
                // The records stay gathered in the quarantine folder, where no store reads them; the user can delete it.
                logger.Warn("Could not recycle " + quarantine + ": " + ex.Message);
                return outcome;
            }
        }

        /// <summary>Whether <paramref name="path"/> is this workspace's root or a root it was moved from.</summary>
        internal static bool IsCurrentOrPrevious(string root, string path)
        {
            if (SamePath(root, path)) return true;
            try
            {
                ReadMarker(Path.Combine(ProjectData.LocalFolder(root), MarkerName), out var previous);
                return previous.Any(p => SamePath(p, path));
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException || ex is JsonException || ex is ArgumentException || ex is NotSupportedException) { return false; }
        }

        private static async Task<FileStream> AcquireAsync(string path, CancellationToken token)
        {
            CheckpointStore.CheckDirectPath(path);
            for (var attempt = 0; ; attempt++)
            {
                try { return new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None); }
                catch (IOException) when (attempt < 100) { await Task.Delay(100, token).ConfigureAwait(false); }
            }
        }

        private static string? ReadMarker(string marker, out List<string> previous)
        {
            previous = new List<string>();
            CheckpointStore.CheckDirectPath(marker);
            if (!File.Exists(marker)) return null;
            var data = JObject.Parse(File.ReadAllText(marker));
            if ((int?)data["version"] != 1 || !(data["root"] is JValue rootValue) || rootValue.Type != JTokenType.String) return null;
            if (data["previousRoots"] is JArray roots)
                previous.AddRange(roots.Where(r => r.Type == JTokenType.String).Select(r => (string)r!).Where(r => r.Length > 0 && Path.IsPathRooted(r)));
            var root = (string)rootValue!;
            return Path.IsPathRooted(root) ? root : null;
        }

        private static void WriteMarker(string marker, string root, IEnumerable<string> previous)
        {
            CheckpointStore.CheckDirectPath(marker);
            AtomicFile.WriteAllText(marker, new JObject { ["version"] = 1, ["root"] = Normalize(root), ["previousRoots"] = new JArray(previous.Cast<object>().ToArray()) }.ToString(Formatting.Indented));
        }

        /// <summary>A <c>.pomp/local</c> from before the marker: the root its root-stamped stores were written for.</summary>
        private static string? RecordedRoot(string local)
        {
            var files = RebasedFiles.Select(name => Path.Combine(local, name)).ToList();
            var checkpoints = Path.Combine(local, CheckpointFolder);
            if (Directory.Exists(checkpoints)) files.AddRange(Directory.EnumerateFiles(checkpoints, "*.json").Take(8));
            foreach (var file in files)
            {
                try
                {
                    if (!File.Exists(file) || new FileInfo(file).Length > MaxRebaseBytes) continue;
                    var root = JObject.Parse(File.ReadAllText(file))["root"];
                    if (root?.Type == JTokenType.String && Path.IsPathRooted((string)root!)) return (string)root!;
                }
                catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException || ex is JsonException) { }
            }
            return null;
        }

        /// <summary>Whether two folder paths are the same folder (a drive mapping, junction or other alias), by a probe file.</summary>
        private static bool SameFolder(string folder, string other)
        {
            var name = ".origin-probe-" + Guid.NewGuid().ToString("N");
            var probe = Path.Combine(folder, name);
            try
            {
                File.WriteAllText(probe, string.Empty);
                return File.Exists(Path.Combine(other, name));
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException) { return false; }
            finally
            {
                try { File.Delete(probe); } catch (IOException) { } catch (UnauthorizedAccessException) { }
            }
        }

        private static bool IsKeptLock(string name) =>
            name.EndsWith(".lock", StringComparison.OrdinalIgnoreCase) && KeptOnCopy.Contains(name.Substring(0, name.Length - ".lock".Length), StringComparer.OrdinalIgnoreCase);

        /// <summary>Moves the matching entries of <paramref name="local"/> into one new folder there; null when nothing matched.</summary>
        private static string? Gather(string local, string recorded, Func<string, bool> drop)
        {
            var entries = Directory.EnumerateFileSystemEntries(local).Where(entry => drop(Path.GetFileName(entry))).ToList();
            if (entries.Count == 0) return null;
            var leaf = Path.GetFileName(Normalize(recorded));
            var safe = new string((leaf.Length == 0 ? "workspace" : leaf).Select(c => Path.GetInvalidFileNameChars().Contains(c) ? '_' : c).ToArray());
            var quarantine = Path.Combine(local, "pomp-records-of-" + safe + "-" + DateTime.Now.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture));
            CheckpointStore.CheckDirectPath(quarantine);
            Directory.CreateDirectory(quarantine);
            foreach (var entry in entries)
            {
                var target = Path.Combine(quarantine, Path.GetFileName(entry));
                if (Directory.Exists(entry)) Directory.Move(entry, target); else File.Move(entry, target);
            }
            return quarantine;
        }

        private static void Rebase(string local, string from, string to, IPompLogger logger)
        {
            var files = RebasedFiles.Select(name => Path.Combine(local, name)).Where(File.Exists).ToList();
            var checkpoints = Path.Combine(local, CheckpointFolder);
            if (Directory.Exists(checkpoints)) files.AddRange(Directory.EnumerateFiles(checkpoints, "*.json"));
            var old = Normalize(from);
            foreach (var file in files)
            {
                try
                {
                    CheckpointStore.CheckDirectPath(file);
                    if (new FileInfo(file).Length > MaxRebaseBytes) continue;
                    var data = JToken.Parse(File.ReadAllText(file));
                    var changed = RebaseValues(data, old, to);
                    // Remembered conversations are identified by the hash of their path.
                    if (string.Equals(Path.GetFileName(file), "recent-sessions.json", StringComparison.OrdinalIgnoreCase) && data["entries"] is JArray rows)
                        foreach (var row in rows.OfType<JObject>())
                            if (row["path"]?.Type == JTokenType.String) row["id"] = ContentHash.Compute((string)row["path"]!);
                    if (changed) AtomicFile.WriteAllText(file, data.ToString(Formatting.None));
                }
                catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException || ex is JsonException || ex is InvalidOperationException)
                {
                    logger.Warn("Could not move " + file + " to the new workspace root: " + ex.Message);
                }
            }
        }

        /// <summary>Recorded file contents (checkpoints keep each file's text before and after); never paths, so never rewritten.</summary>
        private static readonly HashSet<string> ContentProperties = new HashSet<string>(StringComparer.Ordinal) { "before", "after" };

        /// <summary>
        /// Rewrites every string that is the old root or a path under it. File contents are left alone: a checkpointed file whose text
        /// starts with the old root must keep its text (and so its recorded hash); a path is one line.
        /// </summary>
        private static bool RebaseValues(JToken token, string old, string root)
        {
            var changed = false;
            var values = token is JContainer container ? container.DescendantsAndSelf().OfType<JValue>() : new[] { token }.OfType<JValue>();
            foreach (var value in values.Where(v => v.Type == JTokenType.String).ToList())
            {
                if (value.Parent is JProperty property && ContentProperties.Contains(property.Name)) continue;
                var text = (string)value!;
                if (text.Length < old.Length || !text.StartsWith(old, Comparison) || text.IndexOfAny(new[] { '\r', '\n' }) >= 0) continue;
                if (text.Length == old.Length || (text.Length == old.Length + 1 && (text[old.Length] == '\\' || text[old.Length] == '/'))) { value.Value = root; changed = true; }
                else if (text[old.Length] == '\\' || text[old.Length] == '/') { value.Value = root.TrimEnd('\\', '/') + text.Substring(old.Length); changed = true; }
            }
            return changed;
        }
    }
}
