/**
 * UI protocol between this page and the Visual Studio host.
 * Mirrors docs/ui-protocol.md and src/Pomp.Contracts/Ui/UiProtocol.cs (camelCase JSON, enums as camelCase strings).
 * The page only ever talks to `window.chrome.webview`; it never reaches omp or Visual Studio directly.
 */

import type { ConversationHostToUi, ConversationPartner, ConversationUiToHost } from './conversationProtocol';
import type { ToolsHostToUi, ToolsUiToHost } from './toolsProtocol';

// ---- Shared enumerations -------------------------------------------------------------------

export type BridgeStateName =
  | 'stopped'
  | 'starting'
  | 'handshaking'
  | 'running'
  | 'reconnecting'
  | 'failed'
  | 'stopping';
export type ThemeKind = 'dark' | 'light';
export type Level = 'info' | 'warning' | 'error';
export type LogLevel = 'debug' | 'info' | 'warning' | 'error';
export type TurnStatus = 'completed' | 'aborted' | 'error';
export type PermissionSource = 'hostTool' | 'ompApproval';
export type PermissionKind =
  | 'fileWrite'
  | 'projectChange'
  | 'processExec'
  | 'debuggerControl'
  | 'breakpointChange'
  | 'bookmarkChange'
  | 'packageChange'
  | 'vcsWrite'
  | 'testRun'
  | 'other';
export type PermissionDecision = 'allow' | 'allowForSession' | 'alwaysAllow' | 'deny';
export type DialogMethod = 'select' | 'confirm' | 'input' | 'editor';

/** Exactly one destination for the next message; undefined means the main conversation. */
/** `main` (id '') = POMP itself, whatever the conversation partner is (page actions of the main conversation, retries). */
export type SendTarget = { kind: 'team' | 'agent' | 'instance' | 'main'; id: string };
export const MAIN_TARGET: SendTarget = { kind: 'main', id: '' };

// ---- Host -> UI payloads -------------------------------------------------------------------

export interface HostHelloPayload {
  version: string;
  vsVersion?: string;
  /** UI culture, e.g. "ko-KR". */
  locale?: string;
  includeEditorContextByDefault: boolean;
  composerPredictions: boolean;
  /** omp's approval mode in effect; absent on an older host (= alwaysAsk). Changed with `permission/mode`. */
  approvalMode?: ApprovalMode;
  /** The page's shortcuts (Tools > Options > Pomp > Shortcuts); absent keys keep {@link DEFAULT_SHORTCUTS}. */
  shortcuts?: Partial<PageShortcuts>;
}

/** omp's approval mode: alwaysAsk (reads run, writes and commands ask), write (commands ask), yolo (nothing asks except forced safety prompts). */
export type ApprovalMode = 'alwaysAsk' | 'write' | 'yolo';
export const APPROVAL_MODES: readonly ApprovalMode[] = ['alwaysAsk', 'write', 'yolo'];

/**
 * Keyboard shortcuts of the page as chord strings ("Ctrl+Alt+T": optional Ctrl/Alt/Shift and one key; '' = none), see shortcuts.ts.
 * `send` is 'Enter' (Shift+Enter inserts a line break) or 'Ctrl+Enter' (Enter inserts a line break).
 */
export interface PageShortcuts {
  send: 'Enter' | 'Ctrl+Enter';
  /** Opens the composer's one-message send-target picker. */
  sendTargetPicker: string;
  /** The previous / next agent or team of that list becomes the target (past either end: the chat's partner again). */
  previousTarget: string;
  nextTarget: string;
  /** Opens or closes the composer's context basket (arrow keys move between its files, Space checks one). */
  contextBasket: string;
  /** Turns "include editor context" on or off. */
  editorContext: string;
  /** Host-only toggles; neither submits a prompt. */
  liveFollow: string;
  liveBookmarks: string;
  /** Opens the composer's permission-mode picker. */
  permissionMode: string;
  /** Answers the pending request card: allow once / confirm. */
  allow: string;
  /** Answers the pending request card: deny / cancel. */
  deny: string;
  /** Opens Tools › Permissions. */
  permissionsView: string;
  /** Moves the focus back to the editor; the page sends `ui/toggleFocus` itself (keys inside the WebView never reach Visual Studio). */
  toggleFocus: string;
}
export const DEFAULT_SHORTCUTS: PageShortcuts = { send: 'Enter', sendTargetPicker: 'Ctrl+Alt+T', previousTarget: 'Alt+,', nextTarget: 'Alt+.', contextBasket: 'Alt+;', editorContext: "Alt+'", liveFollow: 'Alt+L', liveBookmarks: 'Alt+K', permissionMode: 'Alt+Q', allow: 'Alt+A', deny: 'Alt+D', permissionsView: 'Ctrl+Alt+P', toggleFocus: 'Alt+J' };

/** `bridge/state.reason` of a failed bridge whose cause is a missing model provider: the page shows the engine setup panel. */
export const SETUP_REQUIRED = 'setupRequired';

export interface BridgeStatePayload {
  state: BridgeStateName;
  message?: string;
  attempt?: number;
  nextRetryMs?: number;
  /** Why the bridge failed, when the page should react specifically; kept as string so unknown future reasons still render. */
  reason?: string;
}

// ---- Engine setup (accounts / login / API keys / model / usage), docs/ui-protocol.md "Engine setup" ----

export type SetupLoginStateName = 'idle' | 'running' | 'succeeded' | 'failed';

/** The running sign-in waits for this input (e.g. an authorization code to paste). Its answer is a secret. */
export interface SetupLoginPrompt {
  id: string;
  message: string;
  placeholder?: string;
}

export interface SetupLogin {
  state: SetupLoginStateName;
  provider?: string;
  message?: string;
  /** The `open_url` of the running sign-in; the host already opened it in the browser. */
  url?: string;
  instructions?: string;
  /** Latest progress text (omp `notify`). */
  progress?: string;
  prompt?: SetupLoginPrompt;
}

/** A provider omp can sign in to (about 80), from the host's auth helper (`listProviders`). */
export interface SetupProvider {
  id: string;
  name: string;
  authenticated: boolean;
  available: boolean;
}

/** A provider the host lets a key be saved for, with the environment variable omp reads it from. */
export interface SetupApiKeyProvider {
  id: string;
  label: string;
  envVar: string;
}

/** A saved key. The key itself never leaves the host. */
export interface SetupSavedKey {
  provider: string;
  envVar: string;
  savedUtc: string;
}

/** A model server the user runs (any OpenAI-compatible server), saved in the key form; its models appear under provider `id`. */
export interface SetupLocalServer {
  id: string;
  name: string;
  address: string;
  /** A key is saved for it (the key itself never leaves the host). */
  hasKey: boolean;
  savedUtc: string;
}

export interface SetupModel {
  provider: string;
  id: string;
  name?: string;
}

export type EditorModelChoice = 'auto' | 'chat' | 'model';

/** Model used by editor features, independently of the chat session. Auto resolves to omp's fast @smol model. */
export interface SetupEditorModel {
  choice: EditorModelChoice;
  model?: { provider: string; id: string };
  effective?: SetupModel;
  resolving?: boolean;
  error?: string;
}

export type UsageStatus = 'ok' | 'warning' | 'exhausted' | 'unknown';

/** One rate limit of an account (a 5 hour window, a weekly cap, ...). */
export interface UsageLimit {
  label: string;
  /** For example "5 Hour", "7 Day". */
  window?: string;
  /** ISO 8601 (UTC). */
  resetsAt?: string;
  /** 0..1 when known. */
  usedFraction?: number;
  used?: number;
  limit?: number;
  unit?: string;
  status: UsageStatus;
  /** omp scope: counted for every model of the account. */
  shared?: boolean;
  /** omp scope: the one model family it counts (opus, sonnet, fable, spark...). */
  tier?: string;
  /** omp scope: the one model id it counts. */
  modelId?: string;
}

export interface UsageReport {
  /** The subscription plan when the provider names it (e.g. Codex "prolite", "plus", "pro"). */
  plan?: string;
  provider: string;
  /** Email / account id label, when omp reports one. */
  account?: string;
  limits: UsageLimit[];
}

export interface SetupUsagePayload {
  /** When the host fetched it (ISO 8601 text, or a number of milliseconds since the epoch). */
  fetchedAt?: string | number;
  reports: UsageReport[];
  error?: string;
}

