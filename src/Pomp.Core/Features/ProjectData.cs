using System;
using System.IO;
using Pomp.Internal;

namespace Pomp.Features
{
    /// <summary>
    /// Where a workspace keeps what POMP knows about it: everything lives in <c>&lt;root&gt;/.pomp/</c>, so deleting that one folder resets
    /// POMP for the project. The shared project files (rules, workflows, skills, hooks, teams, agents, permissions.json, knowledge.json) sit directly in
    /// it; POMP's own data (conversations, presets, checkpoints, ...) in <c>.pomp/local/</c>, which its own <c>.gitignore</c>
    /// keeps out of commits. Data an earlier POMP kept for the workspace under <c>%LOCALAPPDATA%\Pomp</c> is moved (not copied) on first
    /// use, so it does not come back after the folder is deleted. Workspace trust and remembered tool approvals stay outside the project:
    /// a repository must not be able to ship them.
    /// </summary>
    internal static class ProjectData
    {
        internal const string FolderName = ".pomp";
        internal const string LocalName = "local";

        /// <summary><c>&lt;root&gt;/.pomp</c>: the full root without a trailing separator.</summary>
        internal static string ProjectFolder(string workspaceRoot) => Path.Combine(Path.GetFullPath(workspaceRoot).TrimEnd('\\', '/'), FolderName);

        /// <summary><c>&lt;root&gt;/.pomp/local</c>: POMP's own data for the workspace.</summary>
        internal static string LocalFolder(string workspaceRoot) => Path.Combine(ProjectFolder(workspaceRoot), LocalName);

        /// <summary>Whether a folder met while scanning a workspace is a <c>.pomp/local</c> (scans leave it out; the rest of <c>.pomp</c> is the project's).</summary>
        internal static bool IsLocalFolder(string directory) =>
            string.Equals(Path.GetFileName(directory), LocalName, StringComparison.OrdinalIgnoreCase) && string.Equals(Path.GetFileName(Path.GetDirectoryName(directory)), FolderName, StringComparison.OrdinalIgnoreCase);

        /// <summary>
        /// A data file of the workspace (<c>.pomp/local/&lt;name&gt;</c>). When it does not exist yet and <paramref name="legacyFile"/>
        /// (where an earlier POMP kept it) does, that file is moved here first; its <c>.lock</c> lease file is dropped.
        /// </summary>
        internal static string File(string workspaceRoot, string name, string? legacyFile = null)
        {
            var file = Path.Combine(Prepare(workspaceRoot), name);
            if (legacyFile != null && !System.IO.File.Exists(file) && System.IO.File.Exists(legacyFile))
            {
                // Another VS may move it at the same moment: whichever move loses leaves the winner's file in place.
                try { System.IO.File.Move(legacyFile, file); } catch (IOException) { } catch (UnauthorizedAccessException) { }
                try { System.IO.File.Delete(legacyFile + ".lock"); } catch (IOException) { } catch (UnauthorizedAccessException) { }
            }
            return file;
        }

        /// <summary>A data folder of the workspace (<c>.pomp/local/&lt;name&gt;</c>), taking over <paramref name="legacyFolder"/> like <see cref="File"/>.</summary>
        internal static string Folder(string workspaceRoot, string name, string? legacyFolder = null)
        {
            var folder = Path.Combine(Prepare(workspaceRoot), name);
            if (legacyFolder != null && !Directory.Exists(folder) && Directory.Exists(legacyFolder))
            {
                try { MoveFolder(legacyFolder, folder); } catch (IOException) { } catch (UnauthorizedAccessException) { }
            }
            return folder;
        }

        /// <summary>
        /// The workspace's conversation folder (<c>.pomp/local/sessions</c>, omp's <c>--session-dir</c>), taking over the folder omp kept
        /// them in before. <paramref name="movedFrom"/> is that old folder when its conversations were moved now, else null.
        /// </summary>
        internal static string Sessions(string workspaceRoot, string userHome, out string? movedFrom)
        {
            var legacy = LegacySessionFolder(workspaceRoot, userHome);
            var existed = Directory.Exists(Path.Combine(LocalFolder(workspaceRoot), "sessions"));
            var folder = Folder(workspaceRoot, "sessions", legacy);
            movedFrom = !existed && Directory.Exists(folder) && !Directory.Exists(legacy) ? legacy : null;
            return folder;
        }

        /// <summary>
        /// omp's own folder for a working directory's conversations: <c>~/.omp/agent/sessions/</c> plus, under the home folder, "-" and the
        /// relative path, elsewhere "--", the full path and "--", with every <c>\ / :</c> written as "-".
        /// </summary>
        internal static string LegacySessionFolder(string workspaceRoot, string userHome)
        {
            var root = Path.GetFullPath(workspaceRoot).TrimEnd('\\', '/');
            var home = Path.GetFullPath(userHome).TrimEnd('\\', '/');
            static string Encode(string text) => text.Replace('\\', '-').Replace('/', '-').Replace(':', '-');
            var name = root.StartsWith(home + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) ? "-" + Encode(root.Substring(home.Length + 1)) : "--" + Encode(root) + "--";
            return Path.Combine(home, ".omp", "agent", "sessions", name);
        }

        /// <summary>
        /// Creates <c>.pomp/local</c> with its <c>.gitignore</c> (<c>*</c>) and returns it. A folder that cannot be created (a read-only
        /// share) is left to the store's own write to report.
        /// </summary>
        internal static string Prepare(string workspaceRoot)
        {
            var local = LocalFolder(workspaceRoot);
            try
            {
                CheckpointStore.CheckDirectPath(local);
                Directory.CreateDirectory(local);
                var ignore = Path.Combine(local, ".gitignore");
                if (!System.IO.File.Exists(ignore)) AtomicFile.WriteAllText(ignore, "*\n");
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
            return local;
        }

        /// <summary>Directory.Move only works within a volume: across volumes the folder is copied, then the source removed.</summary>
        private static void MoveFolder(string source, string target)
        {
            if (string.Equals(Path.GetPathRoot(Path.GetFullPath(source)), Path.GetPathRoot(Path.GetFullPath(target)), StringComparison.OrdinalIgnoreCase))
            {
                Directory.Move(source, target);
                return;
            }
            Copy(source, target);
            Directory.Delete(source, true);
        }

        private static void Copy(string source, string target)
        {
            Directory.CreateDirectory(target);
            foreach (var file in Directory.GetFiles(source)) System.IO.File.Copy(file, Path.Combine(target, Path.GetFileName(file)));
            foreach (var folder in Directory.GetDirectories(source)) Copy(folder, Path.Combine(target, Path.GetFileName(folder)));
        }
    }
}
