using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Pomp.Bridge;
using Pomp.Diagnostics;
using Pomp.Features;
using Pomp.Internal;
using Pomp.Protocol;
using Pomp.Ui;

namespace Pomp.Hosting
{
    /// <summary>An entry of the code editor's "to agent or team" menu: an agent role (<c>agent</c>, name) or a team (<c>team</c>, id).</summary>
    public sealed class EditorSendTarget
    {
        public EditorSendTarget(string kind, string id, string title)
        {
            Kind = kind;
            Id = id;
            Title = title;
        }

        /// <summary>"agent" or "team".</summary>
        public string Kind { get; }

        public string Id { get; }

        /// <summary>The menu text.</summary>
        public string Title { get; }
    }

    // Agent roles and teams (docs/agent-teams.md §2, §3, §6): agents/command from the page.
    public sealed partial class PompSession
    {
        private AgentRoster GetAgentRoster()
        {
            string? root;
            lock (_stateLock)
            {
                root = _workingDirectory;
            }
            return new AgentRoster(root, _options.UserHome, _options.UserTeamsDirectory);
        }

        /// <summary>
        /// The agents and teams the code editor's POMP menu can send a selection to (its "to agent or team" entries): usable roles
        /// first, then runnable teams, each by name like the page's lists (Pomp.Internal.NameOrder); all of them (no count limit).
        /// </summary>
        public IReadOnlyList<EditorSendTarget> ListEditorSendTargets()
        {
            var state = GetAgentRoster().GetState();
            var targets = new List<EditorSendTarget>();
            foreach (var role in state.Roles)
            {
                if (role.Shadowed || !string.IsNullOrEmpty(role.Error) || !AgentRoster.IsValidName(role.Name)) continue;
                targets.Add(new EditorSendTarget("agent", role.Name, string.IsNullOrWhiteSpace(role.Title) ? role.Name : role.Title));
            }
            targets.Sort((left, right) => Pomp.Internal.NameOrder.Compare(left.Title, right.Title));
            var teams = new List<EditorSendTarget>();
            // A runnable team, as the page's send-target list sees it (teamsCache.ts isRunnableTeam): two or more distinct members, all usable roles.
            var usable = new HashSet<string>(targets.Select(target => target.Id), StringComparer.Ordinal);
            foreach (var team in state.Teams)
            {
                if (team.Shadowed || !string.IsNullOrEmpty(team.Error) || team.Missing.Count > 0 || string.IsNullOrEmpty(team.Id)) continue;
                if (team.Members.Count < 2 || team.Members.Distinct(StringComparer.Ordinal).Count() != team.Members.Count || !team.Members.All(usable.Contains)) continue;
                teams.Add(new EditorSendTarget("team", team.Id, string.IsNullOrWhiteSpace(team.Title) ? team.Id : team.Title));
            }
            teams.Sort((left, right) => Pomp.Internal.NameOrder.Compare(left.Title, right.Title));
            targets.AddRange(teams);
            return targets;
        }

        private void PublishAgentsState()
        {
            _ui.Post(UiMessageTypes.AgentsState, AgentsState(), UiDelivery.State);
        }

        private UiMessage AgentsStateMessage()
        {
            return new UiMessage(UiMessageTypes.AgentsState, AgentsState());
        }

        private AgentsStatePayload AgentsState()
        {
            var roster = GetAgentRoster();
            var state = roster.GetState();
            string? started;
            lock (_stateLock) started = _bridgeState.State == BridgeState.Running ? _mainInstructionsStarted : null;
            state.Main.RestartRequired = started != null && started != roster.MainInstructions().Fingerprint();
            return state;
        }

        // What POMP's own role instructions (APPEND_SYSTEM.md) were when the engine last started: the engine reads them only then.
        private string? _mainInstructionsStarted;

        private void MainInstructionsBridgeStateChanged(BridgeState state)
        {
            if (state == BridgeState.Starting)
            {
                string fingerprint;
                try { fingerprint = GetAgentRoster().MainInstructions().Fingerprint(); }
                catch (Exception ex) { _logger.Debug("Could not read the chat agent's instructions file: " + ex.Message); fingerprint = "unreadable"; }
                lock (_stateLock) _mainInstructionsStarted = fingerprint;
            }
            else if (state == BridgeState.Running)
            {
                PublishAgentsState();
            }
        }

