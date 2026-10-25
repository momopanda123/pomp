using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;
using Pomp.Bridge;
using Pomp.Adapters;
using Pomp.Diagnostics;
using Pomp.Features;
using Pomp.Internal;
using Pomp.Protocol;
using Pomp.Ui;

namespace Pomp.Hosting
{
    public sealed partial class PompSession
    {
        private string _recoveryStatus = "ready";
        private string? _recoveryFile;
        private string? _recoverySession;
        // The model and thinking level in force for the conversation being recovered: the new engine gets them back explicitly.
        private ModelInfoPayload? _recoveryModel;
        private string? _recoveryThinking;
        /// <summary>Wait until the newly launched engine has established its tab's conversation and replayed its history.</summary>
        internal async Task WaitForConversationAsync(CancellationToken token)
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token, _disposeCts.Token);
            timeout.CancelAfter(TimeSpan.FromSeconds(100));
            while (true)
            {
                lock (_stateLock)
                {
                    if (_recoveryStatus == "failed") throw new InvalidOperationException(_info.RecoveryMessage ?? "Conversation recovery failed.");
                    if (_recoveryStatus == "ready" && !_sessionSwitchUncertain && _managedInitialTranscriptReady && !string.IsNullOrEmpty(_info.SessionId)) return;
                }
                if (_bridge.State == BridgeState.Failed || _bridge.State == BridgeState.Stopped)
                    throw new InvalidOperationException("The conversation engine did not start.");
                await Task.Delay(30, timeout.Token).ConfigureAwait(false);
            }
        }
        private string _recoveryRoot = "";

        /// <summary>How long recovery (and a session switch check) waits for the engine to settle before it judges, and how often it looks again.</summary>
        private static readonly TimeSpan RecoverySettleTimeout = TimeSpan.FromSeconds(60);
        private static readonly TimeSpan RecoverySettlePoll = TimeSpan.FromSeconds(1);
        private TaskCompletionSource<bool> _settledSignal = NewSettledSignal();

        private static TaskCompletionSource<bool> NewSettledSignal() => new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

        /// <summary>Wakes a recovery that waits for the engine to settle (omp sent session_settled).</summary>
        private void SignalSessionSettled() => Interlocked.Exchange(ref _settledSignal, NewSettledSignal()).TrySetResult(true);

        /// <summary>
        /// The engine state once it is settled. omp reports isSettled only when no run is live or admitted, nothing is queued and no
        /// background work can wake the session, which a just-started engine often is not yet. omp sends session_settled only after
        /// agent activity, so the state is also read again every second. After the timeout the last state is returned as it is.
        /// </summary>
        private async Task<JObject> WaitForSettledStateAsync(OmpConnection connection, JObject state, CancellationToken token, string purpose = "Conversation recovery")
        {
            if (state.GetBool("isSettled") == true) return state;
            var started = DateTime.UtcNow; var deadline = started + RecoverySettleTimeout;
            while (state.GetBool("isSettled") != true && DateTime.UtcNow < deadline)
            {
                var signal = Volatile.Read(ref _settledSignal).Task;
                var wait = deadline - DateTime.UtcNow;
                if (wait > RecoverySettlePoll) wait = RecoverySettlePoll;
                if (wait > TimeSpan.Zero) await Task.WhenAny(signal, Task.Delay(wait, token)).ConfigureAwait(false);
                token.ThrowIfCancellationRequested();
                var reply = await connection.SendCommandAsync(OmpCommands.GetState(), TimeSpan.FromSeconds(15), token).ConfigureAwait(false);
                if (!reply.Success || reply.Data is not JObject next) throw new InvalidOperationException("Could not read the active conversation.");
                state = next;
            }
            var waited = (long)(DateTime.UtcNow - started).TotalMilliseconds;
            if (state.GetBool("isSettled") == true) _logger.Debug($"{purpose} waited {waited} ms for the engine to settle.");
            else _logger.Debug($"The engine did not settle within {waited} ms ({purpose}).");
            return state;
        }

        private void BeginSessionRecoveryLocked()
        {
            if (!string.Equals(NormalizePath(_workingDirectory), NormalizePath(_recoveryRoot), StringComparison.OrdinalIgnoreCase))
            {
                _recoveryFile = null; _recoverySession = null; _recoveryModel = null; _recoveryThinking = null;
            }
            if (_recoveryStatus == "ready" && string.Equals(NormalizePath(_workingDirectory), NormalizePath(_sessionFolderRoot), StringComparison.OrdinalIgnoreCase))
            {
                _recoveryFile = _sessionFile; _recoverySession = _info.SessionId;
                _recoveryModel = _info.Model; _recoveryThinking = _info.ThinkingLevel;
            }
            _recoveryRoot = _workingDirectory;
            _recoveryStatus = "restoring";
            _sessionSwitchUncertain = true;
            _info = CloneInfo(_info);
            _info.RecoveryStatus = "restoring"; _info.RecoveryMessage = null;
        }

        /// <summary>False when <paramref name="currentLocked"/> no longer holds. Never call this while holding the state lock.</summary>
        private bool PublishRecovery(string status, string? message = null, Func<bool>? currentLocked = null)
        {
            SessionInfoPayload info;
            lock (_stateLock)
            {
                if (currentLocked != null && !currentLocked()) return false;
                _recoveryStatus = status;
                info = CloneInfo(_info);
                info.RecoveryStatus = status; info.RecoveryMessage = message;
                _info = info; _infoJson = "";
            }
            _ui.Post(UiMessageTypes.SessionInfo, info, UiDelivery.State);
            return true;
        }

        private async Task RecoverSessionAsync()
        {
            OmpConnection? connection; CancellationToken token; string root; string? path, id;
            ModelInfoPayload? model; string? thinking;
            bool restoreInitial;
            lock (_stateLock)
            {
                connection = _epochConnection; token = _epoch.Token; root = _recoveryRoot;
                path = _recoveryFile; id = _recoverySession; model = _recoveryModel; thinking = _recoveryThinking;
                restoreInitial = !_managedInitialTranscriptReady;
                if (connection == null || _bridge.State != BridgeState.Running || _recoveryStatus == "ready") return;
                _sessionSwitchUncertain = true;
            }
            PublishRecovery("restoring");
            try
            {
                var reply = await connection.SendCommandAsync(OmpCommands.GetState(), TimeSpan.FromSeconds(15), token).ConfigureAwait(false);
                if (!reply.Success || reply.Data is not JObject state) throw new InvalidOperationException("Could not read the active conversation.");
                // Judge only a settled engine: right after a start it may still be draining queued or background work.
                state = await WaitForSettledStateAsync(connection, state, token).ConfigureAwait(false);
                var unsaved = false;
                if (!string.IsNullOrEmpty(id) && (state.GetString("sessionId") != id || (!string.IsNullOrEmpty(path) && !SameSessionPath(state.GetString("sessionFile"), path))))
                {
                    // A conversation omp never saved (no session file) has nothing to restore.
                    unsaved = string.IsNullOrEmpty(path) || !await Task.Run(() => { CheckpointStore.CheckDirectPath(path!); return File.Exists(path); }, token).ConfigureAwait(false);
                }
                if (unsaved)
                {
                    id = null; path = null;
                    PostNotice("info", PompLanguage.Pick("이전 대화의 세션 파일이 없어 새 대화로 이어가요.", "The previous conversation has no session file; it continues as a new one."));
                    state = await RestoreModelAsync(connection, state, model, thinking, token).ConfigureAwait(false);
                }
                else if (!string.IsNullOrEmpty(id) && (state.GetString("sessionId") != id || (!string.IsNullOrEmpty(path) && !SameSessionPath(state.GetString("sessionFile"), path))))
                {
                    if (state.GetBool("isStreaming") == true || state.GetBool("hasPendingAsyncWork") == true || _ui.PendingRequestCount > 0)
                        throw new InvalidOperationException("The conversation is not idle; the previous conversation cannot be restored safely.");
                    var store = new RecentSessionStore(_environment.DataDirectory, root);
                    var resolved = await Task.Run(() =>
                    {
                        CheckpointStore.CheckDirectPath(path!);
                        if (!File.Exists(path)) throw new FileNotFoundException("The previous conversation file is unavailable.");
                        store.Remember(id!, path!, "");
                        return store.Resolve(root, ContentHash.Compute(Path.GetFullPath(path!)), path);
                    }, token).ConfigureAwait(false);
                    var switched = await SwitchSessionAsync(connection, resolved, token).ConfigureAwait(false);
                    if (!switched.Success || switched.Data.GetBool("cancelled") == true) throw new InvalidOperationException("The previous conversation could not be restored." + (switched.Error != null ? " " + switched.Error : ""));
                    reply = await connection.SendCommandAsync(OmpCommands.GetState(), TimeSpan.FromSeconds(15), token).ConfigureAwait(false);
                    if (!reply.Success || reply.Data is not JObject restored) throw new InvalidOperationException("Could not confirm the restored conversation.");
                    state = await WaitForSettledStateAsync(connection, restored, token).ConfigureAwait(false);
                    // omp records no model_change for the model a new conversation inherits: its file alone would restart on the launch model.
                    state = await RestoreModelAsync(connection, state, model, thinking, token).ConfigureAwait(false);
                }
                if (state.GetBool("isSettled") != true) throw new InvalidOperationException("The conversation is not settled yet.");
                if (string.IsNullOrEmpty(state.GetString("sessionId")) || (!string.IsNullOrEmpty(id) && (state.GetString("sessionId") != id || (!string.IsNullOrEmpty(path) && !SameSessionPath(state.GetString("sessionFile"), path)))))
                    throw new InvalidOperationException("The requested conversation was not confirmed.");
                token.ThrowIfCancellationRequested();
                ApplyState(state, acceptRecovery: true, currentLocked: () => ReferenceEquals(connection, _epochConnection) && root == _workingDirectory, confirmSession: true);
                if (restoreInitial)
                {
                    try { await RestoreTranscriptAsync(connection, token).ConfigureAwait(false); }
                    catch (Exception ex) when (!(ex is OperationCanceledException)) { _logger.Warn("Could not restore the tab transcript: " + ex.Message); }
                    finally { lock (_stateLock) _managedInitialTranscriptReady = true; }
                }
                // The host transcript is deliberately retained, including tool output and interrupted turns.
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested) { }
            catch (Exception ex)
            {
                lock (_stateLock) _managedInitialTranscriptReady = true;
                if (PublishRecovery("failed", PompLanguage.Pick("이전 대화를 확인하지 못했어요. 기록은 유지했습니다.", "Could not confirm the previous conversation. Its displayed history was kept."),
                    () => ReferenceEquals(connection, _epochConnection) && !token.IsCancellationRequested))
                    _logger.Warn("Conversation recovery failed: " + ex.Message);
            }
        }

        /// <summary>
        /// The new engine starts on its launch model, and a conversation's session file may not name one (a new conversation's inherited
        /// model is never written): a model or thinking level chosen in the picker (not saved in the settings) would silently change with a
        /// restart, such as a new permission mode. A no-op when the recovered conversation already has them.
        /// </summary>
        private async Task<JObject> RestoreModelAsync(OmpConnection connection, JObject state, ModelInfoPayload? model, string? thinking, CancellationToken token)
        {
            var changed = false;
            var current = state.GetObject("model");
            if (model != null && !string.IsNullOrEmpty(model.Provider) && !string.IsNullOrEmpty(model.Id)
                && (current?.GetString("provider") != model.Provider || current?.GetString("id") != model.Id))
            {
                var reply = await connection.SendCommandAsync(OmpCommands.SetModel(model.Provider, model.Id), TimeSpan.FromSeconds(30), token).ConfigureAwait(false);
                if (reply.Success) changed = true;
                else _logger.Warn("Could not keep the conversation's model after the engine restarted: " + reply.Error);
            }
            if (!string.IsNullOrEmpty(thinking) && !string.Equals(state.GetString("thinkingLevel"), thinking, StringComparison.Ordinal))
            {
                var reply = await connection.SendCommandAsync(OmpCommands.SetThinkingLevel(thinking!), TimeSpan.FromSeconds(15), token).ConfigureAwait(false);
                if (reply.Success) changed = true;
                else _logger.Warn("Could not keep the conversation's thinking level after the engine restarted: " + reply.Error);
            }
            if (!changed) return state;
            var refreshed = await connection.SendCommandAsync(OmpCommands.GetState(), TimeSpan.FromSeconds(15), token).ConfigureAwait(false);
            return refreshed.Success && refreshed.Data is JObject next ? next : state;
        }

        private static bool SameSessionPath(string? first, string? second) => !string.IsNullOrEmpty(first) && !string.IsNullOrEmpty(second)
            && string.Equals(NormalizePath(first), NormalizePath(second), Path.DirectorySeparatorChar == '\\' ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);
    }
}
