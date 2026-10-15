using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;
using System.Threading;
using System.Threading.Tasks;
using Pomp.Tools;

namespace Pomp.VisualStudio.Adapters
{
    /// <summary>Disk-only preflight and create-new operations. Callers run these on a worker thread, never the VS UI thread.</summary>
    internal static class ProjectFileCreation
    {
        private const int MaxContentBytes = 1024 * 1024;

        // MAX_PATH without its terminating NUL, and the CreateDirectory limit (MAX_PATH - 12): Visual Studio and the unprefixed
        // CreateFile calls that pin the parent directories support no more.
        private const int MaxFilePathLength = 259;
        private const int MaxDirectoryPathLength = 247;
        private static readonly Encoding ContentEncoding = new UTF8Encoding(false, true);

        public static string Validate(string workspaceRoot, string projectPath, string relativePath, bool requireMissing)
        {
            if (string.IsNullOrWhiteSpace(relativePath)
                || Path.IsPathRooted(relativePath)
                || relativePath.IndexOf(':') >= 0)
            {
                throw new ArgumentException("The new file path must be relative to the project directory.");
            }

            // PathGuard trims its input, but the project registers exactly this spelling: " a.cs" must not quietly become "a.cs".
            if (relativePath.Trim().Length != relativePath.Length)
            {
                throw new ArgumentException("The new file path must not start or end with whitespace.");
            }

            foreach (var segment in relativePath.Replace('\\', '/').Split('/'))
            {
                if (segment.Length == 0 || segment == "." || segment == "..")
                {
                    throw new ArgumentException("Empty, '.' and '..' path segments are not allowed.");
                }
            }

            var workspaceGuard = new PathGuard(workspaceRoot);
            RequireDirectory(workspaceGuard.Root);
            var resolvedProject = workspaceGuard.Resolve(projectPath);
            var projectAttributes = File.GetAttributes(resolvedProject);
            if ((projectAttributes & (FileAttributes.Directory | FileAttributes.ReparsePoint)) != 0)
            {
                throw new ArgumentException("The project path must be a regular file, not a directory or reparse point.");
            }

            var projectDirectory = Path.GetDirectoryName(resolvedProject)!;
            RequireDirectory(projectDirectory);
            var projectGuard = new PathGuard(projectDirectory);
            var target = projectGuard.Resolve(relativePath);
            // Refused here, before anything exists: a longer path would fail only after its first new directories were created.
            if (target.Length > MaxFilePathLength || Path.GetDirectoryName(target)!.Length > MaxDirectoryPathLength)
            {
                throw new ArgumentException("The new file path is too long for Visual Studio: '" + target + "' has " + target.Length
                    + " characters (at most " + MaxFilePathLength + ", and " + MaxDirectoryPathLength + " for its folder).");
            }

            // The workspace guard also checks the ancestors of the project directory.
            workspaceGuard.Resolve(target);
            ValidateParents(projectDirectory, Path.GetDirectoryName(target)!);

            if (TryGetAttributes(target, out var attributes))
            {
                if (requireMissing)
                {
                    throw new ArgumentException("The target already exists; existing files and directories are never overwritten: '" + target + "'.");
                }

                if ((attributes & (FileAttributes.Directory | FileAttributes.ReparsePoint)) != 0)
                {
                    throw new ArgumentException("The target is not a regular file: '" + target + "'.");
                }
            }
            else if (!requireMissing)
            {
                throw new IOException("The newly created file is no longer at its approved path: '" + target + "'.");
            }

            return target;
        }

        public static byte[] Encode(string content)
        {
            var count = ContentEncoding.GetByteCount(content);
            if (count > MaxContentBytes)
            {
                throw new ArgumentException("Initial file content must not exceed 1 MiB of UTF-8 text.");
            }

            return ContentEncoding.GetBytes(content);
        }

