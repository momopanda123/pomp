using System;
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json.Linq;
using Pomp.Json;
using Pomp.Tools;

namespace Pomp.Protocol
{
    /// <summary>
    /// Builders for frames the host writes to omp's stdin. Commands are created without an "id";
    /// the connection assigns one when it sends them and correlates the "response" frame.
    /// </summary>
    public static class OmpCommands
    {
        public static JObject Prompt(string message, string? streamingBehavior = null)
        {
            var command = Command(OmpCommandTypes.Prompt);
            command["message"] = message;
            if (streamingBehavior != null)
            {
                // "steer" | "followUp" — required while the agent is streaming.
                command["streamingBehavior"] = streamingBehavior;
            }

            return command;
        }

        public static JObject Abort() => Command(OmpCommandTypes.Abort);

        public static JObject NewSession() => Command(OmpCommandTypes.NewSession);

        public static JObject NegotiateProtocol(int protocolVersion)
        {
            var command = Command(OmpCommandTypes.NegotiateProtocol);
            command["protocolVersion"] = protocolVersion;
            return command;
        }

        public static JObject GetState() => Command(OmpCommandTypes.GetState);

        public static JObject SetTodos(JArray phases)
        {
            var command = Command(OmpCommandTypes.SetTodos);
            command["phases"] = phases;
            return command;
        }

        public static JObject GetSessionStats() => Command(OmpCommandTypes.GetSessionStats);

        public static JObject GetAvailableModels() => Command(OmpCommandTypes.GetAvailableModels);

        public static JObject SetModel(string provider, string modelId)
        {
            var command = Command(OmpCommandTypes.SetModel);
            command["provider"] = provider;
            command["modelId"] = modelId;
            return command;
        }

        public static JObject SetThinkingLevel(string level)
        {
            var command = Command(OmpCommandTypes.SetThinkingLevel);
            command["level"] = level;
            return command;
        }

        public static JObject Compact(string? customInstructions = null)
        {
            var command = Command(OmpCommandTypes.Compact);
            if (customInstructions != null)
            {
                command["customInstructions"] = customInstructions;
            }

            return command;
        }

        public static JObject SetHostTools(IEnumerable<HostToolDefinition> tools)
        {
            var command = Command(OmpCommandTypes.SetHostTools);
            command["tools"] = new JArray(tools.Select(PompJson.ToJObject));
            return command;
        }

        /// <summary>Restricts forwarded session events; <c>null</c> forwards everything.</summary>
        public static JObject SetEventFilter(IEnumerable<string>? eventTypes)
        {
            var command = Command(OmpCommandTypes.SetEventFilter);
            command["events"] = eventTypes == null ? JValue.CreateNull() : new JArray(eventTypes);
            return command;
        }

        public static JObject ExtensionUiValue(string requestId, string value) =>
            new JObject { ["type"] = OmpCommandTypes.ExtensionUiResponse, ["id"] = requestId, ["value"] = value };

        public static JObject ExtensionUiConfirmed(string requestId, bool confirmed) =>
            new JObject { ["type"] = OmpCommandTypes.ExtensionUiResponse, ["id"] = requestId, ["confirmed"] = confirmed };

        public static JObject ExtensionUiCancelled(string requestId, bool timedOut = false)
        {
            var frame = new JObject { ["type"] = OmpCommandTypes.ExtensionUiResponse, ["id"] = requestId, ["cancelled"] = true };
            if (timedOut)
            {
                frame["timedOut"] = true;
            }

            return frame;
        }

        public static JObject HostToolUpdate(string hostCallId, string text)
        {
            var partial = new JObject { ["content"] = new JArray(new JObject { ["type"] = "text", ["text"] = text }) };
            return new JObject { ["type"] = OmpCommandTypes.HostToolUpdate, ["id"] = hostCallId, ["partialResult"] = partial };
        }

        public static JObject HostToolResult(string hostCallId, HostToolResult result)
        {
            if (result == null)
            {
                throw new ArgumentNullException(nameof(result));
            }

            var content = new JArray(result.Content.Select(PompJson.ToJObject));
            var payload = new JObject { ["content"] = content };
            if (result.Details != null)
            {
                payload["details"] = result.Details;
            }

            var frame = new JObject { ["type"] = OmpCommandTypes.HostToolResult, ["id"] = hostCallId, ["result"] = payload };
            if (result.IsError)
            {
                frame["isError"] = true;
            }

            return frame;
        }

        private static JObject Command(string type) => new JObject { ["type"] = type };
    }
}
