using System;
using System.Collections.Generic;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using EnvDTE;
using Microsoft.VisualStudio.Shell;
using Pomp.Adapters;
using Pomp.Diagnostics;
using Task = System.Threading.Tasks.Task;

namespace Pomp.VisualStudio.Adapters
{
    /// <summary>Breakpoint changes and debugger execution/state through DTE. All automation and COM access is UI-thread-only.</summary>
    internal sealed class DebuggerAdapter : IDebuggerAdapter
    {
        private const int DefaultMaxFrames = 20;
        private const int MaxLocals = 200;
        private const int MaxValueChars = 500;

        private readonly VsAdapterContext context;

        public DebuggerAdapter(VsAdapterContext context)
        {
            this.context = context;
        }

        public async Task<IReadOnlyList<BreakpointInfo>> ListBreakpointsAsync(BreakpointFilter? filter, CancellationToken cancellationToken)
        {
            await context.Jtf.SwitchToMainThreadAsync(cancellationToken);
            LogUiThread("ListBreakpoints");

            var debugger = context.Dte.Debugger;
            var mode = ReadMode(debugger);
            var result = new List<BreakpointInfo>();

            Breakpoints breakpoints;
            try
            {
                breakpoints = debugger.Breakpoints;
            }
            catch (COMException ex)
            {
                context.LogUnexpected("Reading the debugger breakpoint collection", ex);
                return result;
            }

            foreach (Breakpoint breakpoint in breakpoints)
            {
                cancellationToken.ThrowIfCancellationRequested();

                var info = TryDescribe(breakpoint, mode);
                if (info == null)
                {
                    continue;
                }

                if (filter != null)
                {
                    if (filter.EnabledOnly && !info.Enabled)
                    {
                        continue;
                    }

                    if (!string.IsNullOrEmpty(filter.File) && !VsAdapterContext.SamePath(filter.File, info.File))
                    {
                        continue;
                    }
                }

                result.Add(info);
            }

            return result;
        }

