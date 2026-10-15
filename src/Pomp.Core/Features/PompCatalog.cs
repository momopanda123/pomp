using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Pomp.Diagnostics;
using Pomp.Hosting;
using Pomp.Internal;
using Pomp.Ui;

namespace Pomp.Features
{
    /// <summary>
    /// The POMP catalog: skills built into POMP and MCP servers at pinned versions, bundled with the extension
    /// (<c>omp-extension/catalog/catalog.json</c>, skill folders under <c>catalog/skills/&lt;id&gt;/</c>).
    /// The skill folders are a <c>skills.customDirectories</c> entry of the engine overlay, so omp discovers the
    /// enabled catalog skills itself (name + description in its skill list, <c>skill:&lt;name&gt;</c> commands)
    /// and a user's own copy with the same name wins; <c>disabledExtensions: skill:&lt;name&gt;</c> switches them
    /// off without a restart. MCP entries stay install-based (<c>catalog/command install</c> writes one
    /// create-only server): they need inputs such as tokens. Installs never overwrite, never download and never run anything.
    /// </summary>
    internal sealed class PompCatalog
    {
        internal const int MaxBytes = 256 * 1024;
        internal const int MaxEntries = 100;
        private const int MaxString = 2000;
        private const int MaxInputs = 20;
        private const int MaxRequires = 20;
        private const int MaxServerFields = 200;
        private static readonly Regex Id = new Regex(@"\A[a-z0-9-]{2,64}\z", RegexOptions.CultureInvariant);
        private static readonly string[] Kinds = { "skill", "mcp" };
        private static readonly string[] Scopes = { "project", "user" };
        private static readonly string[] Needs = { "network", "token", "browser", "node" };
        private static readonly Regex InputKey = new Regex(@"\A[A-Za-z_][A-Za-z0-9_]{0,63}\z", RegexOptions.CultureInvariant);

        private readonly string root, home;
        private readonly string? package;
        private readonly IPompLogger? logger;
        internal PompCatalog(string root, string? home, string? package = null, IPompLogger? logger = null)
        {
            this.root = Path.GetFullPath(root); this.home = ToolResourceFiles.Home(home);
            this.package = package == null ? null : Path.GetFullPath(package); this.logger = logger;
        }

        internal sealed class Input
        {
            internal string Key = "", Label = "", LabelKo = ""; internal bool Secret, Required;
        }

        internal sealed class Entry
        {
            internal string Id = "", Kind = "", Title = "", Description = "", Category = "", Version = "";

            // Optional Korean texts, shown when the POMP language is Korean (the English ones are the fallback).
            internal string TitleKo = "", DescriptionKo = "";
            internal string? Publisher, License;
            internal readonly List<string> Scopes = new List<string>();
            internal readonly List<string> Requires = new List<string>();
            internal readonly List<string> Needs = new List<string>();
            internal readonly List<Input> Inputs = new List<Input>();
            internal JObject? Server;
            /// <summary>Skills: whether the skill is on out of the box (absent = off); ignored on MCP entries.</summary>
            internal bool DefaultEnabled;
        }

        /// <summary>The folder omp discovers the built-in skills from (a <c>skills.customDirectories</c> entry), or "" without a package.</summary>
        internal string SkillsDirectory() => package == null ? "" : Path.Combine(package, "catalog", "skills");

        internal string CatalogFile() => package == null ? "" : Path.Combine(package, "catalog", "catalog.json");

        internal string SkillFolder(string id) => package == null ? "" : Path.Combine(package, "catalog", "skills", id);

