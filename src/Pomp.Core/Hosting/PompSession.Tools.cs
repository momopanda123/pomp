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
    public sealed partial class PompSession
    {
        private const string ToolsResultPrefix = "pomp-tools-result:";
        private readonly InstalledSkills? _installedSkills;
        private ToolsStatePayload _toolsView = new ToolsStatePayload();
        private SkillsStatePayload _skillsView = new SkillsStatePayload();
        private RulesStatePayload _rulesView = new RulesStatePayload();
        private ActiveRulesPayload _activeRules = new ActiveRulesPayload();
        private bool _rulesCommandSeen;
        private int _activeRulesRefreshing;
        private string? _activeRulesRequest;
        private DateTime _activeRulesRead;
        /// <summary>The engine answered for the current document, session and rule files: nothing is sent again until one of them changes.</summary>
        private bool _activeRulesFresh;
        private int _activeRulesFailures;
        private DateTime _activeRulesRetryAt;
        /// <summary>Asks again on the next state poll (a document, session, solution or rule file change).</summary>
        private void InvalidateActiveRulesLocked() { _activeRulesRead = DateTime.MinValue; _activeRulesFresh = false; _activeRulesFailures = 0; _activeRulesRetryAt = DateTime.MinValue; }
        /// <summary>A refresh failed (the engine was slow or busy): wait 5 s, 10 s, 20 s ... up to 2 minutes before asking again, so a slow engine is
        /// not sent a new prompt on every state poll.</summary>
        private TimeSpan BackOffActiveRulesLocked()
        {
            _activeRulesFailures = Math.Min(_activeRulesFailures + 1, 16);
            var delay = TimeSpan.FromSeconds(Math.Min(120, 5 * Math.Pow(2, _activeRulesFailures - 1)));
            _activeRulesRetryAt = DateTime.UtcNow + delay;
            return delay;
        }
        private string? _activeRulesDocument;
        private ActiveRulesPayload ActiveRulesView() { lock (_stateLock) return _activeRules; }
        private void ClearActiveRules()
        {
            lock (_resourcePublishLock)
            {
                lock (_stateLock) { _activeRulesRequest = null; InvalidateActiveRulesLocked(); _activeRulesDocument = null; _activeRules = new ActiveRulesPayload { SessionId = _info.SessionId }; }
                _ui.Post(UiMessageTypes.RulesActive, ActiveRulesView(), UiDelivery.State);
            }
        }
        private void RefreshActiveRules()
        {
            OmpConnection? connection; string request;
            lock (_resourcePublishLock)
            {
                bool changed;
                lock (_stateLock)
                {
                    var document = _vsState.ActiveDocumentPath;
                    changed = !string.Equals(_activeRulesDocument, document, StringComparison.OrdinalIgnoreCase);
                    if (changed) { _activeRulesDocument = document; _activeRulesRequest = null; InvalidateActiveRulesLocked(); _activeRules = new ActiveRulesPayload { SessionId = _info.SessionId }; }
                }
                if (changed) _ui.Post(UiMessageTypes.RulesActive, ActiveRulesView(), UiDelivery.State);
            }
            lock (_stateLock)
            {
                // Called on every VS state poll (2 s): it asks the engine only when the answer is not fresh, and after a failure only once the
                // back-off has passed. Asking on every poll kept a /pomp-rules prompt going to the engine every few seconds while idle.
                if (!_rulesCommandSeen || _bridgeState.State != BridgeState.Running || _epochConnection == null || _epochConnection.IsClosed || Volatile.Read(ref _workspaceSwitching) == 1
                    || _activeRulesFresh || DateTime.UtcNow < _activeRulesRetryAt || DateTime.UtcNow - _activeRulesRead < TimeSpan.FromSeconds(2)) return;
                if (Interlocked.Exchange(ref _activeRulesRefreshing, 1) != 0) return;
                _activeRulesRead = DateTime.UtcNow; connection = _epochConnection; request = _activeRulesRequest = Guid.NewGuid().ToString("N");
            }
            Task.Run(async () =>
            {
                try
                {
                    var response = await connection.SendCommandAsync(OmpCommands.Prompt("/pomp-rules --request " + request), TimeSpan.FromSeconds(5), _disposeCts.Token).ConfigureAwait(false);
                    // The result notification comes before the command's response, so the answer is in by now.
                    TimeSpan? retry = null;
                    lock (_stateLock) if (_activeRulesRequest == request) { if (response.Success) { _activeRulesFresh = true; _activeRulesFailures = 0; _activeRulesRetryAt = DateTime.MinValue; } else retry = BackOffActiveRulesLocked(); }
                    if (retry != null) _logger.Debug("Active rules refresh failed: " + response.Error + " (next try in " + retry.Value.TotalSeconds.ToString("0") + " s)");
                }
                catch (Exception ex)
                {
                    TimeSpan? retry = null;
                    lock (_stateLock) if (_activeRulesRequest == request) retry = BackOffActiveRulesLocked();
                    _logger.Debug("Active rules refresh failed: " + ex.Message + (retry != null ? " (next try in " + retry.Value.TotalSeconds.ToString("0") + " s)" : ""));
                }
                finally { Interlocked.Exchange(ref _activeRulesRefreshing, 0); }
            }).Forget(_logger, "active rules");
        }
        private bool HandleActiveRulesNotification(string? message, OmpConnection? origin)
        {
            const string prefix = "pomp-rules-result:";
            if (message == null || !message.StartsWith(prefix, StringComparison.Ordinal)) return false;
            if (message.Length > 50000) return true;
            try
            {
                var result = JObject.Parse(Encoding.UTF8.GetString(Convert.FromBase64String(message.Substring(prefix.Length))));
                var payload = Pomp.Json.PompJson.ToObject<ActiveRulesPayload>(result);
                if (payload == null || payload.Rules == null || payload.Rules.Count > 5 || payload.Rules.Any(r => r == null || r.Name.Length > 256 || r.File.Length > 4096 || r.Globs == null || r.Globs.Count > 32 || r.Globs.Any(g => g == null || g.Length > 512))) return true;
                lock (_resourcePublishLock)
                {
                    lock (_stateLock)
                    {
                        if (origin == null || origin.IsClosed || !ReferenceEquals(origin, _epochConnection) || Volatile.Read(ref _workspaceSwitching) == 1 || result.GetString("sessionId") != _info.SessionId || !string.Equals(NormalizePath(result.GetString("root")), NormalizePath(_workingDirectory), StringComparison.OrdinalIgnoreCase)) return true;
                        if (!string.Equals(result.GetString("activeDocument"), _vsState.ActiveDocumentPath, StringComparison.OrdinalIgnoreCase)) return true;
                        var request = result.GetString("id");
                        if (!string.IsNullOrEmpty(request) && request != _activeRulesRequest) return true;
                        // Catalog identities are shared; the engine's bounded YAML/glob parser selects the next prompt's set.
                        foreach (var rule in payload.Rules)
                        {
                            var catalog = _rulesView.Rules.FirstOrDefault(r => string.Equals(r.File, rule.File, StringComparison.OrdinalIgnoreCase));
                            if (catalog != null) rule.Name = catalog.Name;
                        }
                        _activeRules = payload;
                    }
                    _ui.Post(UiMessageTypes.RulesActive, payload, UiDelivery.State);
                }
            }
            catch (Exception ex) { _logger.Debug("Invalid active rules result: " + ex.Message); }
            return true;
        }
        private McpStatePayload _mcpView = new McpStatePayload();
        private CatalogStatePayload _catalogView = new CatalogStatePayload();
        private JArray _engineSkills = new JArray();
        /// <summary>The file the Skills tab's import dialog chose last (the page refers to it by ticket only).</summary>
        private (string Ticket, string File, string Name)? _skillImport;
        /// <summary>A skills change came in while the engine was busy: rediscover once the reply ends.</summary>
        private bool _skillReloadPending;
        /// <summary>The SKILL.md files (path, size, time) seen at the last list refresh: a reply that wrote one makes the engine rediscover.</summary>
        private string _skillFingerprint = "";
        /// <summary>A /reload-plugins sent by POMP itself is running: its "Plugins reloaded." output is not a chat notice.</summary>
        private int _skillReloadsRunning;
        private bool _mcpRestartRequired;
        private string? _toolsSession;
        private ToolsTicket? _toolsTicket;
        private OutputTicket? _mcpOutput;
        private bool _toolsCommandSeen;
        // Publishers on the frame, command and bridge threads read the current view inside this lock, so a stale snapshot is
        // never posted after a newer one. Lock order: this lock, then _stateLock (released), then the hub lock.
        private readonly object _resourcePublishLock = new object();
        private sealed class ToolsTicket
        {
            internal string Id = "", Session = ""; internal OmpConnection? Origin;
            /// <summary>User set/reset only: background catalog refreshes never lock the page.</summary>
            internal bool Visible;
            internal TaskCompletionSource<JObject> Completion = new TaskCompletionSource<JObject>(TaskCreationOptions.RunContinuationsAsynchronously);
        }
        private sealed class OutputTicket { internal OmpConnection? Origin; internal StringBuilder Text = new StringBuilder(); internal bool Received; }
        private ToolsStatePayload ToolsView() { lock (_stateLock) return _toolsView; }
        private SkillsStatePayload SkillsView() { lock (_stateLock) return _skillsView; }
        private McpStatePayload McpView() { lock (_stateLock) return _mcpView; }
        private CatalogStatePayload CatalogView() { lock (_stateLock) return _catalogView; }
        private RulesStatePayload RulesView() { lock (_stateLock) return _rulesView; }
        private void PublishRules() { lock (_resourcePublishLock) _ui.Post(UiMessageTypes.RulesState, RulesView(), UiDelivery.State); }
        private RuleCatalog RuleFiles() { lock (_stateLock) return new RuleCatalog(_workingDirectory.Length == 0 ? Path.Combine(_environment.DataDirectory, "workspace") : _workingDirectory, _options.UserHome); }
        private void PublishTools() { lock (_resourcePublishLock) _ui.Post(UiMessageTypes.ToolsState, ToolsView(), UiDelivery.State); }
        private void PublishSkills() { lock (_resourcePublishLock) _ui.Post(UiMessageTypes.SkillsState, SkillsView(), UiDelivery.State); }
        private void PublishMcp() { lock (_resourcePublishLock) _ui.Post(UiMessageTypes.McpState, McpView(), UiDelivery.State); }
        private void PublishCatalog() { lock (_resourcePublishLock) _ui.Post(UiMessageTypes.CatalogState, CatalogView(), UiDelivery.State); }
        private string WorkingRoot() { lock (_stateLock) return _workingDirectory.Length == 0 ? Path.Combine(_environment.DataDirectory, "workspace") : _workingDirectory; }
        private PompCatalog CatalogFiles() => new PompCatalog(WorkingRoot(), _options.UserHome, OmpExtensionArguments.FindPackageDirectory(_environment.ExtensionDirectory), _logger);
        private SkillSwitchStore SkillSwitches() => new SkillSwitchStore(_environment.DataDirectory);
        /// <summary>The engine profile the extra arguments or the environment select (null = the default one); shared with the MCP file paths.</summary>
        private string? OmpProfile()
        {
            var args = ArgumentSplitter.Split(_settings.ExtraArguments);
            for (var index = 0; index < args.Count; index++)
            {
                if (args[index] is "--profile" or "--alias") { if (++index >= args.Count) throw new ArgumentException(PompLanguage.Pick("프로필 인수가 비어 있어요.", "The profile argument is empty.")); return args[index]; }
                else if (args[index].StartsWith("--profile=", StringComparison.Ordinal)) return args[index].Substring(10);
                else if (args[index].StartsWith("--alias=", StringComparison.Ordinal)) return args[index].Substring(8);
            }
            var profile = Environment.GetEnvironmentVariable("OMP_PROFILE") ?? Environment.GetEnvironmentVariable("PI_PROFILE");
            return string.IsNullOrEmpty(profile) || profile == "default" ? null : profile;
        }
        /// <summary>The skill names the user's or the workspace's own omp config disables (<c>skill:&lt;name&gt;</c> entries).</summary>
        private HashSet<string> ConfigDisabledSkills()
        {
            var names = new HashSet<string>(StringComparer.Ordinal);
            try
            {
                var root = WorkingRoot();
                foreach (var layer in new[] { OmpConfigLayers.UserConfigPath(_options.UserHome, OmpProfile()), OmpConfigLayers.ProjectConfigPath(root) })
                    foreach (var entry in OmpConfigLayers.Read(layer, _logger).DisabledExtensions)
                        if (entry.StartsWith("skill:", StringComparison.Ordinal) && entry.Length > 6) names.Add(entry.Substring(6));
            }
            catch (Exception ex) when (ex is ArgumentException || ex is NotSupportedException || ex is PathTooLongException || ex is System.Security.SecurityException) { _logger.Warn("Could not read the omp skill configuration: " + ex.Message); }
            return names;
        }
        /// <summary>
        /// The skill names POMP itself switches off: catalog skills whose default is off (minus explicit ons)
        /// plus explicit offs. Built-ins and user skills default to on.
        /// </summary>
        private HashSet<string> EffectiveOffSkills(SkillSwitchStore.Overrides? overrides = null)
        {
            var off = new HashSet<string>(StringComparer.Ordinal);
            var current = overrides ?? SkillSwitches().Load();
            try
            {
                foreach (var skill in CatalogFiles().BuiltinSkills())
                    if (!skill.DefaultEnabled && !current.On.Contains(skill.Id)) off.Add(skill.Id);
            }
            catch (Exception ex) when (ex is ArgumentException || ex is NotSupportedException || ex is PathTooLongException || ex is System.Security.SecurityException)
            {
                _logger.Warn("Could not read the bundled skills: " + ex.Message);
            }

            foreach (var name in current.Off) off.Add(name);
            off.ExceptWith(current.On);
            return off;
        }
        /// <summary>
        /// Writes the <c>--config</c> overlay: POMP's two skill folders followed by the user's and the workspace's own
        /// <c>skills.customDirectories</c> and the bundled catalog skills last, and POMP's effective off-set merged with
        /// the lower layers' <c>disabledExtensions</c> (an overlay array replaces the lower layers, so both must be merged
        /// or the user's own values would be wiped). A user's own copy of a catalog skill wins by directory order.
        /// Atomic, and safe to rewrite while the engine runs: omp watches the file and applies both keys without a restart.
        /// </summary>
        private void WriteOverlay(string overlayPath, string root, IReadOnlyList<string> hostTools, IReadOnlyList<string>? deniedTools, ComputerUseOptions? computer, bool yolo, bool toolUi, string? worktreeBase)
        {
            var home = ToolResourceFiles.Home(_options.UserHome);
            var directories = new List<string> { Path.Combine(root, ".pomp", "skills"), Path.Combine(home, ".pomp", "skills") };
            var disabled = new List<string>();
            var seenDisabled = new HashSet<string>(StringComparer.Ordinal);
            foreach (var name in EffectiveOffSkills().OrderBy(n => n, StringComparer.Ordinal))
            {
                var entry = "skill:" + name;
                if (seenDisabled.Add(entry)) disabled.Add(entry);
            }

            try
            {
                string? profile = null;
                try { profile = OmpProfile(); } catch (ArgumentException ex) { _logger.Warn("Could not read the engine profile: " + ex.Message); }
                foreach (var layer in new[] { OmpConfigLayers.UserConfigPath(_options.UserHome, profile), OmpConfigLayers.ProjectConfigPath(root) })
                {
                    var values = OmpConfigLayers.Read(layer, _logger);
                    foreach (var directory in values.CustomDirectories) if (!directories.Contains(directory)) directories.Add(directory);
                    foreach (var entry in values.DisabledExtensions) if (seenDisabled.Add(entry)) disabled.Add(entry);
                }
            }
            catch (Exception ex) when (ex is ArgumentException || ex is NotSupportedException || ex is PathTooLongException || ex is System.Security.SecurityException)
            {
                _logger.Warn("Could not merge the omp configuration into the overlay: " + ex.Message);
            }

            try
            {
                var catalog = CatalogFiles().SkillsDirectory();
                if (catalog.Length > 0 && Directory.Exists(catalog) && !directories.Contains(catalog)) directories.Add(catalog);
            }
            catch (Exception ex) when (ex is ArgumentException || ex is NotSupportedException || ex is PathTooLongException || ex is System.Security.SecurityException)
            {
                _logger.Warn("Could not add the bundled skills to the overlay: " + ex.Message);
            }

            OmpOverlayWriter.Write(overlayPath, hostTools, deniedTools, computer, yolo, toolUi, directories, worktreeBase, disabled);
        }
        /// <summary>Switches one skill off or on (any source, including engine rows): stored, then the live overlay is rewritten.</summary>
        private void SetSkillEnabled(string name, bool enabled)
        {
            SkillInfoPayload[] rows;
            lock (_stateLock) rows = _skillsView.Skills.Where(s => s.Name == name).ToArray();
            if (rows.Length == 0) throw new InvalidOperationException(PompLanguage.Pick("목록에 없는 스킬이에요.", "This skill is not in the list."));
            // Switching on also switches on everything the skill requires (transitive closure); switching off never cascades.
            var names = enabled ? RequiresClosure(name) : new HashSet<string>(new[] { name }, StringComparer.Ordinal);
            if (!SkillSwitches().SetMany(names, enabled)) return;
            RewriteLiveOverlay();
        }
        /// <summary>Clears the explicit on/off of one skill (back to its default): stored, then the live overlay is rewritten.</summary>
        private void ResetSkillEnabled(string name)
        {
            SkillInfoPayload[] rows;
            lock (_stateLock) rows = _skillsView.Skills.Where(s => s.Name == name).ToArray();
            if (rows.Length == 0) throw new InvalidOperationException(PompLanguage.Pick("목록에 없는 스킬이에요.", "This skill is not in the list."));
            if (!SkillSwitches().Reset(name)) return;
            RewriteLiveOverlay();
        }
        /// <summary>The skill itself plus every catalog skill it requires, transitively (cycle-safe).</summary>
        private HashSet<string> RequiresClosure(string name)
        {
            var closure = new HashSet<string>(StringComparer.Ordinal) { name };
            try
            {
                var catalog = CatalogFiles().Load().Where(e => e.Kind == "skill").ToDictionary(e => e.Id, e => e.Requires, StringComparer.Ordinal);
                var queue = new Queue<string>(new[] { name });
                while (queue.Count > 0 && closure.Count <= PompCatalog.MaxEntries)
                    if (catalog.TryGetValue(queue.Dequeue(), out var requires))
                        foreach (var id in requires)
                            if (closure.Add(id)) queue.Enqueue(id);
            }
            catch (Exception ex) when (ex is ArgumentException || ex is NotSupportedException || ex is PathTooLongException || ex is System.Security.SecurityException)
            {
                _logger.Warn("Could not read the catalog requirements: " + ex.Message);
            }

            return closure;
        }
        private void RewriteLiveOverlay()
        {
            var dataDirectory = Path.GetFullPath(_environment.DataDirectory);
            var root = WorkingRoot();
            var hostTools = _registry.GetDefinitions().Select(definition => definition.Name).ToList();
            var blockNetwork = _settings.BlockOmpNetworkTools;
            ComputerUseOptions computer;
            lock (_computerLock) computer = _computerOptions;
            var extensionArguments = _settings.LoadOmpExtensions ? OmpExtensionArguments.Build(_environment.ExtensionDirectory ?? string.Empty) : Array.Empty<string>();
            var worktreeBase = extensionArguments.Count > 0 && OmpExtensionArguments.ContainsModule(_environment.ExtensionDirectory, "./extensions/teams.ts") ? ProjectData.Folder(root, "wt") : null;
            WriteOverlay(Path.Combine(dataDirectory, "omp", "pomp-overlay-" + _engineKey + ".yml"), root, hostTools, blockNetwork ? OmpNetworkTools.Names : null, computer, _settings.ApprovalMode == OmpApprovalMode.Yolo, _settings.UseRpcUiMode, worktreeBase);
        }
        // ui/ready holds the hub lock while taking the session snapshot: never take the hub lock under _stateLock.
        private bool ResourcesBusy() { var pending = _ui.PendingRequestCount > 0; lock (_stateLock) return pending || ResourcesBusyLocked(); }
        private bool ResourcesBusyLocked() => _turns.Count > 0 || _info.IsStreaming || _sessionSwitchUncertain || inlineSlot.CurrentCount == 0;
        private static string ResourceBusyMessage() => PompLanguage.Pick("응답이나 승인 요청이 진행 중이에요.", "A reply or approval request is in progress.");
        private SkillCatalog SkillFiles() { lock (_stateLock) return new SkillCatalog(_workingDirectory.Length == 0 ? Path.Combine(_environment.DataDirectory, "workspace") : _workingDirectory, _options.UserHome); }
        private NativeMcpConfig McpFiles()
        {
            lock (_stateLock) return new NativeMcpConfig(_workingDirectory.Length == 0 ? Path.Combine(_environment.DataDirectory, "workspace") : _workingDirectory, _options.UserHome, OmpProfile());
        }
        private void QueueResourceCommand(UiMessage message, string reply, Func<JObject, Task> handler)
        {
            // Reading a list or one file for the editor stays allowed while a reply runs; changes wait.
            var payload = message.Payload ?? new JObject(); var reject = ResourcesBusy() && (reply == UiMessageTypes.ToolsReply || payload.GetString("action") is not ("refresh" or "read" or "get"));
            if (reject) _ui.Post(reply, new UiCommandReplyPayload { RequestId = payload.GetString("requestId") ?? "", Ok = false, Error = ResourceBusyMessage() }, UiDelivery.State);
            else _commands.Enqueue(() => handler(payload));
        }
        private async Task HandleToolsAsync(JObject payload)
        {
            var requestId = payload.GetString("requestId") ?? ""; string? error = null;
            try
            {
                var command = new UiMessage("", payload).PayloadAs<ToolsCommandPayload>() ?? throw new ArgumentException();
                if (command.Action is not ("refresh" or "set" or "reset")) throw new ArgumentException(PompLanguage.Pick("알 수 없는 도구 동작이에요.", "Unknown tools action."));
                if (ResourcesBusy()) throw new InvalidOperationException(ResourceBusyMessage());
                await SendToolsAsync(command.Action, command.Active).ConfigureAwait(false);
            }
            catch (Exception ex) { error = ResourceError(ex); }
            _ui.Post(UiMessageTypes.ToolsReply, new UiCommandReplyPayload { RequestId = requestId, Ok = error == null, Error = error }, UiDelivery.State);
        }
        private async Task SendToolsAsync(string action, IReadOnlyList<string>? active = null)
        {
            ToolsTicket ticket;
            lock (_stateLock)
            {
                if (_bridgeState.State != BridgeState.Running || _epochConnection == null) throw new InvalidOperationException(PompLanguage.Pick("연결되어 있지 않아요.", "Not connected."));
                ticket = new ToolsTicket { Id = Guid.NewGuid().ToString("N"), Session = _info.SessionId ?? "", Origin = _epochConnection, Visible = action != "refresh" };
            }
            try { await RequireEngineCommandsAsync(ticket.Origin, _disposeCts.Token, "pomp-tools").ConfigureAwait(false); }
            catch
            {
                lock (_stateLock) _toolsView = new ToolsStatePayload { SessionId = _info.SessionId, Message = PompLanguage.Pick("도구 선택을 사용할 수 없어요.", "Tool selection is unavailable.") };
                PublishTools(); throw;
            }
            if (ResourcesBusy()) throw new InvalidOperationException(ResourceBusyMessage());
            lock (_stateLock)
            {
                if (ResourcesBusyLocked() || ticket.Session != _info.SessionId || !ReferenceEquals(ticket.Origin, _epochConnection)) throw new InvalidOperationException(ResourceBusyMessage());
                if (action == "set" && (active == null || active.Any(n => n == null || !_toolsView.Tools.Any(t => t.Name == n)))) throw new ArgumentException(PompLanguage.Pick("도구 선택이 올바르지 않아요.", "The tool selection is invalid."));
                _toolsTicket = ticket;
                if (ticket.Visible) _toolsView = new ToolsStatePayload { SessionId = _info.SessionId, Tools = _toolsView.Tools, Available = _toolsView.Available, Pending = true, Message = _toolsView.Message };
            }
            if (ticket.Visible) PublishTools();
            try
            {
                var input = action == "set" ? "set " + new JArray(active!).ToString(Formatting.None) : action == "reset" ? "reset" : "list";
                var response = await ticket.Origin!.SendCommandAsync(OmpCommands.Prompt("/pomp-tools " + input + " --request " + ticket.Id), TimeSpan.FromSeconds(10), _disposeCts.Token).ConfigureAwait(false);
                if (!response.Success) throw new InvalidOperationException(PompLanguage.Pick("도구 선택을 적용하지 못했어요.", "Tool selection could not be applied."));
                if (!await AsyncUtil.WaitForAsync(ticket.Completion.Task, TimeSpan.FromSeconds(3)).WithCancellation(_disposeCts.Token).ConfigureAwait(false)) throw new TimeoutException(PompLanguage.Pick("도구 선택 결과가 오지 않았어요.", "Tool selection did not return a result."));
                var result = await ticket.Completion.Task.ConfigureAwait(false);
                if (result.GetString("error") is string engineError) throw new InvalidOperationException(Bound(engineError, 500));
                if (result.GetBool("available") != true) throw new InvalidOperationException(PompLanguage.Pick("도구 선택을 사용할 수 없어요.", "Tool selection is unavailable."));
            }
            finally
            {
                lock (_stateLock)
                {
                    if (_toolsTicket == ticket) _toolsTicket = null;
                    _toolsView = new ToolsStatePayload { SessionId = _toolsView.SessionId, Tools = _toolsView.Tools, Available = _toolsView.Available, Pending = false, Message = _toolsView.Message };
                }
                PublishTools();
            }
        }
        private bool HandleToolsNotification(string? message, OmpConnection? origin)
        {
            if (message == null || !message.StartsWith(ToolsResultPrefix, StringComparison.Ordinal)) return false;
            // Consume malformed and late notifications too: management data never belongs in the transcript.
            // No tool count limit; the notification only has to fit the engine bridge's line (8 MiB).
            if (message.Length > 6 * 1024 * 1024) return true;
            try
            {
                var result = JObject.Parse(Encoding.UTF8.GetString(Convert.FromBase64String(message.Substring(ToolsResultPrefix.Length))));
                lock (_stateLock)
                {
                    if (origin == null || origin.IsClosed || !ReferenceEquals(origin, _epochConnection)) return true;
                    var session = result.GetString("sessionId"); if (session != null && _info.SessionId != null && session != _info.SessionId) return true;
                    var id = result.GetString("id");
                    if (!string.IsNullOrEmpty(id) && (_toolsTicket == null || _toolsTicket.Id != id || !ReferenceEquals(_toolsTicket.Origin, origin))) return true;
                    var active = ToolNames(result["active"]); var defaults = ToolNames(result["defaults"]); var host = new HashSet<string>(_registry.GetDefinitions().Select(t => t.Name), StringComparer.Ordinal);
                    var servers = _mcpView.Servers.Select(s => s.Name).Distinct().OrderByDescending(s => s.Length).ToArray();
                    var rows = (result["all"] as JArray ?? throw new JsonException()).OfType<JObject>().Where(t => PlainToolName(t.GetString("name"))).GroupBy(t => t.GetString("name")!, StringComparer.Ordinal).Select(group =>
                    {
                        var tool = group.First(); var name = tool.GetString("name")!; var server = McpServerOf(name, servers); var kind = host.Contains(name) ? "host" : tool.GetString("source") == "mcp" || name.StartsWith("mcp__", StringComparison.Ordinal) ? "mcp" : BuiltinToolGroup(name);
                        return new ToolSelectionPayload { Name = name, Group = kind, Server = kind == "mcp" ? server : null, Description = Bound(new string((tool.GetString("description") ?? "").Where(c => !char.IsControl(c)).ToArray()), 200), Active = active.Contains(name), DefaultActive = defaults.Contains(name) };
                    }).OrderBy(t => t.Group, StringComparer.Ordinal).ThenBy(t => t.Server, StringComparer.Ordinal).ThenBy(t => t.Name, StringComparer.Ordinal).ToList();
                    _toolsSession = session ?? _info.SessionId;
                    // The page already explains the per-conversation defaults; Message is kept for exceptional states only.
                    _toolsView = new ToolsStatePayload { SessionId = _toolsSession, Tools = rows, Available = result.GetBool("available") == true, Pending = _toolsTicket?.Visible == true };
                    // No count limit: every tool stays (Apply sends the names of all of them). A message that would pass the UI hub's size leaves out the
                    // descriptions of the last tools instead.
                    long toolsBytes = StateBudget.Bytes(new UiMessage(UiMessageTypes.ToolsState, _toolsView));
                    for (var index = rows.Count - 1; index >= 0 && toolsBytes > StateBudget.Limit; index--)
                    {
                        toolsBytes -= System.Text.Encoding.UTF8.GetByteCount(rows[index].Description ?? "");
                        rows[index].Description = null;
                    }
                    if (!string.IsNullOrEmpty(id)) _toolsTicket?.Completion.TrySetResult(result);
                }
                PublishTools();
            }
            catch (Exception ex) when (ex is FormatException or JsonException or InvalidCastException or ArgumentException) { _logger.Debug("Ignored malformed tool-selection result."); }
            return true;
        }
        /// <summary>
        /// Whether the current conversation can call <c>read_buffer</c>, so a composed prompt names it only then: the engine's tool snapshot of
        /// this conversation (active and xd://-mounted tools; an agent conversation has its role's tools only, docs/architecture.md §8.8), or,
        /// before a snapshot of this conversation arrived, whether the host offers the tool.
        /// </summary>
        private bool CanReadBuffer()
        {
            lock (_stateLock)
            {
                if (_toolsView.Available && _toolsView.SessionId != null && _toolsView.SessionId == _info.SessionId)
                    return _toolsView.Tools.Any(t => t.Name == Pomp.Tools.HostToolNames.ReadBuffer && t.Active);
            }
            return _registry.TryGet(Pomp.Tools.HostToolNames.ReadBuffer, out _);
        }
        private static bool PlainToolName(string? name) => name != null && name.Length is > 0 and <= 256 && !name.Any(char.IsControl);
        private static HashSet<string> ToolNames(JToken? value) => value is JArray array ? new HashSet<string>(array.Where(t => t.Type == JTokenType.String).Select(t => (string)t!).Where(PlainToolName), StringComparer.Ordinal) : throw new JsonException();
        internal static string? McpServerOf(string name, IEnumerable<string> servers)
        {
            foreach (var server in servers.OrderByDescending(s => s.Length))
            {
                var normalized = System.Text.RegularExpressions.Regex.Replace(System.Text.RegularExpressions.Regex.Replace(server.ToLowerInvariant(), "[^a-z0-9_]+", "_"), "_+", "_").Trim('_');
                if (normalized.Length == 0) normalized = "server";
                if (name.StartsWith("mcp__" + normalized + "_", StringComparison.Ordinal)) return server;
            }
            if (!name.StartsWith("mcp__", StringComparison.Ordinal)) return null;
            var remainder = name.Substring(5); var alias = remainder.IndexOf("__", StringComparison.Ordinal); var separator = alias >= 0 ? alias : remainder.IndexOf('_');
            return separator > 0 ? remainder.Substring(0, separator) : null;
        }
        internal static string BuiltinToolGroup(string name) => name switch
        {
            "read" or "find" or "grep" or "ls" or "glob" or "search" or "code_search" or "lsp" or "ast_grep" or "ast_grep_search" => "files",
            "edit" or "write" or "apply_patch" or "notebook" => "edit",
            "bash" or "exec" or "eval" or "python" or "terminal" => "exec",
            "web_search" or "fetch" or "web_fetch" or "browser" => "web",
            "task" or "agent" or "subagent" or "send_message" or "agent_message" or "wait" or "ask" or "todo_write" => "agents",
            _ => "other"
        };
        private async Task RefreshSkillsAsync()
        {
            var installed = new SkillsStatePayload();
            if (_installedSkills != null) try { installed = await _installedSkills.ViewAsync(_disposeCts.Token).ConfigureAwait(false); } catch (Exception ex) { installed.Diagnostics.Add(ResourceError(ex)); }
            JArray commands; lock (_stateLock) commands = _engineSkills;
            var off = EffectiveOffSkills();
            var builtins = CatalogFiles().BuiltinSkills();
            var files = SkillFiles();
            string fingerprint;
            try { fingerprint = files.Fingerprint(); } catch (Exception ex) { fingerprint = ""; _logger.Debug("Could not check the skill folders: " + ex.Message); }
            var catalog = files.View(commands, installed.Skills, builtins, off, ConfigDisabledSkills()); catalog.Diagnostics.AddRange(installed.Diagnostics.Take(Math.Max(0, 20 - catalog.Diagnostics.Count)));
            lock (_stateLock)
            {
                // A disconnect or newer command catalog wins over a slow installed-provider/filesystem refresh.
                if (!ReferenceEquals(commands, _engineSkills)) { _commands.Enqueue(RefreshSkillsAsync); return; }
                // The engine discovered the skills when it started: the first look is its baseline; later only a reload moves it.
                _skillsView = catalog; if (_skillFingerprint.Length == 0) _skillFingerprint = fingerprint;
            }
            PublishSkills();
        }
        private async Task HandleSkillsAsync(JObject payload)
        {
            var id = payload.GetString("requestId") ?? ""; string? error = null; ResourceDocumentPayload? document = null;
            var engineReload = false;
            try
            {
                var command = new UiMessage("", payload).PayloadAs<SkillsCommandPayload>() ?? throw new ArgumentException(); string? open = null;
                if (command.Action != "refresh" && command.Action != "read" && ResourcesBusy()) throw new InvalidOperationException(ResourceBusyMessage());
                // The dialog is modal over VS and may stay open a while: it answers on its own, outside the command queue.
                if (command.Action == "pick") { PickSkillImportAsync(id).Forget(_logger, "skill import picker"); return; }
                // Every change and the refresh button make the engine rediscover its skills: a new file is usable at once.
                engineReload = command.Action is "refresh" or "create" or "import" or "update" or "delete";
                // Created and imported files are edited in the page now; only the explicit open action opens the VS editor.
                if (command.Action == "create") SkillFiles().Create(command.Scope ?? "", command.Name ?? "", command.Description ?? "", command.Body);
                else if (command.Action == "import") ImportPickedSkill(command.Scope ?? "", command.Ticket);
                else if (command.Action == "update") SkillFiles().Update(command.Scope ?? "", command.Name ?? "", command.ExpectedHash, command.Description, command.Body, command.Content);
                else if (command.Action == "read")
                {
                    if (command.Scope != null) document = SkillFiles().Document(command.Scope, command.Name ?? "");
                    else
                    {
                        // A built-in or installed row (copied into the user's own skills) comes from the verified package path.
                        SkillInfoPayload[] rows;
                        lock (_stateLock) rows = _skillsView.Skills.Where(s => s.Name == command.Name && s.Source is "omp" or "installed" or "pomp" && s.File != null).ToArray();
                        if (rows.Length != 1) throw new InvalidOperationException(PompLanguage.Pick("읽을 수 있는 스킬 파일을 찾지 못했어요.", "No unambiguous skill file is available to read."));
                        document = SkillCatalog.DocumentOf(rows[0].File!, rows[0].Name);
                    }
                }
                else if (command.Action == "delete") SkillFiles().Delete(command.Scope ?? "", command.Name ?? "", command.ExpectedHash);
                else if (command.Action is "enable" or "disable") SetSkillEnabled(command.Name ?? "", command.Action == "enable");
                else if (command.Action == "reset") ResetSkillEnabled(command.Name ?? "");
                else if (command.Action == "open")
                {
                    await RefreshSkillsAsync().ConfigureAwait(false); SkillInfoPayload[] rows;
                    lock (_stateLock) rows = _skillsView.Skills.Where(s => s.Name == command.Name && (command.Scope == null ? s.Source is "omp" or "installed" or "pomp" : s.Source == command.Scope)).ToArray();
                    if (rows.Length != 1 || rows[0].File == null) throw new InvalidOperationException(PompLanguage.Pick("열 수 있는 스킬 파일을 찾지 못했어요.", "No unambiguous skill file is available to open.")); open = rows[0].File;
                }
                else if (command.Action != "refresh") throw new ArgumentException(PompLanguage.Pick("알 수 없는 스킬 동작이에요.", "Unknown skills action."));
                if (command.Action != "read") await RefreshSkillsAsync().ConfigureAwait(false);
                if (engineReload) await ReloadEngineSkillsAsync().ConfigureAwait(false);
                if (open != null)
                {
                    CheckpointStore.CheckDirectPath(open);
                    if (!File.Exists(open)) throw new FileNotFoundException(PompLanguage.Pick("스킬 파일이 더 이상 없어요.", "The skill file no longer exists."));
                    var opened = await AdapterGuard.TryRunAsync(token => _adapters.Editor.OpenDocumentAsync(open, null, token), _options.AdapterTimeout, _logger, "open skill", _disposeCts.Token).ConfigureAwait(false);
                    if (!opened.Succeeded || !opened.Value) throw new InvalidOperationException(PompLanguage.Pick("스킬 파일을 VS 편집기에서 열지 못했어요.", "Could not open the skill in the VS editor."));
                }
            }
            catch (Exception ex) { error = ResourceError(ex); }
            _ui.Post(UiMessageTypes.SkillsReply, new UiCommandReplyPayload { RequestId = id, Ok = error == null, Error = error, Document = error == null ? document : null }, UiDelivery.State);
        }
        /// <summary>skills pick: the host's open-file dialog, then a preview of what import would copy (no reply field when it was closed).</summary>
        private async Task PickSkillImportAsync(string requestId)
        {
            string? error = null; SkillImportPreviewPayload? preview = null;
            try
            {
                if (!(_environment is IPompFilePicker picker)) throw new InvalidOperationException(PompLanguage.Pick("파일을 고를 수 없어요.", "Choosing a file is not available."));
                var root = await TryGetWorkspaceRootAsync().ConfigureAwait(false);
                var files = await picker.PickFilesAsync(root, PompLanguage.Pick("가져올 스킬 파일", "Skill file to import"),
                    PompLanguage.Pick("스킬 (SKILL.md, *.md)|SKILL.md;*.md", "Skills (SKILL.md, *.md)|SKILL.md;*.md"), false, _disposeCts.Token).ConfigureAwait(false);
                if (files.Count == 1)
                {
                    var plan = SkillCatalog.PlanImport(files[0]);
                    var ticket = Guid.NewGuid().ToString("N");
                    lock (_stateLock) _skillImport = (ticket, plan.File, plan.Name);
                    preview = new SkillImportPreviewPayload { Ticket = ticket, Name = plan.Name, Description = plan.Description, File = plan.File, Files = plan.Files, Bytes = plan.Bytes };
                }
            }
            catch (OperationCanceledException) when (_disposeCts.IsCancellationRequested) { return; }
            catch (Exception ex) { error = ResourceError(ex); }
            _ui.Post(UiMessageTypes.SkillsReply, new UiCommandReplyPayload { RequestId = requestId, Ok = error == null, Error = error, SkillImport = preview }, UiDelivery.State);
        }
        /// <summary>skills import: the picked file is read again (it may have changed since the preview) and copied into the scope.</summary>
        private void ImportPickedSkill(string scope, string? ticket)
        {
            (string Ticket, string File, string Name)? picked;
            lock (_stateLock) picked = _skillImport;
            if (picked == null || string.IsNullOrEmpty(ticket) || picked.Value.Ticket != ticket) throw new InvalidOperationException(PompLanguage.Pick("가져올 파일을 다시 골라 주세요.", "Choose the file to import again."));
            var plan = SkillCatalog.PlanImport(picked.Value.File);
            if (plan.Name != picked.Value.Name) throw new InvalidOperationException(PompLanguage.Pick("파일이 바뀌었어요. 다시 골라 주세요.", "The file changed. Choose it again."));
            SkillFiles().Import(scope, plan);
            lock (_stateLock) if (_skillImport?.Ticket == ticket) _skillImport = null;
        }
        /// <summary>
        /// Makes the engine rediscover its skills and file commands (omp's <c>/reload-plugins</c>, no model request), so a skill
        /// file written by the page, the model or by hand is usable without a restart. While a reply or request runs it waits for the reply's end.
        /// </summary>
        private async Task ReloadEngineSkillsAsync()
        {
            OmpConnection? connection;
            lock (_stateLock)
            {
                connection = _epochConnection;
                if (_bridgeState.State != BridgeState.Running || connection == null || connection.IsClosed || Volatile.Read(ref _workspaceSwitching) == 1) return;
            }
            if (ResourcesBusy()) { lock (_stateLock) _skillReloadPending = true; return; }
            // Taken before the reload: a file written while it runs still differs afterwards and is caught at the next reply's end.
            string fingerprint;
            try { fingerprint = SkillFiles().Fingerprint(); } catch (Exception ex) { fingerprint = ""; _logger.Debug("Could not check the skill folders: " + ex.Message); }
            lock (_stateLock) { _skillReloadPending = false; _skillFingerprint = fingerprint; }
            Interlocked.Increment(ref _skillReloadsRunning);
            try
            {
                var response = await connection.SendCommandAsync(OmpCommands.Prompt("/reload-plugins"), TimeSpan.FromSeconds(30), _disposeCts.Token).ConfigureAwait(false);
                if (!response.Success) _logger.Warn("The engine did not reload its skills: " + response.Error);
            }
            catch (Exception ex) when (!(ex is OperationCanceledException && _disposeCts.IsCancellationRequested)) { _logger.Warn("The engine did not reload its skills: " + ex.Message); }
            finally { Interlocked.Decrement(ref _skillReloadsRunning); }
        }
        /// <summary>True when <paramref name="frame"/> is the output of POMP's own skill reload (not shown in the chat).</summary>
        private bool ObserveSkillReloadOutput(OmpFrame frame) =>
            Volatile.Read(ref _skillReloadsRunning) > 0 && frame.Json.GetString("text") == "Plugins reloaded.";
        /// <summary>After a reply: when a SKILL.md was added, changed or removed (the model may write one) or a change waited, the list and the engine catch up.</summary>
        private async Task CatchUpSkillsAfterReplyAsync()
        {
            bool pending; string before;
            lock (_stateLock) { pending = _skillReloadPending; before = _skillFingerprint; }
            string now;
            try { now = SkillFiles().Fingerprint(); }
            catch (Exception ex) { _logger.Debug("Could not check the skill folders: " + ex.Message); return; }
            if (!pending && now == before) return;
            await RefreshSkillsAsync().ConfigureAwait(false);
            await ReloadEngineSkillsAsync().ConfigureAwait(false);
        }
        private void RefreshRules()
        {
            RulesStatePayload view;
            try { view = RuleFiles().View(); }
            catch (Exception ex) { view = new RulesStatePayload(); view.Diagnostics.Add(ResourceError(ex)); }
            lock (_stateLock) _rulesView = view;
            PublishRules();
            lock (_stateLock) InvalidateActiveRulesLocked();
            RefreshActiveRules();
        }
        /// <summary>Rule files only: nothing here runs a model, and a rule never grants tool permissions.</summary>
        private async Task HandleRulesAsync(JObject payload)
        {
            var id = payload.GetString("requestId") ?? ""; string? error = null; ResourceDocumentPayload? document = null;
            try
            {
                var command = new UiMessage("", payload).PayloadAs<RulesCommandPayload>() ?? throw new ArgumentException(); string? open = null;
                if (command.Action != "refresh" && command.Action != "read" && ResourcesBusy()) throw new InvalidOperationException(ResourceBusyMessage());
                // Created and imported files are edited in the page now; only the explicit open action opens the VS editor.
                if (command.Action == "create") RuleFiles().Create(command.Scope ?? "", command.Name ?? "", command.Description ?? "", command.AlwaysApply, command.Globs, command.Body);
                else if (command.Action == "import") RuleFiles().Import(command.Scope ?? "", command.Name ?? "", command.Content ?? "");
                else if (command.Action == "read") document = RuleFiles().Document(command.Scope ?? "", command.Name ?? "");
                else if (command.Action == "update") RuleFiles().Update(command.Scope ?? "", command.Name ?? "", command.ExpectedHash, command.Description, command.AlwaysApply, command.Globs, command.Body, command.Content);
                else if (command.Action == "delete") RuleFiles().Delete(command.Scope ?? "", command.Name ?? "", command.ExpectedHash);
                else if (command.Action == "open") open = RuleFiles().FileOf(command.Scope ?? "", command.Name ?? "");
                else if (command.Action != "refresh") throw new ArgumentException(PompLanguage.Pick("알 수 없는 규칙 동작이에요.", "Unknown rules action."));
                if (command.Action != "read") RefreshRules();
                if (open != null)
                {
                    var opened = await AdapterGuard.TryRunAsync(token => _adapters.Editor.OpenDocumentAsync(open, null, token), _options.AdapterTimeout, _logger, "open rule", _disposeCts.Token).ConfigureAwait(false);
                    if (!opened.Succeeded || !opened.Value) throw new InvalidOperationException(PompLanguage.Pick("규칙 파일을 VS 편집기에서 열지 못했어요.", "Could not open the rule in the VS editor."));
                }
            }
            catch (Exception ex) { error = ResourceError(ex); }
            _ui.Post(UiMessageTypes.RulesReply, new UiCommandReplyPayload { RequestId = id, Ok = error == null, Error = error, Document = error == null ? document : null }, UiDelivery.State);
        }
        private void RefreshMcp()
        {
            McpStatePayload view;
            try { view = McpFiles().View(); } catch { view = new McpStatePayload { Message = PompLanguage.Pick("MCP 설정을 읽을 수 없어요.", "Cannot read the MCP configuration.") }; }
            lock (_stateLock)
            {
                view.RestartRequired = _mcpRestartRequired;
                if (_mcpRestartRequired) view.Message = PompLanguage.Pick("변경 내용이 아직 적용되지 않았어요.", "The changes are not applied yet.");
                _mcpView = view;
            }
            PublishMcp();
        }
        private async Task HandleMcpAsync(JObject payload)
        {
            var id = payload.GetString("requestId") ?? ""; string? error = null, output = null;
            try
            {
                var command = new UiMessage("", payload).PayloadAs<McpCommandPayload>() ?? throw new ArgumentException();
                if (command.Action != "refresh" && ResourcesBusy()) throw new InvalidOperationException(ResourceBusyMessage());
                if (command.Action == "restart")
                {
                    await RestartAsync(_disposeCts.Token).ConfigureAwait(false);
                    if (_bridge.State != BridgeState.Running) throw new InvalidOperationException(PompLanguage.Pick("다시 시작하지 못했어요.", "The restart failed."));
                }
                else if (command.Action == "test") output = await TestMcpAsync(command.Name ?? "").ConfigureAwait(false);
                else if (command.Action != "refresh")
                {
                    var store = McpFiles();
                    // The project file is engine configuration under workspace trust: the user's own change here keeps a trust they gave.
                    var trusted = _workspaceTrust.Before(store.ProjectRoot, _settings.LoadDiscoveredOmpExtensions);
                    try { store.Mutate(command); }
                    finally
                    {
                        if (store.Changed)
                        {
                            lock (_stateLock) _mcpRestartRequired = true;
                            _workspaceTrust.KeepAfterOwnWrite(trusted, ".omp/mcp.json");
                        }
                    }
                }
                // A bundled server that was deleted is offered again in the Extensions list: the catalog says where it is installed.
                RefreshMcp(); RefreshCatalog();
            }
            catch (Exception ex) { error = ResourceError(ex); RefreshMcp(); RefreshCatalog(); }
            _ui.Post(UiMessageTypes.McpReply, new McpReplyPayload { RequestId = id, Ok = error == null, Error = error, Output = output }, UiDelivery.State);
        }
        private async Task<string> TestMcpAsync(string name)
        {
            NativeMcpConfig.ValidateName(name);
            // Stock /mcp test currently reads only the first whitespace token, so never test a different server accidentally.
            if (name.Contains(' ')) throw new ArgumentException(PompLanguage.Pick("서버 이름에 공백이 있어요.", "The server name contains spaces."));
            OutputTicket ticket;
            lock (_stateLock)
            {
                if (_bridgeState.State != BridgeState.Running || _epochConnection == null) throw new InvalidOperationException(PompLanguage.Pick("연결되어 있지 않아요.", "Not connected."));
                ticket = new OutputTicket { Origin = _epochConnection };
            }
            var catalog = await ticket.Origin!.SendCommandAsync(new JObject { ["type"] = "get_available_commands" }, TimeSpan.FromSeconds(5), _disposeCts.Token).ConfigureAwait(false);
            if (!catalog.Success || catalog.Data?["commands"] is not JArray commands || !commands.OfType<JObject>().Any(c => c.GetString("name") == "mcp" && c.GetString("source") == "builtin"))
                throw new InvalidOperationException(PompLanguage.Pick("MCP 명령을 쓸 수 없어요. 테스트 요청은 보내지 않았어요.", "The MCP command is unavailable. No test prompt was sent."));
            if (ResourcesBusy()) throw new InvalidOperationException(ResourceBusyMessage());
            lock (_stateLock)
            {
                if (ResourcesBusyLocked() || !ReferenceEquals(ticket.Origin, _epochConnection)) throw new InvalidOperationException(ResourceBusyMessage());
                _mcpOutput = ticket;
            }
            try
            {
                var result = await ticket.Origin!.SendCommandAsync(OmpCommands.Prompt("/mcp test " + name), TimeSpan.FromSeconds(45), _disposeCts.Token).ConfigureAwait(false);
                if (!result.Success) throw new InvalidOperationException(PompLanguage.Pick("MCP 테스트를 시작하지 못했어요.", "The MCP test could not be started."));
                lock (_stateLock)
                {
                    if (!ReferenceEquals(ticket.Origin, _epochConnection)) throw new OperationCanceledException();
                    if (!ticket.Received) throw new InvalidOperationException(PompLanguage.Pick("MCP 테스트 출력이 오지 않았어요.", "The MCP test returned no command output."));
                    return ticket.Text.ToString();
                }
            }
            finally { lock (_stateLock) if (_mcpOutput == ticket) _mcpOutput = null; }
        }
        private bool ObserveMcpOutput(OmpFrame frame)
        {
            lock (_stateLock)
            {
                if (_mcpOutput == null || !ReferenceEquals(_mcpOutput.Origin, _bridge.OriginOf(frame))) return false;
                var text = StripTerminalEscapes(frame.Json.GetString("text") ?? ""); _mcpOutput.Received = true;
                if (_mcpOutput.Text.Length > 0 && text.Length > 0) _mcpOutput.Text.Append('\n');
                // Bound before concatenation as well as by UTF-8 bytes (8 KiB), including multibyte output.
                _mcpOutput.Text.Append(Bound(text, 8192)); var combined = _mcpOutput.Text.ToString();
                while (Encoding.UTF8.GetByteCount(combined) > 8192) combined = combined.Substring(0, combined.Length - (combined.Length > 1 && char.IsLowSurrogate(combined[combined.Length - 1]) ? 2 : 1));
                _mcpOutput.Text.Clear(); _mcpOutput.Text.Append(combined); return true;
            }
        }
        private static string ResourceError(Exception ex) => ex is JsonException or InvalidCastException ? PompLanguage.Pick("요청이나 설정 형식이 올바르지 않아요.", "The request or configuration format is invalid.") : ex is OperationCanceledException ? PompLanguage.Pick("연결이나 대화가 바뀌어 요청을 취소했어요.", "Request cancelled because the connection or conversation changed.") : Bound(ex.Message, 500);
        private void ObserveSkillCommands(JArray rows)
        {
            var normalized = new JArray(rows.OfType<JObject>().Where(r => r.GetString("source") == "skill" && System.Text.RegularExpressions.Regex.IsMatch(r.GetString("name") ?? "", @"\Askill:[a-zA-Z0-9][a-zA-Z0-9._:/-]{0,127}\z") && (r.GetString("name")!.Substring(6).IndexOf('/') < 0 || r.GetString("name")!.Substring(6).IndexOf('/') == r.GetString("name")!.Substring(6).LastIndexOf('/'))).GroupBy(r => r.GetString("name"), StringComparer.Ordinal).Select(g => g.First()).Select(r => new JObject { ["name"] = r.GetString("name"), ["description"] = CommandText(r.GetString("description")) }));
            var toolsCommand = rows.OfType<JObject>().Any(r => r.GetString("name") == "pomp-tools");
            var rulesCommand = rows.OfType<JObject>().Any(r => r.GetString("name") == "pomp-rules");
            lock (_stateLock)
            {
                // The engine re-announces its catalog often (also after our own /pomp-tools prompts): refresh only on a real change,
                // or a refresh would send another prompt and keep the page busy in a loop.
                if (JToken.DeepEquals(_engineSkills, normalized) && toolsCommand == _toolsCommandSeen && rulesCommand == _rulesCommandSeen) return;
                _engineSkills = normalized; _toolsCommandSeen = toolsCommand;
                _rulesCommandSeen = rulesCommand;
            }
            _commands.Enqueue(RefreshResourcesAsync);
        }
        private void ResourceSessionObserved(string? session)
        {
            if (session == null) return;
            lock (_stateLock)
            {
                if (_toolsSession == session) return;
                _toolsSession = session; _toolsView = new ToolsStatePayload { SessionId = session, Message = PompLanguage.Pick("도구 기본값 적용됨", "Tool defaults applied") };
            }
            ClearActiveRules(); RefreshActiveRules();
            PublishTools(); _commands.Enqueue(RefreshResourcesAsync);
        }
        /// <summary>
        /// The MCP servers bundled with POMP are registered once in the user's mcp.json (off, inputs empty), then they are the user's like any
        /// other. The ids registered so far live in POMP's data folder, so a deleted one stays deleted and a later POMP adds only its new ones.
        /// </summary>
        private void SeedMcp()
        {
            try
            {
                var file = Path.Combine(_environment.DataDirectory, "seeded-mcp.json");
                var done = new HashSet<string>(StringComparer.Ordinal);
                if (File.Exists(file) && JObject.Parse(File.ReadAllText(file))["ids"] is JArray ids)
                    foreach (var id in ids) if (id.Type == JTokenType.String) done.Add((string)id!);
                var before = done.Count;
                CatalogFiles().Seed(McpFiles(), done);
                if (done.Count != before) AtomicFile.WriteAllText(file, new JObject { ["ids"] = new JArray(done.OrderBy(i => i, StringComparer.Ordinal)) }.ToString(Formatting.Indented));
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException || ex is JsonException || ex is ArgumentException)
            {
                _logger.Warn("Registering the bundled MCP servers failed: " + ex.Message);
            }
        }
        private async Task RefreshResourcesAsync()
        {
            SeedMcp(); RefreshMcp(); RefreshRules(); RefreshWorkflows(); RefreshHooks(); RefreshCatalog(); await RefreshSkillsAsync().ConfigureAwait(false);
            bool available; lock (_stateLock) available = _availableCommands.OfType<JObject>().Any(c => c.GetString("name") == "pomp-tools");
            if (available && !ResourcesBusy()) try { await SendToolsAsync("refresh").ConfigureAwait(false); } catch (Exception ex) { _logger.Debug("Could not refresh tool selection: " + ResourceError(ex)); }
        }
        private void ResourceBridgeStateChanged(BridgeState state)
        {
            lock (_stateLock)
            {
                if (state == BridgeState.Running) _mcpRestartRequired = false;
                else
                {
                    _toolsTicket?.Completion.TrySetCanceled(); _toolsTicket = null; _toolsSession = null; _rulesCommandSeen = false;
                    _toolsView = new ToolsStatePayload { Message = PompLanguage.Pick("연결되어 있지 않아요.", "Not connected.") };
                    _engineSkills = new JArray(); _toolsCommandSeen = false;
                    _skillsView = new SkillsStatePayload { Skills = _skillsView.Skills.Where(s => s.Source is "project" or "user" or "installed" or "pomp").Select(s => new SkillInfoPayload { Name = s.Name, Description = s.Description, Source = s.Source, File = s.File, Hash = s.Hash, Invocable = false, Unreadable = s.Unreadable, Enabled = s.Enabled, CatalogId = s.CatalogId,
                        DefaultEnabled = s.DefaultEnabled, Category = s.Category,
                        Note = s.Source == "installed" || s.Unreadable ? s.Note : null }).ToList(), Diagnostics = _skillsView.Diagnostics };
                }
            }
            if (state != BridgeState.Running) ClearActiveRules();
            PublishTools(); PublishSkills();
            if (state == BridgeState.Running) _commands.Enqueue(RefreshResourcesAsync);
        }
        /// <summary>The POMP catalog bundled with this extension (skills POMP wrote, MCP servers at pinned versions).</summary>
        private void RefreshCatalog()
        {
            CatalogStatePayload view;
            try { view = CatalogFiles().View(); }
            catch (Exception ex) { view = new CatalogStatePayload { Message = ResourceError(ex) }; }
            lock (_stateLock) _catalogView = view;
            PublishCatalog();
        }
        private async Task HandleCatalogAsync(JObject payload)
        {
            var id = payload.GetString("requestId") ?? ""; string? error = null;
            try
            {
                var command = new UiMessage("", payload).PayloadAs<CatalogCommandPayload>() ?? throw new ArgumentException();
                if (command.Action != "refresh" && ResourcesBusy()) throw new InvalidOperationException(ResourceBusyMessage());
                if (command.Action == "install")
                {
                    var catalog = CatalogFiles();
                    var store = McpFiles();
                    var trusted = _workspaceTrust.Before(store.ProjectRoot, _settings.LoadDiscoveredOmpExtensions);
                    try { catalog.Install(command.Id ?? "", command.Scope ?? "", command.Inputs, store); }
                    finally
                    {
                        if (store.Changed)
                        {
                            lock (_stateLock) _mcpRestartRequired = true;
                            _workspaceTrust.KeepAfterOwnWrite(trusted, ".omp/mcp.json");
                        }
                    }
                    RefreshMcp(); await RefreshSkillsAsync().ConfigureAwait(false);
                }
                else if (command.Action != "refresh") throw new ArgumentException(PompLanguage.Pick("알 수 없는 목록 동작이에요.", "Unknown catalog action."));
                RefreshCatalog();
            }
            catch (Exception ex) { error = ResourceError(ex); RefreshCatalog(); }
            _ui.Post(UiMessageTypes.CatalogReply, new UiCommandReplyPayload { RequestId = id, Ok = error == null, Error = error }, UiDelivery.State);
        }
    }
}
