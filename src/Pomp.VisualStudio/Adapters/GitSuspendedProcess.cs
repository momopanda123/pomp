using System;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace Pomp.VisualStudio.Adapters
{
    /// <summary>Starts git suspended and assigns its job before its first instruction, so no hook/helper can escape a startup race.</summary>
    internal sealed class GitSuspendedProcess : IDisposable
    {
        private GitSuspendedProcess(Process process, FileStream stdout, FileStream stderr)
        {
            Process = process;
            Stdout = stdout;
            Stderr = stderr;
        }

        public Process Process { get; }

        public FileStream Stdout { get; }

        public FileStream Stderr { get; }

        public static GitSuspendedProcess Start(ProcessStartInfo start, GitProcessJob job)
        {
            var security = new SecurityAttributes { Length = Marshal.SizeOf(typeof(SecurityAttributes)), Inherit = true };
            using (var input = CreateFile("NUL", 0x80000000, 3, ref security, 3, 0, IntPtr.Zero))
            using (var stdoutWrite = CreateOutputPipe(ref security, out var stdoutRead))
            using (var stderrWrite = CreateOutputPipe(ref security, out var stderrRead))
            {
                if (input.IsInvalid)
                {
                    stdoutRead.Dispose();
                    stderrRead.Dispose();
                    throw new Win32Exception(Marshal.GetLastWin32Error(), "Could not open non-interactive git stdin.");
                }

                Process? process = null;
                FileStream? stdout = null;
                FileStream? stderr = null;
                var attributes = IntPtr.Zero;
                var handles = IntPtr.Zero;
                var environment = IntPtr.Zero;
                var native = new ProcessInformation();
                var listInitialized = false;
                try
                {
                    var size = IntPtr.Zero;
                    InitializeProcThreadAttributeList(IntPtr.Zero, 1, 0, ref size);
                    attributes = Marshal.AllocHGlobal(size);
                    if (!InitializeProcThreadAttributeList(attributes, 1, 0, ref size))
                    {
                        throw NativeError("Could not initialize git's inherited-handle list.");
                    }

                    listInitialized = true;
                    handles = Marshal.AllocHGlobal(IntPtr.Size * 3);
                    Marshal.WriteIntPtr(handles, 0, input.DangerousGetHandle());
                    Marshal.WriteIntPtr(handles, IntPtr.Size, stdoutWrite.DangerousGetHandle());
                    Marshal.WriteIntPtr(handles, IntPtr.Size * 2, stderrWrite.DangerousGetHandle());
                    // PROC_THREAD_ATTRIBUTE_HANDLE_LIST: inherit only git's three standard handles, never unrelated IDE handles.
                    if (!UpdateProcThreadAttribute(attributes, 0, new IntPtr(0x00020002), handles, new IntPtr(IntPtr.Size * 3), IntPtr.Zero, IntPtr.Zero))
                    {
                        throw NativeError("Could not restrict git's inherited handles.");
                    }

                    var startup = new StartupInformationEx
                    {
                        Startup = new StartupInformation
                        {
                            Size = Marshal.SizeOf(typeof(StartupInformationEx)),
                            Flags = 0x00000100, // STARTF_USESTDHANDLES
                            Input = input.DangerousGetHandle(),
                            Output = stdoutWrite.DangerousGetHandle(),
                            Error = stderrWrite.DangerousGetHandle(),
                        },
                        AttributeList = attributes,
                    };
                    var variables = start.EnvironmentVariables.Keys.Cast<string>().OrderBy(k => k, StringComparer.OrdinalIgnoreCase)
                        .Select(k => k + "=" + start.EnvironmentVariables[k]);
                    environment = Marshal.StringToHGlobalUni(string.Join("\0", variables) + "\0\0");
                    var command = new StringBuilder(GitCli.QuoteArgument(start.FileName) + " " + start.Arguments);
                    // CREATE_SUSPENDED | CREATE_UNICODE_ENVIRONMENT | EXTENDED_STARTUPINFO_PRESENT | CREATE_NO_WINDOW
                    if (!CreateProcess(start.FileName, command, IntPtr.Zero, IntPtr.Zero, true, 0x08080404, environment, start.WorkingDirectory, ref startup, out native))
                    {
                        throw NativeError("git.exe could not be started.");
                    }

                    job.Assign(native.Process);
                    process = Process.GetProcessById((int)native.ProcessId);
                    process.EnableRaisingEvents = true;
                    // Open the managed process handle before the process can exit; we still need its ExitCode after exit.
                    _ = process.Handle;
                    stdout = new FileStream(stdoutRead, FileAccess.Read, 8192, isAsync: false);
                    stderr = new FileStream(stderrRead, FileAccess.Read, 8192, isAsync: false);
                    if (ResumeThread(native.Thread) == uint.MaxValue)
                    {
                        throw NativeError("The protected git process could not be resumed.");
                    }

                    return new GitSuspendedProcess(process, stdout, stderr);
                }
                catch
                {
                    if (native.Process != IntPtr.Zero)
                    {
                        job.Terminate();
                        TerminateProcess(native.Process, 1);
                    }

                    stdout?.Dispose();
                    stderr?.Dispose();
                    stdoutRead.Dispose();
                    stderrRead.Dispose();
                    process?.Dispose();
                    throw;
                }
                finally
                {
                    if (native.Thread != IntPtr.Zero) CloseHandle(native.Thread);
                    if (native.Process != IntPtr.Zero) CloseHandle(native.Process);
                    if (environment != IntPtr.Zero) Marshal.FreeHGlobal(environment);
                    if (handles != IntPtr.Zero) Marshal.FreeHGlobal(handles);
                    if (listInitialized) DeleteProcThreadAttributeList(attributes);
                    if (attributes != IntPtr.Zero) Marshal.FreeHGlobal(attributes);
                }
            }
        }

        public void Dispose()
        {
            Stdout.Dispose();
            Stderr.Dispose();
            Process.Dispose();
        }

        private static SafeFileHandle CreateOutputPipe(ref SecurityAttributes security, out SafeFileHandle read)
        {
            if (!CreatePipe(out read, out var write, ref security, 0))
            {
                throw NativeError("Could not create a git output pipe.");
            }

            if (!SetHandleInformation(read, 1, 0)) // HANDLE_FLAG_INHERIT off on the parent-side reader.
            {
                read.Dispose();
                write.Dispose();
                throw NativeError("Could not protect the git output reader.");
            }

            return write;
        }

        private static Win32Exception NativeError(string message) => new Win32Exception(Marshal.GetLastWin32Error(), message);

        [StructLayout(LayoutKind.Sequential)]
        private struct SecurityAttributes
        {
            public int Length;
            public IntPtr Descriptor;
            [MarshalAs(UnmanagedType.Bool)] public bool Inherit;
        }

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct StartupInformation
        {
            public int Size;
            public string? Reserved;
            public string? Desktop;
            public string? Title;
            public uint X;
            public uint Y;
            public uint XSize;
            public uint YSize;
            public uint XCountChars;
            public uint YCountChars;
            public uint FillAttribute;
            public uint Flags;
            public ushort ShowWindow;
            public ushort ReservedSize;
            public IntPtr ReservedPointer;
            public IntPtr Input;
            public IntPtr Output;
            public IntPtr Error;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct StartupInformationEx
        {
            public StartupInformation Startup;
            public IntPtr AttributeList;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct ProcessInformation
        {
            public IntPtr Process;
            public IntPtr Thread;
            public uint ProcessId;
            public uint ThreadId;
        }

        [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        private static extern SafeFileHandle CreateFile(string name, uint access, uint share, ref SecurityAttributes security, uint creation, uint flags, IntPtr template);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool CreatePipe(out SafeFileHandle read, out SafeFileHandle write, ref SecurityAttributes security, uint size);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool SetHandleInformation(SafeFileHandle handle, uint mask, uint flags);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool InitializeProcThreadAttributeList(IntPtr attributes, int count, uint flags, ref IntPtr size);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool UpdateProcThreadAttribute(IntPtr attributes, uint flags, IntPtr attribute, IntPtr value, IntPtr size, IntPtr previous, IntPtr returnSize);

        [DllImport("kernel32.dll")]
        private static extern void DeleteProcThreadAttributeList(IntPtr attributes);

        [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool CreateProcess(string application, StringBuilder command, IntPtr processSecurity, IntPtr threadSecurity,
            [MarshalAs(UnmanagedType.Bool)] bool inherit, uint flags, IntPtr environment, string directory, ref StartupInformationEx startup, out ProcessInformation process);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern uint ResumeThread(IntPtr thread);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool TerminateProcess(IntPtr process, uint exitCode);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool CloseHandle(IntPtr handle);
    }
}
