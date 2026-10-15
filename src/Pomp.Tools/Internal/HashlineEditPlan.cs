using System;
using System.Collections.Generic;
using System.Text;
using Pomp.Adapters;

namespace Pomp.Tools
{
    /// <summary>Immutable line edit; fresh mutable adapter DTOs are created only when applying an approved plan.</summary>
    internal sealed class HashlineLineChange
    {
        public HashlineLineChange(int startLine, int endLine, IReadOnlyList<string> newLines)
        {
            StartLine = startLine;
            EndLine = endLine;
            NewLines = newLines;
        }

        public int StartLine { get; }
        public int EndLine { get; }
        public IReadOnlyList<string> NewLines { get; }

        public TextLineEdit ToAdapterEdit() => new TextLineEdit
        {
            StartLine = StartLine,
            EndLine = EndLine,
            NewLines = new List<string>(NewLines),
        };
    }

    internal sealed class HashlineFilePlan
    {
        private HashlineFilePlan(string path, string displayPath, string expectedHash, string originalText, string resultText, IReadOnlyList<HashlineLineChange> changes)
        {
            Path = path;
            DisplayPath = displayPath;
            ExpectedHash = expectedHash;
            OriginalText = originalText;
            ResultText = resultText;
            ResultHash = TextUtil.ComputeContentHash(resultText);
            Changes = changes;
        }

        public string Path { get; }
        public string DisplayPath { get; }
        public string ExpectedHash { get; }
        public string OriginalText { get; }
        public string ResultText { get; }
        public string ResultHash { get; }
        public IReadOnlyList<HashlineLineChange> Changes { get; }

        public BufferEditRequest ToAdapterRequest()
        {
            var edits = new List<TextLineEdit>();
            foreach (var change in Changes)
            {
                edits.Add(change.ToAdapterEdit());
            }

            return new BufferEditRequest
            {
                Path = Path,
                ExpectedContentHash = ExpectedHash,
                ExpectedResultContentHash = ResultHash,
                Edits = edits,
                UndoDescription = "Pomp hashline edit",
            };
        }

        /// <summary>Validates against the shown snapshot and predicts the adapter's line-edit/newline semantics.</summary>
        public static HashlineFilePlan Create(HashlineSection section, BufferSnapshotEntry entry, string currentText, string fullPath, string displayPath)
        {
            var original = TextUtil.NormalizeLineEndings(currentText);
            var hash = TextUtil.ComputeContentHash(original);
            if (!string.Equals(hash, entry.ContentHash, StringComparison.Ordinal))
            {
                throw new ToolException("'" + displayPath + "' is stale: its full content hash changed. Read the file again before editing.");
            }

            var lines = TextUtil.SplitLines(original);
            if (lines.Count != entry.Lines.Count)
            {
                throw new ToolException("The stored snapshot for '" + displayPath + "' is inconsistent; read the file again.");
            }

            for (var i = 0; i < lines.Count; i++)
            {
                if (!string.Equals(lines[i], entry.Lines[i], StringComparison.Ordinal))
                {
                    throw new ToolException("The stored snapshot for '" + displayPath + "' is inconsistent; read the file again.");
                }
            }

            var changes = new List<HashlineLineChange>();
            var previousStart = 0;
            var previousEnd = 0;
            var previousInsertion = false;
            foreach (var operation in section.Operations)
            {
                var start = operation.Start;
                var end = operation.End;
                var insertion = operation.Kind == HashlineOperationKind.InsertBefore
                    || operation.Kind == HashlineOperationKind.InsertAfter || operation.Kind == HashlineOperationKind.Append;
                if (insertion)
                {
                    if (operation.Kind == HashlineOperationKind.Append)
                    {
                        RequireSeen(entry, lines.Count, lines.Count, displayPath, operation.PatchLine, lines.Count == 0);
                        start = lines.Count + 1;
                    }
                    else if (lines.Count == 0 && operation.Kind == HashlineOperationKind.InsertBefore && start == 1)
                    {
                        start = 1;
                    }
                    else
                    {
                        RequireSeen(entry, start, start, displayPath, operation.PatchLine);
                        if (operation.Kind == HashlineOperationKind.InsertAfter)
                        {
                            start++;
                        }
                    }

                    end = start - 1;
                }
                else
                {
                    RequireSeen(entry, start, end, displayPath, operation.PatchLine);
                }

                if (start < previousStart || start <= previousEnd || (previousInsertion && insertion && start == previousStart))
                {
                    throw new ToolException("Operations for '" + displayPath + "' must be ordered and non-overlapping in ORIGINAL snapshot coordinates (patch row " + operation.PatchLine + "). Duplicate insertion positions are not allowed.");
                }

                // Defensive copy: neither the parse result nor a later adapter call can change the approved edits.
                changes.Add(new HashlineLineChange(start, end, new List<string>(operation.NewLines).AsReadOnly()));
                previousStart = start;
                previousEnd = end;
                previousInsertion = insertion;
            }

            var result = PredictText(original, lines, changes);
            if (string.Equals(result, original, StringComparison.Ordinal))
            {
                throw new ToolException("The patch makes no text changes to '" + displayPath + "'.");
            }

            return new HashlineFilePlan(fullPath, displayPath, hash, original, result, changes.AsReadOnly());
        }

