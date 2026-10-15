using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;
using Pomp.Adapters;

namespace Pomp.Tools
{
    /// <summary>
    /// <c>get_debug_state</c>: debugger mode, process, break reason and, in break mode, the call stack (top frame first)
    /// and optionally the locals. Never runs or steps the program.
    /// </summary>
    internal sealed class GetDebugStateTool : HostToolBase
    {
        internal const int DefaultMaxFrames = 10;
        internal const int MaxMaxFrames = 100;
        internal const int MaxLocalsListed = 100;

        private const int MaxValueLength = 200;

        public GetDebugStateTool(HostToolContext context)
            : base(context, CreateDefinition())
        {
        }

        private static HostToolDefinition CreateDefinition()
        {
            return new HostToolDefinition
            {
                Name = HostToolNames.GetDebugState,
                Label = "Get Debug State",
                Description =
                    "Report the Visual Studio debugger state: design (not debugging), running or break mode, the debugged process and break reason, " +
                    "and in break mode the call stack (top frame first, `#N Function file:line [module]`) and optionally the local variables (`name (type) = value`). " +
                    "Use it when the user is stopped at a breakpoint or exception, or asks what a variable holds. Read-only: it never runs or steps the program.",
                Parameters = new SchemaBuilder()
                    .Integer("maxFrames", "Maximum number of call stack frames.", 1, MaxMaxFrames, DefaultMaxFrames)
                    .Boolean("includeLocals", "Also return the local variables of the current frame (slower, can be long).", false)
                    .Build(),
                LoadMode = EssentialLoadMode,
            };
        }

        protected override async Task<HostToolResult> ExecuteCoreAsync(HostToolInvocation invocation, CancellationToken cancellationToken)
        {
            var args = new ToolArguments(invocation.Arguments, "maxFrames", "includeLocals");
            var maxFrames = args.OptionalInt("maxFrames", 1, MaxMaxFrames) ?? DefaultMaxFrames;
            var includeLocals = args.OptionalBool("includeLocals") ?? false;
            if (args.TryGetError(Name, out var invalid))
            {
                return invalid;
            }

            var state = await Adapters.Debugger.GetDebugStateAsync(maxFrames, includeLocals, cancellationToken).ConfigureAwait(false);
            if (state == null)
            {
                return Fail("the debugger adapter returned no state.");
            }

            var guard = await Context.TryGetPathGuardAsync(cancellationToken).ConfigureAwait(false);
            var display = HostToolContext.DisplayFunc(guard);

            var builder = new StringBuilder("Debugger: ");
            var process = string.IsNullOrWhiteSpace(state.ProcessName) ? null : state.ProcessName!.Trim();
            switch (state.Mode)
            {
                case DebugMode.Design:
                    builder.Append("not running (design mode).");
                    break;
                case DebugMode.Run:
                    builder.Append("running").Append(process != null ? " (" + process + ")" : string.Empty)
                        .Append(". The call stack and locals are only available in break mode.");
                    break;
                default:
                    builder.Append("paused in break mode").Append(process != null ? " (" + process + ")" : string.Empty);
                    if (!string.IsNullOrWhiteSpace(state.LastBreakReason))
                    {
                        builder.Append(", reason: ").Append(TextUtil.ToSingleLine(state.LastBreakReason, MaxValueLength));
                    }

                    builder.Append('.');
                    break;
            }

            var frames = (state.CallStack ?? new List<StackFrameInfo>()).Where(f => f != null).ToList();
            var locals = (state.Locals ?? new List<VariableInfo>()).Where(v => v != null).ToList();

            if (state.Mode == DebugMode.Break)
            {
                if (frames.Count == 0)
                {
                    builder.Append("\nCall stack: (empty)");
                }
                else
                {
                    var shown = frames.Take(maxFrames).ToList();
                    builder.Append("\nCall stack:");
                    for (var i = 0; i < shown.Count; i++)
                    {
                        builder.Append("\n#").Append(i).Append(' ').Append(FormatFrame(shown[i], display));
                    }

                    if (frames.Count > maxFrames || shown.Count == maxFrames)
                    {
                        builder.Append("\n[showing the top ").Append(shown.Count).Append(" frames; raise maxFrames for more]");
                    }
                }

                if (includeLocals)
                {
                    if (locals.Count == 0)
                    {
                        builder.Append("\nLocals: (none)");
                    }
                    else
                    {
                        builder.Append("\nLocals:");
                        foreach (var local in locals.Take(MaxLocalsListed))
                        {
                            builder.Append('\n').Append(FormatVariable(local));
                        }

                        if (locals.Count > MaxLocalsListed)
                        {
                            builder.Append("\n[").Append(locals.Count - MaxLocalsListed).Append(" more locals not shown]");
                        }
                    }
                }
            }

            var details = new JObject
            {
                ["mode"] = state.Mode.ToString().ToLowerInvariant(),
                ["process"] = process,
                ["frames"] = frames.Count,
                ["locals"] = locals.Count,
            };
            return HostToolResult.Text(builder.ToString(), details);
        }

        private static string FormatFrame(StackFrameInfo frame, Func<string, string> display)
        {
            var builder = new StringBuilder(string.IsNullOrWhiteSpace(frame.FunctionName) ? "(unknown function)" : TextUtil.ToSingleLine(frame.FunctionName, MaxValueLength));
            if (!string.IsNullOrWhiteSpace(frame.File))
            {
                builder.Append(' ').Append(display(frame.File!.Trim()));
                if (frame.Line.HasValue && frame.Line.Value > 0)
                {
                    builder.Append(':').Append(frame.Line.Value);
                }
            }

            if (!string.IsNullOrWhiteSpace(frame.Module))
            {
                builder.Append(" [").Append(frame.Module!.Trim()).Append(']');
            }

            return builder.ToString();
        }

        private static string FormatVariable(VariableInfo variable)
        {
            var builder = new StringBuilder(variable.Name);
            if (!string.IsNullOrWhiteSpace(variable.Type))
            {
                builder.Append(" (").Append(variable.Type!.Trim()).Append(')');
            }

            builder.Append(" = ").Append(variable.Value == null ? "(unavailable)" : TextUtil.ToSingleLine(variable.Value, MaxValueLength));
            return builder.ToString();
        }
    }
}
