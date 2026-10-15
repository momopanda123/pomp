using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using Pomp.Adapters;

namespace Pomp.VisualStudio.Adapters
{
    /// <summary>One text replacement in original-text coordinates.</summary>
    internal sealed class PlannedChange
    {
        public PlannedChange(int start, int length, string newText)
        {
            Start = start;
            Length = length;
            NewText = newText;
        }

        public int Start { get; }

        public int Length { get; }

        public string NewText { get; }
    }

    /// <summary>
    /// Turns line based edits ("replace original lines [S..E] with these lines") into character replacements over the exact text.
    /// <para>
    /// Lines follow the editor at CRLF, CR, LF, U+0085, U+2028 and U+2029. As in the tools' hashline model,
    /// a final line break does not start another
    /// line, and empty text has no lines. Lines are 1-based; a pure insertion before line S is (S, S-1); appending after the last
    /// line is (count+1, count). Touching replacements are merged, so the planned spans are ordered and never adjacent.
    /// </para>
    /// Pure (no editor types), so the tools' predicted result can be checked against it.
    /// </summary>
    internal static class LineEditPlanner
    {
        /// <summary>Returns null on success (plan filled), otherwise an error message.</summary>
        public static string? Plan(string text, IReadOnlyList<TextLineEdit> edits, string lineEnding, out List<PlannedChange> plan)
        {
            plan = new List<PlannedChange>();

            // Per logical line: where it starts, where its text ends (before the break) and where its break ends.
            var starts = new List<int>();
            var contentEnds = new List<int>();
            var breakEnds = new List<int>();
            var position = 0;
            while (position < text.Length)
            {
                starts.Add(position);
                while (position < text.Length && !TextLineBreaks.IsBreak(text[position]))
                {
                    position++;
                }

                contentEnds.Add(position);
                if (position < text.Length)
                {
                    position += text[position] == '\r' && position + 1 < text.Length && text[position + 1] == '\n' ? 2 : 1;
                }

                breakEnds.Add(position);
            }

            var logicalCount = starts.Count;
            var endsWithBreak = logicalCount > 0 && breakEnds[logicalCount - 1] > contentEnds[logicalCount - 1];
            var bufferEnd = text.Length;

            // Stable ordering: by start line, insertions (end = start - 1) before replacements of the same line, then by
            // request order (two insertions before the same line keep their order).
            var ordered = edits
                .Select((edit, index) => new { edit, index })
                .OrderBy(x => x.edit.StartLine)
                .ThenBy(x => x.edit.EndLine)
                .ThenBy(x => x.index)
                .Select(x => x.edit)
                .ToList();

            var previousEnd = -1;
            var previousEndLine = 0;
            foreach (var edit in ordered)
            {
                var start = edit.StartLine;
                var end = edit.EndLine;
                var isInsertion = end == start - 1;

                if (start < 1 || start > logicalCount + 1)
                {
                    return "Edit start line " + start + " is outside the file (1.." + (logicalCount + 1) + ").";
                }

                if (end < start - 1 || end > logicalCount)
                {
                    return "Edit end line " + end + " is invalid for a start line of " + start + " in a file of " + logicalCount + " lines.";
                }

                if (start <= previousEndLine)
                {
                    return "Edits overlap (line " + start + " is already covered by the previous edit).";
                }

                var newText = JoinLines(edit.NewLines, lineEnding, out var hasNewLines);
                PlannedChange change;

                if (isInsertion)
                {
                    if (!hasNewLines)
                    {
                        previousEndLine = Math.Max(previousEndLine, end);
                        continue;
                    }

                    if (start <= logicalCount)
                    {
                        change = new PlannedChange(starts[start - 1], 0, newText + lineEnding);
                    }
                    else if (endsWithBreak || logicalCount == 0)
                    {
                        change = new PlannedChange(bufferEnd, 0, newText + lineEnding);
                    }
                    else
                    {
                        // Appending after a last line that has no line break: add the separator first, keep "no trailing newline".
                        change = new PlannedChange(bufferEnd, 0, lineEnding + newText);
                    }
                }
                else
                {
                    var spanStart = starts[start - 1];
                    var spanEnd = breakEnds[end - 1];
                    var lastHasBreak = breakEnds[end - 1] > contentEnds[end - 1];
                    var replacement = newText;

                    if (hasNewLines && lastHasBreak)
                    {
                        replacement += lineEnding;
                    }
                    else if (!hasNewLines && !lastHasBreak && start > 1)
                    {
                        // Deleting the tail of a file without a trailing line break: also drop the break before it.
                        var candidate = contentEnds[start - 2];
                        if (candidate >= previousEnd)
                        {
                            spanStart = candidate;
                        }
                    }

                    change = new PlannedChange(spanStart, spanEnd - spanStart, replacement);
                }

                if (change.Start < previousEnd)
                {
                    return "Edits overlap at a line break; separate them into different calls.";
                }

                if (plan.Count > 0 && change.Start == previousEnd)
                {
                    // Touching changes become one replacement, so the editor cannot order same-position changes differently.
                    var last = plan[plan.Count - 1];
                    plan[plan.Count - 1] = new PlannedChange(last.Start, last.Length + change.Length, last.NewText + change.NewText);
                }
                else
                {
                    plan.Add(change);
                }

                previousEnd = change.Start + change.Length;
                previousEndLine = Math.Max(previousEndLine, end);
            }

            SeparateLoneCarriageReturns(text, lineEnding, plan);
            return null;
        }

