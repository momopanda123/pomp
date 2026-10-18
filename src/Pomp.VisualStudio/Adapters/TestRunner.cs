using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Xml;
using Pomp.Adapters;

namespace Pomp.VisualStudio.Adapters
{
    /// <summary>Immutable project-system snapshot: no COM/DTE objects cross onto the runner thread.</summary>
    internal sealed class TestProjectSnapshot
    {
        public TestProjectSnapshot(string name, string path, bool sdkStyle, string? outputAssembly, IReadOnlyList<string>? targetFrameworks = null)
        {
            Name = name;
            Path = path;
            SdkStyle = sdkStyle;
            OutputAssembly = outputAssembly;
            TargetFrameworks = targetFrameworks ?? Array.Empty<string>();
        }

        public string Name { get; }
        public string Path { get; }
        public bool SdkStyle { get; }
        public string? OutputAssembly { get; }
        public IReadOnlyList<string> TargetFrameworks { get; }
    }

    /// <summary>Standalone .NET test platform runner, deliberately independent of Visual Studio and shell execution.</summary>
    internal sealed class TestRunner
    {
        private readonly string? dotnetPath;
        private readonly string? vstestPath;
        private readonly string resultsRoot;

        public TestRunner(string? dotnetPath, string? vstestPath, string? resultsRoot = null)
        {
            this.dotnetPath = dotnetPath;
            this.vstestPath = vstestPath;
            this.resultsRoot = resultsRoot ?? System.IO.Path.Combine(System.IO.Path.GetTempPath(), "pomp-tests");
        }

        public static string? FindDotnet()
        {
            var path = Environment.GetEnvironmentVariable("PATH") ?? string.Empty;
            foreach (var entry in path.Split(System.IO.Path.PathSeparator))
            {
                try
                {
                    var directory = entry.Trim().Trim('"');
                    if (directory.Length > 0 && System.IO.Path.IsPathRooted(directory))
                    {
                        var candidate = System.IO.Path.Combine(directory, "dotnet.exe");
                        if (File.Exists(candidate))
                        {
                            return System.IO.Path.GetFullPath(candidate);
                        }
                    }
                }
                catch (Exception ex) when (ex is ArgumentException || ex is NotSupportedException || ex is IOException || ex is System.Security.SecurityException)
                {
                    // Ignore unusable PATH entries rather than falling back to the project working directory.
                }
            }

            // ProgramW6432 avoids the x86 Program Files redirection in the VS 2019 host.
            foreach (var root in new[] { Environment.GetEnvironmentVariable("ProgramW6432"), Environment.GetEnvironmentVariable("ProgramFiles"), Environment.GetEnvironmentVariable("ProgramFiles(x86)") })
            {
                if (!string.IsNullOrWhiteSpace(root))
                {
                    var candidate = System.IO.Path.Combine(root, "dotnet", "dotnet.exe");
                    if (File.Exists(candidate))
                    {
                        return candidate;
                    }
                }
            }

            return null;
        }

        public Task<TestListResult> ListAsync(TestProjectSnapshot project, TimeSpan timeout, CancellationToken cancellationToken) =>
            Task.Run(() => ListCoreAsync(project, timeout, cancellationToken));

        public Task<TestRunResult> RunAsync(TestProjectSnapshot project, string? filter, TimeSpan timeout, CancellationToken cancellationToken) =>
            Task.Run(() => RunCoreAsync(project, filter, timeout, cancellationToken));

