using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;
using Pomp.Diagnostics;
using Pomp.Features;
using Pomp.Ui;

namespace Pomp.Hosting
{
    public sealed partial class PompConversationHost
    {
        private async Task ReleaseReadyAsync(IConversationSession owner, Channel channel, IUiChannel page)
        {
            // backend/state is advisory: nothing about it may keep the page's ui/ready from its conversation.
            try { await PublishBackendStateAsync().ConfigureAwait(false); }
            catch (Exception ex) { _logger.Debug("backend/state before ui/ready failed: " + ex.Message); }
            lock (_gate)
                if (_disposed || !ReferenceEquals(owner, _active) || !ReferenceEquals(page, _page)) return;
            channel.AllowReady();
        }

        private IConversationSession? UnstartedNative(string root)
        {
            lock (_gate) return _channels.Keys.FirstOrDefault(session => session.BackendId == "omp" && session.ConversationId == null
                && (string.IsNullOrEmpty(session.ConversationRoot) || SameRoot(session.ConversationRoot, root)));
        }

        private async Task HandleBackendAsync(UiMessage message, IConversationSession requester)
        {
            var args = message.Payload ?? new JObject();
            var requestId = (string?)args["requestId"];
            try
            {
                var backend = (string?)args["backend"] ?? requester.BackendId;
                if (backend != "omp" && backend != "antigravity-acp") throw new ArgumentException(PompLanguage.Pick("알 수 없는 대화 연결 방식이에요.", "Unknown conversation backend."));
                CancellationToken epoch;
                lock (_gate)
                {
                    if (_disposed || !_channels.ContainsKey(requester)) throw new InvalidOperationException(PompLanguage.Pick("요청한 대화가 닫혔어요.", "The requesting conversation is closed."));
                    epoch = _workspaceEpoch.Token;
                    _startRequested = true;
                }
                var root = await WorkspaceRootAsync(epoch).ConfigureAwait(false);
                bool first;
                lock (_gate) { epoch.ThrowIfCancellationRequested(); first = _root.Length == 0; }
                // Before the first store read of this root, and never under _gate.
                if (first) await ReconcileOriginAsync(root, epoch).ConfigureAwait(false);
                lock (_gate) { epoch.ThrowIfCancellationRequested(); if (_root.Length == 0) _root = root; }
                if (message.Type == UiMessageTypes.SessionNew && backend == "omp" && requester.BackendId == "omp")
                {
                    Channel channel;
                    lock (_gate)
                    {
                        if (!ReferenceEquals(requester, _active)) throw new InvalidOperationException(PompLanguage.Pick("요청한 대화가 더 이상 선택되어 있지 않아요.", "The requesting conversation is no longer selected."));
                        channel = _channels[requester];
                    }
                    channel.Deliver(message); // Native queue/recovery admission stays native.
                    return;
                }
                IConversationSession? existing = null;
                string? id = null;
                if (message.Type == "conversation/backend")
                {
                    if (requester.BackendId == backend) existing = requester;
                    else if (backend == "omp")
                    {
                        // The Default conversation, never whichever native tab happens to be listed first.
                        var main = _tabs.Read(root).Main;
                        existing = (main == null ? null : Find(root, main)) ?? UnstartedNative(root);
                    }
                    else
                        lock (_gate)
                            existing = _channels.Keys.FirstOrDefault(session => session.BackendId == backend
                                && (SameRoot(session.ConversationRoot, root) || string.IsNullOrEmpty(session.ConversationRoot)));
                    if (existing != null && backend == "omp" && existing.ConversationId == null && string.IsNullOrEmpty(existing.ConversationRoot))
                    {
                        var savedId = _tabs.Read(root).Main;
                        var savedFile = savedId == null ? null : SavedFileOrNull(root, savedId);
                        existing.InitializeConversation(root, savedFile == null ? null : savedId, savedFile, null);
                        HandOrigin(existing, root);
                    }
                    id = existing?.ConversationId;
                    if (existing == null)
                    {
                        var tabs = _tabs.Read(root);
                        id = backend == "omp" ? tabs.Main : tabs.Conversations.LastOrDefault(entry => entry.Backend == backend && entry.Open)?.SessionId;
                    }
                }
                if (existing != null && ReferenceEquals(existing, ActiveSession))
                {
                    Reply(UiMessageTypes.SessionNewResult, requestId, null);
                    await PublishBackendStateAsync().ConfigureAwait(false);
                    try { await existing.EnsureStartedAsync(epoch).ConfigureAwait(false); }
                    catch (Exception ex) when (!(ex is OperationCanceledException)) { _logger.Warn("The selected backend could not start: " + ex.Message); }
                    return;
                }
                await ActivateAsync(root, id, null, requestId, backend, existing,
                    selectBeforeStart: id == null || (backend == "antigravity-acp" && message.Type == "conversation/backend")).ConfigureAwait(false);
                // A predecessor that never got a session has no tab to close it by; its retained sign-in connection would stay.
                if (message.Type == UiMessageTypes.SessionNew && requester.BackendId != "omp" && requester.ConversationId == null
                    && !ReferenceEquals(requester, ActiveSession)) RemoveChild(requester);
            }
            catch (Exception ex) { Reply(UiMessageTypes.SessionNewResult, requestId, ex.Message); }
        }