/** `setup/state` (host -> page): everything the account panel and the composer pickers show. Carries no secret. */
export interface SetupStatePayload {
  /** Echo of the `setup/command` this state answers; absent for states the host posts on its own. */
  requestId?: string;
  /** Why that command failed. */
  error?: string;
  /** From the auth helper; absent until `listProviders` was answered. */
  providers?: SetupProvider[];
  providersError?: string;
  login: SetupLogin;
  apiKeyProviders: SetupApiKeyProvider[];
  keys: SetupSavedKey[];
  localServers: SetupLocalServer[];
  models?: SetupModel[];
  modelsError?: string;
  currentModel?: { provider: string; id: string };
  editorModel?: SetupEditorModel;
  /** Levels the current model offers (get_available_thinking_levels) and the session's level. */
  thinkingLevels?: string[];
  currentThinking?: string;
  usage?: SetupUsagePayload;
  usageLoading?: boolean;
  /** Credentials changed while omp was running: it must restart to use them. */
  restartRecommended?: boolean;
}

/**
 * `setup/command` (page -> host). `key` is only ever part of `saveKey` and `value` of `answerPrompt`: both are secrets, kept
 * only in the input that produced them and never stored or logged by the page.
 */
export type SetupCommand = { requestId: string } & (
  | { action: 'refresh' | 'listProviders' | 'cancelLogin' | 'listModels' | 'listThinking' | 'listUsage' | 'resolveEditorModel' }
  | { action: 'login'; provider: string }
  | { action: 'loginConsole'; provider?: string }
  | { action: 'answerPrompt'; promptId: string; value?: string }
  | { action: 'saveKey'; provider: string; key: string }
  | { action: 'removeKey'; provider: string }
  /** Adds a local model server, or replaces the one with `id`; an empty `key` keeps its saved key. */
  | { action: 'saveLocalServer'; id?: string; name: string; address: string; key?: string }
  | { action: 'removeLocalServer'; id: string }
  /** Signs out of every login omp stored for the provider (omp has no per-account logout over RPC); POMP-saved API keys stay. */
  | { action: 'logout'; provider: string }
  | { action: 'setModel'; provider: string; modelId: string }
  | { action: 'setEditorModel'; choice: 'auto' | 'chat' }
  | { action: 'setEditorModel'; choice: 'model'; provider: string; modelId: string }
  | { action: 'setThinking'; level: string }
);

export interface ThemePayload {
  kind: ThemeKind;
  /** CSS custom properties, e.g. { "--pomp-bg": "#1e1e1e" }. */
  colors: Record<string, string>;
  fontFamily?: string;
  fontSizePx?: number;
}

export interface ModelInfo {
  contextWindow?: number;
  provider: string;
  id: string;
  name?: string;
}

export interface ContextUsage {
  tokens: number;
  contextWindow: number;
  percent: number;
  /** By source (the engine's /context breakdown, refreshed after each turn); absent until the host measured it. */
  breakdown?: ContextBreakdown;
}

/**
 * Tokens of the context window by source: system prompt, tool definitions (`systemTools` in total; `hostTools` and `mcpTools` are the
 * host's estimates of the VS and MCP shares inside it), context files (rules, AGENTS.md), the skills list and the messages.
 * `free` and `autoCompactBuffer` are the engine's figures for the rest of the window.
 */
export interface ContextBreakdown {
  systemPrompt: number;
  systemTools: number;
  hostTools: number;
  mcpTools: number;
  systemContext: number;
  skills: number;
  messages: number;
  /** The engine printed no "Messages" row: `messages` is the host's estimate (usage total minus the printed rows). */
  messagesEstimated?: boolean;
  autoCompactBuffer: number;
  free: number;
  contextWindow: number;
  measuredAt: string;
}

/** Rules currently applied by the host to this conversation. */
export interface ActiveRule { name: string; file: string; always: boolean; globs: string[] }
export interface ActiveRulesPayload { sessionId?: string; rules: ActiveRule[] }
/** Host → UI answer to `workspace/countBasketTokens`: a characters-based estimate (not the model's tokenizer). */
export interface BasketTokens { requestId: string; ok: boolean; tokens: number; editorContext: number; attachments: number; error?: string }

/** `.pomp/permissions.json` as the host enforces it; an invalid policy blocks project tools (fails closed). */
export interface ProjectPolicyRule { tool: string; effect: 'allow' | 'deny'; paths?: string[]; commands?: string[] }
/** `hash`: of the file's bytes when one exists (valid or invalid); a save or delete names it so a changed file is never overwritten. */
export interface ProjectPolicyView {
  file: string; status: 'none' | 'valid' | 'invalid'; error?: string; requireSandbox?: boolean; requireReviewedEdits?: boolean;
  rules?: ProjectPolicyRule[]; hash?: string;
}
/** What the in-page policy editor saves; the host adds `version: 1` and `default: "deny"`. */
export interface ProjectPolicyDraft { requireReviewedEdits: boolean; requireSandbox: boolean; rules: ProjectPolicyRule[] }
export interface PermissionRulesState {
  requestId?: string; root: string; sessionId: string; session: string[]; persistent: string[];
  approvalMode: string; discoveredExtensions: boolean; blockNetworkTools: boolean; error?: string; projectPolicy?: ProjectPolicyView;
  /** Workspaces whose engine configuration the user trusted (newest first); `untrust` forgets one. */
  trustedWorkspaces?: TrustedWorkspace[];
}
export interface TrustedWorkspace { root: string; trustedUtc: string; files: number }

/**
 * workspace/trustState (docs/ui-protocol.md "작업 영역 신뢰"): what omp would load from the workspace that runs code, starts processes,
 * changes approvals or pulls outside files into the conversation. `required && !trusted`: omp is not started (bridge reason
 * `workspaceTrustRequired`) until the user trusts exactly this set (`fingerprint`).
 */
export interface WorkspaceTrustState {
  requestId?: string; root: string; required: boolean; trusted: boolean; fingerprint: string; truncated?: boolean; error?: string;
  files: { path: string; kind: 'settings' | 'mcp' | 'tools' | 'lsp' | 'plugins' | 'extensions' | 'include' | string; detail?: string }[];
}
export const WORKSPACE_TRUST_REQUIRED = 'workspaceTrustRequired';

/** `project`: the workspace's `.pomp/knowledge.json` (committed); `user`: `~/.pomp/knowledge.json` (every project on this PC). */
export type KnowledgeScope = 'project' | 'user';
/** `original`: the pieces as written; `optimized`: the compact text the editor model made from them. */
export type KnowledgeUse = 'original' | 'optimized';
/** Where the lines of an anchored piece are now, as the host found them in the file on disk. */
export type KnowledgeAnchorStatus = 'current' | 'moved' | 'changed' | 'missing';
export interface KnowledgePieceAnchor { path: string; startLine: number; endLine: number; status: KnowledgeAnchorStatus }
/** One piece of text under a knowledge. Agents add only candidates; the user accepts them. */
export interface KnowledgePiece {
  id: string; text: string; source: 'user' | 'agent' | 'editor'; status: 'saved' | 'candidate'; createdAt: string;
  /** Workspace-relative path with `/` and the lines the text was taken from. */
  anchor?: KnowledgePieceAnchor;
}
/** A knowledge: a name and a color the user chose, and the pieces collected under it (docs/ui-protocol.md "Knowledge"). */
export interface KnowledgeItem {
  id: string; name: string; color: string; scope: KnowledgeScope; use: KnowledgeUse; revision: number; createdAt: string; updatedAt: string;
  /** Names of the roles and ids of the teams that always get this knowledge. */
  roles: string[]; teams: string[];
  /** Every piece is a candidate: an agent proposed this name and the user has not accepted anything yet. */
  candidate: boolean;
  /** Used in every conversation, agent and team without being selected in the basket. */
  default: boolean;
  /** `stale`: the pieces changed after the last conversion, so the original is used until it is converted again. */
  optimized?: { text: string; createdAt: string; stale: boolean };
  pieces: KnowledgePiece[];
}
/** The full knowledge list of a workspace and the conversation's selection; the answer to every `knowledge/command`. */
export interface KnowledgeState {
  requestId?: string | null; root: string; sessionId?: string | null;
  /** Ids of the knowledge selected for the current conversation. */
  selected: string[];
  /** Id of the knowledge being converted right now. */
  optimizing?: string | null;
  items: KnowledgeItem[]; error?: string;
}
/** The editor's "Add to Knowledge ▸ New": lines of a file that become the first piece of a new knowledge. */
export interface KnowledgeDraft { root: string; path: string; startLine: number; endLine: number; text: string }
export interface KnowledgeFields { name: string; color: string; scope: KnowledgeScope }
export interface KnowledgePieceInput { text: string; anchor?: { path: string; startLine: number; endLine: number } }
/** What the page asks of the knowledge store; the host answers each with a full `knowledge/state` carrying the same `requestId`. */
export type KnowledgeCommand =
  | { action: 'refresh' }
  | { action: 'create'; knowledge: KnowledgeFields; piece?: KnowledgePieceInput }
  | { action: 'update'; id: string; revision: number; knowledge: KnowledgeFields & { use: KnowledgeUse } }
  | { action: 'delete'; id: string; revision: number }
  | { action: 'setDefault'; id: string; revision: number; on: boolean }
  | { action: 'addPiece'; id: string; revision: number; piece: KnowledgePieceInput }
  | { action: 'updatePiece'; id: string; revision: number; pieceId: string; text: string }
  | { action: 'deletePiece'; id: string; revision: number; pieceId: string }
  | { action: 'acceptPiece'; id: string; revision: number; pieceId: string }
  | { action: 'optimize'; id: string; revision: number }
  | { action: 'assign'; kind: 'role' | 'team'; name: string; ids: string[] }
  | { action: 'select'; ids: string[] };
