using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Pomp.Hosting;
using Pomp.Ui;

namespace Pomp.Features
{
    internal sealed class SkillCatalog
    {
        private readonly string root, home;
        private static readonly Regex Name = new Regex(@"\A[a-zA-Z0-9][a-zA-Z0-9._-]{0,63}\z", RegexOptions.CultureInvariant);
        /// <summary>Names from the engine (its skill command catalog): namespaces use one '/', plugin rows may hold a ':'.</summary>
        private static readonly Regex EngineName = new Regex(@"\A[a-zA-Z0-9][a-zA-Z0-9._:/-]{0,127}\z", RegexOptions.CultureInvariant);
        private static readonly Regex NewName = new Regex(@"\A[a-z0-9-]{2,40}\z", RegexOptions.CultureInvariant);
        internal const int MaxBytes = 32768;
        /// <summary>Directory entries inspected per scope: a guard against a runaway tree (thousands of skills fit far below it), not a list limit.</summary>
        private const int MaxEntries = 200000;
        internal SkillCatalog(string root, string? home) { this.root = Path.GetFullPath(root); this.home = ToolResourceFiles.Home(home); }
        internal string DirectoryFor(string scope) => Path.Combine(ToolResourceFiles.Scope(scope, root, home), ".pomp", "skills");
        /// <summary>The SKILL.md files of one scope in walk order, and whether the entry guard cut the walk short.</summary>
        private sealed class Scan
        {
            internal readonly List<(string File, string Folder, int Depth)> Files = new List<(string File, string Folder, int Depth)>();
            internal bool Truncated;
        }
        private Scan ScanScope(string scope, Action<string> diagnostic)
        {
            var scan = new Scan(); var visited = 0;
            void Walk(string folder, int depth)
            {
                if (depth > 3 || scan.Truncated) return;
                string[] entries;
                try
                {
                    // The scope folder is checked in full; a deeper folder was checked as an entry of its parent just before.
                    if (depth == 0) CheckpointStore.CheckDirectPath(folder);
                    if (!Directory.Exists(folder)) return;
                    entries = Directory.EnumerateFileSystemEntries(folder).Take(MaxEntries + 1).OrderBy(p => p, StringComparer.Ordinal).ToArray();
                }
                catch (Exception ex) { diagnostic(scope + ": " + ex.Message); return; }
                foreach (var entry in entries)
                {
                    if (++visited > MaxEntries) { scan.Truncated = true; return; }
                    try
                    {
                        ToolResourceFiles.CheckNotLinked(entry);
                        if (Directory.Exists(entry)) { Walk(entry, depth + 1); continue; }
                        if (Path.GetFileName(entry) != "SKILL.md") continue;
                        scan.Files.Add((entry, folder, depth));
                    }
                    catch (Exception ex) { diagnostic(Path.GetFileName(entry) + ": " + ex.Message); }
                    if (scan.Truncated) return;
                }
            }

            Walk(DirectoryFor(scope), 0);
            return scan;
        }
        internal SkillsStatePayload View(JArray commands, IEnumerable<SkillInfoPayload>? installed = null, IReadOnlyList<PompCatalog.BuiltinSkill>? builtins = null, ISet<string>? switchedOff = null, ISet<string>? configDisabled = null)
        {
            lock (ToolResourceFiles.Gate)
            {
                var result = new SkillsStatePayload(); var engine = commands.OfType<JObject>().Where(c => ((string?)c["name"])?.StartsWith("skill:", StringComparison.Ordinal) == true)
                    .ToDictionary(c => ((string)c["name"]!).Substring(6), c => (string?)c["description"] ?? "", StringComparer.Ordinal);
                bool Off(string name) => switchedOff != null && (switchedOff.Contains(name) || (configDisabled?.Contains(name) == true));
                var loaded = new HashSet<string>(StringComparer.Ordinal); var named = new HashSet<string>(StringComparer.Ordinal);
                var scans = new[] { "project", "user" }.Select(which => (Scope: which, Scan: ScanScope(which, Diagnostic))).ToArray();
                foreach (var (scope, scan) in scans)
                {
                    if (scan.Truncated) Diagnostic(PompLanguage.Pick(ToolResourceFiles.ScopeName(scope) + " 스킬 폴더 항목이 너무 많아요.", "The " + ToolResourceFiles.ScopeName(scope) + " skills folder has too many entries."));
                }

                foreach (var (scope, scan) in scans)
                    foreach (var found in scan.Files)
                    {
                        var folderName = Path.GetFileName(found.Folder);
                        try
                        {
                            var probe = ToolResourceFiles.Examine(found.File, MaxBytes);
                            if (probe.Unreadable) { result.Skills.Add(Unlisted(scope, Fallback(scope, folderName, found.Folder), found.File, probe.Hash, probe.Problem!)); continue; }
                            string name, description;
                            try { (name, description) = Metadata(probe.Text, folderName); }
                            catch (Exception ex) when (ex is ArgumentException || ex is JsonException)
                            {
                                // Not a skill the catalog can edit (it may still be one the engine loads): open or delete it, never silently hidden.
                                result.Skills.Add(Unlisted(scope, Fallback(scope, folderName, found.Folder), found.File, probe.Hash, FixOrDelete(ex is JsonException ? PompLanguage.Pick("스킬 메타데이터를 읽지 못했어요.", "The skill metadata could not be read.") : ex.Message)));
                                continue;
                            }

                            // Rows are addressed by scope and name: a second file declaring a name that is already taken could never be opened or deleted from the page.
                            if (!named.Add(scope + "\n" + name)) { result.Skills.Add(Unlisted(scope, Fallback(scope, folderName, found.Folder), found.File, probe.Hash, FixOrDelete(PompLanguage.Pick("같은 이름의 스킬이 이미 있어요: " + name + ".", "Another skill already has this name: " + name + ".")))); continue; }
                            var invocable = found.Depth == 1 && description.Length > 0 && engine.ContainsKey(name) && loaded.Add(name);
                            var row = new SkillInfoPayload { Name = name, Description = description, Source = scope, File = found.File, Hash = probe.Hash, Invocable = invocable,
                                // A row that cannot be used says so on the page (no explanatory note).
                                Note = null,
                                Enabled = !Off(name) };
                            result.Skills.Add(row);
                        }
                        catch (Exception ex) { Diagnostic(folderName + ": " + ex.Message); }
                    }

                var others = new List<SkillInfoPayload>();
                // Skills built into POMP: listed even before the engine reports them (the engine discovers them from
                // the overlay's catalog directory). Read-only: built into POMP, switched on and off only.
                if (builtins != null) foreach (var builtin in builtins)
                {
                    if (loaded.Contains(builtin.Id)) continue;
                    others.Add(new SkillInfoPayload { Name = builtin.Id, Description = builtin.Description, Source = "pomp",
                        File = builtin.File, Hash = null, Invocable = engine.ContainsKey(builtin.Id) && loaded.Add(builtin.Id),
                        Enabled = !Off(builtin.Id), CatalogId = builtin.Id, DefaultEnabled = builtin.DefaultEnabled, Category = builtin.Category });
                }
                foreach (var item in engine.OrderBy(p => p.Key, StringComparer.Ordinal))
                    if (!loaded.Contains(item.Key)) others.Add(new SkillInfoPayload { Name = item.Key, Description = item.Value, Source = "omp", Invocable = true, Enabled = !Off(item.Key) });
                if (installed != null) foreach (var row in installed) { row.Enabled = !Off(row.Name); others.Add(row); }
                // Switched-off skills vanish from the engine's command list: keep them as rows so the page can switch them back on.
                if (switchedOff != null) foreach (var name in switchedOff.OrderBy(n => n, StringComparer.Ordinal))
                {
                    if (!EngineName.IsMatch(name) || engine.ContainsKey(name) || others.Any(s => s.Name == name) || result.Skills.Any(s => s.Name == name)) continue;
                    others.Add(new SkillInfoPayload { Name = name, Source = "omp", Invocable = false, Enabled = false });
                }
                // The user's own files come first, so a list too large for one message drops the engine's and the packages' rows before any of them.
                others = others.OrderBy(s => s.Name, StringComparer.Ordinal).ThenBy(s => s.Source, StringComparer.Ordinal).ToList();
                result.Skills = result.Skills.OrderBy(s => s.Name, StringComparer.Ordinal).ThenBy(s => s.Source, StringComparer.Ordinal).Concat(others).ToList();
                // No count limit: every row is sent while the message fits the UI hub (StateBudget); the rest are counted, never silently dropped.
                var left = StateBudget.Fit(result.Skills, StateBudget.Bytes(new UiMessage(UiMessageTypes.SkillsState, new SkillsStatePayload { Diagnostics = result.Diagnostics })) + 2048);
                if (left > 0) { result.Diagnostics.Insert(0, PompLanguage.Pick(left + "개 스킬은 목록이 너무 커서 표시하지 않아요.", left + " skills are not shown: the list is too large.")); if (result.Diagnostics.Count > 20) result.Diagnostics.RemoveRange(20, result.Diagnostics.Count - 20); }
                result.Skills = result.Skills.OrderBy(s => s.Name, StringComparer.Ordinal).ThenBy(s => s.Source, StringComparer.Ordinal).ToList();
                return result;
                // A row the page cannot edit is listed under its folder name; when that name is taken it gets its own path, which no declared name can equal.
                string Fallback(string scope, string folderName, string folder)
                {
                    if (named.Add(scope + "\n" + folderName)) return folderName;
                    var relative = folder.Substring(DirectoryFor(scope).Length).Trim('\\', '/').Replace('\\', '/');
                    var path = (relative.Length == 0 ? "" : relative + "/") + "SKILL.md";
                    named.Add(scope + "\n" + path);
                    return path;
                }
                void Diagnostic(string message) { if (result.Diagnostics.Count < 20) result.Diagnostics.Add(message.Substring(0, Math.Min(500, message.Length))); }
            }
        }
        /// <summary>A SKILL.md the editor cannot load (too large, not UTF-8, not a skill the catalog reads, or a name that is already taken): listed under its folder name to open or delete.</summary>
        private static SkillInfoPayload Unlisted(string scope, string folderName, string file, string? hash, string note) =>
            new SkillInfoPayload { Name = folderName, Source = scope, File = file, Hash = hash, Invocable = false, Unreadable = true, Note = note };
        private static string FixOrDelete(string reason) => reason;
        private sealed class ParsedSkill
        {
            internal string Name = "";
            internal string Description = "";
            internal string Body = "";
            /// <summary>False when the frontmatter holds anything the in-page form would drop (another key, a comment, a continuation line).</summary>
            internal bool FormOk = true;
        }
        private static (string, string) Metadata(string text, string fallback)
        {
            var parsed = Parse(text, fallback);
            return (parsed.Name, parsed.Description);
        }
        /// <summary>The frontmatter name and description of a SKILL.md text; false when it does not parse as a skill (built-in rows keep their catalog id only on a match).</summary>
        internal static bool TrySkillIdentity(string text, out string name, out string description)
        {
            try { (name, description) = Metadata(text, ""); return true; }
            catch (Exception ex) when (ex is ArgumentException || ex is JsonException) { name = ""; description = ""; return false; }
        }
        private static ParsedSkill Parse(string text, string fallback)
        {
            if (text.IndexOf('\0') >= 0) throw new ArgumentException(PompLanguage.Pick("올바른 Markdown 파일이 아니에요.", "Not an ordinary Markdown file."));
            var normalized = text.Replace("\r\n", "\n");
            var match = Regex.Match(normalized, @"\A---\n([\s\S]*?)\n---(?:\n|$)");
            if (text.StartsWith("---", StringComparison.Ordinal) && !match.Success) throw new ArgumentException(PompLanguage.Pick("프런트매터가 닫히지 않았어요.", "Unclosed frontmatter."));
            var result = new ParsedSkill { Body = match.Success ? normalized.Substring(match.Length).Trim() : normalized.Trim() };
            if (result.Body.Length == 0 || result.Body.Length > 24000) throw new ArgumentException(PompLanguage.Pick("스킬 본문이 올바르지 않아요.", "The skill body is invalid."));
            // Without frontmatter the form would invent one: text only.
            if (!match.Success) { result.FormOk = false; result.Name = fallback; RequireValid(result); return result; }
            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (var line in match.Groups[1].Value.Split('\n'))
            {
                if (line.Length == 0) continue;
                if (char.IsWhiteSpace(line[0]) || line[0] == '#') { result.FormOk = false; continue; }
                var colon = line.IndexOf(':'); if (colon < 1) { result.FormOk = false; continue; }
                var key = line.Substring(0, colon).Trim(); if (key != "name" && key != "description") { result.FormOk = false; continue; }
                if (!seen.Add(key)) throw new ArgumentException(PompLanguage.Pick("스킬 메타데이터 키가 중복돼요.", "Duplicate skill metadata key."));
                var value = line.Substring(colon + 1).Trim();
                if (value.StartsWith("\"", StringComparison.Ordinal)) value = JsonConvert.DeserializeObject<string>(value) ?? "";
                else if (value.StartsWith("'", StringComparison.Ordinal) && value.EndsWith("'", StringComparison.Ordinal) && value.Length >= 2) value = value.Substring(1, value.Length - 2).Replace("''", "'");
                else if (value.Length > 0 && "*&!|>[{".Contains(value[0])) throw new ArgumentException(PompLanguage.Pick("편집기에서 바꿀 수 없는 메타데이터예요.", "The metadata cannot be edited here."));
                if (key == "name") result.Name = value; else result.Description = value;
            }
            if (result.Name.Length == 0) result.Name = fallback;
            RequireValid(result);
            return result;
        }
        private static void RequireValid(ParsedSkill result)
        {
            if (!Name.IsMatch(result.Name) || result.Description.Length > 500 || result.Description.Any(char.IsControl)) throw new ArgumentException(PompLanguage.Pick("스킬 이름이나 설명이 올바르지 않아요.", "Invalid skill name or description."));
        }
        internal string Create(string scope, string name, string description, string? body = null)
        {
            if (!NewName.IsMatch(name) || description.Length is < 1 or > 500 || description.Any(char.IsControl)) throw new ArgumentException(PompLanguage.Pick("이름이나 설명이 올바르지 않아요.", "The name or description is invalid."));
            return WriteNewSkill(scope, name, Encoded(Compose(name, description, body)));
        }
        /// <summary>Frontmatter exactly as <see cref="Create"/> writes it; an empty body gets the starter text. Refuses what <see cref="Parse"/> would refuse in the file.</summary>
        internal static string Compose(string name, string description, string? body)
        {
            body = (body ?? "").Trim();
            if (body.Length == 0) body = "# " + name + "\n\nDescribe the steps and expected result here. This skill does not grant tool permissions.";
            if (body.Length > 24000) throw new ArgumentException(PompLanguage.Pick("스킬 본문이 올바르지 않아요.", "The skill body is invalid."));
            // The reader refuses a NUL anywhere in the file; a skill that could be written but never read again must not be written.
            if (body.IndexOf('\0') >= 0 || description.IndexOf('\0') >= 0 || name.IndexOf('\0') >= 0) throw new ArgumentException(PompLanguage.Pick("올바른 Markdown 파일이 아니에요.", "Not an ordinary Markdown file."));
            return "---\nname: " + JsonConvert.SerializeObject(name) + "\ndescription: " + JsonConvert.SerializeObject(description) + "\n---\n\n" + body + "\n";
        }
        /// <summary>Strict UTF-8 of at most <see cref="MaxBytes"/> bytes (the limit the listing and the editor read with), else refused with both sizes.</summary>
        private static byte[] Encoded(string text) => ToolResourceFiles.Encode(text, MaxBytes, "스킬 파일", "skill file");
        internal string Import(string scope, string name, string content)
        {
            if (!NewName.IsMatch(name)) throw new ArgumentException(PompLanguage.Pick("이름이 올바르지 않아요.", "The name is invalid."));
            var bytes = ToolResourceFiles.DecodeImport(content, MaxBytes, "스킬 파일", "skill file");
            var (declaredName, description) = Metadata(ToolResourceFiles.Text(bytes), "");
            if (declaredName != name || description.Length == 0) throw new ArgumentException(PompLanguage.Pick("프런트매터의 name이나 description이 올바르지 않아요.", "The frontmatter name or description is invalid."));
            return WriteNewSkill(scope, name, bytes);
        }
        private string WriteNewSkill(string scope, string name, byte[] bytes)
        {
            lock (ToolResourceFiles.Gate)
            {
                RequireRoom(scope);
                var file = Path.Combine(DirectoryFor(scope), name, "SKILL.md");
                ToolResourceFiles.WriteNew(file, bytes);
                return file;
            }
        }
        /// <summary>A new skill must show up in the list: refuse when the walk cannot see all of the scope.</summary>
        private void RequireRoom(string scope)
        {
            var scan = ScanScope(scope, _ => { });
            if (scan.Truncated)
                throw new InvalidOperationException(PompLanguage.Pick(ToolResourceFiles.ScopeName(scope) + " 스킬 폴더 항목이 너무 많아요.", "The " + ToolResourceFiles.ScopeName(scope) + " skills folder has too many entries."));
        }
        /// <summary>
        /// The one listed project/user row with this name: resolved from the catalog (a folder may differ from its
        /// declared name), never from a page-supplied path.
        /// </summary>
        private SkillInfoPayload RowOf(string scope, string name)
        {
            RequireWritableScope(scope);
            var rows = View(new JArray()).Skills.Where(s => s.Source == scope && s.Name == name && s.File != null).ToArray();
            if (rows.Length != 1) throw new InvalidOperationException(PompLanguage.Pick("스킬이 바뀌었거나 모호해요.", "Skill changed or is ambiguous."));
            CheckpointStore.CheckDirectPath(rows[0].File!);
            return rows[0];
        }
        /// <summary>The SKILL.md of the one listed project/user row with this name (also a row the editor cannot load: it can still be opened).</summary>
        internal string FileOf(string scope, string name) => RowOf(scope, name).File!;
        /// <summary>One listed project/user row as <see cref="ResourceDocumentPayload"/>; form only when the frontmatter round-trips.</summary>
        internal ResourceDocumentPayload Document(string scope, string name)
        {
            var row = RowOf(scope, name);
            if (row.Unreadable) throw new InvalidOperationException(row.Note);
            return DocumentOf(row.File!, name);
        }
        /// <summary>A verified skill file (also an installed one, for copying) for the editor; its declared name must still be <paramref name="name"/>.</summary>
        internal static ResourceDocumentPayload DocumentOf(string file, string name)
        {
            var probe = ToolResourceFiles.Examine(file, MaxBytes);
            if (probe.Unreadable) throw new InvalidOperationException(probe.Problem);
            var parsed = Parse(probe.Text, Path.GetFileName(Path.GetDirectoryName(file)) ?? "");
            if (parsed.Name != name || parsed.Description.Length == 0) throw new InvalidOperationException(PompLanguage.Pick("스킬 메타데이터가 목록과 맞지 않아요.", "Skill metadata no longer matches the list."));
            var document = new ResourceDocumentPayload { Content = probe.Text, Hash = probe.Hash! };
            if (parsed.FormOk) document.Form = new JObject { ["description"] = parsed.Description, ["body"] = parsed.Body };
            return document;
        }
        /// <summary>Form fields (written as <see cref="Create"/> writes them) or the whole text (validated as <see cref="Import"/>).</summary>
        internal void Update(string scope, string name, string? expectedHash, string? description, string? body, string? content)
        {
            RequireWritableScope(scope);
            byte[] bytes;
            if (content != null)
            {
                bytes = ToolResourceFiles.DecodeImport(content, MaxBytes, "스킬 파일", "skill file");
                var (declaredName, declaredDescription) = Metadata(ToolResourceFiles.Text(bytes), "");
                if (declaredName != name || declaredDescription.Length == 0) throw new ArgumentException(PompLanguage.Pick("프런트매터의 name이나 description이 올바르지 않아요.", "The frontmatter name or description is invalid."));
            }
            else
            {
                if (description == null || body == null) throw new ArgumentException(PompLanguage.Pick("스킬 설명이나 본문이 비어 있어요.", "The skill description or body is empty."));
                if (description.Length is < 1 or > 500 || description.Any(char.IsControl)) throw new ArgumentException(PompLanguage.Pick("설명이 올바르지 않아요.", "The description is invalid."));
                bytes = Encoded(Compose(name, description, body));
            }

            var row = RowOf(scope, name);
            if (row.Unreadable) throw new InvalidOperationException(row.Note);
            ToolResourceFiles.Replace(row.File!, bytes, expectedHash, MaxBytes);
        }
        private static void RequireWritableScope(string scope)
        {
            if (scope != "project" && scope != "user") throw new ArgumentException(PompLanguage.Pick("이 스킬은 편집할 수 없어요.", "This skill cannot be edited."));
        }
        internal void Delete(string scope, string name, string? expectedHash)
        {
            lock (ToolResourceFiles.Gate)
            {
                var candidates = View(new JArray()).Skills.Where(s => s.Source == scope && s.Name == name).ToArray();
                var changed = PompLanguage.Pick("스킬이 바뀌었거나 모호해요.", "Skill changed or is ambiguous.");
                if (candidates.Length != 1 || string.IsNullOrEmpty(expectedHash) || candidates[0].Hash != expectedHash) throw new InvalidOperationException(changed);
                var file = candidates[0].File!;
                ToolResourceFiles.DeleteIfHash(file, expectedHash, changed);
                var folder = Path.GetDirectoryName(file)!;
                if (!Directory.EnumerateFileSystemEntries(folder).Any()) Directory.Delete(folder);
            }
        }
    }
}
