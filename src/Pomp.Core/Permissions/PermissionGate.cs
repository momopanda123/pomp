using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Pomp.Diagnostics;
using Pomp.Hosting;
using Pomp.Internal;
using Pomp.Protocol;

namespace Pomp.Permissions
{
    /// <summary>
    /// Decides permission requests (design: 권한 모델). Answers without asking when the policy key is in the persisted
    /// "always allow" list (<see cref="IPompSettings.AlwaysAllowedPolicyKeys"/>, scoped to the workspace it was given in) or was
    /// granted "for this session", or when the approval mode decides it (<see cref="AllowedByMode"/>); otherwise it asks through
    /// <see cref="IPermissionPrompt"/>. Requests that can run arbitrary work (<see cref="IsOnceOnly"/>) are never remembered. A timeout
    /// (<see cref="PermissionRequest.Timeout"/>), cancellation, or a failing prompt all resolve to <see cref="PermissionDecision.Deny"/>
    /// (fail closed).
    /// </summary>
    public sealed class PermissionGate : IPermissionGate
    {
        // omp's debug asks only for actions that launch or drive a program (it classes them as exec), with an adapter it may take from the workspace.
        private static readonly HashSet<string> OnceOnlyOmpTools = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "task", "computer", "browser", "debug" };

        private readonly IPompSettings _settings;
        private readonly IPermissionPrompt _prompt;
        private readonly IPompLogger _logger;
        private readonly UsageMetrics? _metrics;
        private readonly Func<string?>? _workspaceRoot;
        private readonly object _lock = new object();
        private readonly HashSet<string> _sessionGrants = new HashSet<string>(StringComparer.Ordinal);

        // Advanced by ResetSession: an answer to a prompt that started in an earlier omp session must not grant the new one.
        private long _generation;

        /// <param name="settings">The persisted always-allow list.</param>
        /// <param name="prompt">Asks the user.</param>
        /// <param name="logger">Log sink.</param>
        /// <param name="metrics">Counts every outcome per tool (<c>permission.&lt;host|omp&gt;.&lt;tool&gt;.&lt;allowed|denied|remembered|system&gt;</c>).</param>
        /// <param name="workspaceRoot">
        /// The workspace an "always allow" belongs to (<c>&lt;policy key&gt;@&lt;root&gt;</c>). Null keeps unscoped keys (hosts without a
        /// workspace); a provider that returns no usable root makes "always allow" last for the session only.
        /// </param>
        public PermissionGate(IPompSettings settings, IPermissionPrompt prompt, IPompLogger? logger = null, UsageMetrics? metrics = null, Func<string?>? workspaceRoot = null)
        {
            _settings = settings ?? throw new ArgumentNullException(nameof(settings));
            _prompt = prompt ?? throw new ArgumentNullException(nameof(prompt));
            _logger = logger ?? NullPompLogger.Instance;
            _metrics = metrics;
            _workspaceRoot = workspaceRoot;
        }

        /// <summary>
        /// Execution, VCS writes, package changes, debugger control (also omp's <c>debug</c>), test runs, omp subagents (<c>task</c>, which then run without
        /// asking), computer and browser control, and changes to the project policy or to engine configuration (omp reads these from
        /// the workspace and can auto-approve tools or start processes from them, even mid-session): a remembered grant would let any
        /// later call of that tool run unasked, so these are decided for the call at hand only.
        /// </summary>
        public static bool IsOnceOnly(PermissionRequest request)
        {
            if (request == null) return false;
            switch (request.Kind)
            {
                case PermissionKind.ProcessExec:
                case PermissionKind.VcsWrite:
                case PermissionKind.PackageChange:
                case PermissionKind.DebuggerControl:
                case PermissionKind.TestRun:
                    return true;
            }

            if (request.Source == PermissionSource.OmpApproval && OnceOnlyOmpTools.Contains(request.ToolName ?? string.Empty)) return true;
            // The target and the prompt details name the path; a mention that is not the path only makes the prompt stricter.
            return TouchesProtectedConfig(request.Target) || TouchesProtectedConfig(request.Message);
        }

        // The project policy, the project roles omp runs (.pomp/agents, copied into its plugin folder) and POMP's own data (.pomp/local), and what omp
        // (18.4.3 through 18.12.1) reads from a workspace as settings, MCP servers, tools, hooks, plugins, LSP servers (also the tool folders it takes
        // language server programs from) and context files (whose @path includes reach outside the workspace).
        private static readonly System.Text.RegularExpressions.Regex ProtectedConfig = new System.Text.RegularExpressions.Regex(
            @"\.pomp/(?:permissions\.json|agents/|local/)|\.omp/|\.claude/(?:settings|tools/|hooks/|commands/)|\.cursor/settings|\.gemini/(?:settings|commands/)|\.codex/|\.windsurf/|\.opencode/|opencode\.jsonc?|mcp\.json|lsp\.(?:json|ya?ml)" +
            @"|node_modules/\.bin/|\.?venv/(?:bin|Scripts)/|\.env/(?:bin|Scripts)/|vendor/bundle/bin/|(?:^|[/\s])bin/" +
            @"|(?:AGENTS|CLAUDE|GEMINI|SYSTEM|SYSTEM_TEMPLATE)\.md|copilot-instructions\.md",
            System.Text.RegularExpressions.RegexOptions.IgnoreCase | System.Text.RegularExpressions.RegexOptions.CultureInvariant);

