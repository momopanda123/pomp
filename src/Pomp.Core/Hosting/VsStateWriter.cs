using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Pomp.Adapters;
using Pomp.Diagnostics;
using Pomp.Internal;

namespace Pomp.Hosting
{
    /// <summary>
    /// Maintains <c>&lt;POMP_STATE_DIR&gt;\vs-state.json</c> (version 1): solution, active document and the open documents with their
    /// dirty flag. omp-side hooks read it to protect unsaved editor buffers. The file is rewritten (atomically, through a temp file)
    /// only when the content changed; a failed adapter call keeps the previously known value instead of overwriting good data.
    /// </summary>
    internal sealed class VsStateWriter : IDisposable
    {
        public const string FileName = "vs-state.json";
        internal Action? StateObserved { get; set; }

        private readonly IVsAdapterFactory _adapters;
        private readonly IPompLogger _logger;
        private readonly TimeSpan _adapterTimeout;
        private readonly object _lock = new object();
        private readonly SemaphoreSlim _writeGate = new SemaphoreSlim(1, 1);
        private string? _directory;
        private string? _lastContent;
        private string? _activeDocumentPath;
        internal string? ActiveDocumentPath { get { lock (_lock) return _activeDocumentPath; } }
        private SolutionInfo? _solution;
        private ActiveDocumentInfo? _active;
        private IReadOnlyList<OpenDocumentInfo> _open = Array.Empty<OpenDocumentInfo>();
        private Timer? _timer;
        private TimeSpan _interval;
        private bool _running;
        private bool _disposed;

        public VsStateWriter(IVsAdapterFactory adapters, IPompLogger logger, TimeSpan? adapterTimeout = null)
        {
            _adapters = adapters;
            _logger = logger;
            _adapterTimeout = adapterTimeout ?? TimeSpan.FromSeconds(2);
        }

        /// <summary>The directory that holds the state file, or null before <see cref="SetDirectory"/>.</summary>
        public string? Directory
        {
            get
            {
                lock (_lock)
                {
                    return _directory;
                }
            }
        }

        public string? FilePath
        {
            get
            {
                var directory = Directory;
                return directory == null ? null : Path.Combine(directory, FileName);
            }
        }

        /// <summary>Sets (and creates) the state directory. The next write is forced.</summary>
        public void SetDirectory(string directory)
        {
            System.IO.Directory.CreateDirectory(directory);
            lock (_lock)
            {
                _directory = directory;
                _lastContent = null;
            }
        }

        /// <summary>Gathers the state from the adapters and writes the file when it changed (or when <paramref name="force"/> is set).</summary>
        public async Task WriteAsync(bool force, CancellationToken cancellationToken)
        {
            string? directory;
            lock (_lock)
            {
                directory = _directory;
            }

            if (directory == null)
            {
                return;
            }

            await _writeGate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                // Polled every couple of seconds: only the cheap location query (no project enumeration on the UI thread).
                var solution = AdapterGuard.TryRunAsync(_adapters.Solution.GetSolutionLocationAsync, _adapterTimeout, _logger, "vs-state: solution", cancellationToken);
                var active = AdapterGuard.TryRunAsync(_adapters.Editor.GetActiveDocumentAsync, _adapterTimeout, _logger, "vs-state: active document", cancellationToken);
                var open = AdapterGuard.TryRunAsync(_adapters.Editor.GetOpenDocumentsAsync, _adapterTimeout, _logger, "vs-state: open documents", cancellationToken);
                var solutionResult = await solution.ConfigureAwait(false);
                var activeResult = await active.ConfigureAwait(false);
                var openResult = await open.ConfigureAwait(false);

                if (solutionResult.Succeeded)
                {
                    _solution = solutionResult.Value;
                }

                if (activeResult.Succeeded)
                {
                    _active = activeResult.Value;
                }

                if (openResult.Succeeded && openResult.Value != null)
                {
                    _open = openResult.Value;
                }

                var state = BuildState(_solution, _active, _open);
                var content = state.ToString(Formatting.None);
                lock (_lock)
                {
                    if (!force && string.Equals(content, _lastContent, StringComparison.Ordinal))
                    {
                        return;
                    }
                }

                state["updatedAt"] = DateTime.UtcNow.ToString("o");
                var text = state.ToString(Formatting.Indented);
                lock (_lock)
                {
                    // Deleted or replaced while the adapters were queried: this state belongs to no directory now. Written under
                    // the lock, so DeleteDirectory cannot remove the directory between this check and a write that would recreate it.
                    if (!string.Equals(_directory, directory, StringComparison.Ordinal))
                    {
                        return;
                    }

                    AtomicFile.WriteAllText(Path.Combine(directory, FileName), text);
                    _lastContent = content;
                    _activeDocumentPath = string.IsNullOrEmpty(_active?.Path) ? null : _active!.Path;
                }
            }
            finally
            {
                _writeGate.Release();
                StateObserved?.Invoke();
            }
        }