        private async Task<TestListResult> ListCoreAsync(TestProjectSnapshot project, TimeSpan timeout, CancellationToken cancellationToken)
        {
            var result = new TestListResult();
            var executable = Executable(project, out var problem);
            if (problem != null)
            {
                result.Errors.Add(project.Name + ": " + problem);
                return result;
            }

            string? directory = null;
            var parser = new TestOutputParser();
            try
            {
                var arguments = new List<string>();
                string? listFile = null;
                if (project.SdkStyle)
                {
                    arguments.AddRange(new[] { "test", project.Path, "--list-tests", "--nologo", "-v", "q" });
                }
                else
                {
                    directory = CreateResultsDirectory();
                    listFile = System.IO.Path.Combine(directory, "tests.txt");
                    arguments.AddRange(new[] { project.OutputAssembly!, "/ListFullyQualifiedTests", "/ListTestsTargetPath:" + listFile });
                }

                var command = await ExecuteProcessAsync(executable!, arguments, System.IO.Path.GetDirectoryName(project.Path)!, timeout, cancellationToken,
                    project.SdkStyle ? (Action<string>)parser.AddLine : null).ConfigureAwait(false);
                if (command.Stopped)
                {
                    result.Errors.Add(project.Name + ": Test discovery timed out or was cancelled. " + (command.Error ?? string.Empty));
                }
                else if (command.Error != null || command.ExitCode != 0)
                {
                    result.Errors.Add(project.Name + ": " + (command.Error ?? "Test discovery exited with code " + command.ExitCode.ToString(CultureInfo.InvariantCulture) + ".") +
                        "\n" + TestRunnerText.Truncate(command.OutputTail, 4000));
                }
                else if (!project.SdkStyle)
                {
                    if (listFile == null || !File.Exists(listFile))
                    {
                        result.Errors.Add(project.Name + ": The test platform did not produce its fully qualified test list.");
                    }
                    else
                    {
                        using (var reader = new StreamReader(listFile))
                        {
                            var seen = new HashSet<string>(StringComparer.Ordinal);
                            string? line;
                            while ((line = await reader.ReadLineAsync().ConfigureAwait(false)) != null)
                            {
                                cancellationToken.ThrowIfCancellationRequested();
                                var name = line.Trim().TrimStart('﻿');
                                if (name.Length > 0 && seen.Add(name))
                                {
                                    if (result.Tests.Count == 2000)
                                    {
                                        result.Errors.Add(project.Name + ": Discovery was truncated at 2000 tests; select a narrower project.");
                                        break;
                                    }

                                    result.Tests.Add(new TestCaseInfo { Project = project.Name, FullyQualifiedName = TestRunnerText.Truncate(name, 4096) });
                                }
                            }
                        }
                    }
                }
                else if (!parser.HeaderFound && !parser.NoTestsFound)
                {
                    result.Errors.Add(project.Name + ": The runner did not produce a recognized test list. " + TestRunnerText.Truncate(command.OutputTail, 4000));
                }

                if (project.SdkStyle)
                {
                    result.Tests.AddRange(parser.Names.Select(n => new TestCaseInfo { Project = project.Name, FullyQualifiedName = n }));
                    if (parser.Truncated)
                    {
                        result.Errors.Add(project.Name + ": Discovery was truncated at 2000 tests; select a narrower project.");
                    }
                }
            }
            catch (OperationCanceledException)
            {
                result.Errors.Add(project.Name + ": Test discovery was cancelled.");
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException || ex is Win32Exception || ex is ArgumentException)
            {
                result.Errors.Add(project.Name + ": Test discovery failed: " + TestRunnerText.Truncate(ex.Message, 1000));
            }
            finally
            {
                var cleanup = DeleteResultsDirectory(directory);
                if (cleanup != null)
                {
                    result.Errors.Add(cleanup);
                }
            }

            return result;
        }

