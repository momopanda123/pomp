using System;
using System.Collections.Generic;
using System.Globalization;

namespace Pomp.Tools
{
    internal enum HashlineOperationKind
    {
        Replace,
        InsertBefore,
        InsertAfter,
        Append,
        Delete,
    }

    internal sealed class HashlineOperation
    {
        public HashlineOperation(HashlineOperationKind kind, int start, int end, IReadOnlyList<string> newLines, int patchLine)
        {
            Kind = kind;
            Start = start;
            End = end;
            NewLines = newLines;
            PatchLine = patchLine;
        }

        public HashlineOperationKind Kind { get; }
        public int Start { get; }
        public int End { get; }
        public IReadOnlyList<string> NewLines { get; }
        public int PatchLine { get; }
    }

    internal sealed class HashlineSection
    {
        public HashlineSection(string path, string tag, IReadOnlyList<HashlineOperation> operations)
        {
            Path = path;
            Tag = tag;
            Operations = operations;
        }

        public string Path { get; }
        public string Tag { get; }
        public IReadOnlyList<HashlineOperation> Operations { get; }
    }

    /// <summary>Bounded parser for the host's PUT/CUT subset; body rows are never trimmed or interpreted as commands.</summary>
    internal static class HashlinePatch
    {
        public const int MaxInputCharacters = 200000;
        public const int MaxPatchLines = 10000;
        public const int MaxFiles = 16;
        public const int MaxOperations = 256;
        private const int MaxPathCharacters = 4096;

        public static IReadOnlyList<HashlineSection> Parse(string input)
        {
            if (input.Length > MaxInputCharacters)
            {
                throw new ToolException("The patch exceeds " + MaxInputCharacters + " characters; split it into smaller calls.");
            }

            if (string.IsNullOrWhiteSpace(input))
            {
                throw new ToolException("The hashline patch is empty.");
            }

            if (input.IndexOf('\0') >= 0)
            {
                throw new ToolException("The patch contains a NUL character; only text edits are supported.");
            }

            var normalized = Pomp.Adapters.TextLineBreaks.NormalizePatch(input);
            var lineCount = 1;
            foreach (var c in normalized)
            {
                if (c == '\n' && ++lineCount > MaxPatchLines)
                {
                    throw new ToolException("The patch exceeds " + MaxPatchLines + " rows; split it into smaller calls.");
                }
            }

            var rows = normalized.Split('\n');
            var sections = new List<HashlineSection>();
            var index = 0;
            SkipBlank(rows, ref index);
            var wrapped = index < rows.Length && Trim(rows[index]) == "*** Begin Patch";
            if (wrapped)
            {
                index++;
            }

            var operationCount = 0;
            var ended = false;
            while (index < rows.Length)
            {
                SkipBlank(rows, ref index);
                if (index == rows.Length)
                {
                    break;
                }

                var line = Trim(rows[index]);
                if (line == "*** End Patch")
                {
                    if (!wrapped)
                    {
                        throw Invalid(index, "An End Patch marker requires a Begin Patch marker.");
                    }

                    index++;
                    ended = true;
                    SkipBlank(rows, ref index);
                    if (index != rows.Length)
                    {
                        throw Invalid(index, "Unexpected text after End Patch.");
                    }

                    break;
                }

                if (!TryHeader(line, out var path, out var tag))
                {
                    throw Invalid(index, "Expected a [path#TAG] header with a four-digit hexadecimal TAG.");
                }

                if (sections.Count == MaxFiles)
                {
                    throw Invalid(index, "A patch can target at most " + MaxFiles + " files.");
                }

                if (path.Length > MaxPathCharacters)
                {
                    throw Invalid(index, "The header path is too long.");
                }

                index++;
                var operations = new List<HashlineOperation>();
                while (index < rows.Length)
                {
                    SkipBlank(rows, ref index);
                    if (index == rows.Length)
                    {
                        break;
                    }

                    line = Trim(rows[index]);
                    if (line.StartsWith("[", StringComparison.Ordinal) || line == "*** End Patch")
                    {
                        break;
                    }

                    if (++operationCount > MaxOperations)
                    {
                        throw Invalid(index, "A patch can contain at most " + MaxOperations + " operations.");
                    }

                    var patchLine = index + 1;
                    var kind = ParseCommand(line, index, out var start, out var end);
                    index++;
                    var body = new List<string>();
                    if (kind != HashlineOperationKind.Delete)
                    {
                        while (index < rows.Length && rows[index].StartsWith("+", StringComparison.Ordinal))
                        {
                            body.Add(rows[index].Substring(1));
                            index++;
                        }

                        if (body.Count == 0)
                        {
                            throw Invalid(patchLine - 1, "PUT requires at least one +text row (use a lone + for an empty line).");
                        }
                    }

                    operations.Add(new HashlineOperation(kind, start, end, body.AsReadOnly(), patchLine));
                }

                if (operations.Count == 0)
                {
                    throw Invalid(index, "Every file header requires at least one PUT or CUT operation.");
                }

                sections.Add(new HashlineSection(path, tag, operations.AsReadOnly()));
            }

            if (sections.Count == 0)
            {
                throw new ToolException("The patch must contain at least one [path#TAG] section.");
            }

            if (wrapped && !ended)
            {
                throw new ToolException("The patch is missing its *** End Patch marker.");
            }

            return sections.AsReadOnly();
        }

