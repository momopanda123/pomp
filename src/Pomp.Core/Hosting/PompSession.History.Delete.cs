using System;
using System.Collections.Generic;
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
    public sealed partial class PompSession
    {
        private sealed class SessionDeletionException : InvalidOperationException
        {
            public SessionDeletionException(string message) : base(message) { }
        }

        private async Task DeleteRecentSessionAsync(RecentSessionStore store, JObject args, string root, OmpConnection connection, CancellationToken token)
        {
            if (!(_environment is IPompRecycleBin recycle)) throw new SessionDeletionException(PompLanguage.Pick("휴지통을 사용할 수 없어요.", "The Recycle Bin is unavailable."));
            if (_terminalJobs.HasUnfinishedWork || _interactiveTerminal.HasUnfinishedWork) throw new SessionDeletionException(PompLanguage.Pick("실행 중인 터미널 작업이 있어요.", "A terminal job is still running."));
            string? path, id, name;
            lock (_stateLock) { path = _sessionFile; id = _info.SessionId; name = _info.SessionName; }
            await _sessionReferences.Enqueue(() => Task.CompletedTask).ConfigureAwait(false);
            SessionDeletionTarget target;
            try { target = await Task.Run(() => store.ResolveForDeletion(args.GetString("id") ?? "", path, id, name), token).ConfigureAwait(false); }
            catch (Exception) { throw new SessionDeletionException(PompLanguage.Pick("이 작업 영역의 대화 파일을 확인할 수 없어요.", "Could not validate this workspace's conversation file.")); }
            if (target.SessionId == id || SameSessionPath(target.Path, path))
            {
                await HandleNewSessionAsync(null).ConfigureAwait(false);
                token.ThrowIfCancellationRequested();
                lock (_stateLock)
                    if (_sessionSwitchUncertain || !ReferenceEquals(connection, _epochConnection) || string.IsNullOrEmpty(_info.SessionId) || string.IsNullOrEmpty(_sessionFile) || _info.SessionId == target.SessionId || SameSessionPath(_sessionFile, target.Path))
                        throw new SessionDeletionException(PompLanguage.Pick("새 대화를 확인하지 못해 이전 대화를 삭제하지 않았어요.", "The new conversation was not confirmed; the previous conversation was not deleted."));
            }
            // Reconfirm immediately before recycling; the active engine must no longer own the target.
            var current = await connection.SendCommandAsync(OmpCommands.GetState(), TimeSpan.FromSeconds(10), token).ConfigureAwait(false);
            if (!current.Success || current.Data is not JObject state || string.IsNullOrEmpty(state.GetString("sessionId")) || string.IsNullOrEmpty(state.GetString("sessionFile")) || state.GetString("sessionId") == target.SessionId || SameSessionPath(state.GetString("sessionFile"), target.Path))
                throw new SessionDeletionException(PompLanguage.Pick("활성 대화를 확인하지 못했어요.", "The active conversation could not be confirmed."));
            if (state.GetBool("isStreaming") == true || state.GetBool("hasPendingAsyncWork") == true || state.GetBool("isSettled") == false)
                throw new SessionDeletionException(PompLanguage.Pick("현재 세션이나 백그라운드 작업이 진행 중이에요.", "The current session or background work is still running."));
            token.ThrowIfCancellationRequested();
            lock (_stateLock)
                if (!ReferenceEquals(connection, _epochConnection) || _sessionSwitchUncertain || !string.Equals(root, _workingDirectory, StringComparison.OrdinalIgnoreCase)) throw new OperationCanceledException(token);
            target.Validate();
            await recycle.RecycleAsync(target.Paths, token).ConfigureAwait(false);
            // Once recycled, finish local cleanup even if a newer engine epoch cancels its own work.
            var cleanupFailed = false;
            try { await _sessionReferences.Enqueue(() => Task.Run(() => store.Forget(target.SessionId, target.Path))).ConfigureAwait(false); }
            catch (Exception ex) { cleanupFailed = true; _logger.Warn("Deleted conversation reference cleanup failed: " + ex.Message); }
            foreach (var clean in new Action[] {
                () => new KnowledgeSelection(_environment.DataDirectory, root).Remove(target.SessionId),
                () => _tabs.Remove(root, target.SessionId),
                () => new AgentRunHistory(_environment.DataDirectory, root, _logger).RemoveSession(target.SessionId) })
                try { clean(); } catch (Exception ex) { cleanupFailed = true; _logger.Warn("Deleted conversation data cleanup failed: " + ex.Message); }
            try { await _backgroundAgents.ForgetSessionAsync(root, target.SessionId).ConfigureAwait(false); }
            catch (Exception ex) { cleanupFailed = true; _logger.Warn("Deleted conversation background cleanup failed: " + ex.Message); }
            lock (_stateLock)
            {
                if (_queueSession == target.SessionId) { _chatQueue.Clear(); _queueSession = null; _queueReason = null; _queuePaused = false; }
                if (_partnerSession == target.SessionId) { _partnerSession = null; _partner = new ConversationPartnerPayload(); _partnerMessage = null; _teamInstances.Clear(); _unavailableTeams.Clear(); }
                _recordedSession = "";
            }
            PublishChatQueue(); PublishTabs(); PublishRunHistory();
            if (cleanupFailed) throw new SessionDeletionException(PompLanguage.Pick("대화 파일은 휴지통으로 옮겼지만 일부 대화 자료를 지우지 못했어요.", "The conversation files were recycled, but some conversation data could not be removed."));
        }
    }
}
