using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;

namespace Pomp.Tools
{
    /// <summary>
    /// Type and member declarations of the languages <see cref="ScannedLanguage"/> describes (Python, Java, Kotlin, Go, Rust, PHP, Ruby,
    /// Swift, Scala, Dart, Lua, PowerShell, shell, F#, SQL, R, Objective-C, Perl, Groovy). Lexical like <see cref="SymbolOutline"/>: comments
    /// and literals are masked first, declarations start where a language's declaration pattern matches at a statement start, and a body
    /// ends at its closing brace, its dedent, its <c>end</c> keyword or its statement end. Declarations whose body never closes are left out.
    /// </summary>
    internal static class DeclarationScanner
    {
        /// <summary>Declarations in source order; null when the file type is not scanned this way.</summary>
        internal static List<SymbolOutline.LexicalDeclaration>? Find(string path, IReadOnlyList<string> lines)
        {
            var language = ScannedLanguage.For(path, lines);
            if (language == null) return null;
            return new Scan(language, SourceMask.Create(lines, language)).Run();
        }

        private enum HeadKind { None, Brace, Semicolon, StatementEnd, Expression, Indented }

        private sealed class Found
        {
            public Found(int start, int end, bool isType, string name) { Start = start; End = end; IsType = isType; Name = name; }
            public int Start { get; }
            public int End { get; }
            public bool IsType { get; }
            public string Name { get; }
        }

        private sealed class Scan
        {
            /// <summary>How far a head is searched for its body.</summary>
            private const int MaxHead = 4000;
            private readonly ScannedLanguage _language;
            private readonly SourceMask _mask;
            private readonly string _text;
            private readonly string[] _lines;
            private readonly List<Found> _enclosing = new List<Found>();
            private readonly List<SymbolOutline.LexicalDeclaration> _found = new List<SymbolOutline.LexicalDeclaration>();

            public Scan(ScannedLanguage language, SourceMask mask)
            {
                _language = language;
                _mask = mask;
                _text = mask.Text;
                _lines = new string[mask.LineCount];
                for (var i = 0; i < _lines.Length; i++) _lines[i] = _text.Substring(mask.LineStarts[i], mask.LineEnd(i) - mask.LineStarts[i]);
            }

            private char At(int i) => i >= 0 && i < _text.Length ? _text[i] : '\0';

            public List<SymbolOutline.LexicalDeclaration> Run()
            {
                for (var line = 0; line < _lines.Length; line++)
                {
                    if (_mask.Parentheses[line] > 0) continue;
                    var text = _lines[line];
                    var column = 0;
                    while (column < text.Length && char.IsWhiteSpace(text[column])) column++;
                    if (column == text.Length) continue;
                    Try(line, column);
                    if (!_language.MidLine) continue;
                    var parentheses = 0;
                    for (var i = column; i < text.Length; i++)
                    {
                        var c = text[i];
                        if (c == '(' || c == '[') parentheses++;
                        else if (c == ')' || c == ']') parentheses = Math.Max(0, parentheses - 1);
                        else if (parentheses == 0 && (c == ';' || c == '{' || c == '}'))
                        {
                            var next = i + 1;
                            while (next < text.Length && char.IsWhiteSpace(text[next])) next++;
                            if (next < text.Length && text[next] != ';' && text[next] != '}') Try(line, next);
                        }
                    }
                }

                return _found;
            }

