using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;
using Pomp.Bridge;
using Pomp.Diagnostics;
using Pomp.Features;
using Pomp.Internal;
using Pomp.Protocol;
using Pomp.Ui;

namespace Pomp.Hosting
{
    // Computer use for this conversation (docs/architecture.md §8.5). The composer's button runs omp's /computer on|off, a builtin answered
    // with command_output and no model request. The extension's guard keeps subagents (they run unasked) away from Eval while it is on, so the
    // guard's state file is written before switching on and after switching off. Everything starts off: each omp process (the overlay) and a
    // reconnect. One conversation on the PC holds it (ComputerOwnerLock): taken when it is switched on, kept while its tab stays open (the
    // engine turns it off while another tab is shown and on again on return), released when it is switched off, its tab closes or its
    // conversation ends, omp restarts, or POMP closes.
    public sealed partial class PompSession
    {
        private readonly object _computerLock = new object();
        private ComputerUseOptions _computerOptions = ComputerUseOptions.Default; // what the running omp was started with
        private string? _computerStateDirectory;
        private bool _computerGuard;
        private bool _computerDesktop;
        private bool _computerPending;
        private string? _computerMessage;
        private string? _computerSessionId;
        private TaskCompletionSource<string>? _computerAnswer;
        private ComputerOwnerLock? _computerOwner;

        private ComputerUseOptions ComputerOptions => (_settings as IComputerUseSettings)?.ComputerUse ?? ComputerUseOptions.Default;

        /// <summary>At each omp start: the options it runs with (nothing without the extension's guard), a cleared state file, and no owner.</summary>
        private ComputerUseOptions PrepareComputerUse(string stateDirectory, bool guardInstalled, bool blockNetwork)
        {
            var options = ComputerOptions;
            var effective = guardInstalled
                ? new ComputerUseOptions(options.Policy, options.Browser && !blockNetwork, options.ConfirmReads)
                : new ComputerUseOptions(ComputerUsePolicy.Off, browser: false, options.ConfirmReads);
            ComputerOwnerLock? owner;
            lock (_computerLock)
            {
                _computerOptions = effective;
                _computerStateDirectory = stateDirectory;
                _computerGuard = guardInstalled;
                _computerDesktop = false;
                _computerSessionId = null;
                _computerMessage = guardInstalled || options.Policy == ComputerUsePolicy.Off ? null
                    : PompLanguage.Pick("확장이 꺼져 있어 컴퓨터 사용을 켤 수 없어요.", "Computer use cannot be switched on because extensions are off.");
                owner = _computerOwner;
            }

            if (owner == null)
            {
                var root = Path.GetDirectoryName(stateDirectory);
                if (!string.IsNullOrEmpty(root))
                {
                    try
                    {
                        owner = new ComputerOwnerLock(root!, _hostProcessId, _logger);
                        owner.Changed += (_, __) => PublishComputerState();
                        lock (_computerLock)
                        {
                            if (_computerOwner == null) _computerOwner = owner;
                            else { owner.Dispose(); owner = _computerOwner; }
                        }
                    }
                    catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException || ex is ArgumentException || ex is WaitHandleCannotBeOpenedException)
                    {
                        _logger.Warn("Could not prepare the computer use owner lock: " + ex.Message);
                        owner = null;
                    }
                }
            }

            owner?.Release();
            TryWriteComputerState(null, desktop: false);
            PublishComputerState();
            return effective;
        }

        private UiMessage ComputerStateMessage()
        {
            ComputerOwnerLock? ownerLock;
            lock (_computerLock) ownerLock = _computerOwner;
            var owner = ownerLock?.Current();
            string? current;
            lock (_stateLock) current = _info.SessionId;
            lock (_computerLock)
            {
                return new UiMessage(UiMessageTypes.ComputerState, new JObject
                {
                    ["policy"] = ComputerOptions.Policy == ComputerUsePolicy.Off ? "off" : "perConversation",
                    ["browser"] = _computerOptions.Browser,
                    ["confirmReads"] = _computerOptions.ConfirmReads,
                    ["desktop"] = _computerDesktop,
                    ["pending"] = _computerPending,
                    // Only while omp runs: a toggle pressed during start-up would only fail.
                    ["available"] = _computerGuard && _computerOptions.Policy != ComputerUsePolicy.Off && _bridge.State == BridgeState.Running,
                    ["message"] = _computerMessage,
                    // Another conversation (another tab here, or another POMP on this PC) holds it: this one cannot switch it on.
                    ["ownerElsewhere"] = owner == null || (owner.Mine && owner.SessionId == current) ? null : ComputerOwnerName(owner),
                });
            }
        }

