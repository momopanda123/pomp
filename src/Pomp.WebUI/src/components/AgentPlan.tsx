import { useEffect, useMemo, useRef, useState } from 'react';
import { useT } from '../i18n';
import { newId } from '../format';
import { onMessage, send, type AgentPlanEdit, type AgentPlanPage, type SessionInfoPayload } from '../protocol';
import { revealControls } from './RevealControls';
import { stateLabel } from '../stateLabels';
import '../styles/work-tab.css';
import type { WorkLayout } from './WorkPanel';
import { ViewSection } from './ViewSection';
import { Icon } from './Icon';
import { Actions, Badge, Cell, DataTable, Field, Form, FormActions, IconButton, matches, PAGE_SIZE, Pager, Row, SearchBox, Segmented, Toolbar, usePaged, type BadgeTone } from './ui';

const planStatuses = ['pending', 'in_progress', 'completed', 'blocked', 'abandoned'] as const;
const limits = { name: 200, content: 600, blocker: 300 };
// eslint-disable-next-line no-control-regex
const control = /[\u0000-\u001f\u007f-\u009f]/;
const fits = (text: string, max: number) => text.length <= max && !control.test(text);
type DistributiveOmit<T, K extends PropertyKey> = T extends unknown ? Omit<T, K> : never;
type EditOp = DistributiveOmit<AgentPlanEdit, 'requestId' | 'sessionId' | 'revision'>;
type PlanRow = AgentPlanPage['rows'][number];
/** The task form: a new task (in a phase, or the first task of a new phase) or the edit of one task. */
interface TaskDraft { mode: 'add' | 'edit'; phase: number | 'new'; task?: number; phaseName: string; content: string; status: string; blocker: string }

/**
 * The whole agent plan, one page at a time: the host keeps the plan and answers pages (query, clamped page) and single-task or
 * single-phase edits checked against the revision the page showed, so any number of tasks stays usable.
 */
