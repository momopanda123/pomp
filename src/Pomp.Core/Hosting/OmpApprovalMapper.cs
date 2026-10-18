using System;
using System.Collections.Generic;
using Pomp.Permissions;
using Pomp.Protocol;

namespace Pomp.Hosting
{
    /// <summary>
    /// Recognizes omp's tool-approval prompt (an extension UI <c>select</c> titled <c>Allow tool: &lt;name&gt;</c> with the options
    /// <c>Approve</c>/<c>Deny</c>, optionally with omp's <c>Approve &lt;subject&gt; for this session</c> between them) and maps it onto a
    /// <see cref="PermissionRequest"/>. The host answers only <c>Approve</c> or <c>Deny</c>, so every call still passes its gate.
    /// </summary>
    internal static class OmpApprovalMapper
    {
        private static readonly HashSet<string> ProcessTools = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "bash", "eval", "python", "shell", "sh", "zsh", "cmd", "powershell", "pwsh", "exec", "run", "terminal",
        };

        private static readonly HashSet<string> FileWriteTools = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "edit", "write", "delete", "move", "rename", "ast_edit", "notebook", "notebook_edit", "patch", "apply_patch",
        };

        /// <summary>True when the request has exactly the shape of omp's approval prompt.</summary>
        public static bool IsApprovalPrompt(ExtensionUiRequestFrame request)
        {
            var options = request.Options;
            return request.Method == OmpUiMethods.Select
                && request.Title != null
                && request.Title.StartsWith(OmpApprovalPrompt.TitlePrefix, StringComparison.Ordinal)
                && options != null
                && (options.Count == 2 || (options.Count == 3 && IsSessionOption(options[1])))
                && options[0] == OmpApprovalPrompt.Approve
                && options[options.Count - 1] == OmpApprovalPrompt.Deny;
        }

        private static bool IsSessionOption(string? option) =>
            option != null
            && option.Length > OmpApprovalPrompt.SessionOptionPrefix.Length + OmpApprovalPrompt.SessionOptionSuffix.Length
            && option.StartsWith(OmpApprovalPrompt.SessionOptionPrefix, StringComparison.Ordinal)
            && option.EndsWith(OmpApprovalPrompt.SessionOptionSuffix, StringComparison.Ordinal);

        /// <summary>Maps an approval prompt; null when <paramref name="request"/> is not one.</summary>
        public static PermissionRequest? TryMap(ExtensionUiRequestFrame request)
        {
            if (!IsApprovalPrompt(request))
            {
                return null;
            }

            var title = request.Title!;
            var firstLineEnd = title.IndexOfAny(new[] { '\r', '\n' });
            var firstLine = firstLineEnd < 0 ? title : title.Substring(0, firstLineEnd);
            var toolName = firstLine.Substring(OmpApprovalPrompt.TitlePrefix.Length).Trim();

            return new PermissionRequest
            {
                RequestId = request.Id,
                Source = PermissionSource.OmpApproval,
                Kind = MapKind(toolName),
                ToolName = toolName,
                Title = firstLine.Trim(),
                Message = title,
                Timeout = request.Timeout is int ms && ms > 0 ? TimeSpan.FromMilliseconds(ms) : (TimeSpan?)null,
                CanRemember = !IsForcedPrompt(title),
            };
        }

        /// <summary>
        /// A prompt that omp shows even where a user "allow" rule or yolo mode would skip it: a <c>Reason:</c> line (the tool's own
        /// policy, e.g. a dangerous command) or a <c>Provider safety checks:</c> section (the model provider's pending computer-use
        /// safety checks). Such a prompt is answered for this call only, never from a remembered grant. Lines of the tool's details
        /// that happen to look like a marker only make the prompt stricter.
        /// </summary>
        public static bool IsForcedPrompt(string title)
        {
            foreach (var line in title.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries))
            {
                var trimmed = line.TrimStart();
                if (trimmed.StartsWith(OmpApprovalPrompt.ReasonPrefix, StringComparison.Ordinal)
                    || trimmed.StartsWith(OmpApprovalPrompt.ProviderSafetyChecksPrefix, StringComparison.Ordinal))
                {
                    return true;
                }
            }

            return false;
        }

        /// <summary>bash/eval/python/shell-like → ProcessExec; edit/write/delete/move/ast_edit/notebook → FileWrite; anything else → Other.</summary>
        public static PermissionKind MapKind(string toolName)
        {
            if (string.IsNullOrEmpty(toolName))
            {
                return PermissionKind.Other;
            }

            if (ProcessTools.Contains(toolName)
                || toolName.IndexOf("bash", StringComparison.OrdinalIgnoreCase) >= 0
                || toolName.IndexOf("shell", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                return PermissionKind.ProcessExec;
            }

            return FileWriteTools.Contains(toolName) ? PermissionKind.FileWrite : PermissionKind.Other;
        }
    }
}
