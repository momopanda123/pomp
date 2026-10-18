import type { ExtensionAPI } from '@oh-my-pi/pi-coding-agent';
import * as fs from 'node:fs/promises';
import * as path from 'node:path';
import * as os from 'node:os';
import { createHash } from 'node:crypto';
import { isPompHost } from '../lib/vs-state.ts';

const RESULT = 'pomp-advisor-result:';
const MAX_BYTES = 256 * 1024;
// A transcript stays far below the 900000-character notice the host accepts, even after JSON escaping and base64.
const HISTORY_BYTES = 256 * 1024;
// The notes ride in the same notice as the transcript.
const NOTES_BYTES = 160 * 1024;
export const TOOLS = new Set(['read', 'bash', 'edit', 'ast_grep', 'ast_edit', 'ask', 'debug', 'ida', 'eval', 'github', 'glob', 'grep', 'find', 'lsp', 'checkpoint', 'rewind', 'context_notes', 'new_context', 'security_scan', 'task', 'wait', 'todo', 'web_search', 'write', 'memory_edit', 'retain', 'recall', 'reflect', 'learn', 'manage_skill']);
const BACKLOG = new Set(['off', '1', '3', '5', 'strict']);
export interface AdvisorEntry { name: string; enabled?: boolean; model?: string; reviewMode?: 'turn' | 'agent-end'; reviewInterval?: number; syncBacklog?: string; tools?: string[]; instructions?: string; maxNotesPerUpdate?: number }
export interface AdvisorDocument { advisors: AdvisorEntry[]; instructions?: string; maxNotesPerUpdate?: number }
interface FileRow { scope: 'project' | 'user'; path: string; depth: number }
export interface Engine {
  mainId: string;
  session: any;
  agentDir: string;
  projectDir: string;
  cwd: string;
  vcsRoot?: string;
  yaml: { parse(text: string): unknown };
}
export const slug = (name: string): string => name.toLowerCase().replace(/[^a-z0-9]+/g, '-').replace(/^-+|-+$/g, '') || 'advisor';
const hash = (content: Uint8Array): string => createHash('sha256').update(content).digest('hex');
const same = (a: string, b: string): boolean => process.platform === 'win32' ? a.toLowerCase() === b.toLowerCase() : a === b;
const optional = (value: any, key: string, check: (v: any) => boolean): boolean => value[key] === undefined || check(value[key]);
const budget = (value: any): number | undefined => typeof value === 'number' && Number.isFinite(value) && value >= 1 ? Math.trunc(value) : undefined;
const strings = (value: any): boolean => Array.isArray(value) && value.every(v => typeof v === 'string');

/** Only imports retained by the pinned engine's virtual-module registry (18.8.6, 18.10.3, 18.10.4, 18.12.1) are used. */
export async function loadEngine(): Promise<Engine | null> {
  try {
    const registry: any = await import('@oh-my-pi/pi-coding-agent/registry/agent-registry');
    const utils: any = await import('@oh-my-pi/pi-utils');
    const vcs: any = await import('@oh-my-pi/pi-natives/vcs');
    const session = registry?.AgentRegistry?.global?.()?.get?.(registry.MAIN_AGENT_ID)?.session;
    const yaml = (globalThis as any).Bun?.YAML;
    if (typeof registry.MAIN_AGENT_ID !== 'string' || typeof utils.getAgentDir !== 'function' || typeof vcs.repo !== 'function'
      || !session || typeof session.sessionManager?.getCwd !== 'function' || typeof session.sessionManager?.getSessionId !== 'function'
      || typeof session.setAdvisorEnabled !== 'function' || typeof session.isAdvisorEnabled !== 'function'
      || typeof session.getAdvisorStatusOverview !== 'function' || typeof session.getAdvisorStats !== 'function' || typeof yaml?.parse !== 'function') return null;
    const cwd = path.resolve(session.sessionManager.getCwd());
    const agent = utils.getAgentDir();
    if (typeof agent !== 'string' || !agent) return null;
    let vcsRoot: string | undefined;
    try { const repo = vcs.repo(cwd); if (repo && typeof repo.root === 'function') vcsRoot = path.resolve(repo.root()); }
    catch { /* The native selector likewise falls back to cwd. */ }
    return { mainId: registry.MAIN_AGENT_ID, session, cwd, agentDir: path.resolve(agent), projectDir: vcsRoot ?? cwd, vcsRoot, yaml };
  } catch { return null; }
}

