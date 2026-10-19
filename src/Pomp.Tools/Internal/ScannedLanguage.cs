using System;
using System.Collections.Generic;
using System.IO;
using System.Text.RegularExpressions;

namespace Pomp.Tools
{
    /// <summary>How a quote character opens a literal.</summary>
    internal enum QuoteMode
    {
        /// <summary>Not a literal.</summary>
        None,
        /// <summary>Backslash escapes the next character.</summary>
        Escaped,
        /// <summary>The quote written twice is a quote (SQL, PowerShell single quotes).</summary>
        Doubled,
        /// <summary>No escapes at all (shell single quotes, Go raw strings).</summary>
        Raw,
        /// <summary>A character literal: backslash escapes, and it only counts when it closes on its line (Java, Kotlin, Go).</summary>
        Char,
        /// <summary>A character literal only in the forms 'x' and '\…' (Rust lifetimes and F# type variables are not literals).</summary>
        Short,
        /// <summary>PowerShell double quotes: a backtick escapes, the quote written twice is a quote.</summary>
        PowerShell,
    }

    /// <summary>When '#' starts a comment.</summary>
    internal enum HashComment { None, Anywhere, WordStart, NotAfterDollar, NotAttribute, Directive }

    /// <summary>How the end of a declaration's body is found.</summary>
    internal enum BodyStyle { Braces, Indent, RubyEnd, LuaEnd, Sql }

    internal enum RuleScope
    {
        Any,
        /// <summary>At file level or directly in a type, never inside a function (keywordless declarations look like calls there).</summary>
        OutsideFunction,
        InsideType,
    }

    internal enum RuleBody
    {
        /// <summary>The language's own body style.</summary>
        Default,
        /// <summary>Objective-C <c>@interface</c> / <c>@implementation</c> / <c>@protocol</c> through <c>@end</c>.</summary>
        AtEnd,
        /// <summary>The parentheses of a call that declares (R <c>setClass(…)</c>).</summary>
        Parens,
        /// <summary>R <c>name &lt;- function(…)</c>: a braced body or one expression.</summary>
        RFunction,
        /// <summary>Shell <c>name() { … }</c> or <c>name() ( … )</c>.</summary>
        Shell,
    }

    internal sealed class DeclarationRule
    {
        public DeclarationRule(string pattern, bool isType, RegexOptions options)
        {
            Pattern = new Regex(pattern, options | RegexOptions.CultureInvariant);
            IsType = isType;
        }

        /// <summary>Matched at a statement start (<c>\G</c>) on a masked line. Group <c>s</c> is the declaration's first token; <c>n</c> the name, <c>k</c> the keyword.</summary>
        public Regex Pattern { get; }
        public bool IsType { get; }
        public RuleScope Scope { get; set; }
        /// <summary>Whether a declaration without a body counts (a prototype ending in ';', a newline-terminated head).</summary>
        public bool Bodiless { get; set; } = true;
        /// <summary>A constructor: its name (group <c>n</c>) must be the enclosing type's name.</summary>
        public bool Constructor { get; set; }
        public RuleBody Body { get; set; }
    }

