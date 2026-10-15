using System;
using System.Collections.Generic;
using System.Globalization;
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
    /// <summary>
    /// The top-level rule files the engine extension reads (lib/project-rules.ts): <c>&lt;workspace&gt;/.pomp/rules/*.mdc</c> and
    /// <c>~/.pomp/rules/*.mdc</c>. Folder rules deeper in the workspace and <c>&lt;file&gt;.mdc</c> companions stay plain files.
    /// The extension's YAML parser decides what applies; this catalog lists, creates, imports and deletes files, and reads only
    /// the small YAML subset whose meaning is identical here and in that parser (everything else is edited as text).
    /// </summary>
    internal sealed class RuleCatalog
    {
        internal const int MaxBytes = 32768;
        private const int MaxGlobs = 32;
        private readonly string root, home;
        private static readonly Regex NewName = new Regex(@"\A[a-z0-9-]{2,40}\z", RegexOptions.CultureInvariant);
        private static readonly Regex Frontmatter = new Regex(@"\A---\n([\s\S]*?)\n---(?:\n|$)", RegexOptions.CultureInvariant);
        private static readonly Regex BlockItem = new Regex(@"\A( +)- (.*)\z", RegexOptions.CultureInvariant);
        // What the YAML core schema reads as null, a boolean or a number rather than text (numbers generously: any number-like token).
        private static readonly Regex NotText = new Regex(@"\A(?:~|[Nn]ull|NULL|[Tt]rue|TRUE|[Ff]alse|FALSE|[-+]?\.?[0-9][0-9A-Za-z_.+-]*|[-+]?\.(?:[Ii]nf|INF|[Nn]a[Nn]|NAN))\z", RegexOptions.CultureInvariant);
        private const string PlainStarts = "-?:,[]{}#&*!|>'\"%@`";

        internal RuleCatalog(string root, string? home) { this.root = Path.GetFullPath(root); this.home = ToolResourceFiles.Home(home); }

        internal string DirectoryFor(string scope) => Path.Combine(ToolResourceFiles.Scope(scope, root, home), ".pomp", "rules");

        internal RulesStatePayload View()
        {
            lock (ToolResourceFiles.Gate)
            {
                var result = new RulesStatePayload();
                foreach (var scope in new[] { "project", "user" })
                {
                    var folder = DirectoryFor(scope);
                    try
                    {
                        CheckpointStore.CheckDirectPath(folder);
                        if (!Directory.Exists(folder)) continue;
                        var files = FilesIn(folder);
                        foreach (var file in files)
                        {
                            try
                            {
                                CheckpointStore.CheckDirectPath(file);
                                var name = Path.GetFileNameWithoutExtension(file);
                                if (name.Length == 0) { Diagnostic(Path.GetFileName(file) + ": " + PompLanguage.Pick("이름이 없는 파일은 표시하지 않아요.", "A file without a name is not shown.")); continue; }
                                var probe = ToolResourceFiles.Examine(file, MaxBytes);
                                var rule = new RuleInfoPayload { Name = name, Scope = scope, File = file, Hash = probe.Hash ?? "" };
                                if (probe.Unreadable) { rule.Unreadable = true; rule.Note = probe.Problem; }
                                else Describe(rule, probe.Text);
                                result.Rules.Add(rule);
                            }
                            catch (Exception ex) { Diagnostic(Path.GetFileName(file) + ": " + ex.Message); }
                        }
                    }
                    catch (Exception ex) { Diagnostic(scope + ": " + ex.Message); }
                }

                // No count limit: every row is sent while the message fits the UI hub (StateBudget); the rest are counted, never silently dropped.
                var left = StateBudget.Fit(result.Rules, StateBudget.Bytes(new UiMessage(UiMessageTypes.RulesState, new RulesStatePayload { Diagnostics = result.Diagnostics })) + 2048);
                if (left > 0) { result.Diagnostics.Insert(0, PompLanguage.Pick(left + "개 규칙은 목록이 너무 커서 표시하지 않아요.", left + " rules are not shown: the list is too large.")); if (result.Diagnostics.Count > 20) result.Diagnostics.RemoveRange(20, result.Diagnostics.Count - 20); }
                return result;
                void Diagnostic(string message) { if (result.Diagnostics.Count < 20) result.Diagnostics.Add(message.Substring(0, Math.Min(500, message.Length))); }
            }
        }

        /// <summary>The <c>*.mdc</c> files of a scope folder in listing order (the extension is checked exactly: a legacy short-name match such as <c>x.mdcx</c> is no rule).</summary>
        private static string[] FilesIn(string folder) => Directory.EnumerateFiles(folder, "*.mdc").Where(f => string.Equals(Path.GetExtension(f), ".mdc", StringComparison.OrdinalIgnoreCase)).OrderBy(p => p, StringComparer.Ordinal).ToArray();

        /// <summary>
        /// Reads description, alwaysApply and globs, and says whether the in-page form may be offered: only when every frontmatter line is one the form
        /// edits and its reading here is guaranteed to equal the engine's YAML reading (anything with comments, escapes, indicators, block scalars,
        /// continuation lines, duplicate keys, tabs, ... is the engine's and edited as text).
        /// </summary>
        private static bool Describe(RuleInfoPayload rule, string text)
        {
            text = text.Replace("\r\n", "\n");
            if (text.IndexOf('\0') >= 0) { rule.Note = PompLanguage.Pick("올바른 Markdown 파일이 아니에요.", "Not an ordinary Markdown file."); return false; }
            var match = Frontmatter.Match(text);
            if (text.StartsWith("---\n", StringComparison.Ordinal) && !match.Success) { rule.Note = PompLanguage.Pick("머리말(---)이 닫히지 않았어요.", "Unclosed frontmatter."); return false; }
            // An empty body is worth a note but not a reason to refuse the form: the form is how the user fills it in.
            var emptyBody = (match.Success ? text.Substring(match.Length) : text).Trim().Length == 0;
            if (emptyBody) rule.Note = EmptyBodyNote();
            if (!match.Success) return true;
            var form = true;
            void Engine() { rule.Note ??= EngineNote(); form = false; }
            var content = match.Groups[1].Value;
            // A tab, a lone CR or another control character makes even the line splitting uncertain.
            if (content.Any(c => c != '\n' && char.IsControl(c))) { Engine(); return false; }
            var lines = content.Split('\n'); var seen = new HashSet<string>(StringComparer.Ordinal);
            // Undoes the last recognised key when an indented line follows it: YAML continues that value there, so what was read is not what the engine reads.
            Action? revoke = null;
            for (var index = 0; index < lines.Length; index++)
            {
                var line = lines[index];
                if (line.Length == 0) continue;
                if (line[0] == ' ' && revoke != null) { revoke(); revoke = null; Engine(); continue; }
                // Comments, indented lines and other keys are not the form's: rewriting would drop or move them.
                if (line[0] == ' ' || line[0] == '#') { form = false; continue; }
                revoke = null;
                var colon = line.IndexOf(':');
                if (colon < 1) { form = false; continue; }
                var key = line.Substring(0, colon);
                if (key != "description" && key != "alwaysApply" && key != "globs") { form = false; continue; }
                var rest = line.Substring(colon + 1);
                if ((rest.Length > 0 && rest[0] != ' ') || !seen.Add(key)) { Engine(); continue; }
                var value = rest.Trim(' ');
                if (key == "description")
                {
                    if (!TryScalar(value, out var description)) { Engine(); continue; }
                    rule.Description = description; revoke = () => rule.Description = "";
                    if (description.Length > 500 || description.Any(char.IsControl)) form = false;
                }
                else if (key == "alwaysApply")
                {
                    if (value != "true" && value != "false") Engine(); else { rule.AlwaysApply = value == "true"; revoke = () => rule.AlwaysApply = false; }
                }
                else
                {
                    var globs = new List<string>(); var first = rule.Globs.Count;
                    if (value.Length == 0)
                    {
                        // A block list: items of one indentation, each a scalar of its own.
                        var indent = -1;
                        while (index + 1 < lines.Length)
                        {
                            var item = BlockItem.Match(lines[index + 1]);
                            if (!item.Success || (indent >= 0 && item.Groups[1].Length != indent)) break;
                            indent = item.Groups[1].Length; index++;
                            if (TryScalar(item.Groups[2].Value.Trim(' '), out var glob)) globs.Add(glob); else Engine();
                        }

                        if (indent < 0) Engine();
                    }
                    else if (value[0] == '[') { if (!TryFlowList(value, globs)) Engine(); }
                    else if (TryScalar(value, out var single)) globs.Add(single);
                    else Engine();
                    foreach (var glob in globs)
                    {
                        if (rule.Globs.Count >= MaxGlobs) { Engine(); break; }
                        rule.Globs.Add(glob);
                        // The form writes globs trimmed, one line of up to 200 characters, and never an empty one.
                        if (glob.Length == 0 || glob.Length > 200 || glob.Trim() != glob || glob.Any(char.IsControl)) form = false;
                    }

                    revoke = () => rule.Globs.RemoveRange(first, rule.Globs.Count - first);
                }
            }

            return form && (rule.Note == null || (emptyBody && rule.Note == EmptyBodyNote()));
        }

        private static string EmptyBodyNote() => PompLanguage.Pick("규칙 본문이 비어 있어요.", "The rule body is empty.");

        private static string EngineNote() => PompLanguage.Pick("편집기에서 바꿀 수 없는 머리말이 있어요.", "The frontmatter has entries that cannot be edited here.");

        /// <summary>One single-line YAML scalar, double-quoted (escapes \" \\ \/ \uXXXX), single-quoted, or plain text in block context; nothing else follows it.</summary>
        private static bool TryScalar(string value, out string result)
        {
            result = "";
            if (value.Length == 0) return false;
            var position = 0; string? text;
            if (value[0] == '"') text = ReadDouble(value, ref position, value.Length);
            else if (value[0] == '\'') text = ReadSingle(value, ref position, value.Length);
            else
            {
                // Plain text: no indicator first, no comment (" #") or mapping (": ", trailing ':'), and not null/boolean/number-like.
                if (PlainStarts.IndexOf(value[0]) >= 0 || value.Contains(": ") || value[value.Length - 1] == ':' || value.Contains(" #") || NotText.IsMatch(value)) return false;
                result = value; return true;
            }

            if (text == null || position != value.Length) return false;
            result = text; return true;
        }

        /// <summary><c>["a", 'b']</c> of quoted items only (an empty list is fine); a trailing comma or plain items are the engine's.</summary>
        private static bool TryFlowList(string value, List<string> items)
        {
            if (value.Length < 2 || value[value.Length - 1] != ']') return false;
            var end = value.Length - 1; var position = 1; var read = new List<string>();
            while (position < end && value[position] == ' ') position++;
            while (position < end)
            {
                var item = value[position] == '"' ? ReadDouble(value, ref position, end) : value[position] == '\'' ? ReadSingle(value, ref position, end) : null;
                if (item == null) return false;
                read.Add(item);
                while (position < end && value[position] == ' ') position++;
                if (position == end) break;
                if (value[position++] != ',') return false;
                while (position < end && value[position] == ' ') position++;
                if (position == end) return false;
            }

            items.AddRange(read); return true;
        }

        private static string? ReadDouble(string value, ref int position, int end)
        {
            var builder = new StringBuilder(); position++;
            while (position < end)
            {
                var c = value[position++];
                if (c == '"') return builder.ToString();
                if (c != '\\') { builder.Append(c); continue; }
                if (position >= end) return null;
                var escape = value[position++];
                if (escape == '"' || escape == '\\' || escape == '/') builder.Append(escape);
                else if (escape == 'u' && position + 4 <= end && int.TryParse(value.Substring(position, 4), NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out var code) && (code < 0xD800 || code > 0xDFFF)) { builder.Append((char)code); position += 4; }
                else return null;
            }

            return null;
        }

        private static string? ReadSingle(string value, ref int position, int end)
        {
            var builder = new StringBuilder(); position++;
            while (position < end)
            {
                var c = value[position++];
                if (c != '\'') { builder.Append(c); continue; }
                if (position < end && value[position] == '\'') { builder.Append('\''); position++; continue; }
                return builder.ToString();
            }

            return null;
        }

        internal string Create(string scope, string name, string description, bool alwaysApply, IReadOnlyList<string>? globs, string? body = null)
        {
            RequireNewName(name);
            return Write(scope, name, Encoded(Compose(name, description, alwaysApply, globs, body)));
        }

        /// <summary>The frontmatter <see cref="Create"/> writes (JSON-quoted scalars, a flow list of globs) and the body; an empty body gets the starter text.</summary>
        private static string Compose(string name, string description, bool alwaysApply, IReadOnlyList<string>? globs, string? body)
        {
            globs ??= Array.Empty<string>();
            if (description.Length > 500 || description.Any(char.IsControl)) throw new ArgumentException(PompLanguage.Pick("설명이 올바르지 않아요.", "The description is invalid."));
            if (globs.Count > MaxGlobs || globs.Any(glob => glob == null || glob.Trim().Length == 0 || glob.Length > 200 || glob.Any(char.IsControl)))
                throw new ArgumentException(PompLanguage.Pick("파일 패턴이 올바르지 않아요.", "The file patterns are invalid."));
            body = (body ?? "").Replace("\r\n", "\n").Trim();
            if (body.IndexOf('\0') >= 0) throw new ArgumentException(PompLanguage.Pick("올바른 Markdown 파일이 아니에요.", "Not an ordinary Markdown file."));
            var text = new StringBuilder("---\n");
            if (description.Length > 0) text.Append("description: ").Append(JsonConvert.SerializeObject(description)).Append('\n');
            text.Append("alwaysApply: ").Append(alwaysApply ? "true" : "false").Append('\n');
            if (globs.Count > 0) text.Append("globs: ").Append(JsonConvert.SerializeObject(globs.Select(glob => glob.Trim()))).Append('\n');
            text.Append("---\n\n").Append(body.Length > 0 ? body : "# " + name + "\n\nWrite the guidance for the agent here. Rules add guidance only; they never grant tool permissions.").Append('\n');
            return text.ToString();
        }

        private static byte[] Encoded(string text) => ToolResourceFiles.Encode(text, MaxBytes, "규칙 파일", "rule file");

        internal string Import(string scope, string name, string content)
        {
            RequireNewName(name);
            return Write(scope, name, CheckedText(content));
        }

        /// <summary>Pasted or edited whole-file text: strict UTF-8 within the limit, closed frontmatter, a non-empty body.</summary>
        private static byte[] CheckedText(string content)
        {
            var bytes = ToolResourceFiles.DecodeImport(content, MaxBytes, "규칙 파일", "rule file");
            var probe = new RuleInfoPayload(); Describe(probe, ToolResourceFiles.Text(bytes));
            if (content.IndexOf('\0') >= 0 || (probe.Note != null && probe.Note != EngineNote()))
                throw new ArgumentException(probe.Note ?? PompLanguage.Pick("올바른 Markdown 파일이 아니에요.", "Not an ordinary Markdown file."));
            return bytes;
        }

        /// <summary>One rule file for the in-page editor; the form only when the frontmatter reads identically here and in the engine.</summary>
        internal ResourceDocumentPayload Document(string scope, string name)
        {
            var file = ToolResourceFiles.Examine(FileOf(scope, name), MaxBytes);
            if (file.Unreadable) throw new InvalidOperationException(file.Problem);
            var text = file.Text;
            var probe = new RuleInfoPayload();
            var document = new ResourceDocumentPayload { Content = text, Hash = file.Hash! };
            if (Describe(probe, text))
            {
                var normalized = text.Replace("\r\n", "\n"); var match = Frontmatter.Match(normalized);
                document.Form = new JObject { ["description"] = probe.Description, ["alwaysApply"] = probe.AlwaysApply, ["globs"] = new JArray(probe.Globs), ["body"] = (match.Success ? normalized.Substring(match.Length) : normalized).Trim() };
            }

            return document;
        }

        /// <summary>The form fields (written as <see cref="Create"/> writes them; the body is required) or the whole text (checked as <see cref="Import"/>).</summary>
        internal void Update(string scope, string name, string? expectedHash, string? description, bool alwaysApply, IReadOnlyList<string>? globs, string? body, string? content)
        {
            byte[] bytes;
            if (content != null) bytes = CheckedText(content);
            else
            {
                if (string.IsNullOrWhiteSpace(body)) throw new ArgumentException(PompLanguage.Pick("규칙 본문이 비어 있어요.", "The rule body is empty."));
                bytes = Encoded(Compose(name, description ?? "", alwaysApply, globs, body));
            }

            var file = FileOf(scope, name);
            var current = ToolResourceFiles.Examine(file, MaxBytes);
            if (current.Unreadable) throw new InvalidOperationException(current.Problem);
            ToolResourceFiles.Replace(file, bytes, expectedHash, MaxBytes);
        }

        internal void Delete(string scope, string name, string? expectedHash)
        {
            lock (ToolResourceFiles.Gate)
                ToolResourceFiles.DeleteIfHash(Existing(scope, name), expectedHash, PompLanguage.Pick("규칙이 바뀌었어요.", "The rule changed."));
        }

        /// <summary>The file of an existing top-level rule, for opening in the editor.</summary>
        internal string FileOf(string scope, string name)
        {
            var file = Existing(scope, name);
            if (!File.Exists(file)) throw new FileNotFoundException(PompLanguage.Pick("규칙 파일이 더 이상 없어요.", "The rule file no longer exists."));
            return file;
        }

        /// <summary>A new rule gets the strict name; a listed one may have any name the engine loads (e.g. <c>My_Rule</c>), as long as it is one plain file name here.</summary>
        private string Existing(string scope, string name) => ToolResourceFiles.ExistingFile(DirectoryFor(scope), name, ".mdc", PompLanguage.Pick("규칙 이름이 올바르지 않아요.", "Invalid rule name."));

        private static void RequireNewName(string name)
        {
            if (!NewName.IsMatch(name)) throw new ArgumentException(PompLanguage.Pick("이름이 올바르지 않아요.", "The name is invalid."));
        }

        private string Write(string scope, string name, byte[] bytes)
        {
            lock (ToolResourceFiles.Gate)
            {
                var folder = DirectoryFor(scope);
                CheckpointStore.CheckDirectPath(folder);
                var file = Path.Combine(folder, name + ".mdc");
                ToolResourceFiles.WriteNew(file, bytes);
                return file;
            }
        }
    }
}
