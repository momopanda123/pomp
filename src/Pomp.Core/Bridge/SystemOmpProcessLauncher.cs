using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Threading;
using Pomp.Diagnostics;

namespace Pomp.Bridge
{
    /// <summary>
    /// Starts the real omp process with <see cref="System.Diagnostics.Process"/>: redirected stdio, no window, given working
    /// directory and environment, correct Windows argument quoting, batch files (<c>omp.cmd</c>) run through
    /// <c>cmd.exe /d /e:on /v:off /s /c</c>, and the process placed in a kill-on-close Job Object so it cannot outlive Visual Studio.
    /// </summary>
    public sealed class SystemOmpProcessLauncher : IOmpProcessLauncher
    {
        private readonly IPompLogger _logger;

        public SystemOmpProcessLauncher(IPompLogger? logger = null)
        {
            _logger = logger ?? NullPompLogger.Instance;
        }

        /// <inheritdoc />
        public IOmpProcess Start(OmpLaunchOptions options)
        {
            if (options == null)
            {
                throw new ArgumentNullException(nameof(options));
            }

            if (string.IsNullOrWhiteSpace(options.ExecutablePath))
            {
                throw new ArgumentException("OmpLaunchOptions.ExecutablePath is empty.", nameof(options));
            }

            BuildCommand(options, out var fileName, out var arguments);

            var startInfo = new ProcessStartInfo
            {
                FileName = fileName,
                Arguments = arguments,
                UseShellExecute = false,
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
                StandardOutputEncoding = new UTF8Encoding(false),
                StandardErrorEncoding = new UTF8Encoding(false),
            };
            if (!string.IsNullOrEmpty(options.WorkingDirectory))
            {
                startInfo.WorkingDirectory = options.WorkingDirectory;
            }

            foreach (var pair in options.Environment)
            {
                startInfo.EnvironmentVariables[pair.Key] = pair.Value;
            }

            var job = WindowsJobObject.TryCreate(_logger);
            var process = new Process { StartInfo = startInfo, EnableRaisingEvents = true };
            SystemOmpProcess? wrapper = null;
            try
            {
                wrapper = new SystemOmpProcess(process, job, _logger);
                process.Start();
                wrapper.OnStarted();
                // User-supplied extra arguments may carry secrets (--api-key ...): the log gets a redacted command line.
                _logger.Info("Started omp: " + options.ExecutablePath + " " + WindowsCommandLine.Join(RedactSecrets(options.Arguments)) + " (pid " + process.Id + ")");
                return wrapper;
            }
            catch
            {
                if (wrapper != null)
                {
                    wrapper.Dispose();
                }
                else
                {
                    job?.Dispose();
                    process.Dispose();
                }

                throw;
            }
        }

        /// <summary>
        /// Copy of <paramref name="arguments"/> for logging: the value of any <c>--flag</c> whose name mentions a key, token, secret
        /// or password (<c>--api-key X</c> or <c>--api-key=X</c>) is replaced by <c>***</c>.
        /// </summary>
        internal static List<string> RedactSecrets(IList<string> arguments)
        {
            var shown = new List<string>(arguments.Count);
            for (var i = 0; i < arguments.Count; i++)
            {
                var argument = arguments[i] ?? string.Empty;
                var equals = argument.IndexOf('=');
                var name = equals > 0 ? argument.Substring(0, equals) : argument;
                if (!name.StartsWith("-", StringComparison.Ordinal) || !IsSecretName(name))
                {
                    shown.Add(argument);
                    continue;
                }

                if (equals > 0)
                {
                    shown.Add(name + "=***");
                    continue;
                }

                shown.Add(argument);
                if (i + 1 < arguments.Count)
                {
                    shown.Add("***");
                    i++;
                }
            }

            return shown;
        }

