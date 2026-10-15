import { createContext, useCallback, useContext, useEffect, useMemo, useRef, useState, type ReactNode } from 'react';
import { newId } from './format';
import { useT } from './i18n';
import { normalizeKnowledge, sameRoot, type KnowledgeSnapshot } from './knowledge';
import { onMessage, send, type KnowledgeCommand, type KnowledgeItem } from './protocol';

/** The host answers a command within this time or the request ends with the "no reply" error (its late answer still updates the list). */
const REPLY_TIMEOUT_MS = 15000;
/** Optimizing runs the editor model for up to a minute: once the host reported it running, the request waits this long for the result. */
const OPTIMIZE_TIMEOUT_MS = 90000;

/** How a command ended. `error` is the host's text, or the page's when no host answered. */
export interface KnowledgeReply { ok: boolean; error?: string }

export interface KnowledgeController {
  /** Every knowledge of the workspace, candidates included (`usableKnowledge` leaves those out). */
  items: readonly KnowledgeItem[];
  /** Ids selected for the current conversation. */
  selected: readonly string[];
  /** The knowledge being optimized now. */
  optimizing: string | undefined;
  /** Knowledge the host left out of its message (too large to send it all); 0 when none. */
  hidden: number;
  /** A command (other than an optimize) waits for its answer or for the one before it. */
  pending: boolean;
  /** The error of a state the host sent unasked, e.g. a failed automatic optimize; empty once a later state has none. */
  notice: string;
  /** Sends the command and resolves with how it ended. Commands go one at a time, in order; an optimize does not hold the others back. */
  command: (command: KnowledgeCommand) => Promise<KnowledgeReply>;
  refresh: () => void;
}

interface Request {
  action: KnowledgeCommand['action'];
  /** The knowledge an optimize works on. */
  id: string | undefined;
  timer: number;
  /** An optimize the host reported running; only its end settles the request. */
  started: boolean;
  resolve: (reply: KnowledgeReply) => void;
}

const inert: KnowledgeController = {
  items: [], selected: [], optimizing: undefined, hidden: 0, pending: false, notice: '',
  command: () => Promise.resolve({ ok: false, error: '' }), refresh: () => {},
};
const KnowledgeContext = createContext<KnowledgeController>(inert);

/** The workspace's knowledge, the conversation's selection and the commands that change them. Without a provider the list is empty and every command fails quietly. */
export const useKnowledge = (): KnowledgeController => useContext(KnowledgeContext);

/**
 * One owner of the `knowledge/state` snapshot for the whole page: the Tools list, the context basket and the role and team editors all read it.
 * - A state for another workspace, or for another conversation than the page shows, is ignored; a change of either drops the snapshot and every
 *   request, then asks again.
 * - Commands carry the workspace and a request id; the state with that id answers it. A state without an id is a push (another place changed
 *   the list) and only updates the snapshot.
 * - An optimize is answered twice: the state that reports it running, then the result. Whichever of them carries the request id, the request
 *   ends with the result.
 */
