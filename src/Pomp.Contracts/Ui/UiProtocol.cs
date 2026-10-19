using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Pomp.Bridge;
using Pomp.Json;
using Pomp.Permissions;

namespace Pomp.Ui
{
    /// <summary>
    /// Envelope of every message between the WebView2 chat UI and the host
    /// (window.chrome.webview.postMessage / CoreWebView2.PostWebMessageAsJson). Spec: docs/ui-protocol.md.
    /// </summary>
    public sealed class UiMessage
    {
        public UiMessage()
        {
        }

        public UiMessage(string type, object? payload = null, string? id = null)
        {
            Type = type;
            Id = id;
            Payload = payload == null ? null : payload as JObject ?? PompJson.ToJObject(payload);
        }

        public string Type { get; set; } = string.Empty;

        /// <summary>Optional correlation id.</summary>
        public string? Id { get; set; }

        public JObject? Payload { get; set; }

        public T? PayloadAs<T>()
            where T : class => PompJson.ToObject<T>(Payload);
    }

    /// <summary>Message types (the "type" field). Keep in sync with docs/ui-protocol.md and src/Pomp.WebUI.</summary>
    public static class UiMessageTypes
    {
        // Host -> UI
        public const string HostHello = "host/hello";
        public const string BridgeState = "bridge/state";
        public const string ThemeChanged = "theme/changed";
        public const string SessionInfo = "session/info";
        public const string ChatTurnStarted = "chat/turnStarted";
        public const string ChatAssistantStart = "chat/assistantStart";
        public const string ChatAssistantDelta = "chat/assistantDelta";
        public const string ChatAssistantNarration = "chat/assistantNarration";
        public const string ChatAssistantEnd = "chat/assistantEnd";
        public const string ChatToolStart = "chat/toolStart";
        public const string ChatToolUpdate = "chat/toolUpdate";
        public const string ChatToolEnd = "chat/toolEnd";
        public const string ChatTurnEnd = "chat/turnEnd";
        public const string ChatNotice = "chat/notice";

        /// <summary>
        /// Host → UI: a message the user steered into the running agent (<see cref="ChatSteeredPayload"/>); part of the transcript, shown
        /// as a user bubble marked as steered inside the running turn.
        /// </summary>
        public const string ChatSteered = "chat/steered";

        /// <summary>Host → UI: a side question's full record (<see cref="BtwRecordPayload"/>) at each lifecycle change; not part of the transcript.</summary>
        public const string BtwRecord = "btw/record";

        /// <summary>Host → UI: answer text of the running side question (<see cref="BtwDeltaPayload"/>).</summary>
        public const string BtwDelta = "btw/delta";

        /// <summary>Host → UI answer to <see cref="BtwAsk"/> (<see cref="BtwReplyPayload"/>).</summary>
        public const string BtwReply = "btw/reply";

        /// <summary>Host → UI: the notes an advisor gave the chat agent (<see cref="ChatAdvisorNotesPayload"/>); part of the transcript.</summary>
        public const string ChatAdvisorNotes = "chat/advisorNotes";
        public const string PermissionRequest = "permission/request";
        public const string PermissionDismiss = "permission/dismiss";

        /// <summary>Host → UI answer to <see cref="PermissionMode"/>: <c>{ requestId, ok, mode, error? }</c>.</summary>
        public const string PermissionModeResult = "permission/modeResult";
        public const string DialogRequest = "dialog/request";
        public const string DialogDismiss = "dialog/dismiss";
        public const string SessionNewResult = "session/newResult";
        public const string WorkspaceFiles = "workspace/files";
        public const string ChatReplayBegin = "chat/replayBegin";
        public const string ChatReplayEnd = "chat/replayEnd";
        public const string SetupState = "setup/state";
        public const string CommandsState = "commands/state";
        public const string SessionHistoryState = "session/historyState";
        public const string SessionFastResult = "session/fastResult";
        public const string EngineInfo = "engine/info";

        /// <summary>Host → UI: open a view of the work tab (after an action the user started outside the page, e.g. an inline edit).</summary>
        public const string WorkOpen = "work/open";

        /// <summary>Host → UI: fill the chat input from the editor's POMP menu (explain, review, ask, add file); optionally send it.</summary>
        public const string ComposerPrefill = "composer/prefill";

        public const string ComposerPredict = "composer/predict";
        public const string ComposerPrediction = "composer/prediction";
        public const string ProblemsState = "workspace/problemsState";
        public const string ProblemsCommand = "workspace/problems";
        public const string ProblemsReply = "workspace/problemsReply";
        public const string AdvisorsCommand = "advisors/command";
        public const string AdvisorsReply = "advisors/reply";
        public const string AdvisorsState = "advisors/state";

        /// <summary>Host → UI: agent roles and teams (docs/architecture.md §8), and the reply to one agents/command.</summary>
        public const string AgentsState = "agents/state";
        public const string AgentsReply = "agents/reply";
        public const string ConversationTabsState = "conversation/tabsState";
        public const string ConversationTabReply = "conversation/tabReply";
        public const string ChatQueued = "chat/queued";
        public const string ChatQueueState = "chat/queueState";
        public const string ChatQueueReply = "chat/queueReply";

        /// <summary>The engine configuration in the workspace and whether the user trusted it (<see cref="WorkspaceTrustStatePayload"/>).</summary>
        public const string WorkspaceTrustState = "workspace/trustState";
        public const string AgentsHistoryState = "agents/historyState";
        public const string AgentsHistoryReply = "agents/historyReply";
        public const string ToolsState = "tools/state";
        public const string ToolsReply = "tools/reply";
        public const string SkillsState = "skills/state";
        public const string SkillsReply = "skills/reply";
        public const string McpState = "mcp/state";
        public const string McpReply = "mcp/reply";

        /// <summary>Host → UI: the catalog bundled with POMP (its MCP servers are registered once in the user's mcp.json), and the reply to one catalog/command.</summary>
        public const string CatalogState = "catalog/state";
        public const string CatalogReply = "catalog/reply";

        /// <summary>Host → UI: the project and user rule files (<c>.pomp/rules/*.mdc</c>), and the reply to one rules/command.</summary>
        public const string RulesState = "rules/state";
        public const string RulesActive = "rules/active";
        public const string RulesReply = "rules/reply";

        /// <summary>Host → UI: saved workflows (<c>.pomp/workflows/*.json</c>, project and user), and the reply to one workflows/command.</summary>
        public const string WorkflowsState = "workflows/state";
        public const string WorkflowsReply = "workflows/reply";

        /// <summary>Host → UI: the project hook files (<c>.pomp/hooks/*.md</c>), and the reply to one hooks/command.</summary>
        public const string HooksState = "hooks/state";
        public const string HooksReply = "hooks/reply";

        /// <summary>Host → UI: the workspace's knowledge <c>{requestId?, root, sessionId?, selected, optimizing?, items, error?}</c>.</summary>
        public const string KnowledgeState = "knowledge/state";

        /// <summary>Host → UI: editor lines on their way into a new knowledge <c>{root, path, startLine, endLine, text}</c> (the page opens the Tools › Knowledge form); parked for a page that is not ready yet, like <see cref="ComposerPrefill"/>.</summary>
        public const string KnowledgeDraft = "knowledge/draft";

        /// <summary>Host → UI: event rules between agents and teams, this conversation's forwarding switch and recent firings.</summary>
        public const string AgentHooksState = "agentHooks/state";

        /// <summary>Host → UI: computer use for this conversation (docs/architecture.md §8.5), and the reply to one computer/set.</summary>
        public const string ComputerState = "computer/state";
        public const string ComputerReply = "computer/reply";
        public const string Error = "error";

        // UI -> Host
        public const string UiReady = "ui/ready";
        public const string UiLog = "ui/log";
        public const string ChatSend = "chat/send";
        public const string ChatStop = "chat/stop";

        /// <summary>
        /// UI → host (<see cref="ChatSendPayload"/>): the message goes to the running agent now (omp <c>steer</c>); with nothing running it
        /// is sent like <see cref="ChatSend"/>.
        /// </summary>
        public const string ChatSteer = "chat/steer";

        /// <summary>UI → host <c>{ requestId, question, recordId? }</c> (<see cref="BtwAskPayload"/>): a side question; answered by <see cref="BtwReply"/>.</summary>
        public const string BtwAsk = "btw/ask";

        /// <summary>UI → host <c>{ recordId? }</c>: cancels the running side question (of that topic only, when given).</summary>
        public const string BtwCancel = "btw/cancel";
        public const string PermissionRespond = "permission/respond";

