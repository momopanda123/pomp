using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.VisualStudio;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Shell.Interop;
using Microsoft.VisualStudio.Threading;
using NuGet.VisualStudio;
using Pomp.Adapters;
using Pomp.Tools;
using Pomp.Diagnostics;
using Pomp.VisualStudio.Interop;
using Task = System.Threading.Tasks.Task;

// VS 2019/2022 baseline interoperability uses the supported MEF contract.
// Reads run on a worker thread, never blocking the UI thread (the reason for this deprecation).
#pragma warning disable CS0618 // IVsPackageInstallerServices: maintain VS 16.10 compatibility.
namespace Pomp.VisualStudio.Adapters
{
    /// <summary>NuGet's supported MEF interfaces; project/COM inspection is confined to the UI thread.</summary>
    internal sealed partial class SolutionAdapter : IPackageManagementAdapter, IPackageChangePreparationAdapter
    {
        private readonly SemaphoreSlim packageChanges = new SemaphoreSlim(1, 1);

        public async Task<IReadOnlyList<PackageInfo>> ListPackagesAsync(string? projectName, CancellationToken cancellationToken)
        {
            await context.Jtf.SwitchToMainThreadAsync(cancellationToken);
            var entries = projectName == null
                ? EnumerateProjects(cancellationToken).Where(p => !p.IsSolutionFolder).ToList()
                : new List<ProjectEntry> { FindUniqueProject(projectName, cancellationToken) };
            var service = context.ComponentModel.GetService<IVsPackageInstallerServices>();
            var projects = entries.Select(e => new PackageProject(e.Name, GetPackageProject(e))).ToList();
            // NuGet owns its thread marshaling. Do not enumerate DTE properties on this worker thread.
            // Its synchronous MEF APIs must not block the IDE UI thread.
            await TaskScheduler.Default;
            var result = new List<PackageInfo>();
            foreach (var project in projects)
            {
                cancellationToken.ThrowIfCancellationRequested();
                result.AddRange(ReadPackages(service, project.Project, project.Name));
            }
            return result;
        }

        public async Task<IPreparedPackageChange> PreparePackageChangeAsync(string projectName, string action,
            string packageId, string? version, CancellationToken cancellationToken)
        {
            if (action != "install" && action != "update" && action != "uninstall")
                throw new ArgumentException("Unsupported package operation.");
            if (string.IsNullOrWhiteSpace(packageId) || !System.Text.RegularExpressions.Regex.IsMatch(packageId, @"\A[A-Za-z0-9_][A-Za-z0-9_.-]{0,99}\z"))
                throw new ArgumentException("A valid package id is required.");
            if (action == "uninstall" && version != null) throw new ArgumentException("Uninstall does not accept a version.");
            await context.Jtf.SwitchToMainThreadAsync(cancellationToken);
            if (disposed || eventsCookie == 0) throw new InvalidOperationException("Solution change tracking is unavailable.");
            var location = SolutionLocation.Resolve(context.Solution);
            if (location == null || location.IsFolderWorkspace) throw new ArgumentException("Open a solution with a loaded NuGet project.");
            var entry = FindUniqueProject(projectName, cancellationToken);
            if (string.IsNullOrWhiteSpace(entry.Path)) throw new ArgumentException("The project has no saved file identity.");
            var project = GetPackageProject(entry);
            var path = entry.Path!;
            var root = location.RootDirectory;
            var generation = Interlocked.Read(ref solutionGeneration);
            var identity = GetHierarchyIdentity(entry.Hierarchy);
            var services = context.ComponentModel.GetService<IVsPackageInstallerServices>();
            var installer = context.ComponentModel.GetService<IVsPackageInstaller>();
            var latest = context.ComponentModel.GetService<IVsPackageInstaller2>();
            var uninstaller = context.ComponentModel.GetService<IVsPackageUninstaller>();
            var versions = context.ComponentModel.GetService<IVsSemanticVersionComparer>();
            await TaskScheduler.Default;
            cancellationToken.ThrowIfCancellationRequested();
            new PathGuard(root).Resolve(path);
            if (version != null) versions.Compare(version, version); // Validate exact SemVer before approval, with no download.
            var before = ReadPackages(services, project, entry.Name);
            var current = before.FirstOrDefault(p => string.Equals(p.Id, packageId, StringComparison.OrdinalIgnoreCase))?.Version;
            if ((action == "update" || action == "uninstall") && current == null)
                throw new ArgumentException("The package is not installed in the selected project.");
            return new PreparedPackageChange(this, entry.Name, path, root, generation, identity, project,
                services, installer, latest, uninstaller, versions, action, packageId, version, current, PackageFingerprint(before));
        }

        public async Task<PackageOperationResult> InstallPackageAsync(string projectName, string packageId, string? version, CancellationToken cancellationToken)
        {
            using var plan = await PreparePackageChangeAsync(projectName, "install", packageId, version, cancellationToken).ConfigureAwait(false);
            return await plan.CommitAsync(cancellationToken).ConfigureAwait(false);
        }

        public async Task<PackageOperationResult> UninstallPackageAsync(string projectName, string packageId, CancellationToken cancellationToken)
        {
            using var plan = await PreparePackageChangeAsync(projectName, "uninstall", packageId, null, cancellationToken).ConfigureAwait(false);
            return await plan.CommitAsync(cancellationToken).ConfigureAwait(false);
        }

        private static EnvDTE.Project GetPackageProject(ProjectEntry entry)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            return HierarchyHelper.GetProperty(entry.Hierarchy, VSConstants.VSITEMID_ROOT, __VSHPROPID.VSHPROPID_ExtObject) as EnvDTE.Project
                ?? throw new InvalidOperationException("The project system exposes no NuGet-compatible automation project.");
        }

