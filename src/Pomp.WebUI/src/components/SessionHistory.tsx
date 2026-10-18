import { useEffect, useRef, useState } from 'react';
import { sentCaseFeedbackSummary } from '../caseFeedbackMessage';
import { useT } from '../i18n';
import type { SessionHistoryController } from '../sessionHistory';
import { useRoleTitle, useTeamTitle } from '../teamsCache';
import type { ConversationPartner } from '../conversationProtocol';
import { ConfirmPopover } from './ConfirmPopover';
import { Icon } from './Icon';
import { revealControls } from './RevealControls';
import { SessionRename } from './SessionHeader';
import { Pager, usePaged } from './ui';

function PartnerBadge({ partner }: { partner: ConversationPartner }) {
  const t = useT(); const role = useRoleTitle(partner.id || ''); const team = useTeamTitle(partner.id || '');
  const name = partner.kind === 'main' ? t('partner.main') : partner.kind === 'agent' ? role : team;
  return <span className="session-partner-badge" data-testid="session-partner-badge">{revealControls(t('partner.badge', { name }))}</span>;
}

/** The conversation list: a dropdown under the session header that closes on an outside press, Escape or a choice; previews expand under their row. */
export function SessionHistory({ locale, sessionId, sessionName, history, disabled, onNew, onOpened, onClose }: {
  locale: string; sessionId?: string; sessionName?: string; history: SessionHistoryController;
  disabled: boolean; onNew: () => void; onOpened: () => void; onClose?: () => void;
}) {
  const t = useT(); const [query, setQuery] = useState(''); const [editing, setEditing] = useState(false);
  const [deleting, setDeleting] = useState<{ id: string; name: string }>();
  const deleteAnchor = useRef<HTMLDivElement>(null);
  const [menu, setMenu] = useState<string>(); const [previewId, setPreviewId] = useState<string>();
  const opener = useRef<HTMLButtonElement | null>(null);
  const cancelDelete = () => { setDeleting(undefined); window.requestAnimationFrame(() => opener.current?.focus()); };
  const closeMenu = () => { setMenu(undefined); window.requestAnimationFrame(() => opener.current?.focus()); };
  useEffect(() => { if (menu) document.querySelector<HTMLButtonElement>('[data-testid="sessions-panel"] [role="menuitem"]:not(:disabled)')?.focus(); }, [menu]);
  // A short list needs no filter field (like the other pickers): the search appears only past ten conversations. Opening puts the
  // keyboard on the search when there is one, else on the current conversation's row.
  // A filter in use keeps its field, so a list that shrinks to ten or fewer while filtered can still be cleared.
  const searchable = history.state.entries.length > 10 || query !== '';
  const panel = useRef<HTMLDivElement>(null);
  useEffect(() => {
    const root = panel.current;
    (root?.querySelector<HTMLElement>('[data-testid="session-search"]') ?? root?.querySelector<HTMLElement>('li.current [data-testid="session-open"]') ?? root?.querySelector<HTMLElement>('[data-testid="session-open"]'))?.focus({ preventScroll: true });
  }, [searchable]);
  useEffect(() => { setEditing(false); setDeleting(undefined); setMenu(undefined); setPreviewId(undefined); }, [sessionId]);
  const entries = history.state.entries.filter(entry => `${entry.title ?? ''} ${entry.name ?? ''} ${entry.sessionId ?? ''} ${entry.partner?.id ?? ''}`.toLocaleLowerCase().includes(query.toLocaleLowerCase()));
  const paged = usePaged(entries, query);
  const date = (value?: string) => value && Number.isFinite(Date.parse(value)) ? new Date(value).toLocaleString(locale) : '';
  return <div ref={panel} className="sessions-panel" data-testid="sessions-panel" onKeyDown={event => {
    if (event.nativeEvent.isComposing || event.keyCode === 229 || event.key !== 'Escape') return;
    event.preventDefault(); event.stopPropagation(); if (deleting) cancelDelete(); else if (menu) closeMenu(); else onClose?.();
  }}>
    <div className="sessions-toolbar"><strong>{t('session.list')}</strong>
      <button className="icon-btn" type="button" disabled={history.pending} title={t('session.refresh')} aria-label={t('session.refresh')} onClick={() => history.command('refresh')}><Icon name="restart" /></button>
      <button className="icon-btn" type="button" data-testid="sessions-new" disabled={disabled || history.pending} onClick={onNew} title={t('status.newSession')} aria-label={t('status.newSession')}><Icon name="plus" /></button>
    </div>
    {searchable && <input className="session-search" type="search" data-testid="session-search" aria-label={t('session.search')} placeholder={t('session.search')} value={query} onChange={event => setQuery(event.target.value)} />}
    {disabled && <p className="hint" data-testid="session-switch-blocked">{t('session.blocked')}</p>}
    {history.pending && <p role="status">{t('session.loading')}</p>}{history.error && <p role="alert">{revealControls(history.error)}</p>}
    <ol className="session-list" data-testid="session-list">{paged.slice.map(entry => {
      const current = entry.current === true || entry.sessionId === sessionId;
      const preview = previewId === entry.id && history.state.preview?.id === entry.id ? history.state.preview : undefined;
      return <li key={entry.id} className={current ? 'current' : ''} data-testid={`session-row-${entry.id}`} aria-busy={history.pendingId === entry.id || undefined}>
        <div className="session-row-main">
          <button className="session-row-open" type="button" data-testid="session-open" disabled={history.pending || (!current && disabled)} onClick={() => { if (current) onOpened(); else if (history.command('switch', entry.id)) onOpened(); }}>
            <strong className="session-title">{current && <Icon name="check" size={12} />}{revealControls(entry.title || entry.name || entry.sessionId || entry.id)}</strong>
            <span className="session-meta">{date(entry.modifiedUtc || entry.visitedUtc)}{entry.messageCount !== undefined && <> · {t('session.messages', { count: entry.messageCount })}</>}</span>
            <PartnerBadge partner={entry.partner ?? { kind: 'main' }} />
          </button>
          <button type="button" className="icon-btn" data-testid="session-row-menu" disabled={history.pending} title={t('session.actions')} aria-label={t('session.actions')} aria-expanded={menu === entry.id} aria-haspopup="menu" onClick={event => { opener.current = event.currentTarget; setMenu(menu === entry.id ? undefined : entry.id); }}><Icon name="more" /></button>
        </div>
        {menu === entry.id && <div className="session-row-actions" role="menu" onKeyDown={event => {
          if (event.nativeEvent.isComposing || event.keyCode === 229) return;
          const items = Array.from(event.currentTarget.querySelectorAll<HTMLButtonElement>('[role="menuitem"]:not(:disabled)'));
          if (!items.length || !['ArrowDown', 'ArrowUp', 'Home', 'End'].includes(event.key)) return;
          event.preventDefault(); const index = items.indexOf(document.activeElement as HTMLButtonElement);
          items[event.key === 'Home' ? 0 : event.key === 'End' ? items.length - 1 : (index + (event.key === 'ArrowDown' ? 1 : -1) + items.length) % items.length]?.focus();
        }}>
          <button className="btn subtle" type="button" role="menuitem" data-testid="session-preview" disabled={history.pending} onClick={() => {
            closeMenu(); if (previewId === entry.id) setPreviewId(undefined); else if (history.command('preview', entry.id)) setPreviewId(entry.id);
          }}>{t('session.preview')}</button>
          {current && <button className="btn subtle" type="button" role="menuitem" data-testid="session-rename" disabled={disabled || history.pending} onClick={() => { setMenu(undefined); setEditing(true); }}>{t('session.rename')}</button>}
          <button className="btn subtle danger" type="button" role="menuitem" data-testid="session-delete" disabled={disabled || history.pending} onClick={() => { setMenu(undefined); setDeleting({ id: entry.id, name: entry.title || entry.name || entry.sessionId || entry.id }); }}>{t('session.delete')}</button>
        </div>}
        {preview && <section className="session-preview" data-testid="session-preview-content" aria-label={t('session.preview')}>
          {preview.messages.map((message, index) => <article key={index}><strong>{message.role === 'user' ? t('session.roleUser') : t('session.roleAssistant')}</strong><p>{revealControls((message.role === 'user' && sentCaseFeedbackSummary(message.text, t)) || message.text)}</p>{message.truncated && <span className="hint">{t('session.messageTruncated')}</span>}</article>)}
          {preview.truncated && <p className="hint">{t('history.truncated')}</p>}
        </section>}
      </li>;
    })}</ol>
    <Pager paged={paged} testId="session-pager" />
    {(history.state.hidden ?? 0) > 0 && <p role="status" data-testid="session-hidden">{t('session.hidden', { count: history.state.hidden! })}</p>}

    {deleting && <div ref={deleteAnchor} className="session-delete-anchor">
      <ConfirmPopover anchorRef={deleteAnchor} testId="session-delete-confirm" label={t('session.delete')}
        question={t('session.deleteQuestion', { name: deleting.name })} cancelLabel={t('session.cancel')} confirmLabel={t('session.delete')}
        cancelTestId="session-delete-cancel" confirmTestId="session-delete-accept" className="session-delete-confirm" enterConfirms
        confirmDisabled={disabled || history.pending} onCancel={cancelDelete} onConfirm={() => { if (history.command('delete', deleting.id)) setDeleting(undefined); }} />
    </div>}
    {editing && <SessionRename name={sessionName || history.state.current?.name || ''} disabled={disabled || history.pending} onSave={value => history.command('rename', value)} onDone={restoreFocus => { setEditing(false); if (restoreFocus) window.requestAnimationFrame(() => opener.current?.focus()); }} />}
  </div>;
}
