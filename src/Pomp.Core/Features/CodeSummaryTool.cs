using System;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Pomp.Hosting;
using Pomp.Permissions;
using Pomp.Tools;
namespace Pomp.Features
{
    internal sealed class CodeSummaryTool : IHostTool
    {
        private readonly ICodeWorkspaceSource source;
        private readonly IPompHostEnvironment environment;
        public CodeSummaryTool(ICodeWorkspaceSource source, IPompHostEnvironment environment) { this.source = source; this.environment = environment; }
        public HostToolDefinition Definition { get; } = new HostToolDefinition {
            Name = "remember_code_summary", Label = "Cache grounded code summary", LoadMode = "discoverable",
            Description = "Store an agent-authored file/folder/workspace explanation locally for later build_codebase_context calls. First inspect source; submit one to eight exact source quotes and current file hashes from the index. Hash/quote checking anchors evidence but does not prove semantic correctness. Parent scopes invalidate when any indexed dependency changes. No source edit or extra provider request. Stores summary text and dependency hash, not evidence/source bodies. Up to 128 summaries/1 MiB per workspace; 2,000 characters each. Workspace scope uses path '.'.",
            Parameters = JObject.Parse("{\"type\":\"object\",\"properties\":{\"kind\":{\"type\":\"string\",\"enum\":[\"file\",\"folder\",\"workspace\"]},\"path\":{\"type\":\"string\",\"maxLength\":4096},\"summary\":{\"type\":\"string\",\"maxLength\":2000},\"evidence\":{\"type\":\"array\",\"minItems\":1,\"maxItems\":8,\"items\":{\"type\":\"object\",\"properties\":{\"path\":{\"type\":\"string\"},\"contentHash\":{\"type\":\"string\"},\"quote\":{\"type\":\"string\",\"minLength\":8,\"maxLength\":1000}},\"required\":[\"path\",\"contentHash\",\"quote\"],\"additionalProperties\":false}}},\"required\":[\"kind\",\"path\",\"summary\",\"evidence\"],\"additionalProperties\":false}") };
        public PermissionKind ResolvePermission(HostToolInvocation invocation) => PermissionKind.None;
        public Task<PermissionRequest?> DescribePermissionAsync(HostToolInvocation invocation, CancellationToken token) => Task.FromResult<PermissionRequest?>(null);
        public async Task<HostToolResult> ExecuteAsync(HostToolInvocation invocation, CancellationToken token)
        {
            try
            {
                var root = await environment.GetWorkspaceRootAsync(token).ConfigureAwait(false);
                var snapshot = await source.CaptureAsync(root, token).ConfigureAwait(false);
                if (!string.Equals(root, await environment.GetWorkspaceRootAsync(token).ConfigureAwait(false), StringComparison.OrdinalIgnoreCase)) return HostToolResult.Error("Workspace changed; request a fresh source capture.");
                var entry = new CodeSummaryCache(environment.DataDirectory, root).Save(snapshot, invocation.Arguments, token);
                return HostToolResult.Text(new JObject { ["stored"] = entry, ["notice"] = "Saved locally as an agent-authored explanation. Evidence matched the captured index, not a proof of semantic correctness. Future reads revalidate source dependencies." }.ToString(Formatting.None));
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex) { return HostToolResult.Error("Code summary unavailable: " + ex.Message); }
        }
    }
}
