using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading;
using Pomp.Adapters;
using Pomp.Hosting;

namespace Pomp.Features
{
    /// <summary>One independently selectable line replacement, insertion or deletion in original text coordinates.</summary>
    public sealed class DiffHunk
    {
        internal DiffHunk(int id, int start, int length, string before, string after, int oldLine, int newLine)
        { Id = id; Start = start; Length = length; Before = before; After = after; OldLine = oldLine; NewLine = newLine; }
        public int Id { get; }
        public int Start { get; }
        public int Length { get; }
        public string Before { get; }
        public string After { get; }
        public int OldLine { get; }
        public int NewLine { get; }
    }

    /// <summary>Immutable diff. Selection and proposal amendments never mutate the original source.</summary>
    public sealed class LineDiff
    {
        private LineDiff(string before, string after, IReadOnlyList<DiffHunk> hunks, bool coarse)
        { Before = before; After = after; Hunks = hunks; IsCoarse = coarse; }
        public string Before { get; }
        public string After { get; }
        public IReadOnlyList<DiffHunk> Hunks { get; }
        /// <summary>Resource limit fallback: one whole-file hunk instead of an unbounded computation.</summary>
        public bool IsCoarse { get; }

        public string ApplySelection(IEnumerable<int> selected)
        {
            if (selected == null) throw new ArgumentNullException(nameof(selected));
            var ids = new HashSet<int>(selected);
            if (ids.Any(id => id < 0 || id >= Hunks.Count)) throw new ArgumentException("Unknown diff hunk.", nameof(selected));
            var output = new StringBuilder(Before.Length);
            var cursor = 0;
            foreach (var hunk in Hunks)
            {
                if (!ids.Contains(hunk.Id)) continue;
                output.Append(Before, cursor, hunk.Start - cursor).Append(hunk.After);
                cursor = hunk.Start + hunk.Length;
            }
            return output.Append(Before, cursor, Before.Length - cursor).ToString();
        }

        /// <summary>Rebuild the complete proposal after editing one hunk. Callers must review new hunk identities/selections.</summary>
        public LineDiff WithHunkReplacement(int hunkId, string replacement, CancellationToken cancellationToken = default)
        {
            if (replacement == null) throw new ArgumentNullException(nameof(replacement));
            if (hunkId < 0 || hunkId >= Hunks.Count) throw new ArgumentException(PompLanguage.Pick("알 수 없는 변경 덩어리예요.", "Unknown diff hunk."), nameof(hunkId));
            cancellationToken.ThrowIfCancellationRequested();
            if (Hunks[hunkId].After == replacement) return this;
            var output = new StringBuilder(Before.Length); var cursor = 0;
            foreach (var hunk in Hunks)
            {
                cancellationToken.ThrowIfCancellationRequested();
                output.Append(Before, cursor, hunk.Start - cursor).Append(hunk.Id == hunkId ? replacement : hunk.After);
                cursor = hunk.Start + hunk.Length;
            }
            output.Append(Before, cursor, Before.Length - cursor);
            return Create(Before, output.ToString(), cancellationToken);
        }

        /// <summary>Myers shortest edit script with bounded trace memory/work and cancellation.</summary>
        public static LineDiff Create(string before, string after, CancellationToken cancellationToken = default)
        {
            if (before == null || after == null) throw new ArgumentNullException(before == null ? nameof(before) : nameof(after));
            cancellationToken.ThrowIfCancellationRequested();
            if (before == after) return new LineDiff(before, after, Array.Empty<DiffHunk>(), false);
            if (before.Length > 2_000_000 || after.Length > 2_000_000) return Coarse(before, after);
            var a = Lines(before); var b = Lines(after);
            var max = a.Count + b.Count;
            if (max > 40000) return Coarse(before, after);
            var offset = max + 1;
            var width = 2 * max + 3;
            var v = new int[width];
            var trace = new List<int[]>();
            v[offset + 1] = 0;
            long work = 0;
            for (var d = 0; d <= max; d++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if ((long)(d + 1) * width > 4_000_000 || work > 4_000_000) return Coarse(before, after);
                trace.Add((int[])v.Clone());
                for (var k = -d; k <= d; k += 2)
                {
                    work++;
                    var x = k == -d || (k != d && v[offset + k - 1] < v[offset + k + 1])
                        ? v[offset + k + 1] : v[offset + k - 1] + 1;
                    var y = x - k;
                    while (x < a.Count && y < b.Count && a[x] == b[y]) { x++; y++; work++; }
                    v[offset + k] = x;
                    if (x >= a.Count && y >= b.Count)
                        return new LineDiff(before, after, BuildHunks(a, b, Backtrack(trace, offset, a.Count, b.Count)).AsReadOnly(), false);
                }
            }
            return Coarse(before, after);
        }

