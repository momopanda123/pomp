import { useCallback, useEffect, useRef, useState } from 'react';
import { newId } from './format';
import { makeTranslate } from './i18n';
import { normalizeSessionHistory, onMessage, send, type SessionHistoryState, type UiToHost } from './protocol';

type HistoryAction = UiToHost['session/history']['action'];
interface Pending { action: HistoryAction; timer: number }
/** Requests that only read: the host runs session requests one after another, so a rename or compaction may follow them. */
const READS: Partial<Record<HistoryAction, true>> = { refresh: true, preview: true, messages: true };
/** The current conversation's own changes (also the /rename and /compact commands): their failures are told in the chat, the host's too. */
const CHAT_ACTIONS: Partial<Record<HistoryAction, true>> = { rename: true, compact: true };

/** One correlated store for the chat header and session page; previews never replace the list. */
export function useSessionHistory(workspaceKey: string, sessionId: string | undefined, connected: boolean, locale: string,
  onSwitch: (requestId: string, root: string, id: string) => boolean, onChatNotice: (message: string) => void) {
  const [state, setState] = useState<SessionHistoryState>({ root: '', entries: [] });
  const [pending, setPending] = useState(false);
  const [error, setError] = useState('');
  const [pendingId, setPendingId] = useState<string>();
  const requests = useRef(new Map<string, Pending>());
  const snapshot = useRef(state); snapshot.current = state;
  const currentScope = useRef(workspaceKey); currentScope.current = workspaceKey;
  const t = makeTranslate(locale.startsWith('ko') ? 'ko' : 'en');

  useEffect(() => {
    for (const request of requests.current.values()) window.clearTimeout(request.timer);
    requests.current.clear(); setPending(false); setPendingId(undefined); setState({ root: '', entries: [] }); setError('');
    const off = onMessage(message => {
      if (message.type !== 'session/historyState') return;
      const payload = normalizeSessionHistory(message.payload);
      const id = payload.requestId;
      if (!id || !requests.current.has(id)) return;
      const action = requests.current.get(id)!.action;
      window.clearTimeout(requests.current.get(id)!.timer); requests.current.delete(id);
      // A rename or /compact failure is a chat notice of the host (like the start and end of a compaction), not a session-list error.
      setPending(requests.current.size > 0); setPendingId(undefined); setError(CHAT_ACTIONS[action] ? '' : payload.error ?? '');
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

  /** True while another request keeps `action` from starting now (a rename or compaction waits only for a request that changes something). */
  const busyFor = useCallback((action: HistoryAction): boolean => {
    const pending = [...requests.current.values()];
    return pending.length > 0 && !(CHAT_ACTIONS[action] && pending.every(request => READS[request.action]));
  }, []);

  const command = useCallback((action: HistoryAction, value?: string): boolean => {
    if (!connected || busyFor(action)) return false;
    const fail = (message: string): void => { if (CHAT_ACTIONS[action]) onChatNotice(message); else setError(message); };
    if (action === 'rename' && (!value?.trim() || value.trim().length > 120 || /[\p{Cc}\p{Cf}]/u.test(value))) {
      fail(t('session.invalidName')); return false;
    }
    const requestId = newId();
    const root = snapshot.current.root ?? currentScope.current;
    const payload = { action, requestId, ...(action === 'rename' ? { name: value!.trim() }
      : action === 'compact' ? { instructions: value ?? '' }
      : action === 'preview' || action === 'delete' ? { id: value }
      : action === 'messages' ? { sessionId, cursor: value } : {}) } as UiToHost['session/history'];
    // Register before posting: a synchronous dev host can already reply to this request.
    const timer = window.setTimeout(() => {
      if (!requests.current.delete(requestId)) return;
      setPending(requests.current.size > 0); setPendingId(undefined); fail(t('session.noReply'));
    }, action === 'delete' ? 60000 : action === 'compact' ? 360000 : action === 'messages' || action === 'rename' ? 30000 : 15000);
    requests.current.set(requestId, { action, timer });
    setPending(true); setPendingId(action === 'delete' ? value : undefined); setError('');
    const accepted = action === 'switch' ? onSwitch(requestId, root, value ?? '') : send('session/history', payload);
    if (!accepted) {
      window.clearTimeout(timer); requests.current.delete(requestId); setPending(requests.current.size > 0); setPendingId(undefined); fail(t('error.hostUnavailable')); return false;
    }
    return true;
  }, [connected, onSwitch, onChatNotice, busyFor, sessionId, t]);

  const commandRef = useRef(command); commandRef.current = command;
  useEffect(() => { if (connected) commandRef.current('refresh'); }, [workspaceKey, sessionId, connected]);
  return { state, pending, pendingId, error, command, busyFor };
}

export type SessionHistoryController = ReturnType<typeof useSessionHistory>;
