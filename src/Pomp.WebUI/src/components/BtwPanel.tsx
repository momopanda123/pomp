import { useEffect, useId, useLayoutEffect, useRef, useState, type FormEvent, type KeyboardEvent } from 'react';
import { useT } from '../i18n';
import { btwTurns, type BtwPanel as BtwPanelState } from '../state';
import { Icon } from './Icon';
import { Markdown } from './Markdown';
import { revealControls } from './RevealControls';

/**
 * The side-question panel (`/btw`): a temporary chat about the current conversation, docked above the queue and the input. It never
 * touches the transcript, the queue or the running work; closing it cancels only its own running answer.
 */
export function BtwPanel({ panel, name, connected, onAsk, onClose }: {
  panel: BtwPanelState; name: string; connected: boolean; onAsk: (question: string) => boolean; onClose: () => void;
}) {
  const t = useT(); const titleId = useId();
  const [draft, setDraft] = useState('');
  const turns = btwTurns(panel.record);
  const latest = turns.at(-1);
  const running = panel.pending !== undefined || latest?.status === 'running';
  // The question this page sent shows until omp's record carries it.
  const waiting = panel.pending && latest?.question.trim() !== panel.pending.question.trim() ? panel.pending.question : undefined;
  // Without a topic (its first question failed) the input starts one.
  const canAsk = connected && !running && draft.trim().length > 0;
  const scroller = useRef<HTMLDivElement>(null); const input = useRef<HTMLTextAreaElement>(null);
  const answerLength = latest?.answer.length ?? 0;
  useLayoutEffect(() => { const element = scroller.current; if (element) element.scrollTop = element.scrollHeight; }, [turns.length, answerLength, waiting]);
  useEffect(() => { input.current?.focus({ preventScroll: true }); }, []);
  const submit = (event?: FormEvent) => {
    event?.preventDefault();
    if (!canAsk) return;
    if (onAsk(draft.trim())) setDraft('');
  };
  const onKeyDown = (event: KeyboardEvent<HTMLTextAreaElement>) => {
    if (event.key === 'Enter' && !event.shiftKey && !event.nativeEvent.isComposing) { event.preventDefault(); submit(); }
    else if (event.key === 'Escape') { event.preventDefault(); onClose(); }
  };
  return <section className="btw-panel" data-testid="btw-panel" aria-labelledby={titleId}>
    <div className="btw-head">
      <h4 id={titleId}>{t('btw.title', { name })}</h4>
      <button className="icon-btn" type="button" data-testid="btw-close" title={t('btw.close')} aria-label={t('btw.close')} onClick={onClose}><Icon name="x" size={12} /></button>
    </div>
    <div className="btw-turns" ref={scroller} aria-live="polite">
      <p className="btw-note">{t('btw.note')}</p>
      {turns.map((turn, index) => <div className="btw-turn" key={index} data-testid="btw-turn">
        <div className="btw-question">{revealControls(turn.question)}</div>
        {turn.answer && <div className="btw-answer"><Markdown text={turn.answer} /></div>}
        {turn.status !== 'complete' && <small className={`btw-status ${turn.status}`} role={turn.status === 'error' ? 'alert' : 'status'}>{t(`btw.status.${turn.status}`)}{turn.error ? ` · ${turn.error}` : ''}</small>}
      </div>)}
      {waiting !== undefined && <div className="btw-turn"><div className="btw-question">{revealControls(waiting)}</div><small className="btw-status running" role="status">{t('btw.sending')}</small></div>}
      {panel.error && <p className="btw-error" role="alert" data-testid="btw-error">{panel.error}</p>}
    </div>
    <form className="btw-input" onSubmit={submit}>
      <textarea ref={input} rows={1} data-testid="btw-input" aria-label={t('btw.input')} placeholder={t('btw.input')} value={draft} disabled={!connected} onChange={event => setDraft(event.target.value)} onKeyDown={onKeyDown} />
      <button className="btn primary small" type="submit" data-testid="btw-ask" disabled={!canAsk}>{t('btw.ask')}</button>
    </form>
  </section>;
}
