import { useEffect, useMemo, useRef, useState, type CSSProperties, type DragEvent, type ReactNode } from 'react';
import { useT } from '../i18n';
import { newId } from '../format';
import type { SendResult } from '../limits';
import { onMessage, send, type AgentPlanEdit, type AgentPlanPage, type SessionInfoPayload } from '../protocol';
import { revealControls } from './RevealControls';
import { stateLabel } from '../stateLabels';
import '../styles/work-tab.css';
import type { WorkLayout } from './WorkPanel';
import { ViewSection } from './ViewSection';
import { Icon } from './Icon';
import { Actions, Badge, Cell, DataTable, Field, Form, FormActions, IconButton, PAGE_SIZE, Pager, Row, SearchBox, Toolbar, type BadgeTone } from './ui';

const planStatuses = ['pending', 'in_progress', 'completed', 'abandoned'] as const;
const limits = { content: 600 };
// eslint-disable-next-line no-control-regex
const control = /[\u0000-\u001f\u007f-\u009f]/;
const fits = (text: string, max: number) => text.length <= max && !control.test(text);
type DistributiveOmit<T, K extends PropertyKey> = T extends unknown ? Omit<T, K> : never;
type EditOp = DistributiveOmit<AgentPlanEdit, 'requestId' | 'root' | 'sessionId' | 'revision'>;
type PlanRow = AgentPlanPage['rows'][number];
/** Phase indexes stay internal; new tasks append to the last existing group. */
interface TaskDraft { mode: 'add' | 'edit'; phase: number | 'new'; task?: number; content: string; status: string; revision: string }

/**
 * The whole agent plan, one page at a time: the host keeps the plan and answers pages (query, clamped page) and task
 * edits checked against the revision the page showed, so any number of tasks stays usable. A task is run on its own
 * (idle: `startTask`, then the chat request of `onRunTask`; while the agent works: only the request, queued after the current turn)
 * and moved by dragging its handle or with Alt+Up/Down on it (`moveTask`).
 */
