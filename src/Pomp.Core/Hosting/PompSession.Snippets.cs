using System;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;
using Pomp.Features;
using Pomp.Internal;
using Pomp.Ui;
namespace Pomp.Hosting
{
    public sealed partial class PompSession
    {
        private async Task HandleSnippetAsync(JObject args)
        {
            var requestId=args["requestId"]?.Type==JTokenType.String?(string?)args["requestId"]:null;
            if(string.IsNullOrEmpty(requestId)||requestId!.Length>128)return;
            var result=new JObject{["requestId"]=requestId};
            using var cancellation=CancellationTokenSource.CreateLinkedTokenSource(_disposeCts.Token);cancellation.CancelAfter(TimeSpan.FromSeconds(15));var token=cancellation.Token;
            try
            {
                if(_workspaceReview==null||!(_adapters is ICodeSnippetAdapters snippet))throw new InvalidOperationException(PompLanguage.Pick("코드 블록 제안 준비를 사용할 수 없어요.", "Code-block staging is unavailable."));
                if(args["code"]?.Type!=JTokenType.String)throw new ArgumentException(PompLanguage.Pick("코드 텍스트가 비어 있어요.", "The code text is empty."));
                var expectedSession=(string?)args["sessionId"]; var expectedRoot=(string?)args["root"];
                var code=(string)args["code"]!;if(code.Length>40000||string.IsNullOrWhiteSpace(code)||code.IndexOf('\0')>=0)throw new ArgumentException(PompLanguage.Pick("코드 블록이 올바르지 않아요.", "The code block is invalid."));
                lock(_stateLock)if(_sessionSwitchUncertain||_info.IsStreaming||_info.HasPendingAsyncWork||_turns.Count!=0||expectedSession!=_info.SessionId)throw new InvalidOperationException(PompLanguage.Pick("현재 대화가 진행 중이에요.", "The current conversation is still running."));
                if(_ui.PendingRequestCount>0)throw new InvalidOperationException(PompLanguage.Pick("대기 중인 승인이나 질문이 있어요.", "An approval or question is pending."));
                var root=await _environment.GetWorkspaceRootAsync(token).ConfigureAwait(false);
                if(!string.Equals(root,expectedRoot,StringComparison.OrdinalIgnoreCase))throw new InvalidOperationException(PompLanguage.Pick("작업 영역이 바뀌었어요.", "Workspace changed."));
                var captured=await snippet.SnippetCapture.CaptureAsync(root,code,token).WithCancellation(token).ConfigureAwait(false);
                lock(_stateLock)if(expectedSession!=_info.SessionId)throw new InvalidOperationException(PompLanguage.Pick("소스를 캡처하는 동안 대화가 바뀌었어요.", "Conversation changed during source capture."));
                var staged=await _workspaceReview.StageCapturedAsync(root,captured,token).WithCancellation(token).ConfigureAwait(false);
                if(staged.IsError)throw new InvalidOperationException(staged.Content[0].Text);
                result["staged"]=true;result["path"]=captured.Path;
            }
            catch(Exception ex){result["error"]=ex.Message.Length>3000?ex.Message.Substring(0,3000):ex.Message;}
            _ui.Post("workspace/snippetResult",result);
        }
    }
}
