import type { McpServer, ResourceScope } from '../../toolsProtocol';

/** One entry of a pasted MCP configuration: a server the editor can save as is, or why it cannot. */
export interface McpImportEntry { name: string; server?: McpServer; error?: 'fields' | 'invalid' | 'exists'; fields?: string[] }

/** The pasted text's size is the bound: a configuration may hold any number of servers. */
export const MCP_IMPORT_MAX_CHARS = 65536;
const known = new Set(['type', 'command', 'args', 'env', 'url', 'headers', 'disabled']);
// eslint-disable-next-line no-control-regex
const control = /[\x00-\x1f\x7f]/;
/** An HTTP header name is a token; a value is single-line text (a tab is allowed) of at most 8192 characters, as the host reads them. */
export const HEADER_NAME = /^[!#$%&'*+\-.^_`|~0-9A-Za-z]{1,128}$/;
// eslint-disable-next-line no-control-regex
export const headerValueValid = (value: string) => value.length <= 8192 && !/[\x00-\x08\x0a-\x1f\x7f]/.test(value);

/**
 * Reads the `mcpServers` (Claude, Cursor) or `servers` (VS Code) object of a pasted configuration, or a bare name → server map.
 * Nothing is dropped silently: a server with fields this editor cannot keep (cwd, timeouts…) is refused, not trimmed. `headers` (http and sse only) are kept.
 * Null when the text is not such a configuration.
 */
export function parseMcpImport(text: string, scope: ResourceScope, existing: McpServer[]): McpImportEntry[] | null {
  if (!text.trim() || text.length > MCP_IMPORT_MAX_CHARS) return null;
  let root: unknown;
  try { root = JSON.parse(text); } catch { return null; }
  if (!isObject(root)) return null;
  const map = isObject(root.mcpServers) ? root.mcpServers : isObject(root.servers) ? root.servers : root;
  const names = Object.keys(map);
  if (!names.length || !names.every(name => isObject(map[name]))) return null;
  return names.map(name => entry(name, map[name] as Record<string, unknown>, scope, existing));
}

function entry(name: string, raw: Record<string, unknown>, scope: ResourceScope, existing: McpServer[]): McpImportEntry {
  const unknown = Object.keys(raw).filter(key => !known.has(key));
  if (unknown.length) return { name, error: 'fields', fields: unknown };
  const trimmed = name.trim();
  if (!trimmed || trimmed.length > 100 || control.test(trimmed)) return { name, error: 'invalid' };
  const declared = raw.type === undefined ? (raw.url !== undefined ? 'http' : 'stdio') : raw.type === 'streamable-http' ? 'http' : raw.type;
  if (declared !== 'stdio' && declared !== 'http' && declared !== 'sse') return { name, error: 'invalid' };
  if (raw.disabled !== undefined && typeof raw.disabled !== 'boolean') return { name, error: 'invalid' };
  const env = raw.env ?? {};
  if (!isObject(env) || !Object.entries(env).every(([key, value]) => /^[A-Za-z_][A-Za-z0-9_]*$/.test(key) && typeof value === 'string')) return { name, error: 'invalid' };
  const headers = raw.headers ?? {};
  const headerNames = isObject(headers) ? Object.keys(headers) : [];
  if (!isObject(headers) || !Object.entries(headers).every(([key, value]) => HEADER_NAME.test(key) && typeof value === 'string' && headerValueValid(value)) ||
    new Set(headerNames.map(key => key.toLowerCase())).size !== headerNames.length || (declared === 'stdio' && raw.headers !== undefined)) return { name, error: 'invalid' };
  let server: McpServer;
  if (declared === 'stdio') {
    const args = raw.args ?? [];
    if (typeof raw.command !== 'string' || !raw.command.trim() || raw.url !== undefined || !Array.isArray(args) || !args.every(arg => typeof arg === 'string')) return { name, error: 'invalid' };
    server = { name: trimmed, scope, type: 'stdio', enabled: raw.disabled !== true, env: env as Record<string, string>, command: raw.command.trim(), args: args as string[] };
  } else {
    let valid = false;
    try { const url = new URL(String(raw.url)); valid = typeof raw.url === 'string' && ['https:', 'http:'].includes(url.protocol) && !url.username && !url.password; } catch { /* invalid */ }
    if (!valid || raw.command !== undefined || raw.args !== undefined) return { name, error: 'invalid' };
    server = { name: trimmed, scope, type: declared, enabled: raw.disabled !== true, env: env as Record<string, string>, url: (raw.url as string).trim(),
      ...(headerNames.length ? { headers: headers as Record<string, string> } : {}) };
  }
  if (existing.some(item => item.scope === scope && item.name === trimmed)) return { name, server, error: 'exists' };
  return { name, server };
}

function isObject(value: unknown): value is Record<string, unknown> {
  return typeof value === 'object' && value !== null && !Array.isArray(value);
}
