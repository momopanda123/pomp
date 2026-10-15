import { useLayoutEffect, useSyncExternalStore } from 'react';
import { normalizeProblems } from '../problems';
import { onMessage, type BackgroundAgentState, type CodeReviewState, type ComposerWorkflowState, type HostMessage, type InteractiveTerminalState, type IsolationResult, type ProjectHookState, type TerminalState, type WorkspaceHistoryState, type WorkspaceReviewState, type WorkspaceProblemsState } from '../protocol';

export interface WorkState {
  review?: WorkspaceReviewState;
  /** Isolated member results of the current workspace only. */
  isolation: IsolationResult[];
  /** Results the host left out of its message (too large to send them all). */
  isolationHidden: number;
  codeReview: CodeReviewState;
  problems: WorkspaceProblemsState;
  workflow?: ComposerWorkflowState;
  terminal: TerminalState;
  interactive: InteractiveTerminalState;
  background?: BackgroundAgentState;
  hooks?: ProjectHookState;
  history: WorkspaceHistoryState;
}

const emptyState = (): WorkState => ({
  isolation: [],
  isolationHidden: 0,
  problems: { root: '', items: [], truncated: false, updatedUtc: '' },
  codeReview: { id: '', root: '', status: 'empty', autoReview: false, message: '', findings: [], notices: [], history: [], viewedId: '', prior: null },
  terminal: { jobs: [], paused: false },
  interactive: { root: '', sessionId: '', enabled: false, supported: false, connected: false, state: 'empty' },
  history: { entries: [] },
});
let state = emptyState();
let scope: { root: string; sessionId?: string } | undefined;
const listeners = new Set<() => void>();
let release: (() => void) | undefined;
const rootKey = (root: string) => root.replaceAll('\\', '/').toLowerCase();
const notify = () => listeners.forEach(listener => listener());

/** In-memory only. A workspace/session switch invalidates every previous panel snapshot. */
export function setWorkScope(root: string, sessionId?: string): void {
  if (scope && rootKey(scope.root) === rootKey(root) && scope.sessionId === sessionId) { scope = { root, sessionId }; return; }
  scope = { root, sessionId };
  state = emptyState();
  notify();
}

export function resetWorkState<K extends keyof WorkState>(key: K): void {
  state = { ...state, [key]: emptyState()[key] };
  notify();
}

export function useWorkScope(root: string, sessionId?: string): void {
  useLayoutEffect(() => setWorkScope(root, sessionId), [root, sessionId]);
}

export function matchesWorkScope(root: string, sessionId?: string): boolean {
  return (!scope?.root || rootKey(root) === rootKey(scope.root)) && (!scope?.sessionId || !sessionId || sessionId === scope.sessionId);
}

function receive(message: HostMessage): void {
  switch (message.type) {
    case 'session/info':
      setWorkScope(message.payload.workingDirectory ?? '', message.payload.sessionId);
      return;
    case 'workspace/reviewState': {
      const data = message.payload;
      if (typeof data.id !== 'string' || !Number.isSafeInteger(data.revision) || !Array.isArray(data.files) || !Array.isArray(data.diagnostics) || !Array.isArray(data.outcomes)) return;
      // The host writes a file outcome's state as its enum name ("Applied"); the page compares lowercase.
      state = { ...state, review: { ...data, outcomes: data.outcomes.map((item) => ({ ...item, state: String(item.state).toLowerCase() })) } };
      break;
    }
    case 'workspace/isolationState': {
      const items = message.payload.items;
      if (!Array.isArray(items)) return;
      const hidden = message.payload.hidden;
      state = { ...state, isolationHidden: Number.isSafeInteger(hidden) && (hidden as number) > 0 ? hidden as number : 0,
        isolation: items.filter(item => typeof item?.id === 'string' && typeof item.root === 'string' && Array.isArray(item.files) && !!scope?.root && matchesWorkScope(item.root)) };
      break;
    }
    case 'workspace/problemsState': {
      const data = normalizeProblems(message.payload);
      if (!data || !matchesWorkScope(data.root)) return;
      state = { ...state, problems: data };
      break;
    }
    case 'workspace/codeReviewState':
      if (!Array.isArray(message.payload.findings) || !matchesWorkScope(message.payload.root)) return;
      state = { ...state, codeReview: message.payload };
      break;
    case 'workspace/composerState': {
      const data = message.payload;
      if (!matchesWorkScope(data.root, data.sessionId) || !Number.isSafeInteger(data.revision) || data.revision < 0 || !Array.isArray(data.steps) || data.steps.length > 32 || !data.steps.every(step => typeof step === 'string' && step.length <= 500) || !Array.isArray(data.outcomes)) return;
      if (state.workflow?.id === data.id && state.workflow.revision > data.revision) return;
      state = { ...state, workflow: data };
      break;
    }
    case 'workspace/terminalState':
      if (!Array.isArray(message.payload.jobs)) return;
      state = { ...state, terminal: message.payload };
      break;
    case 'workspace/interactiveState':
      if (!matchesWorkScope(message.payload.root, message.payload.sessionId)) return;
      state = { ...state, interactive: message.payload };
      break;
    case 'workspace/backgroundState':
      if (!matchesWorkScope(message.payload.root, message.payload.state === 'live' ? message.payload.sessionId : undefined)) return;
      state = { ...state, background: message.payload };
      break;
    case 'workspace/hooksState':
      if ((scope?.root && message.payload.root !== scope.root) || !matchesWorkScope(message.payload.root, message.payload.sessionId)) return;
      state = { ...state, hooks: message.payload };
      break;
    case 'workspace/historyState':
      if (message.payload.error || !Array.isArray(message.payload.entries) || (message.payload.root && !matchesWorkScope(message.payload.root))) return;
      state = { ...state, history: message.payload };
      break;
    default: return;
  }
  notify();
}

function subscribe(listener: () => void): () => void {
  listeners.add(listener);
  if (!release) release = onMessage(receive);
  return () => {
    listeners.delete(listener);
    if (!listeners.size) { release?.(); release = undefined; scope = undefined; state = emptyState(); }
  };
}
const snapshot = () => state;

/** Panels and badges subscribe to the very same validated, ephemeral host snapshots. No host commands are sent here. */
export function useWorkState<K extends keyof WorkState>(key: K): WorkState[K] {
  return useSyncExternalStore(subscribe, snapshot, snapshot)[key];
}
export function useWorkSnapshot(): WorkState {
  return useSyncExternalStore(subscribe, snapshot, snapshot);
}