    /// <summary>
    /// Lexical rules of a language the declaration scanner (<see cref="DeclarationScanner"/>) reads: what is a comment or a literal,
    /// where declarations start, and how their bodies end.
    /// </summary>
    internal sealed class ScannedLanguage
    {
        public string Name { get; private set; } = "";
        public string[] LineComments { get; private set; } = Array.Empty<string>();
        public HashComment Hash { get; private set; }
        /// <summary>Open, close; nested when <see cref="NestedComments"/>.</summary>
        public string[] BlockComments { get; private set; } = Array.Empty<string>();
        public bool NestedComments { get; private set; }
        public QuoteMode Double { get; private set; } = QuoteMode.Escaped;
        public QuoteMode Single { get; private set; } = QuoteMode.None;
        public QuoteMode Backtick { get; private set; } = QuoteMode.None;
        public bool DoubleMultiline { get; private set; }
        public bool SingleMultiline { get; private set; }
        public bool BacktickMultiline { get; private set; }
        public string[] Triple { get; private set; } = Array.Empty<string>();
        public bool TripleEscapes { get; private set; }
        /// <summary>Openers of code inside double-quoted strings: <c>${</c> and <c>#{</c> close with '}', <c>$(</c> and <c>\(</c> with ')'.</summary>
        public string[] Interpolation { get; private set; } = Array.Empty<string>();
        public bool SingleInterpolates { get; private set; }
        /// <summary>Shell, Ruby and Perl <c>&lt;&lt;WORD</c>; PHP <c>&lt;&lt;&lt;WORD</c>.</summary>
        public bool Heredoc { get; private set; }
        public bool PhpHeredoc { get; private set; }
        public bool PowerShellHereStrings { get; private set; }
        public bool DollarQuotes { get; private set; }
        public bool SqlBrackets { get; private set; }
        public bool LuaLongBrackets { get; private set; }
        public bool RustRaw { get; private set; }
        public bool SwiftRaw { get; private set; }
        public bool FSharpVerbatim { get; private set; }
        public bool RubyBlockComments { get; private set; }
        public bool PerlPod { get; private set; }
        /// <summary>The escape character outside literals ('\' in shell, '`' in PowerShell); '\0' for none.</summary>
        public char CodeEscape { get; private set; }

        public BodyStyle Body { get; private set; }
        /// <summary>A line break ends a statement unless the line or the next one continues it (Kotlin, Swift, Go, Scala, Groovy, R).</summary>
        public bool NewlineTerminated { get; private set; }
        /// <summary>Declarations may also start after ';', '{' or '}' on a line, not only at its first token.</summary>
        public bool MidLine { get; private set; }
        /// <summary>'=>' starts an expression body.</summary>
        public bool Arrow { get; private set; }
        /// <summary>'=' starts an expression body (Kotlin, Scala, F#).</summary>
        public bool EqualsBody { get; private set; }
        /// <summary>':' ends the head and an indented body follows (Python).</summary>
        public bool ColonBody { get; private set; }
        /// <summary>':' at the end of a line ends the head and an indented body follows (Scala 3).</summary>
        public bool ColonAtLineEnd { get; private set; }
        /// <summary>A head line break is continued by a deeper next line (F#).</summary>
        public bool OffsideHeads { get; private set; }
        public string[] ContinuationWords { get; private set; } = Array.Empty<string>();
        public bool IgnoreCase { get; private set; }
        public DeclarationRule[] Rules { get; private set; } = Array.Empty<DeclarationRule>();

        private RegexOptions Options => IgnoreCase ? RegexOptions.IgnoreCase : RegexOptions.None;

        private DeclarationRule Rule(string pattern, bool isType, RuleScope scope = RuleScope.Any, bool bodiless = true, bool constructor = false, RuleBody body = RuleBody.Default)
            => new DeclarationRule(pattern, isType, Options) { Scope = scope, Bodiless = bodiless, Constructor = constructor, Body = body };

        // Shared fragments. Annotations and attributes precede group s: they are not part of the declaration.
        private const string Annotation = @"(?:@[\w.]+(?:\s*\((?:[^()]|\([^()]*\))*\))?\s+)*";
        private const string Generic = @"<(?:[^<>;{}()=]|<(?:[^<>;{}()=]|<[^<>;{}()=]*>)*>)*>";
        private const string NotKeyword = @"(?!(?:return|new|throw|throws|else|case|goto|assert|yield|await|do|import|package|delete|typedef|using|sizeof|typeof|if|for|foreach|while|switch|catch|synchronized|try|finally|in|is|as|var|let|val|super|this|when|echo|print|not|and|or|defer|go|select|operator)\b)";

        /// <summary><c>Type name(</c> with C-family/Java-family type syntax (generics, arrays, nullable, pointers).</summary>
        private static string TypedFunction(string modifiers) =>
            @"\G" + Annotation + @"(?<s>(?:(?:" + modifiers + @")\s+)*(?:" + Generic + @"\s*)?" + NotKeyword +
            @"[\w$]+(?:\s*\.\s*[\w$]+)*(?:\s*" + Generic + @")?(?:\s*\[\s*\])*\??(?:\s+|\s*\*+\s*)" + NotKeyword + @"(?<n>[\w$]+))(?=\s*\()";

