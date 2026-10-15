using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.VisualStudio;
using Microsoft.VisualStudio.Text;
using Microsoft.VisualStudio.Text.Editor;
using Microsoft.VisualStudio.Threading;
using Pomp.Features;
using Pomp.Tools;
using Task = System.Threading.Tasks.Task;
namespace Pomp.VisualStudio.Adapters
{
    internal sealed class SnippetCapture : ICodeSnippetCapture
    {
        private readonly VsAdapterContext context;
        public SnippetCapture(VsAdapterContext context){this.context=context;}
        public async Task<ShadowFile> CaptureAsync(string root,string code,CancellationToken token)
        {
            await context.Jtf.SwitchToMainThreadAsync(token);
            if(ErrorHandler.Failed(context.TextManager.GetActiveView(0,null,out var native))||native==null)throw new InvalidOperationException(Pomp.Hosting.PompLanguage.Pick("열린 소스 편집기가 없어요.", "No source editor is open."));
            var view=context.EditorAdapters.GetWpfTextView(native);
            if(view==null||view.IsClosed||!context.TextDocuments.TryGetTextDocument(view.TextBuffer,out var document))throw new InvalidOperationException(Pomp.Hosting.PompLanguage.Pick("활성 편집기가 소스 파일이 아니에요.", "The active editor is not a source file."));
            var broker=view.GetMultiSelectionBroker();
            if(broker.HasMultipleSelections||view.Selection.Mode==TextSelectionMode.Box||view.Caret.Position.VirtualBufferPosition.VirtualSpaces!=0)throw new InvalidOperationException(Pomp.Hosting.PompLanguage.Pick("선택 영역이 올바르지 않아요.", "The selection is invalid."));
            var snapshot=view.TextSnapshot;
            if(snapshot.Length>500000)throw new InvalidOperationException(Pomp.Hosting.PompLanguage.Pick("소스 파일이 너무 커요.", "The source file is too large."));
            var selected=broker.AllSelections.Select(s=>s.Extent.SnapshotSpan).ToArray();
            var span=selected.Length==0?new SnapshotSpan(snapshot,view.Caret.Position.BufferPosition.Position,0):selected[0];
            if(span.Snapshot!=snapshot)throw new InvalidOperationException(Pomp.Hosting.PompLanguage.Pick("편집기 선택 영역이 바뀌었어요.", "The editor selection changed."));
            var path=document.FilePath;
            var prefix=System.IO.Path.GetFullPath(root).TrimEnd('\\','/')+System.IO.Path.DirectorySeparatorChar;
            if(!System.IO.Path.GetFullPath(path).StartsWith(prefix,StringComparison.OrdinalIgnoreCase))throw new InvalidOperationException(Pomp.Hosting.PompLanguage.Pick("활성 편집기가 작업 영역 밖이에요.", "The active editor is outside the workspace."));
            var before=snapshot.GetText();var start=span.Start.Position;var length=span.Length;
            await TaskScheduler.Default;token.ThrowIfCancellationRequested();
            var resolved=new PathGuard(root).Resolve(path);
            return SnippetProposal.Replace(resolved,before,start,length,code);
        }
    }
}
