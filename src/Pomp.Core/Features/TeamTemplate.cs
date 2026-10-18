using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Pomp.Hosting;
using Pomp.Internal;

namespace Pomp.Features
{
    /// <summary>
    /// Team template export/import (docs/architecture.md §8.13): one JSON text carrying a team and its user-defined member roles.
    /// Preview compares parsed fields (never bytes); import creates new files only and removes its own files when a later write fails.
    /// </summary>
    public sealed class TeamTemplate
    {
        internal const int MaxTemplateBytes = 256 * 1024;
        private const int MaxJsonDepth = 8;
        private const string TemplateFormat = "pomp-team-template";
        private const int TemplateVersion = 1;

        private static readonly HashSet<string> RoleKeys = new HashSet<string>(StringComparer.Ordinal)
        {
            "name", "title", "job", "description", "instructions", "model", "thinkingLevel", "tools"
        };
        private static readonly HashSet<string> TeamKeys = new HashSet<string>(StringComparer.Ordinal)
        {
            "id", "title", "goal", "members", "mode", "instructions", "membersOnly", "isolation"
        };

        private readonly AgentRoster _roster;

        public TeamTemplate(AgentRoster roster)
        {
            _roster = roster ?? throw new ArgumentNullException(nameof(roster));
        }

        /// <summary>Exports a team and its resolvable user-defined member roles; bundled members stay name-only.</summary>
        public string Export(string scope, string teamId)
        {
            var team = FindTeam(scope, teamId);
            var roles = _roster.ListRoles();
            var byName = new Dictionary<string, AgentRole>(StringComparer.OrdinalIgnoreCase);
            foreach (var r in roles)
            {
                if (!string.IsNullOrEmpty(r.Error) || byName.ContainsKey(r.Name)) continue;
                byName[r.Name] = r;
            }

            var roleObjs = new JArray();
            foreach (var member in team.Members)
            {
                if (!byName.TryGetValue(member, out var role))
                {
                    throw new InvalidOperationException(PompLanguage.Pick($"팀 '{team.Id}'의 구성원 '{member}'을(를) 찾을 수 없어 내보낼 수 없어요.", $"Cannot export team '{team.Id}': member '{member}' was not found."));
                }
                if (string.Equals(role.Scope, "bundled", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(role.Scope, "extension", StringComparison.OrdinalIgnoreCase) ||
                    AgentRoster.IsBundledRoleName(role.Name))
                {
                    continue;
                }
                if (!string.Equals(role.Scope, "project", StringComparison.OrdinalIgnoreCase) &&
                    !string.Equals(role.Scope, "user", StringComparison.OrdinalIgnoreCase))
                {
                    throw new InvalidOperationException(PompLanguage.Pick($"팀 '{team.Id}'의 구성원 '{member}'을(를) 템플릿에 담을 수 없어요.", $"Cannot export team '{team.Id}': member '{member}' cannot be represented in a template."));
                }
                if (!role.Editable || !string.IsNullOrEmpty(role.Note))
                {
                    throw new InvalidOperationException(PompLanguage.Pick($"팀 '{team.Id}'의 구성원 '{member}'은(는) POMP에서 다시 쓸 수 없는 파일이라 내보낼 수 없어요.", $"Cannot export team '{team.Id}': member '{member}' is not rewritable by POMP."));
                }
                if (!string.Equals(role.Tools, "all", StringComparison.OrdinalIgnoreCase) &&
                    !string.Equals(role.Tools, "readOnly", StringComparison.OrdinalIgnoreCase))
                {
                    throw new InvalidOperationException(PompLanguage.Pick($"팀 '{team.Id}'의 구성원 '{member}'은(는) 사용자 지정 도구라 내보낼 수 없어요.", $"Cannot export team '{team.Id}': member '{member}' uses custom tools."));
                }
                roleObjs.Add(ExportRole(role));
            }

            var doc = new JObject
            {
                ["format"] = TemplateFormat,
                ["version"] = TemplateVersion,
                ["team"] = new JObject
                {
                    ["id"] = team.Id,
                    ["title"] = team.Title,
                    ["goal"] = team.Goal,
                    ["members"] = new JArray(team.Members),
                    ["mode"] = team.Mode,
                    ["instructions"] = team.Instructions,
                    ["membersOnly"] = team.MembersOnly,
                    ["isolation"] = team.Isolation
                },
                ["roles"] = roleObjs
            };

            var text = doc.ToString(Formatting.Indented);
            if (Encoding.UTF8.GetByteCount(text) > MaxTemplateBytes)
            {
                throw new InvalidOperationException(PompLanguage.Pick("템플릿이 너무 커요.", "Template is too large."));
            }
            return text;
        }

        /// <summary>How the template text would land in <paramref name="scope"/>; refuses malformed text and unrepresentable content.</summary>
        public JObject Preview(string text, string scope)
        {
            var parsed = Parse(text, scope);
            return BuildPreview(parsed, scope, includeHash: true);
        }

        /// <summary>Creates the new roles, then the team; refuses on hash drift, conflicts, or problems; rolls back its own files.</summary>
        public string Import(string text, string scope, string previewHash)
        {
            var parsed = Parse(text, scope);
            var preview = BuildPreview(parsed, scope, includeHash: true);
            var hash = (string?)preview["hash"];
            if (string.IsNullOrEmpty(previewHash) || !string.Equals(hash, previewHash, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException(PompLanguage.Pick("미리본 뒤 파일이 바뀌었어요.", "Files changed since the preview."));
            }
            if (((JArray?)preview["problems"])?.Count > 0)
            {
                throw new InvalidOperationException(PompLanguage.Pick("가져오기에 문제가 있어요.", "The import has problems."));
            }
            var teamEntry = (JObject?)preview["team"];
            if (!string.Equals((string?)teamEntry?["status"], "new", StringComparison.Ordinal))
            {
                throw new InvalidOperationException(PompLanguage.Pick("같은 팀이 이미 있어 가져올 수 없어요.", "The team already exists, so it cannot be imported."));
            }
            foreach (var r in (JArray?)preview["roles"] ?? new JArray())
            {
                if (string.Equals((string?)((JObject)r)["status"], "conflict", StringComparison.Ordinal))
                {
                    throw new InvalidOperationException(PompLanguage.Pick("이름이 겹치는 역할이 있어 가져올 수 없어요.", "A role name conflicts, so the template cannot be imported."));
                }
            }

            var created = new List<CreatedFile>();
            try
            {
                foreach (var roleObj in parsed.Roles)
                {
                    var name = (string?)roleObj["name"] ?? string.Empty;
                    if (AgentRoster.IsBundledRoleName(name)) continue;
                    if (_roster.HasUsableRole(name)) continue;
                    _roster.SaveRole(scope, roleObj, null);
                    var path = _roster.ExistingRoleFile(scope, name);
                    created.Add(new CreatedFile(path, AgentRoster.ComputeFileHash(path)));
                }
                _roster.SaveTeam(scope, parsed.Team, null);
                var teamPath = _roster.ExistingTeamFile(scope, parsed.TeamId);
                created.Add(new CreatedFile(teamPath, AgentRoster.ComputeFileHash(teamPath)));
                return parsed.TeamId;
            }
            catch
            {
                foreach (var createdFile in created)
                {
                    try
                    {
                        // Only what this import wrote: another process may have replaced it since.
                        if (File.Exists(createdFile.Path) &&
                            string.Equals(AgentRoster.ComputeFileHash(createdFile.Path), createdFile.Hash, StringComparison.OrdinalIgnoreCase))
                        {
                            File.Delete(createdFile.Path);
                        }
                    }
                    catch { }
                }
                throw;
            }
        }

        private AgentTeam FindTeam(string scope, string teamId)
        {
            if (teamId != null) teamId = teamId.Trim();
            if (string.IsNullOrEmpty(teamId))
            {
                throw new InvalidOperationException(PompLanguage.Pick("팀 ID가 비어 있어요.", "Team ID is empty."));
            }
            var teams = _roster.ListTeams();
            var match = teams.FirstOrDefault(t => string.Equals(t.Scope, scope, StringComparison.OrdinalIgnoreCase) && string.Equals(t.Id, teamId, StringComparison.OrdinalIgnoreCase));
            if (match == null)
            {
                throw new InvalidOperationException(PompLanguage.Pick($"팀 '{teamId}'을(를) 찾을 수 없어요.", $"Team '{teamId}' was not found."));
            }
            if (!string.IsNullOrEmpty(match.Error))
            {
                throw new InvalidOperationException(PompLanguage.Pick($"팀 '{teamId}'에 오류가 있어요: {match.Error}", $"Team '{teamId}' has an error: {match.Error}"));
            }
            if (match.Members.Count < 2)
            {
                throw new InvalidOperationException(PompLanguage.Pick($"팀 '{teamId}'의 구성원이 너무 적어요.", $"Team '{teamId}' has too few members."));
            }
            if (match.Missing.Count > 0)
            {
                throw new InvalidOperationException(PompLanguage.Pick($"팀 '{teamId}'의 일부 구성원을 찾을 수 없어요: {string.Join(", ", match.Missing)}", $"Some members of team '{teamId}' are missing: {string.Join(", ", match.Missing)}"));
            }
            return match;
        }

        private static JObject ExportRole(AgentRole role)
        {
            var obj = new JObject
            {
                ["name"] = role.Name,
                ["title"] = role.Title,
                ["job"] = role.Job,
                ["description"] = role.Description,
                ["instructions"] = role.Instructions,
                ["tools"] = role.Tools
            };
            if (!string.IsNullOrEmpty(role.Model)) obj["model"] = role.Model;
            if (!string.IsNullOrEmpty(role.ThinkingLevel)) obj["thinkingLevel"] = role.ThinkingLevel;
            return obj;
        }

        private sealed class ParsedTemplate
        {
            public string TeamId { get; set; } = string.Empty;
            public string TeamTitle { get; set; } = string.Empty;
            public string TeamMode { get; set; } = "lead";
            public List<string> TeamMembers { get; set; } = new List<string>();
            public JObject Team { get; set; } = new JObject();
            public List<JObject> Roles { get; set; } = new List<JObject>();
        }

        private sealed class CreatedFile
        {
            public CreatedFile(string path, string hash)
            {
                Path = path;
                Hash = hash;
            }

            public string Path { get; }
            public string Hash { get; }
        }

        private static void RejectScope(string scope)
        {
            if (!string.Equals(scope, "project", StringComparison.OrdinalIgnoreCase) &&
                !string.Equals(scope, "user", StringComparison.OrdinalIgnoreCase))
            {
                throw new ArgumentException(PompLanguage.Pick($"알 수 없는 범위예요: '{scope}'", $"Unknown scope: '{scope}'"));
            }
        }

        private ParsedTemplate Parse(string text, string scope)
        {
            RejectScope(scope);
            if (text == null) text = string.Empty;
            var bytes = Encoding.UTF8.GetByteCount(text);
            if (bytes == 0 || bytes > MaxTemplateBytes)
            {
                throw new InvalidOperationException(PompLanguage.Pick("템플릿이 비어 있거나 너무 커요.", "Template is empty or too large."));
            }

            JObject doc;
            try
            {
                using (var reader = new StringReader(text))
                using (var json = new JsonTextReader(reader) { MaxDepth = MaxJsonDepth, DateParseHandling = DateParseHandling.None })
                {
                    doc = JObject.Load(json, new JsonLoadSettings { DuplicatePropertyNameHandling = DuplicatePropertyNameHandling.Error });
                    if (json.Read()) throw new InvalidDataException("Trailing content.");
                }
            }
            catch (InvalidOperationException) { throw; }
            catch (Exception ex)
            {
                throw new InvalidOperationException(PompLanguage.Pick("템플릿 형식이 올바르지 않아요: " + ex.Message, "Template is not valid: " + ex.Message));
            }

            CheckKeys(doc, "template", new HashSet<string>(StringComparer.Ordinal) { "format", "version", "team", "roles" });
            if ((string?)doc["format"] != TemplateFormat || doc["version"]?.Type != JTokenType.Integer || (int)doc["version"]! != TemplateVersion)
            {
                throw new InvalidOperationException(PompLanguage.Pick("지원되지 않는 템플릿 형식이에요.", "Unsupported template format."));
            }
            var teamObj = doc["team"] as JObject ?? throw new InvalidOperationException(PompLanguage.Pick("템플릿에 팀이 없어요.", "Template has no team."));
            CheckKeys(teamObj, "team", TeamKeys);
            var rolesArray = doc["roles"] as JArray ?? throw new InvalidOperationException(PompLanguage.Pick("템플릿에 역할이 없어요.", "Template has no roles."));

            var parsed = new ParsedTemplate { Team = teamObj };
            var teamFields = AgentRoster.TeamFields(teamObj);
            parsed.TeamId = teamFields.Id;
            parsed.TeamTitle = teamFields.Title;
            parsed.TeamMode = teamFields.Mode;
            parsed.TeamMembers = teamFields.Members;

            // Members reference bundled roles by name: only listed roles need full validation.
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var item in rolesArray)
            {
                var roleObj = item as JObject ?? throw new InvalidOperationException(PompLanguage.Pick("템플릿 역할이 올바르지 않아요.", "Template has an invalid role."));
                CheckKeys(roleObj, "role", RoleKeys);
                // Custom tool lists are not representable in a template (export refuses them too):
                // reject before the shared validator, whose string cast would throw a raw ArgumentException.
                var toolsToken = roleObj["tools"];
                var toolsText = (toolsToken?.Type == JTokenType.String ? (string?)toolsToken : null)?.Trim();
                if ((toolsToken != null && toolsToken.Type == JTokenType.Array) ||
                    (toolsText != null && !string.Equals(toolsText, "all", StringComparison.OrdinalIgnoreCase) && !string.Equals(toolsText, "readOnly", StringComparison.OrdinalIgnoreCase)))
                {
                    var carriedName = (string?)roleObj["name"] ?? "?";
                    throw new InvalidOperationException(PompLanguage.Pick($"템플릿 역할 '{carriedName}'은(는) 사용자 지정 도구라 가져올 수 없어요.", $"Template role '{carriedName}' uses custom tools and cannot be imported."));
                }
                var fields = AgentRoster.RoleFields(roleObj);
                if (!seen.Add(fields.Name))
                {
                    throw new InvalidOperationException(PompLanguage.Pick($"템플릿에 중복된 역할 '{fields.Name}'이(가) 있어요.", $"Template has a duplicate role '{fields.Name}'."));
                }
                if (AgentRoster.IsBundledRoleName(fields.Name))
                {
                    throw new InvalidOperationException(PompLanguage.Pick($"템플릿 역할 '{fields.Name}'은(는) POMP에 들어 있는 역할 이름이라 담을 수 없어요.", $"Template role '{fields.Name}' is a bundled name and cannot be carried."));
                }
                if (!string.Equals(fields.Tools, "all", StringComparison.OrdinalIgnoreCase) &&
                    !string.Equals(fields.Tools, "readOnly", StringComparison.OrdinalIgnoreCase))
                {
                    throw new InvalidOperationException(PompLanguage.Pick($"템플릿 역할 '{fields.Name}'은(는) 사용자 지정 도구라 가져올 수 없어요.", $"Template role '{fields.Name}' uses custom tools and cannot be imported."));
                }
                parsed.Roles.Add(roleObj);
            }

            var missingRefs = parsed.TeamMembers.Where(m => !parsed.Roles.Any(r => string.Equals((string?)r["name"], m, StringComparison.OrdinalIgnoreCase)) && !AgentRoster.IsBundledRoleName(m) && !_roster.HasUsableRole(m)).ToList();
            if (missingRefs.Count > 0)
            {
                throw new InvalidOperationException(PompLanguage.Pick($"템플릿에 없는 구성원이 있어요: {string.Join(", ", missingRefs)}", $"Template is missing members: {string.Join(", ", missingRefs)}"));
            }

            return parsed;
        }

        private static void CheckKeys(JObject obj, string what, HashSet<string> allowed)
        {
            foreach (var prop in obj.Properties())
            {
                if (!allowed.Contains(prop.Name))
                {
                    throw new InvalidOperationException(PompLanguage.Pick($"템플릿 {what}에 알 수 없는 키 '{prop.Name}'이(가) 있어요.", $"Template {what} has an unknown key '{prop.Name}'."));
                }
            }
        }

        private JObject BuildPreview(ParsedTemplate parsed, string scope, bool includeHash)
        {
            var problems = new JArray();
            var teamStatus = CompareTeam(parsed, scope, problems);
            var roleEntries = new JArray();
            foreach (var roleObj in parsed.Roles)
            {
                roleEntries.Add(CompareRole(roleObj, problems));
            }
            // Referenced but not carried: bundled by name, otherwise whatever resolves locally counts as existing.
            foreach (var member in parsed.TeamMembers)
            {
                if (parsed.Roles.Any(r => string.Equals((string?)r["name"], member, StringComparison.OrdinalIgnoreCase))) continue;
                roleEntries.Add(new JObject
                {
                    ["name"] = member,
                    ["title"] = member,
                    ["status"] = AgentRoster.IsBundledRoleName(member) ? "bundled" : "existing"
                });
            }

            var preview = new JObject
            {
                ["team"] = new JObject
                {
                    ["id"] = parsed.TeamId,
                    ["title"] = parsed.TeamTitle,
                    ["mode"] = parsed.TeamMode,
                    ["members"] = new JArray(parsed.TeamMembers),
                    ["status"] = teamStatus
                },
                ["roles"] = roleEntries,
                ["problems"] = problems
            };
            if (includeHash)
            {
                preview["hash"] = ComputePreviewHash(parsed, scope);
            }
            return preview;
        }

        private string CompareTeam(ParsedTemplate parsed, string scope, JArray problems)
        {
            string target;
            try
            {
                target = _roster.ExistingTeamFile(scope, parsed.TeamId);
            }
            catch (Exception ex)
            {
                problems.Add(ex.Message);
                return "conflict";
            }
            if (!File.Exists(target)) return "new";
            AgentTeam current;
            try
            {
                current = _roster.ReadTeamFile(target, scope);
            }
            catch (Exception ex)
            {
                problems.Add(PompLanguage.Pick($"팀 '{parsed.TeamId}' 파일을 읽을 수 없어요: {ex.Message}", $"Cannot read team '{parsed.TeamId}': {ex.Message}"));
                return "conflict";
            }
            if (!string.IsNullOrEmpty(current.Error))
            {
                problems.Add(PompLanguage.Pick($"팀 '{parsed.TeamId}' 파일에 오류가 있어요: {current.Error}", $"Team file '{parsed.TeamId}' has an error: {current.Error}"));
                return "conflict";
            }
            var fields = AgentRoster.TeamFields(parsed.Team);
            if (string.Equals(current.Title, fields.Title, StringComparison.Ordinal) &&
                string.Equals(current.Goal, fields.Goal, StringComparison.Ordinal) &&
                string.Equals(current.Mode, fields.Mode, StringComparison.Ordinal) &&
                string.Equals(current.Instructions, fields.Instructions, StringComparison.Ordinal) &&
                current.MembersOnly == fields.MembersOnly &&
                current.Isolation == fields.Isolation &&
                current.Members.SequenceEqual(fields.Members, StringComparer.OrdinalIgnoreCase))
            {
                return "same";
            }
            return "conflict";
        }

        private JObject CompareRole(JObject roleObj, JArray problems)
        {
            var fields = AgentRoster.RoleFields(roleObj);
            var entry = new JObject { ["name"] = fields.Name, ["title"] = fields.Title ?? fields.Name };

            // Both scopes shadow like the roster lists them: a project file of any state wins over the user file.
            foreach (var scope in new[] { "project", "user" })
            {
                string target;
                try
                {
                    target = _roster.ExistingRoleFile(scope, fields.Name);
                }
                catch (Exception ex)
                {
                    problems.Add(ex.Message);
                    entry["status"] = "conflict";
                    return entry;
                }
                if (!File.Exists(target)) continue;
                AgentRole current;
                try
                {
                    current = _roster.ReadRoleFile(target, scope);
                }
                catch (Exception ex)
                {
                    problems.Add(PompLanguage.Pick($"역할 '{fields.Name}' 파일을 읽을 수 없어요: {ex.Message}", $"Cannot read role '{fields.Name}': {ex.Message}"));
                    entry["status"] = "conflict";
                    return entry;
                }
                if (!string.IsNullOrEmpty(current.Error) || !current.Editable)
                {
                    // A file POMP cannot rewrite never counts as 'same': importing must not claim it.
                    entry["status"] = "conflict";
                    return entry;
                }
                if (string.Equals(current.Title, fields.Title ?? fields.Name, StringComparison.Ordinal) &&
                    string.Equals(current.Job ?? string.Empty, fields.Job ?? string.Empty, StringComparison.Ordinal) &&
                    string.Equals(current.Description, fields.Description, StringComparison.Ordinal) &&
                    string.Equals(current.Instructions ?? string.Empty, fields.Instructions ?? string.Empty, StringComparison.Ordinal) &&
                    string.Equals(current.Model ?? string.Empty, fields.Model ?? string.Empty, StringComparison.Ordinal) &&
                    string.Equals(current.ThinkingLevel ?? string.Empty, fields.ThinkingLevel ?? string.Empty, StringComparison.Ordinal) &&
                    string.Equals(current.Tools, fields.Tools, StringComparison.OrdinalIgnoreCase))
                {
                    entry["status"] = "same";
                }
                else
                {
                    entry["status"] = "conflict";
                }
                return entry;
            }
            entry["status"] = "new";
            return entry;
        }

        /// <summary>Stable hash over the parsed fields plus the current target files: any change refuses the import.</summary>
        private string ComputePreviewHash(ParsedTemplate parsed, string scope)
        {
            var sb = new StringBuilder();
            sb.Append(scope.ToLowerInvariant()).Append('\n');
            sb.Append(parsed.Team.ToString(Formatting.None)).Append('\n');
            foreach (var roleObj in parsed.Roles.OrderBy(r => (string?)r["name"], StringComparer.Ordinal))
            {
                sb.Append(((string?)roleObj["name"])?.ToLowerInvariant()).Append(':').Append(roleObj.ToString(Formatting.None)).Append('\n');
            }
            foreach (var file in TargetFiles(parsed, scope).OrderBy(f => f, StringComparer.OrdinalIgnoreCase))
            {
                try
                {
                    sb.Append(file.ToLowerInvariant()).Append(':');
                    sb.Append(File.Exists(file) ? AgentRoster.ComputeFileHash(file) : "absent").Append('\n');
                }
                catch
                {
                    sb.Append(file.ToLowerInvariant()).Append(":unreadable\n");
                }
            }
            return AgentRoster.ComputeSha256(Encoding.UTF8.GetBytes(sb.ToString()));
        }

        private List<string> TargetFiles(ParsedTemplate parsed, string scope)
        {
            var files = new List<string>();
            try { files.Add(_roster.ExistingTeamFile(scope, parsed.TeamId)); } catch { }
            foreach (var roleObj in parsed.Roles)
            {
                try { files.Add(_roster.ExistingRoleFile(scope, (string?)roleObj["name"] ?? string.Empty)); } catch { }
            }
            return files;
        }
    }
}