        public static async Task CreateAsync(
            string workspaceRoot,
            string projectPath,
            string relativePath,
            string expectedTarget,
            byte[] content,
            CreationState state,
            Action validateGeneration,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            validateGeneration();
            var target = Validate(workspaceRoot, projectPath, relativePath, requireMissing: true);
            if (!string.Equals(target, expectedTarget, StringComparison.OrdinalIgnoreCase))
            {
                throw new ArgumentException("The new file path changed after approval; nothing was created.");
            }

            var projectDirectory = Path.GetDirectoryName(projectPath)!;
            var missing = new Stack<string>();
            var parent = Path.GetDirectoryName(target)!;
            var deepestExisting = projectDirectory;
            for (var current = parent; !string.Equals(current, projectDirectory, StringComparison.OrdinalIgnoreCase); current = Path.GetDirectoryName(current)!)
            {
                if (TryGetAttributes(current, out _))
                {
                    deepestExisting = current;
                    break;
                }

                missing.Push(current);
            }

            // Pin the whole existing chain (workspace root, project directory, existing parents) before anything is created below it.
            HoldDirectories(workspaceRoot, projectDirectory, state);
            HoldDirectories(projectDirectory, deepestExisting, state);
            state.HoldDirectory(projectDirectory);
            var projectFinal = state.FinalPathOf(projectDirectory);
            Validate(workspaceRoot, projectPath, relativePath, requireMissing: true);
            validateGeneration();
            while (missing.Count > 0)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var directory = missing.Pop();
                Validate(workspaceRoot, projectPath, relativePath, requireMissing: true);
                validateGeneration();
                Directory.CreateDirectory(directory);
                // Never remove a directory on failure: another actor may have created or populated it meanwhile.
                state.Directories.Add(directory);
                RequireDirectory(directory);
                state.HoldDirectory(directory);
                RequireFinalPath(state.FinalPathOf(directory), projectFinal, projectDirectory, directory, "directory");
            }

            HoldDirectories(projectDirectory, parent, state);
            cancellationToken.ThrowIfCancellationRequested();
            Validate(workspaceRoot, projectPath, relativePath, requireMissing: true);
            validateGeneration();
            state.Stream = new FileStream(target, FileMode.CreateNew, FileAccess.Write, FileShare.Read, 4096, FileOptions.Asynchronous);
            state.FileCreated = true;

            // Before any content is written: the new file must really be where it was approved, whatever was swapped in meanwhile.
            RequireFinalPath(FinalPath(state.Stream.SafeFileHandle), projectFinal, projectDirectory, target, "file");
            await state.Stream.WriteAsync(content, 0, content.Length, cancellationToken).ConfigureAwait(false);
            await state.Stream.FlushAsync(cancellationToken).ConfigureAwait(false);

            // Through registration keep a read-only handle to this very file: the project system can read it, but nobody can write,
            // delete or replace it.
            var written = FileId(state.Stream.SafeFileHandle);
            state.Stream.Dispose();
            state.Stream = null;
            state.Stream = new FileStream(target, FileMode.Open, FileAccess.Read, FileShare.Read);
            if (!string.Equals(FileId(state.Stream.SafeFileHandle), written, StringComparison.Ordinal))
            {
                throw new IOException("The new file was replaced before it could be registered: '" + target + "'.");
            }

            // The same file could still have been rewritten while no handle was held: it must hold exactly the approved bytes.
            if (!HasContent(state.Stream, content))
            {
                throw new IOException("The new file was changed before it could be registered: '" + target + "'.");
            }

            Validate(workspaceRoot, projectPath, relativePath, requireMissing: false);
        }

        /// <summary>
        /// Refuses a <paramref name="path"/> (created by this operation) whose handle resolves anywhere but below the held project
        /// directory: a junction or link swapped in along the way would otherwise redirect the creation outside the workspace.
        /// </summary>
        private static void RequireFinalPath(string actual, string projectFinal, string projectDirectory, string path, string what)
        {
            var expected = projectFinal.TrimEnd(Path.DirectorySeparatorChar) + path.Substring(projectDirectory.TrimEnd(Path.DirectorySeparatorChar).Length);
            if (!string.Equals(actual, expected, StringComparison.OrdinalIgnoreCase))
            {
                throw new IOException("The new " + what + " '" + path + "' resolved to '" + actual + "' instead of its approved location (a junction or link was swapped in); "
                    + (what == "file" ? "no content was written." : "nothing was written into it."));
            }
        }

