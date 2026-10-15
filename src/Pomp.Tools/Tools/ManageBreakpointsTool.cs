using System;
using System.Globalization;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;
using Pomp.Adapters;
using Pomp.Hosting;
using Pomp.Permissions;

namespace Pomp.Tools
{
    /// <summary><c>manage_breakpoints</c>: approved changes to line and function breakpoints, including conditions and tracepoints.</summary>
    internal sealed class ManageBreakpointsTool : HostToolBase
    {
        internal static readonly string[] Actions = { "add", "remove", "enable", "disable" };
        internal static readonly string[] HitCountModes = { "equal", "greaterOrEqual", "multiple" };

        private const int MaxArgumentLength = 4096;

        public ManageBreakpointsTool(HostToolContext context)
            : base(context, CreateDefinition())
        {
        }

        private static HostToolDefinition CreateDefinition()
        {
            return new HostToolDefinition
            {
                Name = HostToolNames.ManageBreakpoints,
                Label = "Manage Breakpoints",
                Description = "Add, remove, enable or disable Visual Studio breakpoints. Add accepts either file+line or function, with optional condition, hit count and tracepoint log message. File paths must be inside the workspace. Every change requires user approval. Returns the binding state when known; a pending or unbound breakpoint may not hit until its code is loaded.",
                Parameters = new SchemaBuilder()
                    .Enum("action", "What to do: add, remove, enable or disable.", Actions, required: true)
                    .String("file", "Workspace-relative or absolute source file of a line breakpoint.")
                    .Integer("line", "1-based line of a line breakpoint.", 1)
                    .String("function", "Function name of a function breakpoint (action add only), e.g. MyNamespace.MyClass.Run.")
                    .String("condition", "Condition expression in the language of the code; the breakpoint only breaks when it is true (add only).")
                    .Integer("hitCount", "Hit count that triggers the breakpoint (add only).", 1)
                    .Enum("hitCountMode", "How hitCount is compared: equal, greaterOrEqual or multiple (default equal).", HitCountModes, "equal")
                    .String("logMessage", "Tracepoint message written to the Output window on each hit instead of breaking (add only); {expression} placeholders are evaluated.")
                    .Build(),
                LoadMode = EssentialLoadMode,
            };
        }

        public override PermissionKind ResolvePermission(HostToolInvocation invocation) => PermissionKind.BreakpointChange;

        public override async Task<PermissionRequest?> DescribePermissionAsync(HostToolInvocation invocation, CancellationToken cancellationToken)
        {
            var parsed = Parse(invocation?.Arguments, out var args);
            if (args.HasProblems)
            {
                return GenericPermission();
            }

            // Invalid/missing arguments above never query the environment. Approval descriptions never touch an adapter,
            // and an unavailable workspace or cancellation must not throw out of this best-effort description path.
            try
            {
                var location = "fn " + IdeWriteToolText.Exact(parsed.Function ?? string.Empty);
                if (parsed.File != null)
                {
                    var guard = await Context.TryGetPathGuardAsync(cancellationToken).ConfigureAwait(false);
                    if (guard == null || !guard.TryResolve(parsed.File, out var fullPath, out _))
                    {
                        return GenericPermission();
                    }

                    location = IdeWriteToolText.Exact(guard.ToDisplayPath(fullPath)) + ":" + parsed.Line!.Value.ToString(CultureInfo.InvariantCulture);
                }

                var message = parsed.Action == "add" ? DescribeOptions(parsed) : null;
                return NewPermissionRequest(PermissionKind.BreakpointChange, Title(parsed.Action!, parsed.LogMessage != null), message, location);
            }
            catch (Exception)
            {
                return GenericPermission();
            }
        }

