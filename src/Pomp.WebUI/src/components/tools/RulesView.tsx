import { useState, type CSSProperties } from 'react';
import type { ActiveRule } from '../../protocol';
import { toolsTranslate } from '../../i18n/toolsStrings';
import { RULE_BYTES, composeRule } from '../../resourceBytes';
import type { ResourceDocument, ResourceScope, RuleInfo } from '../../toolsProtocol';
import type { ToolsController } from '../../toolsState';
import { Icon } from '../Icon';
import { revealControls } from '../RevealControls';
import { SectionRefresh, ViewSection } from '../ViewSection';
import { Badge, DataTable, Field, FormActions, IconButton, matches, Pager, SearchBox, Switch, Toolbar, usePaged } from '../ui';
import { bytesOver, ChoiceField, ConfirmDelete, InPageDialog, ScopeChoice } from './InPageDialog';

interface Draft { mode: 'create' | 'import'; name: string; scope: ResourceScope; description: string; alwaysApply: boolean; globs: string; body: string; content: string }
interface Editor { rule: RuleInfo; document: ResourceDocument; description: string; alwaysApply: boolean; globs: string; body: string; content: string }
const blank = (mode: Draft['mode']): Draft => ({ mode, name: '', scope: 'project', description: '', alwaysApply: false, globs: '', body: '', content: '' });
// eslint-disable-next-line no-control-regex
const control = /[\x00-\x1f\x7f]/;
/** Applied rules beyond this many wait behind a +N tag. */
const ACTIVE_SHOWN = 12;
const lines = (text: string) => text.split(/\r?\n/).map(glob => glob.trim()).filter(Boolean);

/**
 * Tools › Rules: the rules applied in this conversation now (live state from the engine) and the project and user rule files, which are
 * created, imported, edited, opened in VS and deleted here. Create never overwrites; edit names the hash the page last saw.
 */
