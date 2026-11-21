import { useCallback, useEffect, useRef, useState } from 'react';
import { newId } from './format';
import { makeTranslate } from './i18n';
import { sameRoot } from './knowledge';
import { normalizeSessionHistory, onMessage, send, type SessionHistoryState, type UiToHost } from './protocol';

type HistoryAction = UiToHost['session/history']['action'];
interface Pending { action: HistoryAction; sessionId?: string; timer?: number }
/** Requests that only read: the host runs session requests one after another, so a rename or compaction may follow them. */
const READS: Partial<Record<HistoryAction, true>> = { refresh: true, preview: true, messages: true };
/** The current conversation's own changes (also the /rename and /compact commands): their failures are told in the chat, the host's too. */
const CHAT_ACTIONS: Partial<Record<HistoryAction, true>> = { rename: true, compact: true };

/**
 * One correlated store for the chat header and session page; previews never replace the list.
 * `pendingSwitch`: the id of the conversation change the page still waits for (a switch requested here ends with it).
 */
export function useSessionHistory(workspaceKey: string, sessionId: string | undefined, connected: boolean, locale: string,
  onSwitch: (requestId: string, root: string, id: string) => boolean, onChatNotice: (message: string) => void,
  pendingSwitch?: string) {
  const [state, setState] = useState<SessionHistoryState>({ root: '', entries: [] });
  const [pending, setPending] = useState(false);
  const [error, setError] = useState('');
  const [pendingId, setPendingId] = useState<string>();
  const requests = useRef(new Map<string, Pending>());
  const snapshot = useRef(state); snapshot.current = state;
  const currentScope = useRef(workspaceKey); currentScope.current = workspaceKey;
  const currentSession = useRef(sessionId); currentSession.current = sessionId;
  const refreshNeeded = useRef(true);
  const t = makeTranslate(locale.startsWith('ko') ? 'ko' : 'en');
  useEffect(() => {
    for (const request of requests.current.values()) window.clearTimeout(request.timer);
    requests.current.clear(); setPending(false); setPendingId(undefined); setState({ root: '', entries: [] }); setError('');
    const off = onMessage(message => {
      if (message.type !== 'session/historyState' || currentScope.current !== workspaceKey) return;
      const payload = normalizeSessionHistory(message.payload);
      const id = payload.requestId;
      if (!id || !requests.current.has(id)) return;
      const request = requests.current.get(id)!;
      const action = request.action;
      window.clearTimeout(request.timer); requests.current.delete(id);
      setPending(requests.current.size > 0); setPendingId(undefined);
      if ((payload.root && !sameRoot(payload.root, currentScope.current)) ||
        (request.sessionId !== currentSession.current && action !== 'switch' && action !== 'delete')) return;
      // A rename or /compact failure is a chat notice of the host (like the start and end of a compaction), not a session-list error.
      setError(CHAT_ACTIONS[action] ? '' : payload.error ?? '');
      if (!payload.error) setState(previous => ({ ...previous, ...payload,
        root: payload.root || previous.root,
        entries: ['preview', 'messages', 'compact'].includes(action) ? previous.entries : payload.entries,
        hidden: ['preview', 'messages', 'compact'].includes(action) ? previous.hidden : payload.hidden,
      }));
    });
    return () => {
      off(); for (const request of requests.current.values()) window.clearTimeout(request.timer);
      requests.current.clear();
    };
  }, [workspaceKey]);
  useEffect(() => {
    if (connected) return;
    for (const request of requests.current.values()) window.clearTimeout(request.timer);
    requests.current.clear(); setPending(false); setPendingId(undefined);
  }, [connected]);
  // A switch has no timer: it lasts as long as the page's conversation change. The host ends an abandoned one with `session/newResult`
  // alone (no list reply), so once the page no longer waits for it the request is over here as well.
  useEffect(() => {
    let ended = false;
    for (const [id, request] of requests.current) if (request.action === 'switch' && id !== pendingSwitch) { requests.current.delete(id); ended = true; }
    if (ended) { setPending(requests.current.size > 0); setPendingId(undefined); }
  }, [pendingSwitch, pending]);

  /** True while another request keeps `action` from starting now (a rename or compaction waits only for a request that changes something). */
  const busyFor = useCallback((action: HistoryAction): boolean => {
    const pending = [...requests.current.values()];
    return pending.length > 0 && !(CHAT_ACTIONS[action] && pending.every(request => READS[request.action]));
  }, []);

  const command = useCallback((action: HistoryAction, value?: string, selection?: { root: string; sessionId: string }): boolean => {
    if (!connected || currentScope.current !== workspaceKey || currentSession.current !== sessionId || busyFor(action)) return false;
    const fail = (message: string): void => { if (CHAT_ACTIONS[action]) onChatNotice(message); else setError(message); };
    if (action === 'rename' && (!value?.trim() || value.trim().length > 120 || /[\p{Cc}\p{Cf}]/u.test(value))) {
      fail(t('session.invalidName')); return false;
    }
    if ((action === 'switch' || action === 'preview') && (!sameRoot(snapshot.current.root, currentScope.current) ||
      !snapshot.current.entries.some(entry => entry.id === value))) {
      fail(t('session.selectionChanged')); return false;
    }
    if (action === 'delete' && (!selection?.sessionId || !sameRoot(selection.root, currentScope.current) || selection.root !== snapshot.current.root ||
      !snapshot.current.entries.some(entry => entry.id === value && entry.sessionId === selection.sessionId))) {
      fail(t('session.selectionChanged')); return false;
    }
    const requestId = newId();
    const root = snapshot.current.root ?? currentScope.current;
    const payload = { action, requestId, ...(action === 'rename' ? { name: value!.trim() }
      : action === 'compact' ? { instructions: value ?? '' }
      : action === 'delete' ? { id: value, root: selection!.root, sessionId: selection!.sessionId }
      : action === 'preview' ? { id: value }
      : action === 'messages' ? { sessionId, cursor: value } : {}) } as UiToHost['session/history'];
    // Register before posting: a synchronous dev host can already reply to this request.
    // A switch can wait for a running turn to finish. Its answer is not bounded by the usual read-request timeout.
    const timer = action === 'switch' ? undefined : window.setTimeout(() => {
      if (!requests.current.delete(requestId)) return;
      setPending(requests.current.size > 0); setPendingId(undefined); fail(t('session.noReply'));
    }, action === 'delete' ? 60000 : action === 'compact' ? 360000 : action === 'messages' || action === 'rename' ? 30000 : 15000);
    requests.current.set(requestId, { action, sessionId, timer });
    setPending(true); setPendingId(action === 'delete' ? value : undefined); setError('');
    const accepted = action === 'switch' ? onSwitch(requestId, root, value ?? '') : send('session/history', payload);
    if (!accepted) {
      window.clearTimeout(timer); requests.current.delete(requestId); setPending(requests.current.size > 0); setPendingId(undefined); fail(t('error.hostUnavailable')); return false;
    }
    return true;
  }, [connected, workspaceKey, onSwitch, onChatNotice, busyFor, sessionId, t]);

  const commandRef = useRef(command); commandRef.current = command;
  useEffect(() => {
    refreshNeeded.current = true;
    for (const [id, request] of requests.current) {
      if (request.sessionId === sessionId || request.action === 'switch' || request.action === 'delete') continue;
      window.clearTimeout(request.timer); requests.current.delete(id);
    }
    setPending(requests.current.size > 0);
    setState(previous => ({ ...previous, current: undefined, preview: undefined, messages: undefined, nextCursor: undefined, totalMessages: undefined }));
    setError('');
  }, [workspaceKey, sessionId, connected]);
  useEffect(() => {
    if (connected && refreshNeeded.current && commandRef.current('refresh')) refreshNeeded.current = false;
  }, [workspaceKey, sessionId, connected, pending]);
  return { state, pending, pendingId, error, command, busyFor };
}

export type SessionHistoryController = ReturnType<typeof useSessionHistory>;
