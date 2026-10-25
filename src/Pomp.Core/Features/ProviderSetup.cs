using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;
using Pomp.Adapters;
using Pomp.Bridge;
using Pomp.Diagnostics;
using Pomp.Hosting;
using Pomp.Internal;
using Pomp.Protocol;
using Pomp.Ui;

namespace Pomp.Features
{
    /// <summary>
    /// Host-only engine setup (docs/ui-protocol.md "Engine setup"): provider accounts and RPC sign-in through the auth helper
    /// (<see cref="OmpAuthHelper"/>), the console sign-in fallback, sign-out (<c>omp auth-broker logout</c>), encrypted API keys, the
    /// running engine's model and thinking-level pickers and the account usage report (<c>omp usage --json</c>). Nothing secret is ever
    /// published or logged.
    /// </summary>
    internal sealed partial class ProviderSetup : IDisposable
    {
        private const string GetLoginProvidersCommand = "get_login_providers";
        private const string LoginCommand = "login";
        private const string GetThinkingLevelsCommand = "get_available_thinking_levels";

        private static readonly Regex LoginProvider = new Regex("\\A[a-z0-9][a-z0-9.-]{0,63}\\z", RegexOptions.CultureInvariant);
        private static readonly string[] ThinkingAllowlist = { "off", "minimal", "low", "medium", "high", "xhigh", "max", "auto" };

        private readonly object _lock = new object();
        private readonly object _publishLock = new object();
        private readonly SemaphoreSlim _commands = new SemaphoreSlim(1, 1);
        private readonly ProviderCredentialStore _credentials;
        private readonly IPompSettings _settings;
        private readonly IPompHostEnvironment _environment;
        private readonly OmpExecutableResolver _resolver;
        private readonly UiHub _ui;
        private readonly Func<BridgeState> _bridgeState;
        private readonly Func<JObject, CancellationToken, Task<ResponseFrame>> _send;
        private readonly Func<JObject?> _currentModel;
        private readonly Func<string?> _currentThinking;
        private readonly Action _refreshInfo;
        private readonly Func<Task> _restart;
        private readonly Func<ProcessStartInfo, IProviderLoginProcess> _startLogin;
        private readonly Action<string> _openUrl;
        private readonly Func<OmpLaunchOptions, CancellationToken, Task<string>> _runUsage;
        private readonly OmpAuthHelper _helper;
        private readonly TimeSpan _providersTimeout;
        private readonly TimeSpan _loginTimeout;
        private readonly IPompLogger _logger;
        private readonly CancellationToken _token;
        private IProviderLoginProcess? _login;
        private LoginRun? _run;
        private JObject _loginState = new JObject { ["state"] = "idle" };
        private JArray? _providers;
        private string? _providersError;
        private JArray? _models;
        private string? _modelsError;
        private JArray? _thinkingLevels;
        private bool _thinkingRefreshQueued;
        private JObject? _usage;
        private bool _usageLoading;
        private bool _helperStale;
        private bool _restartRecommended;
        private bool _disposed;

        public ProviderSetup(ProviderCredentialStore credentials, IPompSettings settings, IPompHostEnvironment environment,
            OmpExecutableResolver resolver, IOmpProcessLauncher launcher, PompSessionOptions options, UiHub ui, Func<BridgeState> bridgeState,
            Func<JObject, CancellationToken, Task<ResponseFrame>> send, Func<JObject?> currentModel, Func<string?> currentThinking,
            Action refreshInfo, Func<Task> restart, IPompLogger logger, CancellationToken token)
        {
            _credentials = credentials; _settings = settings; _environment = environment; _resolver = resolver; _ui = ui;
            _bridgeState = bridgeState; _send = send; _currentModel = currentModel; _currentThinking = currentThinking; _refreshInfo = refreshInfo;
            _restart = restart; _logger = logger; _token = token;
            _startLogin = options.StartProviderLogin ?? SystemProviderLoginProcess.Start;
            _openUrl = options.OpenUrl ?? DefaultOpenUrl;
            _providersTimeout = options.AuthHelperProvidersTimeout;
            _loginTimeout = options.AuthHelperLoginTimeout;
            _runUsage = options.RunUsage ?? ((launch, cancellationToken) => OmpUsageRunner.RunAsync(launcher, launch, options.UsageTimeout, OmpUsageRunner.MaxOutputBytes, logger, cancellationToken));
            // A sign-out is one local database write: the page waits for it, so it ends well before a usage run may (a shorter usage limit applies to it too).
            _logoutTimeout = options.UsageTimeout < MaxLogoutTime ? options.UsageTimeout : MaxLogoutTime;
            _runLogout = (launch, cancellationToken) => OmpLogoutRunner.RunAsync(launcher, launch, _logoutTimeout, logger, cancellationToken);
            _helper = new OmpAuthHelper(launcher, BuildHelperLaunch, options.Bridge, logger, options.AuthHelperIdleTimeout, options.AuthHelperStartTimeout);
        }