        private async Task HandleAgentsCommandAsync(JObject payload)
        {
            var requestId = (string?)payload["requestId"] ?? string.Empty;
            var action = (string?)payload["action"] ?? string.Empty;
            var roster = GetAgentRoster();

            try
            {
                switch (action)
                {
                    case "refresh":
                        PublishAgentsState();
                        _ui.Post(UiMessageTypes.AgentsReply, new JObject { ["requestId"] = requestId, ["ok"] = true }, UiDelivery.State);
                        break;

                    case "builtinRole":
                    {
                        // What the role form shows for a built-in role: its fields as the engine defines it, instructions included.
                        var name = ((string?)payload["name"])?.Trim() ?? string.Empty;
                        if (!AgentRoster.IsBundledRoleName(name)) throw new InvalidOperationException(PompLanguage.Pick("역할을 찾을 수 없어요.", "The role was not found."));
                        var form = AgentRoster.BuiltinRoleForm(await BuiltinRoleDefinitionAsync(name).ConfigureAwait(false));
                        _ui.Post(UiMessageTypes.AgentsReply, new JObject { ["requestId"] = requestId, ["ok"] = true, ["role"] = form }, UiDelivery.State);
                        break;
                    }

                    case "saveRole":
                    {
                        var scope = (string?)payload["scope"] ?? "project";
                        var roleObj = payload["role"] as JObject ?? throw new InvalidOperationException(PompLanguage.Pick("역할이 비어 있어요.", "The role is empty."));
                        var expectedHash = (string?)payload["expectedHash"];
                        // An edited built-in role: its file starts from the engine's definition (output schema, tool list, ...).
                        var roleName = ((string?)roleObj["name"])?.Trim() ?? string.Empty;
                        var builtin = roleObj["builtin"]?.Type == JTokenType.Boolean && (bool)roleObj["builtin"]! && AgentRoster.IsBundledRoleName(roleName)
                            ? await BuiltinRoleDefinitionAsync(roleName).ConfigureAwait(false) : null;
                        var saved = roster.SaveRole(scope, roleObj, expectedHash, builtin);
                        _ui.Post(UiMessageTypes.AgentsReply, new JObject { ["requestId"] = requestId, ["ok"] = true, ["name"] = saved.Name }, UiDelivery.State);
                        PublishAgentsState();
                        break;
                    }

                    case "mainRole":
                        // POMP's own role: the instructions the engine appends to the chat agent, the project's and the user's.
                        _ui.Post(UiMessageTypes.AgentsReply, new JObject { ["requestId"] = requestId, ["ok"] = true, ["main"] = roster.MainInstructions().Read() }, UiDelivery.State);
                        break;

                    case "saveMain":
                    {
                        var scope = (string?)payload["scope"] ?? "project";
                        var text = payload["instructions"]?.Type == JTokenType.String ? (string)payload["instructions"]! : string.Empty;
                        roster.MainInstructions().Save(scope, text, (string?)payload["expectedHash"]);
                        _ui.Post(UiMessageTypes.AgentsReply, new JObject { ["requestId"] = requestId, ["ok"] = true }, UiDelivery.State);
                        PublishAgentsState();
                        break;
                    }

                    case "setAvatar":
                    {
                        // The picture of a role: shape, color, optional image (null gives it back its letter in a circle).
                        var scope = (string?)payload["scope"] ?? "project";
                        var name = ((string?)payload["name"])?.Trim() ?? string.Empty;
                        roster.SetAvatar(scope, name, payload["avatar"] as JObject);
                        _ui.Post(UiMessageTypes.AgentsReply, new JObject { ["requestId"] = requestId, ["ok"] = true, ["name"] = name }, UiDelivery.State);
                        PublishAgentsState();
                        break;
                    }

                    case "avatarImages":
                    {
                        // The images of the pictures the page shows now; the agents state carries none.
                        var wanted = (payload["roles"] as JArray ?? new JArray()).OfType<JObject>().Take(500)
                            .Select(role => ((string?)role["scope"] ?? string.Empty, ((string?)role["name"])?.Trim() ?? string.Empty))
                            .Where(role => AgentRoster.IsValidName(role.Item2));
                        _ui.Post(UiMessageTypes.AgentsReply, new JObject { ["requestId"] = requestId, ["ok"] = true, ["images"] = roster.AvatarImages(wanted, 1024 * 1024) }, UiDelivery.State);
                        break;
                    }

                    case "deleteRole":
                    {
                        var scope = (string?)payload["scope"] ?? "project";
                        var name = (string?)payload["name"] ?? throw new InvalidOperationException(PompLanguage.Pick("역할 이름이 비어 있어요.", "The role name is empty."));
                        var expectedHash = (string?)payload["expectedHash"];
                        roster.DeleteRole(scope, name, expectedHash);
                        _ui.Post(UiMessageTypes.AgentsReply, new JObject { ["requestId"] = requestId, ["ok"] = true, ["name"] = name }, UiDelivery.State);
                        PublishAgentsState();
                        break;
                    }

                    case "saveTeam":
                    {
                        var scope = (string?)payload["scope"] ?? "project";
                        var teamObj = payload["team"] as JObject ?? throw new InvalidOperationException(PompLanguage.Pick("팀이 비어 있어요.", "The team is empty."));
                        var expectedHash = (string?)payload["expectedHash"];
                        var saved = roster.SaveTeam(scope, teamObj, expectedHash);
                        _ui.Post(UiMessageTypes.AgentsReply, new JObject { ["requestId"] = requestId, ["ok"] = true, ["name"] = saved.Id }, UiDelivery.State);
                        PublishAgentsState();
                        break;
                    }

                    case "deleteTeam":
                    {
                        var scope = (string?)payload["scope"] ?? "project";
                        var id = (string?)payload["name"] ?? (string?)payload["id"] ?? throw new InvalidOperationException(PompLanguage.Pick("팀 ID가 비어 있어요.", "The team id is empty."));
                        var expectedHash = (string?)payload["expectedHash"];
                        roster.DeleteTeam(scope, id, expectedHash);
                        _ui.Post(UiMessageTypes.AgentsReply, new JObject { ["requestId"] = requestId, ["ok"] = true, ["name"] = id }, UiDelivery.State);
                        PublishAgentsState();
                        break;
                    }

                    case "exportTeam":
                    {
                        var scope = (string?)payload["scope"] ?? "project";
                        var name = (string?)payload["name"] ?? (string?)payload["id"] ?? throw new InvalidOperationException(PompLanguage.Pick("팀 ID가 비어 있어요.", "The team id is empty."));
                        var template = new TeamTemplate(roster).Export(scope, name);
                        _ui.Post(UiMessageTypes.AgentsReply, new JObject { ["requestId"] = requestId, ["ok"] = true, ["template"] = template }, UiDelivery.State);
                        break;
                    }

                    case "openTeamExport":
                    {
                        var scope = (string?)payload["scope"] ?? "project";
                        var name = (string?)payload["name"] ?? (string?)payload["id"] ?? throw new InvalidOperationException(PompLanguage.Pick("팀 ID가 비어 있어요.", "The team id is empty."));
                        var template = new TeamTemplate(roster).Export(scope, name);
                        var exportsDir = Path.Combine(Path.GetFullPath(_environment.DataDirectory), "exports");
                        CheckpointStore.CheckDirectPath(exportsDir);
                        Directory.CreateDirectory(exportsDir);
                        var file = Path.Combine(exportsDir, name.Trim() + ".pomp-team.json");
                        AtomicFile.WriteAllText(file, template);
                        var opened = await AdapterGuard.TryRunAsync(token => _adapters.Editor.OpenDocumentAsync(file, null, token), _options.AdapterTimeout, _logger, "open team export", _disposeCts.Token).ConfigureAwait(false);
                        if (!opened.Succeeded || !opened.Value) throw new InvalidOperationException(PompLanguage.Pick("팀 내보내기 파일을 VS 편집기에서 열지 못했어요.", "Could not open the team export in the VS editor."));
                        _ui.Post(UiMessageTypes.AgentsReply, new JObject { ["requestId"] = requestId, ["ok"] = true }, UiDelivery.State);
                        break;
                    }

                    case "openRole":
                    {
                        var scope = (string?)payload["scope"] ?? "project";
                        var name = (string?)payload["name"] ?? throw new InvalidOperationException(PompLanguage.Pick("역할 이름이 비어 있어요.", "The role name is empty."));
                        var file = roster.FileOf(scope, name);
                        var opened = await AdapterGuard.TryRunAsync(token => _adapters.Editor.OpenDocumentAsync(file, null, token), _options.AdapterTimeout, _logger, "open role", _disposeCts.Token).ConfigureAwait(false);
                        if (!opened.Succeeded || !opened.Value) throw new InvalidOperationException(PompLanguage.Pick("역할 파일을 VS 편집기에서 열지 못했어요.", "Could not open the role file in the VS editor."));
                        _ui.Post(UiMessageTypes.AgentsReply, new JObject { ["requestId"] = requestId, ["ok"] = true, ["name"] = name }, UiDelivery.State);
                        break;
                    }

                    case "previewTeamImport":
                    {
                        var scope = (string?)payload["scope"] ?? "project";
                        var text = (string?)payload["text"] ?? throw new InvalidOperationException(PompLanguage.Pick("템플릿 텍스트가 비어 있어요.", "The template text is empty."));
                        var preview = new TeamTemplate(roster).Preview(text, scope);
                        _ui.Post(UiMessageTypes.AgentsReply, new JObject { ["requestId"] = requestId, ["ok"] = true, ["preview"] = preview }, UiDelivery.State);
                        break;
                    }

                    case "importTeam":
                    {
                        var scope = (string?)payload["scope"] ?? "project";
                        var text = (string?)payload["text"] ?? throw new InvalidOperationException(PompLanguage.Pick("템플릿 텍스트가 비어 있어요.", "The template text is empty."));
                        var previewHash = (string?)payload["previewHash"];
                        var id = new TeamTemplate(roster).Import(text, scope, previewHash ?? string.Empty);
                        _ui.Post(UiMessageTypes.AgentsReply, new JObject { ["requestId"] = requestId, ["ok"] = true, ["name"] = id }, UiDelivery.State);
                        PublishAgentsState();
                        break;
                    }

                    case "transcript":
                    {
                        var subagentId = (string?)payload["subagentId"] ?? string.Empty;
                        if (string.IsNullOrWhiteSpace(subagentId))
                        {
                            throw new InvalidOperationException(PompLanguage.Pick("하위 에이전트 ID가 비어 있어요.", "Subagent ID is empty."));
                        }

                        var sessionId = (string?)payload["sessionId"];
                        string? currentSession;
                        lock (_stateLock)
                        {
                            currentSession = _info.SessionId;
                        }

                        if (!string.IsNullOrEmpty(sessionId) && !string.Equals(sessionId, currentSession, StringComparison.Ordinal))
                        {
                            throw new InvalidOperationException(PompLanguage.Pick("대화 세션이 바뀌었어요.", "Conversation session changed."));
                        }

                        if (_bridge.State != BridgeState.Running)
                        {
                            throw new InvalidOperationException(PompLanguage.Pick("연결되어 있지 않아요.", "Not connected."));
                        }

                        using var cts = CancellationTokenSource.CreateLinkedTokenSource(_disposeCts.Token);
                        cts.CancelAfter(TimeSpan.FromSeconds(15));

                        var req = new JObject { ["type"] = "get_subagent_messages", ["subagentId"] = subagentId };
                        var response = await _bridge.SendCommandAsync(req, TimeSpan.FromSeconds(15), cts.Token).ConfigureAwait(false);
                        if (!response.Success)
                        {
                            throw new InvalidOperationException(response.Error ?? PompLanguage.Pick("하위 에이전트 대화 기록을 가져올 수 없어요.", "Could not retrieve subagent messages."));
                        }

                        string agentName = subagentId;
                        var bgJobs = _backgroundAgents.View()["jobs"] as JArray;
                        var matchingJob = bgJobs?.OfType<JObject>().FirstOrDefault(j => (string?)j["id"] == subagentId);
                        if (matchingJob != null && matchingJob["agent"]?.Type == JTokenType.String)
                        {
                            agentName = (string)matchingJob["agent"]!;
                        }

                        var (messages, truncated) = ProcessTranscriptMessages(response.Data?["messages"] as JArray);

                        var replyPayload = new JObject
                        {
                            ["requestId"] = requestId,
                            ["ok"] = true,
                            ["transcript"] = new JObject
                            {
                                ["agent"] = agentName,
                                ["messages"] = messages,
                                ["truncated"] = truncated
                            }
                        };

                        _ui.Post(UiMessageTypes.AgentsReply, replyPayload, UiDelivery.State);
                        break;
                    }

                    default:
                        throw new InvalidOperationException(PompLanguage.Pick($"알 수 없는 에이전트 명령이에요: '{action}'", $"Unknown agents command action: '{action}'"));
                }
            }
            catch (Exception ex)
            {
                _ui.Post(
                    UiMessageTypes.AgentsReply,
                    new JObject
                    {
                        ["requestId"] = requestId,
                        ["ok"] = false,
                        ["error"] = ex.Message
                    },
                    UiDelivery.State);
            }
        }