        /// <summary>UI → host <c>{ requestId, mode: alwaysAsk | write | yolo }</c>: the composer's permission-mode chip; answered by <see cref="PermissionModeResult"/>.</summary>
        public const string PermissionMode = "permission/mode";
        public const string DialogRespond = "dialog/respond";

        /// <summary>{ requestId, action: refresh | trust | open, root, fingerprint? (trust), path? (open) }; answered by workspace/trustState.</summary>
        public const string WorkspaceTrust = "workspace/trust";
        public const string SessionNew = "session/new";
        public const string BridgeRestart = "bridge/restart";
        public const string LogOpen = "log/open";
        public const string SettingsOpen = "settings/open";
        public const string WorkspaceFindFiles = "workspace/findFiles";

        /// <summary>UI → host <c>{ requestId, root, sessionId?, includeEditorContext, attachments?: [{ path }] }</c>; answered by <see cref="WorkspaceBasketTokens"/>.</summary>
        public const string WorkspaceCountBasketTokens = "workspace/countBasketTokens";

        /// <summary>Host → UI answer to <see cref="WorkspaceCountBasketTokens"/>: <c>{ requestId, ok, tokens, editorContext, attachments, error? }</c>.</summary>
        public const string WorkspaceBasketTokens = "workspace/basketTokens";
        public const string WorkspacePickFiles = "workspace/pickFiles";
        public const string SetupCommand = "setup/command";
        public const string SessionHistory = "session/history";
        public const string SessionFast = "session/fast";
        public const string AgentsCommand = "agents/command";
        public const string ConversationTab = "conversation/tab";
        public const string ChatQueue = "chat/queue";

        /// <summary>Edit of the agent's current plan (engine todo phases): <c>{requestId, sessionId, base, phases}</c>.</summary>
        public const string AgentPlan = "workspace/agentPlan";

        /// <summary>Host → UI answer to <see cref="AgentPlan"/> and <see cref="AgentPlanEdit"/>: <c>{requestId, ok, error?}</c>.</summary>
        public const string AgentPlanResult = "workspace/agentPlanResult";

        /// <summary>One page of the whole plan: <c>{requestId, sessionId, page, size, query?}</c>; answered by <see cref="AgentPlanPageResult"/>.</summary>
        public const string AgentPlanPage = "workspace/agentPlanPage";

        /// <summary>
        /// Host → UI page of the plan: <c>{requestId, ok, error?, revision, page, size, total, phases: [{index, name, taskCount}],
        /// rows: [{phase, task, content, status, blocker?}]}</c>; <c>total</c> counts the tasks that match the query.
        /// </summary>
        public const string AgentPlanPageResult = "workspace/agentPlanPageResult";

        /// <summary>
        /// One change to the whole plan, checked against <c>revision</c>: <c>{requestId, sessionId, revision, op, phase?, task?, name?,
        /// content?, status?, blocker?}</c> with op <c>addTask | updateTask | deleteTask | addPhase | renamePhase | deletePhase</c>.
        /// </summary>
        public const string AgentPlanEdit = "workspace/agentPlanEdit";
        public const string AgentsHistory = "agents/history";
        public const string ComputerSet = "computer/set";
        public const string ToolsCommand = "tools/command";
        public const string SkillsCommand = "skills/command";
        public const string McpCommand = "mcp/command";
        public const string CatalogCommand = "catalog/command";

        /// <summary>UI → host: open a workspace .svg/.html file in the script-free preview document tab.</summary>
        public const string PreviewOpen = "preview/open";
        public const string RulesCommand = "rules/command";
        public const string WorkflowsCommand = "workflows/command";
        public const string HooksCommand = "hooks/command";
        public const string KnowledgeCommand = "knowledge/command";
        public const string AgentHooksCommand = "agentHooks/command";
    }

    /// <summary>Transport between the host and the chat UI.</summary>
    public interface IUiChannel
    {
        /// <summary>True once the page has sent <see cref="UiMessageTypes.UiReady"/>.</summary>
        bool IsReady { get; }

        /// <summary>Raised for every message coming from the page. Handlers must return quickly.</summary>
        event EventHandler<UiMessage>? MessageReceived;

        /// <summary>Thread-safe; marshals to the UI thread. Messages posted before the page is ready are queued.</summary>
        Task PostAsync(UiMessage message, CancellationToken cancellationToken = default);
    }

    // ---- Host -> UI payloads -------------------------------------------------------------------

    public sealed class HostHelloPayload
    {
        public string Version { get; set; } = string.Empty;

        public string? VsVersion { get; set; }

        /// <summary>UI culture, e.g. "ko-KR".</summary>
        public string? Locale { get; set; }

        public bool IncludeEditorContextByDefault { get; set; } = true;

        public bool ComposerPredictions { get; set; }

        /// <summary>Word completion in the chat input (Tools > Options > Pomp > Chat); absent from an older host = on.</summary>
        public bool WordCompletion { get; set; } = true;

        /// <summary>omp's approval mode in effect (Tools > Options > Pomp > Agent, or the composer's permission-mode chip).</summary>
        public Pomp.Protocol.OmpApprovalMode ApprovalMode { get; set; }

        /// <summary>The page's keyboard shortcuts (Tools > Options > Pomp > Shortcuts).</summary>
        public UiShortcutsPayload Shortcuts { get; set; } = new UiShortcutsPayload();
    }

    /// <summary>
    /// Keyboard shortcuts of the chat page as chord strings: optional Ctrl/Alt/Shift and one key ("Ctrl+Alt+T"); empty = none.
    /// <see cref="Send"/> is "Enter" (Shift+Enter inserts a line break) or "Ctrl+Enter" (Enter inserts a line break).
    /// </summary>
    public sealed class UiShortcutsPayload
    {
        public string Send { get; set; } = "Enter";

        /// <summary>Opens the one-message send-target picker of the composer.</summary>
        public string SendTargetPicker { get; set; } = "Ctrl+Alt+T";

        /// <summary>Makes the previous agent or team of the send-target list the composer's target (from the first, back to the chat's partner).</summary>
        public string PreviousTarget { get; set; } = "Alt+,";

        /// <summary>Makes the next agent or team of the send-target list the composer's target (after the last, back to the chat's partner).</summary>
        public string NextTarget { get; set; } = "Alt+.";

        /// <summary>Opens or closes the composer's context basket on its first file (arrow keys move between the files, Space checks one).</summary>
        public string ContextBasket { get; set; } = "Alt+;";

        /// <summary>Turns "include editor context" on or off.</summary>
        public string EditorContext { get; set; } = "Alt+'";

        /// <summary>Toggles host-only Live edit following without submitting a prompt.</summary>
        public string LiveFollow { get; set; } = "Alt+L";

        /// <summary>Toggles POMP edit bookmarks without submitting a prompt.</summary>
        public string LiveBookmarks { get; set; } = "Alt+K";

        /// <summary>Opens the composer's permission-mode picker.</summary>
        public string PermissionMode { get; set; } = "Alt+Q";

        /// <summary>Answers the pending request card: allow once / confirm.</summary>
        public string Allow { get; set; } = "Alt+A";

        /// <summary>Answers the pending request card: deny / cancel.</summary>
        public string Deny { get; set; } = "Alt+D";

        /// <summary>
        /// Moves the focus from the chat page back to the editor (the page handles it itself, since keys inside the WebView never reach
        /// Visual Studio); the same chord is the IDE binding of the Toggle Editor ↔ Chat Focus command.
        /// </summary>
        public string ToggleFocus { get; set; } = "Alt+J";

        /// <summary>Opens Tools › Permissions.</summary>
        public string PermissionsView { get; set; } = "Ctrl+Alt+P";
    }

    /// <summary>
    /// Keyboard shortcuts of POMP's Visual Studio commands as chord strings (canonical "Alt+Shift+E"; empty = no key), set in
    /// Tools > Options > Pomp > Shortcuts and applied to the IDE's key bindings by the VSIX. The code actions and the two prompts are
    /// text-editor shortcuts; Open POMP, New conversation and the focus toggle are global.
    /// </summary>
    public sealed class EditorShortcutsPayload
    {
        public string OpenPomp { get; set; } = "Alt+Shift+Q";

        public string NewConversation { get; set; } = "Alt+Shift+N";

        public string ExplainCode { get; set; } = "Alt+Shift+E";

        public string ReviewCode { get; set; } = "Alt+Shift+R";

        public string FixCode { get; set; } = "Alt+Shift+F";

        public string WriteTests { get; set; } = "Alt+Shift+U";

        public string AddFileToContext { get; set; } = "Alt+Shift+I";

