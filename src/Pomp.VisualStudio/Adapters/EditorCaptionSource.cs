using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.CodeAnalysis;
using Microsoft.VisualStudio.LanguageServices;
using Microsoft.VisualStudio.Text.Tagging;
using Microsoft.VisualStudio.Threading;
using Pomp.Features;
using Pomp.Tools;
using Pomp.VisualStudio.Editor;
using Task = System.Threading.Tasks.Task;
namespace Pomp.VisualStudio.Adapters
{
    /// <summary>
    /// Caption targets of an editor file in the workspace folder: Roslyn's syntax tree where the file is a Roslyn document (C#, VB), otherwise
    /// the lexical declaration scanners (<see cref="SourceDeclarations"/>: C/C++, TypeScript/JavaScript, C# outside Roslyn, Python, Java,
    /// Kotlin, Go, Rust, PHP, Ruby, Swift, Scala, Dart, Lua, PowerShell, shell, F#, SQL, R, Objective-C, Perl, Groovy), otherwise the type and
    /// member blocks of the editor's own block structure (<see cref="EditorStructureDeclarations"/>). Files outside the folder have none.
    /// </summary>
    internal sealed class EditorCaptionSource : IEditorCaptionLocationSource
    {
        private static readonly HashSet<string> Kinds = new HashSet<string>(StringComparer.Ordinal) {
            "MethodDeclarationSyntax", "ConstructorDeclarationSyntax", "PropertyDeclarationSyntax", "ClassDeclarationSyntax", "StructDeclarationSyntax", "InterfaceDeclarationSyntax", "RecordDeclarationSyntax", "EnumDeclarationSyntax", "LocalFunctionStatementSyntax",
            "MethodBlockSyntax", "PropertyBlockSyntax", "ClassBlockSyntax", "StructureBlockSyntax", "InterfaceBlockSyntax", "ModuleBlockSyntax" };
        // A query covers the whole file up to 3000 lines (else three screens around the visible one, see CodeCaptionFeature), every declaration of it.
        private const int MaxTargets = 1000;
        private readonly VsAdapterContext context;
        public EditorCaptionSource(VsAdapterContext context) { this.context = context; }
        public Task<EditorCaptionSnapshot> CaptureAsync(string root, string path, string exactText, int firstLine, int lastLine, CancellationToken token)
            => CaptureCoreAsync(root, path, exactText, firstLine, lastLine, null, token);
        public Task<EditorCaptionSnapshot> CaptureLocationAsync(string root, string path, string exactText, int position, CancellationToken token)
            => CaptureCoreAsync(root, path, exactText, 1, int.MaxValue, position, token);
        private async Task<EditorCaptionSnapshot> CaptureCoreAsync(string root, string path, string exactText, int firstLine, int lastLine, int? position, CancellationToken token)
        {
            await context.Jtf.SwitchToMainThreadAsync(token);
            var workspace = context.ComponentModel.GetService<VisualStudioWorkspace>();
            var solution = workspace?.CurrentSolution;
            await TaskScheduler.Default;
            if (!new PathGuard(root).TryResolve(path, out var file, out _) || exactText.Length > 500000) return new EditorCaptionSnapshot(true, Array.Empty<EditorCaptionTarget>());
            var document = solution?.GetDocumentIdsWithFilePath(file).Select(id => solution.GetDocument(id)).FirstOrDefault(d => d?.SupportsSyntaxTree == true);
            if (document == null)
            {
                var declarations = SourceDeclarations.Find(file, exactText) ?? await StructureAsync(file, exactText, token);
                return declarations == null ? new EditorCaptionSnapshot(false, Array.Empty<EditorCaptionTarget>()) : Lexical(declarations, firstLine, lastLine, position);
            }
            var text = await document.GetTextAsync(token).ConfigureAwait(false);
            if (!string.Equals(text.ToString(), exactText, StringComparison.Ordinal)) return new EditorCaptionSnapshot(false, Array.Empty<EditorCaptionTarget>());
            var syntax = await document.GetSyntaxRootAsync(token).ConfigureAwait(false);
            if (syntax == null) return new EditorCaptionSnapshot(true, Array.Empty<EditorCaptionTarget>());
            document.TryGetSemanticModel(out var semantic);
            var targets = new List<EditorCaptionTarget>(); var lines = new HashSet<int>();
            if (position.HasValue && (position.Value < 0 || position.Value >= syntax.FullSpan.End)) return new EditorCaptionSnapshot(true, Array.Empty<EditorCaptionTarget>());
            var nodes = position.HasValue
                ? syntax.FindToken(position.Value).Parent?.AncestorsAndSelf().Where(node => Kinds.Contains(node.GetType().Name)).Take(1) ?? Enumerable.Empty<SyntaxNode>()
                : syntax.DescendantNodes().Take(30000);
            foreach (var node in nodes)
            {
                token.ThrowIfCancellationRequested();
                if (!Kinds.Contains(node.GetType().Name) || node.Span.Length == 0) continue;
                var line = text.Lines.GetLineFromPosition(node.SpanStart).LineNumber + 1;
                if (line < firstLine || line > lastLine || !lines.Add(line)) continue;
                ISymbol? symbol = null;
                if (semantic != null) try { symbol = semantic.GetDeclaredSymbol(node, token); } catch (ArgumentException) { }
                // Without a cached semantic model there is no symbol: the declaration's own first line past its attributes (its
                // signature) names it instead.
                var head = node.DescendantTokens().FirstOrDefault(t => t.Parent?.AncestorsAndSelf().Any(a => a.GetType().Name.EndsWith("AttributeListSyntax", StringComparison.Ordinal)) == false);
                var name = symbol?.ToDisplayString(SymbolDisplayFormat.MinimallyQualifiedFormat)
                    ?? text.Lines.GetLineFromPosition(head.RawKind != 0 ? head.SpanStart : node.SpanStart).ToString().Trim();
                if (name.Length > 120) name = name.Substring(0, 120);
                targets.Add(new EditorCaptionTarget(name, line, node.SpanStart, node.Span.Length));
                if (targets.Count == MaxTargets) break;
            }
            return new EditorCaptionSnapshot(true, targets.AsReadOnly());
        }