/** An event rule between agents and teams (AgentHookStore); forwarding also needs the per-conversation switch. */
export interface AgentHookRule {
  id: string; name: string; enabled: boolean;
  event: 'agent.completed' | 'agent.failed' | 'team.completed' | 'team.failed' | 'feedback.waiting' | 'ide.saved' | 'ide.build.succeeded' | 'ide.build.failed';
  sourceKind: 'any' | 'agent' | 'team'; sourceId?: string;
  /** ide.saved only: workspace-relative glob, default `**` (docs/architecture.md §8.10). */
  glob?: string;
  action: 'notify' | 'forward'; targetKind?: 'main' | 'agent' | 'team'; targetId?: string;
  message: string; revision: number;
}
export interface AgentHooksState {
  requestId?: string | null; root: string; sessionId?: string | null; autoForward: boolean; forwards: number; maxForwards: number;
  log: { at: string; rule: string; event: string; source: string; outcome: string }[]; rules: AgentHookRule[]; error?: string;
}
export interface AgentPlanTask { content: string; status: string; blocker?: string }
export interface AgentPlanPhase { name: string; tasks: AgentPlanTask[] }
/** One page of the whole plan; phase/task are indexes into the whole plan. */
export interface AgentPlanPage {
  requestId: string; ok: boolean; error?: string; revision?: string; editable?: boolean; page: number; size: number; total: number;
  phases: { index: number; name: string; taskCount: number }[];
  rows: { phase: number; task: number; content: string; status: string; blocker?: string }[];
}
export type AgentPlanEdit = { requestId: string; sessionId: string; revision: string } & (
  | { op: 'addTask'; phase: number; content: string; status?: string; blocker?: string }
  | { op: 'updateTask'; phase: number; task: number; content?: string; status?: string; blocker?: string }
  | { op: 'deleteTask'; phase: number; task: number }
  | { op: 'addPhase'; name: string; content: string; status?: string }
  | { op: 'renamePhase'; phase: number; name: string }
  | { op: 'deletePhase'; phase: number });
/** Answer to workspace/review editorChanges (the candidate files) and fromEditor (staging). */
export interface EditorChangesReply { requestId: string; action: 'editorChanges' | 'fromEditor'; ok: boolean; error?: string; files?: { path: string; unsaved: boolean }[] }

/** Host-only editor settings; receiving this payload advertises support (independent of engine readiness). */
export interface EditorFollowState {
  live: boolean;
  bookmarks: boolean;
  requestId?: string;
  error?: string;
}

export interface SessionInfoPayload {
  recoveryStatus?: 'ready' | 'restoring' | 'failed';
  recoveryMessage?: string;
  todoPhases?: AgentPlanPhase[];
  planTruncated?: boolean;
  /** Every task of the plan (todoPhases holds at most the first 200), the open ones, and the hash a page or edit names. */
  planTaskCount?: number; planOpenCount?: number; planRevision?: string;
  hasPendingAsyncWork?: boolean;
  model?: ModelInfo;
  thinkingLevel?: string;
  sessionId?: string;
  sessionName?: string;
  contextUsage?: ContextUsage;
  threeTurnCompaction?: boolean;
  isStreaming: boolean;
  workingDirectory?: string;
  /** omp's fast mode (priority service tier); absent before the first engine state. */
  fastMode?: FastMode;
}

export interface FastMode {
  /** The current model's provider has a priority tier to switch. */
  available: boolean;
  /** Requested for the model's family. */
  enabled: boolean;
  /** In effect for the next request. */
  active: boolean;
}

/** Which omp engine runs: the one the POMP package ships and pins (verified), or another one (options path, installed omp). */
/**
 * Host → UI: the code editor's POMP menu fills the chat input. Files go into the context basket, `includeEditorContext` switches the editor
 * context on, `text` fills an empty input (an unsent draft is kept and the text appended, never sent) and `send` sends it at once when
 * the chat can take a message.
 */
export interface ComposerPrefill {
  text?: string;
  files?: WorkspaceFile[];
  includeEditorContext?: boolean;
  send?: boolean;
  /** The one-message send target (the editor menu's "to agent or team"): selected, with the main conversation shown. */
  target?: { kind: 'agent' | 'team'; id: string };
  /** Opens the send-target picker in the main conversation (the editor menu's "show all"). */
  pickTarget?: boolean;
}

/** Host → UI: show a view of the work tab (the host sends it after an action started outside the page, e.g. an inline edit). */
export interface WorkOpen {
  view: string;
}

export interface EngineInfo {
  source: 'bundled' | 'settings' | 'installed';
  /** Known for the bundled engine (its manifest) and, after `omp --version`, for others. */
  version?: string;
  /** Exactly the engine this POMP release was tested with (size and SHA-256 checked). */
  verified: boolean;
  /** The omp version this POMP release ships; absent in a development package without an engine. */
  expectedVersion?: string;
  /** Path of an engine that is not the bundled one. */
  path?: string;
}

/** The one answer to a `session/fast`; `session/info` follows with the new state. */
export interface SessionFastResult {
  requestId?: string;
  ok: boolean;
  error?: string;
  enabled: boolean;
  active: boolean;
}

export interface TurnStartedPayload {
  promptId: string;
  clientMessageId?: string;
  /** Original user input, without injected editor context; used when rebuilding a replayed bubble. */
  text?: string;
  attachments?: { path: string }[];
  /** Active editor reference actually included in this turn's prompt, also restored on replay. */
  editorContext?: { path: string; startLine?: number; endLine?: number; caretLine?: number };
  team?: string | null;
  agent?: string | null;
  agentInstance?: string | null;
  /** POMP itself answered: a retry keeps that destination after a partner change. */
  main?: boolean | null;
  /** The agent/team tab a message to another target was sent from (also restored from the engine's transcript): keeps it in that tab. */
  from?: { kind: string; id?: string | null } | null;
}

export interface WorkspaceFile {
  kind?: string; symbol?: string; line?: number;
  /** Workspace-relative display path, or the full path for a file outside the workspace. */
  path: string;
  fullPath: string;
}

export interface WorkspaceFilesPayload {
  requestId: string;
  files: WorkspaceFile[];
  truncated: boolean;
  error?: string;
}

export interface AssistantStartPayload {
  messageId: string;
}

export interface AssistantDeltaPayload {
  messageId: string;
  kind: 'text' | 'thinking';
  delta: string;
}

/** The thinking just streamed for `messageId` was narration (prose addressed to the user, omp 18.12+): `text`, the end of that thinking, moves to the answer text. */
export interface AssistantNarrationPayload {
  messageId: string;
  text: string;
}

export interface AssistantEndPayload {
  messageId: string;
  stopReason?: string;
  errorMessage?: string;
}

export interface ToolStartPayload {
  toolCallId: string;
  toolName: string;
  args?: Record<string, unknown>;
  isHostTool: boolean;
}

export interface ToolUpdatePayload {
  toolCallId: string;
  /** Progress text; replaces the previous one. */
  text: string;
}

export interface ToolEndPayload {
  toolCallId: string;
  toolName: string;
  isError: boolean;
  /** Already truncated by the host. */
  resultText?: string;
  durationMs?: number;
}

export interface UiErrorInfo {
  code: string;
  message: string;
  retryable: boolean;
}

export interface TurnEndPayload {
  promptId: string;
  status: TurnStatus;
  error?: UiErrorInfo;
}

export interface NoticePayload {
  level: Level;
  message: string;
  /** 'pre': command output laid out in columns, shown as a monospace block. */
  format?: 'pre';
}

export interface PermissionRequestPayload {
  requestId: string;
  source: PermissionSource;
  /** One of {@link PermissionKind}; kept as string so unknown future kinds still render. */
  kind: string;
  toolName: string;
  title: string;
  message?: string;
  /** Complete assignments for a task-tool approval; the engine message may contain only a preview. */
  tasks?: { agent: string; task: string }[];
  target?: string;
  /** Unified diff preview. */
  diff?: string;
  timeoutMs?: number;
  /**
   * False when the host will not remember an answer (omp's forced safety prompts): the dialog then offers only
   * "allow once" and "deny". Absent means true.
   */
  canRemember?: boolean;
}

