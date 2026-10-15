using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;

namespace Pomp.Tools
{
    /// <summary>
    /// A bounded, lexical navigation aid, not a language service. Scans the existing lines once, never reads a file,
    /// and never treats an outline signature as a fully displayed source line. Bodies and literal contents are skipped.
    /// </summary>
    internal static class SymbolOutline
    {
        internal const int MaxEntries = 400;
        internal const int MaxCharacters = 24000;
        internal const int MaxSignatureCharacters = 180;
        private const int MaxCandidateCharacters = 2048;

        internal static string Create(string path, IReadOnlyList<string> lines)
        {
            return CreateBounded(path, lines, MaxEntries, MaxCharacters);
        }

        /// <summary>Automatic editor context may use a tighter navigation budget than an explicit outline request.</summary>
        internal static string CreateBounded(string path, IReadOnlyList<string> lines, int maxEntries, int maxCharacters)
        {
            var language = GetLanguage(path);
            if (language == Language.Unknown || lines.Count == 0)
            {
                return string.Empty;
            }

            var output = new OutlineWriter(Math.Min(MaxEntries, Math.Max(1, maxEntries)), Math.Min(MaxCharacters, Math.Max(64, maxCharacters)));
            if (language == Language.VisualBasic)
            {
                ScanVisualBasic(lines, output);
            }
            else
            {
                new BraceScanner(lines, language, output).Scan();
            }

            return output.Finish();
        }

        private enum Language { Unknown, CSharp, VisualBasic, JavaScript, Cpp }
        private enum ScopeKind { Body, Namespace, Type, Interface, Enum, Property }

        private static Language GetLanguage(string path)
        {
            var extension = Path.GetExtension(path).ToLowerInvariant();
            switch (extension)
            {
                case ".cs": return Language.CSharp;
                case ".vb": return Language.VisualBasic;
                case ".ts": case ".tsx": case ".js": case ".jsx": case ".mjs": case ".cjs": case ".mts": case ".cts":
                    return Language.JavaScript;
                case ".c": case ".cc": case ".cpp": case ".cxx": case ".h": case ".hh": case ".hpp": case ".hxx": case ".inl":
                    return Language.Cpp;
                default: return Language.Unknown;
            }
        }

        private sealed class OutlineWriter
        {
            // Reserve enough space for the omission marker even for the largest possible line list.
            private const int MarkerReserve = 48;
            private readonly StringBuilder _text = new StringBuilder();
            private int _shown;
            private int _omitted;
            private readonly int _maxEntries;
            private readonly int _maxCharacters;

            public OutlineWriter(int maxEntries, int maxCharacters)
            {
                _maxEntries = maxEntries;
                _maxCharacters = maxCharacters;
            }

            public bool NeedsSignature => _shown < _maxEntries && _omitted == 0;
            public void Omit() { _omitted++; }

            public void Add(int line, int depth, string signature)
            {
                if (signature.Length == 0) return;
                if (_shown == _maxEntries || _omitted > 0)
                {
                    _omitted++;
                    return;
                }

                if (signature.Length > MaxSignatureCharacters)
                {
                    signature = signature.Substring(0, MaxSignatureCharacters - 1) + "…";
                }

                var row = new string(' ', Math.Min(depth, 16) * 2) + line.ToString(CultureInfo.InvariantCulture) + ": " + signature;
                if (_text.Length + row.Length + 1 > _maxCharacters - MarkerReserve)
                {
                    _omitted++;
                    return;
                }

                if (_text.Length > 0) _text.Append('\n');
                _text.Append(row);
                _shown++;
            }

            public string Finish()
            {
                if (_omitted > 0)
                {
                    if (_text.Length > 0) _text.Append('\n');
                    _text.Append('[').Append(_omitted.ToString(CultureInfo.InvariantCulture)).Append(" more symbols]");
                }

                return _text.ToString();
            }
        }

        private readonly struct Token
        {
            public Token(string text, int line, bool gap) { Text = text; Line = line; Gap = gap; }
            public string Text { get; }
            public int Line { get; }
            public bool Gap { get; }
        }

        private readonly struct Scope
        {
            public Scope(ScopeKind kind, string name = "") { Kind = kind; Name = name; }
            public ScopeKind Kind { get; }
            public string Name { get; }
            public bool IsSymbol => Kind == ScopeKind.Namespace || Kind == ScopeKind.Type || Kind == ScopeKind.Interface || Kind == ScopeKind.Enum;
            public bool IsType => Kind == ScopeKind.Type || Kind == ScopeKind.Interface;
        }