        public async Task<BreakpointInfo> AddBreakpointAsync(BreakpointSpec spec, CancellationToken cancellationToken)
        {
            if (spec == null)
            {
                throw new ArgumentNullException(nameof(spec));
            }

            // Freeze the caller's DTO before awaiting; no COM or environment access is needed to validate it.
            var file = string.IsNullOrWhiteSpace(spec.File) ? null : VsAdapterContext.NormalizePath(spec.File);
            var function = string.IsNullOrWhiteSpace(spec.FunctionName) ? null : spec.FunctionName!.Trim();
            var line = spec.Line;
            var condition = string.IsNullOrWhiteSpace(spec.Condition) ? null : spec.Condition;
            var conditionWhenChanged = spec.ConditionWhenChanged;
            var hitCount = spec.HitCount;
            var hitCountMode = spec.HitCountMode;
            var logMessage = string.IsNullOrWhiteSpace(spec.LogMessage) ? null : spec.LogMessage;
            var breakWhenLogged = spec.BreakWhenLogged;
            if (!string.IsNullOrWhiteSpace(spec.File) && (file == null || !VsAdapterContext.IsFilePath(spec.File)))
            {
                throw new ArgumentException("A line breakpoint needs a valid absolute source file path.", nameof(spec));
            }

            if ((file == null) == (function == null) || (file != null && (!line.HasValue || line.Value < 1)) || (function != null && line.HasValue))
            {
                throw new ArgumentException("Provide either an absolute File and a Line >= 1, or FunctionName without File or Line.", nameof(spec));
            }

            if (conditionWhenChanged && condition == null)
            {
                throw new ArgumentException("ConditionWhenChanged requires a nonempty condition expression.", nameof(spec));
            }

            if ((hitCount.HasValue && hitCount.Value < 1) || !Enum.IsDefined(typeof(BreakpointHitCountMode), hitCountMode))
            {
                throw new ArgumentException("HitCount must be >= 1 and HitCountMode must be Equal, GreaterOrEqual or Multiple.", nameof(spec));
            }

            var conditionType = conditionWhenChanged
                ? dbgBreakpointConditionType.dbgBreakpointConditionTypeWhenChanged
                : dbgBreakpointConditionType.dbgBreakpointConditionTypeWhenTrue;
            var hitCountType = hitCount.HasValue ? ToHitCountType(hitCountMode) : dbgHitCountType.dbgHitCountTypeNone;

            await context.Jtf.SwitchToMainThreadAsync(cancellationToken);
            LogUiThread("AddBreakpoint");

            var debugger = context.Dte.Debugger;
            var breakpoints = debugger.Breakpoints;
            if (file != null && FindLineBreakpoints(breakpoints, file, line!.Value, cancellationToken).Count > 0)
            {
                throw new InvalidOperationException("A breakpoint already exists at '" + file + "':" + line.Value.ToString(CultureInfo.InvariantCulture) + ". Use enable/disable/remove instead of adding a duplicate.");
            }

            cancellationToken.ThrowIfCancellationRequested();
            var added = breakpoints.Add(
                Function: function ?? string.Empty,
                File: file ?? string.Empty,
                Line: line ?? 1,
                Column: 1,
                Condition: condition ?? string.Empty,
                ConditionType: conditionType,
                Language: string.Empty,
                Data: string.Empty,
                DataCount: 1,
                Address: string.Empty,
                HitCount: hitCount ?? 0,
                HitCountType: hitCountType);
            if (added == null)
            {
                throw new InvalidOperationException("Visual Studio returned no created-breakpoint collection; creation cannot be confirmed. Inspect list_breakpoints before retrying.");
            }

            // A function expression can yield more than one user breakpoint. Apply the action to every newly created parent.
            // EnvDTE80.Breakpoint2.Message and BreakWhenHit exist in both SDK 16.10 and SDK 17.0 interop assemblies.
            var created = new List<Breakpoint>();
            try
            {
                foreach (Breakpoint breakpoint in added)
                {
                    var parent = UserBreakpoint(breakpoint);
                    if (!created.Contains(parent))
                    {
                        created.Add(parent);
                    }
                }

                if (created.Count == 0)
                {
                    throw new InvalidOperationException("Visual Studio returned no usable user breakpoint.");
                }

                foreach (var breakpoint in created)
                {
                    if (logMessage != null)
                    {
                        if (!(breakpoint is EnvDTE80.Breakpoint2 tracepoint))
                        {
                            throw new NotSupportedException("The debugger does not support tracepoint messages (EnvDTE80.Breakpoint2).");
                        }

                        tracepoint.Message = logMessage;
                        tracepoint.BreakWhenHit = breakWhenLogged;
                    }
                }
            }
            catch (Exception ex)
            {
                // Do not silently leave an ordinary breaking breakpoint behind when configuring a tracepoint failed.
                if (!DeleteCreated(added))
                {
                    throw new InvalidOperationException("Breakpoint creation/configuration failed (HRESULT 0x" + ex.HResult.ToString("X8", CultureInfo.InvariantCulture) + "); cleanup also failed. Some new breakpoints may remain; inspect list_breakpoints before retrying.", ex);
                }

                throw;
            }

            var mode = ReadMode(debugger);
            var info = TryDescribe(created[0], mode) ?? new BreakpointInfo
            {
                File = file,
                Line = line,
                FunctionName = function,
                Condition = condition,
                HitCountTarget = hitCount,
                Enabled = true,
                BindingState = DescribeBinding(created[0], mode),
            };
            LogCreatedBreakpoint(created[0], info);
            return info;
        }

        public async Task<bool> RemoveBreakpointAsync(string file, int line, CancellationToken cancellationToken)
        {
            var fullPath = ValidateLineLocation(file, line);
            await context.Jtf.SwitchToMainThreadAsync(cancellationToken);
            LogUiThread("RemoveBreakpoint");

            // Snapshot first: deleting a breakpoint invalidates enumeration of the live DTE collection.
            var matches = FindLineBreakpoints(context.Dte.Debugger.Breakpoints, fullPath, line, cancellationToken);
            foreach (var breakpoint in matches)
            {
                cancellationToken.ThrowIfCancellationRequested();
                breakpoint.Delete();
            }

            return matches.Count > 0;
        }

