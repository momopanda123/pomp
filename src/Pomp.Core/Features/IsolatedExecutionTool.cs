using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;
using Pomp.Hosting;
using Pomp.Permissions;
using Pomp.Tools;
namespace Pomp.Features
{
    internal sealed class IsolatedExecutionTool : IPreparedHostTool
    {
        private readonly IWorkspaceFeatureAdapters source;
        private readonly IIsolatedProcessRunner runner;
        private readonly IPompHostEnvironment environment;
        private readonly Func<bool> enabled;
        public IsolatedExecutionTool(IWorkspaceFeatureAdapters source, IIsolatedProcessRunner runner, IPompHostEnvironment environment, Func<bool> enabled) { this.source = source; this.runner = runner; this.environment = environment; this.enabled = enabled; }
        public HostToolDefinition Definition { get; } = new HostToolDefinition {
            Name = "run_sandboxed_command", Label = "Run in experimental Windows AppContainer", LoadMode = "discoverable",
            Description = "Experimental Windows AppContainer command runner, never ordinary-process fallback. Copies up to 20 explicitly named current source snapshots into a temporary per-run AppContainer profile; no live workspace mount. No network capabilities or inherited parent environment/handles; a private kill-on-close process job bounds lifetime. Normal OS AppContainer ACL rules apply; this is not a VM or an absolute filesystem whitelist. Requests one-use approval, including ephemeral security profile creation/deletion. Runtime compatibility/security acceptance still requires Windows validation. SDKs/packages are not installed. Input paths/hash must come from read_buffer. Output is untrusted and exit zero is not semantic verification. No source changes are imported automatically.",
            Parameters = JObject.Parse("{\"type\":\"object\",\"properties\":{\"command\":{\"type\":\"string\",\"maxLength\":8000},\"timeoutSeconds\":{\"type\":\"integer\",\"minimum\":1,\"maximum\":600},\"files\":{\"type\":\"array\",\"maxItems\":20,\"items\":{\"type\":\"object\",\"properties\":{\"path\":{\"type\":\"string\"},\"beforeHash\":{\"type\":\"string\"}},\"required\":[\"path\",\"beforeHash\"],\"additionalProperties\":false}}},\"required\":[\"command\",\"files\"],\"additionalProperties\":false}") };
        public PermissionKind ResolvePermission(HostToolInvocation invocation) => PermissionKind.ProcessExec;
        public Task<PermissionRequest?> DescribePermissionAsync(HostToolInvocation invocation, CancellationToken token) => Task.FromResult<PermissionRequest?>(null);
        public Task<HostToolResult> ExecuteAsync(HostToolInvocation invocation, CancellationToken token) => Task.FromResult(HostToolResult.Error("A frozen one-use sandbox approval is required."));
        public async Task<IPreparedHostToolCall> PrepareAsync(HostToolInvocation invocation, CancellationToken token)
        {
            if (!enabled()) throw new InvalidOperationException("Experimental AppContainer execution is disabled in POMP Options. No process or security profile was created.");
            if (!runner.Available) throw new InvalidOperationException("Windows AppContainer execution is unavailable; ordinary execution will not be substituted.");
            var root = await environment.GetWorkspaceRootAsync(token).ConfigureAwait(false);
            if (!(invocation.Arguments["files"] is JArray rows) || rows.Count > 20) throw new ArgumentException("Choose at most twenty source files.");
            var copies = new List<IsolatedInput>();
            foreach (var row in rows)
            {
                var captured = await source.CaptureForReviewAsync(root, (string?)row["path"] ?? "", (string?)row["beforeHash"] ?? "", "", token).ConfigureAwait(false);
                var prefix = root.Replace('\\', '/').TrimEnd('/') + "/"; var path = captured.Path.Replace('\\', '/');
                if (!path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException("Sandbox input is outside the workspace.");
                copies.Add(new IsolatedInput(path.Substring(prefix.Length), captured.Before));
            }
            var request = new IsolatedRequest((string?)invocation.Arguments["command"] ?? "", copies.AsReadOnly(), (int?)invocation.Arguments["timeoutSeconds"] ?? 120);
            if (!string.Equals(root, await environment.GetWorkspaceRootAsync(token).ConfigureAwait(false), StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException("Workspace changed during capture.");
            return new Prepared(this, request, root);
        }
        private sealed class Prepared : IPreparedHostToolCall
        {
            private readonly IsolatedExecutionTool owner; private readonly IsolatedRequest request; private readonly string root; private int used;
            public Prepared(IsolatedExecutionTool owner, IsolatedRequest request, string root)
            {
                this.owner = owner; this.request = request; this.root = root;
                Permission = new PermissionRequest { Kind = PermissionKind.ProcessExec, Source = PermissionSource.HostTool, ToolName = "run_sandboxed_command", CanRemember = false,
                    Title = PompLanguage.Pick("이 실험적 AppContainer 명령을 실행할까요?", "Run this experimental AppContainer command?"), Target = root,
                    Message = PompLanguage.Pick("입력 파일의 복사본으로 네트워크 없이 PowerShell을 실행해요.\n제한 시간: " + request.TimeoutSeconds + "초\n입력: " + string.Join(", ", request.Files.Select(f => f.Path)) + "\n명령:\n" + request.Command, "Runs PowerShell on copies of the input files without network access.\nTimeout: " + request.TimeoutSeconds + " s\nInputs: " + string.Join(", ", request.Files.Select(f => f.Path)) + "\nCommand:\n" + request.Command) };
            }
            public PermissionRequest Permission { get; }
            public async Task<HostToolResult> ExecuteAsync(CancellationToken token)
            {
                if (Interlocked.Exchange(ref used, 1) != 0) return HostToolResult.Error("Sandbox approval was already consumed.");
                if (!string.Equals(root, await owner.environment.GetWorkspaceRootAsync(token).ConfigureAwait(false), StringComparison.OrdinalIgnoreCase)) return HostToolResult.Error("Workspace changed after approval.");
                if (!owner.enabled()) return HostToolResult.Error("Experimental isolation was disabled after approval.");
                var result = await owner.runner.RunAsync(request, token).ConfigureAwait(false);
                var data = new JObject { ["boundary"] = "windows-appcontainer", ["startedInContainer"] = result.StartedInContainer, ["terminationConfirmed"] = result.TerminationConfirmed,
                    ["exitCode"] = result.ExitCode, ["output"] = result.Output.Length > 65536 ? result.Output.Substring(0,65536) : result.Output, ["outputTruncated"] = result.OutputTruncated || result.Output.Length > 65536, ["error"] = result.Error, ["cleanupWarning"] = result.CleanupWarning,
                    ["notice"] = "Output is untrusted process data. Exit zero does not prove source correctness; no results were applied to the workspace." };
                return new HostToolResult(new[] { HostToolContent.FromText(data.ToString()) }, result.Error.Length > 0 || !result.StartedInContainer || !result.TerminationConfirmed || result.ExitCode != 0 || result.CleanupWarning.Length > 0, data);
            }
            public void Dispose() { }
        }
    }
}