/** Reject redirected paths, junctions/symlinks (including dangling ones), and multiply-linked files. */
export async function directPath(file: string): Promise<void> {
  const full = path.resolve(file);
  if (full.includes('\0') || (process.platform === 'win32' && (full.startsWith('\\\\') || full.slice(2).includes(':')))) throw new Error('unsafe-path');
  // The drive root is never a link, and the engine runtime's realpath('C:\') answers with the working directory: stop below it.
  for (let current = full; path.dirname(current) !== current; current = path.dirname(current)) {
    try {
      const stat = await fs.lstat(current);
      if (stat.isSymbolicLink() || (stat.isFile() && stat.nlink > 1) || !same(path.resolve(await fs.realpath(current)), current)) throw new Error('unsafe-path');
    } catch (error: any) { if (error?.code !== 'ENOENT') throw error; }
  }
}
async function read(file: string): Promise<{ exists: boolean; hash: string; text: string }> {
  await directPath(file);
  let handle;
  try {
    handle = await fs.open(file, 'r');
    const stat = await handle.stat();
    if (!stat.isFile() || stat.size > MAX_BYTES || stat.nlink > 1) throw new Error('file-too-large-or-unsafe');
    const bytes = Buffer.alloc(MAX_BYTES + 1);
    let size = 0;
    while (size < bytes.length) { const part = await handle.read(bytes, size, bytes.length - size, size); if (!part.bytesRead) break; size += part.bytesRead; }
    if (size > MAX_BYTES) throw new Error('file-too-large-or-unsafe');
    await directPath(file);
    const content = bytes.subarray(0, size);
    return { exists: true, hash: hash(content), text: content.toString('utf8') };
  } catch (error: any) { if (error?.code === 'ENOENT') return { exists: false, hash: hash(Buffer.alloc(0)), text: '' }; throw error; }
  finally { await handle?.close(); }
}
export async function selectedPath(scope: string, engine: Engine): Promise<string> {
  const directory = scope === 'user' ? engine.agentDir : engine.projectDir;
  const yml = path.join(directory, 'WATCHDOG.yml'); const yaml = path.join(directory, 'WATCHDOG.yaml');
  await directPath(yml); await directPath(yaml);
  const exists = async (file: string) => { try { await fs.lstat(file); return true; } catch (error: any) { if (error?.code === 'ENOENT') return false; throw error; } };
  return !await exists(yml) && await exists(yaml) ? yaml : yml;
}

