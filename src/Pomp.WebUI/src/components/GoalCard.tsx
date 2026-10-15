import { useEffect, useRef, useState, type KeyboardEvent } from 'react';
import { newId } from '../format';
import { useT } from '../i18n';
import { onMessage, send } from '../protocol';
import { parseTokenBudget } from '../tokenBudget';
import { cardOnScreen, composing, consume, handlesEnter, RequestCard, useArmed } from './Dialogs';
import { revealControls } from './RevealControls';

/** What the goal card shows: the goal text and a token budget to start from; `fromRequest` when the model asked for it (`request_goal_run`). */
export interface GoalCardState { key: number; text: string; tokenBudget?: number; fromRequest?: string; root?: string; sessionId?: string }


/**
 * The confirmation of a goal run (typed `/goal`, or asked for by the model): the goal text and an optional token budget, start or cancel.
 * Automatic continuation costs model usage, so nothing starts before the answer. It is a card at the end of the conversation like the
 * other requests (Enter starts, Esc cancels); the host starts the workflow and arms the continuation when it gets the answer.
 */
export function GoalCard({ card, root, sessionId, busy, onClose }: { card: GoalCardState; root: string; sessionId: string; busy: boolean; onClose: () => void }) {
  const t = useT();
  const cardRef = useRef<HTMLDivElement>(null);
  const armed = useArmed();
  const [text, setText] = useState(card.text);
  const [budget, setBudget] = useState(card.tokenBudget === undefined ? '' : String(card.tokenBudget));
  const [error, setError] = useState('');
  const [pending, setPending] = useState(false);
  const request = useRef<string | undefined>(undefined);
  const close = useRef(onClose); close.current = onClose;
  useEffect(() => onMessage(message => {
    if (message.type !== 'workspace/goalState' || message.payload.requestId !== request.current) return;
    request.current = undefined; setPending(false);
    if (message.payload.ok) close.current(); else setError(message.payload.error || t('error.hostUnavailable'));
  }), [t]);
  useEffect(() => {
    if (!pending) return;
    const timer = window.setTimeout(() => { request.current = undefined; setPending(false); setError(t('setup.noReply')); }, 15000);
    return () => window.clearTimeout(timer);
  }, [pending, t]);

  const locked = !armed || pending || busy;
  const start = (): void => {
    if (locked) return;
    const goal = text.trim();
    if (!goal) { setError(t('goal.empty')); return; }
    const tokenBudget = parseTokenBudget(budget);
    if (tokenBudget === 'invalid') { setError(t('goal.budgetInvalid')); return; }
    const requestId = newId();
    request.current = requestId; setPending(true); setError('');
    if (!send('workspace/goal', { requestId, action: 'start', root: card.root ?? root, sessionId: card.sessionId ?? sessionId, text: goal, ...(tokenBudget === null ? {} : { tokenBudget }), ...(card.fromRequest ? { fromRequest: card.fromRequest } : {}) })) {
      request.current = undefined; setPending(false); setError(t('error.hostUnavailable'));
    }
  };
  const cancel = (): void => {
    if (!armed || pending) return;
    // The host keeps the card the model asked for until it is answered; tell it this one was declined.
    if (card.fromRequest) send('workspace/goal', { requestId: newId(), action: 'cancel', root: card.root ?? root, sessionId: card.sessionId ?? sessionId, fromRequest: card.fromRequest });
    onClose();
  };
  const onField = (event: KeyboardEvent<HTMLElement>): void => {
    // A line break stays Shift+Enter (the composer's rule); a plain Enter answers.
    if (event.key !== 'Enter' || event.shiftKey || event.ctrlKey || event.altKey || event.metaKey || composing(event)) return;
    consume(event);
    if (!event.repeat) start();
  };
  const onCardKeyDown = (event: KeyboardEvent<HTMLDivElement>): void => {
    if (composing(event)) return;
    if (event.key === 'Escape') { consume(event); if (!event.repeat) cancel(); }
    else if (event.key === 'Enter' && !handlesEnter(event.target)) {
      if (!cardOnScreen(cardRef.current)) return;
      consume(event);
      if (!event.repeat) start();
    }
  };

  return (
    <RequestCard title={t('goal.title')} cardRef={cardRef} onCardKeyDown={onCardKeyDown}
      footer={<>
        {error && <div className="dialog-error" role="alert" data-testid="goal-error"><span>{revealControls(error)}</span></div>}
        <div className="dialog-actions">
          <button type="button" className="btn primary" data-testid="goal-start" disabled={locked} aria-keyshortcuts="Enter" onClick={start}>{t('goal.start')}</button>
          <button type="button" className="btn" data-testid="goal-cancel" disabled={!armed || pending} aria-keyshortcuts="Escape" onClick={cancel}>{t('goal.cancel')}</button>
        </div>
      </>}>
      <label className="note"><span>{t('goal.text')}</span>
        <textarea className="field" rows={3} data-testid="goal-text" value={text} onChange={event => { setText(event.target.value); setError(''); }} onKeyDown={onField} />
      </label>
      <label className="note"><span>{t('goal.budget')}</span>
        <input className="field" type="text" inputMode="numeric" autoComplete="off" spellCheck={false} data-testid="goal-budget" value={budget} onChange={event => { setBudget(event.target.value); setError(''); }} onKeyDown={onField} />
      </label>
      <div className="dialog-message">{t('goal.question')}</div>
    </RequestCard>
  );
}
