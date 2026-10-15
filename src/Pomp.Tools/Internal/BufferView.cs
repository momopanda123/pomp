using System;
using System.Collections.Generic;
using System.Text;
using Pomp.Adapters;

namespace Pomp.Tools
{
    /// <summary>A <see cref="BufferSnapshot"/> prepared for output: LF-normalized lines, content hash and tag.</summary>
    internal sealed class BufferView
    {
        private BufferView(IReadOnlyList<string> lines, string contentHash, bool isDirty, bool isOpenInEditor, bool looksBinary)
        {
            Lines = lines;
            ContentHash = contentHash;
            Tag = TextUtil.TagFromHash(contentHash);
            IsDirty = isDirty;
            IsOpenInEditor = isOpenInEditor;
            LooksBinary = looksBinary;
        }

        public IReadOnlyList<string> Lines { get; }

        /// <summary>SHA-256 (lower-case hex) of the LF-normalized text; the adapter's value when it is well-formed.</summary>
        public string ContentHash { get; }

        public string Tag { get; }

        public bool IsDirty { get; }

        public bool IsOpenInEditor { get; }

        public bool LooksBinary { get; }

        /// <summary>
        /// Uses the adapter's <see cref="BufferSnapshot.ContentHash"/> when it is a proper SHA-256 digest (it is the value
        /// <see cref="ITextBufferAdapter.ApplyEditsAsync"/> compares against) and computes the hash itself otherwise.
        /// </summary>
        public static BufferView Create(BufferSnapshot snapshot)
        {
            var text = snapshot.Text ?? string.Empty;
            var normalized = TextUtil.NormalizeLineEndings(text);
            var hash = TextUtil.IsContentHash(snapshot.ContentHash)
                ? snapshot.ContentHash.ToLowerInvariant()
                : TextUtil.ComputeContentHash(normalized);
            return new BufferView(TextUtil.SplitLines(normalized), hash, snapshot.IsDirty, snapshot.IsOpenInEditor, TextUtil.LooksBinary(text));
        }
    }

    /// <summary>The omp hashline read format: a <c>[path#TAG]</c> header followed by <c>N:TEXT</c> rows.</summary>
    internal static class HashlineFormat
    {
        public static string Header(string displayPath, string tag) => "[" + displayPath + "#" + tag + "]";

        /// <summary>
        /// Appends a <c>\nN:TEXT</c> row for each line in [startLine, endLine] (1-based, inclusive), stopping before the
        /// row that would push the output over <paramref name="maxCharacters"/> (the first row is always written;
        /// if it alone is too long it is cut and marked). Returns the number of the last FULLY shown line; a cut line is not seen for editing.
        /// </summary>
        public static int AppendNumberedLines(StringBuilder builder, IReadOnlyList<string> lines, int startLine, int endLine, int maxCharacters)
        {
            var used = 0;
            var last = startLine - 1;
            for (var number = startLine; number <= endLine; number++)
            {
                var text = lines[number - 1];
                var cost = text.Length + number.ToString().Length + 2;
                if (number > startLine && used + cost > maxCharacters)
                {
                    break;
                }

                builder.Append('\n').Append(number).Append(':');
                if (number == startLine && text.Length > maxCharacters)
                {
                    builder.Append(text, 0, maxCharacters)
                        .Append(TextUtil.Ellipsis).Append("[line cut: ").Append(text.Length - maxCharacters).Append(" more characters; not eligible for line-anchored editing]");
                    break;
                }
                else
                {
                    builder.Append(text);
                }

                used += Math.Min(cost, maxCharacters);
                last = number;
            }

            return last;
        }
    }
}
