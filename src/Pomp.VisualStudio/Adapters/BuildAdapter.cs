using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using EnvDTE;
using EnvDTE80;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Threading;
using Pomp.Diagnostics;
using Pomp.Adapters;
using Task = System.Threading.Tasks.Task;

namespace Pomp.VisualStudio.Adapters
{
    /// <summary>
    /// Solution/project builds through DTE <c>SolutionBuild</c>. The build is started with WaitForBuildToFinish=false and
    /// completion is awaited through <c>BuildEvents.OnBuildDone</c>, so the UI thread is never blocked.
    /// <para>
    /// A solution configuration is a (name, platform) pair: <c>SolutionBuild.SolutionConfigurations</c> holds one entry per pair, so
    /// "Debug" alone is ambiguous when a solution has several platforms. A request names the configuration as <c>Name</c> (the
    /// platform stays the active one) or <c>Name|Platform</c>; the target is activated only when it differs from the active
    /// pair, and the exact original pair is activated again when the build is over.
    /// </para>
    /// </summary>
    internal sealed class BuildAdapter : IBuildAdapter
    {
        private const int MaxReportedErrors = 100;
        private static readonly TimeSpan CancelGracePeriod = TimeSpan.FromSeconds(15);

        private readonly VsAdapterContext context;
        private readonly ErrorListAdapter errorList;
        private readonly SemaphoreSlim buildLock = new SemaphoreSlim(1, 1);

        public BuildAdapter(VsAdapterContext context, ErrorListAdapter errorList)
        {
            this.context = context;
            this.errorList = errorList;
        }

        public async Task<BuildResult> BuildAsync(BuildRequest request, CancellationToken cancellationToken)
        {
            request ??= new BuildRequest();

            // One agent-initiated build at a time; VS itself refuses concurrent builds.
            await buildLock.WaitAsync(cancellationToken);
            try
            {
                return await BuildCoreAsync(request, cancellationToken);
            }
            finally
            {
                buildLock.Release();
            }
        }

        private async Task<BuildResult> BuildCoreAsync(BuildRequest request, CancellationToken cancellationToken)
        {
            await context.Jtf.SwitchToMainThreadAsync(cancellationToken);

            var dte = context.Dte;
            if (dte.Solution == null || !dte.Solution.IsOpen)
            {
                return Failure("No solution is open, so there is nothing to build.");
            }

            var solutionBuild = dte.Solution.SolutionBuild;
            if (solutionBuild.BuildState == vsBuildState.vsBuildStateInProgress)
            {
                return Failure("A build is already running in Visual Studio.");
            }

            // Resolve everything that can fail BEFORE the IDE state is touched: a bad request must not change the active configuration.
            ConfigurationPair? wanted = null;
            ConfigurationPair? active = null;
            SolutionConfiguration? target = null;
            if (!string.IsNullOrWhiteSpace(request.Configuration))
            {
                if (!TryParseConfiguration(request.Configuration!, out var configurationName, out var requestedPlatform))
                {
                    return Failure("The configuration '" + request.Configuration + "' is not valid. Use a configuration name such as Debug, or Name|Platform such as Release|x64.");
                }

                active = ReadActivePair(solutionBuild);
                if (active == null)
                {
                    return Failure("The active solution configuration could not be read, so the configuration was not changed and nothing was built.");
                }

                // Without a platform the active platform is kept (a bare "Release" must not silently switch Debug|x64 to Release|Any CPU).
                wanted = new ConfigurationPair(configurationName, requestedPlatform ?? active.Platform);
                target = FindConfiguration(solutionBuild, wanted);
                if (target == null)
                {
                    return Failure("The solution configuration '" + wanted + "' does not exist. Available: " + ListConfigurations(solutionBuild) + ".");
                }
            }

            string? projectUniqueName = null;
            if (!string.IsNullOrWhiteSpace(request.Project))
            {
                projectUniqueName = FindProjectUniqueName(dte, request.Project!);
                if (projectUniqueName == null)
                {
                    return Failure("The project '" + request.Project + "' was not found in the solution.");
                }
            }

            // Only a target that differs from the active pair is activated; the exact original pair is what gets restored.
            ConfigurationPair? restoreTo = null;
            if (target != null && wanted != null && active != null && !active.Matches(wanted))
            {
                target.Activate();
                restoreTo = active;
            }

            try
            {
                // The tracker keeps the BuildEvents object referenced for the whole build: event sinks are COM objects.
                using (var tracker = new BuildTracker(dte, context.Jtf))
                {
                    tracker.Start();

                    if (projectUniqueName != null)
                    {
                        var configName = ReadActivePair(solutionBuild)?.Name ?? wanted?.Name ?? "Debug";
                        solutionBuild.BuildProject(configName, projectUniqueName, false);
                    }
                    else
                    {
                        solutionBuild.Build(false);
                    }

                    var completed = await AwaitBuildAsync(dte, tracker, cancellationToken);
                    if (!completed)
                    {
                        throw new OperationCanceledException(cancellationToken);
                    }

                    // Let the Error List settle after OnBuildDone (its table is fed asynchronously).
                    await Task.Delay(300, CancellationToken.None);
                    await context.Jtf.SwitchToMainThreadAsync(cancellationToken);

                    return await CollectResultAsync(solutionBuild, tracker, cancellationToken);
                }
            }
            finally
            {
                if (restoreTo != null)
                {
                    await context.Jtf.SwitchToMainThreadAsync();
                    RestoreConfiguration(solutionBuild, restoreTo);
                }
            }
        }

