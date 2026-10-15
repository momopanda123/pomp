using System;
using System.Collections.Generic;

namespace Pomp.Tools
{
    /// <summary>Inclusive 1-based range of line numbers.</summary>
    public readonly struct LineRange
    {
        public LineRange(int start, int end)
        {
            Start = start;
            End = end;
        }

        public int Start { get; }

        public int End { get; }
    }

    /// <summary>
    /// One recorded version of a file as shown to the model: the tag, the content hash the editor adapter uses for
    /// stale detection, all lines (LF-normalized) and which line ranges the model has actually seen.
    /// </summary>
    public sealed class BufferSnapshotEntry
    {
        internal BufferSnapshotEntry(string path, string contentHash, IReadOnlyList<string> lines, IReadOnlyList<LineRange> seenRanges, int characterCount, DateTime recordedAtUtc)
        {
            Path = path;
            ContentHash = contentHash;
            Tag = TextUtil.TagFromHash(contentHash);
            Lines = new List<string>(lines).AsReadOnly();
            SeenRanges = new List<LineRange>(seenRanges).AsReadOnly();
            CharacterCount = characterCount;
            RecordedAtUtc = recordedAtUtc;
        }

        /// <summary>Full path the snapshot was recorded for.</summary>
        public string Path { get; }

        /// <summary>Four uppercase hex characters shown in the <c>[path#TAG]</c> header.</summary>
        public string Tag { get; }

        /// <summary>SHA-256 (lower-case hex) of the LF-normalized content; the value to send as <c>ExpectedContentHash</c>.</summary>
        public string ContentHash { get; }

        /// <summary>All lines of this version (no trailing empty line for a final newline).</summary>
        public IReadOnlyList<string> Lines { get; }

        /// <summary>Merged, ordered line ranges that were part of a read result.</summary>
        public IReadOnlyList<LineRange> SeenRanges { get; }

        public DateTime RecordedAtUtc { get; }

        /// <summary>Approximate memory cost in characters (line lengths plus one per line).</summary>
        internal int CharacterCount { get; }

        /// <summary>True when every line of [startLine, endLine] was shown to the model.</summary>
        public bool HasSeen(int startLine, int endLine)
        {
            foreach (var range in SeenRanges)
            {
                if (range.Start <= startLine && range.End >= endLine)
                {
                    return true;
                }
            }

            return false;
        }
    }

    /// <summary>
    /// Small in-memory store of the file versions handed out by <c>read_buffer</c> / <c>get_editor_context</c>
    /// (path + tag -> content hash and lines). The future <c>edit_buffer_hashline</c> resolves the tag of a
    /// <c>[path#TAG]</c> header here to detect stale edits. It lives inside the Visual Studio process, so it is strictly
    /// bounded: at most a fixed number of paths and versions per path, no single huge file, and a total character
    /// budget; the least recently used data is evicted first. Thread-safe. Path keys are compared case-insensitively.
    /// </summary>
    public sealed class BufferSnapshotStore
    {
        public const int DefaultMaxPaths = 128;
        public const int DefaultMaxVersionsPerPath = 4;
        public const int DefaultMaxCharactersPerSnapshot = 2000000;
        public const int DefaultMaxTotalCharacters = 8000000;

        private readonly object _gate = new object();
        private readonly int _maxPaths;
        private readonly int _maxVersionsPerPath;
        private readonly int _maxCharactersPerSnapshot;
        private readonly int _maxTotalCharacters;
        private readonly Dictionary<string, List<BufferSnapshotEntry>> _entries = new Dictionary<string, List<BufferSnapshotEntry>>(StringComparer.OrdinalIgnoreCase);
        private readonly List<string> _recency = new List<string>();
        private long _totalCharacters;

        public BufferSnapshotStore()
            : this(DefaultMaxPaths, DefaultMaxVersionsPerPath, DefaultMaxCharactersPerSnapshot, DefaultMaxTotalCharacters)
        {
        }

        public BufferSnapshotStore(int maxPaths, int maxVersionsPerPath, int maxCharactersPerSnapshot)
            : this(maxPaths, maxVersionsPerPath, maxCharactersPerSnapshot, DefaultMaxTotalCharacters)
        {
        }