        private async Task<TestRunResult> RunCoreAsync(TestProjectSnapshot project, string? filter, TimeSpan timeout, CancellationToken cancellationToken)
        {
            var result = new TestRunResult();
            var executable = Executable(project, out var problem);
            if (problem != null)
            {
                result.Error = project.Name + ": " + problem;
                return result;
            }

            var output = new TestOutputTail();
            var elapsed = Stopwatch.StartNew();
            string? directory = null;
            try
            {
                directory = CreateResultsDirectory();
                // A fixed LogFileName is overwritten by a multi-targeted dotnet test run. Run each evaluated TFM
                // separately instead, retaining the required filename logger and correct combined counters.
                var frameworks = project.SdkStyle && project.TargetFrameworks.Count > 1
                    ? project.TargetFrameworks.Cast<string?>().ToArray()
                    : new string?[] { null };
                for (var index = 0; index < frameworks.Length; index++)
                {
                    var remaining = timeout - elapsed.Elapsed;
                    if (remaining <= TimeSpan.Zero || cancellationToken.IsCancellationRequested)
                    {
                        result.TimedOut = true;
                        result.Error = TestRunnerText.JoinError(result.Error, project.Name + ": The test run timed out or was cancelled.");
                        break;
                    }

                    var targetDirectory = System.IO.Path.Combine(directory, index.ToString(CultureInfo.InvariantCulture));
                    Directory.CreateDirectory(targetDirectory);
                    var name = "pomp-" + Guid.NewGuid().ToString("N") + ".trx";
                    var arguments = new List<string>();
                    if (project.SdkStyle)
                    {
                        arguments.AddRange(new[] { "test", project.Path, "--nologo" });
                        if (!string.IsNullOrWhiteSpace(filter))
                        {
                            arguments.AddRange(new[] { "--filter", filter! });
                        }

                        if (frameworks[index] != null)
                        {
                            arguments.AddRange(new[] { "--framework", frameworks[index]! });
                        }

                        arguments.AddRange(new[] { "--logger", "trx;LogFileName=" + name, "--results-directory", targetDirectory });
                    }
                    else
                    {
                        arguments.AddRange(new[] { project.OutputAssembly!, "/Logger:trx;LogFileName=" + name, "/ResultsDirectory:" + targetDirectory });
                        if (!string.IsNullOrWhiteSpace(filter))
                        {
                            arguments.Add("/TestCaseFilter:" + filter);
                        }
                    }

                    output.Add("Project: " + project.Name + (frameworks[index] == null ? string.Empty : " (" + frameworks[index] + ")"));
                    var command = await ExecuteProcessAsync(executable!, arguments, System.IO.Path.GetDirectoryName(project.Path)!, remaining, cancellationToken).ConfigureAwait(false);
                    output.AddText(command.OutputTail);
                    var trxFound = false;
                    var parsed = false;
                    var targetFailed = 0;
                    foreach (var file in Directory.EnumerateFiles(targetDirectory, "*.trx", SearchOption.AllDirectories).Take(100))
                    {
                        trxFound = true;
                        try
                        {
                            var data = TrxParser.ParseFile(file, project.Name);
                            targetFailed = (int)Math.Min(int.MaxValue, (long)targetFailed + data.Failed);
                            TrxParser.Merge(result, data);
                            parsed = true;
                        }
                        catch (Exception ex) when (ex is XmlException || ex is IOException || ex is UnauthorizedAccessException)
                        {
                            result.Error = TestRunnerText.JoinError(result.Error, project.Name + ": Could not read a TRX result: " + ex.Message);
                        }
                    }

                    if (command.Stopped)
                    {
                        result.TimedOut = true;
                        result.Error = TestRunnerText.JoinError(result.Error, project.Name + ": The test run timed out or was cancelled.");
                    }

                    if (command.Error != null)
                    {
                        result.Error = TestRunnerText.JoinError(result.Error, project.Name + ": " + command.Error);
                    }

                    if (!command.Stopped && command.Error == null)
                    {
                        if (!trxFound)
                        {
                            result.Error = TestRunnerText.JoinError(result.Error, project.Name + ": The runner produced no TRX results (exit code " + command.ExitCode.ToString(CultureInfo.InvariantCulture) + "). Check the output for build or test-platform errors.");
                        }
                        else if (command.ExitCode != 0 && (command.ExitCode != 1 || !parsed || targetFailed == 0))
                        {
                            result.Error = TestRunnerText.JoinError(result.Error, project.Name + ": The runner exited with code " + command.ExitCode.ToString(CultureInfo.InvariantCulture) + "; the run did not finish normally.");
                        }
                    }

                    if (result.TimedOut)
                    {
                        break;
                    }
                }
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException || ex is Win32Exception || ex is ArgumentException)
            {
                result.Error = TestRunnerText.JoinError(result.Error, project.Name + ": Could not run tests: " + ex.Message);
            }
            finally
            {
                var cleanup = DeleteResultsDirectory(directory);
                if (cleanup != null)
                {
                    result.Error = TestRunnerText.JoinError(result.Error, cleanup);
                }

                result.OutputTail = output.Text;
            }

            return result;
        }

