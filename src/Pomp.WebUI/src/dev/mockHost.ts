/**
 * Dev-only mock of the Visual Studio host, installed by main.tsx when the page runs in a plain browser
 * (no `window.chrome.webview`). It is imported dynamically behind `import.meta.env.DEV`, so it is not part of
 * production bundles.
 *
 * Query parameters (all optional):
 *   scenario = idle (default) | chat | permission | diff | queue | dialog | error | long | full | reconnect | failed | setup
 *              (setup: omp has no model provider yet, so the page shows the engine setup panel; every scenario answers setup/command)
 *   theme    = dark (default) | light
 *   locale   = ko (default) | en
 *   autosend = 0      do not type the scenario's first prompt automatically
 *   speed    = 1      time multiplier (2 = twice as fast)
 *   until    = <ms>   freeze the timeline at this point (deterministic screenshots)
 *   newsession = completed (default) | failed | cancelled   how the host answers "New session"
 *   replay = 1 | truncated   start with a replay fixture (or call window.pompMockHost.replay later)
 *   accounts  = comma separated provider ids that are signed in at the start (default openai-codex,anthropic; none in "setup")
 *   providers = ok (default) | error | slow   how the host answers listProviders (error: providersError, slow: 3 s)
 *   models    = ok (default) | error | slow   how the host answers listModels
 *   usage     = ok (default) | error | slow   how the host answers listUsage (slow: 6 s, error: usage.error)
 *   logout    = ok (default) | error | slow | env   how the host answers a sign-out (slow: 6 s, error: an error answer, env: the
 *               provider stays authenticated afterwards, as with an API key or an environment variable)
 *   permissionMode = ok (default) | error   how the host answers a permission-mode change (error: a failed correlated reply)
 *   commands  = ok (default) | empty   engine slash-command list (empty also while the bridge is stopped)
 *   sessions  = ok (default) | error | slow | empty   session list/operations (slow: 3 s, error: a correlated reply)
 *   preview   = ok (default) | error | slow   preview any listed entry without switching
 *   rename    = ok (default) | error | slow   rename the current session/header
 *   compact   = ok (default) | error | slow   compression (1.2 s normally; 3 s when slow)
 *   switch    = ok (default) | error | slow   switch session and replay a short transcript
 *   messages  = ok (default) | error | slow   40-message pages with nextCursor
 *   knowledgeDraft = 1   the editor's "Add to Knowledge > New" arrives at the start (knowledge/draft), as window.pompMockHost.knowledge.draft() sends it later
 *
 * Whatever the scenario, typing a message in the composer starts a turn of the scenario's kind.
 *
 * Sign-in (login command) follows the provider: openai-codex opens a browser link and finishes by itself, anthropic asks for an
 * authorization code (answerPrompt; "wrong-code" is refused once), github-copilot shows a device code, google-antigravity fails
 * because it needs a terminal (the page offers loginConsole), every other provider finishes like openai-codex. openai-codex-device and
 * zai-coding-plan are sign-in methods that store as openai-codex / zai: they sign that provider in and are never listed as authenticated.
 */
import { DEFAULT_SHORTCUTS } from '../protocol';
import type {
  AgentsCommand,
  ApprovalMode,
  AgentHookRule,
  BtwRecord,
  BtwStatus,
  CommandInfo,
  ContextBreakdown,
  ComputerSetCommand,
  ComputerStatePayload,
  HostMessage,
  HostMessageType,
  HostToUi,
  PermissionRequestPayload,
  Role,
  RoleAvatar,
  SessionEntry,
  SessionHistoryCommand,
  SessionHistoryMessage,
  SessionHistoryState,
  SessionInfoPayload,
  SetupCommand,
  SetupModel,
  SetupProvider,
  SetupSavedKey,
  SetupLocalServer,
  SetupStatePayload,
  SetupUsagePayload,
  Team,
  UiToHost,
  UsageReport,
  WorkspaceFile,
} from '../protocol';
import { createMockToolsHandler } from './mockTools';
import { createMockConversations } from './mockConversations';
import type { ConversationPartner } from '../conversationProtocol';
import { createMockKnowledge } from './mockKnowledge';
import { createMockAdvisors } from './mockAdvisors';
import { createMockWork } from './mockWork';

declare global {
  interface Window {
    /** Dev-only control surface; production excludes this entire module. */
    pompMockHost?: {
      emit: (message: HostMessage) => void;
      replay: (truncated?: boolean, messages?: HostMessage[]) => void;
      sent: unknown[];
      /** The in-memory knowledge store: `draft` sends the editor's knowledge/draft, `propose` an agent's proposal, the rest control optimize. */
      knowledge: Pick<ReturnType<typeof createMockKnowledge>, 'draft' | 'propose' | 'setOptimizeDelay' | 'failNextOptimize'>;
    };
  }
}

type Listener = (event: { data: unknown }) => void;
type Scenario = 'idle' | 'chat' | 'permission' | 'diff' | 'queue' | 'dialog' | 'error' | 'long' | 'full' | 'reconnect' | 'failed' | 'setup';

const SCENARIOS: readonly Scenario[] = ['idle', 'chat', 'permission', 'diff', 'queue', 'dialog', 'error', 'long', 'full', 'reconnect', 'failed', 'setup'];

// ---- Themes (VS 2022 look-alikes; every variable of docs/ui-protocol.md) -------------------------

const DARK: Record<string, string> = {
  '--pomp-bg': '#1f1f1f',
  '--pomp-fg': '#f1f1f1',
  '--pomp-muted-fg': '#a0a0a0',
  '--pomp-border': '#3f3f46',
  '--pomp-panel-bg': '#252526',
  '--pomp-input-bg': '#333337',
  '--pomp-input-fg': '#f1f1f1',
  '--pomp-input-border': '#434346',
  '--pomp-focus-border': '#007acc',
  '--pomp-button-bg': '#0e70c0',
  '--pomp-button-fg': '#ffffff',
  '--pomp-button-hover-bg': '#1c97ea',
  '--pomp-button-secondary-bg': '#3f3f46',
  '--pomp-button-secondary-fg': '#f1f1f1',
  '--pomp-link': '#3794ff',
  '--pomp-accent': '#4fa8e8',
  '--pomp-error': '#f48771',
  '--pomp-warning': '#e0b341',
  '--pomp-success': '#6ac48a',
  '--pomp-code-bg': '#181818',
  '--pomp-selection-bg': '#264f78',
  '--pomp-scrollbar': 'rgba(146, 146, 146, 0.4)',
  '--pomp-diff-add-bg': 'rgba(155, 185, 85, 0.2)',
  '--pomp-diff-del-bg': 'rgba(255, 0, 0, 0.2)',
};

const LIGHT: Record<string, string> = {
  '--pomp-bg': '#f5f5f5',
  '--pomp-fg': '#1e1e1e',
  '--pomp-muted-fg': '#717171',
  '--pomp-border': '#cccedb',
  '--pomp-panel-bg': '#eeeef2',
  '--pomp-input-bg': '#ffffff',
  '--pomp-input-fg': '#1e1e1e',
  '--pomp-input-border': '#cccedb',
  '--pomp-focus-border': '#007acc',
  '--pomp-button-bg': '#0e70c0',
  '--pomp-button-fg': '#ffffff',
  '--pomp-button-hover-bg': '#1c97ea',
  '--pomp-button-secondary-bg': '#dcdce4',
  '--pomp-button-secondary-fg': '#1e1e1e',
  '--pomp-link': '#0066b8',
  '--pomp-accent': '#0e70c0',
  '--pomp-error': '#c72e2e',
  '--pomp-warning': '#9a6700',
  '--pomp-success': '#1a7f37',
  '--pomp-code-bg': '#e8e9ef',
  '--pomp-selection-bg': '#add6ff',
  '--pomp-scrollbar': 'rgba(100, 100, 100, 0.4)',
  '--pomp-diff-add-bg': 'rgba(46, 160, 67, 0.22)',
  '--pomp-diff-del-bg': 'rgba(248, 81, 73, 0.22)',
};

// ---- Sample content ------------------------------------------------------------------------------

const PROMPT = '이 솔루션의 빌드 오류를 확인하고 고쳐줘';

const THINKING =
  '빌드 오류 목록부터 확인해야 한다. read_error_list VS 도구로 현재 오류를 가져오고, 문제가 되는 파일을 열어 ' +
  '원인을 파악하자. 저장하지 않은 버퍼가 있을 수 있으니 디스크 파일이 아니라 VS 버퍼를 기준으로 읽어야 한다. ' +
  '원인이 확실해지면 hashline 편집으로 최소한만 고치고, 마지막에 다시 빌드해서 확인한다.';

const ANSWER = `## 빌드 오류 원인

\`Program.cs\` 12번째 줄에서 **세미콜론이 누락**되어 있었습니다. 함께 보고된 오류는 다음과 같습니다.

1. \`CS1002\` — \`;\` 필요 (\`Program.cs:12\`)
2. \`CS0103\` — 현재 컨텍스트에 \`config\` 이름이 없습니다
3. \`CS8600\` — null 리터럴을 non-nullable 형식으로 변환 (*경고*)

수정한 코드는 다음과 같습니다.

\`\`\`csharp
internal static class Program
{
    private static int Main(string[] args)
    {
        var config = Load(args[0]);   // 세미콜론 추가
        var message = $"Building {config.Name} for configuration {config.Configuration} on platform {config.Platform} (warnings as errors: off)";
        Console.WriteLine(message);
        return 0;
    }
}
\`\`\`

| 항목 | 수정 전 | 수정 후 |
|---|---:|---:|
| 오류 | 3 | 0 |
| 경고 | 2 | 2 |

> 저장하지 않은 버퍼까지 분석했기 때문에 디스크의 파일과 줄 번호가 다를 수 있습니다.

자세한 내용은 [컴파일러 메시지 문서](https://learn.microsoft.com/dotnet/csharp/language-reference/compiler-messages/)를 참고하세요.
셸에서는 다음과 같이 다시 확인할 수 있습니다.

\`\`\`bash
dotnet build Pomp.sln -c Release
\`\`\`

- [x] 오류 목록 확인
- [x] \`Program.cs\` 수정
- [ ] 단위 테스트 실행
`;

const SHORT_ANSWER = '요청하신 작업을 마쳤습니다. 변경한 내용은 위의 도구 호출 기록에서 확인할 수 있습니다.';

const DIFF = `--- a/src/Pomp.Sample/Program.cs
+++ b/src/Pomp.Sample/Program.cs
@@ -9,8 +9,9 @@ namespace Pomp.Sample
     internal static class Program
     {
         private static int Main(string[] args)
         {
-            var config = Load(args[0])
+            var config = Load(args[0]);
+            ArgumentNullException.ThrowIfNull(config);
             Console.WriteLine(config.Name);
             return 0;
         }
@@ -24,5 +25,5 @@ namespace Pomp.Sample
         private static Config Load(string path)
         {
-            return JsonConvert.DeserializeObject<Config>(File.ReadAllText(path));
+            return JsonConvert.DeserializeObject<Config>(File.ReadAllText(path)) ?? new Config();
         }
     }
`;

const LONG_MARKDOWN = `### 요약

이 저장소는 **VS 확장(VSIX)** 과 **WebView2 채팅 UI**, 그리고 에이전트 프로세스 브리지로 구성됩니다.

- \`Pomp.Contracts\` — 프로토콜과 인터페이스
- \`Pomp.Core\` — VS 비의존 구현 (세션 컨트롤러, 툴 디스패치)
  - \`AgentProcessBridge\` — NDJSON 브리지
  - \`PermissionGate\` — 승인 게이트
- \`Pomp.WebUI\` — 이 채팅 화면

\`\`\`json
{ "type": "chat/send", "payload": { "clientMessageId": "c1", "text": "안녕", "includeEditorContext": true } }
\`\`\`

1. 먼저 \`ui/ready\` 를 보냅니다.
2. VS가 \`host/hello\` 로 응답합니다.
`;

const WORKSPACE_FILES: WorkspaceFile[] = [
  'src/Pomp.Sample/Program.cs', 'src/Pomp.Core/PompSession.cs', 'src/Pomp.Contracts/Ui/UiProtocol.cs',
  'src/Pomp.WebUI/src/App.tsx', 'src/Pomp.WebUI/src/components/Composer.tsx', 'docs/ui-protocol.md',
  'src/한글/설정.cs', 'docs/Design Notes.md',
  ...Array.from({ length: 40 }, (_, index) => `src/Pomp.Sample/Services/Service${index + 1}.cs`),
].map((path) => ({ path, fullPath: `C:\\repo\\Pomp\\${path.replaceAll('/', '\\')}` }));

const REPLAY_FIXTURE: HostMessage[] = [
  { type: 'chat/turnStarted', payload: { promptId: 'replay-prompt', clientMessageId: 'replay-user', text: 'Program.cs를 확인해 줘', attachments: [{ path: WORKSPACE_FILES[0]!.fullPath }] } },
  { type: 'chat/toolStart', payload: { toolCallId: 'replay-tool', toolName: 'read_buffer', isHostTool: true, args: { path: 'src/Pomp.Sample/Program.cs' } } },
  { type: 'chat/toolEnd', payload: { toolCallId: 'replay-tool', toolName: 'read_buffer', isError: false, resultText: '파일 내용을 확인했습니다.', durationMs: 18 } },
  { type: 'chat/assistantStart', payload: { messageId: 'replay-assistant' } },
  { type: 'chat/assistantDelta', payload: { messageId: 'replay-assistant', kind: 'text', delta: '이 메시지는 저장된 이전 대화를 재생한 것입니다.' } },
  { type: 'chat/assistantEnd', payload: { messageId: 'replay-assistant', stopReason: 'stop' } },
  { type: 'chat/turnEnd', payload: { promptId: 'replay-prompt', status: 'completed' } },
];

const SETUP_STDERR = [
  'omp: No models available.',
  'Run `omp login` to sign in with a subscription, or set an API key environment variable (for example OPENAI_API_KEY).',
  ...Array.from({ length: 18 }, (_, index) => `    at Object.<anonymous> (C:\\Users\\dev\\AppData\\Local\\omp\\cli\\main.js:${100 + index}:13)`),
].join('\n');

