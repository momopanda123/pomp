using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Pomp.Hosting;

namespace Pomp.Features
{
    /// <summary>
    /// Event rules of one workspace: when an agent or team run ends, or proposed cases wait for feedback, notify the user or
    /// forward a message to the main conversation, an agent or a team. Separate from the IDE save/build hooks (.pomp/hooks).
    /// Rules are data only; whether a forward may run is decided per conversation by the host (PompSession.AgentHooks.cs).
    /// Stored in the workspace's <c>.pomp/local</c> (not committed); a malformed file fails closed.
    /// </summary>
    internal sealed class AgentHookStore
    {
        internal const int MaxMessageChars = 2000;
        internal const int MaxGlobChars = 200;
        internal static readonly string[] Events = { "agent.completed", "agent.failed", "team.completed", "team.failed", "feedback.waiting", "ide.saved", "ide.build.succeeded", "ide.build.failed" };

        private static readonly Regex NameRegex = new Regex(@"\A[a-z0-9-]{2,40}\z", RegexOptions.CultureInvariant);
        private static readonly WorkspaceJsonFile.Shape Shape = new WorkspaceJsonFile.Shape
        {
            Folder = "agent-hooks", Rows = "rules", MaxBytes = 256 * 1024, MaxRows = int.MaxValue,
            Fields = new HashSet<string>(StringComparer.Ordinal) { "id", "name", "enabled", "event", "sourceKind", "sourceId", "glob", "action", "targetKind", "targetId", "message", "revision" },
            CheckRow = CheckRow,
            Busy = ("다른 창에서 이벤트 규칙을 쓰고 있어요.", "Event rules are being changed in another window."),
            Full = ("이벤트 규칙 파일이 가득 찼어요.", "The event rule file is full."),
            Unreadable = ("이벤트 규칙 파일을 읽지 못했어요: ", "The event rule file could not be read: "),
        };

        private readonly WorkspaceJsonFile store;

        internal AgentHookStore(string dataDirectory, string workspaceRoot) => store = new WorkspaceJsonFile(dataDirectory, workspaceRoot, Shape);

        internal sealed class Rule
        {
            public string Id { get; set; } = "";
            public string Name { get; set; } = "";
            public bool Enabled { get; set; }
            public string Event { get; set; } = "agent.completed";
            public string SourceKind { get; set; } = "any";
            public string? SourceId { get; set; }
            /// <summary>ide.saved only: workspace-relative glob, default "**" (docs/architecture.md §8.10).</summary>
            public string? Glob { get; set; }
            public string Action { get; set; } = "notify";
            public string? TargetKind { get; set; }
            public string? TargetId { get; set; }
            public string Message { get; set; } = "";
            public int Revision { get; set; } = 1;
        }

        internal IReadOnlyList<Rule> List() => store.Read().OfType<JObject>().Select(row => row.ToObject<Rule>(JsonSerializer.Create(Pomp.Json.PompJson.Settings))!).ToList();

        /// <summary>Creates (empty id) or replaces (id + revision) a rule.</summary>
        internal string Save(Rule input, string? id, int revision)
        {
            var rule = Validate(input);
            return store.Mutate(rows =>
            {
                if (string.IsNullOrEmpty(id))
                {
                    rule.Id = Guid.NewGuid().ToString("N"); rule.Revision = 1;
                    rows.Add(Row(rule));
                    return rule.Id;
                }

                var index = IndexOf(rows, id!, revision);
                rule.Id = id!; rule.Revision = checked(revision + 1);
                rows[index] = Row(rule);
                return rule.Id;
            });
        }

        internal void Delete(string id, int revision) => store.Mutate(rows => { rows.RemoveAt(IndexOf(rows, id, revision)); return id; });

        private static int IndexOf(JArray rows, string id, int revision)
        {
            for (var i = 0; i < rows.Count; i++)
                if ((string?)rows[i]["id"] == id && (int)rows[i]["revision"]! == revision) return i;
            throw new InvalidOperationException(PompLanguage.Pick("규칙이 바뀌었거나 지워졌어요.", "The rule changed or was deleted."));
        }

        private static JObject Row(Rule rule) => JObject.FromObject(rule, JsonSerializer.Create(Pomp.Json.PompJson.Settings));