        /// <summary>Builds the JSON document (without <c>updatedAt</c>).</summary>
        internal static JObject BuildState(SolutionInfo? solution, ActiveDocumentInfo? active, IEnumerable<OpenDocumentInfo> open)
        {
            var documents = new JArray();
            foreach (var document in open)
            {
                if (string.IsNullOrEmpty(document.Path))
                {
                    continue;
                }

                documents.Add(new JObject { ["path"] = document.Path, ["isDirty"] = document.IsDirty });
            }

            return new JObject
            {
                ["version"] = 1,
                ["solutionPath"] = NullIfEmpty(solution?.SolutionPath),
                ["solutionRoot"] = NullIfEmpty(solution?.RootDirectory),
                ["activeDocument"] = NullIfEmpty(active?.Path),
                ["openDocuments"] = documents,
            };
        }

        /// <summary>Starts refreshing the file in the background (first refresh immediately, then every <paramref name="interval"/>).</summary>
        public void Start(TimeSpan interval)
        {
            lock (_lock)
            {
                if (_disposed)
                {
                    return;
                }

                _interval = interval;
                _running = true;
                if (_timer == null)
                {
                    _timer = new Timer(OnTimer, null, TimeSpan.Zero, Timeout.InfiniteTimeSpan);
                }
                else
                {
                    _timer.Change(TimeSpan.Zero, Timeout.InfiniteTimeSpan);
                }
            }
        }

        public void Stop()
        {
            lock (_lock)
            {
                _running = false;
                _timer?.Change(Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
            }
        }

        /// <summary>Stops refreshing and deletes the state directory.</summary>
        public void DeleteDirectory()
        {
            Stop();
            string? directory;
            lock (_lock)
            {
                directory = _directory;
                _directory = null;
                _lastContent = null;
            }

            if (directory == null)
            {
                return;
            }

            try
            {
                if (System.IO.Directory.Exists(directory))
                {
                    System.IO.Directory.Delete(directory, true);
                }
            }
            catch (Exception ex)
            {
                _logger.Debug("Could not delete the state directory '" + directory + "': " + ex.Message);
            }
        }

        public void Dispose()
        {
            Timer? timer;
            lock (_lock)
            {
                _disposed = true;
                _running = false;
                timer = _timer;
                _timer = null;
            }

            timer?.Dispose();
        }

        private void OnTimer(object? state)
        {
            TickAsync().Forget(_logger, "vs-state refresh");
        }

        private async Task TickAsync()
        {
            try
            {
                await WriteAsync(false, CancellationToken.None).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger.Debug("vs-state refresh failed: " + ex.Message);
            }
            finally
            {
                lock (_lock)
                {
                    if (_running && !_disposed)
                    {
                        _timer?.Change(_interval, Timeout.InfiniteTimeSpan);
                    }
                }
            }
        }

        private static JToken NullIfEmpty(string? text) => string.IsNullOrEmpty(text) ? JValue.CreateNull() : new JValue(text);
    }
}