        private static bool TouchesProtectedConfig(string? text) => text != null && ProtectedConfig.IsMatch(text.Replace('\\', '/'));

        /// <summary>
        /// The persisted form of <paramref name="policyKey"/>: <c>key@root</c> with the characters the options page splits on escaped.
        /// Null when the workspace is unknown (the grant then lasts for the session only).
        /// </summary>
        private string? PersistentKey(string policyKey)
        {
            if (_workspaceRoot == null) return policyKey;
            string? root;
            try
            {
                root = _workspaceRoot();
                if (string.IsNullOrWhiteSpace(root)) return null;
                root = System.IO.Path.GetFullPath(root).TrimEnd('\\', '/');
            }
            catch (Exception ex) when (ex is ArgumentException || ex is NotSupportedException || ex is System.IO.PathTooLongException || ex is System.Security.SecurityException)
            {
                return null;
            }

            var escaped = new System.Text.StringBuilder(policyKey.Length + root.Length + 1).Append(policyKey).Append('@');
            foreach (var c in root)
            {
                if (c == '%' || c == ';' || c == ',' || c == ' ' || char.IsControl(c)) escaped.Append('%').Append(((int)c).ToString("X2", System.Globalization.CultureInfo.InvariantCulture));
                else escaped.Append(c);
            }

            return escaped.Length <= 1024 ? escaped.ToString() : null;
        }

        /// <summary>
        /// What the approval mode (Tools > Options > Pomp > Agent, or the chat composer's permission-mode chip) answers without asking:
        /// <see cref="OmpApprovalMode.Write"/> allows the host's file, project, breakpoint and bookmark changes (omp's own write tier it
        /// approves itself); <see cref="OmpApprovalMode.Yolo"/> allows every request. Never a prompt a person must answer
        /// (<paramref name="forced"/>: omp's "Reason:" and provider safety prompts) and never omp's computer and browser helpers, whose
        /// input on the user's screen always asks (docs/architecture.md §8.5).
        /// </summary>
        public static bool AllowedByMode(OmpApprovalMode mode, PermissionRequest request, bool forced)
        {
            if (request == null) throw new ArgumentNullException(nameof(request));
            if (forced || mode == OmpApprovalMode.AlwaysAsk) return false;
            if (request.Source == PermissionSource.OmpApproval
                && (string.Equals(request.ToolName, "computer", StringComparison.OrdinalIgnoreCase) || string.Equals(request.ToolName, "browser", StringComparison.OrdinalIgnoreCase)))
                return false;
            if (mode == OmpApprovalMode.Yolo) return true;
            if (IsOnceOnly(request)) return false;
            switch (request.Kind)
            {
                case PermissionKind.FileWrite:
                case PermissionKind.ProjectChange:
                case PermissionKind.BreakpointChange:
                case PermissionKind.BookmarkChange:
                    return true;
                default:
                    return false;
            }
        }

        /// <inheritdoc />
        public async Task<PermissionResponse> RequestAsync(PermissionRequest request, CancellationToken cancellationToken)
        {
            if (request == null)
            {
                throw new ArgumentNullException(nameof(request));
            }

            var remembered = false;
            var response = await DecideAsync(request, () => remembered = true, cancellationToken).ConfigureAwait(false);
            if (_metrics != null)
            {
                var outcome = remembered ? "remembered" : response.SystemReason != null ? "system" : response.IsAllowed ? "allowed" : "denied";
                _metrics.Count("permission." + (request.Source == PermissionSource.HostTool ? "host" : "omp") + "." + request.ToolName + "." + outcome);
            }

            return response;
        }