        public BufferSnapshotStore(int maxPaths, int maxVersionsPerPath, int maxCharactersPerSnapshot, int maxTotalCharacters)
        {
            if (maxPaths < 1)
            {
                throw new ArgumentOutOfRangeException(nameof(maxPaths));
            }

            if (maxVersionsPerPath < 1)
            {
                throw new ArgumentOutOfRangeException(nameof(maxVersionsPerPath));
            }

            if (maxCharactersPerSnapshot < 1)
            {
                throw new ArgumentOutOfRangeException(nameof(maxCharactersPerSnapshot));
            }

            if (maxTotalCharacters < 1)
            {
                throw new ArgumentOutOfRangeException(nameof(maxTotalCharacters));
            }

            _maxPaths = maxPaths;
            _maxVersionsPerPath = maxVersionsPerPath;
            _maxCharactersPerSnapshot = maxCharactersPerSnapshot;
            _maxTotalCharacters = maxTotalCharacters;
        }

        /// <summary>Total number of stored versions over all paths.</summary>
        public int Count
        {
            get
            {
                lock (_gate)
                {
                    var count = 0;
                    foreach (var versions in _entries.Values)
                    {
                        count += versions.Count;
                    }

                    return count;
                }
            }
        }

        /// <summary>Approximate number of characters held by all stored versions.</summary>
        public long TotalCharacters
        {
            get
            {
                lock (_gate)
                {
                    return _totalCharacters;
                }
            }
        }

        /// <summary>
        /// Records a version of <paramref name="path"/> and the line range the model was shown
        /// (pass <c>seenStartLine &gt; seenEndLine</c> for none). Recording the same content again merges the seen ranges.
        /// Returns null when the content is too large to keep.
        /// </summary>
        public BufferSnapshotEntry? Record(string path, string contentHash, IReadOnlyList<string> lines, int seenStartLine, int seenEndLine)
        {
            if (string.IsNullOrEmpty(path))
            {
                throw new ArgumentException("The path must not be empty.", nameof(path));
            }

            if (!TextUtil.IsContentHash(contentHash))
            {
                throw new ArgumentException("The content hash must be a 64-character hexadecimal SHA-256 digest.", nameof(contentHash));
            }

            if (lines == null)
            {
                throw new ArgumentNullException(nameof(lines));
            }

            long characters = 0;
            for (var i = 0; i < lines.Count; i++)
            {
                characters += lines[i].Length + 1;
                if (characters > _maxCharactersPerSnapshot || characters > _maxTotalCharacters)
                {
                    return null;
                }
            }

            var hash = contentHash.ToLowerInvariant();
            lock (_gate)
            {
                if (!_entries.TryGetValue(path, out var versions))
                {
                    versions = new List<BufferSnapshotEntry>();
                    _entries[path] = versions;
                }

                var seen = new List<LineRange>();
                var existingIndex = versions.FindIndex(e => string.Equals(e.ContentHash, hash, StringComparison.Ordinal));
                var sharedLines = lines;
                if (existingIndex >= 0)
                {
                    seen.AddRange(versions[existingIndex].SeenRanges);
                    sharedLines = versions[existingIndex].Lines;
                    RemoveVersion(versions, existingIndex);
                }

                if (seenStartLine >= 1 && seenEndLine >= seenStartLine)
                {
                    seen.Add(new LineRange(seenStartLine, seenEndLine));
                }

                var entry = new BufferSnapshotEntry(path, hash, sharedLines, Merge(seen), (int)characters, DateTime.UtcNow);
                versions.Add(entry);
                _totalCharacters += entry.CharacterCount;
                while (versions.Count > _maxVersionsPerPath)
                {
                    RemoveVersion(versions, 0);
                }

                Touch(path);
                while (_recency.Count > _maxPaths)
                {
                    RemovePath(_recency[0]);
                }

                EnforceTotalBudget(entry);
                return entry;
            }
        }

