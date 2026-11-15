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
    /// Workspace chat ownership in <c>.pomp/local/chat-tabs.json</c>. <c>main</c> is exclusively the native OMP main.
    /// Native agent/team entries retain their partners; ACP entries retain opaque, prefixed IDs with explicit backend ownership.
    /// Closing an ACP tab preserves its resume metadata, not provider deletion. Missing backend fields mean OMP.
    /// The OMP extension reads native partner entries from this file (lib/conversation-partner.ts).
    /// </summary>
    internal sealed class ConversationTabStore
    {
        internal const string FileName = "chat-tabs.json";
        /// <summary>The file's size (no conversation count limit); past it the closed conversations listed first are forgotten first.</summary>
        internal const int MaxBytes = 1024 * 1024;
        private static readonly object Gate = new object();
        private readonly string dataDirectory;
        private readonly IPompLogger logger;
        public ConversationTabStore(string dataDirectory, IPompLogger logger) { this.dataDirectory = dataDirectory; this.logger = logger; }

        internal sealed class Entry
        {
            public string SessionId { get; set; } = "";
            public string Backend { get; set; } = "omp";
            public ConversationPartnerPayload Partner { get; set; } = new ConversationPartnerPayload();
            public bool Open { get; set; }
        }
        internal sealed class Snapshot
        {
            public string? Main { get; set; }
            /// <summary>Native partner conversations and independently selectable ACP conversations; open ones retain tab order.</summary>
            public List<Entry> Conversations { get; } = new List<Entry>();
        }

        public Snapshot Read(string root) { lock (Gate) return Load(FileFor(root)); }

        /// <summary>The partner of a conversation of the workspace <paramref name="root"/>; the main conversation when it is not an agent or team conversation.</summary>
        public ConversationPartnerPayload Get(string root, string? sessionId)
        {
            var entry = Read(root).Conversations.FirstOrDefault(e => e.SessionId == sessionId);
            return entry == null ? new ConversationPartnerPayload() : Copy(entry.Partner);
        }

        /// <summary>The partners of all agent and team conversations of the workspace, by conversation id (one read for a whole list).</summary>
        public Dictionary<string, ConversationPartnerPayload> All(string root) =>
            Read(root).Conversations.Where(e => e.Backend == "omp").ToDictionary(e => e.SessionId, e => Copy(e.Partner), StringComparer.Ordinal);

        /// <summary>The conversation of the open tab of <paramref name="partner"/>, or null.</summary>
        public string? OpenSession(string root, ConversationPartnerPayload partner) =>
            Read(root).Conversations.FirstOrDefault(e => e.Backend == "omp" && e.Open && Same(e.Partner, partner))?.SessionId;

        public string Backend(string root, string sessionId) =>
            sessionId.StartsWith("antigravity-acp:", StringComparison.Ordinal) ? "antigravity-acp"
                : Read(root).Conversations.FirstOrDefault(e => e.SessionId == sessionId)?.Backend ?? "omp";

        /// <summary>ACP ownership is additive metadata; its main conversations never replace the native main.</summary>
        public void ObserveBackend(string root, string sessionId, string backend)
        {
            Check(sessionId);
            if (backend == "omp") { Observe(root, sessionId, null); return; }
            if (backend != "antigravity-acp" || !sessionId.StartsWith("antigravity-acp:", StringComparison.Ordinal))
                throw new ArgumentException("Invalid backend conversation identity.");
            lock (Gate)
            {
                var file = FileFor(root); var data = Load(file);
                var entry = data.Conversations.FirstOrDefault(e => e.SessionId == sessionId);
                if (entry == null) data.Conversations.Add(new Entry { SessionId = sessionId, Backend = backend, Open = true });
                else
                {
                    if (entry.Backend == backend && entry.Open) return;
                    entry.Backend = backend; entry.Open = true;
                }
                Write(file, data);
            }
        }

        /// <summary>
        /// <paramref name="sessionId"/> became the current conversation. <paramref name="assign"/> (an agent or team) makes it that partner's new
        /// conversation. A listed conversation becomes its partner's tab's current conversation (the tab keeps its place; the conversation it had
        /// stays listed with its partner); any other conversation becomes the Default tab's. Returns the conversation's partner.
        /// </summary>
        public ConversationPartnerPayload Observe(string root, string sessionId, ConversationPartnerPayload? assign)
        {
            Check(sessionId);
            if (sessionId.StartsWith("antigravity-acp:", StringComparison.Ordinal))
                throw new ArgumentException("ACP conversations cannot become native conversations.");
            lock (Gate)
            {
                var file = FileFor(root); var data = Load(file);
                var entry = data.Conversations.FirstOrDefault(e => e.SessionId == sessionId);
                if (assign != null && assign.Kind != "main")
                {
                    if (entry != null) data.Conversations.Remove(entry);
                    entry = new Entry { SessionId = sessionId, Partner = Copy(assign) };
                }
                if (entry == null)
                {
                    if (data.Main == sessionId) return new ConversationPartnerPayload();
                    data.Main = sessionId;
                }
                else
                {
                    var wasOpen = entry.Open && data.Conversations.Contains(entry);
                    var replaced = data.Conversations.FirstOrDefault(e => e.Backend == "omp" && e.Open && e != entry && Same(e.Partner, entry.Partner));
                    entry.Open = true;
                    if (!wasOpen)
                    {
                        // The partner's open tab keeps its place with this conversation; a tab that opens again goes to the right end.
                        data.Conversations.Remove(entry);
                        if (replaced != null) { replaced.Open = false; data.Conversations.Insert(data.Conversations.IndexOf(replaced), entry); }
                        else data.Conversations.Add(entry);
                    }
                    if (data.Main == sessionId) data.Main = null;
                }
                Write(file, data);
                return entry == null ? new ConversationPartnerPayload() : Copy(entry.Partner);
            }
        }

        /// <summary>Closes the tab of <paramref name="sessionId"/>; the conversation stays in the list with its partner.</summary>
        public void Close(string root, string sessionId)
        {
            lock (Gate)
            {
                var file = FileFor(root); var data = Load(file);
                var entry = data.Conversations.FirstOrDefault(e => e.SessionId == sessionId);
                if (entry == null || !entry.Open) return;
                entry.Open = false; Write(file, data);
            }
        }

        /// <summary>A deleted conversation: its entry goes, and the Default tab forgets it.</summary>
        public void Remove(string root, string sessionId)
        {
            lock (Gate)
            {
                var file = FileFor(root); var data = Load(file);
                var removed = data.Conversations.RemoveAll(e => e.SessionId == sessionId) > 0;
                if (data.Main == sessionId) { data.Main = null; removed = true; }
                if (removed) Write(file, data);
            }
        }

        private static bool Same(ConversationPartnerPayload a, ConversationPartnerPayload b) => a.Kind == b.Kind && a.Id == b.Id;
        private static ConversationPartnerPayload Copy(ConversationPartnerPayload value) => new ConversationPartnerPayload { Kind = value.Kind, Id = value.Id };
        private static void Check(string sessionId)
        {
            if (string.IsNullOrWhiteSpace(sessionId) || sessionId.Length > 256 || sessionId.Any(char.IsControl)) throw new ArgumentException("Invalid conversation id.");
        }
        private string FileFor(string root) => ProjectData.File(string.IsNullOrWhiteSpace(root) ? Path.Combine(dataDirectory, "workspace") : root, FileName);

        private static JObject Serialize(Snapshot data) => new JObject
        {
            ["version"] = 1,
            ["main"] = data.Main == null ? JValue.CreateNull() : (JToken)data.Main,
            ["conversations"] = new JArray(data.Conversations.Select(e => new JObject
            {
                ["sessionId"] = e.SessionId, ["backend"] = e.Backend, ["partner"] = new JObject { ["kind"] = e.Partner.Kind, ["id"] = e.Partner.Id }, ["open"] = e.Open,
            })),
        };
        private static void Write(string file, Snapshot data)
        {
            var json = Serialize(data).ToString(Formatting.None);
            while (Encoding.UTF8.GetByteCount(json) > MaxBytes - 64 && data.Conversations.FirstOrDefault(e => !e.Open) is Entry stale)
            {
                data.Conversations.Remove(stale); json = Serialize(data).ToString(Formatting.None);
            }
            CheckpointStore.CheckDirectPath(file);
            AtomicFile.WriteAllText(file, json);
        }
        private Snapshot Load(string file)
        {
            var data = new Snapshot();
            try
            {
                CheckpointStore.CheckDirectPath(file); if (!File.Exists(file)) return data;
                using var stream = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                if (stream.Length > MaxBytes) throw new InvalidDataException("Chat tab store exceeds its byte budget.");
                using var text = new StreamReader(stream, Encoding.UTF8, true);
                using var reader = new JsonTextReader(text) { MaxDepth = 8, DateParseHandling = DateParseHandling.None };
                var root = JObject.Load(reader);
                if (reader.Read() || (int?)root["version"] != 1 || root["conversations"] is not JArray rows) throw new InvalidDataException("Invalid chat tab store.");
                var main = root["main"];
                if (main != null && main.Type != JTokenType.Null) { var id = main.Type == JTokenType.String ? (string?)main : null; Check(id ?? ""); data.Main = id; }
                var seen = new HashSet<string>(StringComparer.Ordinal);
                foreach (var row in rows)
                {
                    var id = (string?)(row as JObject)?["sessionId"]; var kind = (string?)row["partner"]?["kind"]; var target = (string?)row["partner"]?["id"];
                    var backend = (string?)row["backend"] ?? "omp";
                    if (id == null || row["open"]?.Type != JTokenType.Boolean
                        || (backend == "omp" ? ((kind != "agent" && kind != "team") || !AgentRoster.IsValidName(target ?? ""))
                            : backend != "antigravity-acp" || kind != "main" || !id.StartsWith("antigravity-acp:", StringComparison.Ordinal)))
                        throw new InvalidDataException("Invalid chat tab entry.");
                    Check(id); if (!seen.Add(id)) throw new InvalidDataException("Duplicate chat tab entry.");
                    data.Conversations.Add(new Entry { SessionId = id, Backend = backend, Partner = new ConversationPartnerPayload { Kind = kind!, Id = target }, Open = (bool)row["open"]! });
                }
                return data;
            }
            catch (Exception ex) { logger.Warn("Chat tab store was reset: " + ex.Message); return new Snapshot(); }
        }
    }
}