        private static void RequireSeen(BufferSnapshotEntry entry, int start, int end, string displayPath, int patchLine, bool emptyAppend = false)
        {
            if (emptyAppend)
            {
                return;
            }

            if (start < 1 || end < start || end > entry.Lines.Count)
            {
                throw new ToolException("An anchor for '" + displayPath + "' is outside its " + entry.Lines.Count + " original lines (patch row " + patchLine + ").");
            }

            if (!entry.HasSeen(start, end))
            {
                throw new ToolException("Lines " + start + ".." + end + " of '" + displayPath + "' were not fully shown. Use read_buffer to read the anchor/range before editing (patch row " + patchLine + ").");
            }

            // An unpaired UTF-16 surrogate reaches the model as U+FFFD, so such a line was not really shown as it is.
            for (var line = start; line <= end; line++)
            {
                if (HasUnpairedSurrogate(entry.Lines[line - 1]))
                {
                    throw new ToolException("Line " + line + " of '" + displayPath + "' contains an invalid (unpaired) UTF-16 character that cannot be shown exactly; edit it in Visual Studio (patch row " + patchLine + ").");
                }
            }
        }

        private static bool HasUnpairedSurrogate(string text)
        {
            for (var i = 0; i < text.Length; i++)
            {
                if (char.IsHighSurrogate(text[i]) && i + 1 < text.Length && char.IsLowSurrogate(text[i + 1]))
                {
                    i++;
                }
                else if (char.IsSurrogate(text[i]))
                {
                    return true;
                }
            }

            return false;
        }

        private static string PredictText(string original, IReadOnlyList<string> lines, IReadOnlyList<HashlineLineChange> changes)
        {
            var offsets = new int[lines.Count];
            var offset = 0;
            for (var i = 0; i < lines.Count; i++)
            {
                offsets[i] = offset;
                offset += lines[i].Length + 1;
            }

            var result = new StringBuilder();
            var cursor = 0;
            var previousEnd = -1;
            var endsWithBreak = original.Length > 0 && TextLineBreaks.IsBreak(original[original.Length - 1]);
            foreach (var change in changes)
            {
                var hasNewLines = change.NewLines.Count > 0;
                var replacement = string.Join("\n", change.NewLines);
                int start;
                int end;
                if (change.EndLine == change.StartLine - 1)
                {
                    start = change.StartLine <= lines.Count ? offsets[change.StartLine - 1] : original.Length;
                    end = start;
                    replacement = change.StartLine <= lines.Count || endsWithBreak || lines.Count == 0
                        ? replacement + "\n" : "\n" + replacement;
                }
                else
                {
                    start = offsets[change.StartLine - 1];
                    end = change.EndLine == lines.Count ? original.Length : offsets[change.EndLine];
                    var lastHasBreak = end > start && TextLineBreaks.IsBreak(original[end - 1]);
                    if (hasNewLines && lastHasBreak)
                    {
                        replacement += "\n";
                    }
                    else if (!hasNewLines && !lastHasBreak && change.StartLine > 1 && start - 1 >= previousEnd)
                    {
                        start--;
                    }
                }

                if (start < cursor)
                {
                    throw new ToolException("The edits overlap at a newline boundary; separate them into different calls.");
                }

                result.Append(original, cursor, start - cursor).Append(replacement);
                cursor = end;
                previousEnd = end;
            }

            result.Append(original, cursor, original.Length - cursor);
            if (result.Length > BufferSnapshotStore.DefaultMaxCharactersPerSnapshot)
            {
                throw new ToolException("The edited file exceeds the supported snapshot size; use a smaller edit.");
            }

            return result.ToString();
        }
    }
}
