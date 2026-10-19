using System;
using System.Diagnostics;
using System.IO;
using System.Threading;
using Newtonsoft.Json.Linq;
using Pomp.Diagnostics;
using Pomp.Internal;

namespace Pomp.Features
{
    /// <summary>The conversation that holds computer use on this PC: one conversation in one POMP (docs/architecture.md §8.5).</summary>
    public sealed class ComputerOwner
    {
        public ComputerOwner(string sessionId, string label, string workspace, bool mine)
        {
            SessionId = sessionId;
            Label = label;
            Workspace = workspace;
            Mine = mine;
        }

        public string SessionId { get; }

        /// <summary>The conversation's name as its chat shows it (its title, or its agent or team).</summary>
        public string Label { get; }

        /// <summary>The workspace folder name of the POMP that holds it.</summary>
        public string Workspace { get; }

        /// <summary>Held by this POMP (another conversation of the same chat, or the current one).</summary>
        public bool Mine { get; }
    }

    /// <summary>
    /// The PC-wide owner record <c>&lt;data&gt;\state\computer-owner.json</c>:
    /// <c>{ instance, pid, processStart, sessionId, label, workspace, updatedAt }</c>. Taking and releasing run under one named
    /// mutex, so two chats (in this POMP or in another Visual Studio) can never both take it. A record whose process has ended
    /// (or whose process id now names another process) is stale and counts as free. <see cref="Changed"/> fires when the record
    /// may have changed: a file event, or a holder that has gone away (checked every few seconds).
    /// </summary>
    public sealed class ComputerOwnerLock : IDisposable
    {
        public const string FileName = "computer-owner.json";
        private const string MutexName = @"Local\Pomp.ComputerOwner";
        private static readonly TimeSpan MutexWait = TimeSpan.FromSeconds(2);
        private const long MaxRecordBytes = 4096;
        // The instances of this process that are not disposed: a record of another instance of this process is live only while it is.
        private static readonly System.Collections.Generic.HashSet<string> Instances = new System.Collections.Generic.HashSet<string>(StringComparer.Ordinal);
        private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(3);

        private readonly string _file;
        private readonly string _instance = Guid.NewGuid().ToString("N");
        private readonly int _pid;
        private readonly long _processStart;
        private readonly IPompLogger _logger;
        private readonly Mutex _mutex;
        private readonly FileSystemWatcher? _watcher;
        private readonly Timer _timer;
        private readonly object _viewLock = new object();
        private string _lastView = "";
        private int _disposed;

        /// <param name="stateRoot">The folder that holds the record (<c>%LOCALAPPDATA%\Pomp\state</c>).</param>
        public ComputerOwnerLock(string stateRoot, int processId, IPompLogger? logger = null)
        {
            _file = Path.Combine(stateRoot, FileName);
            _pid = processId;
            _processStart = StartTicks(processId) ?? 0;
            _logger = logger ?? NullPompLogger.Instance;
            _mutex = new Mutex(false, MutexName);
            lock (Instances) Instances.Add(_instance);
            try
            {
                Directory.CreateDirectory(stateRoot);
                var watcher = new FileSystemWatcher(stateRoot, FileName + "*") { NotifyFilter = NotifyFilters.FileName | NotifyFilters.LastWrite };
                watcher.Changed += (_, __) => Check();
                watcher.Created += (_, __) => Check();
                watcher.Deleted += (_, __) => Check();
                watcher.Renamed += (_, __) => Check();
                watcher.EnableRaisingEvents = true;
                _watcher = watcher;
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException || ex is ArgumentException || ex is PlatformNotSupportedException)
            {
                // The poll below still notices every change, only later.
                _logger.Warn("Could not watch " + FileName + ": " + ex.Message);
            }

            _lastView = View(Current());
            _timer = new Timer(_ => Check(), null, PollInterval, PollInterval);
        }

        /// <summary>The record may have changed (another chat took or released it, or its holder went away).</summary>
        public event EventHandler? Changed;

        /// <summary>The live holder; null when nobody holds it (no record, an unreadable one, or a stale one).</summary>
        public ComputerOwner? Current()
        {
            var record = Read();
            return record == null || !Live(record) ? null : Owner(record);
        }