        /// <summary>
        /// Each command gets one correlated snapshot; asynchronous completions (sign-in progress and result, usage) get unsolicited ones.
        /// Commands that only touch sign-in or usage state are not queued behind the others, so a prompt answer or a cancel never waits.
        /// </summary>
        public async Task HandleAsync(JObject request)
        {
            var requestId = Text(request, "requestId") ?? string.Empty;
            var action = Text(request, "action");
            string? error = null;
            var restart = false;
            IProviderLoginProcess? observe = null;
            var entered = false;
            try
            {
                if (!RunsUnqueued(action))
                {
                    await _commands.WaitAsync(_token).ConfigureAwait(false);
                    entered = true;
                }

                _token.ThrowIfCancellationRequested();
                switch (action)
                {
                    case "refresh": break;
                    case "listProviders":
                        await ListProvidersAsync().ConfigureAwait(false);
                        break;
                    case "login":
                        StartLogin(request);
                        break;
                    case "answerPrompt":
                        await AnswerPromptAsync(request).ConfigureAwait(false);
                        break;
                    case "cancelLogin":
                        CancelLogin();
                        break;
                    case "loginConsole":
                        observe = StartConsoleLogin(request);
                        break;
                    case "saveKey":
                        _credentials.Save(Text(request, "provider"), Text(request, "key"));
                        restart = CredentialsChanged();
                        CredentialSourcesChanged();
                        break;
                    case "removeKey":
                        _credentials.Remove(Text(request, "provider"));
                        restart = CredentialsChanged();
                        CredentialSourcesChanged();
                        break;
                    case "saveLocalServer":
                        _credentials.SaveLocalServer(Text(request, "id"), Text(request, "name"), Text(request, "address"), Text(request, "key"));
                        restart = CredentialsChanged();
                        break;
                    case "removeLocalServer":
                        _credentials.RemoveLocalServer(Text(request, "id"));
                        restart = CredentialsChanged();
                        break;
                    case "logout":
                        restart = await LogoutAsync(request).ConfigureAwait(false);
                        await ReloadProvidersAfterLogoutAsync().ConfigureAwait(false);
                        break;
                    case "listModels":
                        await ListModelsAsync().ConfigureAwait(false);
                        break;
                    case "setModel":
                        await SetModelAsync(request).ConfigureAwait(false);
                        break;
                    case "listThinking":
                        await ListThinkingAsync().ConfigureAwait(false);
                        break;
                    case "setThinking":
                        await SetThinkingAsync(request).ConfigureAwait(false);
                        break;
                    case "listUsage":
                        StartUsage();
                        break;
                    case "setEditorModel":
                        await SetEditorModelAsync(request).ConfigureAwait(false);
                        break;
                    case "resolveEditorModel":
                        await ResolveEditorModelAsync().ConfigureAwait(false);
                        break;
                    default: throw new ProviderSetupException(SetupText.Get("알 수 없는 설정 명령입니다.", "Unknown setup command."));
                }
            }
            catch (ProviderSetupException ex) { error = ex.Message; }
            catch (OperationCanceledException) { error = SetupText.Get("설정 작업이 취소되었습니다.", "Setup was cancelled."); }
            catch (Exception)
            {
                // Do not publish arbitrary exception/omp response text: it might contain a submitted credential.
                error = SetupText.Get("설정 명령을 완료하지 못했습니다.", "Setup could not complete this command.");
                _logger.Warn("An engine setup command failed.");
            }
            finally
            {
                Publish(requestId, error);
                if (entered) _commands.Release();
            }
            if (observe != null) ObserveConsoleLoginAsync(observe).Forget(_logger, "provider login completion");
            if (restart) await RestartSafelyAsync().ConfigureAwait(false);
        }

