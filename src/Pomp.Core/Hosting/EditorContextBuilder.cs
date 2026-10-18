using System;
using System.Collections.Generic;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Pomp.Adapters;
using Pomp.Diagnostics;
using Pomp.Internal;
using Pomp.Ui;

namespace Pomp.Hosting
{
    /// <summary>
    /// Builds the compact <c>&lt;vs_context&gt;</c> block that is placed before the user's message (design: addAdditionalContext):
    /// solution, active file with language/dirty flag, caret and selection, the lines around the caret (never the whole file), and
    /// Error List errors, each sized by <see cref="EditorContextLimits"/> (Tools > Options > Pomp > Chat). Every adapter call is guarded
    /// by a short timeout, so gathering context can never fail or stall a prompt.
    /// </summary>
    internal sealed class EditorContextBuilder
    {
        private readonly IVsAdapterFactory _adapters;
        private readonly IPompLogger _logger;
        private readonly Func<EditorContextLimits> _limits;

        /// <param name="limits">Read at each build, so applied options take effect with the next message.</param>
        public EditorContextBuilder(IVsAdapterFactory adapters, IPompLogger logger, Func<EditorContextLimits> limits)
        {
            _adapters = adapters;
            _logger = logger;
            _limits = limits;
        }

        /// <summary>Returns the context block and its editor reference, or null when Visual Studio has nothing to report.</summary>
        public async Task<EditorContextResult?> BuildAsync(CancellationToken cancellationToken)
        {
            if (cancellationToken.IsCancellationRequested)
            {
                return null;
            }

            var limits = _limits();
            var timeout = limits.Timeout;
            var documentTask = AdapterGuard.RunAsync(_adapters.Editor.GetActiveDocumentAsync, timeout, _logger, "context: active document", cancellationToken);
            // Only name/path/root are shown: the cheap location query, not the project enumeration.
            var solutionTask = AdapterGuard.RunAsync(_adapters.Solution.GetSolutionLocationAsync, timeout, _logger, "context: solution", cancellationToken);
            var errorsTask = limits.Errors == 0 ? Task.FromResult<IReadOnlyList<ErrorListItem>>(Array.Empty<ErrorListItem>()) : AdapterGuard.RunAsync(
                token => _adapters.ErrorList.ReadAsync(
                    new ErrorListFilter { Severities = new List<ErrorSeverity> { ErrorSeverity.Error }, MaxItems = limits.Errors },
                    token),
                timeout,
                _logger,
                "context: error list",
                cancellationToken);

            var document = await documentTask.ConfigureAwait(false);
            var solution = await solutionTask.ConfigureAwait(false);
            var errors = await errorsTask.ConfigureAwait(false);

            if (cancellationToken.IsCancellationRequested)
            {
                return null;
            }

            BufferSnapshot? snapshot = null;
            if (document != null && !string.IsNullOrEmpty(document.Path) && limits.WindowChars > 0)
            {
                var path = document.Path;
                snapshot = await AdapterGuard.RunAsync(token => _adapters.TextBuffers.ReadAsync(path, token), timeout, _logger, "context: buffer", cancellationToken).ConfigureAwait(false);
            }

            try
            {
                var text = Format(solution, document, snapshot, errors, limits);
                return text == null ? null : new EditorContextResult(text, Reference(document, solution?.RootDirectory));
            }
            catch (Exception ex)
            {
                _logger.Warn("Could not format the editor context: " + ex.Message);
                return null;
            }
        }

        /// <summary>Uses the same document snapshot as the text; selection ends at column 1 are exclusive.</summary>
        internal static ChatEditorContext? Reference(ActiveDocumentInfo? document, string? root)
        {
            if (document == null || string.IsNullOrEmpty(document.Path)) return null;
            var reference = new ChatEditorContext
            {
                Path = string.IsNullOrEmpty(root) ? document.Path : WorkspaceFileSearch.Relative(root!, document.Path),
                CaretLine = Math.Max(1, document.Caret?.Line ?? 1),
            };
            var start = document.SelectionStart;
            var end = document.SelectionEnd;
            if (start != null && end != null && (start.Line != end.Line || start.Column != end.Column))
            {
                if (start.Line > end.Line || (start.Line == end.Line && start.Column > end.Column))
                {
                    var swap = start; start = end; end = swap;
                }
                reference.StartLine = Math.Max(1, start.Line);
                reference.EndLine = Math.Max(reference.StartLine.Value, end.Column == 1 && end.Line > start.Line ? end.Line - 1 : end.Line);
            }
            return reference;
        }