        private string? Executable(TestProjectSnapshot project, out string? problem)
        {
            problem = null;
            if (!File.Exists(project.Path))
            {
                problem = "The project file no longer exists.";
                return null;
            }

            if (!project.SdkStyle && (string.IsNullOrWhiteSpace(project.OutputAssembly) || !File.Exists(project.OutputAssembly)))
            {
                problem = "The non-SDK test project's output assembly does not exist. Build it in Visual Studio first.";
                return null;
            }

            var executable = project.SdkStyle ? dotnetPath : vstestPath;
            if (string.IsNullOrEmpty(executable) || !File.Exists(executable))
            {
                problem = project.SdkStyle
                    ? "dotnet.exe was not found on PATH or in Program Files\\dotnet. Install/configure the required .NET SDK."
                    : "vstest.console.exe was not found in this Visual Studio installation's Common7\\IDE\\Extensions\\TestPlatform directory.";
                return null;
            }

            return executable;
        }

        private string CreateResultsDirectory()
        {
            var directory = System.IO.Path.Combine(resultsRoot, Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(directory);
            return directory;
        }

        private static string? DeleteResultsDirectory(string? directory)
        {
            if (directory == null)
            {
                return null;
            }

            try
            {
                Directory.Delete(directory, true);
                return null;
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
            {
                return "Could not delete temporary test results at '" + directory + "': " + TestRunnerText.Truncate(ex.Message, 1000);
            }
        }

        internal static async Task<TestCommandResult> ExecuteProcessAsync(string executable, IReadOnlyList<string> arguments, string workingDirectory,
            TimeSpan timeout, CancellationToken cancellationToken, Action<string>? onOutput = null)
        {
            var result = new TestCommandResult();
            var tail = new TestOutputTail();
            if (timeout <= TimeSpan.Zero || cancellationToken.IsCancellationRequested)
            {
                result.Stopped = true;
                return result;
            }

            using (var lifetime = new TestProcessLifetime())
            using (var process = new Process
            {
                StartInfo = new ProcessStartInfo(executable, string.Join(" ", arguments.Select(QuoteArgument)))
                {
                    WorkingDirectory = workingDirectory,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    StandardOutputEncoding = Encoding.UTF8,
                    StandardErrorEncoding = Encoding.UTF8,
                },
                EnableRaisingEvents = true,
            })
            {
                process.StartInfo.EnvironmentVariables["DOTNET_CLI_UI_LANGUAGE"] = "en";
                process.StartInfo.EnvironmentVariables["DOTNET_NOLOGO"] = "1";
                process.StartInfo.EnvironmentVariables["DOTNET_CLI_TELEMETRY_OPTOUT"] = "1";
                process.StartInfo.EnvironmentVariables["VSLANG"] = "1033";
                process.StartInfo.EnvironmentVariables["MSBUILDDISABLENODEREUSE"] = "1";
                var ended = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
                process.Exited += (sender, args) => ended.TrySetResult(true);
                try
                {
                    if (!process.Start())
                    {
                        result.Error = "The test runner could not be started.";
                        return result;
                    }
                }
                catch (Exception ex) when (ex is Win32Exception || ex is InvalidOperationException)
                {
                    result.Error = "The test runner could not be started: " + TestRunnerText.Truncate(ex.Message, 1000);
                    return result;
                }

                lifetime.Assign(process);
                var stdout = PumpAsync(process.StandardOutput, tail, onOutput);
                var stderr = PumpAsync(process.StandardError, tail, null);
                if (process.HasExited)
                {
                    ended.TrySetResult(true);
                }

                using (var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken))
                {
                    deadline.CancelAfter(timeout);
                    var stopped = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
                    using (deadline.Token.Register(() => stopped.TrySetResult(true)))
                    {
                        if (await Task.WhenAny(ended.Task, stopped.Task).ConfigureAwait(false) != ended.Task)
                        {
                            result.Stopped = true;
                            result.Error = await lifetime.TerminateAsync(process).ConfigureAwait(false);
                            await Task.WhenAny(ended.Task, Task.Delay(5000)).ConfigureAwait(false);
                        }

                        // Closing the job also stops any surviving testhost children that still hold redirected pipes.
                        lifetime.Dispose();
                        var drained = Task.WhenAll(stdout, stderr);
                        if (await Task.WhenAny(drained, Task.Delay(5000)).ConfigureAwait(false) == drained)
                        {
                            await drained.ConfigureAwait(false);
                        }
                        else
                        {
                            result.Error = TestRunnerText.JoinError(result.Error, "The runner's redirected output did not close after termination.");
                        }
                    }
                }

                result.ExitCode = process.HasExited ? process.ExitCode : -1;
                result.OutputTail = tail.Text;
                return result;
            }
        }

        // .NET Framework lacks ProcessStartInfo.ArgumentList. Windows CRT quoting preserves each argument exactly,
        // including quotes, backslashes, filter operators, spaces and shell metacharacters, with UseShellExecute=false.
        internal static string QuoteArgument(string argument)
        {
            if (argument.IndexOf('\0') >= 0)
            {
                throw new ArgumentException("A process argument contains a NUL character.", nameof(argument));
            }

            var builder = new StringBuilder("\"");
            var slashes = 0;
            foreach (var character in argument)
            {
                if (character == '\\')
                {
                    slashes++;
                    continue;
                }

                if (character == '"')
                {
                    builder.Append('\\', slashes * 2 + 1).Append('"');
                }
                else
                {
                    builder.Append('\\', slashes).Append(character);
                }

                slashes = 0;
            }

            return builder.Append('\\', slashes * 2).Append('"').ToString();
        }

        private static async Task PumpAsync(StreamReader reader, TestOutputTail output, Action<string>? onLine)
        {
            var buffer = new char[4096];
            var line = new StringBuilder();
            var shortened = false;
            try
            {
                int count;
                while ((count = await reader.ReadAsync(buffer, 0, buffer.Length).ConfigureAwait(false)) > 0)
                {
                    for (var i = 0; i < count; i++)
                    {
                        var character = buffer[i];
                        if (character == '\r' || character == '\n')
                        {
                            if (line.Length > 0 || shortened)
                            {
                                var text = line + (shortened ? " [line truncated]" : string.Empty);
                                output.Add(text);
                                onLine?.Invoke(text);
                                line.Clear();
                                shortened = false;
                            }
                        }
                        else if (line.Length < 8192)
                        {
                            line.Append(character);
                        }
                        else
                        {
                            shortened = true;
                        }
                    }
                }

                if (line.Length > 0 || shortened)
                {
                    var text = line + (shortened ? " [line truncated]" : string.Empty);
                    output.Add(text);
                    onLine?.Invoke(text);
                }
            }
            catch (Exception ex) when (ex is IOException || ex is ObjectDisposedException)
            {
                output.Add("[Output stream closed: " + TestRunnerText.Truncate(ex.Message, 300) + "]");
            }
        }
    }

