import { addAttachments, dismissMention, emptyComposer, fileFromAttachment, fileIdentity, receiveFiles, selectMention, updateMention, withoutMentions, type ComposerState } from './composerState';
import { localeFromHost, makeTranslate, type Locale } from './i18n';
import { dialogTitle, permissionTitle } from './requestTitle';
import type { AgentHistoryState, CommandReply, PartnerState, QueueState } from './conversationProtocol';
import { normalizeSetup, recordReply, type SetupReply, type SetupState } from './setup';
import type {
  AdvisorNote,
  ActiveRule,
  AgentsReplyPayload,
  AgentsStatePayload,
  ComposerPrefill,
  ComputerReplyPayload,
  ComputerStatePayload,
  BridgeStatePayload,
  CommandsState,
  DialogRequestPayload,
  EngineInfo,
  HostHelloPayload,
  HostMessage,
  KnowledgeDraft,
  Level,
  BasketTokens,
  PermissionModeResult,
  PermissionRequestPayload,
  SessionInfoPayload,
  SendTarget,
  TurnStartedPayload,
  UiErrorInfo,
  WorkspaceFile,
} from './protocol';

// ---- Transcript items (rendered in arrival order) ------------------------------------------

export interface UserItem {
  kind: 'user';
  key: string;
  /** Id this page put into `chat/send` (the host echoes it in `chat/turnStarted`). */
  clientMessageId: string;
  text: string;
  /** Page-selected or host-replayed destination badge. */
  target?: SendTarget;
  attachments?: WorkspaceFile[];
  /** Editor snapshot the host prepended to the composed prompt. */
  editorContext?: TurnStartedPayload['editorContext'];
  /** Set once the host reported `chat/turnStarted` for this message. */
  promptId?: string;
  /** Set when the host ended this message's turn without it becoming the running turn (e.g. rejected as busy). */
  failed?: UiErrorInfo;
  /** The partner tab (`agent:name`/`team:id`) it was sent from: keeps it in that tab before the host names its destination. View only. */
  partnerKey?: string;
}

export interface AssistantItem {
  kind: 'assistant';
  key: string;
  messageId: string;
  text: string;
  thinking: string;
  done: boolean;
  stopReason?: string;
  errorMessage?: string;
  /** The text / thinking reached {@link MAX_ASSISTANT_CHARS}: later deltas of that kind were dropped and the bubble says so. */
  textTruncated?: boolean;
  thinkingTruncated?: boolean;
}

/** The most text (and, separately, thinking) one assistant message keeps, in UTF-16 units: a runaway reply must not grow the page without bound. The `msg.truncated` notice names this number. */
export const MAX_ASSISTANT_CHARS = 2_000_000;

export interface ToolItem {
  kind: 'tool';
  key: string;
  toolCallId: string;
  toolName: string;
  args?: Record<string, unknown>;
  isHostTool: boolean;
  progress?: string;
  resultText?: string;
  isError: boolean;
  durationMs?: number;
  state: 'running' | 'done' | 'interrupted';
}

export interface NoticeItem {
  kind: 'notice';
  key: string;
  level: Level;
  message: string;
  format?: 'pre';
}

export interface TurnErrorItem {
  kind: 'turnError';
  key: string;
  error: UiErrorInfo;
  /** The turn that failed, and the user message it belongs to (absent when this page never saw that message). */
  promptId?: string;
  clientMessageId?: string;
  /** Text of that user message: what "Retry" resends. Absent when it cannot be resolved (Retry is then disabled). */
  retryText?: string;
  retryAttachments?: WorkspaceFile[];
  /** The failed message's destination; undefined means this conversation, never the current draft target. */
  retryTarget?: SendTarget;
}

export interface AbortedItem {
  kind: 'aborted';
  key: string;
}

/** How a permission request or question ended; 'handled' = the host closed it without an answer from this page. */
export type RequestOutcome = 'allow' | 'deny' | 'always' | 'session' | 'expired' | 'cancelled' | 'answered' | 'handled';

/** Compact record of a permission request or question that left the queue; local to this page (not part of a transcript replay). */
export interface RequestItem {
  kind: 'request';
  key: string;
  title: string;
  source?: 'permission' | 'dialog';
  outcome: RequestOutcome;
  at: number;
}

/** Notes advisors gave the chat agent, where they came in the conversation. */
export interface AdvisorNotesItem {
  kind: 'advisor';
  key: string;
  notes: AdvisorNote[];
}

/** Boundary in the transcript: omp continues in a new session, the messages above are not part of its context. */
export interface SessionResetItem {
  kind: 'sessionReset';
  key: string;
}

export type TranscriptItem =
  | UserItem
  | AssistantItem
  | ToolItem
  | NoticeItem
  | TurnErrorItem
  | AbortedItem
  | RequestItem
  | SessionResetItem
  | AdvisorNotesItem;

// ---- State -----------------------------------------------------------------------------------

/**
 * Turn started from this UI. idle: none; sending: message posted, waiting for `chat/turnStarted`; running: turn in progress.
 * Agent runs that were not started by `chat/send` (omp waking up for a background job) are not turns; they show up through
 * `session.isStreaming` and are folded in by {@link isBusy}.
 */
export type TurnPhase = 'idle' | 'sending' | 'running';

export type PendingPermission = PermissionRequestPayload & { receivedAt: number };
export type PendingDialog = DialogRequestPayload & { receivedAt: number; draft?: string };

