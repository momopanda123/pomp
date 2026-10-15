using System;
using System.Collections.Generic;
using System.Threading;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Shell.Interop;
using Pomp.VisualStudio.ToolWindow;
using Task = System.Threading.Tasks.Task;

namespace Pomp.VisualStudio.Hosting
{
    internal sealed partial class VsHostEnvironment : Pomp.Features.IFilePreviewPresenter
    {
        // One preview tab per file: instance ids handed out by this session, keyed by the file they show.
        private readonly Dictionary<string, int> previewTabs = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        private int nextPreviewTab;

        public async Task ShowPreviewAsync(string fullPath, string workspaceRoot, CancellationToken cancellationToken)
        {
            await joinableTaskFactory.SwitchToMainThreadAsync(cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            PompPreviewToolWindow? window = null;
            if (previewTabs.TryGetValue(fullPath, out var id))
            {
                // A tab the user closed is gone (transient window): find returns null and a new tab is made below.
                window = await package.FindToolWindowAsync(typeof(PompPreviewToolWindow), id, false, cancellationToken) as PompPreviewToolWindow;
                if (window != null && !string.Equals(window.File, fullPath, StringComparison.OrdinalIgnoreCase)) window = null;
            }

            if (window == null)
            {
                id = nextPreviewTab++;
                window = await package.FindToolWindowAsync(typeof(PompPreviewToolWindow), id, true, cancellationToken) as PompPreviewToolWindow
                         ?? throw new InvalidOperationException("The preview window could not be created.");
                previewTabs[fullPath] = id;
            }

            // The WebView2 control starts only once it sits in a shown window, so the tab is shown before the file is loaded into it.
            if (window.Frame is IVsWindowFrame frame) Microsoft.VisualStudio.ErrorHandler.ThrowOnFailure(frame.Show());
            await window.ShowAsync(package, logger, DataDirectory, fullPath, workspaceRoot);
        }
    }
}