        public string InlineEdit { get; set; } = "Alt+M";

        public string KnowledgeTag { get; set; } = "Alt+/";

        public string ToggleFocus { get; set; } = "Alt+J";

        public string LiveFollow { get; set; } = "Alt+L";

        public string LiveBookmarks { get; set; } = "Alt+K";
    }

    /// <summary>
    /// workspace/trustState (docs/architecture.md "작업 영역 신뢰"): what omp would load from the workspace that can run code, start
    /// processes, change approvals or pull in outside files. With <see cref="Required"/> and not <see cref="Trusted"/> omp is not started
    /// (bridge failure reason <c>workspaceTrustRequired</c>) until the user trusts exactly this set.
    /// </summary>
    public sealed class WorkspaceTrustStatePayload
    {
        /// <summary>The workspace/trust request this answers, if any.</summary>
        public string? RequestId { get; set; }

        public string Root { get; set; } = string.Empty;

        public bool Required { get; set; }

        public bool Trusted { get; set; }

        /// <summary>Changes when any listed file is added, removed or edited; trust names it, so a changed set is never trusted unseen.</summary>
        public string Fingerprint { get; set; } = string.Empty;

        public List<WorkspaceTrustFilePayload> Files { get; set; } = new List<WorkspaceTrustFilePayload>();

        /// <summary>More files than listed (the fingerprint still covers what was scanned).</summary>
        public bool Truncated { get; set; }

        /// <summary>Why the request failed (e.g. the files changed meanwhile), for the request it answers.</summary>
        public string? Error { get; set; }
    }

    public sealed class WorkspaceTrustFilePayload
    {
        /// <summary>Workspace-relative, '/' separated.</summary>
        public string Path { get; set; } = string.Empty;

        /// <summary>settings, mcp, tools, lsp, plugins, extensions or include.</summary>
        public string Kind { get; set; } = string.Empty;

        /// <summary>E.g. "MCP 서버 2개", or for an include the outside file it pulls into the conversation.</summary>
        public string? Detail { get; set; }
    }

    public sealed class BridgeStatePayload
    {
        public BridgeState State { get; set; }

        public string? Message { get; set; }

        public int? Attempt { get; set; }

        public int? NextRetryMs { get; set; }

        /// <summary>Why the bridge failed, when the UI should react specifically (<see cref="Pomp.Bridge.BridgeFailureReasons"/>).</summary>
        public string? Reason { get; set; }
    }

    public sealed class ThemePayload
    {
        /// <summary>"dark" or "light".</summary>
        public string Kind { get; set; } = "dark";

        /// <summary>CSS custom properties, e.g. { "--pomp-bg": "#1e1e1e" }. Names: docs/ui-protocol.md.</summary>
        public Dictionary<string, string> Colors { get; set; } = new Dictionary<string, string>();

        public string? FontFamily { get; set; }

        public double? FontSizePx { get; set; }
    }

    public sealed class ModelInfoPayload
    {
        public string Provider { get; set; } = string.Empty;

        public string Id { get; set; } = string.Empty;

        public string? Name { get; set; }

        public long? ContextWindow { get; set; }
    }

    public sealed class ContextUsagePayload
    {
        public long Tokens { get; set; }

        public long ContextWindow { get; set; }

        public double Percent { get; set; }

        /// <summary>What the context holds, by source (the engine's /context breakdown, refreshed after each turn); null until known.</summary>
        public ContextBreakdownPayload? Breakdown { get; set; }
    }

    /// <summary>
    /// Tokens of the context window by source, as the engine reports them (<c>/context</c>): the system prompt, the tool definitions
    /// (<see cref="SystemTools"/> in total; <see cref="HostTools"/> and <see cref="McpTools"/> are the host's estimates of the VS and
    /// MCP shares inside it), the context files (rules, AGENTS.md), the skills list and the messages. <see cref="Free"/> and
    /// <see cref="AutoCompactBuffer"/> are the engine's own figures for the rest of the window.
    /// </summary>
    public sealed class ContextBreakdownPayload
    {
        public long SystemPrompt { get; set; }

        public long SystemTools { get; set; }

        /// <summary>Estimated tokens of the VS host tools inside <see cref="SystemTools"/>.</summary>
        public long HostTools { get; set; }

        /// <summary>Estimated tokens of the MCP tools inside <see cref="SystemTools"/>.</summary>
        public long McpTools { get; set; }

        public long SystemContext { get; set; }

        public long Skills { get; set; }

        public long Messages { get; set; }

        /// <summary>
        /// True when the engine printed no "Messages" row (it does not count the conversation for some models): <see cref="Messages"/>
        /// is then the host's estimate — the engine's usage total minus the rows it did print — and the page marks it as such.
        /// </summary>
        public bool MessagesEstimated { get; set; }

        public long AutoCompactBuffer { get; set; }

        public long Free { get; set; }

        /// <summary>The window the breakdown was measured against.</summary>
        public long ContextWindow { get; set; }

        /// <summary>When the engine answered (UTC, ISO 8601).</summary>
        public string MeasuredAt { get; set; } = string.Empty;
    }

    /// <summary>UI → host <c>workspace/countBasketTokens</c>: estimate what the next message adds beyond its text (editor context, attachment list).</summary>
    public sealed class CountBasketTokensCommand
    {
        public string RequestId { get; set; } = string.Empty;

        public string Root { get; set; } = string.Empty;

        public string? SessionId { get; set; }

        public bool IncludeEditorContext { get; set; }

        public List<ChatAttachment>? Attachments { get; set; }
    }

    /// <summary>Host → UI <c>workspace/basketTokens</c>: the estimate (characters-based, not the model's tokenizer).</summary>
    public sealed class BasketTokensPayload
    {
        public bool? Estimated { get; set; }

        public string RequestId { get; set; } = string.Empty;

        public bool Ok { get; set; }

        /// <summary>Estimated tokens of everything counted.</summary>
        public long Tokens { get; set; }

        public long EditorContext { get; set; }

        public long Attachments { get; set; }

        public string? Error { get; set; }
    }

    public sealed class AgentPlanTaskPayload
    {
        public string Content { get; set; } = string.Empty;
        public string Status { get; set; } = "pending";

        /// <summary>What a blocked task waits for, as the engine reported it.</summary>
        public string? Blocker { get; set; }
    }

    public sealed class AgentPlanPhasePayload
    {
        public string Name { get; set; } = string.Empty;
        public List<AgentPlanTaskPayload> Tasks { get; set; } = new List<AgentPlanTaskPayload>();
    }

    public sealed class SessionInfoPayload
    {
        public string RecoveryStatus { get; set; } = "ready";

        public string? RecoveryMessage { get; set; }

        public ModelInfoPayload? Model { get; set; }

        public string? ThinkingLevel { get; set; }

        public string? SessionId { get; set; }

        public string? SessionName { get; set; }

        public List<AgentPlanPhasePayload> TodoPhases { get; set; } = new List<AgentPlanPhasePayload>();

        public bool PlanTruncated { get; set; }

        /// <summary>Every task of the plan (<see cref="TodoPhases"/> holds at most the first 200).</summary>
        public int PlanTaskCount { get; set; }

        /// <summary>Tasks of the whole plan that are not completed or abandoned.</summary>
        public int PlanOpenCount { get; set; }

        /// <summary>Hash of the whole plan; a page or an edit names the revision it was made against.</summary>
        public string? PlanRevision { get; set; }

        public bool HasPendingAsyncWork { get; set; }

        public ContextUsagePayload? ContextUsage { get; set; }

        public bool ThreeTurnCompaction { get; set; }

        public bool IsStreaming { get; set; }

        public string? WorkingDirectory { get; set; }

        /// <summary>omp's fast mode (priority service tier) for the current model; null before the first get_state.</summary>
        public FastModePayload? FastMode { get; set; }
    }

    /// <summary>Fast mode as <c>get_state</c> reports it, plus whether the current model has a service tier to switch.</summary>
    public sealed class FastModePayload
    {
        /// <summary>The model's provider family has a priority tier omp can toggle (openai, anthropic, google, ...).</summary>
        public bool Available { get; set; }

        /// <summary>Requested for the model's family (<c>fastModeEnabled</c>).</summary>
        public bool Enabled { get; set; }

        /// <summary>Actually in effect for the next request (<c>fastModeActive</c>); can lag behind <see cref="Enabled"/>.</summary>
        public bool Active { get; set; }
    }

    /// <summary>The active editor reference included in this turn's composed prompt.</summary>
    public sealed class ChatEditorContext
    {
        /// <summary>Workspace-relative path when inside the workspace, otherwise the full path.</summary>
        public string Path { get; set; } = string.Empty;