        private static (JArray messages, bool truncated) ProcessTranscriptMessages(JArray? rawMessages)
        {
            var result = new JArray();
            bool truncated = false;
            if (rawMessages == null || rawMessages.Count == 0)
            {
                return (result, false);
            }

            var items = rawMessages.OfType<JObject>().ToList();
            if (items.Count > 40)
            {
                truncated = true;
                items = items.Skip(items.Count - 40).ToList();
            }

            foreach (var item in items)
            {
                var role = item["role"] is JValue { Type: JTokenType.String } roleValue ? (string)roleValue! : "assistant";
                if (role is not ("user" or "assistant" or "toolResult" or "tool")) continue;
                var text = Pomp.Features.MemberTranscript.Text(item);
                if (text.Length == 0) continue;
                if (text.Length > 8000)
                {
                    text = text.Substring(0, 8000);
                    truncated = true;
                }
                result.Add(new JObject { ["role"] = role, ["text"] = text });
            }

            // Cap the whole reply to 64 KB
            while (result.Count > 0 && Encoding.UTF8.GetByteCount(result.ToString(Formatting.None)) > 60 * 1024)
            {
                result.RemoveAt(0);
                truncated = true;
            }

            return (result, truncated);
        }

        private static string ExtractMessageText(JObject message)
        {
            if (message["content"] is JArray contentArray)
            {
                var sb = new StringBuilder();
                foreach (var block in contentArray)
                {
                    if (block["type"]?.ToString() == "text")
                    {
                        var t = block["text"]?.ToString();
                        if (!string.IsNullOrEmpty(t))
                        {
                            if (sb.Length > 0) sb.Append("\n");
                            sb.Append(t);
                        }
                    }
                }
                if (sb.Length > 0) return sb.ToString();
            }

            // Only plain strings: a content array without text blocks (tool calls, images) has no text to show.
            foreach (var field in new[] { "text", "content" })
                if (message[field] is JValue { Type: JTokenType.String } value) return (string)value!;
            return string.Empty;
        }

