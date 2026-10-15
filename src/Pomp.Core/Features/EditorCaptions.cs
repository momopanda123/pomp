using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
namespace Pomp.Features
{
    public sealed class EditorCaptionTarget
    {
        public EditorCaptionTarget(string name, int line, int start, int length) { Name = name; Line = line; Start = start; Length = length; }
        public string Name { get; }
        public int Line { get; }
        public int Start { get; }
        public int Length { get; }
    }
    public sealed class EditorCaptionSnapshot
    {
        public EditorCaptionSnapshot(bool sourceMatched, IReadOnlyList<EditorCaptionTarget> targets) { SourceMatched = sourceMatched; Targets = targets; }
        public bool SourceMatched { get; }
        public IReadOnlyList<EditorCaptionTarget> Targets { get; }
    }
    public interface IEditorCaptionSource
    {
        Task<EditorCaptionSnapshot> CaptureAsync(string root, string path, string exactText, int firstLine, int lastLine, CancellationToken token);
    }
    public interface IEditorCaptionLocationSource : IEditorCaptionSource
    {
        Task<EditorCaptionSnapshot> CaptureLocationAsync(string root, string path, string exactText, int position, CancellationToken token);
    }
    public interface IEditorCaptionAdapters { IEditorCaptionSource EditorCaptions { get; } }
}