        private static bool RunsUnqueued(string? action) => action == "refresh" || action == "answerPrompt" || action == "cancelLogin" || action == "listUsage";

        public void Publish(string? requestId = null, string? error = null)
        {
            // One snapshot at a time, in the order they were taken: the page keeps the last one it receives.
            lock (_publishLock)
            {
                JObject state;
                lock (_lock)
                {
                    if (_disposed) return;
                    state = new JObject
                    {
                        ["login"] = _loginState.DeepClone(),
                        ["apiKeyProviders"] = ProviderCredentialStore.ApiKeyProviders(),
                        ["restartRecommended"] = _restartRecommended,
                        ["usageLoading"] = _usageLoading,
                    };
                    if (_providers != null) state["providers"] = _providers.DeepClone();
                    if (_providersError != null) state["providersError"] = _providersError;
                    if (_models != null) state["models"] = _models.DeepClone();
                    if (_modelsError != null) state["modelsError"] = _modelsError;
                    if (_thinkingLevels != null) state["thinkingLevels"] = _thinkingLevels.DeepClone();
                    if (_usage != null) state["usage"] = _usage.DeepClone();
                    state["editorModel"] = EditorModelState();
                }
                state["keys"] = _credentials.SavedKeys();
                state["localServers"] = _credentials.LocalServers();
                var model = _currentModel();
                if (model != null) state["currentModel"] = model;
                var thinking = SetupSanitize.Token(_currentThinking());
                if (thinking != null) state["currentThinking"] = thinking;
                if (requestId != null) state["requestId"] = requestId;
                if (error != null) state["error"] = error;
                _ui.Post(UiMessageTypes.SetupState, state, UiDelivery.State);
            }
        }

        public void BridgeStateChanged(BridgeState state)
        {
            lock (_lock)
            {
                if (state == BridgeState.Running) _restartRecommended = false;
                else
                {
                    // A list belongs to the credentials and the model of one running process.
                    _models = null;
                    _thinkingLevels = null;
                }

                // Which model the editor choice means belongs to one running process too (the page asks again).
                ForgetEditorModel();
            }

            Publish();
            if (state == BridgeState.Running) ScheduleThinkingRefresh();
        }

        /// <summary>The session info changed its model or thinking level (<c>session/info</c>).</summary>
        public void SessionInfoChanged(bool modelChanged)
        {
            if (modelChanged)
            {
                // The levels a model offers depend on the model.
                bool editorKnown;
                lock (_lock)
                {
                    _thinkingLevels = null;
                    editorKnown = _editorEffective != null;
                    ForgetEditorModel();
                }

                ScheduleThinkingRefresh();
                // "Same as chat" (and the automatic fallback) follow the chat model: show what it means now.
                if (editorKnown) Task.Run(async () => { await ResolveEditorModelAsync().ConfigureAwait(false); Publish(); }).Forget(_logger, "editor model refresh");
            }

            Publish();
        }

        private bool CredentialsChanged()
        {
            lock (_lock)
            {
                _models = null;
                _modelsError = null;
                _restartRecommended = _bridgeState() == BridgeState.Running;
                return !_restartRecommended;
            }
        }

        /// <summary>
        /// The saved keys (or another process's sign-in) changed what omp sees: the helper's environment is stale, so it is ended (after a
        /// running sign-in, which must not be cut short) and a loaded provider list is read again from a fresh one.
        /// </summary>
        private void CredentialSourcesChanged()
        {
            bool defer;
            lock (_lock)
            {
                defer = _run != null;
                if (defer) _helperStale = true;
            }

            if (!defer) _helper.Kill("the saved credentials changed");
            ScheduleProvidersRefresh();
        }