        /// <summary>First selected line (1-based); omitted without a non-empty selection.</summary>
        public int? StartLine { get; set; }

        /// <summary>Last selected line (1-based, inclusive); an end at column 1 excludes that line.</summary>
        public int? EndLine { get; set; }

        /// <summary>Caret line (1-based) whose nearby editor text was included.</summary>
        public int? CaretLine { get; set; }
    }

    public sealed class ChatTurnStartedPayload
    {
        public string PromptId { get; set; } = string.Empty;

        public string? ClientMessageId { get; set; }

        /// <summary>
        /// The user's text as typed (without the editor context the host prepends), so a replayed transcript can show the user
        /// bubble. A page that already shows the optimistic bubble for <see cref="ClientMessageId"/> ignores it.
        /// </summary>
        public string? Text { get; set; }

        public List<ChatAttachment>? Attachments { get; set; }

        /// <summary>The editor reference actually composed for this turn; omitted when no active file context was included.</summary>
        public ChatEditorContext? EditorContext { get; set; }

        /// <summary>The message's target as sent (docs/architecture.md §8.2, §8.3), so a replayed bubble keeps its "→ target" badge.</summary>
        public string? Team { get; set; }

        public string? Agent { get; set; }

        public string? AgentInstance { get; set; }

        /// <summary>True when the conversation answered itself (no team, agent or instance), so a retry keeps that destination.</summary>
        public bool? Main { get; set; }
    }

    /// <summary>A message steered into the running agent, shown where it was sent.</summary>
    public sealed class ChatSteeredPayload
    {
        /// <summary>Unique id of this steered message (never a turn's prompt id).</summary>
        public string SteerId { get; set; } = string.Empty;

        public string? ClientMessageId { get; set; }

        /// <summary>The user's text as typed.</summary>
        public string Text { get; set; } = string.Empty;

        public List<ChatAttachment>? Attachments { get; set; }

        public ChatEditorContext? EditorContext { get; set; }
    }

    public sealed class BtwAskPayload
    {
        public string RequestId { get; set; } = string.Empty;
        public string Question { get; set; } = string.Empty;

        /// <summary>The topic (id of its first record) this question follows up; null starts a new topic.</summary>
        public string? RecordId { get; set; }
    }

    public sealed class BtwCancelPayload
    {
        public string? RecordId { get; set; }
    }

    public sealed class BtwReplyPayload
    {
        public string RequestId { get; set; } = string.Empty;
        public bool Ok { get; set; }
        public string? Error { get; set; }

        /// <summary>omp's <c>BtwHistoryRecord</c> as started: <c>{ id, question, answer, status, createdAt, updatedAt, error?, followUps? }</c>.</summary>
        public JObject? Record { get; set; }
    }

    public sealed class BtwRecordPayload
    {
        /// <summary>omp's <c>BtwHistoryRecord</c>; the last one for an id wins.</summary>
        public JObject Record { get; set; } = new JObject();
    }

    public sealed class BtwDeltaPayload
    {
        public string RecordId { get; set; } = string.Empty;
        public string Delta { get; set; } = string.Empty;
    }

    public sealed class ChatAssistantStartPayload
    {
        public string MessageId { get; set; } = string.Empty;
    }

    public sealed class ChatAssistantDeltaPayload
    {
        public string MessageId { get; set; } = string.Empty;

        /// <summary>"text" or "thinking".</summary>
        public string Kind { get; set; } = "text";

        public string Delta { get; set; } = string.Empty;
    }

    /// <summary>
    /// The thinking block just streamed for <see cref="MessageId"/> was narration (omp 18.12+: prose the model addressed to the user
    /// that the provider returned as thinking): the page moves <see cref="Text"/>, the end of that message's thinking, to its answer text.
    /// </summary>
    public sealed class ChatAssistantNarrationPayload
    {
        public string MessageId { get; set; } = string.Empty;

        public string Text { get; set; } = string.Empty;
    }

    public sealed class ChatAssistantEndPayload
    {
        public string MessageId { get; set; } = string.Empty;

        public string? StopReason { get; set; }

        public string? ErrorMessage { get; set; }
    }

    public sealed class ChatToolStartPayload
    {
        public string ToolCallId { get; set; } = string.Empty;

        public string ToolName { get; set; } = string.Empty;

        public JObject? Args { get; set; }

        public bool IsHostTool { get; set; }
    }

    public sealed class ChatToolUpdatePayload
    {
        public string ToolCallId { get; set; } = string.Empty;

        public string Text { get; set; } = string.Empty;
    }

    public sealed class ChatToolEndPayload
    {
        public string ToolCallId { get; set; } = string.Empty;

        public string ToolName { get; set; } = string.Empty;

        public bool IsError { get; set; }

        /// <summary>Result text, truncated by the host for display.</summary>
        public string? ResultText { get; set; }

        public long? DurationMs { get; set; }
    }

    public sealed class ChatTurnEndPayload
    {
        public string PromptId { get; set; } = string.Empty;

        /// <summary>"completed", "aborted" or "error".</summary>
        public string Status { get; set; } = "completed";

        public UiErrorInfo? Error { get; set; }
    }

    public sealed class ChatNoticePayload
    {
        /// <summary>"info", "warning" or "error".</summary>
        public string Level { get; set; } = "info";

        public string Message { get; set; } = string.Empty;

        /// <summary>"pre" for command output laid out in columns (shown in a monospace block); null = ordinary text.</summary>
        public string? Format { get; set; }
    }

    public sealed class PermissionRequestPayload
    {
        public string RequestId { get; set; } = string.Empty;

        public PermissionSource Source { get; set; }

        public List<PermissionTask>? Tasks { get; set; }

        public PermissionKind Kind { get; set; }

        public string ToolName { get; set; } = string.Empty;

        public string Title { get; set; } = string.Empty;

        public string? Message { get; set; }

        public string? Target { get; set; }

        /// <summary>Unified diff preview.</summary>
        public string? Diff { get; set; }

        public int? TimeoutMs { get; set; }

        /// <summary>False: offer only "allow once" and "deny" (the host downgrades remembered answers to "allow once").</summary>
        public bool CanRemember { get; set; } = true;
    }

    /// <summary>The one answer to a <c>session/new</c>: the page clears its transcript only on "completed".</summary>
    public sealed class SessionNewResultPayload
    {
        public string? RequestId { get; set; }

        /// <summary>"completed", "failed" or "cancelled" (an omp extension vetoed the new session).</summary>
        public string Status { get; set; } = "completed";

        public string? Message { get; set; }
    }

    public sealed class DismissPayload
    {
        public string RequestId { get; set; } = string.Empty;

        public string? Reason { get; set; }
    }

    public sealed class DialogRequestPayload
    {
        public string RequestId { get; set; } = string.Empty;

        /// <summary>"select", "confirm", "input" or "editor".</summary>
        public string Method { get; set; } = string.Empty;

        public string? Title { get; set; }

        public string? Message { get; set; }

        public List<string>? Options { get; set; }

        public string? Placeholder { get; set; }

        public string? DefaultValue { get; set; }

        public int? TimeoutMs { get; set; }
    }

    public sealed class UiErrorInfo
    {
        public string Code { get; set; } = "error";

        public string Message { get; set; } = string.Empty;

        public bool Retryable { get; set; }
    }

    // ---- UI -> Host payloads -------------------------------------------------------------------

    public sealed class UiLogPayload
    {
        public string Level { get; set; } = "info";

        public string Message { get; set; } = string.Empty;
    }

    public sealed class ChatAttachment
    {
        public string Path { get; set; } = string.Empty;
    }

    public sealed class ChatSendPayload
    {
        /// <summary>Id generated by the UI for its optimistic message bubble.</summary>
        public string ClientMessageId { get; set; } = string.Empty;

        public string Text { get; set; } = string.Empty;

        /// <summary>Prepend the active editor/solution/error-list summary (design: addAdditionalContext).</summary>
        public bool IncludeEditorContext { get; set; } = true;

        public List<ChatAttachment>? Attachments { get; set; }

        /// <summary>A team id (docs/architecture.md §8.2): the message goes to that team through <c>/pomp-team</c>; null = this conversation.</summary>
        public string? Team { get; set; }

        /// <summary>A role name (docs/architecture.md §8.3): a new assignment run by exactly that agent (<c>/pomp-agent</c>).</summary>
        public string? Agent { get; set; }

