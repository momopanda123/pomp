using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Shell.Interop;
using Pomp.VisualStudio.ToolWindow;
using Pomp.Diagnostics;
using Pomp.Hosting;
using Task = System.Threading.Tasks.Task;
namespace Pomp.VisualStudio.Hosting
{
    internal sealed partial class VsHostEnvironment
    {
        private IVsInfoBarUIElement? taskNotice;
        private uint taskNoticeCookie;
        private string? statusText;
        public async Task UpdateBackgroundTasksAsync(string summary, string? notice, CancellationToken cancellationToken)
        {
            await joinableTaskFactory.SwitchToMainThreadAsync(cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            if (Package.GetGlobalService(typeof(SVsStatusbar)) is IVsStatusbar statusbar)
            {
                if (summary.Length > 0)
                {
                    statusbar.SetText(summary);
                    statusText = summary;
                }
                else if (statusText != null)
                {
                    // The status bar is shared: clear it only while it still shows POMP's own text.
                    if (statusbar.GetText(out var current) == 0 && current == statusText) statusbar.SetText(string.Empty);
                    statusText = null;
                }
            }

            if (notice == null) return;
            CloseTaskNotice();
            // The chat already shows the ended jobs while the POMP window is on screen: the bar is only for a hidden window.
            if (notice.Length == 0 || IsChatOnScreen()) return;
            var shell = Package.GetGlobalService(typeof(SVsShell)) as IVsShell;
            if (shell == null || shell.GetProperty((int)__VSSPROPID7.VSSPROPID_MainWindowInfoBarHost, out var value) != 0 || !(value is IVsInfoBarHost host)) return;
            var factory = Package.GetGlobalService(typeof(SVsInfoBarUIFactory)) as IVsInfoBarUIFactory;
            if (factory == null) return;
            var model = new InfoBarModel(new[] { new InfoBarTextSpan(notice) }, new[] { new InfoBarHyperlink(PompLanguage.Pick("POMP 열기", "Open POMP")) }, default, true);
            taskNotice = factory.CreateInfoBar(model);
            taskNotice.Advise(new TaskNoticeEvents(this, cancellationToken), out taskNoticeCookie);
            host.AddInfoBar(taskNotice);
        }
        private bool IsChatOnScreen()
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            return package.FindToolWindow(typeof(PompChatToolWindow), 0, false)?.Frame is IVsWindowFrame frame
                && frame.IsOnScreen(out var onScreen) == 0 && onScreen != 0;
        }
        private void CloseTaskNotice()
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            var previous = taskNotice; taskNotice = null;
            if (previous == null) return;
            previous.Unadvise(taskNoticeCookie); taskNoticeCookie = 0; previous.Close();
        }
        private sealed class TaskNoticeEvents : IVsInfoBarUIEvents
        {
            private readonly VsHostEnvironment owner; private readonly CancellationToken token;
            public TaskNoticeEvents(VsHostEnvironment owner, CancellationToken token) { this.owner = owner; this.token = token; }
            public void OnClosed(IVsInfoBarUIElement element)
            {
                ThreadHelper.ThrowIfNotOnUIThread();
                if (!ReferenceEquals(owner.taskNotice, element)) return;
                element.Unadvise(owner.taskNoticeCookie); owner.taskNotice = null; owner.taskNoticeCookie = 0;
            }
            public void OnActionItemClicked(IVsInfoBarUIElement element, IVsInfoBarActionItem actionItem)
            {
                ThreadHelper.ThrowIfNotOnUIThread();
                owner.CloseTaskNotice();
                if (token.IsCancellationRequested) return;
                _ = owner.joinableTaskFactory.RunAsync(async () =>
                {
                    try { await owner.package.ShowToolWindowAsync(typeof(PompChatToolWindow), 0, true, token); }
                    catch (OperationCanceledException) { }
                    catch (Exception ex) { owner.logger.Warn("Could not show background task results.", ex); }
                });
            }
        }
    }
}