        private async Task RestartSafelyAsync()
        {
            if (_token.IsCancellationRequested) return;
            try { await _restart().ConfigureAwait(false); }
            catch (Exception) { _logger.Warn("The engine could not restart after provider credentials changed."); }
        }

        // ---- provider list (through the auth helper) --------------------------------------------------------------------

        private async Task ListProvidersAsync()
        {
            lock (_lock)
            {
                // The helper is busy with a sign-in; the list is read again when that ends.
                if (_run != null) return;
            }

            await RefreshProvidersAsync().ConfigureAwait(false);
        }

        /// <summary>Reads the provider list from the helper; the caller holds the command lock. A failure is recorded in <c>providersError</c> and thrown.</summary>
        private async Task RefreshProvidersAsync()
        {
            string message;
            try
            {
                var response = await _helper.SendAsync(new JObject { ["type"] = GetLoginProvidersCommand }, _providersTimeout, _token).ConfigureAwait(false);
                if (response.Success)
                {
                    var providers = ParseProviders(response.Data);
                    if (providers != null)
                    {
                        lock (_lock) { _providers = providers; _providersError = null; }
                        return;
                    }

                    message = SetupText.Get("공급자 목록이 올바르지 않습니다.", "The provider list is invalid.");
                }
                else message = SetupText.Get("공급자 목록을 가져오지 못했습니다.", "The provider list could not be loaded.");
            }
            catch (OperationCanceledException) { throw; }
            catch (ProviderSetupException ex) { message = ex.Message; }
            catch (AuthHelperStartException) { message = HelperStartText(); }
            catch (TimeoutException) { message = SetupText.Get("로그인이 제때 응답하지 않았습니다.", "Sign-in did not respond in time."); }
            catch (Exception ex)
            {
                _logger.Warn("The provider list could not be loaded: " + ex.GetType().Name);
                message = SetupText.Get("공급자 목록을 불러오지 못했습니다.", "The provider list could not be loaded.");
            }

            lock (_lock) _providersError = message;
            throw new ProviderSetupException(message);
        }

        /// <summary>Re-reads an already loaded provider list in the background (credentials changed under it).</summary>
        private void ScheduleProvidersRefresh()
        {
            lock (_lock)
            {
                if (_disposed || _providers == null) return;
            }

            Task.Run(async () =>
            {
                try { await _commands.WaitAsync(_token).ConfigureAwait(false); }
                catch (OperationCanceledException) { return; }
                try
                {
                    lock (_lock)
                    {
                        if (_run != null) return; // The sign-in refreshes the list itself when it ends.
                    }

                    await RefreshProvidersAsync().ConfigureAwait(false);
                }
                catch (ProviderSetupException) { /* providersError is published below. */ }
                catch (OperationCanceledException) { return; }
                finally { _commands.Release(); }
                Publish();
            }).Forget(_logger, "provider list refresh");
        }

        /// <summary>
        /// Sign-in methods whose credentials are stored under another provider (omp's <c>storeCredentialsAs</c>, 18.8): the device-code login
        /// <c>openai-codex-device</c> stores as <c>openai-codex</c>, Z.AI's browser sign-in <c>zai-coding-plan</c> as <c>zai</c>. omp's
        /// <c>get_login_providers</c> answers <c>authenticated</c> from the stored credential of the provider an entry stores as, and does not
        /// name that provider, so such an entry reads as signed in whenever the other one is and the page would list a second account for the
        /// same sign-in. The table is explicit because the RPC has no field to derive it from: check it whenever the bundled omp changes (every
        /// auth rule of the engine with a <c>storeAs</c> belongs here; 18.8.6, 18.10.3, 18.10.4 and 18.12.1 have exactly these two).
        /// </summary>
        private static readonly Dictionary<string, string> StoredUnder = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["openai-codex-device"] = "openai-codex",
            ["zai-coding-plan"] = "zai",
        };