        private static readonly Dictionary<string, Func<ScannedLanguage>> ByExtension = new Dictionary<string, Func<ScannedLanguage>>(StringComparer.OrdinalIgnoreCase)
        {
            [".py"] = Python, [".pyw"] = Python, [".pyi"] = Python,
            [".rb"] = Ruby, [".rake"] = Ruby, [".gemspec"] = Ruby, [".ru"] = Ruby, [".rbw"] = Ruby,
            [".lua"] = Lua,
            [".java"] = Java,
            [".kt"] = Kotlin, [".kts"] = Kotlin,
            [".go"] = Go,
            [".rs"] = Rust,
            [".php"] = Php,
            [".swift"] = Swift,
            [".scala"] = Scala, [".sc"] = Scala,
            [".dart"] = Dart,
            [".groovy"] = Groovy, [".gvy"] = Groovy, [".gradle"] = Groovy,
            [".ps1"] = PowerShell, [".psm1"] = PowerShell,
            [".sh"] = Shell, [".bash"] = Shell, [".zsh"] = Shell, [".ksh"] = Shell, [".bashrc"] = Shell, [".bash_profile"] = Shell, [".zshrc"] = Shell,
            [".fs"] = FSharp, [".fsi"] = FSharp, [".fsx"] = FSharp,
            [".sql"] = Sql,
            [".r"] = R,
            [".m"] = ObjectiveC, [".mm"] = ObjectiveC,
            [".pl"] = Perl, [".pm"] = Perl,
        };

        private static readonly Dictionary<string, Func<ScannedLanguage>> ByFileName = new Dictionary<string, Func<ScannedLanguage>>(StringComparer.OrdinalIgnoreCase)
        {
            ["Rakefile"] = Ruby, ["Gemfile"] = Ruby, ["Podfile"] = Ruby, ["Vagrantfile"] = Ruby, ["Fastfile"] = Ruby, ["Brewfile"] = Ruby,
            ["Jenkinsfile"] = Groovy,
        };

        /// <summary>The language of a file by its extension, its name, or (without an extension) its <c>#!</c> line; null when not scanned this way.</summary>
        public static ScannedLanguage? For(string path, IReadOnlyList<string> lines)
        {
            if (ByExtension.TryGetValue(Path.GetExtension(path), out var create) || ByFileName.TryGetValue(Path.GetFileName(path), out create)) return create();
            if (Path.GetExtension(path).Length > 0 || lines.Count == 0 || !lines[0].StartsWith("#!", StringComparison.Ordinal)) return null;
            var shebang = lines[0];
            if (Regex.IsMatch(shebang, @"[/ ](?:ba|z|k|da)?sh\b")) return Shell();
            if (shebang.Contains("python")) return Python();
            if (shebang.Contains("ruby")) return Ruby();
            if (shebang.Contains("perl")) return Perl();
            if (shebang.Contains("pwsh")) return PowerShell();
            if (shebang.Contains("Rscript")) return R();
            if (shebang.Contains("lua")) return Lua();
            return null;
        }

        private static ScannedLanguage Python()
        {
            var l = new ScannedLanguage
            {
                Name = "Python", Hash = HashComment.Anywhere, Single = QuoteMode.Escaped, Triple = new[] { "\"\"\"", "'''" }, TripleEscapes = true,
                Body = BodyStyle.Indent, ColonBody = true,
            };
            l.Rules = new[]
            {
                l.Rule(@"\G(?<s>class)\s+(?<n>\w+)", isType: true),
                l.Rule(@"\G(?<s>(?:async\s+)?def)\s+\w+", isType: false),
            };
            return l;
        }