export function useKnowledgeController({ root, sessionId, connected }: { root: string; sessionId?: string; connected: boolean }): KnowledgeController {
  const t = useT();
  const [snapshot, setSnapshot] = useState<KnowledgeSnapshot | null>(null);
  const [notice, setNotice] = useState('');
  const [waiting, setWaiting] = useState(0);
  const requests = useRef(new Map<string, Request>());
  const tail = useRef<Promise<unknown>>(Promise.resolve());
  const refreshing = useRef<Promise<KnowledgeReply> | null>(null);
  const generation = useRef(0);
  const latest = useRef({ root, sessionId, connected, t }); latest.current = { root, sessionId, connected, t };

  const settle = useCallback((requestId: string, reply: KnowledgeReply) => {
    const request = requests.current.get(requestId);
    if (!request) return;
    window.clearTimeout(request.timer); requests.current.delete(requestId);
    request.resolve(reply);
  }, []);
  const arm = useCallback((requestId: string, ms: number) => {
    const request = requests.current.get(requestId);
    if (!request) return;
    window.clearTimeout(request.timer);
    request.timer = window.setTimeout(() => settle(requestId, { ok: false, error: latest.current.t('setup.noReply') }), ms);
  }, [settle]);
  /** Ends every request, sent or still queued, without waiting for the host (a change of workspace or conversation, or the end of the page). */
  const dropRequests = useCallback(() => {
    generation.current++; tail.current = Promise.resolve(); refreshing.current = null;
    for (const requestId of [...requests.current.keys()]) settle(requestId, { ok: false, error: latest.current.t('setup.noReply') });
  }, [settle]);

  useEffect(() => onMessage(message => {
    if (message.type !== 'knowledge/state') return;
    const payload = message.payload; const current = latest.current;
    if (!current.root || !sameRoot(payload.root, current.root)) return;
    if (typeof payload.sessionId === 'string' && current.sessionId && payload.sessionId !== current.sessionId) return;
    const next = normalizeKnowledge(payload);
    if (next) setSnapshot(next);
    const error = typeof payload.error === 'string' ? payload.error : '';
    const reply: KnowledgeReply = error ? { ok: false, error } : { ok: true };
    const requestId = typeof payload.requestId === 'string' && payload.requestId ? payload.requestId : undefined;
    let answered = false;
    for (const [id, request] of [...requests.current]) {
      if (request.action === 'optimize') {
        const running = !error && next?.optimizing === request.id;
        if (id === requestId) { answered = true; if (running) { request.started = true; arm(id, OPTIMIZE_TIMEOUT_MS); } else settle(id, reply); }
        // A push: the host reports the optimize running, then its end.
        else if (!requestId && (running || request.started)) { answered = true; if (running) { request.started = true; arm(id, OPTIMIZE_TIMEOUT_MS); } else settle(id, reply); }
      } else if (id === requestId) { answered = true; settle(id, reply); }
    }
    // An error nobody asked for (a failed automatic optimize) is shown until a later state replaces it.
    setNotice(!answered && error ? error : '');
  }), [arm, settle]);

  const command = useCallback((payload: KnowledgeCommand): Promise<KnowledgeReply> => {
    const unavailable = () => Promise.resolve<KnowledgeReply>({ ok: false, error: latest.current.t('error.hostUnavailable') });
    if (!latest.current.root || !latest.current.connected) return unavailable();
    if (payload.action === 'refresh' && refreshing.current) return refreshing.current;
    const queued = generation.current;
    const run = () => new Promise<KnowledgeReply>(resolve => {
      const { root: scope, connected: online, t: translate } = latest.current;
      if (queued !== generation.current) { resolve({ ok: false, error: translate('setup.noReply') }); return; }
      if (!scope || !online) { resolve({ ok: false, error: translate('error.hostUnavailable') }); return; }
      const requestId = newId();
      requests.current.set(requestId, { action: payload.action, id: payload.action === 'optimize' ? payload.id : undefined, started: false, resolve,
        timer: window.setTimeout(() => settle(requestId, { ok: false, error: latest.current.t('setup.noReply') }), REPLY_TIMEOUT_MS) });
      if (!send('knowledge/command', { ...payload, requestId, root: scope })) settle(requestId, { ok: false, error: translate('error.hostUnavailable') });
    });
    // An optimize takes up to a minute: it neither waits for the earlier commands nor holds back the later ones.
    if (payload.action === 'optimize') return run();
    setWaiting(count => count + 1);
    const result = tail.current.then(run);
    tail.current = result;
    void result.then(() => setWaiting(count => count - 1));
    if (payload.action === 'refresh') {
      refreshing.current = result;
      void result.then(() => { if (refreshing.current === result) refreshing.current = null; });
    }
    return result;
  }, [settle]);
  const refresh = useCallback(() => { void command({ action: 'refresh' }); }, [command]);

  const scope = useRef({ root, sessionId });
  useEffect(() => {
    if (scope.current.root !== root || scope.current.sessionId !== sessionId) {
      scope.current = { root, sessionId };
      dropRequests(); setSnapshot(null); setNotice('');
    }
    if (connected && root) refresh();
  }, [root, sessionId, connected, dropRequests, refresh]);
  useEffect(() => dropRequests, [dropRequests]);

  return useMemo<KnowledgeController>(() => ({
    items: snapshot?.items ?? inert.items, selected: snapshot?.selected ?? inert.selected, optimizing: snapshot?.optimizing, hidden: snapshot?.hidden ?? 0,
    pending: waiting > 0, notice, command, refresh,
  }), [snapshot, waiting, notice, command, refresh]);
}

/** Provides the page's knowledge to every component below it. */
export function KnowledgeProvider({ root, sessionId, connected, children }: { root: string; sessionId?: string; connected: boolean; children: ReactNode }) {
  const controller = useKnowledgeController({ root, sessionId, connected });
  return <KnowledgeContext value={controller}>{children}</KnowledgeContext>;
}
