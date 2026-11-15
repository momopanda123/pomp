using System;
using System.Threading;
using System.Collections.Generic;
using System.Globalization;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;
using Pomp.Internal;
using Pomp.Hosting;
namespace Pomp.Features
{
    internal enum GoalContinuationOutcome { Deferred, Accepted, Failed }
    internal sealed class GoalContinuation
    {
        public string Root="",SessionId="",WorkflowId="",Task="",Phase="";
        public long Revision,Arm;
    }
    /// <summary>Runs only a user-confirmed goal, with at most six follow-up runs. Never restores authorization after restart.</summary>
    /// <remarks>The optional token budget includes the initial goal prompt and all follow-up model usage.</remarks>
    internal sealed partial class GoalRunner : IDisposable
    {
        public const long MinTokenBudget=10000,MaxTokenBudget=50000000;
        private readonly Func<JObject> workflow;
        private readonly Func<bool> ready;
        private readonly Func<GoalContinuation,CancellationToken,Task<GoalContinuationOutcome>> send;
        private readonly Func<long>? usage;
        private readonly Action<string,string>? notice;
        private readonly object gate=new object();
        private readonly Timer? timer;
        private CancellationTokenSource armCancellation=new CancellationTokenSource();
        private bool running,dispatching,disposed;
        private string root="",session="",id="";
        private int remaining;
        private long? tokenBudget;
        private long tokenBase;
        private long arm,lastRevision=-1;
        /// <param name="usage">The conversation's cumulative model tokens; null means token budgets are unavailable.</param>
        public GoalRunner(Func<JObject> workflow,Func<bool> ready,Func<GoalContinuation,CancellationToken,Task<GoalContinuationOutcome>> send,bool startTimer=true,Func<long>? usage=null,Action<string,string>? notice=null)
        {this.workflow=workflow;this.ready=ready;this.send=send;this.usage=usage;this.notice=notice;if(startTimer)timer=new Timer(_=>TickAsync().Observe(),null,Timeout.Infinite,Timeout.Infinite);}
        private static bool Active(JObject view)
        {
            var phase=(string?)view["phase"];
            return !string.IsNullOrEmpty((string?)view["id"])&&phase!="empty"&&phase!="done"&&phase!="cancelled";
        }
        private long UsedLocked()=>usage==null?0:Math.Max(0,usage()-tokenBase);
        private bool OverBudgetLocked()=>running&&tokenBudget!=null&&UsedLocked()>=tokenBudget;
        private static string BudgetText(long value)=>value.ToString("N0",CultureInfo.InvariantCulture);
        private static string BudgetUsedUp(long budget)=>PompLanguage.Pick("토큰 예산("+BudgetText(budget)+")을 모두 써서 목표 실행을 중지했어요.", "Goal execution stopped: token budget ("+BudgetText(budget)+") used up.");
        /// <summary>The optional whole-number token budget; empty means no budget.</summary>
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
        private void StopLocked()
        {running=false;remaining=0;tokenBudget=null;tokenBase=0;goal=null;arm++;timer?.Change(Timeout.Infinite,Timeout.Infinite);armCancellation.Cancel();armCancellation.Dispose();armCancellation=new CancellationTokenSource();}
        /// <summary>Stops a pending or running goal. An ordinary conversation has nothing to stop.</summary>
        public void Stop(string reason){lock(gate){if(disposed||(!running&&goal==null))return;StopLocked();}notice?.Invoke("info",reason);}
        public bool IsCurrent(GoalContinuation request)
        {
            var current=workflow();
            lock(gate)return !disposed&&running&&request.Arm==arm&&request.Root==(string?)current["root"]&&request.SessionId==(string?)current["sessionId"]&&request.WorkflowId==(string?)current["id"]&&request.Revision==(long?)current["revision"]&&(bool?)current["paused"]!=true&&Active(current)&&!OverBudgetLocked();
        }
        internal async Task TickAsync()
        {
            GoalContinuation? request=null;CancellationToken token=default;List<(string Level,string Text)> notices;
            lock(gate)
            {
                if(disposed||dispatching||(!running&&goal==null))return;
                var current=workflow();
                notices=new List<(string Level,string Text)>();
                if(goal!=null)ObserveGoalLocked(current,notices);
                if(running)request=PlanLocked(current,out token,notices);
            }
            foreach(var item in notices)notice?.Invoke(item.Level,item.Text);
            if(request==null)return;
            GoalContinuationOutcome outcome;
            try{outcome=await send(request,token).ConfigureAwait(false);}catch{outcome=GoalContinuationOutcome.Failed;}
            string? stopped=null;
            lock(gate)
            {
                dispatching=false;
                if(!disposed&&request.Arm==arm)
                {
                    if(outcome==GoalContinuationOutcome.Accepted)
                    {
                        remaining--;lastRevision=request.Revision;
                        if(remaining<=0)stopped=PompLanguage.Pick("후속 에이전트 실행 6회를 모두 써서 목표 실행을 중지했어요.", "Goal execution stopped after six follow-up agent runs.");
                    }
                    else if(outcome==GoalContinuationOutcome.Failed)stopped=PompLanguage.Pick("목표 실행 결과를 확인하지 못해서 중지했어요. 자동으로 다시 시도하지 않아요.", "Goal execution stopped because the follow-up was not confirmed. It will not be retried automatically.");
                    if(stopped!=null)StopLocked();
                }
            }
            if(stopped!=null)notice?.Invoke("info",stopped);
        }
        private GoalContinuation? PlanLocked(JObject current,out CancellationToken token,List<(string Level,string Text)> notices)
        {
            token=default;
            string? stopped=null;
            if(!Active(current)||(bool?)current["paused"]==true||root!=(string?)current["root"]||session!=(string?)current["sessionId"]||id!=(string?)current["id"])stopped=PompLanguage.Pick("워크플로가 끝났거나 일시 중지되었거나 바뀌어서 목표 실행을 중지했어요.", "Goal execution stopped because the workflow ended, paused or changed.");
            else if(OverBudgetLocked())stopped=BudgetUsedUp(tokenBudget!.Value);
            if(stopped!=null){StopLocked();notices.Add(("info",stopped));return null;}
            if((string?)current["phase"]=="awaitingReview"||lastRevision==(long?)current["revision"])return null;
            bool hostReady;
            try { hostReady=ready(); } catch { StopLocked();notices.Add(("warning",PompLanguage.Pick("상태를 확인하지 못해서 목표 실행을 중지했어요.", "Goal execution stopped because the state could not be confirmed.")));return null; }
            if(!hostReady)return null;
            dispatching=true;token=armCancellation.Token;
            return new GoalContinuation{Root=root,SessionId=session,WorkflowId=id,Revision=(long?)current["revision"]??0,Arm=arm,Task=(string?)current["task"]??"",Phase=(string?)current["phase"]??""};
        }
        public void Dispose(){lock(gate){if(disposed)return;StopLocked();disposed=true;timer?.Dispose();armCancellation.Dispose();}}
    }
}
