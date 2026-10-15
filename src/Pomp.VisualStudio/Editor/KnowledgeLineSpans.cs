using System;
using System.Text;
using Microsoft.VisualStudio.Text;
using Pomp.Features;
using Pomp.Hosting;

namespace Pomp.VisualStudio.Editor
{
    /// <summary>The line-level views of a text snapshot that the knowledge features need (no editor view, no services).</summary>
    internal static class KnowledgeLineSpans
    {
        /// <summary>
        /// The lines a knowledge piece is made from: the selected lines, or the caret's line when nothing is selected. A selection that ends
        /// at column 1 of a line leaves that line out (as EditorContextBuilder.Reference does for the chat's editor context). The text is the
        /// editor's own, line by line, joined with line feeds. Throws <see cref="InvalidOperationException"/> when the lines hold more than
        /// <paramref name="maxChars"/> characters.
        /// </summary>
        public static KnowledgeDraft Read(ITextSnapshot snapshot, SnapshotPoint caret, SnapshotSpan? selection, string fullPath, int maxChars)
        {
            ITextSnapshotLine first, last;
            if (selection == null || selection.Value.IsEmpty)
            {
                first = last = caret.TranslateTo(snapshot, PointTrackingMode.Positive).GetContainingLine();
            }
            else
            {
                var span = selection.Value.TranslateTo(snapshot, SpanTrackingMode.EdgeExclusive);
                first = span.Start.GetContainingLine();
                last = span.End.GetContainingLine();
                if (last.LineNumber > first.LineNumber && span.End.Position == last.Start.Position) last = snapshot.GetLineFromLineNumber(last.LineNumber - 1);
            }

            if (last.End.Position - first.Start.Position > maxChars)
                throw new InvalidOperationException(PompLanguage.Pick("선택한 범위가 너무 커요.", "The selection is too large."));
            var text = new StringBuilder();
            for (var number = first.LineNumber; number <= last.LineNumber; number++)
            {
                if (number > first.LineNumber) text.Append('\n');
                text.Append(snapshot.GetLineFromLineNumber(number).GetText());
            }
            return new KnowledgeDraft { FullPath = fullPath, StartLine = first.LineNumber + 1, EndLine = last.LineNumber + 1, Text = text.ToString() };
        }

        /// <summary>
        /// The lines (0-based) a tracked range covers now. An end at the start of a later line (the range's last line was deleted together
        /// with its line break) leaves that line out.
        /// </summary>
        public static KnowledgeLineRange LinesOf(ITextSnapshot snapshot, Span span)
        {
            var first = snapshot.GetLineNumberFromPosition(span.Start);
            var last = snapshot.GetLineNumberFromPosition(span.End);
            if (last > first && span.End == snapshot.GetLineFromLineNumber(last).Start.Position) last--;
            return new KnowledgeLineRange(first, last);
        }

        /// <summary>A span over the lines, from the start of the first to the end of the last (not its line break); text typed at either edge is not part of it.</summary>
        public static ITrackingSpan Track(ITextSnapshot snapshot, KnowledgeLineRange range) =>
            snapshot.CreateTrackingSpan(Span.FromBounds(snapshot.GetLineFromLineNumber(range.First).Start.Position, snapshot.GetLineFromLineNumber(range.Last).End.Position), SpanTrackingMode.EdgeExclusive);
    }
}
