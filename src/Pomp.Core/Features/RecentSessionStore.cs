using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Pomp.Adapters;
using Pomp.Hosting;
using Pomp.Internal;

namespace Pomp.Features
{
    /// <summary>Remembered omp references plus a read-only view of the active workspace's session folder (no count limit; sizes bound the store and cache).</summary>
    public sealed class RecentSessionStore
    {
        private const int ReadBudget = 64 * 1024;
        /// <summary>The remembered references' file; past it the conversations visited longest ago are forgotten first.</summary>
        internal const int MaxStoreBytes = 1024 * 1024;
        /// <summary>The cached metadata of all workspaces together (characters, roughly); past it the entries cached first go first.</summary>
        private const long CacheBudget = 16L * 1024 * 1024;
        private static long cacheSize;
        private readonly string root, file;
        private static StringComparison Comparison => Path.DirectorySeparatorChar == '\\' ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        private static StringComparer PathComparer => Path.DirectorySeparatorChar == '\\' ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;
        private static readonly object CacheLock = new object();
        private static readonly object WriteGate = new object();
        private static readonly Dictionary<string, CachedSession> Cache = new Dictionary<string, CachedSession>(PathComparer);

        public RecentSessionStore(string dataDirectory, string workspaceRoot)
        {
            root = Path.GetFullPath(workspaceRoot);
            file = ProjectData.File(root, "recent-sessions.json", Path.Combine(dataDirectory, "recent-sessions", ContentHash.Compute(Path.DirectorySeparatorChar == '\\' ? root.ToLowerInvariant() : root) + ".json"));
        }

