using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using System.Xml;
using System.Xml.Linq;
using Microsoft.VisualStudio;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Shell.Interop;
using Pomp.Adapters;
using Task = System.Threading.Tasks.Task;

namespace Pomp.VisualStudio.Adapters
{
    /// <summary>Loaded-project discovery through public VS APIs; .NET test platform execution outside the UI thread.</summary>
    internal sealed class TestAdapter : ITestAdapter
    {
        private const string LegacyTestProjectGuid = "{3AC096D0-A1C2-E12C-1390-A8335801FDAB}";
        private readonly VsAdapterContext context;
        private readonly SemaphoreSlim operationLock = new SemaphoreSlim(1, 1);

        public TestAdapter(VsAdapterContext context)
        {
            this.context = context;
        }

        public async Task<TestListResult> ListTestsAsync(string? projectName, CancellationToken cancellationToken)
        {
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            deadline.CancelAfter(TimeSpan.FromMinutes(10));
            var listCancellation = deadline.Token;
            var acquired = false;
            var result = new TestListResult();
            var timer = Stopwatch.StartNew();
            try
            {
                await operationLock.WaitAsync(listCancellation).ConfigureAwait(false);
                acquired = true;
                var discovery = await DiscoverAsync(projectName, listCancellation).ConfigureAwait(false);
                result.Errors.AddRange(discovery.Errors);
                if (discovery.Projects.Count == 0)
                {
                    return result;
                }

                var runner = new TestRunner(TestRunner.FindDotnet(), discovery.VstestPath);
                foreach (var project in discovery.Projects)
                {
                    listCancellation.ThrowIfCancellationRequested();
                    var busy = await BuildBusyErrorAsync(listCancellation).ConfigureAwait(false);
                    if (busy != null)
                    {
                        result.Errors.Add(busy);
                        break;
                    }

                    var remaining = TimeSpan.FromMinutes(10) - timer.Elapsed;
                    if (remaining <= TimeSpan.Zero)
                    {
                        result.Errors.Add("Test discovery reached its 600-second time limit.");
                        break;
                    }

                    var listed = await runner.ListAsync(project, remaining, listCancellation).ConfigureAwait(false);
                    result.Tests.AddRange(listed.Tests.Take(Math.Max(0, 2000 - result.Tests.Count)));
                    result.Errors.AddRange(listed.Errors.Take(Math.Max(0, 100 - result.Errors.Count)));
                    if (result.Tests.Count >= 2000)
                    {
                        result.Errors.Add("The solution test list is limited to 2000 tests; select a project for further discovery.");
                        break;
                    }
                }

                return result;
            }
            catch (OperationCanceledException) when (listCancellation.IsCancellationRequested)
            {
                result.Errors.Add(cancellationToken.IsCancellationRequested ? "Test discovery was cancelled." : "Test discovery reached its 600-second time limit.");
                return result;
            }
            finally
            {
                if (acquired)
                {
                    operationLock.Release();
                }
            }
        }