        public async Task<bool> SetBreakpointEnabledAsync(string file, int line, bool enabled, CancellationToken cancellationToken)
        {
            var fullPath = ValidateLineLocation(file, line);
            await context.Jtf.SwitchToMainThreadAsync(cancellationToken);
            LogUiThread("SetBreakpointEnabled");

            var matches = FindLineBreakpoints(context.Dte.Debugger.Breakpoints, fullPath, line, cancellationToken);
            foreach (var breakpoint in matches)
            {
                cancellationToken.ThrowIfCancellationRequested();
                breakpoint.Enabled = enabled;
            }

            return matches.Count > 0;
        }

        public async Task<DebugState> ControlAsync(DebugAction action, CancellationToken cancellationToken)
        {
            if (!Enum.IsDefined(typeof(DebugAction), action))
            {
                throw new ArgumentOutOfRangeException(nameof(action), "Unknown debugger action.");
            }

            await context.Jtf.SwitchToMainThreadAsync(cancellationToken);
            LogUiThread("Control " + action);

            var debugger = context.Dte.Debugger;
            // Never assume design mode if CurrentMode could not be read: an unreadable mode must fail closed for writes.
            var mode = ReadMode(debugger, requireAvailable: true);
            var valid = action == DebugAction.Start ? mode == DebugMode.Design
                : action == DebugAction.Break ? mode == DebugMode.Run
                : action == DebugAction.Stop ? mode != DebugMode.Design
                : mode == DebugMode.Break;
            if (!valid)
            {
                throw new InvalidOperationException("Debugger action '" + action + "' is not valid in " + mode.ToString().ToLowerInvariant()
                    + " mode. Start requires design mode; break requires run mode; continue and steps require break mode; stop requires an active session.");
            }

            cancellationToken.ThrowIfCancellationRequested();
            switch (action)
            {
                case DebugAction.Start:
                case DebugAction.Continue:
                    debugger.Go(false);
                    break;
                case DebugAction.Break:
                    debugger.Break(false);
                    break;
                case DebugAction.Stop:
                    debugger.Stop(false);
                    break;
                case DebugAction.StepInto:
                    debugger.StepInto(false);
                    break;
                case DebugAction.StepOver:
                    debugger.StepOver(false);
                    break;
                case DebugAction.StepOut:
                    debugger.StepOut(false);
                    break;
            }

            // No waiting, sleeping or polling: the observed mode can still be the previous/transitional one.
            return ReadDebugState(debugger, 1, false, cancellationToken);
        }

        public async Task<DebugState> GetDebugStateAsync(int maxFrames, bool includeLocals, CancellationToken cancellationToken)
        {
            await context.Jtf.SwitchToMainThreadAsync(cancellationToken);
            LogUiThread("GetDebugState");
            return ReadDebugState(context.Dte.Debugger, maxFrames, includeLocals, cancellationToken);
        }

        private DebugState ReadDebugState(Debugger debugger, int maxFrames, bool includeLocals, CancellationToken cancellationToken)
        {
            ThreadHelper.ThrowIfNotOnUIThread();

            var state = new DebugState { Mode = ReadMode(debugger) };
            if (state.Mode == DebugMode.Design)
            {
                return state;
            }

            try
            {
                state.ProcessName = debugger.CurrentProcess?.Name;
            }
            catch (COMException)
            {
                // No current process (attaching/detaching).
            }

            state.LastBreakReason = ReadBreakReason(debugger);

            if (state.Mode != DebugMode.Break)
            {
                // Stack and locals exist only while the debuggee is stopped.
                return state;
            }

            var frameLimit = maxFrames > 0 ? maxFrames : DefaultMaxFrames;
            try
            {
                var frames = debugger.CurrentThread?.StackFrames;
                if (frames != null)
                {
                    var count = frames.Count;
                    for (var index = 1; index <= count && state.CallStack.Count < frameLimit; index++)
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        state.CallStack.Add(Describe(frames.Item(index)));
                    }
                }

                if (includeLocals)
                {
                    AddLocals(debugger.CurrentStackFrame, state.Locals, cancellationToken);
                }
            }
            catch (COMException ex)
            {
                // The debuggee resumed or the thread is not inspectable; return what we have and log the partial result.
                context.LogUnexpected("Reading the debug state (call stack and locals)", ex);
            }

            return state;
        }