export interface AppState {
  locale: Locale;
  hello: HostHelloPayload | null;
  /** Which omp engine runs (`engine/info`); null until the host has started one. */
  engine: EngineInfo | null;
  /** The latest `work/open` from the host; `seq` makes a repeated request for the same view count again. */
  workRequest: { view: string; seq: number } | null;
  /** The latest `composer/prefill` (bounded); `seq` makes every request count, also a repeated one. */
  prefill: { payload: ComposerPrefill; seq: number } | null;
  /** The latest `knowledge/draft` (lines of a file on their way into a new knowledge, bounded); `seq` makes every request count. */
  knowledgeDraft: { payload: KnowledgeDraft; seq: number } | null;
  bridge: BridgeStatePayload & { since: number };
  session: SessionInfoPayload | null;
  items: TranscriptItem[];
  composer: ComposerState;
  /** Locally handed-off sends not yet matched to turnStarted; survives transcript replay independently of phase. */
  pendingSends: UserItem[];
  /** Deliberate busy sends stay outside the transcript until the host starts them. */
  pendingQueued: UserItem[];
  /** A rejected queue send is restored to the draft, without creating a chat bubble. */
  sendError?: string;
  replaying: boolean;
  replayTruncated: boolean;
  /** Increments at every replayEnd, even when begin/end were coalesced into one render. */
  replayRevision: number;
  /** Counter for generated item keys (keeps the reducer pure). */
  seq: number;
  phase: TurnPhase;
  /**
   * The turn that owns `phase`: the first `chat/turnStarted` while no turn is open. Only its `chat/turnEnd` ends the phase and
   * finalizes in-flight items; any other prompt (rejected as busy, ...) only affects its own user message.
   */
  activePromptId: string | undefined;
  /**
   * `requestId` of the `session/new` this page sent and the host has not answered yet. The transcript is kept until the host
   * reports "completed" (omp may refuse or an extension may cancel); Send and New session stay disabled meanwhile.
   */
  pendingNewSession: string | undefined;
  permissions: PendingPermission[];
  dialogs: PendingDialog[];
  /** Requests answered since the queue was last empty: drives the "2 / 3" counter of queued dialogs. */
  permissionDone: number;
  dialogDone: number;
  includeEditorContext: boolean;
  /** True once the user toggled the checkbox, so a later hello does not override the choice. */
  contextChosen: boolean;
  /**
   * Latest `setup/state` (engine setup: login, API keys, model). It holds no secret, and an API key never enters this state:
   * the key lives only in the setup panel's input and in the one `setup/command` that carries it.
   */
  setup: SetupState | null;
  /** Answers to this page's `setup/command`s (newest last, bounded): the error that belongs to the command with that id. */
  setupReplies: SetupReply[];
  /** Latest bounded command catalog from omp; no secret input is stored here. */
  commands: CommandsState;
  /** Agents and teams state (roles, teams, diagnostics). */
  agents: AgentsStatePayload | null;
  /** Answers to agents/command (saved, deleted, transcript). */
  agentsReplies: AgentsReplyPayload[];
  /** Computer use state (policy, available, desktop on/off, message). */
  computer: ComputerStatePayload | null;
  /** Latest computer/reply. */
  computerReply: ComputerReplyPayload | null;
  /** Latest permission/modeResult (the composer's permission-mode chip). */
  permissionModeReply: PermissionModeResult | null;
  /** Latest workspace/basketTokens (what the next message adds: editor context, attachment list; an estimate). */
  basketTokens: BasketTokens | null;
  activeRules: ActiveRule[];
  /** Chosen destination for the next message (cleared only after successful delivery). */
  selectedTarget?: SendTarget;
  partner: PartnerState | null;
  queue: QueueState;
  agentHistory: AgentHistoryState;
  conversationReplies: CommandReply[];
}

export const initialState: AppState = {
  locale: 'ko',
  hello: null,
  engine: null,
  workRequest: null,
  prefill: null,
  knowledgeDraft: null,
  bridge: { state: 'starting', since: 0 },
  session: null,
  items: [],
  composer: emptyComposer,
  pendingSends: [],
  pendingQueued: [],
  replaying: false,
  replayTruncated: false,
  replayRevision: 0,
  seq: 0,
  phase: 'idle',
  activePromptId: undefined,
  pendingNewSession: undefined,
  permissions: [],
  dialogs: [],
  permissionDone: 0,
  dialogDone: 0,
  includeEditorContext: true,
  contextChosen: false,
  setup: null,
  setupReplies: [],
  commands: { commands: [] },
  agents: null,
  agentsReplies: [],
  computer: null,
  computerReply: null,
  permissionModeReply: null,
  basketTokens: null,
  activeRules: [],
  selectedTarget: undefined,
  partner: null,
  queue: { items: [], paused: false },
  agentHistory: { entries: [], truncated: false },
  conversationReplies: [],
};

/** A host message stamped with the local receive time (used for countdowns). */
export interface Stamped {
  msg: HostMessage;
  at: number;
}

export type Action =
  | { type: 'host'; batch: Stamped[] }
  | { type: 'userSend'; id: string; text: string; attachments?: WorkspaceFile[]; clearDraft?: boolean; target?: SendTarget; preserveDraftTarget?: boolean; queued?: boolean; partnerKey?: string }
  | { type: 'queueSendUnconfirmed'; id: string }
  | { type: 'recoverQueueSend'; id: string }
  | { type: 'composerEdit'; text: string; caret: number; selectionEnd: number }
  | { type: 'composerCaret'; caret: number; selectionEnd: number }
  | { type: 'composerComposition'; composing: boolean }
  | { type: 'mentionRequested'; requestId: string; start: number; query: string }
  | { type: 'mentionMove'; offset: number; count?: number }
  | { type: 'mentionDismiss' }
  | { type: 'mentionSelect'; file: WorkspaceFile }
  | { type: 'pickerRequested'; requestId: string }
  | { type: 'workspaceFailed'; requestId: string; message: string }
  | { type: 'attachmentsAdd'; files: WorkspaceFile[] }
  | { type: 'attachmentToggle'; fullPath: string }
  | { type: 'attachmentReorder'; fullPath: string; beforePath?: string }
  | { type: 'attachmentsReplace'; files: WorkspaceFile[] }
  | { type: 'attachmentRemove'; fullPath: string }
  | { type: 'dialogDraft'; requestId: string; text: string }
  /** `session/new` was posted: wait for the host's `session/newResult` before touching the transcript. */
  | { type: 'newSessionRequested'; requestId: string }
  /** A notice created by the page itself (nothing reached the host). */
  | { type: 'localNotice'; level: Level; message: string }
  | { type: 'permissionAnswered'; requestId: string; outcome: RequestOutcome; at: number }
  | { type: 'dialogAnswered'; requestId: string; outcome: RequestOutcome; at: number }
  | { type: 'selectTarget'; target?: SendTarget }
  | { type: 'setIncludeEditorContext'; value: boolean };

