using System;
using System.Collections.Generic;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;
using Pomp.Adapters;

namespace Pomp.Tools
{
    /// <summary>
    /// <c>query_solution</c>: the solution / project / folder / file tree as an indented list. Paths are shown relative to
    /// the workspace root; a file is printed by name only when it lives directly in the folder or project directory
    /// printed above it, otherwise with its full relative path.
    /// </summary>
    internal sealed class QuerySolutionTool : HostToolBase
    {
        internal const int DefaultDepth = 3;
        internal const int MaxDepth = 10;
        internal const int DefaultMaxResults = 300;
        internal const int MaxMaxResults = 2000;

        private const int RenderDepthLimit = 50;

        public QuerySolutionTool(HostToolContext context)
            : base(context, CreateDefinition())
        {
        }

        private static HostToolDefinition CreateDefinition()
        {
            return new HostToolDefinition
            {
                Name = HostToolNames.QuerySolution,
                Label = "Query Solution",
                Description =
                    "List the open Visual Studio solution as an indented tree of projects, folders and files, with paths relative to the workspace root. " +
                    "Use it to discover project names and file locations (for read_buffer, build_solution, ...). " +
                    "Project lines are `project Name (kind, frameworks) path`; folder lines show the folder path followed by `/`; " +
                    "a file line shows just the file name when the file sits directly in the folder or project directory printed above it, otherwise its full relative path. " +
                    "`pattern` narrows the tree to matching items (case-insensitive substring, or glob with * and ?).",
                Parameters = new SchemaBuilder()
                    .String("pattern", "Case-insensitive substring or glob (* and ?) matched against item names and paths. Default: everything.")
                    .Integer("depth", "Tree depth below the solution (1 = projects only).", 1, MaxDepth, DefaultDepth)
                    .Integer("maxResults", "Maximum number of tree items returned.", 1, MaxMaxResults, DefaultMaxResults)
                    .Build(),
                LoadMode = EssentialLoadMode,
            };
        }

        protected override async Task<HostToolResult> ExecuteCoreAsync(HostToolInvocation invocation, CancellationToken cancellationToken)
        {
            var args = new ToolArguments(invocation.Arguments, "pattern", "depth", "maxResults");
            var pattern = args.OptionalString("pattern");
            var depth = args.OptionalInt("depth", 1, MaxDepth) ?? DefaultDepth;
            var maxResults = args.OptionalInt("maxResults", 1, MaxMaxResults) ?? DefaultMaxResults;
            if (args.TryGetError(Name, out var invalid))
            {
                return invalid;
            }

            var solution = await Adapters.Solution.GetSolutionInfoAsync(cancellationToken).ConfigureAwait(false);
            if (solution == null)
            {
                return HostToolResult.Text("No solution or folder is open.");
            }

            IReadOnlyList<SolutionNode> nodes = await Adapters.Solution
                .QueryAsync(new SolutionQuery { Pattern = pattern, Depth = depth, MaxResults = maxResults }, cancellationToken)
                .ConfigureAwait(false) ?? new List<SolutionNode>();

            var guard = await Context.TryGetPathGuardAsync(cancellationToken).ConfigureAwait(false);
            var display = HostToolContext.DisplayFunc(guard);

            var builder = new StringBuilder();
            builder.Append("Solution: ").Append(string.IsNullOrWhiteSpace(solution.Name) ? "(unnamed)" : solution.Name!.Trim());
            builder.Append(string.IsNullOrWhiteSpace(solution.SolutionPath) ? " (folder workspace)" : " (" + display(solution.SolutionPath!) + ")");
            var root = !string.IsNullOrWhiteSpace(solution.RootDirectory) ? solution.RootDirectory : guard?.Root;
            if (!string.IsNullOrWhiteSpace(root))
            {
                builder.Append("\nRoot: ").Append(root);
            }

            var total = 0;
            foreach (var node in nodes)
            {
                total += CountNodes(node, 0);
            }

            if (total == 0)
            {
                builder.Append(pattern != null ? "\nNo items match '" + pattern + "'." : "\nThe solution contains no items.");
            }
            else
            {
                var state = new RenderState(builder, display, maxResults, solution.Projects);
                foreach (var node in nodes)
                {
                    Render(node, 0, "", state);
                }

                if (total > maxResults)
                {
                    builder.Append("\n[").Append(total - maxResults).Append(" more item(s) not shown (limit ").Append(maxResults).Append("); narrow with pattern or depth]");
                }
                else if (total == maxResults)
                {
                    builder.Append("\n[maxResults limit (").Append(maxResults).Append(") reached; more items may exist - narrow with pattern or depth]");
                }
            }

            var details = new JObject
            {
                ["solution"] = solution.Name,
                ["projects"] = solution.Projects?.Count ?? 0,
                ["items"] = total,
                ["shown"] = Math.Min(total, maxResults),
            };
            return HostToolResult.Text(builder.ToString(), details);
        }