            private void Try(int line, int column)
            {
                var position = _mask.LineStarts[line] + column;
                while (_enclosing.Count > 0 && _enclosing[_enclosing.Count - 1].End <= position) _enclosing.RemoveAt(_enclosing.Count - 1);
                var enclosing = _enclosing.Count > 0 ? _enclosing[_enclosing.Count - 1] : null;
                foreach (var rule in _language.Rules)
                {
                    if (rule.Scope == RuleScope.OutsideFunction && enclosing != null && !enclosing.IsType) continue;
                    if (rule.Scope == RuleScope.InsideType && (enclosing == null || !enclosing.IsType)) continue;
                    var match = rule.Pattern.Match(_lines[line], column);
                    if (!match.Success) continue;
                    var name = match.Groups["n"].Value;
                    if (rule.Constructor && (enclosing == null || !string.Equals(name, enclosing.Name, _language.IgnoreCase ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal))) continue;
                    var start = _mask.LineStarts[line] + match.Groups["s"].Index;
                    if (!Resolve(rule, match, line, start, out var head, out var end) || end <= start) continue;
                    head = Math.Max(start, Math.Min(head, end));
                    var found = new Found(start, end, rule.IsType, name);
                    _enclosing.Add(found);
                    var declaration = new SymbolOutline.LexicalDeclaration(rule.IsType, line + 1, start - _mask.LineStarts[line], _mask.LineOf(head) + 1, head - _mask.LineStarts[_mask.LineOf(head)]);
                    var endLine = _mask.LineOf(end);
                    declaration.End(endLine + 1, end - _mask.LineStarts[endLine], trim: false);
                    _found.Add(declaration);
                    return;
                }
            }

            /// <summary>The end of the signature (<paramref name="head"/>) and of the body (<paramref name="end"/>, exclusive).</summary>
            private bool Resolve(DeclarationRule rule, Match match, int line, int start, out int head, out int end)
            {
                head = end = -1;
                var after = _mask.LineStarts[line] + match.Index + match.Length;
                var indent = Indent(line);
                switch (rule.Body)
                {
                    case RuleBody.AtEnd: return AtEnd(after, out head, out end);
                    case RuleBody.Parens:
                        head = SkipSpace(after, lines: false);
                        if (At(head) != '(') return false;
                        end = Close(head, '(', ')');
                        return end > 0;
                    case RuleBody.RFunction: return RFunction(after, line, indent, out head, out end);
                    case RuleBody.Shell: return ShellBody(after, out head, out end);
                }

                switch (_language.Body)
                {
                    case BodyStyle.RubyEnd: return RubyEnd(match, line, out head, out end);
                    case BodyStyle.LuaEnd: return LuaEnd(match, line, out head, out end);
                    case BodyStyle.Sql: return SqlEnd(match, line, start, after, out head, out end);
                }

                var kind = Head(after, indent, out head);
                switch (kind)
                {
                    case HeadKind.Brace:
                        end = Close(head, '{', '}');
                        return end > 0;
                    case HeadKind.Semicolon:
                        end = head + 1;
                        return rule.Bodiless;
                    case HeadKind.StatementEnd:
                        end = TrimBack(head, start);
                        head = end;
                        return rule.Bodiless;
                    case HeadKind.Expression:
                        end = _language.Body == BodyStyle.Indent ? IndentEnd(head + 1, line, indent) : ExpressionEnd(head + (At(head) == '=' && At(head + 1) == '>' ? 2 : 1), line, indent);
                        return end > head;
                    case HeadKind.Indented:
                        end = IndentEnd(head + 1, line, indent);
                        return end > head;
                    default:
                        return false;
                }
            }

            /// <summary>Where the head starting at <paramref name="i"/> ends: its body's opener, its terminator, or its statement end.</summary>
            private HeadKind Head(int i, int indent, out int head)
            {
                var depth = 0;
                var limit = Math.Min(_text.Length, i + MaxHead);
                for (head = i; head < limit; head++)
                {
                    var c = _text[head];
                    if (c == '(' || c == '[') { depth++; continue; }
                    if (c == ')' || c == ']')
                    {
                        if (depth == 0) return HeadKind.None;
                        depth--;
                        continue;
                    }

                    if (depth > 0) continue;
                    switch (c)
                    {
                        case '\n':
                            if (_language.ColonBody)
                            {
                                if (EndsWithBackslash(head)) continue;
                                return HeadKind.None;
                            }

                            if (_language.NewlineTerminated && !Continues(head, indent)) return _language.OffsideHeads ? HeadKind.None : HeadKind.StatementEnd;
                            continue;
                        case '{':
                            if (_language.Body == BodyStyle.Indent) continue;
                            return HeadKind.Brace;
                        case '}':
                            return _language.NewlineTerminated ? HeadKind.StatementEnd : HeadKind.None;
                        case ';':
                            return HeadKind.Semicolon;
                        case '=':
                            if (_language.Arrow && At(head + 1) == '>') return HeadKind.Expression;
                            if (_language.EqualsBody && IsAssignment(head)) return HeadKind.Expression;
                            continue;
                        case ':':
                            if (_language.ColonBody) return HeadKind.Indented;
                            if (_language.ColonAtLineEnd && RestOfLineBlank(head + 1)) return HeadKind.Indented;
                            continue;
                    }
                }

                return HeadKind.None;
            }