        public async Task<TestRunResult> RunTestsAsync(TestRunRequest request, CancellationToken cancellationToken)
        {
            if (request == null || request.TimeoutSeconds < 10 || request.TimeoutSeconds > 3600)
            {
                return new TestRunResult { Error = "A test run requires a timeout between 10 and 3600 seconds." };
            }

            if ((request.Filter != null && (request.Filter.Length > 4096 || request.Filter.Any(char.IsControl)))
                || (request.ProjectName != null && (request.ProjectName.Length > 4096 || request.ProjectName.Any(char.IsControl))))
            {
                return new TestRunResult { Error = "Test project and filter arguments must be at most 4096 characters and contain no control characters." };
            }

            // Freeze caller-owned inputs before any await.
            var projectName = request.ProjectName;
            var filter = request.Filter;
            var timeout = TimeSpan.FromSeconds(request.TimeoutSeconds);
            var acquired = false;
            var result = new TestRunResult();
            var timer = Stopwatch.StartNew();
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            deadline.CancelAfter(timeout);
            var runCancellation = deadline.Token;
            try
            {
                await operationLock.WaitAsync(runCancellation).ConfigureAwait(false);
                acquired = true;
                var discovery = await DiscoverAsync(projectName, runCancellation).ConfigureAwait(false);
                foreach (var error in discovery.Errors)
                {
                    result.Error = TestRunnerText.JoinError(result.Error, error);
                }

                var runner = new TestRunner(TestRunner.FindDotnet(), discovery.VstestPath);
                var output = new TestOutputTail();
                foreach (var project in discovery.Projects)
                {
                    var remaining = timeout - timer.Elapsed;
                    if (remaining <= TimeSpan.Zero || runCancellation.IsCancellationRequested)
                    {
                        result.TimedOut = true;
                        result.Error = TestRunnerText.JoinError(result.Error, "The test run timed out or was cancelled.");
                        break;
                    }

                    var busy = await BuildBusyErrorAsync(runCancellation).ConfigureAwait(false);
                    if (busy != null)
                    {
                        result.Error = TestRunnerText.JoinError(result.Error, busy);
                        break;
                    }

                    var run = await runner.RunAsync(project, filter, remaining, runCancellation).ConfigureAwait(false);
                    TrxParser.Merge(result, run);
                    output.AddText(run.OutputTail);
                    result.OutputTail = output.Text;
                    result.TimedOut |= run.TimedOut;
                    if (run.TimedOut)
                    {
                        break;
                    }
                }
            }
            catch (OperationCanceledException) when (runCancellation.IsCancellationRequested)
            {
                result.TimedOut = true;
                result.Error = TestRunnerText.JoinError(result.Error, cancellationToken.IsCancellationRequested
                    ? "The test run was cancelled." : "The test run timed out.");
            }
            finally
            {
                if (acquired)
                {
                    operationLock.Release();
                }
            }

            return result;
        }

        private async Task<ProjectDiscovery> DiscoverAsync(string? projectName, CancellationToken cancellationToken)
        {
            await context.Jtf.SwitchToMainThreadAsync(cancellationToken);
            var discovery = new ProjectDiscovery();
            var busy = BuildBusyError();
            if (busy != null)
            {
                discovery.Errors.Add(busy);
                return discovery;
            }

            discovery.VstestPath = GetVstestPath();
            var projects = CaptureProjects(cancellationToken);
            if (!string.IsNullOrWhiteSpace(projectName))
            {
                var wanted = projectName!.Trim();
                // Exact unique name/path takes precedence over a short caption; duplicate short captions are an error.
                var exact = projects.Where(p => string.Equals(p.UniqueName, wanted, StringComparison.OrdinalIgnoreCase)
                    || (Path.IsPathRooted(wanted) && VsAdapterContext.SamePath(p.Path, wanted))).ToList();
                var matching = exact.Count > 0 ? exact : projects.Where(p => string.Equals(p.Caption, wanted, StringComparison.OrdinalIgnoreCase)).ToList();
                if (matching.Count != 1)
                {
                    discovery.Errors.Add(matching.Count == 0
                        ? "The loaded project '" + wanted + "' was not found. Use a unique project name or full project path."
                        : "The project name '" + wanted + "' is ambiguous. Use a unique project name or full project path.");
                    return discovery;
                }

                projects = matching;
            }

            // Read XML and packages.config on a worker; all IVs* properties and reference-node metadata above were
            // captured on the UI thread. Snapshot objects contain strings/booleans only, never live hierarchies.
            await Task.Run(() => CompleteSnapshots(projects, discovery, cancellationToken)).ConfigureAwait(false);
            if (discovery.Projects.Count == 0 && discovery.Errors.Count == 0)
            {
                discovery.Errors.Add(projectName == null ? "No loaded test projects were found in the solution."
                    : "The selected loaded project is not a test project (IsTestProject, Microsoft.NET.Test.Sdk or legacy MSTest project type was not found).");
            }

            return discovery;
        }

        private List<ProjectProperties> CaptureProjects(CancellationToken cancellationToken)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            var projects = new List<ProjectProperties>();
            var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var filter = Guid.Empty;
            ErrorHandler.ThrowOnFailure(context.Solution.GetProjectEnum((uint)__VSENUMPROJFLAGS.EPF_LOADEDINSOLUTION, ref filter, out var enumeration));
            if (enumeration == null)
            {
                return projects;
            }

