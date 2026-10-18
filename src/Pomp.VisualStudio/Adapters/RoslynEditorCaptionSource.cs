using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.CodeAnalysis;
using Microsoft.VisualStudio.LanguageServices;
using Microsoft.VisualStudio.Threading;
using Pomp.Features;
using Pomp.Tools;
using Task = System.Threading.Tasks.Task;
namespace Pomp.VisualStudio.Adapters
{
    internal sealed class RoslynEditorCaptionSource : IEditorCaptionLocationSource
    {
        private static readonly HashSet<string> Kinds = new HashSet<string>(StringComparer.Ordinal) {
            "MethodDeclarationSyntax", "ConstructorDeclarationSyntax", "PropertyDeclarationSyntax", "ClassDeclarationSyntax", "StructDeclarationSyntax", "InterfaceDeclarationSyntax", "RecordDeclarationSyntax", "EnumDeclarationSyntax", "LocalFunctionStatementSyntax",
            "MethodBlockSyntax", "PropertyBlockSyntax", "ClassBlockSyntax", "StructureBlockSyntax", "InterfaceBlockSyntax", "ModuleBlockSyntax" };
        private readonly VsAdapterContext context;
        public RoslynEditorCaptionSource(VsAdapterContext context) { this.context = context; }
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
            if (!new PathGuard(root).TryResolve(path, out var file, out _)) return new EditorCaptionSnapshot(true, Array.Empty<EditorCaptionTarget>());
            var document = solution?.GetDocumentIdsWithFilePath(file).Select(id => solution.GetDocument(id)).FirstOrDefault(d => d?.SupportsSyntaxTree == true);
            if (document == null || exactText.Length > 500000) return new EditorCaptionSnapshot(true, Array.Empty<EditorCaptionTarget>());
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
                if (targets.Count == 12) break;
            }
            return new EditorCaptionSnapshot(true, targets.AsReadOnly());
        }
    }
}
