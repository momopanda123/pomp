using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.VisualStudio.Text;

namespace Pomp.VisualStudio.Editor
{
    /// <summary>A confirmed POMP replacement, never a whole-document baseline containing unrelated user edits.</summary>
    internal sealed class LiveChange
    {
        public int Start;
        public string Before = string.Empty;
        public string After = string.Empty;
        public int Length;
        public bool Conflict;
        public string Left = string.Empty;
        public string Right = string.Empty;
        public int End => Start + Length;
        public LiveChange Copy() => (LiveChange)MemberwiseClone();
        public bool Matches(ITextSnapshot snapshot)
        {
            if (Conflict || Length != After.Length || Start < 0 || End > snapshot.Length || snapshot.GetText(Start, Length) != After) return false;
            // An empty postimage is not evidence: require the surrounding text as well.
            return After.Length != 0 || (Start >= Left.Length && End + Right.Length <= snapshot.Length
                && snapshot.GetText(Start - Left.Length, Left.Length) == Left
                && snapshot.GetText(End, Right.Length) == Right);
        }
        public void CaptureContext(ITextSnapshot snapshot)
        {
            Left = snapshot.GetText(Math.Max(0, Start - 32), Math.Min(32, Start));
            Right = snapshot.GetText(End, Math.Min(32, snapshot.Length - End));
        }
    }

    internal static class LiveChangeTracker
    {
        internal static bool Touches(int start, int end, int otherStart, int otherEnd)
        {
            if (start == end || otherStart == otherEnd) return start <= otherEnd && otherStart <= end;
            return start < otherEnd && otherStart < end;
        }

        /// <summary>Unknown/user edits can move marks, but can never create provenance or silently renew a conflicted baseline.</summary>
        internal static List<LiveChange> Move(IReadOnlyList<LiveChange> entries, ITextSnapshot before, ITextSnapshot after, IEnumerable<ITextChange> changes)
        {
            var result = new List<LiveChange>(entries.Count);
            foreach (var original in entries)
            {
                var entry = original.Copy();
                foreach (var change in changes)
                {
                    // Insertion immediately before/after a nonempty range is a shift, not an overlapping user edit.
                    var overlap = change.OldLength == 0 && entry.Length > 0
                        ? change.OldPosition > entry.Start && change.OldPosition < entry.End
                        : Touches(entry.Start, entry.End, change.OldPosition, change.OldEnd);
                    if (overlap) entry.Conflict = true;
                }
                var span = before.CreateTrackingSpan(Span.FromBounds(entry.Start, entry.End), SpanTrackingMode.EdgeExclusive).GetSpan(after);
                entry.Length = span.Length;
                entry.Start = span.Start.Position;
                if (!entry.Conflict && entry.After.Length == 0) entry.CaptureContext(after);
                // Keep the recorded postimage, not the user's replacement, even when its length changed.
                result.Add(entry);
            }
            return result;
        }

        /// <summary>Compose the confirmed replacements with earlier POMP-only ranges, preserving the session's earliest preimage.</summary>
        internal static List<LiveChange> Record(IReadOnlyList<LiveChange> entries, ITextSnapshot before, ITextSnapshot after, IEnumerable<ITextChange> changes)
        {
            var groups = changes.Select(c => new Span(c.OldPosition, c.OldLength)).ToList();
            // Expand through existing ranges and coalesce. A later replacement may bridge two old hunks.
            bool expanded;
            do
            {
                expanded = false;
                for (var i = 0; i < groups.Count; i++)
                {
                    var span = groups[i];
                    foreach (var entry in entries)
                    {
                        if (!Touches(span.Start, span.End, entry.Start, entry.End)) continue;
                        var union = Span.FromBounds(Math.Min(span.Start, entry.Start), Math.Max(span.End, entry.End));
                        if (union != span) { span = union; expanded = true; }
                    }
                    groups[i] = span;
                    for (var j = groups.Count - 1; j > i; j--)
                    {
                        if (!Touches(span.Start, span.End, groups[j].Start, groups[j].End)) continue;
                        span = Span.FromBounds(Math.Min(span.Start, groups[j].Start), Math.Max(span.End, groups[j].End));
                        groups[i] = span; groups.RemoveAt(j); expanded = true;
                    }
                }
            } while (expanded);

            var untouched = entries.Where(e => !groups.Any(g => Touches(g.Start, g.End, e.Start, e.End))).ToList();
            var result = Move(untouched, before, after, changes);
            foreach (var group in groups)
            {
                var old = before.GetText(group);
                var conflict = false;
                foreach (var entry in entries.Where(e => Touches(group.Start, group.End, e.Start, e.End)).OrderByDescending(e => e.Start))
                {
                    if (!entry.Matches(before)) { conflict = true; continue; }
                    old = old.Remove(entry.Start - group.Start, entry.After.Length).Insert(entry.Start - group.Start, entry.Before);
                }
                var mapped = before.CreateTrackingSpan(group, SpanTrackingMode.EdgeInclusive).GetSpan(after);
                var latest = mapped.GetText();
                if (old == latest && !conflict) continue;
                var next = new LiveChange { Start = mapped.Start.Position, Length = mapped.Length, Before = old, After = latest, Conflict = conflict };
                next.CaptureContext(after);
                result.Add(next);
            }
            result.Sort((a, b) => a.Start.CompareTo(b.Start));
            return result;
        }
    }
}