        private sealed class BraceScanner
        {
            private readonly Lexer _lexer;
            private readonly Language _language;
            private readonly OutlineWriter _output;
            private readonly List<Scope> _scopes = new List<Scope>();
            private readonly List<Token> _candidate = new List<Token>();
            private int _candidateCharacters;
            private int _parentheses;
            private int _brackets;
            private int _embeddedBraces;
            private int _attributeBrackets;
            private int _symbolDepth;
            private int _fileNamespaceDepth;
            private int _skipExpressionAtDepth = -1;
            private int _expressionParentheses;
            private int _expressionBrackets;
            private int _expressionLine;
            private string _expressionLastToken = string.Empty;

            public BraceScanner(IReadOnlyList<string> lines, Language language, OutlineWriter output)
            {
                _lexer = new Lexer(lines, language);
                _language = language;
                _output = output;
            }

            private bool CanDeclare => _scopes.Count == 0 || _scopes[_scopes.Count - 1].Kind == ScopeKind.Namespace || _scopes[_scopes.Count - 1].IsType;
            private bool InsideType => _scopes.Count > 0 && _scopes[_scopes.Count - 1].IsType;

            public void Scan()
            {
                while (_lexer.Next(CanDeclare && (_skipExpressionAtDepth < 0 || _language == Language.JavaScript), out var token))
                {
                    var text = token.Text;
                    if (_language == Language.JavaScript && _skipExpressionAtDepth >= 0 && _scopes.Count == _skipExpressionAtDepth)
                    {
                        var canFinish = _expressionParentheses == 0 && _expressionBrackets == 0 && !NeedsExpressionContinuation(_expressionLastToken);
                        if (canFinish && token.Line > _expressionLine && !ContinuesExpression(text) && StartsJavaScriptDeclaration(text))
                        {
                            // ASI: a fresh declaration/member on a later line cannot belong to the completed initializer.
                            _skipExpressionAtDepth = -1;
                        }
                        else
                        {
                            if (text == "(") _expressionParentheses++;
                            else if (text == ")") _expressionParentheses = Math.Max(0, _expressionParentheses - 1);
                            else if (text == "[") _expressionBrackets++;
                            else if (text == "]") _expressionBrackets = Math.Max(0, _expressionBrackets - 1);
                            _expressionLastToken = text;
                            _expressionLine = token.Line;
                        }
                    }
                    if (_attributeBrackets > 0)
                    {
                        if (text == "[") _attributeBrackets++;
                        else if (text == "]") _attributeBrackets--;
                        continue;
                    }

                    if (CanDeclare && text == "[" && _language != Language.JavaScript &&
                        (_candidate.Count == 0 || (_language == Language.CSharp && _parentheses > 0 && _brackets == 0 &&
                        (_candidate[_candidate.Count - 1].Text == "(" || _candidate[_candidate.Count - 1].Text == ","))))
                    {
                        _attributeBrackets = 1;
                        continue;
                    }

                    if (_skipExpressionAtDepth >= 0 || !CanDeclare)
                    {
                        if (text == "{") Push(new Scope(ScopeKind.Body));
                        else if (text == "}")
                        {
                            Pop();
                            if (_language == Language.JavaScript && _scopes.Count == _skipExpressionAtDepth)
                            {
                                _expressionLastToken = "}";
                                _expressionLine = token.Line;
                            }
                        }
                        else if (text == ";" && _scopes.Count == _skipExpressionAtDepth) _skipExpressionAtDepth = -1;
                        continue;
                    }

                    // Braces in parameter/default-value types do not open a declaration body (particularly in TS).
                    if (text == "{" && (_parentheses > 0 || _brackets > 0 || _embeddedBraces > 0))
                    {
                        _embeddedBraces++;
                        Add(token);
                    }
                    else if (text == "}" && _embeddedBraces > 0)
                    {
                        _embeddedBraces--;
                        Add(token);
                    }
                    else if (text == "(" || text == "[")
                    {
                        if (text == "(") _parentheses++; else _brackets++;
                        Add(token);
                    }
                    else if (text == ")" || text == "]")
                    {
                        if (text == ")") _parentheses = Math.Max(0, _parentheses - 1); else _brackets = Math.Max(0, _brackets - 1);
                        Add(token);
                    }
                    else if (_parentheses > 0 || _brackets > 0 || _embeddedBraces > 0)
                    {
                        Add(token);
                    }
                    else if (text == "{")
                    {
                        var scope = Declare("{");
                        Clear();
                        Push(scope ?? new Scope(ScopeKind.Body));
                    }
                    else if (text == "}")
                    {
                        Clear();
                        Pop();
                    }
                    else if (text == ";")
                    {
                        var scope = Declare(";");
                        if (scope.HasValue && scope.Value.Kind == ScopeKind.Namespace && _language == Language.CSharp) _fileNamespaceDepth = 1;
                        Clear();
                    }
                    else if (text == "=>" || text == "=")
                    {
                        Declare(text);
                        Clear();
                        _skipExpressionAtDepth = _scopes.Count;
                        _expressionParentheses = _expressionBrackets = 0;
                        _expressionLine = token.Line;
                        _expressionLastToken = text;
                    }
                    else if (text == ":" && _language == Language.Cpp && _candidate.Count == 1 && IsAccess(_candidate[0].Text))
                    {
                        Clear();
                    }
                    else
                    {
                        Add(token);
                    }
                }
            }