        internal static Rule Validate(Rule input)
        {
            var name = (input.Name ?? "").Trim(); var message = (input.Message ?? "").Trim();
            if (name.Length == 0 || name.Length > 60 || name.Any(char.IsControl)) throw new ArgumentException(PompLanguage.Pick("규칙 이름이 올바르지 않아요.", "The rule name is invalid."));
            if (!Events.Contains(input.Event)) throw new ArgumentException(PompLanguage.Pick("알 수 없는 이벤트예요.", "Unknown event."));
            var teamEvent = input.Event.StartsWith("team.", StringComparison.Ordinal);
            var ideEvent = input.Event.StartsWith("ide.", StringComparison.Ordinal);
            var glob = input.Glob;
            if (ideEvent)
            {
                // Routine rules (docs/architecture.md §8.10) watch the whole IDE, not one agent or team.
                if (input.SourceKind != "any" || input.SourceId != null) throw Bad();
                // A glob filters only saves; build outcomes have no path to match.
                if (input.Event == "ide.saved")
                {
                    glob = (input.Glob ?? "").Trim();
                    if (glob.Length == 0) glob = "**";
                    if (glob.Length > MaxGlobChars || glob.Any(char.IsControl)) throw new ArgumentException(PompLanguage.Pick("저장 경로가 올바르지 않아요.", "The save path is invalid."));
                    try { ProjectHook.Pattern(glob); }
                    catch (ArgumentException) { throw new ArgumentException(PompLanguage.Pick("저장 경로가 올바르지 않아요.", "The save path is invalid.")); }
                }
                else if (input.Glob != null) throw Bad();
            }
            else if (input.Glob != null) throw Bad();
            if (input.SourceKind == "any") { if (input.SourceId != null) throw Bad(); }
            else if ((input.SourceKind == "agent" && !teamEvent && input.Event != "feedback.waiting") || (input.SourceKind == "team" && teamEvent)) { if (input.SourceId == null || !NameRegex.IsMatch(input.SourceId)) throw Bad(); }
            else throw Bad();
            if (input.Action == "notify") { if (input.TargetKind != null || input.TargetId != null) throw Bad(); }
            else if (input.Action == "forward")
            {
                if (input.TargetKind == "main") { if (input.TargetId != null) throw Bad(); }
                else if (input.TargetKind == "agent" || input.TargetKind == "team") { if (input.TargetId == null || !NameRegex.IsMatch(input.TargetId)) throw Bad(); }
                else throw Bad();
                if (message.Length == 0) throw new ArgumentException(PompLanguage.Pick("보낼 메시지가 비어 있어요.", "The message to send is empty."));
                if (input.Event.StartsWith("team.", StringComparison.Ordinal) && input.TargetKind == "team" && input.TargetId == input.SourceId)
                    throw new ArgumentException(PompLanguage.Pick("전달 대상이 출발 팀과 같아요.", "The target is the same as the source team."));
            }
            else throw Bad();
            if (message.Length > MaxMessageChars || message.Any(c => char.IsControl(c) && c != '\n' && c != '\r' && c != '\t'))
                throw new ArgumentException(PompLanguage.Pick("메시지가 올바르지 않아요.", "The message is invalid."));
            return new Rule { Name = name, Enabled = input.Enabled, Event = input.Event, SourceKind = input.SourceKind, SourceId = input.SourceId, Glob = glob, Action = input.Action, TargetKind = input.TargetKind, TargetId = input.TargetId, Message = message, Revision = input.Revision };

            static ArgumentException Bad() => new ArgumentException(PompLanguage.Pick("이벤트·출발·동작·대상 조합이 올바르지 않아요.", "Invalid combination of event, source, action and target."));
        }

        /// <summary>Row types and meaning: a wrong type or an invalid combination makes the whole file unreadable (it stays as it is).</summary>
        private static void CheckRow(JObject row)
        {
            if (new[] { "name", "event", "sourceKind", "action", "message" }.Any(f => row[f]?.Type != JTokenType.String)
                || row["enabled"]?.Type != JTokenType.Boolean || row["revision"]?.Type != JTokenType.Integer || (int)row["revision"]! < 1
                || new[] { "sourceId", "targetKind", "targetId", "glob" }.Any(f => row[f] != null && row[f]!.Type != JTokenType.String && row[f]!.Type != JTokenType.Null))
                throw new InvalidDataException("Invalid rule entry.");
            Validate(new Rule
            {
                Name = (string)row["name"]!, Event = (string)row["event"]!, SourceKind = (string)row["sourceKind"]!, SourceId = (string?)row["sourceId"],
                Glob = (string?)row["glob"],
                Action = (string)row["action"]!, TargetKind = (string?)row["targetKind"], TargetId = (string?)row["targetId"], Message = (string)row["message"]!,
            });
        }
    }
}
