import type { HostMessageType, HostToUi, UiToHost } from '../protocol';
import type { AgentHistoryEntry, ConversationPartner, HistoryFilter, QueueState } from '../conversationProtocol';

/** Browser-only chat tabs, queue and bounded history fixtures. Never invokes a provider. */
export function createMockConversations(post: <K extends HostMessageType>(type: K, payload: HostToUi[K]) => void, options: {
  sessionId: () => string; busy: () => boolean; start: (input: UiToHost['chat/send']) => void;
}) {
  let counter = 0;
  // Agent and team conversations (the host's chat-tabs.json): their partners, the open tabs in order, and the Default tab's conversation.
  const partners = new Map<string, ConversationPartner>();
  let open: string[] = [];
  let main: string | null = options.sessionId();
  const same = (a?: ConversationPartner, b?: ConversationPartner) => !!a && !!b && a.kind === b.kind && a.id === b.id;
  const titleOf = (partner: ConversationPartner) => partner.kind === 'team' ? '기능 개발팀' : partner.kind === 'agent' ? '설계자' : undefined;
  let queue: QueueState = { sessionId: null, items: [], paused: false };
  let history: AgentHistoryEntry[] = [
    { id: 'history-feature', sessionId: options.sessionId(), target: { kind: 'team', id: 'feature-dev', title: '기능 개발팀' }, text: '설계와 구현 결과를 확인하고 회귀 테스트를 정리해 주세요.', startedAt: '2026-10-04T08:00:00Z', endedAt: '2026-10-04T08:02:11Z', status: 'completed', pinned: true, label: '확인할 결과', members: [{ agent: 'architect', jobId: 'job-mock-1', status: 'completed', toolCount: 5, tokens: 2400, durationMs: 12000 }, { agent: 'implementer', jobId: 'job-mock-2', status: 'completed', toolCount: 13, tokens: 6900, durationMs: 52000 }] },
    { id: 'history-review', sessionId: 'mock-session-2', target: { kind: 'agent', id: 'reviewer-strict', title: '꼼꼼한 리뷰어' }, text: '오류 경로와 예외 처리를 읽기 전용으로 검토해 주세요.', startedAt: '2026-10-04T07:00:00Z', endedAt: '2026-10-04T07:01:11Z', status: 'error', pinned: false, members: [{ agent: 'reviewer-strict', jobId: 'other-review', status: 'error', toolCount: 3, tokens: 840, durationMs: 71000 }] },
    { id: 'history-dm', sessionId: options.sessionId(), target: { kind: 'dm', id: 'job-mock-1', title: '설계자 #mock-1' }, text: '인터페이스 제안의 근거를 설명해 주세요.', startedAt: '2026-10-04T06:00:00Z', endedAt: '2026-10-04T06:00:20Z', status: 'aborted', pinned: false, members: [{ agent: 'architect', jobId: 'job-mock-1', status: 'aborted', toolCount: 1, tokens: 210, durationMs: 20000 }] },
  ];
  const publishQueue = () => post('chat/queueState', structuredClone(queue));
  const publishHistory = () => post('agents/historyState', { entries: structuredClone(history), truncated: false });
  const publishTabs = () => {
    const partner = partners.get(options.sessionId()) ?? { kind: 'main' as const };
    post('conversation/tabsState', { sessionId: options.sessionId(), partner, title: titleOf(partner), main, tabs: open.map(id => ({ sessionId: id, partner: partners.get(id)!, title: titleOf(partners.get(id)!) })) });
  };
  const snapshot = () => { publishTabs(); publishQueue(); publishHistory(); };
  const matches = (entry: AgentHistoryEntry, filter?: HistoryFilter) => (!filter?.kind || entry.target.kind === filter.kind) && (!filter?.id || entry.target.id === filter.id) && (!filter?.status || entry.status === filter.status) && (!entry.pinned || filter?.includePinned === true);
  // A team conversation sends every message to its team; an agent conversation is the agent itself (the message goes as typed).
  const route = (input: UiToHost['chat/send']): UiToHost['chat/send'] => {
    if (input.main || input.agent || input.team || input.agentInstance) return input;
    const partner = partners.get(options.sessionId());
    return partner?.kind === 'team' ? { ...input, team: partner.id } : input;
  };
  const drain = () => {
    if (options.busy() || queue.paused || !queue.items.length || queue.sessionId !== options.sessionId()) return;
    const item = queue.items.shift()!; if (!queue.items.length) queue.sessionId = null;
    publishQueue(); options.start({ clientMessageId: `queued-${item.id}`, text: item.text, attachments: item.attachments, includeEditorContext: item.includeEditorContext, team: item.team, agent: item.agent, agentInstance: item.agentInstance });
  };
  const enqueue = (input: UiToHost['chat/send']): boolean => {
    if (queue.items.length && queue.sessionId !== options.sessionId()) { post('error', { code: 'queue_rejected', message: 'This queue belongs to another conversation.', retryable: false }); return false; }
    const id = `queue-${++counter}`;
    queue.sessionId = options.sessionId(); queue.items.push({ id, text: input.text, includeEditorContext: input.includeEditorContext, attachments: input.attachments, team: input.team ?? undefined, agent: input.agent ?? undefined, agentInstance: input.agentInstance ?? undefined, createdAt: new Date().toISOString() });
    post('chat/queued', { clientMessageId: input.clientMessageId, itemId: id }); publishQueue(); return true;
  };
  const tryQueue = (input: UiToHost['chat/send']) => { if (!options.busy() && !queue.items.length) return false; enqueue(route(input)); return true; };
  const pause = (reason: string) => { if (!queue.items.length) return; queue.paused = true; queue.reason = reason; publishQueue(); };
  /** The current conversation changed; `assign` makes it that partner's new conversation. A listed one opens as its partner's tab, any other becomes Default's. */
  const changedSession = (assign?: ConversationPartner) => {
    const id = options.sessionId();
    if (assign && assign.kind !== 'main') partners.set(id, assign);
    const partner = partners.get(id);
    if (partner) { open = [...open.filter(other => other !== id && !same(partners.get(other), partner)), id]; if (main === id) main = null; }
    else main = id;
    pause('대화가 바뀌어 대기열을 멈췄어요.'); snapshot();
  };
  const tabs = {
    current: () => partners.get(options.sessionId()),
    seed: (id: string, partner: ConversationPartner) => { if (partner.kind !== 'main') partners.set(id, partner); },
    openSession: (partner: ConversationPartner) => open.find(id => same(partners.get(id), partner)),
    main: () => main,
    close: (id: string) => { open = open.filter(other => other !== id); publishTabs(); },
    forget: (id: string) => { open = open.filter(other => other !== id); partners.delete(id); if (main === id) main = null; },
  };
  const start = (input: UiToHost['chat/send'], promptId: string) => {
    const target = input.team ? { kind: 'team' as const, id: input.team, title: '기능 개발팀' } : input.agent ? { kind: 'agent' as const, id: input.agent, title: input.agent } : input.agentInstance ? { kind: 'dm' as const, id: input.agentInstance, title: input.agentInstance } : undefined;
    if (!target) return;
    history.unshift({ id: `history-${++counter}`, sessionId: options.sessionId(), promptId, target, text: input.text, startedAt: new Date().toISOString(), status: 'running', pinned: false, members: [] }); history = history.slice(0, 200); publishHistory();
  };
  const finish = (promptId: string, status: string) => {
    const entry = history.find(item => item.promptId === promptId);
    if (entry) { entry.status = status; entry.endedAt = new Date().toISOString(); publishHistory(); }
    if (status !== 'completed') pause(status === 'aborted' ? '중지 요청으로 대기열을 멈췄어요.' : '응답 오류로 대기열을 멈췄어요.'); else window.setTimeout(drain, 0);
  };
  function handle(type: string, payload: Record<string, unknown> | undefined): boolean {
    if (type === 'chat/queue') {
      const p = payload as unknown as UiToHost['chat/queue']; let error: string | undefined;
      const index = queue.items.findIndex(item => item.id === p.id);
      if (p.action === 'resume' && queue.items.length && queue.sessionId !== options.sessionId()) error = 'Return to the owning conversation to resume.';
      else if (p.action === 'pause') { queue.paused = true; queue.reason = '사용자가 대기열을 일시정지했어요.'; }
      else if (p.action === 'resume') { queue.paused = false; queue.reason = undefined; }
      else if (p.action === 'clear') queue = { sessionId: null, items: [], paused: false };
      else if (index < 0) error = 'Queue item no longer exists.';
      else if (p.action === 'remove') queue.items.splice(index, 1);
      else if (p.action === 'edit') { if (!p.text?.trim()) error = 'Message is empty.'; else queue.items[index]!.text = p.text; }
      else if (p.action === 'move') { const item = queue.items.splice(index, 1)[0]!; queue.items.splice(Math.max(0, Math.min(p.index ?? 0, queue.items.length)), 0, item); }
      if (!queue.items.length) queue.sessionId = null;
      post('chat/queueReply', { requestId: p.requestId, ok: !error, error }); publishQueue(); if (!error && p.action === 'resume') drain(); return true;
    }
    if (type === 'agents/history') {
      const p = payload as unknown as UiToHost['agents/history']; const entry = history.find(item => item.id === p.id); let error: string | undefined;
      if (p.action === 'clear') history = history.filter(item => !matches(item, p.filter));
      else if (p.action !== 'refresh' && !entry) error = 'Run record no longer exists.';
      else if (entry && p.action === 'delete') history = history.filter(item => item !== entry);
      else if (entry && (p.action === 'pin' || p.action === 'unpin')) entry.pinned = p.action === 'pin';
      else if (entry && p.action === 'label') { if ((p.label?.length ?? 0) > 80) error = 'Label limit: 80 characters.'; else entry.label = p.label || undefined; }
      else if (entry && p.action === 'rerun') { const target = entry.target.kind === 'team' ? { team: entry.target.id } : entry.target.kind === 'agent' ? { agent: entry.target.id } : { agentInstance: entry.target.id }; if (!enqueue({ clientMessageId: `rerun-${++counter}`, text: entry.text, includeEditorContext: true, ...target })) error = 'Could not queue this run.'; else window.setTimeout(drain, 0); }
      post('agents/historyReply', { requestId: p.requestId, ok: !error, error }); publishHistory(); return true;
    }
    return false;
  }
  const mode = new URLSearchParams(window.location.search).get('chatQueue');
  if (mode) queue = { sessionId: mode === 'other' ? 'mock-session-2' : options.sessionId(), paused: true, reason: mode === 'error' ? '응답 오류로 대기열을 멈췄어요.' : '사용자가 대기열을 일시정지했어요.', items: [{ id: 'fixture-queue', text: '완료된 변경과 검증 결과를 정리해 주세요.', includeEditorContext: true, agent: 'architect', createdAt: new Date().toISOString() }] };
  return { handle, route, tryQueue, snapshot, changedSession, start, finish, pause, tabs };
}
