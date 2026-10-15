using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;
using Pomp.Bridge;
using Pomp.Diagnostics;
using Pomp.Internal;
using Pomp.Protocol;

namespace Pomp.Features
{
    // Sign-in: RPC through the auth helper (login / answerPrompt / cancelLogin) and the console fallback (loginConsole).
    internal sealed partial class ProviderSetup
    {
        /// <summary>The longest link the host passes to the browser, and how often one sign-in may open it (omp normally asks once).</summary>
        private const int MaxUrlChars = 4096;
        private const int MaxBrowserOpensPerLogin = 3;
        private const int MinScrubbedAnswerChars = 3;

        /// <summary>One RPC sign-in through the auth helper. Fields other than the constants are guarded by <c>_lock</c>.</summary>
        private sealed class LoginRun
        {
            public LoginRun(string provider)
            {
                Provider = provider;
                Cts = new CancellationTokenSource();
            }

            public string Provider { get; }
            public CancellationTokenSource Cts { get; }
            public bool Cancelled;
            public string? PromptId;
            public int BrowserOpens;

            /// <summary>What the user typed into this sign-in's prompts: removed from every text that leaves the host.</summary>
            public readonly List<string> Answers = new List<string>();
        }

        // ---- RPC sign-in ------------------------------------------------------------------------------------------------

        /// <summary><c>login</c>: starts the sign-in in the background; progress, the prompt and the result arrive as unsolicited snapshots.</summary>
        private void StartLogin(JObject request)
        {
            var provider = ReadLoginProvider(request, required: true);
            LoginRun run;
            lock (_lock)
            {
                if (_disposed) throw new OperationCanceledException();
                if (_login != null || _run != null) throw new ProviderSetupException(SetupText.Get("이미 로그인이 진행 중입니다.", "A provider login is already running."));
                // Reported by the command itself, not only by the sign-in that follows.
                if (!_resolver.Resolve(_settings.OmpExecutablePath, _environment.ExtensionDirectory).Found) throw new ProviderSetupException(OmpNotFoundText());
                _run = run = new LoginRun(provider);
                _loginState = new JObject { ["state"] = "running", ["provider"] = provider };
            }

            Task.Run(() => RunLoginAsync(run)).Forget(_logger, "provider sign-in");
        }

        private async Task RunLoginAsync(LoginRun run)
        {
            var succeeded = false;
            string? failure = null;
            Action<JObject> handler = frame => OnHelperUiRequest(run, frame);
            try
            {
                _helper.SetUiRequestHandler(handler);
                var response = await _helper.SendAsync(new JObject { ["type"] = LoginCommand, ["providerId"] = run.Provider }, _loginTimeout, run.Cts.Token).ConfigureAwait(false);
                if (response.Success) succeeded = true;
                else failure = LoginFailureText(run, response.Error);
            }
            catch (OperationCanceledException) { /* Cancelled by the user or by shutdown: whoever cancelled set the state. */ }
            catch (ProviderSetupException ex) { failure = ex.Message; }
            catch (AuthHelperStartException) { failure = HelperStartText(); }
            catch (TimeoutException)
            {
                _helper.Kill("the sign-in timed out");
                failure = SetupText.Get("로그인 시간이 초과되어 취소했습니다.", "Sign-in timed out and was cancelled.");
            }
            catch (Exception ex)
            {
                _logger.Warn("A provider sign-in failed: " + ex.GetType().Name);
                if (!run.Cancelled) failure = SetupText.Get("로그인이 예기치 않게 종료되었습니다.", "Sign-in ended unexpectedly.");
            }
            finally { _helper.ClearUiRequestHandler(handler); }

            await FinishLoginAsync(run, succeeded, failure).ConfigureAwait(false);
        }

        private string LoginFailureText(LoginRun run, string? omp)
        {
            // omp's own words stay (the page recognizes the "requires secret input / terminal" kind and offers the console sign-in), minus anything typed in.
            var text = SetupSanitize.Block(omp, 400);
            return SetupText.Get("로그인하지 못했습니다: ", "Sign-in failed: ")
                + (text == null ? SetupText.Get("이유를 알 수 없습니다.", "No reason was given.") : Scrub(run, text));
        }