        private static ScannedLanguage Ruby()
        {
            var l = new ScannedLanguage
            {
                Name = "Ruby", Hash = HashComment.Anywhere, Single = QuoteMode.Escaped, SingleMultiline = true, DoubleMultiline = true,
                Backtick = QuoteMode.Escaped, BacktickMultiline = true, Interpolation = new[] { "#{" }, Heredoc = true, RubyBlockComments = true,
                Body = BodyStyle.RubyEnd, MidLine = true,
            };
            l.Rules = new[]
            {
                l.Rule(@"\G(?<s>(?<k>class|module)\s+(?!<<)(?<n>[\w:]+))", isType: true),
                l.Rule(@"\G(?<s>(?:(?:private|public|protected|module_function)\s+)?(?<k>def))\s+\S", isType: false),
            };
            return l;
        }

        private static ScannedLanguage Lua()
        {
            var l = new ScannedLanguage
            {
                Name = "Lua", LineComments = new[] { "--" }, Single = QuoteMode.Escaped, LuaLongBrackets = true, Body = BodyStyle.LuaEnd,
            };
            l.Rules = new[]
            {
                l.Rule(@"\G(?<s>(?:local\s+)?(?<k>function)\s+(?<n>[\w.:]+))(?=\s*\()", isType: false),
                l.Rule(@"\G(?<s>(?:local\s+)?(?<n>[\w.]+(?:\[[^\]]*\])?)\s*=\s*(?<k>function))(?=\s*\()", isType: false),
            };
            return l;
        }

        private const string JavaModifiers = "public|protected|private|static|abstract|final|native|synchronized|default|strictfp|transient";

        private static ScannedLanguage Java() => JavaFamily("Java", newlines: false);
        private static ScannedLanguage Groovy() => JavaFamily("Groovy", newlines: true);

        private static ScannedLanguage JavaFamily(string name, bool newlines)
        {
            var groovy = newlines;
            var l = new ScannedLanguage
            {
                Name = name, LineComments = new[] { "//" }, BlockComments = new[] { "/*", "*/" }, Single = groovy ? QuoteMode.Escaped : QuoteMode.Char,
                Triple = groovy ? new[] { "\"\"\"", "'''" } : new[] { "\"\"\"" }, TripleEscapes = true, Interpolation = groovy ? new[] { "${" } : Array.Empty<string>(),
                DoubleMultiline = false, Body = BodyStyle.Braces, MidLine = true, NewlineTerminated = newlines,
            };
            var rules = new List<DeclarationRule>
            {
                l.Rule(@"\G" + Annotation + @"(?<s>(?:(?:public|protected|private|static|abstract|final|sealed|non-sealed|strictfp)\s+)*(?:class|interface|enum|record|trait|@interface)\s+(?<n>[\w$]+))", isType: true),
                l.Rule(TypedFunction(JavaModifiers), isType: false, scope: RuleScope.OutsideFunction),
                l.Rule(@"\G" + Annotation + @"(?<s>(?:(?:public|protected|private)\s+)?(?:" + Generic + @"\s*)?(?<n>[\w$]+))(?=\s*\()", isType: false, scope: RuleScope.InsideType, bodiless: false, constructor: true),
            };
            if (groovy) rules.Insert(1, l.Rule(@"\G" + Annotation + @"(?<s>(?:(?:" + JavaModifiers + @")\s+)*def\s+(?<n>\w+))(?=\s*\()", isType: false, scope: RuleScope.OutsideFunction));
            l.Rules = rules.ToArray();
            return l;
        }

        private const string KotlinModifiers = "public|private|protected|internal|open|final|abstract|sealed|data|enum|annotation|inner|value|inline|companion|override|suspend|tailrec|operator|infix|external|expect|actual|const|lateinit";

        private static ScannedLanguage Kotlin()
        {
            var l = new ScannedLanguage
            {
                Name = "Kotlin", LineComments = new[] { "//" }, BlockComments = new[] { "/*", "*/" }, NestedComments = true, Single = QuoteMode.Char,
                Backtick = QuoteMode.Raw, Triple = new[] { "\"\"\"" }, Interpolation = new[] { "${" },
                Body = BodyStyle.Braces, MidLine = true, NewlineTerminated = true, EqualsBody = true, ContinuationWords = new[] { "where" },
            };
            l.Rules = new[]
            {
                l.Rule(@"\G" + Annotation + @"(?<s>(?:(?:" + KotlinModifiers + @")\s+)*(?:fun\s+interface|class|interface|object)\b(?:\s+(?<n>\w+))?)", isType: true),
                l.Rule(@"\G" + Annotation + @"(?<s>(?:(?:" + KotlinModifiers + @")\s+)*fun)\b", isType: false),
                l.Rule(@"\G" + Annotation + @"(?<s>(?:(?:public|private|protected|internal)\s+)?constructor)(?=\s*\()", isType: false, scope: RuleScope.InsideType),
            };
            return l;
        }

