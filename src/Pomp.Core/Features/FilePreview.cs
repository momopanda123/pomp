using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Pomp.Hosting;

namespace Pomp.Features
{
    /// <summary>Optional capability of the host environment: shows a workspace .svg/.html file in the script-free preview tab.</summary>
    public interface IFilePreviewPresenter
    {
        /// <summary>Opens (or brings forward) the preview of <paramref name="fullPath"/>, already checked by <see cref="FilePreview.Resolve"/>.</summary>
        Task ShowPreviewAsync(string fullPath, string workspaceRoot, CancellationToken cancellationToken);
    }

    /// <summary>Which files the preview tab may show (docs/ui-protocol.md, Preview).</summary>
    public static class FilePreview
    {
        /// <summary>The largest file the preview reads.</summary>
        public const long MaxBytes = 2 * 1024 * 1024;

        /// <summary>True for the extensions the preview renders.</summary>
        public static bool IsPreviewable(string path)
        {
            var extension = Path.GetExtension(path);
            return string.Equals(extension, ".svg", StringComparison.OrdinalIgnoreCase)
                || string.Equals(extension, ".html", StringComparison.OrdinalIgnoreCase)
                || string.Equals(extension, ".htm", StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>
        /// The full path of <paramref name="path"/> (absolute, or relative to <paramref name="root"/>) when it is an existing previewable file
        /// inside the workspace, reached without links, and not over <see cref="MaxBytes"/>; otherwise an <see cref="InvalidOperationException"/>
        /// whose message can be shown to the user.
        /// </summary>
        public static string Resolve(string root, string path)
        {
            if (string.IsNullOrWhiteSpace(root)) throw Refuse("열린 작업 영역이 없어요.", "No workspace is open.");
            if (string.IsNullOrWhiteSpace(path) || path.Length > 1024 || path.IndexOf('\0') >= 0) throw Refuse("미리 볼 파일 경로가 올바르지 않아요.", "The preview path is not valid.");
            string full, workspace;
            try
            {
                workspace = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
                full = Path.GetFullPath(Path.IsPathRooted(path) ? path : Path.Combine(workspace, path));
            }
            catch (Exception ex) when (ex is ArgumentException || ex is NotSupportedException || ex is PathTooLongException)
            {
                throw Refuse("미리 볼 파일 경로가 올바르지 않아요.", "The preview path is not valid.");
            }

            if (!full.StartsWith(workspace + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                throw Refuse("작업 영역 밖의 파일은 미리 볼 수 없어요.", "Files outside the workspace cannot be previewed.");
            if (!IsPreviewable(full)) throw Refuse("SVG와 HTML 파일만 미리 볼 수 있어요.", "Only SVG and HTML files can be previewed.");
            try
            {
                CheckpointStore.CheckDirectPath(full);
                var info = new FileInfo(full);
                if (!info.Exists) throw Refuse("파일이 없어요.", "The file does not exist.");
                if (info.Length > MaxBytes) throw Refuse("파일이 너무 커요.", "The file is too large.");
            }
            catch (IOException) { throw Refuse("링크를 거치는 경로는 미리 볼 수 없어요.", "Paths through links cannot be previewed."); }
            catch (UnauthorizedAccessException) { throw Refuse("파일을 읽을 수 없어요.", "The file cannot be read."); }
            return full;
        }

        /// <summary>
        /// The theme contract of the bundled diagram and page-design skills: <c>data-theme</c> on the root element (<c>svg</c> or
        /// <c>html</c>). An existing <c>data-theme</c> is left alone; the profile's colour scheme covers files that only use the media query.
        /// </summary>
        public static string WithTheme(string text, string rootTag, string theme)
        {
            var start = 0;
            while (true)
            {
                var at = text.IndexOf("<" + rootTag, start, StringComparison.OrdinalIgnoreCase);
                if (at < 0) return text;
                var after = at + 1 + rootTag.Length;
                if (after < text.Length && (char.IsWhiteSpace(text[after]) || text[after] == '>' || text[after] == '/'))
                {
                    var end = text.IndexOf('>', after);
                    if (end < 0) return text;
                    if (text.IndexOf("data-theme", after, end - after, StringComparison.OrdinalIgnoreCase) >= 0) return text;
                    return text.Substring(0, after) + " data-theme=\"" + theme + "\"" + text.Substring(after);
                }

                start = after;
            }
        }

        private static InvalidOperationException Refuse(string ko, string en) => new InvalidOperationException(PompLanguage.Pick(ko, en));
    }
}
