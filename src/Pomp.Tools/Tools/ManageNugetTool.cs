using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;
using Pomp.Adapters;
using Pomp.Hosting;
using Pomp.Permissions;

namespace Pomp.Tools
{
    /// <summary>NuGet queries and approval-bound package changes through the IDE's NuGet services.</summary>
    internal sealed class ManageNugetTool : HostToolBase, IPreparedHostTool
    {
        internal const int MaxListedPackages = 500;

        private static readonly string[] Actions = { "list", "install", "update", "uninstall" };

        public ManageNugetTool(HostToolContext context)
            : base(context, CreateDefinition())
        {
        }

        private static HostToolDefinition CreateDefinition()
        {
            return new HostToolDefinition
            {
                Name = HostToolNames.ManageNuget,
                Label = "Manage NuGet",
                Description =
                    "Manage the NuGet packages of the solution's projects. Action `list` returns the installed packages (id and version) grouped by project; " +
                    "`packageId` filters the list by case-insensitive substring. " +
                    "`install`, `update` and `uninstall` require approval of one exact project and package. " +
                    "Changes use configured NuGet sources, may download dependencies and execute package/project code. " +
                    "No source configuration is changed. Uninstall keeps dependencies. Partial failures must be inspected before retrying.",
                Parameters = new SchemaBuilder()
                    .Enum("action", "What to do: list, install, update or uninstall.", Actions, required: true)
                    .String("project", "Project name. For list: only this project (default: all projects). For install/update/uninstall: the target project.")
                    .String("packageId", "NuGet package id (for list: substring filter).")
                    .String("version", "Package version for install/update (default: latest stable).")
                    .Build(),
                LoadMode = EssentialLoadMode,
            };
        }

        public override PermissionKind ResolvePermission(HostToolInvocation invocation)
        {
            // An unknown or missing action executes nothing (ExecuteAsync rejects it), so it needs no approval.
            var action = ToolArguments.PeekEnum(invocation?.Arguments, "action", Actions);
            return action == null || action == "list" ? PermissionKind.None : PermissionKind.PackageChange;
        }

        public async Task<IPreparedHostToolCall> PrepareAsync(HostToolInvocation invocation, CancellationToken cancellationToken)
        {
            var options = ReadOptions(invocation.Arguments, out var args);
            if (args.TryGetError(Name, out var invalid)) throw new ToolException(invalid.Content[0].Text!);
            if (options.Action == "list") throw new ToolException("List is a read-only call, not a prepared change.");
            if (!(Adapters.Solution is IPackageChangePreparationAdapter adapter))
                throw new ToolException("This host does not support approval-bound package changes.");
            var guard = await Context.GetPathGuardAsync(cancellationToken).ConfigureAwait(false);
            var plan = await adapter.PreparePackageChangeAsync(options.Project!, options.Action, options.PackageId!, options.Version, cancellationToken).ConfigureAwait(false);
            try
            {
                if (!System.IO.Path.IsPathRooted(plan.ProjectPath) || plan.Action != options.Action ||
                    plan.PackageId != options.PackageId || plan.Version != options.Version)
                    throw new ToolException("Package adapter changed the requested operation.");
                guard.Resolve(plan.ProjectPath);
                var request = NewPermissionRequest(PermissionKind.PackageChange, ChangeTitle(options.Action),
                    PompLanguage.Pick("프로젝트: ", "Project: ") + plan.ProjectName +
                    PompLanguage.Pick("\n프로젝트 파일: ", "\nProject file: ") + plan.ProjectPath +
                    PompLanguage.Pick("\n패키지: ", "\nPackage: ") + plan.PackageId +
                    PompLanguage.Pick("\n현재 버전: ", "\nCurrent version: ") + (plan.CurrentVersion ?? PompLanguage.Pick("설치 안 됨", "not installed")) +
                    (plan.Action == "uninstall" ? string.Empty : PompLanguage.Pick("\n대상 버전: ", "\nRequested version: ") + (plan.Version ?? PompLanguage.Pick("최신 안정 버전", "latest stable"))) +
                    "\n" + PompLanguage.Pick("프로젝트 파일을 바꾸고 패키지 코드를 실행할 수 있어요.", "Can change project files and run package code."), plan.ProjectPath);
                return new PreparedCall(this, plan, request, guard.Root);
            }
            catch { plan.Dispose(); throw; }
        }

        private static string ChangeTitle(string action)
        {
            switch (action)
            {
                case "install": return PompLanguage.Pick("NuGet 패키지를 설치할까요?", "Install the NuGet package?");
                case "update": return PompLanguage.Pick("NuGet 패키지를 업데이트할까요?", "Update the NuGet package?");
                default: return PompLanguage.Pick("NuGet 패키지를 제거할까요?", "Uninstall the NuGet package?");
            }
        }

        public override async Task<PermissionRequest?> DescribePermissionAsync(HostToolInvocation invocation, CancellationToken cancellationToken)
        {
            if (ResolvePermission(invocation) == PermissionKind.None) return null;
            using var prepared = await PrepareAsync(invocation, cancellationToken).ConfigureAwait(false);
            return prepared.Permission;
        }