            private bool StartsJavaScriptDeclaration(string text)
            {
                if (text == "export" || text == "declare" || text == "function" || text == "class" || text == "interface" || text == "enum" ||
                    text == "type" || text == "namespace" || text == "const" || text == "let" || text == "var" || text == "async") return true;
                return InsideType && (IsIdentifier(text) || text == "#" || text == "*");
            }

            private static bool NeedsExpressionContinuation(string text)
            {
                switch (text)
                {
                    case "=": case "=>": case "+": case "-": case "*": case "/": case "%": case ".": case ",": case "?":
                    case ":": case "&": case "|": case "!": case "<": case ">": case "(": case "[": case "{": case "new":
                    case "await": case "yield": case "as": case "instanceof": case "in": case "typeof": case "void":
                        return true;
                    default: return false;
                }
            }

            private static bool ContinuesExpression(string text)
            {
                switch (text)
                {
                    case "(": case "[": case ".": case "?": case "+": case "-": case "*": case "/": case "%": case ",":
                    case ":": case "&": case "|": case "<": case ">": case "as": case "instanceof": case "in":
                        return true;
                    default: return false;
                }
            }

            private void Add(Token token)
            {
                // Once output is full, retain only the declaration head needed for classification/nesting/counts.
                if (!_output.NeedsSignature && (_parentheses > 0 || _brackets > 0 || _embeddedBraces > 0 || token.Text == ")" || token.Text == "]"))
                {
                    if (token.Text == "(" || token.Text == "[") _candidate.Add(token);
                    return;
                }

                if (_candidateCharacters + token.Text.Length < MaxCandidateCharacters && _candidate.Count < 512)
                {
                    _candidate.Add(token);
                    _candidateCharacters += token.Text.Length + 1;
                }
            }

            private void Clear()
            {
                _candidate.Clear();
                _candidateCharacters = _parentheses = _brackets = _embeddedBraces = 0;
            }

            private void Push(Scope scope)
            {
                _scopes.Add(scope);
                if (scope.IsSymbol) _symbolDepth++;
            }

            private void Pop()
            {
                if (_scopes.Count > 0)
                {
                    if (_scopes[_scopes.Count - 1].IsSymbol) _symbolDepth--;
                    _scopes.RemoveAt(_scopes.Count - 1);
                }

                if (_scopes.Count < _skipExpressionAtDepth) _skipExpressionAtDepth = -1;
            }

