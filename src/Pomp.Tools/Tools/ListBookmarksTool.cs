using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;
using Pomp.Adapters;

namespace Pomp.Tools
{
    /// <summary>
    /// <c>list_bookmarks</c>: one line per bookmark, <c>file:line "name"</c>. Visual Studio has no public bookmark API,
    /// so only bookmarks of documents that are open in the editor are reported.
    /// </summary>
    internal sealed class ListBookmarksTool : HostToolBase
    {
        internal const int MaxListed = 200;

        public ListBookmarksTool(HostToolContext context)
            : base(context, CreateDefinition())
        {
        }

        private static HostToolDefinition CreateDefinition()
        {
            return new HostToolDefinition
            {
                Name = HostToolNames.ListBookmarks,
                Label = "List Bookmarks",
                Description =
                    "List Visual Studio bookmarks, one per line as `file:line \"name\"`. " +
                    "Only bookmarks in documents that are currently open in the editor are reported. Read-only.",
                Parameters = new SchemaBuilder().Build(),
                LoadMode = EssentialLoadMode,
            };
        }

        protected override async Task<HostToolResult> ExecuteCoreAsync(HostToolInvocation invocation, CancellationToken cancellationToken)
        {
            var args = new ToolArguments(invocation.Arguments);
            if (args.TryGetError(Name, out var invalid))
            {
                return invalid;
            }

            var bookmarks = await Adapters.Bookmarks.ListAsync(cancellationToken).ConfigureAwait(false);
            var list = (bookmarks ?? new List<BookmarkInfo>()).Where(b => b != null).ToList();
            if (list.Count == 0)
            {
                return HostToolResult.Text("No bookmarks in the open documents.", new JObject { ["count"] = 0 });
            }

            var guard = await Context.TryGetPathGuardAsync(cancellationToken).ConfigureAwait(false);
            var display = HostToolContext.DisplayFunc(guard);

            var builder = new StringBuilder();
            builder.Append("Bookmarks: ").Append(list.Count);
            foreach (var bookmark in list.Take(MaxListed))
            {
                builder.Append('\n').Append(display(bookmark.File ?? string.Empty));
                if (bookmark.Line > 0)
                {
                    builder.Append(':').Append(bookmark.Line);
                }

                if (!string.IsNullOrWhiteSpace(bookmark.Name))
                {
                    builder.Append(" \"").Append(TextUtil.ToSingleLine(bookmark.Name, 100)).Append('"');
                }
            }

            if (list.Count > MaxListed)
            {
                builder.Append("\n[").Append(list.Count - MaxListed).Append(" more bookmark(s) not shown]");
            }

            return HostToolResult.Text(builder.ToString(), new JObject { ["count"] = list.Count });
        }
    }
}
