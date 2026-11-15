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
        private bool GoalContinuationReady()
        {
            // The hub's lock is never taken inside the state lock (the page's ready handler nests them the other way).
            if(_ui.PendingRequestCount!=0)return false;
            bool idle,settled;
            lock(_stateLock){idle=!_disposeCts.IsCancellationRequested&&_bridge.State==BridgeState.Running&&!_sessionSwitchUncertain&&!_info.IsStreaming&&!_info.HasPendingAsyncWork&&_turns.Count==0&&_chatQueue.Count==0&&_dispatcher.ActiveCount==0&&!_terminalJobs.HasUnfinishedWork&&!_interactiveTerminal.HasUnfinishedWork;settled=_chatEngineSettled;}
            // A finished prompt or a local command can leave engine work behind: until the engine reports itself settled, a send would be
            // parked in the chat queue. Ask the engine again, so a goal that waits here goes on as soon as it has settled.
            if(idle&&!settled)RequestInfoRefresh();
            return idle&&settled;
        }
        private Task<GoalContinuationOutcome> QueueGoalContinuationAsync(GoalContinuation request,CancellationToken token)
        {
            var completion=new TaskCompletionSource<GoalContinuationOutcome>(TaskCreationOptions.RunContinuationsAsynchronously);
            _commands.Enqueue(async()=>
            {
                try
                {
                    token.ThrowIfCancellationRequested();
                    if(_goalRunner==null||!_goalRunner.IsCurrent(request)||!GoalContinuationReady()){completion.TrySetResult(GoalContinuationOutcome.Deferred);return;}
                    string root;
                    using(var check=CancellationTokenSource.CreateLinkedTokenSource(token,_disposeCts.Token))
                    {
                        check.CancelAfter(TimeSpan.FromSeconds(3));
                        var rootTask=_environment.GetWorkspaceRootAsync(check.Token);rootTask.Observe();
                        root=await rootTask.WithCancellation(check.Token).ConfigureAwait(false);
                    }
                    token.ThrowIfCancellationRequested();
                    if(!string.Equals(root,request.Root,StringComparison.OrdinalIgnoreCase)){completion.TrySetResult(GoalContinuationOutcome.Failed);return;}
                    if(!_goalRunner.IsCurrent(request)){completion.TrySetResult(GoalContinuationOutcome.Deferred);return;}
                    long generation;lock(_stateLock) { if (_info.SessionId != request.SessionId) { completion.TrySetResult(GoalContinuationOutcome.Failed); return; } generation=_chatStopGeneration; }
                    var text="[POMP Workflow goal follow-up, authorized by the user's confirmed /goal]\nContinue only the existing task: "+request.Task+"\nWorkflow "+request.WorkflowId+", last observed phase "+request.Phase+". First call composer_workflow status and use its current id/revision. Inspect actual outcomes, preserve unrelated work, and proceed with the next authorized step. Await user review for any proposal; never invent approval. Keep tool permissions and the project policy in force. Do not start a different task or claim native/runtime tests that did not run. Finish only when the workflow's observed requirements and the user's requested acceptance scope are met.";
                    PostNotice("info", PompLanguage.Pick("목표 실행을 이어서 진행하고 있어요.", "Goal execution is continuing."));
                    var accepted=await HandleChatSendAsync(new ChatSendPayload{Text=text,ClientMessageId="goal-follow-up-"+Guid.NewGuid().ToString("N"),IncludeEditorContext=false},generation,token,request.SessionId,bypassQueue:true,capturedPartner:new ConversationPartnerPayload()).ConfigureAwait(false);
                    completion.TrySetResult(accepted?GoalContinuationOutcome.Accepted:GoalContinuationOutcome.Failed);
                }
                catch{completion.TrySetResult(GoalContinuationOutcome.Failed);}
            });
            return completion.Task.WithCancellation(token);
        }
    }
}
