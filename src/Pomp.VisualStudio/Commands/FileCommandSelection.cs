using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;

namespace Pomp.VisualStudio.Commands
{
    internal enum FileCommandOrigin { ActiveWindow, EditorMenu, SolutionExplorerMenu, None }

    /// <summary>File commands never fall back from an empty Explorer selection to an earlier editor.</summary>
    internal static class FileCommandSelection
    {
        internal static FileCommandOrigin ResolveOrigin(FileCommandOrigin origin, bool explorerActive, bool editorActive)
            => origin != FileCommandOrigin.ActiveWindow ? origin
                : explorerActive ? FileCommandOrigin.SolutionExplorerMenu
                : editorActive ? FileCommandOrigin.EditorMenu : FileCommandOrigin.None;

        internal static List<string> Collect(IEnumerable items, Func<object, string?> readPath, Action<Exception>? failed = null)
        {
            var paths = new List<string>();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var item in items)
            {
                // A disconnected hierarchy item must not hide the remaining selected files.
                try
                {
                    var path = readPath(item);
                    if (string.IsNullOrEmpty(path) || HasControl(path!) || !Path.IsPathRooted(path)) continue;
                    var full = Path.GetFullPath(path!.Replace(Path.AltDirectorySeparatorChar, Path.DirectorySeparatorChar));
                    if (File.Exists(full) && seen.Add(full)) paths.Add(full);
                }
                catch (Exception ex) when (ex is COMException || ex is ArgumentException || ex is InvalidCastException
                    || ex is IOException || ex is UnauthorizedAccessException || ex is NotSupportedException)
                {
                    failed?.Invoke(ex);
                }
            }
            return paths;
        }

        private static bool HasControl(string path)
        {
            foreach (var c in path) if (char.IsControl(c)) return true;
            return false;
        }
    }
}