            private Scope? Declare(string terminator)
            {
                if (_candidate.Count == 0) return null;
                var first = FirstDeclarationToken();
                if (first >= _candidate.Count) return null;
                var kind = _candidate[first].Text;
                Scope? scope = null;
                var emit = false;
                if (kind == "namespace")
                {
                    emit = first + 1 < _candidate.Count && terminator != "=" && terminator != "=>";
                    scope = new Scope(ScopeKind.Namespace);
                }
                else if (kind == "class" || kind == "struct" || kind == "interface" || kind == "enum" ||
                         (_language == Language.CSharp && (kind == "record" || kind == "delegate")))
                {
                    var nameIndex = first + 1;
                    if (kind == "record" && nameIndex < _candidate.Count && (_candidate[nameIndex].Text == "class" || _candidate[nameIndex].Text == "struct")) nameIndex++;
                    // delegate* is a function-pointer type, not a declaration.
                    emit = nameIndex < _candidate.Count && IsIdentifier(_candidate[nameIndex].Text) && terminator != "=" && terminator != "=>";
                    if (kind == "delegate") emit = emit && IsCallable(first);
                    scope = kind == "delegate" ? new Scope(ScopeKind.Body) : new Scope(kind == "enum" ? ScopeKind.Enum : kind == "interface" ? ScopeKind.Interface : ScopeKind.Type,
                        nameIndex < _candidate.Count ? _candidate[nameIndex].Text : "");
                }
                else if (_language == Language.JavaScript && kind == "type")
                {
                    emit = terminator == "=" && first + 1 < _candidate.Count && IsIdentifier(_candidate[first + 1].Text);
                }
                else if (InsideType || _language == Language.JavaScript || _language == Language.Cpp)
                {
                    // C# fields are intentionally absent, except events. Declarations with bodies, expression-bodied
                    // properties/indexers, and method prototypes are included; calls inside bodies are never candidates.
                    emit = IsCallable(first) || (InsideType && Contains("event")) ||
                           (InsideType && (terminator == "{" || terminator == "=>") && IsProperty(first));
                    if (terminator == "=" && !Contains("event")) emit = false;
                }

                if (emit)
                {
                    if (_output.NeedsSignature) _output.Add(_candidate[0].Line, _symbolDepth + _fileNamespaceDepth, Signature());
                    else _output.Omit();
                    return scope;
                }

                return null;
            }

            private int FirstDeclarationToken()
            {
                var first = 0;
                // Header templates precede C++ declarations, but their type parameters are not symbols.
                if (_language == Language.Cpp && _candidate[0].Text == "template")
                {
                    first = 1;
                    var angle = 0;
                    while (first < _candidate.Count)
                    {
                        if (_candidate[first].Text == "<") angle++;
                        else if (_candidate[first].Text == ">" && --angle == 0) { first++; break; }
                        first++;
                    }
                }

                while (first < _candidate.Count && IsModifier(_candidate[first].Text)) first++;
                return first;
            }

            private bool IsCallable(int first)
            {
                if (_candidate[first].Text == "using" || _candidate[first].Text == "return" || _candidate[first].Text == "new") return false;
                var paren = 0;
                var bracket = 0;
                for (var i = first; i < _candidate.Count; i++)
                {
                    var text = _candidate[i].Text;
                    if (text == "[") bracket++;
                    else if (text == "]") bracket--;
                    else if (text == "(")
                    {
                        if (paren == 0 && bracket == 0 && i > first)
                        {
                            var name = i - 1;
                            if (_candidate[name].Text == ">")
                            {
                                var angle = 1;
                                while (--name >= first)
                                {
                                    if (_candidate[name].Text == ">") angle++;
                                    else if (_candidate[name].Text == "<" && --angle == 0) { name--; break; }
                                }
                            }

                            if (Contains("operator")) return true;
                            if (name >= first && IsIdentifier(_candidate[name].Text) && !IsControlWord(_candidate[name].Text))
                            {
                                // Root C# top-level invocations are not declarations. TS/JS named functions and C++
                                // prototypes require a return type / function keyword; class constructors need neither.
                                if (InsideType || name > first || _candidate[first].Text == "function") return true;
                            }
                        }
                        paren++;
                    }
                    else if (text == ")") paren--;
                }

                return false;
            }

            private bool IsProperty(int first)
            {
                if (Contains("=") || _candidate.Count - first < 2) return false;
                var start = _candidate[first].Text;
                if (IsControlWord(start) || start == "get" || start == "set" || start == "add" || start == "remove" || start == "using" || start == "return") return false;
                if (Contains("this") && Contains("[")) return true;
                return IsIdentifier(_candidate[_candidate.Count - 1].Text) || _candidate[_candidate.Count - 1].Text == "?";
            }

            private bool Contains(string value)
            {
                foreach (var token in _candidate) if (token.Text == value) return true;
                return false;
            }

            private string Signature()
            {
                var builder = new StringBuilder();
                foreach (var token in _candidate)
                {
                    if (builder.Length > MaxSignatureCharacters) break;
                    if (builder.Length > 0 && token.Gap) builder.Append(' ');
                    builder.Append(token.Text);
                }

                return builder.ToString();
            }
        }

