import { memo, useEffect, useRef, useState, type RefObject } from 'react';
import { newId } from '../format';
import { useT } from '../i18n';
import { send, type DictationReplyPayload, type DictationStatePayload } from '../protocol';
import { reportStatus } from '../statusBar';
import { Icon } from './Icon';
import { plainControls } from './RevealControls';

export interface DictationButtonProps {
  dictation?: DictationStatePayload | null;
  reply?: DictationReplyPayload | null;
  connected: boolean;
  /** The composer takes no text now (a dialog, another view, a new conversation starting). */
  blocked: boolean;
  inputRef: RefObject<HTMLTextAreaElement | null>;
  onStart: (captureId: string) => void;
  onStartFailed: (captureId: string) => void;
}

/**
 * The composer's microphone (left of Send, the same square): starts and ends a voice capture. The engine listens on this PC's default
 * microphone and transcribes on this PC; the text goes into the draft (Composer), never straight to the model. While a capture
 * starts (the first one downloads the speech model) a press cancels it; while it records a press ends it.
 */
function DictationButtonView({ dictation, reply, connected, blocked, inputRef, onStart, onStartFailed }: DictationButtonProps) {
  const t = useT();
  const [pending, setPending] = useState<string | null>(null);
  const pendingRef = useRef<string | null>(null);
  const startingRef = useRef<string | null>(null);
  const phase = dictation?.state ?? 'idle';
  const active = phase !== 'idle';

  // Only the reply to this button's request ends its wait; a refusal goes to the status bar.
  useEffect(() => {
    if (!reply || reply.requestId !== pendingRef.current) return;
    pendingRef.current = null; setPending(null);
    if (!reply.ok) {
      if (startingRef.current === reply.requestId) onStartFailed(reply.requestId);
      reportStatus(reply.error ? plainControls(reply.error) : t('dictation.failed'), 'error');
    }
    startingRef.current = null;
  }, [reply, t, onStartFailed]);
  useEffect(() => {
    if (!connected) { pendingRef.current = null; setPending(null); }
  }, [connected]);

  const toggle = () => {
    if (pendingRef.current !== null) return;
    const requestId = newId();
    pendingRef.current = requestId; setPending(requestId);
    if (!active) { startingRef.current = requestId; onStart(requestId); }
    if (!send('dictation/set', { requestId, on: !active })) {
      pendingRef.current = null; setPending(null);
      if (!active) onStartFailed(requestId);
      startingRef.current = null;
      reportStatus(t('dictation.failed'), 'error');
    }
    inputRef.current?.focus({ preventScroll: true });
  };
  const waiting = pending !== null || phase === 'starting' || phase === 'transcribing';
  const disabled = !connected || pending !== null || phase === 'transcribing' || (blocked && !active);
  const title = t(phase === 'recording' ? 'dictation.stop' : phase === 'starting' ? 'dictation.starting' : phase === 'transcribing' ? 'dictation.transcribing' : 'dictation.start');
  return (
    <button type="button" className={`btn composer-send-btn dictation-btn${phase === 'recording' ? ' recording' : ''}`} data-testid="dictation" data-state={phase}
      aria-pressed={active} disabled={disabled} title={title} aria-label={title}
      // The press must not take the caret out of the input: the dictated text goes where it is.
      onMouseDown={event => event.preventDefault()} onClick={toggle}>
      {waiting && phase !== 'recording' ? <span className="spinner" aria-hidden="true" /> : <Icon name="mic" size={14} />}
      <span className="sr-only">{title}</span>
    </button>
  );
}

export const DictationButton = memo(DictationButtonView);