/** Reproduces the native recognized schema; diagnostics never contain YAML/instructions or parser exception text. */
export function parseDocument(text: string, yaml: Engine['yaml']): { document: AdvisorDocument; warnings: string[] } {
  const warnings: string[] = [];
  const document: AdvisorDocument = { advisors: [] };
  let source: any;
  try { source = yaml.parse(text); } catch { return { document, warnings: ['Invalid YAML.'] }; }
  if (!source || typeof source !== 'object' || Array.isArray(source)) return { document, warnings: ['Expected a YAML mapping.'] };
  if (source.instructions !== undefined && typeof source.instructions !== 'string') warnings.push('Shared instructions must be a string.');
  if (typeof source.instructions === 'string' && source.instructions.trim()) document.instructions = source.instructions;
  if (budget(source.maxNotesPerUpdate) !== undefined) document.maxNotesPerUpdate = budget(source.maxNotesPerUpdate);
  if (source.advisors !== undefined && !Array.isArray(source.advisors)) warnings.push('Advisors must be a list.');
  for (const [index, entry] of (Array.isArray(source.advisors) ? source.advisors : []).entries()) {
    const valid = entry && typeof entry === 'object' && !Array.isArray(entry) && typeof entry.name === 'string'
      && optional(entry, 'model', v => typeof v === 'string') && optional(entry, 'instructions', v => typeof v === 'string')
      && optional(entry, 'tools', strings) && optional(entry, 'enabled', v => typeof v === 'boolean')
      && optional(entry, 'reviewMode', v => v === 'turn' || v === 'agent-end')
      && optional(entry, 'reviewInterval', v => Number.isSafeInteger(v) && v >= 1)
      && optional(entry, 'syncBacklog', v => BACKLOG.has(v) || v === 1 || v === 3 || v === 5)
      && optional(entry, 'maxNotesPerUpdate', v => typeof v === 'number' && Number.isFinite(v));
    if (!valid) { warnings.push(`Advisor #${index + 1} dropped: invalid fields.`); continue; }
    const row: AdvisorEntry = { name: entry.name };
    if (entry.model?.trim()) row.model = entry.model;
    if (entry.instructions?.trim()) row.instructions = entry.instructions;
    for (const key of ['tools', 'enabled', 'reviewMode', 'reviewInterval'] as const) if (entry[key] !== undefined) (row as any)[key] = entry[key];
    if (entry.syncBacklog !== undefined) row.syncBacklog = String(entry.syncBacklog);
    if (budget(entry.maxNotesPerUpdate) !== undefined) row.maxNotesPerUpdate = budget(entry.maxNotesPerUpdate);
    document.advisors.push(row);
  }
  return { document, warnings };
}

export function validateDocument(value: any): AdvisorDocument {
  if (!value || typeof value !== 'object' || !Array.isArray(value.advisors) || Buffer.byteLength(JSON.stringify(value)) > MAX_BYTES) throw new Error('invalid-document');
  const validBudget = (v: any) => Number.isInteger(v) && v >= 1 && v <= 32;
  const validInstructions = (v: any) => typeof v === 'string' && v.length <= 16000;
  if (!optional(value, 'instructions', validInstructions) || !optional(value, 'maxNotesPerUpdate', validBudget)) throw new Error('invalid-document');
  const names = new Set<string>();
  const doc: AdvisorDocument = { advisors: [] };
  if (value.instructions?.trim()) doc.instructions = value.instructions;
  if (value.maxNotesPerUpdate !== undefined) doc.maxNotesPerUpdate = value.maxNotesPerUpdate;
  for (const entry of value.advisors) {
    if (!entry || typeof entry.name !== 'string' || !entry.name.trim() || entry.name.trim().length > 64 || /[\x00-\x1f\x7f]/.test(entry.name)
      || !optional(entry, 'enabled', v => typeof v === 'boolean') || !optional(entry, 'model', v => typeof v === 'string' && v.length <= 200 && !/[\x00-\x1f\x7f]/.test(v))
      || !optional(entry, 'reviewMode', v => v === 'turn' || v === 'agent-end') || !optional(entry, 'reviewInterval', v => Number.isInteger(v) && v >= 1 && v <= 1000000)
      || !optional(entry, 'syncBacklog', v => BACKLOG.has(v)) || !optional(entry, 'instructions', validInstructions) || !optional(entry, 'maxNotesPerUpdate', validBudget)
      || !optional(entry, 'tools', v => strings(v) && v.length <= TOOLS.size && v.every((name: string) => TOOLS.has(name)))) throw new Error('invalid-document');
    const key = slug(entry.name.trim()); if (names.has(key)) throw new Error('duplicate-name'); names.add(key);
    const row: AdvisorEntry = { name: entry.name.trim() };
    for (const key of ['enabled', 'model', 'reviewMode', 'reviewInterval', 'syncBacklog', 'tools', 'instructions', 'maxNotesPerUpdate'] as const)
      if (entry[key] !== undefined) (row as any)[key] = entry[key];
    doc.advisors.push(row);
  }
  return doc;
}

