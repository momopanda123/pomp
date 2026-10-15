using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;
using Pomp.Tools;

namespace Pomp.VisualStudio.Adapters
{
    /// <summary>Disk-only validation for project-item changes. Call from a worker thread, never the Visual Studio UI thread.</summary>
    internal static class ProjectItemChangeSafety
    {
        internal const int MaxPathLength = 259;

        public static string ResolveExisting(string workspaceRoot, string path)
        {
            if (string.IsNullOrWhiteSpace(path) || path.Trim().Length != path.Length)
            {
                throw new ArgumentException("The file path must not be blank or start or end with whitespace.");
            }

            var guard = new PathGuard(workspaceRoot);
            var full = guard.Resolve(path);
            RequirePathLength(full);
            RequireRegularFile(full);
            foreach (var directory in DirectoryChain(Path.GetDirectoryName(full)!))
            {
                RequireDirectory(directory);
            }

            return full;
        }

        public static string? ValidateProjectPaths(string workspaceRoot, string projectPath, string path, string? newName)
        {
            var guard = new PathGuard(workspaceRoot);
            var project = guard.Resolve(projectPath);
            RequirePathLength(project);
            RequireRegularFile(project);
            var source = ResolveExisting(workspaceRoot, path);
            var projectDirectory = Path.GetDirectoryName(project)!;
            var projectGuard = new PathGuard(projectDirectory);
            if (!projectGuard.IsInsideRoot(source) || string.Equals(source, project, StringComparison.OrdinalIgnoreCase))
            {
                throw new ArgumentException("Only files inside the owning project's folder are supported; external linked items and the project file itself cannot be changed.");
            }

            if (newName == null)
            {
                return null;
            }

            ValidateFileName(newName);
            var destination = guard.Resolve(Path.Combine(Path.GetDirectoryName(source)!, newName));
            RequirePathLength(destination);
            if (string.Equals(source, destination, StringComparison.OrdinalIgnoreCase))
            {
                throw new ArgumentException("The new file name must differ from the existing name; case-only renames are not supported safely in this build.");
            }

            RequireMissing(destination);
            return destination;
        }

        public static void ValidateFileName(string name)
        {
            if (string.IsNullOrWhiteSpace(name) || name.Trim().Length != name.Length || name == "." || name == ".."
                || name.EndsWith(".", StringComparison.Ordinal) || name.IndexOfAny(new[] { '/', '\\', ':', '<', '>', '"', '|', '*', '?' }) >= 0)
            {
                throw new ArgumentException("The new name must be a plain file name without separators, invalid characters, leading/trailing whitespace or a trailing dot.");
            }

            foreach (var character in name)
            {
                if (character < ' ')
                {
                    throw new ArgumentException("The new name must not contain control characters.");
                }
            }

            // PathGuard supplies the shared Windows device-name and bidi-control checks; there is no disk mutation here.
            new PathGuard(Path.GetPathRoot(Environment.SystemDirectory)!).Resolve(name);
        }

        public static HeldFile Hold(string workspaceRoot, string projectPath, string path, string? newName, FileIdentity? expected, bool allowDelete)
        {
            ValidateProjectPaths(workspaceRoot, projectPath, path, newName);
            var held = new HeldFile();
            try
            {
                foreach (var directory in DirectoryChain(Path.GetDirectoryName(path)!))
                {
                    held.HoldDirectory(directory);
                }

                ValidateProjectPaths(workspaceRoot, projectPath, path, newName);
                // Deny writes. Removal without disk deletion also denies replacement; rename/recycle must permit DELETE sharing.
                held.Stream = new FileStream(path, FileMode.Open, FileAccess.Read, allowDelete ? FileShare.Read | FileShare.Delete : FileShare.Read);
                held.VerifyPath(path);
                held.Identity = Identify(held.Stream.SafeFileHandle);
                if (expected.HasValue && !held.Identity.Equals(expected.Value))
                {
                    throw new IOException("The file changed or was replaced after approval; no project or disk change was made. Request approval again.");
                }

                return held;
            }
            catch
            {
                held.Dispose();
                throw;
            }
        }