export function RulesView({ controller, locale, disabled, busy, active }: { controller: ToolsController; locale: string; disabled: boolean; busy: boolean; active?: ActiveRule[] }) {
  const t = toolsTranslate(locale);
  const [draft, setDraft] = useState<Draft>(); const [editor, setEditor] = useState<Editor>(); const [editorError, setEditorError] = useState('');
  const [deleting, setDeleting] = useState<RuleInfo>(); const [validation, setValidation] = useState(''); const [query, setQuery] = useState('');
  const [allActive, setAllActive] = useState(false);
  const pending = controller.pending('rules'); const locked = disabled || busy || pending;
  const update = (patch: Partial<Draft>) => setDraft(previous => previous ? { ...previous, ...patch } : previous);
  const patchEditor = (patch: Partial<Editor>) => setEditor(previous => previous ? { ...previous, ...patch } : undefined);
  const globs = draft ? lines(draft.globs) : [];
  // The exact file the host will write for the open dialog: its limit counts UTF-8 bytes, not characters.
  const editForm = !!editor?.document.form && 'alwaysApply' in editor.document.form;
  const editLines = editor ? lines(editor.globs) : [];
  const createText = !draft ? '' : draft.mode === 'import' ? draft.content : composeRule(draft.name.trim(), draft.description.trim(), draft.alwaysApply, globs, draft.body);
  const editText = !editor ? '' : editForm ? composeRule(editor.rule.name, editor.description.trim(), editor.alwaysApply, editLines, editor.body) : editor.content;
  async function save() {
    if (!draft || locked) return;
    const name = draft.name.trim();
    const valid = /^[a-z0-9-]{2,40}$/.test(name) && (draft.mode === 'import'
      ? draft.content.trim().length > 0
      : draft.description.length <= 500 && !control.test(draft.description) && globs.length <= 32 && globs.every(glob => glob.length <= 200 && !control.test(glob)));
    if (!valid) { setValidation(t('rules.invalid')); return; }
    const over = bytesOver(t, createText, RULE_BYTES);
    if (over) { setValidation(over); return; }
    setValidation('');
    const reply = await controller.command('rules', draft.mode === 'import'
      ? { action: 'import', scope: draft.scope, name, content: draft.content }
      : { action: 'create', scope: draft.scope, name, description: draft.description.trim(), alwaysApply: draft.alwaysApply, globs, ...(draft.body ? { body: draft.body } : {}) });
    if (reply.ok) { setDraft(undefined); setQuery(''); } // The pasted content leaves the page with the dialog.
  }
  async function edit(rule: RuleInfo) {
    if (locked) return;
    setEditorError('');
    const reply = await controller.command('rules', { action: 'read', scope: rule.scope, name: rule.name });
    if (!reply.ok || !reply.document) { setEditorError(reply.error ?? t('tools.failed')); return; }
    const document = reply.document;
    const form = document.form && 'alwaysApply' in document.form ? document.form : undefined;
    setDraft(undefined);
    setEditor({ rule, document, description: form?.description ?? '', alwaysApply: form?.alwaysApply ?? false, globs: form ? form.globs.join('\n') : '', body: form?.body ?? '', content: document.content });
  }
  async function saveEdit() {
    if (!editor || locked) return;
    const form = editor.document.form && 'alwaysApply' in editor.document.form ? editor.document.form : undefined;
    if (form) {
      // The body is what the rule says; the host refuses an empty one.
      if (editor.description.length > 500 || control.test(editor.description) || editLines.length > 32 || editLines.some(glob => glob.length > 200 || control.test(glob)) || !editor.body.trim()) { setEditorError(t('rules.invalid')); return; }
    } else if (!editor.content.trim()) { setEditorError(t('rules.invalid')); return; }
    const over = bytesOver(t, editText, RULE_BYTES);
    if (over) { setEditorError(over); return; }
    setEditorError('');
    const reply = await controller.command('rules', { action: 'update', scope: editor.rule.scope, name: editor.rule.name, expectedHash: editor.document.hash,
      ...(form ? { description: editor.description.trim(), alwaysApply: editor.alwaysApply, globs: editLines, body: editor.body } : { content: editor.content }) });
    if (reply.ok) setEditor(undefined); else setEditorError(reply.error ?? t('tools.failed'));
  }
  async function remove() {
    if (locked || !deleting) return;
    const reply = await controller.command('rules', { action: 'delete', scope: deleting.scope, name: deleting.name, expectedHash: deleting.hash });
    if (reply.ok) setDeleting(undefined);
  }
  const rules = controller.rules.rules.filter(rule => matches(query, rule.name, rule.description, ...rule.globs))
    .toSorted((left, right) => left.scope.localeCompare(right.scope) || left.name.localeCompare(right.name, 'en', { numeric: true }));
  const paged = usePaged(rules, query);
  const applies = (rule: RuleInfo) => rule.alwaysApply ? <Badge tone="info">{t('rules.alwaysShort')}</Badge>
    : rule.globs.length ? <Badge mono title={rule.globs.join('\n')}>{rule.globs.join(', ')}</Badge> : <Badge tone="warn" title={t('rules.noTarget')}>{t('rules.noTargetShort')}</Badge>;
  const scopeBadge = (scope: ResourceScope) => <Badge tone={scope === 'project' ? 'accent' : 'neutral'}>{t(scope === 'project' ? 'skills.kind.project' : 'skills.kind.user')}</Badge>;
  return <div className="tools-scroll-body" data-testid="rules-view">
    <ViewSection sectionKey="tools-rules-active" title={t('tools.activeRules')} status={active?.length ?? 0} data-testid="active-rules" bodyClassName="ui-flush">
      {active?.length ? <div className="tools-tags" role="list">{(allActive ? active : active.slice(0, ACTIVE_SHOWN)).map((rule, index) => <span role="listitem" key={`${rule.file}:${index}`} className="tools-tag" title={[rule.file, rule.always ? t('rules.always') : rule.globs.join(', ')].filter(Boolean).join('\n')} data-testid={`active-rule-${rule.name}`}>
        <Icon name="book" size={12} />{revealControls(rule.name)}{!rule.always && rule.globs.length > 0 && <span className="ui-muted ui-mono">{rule.globs.join(', ')}</span>}</span>)}
        {active.length > ACTIVE_SHOWN && <button type="button" className="tools-tag tools-tag-more" data-testid="active-rules-more" aria-expanded={allActive} onClick={() => setAllActive(value => !value)}>
          {allActive ? <><Icon name="chevronLeft" size={12} />{t('section.collapse')}</> : <>+{active.length - ACTIVE_SHOWN}</>}</button>}</div>
        : <div className="ui-empty-rows" aria-hidden="true" style={{ '--ui-empty-rows': 1 } as CSSProperties} />}
    </ViewSection>
    <ViewSection sectionKey="tools-rules-files" title={t('tools.rules')} status={controller.rules.rules.length} bodyClassName="ui-flush"
      actions={<>
        <button type="button" className="btn small subtle" data-testid="rules-import" disabled={locked} onClick={() => { setValidation(''); setEditor(undefined); setDraft(blank('import')); }}><Icon name="download" size={12} />{t('tools.import')}</button>
        <button type="button" className="btn small" data-testid="rules-create" data-tools-dialog-fallback disabled={locked} onClick={() => { setValidation(''); setEditor(undefined); setDraft(blank('create')); }}><Icon name="plus" size={12} />{t('rules.create')}</button>
        <SectionRefresh onClick={() => { void controller.command('rules', { action: 'refresh' }); }} disabled={disabled || pending} testId="rules-refresh" />
      </>}>
      {draft && <InPageDialog title={t(draft.mode === 'import' ? 'rules.editorImport' : 'rules.editorCreate')} testId="rules-editor" onClose={() => setDraft(undefined)} pending={pending}>
        <form className="ui-form tools-form" noValidate onSubmit={event => { event.preventDefault(); void save(); }}><fieldset disabled={locked}>
          <div className="ui-field-row">
            <Field label={t('rules.name')}>{id => <input id={id} value={draft.name} spellCheck={false} data-testid="rules-name" maxLength={40} onChange={event => update({ name: event.target.value })} />}</Field>
            <ChoiceField label={t('skills.scope')}><ScopeChoice t={t} label={t('skills.scope')} testId="rules-scope" value={draft.scope} onChange={scope => update({ scope })} /></ChoiceField>
          </div>
          {draft.mode === 'create' ? <>
            <Field label={t('rules.description')}>{id => <input id={id} value={draft.description} data-testid="rules-description" maxLength={500} onChange={event => update({ description: event.target.value })} />}</Field>
            <ChoiceField label={t('rules.always')}><Switch checked={draft.alwaysApply} label={t('rules.always')} showLabel={false} testId="rules-always" onChange={alwaysApply => update({ alwaysApply })} /></ChoiceField>
            <Field label={t('rules.globs')}>{id => <textarea id={id} className="tools-mono" value={draft.globs} data-testid="rules-globs" rows={3} spellCheck={false} disabled={draft.alwaysApply} onChange={event => update({ globs: event.target.value })} />}</Field>
            {!draft.alwaysApply && !globs.length && <p role="status" className="ui-warn">{t('rules.neverApplies')}</p>}
            <Field label={t('rules.body')}>{id => <textarea id={id} className="tools-mono" value={draft.body} data-testid="rules-body" rows={8} spellCheck={false} onChange={event => update({ body: event.target.value })} />}</Field>
          </> : <Field label={t('rules.content')}>{id => <textarea id={id} className="tools-mono" value={draft.content} data-testid="rules-content" rows={12} spellCheck={false} maxLength={32768} onChange={event => update({ content: event.target.value })} />}</Field>}
        </fieldset>
          {validation && <p role="alert" className="ui-err" data-testid="rules-validation">{validation}</p>}
          <FormActions><button type="button" className="btn" disabled={pending} onClick={() => setDraft(undefined)}>{t('tools.cancel')}</button>
            <button type="submit" className="btn primary" data-testid="rules-save" disabled={locked}>{t(draft.mode === 'import' ? 'rules.importSubmit' : 'rules.save')}</button></FormActions>
        </form>
      </InPageDialog>}
      {editor && <InPageDialog title={`${t('rules.editorEdit')} · ${editor.rule.name}`} testId="rules-edit-form" onClose={() => setEditor(undefined)} pending={pending}>
        <form className="ui-form tools-form" noValidate onSubmit={event => { event.preventDefault(); void saveEdit(); }}><fieldset disabled={locked}>
          {editForm ? <>
            <Field label={t('rules.description')}>{id => <input id={id} value={editor.description} data-testid="rules-edit-description" maxLength={500} onChange={event => patchEditor({ description: event.target.value })} />}</Field>
            <ChoiceField label={t('rules.always')}><Switch checked={editor.alwaysApply} label={t('rules.always')} showLabel={false} testId="rules-edit-always" onChange={alwaysApply => patchEditor({ alwaysApply })} /></ChoiceField>
            <Field label={t('rules.globs')}>{id => <textarea id={id} className="tools-mono" value={editor.globs} data-testid="rules-edit-globs" rows={3} spellCheck={false} disabled={editor.alwaysApply} onChange={event => patchEditor({ globs: event.target.value })} />}</Field>
            <Field label={t('rules.body')}>{id => <textarea id={id} value={editor.body} data-testid="rules-edit-body" rows={12} spellCheck={false} className="tools-mono" maxLength={32768} onChange={event => patchEditor({ body: event.target.value })} />}</Field>
          </> : <Field label={t('rules.content')}>{id => <textarea id={id} className="tools-mono" value={editor.content} data-testid="rules-edit-content" rows={14} spellCheck={false} onChange={event => patchEditor({ content: event.target.value })} />}</Field>}
        </fieldset>
          {editorError && <p role="alert" className="ui-err" data-testid="rules-edit-validation">{editorError}</p>}
          <FormActions><button type="button" className="btn" disabled={pending} onClick={() => setEditor(undefined)}>{t('tools.cancel')}</button>
            <button type="submit" className="btn primary" data-testid="rules-edit-save" disabled={locked}>{t('tools.save')}</button></FormActions>
        </form>
      </InPageDialog>}
      {deleting && <ConfirmDelete t={t} testId="rules-delete-confirm" yesTestId="rules-delete-yes" question={t('rules.deleteQuestion', { name: deleting.name })} locked={locked} pending={pending} onCancel={() => setDeleting(undefined)} onConfirm={() => { void remove(); }} />}
      <Toolbar><SearchBox value={query} onChange={setQuery} testId="rules-search" /></Toolbar>
      {editorError && !editor && <p className="ui-note" role="alert" data-testid="rules-edit-error">{editorError}</p>}
      {controller.errors('rules').map((error, index) => <p className="ui-note" role="alert" key={index}>{error}</p>)}
      {controller.rules.diagnostics.map((message, index) => <p className="ui-note ui-muted" key={'d' + index}>{message}</p>)}
      <div className="tools-narrow" style={{ '--ui-cols-narrow': 'minmax(0, 1fr) auto auto' } as CSSProperties}>
        <DataTable columns="minmax(0, 1fr) auto auto auto" label={t('tools.rules')} testId="rules-table" empty={!rules.length} emptyRows={3}
          head={[t('tcol.name'), t('tcol.applies'), <span className="narrow-hide">{t('tcol.scope')}</span>, '']}>
          {paged.slice.map(rule => <div className="ui-tr" role="row" key={`${rule.scope}:${rule.name}`} data-testid={`rules-row-${rule.scope}-${rule.name}`}>
            <span role="cell" className="ui-cell"><span className="tools-name-line"><strong>{rule.name}</strong><span className="inline-kind">{scopeBadge(rule.scope)}</span></span>
              {rule.description && <span className="ui-sub" title={rule.description}>{rule.description}</span>}
              {(rule.note || rule.unreadable) && <span role="note" className="ui-sub ui-warn" title={rule.note || t('tools.unreadable')}>{rule.note || t('tools.unreadable')}</span>}</span>
            {/* What an unreadable file applies to is not known, so no target badge. */}
            <span role="cell" className="ui-cell tools-applies">{!rule.unreadable && applies(rule)}</span>
            <span role="cell" className="ui-cell narrow-hide">{scopeBadge(rule.scope)}</span>
            <span role="cell" className="ui-cell ui-actions">
              {!rule.unreadable && <IconButton icon="edit" label={t('rules.edit')} testId={`rules-edit-${rule.scope}-${rule.name}`} disabled={locked} onClick={() => { void edit(rule); }} />}
              <IconButton icon="external" label={t('rules.open')} testId={`rules-open-${rule.scope}-${rule.name}`} disabled={locked} onClick={() => { void controller.command('rules', { action: 'open', scope: rule.scope, name: rule.name }); }} />
              <IconButton icon="trash" tone="danger" label={t('tools.delete')} testId={`rules-delete-${rule.scope}-${rule.name}`} disabled={locked} onClick={() => { setEditor(undefined); setDraft(undefined); setDeleting(rule); }} />
            </span>
          </div>)}
        </DataTable>
      </div>
      <Pager paged={paged} testId="rules-pager" />
    </ViewSection>
  </div>;
}