        /// <summary>The bundled entries (at most <see cref="MaxEntries"/>); a missing or invalid catalog is an empty list, never an error.</summary>
        internal IReadOnlyList<Entry> Load()
        {
            var file = CatalogFile();
            try
            {
                CheckpointStore.CheckDirectPath(file);
                if (package == null || !File.Exists(file)) return Array.Empty<Entry>();
                var bytes = ToolResourceFiles.Read(file, MaxBytes);
                using var reader = new JsonTextReader(new StringReader(ToolResourceFiles.Text(bytes))) { MaxDepth = 10, DateParseHandling = DateParseHandling.None };
                var root = JObject.Load(reader, new JsonLoadSettings { DuplicatePropertyNameHandling = DuplicatePropertyNameHandling.Error });
                if (reader.Read()) throw new JsonException("Trailing catalog content.");
                if ((int?)root["version"] != 1 || root["entries"] is not JArray list) throw new JsonException("Not a POMP catalog.");
                var entries = new List<Entry>();
                foreach (var item in list.Take(MaxEntries + 1))
                {
                    if (item is not JObject entry) throw new JsonException("Not a POMP catalog.");
                    entries.Add(Parse(entry));
                }

                if (entries.Count > MaxEntries) throw new JsonException("Too many catalog entries.");
                if (entries.GroupBy(e => e.Id, StringComparer.Ordinal).Any(g => g.Count() > 1)) throw new JsonException("Duplicate catalog id.");
                if (entries.Any(e => e.Requires.Any(r => entries.All(o => o.Id != r)))) throw new JsonException("Unknown catalog requirement.");
                if (entries.Any(e => e.Kind == "skill" && e.Requires.Any(r => entries.Single(o => o.Id == r).Kind != "skill"))) throw new JsonException("A skill requirement is not a skill.");
                return entries;
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException || ex is JsonException || ex is ArgumentException || ex is System.Security.SecurityException)
            {
                logger?.Warn("Could not read the POMP catalog " + file + ": " + ex.Message);
                return Array.Empty<Entry>();
            }
        }

        private static string Localized(string english, string korean) =>
            korean.Length > 0 && PompLanguage.IsKorean ? korean : english;

        private static Entry Parse(JObject json)
        {
            string Text(string key, bool optional = false, int max = MaxString)
            {
                var token = json[key];
                if (token == null)
                {
                    if (optional) return "";
                    throw new JsonException("Missing catalog " + key + ".");
                }

                if (token.Type != JTokenType.String) throw new JsonException("Invalid catalog " + key + ".");
                var value = (string)token!;
                if (value.Length > max || value.IndexOf('\0') >= 0) throw new JsonException("Invalid catalog " + key + ".");
                return value;
            }

            List<string> Words(string key, string[] allowed)
            {
                if (json[key] is not JArray list || list.Count > MaxRequires) throw new JsonException("Invalid catalog " + key + ".");
                var values = new List<string>();
                foreach (var item in list)
                {
                    if (item.Type != JTokenType.String || !allowed.Contains((string)item!)) throw new JsonException("Invalid catalog " + key + ".");
                    if (!values.Contains((string)item!)) values.Add((string)item!);
                }

                if (values.Count == 0 && key == "scopes") throw new JsonException("Invalid catalog scopes.");
                return values;
            }

            var entry = new Entry { Id = Text("id", max: 64), Kind = Text("kind", max: 16), Title = Text("title"), Description = Text("description"), Category = Text("category", max: 64), Version = Text("version", max: 64) };
            if (!Id.IsMatch(entry.Id) || !Kinds.Contains(entry.Kind) || entry.Title.Length == 0 || entry.Description.Length == 0 || entry.Category.Length == 0 || entry.Version.Length == 0) throw new JsonException("Invalid catalog entry.");
            entry.Publisher = Text("publisher", true); entry.License = Text("license", true);
            entry.TitleKo = Text("titleKo", true); entry.DescriptionKo = Text("descriptionKo", true);
            entry.Scopes.AddRange(Words("scopes", Scopes));
            entry.Needs.AddRange(Words("needs", Needs));
            if (json["requires"] != null)
            {
                if (json["requires"] is not JArray requires || requires.Count > MaxRequires) throw new JsonException("Invalid catalog requires.");
                foreach (var item in requires)
                {
                    if (item.Type != JTokenType.String || !Id.IsMatch((string)item!)) throw new JsonException("Invalid catalog requires.");
                    if (!entry.Requires.Contains((string)item!)) entry.Requires.Add((string)item!);
                }
            }

            if (json["inputs"] != null)
            {
                if (json["inputs"] is not JArray inputs || inputs.Count > MaxInputs) throw new JsonException("Invalid catalog inputs.");
                foreach (var item in inputs)
                {
                    if (item is not JObject input || input["key"]?.Type != JTokenType.String || !InputKey.IsMatch((string)input["key"]!) || input["label"]?.Type != JTokenType.String || ((string)input["label"]!).Length is < 1 or > 200 || input["secret"]?.Type != JTokenType.Boolean || input["required"]?.Type != JTokenType.Boolean) throw new JsonException("Invalid catalog input.");
                    if (entry.Inputs.Any(i => i.Key == (string)input["key"]!)) throw new JsonException("Duplicate catalog input.");
                    entry.Inputs.Add(new Input { Key = (string)input["key"]!, Label = (string)input["label"]!, LabelKo = input["labelKo"]?.Type == JTokenType.String && ((string)input["labelKo"]!).Length <= 200 ? (string)input["labelKo"]! : "", Secret = (bool)input["secret"]!, Required = (bool)input["required"]! });
                }
            }

            if (entry.Kind == "mcp")
            {
                if (json["server"] is not JObject server) throw new JsonException("A catalog MCP entry needs a server.");
                entry.Server = ValidateServer(server);
            }
            else if (json["server"] != null) throw new JsonException("A catalog skill entry takes no server.");
            else if (entry.Inputs.Count > 0) throw new JsonException("A catalog skill entry takes no inputs.");
            if (json["defaultEnabled"] != null)
            {
                if (json["defaultEnabled"]!.Type != JTokenType.Boolean) throw new JsonException("Invalid catalog defaultEnabled.");
                entry.DefaultEnabled = (bool)json["defaultEnabled"]!;
            }

            return entry;
        }

