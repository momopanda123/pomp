import { useEffect, useRef, useState, type CSSProperties } from 'react';
import { newId } from '../../format';
import { toolsTranslate } from '../../i18n/toolsStrings';
import { onMessage, send } from '../../protocol';
import { WORKFLOW_BYTES, composeWorkflow } from '../../resourceBytes';
import type { ResourceScope, WorkflowInfo } from '../../toolsProtocol';
import type { ToolsController } from '../../toolsState';
import { useWorkState } from '../../work/store';
import { Icon } from '../Icon';
import { SectionRefresh, ViewSection } from '../ViewSection';
import { Badge, DataTable, Field, FormActions, IconButton, matches, Pager, SearchBox, Toolbar, usePaged } from '../ui';
import { bytesOver, ChoiceField, ConfirmDelete, InPageDialog, ScopeChoice } from './InPageDialog';

interface Editor { original?: WorkflowInfo; name: string; scope: ResourceScope; description: string; task: string; steps: string }
/** The fields as the host receives them. */
function fieldsOf(editor: Editor) {
  return { name: editor.name.trim(), description: editor.description.trim(), task: editor.task.replace(/\r\n/g, '\n'), steps: editor.steps.split('\n').map(step => step.trim()).filter(Boolean) };
}
function editorFor(copy?: WorkflowInfo, duplicate = false): Editor {
  return { original: duplicate ? undefined : copy, name: copy && !duplicate ? copy.name : copy && duplicate ? `${copy.name}-copy` : '',
    scope: copy?.scope ?? 'project', description: copy?.description ?? '', task: copy?.task ?? '', steps: copy?.steps.join('\n') ?? '' };
}
// eslint-disable-next-line no-control-regex
const control = /[\x00-\x1f\x7f]/;
const rootKey = (root: string) => root.replaceAll('\\', '/').toLowerCase();

/**
 * Tools › Workflows: saved task recipes. Each is created, edited, duplicated and deleted here, and Start runs it in this conversation the way
 * Work › Progress does (the host anchors the run, then the task goes to the chat). Files a form cannot read stay open/delete only.
 */
