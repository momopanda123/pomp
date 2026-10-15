import { useCallback, useEffect, useRef, useState } from 'react';
import { newId } from './format';
import { onMessage } from './protocol';
import { toolsTranslate } from './i18n/toolsStrings';
import { useRefreshOnShow } from './useRefreshOnShow';
import { MASKED_ENV, sendToolsCommand, type CatalogNeed, type CatalogStatePayload, type HooksStatePayload, type McpStatePayload, type RulesStatePayload, type SkillInfo, type SkillsStatePayload, type ToolsCommandFor, type ToolsDomain, type ToolsHostMessage, type ToolsReply, type ToolsStatePayload, type ToolsUiToHost, type WorkflowsStatePayload } from './toolsProtocol';

interface McpTestServer { name: string; scope: string; configuration: string }
export interface ToolsActionState { pending: boolean; error?: string; output?: string; requestId?: string; ok?: boolean; testServer?: McpTestServer; stale?: boolean }
function mcpConfiguration(server: McpStatePayload['servers'][number]): string {
  return server.hash ?? JSON.stringify([server.type, server.command, server.args, server.url, server.enabled,
    Object.entries(server.env ?? {}).sort(([a], [b]) => a.localeCompare(b)), Object.entries(server.headers ?? {}).sort(([a], [b]) => a.localeCompare(b))]);
}
function matchesTest(server: McpStatePayload['servers'][number], test: McpTestServer): boolean { return server.name === test.name && server.scope === test.scope; }
interface Request { domain: ToolsDomain; action: string; timer: number; resolve: (reply: ToolsReply) => void; abort: (reply: ToolsReply) => void }
const emptyTools = (): ToolsStatePayload => ({ tools: [], pending: false, available: false });
const emptySkills = (): SkillsStatePayload => ({ skills: [], diagnostics: [] });
const emptyMcp = (): McpStatePayload => ({ servers: [], restartRequired: false });
const emptyCatalog = (): CatalogStatePayload => ({ entries: [] });
/** A host list of scopes or words, reduced to what the page can show: strings only, no repeats. */
const stringList = (value: unknown): string[] => Array.isArray(value) ? [...new Set(value.filter((item): item is string => typeof item === 'string'))] : [];
const scopeList = (value: unknown): Array<'project' | 'user'> => stringList(value).filter((scope): scope is 'project' | 'user' => scope === 'project' || scope === 'user');
const emptyRules = (): RulesStatePayload => ({ rules: [], diagnostics: [] });
const emptyWorkflows = (): WorkflowsStatePayload => ({ workflows: [], diagnostics: [] });
const emptyHooks = (): HooksStatePayload => ({ hooks: [], diagnostics: [], policy: 'none' });
/** The host domains a sub-view lists besides the ones the Tools tab always keeps fresh: a Tools view id, or `hooks` (the hook definitions of Agents › Rules). */
const viewDomains = (view: string): ToolsDomain[] => view === 'conversation' ? ['tools', 'skills'] : view === 'skills' ? ['skills'] : view === 'mcp' ? ['mcp', 'catalog', 'tools'] : view === 'rules' ? ['rules'] : view === 'workflows' ? ['workflows'] : view === 'hooks' ? ['hooks'] : [];
/** Every domain this hook handles (the default of `domains`). */
export const allToolsDomains: readonly ToolsDomain[] = ['tools', 'skills', 'mcp', 'catalog', 'rules', 'workflows', 'hooks'];
/**
 * An instance handles the state and replies of the domains it owns, and refreshes and accepts commands for those only. The Tools tab owns the first
 * list, the project hook definitions (Agents › Rules) the second, so every host message has exactly one handler and nothing is tracked twice.
 */
export const toolsTabDomains: readonly ToolsDomain[] = ['tools', 'skills', 'mcp', 'catalog', 'rules', 'workflows'];
export const hookDefinitionDomains: readonly ToolsDomain[] = ['hooks'];

