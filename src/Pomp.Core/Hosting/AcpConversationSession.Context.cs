using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;
using Pomp.Features;
using Pomp.Ui;

namespace Pomp.Hosting
{
    internal sealed partial class AcpConversationSession
    {
        // base64 grows the bytes by a third and the whole prompt is one frame: 5 MiB of images leave room for the text.
        private const int ImageBytesLimit = 5 * 1024 * 1024;
        // The frame bound of the connection, less its envelope (method, request id, session id).
        private const int PromptBytesLimit = Pomp.Transport.NdjsonLineReader.DefaultMaxLineBytes - 16 * 1024;

        private async Task<JArray> BuildPromptAsync(ChatSendPayload send, EditorContextResult? context, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            string root; bool images;
            lock (_sync) { root = _root; images = (bool?)_agentCapabilities["promptCapabilities"]?["image"] == true; }
            var blocks = new JArray();
            if (context != null) blocks.Add(new JObject { ["type"] = "text", ["text"] = context.Text });
            if (!string.IsNullOrEmpty(send.Text)) blocks.Add(new JObject { ["type"] = "text", ["text"] = send.Text });
            var imageBytes = 0;
            foreach (var attachment in send.Attachments ?? Enumerable.Empty<ChatAttachment>())
            {
                token.ThrowIfCancellationRequested();
                var full = Path.GetFullPath(Path.IsPathRooted(attachment.Path) ? attachment.Path : Path.Combine(root, attachment.Path));
                if (!File.Exists(full)) throw new FileNotFoundException(PompLanguage.Pick("첨부 파일을 찾을 수 없어요: ", "Attachment not found: ") + full);
                var uri = new Uri(full).AbsoluteUri;
                var mime = ImageMime(full);
                if (mime == null)
                {
                    // resource_link is baseline ACP. Do not silently embed a potentially large/binary file as text.
                    blocks.Add(new JObject { ["type"] = "resource_link", ["uri"] = uri, ["name"] = Path.GetFileName(full) });
                    continue;
                }
                if (!images) throw new NotSupportedException(PompLanguage.Pick("현재 CLI가 이미지 입력을 지원하지 않아요.", "The current CLI does not support image prompts."));
                using var input = new FileStream(full, FileMode.Open, FileAccess.Read, FileShare.Read, 8192, useAsync: true);
                if (input.Length == 0 || input.Length > ImageBytesLimit - imageBytes)
                    throw new InvalidOperationException(PompLanguage.Pick("한 메시지의 이미지 파일 합계는 5 MiB 이하여야 해요.", "Image files in one message must total at most 5 MiB."));
                var bytes = new byte[(int)input.Length];
                var offset = 0;
                while (offset < bytes.Length)
                {
                    var count = await input.ReadAsync(bytes, offset, bytes.Length - offset, token).ConfigureAwait(false);
                    if (count == 0) throw new IOException(PompLanguage.Pick("이미지를 읽는 중에 파일이 바뀌었어요.", "The image changed while it was read."));
                    offset += count;
                }
                imageBytes += bytes.Length;
                blocks.Add(new JObject { ["type"] = "image", ["mimeType"] = mime, ["data"] = Convert.ToBase64String(bytes), ["uri"] = uri });
            }
            // What the connection would refuse as one frame is refused here, before the turn is shown as started.
            if (EncodedBytes(blocks) > PromptBytesLimit)
                throw new InvalidOperationException(PompLanguage.Pick(
                    "메시지와 이미지가 한 번에 보내기에는 너무 커요. 이미지나 내용을 줄여 주세요.",
                    "The message and its images are too large to send at once. Send fewer images or less text."));
            return blocks;
        }
        /// <summary>The UTF-8 size of the prompt blocks as compact JSON on the wire, without building that JSON.</summary>
        private static long EncodedBytes(JArray blocks)
        {
            long total = 2;
            foreach (var block in blocks.OfType<JObject>())
            {
                total += 3; // Braces and the separator after the block.
                foreach (var property in block.Properties())
                {
                    total += property.Name.Length + 4; // Quoted ASCII name, colon, separator.
                    var value = (string?)property.Value ?? "";
                    // base64 needs no escaping; any other text is measured as it is written (the wire also escapes U+2028/U+2029).
                    if (property.Name == "data") total += value.Length + 2;
                    else total += System.Text.Encoding.UTF8.GetByteCount(Newtonsoft.Json.JsonConvert.ToString(value))
                        + 3L * value.Count(c => c == '\u2028' || c == '\u2029');
                }
            }
            return total;
        }
        private static string? ImageMime(string path)
        {
            switch (Path.GetExtension(path).ToLowerInvariant())
            {
                case ".png": return "image/png";
                case ".jpg": case ".jpeg": return "image/jpeg";
                case ".gif": return "image/gif";
                case ".webp": return "image/webp";
                default: return null;
            }
        }

        public void PrefillComposer(ComposerPrefillPayload prefill)
        {
            if (prefill == null) throw new ArgumentNullException(nameof(prefill));
            if (prefill.RequestFiles?.Count > 0)
            {
                Notice("error", PompLanguage.Pick("전체 파일 요청은 기본 OMP 연결에서만 지원돼요. ACP에서는 지원되지 않아요.", "Whole-file requests require native OMP and are not supported by ACP."));
                return;
            }
            if (prefill.Target != null)
            {
                Notice("error", PompLanguage.Pick("이 편집기 요청의 에이전트·팀 대상은 OMP 연결에서만 사용할 수 있어요.", "The editor request's agent/team destination requires an OMP connection."));
                return;
            }
            var bounded = new ComposerPrefillPayload
            {
                Text = prefill.Text == null ? null : prefill.Text.Length <= 4000 ? prefill.Text : prefill.Text.Substring(0, 4000),
                Files = prefill.Files?.Where(file => !string.IsNullOrEmpty(file.FullPath) && file.FullPath.Length <= 1024 && file.Path.Length <= 1024).ToList(),
                IncludeEditorContext = prefill.IncludeEditorContext, Send = prefill.Send,
            };
            lock (_sync) { if (_disposed) return; _prefill = bounded; _prefillAt = DateTime.UtcNow; }
            if (!_ui.IsReady) return;
            lock (_sync) { if (!ReferenceEquals(_prefill, bounded)) return; _prefill = null; }
            _ui.Post(UiMessageTypes.ComposerPrefill, bounded, UiDelivery.State);
        }

