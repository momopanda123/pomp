using System;
using System.Threading;
using Task = System.Threading.Tasks.Task;
using Microsoft.VisualStudio.ComponentModelHost;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Text;
using Microsoft.VisualStudio.Text.Differencing;
using Microsoft.VisualStudio.Utilities;
using Pomp.VisualStudio.ToolWindow;
namespace Pomp.VisualStudio.Hosting
{
    internal sealed partial class VsHostEnvironment
    {
        public async Task ShowWorkspaceDiffAsync(string path, string before, string after, long revision, CancellationToken cancellationToken)
        {
            await joinableTaskFactory.SwitchToMainThreadAsync(cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            var model = Package.GetGlobalService(typeof(SComponentModel)) as IComponentModel ?? throw new InvalidOperationException("Visual Studio editor composition is unavailable.");
            var window = await package.ShowToolWindowAsync(typeof(PompDiffToolWindow), 0, true, cancellationToken) as PompDiffToolWindow ?? throw new InvalidOperationException("Native diff window could not be opened.");
            cancellationToken.ThrowIfCancellationRequested();
            window.ShowSnapshot(model.GetService<ITextBufferFactoryService>(), model.GetService<IContentTypeRegistryService>(), model.GetService<IDifferenceBufferFactoryService>(), model.GetService<IWpfDifferenceViewerFactoryService>(), path, before, after, revision);
        }
    }
}