        private async Task FinishLoginAsync(LoginRun run, bool succeeded, string? failure)
        {
            bool stopHelper;
            lock (_lock)
            {
                // Cancelled or shut down meanwhile: the canceller already set the state.
                if (_disposed || !ReferenceEquals(_run, run)) return;
                _run = null;
                stopHelper = _helperStale;
                _helperStale = false;
                _loginState = succeeded
                    ? new JObject { ["state"] = "succeeded", ["provider"] = run.Provider, ["message"] = SetupText.Get("로그인했습니다.", "Login completed.") }
                    : new JObject
                    {
                        ["state"] = "failed", ["provider"] = run.Provider,
                        ["message"] = failure ?? SetupText.Get("로그인하지 못했습니다.", "Sign-in failed."),
                    };
            }

            run.Cts.Dispose();
            Publish();

            // What follows changes shared state (provider list, models): it waits for the other commands.
            try { await _commands.WaitAsync(_token).ConfigureAwait(false); }
            catch (OperationCanceledException) { return; }
            var restart = false;
            try
            {
                if (stopHelper) await _helper.StopAsync().ConfigureAwait(false);
                if (succeeded) restart = await AfterSignInAsync(run.Provider).ConfigureAwait(false);
                else if (stopHelper) ScheduleProvidersRefresh();
                Publish();
            }
            catch (OperationCanceledException) { /* Shutdown. */ }
            finally { _commands.Release(); }

            if (restart)
            {
                // The helper must be gone before the engine starts on the same profile (unless a new sign-in already uses it).
                bool busy;
                lock (_lock) busy = _run != null;
                if (!busy)
                {
                    try { await _helper.StopAsync().ConfigureAwait(false); }
                    catch (Exception) { _logger.Warn("The sign-in helper could not be stopped before the engine restart."); }
                }

                await RestartSafelyAsync().ConfigureAwait(false);
            }
        }

        /// <summary>
        /// A sign-in stored a credential: the provider list is read again from the helper (which has it), and the engine either restarts
        /// (it is not running: returns true) or is asked for its models — when it lists the provider's models the change is live,
        /// otherwise a restart is recommended.
        /// </summary>
        private async Task<bool> AfterSignInAsync(string provider)
        {
            bool busy;
            lock (_lock) busy = _run != null; // Another sign-in already started: the helper is its.
            if (!busy)
            {
                try { await RefreshProvidersAsync().ConfigureAwait(false); }
                catch (ProviderSetupException) { /* providersError is published. */ }
                Publish();
            }

            lock (_lock)
            {
                _models = null;
                _modelsError = null;
            }

            if (_bridgeState() != BridgeState.Running) return true;
            JArray? models = null;
            try { models = await FetchModelsAsync().ConfigureAwait(false); }
            catch (ProviderSetupException) { /* Unknown: recommend a restart below. */ }
            catch (Exception ex) when (!(ex is OperationCanceledException)) { _logger.Debug("The engine's models could not be read after a sign-in: " + ex.GetType().Name); }

            if (models == null && _bridgeState() != BridgeState.Running) return true;
            // A sign-in method that stores as another provider unlocks that provider's models (StoredUnder).
            var unlocked = StoredUnder.TryGetValue(provider, out var storedAs) ? storedAs : provider;
            lock (_lock)
            {
                var listed = models != null && models.Any(model =>
                    string.Equals((string?)model["provider"], provider, StringComparison.OrdinalIgnoreCase)
                    || string.Equals((string?)model["provider"], unlocked, StringComparison.OrdinalIgnoreCase));
                if (models != null) _models = models;
                _restartRecommended = _restartRecommended || !listed;
            }

            return false;
        }

        // ---- sign-in requests of omp (on the helper's reader thread) ------------------------------------------------------

