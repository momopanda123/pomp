using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Pomp.Adapters;
using Pomp.Diagnostics;
using Pomp.Hosting;
using Pomp.Internal;
using Pomp.Ui;

namespace Pomp.Features
{
    internal sealed class AdvisorOperationContext
    {
        public object Identity { get; set; } = new object();
        public string SessionId { get; set; } = "";
        public CancellationToken Token { get; set; }
        public long Expires { get; set; }
        public Func<bool> IsCurrent { get; set; } = () => false;
    }

    /// <summary>Bounded Advisor management; YAML and effective scope resolution remain inside the running engine.</summary>
    internal sealed class AdvisorController : IDisposable
    {
        internal static readonly HashSet<string> Tools = new HashSet<string>(new[] { "read", "bash", "edit", "ast_grep", "ast_edit", "ask", "debug", "ida", "eval", "github", "glob", "grep", "find", "lsp", "checkpoint", "rewind", "context_notes", "new_context", "security_scan", "task", "wait", "todo", "web_search", "write", "memory_edit", "retain", "recall", "reflect", "learn", "manage_skill" }, StringComparer.Ordinal);
        private readonly UiHub ui;
        private readonly IEditorAdapter editor;
        private readonly WorkspaceTrust trust;
        private readonly Func<bool> includeExtensions;
        private readonly Func<AdvisorOperationContext?> capture;
        private readonly Func<AdvisorOperationContext, JObject, CancellationToken, Task<JObject>> send;
        private readonly SemaphoreSlim operation = new SemaphoreSlim(1, 1);
        private readonly CancellationTokenSource lifetime = new CancellationTokenSource();
        // Publications never take the host state lock, avoiding the UI hub's snapshot lock inversion.
        private readonly object gate = new object();
        private readonly Dictionary<string, string> selected = new Dictionary<string, string>(StringComparer.Ordinal);
        // The advisors each conversation turned on or off for itself (session id → advisor slug → on), over the files' `enabled`.
        private readonly Dictionary<string, Dictionary<string, bool>> choices = new Dictionary<string, Dictionary<string, bool>>(StringComparer.Ordinal);
        private readonly string? exportsDirectory;
        private AdvisorsStatePayload state = new AdvisorsStatePayload();
        private object? identity;
        private string sessionId = "";
        private bool disposed;
        private int refreshing;
        private int refreshQueued;
        internal TimeSpan Deadline { get; set; } = TimeSpan.FromSeconds(15);
        // The extension sends at most 256 KiB of UTF-8 transcript text.
        private const int MaxHistoryChars = 256 * 1024;

        public AdvisorController(UiHub ui, IEditorAdapter editor, WorkspaceTrust trust, Func<bool> includeExtensions,
            Func<AdvisorOperationContext?> capture, Func<AdvisorOperationContext, JObject, CancellationToken, Task<JObject>> send, string? exportsDirectory = null)
        { this.ui = ui; this.editor = editor; this.trust = trust; this.includeExtensions = includeExtensions; this.capture = capture; this.send = send; this.exportsDirectory = exportsDirectory; }

        /// <summary>The engine's name key of an advisor: its letters and digits, lowercase, joined by dashes ("advisor" when it has none).</summary>
        internal static string Slug(string name)
        {
            var slug = Regex.Replace(name.ToLowerInvariant(), "[^a-z0-9]+", "-").Trim('-');
            return slug.Length == 0 ? "advisor" : slug;
        }

        private JObject Choices(string session)
        {
            lock (gate) return choices.TryGetValue(session, out var map) ? JObject.FromObject(map) : new JObject();
        }

