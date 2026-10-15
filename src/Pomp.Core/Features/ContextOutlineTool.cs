using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Pomp.Hosting;
using Pomp.Permissions;
using Pomp.Tools;
namespace Pomp.Features
{
    internal sealed class ContextOutlineTool : IHostTool
    {
        private readonly ICodeWorkspaceSource source;
        private readonly IPompHostEnvironment environment;
        private readonly Func<CancellationToken, Task<ContextBudget?>>? readBudget;
        public ContextOutlineTool(ICodeWorkspaceSource source, IPompHostEnvironment environment, Func<CancellationToken, Task<ContextBudget?>>? readBudget = null) { this.source = source; this.environment = environment; this.readBudget = readBudget; }
        public HostToolDefinition Definition { get; } = new HostToolDefinition {
            Name = "build_codebase_context", Label = "Build bounded codebase context", LoadMode = "essential",
            Description = "Build a bounded folder/file/symbol outline with selected exact source excerpts from the live source index. Prioritize explicit full paths and a query. Reports omissions and partial excerpts; structural compression is not a model summary or whole-codebase coverage guarantee. Also includes hash-current, explicitly agent-authored hierarchical cached explanations when available; absent/stale entries are not invented. remember_code_summary can populate them after source inspection. When a current context estimate is available, automatically reduces the body allowance with reply/envelope reserve or refuses a nearly full context. Character budget is not exact tokens. No model call or source mutation.",
            Parameters = JObject.Parse("{\"type\":\"object\",\"properties\":{\"query\":{\"type\":\"string\",\"maxLength\":2000},\"priorityPaths\":{\"type\":\"array\",\"maxItems\":20,\"items\":{\"type\":\"string\",\"maxLength\":1024}},\"characterBudget\":{\"type\":\"integer\",\"minimum\":4096,\"maximum\":128000}},\"additionalProperties\":false}") };
        public PermissionKind ResolvePermission(HostToolInvocation invocation) => PermissionKind.None;
        public Task<PermissionRequest?> DescribePermissionAsync(HostToolInvocation invocation, CancellationToken token) => Task.FromResult<PermissionRequest?>(null);
        public async Task<HostToolResult> ExecuteAsync(HostToolInvocation invocation, CancellationToken token)
        {
            try {
                var root = await environment.GetWorkspaceRootAsync(token).ConfigureAwait(false);
                var snapshot = await source.CaptureAsync(root, token).ConfigureAwait(false);
                var paths = (invocation.Arguments["priorityPaths"] as JArray)?.Select(p => (string?)p ?? "").ToArray() ?? Array.Empty<string>();
                var budget = (int?)invocation.Arguments["characterBudget"] ?? 24000;
                if (budget < 4096 || budget > 128000) throw new ArgumentOutOfRangeException("characterBudget");
                var requested = budget;
                var estimate = readBudget == null ? null : await readBudget(token).ConfigureAwait(false);
                if (estimate != null) budget = estimate.LimitCharacters(budget);
                var budgetInfo = estimate?.Describe(requested, budget) ?? new JObject { ["mode"] = "unavailable", ["limitations"] = "Context estimate unavailable; only the requested serialized-character limit is enforced. No full-request token fit is promised." };
                JObject? summaries = null; string? cacheWarning = null;
                try { summaries = new CodeSummaryCache(environment.DataDirectory, root).Context(snapshot, Math.Min(12000, budget / 3), token); }
                catch (OperationCanceledException) { throw; }
                catch (Exception ex) { cacheWarning = "Summary cache unavailable; structural source remains available. " + ex.Message.Substring(0, Math.Min(300, ex.Message.Length)); }
                var reserve = (summaries == null ? 512 : summaries.ToString(Formatting.None).Length + 100) + budgetInfo.ToString(Formatting.None).Length + 64;
                var outlineBudget = Math.Max(4096, budget - reserve);
                var output = ContextOutline.Build(snapshot, (string?)invocation.Arguments["query"] ?? "", paths, outlineBudget, token);
                output["characterBudget"] = budget;
                output["contextBudget"] = budgetInfo;
                if (summaries != null) output["cachedExplanations"] = summaries;
                if (cacheWarning != null) output["cacheWarning"] = cacheWarning;
                // At the minimum budget, prefer exact source over optional cached prose.
                if (output.ToString(Formatting.None).Length > budget) output.Remove("cachedExplanations");
                if (output.ToString(Formatting.None).Length > budget) output.Remove("cacheWarning");
                if (output.ToString(Formatting.None).Length > budget && output["excerpts"] is JArray excerpts)
                    while (output.ToString(Formatting.None).Length > budget && excerpts.Count > 0) excerpts.Last!.Remove();
                output["includedExcerptCount"] = (output["excerpts"] as JArray)?.Count ?? 0;
                if (output.ToString(Formatting.None).Length > budget) throw new InvalidOperationException("Context output exceeds its budget.");
                if (!string.Equals(root, await environment.GetWorkspaceRootAsync(token).ConfigureAwait(false), StringComparison.OrdinalIgnoreCase)) return HostToolResult.Error("Workspace changed during context capture. Retry in the current solution.");
                return HostToolResult.Text(output.ToString(Formatting.None), output);
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex) { return HostToolResult.Error("Codebase context unavailable: " + ex.Message); }
        }
    }
}