        private static bool IsAccess(string value) => value == "public" || value == "private" || value == "protected";

        private static bool IsModifier(string value)
        {
            switch (value)
            {
                case "public": case "private": case "protected": case "internal": case "static": case "abstract": case "sealed":
                case "partial": case "unsafe": case "readonly": case "ref": case "new": case "required": case "extern":
                case "virtual": case "override": case "async": case "volatile": case "scoped": case "file": case "export":
                case "default": case "declare": case "inline": case "constexpr": case "consteval": case "friend": case "explicit":
                    return true;
                default: return false;
            }
        }

        private static bool IsControlWord(string value)
        {
            switch (value)
            {
                case "if": case "else": case "while": case "for": case "foreach": case "switch": case "catch": case "using":
                case "lock": case "fixed": case "do": case "try": case "finally": case "checked": case "unchecked": case "nameof":
                case "typeof": case "sizeof": case "return": case "throw": case "new":
                    return true;
                default: return false;
            }
        }

        private static bool IsIdentifier(string text) => text.Length > 0 && (char.IsLetter(text[0]) || text[0] == '_' || text[0] == '@' || text[0] == '$');
        private static bool IsIdentifierPart(char c) => char.IsLetterOrDigit(c) || c == '_' || c == '$';

        /// <summary>
        /// Streaming lexical scanner over the supplied lines. It does not tokenize method bodies (only structural
        /// punctuation), avoiding allocations proportional to statement count. Recursive literal/interpolation skipping
        /// has a fixed depth limit; excessively nested or unterminated literals conservatively hide the remaining text.
        /// </summary>
        private sealed class Lexer
        {
            private readonly IReadOnlyList<string> _lines;
            private readonly Language _language;
            private int _line;
            private int _offset;
            private bool _gap = true;
            private bool _lineHasCode;
            private bool _regexMayStart = true;

            public Lexer(IReadOnlyList<string> lines, Language language) { _lines = lines; _language = language; }
            private bool End => _line >= _lines.Count;
            private char Current => Peek(0);
            private char Peek(int ahead)
            {
                if (End) return '\0';
                var offset = _offset + ahead;
                return offset < _lines[_line].Length ? _lines[_line][offset] : offset == _lines[_line].Length ? '\n' : '\0';
            }

            private void Advance()
            {
                if (End) return;
                if (_offset == _lines[_line].Length) { _line++; _offset = 0; _lineHasCode = false; }
                else _offset++;
            }

            private void Advance(int count) { for (var i = 0; i < count; i++) Advance(); }
            private int Run(char c) { var count = 0; while (Peek(count) == c) count++; return count; }

            public bool Next(bool capture, out Token token)
            {
                while (!End)
                {
                    if (!capture)
                    {
                        var structural = FastBodyToken();
                        if (structural != '\0')
                        {
                            token = new Token(structural.ToString(), _line + 1, true);
                            _offset++;
                            _lineHasCode = true;
                            _regexMayStart = structural != '}';
                            return true;
                        }

                        if (End) break;
                    }

                    var c = Current;
                    if (char.IsWhiteSpace(c)) { _gap = true; Advance(); continue; }
                    if (c == '/' && Peek(1) == '/') { SkipLine(); _gap = true; continue; }
                    if (c == '/' && Peek(1) == '*') { SkipComment(); _gap = true; continue; }
                    if (c == '#' && !_lineHasCode && (_language != Language.JavaScript || (_line == 0 && Peek(1) == '!')))
                    {
                        SkipDirective();
                        _gap = true;
                        continue;
                    }
                    var line = _line + 1;
                    if (TrySkipLiteral(0))
                    {
                        _lineHasCode = true;
                        _regexMayStart = false;
                        if (!capture) continue;
                        token = new Token("\"…\"", line, _gap);
                        _gap = false;
                        return true;
                    }

                    if (_language == Language.JavaScript && c == '/' && _regexMayStart)
                    {
                        SkipRegex();
                        _lineHasCode = true;
                        _regexMayStart = false;
                        if (!capture) continue;
                        token = new Token("/…/", line, _gap);
                        _gap = false;
                        return true;
                    }

                    _lineHasCode = true;
                    if (IsIdentifierPart(c) || (_language == Language.CSharp && c == '@' && IsIdentifierPart(Peek(1))))
                    {
                        var start = _offset;
                        var number = c >= '0' && c <= '9';
                        Advance();
                        while (IsIdentifierPart(Current) || (number && _language == Language.Cpp && Current == '\'' && IsIdentifierPart(Peek(1)))) Advance();
                        var length = _offset - start;
                        var source = _lines[line - 1];
                        _regexMayStart = RangeEquals(source, start, length, "return") || RangeEquals(source, start, length, "throw") || RangeEquals(source, start, length, "case") || RangeEquals(source, start, length, "yield");
                        if (!capture) continue;
                        token = new Token(source.Substring(start, Math.Min(length, MaxCandidateCharacters)), line, _gap);
                        _gap = false;
                        return true;
                    }

                    if (!capture && c != '{' && c != '}' && c != ';')
                    {
                        _regexMayStart = c == '=' || c == '(' || c == '[' || c == ',' || c == ':' || c == '!' || c == '?';
                        Advance();
                        continue;
                    }

                    var text = c == '=' && Peek(1) == '>' ? "=>" : c == ':' && Peek(1) == ':' ? "::" :
                        (c == '=' || c == '!' || c == '<' || c == '>') && Peek(1) == '=' ? c.ToString() + "=" : c.ToString();
                    Advance(text.Length);
                    _regexMayStart = c == '=' || c == '(' || c == '[' || c == ',' || c == ':' || c == ';' || c == '!' || c == '?' || c == '{' || text == "=>";
                    token = new Token(text, line, _gap);
                    _gap = false;
                    return true;
                }

                token = default;
                return false;
            }

