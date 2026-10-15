using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Pomp.Adapters;
using Pomp.Internal;
using Pomp.Hosting;

namespace Pomp.Features
{
    /// <summary>One page of the history list: rows newest first, how many records matched, and the page and size actually served.</summary>
    public sealed class CheckpointPage
    {
        public CheckpointPage(JArray entries, int total, int page, int size) { Entries = entries; Total = total; Page = page; Size = size; }
        public JArray Entries { get; }
        public int Total { get; }
        public int Page { get; }
        public int Size { get; }
    }

    /// <summary>What a multi-record delete did: how many records are gone, and the first reason any other was kept.</summary>
    public sealed class CheckpointDeletion
    {
        public int Deleted { get; internal set; }
        public string? Error { get; internal set; }
    }

    /// <summary>
    /// Per-workspace source history in the workspace's <c>.pomp/local/checkpoints</c> (not committed), bounded by size only (oldest records go first). Pending records never imply successful application.
    /// Listing never parses whole records to order or count them: records are ordered by their file time, and <c>index.json</c> keeps each record's id, time, state,
    /// file paths and error next to the length and timestamp of the record file it was made from. The index is only a cache: a record that is new or changed
    /// is read once, when a page shows it or a path search needs it.
    /// </summary>
    public sealed class CheckpointStore
    {
        public const int DefaultPageSize = 20;
        public const int MaximumPageSize = 100;
        public const int MaximumQueryLength = 200;
        /// <summary>A prepared record untouched for this long belongs to an apply that died with its process: it can be deleted.</summary>
        public static readonly TimeSpan AbandonedAfter = TimeSpan.FromHours(1);
        private const long MaximumBytes = 500L * 1024 * 1024;
        private const long MaximumRecordBytes = 64L * 1024 * 1024;
        private const long MaximumIndexBytes = 128L * 1024 * 1024;
        private const string IndexName = "index.json", UnreadableState = "unreadable";
        private readonly string directory;
        private readonly string root;

        /// <summary>The cheap view of one record. Length and Stamp belong to the record file as indexed: when either differs the entry is stale.</summary>
        private sealed class Meta
        {
            [JsonProperty("id")] public string Id = "";
            [JsonProperty("length")] public long Length;
            [JsonProperty("stamp")] public long Stamp;
            [JsonProperty("created")] public string? Created;
            [JsonProperty("state")] public string State = "";
            [JsonProperty("error")] public string? Error;
            [JsonProperty("files")] public string[] Files = Array.Empty<string>();
            /// <summary>False for a verdict that may be transient (the record could not be opened): not stored, read again next time.</summary>
            [JsonIgnore] public bool Persist = true;
        }

        private sealed class IndexFile
        {
            [JsonProperty("version")] public int Version;
            [JsonProperty("entries")] public List<Meta>? Entries;
        }

        /// <summary>One record file on disk and what the index knows of it (null until it matches or the record was read).</summary>
        private sealed class Slot
        {
            public string Id = "";
            public FileInfo Info = null!;
            public long Stamp;
            public Meta? Meta;
            public bool Indexed;
        }

