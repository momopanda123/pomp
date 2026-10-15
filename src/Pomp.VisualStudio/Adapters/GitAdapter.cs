using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.VisualStudio.Threading;
using Pomp.Adapters;
using Task = System.Threading.Tasks.Task;

namespace Pomp.VisualStudio.Adapters
{
    /// <summary>Git CLI in the same solution/open-folder/fallback workspace as the host tools.</summary>
    internal sealed class GitAdapter : IGitAdapter
    {
        private readonly VsAdapterContext context;
        private readonly SemaphoreSlim writes = new SemaphoreSlim(1, 1);

        public GitAdapter(VsAdapterContext context)
        {
            this.context = context;
        }

        public async Task<GitStatus> GetStatusAsync(CancellationToken cancellationToken)
        {
            var repository = await OpenAsync(cancellationToken).ConfigureAwait(false);
            return repository == null ? new GitStatus { IsRepository = false } : await repository.GetStatusAsync(cancellationToken).ConfigureAwait(false);
        }

        public async Task<GitDiffResult> GetDiffAsync(GitDiffRequest request, CancellationToken cancellationToken)
        {
            var repository = await RequireRepositoryAsync(cancellationToken).ConfigureAwait(false);
            return await repository.GetDiffAsync(request, cancellationToken).ConfigureAwait(false);
        }

        public async Task<GitCommandResult> CommitAsync(GitCommitRequest request, CancellationToken cancellationToken)
        {
            await writes.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                var repository = await RequireRepositoryAsync(cancellationToken).ConfigureAwait(false);
                await VerifyWorkspaceAsync(repository, cancellationToken).ConfigureAwait(false);
                return await repository.CommitAsync(request, cancellationToken).ConfigureAwait(false);
            }
            finally
            {
                writes.Release();
            }
        }

        public async Task<GitCommandResult> PushAsync(GitPushRequest request, CancellationToken cancellationToken)
        {
            await writes.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                var repository = await RequireRepositoryAsync(cancellationToken).ConfigureAwait(false);
                await VerifyWorkspaceAsync(repository, cancellationToken).ConfigureAwait(false);
                return await repository.PushAsync(request, cancellationToken).ConfigureAwait(false);
            }
            finally
            {
                writes.Release();
            }
        }

        /// <summary>The repository of the current workspace, or null outside git (read-only callers such as the editor change source).</summary>
        internal Task<GitRepository?> OpenRepositoryAsync(CancellationToken cancellationToken) => OpenAsync(cancellationToken);

        private async Task<GitRepository?> OpenAsync(CancellationToken cancellationToken)
        {
            // This is the host environment's shared resolver, not an independently inferred solution directory.
            var root = await context.GetWorkspaceRootAsync(cancellationToken).ConfigureAwait(false);
            var executable = await ResolveExecutableAsync(cancellationToken).ConfigureAwait(false);
            return await GitRepository.OpenAsync(new GitCli(executable), root, cancellationToken).ConfigureAwait(false);
        }

        private async Task<GitRepository> RequireRepositoryAsync(CancellationToken cancellationToken)
        {
            return await OpenAsync(cancellationToken).ConfigureAwait(false)
                ?? throw new InvalidOperationException("The current workspace is not inside a git repository.");
        }

        private async Task VerifyWorkspaceAsync(GitRepository repository, CancellationToken cancellationToken)
        {
            await context.Jtf.SwitchToMainThreadAsync(cancellationToken);
            if (!context.IsCurrentWorkspaceRoot(repository.WorkspaceRoot))
            {
                throw new InvalidOperationException("The Visual Studio workspace changed; retry the git operation in the current workspace.");
            }

            await TaskScheduler.Default;
            cancellationToken.ThrowIfCancellationRequested();
        }

        private async Task<string> ResolveExecutableAsync(CancellationToken cancellationToken)
        {
            await TaskScheduler.Default;
            cancellationToken.ThrowIfCancellationRequested();
            var path = GitCli.FindOnPath();
            if (path != null)
            {
                return path;
            }

            // DTE.FullName is the executable of the running VS, not an arbitrary installed version.
            await context.Jtf.SwitchToMainThreadAsync(cancellationToken);
            var devenv = context.Dte.FullName;
            await TaskScheduler.Default;
            cancellationToken.ThrowIfCancellationRequested();
            var ideDirectory = Path.GetDirectoryName(devenv);
            if (ideDirectory != null)
            {
                var bundled = Path.Combine(ideDirectory, "CommonExtensions", "Microsoft", "TeamFoundation", "Team Explorer", "Git", "cmd", "git.exe");
                if (File.Exists(bundled))
                {
                    return bundled;
                }
            }

            throw new InvalidOperationException("git.exe was not found on PATH or in the running Visual Studio's bundled Team Explorer Git. Install Git for Windows or enable the Git component of Visual Studio.");
        }
    }
}
