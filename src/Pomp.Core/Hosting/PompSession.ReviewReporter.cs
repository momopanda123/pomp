using System;
using System.Text;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Pomp.Bridge;
using Pomp.Diagnostics;

namespace Pomp.Hosting
{
    // Who reported an agent finding (Work › 코드 점검 shows that agent's picture and name). A host tool call does not say which agent made
    // it; POMP's extension does, right before the call reaches the host: the chat's POMP ("main") or a member's role name.
    public sealed partial class PompSession
    {
        private const string ReviewReporterPrefix = "pomp-review-reporter:";

        private bool HandleReviewReporterNotification(string? message, OmpConnection? origin)
        {
            if (message == null || !message.StartsWith(ReviewReporterPrefix, StringComparison.Ordinal)) return false;
            if (message.Length > 2000) return true;
            try
            {
                var note = JObject.Parse(Encoding.UTF8.GetString(Convert.FromBase64String(message.Substring(ReviewReporterPrefix.Length))));
                var toolCallId = note["toolCallId"]?.Type == JTokenType.String ? (string?)note["toolCallId"] : null;
                var reporter = (string?)note["kind"] == "sub" ? note["name"]?.Type == JTokenType.String ? (string?)note["name"] : null : "main";
                lock (_stateLock) if (!ReferenceEquals(origin, _epochConnection)) return true;
                if (toolCallId != null && reporter != null) _codeReview?.NoteReporter(toolCallId, reporter);
            }
            catch (Exception ex) when (ex is FormatException || ex is JsonException || ex is InvalidCastException)
            { _logger.Debug("Ignored a malformed review reporter note."); }
            return true;
        }
    }
}
