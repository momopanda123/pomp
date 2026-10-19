using System.Threading;
using System.Threading.Tasks;
namespace Pomp.Features
{
    public sealed class InlineRequest
    {
        public bool IsCaption { get; set; }
        public bool IsEdit { get; set; }
        public string Prefix { get; set; } = string.Empty;
        public string Selection { get; set; } = string.Empty;
        public string Suffix { get; set; } = string.Empty;
        public string Instruction { get; set; } = string.Empty;
        public string Language { get; set; } = string.Empty;
    }
    public interface IInlineModelSettings
    {
        /// <summary>The editor model: empty = automatic (omp's fast model), <c>@chat</c> = the chat model, else a model string (<c>provider/id</c>).</summary>
        string? InlineModel { get; }
        /// <summary>How long one automatic completion may take, in seconds (<see cref="InlineCompletionLimits"/>).</summary>
        int InlineCompletionTimeoutSeconds { get; }
    }

    /// <summary>Bounds of the completion time limit option; a value outside them is clamped.</summary>
    public static class InlineCompletionLimits
    {
        public const int DefaultSeconds = 10, MinSeconds = 2, MaxSeconds = 60;
    }

    /// <summary>Saves the editor model chosen in the chat window (the same options value as <see cref="IInlineModelSettings.InlineModel"/>).</summary>
    public interface IInlineModelSettingsWriter
    {
        Task SetInlineModelAsync(string value, CancellationToken cancellationToken);
    }
    public static class InlineEditText
    {
        /// <summary>
        /// Fits a model's replacement for a selection to the document: its line breaks become the document's (<paramref name="lineEnding"/>),
        /// and the selection's own trailing line break is kept or left out as it was — models trim or add one, and a CRLF file must not get
        /// LF lines (every line of the region would show as changed in the review and the file would end up with mixed line endings).
        /// </summary>
        public static string FitReplacement(string replacement, string selection, string lineEnding)
        {
            var text = (replacement ?? string.Empty).Replace("\r\n", "\n").Replace('\r', '\n');
            if (selection.Length > 0)
            {
                var selectionEndsWithBreak = selection.EndsWith("\n", System.StringComparison.Ordinal) || selection.EndsWith("\r", System.StringComparison.Ordinal);
                var textEndsWithBreak = text.EndsWith("\n", System.StringComparison.Ordinal);
                if (selectionEndsWithBreak && !textEndsWithBreak) text += "\n";
                else if (!selectionEndsWithBreak && textEndsWithBreak) text = text.Substring(0, text.Length - 1);
            }

            return lineEnding == "\n" ? text : text.Replace("\n", lineEnding);
        }
    }

    public interface IInlineAssistant
    {
        Task<string> CompleteInlineAsync(InlineRequest request, CancellationToken cancellationToken);
        Task StageInlineEditAsync(string path, string beforeHash, string after, CancellationToken cancellationToken);
        Task StageInlineSnapshotAsync(string path, string before, string after, CancellationToken cancellationToken);
    }
}
