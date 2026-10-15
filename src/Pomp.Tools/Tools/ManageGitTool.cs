using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Pomp.Adapters;
using Pomp.Hosting;
using Pomp.Permissions;

namespace Pomp.Tools
{
    /// <summary>Read-only Git status/diff and approved commits/pushes; every supplied path is workspace-guarded.</summary>
    internal sealed class ManageGitTool : HostToolBase
    {
        internal const int MaxPaths = 200;
        internal const int MaxChangedEntries = 300;
        internal const int MaxDiffCharacters = 60000;
        private const int MaxMessageCharacters = 60000;
        private const int MaxCommandOutputCharacters = 60000;
        internal static readonly string[] Actions = { "status", "diff", "commit", "push" };
        private readonly ConditionalWeakTable<HostToolInvocation, ApprovalState> approvals = new ConditionalWeakTable<HostToolInvocation, ApprovalState>();
        private readonly object approvalGate = new object();

        public ManageGitTool(HostToolContext context)
            : base(context, CreateDefinition())
        {
        }

        private static HostToolDefinition CreateDefinition()
        {
            return new HostToolDefinition
            {
                Name = HostToolNames.ManageGit,
                Label = "Git",
                Description =
                    "Git in the workspace repository. Read-only `status` groups staged, unstaged and untracked changes with branch/upstream ahead/behind; " +
                    "`diff` reads a bounded patch (optionally staged or limited to one literal path). Use these to summarize changes or suggest a commit message. " +
                    "`commit` and `push` require user approval. Commit stages the listed literal files, all changes with git add -A, or nothing (commit already staged changes); " +
                    "it includes every staged change, runs repository hooks normally and returns the commit id. Git reads saved files, not unsaved editor buffers. " +
                    "Push defaults to the branch's upstream remote, or origin, and the current branch. Detached HEAD requires an explicit branch. No force push or amend. " +
                    "Credential helpers run non-interactively; failed authentication is reported rather than prompting.",
                Parameters = new SchemaBuilder()
                    .Enum("action", "status, diff, commit or push.", Actions, required: true)
                    .String("path", "diff: only this literal file or folder (workspace-relative or absolute).")
                    .Boolean("staged", "diff: the staged changes instead of the unstaged ones.", false)
                    .String("message", "commit: the complete commit message (required for commit, at most 60000 characters).")
                    .StringArray("paths", "commit: literal files to stage before committing (default: commit every already staged change).", MaxPaths)
                    .Boolean("all", "commit: stage every change in the repository first (git add -A), mutually exclusive with paths.", false)
                    .String("remote", "push: configured remote name (default: the branch's upstream remote, or origin).")
                    .String("branch", "push: literal branch name, not a refspec (default: the current branch).")
                    .Boolean("setUpstream", "push: set the pushed branch as upstream.", false)
                    .Build(),
                LoadMode = EssentialLoadMode,
            };
        }

        public override PermissionKind ResolvePermission(HostToolInvocation invocation)
        {
            var action = ToolArguments.PeekEnum(invocation?.Arguments, "action", Actions);
            return action == "commit" || action == "push" ? PermissionKind.VcsWrite : PermissionKind.None;
        }