        /// <summary>
        /// Takes computer use for <paramref name="sessionId"/> of this POMP. Null when it is now this conversation's (also when it
        /// already was); otherwise the conversation that holds it. Taking from this POMP's other conversation is refused too.
        /// </summary>
        public ComputerOwner? TryTake(string sessionId, string label, string workspace)
        {
            ComputerOwner? holder = null;
            var taken = Locked(() =>
            {
                var record = Read();
                if (record != null && Live(record) && !(Mine(record) && (string?)record["sessionId"] == sessionId))
                {
                    holder = Owner(record);
                    return;
                }

                AtomicFile.WriteAllText(_file, new JObject
                {
                    ["instance"] = _instance,
                    ["pid"] = _pid,
                    ["processStart"] = _processStart,
                    ["sessionId"] = sessionId,
                    ["label"] = label,
                    ["workspace"] = workspace,
                    ["updatedAt"] = DateTime.UtcNow.ToString("o"),
                }.ToString());
            });
            if (!taken) return holder ?? new ComputerOwner("", "", "", mine: false);
            Check();
            return holder;
        }

        /// <summary>Releases this POMP's record (only for <paramref name="sessionId"/> when given); a record of another POMP stays.</summary>
        public void Release(string? sessionId = null)
        {
            Locked(() =>
            {
                var record = Read();
                if (record == null || !Mine(record) || (sessionId != null && (string?)record["sessionId"] != sessionId)) return;
                File.Delete(_file);
            });
            Check();
        }

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 1) return;
            _timer.Dispose();
            _watcher?.Dispose();
            Release();
            lock (Instances) Instances.Remove(_instance);
            _mutex.Dispose();
        }

        private bool Locked(Action action)
        {
            bool owned;
            try { owned = _mutex.WaitOne(MutexWait); }
            catch (AbandonedMutexException) { owned = true; }
            if (!owned)
            {
                _logger.Warn("Timed out waiting for the computer use owner lock.");
                return false;
            }

            try
            {
                action();
                return true;
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
            {
                _logger.Warn("Could not update " + FileName + ": " + ex.Message);
                return false;
            }
            finally
            {
                _mutex.ReleaseMutex();
            }
        }

        private void Check()
        {
            if (Volatile.Read(ref _disposed) == 1) return;
            string view;
            try { view = View(Current()); }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException) { return; }
            lock (_viewLock)
            {
                if (view == _lastView) return;
                _lastView = view;
            }

            try { Changed?.Invoke(this, EventArgs.Empty); }
            catch (Exception ex) { _logger.Warn("Computer use owner change handler failed: " + ex.Message); }
        }

        private static string View(ComputerOwner? owner) => owner == null ? "" : (owner.Mine ? "1" : "0") + "\n" + owner.SessionId + "\n" + owner.Label + "\n" + owner.Workspace;

        private JObject? Read()
        {
            try
            {
                var info = new FileInfo(_file);
                if (!info.Exists || info.Length > MaxRecordBytes) return null;
                return JObject.Parse(File.ReadAllText(_file));
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException || ex is Newtonsoft.Json.JsonException)
            {
                return null;
            }
        }

        private bool Mine(JObject record) => (string?)record["instance"] == _instance;

        private ComputerOwner Owner(JObject record) =>
            new ComputerOwner((string?)record["sessionId"] ?? "", (string?)record["label"] ?? "", (string?)record["workspace"] ?? "", Mine(record));

        private bool Live(JObject record)
        {
            if (Mine(record)) return true;
            var pid = record["pid"]?.Type == JTokenType.Integer ? (int)record["pid"]! : 0;
            if (pid <= 0) return false;
            var start = record["processStart"]?.Type == JTokenType.Integer ? (long)record["processStart"]! : 0;
            // This process: live only while that instance is (a POMP session of this process that was disposed holds nothing).
            if (pid == _pid)
            {
                var instance = (string?)record["instance"];
                lock (Instances) return instance != null && Instances.Contains(instance);
            }

            try
            {
                using (var process = Process.GetProcessById(pid))
                {
                    var ticks = StartTicks(process);
                    return ticks == null || start == 0 || ticks == start;
                }
            }
            catch (ArgumentException) { return false; }
            catch (InvalidOperationException) { return false; }
        }

        private static long? StartTicks(int pid)
        {
            try { using (var process = Process.GetProcessById(pid)) return StartTicks(process); }
            catch (Exception ex) when (ex is ArgumentException || ex is InvalidOperationException) { return null; }
        }

        private static long? StartTicks(Process process)
        {
            try { return process.StartTime.ToUniversalTime().Ticks; }
            catch (Exception ex) when (ex is System.ComponentModel.Win32Exception || ex is InvalidOperationException || ex is NotSupportedException) { return null; }
        }
    }
}