/** Fixed-schema native-compatible emitter. JSON quoted scalars are valid YAML; unknown keys/comments are intentionally omitted. */
export function serializeDocument(doc: AdvisorDocument): string {
  const lines: string[] = [];
  const text = (indent: string, key: string, value: string) => {
    if (!value.includes('\n') || value.split('\n').some(line => /^[ \t]/.test(line))) { lines.push(`${indent}${key}: ${JSON.stringify(value)}`); return; }
    const valueLf = value.replaceAll('\r\n', '\n'); const trailing = /\n*$/.exec(valueLf)![0].length;
    lines.push(`${indent}${key}: ${trailing === 0 ? '|2-' : trailing === 1 ? '|2' : '|2+'}`);
    const body = trailing ? valueLf.slice(0, -trailing) : valueLf;
    for (const line of body.split('\n')) lines.push(`${indent}  ${line}`);
    for (let n = 1; n < trailing; n++) lines.push(`${indent}  `);
  };
  if (doc.instructions?.trim()) text('', 'instructions', doc.instructions);
  if (doc.maxNotesPerUpdate !== undefined) lines.push(`maxNotesPerUpdate: ${doc.maxNotesPerUpdate}`);
  if (doc.advisors.length) lines.push('advisors:');
  for (const row of doc.advisors) {
    lines.push(`  - name: ${JSON.stringify(row.name)}`);
    if (row.model?.trim()) lines.push(`    model: ${JSON.stringify(row.model.trim())}`);
    const tools = row.tools === undefined ? undefined : [...new Set(row.tools)];
    const editorDefault = tools?.length === 3 && ['read', 'grep', 'glob'].every(name => tools.includes(name));
    if (tools !== undefined && !editorDefault) {
      if (!tools.length) lines.push('    tools: []');
      else { lines.push('    tools:'); for (const tool of tools) lines.push(`      - ${JSON.stringify(tool)}`); }
    }
    if (row.reviewMode !== undefined && row.reviewMode !== 'turn') lines.push(`    reviewMode: ${JSON.stringify(row.reviewMode)}`);
    if (row.syncBacklog !== undefined) lines.push(`    syncBacklog: ${JSON.stringify(row.syncBacklog)}`);
    if (row.instructions?.trim()) text('    ', 'instructions', row.instructions);
    if (row.enabled !== undefined) lines.push(`    enabled: ${row.enabled}`);
    if (row.maxNotesPerUpdate !== undefined) lines.push(`    maxNotesPerUpdate: ${row.maxNotesPerUpdate}`);
    if (row.reviewInterval !== undefined && row.reviewInterval !== 1) lines.push(`    reviewInterval: ${row.reviewInterval}`);
  }
  return lines.length ? lines.join('\n') + '\n' : '';
}