        private void OnHelperUiRequest(LoginRun run, JObject frame)
        {
            switch (frame.GetString("method"))
            {
                case OmpUiMethods.OpenUrl: HandleOpenUrl(run, frame); break;
                case OmpUiMethods.Notify: HandleProgress(run, frame); break;
                case OmpUiMethods.Input: HandlePrompt(run, frame); break;
                case OmpUiMethods.Cancel: HandlePromptWithdrawn(run, frame); break;
                case OmpUiMethods.Select:
                case OmpUiMethods.Confirm:
                case OmpUiMethods.Editor:
                    Decline(frame.GetString("id"));
                    break;
                // setStatus, setWidget, setTitle...: nothing to show.
            }
        }

        private bool IsCurrent(LoginRun run) => !_disposed && !run.Cancelled && ReferenceEquals(_run, run);

        /// <summary>omp wants a browser page: <c>launchUrl ?? url</c> is opened when it is an http(s) address, and shown in the login state.</summary>
        private void HandleOpenUrl(LoginRun run, JObject frame)
        {
            var launchUrl = frame.GetString("launchUrl");
            var target = BrowserUrl(!string.IsNullOrEmpty(launchUrl) ? launchUrl : frame.GetString("url"));
            var instructions = SetupSanitize.Block(frame.GetString("instructions"), 500);
            var open = false;
            lock (_lock)
            {
                if (!IsCurrent(run)) return;
                if (target != null && run.BrowserOpens < MaxBrowserOpensPerLogin)
                {
                    run.BrowserOpens++;
                    open = true;
                }
            }

            string? progress = null;
            if (target == null)
                progress = SetupText.Get("http(s) 주소가 아닌 링크는 열지 않았습니다.", "A link that is not an http(s) address was not opened.");
            else if (open)
            {
                try
                {
                    _openUrl(target);
                    _logger.Info("Opened the sign-in page of " + new Uri(target).Host + ".");
                }
                catch (Exception ex)
                {
                    _logger.Warn("The browser could not be opened for the sign-in: " + ex.GetType().Name);
                    progress = SetupText.Get("브라우저를 열지 못했습니다.", "The browser could not be opened.");
                }
            }

            lock (_lock)
            {
                if (!IsCurrent(run)) return;
                if (open || (target != null && _loginState["url"] == null)) _loginState["url"] = target;
                if (instructions != null) _loginState["instructions"] = Scrub(run, instructions);
                if (progress != null) _loginState["progress"] = progress;
            }

            Publish();
        }

        private void HandleProgress(LoginRun run, JObject frame)
        {
            var message = SetupSanitize.Block(frame.GetString("message"), 400);
            if (message == null) return;
            lock (_lock)
            {
                if (!IsCurrent(run)) return;
                _loginState["progress"] = Scrub(run, message);
            }

            Publish();
        }

        /// <summary>omp waits for typed input (e.g. the authorization code); the page answers with <c>answerPrompt</c>.</summary>
        private void HandlePrompt(LoginRun run, JObject frame)
        {
            var id = frame.GetString("id");
            if (!ValidPromptId(id)) return; // Nothing to answer with.
            var message = SetupSanitize.Block(frame.GetString("title") ?? frame.GetString("message"), 500)
                ?? SetupText.Get("로그인이 입력을 기다리고 있습니다.", "The sign-in is waiting for input.");
            var placeholder = SetupSanitize.Line(frame.GetString("placeholder"), 200);
            string? replaced = null;
            var current = true;
            lock (_lock)
            {
                if (!IsCurrent(run)) current = false;
                else
                {
                    replaced = run.PromptId;
                    run.PromptId = id;
                    var prompt = new JObject { ["id"] = id, ["message"] = message };
                    if (placeholder != null) prompt["placeholder"] = placeholder;
                    _loginState["prompt"] = prompt;
                }
            }

            if (!current) { Decline(id); return; }
            if (replaced != null && replaced != id) Decline(replaced);
            Publish();
        }

        /// <summary>omp withdrew a dialog it asked for (<c>cancel</c> request).</summary>
        private void HandlePromptWithdrawn(LoginRun run, JObject frame)
        {
            var target = frame.GetString("targetId");
            lock (_lock)
            {
                if (!IsCurrent(run) || target == null || run.PromptId != target) return;
                run.PromptId = null;
                _loginState.Remove("prompt");
            }

            Publish();
        }

