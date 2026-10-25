using System;
using System.Collections.Generic;
using Pomp.Permissions;
using Pomp.Protocol;

namespace Pomp.Hosting
{
    /// <summary>
    /// Maps omp's tool-approval select onto a <see cref="PermissionRequest"/>. The host only answers
    /// the call-once option ("Approve" or "Approve Once") or "Deny", never an engine session/target/directory
    /// grant that would bypass the host gate on subsequent calls.
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

        /// <summary>True when the request has omp's approval title and its known call-once/grant option ordering.</summary>
        public static bool IsApprovalPrompt(ExtensionUiRequestFrame request)
        {
            var options = request.Options;
            if (request.Method != OmpUiMethods.Select || request.Title == null
                || !request.Title.StartsWith(OmpApprovalPrompt.TitlePrefix, StringComparison.Ordinal)
                || options == null || options.Count < 2 || options.Count > 7
                || options[options.Count - 1] != OmpApprovalPrompt.Deny) return false;
            var onceIndex = options.IndexOf(OmpApprovalPrompt.ApproveOnce);
            if (onceIndex < 0)
                return (options.Count == 2 || (options.Count == 3 && IsSessionOption(options[1])))
                    && options[0] == OmpApprovalPrompt.Approve;
            var sessionCount = options.Count - onceIndex - 2;
            if (sessionCount < 0 || sessionCount > 1 || (sessionCount == 1 && !IsSessionOption(options[onceIndex + 1])))
                return false;
            if (onceIndex != 2 && onceIndex != 4) return false;
            var grants = onceIndex / 2;
            for (var i = 0; i < grants; i++)
            {
                var label = options[i];
                if (label != OmpApprovalPrompt.Approve && label != "Approve File"
                    && (label == null || !label.StartsWith("Approve Directory: ", StringComparison.Ordinal)
                        || label.Length <= "Approve Directory: ".Length)) return false;
                if (options[i + grants] != "Always " + label) return false;
            }
            return grants != 2 || options[0]!.StartsWith("Approve Directory: ", StringComparison.Ordinal)
                && (options[1] == OmpApprovalPrompt.Approve || options[1] == "Approve File");
        }

        /// <summary>The only approval answer that does not install an engine-side remembered grant.</summary>
        public static string ApproveThisCall(ExtensionUiRequestFrame request) =>
            request.Options?.Contains(OmpApprovalPrompt.ApproveOnce) == true ? OmpApprovalPrompt.ApproveOnce : OmpApprovalPrompt.Approve;

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
        /// A prompt omp forces despite its ordinary approval tier: a <c>Reason:</c> line (the tool's own
        /// policy, e.g. a dangerous command) or <c>Provider safety checks:</c> (computer use). The
        /// request cannot create a remembered grant. The host's auto-all mode may still answer
        /// a non-computer tool for this call; screen/browser control always needs a person.
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
