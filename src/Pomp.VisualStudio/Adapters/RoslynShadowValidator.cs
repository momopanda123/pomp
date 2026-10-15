using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Text;
using Microsoft.CodeAnalysis.Diagnostics;
using Microsoft.VisualStudio.LanguageServices;
using Microsoft.VisualStudio.Threading;
using Pomp.Features;
using Task = System.Threading.Tasks.Task;

namespace Pomp.VisualStudio.Adapters
{
    /// <summary>Compiles an immutable Roslyn solution branch. Never applies the branch to the live workspace or disk.</summary>
    internal sealed class RoslynShadowValidator : ISourceGeneratorShadowValidator
    {
        private readonly VsAdapterContext context;
        private readonly bool allowSourceGenerators;
        public RoslynShadowValidator(VsAdapterContext context, bool allowSourceGenerators = false) { this.context = context; this.allowSourceGenerators = allowSourceGenerators; }
        public IShadowValidator WithSourceGenerators(bool enabled) => new RoslynShadowValidator(context, enabled);
        public async Task<ShadowValidation> ValidateAsync(ShadowSnapshot snapshot, CancellationToken cancellationToken)
        {
            await context.Jtf.SwitchToMainThreadAsync(cancellationToken);
            var workspace = context.ComponentModel.GetService<VisualStudioWorkspace>();
            if (workspace == null) return Unsupported("Visual Studio's Roslyn workspace is unavailable.");
            var original = workspace.CurrentSolution;
            var solution = original;
            await TaskScheduler.Default;
            if (!allowSourceGenerators)
                foreach (var project in solution.Projects.ToArray())
                    solution = solution.WithProjectAnalyzerReferences(project.Id, Array.Empty<AnalyzerReference>());
            var affected = new HashSet<ProjectId>();
            var diagnostics = new List<ShadowDiagnostic>();
            foreach (var file in snapshot.Files)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var ids = original.GetDocumentIdsWithFilePath(file.Path);
                if (ids.Length == 0) return Unsupported("The file is not a loaded Roslyn source document: " + file.Path);
                foreach (var id in ids)
                {
                    var document = original.GetDocument(id);
                    if (document == null) return Unsupported("A source document disappeared while preparing validation.");
                    var source = await document.GetTextAsync(cancellationToken).ConfigureAwait(false);
                    if (!string.Equals(source.ToString(), file.Before, StringComparison.Ordinal))
                        return Unsupported("The live source differs from the reviewed snapshot: " + file.Path);
                    solution = solution.WithDocumentText(id, SourceText.From(file.After, source.Encoding));
                    affected.Add(id.ProjectId);
                }
            }
            diagnostics.Add(new ShadowDiagnostic(allowSourceGenerators ? Pomp.Hosting.PompLanguage.Pick("소스 생성기를 포함해 검사했어요.", "Checked with source generators.") : Pomp.Hosting.PompLanguage.Pick("소스 생성기는 검사에서 제외했어요.", "Source generators were excluded from the check."), false));
            var graph = solution.GetProjectDependencyGraph();
            foreach (var id in affected.ToArray())
                foreach (var dependent in graph.GetProjectsThatTransitivelyDependOnThisProject(id)) affected.Add(dependent);
            if (affected.Count == 0) return Unsupported("No changed source projects to validate.");
            foreach (var id in graph.GetTopologicallySortedProjects(cancellationToken).Where(affected.Contains))
            {
                var project = solution.GetProject(id);
                if (project == null || !project.SupportsCompilation) return Unsupported("A changed/dependent project does not support Roslyn compilation.");
                var compilation = await project.GetCompilationAsync(cancellationToken).ConfigureAwait(false);
                if (compilation == null) return Unsupported("The project compilation is not ready: " + project.Name);
                foreach (var diagnostic in compilation.GetDiagnostics(cancellationToken))
                {
                    if (diagnostic.Severity != DiagnosticSeverity.Error) continue;
                    var location = diagnostic.Location.GetLineSpan();
                    diagnostics.Add(new ShadowDiagnostic(project.Name + ": " + diagnostic.Id + ": " + diagnostic.GetMessage(), true,
                        location.Path, diagnostic.Location.IsInSource ? location.StartLinePosition.Line + 1 : (int?)null));
                    if (diagnostics.Count >= 200)
                    {
                        diagnostics.Add(new ShadowDiagnostic("Additional compiler errors omitted; application remains blocked.", true));
                        return new ShadowValidation(true, diagnostics);
                    }
                }
            }
            diagnostics.Add(new ShadowDiagnostic("Roslyn compiler diagnostics only. No separate MSBuild target, analyzer pass or runtime test was requested.", false));
            return new ShadowValidation(true, diagnostics);
        }
        private static ShadowValidation Unsupported(string message) => new ShadowValidation(false, new[] { new ShadowDiagnostic(message, true) });
    }
}
