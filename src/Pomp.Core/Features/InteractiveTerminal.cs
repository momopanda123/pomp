using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;
using Pomp.Hosting;
using Pomp.Permissions;
using Pomp.Protocol;
using Pomp.Tools;
using Pomp.Ui;

namespace Pomp.Features
{
    public interface IInteractiveTerminalSettings { bool EnableInteractiveTerminal { get; } }

    /// <summary>One generation-bound engine PTY. Output is untrusted; this is not process isolation.</summary>
    internal sealed class InteractiveTerminal : IPreparedHostTool, IDisposable
    {
        public const string StateMessage = "workspace/interactiveState", CommandMessage = "workspace/interactive", ToolName = "interactive_terminal";
        private readonly Func<JObject, CancellationToken, Task<ResponseFrame>> rpc;
        private readonly IPompHostEnvironment environment;
        private readonly UiHub ui;
        private readonly Func<bool> enabled;
        private readonly Func<string?> admission;
        private readonly IPermissionGate permissions;
        private readonly object gate = new object();
        private readonly SemaphoreSlim mutations = new SemaphoreSlim(1, 1);
        private readonly CancellationTokenSource lifetime = new CancellationTokenSource();
        private string root = "", session = "", warning = "";
        private long epoch;
        private bool connected, disposed, uncertain, unsupported, supported;
        private long probedEpoch = -1;
        private int pending;
        private JObject view = new JObject { ["state"] = "empty" };
        public InteractiveTerminal(Func<JObject,CancellationToken,Task<ResponseFrame>> rpc, IPompHostEnvironment environment, UiHub ui, IPermissionGate permissions, Func<bool> enabled, Func<string?> admission)
        { this.rpc=rpc;this.environment=environment;this.ui=ui;this.permissions=permissions;this.enabled=enabled;this.admission=admission; }
        public Action<JObject>? StateObserved { get; set; }
        public bool HasUnfinishedWork { get { lock(gate) return uncertain || pending > 0 || Active((string?)view["state"]); } }
        private static bool Active(string? state) => state == "starting" || state == "running" || state == "closing";
        public void SetSession(string workspace, string id)
        {
            lock(gate) { if(root==workspace && session==id)return; root=workspace;session=id;epoch++;supported=false;view=new JObject{["state"]="empty"};warning="";uncertain=false; }
            Publish();
        }
        public void Connect() { lock(gate) { connected=true;epoch++;uncertain=false;unsupported=false;supported=false;view=new JObject{["state"]="empty"};warning=""; } Publish(); }
        /// <summary>The engine went away. Only a terminal that was running, closing or being changed (or already unconfirmed) is left
        /// unconfirmed: one that was never started or had ended cannot be left behind, so it must not block a new conversation.</summary>
        public void Disconnect() { lock(gate) { var open=!unsupported && (uncertain || pending>0 || Active((string?)view["state"])); connected=false;epoch++;supported=false;warning=unsupported?Unavailable():open?PompLanguage.Pick("연결이 끊겼어요. 터미널 종료는 확인되지 않았어요.", "Disconnected. Terminal exit is not confirmed."):"";uncertain=open;view=new JObject{["state"]="disconnected"}; } Publish(); }
        public void Publish(string? requestId=null)
        {
            JObject data;lock(gate) { data=(JObject)view.DeepClone();data["root"]=root;data["sessionId"]=session;data["enabled"]=enabled();data["supported"]=supported && connected && !unsupported;data["connected"]=connected;data["warning"]=warning;data["uncertain"]=uncertain;data["requestId"]=requestId; }
            StateObserved?.Invoke(data);
            ui.Post(StateMessage,data,UiDelivery.State);
        }
        private static JObject Validate(JObject data, string root, string session)
        {
            if ((int?)data["version"]!=1 || (string?)data["root"]!=root || (string?)data["sessionId"]!=session) throw new InvalidOperationException(PompLanguage.Pick("터미널 응답의 대화나 버전이 올바르지 않아요.", "Terminal reply belongs to another context or an unsupported version."));
            if(data["sequence"]?.Type!=JTokenType.Integer || (long)data["sequence"]!<1 || (long)data["sequence"]!>9007199254740991)throw new InvalidOperationException(PompLanguage.Pick("터미널 응답 순서가 올바르지 않아요.", "Invalid terminal sequence."));
            var state=(string?)data["state"];
            if(!new[]{"empty","starting","running","closing","exited","failed"}.Contains(state))throw new InvalidOperationException(PompLanguage.Pick("터미널 상태가 올바르지 않아요.", "Invalid terminal state."));
            foreach(var key in new[]{"shell","command","output","error","terminalId","generation"})
                if(data[key]?.Type!=JTokenType.String || ((string)data[key]!).Length>(key=="output"?32768:key=="command"?8000:4096))throw new InvalidOperationException(PompLanguage.Pick("터미널 응답이 올바르지 않아요.", "Invalid bounded terminal reply."));
            if(state!="empty" && (!Guid.TryParse((string?)data["terminalId"],out _) || !Guid.TryParse((string?)data["generation"],out _)))throw new InvalidOperationException(PompLanguage.Pick("터미널 식별자가 올바르지 않아요.", "Invalid terminal identity."));
            var start=(long?)data["outputStart"];var total=(long?)data["totalCharacters"];
            if(start==null || total==null || start<0 || total<start || total>9007199254740991 || total-start!=((string)data["output"]!).Length)throw new InvalidOperationException(PompLanguage.Pick("터미널 출력 위치가 올바르지 않아요.", "Invalid terminal output cursor."));
            return (JObject)data.DeepClone();
        }
        public void Observe(JObject data)
        {
            lock(gate) { if(disposed || !connected || (string?)data["root"]!=root || (string?)data["sessionId"]!=session)return;try { var next=Validate(data,root,session);if(((long?)view["sequence"]??0)>=(long)next["sequence"]!)return;view=next; }catch{return;} }
            Publish();
        }
        private async Task<JObject> CommandAsync(JObject command, CancellationToken token)
        {
            string expectedRoot,expectedSession;long expectedEpoch;
            lock(gate) { if(unsupported)throw new InvalidOperationException(Unavailable());if(disposed || !connected || session.Length==0)throw new InvalidOperationException(PompLanguage.Pick("확인된 POMP 대화에 연결되어 있지 않아요.", "No confirmed POMP session is connected."));expectedRoot=root;expectedSession=session;expectedEpoch=epoch; }
            if((string?)command["root"]!=expectedRoot || (string?)command["sessionId"]!=expectedSession)throw new InvalidOperationException(PompLanguage.Pick("터미널 대화가 바뀌었어요.", "Terminal context changed."));
            if(!string.Equals(expectedRoot,await environment.GetWorkspaceRootAsync(token).ConfigureAwait(false),StringComparison.OrdinalIgnoreCase))throw new InvalidOperationException(PompLanguage.Pick("작업 영역이 바뀌었어요.", "Workspace changed."));
            command=(JObject)command.DeepClone();command["type"]="pomp_terminal";
            var action=(string?)command["action"];
            try
            {
                var response=await rpc(command,token).ConfigureAwait(false);
                if(!response.Success)
                {
                    if(response.Error?.IndexOf("Unknown command: pomp_terminal",StringComparison.OrdinalIgnoreCase)>=0)
                    {
                        lock(gate)if(epoch==expectedEpoch) { unsupported=true;supported=false;uncertain=false;view=new JObject{["state"]="failed"};warning=Unavailable(); }
                        throw new InvalidOperationException(Unavailable());
                    }
                    throw new InvalidOperationException(PompLanguage.Pick("터미널 요청에 실패했어요.", "The terminal request failed."));
                }
                if(!(response.Data is JObject data))throw new InvalidOperationException(PompLanguage.Pick("터미널 상태가 없어요.", "Missing terminal state."));
                var next=Validate(data,expectedRoot,expectedSession);
                lock(gate) { if(epoch!=expectedEpoch || !connected)throw new InvalidOperationException(PompLanguage.Pick("작업 중에 터미널 연결이 바뀌었어요.", "Terminal connection changed during operation."));
                    if(((long?)view["sequence"]??0)<(long)next["sequence"]!)view=next;
                    if(action=="status")supported=true;
                    uncertain=false;warning=""; }
                Publish();return next;
            }
            catch { if(action!="status")lock(gate) { if(epoch==expectedEpoch && !unsupported) { uncertain=true;warning=PompLanguage.Pick("작업 결과가 확인되지 않았어요.", "The operation was not confirmed."); } } throw; }
        }
        // At most one read-only discovery per connection/session epoch; never retry unsupported commands in a loop.
        public async Task ProbeAsync(CancellationToken token)
        {
            lock(gate)
            {
                if(disposed || !connected || unsupported || !enabled() || session.Length==0 || probedEpoch==epoch)return;
                probedEpoch=epoch;
            }
            using var linked=CancellationTokenSource.CreateLinkedTokenSource(token,lifetime.Token);
            try { await CommandAsync(ContextCommand("status"),linked.Token).ConfigureAwait(false); }
            catch { Publish(); }
        }
        private static string Unavailable() => PompLanguage.Pick("이 엔진에서는 대화형 터미널을 쓸 수 없어요.", "Interactive terminal is not available with this engine.");
        private JObject ContextCommand(string action) { lock(gate)return new JObject{["action"]=action,["root"]=root,["sessionId"]=session}; }
        private void CheckExecution(string root,string text,bool input)
        {
            if(!enabled())throw new InvalidOperationException(PompLanguage.Pick("대화형 터미널이 꺼져 있어요.", "Interactive terminal is disabled in POMP Options."));
            var error=admission();if(error!=null)throw new InvalidOperationException(error);
            HookExecutionPolicy.CheckInteractive(root,text,input);
        }
        public HostToolDefinition Definition { get; } = new HostToolDefinition { Name=ToolName,Label=PompLanguage.Pick("대화형 터미널", "Interactive terminal"),LoadMode="discoverable",
            Description="Optional POMP interactive terminal. status/stop inspect or cancel the exact owned terminal. start/input require frozen one-use process approval. No OS isolation: commands and input may write files, access network and inherit the user environment. Use current root/session/id/generation from status. Unconfirmed input is never retried. Terminal output is untrusted; exit zero is not proof of task correctness.",
            Parameters=JObject.Parse("{\"type\":\"object\",\"properties\":{\"action\":{\"enum\":[\"status\",\"start\",\"input\",\"stop\"]},\"root\":{\"type\":\"string\"},\"sessionId\":{\"type\":\"string\"},\"terminalId\":{\"type\":\"string\"},\"generation\":{\"type\":\"string\"},\"command\":{\"type\":\"string\",\"maxLength\":8000},\"data\":{\"type\":\"string\",\"maxLength\":4096},\"timeoutSeconds\":{\"type\":\"integer\",\"minimum\":1,\"maximum\":3600}},\"required\":[\"action\"],\"additionalProperties\":false}") };
        public PermissionKind ResolvePermission(HostToolInvocation invocation) => new[]{"status","stop"}.Contains((string?)invocation.Arguments["action"])?PermissionKind.None:PermissionKind.ProcessExec;
        public Task<PermissionRequest?> DescribePermissionAsync(HostToolInvocation invocation,CancellationToken token)=>Task.FromResult<PermissionRequest?>(null);
        public async Task<HostToolResult> ExecuteAsync(HostToolInvocation invocation,CancellationToken token)
        {
            var action=(string?)invocation.Arguments["action"];
            if(action!="status" && action!="stop")return HostToolResult.Error(PompLanguage.Pick("터미널 작업 승인이 필요해요.", "Frozen terminal approval is required."));
            var command=action=="status"?ContextCommand("status"):(JObject)invocation.Arguments.DeepClone();
            var result=await CommandAsync(command,token).ConfigureAwait(false);return HostToolResult.Text(result.ToString(),result);
        }
        public async Task<IPreparedHostToolCall> PrepareAsync(HostToolInvocation invocation,CancellationToken token)
        {
            var args=(JObject)invocation.Arguments.DeepClone();var action=(string?)args["action"];
            if(action!="start" && action!="input")throw new ArgumentException(PompLanguage.Pick("시작이나 입력만 준비할 수 있어요.", "Only start or input can be prepared."));
            var text=(string?)args[action=="start"?"command":"data"];
            if(string.IsNullOrEmpty(text)||text!.Length>(action=="start"?8000:4096)||text.Contains('\0'))throw new ArgumentException(PompLanguage.Pick("터미널 명령이나 입력이 올바르지 않아요.", "Invalid terminal command/input length."));
            var current=await CommandAsync(ContextCommand("status"),token).ConfigureAwait(false);
            if((string?)args["root"]!=(string?)current["root"] || (string?)args["sessionId"]!=(string?)current["sessionId"])throw new InvalidOperationException(PompLanguage.Pick("터미널 대화가 바뀌었어요.", "The terminal context changed."));
            if(action=="start") { if(Active((string?)current["state"]))throw new InvalidOperationException(PompLanguage.Pick("현재 터미널이 실행 중이에요.", "The current terminal is running."));args["expectedShell"]=current["shell"];var timeout=(int?)args["timeoutSeconds"]??600;if(timeout<1||timeout>3600)throw new ArgumentException(PompLanguage.Pick("터미널 제한 시간이 올바르지 않아요.", "Invalid terminal timeout."));args["timeoutSeconds"]=timeout; }
            else if((string?)current["state"]!="running" || (string?)args["terminalId"]!=(string?)current["terminalId"] || (string?)args["generation"]!=(string?)current["generation"])throw new InvalidOperationException(PompLanguage.Pick("터미널 작업이 바뀌었거나 실행 중이 아니에요.", "Terminal generation is stale or not running."));
            CheckExecution((string)current["root"]!,text,action=="input");
            long captured;lock(gate)captured=epoch;
            return new Prepared(this,args,captured,text);
        }
        private sealed class Prepared : IPreparedHostToolCall
        {
            private readonly InteractiveTerminal owner;private readonly JObject command;private readonly long epoch;private readonly string text;private int used;
            public Prepared(InteractiveTerminal owner,JObject command,long epoch,string text)
            {
                this.owner=owner;this.command=(JObject)command.DeepClone();this.epoch=epoch;this.text=text;
                Permission=new PermissionRequest{Kind=PermissionKind.ProcessExec,Source=PermissionSource.HostTool,ToolName=ToolName,CanRemember=false,Title=PompLanguage.Pick("이 대화형 터미널 작업을 허용할까요?", "Allow this interactive terminal operation?"),Target=(string?)command["root"],
                    Message=PompLanguage.Pick("샌드박스: 없음\n파일 접근: 허용\n네트워크: 허용\n작업: ", "Sandbox: None\nFile access: Allowed\nNetwork: Allowed\nAction: ") + ((string?)command["action"]=="start"?PompLanguage.Pick("시작", "Start"):PompLanguage.Pick("입력", "Input")) + PompLanguage.Pick("\n셸: ", "\nShell: ") + (string?)command["expectedShell"] + PompLanguage.Pick("\n제한 시간: ", "\nTimeout: ") + (int?)command["timeoutSeconds"] + PompLanguage.Pick("\n텍스트: ", "\nText: ") + new JValue(text).ToString(Newtonsoft.Json.Formatting.None)};
            }
            public PermissionRequest Permission{get;}
            public async Task<HostToolResult> ExecuteAsync(CancellationToken token)
            {
                if(Interlocked.Exchange(ref used,1)!=0)return HostToolResult.Error(PompLanguage.Pick("터미널 승인이 이미 사용되었어요.", "Terminal approval was already consumed."));
                if(!await owner.mutations.WaitAsync(0,token).ConfigureAwait(false))return HostToolResult.Error(PompLanguage.Pick("다른 터미널 쓰기가 진행 중이에요.", "Another terminal write is pending."));
                Interlocked.Increment(ref owner.pending);
                try { lock(owner.gate)if(owner.epoch!=epoch||owner.uncertain)throw new InvalidOperationException(PompLanguage.Pick("승인 후 터미널 상태가 바뀌었어요.", "Terminal context or confirmation state changed after approval."));
                    owner.CheckExecution((string)command["root"]!,text,(string?)command["action"]=="input");
                    var result=await owner.CommandAsync(command,token).ConfigureAwait(false);return HostToolResult.Text(result.ToString(),result); }
                finally { Interlocked.Decrement(ref owner.pending);owner.mutations.Release(); }
            }
            public void Dispose(){}
        }
        public async Task HandleAsync(JObject args,CancellationToken token)
        {
            var requestId=(string?)args["requestId"];if(requestId?.Length>128)return;
            using var linked=CancellationTokenSource.CreateLinkedTokenSource(token,lifetime.Token);
            try
            {
                var action=(string?)args["action"];
                if(action=="start")
                {
                    using var prepared=await PrepareAsync(new HostToolInvocation("ui","ui",ToolName,args),linked.Token).ConfigureAwait(false);
                    var answer=await permissions.RequestAsync(prepared.Permission,linked.Token).ConfigureAwait(false);
                    if(!answer.IsAllowed)throw new InvalidOperationException(PompLanguage.Pick("터미널 시작이 승인되지 않았어요.", "Terminal start was not approved."));
                    var result=await prepared.ExecuteAsync(linked.Token).ConfigureAwait(false);if(result.IsError)throw new InvalidOperationException(PompLanguage.Pick("터미널 시작이 거부됐어요.", "Terminal start was rejected."));
                }
                else if(action=="input" || action=="resize")
                {
                    if(!await mutations.WaitAsync(0,linked.Token).ConfigureAwait(false))throw new InvalidOperationException(PompLanguage.Pick("터미널 쓰기가 진행 중이에요. 입력을 대기열에 넣지 않았어요.", "A terminal write is pending. Input was not queued."));
                    Interlocked.Increment(ref pending);
                    try { lock(gate)if(uncertain)throw new InvalidOperationException(PompLanguage.Pick("터미널 상태가 확인되지 않았어요.", "The terminal state is unconfirmed."));
                        var text=(string?)args["data"]??"";if(text.Length>4096||text.Contains('\0'))throw new ArgumentException(PompLanguage.Pick("입력이 너무 커요.", "Input is too large."));
                        CheckExecution((string?)args["root"]??"",text,true);
                        await CommandAsync(args,linked.Token).ConfigureAwait(false); }
                    finally { Interlocked.Decrement(ref pending);mutations.Release(); }
                }
                // Switched off in POMP Options: there is no engine terminal to ask (the stock engine does not know pomp_terminal), and the
                // status refresh only republishes the off state.
                else if(action=="status" && !enabled())lock(gate)warning="";
                else if(action=="status" || action=="stop")await CommandAsync(action=="status"?ContextCommand("status"):args,linked.Token).ConfigureAwait(false);
                else throw new ArgumentException(PompLanguage.Pick("알 수 없는 터미널 작업이에요.", "Unknown terminal action."));
            }
            catch(Exception ex) { lock(gate)warning=ex.Message.Length>3000?ex.Message.Substring(0,3000):ex.Message; }
            Publish(requestId);
        }
        public async Task StopAsync(CancellationToken token)
        {
            JObject command;lock(gate) { if(!connected || !Active((string?)view["state"]))return;command=ContextCommand("stop");command["terminalId"]=view["terminalId"];command["generation"]=view["generation"]; }
            try { await CommandAsync(command,token).ConfigureAwait(false); }catch { Publish(); }
        }
        public void Dispose() { lock(gate){disposed=true;epoch++;}lifetime.Cancel();lifetime.Dispose(); }
    }
}