        /// <summary>Characters of one Error List description (not an option: one line of an error says enough).</summary>
        private const int MaxErrorMessageChars = 300;

        /// <summary>Pure formatting step (unit tested).</summary>
        internal static string? Format(SolutionInfo? solution, ActiveDocumentInfo? document, BufferSnapshot? snapshot, IReadOnlyList<ErrorListItem>? errors, EditorContextLimits limits)
        {
            var hasSolution = solution != null && (!string.IsNullOrEmpty(solution.Name) || !string.IsNullOrEmpty(solution.RootDirectory));
            var hasDocument = document != null && !string.IsNullOrEmpty(document.Path);
            var hasErrors = errors != null && errors.Count > 0 && limits.Errors > 0;
            if (!hasSolution && !hasDocument && !hasErrors)
            {
                return null;
            }

            // The body is built first and neutralized as a whole: every value in it (file text, selection, diagnostic codes, paths,
            // project and solution names) comes from outside, and none of it may close the block and pose as instructions.
            var text = new StringBuilder();

            if (hasSolution)
            {
                text.Append("solution: ").Append(string.IsNullOrEmpty(solution!.Name) ? "(unnamed)" : solution.Name);
                if (!string.IsNullOrEmpty(solution.SolutionPath))
                {
                    text.Append(" (").Append(solution.SolutionPath).Append(')');
                }
                else if (!string.IsNullOrEmpty(solution.RootDirectory))
                {
                    text.Append(" (folder ").Append(solution.RootDirectory).Append(')');
                }

                text.Append('\n');
            }

            if (hasDocument)
            {
                AppendDocument(text, document!, snapshot, limits);
            }
            else
            {
                text.Append("active_file: (none)\n");
            }

            if (hasErrors)
            {
                AppendErrors(text, errors!, solution?.RootDirectory, limits.Errors);
            }

            return "<vs_context>\n" + Neutralize(text.ToString()) + "</vs_context>";
        }

        private static void AppendDocument(StringBuilder text, ActiveDocumentInfo document, BufferSnapshot? snapshot, EditorContextLimits limits)
        {
            text.Append("active_file: ").Append(document.Path);
            var details = new List<string>();
            if (!string.IsNullOrEmpty(document.Language))
            {
                details.Add(document.Language!);
            }

            if (document.IsDirty)
            {
                details.Add("unsaved changes (the editor buffer differs from disk)");
            }

            if (details.Count > 0)
            {
                text.Append(" [").Append(string.Join(", ", details)).Append(']');
            }

            text.Append('\n');

            var caretLine = Math.Max(1, document.Caret?.Line ?? 1);
            var caretColumn = Math.Max(1, document.Caret?.Column ?? 1);
            text.Append("caret: line ").Append(caretLine).Append(", column ").Append(caretColumn);
            if (document.LineCount > 0)
            {
                text.Append(" (").Append(document.LineCount).Append(" lines)");
            }

            text.Append('\n');

            if (document.SelectionStart != null && document.SelectionEnd != null
                && (document.SelectionStart.Line != document.SelectionEnd.Line || document.SelectionStart.Column != document.SelectionEnd.Column))
            {
                text.Append("selection: ")
                    .Append(document.SelectionStart.Line).Append(':').Append(document.SelectionStart.Column)
                    .Append('-')
                    .Append(document.SelectionEnd.Line).Append(':').Append(document.SelectionEnd.Column)
                    .Append('\n');
            }

            if (!string.IsNullOrEmpty(document.SelectedText) && limits.SelectedChars > 0)
            {
                text.Append("selected_text:\n```\n")
                    .Append(Neutralize(TextUtil.Truncate(document.SelectedText, limits.SelectedChars)))
                    .Append("\n```\n");
            }

            if (snapshot != null && !string.IsNullOrEmpty(snapshot.Text) && limits.WindowChars > 0)
            {
                AppendWindow(text, document, snapshot.Text, caretLine, limits);
            }
        }

        private static void AppendWindow(StringBuilder text, ActiveDocumentInfo document, string content, int caretLine, EditorContextLimits limits)
        {
            var radius = document.LineCount >= limits.LargeFileThreshold ? limits.LargeFileWindowLines : limits.WindowLines;
            var first = Math.Max(1, caretLine - radius);
            var last = caretLine + radius;
            if (document.LineCount > 0)
            {
                last = Math.Min(last, document.LineCount);
            }

            var lines = TextLines.Extract(content, first, last);
            if (lines.Count == 0)
            {
                return;
            }

            text.Append("lines ").Append(lines[0].Key).Append('-').Append(lines[lines.Count - 1].Key)
                .Append(" around the caret (line: text):\n");
            var budget = limits.WindowChars;
            foreach (var line in lines)
            {
                var shown = line.Value.Length > limits.LineChars ? line.Value.Substring(0, limits.LineChars) + "..." : line.Value;
                var row = line.Key + ": " + Neutralize(shown) + "\n";
                if (budget - row.Length < 0)
                {
                    text.Append("...(window truncated)\n");
                    break;
                }

                budget -= row.Length;
                text.Append(row);
            }
        }

