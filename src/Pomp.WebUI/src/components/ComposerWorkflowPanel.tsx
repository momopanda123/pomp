import { useEffect, useRef, useState } from 'react';
import { useT, type MessageKey, type Translate } from '../i18n';
import { newId } from '../format';
import { onMessage, send } from '../protocol';
import { sendToolsCommand, type WorkflowInfo } from '../toolsProtocol';
import { plainControls, revealControls } from './RevealControls';
import type { WorkLayout } from './WorkPanel';
import { ViewSection } from './ViewSection';
import { Icon } from './Icon';
import { Actions, Badge, Cell, DataTable, Field, Form, FormActions, IconButton, matches, Pager, Row, SearchBox, Toolbar, usePaged, type BadgeTone } from './ui';
import { useWorkScope, useWorkState } from '../work/store';
import { useRefreshOnShow } from '../useRefreshOnShow';
import { stateLabel } from '../stateLabels';
const savedKey = (row: WorkflowInfo) => `${row.scope}:${row.name}`;
/** A running workflow's phase reads as an activity, so it never looks like the Edit button next to it. */
const phaseKeys: Record<string, MessageKey> = { planning: 'work.workflow.phase.planning', discovery: 'work.workflow.phase.discovery', editing: 'work.workflow.phase.editing', awaitingreview: 'work.workflow.phase.awaitingReview', verification: 'work.workflow.phase.verification' };
const phaseName = (phase: string, locale: string, t: Translate) => { const key = phaseKeys[phase.toLowerCase()]; return key ? t(key) : stateLabel(phase, locale, 'workflow'); };
export function ComposerWorkflowPanel({ locale, workspaceKey, sessionId, connected, busy = false, inert = false, onContinue }: { locale: string; workspaceKey: string; sessionId?: string; connected: boolean; busy?: boolean; layout?: WorkLayout; inert?: boolean; onContinue?: (text: string) => void; onOpenWorkflows?: () => void }) {
  useWorkScope(workspaceKey, sessionId);
  const state = useWorkState('workflow');
  const outcomes = usePaged(state?.outcomes ?? [], `${state?.id ?? ''}:${state?.revision ?? ''}`);
  const [pending, setPending] = useState(false); const request = useRef<string | undefined>(undefined); const saving = useRef(false);
  const [error, setError] = useState(''); const t = useT();
  const [editing, setEditing] = useState(false); const [draftTask, setDraftTask] = useState(''); const [draftSteps, setDraftSteps] = useState('');
  // Saved workflows (managed in Tools › 워크플로, started here): refresh on show, mount and workspace change; 'workflows/reply' has no payload for refresh and is ignored.
  const [saved, setSaved] = useState<WorkflowInfo[]>([]);
  const [startName, setStartName] = useState('');
  const [starting, setStarting] = useState(false);
  const startRequest = useRef<string | undefined>(undefined);
  // The saved list is workspace-scoped: another workspace or conversation asks again (the first show asks through useRefreshOnShow below).
  const scopeKey = `${workspaceKey}|${sessionId ?? ''}`; const lastScope = useRef(scopeKey);
  useEffect(() => {
    setSaved([]); setStartName(''); setStarting(false); startRequest.current = undefined;
    if (lastScope.current !== scopeKey) { lastScope.current = scopeKey; if (connected && !inert && workspaceKey) sendToolsCommand('workflows', { requestId: newId(), action: 'refresh' }); }
    return onMessage(message => {
      if (message.type !== 'workflows/state') return;
      const rows = message.payload.workflows;
      if (!Array.isArray(rows)) return;
      setSaved(rows);
      // A project and a user workflow may share a name: the choice is scope and name.
      setStartName(previous => (rows.some(row => savedKey(row) === previous) ? previous : (rows.find(row => !row.note) ? savedKey(rows.find(row => !row.note)!) : '')));
    });
  }, [workspaceKey, sessionId]);
  useRefreshOnShow(connected && !inert && !!workspaceKey, () => sendToolsCommand('workflows', { requestId: newId(), action: 'refresh' }));
  useEffect(() => {
    if (!starting) return;
    const timer = window.setTimeout(() => { startRequest.current = undefined; setStarting(false); setError(t('workflows.unconfirmed')); }, 10000);
    return () => window.clearTimeout(timer);
  }, [starting, t]);
  // The workflow state itself belongs to the workspace and stays in the shared store: only this panel's own requests end here.
  useEffect(() => {
    setPending(false); request.current = undefined; saving.current = false; setEditing(false); setError('');
    return onMessage(message => {
      if (message.type !== 'workspace/composerState') return;
      const data = message.payload;
      if (data.root?.replaceAll('\\', '/').toLowerCase() !== workspaceKey.replaceAll('\\', '/').toLowerCase()) return;
      if (!Number.isSafeInteger(data.revision) || data.revision < 0 || !Array.isArray(data.steps) || data.steps.length > 32 || !data.steps.every(s => typeof s === 'string' && s.length <= 500) || !Array.isArray(data.outcomes)) return;
      if (startRequest.current && data.controlRequestId === startRequest.current) {
        startRequest.current = undefined; setStarting(false);
        if (data.controlError) setError(data.controlError);
        else onContinue?.(data.task + (data.steps.length ? '\n' + data.steps.join('\n') : ''));
        return;
      }
      if (request.current && data.controlRequestId === request.current) {
        if (data.controlError) setError(data.controlError);
        else if (saving.current) setEditing(false);
        saving.current = false; request.current = undefined; setPending(false);
      }
    });
  }, [workspaceKey, sessionId, connected]);
  useRefreshOnShow(connected && !inert && !!workspaceKey, () => act('refresh'));
  useEffect(() => {
    if (!pending) return;
    const timer = window.setTimeout(() => { request.current = undefined; setPending(false); setError(t('workflows.unconfirmed')); }, 10000);
    return () => window.clearTimeout(timer);
  }, [pending, t]);
  const act = (action: 'refresh' | 'pause' | 'resume' | 'cancel' | 'save', extra: { task?: string; steps?: string[] } = {}) => {
    if (!connected || inert || request.current || (action === 'save' && busy)) return;
    const id = newId(); request.current = id; setPending(true); setError('');
    saving.current = action === 'save';
    if (!send('workspace/composer', { action, requestId: id, root: workspaceKey, sessionId, id: state?.id, revision: state?.revision, ...extra })) {
      request.current = undefined; setPending(false); setError(t('error.hostUnavailable'));
    }
  };
  const detailRequest = useRef<string | undefined>(undefined);
  const startWith = (task: string, steps: string[]) => {
    if (!connected || inert || busy) { setError(t('work.workflow.startWhenIdle')); return; }
    // CURRENT id/revision anchors the run; task/steps come from the saved row.
    const id = newId(); startRequest.current = id; setStarting(true); setError('');
    if (!send('workspace/composer', { action: 'start', requestId: id, root: workspaceKey, sessionId, id: state?.id, revision: state?.revision, task, steps })) {
      startRequest.current = undefined; setStarting(false); setError(t('error.hostUnavailable'));
    }
  };
  useEffect(() => onMessage(message => {
    if (message.type !== 'workflows/reply' || message.payload.requestId !== detailRequest.current) return;
    detailRequest.current = undefined; setStarting(false);
    const got = message.payload.workflow;
    if (!message.payload.ok || !got || typeof got.task !== 'string' || !Array.isArray(got.steps)) { setError(message.payload.error ?? t('error.hostUnavailable')); return; }
    startWith(got.task, got.steps.filter(step => typeof step === 'string'));
  // eslint-disable-next-line react-hooks/exhaustive-deps
  }), [workspaceKey, sessionId, state?.id, state?.revision, connected, inert, busy]);
  const startSaved = (key = startName) => {
    const row = saved.find(item => savedKey(item) === key);
    if (!connected || inert || busy || !row || row.note || active || pending || starting || request.current || detailRequest.current || !onContinue) return;
    if (!row.summary) { startWith(row.task, row.steps); return; }
    // A summary row (the host left task/steps out of a very long list) is read in full first.
    const id = newId(); detailRequest.current = id; setStarting(true); setError('');
    if (!sendToolsCommand('workflows', { requestId: id, action: 'get', scope: row.scope, name: row.name })) { detailRequest.current = undefined; setStarting(false); setError(t('error.hostUnavailable')); }
  };
  const parseSteps = (raw: string) => raw.split('\n').map(line => line.trim()).filter(line => line.length > 0);
  const openEditor = () => { setDraftTask(active ? state?.task ?? '' : ''); setDraftSteps(active ? (state?.steps ?? []).join('\n') : ''); setEditing(true); };
  const active = !!state?.id && !['done', 'cancelled', 'empty'].includes(state.phase);
  // A run that another conversation of this workspace owns is on hold here: the host admits its tools only after this conversation
  // resumes it (which takes it over) or cancels it, so it reads as paused and offers exactly those two.
  const foreign = active && !!state?.sessionId && !!sessionId && state.sessionId !== sessionId;
  const paused = state?.paused === true || foreign;
  // Save/edit admitted only where the backend allows it: no active run, or an editable phase of this conversation's run.
  const canEdit = !!state && (!active || (!foreign && ['planning', 'discovery', 'editing'].includes(state.phase)));
  const [savedQuery, setSavedQuery] = useState('');
  const shownSaved = saved.filter(row => matches(savedQuery, row.name, row.description, row.task, row.note));
  const pagedSaved = usePaged(shownSaved, workspaceKey + ':' + savedQuery);
  const phaseTone = (phase: string): BadgeTone => phase === 'done' ? 'ok' : phase === 'cancelled' ? 'neutral' : paused ? 'warn' : 'info';
  const idleReason = busy ? t('work.workflow.waitForIdle') : undefined;
  return (
    <ViewSection sectionKey="work-workflow" headingLevel="h2" active={active} inert={inert || undefined} bodyClassName="ui-flush"
      title={t('work.workflow')} status={active && state ? <Badge tone={phaseTone(state.phase)} title={state.phase} testId="workflow-phase">{revealControls(phaseName(state.phase, locale, t))}{paused ? ` · ${t('work.workflow.paused')}` : ''}</Badge> : saved.length}
      actions={active ? <>
        <button type="button" className="btn small subtle" data-testid="workflow-pause" aria-pressed={paused} disabled={!connected || pending} onClick={() => act(paused ? 'resume' : 'pause')}><Icon name={paused ? 'play' : 'pause'} size={11} />{t(paused ? 'work.workflow.resume' : 'work.workflow.pause')}</button>
        {!editing && <button type="button" className="btn small subtle" data-testid="workflow-edit" disabled={!connected || pending || !canEdit} onClick={openEditor}><Icon name="edit" size={12} />{t('ui.edit')}</button>}
        <button type="button" className="btn small subtle" data-testid="workflow-cancel" disabled={!connected || pending} onClick={() => act('cancel')}><Icon name="x" size={12} />{t('ui.cancel')}</button>
      </> : !editing ? <button type="button" className="btn small" data-testid="workflow-new" disabled={!connected || pending || !canEdit} onClick={openEditor}><Icon name="plus" size={12} />{t('work.workflow.new')}</button> : undefined}>
      {editing && <Form testId="workflow-editor">
        <Field label={t('work.workflow.task')}>{id => <textarea id={id} value={draftTask} maxLength={16000} rows={4} disabled={pending} onChange={event => setDraftTask(event.target.value)} />}</Field>
        <Field label={t('workflows.steps')}>{id => <textarea id={id} value={draftSteps} rows={6} disabled={pending} onChange={event => setDraftSteps(event.target.value)} />}</Field>
        <FormActions>
          <button type="button" className="btn" disabled={pending} onClick={() => setEditing(false)}>{t('ui.cancel')}</button>
          <button type="button" className="btn primary" data-testid="workflow-save" disabled={!connected || pending || busy || !draftTask.trim()} title={idleReason} onClick={() => act('save', { task: draftTask.trim(), steps: parseSteps(draftSteps) })}>{t('ui.save')}</button>
        </FormActions>
      </Form>}
      {!editing && active && <>
        {foreign && <p className="ui-note ui-muted" role="status" data-testid="workflow-foreign">{t('work.workflow.foreign')}</p>}
        {state?.task && <p className="ui-note">{revealControls(state.task)}</p>}
        <DataTable columns="28px minmax(0, 1fr)" label={t('workflows.steps')} empty={!state?.steps?.length} emptyRows={1} head={['#', t('work.workflow.step')]}>
          {state?.steps?.map((step, i) => <Row key={i}><Cell className="ui-muted">{i + 1}</Cell><Cell wrap>{revealControls(step)}</Cell></Row>)}
        </DataTable>
        {state?.outcomes?.length ? <DataTable columns="minmax(0, 1fr) auto" label={t('work.files')} head={[t('ui.col.file'), t('tcol.state')]}>
          {outcomes.slice.map((file, i) => <Row key={`${outcomes.page}:${i}`}><Cell className="ui-mono" title={file.path}>{revealControls(file.path)}</Cell><Cell><Badge title={file.state}>{revealControls(stateLabel(file.state, locale, 'outcome'))}</Badge></Cell></Row>)}
        </DataTable> : null}
        <Pager paged={outcomes} testId="workflow-outcomes-pager" />
        {onContinue && <div className="ui-card-foot"><span className="ui-grow" /><button type="button" className="btn small" data-testid="workflow-continue" disabled={!connected || inert || pending || paused} onClick={() => onContinue((state?.task ?? '') + (state?.steps?.length ? '\n' + state.steps.join('\n') : ''))}><Icon name="send" size={12} />{t('work.workflow.continueInChat')}</button></div>}
      </>}
      {!editing && !active && saved.length > 0 && <Toolbar><SearchBox value={savedQuery} onChange={setSavedQuery} label={t('work.workflow.search')} testId="workflow-saved-search" /></Toolbar>}
      {!editing && !active && <DataTable columns="minmax(0, 1fr) 44px auto auto" label={t('work.workflow.savedList')} testId="workflow-saved" empty={!shownSaved.length} head={[t('work.workflow.saved'), t('workflows.steps'), t('ui.col.scope'), '']}>
        {pagedSaved.slice.map(row => <Row key={savedKey(row)} testId={`workflow-saved-${savedKey(row)}`}>
          <Cell title={row.description}><strong>{plainControls(row.name)}</strong>{(row.note || row.description) && <span className="ui-sub">{plainControls(row.note || row.description || '')}</span>}</Cell>
          <Cell className="ui-muted">{row.stepCount ?? row.steps?.length ?? 0}</Cell>
          <Cell><Badge tone={row.scope === 'project' ? 'accent' : 'neutral'}>{t(row.scope === 'project' ? 'ui.scope.project' : 'ui.scope.user')}</Badge></Cell>
          <Actions><IconButton icon="play" label={idleReason ?? t(starting && startName === savedKey(row) ? 'workflows.starting' : 'workflows.start')} testId={`workflow-start-${savedKey(row)}`}
            disabled={!connected || inert || busy || pending || starting || !!row.note || !onContinue} onClick={() => { setStartName(savedKey(row)); startSaved(savedKey(row)); }} /></Actions>
        </Row>)}
      </DataTable>}
      {!editing && !active && <Pager paged={pagedSaved} testId="workflow-saved-pager" />}
      {state?.message && <p className="ui-note ui-muted" role="status">{revealControls(state.message)}</p>}
      {error && <p className="ui-note" role="alert">{error}</p>}
    </ViewSection>
  );
}