        public static void Revalidate(string workspaceRoot, string projectPath, string path, string? newName, FileIdentity expected, HeldFile held)
        {
            ValidateProjectPaths(workspaceRoot, projectPath, path, newName);
            held.VerifyPath(path);
            if (!Identify(held.Stream!.SafeFileHandle).Equals(expected))
            {
                throw new IOException("The approved file changed; nothing further was changed. Request approval again.");
            }

            using (var current = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete))
            {
                RequireFinalPath(current.SafeFileHandle, path);
                if (!Identify(current.SafeFileHandle).Equals(expected))
                {
                    throw new IOException("The approved path now names a different file; nothing further was changed. Request approval again.");
                }
            }
        }

        public static void VerifyRenamed(string workspaceRoot, string projectPath, string source, string destination, FileIdentity expected, HeldFile held)
        {
            ResolveExisting(workspaceRoot, destination);
            RequireMissing(source);
            held.VerifyPath(destination);
            using (var current = new FileStream(destination, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete))
            {
                RequireFinalPath(current.SafeFileHandle, destination);
                if (!Identify(current.SafeFileHandle).Equals(expected))
                {
                    throw new IOException("The destination is not the unchanged approved file; inspect both paths and the project before retrying.");
                }
            }

            new PathGuard(Path.GetDirectoryName(projectPath)!).Resolve(destination);
        }

        public static void RequireMissing(string path)
        {
            if (TryAttributes(path, out _))
            {
                throw new IOException("The destination already exists; existing files and directories are never overwritten: '" + path + "'.");
            }
        }

        public static bool Exists(string path) => TryAttributes(path, out _);

        private static bool TryAttributes(string path, out FileAttributes attributes)
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

        private static void RequirePathLength(string path)
        {
            if (path.Length > MaxPathLength)
            {
                throw new ArgumentException("Project file paths must not exceed 259 characters (Visual Studio MAX_PATH limit).");
            }
        }

        private static void RequireRegularFile(string path)
        {
            var attributes = File.GetAttributes(path);
            if ((attributes & (FileAttributes.Directory | FileAttributes.ReparsePoint | FileAttributes.Device)) != 0)
            {
                throw new ArgumentException("The target must be an existing regular file, not a directory, device or reparse point: '" + path + "'.");
            }
        }

        private static void RequireDirectory(string path)
        {
            var attributes = File.GetAttributes(path);
            if ((attributes & FileAttributes.Directory) == 0 || (attributes & FileAttributes.ReparsePoint) != 0)
            {
                throw new ArgumentException("The path chain contains a missing directory, non-directory or reparse point: '" + path + "'.");
            }
        }

        private static IEnumerable<string> DirectoryChain(string directory)
        {
            var chain = new Stack<string>();
            for (var current = directory; !string.IsNullOrEmpty(current); current = Path.GetDirectoryName(current))
            {
                chain.Push(current);
            }

            return chain;
        }

        internal readonly struct FileIdentity : IEquatable<FileIdentity>
        {
            public FileIdentity(uint volume, uint indexHigh, uint indexLow, uint sizeHigh, uint sizeLow, int writeHigh, int writeLow)
            {
                Volume = volume;
                Index = ((ulong)indexHigh << 32) | indexLow;
                Size = ((ulong)sizeHigh << 32) | sizeLow;
                LastWrite = ((ulong)(uint)writeHigh << 32) | (uint)writeLow;
            }

            public uint Volume { get; }
            public ulong Index { get; }
            public ulong Size { get; }
            public ulong LastWrite { get; }
            public bool Equals(FileIdentity other) => Volume == other.Volume && Index == other.Index && Size == other.Size && LastWrite == other.LastWrite;
            public override bool Equals(object? value) => value is FileIdentity other && Equals(other);
            public override int GetHashCode() => Volume.GetHashCode() ^ Index.GetHashCode() ^ Size.GetHashCode() ^ LastWrite.GetHashCode();
            public override string ToString() => Volume.ToString("x8", CultureInfo.InvariantCulture) + ":" + Index.ToString("x16", CultureInfo.InvariantCulture)
                + ":" + Size.ToString(CultureInfo.InvariantCulture) + ":" + LastWrite.ToString("x16", CultureInfo.InvariantCulture);
        }