        private static JArray? ParseProviders(JToken? data)
        {
            var raw = (data as JObject)?["providers"] as JArray ?? data as JArray;
            if (raw == null) return null;
            var seen = new HashSet<string>(StringComparer.Ordinal);
            var providers = new JArray();
            foreach (var item in raw.OfType<JObject>())
            {
                var id = Text(item, "id");
                if (id == null || !LoginProvider.IsMatch(id) || !seen.Add(id)) continue;
                providers.Add(new JObject
                {
                    ["id"] = id,
                    ["name"] = SetupSanitize.Line(Text(item, "name"), 200) ?? id,
                    // A sign-in method stays in the list (it is another way to sign in) but is never an account itself: the provider it stores as is.
                    ["authenticated"] = !StoredUnder.ContainsKey(id) && (item.GetBool("authenticated") ?? false),
                    ["available"] = item.GetBool("available") ?? true,
                });
                if (providers.Count == 256) break;
            }

            return providers;
        }

        private string HelperStartText() => SetupText.Get(
            "로그인을 시작하지 못했습니다.",
            "Sign-in could not be started.");

        // ---- launches of omp that are not the chat engine ---------------------------------------------------------------

        /// <summary>
        /// The auth helper's launch: <c>omp</c> with <see cref="OmpAuthHelper.Arguments"/> in an empty folder of POMP's own; the
        /// environment is the host's plus the saved API keys and a closed <c>OPENAI_BASE_URL</c> (never a <c>POMP_*</c> variable).
        /// </summary>
        private OmpLaunchOptions BuildHelperLaunch()
        {
            var launch = NewAuxiliaryLaunch();
            foreach (var argument in OmpAuthHelper.Arguments) launch.Arguments.Add(argument);
            launch.Environment["OPENAI_BASE_URL"] = OmpAuthHelper.ClosedBaseUrl;
            return launch;
        }

        /// <summary>omp resolved like for the engine, started in <c>&lt;data&gt;\workspace</c> with the saved keys in its environment.</summary>
        private OmpLaunchOptions NewAuxiliaryLaunch()
        {
            var resolution = _resolver.Resolve(_settings.OmpExecutablePath, _environment.ExtensionDirectory);
            // The same engine policy as the chat engine (docs/architecture.md "엔진 버전"): a damaged bundled engine starts nothing.
            var refusal = BundledEngine.Refusal(resolution, _environment.ExtensionDirectory, out _);
            if (refusal != null) throw new ProviderSetupException(refusal);
            if (!resolution.Found) throw new ProviderSetupException(OmpNotFoundText());
            if (resolution.ConfiguredPathProblem != null)
                _logger.Warn(resolution.ConfiguredPathProblem + " Using " + resolution.Path + " (" + resolution.Source + ") instead.");
            string workspace;
            try
            {
                workspace = Path.Combine(Path.GetFullPath(_environment.DataDirectory), "workspace");
                Directory.CreateDirectory(workspace);
            }
            catch (Exception)
            {
                throw new ProviderSetupException(SetupText.Get("POMP 데이터 폴더를 준비하지 못했습니다.", "The POMP data folder could not be prepared."));
            }

            var launch = new OmpLaunchOptions { ExecutablePath = resolution.Path!, WorkingDirectory = workspace };
            BundledEngine.UseOwnNatives(launch, _environment.DataDirectory);
            foreach (var pair in _credentials.GetLaunchEnvironment())
            {
                if (!pair.Key.StartsWith("POMP_", StringComparison.OrdinalIgnoreCase)) launch.Environment[pair.Key] = pair.Value;
            }

            return launch;
        }

        private static string OmpNotFoundText() => SetupText.Get(
            "POMP 에이전트 실행 파일을 찾을 수 없습니다.",
            "The POMP agent executable was not found.");

        // ---- models -----------------------------------------------------------------------------------------------------