            private bool IsAssignment(int i)
            {
                var before = At(i - 1);
                var after = At(i + 1);
                return "=!<>:+-*/%&|^?.".IndexOf(before) < 0 && after != '=' && after != '>' && after != '~';
            }

            /// <summary>Whether the statement goes on past the line break at <paramref name="i"/> (newline-terminated languages).</summary>
            private bool Continues(int i, int indent)
            {
                var line = _mask.LineOf(i);
                var last = TrimBack(i, _mask.LineStarts[line]) - 1;
                if (last < _mask.LineStarts[line]) return false;
                var c = _text[last];
                if ("=+-*/%.,([&|:?\\".IndexOf(c) >= 0 || (c == '>' && At(last - 1) == '-')) return true;
                if (line + 1 >= _lines.Length) return false;
                var next = _lines[line + 1];
                var column = 0;
                while (column < next.Length && char.IsWhiteSpace(next[column])) column++;
                if (column == next.Length) return false;
                if (_language.OffsideHeads) return Indent(line + 1) > indent;
                var first = next[column];
                if ("{.?:)]".IndexOf(first) >= 0) return true;
                if (first == '=' && At(_mask.LineStarts[line + 1] + column + 1) != '=') return true;
                if ((first == '&' || first == '|') && column + 1 < next.Length && next[column + 1] == first) return true;
                if (first == '-' && column + 1 < next.Length && next[column + 1] == '>') return true;
                foreach (var word in _language.ContinuationWords)
                {
                    if (string.CompareOrdinal(next, column, word, 0, word.Length) == 0 && (column + word.Length == next.Length || !IsWordPart(word[word.Length - 1]) || !IsWordPart(next[column + word.Length]))) return true;
                }

                return false;
            }

            /// <summary>The end of an expression body starting at <paramref name="i"/>: its ';', its statement end, or its dedent.</summary>
            private int ExpressionEnd(int i, int line, int indent)
            {
                if (_language.NewlineTerminated && RestOfLineBlank(i)) return IndentEnd(i, line, indent);
                var depth = 0;
                for (var k = i; k < _text.Length; k++)
                {
                    var c = _text[k];
                    if (c == '(' || c == '[' || c == '{') depth++;
                    else if (c == ')' || c == ']' || c == '}')
                    {
                        if (depth == 0) return TrimBack(k, i);
                        depth--;
                    }
                    else if (depth == 0 && c == ';') return k + 1;
                    else if (depth == 0 && c == '\n' && _language.NewlineTerminated && !Continues(k, indent)) return TrimBack(k, i);
                }

                return TrimBack(_text.Length, i);
            }

            /// <summary>
            /// The end of an indented body after <paramref name="i"/>: code on the head's line after it, then every following line indented
            /// deeper than the declaration's line (or inside a literal or brackets opened in the body). -1 when there is none.
            /// </summary>
            private int IndentEnd(int i, int line, int indent)
            {
                var headLine = _mask.LineOf(Math.Min(i, _text.Length));
                var end = -1;
                var lineEnd = _mask.LineEnd(headLine);
                var code = TrimBack(lineEnd, i);
                if (code > i) end = code;
                var nesting = _mask.Nesting[line];
                for (var j = headLine + 1; j < _lines.Length; j++)
                {
                    var text = _lines[j];
                    var blank = text.Trim().Length == 0;
                    if (_mask.Continued[j] || _mask.Nesting[j] > nesting)
                    {
                        if (!blank) end = LineContentEnd(j);
                        continue;
                    }

                    if (blank) continue;
                    var depth = Indent(j);
                    if (depth > indent || (_language.OffsideHeads && depth == indent && text.TrimStart().StartsWith("|", StringComparison.Ordinal)))
                    {
                        end = LineContentEnd(j);
                        continue;
                    }

                    break;
                }

                return end;
            }

