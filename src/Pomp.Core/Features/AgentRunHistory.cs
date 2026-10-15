using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Pomp.Diagnostics;
using Pomp.Hosting;
using Pomp.Internal;
using Pomp.Json;
using Pomp.Ui;

namespace Pomp.Features
{
    /// <summary>Bounded run metadata. Only typed user text and allowlisted job metrics enter this file.</summary>
    internal sealed class AgentRunHistory
    {
        /// <summary>No count limit: the history keeps its newest entries (pinned ones last to go) while the file fits one UI message.</summary>
        internal const int MaxBytes = StateBudget.Limit;
        /// <summary>What a stored file may be when read (older builds wrote up to 2 MiB); the next save trims it to <see cref="MaxBytes"/>.</summary>
        private const int MaxFileBytes = 2 * 1024 * 1024;
        private static readonly object Gate = new object();
        private readonly string file;
        private readonly IPompLogger logger;
        public AgentRunHistory(string dataDirectory, string workspaceRoot, IPompLogger logger)
        {
            file = WorkspaceJsonFile.PathFor(dataDirectory, "agent-history", workspaceRoot);
            this.logger = logger;
        }
        public AgentsHistoryStatePayload View()
        {
            lock (Gate)
            {
                var state = Read();
                // The envelope has a lower delivery budget than the on-disk file. Never fault chat delivery for a large history.
                while (state.Entries.Count > 0 && Encoding.UTF8.GetByteCount(PompJson.Serialize(state)) > 1800 * 1024) { var drop = state.Entries.FindLastIndex(e => !e.Pinned); state.Entries.RemoveAt(drop < 0 ? state.Entries.Count - 1 : drop); state.Truncated = true; }
                return state;
            }
        }
        public string Begin(string sessionId, AgentRunTargetPayload target, string text, string? promptId = null)
        {
            lock (Gate)
            {
                var state = Read(); var id = Guid.NewGuid().ToString("N");
                state.Entries.Insert(0, new AgentRunEntryPayload { Id = id, SessionId = Bound(sessionId, 256), PromptId = SafeId(promptId), Target = new AgentRunTargetPayload { Kind = target.Kind, Id = Bound(target.Id, 128), Title = Bound(target.Title, 40) }, Text = Bound(text, 4000), StartedAt = DateTimeOffset.UtcNow.ToString("O") });
                Save(state); return id;
            }
        }
        public void End(string id, string status)
        {
            lock (Gate)
            {
                var state = Read(); var entry = state.Entries.FirstOrDefault(e => e.Id == id); if (entry == null) return;
                entry.Status = status; entry.EndedAt = DateTimeOffset.UtcNow.ToString("O"); Save(state);
            }
        }
        public bool Observe(string sessionId, string? runId, JObject job, bool newlyStarted)
        {
            lock (Gate)
            {
                var state = Read(); var changed = false; var id = (string?)job["id"] ?? "";
                if (id.Length == 0 || id.Length > 128 || id.Any(char.IsControl)) return false;
                var prompt = SafeId((string?)job["promptId"]);
                foreach (var entry in state.Entries.Where(e => e.SessionId == sessionId && (job["promptId"] == null || (job["promptId"]!.Type == JTokenType.Null && e.PromptId == null) || (prompt != null && e.PromptId == prompt))))
                {
                    var member = entry.Members.FirstOrDefault(m => m.JobId == id);
                    if (member == null && newlyStarted && entry.Id == runId)
                    {
                        member = new AgentRunMemberPayload { JobId = id, Agent = Bound((string?)job["agent"] ?? "", 120) }; entry.Members.Add(member); changed = true;
                    }
                    if (member == null) continue;
                    var status = Bound((string?)job["status"] ?? member.Status, 40); var p = job["progress"] as JObject;
                    if (member.Status != "running" && status == "running") status = member.Status;
                    var tools = Math.Max(member.ToolCount, Metric(p?["toolCount"], member.ToolCount)); var tokens = OptionalMetric(p?["tokens"], member.Tokens); var duration = OptionalMetric(p?["durationMs"], member.DurationMs);
                    var result = job["result"]?.Type == JTokenType.String ? Bound((string)job["result"]!, 8000) : member.Result;
                    if (member.Status == status && member.ToolCount == tools && member.Tokens == tokens && member.DurationMs == duration && member.Result == result) continue;
                    member.Status = status; member.ToolCount = tools; member.Tokens = tokens; member.DurationMs = duration; member.Result = result; changed = true;
                }
                if (changed) Save(state); return changed;
            }
        }
        /// <summary>
        /// The History view's target filter, applied identically here so "clear filtered" removes exactly the entries the view shows:
        /// the trimmed text, case-insensitively contained in the target's id or title; empty matches everything.
        /// </summary>
        internal static bool TargetMatches(string? query, AgentRunTargetPayload target)
        {
            var text = query?.Trim();
            if (string.IsNullOrEmpty(text)) return true;
            var needle = text!.ToLowerInvariant();
            return (target.Id ?? string.Empty).ToLowerInvariant().Contains(needle) || (target.Title ?? string.Empty).ToLowerInvariant().Contains(needle);
        }
        public AgentRunEntryPayload Command(AgentsHistoryCommandPayload command)
        {
            lock (Gate)
            {
                var state = Read(); var entry = state.Entries.FirstOrDefault(e => e.Id == command.Id);
                if (command.Action == "clear")
                {
                    var f = command.Filter;
                    state.Entries.RemoveAll(e => (!e.Pinned || f?.IncludePinned == true) && (f?.Kind == null || f.Kind == e.Target.Kind) && TargetMatches(f?.Id, e.Target) && (f?.Status == null || f.Status == e.Status));
                    Save(state); return new AgentRunEntryPayload();
                }
                if (entry == null) throw new InvalidOperationException(PompLanguage.Pick("실행 기록을 찾을 수 없어요.", "Run history entry was not found."));
                switch (command.Action)
                {
                    case "delete": state.Entries.Remove(entry); break;
                    case "pin": entry.Pinned = true; break;
                    case "unpin": entry.Pinned = false; break;
                    case "label":
                        if (command.Label != null && (command.Label.Length > 80 || command.Label.Any(char.IsControl))) throw new ArgumentException(PompLanguage.Pick("메모가 올바르지 않아요.", "The label is invalid."));
                        entry.Label = string.IsNullOrWhiteSpace(command.Label) ? null : command.Label!.Trim(); break;
                    case "rerun": return entry;
                    default: throw new ArgumentException(PompLanguage.Pick("알 수 없는 실행 기록 명령이에요.", "Unknown run-history command."));
                }
                Save(state); return entry;
            }
        }
        public void RemoveSession(string sessionId)
        {
            lock (Gate)
            {
                var state = Read();
                state.Entries.RemoveAll(entry => entry.SessionId == sessionId);
                Save(state);
            }
        }
        private void Save(AgentsHistoryStatePayload state)
        {
            string Encode() => new JObject { ["version"] = 1, ["truncated"] = state.Truncated, ["entries"] = PompJson.ToJObject(state)["entries"] }.ToString(Formatting.None);
            var encoded = Encode();
            while (Encoding.UTF8.GetByteCount(encoded) > MaxBytes && state.Entries.Count > 0)
            {
                var drop = state.Entries.FindLastIndex(e => !e.Pinned); if (drop < 0) drop = state.Entries.Count - 1;
                state.Entries.RemoveAt(drop); state.Truncated = true; encoded = Encode();
            }
            CheckpointStore.CheckDirectPath(file); AtomicFile.WriteAllText(file, encoded);
        }
        private AgentsHistoryStatePayload Read()
        {
            try
            {
                CheckpointStore.CheckDirectPath(file); if (!File.Exists(file)) return new AgentsHistoryStatePayload();
                using var stream = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                if (stream.Length > MaxFileBytes) throw new InvalidDataException("Run history exceeds its byte budget.");
                using var text = new StreamReader(stream, Encoding.UTF8, true);
                using var reader = new JsonTextReader(text) { MaxDepth = 12, DateParseHandling = DateParseHandling.None };
                var raw = JObject.Load(reader);
                if (reader.Read() || (int?)raw["version"] != 1 || raw["entries"] is not JArray) throw new InvalidDataException("Invalid run history.");
                var state = PompJson.ToObject<AgentsHistoryStatePayload>(raw) ?? throw new InvalidDataException("Invalid run history.");
                var seen = new HashSet<string>(StringComparer.Ordinal);
                foreach (var e in state.Entries)
                {
                    if (e == null || !Guid.TryParseExact(e.Id, "N", out _) || !seen.Add(e.Id) || e.Target == null || (e.Target.Kind != "agent" && e.Target.Kind != "team" && e.Target.Kind != "dm") || e.Target.Id.Length == 0 || e.Target.Id.Length > 128 || e.Target.Title.Length > 40 || e.SessionId.Length > 256 || (e.PromptId != null && SafeId(e.PromptId) != e.PromptId) || e.Text.Length > 4000 || e.Label?.Length > 80 || !DateTimeOffset.TryParse(e.StartedAt, out _) || (e.EndedAt != null && !DateTimeOffset.TryParse(e.EndedAt, out _)) || (e.Status != "running" && e.Status != "completed" && e.Status != "error" && e.Status != "aborted") || e.Members == null) throw new InvalidDataException("Invalid run entry.");
                    foreach (var m in e.Members) if (m == null || m.JobId.Length > 128 || m.Agent.Length > 120 || m.Status.Length > 40 || m.ToolCount < 0 || m.Tokens < 0 || (m.DurationMs < 0) || m.Result?.Length > 8000) throw new InvalidDataException("Invalid run member.");
                }
                return state;
            }
            catch (Exception ex) { logger.Warn("Agent run history was reset: " + ex.Message); return new AgentsHistoryStatePayload(); }
        }
        private static string? SafeId(string? value) => string.IsNullOrWhiteSpace(value) || value!.Length > 128 || value.Any(char.IsControl) ? null : value;
        private static string Bound(string value, int cap) => value.Length > cap ? value.Substring(0, cap) : value;
        private static long? OptionalMetric(JToken? token, long? fallback) => long.TryParse(token?.ToString(), out var value) && value >= 0 ? Math.Max(value, fallback ?? 0) : fallback;
        private static long Metric(JToken? token, long fallback) => long.TryParse(token?.ToString(), out var value) && value >= 0 ? value : fallback;
    }
}