/** Host → UI answer to `permission/mode`. */
export interface PermissionModeResult { requestId: string; ok: boolean; mode: ApprovalMode; error?: string }

export interface DismissPayload {
  requestId: string;
  reason?: string;
}

export type SessionNewStatus = 'completed' | 'failed' | 'cancelled';

/** The one answer to a `session/new`. The page clears its transcript only on "completed". */
export interface SessionNewResultPayload {
  /** Echo of the pending page request; absent/null for a legacy host or a Visual Studio command. Unmatched ids are ignored. */
  requestId?: string | null;
  status: SessionNewStatus;
  /** Why it did not complete (failed / cancelled); shown to the user as is. */
  message?: string;
}

export interface DialogRequestPayload {
  requestId: string;
  method: DialogMethod;
  title?: string;
  message?: string;
  options?: string[];
  placeholder?: string;
  defaultValue?: string;
  timeoutMs?: number;
}


export interface CodeReviewFinding { id: string; origin: string; severity: string; code: string; message: string; path: string; line: number; sourceHash: string; evidence: string; acknowledged: boolean }
export interface CodeReviewHistoryEntry { id: string; root: string; createdUtc: string; findings: number; available: boolean }
export interface CodeReviewPriorRun { id: string; root: string; createdUtc: string; findings: CodeReviewFinding[]; notices: string[] }
export interface WorkspaceProblem {
  id: string; source: 'agent' | 'compiler'; severity: 'error' | 'warning'; code: string;
  message: string; path: string; line: number; column?: number; project?: string;
  /** An agent finding's quoted source. */
  evidence?: string;
  /** Who reported an agent finding: "main" (the chat's POMP) or a member's role name. */
  reporter?: string;
}
export interface WorkspaceProblemsState { root: string; items: WorkspaceProblem[]; truncated: boolean; updatedUtc: string }
export interface CodeReviewState { id: string; root: string; status: string; message: string; pageError?: string; autoReview: boolean; findings: CodeReviewFinding[]; notices: string[]; history: CodeReviewHistoryEntry[]; viewedId: string; prior: CodeReviewPriorRun | null }

export interface TerminalState {
  selectedId?: string; paused: boolean; warning?: string;
  jobs: { id: string; origin: { sessionId: string; toolCallId?: string }; root: string; command: string; state: string; background: boolean; output: string; truncated: boolean; error?: string; exitCode?: number | null; createdUtc?: string; startedUtc?: string }[];
}

export interface WorkspaceHistoryState {
  root?: string; message?: string; error?: string;
  /** The page the host sent (newest first): matching records, page index, page size and the path query. */
  total?: number; page?: number; size?: number; query?: string;
  entries?: { id: string; createdUtc?: string; state: string; files: string[]; error?: string }[];
}

export interface WorkspaceReviewState {
  nativeDiffAvailable?: boolean;
  id: string; revision: number; status: string; message: string; canApply: boolean;
  /** editor: made by the user from their own edits in VS (apply keeps the selected changes). */
  origin?: 'agent' | 'editor';
  files: { path: string; coarse: boolean; hunks: { id: number; oldLine: number; newLine: number; before: string; after: string; selected: boolean }[] }[];
  diagnostics: { message: string; isError: boolean; path?: string; line?: number }[];
  outcomes: { path: string; state: string; error?: string }[];
}

/** Results of isolated team members (docs/architecture.md §8.11): files change only when one is applied after approval. */
export interface IsolationResult {
  id: string; root: string; agent: string; job: string;
  status: 'pending' | 'blocked' | 'applying' | 'applied' | 'failed';
  message: string; createdAt: string; patchPath: string;
  files: { path: string; kind: 'create' | 'modify' | 'delete'; added: number; removed: number }[];
}
/** `hidden`: results left out because the message would pass the UI hub's size limit (the newest are sent). */
export interface IsolationResultsState { requestId?: string | null; items: IsolationResult[]; hidden?: number }

// ---- Slash commands and workspace sessions -------------------------------------------------

export interface CommandInfo {
  name: string;
  /** Where the engine got it from: builtin, extension, skill, file, custom or mcp_prompt. */
  source?: string;
  description?: string;
  hint?: string;
  subcommands?: { name: string; description?: string }[];
}

/** Compatibility name used by the command picker. Names do not include the leading slash. */
export type SlashCommand = CommandInfo;
/** `hidden`: commands the host left out because the message would pass its size (no count limit). */
export interface CommandsState { commands: SlashCommand[]; hidden?: number }

export interface SessionEntry {
  /** Opaque host handle, not a file path supplied by the page. */
  id: string;
  sessionId?: string;
  title: string;
  name?: string;
  modifiedUtc?: string;
  visitedUtc?: string;
  messageCount?: number;
  current?: boolean;
  partner?: ConversationPartner;
}

export interface SessionHistoryMessage { role: 'user' | 'assistant'; text: string; truncated?: boolean }

export interface SessionHistoryState {
  requestId?: string;
  error?: string;
  root: string;
  current?: { sessionId: string; name?: string };
  entries: SessionEntry[];
  /** Conversations the host left out because the message would pass its size (no count limit). */
  hidden?: number;
  messages?: SessionHistoryMessage[];
  preview?: { id: string; messages: SessionHistoryMessage[]; truncated: boolean };
  nextCursor?: string;
  totalMessages?: number;
  /** Session whose message preview this response contains; it need not be the current session. */
  sessionId?: string;
}

export type SessionHistoryCommand = { requestId: string } & (
  | { action: 'refresh' }
  | { action: 'switch'; root: string; id: string }
  | { action: 'messages'; sessionId: string; cursor?: string }
  | { action: 'preview'; id: string }
  | { action: 'delete'; id: string }
  | { action: 'rename'; name: string }
  | { action: 'compact'; instructions?: string }
);

function protocolRecord(value: unknown): Record<string, unknown> {
  return typeof value === 'object' && value !== null && !Array.isArray(value) ? value as Record<string, unknown> : {};
}

/** IDs are never shortened: a shortened handle could refer to a different session or request. */
function protocolId(value: unknown, limit = 4096): string | undefined {
  return typeof value === 'string' && value.trim().length > 0 && value.length <= limit && !/[\x00-\x1f\x7f]/.test(value) ? value : undefined;
}

function protocolText(value: unknown, limit: number): string | undefined {
  return typeof value === 'string' && value.trim().length > 0 ? value.slice(0, limit) : undefined;
}

function protocolCount(value: unknown): number | undefined {
  return typeof value === 'number' && Number.isSafeInteger(value) && value >= 0 ? value : undefined;
}

/** Bounds and copies command data field by field; malformed or duplicate command names are ignored. */
export function normalizeCommandsState(payload: unknown): CommandsState {
  const raw = protocolRecord(payload);
  const commands: SlashCommand[] = [];
  const seen = new Set<string>();
  const commandName = (value: unknown): string | undefined => {
    const name = protocolId(value, 128);
    return name && !name.startsWith('/') && !/\s/.test(name) ? name : undefined;
  };
  if (Array.isArray(raw.commands)) {
    for (const value of raw.commands) {
      const row = protocolRecord(value);
      const name = commandName(row.name);
      if (!name || seen.has(name)) continue;
      seen.add(name);
      const command: SlashCommand = { name };
      if (typeof row.source === 'string' && /^[a-z_]{1,20}$/.test(row.source)) command.source = row.source;
      const description = protocolText(row.description, 1000);
      const hint = protocolText(row.hint, 1000);
      if (description !== undefined) command.description = description;
      if (hint !== undefined) command.hint = hint;
      if (Array.isArray(row.subcommands)) {
        command.subcommands = [];
        const subSeen = new Set<string>();
        for (const value of row.subcommands) {
          const sub = protocolRecord(value);
          const subName = commandName(sub.name);
          if (!subName || subSeen.has(subName)) continue;
          subSeen.add(subName);
          const subDescription = protocolText(sub.description, 1000);
          command.subcommands.push({ name: subName, ...(subDescription !== undefined ? { description: subDescription } : {}) });
        }
      }
      commands.push(command);
    }
  }
  const hidden = typeof raw.hidden === 'number' && Number.isSafeInteger(raw.hidden) && raw.hidden > 0 ? raw.hidden : undefined;
  return { commands, ...(hidden ? { hidden } : {}) };
}