        /// <summary>An MCP server template: the NativeMcpConfig shape, where string values may hold <c>${input:KEY}</c> placeholders.</summary>
        private static JObject ValidateServer(JObject server)
        {
            var count = 0;
            void Walk(JToken token)
            {
                if (++count > MaxServerFields) throw new JsonException("The catalog server is too large.");
                switch (token.Type)
                {
                    case JTokenType.Object:
                        foreach (var property in ((JObject)token).Properties())
                        {
                            if (property.Name.Length is < 1 or > 128) throw new JsonException("Invalid catalog server.");
                            Walk(property.Value);
                        }

                        break;
                    case JTokenType.Array:
                        if (((JArray)token).Count > MaxServerFields) throw new JsonException("The catalog server is too large.");
                        foreach (var item in (JArray)token) Walk(item);
                        break;
                    case JTokenType.String:
                        if (((string)token!).Length > MaxString || ((string)token!).IndexOf('\0') >= 0) throw new JsonException("Invalid catalog server.");
                        break;
                    case JTokenType.Boolean:
                    case JTokenType.Integer:
                        break;
                    default:
                        throw new JsonException("Invalid catalog server.");
                }
            }

            Walk(server);
            if ((string?)server["type"] is not ("stdio" or "http" or "sse")) throw new JsonException("Invalid catalog server type.");
            return (JObject)server.DeepClone();
        }

        internal CatalogStatePayload View()
        {
            lock (ToolResourceFiles.Gate)
            {
                var result = new CatalogStatePayload();
                if (package == null) return result;
                var files = new NativeMcpConfig(root, home);
                // Skills are built in (switched on the skills list); only MCP entries stay install-based.
                foreach (var entry in Load().Where(e => e.Kind == "mcp"))
                {
                    var row = new CatalogEntryPayload { Id = entry.Id, Kind = entry.Kind, Title = Localized(entry.Title, entry.TitleKo), Description = Localized(entry.Description, entry.DescriptionKo), Category = entry.Category, Version = entry.Version, Publisher = entry.Publisher, License = entry.License };
                    row.Scopes.AddRange(entry.Scopes); row.Requires.AddRange(entry.Requires); row.Needs.AddRange(entry.Needs);
                    row.Inputs.AddRange(entry.Inputs.Select(i => new CatalogInputPayload { Key = i.Key, Label = Localized(i.Label, i.LabelKo), Secret = i.Secret, Required = i.Required }));
                    foreach (var scope in entry.Scopes) if (IsInstalled(scope, entry, files)) row.Installed.Add(scope);
                    result.Entries.Add(row);
                }

                return result;
            }
        }

        /// <summary>One built-in skill row: its texts come from the bundled SKILL.md (what omp reports), never from catalog.json.</summary>
        internal sealed class BuiltinSkill
        {
            internal string Id = "", Description = "", File = "", Category = "";
            internal bool DefaultEnabled;
            internal readonly List<string> Requires = new List<string>();
        }