        private static ScannedLanguage Go()
        {
            var l = new ScannedLanguage
            {
                Name = "Go", LineComments = new[] { "//" }, BlockComments = new[] { "/*", "*/" }, Single = QuoteMode.Char, Backtick = QuoteMode.Raw, BacktickMultiline = true,
                Body = BodyStyle.Braces, MidLine = true, NewlineTerminated = true,
            };
            l.Rules = new[]
            {
                l.Rule(@"\G(?<s>type\s+(?<n>\w+))", isType: true),
                l.Rule(@"\G(?<s>func\s*(?:\([^()]*\)\s*)?(?<n>\w+))", isType: false),
            };
            return l;
        }

        private static ScannedLanguage Rust()
        {
            const string attributes = @"(?:#!?\[(?:[^\[\]]|\[[^\[\]]*\])*\]\s*)*";
            const string visibility = @"(?:pub(?:\s*\([^()]*\))?\s+)?";
            var l = new ScannedLanguage
            {
                Name = "Rust", LineComments = new[] { "//" }, BlockComments = new[] { "/*", "*/" }, NestedComments = true, Single = QuoteMode.Short,
                DoubleMultiline = true, RustRaw = true, Body = BodyStyle.Braces, MidLine = true,
            };
            l.Rules = new[]
            {
                l.Rule(@"\G" + attributes + @"(?<s>" + visibility + @"(?:(?:unsafe|default)\s+)*(?:struct|enum|union|trait)\s+(?<n>\w+))", isType: true),
                l.Rule(@"\G" + attributes + @"(?<s>(?:(?:unsafe|default)\s+)*impl)\b", isType: true),
                l.Rule(@"\G" + attributes + @"(?<s>" + visibility + @"mod\s+(?<n>\w+))", isType: true, bodiless: false),
                l.Rule(@"\G" + attributes + @"(?<s>" + visibility + @"(?:(?:const|async|unsafe|default|safe|extern(?:\s+""[^""]*"")?)\s+)*fn\s+(?<n>\w+))", isType: false),
            };
            return l;
        }

        private static ScannedLanguage Php()
        {
            const string attributes = @"(?:#\[(?:[^\[\]]|\[[^\[\]]*\])*\]\s*)*";
            var l = new ScannedLanguage
            {
                Name = "PHP", LineComments = new[] { "//" }, Hash = HashComment.NotAttribute, BlockComments = new[] { "/*", "*/" }, Single = QuoteMode.Escaped,
                SingleMultiline = true, DoubleMultiline = true, Interpolation = new[] { "{$", "${" }, Heredoc = true, PhpHeredoc = true,
                Body = BodyStyle.Braces, MidLine = true,
            };
            l.Rules = new[]
            {
                l.Rule(@"\G" + attributes + @"(?<s>(?:(?:abstract|final|readonly)\s+)*(?:class|interface|trait|enum)\s+(?<n>\w+))", isType: true),
                l.Rule(@"\G" + attributes + @"(?<s>(?:(?:public|private|protected|static|abstract|final)\s+)*function\s+&?(?<n>\w+))(?=\s*\()", isType: false),
            };
            return l;
        }

        private const string SwiftModifiers = @"public|private|fileprivate|internal|package|open|static|class|final|override|mutating|nonmutating|convenience|required|dynamic|optional|indirect|nonisolated|isolated|distributed|prefix|postfix|infix|(?:private|fileprivate|internal|public|package)\s*\(\s*set\s*\)";

