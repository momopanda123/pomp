using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Pomp.Diagnostics;
using Pomp.Internal;
using Pomp.Ui;

namespace Pomp.Features
{
    /// <summary>
    /// Session preferences only; agent instances are process-local and never persisted. Kept per workspace in <c>.pomp/local/partners.json</c>,
    /// next to the conversations it is about. The first time a workspace is used it takes its entries over from the single file an earlier POMP
    /// kept for all workspaces (<c>conversations/partners.json</c> in the data directory): those of the conversations in its
    /// <c>.pomp/local/sessions</c>, found by the id in each file's header. Other workspaces' entries stay in that file until they are used.
    /// </summary>
    internal sealed class ConversationPartnerStore
    {
        /// <summary>The file's size (no conversation count limit); past it the conversations used longest ago are forgotten first.</summary>
        internal const int MaxBytes = 4 * 1024 * 1024;
        private const int HeaderBudget = 16 * 1024;
        private static readonly object Gate = new object();
        private static readonly HashSet<string> Taken = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        private readonly string dataDirectory;
        private readonly IPompLogger logger;
        public ConversationPartnerStore(string dataDirectory, IPompLogger logger) { this.dataDirectory = dataDirectory; this.logger = logger; }
        /// <summary>The partner of a conversation of the workspace <paramref name="root"/> (empty: the fallback workspace); the main conversation when none was chosen.</summary>
        public ConversationPartnerPayload Get(string root, string? sessionId)
        {
            lock (Gate)
            {
                var row = Read(FileFor(root)).OfType<JObject>().FirstOrDefault(r => (string?)r["sessionId"] == sessionId);
                return new ConversationPartnerPayload { Kind = (string?)row?["partner"]?["kind"] ?? "main", Id = (string?)row?["partner"]?["id"] };
            }
        }
        /// <summary>The chosen partners of all conversations of the workspace <paramref name="root"/>, by conversation id (one read for a whole list).</summary>
        public Dictionary<string, ConversationPartnerPayload> All(string root)
        {
            lock (Gate)
                return Read(FileFor(root)).OfType<JObject>().ToDictionary(r => (string)r["sessionId"]!, r => new ConversationPartnerPayload { Kind = (string?)r["partner"]?["kind"] ?? "main", Id = (string?)r["partner"]?["id"] }, StringComparer.Ordinal);
        }
        public void Set(string root, string sessionId, ConversationPartnerPayload partner)
        {
            if (string.IsNullOrWhiteSpace(sessionId) || sessionId.Length > 256 || sessionId.Any(char.IsControl)) throw new ArgumentException("Invalid conversation id.");
            lock (Gate)
            {
                var file = FileFor(root);
                var rows = Read(file);
                foreach (var old in rows.OfType<JObject>().Where(r => (string?)r["sessionId"] == sessionId).ToArray()) old.Remove();
                var value = new JObject { ["kind"] = partner.Kind }; if (partner.Kind != "main") value["id"] = partner.Id;
                rows.Insert(0, new JObject { ["sessionId"] = sessionId, ["partner"] = value, ["updatedAt"] = DateTimeOffset.UtcNow.ToString("O") });
                var size = Encoding.UTF8.GetByteCount(rows.ToString(Formatting.None));
                while (size > MaxBytes - 64 && rows.Count > 1) { size -= Encoding.UTF8.GetByteCount(rows.Last!.ToString(Formatting.None)) + 1; rows.RemoveAt(rows.Count - 1); }
                Write(file, rows);
            }
        }
        public void Remove(string root, string sessionId)
        {
            lock (Gate)
            {
                var file = FileFor(root);
                var rows = Read(file);
                foreach (var row in rows.OfType<JObject>().Where(r => (string?)r["sessionId"] == sessionId).ToArray()) row.Remove();
                Write(file, rows);
            }
        }
        private static void Write(string file, JArray rows)
        {
            CheckpointStore.CheckDirectPath(file);
            AtomicFile.WriteAllText(file, new JObject { ["version"] = 1, ["entries"] = rows }.ToString(Formatting.None));
        }
        private string FallbackRoot => Path.Combine(dataDirectory, "workspace");
        /// <summary>The workspace's partners file; a workspace seen for the first time takes its entries over from the old shared file.</summary>
        private string FileFor(string root)
        {
            var workspace = string.IsNullOrWhiteSpace(root) ? FallbackRoot : root;
            var file = ProjectData.File(workspace, "partners.json");
            if (!File.Exists(file) && Taken.Add(file)) TakeOver(workspace, file);
            return file;
        }
        /// <summary>Moves the entries of this workspace's conversations out of the old shared file into <paramref name="file"/> (written even when there are none, so this happens once).</summary>
        private void TakeOver(string workspace, string file)
        {
            var old = Path.Combine(dataDirectory, "conversations", "partners.json");
            try
            {
                CheckpointStore.CheckDirectPath(old);
                if (!File.Exists(old)) return;
                var all = Read(old).OfType<JObject>().ToList();
                var own = SessionIds(Path.Combine(ProjectData.LocalFolder(workspace), "sessions"), new HashSet<string>(all.Select(r => (string)r["sessionId"]!), StringComparer.Ordinal));
                // The new file first: a failure between the two writes leaves the entries in both places, never in neither.
                Write(file, new JArray(all.Where(r => own.Contains((string)r["sessionId"]!))));
                var rest = all.Where(r => !own.Contains((string)r["sessionId"]!)).ToList();
                if (rest.Count == 0) File.Delete(old); else Write(old, new JArray(rest));
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException) { logger.Warn("Conversation partners could not be moved into the workspace: " + ex.Message); }
        }
        /// <summary>Which of the <paramref name="wanted"/> conversation ids have a session file in <paramref name="folder"/> (the id is in the file's first lines).</summary>
        private static HashSet<string> SessionIds(string folder, HashSet<string> wanted)
        {
            var found = new HashSet<string>(StringComparer.Ordinal);
            if (wanted.Count == 0 || !Directory.Exists(folder)) return found;
            CheckpointStore.CheckDirectPath(folder);
            foreach (var path in Directory.EnumerateFiles(folder, "*.jsonl", SearchOption.TopDirectoryOnly))
            {
                if (found.Count == wanted.Count) break;
                try
                {
                    if ((new FileInfo(path).Attributes & FileAttributes.ReparsePoint) != 0) continue;
                    using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                    var bytes = new byte[(int)Math.Min(stream.Length, HeaderBudget)]; var read = 0;
                    while (read < bytes.Length) { var count = stream.Read(bytes, read, bytes.Length - read); if (count == 0) break; read += count; }
                    // An optional title line comes first, then the session header.
                    foreach (var line in Encoding.UTF8.GetString(bytes, 0, read).Split('\n').Take(3))
                    {
                        JObject header;
                        try { header = JObject.Parse(line.Trim().TrimStart('﻿')); } catch (JsonException) { continue; }
                        if ((string?)header["type"] != "session") continue;
                        if ((string?)header["id"] is string id && wanted.Contains(id)) found.Add(id);
                        break;
                    }
                }
                catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException) { }
            }
            return found;
        }
        private JArray Read(string file)
        {
            try
            {
                CheckpointStore.CheckDirectPath(file); if (!File.Exists(file)) return new JArray();
                using var stream = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                if (stream.Length > MaxBytes) throw new InvalidDataException("Partner store exceeds its byte budget.");
                using var text = new StreamReader(stream, Encoding.UTF8, true);
                using var reader = new JsonTextReader(text) { MaxDepth = 8, DateParseHandling = DateParseHandling.None };
                var data = JObject.Load(reader);
                if (reader.Read() || (int?)data["version"] != 1 || data["entries"] is not JArray rows) throw new InvalidDataException("Invalid partner store.");
                var seen = new HashSet<string>(StringComparer.Ordinal);
                foreach (var row in rows.OfType<JObject>())
                {
                    var id = (string?)row["sessionId"]; var kind = (string?)row["partner"]?["kind"]; var target = (string?)row["partner"]?["id"];
                    if (string.IsNullOrWhiteSpace(id) || id!.Length > 256 || id.Any(char.IsControl) || !seen.Add(id) || (kind != "main" && kind != "agent" && kind != "team") || (kind != "main" && !AgentRoster.IsValidName(target ?? ""))) throw new InvalidDataException("Invalid partner entry.");
                }
                if (rows.Any(r => r is not JObject)) throw new InvalidDataException("Invalid partner entry.");
                return rows;
            }
            catch (Exception ex) { logger.Warn("Conversation partner store was reset: " + ex.Message); return new JArray(); }
        }
    }
}
