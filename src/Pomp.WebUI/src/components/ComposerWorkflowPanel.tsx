import { useEffect, useRef, useState } from 'react';
import { useT } from '../i18n';
import { newId } from '../format';
import { onMessage, send } from '../protocol';
import { sendToolsCommand, type WorkflowInfo } from '../toolsProtocol';
import { ComposerAutoContinuePanel } from './ComposerAutoContinuePanel';
import { plainControls, revealControls } from './RevealControls';
import type { WorkLayout } from './WorkPanel';
import { ViewSection } from './ViewSection';
import { Icon } from './Icon';
import { Actions, Badge, Cell, DataTable, Field, Form, FormActions, IconButton, matches, Pager, Row, SearchBox, Toolbar, usePaged, type BadgeTone } from './ui';
import { resetWorkState, useWorkScope, useWorkState } from '../work/store';
import { useRefreshOnShow } from '../useRefreshOnShow';
import { stateLabel } from '../stateLabels';
const savedKey = (row: WorkflowInfo) => `${row.scope}:${row.name}`;
/** A running workflow's phase reads as an activity, so it never looks like the Edit button next to it. */
const phaseNames: Record<string, [string, string]> = { planning: ['계획 중', 'Planning'], discovery: ['조사 중', 'Discovering'], editing: ['수정 중', 'Editing'], awaitingreview: ['검토 대기', 'Awaiting review'], verification: ['확인 중', 'Verifying'] };
const phaseName = (phase: string, locale: string) => { const name = phaseNames[phase.toLowerCase()]; return name ? name[locale.startsWith('ko') ? 0 : 1] : stateLabel(phase, locale, 'workflow'); };
export function ComposerWorkflowPanel({ locale, workspaceKey, sessionId, connected, layout = 'details', inert = false, onContinue }: { locale: string; workspaceKey: string; sessionId?: string; connected: boolean; layout?: WorkLayout; inert?: boolean; onContinue?: (text: string) => void; onOpenWorkflows?: () => void }) {
  useWorkScope(workspaceKey, sessionId);
  const state = useWorkState('workflow');
  const outcomes = usePaged(state?.outcomes ?? [], `${state?.id ?? ''}:${state?.revision ?? ''}`);
  const [pending, setPending] = useState(false); const request = useRef<string | undefined>(undefined); const saving = useRef(false);
  const [error, setError] = useState(''); const ko = locale.startsWith('ko'); const t = useT();
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
    const timer = window.setTimeout(() => { startRequest.current = undefined; setStarting(false); setError(ko ? '결과를 확인하지 못했어요.' : 'Outcome unconfirmed.'); }, 10000);
    return () => window.clearTimeout(timer);
  }, [starting, ko]);
  useEffect(() => {
    resetWorkState('workflow'); setPending(false); request.current = undefined; saving.current = false; setEditing(false); setError('');
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
    const timer = window.setTimeout(() => { request.current = undefined; setPending(false); setError(ko ? '결과를 확인하지 못했어요.' : 'Outcome unconfirmed.'); }, 10000);
    return () => window.clearTimeout(timer);
  }, [pending, ko]);
  const act = (action: 'refresh' | 'pause' | 'resume' | 'cancel' | 'save', extra: { task?: string; steps?: string[] } = {}) => {
    if (!connected || inert || request.current) return;
    const id = newId(); request.current = id; setPending(true); setError('');
    saving.current = action === 'save';
    if (!send('workspace/composer', { action, requestId: id, root: workspaceKey, sessionId, id: state?.id, revision: state?.revision, ...extra })) {
      request.current = undefined; setPending(false); setError(t('error.hostUnavailable'));
    }
  };
  const detailRequest = useRef<string | undefined>(undefined);
  const startWith = (task: string, steps: string[]) => {
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
  }), [workspaceKey, sessionId, state?.id, state?.revision]);
  const startSaved = (key = startName) => {
    const row = saved.find(item => savedKey(item) === key);
    if (!connected || inert || !row || row.note || active || pending || starting || request.current || detailRequest.current || !onContinue) return;
    if (!row.summary) { startWith(row.task, row.steps); return; }
    // A summary row (the host left task/steps out of a very long list) is read in full first.
    const id = newId(); detailRequest.current = id; setStarting(true); setError('');
    if (!sendToolsCommand('workflows', { requestId: id, action: 'get', scope: row.scope, name: row.name })) { detailRequest.current = undefined; setStarting(false); setError(t('error.hostUnavailable')); }
  };
  const parseSteps = (raw: string) => raw.split('\n').map(line => line.trim()).filter(line => line.length > 0);
  const openEditor = () => { setDraftTask(active ? state?.task ?? '' : ''); setDraftSteps(active ? (state?.steps ?? []).join('\n') : ''); setEditing(true); };
  const active = !!state?.id && !['done', 'cancelled', 'empty'].includes(state.phase);
  // Save/edit admitted only where the backend allows it: no active run, or an editable phase.
  const canEdit = !!state && (!active || ['planning', 'discovery', 'editing'].includes(state.phase));
  const [savedQuery, setSavedQuery] = useState('');
  const shownSaved = saved.filter(row => matches(savedQuery, row.name, row.description, row.task, row.note));
  const pagedSaved = usePaged(shownSaved, workspaceKey + ':' + savedQuery);
  const phaseTone = (phase: string): BadgeTone => phase === 'done' ? 'ok' : phase === 'cancelled' ? 'neutral' : state?.paused ? 'warn' : 'info';
  return <>
    <ViewSection sectionKey="work-workflow" className="agent-plan workflow-panel" headingLevel="h2" active={active} inert={inert || undefined} bodyClassName="ui-flush"
      title={ko ? '워크플로' : 'Workflow'} status={active && state ? <Badge tone={phaseTone(state.phase)} title={state.phase}>{revealControls(phaseName(state.phase, locale))}{state.paused ? (ko ? ' · 일시정지' : ' · paused') : ''}</Badge> : saved.length}
      actions={editing ? undefined : active ? <>
        <button type="button" className="btn small subtle" disabled={!connected || pending} onClick={() => act(state?.paused ? 'resume' : 'pause')}><Icon name={state?.paused ? 'play' : 'stop'} size={11} />{state?.paused ? (ko ? '재개' : 'Resume') : (ko ? '일시정지' : 'Pause')}</button>
        <button type="button" className="btn small subtle" disabled={!connected || pending || !canEdit} onClick={openEditor}><Icon name="edit" size={12} />{ko ? '편집' : 'Edit'}</button>
        <button type="button" className="btn small subtle" disabled={!connected || pending} onClick={() => act('cancel')}><Icon name="x" size={12} />{ko ? '취소' : 'Cancel'}</button>
      </> : <button type="button" className="btn small" data-testid="workflow-new" disabled={!connected || pending || !canEdit} onClick={openEditor}><Icon name="plus" size={12} />{ko ? '새 작업' : 'New task'}</button>}>
      {editing && <Form className="workflow-editor" testId="workflow-editor">
        <Field label={ko ? '작업 내용' : 'Task'}>{id => <textarea id={id} value={draftTask} maxLength={16000} rows={4} disabled={pending} onChange={event => setDraftTask(event.target.value)} />}</Field>
        <Field label={ko ? '단계' : 'Steps'}>{id => <textarea id={id} value={draftSteps} rows={6} disabled={pending} onChange={event => setDraftSteps(event.target.value)} />}</Field>
        <FormActions>
          <button type="button" className="btn" disabled={pending} onClick={() => setEditing(false)}>{ko ? '취소' : 'Cancel'}</button>
          <button type="button" className="btn primary" data-testid="workflow-save" disabled={!connected || pending || !draftTask.trim()} onClick={() => act('save', { task: draftTask.trim(), steps: parseSteps(draftSteps) })}>{ko ? '저장' : 'Save'}</button>
        </FormActions>
      </Form>}
      {!editing && active && <>
        {state?.task && <p className="ui-note">{revealControls(state.task)}</p>}
        <DataTable columns="28px minmax(0, 1fr)" label={ko ? '단계' : 'Steps'} empty={!state?.steps?.length} emptyRows={1} head={['#', ko ? '단계' : 'Step']}>
          {state?.steps?.map((step, i) => <Row key={i}><Cell className="ui-muted">{i + 1}</Cell><Cell wrap>{revealControls(step)}</Cell></Row>)}
        </DataTable>
        {state?.outcomes?.length ? <DataTable columns="minmax(0, 1fr) auto" label={ko ? '파일' : 'Files'} head={[ko ? '파일' : 'File', ko ? '상태' : 'State']}>
          {outcomes.slice.map((file, i) => <Row key={`${outcomes.page}:${i}`}><Cell className="ui-mono" title={file.path}>{revealControls(file.path)}</Cell><Cell><Badge title={file.state}>{revealControls(stateLabel(file.state, locale, 'outcome'))}</Badge></Cell></Row>)}
        </DataTable> : null}
        <Pager paged={outcomes} testId="workflow-outcomes-pager" />
        {onContinue && <div className="ui-card-foot"><span className="ui-grow" /><button type="button" className="btn small" disabled={!connected || inert || pending || state?.paused} onClick={() => onContinue((state?.task ?? '') + (state?.steps?.length ? '\n' + state.steps.join('\n') : ''))}><Icon name="send" size={12} />{ko ? '채팅에서 이어하기' : 'Continue in chat'}</button></div>}
      </>}
      {!editing && !active && saved.length > 0 && <Toolbar><SearchBox value={savedQuery} onChange={setSavedQuery} label={ko ? '워크플로 검색' : 'Search workflows'} testId="workflow-saved-search" /></Toolbar>}
      {!editing && !active && <DataTable columns="minmax(0, 1fr) 44px auto auto" label={ko ? '저장된 워크플로' : 'Saved workflows'} testId="workflow-saved" empty={!shownSaved.length} head={[ko ? '저장된 워크플로' : 'Saved workflow', ko ? '단계' : 'Steps', ko ? '범위' : 'Scope', '']}>
        {pagedSaved.slice.map(row => <Row key={savedKey(row)} testId={`workflow-saved-${savedKey(row)}`}>
          <Cell title={row.description}><strong>{plainControls(row.name)}</strong>{(row.note || row.description) && <span className="ui-sub">{plainControls(row.note || row.description || '')}</span>}</Cell>
          <Cell className="ui-muted">{row.stepCount ?? row.steps?.length ?? 0}</Cell>
          <Cell><Badge tone={row.scope === 'project' ? 'accent' : 'neutral'}>{row.scope === 'project' ? (ko ? '프로젝트' : 'Project') : (ko ? '사용자' : 'User')}</Badge></Cell>
          <Actions><IconButton icon="play" label={starting && startName === savedKey(row) ? (ko ? '시작 중' : 'Starting') : (ko ? '시작' : 'Start')} testId={`workflow-start-${savedKey(row)}`}
            disabled={!connected || inert || pending || starting || !!row.note || !onContinue} onClick={() => { setStartName(savedKey(row)); startSaved(savedKey(row)); }} /></Actions>
        </Row>)}
      </DataTable>}
      {!editing && !active && <Pager paged={pagedSaved} testId="workflow-saved-pager" />}
      {state?.message && <p className="ui-note ui-muted" role="status">{revealControls(state.message)}</p>}
      {error && <p className="ui-note" role="alert">{error}</p>}
    </ViewSection>
    <ComposerAutoContinuePanel workflow={state} connected={connected} locale={locale} inert={inert} layout={layout} />
  </>;
}