        protected override async Task<HostToolResult> ExecuteCoreAsync(HostToolInvocation invocation, CancellationToken cancellationToken)
        {
            var options = ReadOptions(invocation.Arguments, out var args);
            if (args.TryGetError(Name, out var invalid)) return invalid;
            var project = options.Project;
            var packageId = options.PackageId;
            if (options.Action != "list") return Fail("A prepared approval is required; direct mutation is disabled.");

            IReadOnlyList<PackageInfo> packages = await Adapters.Solution.ListPackagesAsync(project, cancellationToken).ConfigureAwait(false)
                ?? new List<PackageInfo>();
            var matching = packages
                .Where(p => p != null && (packageId == null || (p.Id ?? string.Empty).IndexOf(packageId, StringComparison.OrdinalIgnoreCase) >= 0))
                .ToList();

            if (matching.Count == 0)
            {
                var none = new StringBuilder("No NuGet packages found");
                if (project != null)
                {
                    none.Append(" in project '").Append(project).Append('\'');
                }

                if (packageId != null)
                {
                    none.Append(" matching '").Append(packageId).Append('\'');
                }

                return HostToolResult.Text(none.Append('.').ToString(), new JObject { ["packages"] = 0, ["projects"] = 0 });
            }

            // Group by project in first-seen order, packages sorted by id.
            var projectOrder = new List<string>();
            var byProject = new Dictionary<string, List<PackageInfo>>(StringComparer.OrdinalIgnoreCase);
            foreach (var package in matching)
            {
                var key = string.IsNullOrWhiteSpace(package.Project) ? "(unknown project)" : package.Project.Trim();
                if (!byProject.TryGetValue(key, out var list))
                {
                    list = new List<PackageInfo>();
                    byProject[key] = list;
                    projectOrder.Add(key);
                }

                list.Add(package);
            }

            var builder = new StringBuilder();
            builder.Append("NuGet packages: ").Append(matching.Count).Append(" in ").Append(projectOrder.Count).Append(projectOrder.Count == 1 ? " project" : " projects");
            var written = 0;
            foreach (var key in projectOrder)
            {
                if (written >= MaxListedPackages)
                {
                    break;
                }

                builder.Append('\n').Append(key);
                foreach (var package in byProject[key].OrderBy(p => p.Id, StringComparer.OrdinalIgnoreCase))
                {
                    if (written >= MaxListedPackages)
                    {
                        break;
                    }

                    builder.Append("\n  ").Append(package.Id);
                    if (!string.IsNullOrWhiteSpace(package.Version))
                    {
                        builder.Append(' ').Append(package.Version!.Trim());
                    }

                    written++;
                }
            }

            if (matching.Count > written)
            {
                builder.Append("\n[").Append(matching.Count - written).Append(" more package(s) not shown; filter with project or packageId]");
            }

            var details = new JObject { ["packages"] = matching.Count, ["projects"] = projectOrder.Count };
            return HostToolResult.Text(builder.ToString(), details);
        }

        private static Options ReadOptions(JObject arguments, out ToolArguments args)
        {
            args = new ToolArguments(arguments, "action", "project", "packageId", "version");
            var action = args.RequiredEnum("action", Actions);
            var project = args.OptionalString("project");
            var id = args.OptionalString("packageId");
            var version = args.OptionalString("version");
            if (action != "list")
            {
                if (project == null) args.AddProblem("'project' is required for changes");
                if (id == null || !System.Text.RegularExpressions.Regex.IsMatch(id, @"\A[A-Za-z0-9_][A-Za-z0-9_.-]{0,99}\z"))
                    args.AddProblem("'packageId' must be a single valid NuGet id of at most 100 characters");
                if (version != null && (version.Length > 100 || version.Any(c => char.IsWhiteSpace(c) || char.IsControl(c))))
                    args.AddProblem("'version' must be a single NuGet version string");
            }
            if ((action == "list" || action == "uninstall") && version != null)
                args.AddProblem("'version' is only valid for install/update");
            return new Options { Action = action ?? string.Empty, Project = project, PackageId = id, Version = version };
        }

        private sealed class Options
        {
            public string Action = string.Empty;
            public string? Project, PackageId, Version;
        }

        private sealed class PreparedCall : IPreparedHostToolCall
        {
            private readonly ManageNugetTool owner;
            private readonly IPreparedPackageChange plan;
            private readonly string root;
            private int consumed;
            public PreparedCall(ManageNugetTool owner, IPreparedPackageChange plan, PermissionRequest permission, string root)
            { this.owner = owner; this.plan = plan; Permission = permission; this.root = root; }
            public PermissionRequest Permission { get; }
            public async Task<HostToolResult> ExecuteAsync(CancellationToken cancellationToken)
            {
                if (Interlocked.Exchange(ref consumed, 1) != 0) return owner.Fail("This package change has already been consumed.");
                cancellationToken.ThrowIfCancellationRequested();
                var guard = await owner.Context.GetPathGuardAsync(cancellationToken).ConfigureAwait(false);
                if (!string.Equals(guard.Root, root, StringComparison.OrdinalIgnoreCase))
                    return owner.Fail("Workspace changed after approval; request approval again.");
                guard.Resolve(plan.ProjectPath);
                var result = await plan.CommitAsync(cancellationToken).ConfigureAwait(false);
                return result.Succeeded
                    ? HostToolResult.Text(plan.Action + " completed: " + result.PackageId + " in " + result.Project +
                        (result.Version == null ? string.Empty : " (" + result.Version + ")"),
                        new JObject { ["succeeded"] = true, ["project"] = result.Project, ["packageId"] = result.PackageId, ["version"] = result.Version })
                    : owner.Fail(result.Error ?? "NuGet did not confirm completion. Inspect project changes before retrying.");
            }
            public void Dispose() { Interlocked.Exchange(ref consumed, 1); plan.Dispose(); }
        }
    }
}
