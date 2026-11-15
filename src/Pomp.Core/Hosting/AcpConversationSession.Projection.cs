using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Pomp.AgentClients;
using Pomp.Features;
using Pomp.Ui;

namespace Pomp.Hosting
{
    internal sealed partial class AcpConversationSession
    {
        // The same bound the OMP backend applies to each string of a tool's arguments shown in the chat.
        private const int ToolArgumentStringChars = 1500;
        private sealed class ToolState
        {
            internal string UiId = "";
            internal JObject Data = new JObject();
            internal bool Ended;
        }
        private readonly Dictionary<string, ToolState> _toolStates = new Dictionary<string, ToolState>(StringComparer.Ordinal);
        private readonly Dictionary<string, string> _assistantIds = new Dictionary<string, string>(StringComparer.Ordinal);
        private readonly HashSet<string> _openAssistantIds = new HashSet<string>(StringComparer.Ordinal);
        private string? _anonymousAssistant, _replayTurnId, _replayUserId;
        private readonly StringBuilder _replayUser = new StringBuilder();
        private JArray _plan = new JArray();
        private string? _planRevision;
        private int _planOpen;

        private void OnNotification(AcpConnection connection, long generation, JObject message)
        {
            if ((string?)message["method"] != "session/update" || message["params"] is not JObject parameters || parameters["update"] is not JObject update) return;
            var output = new List<UiMessage>();
            bool changed = false;
            lock (_sync)
            {
                if (_disposed || !ReferenceEquals(_connection, connection) || generation != _generation) return;
                if (_agentSessionId == null)
                {
                    var chars = message.ToString(Formatting.None).Length;
                    if (_earlyUpdates.Count >= 100 || _earlyUpdateChars + chars > 1024 * 1024)
                        throw new InvalidOperationException(PompLanguage.Pick("대화가 만들어지기 전에 ACP 업데이트가 너무 많이 도착했어요.", "Too many ACP updates arrived before session creation was confirmed."));
                    _earlyUpdateChars += chars;
                    _earlyUpdates.Add(message);
                    return;
                }
                if ((string?)parameters["sessionId"] != _agentSessionId) return;
                var type = (string?)update["sessionUpdate"];
                if (_replaying && type != "user_message_chunk") FlushReplayUserLocked(output);
                switch (type)
                {
                    case "user_message_chunk":
                        // Live input already has its correlated optimistic/queued/steered bubble. Replay is the agent's source of history.
                        if (!_replaying) break;
                        var messageId = (string?)update["messageId"];
                        if (_replayUser.Length > 0 && messageId != null && _replayUserId != messageId) FlushReplayUserLocked(output);
                        _replayUserId = messageId;
                        if (_replayUser.Length < 20000)
                        {
                            var chunk = ContentText(update["content"]);
                            var count = Math.Min(chunk.Length, 20000 - _replayUser.Length);
                            _replayUser.Append(chunk, 0, count);
                            if (count < chunk.Length) _replayUser.Append(PompLanguage.Pick("\n[이후 내용은 표시하지 않았어요]", "\n[Display truncated]"));
                        }
                        break;
                    case "agent_message_chunk":
                    case "agent_thought_chunk":
                        if (_turn == null && !_replaying) break;
                        EnsureReplayTurnLocked(output);
                        var remoteId = (string?)update["messageId"];
                        string assistant;
                        if (remoteId != null)
                        {
                            if (!_assistantIds.TryGetValue(remoteId, out assistant!))
                            {
                                assistant = NextId("acp-assistant"); _assistantIds[remoteId] = assistant;
                                _openAssistantIds.Add(assistant);
                                output.Add(new UiMessage(UiMessageTypes.ChatAssistantStart, new ChatAssistantStartPayload { MessageId = assistant }));
                            }
                        }
                        else
                        {
                            if (_anonymousAssistant == null)
                            {
                                _anonymousAssistant = NextId("acp-assistant"); _openAssistantIds.Add(_anonymousAssistant);
                                output.Add(new UiMessage(UiMessageTypes.ChatAssistantStart, new ChatAssistantStartPayload { MessageId = _anonymousAssistant }));
                            }
                            assistant = _anonymousAssistant;
                        }
                        var text = ContentText(update["content"]);
                        if (text.Length > 0) output.Add(new UiMessage(UiMessageTypes.ChatAssistantDelta, new ChatAssistantDeltaPayload
                        { MessageId = assistant, Kind = type == "agent_thought_chunk" ? "thinking" : "text", Delta = text }));
                        break;
                    case "tool_call":
                    case "tool_call_update":
                        if (_turn == null && !_replaying) break;
                        EnsureReplayTurnLocked(output);
                        _anonymousAssistant = null;
                        ProjectToolLocked(update, output);
                        break;
                    case "plan":
                        if (update["entries"] is JArray plan)
                        {
                            _plan = plan;
                            _planOpen = plan.OfType<JObject>().Count(entry => (string?)entry["status"] != "completed");
                            _planRevision = ToolResourceFiles.Hash(plan.ToString(Formatting.None));
                            changed = true;
                        }
                        break;
                    case "config_option_update": ApplyConfigurationLocked(update); changed = true; break;
                    case "available_commands_update":
                        if (update["availableCommands"] is JArray commands) { _availableCommands = commands; changed = true; }
                        break;
                    case "session_info_update":
                        if (update["title"]?.Type == JTokenType.String)
                        {
                            _title = (string)update["title"]!; changed = true;
                        }
                        break;
                    case "usage_update":
                        var used = (long?)update["used"]; var size = (long?)update["size"];
                        if (used >= 0 && size > 0)
                        {
                            _usage = new ContextUsagePayload { Tokens = used.Value, ContextWindow = size.Value, Percent = 100.0 * used.Value / size.Value }; changed = true;
                        }
                        break;
                    case "notice":
                        output.Add(new UiMessage(UiMessageTypes.ChatNotice, new ChatNoticePayload
                        {
                            Level = (string?)update["severity"] == "error" ? "error" : (string?)update["severity"] == "warning" ? "warning" : "info",
                            Message = (string?)update["title"] + (update["description"]?.Type == JTokenType.String ? "\n" + (string?)update["description"] : ""),
                        }));
                        break;
                }
            }
            PostTranscript(output);
            if (changed) PublishState();
        }