        private async Task<JArray> FetchModelsAsync()
        {
            if (_bridgeState() != BridgeState.Running) throw new ProviderSetupException(SetupText.Get("연결되어 있지 않습니다.", "Not connected."));
            var response = await _send(OmpCommands.GetAvailableModels(), _token).ConfigureAwait(false);
            if (!response.Success) throw new ProviderSetupException(SetupText.Get("모델 목록을 가져오지 못했습니다.", "The model list could not be loaded."));
            var raw = response.Data is JArray array ? array : (response.Data as JObject)?["models"] as JArray;
            if (raw == null) throw new ProviderSetupException(SetupText.Get("모델 목록이 올바르지 않습니다.", "The model list is invalid."));
            var models = new JArray();
            foreach (var item in raw.OfType<JObject>())
            {
                var provider = Text(item, "provider");
                var id = Text(item, "id");
                if (!ValidModelPart(provider) || !ValidModelPart(id)) continue;
                var model = new JObject { ["provider"] = provider, ["id"] = id };
                var name = Text(item, "name");
                if (!string.IsNullOrEmpty(name) && name!.Length <= 200 && !name.Any(char.IsControl)) model["name"] = name;
                models.Add(model);
                if (models.Count == 500) break;
            }

            if (_bridgeState() != BridgeState.Running) throw new ProviderSetupException(SetupText.Get("모델 목록을 불러오는 중 연결이 끊겼습니다.", "The connection was lost while loading models."));
            return models;
        }

        private async Task ListModelsAsync()
        {
            try
            {
                var models = await FetchModelsAsync().ConfigureAwait(false);
                lock (_lock) { _models = models; _modelsError = null; }
            }
            catch (Exception ex)
            {
                var message = ex is ProviderSetupException ? ex.Message : SetupText.Get("모델 목록을 불러오지 못했습니다.", "Available models could not be loaded.");
                lock (_lock) { _models = null; _modelsError = message; }
                throw new ProviderSetupException(message);
            }
        }

        private async Task SetModelAsync(JObject request)
        {
            var provider = Text(request, "provider");
            var id = Text(request, "modelId");
            if (!ValidModelPart(provider) || !ValidModelPart(id)) throw new ProviderSetupException(SetupText.Get("모델 공급자나 ID 형식이 올바르지 않습니다.", "Model provider or ID is invalid."));
            if (_bridgeState() != BridgeState.Running) throw new ProviderSetupException(SetupText.Get("연결되어 있지 않습니다.", "Not connected."));
            var response = await _send(OmpCommands.SetModel(provider!, id!), _token).ConfigureAwait(false);
            if (!response.Success) throw new ProviderSetupException(SetupText.Get("그 모델을 선택하지 못했습니다.", "That model could not be selected."));
            // The levels a model offers depend on the model: read them again (best effort; session/info follows on its own).
            lock (_lock) _thinkingLevels = null;
            try
            {
                var levels = await FetchThinkingLevelsAsync().ConfigureAwait(false);
                lock (_lock) _thinkingLevels = levels;
            }
            catch (Exception) { /* The page can ask with listThinking. */ }

            _refreshInfo();
        }

        // ---- thinking level ---------------------------------------------------------------------------------------------

        private async Task<JArray> FetchThinkingLevelsAsync()
        {
            if (_bridgeState() != BridgeState.Running) throw new ProviderSetupException(SetupText.Get("연결되어 있지 않습니다.", "Not connected."));
            var response = await _send(new JObject { ["type"] = GetThinkingLevelsCommand }, _token).ConfigureAwait(false);
            if (!response.Success) throw new ProviderSetupException(SetupText.Get("추론 수준 목록을 가져오지 못했습니다.", "The thinking levels could not be loaded."));
            var raw = response.Data is JArray array ? array : (response.Data as JObject)?["levels"] as JArray;
            if (raw == null) throw new ProviderSetupException(SetupText.Get("추론 수준 목록이 올바르지 않습니다.", "The thinking level list is invalid."));
            var levels = new JArray();
            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (var item in raw)
            {
                var level = item.Type == JTokenType.String ? SetupSanitize.Token((string?)item) : null;
                if (level == null || !seen.Add(level)) continue;
                levels.Add(level);
                if (levels.Count == 16) break;
            }

            if (_bridgeState() != BridgeState.Running) throw new ProviderSetupException(SetupText.Get("추론 수준 목록을 불러오는 중 연결이 끊겼습니다.", "The connection was lost while loading thinking levels."));
            return levels;
        }

