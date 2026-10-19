using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using EnvDTE;
using EnvDTE80;
using Microsoft.VisualStudio;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Shell.Interop;
using Microsoft.VisualStudio.Threading;
using Pomp.Diagnostics;
using Pomp.Adapters;
using Task = System.Threading.Tasks.Task;

namespace Pomp.VisualStudio.Adapters
{
    /// <summary>
    /// Solution/project builds through DTE <c>SolutionBuild</c>, started with WaitForBuildToFinish=false so the UI thread is never
    /// blocked. omp never times out a host tool, so every way a build can end must end the call:
    /// <list type="bullet">
    /// <item>A build that is already running (the user's, a test run's) is waited for first and never cancelled.</item>
    /// <item>No build is started during a debug session: Visual Studio does not build then.</item>
    /// <item>The build is followed through <c>BuildEvents</c> and the build manager's busy state: OnBuildDone ends it, so does a
    /// build manager that stays idle without that event, and a build that never shows any sign of running counts as not started.</item>
    /// <item>Cancellation (a stop, or the tool's time limit) cancels only the build this call started.</item>
    /// </list>
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
        private const string NothingBuilt = "Nothing was built.";

        /// <summary>How often the build manager is read while a build is awaited (OnBuildDone wakes the wait at once).</summary>
        private static readonly TimeSpan PollInterval = TimeSpan.FromMilliseconds(500);

        /// <summary>
        /// How long a requested build may go without any sign of running before it counts as not started. Generous, so a slow start
        /// is not taken for no build: it only delays the report of a build that really did not start.
        /// </summary>
        private static readonly TimeSpan StartGracePeriod = TimeSpan.FromSeconds(15);

        /// <summary>
        /// How long the build manager may stay idle after a build without OnBuildDone before the build counts as over anyway:
        /// Visual Studio does not raise the event for every way a build ends.
        /// </summary>
        private static readonly TimeSpan MissingDoneGracePeriod = TimeSpan.FromSeconds(3);

        /// <summary>How long starting the build may block before the status says that a Visual Studio dialog may be holding it.</summary>
        private static readonly TimeSpan StartHintDelay = TimeSpan.FromSeconds(10);

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
            var call = new BuildCall(request.Progress);

            // One agent-initiated build at a time; VS itself refuses concurrent builds.
            if (buildLock.CurrentCount == 0)
            {
                call.Report("Waiting for another build_solution call to finish");
            }

