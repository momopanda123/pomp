using System;
using System.Collections.Generic;
using System.Text;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Text;
using Microsoft.VisualStudio.Text.Adornments;
using Microsoft.VisualStudio.Text.Tagging;
using Pomp.Tools;

namespace Pomp.VisualStudio.Editor
{
    /// <summary>
    /// Declarations of a file no POMP scanner reads, from the editor's own block structure: the <see cref="IStructureTag"/> blocks a language
    /// service reports for its structure guide lines and outlining, only those of type <see cref="PredefinedStructureTagTypes.Type"/> or
    /// <see cref="PredefinedStructureTagTypes.Member"/> (never statements, loops, conditionals, comments or regions). A language whose service
    /// reports no such blocks (or no block structure at all) has no declarations. UI thread.
    /// </summary>
    internal static class EditorStructureDeclarations
    {
        private const int MaxNameCharacters = 120;

        /// <summary>The type and member blocks of <paramref name="snapshot"/>, enclosing ones before those inside them.</summary>
        public static IReadOnlyList<SourceDeclaration> Find(IBufferTagAggregatorFactoryService factory, ITextSnapshot snapshot)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            var result = new List<SourceDeclaration>();
            using (var aggregator = factory.CreateTagAggregator<IStructureTag>(snapshot.TextBuffer))
            {
                foreach (var tag in aggregator.GetTags(new SnapshotSpan(snapshot, 0, snapshot.Length)))
                {
                    var isType = string.Equals(tag.Tag.Type, PredefinedStructureTagTypes.Type, StringComparison.Ordinal);
                    if (!isType && !string.Equals(tag.Tag.Type, PredefinedStructureTagTypes.Member, StringComparison.Ordinal)) continue;
                    // The block runs from its header (the declaration's signature) through its collapsible contents.
                    var start = int.MaxValue;
                    var end = -1;
                    foreach (var span in tag.Span.GetSpans(snapshot)) Include(span, ref start, ref end);
                    Include(tag.Tag.Snapshot, tag.Tag.HeaderSpan, snapshot, ref start, ref end);
                    Include(tag.Tag.Snapshot, tag.Tag.OutliningSpan, snapshot, ref start, ref end);
                    while (start < end && char.IsWhiteSpace(snapshot[start])) start++;
                    while (end > start && char.IsWhiteSpace(snapshot[end - 1])) end--;
                    if (end <= start) continue;
                    var line = snapshot.GetLineFromPosition(start);
                    result.Add(new SourceDeclaration(isType, line.LineNumber + 1, start, end - start, Name(snapshot.GetText(start, Math.Min(line.End.Position, end) - start))));
                }
            }

            result.Sort((a, b) => a.Start != b.Start ? a.Start.CompareTo(b.Start) : b.Length.CompareTo(a.Length));
            result.RemoveAll(declaration => declaration.Name.Length == 0);
            for (var i = result.Count - 1; i > 0; i--)
            {
                if (result[i].Start == result[i - 1].Start && result[i].Length == result[i - 1].Length) result.RemoveAt(i);
            }

            return result;
        }

        private static void Include(SnapshotSpan span, ref int start, ref int end)
        {
            start = Math.Min(start, span.Start.Position);
            end = Math.Max(end, span.End.Position);
        }

        private static void Include(ITextSnapshot? source, Span? span, ITextSnapshot snapshot, ref int start, ref int end)
        {
            if (span == null) return;
            source ??= snapshot;
            if (source.TextBuffer != snapshot.TextBuffer || span.Value.End > source.Length) return;
            Include(new SnapshotSpan(source, span.Value).TranslateTo(snapshot, SpanTrackingMode.EdgeInclusive), ref start, ref end);
        }

        /// <summary>The header line as written, whitespace collapsed, at most 120 characters.</summary>
        private static string Name(string header)
        {
            var name = new StringBuilder();
            var space = false;
            foreach (var c in header)
            {
                if (name.Length == MaxNameCharacters) break;
                if (char.IsWhiteSpace(c)) { space = name.Length > 0; continue; }
                if (space) { name.Append(' '); space = false; }
                name.Append(c);
            }

            return name.ToString(0, Math.Min(name.Length, MaxNameCharacters));
        }
    }
}
