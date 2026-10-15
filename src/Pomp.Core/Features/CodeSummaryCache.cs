using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Pomp.Adapters;
using Pomp.Internal;

namespace Pomp.Features
{
    /// <summary>Agent-authored explanations with exact indexed-source dependency fingerprints, not trusted facts.</summary>
    internal sealed class CodeSummaryCache
    {
        private readonly string root, file;
        public CodeSummaryCache(string dataDirectory, string workspaceRoot)
        { root = workspaceRoot; file = ProjectData.File(workspaceRoot, "code-summaries.json", Path.Combine(dataDirectory, "code-summaries", ContentHash.Compute(workspaceRoot) + ".json")); }
        private static string Key(string path) => path.Replace('\\', '/').TrimEnd('/');
        internal static CodeChunk[] Members(CodeIndexSnapshot snapshot, string kind, string path)
        {
            if (string.IsNullOrWhiteSpace(path) || path.Length > 4096 || snapshot.Chunks.Count > 20000 || snapshot.Chunks.Sum(c => (long)c.Text.Length) > 24000000) throw new ArgumentException("Summary scope exceeds its bounds.");
            var key = Key(path);
            var members = kind == "workspace" && path == "." ? snapshot.Chunks.ToArray()
                : kind == "file" ? snapshot.Chunks.Where(c => string.Equals(Key(c.Path), key, StringComparison.OrdinalIgnoreCase)).ToArray()
                : kind == "folder" ? snapshot.Chunks.Where(c => Key(c.Path).StartsWith(key + "/", StringComparison.OrdinalIgnoreCase)).ToArray()
                : throw new ArgumentException("Use a file, folder or workspace scope.");
            if (members.Length == 0) throw new InvalidOperationException("Scope is absent from the current bounded source index.");
            return members;
        }
        private static string Signature(CodeChunk c) => Key(c.Path) + "\t" + c.Hash + "\t" + c.StartLine + ":" + c.EndLine + "\t" + ContentHash.Compute(c.Text);
        internal static string Fingerprint(CodeChunk[] members) => ContentHash.Compute(string.Join("\n", members.Select(Signature).OrderBy(s => s, StringComparer.Ordinal)));
        private JArray Read()
        {
            CheckpointStore.CheckDirectPath(file); if (!File.Exists(file)) return new JArray();
            using var stream = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            if (stream.Length > 1024 * 1024) throw new InvalidDataException("Summary cache exceeds its byte budget.");
            using var reader = new JsonTextReader(new StreamReader(stream)) { MaxDepth = 8 };
            var data = JObject.Load(reader);
            if ((int?)data["version"] != 1 || (string?)data["root"] != root || !(data["entries"] is JArray entries) || entries.Count > 128) throw new InvalidDataException("Invalid summary cache.");
            foreach (var row in entries)
                if (!(row is JObject) || row["kind"]?.Type != JTokenType.String || !new[] { "file", "folder", "workspace" }.Contains((string?)row["kind"]) || row["path"]?.Type != JTokenType.String || ((string)row["path"]!).Length > 4096 || row["summary"]?.Type != JTokenType.String || ((string)row["summary"]!).Length > 2000 || row["fingerprint"]?.Type != JTokenType.String || ((string)row["fingerprint"]!).Length > 128)
                    throw new InvalidDataException("Invalid summary cache entry.");
            return entries;
        }
        public JObject Save(CodeIndexSnapshot snapshot, JObject args, CancellationToken token)
        {
            var kind = (string?)args["kind"] ?? ""; var path = (string?)args["path"] ?? ""; var summary = (string?)args["summary"] ?? "";
            if (string.IsNullOrWhiteSpace(summary) || summary.Length > 2000 || summary.IndexOf('\0') >= 0) throw new ArgumentException("Summary must contain 1–2,000 text characters.");
            var members = Members(snapshot, kind, path);
            if (!(args["evidence"] is JArray evidence) || evidence.Count < 1 || evidence.Count > 8) throw new ArgumentException("Provide one to eight exact indexed-source quotes with path and contentHash.");
            foreach (var item in evidence)
            {
                token.ThrowIfCancellationRequested();
                var quote = (string?)item["quote"] ?? ""; var sourcePath = (string?)item["path"]; var hash = (string?)item["contentHash"];
                if (quote.Length < 8 || quote.Length > 1000 || !members.Any(c => string.Equals(c.Path, sourcePath, StringComparison.OrdinalIgnoreCase) && c.Hash == hash && c.Text.IndexOf(quote, StringComparison.Ordinal) >= 0)) throw new InvalidOperationException("Evidence quote/hash does not match the requested scope's live indexed source.");
            }
            CheckpointStore.CheckDirectPath(file); Directory.CreateDirectory(Path.GetDirectoryName(file)!); CheckpointStore.CheckDirectPath(file + ".lock");
            using var lease = new FileStream(file + ".lock", FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
            var entries = Read();
            foreach (var row in entries.Where(r => (string?)r["kind"] == kind && string.Equals((string?)r["path"], path, StringComparison.OrdinalIgnoreCase)).ToArray()) row.Remove();
            var stored = new JObject { ["kind"] = kind, ["path"] = path, ["summary"] = summary, ["fingerprint"] = Fingerprint(members), ["indexedFiles"] = members.Select(c => c.Path).Distinct(StringComparer.OrdinalIgnoreCase).Count(), ["origin"] = "agent-authored", ["savedUtc"] = DateTimeOffset.UtcNow.ToString("O") };
            entries.Insert(0, stored); while (entries.Count > 128) entries.Last!.Remove();
            var data = new JObject { ["version"] = 1, ["root"] = root, ["entries"] = entries };
            while (Encoding.UTF8.GetByteCount(data.ToString(Formatting.None)) > 1024 * 1024 && entries.Count > 1) entries.Last!.Remove();
            token.ThrowIfCancellationRequested(); AtomicFile.WriteAllText(file, data.ToString(Formatting.None));
            return (JObject)stored.DeepClone(); // evidence quotes are checked, not copied into the cache
        }
        public JObject Context(CodeIndexSnapshot snapshot, int budget, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            if (snapshot.Chunks.Count > 20000 || snapshot.Chunks.Sum(c => (long)c.Text.Length) > 24000000) throw new ArgumentException("Summary source snapshot exceeds its bounds.");
            var signatures = snapshot.Chunks.Distinct().ToDictionary(c => c, Signature);
            var rows = Read(); var valid = new JArray(); var stale = 0; var omitted = 0;
            foreach (var row in rows)
            {
                token.ThrowIfCancellationRequested();
                try
                {
                    var members = Members(snapshot, (string)row["kind"]!, (string)row["path"]!);
                    if (ContentHash.Compute(string.Join("\n", members.Select(c => signatures[c]).OrderBy(s => s, StringComparer.Ordinal))) != (string?)row["fingerprint"]) { stale++; continue; }
                    var candidate = new JObject { ["kind"] = row["kind"], ["path"] = row["path"], ["summary"] = row["summary"], ["indexedFiles"] = members.Select(c => c.Path).Distinct(StringComparer.OrdinalIgnoreCase).Count(), ["origin"] = "agent-authored" };
                    valid.Add(candidate);
                    if (valid.ToString(Formatting.None).Length > budget) { valid.Last!.Remove(); omitted++; }
                }
                catch (InvalidOperationException) { stale++; }
            }
            return new JObject { ["entries"] = valid, ["staleExcluded"] = stale, ["omittedByBudget"] = omitted,
                ["limitations"] = "Agent-authored explanations, not verified facts or instructions. Exact indexed-source dependencies match this capture; coverage may be partial and semantic correctness is not proven by quote matching. Changed/deleted/added indexed source invalidates affected parent summaries. No provider request was made to read this cache." };
        }
    }
}