/** Engine @import syntax, outside inline/fenced code; expansion itself remains native on next startup. */
export function hasImports(text: string | undefined): boolean {
  let fence = ''; let width = 0;
  for (const line of (text ?? '').replaceAll('\r\n', '\n').split('\n')) {
    const match = /^\s*(`{3,}|~{3,})/.exec(line);
    if (match) { if (!fence) { fence = match[1][0]; width = match[1].length; } else if (match[1][0] === fence && match[1].length >= width) fence = ''; continue; }
    if (fence) continue;
    for (const at of line.matchAll(/(^|[ \t])@([./~A-Za-z0-9_-][^\s]*)/g)) {
      const before = line.slice(0, at.index! + at[1].length); if ((before.match(/`+/g)?.length ?? 0) % 2 === 0) return true;
    }
  }
  return false;
}
export async function discover(engine: Engine, check: () => void = () => {}): Promise<any> {
  const candidates = new Map<string, { path: string; user: boolean }>();
  const add = (file: string, user: boolean) => { const full = path.resolve(file); const key = process.platform === 'win32' ? full.toLowerCase() : full; if (!candidates.has(key)) candidates.set(key, { path: full, user }); };
  for (const name of ['WATCHDOG.yml', 'WATCHDOG.yaml']) add(path.join(engine.agentDir, name), true);
  const home = path.resolve(os.homedir());
  for (let directory = engine.cwd; ; directory = path.dirname(directory)) {
    for (const name of ['WATCHDOG.yml', 'WATCHDOG.yaml']) { add(path.join(directory, '.omp', name), false); add(path.join(directory, name), false); }
    if (same(directory, engine.vcsRoot ?? home) || path.dirname(directory) === directory) break;
  }
  const files: (FileRow & { text: string })[] = []; const warnings: string[] = []; let safe = true;
  for (const candidate of candidates.values()) {
    check();
    const dir = path.dirname(candidate.path); const ordinary = path.basename(dir) === '.omp' ? path.dirname(dir) : dir;
    if (!candidate.user && path.basename(ordinary).startsWith('.') && path.basename(dir) !== '.omp') continue;
    try {
      const data = await read(candidate.path); check(); if (!data.exists) continue;
      const relative = path.relative(engine.cwd, ordinary);
      files.push({ scope: candidate.user ? 'user' : 'project', path: candidate.path, depth: relative ? relative.split(path.sep).filter(Boolean).length : 0, text: data.text });
    } catch { safe = false; if (warnings.length < 100) warnings.push(`${candidate.path}: Cannot read advisor file.`); }
  }
  files.sort((a, b) => a.scope !== b.scope ? a.scope === 'user' ? -1 : 1 : b.depth - a.depth);
  const merged = new Map<string, { config: AdvisorEntry; scope: string }>(); const instructions: string[] = []; let sharedMaxNotesPerUpdate;
  for (const file of files) {
    check(); const parsed = parseDocument(file.text, engine.yaml);
    for (const warning of parsed.warnings) if (warnings.length < 100) warnings.push(`${file.path}: ${warning}`);
    if (hasImports(parsed.document.instructions) || parsed.document.advisors.some(row => hasImports(row.instructions))) safe = false;
    if (parsed.document.instructions?.trim()) instructions.push(parsed.document.instructions.trim());
    if (parsed.document.maxNotesPerUpdate !== undefined) sharedMaxNotesPerUpdate = parsed.document.maxNotesPerUpdate;
    for (const row of parsed.document.advisors) {
      const config = { ...row, model: row.model?.trim() || undefined, instructions: row.instructions?.trim() || undefined };
      if (row.tools?.length) { const tools = [...new Set(row.tools)].filter(name => TOOLS.has(name)); config.tools = tools.length ? tools : undefined; }
      merged.set(slug(row.name), { config, scope: file.scope });
    }
  }
  // Native-sized files can exceed the host transport/editor limits; preserve files but never claim a partial apply.
  if (Buffer.byteLength(instructions.join('\n\n')) > 1024 * 1024) safe = false;
  const rows = [...merged.values()];
  return { advisors: rows.map(row => row.config), sharedInstructions: instructions.length ? instructions.join('\n\n') : undefined, sharedMaxNotesPerUpdate,
    files: files.map(({ scope, path }) => ({ scope, path })), roster: fitBytes(rows.map(({ config, scope }) => ({ name: config.name, scope, enabled: config.enabled !== false, ...(config.model ? { model: config.model } : {}) })), REPLY_LIST_BYTES),
    implicitDefault: merged.size === 0, warnings, safe };
}
/** No count limit on the lists a reply carries: the host fits them to the page's message size; this only keeps one reply under the host's line limit. */
const REPLY_LIST_BYTES = 3 * 1024 * 1024;
function fitBytes<T>(rows: T[], limit: number): T[] {
  let total = 0; let count = 0;
  for (const row of rows) { total += Buffer.byteLength(JSON.stringify(row) ?? '') + 1; if (total > limit) break; count++; }
  return count === rows.length ? rows : rows.slice(0, count);
}
function status(engine: Engine, merged: any): any {
  const session = engine.session; const enabled = session.isAdvisorEnabled(); const overview = session.getAdvisorStatusOverview(); const stats = session.getAdvisorStats();
  if (typeof enabled !== 'boolean' || typeof overview?.configured !== 'boolean' || !Array.isArray(overview.advisors) || !Array.isArray(stats?.advisors)) throw new Error('unsupported');
  const count = (value: any): number | undefined => typeof value === 'number' && Number.isFinite(value) && value >= 0 ? value : undefined;
  const advisors = fitBytes(overview.advisors.map((row: any) => {
    const stat = stats.advisors.find((item: any) => item.name === row.name); const model = stat?.model;
    // The figures /advisor status prints: context use and spend.
    const usage = Object.entries({ contextTokens: count(stat?.contextTokens), contextWindow: count(stat?.contextWindow), cost: count(stat?.cost) }).filter(([, value]) => value !== undefined);
    return { name: String(row.name).slice(0, 200), status: String(row.status).slice(0, 64), ...(typeof model?.provider === 'string' && typeof model.id === 'string' ? { model: `${model.provider}/${model.id}`.slice(0, 200) } : {}), ...Object.fromEntries(usage) };
  }), REPLY_LIST_BYTES);
  const active = advisors.filter((row: any) => row.status === 'running').length;
  return { supported: true, sessionId: session.sessionManager.getSessionId(), enabled,
    status: !enabled ? 'off' : active ? 'active' : advisors.some((row: any) => row.status === 'no_model') ? 'no-model' : advisors.length ? 'paused' : 'unknown',
    advisors, files: merged.files, roster: merged.roster, implicitDefault: merged.implicitDefault, updatedUtc: new Date().toISOString() };
}