        private static void AppendErrors(StringBuilder text, IReadOnlyList<ErrorListItem> errors, string? root, int maxErrors)
        {
            var count = Math.Min(errors.Count, maxErrors);
            text.Append("errors (").Append(count).Append(errors.Count > count ? "+" : string.Empty).Append("):\n");
            for (var i = 0; i < count; i++)
            {
                var error = errors[i];
                text.Append("- ");
                if (!string.IsNullOrEmpty(error.Code))
                {
                    text.Append(error.Code).Append(' ');
                }

                if (!string.IsNullOrEmpty(error.File))
                {
                    text.Append(MakeRelative(error.File!, root));
                    if (error.Line.HasValue)
                    {
                        text.Append('(').Append(error.Line.Value);
                        if (error.Column.HasValue)
                        {
                            text.Append(',').Append(error.Column.Value);
                        }

                        text.Append(')');
                    }

                    text.Append(": ");
                }

                text.Append(Neutralize(TextUtil.Truncate(OneLine(error.Description), MaxErrorMessageChars)));
                if (!string.IsNullOrEmpty(error.Project))
                {
                    text.Append(" [").Append(error.Project).Append(']');
                }

                text.Append('\n');
            }
        }

        private static string OneLine(string? text) => Regex.Replace(text ?? string.Empty, @"\s*[\r\n]+\s*", " ").Trim();

        /// <summary>
        /// File text and diagnostics are data: a line containing <c>&lt;/vs_context&gt;</c> must not be able to close the block early and
        /// pose as instructions outside it.
        /// </summary>
        internal static string Neutralize(string text) =>
            text.IndexOf("vs_context", StringComparison.OrdinalIgnoreCase) < 0
                ? text
                : Regex.Replace(text, @"<\s*/?\s*vs_context", match => match.Value.Replace("<", "&lt;"), RegexOptions.IgnoreCase);

        private static string MakeRelative(string file, string? root)
        {
            if (!string.IsNullOrEmpty(root))
            {
                var prefix = root!.TrimEnd('\\', '/') + "\\";
                if (file.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                {
                    return file.Substring(prefix.Length);
                }
            }

            return file;
        }
    }

    /// <summary>The injected text and visible reference gathered from one editor snapshot.</summary>
    internal sealed class EditorContextResult
    {
        public EditorContextResult(string text, ChatEditorContext? editorContext)
        {
            Text = text;
            EditorContext = editorContext;
        }

        public string Text { get; }
        public ChatEditorContext? EditorContext { get; }
    }

    internal static class TextLines
    {
        private static readonly char[] BreakChars = { '\r', '\n', '\u0085', '\u2028', '\u2029' };

        /// <summary>
        /// Returns lines <paramref name="first"/>..<paramref name="last"/> (1-based, inclusive) of <paramref name="text"/> with their
        /// numbers, scanning only as far as needed (no split of the whole text, so huge files stay cheap). Line breaks are
        /// <c>\r\n</c>, <c>\n</c>, a lone <c>\r</c>, U+0085, U+2028 or U+2029; text ending with a break has a final empty line, like an editor shows.
        /// </summary>
        public static List<KeyValuePair<int, string>> Extract(string text, int first, int last)
        {
            var lines = new List<KeyValuePair<int, string>>();
            if (string.IsNullOrEmpty(text) || last < first || last < 1)
            {
                return lines;
            }

            first = Math.Max(1, first);
            var lineNumber = 1;
            var start = 0;
            while (lineNumber <= last)
            {
                var end = text.IndexOfAny(BreakChars, start);
                var stop = end < 0 ? text.Length : end;
                if (lineNumber >= first)
                {
                    lines.Add(new KeyValuePair<int, string>(lineNumber, text.Substring(start, stop - start)));
                }

                if (end < 0)
                {
                    break;
                }

                start = end + 1;
                if (text[end] == '\r' && start < text.Length && text[start] == '\n')
                {
                    start++;
                }

                lineNumber++;
            }

            return lines;
        }
    }
}