        private async Task PublishBackendStateAsync()
        {
            IUiChannel? page;
            IConversationSession active;
            CancellationToken epoch;
            lock (_gate) { if (_disposed) return; page = _page; active = _active; epoch = _workspaceEpoch.Token; }
            if (page == null) return;
            string? root = null, executable = null, message = null;
            var tools = false;
            try
            {
                root = await _environment.GetWorkspaceRootAsync(epoch).ConfigureAwait(false);
                if (!IsRealWorkspace(root)) root = null;
                executable = _options.AntigravityCliLocator != null ? _options.AntigravityCliLocator()
                    : AntigravityMcpSetup.FindCliInstallation(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), Environment.GetEnvironmentVariable("PATH"));
                if (string.IsNullOrWhiteSpace(executable) || !File.Exists(executable)) executable = null;
                tools = BundledEngine.Inspect(_environment.ExtensionDirectory).Status == BundledEngineStatus.Verified;
                message = root == null ? PompLanguage.Pick("Antigravity ACP를 쓰려면 솔루션이나 폴더를 열어 주세요.", "Open a real workspace to use Antigravity ACP.")
                    : executable == null ? PompLanguage.Pick("ACP를 쓰려면 공식 Antigravity CLI(agy)를 설치해 주세요.", "Install the official Antigravity CLI (agy) to use ACP.")
                    : !tools ? PompLanguage.Pick("OMP 도구를 쓰려면 확인된 POMP 내장 엔진이 필요해요.", "OMP tools require a verified POMP bundled engine.")
                    : PompLanguage.Pick("공식 agy ACP와 이 대화 전용 OMP 도구를 사용해요. 작업 영역은 격리되지 않으며, 로그인과 승인은 Antigravity가 맡아요.", "Uses official agy ACP and session-scoped OMP tools. The workspace is not a sandbox; Antigravity owns authentication and approvals.");
            }
            catch (OperationCanceledException) { return; }
            catch (Exception ex) { message = ex.Message; }
            var state = new JObject
            {
                ["active"] = active.BackendId,
                ["antigravity"] = new JObject
                {
                    ["installed"] = executable != null, ["canStart"] = executable != null && root != null && tools,
                    ["executablePath"] = executable, ["workspaceRoot"] = root, ["ompToolsAvailable"] = tools, ["message"] = message,
                },
            };
            lock (_gate)
                if (_disposed || epoch.IsCancellationRequested || !ReferenceEquals(page, _page) || !ReferenceEquals(active, _active)) return;
            // Without the epoch token: a cancelled post must not surface as a failure of the caller's ready handshake.
            try { await page.PostAsync(new UiMessage("backend/state", state)).ConfigureAwait(false); }
            catch (Exception ex) { _logger.Debug("backend/state was not delivered: " + ex.Message); }
        }
    }
}