/** In-memory snapshots and correlated replies. One action per domain; never optimistically report success. */
export function useToolsState({ locale, connected, busy, active, workspaceKey, sessionId, disabled = false, view = 'conversation', domains = allToolsDomains }: {
  locale: string; connected: boolean; busy: boolean; active: boolean; workspaceKey: string; sessionId?: string; disabled?: boolean; view?: string; domains?: readonly ToolsDomain[];
}) {
  const [tools, setTools] = useState(emptyTools);
  const [skills, setSkills] = useState(emptySkills);
  const [mcp, setMcp] = useState(emptyMcp);
  const [catalog, setCatalog] = useState(emptyCatalog);
  const [rules, setRules] = useState(emptyRules);
  const [workflows, setWorkflows] = useState(emptyWorkflows);
  const [hooks, setHooks] = useState(emptyHooks);
  const [actions, setActions] = useState<Record<string, ToolsActionState>>({});
  // Draft choices belong to a conversation, not a mounted view or host snapshot.
  const draftKey = JSON.stringify([workspaceKey, sessionId ?? '']);
  const [toolDrafts, setToolDrafts] = useState<Record<string, Record<string, boolean>>>({});
  const snapshots = useRef({ tools, mcp }); snapshots.current = { tools, mcp };
  const scope = useRef(draftKey); scope.current = draftKey;
  const requests = useRef(new Map<string, Request>());
  const toggleTools = useCallback((names: string[], checked: boolean) => {
    const key = scope.current; const available = snapshots.current.tools.tools;
    setToolDrafts(previous => {
      const next: Record<string, boolean> = Object.assign(Object.create(null), previous[key]);
      for (const name of names) {
        const tool = available.find(item => item.name === name);
        if (!tool) continue;
        if (checked === tool.active) delete next[name]; else next[name] = checked;
      }
      return { ...previous, [key]: next };
    });
  }, []);
  const settings = useRef({ locale, connected, busy, disabled, active }); settings.current = { locale, connected, busy, disabled, active };
  const owned = useRef<ReadonlySet<string>>(new Set()); owned.current = new Set(domains);

  const command = useCallback(<D extends ToolsDomain,>(domain: D, payload: ToolsCommandFor<D>): Promise<ToolsReply> => {
    const current = settings.current;
    const key = `${domain}.${payload.action}`;
    const t = toolsTranslate(current.locale);
    const inFlight = [...requests.current.values()].some(request => request.domain === domain);
    // One text per refusal. `disabled` is a new conversation starting or another dialog open (the shell folds both into it).
    const refusal = !current.connected ? 'tools.hostUnavailable' : !current.active ? 'tools.inactive'
      : current.disabled && payload.action !== 'refresh' ? 'tools.sessionPending'
      : current.busy && (domain === 'tools' || payload.action !== 'refresh') ? 'tools.busy' : inFlight ? 'tools.inFlight' : undefined;
    if (refusal) {
      const error = t(refusal);
      // The domain's request in flight owns its pending state (keyed by action): a refused request never overwrites it.
      if (!inFlight) setActions(previous => ({ ...previous, [key]: { pending: false, ok: false, error } }));
      return Promise.resolve({ requestId: '', ok: false, error });
    }
    const requestId = newId(); const requestScope = scope.current;
    const mcpPayload = domain === 'mcp' ? payload as ToolsCommandFor<'mcp'> : undefined;
    const tested = mcpPayload?.action === 'test' ? snapshots.current.mcp.servers.find(server => server.name === mcpPayload.name && server.scope === mcpPayload.scope) : undefined;
    const testServer = tested ? { name: tested.name, scope: tested.scope, configuration: mcpConfiguration(tested) } : undefined;
    setActions(previous => {
      const next: Record<string, ToolsActionState> = { ...previous, [key]: { requestId, pending: true, testServer } };
      const test = previous['mcp.test'];
      if (mcpPayload && test?.testServer && (mcpPayload.action === 'restart' ||
        (['save', 'enable', 'disable'].includes(mcpPayload.action) && test.testServer.name === mcpPayload.name && test.testServer.scope === mcpPayload.scope))) {
        next['mcp.test'] = { ...test, stale: true };
      }
      return next;
    });
    return new Promise(resolve => {
      const finish = (reply: ToolsReply) => {
        const request = requests.current.get(requestId);
        if (!request) return;
        window.clearTimeout(request.timer); requests.current.delete(requestId);
        if (reply.ok && domain === 'tools' && ['set', 'reset'].includes(payload.action)) {
          setToolDrafts(previous => ({ ...previous, [requestScope]: {} }));
        }
        setActions(previous => {
          const next = { ...previous, [key]: { ...reply, pending: false, testServer } };
          if (testServer) {
            const server = snapshots.current.mcp.servers.find(item => matchesTest(item, testServer));
            if (!server) delete next[key];
            else next[key] = { ...next[key], stale: mcpConfiguration(server) !== testServer.configuration };
          }
          const test = next['mcp.test'];
          if (reply.ok && mcpPayload?.action === 'delete' && test?.testServer && test.testServer.name === mcpPayload.name && test.testServer.scope === mcpPayload.scope) delete next['mcp.test'];
          return next;
        });
        resolve(reply);
      };
      const timer = window.setTimeout(() => finish({ requestId, ok: false, error: toolsTranslate(settings.current.locale)('tools.noReply') }), domain === 'mcp' && ['test', 'save'].includes(payload.action) ? 60000 : 15000);
      requests.current.set(requestId, { domain, action: payload.action, timer, resolve: finish, abort: resolve });
      const envelope = { ...payload, requestId } as ToolsUiToHost[`${D}/command`];
      if (!sendToolsCommand(domain, envelope)) finish({ requestId, ok: false, error: t('tools.hostUnavailable') });
    });
  }, []);

  useEffect(() => {
    setTools(emptyTools()); setSkills(emptySkills()); setMcp(emptyMcp()); setCatalog(emptyCatalog()); setRules(emptyRules()); setWorkflows(emptyWorkflows()); setHooks(emptyHooks()); setActions({});
    // The shell extends protocol.ts's typed maps and HOST_TYPES with the types in toolsProtocol.ts.
    const off = onMessage(raw => {
      const message = raw as unknown as ToolsHostMessage;
      if (!owned.current.has(message.type.split('/')[0])) return;
      switch (message.type) {
        case 'tools/state': {
          const value = message.payload;
          if (!Array.isArray(value.tools) || (value.sessionId && sessionId && value.sessionId !== sessionId)) return;
          const groups = new Set(['files', 'edit', 'exec', 'web', 'agents', 'host', 'mcp', 'other']);
          const snapshot = { ...value, pending: value.pending === true, available: value.available === true,
            tools: value.tools.filter(tool => typeof tool.name === 'string' && groups.has(tool.group)).map(tool => ({ ...tool, active: tool.active === true, defaultActive: tool.defaultActive === true })) };
          snapshots.current.tools = snapshot; setTools(snapshot);
          const names = new Set(snapshot.tools.map(tool => tool.name));
          setToolDrafts(previous => ({ ...previous, [draftKey]: Object.fromEntries(Object.entries(previous[draftKey] ?? {}).filter(([name]) => names.has(name))) }));
          break;
        }
        case 'skills/state':
          // Only a boolean false switches a row off (absent = on) and only a boolean true marks an update; anything else claims nothing.
          if (Array.isArray(message.payload.skills)) setSkills({ skills: message.payload.skills.filter(skill => typeof skill.name === 'string' && ['project', 'user', 'omp', 'installed', 'pomp'].includes(skill.source))
            .map((skill: SkillInfo) => ({ ...skill, unreadable: skill.unreadable === true, enabled: skill.enabled !== false, catalogUpdate: skill.catalogUpdate === true,
              // Rows built into POMP carry a default state and a category; anything but a boolean or a non-empty string claims neither.
              defaultEnabled: typeof skill.defaultEnabled === 'boolean' ? skill.defaultEnabled : undefined,
              category: typeof skill.category === 'string' && skill.category ? skill.category : undefined })),
            diagnostics: Array.isArray(message.payload.diagnostics) ? message.payload.diagnostics : [] });
          break;
        case 'mcp/state':
          if (Array.isArray(message.payload.servers)) {
            const snapshot = { ...message.payload, servers: message.payload.servers.map(server => ({ ...server,
              // Even a defective/older host may not cause an existing secret to appear in the DOM.
              env: server.env ? Object.fromEntries(Object.keys(server.env).map(name => [name, MASKED_ENV])) : undefined,
              headers: server.headers ? Object.fromEntries(Object.keys(server.headers).map(name => [name, MASKED_ENV])) : undefined })) };
            snapshots.current.mcp = snapshot; setMcp(snapshot);
            setActions(previous => {
              const test = previous['mcp.test']; if (!test?.testServer) return previous;
              const server = snapshot.servers.find(item => matchesTest(item, test.testServer!));
              const next = { ...previous };
              if (!server) delete next['mcp.test'];
              else if (mcpConfiguration(server) !== test.testServer.configuration) next['mcp.test'] = { ...test, stale: true };
              return next;
            });
          }
          break;
        case 'catalog/state':
          if (Array.isArray(message.payload.entries)) setCatalog({ message: message.payload.message, entries: message.payload.entries
            .filter(entry => typeof entry.id === 'string' && typeof entry.title === 'string' && (entry.kind === 'skill' || entry.kind === 'mcp'))
            .map(entry => ({ ...entry, description: typeof entry.description === 'string' ? entry.description : '', category: typeof entry.category === 'string' ? entry.category : '', version: typeof entry.version === 'string' ? entry.version : '',
              scopes: scopeList(entry.scopes), installed: scopeList(entry.installed), requires: stringList(entry.requires), needs: stringList(entry.needs) as CatalogNeed[],
              inputs: Array.isArray(entry.inputs) ? entry.inputs.filter(input => typeof input.key === 'string' && typeof input.label === 'string').map(input => ({ key: input.key, label: input.label, secret: input.secret === true, required: input.required === true })) : [] })) });
          break;
        case 'rules/state':
          if (Array.isArray(message.payload.rules)) setRules({ rules: message.payload.rules.filter(rule => typeof rule.name === 'string' && typeof rule.file === 'string' && ['project', 'user'].includes(rule.scope))
            .map(rule => ({ ...rule, alwaysApply: rule.alwaysApply === true, globs: Array.isArray(rule.globs) ? rule.globs.filter(glob => typeof glob === 'string').slice(0, 32) : [], unreadable: rule.unreadable === true })),
            diagnostics: Array.isArray(message.payload.diagnostics) ? message.payload.diagnostics : [] });
          break;
        case 'workflows/state':
          if (Array.isArray(message.payload.workflows)) setWorkflows({ workflows: message.payload.workflows.filter(workflow => typeof workflow.name === 'string' && typeof workflow.file === 'string' && ['project', 'user'].includes(workflow.scope))
            .map(workflow => ({ ...workflow, description: typeof workflow.description === 'string' ? workflow.description : '', task: typeof workflow.task === 'string' ? workflow.task : '', steps: Array.isArray(workflow.steps) ? workflow.steps.filter(step => typeof step === 'string').slice(0, 32) : [],
              stepCount: typeof workflow.stepCount === 'number' ? workflow.stepCount : Array.isArray(workflow.steps) ? workflow.steps.length : 0, summary: workflow.summary === true, unreadable: workflow.unreadable === true })),
            diagnostics: Array.isArray(message.payload.diagnostics) ? message.payload.diagnostics : [] });
          break;
        case 'hooks/state':
          if (Array.isArray(message.payload.hooks)) setHooks({ hooks: message.payload.hooks.filter(hook => typeof hook.id === 'string' && typeof hook.name === 'string' && typeof hook.file === 'string' && typeof hook.hash === 'string')
            .map(hook => ({ ...hook, description: typeof hook.description === 'string' ? hook.description : '', event: hook.event === 'onFileSave' || hook.event === 'onBuild' ? hook.event : '', glob: typeof hook.glob === 'string' ? hook.glob : '', timeoutSeconds: typeof hook.timeoutSeconds === 'number' ? hook.timeoutSeconds : 120, enabled: hook.enabled === true, script: typeof hook.script === 'string' ? hook.script : '', unreadable: hook.unreadable === true })),
            diagnostics: Array.isArray(message.payload.diagnostics) ? message.payload.diagnostics : [],
            policy: message.payload.policy === 'valid' || message.payload.policy === 'invalid' ? message.payload.policy : 'none' });
          break;
        case 'tools/reply': case 'skills/reply': case 'mcp/reply': case 'catalog/reply': case 'rules/reply': case 'workflows/reply': case 'hooks/reply': {
          const reply = message.payload;
          const request = requests.current.get(reply.requestId);
          if (!request || message.type !== `${request.domain}/reply` || typeof reply.ok !== 'boolean') return;
          request.resolve({ ...reply, error: reply.ok ? undefined : reply.error || toolsTranslate(settings.current.locale)('tools.failed') });
          break;
        }
      }
    });
    return () => {
      off();
      // Resolve callers without reporting stale completion after a session switch or unmount.
      for (const [requestId, request] of requests.current) {
        window.clearTimeout(request.timer);
        // Remove before resolving: completion callbacks must not update this store.
        requests.current.delete(requestId);
        request.abort({ requestId, ok: false, error: toolsTranslate(settings.current.locale)('tools.noReply') });
      }
    };
  }, [workspaceKey, sessionId, connected, command]);

  // Ready snapshots arrive while inactive; each show refreshes once, with tools deferred until inference is idle.
  // The instructions (rules, workflows) and hook definition lists refresh only when selected or when the surface is shown with one of them selected
  // (their files can change in VS meanwhile); an InPageDialog or another chip switches back to the list and retries.
  const deferredTools = useRef(false);
  const ownedDomains = (listed: ToolsDomain[]) => listed.filter(domain => owned.current.has(domain));
  useRefreshOnShow(active && connected, () => {
    // Every chip's count comes from these lists, so each one is fresh when the tab shows, not only the open view's.
    const listed = ownedDomains([...new Set<ToolsDomain>(['tools', 'skills', 'mcp', 'rules', 'workflows', ...viewDomains(view)])]);
    deferredTools.current = busy && listed.includes('tools');
    for (const domain of listed) {
      if (domain !== 'tools' || !busy) void command(domain, { action: 'refresh' });
    }
  });
  useEffect(() => {
    if (!active || !connected) { deferredTools.current = false; return; }
    if (!busy && deferredTools.current) { deferredTools.current = false; void command('tools', { action: 'refresh' }); }
  }, [active, connected, busy, command]);

  const previousView = useRef(view);
  useEffect(() => {
    const changed = previousView.current !== view;
    previousView.current = view;
    if (!changed || !active || !connected) return;
    for (const domain of ownedDomains(viewDomains(view))) {
      if (domain === 'tools' && busy) { deferredTools.current = true; continue; }
      void command(domain, { action: 'refresh' });
    }
  }, [view, active, connected, busy, command]);

  const pending = (domain: ToolsDomain) => Object.entries(actions).some(([key, state]) => key.startsWith(`${domain}.`) && state.pending);
  const feedback = (domain: ToolsDomain, action: string): ToolsActionState => actions[`${domain}.${action}`] ?? { pending: false };
  const errors = (domain: ToolsDomain) => Object.entries(actions).filter(([key, state]) => key.startsWith(`${domain}.`) && state.error).map(([, state]) => state.error!);
  const draft = toolDrafts[draftKey] ?? {};
  const selectedTools = new Set(tools.tools.filter(tool => Object.hasOwn(draft, tool.name) ? draft[tool.name] : tool.active).map(tool => tool.name));
  return { tools, skills, mcp, catalog, rules, workflows, hooks, actions, command, pending, feedback, errors, selectedTools, toggleTools };
}
export type ToolsController = ReturnType<typeof useToolsState>;