            private int LineContentEnd(int line) => TrimBack(_mask.LineEnd(line), _mask.LineStarts[line]);

            /// <summary>Objective-C <c>@interface</c> through <c>@end</c>; a forward declaration (<c>@protocol P;</c>) has none.</summary>
            private bool AtEnd(int after, out int head, out int end)
            {
                head = end = -1;
                var next = SkipSpace(after, lines: false);
                if (At(next) == ';' || At(next) == ',') return false;
                var line = _mask.LineOf(after);
                head = LineContentEnd(line);
                for (var k = _text.IndexOf("@end", after, StringComparison.Ordinal); k >= 0; k = _text.IndexOf("@end", k + 4, StringComparison.Ordinal))
                {
                    if (!IsWordPart(At(k + 4))) { end = k + 4; return true; }
                }

                return false;
            }

            /// <summary>R <c>name &lt;- function(…)</c>: the parameters, then a braced body or one expression, possibly on the next line.</summary>
            private bool RFunction(int after, int line, int indent, out int head, out int end)
            {
                head = end = -1;
                var open = SkipSpace(after, lines: false);
                if (At(open) != '(') return false;
                head = Close(open, '(', ')');
                if (head < 0) return false;
                var body = SkipSpace(head, lines: true);
                end = At(body) == '{' ? Close(body, '{', '}') : ExpressionEnd(body, line, indent);
                return end > head;
            }

            /// <summary>Shell <c>name() { … }</c>, <c>function name { … }</c>, or a subshell body <c>( … )</c>.</summary>
            private bool ShellBody(int after, out int head, out int end)
            {
                end = -1;
                head = SkipSpace(after, lines: true);
                if (At(head) == '(')
                {
                    var close = SkipSpace(head + 1, lines: false);
                    if (At(close) == ')') head = SkipSpace(close + 1, lines: true);
                }

                if (At(head) == '{') end = Close(head, '{', '}');
                else if (At(head) == '(') end = Close(head, '(', ')');
                return end > 0;
            }

            /// <summary>Ruby: the <c>end</c> matching the <c>class</c>, <c>module</c> or <c>def</c>; an endless <c>def f = …</c> ends with its line.</summary>
            private bool RubyEnd(Match match, int line, out int head, out int end)
            {
                var keyword = match.Groups["k"];
                var position = _mask.LineStarts[line] + keyword.Index;
                var lineEnd = _mask.LineEnd(line);
                var semicolon = _text.IndexOf(';', position, lineEnd - position);
                head = TrimBack(semicolon >= 0 ? semicolon : lineEnd, position);
                if (keyword.Value == "def")
                {
                    var assignment = EndlessDefinition(position + 3);
                    if (assignment >= 0)
                    {
                        head = assignment;
                        end = LineContentEnd(line);
                        return true;
                    }
                }

                end = RubyBlockEnd(position + keyword.Length);
                return end > 0;
            }

            /// <summary>The '=' of an endless method (<c>def name(args) = expression</c>) after <c>def</c>; -1 otherwise.</summary>
            private int EndlessDefinition(int i)
            {
                i = SkipSpace(i, lines: false);
                while (i < _text.Length && !char.IsWhiteSpace(_text[i]) && _text[i] != '(' && _text[i] != ';') i++;
                i = SkipSpace(i, lines: false);
                if (At(i) == '(') { i = Close(i, '(', ')'); if (i < 0) return -1; i = SkipSpace(i, lines: false); }
                return At(i) == '=' && "=~>".IndexOf(At(i + 1)) < 0 ? i : -1;
            }