/**
 * A turn of this UI is in progress, or omp reports a running agent (a reloaded page, a background-job wake-up).
 * Derived on purpose: `session.isStreaming` is never latched into `phase`, so it can not leave the composer stuck.
 */
export const isBusy = (state: AppState): boolean => state.replaying || state.pendingSends.length > 0 || state.phase !== 'idle' || state.session?.isStreaming === true;

// ---- Helpers ---------------------------------------------------------------------------------

function replaceAt(items: TranscriptItem[], index: number, item: TranscriptItem): TranscriptItem[] {
  const next = items.slice();
  next[index] = item;
  return next;
}

/** Index of the last item matching, searching from the end (streaming targets are near the tail). */
function findLastIndex(items: TranscriptItem[], predicate: (item: TranscriptItem) => boolean): number {
  for (let i = items.length - 1; i >= 0; i--) if (predicate(items[i]!)) return i;
  return -1;
}

// Host ids may repeat across turns (or after an omp restart), so events only ever target the *open* item with that id.
const findOpenAssistant = (items: TranscriptItem[], messageId: string): number =>
  findLastIndex(items, (it) => it.kind === 'assistant' && !it.done && it.messageId === messageId);

const findRunningTool = (items: TranscriptItem[], toolCallId: string): number =>
  findLastIndex(items, (it) => it.kind === 'tool' && it.state === 'running' && it.toolCallId === toolCallId);

/**
 * The card a `chat/toolEnd` completes: still running, or already marked interrupted because the turn/bridge ended first
 * (omp exit: the host reports the tool end after the turn end on older hosts). Completing it in place avoids a duplicate card.
 */
const findUnfinishedTool = (items: TranscriptItem[], toolCallId: string): number =>
  findLastIndex(items, (it) => it.kind === 'tool' && it.state !== 'done' && it.toolCallId === toolCallId);

/** The newest user message the host announced as prompt `promptId`. */
const findUserByPrompt = (items: TranscriptItem[], promptId: string): number =>
  findLastIndex(items, (it) => it.kind === 'user' && it.promptId === promptId);

const newAssistant = (key: string, messageId: string): AssistantItem => ({
  kind: 'assistant',
  key,
  messageId,
  text: '',
  thinking: '',
  done: false,
});

/** Ends streaming messages and running tools whose turn is over (or whose omp session vanished). */
function finalizeInFlight(items: TranscriptItem[]): TranscriptItem[] {
  let changed = false;
  const next = items.map((item): TranscriptItem => {
    if (item.kind === 'assistant' && !item.done) {
      changed = true;
      return { ...item, done: true };
    }
    if (item.kind === 'tool' && item.state === 'running') {
      changed = true;
      return { ...item, state: 'interrupted' };
    }
    return item;
  });
  return changed ? next : items;
}

/** Removes a request from a queue; the "done" counter restarts once the queue is empty. */
function removeRequest<T extends { requestId: string }>(
  queue: T[],
  done: number,
  requestId: string,
): { queue: T[]; done: number } {
  const next = queue.filter((request) => request.requestId !== requestId);
  if (next.length === queue.length) return { queue, done };
  return { queue: next, done: next.length === 0 ? 0 : done + 1 };
}

/** Appends a generated item (notice, banner, ...) and bumps the key counter. */
function withItem(state: AppState, build: (key: string) => TranscriptItem, items = state.items): AppState {
  return { ...state, items: [...items, build(`n:${state.seq}`)], seq: state.seq + 1 };
}

/**
 * What a completed new session clears: the transcript, the turn in progress and every question that waits for an answer.
 * The old session id is forgotten as well, so the `session/info` that follows (the new id) is not reported as a session
 * change against messages the user may already have typed into the new session.
 */
function clearedForNewSession(state: AppState): AppState {
  return {
    ...state,
    items: [],
    pendingSends: [],
    pendingQueued: [],
    sendError: undefined,
    partner: null,
    replaying: false,
    replayTruncated: false,
    phase: 'idle',
    activePromptId: undefined,
    session: state.session ? { ...state.session, sessionId: undefined } : state.session,
    selectedTarget: undefined,
    activeRules: [],
    permissions: [],
    dialogs: [],
    permissionDone: 0,
    dialogDone: 0,
  };
}

// ---- Reducer ---------------------------------------------------------------------------------

