using System;
using System.Collections.Generic;

namespace Pomp.Tools
{
    /// <summary>
    /// The lines of a source file joined by '\n' with comments blanked and literal contents blanked (a literal keeps its delimiters),
    /// so structure can be read with plain text matching. Offsets and line lengths are those of the source.
    /// </summary>
    internal sealed class SourceMask
    {
        private SourceMask(string text, int[] lineStarts, bool[] continued, int[] parentheses, int[] nesting)
        {
            Text = text; LineStarts = lineStarts; Continued = continued; Parentheses = parentheses; Nesting = nesting;
        }

        public string Text { get; }
        public int[] LineStarts { get; }
        /// <summary>The line starts inside a literal or comment, or the line before ends with a backslash.</summary>
        public bool[] Continued { get; }
        /// <summary>Depth of '(' and '[' at the line's start.</summary>
        public int[] Parentheses { get; }
        /// <summary>Depth of '(', '[' and '{' at the line's start.</summary>
        public int[] Nesting { get; }
        public int LineCount => LineStarts.Length;

        public int LineEnd(int line) => line + 1 < LineStarts.Length ? LineStarts[line + 1] - 1 : Text.Length;

        public int LineOf(int position)
        {
            var index = Array.BinarySearch(LineStarts, position);
            return index >= 0 ? index : ~index - 1;
        }

        public static SourceMask Create(IReadOnlyList<string> lines, ScannedLanguage language)
        {
            var starts = new int[lines.Count];
            var length = 0;
            for (var i = 0; i < lines.Count; i++) { starts[i] = length; length += lines[i].Length + 1; }
            var source = string.Join("\n", lines);
            var masker = new Masker(source, language);
            masker.Code(0, '\0', 0);
            var text = new string(masker.Buffer);

            var continued = new bool[lines.Count];
            var parentheses = new int[lines.Count];
            var nesting = new int[lines.Count];
            int parens = 0, braces = 0;
            for (var line = 0; line < lines.Count; line++)
            {
                parentheses[line] = parens;
                nesting[line] = parens + braces;
                if (line > 0 && (masker.BlankNewline[starts[line] - 1] || EndsWithBackslash(text, starts[line - 1], starts[line] - 1))) continued[line] = true;
                var end = line + 1 < lines.Count ? starts[line + 1] - 1 : text.Length;
                for (var i = starts[line]; i < end; i++)
                {
                    switch (text[i])
                    {
                        case '(': case '[': parens++; break;
                        case ')': case ']': if (parens > 0) parens--; break;
                        case '{': braces++; break;
                        case '}': if (braces > 0) braces--; break;
                    }
                }
            }

            return new SourceMask(text, starts, continued, parentheses, nesting);
        }

        private static bool EndsWithBackslash(string text, int start, int end)
        {
            while (end > start && char.IsWhiteSpace(text[end - 1])) end--;
            return end > start && text[end - 1] == '\\';
        }

        private sealed class Masker
        {
            private const int MaxDepth = 16;
            private readonly string _source;
            private readonly ScannedLanguage _language;
            private readonly List<string> _heredocs = new List<string>();

            public Masker(string source, ScannedLanguage language)
            {
                _source = source;
                _language = language;
                Buffer = source.ToCharArray();
                BlankNewline = new bool[source.Length];
            }

            public char[] Buffer { get; }
            /// <summary>The '\n' at this offset lies inside a blanked literal or comment.</summary>
            public bool[] BlankNewline { get; }
            private int Length => _source.Length;
            private char At(int i) => i >= 0 && i < _source.Length ? _source[i] : '\0';

            private bool StartsWith(int i, string value) => i + value.Length <= _source.Length && string.CompareOrdinal(_source, i, value, 0, value.Length) == 0;

            private int LineEnd(int i)
            {
                var end = _source.IndexOf('\n', i);
                return end < 0 ? Length : end;
            }

            private void Blank(int from, int to)
            {
                for (var i = Math.Max(0, from); i < to && i < Length; i++)
                {
                    if (Buffer[i] == '\n') BlankNewline[i] = true;
                    else Buffer[i] = ' ';
                }
            }

            private static bool IsIdentifierPart(char c) => char.IsLetterOrDigit(c) || c == '_';

