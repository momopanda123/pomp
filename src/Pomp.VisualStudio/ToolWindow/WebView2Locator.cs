using System;
using System.IO;
using System.Reflection;
using Microsoft.Web.WebView2.Core;
using Pomp.Diagnostics;
using Pomp.VisualStudio.Interop;

namespace Pomp.VisualStudio.ToolWindow
{
    /// <summary>Outcome of <see cref="WebView2Locator.Probe"/>.</summary>
    internal sealed class WebView2Probe
    {
        public bool RuntimeInstalled { get; set; }

        public string? RuntimeVersion { get; set; }

        /// <summary>Set when the probe failed for a reason other than "runtime not installed" (e.g. loader missing).</summary>
        public Exception? Error { get; set; }
    }

    /// <summary>
    /// Finds the WebView2 runtime and the native WebView2Loader.dll.
    /// <para>
    /// VS 2022/2026 redirect Microsoft.Web.WebView2.* to their own newer copy (loaded from PrivateAssemblies, with
    /// its loader next to it): nothing to do. VS 2017/2019 load the copy shipped in this VSIX, whose loader lives in
    /// <c>runtimes\win-x86\native</c> next to it; Core 1.0.1901.177 probes exactly there on .NET Framework, and
    /// the loader folder is also set explicitly (reflection: the API name differs between WebView2 versions) as a belt-and-braces measure.
    /// </para>
    /// </summary>
    internal static class WebView2Locator
    {
        /// <summary>Evergreen WebView2 Runtime bootstrapper download.</summary>
        public const string RuntimeDownloadUrl = "https://go.microsoft.com/fwlink/p/?LinkId=2124703";

        /// <summary>
        /// Oldest WebView2 Runtime the chat page supports: the page's styles use <c>color-mix()</c>, which arrived with Chromium 111.
        /// An older runtime would render the chat with broken colors, so the fallback panel (with the download link) is shown instead.
        /// </summary>
        public const string MinimumRuntimeVersion = "111.0.1661.41";

        private const string LoaderFileName = "WebView2Loader.dll";
        private static readonly object Gate = new object();
        private static bool prepared;

        /// <summary>Idempotent; call before the first WebView2 API use.</summary>
        public static void PrepareLoader(IPompLogger logger, string extensionDirectory)
        {
            lock (Gate)
            {
                if (prepared)
                {
                    return;
                }

                prepared = true;
            }

            try
            {
                var coreAssembly = typeof(CoreWebView2Environment).Assembly;
                var coreLocation = SafeLocation(coreAssembly);
                logger.Info($"WebView2 managed assembly: {coreAssembly.GetName().Version} ({coreLocation ?? "location unknown"}).");

                var alreadyLoaded = NativeMethods.GetLoadedModulePath(LoaderFileName);
                if (alreadyLoaded != null)
                {
                    logger.Info("WebView2Loader.dll is already loaded from " + alreadyLoaded + ".");
                }

                var usingOwnCopy = coreLocation != null && IsUnder(coreLocation, extensionDirectory);
                if (!usingOwnCopy)
                {
                    logger.Info("Using the WebView2 assemblies provided by Visual Studio; loader resolution is left to them.");
                    return;
                }

                var arch = Environment.Is64BitProcess ? "x64" : "x86";
                var loaderFolder = Path.Combine(extensionDirectory, "runtimes", "win-" + arch, "native");
                var loaderPath = Path.Combine(loaderFolder, LoaderFileName);
                if (!File.Exists(loaderPath))
                {
                    logger.Error($"The {arch} {LoaderFileName} is missing from the extension folder ({loaderPath}); the chat UI cannot start.");
                    return;
                }

                if (alreadyLoaded == null && TrySetLoaderFolder(loaderFolder))
                {
                    logger.Info("WebView2 loader folder set to " + loaderFolder + ".");
                }
            }
            catch (Exception ex)
            {
                logger.Warn("Could not prepare the WebView2 loader.", ex);
            }
        }

        /// <summary>
        /// Points the managed WebView2 at the loader folder. Done through reflection because the API differs between versions
        /// (1.0.1245.22 has the static property LoaderDllFolderPath, newer copies a SetLoaderDllFolderPath method) and a missing
        /// member referenced directly would fail the JIT of the whole calling method.
        /// </summary>
        private static bool TrySetLoaderFolder(string loaderFolder)
        {
            var environmentType = typeof(CoreWebView2Environment);

            var setter = environmentType.GetMethod("SetLoaderDllFolderPath", BindingFlags.Public | BindingFlags.Static, null, new[] { typeof(string) }, null);
            if (setter != null)
            {
                setter.Invoke(null, new object[] { loaderFolder });
                return true;
            }

            var property = environmentType.GetProperty("LoaderDllFolderPath", BindingFlags.Public | BindingFlags.Static);
            if (property != null && property.CanWrite)
            {
                var current = property.CanRead ? property.GetValue(null, null) as string : null;
                if (string.IsNullOrEmpty(current))
                {
                    property.SetValue(null, loaderFolder, null);
                }

                return true;
            }

            return false;
        }

        /// <summary>Checks that a WebView2 runtime is installed (and that the loader can be loaded to ask).</summary>
        public static WebView2Probe Probe()
        {
            try
            {
                var version = CoreWebView2Environment.GetAvailableBrowserVersionString();
                return new WebView2Probe
                {
                    RuntimeInstalled = !string.IsNullOrEmpty(version),
                    RuntimeVersion = version,
                };
            }
            catch (WebView2RuntimeNotFoundException)
            {
                return new WebView2Probe { RuntimeInstalled = false };
            }
            catch (Exception ex)
            {
                return new WebView2Probe { RuntimeInstalled = false, Error = ex };
            }
        }

        /// <summary>
        /// True when <paramref name="runtimeVersion"/> (as reported by the runtime probe) is older than <see cref="MinimumRuntimeVersion"/>,
        /// compared with <c>CoreWebView2Environment.CompareBrowserVersions</c>. A version string that cannot be compared is not treated as
        /// too old: the page is tried instead of blocking a working runtime on a parsing quirk.
        /// </summary>
        public static bool IsRuntimeTooOld(string? runtimeVersion)
        {
            if (string.IsNullOrEmpty(runtimeVersion))
            {
                return false;
            }

            try
            {
                return CoreWebView2Environment.CompareBrowserVersions(runtimeVersion!, MinimumRuntimeVersion) < 0;
            }
            catch (Exception)
            {
                return false;
            }
        }

        /// <summary>User-data folder %LOCALAPPDATA%\Pomp\WebView2\VS&lt;major&gt;.</summary>
        public static string GetUserDataFolder(string dataDirectory, int vsMajorVersion) =>
            Path.Combine(dataDirectory, "WebView2", "VS" + (vsMajorVersion > 0 ? vsMajorVersion.ToString() : "0"));

        private static string? SafeLocation(System.Reflection.Assembly assembly)
        {
            try
            {
                return string.IsNullOrEmpty(assembly.Location) ? null : assembly.Location;
            }
            catch (NotSupportedException)
            {
                return null;
            }
        }

        private static bool IsUnder(string path, string directory)
        {
            var normalizedDirectory = Path.GetFullPath(directory).TrimEnd('\\') + "\\";
            return Path.GetFullPath(path).StartsWith(normalizedDirectory, StringComparison.OrdinalIgnoreCase);
        }
    }
}
