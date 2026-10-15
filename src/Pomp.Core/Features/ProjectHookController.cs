using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;
using Pomp.Hosting;
using Pomp.Internal;
using Pomp.Permissions;
using Pomp.Tools;
using Pomp.Ui;
namespace Pomp.Features
{
    internal sealed class ProjectHookController : IDisposable
    {
        public const string CommandMessage="workspace/hooks", StateMessage="workspace/hooksState";
        private readonly IProjectEventSource source;private readonly IPompHostEnvironment environment;private readonly IPermissionGate permissions;private readonly TerminalJobs terminal;private readonly UiHub ui;private readonly Func<bool> agentBusy;
        private readonly object gate=new object();private readonly CancellationTokenSource lifetime=new CancellationTokenSource();private CancellationTokenSource? active;
        /// <summary>The run history keeps its newest rows while they fit this many bytes: no count limit (the page pages it).</summary>
        private const int MaxHistoryBytes=256*1024;
        private void TrimHistory(){long total=0;var keep=0;foreach(var row in history){total+=System.Text.Encoding.UTF8.GetByteCount(row.ToString(Newtonsoft.Json.Formatting.None))+1;if(total>MaxHistoryBytes)break;keep++;}if(keep<history.Count)history.RemoveRange(keep,history.Count-keep);}
        private readonly List<JObject> history=new List<JObject>();private string root="",session="",message="";private bool enabled,disposed;private int running,attempts,skipped;private DateTimeOffset lastFinished;
        public ProjectHookController(IProjectEventSource source,IPompHostEnvironment environment,IPermissionGate permissions,TerminalJobs terminal,UiHub ui,Func<bool> agentBusy)
        {this.source=source;this.environment=environment;this.permissions=permissions;this.terminal=terminal;this.ui=ui;this.agentBusy=agentBusy;source.Occurred+=OnEvent;}
        public void SetSession(string workspace,string sessionId)
        {
            CancellationTokenSource? cancel;
            lock(gate){if(root==workspace&&session==sessionId)return;root=workspace;session=sessionId;enabled=false;attempts=0;skipped=0;message="";history.Clear();cancel=active;}
            try{cancel?.Cancel();}catch(ObjectDisposedException){} Publish();
        }
        public JObject View(){lock(gate)return new JObject{["root"]=root,["sessionId"]=session,["enabled"]=enabled,["running"]=Volatile.Read(ref running)!=0,["attempts"]=attempts,["skipped"]=skipped,["message"]=message,["history"]=new JArray(history.Select(h=>h.DeepClone()))};}
        public void Publish()=>ui.Post(StateMessage,View(),UiDelivery.State);
        public void Handle(JObject args)
        {
            CancellationTokenSource? cancel=null;
            lock(gate)
            {
                if((string?)args["action"]=="enable")
                {
                    if((string?)args["root"]!=root||(string?)args["sessionId"]!=session||args["enabled"]?.Type!=JTokenType.Boolean){message=PompLanguage.Pick("대화가 바뀌었어요.", "Conversation changed.");}
                    else{enabled=(bool)args["enabled"]!;message="";if(!enabled)cancel=active;}
                }
                else if((string?)args["action"]=="cancel"){if((string?)args["root"]==root&&(string?)args["sessionId"]==session){cancel=active;message=PompLanguage.Pick("훅 취소 요청됨", "Hook cancellation requested");}else message=PompLanguage.Pick("대화가 바뀌었어요.", "Conversation changed.");}
            }
            try{cancel?.Cancel();}catch(ObjectDisposedException){} Publish();
        }
        public void CancelCurrent()
        {
            CancellationTokenSource? cancel; lock(gate)cancel=active;
            try{cancel?.Cancel();}catch(ObjectDisposedException){} Publish();
        }
        private void OnEvent(object? sender,ProjectEvent value)
        {
            CancellationTokenSource cancellation; string expectedSession;
            // The host callback takes its state lock; never call it while holding the hook gate.
            var hostBusy = agentBusy();
            lock(gate)
            {
                if(disposed||!enabled||!string.Equals(root.TrimEnd('\\','/'),value.Root.TrimEnd('\\','/'),StringComparison.OrdinalIgnoreCase)||attempts>=20)return;
                if(hostBusy||!ui.IsReady||ui.PendingRequestCount>0||terminal.HasUnfinishedWork||Volatile.Read(ref running)!=0||DateTimeOffset.UtcNow-lastFinished<TimeSpan.FromSeconds(10)){skipped++;Publish();return;}
                Interlocked.Exchange(ref running,1); expectedSession=session; cancellation=CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token); active=cancellation;
            }
            Task.Run(()=>RunAsync(value,expectedSession,cancellation)).Observe();
        }
        private async Task RunAsync(ProjectEvent value,string expectedSession,CancellationTokenSource cancellation)
        {
            using var ownedCancellation=cancellation; var hadCandidates=false;
            var token=cancellation.Token;
            try
            {
                token.ThrowIfCancellationRequested(); lock(gate){if(!enabled||disposed||session!=expectedSession)return;}
                var workspace=await environment.GetWorkspaceRootAsync(token).ConfigureAwait(false);
                if(!string.Equals(workspace,value.Root,StringComparison.OrdinalIgnoreCase))return;
                var relative="";
                if(value.Name=="onFileSave") {var prefix=Path.GetFullPath(workspace).TrimEnd('\\','/')+Path.DirectorySeparatorChar; var path=Path.GetFullPath(value.Path); if(!Path.IsPathRooted(value.Path)||!path.StartsWith(prefix,StringComparison.OrdinalIgnoreCase))throw new InvalidOperationException(PompLanguage.Pick("저장된 파일이 이 작업 영역 밖에 있어요.", "Saved file is outside this workspace.")); CheckpointStore.CheckDirectPath(path); relative=path.Substring(prefix.Length);if(relative.Split('/','\\').Any(p=>new[]{".pomp",".agents",".git","bin","obj"}.Contains(p,StringComparer.OrdinalIgnoreCase)))return;}
                var diagnostics=new List<string>();var hooks=await Task.Run(()=>ProjectHook.Load(workspace,diagnostics,token),token).ConfigureAwait(false);
                lock(gate)message=string.Join("\n",diagnostics.Take(5));
                foreach(var hook in hooks.Where(h=>h.Matches(value,relative)).Take(3))
                {
                    if(agentBusy()||terminal.HasUnfinishedWork||ui.PendingRequestCount>0){lock(gate)skipped++;return;}
                    hadCandidates=true; token.ThrowIfCancellationRequested();lock(gate){if(!enabled||session!=expectedSession||attempts>=20)return;attempts++;}
                    var row=new JObject{["name"]=hook.Name,["event"]=value.Name,["path"]=value.Path.Length>1024?value.Path.Substring(0,1024)+"…":value.Path,["status"]="awaiting approval",["createdUtc"]=DateTimeOffset.UtcNow.ToString("O")};
                    lock(gate){history.Insert(0,row);TrimHistory();}Publish();
                    try
                    {
                        var command=hook.Command(value); HookExecutionPolicy.Check(workspace,hook.Script,command);
                        var tool=(IPreparedHostTool)terminal.RunTool();
                        using var prepared=await tool.PrepareAsync(new HostToolInvocation("hook-"+Guid.NewGuid().ToString("N"),"project-hook","run_terminal",new JObject{["command"]=command,["timeoutSeconds"]=hook.TimeoutSeconds}),token).ConfigureAwait(false);
                        if(!string.Equals(prepared.Permission.Target,workspace,StringComparison.OrdinalIgnoreCase))throw new InvalidOperationException(PompLanguage.Pick("훅 승인 전에 작업 영역이 바뀌었어요.", "Workspace changed before hook approval."));
                        prepared.Permission.Title=PompLanguage.Pick("프로젝트 훅 '"+hook.Name+"'을 실행할까요?", "Run project hook '"+hook.Name+"'?");
                        prepared.Permission.Message=PompLanguage.Pick("트리거: "+value.Name+"\n훅: "+hook.File+"\n"+prepared.Permission.Message, "Trigger: "+value.Name+"\nHook: "+hook.File+"\n"+prepared.Permission.Message);
                        var approval=await permissions.RequestAsync(prepared.Permission,token).ConfigureAwait(false);
                        if(!approval.IsAllowed){lock(gate)row["status"]="denied";continue;}
                        token.ThrowIfCancellationRequested();if(agentBusy()||terminal.HasUnfinishedWork)throw new InvalidOperationException(PompLanguage.Pick("훅 건너뜀 · 다른 작업 실행 중", "Hook skipped · Other work active"));
                        HookExecutionPolicy.Check(workspace,hook.Script,command);
                        var current=ProjectHook.Load(workspace,new List<string>(),token).FirstOrDefault(h=>h.File==hook.File);
                        if(current==null||current.Hash!=hook.Hash)throw new InvalidOperationException(PompLanguage.Pick("승인 후 훅이 바뀌었어요.", "Hook changed after approval."));
                        if(prepared is IDeferredLaunchCheck checkedCall) checkedCall.PrelaunchCheck=()=>{
                            token.ThrowIfCancellationRequested(); lock(gate)if(!enabled||session!=expectedSession)throw new InvalidOperationException(PompLanguage.Pick("훅 꺼짐 또는 대화 변경", "Hooks off or conversation changed"));
                            HookExecutionPolicy.Check(workspace,hook.Script,command);
                            var latest=ProjectHook.Load(workspace,new List<string>(),token).FirstOrDefault(h=>h.File==hook.File);
                            if(latest==null||latest.Hash!=hook.Hash)throw new InvalidOperationException(PompLanguage.Pick("대기 중 훅 변경", "Hook changed while queued"));
                        };
                        else throw new InvalidOperationException(PompLanguage.Pick("터미널 시작 전 재검증을 사용할 수 없어요.", "Terminal launch revalidation is unavailable."));
                        var result=await prepared.ExecuteAsync(token).ConfigureAwait(false);var jobId=(string?)result.Details?["jobId"];
                        if(result.IsError||string.IsNullOrEmpty(jobId))throw new InvalidOperationException(string.Join("\n",result.Content.Select(c=>c.Text)));
                        lock(gate){row["status"]="terminal job active";row["jobId"]=jobId;}Publish();
                        using var registration=token.Register(()=>terminal.Handle(new JObject{["action"]="cancel",["id"]=jobId}));
                        await terminal.WaitForJobAsync(jobId!,token).ConfigureAwait(false);
                        var job=(terminal.View()["jobs"] as JArray)?.FirstOrDefault(j=>(string?)j["id"]==jobId);
                        lock(gate)row["status"]=(string?)job?["state"]??"result unavailable";
                    }
                    catch(OperationCanceledException){lock(gate)row["status"]="cancellation requested";throw;}
                    catch(Exception ex){lock(gate){row["status"]="blocked or failed";row["error"]=ex.Message.Length>1000?ex.Message.Substring(0,1000):ex.Message;}}
                    finally{Publish();}
                }
            }
            catch(OperationCanceledException){}
            catch(Exception ex){lock(gate)message=PompLanguage.Pick("훅 실행 실패 · "+(ex.Message.Length>2000?ex.Message.Substring(0,2000):ex.Message), "Hook failed · "+(ex.Message.Length>2000?ex.Message.Substring(0,2000):ex.Message));}
            finally{lock(gate){if(ReferenceEquals(active,cancellation))active=null;if(hadCandidates)lastFinished=DateTimeOffset.UtcNow;Interlocked.Exchange(ref running,0);}Publish();}
        }
        public void Dispose(){lock(gate){if(disposed)return;disposed=true;enabled=false;}source.Occurred-=OnEvent;lifetime.Cancel();}
    }
}