        protected override async Task<HostToolResult> ExecuteCoreAsync(HostToolInvocation invocation, CancellationToken cancellationToken)
        {
            var parsed = Parse(invocation.Arguments, out var args);
            if (args.TryGetError(Name, out var invalid))
            {
                return invalid;
            }

            PathGuard? guard = null;
            string? fullPath = null;
            var location = "fn " + IdeWriteToolText.Exact(parsed.Function ?? string.Empty);
            if (parsed.File != null)
            {
                guard = await Context.GetPathGuardAsync(cancellationToken).ConfigureAwait(false);
                if (!guard.TryResolve(parsed.File, out fullPath, out var pathError))
                {
                    return Fail(IdeWriteToolText.Exact(pathError));
                }

                location = IdeWriteToolText.Exact(guard.ToDisplayPath(fullPath)) + ":" + parsed.Line!.Value.ToString(CultureInfo.InvariantCulture);
            }

            if (parsed.Action == "add")
            {
                var spec = new BreakpointSpec
                {
                    File = fullPath,
                    Line = parsed.Line,
                    FunctionName = parsed.Function,
                    Condition = parsed.Condition,
                    HitCount = parsed.HitCount,
                    HitCountMode = ToHitCountMode(parsed.HitCountMode),
                    LogMessage = parsed.LogMessage,
                    BreakWhenLogged = false,
                };
                var breakpoint = await Adapters.Debugger.AddBreakpointAsync(spec, cancellationToken).ConfigureAwait(false);
                if (breakpoint == null)
                {
                    return Fail("the debugger adapter returned no breakpoint details; the breakpoint may have been created. Check list_breakpoints before retrying.");
                }

                if (guard == null && !string.IsNullOrWhiteSpace(breakpoint.File))
                {
                    guard = await Context.TryGetPathGuardAsync(cancellationToken).ConfigureAwait(false);
                }

                var display = HostToolContext.DisplayFunc(guard);
                var builder = new StringBuilder("Added ").Append(parsed.LogMessage == null ? "breakpoint: " : "tracepoint: ")
                    .Append(IdeWriteToolText.ShortLine(ListBreakpointsTool.FormatBreakpoint(breakpoint, display), MaxArgumentLength));
                if (!string.IsNullOrWhiteSpace(breakpoint.BindingState))
                {
                    builder.Append("\nBinding state: ").Append(IdeWriteToolText.ShortLine(breakpoint.BindingState, 128));
                }

                if (parsed.LogMessage != null)
                {
                    builder.Append("\nLog message: \"").Append(IdeWriteToolText.Exact(parsed.LogMessage)).Append("\"; continue without breaking.");
                }

                return HostToolResult.Text(builder.ToString(), new JObject
                {
                    ["action"] = parsed.Action,
                    ["changed"] = true,
                    ["location"] = location,
                    ["bindingState"] = breakpoint.BindingState == null ? null : IdeWriteToolText.ShortLine(breakpoint.BindingState, 128),
                });
            }

            var changed = parsed.Action == "remove"
                ? await Adapters.Debugger.RemoveBreakpointAsync(fullPath!, parsed.Line!.Value, cancellationToken).ConfigureAwait(false)
                : await Adapters.Debugger.SetBreakpointEnabledAsync(fullPath!, parsed.Line!.Value, parsed.Action == "enable", cancellationToken).ConfigureAwait(false);
            var outcome = changed
                ? PastVerb(parsed.Action!) + " breakpoints at " + location + "."
                : "No breakpoint at " + location + "; nothing was changed.";
            return HostToolResult.Text(outcome, new JObject { ["action"] = parsed.Action, ["changed"] = changed, ["location"] = location });
        }

        private PermissionRequest GenericPermission() => NewPermissionRequest(
            PermissionKind.BreakpointChange,
            PompLanguage.Pick("Visual Studio 중단점을 변경할까요?", "Change the Visual Studio breakpoints?"),
            null,
            null);

        private static ParsedArguments Parse(JObject? arguments, out ToolArguments args)
        {
            args = new ToolArguments(arguments, "action", "file", "line", "function", "condition", "hitCount", "hitCountMode", "logMessage");
            var parsed = new ParsedArguments
            {
                Action = args.RequiredEnum("action", Actions),
                File = args.OptionalString("file"),
                Line = args.OptionalInt("line", 1, int.MaxValue),
                Function = args.OptionalString("function"),
                Condition = args.OptionalString("condition"),
                HitCount = args.OptionalInt("hitCount", 1, int.MaxValue),
                HitCountMode = args.OptionalEnum("hitCountMode", HitCountModes),
                // A tracepoint message is literal text: do not silently strip leading/trailing spaces.
                LogMessage = args.OptionalString("logMessage", trim: false),
            };
            if (string.IsNullOrWhiteSpace(parsed.LogMessage))
            {
                parsed.LogMessage = null;
            }

            CheckLength(args, "file", parsed.File);
            CheckLength(args, "function", parsed.Function);
            CheckLength(args, "condition", parsed.Condition);
            CheckLength(args, "logMessage", parsed.LogMessage);

            if (parsed.Action == "add")
            {
                if (parsed.File != null && parsed.Function != null)
                {
                    args.AddProblem("provide either 'file'+'line' or 'function', not both 'file' and 'function'");
                }

                if (parsed.File == null && parsed.Function == null)
                {
                    args.AddProblem("action 'add' requires either 'file'+'line' or 'function'");
                }

                if (parsed.File != null && !parsed.Line.HasValue)
                {
                    args.AddProblem("'line' is required with 'file'");
                }

                if (parsed.Line.HasValue && parsed.File == null)
                {
                    args.AddProblem("'line' requires 'file' and cannot be used with a function breakpoint");
                }
            }
            else if (parsed.Action != null)
            {
                if (parsed.File == null)
                {
                    args.AddProblem("'file' is required for action '" + parsed.Action + "'");
                }

                if (!parsed.Line.HasValue)
                {
                    args.AddProblem("'line' is required for action '" + parsed.Action + "'");
                }

                if (parsed.Function != null)
                {
                    args.AddProblem("'function' is supported only for action 'add'");
                }

                if (parsed.Condition != null)
                {
                    args.AddProblem("'condition' is supported only for action 'add'");
                }

                if (parsed.HitCount.HasValue)
                {
                    args.AddProblem("'hitCount' is supported only for action 'add'");
                }

                if (parsed.HitCountMode != null)
                {
                    args.AddProblem("'hitCountMode' is supported only for action 'add'");
                }

                if (parsed.LogMessage != null)
                {
                    args.AddProblem("'logMessage' is supported only for action 'add'");
                }
            }

            if (parsed.HitCountMode != null && !parsed.HitCount.HasValue)
            {
                args.AddProblem("'hitCountMode' requires 'hitCount'");
            }

            return parsed;
        }