function normalizeHistoryMessages(value: unknown): SessionHistoryMessage[] {
  const messages: SessionHistoryMessage[] = [];
  if (!Array.isArray(value)) return messages;
  for (const item of value.slice(0, 40)) {
    const row = protocolRecord(item);
    if ((row.role !== 'user' && row.role !== 'assistant') || typeof row.text !== 'string') continue;
    const message: SessionHistoryMessage = { role: row.role, text: row.text.slice(0, 16_000) };
    if (typeof row.truncated === 'boolean') message.truncated = row.truncated;
    if (row.text.length > 16_000) message.truncated = true;
    messages.push(message);
  }
  return messages;
}

/** Accepts the legacy recent-session rows too, deriving a title when the host did not send one. */
export function normalizeSessionHistory(payload: unknown): SessionHistoryState {
  const raw = protocolRecord(payload);
  const state: SessionHistoryState = { root: protocolId(raw.root) ?? '', entries: [] };
  const requestId = protocolId(raw.requestId, 256);
  const error = protocolText(raw.error, 4000);
  const sessionId = protocolId(raw.sessionId);
  const nextCursor = protocolId(raw.nextCursor);
  const totalMessages = protocolCount(raw.totalMessages);
  if (requestId !== undefined) state.requestId = requestId;
  if (error !== undefined) state.error = error;
  if (sessionId !== undefined) state.sessionId = sessionId;
  if (nextCursor !== undefined) state.nextCursor = nextCursor;
  if (totalMessages !== undefined) state.totalMessages = totalMessages;
  const hidden = protocolCount(raw.hidden);
  if (hidden) state.hidden = hidden;
  const current = protocolRecord(raw.current);
  const currentId = protocolId(current.sessionId);
  if (currentId !== undefined) {
    const name = protocolText(current.name, 120);
    state.current = { sessionId: currentId, ...(name !== undefined ? { name } : {}) };
  }
  const seen = new Set<string>();
  if (Array.isArray(raw.entries)) {
    for (const value of raw.entries) {
      const row = protocolRecord(value);
      const id = protocolId(row.id);
      if (!id || seen.has(id)) continue;
      seen.add(id);
      const name = protocolText(row.name, 120);
      const modifiedUtc = protocolId(row.modifiedUtc, 64);
      const visitedUtc = protocolId(row.visitedUtc, 64);
      const entry: SessionEntry = {
        id,
        title: protocolText(row.title, 120) ?? name ?? (modifiedUtc ?? visitedUtc)?.slice(0, 10) ?? id.slice(0, 120),
      };
      const entrySessionId = protocolId(row.sessionId);
      const messageCount = protocolCount(row.messageCount);
      if (entrySessionId !== undefined) entry.sessionId = entrySessionId;
      if (name !== undefined) entry.name = name;
      if (modifiedUtc !== undefined) entry.modifiedUtc = modifiedUtc;
      if (visitedUtc !== undefined) entry.visitedUtc = visitedUtc;
      if (messageCount !== undefined) entry.messageCount = messageCount;
      if (typeof row.current === 'boolean') entry.current = row.current;
      const partner = protocolRecord(row.partner);
      if (partner.kind === 'main' || partner.kind === 'agent' || partner.kind === 'team') {
        const partnerId = protocolId(partner.id, 256);
        entry.partner = { kind: partner.kind, ...(partnerId ? { id: partnerId } : {}) };
      }
      state.entries.push(entry);
    }
  }
  if (Array.isArray(raw.messages)) state.messages = normalizeHistoryMessages(raw.messages);
  const preview = protocolRecord(raw.preview);
  const previewId = protocolId(preview.id);
  if (previewId !== undefined && Array.isArray(preview.messages)) {
    state.preview = {
      id: previewId,
      messages: normalizeHistoryMessages(preview.messages),
      truncated: preview.truncated === true || preview.messages.length > 40,
    };
  }
  return state;
}
export interface BackgroundAgentJobProgress {
  toolCount?: number;
  tokens?: number | null;
  cost?: number | null;
  durationMs?: number | null;
  recentTools?: string[];
  resolvedModel?: string;
  thinkingLevel?: string;
}

export interface BackgroundAgentJob {
  id: string;
  agent: string;
  description: string;
  status: string;
  result?: string | null;
  lastUpdate?: number;
  control?: { generation: string; state: 'running' | 'pauseRequested'; cooperative: boolean };
  parentToolCallId?: string;
  promptId?: string;
  team?: string;
  progress?: BackgroundAgentJobProgress;
}

export interface BackgroundAgentState { root: string; sessionId: string; state: string; message: string;
  controlVersion?: number; controlPending?: boolean; controlRequestId?: string; controlMessage?: string;
  /** The engine offers POMP's stop-one-member command (docs/architecture.md §8.12). */
  stopAvailable?: boolean;
  jobs: BackgroundAgentJob[]; }

// ---- Agents & Teams (docs/architecture.md §8) ----------------------------------------------------

export type RoleScope = 'project' | 'user' | 'bundled' | 'extension';
export type RoleTools = 'all' | 'readOnly' | 'custom';

export interface Role {
  name: string;
  title: string;
  job: string;
  description: string;
  instructions: string;
  model?: string;
  thinkingLevel?: string;
  tools: RoleTools;
  scope: RoleScope;
  file?: string;
  hash?: string;
  editable: boolean;
  error?: string;
  /** Usable by the engine, but a file format POMP cannot safely rewrite. */
  note?: string;
  /** Authoritative when present; older hosts infer precedence from usable roles. */
  shadowed?: boolean;
  /** The picture the user gave the role; absent = its first letter in a circle of its own color. */
  avatar?: RoleAvatar | null;
}

export type AvatarShape = 'circle' | 'rounded' | 'square' | 'hexagon';
/** A role's picture as the agents state carries it; the image itself comes on demand (`avatarImages`), by revision. */
export interface RoleAvatar {
  shape: AvatarShape;
  /** #rrggbb; absent = the role's own color. */
  color?: string;
  /** Set when the picture has an image: changes with every change of the picture. */
  imageRev?: number;
}

export type TeamScope = 'project' | 'user';
export type TeamMode = 'lead' | 'parallel' | 'pipeline';

export interface Team {
  id: string;
  title: string;
  goal: string;
  members: string[];
  mode: TeamMode;
  instructions: string;
  membersOnly: boolean;
  /** Members work in isolated copies; their changes come back as review proposals (docs/architecture.md §8.11). */
  isolation?: boolean;
  scope: TeamScope;
  file: string;
  hash: string;
  missing: string[];
  error?: string;
  /** Hidden by the project team with the same case-insensitive ID. */
  shadowed?: boolean;
}

export interface AgentsStatePayload {
  root: string;
  roles: Role[];
  teams: Team[];
  diagnostics: string[];
  /** Roles and teams the host left out of the message (too large to send them all). */
  rolesHidden?: number;
  teamsHidden?: number;
  /** POMP's own role, the chat agent itself (older hosts send none). */
  main?: MainRole;
}

/** The chat agent as a role: its picture, and which APPEND_SYSTEM.md the engine appends to it (`scope`; null = none). */
export interface MainRole {
  avatar?: RoleAvatar | null;
  scope?: 'project' | 'user' | null;
  /** The running engine started with other instructions than the files hold now: they apply after a restart. */
  restartRequired?: boolean;
}

/** One APPEND_SYSTEM.md as `mainRole` reads it: `hash` is what `saveMain` names as `expectedHash`. */
export interface MainInstructionsFile { exists: boolean; text: string; hash?: string }

export interface AgentsCommand {
  requestId: string;
  /** openRole: open a project/user role file (scope + name) in the VS editor, for a role the form cannot edit.
   *  builtinRole: the form fields of the built-in role `name` as the engine defines it (reply `role`).
   *  setAvatar: the picture of role `name` in `scope` (`avatar`; null = back to its letter in a circle).
   *  avatarImages: the images of `roles` (reply `images`, keyed "scope:name").
   *  mainRole: POMP's own role instructions, the project's and the user's (reply `main`).
   *  saveMain: writes `instructions` to the APPEND_SYSTEM.md of `scope` (blank removes it), refused when its hash is not `expectedHash`. */
  action: 'refresh' | 'saveRole' | 'deleteRole' | 'saveTeam' | 'deleteTeam' | 'transcript' | 'exportTeam' | 'openTeamExport' | 'previewTeamImport' | 'importTeam' | 'openRole' | 'builtinRole' | 'setAvatar' | 'avatarImages' | 'mainRole' | 'saveMain';
  scope?: 'project' | 'user';
  /** saveRole with `builtin: true`: an edited built-in role; its new file starts from the engine's definition of it. */
  role?: Partial<Role> & { builtin?: boolean };
  team?: Partial<Team>;
  name?: string;
  expectedHash?: string;
  subagentId?: string;
  /** Team template text for previewTeamImport / importTeam. */
  text?: string;
  /** The preview's hash: importTeam is refused when the files changed since. */
  previewHash?: string;
  /** setAvatar: no `image` keeps the stored image, `image: null` removes it. */
  avatar?: { shape: AvatarShape; color?: string; image?: string | null } | null;
  roles?: { scope: string; name: string }[];
  /** saveMain: the instructions text. */
  instructions?: string;
}

