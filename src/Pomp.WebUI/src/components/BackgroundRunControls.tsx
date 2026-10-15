import { useEffect, useRef, useState } from 'react';
import { newId } from '../format';
import { useT } from '../i18n';
import { onMessage, send, type BackgroundAgentJob, type BackgroundAgentState } from '../protocol';
import { revealControls } from './RevealControls';

const STOPPABLE = ['running', 'pending', 'queued', 'inProgress'];

/**
 * Controls stay generation- and session-bound, just as in the former background Work panel. Pause/resume/cancel need the
 * engine's control protocol; without it, Stop (one member only, after confirmation) appears when the engine offers POMP's stop command.
 */
export function BackgroundRunControls({ state, job, sessionId, disabled }: { state: BackgroundAgentState | undefined; job: BackgroundAgentJob; sessionId?: string; disabled: boolean }) {
  const t = useT(); const request = useRef<string | null>(null); const [pending, setPending] = useState(false); const [error, setError] = useState('');
  const [confirming, setConfirming] = useState(false); const stopButton = useRef<HTMLButtonElement>(null); const confirmButton = useRef<HTMLButtonElement>(null);
  useEffect(() => { request.current = null; setPending(false); setError(''); setConfirming(false); }, [sessionId, job.control?.generation]);
  useEffect(() => onMessage(message => {
    if (message.type !== 'workspace/backgroundState' || message.payload.sessionId !== sessionId || message.payload.root !== state?.root) return;
    if (request.current && message.payload.controlRequestId === request.current && !message.payload.controlPending) { request.current = null; setPending(false); }
  }), [sessionId, state?.root]);
  useEffect(() => {
    if (!pending) return;
    const timer = window.setTimeout(() => { request.current = null; setPending(false); setError(t('setup.noReply')); }, 20000);
    return () => window.clearTimeout(timer);
  }, [pending, t]);
  useEffect(() => { if (confirming) confirmButton.current?.focus(); }, [confirming]);
  if (state?.state !== 'live' || state.sessionId !== sessionId || !sessionId) return null;
  const controlled = state.controlVersion === 1 && !!job.control;
  const stoppable = !controlled && state.stopAvailable === true && STOPPABLE.includes(job.status);
  if (!controlled && !stoppable) return null;
  const blocked = disabled || pending || state.controlPending;
  const post = (action: 'pause' | 'resume' | 'cancel' | 'stop') => {
    if (blocked || request.current) return;
    const requestId = newId(); request.current = requestId; setPending(true); setError('');
    const target = action === 'stop' ? { id: job.id } : { id: job.id, generation: job.control!.generation };
    if (!send('workspace/background', { action, requestId, root: state.root, sessionId, ...target })) { request.current = null; setPending(false); setError(t('error.hostUnavailable')); }
  };
  const cancelStop = () => { setConfirming(false); stopButton.current?.focus(); };
  if (!controlled) return <>
    <button ref={stopButton} className="btn subtle small danger" type="button" data-testid={`run-stop-${job.id}`} disabled={blocked} aria-expanded={confirming} onClick={() => setConfirming(true)}>{t('runs.stop')}</button>
    {confirming && <span className="run-stop-confirm" role="group" aria-label={t('runs.stopConfirm')} data-testid={`run-stop-confirm-${job.id}`} onKeyDown={event => {
      if (event.key === 'Escape' && !event.nativeEvent.isComposing) { event.preventDefault(); event.stopPropagation(); cancelStop(); }
    }}>
      <span>{t('runs.stopQuestion')}</span>
      <button ref={confirmButton} className="btn small danger" type="button" data-testid={`run-stop-yes-${job.id}`} disabled={blocked} onClick={() => { setConfirming(false); post('stop'); }}>{t('runs.stopConfirm')}</button>
      <button className="btn subtle small" type="button" onClick={cancelStop}>{t('dialog.cancel')}</button>
    </span>}
    {error && <span role="alert">{revealControls(error)}</span>}
  </>;
  return <><button className="btn subtle small" type="button" data-testid={`run-pause-${job.id}`} disabled={blocked || job.control!.state === 'pauseRequested'} onClick={() => post('pause')}>{t('queue.pause')}</button><button className="btn subtle small" type="button" data-testid={`run-resume-${job.id}`} disabled={blocked || job.control!.state !== 'pauseRequested'} onClick={() => post('resume')}>{t('queue.resume')}</button><button className="btn subtle small danger" type="button" data-testid={`run-cancel-${job.id}`} disabled={blocked} onClick={() => post('cancel')}>{t('dialog.cancel')}</button>{error && <span role="alert">{revealControls(error)}</span>}</>;
}