        private static void CheckLength(ToolArguments args, string name, string? value)
        {
            if (value != null && value.Length > MaxArgumentLength)
            {
                args.AddProblem("'" + name + "' may contain at most " + MaxArgumentLength.ToString(CultureInfo.InvariantCulture) + " characters");
            }

            if (value != null && value.IndexOf('\0') >= 0)
            {
                args.AddProblem("'" + name + "' must not contain a NUL character");
            }
        }

        /// <summary>The approval details of a new breakpoint: its condition, hit count and tracepoint message as values; null when it has none.</summary>
        private static string? DescribeOptions(ParsedArguments parsed)
        {
            var message = new StringBuilder();
            if (parsed.Condition != null)
            {
                AppendLine(message, PompLanguage.Pick("조건: \"", "Condition: \"") + IdeWriteToolText.Exact(parsed.Condition) + "\"");
            }

            if (parsed.HitCount.HasValue)
            {
                var count = parsed.HitCount.Value.ToString(CultureInfo.InvariantCulture);
                var value = parsed.HitCountMode == "greaterOrEqual" ? PompLanguage.Pick(count + " 이상", "at least " + count)
                    : parsed.HitCountMode == "multiple" ? PompLanguage.Pick(count + "의 배수", "multiple of " + count)
                    : count;
                AppendLine(message, PompLanguage.Pick("적중 횟수: ", "Hit count: ") + value);
            }

            if (parsed.LogMessage != null)
            {
                AppendLine(message, PompLanguage.Pick("로그 메시지: \"", "Log message: \"") + IdeWriteToolText.Exact(parsed.LogMessage) + "\"");
            }

            if (parsed.Condition != null || parsed.LogMessage != null)
            {
                AppendLine(message, PompLanguage.Pick("디버거가 식을 프로그램 안에서 평가하며 코드가 실행될 수 있어요.", "The debugger evaluates expressions in the program and may execute code."));
            }

            return message.Length == 0 ? null : message.ToString();
        }

        private static void AppendLine(StringBuilder message, string line)
        {
            if (message.Length > 0)
            {
                message.Append('\n');
            }

            message.Append(line);
        }

        private static BreakpointHitCountMode ToHitCountMode(string? mode) => mode == "greaterOrEqual"
            ? BreakpointHitCountMode.GreaterOrEqual
            : mode == "multiple" ? BreakpointHitCountMode.Multiple : BreakpointHitCountMode.Equal;

        private static string Title(string action, bool tracepoint)
        {
            switch (action)
            {
                case "add": return tracepoint ? PompLanguage.Pick("추적점을 추가할까요?", "Add a tracepoint?") : PompLanguage.Pick("중단점을 추가할까요?", "Add a breakpoint?");
                case "remove": return PompLanguage.Pick("중단점을 제거할까요?", "Remove the breakpoint?");
                case "enable": return PompLanguage.Pick("중단점을 켤까요?", "Enable the breakpoint?");
                default: return PompLanguage.Pick("중단점을 끌까요?", "Disable the breakpoint?");
            }
        }

        private static string PastVerb(string action) => action == "remove" ? "Removed" : action == "enable" ? "Enabled" : "Disabled";

        private sealed class ParsedArguments
        {
            public string? Action { get; set; }
            public string? File { get; set; }
            public int? Line { get; set; }
            public string? Function { get; set; }
            public string? Condition { get; set; }
            public int? HitCount { get; set; }
            public string? HitCountMode { get; set; }
            public string? LogMessage { get; set; }
        }
    }

    /// <summary>Bounded, ASCII-safe diagnostics and exact literal display for the IDE write tools. Never alters adapter inputs.</summary>
    internal static class IdeWriteToolText
    {
        public static string Exact(string text)
        {
            var builder = new StringBuilder(text.Length);
            foreach (var c in text)
            {
                switch (c)
                {
                    case '\\': builder.Append("\\\\"); break;
                    case '"': builder.Append("\\\""); break;
                    case '\r': builder.Append("\\r"); break;
                    case '\n': builder.Append("\\n"); break;
                    case '\t': builder.Append("\\t"); break;
                    default:
                        if (c >= ' ' && c < 127)
                        {
                            builder.Append(c);
                        }
                        else
                        {
                            builder.Append("\\u").Append(((int)c).ToString("X4", CultureInfo.InvariantCulture));
                        }

                        break;
                }
            }

            return builder.ToString();
        }

        public static string ShortLine(string? text, int maxLength)
        {
            if (string.IsNullOrEmpty(text))
            {
                return string.Empty;
            }

            var cut = text!.Length > maxLength;
            var limited = cut ? text.Substring(0, maxLength) : text;
            return Exact(TextUtil.ToSingleLine(limited, maxLength)) + (cut ? " [truncated]" : string.Empty);
        }
    }
}