    internal sealed class TestCommandResult
    {
        public int ExitCode { get; set; } = -1;
        public bool Stopped { get; set; }
        public string? Error { get; set; }
        public string? OutputTail { get; set; }
    }

    internal sealed class TestOutputTail
    {
        private readonly object gate = new object();
        private readonly Queue<string> lines = new Queue<string>();
        private int length;

        public void Add(string text)
        {
            var line = TestRunnerText.Truncate(text, 2000);
            lock (gate)
            {
                lines.Enqueue(line);
                length += line.Length + 1;
                while (lines.Count > 80 || length > 16000)
                {
                    length -= lines.Dequeue().Length + 1;
                }
            }
        }

        public void AddText(string? text)
        {
            using (var reader = new StringReader(text ?? string.Empty))
            {
                string? line;
                while ((line = reader.ReadLine()) != null)
                {
                    Add(line);
                }
            }
        }

        public string Text
        {
            get
            {
                lock (gate)
                {
                    return string.Join("\n", lines);
                }
            }
        }
    }

    internal static class TestRunnerText
    {
        public static string Truncate(string? text, int maximum)
        {
            if (string.IsNullOrEmpty(text))
            {
                return string.Empty;
            }

            return text!.Length <= maximum ? text : text.Substring(0, maximum);
        }

        public static string JoinError(string? error, string message) => Truncate(string.IsNullOrEmpty(error) ? message : error + "\n" + message, 8000);
    }
}
