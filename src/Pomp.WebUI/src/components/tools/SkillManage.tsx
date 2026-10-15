import { useEffect, useRef, useState, type ReactNode } from 'react';
import { toolsTranslate, type ToolsTranslate } from '../../i18n/toolsStrings';
import { SKILL_BYTES, composeSkill } from '../../resourceBytes';
import type { ResourceDocument, ResourceScope, SkillInfo } from '../../toolsProtocol';
import type { ToolsController } from '../../toolsState';
import { Field, FormActions } from '../ui';
import { skillOn, writableSkill } from './extensionRows';
import { bytesOver, ChoiceField, ConfirmDelete, InPageDialog, ScopeChoice } from './InPageDialog';

interface CreateDraft { name: string; description: string; scope: ResourceScope; body: string }
interface Editor { skill: SkillInfo; document: ResourceDocument; description: string; body: string; content: string }
const blankCreate = (): CreateDraft => ({ name: '', description: '', scope: 'project', body: '' });

export interface SkillManager {
  t: ToolsTranslate;
  /** Writes are refused: the host is not usable, a reply is running, or a skills request is in flight. */
  locked: boolean;
  /** Every skill dialog that is open (create/import, editor, delete question). */
  dialogs: ReactNode; editing: boolean;
  /** The name of the skill created last (announced until the next create), and the skills a switch-on turned on with it (its requirements). */
  created: string; together: string[];
  /** A failed read for the editor (shown under the list while no editor is open). */
  readError: string;
  startCreate(): void; startImport(): void;
  use(skill: SkillInfo): void; edit(skill: SkillInfo): void; remove(skill: SkillInfo): void;
  open(skill: SkillInfo): void; toggle(skill: SkillInfo): void; reset(skill: SkillInfo): void;
  /** Ref callback of a list row: the row of a newly created skill takes focus. */
  rowRef(skill: SkillInfo): (element: HTMLElement | null) => void;
}

/**
 * Everything the skill rows and dialogs do. The Skills view owns the list; this owns the writes, so the rows stay presentation.
 * `onCreated` runs after a skill was created or imported (the view clears its search); `onControl` after a row control was used (the view keeps focus on it).
 */