        public override async Task<PermissionRequest?> DescribePermissionAsync(HostToolInvocation invocation, CancellationToken cancellationToken)
        {
            // Contract callers pass {} and forbidden adapters/environment. Reject syntax before touching either.
            if (invocation == null)
            {
                return null;
            }

            var options = ReadArguments(invocation.Arguments, out var args);
            if (args.HasProblems || (options.Action != "commit" && options.Action != "push"))
            {
                return null;
            }

            try
            {
                cancellationToken.ThrowIfCancellationRequested();
                var guard = await Context.GetPathGuardAsync(cancellationToken).ConfigureAwait(false);
                var paths = ResolvePaths(options, guard);
                var state = new ApprovalState { Root = guard.Root, Arguments = Fingerprint(invocation) };
                Remember(invocation, state);
                var message = new StringBuilder(PompLanguage.Pick("작업 영역: ", "Workspace: ")).Append(guard.Root).Append('\n');
                if (options.Action == "commit")
                {
                    if (options.All)
                    {
                        message.Append(PompLanguage.Pick("스테이징: 모든 변경 (git add -A)", "Staging: all changes (git add -A)"));
                    }
                    else if (paths != null && paths.Count > 0)
                    {
                        message.Append(PompLanguage.Pick("스테이징할 파일:", "Files to stage:"));
                        foreach (var path in paths)
                        {
                            message.Append("\n  ").Append(guard.ToDisplayPath(path));
                        }
                    }
                    else
                    {
                        message.Append(PompLanguage.Pick("스테이징: 없음", "Staging: none"));
                    }

                    message.Append('\n').Append(PompLanguage.Pick("스테이징된 모든 변경이 커밋에 포함돼요.", "Every staged change is included in the commit."))
                        .Append("\n\n").Append(PompLanguage.Pick("커밋 메시지:", "Commit message:")).Append('\n').Append(options.Message);
                    return NewPermissionRequest(PermissionKind.VcsWrite, PompLanguage.Pick("Git 커밋을 만들까요?", "Create a Git commit?"), message.ToString(), guard.Root);
                }

                message.Append(PompLanguage.Pick("원격: ", "Remote: ")).Append(options.Remote ?? PompLanguage.Pick("브랜치의 업스트림 원격 또는 origin", "the branch's upstream remote, or origin"))
                    .Append('\n').Append(PompLanguage.Pick("브랜치: ", "Branch: ")).Append(options.Branch ?? PompLanguage.Pick("현재 브랜치", "the current branch"))
                    .Append('\n').Append(PompLanguage.Pick("업스트림 설정(--set-upstream): ", "Set upstream (--set-upstream): "))
                    .Append(options.SetUpstream ? PompLanguage.Pick("예", "yes") : PompLanguage.Pick("아니요", "no"))
                    .Append('\n').Append(PompLanguage.Pick("원격 저장소로 커밋을 보내요.", "Sends commits to the remote."));
                return NewPermissionRequest(PermissionKind.VcsWrite, PompLanguage.Pick("Git 브랜치를 푸시할까요?", "Push the Git branch?"), message.ToString(), options.Remote ?? PompLanguage.Pick("업스트림 원격 또는 origin", "upstream remote or origin"));
            }
            catch (Exception ex)
            {
                // The dispatcher can fall back to a generic prompt when description returns null. Pin a failure as well so that
                // such a prompt can never execute an operation whose paths/workspace could not be preflighted.
                Remember(invocation, new ApprovalState { Arguments = Fingerprint(invocation), Error = ToolErrors.ShortMessage(ex) });
                return null;
            }
        }