        private static string ContentText(JToken? content)
        {
            if (content is not JObject block) return "";
            switch ((string?)block["type"])
            {
                case "text": return (string?)block["text"] ?? "";
                case "resource_link": return (string?)block["uri"] ?? (string?)block["name"] ?? "";
                case "resource": return (string?)block["resource"]?["text"] ?? (string?)block["resource"]?["uri"] ?? "";
                case "image": return PompLanguage.Pick("\n[이미지: ", "\n[Image: ") + ((string?)block["mimeType"] ?? "image") + "]\n";
                case "audio": return PompLanguage.Pick("\n[오디오: ", "\n[Audio: ") + ((string?)block["mimeType"] ?? "audio") + "]\n";
                default: return block.ToString(Formatting.Indented);
            }
        }

        private static string ToolName(JObject tool) => (string?)tool["name"] ?? (string?)tool["title"] ?? (string?)tool["kind"] ?? PompLanguage.Pick("도구", "Tool");
        private void ProjectToolLocked(JObject update, List<UiMessage> output)
        {
            var id = (string?)update["toolCallId"];
            if (string.IsNullOrEmpty(id)) return;
            var fresh = !_toolStates.TryGetValue(id!, out var tool);
            if (fresh) { tool = new ToolState { UiId = NextId("acp-tool") }; _toolStates.Add(id!, tool); }
            foreach (var field in update.Properties())
            {
                // Null/omitted fields are not clears in ACP v1 tool updates.
                if (field.Name != "sessionUpdate" && field.Value.Type != JTokenType.Null) tool!.Data[field.Name] = field.Value.DeepClone();
            }
            if (fresh || (!tool!.Ended && (update["rawInput"] != null || update["name"] != null || update["title"] != null)))
            {
                var input = tool!.Data["rawInput"];
                output.Add(new UiMessage(UiMessageTypes.ChatToolStart, new ChatToolStartPayload
                {
                    ToolCallId = tool.UiId, ToolName = ToolName(tool.Data), IsHostTool = false,
                    // Tool input can carry whole files; the page and every transcript replay get the shortened form, as on the OMP backend.
                    Args = input == null ? null
                        : Pomp.Internal.JsonExtensions.Compact(input as JObject ?? new JObject { ["value"] = input }, ToolArgumentStringChars),
                }));
            }
            var text = ToolText(tool!.Data);
            var status = (string?)tool.Data["status"];
            if (status == "completed" || status == "failed")
            {
                tool.Ended = true;
                output.Add(new UiMessage(UiMessageTypes.ChatToolEnd, new ChatToolEndPayload
                {
                    ToolCallId = tool.UiId, ToolName = ToolName(tool.Data), IsError = status == "failed", ResultText = text,
                }));
            }
            else if (text.Length > 0)
                output.Add(new UiMessage(UiMessageTypes.ChatToolUpdate, new ChatToolUpdatePayload { ToolCallId = tool.UiId, Text = text }));
        }