        private static FileIdentity Identify(SafeFileHandle handle)
        {
            if (GetFileType(handle) != 1 || !GetFileInformationByHandle(handle, out var information))
            {
                throw new IOException("Cannot capture a regular disk file identity.", new Win32Exception(Marshal.GetLastWin32Error()));
            }

            if ((information.FileAttributes & (uint)(FileAttributes.Directory | FileAttributes.ReparsePoint | FileAttributes.Device)) != 0
                || information.NumberOfLinks != 1)
            {
                throw new IOException("Project changes refuse directories, reparse points and multiply hard-linked files.");
            }

            return new FileIdentity(information.VolumeSerialNumber, information.FileIndexHigh, information.FileIndexLow,
                information.FileSizeHigh, information.FileSizeLow, information.LastWriteTime.dwHighDateTime, information.LastWriteTime.dwLowDateTime);
        }

        private static void RequireFinalPath(SafeFileHandle handle, string expected)
        {
            var actual = FinalPath(handle);
            if (!string.Equals(actual, expected, StringComparison.OrdinalIgnoreCase))
            {
                throw new IOException("The file or directory resolved to a different physical path: '" + actual + "' (approved: '" + expected + "'). Nothing further was changed.");
            }
        }

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
                throw new IOException("Cannot resolve the physical file or directory path.", new Win32Exception(Marshal.GetLastWin32Error()));
            }

            var result = builder.ToString();
            if (result.StartsWith(@"\\?\UNC\", StringComparison.OrdinalIgnoreCase))
            {
                return @"\\" + result.Substring(8);
            }

            return result.StartsWith(@"\\?\", StringComparison.Ordinal) ? result.Substring(4) : result;
        }

        internal sealed class HeldFile : IDisposable
        {
            private readonly List<SafeFileHandle> directories = new List<SafeFileHandle>();
            public FileStream? Stream { get; set; }
            public FileIdentity Identity { get; set; }

            public void HoldDirectory(string path)
            {
                RequireDirectory(path);
                // OPEN_EXISTING | BACKUP_SEMANTICS | OPEN_REPARSE_POINT, share read only: pin the ancestor against rename/reparse swaps.
                var handle = CreateFile(path, 0, 1, IntPtr.Zero, 3, 0x02200000, IntPtr.Zero);
                if (handle.IsInvalid)
                {
                    var error = Marshal.GetLastWin32Error();
                    handle.Dispose();
                    throw new IOException("Cannot protect the project item's directory: '" + path + "'.", new Win32Exception(error));
                }

                try
                {
                    RequireDirectory(path);
                    RequireFinalPath(handle, path);
                    directories.Add(handle);
                }
                catch
                {
                    handle.Dispose();
                    throw;
                }
            }

            public void VerifyPath(string path) => RequireFinalPath(Stream!.SafeFileHandle, path);

            public void VerifyUnchanged(string path, FileIdentity expected)
            {
                VerifyPath(path);
                if (!Identify(Stream!.SafeFileHandle).Equals(expected))
                {
                    throw new IOException("The approved file changed or moved after the last validation; nothing further was changed. Request approval again.");
                }
            }

            public void AllowDeletion(string path, FileIdentity expected)
            {
                // Open the delete-sharing handle BEFORE releasing the non-delete-sharing one; there is no unpinned handoff gap.
                var next = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete);
                try
                {
                    RequireFinalPath(next.SafeFileHandle, path);
                    if (!Identify(next.SafeFileHandle).Equals(expected))
                    {
                        throw new IOException("The approved file changed before recycling; the disk file was left untouched.");
                    }
                }
                catch
                {
                    next.Dispose();
                    throw;
                }

                Stream!.Dispose();
                Stream = next;
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
                    foreach (var directory in directories)
                    {
                        directory.Dispose();
                    }

                    directories.Clear();
                }
            }
        }

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern SafeFileHandle CreateFile(string name, uint access, uint share, IntPtr security, uint disposition, uint flags, IntPtr template);
        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern uint GetFileType(SafeFileHandle handle);
        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool GetFileInformationByHandle(SafeFileHandle handle, out ByHandleFileInformation information);
        [DllImport("kernel32.dll", EntryPoint = "GetFinalPathNameByHandleW", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern uint GetFinalPathNameByHandle(SafeFileHandle handle, StringBuilder path, uint capacity, uint flags);

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
    }
}
