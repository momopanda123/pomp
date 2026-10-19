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
import { Icon } from './Icon';
import { revealControls } from './RevealControls';
import { Pager, SearchBox, matches, usePaged } from './ui';

const QUEUE_PAGE = 10;

function QueueDestination({ item }: { item: QueueItem }) {
  const t = useT(); const role = useRoleTitle(item.agent || ''); const team = useTeamTitle(item.team || '');
  return <small>{revealControls(item.team ? team : item.agent ? role : item.agentInstance || t('partner.main'))}</small>;
}

export function ChatQueue({ state, replies, sessionId, disabled, pendingSends, onRecover }: {
  state: QueueState; replies: CommandReply[]; sessionId?: string; disabled: boolean; pendingSends: UserItem[]; onRecover: (item: UserItem) => void;
}) {
  const t = useT(); const [open, setOpen] = useState(false); const [editing, setEditing] = useState<QueueItem>(); const [text, setText] = useState('');
  const [clear, setClear] = useState(false); const opener = useRef<HTMLElement | null>(null);
  const { post, pending, error } = useConversationCommands(replies, sessionId);
  const other = state.items.length > 0 && state.sessionId !== sessionId;
  useEffect(() => { setEditing(undefined); setClear(false); }, [sessionId]);
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
    <button type="button" className="conversation-strip-toggle" data-testid="queue-toggle" aria-expanded={open} onClick={() => setOpen(!open)}><Icon name="chevronDown" size={12} /><strong>{t('queue.title', { count: state.items.length + pendingSends.length })}</strong>{state.paused && <span>{t('queue.paused')}</span>}</button>
    {state.paused && <p className="hint" role="status" data-testid="queue-reason">{revealControls(state.reason || t('queue.paused'))}</p>}
    {other && <p className="hint" role="status" data-testid="queue-other">{t('queue.other')}</p>}
    {open && <div className="queue-expanded">
      <div className="conversation-actions">
        <button className="btn subtle small" type="button" data-testid="queue-pause" disabled={blocked || !state.items.length || (state.paused && other)} onClick={() => post('chat/queue', { action: state.paused ? 'resume' : 'pause' })}>{t(state.paused ? 'queue.resume' : 'queue.pause')}</button>
        <button className="btn subtle small danger" type="button" data-testid="queue-clear" disabled={blocked || !state.items.length} onClick={() => { capture(); setClear(true); }}>{t('queue.clear')}</button>
      </div>
      {state.items.length > QUEUE_PAGE && <SearchBox value={query} onChange={setQuery} testId="queue-search" />}
      <ol className="queue-list">{paged.slice.map(({ item, index }) => <li key={item.id} data-testid={`queue-item-${item.id}`}>
        <p>{revealControls(sentCaseFeedbackSummary(item.text, t) ?? item.text)}</p><QueueDestination item={item} />
        {item.attachments?.length ? <AttachmentSummary files={item.attachments} testId={`queue-attachments-${item.id}`} /> : null}
        <div className="conversation-actions">
          {/* Sends now: to the agent working at this moment (or, with nothing running, as the next message). */}
          <button className="btn subtle small" type="button" data-testid={`queue-steer-${item.id}`} title={t('queue.steerTitle')} disabled={blocked || other || !item.canSteer} onClick={() => post('chat/queue', { action: 'steer', id: item.id })}><Icon name="zap" size={12} /> {t('queue.steer')}</button>
          <button className="btn subtle small" type="button" data-testid={`queue-edit-${item.id}`} disabled={blocked} onClick={() => { capture(); setText(item.text); setEditing(item); }}>{t('queue.edit')}</button>
          <button className="icon-btn" type="button" data-testid={`queue-up-${item.id}`} title={t('queue.up')} aria-label={t('queue.up')} disabled={blocked || index === 0} onClick={() => post('chat/queue', { action: 'move', id: item.id, index: index - 1 })}><Icon name="arrowUp" size={12} /></button>
          <button className="icon-btn" type="button" data-testid={`queue-down-${item.id}`} title={t('queue.down')} aria-label={t('queue.down')} disabled={blocked || index === state.items.length - 1} onClick={() => post('chat/queue', { action: 'move', id: item.id, index: index + 1 })}><Icon name="arrowDown" size={12} /></button>
          <button className="btn subtle small danger" type="button" data-testid={`queue-remove-${item.id}`} disabled={blocked} onClick={() => post('chat/queue', { action: 'remove', id: item.id })}>{t('queue.remove')}</button>
        </div>
      </li>)}</ol>
      <Pager paged={paged} testId="queue-pager" />
      {pendingSends.map(item => <div className="queue-awaiting" key={item.clientMessageId} data-testid="queue-awaiting"><p>{revealControls(sentCaseFeedbackSummary(item.text, t) ?? item.text)}</p><small role={item.failed ? 'alert' : 'status'}>{revealControls(engineAbortText(item.failed?.message || t('queue.awaiting'), t))}</small>{item.failed && <button className="btn subtle small" type="button" onClick={() => onRecover(item)}>{t('queue.edit')}</button>}</div>)}
    </div>}
    {editing && <AgentDialog testId="queue-editor" labelledBy="queue-editor-title" className="editor-dialog" returnFocus={opener.current} onClose={() => setEditing(undefined)}>
      <div className="dialog-head"><h4 id="queue-editor-title">{t('queue.edit')}</h4></div><div className="dialog-form"><textarea data-testid="queue-edit-input" aria-label={t('queue.edit')} rows={5} value={text} onChange={event => setText(event.target.value)} /></div>
      <div className="dialog-foot"><button className="btn subtle" type="button" onClick={() => setEditing(undefined)}>{t('dialog.cancel')}</button><button className="btn primary" type="button" data-testid="queue-edit-save" disabled={blocked || editInvalid} onClick={() => { if (post('chat/queue', { action: 'edit', id: editing.id, text })) setEditing(undefined); }}>{t('session.save')}</button></div>
    </AgentDialog>}
    {clear && <AgentDialog testId="queue-clear-confirm" labelledBy="queue-clear-title" className="confirm-dialog" returnFocus={opener.current} onClose={() => setClear(false)}><h4 id="queue-clear-title">{t('queue.confirmClear')}</h4><div className="dialog-actions"><button className="btn subtle" type="button" onClick={() => setClear(false)}>{t('dialog.cancel')}</button><button className="btn danger" type="button" data-testid="queue-clear-proceed" disabled={blocked} onClick={() => { if (post('chat/queue', { action: 'clear' })) setClear(false); }}>{t('queue.clear')}</button></div></AgentDialog>}
  </div>;
}