            /// <summary>Skip ordinary body characters in a direct per-line loop; stop at structure or a lexical hazard.</summary>
            private char FastBodyToken()
            {
                while (!End)
                {
                    var source = _lines[_line];
                    var offset = _offset;
                    var code = _lineHasCode;
                    while (offset < source.Length)
                    {
                        var c = source[offset];
                        if (c == '{' || c == '}' || c == ';')
                        {
                            _offset = offset;
                            _lineHasCode = code;
                            return c;
                        }

                        if (_language == Language.Cpp && c == '\'' && offset > 0 && offset + 1 < source.Length &&
                            IsIdentifierPart(source[offset - 1]) && IsIdentifierPart(source[offset + 1]))
                        {
                            offset++;
                            continue;
                        }

                        if (c == '/' || c == '"' || c == '\'' || c == '`' || c == '$' || c == '@' || (c == '#' && !code) ||
                            (_language == Language.Cpp && c == 'R') || (_language == Language.JavaScript && IsIdentifierPart(c)))
                        {
                            _offset = offset;
                            _lineHasCode = code;
                            return '\0';
                        }

                        if (c != ' ' && c != '\t')
                        {
                            code = true;
                            _regexMayStart = c == '=' || c == '(' || c == '[' || c == ',' || c == ':' || c == '!' || c == '?';
                        }

                        offset++;
                    }

                    _line++;
                    _offset = 0;
                    _lineHasCode = false;
                    _gap = true;
                }

                return '\0';
            }

            private static bool RangeEquals(string source, int start, int length, string value) => length == value.Length && string.CompareOrdinal(source, start, value, 0, length) == 0;

            private void SkipLine() { if (!End) _offset = _lines[_line].Length; }
            private void SkipComment()
            {
                Advance(2);
                while (!End)
                {
                    if (Current == '*' && Peek(1) == '/') { Advance(2); return; }
                    Advance();
                }
            }

            private void SkipDirective()
            {
                var continuation = true;
                while (!End && continuation)
                {
                    SkipLine();
                    continuation = _language == Language.Cpp && _lines[_line].TrimEnd().EndsWith("\\", StringComparison.Ordinal);
                    Advance();
                }
            }

            private bool TrySkipLiteral(int depth)
            {
                if (depth > 32) { _line = _lines.Count; return true; }
                var c = Current;
                if (_language == Language.Cpp && c == 'R' && Peek(1) == '"')
                {
                    SkipCppRaw();
                    return true;
                }

                if (c == '\'') { SkipString('\'', false, 0, 0, depth); return true; }
                if (_language == Language.JavaScript && c == '`') { SkipString('`', false, 1, 0, depth); return true; }
                var prefix = 0;
                var dollars = 0;
                var verbatim = false;
                if (_language == Language.CSharp)
                {
                    while (Peek(prefix) == '$') { dollars++; prefix++; }
                    if (Peek(prefix) == '@')
                    {
                        verbatim = true;
                        prefix++;
                        if (dollars == 0 && Peek(prefix) == '$') { dollars = 1; prefix++; }
                    }
                }

                if (Peek(prefix) != '"') return false;
                Advance(prefix);
                var quotes = _language == Language.CSharp && !verbatim ? Run('"') : 1;
                SkipString('"', verbatim, dollars, quotes >= 3 ? quotes : 0, depth);
                return true;
            }

