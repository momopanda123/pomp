using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Threading;
using Pomp.Adapters;
using Pomp.Features;
using Pomp.Tools;
using Pomp.Diagnostics;
using Task = System.Threading.Tasks.Task;

namespace Pomp.VisualStudio.Adapters
{
    /// <summary>Single access point to all Visual Studio adapters (design: adapter factory).</summary>
    internal sealed class VsAdapterFactory : IVsAdapterFactory, IWorkspaceFeatureAdapters, IEditorChangeSource, ICodeIndexAdapters, ICodeReviewAdapters, IEditorCaptionAdapters, IProjectEventAdapters, ICodeSnippetAdapters, IIsolatedExecutionAdapters, IInstalledSkillAdapters, IDisposable
    {
        private readonly SolutionAdapter solution;
        private readonly ProjectEventSource projectEvents;
        private readonly EditorChangeSource editorChanges;

        private VsAdapterFactory(VsAdapterContext context)
        {
            projectEvents = new ProjectEventSource(context);
            InstalledSkills = new InstalledSkillSource(context);
            SnippetCapture = new SnippetCapture(context);
            Editor = new EditorAdapter(context);
            var buffers = new TextBufferAdapter(context);
            TextBuffers = buffers;
            WorkspaceEdits = buffers;
            ShadowValidator = new RoslynShadowValidator(context);
            CodeSource = new RoslynCodeWorkspaceSource(context);
            EditorCaptions = new EditorCaptionSource(context);
            solution = new SolutionAdapter(context);
            Debugger = new DebuggerAdapter(context);
            Bookmarks = new BookmarkAdapter(context);
            var errorList = new ErrorListAdapter(context);
            ErrorList = errorList;
            CodeReview = new RoslynCodeReviewSource(context, errorList, buffers, Editor);
            Build = new BuildAdapter(context, errorList);
            Output = new OutputWindowAdapter(context);
            var git = new GitAdapter(context);
            Git = git;
            editorChanges = new EditorChangeSource(context, git, buffers);
            Tests = new TestAdapter(context);
        }

        public async Task<ShadowFile> CaptureForReviewAsync(string workspaceRoot, string path, string expectedHash, string after, CancellationToken cancellationToken)
        {
            await TaskScheduler.Default;
            var resolved = new PathGuard(workspaceRoot).Resolve(path);
            var snapshot = await TextBuffers.ReadAsync(resolved, cancellationToken).ConfigureAwait(false);
            if (snapshot == null || !string.Equals(snapshot.ContentHash, expectedHash, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("Source changed or is unavailable; read_buffer again before proposing edits: " + path);
            return new ShadowFile(resolved, snapshot.Text, after);
        }

        Task<System.Collections.Generic.IReadOnlyList<EditorChangeEntry>> IEditorChangeSource.ListAsync(string workspaceRoot, CancellationToken cancellationToken) => editorChanges.ListAsync(workspaceRoot, cancellationToken);

        Task<System.Collections.Generic.IReadOnlyList<EditorChangeText>> IEditorChangeSource.ReadAsync(string workspaceRoot, System.Collections.Generic.IReadOnlyList<string> paths, CancellationToken cancellationToken) => editorChanges.ReadAsync(workspaceRoot, paths, cancellationToken);

        public IInstalledSkillSource InstalledSkills { get; }

        public IIsolatedProcessRunner IsolatedRunner { get; } = new Pomp.VisualStudio.Isolation.AppContainerRunner();

        public IShadowValidator ShadowValidator { get; }

        public ICodeWorkspaceSource CodeSource { get; }

        public ICodeReviewSource CodeReview { get; }

        public IEditorCaptionSource EditorCaptions { get; }

        public IProjectEventSource ProjectEvents => projectEvents;

        public ICodeSnippetCapture SnippetCapture { get; }

        public IWorkspaceEditApplier WorkspaceEdits { get; }

        public IEditorAdapter Editor { get; }

        public ITextBufferAdapter TextBuffers { get; }

        public ISolutionAdapter Solution => solution;

        public IDebuggerAdapter Debugger { get; }

        public IBookmarkAdapter Bookmarks { get; }

        public IErrorListAdapter ErrorList { get; }

        public IBuildAdapter Build { get; }

        public IOutputWindowAdapter Output { get; }

        public IGitAdapter Git { get; }

        public ITestAdapter Tests { get; }

        /// <summary>Creates the adapters and connects the ones that listen to Visual Studio events (solution changes).</summary>
        public static async Task<VsAdapterFactory> CreateAsync(
            AsyncPackage package,
            JoinableTaskFactory joinableTaskFactory,
            IPompLogger logger,
            CancellationToken cancellationToken)
        {
            var context = new VsAdapterContext(package, joinableTaskFactory, logger);
            var factory = new VsAdapterFactory(context);
            await factory.solution.InitializeAsync(cancellationToken);
            await factory.projectEvents.InitializeAsync(cancellationToken);
            await ((ErrorListAdapter)factory.ErrorList).InitializeAsync(cancellationToken);
            return factory;
        }

        public void Dispose()
        {
            solution.Dispose();
            projectEvents.Dispose();
            (ErrorList as IDisposable)?.Dispose();
            (CodeSource as IDisposable)?.Dispose();
        }
    }
}
