using System;
using System.Globalization;
using System.Linq;
using System.Text.RegularExpressions;
using Pomp.Adapters;
using Pomp.Features;
using Pomp.Ui;

namespace Pomp.Hosting
{
    /// <summary>
    /// Recovers what the user typed from a prompt POMP composed (<see cref="PompSession"/> ComposeMessageAsync): the leading
    /// <c>&lt;vs_context&gt;</c> block and the attached-references paragraph are POMP's additions and are not shown in session
    /// titles, previews or history pages.
    /// </summary>
    internal static class PromptDisplay
    {
        /// <summary>
        /// Before the paths a slash command gets from the context basket (PompSession.ComposeSlashCommand). The headers name
        /// <c>read_buffer</c> only to a conversation that can call it (<see cref="PompSession"/> CanReadBuffer); an agent conversation has
        /// its role's tools only (docs/architecture.md §8.8) and gets the disk-read wording.
        /// </summary>
        internal static string CommandReferencesHeader(bool readBuffer) => "Work only on these workspace references, highest priority first (the user checked them for this command). " + ReferencesNote + (readBuffer ? BufferReadNote : DiskReadNote);

        internal static string AttachmentsHeader(bool readBuffer) => readBuffer ? BufferAttachmentsHeader : DiskAttachmentsHeader;

        private const string ReferencesNote = "Folder references are metadata, not recursively attached contents; inspect folders with bounded directory tools and select relevant files. ";
        private const string BufferReadNote = "Use read_buffer for workspace text to include unsaved editor changes; use read for images:";
        private const string DiskReadNote = "Use read for workspace text (the saved file, without unsaved editor changes) and images:";
        private const string BufferAttachmentsHeader = "Attached workspace references, highest priority first. " + ReferencesNote + BufferReadNote;
        private const string DiskAttachmentsHeader = "Attached workspace references, highest priority first. " + ReferencesNote + DiskReadNote;

        /// <summary>The extension's routing brief of a message for a team or one agent (docs/architecture.md §8.2, §8.3).</summary>
        private static readonly Regex RoutingBrief = new Regex(@"^<!-- pomp-(team|agent|dm):[A-Za-z0-9._:-]{1,128} -->[\s\S]*?<!-- /pomp-\1 -->", RegexOptions.CultureInvariant);

        /// <summary>The user's saved team notes the host appends to a message for that team (PompSession.Knowledge.cs).</summary>
        internal const string TeamKnowledgeOpen = "<team_knowledge>";
        internal const string TeamKnowledgeClose = "</team_knowledge>";

        /// <summary>A note can never open or close the block early (any case, any spacing around the slash).</summary>
        internal static string NeutralizeTeamKnowledge(string text) => NeutralizeTag(text, "team_knowledge");