/** The conversation's own on/off per advisor (slug → on), over the files' `enabled`; the files themselves are never changed. */
export function withChoices(advisors: AdvisorEntry[], choices: unknown): AdvisorEntry[] {
  const map = choices && typeof choices === 'object' && !Array.isArray(choices) ? choices as Record<string, unknown> : {};
  return advisors.map(row => { const on = Object.hasOwn(map, slug(row.name)) ? map[slug(row.name)] : undefined; return typeof on === 'boolean' ? { ...row, enabled: on } : row; });
}

/**
 * One advisor's part of the engine's transcript: with several advisors running it heads each part "### Advisor: name (provider/model)";
 * a single running advisor's transcript has no heading. Null when that advisor has none.
 */
export function advisorSection(text: string, name: string, running: string[]): string | null {
  const heading = /^### Advisor: (.*) \([^()\n]*\)$/gm;
  const parts: { name: string; start: number; body: number }[] = [];
  for (let match; (match = heading.exec(text));) parts.push({ name: match[1], start: match.index, body: match.index + match[0].length });
  if (!parts.length) return running.length === 1 && running[0] === name ? text : null;
  const index = parts.findIndex(part => part.name === name);
  if (index < 0) return null;
  return text.slice(parts[index].body, index + 1 < parts.length ? parts[index + 1].start : text.length).replace(/^\n+|\n+$/g, '');
}

/** The advisors' notes in this conversation, oldest first: the engine keeps each delivery as an "advisor" custom message. */
export function advisorNotes(entries: unknown): { advisor: string; severity?: string; note: string; at?: string }[] {
  const notes: { advisor: string; severity?: string; note: string; at?: string }[] = [];
  for (const entry of Array.isArray(entries) ? entries : []) {
    if (entry?.type !== 'custom_message' || entry.customType !== 'advisor' || !Array.isArray(entry.details?.notes)) continue;
    const time = typeof entry.timestamp === 'string' || typeof entry.timestamp === 'number' ? new Date(entry.timestamp) : undefined;
    const at = time && Number.isFinite(time.getTime()) ? time.toISOString() : undefined;
    for (const row of entry.details.notes) {
      if (typeof row?.note !== 'string' || !row.note.trim()) continue;
      notes.push({ advisor: typeof row.advisor === 'string' ? row.advisor.slice(0, 200) : '', ...(typeof row.severity === 'string' ? { severity: row.severity.slice(0, 32) } : {}), note: row.note.slice(0, 8000), ...(at ? { at } : {}) });
    }
  }
  // No count limit: the newest notes that fit the notice.
  let kept = notes;
  while (kept.length && Buffer.byteLength(JSON.stringify(kept)) > NOTES_BYTES) kept = kept.slice(Math.ceil(kept.length / 4));
  return kept;
}

