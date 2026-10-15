using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Pomp.Diagnostics;
using Pomp.Internal;

namespace Pomp.Hosting
{
    /// <summary>
    /// Local usage metrics (the design's success metrics: approvals per tool, edit accuracy, UI responsiveness, omp stability).
    /// Counters and timings are kept in <c>%LOCALAPPDATA%\Pomp\usage-metrics.json</c> and never sent anywhere. Changes are held
    /// as deltas and merged into the file (several Visual Studio instances share it) at most every few minutes and on dispose.
    /// Keys are bounded: at most <see cref="MaxKeys"/> distinct keys of <see cref="MaxKeyLength"/> characters. Thread-safe.
    /// </summary>
    public sealed class UsageMetrics : IDisposable
    {
        public const string FileName = "usage-metrics.json";

        internal const int MaxKeys = 1000;
        internal const int MaxKeyLength = 120;
        private const string OverflowKey = "other";
        private const string MutexName = "Local\\Pomp.UsageMetrics";

        private readonly object _lock = new object();
        private readonly Dictionary<string, long> _counters = new Dictionary<string, long>(StringComparer.Ordinal);
        private readonly Dictionary<string, Timing> _timings = new Dictionary<string, Timing>(StringComparer.Ordinal);
        private readonly string? _path;
        private readonly IPompLogger _logger;
        private readonly Timer? _timer;
        private bool _warned;
        private int _disposed;

        /// <param name="path">The metrics file; null keeps everything in memory (tests, no data folder).</param>
        /// <param name="logger">Log sink.</param>
        /// <param name="saveInterval">How often pending changes are merged into the file (default 5 minutes).</param>
        public UsageMetrics(string? path, IPompLogger? logger = null, TimeSpan? saveInterval = null)
        {
            _path = string.IsNullOrWhiteSpace(path) ? null : path;
            _logger = logger ?? NullPompLogger.Instance;
            var interval = saveInterval ?? TimeSpan.FromMinutes(5);
            if (_path != null && interval > TimeSpan.Zero)
            {
                _timer = new Timer(_ => Save(), null, interval, interval);
            }
        }

        /// <summary>A sink that records nothing persistently (still counts in memory).</summary>
        public static UsageMetrics InMemory() => new UsageMetrics(null);

        /// <summary>Adds <paramref name="amount"/> to a counter.</summary>
        public void Count(string key, long amount = 1)
        {
            if (Volatile.Read(ref _disposed) == 1)
            {
                return;
            }

            lock (_lock)
            {
                var name = BoundedKey(key);
                _counters.TryGetValue(name, out var value);
                _counters[name] = value + amount;
            }
        }

        /// <summary>Records one duration (count, total and maximum are kept).</summary>
        public void Time(string key, TimeSpan duration)
        {
            if (Volatile.Read(ref _disposed) == 1 || duration < TimeSpan.Zero)
            {
                return;
            }

            lock (_lock)
            {
                var name = BoundedKey(key);
                if (!_timings.TryGetValue(name, out var timing))
                {
                    timing = new Timing();
                    _timings[name] = timing;
                }

                timing.Add(1, duration.TotalMilliseconds, duration.TotalMilliseconds);
            }
        }

        /// <summary>The file's totals plus the changes not saved yet.</summary>
        public JObject Snapshot()
        {
            var stored = _path == null ? null : TryRead(_path);
            lock (_lock)
            {
                return Merge(stored, _counters, _timings, DateTime.UtcNow);
            }
        }

