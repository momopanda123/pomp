using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;

namespace Pomp.VisualStudio.Adapters
{
    /// <summary>
    /// Recycle-only shell operation. Shell COM is created, used and released in its own STA, not on the VS UI thread.
    /// Requests recycling; the sink rejects transfers not marked for recycling. The tool stays disabled until
    /// real Windows failure cases (disabled/full Recycle Bin, oversized file) have been verified.
    /// </summary>
    internal static class ProjectItemRecycleBin
    {
        private const int Abort = unchecked((int)0x80004004);
        private const uint RecycleTransfer = 0x80;
        private const uint Silent = 0x0004;
        private const uint NoErrorUi = 0x0400;
        private const uint NoConnectedElements = 0x2000;
        private const uint WantNukeWarning = 0x4000;
        private const uint RecycleOnDelete = 0x00080000;
        private const uint EarlyFailure = 0x00100000;
        private const uint AddUndoRecord = 0x20000000;

        public static Task RecycleAsync(string path, Action validate, CancellationToken cancellationToken)
        {
            var completion = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            var thread = new Thread(() =>
            {
                try
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    validate();
                    Recycle(path, validate, cancellationToken);
                    completion.TrySetResult(true);
                }
                catch (Exception ex)
                {
                    completion.TrySetException(ex);
                }
            }) { IsBackground = true, Name = "Pomp recycle project file" };
            thread.SetApartmentState(ApartmentState.STA);
            thread.Start();
            // Do not complete early on cancellation: the caller owns pinned handles until the shell actually finishes.
            return completion.Task;
        }

        private static void Recycle(string path, Action validate, CancellationToken cancellationToken)
        {
            IShellItem? item = null;
            IFileOperation? operation = null;
            try
            {
                var shellItemId = typeof(IShellItem).GUID;
                Marshal.ThrowExceptionForHR(SHCreateItemFromParsingName(path, IntPtr.Zero, ref shellItemId, out item));
                operation = (IFileOperation)Activator.CreateInstance(Type.GetTypeFromCLSID(new Guid("3AD05575-8857-4850-9277-11B85BDB8E09"), throwOnError: true)!)!;
                // Never answer Yes-to-All to a permanent-delete fallback. Ask the shell to warn if it
                // cannot recycle, in addition to the PreDeleteItem veto. This is defense in depth,
                // not evidence that every Windows recycle failure is safe (catalog remains gated).
                Marshal.ThrowExceptionForHR(operation.SetOperationFlags(Silent | NoErrorUi | NoConnectedElements |
                    WantNukeWarning | RecycleOnDelete | EarlyFailure | AddUndoRecord));
                var sink = new RecycleSink(path, validate, cancellationToken);
                Marshal.ThrowExceptionForHR(operation.DeleteItem(item, sink));
                cancellationToken.ThrowIfCancellationRequested();
                validate();
                var result = operation.PerformOperations();
                // Report our own identity/cancellation/refusal reason before a generic shell HRESULT.
                if (sink.Error != null)
                {
                    throw new IOException("The file was not safely recycled: " + sink.Error);
                }

                Marshal.ThrowExceptionForHR(result);
                Marshal.ThrowExceptionForHR(operation.GetAnyOperationsAborted(out var aborted));
                if (aborted || !sink.Recycled)
                {
                    throw new IOException("The shell did not confirm the file in the Recycle Bin; no permanent-delete fallback was attempted. Inspect the file before retrying.");
                }
            }
            finally
            {
                if (operation != null) Marshal.ReleaseComObject(operation);
                if (item != null) Marshal.ReleaseComObject(item);
            }
        }

        [ComVisible(true)]
        [ClassInterface(ClassInterfaceType.None)]
        private sealed class RecycleSink : IFileOperationProgressSink
        {
            private readonly string path;
            private readonly Action validate;
            private readonly CancellationToken cancellationToken;

