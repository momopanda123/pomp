using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Pomp.Hosting;
using Pomp.Internal;
using Pomp.Ui;

namespace Pomp.Features
{
    public sealed class AgentRole
    {
        public string Name { get; set; } = string.Empty;
        public string Title { get; set; } = string.Empty;
        public string Job { get; set; } = string.Empty;
        public string Description { get; set; } = string.Empty;
        public string Instructions { get; set; } = string.Empty;
        public string? Model { get; set; }
        public string? ThinkingLevel { get; set; }
        public string Tools { get; set; } = "all"; // 'all' | 'readOnly' | 'custom'
        public string Scope { get; set; } = "project"; // 'project' | 'user' | 'bundled' | 'extension'
        public string? File { get; set; }
        public string? Hash { get; set; }
        public bool Editable { get; set; }
        public string? Error { get; set; }

        /// <summary>Why a usable role is read-only in POMP (it stays a team member candidate, unlike a role with <see cref="Error"/>).</summary>
        public string? Note { get; set; }

        /// <summary>An earlier entry of the same name wins in omp (user, then project, then bundled): this one is listed but never used.</summary>
        public bool Shadowed { get; set; }

        /// <summary>The picture the user gave the role (<see cref="AgentAvatars.Summary"/>: shape, color, image revision), or null.</summary>
        public JObject? Avatar { get; set; }

        internal AgentRole Copy() => (AgentRole)MemberwiseClone();
    }

    public sealed class AgentTeam
    {
        public string Id { get; set; } = string.Empty;
        public string Title { get; set; } = string.Empty;
        public string Goal { get; set; } = string.Empty;
        public List<string> Members { get; set; } = new List<string>();
        public string Mode { get; set; } = "lead"; // 'lead' | 'parallel' | 'pipeline'
        public string Instructions { get; set; } = string.Empty;
        public bool MembersOnly { get; set; } = true;

        /// <summary>Members work in isolated copies; their changes come back as review proposals (docs/architecture.md §8.11).</summary>
        public bool Isolation { get; set; }
        public string Scope { get; set; } = "project"; // 'project' | 'user'
        public string? File { get; set; }
        public string? Hash { get; set; }
        public List<string> Missing { get; set; } = new List<string>();
        public string? Error { get; set; }

        /// <summary>A project team of the same id wins (the host and the extension read the project file first): this one is never run.</summary>
        public bool Shadowed { get; set; }
    }

    public sealed class AgentsStatePayload
    {
        public string? Root { get; set; }
        public List<AgentRole> Roles { get; set; } = new List<AgentRole>();
        public List<AgentTeam> Teams { get; set; } = new List<AgentTeam>();
        public List<string> Diagnostics { get; set; } = new List<string>();

        /// <summary>Roles and teams left out because the message would pass the UI hub's size (no count limit; null when none were).</summary>
        public int? RolesHidden { get; set; }
        public int? TeamsHidden { get; set; }

        /// <summary>POMP's own role: the chat agent itself (Agents › 역할 › POMP; never deleted).</summary>
        public MainRoleState Main { get; set; } = new MainRoleState();
    }

    /// <summary>The chat agent as a role: its picture, and where the instructions the engine appends to it come from.</summary>
    public sealed class MainRoleState
    {
        /// <summary>The role name pictures and reports use for the chat agent (a role file can never take it).</summary>
        public const string Name = "main";

        /// <summary>The picture the user gave POMP (<see cref="AgentAvatars.Summary"/>), or null.</summary>
        public JObject? Avatar { get; set; }

        /// <summary>"project" or "user": the <see cref="MainRoleInstructions"/> file the engine takes; null when there is none.</summary>
        public string? Scope { get; set; }

        /// <summary>The running engine started with other instructions than the files hold now: they apply after a restart.</summary>
        public bool RestartRequired { get; set; }
    }

