using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;
using Pomp.Bridge;
using Pomp.Diagnostics;
using Pomp.Features;
using Pomp.Internal;
using Pomp.Ui;

namespace Pomp.Hosting
{
    /// <summary>Workspace trust in the chat (docs/architecture.md "작업 영역 신뢰"): show what omp would run, trust it, open a listed file.</summary>
    public sealed partial class PompSession
    {
        private readonly SemaphoreSlim _trustCommands = new SemaphoreSlim(1, 1);

        private void PublishTrust(WorkspaceTrustStatePayload state, string? requestId = null)
        {
            state.RequestId = requestId;
            _ui.Post(UiMessageTypes.WorkspaceTrustState, state, UiDelivery.State);
        }

        /// <summary>workspace/trust: refresh (re-scan), trust (the fingerprint the user saw; then omp starts), open (a listed file in VS).</summary>
        private async Task HandleWorkspaceTrustAsync(JObject args)
        {
            await _trustCommands.WaitAsync(_disposeCts.Token).ConfigureAwait(false);
            try
            {
                var requestId = args.GetString("requestId");
                var action = args.GetString("action") ?? "refresh";
                var extensions = _settings.LoadDiscoveredOmpExtensions;
                // The root omp would start in now (the same resolution as the launch), not the one of a previous omp.
                var root = await ResolveWorkspaceRootAsync(Path.GetFullPath(_environment.DataDirectory), _disposeCts.Token).ConfigureAwait(false);
                string? error = null;
                var start = false;
                try
                {
                    if (action != "refresh" && !string.Equals(NormalizePath(args.GetString("root")), NormalizePath(root), StringComparison.OrdinalIgnoreCase))
                        throw new InvalidOperationException(PompLanguage.Pick("작업 영역이 바뀌었어요.", "The workspace changed."));
                    if (action == "trust")
                    {
                        _workspaceTrust.Trust(root, args.GetString("fingerprint"), extensions);
                        // Start omp if trust is what kept it from starting (a running omp already belongs to a trusted set).
                        start = _bridge.State == BridgeState.Failed || _bridge.State == BridgeState.Stopped;
                    }
                    else if (action == "open")
                    {
                        var file = _workspaceTrust.FileOf(root, args.GetString("path") ?? string.Empty, extensions)
                            ?? throw new FileNotFoundException(PompLanguage.Pick("목록에 없는 파일이에요.", "The file is not in the list."));
                        var opened = await AdapterGuard.TryRunAsync(token => _adapters.Editor.OpenDocumentAsync(file, null, token), _options.AdapterTimeout, _logger, "open engine configuration", _disposeCts.Token).ConfigureAwait(false);
                        if (!opened.Succeeded || !opened.Value) throw new InvalidOperationException(PompLanguage.Pick("파일을 VS 편집기에서 열지 못했어요.", "Could not open the file in the VS editor."));
                    }
                    else if (action != "refresh")
                    {
                        throw new ArgumentException(PompLanguage.Pick("알 수 없는 신뢰 동작이에요.", "Unknown trust action."));
                    }
                }
                catch (Exception ex) when (!(ex is OperationCanceledException))
                {
                    error = ex.Message;
                }

                var state = _workspaceTrust.Evaluate(root, extensions);
                state.Error = error;
                PublishTrust(state, requestId);
                if (start)
                {
                    _logger.Info("The workspace configuration was trusted; starting omp.");
                    // Not awaited here: a start can take a while (handshake, retries) and must not hold trust requests.
                    RestartAsync(_disposeCts.Token).Forget(_logger, "start after trust");
                }
            }
            finally
            {
                _trustCommands.Release();
            }
        }

        /// <summary>
        /// Tools › 권한: forgets the trust of a workspace. For the workspace omp runs in now, omp restarts, so the configuration it loaded
        /// stops applying and the chat asks again.
        /// </summary>
        private void RevokeWorkspaceTrust(string root)
        {
            if (string.IsNullOrWhiteSpace(root)) throw new ArgumentException(PompLanguage.Pick("작업 영역이 비어 있어요.", "The workspace is empty."));
            if (!_workspaceTrust.Revoke(root)) return;
            string current;
            lock (_stateLock) current = _workingDirectory;
            if (string.Equals(NormalizePath(root), NormalizePath(current), StringComparison.OrdinalIgnoreCase) && _bridge.State != BridgeState.Stopped)
                RestartAsync(_disposeCts.Token).Forget(_logger, "restart after revoking trust");
        }

        /// <summary>The trusted workspaces for the permission/rules reply.</summary>
        private JArray TrustedWorkspacesView()
        {
            var list = new JArray();
            foreach (var entry in _workspaceTrust.List()) list.Add(new JObject { ["root"] = entry.Root, ["trustedUtc"] = entry.TrustedUtc, ["files"] = entry.Files });
            return list;
        }
    }
}
