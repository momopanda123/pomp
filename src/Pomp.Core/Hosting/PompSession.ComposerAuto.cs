using System;
using System.Threading;
using System.Threading.Tasks;
using Pomp.Bridge;
using Pomp.Features;
using Pomp.Internal;
using Pomp.Ui;
namespace Pomp.Hosting
{
    public sealed partial class PompSession
    {
        private bool ComposerContinuationReady()
        {
            // The hub's lock is never taken inside the state lock (the page's ready handler nests them the other way).
            if(_ui.PendingRequestCount!=0)return false;
            lock(_stateLock)return !_disposeCts.IsCancellationRequested&&_bridge.State==BridgeState.Running&&!_sessionSwitchUncertain&&!_info.IsStreaming&&!_info.HasPendingAsyncWork&&_turns.Count==0&&_chatQueue.Count==0&&_dispatcher.ActiveCount==0&&!_terminalJobs.HasUnfinishedWork&&!_interactiveTerminal.HasUnfinishedWork;
        }
        private Task<ComposerContinuationOutcome> QueueComposerContinuationAsync(ComposerContinuation request,CancellationToken token)
        {
            var completion=new TaskCompletionSource<ComposerContinuationOutcome>(TaskCreationOptions.RunContinuationsAsynchronously);
            _commands.Enqueue(async()=>
            {
                try
                {
                    token.ThrowIfCancellationRequested();
                    if(_composerAuto==null||!_composerAuto.IsCurrent(request)||!ComposerContinuationReady()){completion.TrySetResult(ComposerContinuationOutcome.Deferred);return;}
                    string root;
                    using(var check=CancellationTokenSource.CreateLinkedTokenSource(token,_disposeCts.Token))
                    {
                        check.CancelAfter(TimeSpan.FromSeconds(3));
                        var rootTask=_environment.GetWorkspaceRootAsync(check.Token);rootTask.Observe();
                        root=await rootTask.WithCancellation(check.Token).ConfigureAwait(false);
                    }
                    token.ThrowIfCancellationRequested();
                    if(!string.Equals(root,request.Root,StringComparison.OrdinalIgnoreCase)){completion.TrySetResult(ComposerContinuationOutcome.Failed);return;}
                    if(!_composerAuto.IsCurrent(request)){completion.TrySetResult(ComposerContinuationOutcome.Deferred);return;}
                    long generation;lock(_stateLock) { if (_info.SessionId != request.SessionId) { completion.TrySetResult(ComposerContinuationOutcome.Failed); return; } generation=_chatStopGeneration; }
                    var text="[POMP Workflow automatic continuation, explicitly enabled in the workflow panel]\nContinue only the existing task: "+request.Task+"\nWorkflow "+request.WorkflowId+", last observed phase "+request.Phase+". First call composer_workflow status and use its current id/revision. Inspect actual outcomes, preserve unrelated work, and proceed with the next authorized step. Await user review for any proposal; never invent approval. Keep tool permissions and the project policy in force. Do not start a different task or claim native/runtime tests that did not run. Finish only when the workflow's observed requirements and the user's requested acceptance scope are met.";
                    PostNotice("info", PompLanguage.Pick("POMP Workflow가 이어서 작업하고 있어요.", "POMP Workflow is continuing."));
                    var accepted=await HandleChatSendAsync(new ChatSendPayload{Text=text,ClientMessageId="composer-auto-"+Guid.NewGuid().ToString("N"),IncludeEditorContext=false},generation,token,request.SessionId,bypassQueue:true,capturedPartner:new ConversationPartnerPayload()).ConfigureAwait(false);
                    completion.TrySetResult(accepted?ComposerContinuationOutcome.Accepted:ComposerContinuationOutcome.Failed);
                }
                catch{completion.TrySetResult(ComposerContinuationOutcome.Failed);}
            });
            return completion.Task.WithCancellation(token);
        }
    }
}
