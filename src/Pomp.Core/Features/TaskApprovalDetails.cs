using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using Pomp.Permissions;

namespace Pomp.Features
{
    /// <summary>Display-only enrichment; ambiguous titles never select an assignment or change approval behavior.</summary>
    internal sealed class TaskApprovalDetails
    {
        private readonly object gate = new object();
        private readonly Dictionary<string, Entry> pending = new Dictionary<string, Entry>(StringComparer.Ordinal);
        /// <summary>The task text all pending entries may hold together (no count limit; entries also expire after 30 seconds).</summary>
        internal const long MaxPendingChars = 8 * 1024 * 1024;
        private string session = string.Empty;

        public void SetSession(string value)
        {
            lock (gate)
            {
                if (session == value) return;
                pending.Clear(); session = value;
            }
        }

        public void Clear() { lock (gate) pending.Clear(); }
        public void End(string id) { lock (gate) pending.Remove(id); }

        public void Add(string sessionId, string id, List<PermissionTask> tasks)
        {
            if (id.Length == 0 || id.Length > 200 || tasks.Count == 0 || tasks.Any(t =>
                string.IsNullOrWhiteSpace(t.Agent) || t.Agent.Length > 128 || t.Agent.Any(char.IsControl) || t.Task.Length > 8000 || t.Task.IndexOf('\0') >= 0)) return;
            lock (gate)
            {
                if (session != sessionId || session.Length == 0) return;
                Prune();
                pending.Remove(id);
                if (pending.Values.Sum(e => e.Chars) + Chars(tasks) > MaxPendingChars) return;
                pending[id] = new Entry(tasks.Select(t => new PermissionTask { Agent = t.Agent, Task = t.Task }).ToList());
            }
        }

        public List<PermissionTask>? Match(string sessionId, string title)
        {
            lock (gate)
            {
                title = title.Replace("\r\n", "\n");
                if (sessionId != session || session.Length == 0 || !title.StartsWith("Allow tool: task\n", StringComparison.Ordinal)) return null;
                Prune();
                var matches = pending.Values.Where(entry => Matches(title.Replace("\r\n", "\n"), entry.Tasks)).Take(2).ToList();
                return matches.Count == 1 ? matches[0].Tasks.Select(t => new PermissionTask { Agent = t.Agent, Task = t.Task }).ToList() : null;
            }
        }

        private void Prune()
        {
            foreach (var id in pending.Where(p => DateTime.UtcNow - p.Value.Created > TimeSpan.FromSeconds(30)).Select(p => p.Key).ToArray()) pending.Remove(id);
        }

        private static bool Matches(string title, List<PermissionTask> tasks)
        {
            var batch = Regex.Match(title, @"(?:^|\n)Batch agents: ([^\n]+)\n");
            var details = batch.Success ? title.Substring(batch.Index + batch.Length) : title;
            var agent = Regex.Match(details, @"(?:^|\n)Agent: ([^\n]+)\n");
            var marker = details.IndexOf("\nTask:\n", StringComparison.Ordinal);
            if (!agent.Success || marker < 0 || agent.Groups[1].Value.Trim() != tasks[0].Agent) return false;
            if (batch.Success)
            {
                // The engine lists distinct agents in first-occurrence order, with a count for each.
                var counts = new List<(string Agent, int Count)>();
                foreach (var task in tasks)
                {
                    var index = counts.FindIndex(c => c.Agent == task.Agent);
                    if (index < 0) counts.Add((task.Agent, 1));
                    else counts[index] = (task.Agent, counts[index].Count + 1);
                }
                if (batch.Groups[1].Value != string.Join(", ", counts.Select(c => c.Agent + " ×" + c.Count))) return false;
            }
            else if (tasks.Count != 1) return false;
            var preview = details.Substring(marker + "\nTask:\n".Length);
            if (batch.Success && tasks.Count > 1)
            {
                var tail = "\n+" + (tasks.Count - 1) + " more task" + (tasks.Count == 2 ? "" : "s");
                if (!preview.EndsWith(tail, StringComparison.Ordinal)) return false;
                preview = preview.Substring(0, preview.Length - tail.Length);
            }
            else
            {
                var context = preview.IndexOf("\nContext:\n", StringComparison.Ordinal);
                if (context >= 0) preview = preview.Substring(0, context);
            }
            preview = Regex.Replace(preview, @"\[…\d+ch elided…\]$", "");
            preview = Normalize(preview);
            return preview.Length > 0 && Normalize(tasks[0].Task).StartsWith(preview, StringComparison.Ordinal);
        }

        private static long Chars(List<PermissionTask> tasks) => tasks.Sum(t => (long)t.Agent.Length + t.Task.Length);
        private static string Normalize(string text) => Regex.Replace(text, @"\s+", " ").Trim();
        private sealed class Entry
        {
            public Entry(List<PermissionTask> tasks) { Tasks = tasks; }
            public List<PermissionTask> Tasks { get; }
            public long Chars => TaskApprovalDetails.Chars(Tasks);
            public DateTime Created { get; } = DateTime.UtcNow;
        }
    }
}
