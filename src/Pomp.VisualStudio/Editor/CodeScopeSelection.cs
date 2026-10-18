using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.CodeAnalysis;
using Microsoft.VisualStudio.ComponentModelHost;
using Microsoft.VisualStudio.LanguageServices;
using Microsoft.VisualStudio.Text;
using Microsoft.VisualStudio.Text.Editor;
using Microsoft.VisualStudio.Threading;
using Pomp.Tools;
using Task = System.Threading.Tasks.Task;

namespace Pomp.VisualStudio.Editor
{
    /// <summary>
    /// What an editor question is about when nothing is selected (Ask POMP, Explain / Review / Fix This Code, Write Tests): the code is
    /// selected in the editor before the question goes out, so the person sees it and the chat's editor context carries exactly it.
    /// A selection the person made is never touched. Otherwise the innermost member around the caret: method, constructor, property,
    /// operator, event, local function through Roslyn in C# and VB; function, method, constructor, operator, property through the lexical
    /// declaration scanner (<see cref="SourceDeclarations"/>) in other files it reads (C/C++, TypeScript/JavaScript). Outside a member, or in a
    /// file neither reads, the caret's line without its indentation. A blank line outside a member selects nothing (the context then names
    /// the caret as before).
    /// </summary>
    internal static class CodeScopeSelection
    {
        // By Roslyn syntax type name, so neither language assembly is referenced (as EditorCaptionSource does). Types are not
        // scopes: a whole class rarely fits the editor context's selected text and is seldom what a question at one line is about.
        private static readonly HashSet<string> Members = new HashSet<string>(StringComparer.Ordinal)
        {
            "MethodDeclarationSyntax", "ConstructorDeclarationSyntax", "DestructorDeclarationSyntax", "OperatorDeclarationSyntax",
            "ConversionOperatorDeclarationSyntax", "PropertyDeclarationSyntax", "IndexerDeclarationSyntax", "EventDeclarationSyntax",
            "LocalFunctionStatementSyntax",
            "MethodBlockSyntax", "ConstructorBlockSyntax", "OperatorBlockSyntax", "PropertyBlockSyntax", "EventBlockSyntax",
        };

        /// <summary>How long the member lookup may take before the caret's line is used instead.</summary>
        private static readonly TimeSpan LookupTimeout = TimeSpan.FromSeconds(1.5);

        /// <summary>Selects the scope in <paramref name="view"/> when it has no selection; on the UI thread when it returns.</summary>
        public static async Task SelectAsync(IWpfTextView view, IComponentModel components, JoinableTaskFactory jtf, CancellationToken cancellationToken)
        {
            await jtf.SwitchToMainThreadAsync(cancellationToken);
            if (view.IsClosed || !view.Selection.IsEmpty) return;
            var snapshot = view.TextSnapshot;
            var caret = view.Caret.Position.BufferPosition;
            if (caret.Snapshot != snapshot) return;
            var line = caret.GetContainingLine();
            var blank = string.IsNullOrWhiteSpace(line.GetText());

            Span? member = null;
            if (snapshot.Length <= 500000 && components.GetService<ITextDocumentFactoryService>().TryGetTextDocument(view.TextBuffer, out var document))
            {
                var solution = components.GetService<VisualStudioWorkspace>()?.CurrentSolution;
                var path = document.FilePath; var text = snapshot.GetText(); var position = caret.Position;
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                timeout.CancelAfter(LookupTimeout);
                try { member = await FindMemberAsync(solution, path, text, position, blank, timeout.Token).ConfigureAwait(false); }
                catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested) { }
                // A failed lookup falls back to the caret's line like a slow one, so the request never names an empty selection.
                catch (Exception ex) when (!(ex is OperationCanceledException)) { }
                await jtf.SwitchToMainThreadAsync(cancellationToken);
                // The person moved on while the lookup answered: their editor wins.
                if (view.IsClosed || view.TextSnapshot != snapshot || !view.Selection.IsEmpty || view.Caret.Position.BufferPosition != caret) return;
            }

            var scope = member ?? (blank ? (Span?)null : TrimmedLine(line));
            if (scope == null || scope.Value.Length == 0) return;
            // The caret stays where the person put it: the Ask POMP prompt opens there and the context still names that line.
            view.Selection.Select(new SnapshotSpan(snapshot, scope.Value), isReversed: false);
        }

        private static async Task<Span?> FindMemberAsync(Solution? solution, string path, string text, int position, bool blankLine, CancellationToken token)
        {
            await TaskScheduler.Default;
            if (string.IsNullOrEmpty(path)) return null;
            var document = solution == null ? null : solution.GetDocumentIdsWithFilePath(path).Select(solution.GetDocument).FirstOrDefault(d => d?.SupportsSyntaxTree == true);
            if (document == null) return LexicalMember(path, text, position);
            var source = await document.GetTextAsync(token).ConfigureAwait(false);
            // The workspace can trail the editor buffer by a moment; positions would not match, so the caret's line is used.
            if (!string.Equals(source.ToString(), text, StringComparison.Ordinal)) return null;
            var root = await document.GetSyntaxRootAsync(token).ConfigureAwait(false);
            if (root == null || position > root.FullSpan.End) return null;
            var node = root.FindToken(position).Parent?.AncestorsAndSelf().FirstOrDefault(candidate => Members.Contains(candidate.GetType().Name));
            if (node == null || node.Span.Length == 0) return null;
            // A blank line above a member belongs to its leading trivia, but it sits between members, not in this one.
            if (blankLine && position < node.SpanStart) return null;
            return new Span(node.SpanStart, node.Span.Length);
        }

        /// <summary>
        /// The innermost member declaration around <paramref name="position"/> by the lexical scanner, from the start of its first line (its
        /// indentation and the caret there belong to it, as Roslyn's leading trivia does) through its closing brace; null outside every member.
        /// </summary>
        private static Span? LexicalMember(string path, string text, int position)
        {
            var declarations = SourceDeclarations.Find(path, text);
            if (declarations == null) return null;
            SourceDeclaration? member = null;
            foreach (var declaration in declarations)
            {
                if (declaration.IsType || position > declaration.Start + declaration.Length) continue;
                var lineStart = declaration.Start;
                while (lineStart > 0 && text[lineStart - 1] != '\n' && text[lineStart - 1] != '\r' && text[lineStart - 1] != '\u0085' && text[lineStart - 1] != '\u2028' && text[lineStart - 1] != '\u2029') lineStart--;
                // Source order puts an enclosing declaration before the ones inside it: the last one around the caret is the innermost.
                if (position >= lineStart) member = declaration;
            }
            return member == null ? (Span?)null : new Span(member.Start, member.Length);
        }

        private static Span TrimmedLine(ITextSnapshotLine line)
        {
            var text = line.GetText();
            var start = 0;
            while (start < text.Length && char.IsWhiteSpace(text[start])) start++;
            var end = text.Length;
            while (end > start && char.IsWhiteSpace(text[end - 1])) end--;
            return Span.FromBounds(line.Start.Position + start, line.Start.Position + end);
        }
    }
}