            private void SkipString(char quote, bool verbatim, int dollars, int rawQuotes, int depth)
            {
                Advance(rawQuotes > 0 ? rawQuotes : 1);
                while (!End)
                {
                    if (rawQuotes > 0)
                    {
                        if (Current == '"' && Run('"') >= rawQuotes) { Advance(rawQuotes); return; }
                    }
                    else if (Current == quote)
                    {
                        Advance();
                        if (verbatim && Current == quote) { Advance(); continue; }
                        return;
                    }
                    else if (!verbatim && Current == '\\')
                    {
                        Advance();
                        Advance();
                        continue;
                    }

                    if (dollars > 0)
                    {
                        if (quote == '`' && Current == '$' && Peek(1) == '{')
                        {
                            Advance(2);
                            SkipInterpolation(1, depth + 1);
                            continue;
                        }

                        if (quote != '`' && Current == '{')
                        {
                            var braces = Run('{');
                            if (rawQuotes > 0)
                            {
                                Advance(braces);
                                if (braces >= dollars) SkipInterpolation(dollars, depth + 1);
                                continue;
                            }

                            if (braces >= 2) { Advance(2); continue; }
                            Advance();
                            SkipInterpolation(1, depth + 1);
                            continue;
                        }
                    }

                    Advance();
                }
            }

            private void SkipInterpolation(int closingBraces, int depth)
            {
                if (depth > 32) { _line = _lines.Count; return; }
                var braces = 0;
                while (!End)
                {
                    if (Current == '/' && Peek(1) == '/') { SkipLine(); continue; }
                    if (Current == '/' && Peek(1) == '*') { SkipComment(); continue; }
                    if (TrySkipLiteral(depth)) continue;
                    if (Current == '{') braces++;
                    else if (Current == '}')
                    {
                        if (braces == 0 && Run('}') >= closingBraces) { Advance(closingBraces); return; }
                        braces = Math.Max(0, braces - 1);
                    }

                    Advance();
                }
            }

            private void SkipCppRaw()
            {
                Advance(2);
                var delimiter = new StringBuilder();
                while (!End && Current != '(' && Current != '\n' && delimiter.Length <= 16) { delimiter.Append(Current); Advance(); }
                if (Current != '(') { SkipLine(); return; }
                Advance();
                var suffix = delimiter.ToString() + "\"";
                while (!End)
                {
                    if (Current == ')')
                    {
                        var match = true;
                        for (var i = 0; i < suffix.Length; i++) if (Peek(i + 1) != suffix[i]) { match = false; break; }
                        if (match) { Advance(suffix.Length + 1); return; }
                    }

                    Advance();
                }
            }

            private void SkipRegex()
            {
                Advance();
                var inClass = false;
                while (!End && Current != '\n')
                {
                    if (Current == '\\') { Advance(2); continue; }
                    if (Current == '[') inClass = true;
                    else if (Current == ']') inClass = false;
                    else if (Current == '/' && !inClass) { Advance(); while (char.IsLetter(Current)) Advance(); return; }
                    Advance();
                }
            }
        }

