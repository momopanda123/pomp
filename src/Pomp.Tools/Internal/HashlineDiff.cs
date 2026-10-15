using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using Pomp.Adapters;

namespace Pomp.Tools
{
    /// <summary>Exact, bounded unified previews generated from the same immutable before/after text as the adapter plan.</summary>
    internal static class HashlineDiff
    {
        public const int MaxCharacters = 180000;
        private const int ContextLines = 3;

        public static string Create(IReadOnlyList<HashlineFilePlan> files)
        {
            var builder = new StringBuilder();
            foreach (var file in files)
            {
                AppendFile(builder, file);
            }

            if (builder.Length == 0)
            {
                throw new ToolException("The patch makes no text changes.");
            }

            return builder.ToString();
        }

        private static void AppendFile(StringBuilder builder, HashlineFilePlan file)
        {
            if (string.Equals(file.OriginalText, file.ResultText, StringComparison.Ordinal))
            {
                return;
            }

            var unicode = HasUnicodeBreak(file.OriginalText) || HasUnicodeBreak(file.ResultText);
            var before = unicode ? PreviewLines(file.OriginalText) : TextUtil.SplitLines(file.OriginalText);
            var after = unicode ? PreviewLines(file.ResultText) : TextUtil.SplitLines(file.ResultText);
            var beforeBreak = file.OriginalText.Length > 0 && TextLineBreaks.IsBreak(file.OriginalText[file.OriginalText.Length - 1]);
            var afterBreak = file.ResultText.Length > 0 && TextLineBreaks.IsBreak(file.ResultText[file.ResultText.Length - 1]);
            if (unicode) Append(builder, "Unicode line breaks are shown as ⟦U+XXXX⟧; literal backslashes and marker brackets are escaped in this preview.\n");
            var prefix = 0;
            while (prefix < before.Count && prefix < after.Count && SameLine(before, prefix, beforeBreak, after, prefix, afterBreak))
            {
                prefix++;
            }

            var suffix = 0;
            while (suffix < before.Count - prefix && suffix < after.Count - prefix
                && SameLine(before, before.Count - suffix - 1, beforeBreak, after, after.Count - suffix - 1, afterBreak))
            {
                suffix++;
            }

            var contextStart = Math.Max(0, prefix - ContextLines);
            var trailingContext = Math.Min(ContextLines, suffix);
            var oldEnd = before.Count - suffix + trailingContext;
            var newEnd = after.Count - suffix + trailingContext;
            Append(builder, "--- a/" + file.DisplayPath + "\n+++ b/" + file.DisplayPath + "\n");
            Append(builder, "@@ -" + Range(contextStart, oldEnd - contextStart) + " +" + Range(contextStart, newEnd - contextStart) + " @@\n");
            for (var i = contextStart; i < prefix; i++)
            {
                AppendLine(builder, ' ', before, i, beforeBreak);
            }

            for (var i = prefix; i < before.Count - suffix; i++)
            {
                AppendLine(builder, '-', before, i, beforeBreak);
            }

            for (var i = prefix; i < after.Count - suffix; i++)
            {
                AppendLine(builder, '+', after, i, afterBreak);
            }

            for (var i = before.Count - suffix; i < oldEnd; i++)
            {
                AppendLine(builder, ' ', before, i, beforeBreak);
            }
        }

        private static bool HasUnicodeBreak(string text) => text.IndexOfAny(new[] { '\u0085', '\u2028', '\u2029' }) >= 0;

        private static List<string> PreviewLines(string text)
        {
            var lines = new List<string>();
            var start = 0;
            for (var i = 0; i < text.Length; i++)
            {
                if (!TextLineBreaks.IsBreak(text[i])) continue;
                var content = EscapePreview(text.Substring(start, i - start));
                if (text[i] != '\n' && text[i] != '\r')
                    content += "⟦U+" + ((int)text[i]).ToString("X4", CultureInfo.InvariantCulture) + "⟧";
                lines.Add(content);
                if (text[i] == '\r' && i + 1 < text.Length && text[i + 1] == '\n') i++;
                start = i + 1;
            }
            if (start < text.Length) lines.Add(EscapePreview(text.Substring(start)));
            return lines;
        }

        private static string EscapePreview(string text) => text.Replace("\\", "\\\\").Replace("⟦", "\\u27E6").Replace("⟧", "\\u27E7");

        private static bool SameLine(IReadOnlyList<string> left, int leftIndex, bool leftBreak, IReadOnlyList<string> right, int rightIndex, bool rightBreak) =>
            string.Equals(left[leftIndex], right[rightIndex], StringComparison.Ordinal)
            && (leftIndex < left.Count - 1 || leftBreak) == (rightIndex < right.Count - 1 || rightBreak);

        private static string Range(int offset, int count) =>
            (count == 0 ? offset : offset + 1).ToString(CultureInfo.InvariantCulture) + "," + count.ToString(CultureInfo.InvariantCulture);

        private static void AppendLine(StringBuilder builder, char prefix, IReadOnlyList<string> lines, int index, bool endsWithBreak)
        {
            Append(builder, prefix + lines[index] + "\n");
            if (index == lines.Count - 1 && !endsWithBreak)
            {
                Append(builder, "\\ No newline at end of file\n");
            }
        }

        private static void Append(StringBuilder builder, string text)
        {
            if (text.Length > MaxCharacters - builder.Length)
            {
                throw new ToolException("The complete approval diff exceeds " + MaxCharacters + " characters. Split distant or large edits into smaller calls; no edits were applied.");
            }

            builder.Append(text);
        }
    }
}