        private void Decline(string? id)
        {
            if (!ValidPromptId(id)) return;
            try { _helper.SendFrameAsync(OmpCommands.ExtensionUiCancelled(id!), CancellationToken.None).Forget(_logger, "decline a sign-in dialog"); }
            catch (OmpBridgeException) { /* The helper ended meanwhile. */ }
        }

        private static bool ValidPromptId(string? id) => !string.IsNullOrEmpty(id) && id!.Length <= 200 && !id.Any(char.IsControl);

        /// <summary>The address omp asks to open, normalized (escaped) for the shell — or null when it is not an absolute http(s) address.</summary>
        internal static string? BrowserUrl(string? text)
        {
            if (string.IsNullOrWhiteSpace(text)) return null;
            var url = text!.Trim();
            if (url.Length > MaxUrlChars) return null;
            // Anything that would have to be escaped (or could end a quoted argument) is not a sign-in link.
            foreach (var c in url)
            {
                if (char.IsControl(c) || char.IsWhiteSpace(c) || c == '"' || c == '<' || c == '>' || c == '\\' || c == '^' || c == '`' || c == '{' || c == '}' || c == '|') return null;
            }

            if (!Uri.TryCreate(url, UriKind.Absolute, out var uri)) return null;
            if (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps) return null;
            if (string.IsNullOrEmpty(uri.Host) || !string.IsNullOrEmpty(uri.UserInfo)) return null;
            var normalized = uri.AbsoluteUri;
            return normalized.Length <= MaxUrlChars ? normalized : null;
        }

        // ---- answers and cancel -----------------------------------------------------------------------------------------

        /// <summary><c>answerPrompt</c>: hands the typed text (or a cancel) to the waiting sign-in; the text is never stored or logged.</summary>
        private async Task AnswerPromptAsync(JObject request)
        {
            var promptId = Text(request, "promptId");
            string? value = null;
            var token = request["value"];
            if (token != null && token.Type != JTokenType.Null)
            {
                if (token.Type != JTokenType.String) throw new ProviderSetupException(SetupText.Get("입력 형식이 올바르지 않습니다.", "The input is invalid."));
                value = (string)token!;
                if (value.Length > 4096) throw new ProviderSetupException(SetupText.Get("입력이 너무 깁니다.", "The input is too long."));
                value = value.Trim();
            }

            lock (_lock)
            {
                var run = _run;
                if (run == null || run.Cancelled || string.IsNullOrEmpty(promptId) || run.PromptId != promptId)
                    throw new ProviderSetupException(SetupText.Get("대기 중인 입력 요청이 없습니다.", "No sign-in input is being waited for."));
                run.PromptId = null;
                _loginState.Remove("prompt");
                if (!string.IsNullOrEmpty(value)) RememberAnswer(run, value!);
            }

            var frame = value == null ? OmpCommands.ExtensionUiCancelled(promptId!) : OmpCommands.ExtensionUiValue(promptId!, value);
            try { await _helper.SendFrameAsync(frame, _token).ConfigureAwait(false); }
            catch (Exception ex) when (!(ex is OperationCanceledException))
            {
                throw new ProviderSetupException(SetupText.Get("입력을 전달하지 못했습니다.", "The input could not be delivered."));
            }
        }

        /// <summary>Remembers an answer so it can be scrubbed from texts; a pasted redirect link also contributes its query values.</summary>
        private static void RememberAnswer(LoginRun run, string value)
        {
            lock (run.Answers)
            {
                AddAnswer(run, value);
                var query = value.IndexOfAny(new[] { '?', '#' });
                if (query < 0) return;
                foreach (var pair in value.Substring(query + 1).Split('&', '#', '?'))
                {
                    var equals = pair.IndexOf('=');
                    if (equals < 0 || pair.Length - equals - 1 < 8) continue;
                    var raw = pair.Substring(equals + 1);
                    AddAnswer(run, raw);
                    try { AddAnswer(run, Uri.UnescapeDataString(raw)); }
                    catch (UriFormatException) { /* The raw value is already scrubbed. */ }
                }
            }
        }

        private static void AddAnswer(LoginRun run, string value)
        {
            if (value.Length >= MinScrubbedAnswerChars && !run.Answers.Contains(value)) run.Answers.Add(value);
        }