        /// <summary>A subagent id of this conversation (docs/architecture.md §8.3): a direct message to that existing agent (<c>/pomp-dm</c>).</summary>
        public string? AgentInstance { get; set; }

        /// <summary>
        /// True: the conversation answers itself, without the team routing of a team conversation (docs/architecture.md §8.8). Used by page
        /// actions (work plan, code review, editor menu) and by retries of such messages. Excludes the targets above.
        /// </summary>
        public bool? Main { get; set; }
    }

    /// <summary>workspace/findFiles: workspace files whose path contains <see cref="Query"/> (the @-mention list).</summary>
    public sealed class WorkspaceFindFilesPayload
    {
        public string RequestId { get; set; } = string.Empty;

        /// <summary>Case-insensitive substring of the workspace-relative path; empty = the most relevant files (open documents first).</summary>
        public string Query { get; set; } = string.Empty;

        /// <summary>At most this many files (1..100, default 30).</summary>
        public int? Limit { get; set; }
    }

    /// <summary>workspace/pickFiles: the host shows a file dialog (multi-select) to attach files.</summary>
    public sealed class WorkspacePickFilesPayload
    {
        public string RequestId { get; set; } = string.Empty;
    }

    public sealed class WorkspaceFileEntry
    {
        public string? Kind { get; set; }
        public string? Symbol { get; set; }
        public int? Line { get; set; }
        /// <summary>Workspace-relative with forward slashes when inside the workspace, otherwise the full path.</summary>
        public string Path { get; set; } = string.Empty;

        public string FullPath { get; set; } = string.Empty;
    }

    /// <summary>workspace/files: the answer to workspace/findFiles or workspace/pickFiles (same requestId).</summary>
    public sealed class WorkspaceFilesPayload
    {
        public string RequestId { get; set; } = string.Empty;

        public List<WorkspaceFileEntry> Files { get; set; } = new List<WorkspaceFileEntry>();

        /// <summary>More files matched than were returned.</summary>
        public bool Truncated { get; set; }

        /// <summary>Why the request failed or was cancelled (a closed dialog is not an error: empty files, no error).</summary>
        public string? Error { get; set; }
    }

    /// <summary>
    /// chat/replayBegin: the page must drop its transcript and rebuild it from the chat/* messages that follow, up to
    /// chat/replayEnd (sent after ui/ready when the host still holds the current session's transcript, e.g. after a page reload).
    /// </summary>
    public sealed class ChatReplayBeginPayload
    {
        /// <summary>Older messages of the session were dropped from the host's bounded transcript log.</summary>
        public bool Truncated { get; set; }
    }

    public sealed class PermissionRespondPayload
    {
        public string RequestId { get; set; } = string.Empty;

        public PermissionDecision Decision { get; set; } = PermissionDecision.Deny;

        public string? Note { get; set; }
    }

    public sealed class DialogRespondPayload
    {
        public string RequestId { get; set; } = string.Empty;

        public string? Value { get; set; }

        public bool? Confirmed { get; set; }

        public bool? Cancelled { get; set; }
    }

    /// <summary>
    /// Which omp engine runs (docs/ui-protocol.md "Engine version"): the one the POMP package ships and pins (verified), or another one
    /// the options name or the PC has installed (not verified against this POMP release).
    /// </summary>
    public sealed class EngineInfoPayload
    {
        /// <summary>"bundled" (the package's own, verified), "settings" (Tools &gt; Options path) or "installed" (PATH / usual install folders).</summary>
        public string Source { get; set; } = "bundled";

        /// <summary>The engine's version when known (bundled: from the package manifest; others: from <c>omp --version</c>).</summary>
        public string? Version { get; set; }

        /// <summary>The engine is exactly the file this POMP release was tested with (size and SHA-256 match the package manifest).</summary>
        public bool Verified { get; set; }

        /// <summary>The omp version this POMP release ships and was tested with; null in a development package without an engine.</summary>
        public string? ExpectedVersion { get; set; }

        /// <summary>Full path of an engine that is not the bundled one (shown so that the user can tell which omp runs).</summary>
        public string? Path { get; set; }
    }

    /// <summary>UI → host <c>composer/predict</c>: the current draft, at most 4,000 characters.</summary>
    public sealed class ComposerPredictPayload
    {
        public string RequestId { get; set; } = string.Empty;
        public string SessionId { get; set; } = string.Empty;
        public string Text { get; set; } = string.Empty;
    }

    public sealed class ComposerPredictionPayload
    {
        public string RequestId { get; set; } = string.Empty;
        public string? Text { get; set; }
        public string? Error { get; set; }
    }

    public sealed class AdvisorsCommandPayload
    {
        public string RequestId { get; set; } = string.Empty;
        public string Action { get; set; } = string.Empty;
        public string? Scope { get; set; }
        public string? Path { get; set; }
        public AdvisorDocumentPayload? Document { get; set; }
        public string? BaseHash { get; set; }
    }
    public sealed class AdvisorDocumentPayload
    {
        public string? Instructions { get; set; }
        public int? MaxNotesPerUpdate { get; set; }
        public List<AdvisorEntryPayload> Advisors { get; set; } = new List<AdvisorEntryPayload>();
    }
    public sealed class AdvisorEntryPayload
    {
        public string Name { get; set; } = string.Empty;
        public bool? Enabled { get; set; }
        public string? Model { get; set; }
        public string? ReviewMode { get; set; }
        public int? ReviewInterval { get; set; }
        public string? SyncBacklog { get; set; }
        public List<string>? Tools { get; set; }
        public string? Instructions { get; set; }
        public int? MaxNotesPerUpdate { get; set; }
    }
    public sealed class AdvisorsReplyPayload
    {
        public string RequestId { get; set; } = string.Empty;
        public bool Ok { get; set; }
        public string? Error { get; set; }
        public string? Scope { get; set; }
        public string? Path { get; set; }
        public bool? Exists { get; set; }
        public string? Hash { get; set; }
        public AdvisorDocumentPayload? Document { get; set; }
        public List<string>? Warnings { get; set; }
        public bool? Applied { get; set; }
        public int? Active { get; set; }
        /// <summary>history: the advisors' compact transcript in this conversation (null while none runs), newest part when truncated.</summary>
        public string? History { get; set; }
        public bool? Truncated { get; set; }
    }
    public sealed class AdvisorsStatePayload
    {
        public bool Supported { get; set; }
        public string? SessionId { get; set; }
        public bool Enabled { get; set; }
        public string Status { get; set; } = "unknown";
        public List<AdvisorStatusPayload> Advisors { get; set; } = new List<AdvisorStatusPayload>();
        public List<AdvisorFilePayload> Files { get; set; } = new List<AdvisorFilePayload>();
        public List<AdvisorRosterPayload> Roster { get; set; } = new List<AdvisorRosterPayload>();
        /// <summary>Roster rows left out because the message would pass the UI hub's size (no count limit; null when none were).</summary>
        public int? RosterHidden { get; set; }
        public bool ImplicitDefault { get; set; }
        public string UpdatedUtc { get; set; } = string.Empty;
    }
    public sealed class AdvisorStatusPayload
    {
        public string Name { get; set; } = string.Empty;
        public string? Model { get; set; }
        public string Status { get; set; } = "unknown";
        public double? ContextTokens { get; set; }
        public double? ContextWindow { get; set; }
        /// <summary>Spend in US dollars, as the engine's advisor status reports it.</summary>
        public double? Cost { get; set; }
    }
    public sealed class AdvisorFilePayload
    {
        public string Scope { get; set; } = string.Empty;
        public string Path { get; set; } = string.Empty;
    }
    /// <summary>Advisor notes as the engine delivered them to the chat agent (its "advisor" custom message).</summary>
    public sealed class ChatAdvisorNotesPayload
    {
        public List<ChatAdvisorNotePayload> Notes { get; set; } = new List<ChatAdvisorNotePayload>();
    }

    public sealed class ChatAdvisorNotePayload
    {
        /// <summary>The advisor's name as its file names it ("default" for the engine's own advisor).</summary>
        public string Advisor { get; set; } = string.Empty;

        /// <summary>The engine's weight: "nit", "concern" or "blocker"; absent when it gave none.</summary>
        public string? Severity { get; set; }
        public string Note { get; set; } = string.Empty;
    }

    public sealed class AdvisorRosterPayload
    {
        public string Name { get; set; } = string.Empty;
        public string Scope { get; set; } = string.Empty;
        public bool Enabled { get; set; }
        public string? Model { get; set; }
    }

