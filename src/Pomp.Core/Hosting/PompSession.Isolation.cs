using System;
using System.IO;
using System.Threading.Tasks;
using Pomp.Adapters;
using Pomp.Diagnostics;
using Pomp.Features;
using Pomp.Internal;

namespace Pomp.Hosting
{
    // Isolated team runs (docs/architecture.md §8.11): the engine keeps each member's changes as a patch; IsolationResults holds them
    // for review and applies one only through the approval card.
    public sealed partial class PompSession
    {
        private readonly IsolationResults? _isolationResults;

        private IsolationResults CreateIsolationResults(IVsAdapterFactory adapters, IPompHostEnvironment environment) =>
            new IsolationResults(adapters.TextBuffers, environment, _gate, _ui, _logger, (level, message) => PostNotice(level, message),
                async path =>
                {
                    var opened = await AdapterGuard.TryRunAsync(token => adapters.Editor.OpenDocumentAsync(path, null, token), _options.AdapterTimeout, _logger, "open isolation patch", _disposeCts.Token).ConfigureAwait(false);
                    if (!opened.Succeeded || !opened.Value) throw new InvalidOperationException(PompLanguage.Pick("편집기에서 패치를 열지 못했어요: " + path, "Could not open the patch in the editor: " + path));
                })
            { Applied = token => _workspaceReview?.PublishHistoryAsync(token) ?? Task.CompletedTask };

        /// <summary>The folder holding <c>.git</c> (a directory, or a file for worktrees and submodules) at or above <paramref name="start"/>.</summary>
        internal static string? FindGitRoot(string start)
        {
            if (string.IsNullOrWhiteSpace(start)) return null;
            try
            {
                for (var current = Path.GetFullPath(start); !string.IsNullOrEmpty(current); current = Path.GetDirectoryName(current))
                {
                    var marker = Path.Combine(current, ".git");
                    if (Directory.Exists(marker) || File.Exists(marker)) return current;
                }
            }
            catch (Exception ex) when (ex is ArgumentException || ex is IOException || ex is UnauthorizedAccessException || ex is NotSupportedException) { }
            return null;
        }
    }
}