            try
            {
                await buildLock.WaitAsync(cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw new OperationCanceledException("Nothing was built: another build_solution call was still running.", cancellationToken);
            }

            try
            {
                return await BuildCoreAsync(request, call, cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw new OperationCanceledException(call.CancelledOutcome, cancellationToken);
            }
            finally
            {
                buildLock.Release();
            }
        }

        private async Task<BuildResult> BuildCoreAsync(BuildRequest request, BuildCall call, CancellationToken cancellationToken)
        {
            await context.Jtf.SwitchToMainThreadAsync(cancellationToken);

            var dte = context.Dte;

            // A build that something else started is waited for, never cancelled: it is not ours.
            if (IsBuildRunning(dte))
            {
                call.Report("Waiting for the build already running in Visual Studio to finish");
                call.CancelledOutcome = "Nothing was built: the build already running in Visual Studio had not finished.";
                do
                {
                    await Task.Delay(PollInterval, cancellationToken);
                    await context.Jtf.SwitchToMainThreadAsync(cancellationToken);
                }
                while (IsBuildRunning(dte));

                call.CancelledOutcome = NothingBuilt;
            }

            if (dte.Solution == null || !dte.Solution.IsOpen)
            {
                return NotBuilt("No solution is open, so there is nothing to build.");
            }

            if (IsDebugging(dte))
            {
                return NotBuilt("Visual Studio is debugging, and it does not build during a debug session. Stop debugging first (the user can press Shift+F5), then build again.");
            }

            var solutionBuild = dte.Solution.SolutionBuild;

            // Resolve everything that can fail BEFORE the IDE state is touched: a bad request must not change the active configuration.
            ConfigurationPair? wanted = null;
            ConfigurationPair? active = null;
            SolutionConfiguration? target = null;
            if (!string.IsNullOrWhiteSpace(request.Configuration))
            {
                if (!TryParseConfiguration(request.Configuration!, out var configurationName, out var requestedPlatform))
                {
                    return NotBuilt("The configuration '" + request.Configuration + "' is not valid. Use a configuration name such as Debug, or Name|Platform such as Release|x64.");
                }

                active = ReadActivePair(solutionBuild);
                if (active == null)
                {
                    return NotBuilt("The active solution configuration could not be read, so the configuration was not changed and nothing was built.");
                }

                // Without a platform the active platform is kept (a bare "Release" must not silently switch Debug|x64 to Release|Any CPU).
                wanted = new ConfigurationPair(configurationName, requestedPlatform ?? active.Platform);
                target = FindConfiguration(solutionBuild, wanted);
                if (target == null)
                {
                    return NotBuilt("The solution configuration '" + wanted + "' does not exist. Available: " + ListConfigurations(solutionBuild) + ".");
                }
            }

            string? projectUniqueName = null;
            if (!string.IsNullOrWhiteSpace(request.Project))
            {
                projectUniqueName = FindProjectUniqueName(dte, request.Project!);
                if (projectUniqueName == null)
                {
                    return NotBuilt("The project '" + request.Project + "' was not found in the solution.");
                }
            }

            cancellationToken.ThrowIfCancellationRequested();

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
                    call.CancelledOutcome = "Nothing was built: Visual Studio had not started the build.";
                    StartBuild(solutionBuild, call, projectUniqueName, wanted);

                    if (!await FollowBuildAsync(dte, tracker, call, cancellationToken))
                    {
                        context.Log.Warn("Visual Studio did not start the build that build_solution requested: no build event, and the build manager stayed idle.");
                        return NotBuilt("Visual Studio did not start the build. A prompt to save changes may have been cancelled, or Visual Studio refused to build right now; check Visual Studio, then build again.");
                    }

                    // Let the Error List settle after the build (its table is fed asynchronously).
                    call.CancelledOutcome = "The build ended, but its diagnostics were not read; call read_error_list for them.";
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

        /// <summary>
        /// Starts the build without waiting for it. Starting can block in a modal Visual Studio prompt (save changes before
        /// building); a status line then says so, from a timer that does not need the UI thread.
        /// </summary>
        private static void StartBuild(SolutionBuild solutionBuild, BuildCall call, string? projectUniqueName, ConfigurationPair? wanted)
        {
            ThreadHelper.ThrowIfNotOnUIThread();

            using (var starting = new CancellationTokenSource())
            {
                _ = HintWhileStartingAsync(call, starting.Token);
                try
                {
                    if (projectUniqueName != null)
                    {
                        var configName = ReadActivePair(solutionBuild)?.Name ?? wanted?.Name ?? "Debug";
                        solutionBuild.BuildProject(configName, projectUniqueName, false);
                    }
                    else
                    {
                        solutionBuild.Build(false);
                    }
                }
                finally
                {
                    starting.Cancel();
                }
            }

            call.Report("Building");
        }

        private static async Task HintWhileStartingAsync(BuildCall call, CancellationToken starting)
        {
            try
            {
                await Task.Delay(StartHintDelay, starting).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }

            call.ReportUnless(starting, "Waiting for Visual Studio to start the build; a Visual Studio dialog may be waiting for an answer");
        }

        /// <summary>
        /// Follows the build this call started to its end: OnBuildDone ends it, and so does a build manager that stays idle after
        /// the build without that event. False when no sign of a build appeared within <see cref="StartGracePeriod"/>. On
        /// cancellation the build is cancelled before the cancellation propagates.
        /// </summary>
        private async Task<bool> FollowBuildAsync(DTE2 dte, BuildTracker tracker, BuildCall call, CancellationToken cancellationToken)
        {
            var waited = System.Diagnostics.Stopwatch.StartNew();
            var idle = new System.Diagnostics.Stopwatch();
            var seenRunning = false;
            try
            {
                await context.Jtf.SwitchToMainThreadAsync(cancellationToken);
                while (!tracker.Completion.Task.IsCompleted)
                {
                    var running = IsBuildRunning(dte);
                    seenRunning |= running;
                    if (running)
                    {
                        idle.Reset();
                    }
                    else if (seenRunning || tracker.Began)
                    {
                        idle.Start();
                        if (idle.Elapsed >= MissingDoneGracePeriod)
                        {
                            context.Log.Warn("A build ended without BuildEvents.OnBuildDone; build_solution read its outcome from the build manager.");
                            return true;
                        }
                    }
                    else if (waited.Elapsed >= StartGracePeriod)
                    {
                        return false;
                    }

                    await Task.WhenAny(tracker.Completion.Task, Task.Delay(PollInterval, cancellationToken));
                    cancellationToken.ThrowIfCancellationRequested();
                    await context.Jtf.SwitchToMainThreadAsync(cancellationToken);
                }

                return true;
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                await CancelBuildAsync(dte, tracker, call);
                throw;
            }
        }

        /// <summary>
        /// Asks Visual Studio to cancel the build this call started and follows it for a bounded time; the call's cancellation
        /// outcome says how far that got. A build that is not running (not started yet, or already over) is left alone.
        /// </summary>
        private async Task CancelBuildAsync(DTE2 dte, BuildTracker tracker, BuildCall call)
        {
            try
            {
                await context.Jtf.SwitchToMainThreadAsync();
                if (tracker.Completion.Task.IsCompleted || !IsBuildRunning(dte))
                {
                    if (tracker.Completion.Task.IsCompleted || tracker.Began)
                    {
                        call.CancelledOutcome = "The build ended, but its result was not read; call read_error_list for its diagnostics.";
                    }

                    return;
                }

                call.CancelledOutcome = "Visual Studio was asked to cancel the build, but it was still running " + (int)CancelGracePeriod.TotalSeconds + " seconds later.";
                dte.ExecuteCommand("Build.Cancel");
                var waited = System.Diagnostics.Stopwatch.StartNew();
                while (!tracker.Completion.Task.IsCompleted && IsBuildRunning(dte))
                {
                    if (waited.Elapsed >= CancelGracePeriod)
                    {
                        return;
                    }

                    await Task.WhenAny(tracker.Completion.Task, Task.Delay(PollInterval));
                    await context.Jtf.SwitchToMainThreadAsync();
                }

                call.CancelledOutcome = "The build was cancelled in Visual Studio.";
            }
            catch (Exception ex)
            {
                context.LogUnexpected("Cancelling the build (Build.Cancel)", ex);
            }
        }

        /// <summary>True while any build runs: the solution build manager is busy, or DTE reports a build in progress.</summary>
        private bool IsBuildRunning(DTE2 dte)
        {
            ThreadHelper.ThrowIfNotOnUIThread();

            var manager = context.GetService<IVsSolutionBuildManager>(typeof(SVsSolutionBuildManager));
            if (ErrorHandler.Succeeded(manager.QueryBuildManagerBusy(out var busy)) && busy != 0)
            {
                return true;
            }

            var solution = dte.Solution;
            return solution != null && solution.IsOpen && solution.SolutionBuild.BuildState == vsBuildState.vsBuildStateInProgress;
        }

        /// <summary>True in run or break mode.</summary>
        private static bool IsDebugging(DTE2 dte)
        {
            ThreadHelper.ThrowIfNotOnUIThread();

            try
            {
                var debugger = dte.Debugger;
                return debugger != null && debugger.CurrentMode != dbgDebugMode.dbgDesignMode;
            }
            catch (System.Runtime.InteropServices.COMException)
            {
                // An unreadable mode does not block the build; a build that does not start is still reported.
                return false;
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

        private static BuildResult NotBuilt(string reason) => new BuildResult { Error = reason };

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

        /// <summary>One build_solution call: its status lines, and what a cancellation at the current step means for the model.</summary>
        private sealed class BuildCall
        {
            private readonly object gate = new object();
            private readonly IProgress<string>? progress;

            public BuildCall(IProgress<string>? progress)
            {
                this.progress = progress;
            }

            /// <summary>The message of the cancellation (a stop, or the time limit) at the current step.</summary>
            public string CancelledOutcome { get; set; } = NothingBuilt;

            public void Report(string status) => ReportUnless(CancellationToken.None, status);

            /// <summary>Reports unless <paramref name="stale"/> is cancelled; check and report are atomic against other reports.</summary>
            public void ReportUnless(CancellationToken stale, string status)
            {
                lock (gate)
                {
                    if (stale.IsCancellationRequested)
                    {
                        return;
                    }

                    try
                    {
                        progress?.Report(status);
                    }
                    catch (Exception)
                    {
                        // Status lines are best effort.
                    }
                }
            }
        }

        /// <summary>Subscribes to <c>BuildEvents</c> for the duration of one build: whether it began, and when it is done.</summary>
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

            /// <summary>OnBuildBegin was raised after <see cref="Start"/>.</summary>
            public bool Began { get; private set; }

            public void Start()
            {
                ThreadHelper.ThrowIfNotOnUIThread();

                started = true;
                buildEvents.OnBuildBegin += OnBuildBegin;
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
                        buildEvents.OnBuildBegin -= OnBuildBegin;
                        buildEvents.OnBuildProjConfigDone -= OnProjectConfigurationDone;
                        buildEvents.OnBuildDone -= OnBuildDone;
                    }
                    catch (Exception)
                    {
                        // Shutting down.
                    }
                });
            }

            private void OnBuildBegin(vsBuildScope scope, vsBuildAction action)
            {
                Began = true;
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
