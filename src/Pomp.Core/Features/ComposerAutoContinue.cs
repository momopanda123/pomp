using System;
using System.Threading;
using System.Linq;
using System.Collections.Generic;
using System.Globalization;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;
using Pomp.Internal;
using Pomp.Hosting;
using Pomp.Ui;
namespace Pomp.Features
{
    internal enum ComposerContinuationOutcome { Deferred, Accepted, Failed }
    internal sealed class ComposerContinuation
    {
        public string Root="",SessionId="",WorkflowId="",Task="",Phase="";
        public long Revision,Arm;
    }
    /// <summary>Explicit UI opt-in, bounded automatic model continuation. Never restores approval/arming after restart.</summary>
    /// <remarks>Two limits end it, whichever is hit first: six follow-up runs, and the optional token budget (the conversation's model calls since enabling, counted as the engine counts an agent run's tokens: input + output + cache writes).</remarks>
    internal sealed partial class ComposerAutoContinue : IDisposable
    {
        public const string CommandMessage="workspace/composerAuto",StateMessage="workspace/composerAutoState";
        public const long MinTokenBudget=10000,MaxTokenBudget=50000000;
        private readonly Func<JObject> workflow;
        private readonly Func<bool> ready;
        private readonly Func<ComposerContinuation,CancellationToken,Task<ComposerContinuationOutcome>> send;
        private readonly UiHub ui;
        private readonly Func<long>? usage;
        private readonly Action<string,string>? notice;
        private readonly object gate=new object();
        private readonly Timer? timer;
        private CancellationTokenSource armCancellation=new CancellationTokenSource();
        private bool enabled,dispatching,disposed;
        private string root="",session="",id="",message=PompLanguage.Pick("자동 이어가기가 꺼져 있어요.", "Automatic continuation is off.");
        private int remaining;
        private long? tokenBudget;
        private long tokenBase,publishedUsed=-1;
        private readonly HashSet<string> usedControls=new HashSet<string>();
        private readonly Queue<string> controlOrder=new Queue<string>();
        private long arm,lastRevision=-1;
        /// <param name="usage">The conversation's cumulative model tokens (never decreasing); the budget is measured against it. Null = no token accounting, a budget is refused.</param>
        /// <param name="notice">Posts a chat notice (level, text) when a goal run is armed or could not be.</param>
        public ComposerAutoContinue(Func<JObject> workflow,Func<bool> ready,Func<ComposerContinuation,CancellationToken,Task<ComposerContinuationOutcome>> send,UiHub ui,bool startTimer=true,Func<long>? usage=null,Action<string,string>? notice=null)
        {this.workflow=workflow;this.ready=ready;this.send=send;this.ui=ui;this.usage=usage;this.notice=notice;if(startTimer)timer=new Timer(_=>TickAsync().Observe(),null,1000,1000);}
        public JObject View()
        {
            lock(gate)
            {
                var view=new JObject{["root"]=root,["sessionId"]=session,["id"]=id,["enabled"]=enabled,["remaining"]=remaining,["dispatching"]=dispatching,["message"]=message};
                if(enabled&&tokenBudget!=null){view["tokenBudget"]=tokenBudget;view["tokensUsed"]=UsedLocked();}
                return view;
            }
        }
        public void Publish(string? requestId=null){var view=View();view["requestId"]=requestId;ui.Post(StateMessage,view,UiDelivery.State);}
        private static bool Active(JObject view)=>!string.IsNullOrEmpty((string?)view["id"])&&!new[]{"empty","done","cancelled"}.Contains((string?)view["phase"]);
        /// <summary>Tokens the conversation used since the budget was set (monotonic counter minus its value then).</summary>
        private long UsedLocked()=>usage==null?0:Math.Max(0,usage()-tokenBase);
        private bool OverBudgetLocked()=>enabled&&tokenBudget!=null&&UsedLocked()>=tokenBudget;
        private static string BudgetText(long value)=>value.ToString("N0",CultureInfo.InvariantCulture);
        private static string BudgetUsedUp(long budget)=>PompLanguage.Pick("토큰 예산("+BudgetText(budget)+")을 모두 썼어요.", "Token budget ("+BudgetText(budget)+") used up.");
        /// <summary>The optional token budget of a request: null when empty or absent; otherwise a whole number from <see cref="MinTokenBudget"/> to <see cref="MaxTokenBudget"/> (a numeric string with thousands commas is accepted), else an <see cref="ArgumentException"/> the page shows.</summary>
        internal static long? ReadTokenBudget(JToken? value)
        {
            if(value==null||value.Type==JTokenType.Null)return null;
            if(value.Type==JTokenType.String&&string.IsNullOrWhiteSpace((string?)value))return null;
            long budget;
            try
            {
                if(value.Type==JTokenType.Integer)budget=checked((long)value);
                else if(value.Type==JTokenType.String&&long.TryParse(((string)value!).Replace(",","").Trim(),NumberStyles.None,CultureInfo.InvariantCulture,out var parsed))budget=parsed;
                else budget=-1;
            }
            catch(Exception ex) when (ex is ArgumentException||ex is OverflowException||ex is InvalidCastException){budget=-1;}
            if(budget<MinTokenBudget||budget>MaxTokenBudget)throw new ArgumentException(PompLanguage.Pick("토큰 예산은 "+BudgetText(MinTokenBudget)+"에서 "+BudgetText(MaxTokenBudget)+" 사이의 정수여야 해요.", "The token budget must be a whole number from "+BudgetText(MinTokenBudget)+" to "+BudgetText(MaxTokenBudget)+"."));
            return budget;
        }
        public void Handle(JObject args)
        {
            var current=workflow();var requestId=(string?)args["requestId"];if(requestId?.Length>128)return;
            lock(gate)
            {
                if(disposed)return;
                try
                {
                    if((string?)args["action"]=="status") { if((enabled||goal!=null)&&(root!=(string?)current["root"]||session!=(string?)current["sessionId"]||id!=(string?)current["id"]))StopLocked(PompLanguage.Pick("워크플로 컨텍스트가 바뀌어서 자동 이어가기가 꺼졌어요.", "Workflow context changed; automatic continuation is off.")); if(!enabled){root=(string?)current["root"]??"";session=(string?)current["sessionId"]??"";id=(string?)current["id"]??"";} }
                    else
                    {
                        if(string.IsNullOrEmpty(requestId))throw new ArgumentException(PompLanguage.Pick("제어 요청 ID가 비어 있어요.", "Control request ID is empty."));
                        if(!usedControls.Add(requestId!)){Publish(requestId);return;}
                        controlOrder.Enqueue(requestId!);while(controlOrder.Count>128)usedControls.Remove(controlOrder.Dequeue());
                        if((string?)args["root"]!=(string?)current["root"]||(string?)args["sessionId"]!=(string?)current["sessionId"]||(string?)args["id"]!=(string?)current["id"]||(long?)args["revision"]!=(long?)current["revision"])throw new InvalidOperationException(PompLanguage.Pick("워크플로가 바뀌었어요.", "Workflow changed."));
                        if((string?)args["action"]=="disable")StopLocked(PompLanguage.Pick("사용자가 자동 이어가기를 껐어요.", "Automatic continuation disabled by user."));
                        else if((string?)args["action"]=="enable")
                        {
                            if(dispatching)throw new InvalidOperationException(PompLanguage.Pick("이어가기가 진행 중이에요.", "A continuation is in progress."));
                            if(enabled)throw new InvalidOperationException(PompLanguage.Pick("자동 이어가기가 이미 켜져 있어요.", "Automatic continuation is already enabled."));
                            if(!Active(current)||(bool?)current["paused"]==true||string.IsNullOrEmpty((string?)current["sessionId"]))throw new InvalidOperationException(PompLanguage.Pick("진행 중인 POMP Workflow 워크플로가 없어요.", "No POMP Workflow is running."));
                            var budget=ReadTokenBudget(args["tokenBudget"]);
                            if(budget!=null&&usage==null)throw new InvalidOperationException(PompLanguage.Pick("토큰 사용량을 읽을 수 없어서 토큰 예산을 쓸 수 없어요.", "Token usage cannot be read, so a token budget is unavailable."));
                            StopLocked("");root=(string)current["root"]!;session=(string)current["sessionId"]!;id=(string)current["id"]!;
                            enabled=true;remaining=6;lastRevision=-1;ArmBudgetLocked(budget);
                            message=budget==null?PompLanguage.Pick("이 워크플로에서 후속 에이전트 실행을 최대 6회 허용했어요.", "Up to six follow-up agent runs are armed for this workflow.")
                                :PompLanguage.Pick("이 워크플로에서 후속 에이전트 실행을 최대 6회, 토큰 예산("+BudgetText(budget.Value)+") 안에서 허용했어요.", "Up to six follow-up agent runs, within a budget of "+BudgetText(budget.Value)+" tokens, are armed for this workflow.");
                        }
                        else throw new ArgumentException(PompLanguage.Pick("알 수 없는 자동 이어가기 제어예요.", "Unknown automatic continuation control."));
                    }
                }
                catch(Exception ex){message=ex.Message;}
            }
            Publish(requestId);
        }
        /// <summary>Starts counting tokens now; the count shown is what the conversation used since this moment.</summary>
        private void ArmBudgetLocked(long? budget){tokenBudget=budget;tokenBase=budget==null||usage==null?0:usage();publishedUsed=-1;}
        private void StopLocked(string reason)
        {enabled=false;remaining=0;tokenBudget=null;tokenBase=0;publishedUsed=-1;goal=null;arm++;armCancellation.Cancel();armCancellation.Dispose();armCancellation=new CancellationTokenSource();message=reason;}
        /// <summary>Turns automatic continuation and a pending goal off, showing <paramref name="reason"/>. Does nothing while neither is on, so the
        /// panel never reports a stop that did not happen (a chat message, a new conversation or a restart used to say it was turned off).</summary>
        public void Stop(string reason){lock(gate){if(disposed||(!enabled&&goal==null))return;StopLocked(reason);}Publish();}
        public bool IsCurrent(ComposerContinuation request)
        {
            var current=workflow();
            lock(gate)return !disposed&&enabled&&request.Arm==arm&&request.Root==(string?)current["root"]&&request.SessionId==(string?)current["sessionId"]&&request.WorkflowId==(string?)current["id"]&&request.Revision==(long?)current["revision"]&&(bool?)current["paused"]!=true&&Active(current)&&!OverBudgetLocked();
        }
        internal async Task TickAsync()
        {
            var current=workflow();ComposerContinuation? request=null;CancellationToken token=default;var publish=false;var notices=new List<(string Level,string Text)>();
            lock(gate)
            {
                if(disposed||dispatching)return;
                if(goal!=null)ObserveGoalLocked(current,notices,ref publish);
                if(enabled)request=PlanLocked(current,out token,ref publish);
            }
            foreach(var item in notices)notice?.Invoke(item.Level,item.Text);
            if(publish)Publish();
            if(request==null)return;
            ComposerContinuationOutcome outcome;
            try{outcome=await send(request,token).ConfigureAwait(false);}catch{outcome=ComposerContinuationOutcome.Failed;}
            lock(gate)
            {
                dispatching=false;
                if(!disposed&&request.Arm==arm)
                {
                if(outcome==ComposerContinuationOutcome.Accepted)
                {remaining--;lastRevision=request.Revision;message=PompLanguage.Pick("자동 이어가기를 보냈어요. 새 워크플로 진행 상황이 관찰되기를 기다리고 있어요.", "Automatic continuation sent; waiting for new observed workflow progress.");if(remaining<=0)StopLocked(PompLanguage.Pick("후속 에이전트 실행 6회를 모두 사용했어요.", "Six follow-up agent runs were used."));}
                else if(outcome==ComposerContinuationOutcome.Failed)StopLocked(PompLanguage.Pick("이어가기 결과는 확인되지 않았어요. 자동으로 다시 시도하지 않아요.", "Continuation was not confirmed. It will not be retried automatically."));
                }
            }
            Publish();
        }
        /// <summary>One look at an armed continuation: stop when the workflow ended or the budget is used up, otherwise the run to send now (null = nothing to do yet).</summary>
        private ComposerContinuation? PlanLocked(JObject current,out CancellationToken token,ref bool publish)
        {
            token=default;
            if(!Active(current)||(bool?)current["paused"]==true||root!=(string?)current["root"]||session!=(string?)current["sessionId"]||id!=(string?)current["id"]){StopLocked(PompLanguage.Pick("워크플로가 끝났거나 일시 중지되었거나 바뀌었어요. 자동 이어가기가 꺼졌어요.", "Workflow ended, paused or changed. Automatic continuation is off."));publish=true;return null;}
            // Checked on every look, not only when a run is about to go out: a run that finished (or is still running) past the budget ends it here, and no run starts over budget.
            if(OverBudgetLocked()){var budget=tokenBudget!.Value;StopLocked(BudgetUsedUp(budget));publish=true;return null;}
            if(tokenBudget!=null){var used=UsedLocked();if(used!=publishedUsed){publishedUsed=used;publish=true;}}
            if((string?)current["phase"]=="awaitingReview"||lastRevision==(long?)current["revision"])return null;
            bool hostReady;
            try { hostReady=ready(); } catch { StopLocked(PompLanguage.Pick("상태를 확인하지 못해서 자동 이어가기가 꺼졌어요.", "The state could not be confirmed; automatic continuation is off.")); publish=true; return null; }
            if(!hostReady)return null;
            dispatching=true;token=armCancellation.Token;
            return new ComposerContinuation{Root=root,SessionId=session,WorkflowId=id,Revision=(long?)current["revision"]??0,Arm=arm,Task=(string?)current["task"]??"",Phase=(string?)current["phase"]??""};
        }
        public void Dispose(){lock(gate){if(disposed)return;StopLocked(PompLanguage.Pick("닫혔어요", "Closed"));disposed=true;timer?.Dispose();armCancellation.Dispose();}}
    }
}