/** The newest part of a transcript that fits the result notice, cut at a line start. */
export function historyTail(text: string): { history: string; truncated: boolean } {
  let history = text; let truncated = false;
  for (let limit = HISTORY_BYTES; Buffer.byteLength(JSON.stringify(history)) > HISTORY_BYTES; limit = Math.floor(limit / 2)) {
    const bytes = Buffer.from(history, 'utf8');
    history = bytes.subarray(Math.max(0, bytes.length - limit)).toString('utf8').replace(/^�+/, '');
    const line = history.indexOf('\n'); if (line >= 0 && line < history.length - 1) history = history.slice(line + 1);
    truncated = true;
  }
  return { history, truncated };
}

export async function execute(command: any, engine: Engine, ctx: any): Promise<any> {
  const check = () => {
    if (!Number.isSafeInteger(command.expires) || Date.now() >= command.expires) throw new Error('expired');
    const id = engine.session.sessionManager.getSessionId();
    if (!same(path.resolve(engine.session.sessionManager.getCwd()), engine.cwd)) throw new Error('stale-session');
    if (typeof command.sessionId !== 'string' || !command.sessionId || command.sessionId !== id || ctx.sessionManager?.getSessionId?.() !== id
      || ctx.agent?.kind === 'sub' || (ctx.agent?.id && ctx.agent.id !== engine.mainId)) throw new Error('stale-session');
  };
  check();
  if (!['load', 'save', 'enable', 'disable', 'status', 'history', 'apply'].includes(command.action)) throw new Error('invalid-action');
  const result: any = { ok: true };
  if (command.action === 'load' || command.action === 'save') {
    if (command.scope !== 'user' && command.scope !== 'project') throw new Error('invalid-scope');
    const file = await selectedPath(command.scope, engine); check();
    if (command.action === 'save') {
      const document = validateDocument(command.document);
      if (typeof command.expectedPath !== 'string' || !same(path.resolve(command.expectedPath), file)) throw new Error('conflict');
      if (command.scope === 'project' && command.projectTrusted !== true) throw new Error('untrusted');
      const before = await read(file); check();
      if (!/^[a-f0-9]{64}$/.test(command.baseHash ?? '') || command.baseHash !== before.hash) throw new Error('conflict');
      const content = serializeDocument(document); if (Buffer.byteLength(content) > MAX_BYTES) throw new Error('document-too-large');
      // Open existing files without truncating first; verify the handle and hash immediately before the write.
      if (content) {
        await directPath(file); check();
        const handle = await fs.open(file, before.exists ? 'r+' : 'wx+');
        try {
          const stat = await handle.stat(); if (!stat.isFile() || stat.nlink > 1 || stat.size > MAX_BYTES) throw new Error('unsafe-path');
          const bytes = await handle.readFile(); await directPath(file); check();
          if (hash(bytes) !== before.hash) throw new Error('conflict');
          const output = Buffer.from(content); let written = 0;
          while (written < output.length) { check(); const part = await handle.write(output, written, output.length - written, written); if (!part.bytesWritten) throw new Error('write-failed'); written += part.bytesWritten; }
          await handle.truncate(output.length);
        } finally { await handle.close(); }
      } else { await directPath(file); const latest = await read(file); check(); if (latest.hash !== before.hash) throw new Error('conflict'); if (latest.exists) await fs.unlink(file); }
      result.applied = false;
      result.saved = true;
    }
    const data = await read(file); check();
    const parsed = data.exists ? parseDocument(data.text, engine.yaml) : { document: { advisors: [] }, warnings: [] };
    Object.assign(result, { scope: command.scope, path: file, exists: data.exists, hash: data.hash, document: parsed.document, warnings: parsed.warnings });
  }
  const merged = await discover(engine, check); check();
  if (command.action === 'save') {
    if (merged.safe && typeof engine.session.applyAdvisorConfigs === 'function') {
      try { const active = engine.session.applyAdvisorConfigs(withChoices(merged.advisors, command.choices), merged.sharedInstructions, merged.sharedMaxNotesPerUpdate); result.applied = Number.isInteger(active) && active >= 0; if (result.applied) result.active = active; }
      catch { result.applied = false; }
    }
    result.warnings = [...new Set([...(result.warnings ?? []), ...merged.warnings])].slice(0, 100);
  }
  if (command.action === 'enable' || command.action === 'disable') { check(); engine.session.setAdvisorEnabled(command.action === 'enable'); }
  if (command.action === 'apply') {
    // The advisors this conversation runs: the files' set with the conversation's own choices, then the conversation's on/off.
    if (!merged.safe || typeof engine.session.applyAdvisorConfigs !== 'function') throw new Error('unsupported');
    check(); engine.session.applyAdvisorConfigs(withChoices(merged.advisors, command.choices), merged.sharedInstructions, merged.sharedMaxNotesPerUpdate);
    if (typeof command.enabled === 'boolean' && engine.session.isAdvisorEnabled() !== command.enabled) engine.session.setAdvisorEnabled(command.enabled);
  }
  if (command.action === 'history') {
    // The compact transcript /advisor dump prints; null while no advisor runs in this conversation.
    if (typeof engine.session.formatAdvisorHistoryAsText !== 'function') throw new Error('unsupported');
    const text = engine.session.formatAdvisorHistoryAsText({ compact: true });
    // One advisor's part when the page asks for that advisor.
    const running = (engine.session.getAdvisorStatusOverview()?.advisors ?? []).filter((row: any) => row.status === 'running').map((row: any) => String(row.name));
    const part = typeof text === 'string' && typeof command.name === 'string' ? advisorSection(text, command.name, running) : text;
    Object.assign(result, typeof part === 'string' ? historyTail(part) : { history: null, truncated: false });
    result.notes = advisorNotes(engine.session.sessionManager?.getEntries?.());
  }
  result.state = status(engine, merged);
  return result;
}

