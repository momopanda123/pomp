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
    /// another root, because the folder was moved, renamed or copied. The history comes along either way: root stamps and paths
    /// under the old root are rewritten to the new one, in POMP's stores and in the header of each omp session file (its <c>cwd</c>
    /// and paths), so every conversation lists and resumes in the new root. The header must be rewritten: omp adopts a session's
    /// recorded cwd whenever that folder exists, so a copied conversation would otherwise run in the original project. The old root
    /// is remembered (<see cref="IsCurrentOrPrevious"/>) for the checks that compare a session's cwd with the workspace. omp's
    /// isolation copies (<c>wt</c>) borrow the original repository's objects by absolute path and go to the Recycle Bin.
    /// </summary>
    internal static class WorkspaceOrigin
    {
        internal const string MarkerName = "workspace.json";
        private const int MaxPreviousRoots = 8;
        private const long MaxRebaseBytes = 16L * 1024 * 1024;

        private const string SessionsFolder = "sessions";
        /// <summary>The omp session header sits in the first lines of a session file (after its title line).</summary>
        private const int SessionHeaderLines = 8;

        // Stores that stamp the workspace root or hold absolute paths under it.
        private static readonly string[] RebasedFiles = { "recent-sessions.json", "reviews.json", "background-agents.json",
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
                var copied = Directory.Exists(ProjectData.LocalFolder(recorded)) && !SameFolder(local, ProjectData.LocalFolder(recorded));
                Rebase(local, recorded, root, logger);
                RebaseSessions(Path.Combine(local, SessionsFolder), recorded, root, logger);
                // omp's isolation copies borrow the repository's objects by absolute path; they are transient leftovers anyway.
                quarantine = Gather(local, recorded, name => string.Equals(name, IsolationFolder, StringComparison.OrdinalIgnoreCase));
                var roots = new List<string> { Normalize(recorded) };
                roots.AddRange(previous.Where(p => !roots.Any(r => SamePath(r, p)) && !SamePath(p, root)));
                WriteMarker(marker, root, roots.Take(MaxPreviousRoots));
                outcome = new Outcome(copied ? Change.Copied : Change.Moved, recorded, quarantine);
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

        /// <summary>
        /// Rewrites the old root in the session header (<c>{"type":"session",...}</c>: <c>cwd</c>, a parent session's path, extra
        /// directories) of every omp session file under <paramref name="sessions"/>, subagent sessions included. Only the header line
        /// changes; the rest of the file is copied byte for byte.
        /// </summary>
        private static void RebaseSessions(string sessions, string from, string to, IPompLogger logger)
        {
            if (!Directory.Exists(sessions)) return;
            var old = Normalize(from);
            foreach (var file in Directory.EnumerateFiles(sessions, "*.jsonl", SearchOption.AllDirectories))
            {
                try { RebaseSessionHeader(file, old, to); }
                catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException || ex is JsonException || ex is InvalidOperationException)
                {
                    logger.Warn("Could not move the conversation " + file + " to the new workspace root: " + ex.Message);
                }
            }
        }

        private static void RebaseSessionHeader(string file, string old, string root)
        {
            CheckpointStore.CheckDirectPath(file);
            string? replaced = null; var prefix = 0L; var headerBytes = 0;
            using (var input = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
            {
                var reader = new LineScanner(input);
                for (var index = 0; index < SessionHeaderLines && reader.Next(out var start, out var line, out var length); index++)
                {
                    if (line.IndexOf("\"session\"", StringComparison.Ordinal) < 0) continue;
                    var header = JObject.Parse(line);
                    if ((string?)header["type"] != "session") continue;
                    if (!RebaseValues(header, old, root)) return;
                    replaced = header.ToString(Formatting.None); prefix = start; headerBytes = length;
                    break;
                }
            }
            if (replaced == null) return;
            var temp = file + ".rebase";
            using (var input = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
            using (var output = new FileStream(temp, FileMode.Create, FileAccess.Write, FileShare.None))
            {
                CopyBytes(input, output, prefix);
                var bytes = new System.Text.UTF8Encoding(false).GetBytes(replaced);
                output.Write(bytes, 0, bytes.Length);
                input.Seek(prefix + headerBytes, SeekOrigin.Begin);
                input.CopyTo(output);
            }
            File.Copy(temp, file, overwrite: true);
            File.Delete(temp);
        }

        private static void CopyBytes(Stream input, Stream output, long count)
        {
            var buffer = new byte[81920];
            while (count > 0)
            {
                var read = input.Read(buffer, 0, (int)Math.Min(buffer.Length, count));
                if (read <= 0) throw new IOException("The file ended early.");
                output.Write(buffer, 0, read); count -= read;
            }
        }

        /// <summary>Reads UTF-8 lines with their byte offset and length (without the line break) from the start of a stream.</summary>
        private sealed class LineScanner
        {
            private const int MaxLineBytes = 1024 * 1024;
            private readonly Stream _stream;
            private long _position;
            public LineScanner(Stream stream) { _stream = stream; }
            public bool Next(out long start, out string line, out int length)
            {
                start = _position; line = string.Empty; length = 0;
                var bytes = new List<byte>();
                int value;
                while ((value = _stream.ReadByte()) >= 0)
                {
                    _position++;
                    if (value == '\n') break;
                    if (bytes.Count >= MaxLineBytes) throw new InvalidOperationException("A session header line is too long.");
                    bytes.Add((byte)value);
                }
                if (value < 0 && bytes.Count == 0) return false;
                length = bytes.Count;
                if (length > 0 && bytes[length - 1] == '\r') length--;
                line = System.Text.Encoding.UTF8.GetString(bytes.ToArray(), 0, length);
                return true;
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