        public async Task<int> PasteIntoBasketAsync(IReadOnlyList<string> paths, byte[]? png, CancellationToken token)
        {
            string root; lock (_sync) { ThrowIfDisposed(); root = _root; }
            var files = new List<ComposerPrefillFile>();
            foreach (var path in paths)
            {
                token.ThrowIfCancellationRequested();
                try
                {
                    var full = Path.GetFullPath(path);
                    if (File.Exists(full)) files.Add(new ComposerPrefillFile { FullPath = full, Path = WorkspaceFileSearch.Relative(root, full) });
                }
                catch (Exception ex) when (ex is ArgumentException || ex is NotSupportedException || ex is PathTooLongException) { }
            }
            if (png != null && png.Length > 0)
            {
                if (png.Length > ImageBytesLimit)
                    throw new InvalidOperationException(PompLanguage.Pick("클립보드 이미지가 5 MiB보다 커요.", "The clipboard image is larger than 5 MiB."));
                var folder = ProjectData.Folder(root, "clipboard");
                var path = Path.Combine(folder, "clipboard-" + Guid.NewGuid().ToString("N") + ".png");
                CheckpointStore.CheckDirectPath(path);
                Directory.CreateDirectory(folder);
                CheckpointStore.CheckDirectPath(path);
                using (var output = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None, 8192, useAsync: true))
                    await output.WriteAsync(png, 0, png.Length, token).ConfigureAwait(false);
                files.Add(new ComposerPrefillFile { FullPath = path, Path = WorkspaceFileSearch.Relative(root, path) });
            }
            token.ThrowIfCancellationRequested();
            if (files.Count > 0) PrefillComposer(new ComposerPrefillPayload { Files = files });
            return files.Count;
        }

        private async Task FindFilesAsync(JObject args)
        {
            WorkspaceFilesPayload result;
            try
            {
                string root; lock (_sync) root = _root;
                result = await WorkspaceFileSearch.FindAsync(_adapters, root, (string?)args["query"] ?? "", (int?)args["limit"] ?? 30, _logger, _lifetime.Token).ConfigureAwait(false);
            }
            catch (Exception ex) { result = new WorkspaceFilesPayload { Error = ex.Message }; }
            result.RequestId = (string?)args["requestId"] ?? "";
            _ui.Post(UiMessageTypes.WorkspaceFiles, result, UiDelivery.State);
        }
        private async Task PickFilesAsync(JObject args)
        {
            var result = new WorkspaceFilesPayload { RequestId = (string?)args["requestId"] ?? "" };
            try
            {
                if (_environment is not IPompFilePicker picker) throw new NotSupportedException(PompLanguage.Pick("이 환경에서는 파일 선택 창을 쓸 수 없어요.", "The host does not provide a file picker."));
                string root; lock (_sync) root = _root;
                var paths = await picker.PickFilesAsync(root, PompLanguage.Pick("첨부 파일 선택", "Select attachments"), null, true, _lifetime.Token).ConfigureAwait(false);
                result.Files = paths.Where(File.Exists).Select(path => new WorkspaceFileEntry
                { FullPath = Path.GetFullPath(path), Path = WorkspaceFileSearch.Relative(root, Path.GetFullPath(path)) }).ToList();
            }
            catch (Exception ex) { result.Error = ex.Message; }
            _ui.Post(UiMessageTypes.WorkspaceFiles, result, UiDelivery.State);
        }
        private async Task CountBasketAsync(JObject args)
        {
            var result = new JObject { ["requestId"] = (string?)args["requestId"], ["estimated"] = true };
            try
            {
                if (args["requestFiles"] is JArray requests && requests.Count > 0)
                    throw new NotSupportedException(PompLanguage.Pick("전체 파일 요청은 기본 OMP 연결에서만 지원돼요. ACP에서는 지원되지 않아요.", "Whole-file requests require native OMP and are not supported by ACP."));
                lock (_sync)
                    if (!SameRoot((string?)args["root"] ?? "", _root) || (string?)args["sessionId"] != PublicId(_agentSessionId))
                        throw new InvalidOperationException(PompLanguage.Pick("이 컨텍스트 추정은 다른 대화의 것이에요.", "The context estimate belongs to another conversation."));
                var context = (bool?)args["includeEditorContext"] == true ? await _contextBuilder.BuildAsync(_lifetime.Token).ConfigureAwait(false) : null;
                var editor = ((context?.Text.Length ?? 0) + 3L) / 4;
                var attachments = ((args["attachments"] as JArray)?.OfType<JObject>().Sum(a => ((string?)a["path"] ?? "").Length + 3L) ?? 0) / 4;
                result["ok"] = true; result["tokens"] = editor + attachments; result["editorContext"] = editor; result["attachments"] = attachments;
            }
            catch (Exception ex) { result["ok"] = false; result["error"] = ex.Message; }
            _ui.Post("workspace/basketTokens", result, UiDelivery.State);
        }
    }
}