        /// <summary>
        /// The engine command that turns one advisor on or off in this conversation. Turned on while the conversation runs no advisor, only
        /// that one starts; the conversation's advisors stop when none is left on. Without advisor files the engine's own advisor is the
        /// conversation's only one.
        /// </summary>
        private JObject SelectCommand(AdvisorOperationContext context, string name, bool on, out Dictionary<string, bool> next)
        {
            var slug = Slug(name);
            List<AdvisorRosterPayload> roster; bool enabled, implicitDefault;
            lock (gate)
            {
                if (!Matches(context)) throw new InvalidOperationException("stale-session");
                roster = state.Roster.ToList(); enabled = state.Enabled; implicitDefault = state.ImplicitDefault;
                next = choices.TryGetValue(context.SessionId, out var saved) ? new Dictionary<string, bool>(saved, StringComparer.Ordinal) : new Dictionary<string, bool>(StringComparer.Ordinal);
            }
            bool nextEnabled;
            if (implicitDefault || roster.Count == 0) nextEnabled = on;
            else
            {
                if (!roster.Any(row => Slug(row.Name) == slug)) throw new InvalidOperationException("unknown-advisor");
                next[slug] = on;
                if (on && !enabled) foreach (var other in roster) { var key = Slug(other.Name); if (key != slug) next[key] = false; }
                var chosen = next;
                nextEnabled = roster.Any(row => chosen.TryGetValue(Slug(row.Name), out var value) ? value : row.Enabled);
            }
            return new JObject { ["action"] = "apply", ["choices"] = JObject.FromObject(next), ["enabled"] = nextEnabled };
        }

        /// <summary>The notes the extension read from the conversation, checked for shape and size; they are shown to the user as data.</summary>
        private static JArray Notes(JToken? value)
        {
            var notes = new JArray();
            if (value == null || value.Type == JTokenType.Null) return notes;
            if (value is not JArray rows) throw new InvalidOperationException("invalid-result");
            foreach (var row in rows)
            {
                if (row is not JObject note) throw new InvalidOperationException("invalid-result");
                string? Field(string key, int max, bool required)
                {
                    var field = note[key];
                    if (field == null || field.Type == JTokenType.Null) return required ? throw new InvalidOperationException("invalid-result") : null;
                    if (field.Type != JTokenType.String || ((string)field!).Length > max) throw new InvalidOperationException("invalid-result");
                    return (string)field!;
                }
                var clean = new JObject { ["advisor"] = Field("advisor", 200, true), ["note"] = Field("note", 8000, true) };
                if (Field("severity", 32, false) is string severity) clean["severity"] = severity;
                if (Field("at", 40, false) is string at) clean["at"] = at;
                notes.Add(clean);
            }
            return notes;
        }

        /// <summary>Writes one advisor's transcript under the exports folder and opens it in the editor (the newest ten such files are kept).</summary>
        private async Task OpenHistoryAsync(string name, string text, CancellationToken token)
        {
            if (exportsDirectory == null) throw new InvalidOperationException("unsupported");
            var folder = Path.GetFullPath(exportsDirectory);
            CheckpointStore.CheckDirectPath(folder);
            Directory.CreateDirectory(folder);
            var file = Path.Combine(folder, "advisor-" + Slug(name) + "-" + DateTime.Now.ToString("yyyyMMdd-HHmmss", System.Globalization.CultureInfo.InvariantCulture) + ".md");
            AtomicFile.WriteAllText(file, text);
            foreach (var old in new DirectoryInfo(folder).GetFiles("advisor-*.md").OrderByDescending(info => info.LastWriteTimeUtc).Skip(10))
            {
                try { old.Delete(); } catch (IOException) { } catch (UnauthorizedAccessException) { }
            }
            if (!await editor.OpenDocumentAsync(file, null, token).ConfigureAwait(false)) throw new InvalidOperationException("open-failed");
        }