            /// <summary>Code from <paramref name="i"/> to the end, or to the unmatched <paramref name="closer"/> (whose offset it returns).</summary>
            public int Code(int i, char closer, int depth)
            {
                var opener = closer == '}' ? '{' : closer == ')' ? '(' : '\0';
                var nest = 0;
                while (i < Length)
                {
                    var c = _source[i];
                    if (c == '\n') { i = NewLine(i); continue; }
                    var skipped = depth > MaxDepth ? Length : Skip(i, depth);
                    if (skipped >= 0) { i = skipped; continue; }
                    if (closer != '\0')
                    {
                        if (c == opener) nest++;
                        else if (c == closer) { if (nest == 0) return i; nest--; }
                    }

                    i++;
                }

                return Length;
            }

            /// <summary>Past the line break at <paramref name="i"/> and the bodies of the here-documents opened on its line.</summary>
            private int NewLine(int i)
            {
                if (_heredocs.Count == 0) return i + 1;
                var k = i + 1;
                foreach (var word in _heredocs)
                {
                    while (k < Length)
                    {
                        var end = LineEnd(k);
                        var text = _source.Substring(k, end - k).Trim();
                        k = end;
                        if (text.StartsWith(word, StringComparison.Ordinal) && (text.Length == word.Length || !IsIdentifierPart(text[word.Length]))) break;
                        if (k < Length) k++;
                    }
                }

                _heredocs.Clear();
                Blank(i + 1, k);
                return Math.Max(k, i + 1);
            }

            /// <summary>The offset past a comment or literal starting at <paramref name="i"/> (now blanked); -1 when none starts there.</summary>
            private int Skip(int i, int depth)
            {
                var c = _source[i];
                var previous = At(i - 1);
                var lineStart = i == 0 || previous == '\n';
                var language = _language;

                if (language.CodeEscape != '\0' && c == language.CodeEscape)
                {
                    if (At(i + 1) == '\n' || i + 1 >= Length) return i + 1;
                    Blank(i, i + 2);
                    return i + 2;
                }

                if (lineStart && language.RubyBlockComments && StartsWith(i, "=begin")) return BlankToLine(i, "=end");
                if (lineStart && language.PerlPod && c == '=' && char.IsLetter(At(i + 1))) return BlankToLine(i, "=cut");
                if (language.LuaLongBrackets && StartsWith(i, "--["))
                {
                    var close = LongBracketClose(i + 2);
                    if (close != null) return BlankThrough(i, i + 2, close, keepDelimiters: false);
                }

                var comments = language.BlockComments;
                for (var b = 0; b + 1 < comments.Length; b += 2)
                {
                    if (!StartsWith(i, comments[b]) || (comments[b] == "(*" && At(i + 2) == ')')) continue;
                    var end = BlockCommentEnd(i + comments[b].Length, comments[b], comments[b + 1]);
                    Blank(i, end);
                    return end;
                }

                foreach (var marker in language.LineComments)
                {
                    if (!StartsWith(i, marker)) continue;
                    var end = LineEnd(i);
                    Blank(i, end);
                    return end;
                }

                if (c == '#' && IsHashComment(i))
                {
                    var end = LineEnd(i);
                    if (language.Hash == HashComment.Directive)
                    {
                        while (end < Length && EndsWithBackslash(_source, i, end)) end = LineEnd(end + 1);
                    }

                    Blank(i, end);
                    return end;
                }

                if (language.Heredoc && c == '<' && StartsWith(i, "<<")) { var end = Heredoc(i); if (end >= 0) return end; }
                if (language.PowerShellHereStrings && c == '@' && (At(i + 1) == '"' || At(i + 1) == '\'') && RestOfLineBlank(i + 2))
                {
                    var quote = At(i + 1);
                    var k = LineEnd(i);
                    while (k < Length)
                    {
                        var start = k + 1;
                        if (At(start) == quote && At(start + 1) == '@') { Blank(i + 2, start); return start + 2; }
                        k = LineEnd(start);
                    }

                    Blank(i + 2, Length);
                    return Length;
                }

                if (language.DollarQuotes && c == '$')
                {
                    var k = i + 1;
                    while (k < Length && (char.IsLetter(_source[k]) || _source[k] == '_')) k++;
                    if (At(k) == '$' && !IsIdentifierPart(previous))
                    {
                        var tag = _source.Substring(i, k + 1 - i);
                        var close = _source.IndexOf(tag, k + 1, StringComparison.Ordinal);
                        var end = close < 0 ? Length : close;
                        Blank(k + 1, end);
                        return close < 0 ? Length : close + tag.Length;
                    }
                }

                if (language.SqlBrackets && c == '[')
                {
                    var close = _source.IndexOf(']', i + 1);
                    var line = LineEnd(i);
                    if (close > 0 && close < line) { Blank(i + 1, close); return close + 1; }
                }

                if (language.LuaLongBrackets && c == '[')
                {
                    var close = LongBracketClose(i);
                    if (close != null) return BlankThrough(i, i, close, keepDelimiters: true);
                }

                if (language.RustRaw && !IsIdentifierPart(previous) && (c == 'r' || ((c == 'b' || c == 'c') && At(i + 1) == 'r')))
                {
                    var k = c == 'r' ? i + 1 : i + 2;
                    var hashes = 0;
                    while (At(k + hashes) == '#') hashes++;
                    if (At(k + hashes) == '"')
                    {
                        var close = "\"" + new string('#', hashes);
                        return BlankUntil(k + hashes + 1, close);
                    }
                }

                if (language.SwiftRaw && c == '#')
                {
                    var hashes = 0;
                    while (At(i + hashes) == '#') hashes++;
                    if (At(i + hashes) == '"')
                    {
                        var triple = StartsWith(i + hashes, "\"\"\"");
                        var open = i + hashes + (triple ? 3 : 1);
                        return BlankUntil(open, (triple ? "\"\"\"" : "\"") + new string('#', hashes));
                    }
                }

                if (language.FSharpVerbatim && c == '@' && At(i + 1) == '"') return Quoted(i + 1, '"', QuoteMode.Doubled, multiline: true, interpolate: false, depth);

                foreach (var triple in language.Triple)
                {
                    if (StartsWith(i, triple)) return Triple(i, triple, depth);
                }

                if (c == '"' && language.Double != QuoteMode.None) return Quoted(i, '"', language.Double, language.DoubleMultiline, interpolate: true, depth);
                if (c == '\'' && language.Single != QuoteMode.None) return Quoted(i, '\'', language.Single, language.SingleMultiline, language.SingleInterpolates, depth);
                if (c == '`' && language.Backtick != QuoteMode.None) return Quoted(i, '`', language.Backtick, language.BacktickMultiline, interpolate: false, depth);
                if (language.CodeEscape == '\\' && c == '$' && At(i + 1) == '\'') return Quoted(i + 1, '\'', QuoteMode.Escaped, multiline: true, interpolate: false, depth);
                return -1;
            }