            public RecycleSink(string path, Action validate, CancellationToken cancellationToken)
            {
                this.path = path;
                this.validate = validate;
                this.cancellationToken = cancellationToken;
            }

            public string? Error { get; private set; }
            public bool Recycled { get; private set; }

            public int PreDeleteItem(uint flags, IShellItem item)
            {
                try
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    if ((flags & RecycleTransfer) == 0)
                    {
                        throw new IOException("The shell selected permanent deletion, which is not approved.");
                    }

                    Marshal.ThrowExceptionForHR(item.GetDisplayName(0x80058000, out var name)); // SIGDN_FILESYSPATH
                    string? actual;
                    try { actual = Marshal.PtrToStringUni(name); }
                    finally { Marshal.FreeCoTaskMem(name); }
                    if (!string.Equals(actual, path, StringComparison.OrdinalIgnoreCase))
                    {
                        throw new IOException("The shell selected a different file.");
                    }

                    validate();
                    return 0;
                }
                catch (Exception ex)
                {
                    Error = ex.Message;
                    return Abort;
                }
            }

            public int PostDeleteItem(uint flags, IShellItem item, int result, IShellItem? recycledItem)
            {
                Recycled = result >= 0 && recycledItem != null;
                if (result < 0) Error = "The shell returned HRESULT 0x" + result.ToString("X8") + ".";
                else if (recycledItem == null) Error = "The shell did not return a Recycle Bin item; inspect the operation's outcome.";
                return 0;
            }