        /// <summary>Finds a tag only when it identifies exactly one full hash among retained versions.</summary>
        public bool TryGetUnambiguous(string path, string tag, out BufferSnapshotEntry entry)
        {
            entry = null!;
            if (string.IsNullOrEmpty(path) || string.IsNullOrEmpty(tag)) return false;
            lock (_gate)
            {
                if (!_entries.TryGetValue(path, out var versions)) return false;
                BufferSnapshotEntry? match = null;
                foreach (var version in versions)
                {
                    if (!string.Equals(version.Tag, tag, StringComparison.OrdinalIgnoreCase)) continue;
                    if (match != null && !string.Equals(match.ContentHash, version.ContentHash, StringComparison.OrdinalIgnoreCase)) return false;
                    match = version;
                }

                if (match == null) return false;
                entry = match;
                return true;
            }
        }

        /// <summary>Finds the version of <paramref name="path"/> whose tag equals <paramref name="tag"/> (case-insensitive).</summary>
        public bool TryGet(string path, string tag, out BufferSnapshotEntry entry)
        {
            entry = null!;
            if (string.IsNullOrEmpty(path) || string.IsNullOrEmpty(tag))
            {
                return false;
            }

            lock (_gate)
            {
                if (!_entries.TryGetValue(path, out var versions))
                {
                    return false;
                }

                for (var i = versions.Count - 1; i >= 0; i--)
                {
                    if (string.Equals(versions[i].Tag, tag, StringComparison.OrdinalIgnoreCase))
                    {
                        entry = versions[i];
                        return true;
                    }
                }

                return false;
            }
        }

        /// <summary>The most recently recorded version of <paramref name="path"/>, or null.</summary>
        public BufferSnapshotEntry? GetLatest(string path)
        {
            if (string.IsNullOrEmpty(path))
            {
                return null;
            }

            lock (_gate)
            {
                if (_entries.TryGetValue(path, out var versions) && versions.Count > 0)
                {
                    return versions[versions.Count - 1];
                }

                return null;
            }
        }

        public void Remove(string path)
        {
            if (string.IsNullOrEmpty(path))
            {
                return;
            }

            lock (_gate)
            {
                RemovePath(path);
            }
        }

        public void Clear()
        {
            lock (_gate)
            {
                _entries.Clear();
                _recency.Clear();
                _totalCharacters = 0;
            }
        }

        private void RemoveVersion(List<BufferSnapshotEntry> versions, int index)
        {
            _totalCharacters -= versions[index].CharacterCount;
            versions.RemoveAt(index);
        }

        private void RemovePath(string path)
        {
            if (_entries.TryGetValue(path, out var versions))
            {
                foreach (var version in versions)
                {
                    _totalCharacters -= version.CharacterCount;
                }

                _entries.Remove(path);
            }

            _recency.RemoveAll(p => string.Equals(p, path, StringComparison.OrdinalIgnoreCase));
        }

        /// <summary>Drops the oldest versions of the least recently used paths until the character budget holds (never <paramref name="keep"/>).</summary>
        private void EnforceTotalBudget(BufferSnapshotEntry keep)
        {
            while (_totalCharacters > _maxTotalCharacters)
            {
                string? victimPath = null;
                var victimIndex = -1;
                foreach (var candidatePath in _recency)
                {
                    var index = _entries[candidatePath].FindIndex(e => !ReferenceEquals(e, keep));
                    if (index >= 0)
                    {
                        victimPath = candidatePath;
                        victimIndex = index;
                        break;
                    }
                }

                if (victimPath == null)
                {
                    return;
                }

                var versions = _entries[victimPath];
                RemoveVersion(versions, victimIndex);
                if (versions.Count == 0)
                {
                    RemovePath(victimPath);
                }
            }
        }

        private void Touch(string path)
        {
            _recency.RemoveAll(p => string.Equals(p, path, StringComparison.OrdinalIgnoreCase));
            _recency.Add(path);
        }

        private static IReadOnlyList<LineRange> Merge(List<LineRange> ranges)
        {
            if (ranges.Count <= 1)
            {
                return ranges.ToArray();
            }

            ranges.Sort((a, b) => a.Start.CompareTo(b.Start));
            var merged = new List<LineRange>();
            var current = ranges[0];
            for (var i = 1; i < ranges.Count; i++)
            {
                var next = ranges[i];
                if (next.Start <= current.End + 1)
                {
                    current = new LineRange(current.Start, Math.Max(current.End, next.End));
                }
                else
                {
                    merged.Add(current);
                    current = next;
                }
            }

            merged.Add(current);
            return merged.ToArray();
        }
    }
}