        private static string Scrub(LoginRun run, string text)
        {
            string[] answers;
            lock (run.Answers) answers = run.Answers.OrderByDescending(answer => answer.Length).ToArray();
            foreach (var answer in answers) text = text.Replace(answer, "***");
            return text;
        }

        /// <summary><c>cancelLogin</c>: ends the running sign-in (the helper process is killed; a console sign-in's console is closed).</summary>
        private void CancelLogin()
        {
            LoginRun? run;
            IProviderLoginProcess? console;
            var stale = false;
            lock (_lock)
            {
                run = _run;
                console = _login;
            }

            if (run == null && console == null) return;
            if (console != null)
            {
                try { console.Kill(); }
                catch (Exception) { throw new ProviderSetupException(SetupText.Get("로그인 콘솔을 종료하지 못했습니다.", "The login console could not be stopped.")); }
            }

            lock (_lock)
            {
                if (run != null && ReferenceEquals(_run, run))
                {
                    run.Cancelled = true;
                    _run = null;
                    // The process is about to end, and the next one starts with the current keys.
                    stale = _helperStale;
                    _helperStale = false;
                }
                else run = null;

                if (console != null && ReferenceEquals(_login, console)) _login = null;
                else console = null;
                if (run == null && console == null) return;
                _loginState["state"] = "idle";
                _loginState["message"] = SetupText.Get("로그인을 취소했습니다.", "Login cancelled.");
                _loginState.Remove("url");
                _loginState.Remove("instructions");
                _loginState.Remove("progress");
                _loginState.Remove("prompt");
            }

            if (run != null)
            {
                try { run.Cts.Cancel(); }
                catch (Exception) { /* Already disposed. */ }
                _helper.Kill("the sign-in was cancelled");
                // A key changed during the sign-in: a loaded provider list is read again from a fresh helper.
                if (stale) ScheduleProvidersRefresh();
            }

            console?.Dispose();
        }

        // ---- console sign-in (fallback) -----------------------------------------------------------------------------------

        private static string ReadLoginProvider(JObject request, bool required)
        {
            var token = request["provider"];
            if (token != null && token.Type != JTokenType.String && token.Type != JTokenType.Null)
                throw new ProviderSetupException(SetupText.Get("로그인 공급자 ID 형식이 올바르지 않습니다.", "Login provider ID is invalid."));
            var provider = Text(request, "provider") ?? string.Empty;
            if (provider.Length == 0)
            {
                if (required) throw new ProviderSetupException(SetupText.Get("로그인할 공급자를 고르지 않았습니다.", "No provider was chosen."));
                return provider;
            }

            if (!LoginProvider.IsMatch(provider))
                throw new ProviderSetupException(SetupText.Get("로그인 공급자 ID 형식이 올바르지 않습니다.", "Login provider ID is invalid."));
            return provider;
        }

        /// <summary><c>loginConsole</c>: <c>omp login [provider]</c> in its own console window (an empty provider is omp's own picker).</summary>
        private IProviderLoginProcess StartConsoleLogin(JObject request)
        {
            var provider = ReadLoginProvider(request, required: false);
            lock (_lock)
            {
                if (_disposed) throw new OperationCanceledException();
                if (_login != null || _run != null) throw new ProviderSetupException(SetupText.Get("이미 로그인이 진행 중입니다.", "A provider login is already running."));
                var resolution = _resolver.Resolve(_settings.OmpExecutablePath, _environment.ExtensionDirectory);
                if (!resolution.Found) throw new ProviderSetupException(OmpNotFoundText());
                try
                {
                    Directory.CreateDirectory(_environment.DataDirectory);
                    var launch = new OmpLaunchOptions { ExecutablePath = resolution.Path! };
                    launch.Arguments.Add("login");
                    if (provider.Length != 0) launch.Arguments.Add(provider);
                    SystemOmpProcessLauncher.BuildCommand(launch, out var fileName, out var arguments);
                    var startInfo = new ProcessStartInfo
                    {
                        FileName = fileName, Arguments = arguments, WorkingDirectory = _environment.DataDirectory,
                        UseShellExecute = false, CreateNoWindow = false,
                        RedirectStandardInput = false, RedirectStandardOutput = false, RedirectStandardError = false,
                    };
                    _login = _startLogin(startInfo);
                    _loginState = new JObject { ["state"] = "running", ["provider"] = provider };
                    return _login;
                }
                catch (Exception)
                {
                    _loginState = new JObject { ["state"] = "failed", ["provider"] = provider, ["message"] = SetupText.Get("로그인 창을 시작하지 못했습니다.", "The login window could not be started.") };
                    throw new ProviderSetupException(SetupText.Get("로그인 창을 시작하지 못했습니다.", "The login window could not be started."));
                }
            }
        }

