import { useEffect, useRef, useState } from 'react';
import { sentCaseFeedbackSummary } from '../caseFeedbackMessage';
import type { CommandReply, QueueItem, QueueState } from '../conversationProtocol';
import { useConversationCommands } from '../conversationCommands';
import { useT } from '../i18n';
import { engineAbortText } from '../engineAbortText';
import { promptRejection } from '../limits';
import { useStatusReport } from '../statusBar';
import type { UserItem } from '../state';
import { useRoleTitle, useTeamTitle } from '../teamsCache';
import { AgentDialog } from './AgentDialog';
import { AttachmentSummary } from './AttachmentSummary';
import { ConfirmPopover } from './ConfirmPopover';
import { Icon } from './Icon';
import { revealControls } from './RevealControls';
import { Badge, Pager, SearchBox, matches, usePaged } from './ui';

const QUEUE_PAGE = 10;

function QueueDestination({ item }: { item: QueueItem }) {
  const t = useT(); const role = useRoleTitle(item.agent || ''); const team = useTeamTitle(item.team || '');
  return <small className="hint">{revealControls(item.team ? team : item.agent ? role : item.agentInstance || t('partner.main'))}</small>;
}

export function ChatQueue({ state, replies, sessionId, disabled, pendingSends, onRecover }: {
  state: QueueState; replies: CommandReply[]; sessionId?: string; disabled: boolean; pendingSends: UserItem[]; onRecover: (item: UserItem) => void;
}) {
  const t = useT(); const [open, setOpen] = useState(false); const [editing, setEditing] = useState<QueueItem>(); const [text, setText] = useState('');
  const [clear, setClear] = useState(false); const opener = useRef<HTMLElement | null>(null); const actions = useRef<HTMLDivElement>(null);
  const { post, pending, error } = useConversationCommands(replies, sessionId);
  const other = state.items.length > 0 && state.sessionId !== sessionId;
  useEffect(() => { setEditing(undefined); setClear(false); }, [sessionId]);
  useEffect(() => { if (!open) setClear(false); }, [open]);
  // An item that left the queue while its editor was open (sent, removed elsewhere) has nothing left to edit.
  useEffect(() => { if (editing && !state.items.some(item => item.id === editing.id)) setEditing(undefined); }, [state.items, editing]);
  // No count limit: a long queue is searched and shown a page at a time.
  const [query, setQuery] = useState('');
  const shown = state.items.map((item, index) => ({ item, index })).filter(({ item }) => matches(query, item.text, item.agent, item.team, item.agentInstance));
  const paged = usePaged(shown, (sessionId ?? '') + ':' + query, QUEUE_PAGE);
  // A failed queue action is reported in the VS status bar, not as a line inside the queue strip.
  useStatusReport(error ? engineAbortText(error, t) : null);
  if (!state.items.length && !pendingSends.length) return null;
  const blocked = disabled || pending;
  const editInvalid = !text.trim() || !!promptRejection(text, { type: 'chat/send', payload: { clientMessageId: 'queue-edit', text, includeEditorContext: editing?.includeEditorContext ?? false } }, editing?.attachments?.map(file => file.path));
  const capture = () => { opener.current = document.activeElement instanceof HTMLElement ? document.activeElement : null; };
  return <div className="chat-queue" data-testid="chat-queue">
    <button type="button" className="conversation-strip-toggle" data-testid="queue-toggle" aria-expanded={open} onClick={() => setOpen(!open)}><Icon name={open ? 'chevronDown' : 'chevron'} size={12} /><strong>{t('queue.title', { count: state.items.length + pendingSends.length })}</strong>{state.paused && <Badge tone="warn">{t('queue.paused')}</Badge>}</button>
    {state.paused && <p className="hint" role="status" data-testid="queue-reason">{revealControls(state.reason || t('queue.paused'))}</p>}
    {other && <p className="hint" role="status" data-testid="queue-other">{t('queue.other')}</p>}
    {open && <div className="queue-expanded">
      <div ref={actions}>
        <div className="conversation-actions">
          <button className="btn subtle small" type="button" data-testid="queue-pause" disabled={blocked || !state.items.length || (state.paused && other)} onClick={() => post('chat/queue', { action: state.paused ? 'resume' : 'pause' })}>{t(state.paused ? 'queue.resume' : 'queue.pause')}</button>
          <button className="btn subtle small danger" type="button" data-testid="queue-clear" disabled={blocked || !state.items.length} onClick={() => { capture(); setClear(true); }}>{t('queue.clear')}</button>
        </div>
        {clear && <ConfirmPopover anchorRef={actions} testId="queue-clear-confirm" className="queue-clear-confirm" label={t('queue.clear')} question={t('queue.confirmClear')}
          cancelLabel={t('dialog.cancel')} confirmLabel={t('queue.clear')} confirmTestId="queue-clear-proceed" confirmDisabled={blocked || !state.items.length}
          onCancel={() => { setClear(false); opener.current?.focus(); }} onConfirm={() => { if (post('chat/queue', { action: 'clear' })) setClear(false); }} />}
      </div>
      {state.items.length > QUEUE_PAGE && <SearchBox value={query} onChange={setQuery} testId="queue-search" />}
      <ol className="queue-list">{paged.slice.map(({ item, index }) => <li key={item.id} data-testid={`queue-item-${item.id}`}>
        <p>{revealControls(sentCaseFeedbackSummary(item.text, t) ?? item.text)}</p><QueueDestination item={item} />
        {item.attachments?.length ? <AttachmentSummary files={item.attachments} testId={`queue-attachments-${item.id}`} /> : null}
        <div className="conversation-actions">
          {/* Sends now: to the agent working at this moment (or, with nothing running, as the next message). */}
          <button className="btn subtle small" type="button" data-testid={`queue-steer-${item.id}`} title={t('queue.steerTitle')} disabled={blocked || other || !item.canSteer} onClick={() => post('chat/queue', { action: 'steer', id: item.id })}><Icon name="zap" size={12} /> {t('queue.steer')}</button>
          <button className="icon-btn" type="button" data-testid={`queue-up-${item.id}`} title={t('queue.up')} aria-label={t('queue.up')} disabled={blocked || index === 0} onClick={() => post('chat/queue', { action: 'move', id: item.id, index: index - 1 })}><Icon name="arrowUp" size={12} /></button>
          <button className="icon-btn" type="button" data-testid={`queue-down-${item.id}`} title={t('queue.down')} aria-label={t('queue.down')} disabled={blocked || index === state.items.length - 1} onClick={() => post('chat/queue', { action: 'move', id: item.id, index: index + 1 })}><Icon name="arrowDown" size={12} /></button>
          <button className="btn subtle small" type="button" data-testid={`queue-edit-${item.id}`} disabled={blocked} onClick={() => { capture(); setText(item.text); setEditing(item); }}>{t('queue.edit')}</button>
          <button className="btn subtle small danger" type="button" data-testid={`queue-remove-${item.id}`} disabled={blocked} onClick={() => post('chat/queue', { action: 'remove', id: item.id })}>{t('queue.remove')}</button>
        </div>
      </li>)}</ol>
      <Pager paged={paged} testId="queue-pager" />
      {pendingSends.map(item => <div className="queue-awaiting" key={item.clientMessageId} data-testid="queue-awaiting"><p>{revealControls(sentCaseFeedbackSummary(item.text, t) ?? item.text)}</p>
        {item.failed ? <div className="composer-error" role="alert"><Icon name="error" size={12} /><span>{revealControls(engineAbortText(item.failed.message || t('queue.unconfirmed'), t))}</span></div>
          : <small className="hint" role="status">{t('queue.awaiting')}</small>}
        {item.failed && <button className="btn subtle small" type="button" onClick={() => onRecover(item)}>{t('queue.edit')}</button>}</div>)}
    </div>}
    {editing && <AgentDialog testId="queue-editor" labelledBy="queue-editor-title" returnFocus={opener.current} onClose={() => setEditing(undefined)}>
      <div className="dialog-head"><h4 id="queue-editor-title">{t('queue.edit')}</h4></div><div className="dialog-form"><div className="form-field"><textarea data-testid="queue-edit-input" aria-label={t('queue.edit')} rows={5} value={text} onChange={event => setText(event.target.value)} /></div></div>
      <div className="dialog-foot"><button className="btn subtle" type="button" onClick={() => setEditing(undefined)}>{t('dialog.cancel')}</button><button className="btn primary" type="button" data-testid="queue-edit-save" disabled={blocked || editInvalid} onClick={() => { if (post('chat/queue', { action: 'edit', id: editing.id, text })) setEditing(undefined); }}>{t('session.save')}</button></div>
    </AgentDialog>}
  </div>;
}