    public sealed class ProblemsStatePayload
    {
        public string Root { get; set; } = string.Empty;
        public List<ProblemItemPayload> Items { get; set; } = new List<ProblemItemPayload>();
        public bool Truncated { get; set; }
        public string UpdatedUtc { get; set; } = string.Empty;
    }

    public sealed class ProblemItemPayload
    {
        public string Id { get; set; } = string.Empty;
        public string Source { get; set; } = string.Empty;
        public string Severity { get; set; } = string.Empty;
        public string Code { get; set; } = string.Empty;
        public string Message { get; set; } = string.Empty;
        public string Path { get; set; } = string.Empty;
        public int Line { get; set; }
        public int? Column { get; set; }
        public string? Project { get; set; }

        /// <summary>An agent finding's quoted source (the evidence it reported), bounded; absent for compiler rows.</summary>
        public string? Evidence { get; set; }

        /// <summary>Who reported an agent finding: "main" (the chat's POMP) or a member's role name; absent when not known.</summary>
        public string? Reporter { get; set; }
    }

    public sealed class ProblemsCommandPayload
    {
        public string RequestId { get; set; } = string.Empty;
        public string Action { get; set; } = string.Empty;
        public string? Id { get; set; }
    }

    public sealed class ProblemsReplyPayload
    {
        public string RequestId { get; set; } = string.Empty;
        public bool Ok { get; set; }
        public string? Error { get; set; }
    }

    /// <summary>Host → UI <c>composer/prefill</c>: what the editor's POMP menu puts into the chat input.</summary>
    public sealed class ComposerPrefillPayload
    {
        /// <summary>The message text. An unsent draft in the input is kept: the text is appended to it and nothing is sent automatically.</summary>
        public string? Text { get; set; }

        /// <summary>Files added to the context basket (<c>{ path, fullPath }</c> like <c>workspace/files</c>).</summary>
        public System.Collections.Generic.List<ComposerPrefillFile>? Files { get; set; }

        /// <summary>Turns "include editor context" on (the selection or the code around the caret goes with the message).</summary>
        public bool IncludeEditorContext { get; set; }

        /// <summary>Send at once when the chat can take a message; otherwise the text stays in the input.</summary>
        public bool Send { get; set; }

        /// <summary>The one-message send target (the editor menu's "to agent or team"): the page selects it and shows the main conversation.</summary>
        public ComposerPrefillTarget? Target { get; set; }

        /// <summary>Opens the send-target picker in the main conversation (the editor menu's "show all", past the menu's 100 entries).</summary>
        public bool PickTarget { get; set; }
    }

    public sealed class ComposerPrefillFile
    {
        public string Path { get; set; } = string.Empty;

        public string FullPath { get; set; } = string.Empty;
    }

    /// <summary>A send target of <see cref="ComposerPrefillPayload"/>: an agent role name or a team id.</summary>
    public sealed class ComposerPrefillTarget
    {
        /// <summary>"agent" or "team".</summary>
        public string Kind { get; set; } = "agent";

        public string Id { get; set; } = string.Empty;
    }

    /// <summary>Host → UI <c>knowledge/draft</c>: source lines the editor offers as the first piece of a new knowledge.</summary>
    public sealed class KnowledgeDraftPayload
    {
        public string Root { get; set; } = string.Empty;

        /// <summary>Workspace-relative, '/' separated.</summary>
        public string Path { get; set; } = string.Empty;

        /// <summary>1-based, inclusive.</summary>
        public int StartLine { get; set; }

        /// <summary>1-based, inclusive.</summary>
        public int EndLine { get; set; }

        public string Text { get; set; } = string.Empty;
    }

    /// <summary>UI → host <c>permission/mode</c>: change omp's approval mode (a launch setting: the host restarts omp).</summary>
    public sealed class PermissionModeCommand
    {
        public string RequestId { get; set; } = string.Empty;

        /// <summary>alwaysAsk | write | yolo.</summary>
        public string Mode { get; set; } = string.Empty;
    }

    /// <summary>Host → UI <c>permission/modeResult</c>.</summary>
    public sealed class PermissionModeResultPayload
    {
        public string RequestId { get; set; } = string.Empty;

        public bool Ok { get; set; }

        /// <summary>The mode in effect after the answer.</summary>
        public Pomp.Protocol.OmpApprovalMode Mode { get; set; }

        public string? Error { get; set; }
    }

    /// <summary>Host → UI <c>work/open</c>: show this view of the work tab ("review" for a staged proposal).</summary>
    public sealed class WorkOpenPayload
    {
        public string View { get; set; } = "review";
    }

    /// <summary>UI → host <c>session/fast</c>: turn omp's fast mode on or off for the current model.</summary>
    public sealed class SessionFastPayload
    {
        public string? RequestId { get; set; }

        public bool Enabled { get; set; }
    }

    /// <summary>The one answer to a <c>session/fast</c>; <c>session/info</c> follows with the new state.</summary>
    public sealed class SessionFastResultPayload
    {
        public string? RequestId { get; set; }

        public bool Ok { get; set; }

        public string? Error { get; set; }

        public bool Enabled { get; set; }

        public bool Active { get; set; }
    }

    public sealed class ToolsStatePayload
    {
        public string? SessionId { get; set; }
        public List<ToolSelectionPayload> Tools { get; set; } = new List<ToolSelectionPayload>();
        public bool Pending { get; set; }
        public bool Available { get; set; }
        public string? Message { get; set; }
    }
    public sealed class ToolSelectionPayload
    {
        public string Name { get; set; } = string.Empty;
        public string Group { get; set; } = "other";
        public string? Server { get; set; }
        public string? Description { get; set; }
        public bool Active { get; set; }
        public bool DefaultActive { get; set; }
    }
    public sealed class ToolsCommandPayload
    {
        public string RequestId { get; set; } = string.Empty;
        public string Action { get; set; } = string.Empty;
        public List<string>? Active { get; set; }
    }
    public sealed class SkillsStatePayload
    {
        public List<SkillInfoPayload> Skills { get; set; } = new List<SkillInfoPayload>();
        public List<string> Diagnostics { get; set; } = new List<string>();
    }
    public sealed class SkillInfoPayload
    {
        public string Name { get; set; } = string.Empty;
        public string Description { get; set; } = string.Empty;
        public string Source { get; set; } = "omp";
        public string? File { get; set; }
        public string? Hash { get; set; }
        public bool Invocable { get; set; }
        public string? Note { get; set; }

        /// <summary>True when the file cannot be loaded into the in-page editor (over the size limit, not valid UTF-8): the page offers only open and delete.</summary>
        public bool Unreadable { get; set; }

        /// <summary>False when the skill is switched off (POMP's own overrides or the omp config's <c>disabledExtensions</c>): the engine does not offer it.</summary>
        public bool Enabled { get; set; } = true;

        /// <summary>The POMP catalog entry this skill is built in from (on <c>pomp</c> rows: the entry id).</summary>
        public string? CatalogId { get; set; }

        /// <summary>Deprecated: catalog skills are built in, never copied, so there is nothing newer; always false.</summary>
        public bool CatalogUpdate { get; set; }

        /// <summary>Built-in (<c>pomp</c>) rows only: whether POMP ships the skill switched on. Absent on every other row.</summary>
        public bool? DefaultEnabled { get; set; }

        /// <summary>Built-in (<c>pomp</c>) rows only: the catalog group key (e.g. <c>process</c>, <c>testing</c>). Absent on every other row.</summary>
        public string? Category { get; set; }
    }
    public sealed class SkillsCommandPayload
    {
        public string RequestId { get; set; } = string.Empty;
        public string Action { get; set; } = string.Empty;
        public string? Scope { get; set; }
        public string? Name { get; set; }
        public string? Description { get; set; }

        /// <summary>create/update: the Markdown body under the frontmatter (create: empty means a starter body).</summary>
        public string? Body { get; set; }
        public string? Content { get; set; }
        public string? ExpectedHash { get; set; }

        /// <summary>import: the ticket of the file the host's <c>pick</c> dialog chose (the page never names a path).</summary>
        public string? Ticket { get; set; }
    }

    /// <summary>The answer to a skills <c>pick</c>: what <c>import</c> with <see cref="Ticket"/> would copy.</summary>
    public sealed class SkillImportPreviewPayload
    {
        public string Ticket { get; set; } = string.Empty;
        public string Name { get; set; } = string.Empty;
        public string Description { get; set; } = string.Empty;

        /// <summary>The chosen file.</summary>
        public string File { get; set; } = string.Empty;