        private async Task ObserveConsoleLoginAsync(IProviderLoginProcess process)
        {
            int? code = null;
            try { code = await process.Completion.ConfigureAwait(false); }
            catch (Exception) { /* Fixed, credential-free message below. */ }
            var restart = false;
            try { await _commands.WaitAsync(_token).ConfigureAwait(false); }
            catch (OperationCanceledException) { return; }
            try
            {
                lock (_lock)
                {
                    if (_disposed || !ReferenceEquals(_login, process)) return;
                    _login = null;
                    _loginState["state"] = code == 0 ? "succeeded" : "failed";
                    _loginState["message"] = code == 0 ? SetupText.Get("로그인했습니다.", "Login completed.") : code.HasValue
                        ? SetupText.Get(
                            "로그인이 코드 " + code.Value.ToString(CultureInfo.InvariantCulture) + "(으)로 끝났습니다.",
                            "Sign-in ended with code " + code.Value.ToString(CultureInfo.InvariantCulture) + ".")
                        : SetupText.Get("로그인 창의 종료 상태를 알 수 없습니다.", "The login window's exit status is unknown.");
                }

                process.Dispose();
                if (code == 0)
                {
                    restart = CredentialsChanged();
                    // Another process stored the credential: the helper's view of it is stale.
                    CredentialSourcesChanged();
                }

                Publish();
            }
            finally { _commands.Release(); }
            if (restart) await RestartSafelyAsync().ConfigureAwait(false);
        }
    }

    /// <summary>Injectable login process; unlike the RPC child, it owns an interactive console and no redirected pipes.</summary>
    internal interface IProviderLoginProcess : IDisposable
    {
        Task<int> Completion { get; }
        void Kill();
    }

    /// <remarks>
    /// Deliberately not in a kill-on-close job object (unlike the RPC child): omp login opens the user's browser, which
    /// would otherwise be part of the job and be closed with it. Cancel and shutdown end the omp process itself only.
    /// </remarks>
    internal sealed class SystemProviderLoginProcess : IProviderLoginProcess
    {
        private readonly Process _process;
        private readonly TaskCompletionSource<int> _completion = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);

        private SystemProviderLoginProcess(Process process)
        {
            _process = process;
            process.Exited += OnExited;
        }

        public static IProviderLoginProcess Start(ProcessStartInfo startInfo)
        {
            var process = new Process { StartInfo = startInfo, EnableRaisingEvents = true };
            var login = new SystemProviderLoginProcess(process);
            try
            {
                if (!process.Start()) throw new InvalidOperationException("The login process did not start.");
                if (process.HasExited) login.OnExited(process, EventArgs.Empty);
                return login;
            }
            catch
            {
                try { if (!process.HasExited) process.Kill(); } catch (Exception) { }
                login.Dispose();
                throw new ProviderSetupException(SetupText.Get("로그인 창을 시작하지 못했습니다.", "The login window could not be started."));
            }
        }

        public Task<int> Completion => _completion.Task;

        public void Kill()
        {
            if (!_process.HasExited) _process.Kill();
        }

        private void OnExited(object? sender, EventArgs e)
        {
            try { _completion.TrySetResult(_process.ExitCode); }
            catch (Exception) { _completion.TrySetException(new ProviderSetupException(SetupText.Get("로그인 종료 상태를 알 수 없습니다.", "The login exit status is unavailable."))); }
        }

        public void Dispose()
        {
            _process.Exited -= OnExited;
            _completion.TrySetCanceled();
            _process.Dispose();
        }
    }
}