        private static bool IsSecretName(string flag)
        {
            foreach (var word in new[] { "key", "token", "secret", "password", "passwd", "credential", "auth" })
            {
                if (flag.IndexOf(word, StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    return true;
                }
            }

            return false;
        }

        /// <summary>Resolves the file name and argument string for a launch (batch files go through cmd.exe).</summary>
        internal static void BuildCommand(OmpLaunchOptions options, out string fileName, out string arguments)
        {
            if (WindowsCommandLine.IsBatchFile(options.ExecutablePath))
            {
                fileName = Environment.GetEnvironmentVariable("ComSpec");
                if (string.IsNullOrWhiteSpace(fileName))
                {
                    fileName = "cmd.exe";
                }

                arguments = WindowsCommandLine.BuildCmdArguments(options.ExecutablePath, options.Arguments);
                return;
            }

            fileName = options.ExecutablePath;
            arguments = WindowsCommandLine.Join(options.Arguments);
        }

        private sealed class SystemOmpProcess : IOmpProcess
        {
            private readonly Process _process;
            private readonly WindowsJobObject? _job;
            private readonly IPompLogger _logger;
            private int _disposed;
            private int _killed;
            private bool _jobAssigned;

            public SystemOmpProcess(Process process, WindowsJobObject? job, IPompLogger logger)
            {
                _process = process;
                _job = job;
                _logger = logger;
                _process.Exited += OnProcessExited;
            }

            public int Id { get; private set; }

            public Stream StandardInput => _process.StandardInput.BaseStream;

            public Stream StandardOutput => _process.StandardOutput.BaseStream;

            public Stream StandardError => _process.StandardError.BaseStream;

            public bool HasExited
            {
                get
                {
                    try
                    {
                        return _process.HasExited;
                    }
                    catch (InvalidOperationException)
                    {
                        return true;
                    }
                }
            }

            public int? ExitCode
            {
                get
                {
                    try
                    {
                        return _process.HasExited ? _process.ExitCode : (int?)null;
                    }
                    catch (InvalidOperationException)
                    {
                        return null;
                    }
                }
            }

            public event EventHandler? Exited;

            /// <summary>Called right after <see cref="Process.Start()"/>: records the id and ties the process to the job.</summary>
            public void OnStarted()
            {
                Id = _process.Id;
                if (_job != null)
                {
                    _jobAssigned = _job.TryAssign(_process, out var error);
                    if (!_jobAssigned)
                    {
                        _logger.Warn("Could not add omp (pid " + Id + ") to the kill-on-close job object (error " + error + "); it may outlive Visual Studio if VS crashes.");
                    }
                }
            }

            public void Kill()
            {
                if (Interlocked.Exchange(ref _killed, 1) == 1 || HasExited)
                {
                    return;
                }

                var terminated = false;
                try
                {
                    // TerminateJobObject kills omp and every process it started.
                    terminated = _jobAssigned && _job != null && _job.Terminate();
                }
                catch (Exception ex)
                {
                    _logger.Debug("TerminateJobObject failed: " + ex.Message);
                }

                if (terminated)
                {
                    return;
                }

                // No job (unsupported platform, assignment failed): kill the tree the slow way.
                try
                {
                    KillTreeWithTaskkill();
                }
                catch (Exception ex)
                {
                    _logger.Debug("taskkill failed: " + ex.Message);
                }

                try
                {
                    if (!_process.HasExited)
                    {
                        _process.Kill();
                    }
                }
                catch (Exception ex) when (ex is InvalidOperationException || ex is Win32Exception)
                {
                    // Already exited.
                }
            }

            public void Dispose()
            {
                if (Interlocked.Exchange(ref _disposed, 1) == 1)
                {
                    return;
                }

                try
                {
                    Kill();
                }
                catch
                {
                    // Best effort.
                }

                _process.Exited -= OnProcessExited;
                try
                {
                    _job?.Dispose();
                }
                catch
                {
                    // Best effort.
                }

                try
                {
                    _process.Dispose();
                }
                catch
                {
                    // Best effort.
                }
            }

            private void KillTreeWithTaskkill()
            {
                if (Id <= 0 || Environment.OSVersion.Platform != PlatformID.Win32NT)
                {
                    return;
                }

                var startInfo = new ProcessStartInfo("taskkill", "/PID " + Id + " /T /F")
                {
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                };
                using (var taskkill = Process.Start(startInfo))
                {
                    taskkill?.WaitForExit(5000);
                }
            }

            private void OnProcessExited(object sender, EventArgs e)
            {
                try
                {
                    Exited?.Invoke(this, EventArgs.Empty);
                }
                catch (Exception ex)
                {
                    _logger.Warn("Exited handler failed: " + ex.Message);
                }
            }
        }
    }
}
