using System;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Shell.Interop;
using Microsoft.VisualStudio.Threading;
using Pomp.Diagnostics;
using Pomp.Hosting;
using Pomp.VisualStudio.Interop;
using Pomp.VisualStudio.Logging;
using Pomp.VisualStudio.Options;
using Task = System.Threading.Tasks.Task;

namespace Pomp.VisualStudio.Hosting
{
    /// <summary>Facts about the hosting Visual Studio and the installed extension (see <see cref="IPompHostEnvironment"/>).</summary>
    internal sealed partial class VsHostEnvironment : IPompHostEnvironment, IPompFilePicker, Pomp.Features.IBackgroundTaskNotifications, Pomp.Features.IWorkspaceDiffPresenter
    {
        private readonly AsyncPackage package;
        private readonly JoinableTaskFactory joinableTaskFactory;
        private readonly VsPompLogger logger;

        public VsHostEnvironment(AsyncPackage package, JoinableTaskFactory joinableTaskFactory, VsPompLogger logger)
        {
            this.package = package;
            this.joinableTaskFactory = joinableTaskFactory;
            this.logger = logger;

            var assembly = typeof(VsHostEnvironment).Assembly;
            HostVersion = ReadHostVersion(assembly);
            ExtensionDirectory = ResolveExtensionDirectory(assembly);
            DataDirectory = ResolveDataDirectory();
            VsVersion = ReadVsVersion();
        }

        /// <inheritdoc />
        public string HostVersion { get; }

        /// <inheritdoc />
        public string VsVersion { get; }

        /// <summary>Major version of the hosting Visual Studio (16, 17, 18...), 0 when unknown.</summary>
        public int VsMajorVersion => Version.TryParse(VsVersion, out var version) ? version.Major : 0;

        /// <inheritdoc />
        public string ExtensionDirectory { get; }

        /// <inheritdoc />
        public string DataDirectory { get; }

        /// <summary>Folder that fallback workspaces use when nothing is open.</summary>
        public string FallbackWorkspaceDirectory => Pomp.VisualStudio.Adapters.VsAdapterContext.FallbackWorkspaceDirectory;

        /// <inheritdoc />
        public Task<string> GetWorkspaceRootAsync(CancellationToken cancellationToken) =>
            Pomp.VisualStudio.Adapters.VsAdapterContext.ResolveWorkspaceRootAsync(joinableTaskFactory, logger, cancellationToken);

        /// <inheritdoc />
        public async Task<System.Collections.Generic.IReadOnlyList<string>> PickFilesAsync(string? initialDirectory, CancellationToken cancellationToken)
        {
            await joinableTaskFactory.SwitchToMainThreadAsync(cancellationToken);

            var dialog = new Microsoft.Win32.OpenFileDialog
            {
                Title = PompLanguage.Pick("POMP 메시지에 첨부할 파일", "Attach files to the POMP message"),
                Multiselect = true,
                CheckFileExists = true,
                ValidateNames = true,
                DereferenceLinks = true,
            };
            if (!string.IsNullOrEmpty(initialDirectory) && Directory.Exists(initialDirectory))
            {
                dialog.InitialDirectory = initialDirectory;
            }

            // Modal over Visual Studio's main window; VS must not start other modal UI meanwhile.
            var shell = Package.GetGlobalService(typeof(SVsUIShell)) as IVsUIShell;
            shell?.EnableModeless(0);
            try
            {
                var owner = shell != null && shell.GetDialogOwnerHwnd(out var hwnd) == 0 && hwnd != IntPtr.Zero
                    ? System.Windows.Interop.HwndSource.FromHwnd(hwnd)?.RootVisual as System.Windows.Window
                    : null;
                var accepted = owner != null ? dialog.ShowDialog(owner) : dialog.ShowDialog();
                return accepted == true ? dialog.FileNames : Array.Empty<string>();
            }
            finally
            {
                shell?.EnableModeless(1);
            }
        }

        /// <inheritdoc />
        public void ShowLog()
        {
            _ = joinableTaskFactory.RunAsync(async () =>
            {
                try
                {
                    await joinableTaskFactory.SwitchToMainThreadAsync();
                    ShowOutputWindow();
                    logger.ActivatePane();
                }
                catch (Exception ex)
                {
                    logger.Warn("Could not show the Pomp output pane.", ex);
                }
            });
        }

        /// <inheritdoc />
        public void ShowSettings()
        {
            _ = joinableTaskFactory.RunAsync(async () =>
            {
                try
                {
                    await joinableTaskFactory.SwitchToMainThreadAsync();
                    package.ShowOptionPage(typeof(PompOptionsPage));
                }
                catch (Exception ex)
                {
                    logger.Warn("Could not open the Pomp options page.", ex);
                }
            });
        }

        private static void ShowOutputWindow()
        {
            ThreadHelper.ThrowIfNotOnUIThread();

            if (!(Package.GetGlobalService(typeof(SVsUIShell)) is IVsUIShell shell))
            {
                return;
            }

            // {34E76E81-EE4A-11D0-AE2E-00A0C90FFFC3} is the Output tool window.
            var outputWindowGuid = new Guid("34E76E81-EE4A-11D0-AE2E-00A0C90FFFC3");
            if (shell.FindToolWindow((uint)__VSFINDTOOLWIN.FTW_fForceCreate, ref outputWindowGuid, out var frame) == 0)
            {
                frame?.Show();
            }
        }

        private static string ReadHostVersion(Assembly assembly)
        {
            var informational = assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
            if (!string.IsNullOrWhiteSpace(informational))
            {
                // Strip SemVer build metadata (+hash) if any.
                var plus = informational!.IndexOf('+');
                return plus > 0 ? informational.Substring(0, plus) : informational;
            }

            var version = assembly.GetName().Version;
            return version == null ? "0.0.0" : $"{version.Major}.{version.Minor}.{version.Build}";
        }

        private static string ResolveExtensionDirectory(Assembly assembly)
        {
            try
            {
                if (!string.IsNullOrEmpty(assembly.Location))
                {
                    return Path.GetDirectoryName(assembly.Location)!;
                }

                var codeBase = new Uri(assembly.CodeBase);
                return Path.GetDirectoryName(codeBase.LocalPath)!;
            }
            catch (Exception)
            {
                return AppDomain.CurrentDomain.BaseDirectory;
            }
        }

        internal static string ResolveDataDirectory()
        {
            var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            return Path.Combine(localAppData, "Pomp");
        }

        private static string ReadVsVersion()
        {
            try
            {
                using (var process = Process.GetCurrentProcess())
                {
                    var fileName = process.MainModule?.FileName;
                    if (!string.IsNullOrEmpty(fileName))
                    {
                        var info = FileVersionInfo.GetVersionInfo(fileName);
                        if (!string.IsNullOrWhiteSpace(info.ProductVersion))
                        {
                            return $"{info.ProductMajorPart}.{info.ProductMinorPart}.{info.ProductBuildPart}.{info.ProductPrivatePart}";
                        }
                    }
                }
            }
            catch (Exception)
            {
                // Fall through to the DTE-independent default below.
            }

            return "0.0.0.0";
        }
    }
}