function applyHost(state: AppState, msg: HostMessage, at: number): AppState {
  switch (msg.type) {
    case 'composer/prediction': return state;
    case 'rules/active': {
      const payload = msg.payload;
      if (payload.sessionId && state.session?.sessionId && payload.sessionId !== state.session.sessionId) return state;
      if (!Array.isArray(payload.rules)) return state;
      return { ...state, activeRules: payload.rules.filter(rule => rule && typeof rule.name === 'string' && typeof rule.file === 'string')
        .map(rule => ({ name: rule.name, file: rule.file, always: rule.always === true, globs: Array.isArray(rule.globs) ? rule.globs.filter(glob => typeof glob === 'string') : [] })) };
    }
    case 'conversation/partnerState': return { ...state, partner: msg.payload };
    case 'chat/queueState': return { ...state, queue: msg.payload };
    case 'agents/historyState': return { ...state, agentHistory: msg.payload };
    case 'conversation/partnerReply':
    case 'chat/queueReply':
    case 'agents/historyReply': return { ...state, conversationReplies: [...state.conversationReplies.filter(reply => reply.requestId !== msg.payload.requestId), msg.payload].slice(-40) };
    case 'chat/queued': {
      const id = msg.payload.clientMessageId;
      const pendingSends = state.pendingSends.filter(item => item.clientMessageId !== id);
      return { ...state, pendingSends, pendingQueued: state.pendingQueued.filter(item => item.clientMessageId !== id), items: state.items.filter(item => item.kind !== 'user' || item.clientMessageId !== id), phase: state.phase === 'sending' && !pendingSends.length ? 'idle' : state.phase };
    }
    case 'tools/state': case 'tools/reply': case 'skills/state': case 'skills/reply': case 'mcp/state': case 'mcp/reply': case 'catalog/state': case 'catalog/reply': case 'rules/state': case 'rules/reply':
    case 'workflows/state': case 'workflows/reply': case 'hooks/state': case 'hooks/reply':
      return state; // ToolsTab owns these correlated snapshots.
    case 'commands/state':
      return { ...state, commands: msg.payload };
    case 'workspace/composerState':
    case 'workspace/agentPlanResult':
    case 'workspace/agentPlanPageResult':
    case 'workspace/editorChanges':
    case 'knowledge/state':
    case 'agentHooks/state':
    case 'workspace/composerAutoState':
    case 'workspace/goalState':
    case 'workspace/goalRequest':
    case 'workspace/tokenCount':
    case 'workspace/contextSuggestions':
    case 'workspace/snippetResult':
    case 'workspace/hooksState':
    case 'workspace/backgroundState':
    case 'session/historyState':
    case 'workspace/contextPresetsState':
    case 'advisors/state':
    case 'advisors/reply':
    case 'workspace/problemsState':
    case 'workspace/problemsReply':
    case 'workspace/codeReviewState':
    case 'workspace/terminalTrace':
    case 'workspace/terminalState':
    case 'workspace/interactiveState':
    case 'workspace/historyState':
    case 'workspace/historyReply':
    case 'permission/rulesState':
    case 'workspace/trustState':
    case 'feedback/state':
    case 'feedback/imageResult':
    case 'workspace/reviewState':
    case 'workspace/isolationState':
      return state; // The isolated review panel owns this snapshot.
    case 'engine/info':
      return { ...state, engine: msg.payload };
    case 'composer/prefill': {
      // A target (the editor menu's "to agent or team") becomes the one-message send target; App shows the main conversation for it.
      const payload = boundedPrefill(msg.payload);
      return { ...state, prefill: { payload, seq: (state.prefill?.seq ?? 0) + 1 }, selectedTarget: payload.target ?? state.selectedTarget };
    }
    case 'knowledge/draft': {
      const payload = boundedKnowledgeDraft(msg.payload);
      return payload ? { ...state, knowledgeDraft: { payload, seq: (state.knowledgeDraft?.seq ?? 0) + 1 } } : state;
    }
    case 'work/open':
      return { ...state, workRequest: { view: msg.payload.view, seq: (state.workRequest?.seq ?? 0) + 1 } };
    case 'session/fastResult':
      return state; // The fast-mode toggle waits for its own answer; session/info carries the state.
    case 'host/hello': {
      const hello = msg.payload;
      return {
        ...state,
        hello,
        locale: localeFromHost(hello.locale),
        includeEditorContext: state.contextChosen ? state.includeEditorContext : hello.includeEditorContextByDefault !== false,
      };
    }

    case 'bridge/state': {
      const bridge = { ...msg.payload, since: at };
      if (bridge.state !== state.bridge.state) state = { ...state, activeRules: [] };
      // A new connection starts without any open turn (the previous one's end was reported before the process went away).
      if (bridge.state === 'running') {
        return { ...state, bridge, activePromptId: state.phase === 'idle' ? undefined : state.activePromptId };
      }
      // Any other state means the omp session is gone: drop in-flight work and unanswerable questions. `activePromptId` stays
      // so the host's turn end for the interrupted turn (it may follow this state) is still recognised as that turn's end.
      // A pending new-session request is given up too: nobody is left to answer it (late correlated results are ignored).
      return {
        ...state,
        bridge,
        phase: 'idle',
        pendingSends: [],
        pendingQueued: state.pendingQueued.map(item => item.failed ? item : { ...item, failed: { code: 'queue_unconfirmed', message: makeTranslate(state.locale)('queue.unconfirmed'), retryable: false } }),
        composer: { ...state.composer, mention: null, pickerRequestId: undefined, composing: false },
        pendingNewSession: undefined,
        session: state.session ? { ...state.session, isStreaming: false } : state.session,
        items: finalizeInFlight(state.items),
        permissions: [],
        dialogs: [],
        permissionDone: 0,
        dialogDone: 0,
      };
    }

    case 'theme/changed':
      return state; // applied to the document by the message pump, not part of the reducer state

    case 'setup/state': {
      // The answer to a command is kept by its id: a later state (posted on its own, even in the same batch) must not hide it.
      const { requestId, error } = msg.payload;
      const setupReplies = typeof requestId === 'string' && requestId ? recordReply(state.setupReplies, requestId, typeof error === 'string' && error ? error : undefined) : state.setupReplies;
      return { ...state, setup: normalizeSetup(msg.payload), setupReplies };
    }

    case 'session/info': {
      const session = msg.payload;
      const before = state.session?.sessionId;
      const restarted = before !== undefined && session.sessionId !== undefined && before !== session.sessionId;
      // isStreaming is read through isBusy(); it is never latched into `phase`.
      const next: AppState = { ...state, session, activeRules: restarted || state.session?.workingDirectory !== session.workingDirectory ? [] : state.activeRules, selectedTarget: restarted ? undefined : state.selectedTarget, sendError: restarted ? undefined : state.sendError };
      // omp continues in another session (restart, `/new`): mark the boundary, what is above is not part of its context.
      if (restarted && state.items.length > 0 && state.items[state.items.length - 1]!.kind !== 'sessionReset') {
        return withItem(next, (key) => ({ kind: 'sessionReset', key }));
      }
      return next;
    }

    case 'chat/replayBegin':
      return { ...state, items: [], phase: 'idle', activePromptId: undefined, replaying: true, replayTruncated: msg.payload.truncated === true };

    case 'chat/replayEnd': {
      // The host may not have accepted a just-posted send when it took the replay snapshot. Put only still-unmatched
      // optimistic bubbles back at the tail, and keep waiting for their own turnStarted rather than an old replayed end.
      const missing = state.pendingSends.filter((send) => !state.items.some((item) => item.kind === 'user' && item.clientMessageId === send.clientMessageId));
      return {
        ...state,
        items: missing.length > 0 ? [...state.items, ...missing] : state.items,
        replaying: false,
        replayRevision: state.replayRevision + 1,
        phase: state.activePromptId !== undefined ? 'running' : state.pendingSends.length > 0 ? 'sending' : 'idle',
      };
    }

    case 'workspace/files': {
      const composer = receiveFiles(state.composer, msg.payload);
      return composer === state.composer ? state : { ...state, composer };
    }

    case 'chat/turnStarted': {
      const { clientMessageId, promptId, text, attachments, editorContext, team, agent, agentInstance, main } = msg.payload;
      const target: SendTarget | undefined = team ? { kind: 'team', id: team } : agent ? { kind: 'agent', id: agent } : agentInstance ? { kind: 'instance', id: agentInstance } : main ? { kind: 'main', id: '' } : undefined;
      if (!promptId) return state;
      let next = state;
      const index = findLastIndex(state.items, (it) => it.kind === 'user' && (clientMessageId ? it.clientMessageId === clientMessageId : it.promptId === promptId));
      const item = state.items[index];
      if (item?.kind === 'user') {
        // Normal live flow: retain the optimistic bubble exactly, including its page-recorded destination.
        next = { ...next, items: replaceAt(next.items, index, { ...item, target: item.target ?? target, editorContext, promptId, failed: undefined }) };
      } else {
        const pending = [...state.pendingSends, ...state.pendingQueued].find((entry) => entry.clientMessageId === clientMessageId);
        if (pending) next = { ...next, items: [...next.items, { ...pending, editorContext, promptId, failed: undefined }] };
        else if (typeof text === 'string' || attachments?.length) {
          next = withItem(next, (key) => ({
            kind: 'user', key, clientMessageId: clientMessageId ?? promptId, promptId, text: text ?? '', target, editorContext,
            attachments: attachments?.filter((entry) => typeof entry?.path === 'string').map((entry) => fileFromAttachment(entry.path)),
          }));
        }
      }
      next = { ...next, pendingQueued: next.pendingQueued.filter(entry => entry.clientMessageId !== clientMessageId) };
      const pendingSends = next.pendingSends.filter((entry) => entry.clientMessageId !== clientMessageId);
      // A busy-rejected second prompt never takes over the turn represented by the first prompt.
      if (next.activePromptId !== undefined && next.activePromptId !== promptId) return { ...next, pendingSends };
      return { ...next, pendingSends, phase: 'running', activePromptId: promptId };
    }

    case 'chat/assistantStart': {
      const { messageId } = msg.payload;
      if (!messageId || findOpenAssistant(state.items, messageId) >= 0) return state;
      return withItem(state, (key) => newAssistant(key, messageId));
    }

    case 'chat/assistantDelta': {
      const { messageId, kind, delta } = msg.payload;
      if (!messageId || !delta || (kind !== 'text' && kind !== 'thinking')) return state;
      let current = state;
      let index = findOpenAssistant(current.items, messageId);
      if (index < 0) {
        // A delta without a preceding assistantStart opens the message implicitly.
        current = withItem(current, (key) => newAssistant(key, messageId));
        index = current.items.length - 1;
      }
      const item = current.items[index] as AssistantItem;
      const thinking = kind === 'thinking';
      // Once a kind reached the cap its deltas are ignored without touching the state (no re-render per dropped delta).
      if (thinking ? item.thinkingTruncated : item.textTruncated) return state;
      const room = MAX_ASSISTANT_CHARS - (thinking ? item.thinking : item.text).length;
      const cut = delta.length > room;
      let piece = cut ? delta.slice(0, room) : delta;
      // A cut must not leave half of a surrogate pair at the end.
      if (cut && piece.length > 0) { const last = piece.charCodeAt(piece.length - 1); if (last >= 0xd800 && last <= 0xdbff) piece = piece.slice(0, -1); }
      const next: AssistantItem = thinking
        ? { ...item, thinking: item.thinking + piece, ...(cut ? { thinkingTruncated: true } : {}) }
        : { ...item, text: item.text + piece, ...(cut ? { textTruncated: true } : {}) };
      return { ...current, items: replaceAt(current.items, index, next) };
    }

    case 'chat/assistantEnd': {
      const { messageId, stopReason, errorMessage } = msg.payload;
      if (!messageId) return state;
      const index = findOpenAssistant(state.items, messageId);
      if (index < 0) {
        if (!errorMessage) return state;
        // The bubble was already finalized (turn end / bridge state came first): attach the error to it instead of adding an
        // empty bubble that carries only the error.
        const last = findLastIndex(state.items, (it) => it.kind === 'assistant' && it.messageId === messageId);
        if (last >= 0) {
          const prev = state.items[last] as AssistantItem;
          const patched: AssistantItem = {
            ...prev,
            stopReason: stopReason ?? prev.stopReason,
            errorMessage: prev.errorMessage ?? errorMessage,
          };
          return { ...state, items: replaceAt(state.items, last, patched) };
        }
        return withItem(state, (key) => ({ ...newAssistant(key, messageId), done: true, stopReason, errorMessage }));
      }
      const item = state.items[index] as AssistantItem;
      // A message that only carried tool calls has nothing to show: drop the empty shell.
      if (!item.text && !item.thinking && !errorMessage) {
        return { ...state, items: state.items.filter((_, i) => i !== index) };
      }
      return { ...state, items: replaceAt(state.items, index, { ...item, done: true, stopReason, errorMessage }) };
    }

    case 'chat/toolStart': {
      const { toolCallId, toolName, args, isHostTool } = msg.payload;
      if (!toolCallId) return state;
      const index = findRunningTool(state.items, toolCallId);
      const running = (key: string): ToolItem => ({
        kind: 'tool',
        key,
        toolCallId,
        toolName,
        args,
        isHostTool: isHostTool === true,
        isError: false,
        state: 'running',
      });
      // A repeated start for a call that is still running restarts its card in place.
      if (index >= 0) return { ...state, items: replaceAt(state.items, index, running(state.items[index]!.key)) };
      return withItem(state, running);
    }

    case 'chat/toolUpdate': {
      const index = msg.payload.toolCallId ? findRunningTool(state.items, msg.payload.toolCallId) : -1;
      if (index < 0) return state;
      const item = state.items[index] as ToolItem;
      return { ...state, items: replaceAt(state.items, index, { ...item, progress: msg.payload.text }) };
    }

    case 'chat/toolEnd': {
      const { toolCallId, toolName, isError, resultText, durationMs } = msg.payload;
      if (!toolCallId) return state;
      const index = findUnfinishedTool(state.items, toolCallId);
      if (index < 0) {
        // The start was missed: still show the result.
        return withItem(state, (key) => ({
          kind: 'tool',
          key,
          toolCallId,
          toolName,
          isHostTool: false,
          isError: isError === true,
          resultText,
          durationMs,
          state: 'done',
        }));
      }
      const item = state.items[index] as ToolItem;
      const done: ToolItem = {
        ...item,
        toolName: toolName || item.toolName,
        isError: isError === true,
        resultText,
        durationMs,
        progress: undefined,
        state: 'done',
      };
      return { ...state, items: replaceAt(state.items, index, done) };
    }

    case 'chat/turnEnd': {
      const { promptId, status, error } = msg.payload;
      const owner = state.activePromptId;
      const own = findUserByPrompt(state.items, promptId);
      // The end of the running turn. Without an owner the ending turn counts as the running one when this page never saw it
      // start (reloaded mid-turn) or a turn is in progress from this page's view; only a late end for an already finished,
      // known message (idle page) does not.
      const isActive = owner === promptId || (owner === undefined && (own < 0 || state.phase !== 'idle'));
      if (!isActive) {
        // Another prompt ended (rejected as busy, ...): the running turn and what is in flight are not affected.
        if (own < 0 || status === 'completed') return state;
        const item = state.items[own] as UserItem;
        const failed: UiErrorInfo = error ?? { code: status, message: '', retryable: false };
        return { ...state, items: replaceAt(state.items, own, { ...item, failed }) };
      }
      // A prompt the host rejected as busy never ran: what is in flight belongs to the agent run that made it answer "busy".
      const rejected = status === 'error' && error?.code === 'busy';
      const items = rejected ? state.items : finalizeInFlight(state.items);
      const ended: AppState = { ...state, phase: !state.replaying && state.pendingSends.length > 0 ? 'sending' : 'idle', activePromptId: undefined };
      if (status === 'error') {
        const info: UiErrorInfo = error ?? { code: 'error', message: '', retryable: false };
        // "Retry" resends the message of the turn that failed, not whatever the newest user message is.
        const user = own >= 0 ? (state.items[own] as UserItem) : undefined;
        return withItem(
          ended,
          (key) => ({
            kind: 'turnError',
            key,
            error: info,
            promptId,
            clientMessageId: user?.clientMessageId,
            retryText: user?.text,
            retryAttachments: user?.attachments,
            retryTarget: user?.target,
          }),
          items,
        );
      }
      if (status === 'aborted') return withItem(ended, (key) => ({ kind: 'aborted', key }), items);
      return { ...ended, items };
    }

    case 'chat/notice':
      return withItem(state, (key) => ({ kind: 'notice', key, level: msg.payload.level, message: msg.payload.message, ...(msg.payload.format === 'pre' ? { format: 'pre' as const } : {}) }));

    case 'chat/advisorNotes': {
      const notes = Array.isArray(msg.payload?.notes) ? msg.payload.notes.filter(note => typeof note?.note === 'string' && note.note.trim()) : [];
      return notes.length ? withItem(state, (key) => ({ kind: 'advisor', key, notes })) : state;
    }

    case 'permission/request': {
      if (!msg.payload.requestId) return state;
      const request: PendingPermission = { ...msg.payload, receivedAt: at };
      const permissions = [...state.permissions.filter((p) => p.requestId !== request.requestId), request];
      return { ...state, permissions, permissionDone: state.permissions.length === 0 ? 0 : state.permissionDone };
    }

    case 'permission/dismiss': {
      const { requestId } = msg.payload;
      const found = state.permissions.find((p) => p.requestId === requestId);
      if (!found) return state;
      const { queue, done } = removeRequest(state.permissions, state.permissionDone, requestId);
      const next = { ...state, permissions: queue, permissionDone: done };
      return withItem(next, (key) => ({ kind: 'request', key, title: permissionTitle(found, makeTranslate(state.locale)), source: 'permission', outcome: 'handled', at }));
    }

    case 'dialog/request': {
      if (!msg.payload.requestId) return state;
      const previous = state.dialogs.find((dialog) => dialog.requestId === msg.payload.requestId);
      const request: PendingDialog = { ...msg.payload, receivedAt: at, draft: previous?.draft };
      const dialogs = [...state.dialogs.filter((d) => d.requestId !== request.requestId), request];
      return { ...state, dialogs, dialogDone: state.dialogs.length === 0 ? 0 : state.dialogDone };
    }

    case 'dialog/dismiss': {
      const { requestId } = msg.payload;
      const found = state.dialogs.find((d) => d.requestId === requestId);
      if (!found) return state;
      const { queue, done } = removeRequest(state.dialogs, state.dialogDone, requestId);
      const next = { ...state, dialogs: queue, dialogDone: done };
      return withItem(next, (key) => ({ kind: 'request', key, title: dialogTitle(found, makeTranslate(state.locale)), source: 'dialog', outcome: 'handled', at }));
    }

    case 'agents/state':
      return { ...state, agents: msg.payload };

    case 'agents/reply':
      return { ...state, agentsReplies: [...state.agentsReplies.slice(-19), msg.payload] };

    case 'computer/state':
      return { ...state, computer: msg.payload };

    case 'computer/reply':
      return { ...state, computerReply: msg.payload };

    case 'permission/modeResult':
      return { ...state, permissionModeReply: msg.payload };

    case 'workspace/basketTokens':
      return { ...state, basketTokens: msg.payload };

    case 'session/newResult': {
      const { requestId, status, message } = msg.payload;
      // A nonnull id must match the request still pending on this page. Foreign, duplicate and abandoned results cannot
      // clear a newer conversation or add a failure notice to it. No id (including JSON null) is the legacy/VS-command path:
      // apply the actual session change, but it cannot acknowledge a differently correlated pending request.
      if (requestId != null && requestId !== state.pendingNewSession) return state;
      const pendingNewSession = requestId != null ? undefined : state.pendingNewSession;
      // The matched request (or a host command with no id) replaced the session: the old transcript is no longer context.
      if (status === 'completed') return { ...clearedForNewSession(state), pendingNewSession };
      // Failed / cancelled: nothing changed on the omp side, so the conversation stays as it is; tell the user why.
      const t = makeTranslate(state.locale);
      const cancelled = status === 'cancelled';
      const text = message?.trim() ? message : t(cancelled ? 'newSession.cancelled' : 'newSession.failed');
      return withItem({ ...state, pendingNewSession }, (key) => ({
        kind: 'notice',
        key,
        level: cancelled ? 'warning' : 'error',
        message: text,
      }));
    }

    case 'error': {
      const { code, message } = msg.payload;
      const text = code && code !== 'error' ? `${message} (${code})` : message;
      if (code === 'queue_rejected' && !state.replaying) {
        // The host guarantees this send was not accepted. There is at most one unacknowledged send from this page.
        const rejected = state.pendingQueued.find(item => !item.failed) ?? state.pendingSends[0];
        if (rejected) {
          const draftText = state.composer.text ? `${state.composer.text}\n\n${rejected.text}` : rejected.text;
          return { ...state, pendingQueued: state.pendingQueued.filter(item => item !== rejected), pendingSends: state.pendingSends.filter(item => item !== rejected),
            items: state.items.filter(item => item.kind !== 'user' || item.clientMessageId !== rejected.clientMessageId),
            composer: addAttachments({ ...state.composer, text: draftText }, rejected.attachments ?? []), selectedTarget: state.selectedTarget ?? rejected.target,
            phase: state.phase === 'sending' ? 'idle' : state.phase, sendError: message };
        }
        return { ...state, sendError: message };
      }
      // A host error while waiting for `chat/turnStarted` most likely means the send failed (e.g. the host dropped an
      // oversized message): the message just sent was not accepted. The same goes for a pending new session: its request
      // (or the answer) is gone, so the page stops waiting for it.
      let next: AppState = state.pendingNewSession === undefined ? state : { ...state, pendingNewSession: undefined };
      if (code === 'ui_delivery_failed') {
        // The host lost messages for this page: it denied every pending approval and cancelled every dialog, and turn ends may be
        // missing. Nothing shown can still be answered or completed; the snapshot that follows restores the live state (a turn
        // that is really still running shows through session/info.isStreaming).
        next = {
          ...next,
          phase: 'idle',
          activePromptId: undefined,
          pendingSends: [],
          items: finalizeInFlight(next.items),
          permissions: [],
          dialogs: [],
          permissionDone: 0,
          dialogDone: 0,
        };
      } else if (!state.replaying && state.pendingSends.length > 0) {
        // Replay history can contain an old error; it must not fail a new live send that is still awaiting its own ID.
        const failed: UiErrorInfo = { code: code || 'error', message, retryable: false };
        const pendingIds = new Set(next.pendingSends.map((entry) => entry.clientMessageId));
        next = {
          ...next,
          phase: next.activePromptId !== undefined ? next.phase : 'idle',
          pendingSends: [],
          items: next.items.map((item) => item.kind === 'user' && pendingIds.has(item.clientMessageId) ? { ...item, failed } : item),
        };
      }
      if (!state.replaying && next.pendingQueued.length) next = { ...next, pendingQueued: next.pendingQueued.map(item => ({ ...item, failed: { code: code || 'error', message, retryable: false } })) };
      return withItem(next, (key) => ({ kind: 'notice', key, level: 'error', message: text }));
    }
  }
}

