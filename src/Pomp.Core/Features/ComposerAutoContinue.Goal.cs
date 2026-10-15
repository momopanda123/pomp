using System;
using System.Collections.Generic;
using Newtonsoft.Json.Linq;
using Pomp.Hosting;
namespace Pomp.Features
{
    // A goal run (the chat command /goal): the workflow command is sent, and automatic continuation is armed for the workflow that
    // start creates, with the token budget counted from the start (the first run is part of the goal).
    internal sealed partial class ComposerAutoContinue
    {
        /// <summary>How long a started goal waits for its workflow to appear, even when the conversation stays busy.</summary>
        internal static readonly TimeSpan GoalWindow=TimeSpan.FromMinutes(10);
        /// <summary>Tests shorten the wait.</summary>
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
        /// <summary>True from <see cref="BeginGoal"/> until the workflow appeared (armed), the start failed or automatic continuation was stopped.</summary>
        public bool GoalPending{get{lock(gate)return goal!=null;}}
        /// <summary>
        /// Registers a goal start for the conversation <paramref name="sessionId"/> of <paramref name="root"/>. Returns the reason it cannot
        /// start (an automatic continuation or a goal is already going, a workflow is active), else null. Nothing is sent here.
        /// </summary>
        public string? BeginGoal(string root,string sessionId,long? budget)
        {
            lock(gate)
            {
                if(disposed)return PompLanguage.Pick("닫혔어요", "Closed");
                if(goal!=null)return PompLanguage.Pick("목표를 이미 시작하는 중이에요.", "A goal run is already starting.");
                if(enabled||dispatching)return PompLanguage.Pick("자동 이어가기가 이미 켜져 있어요.", "Automatic continuation is already enabled.");
                var current=workflow();
                if(Active(current))return PompLanguage.Pick("이미 진행 중인 워크플로가 있어요.", "A workflow is already active.");
                if(budget!=null&&usage==null)return PompLanguage.Pick("토큰 사용량을 읽을 수 없어서 토큰 예산을 쓸 수 없어요.", "Token usage cannot be read, so a token budget is unavailable.");
                goal=new PendingGoal{Root=root,SessionId=sessionId,BaselineId=(string?)current["id"]??"",Budget=budget,Base=usage?.Invoke()??0,Deadline=DateTime.UtcNow+GoalTimeout};
                message=PompLanguage.Pick("목표를 시작하고 있어요.", "Starting the goal run.");
            }
            return null;
        }
        /// <summary>The goal's first prompt was accepted by the engine: from now on a finished turn without a workflow ends the wait.</summary>
        public void GoalSent(){lock(gate)if(goal!=null)goal.Sent=true;}
        /// <summary>The start could not be carried out (nothing was sent, or the send failed): forget the goal and say why.</summary>
        public void CancelGoal(string reason)
        {
            lock(gate){if(goal==null||disposed)return;goal=null;message=reason;}
            Publish();
        }
        private void FailGoalLocked(string reason,List<(string Level,string Text)> notices,ref bool publish)
        {
            goal=null;message=reason;publish=true;notices.Add(("warning",reason));
        }
        /// <summary>Looks for the workflow the goal start created (the first active one of this conversation that is not the one that was there before) and arms continuation for it.</summary>
        private void ObserveGoalLocked(JObject current,List<(string Level,string Text)> notices,ref bool publish)
        {
            var wanted=goal!;
            if(Active(current)&&(string?)current["id"]!=wanted.BaselineId)
            {
                if((string?)current["root"]!=wanted.Root||(string?)current["sessionId"]!=wanted.SessionId){FailGoalLocked(PompLanguage.Pick("다른 대화의 워크플로가 시작돼서 자동 이어가기를 켜지 않았어요.", "A workflow of another conversation started, so automatic continuation was not enabled."),notices,ref publish);return;}
                if((bool?)current["paused"]==true){FailGoalLocked(PompLanguage.Pick("워크플로가 일시 중지돼서 자동 이어가기를 켜지 않았어요.", "The workflow is paused, so automatic continuation was not enabled."),notices,ref publish);return;}
                goal=null;
                root=wanted.Root;session=wanted.SessionId;id=(string)current["id"]!;
                enabled=true;remaining=6;lastRevision=-1;
                // The first run belongs to the goal: counting starts where the goal started, not where the workflow appeared.
                tokenBudget=wanted.Budget;tokenBase=wanted.Base;publishedUsed=-1;
                message=wanted.Budget==null?PompLanguage.Pick("목표 실행에 자동 이어가기를 켰어요. 후속 에이전트 실행은 최대 6회예요.", "Automatic continuation is on for the goal run: up to six follow-up agent runs.")
                    :PompLanguage.Pick("목표 실행에 자동 이어가기를 켰어요. 후속 에이전트 실행은 최대 6회, 토큰 예산("+BudgetText(wanted.Budget.Value)+") 안에서 허용해요.", "Automatic continuation is on for the goal run: up to six follow-up agent runs, within a budget of "+BudgetText(wanted.Budget.Value)+" tokens.");
                publish=true;notices.Add(("info",message));
                return;
            }
            if(DateTime.UtcNow>wanted.Deadline){FailGoalLocked(PompLanguage.Pick("워크플로가 시작되지 않아서 자동 이어가기를 켜지 않았어요.", "No workflow started in time, so automatic continuation was not enabled."),notices,ref publish);return;}
            if(!wanted.Sent)return;
            bool hostReady;
            try{hostReady=ready();}catch{hostReady=false;}
            // The turn the goal started ended and no workflow came out of it: waiting longer would only arm a later, unrelated workflow.
            if(hostReady)FailGoalLocked(PompLanguage.Pick("워크플로가 시작되지 않아서 자동 이어가기를 켜지 않았어요.", "No workflow was started, so automatic continuation was not enabled."),notices,ref publish);
        }
    }
}