            private int RubyBlockEnd(int i)
            {
                var depth = 1;
                var last = 'x';
                var previous = "";
                var loopDo = false;
                while (i < _text.Length)
                {
                    var c = _text[i];
                    if (c == '\n') { last = '\0'; previous = ""; loopDo = false; i++; continue; }
                    if (char.IsWhiteSpace(c)) { i++; continue; }
                    if (char.IsLetter(c) || c == '_')
                    {
                        var j = i + 1;
                        while (j < _text.Length && IsWordPart(_text[j])) j++;
                        if (At(j) == '?' || At(j) == '!') j++;
                        var word = _text.Substring(i, j - i);
                        var before = At(i - 1);
                        var member = before == '.' || before == '$' || before == '@' || (before == ':' && At(i - 2) != ':') || (At(j) == ':' && At(j + 1) != ':');
                        if (!member && previous != "def")
                        {
                            var statement = last == '\0' || "=([{,|&!;?:+-*/<>%~^".IndexOf(last) >= 0 ||
                                previous == "then" || previous == "else" || previous == "do" || previous == "begin" || previous == "and" || previous == "or" || previous == "not";
                            switch (word)
                            {
                                case "def": if (EndlessDefinition(j) < 0) depth++; break;
                                case "class": case "module": case "begin": case "case": depth++; break;
                                case "if": case "unless": if (statement) depth++; break;
                                case "while": case "until": case "for": if (statement) { depth++; loopDo = true; } break;
                                case "do": if (loopDo) loopDo = false; else depth++; break;
                                case "end": if (--depth == 0) return j; break;
                            }
                        }

                        previous = word;
                        last = word[word.Length - 1];
                        i = j;
                        continue;
                    }

                    if (c == ';') loopDo = false;
                    last = c;
                    previous = "";
                    i++;
                }

                return -1;
            }

            /// <summary>Lua: the <c>end</c> matching the declaration's <c>function</c>.</summary>
            private bool LuaEnd(Match match, int line, out int head, out int end)
            {
                head = end = -1;
                var keyword = match.Groups["k"];
                var position = _mask.LineStarts[line] + keyword.Index;
                var open = SkipSpace(_mask.LineStarts[line] + match.Index + match.Length, lines: false);
                if (At(open) != '(') return false;
                head = Close(open, '(', ')');
                if (head < 0) return false;
                var depth = 1;
                for (var i = position + keyword.Length; i < _text.Length;)
                {
                    var c = _text[i];
                    if (!(char.IsLetter(c) || c == '_')) { i++; continue; }
                    var j = i + 1;
                    while (j < _text.Length && IsWordPart(_text[j])) j++;
                    var before = At(i - 1);
                    if (before != '.' && before != ':' && !IsWordPart(before))
                    {
                        switch (_text.Substring(i, j - i))
                        {
                            case "function": case "if": case "do": case "repeat": depth++; break;
                            case "end": case "until":
                                if (--depth == 0) { end = j; return true; }
                                break;
                        }
                    }

                    i = j;
                }

                return false;
            }

