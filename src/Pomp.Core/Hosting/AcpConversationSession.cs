using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;
using Pomp.Adapters;
using Pomp.AgentClients;
using Pomp.Bridge;
using Pomp.Diagnostics;
using Pomp.Features;
using Pomp.Internal;
using Pomp.Ui;

namespace Pomp.Hosting
{
    /// <summary>A real ACP conversation. The official CLI owns authentication, inference and tool approval policy.</summary>
    internal sealed partial class AcpConversationSession : IConversationSession
    {
        internal const string Backend = "antigravity-acp";
        internal const string IdPrefix = Backend + ":";
        private readonly object _sync = new object();
        private readonly IPompSettings _settings;
        private readonly IPompHostEnvironment _environment;
        private readonly IVsAdapterFactory _adapters;
        private readonly IPompLogger _logger;
        private readonly PompSessionOptions _options;
        private readonly UiHub _ui;
        private readonly SerialQueue _commands;
        private readonly EditorContextBuilder _contextBuilder;
        private readonly CancellationTokenSource _lifetime = new CancellationTokenSource();
        private readonly SemaphoreSlim _startGate = new SemaphoreSlim(1, 1);
        private AcpConnection? _connection;
        private CancellationTokenSource _epoch = new CancellationTokenSource();
        private string _root = "", _title = "Antigravity ACP";
        private string? _agentSessionId, _error;
        private bool _ready, _initialized, _authRequired, _frozen, _disposed, _everLoaded, _replaying;
        private long _generation, _sequence;
        private JObject _agentCapabilities = new JObject();
        private JArray _configOptions = new JArray(), _authMethods = new JArray(), _availableCommands = new JArray();
        private JObject? _ompServer;
        private ContextUsagePayload? _usage;
        private BridgeStatePayload _bridgeState = new BridgeStatePayload { State = BridgeState.Stopped };
        private readonly List<JObject> _earlyUpdates = new List<JObject>();
        private int _earlyUpdateChars;
        private ComposerPrefillPayload? _prefill;
        private DateTime _prefillAt;
        // The JSON of the host/hello last built for the page; an identical one is not sent again.
        private string? _lastHelloJson;

        internal AcpConversationSession(IPompSettings settings, IPompHostEnvironment environment, IVsAdapterFactory adapters,
            IPompLogger logger, PompSessionOptions options)
        {
            _settings = settings;
            _environment = environment;
            _adapters = adapters;
            _logger = logger;
            _options = options;
            _ui = new UiHub(logger) { SnapshotProvider = Snapshot };
            _ui.CommandReceived += OnUiCommand;
            PompLanguage.Changed += OnHelloSettingsChanged;
            _settings.Changed += OnHelloSettingsChanged;
            _commands = new SerialQueue(logger);
            _contextBuilder = new EditorContextBuilder(adapters, logger,
                () => (settings as IEditorContextSettings)?.EditorContextLimits ?? EditorContextLimits.Default);
        }

        private HostHelloPayload Hello() => new HostHelloPayload
        {
            Version = _environment.HostVersion, VsVersion = _environment.VsVersion,
            Locale = PompLanguage.Locale, IncludeEditorContextByDefault = _settings.IncludeEditorContextByDefault,
            ComposerPredictions = false, WordCompletion = _settings.WordCompletion,
            Shortcuts = (_settings as IUiShortcutSettings)?.Shortcuts ?? new UiShortcutsPayload(),
        };

        /// <summary>The language or an option changed: the page gets a new host/hello when it differs from the last one sent.</summary>
        private void OnHelloSettingsChanged(object? sender, EventArgs e)
        {
            var hello = Hello();
            var json = Pomp.Json.PompJson.Serialize(hello);
            lock (_sync)
            {
                if (_disposed || string.Equals(json, _lastHelloJson, StringComparison.Ordinal)) return;
                _lastHelloJson = json;
            }
            _ui.Post(UiMessageTypes.HostHello, hello, UiDelivery.State);
        }