        protected override async Task<HostToolResult> ExecuteCoreAsync(HostToolInvocation invocation, CancellationToken cancellationToken)
        {
            var options = ReadArguments(invocation.Arguments, out var args);
            if (args.TryGetError(Name, out var invalid))
            {
                return invalid;
            }

            if (options.Action == "status")
            {
                if (options.Path != null || options.Paths != null)
                {
                    ResolvePaths(options, await Context.GetPathGuardAsync(cancellationToken).ConfigureAwait(false));
                }

                var status = await Adapters.Git.GetStatusAsync(cancellationToken).ConfigureAwait(false);
                return status == null ? Fail("the git adapter returned no status.") : FormatStatus(status);
            }

            if (options.Action == "diff")
            {
                string? path = null;
                if (options.Path != null || options.Paths != null)
                {
                    var guard = await Context.GetPathGuardAsync(cancellationToken).ConfigureAwait(false);
                    ResolvePaths(options, guard);
                    path = options.Path == null ? null : guard.Resolve(options.Path);
                }

                var diff = await Adapters.Git.GetDiffAsync(new GitDiffRequest { Path = path, Staged = options.Staged, MaxChars = MaxDiffCharacters }, cancellationToken).ConfigureAwait(false);
                if (diff == null)
                {
                    return Fail("the git adapter returned no diff.");
                }

                var text = BoundLines(diff.Diff ?? string.Empty, MaxDiffCharacters, out var capped);
                var truncated = diff.Truncated || capped;
                if (text.Length == 0 && !truncated)
                {
                    text = options.Staged ? "No staged differences." : "No unstaged differences (untracked files are not included in git diff).";
                }

                if (truncated)
                {
                    text = text.TrimEnd('\r', '\n') + "\n[diff truncated; use path to narrow the result]";
                }

                return HostToolResult.Text(text, new JObject { ["truncated"] = truncated, ["staged"] = options.Staged });
            }

            ApprovalState? approved;
            lock (approvalGate)
            {
                approvals.TryGetValue(invocation, out approved);
                approvals.Remove(invocation);
            }

            if (approved != null && (approved.Error != null || approved.Arguments != Fingerprint(invocation)))
            {
                return Fail(approved.Error == null ? "the arguments changed after approval; request approval again." : "approval preflight failed: " + approved.Error);
            }

            var current = await Context.GetPathGuardAsync(cancellationToken).ConfigureAwait(false);
            if (approved != null && !string.Equals(current.Root, approved.Root, StringComparison.OrdinalIgnoreCase))
            {
                return Fail("the workspace changed after approval; request approval again in the current workspace.");
            }

            var commitPaths = ResolvePaths(options, current);
            GitCommandResult result;
            if (options.Action == "commit")
            {
                ReportProgress(invocation, "Creating Git commit...");
                result = await Adapters.Git.CommitAsync(new GitCommitRequest { Message = options.Message!, Paths = commitPaths, StageAll = options.All }, cancellationToken).ConfigureAwait(false);
            }
            else
            {
                ReportProgress(invocation, "Pushing Git branch...");
                result = await Adapters.Git.PushAsync(new GitPushRequest { Remote = options.Remote, Branch = options.Branch, SetUpstream = options.SetUpstream }, cancellationToken).ConfigureAwait(false);
            }

            if (result == null)
            {
                return Fail("the git adapter returned no command result.");
            }

            var output = BoundLines(result.Output ?? string.Empty, MaxCommandOutputCharacters, out var outputTruncated).TrimEnd('\r', '\n');
            if (outputTruncated)
            {
                output += "\n[git command output truncated]";
            }

            if (!result.Succeeded)
            {
                return Fail("Git " + options.Action + " failed (exit " + result.ExitCode.ToString(CultureInfo.InvariantCulture) + "). "
                    + (result.Error ?? "Inspect the git output before retrying.") + (output.Length == 0 ? string.Empty : "\n" + output));
            }

            var summary = options.Action == "commit"
                ? "Git commit created" + (string.IsNullOrWhiteSpace(result.CommitId) ? " (commit id unavailable)." : ": " + result.CommitId + ".")
                : "Git push succeeded.";
            if (!string.IsNullOrWhiteSpace(result.Error))
            {
                summary += "\nWarning: " + result.Error;
            }

            return HostToolResult.Text(summary + (output.Length == 0 ? string.Empty : "\n" + output), new JObject
            {
                ["succeeded"] = true,
                ["commitId"] = result.CommitId,
                ["exitCode"] = result.ExitCode,
                ["truncated"] = outputTruncated,
            });
        }

