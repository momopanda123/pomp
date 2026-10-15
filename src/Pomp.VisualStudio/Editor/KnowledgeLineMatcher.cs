using System;
using Pomp.Features;

namespace Pomp.VisualStudio.Editor
{
    /// <summary>Lines of a buffer, 0-based and inclusive at both ends.</summary>
    internal readonly struct KnowledgeLineRange
    {
        public KnowledgeLineRange(int first, int last)
        {
            First = first;
            Last = last;
        }

        public int First { get; }

        public int Last { get; }
    }

    /// <summary>
    /// Finds where the saved lines of a knowledge piece sit in a buffer now. It is only given the buffer's lines, so it needs no editor
    /// services (KnowledgeLineFeature feeds it the lines of the open document).
    /// </summary>
    internal sealed class KnowledgeLineMatcher
    {
        /// <summary>A stored range longer than this is not looked at (the store keeps at most 200 lines per piece).</summary>
        private const int MaxRangeLines = 2000;

        private readonly int count;
        private readonly Func<int, string> line;
        private string[]? trimmed;

        /// <param name="count">The number of lines of the buffer.</param>
        /// <param name="line">The text of one line (0-based), without its line break.</param>
        public KnowledgeLineMatcher(int count, Func<int, string> line)
        {
            this.count = count;
            this.line = line;
        }

        /// <summary>
        /// The lines of the piece's saved text, without blank lines at either end: at the range where it was last seen when the hash of that
        /// range's text is still the saved one, else at its one exact match elsewhere in the buffer. Null when the text is gone, or sits in
        /// several places.
        /// </summary>
        public KnowledgeLineRange? Find(KnowledgeLineMark mark)
        {
            var wanted = KnowledgeText.Normalize(mark.Text);
            if (wanted.Length == 0) return null;
            var hash = string.IsNullOrEmpty(mark.Hash) ? KnowledgeText.Hash(mark.Text) : mark.Hash;

            var from = mark.StartLine - 1;
            var to = mark.EndLine - 1;
            if (from >= 0 && to >= from && to < count && to - from < MaxRangeLines)
            {
                var seen = new string[to - from + 1];
                for (var i = 0; i < seen.Length; i++) seen[i] = line(from + i);
                if (string.Equals(KnowledgeText.Hash(string.Join("\n", seen)), hash, StringComparison.OrdinalIgnoreCase))
                {
                    var first = 0;
                    var last = seen.Length - 1;
                    while (first <= last && string.IsNullOrWhiteSpace(seen[first])) first++;
                    while (last >= first && string.IsNullOrWhiteSpace(seen[last])) last--;
                    return first > last ? (KnowledgeLineRange?)null : new KnowledgeLineRange(from + first, from + last);
                }
            }

            var wantedLines = wanted.Split('\n');
            var lines = Trimmed();
            var found = -1;
            for (var start = 0; start + wantedLines.Length <= lines.Length; start++)
            {
                if (!string.Equals(lines[start], wantedLines[0], StringComparison.Ordinal)) continue;
                var k = 1;
                while (k < wantedLines.Length && string.Equals(lines[start + k], wantedLines[k], StringComparison.Ordinal)) k++;
                if (k < wantedLines.Length) continue;
                if (found >= 0) return null;
                found = start;
            }

            return found < 0 ? (KnowledgeLineRange?)null : new KnowledgeLineRange(found, found + wantedLines.Length - 1);
        }

        /// <summary>Every line without trailing white space, as <see cref="KnowledgeText.Normalize"/> compares them (built once, when first needed).</summary>
        private string[] Trimmed()
        {
            if (trimmed != null) return trimmed;
            var lines = new string[count];
            for (var i = 0; i < lines.Length; i++) lines[i] = line(i).TrimEnd();
            return trimmed = lines;
        }
    }
}
