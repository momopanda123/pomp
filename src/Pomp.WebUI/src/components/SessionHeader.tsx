import { useEffect, useRef, useState, type ReactNode } from 'react';
import { useT } from '../i18n';
import type { SessionInfoPayload } from '../protocol';
import type { SessionHistoryController } from '../sessionHistory';
import { Icon } from './Icon';
import { revealControls } from './RevealControls';

/** Empty names remain placeholders, never editable translated text. */
export function SessionRename({ name, disabled, onSave, onCancel }: { name: string; disabled: boolean; onSave: (name: string) => boolean; onCancel: () => void }) {
  const t = useT(); const [value, setValue] = useState(name);
  return <form className="session-rename" onSubmit={event => { event.preventDefault(); if (onSave(value)) onCancel(); }}>
    <input autoFocus data-testid="session-rename-input" aria-label={t('session.name')} value={value} maxLength={120} disabled={disabled} placeholder={t('session.untitled')} onFocus={event => event.currentTarget.select()} onChange={event => setValue(event.target.value)} onKeyDown={event => {
      if (event.nativeEvent.isComposing || event.keyCode === 229) return;
      if (event.key === 'Escape') { event.preventDefault(); event.stopPropagation(); onCancel(); }
    }} />
    <button className="icon-btn" type="submit" disabled={disabled || !value.trim()} title={t('session.save')} aria-label={t('session.save')}><Icon name="check" /></button>
    <button className="icon-btn" type="button" onClick={onCancel} title={t('session.cancel')} aria-label={t('session.cancel')}><Icon name="x" /></button>
  </form>;
}

export function SessionHeader({ session, history, disabled, onNew, listOpen, onToggleList, children }: {
  session: SessionInfoPayload | null; history: SessionHistoryController; disabled: boolean; onNew: () => void;
  listOpen: boolean; onToggleList: () => void; children?: ReactNode;
}) {
  const t = useT(); const [editing, setEditing] = useState(false); const nameButton = useRef<HTMLButtonElement>(null);
  const realName = (history.state.current?.sessionId === session?.sessionId ? history.state.current?.name || session?.sessionName : session?.sessionName) || '';
  useEffect(() => { setEditing(false); }, [session?.sessionId]);
  return <div className="session-header" data-testid="session-header">
    {editing ? <SessionRename name={realName} disabled={disabled || history.pending} onSave={value => history.command('rename', value)} onCancel={() => { setEditing(false); window.requestAnimationFrame(() => nameButton.current?.focus()); }} />
      : <button ref={nameButton} className="session-name" type="button" disabled={disabled || !session?.sessionId} title={t('session.rename')} data-testid="session-name" onClick={() => setEditing(true)}>{revealControls(realName || t('session.untitled'))}</button>}
    <button className="icon-btn" type="button" data-testid="session-quick" aria-label={t('session.quick')} title={t('session.quick')} aria-controls="conversation-list" aria-expanded={listOpen} onClick={onToggleList}><Icon name="chevronDown" size={12} /></button>
    <button className="icon-btn" type="button" data-testid="new-session" disabled={disabled || history.pending} title={t('status.newSession')} aria-label={t('status.newSession')} onClick={onNew}><Icon name="plus" /></button>
    {children}
    {history.error && !listOpen && <span className="session-header-error" role="alert">{revealControls(history.error)}</span>}
  </div>;
}
