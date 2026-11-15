import type { SessionInfoPayload } from './protocol';

export type BackendId = 'omp' | 'antigravity-acp';
export interface SessionCapabilities {
  nativeOmp: boolean; queue: boolean; steer: boolean; stop: boolean; images: boolean; editorContext: boolean;
  plan: { read: boolean; edit: boolean; actions: boolean };
  history: { list: boolean; preview: boolean; resume: boolean; rename: boolean; delete: boolean; compact: boolean };
}
export interface BackendState {
  active: BackendId;
  antigravity: { installed: boolean; canStart: boolean; executablePath?: string; workspaceRoot?: string; ompToolsAvailable: boolean; message?: string };
}
export interface AcpConfigValue { value: string; name: string; description?: string }
export interface AcpConfigGroup { group: string; name: string; options: AcpConfigValue[] }
export interface AcpConfigOption {
  id: string; name: string; description?: string; category?: string; type: string; currentValue: string | boolean;
  options?: (AcpConfigValue | AcpConfigGroup)[];
}
export interface AcpState {
  sessionId?: string; connected: boolean; authRequired: boolean; error?: string;
  authMethods?: { id: string; name: string; description?: string }[];
  configOptions: AcpConfigOption[];
  permissions: { requestId: string; toolCall: Record<string, unknown>; options: { optionId: string; name: string; kind: string }[] }[];
  commands?: { name: string; description: string; input?: { hint: string } }[];
  ompTools?: { serverName: string; command: string; args: string[]; state: 'configured' };
}
export interface AcpCommand {
  requestId: string; sessionId?: string; action: 'set-config' | 'permission' | 'authenticate';
  configId?: string; value?: string | boolean; permissionRequestId?: string; optionId?: string; methodId?: string;
}
export interface AcpReply { requestId: string; ok: boolean; error?: string }
const nativeCapabilities: SessionCapabilities = {
  nativeOmp: true, queue: true, steer: true, stop: true, images: true, editorContext: true,
  plan: { read: true, edit: true, actions: true }, history: { list: true, preview: true, resume: true, rename: true, delete: true, compact: true },
};
export function backendId(session: SessionInfoPayload | null, state?: BackendState | null): BackendId {
  return state?.active ?? session?.backend ?? 'omp';
}
export function capabilities(session: SessionInfoPayload | null, backend: BackendId): SessionCapabilities {
  if (backend === 'omp') return session?.capabilities ?? nativeCapabilities;
  const source = session?.backend === backend ? session.capabilities : undefined;
  return {
    nativeOmp: false, queue: source?.queue === true, steer: source?.steer === true, stop: source?.stop === true,
    images: source?.images === true, editorContext: source?.editorContext === true,
    plan: { read: source?.plan?.read === true, edit: source?.plan?.edit === true, actions: source?.plan?.actions === true },
    history: { list: source?.history?.list === true, preview: source?.history?.preview === true, resume: source?.history?.resume === true,
      rename: source?.history?.rename === true, delete: source?.history?.delete === true, compact: source?.history?.compact === true },
  };
}
/** Page commands that do not depend on native OMP. Server commands retain their own names. */
export function backendCommandAllowed(name: string, caps: SessionCapabilities): boolean {
  if (caps.nativeOmp) return true;
  if (['new', 'accounts', 'work', 'help'].includes(name)) return true;
  if (name === 'sessions') return caps.history.list;
  if (name === 'rename' || name === 'compact') return caps.history[name];
  return name === 'steer' && caps.steer;
}

const record = (value: unknown): Record<string, unknown> => value !== null && typeof value === 'object' && !Array.isArray(value) ? value as Record<string, unknown> : {};
const text = (value: unknown): string | undefined => typeof value === 'string' ? value : undefined;
const array = (value: unknown): unknown[] => Array.isArray(value) ? value : [];

export function normalizeBackendState(payload: unknown): BackendState {
  const raw = record(payload), agy = record(raw.antigravity);
  return { active: raw.active === 'antigravity-acp' ? 'antigravity-acp' : 'omp',
    antigravity: { installed: agy.installed === true, canStart: agy.canStart === true, ompToolsAvailable: agy.ompToolsAvailable === true,
      executablePath: text(agy.executablePath), workspaceRoot: text(agy.workspaceRoot), message: text(agy.message) } };
}

export function normalizeAcpState(payload: unknown): AcpState {
  const raw = record(payload);
  const values = (source: unknown): AcpConfigValue[] => array(source).flatMap(item => {
    const value = record(item);
    return typeof value.value === 'string' && typeof value.name === 'string' ? [{ value: value.value, name: value.name, description: text(value.description) }] : [];
  });
  const configOptions: AcpConfigOption[] = array(raw.configOptions).flatMap(item => {
    const option = record(item);
    if (typeof option.id !== 'string' || typeof option.name !== 'string' || typeof option.type !== 'string' ||
      typeof option.currentValue !== 'string' && typeof option.currentValue !== 'boolean') return [];
    const options: (AcpConfigValue | AcpConfigGroup)[] = [];
    for (const item of array(option.options)) {
      const group = record(item);
      if (typeof group.group === 'string' && typeof group.name === 'string' && Array.isArray(group.options))
        options.push({ group: group.group, name: group.name, options: values(group.options) });
      else options.push(...values([item]));
    }
    return [{ id: option.id, name: option.name, type: option.type, currentValue: option.currentValue,
      category: text(option.category), description: text(option.description), ...(Array.isArray(option.options) ? { options } : {}) }];
  });
  const permissions: AcpState['permissions'] = array(raw.permissions).flatMap(item => {
    const permission = record(item);
    if (typeof permission.requestId !== 'string') return [];
    return [{ requestId: permission.requestId, toolCall: record(permission.toolCall), options: array(permission.options).flatMap(item => {
      const option = record(item);
      return typeof option.optionId === 'string' && typeof option.name === 'string' && typeof option.kind === 'string'
        ? [{ optionId: option.optionId, name: option.name, kind: option.kind }] : [];
    }) }];
  });
  const tools = record(raw.ompTools);
  return { sessionId: text(raw.sessionId), connected: raw.connected === true, authRequired: raw.authRequired === true, error: text(raw.error),
    configOptions, permissions,
    authMethods: array(raw.authMethods).flatMap(item => { const method = record(item); return typeof method.id === 'string' && typeof method.name === 'string' ? [{ id: method.id, name: method.name, description: text(method.description) }] : []; }),
    commands: array(raw.commands).flatMap(item => { const command = record(item); const hint = text(record(command.input).hint);
      return typeof command.name === 'string' && typeof command.description === 'string' ? [{ name: command.name, description: command.description, ...(hint ? { input: { hint } } : {}) }] : []; }),
    ...(tools.state === 'configured' && typeof tools.serverName === 'string' && typeof tools.command === 'string' && Array.isArray(tools.args) && tools.args.every(arg => typeof arg === 'string')
      ? { ompTools: { serverName: tools.serverName, command: tools.command, args: tools.args as string[], state: 'configured' as const } } : {}),
  };
}
