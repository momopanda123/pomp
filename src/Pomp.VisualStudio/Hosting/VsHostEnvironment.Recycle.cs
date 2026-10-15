using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using Pomp.Hosting;
using Task = System.Threading.Tasks.Task;

namespace Pomp.VisualStudio.Hosting
{
    internal sealed partial class VsHostEnvironment : IPompRecycleBin
    {
        public async Task RecycleAsync(IReadOnlyList<string> paths, CancellationToken cancellationToken)
        {
            await joinableTaskFactory.SwitchToMainThreadAsync(cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            try { WindowsRecycleBin.Move(paths); }
            catch (Exception ex) when (ex is COMException || ex is IOException || ex is UnauthorizedAccessException || ex is NotSupportedException)
            {
                throw new InvalidOperationException(PompLanguage.Pick("대화 파일을 휴지통으로 옮기지 못했어요.", "Could not move the conversation files to the Recycle Bin."), ex);
            }
        }
    }

    internal static class WindowsRecycleBin
    {
        private const uint Recycle = 0x00080000, EarlyFailure = 0x00100000;
        private const uint Silent = 0x0004, NoConfirmation = 0x0010, NoErrorUi = 0x0400, NoConfirmMkdir = 0x0200;
        public static void Move(IReadOnlyList<string> paths)
        {
            if (paths.Count == 0 || paths.Count > 2) throw new ArgumentException("Invalid recycle operation.");
            foreach (var path in paths) Validate(path);
            var operation = (IFileOperation)Activator.CreateInstance(Type.GetTypeFromCLSID(new Guid("3AD05575-8857-4850-9277-11B85BDB8E09"), true)!)!;
            var items = new List<object>(); var sink = new RecycleOnlySink();
            try
            {
                operation.SetOperationFlags(Recycle | EarlyFailure | Silent | NoConfirmation | NoErrorUi | NoConfirmMkdir);
                foreach (var path in paths)
                {
                    var iid = new Guid("43826D1E-E718-42EE-BC55-A1E261C37BFE");
                    Marshal.ThrowExceptionForHR(SHCreateItemFromParsingName(path, IntPtr.Zero, ref iid, out var item));
                    items.Add(item); operation.DeleteItem(item, sink);
                }
                operation.PerformOperations(); operation.GetAnyOperationsAborted(out var aborted);
                if (aborted || sink.Failed || sink.Recycled != paths.Count) throw new IOException("The recycle operation did not complete.");
                foreach (var path in paths) if (File.Exists(path) || Directory.Exists(path)) throw new IOException("A recycled path is still present.");
            }
            finally
            {
                foreach (var item in items) Marshal.ReleaseComObject(item);
                Marshal.ReleaseComObject(operation);
            }
        }
        private static void Validate(string path)
        {
            if (!Path.IsPathRooted(path) || !string.Equals(Path.GetFullPath(path), path, StringComparison.OrdinalIgnoreCase) || path.StartsWith("\\\\", StringComparison.Ordinal)) throw new NotSupportedException("Only direct local paths can be recycled.");
            if (new DriveInfo(Path.GetPathRoot(path)!).DriveType != DriveType.Fixed) throw new NotSupportedException("The volume does not support this recycle operation.");
            for (var current = path; !string.IsNullOrEmpty(current); current = Path.GetDirectoryName(current))
                if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0) throw new IOException("Linked paths cannot be recycled.");
            if (!Directory.Exists(path)) return;
            var folders = new Stack<string>(); folders.Push(path); var count = 0;
            while (folders.Count > 0)
                foreach (var entry in Directory.EnumerateFileSystemEntries(folders.Pop()))
                {
                    if (++count > 20000) throw new IOException("Too many recycle entries.");
                    var attributes = File.GetAttributes(entry);
                    if ((attributes & FileAttributes.ReparsePoint) != 0) throw new IOException("Linked paths cannot be recycled.");
                    if ((attributes & FileAttributes.Directory) != 0) folders.Push(entry);
                }
        }
        [DllImport("shell32.dll", CharSet = CharSet.Unicode, PreserveSig = true)]
        private static extern int SHCreateItemFromParsingName(string path, IntPtr bindContext, ref Guid iid, out IShellItem item);