    /// <summary>
    /// Manages discovery, validation, saving and deletion of agent roles and teams (docs/architecture.md §8.1–§8.2, §8.7).
    /// </summary>
    public sealed class AgentRoster
    {
        private static readonly Regex ValidNameRegex = new Regex(@"^[a-z0-9-]{2,40}$", RegexOptions.Compiled);
        // A built-in role's name is allowed: a role file of that name replaces the built-in role for the engine (editing a built-in role).
        private static readonly HashSet<string> ProhibitedRoleNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "main", "sub"
        };
        private static readonly HashSet<string> ProhibitedTeamIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "main", "sub"
        };
        private static readonly HashSet<string> ReadOnlyToolSet = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "read", "find", "grep", "glob", "lsp", "yield"
        };

        private const int MaxFileSizeRoleBytes = 64 * 1024;
        private const int MaxFileSizeTeamBytes = 32 * 1024;

        private readonly string? _workspaceRoot;
        private readonly string _userHome;
        private readonly string _userTeamsDirectory;

        public AgentRoster(string? workspaceRoot, string? userHome = null, string? userTeamsDirectory = null)
        {
            _workspaceRoot = string.IsNullOrWhiteSpace(workspaceRoot) ? null : Path.GetFullPath(workspaceRoot);
            _userHome = !string.IsNullOrWhiteSpace(userHome)
                ? Path.GetFullPath(userHome!)
                : (Environment.GetFolderPath(Environment.SpecialFolder.UserProfile) ?? ".");
            _userTeamsDirectory = !string.IsNullOrWhiteSpace(userTeamsDirectory)
                ? Path.GetFullPath(userTeamsDirectory!)
                : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData) ?? ".", "Pomp", "teams");
        }

        public string? WorkspaceRoot => _workspaceRoot;
        public string UserHome => _userHome;
        public string UserTeamsDirectory => _userTeamsDirectory;

        public string? ProjectRolesDirectory => _workspaceRoot != null ? Path.Combine(ProjectData.ProjectFolder(_workspaceRoot), "agents") : null;
        /// <summary>Where an earlier POMP kept project roles: the engine's own project folder.</summary>
        private string? LegacyProjectRolesDirectory => _workspaceRoot != null ? Path.Combine(_workspaceRoot, ".omp", "agents") : null;
        public string UserRolesDirectory => Path.Combine(_userHome, ".omp", "agent", "agents");
        /// <summary>The instructions of POMP's own role, in this workspace and for the user.</summary>
        public MainRoleInstructions MainInstructions() => new MainRoleInstructions(_workspaceRoot, _userHome);
        public string? ProjectTeamsDirectory => _workspaceRoot != null ? Path.Combine(_workspaceRoot, ".pomp", "teams") : null;

        public static IReadOnlyList<AgentRole> BundledRoles { get; } = new List<AgentRole>
        {
            new AgentRole
            {
                Name = "task",
                Title = "Task",
                Job = "General-purpose delegated tasks",
                Description = "General-purpose subagent with full capabilities for delegated multi-step tasks",
                Model = "@task",
                Tools = "all",
                Scope = "bundled",
                Editable = false
            },
            new AgentRole
            {
                Name = "sonic",
                Title = "Sonic",
                Job = "Mechanical updates and data collection",
                Description = "Low-reasoning agent for strictly mechanical updates or data collection only",
                Model = "@smol",
                ThinkingLevel = "medium",
                Tools = "all",
                Scope = "bundled",
                Editable = false
            },
            new AgentRole
            {
                Name = "scout",
                Title = "Scout",
                Job = "Codebase exploration and research",
                Description = "MUST be used for exploratory codebase research, rapid code analysis, and broad pattern searches. Fast read-only scout returning compressed context for handoff.",
                Model = "@smol",
                ThinkingLevel = "medium",
                Tools = "custom",
                Scope = "bundled",
                Editable = false
            },
            new AgentRole
            {
                Name = "reviewer",
                Title = "Reviewer",
                Job = "Code review specialist",
                Description = "Code review specialist for quality/security analysis",
                Model = "@slow",
                Tools = "custom",
                Scope = "bundled",
                Editable = false
            },
            new AgentRole
            {
                Name = "security-reviewer",
                Title = "Security Reviewer",
                Job = "Security and vulnerability specialist",
                Description = "Read-only security specialist for evidence-backed repository vulnerability discovery",
                Tools = "custom",
                Scope = "bundled",
                Editable = false
            }
        };

        /// <summary>The pictures of this project's roles (deleting <c>.pomp</c> resets them).</summary>
        private string? ProjectAvatarsFile => _workspaceRoot != null ? Path.Combine(ProjectData.LocalFolder(_workspaceRoot), "avatars.json") : null;
        /// <summary>The pictures of the user's roles and of POMP's own: next to the user's teams.</summary>
        private string UserAvatarsFile => Path.Combine(Path.GetDirectoryName(Path.GetFullPath(_userTeamsDirectory))!, "avatars.json");

        private string AvatarsFileFor(string scope) => scope == "project"
            ? ProjectAvatarsFile ?? throw new InvalidOperationException(PompLanguage.Pick("작업 영역이 열려 있지 않아요.", "No workspace is open."))
            : scope == "user" || scope == "bundled" || scope == "extension" ? UserAvatarsFile
            : throw new ArgumentException(PompLanguage.Pick($"알 수 없는 범위예요: '{scope}'", $"Unknown scope: '{scope}'"), nameof(scope));

        /// <summary>Sets the picture of a role (shape, color, optional image), or with null gives it back its letter in a circle.</summary>
        public void SetAvatar(string scope, string name, JObject? avatar)
        {
            if (!IsValidName(name)) throw new ArgumentException(PompLanguage.Pick("역할 이름 형식이 올바르지 않아요.", "Role name is invalid."), nameof(name));
            var file = AvatarsFileFor(scope);
            if (scope == "project") ProjectData.Prepare(_workspaceRoot!);
            AgentAvatars.Write(file, name, avatar);
        }

        /// <summary>The images of the asked roles (<c>"scope:name"</c> → data URL), up to about <paramref name="budgetChars"/> characters.</summary>
        public JObject AvatarImages(IEnumerable<(string Scope, string Name)> roles, int budgetChars)
        {
            var stores = new Dictionary<string, JObject>(StringComparer.OrdinalIgnoreCase);
            var images = new JObject();
            var used = 0;
            foreach (var (scope, name) in roles.Distinct())
            {
                string file;
                try { file = AvatarsFileFor(scope); }
                catch (Exception ex) when (ex is InvalidOperationException || ex is ArgumentException) { continue; }
                if (!stores.TryGetValue(file, out var store)) stores[file] = store = AgentAvatars.Read(file);
                if (!(store[name]?["image"] is JValue image) || image.Type != JTokenType.String) continue;
                var data = (string)image!;
                if (used + data.Length > budgetChars && used > 0) break;
                images[scope + ":" + name] = data;
                used += data.Length;
            }
            return images;
        }

        public IReadOnlyList<AgentRole> ListRoles()
        {
            var results = new List<AgentRole>();
            MoveLegacyProjectRoles();

            // omp reads the user's roles before the project's: those reach it through a plugin folder (SyncEngineAgents), which ranks after them.
            // 1. User roles
            if (Directory.Exists(UserRolesDirectory))
            {
                results.AddRange(LoadRolesFromDirectory(UserRolesDirectory, "user", null));
            }

            // 2. Project roles
            if (ProjectRolesDirectory != null && Directory.Exists(ProjectRolesDirectory))
            {
                results.AddRange(LoadRolesFromDirectory(ProjectRolesDirectory, "project", _workspaceRoot));
            }

            // 3. Bundled roles
            results.AddRange(BundledRoles);

            // omp runs the first usable definition of a name (user, project, bundled); a file omp cannot parse hides nothing.
            // Bundled roles are shared instances, so marked entries are copies.
            var seen = new HashSet<string>(StringComparer.Ordinal);
            for (var i = 0; i < results.Count; i++)
            {
                if (!string.IsNullOrEmpty(results[i].Error) || seen.Add(results[i].Name)) continue;
                var copy = (AgentRole)results[i].Copy();
                copy.Shadowed = true;
                results[i] = copy;
            }

            return results;
        }

        public IReadOnlyList<AgentTeam> ListTeams(IReadOnlyList<AgentRole>? roles = null)
        {
            roles ??= ListRoles();
            var knownRoleNames = new HashSet<string>(roles.Where(r => string.IsNullOrEmpty(r.Error)).Select(r => r.Name), StringComparer.OrdinalIgnoreCase);

            var results = new List<AgentTeam>();

            // 1. Project teams
            if (ProjectTeamsDirectory != null && Directory.Exists(ProjectTeamsDirectory))
            {
                results.AddRange(LoadTeamsFromDirectory(ProjectTeamsDirectory, "project", _workspaceRoot, knownRoleNames));
            }

            // 2. User teams
            if (Directory.Exists(UserTeamsDirectory))
            {
                results.AddRange(LoadTeamsFromDirectory(UserTeamsDirectory, "user", null, knownRoleNames));
            }

            // The project file of an id is the one read (team files live on a case-insensitive file system), even a broken one: the host
            // and the extension then refuse the team rather than fall back to the user's file.
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var team in results)
                if (!seen.Add(team.Id)) team.Shadowed = true;

            return results;
        }

        public AgentsStatePayload GetState()
        {
            var roles = ListRoles();
            var teams = ListTeams(roles);
            var diagnostics = new List<string>();

            foreach (var r in roles)
            {
                if (!string.IsNullOrEmpty(r.Error))
                {
                    diagnostics.Add(PompLanguage.Pick($"역할 '{r.Name}' 오류: {r.Error}", $"Role '{r.Name}' error: {r.Error}"));
                }
            }
            foreach (var t in teams)
            {
                if (!string.IsNullOrEmpty(t.Error))
                {
                    diagnostics.Add(PompLanguage.Pick($"팀 '{t.Id}' 오류: {t.Error}", $"Team '{t.Id}' error: {t.Error}"));
                }
                else if (t.Missing.Count > 0)
                {
                    diagnostics.Add(PompLanguage.Pick($"팀 '{t.Id}'에 누락된 구성원이 있어요: {string.Join(", ", t.Missing)}", $"Team '{t.Id}' has missing members: {string.Join(", ", t.Missing)}"));
                }
            }

            var projectAvatars = AgentAvatars.Read(ProjectAvatarsFile);
            var userAvatars = AgentAvatars.Read(UserAvatarsFile);
            foreach (var role in roles)
                role.Avatar = AgentAvatars.Summary((role.Scope == "project" ? projectAvatars : userAvatars)[role.Name] as JObject);

            var state = new AgentsStatePayload
            {
                Root = _workspaceRoot,
                Roles = roles.ToList(),
                Teams = teams.ToList(),
                Diagnostics = diagnostics,
                Main = new MainRoleState { Avatar = AgentAvatars.Summary(userAvatars[MainRoleState.Name] as JObject), Scope = MainInstructions().Effective()?.Scope },
            };
            // No count limit: every role and team is sent while the message fits the UI hub (StateBudget); the rest are counted, never silently dropped.
            AgentsStatePayload Without(bool roles, bool teams) => new AgentsStatePayload { Root = state.Root, Roles = roles ? state.Roles : new List<AgentRole>(), Teams = teams ? state.Teams : new List<AgentTeam>(), Diagnostics = state.Diagnostics, Main = state.Main };
            var rolesLeft = StateBudget.Fit(state.Roles, StateBudget.Bytes(new UiMessage(UiMessageTypes.AgentsState, Without(false, true))) + 2048);
            var teamsLeft = StateBudget.Fit(state.Teams, StateBudget.Bytes(new UiMessage(UiMessageTypes.AgentsState, Without(true, false))) + 2048);
            if (rolesLeft > 0) state.RolesHidden = rolesLeft;
            if (teamsLeft > 0) state.TeamsHidden = teamsLeft;
            return state;
        }

        /// <summary>
        /// Writes a role file. <paramref name="builtinDefinition"/> is the engine's own definition of the built-in role of that name
        /// (<see cref="BuiltinRoleForm"/>): a new file starts from it, so what the form does not show (its output schema, the roles it may
        /// call, its tool list) stays as the engine had it.
        /// </summary>
        public AgentRole SaveRole(string scope, JObject roleObj, string? expectedHash, string? builtinDefinition = null)
        {
            if (roleObj == null) throw new ArgumentNullException(nameof(roleObj));
            ValidateRoleContent(roleObj);
            var dir = GetRoleDirectory(scope, forWrite: true);

            var name = ((string?)roleObj["name"])?.Trim() ?? string.Empty;
            var fields = RoleFields(roleObj);

            var targetFile = Path.Combine(dir, name + ".md");
            VerifyPathInsideScope(dir, targetFile);
            CheckpointStore.CheckDirectPath(dir);
            if (File.Exists(targetFile))
            {
                CheckpointStore.CheckDirectPath(targetFile);
            }

            bool exists = File.Exists(targetFile);
            if (exists)
            {
                if (string.IsNullOrEmpty(expectedHash))
                {
                    throw new InvalidOperationException(PompLanguage.Pick("expectedHash가 없어요.", "expectedHash is missing."));
                }
                var currentHash = ComputeFileHash(targetFile);
                if (!string.Equals(currentHash, expectedHash, StringComparison.OrdinalIgnoreCase))
                {
                    throw new InvalidOperationException(PompLanguage.Pick("파일이 다른 프로세스에 의해 변경되었어요 (해시 불일치).", "File was modified by another process (hash mismatch)."));
                }

                // A file POMP lists read-only (multi-line YAML, an error) is never rewritten, whatever the page sends.
                var current = ParseRoleFile(targetFile, scope, _workspaceRoot);
                if (!current.Editable)
                    throw new InvalidOperationException(current.Note ?? current.Error ?? PompLanguage.Pick("이 역할 파일은 POMP에서 고칠 수 없어요.", "POMP cannot edit this role file."));
            }
            else
            {
                if (!string.IsNullOrEmpty(expectedHash))
                {
                    throw new InvalidOperationException(PompLanguage.Pick("새 파일에 expectedHash가 지정되었어요.", "expectedHash was given for a new file."));
                }
            }

            string? baseText = null;
            if (exists)
            {
                try { baseText = File.ReadAllText(targetFile, Encoding.UTF8); }
                catch (IOException) { }
                catch (UnauthorizedAccessException) { }
            }
            else if (builtinDefinition != null && IsBundledRoleName(name))
            {
                baseText = builtinDefinition;
            }

            string content = BuildRoleMarkdown(baseText, name, fields.Title, fields.Job, fields.Description, fields.Model, fields.ThinkingLevel, fields.Tools, fields.CustomToolsArray, fields.Instructions);
            var byteCount = Encoding.UTF8.GetByteCount(content);
            if (byteCount > MaxFileSizeRoleBytes)
            {
                throw new InvalidOperationException(PompLanguage.Pick("역할 파일이 너무 커요.", "Role file is too large."));
            }

            AtomicFile.WriteAllText(targetFile, content);
            if (dir == ProjectRolesDirectory) RefreshEngineAgents();

            return ParseRoleFile(targetFile, scope, _workspaceRoot);
        }

        public void DeleteRole(string scope, string name, string? expectedHash)
        {
            ValidateRoleName(name);
            var dir = GetRoleDirectory(scope, forWrite: true);
            var targetFile = Path.Combine(dir, name + ".md");
            VerifyPathInsideScope(dir, targetFile);
            CheckpointStore.CheckDirectPath(targetFile);

            if (!File.Exists(targetFile))
            {
                throw new FileNotFoundException(PompLanguage.Pick("삭제할 역할 파일을 찾을 수 없어요.", "Role file to delete was not found."), targetFile);
            }

            if (string.IsNullOrEmpty(expectedHash))
            {
                throw new InvalidOperationException(PompLanguage.Pick("expectedHash가 없어요.", "expectedHash is missing."));
            }

            var currentHash = ComputeFileHash(targetFile);
            if (!string.Equals(currentHash, expectedHash, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException(PompLanguage.Pick("파일이 다른 프로세스에 의해 변경되었어요 (해시 불일치).", "File was modified by another process (hash mismatch)."));
            }

            File.Delete(targetFile);
            if (dir == ProjectRolesDirectory) RefreshEngineAgents();
        }

        public AgentTeam SaveTeam(string scope, JObject teamObj, string? expectedHash)
        {
            if (teamObj == null) throw new ArgumentNullException(nameof(teamObj));
            ValidateTeamContent(teamObj);
            var dir = GetTeamDirectory(scope, forWrite: true);

            var id = ((string?)teamObj["id"])?.Trim() ?? string.Empty;
            var fields = TeamFields(teamObj);

            var targetFile = Path.Combine(dir, id + ".json");
            VerifyPathInsideScope(dir, targetFile);
            CheckpointStore.CheckDirectPath(dir);
            if (File.Exists(targetFile))
            {
                CheckpointStore.CheckDirectPath(targetFile);
            }

            bool exists = File.Exists(targetFile);
            if (exists)
            {
                if (string.IsNullOrEmpty(expectedHash))
                {
                    throw new InvalidOperationException(PompLanguage.Pick("expectedHash가 없어요.", "expectedHash is missing."));
                }
                var currentHash = ComputeFileHash(targetFile);
                if (!string.Equals(currentHash, expectedHash, StringComparison.OrdinalIgnoreCase))
                {
                    throw new InvalidOperationException(PompLanguage.Pick("파일이 다른 프로세스에 의해 변경되었어요 (해시 불일치).", "File was modified by another process (hash mismatch)."));
                }
            }
            else
            {
                if (!string.IsNullOrEmpty(expectedHash))
                {
                    throw new InvalidOperationException(PompLanguage.Pick("새 파일에 expectedHash가 지정되었어요.", "expectedHash was given for a new file."));
                }
            }

            var doc = new JObject
            {
                ["version"] = 1,
                ["id"] = id,
                ["title"] = fields.Title,
                ["goal"] = fields.Goal,
                ["members"] = new JArray(fields.Members),
                ["mode"] = fields.Mode,
                ["instructions"] = fields.Instructions,
                ["membersOnly"] = fields.MembersOnly
            };
            // Written only when on: files of teams without isolation stay byte-identical to before.
            if (fields.Isolation) doc["isolation"] = true;

            var json = doc.ToString(Formatting.Indented);
            var byteCount = Encoding.UTF8.GetByteCount(json);
            if (byteCount > MaxFileSizeTeamBytes)
            {
                throw new InvalidOperationException(PompLanguage.Pick("팀 파일이 너무 커요.", "Team file is too large."));
            }

            AtomicFile.WriteAllText(targetFile, json);

            var roles = ListRoles();
            var knownRoleNames = new HashSet<string>(roles.Where(r => string.IsNullOrEmpty(r.Error)).Select(r => r.Name), StringComparer.OrdinalIgnoreCase);
            return ParseTeamFile(targetFile, scope, _workspaceRoot, knownRoleNames);
        }

        public void DeleteTeam(string scope, string id, string? expectedHash)
        {
            ValidateTeamId(id);
            var dir = GetTeamDirectory(scope, forWrite: true);
            var targetFile = Path.Combine(dir, id + ".json");
            VerifyPathInsideScope(dir, targetFile);
            CheckpointStore.CheckDirectPath(targetFile);

            if (!File.Exists(targetFile))
            {
                throw new FileNotFoundException(PompLanguage.Pick("삭제할 팀 파일을 찾을 수 없어요.", "Team file to delete was not found."), targetFile);
            }

            if (string.IsNullOrEmpty(expectedHash))
            {
                throw new InvalidOperationException(PompLanguage.Pick("expectedHash가 없어요.", "expectedHash is missing."));
            }

            var currentHash = ComputeFileHash(targetFile);
            if (!string.Equals(currentHash, expectedHash, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException(PompLanguage.Pick("파일이 다른 프로세스에 의해 변경되었어요 (해시 불일치).", "File was modified by another process (hash mismatch)."));
            }

            File.Delete(targetFile);
        }

        /// <summary>
        /// Moves the project roles an earlier POMP kept in the engine's project folder (<c>.omp/agents</c>) into <c>.pomp/agents</c>, never over a
        /// file of the same name (that one stays where it was), and removes the old folders once they are empty.
        /// </summary>
        internal void MoveLegacyProjectRoles()
        {
            var legacy = LegacyProjectRolesDirectory; var target = ProjectRolesDirectory;
            if (legacy == null || target == null || !Directory.Exists(legacy)) return;
            try
            {
                CheckpointStore.CheckDirectPath(legacy); CheckpointStore.CheckDirectPath(target);
                Directory.CreateDirectory(target);
                foreach (var file in Directory.EnumerateFiles(legacy, "*.md").ToList())
                {
                    var moved = Path.Combine(target, Path.GetFileName(file));
                    if ((File.GetAttributes(file) & FileAttributes.ReparsePoint) != 0 || File.Exists(moved)) continue;
                    File.Move(file, moved);
                }
                if (!Directory.EnumerateFileSystemEntries(legacy).Any()) Directory.Delete(legacy);
                var engineFolder = Path.GetDirectoryName(legacy)!;
                if (!Directory.EnumerateFileSystemEntries(engineFolder).Any()) Directory.Delete(engineFolder);
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }

        /// <summary>
        /// The engine's copy of the project roles: <c>.pomp/local/engine-plugin/agents</c>, given to omp as a plugin folder (<c>--plugin-dir</c>).
        /// omp would load skills and commands too from a plugin folder, so it never gets <c>.pomp</c> itself: only the role files, direct and
        /// bounded like the roster reads them, are copied, and copies whose role is gone are removed. Returns the plugin folder, or null
        /// without a workspace.
        /// </summary>
        internal string? SyncEngineAgents()
        {
            if (_workspaceRoot == null || ProjectRolesDirectory == null) return null;
            MoveLegacyProjectRoles();
            var plugin = ProjectData.Folder(_workspaceRoot, "engine-plugin");
            var agents = Path.Combine(plugin, "agents");
            CheckpointStore.CheckDirectPath(plugin);
            Directory.CreateDirectory(plugin);
            // omp loads skills, commands, hooks and more from a plugin folder: anything but the role copies (a repository could commit
            // some there) is removed before omp gets the folder.
            foreach (var entry in Directory.EnumerateFileSystemEntries(plugin).ToList())
                if (!string.Equals(Path.GetFileName(entry), "agents", StringComparison.OrdinalIgnoreCase) || (File.GetAttributes(entry) & (FileAttributes.Directory | FileAttributes.ReparsePoint)) != FileAttributes.Directory) RemoveEntry(entry);
            Directory.CreateDirectory(agents);
            foreach (var entry in Directory.EnumerateDirectories(agents).ToList()) RemoveEntry(entry);
            var wanted = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (Directory.Exists(ProjectRolesDirectory))
            {
                CheckpointStore.CheckDirectPath(ProjectRolesDirectory);
                foreach (var file in Directory.EnumerateFiles(ProjectRolesDirectory, "*.md").OrderBy(f => f, StringComparer.OrdinalIgnoreCase))
                {
                    var info = new FileInfo(file);
                    if ((info.Attributes & FileAttributes.ReparsePoint) != 0 || info.Length > MaxFileSizeRoleBytes) continue;
                    var copy = Path.Combine(agents, info.Name);
                    wanted.Add(info.Name);
                    if (File.Exists(copy) && (File.GetAttributes(copy) & FileAttributes.ReparsePoint) != 0) RemoveEntry(copy);
                    var text = File.ReadAllText(file);
                    if (!File.Exists(copy) || File.ReadAllText(copy) != text) AtomicFile.WriteAllText(copy, text);
                }
            }
            foreach (var stale in Directory.EnumerateFiles(agents).Where(f => !wanted.Contains(Path.GetFileName(f)) || (File.GetAttributes(f) & FileAttributes.ReparsePoint) != 0).ToList()) RemoveEntry(stale);
            return plugin;
        }

        /// <summary>Deletes a file or a folder tree; a link (file or folder) is removed itself, never what it points to.</summary>
        private static void RemoveEntry(string path)
        {
            var attributes = File.GetAttributes(path);
            if ((attributes & FileAttributes.Directory) == 0)
            {
                if ((attributes & (FileAttributes.ReadOnly | FileAttributes.ReparsePoint)) == FileAttributes.ReadOnly) File.SetAttributes(path, attributes & ~FileAttributes.ReadOnly);
                File.Delete(path);
                return;
            }
            if ((attributes & FileAttributes.ReparsePoint) == 0)
                foreach (var child in Directory.EnumerateFileSystemEntries(path).ToList()) RemoveEntry(child);
            Directory.Delete(path, false);
        }

        /// <summary>A saved or deleted project role reaches the engine's copy right away (omp reads its agents again before each task).</summary>
        private void RefreshEngineAgents()
        {
            try { SyncEngineAgents(); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }

        private string GetRoleDirectory(string scope, bool forWrite)
        {
            if (string.Equals(scope, "project", StringComparison.OrdinalIgnoreCase))
            {
                if (ProjectRolesDirectory == null)
                {
                    throw new InvalidOperationException(PompLanguage.Pick("작업 영역이 열려 있지 않아 프로젝트 역할을 저장할 수 없어요.", "Cannot access project roles without an active workspace."));
                }
                MoveLegacyProjectRoles();
                if (forWrite && !Directory.Exists(ProjectRolesDirectory))
                {
                    CheckpointStore.CheckDirectPath(ProjectRolesDirectory);
                    Directory.CreateDirectory(ProjectRolesDirectory);
                }
                return ProjectRolesDirectory;
            }
            if (string.Equals(scope, "user", StringComparison.OrdinalIgnoreCase))
            {
                if (forWrite && !Directory.Exists(UserRolesDirectory))
                {
                    CheckpointStore.CheckDirectPath(UserRolesDirectory);
                    Directory.CreateDirectory(UserRolesDirectory);
                }
                return UserRolesDirectory;
            }
            throw new ArgumentException(PompLanguage.Pick($"알 수 없는 범위예요: '{scope}'", $"Unknown scope: '{scope}'"), nameof(scope));
        }

        private string GetTeamDirectory(string scope, bool forWrite)
        {
            if (string.Equals(scope, "project", StringComparison.OrdinalIgnoreCase))
            {
                if (ProjectTeamsDirectory == null)
                {
                    throw new InvalidOperationException(PompLanguage.Pick("작업 영역이 열려 있지 않아 프로젝트 팀을 저장할 수 없어요.", "Cannot access project teams without an active workspace."));
                }
                if (forWrite && !Directory.Exists(ProjectTeamsDirectory))
                {
                    CheckpointStore.CheckDirectPath(ProjectTeamsDirectory);
                    Directory.CreateDirectory(ProjectTeamsDirectory);
                }
                return ProjectTeamsDirectory;
            }
            if (string.Equals(scope, "user", StringComparison.OrdinalIgnoreCase))
            {
                if (forWrite && !Directory.Exists(UserTeamsDirectory))
                {
                    CheckpointStore.CheckDirectPath(UserTeamsDirectory);
                    Directory.CreateDirectory(UserTeamsDirectory);
                }
                return UserTeamsDirectory;
            }
            throw new ArgumentException(PompLanguage.Pick($"알 수 없는 범위예요: '{scope}'", $"Unknown scope: '{scope}'"), nameof(scope));
        }

        /// <summary>The name shape omp agents and POMP roles share (bundled names included).</summary>
        public static bool IsValidName(string name) => ValidNameRegex.IsMatch(name);

        /// <summary>Validated role fields without touching the file system (docs/architecture.md §8.13 shares SaveRole's rules).</summary>
        internal sealed class ValidatedRole
        {
            public string Name { get; set; } = string.Empty;
            public string? Title { get; set; }
            public string? Job { get; set; }
            public string Description { get; set; } = string.Empty;
            public string Instructions { get; set; } = string.Empty;
            public string? Model { get; set; }
            public string? ThinkingLevel { get; set; }
            public string Tools { get; set; } = "all";
            public JArray? CustomToolsArray { get; set; }
        }

        /// <summary>Validated team fields without touching the file system (docs/architecture.md §8.13 shares SaveTeam's rules).</summary>
        internal sealed class ValidatedTeam
        {
            public string Id { get; set; } = string.Empty;
            public string Title { get; set; } = string.Empty;
            public string Goal { get; set; } = string.Empty;
            public List<string> Members { get; set; } = new List<string>();
            public string Mode { get; set; } = "lead";
            public string Instructions { get; set; } = string.Empty;
            public bool MembersOnly { get; set; } = true;
            public bool Isolation { get; set; }
        }

        /// <summary>Same checks SaveRole applies to the object's fields: name, lengths, model, thinking level and tools.</summary>
        internal static ValidatedRole ValidateRoleContent(JObject roleObj)
        {
            if (roleObj == null) throw new ArgumentNullException(nameof(roleObj));
            var name = ((string?)roleObj["name"])?.Trim() ?? string.Empty;
            ValidateRoleName(name);

            var title = ((string?)roleObj["title"] ?? (string?)roleObj["pomp-title"])?.Trim();
            if (title != null && title.Length > 40)
            {
                throw new InvalidOperationException(PompLanguage.Pick("역할 제목이 너무 길어요.", "Role title is too long."));
            }

            var job = ((string?)roleObj["job"] ?? (string?)roleObj["pomp-job"])?.Trim();
            if (job != null && job.Length > 80)
            {
                throw new InvalidOperationException(PompLanguage.Pick("역할 작업이 너무 길어요.", "Role job is too long."));
            }

            var description = ((string?)roleObj["description"])?.Trim() ?? string.Empty;
            if (string.IsNullOrEmpty(description))
            {
                throw new InvalidOperationException(PompLanguage.Pick("역할 설명(description)이 비어 있어요.", "Role description is empty."));
            }
            if (description.Length > 1000)
            {
                throw new InvalidOperationException(PompLanguage.Pick("역할 설명이 너무 길어요.", "Role description is too long."));
            }

            var instructions = (string?)roleObj["instructions"] ?? string.Empty;
            if (instructions.Length > 24000)
            {
                throw new InvalidOperationException(PompLanguage.Pick("역할 지시 본문이 너무 길어요.", "Role instructions are too long."));
            }

            var model = ((string?)roleObj["model"])?.Trim();
            if (model != null && (model.Length > 200 || model.Any(char.IsControl)))
                throw new InvalidOperationException(PompLanguage.Pick("모델 이름이 올바르지 않아요.", "The model name is not valid."));
            var thinkingLevel = ((string?)roleObj["thinkingLevel"] ?? (string?)roleObj["thinking-level"] ?? (string?)roleObj["thinking"])?.Trim();
            if (!string.IsNullOrEmpty(thinkingLevel) && Array.IndexOf(new[] { "auto", "off", "minimal", "low", "medium", "high", "xhigh", "max" }, thinkingLevel) < 0)
                throw new InvalidOperationException(PompLanguage.Pick("추론 단계가 올바르지 않아요.", "The thinking level is not valid."));

            var tools = ((string?)roleObj["tools"])?.Trim() ?? "all";
            JArray? customToolsArray = roleObj["tools"] as JArray;

            return new ValidatedRole
            {
                Name = name,
                Title = title,
                Job = job,
                Description = description,
                Instructions = instructions,
                Model = model,
                ThinkingLevel = thinkingLevel,
                Tools = tools,
                CustomToolsArray = customToolsArray
            };
        }

        /// <summary>Same checks SaveTeam applies to the object's fields: id, lengths, members, mode, flags.</summary>
        internal static ValidatedTeam ValidateTeamContent(JObject teamObj)
        {
            if (teamObj == null) throw new ArgumentNullException(nameof(teamObj));
            var id = ((string?)teamObj["id"])?.Trim() ?? string.Empty;
            ValidateTeamId(id);

            var title = ((string?)teamObj["title"])?.Trim() ?? string.Empty;
            if (title.Length > 40)
            {
                throw new InvalidOperationException(PompLanguage.Pick("팀 제목이 너무 길어요.", "Team title is too long."));
            }

            var goal = ((string?)teamObj["goal"])?.Trim() ?? string.Empty;
            if (goal.Length > 400)
            {
                throw new InvalidOperationException(PompLanguage.Pick("팀 목표가 너무 길어요.", "Team goal is too long."));
            }

            var instructions = (string?)teamObj["instructions"] ?? string.Empty;
            if (instructions.Length > 8000)
            {
                throw new InvalidOperationException(PompLanguage.Pick("팀 공통 지시가 너무 길어요.", "Team instructions are too long."));
            }

            var membersArray = teamObj["members"] as JArray;
            if (membersArray == null || membersArray.Count < 2)
            {
                throw new InvalidOperationException(PompLanguage.Pick("팀 구성원이 너무 적어요.", "Too few team members."));
            }

            var members = new List<string>();
            foreach (var m in membersArray!)
            {
                var memberName = (string?)m;
                if (string.IsNullOrWhiteSpace(memberName))
                {
                    throw new InvalidOperationException(PompLanguage.Pick("유효하지 않은 구성원 이름이 포함되어 있어요.", "Invalid member name in team."));
                }
                memberName = memberName!.Trim();
                if (!ValidNameRegex.IsMatch(memberName))
                {
                    throw new InvalidOperationException(PompLanguage.Pick($"구성원 이름 '{memberName}'이(가) 형식에 맞지 않아요.", $"Member name '{memberName}' is invalid."));
                }
                if (members.Contains(memberName, StringComparer.OrdinalIgnoreCase))
                {
                    throw new InvalidOperationException(PompLanguage.Pick($"팀 구성원에 중복된 이름 '{memberName}'이(가) 있어요.", $"Duplicate member name '{memberName}' in team."));
                }
                members.Add(memberName);
            }

            var mode = ((string?)teamObj["mode"])?.Trim()?.ToLowerInvariant() ?? "lead";
            if (mode != "lead" && mode != "parallel" && mode != "pipeline")
            {
                mode = "lead";
            }

            var membersOnly = teamObj["membersOnly"] == null || teamObj["membersOnly"]!.Type == JTokenType.Null || (bool)teamObj["membersOnly"]!;
            if (teamObj["isolation"] != null && teamObj["isolation"]!.Type != JTokenType.Null && teamObj["isolation"]!.Type != JTokenType.Boolean)
            {
                throw new InvalidOperationException(PompLanguage.Pick("격리 설정 형식이 올바르지 않아요.", "Isolation setting is invalid."));
            }
            var isolation = teamObj["isolation"]?.Type == JTokenType.Boolean && (bool)teamObj["isolation"]!;

            return new ValidatedTeam
            {
                Id = id,
                Title = title,
                Goal = goal,
                Members = members,
                Mode = mode,
                Instructions = instructions,
                MembersOnly = membersOnly,
                Isolation = isolation
            };
        }

        /// <summary>Same fields SaveRole writes, usable without a scope directory existing yet.</summary>
        internal static ValidatedRole RoleFields(JObject roleObj) => ValidateRoleContent(roleObj);

        /// <summary>Same fields SaveTeam writes, usable without a scope directory existing yet.</summary>
        internal static ValidatedTeam TeamFields(JObject teamObj) => ValidateTeamContent(teamObj);

        /// <summary>Target role file for template import: path rules only, the file must already exist for preview comparison.</summary>
        internal string ExistingRoleFile(string scope, string name)
        {
            ValidateRoleName(name);
            var dir = GetRoleDirectory(scope, forWrite: false);
            var path = Path.Combine(dir, name + ".md");
            VerifyPathInsideScope(dir, path);
            return path;
        }

        /// <summary>Target team file for template import: path rules only, the file must already exist for preview comparison.</summary>
        internal string ExistingTeamFile(string scope, string id)
        {
            ValidateTeamId(id);
            var dir = GetTeamDirectory(scope, forWrite: false);
            var path = Path.Combine(dir, id + ".json");
            VerifyPathInsideScope(dir, path);
            return path;
        }

        /// <summary>
        /// The role file the VS editor should open for a project/user role the in-page form cannot edit.
        /// Validates the name with the roster's own rules, builds the path from the scope directory, and refuses reserved names and
        /// missing files (a built-in role has a file only once it was edited: the file of its name replaces it).
        /// </summary>
        public string FileOf(string scope, string name)
        {
            var trimmed = (name ?? "").Trim();
            ValidateRoleName(trimmed);
            var dir = GetRoleDirectory(scope, forWrite: false);
            var path = Path.Combine(dir, trimmed + ".md");
            VerifyPathInsideScope(dir, path);
            CheckpointStore.CheckDirectPath(path);
            if (!File.Exists(path))
                throw new FileNotFoundException(PompLanguage.Pick("역할 파일을 찾을 수 없어요.", "Role file was not found."), path);
            return path;
        }

        /// <summary>Parsed role used for template import: 'same' means every field matches, not the bytes.</summary>
        internal AgentRole ReadRoleFile(string filePath, string scope) => ParseRoleFile(filePath, scope, _workspaceRoot);

        /// <summary>Parsed team used for template import: 'same' means every field matches, not the bytes.</summary>
        internal AgentTeam ReadTeamFile(string filePath, string scope)
        {
            var known = new HashSet<string>(ListRoles().Where(r => string.IsNullOrEmpty(r.Error)).Select(r => r.Name), StringComparer.OrdinalIgnoreCase);
            return ParseTeamFile(filePath, scope, _workspaceRoot, known);
        }

        /// <summary>Bundled role names (docs/architecture.md §8.13): templates reference them by name only, never carry them.</summary>
        internal static bool IsBundledRoleName(string name) =>
            BundledRoles.Any(r => string.Equals(r.Name, name, StringComparison.OrdinalIgnoreCase));

        /// <summary>Whether the local lists resolve this name anywhere (docs/architecture.md §8.13 'existing').</summary>
        internal bool HasUsableRole(string name) =>
            ListRoles().Any(r => string.IsNullOrEmpty(r.Error) && string.Equals(r.Name, name, StringComparison.OrdinalIgnoreCase));

        public static void ValidateRoleName(string name)
        {
            if (string.IsNullOrWhiteSpace(name))
            {
                throw new ArgumentException(PompLanguage.Pick("역할 이름이 비어 있어요.", "Role name is empty."), nameof(name));
            }
            if (!ValidNameRegex.IsMatch(name))
            {
                throw new ArgumentException(PompLanguage.Pick("역할 이름 형식이 올바르지 않아요.", "Role name is invalid."), nameof(name));
            }
            if (ProhibitedRoleNames.Contains(name))
            {
                throw new ArgumentException(PompLanguage.Pick($"'{name}'은(는) 예약된 이름이라 역할 이름으로 쓸 수 없어요.", $"'{name}' is a reserved name and cannot be used as a role name."), nameof(name));
            }
        }

        public static void ValidateTeamId(string id)
        {
            if (string.IsNullOrWhiteSpace(id))
            {
                throw new ArgumentException(PompLanguage.Pick("팀 ID가 비어 있어요.", "Team ID is empty."), nameof(id));
            }
            if (!ValidNameRegex.IsMatch(id))
            {
                throw new ArgumentException(PompLanguage.Pick("팀 ID 형식이 올바르지 않아요.", "Team ID is invalid."), nameof(id));
            }
            if (ProhibitedTeamIds.Contains(id))
            {
                throw new ArgumentException(PompLanguage.Pick($"'{id}'은(는) 예약된 이름이라 팀 ID로 쓸 수 없어요.", $"'{id}' is a reserved ID and cannot be used as a team ID."), nameof(id));
            }
        }

        private static void VerifyPathInsideScope(string scopeDir, string filePath)
        {
            var fullScope = Path.GetFullPath(scopeDir).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            var fullFile = Path.GetFullPath(filePath);
            var prefix = fullScope + Path.DirectorySeparatorChar;
            if (!fullFile.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException(PompLanguage.Pick("지정된 경로가 대상 범위 폴더를 벗어납니다.", "Target file is outside the scope directory."));
            }
        }

        private List<AgentRole> LoadRolesFromDirectory(string dir, string scope, string? workspaceRoot)
        {
            var list = new List<AgentRole>();
            try
            {
                CheckpointStore.CheckDirectPath(dir);
                foreach (var file in Directory.EnumerateFiles(dir, "*.md").OrderBy(f => f, StringComparer.OrdinalIgnoreCase))
                {
                    try
                    {
                        CheckpointStore.CheckDirectPath(file);
                        list.Add(ParseRoleFile(file, scope, workspaceRoot));
                    }
                    catch (Exception ex)
                    {
                        var name = Path.GetFileNameWithoutExtension(file);
                        list.Add(new AgentRole
                        {
                            Name = name,
                            Title = name,
                            Scope = scope,
                            File = FormatDisplayPath(file, workspaceRoot),
                            Editable = false,
                            Error = ex.Message
                        });
                    }
                }
            }
            catch (Exception ex)
            {
                // Unreadable directory
                list.Add(new AgentRole
                {
                    Name = scope,
                    Title = scope,
                    Scope = scope,
                    Editable = false,
                    Error = PompLanguage.Pick("역할 디렉터리를 읽을 수 없어요: " + ex.Message, "Cannot read roles directory: " + ex.Message)
                });
            }
            return list;
        }

        private List<AgentTeam> LoadTeamsFromDirectory(string dir, string scope, string? workspaceRoot, HashSet<string> knownRoleNames)
        {
            var list = new List<AgentTeam>();
            try
            {
                CheckpointStore.CheckDirectPath(dir);
                foreach (var file in Directory.EnumerateFiles(dir, "*.json").OrderBy(f => f, StringComparer.OrdinalIgnoreCase))
                {
                    try
                    {
                        CheckpointStore.CheckDirectPath(file);
                        list.Add(ParseTeamFile(file, scope, workspaceRoot, knownRoleNames));
                    }
                    catch (Exception ex)
                    {
                        var id = Path.GetFileNameWithoutExtension(file);
                        list.Add(new AgentTeam
                        {
                            Id = id,
                            Title = id,
                            Scope = scope,
                            File = FormatDisplayPath(file, workspaceRoot),
                            Error = ex.Message
                        });
                    }
                }
            }
            catch (Exception ex)
            {
                list.Add(new AgentTeam
                {
                    Id = scope,
                    Title = scope,
                    Scope = scope,
                    Error = PompLanguage.Pick("팀 디렉터리를 읽을 수 없어요: " + ex.Message, "Cannot read teams directory: " + ex.Message)
                });
            }
            return list;
        }

        private AgentRole ParseRoleFile(string filePath, string scope, string? workspaceRoot)
        {
            var fileInfo = new FileInfo(filePath);
            if (fileInfo.Length > MaxFileSizeRoleBytes)
            {
                return new AgentRole
                {
                    Name = Path.GetFileNameWithoutExtension(filePath),
                    Title = Path.GetFileNameWithoutExtension(filePath),
                    Scope = scope,
                    File = FormatDisplayPath(filePath, workspaceRoot),
                    Editable = false,
                    Error = PompLanguage.Pick("역할 파일이 너무 커요.", "Role file is too large.")
                };
            }

            byte[] bytes = File.ReadAllBytes(filePath);
            string hash = ComputeSha256(bytes);
            string text = Encoding.UTF8.GetString(bytes).TrimStart('﻿');

            if (!text.StartsWith("---", StringComparison.Ordinal))
            {
                return new AgentRole
                {
                    Name = Path.GetFileNameWithoutExtension(filePath),
                    Title = Path.GetFileNameWithoutExtension(filePath),
                    Scope = scope,
                    File = FormatDisplayPath(filePath, workspaceRoot),
                    Hash = hash,
                    Editable = false,
                    Error = PompLanguage.Pick("YAML 앞머리(---)를 찾을 수 없어요.", "Missing YAML frontmatter (---).")
                };
            }

            int secondDelimiter = text.IndexOf("\n---", 3, StringComparison.Ordinal);
            if (secondDelimiter < 0)
            {
                secondDelimiter = text.IndexOf("\r\n---", 3, StringComparison.Ordinal);
            }
            if (secondDelimiter < 0)
            {
                return new AgentRole
                {
                    Name = Path.GetFileNameWithoutExtension(filePath),
                    Title = Path.GetFileNameWithoutExtension(filePath),
                    Scope = scope,
                    File = FormatDisplayPath(filePath, workspaceRoot),
                    Hash = hash,
                    Editable = false,
                    Error = PompLanguage.Pick("YAML 앞머리가 닫히지 않았어요.", "Unclosed YAML frontmatter.")
                };
            }

            string frontmatterText = text.Substring(3, secondDelimiter - 3);
            int bodyStart = text.IndexOf('\n', secondDelimiter + 1);
            string instructions = bodyStart >= 0 ? text.Substring(bodyStart + 1).Trim() : string.Empty;

            var parsedKeys = ParseFrontmatterLines(frontmatterText);
            var name = parsedKeys.TryGetValue("name", out var n) ? n : Path.GetFileNameWithoutExtension(filePath);
            var title = parsedKeys.TryGetValue("pomp-title", out var pt) ? pt : name;
            var job = parsedKeys.TryGetValue("pomp-job", out var pj) ? pj : string.Empty;
            var description = parsedKeys.TryGetValue("description", out var d) ? d : string.Empty;
            var model = parsedKeys.TryGetValue("model", out var m) ? m : null;
            var thinkingLevel = parsedKeys.TryGetValue("thinking-level", out var tl)
                ? tl
                : parsedKeys.TryGetValue("thinkingLevel", out var tl2)
                    ? tl2
                    : parsedKeys.TryGetValue("thinking", out var tl3) ? tl3 : null;

            string toolsMapped = "all";
            if (parsedKeys.TryGetValue("tools", out var toolsRaw))
            {
                var toolsList = ParseToolsList(toolsRaw);
                if (toolsList.Count == 0)
                {
                    // `tools: []` (no tools but yield) or a block list this reader does not follow: never shown as "all".
                    toolsMapped = "custom";
                }
                else if (toolsList.Count == ReadOnlyToolSet.Count && toolsList.All(t => ReadOnlyToolSet.Contains(t)))
                {
                    toolsMapped = "readOnly";
                }
                else
                {
                    toolsMapped = "custom";
                }
            }

            if (string.IsNullOrWhiteSpace(description))
            {
                return new AgentRole
                {
                    Name = name,
                    Title = title,
                    Scope = scope,
                    File = FormatDisplayPath(filePath, workspaceRoot),
                    Hash = hash,
                    Editable = false,
                    Error = PompLanguage.Pick("역할 설명(description)이 없어요.", "Missing role description.")
                };
            }

            var unsupported = UnsupportedFrontmatter(frontmatterText);
            return new AgentRole
            {
                Name = name,
                Title = title,
                Job = job,
                Description = description,
                Instructions = instructions,
                Model = model,
                ThinkingLevel = thinkingLevel,
                Tools = toolsMapped,
                Scope = scope,
                File = FormatDisplayPath(filePath, workspaceRoot),
                Hash = hash,
                Editable = unsupported == null,
                Note = unsupported,
            };
        }

        /// <summary>
        /// Why POMP must not rewrite this frontmatter, or null. Saving keeps unknown lines one by one and writes its own keys as single
        /// JSON-quoted lines, so multi-line YAML (indented or block values, list items) and model lists would be corrupted: such files stay
        /// usable by omp and are listed read-only.
        /// </summary>
        private static string? UnsupportedFrontmatter(string frontmatter)
        {
            var currentKnown = false;
            foreach (var raw in frontmatter.Split(new[] { "\r\n", "\n" }, StringSplitOptions.None))
            {
                var line = raw.TrimEnd();
                if (line.Length == 0 || line.TrimStart().StartsWith("#", StringComparison.Ordinal)) continue;
                bool unsupported;
                if (IsContinuation(line))
                {
                    // Nested lines of another key are kept with it; those of a key POMP rewrites would be left behind.
                    unsupported = currentKnown;
                }
                else
                {
                    var colon = line.IndexOf(':');
                    var key = colon > 0 ? line.Substring(0, colon).Trim() : string.Empty;
                    var value = colon > 0 ? line.Substring(colon + 1).Trim() : string.Empty;
                    currentKnown = IsKnownPompKey(key);
                    unsupported = colon <= 0
                        || (currentKnown && (value.Length == 0 || value[0] == '|' || value[0] == '>'))
                        || (string.Equals(key, "model", StringComparison.OrdinalIgnoreCase) && value.StartsWith("[", StringComparison.Ordinal));
                }

                if (unsupported)
                    return PompLanguage.Pick("여러 줄 값이나 목록이 있어 POMP에서는 고칠 수 없어요.",
                        "It has multi-line values or lists that POMP cannot rewrite safely.");
            }

            return null;
        }

        /// <summary>An indented line or a list item: it belongs to the key line above it.</summary>
        private static bool IsContinuation(string line) => line.Length > 0 && (char.IsWhiteSpace(line[0]) || line[0] == '-');

        private AgentTeam ParseTeamFile(string filePath, string scope, string? workspaceRoot, HashSet<string> knownRoleNames)
        {
            var fileInfo = new FileInfo(filePath);
            if (fileInfo.Length > MaxFileSizeTeamBytes)
            {
                return new AgentTeam
                {
                    Id = Path.GetFileNameWithoutExtension(filePath),
                    Title = Path.GetFileNameWithoutExtension(filePath),
                    Scope = scope,
                    File = FormatDisplayPath(filePath, workspaceRoot),
                    Error = PompLanguage.Pick("팀 파일이 너무 커요.", "Team file is too large.")
                };
            }

            byte[] bytes = File.ReadAllBytes(filePath);
            string hash = ComputeSha256(bytes);
            string text = Encoding.UTF8.GetString(bytes).TrimStart('﻿');

            JObject json;
            try
            {
                json = JObject.Parse(text);
            }
            catch (Exception ex)
            {
                return new AgentTeam
                {
                    Id = Path.GetFileNameWithoutExtension(filePath),
                    Title = Path.GetFileNameWithoutExtension(filePath),
                    Scope = scope,
                    File = FormatDisplayPath(filePath, workspaceRoot),
                    Hash = hash,
                    Error = PompLanguage.Pick("JSON 파싱 오류: " + ex.Message, "JSON parse error: " + ex.Message)
                };
            }

            if ((int?)json["version"] != 1)
            {
                return new AgentTeam
                {
                    Id = (string?)json["id"] ?? Path.GetFileNameWithoutExtension(filePath),
                    Title = (string?)json["title"] ?? Path.GetFileNameWithoutExtension(filePath),
                    Scope = scope,
                    File = FormatDisplayPath(filePath, workspaceRoot),
                    Hash = hash,
                    Error = PompLanguage.Pick("지원되지 않거나 잘못된 팀 버전이에요.", "Unsupported team version.")
                };
            }

            var id = (string?)json["id"] ?? Path.GetFileNameWithoutExtension(filePath);
            var title = (string?)json["title"] ?? id;
            var goal = (string?)json["goal"] ?? string.Empty;
            var instructions = (string?)json["instructions"] ?? string.Empty;
            var mode = ((string?)json["mode"])?.ToLowerInvariant() ?? "lead";
            if (mode != "lead" && mode != "parallel" && mode != "pipeline") mode = "lead";
            var membersOnly = json["membersOnly"] == null || json["membersOnly"]!.Type == JTokenType.Null || (bool)json["membersOnly"]!;
            var isolation = json["isolation"]?.Type == JTokenType.Boolean && (bool)json["isolation"]!;

            var members = new List<string>();
            var missing = new List<string>();
            if (json["members"] is JArray arr)
            {
                foreach (var item in arr)
                {
                    var m = (string?)item;
                    if (!string.IsNullOrWhiteSpace(m))
                    {
                        var trimmed = m!.Trim();
                        members.Add(trimmed);
                        if (!knownRoleNames.Contains(trimmed))
                        {
                            missing.Add(trimmed);
                        }
                    }
                }
            }

            return new AgentTeam
            {
                Id = id,
                Title = title,
                Goal = goal,
                Members = members,
                Mode = mode,
                Instructions = instructions,
                MembersOnly = membersOnly,
                Isolation = isolation,
                Scope = scope,
                File = FormatDisplayPath(filePath, workspaceRoot),
                Hash = hash,
                Missing = missing
            };
        }

        private static Dictionary<string, string> ParseFrontmatterLines(string frontmatter)
        {
            var dict = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            var lines = frontmatter.Split(new[] { "\r\n", "\n" }, StringSplitOptions.None);
            for (int i = 0; i < lines.Length; i++)
            {
                // Only top-level keys: a nested "name:" under another key must not replace the role's name.
                if (IsContinuation(lines[i])) continue;
                var line = lines[i].Trim();
                if (string.IsNullOrEmpty(line) || line.StartsWith("#", StringComparison.Ordinal)) continue;
                int colon = line.IndexOf(':');
                if (colon <= 0) continue;

                var key = line.Substring(0, colon).Trim();
                var rawValue = line.Substring(colon + 1).Trim();
                dict[key] = UnquoteScalar(rawValue);
            }
            return dict;
        }

        private static string UnquoteScalar(string raw)
        {
            if (string.IsNullOrEmpty(raw)) return string.Empty;
            if ((raw.StartsWith("\"") && raw.EndsWith("\"") && raw.Length >= 2) ||
                (raw.StartsWith("'") && raw.EndsWith("'") && raw.Length >= 2))
            {
                try
                {
                    if (raw.StartsWith("\""))
                    {
                        return (string?)JToken.Parse(raw) ?? raw.Substring(1, raw.Length - 2);
                    }
                    return raw.Substring(1, raw.Length - 2);
                }
                catch
                {
                    return raw.Substring(1, raw.Length - 2);
                }
            }
            return raw;
        }

        private static List<string> ParseToolsList(string raw)
        {
            var list = new List<string>();
            raw = raw.Trim();
            if (raw.StartsWith("[") && raw.EndsWith("]"))
            {
                try
                {
                    var arr = JArray.Parse(raw);
                    foreach (var item in arr)
                    {
                        var s = (string?)item;
                        if (!string.IsNullOrWhiteSpace(s)) list.Add(s!.Trim());
                    }
                    return list;
                }
                catch
                {
                    // Fall through to CSV
                }
            }
            var parts = raw.Split(new[] { ',' }, StringSplitOptions.RemoveEmptyEntries);
            foreach (var p in parts)
            {
                var trimmed = UnquoteScalar(p.Trim());
                if (!string.IsNullOrWhiteSpace(trimmed)) list.Add(trimmed);
            }
            return list;
        }

        /// <summary>The frontmatter between the opening and closing <c>---</c> of a role file, or null.</summary>
        private static string? FrontmatterOf(string? text)
        {
            if (text == null) return null;
            text = text.TrimStart('﻿');
            if (!text.StartsWith("---", StringComparison.Ordinal)) return null;
            int second = text.IndexOf("\n---", 3, StringComparison.Ordinal);
            if (second < 0) second = text.IndexOf("\r\n---", 3, StringComparison.Ordinal);
            return second > 0 ? text.Substring(3, second - 3) : null;
        }

        /// <summary>The items of a top-level list key, inline (<c>key: [a, b]</c>, <c>key: a, b</c>) or as nested <c>- item</c> lines.</summary>
        private static List<string> FrontmatterList(string? frontmatter, string key)
        {
            var items = new List<string>();
            if (frontmatter == null) return items;
            var inKey = false;
            foreach (var raw in frontmatter.Split(new[] { "\r\n", "\n" }, StringSplitOptions.None))
            {
                var line = raw.TrimEnd();
                if (line.Length == 0) continue;
                if (!IsContinuation(line))
                {
                    if (inKey) break;
                    int colon = line.IndexOf(':');
                    if (colon <= 0 || !string.Equals(line.Substring(0, colon).Trim(), key, StringComparison.OrdinalIgnoreCase)) continue;
                    var value = line.Substring(colon + 1).Trim();
                    if (value.Length > 0) return ParseToolsList(value);
                    inKey = true;
                }
                else if (inKey)
                {
                    var item = line.Trim();
                    if (item.StartsWith("-", StringComparison.Ordinal)) item = UnquoteScalar(item.Substring(1).Trim());
                    if (!string.IsNullOrWhiteSpace(item)) items.Add(item);
                }
            }
            return items;
        }

        /// <summary>
        /// The role form's fields of a built-in role, read from the engine's own definition of it (<c>omp agents unpack</c>):
        /// <c>{ description, model?, thinkingLevel?, tools, instructions }</c>.
        /// </summary>
        internal static JObject BuiltinRoleForm(string definition)
        {
            var frontmatter = FrontmatterOf(definition) ?? throw new InvalidDataException("The built-in role definition has no frontmatter.");
            var keys = ParseFrontmatterLines(frontmatter);
            var text = definition.TrimStart('﻿');
            int close = text.IndexOf("\n---", 3, StringComparison.Ordinal);
            int bodyStart = text.IndexOf('\n', close + 1);
            var form = new JObject
            {
                ["description"] = keys.TryGetValue("description", out var description) ? description : string.Empty,
                ["instructions"] = bodyStart >= 0 ? text.Substring(bodyStart + 1).Trim() : string.Empty,
            };
            var model = keys.TryGetValue("model", out var inline) && inline.Length > 0 ? inline : FrontmatterList(frontmatter, "model").FirstOrDefault();
            if (!string.IsNullOrWhiteSpace(model)) form["model"] = model;
            var thinking = keys.TryGetValue("thinking-level", out var level) ? level : keys.TryGetValue("thinkingLevel", out var level2) ? level2 : null;
            if (!string.IsNullOrWhiteSpace(thinking)) form["thinkingLevel"] = thinking;
            var tools = FrontmatterList(frontmatter, "tools");
            form["tools"] = tools.Count == 0 ? "all" : tools.Count == ReadOnlyToolSet.Count && tools.All(t => ReadOnlyToolSet.Contains(t)) ? "readOnly" : "custom";
            return form;
        }

        /// <param name="baseText">The file the role starts from (the existing file, or a built-in role's definition): its frontmatter lines
        /// POMP does not write stay as they are, and a "custom" tool choice keeps its tool list.</param>
        private static string BuildRoleMarkdown(
            string? baseText,
            string name,
            string? title,
            string? job,
            string description,
            string? model,
            string? thinkingLevel,
            string tools,
            JArray? customToolsArray,
            string instructions)
        {
            var unknownLines = new List<string>();
            var frontmatter = FrontmatterOf(baseText);
            if (frontmatter != null)
            {
                var lines = frontmatter.Split(new[] { "\r\n", "\n" }, StringSplitOptions.None);
                // A key line decides for its nested lines too: a nested "name:" of another key is not POMP's name.
                var keep = true;
                foreach (var line in lines)
                {
                    if (string.IsNullOrWhiteSpace(line)) continue;
                    if (!IsContinuation(line))
                    {
                        int colon = line.IndexOf(':');
                        keep = !(colon > 0 && IsKnownPompKey(line.Substring(0, colon).Trim()));
                    }

                    if (keep) unknownLines.Add(line);
                }
            }
            var baseTools = FrontmatterList(frontmatter, "tools");

            var sb = new StringBuilder();
            sb.AppendLine("---");
            sb.AppendLine("name: " + JsonConvert.ToString(name));
            sb.AppendLine("description: " + JsonConvert.ToString(description));
            if (!string.IsNullOrWhiteSpace(title) && !string.Equals(title, name, StringComparison.Ordinal))
            {
                sb.AppendLine("pomp-title: " + JsonConvert.ToString(title));
            }
            if (!string.IsNullOrWhiteSpace(job))
            {
                sb.AppendLine("pomp-job: " + JsonConvert.ToString(job));
            }
            if (!string.IsNullOrWhiteSpace(model))
            {
                sb.AppendLine("model: " + JsonConvert.ToString(model));
            }
            if (!string.IsNullOrWhiteSpace(thinkingLevel))
            {
                sb.AppendLine("thinking-level: " + JsonConvert.ToString(thinkingLevel));
            }

            if (string.Equals(tools, "readOnly", StringComparison.OrdinalIgnoreCase))
            {
                sb.AppendLine("tools: [\"read\", \"find\", \"grep\", \"glob\", \"lsp\", \"yield\"]");
            }
            else if (string.Equals(tools, "custom", StringComparison.OrdinalIgnoreCase) && customToolsArray != null && customToolsArray.Count > 0)
            {
                sb.AppendLine("tools: " + customToolsArray.ToString(Formatting.None));
            }
            else if (string.Equals(tools, "custom", StringComparison.OrdinalIgnoreCase) && baseTools.Count > 0)
            {
                // The form keeps a role's own tool list as it is: the list of the file (or of the built-in role) it started from.
                sb.AppendLine("tools: " + new JArray(baseTools.ToArray<object>()).ToString(Formatting.None));
            }
            // If tools == 'all', omit tools line

            foreach (var unk in unknownLines)
            {
                sb.AppendLine(unk);
            }
            sb.AppendLine("---");

            if (!string.IsNullOrWhiteSpace(instructions))
            {
                sb.AppendLine(instructions.Trim());
            }

            return sb.ToString();
        }

        private static bool IsKnownPompKey(string key)
        {
            return string.Equals(key, "name", StringComparison.OrdinalIgnoreCase) ||
                   string.Equals(key, "description", StringComparison.OrdinalIgnoreCase) ||
                   string.Equals(key, "pomp-title", StringComparison.OrdinalIgnoreCase) ||
                   string.Equals(key, "pomp-job", StringComparison.OrdinalIgnoreCase) ||
                   string.Equals(key, "model", StringComparison.OrdinalIgnoreCase) ||
                   string.Equals(key, "thinking-level", StringComparison.OrdinalIgnoreCase) ||
                   string.Equals(key, "thinkingLevel", StringComparison.OrdinalIgnoreCase) ||
                   string.Equals(key, "thinking", StringComparison.OrdinalIgnoreCase) ||
                   string.Equals(key, "tools", StringComparison.OrdinalIgnoreCase);
        }

        public static string ComputeSha256(byte[] bytes)
        {
            using (var sha = SHA256.Create())
            {
                var hash = sha.ComputeHash(bytes);
                var sb = new StringBuilder(hash.Length * 2);
                foreach (var b in hash)
                {
                    sb.Append(b.ToString("x2"));
                }
                return sb.ToString();
            }
        }

        public static string ComputeFileHash(string path)
        {
            var bytes = File.ReadAllBytes(path);
            return ComputeSha256(bytes);
        }

        private static string FormatDisplayPath(string fullPath, string? workspaceRoot)
        {
            if (string.IsNullOrEmpty(workspaceRoot)) return fullPath;
            try
            {
                var fullRoot = Path.GetFullPath(workspaceRoot).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
                var fullFile = Path.GetFullPath(fullPath);
                if (fullFile.StartsWith(fullRoot, StringComparison.OrdinalIgnoreCase))
                {
                    return fullFile.Substring(fullRoot.Length).Replace('\\', '/');
                }
            }
            catch { }
            return fullPath;
        }
    }
}
