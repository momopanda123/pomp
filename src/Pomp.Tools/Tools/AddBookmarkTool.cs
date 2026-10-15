using System;
using System.Globalization;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;
using Pomp.Hosting;
using Pomp.Permissions;

namespace Pomp.Tools
{
    /// <summary><c>add_bookmark</c>: approved, idempotent bookmarking of an existing workspace text line.</summary>
    internal sealed class AddBookmarkTool : HostToolBase
    {
        public AddBookmarkTool(HostToolContext context)
            : base(context, CreateDefinition())
        {
        }

        private static HostToolDefinition CreateDefinition()
        {
            return new HostToolDefinition
            {
                Name = HostToolNames.AddBookmark,
                Label = "Add Bookmark",
                Description = "Add a Visual Studio bookmark at an existing line of a workspace text file. Opens the file and moves the caret; an existing bookmark is kept, not toggled off. Checks the editor buffer, including unsaved changes, before bookmarking. Does not change or save the file's text. Requires user approval.",
                Parameters = new SchemaBuilder()
                    .String("path", "Workspace-relative or absolute path of the file.", required: true)
                    .Integer("line", "1-based line to bookmark.", 1, required: true)
                    .Build(),
                LoadMode = EssentialLoadMode,
            };
        }

        public override PermissionKind ResolvePermission(HostToolInvocation invocation) => PermissionKind.BookmarkChange;

        public override async Task<PermissionRequest?> DescribePermissionAsync(HostToolInvocation invocation, CancellationToken cancellationToken)
        {
            Parse(invocation?.Arguments, out var args, out var path, out var line);
            if (args.HasProblems)
            {
                return GenericPermission();
            }

            try
            {
                var guard = await Context.TryGetPathGuardAsync(cancellationToken).ConfigureAwait(false);
                if (guard == null || !guard.TryResolve(path, out var fullPath, out _))
                {
                    return GenericPermission();
                }

                var location = IdeWriteToolText.Exact(guard.ToDisplayPath(fullPath)) + ":" + line!.Value.ToString(CultureInfo.InvariantCulture);
                return NewPermissionRequest(PermissionKind.BookmarkChange, PompLanguage.Pick("책갈피를 추가할까요?", "Add a bookmark?"), null, location);
            }
            catch (Exception)
            {
                return GenericPermission();
            }
        }

        protected override async Task<HostToolResult> ExecuteCoreAsync(HostToolInvocation invocation, CancellationToken cancellationToken)
        {
            Parse(invocation.Arguments, out var args, out var path, out var line);
            if (args.TryGetError(Name, out var invalid))
            {
                return invalid;
            }

            var guard = await Context.GetPathGuardAsync(cancellationToken).ConfigureAwait(false);
            if (!guard.TryResolve(path, out var fullPath, out var pathError))
            {
                return Fail(IdeWriteToolText.Exact(pathError));
            }

            var display = IdeWriteToolText.Exact(guard.ToDisplayPath(fullPath));
            var snapshot = await Adapters.TextBuffers.ReadAsync(fullPath, cancellationToken).ConfigureAwait(false);
            if (snapshot == null)
            {
                return Fail("File not found: " + display + ".");
            }

            var text = snapshot.Text ?? string.Empty;
            if (TextUtil.LooksBinary(text))
            {
                return Fail("'" + display + "' looks like a binary file; only text lines can be bookmarked.");
            }

            var totalLines = TextUtil.SplitLines(TextUtil.NormalizeLineEndings(text)).Count;
            if (line!.Value > totalLines)
            {
                return Fail("line " + line.Value.ToString(CultureInfo.InvariantCulture) + " does not exist in '" + display + "' (" + totalLines.ToString(CultureInfo.InvariantCulture) + " line(s)).");
            }

            var added = await Adapters.Bookmarks.AddAsync(fullPath, line.Value, cancellationToken).ConfigureAwait(false);
            if (!added)
            {
                return Fail("Visual Studio could not add the bookmark at " + display + ":" + line.Value.ToString(CultureInfo.InvariantCulture) + "; the line may have changed or the editor may not support bookmarks.");
            }

            return HostToolResult.Text("Bookmark present at " + display + ":" + line.Value.ToString(CultureInfo.InvariantCulture) + " (added if absent; an existing bookmark was kept).",
                new JObject { ["path"] = display, ["line"] = line.Value, ["bookmarked"] = true });
        }

        private PermissionRequest GenericPermission() => NewPermissionRequest(PermissionKind.BookmarkChange,
            PompLanguage.Pick("책갈피를 추가할까요?", "Add a bookmark?"), null, null);

        private static void Parse(JObject? arguments, out ToolArguments args, out string path, out int? line)
        {
            args = new ToolArguments(arguments, "path", "line");
            path = args.RequiredString("path");
            line = args.OptionalInt("line", 1, int.MaxValue);
            if (arguments == null || arguments["line"] == null || arguments["line"]!.Type == JTokenType.Null || arguments["line"]!.Type == JTokenType.Undefined)
            {
                args.AddProblem("'line' is required");
            }

            if (path.Length > 4096)
            {
                args.AddProblem("'path' may contain at most 4096 characters");
            }

            if (path.IndexOf('\0') >= 0)
            {
                args.AddProblem("'path' must not contain a NUL character");
            }
        }
    }
}
