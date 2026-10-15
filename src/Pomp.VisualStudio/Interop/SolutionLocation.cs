using System;
using System.IO;
using Microsoft.VisualStudio;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Shell.Interop;

namespace Pomp.VisualStudio.Interop
{
    /// <summary>Where the current solution / "open folder" workspace lives on disk.</summary>
    internal sealed class SolutionLocation
    {
        private SolutionLocation(string rootDirectory, string? solutionFile, bool isFolderWorkspace)
        {
            RootDirectory = rootDirectory;
            SolutionFile = solutionFile;
            IsFolderWorkspace = isFolderWorkspace;
        }

        /// <summary>Solution directory, or the opened folder (no trailing separator).</summary>
        public string RootDirectory { get; }

        /// <summary>Full path of the .sln/.slnx file; null in folder mode.</summary>
        public string? SolutionFile { get; }

        public bool IsFolderWorkspace { get; }

        /// <summary>Display name: solution file name without extension, or the folder name.</summary>
        public string Name => SolutionFile != null
            ? Path.GetFileNameWithoutExtension(SolutionFile)
            : new DirectoryInfo(RootDirectory).Name;

        /// <summary>Returns null when neither a solution nor a folder is open. UI thread only.</summary>
        public static SolutionLocation? Resolve(IVsSolution solution)
        {
            ThreadHelper.ThrowIfNotOnUIThread();

            if (solution.GetSolutionInfo(out var directory, out var file, out _) != VSConstants.S_OK)
            {
                return null;
            }

            var isFolder = false;
            if (solution.GetProperty((int)__VSPROPID7.VSPROPID_IsInOpenFolderMode, out object folderMode) == VSConstants.S_OK
                && folderMode is bool folderModeValue)
            {
                isFolder = folderModeValue;
            }

            var hasFile = !string.IsNullOrWhiteSpace(file);
            var hasDirectory = !string.IsNullOrWhiteSpace(directory);
            if (!hasFile && !hasDirectory)
            {
                return null;
            }

            var root = hasDirectory ? directory! : Path.GetDirectoryName(file)!;
            root = TrimSeparator(root);

            if (isFolder || !hasFile)
            {
                return new SolutionLocation(root, null, true);
            }

            return new SolutionLocation(root, file, false);
        }

        private static string TrimSeparator(string path)
        {
            var trimmed = path.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            // Keep "C:\" intact.
            return trimmed.Length == 2 && trimmed[1] == ':' ? trimmed + Path.DirectorySeparatorChar : trimmed;
        }
    }
}