        /// <summary>
        /// The editor's block structure of the open buffer of <paramref name="file"/>: none when it is not open, null when the buffer no
        /// longer holds <paramref name="exactText"/>.
        /// </summary>
        private async Task<IReadOnlyList<SourceDeclaration>?> StructureAsync(string file, string exactText, CancellationToken token)
        {
            await context.Jtf.SwitchToMainThreadAsync(token);
            var buffer = DocumentTable.TryGetTextBuffer(context, file);
            if (buffer == null) return Array.Empty<SourceDeclaration>();
            var snapshot = buffer.CurrentSnapshot;
            if (snapshot.Length != exactText.Length || !string.Equals(snapshot.GetText(), exactText, StringComparison.Ordinal)) return null;
            return EditorStructureDeclarations.Find(context.ComponentModel.GetService<IBufferTagAggregatorFactoryService>(), snapshot);
        }

        /// <summary>
        /// The targets among <paramref name="declarations"/> (read from the exact source): every type and member declaration starting in
        /// the lines, or the innermost one around <paramref name="position"/>.
        /// </summary>
        private static EditorCaptionSnapshot Lexical(IReadOnlyList<SourceDeclaration> declarations, int firstLine, int lastLine, int? position)
        {
            var targets = new List<EditorCaptionTarget>(); var lines = new HashSet<int>();
            if (position.HasValue)
            {
                // Source order puts an enclosing declaration before the ones inside it: the last one around the position is the innermost.
                var around = declarations.LastOrDefault(d => d.Start <= position.Value && position.Value < d.Start + d.Length);
                if (around != null) targets.Add(new EditorCaptionTarget(around.Name, around.Line, around.Start, around.Length));
                return new EditorCaptionSnapshot(true, targets.AsReadOnly());
            }
            foreach (var declaration in declarations)
            {
                if (declaration.Line < firstLine || declaration.Line > lastLine || !lines.Add(declaration.Line)) continue;
                targets.Add(new EditorCaptionTarget(declaration.Name, declaration.Line, declaration.Start, declaration.Length));
                if (targets.Count == MaxTargets) break;
            }
            return new EditorCaptionSnapshot(true, targets.AsReadOnly());
        }
    }
}
