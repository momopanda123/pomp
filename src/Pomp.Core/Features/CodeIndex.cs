using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace Pomp.Features
{
    public sealed class CodeChunk
    {
        public CodeChunk(string path, string hash, string symbol, int startLine, int endLine, string text)
        { Path = path; Hash = hash; Symbol = symbol; StartLine = startLine; EndLine = endLine; Text = text; }
        public string Path { get; }
        public string Hash { get; }
        public string Symbol { get; }
        public int StartLine { get; }
        public int EndLine { get; }
        public string Text { get; }
    }
    public sealed class CodeIndexSnapshot
    {
        public CodeIndexSnapshot(string identity, IEnumerable<CodeChunk> chunks, IEnumerable<string> notices)
        { Identity = identity; Chunks = chunks.ToArray(); Notices = notices.ToArray(); }
        public string Identity { get; }
        public IReadOnlyList<CodeChunk> Chunks { get; }
        public IReadOnlyList<string> Notices { get; }
    }
    public interface ICodeWorkspaceSource
    {
        /// <summary>Returns only bounded, workspace-contained source snapshots, including unsaved editor text.</summary>
        Task<CodeIndexSnapshot> CaptureAsync(string root, CancellationToken cancellationToken);
    }
    public interface IObservableCodeWorkspaceSource : ICodeWorkspaceSource
    {
        event EventHandler? Changed;
    }
    public interface ICodeIndexAdapters { ICodeWorkspaceSource CodeSource { get; } }
    public sealed class CodeSearchHit
    {
        internal CodeSearchHit(CodeChunk chunk, double score) { Chunk = chunk; Score = score; }
        public CodeChunk Chunk { get; }
        public double Score { get; }
    }
    /// <summary>Immutable local BM25 + symbol/path index. No source is sent to a provider and no opaque vectors are fabricated.</summary>
    public sealed class CodeIndex
    {
        private static readonly Regex Boundary = new Regex(@"(?<=[\p{Ll}\d])(?=\p{Lu})|(?<=\p{Lu})(?=\p{Lu}\p{Ll})", RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(100));
        private static readonly Regex Word = new Regex(@"[\p{L}\p{Nd}]+", RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(100));
        private readonly CodeChunk[] chunks;
        private readonly Dictionary<string, List<Posting>> postings = new Dictionary<string, List<Posting>>(StringComparer.Ordinal);
        private readonly int[] lengths;
        private readonly double averageLength;
        private sealed class Posting { public int Id; public int Count; }
        public CodeIndex(CodeIndexSnapshot snapshot, CancellationToken cancellationToken = default)
        {
            if (snapshot.Chunks.Count > 20000 || snapshot.Chunks.Sum(c => (long)c.Text.Length) > 24000000)
                throw new ArgumentException("Code index snapshot exceeds the local memory budget.", nameof(snapshot));
            chunks = snapshot.Chunks.ToArray(); lengths = new int[chunks.Length];
            for (var i = 0; i < chunks.Length; i++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var terms = Terms(chunks[i].Text + " " + chunks[i].Symbol + " " + chunks[i].Path).GroupBy(x => x);
                foreach (var group in terms)
                {
                    if (!postings.TryGetValue(group.Key, out var list)) postings[group.Key] = list = new List<Posting>();
                    var count = group.Count(); lengths[i] += count; list.Add(new Posting { Id = i, Count = count });
                }
            }
            averageLength = Math.Max(1, lengths.Length == 0 ? 1 : lengths.Average());
        }
        public static IReadOnlyList<string> Terms(string text)
        {
            if (text.Length > 100000) throw new ArgumentException("Token input exceeds its budget.", nameof(text));
            var split = Boundary.Replace(text, " ");
            return Word.Matches(split).Cast<Match>().Select(m => m.Value.ToLowerInvariant()).Where(s => s.Length <= 100).ToArray();
        }
        public IReadOnlyList<CodeSearchHit> Search(string query, int limit = 8, CancellationToken cancellationToken = default)
        {
            if (string.IsNullOrWhiteSpace(query) || query.Length > 2000) throw new ArgumentException("The query is empty or too long.", nameof(query));
            limit = Math.Max(1, Math.Min(20, limit));
            var scores = new Dictionary<int, double>();
            foreach (var term in Terms(query).Distinct().Take(64))
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (!postings.TryGetValue(term, out var list)) continue;
                var idf = Math.Log(1 + (chunks.Length - list.Count + 0.5) / (list.Count + 0.5));
                foreach (var p in list)
                {
                    var score = idf * p.Count * 2.2 / (p.Count + 1.2 * (0.25 + 0.75 * lengths[p.Id] / averageLength));
                    if (chunks[p.Id].Symbol.IndexOf(term, StringComparison.OrdinalIgnoreCase) >= 0) score += 1.5;
                    scores.TryGetValue(p.Id, out var current); scores[p.Id] = current + score;
                }
            }
            var result = new List<CodeSearchHit>();
            foreach (var pair in scores.OrderByDescending(x => x.Value).ThenBy(x => chunks[x.Key].Path, StringComparer.OrdinalIgnoreCase).ThenBy(x => chunks[x.Key].StartLine))
            {
                cancellationToken.ThrowIfCancellationRequested();
                var chunk = chunks[pair.Key];
                // Prefer the more relevant overlapping symbol rather than returning its entire containing class as a duplicate.
                if (result.Any(h => string.Equals(h.Chunk.Path, chunk.Path, StringComparison.OrdinalIgnoreCase) && h.Chunk.StartLine <= chunk.EndLine && chunk.StartLine <= h.Chunk.EndLine)) continue;
                result.Add(new CodeSearchHit(chunk, pair.Value));
                if (result.Count == limit) break;
            }
            return result.AsReadOnly();
        }
    }
}
