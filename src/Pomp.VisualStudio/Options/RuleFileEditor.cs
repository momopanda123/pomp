using System;
using System.ComponentModel;
using System.Drawing.Design;
using System.IO;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Shell.Interop;
using Pomp.Diagnostics;
using Pomp.VisualStudio.Adapters;

namespace Pomp.VisualStudio.Options
{
    /// <summary>Explicit file selection opens a rule in the normal VS editor; no content is created or rewritten.</summary>
    public sealed class RuleFileEditor : UITypeEditor
    {
        public override UITypeEditorEditStyle GetEditStyle(ITypeDescriptorContext context) => UITypeEditorEditStyle.Modal;
        public override object EditValue(ITypeDescriptorContext context, IServiceProvider provider, object value)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            try
            {
                var root = ThreadHelper.JoinableTaskFactory.Run(() => VsAdapterContext.ResolveWorkspaceRootAsync(ThreadHelper.JoinableTaskFactory, NullPompLogger.Instance, default));
                var rules = Path.Combine(root, ".pomp", "rules");
                var dialog = new Microsoft.Win32.OpenFileDialog
                {
                    Title = "Choose a scoped POMP rule to edit",
                    Filter = "POMP rule files (*.mdc)|*.mdc",
                    InitialDirectory = Directory.Exists(rules) ? rules : root,
                    Multiselect = false, CheckFileExists = true, ValidateNames = true,
                };
                if (value is string previous && File.Exists(previous) && string.Equals(Path.GetExtension(previous), ".mdc", StringComparison.OrdinalIgnoreCase)) dialog.FileName = previous;
                if (dialog.ShowDialog() != true) return value;
                if (!string.Equals(Path.GetExtension(dialog.FileName), ".mdc", StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException("Choose an existing .mdc rule file.");
                VsShellUtilities.OpenDocument(ServiceProvider.GlobalProvider, dialog.FileName);
                return dialog.FileName;
            }
            catch (Exception ex)
            {
                VsShellUtilities.ShowMessageBox(ServiceProvider.GlobalProvider, "Could not open the rule file: " + ex.Message,
                    "POMP rules", OLEMSGICON.OLEMSGICON_WARNING, OLEMSGBUTTON.OLEMSGBUTTON_OK, OLEMSGDEFBUTTON.OLEMSGDEFBUTTON_FIRST);
                return value;
            }
        }
    }
}
