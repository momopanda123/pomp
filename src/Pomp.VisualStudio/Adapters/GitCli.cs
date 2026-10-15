using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace Pomp.VisualStudio.Adapters
{
    /// <summary>Non-interactive, bounded git.exe invocations. No shell, and no UI-thread or Visual Studio dependency.</summary>
    internal sealed class GitCli
    {
        internal const int MaxStreamBytes = 1024 * 1024;
        internal static readonly TimeSpan ReadTimeout = TimeSpan.FromSeconds(30);
        internal static readonly TimeSpan CommitTimeout = TimeSpan.FromSeconds(60);
        internal static readonly TimeSpan PushTimeout = TimeSpan.FromSeconds(120);
        private static readonly UTF8Encoding Utf8 = new UTF8Encoding(false);
        private readonly string executable;

        public GitCli(string executable)
        {
            this.executable = Path.GetFullPath(executable);
        }

        /// <summary>PATH only, never the working directory. The VS fallback is resolved separately on the UI thread.</summary>
        public static string? FindOnPath()
        {
            var path = Environment.GetEnvironmentVariable("PATH") ?? string.Empty;
            foreach (var directory in path.Split(Path.PathSeparator))
            {
                var candidateDirectory = directory.Trim().Trim('"');
                if (candidateDirectory.Length == 0 || !Path.IsPathRooted(candidateDirectory))
                {
                    continue;
                }

                try
                {
                    var candidate = Path.GetFullPath(Path.Combine(candidateDirectory, "git.exe"));
                    if (File.Exists(candidate))
                    {
                        return candidate;
                    }
                }
                catch (Exception ex) when (ex is ArgumentException || ex is NotSupportedException || ex is PathTooLongException)
                {
                    // An invalid PATH segment must not hide a later usable git.exe.
                }
            }

            return null;
        }

        public Task<GitCliResult> RunAsync(string root, IEnumerable<string> arguments, TimeSpan timeout, CancellationToken cancellationToken)
        {
            // Task.Run guarantees even process startup and cancellation cleanup stay off the IDE thread.
            var command = new List<string>
            {
                "-C", root, "-c", "core.quotepath=false", "-c", "color.ui=false",
                "-c", "i18n.commitEncoding=UTF-8", "-c", "i18n.logOutputEncoding=UTF-8", "--literal-pathspecs",
            };
            command.AddRange(arguments);
            var commandLine = string.Join(" ", command.Select(QuoteArgument));
            if (commandLine.Length > 30000)
            {
                throw new ArgumentException("The git command is too long; use fewer or shorter paths.");
            }

            return Task.Run(() => RunCoreAsync(root, commandLine, timeout, cancellationToken), cancellationToken);
        }

        private async Task<GitCliResult> RunCoreAsync(string root, string commandLine, TimeSpan timeout, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var start = new ProcessStartInfo
            {
                FileName = executable,
                Arguments = commandLine,
                WorkingDirectory = root,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                StandardOutputEncoding = Utf8,
                StandardErrorEncoding = Utf8,
            };
            // -C must determine this operation's repository, not repository-location variables inherited by devenv.
            foreach (var name in new[] { "GIT_DIR", "GIT_WORK_TREE", "GIT_COMMON_DIR", "GIT_INDEX_FILE", "GIT_OBJECT_DIRECTORY", "GIT_ALTERNATE_OBJECT_DIRECTORIES" })
            {
                start.EnvironmentVariables.Remove(name);
            }

            start.EnvironmentVariables["GIT_TERMINAL_PROMPT"] = "0";
            start.EnvironmentVariables["GCM_INTERACTIVE"] = "never";
            start.EnvironmentVariables["GIT_ASKPASS"] = string.Empty;
            start.EnvironmentVariables["SSH_ASKPASS"] = string.Empty;
            start.EnvironmentVariables["SSH_ASKPASS_REQUIRE"] = "never";
            start.EnvironmentVariables["GIT_PAGER"] = "cat";
            start.EnvironmentVariables["LC_ALL"] = "C";
            // A read-only status must not refresh the on-disk index as an optional optimization.
            start.EnvironmentVariables["GIT_OPTIONAL_LOCKS"] = "0";

            using (var job = new GitProcessJob())
            using (var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken))
            {
                deadline.CancelAfter(timeout);
                using (var child = GitSuspendedProcess.Start(start, job))
                {
                    var process = child.Process;
                    var exited = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
                    process.Exited += (sender, args) => exited.TrySetResult(true);
                    Task? completion = null;
                    try
                    {
                    // The process was assigned to its job while suspended; stdin is NUL (EOF), never a prompt.
                    var stdout = CaptureAsync(child.Stdout);
                    var stderr = CaptureAsync(child.Stderr);
                    if (process.HasExited)
                    {
                        exited.TrySetResult(true);
                    }

                    // Include the pipe readers in the deadline: a helper that inherited a pipe can keep it open after git exits.
                    var finished = Task.WhenAll(exited.Task, stdout, stderr);
                    completion = finished;
                    var stopped = Task.Delay(Timeout.Infinite, deadline.Token);
                    if (await Task.WhenAny(finished, stopped).ConfigureAwait(false) != finished)
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        throw new TimeoutException("git.exe timed out after " + (int)timeout.TotalSeconds + " seconds; its process tree was stopped.");
                    }

                    await finished.ConfigureAwait(false);
                    cancellationToken.ThrowIfCancellationRequested();
                    var output = await stdout.ConfigureAwait(false);
                    var error = await stderr.ConfigureAwait(false);
                    return new GitCliResult(process.ExitCode, output.Text, error.Text, output.Truncated, error.Truncated);
                }
                    catch
                    {
                        job.Terminate();
                        KillRoot(process);
                        if (completion != null)
                        {
                            await Task.WhenAny(completion, Task.Delay(TimeSpan.FromSeconds(5))).ConfigureAwait(false);
                        }

                        // Closing the managed pipes unblocks a broken reader; preserve the original timeout/cancellation.
                        child.Stdout.Dispose();
                        child.Stderr.Dispose();
                        if (completion?.IsFaulted == true)
                        {
                            _ = completion.Exception;
                        }

                        throw;
                    }
                }
            }
        }

        private static async Task<CapturedStream> CaptureAsync(Stream stream)
        {
            var buffer = new byte[8192];
            using (var captured = new MemoryStream())
            {
                var truncated = false;
                while (true)
                {
                    var read = await stream.ReadAsync(buffer, 0, buffer.Length).ConfigureAwait(false);
                    if (read == 0)
                    {
                        break;
                    }

                    var keep = Math.Min(read, MaxStreamBytes - (int)captured.Length);
                    if (keep > 0)
                    {
                        await captured.WriteAsync(buffer, 0, keep).ConfigureAwait(false);
                    }

                    truncated |= keep < read;
                    // Continue draining after the cap; stopping here would deadlock git on a full pipe.
                }

                var bytes = captured.ToArray();
                if (!truncated)
                {
                    return new CapturedStream(Utf8.GetString(bytes), false);
                }

                // Do not decode a partial UTF-8 code point at the capture boundary.
                var characters = new char[Utf8.GetMaxCharCount(bytes.Length)];
                var count = Utf8.GetDecoder().GetChars(bytes, 0, bytes.Length, characters, 0, flush: false);
                return new CapturedStream(new string(characters, 0, count), true);
            }
        }

        private static void KillRoot(Process process)
        {
            try
            {
                if (!process.HasExited)
                {
                    process.Kill();
                }
            }
            catch (Exception ex) when (ex is Win32Exception || ex is InvalidOperationException)
            {
                // The job has already killed the tree, or the process raced its own exit.
            }
        }

        /// <summary>MSVCRT quoting; neither shell metacharacters nor paths are ever evaluated by cmd.exe.</summary>
        internal static string QuoteArgument(string argument)
        {
            if (argument == null || argument.IndexOf('\0') >= 0)
            {
                throw new ArgumentException("A git argument cannot be null or contain NUL.");
            }

            var result = new StringBuilder().Append('"');
            var backslashes = 0;
            foreach (var character in argument)
            {
                if (character == '\\')
                {
                    backslashes++;
                    continue;
                }

                result.Append('\\', character == '"' ? backslashes * 2 + 1 : backslashes);
                backslashes = 0;
                result.Append(character);
            }

            return result.Append('\\', backslashes * 2).Append('"').ToString();
        }

        private sealed class CapturedStream
        {
            public CapturedStream(string text, bool truncated)
            {
                Text = text;
                Truncated = truncated;
            }

            public string Text { get; }

            public bool Truncated { get; }
        }
    }

    internal sealed class GitCliResult
    {
        public GitCliResult(int exitCode, string stdout, string stderr, bool stdoutTruncated = false, bool stderrTruncated = false)
        {
            ExitCode = exitCode;
            Stdout = stdout;
            Stderr = stderr;
            StdoutTruncated = stdoutTruncated;
            StderrTruncated = stderrTruncated;
        }

        public int ExitCode { get; }

        public string Stdout { get; }

        public string Stderr { get; }

        public bool StdoutTruncated { get; }

        public bool StderrTruncated { get; }

        public string CombinedOutput => (Stdout.TrimEnd('\r', '\n') + (Stdout.Length > 0 && Stderr.Length > 0 ? "\n" : string.Empty) + Stderr.TrimEnd('\r', '\n'))
            + (StdoutTruncated || StderrTruncated ? "\n[git output truncated at 1 MiB per stream]" : string.Empty);

        public void EnsureSucceeded(string operation)
        {
            if (ExitCode != 0)
            {
                throw new InvalidOperationException("Git " + operation + " failed (exit " + ExitCode + "): " + CombinedOutput);
            }
        }
    }
}
