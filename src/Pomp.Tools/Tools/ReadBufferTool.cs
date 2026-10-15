using System;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;

namespace Pomp.Tools
{
    /// <summary>
    /// <c>read_buffer</c>: a file as Visual Studio currently has it (the open editor buffer first), in omp's hashline
    /// read format: a <c>[path#TAG]</c> header and <c>N:TEXT</c> rows. The read version is recorded in the
    /// <see cref="BufferSnapshotStore"/> so <c>edit_buffer_hashline</c> can detect stale edits later.
    /// </summary>
    internal sealed class ReadBufferTool : HostToolBase
    {
        /// <summary>Most lines returned by one call.</summary>
        internal const int MaxLines = 2000;

        /// <summary>Most characters of numbered text returned by one call (protects against huge lines).</summary>
        internal const int MaxCharacters = 100000;

        public ReadBufferTool(HostToolContext context)
            : base(context, CreateDefinition())
        {
        }

        private static HostToolDefinition CreateDefinition()
        {
            return new HostToolDefinition
            {
                Name = HostToolNames.ReadBuffer,
                Label = "Read Buffer",
                Description =
                    "Read a text file as Visual Studio currently has it: the open editor buffer including unsaved changes, otherwise the file on disk. " +
                    "Output is a `[path#TAG]` header (path relative to the workspace root; TAG = 4 hex characters identifying this exact content) " +
                    "followed by one `N:text` row per line (N = 1-based line number, text verbatim after the first colon). " +
                    "Returns at most " + MaxLines + " lines per call; when more remain, the row before the [contentHash] footer says which startLine continues. " +
                    "For large files use outline=true to get a bounded symbol summary with line numbers and nesting instead of source rows, then read only the needed ranges. " +
                    "An outline keeps the TAG but does not mark any source lines as shown; read the complete anchor/range before editing. " +
                    "Do not combine outline=true with startLine or endLine. " +
                    "Use it instead of the built-in read for files that may have unsaved changes in Visual Studio (the built-in read only sees the file on disk). " +
                    "Do not overwrite a file that has unsaved changes with tools that write to disk: ask the user to save it in Visual Studio first. " +
                    "Paths outside the workspace root are rejected.",
                Parameters = new SchemaBuilder()
                    .String("path", "File path, absolute or relative to the workspace root (forward or back slashes).", required: true, minLength: 1)
                    .Integer("startLine", "First line to return (1-based).", 1, null, 1)
                    .Integer("endLine", "Last line to return (inclusive). Default: end of file, capped at " + MaxLines + " lines per call.", 1)
                    .Boolean("outline", "Return a bounded symbol outline instead of source lines (no lines become eligible as edit anchors). Cannot be combined with startLine/endLine.", false)
                    .Build(),
                LoadMode = EssentialLoadMode,
            };
        }