        public CheckpointStore(string dataDirectory, string workspaceRoot)
        {
            root = Path.GetFullPath(workspaceRoot);
            var key = Path.DirectorySeparatorChar == '\\' ? root.ToLowerInvariant() : root;
            directory = ProjectData.Folder(root, "checkpoints", Path.Combine(dataDirectory, "checkpoints", ContentHash.Compute(key)));
        }
        public string Prepare(ShadowSnapshot snapshot)
        {
            var id = Guid.NewGuid().ToString("N");
            var record = new JObject { ["version"] = 1, ["id"] = id, ["root"] = root,
                ["createdUtc"] = DateTimeOffset.UtcNow.ToString("O"), ["state"] = "prepared",
                ["files"] = new JArray(snapshot.Files.Select(f => new JObject {
                    ["path"] = f.Path, ["before"] = f.Before, ["after"] = f.After,
                    ["beforeHash"] = f.BeforeHash, ["afterHash"] = f.AfterHash })) };
            using (Acquire()) { Write(id, record); Prune(id); }
            return id;
        }
        public void Complete(string id, WorkspaceApplyResult result)
        {
            using (Acquire())
            {
                var record = Read(id);
                record["state"] = result.Succeeded ? "applied" : result.HasPartialChanges ? "partial" : "failed";
                record["error"] = result.Error;
                record["outcomes"] = new JArray(result.Files.Select(f => new JObject { ["path"] = f.Path, ["state"] = f.State.ToString(), ["error"] = f.Error }));
                Write(id, record); Prune(id);
            }
        }
        /// <summary>The newest <see cref="DefaultPageSize"/> records.</summary>
        public JArray List() => ListPage(0, DefaultPageSize, null).Entries;
        /// <summary>
        /// One page of the records, newest first (by the time of the record file). A <paramref name="query"/> keeps the records with a file path containing it
        /// (case-insensitive, either slash). The page is clamped to the last one (0 when nothing matches), the size to 1..<see cref="MaximumPageSize"/>.
        /// Without a query only the records of the page are read (those the index does not know yet); a query reads every record the index lacks, a few at a time.
        /// </summary>
        public CheckpointPage ListPage(int page, int size, string? query)
        {
            size = ClampSize(size);
            var needle = NormalizeQuery(query).Replace('\\', '/');
            if (!Directory.Exists(directory)) return new CheckpointPage(new JArray(), 0, 0, size);
            using (Acquire())
            {
                var known = LoadIndex();
                var slots = Slots(known); var fresh = 0;
                List<Slot> hits;
                if (needle.Length == 0) hits = slots;
                else
                {
                    fresh += Fill(slots);
                    hits = slots.Where(s => s.Meta!.Files.Any(p => p.Replace('\\', '/').IndexOf(needle, StringComparison.OrdinalIgnoreCase) >= 0)).ToList();
                }
                var last = hits.Count == 0 ? 0 : (hits.Count - 1) / size;
                page = page < 0 ? 0 : page > last ? last : page;
                var window = hits.Skip(page * size).Take(size).ToList();
                fresh += Fill(window);
                // Entries of records that are gone or changed (and not read again just now) leave the file.
                if (fresh > 0 || known.Count > slots.Count(s => s.Indexed)) SaveIndex(slots.Where(s => s.Meta != null && s.Meta.Persist).Select(s => s.Meta!));
                return new CheckpointPage(new JArray(window.Select(s => Row(s.Meta!))), hits.Count, page, size);
            }
        }
        public static int ClampSize(int size) => size < 1 ? 1 : size > MaximumPageSize ? MaximumPageSize : size;
        /// <summary>The query as searched: trimmed and cut to <see cref="MaximumQueryLength"/> characters.</summary>
        public static string NormalizeQuery(string? query)
        {
            var text = (query ?? string.Empty).Trim();
            if (text.Length > MaximumQueryLength)
            {
                text = text.Substring(0, MaximumQueryLength);
                if (char.IsHighSurrogate(text[text.Length - 1])) text = text.Substring(0, text.Length - 1);
                text = text.TrimEnd();
            }
            return text;
        }
        /// <summary>
        /// The files of several applied records for one combined restore, in the order given. Refused with a message as soon as two records
        /// changed the same file or the files together exceed <paramref name="maximumFiles"/>, before the next record is read.
        /// </summary>
        public IReadOnlyList<ShadowFile> ReadApplied(IEnumerable<string> ids, int maximumFiles)
        {
            var files = new List<ShadowFile>(); var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase); var done = new HashSet<string>();
            using (Acquire())
            {
                foreach (var raw in ids)
                {
                    var id = Canonical(raw);
                    if (!done.Add(id)) continue;
                    var record = Read(id);
                    if ((string?)record["state"] != "applied") throw new InvalidOperationException(PompLanguage.Pick("체크포인트가 성공적으로 적용되지 않았어요.", "The checkpoint was not applied successfully."));
                    foreach (var f in (JArray)record["files"]!)
                    {
                        var file = new ShadowFile((string)f["path"]!, (string)f["before"]!, (string)f["after"]!);
                        if (!seen.Add(file.Path.Replace('\\', '/'))) throw new InvalidOperationException(PompLanguage.Pick("같은 파일을 바꾼 기록은 함께 되돌릴 수 없어요.", "Records that changed the same file cannot be restored together."));
                        if (files.Count >= maximumFiles) throw new InvalidOperationException(PompLanguage.Pick("한 번에 되돌릴 파일은 " + maximumFiles + "개까지예요.", "Up to " + maximumFiles + " files can be restored at once."));
                        files.Add(file);
                    }
                }
            }
            return files;
        }
        /// <summary>
        /// Removes one finished checkpoint record. A prepared record is refused while an apply may be in flight; one whose file was last written more
        /// than <see cref="AbandonedAfter"/> ago was abandoned (an apply never runs that long) and goes like any other. A record that cannot be
        /// read (damaged, or failing its integrity check) can always go: it can never be restored anyway.
        /// </summary>
        public void Delete(string id)
        {
            id = Canonical(id);
            using (Acquire()) DeleteLocked(id, null);
        }
        /// <summary>
        /// <see cref="Delete(string)"/> for many records at once, each on its own merits: the others still go when one is refused or gone already.
        /// The result counts the removed records and carries the first refusal.
        /// </summary>
        public CheckpointDeletion DeleteMany(IEnumerable<string> ids)
        {
            var result = new CheckpointDeletion(); var done = new HashSet<string>();
            using (Acquire())
            {
                var known = LoadIndex(); // The states of indexed records: a delete reads no record that was listed before.
                foreach (var raw in ids)
                {
                    try
                    {
                        var id = Canonical(raw);
                        if (!done.Add(id)) continue;
                        DeleteLocked(id, known); result.Deleted++;
                    }
                    catch (Exception ex) when (ex is ArgumentException || ex is IOException || ex is InvalidOperationException || ex is UnauthorizedAccessException)
                    { if (result.Error == null) result.Error = ex.Message; }
                }
            }
            return result;
        }
        /// <summary>Deletes one record under the lock. The folder was checked when the lock was taken, so only the file itself is (a walk up the path per record would dominate a big delete).</summary>
        private void DeleteLocked(string id, Dictionary<string, Meta>? known)
        {
            var info = new FileInfo(FileName(id));
            if (!info.Exists) throw new FileNotFoundException(PompLanguage.Pick("체크포인트가 더 이상 없어요.", "The checkpoint no longer exists."));
            if ((info.Attributes & FileAttributes.ReparsePoint) != 0) throw new IOException("The path traverses a symbolic link or junction.");
            var meta = known != null && known.TryGetValue(id, out var indexed) && Current(indexed, info) ? indexed : Describe(id, info);
            if (meta.State == "prepared" && info.LastWriteTimeUtc > DateTime.UtcNow - AbandonedAfter)
                throw new InvalidOperationException(PompLanguage.Pick("아직 적용 중인 체크포인트라 삭제할 수 없어요.", "This checkpoint may still be applying, so it cannot be deleted."));
            info.Delete();
        }
        private static string Canonical(string id)
        {
            if (!Guid.TryParseExact(id, "N", out var guid)) throw new ArgumentException(PompLanguage.Pick("체크포인트 ID가 올바르지 않아요.", "Invalid checkpoint ID."));
            return guid.ToString("N");
        }
        private string FileName(string id) => Path.Combine(directory, Canonical(id) + ".json");
        /// <summary>Reads and verifies one record. <paramref name="folderChecked"/>: the caller holds the lock and has the record's file entry from the folder listing, so the walk up the path is skipped.</summary>
        private JObject Read(string id, bool folderChecked = false)
        {
            var file = FileName(id); if (!folderChecked) CheckDirectPath(file);
            using var stream = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.Read);
            if (stream.Length > MaximumRecordBytes) throw new InvalidOperationException(PompLanguage.Pick("체크포인트가 너무 커요.", "Checkpoint is too large."));
            using var text = new StreamReader(stream, Encoding.UTF8, true);
            using var reader = new JsonTextReader(text) { MaxDepth = 12, DateParseHandling = DateParseHandling.None };
            var record = JObject.Load(reader);
            if ((int?)record["version"] != 1 || (string?)record["id"] != id || !string.Equals((string?)record["root"], root, Path.DirectorySeparatorChar == '\\' ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal) || !(record["files"] is JArray files) || files.Count == 0 || files.Count > 100)
                throw new InvalidOperationException(PompLanguage.Pick("체크포인트 ID나 스키마가 일치하지 않아요.", "Checkpoint identity or schema mismatch."));
            foreach (var f in files)
            {
                if (f["path"]?.Type != JTokenType.String || f["before"]?.Type != JTokenType.String || f["after"]?.Type != JTokenType.String ||
                    ((string)f["before"]!).Length > 1000000 || ((string)f["after"]!).Length > 1000000 ||
                    ContentHash.Compute((string)f["before"]!) != (string?)f["beforeHash"] || ContentHash.Compute((string)f["after"]!) != (string?)f["afterHash"])
                    throw new InvalidOperationException(PompLanguage.Pick("체크포인트 소스 무결성 검사에 실패했어요.", "Checkpoint source integrity check failed."));
            }
            return record;
        }
        private void Write(string id, JObject record)
        {
            var text = record.ToString(Formatting.None);
            if (Encoding.UTF8.GetByteCount(text) > MaximumRecordBytes) throw new InvalidOperationException(PompLanguage.Pick("체크포인트가 너무 커요.", "Checkpoint is too large."));
            var target = FileName(id); CheckDirectPath(target); AtomicFile.WriteAllText(target, text);
        }
        private IEnumerable<FileInfo> Records() => new DirectoryInfo(directory).EnumerateFiles("*.json").Where(f => Guid.TryParseExact(Path.GetFileNameWithoutExtension(f.Name), "N", out _) && (f.Attributes & FileAttributes.ReparsePoint) == 0);
        /// <summary>Keeps the newest records within <see cref="MaximumBytes"/>; the record just written (<paramref name="retain"/>) always counts first, and everything older than the first record that no longer fits goes.</summary>
        private void Prune(string retain)
        {
            long bytes = 0; var full = false;
            foreach (var file in Records().OrderByDescending(f => Path.GetFileNameWithoutExtension(f.Name) == retain).ThenByDescending(f => f.LastWriteTimeUtc).ToArray())
            {
                if (!full && bytes + file.Length <= MaximumBytes) { bytes += file.Length; continue; }
                full = true; File.Delete(file.FullName);
            }
        }

        // ---- the index: what a listing reads instead of the records ----
        private static bool Current(Meta meta, FileInfo file) => meta.Length == file.Length && meta.Stamp == file.LastWriteTimeUtc.Ticks;
        /// <summary>Every record file, newest first by its own timestamp (no record is read to order or count them), each with its index entry while that still matches the file.</summary>
        private List<Slot> Slots(Dictionary<string, Meta> known)
        {
            var slots = new List<Slot>();
            foreach (var file in Records())
            {
                var id = Path.GetFileNameWithoutExtension(file.Name);
                var indexed = known.TryGetValue(id, out var meta) && Current(meta, file);
                slots.Add(new Slot { Id = id, Info = file, Stamp = file.LastWriteTimeUtc.Ticks, Meta = indexed ? meta : null, Indexed = indexed });
            }
            slots.Sort((a, b) => { var by = b.Stamp.CompareTo(a.Stamp); return by != 0 ? by : string.CompareOrdinal(b.Id, a.Id); });
            return slots;
        }
        /// <summary>
        /// Reads the records of <paramref name="slots"/> that have no current index entry (a few at a time when there are many) and gives each its metadata:
        /// a record that is damaged or fails its integrity check is "unreadable" for good, one that cannot be opened is "unreadable" for now.
        /// Returns how many entries are new for the index.
        /// </summary>
        private int Fill(IReadOnlyCollection<Slot> slots)
        {
            var todo = slots.Where(s => s.Meta == null).ToList();
            void One(Slot slot)
            {
                try { slot.Meta = Describe(slot.Id, slot.Info); }
                catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException || ex is OutOfMemoryException) { slot.Meta = Unreadable(slot.Id, slot.Info, false); }
            }
            if (todo.Count <= 16) foreach (var slot in todo) One(slot);
            else Parallel.ForEach(todo, new ParallelOptions { MaxDegreeOfParallelism = Math.Min(Environment.ProcessorCount, 4) }, One);
            return todo.Count(s => s.Meta!.Persist);
        }
        /// <summary>Reads one record for its metadata. Damage, a wrong identity or a failed integrity check make it "unreadable" for good; a read that fails (IO) throws, because it may work next time.</summary>
        private Meta Describe(string id, FileInfo file)
        {
            long length = file.Length, stamp = file.LastWriteTimeUtc.Ticks; // Taken before the read: a record replaced meanwhile no longer matches its entry.
            try
            {
                var record = Read(id, true);
                return new Meta { Id = id, Length = length, Stamp = stamp, Created = (string?)record["createdUtc"], State = (string?)record["state"] ?? "", Error = (string?)record["error"],
                    Files = ((JArray)record["files"]!).Select(f => (string)f["path"]!).ToArray() };
            }
            catch (Exception ex) when (!(ex is IOException || ex is UnauthorizedAccessException || ex is OutOfMemoryException)) { return Unreadable(id, file, true); }
        }
        private static Meta Unreadable(string id, FileInfo file, bool persist) =>
            new Meta { Id = id, Length = file.Length, Stamp = file.LastWriteTimeUtc.Ticks, State = UnreadableState, Persist = persist };
        private static JObject Row(Meta m) => m.State == UnreadableState
            ? new JObject { ["id"] = m.Id, ["state"] = UnreadableState, ["files"] = new JArray(), ["error"] = PompLanguage.Pick("체크포인트를 사용할 수 없거나 형식이 올바르지 않아요.", "Checkpoint is unavailable or malformed.") }
            : new JObject { ["id"] = m.Id, ["createdUtc"] = m.Created, ["state"] = m.State, ["files"] = new JArray(m.Files), ["error"] = m.Error };
        /// <summary>The stored entries by id. A missing, unreadable or foreign index is an empty one: every record is then read again when needed.</summary>
        private Dictionary<string, Meta> LoadIndex()
        {
            var known = new Dictionary<string, Meta>(StringComparer.Ordinal);
            try
            {
                var file = Path.Combine(directory, IndexName);
                using var stream = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                if (stream.Length > MaximumIndexBytes || (File.GetAttributes(file) & FileAttributes.ReparsePoint) != 0) return known;
                using var text = new StreamReader(stream, Encoding.UTF8, true);
                using var reader = new JsonTextReader(text) { MaxDepth = 8, DateParseHandling = DateParseHandling.None };
                var document = new JsonSerializer().Deserialize<IndexFile>(reader);
                if (document?.Version == 1 && document.Entries != null)
                    foreach (var entry in document.Entries)
                    {
                        if (entry == null || entry.State == null || entry.Files == null || Array.IndexOf(entry.Files, null!) >= 0 || !Guid.TryParseExact(entry.Id, "N", out _)) continue;
                        known[entry.Id] = entry;
                    }
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException || ex is JsonException || ex is InvalidCastException) { known.Clear(); }
            return known;
        }
        private void SaveIndex(IEnumerable<Meta> entries)
        {
            try
            {
                var file = Path.Combine(directory, IndexName);
                if (File.Exists(file) && (File.GetAttributes(file) & FileAttributes.ReparsePoint) != 0) return; // Never written through a link.
                AtomicFile.WriteAllText(file, JsonConvert.SerializeObject(new IndexFile { Version = 1, Entries = entries.ToList() }, Formatting.None));
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException) { } // A cache only: the next listing reads what is missing.
        }

        private FileStream Acquire()
        {
            CheckDirectPath(directory); Directory.CreateDirectory(directory);
            var file = Path.Combine(directory, ".lock"); CheckDirectPath(file);
            // A short cross-process lock protects independent VS instances sharing this workspace's history. Listing, delete and prune hold it
            // while they walk many records, so a waiter keeps trying for about ten seconds.
            for (var attempt = 0; ; attempt++)
            {
                try { return new FileStream(file, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None); }
                catch (IOException) when (attempt < 400) { Thread.Sleep(25); }
            }
        }
        internal static void CheckDirectPath(string file)
        {
            for (var current = file; !string.IsNullOrEmpty(current); current = Path.GetDirectoryName(current))
            {
                try
                {
                    // File.Exists follows links and returns false for dangling ones; inspect the entry itself instead.
                    if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                        throw new IOException("The path traverses a symbolic link or junction.");
                }
                catch (FileNotFoundException) { }
                catch (DirectoryNotFoundException) { }
            }
        }
    }
}