export function WorkflowsView({ controller, locale, disabled, busy, workspaceKey, sessionId, onStart }: {
  controller: ToolsController; locale: string; disabled: boolean; busy: boolean; workspaceKey: string; sessionId?: string; onStart?: (text: string) => void;
}) {
  const t = toolsTranslate(locale);
  const [editor, setEditor] = useState<Editor>(); const [deleting, setDeleting] = useState<WorkflowInfo>(); const [validation, setValidation] = useState(''); const [query, setQuery] = useState('');
  const pending = controller.pending('workflows'); const locked = disabled || busy || pending;
  // Start: one request at a time, answered by the composer state that carries its id.
  const run = useWorkState('workflow'); const running = !!run?.id && !['done', 'cancelled', 'empty'].includes(run.phase);
  const [starting, setStarting] = useState<string>(); const [startError, setStartError] = useState('');
  const startRequest = useRef<string | undefined>(undefined); const started = useRef(onStart); started.current = onStart;
  useEffect(() => onMessage(message => {
    if (message.type !== 'workspace/composerState' || !startRequest.current || message.payload.controlRequestId !== startRequest.current) return;
    const data = message.payload; if (rootKey(data.root ?? '') !== rootKey(workspaceKey)) return;
    startRequest.current = undefined; setStarting(undefined);
    if (data.controlError) setStartError(data.controlError); else started.current?.(data.task + (data.steps.length ? '\n' + data.steps.join('\n') : ''));
  }), [workspaceKey]);
  useEffect(() => {
    if (!starting) return;
    const timer = window.setTimeout(() => { startRequest.current = undefined; setStarting(undefined); setStartError(t('workflows.unconfirmed')); }, 10000);
    return () => window.clearTimeout(timer);
  }, [starting]);
  // A summary row (the host left task/steps out of a very long list) is read in full first.
  async function full(workflow: WorkflowInfo): Promise<WorkflowInfo | undefined> {
    if (!workflow.summary) return workflow;
    const reply = await controller.command('workflows', { action: 'get', scope: workflow.scope, name: workflow.name });
    const got = reply.workflow;
    if (!reply.ok || !got || typeof got.task !== 'string' || !Array.isArray(got.steps)) { setStartError(reply.error ?? t('tools.hostUnavailable')); return undefined; }
    return { ...workflow, ...got, task: got.task, steps: got.steps.filter(step => typeof step === 'string'), summary: false };
  }
  async function start(summary: WorkflowInfo) {
    if (locked || running || starting || summary.note || summary.unreadable || !onStart) return;
    const workflow = await full(summary); if (!workflow) return;
    const requestId = newId(); startRequest.current = requestId; setStarting(`${workflow.scope}:${workflow.name}`); setStartError('');
    if (!send('workspace/composer', { action: 'start', requestId, root: workspaceKey, sessionId, id: run?.id, revision: run?.revision, task: workflow.task, steps: workflow.steps })) {
      startRequest.current = undefined; setStarting(undefined); setStartError(t('tools.hostUnavailable'));
    }
  }
  function update(patch: Partial<Editor>) { setEditor(previous => previous ? { ...previous, ...patch } : undefined); }
  const fields = editor && fieldsOf(editor);
  // The exact file the host will write (indented JSON): its limit counts UTF-8 bytes, so a long Korean task can exceed it below 16,000 characters.
  const text = fields ? composeWorkflow(fields.description, fields.task, fields.steps) : '';
  async function save() {
    if (!editor || !fields || locked) return;
    const { name, description, task, steps } = fields;
    if (!/^[a-z0-9-]{2,40}$/.test(name) || description.length > 500 || control.test(description) ||
      // Characters, as the host counts them; the file's bytes are checked below.
      task.trim().length < 1 || task.length > 16000 || task.includes('\0') || steps.length > 32 || steps.some(step => step.length > 500 || control.test(step))) {
      setValidation(t('workflows.invalid')); return;
    }
    const over = bytesOver(t, text, WORKFLOW_BYTES);
    if (over) { setValidation(over); return; }
    setValidation('');
    const workflow = { description, task, steps };
    const reply = editor.original
      ? await controller.command('workflows', { action: 'update', scope: editor.original.scope, name: editor.original.name, expectedHash: editor.original.hash, workflow })
      : await controller.command('workflows', { action: 'create', scope: editor.scope, name, workflow });
    if (reply.ok) { if (!editor.original) setQuery(''); setEditor(undefined); }
  }
  async function remove() {
    if (locked || !deleting) return;
    const reply = await controller.command('workflows', { action: 'delete', scope: deleting.scope, name: deleting.name, expectedHash: deleting.hash });
    if (reply.ok) setDeleting(undefined);
  }
  const openEditor = async (row?: WorkflowInfo, duplicate = false) => {
    const copy = row && await full(row); if (row && !copy) return;
    setValidation(''); setDeleting(undefined); setEditor(editorFor(copy, duplicate));
  };
  const workflows = controller.workflows.workflows.filter(workflow => matches(query, workflow.name, workflow.description, workflow.task))
    .toSorted((left, right) => left.scope.localeCompare(right.scope) || left.name.localeCompare(right.name, 'en', { numeric: true }));
  const paged = usePaged(workflows, query);
  const scopeBadge = (scope: ResourceScope) => <Badge tone={scope === 'project' ? 'accent' : 'neutral'}>{t(scope === 'project' ? 'skills.kind.project' : 'skills.kind.user')}</Badge>;
  return <div className="tools-scroll-body" data-testid="workflows-view">
    <ViewSection sectionKey="tools-workflows" title={t('tools.workflows')} status={controller.workflows.workflows.length} bodyClassName="ui-flush"
      actions={<>
        <button type="button" className="btn small" data-testid="workflows-create" data-tools-dialog-fallback disabled={locked} onClick={() => openEditor()}><Icon name="plus" size={12} />{t('workflows.create')}</button>
        <SectionRefresh onClick={() => { void controller.command('workflows', { action: 'refresh' }); }} disabled={disabled || pending} testId="workflows-refresh" />
      </>}>
      {editor && <InPageDialog title={editor.original ? `${t('workflows.editorEdit')} · ${editor.original.name}` : t('workflows.editorCreate')} testId="workflows-editor" onClose={() => setEditor(undefined)} pending={pending}>
        <form className="ui-form tools-form" noValidate onSubmit={event => { event.preventDefault(); void save(); }}><fieldset disabled={locked}>
          <div className="ui-field-row">
            <Field label={t('workflows.name')}>{id => <input id={id} value={editor.name} spellCheck={false} data-testid="workflows-name" maxLength={40} disabled={!!editor.original} onChange={event => update({ name: event.target.value })} />}</Field>
            <ChoiceField label={t('skills.scope')}><ScopeChoice t={t} label={t('skills.scope')} testId="workflows-scope" disabled={!!editor.original} value={editor.scope} onChange={scope => update({ scope })} /></ChoiceField>
          </div>
          <Field label={t('workflows.description')}>{id => <input id={id} value={editor.description} data-testid="workflows-description" maxLength={500} onChange={event => update({ description: event.target.value })} />}</Field>
          <Field label={t('workflows.task')}>{id => <textarea id={id} value={editor.task} data-testid="workflows-task" rows={5} maxLength={16000} onChange={event => update({ task: event.target.value })} />}</Field>
          <Field label={t('workflows.steps')}>{id => <textarea id={id} value={editor.steps} data-testid="workflows-steps" rows={5} onChange={event => update({ steps: event.target.value })} />}</Field>
        </fieldset>
          {validation && <p role="alert" className="ui-err" data-testid="workflows-validation">{validation}</p>}
          <FormActions><button type="button" className="btn" disabled={pending} onClick={() => setEditor(undefined)}>{t('tools.cancel')}</button>
            <button type="submit" className="btn primary" data-testid="workflows-save" disabled={locked}>{t('tools.save')}</button></FormActions>
        </form>
      </InPageDialog>}
      {deleting && <ConfirmDelete t={t} testId="workflows-delete-confirm" yesTestId="workflows-delete-yes" question={t('workflows.deleteQuestion', { name: deleting.name })} locked={locked} pending={pending} onCancel={() => setDeleting(undefined)} onConfirm={() => { void remove(); }} />}
      <Toolbar><SearchBox value={query} onChange={setQuery} testId="workflows-search" /></Toolbar>
      {startError && <p className="ui-note" role="alert" data-testid="workflows-start-error">{startError}</p>}
      {controller.errors('workflows').map((error, index) => <p className="ui-note" role="alert" key={index}>{error}</p>)}
      {controller.workflows.diagnostics.map((message, index) => <p className="ui-note ui-muted" key={'d' + index}>{message}</p>)}
      <div className="tools-narrow" style={{ '--ui-cols-narrow': 'minmax(0, 1fr) auto auto' } as CSSProperties}>
        <DataTable columns="minmax(0, 1fr) auto auto auto" label={t('tools.workflows')} testId="workflows-table" empty={!workflows.length} emptyRows={3}
          head={[t('tcol.name'), t('tcol.steps'), <span className="narrow-hide">{t('tcol.scope')}</span>, '']}>
          {paged.slice.map(workflow => {
            const key = `${workflow.scope}:${workflow.name}`; const fixed = !!workflow.note || !!workflow.unreadable;
            return <div className="ui-tr" role="row" key={key} data-testid={`workflows-row-${workflow.scope}-${workflow.name}`}>
              <span role="cell" className="ui-cell"><span className="tools-name-line"><strong>{workflow.name}</strong><span className="inline-kind">{scopeBadge(workflow.scope)}</span></span>
                {workflow.description && <span className="ui-sub" title={workflow.description}>{workflow.description}</span>}
                {fixed && <span role="note" className="ui-sub ui-warn" title={workflow.note || t('tools.unreadable')}>{workflow.note || t('tools.unreadable')}</span>}</span>
              <span role="cell" className="ui-cell ui-muted tools-num">{fixed ? '–' : workflow.stepCount ?? workflow.steps.length}</span>
              <span role="cell" className="ui-cell narrow-hide">{scopeBadge(workflow.scope)}</span>
              <span role="cell" className="ui-cell ui-actions">
                {fixed ? <IconButton icon="external" label={t('workflows.open')} testId={`workflows-open-${workflow.scope}-${workflow.name}`} disabled={locked} onClick={() => { void controller.command('workflows', { action: 'open', scope: workflow.scope, name: workflow.name }); }} />
                  : <>
                    {onStart && <IconButton icon="play" label={starting === key ? t('workflows.starting') : t('workflows.start')} testId={`workflows-start-${workflow.scope}-${workflow.name}`} disabled={locked || running || !!starting} onClick={() => start(workflow)} />}
                    <IconButton icon="edit" label={t('workflows.edit')} testId={`workflows-edit-${workflow.scope}-${workflow.name}`} disabled={locked || !workflow.hash} onClick={() => openEditor(workflow)} />
                    <IconButton icon="copy" label={t('workflows.duplicate')} testId={`workflows-duplicate-${workflow.scope}-${workflow.name}`} disabled={locked} onClick={() => openEditor(workflow, true)} />
                  </>}
                <IconButton icon="trash" tone="danger" label={t('tools.delete')} testId={`workflows-delete-${workflow.scope}-${workflow.name}`} disabled={locked} onClick={() => { setEditor(undefined); setDeleting(workflow); }} />
              </span>
            </div>;
          })}
        </DataTable>
      </div>
      <Pager paged={paged} testId="workflows-pager" />
    </ViewSection>
  </div>;
}