        private static List<PackageInfo> ReadPackages(IVsPackageInstallerServices services, EnvDTE.Project project, string name) =>
            services.GetInstalledPackages(project).Select(p => new PackageInfo { Project = name, Id = p.Id, Version = p.VersionString }).ToList();

        private static string PackageFingerprint(IEnumerable<PackageInfo> packages) =>
            string.Join("\n", packages.OrderBy(p => p.Id, StringComparer.OrdinalIgnoreCase)
                .Select(p => p.Id.ToUpperInvariant() + "=" + p.Version));

        private sealed class PackageProject
        {
            public PackageProject(string name, EnvDTE.Project project) { Name = name; Project = project; }
            public string Name { get; }
            public EnvDTE.Project Project { get; }
        }

        private sealed class PreparedPackageChange : IPreparedPackageChange
        {
            private readonly SolutionAdapter owner;
            private readonly string root, fingerprint;
            private readonly long generation;
            private readonly IntPtr identity;
            private readonly EnvDTE.Project project;
            private readonly IVsPackageInstallerServices services;
            private readonly IVsPackageInstaller installer;
            private readonly IVsPackageInstaller2 latest;
            private readonly IVsPackageUninstaller uninstaller;
            private readonly IVsSemanticVersionComparer versions;
            private int consumed;
            public PreparedPackageChange(SolutionAdapter owner, string name, string path, string root, long generation,
                IntPtr identity, EnvDTE.Project project, IVsPackageInstallerServices services, IVsPackageInstaller installer,
                IVsPackageInstaller2 latest, IVsPackageUninstaller uninstaller, IVsSemanticVersionComparer versions, string action, string id, string? version,
                string? current, string fingerprint)
            {
                this.owner = owner; ProjectName = name; ProjectPath = path; this.root = root; this.generation = generation;
                this.identity = identity; this.project = project; this.services = services; this.installer = installer;
                this.latest = latest; this.uninstaller = uninstaller; this.versions = versions; Action = action; PackageId = id; Version = version;
                CurrentVersion = current; this.fingerprint = fingerprint;
            }
            public string ProjectName { get; }
            public string ProjectPath { get; }
            public string PackageId { get; }
            public string Action { get; }
            public string? Version { get; }
            public string? CurrentVersion { get; }
            public void Dispose() => Interlocked.Exchange(ref consumed, 1);

            public async Task<PackageOperationResult> CommitAsync(CancellationToken cancellationToken)
            {
                if (Interlocked.Exchange(ref consumed, 1) != 0) throw new InvalidOperationException("Package operation already consumed.");
                await owner.packageChanges.WaitAsync(cancellationToken).ConfigureAwait(false);
                var started = false;
                try
                {
                    await owner.context.Jtf.SwitchToMainThreadAsync(cancellationToken);
                    if (owner.disposed || generation != Interlocked.Read(ref owner.solutionGeneration) || !owner.context.IsCurrentWorkspaceRoot(root))
                        throw new InvalidOperationException("Solution changed after package approval.");
                    var entry = owner.FindUniqueProject(ProjectPath, cancellationToken);
                    if (GetHierarchyIdentity(entry.Hierarchy) != identity || !VsAdapterContext.SamePath(entry.Path, ProjectPath))
                        throw new InvalidOperationException("Project was replaced after package approval.");
                    await TaskScheduler.Default;
                    new PathGuard(root).Resolve(ProjectPath);
                    var before = ReadPackages(services, project, ProjectName);
                    if (PackageFingerprint(before) != fingerprint) throw new InvalidOperationException("Package references changed after approval; review again.");
                    cancellationToken.ThrowIfCancellationRequested();
                    // Package enumeration may yield to a solution close/reload inside NuGet. Do not
                    // enter a mutation if that invalidated the identity checked on the UI thread.
                    if (owner.disposed || generation != Interlocked.Read(ref owner.solutionGeneration))
                        throw new InvalidOperationException("Solution changed while verifying packages; request approval again.");
                    // No synchronous wait on the UI thread. NuGet owns the COM marshalling and restores.
                    // These supported APIs have no CancellationToken: once entered, await the real outcome.
                    started = true;
                    if (Action == "uninstall") uninstaller.UninstallPackage(project, PackageId, removeDependencies: false);
                    else if (Version == null) latest.InstallLatestPackage(null!, project, PackageId, includePrerelease: false, ignoreDependencies: false);
                    else installer.InstallPackage(null!, project, PackageId, Version, ignoreDependencies: false);
                    var after = ReadPackages(services, project, ProjectName);
                    var installed = after.FirstOrDefault(p => string.Equals(p.Id, PackageId, StringComparison.OrdinalIgnoreCase));
                    var success = Action == "uninstall" ? installed == null : installed != null
                        && (Version == null || versions.Compare(installed.Version!, Version) == 0);
                    return new PackageOperationResult { Succeeded = success, Project = ProjectName, PackageId = PackageId,
                        Version = installed?.Version, Error = success ? null : "NuGet returned without the requested package state. Inspect the project before retrying." };
                }
                catch (Exception ex) when (started)
                {
                    owner.context.Log.Warn("NuGet operation failed after starting; project changes may be partial.", ex);
                    return new PackageOperationResult { Project = ProjectName, PackageId = PackageId,
                        Error = "NuGet failed after starting and may have changed project or package files. Inspect them before retrying. " + ex.Message };
                }
                finally { owner.packageChanges.Release(); }
            }
        }
    }
}

#pragma warning restore CS0618
