using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Pomp.Features;

namespace Pomp.VisualStudio.Isolation
{
    /// <summary>Opt-in, one-use AppContainer execution. No capability grants, live mounts, inherited environment/handles or ordinary fallback.</summary>
    internal sealed class AppContainerRunner : IIsolatedProcessRunner
    {
        private volatile bool blocked;
        private readonly SemaphoreSlim capacity = new SemaphoreSlim(1, 1);
        public bool Available => !blocked && Environment.OSVersion.Platform == PlatformID.Win32NT && Environment.OSVersion.Version >= new Version(6, 2);
        public async Task<IsolatedResult> RunAsync(IsolatedRequest request, CancellationToken token)
        {
            request.Validate();
            if (!Available) return new IsolatedResult { Error = "AppContainer requires supported Windows; no fallback was run." };
            await capacity.WaitAsync(token).ConfigureAwait(false);
            try { if (blocked) return new IsolatedResult { Error = "An earlier isolation cleanup is unconfirmed. Inspect it before any further run." }; var result = await Task.Run(() => Run(request, token), CancellationToken.None).ConfigureAwait(false); if (!result.TerminationConfirmed || result.CleanupWarning.Length > 0) blocked = true; return result; }
            finally { capacity.Release(); }
        }
        private static IsolatedResult Run(IsolatedRequest request, CancellationToken token)
        {
            var result = new IsolatedResult();
            var name = "Pomp.Isolated." + Guid.NewGuid().ToString("N");
            IntPtr sid = IntPtr.Zero, attributes = IntPtr.Zero, capabilities = IntPtr.Zero, environment = IntPtr.Zero, job = IntPtr.Zero;
            var process = new ProcessInfo(); bool ownedProfile = false, initialized = false, assigned = false;
            string output = "", profile = "";
            try
            {
                token.ThrowIfCancellationRequested();
                var creation = CreateAppContainerProfile(name, "POMP isolated command", "Temporary per-command POMP profile", IntPtr.Zero, 0, out sid); if (creation != 0) throw new COMException("A new private AppContainer profile could not be created.", creation); ownedProfile = true;
                var sidText = new SecurityIdentifier(sid).Value;
                IntPtr folder; Hr(GetAppContainerFolderPath(sidText, out folder));
                try { profile = Marshal.PtrToStringUni(folder) ?? throw new InvalidOperationException("AppContainer profile path unavailable."); }
                finally { Marshal.FreeCoTaskMem(folder); }
                CheckDirect(profile);
                var work = Path.Combine(profile, "PompWork"); Directory.CreateDirectory(work);
                var temp = Path.Combine(profile, "PompTemp"); Directory.CreateDirectory(temp);
                foreach (var input in request.Files)
                {
                    token.ThrowIfCancellationRequested();
                    var target = Path.Combine(work, input.Path.Replace('/', Path.DirectorySeparatorChar));
                    CheckDirect(Path.GetDirectoryName(target)!); Directory.CreateDirectory(Path.GetDirectoryName(target)!); CheckDirect(target);
                    File.WriteAllText(target, input.Text, new UTF8Encoding(false));
                }
                output = Path.Combine(profile, "pomp-output.txt");
                var script = "$ErrorActionPreference='Stop'; $global:LASTEXITCODE=0; try { & {\n" + request.Command + "\n} *>&1 | Out-File -LiteralPath '" + output.Replace("'", "''") + "' -Encoding utf8; $ok=$?; $code=$global:LASTEXITCODE; if(-not $ok -and $code -eq 0){$code=1}; exit $code } catch { $_ | Out-File -LiteralPath '" + output.Replace("'", "''") + "' -Encoding utf8 -Append; exit 1 }";
                var executable = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "WindowsPowerShell", "v1.0", "powershell.exe");
                if (!File.Exists(executable)) throw new FileNotFoundException("System PowerShell is unavailable; no fallback will run.");
                var command = new StringBuilder("\"" + executable + "\" -NoLogo -NoProfile -NonInteractive -EncodedCommand " + Convert.ToBase64String(Encoding.Unicode.GetBytes(script)));
                var systemRoot = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
                var variables = new SortedDictionary<string, string>(StringComparer.OrdinalIgnoreCase) {
                    ["SystemRoot"] = systemRoot, ["WINDIR"] = systemRoot, ["PATH"] = Environment.GetFolderPath(Environment.SpecialFolder.System),
                    ["TEMP"] = temp, ["TMP"] = temp, ["USERPROFILE"] = profile, ["LOCALAPPDATA"] = profile, ["APPDATA"] = profile };
                var block = new StringBuilder(); foreach (var pair in variables) block.Append(pair.Key).Append('=').Append(pair.Value).Append('\0'); block.Append('\0');
                environment = Marshal.StringToHGlobalUni(block.ToString());
                IntPtr size = IntPtr.Zero; InitializeProcThreadAttributeList(IntPtr.Zero, 1, 0, ref size);
                if (size.ToInt64() <= 0 || size.ToInt64() > 65536) throw new Win32Exception(Marshal.GetLastWin32Error(), "Cannot size process attributes.");
                attributes = Marshal.AllocHGlobal(size);
                Win(InitializeProcThreadAttributeList(attributes, 1, 0, ref size)); initialized = true;
                var caps = new SecurityCapabilities { AppContainerSid = sid, Capabilities = IntPtr.Zero, CapabilityCount = 0, Reserved = 0 };
                capabilities = Marshal.AllocHGlobal(Marshal.SizeOf(typeof(SecurityCapabilities))); Marshal.StructureToPtr(caps, capabilities, false);
                Win(UpdateProcThreadAttribute(attributes, 0, new IntPtr(0x20009), capabilities, new IntPtr(Marshal.SizeOf(typeof(SecurityCapabilities))), IntPtr.Zero, IntPtr.Zero));
                job = CreateJobObject(IntPtr.Zero, null); if (job == IntPtr.Zero) throw new Win32Exception(Marshal.GetLastWin32Error());
                var limits = new JobLimits { Basic = new BasicLimits { LimitFlags = 0x2000 | 0x8 | 0x100 | 0x200, ActiveProcessLimit = 16 }, ProcessMemoryLimit = new UIntPtr(512u * 1024 * 1024), JobMemoryLimit = new UIntPtr(1024u * 1024 * 1024) };
                Win(SetInformationJobObject(job, 9, ref limits, (uint)Marshal.SizeOf(typeof(JobLimits))));
                var startup = new StartupInfoEx { Info = new StartupInfo { Size = Marshal.SizeOf(typeof(StartupInfoEx)) }, Attributes = attributes };
                token.ThrowIfCancellationRequested();
                // Suspended until assigned to this private job. Never inherit VS handles or its secret-bearing environment.
                Win(CreateProcess(executable, command, IntPtr.Zero, IntPtr.Zero, false, 0x00080000 | 0x08000000 | 0x00000400 | 0x00000004, environment, work, ref startup, out process));
                Win(AssignProcessToJobObject(job, process.Process)); assigned = true;
                VerifyContainerToken(process.Process, sid);
                token.ThrowIfCancellationRequested();
                if (ResumeThread(process.Thread) == uint.MaxValue) throw new Win32Exception(Marshal.GetLastWin32Error());
                result.StartedInContainer = true;
                var clock = Stopwatch.StartNew();
                while (WaitForSingleObject(process.Process, 100) == 258)
                {
                    if (token.IsCancellationRequested || clock.Elapsed.TotalSeconds > request.TimeoutSeconds)
                        throw new OperationCanceledException(token.IsCancellationRequested ? "Isolated command cancelled." : "Isolated command timed out.");
                    if (ExceedsDiskBudget(profile)) throw new InvalidOperationException("Isolated profile exceeded its monitored disk/file budget. This is a best-effort monitor, not a disk quota.");
                }
                uint exit; Win(GetExitCodeProcess(process.Process, out exit));
                if (exit == 259) throw new InvalidOperationException("Isolated process exit is unconfirmed.");
                result.ExitCode = unchecked((int)exit);
            }
            catch (Exception ex) { result.Error = ex.Message; }
            finally
            {
                // Kill the entire owned job even after a normal root exit; no detached child survives by design.
                if (job != IntPtr.Zero && assigned)
                {
                    if (TerminateJobObject(job, 1))
                    {
                        var wait = Stopwatch.StartNew();
                        do { JobAccounting info; uint length; if (QueryInformationJobObject(job, 1, out info, (uint)Marshal.SizeOf(typeof(JobAccounting)), out length) && info.ActiveProcesses == 0) { result.TerminationConfirmed = true; break; } Thread.Sleep(50); } while (wait.Elapsed < TimeSpan.FromSeconds(5));
                    }
                }
                else if (process.Process != IntPtr.Zero)
                {
                    // Assignment failed while still suspended: terminate only the process created by this invocation.
                    if (TerminateProcess(process.Process, 1)) result.TerminationConfirmed = WaitForSingleObject(process.Process, 5000) == 0;
                }
                else result.TerminationConfirmed = true;
                if (process.Thread != IntPtr.Zero) CloseHandle(process.Thread);
                if (process.Process != IntPtr.Zero) CloseHandle(process.Process);
                if (job != IntPtr.Zero) CloseHandle(job);
                if (initialized) DeleteProcThreadAttributeList(attributes);
                if (attributes != IntPtr.Zero) Marshal.FreeHGlobal(attributes);
                if (capabilities != IntPtr.Zero) Marshal.FreeHGlobal(capabilities);
                if (environment != IntPtr.Zero) Marshal.FreeHGlobal(environment);
                if (sid != IntPtr.Zero) FreeSid(sid);
                if (result.TerminationConfirmed && output.Length > 0)
                {
                    try
                    {
                        CheckDirect(output);
                        if (File.Exists(output)) using (var reader = new StreamReader(output, Encoding.UTF8, true)) { var buffer = new char[65537]; var count = reader.ReadBlock(buffer, 0, buffer.Length); result.Output = new string(buffer, 0, Math.Min(65536, count)); result.OutputTruncated = count > 65536; }
                    }
                    catch (Exception ex) { result.Error += " Output unavailable: " + ex.Message; }
                }
                if (ownedProfile)
                {
                    if (!result.TerminationConfirmed) result.CleanupWarning = "Process termination unconfirmed; retained owned profile " + name + ". Inspect it before retrying.";
                    else
                    {
                        try
                        {
                            CheckDirect(profile);
                            if (ExceedsDiskBudget(profile)) result.CleanupWarning = "Profile contains linked paths or exceeds safe cleanup scan limits; retained for inspection: " + name;
                            else { var hr = DeleteAppContainerProfile(name); if (hr < 0) result.CleanupWarning = "Owned profile cleanup failed: " + name + " (0x" + hr.ToString("X8") + ")."; }
                        }
                        catch (Exception ex) { result.CleanupWarning = "Owned profile retained because cleanup could not be checked: " + name + ". " + ex.Message; }
                    }
                }
            }
            return result;
        }
        private static void VerifyContainerToken(IntPtr process, IntPtr expectedSid)
        {
            IntPtr token; Win(OpenProcessToken(process, 0x8, out token));
            try
            {
                foreach (var informationClass in new[] { 29, 30, 31 }) // TokenIsAppContainer, TokenCapabilities, TokenAppContainerSid
                {
                    uint bytes; GetTokenInformation(token, informationClass, IntPtr.Zero, 0, out bytes);
                    if (bytes < 4 || bytes > 65536) throw new InvalidOperationException("Container token information is unavailable.");
                    var buffer = Marshal.AllocHGlobal((int)bytes);
                    try
                    {
                        Win(GetTokenInformation(token, informationClass, buffer, bytes, out bytes));
                        if (informationClass == 29 && Marshal.ReadInt32(buffer) != 1) throw new InvalidOperationException("Process is not an AppContainer; it will not be resumed.");
                        if (informationClass == 30 && Marshal.ReadInt32(buffer) != 0) throw new InvalidOperationException("Unexpected capabilities were granted; process will not be resumed.");
                        if (informationClass == 31 && (bytes < IntPtr.Size || !EqualSid(Marshal.ReadIntPtr(buffer), expectedSid))) throw new InvalidOperationException("Process belongs to a different AppContainer; it will not be resumed.");
                    }
                    finally { Marshal.FreeHGlobal(buffer); }
                }
            }
            finally { CloseHandle(token); }
        }
        private static bool ExceedsDiskBudget(string root)
        {
            long bytes = 0; int entries = 0; var directories = new Stack<string>(); directories.Push(root);
            while (directories.Count > 0)
            {
                var directory = directories.Pop(); CheckDirect(directory);
                foreach (var item in Directory.EnumerateFileSystemEntries(directory))
                {
                    if (++entries > 2000) return true;
                    var attributes = File.GetAttributes(item); if ((attributes & FileAttributes.ReparsePoint) != 0) return true;
                    if ((attributes & FileAttributes.Directory) != 0) directories.Push(item);
                    else { bytes += new FileInfo(item).Length; if (bytes > 64 * 1024 * 1024) return true; }
                }
            }
            return false;
        }
        private static void CheckDirect(string path)
        {
            for (var cursor = Path.GetFullPath(path); !string.IsNullOrEmpty(cursor); cursor = Path.GetDirectoryName(cursor))
                if ((File.Exists(cursor) || Directory.Exists(cursor)) && (File.GetAttributes(cursor) & FileAttributes.ReparsePoint) != 0) throw new IOException("Linked isolation paths are refused.");
        }
        private static void Win(bool value) { if (!value) throw new Win32Exception(Marshal.GetLastWin32Error()); }
        private static void Hr(int value) { if (value < 0) Marshal.ThrowExceptionForHR(value); }
        [StructLayout(LayoutKind.Sequential)] private struct SecurityCapabilities { public IntPtr AppContainerSid, Capabilities; public uint CapabilityCount, Reserved; }
        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)] private struct StartupInfo { public int Size; public IntPtr Reserved, Desktop, Title; public int X, Y, XSize, YSize, XCountChars, YCountChars, FillAttribute, Flags; public short ShowWindow, Reserved2; public IntPtr ReservedBytes, StdInput, StdOutput, StdError; }
        [StructLayout(LayoutKind.Sequential)] private struct StartupInfoEx { public StartupInfo Info; public IntPtr Attributes; }
        [StructLayout(LayoutKind.Sequential)] private struct ProcessInfo { public IntPtr Process, Thread; public uint ProcessId, ThreadId; }
        [StructLayout(LayoutKind.Sequential)] private struct BasicLimits { public long PerProcessTime, PerJobTime; public uint LimitFlags; public UIntPtr MinimumWorkingSet, MaximumWorkingSet; public uint ActiveProcessLimit; public UIntPtr Affinity; public uint PriorityClass, SchedulingClass; }
        [StructLayout(LayoutKind.Sequential)] private struct IoCounters { public ulong ReadOperations, WriteOperations, OtherOperations, ReadBytes, WriteBytes, OtherBytes; }
        [StructLayout(LayoutKind.Sequential)] private struct JobLimits { public BasicLimits Basic; public IoCounters Io; public UIntPtr ProcessMemoryLimit, JobMemoryLimit, PeakProcessMemory, PeakJobMemory; }
        [StructLayout(LayoutKind.Sequential)] private struct JobAccounting { public long TotalUser, TotalKernel, PeriodUser, PeriodKernel; public uint PageFaults, TotalProcesses, ActiveProcesses, TerminatedProcesses; }
        [DllImport("userenv.dll", CharSet = CharSet.Unicode)] private static extern int CreateAppContainerProfile(string name, string display, string description, IntPtr capabilities, uint count, out IntPtr sid);
        [DllImport("userenv.dll", CharSet = CharSet.Unicode)] private static extern int GetAppContainerFolderPath(string sid, out IntPtr path);
        [DllImport("userenv.dll", CharSet = CharSet.Unicode)] private static extern int DeleteAppContainerProfile(string name);
        [DllImport("advapi32.dll", SetLastError = true)] private static extern bool OpenProcessToken(IntPtr process, uint access, out IntPtr token);
        [DllImport("advapi32.dll", SetLastError = true)] private static extern bool GetTokenInformation(IntPtr token, int type, IntPtr buffer, uint length, out uint returned);
        [DllImport("advapi32.dll")] private static extern bool EqualSid(IntPtr first, IntPtr second);
        [DllImport("advapi32.dll")] private static extern IntPtr FreeSid(IntPtr sid);
        [DllImport("kernel32.dll", SetLastError = true)] private static extern bool InitializeProcThreadAttributeList(IntPtr list, int count, int flags, ref IntPtr size);
        [DllImport("kernel32.dll", SetLastError = true)] private static extern bool UpdateProcThreadAttribute(IntPtr list, uint flags, IntPtr attribute, IntPtr value, IntPtr size, IntPtr previous, IntPtr returned);
        [DllImport("kernel32.dll")] private static extern void DeleteProcThreadAttributeList(IntPtr list);
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern bool CreateProcess(string application, StringBuilder command, IntPtr processAttributes, IntPtr threadAttributes, bool inherit, uint flags, IntPtr environment, string directory, ref StartupInfoEx startup, out ProcessInfo process);
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern IntPtr CreateJobObject(IntPtr attributes, string? name);
        [DllImport("kernel32.dll", SetLastError = true)] private static extern bool SetInformationJobObject(IntPtr job, int type, ref JobLimits information, uint length);
        [DllImport("kernel32.dll", SetLastError = true)] private static extern bool QueryInformationJobObject(IntPtr job, int type, out JobAccounting information, uint length, out uint returned);
        [DllImport("kernel32.dll", SetLastError = true)] private static extern bool AssignProcessToJobObject(IntPtr job, IntPtr process);
        [DllImport("kernel32.dll", SetLastError = true)] private static extern uint ResumeThread(IntPtr thread);
        [DllImport("kernel32.dll", SetLastError = true)] private static extern uint WaitForSingleObject(IntPtr handle, uint milliseconds);
        [DllImport("kernel32.dll", SetLastError = true)] private static extern bool GetExitCodeProcess(IntPtr process, out uint code);
        [DllImport("kernel32.dll", SetLastError = true)] private static extern bool TerminateJobObject(IntPtr job, uint code);
        [DllImport("kernel32.dll", SetLastError = true)] private static extern bool TerminateProcess(IntPtr process, uint code);
        [DllImport("kernel32.dll")] private static extern bool CloseHandle(IntPtr handle);
    }
}