        private static void ScanVisualBasic(IReadOnlyList<string> lines, OutlineWriter output)
        {
            var scopes = new List<Scope>();
            var candidate = new StringBuilder();
            var startLine = 1;
            var attributeDepth = 0;
            var parens = 0;
            var pendingProperty = false;
            var inString = false;
            for (var index = 0; index < lines.Count; index++)
            {
                var cleaned = CleanVisualBasicLine(lines[index], ref attributeDepth, ref inString).Trim();
                if (cleaned.Length == 0 || cleaned[0] == '#' || cleaned.Equals("REM", StringComparison.OrdinalIgnoreCase) || cleaned.StartsWith("REM ", StringComparison.OrdinalIgnoreCase)) continue;
                if (candidate.Length == 0) startLine = index + 1;
                var continued = cleaned.EndsWith("_", StringComparison.Ordinal);
                if (continued) cleaned = cleaned.Substring(0, cleaned.Length - 1).TrimEnd();
                foreach (var c in cleaned) { if (c == '(') parens++; else if (c == ')') parens--; }
                if (candidate.Length < MaxCandidateCharacters)
                {
                    if (candidate.Length > 0) candidate.Append(' ');
                    candidate.Append(cleaned, 0, Math.Min(cleaned.Length, MaxCandidateCharacters - candidate.Length));
                }

                if (continued || parens > 0) continue;
                var signature = candidate.ToString();
                candidate.Clear();
                parens = 0;
                var words = signature.Split(new[] { ' ', '\t', '(' }, StringSplitOptions.RemoveEmptyEntries);
                var first = 0;
                while (first < words.Length && IsVisualBasicModifier(words[first])) first++;
                if (first == words.Length) continue;
                var kind = words[first].ToLowerInvariant();
                if (pendingProperty)
                {
                    pendingProperty = false;
                    if (kind != "get" && kind != "set" && scopes.Count > 0) scopes.RemoveAt(scopes.Count - 1);
                }

                if (kind == "end" && first + 1 < words.Length)
                {
                    var ending = words[first + 1].ToLowerInvariant();
                    for (var i = scopes.Count - 1; i >= 0; i--)
                    {
                        if (scopes[i].Name == ending) { scopes.RemoveRange(i, scopes.Count - i); break; }
                    }

                    continue;
                }

                if (scopes.Count > 0 && (scopes[scopes.Count - 1].Kind == ScopeKind.Body || scopes[scopes.Count - 1].Kind == ScopeKind.Property || scopes[scopes.Count - 1].Kind == ScopeKind.Enum)) continue;
                var scopeKind = ScopeKind.Body;
                switch (kind)
                {
                    case "namespace": scopeKind = ScopeKind.Namespace; break;
                    case "class": case "module": case "structure": scopeKind = ScopeKind.Type; break;
                    case "interface": scopeKind = ScopeKind.Interface; break;
                    case "enum": scopeKind = ScopeKind.Enum; break;
                    case "sub": case "function": break;
                    case "property": scopeKind = ScopeKind.Property; break;
                    default: continue;
                }

                if (first + 1 >= words.Length) continue;
                var depth = 0;
                foreach (var scope in scopes) if (scope.IsSymbol) depth++;
                output.Add(startLine, depth, signature);
                var inInterface = scopes.Count > 0 && scopes[scopes.Count - 1].Kind == ScopeKind.Interface;
                if (scopeKind != ScopeKind.Body && scopeKind != ScopeKind.Property ||
                    (!inInterface && signature.IndexOf("MustOverride", StringComparison.OrdinalIgnoreCase) < 0))
                {
                    scopes.Add(new Scope(scopeKind, kind));
                    pendingProperty = scopeKind == ScopeKind.Property;
                }
            }
        }

        private static bool IsVisualBasicModifier(string value)
        {
            switch (value.ToLowerInvariant())
            {
                case "public": case "private": case "protected": case "friend": case "partial": case "shared": case "static":
                case "readonly": case "writeonly": case "default": case "async": case "iterator": case "shadows": case "overloads":
                case "overrides": case "overridable": case "mustoverride": case "mustinherit": case "notinheritable": case "notoverridable":
                    return true;
                default: return false;
            }
        }

        private static string CleanVisualBasicLine(string line, ref int attributeDepth, ref bool inString)
        {
            var builder = new StringBuilder();
            var firstCode = true;
            for (var i = 0; i < line.Length; i++)
            {
                var c = line[i];
                if (inString)
                {
                    if (c == '"')
                    {
                        if (i + 1 < line.Length && line[i + 1] == '"') i++;
                        else inString = false;
                    }

                    continue;
                }

                if (c == '\'') break;
                if (c == '"')
                {
                    if (attributeDepth == 0) builder.Append("\"…\"");
                    inString = true;
                    firstCode = false;
                    continue;
                }

                if (c == '<' && (firstCode || attributeDepth > 0)) { attributeDepth++; continue; }
                if (attributeDepth > 0) { if (c == '>') attributeDepth--; continue; }
                if (!char.IsWhiteSpace(c)) firstCode = false;
                builder.Append(c);
            }

            return builder.ToString();
        }
    }
}