        public string BackendId => Backend;
        public string? ConversationId { get { lock (_sync) return PublicId(_agentSessionId); } }
        public string? ConversationFile => null;
        public string ConversationRoot { get { lock (_sync) return _root; } }
        public bool ConversationRunning { get { lock (_sync) return _turn != null; } }
        public bool ConversationWaiting { get { lock (_sync) return _permissions.Count > 0; } }
        public event EventHandler? ConversationStateChanged;
        public EditorSessionContext EditorSessionContext
        {
            get { lock (_sync) return new EditorSessionContext(_root, PublicId(_agentSessionId) ?? "", _turn != null, _turn?.Id ?? "", _title); }
        }

        internal static string? PublicId(string? id) => string.IsNullOrEmpty(id) ? null : IdPrefix + id;
        private static string AgentId(string id)
        {
            if (!id.StartsWith(IdPrefix, StringComparison.Ordinal) || id.Length <= IdPrefix.Length || id.Length > 2048 || id.Any(char.IsControl))
                throw new ArgumentException(PompLanguage.Pick("Antigravity 대화 식별자가 올바르지 않아요.", "Invalid Antigravity conversation identity."));
            return id.Substring(IdPrefix.Length);
        }

        public void InitializeConversation(string root, string? id, string? file, ConversationPartnerPayload? partner)
        {
            if (partner != null && partner.Kind != "main") throw new NotSupportedException(PompLanguage.Pick("ACP 대화는 OMP 에이전트·팀을 대상으로 할 수 없어요.", "ACP does not use OMP agent/team destinations."));
            if (!Path.IsPathRooted(root) || !Directory.Exists(root) || SameRoot(root, Path.Combine(_environment.DataDirectory, "workspace")))
                throw new InvalidOperationException(PompLanguage.Pick("ACP 연결에는 실제 프로젝트 폴더가 필요해요.", "ACP requires an actual project folder."));
            lock (_sync)
            {
                if (_connection != null || _everLoaded) throw new InvalidOperationException(PompLanguage.Pick("이 대화는 이미 시작되었어요.", "This conversation is already initialized."));
                _root = Path.GetFullPath(root);
                _agentSessionId = string.IsNullOrEmpty(id) ? null : AgentId(id!);
            }
        }

        public void AttachUi(IUiChannel channel) => _ui.Attach(channel);
        public void DetachUi(IUiChannel channel) => _ui.Detach(channel);

        public async Task EnsureStartedAsync(CancellationToken cancellationToken)
        {
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _lifetime.Token);
            await _startGate.WaitAsync(linked.Token).ConfigureAwait(false);
            try
            {
                lock (_sync)
                {
                    ThrowIfDisposed();
                    if (_ready) return;
                    if (_frozen) throw new InvalidOperationException(PompLanguage.Pick("작업 영역이 바뀌는 중이에요.", "The workspace is changing."));
                }
                if (_connection == null) await ConnectAsync(linked.Token).ConfigureAwait(false);
                await OpenSessionAsync(linked.Token).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                if (!(ex is AcpRpcException rpc && rpc.Code == -32000))
                    await CloseConnectionAsync().ConfigureAwait(false);
                if (!_lifetime.IsCancellationRequested) SetFailure(ex);
                throw;
            }
            finally { _startGate.Release(); }
        }

