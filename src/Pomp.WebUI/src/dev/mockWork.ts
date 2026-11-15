/**
 * Dev-only Work tab data for the mock host (`?work=1`, `?work=active` for a running workflow, `?work=many` for thousands of plan
 * tasks and applied-change records): a plan, a change proposal, an isolated member result, applied-change records, problems and
 * editor changes, all answering their commands the way the real host does (pages, queries, revisions).
 */
import type { AgentPlanPhase, ComposerWorkflowState, HostToUi, IsolationResult, UiToHost, WorkspaceHistoryState, WorkspaceProblem, WorkspaceReviewState } from '../protocol';

type Post = <K extends keyof HostToUi>(type: K, payload: HostToUi[K]) => void;

export function createMockWork(post: Post, root: string, session: () => string, onPlanChanged: () => void, liveSessions: () => readonly string[]) {
  const mode = new URLSearchParams(window.location.search).get('work') ?? '';
  const many = mode === 'many';
  const enabled = mode === '1' || mode === 'active' || many;
  const ko = !new URLSearchParams(window.location.search).get('locale')?.startsWith('en');
  let initialPlan: AgentPlanPhase[] = enabled ? [
    { name: ko ? '할인 규칙 정리' : 'Discount rules', tasks: [
      { content: ko ? '주문 할인 규칙 정리' : 'Collect the order discount rules', status: 'completed' },
      { content: ko ? '할인 계산 테스트 작성' : 'Write discount calculation tests', status: 'completed' },
      { content: ko ? 'CalculateTotal 수정' : 'Fix CalculateTotal', status: 'in_progress' },
    ] },
    { name: ko ? '확인' : 'Verify', tasks: [
      { content: ko ? '회원 할인 경계값 처리' : 'Member discount boundaries', status: 'pending' },
      { content: ko ? '빌드와 테스트 실행' : 'Build and run the tests', status: 'pending' },
    ] },
  ] : [];
  if (many) initialPlan = Array.from({ length: 12 }, (_, p) => ({ name: (ko ? '단계 ' : 'Phase ') + (p + 1), tasks: Array.from({ length: 250 }, (_, t) => ({
    content: (ko ? '작업 ' : 'Task ') + (p * 250 + t + 1) + (t % 7 === 0 ? (ko ? ' 테스트 보강' : ' strengthen tests') : ''), status: p < 4 ? 'completed' : p === 4 && t < 30 ? 'in_progress' : 'pending' })) }));
  const plans = new Map<string, { phases: AgentPlanPhase[]; revision: number; running: boolean }>();
  const planFor = (id: string) => {
    let value = plans.get(id);
    if (!value) {
      value = { phases: plans.size === 0 ? initialPlan : [], revision: 1, running: false };
      plans.set(id, value);
    }
    return value;
  };
  const planInfo = (id = session()) => {
    const { phases: plan, revision: planRevision } = planFor(id);
    const tasks = plan.flatMap(phase => phase.tasks); let left = 200;
    const first = plan.slice(0, 20).map(phase => { const taken = phase.tasks.slice(0, Math.max(0, left)); left -= taken.length; return { name: phase.name, tasks: taken }; }).filter(phase => phase.tasks.length);
    return { todoPhases: first, planTruncated: tasks.length > 200, planTaskCount: tasks.length, planOpenCount: tasks.filter(task => !['completed', 'abandoned'].includes(task.status)).length, planRevision: tasks.length ? 'r' + planRevision : undefined };
  };
  const editorFiles = Array.from({ length: many ? 1200 : 14 }, (_, i) => ({ path: `${root}\\src\\${['Services', 'Models', 'Tests', 'Data'][i % 4]}\\File${i + 1}.cs`, unsaved: i % 5 === 0 }));
  let revision = 1;
  const file = (path: string, hunks: Array<[number, number, string, string]>) => ({ path: `${root}\\${path}`, coarse: false, hunks: hunks.map(([oldLine, newLine, before, after], id) => ({ id, oldLine, newLine, before, after, selected: true })) });
  let review: WorkspaceReviewState | undefined = enabled ? {
    id: 'proposal-1', revision, status: 'ready', message: '', canApply: true, nativeDiffAvailable: true,
    files: [
      file('Services\\OrderService.cs', [[31, 31, '        var total = items.Sum(i => i.Price);\n', '        var total = items.Sum(i => i.Price * i.Quantity);\n        total -= discount.For(customer, total);\n'], [58, 59, '        return total;\n', '        return Math.Max(0, total);\n']]),
      file('Tests\\DiscountTests.cs', [[0, 1, '', 'public class DiscountTests\n{\n    [Fact]\n    public void Member_discount_applies_after_bulk() { }\n}\n']]),
    ],
    diagnostics: [], outcomes: [],
  } : undefined;
  let isolation: IsolationResult[] = enabled ? [{
    id: 'iso-1', root, agent: ko ? '구현자' : 'implementer', job: 'job-1', status: 'pending', message: '', createdAt: new Date(Date.now() - 600_000).toISOString(),
    patchPath: `${root}\\.pomp\\isolation\\iso-1.patch`,
    files: [{ path: 'Models/Order.cs', kind: 'modify', added: 6, removed: 2 }, { path: 'Models/Coupon.cs', kind: 'create', added: 40, removed: 0 }],
  }] : [];
  const names = ['OrderService.cs', 'Program.cs', 'Order.cs', 'DiscountTests.cs', 'Coupon.cs', 'Startup.cs'];
  let history: NonNullable<WorkspaceHistoryState['entries']> = enabled ? Array.from({ length: many ? 2500 : 26 }, (_, i) => ({
    id: `h${i}`, createdUtc: new Date(Date.now() - (i + 1) * 3_700_000).toISOString(), state: i === 2 ? 'restored' : 'applied',
    files: names.slice(0, 1 + (i % 3)).map(name => `${root}\\src\\${name}`),
  })) : [];
  let problems: WorkspaceProblem[] = enabled ? [
    { id: 'p1', source: 'compiler', severity: 'error', code: 'CS0103', message: ko ? "'discount' 이름이 현재 컨텍스트에 없습니다." : "The name 'discount' does not exist in the current context", path: `${root}\\Services\\OrderService.cs`, line: 31 },
    { id: 'p2', source: 'compiler', severity: 'warning', code: 'CS0168', message: ko ? "'unusedProbe' 변수가 선언되었지만 사용되지 않았습니다." : "The variable 'unusedProbe' is declared but never used", path: `${root}\\Services\\OrderService.cs`, line: 22 },
    { id: 'p3', source: 'compiler', severity: 'warning', code: 'CA1822', message: ko ? '멤버를 static으로 표시하세요.' : 'Mark members as static', path: `${root}\\Models\\Order.cs`, line: 12 },
    { id: 'p4', source: 'agent', severity: 'warning', code: 'review', message: ko ? '회원 할인이 대량 할인 전에 적용됩니다. 대량 할인 기준(수량 10개 이상)은 할인 전 금액으로 판단해야 하는데, 지금은 회원 할인을 먼저 빼고 남은 금액으로 판단해서 경계 근처 주문이 대량 할인을 받지 못합니다.' : 'The member discount is applied before the bulk discount. The bulk threshold (10 or more) must be judged on the amount before discounts; now the member discount is taken first, so orders near the threshold miss the bulk discount.', path: `${root}\\Services\\OrderService.cs`, line: 40,
      evidence: 'var total = order.Subtotal;\nif (order.Customer.IsMember) total -= total * MemberRate;\nif (total >= BulkThreshold) total -= total * BulkRate;', reporter: 'reviewer-strict' },
    { id: 'p5', source: 'agent', severity: 'warning', code: 'agent-review', message: ko ? '주문이 비어 있을 때 할인 계산이 0으로 나눕니다.' : 'The discount divides by zero for an empty order.', path: `${root}\\Services\\OrderService.cs`, line: 52,
      evidence: 'var average = total / order.Lines.Count;', reporter: 'main' },
  ] : [];
  let workflow: ComposerWorkflowState = { root, sessionId: '', id: '', revision: 0, phase: 'empty', paused: false, task: '', steps: [], message: '', proposalId: '', outcomes: [], discoveryObserved: false, applied: false, verificationObserved: false };
  if (mode === 'active') workflow = { ...workflow, id: 'wf-1', revision: 3, phase: 'editing', task: ko ? '주문 할인 계산을 회원·대량 구매 규칙에 맞게 고치고 테스트를 추가한다.' : 'Fix the order discount for member and bulk rules and add tests.', steps: ko ? ['규칙 정리', '테스트 작성', '코드 수정', '빌드와 테스트 실행'] : ['Collect rules', 'Write tests', 'Change code', 'Build and test'], outcomes: [{ path: 'Services/OrderService.cs', state: 'pending' }] };

  const postReview = () => { if (review) post('workspace/reviewState', review); };
  const postIsolation = (requestId?: string) => post('workspace/isolationState', { requestId: requestId ?? null, items: isolation });
  let historyView = { page: 0, size: 20, query: '' };
  const postHistory = () => {
    const matching = history.filter(entry => !historyView.query || entry.files.some(path => path.toLowerCase().includes(historyView.query.toLowerCase())));
    const page = Math.min(historyView.page, Math.max(0, Math.ceil(matching.length / historyView.size) - 1));
    post('workspace/historyState', { root, entries: matching.slice(page * historyView.size, page * historyView.size + historyView.size), total: matching.length, page, size: historyView.size, query: historyView.query });
  };
  const postProblems = () => post('workspace/problemsState', { root, items: problems, truncated: false, updatedUtc: new Date().toISOString() });
  // Like the host, a workflow keeps the conversation that owns it: the first conversation to see a preset run, then whoever saves, starts or resumes it.
  const postWorkflow = (controlRequestId?: string, controlError?: string) => {
    if (workflow.id && !workflow.sessionId) workflow = { ...workflow, sessionId: session() };
    post('workspace/composerState', { ...workflow, ...(controlRequestId ? { controlRequestId, controlError: controlError ?? null } : {}) });
  };
  const bump = () => { if (review) review = { ...review, revision: ++revision }; };

  function handle(type: string, payload: unknown): boolean {
    const planCommand = type.startsWith('workspace/agentPlan');
    if (!enabled && !planCommand) return false;
    if (planCommand) {
      const fields = payload && typeof payload === 'object' ? payload : {};
      const id = 'sessionId' in fields && typeof fields.sessionId === 'string' ? fields.sessionId : '';
      const requestId = 'requestId' in fields && typeof fields.requestId === 'string' ? fields.requestId : '';
      if (!('root' in fields) || fields.root !== root || !liveSessions().includes(id)) {
        const error = ko ? '작업 계획의 대화나 작업 영역이 바뀌었어요.' : 'The plan conversation or workspace changed.';
        if (type === 'workspace/agentPlanPage') post('workspace/agentPlanPageResult', { requestId, ok: false, error, page: 0, size: 20, total: 0, completedCount: 0, phases: [], rows: [] });
        else post('workspace/agentPlanResult', { requestId, ok: false, error });
        return true;
      }
    }
    switch (type) {
      case 'workspace/review': {
        const command = payload as UiToHost['workspace/review'];
        if (command.action === 'editorChanges') { window.setTimeout(() => post('workspace/editorChanges', { requestId: command.requestId ?? '', action: 'editorChanges', ok: true, files: editorFiles }), 150); return true; }
        if (command.action === 'fromEditor') {
          if (review && !['done', 'empty', 'discarded', 'cancelled'].includes(review.status)) { post('workspace/editorChanges', { requestId: command.requestId ?? '', action: 'fromEditor', ok: false, error: ko ? '검토 중인 제안이 있어요.' : 'A proposal is already awaiting review.' }); return true; }
          review = { id: 'editor-' + (++revision), revision, status: 'ready', origin: 'editor', message: '', canApply: true, nativeDiffAvailable: true, diagnostics: [], outcomes: [],
            files: (command.paths ?? []).map(path => ({ path, coarse: false, hunks: [{ id: 0, oldLine: 12, newLine: 12, before: '        return items.Count;\n', after: '        return items.Count(i => i.IsActive);\n', selected: true }, { id: 1, oldLine: 40, newLine: 40, before: '', after: '        // TODO: cache\n', selected: true }] })) };
          // The host publishes the staged proposal first and answers the request after it.
          postReview(); window.setTimeout(() => post('workspace/editorChanges', { requestId: command.requestId ?? '', action: 'fromEditor', ok: true }), 120); return true;
        }
        if (!review) return true;
        if (command.action === 'select') { review = { ...review, files: review.files.map(f => f.path === command.path ? { ...f, hunks: f.hunks.map(h => ({ ...h, selected: (command.selected ?? []).includes(h.id) })) } : f) }; bump(); }
        else if (command.action === 'amend') { review = { ...review, files: review.files.map(f => f.path === command.path ? { ...f, hunks: f.hunks.map(h => h.id === command.hunkId ? { ...h, after: command.after ?? h.after } : h) } : f) }; bump(); }
        else if (command.action === 'validate') { review = { ...review, status: 'ready', diagnostics: [{ message: ko ? '컴파일 검사 통과' : 'Compilation passed', isError: false }] }; bump(); }
        else if (command.action === 'apply') {
          const applied = review.files.filter(f => f.hunks.some(h => h.selected));
          history = [{ id: `h-new-${revision}`, createdUtc: new Date().toISOString(), state: 'applied', files: applied.map(f => f.path) }, ...history];
          review = { ...review, status: 'done', outcomes: applied.map(f => ({ path: f.path, state: 'applied' })) }; bump(); postHistory();
        } else if (command.action === 'discard') { review = { ...review, status: 'discarded' }; bump(); }
        window.setTimeout(postReview, 120); return true;
      }
      case 'workspace/history': {
        const command = payload as UiToHost['workspace/history'];
        if (command.action === 'refresh') historyView = { page: Math.max(0, command.page ?? historyView.page), size: Math.min(100, Math.max(1, command.size ?? historyView.size)), query: (command.query ?? '').trim() };
        if (command.action === 'delete') { const ids = command.ids ?? (command.id ? [command.id] : []); history = history.filter(entry => !ids.includes(entry.id)); post('workspace/historyReply', { requestId: command.requestId, root, ok: true, deleted: ids.length }); }
        else if (command.action === 'restore') post('workspace/historyReply', { requestId: command.requestId, root, ok: false, error: ko ? '미리보기에서는 되돌릴 수 없어요.' : 'Restoring needs the real host.' });
        window.setTimeout(postHistory, 80); return true;
      }
      case 'workspace/isolation': {
        const command = payload as UiToHost['workspace/isolation'];
        if (command.action === 'apply') isolation = isolation.map(item => item.id === command.id ? { ...item, status: 'applied' } : item);
        if (command.action === 'discard') isolation = isolation.filter(item => item.id !== command.id);
        window.setTimeout(() => postIsolation(command.requestId), 120); return true;
      }
      case 'workspace/problems': {
        const command = payload as UiToHost['workspace/problems'];
        if (command.action === 'dismiss') problems = problems.filter(item => item.id !== command.id);
        post('workspace/problemsReply', { requestId: command.requestId, ok: true }); postProblems(); return true;
      }
      case 'workspace/composer': {
        const command = payload as UiToHost['workspace/composer'];
        if (command.action === 'save' || command.action === 'start') workflow = { ...workflow, sessionId: session(), id: workflow.id || 'wf-new', revision: workflow.revision + 1, phase: command.action === 'start' ? 'discovery' : 'planning', task: command.task ?? workflow.task, steps: command.steps ?? workflow.steps };
        else if (command.action === 'pause') workflow = { ...workflow, revision: workflow.revision + 1, paused: true };
        else if (command.action === 'resume') workflow = { ...workflow, sessionId: session(), revision: workflow.revision + 1, paused: false };
        else if (command.action === 'cancel') workflow = { ...workflow, revision: workflow.revision + 1, phase: 'cancelled' };
        window.setTimeout(() => postWorkflow(command.requestId), 100); return true;
      }
      case 'workspace/agentPlanPage': {
        const command = payload as UiToHost['workspace/agentPlanPage']; const q = (command.query ?? '').toLowerCase();
        const { phases: plan, revision: planRevision, running } = planFor(command.sessionId);
        const rows = plan.flatMap((phase, p) => phase.tasks.map((task, t) => ({ phase: p, task: t, content: task.content, status: task.status })))
          .filter(row => !q || row.content.toLowerCase().includes(q) || plan[row.phase].name.toLowerCase().includes(q));
        const size = Math.min(100, Math.max(1, command.size)); const page = Math.min(Math.max(0, command.page), Math.max(0, Math.ceil(rows.length / size) - 1));
        window.setTimeout(() => post('workspace/agentPlanPageResult', { requestId: command.requestId, ok: true, revision: 'r' + planRevision, editable: !running, editableWhileWorking: true, page, size, total: rows.length,
          completedCount: plan.reduce((count, phase) => count + phase.tasks.filter(task => task.status === 'completed').length, 0),
          phases: plan.map((phase, index) => ({ index, name: phase.name, taskCount: phase.tasks.length })), rows: rows.slice(page * size, page * size + size) }), 60);
        return true;
      }
      case 'workspace/agentPlanEdit': {
        const command = payload as UiToHost['workspace/agentPlanEdit'];
        const model = planFor(command.sessionId);
        const { phases: plan, revision: planRevision } = model;
        const fail = (error: string) => window.setTimeout(() => post('workspace/agentPlanResult', { requestId: command.requestId, ok: false, error }), 80);
        if (command.revision !== 'r' + planRevision) { fail(ko ? '그 사이 계획이 바뀌었어요.' : 'The plan changed meanwhile.'); return true; }
        const next = plan.map(phase => ({ name: phase.name, tasks: phase.tasks.map(task => ({ ...task })) }));
        if (command.op === 'addTask') next[command.phase]?.tasks.push({ content: command.content, status: command.status ?? 'pending' });
        else if (command.op === 'updateTask') { const task = next[command.phase]?.tasks[command.task]; if (task) { if (command.content !== undefined) task.content = command.content; if (command.status) task.status = command.status; } }
        else if (command.op === 'deleteTask') { next[command.phase]?.tasks.splice(command.task, 1); if (next[command.phase] && !next[command.phase].tasks.length) next.splice(command.phase, 1); }
        else if (command.op === 'addPhase') next.push({ name: command.name, tasks: [{ content: command.content, status: command.status ?? 'pending' }] });
        else if (command.op === 'clearCompleted') {
          if (!next.some(phase => phase.tasks.some(task => task.status === 'completed'))) { fail(ko ? '완료된 작업이 없어요.' : 'There are no completed tasks.'); return true; }
          const cleared = next.filter(phase => phase.tasks.some(task => task.status === 'completed'));
          for (const phase of cleared) phase.tasks = phase.tasks.filter(task => task.status !== 'completed');
          next.splice(0, next.length, ...next.filter(phase => phase.tasks.length || !cleared.includes(phase)));
        }
        else if (command.op === 'moveTask') {
          const from = next[command.phase]; const into = next[command.toPhase]; const task = from?.tasks[command.task];
          if (!from || !into || !task) { fail(ko ? '계획에 없는 단계나 작업이에요.' : 'That phase or task is not in the plan.'); return true; }
          from.tasks.splice(command.task, 1); into.tasks.splice(command.toTask, 0, task);
          if (!from.tasks.length) next.splice(next.indexOf(from), 1);
        }
        else if (command.op === 'startTask') {
          const task = next[command.phase]?.tasks[command.task];
          if (!task || task.status === 'completed') { fail(ko ? '이미 완료된 작업이에요.' : 'That task is already completed.'); return true; }
          for (const other of next.flatMap(phase => phase.tasks)) if (other.status === 'in_progress') other.status = 'pending';
          task.status = 'in_progress';
        }
        model.phases = next; model.revision++; onPlanChanged();
        window.setTimeout(() => post('workspace/agentPlanResult', { requestId: command.requestId, ok: true }), 120); return true;
      }
      case 'workspace/agentPlan': {
        const command = payload as UiToHost['workspace/agentPlan'];
        const model = planFor(command.sessionId);
        model.phases = command.phases; model.revision++; onPlanChanged();
        window.setTimeout(() => post('workspace/agentPlanResult', { requestId: command.requestId, ok: true }), 120); return true;
      }
      case 'workspace/agentPlanAction': {
        // In-process mock envelopes have already passed the root/session guard; validate action-specific fields here.
        const command = payload as Record<string, unknown>;
        const model = planFor(String(command.sessionId));
        if (command.action !== 'stop' && command.action !== 'continue' && (command.action !== 'run' || typeof command.task !== 'string' || !command.task.trim())) {
          post('workspace/agentPlanResult', { requestId: String(command.requestId), ok: false, error: 'Invalid plan action.' }); return true;
        }
        model.running = command.action !== 'stop';
        onPlanChanged();
        post('workspace/agentPlanResult', { requestId: String(command.requestId), ok: true });
        return true;
      }
      default: return false;
    }
  }
  function snapshot(): void {
    if (!enabled) return;
    postReview(); postIsolation(); postHistory(); postProblems(); postWorkflow();
  }
  return { enabled, handle, snapshot, sessionInfo: planInfo, isPlanRunning: (id: string) => planFor(id).running };
}