        private async Task ListThinkingAsync()
        {
            var levels = await FetchThinkingLevelsAsync().ConfigureAwait(false);
            lock (_lock) _thinkingLevels = levels;
        }

        private async Task SetThinkingAsync(JObject request)
        {
            var level = Text(request, "level");
            bool allowed;
            lock (_lock)
            {
                // The last list says what the current model offers; without one only the known names pass.
                allowed = level != null && SetupSanitize.Token(level) == level
                    && (_thinkingLevels != null ? _thinkingLevels.Any(item => (string?)item == level) : ThinkingAllowlist.Contains(level));
            }

            if (!allowed) throw new ProviderSetupException(SetupText.Get("지원하지 않는 추론 수준입니다.", "Unsupported thinking level."));
            if (_bridgeState() != BridgeState.Running) throw new ProviderSetupException(SetupText.Get("연결되어 있지 않습니다.", "Not connected."));
            var response = await _send(OmpCommands.SetThinkingLevel(level!), _token).ConfigureAwait(false);
            if (!response.Success) throw new ProviderSetupException(SetupText.Get("그 추론 수준을 적용하지 못했습니다.", "That thinking level could not be applied."));
            _refreshInfo();
        }

        /// <summary>Reads the thinking levels of the running engine in the background (the engine started or its model changed).</summary>
        private void ScheduleThinkingRefresh()
        {
            lock (_lock)
            {
                if (_disposed || _thinkingRefreshQueued) return;
                _thinkingRefreshQueued = true;
            }

            Task.Run(async () =>
            {
                try
                {
                    await _commands.WaitAsync(_token).ConfigureAwait(false);
                    try
                    {
                        lock (_lock) _thinkingRefreshQueued = false;
                        var levels = await FetchThinkingLevelsAsync().ConfigureAwait(false);
                        lock (_lock) _thinkingLevels = levels;
                    }
                    finally { _commands.Release(); }
                }
                catch (Exception) { /* The page can ask with listThinking. */ }
                finally { lock (_lock) _thinkingRefreshQueued = false; }
                Publish();
            }).Forget(_logger, "thinking level refresh");
        }

        // ---- usage ------------------------------------------------------------------------------------------------------

        /// <summary>Starts <c>omp usage --json</c> in the background (one at a time); the answer carries <c>usageLoading</c>, the result follows.</summary>
        private void StartUsage()
        {
            lock (_lock)
            {
                if (_disposed) throw new OperationCanceledException();
                if (_usageLoading) return;
                _usageLoading = true;
            }

            OmpLaunchOptions launch;
            try
            {
                launch = NewAuxiliaryLaunch();
                launch.Arguments.Add("usage");
                launch.Arguments.Add("--json");
            }
            catch
            {
                lock (_lock) _usageLoading = false;
                throw;
            }

            Task.Run(() => RunUsageAsync(launch)).Forget(_logger, "omp usage");
        }

        private async Task RunUsageAsync(OmpLaunchOptions launch)
        {
            JObject? usage = null;
            string? error = null;
            try
            {
                var text = await _runUsage(launch, _token).ConfigureAwait(false);
                usage = OmpUsageReader.Parse(text, DateTimeOffset.UtcNow);
            }
            catch (OperationCanceledException)
            {
                lock (_lock) _usageLoading = false;
                return;
            }
            catch (ProviderSetupException ex) { error = ex.Message; }
            catch (Exception ex)
            {
                _logger.Warn("omp usage failed: " + ex.GetType().Name);
                error = SetupText.Get("사용량을 불러오지 못했습니다.", "Usage could not be loaded.");
            }

            lock (_lock)
            {
                if (_disposed) return;
                _usageLoading = false;
                _usage = usage ?? OmpUsageReader.WithError(_usage, error ?? string.Empty, DateTimeOffset.UtcNow);
            }

            Publish();
        }

        // ---- shared helpers ---------------------------------------------------------------------------------------------