        private void LogUiThread(string operation)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            context.Log.Debug("debugger: " + operation + " on UI thread (managed thread " + System.Threading.Thread.CurrentThread.ManagedThreadId.ToString(CultureInfo.InvariantCulture) + ")");
        }

        private void LogCreatedBreakpoint(Breakpoint breakpoint, BreakpointInfo info)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            try
            {
                // Read back the interop properties, not just the requested settings: the log is evidence of what VS accepted.
                var message = "debugger: breakpoint readback on UI thread (managed thread "
                    + System.Threading.Thread.CurrentThread.ManagedThreadId.ToString(CultureInfo.InvariantCulture) + ")"
                    + "; conditionType=" + breakpoint.ConditionType + "; hasCondition=" + !string.IsNullOrEmpty(breakpoint.Condition)
                    + "; hitCountType=" + breakpoint.HitCountType + "; hitCount=" + breakpoint.HitCountTarget.ToString(CultureInfo.InvariantCulture)
                    + "; binding=" + (info.BindingState ?? "unknown");
                if (breakpoint is EnvDTE80.Breakpoint2 detailed)
                {
                    message += "; hasLogMessage=" + !string.IsNullOrEmpty(detailed.Message) + "; breakWhenHit=" + detailed.BreakWhenHit;
                }

                context.Log.Debug(message);
            }
            catch (COMException ex)
            {
                context.LogUnexpected("Reading back the created breakpoint settings", ex);
            }
        }

        private static string ValidateLineLocation(string file, int line)
        {
            var normalized = VsAdapterContext.NormalizePath(file);
            if (normalized == null || !VsAdapterContext.IsFilePath(file) || line < 1)
            {
                throw new ArgumentException("A breakpoint location needs a valid absolute file path and a line >= 1.");
            }

            return normalized;
        }

        private static dbgHitCountType ToHitCountType(BreakpointHitCountMode mode) => mode == BreakpointHitCountMode.GreaterOrEqual
            ? dbgHitCountType.dbgHitCountTypeGreaterOrEqual
            : mode == BreakpointHitCountMode.Multiple ? dbgHitCountType.dbgHitCountTypeMultiple : dbgHitCountType.dbgHitCountTypeEqual;

        private static Breakpoint UserBreakpoint(Breakpoint breakpoint)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            // Debugger.Breakpoints is the pending (user) collection. Defensively promote a bound location to its parent;
            // never recurse through Children and never delete or change a bound child on its own.
            return breakpoint.Type == dbgBreakpointType.dbgBreakpointTypeBound
                ? breakpoint.Parent ?? throw new InvalidOperationException("The bound breakpoint has no user parent.")
                : breakpoint;
        }

        private static List<Breakpoint> FindLineBreakpoints(Breakpoints breakpoints, string file, int line, CancellationToken cancellationToken)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            var matches = new List<Breakpoint>();
            foreach (Breakpoint breakpoint in breakpoints)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var parent = UserBreakpoint(breakpoint);
                if (parent.LocationType == dbgBreakpointLocationType.dbgBreakpointLocationTypeFile
                    && parent.FileLine == line && VsAdapterContext.SamePath(parent.File, file) && !matches.Contains(parent))
                {
                    matches.Add(parent);
                }
            }

            return matches;
        }

        private bool DeleteCreated(Breakpoints added)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            var clean = true;
            try
            {
                var parents = new List<Breakpoint>();
                foreach (Breakpoint breakpoint in added)
                {
                    var parent = UserBreakpoint(breakpoint);
                    if (!parents.Contains(parent))
                    {
                        parents.Add(parent);
                    }
                }

                foreach (var parent in parents)
                {
                    try
                    {
                        parent.Delete();
                    }
                    catch (Exception ex)
                    {
                        clean = false;
                        context.LogUnexpected("Cleaning up an incompletely configured breakpoint", ex);
                    }
                }
            }
            catch (Exception ex)
            {
                clean = false;
                context.LogUnexpected("Enumerating incompletely configured breakpoints for cleanup", ex);
            }

            return clean;
        }

        private BreakpointInfo? TryDescribe(Breakpoint breakpoint, DebugMode mode)
        {
            ThreadHelper.ThrowIfNotOnUIThread();

            try
            {
                string? function = null;
                try
                {
                    function = breakpoint.FunctionName;
                }
                catch (COMException)
                {
                    // Not every breakpoint kind has a function.
                }

                var file = breakpoint.File;
                var line = breakpoint.FileLine;
                var condition = breakpoint.Condition;
                var hasHitCount = breakpoint.HitCountType != dbgHitCountType.dbgHitCountTypeNone;

                return new BreakpointInfo
                {
                    File = string.IsNullOrEmpty(file) ? null : file,
                    Line = line > 0 ? line : (int?)null,
                    FunctionName = string.IsNullOrEmpty(function) ? null : function,
                    Condition = string.IsNullOrEmpty(condition) ? null : condition,
                    HitCountTarget = hasHitCount ? breakpoint.HitCountTarget : (int?)null,
                    CurrentHits = breakpoint.CurrentHits,
                    Enabled = breakpoint.Enabled,
                    BindingState = DescribeBinding(breakpoint, mode),
                };
            }
            catch (COMException ex)
            {
                context.LogUnexpected("Reading a breakpoint (its details are unavailable)", ex);
                return null;
            }
        }

        /// <summary>DTE has no explicit binding state. Outside a session: pending; inside: bound when there are bound children.</summary>
        private static string DescribeBinding(Breakpoint breakpoint, DebugMode mode)
        {
            ThreadHelper.ThrowIfNotOnUIThread();

            if (mode == DebugMode.Design)
            {
                return "pending";
            }

            try
            {
                var children = breakpoint.Children;
                return children != null && children.Count > 0 ? "bound" : "unbound";
            }
            catch (COMException)
            {
                return "error";
            }
        }

        private static StackFrameInfo Describe(StackFrame frame)
        {
            ThreadHelper.ThrowIfNotOnUIThread();

            var info = new StackFrameInfo();
            try
            {
                info.FunctionName = frame.FunctionName ?? string.Empty;
                info.Module = frame.Module;
            }
            catch (COMException)
            {
                // Frames without symbols may not expose everything.
            }

            if (frame is EnvDTE90a.StackFrame2 detailed)
            {
                try
                {
                    var file = detailed.FileName;
                    var line = (int)detailed.LineNumber;
                    info.File = string.IsNullOrEmpty(file) ? null : file;
                    info.Line = line > 0 ? line : (int?)null;
                }
                catch (COMException)
                {
                    // No source information for this frame.
                }
            }

            return info;
        }

        private static void AddLocals(StackFrame? frame, List<VariableInfo> into, CancellationToken cancellationToken)
        {
            ThreadHelper.ThrowIfNotOnUIThread();

            if (frame == null)
            {
                return;
            }

            var locals = frame.Locals;
            var count = locals.Count;
            for (var index = 1; index <= count && into.Count < MaxLocals; index++)
            {
                cancellationToken.ThrowIfCancellationRequested();

                try
                {
                    var expression = locals.Item(index);
                    into.Add(new VariableInfo
                    {
                        Name = expression.Name ?? string.Empty,
                        Type = expression.Type,
                        Value = expression.IsValidValue ? TextContent.Truncate(expression.Value, MaxValueChars) : "<unavailable>",
                    });
                }
                catch (COMException)
                {
                    // A local that cannot be evaluated right now is skipped.
                }
            }
        }

        private DebugMode ReadMode(Debugger debugger, bool requireAvailable = false)
        {
            ThreadHelper.ThrowIfNotOnUIThread();

            try
            {
                switch (debugger.CurrentMode)
                {
                    case dbgDebugMode.dbgBreakMode:
                        return DebugMode.Break;
                    case dbgDebugMode.dbgRunMode:
                        return DebugMode.Run;
                    case dbgDebugMode.dbgDesignMode:
                        return DebugMode.Design;
                    default:
                        throw new InvalidOperationException("Visual Studio reported an unknown debugger mode.");
                }
            }
            catch (COMException ex) when (!requireAvailable)
            {
                context.LogUnexpected("Reading the debugger mode", ex);
                return DebugMode.Design;
            }
        }

        private static string? ReadBreakReason(Debugger debugger)
        {
            ThreadHelper.ThrowIfNotOnUIThread();

            try
            {
                const string prefix = "dbgEventReason";
                var reason = debugger.LastBreakReason.ToString();
                return reason.StartsWith(prefix, StringComparison.Ordinal) ? reason.Substring(prefix.Length) : reason;
            }
            catch (COMException)
            {
                return null;
            }
        }
    }
}