/** How a team template would land in the chosen scope (docs/architecture.md §8.13). */
export interface TeamImportPreview {
  hash: string;
  team: { id: string; title: string; mode: TeamMode; members: string[]; status: 'new' | 'same' | 'conflict' };
  roles: { name: string; title: string; status: 'new' | 'same' | 'conflict' | 'bundled' | 'existing' }[];
  problems: string[];
}

export interface AgentsReplyPayload {
  requestId: string;
  ok: boolean;
  error?: string;
  name?: string;
  transcript?: {
    agent: string;
    messages: { role: string; text: string }[];
    truncated: boolean;
  };
  /** exportTeam: the pomp-team-template JSON text. */
  template?: string;
  preview?: TeamImportPreview;
  /** builtinRole: the built-in role's form fields. */
  role?: { description: string; instructions: string; model?: string; thinkingLevel?: string; tools: RoleTools };
  /** mainRole: the instructions files (no `project` without a workspace) and the scope the engine takes. */
  main?: { scope?: 'project' | 'user' | null; project?: MainInstructionsFile; user: MainInstructionsFile };
  /** avatarImages: data URLs keyed "scope:name"; a role without an image is left out. */
  images?: Record<string, string>;
}

// ---- Advisors -----------------------------------------------------------------------------

export type AdvisorScope = 'project' | 'user';
export type AdvisorStatus = 'off' | 'no-model' | 'active' | 'paused' | 'unknown';
export interface AdvisorEntry {
  name: string;
  enabled?: boolean;
  model?: string;
  reviewMode?: 'turn' | 'agent-end';
  reviewInterval?: number;
  syncBacklog?: 'off' | '1' | '3' | '5' | 'strict';
  /** Absent = default tools; an empty list = none. */
  tools?: string[];
  instructions?: string;
  maxNotesPerUpdate?: number;
}
export interface AdvisorDocument { instructions?: string; maxNotesPerUpdate?: number; advisors: AdvisorEntry[] }
export interface AdvisorsCommand {
  requestId: string;
  /** select: turns advisor `name` on or off (`on`) in this conversation only; history: the notes (and `name`'s transcript);
   *  openHistory: `name`'s transcript in an editor tab. */
  action: 'load' | 'save' | 'enable' | 'disable' | 'status' | 'history' | 'open' | 'select' | 'openHistory';
  scope?: AdvisorScope;
  name?: string;
  on?: boolean;
  document?: AdvisorDocument;
  baseHash?: string;
  /** open: an exact path from the latest discovered files snapshot. */
  path?: string;
}
export interface AdvisorsReplyPayload {
  requestId: string; ok: boolean; error?: string; scope?: AdvisorScope; path?: string; exists?: boolean; hash?: string;
  document?: AdvisorDocument; warnings?: string[]; applied?: boolean; active?: number;
  /** history: the compact transcript of this conversation's advisors (null while none runs), its newest part when truncated. */
  history?: string | null; truncated?: boolean;
  /** history: every note the advisors gave in this conversation, oldest first. */
  notes?: AdvisorNote[];
}
/** A note an advisor gave the chat agent; `severity` is the engine's "nit", "concern" or "blocker". */
export interface AdvisorNote { advisor: string; severity?: string; note: string; at?: string }
export interface AdvisorsStatePayload {
  supported: boolean; sessionId?: string; enabled: boolean; status: AdvisorStatus;
  /** contextTokens, contextWindow and cost (US dollars): the figures the engine's advisor status reports. */
  advisors: { name: string; model?: string; status: string; contextTokens?: number | null; contextWindow?: number | null; cost?: number | null }[];
  files: { scope: AdvisorScope; path: string }[];
  roster: { name: string; scope: AdvisorScope; enabled: boolean; model?: string }[];
  /** Roster rows the host left out because the message would pass its size (no count limit). */
  rosterHidden?: number;
  implicitDefault: boolean; updatedUtc: string;
}

// ---- Computer use (docs/architecture.md §8.5, §8.7) -----------------------------------------------

export interface ComputerStatePayload {
  policy: 'off' | 'perConversation';
  browser: boolean;
  confirmReads: boolean;
  desktop: boolean;
  pending: boolean;
  available: boolean;
  message?: string;
}

export interface ComputerSetCommand {
  requestId: string;
  desktop: boolean;
}

export interface ComputerReplyPayload {
  requestId: string;
  ok: boolean;
  error?: string;
}
export interface ProjectHookState { root: string; sessionId: string; enabled: boolean; running: boolean; attempts: number; skipped: number; message: string; history: { name: string; event: string; path: string; status: string; createdUtc: string; jobId?: string; error?: string }[] }
export interface ComposerWorkflowState {
  root: string; sessionId: string; id: string; revision: number; phase: string; paused: boolean;
  task: string; steps: string[]; message: string; proposalId: string;
  outcomes: { path: string; state: string }[]; discoveryObserved: boolean; applied: boolean; verificationObserved: boolean; controlRequestId?: string; controlError?: string | null;
}
export interface InteractiveTerminalState {
  root: string; sessionId: string; enabled: boolean; supported: boolean; connected: boolean; uncertain?: boolean; warning?: string; requestId?: string;
  state: string; terminalId?: string; generation?: string; shell?: string; command?: string; output?: string;
  outputStart?: number; totalCharacters?: number; sequence?: number; truncated?: boolean; exitCode?: number; error?: string;
}
/** `tokenBudget`/`tokensUsed`: only while a token budget is set — the conversation's model tokens since enabling (docs/ui-protocol.md "Automatic continuation"). */
export interface ComposerAutoState { root: string; sessionId: string; id: string; enabled: boolean; remaining: number; dispatching: boolean; message: string; requestId?: string; tokenBudget?: number; tokensUsed?: number }
/** Answer to `workspace/goal` (docs/ui-protocol.md "Goal runs"): `started` once the engine took the goal's first message, `refused` with the reason in `error`. */
export interface GoalState { requestId: string; ok: boolean; stage: 'started' | 'cancelled' | 'refused'; error?: string | null }
/** The model asked for a goal run (`request_goal_run`): the page shows the same confirmation card as a typed /goal. */
export interface GoalRequest { requestId: string; root: string; sessionId: string; text: string; tokenBudget?: number }
export interface CaseFeedbackSelection { caseId: string; selected: boolean; comment: string; extra: string }
export interface CaseFeedbackSummary {
  proposalId: string; sessionId: string; target: { kind: string; id: string; title: string };
  revision: number; caseCount: number; status: 'draft' | 'submitted' | 'dispatching' | 'accepted'; createdAt: string;
}
export interface CaseFeedbackProposal extends CaseFeedbackSummary {
  /** `image`: a workspace-relative image path (fetched with feedback/image); `diagram`: Mermaid source. */
  cases: { caseId: string; title: string; bodyMarkdown: string; image?: string; diagram?: string }[];
  draft: CaseFeedbackSelection[]; draftRevision: number;
}
export interface CaseFeedbackState {
  requestId?: string; root: string; sessionId: string; entries: CaseFeedbackSummary[];
  proposal?: CaseFeedbackProposal | null; error?: string; notice?: string;
}
export interface HostToUi extends ConversationHostToUi, ToolsHostToUi {
  'feedback/state': CaseFeedbackState;
  'feedback/imageResult': { requestId: string; proposalId: string; caseId: string; dataUrl?: string; error?: string };
  'workspace/composerAutoState': ComposerAutoState;
  'workspace/goalState': GoalState;
  'workspace/goalRequest': GoalRequest;
  'workspace/interactiveState': InteractiveTerminalState;
  'workspace/snippetResult': { requestId: string; staged?: boolean; path?: string; error?: string };
  'workspace/hooksState': ProjectHookState;
  'workspace/backgroundState': BackgroundAgentState;
  'commands/state': CommandsState;
  'session/historyState': SessionHistoryState;
  'workspace/reviewState': WorkspaceReviewState;
  'workspace/isolationState': IsolationResultsState;
  'workspace/historyState': WorkspaceHistoryState;
  'workspace/historyReply': { requestId?: string; root?: string; proposalId?: string; ok: boolean; error?: string; deleted?: number };
  'workspace/editorChanges': EditorChangesReply;
  'workspace/terminalState': TerminalState;
  'workspace/terminalTrace': { requestId: string; id: string; root?: string; state?: string; truncated?: boolean; error?: string; frames: { atMs: number; text: string }[] };
  'workspace/problemsState': WorkspaceProblemsState;
  'workspace/problemsReply': { requestId: string; ok: boolean; error?: string };
  'workspace/codeReviewState': CodeReviewState;
  'host/hello': HostHelloPayload;
  'host/editorFollow': EditorFollowState;
  'bridge/state': BridgeStatePayload;
  'setup/state': SetupStatePayload;
  'theme/changed': ThemePayload;
  'session/info': SessionInfoPayload;
  'permission/rulesState': PermissionRulesState;
  'permission/modeResult': PermissionModeResult;
  'workspace/basketTokens': BasketTokens;
  'rules/active': ActiveRulesPayload;
  'workspace/trustState': WorkspaceTrustState;
  'session/newResult': SessionNewResultPayload;
  'session/fastResult': SessionFastResult;
  'engine/info': EngineInfo;
  'work/open': WorkOpen;
  'composer/prefill': ComposerPrefill;
  'composer/prediction': { requestId: string; text?: string; error?: string };
  'chat/turnStarted': TurnStartedPayload;
  'chat/assistantStart': AssistantStartPayload;
  'chat/assistantDelta': AssistantDeltaPayload;
  'chat/assistantNarration': AssistantNarrationPayload;
  'chat/assistantEnd': AssistantEndPayload;
  'chat/toolStart': ToolStartPayload;
  'chat/toolUpdate': ToolUpdatePayload;
  'chat/toolEnd': ToolEndPayload;
  'chat/turnEnd': TurnEndPayload;
  'chat/notice': NoticePayload;
  'chat/advisorNotes': { notes: AdvisorNote[] };
  'chat/replayBegin': { truncated: boolean };
  'chat/replayEnd': Record<string, never>;
  'workspace/files': WorkspaceFilesPayload;
  'workspace/composerState': ComposerWorkflowState;
  'workspace/agentPlanResult': { requestId: string; ok: boolean; error?: string };
  'workspace/agentPlanPageResult': AgentPlanPage;
  'knowledge/state': KnowledgeState;
  'knowledge/draft': KnowledgeDraft;
  'agentHooks/state': AgentHooksState;
  'workspace/tokenCount': { requestId: string; root?: string; modelId?: string; provider?: string; tokens?: number; estimated?: boolean; method?: 'catalog-native' | 'fallback-native' | 'byte-upperbound' | 'byte-estimate' | 'empty'; encoding?: string | null; error?: string };
  'agents/state': AgentsStatePayload;
  'agents/reply': AgentsReplyPayload;
  'advisors/state': AdvisorsStatePayload;
  'advisors/reply': AdvisorsReplyPayload;
  'computer/state': ComputerStatePayload;
  'computer/reply': ComputerReplyPayload;
  'permission/request': PermissionRequestPayload;
  'permission/dismiss': DismissPayload;
  'dialog/request': DialogRequestPayload;
  'dialog/dismiss': DismissPayload;
  error: UiErrorInfo;
}