        internal static bool TryHeader(string line, out string path, out string tag)
        {
            path = string.Empty;
            tag = string.Empty;
            if (line.Length < 8 || line[0] != '[' || line[line.Length - 1] != ']')
            {
                return false;
            }

            var separator = line.Length - 6;
            if (line[separator] != '#')
            {
                return false;
            }

            for (var i = separator + 1; i < line.Length - 1; i++)
            {
                var c = line[i];
                if (!((c >= '0' && c <= '9') || (c >= 'a' && c <= 'f') || (c >= 'A' && c <= 'F')))
                {
                    return false;
                }
            }

            path = line.Substring(1, separator - 1).Trim(' ', '\t');
            tag = line.Substring(separator + 1, 4).ToUpperInvariant();
            return path.Length > 0;
        }

        private static HashlineOperationKind ParseCommand(string line, int index, out int start, out int end)
        {
            start = 0;
            end = 0;
            string anchor;
            var deletion = line.StartsWith("CUT ", StringComparison.Ordinal);
            if (deletion)
            {
                anchor = line.Substring(4);
            }
            else if (line.StartsWith("PUT ", StringComparison.Ordinal) && line.EndsWith(":", StringComparison.Ordinal))
            {
                anchor = line.Substring(4, line.Length - 5);
                if (anchor == ">$")
                {
                    return HashlineOperationKind.Append;
                }

                if (anchor.StartsWith("<", StringComparison.Ordinal) || anchor.StartsWith(">", StringComparison.Ordinal))
                {
                    start = ReadNumber(anchor.Substring(1), index);
                    end = start;
                    return anchor[0] == '<' ? HashlineOperationKind.InsertBefore : HashlineOperationKind.InsertAfter;
                }
            }
            else
            {
                throw Invalid(index, "Unsupported command. Use PUT N.=M:, PUT <N:, PUT >N:, PUT >$:, or CUT N.=M; block anchors, registers, REM and MV are not supported.");
            }

            var separator = anchor.IndexOf(".=", StringComparison.Ordinal);
            if (separator < 1 || separator + 2 == anchor.Length)
            {
                throw Invalid(index, "Expected an inclusive original-line range N.=M.");
            }

            start = ReadNumber(anchor.Substring(0, separator), index);
            end = ReadNumber(anchor.Substring(separator + 2), index);
            if (end < start)
            {
                throw Invalid(index, "The range end must not precede its start.");
            }

            return deletion ? HashlineOperationKind.Delete : HashlineOperationKind.Replace;
        }

        private static int ReadNumber(string text, int index)
        {
            if (text.Length == 0 || text.Length > 10)
            {
                throw Invalid(index, "Line numbers must be positive 32-bit decimal integers.");
            }

            var value = 0;
            foreach (var c in text)
            {
                if (c < '0' || c > '9' || value > (int.MaxValue - (c - '0')) / 10)
                {
                    throw Invalid(index, "Line numbers must be positive 32-bit decimal integers.");
                }

                value = value * 10 + c - '0';
            }

            if (value == 0)
            {
                throw Invalid(index, "Line numbers are 1-based; zero is not an anchor.");
            }

            return value;
        }

        private static void SkipBlank(string[] rows, ref int index)
        {
            while (index < rows.Length && Trim(rows[index]).Length == 0)
            {
                index++;
            }
        }

        private static string Trim(string text) => text.Trim(' ', '\t');

        private static ToolException Invalid(int index, string message) =>
            new ToolException("Invalid patch at row " + (index + 1).ToString(CultureInfo.InvariantCulture) + ": " + message);
    }
}