        /// <summary>
        /// The prompt text for the message's target (docs/agent-teams.md §3, §3.1): a team, one agent (a new assignment) or an agent
        /// this conversation ran (a direct message). No target: <paramref name="message"/> unchanged. Throws when the target cannot take it.
        /// </summary>
        private Task<string> ApplyTargetAsync(ChatSendPayload send, string message, CancellationToken cancellationToken)
        {
            var targets = new[] { send.Team, send.Agent, send.AgentInstance }.Count(target => !string.IsNullOrWhiteSpace(target));
            if (targets > 1 || (targets > 0 && send.Main == true)) throw new InvalidOperationException(PompLanguage.Pick("보낼 대상이 너무 많아요.", "Too many targets."));
            if (!string.IsNullOrWhiteSpace(send.Agent)) return Task.FromResult(ApplyAgent(send.Agent!.Trim(), message));
            if (!string.IsNullOrWhiteSpace(send.AgentInstance)) return Task.FromResult(ApplyAgentInstance(send.AgentInstance!.Trim(), message));
            return ApplyTeamAsync(send.Team, message, cancellationToken);
        }

        private string ApplyAgent(string name, string message)
        {
            var role = GetAgentRoster().ListRoles().FirstOrDefault(r => string.Equals(r.Name, name, StringComparison.Ordinal) && !r.Shadowed && string.IsNullOrEmpty(r.Error));
            if (role == null || !AgentRoster.IsValidName(name))
                throw new InvalidOperationException(PompLanguage.Pick($"에이전트 '{name}'을(를) 찾을 수 없어요.", $"Agent '{name}' was not found."));
            RefuseInReviewOnlyWorkspace();
            _backgroundAgents.SetCurrentTurnTeam(null);
            return "/pomp-agent " + role.Name + " " + message;
        }

