using System;
using System.IO;

namespace Pomp.VisualStudio.Adapters
{
    /// <summary>Do not follow a workspace tree node through a junction or symbolic link.</summary>
    internal static class WorkspaceDirectoryPolicy
    {
        public static bool CanTraverse(string path)
        {
            try
            {
                var attributes = File.GetAttributes(path);
                return (attributes & FileAttributes.Directory) != 0 && (attributes & FileAttributes.ReparsePoint) == 0;
            }
            catch (Exception ex) when (ex is UnauthorizedAccessException || ex is IOException)
            {
                return false;
            }
        }
    }
}