        private void PublishComputerState()
        {
            var message = ComputerStateMessage();
            _ui.Post(message.Type, message.Payload, UiDelivery.State);
        }

        /// <summary>The holder as a chat names it: its conversation, and the workspace when it is another POMP's.</summary>
        private static string ComputerOwnerName(ComputerOwner owner)
        {
            var label = owner.Label.Length > 0 ? owner.Label : PompLanguage.Pick("다른 POMP", "Another POMP");
            return owner.Mine || owner.Workspace.Length == 0 ? label : label + " · " + owner.Workspace;
        }

        /// <summary>This conversation's name in the owner record: its title, else its agent or team, else a POMP conversation.</summary>
        private string ComputerConversationLabel()
        {
            string? name; bool main;
            lock (_stateLock) { name = _info.SessionName; main = _partner.Kind == "main"; }
            var label = !string.IsNullOrWhiteSpace(name) ? name!.Trim()
                : !main ? TabsView().Title ?? PompLanguage.Pick("에이전트 대화", "Agent conversation")
                : PompLanguage.Pick("POMP 대화", "POMP conversation");
            return label.Length > 80 ? label.Substring(0, 80) : label;
        }

        /// <summary>computer/set from the page (queued with the chat commands): answers with one computer/reply.</summary>
        private async Task HandleComputerSetAsync(JObject payload)
        {
            var requestId = payload.GetString("requestId") ?? string.Empty;
            string? error;
            try
            {
                error = await SetComputerDesktopAsync(payload.GetBool("desktop") == true, reason: null).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (_disposeCts.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                _logger.Warn("Switching computer use failed: " + ex.Message);
                error = ex.Message;
            }

            _ui.Post(UiMessageTypes.ComputerReply, new JObject { ["requestId"] = requestId, ["ok"] = error == null, ["error"] = error }, UiDelivery.State);
        }

        /// <summary>
        /// Switches computer use for this conversation; null when done, else why not. Switching off is allowed during a reply.
        /// Switching on takes the PC-wide owner record first; switching off releases it unless <paramref name="keepOwner"/> (another
        /// tab is shown and the owner's tab stays open).
        /// </summary>
        private async Task<string?> SetComputerDesktopAsync(bool on, string? reason, bool keepOwner = false)
        {
            string? sessionId;
            bool busy;
            lock (_stateLock)
            {
                sessionId = _info.SessionId;
                busy = _turns.Count > 0 || _info.IsStreaming;
            }

            ComputerOwnerLock? ownerLock;
            lock (_computerLock)
            {
                if (on && ComputerOptions.Policy == ComputerUsePolicy.Off)
                    return PompLanguage.Pick("컴퓨터 사용이 설정에서 '사용 안 함'이에요.", "Computer use is turned off in the settings.");
                if (on && (!_computerGuard || _computerOptions.Policy == ComputerUsePolicy.Off))
                    return _computerMessage ?? PompLanguage.Pick("컴퓨터 사용이 꺼진 채로 시작됐어요.", "Started with computer use off.");
                if (_computerDesktop == on)
                {
                    if (reason != null) _computerMessage = reason;
                    if (!on && !keepOwner) _computerOwner?.Release();
                    return null;
                }

                if (_computerPending) return PompLanguage.Pick("이전 요청을 처리하고 있어요.", "The previous request is still running.");
                _computerPending = true;
                ownerLock = _computerOwner;
            }

            PublishComputerState();
            var taken = false;
            try
            {
                if (_bridge.State != BridgeState.Running) return PompLanguage.Pick("연결되어 있지 않아요.", "Not connected.");
                if (on && busy) return PompLanguage.Pick("응답이 진행 중이에요.", "A reply is in progress.");
                if (on && ownerLock != null)
                {
                    string root;
                    lock (_stateLock) root = _workingDirectory;
                    var holder = ownerLock.TryTake(sessionId ?? "", ComputerConversationLabel(), Path.GetFileName(root.TrimEnd('\\', '/')));
                    if (holder != null)
                        return holder.SessionId.Length == 0
                            ? PompLanguage.Pick("다른 대화와 동시에 바꿀 수 없어요. 다시 해 주세요.", "Another conversation is changing it. Try again.")
                            : PompLanguage.Pick("다른 대화에서 사용 중이에요: ", "In use by another conversation: ") + ComputerOwnerName(holder);
                    taken = true;
                }

                // On: subagents are kept away first. The guard must know before omp does, or a subagent could act unasked.
                if (on && !TryWriteComputerState(sessionId, desktop: true))
                    return PompLanguage.Pick("상태 파일을 쓸 수 없어 켜지 않았어요.", "The state file could not be written, so it stays off.");
                var answer = await RunComputerCommandAsync(on ? "/computer on" : "/computer off").ConfigureAwait(false);
                var result = ComputerUse.ParseToggleOutput(answer, out var unavailable);
                if (on && result != true)
                {
                    TryWriteComputerState(sessionId, desktop: false);
                    return unavailable
                        ? PompLanguage.Pick("컴퓨터 사용을 쓸 수 없어요.", "Computer use is unavailable.")
                        : PompLanguage.Pick("켜졌는지 확인하지 못했어요. 꺼진 것으로 둘게요.", "It was not confirmed on; it stays off.");
                }

                if (!on && result != false)
                {
                    // Unconfirmed: the guard keeps subagents away and the record stays until omp restarts (which turns it off for sure).
                    return PompLanguage.Pick("꺼졌는지 확인하지 못했어요.", "It was not confirmed off.");
                }

                lock (_computerLock)
                {
                    _computerDesktop = on;
                    _computerSessionId = sessionId;
                    _computerMessage = reason;
                }

                taken = false;
                if (!on)
                {
                    TryWriteComputerState(sessionId, desktop: false);
                    if (!keepOwner) ownerLock?.Release();
                }

                return null;
            }
            finally
            {
                if (taken) ownerLock?.Release();
                lock (_computerLock) _computerPending = false;
                PublishComputerState();
            }
        }

        /// <summary>Sends one /computer prompt and returns its command_output text (written before the response; null when none came).</summary>
        private async Task<string?> RunComputerCommandAsync(string command)
        {
            var answer = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
            lock (_computerLock) _computerAnswer = answer;
            try
            {
                var response = await _bridge.SendCommandAsync(OmpCommands.Prompt(command), TimeSpan.FromSeconds(10), _disposeCts.Token).ConfigureAwait(false);
                if (!response.Success) throw new InvalidOperationException(response.Error ?? PompLanguage.Pick("요청을 거부했어요.", "The request was refused."));
                var done = await Task.WhenAny(answer.Task, Task.Delay(TimeSpan.FromSeconds(1), _disposeCts.Token)).ConfigureAwait(false);
                return done == answer.Task ? answer.Task.Result : null;
            }
            finally
            {
                lock (_computerLock) if (_computerAnswer == answer) _computerAnswer = null;
            }
        }

        /// <summary>True when <paramref name="frame"/> answers a pending /computer toggle (then it is not a chat notice).</summary>
        private bool ObserveCommandOutput(OmpFrame frame)
        {
            var text = frame.Json.GetString("text");
            lock (_computerLock)
            {
                if (_computerAnswer == null || ComputerUse.ParseToggleOutput(text, out _) == null) return false;
                _computerAnswer.TrySetResult(text!);
                return true;
            }
        }

        /// <summary>
        /// Another conversation (new or switched): computer use does not carry over to it. Leaving the owner keeps the record while the
        /// owner's tab stays open; coming back to the owner switches it on again.
        /// </summary>
        private void ComputerSessionObserved(string? sessionId)
        {
            string? left;
            bool desktop;
            lock (_computerLock)
            {
                if (string.IsNullOrEmpty(sessionId) || _computerPending || sessionId == _computerSessionId) return;
                desktop = _computerDesktop;
                left = _computerSessionId;
                if (!desktop) { if (TryWriteComputerState(sessionId, desktop: false)) _computerSessionId = sessionId; }
                else if (left == null) { _computerSessionId = sessionId; return; }
            }

            if (!desktop)
            {
                ComputerOwnerLock? ownerLock;
                lock (_computerLock) ownerLock = _computerOwner;
                var owner = ownerLock?.Current();
                if (owner != null && owner.Mine && owner.SessionId == sessionId) { lock (_stateLock) _commands.Enqueue(() => ResumeComputerAsync()); }
                else ReleaseClosedComputerOwner();
                PublishComputerState();
                return;
            }

            var keep = ComputerConversationOpen(left!);
            var reason = keep ? null : PompLanguage.Pick("대화가 바뀌어 화면 입력 제어를 껐어요.", "Screen & input control was switched off because the conversation changed.");
            lock (_stateLock) _commands.Enqueue(() => SetComputerDesktopAsync(false, reason, keepOwner: keep));
        }

        /// <summary>Back in the conversation that holds it: on again (a refusal releases it and says why on the button).</summary>
        private async Task ResumeComputerAsync()
        {
            var error = await SetComputerDesktopAsync(true, reason: null).ConfigureAwait(false);
            if (error == null) return;
            ComputerOwnerLock? ownerLock;
            lock (_computerLock) { _computerMessage = error; ownerLock = _computerOwner; }
            ownerLock?.Release();
            PublishComputerState();
        }

        /// <summary>True when <paramref name="sessionId"/> is the current conversation or an open tab's (the Default tab's included).</summary>
        private bool ComputerConversationOpen(string sessionId)
        {
            string root, current;
            lock (_stateLock) { root = _workingDirectory; current = _info.SessionId ?? ""; }
            if (sessionId == current) return true;
            try
            {
                var tabs = _tabs.Read(root);
                return tabs.Main == sessionId || tabs.Conversations.Any(e => e.Open && e.SessionId == sessionId);
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException || ex is InvalidOperationException || ex is ArgumentException)
            {
                return false;
            }
        }

        /// <summary>After the tabs changed: this POMP's record for a conversation no longer open (its tab closed, a new conversation in it) goes.</summary>
        private void ReleaseClosedComputerOwner()
        {
            ComputerOwnerLock? ownerLock;
            lock (_computerLock) ownerLock = _computerOwner;
            var owner = ownerLock?.Current();
            if (owner == null || !owner.Mine || ComputerConversationOpen(owner.SessionId)) return;
            ownerLock!.Release(owner.SessionId);
        }

        /// <summary>omp stopped or restarts: its replacement starts with computer use off (the overlay), and nothing is held.</summary>
        private void ComputerBridgeStateChanged(BridgeState state)
        {
            if (state != BridgeState.Running)
            {
                bool wasOn;
                ComputerOwnerLock? ownerLock;
                lock (_computerLock)
                {
                    wasOn = _computerDesktop;
                    _computerDesktop = false;
                    _computerSessionId = null;
                    if (wasOn) _computerMessage = PompLanguage.Pick("다시 시작돼 컴퓨터 사용이 꺼졌어요.", "Restarted, so computer use is off.");
                    ownerLock = _computerOwner;
                }

                ownerLock?.Release();
                if (wasOn) TryWriteComputerState(null, desktop: false);
            }

            PublishComputerState();
        }

        /// <summary>POMP closes: the record goes with it (another chat may switch it on at once).</summary>
        private void DisposeComputerOwner()
        {
            ComputerOwnerLock? ownerLock;
            lock (_computerLock) { ownerLock = _computerOwner; _computerOwner = null; }
            ownerLock?.Dispose();
        }

        /// <summary>Throws for a typed <c>/computer</c> command: the toggle is the page's (computer/set).</summary>
        private static void RefuseComputerCommand(string text)
        {
            if (ComputerUse.IsComputerCommand(text))
                throw new InvalidOperationException(PompLanguage.Pick("/computer 명령은 채팅에 입력해서 쓸 수 없어요.", "The /computer command cannot be typed in the chat."));
        }

        private bool TryWriteComputerState(string? sessionId, bool desktop)
        {
            string? directory;
            bool browser;
            lock (_computerLock)
            {
                directory = _computerStateDirectory;
                browser = _computerOptions.Browser;
            }

            if (directory == null) return !desktop;
            try
            {
                ComputerUse.WriteState(directory, sessionId, desktop, browser);
                return true;
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
            {
                _logger.Warn("Could not write " + ComputerUse.StateFileName + ": " + ex.Message);
                return false;
            }
        }
    }
}
