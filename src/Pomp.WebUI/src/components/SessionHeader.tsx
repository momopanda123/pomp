import { useEffect, useRef, useState, type ReactNode } from 'react';
import { useT } from '../i18n';
import type { SessionInfoPayload } from '../protocol';
import type { SessionHistoryController } from '../sessionHistory';
import { Icon } from './Icon';
import { revealControls } from './RevealControls';
import { useStatusReport } from '../statusBar';

/**
 * Inline rename, like renaming a file in an explorer: Enter or a click anywhere else saves a changed name, Escape (or an unchanged or
 * empty name) leaves it as it was. No confirm/cancel buttons. `onDone(restoreFocus)`: true after Enter/Escape (the keyboard stays in
 * the header), false after a click elsewhere (focus stays where the user clicked). Empty names remain placeholders.
 */
export function SessionRename({ name, disabled, onSave, onDone }: { name: string; disabled: boolean; onSave: (name: string) => boolean; onDone: (restoreFocus: boolean) => void }) {
  const t = useT(); const [value, setValue] = useState(name); const finished = useRef(false);
  const finish = (save: boolean, restoreFocus: boolean): void => {
    if (finished.current) return;
    finished.current = true;
    const next = value.trim();
    if (save && next && next !== name.trim() && !disabled) onSave(next);
    onDone(restoreFocus);
  };
  return <input autoFocus className="session-rename" data-testid="session-rename-input" aria-label={t('session.name')} value={value} maxLength={120} disabled={disabled}
    placeholder={t('session.untitled')} onFocus={event => event.currentTarget.select()} onChange={event => setValue(event.target.value)}
    onBlur={() => finish(true, false)} onKeyDown={event => {
      if (event.nativeEvent.isComposing || event.keyCode === 229) return;
      if (event.key === 'Enter') { event.preventDefault(); finish(true, true); }
      else if (event.key === 'Escape') { event.preventDefault(); event.stopPropagation(); finish(false, true); }
    }} />;
}

export function SessionHeader({ session, history, disabled, onNew, listOpen, onToggleList, children }: {
  session: SessionInfoPayload | null; history: SessionHistoryController; disabled: boolean; onNew: () => void;
  listOpen: boolean; onToggleList: () => void; children?: ReactNode;
}) {
  const t = useT(); const [editing, setEditing] = useState(false); const nameButton = useRef<HTMLButtonElement>(null);
  const realName = (history.state.current?.sessionId === session?.sessionId ? history.state.current?.name || session?.sessionName : session?.sessionName) || '';
  useEffect(() => { setEditing(false); }, [session?.sessionId]);
  // A failed rename or conversation-list request is reported in the VS status bar, not as a line under the header (the open list
  // shows its own error).
  useStatusReport(listOpen ? null : history.error);
  return <div className="session-header" data-testid="session-header">
    {editing ? <SessionRename name={realName} disabled={disabled || history.pending} onSave={value => history.command('rename', value)} onDone={restoreFocus => { setEditing(false); if (restoreFocus) window.requestAnimationFrame(() => nameButton.current?.focus()); }} />
      : <button ref={nameButton} className="session-name" type="button" disabled={disabled || !session?.sessionId} title={t('session.rename')} data-testid="session-name" onClick={() => setEditing(true)}>{revealControls(realName || t('session.untitled'))}</button>}
    <button className="icon-btn" type="button" data-testid="session-quick" aria-label={t('session.quick')} title={t('session.quick')} aria-controls="conversation-list" aria-expanded={listOpen} onClick={onToggleList}><Icon name="chevronDown" size={12} /></button>
    <button className="icon-btn" type="button" data-testid="new-session" disabled={disabled || history.pending} title={t('status.newSession')} aria-label={t('status.newSession')} onClick={onNew}><Icon name="plus" /></button>
    {children}
  </div>;
}