        /// <summary>Waits for OnBuildDone; on cancellation asks VS to cancel the build and waits a bounded time for it to wind down.</summary>
        private async Task<bool> AwaitBuildAsync(DTE2 dte, BuildTracker tracker, CancellationToken cancellationToken)
        {
            var cancelled = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            using (cancellationToken.Register(() => cancelled.TrySetResult(true)))
            {
                if (await Task.WhenAny(tracker.Completion.Task, cancelled.Task) == tracker.Completion.Task)
                {
                    return !cancellationToken.IsCancellationRequested;
                }

                await CancelBuildAsync(dte);
                await Task.WhenAny(tracker.Completion.Task, Task.Delay(CancelGracePeriod));
                return false;
            }
        }

        private async Task CancelBuildAsync(DTE2 dte)
        {
            try
            {
                await context.Jtf.SwitchToMainThreadAsync();
                dte.ExecuteCommand("Build.Cancel");
            }
            catch (Exception ex)
            {
                context.LogUnexpected("Cancelling the build (Build.Cancel)", ex);
            }
        }

        private async Task<BuildResult> CollectResultAsync(SolutionBuild solutionBuild, BuildTracker tracker, CancellationToken cancellationToken)
        {
            await context.Jtf.SwitchToMainThreadAsync(cancellationToken);

            var failedProjects = 0;
            try
            {
                failedProjects = solutionBuild.LastBuildInfo;
            }
            catch (System.Runtime.InteropServices.COMException ex)
            {
                failedProjects = tracker.FailedProjectConfigurations;
                context.LogUnexpected("Reading the number of failed projects (SolutionBuild.LastBuildInfo)", ex);
            }

            // Totals come from the whole Error List; only the list of items handed back is capped. Counting a huge list yields to the
            // message loop between batches and honours cancellation.
            var diagnostics = await errorList.ReadSummaryAsync(MaxReportedErrors, cancellationToken);

            // The Error List can also contain unrelated IntelliSense diagnostics; DTE owns the build outcome.
            var succeeded = failedProjects == 0;
            var summary = (succeeded ? "Build succeeded" : "Build FAILED")
                          + ": " + tracker.SucceededProjectConfigurations + " project(s) succeeded, " + failedProjects + " failed. "
                          + "Current Error List: " + diagnostics.ErrorCount + " error(s), " + diagnostics.WarningCount + " warning(s).";

            return new BuildResult
            {
                Succeeded = succeeded,
                FailedProjects = failedProjects,
                Summary = summary,
                Errors = diagnostics.Items,
                ErrorCount = diagnostics.ErrorCount,
                WarningCount = diagnostics.WarningCount,
            };
        }