        /// <summary>Merges the pending changes into the file. Failures keep them for the next attempt (logged once).</summary>
        public void Save()
        {
            if (_path == null)
            {
                return;
            }

            Dictionary<string, long> counters;
            Dictionary<string, Timing> timings;
            lock (_lock)
            {
                if (_counters.Count == 0 && _timings.Count == 0)
                {
                    return;
                }

                counters = new Dictionary<string, long>(_counters, StringComparer.Ordinal);
                timings = _timings.ToDictionary(pair => pair.Key, pair => pair.Value.Copy(), StringComparer.Ordinal);
                _counters.Clear();
                _timings.Clear();
            }

            try
            {
                using (var mutex = new Mutex(false, MutexName))
                {
                    var owned = false;
                    try
                    {
                        try
                        {
                            owned = mutex.WaitOne(TimeSpan.FromSeconds(2));
                        }
                        catch (AbandonedMutexException)
                        {
                            owned = true; // Another process died while saving; the file is replaced atomically, so it is intact.
                        }

                        if (!owned)
                        {
                            throw new IOException("another Visual Studio instance is saving the usage metrics");
                        }

                        var merged = Merge(TryRead(_path), counters, timings, DateTime.UtcNow);
                        AtomicFile.WriteAllText(_path, merged.ToString(Formatting.Indented));
                    }
                    finally
                    {
                        if (owned)
                        {
                            mutex.ReleaseMutex();
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                lock (_lock)
                {
                    // Keep them for the next save.
                    foreach (var pair in counters)
                    {
                        _counters.TryGetValue(pair.Key, out var value);
                        _counters[pair.Key] = value + pair.Value;
                    }

                    foreach (var pair in timings)
                    {
                        if (_timings.TryGetValue(pair.Key, out var timing))
                        {
                            timing.Add(pair.Value.Count, pair.Value.TotalMs, pair.Value.MaxMs);
                        }
                        else
                        {
                            _timings[pair.Key] = pair.Value;
                        }
                    }

                    if (_warned)
                    {
                        return;
                    }

                    _warned = true;
                }

                _logger.Warn("Could not save the usage metrics to '" + _path + "': " + ex.Message);
            }
        }

        /// <summary>A few lines for the log: approval rates, edit accuracy, page load and omp stability.</summary>
        public string Describe()
        {
            var snapshot = Snapshot();
            var counters = snapshot["counters"] as JObject ?? new JObject();
            long Get(string key) => (long?)counters[key] ?? 0;
            var text = new StringBuilder("pomp usage metrics since ").Append((string?)snapshot["since"]).Append(':');

            var approvals = counters.Properties()
                .Select(p => p.Name)
                .Where(name => name.StartsWith("permission.", StringComparison.Ordinal))
                .Select(name => name.Substring(0, name.LastIndexOf('.')))
                .Distinct(StringComparer.Ordinal)
                .OrderBy(name => name, StringComparer.Ordinal);
            foreach (var tool in approvals)
            {
                var allowed = Get(tool + ".allowed");
                var denied = Get(tool + ".denied");
                var asked = allowed + denied;
                text.Append("\n  ").Append(tool.Substring("permission.".Length)).Append(": ")
                    .Append(allowed).Append(" allowed, ").Append(denied).Append(" denied");
                if (asked > 0)
                {
                    text.Append(" (").Append((allowed * 100 / asked).ToString(CultureInfo.InvariantCulture)).Append("% allowed)");
                }

                text.Append(", ").Append(Get(tool + ".remembered")).Append(" by rule, ").Append(Get(tool + ".system")).Append(" denied by POMP");
            }

            var applied = Get("edit.applied");
            var stale = Get("edit.stale");
            text.Append("\n  edits: ").Append(applied).Append(" files applied, ").Append(stale).Append(" stale");
            if (applied + stale > 0)
            {
                text.Append(" (").Append((applied * 100 / (applied + stale)).ToString(CultureInfo.InvariantCulture)).Append("% applied)");
            }

            var ready = snapshot["timings"]?["ui.pageReady"];
            if (ready != null && (long?)ready["count"] > 0)
            {
                text.Append("\n  chat page ready: ").Append(((double)ready["totalMs"]! / (long)ready["count"]!).ToString("0", CultureInfo.InvariantCulture))
                    .Append(" ms average, ").Append(((double?)ready["maxMs"] ?? 0).ToString("0", CultureInfo.InvariantCulture)).Append(" ms max");
            }

            text.Append("\n  omp: ").Append(Get("omp.started")).Append(" starts, ").Append(Get("omp.lost")).Append(" unexpected ends; turns: ")
                .Append(Get("turn.completed")).Append(" completed, ").Append(Get("turn.error")).Append(" failed, ").Append(Get("turn.aborted")).Append(" stopped");
            return text.ToString();
        }

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 1)
            {
                return;
            }

            _timer?.Dispose();
            Save();
        }

        /// <summary>Metric keys use tool names chosen by omp and its plugins: keep them short and plain, and their number bounded.</summary>
        private string BoundedKey(string key)
        {
            var text = new StringBuilder(Math.Min(key?.Length ?? 0, MaxKeyLength));
            foreach (var c in key ?? string.Empty)
            {
                if (text.Length == MaxKeyLength)
                {
                    break;
                }

                text.Append((c >= 'a' && c <= 'z') || (c >= 'A' && c <= 'Z') || (c >= '0' && c <= '9') || c == '.' || c == '_' || c == '-' || c == ':' ? c : '_');
            }

            var name = text.Length == 0 ? OverflowKey : text.ToString();
            return _counters.ContainsKey(name) || _timings.ContainsKey(name) || _counters.Count + _timings.Count < MaxKeys ? name : OverflowKey;
        }

        private JObject? TryRead(string path)
        {
            try
            {
                if (!File.Exists(path))
                {
                    return null;
                }

                return JObject.Parse(File.ReadAllText(path, Encoding.UTF8));
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException || ex is JsonException)
            {
                // A damaged file starts over (it only holds statistics).
                _logger.Debug("Ignoring the usage metrics file '" + path + "': " + ex.Message);
                return null;
            }
        }

        private static JObject Merge(JObject? stored, IReadOnlyDictionary<string, long> counters, IReadOnlyDictionary<string, Timing> timings, DateTime now)
        {
            var mergedCounters = new SortedDictionary<string, long>(StringComparer.Ordinal);
            var mergedTimings = new SortedDictionary<string, Timing>(StringComparer.Ordinal);
            if (stored?["counters"] is JObject storedCounters)
            {
                foreach (var property in storedCounters.Properties())
                {
                    if (property.Value.Type == JTokenType.Integer && mergedCounters.Count < MaxKeys)
                    {
                        mergedCounters[property.Name] = (long)property.Value;
                    }
                }
            }

            if (stored?["timings"] is JObject storedTimings)
            {
                foreach (var property in storedTimings.Properties())
                {
                    if (property.Value is JObject value && mergedTimings.Count < MaxKeys)
                    {
                        var timing = new Timing();
                        timing.Add(ReadLong(value["count"]), ReadDouble(value["totalMs"]), ReadDouble(value["maxMs"]));
                        mergedTimings[property.Name] = timing;
                    }
                }
            }

            foreach (var pair in counters)
            {
                mergedCounters.TryGetValue(pair.Key, out var value);
                mergedCounters[pair.Key] = value + pair.Value;
            }

            foreach (var pair in timings)
            {
                if (!mergedTimings.TryGetValue(pair.Key, out var timing))
                {
                    timing = new Timing();
                    mergedTimings[pair.Key] = timing;
                }

                timing.Add(pair.Value.Count, pair.Value.TotalMs, pair.Value.MaxMs);
            }

            var since = stored?["since"]?.Type == JTokenType.String || stored?["since"]?.Type == JTokenType.Date
                ? stored["since"]!
                : new JValue(now.ToString("o", CultureInfo.InvariantCulture));
            var result = new JObject
            {
                ["version"] = 1,
                ["since"] = since.Type == JTokenType.Date ? new JValue(((DateTime)since).ToUniversalTime().ToString("o", CultureInfo.InvariantCulture)) : since,
                ["updated"] = now.ToString("o", CultureInfo.InvariantCulture),
                ["counters"] = new JObject(mergedCounters.Select(pair => new JProperty(pair.Key, pair.Value))),
                ["timings"] = new JObject(mergedTimings.Select(pair => new JProperty(pair.Key, new JObject
                {
                    ["count"] = pair.Value.Count,
                    ["totalMs"] = Math.Round(pair.Value.TotalMs, 1),
                    ["maxMs"] = Math.Round(pair.Value.MaxMs, 1),
                }))),
            };
            return result;
        }

        private static long ReadLong(JToken? token) => token != null && (token.Type == JTokenType.Integer || token.Type == JTokenType.Float) ? (long)token : 0;

        private static double ReadDouble(JToken? token) => token != null && (token.Type == JTokenType.Integer || token.Type == JTokenType.Float) ? (double)token : 0;

        private sealed class Timing
        {
            public long Count { get; private set; }

            public double TotalMs { get; private set; }

            public double MaxMs { get; private set; }

            public void Add(long count, double totalMs, double maxMs)
            {
                Count += Math.Max(0, count);
                TotalMs += Math.Max(0, totalMs);
                MaxMs = Math.Max(MaxMs, maxMs);
            }

            public Timing Copy()
            {
                var copy = new Timing();
                copy.Add(Count, TotalMs, MaxMs);
                return copy;
            }
        }
    }
}
