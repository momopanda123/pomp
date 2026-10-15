using System;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;
using Pomp.Adapters;

namespace Pomp.Tools
{
    /// <summary>
    /// <c>get_editor_context</c>: what the user is looking at right now (active document, caret, selection, dirty flag)
    /// plus numbered lines around the caret in the same <c>[path#TAG]</c> / <c>N:TEXT</c> format as
    /// <c>read_buffer</c>. The shown lines are recorded in the snapshot store, so they can be edited without another read.
    /// </summary>
    internal sealed class GetEditorContextTool : HostToolBase
    {
        internal const int DefaultRadius = 15;
        internal const int MaxRadius = 200;
        internal const int LargeFileLineThreshold = 10000;
        internal const int MaxSelectionCharacters = 1500;
        internal const int MaxSelectionLines = 40;
        private const int MaxExcerptCharacters = 20000;
        private const int MaxContextSymbols = 200;
        private const int MaxContextSymbolCharacters = 12000;

        public GetEditorContextTool(HostToolContext context)
            : base(context, CreateDefinition())
        {
        }

        private static HostToolDefinition CreateDefinition()
        {
            return new HostToolDefinition
            {
                Name = HostToolNames.GetEditorContext,
                Label = "Get Editor Context",
                Description =
                    "Get what the user is looking at in Visual Studio right now: the active document's path, language, caret position, " +
                    "selected text (truncated) and whether its buffer has unsaved changes, followed by numbered lines around the caret. " +
                    "Use it when the user says 'this file', 'here', 'this selection' or 'the current code'. " +
                    "The excerpt uses the same `[path#TAG]` / `N:text` format as read_buffer. " +
                    "Files of 10,000 lines or more default to at most 200 lines above and below the caret plus a bounded symbol summary; an explicit radius caps the window. " +
                    "Only fully shown source rows can be edit anchors; symbol summaries do not mark any lines as shown. " +
                    "Reports 'No document is active' (not an error) when nothing is open.",
                Parameters = new SchemaBuilder()
                    .Integer("radius", "Maximum lines above and below the caret (0 = caret line only). If omitted: 15 for smaller files, 200 for files with at least 10,000 lines.", 0, MaxRadius, DefaultRadius)
                    .Build(),
                LoadMode = EssentialLoadMode,
            };
        }