export type HostMessageType = keyof HostToUi;

/** A message from the host; `type` discriminates the payload. */
export type HostMessage = {
  [K in HostMessageType]: { type: K; id?: string; payload: HostToUi[K] };
}[HostMessageType];

// ---- UI -> Host payloads -------------------------------------------------------------------

type Empty = Record<string, never>;

/** UI -> Host message types with their payloads. */
export interface UiToHost extends ConversationUiToHost, ToolsUiToHost {
  'composer/predict': { requestId: string; sessionId: string; text: string };
  'feedback/image': { requestId: string; root: string; sessionId: string; proposalId: string; caseId: string };
  'feedback/command': { action: 'refresh' | 'open' | 'draft' | 'submit' | 'delete'; requestId: string; root: string; sessionId?: string; proposalId?: string; revision?: number; draftRevision?: number; selections?: CaseFeedbackSelection[] };
  'workspace/snippet': { requestId: string; root: string; sessionId: string; code: string };
  /** deleteRun: remove one previous run (runId) from the history; never the run being shown as latest. */
  'workspace/problems': { requestId: string; action: 'refresh' | 'open' | 'dismiss'; id?: string };
  'workspace/codeReview': { action: 'refresh' | 'scan' | 'cancel' | 'auto' | 'open' | 'acknowledge' | 'openRun' | 'deleteRun'; reviewId?: string; findingId?: string; runId?: string; enabled?: boolean; value?: boolean };
  /** `tokenBudget` (enable only): optional whole number of tokens, 10,000 to 50,000,000; the continuation also ends when the conversation has used that many since enabling. */
  'workspace/composerAuto': { action: 'status' | 'enable' | 'disable'; requestId?: string; root?: string; sessionId?: string; id?: string; revision?: number; tokenBudget?: number };
  /** start: the goal card was confirmed (the host starts the workflow command and arms automatic continuation); cancel: it was declined. `fromRequest`: the `workspace/goalRequest` the card came from. */
  'workspace/goal': { requestId: string; action: 'start' | 'cancel'; root?: string; sessionId?: string; text?: string; tokenBudget?: number; fromRequest?: string };
  'workspace/interactive': { action: 'status' | 'start' | 'input' | 'resize' | 'stop'; requestId?: string; root?: string; sessionId?: string; terminalId?: string; generation?: string; command?: string; data?: string; cols?: number; rows?: number; timeoutSeconds?: number };
  /** clear: drop finished jobs from the list; cancelAll: cancel every unfinished job. */
  'workspace/terminal': { action: 'refresh' | 'pause' | 'resume' | 'select' | 'cancel' | 'trace' | 'clear' | 'cancelAll'; id?: string; requestId?: string };
  'workspace/hooks': { action: 'refresh' | 'enable' | 'cancel'; root?: string; sessionId?: string; enabled?: boolean };
  /** Open a workspace .svg/.html file in the script-free preview document tab (docs/ui-protocol.md, preview). Errors arrive as a chat notice. */
  'preview/open': { path: string };
  'workspace/background': { action: 'refresh' | 'pause' | 'resume' | 'cancel' | 'stop'; requestId?: string; root?: string; sessionId?: string; id?: string; generation?: string };
  'session/history': SessionHistoryCommand;
  /** delete: remove one finished checkpoint record (id); a prepared (in-flight) record is refused. */
  'workspace/history': { action: 'refresh' | 'restore' | 'delete'; root?: string; id?: string; ids?: string[]; requestId?: string; page?: number; size?: number; query?: string };
  'workspace/review': { action: 'refresh' | 'select' | 'amend' | 'nativeDiff' | 'validate' | 'apply' | 'discard' | 'editorChanges' | 'fromEditor'; requestId?: string; paths?: string[]; hunkId?: number; after?: string; allowSourceGenerators?: boolean; id?: string; revision?: number; path?: string; selected?: number[] };
  'workspace/isolation': { action: 'refresh' | 'apply' | 'discard' | 'openPatch'; requestId: string; root?: string; id?: string };
  'ui/ready': Empty;
  'ui/editorFollow': { requestId: string; action: 'set'; live?: boolean; bookmarks?: boolean };
  'ui/log': { level: LogLevel; message: string };
  'agents/command': AgentsCommand;
  'advisors/command': AdvisorsCommand;
  'computer/set': ComputerSetCommand;
  'chat/send': {
    clientMessageId: string;
    text: string;
    includeEditorContext: boolean;
    attachments?: { path: string }[];
    team?: string;
    agent?: string;
    agentInstance?: string;
    /** POMP itself answers, whatever the conversation partner is; never combined with the targets above. */
    main?: true;
    /** The agent/team tab it is sent from, only when it goes to a different target: that tab keeps it after a restore. View only. */
    from?: { kind: 'agent' | 'team'; id: string };
  };
  'chat/stop': Empty;
  /** start: begin a new run from a saved workflow's task/steps (no active run; unpaused; sends no model request — the page sends the chat message). */
  'workspace/composer': { action: 'refresh' | 'pause' | 'resume' | 'cancel' | 'save' | 'start'; requestId: string; root?: string; sessionId?: string; id?: string; revision?: number; task?: string; steps?: string[] };
  'workspace/agentPlan': { requestId: string; sessionId: string; base: AgentPlanPhase[]; phases: AgentPlanPhase[] };
  'workspace/agentPlanPage': { requestId: string; sessionId: string; page: number; size: number; query?: string };
  'workspace/agentPlanEdit': AgentPlanEdit;
  'knowledge/command': { requestId: string; root: string } & KnowledgeCommand;
  'agentHooks/command': { requestId: string; root: string; sessionId: string; action: 'refresh' | 'save' | 'delete' | 'autoForward'; id?: string; revision?: number; rule?: Omit<AgentHookRule, 'id' | 'revision'>; enabled?: boolean };
  /** The basket's token count: the draft, the attachment list as a send writes it and the conversation's knowledge; answered by `workspace/tokenCount`. */
  'workspace/countDraftTokens': { requestId: string; root: string; sessionId: string; modelId: string; provider: string; text: string; attachments?: { path: string }[] };
  'workspace/findFiles': { requestId: string; query: string; limit?: number };
  /** What the next message adds beyond its text (the editor context when on, the attachment list); answered by `workspace/basketTokens`. */
  'workspace/countBasketTokens': { requestId: string; root: string; sessionId?: string; includeEditorContext: boolean; attachments?: { path: string }[] };
  /** Host-chrome: move the focus from the page to the editor (the VS host handles it; never reaches the session). */
  'ui/toggleFocus': Record<string, never>;
  /** Host-chrome: a short notice (a control action that failed, a draft warning) shown in the VS status bar, not inside the page. */
  'ui/status': { text: string; level: 'error' | 'warning' | 'info' };
  /** Files or an image were pasted into the chat input: the host reads the clipboard and puts them into the context basket (composer/prefill). */
  'ui/pasteFiles': Record<string, never>;
  /** Files dropped on the page: sent with `postMessageWithAdditionalObjects`, the files as additional objects (only VS reads their paths). */
  'ui/dropFiles': Record<string, never>;
  'workspace/pickFiles': { requestId: string };
  'permission/respond': { requestId: string; decision: PermissionDecision; note?: string };
  /** The composer's permission-mode chip; answered by `permission/modeResult`, and a changed mode comes back in a new `host/hello` (omp restarts). */
  'permission/mode': { requestId: string; mode: ApprovalMode };
  'dialog/respond': { requestId: string; value?: string; confirmed?: boolean; cancelled?: boolean };
  /** `requestId` comes back in `session/newResult`. */
  'session/new': { requestId: string };
  'session/recover': Record<string, never>;
  /** savePolicy: write `policy` (expectedHash null = the file must not exist yet); deletePolicy: remove the file (expectedHash required). */
  'permission/rules': { requestId: string; action: 'refresh' | 'revoke' | 'openPolicy' | 'createPolicy' | 'savePolicy' | 'deletePolicy' | 'untrust'; root?: string; sessionId?: string; scope?: 'session' | 'persistent'; key?: string; policy?: ProjectPolicyDraft; expectedHash?: string | null; workspace?: string };
  /** refresh (re-scan) | trust (the fingerprint shown; omp then starts) | open (a listed file in VS); answered by workspace/trustState. */
  'workspace/trust': { requestId: string; action: 'refresh' | 'trust' | 'open'; root?: string; fingerprint?: string; path?: string };
  /** Turn omp's fast mode on or off; `requestId` comes back in `session/fastResult`. */
  'session/fast': { requestId: string; enabled: boolean };
  'bridge/restart': Empty;
  'log/open': Empty;
  'settings/open': Empty;
  'setup/command': SetupCommand;
}

