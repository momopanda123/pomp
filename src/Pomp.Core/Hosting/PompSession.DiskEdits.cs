using System;
using System.Collections.Generic;
using System.Text;
using System.Threading.Tasks;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Pomp.Bridge;
using Pomp.Diagnostics;
using Pomp.Internal;
using Pomp.Protocol;

namespace Pomp.Hosting
{
    // Live for omp's own file tools (edit, write, ast_edit, apply_patch): they write to disk, not through the editor buffer, so the
    // buffer edit path that shows host edits never sees them. POMP's extension (live-edits.ts) reads each target file right before
    // the call and asks the host, with a hidden confirm, to hold the open ones (Visual Studio must not reload them or ask about the
    // outside change while the tool writes); when the call ends it sends those texts here, and the editor shows the change like a
    // host edit. This covers the main agent and its subagents alike (their tool events do not reach the host, the extension's notes do).
    public sealed partial class PompSession
    {
        private const string DiskEditPrefix = "pomp-disk-edit:";
        private const string DiskEditHoldTitle = "pomp-disk-edit-hold";

        private bool HandleDiskEditNotification(string? message, OmpConnection? origin)
        {
            if (message == null || !message.StartsWith(DiskEditPrefix, StringComparison.Ordinal)) return false;
            try
            {
                var note = JObject.Parse(Encoding.UTF8.GetString(Convert.FromBase64String(message.Substring(DiskEditPrefix.Length))));
                lock (_stateLock) if (!ReferenceEquals(origin, _epochConnection)) return true;
                if (!(note["files"] is JArray files)) return true;
                var applied = note["error"]?.Type != JTokenType.Boolean || !(bool)note["error"]!;
                foreach (var file in files)
                {
                    if (!(file is JObject entry) || entry["path"]?.Type != JTokenType.String) continue;
                    var path = (string)entry["path"]!;
                    var before = entry["before"]?.Type == JTokenType.String ? (string?)entry["before"] : null;
                    AdapterGuard.TryRunAsync(async token => { await _adapters.TextBuffers.ShowDiskEditAsync(path, before, applied, token).ConfigureAwait(false); return true; },
                            _options.AdapterTimeout, _logger, "Live: disk edit", _disposeCts.Token)
                        .Forget(_logger, "showing a disk edit of " + path);
                }
            }
            catch (Exception ex) when (ex is FormatException || ex is JsonException || ex is InvalidCastException)
            { _logger.Debug("Ignored a malformed disk edit note."); }
            return true;
        }

        /// <summary>The extension's hidden confirm before a file tool writes: hold the open files, then let the call go on.</summary>
        private bool HandleDiskEditHold(ExtensionUiRequestFrame request, OmpConnection? origin)
        {
            if (request.Method != OmpUiMethods.Confirm || request.Title != DiskEditHoldTitle) return false;
            HoldDiskEditsAsync(request, origin).Forget(_logger, "holding files for a disk edit");
            return true;
        }

        private async Task HoldDiskEditsAsync(ExtensionUiRequestFrame request, OmpConnection? origin)
        {
            try
            {
                var paths = new List<string>();
                if ((request.Message?.Length ?? 0) <= 200000)
                {
                    var note = JObject.Parse(Encoding.UTF8.GetString(Convert.FromBase64String(request.Message ?? string.Empty)));
                    if (note["paths"] is JArray list)
                        foreach (var path in list) if (path.Type == JTokenType.String) paths.Add((string)path!);
                }
                if (paths.Count > 0)
                    await AdapterGuard.TryRunAsync(async token => { await _adapters.TextBuffers.HoldDiskEditAsync(paths, token).ConfigureAwait(false); return true; },
                        _options.AdapterTimeout, _logger, "Live: hold files", _disposeCts.Token).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is FormatException || ex is JsonException || ex is InvalidCastException)
            { _logger.Debug("Ignored a malformed disk edit hold."); }
            // Always answered: the tool waits for it (the extension also stops waiting after a few seconds).
            await SendFrameQuietlyAsync(OmpCommands.ExtensionUiConfirmed(request.Id, true), origin).ConfigureAwait(false);
        }
    }
}
