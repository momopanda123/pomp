using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Pomp.Adapters;
using Pomp.Tools;

namespace Pomp.VisualStudio.Adapters
{
    /// <summary>Git operations independent of VS, with explicit workspace and repository roots.</summary>
    internal sealed class GitRepository
    {
        private readonly GitCli cli;
        private readonly PathGuard workspace;
        private readonly PathGuard repository;

        private GitRepository(GitCli cli, string workspaceRoot, string repositoryRoot)
        {
            this.cli = cli;
            workspace = new PathGuard(workspaceRoot);
            repository = new PathGuard(repositoryRoot);
        }

        public string WorkspaceRoot => workspace.Root;

        public string RepositoryRoot => repository.Root;

        public static async Task<GitRepository?> OpenAsync(GitCli cli, string workspaceRoot, CancellationToken cancellationToken)
        {
            var result = await cli.RunAsync(workspaceRoot, new[] { "rev-parse", "--show-toplevel" }, GitCli.ReadTimeout, cancellationToken).ConfigureAwait(false);
            if (result.ExitCode != 0)
            {
                if (result.Stderr.IndexOf("not a git repository", StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    return null;
                }

                result.EnsureSucceeded("repository discovery"); // Do not disguise dubious-ownership or inaccessible-repository errors.
            }

            var root = result.Stdout.TrimEnd('\r', '\n');
            if (result.StdoutTruncated || string.IsNullOrWhiteSpace(root) || !Path.IsPathRooted(root))
            {
                throw new InvalidOperationException("Git returned no usable repository root.");
            }

            return new GitRepository(cli, workspaceRoot, root);
        }

        public async Task<GitStatus> GetStatusAsync(CancellationToken cancellationToken)
        {
            var result = await cli.RunAsync(RepositoryRoot, new[] { "status", "--porcelain=v2", "--branch", "-z", "--untracked-files=normal" }, GitCli.ReadTimeout, cancellationToken).ConfigureAwait(false);
            result.EnsureSucceeded("status");
            if (result.StdoutTruncated)
            {
                // GitStatus has no Truncated flag. Never present a partially captured index as a complete status.
                throw new InvalidOperationException("Git status exceeds the 1 MiB capture limit; narrow the repository or inspect it in Visual Studio.");
            }

            return GitStatusParser.Parse(result.Stdout, RepositoryRoot);
        }

        public async Task<GitDiffResult> GetDiffAsync(GitDiffRequest request, CancellationToken cancellationToken)
        {
            if (request == null)
            {
                throw new ArgumentNullException(nameof(request));
            }

            var arguments = new List<string> { "diff" };
            if (request.Staged)
            {
                arguments.Add("--cached");
            }

            arguments.AddRange(new[] { "--no-color", "--no-ext-diff", "--no-textconv" });
            if (request.Path != null)
            {
                arguments.Add("--");
                arguments.Add(ResolvePathspec(request.Path));
            }

            var result = await cli.RunAsync(RepositoryRoot, arguments, GitCli.ReadTimeout, cancellationToken).ConfigureAwait(false);
            result.EnsureSucceeded("diff");
            var maxChars = Math.Max(1, Math.Min(request.MaxChars, GitCli.MaxStreamBytes));
            var truncated = result.StdoutTruncated || result.Stdout.Length > maxChars;
            var diff = result.Stdout;
            if (truncated)
            {
                var cut = diff.LastIndexOf('\n', Math.Min(diff.Length, maxChars) - 1);
                diff = cut < 0 ? string.Empty : diff.Substring(0, cut + 1);
            }

            return new GitDiffResult { Diff = diff, Truncated = truncated };
        }

        public async Task<GitCommandResult> CommitAsync(GitCommitRequest request, CancellationToken cancellationToken)
        {
            if (request == null)
            {
                throw new ArgumentNullException(nameof(request));
            }

            if (string.IsNullOrWhiteSpace(request.Message) || request.Message.Length > 60000 || request.Message.IndexOf('\0') >= 0)
            {
                throw new ArgumentException("The commit message must not be blank, exceed 60000 characters or contain NUL.");
            }

            if (request.StageAll && request.Paths != null && request.Paths.Count > 0)
            {
                throw new ArgumentException("Choose either all changes or listed paths to stage, not both.");
            }

            // Validate every path before staging any of them (including deleted, non-existent paths).
            var paths = request.Paths?.Select(ResolvePathspec).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            GitCliResult? staged = null;
            if (request.StageAll)
            {
                staged = await cli.RunAsync(RepositoryRoot, new[] { "add", "-A" }, GitCli.CommitTimeout, cancellationToken).ConfigureAwait(false);
            }
            else if (paths != null && paths.Count > 0)
            {
                var arguments = new List<string> { "add", "--" };
                arguments.AddRange(paths);
                staged = await cli.RunAsync(RepositoryRoot, arguments, GitCli.CommitTimeout, cancellationToken).ConfigureAwait(false);
            }

            if (staged != null && staged.ExitCode != 0)
            {
                return CommandResult(staged, "Staging failed; no commit was created.");
            }

            var quiet = await cli.RunAsync(RepositoryRoot, new[] { "diff", "--cached", "--quiet", "--no-ext-diff", "--no-textconv" }, GitCli.CommitTimeout, cancellationToken).ConfigureAwait(false);
            if (quiet.ExitCode == 0)
            {
                return new GitCommandResult { ExitCode = 1, Error = "Nothing is staged; no commit was created." };
            }

            if (quiet.ExitCode != 1)
            {
                return CommandResult(quiet, "Could not inspect the staged changes; no commit was created.");
            }

            var messageFile = Path.Combine(Path.GetTempPath(), "pomp-git-message-" + Guid.NewGuid().ToString("N") + ".txt");
            GitCliResult committed;
            var cleanupFailed = false;
            try
            {
                // CreateNew prevents an existing temp file from being overwritten. UTF-8 has no BOM, and the message is not argv.
                using (var stream = new FileStream(messageFile, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                using (var writer = new StreamWriter(stream, new UTF8Encoding(false)))
                {
                    await writer.WriteAsync(request.Message).ConfigureAwait(false);
                }

                cancellationToken.ThrowIfCancellationRequested();
                committed = await cli.RunAsync(RepositoryRoot, new[] { "commit", "-F", messageFile }, GitCli.CommitTimeout, cancellationToken).ConfigureAwait(false);
            }
            finally
            {
                // A cleanup error must not turn a successful commit into an ambiguous failure that invites a retry.
                try
                {
                    File.Delete(messageFile);
                }
                catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
                {
                    cleanupFailed = true;
                }
            }

            var result = CommandResult(committed, "Commit failed. Staged changes remain in the index; inspect them before retrying.");
            if (cleanupFailed)
            {
                result.Output += "\nWarning: the OS could not delete the temporary UTF-8 commit message file: " + messageFile;
            }

            if (!result.Succeeded)
            {
                return result;
            }

            try
            {
                var head = await cli.RunAsync(RepositoryRoot, new[] { "rev-parse", "HEAD" }, GitCli.ReadTimeout, cancellationToken).ConfigureAwait(false);
                if (head.ExitCode == 0 && !head.StdoutTruncated)
                {
                    result.CommitId = head.Stdout.TrimEnd('\r', '\n');
                }
                else
                {
                    result.Error = "The commit succeeded, but its id could not be read. Do not retry the commit blindly.";
                    result.Output += "\n" + head.CombinedOutput;
                }
            }
            catch (Exception)
            {
                // Once git commit returned success, a failed follow-up lookup must not report the commit as unapplied.
                result.Error = "The commit succeeded, but reading its id failed, was cancelled or timed out. Do not retry the commit blindly.";
            }

            return result;
        }

        public async Task<GitCommandResult> PushAsync(GitPushRequest request, CancellationToken cancellationToken)
        {
            if (request == null)
            {
                throw new ArgumentNullException(nameof(request));
            }

            // Caller-supplied names are checked before any git command, so a malformed request fails the same way in every repository state.
            if (request.Branch != null) ValidateBranch(request.Branch);
            if (request.Remote != null) ValidateRemote(request.Remote);

            var branch = request.Branch;
            if (branch == null)
            {
                var current = await cli.RunAsync(RepositoryRoot, new[] { "symbolic-ref", "--quiet", "--short", "HEAD" }, GitCli.ReadTimeout, cancellationToken).ConfigureAwait(false);
                if (current.ExitCode == 1)
                {
                    throw new InvalidOperationException("HEAD is detached; specify a branch explicitly before pushing.");
                }

                current.EnsureSucceeded("current branch lookup");
                branch = current.Stdout.TrimEnd('\r', '\n');
            }

            ValidateBranch(branch);
            var branchCheck = await cli.RunAsync(RepositoryRoot, new[] { "check-ref-format", "--branch", branch }, GitCli.ReadTimeout, cancellationToken).ConfigureAwait(false);
            branchCheck.EnsureSucceeded("branch validation");
            var sourceRef = branch.StartsWith("refs/heads/", StringComparison.Ordinal) ? branch : "refs/heads/" + branch;
            var source = await cli.RunAsync(RepositoryRoot, new[] { "show-ref", "--verify", "--quiet", sourceRef }, GitCli.ReadTimeout, cancellationToken).ConfigureAwait(false);
            source.EnsureSucceeded("local branch lookup"); // Never push a tag/revision expression passed off as a branch.

            var remote = request.Remote;
            if (remote == null)
            {
                // Resolve the configured remote, not the short upstream name (remote names can themselves contain '/').
                var branchConfigName = branch.StartsWith("refs/heads/", StringComparison.Ordinal) ? branch.Substring(11) : branch;
                var configured = await cli.RunAsync(RepositoryRoot, new[] { "config", "--get", "branch." + branchConfigName + ".remote" }, GitCli.ReadTimeout, cancellationToken).ConfigureAwait(false);
                if (configured.ExitCode != 0 && configured.ExitCode != 1)
                {
                    configured.EnsureSucceeded("upstream remote lookup");
                }

                remote = configured.ExitCode == 0 ? configured.Stdout.TrimEnd('\r', '\n') : "origin";
            }

            ValidateRemote(remote);
            var remoteUrl = await cli.RunAsync(RepositoryRoot, new[] { "remote", "get-url", "--push", remote }, GitCli.ReadTimeout, cancellationToken).ConfigureAwait(false);
            remoteUrl.EnsureSucceeded("configured remote lookup"); // Reject URLs/local destinations masquerading as remote names.
            var mirror = await cli.RunAsync(RepositoryRoot, new[] { "config", "--bool", "--get", "remote." + remote + ".mirror" }, GitCli.ReadTimeout, cancellationToken).ConfigureAwait(false);
            if (mirror.ExitCode != 0 && mirror.ExitCode != 1)
            {
                mirror.EnsureSucceeded("remote safety lookup");
            }

            if (mirror.ExitCode == 0 && mirror.Stdout.Trim() == "true")
            {
                throw new InvalidOperationException("This remote enables mirror pushing, which can force-update or delete refs. Use a non-mirror remote; Pomp does not force-push.");
            }

            var arguments = new List<string> { "push", "--no-force", "--recurse-submodules=no" };
            if (request.SetUpstream)
            {
                arguments.Add("--set-upstream");
            }

            arguments.Add(remote);
            arguments.Add(branch);
            var pushed = await cli.RunAsync(RepositoryRoot, arguments, GitCli.PushTimeout, cancellationToken).ConfigureAwait(false);
            var result = CommandResult(pushed, "Push failed; configured credential helpers run non-interactively. Check the remote, authentication and upstream.");
            return result;
        }

        /// <summary>Whether <paramref name="fullPath"/> lies inside both the workspace and its repository.</summary>
        public bool Contains(string fullPath) => workspace.IsInsideRoot(fullPath) && repository.IsInsideRoot(fullPath);

        /// <summary>
        /// The HEAD version of a workspace file as the work tree would check it out (eol and filters applied, byte order mark removed),
        /// or null when HEAD has no such file (an added file, or a repository without commits).
        /// </summary>
        public async Task<string?> ReadHeadTextAsync(string fullPath, CancellationToken cancellationToken)
        {
            var relative = ResolvePathspec(fullPath);
            var result = await cli.RunAsync(RepositoryRoot, new[] { "cat-file", "--filters", "HEAD:" + relative }, GitCli.ReadTimeout, cancellationToken).ConfigureAwait(false);
            if (result.ExitCode != 0)
            {
                if (result.Stderr.IndexOf("does not exist", StringComparison.OrdinalIgnoreCase) >= 0 || result.Stderr.IndexOf("not a valid object name", StringComparison.OrdinalIgnoreCase) >= 0
                    || result.Stderr.IndexOf("invalid object name", StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    return null;
                }

                result.EnsureSucceeded("reading the committed file");
            }

            if (result.StdoutTruncated)
            {
                throw new InvalidOperationException(Pomp.Hosting.PompLanguage.Pick("파일이 너무 커요: ", "The file is too large: ") + relative);
            }

            return result.Stdout.Length > 0 && result.Stdout[0] == '\uFEFF' ? result.Stdout.Substring(1) : result.Stdout;
        }

        private string ResolvePathspec(string path)
        {
            var fullPath = workspace.Resolve(path);
            if (!repository.IsInsideRoot(fullPath))
            {
                throw new ArgumentException("The path is outside the workspace repository: " + path);
            }

            // Forward slashes and an explicit -- are used together with --literal-pathspecs, never git's glob/magic syntax.
            return repository.ToDisplayPath(fullPath);
        }

        internal static void ValidateRemote(string remote)
        {
            if (string.IsNullOrWhiteSpace(remote) || remote.Length > 256 || remote[0] == '-' || remote[0] == '.' || remote[0] == '/' || remote.Contains("..")
                || remote.Any(c => char.IsWhiteSpace(c) || char.IsControl(c) || c == ':' || c == '\\'))
            {
                throw new ArgumentException("The remote must be a configured git remote name, not an option, URL or filesystem path.");
            }
        }

        internal static void ValidateBranch(string branch)
        {
            if (string.IsNullOrWhiteSpace(branch) || branch.Length > 1024 || branch[0] == '-' || branch[0] == '+' || branch[0] == '.' || branch[0] == '/'
                || branch == "HEAD" || branch.StartsWith("@", StringComparison.Ordinal) || branch.EndsWith("/", StringComparison.Ordinal)
                || branch.EndsWith(".", StringComparison.Ordinal) || branch.EndsWith(".lock", StringComparison.Ordinal) || branch.Contains("//") || branch.Contains("/.")
                || branch.Contains(":") || branch.Contains("..") || branch.Contains("@{")
                || branch.Any(c => char.IsWhiteSpace(c) || char.IsControl(c) || "~^?*[\\".IndexOf(c) >= 0))
            {
                throw new ArgumentException("The branch must be a literal branch name, not an option, revision expression or push refspec.");
            }
        }

        private static GitCommandResult CommandResult(GitCliResult result, string failure)
        {
            return new GitCommandResult
            {
                Succeeded = result.ExitCode == 0,
                ExitCode = result.ExitCode,
                Output = result.CombinedOutput,
                Error = result.ExitCode == 0 ? null : failure,
            };
        }
    }
}
