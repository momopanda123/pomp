using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;
using Pomp.Bridge;
using Pomp.Diagnostics;
using Pomp.Hosting;
using Pomp.Internal;
using Pomp.Permissions;
using Pomp.Tools;
using Pomp.Ui;

namespace Pomp.Features
{
    internal interface IDeferredLaunchCheck { Action? PrelaunchCheck { get; set; } }

    /// <summary>Approval-bound noninteractive PowerShell jobs. This is process supervision, NOT an OS security sandbox.</summary>
    internal sealed class TerminalJobs : IDisposable
    {
        public const string StateMessage = "workspace/terminalState", CommandMessage = "workspace/terminal";
        private readonly IPompHostEnvironment environment;
        private readonly UiHub ui;
        private readonly IOmpProcessLauncher launcher;
        private readonly Func<string, string, OmpLaunchOptions> buildLaunch;
        public Func<HostToolInvocation, string?>? ResolveSessionId { get; set; }
        private readonly object persistenceGate = new object();
        private string persistenceWarning = "";
        private readonly object gate = new object();
        private readonly List<Job> jobs = new List<Job>();
        private readonly SemaphoreSlim slots = new SemaphoreSlim(5, 5);
        private readonly CancellationTokenSource lifetime = new CancellationTokenSource();
        private readonly Timer timer;
        private bool paused;
        private string selectedJob = "", lastViewJson = "";
        private TaskCompletionSource<bool> resume = CompletedSignal();
        private sealed class Job
        {
            public string Id = Guid.NewGuid().ToString("N"), Root = "", Command = "", State = "queued", Error = "";
            public JObject Origin = new JObject { ["sessionId"] = "" };
            public DateTimeOffset Created = DateTimeOffset.UtcNow;
            public DateTimeOffset? Started, Finished;
            public int? ExitCode;
            public int TimeoutSeconds;
            public CancellationTokenSource Cancellation = new CancellationTokenSource();
            public IOmpProcess? Process;
            public Task Execution = Task.CompletedTask;
            public Action? PrelaunchCheck;
            public Queue<string> Output = new Queue<string>();
            public Queue<TraceFrame> Trace = new Queue<TraceFrame>();
            public Stopwatch TraceClock = new Stopwatch();
            public int Characters, Lines;
            public bool Truncated;
        }
        private sealed class TraceFrame
        {
            public TraceFrame(long atMs, string text) { AtMs = atMs; Text = text; }
            public long AtMs { get; }
            public string Text { get; }
        }
        public TerminalJobs(IPompHostEnvironment environment, UiHub ui, IOmpProcessLauncher? launcher = null, Func<string, string, OmpLaunchOptions>? buildLaunch = null)
        {
            this.environment = environment; this.ui = ui; this.buildLaunch = buildLaunch ?? BuildLaunch;
            // Do not log encoded command arguments or source snippets through the generic process launcher.
            this.launcher = launcher ?? new SystemOmpProcessLauncher(NullPompLogger.Instance);
            timer = new Timer(_ => Publish(false), null, 1000, 1000);
        }
        public bool HasUnfinishedWork { get { lock (gate) return jobs.Any(j => j.State == "queued" || j.State == "running" || j.State == "terminationUnconfirmed"); } }
        public IHostTool RunTool() => new RunJobTool(this);
        public IHostTool StatusTool() => new StatusJobTool(this);
        public JObject View(string? outputId = null)
        {
            lock (gate)
            {
                return new JObject { ["selectedId"] = selectedJob, ["paused"] = paused, ["warning"] = persistenceWarning, ["jobs"] = new JArray(jobs.Select(job => new JObject {
                    ["id"] = job.Id, ["root"] = job.Root, ["command"] = job.Command, ["state"] = job.State, ["origin"] = job.Origin.DeepClone(),
                    ["createdUtc"] = job.Created.ToString("O"), ["startedUtc"] = job.Started?.ToString("O"),
                    ["exitCode"] = job.ExitCode, ["error"] = job.Error, ["truncated"] = job.Truncated,
                    ["background"] = job.State == "running" && job.Started.HasValue && DateTimeOffset.UtcNow - job.Started.Value > TimeSpan.FromSeconds(30),
                    ["output"] = Tail(string.Concat(job.Output), job.Id == (outputId ?? selectedJob) ? 24000 : 1000) })) };
            }
        }
        public void Publish(bool force = true)
        {
            if (lifetime.IsCancellationRequested) return;
            try { var view = View(); var json = view.ToString(); lock (gate) { if (!force && json == lastViewJson) return; lastViewJson = json; } ui.Post(StateMessage, view, UiDelivery.State); } catch (ObjectDisposedException) { }
        }
        public void Handle(JObject args)
        {
            var action = (string?)args["action"];
            if (action == "trace") { PublishTrace(args); return; }
            CancellationTokenSource? toCancel = null;
            bool cancelAll = false;
            lock (gate)
            {
                if (action == "select" && jobs.Any(j => j.Id == (string?)args["id"])) selectedJob = (string)args["id"]!;
                if (action == "pause" && !paused) { paused = true; resume = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously); }
                if (action == "resume" && paused) { paused = false; resume.TrySetResult(true); }
                if (action == "cancel") toCancel = jobs.FirstOrDefault(j => j.Id == (string?)args["id"])?.Cancellation;
                if (action == "clear")
                {
                    // Finished jobs only: queued/running ones keep running, and the page can cancel them explicitly.
                    var removed = jobs.Where(j => j.State != "queued" && j.State != "running" && j.State != "terminationUnconfirmed").ToArray();
                    foreach (var job in removed) { jobs.Remove(job); job.Cancellation.Dispose(); }
                    if (removed.Any(j => j.Id == selectedJob))
                        selectedJob = jobs.Count > 0 ? jobs[jobs.Count - 1].Id : "";
                }
                if (action == "cancelAll") cancelAll = true;
            }
            if (toCancel != null) RequestCancel(toCancel);
            if (cancelAll) CancelAll();
            Publish();
        }
        private void PublishTrace(JObject args)
        {
            var requestId = args["requestId"]?.Type == JTokenType.String ? (string?)args["requestId"] : null;
            if (string.IsNullOrEmpty(requestId) || requestId!.Length > 128) return;
            var id = args["id"]?.Type == JTokenType.String ? (string?)args["id"] : null;
            if (id == null || id.Length > 128) return;
            var answer = new JObject { ["requestId"] = requestId, ["id"] = id, ["frames"] = new JArray() };
            lock (gate)
            {
                var job = jobs.FirstOrDefault(item => item.Id == id);
                if (job == null) answer["error"] = PompLanguage.Pick("이 터미널 작업은 더 이상 보관되어 있지 않아요.", "This terminal job is no longer retained.");
                else
                {
                    var all = job.Trace.ToArray(); var selected = new List<TraceFrame>(); var characters = 0;
                    for (var i = all.Length - 1; i >= 0 && selected.Count < 512; i--)
                    {
                        if (characters + all[i].Text.Length > 65536) break;
                        selected.Add(all[i]); characters += all[i].Text.Length;
                    }
                    selected.Reverse();
                    answer["root"] = job.Root; answer["state"] = job.State;
                    answer["truncated"] = job.Truncated || selected.Count < all.Length;
                    answer["frames"] = new JArray(selected.Select(frame => new JObject { ["atMs"] = frame.AtMs, ["text"] = frame.Text }));
                }
            }
            ui.Post("workspace/terminalTrace", answer);
        }
        private static void RequestCancel(CancellationTokenSource cancellation) => Task.Run(() => { try { cancellation.Cancel(); } catch (ObjectDisposedException) { } }).Forget(NullPompLogger.Instance, "terminal cancel");
        public void CancelAll() { CancellationTokenSource[] active; lock (gate) active = jobs.Where(j => j.State == "queued" || j.State == "running").Select(j => j.Cancellation).ToArray(); foreach (var cancellation in active) RequestCancel(cancellation); }
        internal Task WaitForJobAsync(string id, CancellationToken token) { Task task; lock (gate) task = jobs.FirstOrDefault(j => j.Id == id)?.Execution ?? Task.CompletedTask; return task.WithCancellation(token); }
        internal Task WaitForIdleAsync() { lock (gate) return Task.WhenAll(jobs.Select(j => j.Execution)); }
        private async Task<HostToolResult> StartAsync(string root, string command, int timeoutSeconds, CancellationToken token, Action? prelaunchCheck = null, JObject? origin = null)
        {
            token.ThrowIfCancellationRequested(); lifetime.Token.ThrowIfCancellationRequested();
            if (!string.Equals(root, await environment.GetWorkspaceRootAsync(token).ConfigureAwait(false), StringComparison.OrdinalIgnoreCase))
                return HostToolResult.Error("Workspace changed after terminal approval; request the command again.");
            ProjectExecutionPolicy.ThrowIfIsolationRequired(root); ProjectExecutionPolicy.RequireTool(root, "run_terminal", new JObject { ["command"] = command });
            var job = new Job { Root = root, Command = command, TimeoutSeconds = timeoutSeconds, PrelaunchCheck = prelaunchCheck, Origin = origin == null ? new JObject { ["sessionId"] = "" } : (JObject)origin.DeepClone() };
            lock (gate)
            {
                if (jobs.Any(j => j.State == "terminationUnconfirmed")) return HostToolResult.Error("A previous process has not confirmed exit. Check the terminal result and stop the process before restarting POMP terminal work.");
                if (jobs.Count(j => j.State == "queued" || j.State == "running") >= 20) return HostToolResult.Error("Twenty terminal jobs are already active or queued.");
                while (jobs.Count >= 25)
                {
                    var old = jobs.FirstOrDefault(j => j.State != "queued" && j.State != "running");
                    if (old == null) break; jobs.Remove(old); old.Cancellation.Dispose();
                }
                jobs.Add(job); selectedJob = job.Id;
            }
            job.Execution = Task.Run(() => RunAsync(job)); job.Execution.Forget(NullPompLogger.Instance, "terminal job"); Publish();
            return HostToolResult.Text("Terminal job " + job.Id + " was queued after approval. It is not complete. Use terminal_status or the POMP terminal panel to read its result.", new JObject { ["jobId"] = job.Id, ["state"] = "queued" });
        }
        private async Task RunAsync(Job job)
        {
            var acquired = false;
            using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(job.Cancellation.Token, lifetime.Token);
            try
            {
                while (true)
                {
                    Task wait; lock (gate) wait = resume.Task;
                    await wait.WithCancellation(cancellation.Token).ConfigureAwait(false);
                    await slots.WaitAsync(cancellation.Token).ConfigureAwait(false); acquired = true;
                    lock (gate) { if (!paused) break; }
                    slots.Release(); acquired = false;
                }
                cancellation.Token.ThrowIfCancellationRequested();
                if (!string.Equals(job.Root, await environment.GetWorkspaceRootAsync(cancellation.Token).ConfigureAwait(false), StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException("Workspace changed while this command was queued.");
                ProjectExecutionPolicy.ThrowIfIsolationRequired(job.Root); ProjectExecutionPolicy.RequireTool(job.Root, "run_terminal", new JObject { ["command"] = job.Command });
                job.PrelaunchCheck?.Invoke();
                var options = buildLaunch(job.Root, job.Command);
                cancellation.CancelAfter(TimeSpan.FromSeconds(job.TimeoutSeconds));
                var process = launcher.Start(options);
                lock (gate) { job.Process = process; job.State = "running"; job.Started = DateTimeOffset.UtcNow; job.TraceClock.Restart(); }
                using var kill = cancellation.Token.Register(() => { try { process.Kill(); } catch { } });
                process.StandardInput.Close();
                var exited = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
                void OnExit(object? sender, EventArgs args) => exited.TrySetResult(true);
                process.Exited += OnExit;
                var stdout = ReadAsync(job, process.StandardOutput, false, cancellation.Token);
                var stderr = ReadAsync(job, process.StandardError, true, cancellation.Token);
                stdout.Observe(); stderr.Observe();
                try
                {
                    if (process.HasExited) exited.TrySetResult(true);
                    Publish();
                    await exited.Task.WithCancellation(cancellation.Token).ConfigureAwait(false);
                    // Child processes can inherit pipes; do not hang forever after the shell exits.
                    var drains = Task.WhenAll(stdout, stderr); drains.Observe();
                    await Task.WhenAny(drains, Task.Delay(2000, cancellation.Token)).ConfigureAwait(false);
                    cancellation.Token.ThrowIfCancellationRequested();
                    lock (gate) { job.ExitCode = process.ExitCode; job.State = process.ExitCode == 0 ? "exited" : "failed"; }
                }
                finally { process.Exited -= OnExit; process.Dispose(); }
            }
            catch (OperationCanceledException)
            {
                lock (gate) { job.State = HasExited(job.Process) ? (job.Cancellation.IsCancellationRequested || lifetime.IsCancellationRequested ? "cancelled" : "timedOut") : "terminationUnconfirmed"; job.Error = PompLanguage.Pick("프로세스 종료 요청됨", "Process termination requested"); }
            }
            catch (Exception ex) { lock (gate) { job.State = "failed"; job.Error = ex.Message; } }
            finally
            {
                IOmpProcess? owned;
                lock (gate) { owned = job.Process; job.Process = null; job.Finished = DateTimeOffset.UtcNow; job.TraceClock.Stop(); }
                try { owned?.Dispose(); } catch { }
                if (acquired) slots.Release();
                Publish(); PersistMetadata(job.Root);
            }
        }
        private async Task ReadAsync(Job job, Stream stream, bool error, CancellationToken token)
        {
            try
            {
                using var reader = new StreamReader(stream, new UTF8Encoding(false), true, 4096, true);
                var buffer = new char[2048];
                var xml = new StringBuilder(); var suppressXml = false; var firstError = true;
                while (true)
                {
                    var count = await reader.ReadAsync(buffer, 0, buffer.Length).ConfigureAwait(false);
                    if (token.IsCancellationRequested) break;
                    var atEnd = count == 0;
                    if (atEnd && (!error || !firstError || xml.Length == 0 || xml.ToString().StartsWith("#< CLIXML", StringComparison.Ordinal))) break;
                    var chunk = atEnd ? xml.ToString() : new string(buffer, 0, count);
                    if (atEnd) { xml.Clear(); firstError = false; }
                    if (error)
                    {
                        if (firstError) { xml.Append(chunk); if (xml.Length < 9 && "#< CLIXML".StartsWith(xml.ToString(), StringComparison.Ordinal)) continue; firstError = false; suppressXml = xml.ToString().StartsWith("#< CLIXML", StringComparison.Ordinal); chunk = xml.ToString(); xml.Clear(); }
                        if (suppressXml)
                        {
                            xml.Append(chunk);
                            var content = xml.ToString(); var end = content.IndexOf("</Objs>", StringComparison.Ordinal);
                            if (end < 0) { if (xml.Length > 262144) { xml.Clear(); xml.Append("#< CLIXML"); } continue; }
                            chunk = DecodeCliXml(content.Substring(0, end + 7)) + content.Substring(end + 7);
                            xml.Clear(); suppressXml = false;
                        }
                    }
                    var text = (error ? "[stderr] " : "") + chunk;
                    if (string.IsNullOrWhiteSpace(chunk)) continue;
                    lock (gate)
                    {
                        job.Output.Enqueue(text); job.Trace.Enqueue(new TraceFrame(job.TraceClock.ElapsedMilliseconds, text)); job.Characters += text.Length; job.Lines += text.Count(c => c == '\n');
                        while (job.Characters > 262144 || job.Lines > 10000 || job.Output.Count > 4096)
                        { var removed = job.Output.Dequeue(); job.Trace.Dequeue(); job.Characters -= removed.Length; job.Lines -= removed.Count(c => c == '\n'); job.Truncated = true; }
                    }
                    if (atEnd) break;
                }
            }
            catch (Exception ex) when (ex is IOException || ex is ObjectDisposedException) { }
        }
        internal static string DecodeCliXml(string text)
        {
            try
            {
                var offset = text.StartsWith("#< CLIXML", StringComparison.Ordinal) ? 9 : 0;
                var start = text.IndexOf('<', offset); if (start < 0) return "";
                var settings = new System.Xml.XmlReaderSettings { DtdProcessing = System.Xml.DtdProcessing.Prohibit, XmlResolver = null, MaxCharactersInDocument = 262144 };
                using var input = new StringReader(text.Substring(start));
                using var reader = System.Xml.XmlReader.Create(input, settings);
                var document = System.Xml.Linq.XDocument.Load(reader);
                return string.Join(Environment.NewLine, document.Descendants().Where(e => e.Name.LocalName == "S" && ((string?)e.Attribute("S") is "Error" or "Warning")).Select(e => System.Text.RegularExpressions.Regex.Replace(e.Value, @"_x([0-9a-fA-F]{4})_", m => ((char)int.Parse(m.Groups[1].Value, System.Globalization.NumberStyles.HexNumber)).ToString())));
            }
            catch (Exception ex) when (ex is System.Xml.XmlException || ex is FormatException) { return ""; }
        }
        private static OmpLaunchOptions BuildLaunch(string root, string command)
        {
            if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows)) throw new PlatformNotSupportedException("Native POMP terminal execution requires Windows.");
            var exe = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "WindowsPowerShell", "v1.0", "powershell.exe");
            if (!File.Exists(exe)) throw new FileNotFoundException("Windows PowerShell is unavailable.");
            var script = "[Console]::OutputEncoding = New-Object System.Text.UTF8Encoding($false); $OutputEncoding = [Console]::OutputEncoding; $ErrorActionPreference = 'Stop'; try { & {\n" + command + "\n}; if ($LASTEXITCODE -is [int]) { exit $LASTEXITCODE }; if (!$?) { exit 1 } } catch { [Console]::Error.WriteLine($_.ToString()); exit 1 }";
            var options = new OmpLaunchOptions { ExecutablePath = exe, WorkingDirectory = root };
            foreach (var arg in new[] { "-NoLogo", "-NoProfile", "-NonInteractive", "-OutputFormat", "Text", "-EncodedCommand", Convert.ToBase64String(Encoding.Unicode.GetBytes(script)) }) options.Arguments.Add(arg);
            return options;
        }
        /// <summary>
        /// Saves the metadata (never the output) of one workspace's jobs to its <c>.pomp/local/terminal-jobs.json</c>. The single file an earlier
        /// POMP kept for all workspaces is moved in by the first save and replaced by it.
        /// </summary>
        private void PersistMetadata(string root)
        {
            lock (persistenceGate)
            try
            {
                if (!Directory.Exists(root)) return; // A workspace that is gone is not brought back by a late job.
                JObject data;
                lock (gate) data = new JObject { ["jobs"] = new JArray(jobs.Where(j => string.Equals(j.Root, root, StringComparison.OrdinalIgnoreCase)).Select(j => new JObject { ["id"] = j.Id, ["root"] = j.Root, ["state"] = j.State, ["origin"] = j.Origin.DeepClone(), ["createdUtc"] = j.Created.ToString("O"), ["exitCode"] = j.ExitCode })) };
                var file = ProjectData.File(root, "terminal-jobs.json", Path.Combine(environment.DataDirectory, "terminal-jobs.json")); CheckpointStore.CheckDirectPath(file); AtomicFile.WriteAllText(file, data.ToString());
            }
            catch { lock (gate) persistenceWarning = "Terminal metadata could not be saved; keep this session open to inspect its results."; }
        }
        private static bool HasExited(IOmpProcess? process) { try { return process == null || process.HasExited; } catch { return false; } }
        public void Dispose() { lifetime.Cancel(); timer.Dispose(); CancelAll(); }
        private static string Tail(string value, int count) => value.Length <= count ? value : "[earlier output omitted]\n" + value.Substring(value.Length - count);
        private static TaskCompletionSource<bool> CompletedSignal() { var tcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously); tcs.SetResult(true); return tcs; }

        private sealed class RunJobTool : IPreparedHostTool
        {
            private readonly TerminalJobs owner;
            public RunJobTool(TerminalJobs owner) { this.owner = owner; }
            public HostToolDefinition Definition { get; } = new HostToolDefinition { Name = "run_terminal", Label = "Run supervised terminal command",
                Description = "Run a noninteractive Windows PowerShell command in the workspace after explicit approval. Up to five run concurrently; twenty may be active/queued. Returns a job ID, not a completed result. Use terminal_status for output/exit status. Not an OS security sandbox; commands may modify files, use credentials or access the network. Prefer dedicated IDE tools where available.",
                Parameters = JObject.Parse("{\"type\":\"object\",\"properties\":{\"command\":{\"type\":\"string\",\"minLength\":1,\"maxLength\":8000},\"timeoutSeconds\":{\"type\":\"integer\",\"minimum\":1,\"maximum\":3600}},\"required\":[\"command\"],\"additionalProperties\":false}") };
            public PermissionKind ResolvePermission(HostToolInvocation invocation) => PermissionKind.ProcessExec;
            public Task<PermissionRequest?> DescribePermissionAsync(HostToolInvocation invocation, CancellationToken token) => Task.FromResult<PermissionRequest?>(null);
            public Task<HostToolResult> ExecuteAsync(HostToolInvocation invocation, CancellationToken token) => Task.FromResult(HostToolResult.Error("Terminal commands require a frozen, approved prepared call."));
            public async Task<IPreparedHostToolCall> PrepareAsync(HostToolInvocation invocation, CancellationToken token)
            {
                var command = (string?)invocation.Arguments["command"] ?? ""; var timeout = (int?)invocation.Arguments["timeoutSeconds"] ?? 120;
                if (string.IsNullOrWhiteSpace(command) || command.Length > 8000 || command.IndexOf('\0') >= 0 || timeout < 1 || timeout > 3600) throw new ArgumentException("Invalid terminal command or timeout.");
                var root = await owner.environment.GetWorkspaceRootAsync(token).ConfigureAwait(false); ProjectExecutionPolicy.ThrowIfIsolationRequired(root); ProjectExecutionPolicy.RequireTool(root, "run_terminal", new JObject { ["command"] = command });
                var origin = new JObject { ["sessionId"] = owner.ResolveSessionId?.Invoke(invocation) ?? "" };
                if (!string.IsNullOrEmpty(invocation.ToolCallId) && invocation.ToolCallId.Length <= 256 && !invocation.ToolCallId.Any(char.IsControl)) origin["toolCallId"] = invocation.ToolCallId;
                return new Prepared(owner, root, command, timeout, origin);
            }
        }
        private sealed class Prepared : IPreparedHostToolCall, IDeferredLaunchCheck
        {
            private readonly TerminalJobs owner; private readonly string root, command; private readonly int timeout; private readonly JObject origin; private int started;
            public Prepared(TerminalJobs owner, string root, string command, int timeout, JObject origin)
            {
                this.owner = owner; this.root = root; this.command = command; this.timeout = timeout; this.origin = (JObject)origin.DeepClone();
                Permission = new PermissionRequest { Kind = PermissionKind.ProcessExec, Source = PermissionSource.HostTool, ToolName = "run_terminal", CanRemember = false,
                    Title = PompLanguage.Pick("이 PowerShell 명령을 실행할까요?", "Run this PowerShell command?"), Target = root, Message = PompLanguage.Pick("샌드박스 없이 실행돼 파일과 네트워크에 접근할 수 있어요.\n제한 시간: " + timeout + "초\n\n" + command, "Runs without a sandbox and can access files and the network.\nTimeout: " + timeout + " s\n\n" + command) };
            }
            public Action? PrelaunchCheck { get; set; }
            public PermissionRequest Permission { get; }
            public Task<HostToolResult> ExecuteAsync(CancellationToken token) => Interlocked.Exchange(ref started, 1) == 0 ? owner.StartAsync(root, command, timeout, token, PrelaunchCheck, origin) : Task.FromResult(HostToolResult.Error("This terminal approval was already consumed."));
            public void Dispose() { }
        }
        private sealed class StatusJobTool : IHostTool
        {
            private readonly TerminalJobs owner; public StatusJobTool(TerminalJobs owner) { this.owner = owner; }
            public HostToolDefinition Definition { get; } = new HostToolDefinition { Name = "terminal_status", Label = "Read terminal jobs", Description = "Read bounded output, exit status and queue state for the current workspace's terminal jobs. Pass jobId for a longer output tail of one job. Running/queued is not completion; output can be truncated. Does not start or cancel processes.", Parameters = JObject.Parse("{\"type\":\"object\",\"properties\":{\"jobId\":{\"type\":\"string\"}},\"additionalProperties\":false}") };
            public PermissionKind ResolvePermission(HostToolInvocation invocation) => PermissionKind.None;
            public Task<PermissionRequest?> DescribePermissionAsync(HostToolInvocation invocation, CancellationToken token) => Task.FromResult<PermissionRequest?>(null);
            public async Task<HostToolResult> ExecuteAsync(HostToolInvocation invocation, CancellationToken token)
            {
                var root = await owner.environment.GetWorkspaceRootAsync(token).ConfigureAwait(false);
                var id = (string?)invocation.Arguments["jobId"]; var state = owner.View(id ?? "");
                var visible = ((JArray)state["jobs"]!).Where(job => string.Equals((string?)job["root"], root, StringComparison.OrdinalIgnoreCase) && (id == null || (string?)job["id"] == id)).ToArray();
                if (id != null && visible.Length == 0) return HostToolResult.Error("No retained terminal job with that ID belongs to the current workspace.");
                state["jobs"] = new JArray(visible);
                state["selectedId"] = visible.Any(job => (string?)job["id"] == (string?)state["selectedId"]) ? state["selectedId"] : null;
                return HostToolResult.Text(state.ToString(), state);
            }
        }
    }
}