        public void Observe(object? nextIdentity, string? nextSession)
        {
            lock (gate)
            {
                if (disposed || ReferenceEquals(identity, nextIdentity) && sessionId == (nextSession ?? "")) return;
                identity = nextIdentity; sessionId = nextSession ?? ""; selected.Clear();
                state = new AdvisorsStatePayload { SessionId = string.IsNullOrEmpty(sessionId) ? null : sessionId, UpdatedUtc = DateTimeOffset.UtcNow.ToString("O") };
                ui.Post(UiMessageTypes.AdvisorsState, state, UiDelivery.State);
            }
            Refresh();
        }
        public void Publish() { lock (gate) if (!disposed) ui.Post(UiMessageTypes.AdvisorsState, state, UiDelivery.State); Refresh(); }
        public void Refresh()
        {
            if (Interlocked.CompareExchange(ref refreshing, 1, 0) != 0) { Interlocked.Exchange(ref refreshQueued, 1); return; }
            _ = Task.Run(RefreshAsync);
        }
        private async Task RefreshAsync()
        {
            try
            {
                var context = capture(); if (context == null) return;
                context.Expires = DateTimeOffset.UtcNow.Add(Deadline).ToUnixTimeMilliseconds();
                using var linked = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token, context.Token);
                linked.CancelAfter(Deadline);
                await operation.WaitAsync(linked.Token).ConfigureAwait(false);
                try
                {
                    Check(context, linked.Token);
                    var result = await send(context, new JObject { ["action"] = "status" }, linked.Token).ConfigureAwait(false);
                    Check(context, linked.Token); AcceptState(context, result);
                }
                finally { operation.Release(); }
            }
            catch { /* Explicit commands report errors; background snapshots never enter the transcript or logs. */ }
            finally { Interlocked.Exchange(ref refreshing, 0); if (Interlocked.Exchange(ref refreshQueued, 0) != 0) Refresh(); }
        }
        private static void Check(AdvisorOperationContext context, CancellationToken token)
        { token.ThrowIfCancellationRequested(); if (!context.IsCurrent()) throw new InvalidOperationException("stale-session"); }