        private static ScannedLanguage Swift()
        {
            var l = new ScannedLanguage
            {
                Name = "Swift", LineComments = new[] { "//" }, BlockComments = new[] { "/*", "*/" }, NestedComments = true, Backtick = QuoteMode.Raw,
                Triple = new[] { "\"\"\"" }, TripleEscapes = true, Interpolation = new[] { "\\(" }, SwiftRaw = true,
                Body = BodyStyle.Braces, MidLine = true, NewlineTerminated = true, ContinuationWords = new[] { "where", "throws", "rethrows", "async" },
            };
            l.Rules = new[]
            {
                l.Rule(@"\G" + Annotation + @"(?<s>(?:(?:" + SwiftModifiers + @")\s+)*(?:class|struct|enum|protocol|extension|actor)\s+(?!(?:func|var|let|subscript|init)\b)(?<n>\w+))", isType: true),
                l.Rule(@"\G" + Annotation + @"(?<s>(?:(?:" + SwiftModifiers + @")\s+)*(?:func\b|init\b[?!]?|deinit\b|subscript\b))", isType: false),
            };
            return l;
        }

        private const string ScalaModifiers = @"private(?:\[\w+\])?|protected(?:\[\w+\])?|override|final|sealed|abstract|implicit|lazy|case|open|transparent|inline|opaque|infix|erased";

        private static ScannedLanguage Scala()
        {
            var l = new ScannedLanguage
            {
                Name = "Scala", LineComments = new[] { "//" }, BlockComments = new[] { "/*", "*/" }, NestedComments = true, Single = QuoteMode.Char,
                Backtick = QuoteMode.Raw, Triple = new[] { "\"\"\"" }, Interpolation = new[] { "${" },
                Body = BodyStyle.Braces, MidLine = true, NewlineTerminated = true, EqualsBody = true, ColonAtLineEnd = true,
                ContinuationWords = new[] { "extends", "with", "derives", "(" },
            };
            l.Rules = new[]
            {
                l.Rule(@"\G" + Annotation + @"(?<s>(?:(?:" + ScalaModifiers + @")\s+)*(?:class|trait|object|enum)\s+(?<n>\w+))", isType: true),
                l.Rule(@"\G" + Annotation + @"(?<s>(?:(?:" + ScalaModifiers + @")\s+)*def\s+[^\s(\[:=]+)", isType: false),
            };
            return l;
        }

        private static ScannedLanguage Dart()
        {
            var l = new ScannedLanguage
            {
                Name = "Dart", LineComments = new[] { "//" }, BlockComments = new[] { "/*", "*/" }, NestedComments = true, Single = QuoteMode.Escaped,
                Triple = new[] { "\"\"\"", "'''" }, TripleEscapes = true, Interpolation = new[] { "${" }, SingleInterpolates = true,
                Body = BodyStyle.Braces, MidLine = true, Arrow = true,
            };
            const string type = @"[\w$]+(?:\s*\.\s*[\w$]+)*(?:\s*" + Generic + @")?\??\s+";
            l.Rules = new[]
            {
                l.Rule(@"\G" + Annotation + @"(?<s>(?:(?:abstract|base|final|interface|sealed|mixin)\s+)*(?:class|mixin|enum|extension(?:\s+type)?)\b(?:\s+(?<n>\w+))?)", isType: true),
                l.Rule(@"\G" + Annotation + @"(?<s>(?:(?:static|external)\s+)*(?:" + NotKeyword + type + @")?(?:get|set)\s+\w+)", isType: false, scope: RuleScope.OutsideFunction),
                l.Rule(TypedFunction("static|external"), isType: false, scope: RuleScope.OutsideFunction),
                l.Rule(@"\G" + Annotation + @"(?<s>(?:(?:const|factory|external)\s+)*(?<n>\w+)(?:\.\w+)?)(?=\s*\()", isType: false, scope: RuleScope.InsideType, constructor: true),
            };
            return l;
        }

