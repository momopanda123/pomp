using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using EnvDTE;
using Microsoft.VisualStudio;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Shell.Interop;
using Pomp.Adapters;
using Pomp.VisualStudio.Logging;
using Task = System.Threading.Tasks.Task;

namespace Pomp.VisualStudio.Adapters
{
    /// <summary>Reads Output window panes (Build, Debug, General, Pomp, or any pane by its displayed name).</summary>
    internal sealed class OutputWindowAdapter : IOutputWindowAdapter
    {
        private const int MaxLinesLimit = 100000;

        // Visual Studio localizes the built-in pane titles ("Build" is "빌드" in Korean), so English aliases go through the pane GUIDs.
        private static readonly Dictionary<string, Guid> BuiltInPanes = new Dictionary<string, Guid>(StringComparer.OrdinalIgnoreCase)
        {
            ["Build"] = VSConstants.OutputWindowPaneGuid.BuildOutputPane_guid,
            ["Debug"] = VSConstants.OutputWindowPaneGuid.DebugPane_guid,
            ["General"] = VSConstants.OutputWindowPaneGuid.GeneralPane_guid,
            [VsPompLogger.OutputPaneName] = VsPompLogger.OutputPaneGuid,
        };

        private readonly VsAdapterContext context;

        public OutputWindowAdapter(VsAdapterContext context)
        {
            this.context = context;
        }

        public async Task<string?> ReadPaneAsync(string paneName, int maxLines, CancellationToken cancellationToken)
        {
            if (string.IsNullOrWhiteSpace(paneName))
            {
                return null;
            }

            await context.Jtf.SwitchToMainThreadAsync(cancellationToken);

            cancellationToken.ThrowIfCancellationRequested();
            var lineLimit = Math.Max(1, Math.Min(maxLines <= 0 ? 200 : maxLines, MaxLinesLimit));
            var panes = context.Dte.ToolWindows.OutputWindow.OutputWindowPanes;
            var pane = FindPane(panes, ResolveDisplayName(paneName)) ?? FindPane(panes, paneName);
            if (pane == null)
            {
                return null;
            }

            // IVsOutputWindowPane exposes writing and the pane title, but no portable text getter in both SDKs.
            // DTE's TextDocument is the actual Output buffer, including panes created by other extensions.
            var document = pane.TextDocument;
            var endPoint = document.EndPoint;
            // An output ending in a newline has an empty sentinel line at EndPoint. It must not consume one of maxLines.
            var lastLine = endPoint.Line - (endPoint.LineCharOffset == 1 && endPoint.Line > 1 ? 1 : 0);
            var firstLine = Math.Max(1, lastLine - lineLimit + 1);
            var startPoint = document.StartPoint.CreateEditPoint();
            startPoint.MoveToLineAndOffset(firstLine, 1);
            var text = startPoint.GetText(endPoint);
            cancellationToken.ThrowIfCancellationRequested();
            // Strip exactly the last terminator, preserving meaningful empty lines at the end of the pane.
            return text.EndsWith("\r\n", StringComparison.Ordinal) ? text.Substring(0, text.Length - 2)
                : text.EndsWith("\n", StringComparison.Ordinal) || text.EndsWith("\r", StringComparison.Ordinal) ? text.Substring(0, text.Length - 1)
                : text;
        }

        /// <summary>Maps an English alias of a built-in pane to the pane's (possibly localized) title.</summary>
        private string? ResolveDisplayName(string paneName)
        {
            ThreadHelper.ThrowIfNotOnUIThread();

            if (!BuiltInPanes.TryGetValue(paneName.Trim(), out var guid))
            {
                return null;
            }

            try
            {
                if (ErrorHandler.Succeeded(context.OutputWindow.GetPane(ref guid, out var pane)) && pane != null)
                {
                    string? title = null;
                    if (ErrorHandler.Succeeded(pane.GetName(ref title)) && !string.IsNullOrEmpty(title))
                    {
                        return title;
                    }
                }
            }
            catch (Exception ex)
            {
                context.LogUnexpected("Resolving the title of the output pane '" + paneName + "'", ex);
            }

            return null;
        }

        private static OutputWindowPane? FindPane(OutputWindowPanes panes, string? displayName)
        {
            ThreadHelper.ThrowIfNotOnUIThread();

            if (string.IsNullOrWhiteSpace(displayName))
            {
                return null;
            }

            foreach (OutputWindowPane pane in panes)
            {
                if (string.Equals(pane.Name, displayName!.Trim(), StringComparison.OrdinalIgnoreCase))
                {
                    return pane;
                }
            }

            return null;
        }
    }
}
