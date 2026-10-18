using System.Collections.Generic;
using System.Text;

namespace Pomp.Tools
{
    /// <summary>A type or member declaration found lexically in source text (<see cref="SourceDeclarations"/>).</summary>
    public sealed class SourceDeclaration
    {
        public SourceDeclaration(bool isType, int line, int start, int length, string name)
        {
            IsType = isType; Line = line; Start = start; Length = length; Name = name;
        }

        /// <summary>A class, struct, interface, enum, record, delegate or type alias; otherwise a member (function, method, constructor, operator, property, event).</summary>
        public bool IsType { get; }
        /// <summary>1-based line of <see cref="Start"/>.</summary>
        public int Line { get; }
        /// <summary>Offset of the declaration's first token (attributes excluded, a C++ template header included).</summary>
        public int Start { get; }
        /// <summary>Through the body's closing brace or the terminating ';'.</summary>
        public int Length { get; }
        /// <summary>The signature as written, whitespace collapsed, at most 120 characters.</summary>
        public string Name { get; }
    }

    /// <summary>
    /// Declarations of a source file for editors without a language service model (C/C++, TypeScript/JavaScript, C# outside
    /// Roslyn): the lexical scanner of the symbol outline, not a compiler. Lines break as in the Visual Studio editor
    /// (CR LF, CR, LF, U+0085, U+2028, U+2029), so <see cref="SourceDeclaration.Line"/> is the editor's line number.
    /// </summary>
    public static class SourceDeclarations
    {
        private const int MaxNameCharacters = 120;

        /// <summary>Declarations in source order; null when the file type is not scanned this way (Visual Basic, unknown extensions).</summary>
        public static IReadOnlyList<SourceDeclaration>? Find(string path, string text)
        {
            var lines = new List<string>();
            var starts = new List<int>();
            var lineStart = 0;
            for (var i = 0; i < text.Length; i++)
            {
                var c = text[i];
                if (c != '\r' && c != '\n' && c != '\u0085' && c != '\u2028' && c != '\u2029') continue;
                lines.Add(text.Substring(lineStart, i - lineStart));
                starts.Add(lineStart);
                if (c == '\r' && i + 1 < text.Length && text[i + 1] == '\n') i++;
                lineStart = i + 1;
            }

            lines.Add(text.Substring(lineStart));
            starts.Add(lineStart);
            var found = SymbolOutline.FindDeclarations(path, lines);
            if (found == null) return null;
            var result = new List<SourceDeclaration>(found.Count);
            foreach (var declaration in found)
            {
                var start = starts[declaration.Line - 1] + declaration.Column;
                var end = starts[declaration.EndLine - 1] + declaration.EndColumn;
                if (declaration.TrimEnd) while (end > start && char.IsWhiteSpace(text[end - 1])) end--;
                var head = starts[declaration.HeadLine - 1] + declaration.HeadColumn;
                if (end <= start || head < start || head > end) continue;
                result.Add(new SourceDeclaration(declaration.IsType, declaration.Line, start, end - start, Name(text, start, head)));
            }

            return result;
        }

        private static string Name(string text, int start, int end)
        {
            var name = new StringBuilder();
            var space = false;
            for (var i = start; i < end && name.Length < MaxNameCharacters; i++)
            {
                if (char.IsWhiteSpace(text[i])) { space = name.Length > 0; continue; }
                if (space) { name.Append(' '); space = false; }
                name.Append(text[i]);
            }

            return name.Length <= MaxNameCharacters ? name.ToString() : name.ToString(0, MaxNameCharacters);
        }
    }
}