export function AgentPlan({ root, session, sessionId, locale, disabled, busy, onContinue, onRunTask, onStop, layout = 'details', inert = false, selector, notices, actionPending = false, stopPending = false }: { root: string; session: SessionInfoPayload | null; sessionId?: string; locale: string; disabled: boolean; busy: boolean; onContinue: () => void; onRunTask: (task: string, marked: boolean) => SendResult; onStop: () => void; layout?: WorkLayout; inert?: boolean; selector?: ReactNode; notices?: ReactNode; actionPending?: boolean; stopPending?: boolean }) {
  const t = useT();
  const total = session?.planTaskCount ?? 0; const open = session?.planOpenCount ?? 0; const revision = session?.planRevision ?? '';
  const [query, setQuery] = useState(''); const [search, setSearch] = useState(''); const [page, setPage] = useState(0);
  const [data, setData] = useState<AgentPlanPage | null>(null);
  const [draft, setDraft] = useState<TaskDraft | null>(null);
  const [armed, setArmed] = useState<string>(); const armTimer = useRef<number | undefined>(undefined);
  const [pending, setPending] = useState(false); const [error, setError] = useState('');
  const pageRequest = useRef<string | undefined>(undefined); const editRequest = useRef<string | undefined>(undefined);
  // The run asked for: once its startTask is saved, the chat request goes out.
  const runRequest = useRef<{ id: string; content: string } | undefined>(undefined);
  const runTask = useRef(onRunTask);
  useEffect(() => { runTask.current = onRunTask; });
  // The task being dragged (by its handle), and the row the keyboard move puts the focus on once the moved plan is shown.
  const drag = useRef<{ nonce: string; phase: number; task: number; revision?: string } | null>(null);
  const focusMove = useRef<{ key: string; revision?: string } | undefined>(undefined);
  useEffect(() => { const timer = window.setTimeout(() => { setSearch(query.trim()); setPage(0); }, 250); return () => window.clearTimeout(timer); }, [query]);
  useEffect(() => {
    setDraft(null); setArmed(undefined); setPending(false); setError(''); setPage(0); setQuery(''); setSearch(''); setData(null); editRequest.current = undefined;
    runRequest.current = undefined; pageRequest.current = undefined; drag.current = null; focusMove.current = undefined;
  }, [root, sessionId]);
  useEffect(() => {
    if (inert || !sessionId) return;
    const id = newId(); pageRequest.current = id;
    if (!send('workspace/agentPlanPage', { requestId: id, root, sessionId, page, size: PAGE_SIZE, ...(search ? { query: search } : {}) })) pageRequest.current = undefined;
    // The work starting or ending changes what may be edited (editable, editableWhileWorking): the page is asked again.
  }, [inert, root, sessionId, page, search, revision, busy, session?.hasPendingAsyncWork]);
  useEffect(() => onMessage(message => {
    if (message.type === 'workspace/agentPlanPageResult') {
      if (message.payload.requestId !== pageRequest.current) return;
      pageRequest.current = undefined;
      if (!message.payload.ok) { setError(message.payload.error ?? t('work.plan.readFailed')); return; }
      setData(message.payload); setPage(message.payload.page);
      return;
    }
    if (message.type !== 'workspace/agentPlanResult' || message.payload.requestId !== editRequest.current) return;
    editRequest.current = undefined; setPending(false);
    const run = runRequest.current?.id === message.payload.requestId ? runRequest.current : undefined; runRequest.current = undefined;
    if (!message.payload.ok) { focusMove.current = undefined; setError(message.payload.error ?? t('work.plan.saveFailed')); return; }
    setDraft(null); setError('');
    if (!run) return;
    const result = runTask.current(run.content, true);
    if (!result.ok) setError(t(result.reason === 'hostUnavailable' ? 'error.hostUnavailable' : 'work.plan.runNotSent'));
  }), [t]);
  useEffect(() => {
    if (!pending) return;
    const timer = window.setTimeout(() => { editRequest.current = undefined; setPending(false); setError(t('workflows.unconfirmed')); }, 30000);
    return () => window.clearTimeout(timer);
  }, [pending, t]);
  useEffect(() => () => window.clearTimeout(armTimer.current), []);
  // A delete armed on one plan revision is not a confirmation for another: the rows (and their indexes) may have shifted meanwhile.
  useEffect(() => { window.clearTimeout(armTimer.current); setArmed(undefined); }, [data?.revision]);
  // The task editor opens at the top of the card body: its first field takes the focus, which also brings it into view from a row further down.
  const contentField = useRef<HTMLTextAreaElement>(null);
  useEffect(() => { if (draft) contentField.current?.focus(); }, [draft?.mode, draft?.phase, draft?.task]);
  useEffect(() => {
    const target = focusMove.current;
    if (!target || !data || data.revision === target.revision) return;
    focusMove.current = undefined;
    document.querySelector<HTMLElement>(`[data-testid="plan-move-${target.key}"]`)?.focus();
  }, [data]);
  const phases = useMemo(() => data?.phases ?? [], [data]);
  if (layout === 'details' && !total && !session?.hasPendingAsyncWork) return null;

  const rows = data?.rows ?? []; const matching = data?.total ?? 0; const pages = Math.max(1, Math.ceil(matching / PAGE_SIZE));
  const pagedTasks = { page, pages, total: matching, from: matching ? page * PAGE_SIZE + 1 : 0, to: Math.min(matching, page * PAGE_SIZE + PAGE_SIZE), setPage };
  const working = busy || !!session?.hasPendingAsyncWork;
  const usable = !disabled && !inert && !pending && !!sessionId;
  const canEdit = usable && !working && data?.editable === true;
  // While the agent works, leave its task in progress alone (the host checks again).
  const canEditWorking = usable && data?.editableWhileWorking === true;
  const inProgress = (status: string) => status === 'in_progress';
  const canEditRow = (row: PlanRow) => canEdit || (canEditWorking && !inProgress(row.status));
  const canAdd = canEdit || canEditWorking;
  // Completed tasks are never in progress: clearing them is taken while the agent works too.
  const canClear = canAdd && (data?.completedCount ?? 0) > 0;
  const edit = (op: EditOp, expectedRevision = data?.revision ?? ''): string | undefined => {
    if (!canAdd || !sessionId || !data) return undefined;
    const id = newId(); editRequest.current = id; setPending(true); setError('');
    if (!send('workspace/agentPlanEdit', { ...op, requestId: id, root, sessionId, revision: expectedRevision } as AgentPlanEdit)) { editRequest.current = undefined; setPending(false); setError(t('error.hostUnavailable')); return undefined; }
    return id;
  };
  // Run while idle: the task becomes the one in progress, then the chat asks for it. While the agent works the plan is not changed
  // under its turn: the request alone goes to the chat queue (or into the turn with Send now) and has the agent start the task itself.
  const scheduling = !canEdit && canEditWorking;
  const run = (row: PlanRow) => {
    if (!scheduling) {
      const id = edit({ op: 'startTask', phase: row.phase, task: row.task });
      if (id) runRequest.current = { id, content: row.content };
      return;
    }
    setError('');
    const result = onRunTask(row.content, false);
    if (!result.ok) setError(t(result.reason === 'hostUnavailable' ? 'error.hostUnavailable' : 'work.plan.scheduleFailed'));
  };
  // Move: like an edit of the row, so the task in progress stays put while the agent works (the host checks again).
  const taskCounts = new Map(phases.map(phase => [phase.index, phase.taskCount]));
  /** One step up or down the whole plan: past the first or last task of its phase the task goes to the end or start of the next phase. */
  const stepTarget = (row: PlanRow, step: -1 | 1): { toPhase: number; toTask: number } | undefined => {
    if (step < 0 && row.task > 0) return { toPhase: row.phase, toTask: row.task - 1 };
    if (step > 0 && row.task < (taskCounts.get(row.phase) ?? 0) - 1) return { toPhase: row.phase, toTask: row.task + 1 };
    const neighbour = phases[phases.findIndex(phase => phase.index === row.phase) + step];
    return neighbour ? { toPhase: neighbour.index, toTask: step < 0 ? neighbour.taskCount : 0 } : undefined;
  };
  const move = (from: { phase: number; task: number }, to: { toPhase: number; toTask: number }, keepFocus: boolean) => {
    if (from.phase === to.toPhase && from.task === to.toTask) return;
    // A phase its last task leaves is removed: the phases after it move up one.
    const shift = to.toPhase !== from.phase && taskCounts.get(from.phase) === 1 && from.phase < to.toPhase ? 1 : 0;
    const revisionBefore = data?.revision;
    if (edit({ op: 'moveTask', phase: from.phase, task: from.task, ...to }) && keepFocus) focusMove.current = { key: `${to.toPhase - shift}-${to.toTask}`, revision: revisionBefore };
  };
  const dragType = 'application/x-pomp-plan-order';
  const startDrag = (event: DragEvent<HTMLButtonElement>, row: PlanRow) => {
    if (!canEditRow(row)) { event.preventDefault(); return; }
    const nonce = newId(); drag.current = { nonce, phase: row.phase, task: row.task, revision: data?.revision };
    event.dataTransfer.effectAllowed = 'move'; event.dataTransfer.setData(dragType, nonce);
  };
  const dragOver = (event: DragEvent<HTMLDivElement>) => {
    if (!drag.current) return;
    event.preventDefault(); event.dataTransfer.dropEffect = 'move';
  };
  // Dropped on a row: the task takes that row's place (in its phase, also from another phase).
  const dropOn = (event: DragEvent<HTMLDivElement>, target: PlanRow) => {
    const source = drag.current; drag.current = null;
    if (!source || event.dataTransfer.getData(dragType) !== source.nonce) return;
    event.preventDefault();
    if (source.revision !== data?.revision) return;
    move(source, { toPhase: target.phase, toTask: target.task }, false);
  };
  // Delete is pressed twice on the same row: the first press arms it for ten seconds.
  const arm = (key: string, run: () => void) => {
    window.clearTimeout(armTimer.current);
    if (armed !== key) { setArmed(key); armTimer.current = window.setTimeout(() => setArmed(current => (current === key ? undefined : current)), 10000); return; }
    setArmed(undefined); run();
  };
  const startAdd = () => { setError(''); setDraft({ mode: 'add', phase: phases.length ? phases[phases.length - 1].index : 'new', content: '', status: 'pending', revision: data?.revision ?? '' }); };
  const startEdit = (row: PlanRow) => { setError(''); setDraft({ mode: 'edit', phase: row.phase, task: row.task, content: row.content, status: row.status, revision: data?.revision ?? '' }); };
  const draftValid = !!draft && !!draft.content.trim() && fits(draft.content, limits.content);
  const draftRow = draft?.mode === 'edit' ? rows.find(row => row.phase === draft.phase && row.task === draft.task) : undefined;
  const canSaveDraft = canAdd && !!draft && (canEdit || (!inProgress(draft.status) && (draft.mode === 'add' || (!!draftRow && !inProgress(draftRow.status)))));
  const saveDraft = () => {
    if (!draft || !draftValid || !canSaveDraft) return;
    const content = draft.content.trim();
    if (draft.mode === 'edit' && typeof draft.phase === 'number' && draft.task !== undefined) edit({ op: 'updateTask', phase: draft.phase, task: draft.task, content, status: draft.status }, draft.revision);
    else if (draft.phase === 'new') edit({ op: 'addPhase', name: t('work.plan.defaultGroup'), content, status: draft.status }, draft.revision);
    else edit({ op: 'addTask', phase: draft.phase, content, status: draft.status }, draft.revision);
  };
  const statusOptions = planStatuses.filter(status => canEdit || status !== 'in_progress').map(status => <option key={status} value={status}>{stateLabel(status, locale)}</option>);
  const running = busy || !!session?.hasPendingAsyncWork || rows.some(row => inProgress(row.status));
  const icon = (status: string) => status === 'completed' ? <Icon name="check" size={14} className="ui-ok" /> : inProgress(status) ? <Icon name="dot" size={14} className="ui-info" />
    : status === 'abandoned' ? <Icon name="x" size={14} className="ui-muted" /> : <Icon name="circle" size={14} className="ui-muted" />;
  const tone = (status: string): BadgeTone => status === 'completed' ? 'ok' : inProgress(status) ? 'info' : 'neutral';
  const deleteButton = (key: string, label: string, enabled: boolean, run: () => void) => armed === key
    ? <button type="button" className="btn small danger" data-testid={`plan-delete-confirm-${key}`} disabled={!enabled} onClick={() => arm(key, run)}>{t('work.confirmDelete')}</button>
    : <IconButton icon="trash" tone="danger" label={label} disabled={!enabled} onClick={() => arm(key, run)} testId={`plan-delete-${key}`} />;
  return <ViewSection sectionKey="work-plan" className="agent-plan work-plan" headingLevel="h2" active={running} inert={inert || undefined} bodyClassName="ui-flush"
    title={t('work.plan.title')} status={total ? `${total - open}/${total}` : 0}
    actions={<>
      {selector}
      <button type="button" className="btn small" data-testid="plan-add" disabled={!canAdd} onClick={startAdd}><Icon name="plus" size={12} />{t('work.plan.add')}</button>
      <button type="button" className="btn small subtle" data-testid="plan-continue" disabled={disabled || inert || actionPending || pending || !total} onClick={onContinue}><Icon name="play" size={12} />{t(working ? 'work.plan.queueContinue' : 'work.plan.continue')}</button>
      <button type="button" className="btn small subtle" data-testid="plan-stop" disabled={disabled || inert || stopPending || (!working && !actionPending && !runRequest.current)} onClick={() => { runRequest.current = undefined; onStop(); }}><Icon name="stop" size={11} />{t('work.plan.stop')}</button>
      {armed === 'clear-completed'
        ? <button type="button" className="btn small danger" data-testid="plan-clear-completed-confirm" disabled={!canClear} onClick={() => arm('clear-completed', () => edit({ op: 'clearCompleted' }))}>{t('work.plan.clearConfirm')}</button>
        : <button type="button" className="btn small subtle" data-testid="plan-clear-completed" disabled={!canClear} onClick={() => arm('clear-completed', () => edit({ op: 'clearCompleted' }))}><Icon name="trash" size={12} />{t('work.plan.clearCompleted')}</button>}
    </>}>
    {notices}
    {draft && <Form testId="plan-task-form">
      <Field label={t('work.plan.content')}>{id => <textarea id={id} ref={contentField} rows={2} value={draft.content} maxLength={limits.content} disabled={pending} data-testid="plan-task-content" onChange={event => setDraft({ ...draft, content: event.target.value })} />}</Field>
      <Field label={t('ui.col.status')}>{id => <select id={id} value={draft.status} disabled={pending} onChange={event => setDraft({ ...draft, status: event.target.value })}>{statusOptions}</select>}</Field>
      <FormActions>
        <button type="button" className="btn" disabled={pending} onClick={() => { setDraft(null); setError(''); }}>{t('ui.cancel')}</button>
        <button type="button" className="btn primary" data-testid="plan-task-save" disabled={!canSaveDraft || !draftValid} onClick={saveDraft}>{t('ui.save')}</button>
      </FormActions>
    </Form>}
    {total > 0 && <div className="ui-note"><div className="ui-progress" aria-hidden="true"><i style={{ '--ui-progress': `${Math.round((total - open) / total * 100)}%` } as CSSProperties} /></div></div>}
    <Toolbar>
      <SearchBox value={query} onChange={setQuery} label={t('work.plan.search')} testId="plan-search" />
    </Toolbar>
      <DataTable columns="auto 18px minmax(0, 1fr) auto auto" label={t('work.plan.title')} testId="plan-tasks" empty={!rows.length}
        head={['', '', t('work.plan.task'), t('ui.col.status'), '']}>
        {rows.map(row => {
          const key = `${row.phase}-${row.task}`;
          return <Row key={key} testId={`plan-task-${key}`} selected={draft?.mode === 'edit' && draft.phase === row.phase && draft.task === row.task}
            onDragOver={dragOver} onDrop={event => dropOn(event, row)}>
            <Cell><button type="button" className="icon-btn ui-icon-btn plan-task-handle" data-testid={`plan-move-${key}`} draggable={canEditRow(row)} disabled={!canEditRow(row)}
              title={t('work.plan.reorder')} aria-label={t('work.plan.reorder')} aria-keyshortcuts="Alt+ArrowUp Alt+ArrowDown"
              onDragStart={event => startDrag(event, row)} onDragEnd={() => { drag.current = null; }}
              onKeyDown={event => {
                if (!event.altKey || (event.key !== 'ArrowUp' && event.key !== 'ArrowDown')) return;
                event.preventDefault();
                const target = stepTarget(row, event.key === 'ArrowUp' ? -1 : 1);
                if (target) move(row, target, true);
              }}><Icon name="grip" size={14} /></button></Cell>
            <Cell className="ui-status-icon" title={stateLabel(row.status, locale)}>{icon(row.status)}</Cell>
            <Cell wrap>{revealControls(row.content)}</Cell>
            <Cell><Badge tone={tone(row.status)}>{revealControls(stateLabel(row.status, locale))}</Badge></Cell>
            <Actions>
              {/* While the agent works, the task it is on needs no run of its own. */}
              <IconButton icon="play" label={t(scheduling ? 'work.plan.runAfter' : 'work.plan.run')}
                disabled={!canAdd || actionPending || row.status === 'completed' || (scheduling && inProgress(row.status))} onClick={() => run(row)} testId={`plan-run-${key}`} />
              <IconButton icon="edit" label={t('work.plan.edit')} disabled={!canEditRow(row)} onClick={() => startEdit(row)} testId={`plan-edit-${key}`} />
              {deleteButton(key, t('work.plan.delete'), canEditRow(row), () => edit({ op: 'deleteTask', phase: row.phase, task: row.task }))}
            </Actions>
          </Row>;
        })}
      </DataTable>
      <Pager paged={pagedTasks} testId="plan-pager" />
    {session?.hasPendingAsyncWork && <p className="ui-note ui-muted" role="status">{t('work.plan.backgroundPending')}</p>}
    {error && <p className="ui-note" role="alert">{revealControls(error)}</p>}
  </ViewSection>;
}