        private static BuildResult Failure(string message) =>
            new BuildResult { Succeeded = false, FailedProjects = 0, Summary = message };

        // ---- solution configurations (name + platform) -----------------------------------------------

        /// <summary>Splits <c>Name</c> or <c>Name|Platform</c>; false when the name part is empty.</summary>
        private static bool TryParseConfiguration(string text, out string name, out string? platform)
        {
            var trimmed = text.Trim();
            var bar = trimmed.IndexOf('|');
            if (bar < 0)
            {
                name = trimmed;
                platform = null;
            }
            else
            {
                name = trimmed.Substring(0, bar).Trim();
                var platformText = trimmed.Substring(bar + 1).Trim();
                platform = platformText.Length == 0 ? null : platformText;
            }

            return name.Length > 0;
        }

        private static ConfigurationPair Describe(SolutionConfiguration configuration)
        {
            ThreadHelper.ThrowIfNotOnUIThread();

            var name = configuration.Name ?? string.Empty;
            var platform = string.Empty;
            try
            {
                // SolutionConfiguration2 adds PlatformName; the same COM object implements it.
                platform = (configuration as SolutionConfiguration2)?.PlatformName ?? string.Empty;
            }
            catch (System.Runtime.InteropServices.COMException)
            {
                // A configuration without platform information is compared by name only.
            }

            return new ConfigurationPair(name, platform);
        }

        private static ConfigurationPair? ReadActivePair(SolutionBuild solutionBuild)
        {
            ThreadHelper.ThrowIfNotOnUIThread();

            try
            {
                var active = solutionBuild.ActiveConfiguration;
                return active == null ? null : Describe(active);
            }
            catch (System.Runtime.InteropServices.COMException)
            {
                return null;
            }
        }

        private static SolutionConfiguration? FindConfiguration(SolutionBuild solutionBuild, ConfigurationPair wanted)
        {
            ThreadHelper.ThrowIfNotOnUIThread();

            foreach (SolutionConfiguration configuration in solutionBuild.SolutionConfigurations)
            {
                if (Describe(configuration).Matches(wanted))
                {
                    return configuration;
                }
            }

            return null;
        }

        /// <summary>"Name|Platform" pairs, for the not-found message.</summary>
        private static string ListConfigurations(SolutionBuild solutionBuild)
        {
            ThreadHelper.ThrowIfNotOnUIThread();

            var pairs = new List<string>();
            foreach (SolutionConfiguration configuration in solutionBuild.SolutionConfigurations)
            {
                var pair = Describe(configuration);
                var text = pair.Platform.Length == 0 ? pair.Name : pair.Name + "|" + pair.Platform;
                if (!pairs.Contains(text, StringComparer.OrdinalIgnoreCase))
                {
                    pairs.Add(text);
                }
            }

            return string.Join(", ", pairs);
        }

        /// <summary>Activates the exact (name, platform) pair that was active before the build.</summary>
        private void RestoreConfiguration(SolutionBuild solutionBuild, ConfigurationPair original)
        {
            ThreadHelper.ThrowIfNotOnUIThread();

            try
            {
                var current = ReadActivePair(solutionBuild);
                if (current != null && current.Matches(original))
                {
                    return;
                }

                var configuration = FindConfiguration(solutionBuild, original);
                if (configuration == null)
                {
                    context.Log.Warn("The solution configuration '" + original + "' that was active before the build no longer exists; it could not be restored.");
                    return;
                }

                configuration.Activate();
            }
            catch (System.Runtime.InteropServices.COMException ex)
            {
                // The user's active configuration stays changed: worth a Warning (ActivityLog), not a silent best effort.
                context.LogUnexpected("Restoring the solution configuration '" + original + "'", ex);
            }
        }