        private static bool ValidModelPart(string? text) => !string.IsNullOrWhiteSpace(text) && text!.Length <= 200 && !text.Any(char.IsControl);
        private static string? Text(JObject value, string field) => value[field]?.Type == JTokenType.String ? (string?)value[field] : null;

        private static void DefaultOpenUrl(string url)
        {
            // The URL was validated as an absolute http(s) address: the shell opens it in the default browser.
            using (Process.Start(new ProcessStartInfo(url) { UseShellExecute = true })) { }
        }

        public void Dispose()
        {
            LoginRun? run;
            IProviderLoginProcess? console;
            lock (_lock)
            {
                if (_disposed) return;
                _disposed = true;
                run = _run;
                console = _login;
                _run = null;
                _login = null;
            }

            try { run?.Cts.Cancel(); }
            catch (Exception) { /* Shutdown is best effort. */ }
            try { console?.Kill(); }
            catch (Exception) { _logger.Warn("The provider login console could not be stopped during shutdown."); }
            try { console?.Dispose(); }
            catch (Exception) { /* Shutdown is best effort. */ }
            _helper.Dispose();
        }
    }

    /// <summary>Setup texts shown in the chat in the language chosen in POMP's options; Visual Studio's language by default (never credential material).</summary>
    internal static class SetupText
    {
        public static string Get(string korean, string english) =>
            PompLanguage.Pick(korean, english);
    }

    /// <summary>Text that omp or a provider supplied and the setup page shows: bounded, without control or bidi/zero-width characters.</summary>
    internal static class SetupSanitize
    {
        /// <summary>One line of at most <paramref name="max"/> characters (whitespace runs become one space); null when nothing is left.</summary>
        public static string? Line(string? text, int max) => Clean(text, max, false);

        /// <summary>Like <see cref="Line"/> but keeps line breaks (at most one blank line in a row).</summary>
        public static string? Block(string? text, int max) => Clean(text, max, true);

        /// <summary>A short name such as a thinking level: a letter, then letters, digits, <c>-</c> or <c>_</c> (at most 32 characters).</summary>
        public static string? Token(string? text)
        {
            if (string.IsNullOrEmpty(text) || text!.Length > 32 || !char.IsLetter(text[0])) return null;
            foreach (var c in text)
            {
                if (!(c < 128 && (char.IsLetterOrDigit(c) || c == '-' || c == '_'))) return null;
            }

            return text;
        }

        private static string? Clean(string? text, int max, bool keepLineBreaks)
        {
            if (string.IsNullOrEmpty(text)) return null;
            var builder = new StringBuilder(Math.Min(text!.Length, max + 2));
            var pendingBreaks = 0;
            var pendingSpace = false;
            for (var i = 0; i < text.Length && builder.Length < max; i++)
            {
                var c = text[i];
                if (c == '\r' || c == '\n')
                {
                    if (c == '\r' && i + 1 < text.Length && text[i + 1] == '\n') i++;
                    if (keepLineBreaks) pendingBreaks++;
                    else pendingSpace = true;
                    continue;
                }

                if (char.IsWhiteSpace(c)) { pendingSpace = true; continue; }
                if (char.IsControl(c)) continue;
                var category = char.GetUnicodeCategory(c);
                if (category == UnicodeCategory.Format || category == UnicodeCategory.OtherNotAssigned || category == UnicodeCategory.PrivateUse) continue;
                var width = 1;
                if (char.IsSurrogate(c))
                {
                    if (!(char.IsHighSurrogate(c) && i + 1 < text.Length && char.IsLowSurrogate(text[i + 1]))) continue;
                    width = 2;
                }

                // The separator in front of a character counts too: the result never exceeds max.
                var separator = builder.Length == 0 ? 0 : pendingBreaks > 0 ? Math.Min(pendingBreaks, 2) : pendingSpace ? 1 : 0;
                if (builder.Length + separator + width > max) break;
                if (separator > 0) builder.Append(pendingBreaks > 0 ? '\n' : ' ', separator);
                pendingBreaks = 0;
                pendingSpace = false;
                builder.Append(c);
                if (width == 2) builder.Append(text[++i]);
            }

            return builder.Length == 0 ? null : builder.ToString();
        }
    }
}
