import type { AcpCommand, AcpState, BackendId, BackendState, SessionCapabilities } from '../backend';
import type { ConversationPlan, QueueState } from '../conversationProtocol';
import type { AgentPlanTask, HostMessageType, HostToUi, UiToHost } from '../protocol';

/**
 * Browser-only conversation backends for the dev mock host: the backend choice (`conversation/backend` → `backend/state`) and a
 * scripted Antigravity (ACP) conversation told through the messages the real host posts for one (`bridge/state`, `session/info`,
 * `acp/state`, `acp/reply`, the chat frames, the queue). Nothing here starts a process or reaches a provider.
 *
 * `?acp=` opens the page with the Antigravity conversation already selected:
 *   connected (default when the parameter is present)   connects after a moment
 *   auth      sign-in is required, two methods; choosing one connects shortly after
 *   missing   the CLI is not installed: the start fails and Reconnect fails again
 *   error     the start fails once; Reconnect connects
 *   exited    connected; the CLI exits during the first prompt; Reconnect connects
 *   busy      connected with a turn already running
 * A prompt that contains "permission" (or "권한") asks for approval before the edit; Stop takes a moment, and a `/steer` sent
 * meanwhile is refused.
 */
export type AcpStart = 'connected' | 'auth' | 'missing' | 'error' | 'exited' | 'busy';
const STARTS: readonly AcpStart[] = ['connected', 'auth', 'missing', 'error', 'exited', 'busy'];

type Post = <K extends HostMessageType>(type: K, payload: HostToUi[K]) => void;
type Send = UiToHost['chat/send'];

export interface MockBackend {
  /** True for a frame of the native conversation while the Antigravity one is on screen: the page must not get it. */
  holds(type: string): boolean;
  /** Takes every backend choice, and the conversation's own messages while Antigravity is selected; false: not this module's. */
  handle(type: string, payload: unknown): boolean;
  /** `backend/state` of the `ui/ready` snapshot; with `?acp=` also the Antigravity conversation, which then starts. */
  snapshot(): void;
}

const CAPABILITIES: SessionCapabilities = {
  nativeOmp: false, queue: true, steer: true, stop: true, images: true, editorContext: true,
  plan: { read: true, edit: false, actions: false },
  history: { list: false, preview: false, resume: false, rename: false, delete: false, compact: false },
};
const MODELS = [
  { group: 'google', name: 'Google', options: [{ value: 'gemini-3-pro', name: 'Gemini 3 Pro', description: 'Most capable' }, { value: 'gemini-3-flash', name: 'Gemini 3 Flash', description: 'Fast' }] },
  { group: 'anthropic', name: 'Anthropic', options: [{ value: 'claude-sonnet-4-5', name: 'Claude Sonnet 4.5' }, { value: 'claude-opus-4-5', name: 'Claude Opus 4.5' }] },
  { group: 'open', name: 'Open models', options: [{ value: 'gpt-oss-120b', name: 'GPT-OSS 120B' }] },
];
const MODES = [{ value: 'agent', name: 'Agent', description: 'Edits files and runs tools' }, { value: 'plan', name: 'Plan', description: 'Plans without editing' }, { value: 'ask', name: 'Ask', description: 'Answers only' }];
const FILE = 'src/Orders/OrderService.cs';
const DIFF = `--- a/${FILE}\n+++ b/${FILE}\n@@ -41,7 +41,9 @@\n     public decimal Total(Order order)\n     {\n-        return order.Lines.Sum(line => line.Price);\n+        var sum = order.Lines.Sum(line => line.Price * line.Quantity);\n+        if (order.Member) sum *= 0.95m;\n+        return sum;\n     }\n`;
/** The native conversation's frames (and its questions): held back while the Antigravity conversation is the one shown. */
const NATIVE_FRAME = /^(?:chat\/|permission\/|dialog\/|session\/info$|bridge\/state$|conversation\/tabsState$)/u;