        private async Task ConnectAsync(CancellationToken token)
        {
            string root;
            lock (_sync) root = _root;
            if (root.Length == 0) throw new InvalidOperationException(PompLanguage.Pick("ACP 작업 영역이 선택되지 않았어요.", "No ACP workspace is selected."));
            var executable = _options.AntigravityCliLocator != null ? _options.AntigravityCliLocator() :
                AntigravityMcpSetup.FindCliInstallation(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), Environment.GetEnvironmentVariable("PATH"));
            if (string.IsNullOrEmpty(executable) || !Path.IsPathRooted(executable!) || !File.Exists(executable))
                throw new FileNotFoundException(PompLanguage.Pick("공식 agy CLI를 설치하고 다시 연결해 주세요.", "Install the official agy CLI and reconnect."));
            var cliName = Path.GetFileName(executable);
            if (!new[] { "agy", "agy.exe", "agy.cmd", "agy.bat" }.Contains(cliName, StringComparer.OrdinalIgnoreCase))
                throw new InvalidOperationException(PompLanguage.Pick("ACP에는 Antigravity 데스크톱 프로그램이 아니라 agy CLI가 필요해요.", "ACP requires the agy CLI, not the Antigravity desktop executable."));
            var descriptor = AntigravityMcpSetup.CreateAcpServer(root, _environment.ExtensionDirectory);
            var launch = new OmpLaunchOptions { ExecutablePath = executable!, WorkingDirectory = root };
            launch.Arguments.Add("acp");
            var process = (_options.AcpLauncher ?? new SystemOmpProcessLauncher(_logger)).Start(launch);
            var connection = new AcpConnection(process, _logger);
            long generation;
            lock (_sync)
            {
                if (_disposed) { connection.Dispose(); throw new ObjectDisposedException(nameof(AcpConversationSession)); }
                generation = ++_generation;
                _connection = connection;
                _initialized = false;
                _ompServer = descriptor;
                _error = null;
                _authRequired = false;
                _bridgeState = new BridgeStatePayload { State = BridgeState.Starting, Message = "Antigravity ACP" };
            }
            connection.Notification += message => OnNotification(connection, generation, message);
            connection.RequestHandler = (method, parameters, ct) => HandleServerRequestAsync(connection, generation, method, parameters, ct);
            ObserveConnectionAsync(connection, generation).Forget(_logger, "ACP process lifetime");
            PublishState();
            var result = await MetadataAsync(connection, "initialize", new JObject
            {
                ["protocolVersion"] = 1,
                ["clientInfo"] = new JObject { ["name"] = "pomp", ["title"] = "POMP", ["version"] = _environment.HostVersion },
                // No terminal/fs capability: the official agent and the OMP MCP server execute their own tools.
                ["clientCapabilities"] = new JObject { ["session"] = new JObject
                {
                    ["notices"] = new JObject(), ["configOptions"] = new JObject { ["boolean"] = new JObject() },
                } },
            }, token).ConfigureAwait(false);
            if ((int?)result["protocolVersion"] != 1) throw new NotSupportedException(PompLanguage.Pick("공식 CLI가 ACP 프로토콜 버전 1을 지원하지 않아요.", "The official CLI does not support ACP protocol version 1."));
            lock (_sync)
            {
                RequireConnection(connection, generation);
                _agentCapabilities = result["agentCapabilities"] as JObject ?? new JObject();
                _authMethods = result["authMethods"] as JArray ?? new JArray();
                _initialized = true;
            }
        }

        private async Task OpenSessionAsync(CancellationToken token)
        {
            AcpConnection connection;
            string? id;
            string root;
            bool replay;
            long generation;
            JObject descriptor;
            lock (_sync)
            {
                connection = _connection ?? throw new InvalidOperationException(PompLanguage.Pick("ACP가 연결되어 있지 않아요.", "ACP is not connected."));
                if (!_initialized) throw new InvalidOperationException(PompLanguage.Pick("ACP 연결 준비가 끝나지 않았어요. 먼저 다시 연결해 주세요.", "ACP initialization did not finish. Reconnect first."));
                id = _agentSessionId; root = _root; generation = _generation;
                descriptor = _ompServer ?? throw new InvalidOperationException(PompLanguage.Pick("OMP 도구 연결 정보가 없어요.", "The OMP tool descriptor is missing."));
                replay = id != null && !(_everLoaded && HasSessionCapability("resume"));
                if (replay && (bool?)_agentCapabilities["loadSession"] != true)
                    throw new NotSupportedException(PompLanguage.Pick("이 Antigravity CLI는 이전 대화를 복원할 수 없어요.", "This Antigravity CLI cannot restore conversation history."));
                _replaying = replay;
                _ready = false;
                _earlyUpdates.Clear(); _earlyUpdateChars = 0;
            }
            if (replay)
            {
                ResetProjection();
                _ui.ClearTranscript();
                _ui.Post(UiMessageTypes.ChatReplayBegin, new ChatReplayBeginPayload(), UiDelivery.State);
            }
            var parameters = new JObject { ["cwd"] = root, ["mcpServers"] = new JArray(descriptor.DeepClone()) };
            if (id != null) parameters["sessionId"] = id;
            var result = await MetadataAsync(connection, id == null ? "session/new" : replay ? "session/load" : "session/resume", parameters, token,
                TimeSpan.FromSeconds(100)).ConfigureAwait(false);
            List<JObject> early;
            lock (_sync)
            {
                RequireConnection(connection, generation);
                if (id == null)
                {
                    id = (string?)result["sessionId"];
                    if (string.IsNullOrWhiteSpace(id) || id!.Length > 2000 || id.Any(char.IsControl)) throw new InvalidDataException(PompLanguage.Pick("ACP가 올바르지 않은 대화 ID를 보냈어요.", "ACP returned an invalid session ID."));
                    _agentSessionId = id;
                }
                else if (result["sessionId"] != null && (string?)result["sessionId"] != id)
                    throw new InvalidDataException(PompLanguage.Pick("ACP가 다른 대화를 복원했어요.", "ACP restored a different conversation."));
                ApplyConfigurationLocked(result);
                _ready = true; _everLoaded = true; _error = null; _authRequired = false;
                _bridgeState = new BridgeStatePayload { State = BridgeState.Running, Message = "Antigravity ACP" };
                early = new List<JObject>(_earlyUpdates); _earlyUpdates.Clear(); _earlyUpdateChars = 0;
            }
            foreach (var update in early) OnNotification(connection, generation, update);
            if (replay)
            {
                FinishReplay();
                _ui.Post(UiMessageTypes.ChatReplayEnd, new JObject(), UiDelivery.State);
            }
            lock (_sync) _replaying = false;
            PublishState();
        }

        public Task WaitForConversationAsync(CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            lock (_sync)
            {
                if (!_ready || _agentSessionId == null) throw new InvalidOperationException(_error ?? PompLanguage.Pick("ACP 대화가 아직 준비되지 않았어요.", "The ACP conversation is not ready."));
            }
            return Task.CompletedTask;
        }

        public async Task RestartAsync(CancellationToken token)
        {
            await CloseConnectionAsync().ConfigureAwait(false);
            await EnsureStartedAsync(token).ConfigureAwait(false);
        }

        private async Task CloseConnectionAsync()
        {
            AcpConnection? connection;
            string? id;
            bool close;
            CancellationTokenSource epoch;
            lock (_sync)
            {
                connection = _connection; id = _agentSessionId; close = HasSessionCapability("close");
                _connection = null; _ready = false; _initialized = false; ++_generation;
                epoch = _epoch; _epoch = new CancellationTokenSource();
                _queuePaused = true; _queueReason = PompLanguage.Pick("연결이 바뀌어 대기열을 일시정지했어요.", "Queue paused because the connection changed.");
            }
            CancelPermissions();
            epoch.Cancel(); epoch.Dispose();
            FailCurrentTurn(PompLanguage.Pick("ACP 연결을 종료했어요.", "ACP connection closed."), aborted: true);
            if (connection != null)
            {
                try
                {
                    if (id != null && close)
                    {
                        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(3));
                        await connection.CallAsync("session/close", new JObject { ["sessionId"] = id }, deadline.Token).ConfigureAwait(false);
                    }
                }
                catch (Exception ex) { _logger.Debug("ACP close: " + ex.Message); }
                finally { connection.Dispose(); }
            }
        }

        private async Task ObserveConnectionAsync(AcpConnection connection, long generation)
        {
            Exception? failure = null;
            try { await connection.Completion.ConfigureAwait(false); }
            catch (Exception ex) { failure = ex; }
            lock (_sync)
            {
                if (_disposed || !ReferenceEquals(_connection, connection) || generation != _generation) return;
                _connection = null; _ready = false; _initialized = false;
                _error = failure?.Message ?? PompLanguage.Pick("Antigravity CLI가 종료됐어요. 다시 연결해 주세요.", "Antigravity CLI exited. Reconnect to continue.");
                _bridgeState = new BridgeStatePayload { State = BridgeState.Failed, Message = _error, Reason = "acpDisconnected" };
                _queuePaused = true; _queueReason = _error;
            }
            CancelPermissions();
            FailCurrentTurn(_error ?? PompLanguage.Pick("ACP 연결이 끊겼어요.", "ACP disconnected."), aborted: false);
            connection.Dispose();
            PublishState();
        }

        private async Task<JObject> MetadataAsync(AcpConnection connection, string method, JObject parameters, CancellationToken token, TimeSpan? timeout = null)
        {
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token, _lifetime.Token);
            deadline.CancelAfter(timeout ?? _options.AuthHelperStartTimeout);
            var result = await connection.CallAsync(method, parameters, deadline.Token).ConfigureAwait(false);
            return result as JObject ?? throw new InvalidDataException(PompLanguage.Pick("ACP가 " + method + " 요청에 올바르지 않은 응답을 보냈어요.", "ACP returned an invalid " + method + " response."));
        }

        private void SetFailure(Exception error)
        {
            lock (_sync)
            {
                _ready = false; _replaying = false;
                _authRequired = error is AcpRpcException rpc && rpc.Code == -32000;
                _error = error is OperationCanceledException ? PompLanguage.Pick("ACP 연결 요청이 취소되었거나 시간이 초과됐어요.", "ACP connection was cancelled or timed out.") : error.Message;
                _bridgeState = new BridgeStatePayload { State = BridgeState.Failed, Message = _error, Reason = _authRequired ? "acpAuthenticationRequired" : "acpError" };
            }
            PublishState();
        }

        public void FreezeWorkspace()
        {
            lock (_sync) _frozen = true;
            StopAsync().Forget(_logger, "stop ACP for workspace switch");
        }
        public void UnfreezeWorkspace() { lock (_sync) _frozen = false; PublishState(); }

        private IEnumerable<UiMessage> Snapshot()
        {
            lock (_sync)
            {
                var hello = Hello();
                _lastHelloJson = Pomp.Json.PompJson.Serialize(hello);
                var messages = new List<UiMessage>
                {
                    new UiMessage(UiMessageTypes.HostHello, hello),
                    new UiMessage(UiMessageTypes.BridgeState, _bridgeState),
                    new UiMessage(UiMessageTypes.SessionInfo, InfoLocked()),
                    new UiMessage(UiMessageTypes.AcpState, AcpStateLocked()),
                    new UiMessage(UiMessageTypes.ChatQueueState, QueueLocked()),
                    new UiMessage(UiMessageTypes.ConversationTabsState, ConversationTabs),
                    new UiMessage(UiMessageTypes.CommandsState, new JObject { ["commands"] = new JArray() }),
                };
                if (_prefill != null && DateTime.UtcNow - _prefillAt < TimeSpan.FromMinutes(1)) messages.Add(new UiMessage(UiMessageTypes.ComposerPrefill, _prefill));
                _prefill = null;
                return messages;
            }
        }

        private JObject InfoLocked()
        {
            var model = Config("model"); var thinking = Config("thought_level");
            return new JObject
            {
                ["backend"] = Backend, ["sessionId"] = PublicId(_agentSessionId), ["sessionName"] = _title,
                ["workingDirectory"] = _root, ["isStreaming"] = _turn != null, ["hasPendingAsyncWork"] = false,
                ["recoveryStatus"] = _ready && !_frozen ? "ready" : _error == null ? "restoring" : "failed", ["recoveryMessage"] = _error,
                ["capabilities"] = new JObject
                {
                    ["nativeOmp"] = false, ["queue"] = true, ["steer"] = true, ["stop"] = true,
                    ["images"] = (bool?)_agentCapabilities["promptCapabilities"]?["image"] == true, ["editorContext"] = true,
                    ["plan"] = new JObject { ["read"] = true, ["edit"] = false, ["actions"] = false },
                    ["history"] = new JObject { ["list"] = HasSessionCapability("list"), ["preview"] = false,
                        ["resume"] = (bool?)_agentCapabilities["loadSession"] == true, ["rename"] = false, ["delete"] = false, ["compact"] = false },
                },
                ["model"] = model == null ? null : new JObject { ["provider"] = Backend, ["id"] = model["currentValue"]?.DeepClone(), ["name"] = ConfigValueName(model) },
                ["thinkingLevel"] = thinking?["currentValue"]?.DeepClone(),
                ["todoPhases"] = PlanPhasesLocked(), ["planTaskCount"] = _plan.Count, ["planOpenCount"] = PlanOpenCountLocked(),
                ["planRevision"] = _planRevision, ["planTruncated"] = _plan.Count > 200,
                ["contextUsage"] = _usage == null ? null : Pomp.Json.PompJson.ToJObject(_usage),
            };
        }

        private JObject AcpStateLocked()
        {
            return new JObject
            {
                ["sessionId"] = PublicId(_agentSessionId), ["connected"] = _ready && !_frozen, ["authRequired"] = _authRequired,
                ["error"] = _error, ["authMethods"] = _authMethods.DeepClone(), ["configOptions"] = _configOptions.DeepClone(),
                ["permissions"] = new JArray(_permissions.Values.Select(p => p.Payload.DeepClone())), ["commands"] = _availableCommands.DeepClone(),
                ["ompTools"] = _ompServer == null ? null : new JObject { ["serverName"] = _ompServer["name"]?.DeepClone(),
                    ["command"] = _ompServer["command"]?.DeepClone(), ["args"] = _ompServer["args"]?.DeepClone(), ["state"] = "configured" },
            };
        }

        private void PublishState()
        {
            JObject info, acp; ChatQueueStatePayload queue; BridgeStatePayload bridge;
            lock (_sync) { if (_disposed) return; info = InfoLocked(); acp = AcpStateLocked(); queue = QueueLocked(); bridge = _bridgeState; }
            _ui.Post(UiMessageTypes.BridgeState, bridge, UiDelivery.State);
            _ui.Post(UiMessageTypes.SessionInfo, info, UiDelivery.State);
            _ui.Post(UiMessageTypes.AcpState, acp, UiDelivery.State);
            _ui.Post(UiMessageTypes.ChatQueueState, queue, UiDelivery.State);
            ConversationStateChanged?.Invoke(this, EventArgs.Empty);
        }

        private JObject? Config(string category) => _configOptions.OfType<JObject>().FirstOrDefault(c => (string?)c["category"] == category);
        private static IEnumerable<JObject> ConfigValues(JObject option) => (option["options"] as JArray ?? new JArray()).OfType<JObject>()
            .SelectMany(v => v["options"] is JArray group ? group.OfType<JObject>() : new[] { v });
        private static string ConfigValueName(JObject option) => (string?)ConfigValues(option).FirstOrDefault(v => JToken.DeepEquals(v["value"], option["currentValue"]))?["name"] ?? (string?)option["currentValue"] ?? "";
        private void ApplyConfigurationLocked(JObject result)
        {
            if (result["configOptions"] is JArray options) _configOptions = (JArray)options.DeepClone();
        }
        private bool HasSessionCapability(string name) => _agentCapabilities["sessionCapabilities"]?[name] is JObject;
        private void RequireConnection(AcpConnection connection, long generation)
        {
            ThrowIfDisposed();
            if (!ReferenceEquals(connection, _connection) || generation != _generation || _frozen) throw new OperationCanceledException(PompLanguage.Pick("ACP 연결이나 작업 영역이 바뀌었어요.", "The ACP connection or workspace changed."));
        }
        private void ThrowIfDisposed() { if (_disposed) throw new ObjectDisposedException(nameof(AcpConversationSession)); }
        private static bool SameRoot(string a, string b)
        {
            if (!Path.IsPathRooted(a) || !Path.IsPathRooted(b)) return false;
            try { return string.Equals(Path.GetFullPath(a).TrimEnd('\\', '/'), Path.GetFullPath(b).TrimEnd('\\', '/'), StringComparison.OrdinalIgnoreCase); }
            catch (Exception ex) when (ex is ArgumentException || ex is NotSupportedException || ex is PathTooLongException) { return false; }
        }
        private void Notice(string level, string message) => _ui.Post(UiMessageTypes.ChatNotice, new ChatNoticePayload { Level = level, Message = message });
        private void Reply(string type, string? id, string? error) => _ui.Post(type, new UiCommandReplyPayload { RequestId = id ?? "", Ok = error == null, Error = error }, UiDelivery.State);

        private void OnUiCommand(object? sender, UiMessage message)
        {
            var args = message.Payload ?? new JObject();
            switch (message.Type)
            {
                case UiMessageTypes.ChatSend:
                case UiMessageTypes.ChatSteer:
                    var send = message.PayloadAs<ChatSendPayload>();
                    var stopGeneration = Interlocked.Read(ref _stopGeneration);
                    if (send != null) _commands.Enqueue(() => AdmitAsync(send, message.Type == UiMessageTypes.ChatSteer, expectedStopGeneration: stopGeneration));
                    break;
                case UiMessageTypes.ChatStop: StopAsync().Forget(_logger, "stop ACP turn"); break;
                case UiMessageTypes.ChatQueue:
                    var queueStopGeneration = Interlocked.Read(ref _stopGeneration);
                    _commands.Enqueue(() => QueueCommandAsync(args, queueStopGeneration)); break;
                case UiMessageTypes.AcpCommand: HandleAcpCommandAsync(args).Forget(_logger, "ACP command"); break;
                case UiMessageTypes.SessionHistory: RunHistoryCommandAsync(args).Forget(_logger, "ACP history"); break;
                case UiMessageTypes.AgentPlanPage:
                case UiMessageTypes.AgentPlanAction:
                case UiMessageTypes.AgentPlan:
                case UiMessageTypes.AgentPlanEdit:
                    RunPlanCommandAsync(message, m => _ui.Post(m.Type, m.Payload, UiDelivery.State)).Forget(_logger, "ACP plan"); break;
                case UiMessageTypes.BridgeRestart: RestartAsync(_lifetime.Token).Forget(_logger, "restart ACP"); break;
                case UiMessageTypes.WorkspaceFindFiles: FindFilesAsync(args).Forget(_logger, "ACP workspace files"); break;
                case UiMessageTypes.WorkspacePickFiles: PickFilesAsync(args).Forget(_logger, "ACP file picker"); break;
                case UiMessageTypes.WorkspaceCountBasketTokens: CountBasketAsync(args).Forget(_logger, "ACP basket estimate"); break;
                case UiMessageTypes.LogOpen: _environment.ShowLog(); break;
                case UiMessageTypes.SettingsOpen: _environment.ShowSettings(); break;
                case UiMessageTypes.DictationSet:
                    // The tool window turns voice input off whenever it is hidden: that is nothing this backend can fail.
                    if ((bool?)args["on"] == true)
                        Reply(UiMessageTypes.DictationReply, (string?)args["requestId"],
                            PompLanguage.Pick("Antigravity ACP 연결에서는 음성 입력을 쓸 수 없어요.", "Voice input is unavailable in Antigravity ACP."));
                    break;
                default:
                    _ui.Post(UiMessageTypes.Error, new UiErrorInfo { Code = "acpUnsupported", Message = PompLanguage.Pick(
                        "이 기능은 Antigravity ACP 연결에서 지원하지 않아요: ", "This feature is unavailable in Antigravity ACP: ") + message.Type, Retryable = false }, UiDelivery.State);
                    break;
            }
        }

        public void Dispose()
        {
            AcpConnection? connection;
            lock (_sync)
            {
                if (_disposed) return;
                _disposed = true; _ready = false; connection = _connection; _connection = null; ++_generation;
            }
            PompLanguage.Changed -= OnHelloSettingsChanged;
            _settings.Changed -= OnHelloSettingsChanged;
            CancelPermissions();
            _lifetime.Cancel(); _epoch.Cancel();
            CancelAdmission(_turn);
            connection?.Dispose();
            _ui.Dispose();
        }
    }
}