        /// <summary>Files copied: a chosen <c>SKILL.md</c> brings its whole folder, any other Markdown file only itself.</summary>
        public int Files { get; set; }
        public long Bytes { get; set; }
    }
    public sealed class ActiveRulesPayload
    {
        public string? SessionId { get; set; }
        public List<ActiveRulePayload> Rules { get; set; } = new List<ActiveRulePayload>();
    }

    public sealed class ActiveRulePayload
    {
        public string Name { get; set; } = string.Empty;
        public string File { get; set; } = string.Empty;
        public bool Always { get; set; }
        public List<string> Globs { get; set; } = new List<string>();
    }

    public sealed class RulesStatePayload
    {
        public List<RuleInfoPayload> Rules { get; set; } = new List<RuleInfoPayload>();
        public List<string> Diagnostics { get; set; } = new List<string>();
    }

    /// <summary>One top-level rule file. The engine extension decides what applies (and reports invalid YAML); this is the catalog view.</summary>
    public sealed class RuleInfoPayload
    {
        public string Name { get; set; } = string.Empty;
        public string Scope { get; set; } = "project";
        public string File { get; set; } = string.Empty;
        public string Hash { get; set; } = string.Empty;
        public string Description { get; set; } = string.Empty;
        public bool AlwaysApply { get; set; }
        public List<string> Globs { get; set; } = new List<string>();

        /// <summary>Why the catalog could not read the metadata (the engine's own parser still decides).</summary>
        public string? Note { get; set; }

        /// <summary>True when the file cannot be loaded into the in-page editor (over the size limit, not valid UTF-8): the page offers only open and delete.</summary>
        public bool Unreadable { get; set; }
    }

    public sealed class RulesCommandPayload
    {
        public string RequestId { get; set; } = string.Empty;

        /// <summary>refresh, create, import, open, delete, read or update.</summary>
        public string Action { get; set; } = string.Empty;
        public string? Scope { get; set; }
        public string? Name { get; set; }
        public string? Description { get; set; }
        public bool AlwaysApply { get; set; }
        public List<string>? Globs { get; set; }

        /// <summary>create/update: the Markdown body under the frontmatter (create: empty means a starter body).</summary>
        public string? Body { get; set; }
        public string? Content { get; set; }
        public string? ExpectedHash { get; set; }
    }

    public sealed class WorkflowsStatePayload
    {
        public List<WorkflowInfoPayload> Workflows { get; set; } = new List<WorkflowInfoPayload>();
        public List<string> Diagnostics { get; set; } = new List<string>();
    }

    /// <summary>
    /// One saved workflow file; <see cref="Note"/> says why the page cannot edit it (then it can be opened or deleted).
    /// The list message has to stay under the UI message limit: an entry marked <see cref="Summary"/> leaves out <see cref="Task"/> and
    /// <see cref="Steps"/> (the page reads them with a <c>get</c> command).
    /// </summary>
    public sealed class WorkflowInfoPayload
    {
        public string Name { get; set; } = string.Empty;
        public string Scope { get; set; } = "project";
        public string File { get; set; } = string.Empty;
        public string Hash { get; set; } = string.Empty;
        public string Description { get; set; } = string.Empty;

        /// <summary>Null (left out of the message) in a <see cref="Summary"/> entry.</summary>
        public string? Task { get; set; } = string.Empty;

        /// <summary>Null (left out of the message) in a <see cref="Summary"/> entry.</summary>
        public List<string>? Steps { get; set; } = new List<string>();

        /// <summary>How many steps the workflow has, also when <see cref="Steps"/> is left out.</summary>
        public int StepCount { get; set; }

        /// <summary>True when <see cref="Task"/> and <see cref="Steps"/> are left out of this entry; null (left out) otherwise.</summary>
        public bool? Summary { get; set; }
        public string? Note { get; set; }

        /// <summary>True when the file cannot be read at all (over the size limit, not valid UTF-8): open and delete only, no duplicate.</summary>
        public bool Unreadable { get; set; }
    }

    public sealed class WorkflowDraftPayload
    {
        public string Description { get; set; } = string.Empty;
        public string Task { get; set; } = string.Empty;
        public List<string> Steps { get; set; } = new List<string>();
    }

    public sealed class WorkflowsCommandPayload
    {
        public string RequestId { get; set; } = string.Empty;

        /// <summary>refresh, create, update, delete, open or get (the workflow with its task and steps, answered on workflows/reply).</summary>
        public string Action { get; set; } = string.Empty;
        public string? Scope { get; set; }
        public string? Name { get; set; }
        public WorkflowDraftPayload? Workflow { get; set; }
        public string? ExpectedHash { get; set; }
    }

    public sealed class HooksStatePayload
    {
        public List<HookInfoPayload> Hooks { get; set; } = new List<HookInfoPayload>();
        public List<string> Diagnostics { get; set; } = new List<string>();

        /// <summary>none, valid or invalid: hooks run only with a valid project policy.</summary>
        public string Policy { get; set; } = "none";
    }

    /// <summary>One <c>.pomp/hooks/&lt;id&gt;.md</c> file; one that does not parse has <see cref="Note"/>, an empty event and no script.</summary>
    public sealed class HookInfoPayload
    {
        public string Id { get; set; } = string.Empty;
        public string Name { get; set; } = string.Empty;
        public string File { get; set; } = string.Empty;
        public string Hash { get; set; } = string.Empty;
        public string Description { get; set; } = string.Empty;
        public string Event { get; set; } = string.Empty;
        public string Glob { get; set; } = "**";
        public int TimeoutSeconds { get; set; } = 120;
        public bool Enabled { get; set; }
        public string Script { get; set; } = string.Empty;
        public string? Note { get; set; }

        /// <summary>True when the file cannot be read at all (over the size limit, not valid UTF-8): open and delete only.</summary>
        public bool Unreadable { get; set; }
    }

    public sealed class HookDraftPayload
    {
        public string Description { get; set; } = string.Empty;
        public string Event { get; set; } = string.Empty;
        public string Glob { get; set; } = "**";
        public int TimeoutSeconds { get; set; } = 120;
        public bool Enabled { get; set; }
        public string Script { get; set; } = string.Empty;
    }

    public sealed class HooksCommandPayload
    {
        public string RequestId { get; set; } = string.Empty;

        /// <summary>refresh, create, update, setEnabled, delete or open.</summary>
        public string Action { get; set; } = string.Empty;
        public string? Id { get; set; }
        public string? Name { get; set; }
        public HookDraftPayload? Hook { get; set; }
        public bool? Enabled { get; set; }
        public string? ExpectedHash { get; set; }
    }

    public sealed class McpStatePayload
    {
        public List<McpServerPayload> Servers { get; set; } = new List<McpServerPayload>();
        public bool RestartRequired { get; set; }
        public string? Message { get; set; }
    }
    public sealed class McpServerPayload
    {
        public string Name { get; set; } = string.Empty;
        public string Scope { get; set; } = "project";
        public string Type { get; set; } = "stdio";
        public string? Command { get; set; }
        public List<string>? Args { get; set; }
        public Dictionary<string, string>? Env { get; set; }
        public string? Url { get; set; }

        /// <summary>http/sse: request headers (e.g. <c>Authorization</c>). Values are masked like <see cref="Env"/> on the way to the page.</summary>
        public Dictionary<string, string>? Headers { get; set; }
        public bool Enabled { get; set; }
        public string? Error { get; set; }
        public string? Hash { get; set; }

        /// <summary>The POMP catalog entry this server was installed from (kept in the server entry as <c>pompCatalog</c>).</summary>
        public string? CatalogId { get; set; }
    }

    /// <summary>Host → UI <c>catalog/state</c>: the MCP entries of the catalog bundled with this POMP (catalog skills are built in, switched on the skills list).</summary>
    public sealed class CatalogStatePayload
    {
        public List<CatalogEntryPayload> Entries { get; set; } = new List<CatalogEntryPayload>();
        public string? Message { get; set; }
    }

    public sealed class CatalogEntryPayload
    {
        /// <summary>Stable id, <c>[a-z0-9-]{2,64}</c>; for a skill also its folder and skill name.</summary>
        public string Id { get; set; } = string.Empty;

        /// <summary><c>skill</c> or <c>mcp</c>.</summary>
        public string Kind { get; set; } = "skill";
        public string Title { get; set; } = string.Empty;
        public string Description { get; set; } = string.Empty;

        /// <summary>Group label key (e.g. <c>process</c>, <c>design</c>, <c>dotnet</c>, <c>docs</c>, <c>browser</c>).</summary>
        public string Category { get; set; } = string.Empty;
        public string Version { get; set; } = string.Empty;