/** About eighty providers, like omp's list: the popular ones first by id, then a spread of others (names of different lengths). */
const PROVIDER_NAMES: [string, string][] = [
  ['openai-codex', 'ChatGPT Plus/Pro (Codex Subscription)'],
  ['openai-codex-device', 'ChatGPT Plus/Pro (Codex, headless/device)'],
  ['anthropic', 'Anthropic (Claude Pro/Max)'],
  ['muse-code', 'Muse Code (Subscription)'],
  ['github-copilot', 'GitHub Copilot'],
  ['google-gemini-cli', 'Google Cloud Code Assist (Gemini CLI)'],
  ['google-antigravity', 'Antigravity (Gemini 3, Claude, GPT-OSS)'],
  ['openai', 'OpenAI'],
  ['google', 'Google Gemini'],
  ['xai', 'xAI Grok'],
  ['groq', 'Groq'],
  ['cerebras', 'Cerebras'],
  ['openrouter', 'OpenRouter'],
  ['mistral', 'Mistral'],
  ['deepseek', 'DeepSeek'],
  ['zai', 'Z.AI'],
  ['zai-coding-plan', 'Z.AI (GLM Coding Plan · Sign in)'],
  ['kimi-code', 'Kimi Code'],
  ['minimax', 'MiniMax'],
  ['cursor', 'Cursor'],
  ['opencode', 'OpenCode Zen'],
  ['amazon-bedrock', 'Amazon Bedrock'],
  ['azure-openai', 'Azure OpenAI'],
  ['together', 'Together AI'],
  ['fireworks', 'Fireworks AI'],
  ['huggingface', 'Hugging Face'],
  ['nvidia', 'NVIDIA NIM'],
  ['ollama', 'Ollama (local)'],
  ['lm-studio', 'LM Studio (local)'],
  ['vllm', 'vLLM (local)'],
  ['perplexity', 'Perplexity'],
  ['cloudflare-ai-gateway', 'Cloudflare AI Gateway'],
  ['qwen-portal', 'Qwen Portal'],
];

const PROVIDERS: SetupProvider[] = [
  ...PROVIDER_NAMES.map(([id, name]) => ({ id, name, authenticated: false, available: true })),
  ...Array.from({ length: 50 }, (_, index) => ({
    id: `extra-provider-${String(index + 1).padStart(2, '0')}`,
    name: `Extra provider ${String(index + 1).padStart(2, '0')}${index % 7 === 0 ? ' with a rather long display name that has to wrap in a narrow pane' : ''}`,
    authenticated: false,
    available: index !== 3,
  })),
];

/** Sign-in methods that store their credentials under another provider (omp 18.8 storeCredentialsAs). The host lists them but never reports them as authenticated. */
const STORED_UNDER: Readonly<Record<string, string>> = { 'openai-codex-device': 'openai-codex', 'zai-coding-plan': 'zai' };

const KEY_PROVIDERS = [
  { id: 'anthropic', label: 'Anthropic', envVar: 'ANTHROPIC_API_KEY' },
  { id: 'openai', label: 'OpenAI', envVar: 'OPENAI_API_KEY' },
  { id: 'openrouter', label: 'OpenRouter', envVar: 'OPENROUTER_API_KEY' },
];

/** Every model the mock knows; the host lists only those of a provider that is signed in (or has a saved key). */
const MODEL_LIST: SetupModel[] = [
  { provider: 'openai-codex', id: 'gpt-6-sol', name: 'GPT-6.1 Sol' },
  { provider: 'openai-codex', id: 'gpt-6-luna', name: 'GPT-6 Luna' },
  { provider: 'openai-codex', id: 'gpt-6-astra', name: 'GPT-6 Astra' },
  { provider: 'anthropic', id: 'claude-opus-5-5', name: 'Claude Opus 5.5' },
  { provider: 'anthropic', id: 'claude-sonnet-5-5', name: 'Claude Sonnet 5.5' },
  { provider: 'anthropic', id: 'claude-haiku-4-5', name: 'Claude Haiku 4.5' },
  { provider: 'github-copilot', id: 'gpt-5', name: 'GPT-5' },
  { provider: 'github-copilot', id: 'claude-sonnet-4-5', name: 'Claude Sonnet 4.5' },
  { provider: 'github-copilot', id: 'gemini-2.5-pro', name: 'Gemini 2.5 Pro' },
  { provider: 'google-gemini-cli', id: 'gemini-3-pro', name: 'Gemini 3 Pro' },
  { provider: 'google-gemini-cli', id: 'gemini-3-flash', name: 'Gemini 3 Flash' },
  { provider: 'openai', id: 'gpt-6-luna' },
  { provider: 'openai', id: 'gpt-4.1-mini', name: 'GPT-4.1 mini' },
  { provider: 'openrouter', id: 'meta/llama-4-maverick', name: 'Llama 4 Maverick' },
  { provider: 'openrouter', id: 'qwen/qwen3-coder-480b-a35b-instruct-extremely-long-model-identifier', name: 'Qwen3 Coder 480B A35B Instruct (a very long display name that must be cut off with an ellipsis)' },
  ...Array.from({ length: 24 }, (_, index) => ({ provider: 'openrouter', id: `vendor/model-${index + 1}`, name: `Vendor model ${index + 1}` })),
];

/** Models that do not reason offer no thinking level but "off"; every other model offers these. */
const THINKING_LEVELS = ['off', 'minimal', 'low', 'medium', 'high', 'xhigh'];
const modelLevels = (model: { provider: string; id: string }): string[] =>
  model.id === 'gpt-4.1-mini' || model.id.startsWith('vendor/') ? ['off'] : model.id === 'claude-haiku-4-5' ? ['off', 'low', 'medium', 'high'] : THINKING_LEVELS;

const ENGINE_COMMANDS: CommandInfo[] = [
  { name: 'context', source: 'builtin', description: '컨텍스트 사용량 표시' },
  { name: 'effort', source: 'builtin', description: '추론 수준 설정', hint: '[level]' },
  { name: 'fast', source: 'builtin', description: '빠른 모드 전환', hint: '[on | off | status]', subcommands: [{ name: 'on' }, { name: 'off' }, { name: 'status' }] },
  { name: 'tools', source: 'builtin', description: '사용 가능한 도구 표시' },
  { name: 'mcp', source: 'builtin', description: 'MCP 서버 관리', hint: '<subcommand>', subcommands: [{ name: 'list' }, { name: 'add' }, { name: 'remove' }] },
  { name: 'export', source: 'builtin', description: '대화 내보내기' },
  { name: 'security', source: 'builtin', description: '프로젝트 보안 계획, 검사 및 상태', hint: 'plan | scan | status', subcommands: [
    { name: 'plan', description: '보안 검토 계획 만들기' },
    { name: 'scan', description: '현재 변경 내용 검사' },
    { name: 'status', description: '검사 결과와 진행 상태' },
  ] },
  { name: 'skill:review', source: 'skill', description: '코드 리뷰 스킬 실행', hint: '[검토할 파일 또는 변경 내용]' },
  { name: 'compact-notes', source: 'file', description: '최근 결정 사항을 작업 노트로 정리', hint: '[노트 제목]' },
  { name: 'git', source: 'extension', description: '저장소 읽기 전용 도우미', subcommands: [
    { name: 'status', description: '현재 변경 사항 요약' },
    { name: 'diff', description: '선택한 파일의 변경 비교' },
  ] },
  { name: 'explain', source: 'file', description: '선택한 코드 또는 파일 설명', hint: '[파일 경로]' },
  { name: 'tests', source: 'file', description: '테스트 계획과 결과 요약', hint: '[프로젝트]' },
  { name: 'prompt:release-notes', source: 'mcp_prompt', description: '릴리스 노트 프롬프트', hint: '[버전]' },
];

const MOCK_ROOT = 'C:\\repo\\Pomp';
const SESSION_TITLES = [
  'Pomp 개발 세션', '빌드 오류를 확인하고 최소 수정으로 고쳐 주세요', '채팅 탭 접근성 검토',
  '이 솔루션의 프로젝트 구조를 설명해 줘', '모델 선택 UI 개선', '실패한 테스트 원인 확인',
  '세션 목록 검색과 빠른 전환 설계', '문서의 프로토콜 예제를 검토해 줘', 'WebView2 재연결 문제 조사',
  '변경 내용에서 잠재적인 null 참조를 찾아 줘', '한국어 UI 문구 정리', '터미널 작업 결과를 요약해 줘',
];

/** Named and unnamed sessions, all with first-message titles, newest first. */
/** ?sessions=N lists N conversations (scale check of the paged list; there is no count limit). */
function buildSessions(): SessionEntry[] {
  const now = Date.now();
  const count = Math.max(1, Math.min(20000, Number(new URLSearchParams(window.location.search).get('sessions')) || 25));
  return Array.from({ length: count }, (_, index) => {
    const title = SESSION_TITLES[index % SESSION_TITLES.length]!;
    const named = index === 0 || index % 3 === 2;
    const modifiedUtc = new Date(now - index * index * 35 * 60_000).toISOString();
    return {
      id: `mock-entry-${index + 1}`,
      sessionId: index === 0 ? 'mock-session' : `mock-session-${index + 1}`,
      title: index >= SESSION_TITLES.length ? `${title} (${index + 1})` : title,
      ...(named ? { name: index >= SESSION_TITLES.length ? `${title} (${index + 1})` : title } : {}),
      modifiedUtc,
      visitedUtc: new Date(now - index * index * 35 * 60_000 - index * 5 * 60_000).toISOString(),
      messageCount: index === 0 ? 86 : 4 + ((index * 7) % 31) * 2,
      current: index === 0,
    };
  });
}

const CHUNK_MS = 24;

// ---- Timeline helpers ------------------------------------------------------------------------------

class Aborted extends Error {}

type Mode = 'ok' | 'error' | 'slow';

function pick(): {
  scenario: Scenario;
  speed: number;
  until: number;
  theme: 'dark' | 'light';
  locale: string;
  autosend: boolean;
  newSession: 'completed' | 'failed' | 'cancelled';
  accounts: string[] | undefined;
  providers: Mode;
  models: Mode;
  usage: Mode;
  logout: Mode | 'env';
  commands: 'ok' | 'empty';
  sessions: Mode | 'empty';
  preview: Mode;
  rename: Mode;
  compact: Mode;
  switchSession: Mode;
  messages: Mode;
  engine: 'bundled' | 'settings' | 'installed' | 'none';
} {
  const q = new URLSearchParams(window.location.search);
  const requested = q.get('scenario') as Scenario | null;
  const speed = Number(q.get('speed'));
  const until = q.get('until');
  const newSession = q.get('newsession');
  const accounts = q.get('accounts');
  const mode = (name: string): Mode => (q.get(name) === 'error' ? 'error' : q.get(name) === 'slow' ? 'slow' : 'ok');
  return {
    scenario: requested && SCENARIOS.includes(requested) ? requested : 'idle',
    speed: speed > 0 ? speed : 1,
    until: until !== null && Number.isFinite(Number(until)) ? Number(until) : Infinity,
    theme: q.get('theme') === 'light' ? 'light' : 'dark',
    locale: q.get('locale') === 'en' ? 'en-US' : 'ko-KR',
    autosend: q.get('autosend') !== '0',
    newSession: newSession === 'failed' || newSession === 'cancelled' ? newSession : 'completed',
    accounts: accounts === null ? undefined : accounts.split(',').map((id) => id.trim()).filter((id) => id && id !== 'none'),
    providers: mode('providers'),
    models: mode('models'),
    usage: mode('usage'),
    logout: q.get('logout') === 'env' ? 'env' : mode('logout'),
    commands: q.get('commands') === 'empty' ? 'empty' : 'ok',
    sessions: q.get('sessions') === 'empty' ? 'empty' : mode('sessions'),
    preview: mode('preview'),
    rename: mode('rename'),
    compact: mode('compact'),
    switchSession: mode('switch'),
    messages: mode('messages'),
    engine: (['settings', 'installed', 'none'] as const).find((value) => value === q.get('engine')) ?? 'bundled',
  };
}

/** Deterministic pseudo-random chunking so streamed text looks like tokens and screenshots are reproducible. */
function chunks(text: string, seed = 11): string[] {
  const out: string[] = [];
  let state = seed;
  for (let i = 0; i < text.length; ) {
    state = (state * 1103515245 + 12345) & 0x7fffffff;
    const size = 3 + (state % 7);
    out.push(text.slice(i, i + size));
    i += size;
  }
  return out;
}

