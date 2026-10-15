using System;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Pomp.Hosting;
namespace Pomp.Features
{
    public interface ICodeSnippetCapture { Task<ShadowFile> CaptureAsync(string root, string code, CancellationToken token); }
    public interface ICodeSnippetAdapters { ICodeSnippetCapture SnippetCapture { get; } }
    public static class SnippetProposal
    {
        /// <summary>Compute a proposal from one exact editor snapshot and one contiguous selection/caret. No mutation.</summary>
        public static ShadowFile Replace(string path, string before, int start, int length, string code)
        {
            if (before.Length > 500000 || string.IsNullOrWhiteSpace(code) || code.Length > 40000 || code.IndexOf('\0') >= 0) throw new ArgumentException(PompLanguage.Pick("코드 블록이나 소스 파일이 올바르지 않아요.", "The code block or source file is invalid."));
            if (start < 0 || length < 0 || start > before.Length || length > before.Length - start) throw new ArgumentException(PompLanguage.Pick("편집기 선택 영역이 소스 스냅샷 범위를 벗어나요.", "The editor selection is outside its source snapshot."));
            var newline = Regex.Match(before,"\\r\\n|\\r|\\n").Value;
            if (newline.Length > 0) code = Regex.Replace(code,"\\r\\n|\\r|\\n",_=>newline);
            var after=before.Substring(0,start)+code+before.Substring(start+length);
            if(after.Length>500000)throw new ArgumentException(PompLanguage.Pick("결과 소스가 너무 커요.", "The resulting source is too large."));
            return new ShadowFile(path,before,after);
        }
    }
}