        /// <summary>
        /// A lone "\r" line break directly before a change whose result starts with "\n" would fuse with it into one "\r\n" break
        /// and lose a line. Such carriage returns (the whole run of them) are rewritten with the file's line ending, which keeps the
        /// normalized text, and therefore the content hash, unchanged.
        /// </summary>
        private static void SeparateLoneCarriageReturns(string text, string lineEnding, List<PlannedChange> plan)
        {
            var index = 0;
            while (index < plan.Count)
            {
                var change = plan[index];
                var after = change.Start + change.Length;
                var next = change.NewText.Length > 0 ? change.NewText[0] : after < text.Length ? text[after] : '\0';
                var floor = index > 0 ? plan[index - 1].Start + plan[index - 1].Length : 0;
                var run = 0;
                if (next == '\n')
                {
                    while (change.Start - run > floor && text[change.Start - run - 1] == '\r')
                    {
                        run++;
                    }
                }

                if (run == 0)
                {
                    index++;
                    continue;
                }

                var prefix = new StringBuilder(run * lineEnding.Length);
                for (var i = 0; i < run; i++)
                {
                    prefix.Append(lineEnding);
                }

                var widened = new PlannedChange(change.Start - run, change.Length + run, prefix + change.NewText);
                if (index > 0 && widened.Start == floor)
                {
                    // Now touching the previous change: merge, and check the merged change again (its result may start with "\n").
                    var previous = plan[index - 1];
                    plan[index - 1] = new PlannedChange(previous.Start, previous.Length + widened.Length, previous.NewText + widened.NewText);
                    plan.RemoveAt(index);
                    index--;
                }
                else
                {
                    plan[index] = widened;
                    index++;
                }
            }
        }

        /// <summary>The text an ordered, non-overlapping <paramref name="plan"/> produces from <paramref name="text"/>.</summary>
        public static string ApplyTo(string text, IReadOnlyList<PlannedChange> plan)
        {
            var builder = new StringBuilder(text.Length);
            var cursor = 0;
            foreach (var change in plan)
            {
                builder.Append(text, cursor, change.Start - cursor).Append(change.NewText);
                cursor = change.Start + change.Length;
            }

            return builder.Append(text, cursor, text.Length - cursor).ToString();
        }

        private static string JoinLines(IReadOnlyList<string>? lines, string lineEnding, out bool hasLines)
        {
            hasLines = lines != null && lines.Count > 0;
            if (!hasLines)
            {
                return string.Empty;
            }

            // Elements must not contain line breaks of their own; normalise anything that does.
            var normalized = lines!.Select(line => TextLineBreaks.NormalizePatch(line ?? string.Empty).Replace("\n", lineEnding));
            return string.Join(lineEnding, normalized);
        }
    }
}