export function installMockHost(): void {
  const opts = pick();
  const listeners = new Set<Listener>();
  const backlog: unknown[] = [];
  let ready = false;

  // ---- Virtual clock: `until` freezes the timeline by never resolving sleeps past that point ----
  let virtualNow = 0;
  const wait = (ms: number): Promise<void> => {
    virtualNow += ms;
    if (virtualNow > opts.until) return new Promise<void>(() => {});
    return new Promise((resolve) => window.setTimeout(resolve, ms / opts.speed));
  };

  // ---- Host -> UI ----
  const deliver = (data: unknown): void => {
    window.setTimeout(() => listeners.forEach((listener) => listener({ data })), 0);
  };
  const post = <K extends HostMessageType>(type: K, payload: HostToUi[K]): void => {
    const message = { type, payload };
    if (ready) deliver(message);
    else backlog.push(message);
  };
  // Work tab data (?work=1 | active); session/info is reposted when the plan changes.
  const work = createMockWork(post, MOCK_ROOT, () => sessionId, () => postSession(streaming));

  // ---- Accounts that are signed in and the models they offer (shared by the session and the setup scripts below) ----
  const authenticated = new Set<string>(opts.accounts ?? (opts.scenario === 'setup' ? [] : ['openai-codex', 'anthropic']));
  let keys: SetupSavedKey[] = [];
  // ?localServers=N seeds N saved local model servers.
  let localServers: SetupLocalServer[] = Array.from({ length: Math.max(0, Math.min(1000, Number(new URLSearchParams(window.location.search).get('localServers')) || 0)) },
    (_, index) => ({ id: `local-box-${index + 1}`, name: `Box ${index + 1}`, address: `http://10.0.0.${(index % 250) + 1}:8000/v1`, hasKey: index % 2 === 0, savedUtc: new Date(Date.UTC(2026, 9, 1)).toISOString() }));
  const availableModels = (): SetupModel[] => MODEL_LIST.filter((item) => authenticated.has(item.provider) || keys.some((key) => key.provider === item.provider));

  // ---- Session header, history and context usage ----
  let sessions = buildSessions();
  let currentEntry = sessions[0]!;
  let sessionId = currentEntry.sessionId!;
  let sessionName = currentEntry.name;
  let tokens = 12_840;
  let streaming = false;
  const mockTools = createMockToolsHandler(message => post(message.type, message.payload as never), { sessionId, busy: () => streaming });
  const conversations = createMockConversations(post, { sessionId: () => sessionId, busy: () => streaming, start: input => startTurn(input, opts.scenario, true), steer: input => steerTurn(input) });
  for (const entry of sessions) {
    entry.partner = entry.id === currentEntry.id ? { kind: 'main' } : entry.messageCount! % 3 === 0 ? { kind: 'team', id: 'feature-dev' } : { kind: 'agent', id: 'architect' };
    if (entry.sessionId) conversations.tabs.seed(entry.sessionId, entry.partner);
  }
  let sessionChanging = false;
  const transcripts = new Map<string, SessionHistoryMessage[]>();
  const contextTokens = new Map<string, number>([[sessionId, tokens]]);
  for (const entry of sessions) {
    const count = entry.messageCount ?? 0;
    transcripts.set(entry.id, Array.from({ length: count }, (_, index): SessionHistoryMessage => ({
      role: index % 2 === 0 ? 'user' : 'assistant',
      text: index === 0 ? entry.title : index % 2 === 0
        ? `${entry.title}\n후속 질문 ${Math.floor(index / 2) + 1}: 관련 파일과 다음 확인 항목을 알려 주세요.`
        : `확인한 내용을 정리했습니다 (${Math.floor(index / 2) + 1}). 최소 변경으로 문제를 해결하고 빌드 결과를 검토했습니다.`,
      ...(index === 39 ? { truncated: true } : {}),
    })));
  }
  const initialModel = availableModels().find((item) => item.id === 'claude-sonnet-5-5') ?? availableModels()[0] ?? MODEL_LIST[4]!;
  let model: NonNullable<SessionInfoPayload['model']> = { provider: initialModel.provider, id: initialModel.id, name: initialModel.name };
  let thinkingLevel = 'high';
  const sessionInfo = (isStreaming: boolean): SessionInfoPayload => ({
    model,
    thinkingLevel,
    sessionId,
    ...(sessionName ? { sessionName } : {}),
    contextUsage: { tokens, contextWindow: 200_000, percent: (tokens / 200_000) * 100, breakdown: mockBreakdown(tokens) },
    isStreaming,
    workingDirectory: MOCK_ROOT,
    ...work.sessionInfo(),
  });
  /** The engine's /context split of the mock's token count: fixed parts first, the conversation takes the rest. */
  const mockBreakdown = (used: number): ContextBreakdown => {
    const fixed = 2_471 + 3_410 + 115 + 72;
    const messages = Math.max(0, used - fixed);
    return { systemPrompt: 2_471, systemTools: 3_410, hostTools: 1_600, mcpTools: 420, systemContext: 115, skills: 72, messages, autoCompactBuffer: 40_800, free: Math.max(0, 200_000 - used - 40_800), contextWindow: 200_000, measuredAt: new Date().toISOString() };
  };
  const postSession = (isStreaming: boolean): void => {
    streaming = isStreaming;
    contextTokens.set(sessionId, tokens);
    post('session/info', sessionInfo(isStreaming));
    advisors.snapshot();
  };
  const historyState = (requestId?: string, error?: string): SessionHistoryState => ({
    ...(requestId ? { requestId } : {}),
    ...(error ? { error } : {}),
    root: MOCK_ROOT,
    current: { sessionId, ...(sessionName ? { name: sessionName } : {}) },
    entries: opts.sessions === 'empty' ? [] : sessions.map((entry) => ({ ...entry, current: entry.id === currentEntry.id })),
  });
  const postHistory = (requestId?: string, error?: string): void => post('session/historyState', historyState(requestId, error));
  const touchSession = (): void => {
    currentEntry.modifiedUtc = new Date().toISOString();
    sessions = [currentEntry, ...sessions.filter((entry) => entry.id !== currentEntry.id)];
  };

  // ---- Pending questions (answered by the UI) ----
  const permissionWaiters = new Map<string, (answer: { decision: string; note?: string }) => void>();
  const dialogWaiters = new Map<string, (answer: UiToHost['dialog/respond']) => void>();
  let counter = 0;
  const nextId = (prefix: string): string => `${prefix}-${++counter}`;

  interface Run {
    aborted: boolean;
    promptId: string;
    cancels: Set<() => void>;
  }
  let current: Run | null = null;

  /** Stops a run: pending sleeps and questions reject, which unwinds the turn script. */
  const abortRun = (run: Run | null): void => {
    if (!run || run.aborted) return;
    run.aborted = true;
    const cancels = [...run.cancels];
    run.cancels.clear();
    cancels.forEach((cancel) => cancel());
  };

  // A sleep that rejects when the run is aborted (Stop / bridge crash).
  const runWait = (run: Run, ms: number): Promise<void> => {
    if (run.aborted) return Promise.reject(new Aborted());
    return new Promise<void>((resolve, reject) => {
      const cancel = (): void => reject(new Aborted());
      run.cancels.add(cancel);
      void wait(ms).then(() => {
        run.cancels.delete(cancel);
        if (run.aborted) reject(new Aborted());
        else resolve();
      });
    });
  };

  const askPermission = (run: Run, request: PermissionRequestPayload): Promise<{ decision: string; note?: string }> =>
    new Promise((resolve, reject) => {
      post('permission/request', request);
      const finish = (answer: { decision: string; note?: string }): void => {
        permissionWaiters.delete(request.requestId);
        run.cancels.delete(cancel);
        resolve(answer);
      };
      const cancel = (): void => {
        permissionWaiters.delete(request.requestId);
        post('permission/dismiss', { requestId: request.requestId, reason: 'cancelled' });
        reject(new Aborted());
      };
      permissionWaiters.set(request.requestId, finish);
      run.cancels.add(cancel);
      // Host-side timeout: resolve as a denial and tell the UI to close the dialog.
      if (request.timeoutMs && virtualNow + request.timeoutMs <= opts.until) {
        window.setTimeout(() => {
          if (!permissionWaiters.has(request.requestId)) return;
          post('permission/dismiss', { requestId: request.requestId, reason: 'timeout' });
          finish({ decision: 'deny', note: 'timeout' });
        }, request.timeoutMs / opts.speed);
      }
    });

  const askDialog = (run: Run, request: HostToUi['dialog/request']): Promise<UiToHost['dialog/respond']> =>
    new Promise((resolve, reject) => {
      post('dialog/request', request);
      const finish = (answer: UiToHost['dialog/respond']): void => {
        dialogWaiters.delete(request.requestId);
        run.cancels.delete(cancel);
        resolve(answer);
      };
      const cancel = (): void => {
        dialogWaiters.delete(request.requestId);
        post('dialog/dismiss', { requestId: request.requestId, reason: 'cancelled' });
        reject(new Aborted());
      };
      dialogWaiters.set(request.requestId, finish);
      run.cancels.add(cancel);
    });

  // ---- Turn building blocks ----
  const streamText = async (run: Run, messageId: string, kind: 'text' | 'thinking', text: string): Promise<void> => {
    for (const delta of chunks(text, kind === 'text' ? 11 : 5)) {
      if (run.aborted) throw new Aborted();
      post('chat/assistantDelta', { messageId, kind, delta });
      await runWait(run, CHUNK_MS);
    }
  };

  /** A tool call with live progress, ending after `steps` progress updates. */
  const runTool = async (
    run: Run,
    tool: { id: string; name: string; args?: Record<string, unknown>; host: boolean },
    progress: string[],
    result: { text: string; isError?: boolean; durationMs: number },
  ): Promise<void> => {
    post('chat/toolStart', { toolCallId: tool.id, toolName: tool.name, args: tool.args, isHostTool: tool.host });
    for (const text of progress) {
      await runWait(run, 450);
      post('chat/toolUpdate', { toolCallId: tool.id, text });
    }
    await runWait(run, 450);
    post('chat/toolEnd', {
      toolCallId: tool.id,
      toolName: tool.name,
      isError: result.isError === true,
      resultText: result.text,
      durationMs: result.durationMs,
    });
  };

  const bashApproval = (): PermissionRequestPayload => ({
    requestId: nextId('perm'),
    source: 'ompApproval',
    kind: 'processExec',
    toolName: 'bash',
    title: '셸 명령 실행',
    message: 'Allow tool: bash\ndotnet build src/Pomp.Sample -c Release --no-restore',
    target: 'C:\\repo\\Pomp\\src\\Pomp.Sample',
    timeoutMs: 60_000,
  });

  const editApproval = (): PermissionRequestPayload => ({
    requestId: nextId('perm'),
    source: 'hostTool',
    kind: 'fileWrite',
    toolName: 'edit_buffer_hashline',
    title: '파일 수정',
    message: 'Program.cs 의 저장되지 않은 버퍼 1곳을 수정합니다.',
    target: 'src/Pomp.Sample/Program.cs',
    diff: DIFF,
    timeoutMs: 120_000,
  });

  const approved = (answer: { decision: string }): boolean => answer.decision !== 'deny';

  // ---- Turn scripts ----
  const runTurn = async (run: Run, kind: Scenario, input: UiToHost['chat/send']): Promise<void> => {
    const promptId = run.promptId;
    if (input.text.startsWith('/')) {
      // omp command_output is bridged as a notice; no fake model inference or tools are needed.
      post('chat/turnStarted', { promptId, clientMessageId: input.clientMessageId, text: input.text, team: input.team, agent: input.agent, agentInstance: input.agentInstance, main: !input.team && !input.agent && !input.agentInstance ? true : undefined });
      postSession(true);
      await runWait(run, 180);
      const command = input.text.slice(1).split(/\s+/, 1)[0] ?? '';
      const known = ENGINE_COMMANDS.some((item) => item.name === command);
      post('chat/notice', {
        level: known ? 'info' : 'warning',
        message: known
          ? `command_output (mock): ${input.text.slice(0, 2000)}\n명령을 실행했습니다. 실제 확장·스킬·프롬프트 실행은 Visual Studio에서 처리합니다.`
          : `command_output (mock): 알 수 없는 명령 /${command.slice(0, 128)}. /help에서 사용 가능한 명령을 확인하세요.`,
      });
      post('chat/turnEnd', { promptId, status: 'completed' });
      postSession(false);
      conversations.finish(promptId, 'completed');
      return;
    }
    tokens += 1_900;
    post('chat/turnStarted', { promptId, clientMessageId: input.clientMessageId, text: input.text, attachments: input.attachments, editorContext: input.includeEditorContext ? { path: 'src/Pomp.Sample/Program.cs', startLine: 20, endLine: 45, caretLine: 22 } : undefined, team: input.team, agent: input.agent, agentInstance: input.agentInstance, main: !input.team && !input.agent && !input.agentInstance ? true : undefined });
    postSession(true);
    postBackground();
    await runWait(run, 300);

    // 1) thinking + a short lead-in
    const lead = nextId('msg');
    post('chat/assistantStart', { messageId: lead });
    await streamText(run, lead, 'thinking', THINKING);
    await streamText(run, lead, 'text', '먼저 현재 빌드 오류 목록을 확인해 보겠습니다.');
    post('chat/assistantEnd', { messageId: lead, stopReason: 'toolUse' });

    if (kind === 'error') {
      await runWait(run, 400);
      const partial = nextId('msg');
      post('chat/assistantStart', { messageId: partial });
      await streamText(run, partial, 'text', '오류 목록을 분석하는 중에');
      post('chat/assistantEnd', { messageId: partial, stopReason: 'error', errorMessage: 'HTTP 529: overloaded_error' });
      post('chat/turnEnd', {
        promptId,
        status: 'error',
        error: {
          code: 'provider_overloaded',
          message: '모델 제공자가 일시적으로 과부하 상태입니다 (HTTP 529). 잠시 후 다시 시도해 주세요.',
          retryable: true,
        },
      });
      postSession(false);
      conversations.finish(promptId, 'error');
      return;
    }

    if (kind === 'queue') {
      // Three approvals arrive at once: the UI queues them and shows a counter.
      const requests: PermissionRequestPayload[] = [
        bashApproval(),
        {
          requestId: nextId('perm'),
          source: 'hostTool',
          kind: 'projectChange',
          toolName: 'add_project_file',
          title: '프로젝트에 파일 추가',
          message: 'Pomp.Core.csproj 에 Services\\SessionController.cs 를 추가합니다.',
          target: 'src/Pomp.Core/Pomp.Core.csproj',
        },
        {
          requestId: nextId('perm'),
          source: 'hostTool',
          kind: 'packageChange',
          toolName: 'manage_nuget',
          title: 'NuGet 패키지 설치',
          message: 'Newtonsoft.Json 13.0.3 을 Pomp.Core 에 설치합니다.',
          target: 'src/Pomp.Core/Pomp.Core.csproj',
          timeoutMs: 90_000,
        },
      ];
      const answers = await Promise.all(requests.map((request) => askPermission(run, request)));
      const denied = answers.filter((a) => !approved(a)).length;
      const closing = nextId('msg');
      post('chat/assistantStart', { messageId: closing });
      await streamText(run, closing, 'text', `승인 결과를 반영했습니다. 거부된 요청: ${denied}건.`);
      post('chat/assistantEnd', { messageId: closing, stopReason: 'stop' });
      post('chat/turnEnd', { promptId, status: 'completed' });
      postSession(false);
      conversations.finish(promptId, 'completed');
      return;
    }

    // 2) host tool with live progress
    await runWait(run, 250);
    await runTool(
      run,
      { id: nextId('tool'), name: 'read_error_list', args: { severity: 'error', maxItems: 50 }, host: true },
      ['오류 목록을 읽는 중', '3개 항목을 찾았습니다 (오류 2, 경고 1)'],
      {
        text: 'CS1002  ; 필요  Program.cs(12,34)\nCS0103  현재 컨텍스트에 이름이 없습니다  Program.cs(13,25)\nCS8600  null 리터럴 변환 (경고)  Program.cs(18,22)',
        durationMs: 214,
      },
    );

    // 3) omp built-in tool
    await runTool(
      run,
      { id: nextId('tool'), name: 'read', args: { path: 'src/Pomp.Sample/Program.cs', startLine: 1, endLine: 40 }, host: false },
      [],
      { text: '1:using System;\n2:\n3:namespace Pomp.Sample\n4:{\n5:    internal static class Program\n6:    {\n7:        private static int Main(string[] args)\n8:        {\n9:            var config = Load(args[0])\n10:            Console.WriteLine(config.Name);\n11:            return 0;\n12:        }', durationMs: 38 },
    );

    if (kind === 'permission' || kind === 'full') {
      const id = nextId('tool');
      post('chat/toolStart', { toolCallId: id, toolName: 'bash', args: { command: 'dotnet build src/Pomp.Sample -c Release --no-restore' }, isHostTool: false });
      const answer = await askPermission(run, bashApproval());
      if (approved(answer)) {
        post('chat/toolUpdate', { toolCallId: id, text: 'Restore complete (0.4s)\nBuilding Pomp.Sample' });
        await runWait(run, 900);
        post('chat/toolEnd', {
          toolCallId: id,
          toolName: 'bash',
          isError: false,
          resultText: 'Build succeeded.\n    0 Warning(s)\n    0 Error(s)\nTime Elapsed 00:00:03.42',
          durationMs: 3_420,
        });
      } else {
        post('chat/toolEnd', {
          toolCallId: id,
          toolName: 'bash',
          isError: true,
          resultText: `사용자가 실행을 거부했습니다.${answer.note ? ` 사유: ${answer.note}` : ''}`,
          durationMs: 12,
        });
      }
    }

    if (kind === 'diff' || kind === 'full') {
      const id = nextId('tool');
      post('chat/toolStart', {
        toolCallId: id,
        toolName: 'edit_buffer_hashline',
        args: { path: 'src/Pomp.Sample/Program.cs', edits: [{ line: 9, tag: 'A1F3' }] },
        isHostTool: true,
      });
      const answer = await askPermission(run, editApproval());
      post('chat/toolEnd', {
        toolCallId: id,
        toolName: 'edit_buffer_hashline',
        isError: !approved(answer),
        resultText: approved(answer) ? '1곳을 수정했습니다 (Undo 가능).' : `사용자가 수정을 거부했습니다.${answer.note ? ` 사유: ${answer.note}` : ''}`,
        durationMs: 96,
      });
    }

    if (kind === 'dialog' || kind === 'full') {
      const answer = await askDialog(run, {
        requestId: nextId('dlg'),
        method: 'select',
        title: '빌드 구성을 선택하세요',
        message: '확장이 어떤 구성으로 다시 빌드할지 묻고 있습니다.',
        options: ['Debug', 'Release', 'Release (x64)'],
        defaultValue: 'Debug',
        timeoutMs: 45_000,
      });
      post('chat/notice', {
        level: 'info',
        message: answer.cancelled ? '빌드 구성 선택이 취소되었습니다.' : `빌드 구성: ${answer.value ?? ''}`,
      });
    }

    if (kind === 'full') {
      post('chat/notice', { level: 'warning', message: '컨텍스트 사용량이 높아 자동 압축이 예약되었습니다 (128K → 42K 토큰).' });
    }

    // 4) final answer
    await runWait(run, 350);
    const finalId = nextId('msg');
    post('chat/assistantStart', { messageId: finalId });
    if (kind === 'long') {
      for (let i = 0; i < 6; i++) await streamText(run, finalId, 'text', `${LONG_MARKDOWN}\n`);
    } else {
      await streamText(run, finalId, 'text', kind === 'permission' || kind === 'diff' || kind === 'dialog' ? SHORT_ANSWER : ANSWER);
    }
    post('chat/assistantEnd', { messageId: finalId, stopReason: 'stop' });
    tokens += 3_200;
    post('chat/turnEnd', { promptId, status: 'completed' });
    postSession(false);
    conversations.finish(promptId, 'completed');
  };

  const startTurn = (payload: UiToHost['chat/send'], kind: Scenario, draining = false): void => {
    if (sessionChanging) {
      const promptId = nextId('prompt');
      post('chat/turnStarted', { promptId, clientMessageId: payload.clientMessageId, text: payload.text, team: payload.team, agent: payload.agent, agentInstance: payload.agentInstance });
      post('chat/turnEnd', { promptId, status: 'error', error: { code: 'busy', message: 'A session operation is still running.', retryable: true } });
      return;
    }
    if (!draining && conversations.tryQueue(payload)) return;
    payload = conversations.route(payload);
    abortRun(current);
    const run: Run = { aborted: false, promptId: nextId('prompt'), cancels: new Set() };
    current = run;
    conversations.start(payload, run.promptId);
    const entry = currentEntry;
    const turnKind: Scenario = kind === 'idle' || kind === 'reconnect' || kind === 'failed' || kind === 'setup' ? 'full' : kind;
    void runTurn(run, turnKind, payload).then(() => {
      if (run.aborted || entry !== currentEntry || payload.text.startsWith('/')) return;
      const messages = transcripts.get(entry.id) ?? [];
      if (messages.length === 0 && !entry.name) entry.title = payload.text.slice(0, 120);
      const target = payload.team ? { kind: 'team', id: payload.team } : payload.agent ? { kind: 'agent', id: payload.agent } : payload.agentInstance ? { kind: 'dm', id: payload.agentInstance } : undefined;
      // Like the host: the routing brief, then the user's text.
      const transcriptText = target ? `<!-- pomp-${target.kind}:${target.id} -->\nMock target: ${target.id}\n<!-- /pomp-${target.kind} -->\n\n${payload.text}` : payload.text;
      messages.push({ role: 'user', text: transcriptText }, { role: 'assistant', text: turnKind === 'error' ? '모델 제공자가 일시적으로 과부하 상태입니다.' : SHORT_ANSWER });
      transcripts.set(entry.id, messages);
      entry.messageCount = messages.length;
      touchSession();
      postHistory();
    }).catch((error: unknown) => {
      if (!(error instanceof Aborted)) console.error('[mock host]', error);
    }).finally(() => {
      if (current === run) current = null;
    });
  };

  const stopTurn = (): void => {
    const run = current;
    if (!run || run.aborted) return;
    abortRun(run); // unwinds the script (and dismisses open questions), then close the turn like the real host
    post('chat/turnEnd', { promptId: run.promptId, status: 'aborted' });
    postSession(false);
    conversations.finish(run.promptId, 'aborted');
  };

  /** chat/steer and the queue's "send now": a steered bubble in the running turn, or a turn of its own when nothing runs (like the host). */
  const steerTurn = (payload: UiToHost['chat/send']): void => {
    if (!current || current.aborted) { startTurn(payload, opts.scenario, true); return; }
    post('chat/steered', { steerId: nextId('steer'), clientMessageId: payload.clientMessageId, text: payload.text, attachments: payload.attachments });
  };

  // ---- Engine setup (accounts / login / API keys / model / usage); nobody is signed in at the start of the "setup" scenario ----
  const hasProvider = (): boolean => authenticated.size > 0 || keys.length > 0;
  let bridgeRunning = false;
  const postCommands = (): void => post('commands/state', {
    commands: bridgeRunning && opts.commands !== 'empty' ? ENGINE_COMMANDS : [],
  });
  let login: SetupStatePayload['login'] = { state: 'idle' };
  let providersLoaded = false;
  let models: SetupModel[] | undefined;
  let modelsError: string | undefined;
  let editorModel: NonNullable<SetupStatePayload['editorModel']> = { choice: 'auto' };
  let editorModelRevision = 0;
  let thinkingLevels: string[] | undefined;
  let usage: SetupUsagePayload | undefined;
  let usageLoading = false;
  let restartRecommended = false;
  let loginTimers: number[] = [];
  let promptCounter = 0;

  const later =(ms: number, action: () => void): number => window.setTimeout(action, ms / opts.speed);
  const clearLoginTimers = (): void => {
    loginTimers.forEach((timer) => window.clearTimeout(timer));
    loginTimers = [];
  };

  // ---- Side questions (/btw): one at a time per session, answered beside the running turn, never in the transcript ----
  const btwRecords = new Map<string, BtwRecord>();
  let btwActive: string | undefined;
  let btwTimer: number | undefined;
  const settleBtw = (status: BtwStatus): void => {
    const record = btwActive ? btwRecords.get(btwActive) : undefined;
    btwActive = undefined; window.clearTimeout(btwTimer);
    if (!record) return;
    const latest = record.followUps?.at(-1) ?? record;
    latest.status = status; latest.updatedAt = Date.now();
    post('btw/record', { record: structuredClone(record) });
  };
  const askBtw = ({ requestId, question, recordId }: UiToHost['btw/ask']): void => {
    const ko = opts.locale.startsWith('ko');
    if (btwActive) { post('btw/reply', { requestId, ok: false, error: ko ? '다른 질문에 아직 답하는 중이에요.' : 'Another side question is still running.' }); return; }
    const topic = recordId ? btwRecords.get(recordId) : undefined;
    if (recordId && !topic) { post('btw/reply', { requestId, ok: false, error: ko ? '알 수 없는 질문 주제예요.' : 'Unknown side question topic.' }); return; }
    const now = Date.now();
    const turn = { question, answer: '', status: 'running' as const, createdAt: now, updatedAt: now };
    const record: BtwRecord = topic ? { ...topic, followUps: [...(topic.followUps ?? []), turn] } : { id: nextId('btw'), leafId: null, ...turn };
    btwRecords.set(record.id, record); btwActive = record.id;
    post('btw/record', { record: structuredClone(record) });
    post('btw/reply', { requestId, ok: true, record: structuredClone(record) });
    const words = (ko ? '지금은 계획의 두 번째 단계를 진행 중이에요. 테스트를 고친 뒤 빌드를 다시 확인할 예정이에요.' : 'The agent is on the second step of the plan: it fixes the tests, then checks the build again.').split(' ');
    let index = 0;
    const step = (): void => {
      if (btwActive !== record.id) return;
      if (index >= words.length) { settleBtw('complete'); return; }
      const delta = (index === 0 ? '' : ' ') + words[index++];
      const latest = record.followUps?.at(-1) ?? record;
      latest.answer += delta;
      post('btw/delta', { recordId: record.id, delta });
      btwTimer = later(70, step);
    };
    btwTimer = later(250, step);
  };

  const postSetup = (requestId?: string, error?: string): void =>
    post('setup/state', {
      ...(requestId ? { requestId } : {}),
      ...(error ? { error } : {}),
      ...(providersLoaded
        ? opts.providers === 'error'
          ? { providersError: 'The sign-in helper could not be started (mock failure).' }
          : { providers: PROVIDERS.map((item) => ({ ...item, authenticated: !(item.id in STORED_UNDER) && authenticated.has(item.id) })) }
        : {}),
      login,
      apiKeyProviders: KEY_PROVIDERS,
      keys,
      localServers,
      ...(models ? { models } : {}),
      ...(modelsError ? { modelsError } : {}),
      editorModel,
      ...(bridgeRunning ? { currentModel: { provider: model.provider, id: model.id }, currentThinking: thinkingLevel } : {}),
      ...(bridgeRunning && thinkingLevels ? { thinkingLevels } : {}),
      ...(usage ? { usage } : {}),
      ...(usageLoading ? { usageLoading } : {}),
      ...(restartRecommended ? { restartRecommended } : {}),
    });

  const resolveEditorModel = (requestId: string): void => {
    const revision = ++editorModelRevision;
    editorModel = { ...editorModel, resolving: true, error: undefined };
    postSetup();
    later(450, () => {
      if (revision !== editorModelRevision) { postSetup(requestId); return; }
      if (!bridgeRunning) {
        editorModel = { ...editorModel, resolving: false, effective: undefined };
        postSetup(requestId, 'Not connected.');
        return;
      }
      const chosen = editorModel.choice === 'auto' ? { provider: 'openai-codex', id: 'gpt-5.6-luna', name: 'GPT-5.6-Luna' }
        : editorModel.choice === 'chat' ? { ...model }
        : availableModels().find((item) => item.provider === editorModel.model?.provider && item.id === editorModel.model?.id);
      editorModel = { ...editorModel, resolving: false, effective: chosen, error: chosen ? undefined : 'The selected editor model is not available (mock failure).' };
      postSetup(requestId);
    });
  };

  const usageSoonAt = Date.now() + 20_000;
  /** Usage of every signed-in provider: a mix of statuses and reset times (a few hours, tomorrow, days away). */
  const buildUsage = (): SetupUsagePayload => {
    const inMinutes = (minutes: number): string => new Date(Date.now() + minutes * 60_000).toISOString();
    const reports: UsageReport[] = [];
    if (authenticated.has('openai-codex')) {
      reports.push({
        provider: 'openai-codex',
        account: 'dev.user@example.com',
        limits: [
          // ?usageReset=1: a 5-hour window whose reset time already passed, reported full (as a report taken before the reset).
          // ?usageReset=soon: full until 20 seconds after the page opened, then a new window.
          new URLSearchParams(window.location.search).get('usageReset') === '1'
            ? { label: 'Codex', window: '5 Hour', resetsAt: inMinutes(-10), usedFraction: 1, status: 'exhausted' }
            : new URLSearchParams(window.location.search).get('usageReset') === 'soon'
              ? Date.now() < usageSoonAt
                ? { label: 'Codex', window: '5 Hour', resetsAt: new Date(usageSoonAt).toISOString(), usedFraction: 1, status: 'exhausted' }
                : { label: 'Codex', window: '5 Hour', resetsAt: new Date(usageSoonAt + 5 * 3_600_000).toISOString(), usedFraction: 0.02, status: 'ok' }
              : { label: 'Codex', window: '5 Hour', resetsAt: inMinutes(95), usedFraction: 0.32, status: 'ok' },
          { label: 'Codex', window: '7 Day', resetsAt: inMinutes(60 * 24 * 3 + 40), usedFraction: 0.61, status: 'ok' },
        ],
      });
    }
    if (authenticated.has('anthropic')) {
      reports.push({
        provider: 'anthropic',
        account: 'dev.user@anthropic-example.com',
        limits: [
          new URLSearchParams(window.location.search).get('usageReset') === 'soon'
            ? Date.now() < usageSoonAt
              ? { label: 'Session', window: '5 Hour', resetsAt: new Date(usageSoonAt).toISOString(), usedFraction: 1, status: 'exhausted' }
              : { label: 'Session', window: '5 Hour', resetsAt: new Date(usageSoonAt + 5 * 3_600_000).toISOString(), usedFraction: 0.03, status: 'ok' }
            : { label: 'Session', window: '5 Hour', resetsAt: inMinutes(48), usedFraction: 0.86, status: 'warning' },
          { label: 'Weekly (all models)', window: '7 Day', resetsAt: inMinutes(60 * 30), usedFraction: 1, status: 'exhausted' },
        ],
      });
    }
    if (authenticated.has('github-copilot')) {
      reports.push({
        provider: 'github-copilot',
        limits: [{ label: 'Premium requests', window: '1 Month', resetsAt: inMinutes(60 * 24 * 20), used: 120, limit: 300, unit: 'requests', status: 'ok' }],
      });
    }
    return { fetchedAt: new Date().toISOString(), reports };
  };

  /** Keeps the session on a model that is still offered, and its thinking level on one that model has. */
  const ensureModelAndLevel = (): void => {
    const offered = availableModels();
    if (offered.length > 0 && !offered.some((item) => item.provider === model.provider && item.id === model.id)) {
      model = { provider: offered[0]!.provider, id: offered[0]!.id, name: offered[0]!.name };
    }
    const levels = modelLevels(model);
    if (!levels.includes(thinkingLevel)) thinkingLevel = levels.includes('high') ? 'high' : levels[levels.length - 1]!;
    if (thinkingLevels) thinkingLevels = levels;
  };

  const mockAvatarImages = new Map<string, string>();
  const mockRoles: Role[] = [
    {
      name: 'architect',
      title: '설계자',
      job: '기능 아키텍처 및 인터페이스 설계',
      description: '요구사항을 분석하고 모듈 구조와 계약 인터페이스를 설계한다.',
      instructions: '기존 아키텍처와 코딩 컨벤션을 준수하고 모듈 경계를 명확히 정의하세요.',
      model: 'openai-codex/gpt-6-sol',
      thinkingLevel: 'high',
      tools: 'readOnly',
      scope: 'project',
      editable: true,
      file: 'C:\\workspace\\.pomp\\agents\\architect.md',
      hash: new URLSearchParams(window.location.search).has('agentsConflict') ? 'conflict-hash' : 'hash-role-1',
    },
    {
      name: 'implementer',
      title: '구현자',
      job: '코드 작성 및 리팩토링',
      description: '설계된 사양에 따라 깔끔하고 안전한 코드를 작성한다.',
      instructions: '단일 책임 원칙을 준수하고 오류 처리를 철저히 하세요.',
      tools: 'all',
      scope: 'project',
      editable: true,
      file: 'C:\\workspace\\.pomp\\agents\\implementer.md',
      hash: 'hash-role-2',
    },
    {
      name: 'reviewer-strict',
      title: '꼼꼼한 리뷰어',
      job: '변경 코드 리뷰',
      description: '변경된 코드의 버그·예외 처리·보안 문제를 근거와 함께 찾는다. 코드는 바꾸지 않는다.',
      instructions: '코드를 직접 변경하지 말고 위치와 구체적인 개선안을 제시하세요.',
      model: '@smol',
      thinkingLevel: 'max',
      tools: 'readOnly',
      scope: 'user',
      editable: true,
      file: 'C:\\Users\\dev\\.omp\\agent\\agents\\reviewer-strict.md',
      hash: 'hash-role-3',
    },
    {
      name: 'scout',
      title: 'Scout',
      job: '코드베이스 조사',
      description: '코드베이스의 구조와 참조 관계를 빠르게 파악한다.',
      instructions: '읽기 전용 도구만 사용하고 결과를 요약 보고하세요.',
      tools: 'readOnly',
      scope: 'bundled',
      editable: false,
    },
    { name: 'architect', title: '개인 설계자', job: '개인 설계', description: '같은 이름의 프로젝트 설계자보다 개인 항목이 우선한다.', instructions: '개인 설계 지침', tools: 'readOnly', scope: 'user', editable: true, hash: 'hash-hidden-architect', file: 'C:\\Users\\dev\\.omp\\agent\\agents\\architect.md' },
    { name: 'tester', title: '테스터', job: '테스트 작성 및 검증', description: '경계 조건과 회귀 테스트를 검증한다.', instructions: '재현 가능한 검증 결과를 보고하세요.', tools: 'all', scope: 'user', editable: true, hash: 'hash-tester', file: 'C:\\Users\\dev\\.omp\\agent\\agents\\tester.md' },
    { name: 'yaml-reader', title: '외부 읽기 역할', job: '조사', description: '여러 줄 YAML로 정의된 정상 역할', instructions: '읽기만 하세요.', tools: 'custom', scope: 'user', editable: false, note: '여러 줄 YAML을 안전하게 다시 쓸 수 없어 POMP에서는 읽기 전용입니다.', hash: 'hash-yaml', file: 'C:\\Users\\dev\\.omp\\agent\\agents\\yaml-reader.md' },
    { name: 'broken-role', title: '오류 역할', job: '', description: '', instructions: '', tools: 'all', scope: 'project', editable: false, error: 'YAML 앞머리 구문 오류 (line 3).', file: 'C:\\workspace\\.omp\\agents\\broken-role.md' },
  ];
  // ?roles=N adds N project roles (scale checks of the role table and the team member picker).
  for (let index = 1, count = Math.max(0, Math.min(10000, Number(new URLSearchParams(window.location.search).get('roles')) || 0)); index <= count; index++) {
    const name = `role-${String(index).padStart(4, '0')}`;
    mockRoles.push({ name, title: `역할 ${index}`, job: '대량 확인용', description: '대량 확인용 역할', instructions: '확인용', tools: 'readOnly', scope: 'project', editable: true, hash: `hash-${name}`, file: `C:\\workspace\\.pomp\\agents\\${name}.md` });
  }

  const mockTeams: Team[] = [
    {
      id: 'feature-dev',
      title: '기능 개발팀',
      goal: '요청한 기능을 설계·구현·검토·테스트까지 완성',
      members: ['architect', 'implementer', 'reviewer-strict'],
      mode: 'lead',
      instructions: '공통 아키텍처 규칙과 코딩 컨벤션을 준수하세요.',
      membersOnly: true,
      scope: 'project',
      file: 'C:\\workspace\\.pomp\\teams\\feature-dev.json',
      hash: 'hash-team-1',
      missing: [],
    },
    { id: 'feature-dev', title: '개인 기능팀 (가려짐)', goal: '프로젝트 팀이 우선하며 개인 항목은 편집·삭제할 수 있다.', members: ['architect', 'tester'], mode: 'parallel', instructions: '', membersOnly: true, scope: 'user', file: 'C:\\Users\\dev\\AppData\\Roaming\\Pomp\\teams\\feature-dev.json', hash: 'hash-hidden-feature', missing: [], shadowed: true },
    { id: 'quick-review', title: '신속 검토팀', goal: '읽기 역할로 순서대로 검토', members: ['scout', 'yaml-reader'], mode: 'pipeline', instructions: '조사 결과를 다음 구성원에게 넘기세요.', membersOnly: true, scope: 'user', file: 'C:\\Users\\dev\\AppData\\Roaming\\Pomp\\teams\\quick-review.json', hash: 'hash-team-2', missing: [] },
    { id: 'missing-team', title: '누락 구성원 팀', goal: '', members: ['architect', 'ghost-role'], mode: 'parallel', instructions: '', membersOnly: true, scope: 'user', file: 'C:\\Users\\dev\\AppData\\Roaming\\Pomp\\teams\\missing-team.json', hash: 'hash-team-3', missing: ['ghost-role'] },
  ];

  const urlParams = new URLSearchParams(window.location.search);
  const computerQuery = urlParams.get('computer');
  const permissionModeQuery = urlParams.get('permissionMode');
  const computerState: ComputerStatePayload = {
    policy: computerQuery === 'off' ? 'off' : 'perConversation',
    browser: false,
    confirmReads: false,
    desktop: false,
    pending: false,
    available: computerQuery !== 'unavailable',
    message: computerQuery === 'unavailable' ? '컴퓨터 제어를 사용할 수 없는 환경입니다 (mock).' : undefined,
  };

  // POMP's own role: its picture and the two APPEND_SYSTEM.md files (project, user); saving marks the engine as behind until a restart.
  const mockMain: { avatar: RoleAvatar | null; files: Record<'project' | 'user', { text: string; rev: number }>; restartRequired: boolean } = {
    avatar: null, files: { project: { text: '', rev: 0 }, user: { text: '', rev: 0 } }, restartRequired: false };
  const mainScope = () => mockMain.files.project.text ? 'project' : mockMain.files.user.text ? 'user' : null;
  const mainFile = (scope: 'project' | 'user') => { const file = mockMain.files[scope]; return file.text ? { exists: true, text: file.text, hash: `main-${scope}-${file.rev}` } : { exists: false, text: '' }; };
  const postAgents = () => {
    const rank = { user: 0, project: 1, extension: 2, bundled: 2 };
    for (const role of mockRoles) role.shadowed = mockRoles.some(other => !other.error && other.name === role.name && rank[other.scope] < rank[role.scope]);
    for (const team of mockTeams) team.shadowed = team.scope === 'user' && mockTeams.some(other => other.scope === 'project' && other.id.toLowerCase() === team.id.toLowerCase());
    // Copies, as the real host sends fresh JSON: the page keeps memos keyed by the arrays.
    post('agents/state', { root: MOCK_ROOT, roles: mockRoles.map(role => ({ ...role })), teams: mockTeams.map(team => ({ ...team })), diagnostics: [],
      main: { avatar: mockMain.avatar, scope: mainScope(), restartRequired: mockMain.restartRequired } });
  };

  // ---- Knowledge (in-memory only; nothing here is persisted beyond the page) ----
  const knowledge = createMockKnowledge({ post, root: MOCK_ROOT, sessionId: () => sessionId });
  const advisors = createMockAdvisors({ post, root: MOCK_ROOT, sessionId: () => sessionId, model: () => model ? `${model.provider}/${model.id}` : undefined });
  // ---- Event rules preview (in-memory only; nothing here is persisted beyond the page) ----
  let mockHooks: AgentHookRule[] = [
    { id: 'hook-1', name: '실패 알림', enabled: true, event: 'agent.failed', sourceKind: 'any', action: 'notify', message: '', revision: 1 },
    { id: 'hook-2', name: '팀 완료 전달', enabled: true, event: 'team.completed', sourceKind: 'team', sourceId: 'feature-dev', action: 'forward', targetKind: 'main', message: '팀이 끝났어요.', revision: 1 },
  ];
  let mockHookAutoForward = false;
  const mockHookLog = [
    { at: '2026-10-05T00:00:00Z', rule: '실패 알림', event: 'agent.failed', source: 'agent architect', outcome: 'notified' },
  ];
  const postHooks = (requestId?: string | null, error?: string) => post('agentHooks/state', { requestId: requestId ?? null, root: MOCK_ROOT, sessionId, autoForward: mockHookAutoForward, forwards: 3, maxForwards: 10, log: mockHookLog, rules: mockHooks, ...(error ? { error } : {}) });
  const slugOk = (value: unknown): value is string => typeof value === 'string' && /^[a-z0-9-]{2,40}$/.test(value);
  const handleHooks = (payload: { requestId: string; root?: string; sessionId?: string; action: string; id?: string; revision?: number; rule?: Partial<AgentHookRule>; enabled?: boolean }) => {
    const { requestId, action, id, revision, rule, enabled } = payload;
    const fail = (error: string) => postHooks(requestId, `${error} (mock)`);
    if (payload.root !== MOCK_ROOT || payload.sessionId !== sessionId) { fail('대화나 작업 영역이 바뀌었어요. 새로 고친 뒤 다시 시도해 주세요.'); return; }
    if (action === 'refresh') { postHooks(requestId); return; }
    if (action === 'autoForward') {
      if (typeof enabled !== 'boolean') { fail('켜기/끄기 값이 필요해요.'); return; }
      mockHookAutoForward = enabled;
      postHooks(requestId);
      return;
    }
    if (action === 'save' && rule) {
      const name = (rule.name ?? '').trim();
      const message = (rule.message ?? '').trim();
      const events = ['agent.completed', 'agent.failed', 'team.completed', 'team.failed', 'feedback.waiting'];
      const teamEvent = typeof rule.event === 'string' && rule.event.startsWith('team.');
      let ok = name.length > 0 && name.length <= 60 && !/[\r\n]/.test(rule.name ?? '') && events.includes(rule.event as string);
      if (rule.sourceKind === 'any') ok &&= rule.sourceId == null;
      else if (rule.sourceKind === 'agent' && !teamEvent && rule.event !== 'feedback.waiting') ok &&= slugOk(rule.sourceId);
      else if (rule.sourceKind === 'team' && teamEvent) ok &&= slugOk(rule.sourceId);
      else ok = false;
      if (rule.action === 'notify') ok &&= rule.targetKind == null && rule.targetId == null;
      else if (rule.action === 'forward') {
        if (rule.targetKind === 'main') ok &&= rule.targetId == null;
        else if (rule.targetKind === 'agent' || rule.targetKind === 'team') ok &&= slugOk(rule.targetId);
        else ok = false;
        ok &&= message.length > 0;
        if (teamEvent && rule.targetKind === 'team' && rule.targetId === rule.sourceId) { fail('팀 실행 결과를 같은 팀에 다시 보내는 규칙은 만들 수 없어요.'); return; }
      } else ok = false;
      ok &&= message.length <= 2000;
      if (!ok) { fail('이벤트·출처·동작·대상 조합이 올바르지 않아요.'); return; }
      if (!id) {
        if (mockHooks.length >= 30) { fail('이벤트 규칙은 30개까지예요.'); return; }
        const created: AgentHookRule = { id: `hook-${Date.now()}`, name, enabled: rule.enabled !== false, event: rule.event!, sourceKind: rule.sourceKind!, sourceId: rule.sourceKind === 'any' ? undefined : rule.sourceId, action: rule.action!, targetKind: rule.action === 'notify' ? undefined : rule.targetKind, targetId: rule.action === 'notify' || rule.targetKind === 'main' ? undefined : rule.targetId, message, revision: 1 };
        mockHooks.push(created);
        postHooks(requestId);
        return;
      }
      const row = mockHooks.find((item) => item.id === id);
      if (!row || row.revision !== revision) { fail('규칙이 바뀌었거나 지워졌어요. 새로 고친 뒤 다시 시도해 주세요.'); return; }
      Object.assign(row, { name, enabled: rule.enabled !== false, event: rule.event!, sourceKind: rule.sourceKind!, sourceId: rule.sourceKind === 'any' ? undefined : rule.sourceId, action: rule.action!, targetKind: rule.action === 'notify' ? undefined : rule.targetKind, targetId: rule.action === 'notify' || rule.targetKind === 'main' ? undefined : rule.targetId, message, revision: row.revision + 1 });
      postHooks(requestId);
      return;
    }
    if (action === 'delete') {
      const index = mockHooks.findIndex((item) => item.id === id && item.revision === revision);
      if (index < 0) { fail('규칙이 바뀌었거나 지워졌어요. 새로 고친 뒤 다시 시도해 주세요.'); return; }
      mockHooks.splice(index, 1);
      postHooks(requestId);
      return;
    }
    fail('알 수 없는 이벤트 규칙 동작이에요.');
  };
  const postComputer = () => post('computer/state', computerState);
  const postBackground = () =>
    post('workspace/backgroundState', {
      root: MOCK_ROOT,
      sessionId,
      state: 'live',
      message: '',
      jobs: [
        {
          id: 'job-mock-1',
          agent: 'architect',
          description: '기능 아키텍처 분석 및 인터페이스 설계',
          status: 'completed',
          result: '인터페이스 설계를 완료했습니다.',
          parentToolCallId: 'team-call-101',
          team: 'feature-dev',
          progress: {
            toolCount: 5,
            tokens: 2800,
            durationMs: 3500,
            recentTools: ['read', 'grep', 'glob'],
            resolvedModel: 'openai-codex/gpt-6-sol',
            thinkingLevel: 'high',
          },
        },
        {
          id: 'job-mock-2',
          promptId: current?.promptId,
          agent: 'implementer',
          description: '핵심 모듈 구현 코드 작성',
          status: 'running',
          parentToolCallId: 'team-call-101',
          team: 'feature-dev',
          progress: {
            toolCount: 12,
            tokens: 8500,
            durationMs: 11200,
            recentTools: ['write', 'edit'],
            resolvedModel: 'openai-codex/gpt-6-sol',
          },
        },
      ],
    });

  // ---- Bridge state scripts ----
  const bridgeStart = async (): Promise<void> => {
    bridgeRunning = false;
    postCommands();
    post('bridge/state', { state: 'starting' });
    await wait(350);
    if (!hasProvider()) {
      // Like the real host: omp exits with "No models available" and the bridge fails at once, flagged for the setup panel.
      post('bridge/state', { state: 'failed', reason: 'setupRequired', message: SETUP_STDERR });
      postSetup();
      return;
    }
    post('bridge/state', { state: 'handshaking' });
    await wait(350);
    post('bridge/state', { state: 'running' });
    bridgeRunning = true;
    postCommands();
    restartRecommended = false;
    models = undefined; // a restarted engine lists its models again
    thinkingLevels = undefined;
    ensureModelAndLevel();
    postSession(false);
    postBackground(); // Session scope is established before the scoped job snapshot arrives.
    postSetup();
  };

  /**
   * New credentials: a stopped omp starts by itself. A running one lists the new provider's models at once after a sign-in (live,
   * no restart), but only gets the recommendation to restart after a saved key or a sign-out (it adopts those only when it restarts).
   */
  const credentialsChanged = (kind: 'login' | 'key' | 'logout'): void => {
    if (!bridgeRunning) {
      void bridgeStart();
      return;
    }
    if (kind === 'key' || kind === 'logout') {
      restartRecommended = true;
      return;
    }
    if (models) models = availableModels();
  };

  /** A sign-in ends: the provider is signed in (or not) and everyone is told. */
  const finishLogin = (provider: string | undefined, error?: string): void => {
    clearLoginTimers();
    if (error) {
      login = { state: 'failed', ...(provider ? { provider } : {}), message: error };
    } else {
      if (provider) authenticated.add(STORED_UNDER[provider] ?? provider);
      login = { state: 'succeeded', ...(provider ? { provider } : {}), message: 'Login completed.' };
      credentialsChanged('login');
    }
    postSetup();
  };

  /** What a sign-in of `provider` looks like to the page (see the header comment). */
  const startLogin = (provider: string): void => {
    const url = `https://auth.example.com/oauth/authorize?client_id=pomp-mock&provider=${encodeURIComponent(provider)}&state=mock-state`;
    switch (provider) {
      case 'anthropic':
        login = { state: 'running', provider, url, instructions: 'Sign in in the browser, then copy the authorization code it shows and paste it below.' };
        loginTimers.push(
          later(700, () => {
            login = { ...login, progress: 'Waiting for the authorization code', prompt: { id: `prompt-${++promptCounter}`, message: 'Paste the authorization code', placeholder: 'code#state' } };
            postSetup();
          }),
        );
        break;
      case 'github-copilot':
        login = { state: 'running', provider, url: 'https://github.com/login/device', instructions: 'Enter the device code ABCD-1234 on the page that opened.', progress: 'Waiting for authorization' };
        loginTimers.push(later(4_000, () => finishLogin(provider)));
        break;
      case 'google-antigravity':
        login = { state: 'running', provider, progress: 'Starting the sign-in' };
        loginTimers.push(later(1_200, () => finishLogin(provider, 'This provider needs a terminal: the secret must be typed in a console window (stdin is not available here).')));
        break;
      default:
        login = { state: 'running', provider, url, progress: 'Waiting for the browser' };
        loginTimers.push(later(3_000, () => finishLogin(provider)));
    }
  };

  const handleSetup = (command: SetupCommand): void => {
    const { requestId } = command;
    switch (command.action) {
      case 'refresh':
        postSetup(requestId);
        break;
      case 'listProviders':
        if (opts.providers === 'slow') {
          later(3_000, () => {
            providersLoaded = true;
            postSetup(requestId);
          });
        } else {
          later(500, () => {
            providersLoaded = true;
            postSetup(requestId);
          });
        }
        break;
      case 'login': {
        if (login.state === 'running') {
          postSetup(requestId, 'Another login is already running.');
          break;
        }
        startLogin(command.provider);
        postSetup(requestId);
        break;
      }
      case 'loginConsole': {
        if (login.state === 'running') {
          postSetup(requestId, 'Another login is already running.');
          break;
        }
        const provider = command.provider;
        login = { state: 'running', ...(provider ? { provider } : {}) };
        postSetup(requestId);
        loginTimers.push(later(3_000, () => finishLogin(provider ?? 'openai')));
        break;
      }
      case 'answerPrompt': {
        const prompt = login.prompt;
        if (login.state !== 'running' || !prompt || prompt.id !== command.promptId) {
          postSetup(requestId, 'There is no such question to answer.');
          break;
        }
        if (command.value === undefined) {
          finishLogin(login.provider, 'The sign-in was cancelled.');
          postSetup(requestId);
          break;
        }
        if (command.value === 'wrong-code') {
          // Refused once: the same question comes back with a new id and a progress text.
          login = { ...login, progress: 'The code was not accepted. Paste it again.', prompt: { ...prompt, id: `prompt-${++promptCounter}` } };
          postSetup(requestId);
          break;
        }
        login = { ...login, progress: 'Checking the code', prompt: undefined };
        delete login.prompt;
        postSetup(requestId);
        loginTimers.push(later(800, () => finishLogin(login.provider)));
        break;
      }
      case 'cancelLogin':
        clearLoginTimers();
        if (login.state === 'running') login = { state: 'idle' };
        postSetup(requestId);
        break;
      case 'saveKey': {
        const provider = KEY_PROVIDERS.find((item) => item.id === command.provider);
        if (!provider) {
          postSetup(requestId, 'This provider does not accept an API key.');
          break;
        }
        if (!/^[\x21-\x7e]{8,1024}$/.test(command.key)) {
          postSetup(requestId, 'The key must be 8 to 1024 printable characters without spaces.');
          break;
        }
        keys = [...keys.filter((item) => item.provider !== provider.id), { provider: provider.id, envVar: provider.envVar, savedUtc: new Date().toISOString() }];
        credentialsChanged('key');
        postSetup(requestId);
        break;
      }
      case 'saveLocalServer': {
        const name = command.name.trim();
        if (!name || name.length > 40 || localServers.some((item) => item.id !== command.id && item.name.toLowerCase() === name.toLowerCase())) {
          postSetup(requestId, '로컬 서버 이름이 올바르지 않습니다.');
          break;
        }
        const existing = command.id ? localServers.find((item) => item.id === command.id) : undefined;
        if (command.id && !existing) { postSetup(requestId, '그 로컬 서버를 찾을 수 없습니다.'); break; }
        const slug = name.toLowerCase().replace(/[^a-z0-9]+/g, '-').replace(/^-|-$/g, '') || 'server';
        let id = existing?.id ?? `local-${slug}`;
        for (let n = 2; !existing && localServers.some((item) => item.id === id); n++) id = `local-${slug}-${n}`;
        const row = { id, name, address: command.address, hasKey: !!command.key || !!existing?.hasKey, savedUtc: new Date().toISOString() };
        localServers = existing ? localServers.map((item) => (item.id === id ? row : item)) : [...localServers, row];
        credentialsChanged('key');
        postSetup(requestId);
        break;
      }
      case 'removeLocalServer':
        localServers = localServers.filter((item) => item.id !== command.id);
        postSetup(requestId);
        break;
      case 'removeKey':
        if (!keys.some((item) => item.provider === command.provider)) {
          postSetup(requestId, 'No saved key for this provider.');
          break;
        }
        keys = keys.filter((item) => item.provider !== command.provider);
        postSetup(requestId);
        break;
      case 'logout': {
        const provider = command.provider;
        if (!/^[a-z0-9][a-z0-9.-]{0,63}$/.test(provider)) {
          postSetup(requestId, 'Login provider must contain 1–64 lowercase letters, digits, hyphens or dots, starting with a letter or digit.');
          break;
        }
        if (login.state === 'running') {
          postSetup(requestId, 'A provider login is running, so signing out has to wait. Finish or cancel it first.');
          break;
        }
        // The real host runs `omp auth-broker logout <provider>` and reads the provider list again before it answers.
        later(opts.logout === 'slow' ? 6_000 : 700, () => {
          if (opts.logout === 'error') {
            postSetup(requestId, 'Signing out exited with code 1 (mock failure).');
            return;
          }
          // Every stored sign-in of the provider goes; one that has an API key or an environment variable stays authenticated ("env").
          if (opts.logout !== 'env') authenticated.delete(provider);
          if (usage) usage = { ...usage, reports: usage.reports.filter((report) => report.provider !== provider) };
          models = undefined;
          credentialsChanged('logout');
          postSetup(requestId);
        });
        break;
      }
      case 'listModels':
        if (!bridgeRunning) {
          postSetup(requestId, 'Not connected.');
          break;
        }
        later(opts.models === 'slow' ? 3_000 : 600, () => {
          if (opts.models === 'error') {
            modelsError = 'get_available_models timed out (mock failure).';
            postSetup(requestId, modelsError);
            return;
          }
          modelsError = undefined;
          models = availableModels();
          postSetup(requestId);
        });
        break;
      case 'setEditorModel': {
        const chosen = command.choice === 'model' ? availableModels().find((item) => item.provider === command.provider && item.id === command.modelId) : undefined;
        if (command.choice === 'model' && (!bridgeRunning || !chosen)) {
          postSetup(requestId, bridgeRunning ? 'The selected editor model is not available (mock failure).' : 'Not connected.');
          break;
        }
        ++editorModelRevision;
        editorModel = command.choice === 'model' ? { choice: 'model', model: { provider: command.provider, id: command.modelId } } : { choice: command.choice };
        later(250, () => postSetup(requestId));
        break;
      }
      case 'resolveEditorModel':
        resolveEditorModel(requestId);
        break;
      case 'setModel': {
        const chosen = availableModels().find((item) => item.provider === command.provider && item.id === command.modelId);
        if (!bridgeRunning || !chosen) {
          postSetup(requestId, bridgeRunning ? `Model not found: ${command.provider}/${command.modelId}` : 'Not connected.');
          break;
        }
        model = { provider: chosen.provider, id: chosen.id, name: chosen.name };
        ensureModelAndLevel();
        // The host answers first (still with the previous session values for a moment); session/info follows.
        postSetup(requestId);
        later(250, () => postSession(false));
        break;
      }
      case 'listThinking':
        if (!bridgeRunning) {
          postSetup(requestId, 'Not connected.');
          break;
        }
        later(250, () => {
          thinkingLevels = modelLevels(model);
          postSetup(requestId);
        });
        break;
      case 'setThinking': {
        if (!bridgeRunning || !modelLevels(model).includes(command.level)) {
          postSetup(requestId, bridgeRunning ? `Thinking level not available for this model: ${command.level}` : 'Not connected.');
          break;
        }
        thinkingLevel = command.level;
        postSetup(requestId);
        later(250, () => postSession(false));
        break;
      }
      case 'listUsage':
        usageLoading = true;
        postSetup(requestId); // answered at once; the report follows when `omp usage` is done
        later(opts.usage === 'slow' ? 6_000 : 1_200, () => {
          usageLoading = false;
          usage = opts.usage === 'error' ? { fetchedAt: new Date().toISOString(), reports: [], error: 'Usage could not be checked (mock failure).' } : buildUsage();
          postSetup();
        });
        break;
    }
  };

  const bridgeFailure = async (): Promise<void> => {
    bridgeRunning = false;
    postCommands();
    post('bridge/state', { state: 'starting' });
    await wait(300);
    post('bridge/state', { state: 'handshaking' });
    await wait(300);
    for (const [attempt, retry] of [[1, 2_000], [2, 4_000], [3, 8_000]] as const) {
      post('bridge/state', { state: 'reconnecting', attempt, nextRetryMs: retry, message: 'POMP가 예기치 않게 종료되었습니다 (코드 1)' });
      await wait(retry / 2);
    }
    post('bridge/state', { state: 'failed', message: 'POMP가 연속 3회 실패했습니다.' });
  };

  const bridgeCrashAndRecover = async (): Promise<void> => {
    await wait(3_000);
    // The omp process died mid-turn: the run just stops, the UI must cope on its own.
    abortRun(current);
    bridgeRunning = false;
    postCommands();
    post('bridge/state', { state: 'reconnecting', attempt: 1, nextRetryMs: 5_000, message: 'POMP가 종료되었습니다' });
    await wait(5_000);
    post('bridge/state', { state: 'starting' });
    await wait(500);
    post('bridge/state', { state: 'handshaking' });
    await wait(500);
    post('bridge/state', { state: 'running' });
    bridgeRunning = true;
    postCommands();
    postSession(false);
  };

  // ---- Auto-typing the scenario's first prompt (through the real composer) ----
  const autoSend = async (text: string): Promise<void> => {
    for (let attempt = 0; attempt < 100; attempt++) {
      const input = document.querySelector<HTMLTextAreaElement>('[data-testid="composer-input"]');
      if (input && !input.disabled) {
        // React tracks the value itself: go through the native setter so the input event is seen as a change.
        Object.getOwnPropertyDescriptor(HTMLTextAreaElement.prototype, 'value')?.set?.call(input, text);
        input.dispatchEvent(new Event('input', { bubbles: true }));
        await new Promise((resolve) => window.setTimeout(resolve, 60));
        document.querySelector<HTMLButtonElement>('[data-testid="send"]')?.click();
        return;
      }
      await new Promise((resolve) => window.setTimeout(resolve, 100));
    }
  };

  const replay = (truncated = false, messages: HostMessage[] = REPLAY_FIXTURE): void => {
    post('chat/replayBegin', { truncated });
    messages.forEach((message) => deliver(message));
    post('chat/replayEnd', {});
  };
  const replaySession = (entry: SessionEntry): void => {
    const messages = (transcripts.get(entry.id) ?? []).slice(0, 4);
    const frames: HostMessage[] = [];
    for (let index = 0; index < messages.length; index += 2) {
      const promptId = nextId('replay-prompt');
      const messageId = nextId('replay-message');
      frames.push({ type: 'chat/turnStarted', payload: { promptId, text: messages[index]!.text } });
      const assistant = messages[index + 1];
      if (assistant) {
        frames.push(
          { type: 'chat/assistantStart', payload: { messageId } },
          { type: 'chat/assistantDelta', payload: { messageId, kind: 'text', delta: assistant.text } },
          { type: 'chat/assistantEnd', payload: { messageId, stopReason: 'stop' } },
        );
      }
      frames.push({ type: 'chat/turnEnd', payload: { promptId, status: 'completed' } });
    }
    replay((transcripts.get(entry.id)?.length ?? 0) > messages.length, frames);
  };

  /** session/new, or a tab's new conversation for `partner` (conversation/tab). Answers with exactly one session/newResult. */
  const newConversation = (requestId: string | undefined, partner?: ConversationPartner): void => {
    if (sessionChanging) {
      post('session/newResult', { requestId, status: 'failed', message: 'A session operation is still running.' });
      return;
    }
    if (opts.newSession !== 'completed') {
      // The real host answers exactly once; on failure / veto nothing changes on the omp side.
      post('session/newResult', { requestId, status: opts.newSession, message: opts.newSession === 'failed' ? 'Could not start a new session: mock failure' : 'An extension cancelled the new session.' });
      return;
    }
    abortRun(current);
    current = null;
    currentEntry = { id: nextId('mock-entry-new'), sessionId: nextId('mock-session-new'), title: '새 대화', modifiedUtc: new Date().toISOString(), visitedUtc: new Date().toISOString(), messageCount: 0, current: true, partner: partner ?? { kind: 'main' } };
    sessionId = currentEntry.sessionId!;
    mockTools.resetConversation(sessionId);
    conversations.changedSession(partner);
    sessionName = undefined;
    transcripts.set(currentEntry.id, []);
    touchSession();
    tokens = 0;
    postSession(false);
    knowledge.snapshot();
    post('session/newResult', { requestId, status: 'completed' });
    postHistory();
  };

  const handleHistory = async (command: SessionHistoryCommand): Promise<void> => {
    const { requestId } = command;
    const mutating = command.action === 'switch' || command.action === 'rename' || command.action === 'compact' || command.action === 'delete';
    if (mutating && (!bridgeRunning || streaming || sessionChanging || permissionWaiters.size > 0 || dialogWaiters.size > 0)) {
      postHistory(requestId, !bridgeRunning ? 'Not connected.' : 'Finish the current response, approval or session operation before changing sessions.');
      return;
    }
    const mode = command.action === 'switch' ? opts.switchSession : command.action === 'refresh' || command.action === 'delete' ? 'ok' : opts[command.action];
    if (mutating) sessionChanging = true;
    try {
      await wait(opts.sessions === 'slow' || mode === 'slow' ? 3_000 : command.action === 'compact' ? 1_200 : 180);
      if (opts.sessions === 'error' || mode === 'error') {
        postHistory(requestId, `Session ${command.action} failed (mock failure).`);
        return;
      }
      switch (command.action) {
        case 'refresh':
          postHistory(requestId);
          break;
        case 'preview': {
          const entry = sessions.find((item) => item.id === command.id);
          if (!entry) {
            postHistory(requestId, 'The session entry was not found. Refresh the list and try again.');
            break;
          }
          const messages = transcripts.get(entry.id) ?? [];
          post('session/historyState', {
            ...historyState(requestId),
            preview: { id: entry.id, messages: messages.slice(0, 6).map((message) => ({ ...message })), truncated: messages.length > 6 },
          });
          break;
        }
        case 'messages': {
          if (command.sessionId !== sessionId) {
            postHistory(requestId, 'The current session changed. Refresh before reading its history.');
            break;
          }
          const messages = transcripts.get(currentEntry.id) ?? [];
          const offset = command.cursor === undefined ? 0 : /^\d{1,8}$/.test(command.cursor) ? Number(command.cursor) : -1;
          if (offset < 0 || offset > messages.length) {
            postHistory(requestId, 'The message cursor is invalid. Start again from the first page.');
            break;
          }
          const end = Math.min(offset + 40, messages.length);
          post('session/historyState', {
            ...historyState(requestId), sessionId, messages: messages.slice(offset, end).map((message) => ({ ...message })),
            ...(end < messages.length ? { nextCursor: String(end) } : {}), totalMessages: messages.length,
          });
          break;
        }
        case 'delete': {
          const entry = sessions.find(item => item.id === command.id);
          if (!entry) { postHistory(requestId, opts.locale.startsWith('ko') ? '대화를 찾을 수 없어요.' : 'The conversation was not found.'); break; }
          if (entry.id === currentEntry.id) {
            currentEntry = { id: nextId('mock-entry-new'), sessionId: nextId('mock-session-new'), title: opts.locale.startsWith('ko') ? '새 대화' : 'New conversation', modifiedUtc: new Date().toISOString(), messageCount: 0, current: true };
            sessionId = currentEntry.sessionId!;
            mockTools.resetConversation(sessionId); conversations.changedSession(); sessionName = undefined;
            transcripts.set(currentEntry.id, []); tokens = 0; touchSession(); postSession(false); knowledge.snapshot();
          }
          sessions = sessions.filter(item => item.id !== entry.id); transcripts.delete(entry.id); if (entry.sessionId) conversations.tabs.forget(entry.sessionId);
          postHistory(requestId); break;
        }
        case 'rename': {
          const name = command.name.trim();
          if (!name || command.name.length > 120 || /[\x00-\x1f\x7f]/.test(command.name)) {
            postHistory(requestId, 'The session name must be 1 to 120 characters without control characters.');
            break;
          }
          sessionName = name;
          currentEntry.name = name;
          currentEntry.title = name;
          touchSession();
          postSession(false);
          postHistory(requestId);
          break;
        }
        case 'compact':
          if (command.instructions !== undefined && command.instructions.length > 2000) {
            postHistory(requestId, 'Compaction instructions must be at most 2000 characters.');
            break;
          }
          tokens = Math.floor(tokens * 0.35);
          touchSession();
          postSession(false);
          postHistory(requestId);
          post('chat/notice', { level: 'info', message: `대화를 압축했습니다 (mock). 현재 컨텍스트: ${tokens.toLocaleString()} 토큰.${command.instructions ? `\n지시: ${command.instructions}` : ''}` });
          break;
        case 'switch': {
          const entry = sessions.find((item) => item.id === command.id);
          if (command.root !== MOCK_ROOT || !entry?.sessionId) {
            postHistory(requestId, 'The session entry is no longer available in this workspace.');
            break;
          }
          current = null;
          currentEntry = entry;
          sessionId = entry.sessionId;
          // Like the real host (PompSession.History): a completed switch also answers the page's pending new-session request.
          post('session/newResult', { requestId, status: 'completed' });
          mockTools.resetConversation(sessionId);
          conversations.changedSession();
          sessionName = entry.name;
          tokens = contextTokens.get(sessionId) ?? (entry.messageCount ?? 0) * 145;
          entry.visitedUtc = new Date().toISOString();
          postSession(false);
          knowledge.snapshot();
          replaySession(entry);
          postHistory(requestId);
          break;
        }
      }
    } finally {
      if (mutating) sessionChanging = false;
    }
  };

  let problemItems: HostToUi['workspace/problemsState']['items'] = new URLSearchParams(window.location.search).get('problems') === '1' ? [
    { id: 'mock-agent', source: 'agent', severity: 'warning', code: 'REVIEW', message: 'Null access after cancellation', path: MOCK_ROOT + '/src/Worker.cs', line: 42 },
    { id: 'mock-error', source: 'compiler', severity: 'error', code: 'CS0103', message: "The name 'result' does not exist", path: MOCK_ROOT + '/src/Worker.cs', line: 17 },
    { id: 'mock-warning', source: 'compiler', severity: 'warning', code: 'CS8602', message: 'Possible null reference', path: MOCK_ROOT + '/src/Service.cs', line: 9 },
  ] : [];
  const postProblems = () => post('workspace/problemsState', { root: MOCK_ROOT, items: problemItems, truncated: false, updatedUtc: new Date().toISOString() });
  // This conversation's hook runtime (workspace/hooksState): the listening switch and the run history of the hook scripts.
  let projectHooksEnabled = false;
  const projectHookRuns = [{ name: 'format-on-save', event: 'onFileSave', path: 'src/Program.cs', status: 'completed', createdUtc: '2026-10-11T05:02:00Z' }, { name: 'verify-build', event: 'onBuild', path: '', status: 'failed', createdUtc: '2026-10-11T04:47:00Z', error: 'exit code 1' }];
  const postProjectHooks = () => post('workspace/hooksState', { root: MOCK_ROOT, sessionId, enabled: projectHooksEnabled, running: false, attempts: 2, skipped: 0, message: '', history: projectHookRuns });
  let interactive: HostToUi['workspace/interactiveState'] = { root: MOCK_ROOT, sessionId, enabled: new URLSearchParams(window.location.search).get('interactive') === '1', supported: new URLSearchParams(window.location.search).get('interactiveSupported') === '1', connected: true, state: 'empty' };
  const postInteractive = (requestId?: string) => {
    if (interactive.sessionId !== sessionId) interactive = { ...interactive, sessionId, state: 'empty', terminalId: undefined, generation: undefined, command: undefined, shell: undefined, exitCode: undefined, output: '', totalCharacters: 0, outputStart: 0, sequence: 0 };
    post('workspace/interactiveState', { ...interactive, requestId });
  };
  const handleInteractive = (command: UiToHost['workspace/interactive']) => {
    if (command.root !== MOCK_ROOT || command.sessionId !== sessionId || !interactive.enabled || !interactive.supported) return;
    if (interactive.sessionId !== sessionId) postInteractive();
    if (command.action === 'start' && !['starting', 'running', 'closing'].includes(interactive.state)) {
      const output = 'PS> ' + String.fromCharCode(13, 10);
      interactive = { ...interactive, state: 'running', terminalId: nextId('terminal'), generation: nextId('generation'), exitCode: undefined, shell: 'pwsh', command: command.command ?? '', output, outputStart: 0, totalCharacters: output.length, sequence: (interactive.sequence ?? 0) + 1 };
    } else if (command.action !== 'status') {
      if (command.terminalId !== interactive.terminalId || command.generation !== interactive.generation) return;
      if (command.action === 'stop') interactive = { ...interactive, state: 'exited', exitCode: 0, sequence: (interactive.sequence ?? 0) + 1 };
      else if (command.action === 'input' && interactive.state === 'running') {
        const data = command.data ?? ''; if (data.length > 4096 || data.includes(String.fromCharCode(0))) return;
        const totalCharacters = (interactive.totalCharacters ?? 0) + data.length;
        const output = ((interactive.output ?? '') + data).slice(-32768);
        interactive = { ...interactive, output, outputStart: totalCharacters - output.length, totalCharacters, sequence: (interactive.sequence ?? 0) + 1 };
      }
    }
    postInteractive(command.requestId);
  };
  let approvalMode: ApprovalMode = 'alwaysAsk';
  let editorFollow: HostToUi['host/editorFollow'] = { live: false, bookmarks: false };
  const postHello = (): void => {
    post('host/hello', { version: '0.3.0-mock', vsVersion: '17.12 (mock)', locale: opts.locale, includeEditorContextByDefault: true, composerPredictions: new URLSearchParams(window.location.search).get('composerPredictions') === '1', wordCompletion: new URLSearchParams(window.location.search).get('wordCompletion') !== '0', approvalMode, shortcuts: { ...DEFAULT_SHORTCUTS } });
  };

  const sent: unknown[] = [];
  window.pompMockHost = { emit: (message) => deliver(message), replay, sent, knowledge };

  const onReady = (): void => {
    if (ready) return;
    ready = true;
    // The real host replays everything queued before ui/ready, starting with host/hello.
    postHello();
    post('host/editorFollow', editorFollow);
    post('theme/changed', {
      kind: opts.theme,
      colors: opts.theme === 'light' ? LIGHT : DARK,
      fontFamily: 'Segoe UI',
      fontSizePx: 12,
    });
    backlog.splice(0).forEach(deliver);
    mockTools.snapshot();
    conversations.snapshot();
    postSetup(); // the real host posts the setup state after ui/ready as well
    postCommands();
    postAgents();
    knowledge.snapshot();
    advisors.snapshot();
    postComputer();
    postBackground();
    postProblems();
    postProjectHooks();
    work.snapshot();
    postInteractive();
    // The real host sends engine/info at every engine start and in the ui/ready snapshot.
    if (opts.engine === 'bundled') post('engine/info', { source: 'bundled', version: '18.12.1', verified: true, expectedVersion: '18.12.1' });
    else if (opts.engine === 'settings') post('engine/info', { source: 'settings', version: '18.9.0', verified: false, expectedVersion: '18.12.1', path: 'D:\\tools\\omp-next\\omp.exe' });
    else if (opts.engine === 'installed') post('engine/info', { source: 'installed', version: '18.4.1', verified: false, path: 'C:\\Users\\dev\\AppData\\Local\\omp\\omp.exe' });
    postHistory(undefined, opts.sessions === 'error' ? 'The session list could not be read (mock failure).' : undefined);
    if (new URLSearchParams(window.location.search).get('taskApproval') === '1') post('permission/request', {
      requestId: nextId('task-approval'), source: 'ompApproval', kind: 'other', toolName: 'task', title: 'Allow tool: task',
      message: 'Allow tool: task\nBatch agents: task, scout\nTask:\nUpdate the composer\n+1 more task',
      tasks: [{ agent: 'task', task: 'Update the composer and add regression coverage.' }, { agent: 'scout', task: 'Inspect the composer and report wrapping and scrolling behavior.' }],
    });
    if (new URLSearchParams(window.location.search).get('knowledgeDraft') === '1') knowledge.draft();
    // ?attachments=N puts N files in the basket to check its table with thousands of rows.
    const seededFiles = Math.max(0, Math.min(10000, Number(new URLSearchParams(window.location.search).get('attachments')) || 0));
    if (seededFiles) post('composer/prefill', { files: Array.from({ length: seededFiles }, (_, i) => { const path = `src/Module${String(i % 50).padStart(2, '0')}/File${String(i + 1).padStart(5, '0')}.cs`; return { path, fullPath: MOCK_ROOT + '\\' + path.replaceAll('/', '\\') }; }) });

    const { scenario } = opts;
    if (scenario === 'failed') {
      void bridgeFailure();
      return;
    }
    void (async () => {
      await bridgeStart();
      const replayOption = new URLSearchParams(window.location.search).get('replay');
      replay(replayOption === 'truncated', replayOption ? REPLAY_FIXTURE : []);
      if (scenario === 'reconnect') void bridgeCrashAndRecover();
      if (scenario !== 'idle' && scenario !== 'setup' && opts.autosend) {
        await wait(300);
        await autoSend(PROMPT);
      }
    })();
  };

  const bridge = {
    postMessage(message: unknown): void {
      sent.push(message);
      const { type, payload } = (message ?? {}) as { type?: string; payload?: Record<string, unknown> };
      if (type && (work.handle(type, payload) || mockTools({ type, payload }) || conversations.handle(type, payload))) return;
      switch (type) {
        case 'ui/ready':
          onReady();
          break;
        case 'ui/editorFollow': {
          const command = payload as unknown as UiToHost['ui/editorFollow'];
          editorFollow = { ...editorFollow, live: command.live ?? editorFollow.live, bookmarks: command.bookmarks ?? editorFollow.bookmarks };
          post('host/editorFollow', { ...editorFollow, requestId: command.requestId });
          break;
        }
        case 'workspace/hooks': {
          const command = payload as unknown as UiToHost['workspace/hooks'];
          if (command.action === 'enable') projectHooksEnabled = command.enabled === true;
          postProjectHooks(); break;
        }
        case 'workspace/problems': {
          const command = payload as unknown as UiToHost['workspace/problems'];
          const item = problemItems.find(item => item.id === command.id);
          const ok = command.action === 'refresh' || !!item && (command.action === 'open' || item.source === 'agent');
          if (ok && command.action === 'dismiss') problemItems = problemItems.filter(item => item.id !== command.id);
          post('workspace/problemsReply', { requestId: command.requestId, ok, ...(!ok ? { error: opts.locale.startsWith('ko') ? '문제를 찾지 못했어요.' : 'Problem not found.' } : {}) });
          postProblems(); break;
        }
        case 'workspace/interactive':
          handleInteractive(payload as unknown as UiToHost['workspace/interactive']);
          break;
        case 'composer/predict': {
          const { requestId } = payload as unknown as UiToHost['composer/predict'];
          window.setTimeout(() => post('composer/prediction', { requestId, text: opts.locale.startsWith('ko') ? ' 구현해 줘.' : ' and add a regression test.' }), 60);
          break;
        }
        case 'chat/send':
          startTurn(payload as unknown as UiToHost['chat/send'], opts.scenario);
          break;
        case 'chat/steer':
          steerTurn(payload as unknown as UiToHost['chat/steer']);
          break;
        case 'btw/ask':
          askBtw(payload as unknown as UiToHost['btw/ask']);
          break;
        case 'btw/cancel': {
          const { recordId } = payload as unknown as UiToHost['btw/cancel'];
          if (btwActive && (!recordId || recordId === btwActive)) settleBtw('cancelled');
          break;
        }
        case 'chat/stop':
          stopTurn();
          break;
        case 'workspace/findFiles': {
          const { requestId, query, limit = 30 } = payload as unknown as UiToHost['workspace/findFiles'];
          const matches = WORKSPACE_FILES.filter((file) => file.path.toLowerCase().includes(query.toLowerCase()));
          window.setTimeout(() => post('workspace/files', { requestId, files: matches.slice(0, limit), truncated: matches.length > limit }), 60);
          break;
        }
        case 'workspace/countDraftTokens': {
          const request = payload as unknown as UiToHost['workspace/countDraftTokens'];
          window.setTimeout(() => post('workspace/tokenCount', { requestId: request.requestId, root: request.root, modelId: request.modelId, provider: request.provider, tokens: Math.ceil(request.text.length / 3) + 40 * (request.attachments?.length ?? 0), method: 'byte-estimate', estimated: true }), 60);
          return;
        }
        case 'workspace/countBasketTokens': {
          const { requestId, includeEditorContext, attachments = [] } = payload as unknown as UiToHost['workspace/countBasketTokens'];
          const editorContext = includeEditorContext ? 650 : 0;
          const files = 180 * attachments.length;
          window.setTimeout(() => post('workspace/basketTokens', { requestId, ok: true, tokens: files + editorContext, editorContext, attachments: files }), 60);
          break;
        }
        case 'workspace/pickFiles': {
          const { requestId } = payload as unknown as UiToHost['workspace/pickFiles'];
          window.setTimeout(() => post('workspace/files', { requestId, files: WORKSPACE_FILES.slice(0, 2), truncated: false }), 120);
          break;
        }
        case 'permission/respond': {
          const { requestId, decision, note } = payload as unknown as UiToHost['permission/respond'];
          permissionWaiters.get(requestId)?.({ decision, note });
          break;
        }
        case 'dialog/respond': {
          const response = payload as unknown as UiToHost['dialog/respond'];
          dialogWaiters.get(response.requestId)?.(response);
          break;
        }
        case 'session/history':
          void handleHistory(payload as unknown as SessionHistoryCommand).catch((error: unknown) => console.error('[mock host] history', error));
          break;
        case 'session/new':
          newConversation(((payload ?? {}) as Partial<UiToHost['session/new']>).requestId, conversations.tabs.current());
          break;
        case 'conversation/tab': {
          const command = payload as unknown as UiToHost['conversation/tab'];
          if (command.action === 'close' && command.sessionId !== sessionId) {
            conversations.tabs.close(command.sessionId ?? '');
            post('conversation/tabReply', { requestId: command.requestId, ok: true });
            break;
          }
          if (sessionChanging || streaming || permissionWaiters.size > 0 || dialogWaiters.size > 0) {
            post('session/newResult', { requestId: command.requestId, status: 'failed', message: 'Finish or stop the reply before switching tabs.' });
            break;
          }
          if (command.action === 'close') conversations.tabs.close(command.sessionId!);
          const target = command.action === 'open' ? conversations.tabs.openSession(command.partner!) : command.action === 'activate' && command.sessionId ? command.sessionId : conversations.tabs.main();
          const entry = target ? sessions.find(item => item.sessionId === target) : undefined;
          if (entry) void handleHistory({ action: 'switch', requestId: command.requestId, root: MOCK_ROOT, id: entry.id }).catch((error: unknown) => console.error('[mock host] tab', error));
          else newConversation(command.requestId, command.action === 'open' ? command.partner : undefined);
          break;
        }
        case 'bridge/restart':
          if (mockMain.restartRequired) { mockMain.restartRequired = false; postAgents(); }
          void bridgeStart();
          break;
        case 'setup/command':
          handleSetup(payload as unknown as SetupCommand);
          break;
        case 'advisors/command':
          advisors.handle(payload as unknown as UiToHost['advisors/command']);
          break;
        case 'agents/command': {
          const cmd = payload as unknown as AgentsCommand;
          const { requestId, action } = cmd;
          if (action === 'refresh') {
            postAgents();
          } else if (action === 'saveRole') {
            if (cmd.expectedHash === 'conflict-hash') {
              post('agents/reply', { requestId, ok: false, error: '다른 곳에서 파일이 변경되었습니다 (해시 불일치).' });
            } else if (cmd.role) {
              const idx = mockRoles.findIndex((r) => r.name === cmd.role?.name && r.scope === cmd.scope);
              if (idx >= 0) {
                mockRoles[idx] = { ...mockRoles[idx]!, ...cmd.role, hash: `hash-${Date.now()}` } as Role;
              } else {
                mockRoles.push({
                  ...cmd.role,
                  scope: cmd.scope || 'project',
                  editable: true,
                  file: `C:\\workspace\\.omp\\agents\\${cmd.role?.name}.md`,
                  hash: `hash-${Date.now()}`,
                } as Role);
              }
              post('agents/reply', { requestId, ok: true, name: cmd.role.name });
              postAgents();
            }
          } else if (action === 'deleteRole') {
            const idx = mockRoles.findIndex((r) => r.name === cmd.name && r.scope === cmd.scope);
            if (idx >= 0) mockRoles.splice(idx, 1);
            post('agents/reply', { requestId, ok: true, name: cmd.name });
            postAgents();
          } else if (action === 'saveTeam') {
            if (cmd.expectedHash === 'conflict-hash') {
              post('agents/reply', { requestId, ok: false, error: '다른 곳에서 팀 파일이 변경되었습니다 (해시 불일치).' });
            } else if (cmd.team) {
              const idx = mockTeams.findIndex((t) => t.id === cmd.team?.id && t.scope === cmd.scope);
              if (idx >= 0) {
                mockTeams[idx] = { ...mockTeams[idx]!, ...cmd.team, hash: `hash-${Date.now()}` } as Team;
              } else {
                mockTeams.push({
                  ...cmd.team,
                  scope: cmd.scope || 'project',
                  file: `C:\\workspace\\.pomp\\teams\\${cmd.team?.id}.json`,
                  hash: `hash-${Date.now()}`,
                  missing: [],
                } as Team);
              }
              post('agents/reply', { requestId, ok: true, name: cmd.team.id });
              postAgents();
            }
          } else if (action === 'deleteTeam') {
            const idx = mockTeams.findIndex((t) => t.id === cmd.name && t.scope === cmd.scope);
            if (idx >= 0) mockTeams.splice(idx, 1);
            post('agents/reply', { requestId, ok: true, name: cmd.name });
            postAgents();
          } else if (action === 'exportTeam') {
            const team = mockTeams.find(item => item.id === cmd.name && item.scope === cmd.scope);
            post('agents/reply', team ? { requestId, ok: true, template: JSON.stringify({ version: 1, team: { id: team.id, title: team.title, goal: team.goal, mode: team.mode, members: team.members }, roles: team.members.map(name => ({ name })) }, null, 2) } : { requestId, ok: false, error: '팀을 찾을 수 없어요.' });
          } else if (action === 'openTeamExport' || action === 'openRole') {
            post('agents/reply', { requestId, ok: true, name: cmd.name });
          } else if (action === 'mainRole') {
            post('agents/reply', { requestId, ok: true, main: { scope: mainScope(), project: mainFile('project'), user: mainFile('user') } });
          } else if (action === 'saveMain') {
            const scope = cmd.scope === 'user' ? 'user' : 'project'; const current = mainFile(scope);
            if ((current.hash ?? undefined) !== (cmd.expectedHash ?? undefined)) post('agents/reply', { requestId, ok: false, error: '파일이 다른 프로세스에 의해 변경되었어요 (해시 불일치).' });
            else {
              mockMain.files[scope] = { text: (cmd.instructions ?? '').trim(), rev: mockMain.files[scope].rev + 1 }; mockMain.restartRequired = true;
              post('agents/reply', { requestId, ok: true }); postAgents();
            }
          } else if (action === 'setAvatar' && cmd.name === 'main') {
            const image = cmd.avatar && 'image' in cmd.avatar ? cmd.avatar.image ?? null : mockAvatarImages.get('bundled:main') ?? null;
            if (image) mockAvatarImages.set('bundled:main', image); else mockAvatarImages.delete('bundled:main');
            mockMain.avatar = cmd.avatar ? { shape: cmd.avatar.shape, ...(cmd.avatar.color ? { color: cmd.avatar.color } : {}), ...(image ? { imageRev: (mockMain.avatar?.imageRev ?? 0) + 1 } : {}) } : null;
            post('agents/reply', { requestId, ok: true, name: cmd.name }); postAgents();
          } else if (action === 'setAvatar') {
            for (const role of mockRoles.filter(item => item.name === cmd.name && (cmd.scope === 'project' ? item.scope === 'project' : item.scope !== 'project'))) {
              const image = cmd.avatar && 'image' in cmd.avatar ? cmd.avatar.image ?? null : mockAvatarImages.get(`${role.scope}:${role.name}`) ?? null;
              if (image) mockAvatarImages.set(`${role.scope}:${role.name}`, image); else mockAvatarImages.delete(`${role.scope}:${role.name}`);
              role.avatar = cmd.avatar ? { shape: cmd.avatar.shape, ...(cmd.avatar.color ? { color: cmd.avatar.color } : {}), ...(image ? { imageRev: (role.avatar?.imageRev ?? 0) + 1 } : {}) } : null;
            }
            post('agents/reply', { requestId, ok: true, name: cmd.name });
            postAgents();
          } else if (action === 'avatarImages') {
            post('agents/reply', { requestId, ok: true, images: Object.fromEntries((cmd.roles ?? []).flatMap(role => { const image = mockAvatarImages.get(`${role.scope}:${role.name}`); return image ? [[`${role.scope}:${role.name}`, image]] : []; })) });
          } else if (action === 'builtinRole') {
            const role = mockRoles.find(item => item.scope === 'bundled' && item.name === cmd.name);
            post('agents/reply', role ? { requestId, ok: true, role: { description: role.description, instructions: 'Read-only research agent. Report what you found with file references.', model: role.model ?? '@smol', thinkingLevel: role.thinkingLevel, tools: role.tools } } : { requestId, ok: false, error: '역할을 찾을 수 없어요.' });
          } else if (action === 'previewTeamImport') {
            post('agents/reply', { requestId, ok: true, preview: { hash: 'preview-hash', team: { id: 'imported-team', title: '가져온 팀', mode: 'pipeline', members: ['architect', 'tester'], status: 'new' }, roles: [{ name: 'architect', title: '설계자', status: 'existing' }, { name: 'tester', title: '테스터', status: 'new' }], problems: [] } });
          } else if (action === 'importTeam') {
            mockTeams.push({ id: 'imported-team', title: '가져온 팀', goal: '', members: ['architect', 'tester'], mode: 'pipeline', instructions: '', membersOnly: true, scope: cmd.scope || 'project', file: 'C:\workspace\.pomp\teams\imported-team.json', hash: `hash-${Date.now()}`, missing: [] });
            post('agents/reply', { requestId, ok: true, name: 'imported-team' });
            postAgents();
          } else if (action === 'transcript') {
            post('agents/reply', {
              requestId,
              ok: true,
              transcript: {
                agent: cmd.subagentId || 'architect',
                messages: [
                  { role: 'user', text: '기능 아키텍처 및 인터페이스 설계를 진행해 주세요.' },
                  { role: 'assistant', text: '인터페이스 계약과 모듈 분리 설계를 완료했습니다.' },
                ],
                truncated: false,
              },
            });
          }
          break;
        }
        case 'knowledge/command': {
          knowledge.handle(payload as unknown as UiToHost['knowledge/command']);
          break;
        }
        case 'agentHooks/command': {
          handleHooks(payload as unknown as { requestId: string; root?: string; sessionId?: string; action: string; id?: string; revision?: number; rule?: Partial<AgentHookRule>; enabled?: boolean });
          break;
        }
        case 'permission/mode': {
          const { requestId, mode } = payload as unknown as { requestId: string; mode: ApprovalMode };
          if (permissionModeQuery === 'error') {
            post('permission/modeResult', { requestId, ok: false, mode: approvalMode, error: '권한 모드를 바꾸지 못했어요 (mock).' });
            break;
          }
          post('permission/modeResult', { requestId, ok: true, mode });
          void (async () => {
            // The real host restarts the engine to apply the mode, then sends a new hello with it.
            approvalMode = mode;
            post('bridge/state', { state: 'stopping' });
            await wait(200);
            post('bridge/state', { state: 'starting' });
            await wait(200);
            post('bridge/state', { state: 'running' });
            postHello();
          })();
          break;
        }
        case 'computer/set': {
          const { requestId, desktop } = payload as unknown as ComputerSetCommand;
          computerState.pending = true;
          postComputer();
          window.setTimeout(() => {
            computerState.pending = false;
            if (computerQuery === 'error' || computerState.policy === 'off' || !computerState.available) {
              post('computer/reply', { requestId, ok: false, error: '컴퓨터 사용을 변경하지 못했습니다 (mock).' });
            } else {
              computerState.desktop = desktop;
              post('computer/reply', { requestId, ok: true });
            }
            postComputer();
          }, 350);
          break;
        }
        case 'workspace/agentPlan': {
          // The engine plan lives in omp; the preview never pretends a plan edit was applied.
          const requestId = (payload as { requestId?: string } | undefined)?.requestId ?? '';
          setTimeout(() => post('workspace/agentPlanResult', { requestId, ok: false, error: 'Plan editing requires the real host; this preview did not change the plan.' }), 200);
          break;
        }
        case 'ui/log':
        case 'log/open':
        case 'settings/open':
          console.info('[mock host]', type, payload ?? '');
          break;
        default:
          console.info('[mock host] unhandled', type);
      }
    },
    addEventListener(_type: 'message', listener: Listener): void {
      listeners.add(listener);
    },
    removeEventListener(_type: 'message', listener: Listener): void {
      listeners.delete(listener);
    },
  };

  window.chrome = { ...(window.chrome ?? {}), webview: bridge };
}