        private string ApplyAgentInstance(string id, string message)
        {
            // Only an agent this conversation has run: the id is checked against what omp reported for the current session.
            var view = _backgroundAgents.View();
            string? currentSession;
            lock (_stateLock) currentSession = _info.SessionId;
            var known = AgentInstanceId.IsMatch(id) && (string?)view["sessionId"] == currentSession
                && (view["jobs"] as JArray)?.OfType<JObject>().Any(job => (string?)job["id"] == id) == true;
            if (!known)
                throw new InvalidOperationException(PompLanguage.Pick("이 대화에서 실행한 에이전트를 찾을 수 없어요.", "That agent was not run in this conversation."));
            RefuseInReviewOnlyWorkspace();
            _backgroundAgents.SetCurrentTurnTeam(null);
            return "/pomp-dm " + id + " " + message;
        }

        private static readonly System.Text.RegularExpressions.Regex AgentInstanceId = new System.Text.RegularExpressions.Regex(@"^[A-Za-z0-9][A-Za-z0-9._:-]{0,127}$");

        /// <summary>A review-only project (requireReviewedEdits) blocks child spawning and raw writes, so no team or agent message can run.</summary>
        private void RefuseInReviewOnlyWorkspace()
        {
            string? currentRoot;
            lock (_stateLock) currentRoot = _workingDirectory;
            if (string.IsNullOrEmpty(currentRoot)) return;
            JObject? policy;
            try { policy = ProjectExecutionPolicy.Read(currentRoot!); }
            catch (Exception ex) { throw new InvalidOperationException(PompLanguage.Pick("프로젝트 정책을 확인할 수 없어 실행하지 않았어요: " + ex.Message, "Cannot verify the project policy: " + ex.Message)); }
            if ((bool?)policy?["requireReviewedEdits"] == true)
                throw new InvalidOperationException(PompLanguage.Pick("검토 전용 모드에서는 하위 에이전트와 팀을 실행할 수 없어요.", "Subagents and teams cannot run in a review-only workspace."));
        }

