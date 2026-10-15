import { useEffect, useRef, useState } from 'react';
import { newId } from './format';
import { useT } from './i18n';
import { onMessage, send, type HostMessageType, type HostToUi, type UiMessageType, type UiToHost } from './protocol';

const REPLY_TIMEOUT_MS = 15000;

/** The workspace and, for per-conversation commands, the conversation every request and answer belongs to. */
export interface HostCommandScope { root: string | undefined; sessionId?: string | undefined }

type Scoped = 'requestId' | 'root' | 'sessionId';

/**
 * One request at a time to a host command that its state message answers by requestId.
 * - Every request carries the scope; a state for another scope is ignored, and a scope change drops the pending request.
 * - The host may also push the state without a requestId when the data changed elsewhere: that refreshes the data but
 *   never answers the pending request, which still gets its own answer, error or timeout.
 * - A request without an answer within 15 seconds ends with the "no reply" error; its late answer is ignored.
 * `onState` gets every accepted state. `answered` holds the tag given to `run` when the state answers that request, and
 * is null for a push. The hook keeps `error` for answers; pushes leave it alone.
 */
export function useHostCommand<K extends UiMessageType, M extends HostMessageType, Tag extends string = never>(
  command: K,
  stateType: M,
  scope: HostCommandScope,
  enabled: boolean,
  onState: (payload: HostToUi[M], answered: { tag: Tag | null } | null) => void,
) {
  const t = useT();
  const [pending, setPending] = useState(false);
  const [error, setError] = useState('');
  const pendingRef = useRef<string | null>(null);
  const tagRef = useRef<Tag | null>(null);
  const perConversation = 'sessionId' in scope;
  const latest = useRef({ scope, onState });
  latest.current = { scope, onState };

  useEffect(() => {
    pendingRef.current = null;
    tagRef.current = null;
    setPending(false);
    setError('');
  }, [scope.root, scope.sessionId]);

  useEffect(() => {
    if (!pending) return;
    const timer = window.setTimeout(() => {
      pendingRef.current = null;
      tagRef.current = null;
      setPending(false);
      setError(t('setup.noReply'));
    }, REPLY_TIMEOUT_MS);
    return () => window.clearTimeout(timer);
  }, [pending, t]);

  useEffect(() => onMessage((message) => {
    if (message.type !== stateType) return;
    const raw = (message as { payload?: unknown }).payload;
    if (typeof raw !== 'object' || raw === null) return;
    const payload = raw as { root?: unknown; sessionId?: unknown; requestId?: unknown; error?: unknown };
    const current = latest.current.scope;
    if (payload.root !== current.root || ('sessionId' in current && payload.sessionId !== current.sessionId)) return;
    let answered: { tag: Tag | null } | null = null;
    if (payload.requestId != null) {
      // A late answer to a superseded or timed-out request.
      if (payload.requestId !== pendingRef.current) return;
      answered = { tag: tagRef.current };
      pendingRef.current = null;
      tagRef.current = null;
      setPending(false);
      setError(typeof payload.error === 'string' ? payload.error : '');
    }
    latest.current.onState(raw as HostToUi[M], answered);
  }), [stateType]);

  /** Sends the command with the scope and a new requestId; false when it was not sent (busy, disabled or no host). */
  const run = (request: Omit<UiToHost[K], Scoped>, tag: Tag | null = null): boolean => {
    if (!enabled || pendingRef.current || !scope.root || (perConversation && !scope.sessionId)) return false;
    const requestId = newId();
    pendingRef.current = requestId;
    tagRef.current = tag;
    setError('');
    const scoped = perConversation ? { root: scope.root, sessionId: scope.sessionId } : { root: scope.root };
    if (!send(command, { ...request, ...scoped, requestId } as unknown as UiToHost[K])) {
      pendingRef.current = null;
      tagRef.current = null;
      setError(t('error.hostUnavailable'));
      return false;
    }
    // An answer delivered during send already settled the request.
    if (pendingRef.current === requestId) setPending(true);
    return true;
  };

  return { pending, error, setError, run };
}