export type UiMessageType = keyof UiToHost;

// ---- Typed bridge to WebView2 --------------------------------------------------------------

interface WebViewHost {
  postMessage(message: unknown): void;
  /** WebView2 Runtime 113+: hands DOM objects (a dropped `FileList`) to the host along with the message. */
  postMessageWithAdditionalObjects?(message: unknown, additionalObjects: ArrayLike<unknown>): void;
  addEventListener(type: 'message', listener: (event: { data: unknown }) => void): void;
  removeEventListener(type: 'message', listener: (event: { data: unknown }) => void): void;
}

declare global {
  interface Window {
    chrome?: { webview?: WebViewHost };
  }
}

/** Every known host message type; anything else is ignored (forward compatibility). */
const HOST_TYPES: Record<HostMessageType, true> = {
  'tools/state': true, 'tools/reply': true,
  'skills/state': true, 'skills/reply': true,
  'mcp/state': true, 'mcp/reply': true,
  'catalog/state': true, 'catalog/reply': true,
  'rules/state': true, 'rules/reply': true, 'rules/active': true,
  'workflows/state': true, 'workflows/reply': true,
  'hooks/state': true, 'hooks/reply': true,
  'conversation/partnerState': true,
  'conversation/partnerReply': true,
  'chat/queued': true,
  'chat/queueState': true,
  'chat/queueReply': true,
  'agents/historyState': true,
  'agents/historyReply': true,
  'workspace/reviewState': true,
  'workspace/isolationState': true,
  'workspace/snippetResult': true,
  'workspace/hooksState': true,
  'workspace/backgroundState': true,
  'commands/state': true,
  'session/historyState': true,
  'workspace/historyState': true,
  'workspace/historyReply': true,
  'workspace/terminalState': true,
  'workspace/interactiveState': true,
  'workspace/composerAutoState': true,
  'workspace/goalState': true,
  'workspace/goalRequest': true,
  'feedback/state': true,
  'feedback/imageResult': true,
  'workspace/terminalTrace': true,
  'workspace/problemsState': true,
  'workspace/problemsReply': true,
  'workspace/codeReviewState': true,
  'host/hello': true,
  'host/editorFollow': true,
  'bridge/state': true,
  'setup/state': true,
  'theme/changed': true,
  'session/info': true,
  'permission/rulesState': true,
  'permission/modeResult': true,
  'workspace/basketTokens': true,
  'workspace/trustState': true,
  'session/newResult': true,
  'session/fastResult': true,
  'engine/info': true,
  'work/open': true,
  'composer/prefill': true,
  'composer/prediction': true,
  'chat/turnStarted': true,
  'chat/assistantStart': true,
  'chat/assistantDelta': true,
  'chat/assistantNarration': true,
  'chat/assistantEnd': true,
  'chat/toolStart': true,
  'chat/toolUpdate': true,
  'chat/toolEnd': true,
  'chat/turnEnd': true,
  'chat/notice': true,
  'chat/advisorNotes': true,
  'agents/state': true,
  'agents/reply': true,
  'advisors/state': true,
  'advisors/reply': true,
  'computer/state': true,
  'computer/reply': true,
  'chat/replayBegin': true,
  'chat/replayEnd': true,
  'workspace/files': true,
  'workspace/composerState': true,
  'workspace/agentPlanResult': true,
  'workspace/agentPlanPageResult': true,
  'workspace/editorChanges': true,
  'knowledge/state': true,
  'knowledge/draft': true,
  'agentHooks/state': true,
  'workspace/tokenCount': true,
  'permission/request': true,
  'permission/dismiss': true,
  'dialog/request': true,
  'dialog/dismiss': true,
  error: true,
};

/** True when running inside WebView2 (or when the dev mock host has installed itself). */
export function hasHost(): boolean {
  return window.chrome?.webview !== undefined;
}

/** Turns a raw WebView2 message into a {@link HostMessage}, or null when it is malformed or of an unknown type. */
function normalize(data: unknown): HostMessage | null {
  let value = data;
  if (typeof value === 'string') {
    try {
      value = JSON.parse(value) as unknown;
    } catch {
      return null;
    }
  }
  if (typeof value !== 'object' || value === null) return null;
  const { type, id, payload } = value as { type?: unknown; id?: unknown; payload?: unknown };
  if (typeof type !== 'string' || !Object.hasOwn(HOST_TYPES, type)) return null;
  if (type === 'commands/state') return { type, id: protocolId(id, 256), payload: normalizeCommandsState(payload) };
  if (type === 'session/historyState') return { type, id: protocolId(id, 256), payload: normalizeSessionHistory(payload) };
  return {
    type,
    id: typeof id === 'string' ? id : undefined,
    payload: typeof payload === 'object' && payload !== null ? payload : {},
  } as HostMessage;
}

/** Subscribes to host messages; returns the unsubscribe function. Unknown message types are dropped. */
export function onMessage(handler: (message: HostMessage) => void): () => void {
  const webview = window.chrome?.webview;
  if (!webview) return () => {};
  const listener = (event: { data: unknown }): void => {
    const message = normalize(event.data);
    if (message) handler(message);
  };
  webview.addEventListener('message', listener);
  return () => webview.removeEventListener('message', listener);
}

/**
 * Posts a message to the host. Returns false when nothing was handed over: no host (plain browser without the dev mock) or
 * `postMessage` threw. Callers that change local state on the assumption that the host got the message check it.
 */
export function send<K extends UiMessageType>(type: K, payload?: UiToHost[K]): boolean {
  const webview = window.chrome?.webview;
  if (!webview) return false;
  try {
    webview.postMessage({ type, payload: payload ?? {} });
    return true;
  } catch (error) {
    console.error('[pomp] postMessage failed', type, error);
    return false;
  }
}

/** Page-side diagnostics into the host log (`ui/log`). */
export function logToHost(level: UiToHost['ui/log']['level'], message: string): void {
  send('ui/log', { level, message: message.length > 4000 ? message.slice(0, 4000) : message });
}