export function createMockBackend(post: Post, options: {
  root: string; ko: boolean;
  /** The native Default conversation's id (the tab strip's first tab). */
  nativeSession: () => string;
  /** Ends the native conversation's running turn (its frames are not shown while Antigravity is selected). */
  stopNative: () => void;
  /** Reposts the native conversation's snapshot when the page returns to it. */
  showNative: () => void;
}): MockBackend {
  const requested = new URLSearchParams(window.location.search).get('acp');
  let situation: AcpStart | null = requested === null ? null : STARTS.find(name => name === requested) ?? 'connected';
  let active = situation !== null;
  const installed = (): boolean => situation !== 'missing';
  const say = (ko: string, en: string): string => options.ko ? ko : en;

  let emitting = false;
  const emit: Post = (type, payload) => { emitting = true; try { post(type, payload); } finally { emitting = false; } };
  const later = (ms: number): Promise<void> => new Promise(resolve => window.setTimeout(resolve, ms));
  let counter = 0;
  const id = (prefix: string): string => `${prefix}-${++counter}`;

  // ---- The Antigravity conversation ----
  let generation = 0; // a restart or a new conversation leaves the previous connection's timers behind
  let session: string | undefined;
  let title = '';
  let connected = false, authRequired = false, signedIn = false;
  let error: string | undefined;
  const config = { model: 'gemini-3-pro', mode: 'agent', web: true };
  let permissions: AcpState['permissions'] = [];
  let answer: ((optionId: string | null) => void) | undefined;
  let plan: AgentPlanTask[] = [];
  let planRevision = 0;
  let queue: QueueState = { sessionId: null, items: [], paused: false };
  let turn: { promptId: string; cancelled: boolean; assistant?: string; tool?: { id: string; name: string } } | null = null;
  let prompts = 0;

  const backendState = (): BackendState => ({
    active: (active ? 'antigravity-acp' : 'omp') satisfies BackendId,
    antigravity: {
      installed: installed(), canStart: installed(), ompToolsAvailable: true, workspaceRoot: options.root,
      ...(installed() ? { executablePath: 'C:\\Users\\dev\\AppData\\Local\\Programs\\Antigravity\\bin\\agy.cmd' } : {}),
      message: installed() ? 'Uses official agy ACP and session-scoped OMP tools. The workspace is not a sandbox; Antigravity owns authentication and approvals.'
        : 'Install the official Antigravity CLI (agy) to use ACP.',
    },
  });
  const modelName = (): string => MODELS.flatMap(group => group.options).find(item => item.value === config.model)?.name ?? config.model;
  const open = (): number => plan.filter(task => task.status !== 'completed' && task.status !== 'abandoned').length;
  const acpState = (): AcpState => ({
    sessionId: session, connected, authRequired, error,
    authMethods: authRequired ? [
      { id: 'oauth-personal', name: say('Google 계정으로 로그인', 'Sign in with Google'), description: say('브라우저에서 로그인해요', 'Opens the browser') },
      { id: 'api-key', name: say('API 키 사용', 'Use an API key'), description: 'ANTIGRAVITY_API_KEY' },
    ] : [],
    configOptions: connected ? [
      { id: 'model', name: 'Model', category: 'model', type: 'select', currentValue: config.model, options: MODELS },
      { id: 'mode', name: 'Mode', category: 'mode', type: 'select', currentValue: config.mode, description: 'How far the agent may go on its own', options: MODES },
      { id: 'web', name: 'Web search', type: 'boolean', currentValue: config.web },
    ] : [],
    permissions,
    commands: connected ? [
      { name: 'plan', description: 'Plan the change before editing', input: { hint: 'what to plan' } },
      { name: 'review', description: 'Review the current diff' },
      { name: 'memory', description: 'Show what the agent remembers about this project' },
    ] : [],
    ompTools: { serverName: 'pomp', command: 'omp', args: ['mcp-server', '--cwd', options.root], state: 'configured' },
  });
  const publish = (): void => {
    emit('bridge/state', connected ? { state: 'running', message: 'Antigravity ACP' }
      : error ? { state: 'failed', message: error, reason: authRequired ? 'acpAuthenticationRequired' : 'acpError' } : { state: 'starting', message: 'Antigravity ACP' });
    emit('session/info', {
      backend: 'antigravity-acp', capabilities: CAPABILITIES, sessionId: session, sessionName: title, workingDirectory: options.root,
      isStreaming: turn !== null, hasPendingAsyncWork: false, recoveryStatus: connected ? 'ready' : error ? 'failed' : 'restoring', recoveryMessage: error,
      model: { provider: 'antigravity-acp', id: config.model, name: modelName() },
      todoPhases: plan.length ? [{ name: '', tasks: plan }] : [], planTaskCount: plan.length, planOpenCount: open(), planRevision: `acp-${planRevision}`, planTruncated: false,
    });
    emit('acp/state', acpState());
    emit('chat/queueState', structuredClone(queue));
  };
  const publishTabs = (): void => {
    const plans: ConversationPlan[] = session ? [{
      backend: 'antigravity-acp', editable: false, canContinue: false, canRun: false, canStop: turn !== null, sessionId: session, title: title || say('새 대화', 'New conversation'),
      partner: { kind: 'main' }, ready: connected, isStreaming: turn !== null, hasPendingAsyncWork: false, planTaskCount: plan.length, planOpenCount: open(), planRevision: `acp-${planRevision}`,
    }] : [];
    emit('conversation/tabsState', {
      root: options.root, backend: 'antigravity-acp', plans, sessionId: session ?? null, partner: { kind: 'main' }, title, main: options.nativeSession(),
      tabs: session ? [{ backend: 'antigravity-acp', sessionId: session, partner: { kind: 'main' }, title, isRunning: turn !== null }] : [],
    });
  };
  const setPlan = (statuses: string[]): void => {
    const tasks = [say('주문 합계 계산 읽기', 'Read the order total calculation'), say('수량과 회원 할인 반영', 'Apply quantity and the member discount'),
      say('단위 테스트 추가', 'Add unit tests'), say('변경 요약', 'Summarize the change')];
    plan = tasks.map((content, index) => ({ content, status: statuses[index] ?? 'pending' }));
    planRevision++; publish(); publishTabs();
  };

  /** Ends whatever the turn left open (its message, its tool, its question) and the turn itself. */
  const endTurn = (status: 'completed' | 'aborted' | 'error', failure?: string): void => {
    const ended = turn;
    if (!ended) return;
    turn = null;
    if (answer) { const settle = answer; answer = undefined; settle(null); }
    permissions = [];
    if (ended.assistant) emit('chat/assistantEnd', { messageId: ended.assistant, ...(status === 'completed' ? {} : { stopReason: status === 'aborted' ? 'aborted' : 'error' }) });
    if (ended.tool) emit('chat/toolEnd', { toolCallId: ended.tool.id, toolName: ended.tool.name, isError: true, resultText: failure ?? say('중지했어요.', 'Stopped.') });
    emit('chat/turnEnd', { promptId: ended.promptId, status, ...(failure ? { error: { code: 'acpDisconnected', message: failure, retryable: false } } : {}) });
  };
  const fail = (message: string): void => { connected = false; authRequired = false; error = message; publish(); publishTabs(); };
  const connect = async (): Promise<void> => {
    const run = ++generation;
    endTurn('aborted');
    connected = false; authRequired = false; error = undefined;
    publish(); publishTabs();
    await later(700);
    if (run !== generation || !active) return;
    if (situation === 'missing') { fail(say('공식 agy CLI를 설치하고 다시 연결해 주세요.', 'Install the official agy CLI and reconnect.')); return; }
    if (situation === 'error') { situation = 'connected'; fail('The Antigravity CLI closed the connection while starting (exit code 1).'); return; }
    if (situation === 'auth' && !signedIn) { authRequired = true; error = say('로그인이 필요해요.', 'Authentication required.'); publish(); publishTabs(); return; }
    session ??= `antigravity-acp:mock-${id('session')}`;
    connected = true;
    if (queue.paused && queue.items.length === 0) queue = { sessionId: null, items: [], paused: false };
    publish(); publishTabs();
    if (situation === 'busy') { situation = 'connected'; void runTurn({ clientMessageId: id('mock-busy'), text: say('주문 합계 계산을 고쳐 줘.', 'Fix the order total calculation.'), includeEditorContext: false }); }
  };

  const stream = async (alive: () => boolean, text: string): Promise<void> => {
    const current = turn;
    if (!current || !alive()) return;
    const messageId = id('acp-message');
    current.assistant = messageId;
    emit('chat/assistantStart', { messageId });
    for (const chunk of text.match(/.{1,14}/gsu) ?? []) {
      await later(45);
      if (!alive()) return;
      emit('chat/assistantDelta', { messageId, kind: 'text', delta: chunk });
    }
    current.assistant = undefined;
    emit('chat/assistantEnd', { messageId });
  };
  const tool = async (alive: () => boolean, name: string, args: Record<string, unknown>, result: string, isError = false): Promise<void> => {
    const current = turn;
    if (!current || !alive()) return;
    const toolCallId = id('acp-tool');
    current.tool = { id: toolCallId, name };
    emit('chat/toolStart', { toolCallId, toolName: name, args, isHostTool: false });
    await later(500);
    if (!alive()) return;
    current.tool = undefined;
    emit('chat/toolEnd', { toolCallId, toolName: name, isError, resultText: result, durationMs: 500 });
  };
  const runTurn = async (input: Send): Promise<void> => {
    const run = generation;
    const mine = { promptId: id('acp-prompt'), cancelled: false };
    turn = mine;
    const alive = (): boolean => turn === mine && !mine.cancelled && run === generation;
    if (!title) title = input.text.trim().split(/\r?\n/u)[0]!.slice(0, 40);
    emit('chat/turnStarted', { promptId: mine.promptId, clientMessageId: input.clientMessageId, text: input.text, ...(input.attachments?.length ? { attachments: input.attachments } : {}) });
    publish(); publishTabs();
    const first = ++prompts === 1;
    await stream(alive, say('주문 합계 계산을 살펴보고 계획을 세울게요. ', 'I will read the order total calculation and plan the change. '));
    if (!alive()) return;
    setPlan(['in_progress']);
    await tool(alive, `Read ${FILE}`, { path: FILE }, 'public decimal Total(Order order)\n{\n    return order.Lines.Sum(line => line.Price);\n}');
    if (!alive()) return;
    if (first && situation === 'exited') {
      // The CLI goes away in the middle of the turn, as when its process is killed.
      situation = 'connected'; generation++;
      const message = say('Antigravity CLI가 종료됐어요. 다시 연결해 주세요.', 'Antigravity CLI exited. Reconnect to continue.');
      endTurn('error', message);
      if (queue.items.length) queue = { ...queue, paused: true, reason: message };
      fail(message);
      return;
    }
    setPlan(['completed', 'in_progress']);
    let allowed = true;
    if (/permission|권한/iu.test(input.text)) {
      const requestId = id('acp-permission');
      permissions = [{
        requestId,
        toolCall: { title: `Edit ${FILE}`, kind: 'edit', rawInput: { path: `${options.root}\\${FILE.replaceAll('/', '\\')}` }, locations: [{ path: FILE, line: 43 }],
          content: [{ type: 'diff', path: FILE, oldText: 'return order.Lines.Sum(line => line.Price);', newText: 'var sum = order.Lines.Sum(line => line.Price * line.Quantity);\nif (order.Member) sum *= 0.95m;\nreturn sum;' }] },
        options: [{ optionId: 'allow-once', name: say('이번만 허용', 'Allow once'), kind: 'allow_once' }, { optionId: 'allow-always', name: say('항상 허용', 'Always allow'), kind: 'allow_always' },
          { optionId: 'reject-once', name: say('거부', 'Reject'), kind: 'reject_once' }],
      }];
      publish();
      const chosen = await new Promise<string | null>(resolve => { answer = resolve; });
      if (!alive()) return;
      allowed = chosen !== null && chosen.startsWith('allow');
    }
    await tool(alive, `Edit ${FILE}`, { path: FILE }, allowed ? DIFF : say('사용자가 이 도구 호출을 거부했어요.', 'The user rejected this tool call.'), !allowed);
    if (!alive()) return;
    setPlan(allowed ? ['completed', 'completed', 'in_progress'] : ['completed', 'abandoned', 'pending']);
    await stream(alive, allowed
      ? say('수량을 곱하고 회원 할인 5%를 적용하도록 `Total`을 고쳤어요. 다음으로 단위 테스트를 추가하면 돼요.', 'I changed `Total` to multiply by the quantity and apply the 5% member discount. Unit tests are the next step.')
      : say('편집을 건너뛰었어요. 계산은 그대로예요.', 'I skipped the edit. The calculation is unchanged.'));
    if (!alive()) return;
    endTurn('completed');
    publish(); publishTabs();
    drain();
  };
  const drain = (): void => {
    if (turn || queue.paused || !queue.items.length || !connected) return;
    const item = queue.items[0]!;
    queue = { ...queue, items: queue.items.slice(1), ...(queue.items.length === 1 ? { sessionId: null } : {}) };
    void runTurn({ clientMessageId: `queue-${item.id}`, text: item.text, includeEditorContext: item.includeEditorContext, attachments: item.attachments });
  };
  const reject = (input: Send, message: string): void => emit('error', { code: 'queue_rejected', message, retryable: false, clientMessageId: input.clientMessageId });
  const sendChat = (input: Send, steer: boolean): void => {
    if (!connected || !session) { reject(input, say('Antigravity에 연결되지 않았어요.', 'Antigravity is not connected.')); return; }
    if (steer && turn) {
      // A steer cannot reach a turn that is being stopped.
      if (turn.cancelled) { reject(input, say('중지하는 중이라 지금은 보낼 수 없어요.', 'The turn is stopping; the message cannot be sent now.')); return; }
      emit('chat/steered', { steerId: id('acp-steer'), clientMessageId: input.clientMessageId, text: input.text, ...(input.attachments?.length ? { attachments: input.attachments } : {}) });
      return;
    }
    if (turn || queue.items.length) {
      const itemId = id('acp-queue');
      queue = { ...queue, sessionId: session, items: [...queue.items, { id: itemId, text: input.text, includeEditorContext: input.includeEditorContext, attachments: input.attachments, createdAt: new Date().toISOString(), canSteer: true }] };
      emit('chat/queued', { clientMessageId: input.clientMessageId, itemId });
      emit('chat/queueState', structuredClone(queue));
      return;
    }
    void runTurn(input);
  };
  const stop = (): void => {
    const stopping = turn;
    if (!stopping || stopping.cancelled) return;
    stopping.cancelled = true;
    if (answer) { const settle = answer; answer = undefined; permissions = []; publish(); settle(null); }
    // Cancellation is not immediate: the agent acknowledges it a moment later.
    window.setTimeout(() => {
      if (turn !== stopping) return;
      endTurn('aborted');
      if (queue.items.length) queue = { ...queue, paused: true, reason: say('중지해서 대기열을 멈췄어요.', 'The queue is paused because you stopped the reply.') };
      publish(); publishTabs();
    }, 900);
  };
  const queueCommand = (command: UiToHost['chat/queue']): void => {
    const index = queue.items.findIndex(item => item.id === command.id);
    const item = queue.items[index];
    let failure: string | undefined;
    const items = [...queue.items];
    switch (command.action) {
      case 'pause': queue = { ...queue, paused: true, reason: undefined }; break;
      case 'resume': queue = { ...queue, paused: false, reason: undefined }; break;
      case 'clear': queue = { sessionId: null, items: [], paused: false }; break;
      case 'edit': case 'remove': case 'move': case 'steer':
        if (!item) { failure = say('대기열 항목을 찾을 수 없어요.', 'Queue item was not found.'); break; }
        if (command.action === 'edit') items[index] = { ...item, text: command.text ?? item.text };
        else if (command.action === 'steer' && turn?.cancelled) { failure = say('지금은 보낼 수 없어요.', 'The queued message cannot be steered now.'); break; }
        else {
          items.splice(index, 1);
          if (command.action === 'move') items.splice(Math.max(0, Math.min(items.length, command.index ?? index)), 0, item);
        }
        queue = { ...queue, items, ...(items.length ? {} : { sessionId: null, paused: false, reason: undefined }) };
        if (command.action === 'steer') {
          const send: Send = { clientMessageId: `queue-${item.id}`, text: item.text, includeEditorContext: item.includeEditorContext, attachments: item.attachments };
          if (turn) sendChat(send, true); else if (connected) void runTurn(send);
        }
        break;
    }
    emit('chat/queueReply', { requestId: command.requestId, ok: failure === undefined, ...(failure ? { error: failure } : {}) });
    emit('chat/queueState', structuredClone(queue));
    drain();
  };
  const acpCommand = (command: AcpCommand): void => {
    const reply = (failure?: string): void => emit('acp/reply', { requestId: command.requestId, ok: failure === undefined, ...(failure ? { error: failure } : {}) });
    if (command.action === 'authenticate') {
      // The sign-in happens in the browser: the answer comes when it is done.
      const run = generation;
      window.setTimeout(() => { if (run !== generation || !active) return; signedIn = true; reply(); void connect(); }, 1800);
      return;
    }
    if (!connected || command.sessionId !== session) { reply(say('이 대화는 연결되어 있지 않아요.', 'This conversation is not connected.')); return; }
    if (command.action === 'permission') {
      const pending = permissions.find(item => item.requestId === command.permissionRequestId);
      if (!pending || !answer) { reply(say('이미 처리된 요청이에요.', 'This request was already answered.')); return; }
      const settle = answer; answer = undefined; permissions = [];
      reply(); publish(); settle(command.optionId ?? null);
      return;
    }
    window.setTimeout(() => {
      if (command.configId === 'model' && typeof command.value === 'string') config.model = command.value;
      else if (command.configId === 'mode' && typeof command.value === 'string') config.mode = command.value;
      else if (command.configId === 'web' && typeof command.value === 'boolean') config.web = command.value;
      else { reply(say('바꿀 수 없는 설정이에요.', 'This option cannot be changed.')); return; }
      reply(); publish();
    }, 250);
  };

  // ---- The backend choice ----
  const select = (backend: BackendId, requestId: string | undefined): void => {
    const next = backend === 'antigravity-acp';
    const changed = next !== active;
    // The conversation that leaves the screen ends its turn first: its last frames belong to the transcript being replaced.
    if (changed && next) options.stopNative();
    if (changed && !next) { generation++; endTurn('aborted'); }
    active = next;
    emit('session/newResult', { requestId, status: 'completed' });
    emit('backend/state', backendState());
    if (!active) { if (changed) options.showNative(); return; }
    emit('commands/state', { commands: [] });
    if (connected) { publish(); publishTabs(); } else void connect();
  };
  const newConversation = (requestId: string): void => {
    generation++; endTurn('aborted');
    session = undefined; title = ''; plan = []; planRevision++; prompts = 0; queue = { sessionId: null, items: [], paused: false };
    emit('session/newResult', { requestId, status: 'completed' });
    void connect();
  };

  return {
    holds: type => active && !emitting && NATIVE_FRAME.test(type),
    snapshot() {
      emit('backend/state', backendState());
      if (!active) return;
      emit('commands/state', { commands: [] });
      void connect();
    },
    handle(type, payload) {
      if (type === 'conversation/backend') {
        const command = payload as UiToHost['conversation/backend'];
        select(command.backend === 'antigravity-acp' ? 'antigravity-acp' : 'omp', command.requestId);
        return true;
      }
      if (!active) return false;
      switch (type) {
        case 'chat/send': sendChat(payload as Send, false); return true;
        case 'chat/steer': sendChat(payload as Send, true); return true;
        case 'chat/stop': stop(); return true;
        case 'chat/queue': queueCommand(payload as UiToHost['chat/queue']); return true;
        case 'acp/command': acpCommand(payload as AcpCommand); return true;
        case 'bridge/restart': void connect(); return true;
        case 'session/new': newConversation((payload as UiToHost['session/new']).requestId); return true;
        // Any tab of the strip other than this conversation's is a native one; closing this tab ends the conversation.
        case 'conversation/tab': {
          const command = payload as UiToHost['conversation/tab'];
          if (command.action === 'close') { session = undefined; title = ''; plan = []; queue = { sessionId: null, items: [], paused: false }; connected = false; }
          select('omp', command.requestId);
          return true;
        }
        case 'workspace/agentPlanPage': {
          const command = payload as UiToHost['workspace/agentPlanPage'];
          if (command.sessionId !== session) return false;
          const query = (command.query ?? '').toLowerCase();
          const rows = plan.map((task, index) => ({ phase: 0, task: index, content: task.content, status: task.status })).filter(row => !query || row.content.toLowerCase().includes(query));
          emit('workspace/agentPlanPageResult', { requestId: command.requestId, ok: true, revision: `acp-${planRevision}`, editable: false, editableWhileWorking: false, page: 0, size: command.size, total: rows.length,
            completedCount: plan.filter(task => task.status === 'completed').length, phases: plan.length ? [{ index: 0, name: '', taskCount: plan.length }] : [], rows: rows.slice(0, command.size) });
          return true;
        }
        default: return false;
      }
    },
  };
}