        private static ScannedLanguage ObjectiveC()
        {
            var l = new ScannedLanguage
            {
                Name = "Objective-C", LineComments = new[] { "//" }, BlockComments = new[] { "/*", "*/" }, Hash = HashComment.Directive, Single = QuoteMode.Char,
                Body = BodyStyle.Braces, MidLine = true,
            };
            l.Rules = new[]
            {
                l.Rule(@"\G(?<s>@(?:interface|implementation|protocol)\s+(?<n>\w+))", isType: true, body: RuleBody.AtEnd),
                l.Rule(@"\G(?<s>[-+]\s*\((?:[^()]|\([^()]*\))*\)\s*\w+)", isType: false, scope: RuleScope.InsideType),
                l.Rule(@"\G(?<s>typedef\s+NS_(?:ENUM|OPTIONS|CLOSED_ENUM|ERROR_ENUM))(?=\s*\()", isType: true, bodiless: false),
                l.Rule(TypedFunction("static|inline|extern|const|unsigned|signed|long|short|struct|enum|union|volatile|register|NS_INLINE|FOUNDATION_EXPORT|FOUNDATION_EXTERN|UIKIT_EXTERN"), isType: false, scope: RuleScope.OutsideFunction),
                l.Rule(@"\G(?<s>(?:typedef\s+)?(?:struct|enum|union)\b(?:\s+(?<n>\w+))?)", isType: true, scope: RuleScope.OutsideFunction, bodiless: false),
            };
            return l;
        }

        private static ScannedLanguage PowerShell()
        {
            var l = new ScannedLanguage
            {
                Name = "PowerShell", Hash = HashComment.WordStart, BlockComments = new[] { "<#", "#>" }, Double = QuoteMode.PowerShell, DoubleMultiline = true,
                Single = QuoteMode.Doubled, SingleMultiline = true, Interpolation = new[] { "$(" }, PowerShellHereStrings = true, CodeEscape = '`',
                Body = BodyStyle.Braces, MidLine = true, IgnoreCase = true,
            };
            l.Rules = new[]
            {
                l.Rule(@"\G(?<s>(?:function|filter|workflow|configuration)\s+(?<n>[\w\-:.]+))", isType: false, bodiless: false),
                l.Rule(@"\G(?<s>(?:class|enum)\s+(?<n>\w+))", isType: true, bodiless: false),
                l.Rule(@"\G(?<s>(?:(?:static|hidden)\s+)*\[[^\]]+\]\s*(?<n>\w+))(?=\s*\()", isType: false, scope: RuleScope.InsideType, bodiless: false),
                l.Rule(@"\G(?<s>(?:(?:static|hidden)\s+)*(?<n>\w+))(?=\s*\()", isType: false, scope: RuleScope.InsideType, bodiless: false, constructor: true),
            };
            return l;
        }

        private static ScannedLanguage Shell()
        {
            var l = new ScannedLanguage
            {
                Name = "Shell", Hash = HashComment.WordStart, Single = QuoteMode.Raw, SingleMultiline = true, DoubleMultiline = true,
                Backtick = QuoteMode.Escaped, BacktickMultiline = true, Interpolation = new[] { "$(", "${" }, Heredoc = true, CodeEscape = '\\',
                Body = BodyStyle.Braces, MidLine = true,
            };
            l.Rules = new[]
            {
                l.Rule(@"\G(?<s>function\s+(?<n>[\w.:\-]+))", isType: false, bodiless: false, body: RuleBody.Shell),
                l.Rule(@"\G(?<s>(?<n>[\w.:\-]+))\s*\(\s*\)", isType: false, bodiless: false, body: RuleBody.Shell),
            };
            return l;
        }