        private static LineDiff Coarse(string before, string after) => new LineDiff(before, after,
            Array.AsReadOnly(new[] { new DiffHunk(0, 0, before.Length, before, after, 1, 1) }), true);

        private static List<char> Backtrack(List<int[]> trace, int offset, int x, int y)
        {
            var result = new List<char>();
            for (var d = trace.Count - 1; d >= 0; d--)
            {
                var v = trace[d]; var k = x - y;
                var previousK = k == -d || (k != d && v[offset + k - 1] < v[offset + k + 1]) ? k + 1 : k - 1;
                var previousX = v[offset + previousK]; var previousY = previousX - previousK;
                while (x > previousX && y > previousY) { result.Add('='); x--; y--; }
                if (d == 0) break;
                if (x == previousX) { result.Add('+'); y--; } else { result.Add('-'); x--; }
            }
            result.Reverse();
            return result;
        }

        private static List<DiffHunk> BuildHunks(List<string> a, List<string> b, List<char> edits)
        {
            var result = new List<DiffHunk>();
            var oldLine = 0; var newLine = 0; var position = 0; var index = 0;
            while (index < edits.Count)
            {
                if (edits[index] == '=') { position += a[oldLine++].Length; newLine++; index++; continue; }
                var oldStart = oldLine; var newStart = newLine;
                while (index < edits.Count && edits[index] != '=')
                { if (edits[index++] == '-') oldLine++; else newLine++; }
                var oldCount = oldLine - oldStart; var newCount = newLine - newStart;
                // A missing EOF newline can make an inserted/deleted line depend on an adjacent
                // replacement. Keep that boundary together instead of silently joining two lines
                // when only half is accepted.
                if (oldCount > 0 && newCount > 0 && oldCount != newCount &&
                    (!EndsInBreak(a[oldLine - 1]) || !EndsInBreak(b[newLine - 1])))
                {
                    var oldText = string.Concat(a.Skip(oldStart).Take(oldCount));
                    var newText = string.Concat(b.Skip(newStart).Take(newCount));
                    result.Add(new DiffHunk(result.Count, position, oldText.Length, oldText, newText, oldStart + 1, newStart + 1));
                    position += oldText.Length;
                    continue;
                }
                var count = Math.Max(oldCount, newCount);
                for (var i = 0; i < count; i++)
                {
                    var oldText = oldStart + i < oldLine ? a[oldStart + i] : string.Empty;
                    var newText = newStart + i < newLine ? b[newStart + i] : string.Empty;
                    result.Add(new DiffHunk(result.Count, position, oldText.Length, oldText, newText,
                        oldStart + Math.Min(i, oldLine - oldStart) + 1, newStart + Math.Min(i, newLine - newStart) + 1));
                    position += oldText.Length;
                }
            }
            return result;
        }

        private static bool EndsInBreak(string line) => line.Length > 0 && TextLineBreaks.IsBreak(line[line.Length - 1]);

        private static List<string> Lines(string text)
        {
            var result = new List<string>(); var start = 0;
            for (var i = 0; i < text.Length; i++)
            {
                if (!TextLineBreaks.IsBreak(text[i])) continue;
                if (text[i] == '\r' && i + 1 < text.Length && text[i + 1] == '\n') i++;
                result.Add(text.Substring(start, i + 1 - start)); start = i + 1;
            }
            if (start < text.Length) result.Add(text.Substring(start));
            return result;
        }
    }
}