/** The editor menu's prefill as the page uses it: plain strings of bounded size, files with both paths. */
function boundedPrefill(payload: ComposerPrefill): ComposerPrefill {
  const text = typeof payload?.text === 'string' ? payload.text.slice(0, 4000) : undefined;
  const files = Array.isArray(payload?.files)
    ? payload.files.filter((file) => file && typeof file.path === 'string' && typeof file.fullPath === 'string' && file.fullPath.length > 0 && file.fullPath.length <= 1024 && file.path.length <= 1024)
      .map((file) => ({ path: file.path, fullPath: file.fullPath }))
    : undefined;
  const rawTarget = payload?.target;
  const target = rawTarget && (rawTarget.kind === 'agent' || rawTarget.kind === 'team') && typeof rawTarget.id === 'string' && rawTarget.id.length > 0 && rawTarget.id.length <= 200
    ? { kind: rawTarget.kind, id: rawTarget.id } : undefined;
  return { text: text || undefined, files: files?.length ? files : undefined, includeEditorContext: payload?.includeEditorContext === true, send: payload?.send === true, target, ...(payload?.pickTarget === true && !target ? { pickTarget: true } : {}) };
}

/** The editor's knowledge draft as the page uses it: a workspace path, a line range and the text of those lines, kept verbatim so the anchor stays true; null when it is not usable. */
function boundedKnowledgeDraft(payload: KnowledgeDraft): KnowledgeDraft | null {
  const line = (value: unknown): value is number => typeof value === 'number' && Number.isInteger(value) && value >= 1;
  if (typeof payload?.root !== 'string' || typeof payload.path !== 'string' || typeof payload.text !== 'string' || !payload.path || payload.path.length > 1024
    || !line(payload.startLine) || !line(payload.endLine) || payload.endLine < payload.startLine || !payload.text.trim() || payload.text.length > 100_000) return null;
  return { root: payload.root, path: payload.path, startLine: payload.startLine, endLine: payload.endLine, text: payload.text };
}