        /// <summary>Breaks every <c>&lt;tag</c>/<c>&lt;/tag</c> spelling (case-insensitive, whitespace allowed) by replacing the underscore.</summary>
        internal static string NeutralizeTag(string text, string tag) =>
            Regex.Replace(text, @"<\s*(/?)\s*" + Regex.Escape(tag), match => "<" + match.Groups[1].Value + tag.Replace('_', '-'), RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

        /// <summary>The user's selected knowledge the host hands to the engine (PompSession.Knowledge.cs).</summary>
        internal const string SessionKnowledgeOpen = "<pomp_knowledge>";
        internal const string SessionKnowledgeClose = "</pomp_knowledge>";

        /// <summary>Selected knowledge can never open or close the block early (any case, any spacing around the slash).</summary>
        internal static string NeutralizeSessionKnowledge(string text) => NeutralizeTag(text, "pomp_knowledge");

        private const string ContextOpen = "<vs_context>";
        private const string ContextClose = "</vs_context>";
        private static readonly Regex ContextHeader = new Regex(@"^\r?\n(?:solution: [^\r\n]*\r?\n)?active_file: (?<path>[^\r\n]+)\r?\ncaret: line (?<caret>\d+), column \d+(?: \(\d+ lines\))?\r?\n(?:selection: (?<start>\d+):(?<startColumn>\d+)-(?<end>\d+):(?<endColumn>\d+)\r?\n)?", RegexOptions.CultureInvariant);

        /// <summary>Recovers only the leading host-added editor block, never a context tag inside the user's text.</summary>
        public static ChatEditorContext? EditorContext(string? prompt, string? root)
        {
            if (string.IsNullOrEmpty(prompt)) return null;
            var text = prompt!.TrimStart();
            var brief = RoutingBrief.Match(text);
            if (brief.Success) text = text.Substring(brief.Length).TrimStart();
            if (!text.StartsWith(ContextOpen, StringComparison.Ordinal)) return null;
            var end = text.IndexOf(ContextClose, StringComparison.Ordinal);
            if (end < 0) return null;
            var header = ContextHeader.Match(text.Substring(ContextOpen.Length, end - ContextOpen.Length));
            if (!header.Success || !TryLine(header.Groups["caret"].Value, out var caret)) return null;
            var path = header.Groups["path"].Value;
            var suffix = path.LastIndexOf(" [", StringComparison.Ordinal);
            if (suffix >= 0 && path.EndsWith("]", StringComparison.Ordinal)) path = path.Substring(0, suffix);
            if (string.IsNullOrWhiteSpace(path) || path == "(none)") return null;
            var document = new ActiveDocumentInfo { Path = path, Caret = new TextPosition { Line = caret } };
            if (TryLine(header.Groups["start"].Value, out var start) && TryLine(header.Groups["end"].Value, out var last)
                && TryLine(header.Groups["startColumn"].Value, out var startColumn) && TryLine(header.Groups["endColumn"].Value, out var endColumn))
            {
                document.SelectionStart = new TextPosition { Line = start, Column = startColumn };
                document.SelectionEnd = new TextPosition { Line = last, Column = endColumn };
            }
            return EditorContextBuilder.Reference(document, root);
        }

        private static bool TryLine(string text, out int value) =>
            int.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out value) && value > 0;

        /// <param name="keepRoutingBrief">
        /// Keep the extension's routing brief of a team or agent message: the restored chat shows it collapsed. Plain-text places (titles,
        /// previews, archive pages) leave it out.
        /// </param>
        public static string UserText(string? prompt, bool keepRoutingBrief = false)
        {
            if (string.IsNullOrEmpty(prompt))
            {
                return string.Empty;
            }

            var text = prompt!.TrimStart();
            // The extension puts its brief before POMP's own additions.
            var brief = RoutingBrief.Match(text);
            if (!brief.Success)
            {
                return WithoutPompAdditions(text);
            }

            var rest = WithoutPompAdditions(text.Substring(brief.Length).TrimStart());
            return !keepRoutingBrief ? rest : rest.Length == 0 ? brief.Value : brief.Value + "\n\n" + rest;
        }

        private static string WithoutPompAdditions(string text)
        {
            if (text.StartsWith(ContextOpen, StringComparison.Ordinal))
            {
                // The block's content is neutralized when built, so the first closing tag is the real end of the block.
                var end = text.IndexOf(ContextClose, StringComparison.Ordinal);
                if (end >= 0)
                {
                    text = text.Substring(end + ContextClose.Length).TrimStart();
                }
            }

            if (text.StartsWith(BufferAttachmentsHeader, StringComparison.Ordinal) || text.StartsWith(DiskAttachmentsHeader, StringComparison.Ordinal))
            {
                var lines = text.Split('\n');
                var index = 1;
                while (index < lines.Length && lines[index].StartsWith("- ", StringComparison.Ordinal))
                {
                    index++;
                }

                text = string.Join("\n", lines.Skip(index)).TrimStart();
            }

            // Team notes come last; their content is neutralized, so the last opening tag is the block's start.
            var trimmed = text.TrimEnd();
            if (trimmed.EndsWith(TeamKnowledgeClose, StringComparison.Ordinal))
            {
                var start = trimmed.LastIndexOf(TeamKnowledgeOpen, StringComparison.Ordinal);
                if (start >= 0)
                {
                    text = trimmed.Substring(0, start).TrimEnd();
                }
            }

            return text;
        }
    }
}