export function AgentPlan({ session, sessionId, locale, disabled, busy, onContinue, onStop, layout = 'details', inert = false }: { session: SessionInfoPayload | null; sessionId?: string; locale: string; disabled: boolean; busy: boolean; onContinue: () => void; onStop: () => void; layout?: WorkLayout; inert?: boolean }) {
  const ko = locale.toLowerCase().startsWith('ko'); const t = useT();
  const total = session?.planTaskCount ?? 0; const open = session?.planOpenCount ?? 0; const revision = session?.planRevision ?? '';
  const [view, setView] = useState<'tasks' | 'phases'>('tasks');
  const [query, setQuery] = useState(''); const [search, setSearch] = useState(''); const [page, setPage] = useState(0);
  const [data, setData] = useState<AgentPlanPage | null>(null);
  const [draft, setDraft] = useState<TaskDraft | null>(null); const [renaming, setRenaming] = useState<{ phase: number; name: string } | null>(null);
  const [armed, setArmed] = useState<string>(); const armTimer = useRef<number | undefined>(undefined);
  const [pending, setPending] = useState(false); const [error, setError] = useState('');
  const pageRequest = useRef<string | undefined>(undefined); const editRequest = useRef<string | undefined>(undefined);
  useEffect(() => { const timer = window.setTimeout(() => { setSearch(query.trim()); setPage(0); }, 250); return () => window.clearTimeout(timer); }, [query]);
  useEffect(() => {
    setDraft(null); setRenaming(null); setArmed(undefined); setPending(false); setError(''); setPage(0); setQuery(''); setSearch(''); setData(null); editRequest.current = undefined;
  }, [sessionId]);
  useEffect(() => {
    if (inert || !sessionId) return;
    const id = newId(); pageRequest.current = id;
    if (!send('workspace/agentPlanPage', { requestId: id, sessionId, page, size: PAGE_SIZE, ...(search ? { query: search } : {}) })) pageRequest.current = undefined;
  }, [inert, sessionId, page, search, revision]);
  useEffect(() => onMessage(message => {
    if (message.type === 'workspace/agentPlanPageResult') {
      if (message.payload.requestId !== pageRequest.current) return;
      pageRequest.current = undefined;
      if (!message.payload.ok) { setError(message.payload.error ?? (ko ? '계획을 읽지 못했어요.' : 'The plan could not be read.')); return; }
      setData(message.payload); setPage(message.payload.page);
      return;
    }
    if (message.type !== 'workspace/agentPlanResult' || message.payload.requestId !== editRequest.current) return;
    editRequest.current = undefined; setPending(false);
    if (message.payload.ok) { setDraft(null); setRenaming(null); setError(''); } else setError(message.payload.error ?? (ko ? '계획을 저장하지 못했어요.' : 'The plan was not saved.'));
  }), [ko]);
  useEffect(() => {
    if (!pending) return;
    const timer = window.setTimeout(() => { editRequest.current = undefined; setPending(false); setError(ko ? '결과를 확인하지 못했어요.' : 'Outcome unconfirmed.'); }, 30000);
    return () => window.clearTimeout(timer);
  }, [pending, ko]);
  useEffect(() => () => window.clearTimeout(armTimer.current), []);
  const phases = useMemo(() => data?.phases ?? [], [data]);
  const phaseName = useMemo(() => new Map(phases.map(phase => [phase.index, phase.name])), [phases]);
  const shownPhases = useMemo(() => phases.filter(phase => matches(query, phase.name)), [phases, query]);
  const pagedPhases = usePaged(shownPhases, `${sessionId}:${query}`);
  if (layout === 'details' && !total && !session?.hasPendingAsyncWork) return null;

  const rows = data?.rows ?? []; const matching = data?.total ?? 0; const pages = Math.max(1, Math.ceil(matching / PAGE_SIZE));
  const pagedTasks = { page, pages, total: matching, from: matching ? page * PAGE_SIZE + 1 : 0, to: Math.min(matching, page * PAGE_SIZE + PAGE_SIZE), setPage };
  const canEdit = !disabled && !busy && !inert && !pending && !session?.hasPendingAsyncWork && !!sessionId && data?.editable === true;
  const edit = (op: EditOp) => {
    if (!canEdit || !sessionId || !data) return;
    const id = newId(); editRequest.current = id; setPending(true); setError('');
    if (!send('workspace/agentPlanEdit', { ...op, requestId: id, sessionId, revision: data.revision ?? '' } as AgentPlanEdit)) { editRequest.current = undefined; setPending(false); setError(t('error.hostUnavailable')); }
  };
  // Delete is pressed twice on the same row: the first press arms it for ten seconds.
  const arm = (key: string, run: () => void) => {
    window.clearTimeout(armTimer.current);
    if (armed !== key) { setArmed(key); armTimer.current = window.setTimeout(() => setArmed(current => (current === key ? undefined : current)), 10000); return; }
    setArmed(undefined); run();
  };
  const startAdd = () => { setRenaming(null); setError(''); setDraft({ mode: 'add', phase: phases.length ? phases[phases.length - 1].index : 'new', phaseName: '', content: '', status: 'pending', blocker: '' }); };
  const startEdit = (row: PlanRow) => { setRenaming(null); setError(''); setDraft({ mode: 'edit', phase: row.phase, task: row.task, phaseName: '', content: row.content, status: row.status, blocker: row.blocker ?? '' }); };
  const draftValid = !!draft && !!draft.content.trim() && fits(draft.content, limits.content) && fits(draft.blocker, limits.blocker)
    && (draft.phase !== 'new' || (!!draft.phaseName.trim() && fits(draft.phaseName, limits.name)));
  const saveDraft = () => {
    if (!draft || !draftValid) return;
    const content = draft.content.trim(); const blocker = draft.status === 'blocked' ? draft.blocker.trim() : '';
    if (draft.mode === 'edit' && typeof draft.phase === 'number' && draft.task !== undefined) edit({ op: 'updateTask', phase: draft.phase, task: draft.task, content, status: draft.status, blocker });
    else if (draft.phase === 'new') edit({ op: 'addPhase', name: draft.phaseName.trim(), content, status: draft.status });
    else edit({ op: 'addTask', phase: draft.phase, content, status: draft.status, ...(blocker ? { blocker } : {}) });
  };
  const statusOptions = planStatuses.map(status => <option key={status} value={status}>{stateLabel(status, locale)}</option>);
  const running = busy || !!session?.hasPendingAsyncWork || rows.some(row => ['inProgress', 'in_progress', 'running'].includes(row.status));
  const icon = (status: string) => status === 'completed' ? <Icon name="check" size={14} className="ui-ok" /> : ['in_progress', 'inProgress', 'running'].includes(status) ? <Icon name="dot" size={14} className="ui-info" />
    : status === 'blocked' ? <Icon name="warning" size={14} className="ui-warn" /> : status === 'abandoned' ? <Icon name="x" size={14} className="ui-muted" /> : <Icon name="circle" size={14} className="ui-muted" />;
  const tone = (status: string): BadgeTone => status === 'completed' ? 'ok' : ['in_progress', 'inProgress', 'running'].includes(status) ? 'info' : status === 'blocked' ? 'warn' : 'neutral';
  const deleteButton = (key: string, label: string, run: () => void) => armed === key
    ? <button type="button" className="btn small subtle ui-danger-confirm" data-testid={`plan-delete-confirm-${key}`} disabled={!canEdit} onClick={() => arm(key, run)}>{ko ? '삭제 확인' : 'Confirm'}</button>
    : <IconButton icon="trash" tone="danger" label={label} disabled={!canEdit} onClick={() => arm(key, run)} testId={`plan-delete-${key}`} />;
  return <ViewSection sectionKey="work-plan" className="agent-plan" headingLevel="h2" active={running} inert={inert || undefined} bodyClassName="ui-flush"
    title={ko ? '작업 계획' : 'Work plan'} status={total ? `${total - open}/${total}` : 0}
    actions={<>
      <button type="button" className="btn small subtle" data-testid="plan-continue" disabled={disabled || busy || !total || session?.hasPendingAsyncWork} onClick={() => onContinue()}><Icon name="play" size={12} />{ko ? '이어가기' : 'Continue'}</button>
      <button type="button" className="btn small subtle" disabled={disabled || !busy} onClick={onStop}><Icon name="stop" size={11} />{ko ? '중단' : 'Stop'}</button>
      <button type="button" className="btn small" data-testid="plan-add" disabled={!canEdit} onClick={startAdd}><Icon name="plus" size={12} />{ko ? '작업 추가' : 'Add task'}</button>
    </>}>
    {total > 0 && <div className="ui-note"><div className="ui-progress" aria-hidden="true"><i style={{ width: `${Math.round((total - open) / total * 100)}%` }} /></div></div>}
    <Toolbar>
      <SearchBox value={query} onChange={setQuery} label={view === 'tasks' ? (ko ? '작업 검색' : 'Search tasks') : (ko ? '단계 검색' : 'Search phases')} testId="plan-search" />
      <Segmented<'tasks' | 'phases'> label={ko ? '보기' : 'View'} value={view} onChange={next => { setView(next); setArmed(undefined); }} testId="plan-view" options={[
        { id: 'tasks', label: ko ? '작업' : 'Tasks', badge: total, testId: 'plan-view-tasks' }, { id: 'phases', label: ko ? '단계' : 'Phases', badge: phases.length, testId: 'plan-view-phases' }]} />
    </Toolbar>
    {view === 'tasks' && <>
      <DataTable columns="18px minmax(0, 1fr) minmax(0, .45fr) auto auto" label={ko ? '작업 계획' : 'Work plan'} testId="plan-tasks" empty={!rows.length}
        head={['', ko ? '작업' : 'Task', ko ? '단계' : 'Phase', ko ? '상태' : 'Status', '']}>
        {rows.map(row => {
          const key = `${row.phase}-${row.task}`; const name = phaseName.get(row.phase) ?? '';
          return <Row key={key} testId={`plan-task-${key}`} selected={draft?.mode === 'edit' && draft.phase === row.phase && draft.task === row.task}>
            <Cell className="ui-status-icon" title={stateLabel(row.status, locale)}>{icon(row.status)}</Cell>
            <Cell wrap>{revealControls(row.content)}{row.blocker ? <span className="ui-sub">{ko ? '대기: ' : 'Waiting on: '}{revealControls(row.blocker)}</span> : null}</Cell>
            <Cell className="ui-muted" title={name}>{revealControls(name)}</Cell>
            <Cell><Badge tone={tone(row.status)}>{revealControls(stateLabel(row.status, locale))}</Badge></Cell>
            <Actions>
              <IconButton icon="edit" label={ko ? '작업 편집' : 'Edit task'} disabled={!canEdit} onClick={() => startEdit(row)} testId={`plan-edit-${key}`} />
              {deleteButton(key, ko ? '작업 삭제' : 'Delete task', () => edit({ op: 'deleteTask', phase: row.phase, task: row.task }))}
            </Actions>
          </Row>;
        })}
      </DataTable>
      <Pager paged={pagedTasks} testId="plan-pager" />
    </>}
    {view === 'phases' && <>
      <DataTable columns="minmax(0, 1fr) auto auto" label={ko ? '단계' : 'Phases'} testId="plan-phases" empty={!pagedPhases.slice.length} head={[ko ? '단계' : 'Phase', ko ? '작업' : 'Tasks', '']}>
        {pagedPhases.slice.map(phase => <Row key={phase.index} testId={`plan-phase-${phase.index}`} selected={renaming?.phase === phase.index}>
          <Cell title={phase.name}>{revealControls(phase.name)}</Cell>
          <Cell className="ui-mono">{phase.taskCount}</Cell>
          <Actions>
            <IconButton icon="edit" label={ko ? '단계 이름 변경' : 'Rename phase'} disabled={!canEdit} onClick={() => { setDraft(null); setError(''); setRenaming({ phase: phase.index, name: phase.name }); }} testId={`plan-rename-${phase.index}`} />
            {deleteButton(`phase-${phase.index}`, ko ? '단계 삭제' : 'Delete phase', () => edit({ op: 'deletePhase', phase: phase.index }))}
          </Actions>
        </Row>)}
      </DataTable>
      <Pager paged={pagedPhases} testId="plan-phases-pager" />
    </>}
    {draft && <Form testId="plan-task-form" className="plan-task-form">
      <Field label={ko ? '단계' : 'Phase'}>{id => <select id={id} value={String(draft.phase)} disabled={pending || draft.mode === 'edit'}
        onChange={event => setDraft({ ...draft, phase: event.target.value === 'new' ? 'new' : Number(event.target.value) })}>
        {phases.map(phase => <option key={phase.index} value={phase.index}>{phase.name || (ko ? `단계 ${phase.index + 1}` : `Phase ${phase.index + 1}`)}</option>)}
        {draft.mode === 'add' && <option value="new">{ko ? '새 단계' : 'New phase'}</option>}
      </select>}</Field>
      {draft.phase === 'new' && <Field label={ko ? '새 단계 이름' : 'New phase name'}>{id => <input id={id} type="text" value={draft.phaseName} maxLength={limits.name} disabled={pending} onChange={event => setDraft({ ...draft, phaseName: event.target.value })} />}</Field>}
      <Field label={ko ? '내용' : 'Content'}>{id => <textarea id={id} rows={2} value={draft.content} maxLength={limits.content} disabled={pending} data-testid="plan-task-content" onChange={event => setDraft({ ...draft, content: event.target.value })} />}</Field>
      <Field label={ko ? '상태' : 'Status'}>{id => <select id={id} value={draft.status} disabled={pending} onChange={event => setDraft({ ...draft, status: event.target.value })}>{statusOptions}</select>}</Field>
      {draft.status === 'blocked' && draft.phase !== 'new' && <Field label={ko ? '대기 사유' : 'Blocker'}>{id => <input id={id} type="text" value={draft.blocker} maxLength={limits.blocker} disabled={pending} onChange={event => setDraft({ ...draft, blocker: event.target.value })} />}</Field>}
      <FormActions>
        <button type="button" className="btn" disabled={pending} onClick={() => { setDraft(null); setError(''); }}>{ko ? '취소' : 'Cancel'}</button>
        <button type="button" className="btn primary" data-testid="plan-task-save" disabled={!canEdit || !draftValid} onClick={saveDraft}>{ko ? '저장' : 'Save'}</button>
      </FormActions>
    </Form>}
    {renaming && <Form testId="plan-rename-form" className="plan-task-form">
      <Field label={ko ? '단계 이름' : 'Phase name'}>{id => <input id={id} type="text" value={renaming.name} maxLength={limits.name} disabled={pending} onChange={event => setRenaming({ ...renaming, name: event.target.value })} />}</Field>
      <FormActions>
        <button type="button" className="btn" disabled={pending} onClick={() => { setRenaming(null); setError(''); }}>{ko ? '취소' : 'Cancel'}</button>
        <button type="button" className="btn primary" data-testid="plan-rename-save" disabled={!canEdit || !renaming.name.trim() || !fits(renaming.name, limits.name)}
          onClick={() => edit({ op: 'renamePhase', phase: renaming.phase, name: renaming.name.trim() })}>{ko ? '저장' : 'Save'}</button>
      </FormActions>
    </Form>}
    {session?.hasPendingAsyncWork && <p className="ui-note ui-muted" role="status">{ko ? '백그라운드 작업이 남아 있어요' : 'Background work is still pending'}</p>}
    {error && <p className="ui-note" role="alert">{revealControls(error)}</p>}
  </ViewSection>;
}