        private static bool IsKind(SolutionNode node, string kind) => string.Equals(node.Kind, kind, StringComparison.OrdinalIgnoreCase);

        private static int CountNodes(SolutionNode node, int level)
        {
            if (level > RenderDepthLimit)
            {
                return 0;
            }

            var count = IsKind(node, "solution") ? 0 : 1;
            if (node.Children != null)
            {
                foreach (var child in node.Children)
                {
                    count += CountNodes(child, level + 1);
                }
            }

            return count;
        }

        /// <param name="parentDir">Root-relative directory of the line above (empty = the root); null when unknown.</param>
        private static void Render(SolutionNode node, int level, string? parentDir, RenderState state)
        {
            if (level > RenderDepthLimit)
            {
                return;
            }

            string? childDir = parentDir;
            var childLevel = level + 1;

            if (IsKind(node, "solution"))
            {
                // The header already describes the solution; its children start at the left margin.
                childLevel = level;
                childDir = string.Empty;
            }
            else
            {
                if (state.Remaining <= 0)
                {
                    return;
                }

                state.Remaining--;
                state.Builder.Append('\n').Append(' ', level * 2);
                childDir = AppendNode(node, parentDir, state);
            }

            if (node.Children != null)
            {
                foreach (var child in node.Children)
                {
                    Render(child, childLevel, childDir, state);
                }
            }
        }

        /// <summary>Appends the text of one tree line and returns the directory its children live in (null when unknown).</summary>
        private static string? AppendNode(SolutionNode node, string? parentDir, RenderState state)
        {
            var name = string.IsNullOrWhiteSpace(node.Name) ? "(unnamed)" : node.Name.Trim();
            var path = string.IsNullOrWhiteSpace(node.Path) ? null : state.Display(node.Path!.Trim());

            if (IsKind(node, "project"))
            {
                state.Builder.Append("project ").Append(name);
                if (state.Projects.TryGetValue(name, out var project))
                {
                    var facts = new List<string>();
                    if (!string.IsNullOrWhiteSpace(project.Kind))
                    {
                        facts.Add(project.Kind!.Trim());
                    }

                    if (!string.IsNullOrWhiteSpace(project.TargetFrameworks))
                    {
                        facts.Add(project.TargetFrameworks!.Trim());
                    }

                    if (facts.Count > 0)
                    {
                        state.Builder.Append(" (").Append(string.Join(", ", facts)).Append(')');
                    }
                }

                if (path != null)
                {
                    state.Builder.Append(' ').Append(path);
                    return DirectoryOf(path);
                }

                return null;
            }

            if (IsKind(node, "folder"))
            {
                if (path != null)
                {
                    state.Builder.Append(path == "." ? string.Empty : path).Append('/');
                    return path == "." ? string.Empty : path;
                }

                state.Builder.Append(name).Append('/');
                return parentDir == null ? null : Combine(parentDir, name);
            }

            if (IsKind(node, "file"))
            {
                var implied = parentDir != null && path != null && string.Equals(Combine(parentDir, name), path, StringComparison.OrdinalIgnoreCase);
                state.Builder.Append(path == null || implied ? name : path);
                return null;
            }

            state.Builder.Append(path ?? name).Append(" (").Append(string.IsNullOrWhiteSpace(node.Kind) ? "item" : node.Kind).Append(')');
            return null;
        }

        private static string Combine(string directory, string name) => directory.Length == 0 || directory == "." ? name : directory + "/" + name;

        private static string DirectoryOf(string displayPath)
        {
            var slash = displayPath.LastIndexOf('/');
            return slash <= 0 ? string.Empty : displayPath.Substring(0, slash);
        }

        private sealed class RenderState
        {
            public RenderState(StringBuilder builder, Func<string, string> display, int remaining, List<ProjectInfo>? projects)
            {
                Builder = builder;
                Display = display;
                Remaining = remaining;
                Projects = new Dictionary<string, ProjectInfo>(StringComparer.OrdinalIgnoreCase);
                if (projects != null)
                {
                    foreach (var project in projects)
                    {
                        if (!string.IsNullOrWhiteSpace(project.Name) && !Projects.ContainsKey(project.Name.Trim()))
                        {
                            Projects[project.Name.Trim()] = project;
                        }
                    }
                }
            }

            public StringBuilder Builder { get; }

            public Func<string, string> Display { get; }

            public int Remaining { get; set; }

            public Dictionary<string, ProjectInfo> Projects { get; }
        }
    }
}
