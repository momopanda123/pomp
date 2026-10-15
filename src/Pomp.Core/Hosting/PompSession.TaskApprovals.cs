using System;
using System.Text;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Pomp.Bridge;
using Pomp.Features;
using Pomp.Permissions;

namespace Pomp.Hosting
{
    public sealed partial class PompSession
    {
        private const string TaskDetailsPrefix = "pomp-task-details:";
        private readonly TaskApprovalDetails _taskApprovalDetails = new TaskApprovalDetails();

        private bool HandleTaskDetailsNotification(string? message, OmpConnection? origin)
        {
            if (message == null || !message.StartsWith(TaskDetailsPrefix, StringComparison.Ordinal)) return false;
            if (message.Length > 1500000) return true;
            try
            {
                var payload = JObject.Parse(Encoding.UTF8.GetString(Convert.FromBase64String(message.Substring(TaskDetailsPrefix.Length))));
                lock (_stateLock)
                {
                    if (origin == null || !ReferenceEquals(origin, _epochConnection) || _sessionSwitchUncertain ||
                        payload["sessionId"]?.Type != JTokenType.String || (string?)payload["sessionId"] != _info.SessionId ||
                        payload["toolCallId"]?.Type != JTokenType.String) return true;
                    var id = (string)payload["toolCallId"]!;
                    if (payload["ended"]?.Type == JTokenType.Boolean && (bool)payload["ended"]!) { _taskApprovalDetails.End(id); return true; }
                    if (!(payload["tasks"] is JArray tasks)) return true;
                    var items = new System.Collections.Generic.List<PermissionTask>();
                    foreach (var item in tasks)
                    {
                        if (!(item is JObject task) || task["agent"]?.Type != JTokenType.String || task["task"]?.Type != JTokenType.String) return true;
                        items.Add(new PermissionTask { Agent = (string)task["agent"]!, Task = (string)task["task"]! });
                    }
                    _taskApprovalDetails.Add(_info.SessionId!, id, items);
                }
            }
            catch (Exception error) when (error is FormatException || error is JsonException || error is InvalidCastException) { }
            return true;
        }
    }
}