        private static Options ReadArguments(JObject arguments, out ToolArguments args)
        {
            args = new ToolArguments(arguments, "action", "path", "staged", "message", "paths", "all", "remote", "branch", "setUpstream");
            var result = new Options
            {
                Action = args.RequiredEnum("action", Actions),
                Path = args.OptionalString("path"),
                Staged = args.OptionalBool("staged") ?? false,
                Message = args.OptionalString("message", trim: false),
                Paths = args.OptionalStringArray("paths", MaxPaths),
                All = args.OptionalBool("all") ?? false,
                Remote = args.OptionalString("remote"),
                Branch = args.OptionalString("branch"),
                SetUpstream = args.OptionalBool("setUpstream") ?? false,
            };

            if (result.Action == "commit" && string.IsNullOrWhiteSpace(result.Message))
            {
                args.AddProblem("'message' is required and must not be blank for commit");
            }

            if (result.Message != null && (result.Message.Length > MaxMessageCharacters || result.Message.IndexOf('\0') >= 0))
            {
                args.AddProblem("'message' must be at most 60000 characters and must not contain NUL");
            }

            if (result.All && result.Paths != null)
            {
                args.AddProblem("'all' and 'paths' are mutually exclusive");
            }

            if (result.Remote != null && (result.Remote.Length > 256 || result.Remote[0] == '-' || result.Remote[0] == '.' || result.Remote[0] == '/'
                || result.Remote.Contains("..") || result.Remote.Any(c => char.IsWhiteSpace(c) || char.IsControl(c) || c == ':' || c == '\\')))
            {
                args.AddProblem("'remote' must be a configured remote name, not an option, URL or filesystem path");
            }

            if (result.Branch != null && (result.Branch.Length > 1024 || result.Branch[0] == '-' || result.Branch[0] == '+' || result.Branch[0] == '.' || result.Branch[0] == '/'
                || result.Branch == "HEAD" || result.Branch.StartsWith("@", StringComparison.Ordinal) || result.Branch.EndsWith("/", StringComparison.Ordinal)
                || result.Branch.EndsWith(".", StringComparison.Ordinal) || result.Branch.EndsWith(".lock", StringComparison.Ordinal) || result.Branch.Contains("//") || result.Branch.Contains("/.")
                || result.Branch.Contains(":") || result.Branch.Contains("..") || result.Branch.Contains("@{")
                || result.Branch.Any(c => char.IsWhiteSpace(c) || char.IsControl(c) || "~^?*[\\".IndexOf(c) >= 0)))
            {
                args.AddProblem("'branch' must be a literal branch name, not an option, revision expression or push refspec");
            }

            var pathArguments = (result.Paths ?? Array.Empty<string>()).Concat(result.Path == null ? Array.Empty<string>() : new[] { result.Path });
            if (pathArguments.Any(p => p.Length > 4096 || p.IndexOf('\0') >= 0) || pathArguments.Sum(p => (long)p.Length + 3) > 24000)
            {
                args.AddProblem("path arguments must contain no NUL, each be at most 4096 characters and total at most 24000 characters");
            }

            return result;
        }

        private static List<string>? ResolvePaths(Options options, PathGuard guard)
        {
            // Validate even paths supplied for a different action; no provided path silently bypasses the workspace guard.
            if (options.Path != null)
            {
                guard.Resolve(options.Path);
            }

            return options.Paths?.Select(guard.Resolve).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        }

