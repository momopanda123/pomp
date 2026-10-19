import { useCallback, useEffect, useMemo, useRef, useState } from 'react';
import { promptHistory } from './promptHistory';
import { onMessage } from './protocol';
import type { TranscriptItem } from './state';
import { wordBeforeCaret, wordStore } from './wordCompletion';

interface WordCompletionOptions {
  enabled: boolean;
  workspaceKey: string;
  sessionId?: string;
  items: readonly TranscriptItem[];
  text: string;
  caret: { start: number; end: number };
  focused: boolean;
  suppressed: boolean;
}

export interface WordCompletion {
  /** Caret offset the rest goes in at. */
  at: number;
  /** The typed prefix: the accepted word is `prefix + rest`. */
  prefix: string;
  rest: string;
}

/** The rest of the word before the caret, from this workspace's words; Esc hides it until the draft or the caret changes. */
export function useWordCompletion({ enabled, workspaceKey, sessionId, items, text, caret, focused, suppressed }: WordCompletionOptions) {
  const store = enabled ? wordStore(workspaceKey) : null;
  const latest = useRef({ workspaceKey, sessionId, items });
  latest.current = { workspaceKey, sessionId, items };
  // Reads the conversation a moment after it changes, at most four times a second while a reply streams, never while a key is handled.
  const pending = useRef<number | undefined>(undefined);
  useEffect(() => () => { window.clearTimeout(pending.current); pending.current = undefined; }, [store]);
  useEffect(() => {
    if (!store || pending.current !== undefined) return;
    pending.current = window.setTimeout(() => {
      pending.current = undefined;
      const now = latest.current;
      store.sync(promptHistory(now.workspaceKey), now.sessionId, now.items);
    }, 250);
  }, [store, workspaceKey, sessionId, items]);
  useEffect(() => {
    if (!store) return;
    return onMessage(message => {
      if (message.type === 'workspace/files' && Array.isArray(message.payload.files)) store.addFiles(message.payload.files);
    });
  }, [store]);

  const [dismissed, setDismissed] = useState<{ text: string; at: number } | null>(null);
  const completion = useMemo((): WordCompletion | null => {
    if (!store || !focused || suppressed || caret.start !== caret.end) return null;
    const prefix = wordBeforeCaret(text, caret.start);
    const rest = prefix ? store.complete(prefix) : undefined;
    return prefix && rest ? { at: caret.start, prefix, rest } : null;
  }, [store, focused, suppressed, text, caret.start, caret.end]);
  const visible = completion && !(dismissed?.text === text && dismissed.at === completion.at) ? completion : null;
  const dismiss = useCallback(() => setDismissed({ text, at: caret.start }), [text, caret.start]);
  const accepted = useCallback((word: string) => store?.accept(word), [store]);
  return { completion: visible, dismiss, accepted };
}