        private static string? FindProjectUniqueName(DTE2 dte, string projectName)
        {
            ThreadHelper.ThrowIfNotOnUIThread();

            foreach (Project project in dte.Solution.Projects)
            {
                var match = FindProjectRecursive(project, projectName);
                if (match != null)
                {
                    return match;
                }
            }

            return null;
        }

        private static string? FindProjectRecursive(Project project, string projectName)
        {
            ThreadHelper.ThrowIfNotOnUIThread();

            try
            {
                if (string.Equals(project.Name, projectName, StringComparison.OrdinalIgnoreCase)
                    || string.Equals(project.UniqueName, projectName, StringComparison.OrdinalIgnoreCase))
                {
                    return project.UniqueName;
                }

                if (project.ProjectItems == null)
                {
                    return null;
                }

                foreach (ProjectItem item in project.ProjectItems)
                {
                    if (item.SubProject != null)
                    {
                        var nested = FindProjectRecursive(item.SubProject, projectName);
                        if (nested != null)
                        {
                            return nested;
                        }
                    }
                }
            }
            catch (System.Runtime.InteropServices.COMException)
            {
                // Unloaded or special project: skip.
            }

            return null;
        }

        /// <summary>One solution configuration: its name and its platform ("" when the solution has no platform information).</summary>
        private sealed class ConfigurationPair
        {
            public ConfigurationPair(string name, string platform)
            {
                Name = name.Trim();
                Platform = platform.Trim();
            }

            public string Name { get; }

            public string Platform { get; }

            /// <summary>Case-insensitive; blanks in the platform are ignored ("AnyCPU" equals "Any CPU").</summary>
            public bool Matches(ConfigurationPair other)
            {
                return string.Equals(Name, other.Name, StringComparison.OrdinalIgnoreCase)
                       && string.Equals(Compact(Platform), Compact(other.Platform), StringComparison.OrdinalIgnoreCase);
            }

            public override string ToString() => Platform.Length == 0 ? Name : Name + "|" + Platform;

            private static string Compact(string platform) => platform.Replace(" ", string.Empty);
        }

        /// <summary>Subscribes to <c>BuildEvents</c> for the duration of one build and completes when it is done.</summary>
        private sealed class BuildTracker : IDisposable
        {
            private readonly BuildEvents buildEvents;
            private readonly JoinableTaskFactory joinableTaskFactory;
            private bool started;

            public BuildTracker(DTE2 dte, JoinableTaskFactory joinableTaskFactory)
            {
                ThreadHelper.ThrowIfNotOnUIThread();
                this.joinableTaskFactory = joinableTaskFactory;
                buildEvents = dte.Events.BuildEvents;
            }

            public TaskCompletionSource<bool> Completion { get; } = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

            public int SucceededProjectConfigurations { get; private set; }

            public int FailedProjectConfigurations { get; private set; }

            public void Start()
            {
                ThreadHelper.ThrowIfNotOnUIThread();

                started = true;
                buildEvents.OnBuildProjConfigDone += OnProjectConfigurationDone;
                buildEvents.OnBuildDone += OnBuildDone;
            }

            public void Dispose()
            {
                if (!started)
                {
                    return;
                }

                started = false;
                _ = joinableTaskFactory.RunAsync(async () =>
                {
                    try
                    {
                        await joinableTaskFactory.SwitchToMainThreadAsync();
                        buildEvents.OnBuildProjConfigDone -= OnProjectConfigurationDone;
                        buildEvents.OnBuildDone -= OnBuildDone;
                    }
                    catch (Exception)
                    {
                        // Shutting down.
                    }
                });
            }

            private void OnProjectConfigurationDone(string project, string projectConfig, string platform, string solutionConfig, bool success)
            {
                if (success)
                {
                    SucceededProjectConfigurations++;
                }
                else
                {
                    FailedProjectConfigurations++;
                }
            }

            private void OnBuildDone(vsBuildScope scope, vsBuildAction action)
            {
                Completion.TrySetResult(true);
            }
        }
    }
}