        private static string ToolText(JObject tool)
        {
            var text = new StringBuilder();
            if (tool["content"] is JArray content)
                foreach (var item in content.OfType<JObject>())
                {
                    if ((string?)item["type"] == "diff")
                    {
                        text.Append("--- ").Append((string?)item["path"]).Append(" (before)\n").Append((string?)item["oldText"])
                            .Append("\n+++ ").Append((string?)item["path"]).Append(" (after)\n").Append((string?)item["newText"]).Append('\n');
                    }
                    else if ((string?)item["type"] == "content") text.Append(ContentText(item["content"])).Append('\n');
                    else text.Append(item.ToString(Formatting.Indented)).Append('\n');
                    if (text.Length > 100000) break;
                }
            if (text.Length == 0 && tool["rawOutput"] != null) text.Append(tool["rawOutput"]!.ToString(Formatting.Indented));
            if (text.Length > 100000) { text.Length = 100000; text.Append(PompLanguage.Pick("\n[이후 내용은 표시하지 않았어요]", "\n[Display truncated]")); }
            return text.ToString();
        }

        private void ResetMessageProjectionLocked()
        {
            _toolStates.Clear(); _assistantIds.Clear(); _openAssistantIds.Clear(); _anonymousAssistant = null;
        }
        private void ResetProjection()
        {
            lock (_sync)
            {
                ResetMessageProjectionLocked(); _replayUser.Clear(); _replayUserId = null; _replayTurnId = null;
                _plan = new JArray(); _planRevision = null; _planOpen = 0;
            }
        }
        private void FinishMessagesLocked(List<UiMessage> output, string reason, string? error)
        {
            foreach (var id in _openAssistantIds)
                output.Add(new UiMessage(UiMessageTypes.ChatAssistantEnd, new ChatAssistantEndPayload { MessageId = id, StopReason = reason, ErrorMessage = error }));
            _openAssistantIds.Clear(); _anonymousAssistant = null;
        }
        private void FlushReplayUserLocked(List<UiMessage> output)
        {
            if (_replayUser.Length == 0) return;
            FinishMessagesLocked(output, "end_turn", null);
            if (_replayTurnId != null) output.Add(new UiMessage(UiMessageTypes.ChatTurnEnd, new ChatTurnEndPayload { PromptId = _replayTurnId }));
            ResetMessageProjectionLocked();
            _replayTurnId = NextId("acp-history");
            output.Add(new UiMessage(UiMessageTypes.ChatTurnStarted, new ChatTurnStartedPayload { PromptId = _replayTurnId, Text = _replayUser.ToString(), Main = true }));
            _replayUser.Clear(); _replayUserId = null;
        }
        private void EnsureReplayTurnLocked(List<UiMessage> output)
        {
            if (!_replaying || _replayTurnId != null) return;
            _replayTurnId = NextId("acp-history");
            output.Add(new UiMessage(UiMessageTypes.ChatTurnStarted, new ChatTurnStartedPayload { PromptId = _replayTurnId, Text = "" }));
        }
        private void FinishReplay()
        {
            var output = new List<UiMessage>();
            lock (_sync)
            {
                FlushReplayUserLocked(output);
                FinishMessagesLocked(output, "end_turn", null);
                foreach (var tool in _toolStates.Values.Where(t => !t.Ended))
                    output.Add(new UiMessage(UiMessageTypes.ChatToolEnd, new ChatToolEndPayload { ToolCallId = tool.UiId, ToolName = ToolName(tool.Data),
                        IsError = true, ResultText = PompLanguage.Pick("이 이전 도구 호출은 끝난 기록이 없어요.", "No completion was recorded for this earlier tool call.") }));
                if (_replayTurnId != null) output.Add(new UiMessage(UiMessageTypes.ChatTurnEnd, new ChatTurnEndPayload { PromptId = _replayTurnId }));
                _replayTurnId = null;
            }
            PostTranscript(output);
        }
        private void PostTranscript(IEnumerable<UiMessage> messages)
        {
            foreach (var message in messages)
                _ui.Post(message.Type, message.Payload, message.Type == UiMessageTypes.ChatNotice ? UiDelivery.State : UiDelivery.Transcript);
        }
        private int PlanOpenCountLocked() => _planOpen;
        private JArray PlanPhasesLocked()
        {
            if (_plan.Count == 0) return new JArray();
            return new JArray(new JObject { ["name"] = "Antigravity", ["tasks"] = new JArray(_plan.OfType<JObject>().Take(200).Select(p => new JObject
            { ["content"] = (string?)p["content"] ?? "", ["status"] = (string?)p["status"] ?? "pending" })) });
        }
    }
}
