using System.Collections.Generic;
using Newtonsoft.Json.Linq;
using Pomp.Json;

namespace Pomp.Protocol
{
    /// <summary>
    /// One logical JSON object received from omp's stdout (after NDJSON framing and, with protocol v2,
    /// <c>rpc_chunk</c> reassembly). Kept as a <see cref="JObject"/> so unknown/new frame kinds survive;
    /// typed views are available through <see cref="As{T}"/>.
    /// </summary>
    public sealed class OmpFrame
    {
        public OmpFrame(JObject json)
        {
            Json = json;
            // A non-string "type" (object, array, number) makes this an unknown frame instead of an exception.
            Type = json["type"] is JValue type && type.Type == JTokenType.String ? (string?)type ?? string.Empty : string.Empty;
        }

        /// <summary>Frame discriminator, see <see cref="OmpFrameTypes"/>.</summary>
        public string Type { get; }

        public JObject Json { get; }

        public T As<T>()
            where T : class => PompJson.ToObject<T>(Json)!;

        public override string ToString() => Json.ToString(Newtonsoft.Json.Formatting.None);
    }

    /// <summary>First frame omp writes after start-up.</summary>
    public sealed class ReadyFrame
    {
        public int ProtocolVersion { get; set; } = 1;

        public List<int> SupportedProtocolVersions { get; set; } = new List<int>();

        public long? MaxFrameBytes { get; set; }

        public long? MaxReassembledFrameBytes { get; set; }
    }

    /// <summary>Protocol v2 fragment of an oversized stdout object.</summary>
    public sealed class RpcChunkFrame
    {
        public string ChunkId { get; set; } = string.Empty;

        public int Index { get; set; }

        public int Count { get; set; }

        public long ByteLength { get; set; }

        /// <summary>Base64 segment of the original UTF-8 JSON object.</summary>
        public string Data { get; set; } = string.Empty;
    }

    /// <summary>Answer to a command (correlated through <see cref="Id"/>).</summary>
    public sealed class ResponseFrame
    {
        public string? Id { get; set; }

        public string Command { get; set; } = string.Empty;

        public bool Success { get; set; }

        public JToken? Data { get; set; }

        public string? Error { get; set; }

        /// <summary>Machine readable failure code (e.g. <c>session_busy</c>, <c>stale_cursor</c>).</summary>
        public string? Code { get; set; }
    }

    /// <summary>Completion of an accepted <c>prompt</c> (exactly one per prompt that reached the agent).</summary>
    public sealed class PromptResultFrame
    {
        public string? Id { get; set; }

        public bool AgentInvoked { get; set; }

        /// <summary><c>completed</c>, <c>aborted</c> or <c>error</c>.</summary>
        public string Status { get; set; } = string.Empty;

        public PromptErrorInfo? Error { get; set; }

        public bool SessionSettled { get; set; }
    }

    public sealed class PromptErrorInfo
    {
        public string Message { get; set; } = string.Empty;

        public string? Provider { get; set; }

        public string? Model { get; set; }

        public int? HttpStatus { get; set; }

        public bool Retryable { get; set; }
    }

    /// <summary>omp asks the host to execute one of the tools registered with <c>set_host_tools</c>.</summary>
    public sealed class HostToolCallFrame
    {
        /// <summary>Host-call id; echo it in <c>host_tool_update</c>/<c>host_tool_result</c>.</summary>
        public string Id { get; set; } = string.Empty;

        /// <summary>Model-side tool call id (matches <c>tool_execution_*</c> events).</summary>
        public string ToolCallId { get; set; } = string.Empty;

        public string ToolName { get; set; } = string.Empty;

        public JObject Arguments { get; set; } = new JObject();
    }

    /// <summary>omp aborted a pending host tool call.</summary>
    public sealed class HostToolCancelFrame
    {
        public string Id { get; set; } = string.Empty;

        /// <summary>The <see cref="HostToolCallFrame.Id"/> being cancelled.</summary>
        public string TargetId { get; set; } = string.Empty;
    }

    /// <summary>
    /// Dialog or presentation request from an omp extension (tool approvals arrive as <c>select</c>,
    /// see <see cref="OmpApprovalPrompt"/>). Unknown fields remain available through <see cref="OmpFrame.Json"/>.
    /// </summary>
    public sealed class ExtensionUiRequestFrame
    {
        public string Id { get; set; } = string.Empty;

        /// <summary>See <see cref="OmpUiMethods"/>.</summary>
        public string Method { get; set; } = string.Empty;

        public string? Title { get; set; }

        public string? Message { get; set; }

        public List<string>? Options { get; set; }

        public string? Placeholder { get; set; }

        /// <summary>Milliseconds after which omp resolves the dialog to its default.</summary>
        public int? Timeout { get; set; }
    }

    /// <summary>Error raised inside an omp extension.</summary>
    public sealed class ExtensionErrorFrame
    {
        public string? ExtensionPath { get; set; }

        public string? Event { get; set; }

        public string? Error { get; set; }
    }
}