        public async Task HandleAsync(JObject args, CancellationToken token)
        {
            var id = args["requestId"]?.Type == JTokenType.String ? (string?)args["requestId"] : null;
            if (string.IsNullOrEmpty(id) || id!.Length > 128 || id.Any(char.IsControl)) return;
            var reply = new JObject { ["requestId"] = id, ["ok"] = false };
            try
            {
                var command = ValidateCommand(args);
                var context = capture() ?? throw new InvalidOperationException("unsupported");
                context.Expires = DateTimeOffset.UtcNow.Add(Deadline).ToUnixTimeMilliseconds();
                using var linked = CancellationTokenSource.CreateLinkedTokenSource(token, lifetime.Token, context.Token);
                linked.CancelAfter(Deadline);
                await operation.WaitAsync(linked.Token).ConfigureAwait(false);
                try
                {
                    Check(context, linked.Token);
                    if ((string?)command["action"] == "open")
                    {
                        var full = DirectFile((string)command["path"]!);
                        lock (gate)
                        {
                            if (!Matches(context) || !state.Files.Any(file => string.Equals(file.Path, full, StringComparison.OrdinalIgnoreCase))) throw new InvalidOperationException("unsafe-path");
                        }
                        if (!File.Exists(full)) throw new InvalidOperationException("missing-file");
                        Check(context, linked.Token); CheckpointStore.CheckDirectPath(full);
                        if (!await editor.OpenDocumentAsync(full, null, linked.Token).ConfigureAwait(false)) throw new InvalidOperationException("open-failed");
                        Check(context, linked.Token); reply["ok"] = true;
                    }
                    else
                    {
                        WorkspaceTrust.Snapshot? before = null;
                        string? savedPath = null;
                        if ((string?)command["action"] == "save")
                        {
                            lock (gate)
                            {
                                if (!Matches(context) || !selected.TryGetValue((string)command["scope"]!, out savedPath)) throw new InvalidOperationException("reload-required");
                            }
                            savedPath = DirectFile(savedPath!);
                            command["expectedPath"] = savedPath;
                            if ((string?)command["scope"] == "project")
                            {
                                before = trust.Before(Path.GetDirectoryName(savedPath)!, includeExtensions());
                                if (!before.Accepted) throw new InvalidOperationException("untrusted");
                                command["projectTrusted"] = true;
                            }
                        }
                        Check(context, linked.Token);
                        var action = (string)command["action"]!;
                        Dictionary<string, bool>? chosen = null;
                        // A row's switch: the engine applies the conversation's choices; every other command keeps them when it applies the files.
                        if (action == "select") command = SelectCommand(context, (string)command["name"]!, (bool)command["on"]!, out chosen);
                        else command["choices"] = Choices(context.SessionId);
                        var sent = action == "openHistory" ? new JObject { ["action"] = "history", ["name"] = command["name"]!.DeepClone(), ["choices"] = command["choices"]!.DeepClone() } : command;
                        var result = await send(context, sent, linked.Token).ConfigureAwait(false);
                        Check(context, linked.Token);
                        if (result["ok"]?.Type != JTokenType.Boolean || (bool?)result["ok"] != true) throw new InvalidOperationException(ErrorCode(result["error"]));
                        if (chosen != null)
                            lock (gate)
                            {
                                if (!Matches(context)) throw new InvalidOperationException("stale-session");
                                if (choices.Count >= 64 && !choices.ContainsKey(context.SessionId)) choices.Clear();
                                choices[context.SessionId] = chosen;
                            }
                        AcceptState(context, result);
                        if (action == "openHistory")
                        {
                            if (result["history"]?.Type != JTokenType.String || ((string)result["history"]!).Length > MaxHistoryChars) throw new InvalidOperationException("no-history");
                            await OpenHistoryAsync((string)command["name"]!, (string)result["history"]!, linked.Token).ConfigureAwait(false);
                            Check(context, linked.Token);
                        }
                        if ((string?)command["action"] == "load" || (string?)command["action"] == "save")
                        {
                            var path = result["path"]?.Type == JTokenType.String ? DirectFile((string)result["path"]!) : throw new InvalidOperationException("invalid-result");
                            if ((string?)result["scope"] != (string?)command["scope"] || result["hash"]?.Type != JTokenType.String || !Regex.IsMatch((string)result["hash"]!, "^[a-f0-9]{64}$")
                                || result["document"] is not JObject document || Encoding.UTF8.GetByteCount(document.ToString(Formatting.None)) > 256 * 1024) throw new InvalidOperationException("invalid-result");
                            lock (gate) { if (!Matches(context)) throw new InvalidOperationException("stale-session"); selected[(string)command["scope"]!] = path; }
                            if (before != null && result["saved"]?.Type == JTokenType.Boolean && (bool?)result["saved"] == true && string.Equals(path, savedPath, StringComparison.OrdinalIgnoreCase))
                                trust.KeepAfterOwnWrite(before, Path.GetFileName(path));
                        }
                        if ((string?)command["action"] == "history")
                        {
                            // The transcript is shown to the user as data; only its size and type are checked here.
                            if (result["history"] is JToken history && history.Type != JTokenType.Null && (history.Type != JTokenType.String || ((string)history!).Length > MaxHistoryChars)
                                || result["truncated"] != null && result["truncated"]!.Type != JTokenType.Boolean) throw new InvalidOperationException("invalid-result");
                            reply["history"] = result["history"]?.DeepClone() ?? JValue.CreateNull();
                            reply["truncated"] = (bool?)result["truncated"] == true;
                            reply["notes"] = Notes(result["notes"]);
                        }
                        reply["ok"] = true;
                        foreach (var field in new[] { "scope", "path", "exists", "hash", "document", "warnings", "applied", "active" })
                            if (result[field] != null) reply[field] = result[field]!.DeepClone();
                    }
                }
                finally { operation.Release(); }
            }
            catch (OperationCanceledException) { reply["error"] = ErrorMessage("expired"); }
            catch (TimeoutException) { reply["error"] = ErrorMessage("expired"); }
            catch (Exception ex) { reply["error"] = ErrorMessage(ex is InvalidOperationException || ex is ArgumentException ? ex.Message : "advisor-failed"); }
            lock (gate) if (!disposed) ui.Post(UiMessageTypes.AdvisorsReply, reply, UiDelivery.State);
        }
        private bool Matches(AdvisorOperationContext context) => ReferenceEquals(identity, context.Identity) && sessionId == context.SessionId;
        private void AcceptState(AdvisorOperationContext context, JObject result)
        {
            if (result["state"] is not JObject raw) return;
            if ((string?)raw["sessionId"] != context.SessionId || raw["supported"]?.Type != JTokenType.Boolean || raw["enabled"]?.Type != JTokenType.Boolean
                || raw["implicitDefault"]?.Type != JTokenType.Boolean || raw["status"]?.Type != JTokenType.String) throw new InvalidOperationException("invalid-result");
            foreach (var field in new[] { "files", "advisors", "roster" }) if (raw[field] is not JArray) throw new InvalidOperationException("invalid-result");
            var next = raw.ToObject<AdvisorsStatePayload>() ?? throw new InvalidOperationException("invalid-result");
            foreach (var file in next.Files) { if (file.Scope != "project" && file.Scope != "user") throw new InvalidOperationException("invalid-result"); file.Path = DirectFile(file.Path); }
            bool BadCount(double? value) => value != null && (double.IsNaN(value.Value) || double.IsInfinity(value.Value) || value < 0);
            if (next.Advisors.Any(a => a.Name.Length > 200 || a.Status.Length > 64 || a.Model?.Length > 200 || BadCount(a.ContextTokens) || BadCount(a.ContextWindow) || BadCount(a.Cost)) || next.Roster.Any(a => a.Name.Length > 200 || a.Model?.Length > 200 || a.Scope != "project" && a.Scope != "user")) throw new InvalidOperationException("invalid-result");
            // The page knows exactly five states: engine words (running, no_model, disabled, …) never reach it.
            next.Status = NormalizeStatus(next.Status);
            foreach (var advisor in next.Advisors) advisor.Status = NormalizeStatus(advisor.Status);
            // No count limit: every row is sent while the message fits the UI hub (StateBudget). The running statuses take at most half of
            // the room; roster rows past the rest are counted for the page, never silently dropped.
            AdvisorsStatePayload Without(bool advisors, bool roster) => new AdvisorsStatePayload { SessionId = next.SessionId, Status = next.Status, Files = next.Files,
                Advisors = advisors ? next.Advisors : new List<AdvisorStatusPayload>(), Roster = roster ? next.Roster : new List<AdvisorRosterPayload>() };
            var bare = StateBudget.Bytes(new UiMessage(UiMessageTypes.AdvisorsState, Without(false, false))) + 2048;
            StateBudget.Fit(next.Advisors, bare, bare + (StateBudget.Limit - bare) / 2);
            var rosterLeft = StateBudget.Fit(next.Roster, StateBudget.Bytes(new UiMessage(UiMessageTypes.AdvisorsState, Without(true, false))) + 2048);
            if (rosterLeft > 0) next.RosterHidden = rosterLeft;
            next.UpdatedUtc = DateTimeOffset.UtcNow.ToString("O");
            lock (gate) { if (!disposed && Matches(context)) { state = next; ui.Post(UiMessageTypes.AdvisorsState, state, UiDelivery.State); } }
        }
        /// <summary>One of "off", "no-model", "active", "paused", "unknown" for an engine or extension status word; anything else is "unknown".</summary>
        internal static string NormalizeStatus(string? raw)
        {
            switch ((raw ?? string.Empty).Trim().ToLowerInvariant().Replace('_', '-'))
            {
                case "off": case "disabled": case "stopped": return "off";
                case "no-model": return "no-model";
                case "active": case "running": return "active";
                case "paused": case "idle": case "waiting": case "suspended": return "paused";
                default: return "unknown";
            }
        }
        internal static string DirectFile(string path)
        {
            if (string.IsNullOrWhiteSpace(path) || path.Any(char.IsControl) || !Path.IsPathRooted(path) || path.StartsWith(@"\\", StringComparison.Ordinal) || path.StartsWith("//", StringComparison.Ordinal)
                || path.Skip(2).Contains(':')) throw new InvalidOperationException("unsafe-path");
            var full = Path.GetFullPath(path);
            var name = Path.GetFileName(full);
            if (!string.Equals(name, "WATCHDOG.yml", StringComparison.OrdinalIgnoreCase) && !string.Equals(name, "WATCHDOG.yaml", StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException("unsafe-path");
            CheckpointStore.CheckDirectPath(full); return full;
        }
        internal static JObject ValidateCommand(JObject args)
        {
            var action = Text(args, "action", 16, false);
            if (!new[] { "load", "save", "enable", "disable", "status", "history", "open", "select", "openHistory" }.Contains(action)) throw new ArgumentException("invalid-action");
            var result = new JObject { ["action"] = action };
            if (action == "open") { result["path"] = Text(args, "path", 32768, false); return result; }
            if (action == "select" || action == "history" || action == "openHistory")
            {
                var name = Text(args, "name", 200, false)?.Trim();
                if (!string.IsNullOrEmpty(name)) result["name"] = name;
                else if (action != "history") throw new ArgumentException("invalid-name");
                if (action == "select") { if (args["on"]?.Type != JTokenType.Boolean) throw new ArgumentException("invalid-document"); result["on"] = args["on"]!.DeepClone(); }
                return result;
            }
            if (action == "load" || action == "save")
            {
                var scope = Text(args, "scope", 10, false); if (scope != "project" && scope != "user") throw new ArgumentException("invalid-scope"); result["scope"] = scope;
            }
            if (action == "save")
            {
                var hash = Text(args, "baseHash", 64, false); if (!Regex.IsMatch(hash!, "^[a-f0-9]{64}$")) throw new ArgumentException("invalid-hash");
                result["baseHash"] = hash;
                result["document"] = ValidateDocument(args["document"] as JObject ?? throw new ArgumentException("invalid-document"));
            }
            return result;
        }
        internal static JObject ValidateDocument(JObject document)
        {
            if (Encoding.UTF8.GetByteCount(document.ToString(Formatting.None)) > 256 * 1024 || document["advisors"] is not JArray advisors) throw new ArgumentException("invalid-document");
            var result = new JObject { ["advisors"] = new JArray() };
            CopyText(document, result, "instructions", 16000, true); CopyInteger(document, result, "maxNotesPerUpdate", 32);
            var names = new HashSet<string>(StringComparer.Ordinal);
            foreach (var value in advisors)
            {
                if (value is not JObject entry) throw new ArgumentException("invalid-document");
                var name = Text(entry, "name", 256 * 1024, false)?.Trim(); if (string.IsNullOrEmpty(name) || name!.Length > 64) throw new ArgumentException("invalid-name");
                var slug = Regex.Replace(name!.ToLowerInvariant(), "[^a-z0-9]+", "-").Trim('-'); if (!names.Add(slug.Length == 0 ? "advisor" : slug)) throw new ArgumentException("duplicate-name");
                var row = new JObject { ["name"] = name };
                CopyText(entry, row, "model", 200, false); CopyText(entry, row, "instructions", 16000, true);
                CopyInteger(entry, row, "reviewInterval", 1000000); CopyInteger(entry, row, "maxNotesPerUpdate", 32);
                if (entry["enabled"] != null && entry["enabled"]!.Type != JTokenType.Null) { if (entry["enabled"]!.Type != JTokenType.Boolean) throw new ArgumentException("invalid-document"); row["enabled"] = entry["enabled"]!.DeepClone(); }
                foreach (var key in new[] { "reviewMode", "syncBacklog" })
                {
                    var v = Text(entry, key, 16, false); if (v == null) continue;
                    if (!(key == "reviewMode" ? new[] { "turn", "agent-end" } : new[] { "off", "1", "3", "5", "strict" }).Contains(v)) throw new ArgumentException("invalid-document"); row[key] = v;
                }
                if (entry["tools"] != null && entry["tools"]!.Type != JTokenType.Null)
                {
                    if (entry["tools"] is not JArray tools || tools.Count > Tools.Count || tools.Any(t => t.Type != JTokenType.String || !Tools.Contains((string)t!))) throw new ArgumentException("invalid-tools");
                    row["tools"] = new JArray(tools.Select(t => (string)t!).Distinct(StringComparer.Ordinal));
                }
                ((JArray)result["advisors"]!).Add(row);
            }
            return result;
        }
        private static string? Text(JObject source, string key, int max, bool multiline)
        {
            var value = source[key]; if (value == null || value.Type == JTokenType.Null) return null;
            if (value.Type != JTokenType.String) throw new ArgumentException("invalid-document");
            var text = (string)value!; if (text.Length > max || !multiline && text.Any(char.IsControl)) throw new ArgumentException("invalid-document"); return text;
        }
        private static void CopyText(JObject source, JObject target, string key, int max, bool multiline) { var value = Text(source, key, max, multiline); if (value != null) target[key] = value; }
        private static void CopyInteger(JObject source, JObject target, string key, int max)
        {
            var value = source[key]; if (value == null || value.Type == JTokenType.Null) return;
            if (value.Type != JTokenType.Integer || !long.TryParse(value.ToString(), out var n) || n < 1 || n > max) throw new ArgumentException("invalid-document"); target[key] = n;
        }
        private static string ErrorCode(JToken? value) => value?.Type == JTokenType.String ? (string)value! : "advisor-failed";
        private static string ErrorMessage(string code) => code switch
        {
            "conflict" => PompLanguage.Pick("조언자 파일이 바뀌었어요.", "The advisor file changed."),
            "unknown-advisor" => PompLanguage.Pick("조언자를 찾을 수 없어요.", "The advisor was not found."),
            "no-history" => PompLanguage.Pick("이 조언자의 기록이 없어요.", "This advisor has no transcript."),
            "untrusted" => PompLanguage.Pick("작업 영역이 신뢰되지 않았어요.", "The workspace is not trusted."),
            "expired" => PompLanguage.Pick("조언자 요청 시간이 초과됐어요.", "The advisor request timed out."),
            "stale-session" => PompLanguage.Pick("대화가 바뀌었어요.", "The conversation changed."),
            "unsupported" => PompLanguage.Pick("조언자 설정을 사용할 수 없어요.", "Advisor settings are unavailable."),
            "reload-required" => PompLanguage.Pick("조언자 파일을 먼저 불러와야 해요.", "The advisor file must be loaded first."),
            "unsafe-path" or "missing-file" => PompLanguage.Pick("조언자 파일 경로가 올바르지 않아요.", "Invalid advisor file path."),
            "duplicate-name" => PompLanguage.Pick("조언자 이름이 중복됐어요.", "Duplicate advisor names."),
            "invalid-document" or "invalid-name" or "invalid-tools" or "invalid-hash" or "invalid-scope" or "invalid-action" or "document-too-large" => PompLanguage.Pick("조언자 설정이 올바르지 않아요.", "Invalid advisor settings."),
            "open-failed" => PompLanguage.Pick("조언자 파일을 열지 못했어요.", "Could not open the advisor file."),
            _ => PompLanguage.Pick("조언자 요청을 처리하지 못했어요.", "Could not complete the advisor request."),
        };
        public void Dispose() { lock (gate) { if (disposed) return; disposed = true; } lifetime.Cancel(); }
    }
}