        private async Task<PermissionResponse> DecideAsync(PermissionRequest request, Action byRule, CancellationToken cancellationToken)
        {
            if (cancellationToken.IsCancellationRequested)
            {
                return PermissionResponse.DeniedBySystem("The permission request was cancelled.");
            }

            // omp prompts a person must answer (a "Reason:" line, provider safety checks) arrive with CanRemember false.
            var forced = !request.CanRemember;
            // The request object travels on to the prompt, so the dialog offers only "allow once" for these.
            if (request.CanRemember && IsOnceOnly(request)) request.CanRemember = false;

            var key = request.PolicyKey;
            var mode = _settings.ApprovalMode;
            if (AllowedByMode(mode, request, forced))
            {
                _logger.Debug("Permission '" + key + "' allowed by the approval mode " + mode + ".");
                byRule();
                return new PermissionResponse(PermissionDecision.Allow);
            }

            var persistentKey = request.CanRemember ? PersistentKey(key) : null;
            if (persistentKey != null && IsAlwaysAllowed(persistentKey))
            {
                _logger.Debug("Permission '" + key + "' allowed by the persisted always-allow list.");
                byRule();
                return new PermissionResponse(PermissionDecision.Allow);
            }

            long generation;
            lock (_lock)
            {
                if (request.CanRemember && _sessionGrants.Contains(key))
                {
                    _logger.Debug("Permission '" + key + "' allowed by a session grant.");
                    byRule();
                    return new PermissionResponse(PermissionDecision.Allow);
                }

                generation = _generation;
            }

            if (cancellationToken.IsCancellationRequested)
            {
                return PermissionResponse.DeniedBySystem("The permission request was cancelled.");
            }

            PermissionResponse? response;
            using (var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken))
            {
                if (request.Timeout is TimeSpan timeout && timeout > TimeSpan.Zero)
                {
                    linked.CancelAfter(timeout);
                }

                try
                {
                    // A prompt that ignores its token must not hold the tool call (and omp) forever: stop waiting on cancellation.
                    var promptTask = _prompt.PromptAsync(request, linked.Token);
                    promptTask.Observe();
                    response = await promptTask.WithCancellation(linked.Token).ConfigureAwait(false);
                    linked.Token.ThrowIfCancellationRequested();
                }
                catch (OperationCanceledException)
                {
                    var timedOut = !cancellationToken.IsCancellationRequested;
                    _logger.Info("Permission '" + key + "' " + (timedOut ? "timed out" : "was cancelled") + "; denied.");
                    return PermissionResponse.DeniedBySystem(timedOut ? "The permission request timed out." : "The permission request was cancelled.");
                }
                catch (Exception ex)
                {
                    _logger.Error("The permission prompt for '" + key + "' failed; denying", ex);
                    return PermissionResponse.DeniedBySystem("The permission prompt failed: " + ex.Message);
                }
            }

            if (response == null)
            {
                return PermissionResponse.DeniedBySystem("The permission prompt gave no answer.");
            }

            if (!request.CanRemember && response.IsAllowed && response.Decision != PermissionDecision.Allow)
            {
                // This prompt may only be answered for the call at hand: never turn it into a rule.
                return new PermissionResponse(PermissionDecision.Allow, response.Note);
            }

            switch (response.Decision)
            {
                case PermissionDecision.AllowForSession:
                    AddSessionGrant(key, generation);
                    break;
                case PermissionDecision.AlwaysAllow:
                    if (persistentKey == null)
                    {
                        // No workspace to scope the rule to: it holds for this session only.
                        AddSessionGrant(key, generation);
                        break;
                    }

                    try
                    {
                        lock (_lock)
                        {
                            if (_generation == generation) _settings.AddAlwaysAllowed(persistentKey);
                        }
                    }
                    catch (Exception ex)
                    {
                        // Persisting failed: keep the grant for this session at least.
                        _logger.Error("Could not persist the always-allow rule '" + key + "'", ex);
                        AddSessionGrant(key, generation);
                    }

                    break;
            }

            return response;
        }

        /// <inheritdoc />
        public void ResetSession()
        {
            lock (_lock)
            {
                _sessionGrants.Clear();
                _generation++;
            }
        }

        public IReadOnlyCollection<string> SessionGrants
        {
            get { lock (_lock) return _sessionGrants.OrderBy(key => key, StringComparer.Ordinal).ToArray(); }
        }

        /// <summary>Revocation also invalidates pending remembered answers; it does not cancel a call already approved.</summary>
        public void Revoke(string policyKey, bool persistent)
        {
            if (string.IsNullOrEmpty(policyKey) || policyKey.Length > 1024 || policyKey.Any(char.IsControl)
                || !(policyKey.StartsWith("host:", StringComparison.Ordinal) || policyKey.StartsWith("omp:", StringComparison.Ordinal)))
                throw new ArgumentException("Invalid permission policy key.", nameof(policyKey));
            lock (_lock)
            {
                _generation++;
                _sessionGrants.Remove(policyKey);
                if (persistent) _settings.RemoveAlwaysAllowed(policyKey);
            }
        }

        /// <summary>Remembers a grant only for the session the prompt was asked in (the answer itself still applies to its call).</summary>
        private void AddSessionGrant(string key, long generation)
        {
            lock (_lock)
            {
                if (_generation == generation)
                {
                    _sessionGrants.Add(key);
                }
                else
                {
                    _logger.Debug("Permission '" + key + "' was answered after its omp session ended; the grant is not kept.");
                }
            }
        }

        private bool IsAlwaysAllowed(string key)
        {
            try
            {
                // Workspace paths compare case-insensitively (Windows); tool names are lowercase anyway.
                var keys = _settings.AlwaysAllowedPolicyKeys;
                return keys != null && keys.Contains(key, StringComparer.OrdinalIgnoreCase);
            }
            catch (Exception ex)
            {
                _logger.Warn("Could not read the always-allow list: " + ex.Message);
                return false;
            }
        }
    }
}