        /// <summary>Points the remembered conversations that were in <paramref name="fromFolder"/> at <paramref name="toFolder"/> (their files moved there).</summary>
        internal void Relocate(string fromFolder, string toFolder)
        {
            lock (WriteGate)
            {
                CheckpointStore.CheckDirectPath(file);
                if (!File.Exists(file)) return;
                CheckpointStore.CheckDirectPath(file + ".lock");
                using var lease = new FileStream(file + ".lock", FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
                var rows = Read(); var changed = false;
                var from = Path.GetFullPath(fromFolder).TrimEnd('\\', '/');
                foreach (var row in rows.OfType<JObject>())
                {
                    var path = (string)row["path"]!;
                    if (!string.Equals(Path.GetDirectoryName(path), from, Comparison)) continue;
                    var moved = Path.Combine(Path.GetFullPath(toFolder), Path.GetFileName(path));
                    row["path"] = moved; row["id"] = ContentHash.Compute(moved); changed = true;
                }
                if (changed) AtomicFile.WriteAllText(file, new JObject { ["version"] = 1, ["root"] = root, ["entries"] = rows }.ToString(Formatting.None));
            }
        }

        public bool Remember(string sessionId, string sessionPath, string name)
        {
            if (string.IsNullOrWhiteSpace(sessionId) || sessionId.Length > 256 || !Path.IsPathRooted(sessionPath) || sessionPath.Length > 4096 || sessionPath.Any(char.IsControl)) throw new ArgumentException("Invalid session reference.");
            var path = Path.GetFullPath(sessionPath);
            if (!string.Equals(Path.GetExtension(path), ".jsonl", StringComparison.OrdinalIgnoreCase)) throw new ArgumentException("Expected a JSONL session file.");
            CheckpointStore.CheckDirectPath(path);
            if (!File.Exists(path)) return false; // omp may not have persisted an empty session yet
            lock (WriteGate)
            {
                CheckpointStore.CheckDirectPath(file); Directory.CreateDirectory(Path.GetDirectoryName(file)!);
                CheckpointStore.CheckDirectPath(file + ".lock");
                using var lease = new FileStream(file + ".lock", FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
                var rows = Read();
                foreach (var prior in rows.OfType<JObject>().Where(r => string.Equals((string?)r["path"], path, Comparison)).ToArray()) prior.Remove();
                rows.Insert(0, new JObject { ["id"] = ContentHash.Compute(path), ["sessionId"] = sessionId, ["path"] = path, ["name"] = name.Length > 160 ? name.Substring(0,160) : name, ["visitedUtc"] = DateTimeOffset.UtcNow.ToString("O") });
                var size = Encoding.UTF8.GetByteCount(new JObject { ["version"] = 1, ["root"] = root, ["entries"] = rows }.ToString(Formatting.None));
                while (size > MaxStoreBytes && rows.Count > 1) { size -= Encoding.UTF8.GetByteCount(rows.Last!.ToString(Formatting.None)) + 1; rows.RemoveAt(rows.Count - 1); }
                AtomicFile.WriteAllText(file, new JObject { ["version"] = 1, ["root"] = root, ["entries"] = rows }.ToString(Formatting.None));
                return true;
            }
        }

        public JObject List(string? currentSessionFile = null, string? currentSessionId = null, string? currentName = null)
        {
            var remembered = Read().OfType<JObject>().ToArray();
            var paths = new Dictionary<string, JObject?>(PathComparer);
            foreach (var row in remembered) paths[(string)row["path"]!] = row;
            foreach (var path in FolderFiles(currentSessionFile)) if (!paths.ContainsKey(path)) paths[path] = null;
            var entries = new List<JObject>();
            foreach (var pair in paths)
            {
                var metadata = ReadSession(pair.Key);
                if (metadata == null) continue;
                var row = pair.Value;
                var entry = (JObject)metadata.DeepClone();
                entry["id"] = row?["id"]?.DeepClone() ?? new JValue(ContentHash.Compute(pair.Key));
                if (row?["visitedUtc"]?.Type == JTokenType.String) entry["visitedUtc"] = row["visitedUtc"]!.DeepClone();
                var isCurrent = !string.IsNullOrEmpty(currentSessionFile) && string.Equals(pair.Key, currentSessionFile, Comparison);
                if (isCurrent || (!string.IsNullOrEmpty(currentSessionId) && (string?)entry["sessionId"] == currentSessionId))
                {
                    entry["current"] = true;
                    if (!string.IsNullOrEmpty(currentSessionId)) entry["sessionId"] = currentSessionId;
                    SetTitle(entry, currentName ?? (string?)entry["name"]);
                }
                else if (entry["name"] == null && !string.IsNullOrWhiteSpace((string?)row?["name"])) SetTitle(entry, (string?)row?["name"]);
                entry.Remove("firstMessage");
                entries.Add(entry);
            }
            return new JObject { ["root"] = root, ["entries"] = new JArray(entries.OrderByDescending(e => (string?)e["modifiedUtc"], StringComparer.Ordinal)
                .ThenByDescending(e => (string?)e["visitedUtc"], StringComparer.Ordinal).ThenBy(e => (string?)e["id"], StringComparer.Ordinal)) };
        }

        public string Resolve(string expectedRoot, string id, string? currentSessionFile = null)
        {
            if (!string.Equals(expectedRoot, root, Comparison)) throw new InvalidOperationException("Workspace changed.");
            // Handles are hashes issued by this host, never a path (nor an encoded path) supplied by the page.
            if (string.IsNullOrEmpty(id) || id.Length > 128 || id.Any(c => !((c >= 'a' && c <= 'f') || (c >= '0' && c <= '9'))))
                throw new InvalidOperationException("Invalid session handle.");
            var row = Read().FirstOrDefault(r => (string?)r["id"] == id);
            var path = row == null ? FolderFiles(currentSessionFile).FirstOrDefault(candidate => ContentHash.Compute(candidate) == id) : (string?)row["path"];
            if (path == null) throw new InvalidOperationException("Session is not in this workspace's session list.");
            CheckpointStore.CheckDirectPath(path);
            if (!File.Exists(path)) throw new FileNotFoundException("The recorded session file is unavailable.");
            return path;
        }

        internal SessionDeletionTarget ResolveForDeletion(string id, string? currentSessionFile, string? currentSessionId, string? currentName)
        {
            if (string.IsNullOrEmpty(currentSessionFile)) throw new InvalidOperationException(PompLanguage.Pick("대화 폴더를 확인할 수 없어요.", "The conversation folder is unavailable."));
            var listed = (List(currentSessionFile, currentSessionId, currentName)["entries"] as JArray)?.OfType<JObject>().FirstOrDefault(entry => (string?)entry["id"] == id);
            if (listed == null) throw new InvalidOperationException(PompLanguage.Pick("이 작업 영역의 대화 목록에 없어요.", "The conversation is not listed in this workspace."));
            var path = Resolve(root, id, currentSessionFile);
            var folder = Path.GetDirectoryName(Path.GetFullPath(currentSessionFile))!;
            if (!string.Equals(Path.GetDirectoryName(path), folder, Comparison)) throw new InvalidOperationException(PompLanguage.Pick("대화 파일이 현재 대화 폴더 밖에 있어요.", "The conversation file is outside the active conversation folder."));
            CheckpointStore.CheckDirectPath(path);
            // Read the real header again; a cached title or remembered reference is not deletion authority. The engine keeps the
            // active conversation's file open for appending: read it shared, as Preview does, or the current conversation cannot be deleted.
            using (var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
            using (var reader = new StreamReader(stream, Encoding.UTF8, true))
            {
                string? line; JObject? header = null;
                for (var count = 0; count < 3 && (line = reader.ReadLine()) != null; count++)
                {
                    if (line.Length > ReadBudget) throw new InvalidDataException();
                    var row = JObject.Parse(line.TrimStart('\uFEFF'));
                    if (row.GetString("type") == "session") { header = row; break; }
                    if (row.GetString("type") != "title") throw new InvalidDataException();
                }
                if (header == null || header.GetString("id") != (string?)listed["sessionId"] ||
                    (header.GetString("cwd") is string cwd && !WorkspaceOrigin.IsCurrentOrPrevious(root, cwd)))
                    throw new InvalidOperationException(PompLanguage.Pick("대화가 현재 작업 영역과 일치하지 않아요.", "The conversation does not match the current workspace."));
            }
            var artifacts = path.Substring(0, path.Length - ".jsonl".Length);
            var target = new SessionDeletionTarget(path, (string)listed["sessionId"]!, artifacts);
            target.Validate();
            return target;
        }

        internal void Forget(string sessionId, string path)
        {
            lock (WriteGate)
            {
                CheckpointStore.CheckDirectPath(file); Directory.CreateDirectory(Path.GetDirectoryName(file)!);
                CheckpointStore.CheckDirectPath(file + ".lock");
                using var lease = new FileStream(file + ".lock", FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
                var rows = Read();
                foreach (var row in rows.OfType<JObject>().Where(r => (string?)r["sessionId"] == sessionId || string.Equals((string?)r["path"], path, Comparison)).ToArray()) row.Remove();
                AtomicFile.WriteAllText(file, new JObject { ["version"] = 1, ["root"] = root, ["entries"] = rows }.ToString(Formatting.None));
                lock (CacheLock) if (Cache.TryGetValue(path, out var cached)) { cacheSize -= cached.Size; Cache.Remove(path); }
            }
        }

        public JObject Preview(string id, string? currentSessionFile = null)
        {
            var path = Resolve(root, id, currentSessionFile);
            CheckpointStore.CheckDirectPath(path);
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            var length = stream.Length;
            var bytes = new byte[(int)Math.Min(length, 1024 * 1024)]; var read = 0;
            while (read < bytes.Length) { var size = stream.Read(bytes, read, bytes.Length - read); if (size == 0) break; read += size; }
            var truncated = read < length;
            var text = Encoding.UTF8.GetString(bytes, 0, read).TrimStart('﻿');
            if (truncated) { var end = text.LastIndexOf('\n'); text = end < 0 ? "" : text.Substring(0, end + 1); }
            var messages = new JArray();
            using var lines = new StringReader(text);
            string? line;
            while ((line = lines.ReadLine()) != null)
            {
                if (string.IsNullOrWhiteSpace(line)) continue;
                try
                {
                    using var json = new JsonTextReader(new StringReader(line)) { MaxDepth = 32, DateParseHandling = DateParseHandling.None };
                    var entry = JObject.Load(json);
                    if (json.Read() || entry.GetString("type") != "message" || entry["message"] is not JObject message) continue;
                    var role = message.GetString("role");
                    if (role != "user" && role != "assistant") continue;
                    var content = message["content"];
                    var value = content?.Type == JTokenType.String ? (string?)content : string.Join("\n", (content as JArray ?? new JArray()).OfType<JObject>()
                        .Select(block => block.AnswerText()).Where(text => text != null));
                    if (role == "user") value = PromptDisplay.UserText(value);
                    if (string.IsNullOrEmpty(value)) continue;
                    if (messages.Count >= 20) { truncated = true; break; }
                    var shown = new JObject { ["role"] = role, ["text"] = value!.Substring(0, Math.Min(2000, value.Length)) };
                    if (value.Length > 2000) { shown["truncated"] = true; truncated = true; }
                    messages.Add(shown);
                }
                catch (JsonException) { } // A damaged line must not hide the readable messages around it.
            }
            return new JObject { ["id"] = id, ["messages"] = messages, ["truncated"] = truncated };
        }

        private static IReadOnlyList<string> FolderFiles(string? currentSessionFile)
        {
            var newest = new List<FileInfo>();
            try
            {
                if (string.IsNullOrEmpty(currentSessionFile) || currentSessionFile!.Length > 4096 || currentSessionFile.Any(char.IsControl) || !Path.IsPathRooted(currentSessionFile)) return Array.Empty<string>();
                var folder = Path.GetDirectoryName(Path.GetFullPath(currentSessionFile));
                if (string.IsNullOrEmpty(folder)) return Array.Empty<string>();
                CheckpointStore.CheckDirectPath(folder!);
                if (!Directory.Exists(folder)) return Array.Empty<string>();
                foreach (var path in Directory.EnumerateFiles(folder, "*", SearchOption.TopDirectoryOnly))
                {
                    try
                    {
                        if (!string.Equals(Path.GetExtension(path), ".jsonl", StringComparison.OrdinalIgnoreCase)) continue;
                        // No directory descent and no file links, including dangling ones.
                        CheckpointStore.CheckDirectPath(path);
                        var info = new FileInfo(path);
                        if ((info.Attributes & (FileAttributes.ReparsePoint | FileAttributes.Directory)) != 0) continue;
                        newest.Add(info);
                    }
                    catch (Exception ex) when (IsFileFailure(ex)) { }
                }
            }
            catch (Exception ex) when (IsFileFailure(ex)) { }
            newest.Sort((a, b) => { var time = b.LastWriteTimeUtc.CompareTo(a.LastWriteTimeUtc); return time != 0 ? time : PathComparer.Compare(a.FullName, b.FullName); });
            return newest.Select(info => info.FullName).ToArray();
        }

        private static JObject? ReadSession(string path)
        {
            try
            {
                CheckpointStore.CheckDirectPath(path);
                var info = new FileInfo(path);
                var length = info.Length; var modified = info.LastWriteTimeUtc;
                lock (CacheLock)
                    if (Cache.TryGetValue(path, out var cached) && cached.Length == length && cached.Modified == modified) return cached.Metadata;
                JObject? metadata = null;
                try
                {
                    using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                    var bytes = new byte[(int)Math.Min(length, ReadBudget)]; var read = 0;
                    while (read < bytes.Length) { var count = stream.Read(bytes, read, bytes.Length - read); if (count == 0) break; read += count; }
                    var complete = read == length;
                    var text = Encoding.UTF8.GetString(bytes, 0, read).TrimStart('﻿');
                    if (!complete) { var end = text.LastIndexOf('\n'); text = end < 0 ? "" : text.Substring(0, end + 1); }
                    metadata = ParseSession(text, complete, modified);
                }
                catch (JsonException) { }
                lock (CacheLock)
                {
                    if (Cache.TryGetValue(path, out var replaced)) { cacheSize -= replaced.Size; Cache.Remove(path); }
                    var added = new CachedSession(length, modified, metadata, path);
                    while (Cache.Count > 0 && cacheSize + added.Size > CacheBudget) { var first = Cache.First(); cacheSize -= first.Value.Size; Cache.Remove(first.Key); }
                    Cache[path] = added; cacheSize += added.Size;
                }
                return metadata;
            }
            catch (Exception ex) when (IsFileFailure(ex)) { return null; }
        }

        /// <remarks>
        /// omp 18 session files start with a fixed-width title line rewritten in place (<c>{"type":"title","v":1,"title":…}</c>),
        /// then the <c>{"type":"session",…}</c> header; renames also append <c>{"type":"title_change","title":…}</c>.
        /// </remarks>
        private static JObject? ParseSession(string text, bool complete, DateTime modified)
        {
            JObject? result = null; var count = 0; string? name = null; var titleLineSeen = false;
            using var lines = new StringReader(text);
            string? line;
            while ((line = lines.ReadLine()) != null)
            {
                if (string.IsNullOrWhiteSpace(line)) continue;
                using var json = new JsonTextReader(new StringReader(line)) { MaxDepth = 32, DateParseHandling = DateParseHandling.None };
                var entry = JObject.Load(json);
                if (json.Read()) throw new JsonReaderException("Unexpected trailing session data.");
                var type = entry.GetString("type");
                if (result == null)
                {
                    if (type == "title" && !titleLineSeen)
                    {
                        titleLineSeen = true;
                        name = entry.GetString("title");
                        continue;
                    }

                    var id = entry.GetString("id");
                    if (type != "session" || string.IsNullOrWhiteSpace(id) || id!.Length > 256 || id.Any(char.IsControl)) return null;
                    result = new JObject { ["sessionId"] = id, ["modifiedUtc"] = modified.ToString("O", CultureInfo.InvariantCulture) };
                    if (!titleLineSeen) name = entry.GetString("name");
                }
                else if (type == "title_change" || type == "session_info") name = entry.GetString(type == "title_change" ? "title" : "name") ?? name;
                else if (type == "message" && entry["message"] is JObject message)
                {
                    count++;
                    if (result["firstMessage"] == null && message.GetString("role") == "user")
                    {
                        var content = message["content"];
                        var first = content?.Type == JTokenType.String ? (string?)content : string.Join(" ", (content as JArray ?? new JArray()).OfType<JObject>()
                            .Where(block => block.GetString("type") == "text").Select(block => block.GetString("text") ?? ""));
                        var title = SingleLine(PromptDisplay.UserText(first), 120);
                        if (title.Length > 0) result["firstMessage"] = title;
                    }
                }
            }
            if (result == null) return null;
            if (complete) result["messageCount"] = count;
            SetTitle(result, name);
            return result;
        }

        private static void SetTitle(JObject entry, string? name)
        {
            var clean = SingleLine(name, 120);
            if (clean.Length > 0) entry["name"] = clean; else entry.Remove("name");
            // An untitled session without a question yet reads like the chat header does; its date is shown next to it anyway.
            entry["title"] = clean.Length > 0 ? clean : (string?)entry["firstMessage"] ?? SetupText.Get("새 대화", "New conversation");
        }

        private static string SingleLine(string? value, int limit)
        {
            if (string.IsNullOrEmpty(value)) return "";
            var result = new StringBuilder(limit); var space = false;
            foreach (var c in value!)
            {
                if (char.IsControl(c) || char.IsWhiteSpace(c)) { space = result.Length > 0; continue; }
                if (space && result.Length < limit) result.Append(' ');
                space = false;
                if (result.Length >= limit) break;
                result.Append(c);
            }
            return result.ToString().TrimEnd();
        }

        private static bool IsFileFailure(Exception ex) => ex is IOException || ex is UnauthorizedAccessException || ex is ArgumentException || ex is NotSupportedException || ex is System.Security.SecurityException;
        private sealed class CachedSession
        {
            public CachedSession(long length, DateTime modified, JObject? metadata, string path) { Length = length; Modified = modified; Metadata = metadata; Size = path.Length + 64 + (metadata?.ToString(Formatting.None).Length ?? 0); }
            public long Size { get; }
            public long Length { get; }
            public DateTime Modified { get; }
            public JObject? Metadata { get; }
        }

        private JArray Read()
        {
            CheckpointStore.CheckDirectPath(file);
            if (!File.Exists(file)) return new JArray();
            using var stream = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            if (stream.Length > MaxStoreBytes) throw new InvalidOperationException("The session-reference store is too large.");
            using var text = new StreamReader(stream, Encoding.UTF8, true);
            using var reader = new JsonTextReader(text) { MaxDepth = 6, DateParseHandling = DateParseHandling.None };
            var data = JObject.Load(reader);
            if ((int?)data["version"] != 1 || !string.Equals((string?)data["root"], root, Comparison) || !(data["entries"] is JArray rows)) throw new InvalidOperationException("Malformed session-reference store.");
            foreach (var row in rows)
            {
                var path = (string?)row["path"]; var id = (string?)row["id"];
                if (path == null || path.Length > 4096 || path.Any(char.IsControl) || !Path.IsPathRooted(path) || id != ContentHash.Compute(path) || !string.Equals(Path.GetExtension(path), ".jsonl", StringComparison.OrdinalIgnoreCase) || row["sessionId"]?.Type != JTokenType.String || ((string)row["sessionId"]!).Length > 256 || row["name"]?.Type != JTokenType.String || ((string)row["name"]!).Length > 160) throw new InvalidOperationException("Malformed session reference.");
            }
            return rows;
        }
    }
}
