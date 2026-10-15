using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using EnvDTE80;
using Microsoft.VisualStudio;
using Microsoft.VisualStudio.ComponentModelHost;
using Microsoft.VisualStudio.Editor;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Shell.Interop;
using Microsoft.VisualStudio.Text;
using Microsoft.VisualStudio.Text.Operations;
using Microsoft.VisualStudio.TextManager.Interop;
using Microsoft.VisualStudio.Threading;
using Pomp.Diagnostics;

namespace Pomp.VisualStudio.Adapters
{
    /// <summary>
    /// Shared plumbing of the Visual Studio adapters. Every member that touches a COM service must be called on the UI
    /// thread (the adapters switch with <c>JoinableTaskFactory.SwitchToMainThreadAsync</c> before using them).
    /// </summary>
    internal sealed class VsAdapterContext
    {
        /// <summary>An adapter that keeps swallowing the same failure (polling calls) logs it once per this window.</summary>
        private static readonly TimeSpan UnexpectedLogWindow = TimeSpan.FromSeconds(30);

        private readonly object unexpectedLogGate = new object();
        private readonly Dictionary<string, UnexpectedLogState> unexpectedLogs = new Dictionary<string, UnexpectedLogState>(StringComparer.Ordinal);
        private DTE2? dte;
        private IComponentModel? componentModel;

        public VsAdapterContext(AsyncPackage package, JoinableTaskFactory joinableTaskFactory, IPompLogger logger)
        {
            Package = package;
            Jtf = joinableTaskFactory;
            Log = logger;
        }

        public AsyncPackage Package { get; }

        public JoinableTaskFactory Jtf { get; }

        public IPompLogger Log { get; }

        // ---- diagnostics -----------------------------------------------------------------------------

        /// <summary>
        /// Records an unexpected failure that an adapter caught and turned into a degraded result: a Warning (which reaches the
        /// ActivityLog) naming the operation and the HRESULT. Failures that propagate to the caller are NOT logged here (the tool
        /// layer logs those), and expected failures (an item that vanished while enumerating...) are not logged at all.
        /// Repeats of the same operation within 30 seconds are counted and reported with the next entry, so polling callers
        /// cannot flood the log. Never throws.
        /// </summary>
        public void LogUnexpected(string operation, Exception exception)
        {
            try
            {
                var suppressed = 0;
                lock (unexpectedLogGate)
                {
                    var now = DateTime.UtcNow;
                    if (unexpectedLogs.TryGetValue(operation, out var state))
                    {
                        if (now - state.LastLoggedUtc < UnexpectedLogWindow)
                        {
                            state.Suppressed++;
                            return;
                        }

                        suppressed = state.Suppressed;
                        state.Suppressed = 0;
                        state.LastLoggedUtc = now;
                    }
                    else
                    {
                        unexpectedLogs[operation] = new UnexpectedLogState { LastLoggedUtc = now };
                    }
                }

                var message = operation + " failed (HRESULT 0x" + exception.HResult.ToString("X8", CultureInfo.InvariantCulture) + ")"
                              + (suppressed > 0 ? " [" + suppressed.ToString(CultureInfo.InvariantCulture) + " similar failure(s) suppressed]" : string.Empty)
                              + ": " + FirstLine(exception.Message);
                Log.Warn(message, exception);
            }
            catch (Exception)
            {
                // Logging must never throw.
            }
        }

        private static string FirstLine(string? message)
        {
            if (string.IsNullOrEmpty(message))
            {
                return "(no message)";
            }

            var end = message!.IndexOfAny(new[] { '\r', '\n' });
            return end >= 0 ? message.Substring(0, end) : message;
        }

        private sealed class UnexpectedLogState
        {
            public DateTime LastLoggedUtc { get; set; }

            public int Suppressed { get; set; }
        }

        // ---- services (UI thread only) ---------------------------------------------------------------

        public DTE2 Dte
        {
            get
            {
                ThreadHelper.ThrowIfNotOnUIThread();
                return dte ??= GetService<DTE2>(typeof(EnvDTE.DTE));
            }
        }

        public IVsSolution Solution
        {
            get
            {
                ThreadHelper.ThrowIfNotOnUIThread();
                return GetService<IVsSolution>(typeof(SVsSolution));
            }
        }

        public IVsUIShell UiShell
        {
            get
            {
                ThreadHelper.ThrowIfNotOnUIThread();
                return GetService<IVsUIShell>(typeof(SVsUIShell));
            }
        }

        public IVsTextManager TextManager
        {
            get
            {
                ThreadHelper.ThrowIfNotOnUIThread();
                return GetService<IVsTextManager>(typeof(SVsTextManager));
            }
        }

        public IVsRunningDocumentTable4 Rdt
        {
            get
            {
                ThreadHelper.ThrowIfNotOnUIThread();
                return GetService<IVsRunningDocumentTable4>(typeof(SVsRunningDocumentTable));
            }
        }

        public IVsOutputWindow OutputWindow
        {
            get
            {
                ThreadHelper.ThrowIfNotOnUIThread();
                return GetService<IVsOutputWindow>(typeof(SVsOutputWindow));
            }
        }

