using System.Linq;
using System;
using System.Collections.Generic;
using System.IO;
using Pomp.Protocol;

namespace Pomp.Hosting
{
    /// <summary>
    /// The omp command-line arguments that load pomp's omp-side package (pomp-context extension, pre-edit guard, vs-project skill).
    /// Kept in one small class on purpose: it is the single place that knows how omp is told about the package.
    /// </summary>
    /// <remarks>
    /// The package is loaded as a directory: <c>-e &lt;absolute path of omp-extension&gt;</c> (two argv elements). omp reads the
    /// <c>omp.extensions</c> manifest of its <c>package.json</c> and discovers <c>skills/</c> only from a directory root, so
    /// individual <c>.ts</c> files must not be passed (and <c>--hook</c> is merely an alias, so it is not used). Ordering rule
    /// (see <see cref="OmpLaunchArguments"/>): omp's argument parser can glue following non-flag tokens onto a <c>-e</c> value that has
    /// no <c>.ts</c>/<c>.js</c> suffix, so the element after the directory must be a flag (<c>--...</c>) or the directory must be last.
    /// </remarks>
    public static class OmpExtensionArguments
    {
        /// <summary>Name of the package folder inside the extension's install directory.</summary>
        public const string PackageDirectoryName = "omp-extension";

        /// <summary>
        /// <c>["-e", "&lt;extension root&gt;\omp-extension"]</c> when that folder holds a <c>package.json</c>; empty otherwise.
        /// </summary>
        /// <param name="extensionRoot">The install folder of the extension (<see cref="Pomp.Hosting.IPompHostEnvironment.ExtensionDirectory"/>);
        /// the <c>omp-extension</c> folder itself is accepted too.</param>
        public static IReadOnlyList<string> Build(string extensionRoot)
        {
            var arguments = new List<string>();
            var directory = FindPackageDirectory(extensionRoot);
            if (directory != null)
            {
                arguments.Add("-e");
                arguments.Add(directory);
            }

            return arguments;
        }

        /// <summary>Require the shipped module in the installed package manifest, not merely a nonempty -e argument.</summary>
        internal static bool ContainsModule(string? extensionRoot, string relative)
        {
            try
            {
                var directory = FindPackageDirectory(extensionRoot); if (directory == null) return false;
                var file = Path.Combine(directory, "package.json");
                Pomp.Features.CheckpointStore.CheckDirectPath(file);
                if (new FileInfo(file).Length > 65536) return false;
                var manifest = Newtonsoft.Json.Linq.JObject.Parse(File.ReadAllText(file));
                if (!(manifest["omp"]?["extensions"] is Newtonsoft.Json.Linq.JArray modules) || !modules.Any(m => m.Type == Newtonsoft.Json.Linq.JTokenType.String && (string?)m == relative)) return false;
                var target = Path.Combine(directory, relative.Replace('/', Path.DirectorySeparatorChar));
                Pomp.Features.CheckpointStore.CheckDirectPath(target);
                return File.Exists(target);
            }
            catch { return false; }
        }

        /// <summary>Absolute path of the omp-side package folder (the one with <c>package.json</c>), or null when it is not installed.</summary>
        public static string? FindPackageDirectory(string? extensionRoot)
        {
            if (string.IsNullOrWhiteSpace(extensionRoot))
            {
                return null;
            }

            try
            {
                var root = Path.GetFullPath(extensionRoot!);
                var nested = Path.Combine(root, PackageDirectoryName);
                if (File.Exists(Path.Combine(nested, "package.json")))
                {
                    return nested;
                }

                if (File.Exists(Path.Combine(root, "package.json"))
                    && string.Equals(Path.GetFileName(root.TrimEnd('\\', '/')), PackageDirectoryName, StringComparison.OrdinalIgnoreCase))
                {
                    return root.TrimEnd('\\', '/');
                }
            }
            catch (Exception ex) when (ex is ArgumentException || ex is NotSupportedException || ex is PathTooLongException || ex is System.Security.SecurityException)
            {
                // An unusable path simply means "not installed".
            }

            return null;
        }
    }

    /// <summary>Assembles the omp command line (architecture.md §4).</summary>
    internal static class OmpLaunchArguments
    {
        /// <summary>omp flag that turns off its own extension/hook discovery; explicit <c>-e</c> paths still load.</summary>
        public const string NoExtensionDiscovery = "--no-extensions";

        /// <summary>
        /// <c>--mode rpc|rpc-ui --cwd &lt;root&gt; --approval-mode &lt;mode&gt; --config &lt;overlay&gt; [--session-dir &lt;dir&gt;] [--plugin-dir &lt;dir&gt;] [--no-extensions] [extension args] [--model m] [--thinking t] [extra...]</c>.
        /// Every element is its own argv entry (quoting is the launcher's job). The extension arguments keep their place unless the
        /// element following them would not be a flag; then they move to the very end, so a <c>-e</c> directory value is never followed
        /// by a bare token that omp's parser could glue onto it.
        /// </summary>
        /// <param name="loadDiscoveredExtensions">False adds <see cref="NoExtensionDiscovery"/>: omp then loads no extensions or hooks
        /// from the user profile, installed plugins or the workspace's <c>.omp</c> folder (they run unsandboxed), only pomp's own package.</param>
        /// <param name="sessionDirectory">Where omp keeps and looks up the conversation files (the workspace's <c>.pomp/local/sessions</c>).</param>
        /// <param name="pluginDirectory">The copy of the project's roles omp reads as a plugin folder (<c>.pomp/local/engine-plugin</c>).</param>
        public static List<string> Build(
            bool rpcUiMode,
            string workingDirectory,
            OmpApprovalMode approvalMode,
            string overlayPath,
            bool loadDiscoveredExtensions,
            IReadOnlyList<string> extensionArguments,
            string? model,
            string? thinkingLevel,
            IReadOnlyList<string> extraArguments,
            string? sessionDirectory = null,
            string? pluginDirectory = null)
        {
            var arguments = new List<string>
            {
                "--mode", rpcUiMode ? "rpc-ui" : "rpc",
                "--cwd", workingDirectory,
                "--approval-mode", approvalMode.ToCliValue(),
                "--config", overlayPath,
            };

            if (!string.IsNullOrEmpty(sessionDirectory))
            {
                arguments.Add("--session-dir");
                arguments.Add(sessionDirectory!);
            }

            if (!string.IsNullOrEmpty(pluginDirectory))
            {
                arguments.Add("--plugin-dir");
                arguments.Add(pluginDirectory!);
            }

            if (!loadDiscoveredExtensions)
            {
                arguments.Add(NoExtensionDiscovery);
            }

            var tail = new List<string>();
            if (!string.IsNullOrWhiteSpace(model))
            {
                tail.Add("--model");
                tail.Add(model!.Trim());
            }

            if (!string.IsNullOrWhiteSpace(thinkingLevel))
            {
                tail.Add("--thinking");
                tail.Add(thinkingLevel!.Trim());
            }

            tail.AddRange(extraArguments);

            var extensionFirst = tail.Count == 0 || tail[0].StartsWith("--", StringComparison.Ordinal);
            if (extensionFirst)
            {
                arguments.AddRange(extensionArguments);
                arguments.AddRange(tail);
            }
            else
            {
                arguments.AddRange(tail);
                arguments.AddRange(extensionArguments);
            }

            return arguments;
        }
    }
}