        /// <summary>MCP: publisher and license of the server package; skills written by POMP carry <c>POMP</c>.</summary>
        public string? Publisher { get; set; }
        public string? License { get; set; }

        /// <summary>Scopes the entry may be installed into (<c>project</c>, <c>user</c>); an MCP entry holding a secret is user only.</summary>
        public List<string> Scopes { get; set; } = new List<string>();

        /// <summary>Scopes it is installed in now.</summary>
        public List<string> Installed { get; set; } = new List<string>();

        /// <summary>Other catalog ids installed together with this one.</summary>
        public List<string> Requires { get; set; } = new List<string>();

        /// <summary>What it needs at run time: <c>network</c>, <c>token</c>, <c>browser</c>, <c>node</c>.</summary>
        public List<string> Needs { get; set; } = new List<string>();

        /// <summary>MCP: values the user fills in at install (e.g. a token). Secret values never come back to the page.</summary>
        public List<CatalogInputPayload> Inputs { get; set; } = new List<CatalogInputPayload>();
    }

    public sealed class CatalogInputPayload
    {
        public string Key { get; set; } = string.Empty;
        public string Label { get; set; } = string.Empty;
        public bool Secret { get; set; }
        public bool Required { get; set; }
    }

    /// <summary>UI → host <c>catalog/command</c>: <c>refresh</c>, or <c>install</c> one MCP entry (id, scope, inputs). Installs never overwrite.</summary>
    public sealed class CatalogCommandPayload
    {
        public string RequestId { get; set; } = string.Empty;
        public string Action { get; set; } = string.Empty;
        public string? Id { get; set; }
        public string? Scope { get; set; }
        public Dictionary<string, string>? Inputs { get; set; }
    }

    /// <summary>UI → host <c>preview/open</c>: open a workspace <c>.svg</c>/<c>.html</c> file in the script-free preview tab.</summary>
    public sealed class PreviewOpenPayload
    {
        public string Path { get; set; } = string.Empty;
    }
    public sealed class McpCommandPayload
    {
        public string RequestId { get; set; } = string.Empty;
        public string Action { get; set; } = string.Empty;
        public string? Scope { get; set; }
        public string? Name { get; set; }
        public JObject? Server { get; set; }
        public string? ExpectedHash { get; set; }
    }
    public sealed class McpReplyPayload
    {
        public string RequestId { get; set; } = string.Empty;
        public bool Ok { get; set; }
        public string? Error { get; set; }
        public string? Output { get; set; }
    }

    public sealed class ConversationPartnerPayload
    {
        public string Kind { get; set; } = "main";
        public string? Id { get; set; }
    }

    /// <summary>conversation/tab: <c>open</c> (partner), <c>activate</c> (sessionId; none = the Default tab) or <c>close</c> (sessionId).</summary>
    public sealed class ConversationTabCommandPayload
    {
        public string RequestId { get; set; } = string.Empty;
        public string Action { get; set; } = string.Empty;
        public ConversationPartnerPayload? Partner { get; set; }
        public string? SessionId { get; set; }
    }

    /// <summary>An open agent or team tab: its own conversation.</summary>
    public sealed class ConversationTabPayload
    {
        public string SessionId { get; set; } = string.Empty;
        public ConversationPartnerPayload Partner { get; set; } = new ConversationPartnerPayload();
        public string? Title { get; set; }
    }

    /// <summary>conversation/tabsState: the current conversation's partner and the workspace's chat tabs (docs/architecture.md §8.8).</summary>
    public sealed class ConversationTabsStatePayload
    {
        public string? SessionId { get; set; }
        public ConversationPartnerPayload Partner { get; set; } = new ConversationPartnerPayload();
        public string? Title { get; set; }
        public string? Message { get; set; }
        /// <summary>The Default (POMP) tab's conversation; null when it has none yet (activating it starts a new one).</summary>
        public string? Main { get; set; }
        public List<ConversationTabPayload> Tabs { get; set; } = new List<ConversationTabPayload>();
    }

    /// <summary>Exactly one reply to a tab close, queue or run-history command.</summary>
    public sealed class UiCommandReplyPayload
    {
        public string RequestId { get; set; } = string.Empty;
        public bool Ok { get; set; }
        public string? Error { get; set; }

        /// <summary>A skills/rules <c>read</c>: the file for the in-page editor.</summary>
        public ResourceDocumentPayload? Document { get; set; }

        /// <summary>A skills <c>pick</c> that chose a file; absent when the dialog was closed.</summary>
        public SkillImportPreviewPayload? SkillImport { get; set; }
    }

    /// <summary>A resource file read for editing; <see cref="Form"/> only when every frontmatter key is one the form edits.</summary>
    public sealed class ResourceDocumentPayload
    {
        public string Content { get; set; } = string.Empty;
        public string Hash { get; set; } = string.Empty;

        /// <summary>Skills: <c>{description, body}</c>; rules: <c>{description, alwaysApply, globs, body}</c>.</summary>
        public JObject? Form { get; set; }
    }

    public sealed class ChatQueuedPayload
    {
        public string ClientMessageId { get; set; } = string.Empty;
        public string ItemId { get; set; } = string.Empty;
    }

    public sealed class ChatQueueItemPayload
    {
        public string Id { get; set; } = string.Empty;
        public string Text { get; set; } = string.Empty;
        public List<ChatAttachment>? Attachments { get; set; }
        public bool IncludeEditorContext { get; set; }
        public string? Team { get; set; }
        public string? Agent { get; set; }
        public string? AgentInstance { get; set; }
        public string CreatedAt { get; set; } = string.Empty;

        /// <summary>The item can be steered into the running agent: it goes to this conversation itself (no chosen target) and is no slash command.</summary>
        public bool CanSteer { get; set; }
    }

    public sealed class ChatQueueStatePayload
    {
        public string? SessionId { get; set; }
        public List<ChatQueueItemPayload> Items { get; set; } = new List<ChatQueueItemPayload>();
        public bool Paused { get; set; }
        public string? Reason { get; set; }
    }

    public sealed class ChatQueueCommandPayload
    {
        public string RequestId { get; set; } = string.Empty;
        public string Action { get; set; } = string.Empty;
        public string? Id { get; set; }
        public string? Text { get; set; }
        public int? Index { get; set; }
    }

    public sealed class AgentRunTargetPayload
    {
        /// <summary>"team", "agent" or "dm".</summary>
        public string Kind { get; set; } = string.Empty;
        public string Id { get; set; } = string.Empty;
        public string Title { get; set; } = string.Empty;
    }

    public sealed class AgentRunMemberPayload
    {
        public string Agent { get; set; } = string.Empty;
        public string JobId { get; set; } = string.Empty;
        public string Status { get; set; } = string.Empty;
        public long ToolCount { get; set; }
        [JsonProperty(NullValueHandling = NullValueHandling.Include)]
        public long? Tokens { get; set; }
        [JsonProperty(NullValueHandling = NullValueHandling.Include)]
        public long? DurationMs { get; set; }
        [JsonProperty(NullValueHandling = NullValueHandling.Include)]
        public string? Result { get; set; }
    }

    public sealed class AgentRunEntryPayload
    {
        public string Id { get; set; } = string.Empty;
        public string SessionId { get; set; } = string.Empty;
        public string? PromptId { get; set; }
        public AgentRunTargetPayload Target { get; set; } = new AgentRunTargetPayload();
        public string Text { get; set; } = string.Empty;
        public string StartedAt { get; set; } = string.Empty;
        public string? EndedAt { get; set; }
        public string Status { get; set; } = "running";
        public bool Pinned { get; set; }
        public string? Label { get; set; }
        public List<AgentRunMemberPayload> Members { get; set; } = new List<AgentRunMemberPayload>();
    }

    public sealed class AgentsHistoryStatePayload
    {
        public List<AgentRunEntryPayload> Entries { get; set; } = new List<AgentRunEntryPayload>();
        public bool Truncated { get; set; }
    }

    public sealed class AgentRunHistoryFilterPayload
    {
        public string? Kind { get; set; }
        public string? Id { get; set; }
        public string? Status { get; set; }
        public bool IncludePinned { get; set; }
    }

    public sealed class AgentsHistoryCommandPayload
    {
        public string RequestId { get; set; } = string.Empty;
        public string Action { get; set; } = string.Empty;
        public string? Id { get; set; }
        public string? Label { get; set; }
        public AgentRunHistoryFilterPayload? Filter { get; set; }
    }

    public sealed class SessionNewPayload
    {
        /// <summary>Echoed in <see cref="SessionNewResultPayload.RequestId"/>.</summary>
        public string? RequestId { get; set; }
    }
}