            private bool IsHashComment(int i)
            {
                var previous = At(i - 1);
                switch (_language.Hash)
                {
                    case HashComment.Anywhere: return true;
                    case HashComment.WordStart: return i == 0 || char.IsWhiteSpace(previous) || ";|&(){}".IndexOf(previous) >= 0;
                    case HashComment.NotAfterDollar: return previous != '$' && previous != '\\';
                    case HashComment.NotAttribute: return At(i + 1) != '[';
                    case HashComment.Directive:
                        for (var k = i - 1; k >= 0 && _source[k] != '\n'; k--) if (!char.IsWhiteSpace(_source[k])) return false;
                        return true;
                    default: return false;
                }
            }

            private bool RestOfLineBlank(int i)
            {
                for (; i < Length && _source[i] != '\n'; i++) if (!char.IsWhiteSpace(_source[i])) return false;
                return true;
            }

            /// <summary>Blanks from <paramref name="i"/> through the end of the first later line that starts with <paramref name="terminator"/>.</summary>
            private int BlankToLine(int i, string terminator)
            {
                var k = LineEnd(i);
                while (k < Length && !StartsWith(k + 1, terminator)) k = LineEnd(k + 1);
                var end = k < Length ? LineEnd(k + 1) : Length;
                Blank(i, end);
                return end;
            }

            private int BlockCommentEnd(int i, string open, string close)
            {
                var nest = 0;
                while (i < Length)
                {
                    if (_language.NestedComments && StartsWith(i, open)) { nest++; i += open.Length; continue; }
                    if (StartsWith(i, close))
                    {
                        i += close.Length;
                        if (nest-- == 0) return i;
                        continue;
                    }

                    i++;
                }

                return Length;
            }

            /// <summary>The closing bracket of a Lua long bracket <c>[==[</c> at <paramref name="i"/>; null when none opens there.</summary>
            private string? LongBracketClose(int i)
            {
                if (At(i) != '[') return null;
                var level = 0;
                while (At(i + 1 + level) == '=') level++;
                return At(i + 1 + level) == '[' ? "]" + new string('=', level) + "]" : null;
            }

            private int BlankThrough(int start, int bracket, string close, bool keepDelimiters)
            {
                var open = bracket + close.Length;
                var found = _source.IndexOf(close, open, StringComparison.Ordinal);
                var end = found < 0 ? Length : found + close.Length;
                if (keepDelimiters) Blank(open, found < 0 ? Length : found);
                else Blank(start, end);
                return end;
            }

