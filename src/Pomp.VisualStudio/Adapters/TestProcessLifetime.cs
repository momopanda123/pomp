using System;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading.Tasks;

namespace Pomp.VisualStudio.Adapters
{
    /// <summary>Kill-on-close Windows job for a runner and its testhost/build children; taskkill is the fallback.</summary>
    internal sealed class TestProcessLifetime : IDisposable
    {
        private IntPtr handle;
        private bool assigned;

        public TestProcessLifetime()
        {
            if (Environment.OSVersion.Platform != PlatformID.Win32NT)
            {
                return;
            }

            handle = NativeMethods.CreateJobObject(IntPtr.Zero, null);
            if (handle == IntPtr.Zero)
            {
                return;
            }

            var info = new ExtendedLimitInformation();
            info.BasicLimitInformation.LimitFlags = 0x00002000; // JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE
            var size = Marshal.SizeOf(typeof(ExtendedLimitInformation));
            var memory = Marshal.AllocHGlobal(size);
            try
            {
                Marshal.StructureToPtr(info, memory, false);
                if (!NativeMethods.SetInformationJobObject(handle, 9, memory, (uint)size))
                {
                    Dispose();
                }
            }
            finally
            {
                Marshal.FreeHGlobal(memory);
            }
        }

        public void Assign(Process process)
        {
            if (handle != IntPtr.Zero)
            {
                try
                {
                    assigned = NativeMethods.AssignProcessToJobObject(handle, process.Handle);
                }
                catch (InvalidOperationException)
                {
                    // A runner that exited immediately has nothing left to assign.
                }
                catch (Win32Exception)
                {
                    // Some host jobs disallow nested assignment. taskkill /T remains available.
                }
            }
        }

        public async Task<string?> TerminateAsync(Process process)
        {
            if (assigned && handle != IntPtr.Zero && NativeMethods.TerminateJobObject(handle, 1))
            {
                return null;
            }

            try
            {
                if (Environment.OSVersion.Platform == PlatformID.Win32NT)
                {
                    // Resolve the OS binary explicitly; never invoke a shell or a workspace/PATH taskkill executable.
                    using (var killer = new Process
                    {
                        StartInfo = new ProcessStartInfo(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "taskkill.exe"),
                            "/PID " + process.Id.ToString(CultureInfo.InvariantCulture) + " /T /F")
                        {
                            UseShellExecute = false,
                            CreateNoWindow = true,
                        },
                        EnableRaisingEvents = true,
                    })
                    {
                        var ended = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
                        killer.Exited += (sender, args) => ended.TrySetResult(true);
                        if (killer.Start())
                        {
                            if (killer.HasExited)
                            {
                                ended.TrySetResult(true);
                            }

                            if (await Task.WhenAny(ended.Task, Task.Delay(5000)).ConfigureAwait(false) == ended.Task && killer.ExitCode == 0)
                            {
                                return null;
                            }

                            if (!killer.HasExited)
                            {
                                killer.Kill();
                            }
                        }
                    }
                }
                else
                {
                    // Used by standalone helper tests on newer runtimes; the VSIX itself runs on Windows/.NET Framework.
                    var killTree = typeof(Process).GetMethod("Kill", new[] { typeof(bool) });
                    if (killTree != null)
                    {
                        killTree.Invoke(process, new object[] { true });
                        return null;
                    }
                }

                if (!process.HasExited)
                {
                    process.Kill();
                    return "The runner was killed, but termination of every child process could not be confirmed.";
                }

                return null;
            }
            catch (Exception ex) when (ex is InvalidOperationException || ex is Win32Exception || ex is System.Reflection.TargetInvocationException)
            {
                return "Could not terminate the test process tree: " + TestRunnerText.Truncate(ex.Message, 1000);
            }
        }

        public void Dispose()
        {
            if (handle != IntPtr.Zero)
            {
                NativeMethods.CloseHandle(handle);
                handle = IntPtr.Zero;
            }
        }

        // Pointer-sized fields are essential for the VS 2019 x86 and VS 2022/2026 x64 hosts.
        [StructLayout(LayoutKind.Sequential)]
        private struct BasicLimitInformation
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
        private struct IoCounters
        {
            public ulong ReadOperationCount;
            public ulong WriteOperationCount;
            public ulong OtherOperationCount;
            public ulong ReadTransferCount;
            public ulong WriteTransferCount;
            public ulong OtherTransferCount;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct ExtendedLimitInformation
        {
            public BasicLimitInformation BasicLimitInformation;
            public IoCounters IoInfo;
            public UIntPtr ProcessMemoryLimit;
            public UIntPtr JobMemoryLimit;
            public UIntPtr PeakProcessMemoryUsed;
            public UIntPtr PeakJobMemoryUsed;
        }

        private static class NativeMethods
        {
            [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
            public static extern IntPtr CreateJobObject(IntPtr attributes, string? name);

            [DllImport("kernel32.dll", SetLastError = true)]
            [return: MarshalAs(UnmanagedType.Bool)]
            public static extern bool SetInformationJobObject(IntPtr job, int informationClass, IntPtr information, uint length);

            [DllImport("kernel32.dll", SetLastError = true)]
            [return: MarshalAs(UnmanagedType.Bool)]
            public static extern bool AssignProcessToJobObject(IntPtr job, IntPtr process);

            [DllImport("kernel32.dll", SetLastError = true)]
            [return: MarshalAs(UnmanagedType.Bool)]
            public static extern bool TerminateJobObject(IntPtr job, uint exitCode);

            [DllImport("kernel32.dll", SetLastError = true)]
            [return: MarshalAs(UnmanagedType.Bool)]
            public static extern bool CloseHandle(IntPtr handle);
        }
    }
}
