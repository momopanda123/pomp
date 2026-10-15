import { useContext, useEffect, useRef, useState } from 'react';
import { newId } from '../format';
import { useT } from '../i18n';
import { send, type AgentsReplyPayload, type MainInstructionsFile } from '../protocol';
import { REPLY_TIMEOUT_MS } from '../setupCommands';
import { MAIN_ROLE, MainRoleContext } from '../teamsCache';
import { AgentDialog } from './AgentDialog';
import { Avatar } from './AgentsTables';
import { Icon } from './Icon';
import { revealControls } from './RevealControls';
import { Badge, Segmented } from './ui';

type Scope = 'project' | 'user';
type Loaded = { scope?: Scope | null; project?: MainInstructionsFile; user: MainInstructionsFile };
/** The engine keeps the instructions it reads in its system prompt: the same limit as a role's instructions. */
const MAX_INSTRUCTIONS = 24000;

/**
 * POMP's own role: the instructions the engine appends to the chat agent (APPEND_SYSTEM.md), the project's or the user's. The engine takes
 * the project's when there is one and reads them when it starts; blank instructions remove the file.
 */
export function MainRoleEditor({ returnFocus, disabled, replies, onClose }: {
  returnFocus: HTMLElement | null; disabled: boolean; replies: AgentsReplyPayload[]; onClose: () => void;
}) {
  const t = useT(); const main = useContext(MainRoleContext);
  const [loaded, setLoaded] = useState<Loaded | null>(null);
  const [scope, setScope] = useState<Scope>('project');
  const [texts, setTexts] = useState<Record<Scope, string>>({ project: '', user: '' });
  const [error, setError] = useState<string | null>(null); const [conflict, setConflict] = useState(false);
  const [pending, setPending] = useState<{ id: string; kind: 'load' | 'save' } | null>(null);
  const field = useRef<HTMLTextAreaElement>(null);
  const load = () => {
    const id = newId(); setError(null); setConflict(false); setLoaded(null);
    if (send('agents/command', { requestId: id, action: 'mainRole' })) setPending({ id, kind: 'load' });
    else setError(t('error.hostUnavailable'));
  };
  useEffect(load, []);
  useEffect(() => {
    if (!pending) return;
    const reply = replies.find(item => item.requestId === pending.id);
    if (!reply) return;
    setPending(null);
    if (!reply.ok || (pending.kind === 'load' && !reply.main)) {
      setError(reply.error || t('agents.saveFailed'));
      if (/해시|hash|conflict/i.test(reply.error ?? '')) setConflict(true);
      return;
    }
    if (pending.kind === 'save') { onClose(); return; }
    const files = reply.main!;
    setLoaded(files); setTexts({ project: files.project?.text ?? '', user: files.user.text });
    setScope(files.project && (files.scope !== 'user' || files.project.exists) ? 'project' : 'user');
  }, [replies, pending, t, onClose]);
  useEffect(() => {
    if (!pending) return;
    const timer = window.setTimeout(() => { setPending(null); setError(t('setup.noReply')); }, REPLY_TIMEOUT_MS);
    return () => window.clearTimeout(timer);
  }, [pending, t]);
  useEffect(() => { if (loaded) field.current?.focus({ preventScroll: true }); }, [loaded, scope]);
  const file = loaded?.[scope];
  const dirty = !!file && texts[scope] !== file.text;
  // The user's file is not used while the project has one.
  const hidden = scope === 'user' && !!loaded?.project?.exists;
  const saving = pending?.kind === 'save';
  const save = () => {
    if (!file || !dirty || pending || disabled) return;
    const id = newId(); setError(null); setConflict(false);
    if (send('agents/command', { requestId: id, action: 'saveMain', scope, instructions: texts[scope], ...(file.hash ? { expectedHash: file.hash } : {}) })) setPending({ id, kind: 'save' });
    else setError(t('error.hostUnavailable'));
  };
  return <AgentDialog testId="main-role-editor" className="editor-dialog" labelledBy="main-role-editor-title" returnFocus={returnFocus} onClose={() => { if (!saving) onClose(); }}>
    <div className="dialog-head">
      <h4 id="main-role-editor-title">{t('agents.editRole')}</h4>
      <button type="button" className="icon-btn" disabled={saving} aria-label={t('agents.close')} onClick={onClose}><Icon name="x" size={13} /></button>
    </div>
    {error && <div className="dialog-error" role="alert">
      <Icon name="error" size={12} /><span>{revealControls(error)}</span>
      {conflict && <button type="button" className="btn subtle small" disabled={!!pending} onClick={load}>{t('agents.reload')}</button>}
    </div>}
    <div className="dialog-form">
      <div className="main-role-identity"><Avatar name={MAIN_ROLE.name} label={MAIN_ROLE.title} look={main?.avatar} scope={MAIN_ROLE.scope} /><strong>{MAIN_ROLE.title}</strong></div>
      <div className="form-field">
        <label id="main-role-scope-label">{t('agents.scopeLabel')}</label>
        <div className="main-role-scope">
          <Segmented<Scope> label={t('agents.scopeLabel')} testId="main-role-scope" value={scope} disabled={!loaded || !!pending || dirty} onChange={setScope} options={[
            ...(loaded?.project ? [{ id: 'project' as const, label: t('agents.scope.project'), testId: 'main-role-scope-project' }] : []),
            { id: 'user', label: t('agents.scope.user'), testId: 'main-role-scope-user' },
          ]} />
          {hidden && <Badge title={t('agents.shadowedProject')} testId="main-role-hidden">{t('agents.hidden')}</Badge>}
        </div>
      </div>
      <div className="form-field">
        <label htmlFor="main-role-instructions">{t('agents.instrLabel')}</label>
        <textarea ref={field} id="main-role-instructions" data-testid="main-role-instructions" maxLength={MAX_INSTRUCTIONS} rows={10} disabled={!loaded || saving}
          value={texts[scope]} onChange={event => { const value = event.target.value; setTexts(previous => ({ ...previous, [scope]: value })); }} />
      </div>
    </div>
    <div className="dialog-foot">
      <button type="button" className="btn subtle" disabled={saving} onClick={onClose}>{t('dialog.cancel')}</button>
      <button type="button" className="btn primary" data-testid="main-role-save" disabled={!dirty || !!pending || disabled} onClick={save}>
        {saving ? <span className="spinner" /> : t('agents.save')}
      </button>
    </div>
  </AgentDialog>;
}
