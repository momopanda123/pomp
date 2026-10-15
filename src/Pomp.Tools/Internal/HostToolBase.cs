using System;
using System.Collections.Generic;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using Pomp.Adapters;
using Pomp.Diagnostics;
using Pomp.Permissions;

namespace Pomp.Tools
{
    /// <summary>
    /// Common behavior of the built-in tools: no permission by default, and an <see cref="ExecuteAsync"/> that never
    /// throws (adapter, COM, timeout and cancellation problems become short error results).
    /// </summary>
    internal abstract class HostToolBase : IHostTool
    {
        /// <summary>Last-resort cap on a single text block so a misbehaving adapter cannot flood the model context.</summary>
        internal const int MaxResultCharacters = 200000;

        /// <summary>
        /// Load mode of every built-in tool. omp registers host tools as "discoverable" when the mode is omitted, and with
        /// its default <c>tools.xdev</c> setting hides such tools behind <c>xd://</c> devices instead of listing them to the model.
        /// </summary>
        internal const string EssentialLoadMode = "essential";

        protected HostToolBase(HostToolContext context, HostToolDefinition definition)
        {
            Context = context ?? throw new ArgumentNullException(nameof(context));
            Definition = definition ?? throw new ArgumentNullException(nameof(definition));
        }

        public HostToolDefinition Definition { get; }

        protected HostToolContext Context { get; }

        protected string Name => Definition.Name;

        protected IVsAdapterFactory Adapters => Context.Adapters;

        public virtual PermissionKind ResolvePermission(HostToolInvocation invocation) => PermissionKind.None;

        public virtual Task<PermissionRequest?> DescribePermissionAsync(HostToolInvocation invocation, CancellationToken cancellationToken) =>
            Task.FromResult<PermissionRequest?>(null);

        public async Task<HostToolResult> ExecuteAsync(HostToolInvocation invocation, CancellationToken cancellationToken)
        {
            try
            {
                if (invocation == null)
                {
                    throw new ToolException("no invocation was provided.");
                }

                cancellationToken.ThrowIfCancellationRequested();
                var result = await ExecuteCoreAsync(invocation, cancellationToken).ConfigureAwait(false);
                return Cap(result);
            }
            catch (Exception ex)
            {
                LogUnexpected(ex, cancellationToken);
                return ToolErrors.FromException(Name, ex, cancellationToken);
            }
        }

        /// <summary>
        /// Expected outcomes (invalid arguments, cancellation, adapters that are not implemented yet) stay out of the log; anything
        /// else, typically a COMException from Visual Studio, is logged as a warning so the host can record it in the ActivityLog.
        /// </summary>
        private void LogUnexpected(Exception exception, CancellationToken cancellationToken)
        {
            var inner = exception is AggregateException aggregate && aggregate.InnerExceptions.Count == 1 ? aggregate.InnerExceptions[0] : exception;
            if (inner is ToolException
                || inner is NotImplementedException
                || (inner is OperationCanceledException && cancellationToken.IsCancellationRequested))
            {
                return;
            }

            try
            {
                var code = inner is COMException com ? " (HRESULT 0x" + com.HResult.ToString("X8", CultureInfo.InvariantCulture) + ")" : string.Empty;
                Context.Logger.Warn("Host tool '" + Name + "' failed" + code + ": " + ToolErrors.ShortMessage(inner), inner);
            }
            catch (Exception)
            {
                // Logging must never change the tool result.
            }
        }

        protected abstract Task<HostToolResult> ExecuteCoreAsync(HostToolInvocation invocation, CancellationToken cancellationToken);

        /// <summary>Error result prefixed with the tool name.</summary>
        protected HostToolResult Fail(string message) => HostToolResult.Error(Name + ": " + message);

        protected PermissionRequest NewPermissionRequest(PermissionKind kind, string title, string? message, string? target)
        {
            return new PermissionRequest
            {
                Source = PermissionSource.HostTool,
                Kind = kind,
                ToolName = Name,
                Title = title,
                Message = message,
                Target = target,
            };
        }

        /// <summary>Reports progress; a failing progress sink never fails the tool.</summary>
        protected static void ReportProgress(HostToolInvocation invocation, string message)
        {
            try
            {
                invocation.Progress?.Report(message);
            }
            catch (Exception)
            {
                // Progress is best effort.
            }
        }

        private static HostToolResult Cap(HostToolResult result)
        {
            var oversized = false;
            foreach (var block in result.Content)
            {
                if (block.Text != null && block.Text.Length > MaxResultCharacters)
                {
                    oversized = true;
                    break;
                }
            }

            if (!oversized)
            {
                return result;
            }

            var capped = new List<HostToolContent>();
            foreach (var block in result.Content)
            {
                if (block.Text != null && block.Text.Length > MaxResultCharacters)
                {
                    capped.Add(HostToolContent.FromText(block.Text.Substring(0, MaxResultCharacters) + "\n[output truncated at " + MaxResultCharacters + " characters]"));
                }
                else
                {
                    capped.Add(block);
                }
            }

            return new HostToolResult(capped, result.IsError, result.Details);
        }
    }

}