            var buffer = new IVsHierarchy[1];
            while (enumeration.Next(1, buffer, out var fetched) == VSConstants.S_OK && fetched == 1)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var hierarchy = buffer[0];
                if (!(hierarchy is IVsProject project))
                {
                    continue;
                }

                if (ErrorHandler.Failed(project.GetMkDocument(VSConstants.VSITEMID_ROOT, out var path)) || !VsAdapterContext.IsFilePath(path))
                {
                    continue;
                }

                path = Path.GetFullPath(path);
                if (!paths.Add(path))
                {
                    continue;
                }

                string? caption = null;
                if (ErrorHandler.Succeeded(hierarchy.GetProperty(VSConstants.VSITEMID_ROOT, (int)__VSHPROPID.VSHPROPID_Name, out var name)))
                {
                    caption = name as string;
                }

                var unique = ErrorHandler.Succeeded(context.Solution.GetUniqueNameOfProject(hierarchy, out var uniqueName)) ? uniqueName : null;
                var config = ActiveConfiguration(hierarchy);
                var isTest = string.Equals(ReadProperty(hierarchy, "IsTestProject", config), "true", StringComparison.OrdinalIgnoreCase);
                var typeGuids = ReadProperty(hierarchy, "ProjectTypeGuids", config);
                if (hierarchy is IVsAggregatableProject aggregate && ErrorHandler.Succeeded(aggregate.GetAggregateProjectTypeGuids(out var guids)))
                {
                    typeGuids = (typeGuids ?? string.Empty) + ";" + guids;
                }

                isTest |= (typeGuids ?? string.Empty).IndexOf(LegacyTestProjectGuid, StringComparison.OrdinalIgnoreCase) >= 0;
                var sdkStyle = string.Equals(ReadProperty(hierarchy, "UsingMicrosoftNETSdk", config), "true", StringComparison.OrdinalIgnoreCase)
                    || !string.IsNullOrWhiteSpace(ReadProperty(hierarchy, "MSBuildProjectSdk", config));
                var output = ReadProperty(hierarchy, "TargetPath", config);
                if (string.IsNullOrWhiteSpace(output))
                {
                    var folder = ReadProperty(hierarchy, "OutDir", config) ?? ReadProperty(hierarchy, "OutputPath", config);
                    var file = ReadProperty(hierarchy, "TargetFileName", config);
                    if (string.IsNullOrWhiteSpace(file))
                    {
                        var assembly = ReadProperty(hierarchy, "AssemblyName", config);
                        if (!string.IsNullOrWhiteSpace(assembly))
                        {
                            file = assembly + (string.Equals(ReadProperty(hierarchy, "OutputType", config), "Exe", StringComparison.OrdinalIgnoreCase) ? ".exe" : ".dll");
                        }
                    }

                    output = !string.IsNullOrWhiteSpace(folder) && !string.IsNullOrWhiteSpace(file) ? Path.Combine(folder, file) : null;
                }

                projects.Add(new ProjectProperties
                {
                    Path = Path.GetFullPath(path),
                    Caption = caption ?? Path.GetFileNameWithoutExtension(path),
                    UniqueName = string.IsNullOrWhiteSpace(unique) ? path : unique!,
                    IsTest = isTest || HasTestSdkReference(hierarchy, cancellationToken),
                    SdkStyle = sdkStyle,
                    OutputAssembly = output,
                    Frameworks = ReadProperty(hierarchy, "TargetFrameworks", config) ?? ReadProperty(hierarchy, "TargetFramework", config),
                });
            }

