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
                _recoveryFile = null; _recoverySession = null;
            }
            if (_recoveryStatus == "ready" && string.Equals(NormalizePath(_workingDirectory), NormalizePath(_sessionFolderRoot), StringComparison.OrdinalIgnoreCase))
            {
                _recoveryFile = _sessionFile; _recoverySession = _info.SessionId;
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
            lock (_stateLock)
            {
                connection = _epochConnection; token = _epoch.Token; root = _recoveryRoot;
                path = _recoveryFile; id = _recoverySession;
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
                    // omp writes a session file only after the first answer: a conversation without one has nothing to restore.
                    unsaved = string.IsNullOrEmpty(path) || !await Task.Run(() => { CheckpointStore.CheckDirectPath(path!); return File.Exists(path); }, token).ConfigureAwait(false);
                }
                if (unsaved)
                {
                    id = null; path = null;
                    PostNotice("info", PompLanguage.Pick("이전 대화의 세션 파일이 없어 새 대화로 이어가요.", "The previous conversation has no session file; it continues as a new one."));
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
                }
                if (state.GetBool("isSettled") != true) throw new InvalidOperationException("The conversation is not settled yet.");
                if (string.IsNullOrEmpty(state.GetString("sessionId")) || (!string.IsNullOrEmpty(id) && (state.GetString("sessionId") != id || (!string.IsNullOrEmpty(path) && !SameSessionPath(state.GetString("sessionFile"), path)))))
                    throw new InvalidOperationException("The requested conversation was not confirmed.");
                token.ThrowIfCancellationRequested();
                ApplyState(state, acceptRecovery: true, currentLocked: () => ReferenceEquals(connection, _epochConnection) && root == _workingDirectory, confirmSession: true);
                // The host transcript is deliberately retained, including tool output and interrupted turns.
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested) { }
            catch (Exception ex)
            {
                if (PublishRecovery("failed", PompLanguage.Pick("이전 대화를 확인하지 못했어요. 기록은 유지했습니다.", "Could not confirm the previous conversation. Its displayed history was kept."),
                    () => ReferenceEquals(connection, _epochConnection) && !token.IsCancellationRequested))
                    _logger.Warn("Conversation recovery failed: " + ex.Message);
            }
        }

        private static bool SameSessionPath(string? first, string? second) => !string.IsNullOrEmpty(first) && !string.IsNullOrEmpty(second)
            && string.Equals(NormalizePath(first), NormalizePath(second), Path.DirectorySeparatorChar == '\\' ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);
    }
}