export function useSkillManager({ controller, locale, disabled, busy, onUseSkill, onCreated, onControl }: {
  controller: ToolsController; locale: string; disabled: boolean; busy: boolean; onUseSkill: (name: string) => void; onCreated: () => void; onControl: (testId: string) => void;
}): SkillManager {
  const t = toolsTranslate(locale);
  const [importing, setImporting] = useState(false);
  const [content, setContent] = useState('');
  const [creating, setCreating] = useState(false); const [deleting, setDeleting] = useState<SkillInfo>();
  const [draft, setDraft] = useState<CreateDraft>(blankCreate);
  const [editor, setEditor] = useState<Editor>(); const [editorError, setEditorError] = useState('');
  const [validation, setValidation] = useState('');
  const [created, setCreated] = useState(''); const [focusCreated, setFocusCreated] = useState('');
  const rows = useRef(new Map<string, HTMLElement>());
  useEffect(() => {
    if (creating || !focusCreated) return;
    const row = rows.current.get(focusCreated); if (!row || row.closest('[hidden]')) return;
    const frame = window.requestAnimationFrame(() => { row.focus({ preventScroll: true }); row.scrollIntoView?.({ block: 'nearest' }); setFocusCreated(''); });
    return () => window.cancelAnimationFrame(frame);
  }, [creating, focusCreated, controller.skills.skills]);
  // Switching a skill on also switches on what it requires (the host does it): the skills that turned on besides it are named once the new list is in.
  const [together, setTogether] = useState<string[]>([]); const [replied, setReplied] = useState(0);
  const watch = useRef<{ name: string; before: Set<string>; snapshot: unknown; replied: boolean } | undefined>(undefined);
  useEffect(() => {
    const target = watch.current; if (!target || !target.replied || controller.skills === target.snapshot) return;
    watch.current = undefined;
    setTogether(controller.skills.skills.filter(skill => skillOn(skill) && skill.name !== target.name && !target.before.has(skill.name)).map(skill => skill.name).filter((name, index, all) => all.indexOf(name) === index).sort((left, right) => left.localeCompare(right, 'en')));
  }, [controller.skills, replied]);
  const pending = controller.pending('skills'); const locked = disabled || busy || pending;
  // The exact file the host will write for the open dialog: its limit counts UTF-8 bytes, not characters.
  const editForm = !!editor?.document.form && 'body' in editor.document.form;
  const createText = !creating ? '' : importing ? content : composeSkill(draft.name, draft.description.trim(), draft.body);
  const editText = !editor ? '' : editForm ? composeSkill(editor.skill.name, editor.description.trim(), editor.body) : editor.content;
  function patchDraft(patch: Partial<CreateDraft>) { setDraft(previous => ({ ...previous, ...patch })); }
  function patchEditor(patch: Partial<Editor>) { setEditor(previous => previous ? { ...previous, ...patch } : undefined); }
  async function create() {
    if (locked) return;
    if (!/^[a-z0-9-]{2,40}$/.test(draft.name)) { setValidation(t('skills.invalidName')); return; }
    const summary = draft.description.trim();
    // eslint-disable-next-line no-control-regex
    if (!importing && (summary.length < 1 || summary.length > 500 || /[\x00-\x1f\x7f-\x9f]/.test(summary))) { setValidation(t('skills.invalidDescription')); return; }
    if (importing && !content.trim()) { setValidation(t('skills.invalidContent')); return; }
    const over = bytesOver(t, createText, SKILL_BYTES);
    if (over) { setValidation(over); return; }
    setValidation('');
    const reply = await controller.command('skills', importing
      ? { action: 'import', scope: draft.scope, name: draft.name, content }
      : { action: 'create', scope: draft.scope, name: draft.name, description: summary, ...(draft.body ? { body: draft.body } : {}) });
    if (reply.ok) { onCreated(); setCreated(draft.name); setFocusCreated(`${draft.scope}:${draft.name}`); setCreating(false); }
  }
  async function read(skill: SkillInfo): Promise<ResourceDocument | undefined> {
    if (locked) return undefined;
    setEditorError('');
    const reply = await controller.command('skills', { action: 'read', ...(writableSkill(skill) ? { scope: skill.source as ResourceScope } : {}), name: skill.name });
    if (!reply.ok || !reply.document) { setEditorError(reply.error ?? t('tools.failed')); return undefined; }
    return reply.document;
  }
  async function edit(skill: SkillInfo) {
    const document = await read(skill);
    if (!document) return;
    const form = document.form && 'body' in document.form ? document.form : undefined;
    setEditor({ skill, document, description: form?.description ?? '', body: form?.body ?? '', content: document.content });
  }
  async function saveEdit() {
    if (!editor || locked || !writableSkill(editor.skill)) return;
    const form = editor.document.form && 'body' in editor.document.form ? editor.document.form : undefined;
    if (form) {
      const summary = editor.description.trim();
      // eslint-disable-next-line no-control-regex
      if (summary.length < 1 || summary.length > 500 || /[\x00-\x1f\x7f-\x9f]/.test(summary)) { setEditorError(t('skills.invalidDescription')); return; }
    } else if (!editor.content.trim()) { setEditorError(t('skills.invalidContent')); return; }
    const over = bytesOver(t, editText, SKILL_BYTES);
    if (over) { setEditorError(over); return; }
    setEditorError('');
    const reply = await controller.command('skills', { action: 'update', scope: editor.skill.source as ResourceScope, name: editor.skill.name, expectedHash: editor.document.hash,
      ...(form ? { description: editor.description.trim(), body: editor.body } : { content: editor.content }) });
    if (reply.ok) setEditor(undefined); else setEditorError(reply.error ?? t('tools.failed'));
  }
  async function remove() {
    if (locked || !deleting || !writableSkill(deleting) || !deleting.hash) return;
    const reply = await controller.command('skills', { action: 'delete', scope: deleting.source as ResourceScope, name: deleting.name, expectedHash: deleting.hash });
    if (reply.ok) setDeleting(undefined);
  }
  async function toggle(skill: SkillInfo) {
    const enabling = !skillOn(skill);
    onControl(`skill-toggle-${skill.name}`); setTogether([]);
    watch.current = enabling ? { name: skill.name, before: new Set(controller.skills.skills.filter(skillOn).map(item => item.name)), snapshot: controller.skills, replied: false } : undefined;
    const reply = await controller.command('skills', { action: enabling ? 'enable' : 'disable', name: skill.name });
    if (reply.ok && watch.current) { watch.current.replied = true; setReplied(count => count + 1); } else watch.current = undefined;
  }
  function reset(skill: SkillInfo) {
    // The reset control leaves the row once the state is the default: the focus goes to the row's switch.
    onControl(`skill-toggle-${skill.name}`); setTogether([]); watch.current = undefined;
    void controller.command('skills', { action: 'reset', name: skill.name });
  }
  const dialogs = <>
    {creating && <InPageDialog title={importing ? t('skills.importTitle') : t('skills.createTitle')} testId="skills-create-form" fallback="skill" onClose={() => setCreating(false)} pending={pending}>
      <form className="ui-form tools-form" noValidate onSubmit={event => { event.preventDefault(); void create(); }}><fieldset disabled={locked}>
        <div className="ui-field-row">
          <Field label={t('skills.name')}>{id => <input id={id} value={draft.name} maxLength={40} spellCheck={false} data-testid="skills-name" aria-invalid={!!validation} onChange={event => patchDraft({ name: event.target.value })} />}</Field>
          <ChoiceField label={t('skills.scope')}><ScopeChoice t={t} label={t('skills.scope')} testId="skills-scope" value={draft.scope} onChange={scope => patchDraft({ scope })} /></ChoiceField>
        </div>
        {importing ? <Field label="SKILL.md">{id => <textarea id={id} className="tools-mono" value={content} maxLength={32768} rows={12} spellCheck={false} data-testid="skills-import-content" onChange={event => setContent(event.target.value)} />}</Field>
          : <>
            <Field label={t('skills.description')}>{id => <input id={id} value={draft.description} maxLength={500} data-testid="skills-description" onChange={event => patchDraft({ description: event.target.value })} />}</Field>
            <Field label={t('skills.body')}>{id => <textarea id={id} className="tools-mono" value={draft.body} rows={10} spellCheck={false} data-testid="skills-body" onChange={event => patchDraft({ body: event.target.value })} />}</Field>
          </>}
      </fieldset>
        {validation && <p role="alert" className="ui-err" data-testid="skills-validation">{validation}</p>}
        <FormActions><button type="button" className="btn" disabled={pending} onClick={() => setCreating(false)}>{t('tools.cancel')}</button><button type="submit" className="btn primary" data-testid="skills-create-save" disabled={locked}>{t('tools.save')}</button></FormActions>
      </form>
    </InPageDialog>}
    {editor && <InPageDialog title={`${t('skills.editorEdit')} · ${editor.skill.name}`} testId="skills-editor" fallback="skill" onClose={() => setEditor(undefined)} pending={pending}>
      <form className="ui-form tools-form" noValidate onSubmit={event => { event.preventDefault(); void saveEdit(); }}><fieldset disabled={locked}>
        {editor.document.form && 'body' in editor.document.form ? <>
          <Field label={t('skills.description')}>{id => <input id={id} value={editor.description} maxLength={500} data-testid="skills-edit-description" onChange={event => patchEditor({ description: event.target.value })} />}</Field>
          <Field label={t('skills.body')}>{id => <textarea id={id} value={editor.body} rows={14} spellCheck={false} data-testid="skills-edit-body" className="tools-mono" onChange={event => patchEditor({ body: event.target.value })} />}</Field>
        </> : <Field label="SKILL.md">{id => <textarea id={id} className="tools-mono" value={editor.content} rows={14} spellCheck={false} data-testid="skills-edit-content" onChange={event => patchEditor({ content: event.target.value })} />}</Field>}
      </fieldset>
        {editorError && <p role="alert" className="ui-err" data-testid="skills-edit-error">{editorError}</p>}
        <FormActions><button type="button" className="btn" disabled={pending} onClick={() => setEditor(undefined)}>{t('tools.cancel')}</button><button type="submit" className="btn primary" data-testid="skills-edit-save" disabled={locked}>{t('tools.save')}</button></FormActions>
      </form>
    </InPageDialog>}
    {deleting && <ConfirmDelete t={t} testId="skills-delete-confirm" yesTestId="skills-delete-yes" fallback="skill" question={t('skills.deleteQuestion', { name: deleting.name })} locked={locked} pending={pending} onCancel={() => setDeleting(undefined)} onConfirm={() => { void remove(); }} />}
  </>;
  return {
    t, locked, dialogs, editing: creating || !!editor || !!deleting, created, together, readError: editor ? '' : editorError,
    startCreate() { setDraft(blankCreate()); setValidation(''); setImporting(false); setCreating(true); },
    startImport() { setDraft(blankCreate()); setContent(''); setValidation(''); setImporting(true); setCreating(true); },
    use: skill => onUseSkill(skill.name), edit: skill => { void edit(skill); }, remove: setDeleting,
    open: skill => { void controller.command('skills', { action: 'open', scope: writableSkill(skill) ? skill.source as ResourceScope : undefined, name: skill.name }); },
    toggle: skill => { void toggle(skill); }, reset,
    rowRef: skill => row => { const key = `${skill.source}:${skill.name}`; if (row) rows.current.set(key, row); else rows.current.delete(key); },
  };
}