export function reducer(state: AppState, action: Action): AppState {
  switch (action.type) {
    case 'host':
      return action.batch.reduce((s, { msg, at }) => applyHost(s, msg, at), state);

    case 'userSend': {
      const item: UserItem = { kind: 'user', key: `u:${action.id}`, clientMessageId: action.id, text: action.text, target: action.target, attachments: action.attachments?.slice(), ...(action.partnerKey ? { partnerKey: action.partnerKey } : {}) };
      // A message sent from an idle page starts a new turn; one sent during a running turn (a race) must not downgrade it.
      const idle = state.phase === 'idle';
      return {
        ...state,
        items: action.queued ? state.items : [...state.items, item],
        pendingSends: action.queued ? state.pendingSends : [...state.pendingSends, item],
        pendingQueued: action.queued ? [...state.pendingQueued, item] : state.pendingQueued,
        sendError: undefined,
        composer: action.clearDraft ? { ...emptyComposer } : state.composer,
        phase: idle && !action.queued ? 'sending' : state.phase,
        activePromptId: idle && !action.queued ? undefined : state.activePromptId,
        selectedTarget: action.preserveDraftTarget ? state.selectedTarget : undefined,
      };
    }

    case 'queueSendUnconfirmed':
      return { ...state, pendingQueued: state.pendingQueued.map(item => item.clientMessageId === action.id ? { ...item, failed: { code: 'queue_unconfirmed', message: makeTranslate(state.locale)('queue.unconfirmed'), retryable: false } } : item) };
    case 'recoverQueueSend': {
      const item = state.pendingQueued.find(entry => entry.clientMessageId === action.id);
      if (!item?.failed) return state;
      const text = state.composer.text ? `${state.composer.text}\n\n${item.text}` : item.text;
      return { ...state, pendingQueued: state.pendingQueued.filter(entry => entry !== item), composer: addAttachments({ ...state.composer, text }, item.attachments ?? []), selectedTarget: state.selectedTarget ?? item.target };
    }
    case 'selectTarget':
      return { ...state, selectedTarget: action.target };

    case 'composerEdit': {
      const composer = updateMention({ ...state.composer, text: action.text }, action.caret, action.selectionEnd, state.bridge.state === 'running');
      return { ...state, composer, sendError: undefined };
    }

    case 'composerCaret': {
      const composer = updateMention(state.composer, action.caret, action.selectionEnd, state.bridge.state === 'running');
      return composer === state.composer ? state : { ...state, composer };
    }

    case 'composerComposition':
      return { ...state, composer: { ...state.composer, composing: action.composing, mention: action.composing ? null : state.composer.mention } };

    case 'mentionRequested': {
      const mention = state.composer.mention;
      if (!mention || mention.start !== action.start || mention.query !== action.query || state.composer.composing || state.bridge.state !== 'running') return state;
      return { ...state, composer: { ...state.composer, mention: { ...mention, requestId: action.requestId, pending: true, files: [], selected: 0, error: undefined } } };
    }

    case 'mentionMove': {
      const mention = state.composer.mention;
      const count = action.count ?? mention?.files.length ?? 0;
      if (!mention || count <= 0) return state;
      const selected = (mention.selected + action.offset + count) % count;
      return { ...state, composer: { ...state.composer, mention: { ...mention, selected } } };
    }

    case 'mentionDismiss':
      return { ...state, composer: dismissMention(state.composer) };

    case 'mentionSelect':
      return { ...state, composer: selectMention(state.composer, action.file) };

    case 'pickerRequested':
      return { ...state, composer: { ...state.composer, pickerRequestId: action.requestId, fileError: undefined } };

    case 'workspaceFailed':
      return { ...state, composer: receiveFiles(state.composer, { requestId: action.requestId, files: [], truncated: false, error: action.message }) };

    case 'attachmentsAdd':
      return { ...state, composer: addAttachments(state.composer, action.files) };

    case 'attachmentsReplace':
    {
      const next = addAttachments({ ...state.composer, attachments: [] }, action.files);
      const kept = new Set(next.attachments.map(file => fileIdentity(file.fullPath)));
      return { ...state, composer: withoutMentions(next, state.composer.attachments.filter(file => !kept.has(fileIdentity(file.fullPath)))) };
    }
    case 'attachmentToggle':
      return { ...state, composer: { ...state.composer, attachments: state.composer.attachments.map(file => fileIdentity(file.fullPath) === fileIdentity(action.fullPath) ? { ...file, excluded: !file.excluded } : file) } };

    case 'attachmentReorder': {
      const current = state.composer.attachments;
      const from = current.findIndex(file => fileIdentity(file.fullPath) === fileIdentity(action.fullPath));
      if (from < 0) return state;
      if (action.beforePath !== undefined && !current.some(file => fileIdentity(file.fullPath) === fileIdentity(action.beforePath!))) return state;
      if (action.beforePath !== undefined && fileIdentity(action.beforePath) === fileIdentity(action.fullPath)) return state;
      const files = current.filter((_, index) => index !== from);
      const to = action.beforePath === undefined ? files.length : files.findIndex(file => fileIdentity(file.fullPath) === fileIdentity(action.beforePath!));
      files.splice(to, 0, current[from]!);
      return { ...state, composer: { ...state.composer, attachments: files } };
    }

    case 'attachmentRemove':
      return { ...state, composer: withoutMentions({ ...state.composer, attachments: state.composer.attachments.filter((file) => fileIdentity(file.fullPath) !== fileIdentity(action.fullPath)) },
        state.composer.attachments.filter((file) => fileIdentity(file.fullPath) === fileIdentity(action.fullPath))) };

    case 'dialogDraft':
      return { ...state, dialogs: state.dialogs.map((dialog) => dialog.requestId === action.requestId ? { ...dialog, draft: action.text } : dialog) };

    case 'newSessionRequested':
      // One request at a time (the button is disabled meanwhile, and the handler checks before posting).
      return state.pendingNewSession === undefined ? { ...state, pendingNewSession: action.requestId } : state;

    case 'localNotice':
      return withItem(state, (key) => ({ kind: 'notice', key, level: action.level, message: action.message }));

    case 'permissionAnswered': {
      const found = state.permissions.find((p) => p.requestId === action.requestId);
      if (!found) return state;
      const { queue, done } = removeRequest(state.permissions, state.permissionDone, action.requestId);
      return withItem({ ...state, permissions: queue, permissionDone: done }, (key) => ({ kind: 'request', key, title: permissionTitle(found, makeTranslate(state.locale)), source: 'permission', outcome: action.outcome, at: action.at }));
    }

    case 'dialogAnswered': {
      const found = state.dialogs.find((d) => d.requestId === action.requestId);
      if (!found) return state;
      const { queue, done } = removeRequest(state.dialogs, state.dialogDone, action.requestId);
      return withItem({ ...state, dialogs: queue, dialogDone: done }, (key) => ({ kind: 'request', key, title: dialogTitle(found, makeTranslate(state.locale)), source: 'dialog', outcome: action.outcome, at: action.at }));
    }

    case 'setIncludeEditorContext':
      return { ...state, includeEditorContext: action.value, contextChosen: true };
  }
}
