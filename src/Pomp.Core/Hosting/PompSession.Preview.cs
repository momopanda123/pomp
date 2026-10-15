using System;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;
using Pomp.Features;
using Pomp.Diagnostics;
using Pomp.Internal;

namespace Pomp.Hosting
{
    // preview/open: a workspace .svg/.html file in the script-free preview tab (docs/ui-protocol.md, Preview).
    public sealed partial class PompSession
    {
        private async Task HandlePreviewOpenAsync(JObject payload)
        {
            try
            {
                if (!(_environment is IFilePreviewPresenter presenter))
                    throw new InvalidOperationException(PompLanguage.Pick("미리보기를 쓸 수 없어요.", "Preview is unavailable."));
                var root = await _environment.GetWorkspaceRootAsync(_disposeCts.Token).ConfigureAwait(false);
                var full = FilePreview.Resolve(root, (string?)payload["path"] ?? string.Empty);
                await presenter.ShowPreviewAsync(full, root, _disposeCts.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (_disposeCts.IsCancellationRequested) { }
            catch (InvalidOperationException ex) { PostNotice("warning", ex.Message); }
            catch (Exception ex)
            {
                _logger.Warn("The preview could not be opened: " + ex.GetType().Name);
                PostNotice("warning", PompLanguage.Pick("미리보기를 열지 못했어요.", "The preview could not be opened."));
            }
        }
    }
}