        protected override async Task<HostToolResult> ExecuteCoreAsync(HostToolInvocation invocation, CancellationToken cancellationToken)
        {
            var args = new ToolArguments(invocation.Arguments, "path", "startLine", "endLine", "outline");
            var path = args.RequiredString("path");
            var startLine = args.OptionalInt("startLine", 1, int.MaxValue);
            var endLine = args.OptionalInt("endLine", 1, int.MaxValue);
            var outline = args.OptionalBool("outline") ?? false;
            if (outline && (startLine.HasValue || endLine.HasValue))
            {
                args.AddProblem("'outline' cannot be combined with 'startLine' or 'endLine'");
            }
            if (startLine.HasValue && endLine.HasValue && endLine.Value < startLine.Value)
            {
                args.AddProblem("'endLine' (" + endLine.Value + ") must not be less than 'startLine' (" + startLine.Value + ")");
            }

            if (args.TryGetError(Name, out var invalid))
            {
                return invalid;
            }

            var guard = await Context.GetPathGuardAsync(cancellationToken).ConfigureAwait(false);
            if (!guard.TryResolve(path, out var fullPath, out var pathError))
            {
                return Fail(pathError);
            }

            var display = guard.ToDisplayPath(fullPath);
            if (IsDirectory(fullPath))
            {
                return Fail("'" + display + "' is a directory, not a file. Use query_solution to browse the project tree.");
            }

            var snapshot = await Adapters.TextBuffers.ReadAsync(fullPath, cancellationToken).ConfigureAwait(false);
            if (snapshot == null)
            {
                return Fail("File not found: " + display + ". Use query_solution to find the exact path.");
            }

            var view = BufferView.Create(snapshot);
            if (view.LooksBinary)
            {
                return Fail("'" + display + "' looks like a binary file; only text files can be read.");
            }

            var total = view.Lines.Count;
            if (outline)
            {
                var symbols = SymbolOutline.Create(fullPath, view.Lines);
                var summary = new StringBuilder(HashlineFormat.Header(display, view.Tag));
                summary.Append("\nSymbols (navigation only; no source lines marked as shown):\n")
                    .Append(symbols.Length == 0 ? "[no symbols found for this file type or content]" : symbols);

                // Retain the version/TAG but grant no new anchors: signatures can be shortened or omit literals.
                var outlineEntry = Context.Snapshots.Record(fullPath, view.ContentHash, view.Lines, 1, 0);
                if (view.IsDirty)
                {
                    summary.Append("\n[unsaved changes: showing the editor buffer, not the file on disk]");
                }

                if (outlineEntry == null)
                {
                    summary.Append("\n[file is too large to keep an edit snapshot; line-anchored edits cannot target it]");
                }

                summary.Append("\n[contentHash: ").Append(view.ContentHash).Append(']');
                return HostToolResult.Text(summary.ToString(), new JObject
                {
                    ["path"] = display,
                    ["tag"] = view.Tag,
                    ["outline"] = true,
                    ["startLine"] = 1,
                    ["endLine"] = 0,
                    ["totalLines"] = total,
                    ["unsaved"] = view.IsDirty,
                    ["truncated"] = symbols.EndsWith(" more symbols]", StringComparison.Ordinal),
                    ["contentHash"] = view.ContentHash,
                });
            }

            var builder = new StringBuilder(HashlineFormat.Header(display, view.Tag));
            var start = startLine ?? 1;
            var end = 0;
            var limited = false;

            if (total == 0)
            {
                builder.Append("\n[empty file]");
                start = 1;
            }
            else
            {
                if (start > total)
                {
                    return Fail("startLine " + start + " is past the end of '" + display + "', which has " + total + " line(s).");
                }

                var requestedEnd = Math.Min(endLine ?? total, total);
                end = Math.Min(requestedEnd, start + MaxLines - 1);
                limited = requestedEnd > end;

                var last = HashlineFormat.AppendNumberedLines(builder, view.Lines, start, end, MaxCharacters);
                if (last < end)
                {
                    limited = true;
                    end = last;
                }
            }

            var entry = Context.Snapshots.Record(fullPath, view.ContentHash, view.Lines, start, end);

            if (view.IsDirty)
            {
                builder.Append("\n[unsaved changes: showing the editor buffer, not the file on disk]");
            }

            if (entry == null)
            {
                builder.Append("\n[file is too large to keep an edit snapshot; line-anchored edits cannot target it]");
            }

            if (total > 0)
            {
                if (end < start)
                {
                    builder.Append("\n[no complete line fits the output limit; this oversized line cannot be targeted by line-anchored editing]");
                }
                else if (limited)
                {
                    builder.Append("\n[showing lines ").Append(start).Append('-').Append(end).Append(" of ").Append(total)
                        .Append("; output limit reached, continue with startLine=").Append(end + 1).Append(']');
                }
                else if (start > 1 || end < total)
                {
                    builder.Append("\n[showing lines ").Append(start).Append('-').Append(end).Append(" of ").Append(total).Append(']');
                }
            }

            var details = new JObject
            {
                ["path"] = display,
                ["tag"] = view.Tag,
                ["startLine"] = start,
                ["endLine"] = end,
                ["totalLines"] = total,
                ["unsaved"] = view.IsDirty,
                ["truncated"] = limited,
            };
            builder.Append("\n[contentHash: ").Append(view.ContentHash).Append(']');
            details["contentHash"] = view.ContentHash;
            return HostToolResult.Text(builder.ToString(), details);
        }

        private static bool IsDirectory(string fullPath)
        {
            try
            {
                return Directory.Exists(fullPath);
            }
            catch (Exception)
            {
                return false;
            }
        }
    }
}
