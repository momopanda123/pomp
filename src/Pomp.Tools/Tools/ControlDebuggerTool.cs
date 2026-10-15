using System;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;
using Pomp.Adapters;
using Pomp.Hosting;
using Pomp.Permissions;

namespace Pomp.Tools
{
    /// <summary><c>control_debugger</c>: approved, non-waiting debugger execution control.</summary>
    internal sealed class ControlDebuggerTool : HostToolBase
    {
        internal static readonly string[] Actions = { "start", "continue", "break", "stop", "step_into", "step_over", "step_out" };

        public ControlDebuggerTool(HostToolContext context)
            : base(context, CreateDefinition())
        {
        }

        private static HostToolDefinition CreateDefinition()
        {
            return new HostToolDefinition
            {
                Name = HostToolNames.ControlDebugger,
                Label = "Control Debugger",
                Description = "Control the Visual Studio debugger: start debugging, continue, break, stop or step. Requires user approval because start, continue and steps execute program code. Start requires design mode, continue and steps require break mode, break requires run mode, and stop requires a debugging session. Returns immediately with the observed mode and top stack frame if paused; the mode may still be changing. Use get_debug_state afterwards to inspect it.",
                Parameters = new SchemaBuilder()
                    .Enum("action", "start, continue, break, stop, step_into, step_over or step_out.", Actions, required: true)
                    .Build(),
                LoadMode = EssentialLoadMode,
            };
        }

        public override PermissionKind ResolvePermission(HostToolInvocation invocation) => PermissionKind.DebuggerControl;

        public override Task<PermissionRequest?> DescribePermissionAsync(HostToolInvocation invocation, CancellationToken cancellationToken)
        {
            var args = new ToolArguments(invocation?.Arguments, "action");
            var action = args.RequiredEnum("action", Actions);
            if (args.HasProblems)
            {
                return Task.FromResult<PermissionRequest?>(NewPermissionRequest(
                    PermissionKind.DebuggerControl,
                    PompLanguage.Pick("Visual Studio 디버거를 제어할까요?", "Control the Visual Studio debugger?"),
                    PompLanguage.Pick("시작, 계속, 단계 실행은 프로그램 코드를 실행해요.", "Start, continue and step actions run program code."),
                    null));
            }

            return Task.FromResult<PermissionRequest?>(NewPermissionRequest(
                PermissionKind.DebuggerControl, Title(action!), Description(action!), PompLanguage.Pick("Visual Studio 디버거", "Visual Studio debugger")));
        }

        protected override async Task<HostToolResult> ExecuteCoreAsync(HostToolInvocation invocation, CancellationToken cancellationToken)
        {
            var args = new ToolArguments(invocation.Arguments, "action");
            var action = args.RequiredEnum("action", Actions);
            if (args.TryGetError(Name, out var invalid))
            {
                return invalid;
            }

            var state = await Adapters.Debugger.ControlAsync(ToAction(action!), cancellationToken).ConfigureAwait(false);
            if (state == null)
            {
                return Fail("the debugger adapter returned no state; the action may have started. Inspect get_debug_state before retrying.");
            }

            var mode = state.Mode == DebugMode.Design ? "design (not debugging)" : state.Mode == DebugMode.Run ? "run (running)" : "break (paused)";
            var builder = new StringBuilder("Debugger action '").Append(action).Append("' requested. Observed mode: ").Append(mode).Append('.');
            if (!string.IsNullOrWhiteSpace(state.ProcessName))
            {
                builder.Append("\nProcess: ").Append(IdeWriteToolText.ShortLine(state.ProcessName, 300));
            }

            if (state.Mode == DebugMode.Break)
            {
                var frame = state.CallStack?.FirstOrDefault(f => f != null);
                if (frame == null)
                {
                    builder.Append("\nCurrent location: unavailable.");
                }
                else
                {
                    builder.Append("\nCurrent location: ").Append(string.IsNullOrWhiteSpace(frame.FunctionName) ? "(unknown function)" : IdeWriteToolText.ShortLine(frame.FunctionName, 300));
                    if (!string.IsNullOrWhiteSpace(frame.File))
                    {
                        var guard = await Context.TryGetPathGuardAsync(cancellationToken).ConfigureAwait(false);
                        builder.Append(' ').Append(IdeWriteToolText.ShortLine(HostToolContext.DisplayFunc(guard)(frame.File!), 4096));
                        if (frame.Line.HasValue && frame.Line.Value > 0)
                        {
                            builder.Append(':').Append(frame.Line.Value.ToString(CultureInfo.InvariantCulture));
                        }
                    }
                }
            }

            builder.Append("\nNo wait for the debugger to settle was performed; the mode may still be changing. Use get_debug_state to inspect it again.");
            return HostToolResult.Text(builder.ToString(), new JObject { ["action"] = action, ["mode"] = state.Mode.ToString().ToLowerInvariant() });
        }

        private static DebugAction ToAction(string action)
        {
            switch (action)
            {
                case "start": return DebugAction.Start;
                case "continue": return DebugAction.Continue;
                case "break": return DebugAction.Break;
                case "stop": return DebugAction.Stop;
                case "step_into": return DebugAction.StepInto;
                case "step_over": return DebugAction.StepOver;
                default: return DebugAction.StepOut;
            }
        }

        private static string Title(string action)
        {
            switch (action)
            {
                case "start": return PompLanguage.Pick("시작 프로젝트 디버깅을 시작할까요?", "Start debugging the startup project?");
                case "continue": return PompLanguage.Pick("프로그램 실행을 계속할까요?", "Continue program execution?");
                case "break": return PompLanguage.Pick("프로그램 실행을 일시 중지할까요?", "Pause program execution?");
                case "stop": return PompLanguage.Pick("디버깅 세션을 중지할까요?", "Stop the debugging session?");
                case "step_into": return PompLanguage.Pick("다음 문장을 한 단계씩 실행할까요?", "Step into the next statement?");
                case "step_over": return PompLanguage.Pick("다음 문장을 프로시저 단위로 실행할까요?", "Step over the next statement?");
                default: return PompLanguage.Pick("현재 함수에서 나갈까요?", "Step out of the current function?");
            }
        }

        private static string? Description(string action)
        {
            switch (action)
            {
                case "start":
                    return PompLanguage.Pick("프로그램 코드와 빌드 대상이 실행되며 파일을 쓸 수 있어요.", "Program code and build targets run and can write files.");
                case "break":
                    return null;
                case "stop":
                    return PompLanguage.Pick("디버거가 시작한 프로세스가 종료돼요.", "Processes started by the debugger are terminated.");
                default:
                    return PompLanguage.Pick("프로그램 코드가 실행되며 부작용이 생길 수 있어요.", "Program code runs and may have side effects.");
            }
        }
    }
}
