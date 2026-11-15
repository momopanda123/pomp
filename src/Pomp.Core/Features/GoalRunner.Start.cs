using System;
using System.Collections.Generic;
using Newtonsoft.Json.Linq;
using Pomp.Hosting;
namespace Pomp.Features
{
    internal sealed partial class GoalRunner
    {
        internal static readonly TimeSpan GoalWindow=TimeSpan.FromMinutes(10);
        internal TimeSpan GoalTimeout{get;set;}=GoalWindow;
        private sealed class PendingGoal
        {
            public string Root="",SessionId="",BaselineId="";
            public long? Budget;
            public long Base;
            public DateTime Deadline;
            public bool Sent;
        }
        private PendingGoal? goal;
        public bool GoalPending{get{lock(gate)return goal!=null;}}
        /// <summary>Registers a confirmed goal before its initial prompt is sent. No ordinary workflow can start the runner.</summary>
        public string? BeginGoal(string root,string sessionId,long? budget)
        {
            lock(gate)
            {
                if(disposed)return PompLanguage.Pick("닫혔어요", "Closed");
                if(goal!=null)return PompLanguage.Pick("목표를 이미 시작하는 중이에요.", "A goal run is already starting.");
                if(running||dispatching)return PompLanguage.Pick("목표를 이미 실행 중이에요.", "A goal run is already in progress.");
                var current=workflow();
                if(Active(current))return PompLanguage.Pick("이미 진행 중인 워크플로가 있어요.", "A workflow is already active.");
                if(budget!=null&&usage==null)return PompLanguage.Pick("토큰 사용량을 읽을 수 없어서 토큰 예산을 쓸 수 없어요.", "Token usage cannot be read, so a token budget is unavailable.");
                goal=new PendingGoal{Root=root,SessionId=sessionId,BaselineId=(string?)current["id"]??"",Budget=budget,Base=usage?.Invoke()??0,Deadline=DateTime.UtcNow+GoalTimeout};
                timer?.Change(1000,1000);
            }
            return null;
        }
        /// <summary>The goal's initial prompt was accepted; an idle turn without a workflow now ends the wait.</summary>
        public void GoalSent(){lock(gate)if(goal!=null)goal.Sent=true;}
        public void CancelGoal(string reason)
        {
            lock(gate){if(goal==null||disposed)return;StopLocked();}
            notice?.Invoke("warning",reason);
        }
        private void FailGoalLocked(string reason,List<(string Level,string Text)> notices)
        {StopLocked();notices.Add(("warning",reason));}
        private void ObserveGoalLocked(JObject current,List<(string Level,string Text)> notices)
        {
            var wanted=goal!;
            if(Active(current)&&(string?)current["id"]!=wanted.BaselineId)
            {
                if((string?)current["root"]!=wanted.Root||(string?)current["sessionId"]!=wanted.SessionId){FailGoalLocked(PompLanguage.Pick("다른 대화의 워크플로가 시작돼서 목표 실행을 중지했어요.", "Goal execution stopped because a workflow of another conversation started."),notices);return;}
                if((bool?)current["paused"]==true){FailGoalLocked(PompLanguage.Pick("워크플로가 일시 중지돼서 목표 실행을 중지했어요.", "Goal execution stopped because the workflow is paused."),notices);return;}
                goal=null;
                root=wanted.Root;session=wanted.SessionId;id=(string)current["id"]!;
                running=true;remaining=6;lastRevision=-1;
                // Include tokens consumed before the workflow appeared, starting with the initial goal prompt.
                tokenBudget=wanted.Budget;tokenBase=wanted.Base;
                var text=wanted.Budget==null?PompLanguage.Pick("목표 실행을 시작했어요. 후속 에이전트 실행은 최대 6회예요.", "Goal execution started with up to six follow-up agent runs.")
                    :PompLanguage.Pick("목표 실행을 시작했어요. 후속 에이전트 실행은 최대 6회, 토큰 예산("+BudgetText(wanted.Budget.Value)+") 안에서 허용해요.", "Goal execution started with up to six follow-up agent runs, within a budget of "+BudgetText(wanted.Budget.Value)+" tokens.");
                notices.Add(("info",text));
                return;
            }
            if(DateTime.UtcNow>wanted.Deadline){FailGoalLocked(PompLanguage.Pick("워크플로가 제시간에 시작되지 않아서 목표 실행을 중지했어요.", "Goal execution stopped because no workflow started in time."),notices);return;}
            if(!wanted.Sent)return;
            bool hostReady;
            try{hostReady=ready();}catch{hostReady=false;}
            // A later, unrelated workflow must not inherit this goal's authorization.
            if(hostReady)FailGoalLocked(PompLanguage.Pick("워크플로가 시작되지 않아서 목표 실행을 중지했어요.", "Goal execution stopped because no workflow was started."),notices);
        }
    }
}