        /// <summary>The prompt text for <paramref name="team"/> (null: <paramref name="message"/> unchanged); throws when the team cannot run.</summary>
        private Task<string> ApplyTeamAsync(string? team, string message, CancellationToken cancellationToken)
        {
            if (string.IsNullOrWhiteSpace(team))
            {
                _backgroundAgents.SetCurrentTurnTeam(null);
                return Task.FromResult(message);
            }

            var teamId = team!.Trim();
            var roster = GetAgentRoster();
            var allTeams = roster.ListTeams();
            var teamObj = allTeams.FirstOrDefault(t => string.Equals(t.Id, teamId, StringComparison.OrdinalIgnoreCase) && !t.Shadowed);
            if (teamObj == null)
            {
                throw new InvalidOperationException(PompLanguage.Pick($"팀 '{teamId}'을(를) 찾을 수 없어요.", $"Team '{teamId}' was not found."));
            }

            if (!string.IsNullOrEmpty(teamObj.Error))
            {
                throw new InvalidOperationException(PompLanguage.Pick($"팀 '{teamId}'에 오류가 있어요: {teamObj.Error}", $"Team '{teamId}' has an error: {teamObj.Error}"));
            }

            if (teamObj.Members.Count < 2)
            {
                throw new InvalidOperationException(PompLanguage.Pick($"팀 '{teamId}'의 구성원 수가 올바르지 않아요.", $"The member count of team '{teamId}' is invalid."));
            }

            if (teamObj.Missing.Count > 0)
            {
                throw new InvalidOperationException(PompLanguage.Pick($"팀 '{teamId}'의 일부 구성원을 찾을 수 없어요: {string.Join(", ", teamObj.Missing)}", $"Some members of team '{teamId}' are missing: {string.Join(", ", teamObj.Missing)}"));
            }

            RefuseInReviewOnlyWorkspace();
            _backgroundAgents.SetCurrentTurnTeam(teamObj.Id);
            return Task.FromResult("/pomp-team " + teamObj.Id + TeamContinuation(teamObj) + " " + message + TeamKnowledge(teamObj.Id));
        }

