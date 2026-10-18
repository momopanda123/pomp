using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using Pomp.Hosting;

namespace Pomp.Features
{
    /// <summary>
    /// The git unified diff omp keeps for an isolated task (docs/architecture.md §8.11): read strictly and applied exactly.
    /// Only text additions, modifications and deletions. Renames, copies and binary changes are refused; mode-only entries carry
    /// nothing and are dropped. A hunk applies only where its context and removed lines match the current text exactly (no fuzz,
    /// no offsets), and line endings are kept as they are in the file.
    /// </summary>
    internal static class GitPatch
    {
        internal const int MaxBytes = 2 * 1024 * 1024;
        private const int MaxPathChars = 400;
        private static readonly Regex HunkHeader = new Regex(@"\A@@ -(\d+)(?:,(\d+))? \+(\d+)(?:,(\d+))? @@", RegexOptions.CultureInvariant);

        internal enum ChangeKind { Modify, Create, Delete }

        internal sealed class FileChange
        {
            public string Path { get; set; } = "";
            public ChangeKind Kind { get; set; }
            public List<Hunk> Hunks { get; } = new List<Hunk>();
        }

        internal sealed class Hunk
        {
            public int OldStart, OldCount, NewStart, NewCount;
            public List<KeyValuePair<char, string>> Lines { get; } = new List<KeyValuePair<char, string>>();
            public bool OldNoNewline, NewNoNewline;
        }

        /// <summary>The file changes, in patch order; repository-relative paths with '/'. Throws <see cref="InvalidDataException"/>.</summary>
        internal static IReadOnlyList<FileChange> Parse(string text)
        {
            if (Encoding.UTF8.GetByteCount(text) > MaxBytes) throw Bad("패치가 너무 커요.", "The patch is too large.");
            var lines = text.Split('\n');
            var changes = new List<FileChange>();
            var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var i = 0;
            while (i < lines.Length)
            {
                var header = lines[i].TrimEnd('\r');
                if (header.Length == 0 && i == lines.Length - 1) break;
                if (!header.StartsWith("diff --git ", StringComparison.Ordinal)) throw Bad("패치 형식을 알 수 없어요(" + (i + 1) + "번째 줄).", "Unrecognized patch line " + (i + 1) + ".");
                i++;
                var kind = ChangeKind.Modify; string? oldPath = null, newPath = null; var sawPaths = false;
                // Extended header lines up to the first hunk or the next file.
                for (; i < lines.Length; i++)
                {
                    var line = lines[i].TrimEnd('\r');
                    if (line.StartsWith("diff --git ", StringComparison.Ordinal) || line.StartsWith("@@", StringComparison.Ordinal) || (i == lines.Length - 1 && line.Length == 0)) break;
                    if (line.StartsWith("new file mode ", StringComparison.Ordinal)) kind = ChangeKind.Create;
                    else if (line.StartsWith("deleted file mode ", StringComparison.Ordinal)) kind = ChangeKind.Delete;
                    else if (line.StartsWith("index ", StringComparison.Ordinal) || line.StartsWith("old mode ", StringComparison.Ordinal) || line.StartsWith("new mode ", StringComparison.Ordinal)) { }
                    else if (line.StartsWith("--- ", StringComparison.Ordinal)) { oldPath = PathOf(line.Substring(4), "a/"); sawPaths = true; }
                    else if (line.StartsWith("+++ ", StringComparison.Ordinal)) { newPath = PathOf(line.Substring(4), "b/"); sawPaths = true; }
                    else if (line.StartsWith("Binary files ", StringComparison.Ordinal) || line.StartsWith("GIT binary patch", StringComparison.Ordinal))
                        throw Bad("바이너리 파일 변경은 검토에 올릴 수 없어요.", "Binary file changes cannot be reviewed.");
                    else if (line.StartsWith("rename ", StringComparison.Ordinal) || line.StartsWith("copy ", StringComparison.Ordinal) || line.StartsWith("similarity index", StringComparison.Ordinal) || line.StartsWith("dissimilarity index", StringComparison.Ordinal))
                        throw Bad("파일 이름 바꾸기·복사는 검토에 올릴 수 없어요.", "Renamed or copied files cannot be reviewed.");
                    else throw Bad("패치 형식을 알 수 없어요(" + (i + 1) + "번째 줄).", "Unrecognized patch line " + (i + 1) + ".");
                }

                var change = new FileChange { Kind = kind };
                while (i < lines.Length && lines[i].StartsWith("@@", StringComparison.Ordinal)) change.Hunks.Add(ReadHunk(lines, ref i));
                if (!sawPaths && change.Hunks.Count == 0) continue; // A mode-only change: nothing to review.
                if (change.Hunks.Count == 0) throw Bad("변경 내용이 없는 파일 항목이 있어요.", "A file entry has no changes.");
                if (kind == ChangeKind.Create ? oldPath != null || newPath == null : kind == ChangeKind.Delete ? oldPath == null || newPath != null : oldPath == null || newPath == null || !string.Equals(oldPath, newPath, StringComparison.Ordinal))
                    throw Bad("파일 경로가 맞지 않는 항목이 있어요.", "A file entry has inconsistent paths.");
                change.Path = newPath ?? oldPath!;
                if (!paths.Add(change.Path)) throw Bad("같은 파일이 두 번 나와요: " + change.Path, "A file appears twice: " + change.Path);
                changes.Add(change);
            }

            return changes;
        }