        private static HostToolResult FormatStatus(GitStatus status)
        {
            if (!status.IsRepository)
            {
                return HostToolResult.Text("The current workspace is not inside a Git repository.", new JObject { ["isRepository"] = false });
            }

            var changes = status.Changes?.Where(c => c != null).ToList() ?? new List<GitFileChange>();
            var staged = changes.Where(c => c.IndexStatus != ' ' && c.IndexStatus != '?').ToList();
            var unstaged = changes.Where(c => c.WorkTreeStatus != ' ' && c.WorkTreeStatus != '?').ToList();
            var untracked = changes.Where(c => c.IndexStatus == '?' || c.WorkTreeStatus == '?').ToList();
            var builder = new StringBuilder("Git repository: ").Append(OneLine(status.RepositoryRoot, 4096))
                .Append("\nBranch: ").Append(status.Branch == null ? "(detached HEAD)" : OneLine(status.Branch, 1024));
            if (!string.IsNullOrWhiteSpace(status.HeadCommit))
            {
                builder.Append("\nHEAD: ").Append(OneLine(status.HeadCommit, 128));
            }

            builder.Append("\nUpstream: ").Append(status.Upstream == null ? "(none)" : OneLine(status.Upstream, 1024));
            if (status.Upstream != null)
            {
                builder.Append(" (ahead ").Append(status.Ahead).Append(", behind ").Append(status.Behind).Append(')');
            }

            var written = 0;
            AppendGroup(builder, "Staged", staged, ref written);
            AppendGroup(builder, "Unstaged", unstaged, ref written);
            AppendGroup(builder, "Untracked", untracked, ref written);
            var totalEntries = staged.Count + unstaged.Count + untracked.Count;
            if (totalEntries > written)
            {
                builder.Append("\n[").Append(totalEntries - written).Append(" more change entries not shown; use diff with path to inspect specific files]");
            }

            if (changes.Count == 0)
            {
                builder.Append("\nWorking tree clean.");
            }

            return HostToolResult.Text(builder.ToString(), new JObject
            {
                ["isRepository"] = true, ["branch"] = status.Branch, ["upstream"] = status.Upstream,
                ["ahead"] = status.Ahead, ["behind"] = status.Behind,
                ["staged"] = staged.Count, ["unstaged"] = unstaged.Count, ["untracked"] = untracked.Count,
                ["truncated"] = totalEntries > written,
            });
        }

        private static void AppendGroup(StringBuilder builder, string label, IReadOnlyList<GitFileChange> changes, ref int written)
        {
            builder.Append('\n').Append(label).Append(" (").Append(changes.Count).Append("):");
            if (changes.Count == 0)
            {
                builder.Append(" (none)");
            }

            foreach (var change in changes)
            {
                if (written >= MaxChangedEntries)
                {
                    break;
                }

                builder.Append("\n  ").Append(change.IndexStatus).Append(change.WorkTreeStatus).Append(' ');
                if (change.OriginalPath != null)
                {
                    builder.Append(OneLine(change.OriginalPath, 512)).Append(" -> ");
                }

                builder.Append(OneLine(change.Path, 512));
                written++;
            }
        }

        private static string OneLine(string? text, int maxChars)
        {
            var value = (text ?? string.Empty).Replace("\r", "\\r").Replace("\n", "\\n").Replace("\t", "\\t").Replace("\0", "\\0");
            return value.Length > maxChars ? value.Substring(0, maxChars) + "..." : value;
        }

        private static string BoundLines(string text, int maxChars, out bool truncated)
        {
            truncated = text.Length > maxChars;
            if (!truncated)
            {
                return text;
            }

            var cut = text.LastIndexOf('\n', maxChars - 1);
            return cut < 0 ? string.Empty : text.Substring(0, cut + 1);
        }

        private static string Fingerprint(HostToolInvocation invocation) => invocation.Arguments.ToString(Formatting.None);

        private void Remember(HostToolInvocation invocation, ApprovalState state)
        {
            lock (approvalGate)
            {
                approvals.Remove(invocation);
                approvals.Add(invocation, state);
            }
        }

        private sealed class Options
        {
            public string? Action { get; set; }
            public string? Path { get; set; }
            public bool Staged { get; set; }
            public string? Message { get; set; }
            public IReadOnlyList<string>? Paths { get; set; }
            public bool All { get; set; }
            public string? Remote { get; set; }
            public string? Branch { get; set; }
            public bool SetUpstream { get; set; }
        }

        private sealed class ApprovalState
        {
            public string? Root { get; set; }
            public string Arguments { get; set; } = string.Empty;
            public string? Error { get; set; }
        }
    }
}