        /// <summary>
        /// <summary>
        /// The bundled skills the page lists (before the engine reports them too): name and description from each
        /// bundled <c>SKILL.md</c> frontmatter (bounded read; what omp discovers and shows), default state and
        /// category from the catalog entry. A folder whose SKILL.md does not read, or whose declared name is not
        /// the entry id, is skipped (never a failed list).
        /// </summary>
        /// </summary>
        internal IReadOnlyList<BuiltinSkill> BuiltinSkills()
        {
            var rows = new List<BuiltinSkill>();
            if (package == null) return rows;
            foreach (var entry in Load().Where(e => e.Kind == "skill"))
            {
                try
                {
                    var file = Path.Combine(SkillFolder(entry.Id), "SKILL.md");
                    CheckpointStore.CheckDirectPath(file);
                    if (!File.Exists(file)) continue;
                    var probe = ToolResourceFiles.Examine(file, SkillCatalog.MaxBytes);
                    if (probe.Unreadable) continue;
                    if (!SkillCatalog.TrySkillIdentity(probe.Text, out var declared, out var description)) continue;
                    // The skill only counts as this entry when its declared name is the id (as an installed copy would).
                    if (declared != entry.Id || description.Length == 0) continue;
                    var row = new BuiltinSkill { Id = entry.Id, File = file, Category = entry.Category, DefaultEnabled = entry.DefaultEnabled, Description = description };
                    row.Requires.AddRange(entry.Requires);
                    rows.Add(row);
                }
                catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException || ex is ArgumentException || ex is System.Security.SecurityException)
                {
                    // A broken bundled skill is skipped, not a failed list.
                }
            }

            return rows;
        }

        /// <summary>Whether the MCP <paramref name="entry"/> is installed in <paramref name="scope"/> (a <c>pompCatalog</c> server entry).</summary>
        internal bool IsInstalled(string scope, Entry entry, NativeMcpConfig? mcp = null)
        {
            try
            {
                if (entry.Kind != "mcp") return false;
                var files = mcp ?? new NativeMcpConfig(root, home);
                var servers = (scope == "project" ? files.ProjectMcp() : files.UserMcp())?["mcpServers"] as JObject;
                return servers?.Properties().Any(p => p.Value is JObject server && (string?)server["pompCatalog"] == entry.Id) == true;
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException || ex is JsonException || ex is ArgumentException || ex is System.Security.SecurityException)
            {
                return false;
            }
        }

        /// <summary>Installs the MCP <paramref name="id"/> into <paramref name="scope"/> (one create-only server); skills are built in and rejected.</summary>
        internal Installed Install(string id, string scope, IReadOnlyDictionary<string, string>? inputs, NativeMcpConfig mcp)
        {
            if (scope != "project" && scope != "user") throw new ArgumentException(PompLanguage.Pick("범위를 고르지 않았어요.", "No scope was chosen."));
            var entries = package == null ? new List<Entry>() : Load().Where(e => e.Kind == "mcp").ToList();
            var entry = entries.SingleOrDefault(e => e.Id == id) ?? throw new ArgumentException(PompLanguage.Pick("목록에 없는 항목이에요.", "This catalog entry does not exist."));
            if (!entry.Scopes.Contains(scope)) throw new ArgumentException(PompLanguage.Pick("이 범위에는 설치할 수 없어요.", "This entry cannot be installed into this scope."));
            lock (ToolResourceFiles.Gate)
            {
                return InstallMcp(entry, scope, inputs ?? new Dictionary<string, string>(), mcp);
            }
        }

        /// <summary>
        /// Registers each MCP entry once in the user's mcp.json, switched off and with its inputs left empty (the user fills them in by
        /// editing the server). <paramref name="done"/> holds the ids registered before: a server the user deleted is never registered again,
        /// and a server of the user's own with the same name is left alone.
        /// </summary>
        internal void Seed(NativeMcpConfig mcp, ISet<string> done)
        {
            if (package == null) return;
            lock (ToolResourceFiles.Gate)
            {
                foreach (var entry in Load().Where(e => e.Kind == "mcp" && e.Server != null && !done.Contains(e.Id)))
                {
                    done.Add(entry.Id);
                    var server = FillPlaceholders(entry.Server!, _ => "");
                    PruneEmptyInputs(server, entry);
                    server["enabled"] = false;
                    server["pompCatalog"] = entry.Id;
                    try { mcp.Mutate(new McpCommandPayload { Action = "save", Scope = "user", Name = entry.Id, Server = server, ExpectedHash = null }); }
                    catch (InvalidOperationException) { } // the name is taken
                }
            }
        }