        /// <summary>
        /// The text after the change. <paramref name="before"/> is "" for a file that does not exist yet. Lines are compared without
        /// their line ending (git may write LF where the editor buffer has CRLF), and the result uses the file's dominant ending.
        /// </summary>
        internal static string Apply(FileChange change, string before)
        {
            if (change.Kind == ChangeKind.Create && before.Length != 0) throw Bad("새로 만들 파일이 이미 있어요: " + change.Path, "The file to create already exists: " + change.Path);
            var ending = before.Length > 0
                ? (before.Contains("\r\n") ? "\r\n" : "\n")
                : (change.Hunks.SelectMany(h => h.Lines).Any(l => l.Value.EndsWith("\r", StringComparison.Ordinal)) ? "\r\n" : "\n");
            var old = before.Split('\n').Select(l => l.TrimEnd('\r')).ToList();
            var endsWithNewline = old.Count > 0 && old[old.Count - 1].Length == 0;
            if (endsWithNewline || before.Length == 0) old.RemoveAt(old.Count - 1);
            var result = new List<string>(); var cursor = 0; Hunk? last = null;
            foreach (var hunk in change.Hunks)
            {
                var start = hunk.OldCount == 0 ? hunk.OldStart : hunk.OldStart - 1;
                if (start < cursor || start > old.Count) throw Mismatch(change);
                result.AddRange(old.Skip(cursor).Take(start - cursor));
                var position = start;
                foreach (var line in hunk.Lines)
                {
                    var value = line.Value.TrimEnd('\r');
                    if (line.Key == '+') { result.Add(value); continue; }
                    if (position >= old.Count || !string.Equals(old[position], value, StringComparison.Ordinal)) throw Mismatch(change);
                    if (line.Key == ' ') result.Add(value);
                    position++;
                }

                cursor = position; last = hunk;
            }

            var atEnd = cursor == old.Count;
            result.AddRange(old.Skip(cursor));
            bool newline;
            if (last != null && atEnd)
            {
                // The last hunk reaches the end: its markers state both sides' final newline.
                if (old.Count > 0 && last.OldNoNewline == endsWithNewline) throw Mismatch(change);
                newline = !last.NewNoNewline;
            }
            else newline = endsWithNewline;

            if (change.Kind == ChangeKind.Delete && result.Count != 0) throw Mismatch(change);
            return result.Count == 0 ? "" : string.Join(ending, result) + (newline ? ending : "");
        }