        private sealed class BuiltinRoleDefinitions
        {
            public BuiltinRoleDefinitions(string engine, Dictionary<string, string> texts) { Engine = engine; Texts = texts; }
            public string Engine { get; }
            public Dictionary<string, string> Texts { get; }
        }

        private readonly SemaphoreSlim _builtinRolesGate = new SemaphoreSlim(1, 1);
        private BuiltinRoleDefinitions? _builtinRoles;

        /// <summary>
        /// The engine's own definition of a built-in role, as <c>omp agents unpack</c> writes it (frontmatter and instructions). The engine
        /// writes all of them into a temporary folder once; they are kept for as long as the same engine is used.
        /// </summary>
        private async Task<string> BuiltinRoleDefinitionAsync(string name)
        {
            var resolution = _resolver.Resolve(_settings.OmpExecutablePath, _environment.ExtensionDirectory);
            var refusal = BundledEngine.Refusal(resolution, _environment.ExtensionDirectory, out _);
            if (refusal != null) throw new InvalidOperationException(refusal);
            if (!resolution.Found) throw new InvalidOperationException(OmpExecutableResolver.DescribeNotFound(resolution));
            var engine = Path.GetFullPath(resolution.Path!);
            await _builtinRolesGate.WaitAsync(_disposeCts.Token).ConfigureAwait(false);
            try
            {
                if (_builtinRoles == null || !string.Equals(_builtinRoles.Engine, engine, StringComparison.OrdinalIgnoreCase))
                {
                    var folder = Path.Combine(Path.GetTempPath(), "pomp-roles-" + Guid.NewGuid().ToString("N"));
                    Directory.CreateDirectory(folder);
                    try
                    {
                        var launch = new OmpLaunchOptions { ExecutablePath = engine, WorkingDirectory = folder };
                        foreach (var argument in new[] { "agents", "unpack", "--dir", folder, "--force" }) launch.Arguments.Add(argument);
                        try { await OmpUsageRunner.RunAsync(_processLauncher, launch, TimeSpan.FromSeconds(30), 64 * 1024, _logger, _disposeCts.Token).ConfigureAwait(false); }
                        catch (ProviderSetupException ex)
                        {
                            _logger.Warn("omp agents unpack failed: " + ex.Message);
                            throw new InvalidOperationException(PompLanguage.Pick("이 역할의 정의를 읽지 못했어요.", "The role's definition could not be read."));
                        }
                        var texts = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                        foreach (var file in Directory.EnumerateFiles(folder, "*.md"))
                        {
                            if (new FileInfo(file).Length <= 256 * 1024) texts[Path.GetFileNameWithoutExtension(file)] = File.ReadAllText(file, Encoding.UTF8);
                        }
                        _builtinRoles = new BuiltinRoleDefinitions(engine, texts);
                    }
                    finally
                    {
                        try { Directory.Delete(folder, true); }
                        catch (IOException) { }
                        catch (UnauthorizedAccessException) { }
                    }
                }

                return _builtinRoles.Texts.TryGetValue(name, out var text) ? text
                    : throw new InvalidOperationException(PompLanguage.Pick("이 역할의 정의를 읽지 못했어요.", "The role's definition could not be read."));
            }
            finally { _builtinRolesGate.Release(); }
        }
    }
}
