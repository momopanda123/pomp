using System;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Shell.Interop;
using Microsoft.VisualStudio.Threading;
using Pomp.VisualStudio.Commands;
using Pomp.VisualStudio.Composition;
using Pomp.VisualStudio.Logging;
using Pomp.VisualStudio.Options;
using Pomp.VisualStudio.ToolWindow;
using Task = System.Threading.Tasks.Task;

namespace Pomp.VisualStudio
{
    /// <summary>
    /// Pomp's Visual Studio package: registers the chat tool window, the commands (Pomp.vsct), the options page and the
    /// binding path, and owns <see cref="PompServices"/>. Loads in the background (async package).
    /// </summary>
    [PackageRegistration(UseManagedResourcesOnly = true, AllowsBackgroundLoading = true)]
    [InstalledProductRegistration("Pomp", "AI coding partner for Visual Studio.", "0.5.4")]
    [ProvideMenuResource("Menus.ctmenu", 1)]
    [ProvideBindingPath]
    // The chat opens as a tab of the Solution Explorer group (right edge, full height), like other assistant panes.
    [ProvideToolWindow(typeof(PompChatToolWindow), Style = VsDockStyle.Tabbed, Window = ToolWindowGuids80.SolutionExplorer)]
    [ProvideToolWindow(typeof(PompDiffToolWindow), Style = VsDockStyle.Tabbed, Window = "DocumentWell")]
    [ProvideToolWindow(typeof(PompPreviewToolWindow), Style = VsDockStyle.Tabbed, Window = "DocumentWell", MultiInstances = true, Transient = true)]
    [ProvideOptionPage(typeof(PompOptionsPage), "Pomp", "General", 0, 110, true)]
    [Guid(PompGuids.PackageGuidString)]
    public sealed class PompPackage : AsyncPackage
    {
        private readonly TaskCompletionSource<PompServices> servicesReady =
            new TaskCompletionSource<PompServices>(TaskCreationOptions.RunContinuationsAsynchronously);

        private GlobalExceptionLogger? exceptionLogger;
        private CursorVisibilityGuard? cursorGuard;
        private PompServices? services;

        /// <inheritdoc />
        protected override async Task InitializeAsync(CancellationToken cancellationToken, IProgress<ServiceProgressData> progress)
        {
            await base.InitializeAsync(cancellationToken, progress);

            try
            {
                // Everything below touches Visual Studio services: services, adapters and commands are created on the UI thread.
                await JoinableTaskFactory.SwitchToMainThreadAsync(cancellationToken);

                var created = await PompServices.CreateAsync(this, cancellationToken);
                services = created;
                servicesReady.TrySetResult(created);

                exceptionLogger = new GlobalExceptionLogger(created.Logger);
                exceptionLogger.Install();

                cursorGuard = new CursorVisibilityGuard(created.Logger);
                cursorGuard.Install();

                await PompCommands.InitializeAsync(this, created, cancellationToken);
            }
            catch (Exception ex)
            {
                servicesReady.TrySetException(ex);
                throw;
            }
        }

        /// <inheritdoc />
        public override IVsAsyncToolWindowFactory? GetAsyncToolWindowFactory(Guid toolWindowType)
        {
            return toolWindowType.Equals(new Guid(PompGuids.ToolWindowGuidString)) ? this : null;
        }

        /// <inheritdoc />
        protected override string GetToolWindowTitle(Type toolWindowType, int id)
        {
            return toolWindowType == typeof(PompChatToolWindow) ? "POMP" : base.GetToolWindowTitle(toolWindowType, id);
        }

        /// <inheritdoc />
        protected override async Task<object?> InitializeToolWindowAsync(Type toolWindowType, int id, CancellationToken cancellationToken)
        {
            // The tool window can be restored from the saved layout while InitializeAsync is still running.
            var ready = await servicesReady.Task.WithCancellation(cancellationToken);
            return new PompToolWindowState(ready);
        }

        /// <inheritdoc />
        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                exceptionLogger?.Dispose();
                exceptionLogger = null;

                cursorGuard?.Dispose();
                cursorGuard = null;

                services?.Dispose();
                services = null;
            }

            base.Dispose(disposing);
        }
    }
}
