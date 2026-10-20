namespace Pomp.Protocol
{
    /// <summary>
    /// "type" values of frames omp writes to stdout in RPC mode.
    /// Canonical source: oh-my-pi docs/rpc.md and packages/coding-agent/src/modes/rpc/rpc-types.ts.
    /// </summary>
    public static class OmpFrameTypes
    {
        // Transport / command plumbing
        public const string Ready = "ready";
        public const string Response = "response";
        public const string RpcChunk = "rpc_chunk";
        public const string PromptResult = "prompt_result";
        public const string SessionSettled = "session_settled";

        // Host callbacks
        public const string ExtensionUiRequest = "extension_ui_request";
        public const string HostToolCall = "host_tool_call";
        public const string HostToolCancel = "host_tool_cancel";
        public const string HostUriRequest = "host_uri_request";
        public const string HostUriCancel = "host_uri_cancel";

        // Side channels
        public const string ExtensionError = "extension_error";
        public const string AvailableCommandsUpdate = "available_commands_update";
        public const string CommandOutput = "command_output";
        public const string SessionInfoUpdate = "session_info_update";
        public const string ConfigUpdate = "config_update";
        public const string Notice = "notice";

        // Side questions (/btw): the full record at each lifecycle change, and answer text as it streams
        public const string BtwRecord = "btw_record";
        public const string BtwDelta = "btw_delta";

        // Agent session events (AgentSessionEvent)
        public const string AgentStart = "agent_start";
        public const string AgentEnd = "agent_end";
        public const string TurnStart = "turn_start";
        public const string TurnEnd = "turn_end";
        public const string MessageStart = "message_start";
        public const string MessageUpdate = "message_update";
        public const string MessageEnd = "message_end";
        public const string ToolExecutionStart = "tool_execution_start";
        public const string ToolExecutionUpdate = "tool_execution_update";
        public const string ToolExecutionEnd = "tool_execution_end";
        public const string AutoCompactionStart = "auto_compaction_start";
        public const string AutoCompactionEnd = "auto_compaction_end";
        public const string AutoRetryStart = "auto_retry_start";
        public const string AutoRetryEnd = "auto_retry_end";
        public const string ModelChanged = "model_changed";
        public const string ThinkingLevelChanged = "thinking_level_changed";
    }

    /// <summary>"type" values the host writes to omp's stdin.</summary>
    public static class OmpCommandTypes
    {
        // Commands (answered by a "response" frame carrying the same id)
        public const string Prompt = "prompt";
        public const string Steer = "steer";
        public const string FollowUp = "follow_up";
        public const string Abort = "abort";
        public const string AbortAndPrompt = "abort_and_prompt";
        public const string AbortAndRestoreQueue = "abort_and_restore_queue";
        public const string RemoveQueuedMessage = "remove_queued_message";
        public const string Btw = "btw";
        public const string BtwCancel = "btw_cancel";
        public const string NewSession = "new_session";
        public const string OpenSession = "open_session";
        public const string NegotiateProtocol = "negotiate_protocol";
        public const string GetState = "get_state";

        /// <summary>Replaces the engine's in-memory todo phases (not written to the session file; omp 18.4.3–18.8.6).</summary>
        public const string SetTodos = "set_todos";
        public const string GetAvailableCommands = "get_available_commands";
        public const string SetHostTools = "set_host_tools";
        public const string SetHostUriSchemes = "set_host_uri_schemes";
        public const string SetEventFilter = "set_event_filter";
        public const string SetModel = "set_model";
        public const string GetAvailableModels = "get_available_models";
        public const string SetThinkingLevel = "set_thinking_level";
        public const string Compact = "compact";
        public const string SetAutoCompaction = "set_auto_compaction";
        public const string GetSessionStats = "get_session_stats";
        public const string GetMessages = "get_messages";
        public const string GetLastAssistantText = "get_last_assistant_text";

        // Host replies to omp-initiated requests (no response frame)
        public const string ExtensionUiResponse = "extension_ui_response";
        public const string HostToolUpdate = "host_tool_update";
        public const string HostToolResult = "host_tool_result";
        public const string HostUriResult = "host_uri_result";
    }

    /// <summary>Methods carried by <see cref="OmpFrameTypes.ExtensionUiRequest"/>.</summary>
    public static class OmpUiMethods
    {
        public const string Select = "select";
        public const string Confirm = "confirm";
        public const string Input = "input";
        public const string Editor = "editor";
        public const string Cancel = "cancel";
        public const string Notify = "notify";
        public const string SetStatus = "setStatus";
        public const string SetWidget = "setWidget";
        public const string SetTitle = "setTitle";
        public const string SetEditorText = "set_editor_text";
        public const string OpenUrl = "open_url";
    }

    /// <summary>
    /// Shape of omp's tool-approval prompt: an extension UI "select" whose title starts with
    /// "Allow tool: &lt;name&gt;" and whose options are ["Approve", "Deny"], or (omp 18.11.3 and later, for a prompt the approval
    /// mode raised) ["Approve", "Approve &lt;subject&gt; for this session", "Deny"]
    /// (packages/coding-agent/src/extensibility/extensions/wrapper.ts, tools/approval.ts).
    /// </summary>
    public static class OmpApprovalPrompt
    {
        public const string TitlePrefix = "Allow tool: ";
        public const string Approve = "Approve";
        public const string Deny = "Deny";

        /// <summary>
        /// Start and end of the optional middle option. Choosing it makes omp skip every later prompt of that subject and tier for
        /// the rest of the session, past the host's gate, so the host never answers with it.
        /// </summary>
        public const string SessionOptionPrefix = "Approve ";
        public const string SessionOptionSuffix = " for this session";

        /// <summary>Line omp adds when the tool's own policy demands the prompt (e.g. a dangerous command).</summary>
        public const string ReasonPrefix = "Reason:";

        /// <summary>
        /// Line omp adds before the provider's pending computer-use safety checks. omp prompts for those even in yolo mode or
        /// with a per-tool allow rule, so a human must answer each one.
        /// </summary>
        public const string ProviderSafetyChecksPrefix = "Provider safety checks:";
    }

    /// <summary>Values of the omp <c>--approval-mode</c> launch flag.</summary>
    public enum OmpApprovalMode
    {
        /// <summary>Auto-approves read-tier tools; prompts for write and exec (pomp default).</summary>
        AlwaysAsk,

        /// <summary>Auto-approves read and write tiers; prompts for exec.</summary>
        Write,

        /// <summary>Approves everything without prompting.</summary>
        Yolo,
    }

    public static class OmpApprovalModeExtensions
    {
        public static string ToCliValue(this OmpApprovalMode mode)
        {
            switch (mode)
            {
                case OmpApprovalMode.Write:
                    return "write";
                case OmpApprovalMode.Yolo:
                    return "yolo";
                default:
                    return "always-ask";
            }
        }

        /// <summary>The page's value ("alwaysAsk" | "write" | "yolo"; the CLI spellings are accepted too), case-insensitive.</summary>
        public static bool TryParseUiValue(string? value, out OmpApprovalMode mode)
        {
            switch ((value ?? string.Empty).Trim().ToLowerInvariant())
            {
                case "alwaysask":
                case "always-ask":
                    mode = OmpApprovalMode.AlwaysAsk;
                    return true;
                case "write":
                    mode = OmpApprovalMode.Write;
                    return true;
                case "yolo":
                    mode = OmpApprovalMode.Yolo;
                    return true;
                default:
                    mode = OmpApprovalMode.AlwaysAsk;
                    return false;
            }
        }
    }
}