            return projects;
        }

        private string? ActiveConfiguration(IVsHierarchy hierarchy)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            var build = context.GetService<IVsSolutionBuildManager>(typeof(SVsSolutionBuildManager));
            var configs = new IVsProjectCfg[1];
            return ErrorHandler.Succeeded(build.FindActiveProjectCfg(IntPtr.Zero, IntPtr.Zero, hierarchy, configs)) && configs[0] != null
                && ErrorHandler.Succeeded(configs[0].get_DisplayName(out var name)) ? name : null;
        }

        private static string? ReadProperty(IVsHierarchy hierarchy, string name, string? configuration)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            if (!(hierarchy is IVsBuildPropertyStorage storage))
            {
                return null;
            }

            if (ErrorHandler.Succeeded(storage.GetPropertyValue(name, configuration, (uint)_PersistStorageType.PST_PROJECT_FILE, out var value)) && !string.IsNullOrWhiteSpace(value))
            {
                return value.Trim();
            }

            return configuration != null && ErrorHandler.Succeeded(storage.GetPropertyValue(name, null, (uint)_PersistStorageType.PST_PROJECT_FILE, out value))
                && !string.IsNullOrWhiteSpace(value) ? value.Trim() : null;
        }

        private static bool HasTestSdkReference(IVsHierarchy hierarchy, CancellationToken cancellationToken)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            var pending = new Stack<uint>();
            var seen = new HashSet<uint>();
            pending.Push(VSConstants.VSITEMID_ROOT);
            while (pending.Count > 0 && seen.Count < 10000)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var item = pending.Pop();
                if (!seen.Add(item))
                {
                    continue;
                }

                foreach (var property in new[] { __VSHPROPID.VSHPROPID_Name, __VSHPROPID.VSHPROPID_Caption })
                {
                    if (ErrorHandler.Succeeded(hierarchy.GetProperty(item, (int)property, out var value)) && IsTestSdk(value as string))
                    {
                        return true;
                    }
                }

                if (hierarchy is IVsBuildPropertyStorage storage && ErrorHandler.Succeeded(storage.GetItemAttribute(item, "Include", out var include)) && IsTestSdk(include))
                {
                    return true;
                }

                foreach (var property in new[] { __VSHPROPID.VSHPROPID_FirstChild, __VSHPROPID.VSHPROPID_NextSibling })
                {
                    if (ErrorHandler.Succeeded(hierarchy.GetProperty(item, (int)property, out var child)))
                    {
                        var id = child is int signed ? unchecked((uint)signed) : child is uint unsigned ? unsigned : VSConstants.VSITEMID_NIL;
                        if (id != VSConstants.VSITEMID_NIL && id != VSConstants.VSITEMID_ROOT)
                        {
                            pending.Push(id);
                        }
                    }
                }
            }

            return false;
        }

        private static bool IsTestSdk(string? name) => !string.IsNullOrWhiteSpace(name) &&
            (string.Equals(name!.Trim(), "Microsoft.NET.Test.Sdk", StringComparison.OrdinalIgnoreCase)
                || name.Trim().StartsWith("Microsoft.NET.Test.Sdk (", StringComparison.OrdinalIgnoreCase)
                || name.Trim().StartsWith("Microsoft.NET.Test.Sdk,", StringComparison.OrdinalIgnoreCase));

        private static void CompleteSnapshots(IEnumerable<ProjectProperties> properties, ProjectDiscovery result, CancellationToken cancellationToken)
        {
            foreach (var project in properties)
            {
                cancellationToken.ThrowIfCancellationRequested();
                try
                {
                    var xml = LoadProjectXml(project.Path);
                    var root = xml.Root;
                    if (root == null || root.Name.LocalName != "Project")
                    {
                        continue;
                    }

                    // Fallback for project systems that do not expose evaluated property storage.
                    project.IsTest |= root.Descendants().Any(e => (e.Name.LocalName == "IsTestProject" && string.Equals(e.Value.Trim(), "true", StringComparison.OrdinalIgnoreCase))
                        || (e.Name.LocalName == "ProjectTypeGuids" && e.Value.IndexOf(LegacyTestProjectGuid, StringComparison.OrdinalIgnoreCase) >= 0));
                    project.IsTest |= root.Descendants().Any(e => (e.Name.LocalName == "PackageReference" || e.Name.LocalName == "Reference")
                        && (IsTestSdk((string?)e.Attribute("Include")) || IsTestSdk((string?)e.Attribute("Update"))));
                    project.SdkStyle |= !string.IsNullOrWhiteSpace((string?)root.Attribute("Sdk"))
                        || root.Elements().Any(e => e.Name.LocalName == "Sdk")
                        || root.Descendants().Any(e => e.Name.LocalName == "Import" && !string.IsNullOrWhiteSpace((string?)e.Attribute("Sdk")));
                    if (!project.IsTest)
                    {
                        var packages = Path.Combine(Path.GetDirectoryName(project.Path)!, "packages.config");
                        if (File.Exists(packages))
                        {
                            project.IsTest = LoadProjectXml(packages).Descendants().Any(e => e.Name.LocalName == "package" && IsTestSdk((string?)e.Attribute("id")));
                        }
                    }

                    if (!project.IsTest)
                    {
                        continue;
                    }

                    var output = project.OutputAssembly;
                    if (!string.IsNullOrWhiteSpace(output) && output!.IndexOf("$(", StringComparison.Ordinal) < 0)
                    {
                        output = Path.GetFullPath(Path.IsPathRooted(output) ? output : Path.Combine(Path.GetDirectoryName(project.Path)!, output));
                    }
                    else
                    {
                        output = null;
                    }

                    var frameworks = project.Frameworks;
                    if (string.IsNullOrWhiteSpace(frameworks))
                    {
                        frameworks = root.Descendants().FirstOrDefault(e => e.Name.LocalName == "TargetFrameworks")?.Value;
                    }

                    var tfms = (frameworks ?? string.Empty).Split(';').Select(t => t.Trim()).Where(t => t.Length > 0 && t.IndexOf("$(", StringComparison.Ordinal) < 0)
                        .Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
                    if (tfms.Length > 32)
                    {
                        result.Errors.Add(project.UniqueName + ": More than 32 target frameworks are not supported for a test run.");
                        continue;
                    }

                    result.Projects.Add(new TestProjectSnapshot(project.UniqueName, project.Path, project.SdkStyle, output, tfms));
                }
                catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException || ex is XmlException || ex is ArgumentException)
                {
                    // An unreadable project might contain the test SDK reference; do not silently omit it.
                    if (result.Errors.Count < 100)
                    {
                        result.Errors.Add(project.UniqueName + ": Cannot inspect the project for tests: " + TestRunnerText.Truncate(ex.Message, 1000));
                    }
                }
            }
        }

        private static XDocument LoadProjectXml(string path)
        {
            using (var reader = XmlReader.Create(path, new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null, MaxCharactersInDocument = 4 * 1024 * 1024 }))
            {
                return XDocument.Load(reader);
            }
        }

        private async Task<string?> BuildBusyErrorAsync(CancellationToken cancellationToken)
        {
            await context.Jtf.SwitchToMainThreadAsync(cancellationToken);
            return BuildBusyError();
        }

        private string? BuildBusyError()
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            var manager = context.GetService<IVsSolutionBuildManager>(typeof(SVsSolutionBuildManager));
            ErrorHandler.ThrowOnFailure(manager.QueryBuildManagerBusy(out var busy));
            return busy == 0 ? null : "A Visual Studio build is running. Wait for it to finish before listing or running tests.";
        }

        private string? GetVstestPath()
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            try
            {
                var shell = context.GetService<IVsShell>(typeof(SVsShell));
                if (ErrorHandler.Succeeded(shell.GetProperty((int)__VSSPROPID.VSSPROPID_InstallDirectory, out var directory)) && directory is string path)
                {
                    // InstallDirectory is normally <install>\Common7\IDE; accept an installation-root value too.
                    var local = Path.Combine(path, "Extensions", "TestPlatform", "vstest.console.exe");
                    return File.Exists(local) ? local : Path.Combine(path, "Common7", "IDE", "Extensions", "TestPlatform", "vstest.console.exe");
                }
            }
            catch (COMException ex)
            {
                context.LogUnexpected("Test platform installation lookup", ex);
            }

            return null;
        }

        private sealed class ProjectDiscovery
        {
            public List<TestProjectSnapshot> Projects { get; } = new List<TestProjectSnapshot>();
            public List<string> Errors { get; } = new List<string>();
            public string? VstestPath { get; set; }
        }

        private sealed class ProjectProperties
        {
            public string Caption { get; set; } = string.Empty;
            public string UniqueName { get; set; } = string.Empty;
            public string Path { get; set; } = string.Empty;
            public bool IsTest { get; set; }
            public bool SdkStyle { get; set; }
            public string? OutputAssembly { get; set; }
            public string? Frameworks { get; set; }
        }
    }
}