        private static ScannedLanguage FSharp()
        {
            const string attributes = @"(?:\[<(?:[^>]|>(?!\]))*>\]\s*)*";
            const string access = @"(?:(?:private|internal|public)\s+)*";
            var l = new ScannedLanguage
            {
                Name = "F#", LineComments = new[] { "//" }, BlockComments = new[] { "(*", "*)" }, NestedComments = true, Single = QuoteMode.Short,
                DoubleMultiline = true, Triple = new[] { "\"\"\"" }, FSharpVerbatim = true, Backtick = QuoteMode.Raw,
                Body = BodyStyle.Indent, EqualsBody = true, NewlineTerminated = true, OffsideHeads = true,
            };
            l.Rules = new[]
            {
                l.Rule(@"\G" + attributes + @"(?<s>(?:type|module)\s+" + access + @"(?:rec\s+)?(?<n>\w+))", isType: true, bodiless: false),
                l.Rule(@"\G" + attributes + @"(?<s>and\s+" + access + @"(?<n>[A-Z]\w*))(?=\s*(?:<[^=]*>\s*)?(?:\([^=]*\)\s*)?(?:=|$))", isType: true, bodiless: false),
                l.Rule(@"\G" + attributes + @"(?<s>(?:let|and)(?:\s+(?:rec|inline|private|internal|public))*\s+(?!(?:mutable|rec|inline|private|internal|public)\b)(?<n>[\w']+)(?![\w']))(?=\s*(?:<[^=]*>\s*)?[(\w'])", isType: false, bodiless: false),
                l.Rule(@"\G" + attributes + @"(?<s>(?:(?:static|private|internal|public|inline)\s+)*(?:member|override|default)\b(?:\s+(?:val|inline|private|internal|public))*\s+[\w'.]+)", isType: false, bodiless: false),
                l.Rule(@"\G" + attributes + @"(?<s>new)(?=\s*\()", isType: false, scope: RuleScope.InsideType, bodiless: false),
            };
            return l;
        }

        private static ScannedLanguage Sql()
        {
            var l = new ScannedLanguage
            {
                Name = "SQL", LineComments = new[] { "--" }, BlockComments = new[] { "/*", "*/" }, Double = QuoteMode.Doubled, DoubleMultiline = true,
                Single = QuoteMode.Doubled, SingleMultiline = true, Backtick = QuoteMode.Raw, DollarQuotes = true, SqlBrackets = true,
                Body = BodyStyle.Sql, IgnoreCase = true,
            };
            l.Rules = new[]
            {
                l.Rule(@"\G(?<s>(?:CREATE|ALTER)\b(?:\s+OR\s+(?:REPLACE|ALTER))?(?:\s+(?:TEMP|TEMPORARY|MATERIALIZED|SECURE|EDITIONABLE|NONEDITIONABLE|ALGORITHM\s*=\s*\w+|DEFINER\s*=\s*\S+|SQL\s+SECURITY\s+\w+))*\s+(?<k>PROCEDURE|PROC|FUNCTION|VIEW|TRIGGER)\b)", isType: false),
            };
            return l;
        }

        private static ScannedLanguage R()
        {
            var l = new ScannedLanguage
            {
                Name = "R", Hash = HashComment.Anywhere, Single = QuoteMode.Escaped, SingleMultiline = true, DoubleMultiline = true, Backtick = QuoteMode.Raw,
                Body = BodyStyle.Braces, MidLine = true, NewlineTerminated = true,
            };
            l.Rules = new[]
            {
                l.Rule(@"\G(?<s>(?<n>[\w.]+|`[^`]*`)\s*(?:<<-|<-|=)\s*(?:function\b|\\))(?=\s*\()", isType: false, body: RuleBody.RFunction),
                l.Rule(@"\G(?<s>(?:(?<n>[\w.]+)\s*(?:<<-|<-|=)\s*)?(?:R6::)?(?:R6Class|setRefClass|setClass))(?=\s*\()", isType: true, body: RuleBody.Parens),
                l.Rule(@"\G(?<s>(?:methods::)?(?:setMethod|setGeneric|setValidity))(?=\s*\()", isType: false, body: RuleBody.Parens),
            };
            return l;
        }

        private static ScannedLanguage Perl()
        {
            var l = new ScannedLanguage
            {
                Name = "Perl", Hash = HashComment.NotAfterDollar, Single = QuoteMode.Escaped, SingleMultiline = true, DoubleMultiline = true,
                Backtick = QuoteMode.Escaped, BacktickMultiline = true, Heredoc = true, PerlPod = true, Body = BodyStyle.Braces, MidLine = true,
            };
            l.Rules = new[]
            {
                l.Rule(@"\G(?<s>package\s+(?<n>[\w:]+))", isType: true, bodiless: false),
                l.Rule(@"\G(?<s>sub\s+(?<n>[\w:']+))", isType: false, bodiless: false),
            };
            return l;
        }
    }
}
