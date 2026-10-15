using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using Pomp.Diagnostics;

namespace Pomp.Bridge
{
    /// <summary>
    /// A Windows Job Object with <c>JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE</c>: every process assigned to it (and, because
    /// children inherit job membership, its whole tree) dies when <see cref="Terminate"/> is called or when the last handle
    /// to the job closes, including when the host process itself exits or crashes. The struct layouts follow the Win32
    /// definitions with pointer-sized fields, so the same code is correct in 32-bit (VS 2019) and 64-bit (VS 2022+) hosts.
    /// </summary>
    internal sealed class WindowsJobObject : IDisposable
    {
        private const int JobObjectExtendedLimitInformation = 9;
        private const uint JobObjectLimitKillOnJobClose = 0x00002000;

        private readonly object _lock = new object();
        private IntPtr _handle;

        private WindowsJobObject(IntPtr handle)
        {
            _handle = handle;
        }

        /// <summary>Creates a kill-on-close job, or returns null (with a log entry) when the platform or the OS refuses.</summary>
        public static WindowsJobObject? TryCreate(IPompLogger? logger = null)
        {
            if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            {
                return null;
            }

            IntPtr handle = IntPtr.Zero;
            IntPtr buffer = IntPtr.Zero;
            try
            {
                handle = NativeMethods.CreateJobObject(IntPtr.Zero, null);
                if (handle == IntPtr.Zero)
                {
                    logger?.Warn("CreateJobObject failed (error " + Marshal.GetLastWin32Error() + "); omp will not be tied to the host lifetime.");
                    return null;
                }

                var info = new JOBOBJECT_EXTENDED_LIMIT_INFORMATION();
                info.BasicLimitInformation.LimitFlags = JobObjectLimitKillOnJobClose;
                var size = Marshal.SizeOf(typeof(JOBOBJECT_EXTENDED_LIMIT_INFORMATION));
                buffer = Marshal.AllocHGlobal(size);
                Marshal.StructureToPtr(info, buffer, false);
                if (!NativeMethods.SetInformationJobObject(handle, JobObjectExtendedLimitInformation, buffer, (uint)size))
                {
                    logger?.Warn("SetInformationJobObject failed (error " + Marshal.GetLastWin32Error() + "); omp will not be tied to the host lifetime.");
                    return null; // the finally block closes the handle
                }

                var job = new WindowsJobObject(handle);
                handle = IntPtr.Zero;
                return job;
            }
            catch (Exception ex) when (ex is DllNotFoundException || ex is EntryPointNotFoundException || ex is BadImageFormatException)
            {
                logger?.Warn("Job objects are unavailable: " + ex.Message);
                return null;
            }
            finally
            {
                if (buffer != IntPtr.Zero)
                {
                    Marshal.FreeHGlobal(buffer);
                }

                if (handle != IntPtr.Zero)
                {
                    NativeMethods.CloseHandle(handle);
                }
            }
        }

        /// <summary>Adds a running process to the job. Returns false (Win32 error in <paramref name="error"/>) when that failed.</summary>
        public bool TryAssign(Process process, out int error)
        {
            error = 0;
            lock (_lock)
            {
                if (_handle == IntPtr.Zero)
                {
                    error = -1;
                    return false;
                }

                try
                {
                    if (NativeMethods.AssignProcessToJobObject(_handle, process.Handle))
                    {
                        return true;
                    }

                    error = Marshal.GetLastWin32Error();
                    return false;
                }
                catch (Exception ex) when (ex is InvalidOperationException || ex is System.ComponentModel.Win32Exception)
                {
                    // The process already exited (no handle any more).
                    error = -2;
                    return false;
                }
            }
        }

        /// <summary>Kills every process in the job.</summary>
        public bool Terminate(uint exitCode = 1)
        {
            lock (_lock)
            {
                return _handle != IntPtr.Zero && NativeMethods.TerminateJobObject(_handle, exitCode);
            }
        }

        /// <summary>Closes the job handle; with KILL_ON_JOB_CLOSE that kills whatever is still inside.</summary>
        public void Dispose()
        {
            lock (_lock)
            {
                if (_handle != IntPtr.Zero)
                {
                    NativeMethods.CloseHandle(_handle);
                    _handle = IntPtr.Zero;
                }
            }
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct JOBOBJECT_BASIC_LIMIT_INFORMATION
        {
            public long PerProcessUserTimeLimit;
            public long PerJobUserTimeLimit;
            public uint LimitFlags;
            public UIntPtr MinimumWorkingSetSize;
            public UIntPtr MaximumWorkingSetSize;
            public uint ActiveProcessLimit;
            public UIntPtr Affinity;
            public uint PriorityClass;
            public uint SchedulingClass;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct IO_COUNTERS
        {
            public ulong ReadOperationCount;
            public ulong WriteOperationCount;
            public ulong OtherOperationCount;
            public ulong ReadTransferCount;
            public ulong WriteTransferCount;
            public ulong OtherTransferCount;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct JOBOBJECT_EXTENDED_LIMIT_INFORMATION
        {
            public JOBOBJECT_BASIC_LIMIT_INFORMATION BasicLimitInformation;
            public IO_COUNTERS IoInfo;
            public UIntPtr ProcessMemoryLimit;
            public UIntPtr JobMemoryLimit;
            public UIntPtr PeakProcessMemoryUsed;
            public UIntPtr PeakJobMemoryUsed;
        }

        private static class NativeMethods
        {
            [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
            public static extern IntPtr CreateJobObject(IntPtr lpJobAttributes, string? lpName);

            [DllImport("kernel32.dll", SetLastError = true)]
            [return: MarshalAs(UnmanagedType.Bool)]
            public static extern bool SetInformationJobObject(IntPtr hJob, int jobObjectInfoClass, IntPtr lpJobObjectInfo, uint cbJobObjectInfoLength);

            [DllImport("kernel32.dll", SetLastError = true)]
            [return: MarshalAs(UnmanagedType.Bool)]
            public static extern bool AssignProcessToJobObject(IntPtr hJob, IntPtr hProcess);

            [DllImport("kernel32.dll", SetLastError = true)]
            [return: MarshalAs(UnmanagedType.Bool)]
            public static extern bool TerminateJobObject(IntPtr hJob, uint uExitCode);

            [DllImport("kernel32.dll", SetLastError = true)]
            [return: MarshalAs(UnmanagedType.Bool)]
            public static extern bool CloseHandle(IntPtr hObject);
        }
    }
}