        protected override async Task<HostToolResult> ExecuteCoreAsync(HostToolInvocation invocation, CancellationToken cancellationToken)
        {
            var args = new ToolArguments(invocation.Arguments, "radius");
            var requestedRadius = args.OptionalInt("radius", 0, MaxRadius);
            if (args.TryGetError(Name, out var invalid))
            {
                return invalid;
            }

            var document = await Adapters.Editor.GetActiveDocumentAsync(cancellationToken).ConfigureAwait(false);
            if (document == null)
            {
                return HostToolResult.Text("No document is active in the editor.");
            }

            var path = document.Path ?? string.Empty;
            var guard = await Context.TryGetPathGuardAsync(cancellationToken).ConfigureAwait(false);
            var display = guard != null ? guard.ToDisplayPath(path) : path;
            var fullPath = string.Empty;
            var insideWorkspace = guard != null && guard.TryResolve(path, out fullPath, out _);

            BufferView? view = null;
            string? readProblem = null;
            if (path.Length > 0)
            {
                try
                {
                    var snapshot = await Adapters.TextBuffers.ReadAsync(path, cancellationToken).ConfigureAwait(false);
                    if (snapshot != null)
                    {
                        view = BufferView.Create(snapshot);
                    }
                    else
                    {
                        readProblem = "document content is not available";
                    }
                }
                catch (Exception ex) when (!(ex is OperationCanceledException))
                {
                    readProblem = "could not read the document content: " + ToolErrors.ShortMessage(ex);
                }
            }

            var unsaved = document.IsDirty || (view != null && view.IsDirty);
            var lineCount = document.LineCount > 0 ? document.LineCount : (view != null ? view.Lines.Count : 0);
            var caret = document.Caret ?? new TextPosition { Line = 1, Column = 1 };

            var builder = new StringBuilder();
            builder.Append("Active document: ").Append(display);
            builder.Append("\nLanguage: ").Append(string.IsNullOrWhiteSpace(document.Language) ? "unknown" : document.Language!.Trim());
            builder.Append(" | ").Append(lineCount).Append(" lines");
            if (unsaved)
            {
                builder.Append(" | UNSAVED CHANGES (buffer differs from disk)");
            }

            builder.Append("\nCaret: line ").Append(caret.Line).Append(", column ").Append(caret.Column);
            AppendSelection(builder, document);

            var details = new JObject
            {
                ["path"] = display,
                ["language"] = document.Language,
                ["lineCount"] = lineCount,
                ["unsaved"] = unsaved,
                ["caret"] = new JObject { ["line"] = caret.Line, ["column"] = caret.Column },
                ["selectionLength"] = document.SelectedText?.Length ?? 0,
            };

            if (view != null && view.Lines.Count > 0)
            {
                // Use the same VS-compatible line model as numbered reads, not a possibly stale editor line count.
                var largeFile = view.Lines.Count >= LargeFileLineThreshold;
                var radius = requestedRadius ?? (largeFile ? MaxRadius : DefaultRadius);
                var caretLine = Math.Max(1, Math.Min(caret.Line, view.Lines.Count));
                var start = Math.Max(1, caretLine - radius);
                var end = Math.Min(view.Lines.Count, caretLine + radius);
                builder.Append('\n').Append(HashlineFormat.Header(display, view.Tag));
                var last = HashlineFormat.AppendNumberedLines(builder, view.Lines, start, end, MaxExcerptCharacters);
                if (insideWorkspace)
                {
                    Context.Snapshots.Record(fullPath, view.ContentHash, view.Lines, start, last);
                }

                details["excerpt"] = new JObject { ["startLine"] = start, ["endLine"] = last, ["tag"] = view.Tag };
                if (largeFile)
                {
                    if (last < end)
                    {
                        builder.Append("\n[excerpt output limit reached; only complete rows through line ")
                            .Append(last).Append(" are eligible as edit anchors; use read_buffer for the needed range]");
                    }

                    var symbols = SymbolOutline.CreateBounded(path, view.Lines, MaxContextSymbols, MaxContextSymbolCharacters);
                    builder.Append("\nSymbols (navigation only; not source lines):\n")
                        .Append(symbols.Length == 0 ? "[no symbols found for this file type or content]" : symbols);
                    details["largeFile"] = true;
                }
            }
            else if (view != null)
            {
                // Like read_buffer: an empty document still gets its tag, so it can be filled with PUT <1: or PUT >$: right away.
                builder.Append('\n').Append(HashlineFormat.Header(display, view.Tag)).Append("\n[the document is empty]");
                if (insideWorkspace)
                {
                    Context.Snapshots.Record(fullPath, view.ContentHash, view.Lines, 1, 0);
                }

                details["excerpt"] = new JObject { ["startLine"] = 1, ["endLine"] = 0, ["tag"] = view.Tag };
            }
            else if (readProblem != null)
            {
                builder.Append("\n[").Append(readProblem).Append(']');
            }

            return HostToolResult.Text(builder.ToString(), details);
        }

        private static void AppendSelection(StringBuilder builder, ActiveDocumentInfo document)
        {
            var selected = document.SelectedText;
            if (string.IsNullOrEmpty(selected))
            {
                builder.Append("\nSelection: none");
                return;
            }

            var normalized = TextUtil.NormalizeLineEndings(selected!);
            var lineCount = 1;
            foreach (var c in normalized)
            {
                if (TextLineBreaks.IsBreak(c))
                {
                    lineCount++;
                }
            }

            builder.Append("\nSelection: ");
            if (document.SelectionStart != null && document.SelectionEnd != null)
            {
                builder.Append(document.SelectionStart.Line).Append(':').Append(document.SelectionStart.Column)
                    .Append('-')
                    .Append(document.SelectionEnd.Line).Append(':').Append(document.SelectionEnd.Column)
                    .Append(' ');
            }

            builder.Append('(').Append(lineCount).Append(lineCount == 1 ? " line, " : " lines, ").Append(normalized.Length).Append(" characters)");

            var shown = normalized;
            var lines = shown.Split(new[] { '\n', '\u0085', '\u2028', '\u2029' });
            if (lines.Length > MaxSelectionLines)
            {
                shown = string.Join("\n", lines, 0, MaxSelectionLines);
            }

            if (shown.Length > MaxSelectionCharacters)
            {
                shown = shown.Substring(0, MaxSelectionCharacters);
            }

            builder.Append("\n```\n").Append(shown);
            if (shown.Length < normalized.Length)
            {
                builder.Append('\n').Append(TextUtil.Ellipsis).Append("[selection truncated: ").Append(normalized.Length - shown.Length).Append(" more characters not shown]");
            }

            builder.Append("\n```");
        }
    }
}
