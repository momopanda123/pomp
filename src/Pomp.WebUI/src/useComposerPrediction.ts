import { useCallback, useEffect, useRef, useState, type RefObject } from 'react';
import { newId } from './format';
import { onMessage, send } from './protocol';

interface PredictionOptions {
  inputRef: RefObject<HTMLTextAreaElement | null>;
  enabled: boolean;
  sessionId?: string;
  text: string;
  caret: { start: number; end: number };
  focused: boolean;
  suppressed: boolean;
}

/** Predictions belong to an exact draft and caret, never to the chat transcript. */
export function useComposerPrediction({ inputRef, enabled, sessionId, text, caret, focused, suppressed }: PredictionOptions) {
  const eligible = enabled && !!sessionId && focused && !suppressed && text.length <= 4000
    && text.replace(/\s/gu, '').length >= 3 && !text.trimStart().startsWith('/')
    && caret.start === text.length && caret.end === text.length;
  const current = useRef({ eligible, sessionId, text });
  current.current = { eligible, sessionId, text };
  const pending = useRef<{ requestId: string; sessionId: string; text: string } | null>(null);
  const timer = useRef<number | undefined>(undefined);
  const [prediction, setPrediction] = useState<{ sessionId: string; draft: string; text: string } | null>(null);
  const discard = useCallback(() => {
    if (timer.current !== undefined) window.clearTimeout(timer.current);
    timer.current = undefined;
    pending.current = null;
    setPrediction(null);
  }, []);

  useEffect(() => {
    if (!enabled) return;
    return onMessage(message => {
      if (message.type !== 'composer/prediction') return;
      const request = pending.current;
      const now = current.current;
      const el = inputRef.current;
      if (!request || message.payload.requestId !== request.requestId) return;
      pending.current = null;
      if (!now.eligible || now.sessionId !== request.sessionId || now.text !== request.text || !el
        || document.activeElement !== el || el.value !== request.text
        || el.selectionStart !== el.value.length || el.selectionEnd !== el.value.length) return;
      const continuation = message.payload.text;
      if (message.payload.error || typeof continuation !== 'string' || !continuation.trim()) return;
      const line = continuation.split(/[\r\n]/u, 1)[0]!.slice(0, 200);
      if (line.trim()) setPrediction({ sessionId: request.sessionId, draft: request.text, text: line });
    });
  }, [enabled, inputRef]);

  useEffect(() => {
    discard();
    if (!eligible || !sessionId) return;
    timer.current = window.setTimeout(() => {
      timer.current = undefined;
      const el = inputRef.current;
      if (!current.current.eligible || !el || document.activeElement !== el || el.value !== text
        || el.selectionStart !== text.length || el.selectionEnd !== text.length) return;
      const request = { requestId: newId(), sessionId, text };
      pending.current = request;
      if (!send('composer/predict', request)) pending.current = null;
    }, 500);
    return () => {
      if (timer.current !== undefined) window.clearTimeout(timer.current);
      timer.current = undefined;
      pending.current = null;
    };
  }, [eligible, sessionId, text, caret.start, caret.end, inputRef, discard]);

  return { prediction: eligible && prediction?.sessionId === sessionId && prediction.draft === text ? prediction.text : '', discard };
}
