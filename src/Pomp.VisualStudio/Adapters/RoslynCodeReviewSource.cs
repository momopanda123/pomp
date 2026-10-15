using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Text;
using Microsoft.VisualStudio.LanguageServices;
using Microsoft.VisualStudio.Threading;
using Pomp.Adapters;
using Pomp.Features;
using Pomp.Hosting;
using Pomp.Tools;
using Task = System.Threading.Tasks.Task;
namespace Pomp.VisualStudio.Adapters
{
    /// <summary>Fresh syntax diagnostics plus existing IDE diagnostics. No build, generator or analyzer execution is requested.</summary>
    internal sealed class RoslynCodeReviewSource : ICodeReviewSource
    {
        private readonly VsAdapterContext context;
        private readonly IErrorListAdapter errors;
        private readonly ITextBufferAdapter buffers;
        private readonly IEditorAdapter editor;
        public RoslynCodeReviewSource(VsAdapterContext context, IErrorListAdapter errors, ITextBufferAdapter buffers, IEditorAdapter editor)
        { this.context = context; this.errors = errors; this.buffers = buffers; this.editor = editor; }
        public async Task<CodeReviewResult> ReviewAsync(string root, string? path, CancellationToken token)
        {
            await context.Jtf.SwitchToMainThreadAsync(token);
            var workspace = context.ComponentModel.GetService<VisualStudioWorkspace>();
            if (workspace == null) throw new InvalidOperationException(PompLanguage.Pick("Roslyn 작업 영역 없음", "Roslyn workspace unavailable"));
            var solution = workspace.CurrentSolution;
            await TaskScheduler.Default;
            var guard = new PathGuard(root); var target = path == null ? null : guard.Resolve(path);
            var findings = new List<ReviewFinding>(); var notices = new List<string>(); var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            int files = 0; long characters = 0;
            foreach (var document in solution.Projects.SelectMany(p => p.Documents))
            {
                token.ThrowIfCancellationRequested();
                if (files >= 2000 || characters > 8000000 || findings.Count >= 150) { notices.Add(PompLanguage.Pick("검토 한도 초과", "Review limit reached")); break; }
                if (!document.SupportsSyntaxTree || !guard.TryResolve(document.FilePath, out var file, out _) || (target != null && !string.Equals(file, target, StringComparison.OrdinalIgnoreCase)) || !seen.Add(file)) continue;
                var text = await document.GetTextAsync(token).ConfigureAwait(false);
                if (text.Length > 500000) { notices.Add(PompLanguage.Pick("큰 소스 제외 · ", "Large source omitted · ") + file); continue; }
                files++; characters += text.Length;
                var tree = await document.GetSyntaxTreeAsync(token).ConfigureAwait(false);
                if (tree == null) continue;
                var hash = ContentHash.Compute(text.ToString());
                foreach (var diagnostic in tree.GetDiagnostics(token).Where(d => d.Severity != DiagnosticSeverity.Hidden).Take(150 - findings.Count))
                {
                    var location = diagnostic.Location.GetLineSpan();
                    findings.Add(new ReviewFinding("syntax", Severity(diagnostic.Severity), diagnostic.Id, Bound(diagnostic.GetMessage(), 1000), file, location.StartLinePosition.Line + 1, hash));
                }
            }
            foreach (var severity in new[] { ErrorSeverity.Error, ErrorSeverity.Warning, ErrorSeverity.Message })
            {
                if (findings.Count >= 200) break;
                var rows = await errors.ReadAsync(new ErrorListFilter { Severities = new List<ErrorSeverity> { severity }, MaxItems = 201 }, token).ConfigureAwait(false);
                foreach (var row in rows.Where(r => r.Severity == severity && !(string.IsNullOrWhiteSpace(r.Description) && string.IsNullOrWhiteSpace(r.Code))))
                {
                    if (findings.Count >= 200) { notices.Add(PompLanguage.Pick("추가 IDE 진단 제외", "Additional IDE diagnostics omitted")); break; }
                    var file = "";
                    if (!string.IsNullOrWhiteSpace(row.File) && !guard.TryResolve(row.File, out file, out _)) continue;
                    if (target != null && !string.Equals(file, target, StringComparison.OrdinalIgnoreCase)) continue;
                    if (findings.Any(f => f.Path == file && f.Line == row.Line && f.Code == row.Code)) continue;
                    findings.Add(new ReviewFinding("ide", severity == ErrorSeverity.Error ? "error" : severity == ErrorSeverity.Warning ? "warning" : "suggestion", row.Code ?? "", Bound(row.Description, 1000), file, row.Line ?? 0));
                }
            }
            notices.Add(PompLanguage.Pick("구문 점검 · 문서 " + files + "개", "Syntax review · " + files + " documents"));
            return new CodeReviewResult(findings, notices.Take(20));
        }
        public async Task<bool> VerifyEvidenceAsync(string root, string path, string hash, int line, string evidence, CancellationToken token)
        {
            await TaskScheduler.Default;
            var file = new PathGuard(root).Resolve(path);
            var snapshot = await buffers.ReadAsync(file, token).ConfigureAwait(false);
            if (snapshot == null || snapshot.ContentHash != hash || snapshot.Text.Length > 1000000 || line <= 0 || string.IsNullOrWhiteSpace(evidence) || evidence.Length > 500) return false;
            var text = SourceText.From(snapshot.Text);
            if (line > text.Lines.Count) return false;
            var start = text.Lines[line - 1].Start; var end = text.Lines[Math.Min(text.Lines.Count - 1, line + 2)].End;
            var fragment = text.ToString(TextSpan.FromBounds(start, Math.Min(end, start + 4000)));
            var offset = fragment.IndexOf(evidence, StringComparison.Ordinal);
            return offset >= 0 && start + offset <= text.Lines[line - 1].End;
        }
        public async Task<bool> NavigateAsync(string root, string path, int line, CancellationToken token)
        {
            await TaskScheduler.Default;
            return await editor.OpenDocumentAsync(new PathGuard(root).Resolve(path), Math.Max(1, line), token).ConfigureAwait(false);
        }
        private static string Severity(DiagnosticSeverity severity) => severity == DiagnosticSeverity.Error ? "error" : severity == DiagnosticSeverity.Warning ? "warning" : "suggestion";
        private static string Bound(string text, int limit) => text.Length <= limit ? text : text.Substring(0, limit) + "…";
    }
}