        public IComponentModel ComponentModel
        {
            get
            {
                ThreadHelper.ThrowIfNotOnUIThread();
                return componentModel ??= GetService<IComponentModel>(typeof(SComponentModel));
            }
        }

        public IVsEditorAdaptersFactoryService EditorAdapters
        {
            get
            {
                ThreadHelper.ThrowIfNotOnUIThread();
                return ComponentModel.GetService<IVsEditorAdaptersFactoryService>();
            }
        }

        public ITextDocumentFactoryService TextDocuments
        {
            get
            {
                ThreadHelper.ThrowIfNotOnUIThread();
                return ComponentModel.GetService<ITextDocumentFactoryService>();
            }
        }

        public ITextUndoHistoryRegistry UndoHistory
        {
            get
            {
                ThreadHelper.ThrowIfNotOnUIThread();
                return ComponentModel.GetService<ITextUndoHistoryRegistry>();
            }
        }

        /// <summary>
        /// Whether <paramref name="expectedRoot"/> is still the workspace root (UI thread), by the host environment's rule: the open
        /// solution's root directory, or the fallback workspace when there is none.
        /// </summary>
        public bool IsCurrentWorkspaceRoot(string expectedRoot)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            string? root;
            try
            {
                root = Pomp.VisualStudio.Interop.SolutionLocation.Resolve(Solution)?.RootDirectory;
            }
            catch (Exception)
            {
                return false;
            }

            return root != null ? SamePath(root, expectedRoot) : SamePath(FallbackWorkspaceDirectory, expectedRoot);
        }

        /// <summary>Folder that serves as the workspace when no solution or folder is open.</summary>
        public static string FallbackWorkspaceDirectory => Path.Combine(Pomp.VisualStudio.Hosting.VsHostEnvironment.ResolveDataDirectory(), "workspace");

        /// <summary>The workspace root of the host tools: the open solution's root directory, else the fallback workspace (created on demand).</summary>
        public System.Threading.Tasks.Task<string> GetWorkspaceRootAsync(System.Threading.CancellationToken cancellationToken) =>
            ResolveWorkspaceRootAsync(Jtf, Log, cancellationToken);

        /// <summary>The single implementation of the workspace root rule (also behind <c>IPompHostEnvironment.GetWorkspaceRootAsync</c>).</summary>
        internal static async System.Threading.Tasks.Task<string> ResolveWorkspaceRootAsync(JoinableTaskFactory jtf, IPompLogger logger, System.Threading.CancellationToken cancellationToken)
        {
            // Only the IVsSolution call needs the UI thread.
            await jtf.SwitchToMainThreadAsync(cancellationToken);

            string? root = null;
            try
            {
                if (Microsoft.VisualStudio.Shell.Package.GetGlobalService(typeof(SVsSolution)) is IVsSolution solution)
                {
                    root = Pomp.VisualStudio.Interop.SolutionLocation.Resolve(solution)?.RootDirectory;
                }
            }
            catch (Exception ex)
            {
                logger.Warn("Could not determine the solution directory; using the fallback workspace.", ex);
            }

            // File-system checks run off the UI thread: a solution on an offline UNC share must not freeze Visual Studio.
            await System.Threading.Tasks.TaskScheduler.Default;
            cancellationToken.ThrowIfCancellationRequested();

            if (root != null && Directory.Exists(root))
            {
                return root;
            }

            var fallback = FallbackWorkspaceDirectory;
            Directory.CreateDirectory(fallback);
            return fallback;
        }

        /// <summary>Per-buffer undo managers: the history they own is the one that actually records a buffer's text changes.</summary>
        public ITextBufferUndoManagerProvider UndoManagers
        {
            get
            {
                ThreadHelper.ThrowIfNotOnUIThread();
                return ComponentModel.GetService<ITextBufferUndoManagerProvider>();
            }
        }

        public T GetService<T>(Type serviceType)
            where T : class
        {
            ThreadHelper.ThrowIfNotOnUIThread();

            if (Microsoft.VisualStudio.Shell.Package.GetGlobalService(serviceType) is T service)
            {
                return service;
            }

            throw new InvalidOperationException("The Visual Studio service '" + serviceType.Name + "' is not available.");
        }

        // ---- path helpers ----------------------------------------------------------------------------

        /// <summary>Full path, or null when the input is not a usable file system path.</summary>
        public static string? NormalizePath(string? path)
        {
            if (string.IsNullOrWhiteSpace(path))
            {
                return null;
            }

            try
            {
                return Path.GetFullPath(path);
            }
            catch (Exception ex) when (ex is ArgumentException || ex is NotSupportedException || ex is PathTooLongException || ex is System.Security.SecurityException)
            {
                return null;
            }
        }

        public static bool SamePath(string? left, string? right)
        {
            var a = NormalizePath(left);
            var b = NormalizePath(right);
            return a != null && b != null && string.Equals(a, b, StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>True when the (VS) moniker is an absolute file system path.</summary>
        public static bool IsFilePath(string? moniker)
        {
            if (string.IsNullOrWhiteSpace(moniker))
            {
                return false;
            }

            try
            {
                return Path.IsPathRooted(moniker) && moniker!.IndexOf("://", StringComparison.Ordinal) < 0;
            }
            catch (ArgumentException)
            {
                return false;
            }
        }
    }
}
