using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Text;
using Microsoft.VisualStudio.LanguageServices;
using Microsoft.VisualStudio.Threading;
using Pomp.Adapters;
using Pomp.Features;
using Pomp.Tools;
using Task = System.Threading.Tasks.Task;

namespace Pomp.VisualStudio.Adapters
{
    /// <summary>Indexes immutable Roslyn snapshots; never reads outside the workspace or executes project code.</summary>
    internal sealed class RoslynCodeWorkspaceSource : IObservableCodeWorkspaceSource, IDisposable
    {
        private readonly VsAdapterContext context;
        private readonly SemaphoreSlim operation = new SemaphoreSlim(1, 1);
        private readonly Dictionary<DocumentId, Cached> cache = new Dictionary<DocumentId, Cached>();
        private string cachedRoot = string.Empty;
        private long cacheCharacters;
        private Workspace? observedWorkspace;
        private bool disposed;
        public event EventHandler? Changed;
        private void OnWorkspaceChanged(object? sender, WorkspaceChangeEventArgs args) => Changed?.Invoke(this, EventArgs.Empty);
        public void Dispose() { disposed = true; if (observedWorkspace != null) observedWorkspace.WorkspaceChanged -= OnWorkspaceChanged; observedWorkspace = null; }
        private sealed class Cached
        {
            public string Path = string.Empty;
            public VersionStamp Version;
            public long Characters;
            public IReadOnlyList<CodeChunk> Chunks = Array.Empty<CodeChunk>();
        }
        public RoslynCodeWorkspaceSource(VsAdapterContext context) { this.context = context; }
        public async Task<CodeIndexSnapshot> CaptureAsync(string root, CancellationToken cancellationToken)
        {
            await operation.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                await context.Jtf.SwitchToMainThreadAsync(cancellationToken);
                var workspace = context.ComponentModel.GetService<VisualStudioWorkspace>();
                if (workspace == null) throw new InvalidOperationException("Roslyn workspace is not available.");
                if (!disposed && observedWorkspace != workspace)
                {
                    if (observedWorkspace != null) observedWorkspace.WorkspaceChanged -= OnWorkspaceChanged;
                    observedWorkspace = workspace; workspace.WorkspaceChanged += OnWorkspaceChanged;
                }
                var solution = workspace.CurrentSolution;
                await TaskScheduler.Default;
                var guard = new PathGuard(root);
                if (!string.Equals(root, cachedRoot, StringComparison.OrdinalIgnoreCase)) { cache.Clear(); cacheCharacters = 0; cachedRoot = root; }
                var all = new List<CodeChunk>(); var seen = new HashSet<DocumentId>(); var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                var notices = new List<string>(); int skipped = 0, files = 0; long characters = 0;
                foreach (var document in solution.Projects.OrderBy(p => p.Name, StringComparer.Ordinal).SelectMany(p => p.Documents.OrderBy(d => d.FilePath, StringComparer.OrdinalIgnoreCase)))
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    if (files >= 3000 || all.Count >= 8000 || characters >= 8000000) { notices.Add("Index budget reached; additional source documents are not included."); break; }
                    if (!document.SupportsSyntaxTree || !document.SupportsSemanticModel || document.FilePath == null || !guard.TryResolve(document.FilePath, out var path, out _) || IsGenerated(path) || !paths.Add(path)) { skipped++; continue; }
                    files++; seen.Add(document.Id);
                    var version = await document.GetTextVersionAsync(cancellationToken).ConfigureAwait(false);
                    if (!cache.TryGetValue(document.Id, out var item) || item.Version != version || item.Path != path)
                    {
                        var source = await document.GetTextAsync(cancellationToken).ConfigureAwait(false);
                        item = new Cached { Path = path, Version = version };
                        if (source.Length > 500000) { skipped++; continue; }
                        item.Chunks = await ChunksAsync(document, source, path, cancellationToken).ConfigureAwait(false);
                        item.Characters = item.Chunks.Sum(chunk => (long)chunk.Text.Length);
                        if (cache.TryGetValue(document.Id, out var previous)) cacheCharacters -= previous.Characters;
                        if (cacheCharacters + item.Characters > 12000000) { cache.Clear(); cacheCharacters = 0; }
                        cache[document.Id] = item; cacheCharacters += item.Characters;
                    }
                    foreach (var chunk in item.Chunks)
                    {
                        if (all.Count >= 8000 || characters + chunk.Text.Length > 8000000) { notices.Add("Chunk budget reached; source coverage is partial."); break; }
                        all.Add(chunk); characters += chunk.Text.Length;
                    }
                }
                foreach (var id in cache.Keys.Where(id => !seen.Contains(id)).ToArray()) { cacheCharacters -= cache[id].Characters; cache.Remove(id); }
                if (skipped > 0) notices.Add(skipped + " generated, linked, oversized, outside-root or unsupported documents were skipped.");
                notices.Add("Loaded Roslyn source only; non-Roslyn/unloaded files and generated output are excluded. Very long lines/large declarations may be partially covered by bounded windows. Cached semantic symbols or syntax labels plus BM25; search does not request compilation/source generators. Embedding retrieval, when enabled, is applied separately by the search service.");
                var fingerprint = ContentHash.Compute(root + "\n" + string.Join("\n", all.Select(c => c.Path + "|" + c.Hash + "|" + c.StartLine + "|" + c.EndLine + "|" + c.Symbol)));
                return new CodeIndexSnapshot(fingerprint, all, notices.Distinct());
            }
            finally { operation.Release(); }
        }
        private static bool IsGenerated(string path)
        {
            var parts = path.Replace('\\', '/').Split('/');
            return parts.Any(p => p.Equals("obj", StringComparison.OrdinalIgnoreCase) || p.Equals("bin", StringComparison.OrdinalIgnoreCase) || p.Equals(".agents", StringComparison.OrdinalIgnoreCase) || p.Equals(".pomp", StringComparison.OrdinalIgnoreCase) || p.Equals("node_modules", StringComparison.OrdinalIgnoreCase))
                || path.EndsWith(".g.cs", StringComparison.OrdinalIgnoreCase) || path.EndsWith(".generated.cs", StringComparison.OrdinalIgnoreCase) || path.EndsWith(".designer.cs", StringComparison.OrdinalIgnoreCase);
        }
        private static async Task<IReadOnlyList<CodeChunk>> ChunksAsync(Document document, SourceText source, string path, CancellationToken ct)
        {
            var result = new List<CodeChunk>(); var ranges = new HashSet<string>(); var hash = ContentHash.Compute(source.ToString());
            var syntax = await document.GetSyntaxRootAsync(ct).ConfigureAwait(false);
            document.TryGetSemanticModel(out var semantic); // Never start compilation/generators merely to answer a read-only search.
            if (syntax != null)
            {
                foreach (var node in syntax.DescendantNodes().Take(15000))
                {
                    ct.ThrowIfCancellationRequested();
                    if (result.Count >= 150) break;
                    var kind = node.GetType().Name;
                    if (!(kind.Contains("Declaration") || kind.Contains("Statement") || kind.Contains("Block")) || node.Span.Length > 12000) continue;
                    ISymbol? symbol;
                    try { symbol = semantic?.GetDeclaredSymbol(node, ct); } catch (ArgumentException) { continue; }
                    if (semantic == null)
                    {
                        if (kind.Contains("Declaration") || (kind.Contains("Block") && kind != "BlockSyntax"))
                        {
                            var label = node.ToString().Split(new[] { '\r', '\n' }, 2)[0].Trim();
                            Add(node.Span, "syntax: " + (label.Length <= 160 ? label : label.Substring(0, 160)));
                        }
                        continue;
                    }
                    if (!(symbol is IMethodSymbol || symbol is INamedTypeSymbol || symbol is IPropertySymbol || symbol is IFieldSymbol || symbol is IEventSymbol)) continue;
                    Add(node.Span, symbol.ToDisplayString(SymbolDisplayFormat.MinimallyQualifiedFormat));
                }
            }
            // Ensure symbol-free regions are searchable too, preserving source line citations and bounded text.
            for (var line = 0; line < source.Lines.Count; line += 60)
            {
                ct.ThrowIfCancellationRequested();
                if (result.Count >= 300) break;
                var endLine = Math.Min(source.Lines.Count - 1, line + 71);
                var start = source.Lines[line].Start; var end = source.Lines[endLine].End;
                if (end - start > 12000) end = start + 12000;
                Add(TextSpan.FromBounds(start, end), string.Empty);
            }
            return result.AsReadOnly();
            void Add(TextSpan span, string symbol)
            {
                if (span.Length == 0) return;
                var startLine = source.Lines.GetLineFromPosition(span.Start).LineNumber + 1;
                var endLine = source.Lines.GetLineFromPosition(span.End - 1).LineNumber + 1;
                if (!ranges.Add(span.Start + ":" + span.Length)) return;
                result.Add(new CodeChunk(path, hash, symbol, startLine, endLine, source.ToString(span)));
            }
        }
    }
}