            /// <summary>A raw literal whose contents start at <paramref name="open"/> and end at <paramref name="close"/>.</summary>
            private int BlankUntil(int open, string close)
            {
                var found = _source.IndexOf(close, open, StringComparison.Ordinal);
                if (found < 0) { Blank(open, Length); return Length; }
                Blank(open, found);
                return found + close.Length;
            }

            private int Heredoc(int i)
            {
                var k = i + 2;
                if (_language.PhpHeredoc)
                {
                    if (At(k) != '<') return -1;
                    k++;
                }
                else if (At(k) == '<') return i + 3; // a bash here-string (<<<), not a document

                var indented = _language.PhpHeredoc;
                if (At(k) == '-' || At(k) == '~') { indented = true; k++; }
                var quote = At(k) == '\'' || At(k) == '"' ? At(k) : '\0';
                if (quote != '\0') k++;
                else if (At(k) == '\\') k++;
                var start = k;
                while (k < Length && IsIdentifierPart(_source[k])) k++;
                if (k == start || char.IsDigit(_source[start])) return -1;
                var word = _source.Substring(start, k - start);
                // Ruby's bare <<NAME needs a capitalized name; anything else is the shift / append operator.
                if (_language.RubyBlockComments && quote == '\0' && !indented && !char.IsUpper(word[0])) return -1;
                if (quote != '\0')
                {
                    if (At(k) != quote) return -1;
                    k++;
                }

                _heredocs.Add(word);
                return k;
            }

            private int Triple(int i, string triple, int depth)
            {
                var k = i + triple.Length;
                var interpolate = triple[0] == '"' || _language.SingleInterpolates;
                while (k < Length)
                {
                    if (interpolate)
                    {
                        var after = Interpolation(k, depth);
                        if (after >= 0) { k = after; continue; }
                    }

                    if (_language.TripleEscapes && _source[k] == '\\') { k += 2; continue; }
                    if (StartsWith(k, triple))
                    {
                        while (At(k + triple.Length) == triple[0]) k++;
                        Blank(i + triple.Length, k);
                        return k + triple.Length;
                    }

                    k++;
                }

                Blank(i + triple.Length, Length);
                return Length;
            }

            /// <summary>Past the code of an interpolation opening at <paramref name="k"/>; -1 when none opens there.</summary>
            private int Interpolation(int k, int depth)
            {
                foreach (var opener in _language.Interpolation)
                {
                    if (!StartsWith(k, opener)) continue;
                    var closer = opener[opener.Length - 1] == '(' ? ')' : '}';
                    var close = Code(k + opener.Length, closer, depth + 1);
                    return Math.Min(Length, close + 1);
                }

                return -1;
            }

            private int Quoted(int i, char quote, QuoteMode mode, bool multiline, bool interpolate, int depth)
            {
                if (mode == QuoteMode.Short)
                {
                    if (At(i + 1) == '\\')
                    {
                        var k = i + 2;
                        while (k < Length && k < i + 12 && _source[k] != '\n' && _source[k] != quote) k++;
                        if (At(k) != quote) return -1;
                        Blank(i + 1, k);
                        return k + 1;
                    }

                    var width = char.IsHighSurrogate(At(i + 1)) ? 2 : 1;
                    if (At(i + 1) == '\n' || At(i + 1) == quote || At(i + 1 + width) != quote) return -1;
                    Blank(i + 1, i + 1 + width);
                    return i + 2 + width;
                }

                if (mode == QuoteMode.Char)
                {
                    var k = i + 1;
                    while (k < Length && _source[k] != '\n' && _source[k] != quote) k += _source[k] == '\\' ? 2 : 1;
                    if (At(k) != quote) return -1;
                    Blank(i + 1, k);
                    return k + 1;
                }

                var j = i + 1;
                while (j < Length)
                {
                    var ch = _source[j];
                    if (ch == '\n' && !multiline) break;
                    if (interpolate)
                    {
                        var after = Interpolation(j, depth);
                        if (after >= 0) { j = after; continue; }
                    }

                    if (mode == QuoteMode.Escaped && ch == '\\') { j += 2; continue; }
                    if (mode == QuoteMode.PowerShell && ch == '`') { j += 2; continue; }
                    if (ch == quote)
                    {
                        if ((mode == QuoteMode.Doubled || mode == QuoteMode.PowerShell) && At(j + 1) == quote) { j += 2; continue; }
                        Blank(i + 1, j);
                        return j + 1;
                    }

                    j++;
                }

                j = Math.Min(j, Length);
                Blank(i + 1, j);
                return j;
            }
        }
    }
}