        [ComImport, Guid("43826D1E-E718-42EE-BC55-A1E261C37BFE"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IShellItem
        {
            void BindToHandler(IntPtr bindContext, ref Guid handler, ref Guid iid, out IntPtr result);
            void GetParent(out IShellItem parent);
            void GetDisplayName(uint kind, out IntPtr name);
            void GetAttributes(uint mask, out uint attributes);
            void Compare(IShellItem other, uint hint, out int order);
        }

        [ComImport, Guid("947AAB5F-0A5C-4C13-B4D6-4BF7836FC9F8"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IFileOperation
        {
            void Advise(IntPtr sink, out uint cookie);
            void Unadvise(uint cookie);
            void SetOperationFlags(uint flags);
            void SetProgressMessage([MarshalAs(UnmanagedType.LPWStr)] string message);
            void SetProgressDialog(IntPtr dialog);
            void SetProperties(IntPtr properties);
            void SetOwnerWindow(IntPtr window);
            void ApplyPropertiesToItem([MarshalAs(UnmanagedType.Interface)] object item);
            void ApplyPropertiesToItems([MarshalAs(UnmanagedType.Interface)] object items);
            void RenameItem([MarshalAs(UnmanagedType.Interface)] object item, [MarshalAs(UnmanagedType.LPWStr)] string name, IntPtr sink);
            void RenameItems([MarshalAs(UnmanagedType.Interface)] object items, [MarshalAs(UnmanagedType.LPWStr)] string name);
            void MoveItem([MarshalAs(UnmanagedType.Interface)] object item, [MarshalAs(UnmanagedType.Interface)] object folder, [MarshalAs(UnmanagedType.LPWStr)] string name, IntPtr sink);
            void MoveItems([MarshalAs(UnmanagedType.Interface)] object items, [MarshalAs(UnmanagedType.Interface)] object folder);
            void CopyItem([MarshalAs(UnmanagedType.Interface)] object item, [MarshalAs(UnmanagedType.Interface)] object folder, [MarshalAs(UnmanagedType.LPWStr)] string name, IntPtr sink);
            void CopyItems([MarshalAs(UnmanagedType.Interface)] object items, [MarshalAs(UnmanagedType.Interface)] object folder);
            void DeleteItem(IShellItem item, IFileOperationProgressSink sink);
            void DeleteItems([MarshalAs(UnmanagedType.Interface)] object items);
            void NewItem([MarshalAs(UnmanagedType.Interface)] object folder, uint attributes, [MarshalAs(UnmanagedType.LPWStr)] string name, [MarshalAs(UnmanagedType.LPWStr)] string template, IntPtr sink);
            void PerformOperations();
            void GetAnyOperationsAborted([MarshalAs(UnmanagedType.Bool)] out bool aborted);
        }
        [ComVisible(true), Guid("04B0F1A7-9490-44BC-96E1-4296A31252E2"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IFileOperationProgressSink
        {
            [PreserveSig] int StartOperations();
            [PreserveSig] int FinishOperations(int result);
            [PreserveSig] int PreRenameItem(uint flags, [MarshalAs(UnmanagedType.Interface)] object item, [MarshalAs(UnmanagedType.LPWStr)] string name);
            [PreserveSig] int PostRenameItem(uint flags, [MarshalAs(UnmanagedType.Interface)] object item, [MarshalAs(UnmanagedType.LPWStr)] string name, int result, [MarshalAs(UnmanagedType.Interface)] object created);
            [PreserveSig] int PreMoveItem(uint flags, [MarshalAs(UnmanagedType.Interface)] object item, [MarshalAs(UnmanagedType.Interface)] object folder, [MarshalAs(UnmanagedType.LPWStr)] string name);
            [PreserveSig] int PostMoveItem(uint flags, [MarshalAs(UnmanagedType.Interface)] object item, [MarshalAs(UnmanagedType.Interface)] object folder, [MarshalAs(UnmanagedType.LPWStr)] string name, int result, [MarshalAs(UnmanagedType.Interface)] object created);
            [PreserveSig] int PreCopyItem(uint flags, [MarshalAs(UnmanagedType.Interface)] object item, [MarshalAs(UnmanagedType.Interface)] object folder, [MarshalAs(UnmanagedType.LPWStr)] string name);
            [PreserveSig] int PostCopyItem(uint flags, [MarshalAs(UnmanagedType.Interface)] object item, [MarshalAs(UnmanagedType.Interface)] object folder, [MarshalAs(UnmanagedType.LPWStr)] string name, int result, [MarshalAs(UnmanagedType.Interface)] object created);
            [PreserveSig] int PreDeleteItem(uint flags, [MarshalAs(UnmanagedType.Interface)] object item);
            [PreserveSig] int PostDeleteItem(uint flags, [MarshalAs(UnmanagedType.Interface)] object item, int result, [MarshalAs(UnmanagedType.Interface)] object created);
            [PreserveSig] int PreNewItem(uint flags, [MarshalAs(UnmanagedType.Interface)] object folder, [MarshalAs(UnmanagedType.LPWStr)] string name);
            [PreserveSig] int PostNewItem(uint flags, [MarshalAs(UnmanagedType.Interface)] object folder, [MarshalAs(UnmanagedType.LPWStr)] string name, [MarshalAs(UnmanagedType.LPWStr)] string template, uint attributes, int result, [MarshalAs(UnmanagedType.Interface)] object created);
            [PreserveSig] int UpdateProgress(uint total, uint completed);
            [PreserveSig] int ResetTimer();
            [PreserveSig] int PauseTimer();
            [PreserveSig] int ResumeTimer();
        }
        [ComVisible(true), ClassInterface(ClassInterfaceType.None)]
        private sealed class RecycleOnlySink : IFileOperationProgressSink
        {
            public bool Failed { get; private set; }
            public int Recycled { get; private set; }
            public int StartOperations() => 0;
            public int FinishOperations(int result) { Failed |= result < 0; return 0; }
            // Abort a shell fallback that would permanently delete instead of recycling.
            public int PreDeleteItem(uint flags, object item) { if ((flags & 0x80) != 0) return 0; Failed = true; return unchecked((int)0x80004004); }
            public int PostDeleteItem(uint flags, object item, int result, object created) { if (result < 0) Failed = true; else Recycled++; return 0; }
            public int PreRenameItem(uint flags, object item, string name) => 0;
            public int PostRenameItem(uint flags, object item, string name, int result, object created) => 0;
            public int PreMoveItem(uint flags, object item, object folder, string name) => 0;
            public int PostMoveItem(uint flags, object item, object folder, string name, int result, object created) => 0;
            public int PreCopyItem(uint flags, object item, object folder, string name) => 0;
            public int PostCopyItem(uint flags, object item, object folder, string name, int result, object created) => 0;
            public int PreNewItem(uint flags, object folder, string name) => 0;
            public int PostNewItem(uint flags, object folder, string name, string template, uint attributes, int result, object created) => 0;
            public int UpdateProgress(uint total, uint completed) => 0;
            public int ResetTimer() => 0;
            public int PauseTimer() => 0;
            public int ResumeTimer() => 0;
        }
    }
}