        internal sealed class Installed
        {
            internal string? ServerName;
        }

        private Installed InstallMcp(Entry entry, string scope, IReadOnlyDictionary<string, string> inputs, NativeMcpConfig mcp)
        {
            if (entry.Server == null) throw new InvalidOperationException(PompLanguage.Pick("묶인 MCP 항목이 올바르지 않아요.", "The bundled MCP entry is invalid."));
            // A secret input stays in the user's own file; an optional secret left empty keeps the project scope allowed.
            var secrets = entry.Inputs.Where(i => i.Secret).ToList();
            var filledSecrets = secrets.Where(i => inputs.TryGetValue(i.Key, out var value) && value.Length > 0).ToList();
            if (scope == "project" && filledSecrets.Count > 0) throw new ArgumentException(PompLanguage.Pick("비밀 값이 있어 사용자 범위에만 설치할 수 있어요.", "With a secret value this installs into the user scope only."));
            if (scope == "project" && secrets.Any(i => i.Required)) throw new ArgumentException(PompLanguage.Pick("비밀 값이 필요해 사용자 범위에만 설치할 수 있어요.", "This needs a secret value, so it installs into the user scope only."));
            foreach (var input in entry.Inputs)
            {
                inputs.TryGetValue(input.Key, out var value);
                value ??= "";
                if (input.Required && value.Length == 0) throw new ArgumentException(PompLanguage.Pick("필수 입력이 비어 있어요.", "A required input is empty."));
                if (value.Length > MaxString || value.IndexOf('\0') >= 0) throw new ArgumentException(PompLanguage.Pick("입력 값이 올바르지 않아요.", "An input value is invalid."));
            }

            var server = FillPlaceholders(entry.Server, name => inputs.TryGetValue(name, out var value) ? value : "");
            // An empty optional input leaves no empty variable or header behind.
            PruneEmptyInputs(server, entry);
            server["enabled"] = false;
            server["pompCatalog"] = entry.Id;
            var done = new Installed { ServerName = entry.Id };
            mcp.Mutate(new McpCommandPayload { Action = "save", Scope = scope, Name = entry.Id, Server = server, ExpectedHash = null });
            return done;
        }

        private static readonly Regex Placeholder = new Regex(@"\$\{input:([A-Za-z_][A-Za-z0-9_]{0,63})\}", RegexOptions.CultureInvariant);

        /// <summary>Replaces <c>${input:KEY}</c> in every string of <paramref name="server"/>; unknown keys stay empty (validated inputs only).</summary>
        internal static JObject FillPlaceholders(JObject server, Func<string, string> value)
        {
            var filled = (JObject)server.DeepClone();
            void Walk(JToken token)
            {
                if (token is JObject obj) foreach (var property in obj.Properties()) Walk(property.Value);
                else if (token is JArray list) foreach (var item in list) Walk(item);
                else if (token.Type == JTokenType.String) token.Replace(new JValue(Placeholder.Replace((string)token!, m => value(m.Groups[1].Value))));
            }

            Walk(filled);
            return filled;
        }

        private static void PruneEmptyInputs(JObject server, Entry entry)
        {
            var empty = new HashSet<string>(entry.Inputs.Where(i => !i.Required).Select(i => i.Key), StringComparer.Ordinal);
            if (empty.Count == 0) return;
            foreach (var key in new[] { "env", "headers" })
            {
                if (server[key] is not JObject map) continue;
                // Only whole-value placeholders prune: a partially filled string keeps its (empty) part.
                foreach (var property in map.Properties().Where(p => p.Value.Type == JTokenType.String && Placeholder.IsMatch((string?)entry.Server![key]![p.Name] ?? "") && ((string)p.Value!).Length == 0).ToArray()) property.Remove();
                if (map.Count == 0) server.Remove(key);
            }
        }
    }
}