        /// <summary>The final (link-resolved) path of an open handle, without the \\?\ prefix.</summary>
        private static string FinalPath(SafeFileHandle handle)
        {
            var builder = new StringBuilder(512);
            var length = GetFinalPathNameByHandle(handle, builder, (uint)builder.Capacity, 0);
            if (length >= builder.Capacity)
            {
                builder = new StringBuilder((int)length + 1);
                length = GetFinalPathNameByHandle(handle, builder, (uint)builder.Capacity, 0);
            }

            if (length == 0 || length >= builder.Capacity)
            {
                throw new IOException("Cannot resolve the final path of a new file or directory.", new Win32Exception(Marshal.GetLastWin32Error()));
            }

            var path = builder.ToString();
            if (path.StartsWith(@"\\?\UNC\", StringComparison.OrdinalIgnoreCase))
            {
                return @"\\" + path.Substring(8);
            }

            return path.StartsWith(@"\\?\", StringComparison.Ordinal) ? path.Substring(4) : path;
        }

        private static bool HasContent(FileStream stream, byte[] expected)
        {
            if (stream.Length != expected.Length)
            {
                return false;
            }

            stream.Position = 0;
            var actual = new byte[expected.Length];
            var read = 0;
            while (read < actual.Length)
            {
                var count = stream.Read(actual, read, actual.Length - read);
                if (count == 0)
                {
                    return false;
                }

                read += count;
            }

            for (var i = 0; i < expected.Length; i++)
            {
                if (actual[i] != expected[i])
                {
                    return false;
                }
            }

            return true;
        }

        /// <summary>Volume serial and file index: identifies a file independently of its name.</summary>
        private static string FileId(SafeFileHandle handle)
        {
            if (!GetFileInformationByHandle(handle, out var information))
            {
                throw new IOException("Cannot identify the new file.", new Win32Exception(Marshal.GetLastWin32Error()));
            }

            return information.VolumeSerialNumber.ToString("x8") + ":" + information.FileIndexHigh.ToString("x8") + information.FileIndexLow.ToString("x8");
        }

        [DllImport("kernel32.dll", EntryPoint = "GetFinalPathNameByHandleW", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern uint GetFinalPathNameByHandle(SafeFileHandle file, StringBuilder path, uint length, uint flags);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool GetFileInformationByHandle(SafeFileHandle file, out ByHandleFileInformation information);

        [StructLayout(LayoutKind.Sequential)]
        private struct ByHandleFileInformation
        {
            public uint FileAttributes;
            public System.Runtime.InteropServices.ComTypes.FILETIME CreationTime;
            public System.Runtime.InteropServices.ComTypes.FILETIME LastAccessTime;
            public System.Runtime.InteropServices.ComTypes.FILETIME LastWriteTime;
            public uint VolumeSerialNumber;
            public uint FileSizeHigh;
            public uint FileSizeLow;
            public uint NumberOfLinks;
            public uint FileIndexHigh;
            public uint FileIndexLow;
        }

        private static void HoldDirectories(string root, string directory, CreationState state)
        {
            var normalizedRoot = new PathGuard(root).Root;
            state.HoldDirectory(normalizedRoot);
            var relative = directory.Length == normalizedRoot.Length ? string.Empty
                : directory.Substring(normalizedRoot.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar).Length + 1);
            var current = normalizedRoot;
            foreach (var segment in relative.Split(new[] { Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar }, StringSplitOptions.RemoveEmptyEntries))
            {
                current = Path.Combine(current, segment);
                state.HoldDirectory(current);
            }
        }

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern SafeFileHandle CreateFile(string fileName, uint desiredAccess, uint shareMode, IntPtr securityAttributes,
            uint creationDisposition, uint flagsAndAttributes, IntPtr templateFile);

        private static void ValidateParents(string projectDirectory, string parent)
        {
            var relative = parent.Length == projectDirectory.Length ? string.Empty : parent.Substring(projectDirectory.Length + 1);
            var current = projectDirectory;
            foreach (var segment in relative.Split(new[] { Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar }, StringSplitOptions.RemoveEmptyEntries))
            {
                current = Path.Combine(current, segment);
                if (!TryGetAttributes(current, out var attributes))
                {
                    break;
                }

                if ((attributes & FileAttributes.Directory) == 0 || (attributes & FileAttributes.ReparsePoint) != 0)
                {
                    throw new ArgumentException("The target parent is not a regular directory: '" + current + "'.");
                }
            }
        }

        private static void RequireDirectory(string directory)
        {
            var attributes = File.GetAttributes(directory);
            if ((attributes & FileAttributes.Directory) == 0 || (attributes & FileAttributes.ReparsePoint) != 0)
            {
                throw new ArgumentException("The directory is missing, not a directory, or a reparse point: '" + directory + "'.");
            }
        }

        private static bool TryGetAttributes(string path, out FileAttributes attributes)
        {
            try
            {
                attributes = File.GetAttributes(path);
                return true;
            }
            catch (FileNotFoundException)
            {
                attributes = default;
                return false;
            }
            catch (DirectoryNotFoundException)
            {
                attributes = default;
                return false;
            }
        }

        internal sealed class CreationState : IDisposable
        {
            private readonly Dictionary<string, SafeFileHandle> directories = new Dictionary<string, SafeFileHandle>(StringComparer.OrdinalIgnoreCase);

            public void HoldDirectory(string directory)
            {
                if (directories.ContainsKey(directory))
                {
                    return;
                }

                RequireDirectory(directory);
                // OPEN_EXISTING | FILE_FLAG_BACKUP_SEMANTICS | FILE_FLAG_OPEN_REPARSE_POINT; read sharing only: nobody can rename or delete
                // the directory, nor open it for writing (which turning it into a junction in place requires).
                var handle = CreateFile(directory, 0, 1, IntPtr.Zero, 3, 0x02200000, IntPtr.Zero);
                if (handle.IsInvalid)
                {
                    var error = Marshal.GetLastWin32Error();
                    handle.Dispose();
                    throw new IOException("Cannot protect the target directory: '" + directory + "'.", new Win32Exception(error));
                }

                try
                {
                    RequireDirectory(directory);
                    directories.Add(directory, handle);
                }
                catch
                {
                    handle.Dispose();
                    throw;
                }
            }

            /// <summary>The final (link-resolved) path of a held directory.</summary>
            public string FinalPathOf(string directory)
            {
                if (!directories.TryGetValue(directory, out var handle))
                {
                    throw new InvalidOperationException("The directory is not held: '" + directory + "'.");
                }

                return FinalPath(handle);
            }

            public FileStream? Stream { get; set; }

            public bool FileCreated { get; set; }

            public List<string> Directories { get; } = new List<string>();

            public string DescribeLeftovers(string target)
            {
                var message = FileCreated
                    ? " The created file was not deleted and may remain at '" + target + "'; project registration may be incomplete. Inspect it and the project before retrying."
                    : " No file was created by this operation; any existing target was left untouched.";
                if (Directories.Count > 0)
                {
                    message += " Directories created or encountered during creation were also retained: " + string.Join(", ", Directories) + ".";
                }

                return message;
            }

            public void Dispose()
            {
                try
                {
                    Stream?.Dispose();
                    Stream = null;
                }
                finally
                {
                    foreach (var handle in directories.Values)
                    {
                        handle.Dispose();
                    }

                    directories.Clear();
                }
            }
        }
    }
}