            private static readonly Regex SqlSeparator = new Regex(@"^\s*(?:GO(?:\s+\d+)?|/|DELIMITER\s+\S+)\s*$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
            private static readonly Regex SqlStatement = new Regex(@"^\s*(?:CREATE|ALTER)\s", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
            private static readonly Regex SqlWord = new Regex(@"\b(?<w>BEGIN|CASE|END)\b(?=\s+(?<next>\w+))?", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

            /// <summary>
            /// SQL: a declaration runs to its batch separator (<c>GO</c>, <c>/</c>, <c>DELIMITER</c>); without one, through the END of its
            /// outermost BEGIN block (and a following name and ';'), else through its first ';'; at most to the next CREATE / ALTER.
            /// </summary>
            private bool SqlEnd(Match match, int line, int start, int after, out int head, out int end)
            {
                head = LineContentEnd(line);
                var view = string.Equals(match.Groups["k"].Value, "VIEW", StringComparison.OrdinalIgnoreCase);
                var limit = _text.Length;
                var separated = false;
                for (var j = line + 1; j < _lines.Length; j++)
                {
                    if (SqlSeparator.IsMatch(_lines[j])) { limit = _mask.LineStarts[j]; separated = true; break; }
                    if (SqlStatement.IsMatch(_lines[j])) { limit = _mask.LineStarts[j]; break; }
                }

                end = TrimBack(limit, start);
                if (separated && !view) return end > after;
                if (!view)
                {
                    var depth = 0;
                    var opened = false;
                    var closedCase = -1;
                    for (var word = SqlWord.Match(_text, after); word.Success && word.Index < limit; word = word.NextMatch())
                    {
                        if (word.Index == closedCase) continue;
                        var keyword = word.Groups["w"].Value.ToUpperInvariant();
                        var next = word.Groups["next"].Value.ToUpperInvariant();
                        if (keyword == "BEGIN")
                        {
                            if (next == "TRAN" || next == "TRANSACTION" || next == "DISTRIBUTED" || next == "DIALOG" || next == "CONVERSATION") continue;
                            depth++;
                            opened = true;
                        }
                        else if (keyword == "CASE")
                        {
                            if (opened) depth++;
                        }
                        else if (opened && next != "IF" && next != "LOOP" && next != "WHILE" && next != "REPEAT" && next != "FOR")
                        {
                            // END CASE closes the CASE statement; the CASE after it opens nothing.
                            if (next == "CASE") closedCase = word.Groups["next"].Index;
                            if (--depth > 0) continue;
                            var close = word.Index + 3;
                            var following = SkipSpace(close, lines: false);
                            if (IsWordStart(At(following)))
                            {
                                var name = following;
                                while (IsWordPart(At(name)) || At(name) == '.') name++;
                                var semicolon = SkipSpace(name, lines: false);
                                if (At(semicolon) == ';') close = semicolon + 1;
                            }
                            else if (At(following) == ';') close = following + 1;

                            end = close;
                            return end > after;
                        }
                    }
                }

                var parentheses = 0;
                for (var k = after; k < limit; k++)
                {
                    var c = _text[k];
                    if (c == '(') parentheses++;
                    else if (c == ')') parentheses = Math.Max(0, parentheses - 1);
                    else if (c == ';' && parentheses == 0) { end = k + 1; return end > after; }
                }

                return end > after;
            }

            private int Close(int open, char opener, char closer)
            {
                var depth = 0;
                for (var i = open; i < _text.Length; i++)
                {
                    if (_text[i] == opener) depth++;
                    else if (_text[i] == closer && --depth == 0) return i + 1;
                }

                return -1;
            }

            private int SkipSpace(int i, bool lines)
            {
                while (i < _text.Length && char.IsWhiteSpace(_text[i]) && (lines || _text[i] != '\n')) i++;
                return i;
            }

            private bool RestOfLineBlank(int i)
            {
                for (; i < _text.Length && _text[i] != '\n'; i++) if (!char.IsWhiteSpace(_text[i])) return false;
                return true;
            }

            private bool EndsWithBackslash(int lineBreak)
            {
                var last = TrimBack(lineBreak, 0) - 1;
                return last >= 0 && _text[last] == '\\';
            }

            /// <summary>Past the last non-whitespace character before <paramref name="i"/>, not before <paramref name="floor"/>.</summary>
            private int TrimBack(int i, int floor)
            {
                while (i > floor && char.IsWhiteSpace(_text[i - 1])) i--;
                return i;
            }

            private int Indent(int line)
            {
                var text = _lines[line];
                var width = 0;
                foreach (var c in text)
                {
                    if (c == ' ') width++;
                    else if (c == '\t') width = (width / 8 + 1) * 8;
                    else break;
                }

                return width;
            }

            private static bool IsWordStart(char c) => char.IsLetter(c) || c == '_';
            private static bool IsWordPart(char c) => char.IsLetterOrDigit(c) || c == '_';
        }
    }
}