            public int StartOperations() => 0;
            public int FinishOperations(int result) => 0;
            public int PreRenameItem(uint flags, IShellItem item, string name) => Abort;
            public int PostRenameItem(uint flags, IShellItem item, string name, int result, IShellItem? created) => 0;
            public int PreMoveItem(uint flags, IShellItem item, IShellItem folder, string? name) => Abort;
            public int PostMoveItem(uint flags, IShellItem item, IShellItem folder, string? name, int result, IShellItem? created) => 0;
            public int PreCopyItem(uint flags, IShellItem item, IShellItem folder, string? name) => Abort;
            public int PostCopyItem(uint flags, IShellItem item, IShellItem folder, string? name, int result, IShellItem? created) => 0;
            public int PreNewItem(uint flags, IShellItem folder, string? name) => Abort;
            public int PostNewItem(uint flags, IShellItem folder, string? name, string? template, uint attributes, int result, IShellItem? created) => 0;
            public int UpdateProgress(uint total, uint completed) => cancellationToken.IsCancellationRequested ? Abort : 0;
            public int ResetTimer() => 0;
            public int PauseTimer() => 0;
            public int ResumeTimer() => 0;
        }

        [ComImport, Guid("43826D1E-E718-42EE-BC55-A1E261C37BFE"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IShellItem
        {
            [PreserveSig] int BindToHandler(IntPtr bind, ref Guid handler, ref Guid iid, out IntPtr value);
            [PreserveSig] int GetParent(out IShellItem parent);
            [PreserveSig] int GetDisplayName(uint kind, out IntPtr name);
            [PreserveSig] int GetAttributes(uint mask, out uint attributes);
            [PreserveSig] int Compare(IShellItem item, uint hints, out int order);
        }

        [ComVisible(true), Guid("04B0F1A7-9490-44BC-96E1-4296A31252E2"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IFileOperationProgressSink
        {
            [PreserveSig] int StartOperations();
            [PreserveSig] int FinishOperations(int result);
            [PreserveSig] int PreRenameItem(uint flags, IShellItem item, [MarshalAs(UnmanagedType.LPWStr)] string name);
            [PreserveSig] int PostRenameItem(uint flags, IShellItem item, [MarshalAs(UnmanagedType.LPWStr)] string name, int result, IShellItem? created);
            [PreserveSig] int PreMoveItem(uint flags, IShellItem item, IShellItem folder, [MarshalAs(UnmanagedType.LPWStr)] string? name);
            [PreserveSig] int PostMoveItem(uint flags, IShellItem item, IShellItem folder, [MarshalAs(UnmanagedType.LPWStr)] string? name, int result, IShellItem? created);
            [PreserveSig] int PreCopyItem(uint flags, IShellItem item, IShellItem folder, [MarshalAs(UnmanagedType.LPWStr)] string? name);
            [PreserveSig] int PostCopyItem(uint flags, IShellItem item, IShellItem folder, [MarshalAs(UnmanagedType.LPWStr)] string? name, int result, IShellItem? created);
            [PreserveSig] int PreDeleteItem(uint flags, IShellItem item);
            [PreserveSig] int PostDeleteItem(uint flags, IShellItem item, int result, IShellItem? recycledItem);
            [PreserveSig] int PreNewItem(uint flags, IShellItem folder, [MarshalAs(UnmanagedType.LPWStr)] string? name);
            [PreserveSig] int PostNewItem(uint flags, IShellItem folder, [MarshalAs(UnmanagedType.LPWStr)] string? name, [MarshalAs(UnmanagedType.LPWStr)] string? template, uint attributes, int result, IShellItem? created);
            [PreserveSig] int UpdateProgress(uint total, uint completed);
            [PreserveSig] int ResetTimer();
            [PreserveSig] int PauseTimer();
            [PreserveSig] int ResumeTimer();
        }

        [ComImport, Guid("947AAB5F-0A5C-4C13-B4D6-4BF7836FC9F8"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IFileOperation
        {
            [PreserveSig] int Advise(IFileOperationProgressSink sink, out uint cookie);
            [PreserveSig] int Unadvise(uint cookie);
            [PreserveSig] int SetOperationFlags(uint flags);
            [PreserveSig] int SetProgressMessage([MarshalAs(UnmanagedType.LPWStr)] string message);
            [PreserveSig] int SetProgressDialog(IntPtr dialog);
            [PreserveSig] int SetProperties(IntPtr properties);
            [PreserveSig] int SetOwnerWindow(IntPtr window);
            [PreserveSig] int ApplyPropertiesToItem(IShellItem item);
            [PreserveSig] int ApplyPropertiesToItems(IntPtr items);
            [PreserveSig] int RenameItem(IShellItem item, [MarshalAs(UnmanagedType.LPWStr)] string name, IFileOperationProgressSink? sink);
            [PreserveSig] int RenameItems(IntPtr items, [MarshalAs(UnmanagedType.LPWStr)] string name);
            [PreserveSig] int MoveItem(IShellItem item, IShellItem folder, [MarshalAs(UnmanagedType.LPWStr)] string? name, IFileOperationProgressSink? sink);
            [PreserveSig] int MoveItems(IntPtr items, IShellItem folder);
            [PreserveSig] int CopyItem(IShellItem item, IShellItem folder, [MarshalAs(UnmanagedType.LPWStr)] string? name, IFileOperationProgressSink? sink);
            [PreserveSig] int CopyItems(IntPtr items, IShellItem folder);
            [PreserveSig] int DeleteItem(IShellItem item, IFileOperationProgressSink sink);
            [PreserveSig] int DeleteItems(IntPtr items);
            [PreserveSig] int NewItem(IShellItem folder, uint attributes, [MarshalAs(UnmanagedType.LPWStr)] string name, [MarshalAs(UnmanagedType.LPWStr)] string? template, IFileOperationProgressSink? sink);
            [PreserveSig] int PerformOperations();
            [PreserveSig] int GetAnyOperationsAborted([MarshalAs(UnmanagedType.Bool)] out bool aborted);
        }

        [DllImport("shell32.dll", CharSet = CharSet.Unicode, PreserveSig = true)]
        private static extern int SHCreateItemFromParsingName(string path, IntPtr bind, ref Guid iid, out IShellItem item);
    }
}
