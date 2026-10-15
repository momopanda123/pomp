import { useCallback, useEffect, useRef, useState } from 'react';
import { newId } from './format';
import { useT } from './i18n';
import { onMessage, send, type AdvisorDocument, type AdvisorsCommand, type AdvisorsReplyPayload, type AdvisorsStatePayload } from './protocol';

export const ADVISOR_TOOLS = ['read', 'bash', 'edit', 'ast_grep', 'ast_edit', 'ask', 'debug', 'ida', 'eval', 'github', 'glob', 'grep', 'find', 'lsp', 'checkpoint', 'rewind', 'context_notes', 'new_context', 'security_scan', 'task', 'wait', 'todo', 'web_search', 'write', 'memory_edit', 'retain', 'recall', 'reflect', 'learn', 'manage_skill'] as const;
export const advisorSlug = (name: string): string => name.toLowerCase().replace(/[^a-z0-9]+/g, '-').replace(/^-+|-+$/g, '') || 'advisor';
export const advisorCount = (state: AdvisorsStatePayload | null): number => state?.implicitDefault ? 1 : state?.roster.length ?? 0;
/** The engine runs its own advisor under this name when no file defines any advisor. */
export const DEFAULT_ADVISOR = 'default';

/** Engine selectors carry their optional thinking level in the final suffix, not a separate field. */
export function splitAdvisorModel(value: string | undefined): { selector: string; level: string } {
  const match = /:(off|minimal|low|medium|high|xhigh|max)$/i.exec(value ?? '');
  return match ? { selector: value!.slice(0, -match[0].length), level: match[1]!.toLowerCase() } : { selector: value ?? '', level: '' };
}

/** The UI validates the same bounds as the host before sending any instructions. */
export function validAdvisorDocument(document: AdvisorDocument): boolean {
  // A file holds any number of advisors; its size is the bound (256 KiB, as the host and the engine extension read it).
  if (new TextEncoder().encode(JSON.stringify(document)).length > 256 * 1024 || (document.instructions?.length ?? 0) > 16000) return false;
  const budget = (value: number | undefined) => value === undefined || Number.isInteger(value) && value >= 1 && value <= 32;
  if (!budget(document.maxNotesPerUpdate)) return false;
  const names = new Set<string>();
  for (const entry of document.advisors) {
    const name = entry.name.trim(); const slug = advisorSlug(name);
    if (!name || name.length > 64 || names.has(slug)) return false;
    names.add(slug);
    if ((entry.model?.length ?? 0) > 200 || (entry.instructions?.length ?? 0) > 16000 || !budget(entry.maxNotesPerUpdate)) return false;
    if (entry.reviewInterval !== undefined && (!Number.isInteger(entry.reviewInterval) || entry.reviewInterval < 1 || entry.reviewInterval > 1000000)) return false;
    if (entry.reviewMode !== undefined && entry.reviewMode !== 'turn' && entry.reviewMode !== 'agent-end') return false;
    if (entry.syncBacklog !== undefined && !['off', '1', '3', '5', 'strict'].includes(entry.syncBacklog)) return false;
    if (entry.tools?.some(tool => !(ADVISOR_TOOLS as readonly string[]).includes(tool))) return false;
  }
  return new TextEncoder().encode(JSON.stringify(document)).length <= 256 * 1024;
}

interface Request { id: string; timer: number; resolve: (reply: AdvisorsReplyPayload) => void }
export interface AdvisorsController {
  state: AdvisorsStatePayload | null;
  pending: boolean;
  generation: number;
  command: (command: Omit<AdvisorsCommand, 'requestId'>) => Promise<AdvisorsReplyPayload>;
}

/** Mounted with AgentsPanel, even while hidden, to receive the page-ready snapshot and keep chip counts current. */
export function useAdvisorsController({ root, sessionId, connected }: { root?: string; sessionId?: string; connected: boolean }): AdvisorsController {
  const t = useT();
  const [state, setShown] = useState<AdvisorsStatePayload | null>(null);
  /** The snapshot on screen (snapshot.current may already be the next session's). */
  const shown = useRef<AdvisorsStatePayload | null>(null);
  const setState = useCallback((next: AdvisorsStatePayload | null) => { shown.current = next; setShown(next); }, []);
  const [pending, setPending] = useState(false);
  const [generation, setGeneration] = useState(0);
  const request = useRef<Request | null>(null);
  const snapshot = useRef<AdvisorsStatePayload | null>(null);
  const latest = useRef({ root, sessionId, connected, t }); latest.current = { root, sessionId, connected, t };
  const settle = useCallback((reply: AdvisorsReplyPayload) => {
    const current = request.current;
    if (!current || reply.requestId !== current.id) return;
    request.current = null; window.clearTimeout(current.timer); setPending(false); current.resolve(reply);
  }, []);
  useEffect(() => onMessage(message => {
    if (message.type === 'advisors/reply') {
      if (typeof message.payload?.ok === 'boolean') settle(message.payload);
    } else if (message.type === 'advisors/state') {
      const next = message.payload;
      if (!next || typeof next.supported !== 'boolean' || !Array.isArray(next.advisors) || !Array.isArray(next.files) || !Array.isArray(next.roster)) return;
      // A snapshot for the session the page is about to show (session/info arrived but has not rendered yet) is kept and adopted below.
      snapshot.current = next;
      if (next.sessionId && latest.current.sessionId && next.sessionId !== latest.current.sessionId) return;
      setState(next);
    }
  }), [settle, setState]);
  const previous = useRef({ root, sessionId, connected });
  useEffect(() => {
    const before = previous.current; previous.current = { root, sessionId, connected };
    const scopeChanged = before.root !== root || before.sessionId !== sessionId;
    if (scopeChanged || before.connected !== connected) {
      if (request.current) settle({ requestId: request.current.id, ok: false, error: latest.current.t('setup.noReply') });
      setGeneration(value => value + 1);
      // A page-ready push may already be for the newly selected session.
      if (scopeChanged) {
        const current = snapshot.current;
        // Session ids are unique, so the workspace may have just become known (first session/info) without making the snapshot foreign.
        snapshot.current = current?.sessionId && current.sessionId === sessionId && (before.root === root || !before.root) ? current : null;
        setState(snapshot.current);
      }
    }
  }, [root, sessionId, connected, settle]);
  useEffect(() => () => {
    const current = request.current; request.current = null;
    if (current) { window.clearTimeout(current.timer); current.resolve({ requestId: current.id, ok: false, error: latest.current.t('setup.noReply') }); }
  }, []);
  const command = useCallback((payload: Omit<AdvisorsCommand, 'requestId'>): Promise<AdvisorsReplyPayload> => {
    const id = newId();
    if (!latest.current.connected || request.current) return Promise.resolve({ requestId: id, ok: false, error: latest.current.t('error.hostUnavailable') });
    if (payload.action === 'open' && !shown.current?.files.some(file => file.path === payload.path)) return Promise.resolve({ requestId: id, ok: false, error: latest.current.t('advisors.failed') });
    return new Promise(resolve => {
      // The mock and host can answer synchronously: register the request and timeout BEFORE posting.
      request.current = { id, resolve, timer: window.setTimeout(() => settle({ requestId: id, ok: false, error: latest.current.t('setup.noReply') }), 15000) };
      setPending(true);
      if (!send('advisors/command', { ...payload, requestId: id })) settle({ requestId: id, ok: false, error: latest.current.t('error.hostUnavailable') });
    });
  }, [settle]);
  return { state, pending, generation, command };
}