        private static Hunk ReadHunk(string[] lines, ref int i)
        {
            var match = HunkHeader.Match(lines[i].TrimEnd('\r'));
            if (!match.Success) throw Bad("변경 묶음 머리글이 올바르지 않아요(" + (i + 1) + "번째 줄).", "Invalid hunk header at line " + (i + 1) + ".");
            static int Number(Group group, int fallback) => group.Success ? int.Parse(group.Value, System.Globalization.CultureInfo.InvariantCulture) : fallback;
            var hunk = new Hunk { OldStart = Number(match.Groups[1], 0), OldCount = Number(match.Groups[2], 1), NewStart = Number(match.Groups[3], 0), NewCount = Number(match.Groups[4], 1) };
            i++;
            int oldLeft = hunk.OldCount, newLeft = hunk.NewCount; var previous = '\0';
            // The empty string after the patch's final newline is the end of the text, not an empty context line.
            while (i < lines.Length && !(i == lines.Length - 1 && lines[i].Length == 0) && (oldLeft > 0 || newLeft > 0 || lines[i].StartsWith("\\", StringComparison.Ordinal)))
            {
                var line = lines[i];
                if (line.StartsWith("\\", StringComparison.Ordinal))
                {
                    // "\ No newline at end of file" ends the side(s) of the line before it.
                    if (previous == '\0') throw Bad("변경 묶음 형식이 올바르지 않아요.", "Malformed hunk.");
                    if (previous != '+') hunk.OldNoNewline = true;
                    if (previous != '-') hunk.NewNoNewline = true;
                    i++; continue;
                }

                var op = line.Length == 0 ? ' ' : line[0];
                var body = line.Length == 0 ? "" : line.Substring(1);
                if (op == ' ') { oldLeft--; newLeft--; }
                else if (op == '-') oldLeft--;
                else if (op == '+') newLeft--;
                else throw Bad("변경 묶음 형식이 올바르지 않아요(" + (i + 1) + "번째 줄).", "Malformed hunk line " + (i + 1) + ".");
                if (oldLeft < 0 || newLeft < 0) throw Bad("변경 묶음의 줄 수가 머리글과 달라요.", "Hunk line counts do not match its header.");
                hunk.Lines.Add(new KeyValuePair<char, string>(op, body)); previous = op; i++;
            }

            if (oldLeft != 0 || newLeft != 0) throw Bad("변경 묶음이 끝나기 전에 패치가 끝났어요.", "The patch ends inside a hunk.");
            return hunk;
        }

        /// <summary>A "--- a/x" or "+++ b/x" path: null for /dev/null; quoted C-style names are decoded.</summary>
        private static string? PathOf(string raw, string prefix)
        {
            var value = raw.TrimEnd('\t');
            if (value == "/dev/null") return null;
            if (value.StartsWith("\"", StringComparison.Ordinal)) value = Unquote(value);
            if (!value.StartsWith(prefix, StringComparison.Ordinal)) throw Bad("패치의 파일 경로 형식이 올바르지 않아요.", "Invalid file path in the patch.");
            value = value.Substring(prefix.Length);
            var segments = value.Split('/');
            if (value.Length == 0 || value.Length > MaxPathChars || value.Any(char.IsControl) || value.IndexOf('\\') >= 0 || value.IndexOf(':') >= 0
                || segments.Any(s => s.Length == 0 || s == "." || s == ".."))
                throw Bad("작업 영역 밖이나 올바르지 않은 경로가 있어요: " + value, "The patch names an invalid path: " + value);
            return value;
        }

        private static string Unquote(string quoted)
        {
            if (quoted.Length < 2 || !quoted.EndsWith("\"", StringComparison.Ordinal)) throw Bad("패치의 파일 경로 형식이 올바르지 않아요.", "Invalid file path in the patch.");
            var bytes = new List<byte>();
            for (var i = 1; i < quoted.Length - 1; i++)
            {
                var c = quoted[i];
                if (c != '\\') { bytes.AddRange(Encoding.UTF8.GetBytes(c.ToString())); continue; }
                if (++i >= quoted.Length - 1) throw Bad("패치의 파일 경로 형식이 올바르지 않아요.", "Invalid file path in the patch.");
                var e = quoted[i];
                if (e >= '0' && e <= '7' && i + 2 < quoted.Length - 1)
                {
                    bytes.Add(Convert.ToByte(quoted.Substring(i, 3), 8)); i += 2; continue;
                }
                bytes.Add(e switch { 'n' => (byte)'\n', 't' => (byte)'\t', 'r' => (byte)'\r', '"' => (byte)'"', '\\' => (byte)'\\', 'a' => 7, 'b' => 8, 'f' => 12, 'v' => 11, _ => throw Bad("패치의 파일 경로 형식이 올바르지 않아요.", "Invalid file path in the patch.") });
            }

            try { return new UTF8Encoding(false, true).GetString(bytes.ToArray()); }
            catch (DecoderFallbackException) { throw Bad("패치의 파일 경로 형식이 올바르지 않아요.", "Invalid file path in the patch."); }
        }

        private static InvalidDataException Mismatch(FileChange change) =>
            Bad("현재 파일이 패치와 맞지 않아요: " + change.Path, "The current file does not match the patch: " + change.Path);

        private static InvalidDataException Bad(string ko, string en) => new InvalidDataException(PompLanguage.Pick(ko, en));
    }
}