export default function advisor(pi: ExtensionAPI, load: () => Promise<Engine | null> = loadEngine): void {
  if (!isPompHost()) return;
  let queue = Promise.resolve();
  pi.registerCommand('pomp-advisor', {
    description: 'Advisor settings (POMP host only)',
    handler: async (args: string, ctx: any) => {
      const match = /^\s*([A-Za-z0-9+/=]+)\s+--request\s+([a-f0-9]{32})\s*$/.exec(args ?? ''); const request = match?.[2] ?? '';
      const report = (result: any) => {
        const encoded = Buffer.from(JSON.stringify({ request, ...result })).toString('base64');
        ctx.ui.notify(RESULT + (encoded.length <= 900000 ? encoded : Buffer.from(JSON.stringify({ request, ok: false, error: 'result-too-large' })).toString('base64')), 'info');
      };
      const work = async () => {
        try {
          if (!match || match[1].length > 700000) throw new Error('invalid-command');
          if (ctx.agent?.kind === 'sub') throw new Error('main-only');
          const bytes = Buffer.from(match[1], 'base64'); if (bytes.length > 512 * 1024) throw new Error('invalid-command');
          const command = JSON.parse(bytes.toString('utf8'));
          const engine = await load(); if (!engine) { report({ ok: false, error: 'unsupported' }); return; }
          report(await execute(command, engine, ctx));
        } catch (error: any) {
          // Never forward arbitrary filesystem/parser/runtime errors: they can contain instructions or secrets.
          const allowed = new Set(['invalid-command', 'main-only', 'unsupported', 'expired', 'stale-session', 'invalid-action', 'invalid-scope', 'invalid-document', 'duplicate-name', 'conflict', 'untrusted', 'unsafe-path', 'file-too-large-or-unsafe', 'document-too-large']);
          report({ ok: false, error: allowed.has(error?.message) ? error.message : 'advisor-failed' });
        }
      };
      const pending = queue.then(work, work); queue = pending.catch(() => {}); await pending;
    },
  });
}
