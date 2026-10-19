import { send, type ActiveRulesPayload } from './protocol';

/** docs/ui-protocol.md (tools/skills/mcp); these maps can be extended by the shared protocol. */
export type ToolGroup = 'files' | 'edit' | 'exec' | 'web' | 'agents' | 'host' | 'mcp' | 'other';
export type ResourceScope = 'project' | 'user';
export interface ConversationTool { name: string; group: ToolGroup; server?: string; description?: string; active: boolean; defaultActive: boolean }
export interface ToolsStatePayload { sessionId?: string; tools: ConversationTool[]; pending: boolean; available: boolean; message?: string }
/** `unreadable` (all resource rows): the file cannot be loaded into the in-page editor (over the size limit, not UTF-8) — open and delete only. */
/** `enabled` false (absent = on): switched off (POMP's overrides or the omp config's disabledExtensions). `pomp` rows are built into POMP (read-only): `catalogId` is the entry id, `defaultEnabled` the shipped state, `category` the catalog group. `catalogUpdate` is deprecated, always false. */
export interface SkillInfo { name: string; description: string; source: ResourceScope | 'omp' | 'installed' | 'pomp'; file?: string; hash?: string; invocable: boolean; note?: string; unreadable?: boolean; enabled?: boolean; catalogId?: string; catalogUpdate?: boolean; defaultEnabled?: boolean; category?: string }
export interface SkillsStatePayload { skills: SkillInfo[]; diagnostics: string[] }
export const MASKED_ENV = '••••';
/** `headers` (http/sse) are masked like `env`. `catalogId`: installed from the POMP catalog. */
export interface McpServer { name: string; scope: ResourceScope; type: 'stdio' | 'http' | 'sse'; command?: string; args?: string[]; env?: Record<string, string>; url?: string; headers?: Record<string, string>; enabled: boolean; error?: string; hash?: string; catalogId?: string }
export interface McpStatePayload { servers: McpServer[]; restartRequired: boolean; message?: string }
export type CatalogKind = 'skill' | 'mcp';
export type CatalogNeed = 'network' | 'token' | 'browser' | 'node';
/** One value asked at install (an MCP token, say); a secret never comes back to the page. */
export interface CatalogInput { key: string; label: string; secret: boolean; required: boolean }
/** An entry of the catalog bundled with POMP: skills written by POMP, MCP servers at pinned versions (docs/ui-protocol.md, catalog). */
export interface CatalogEntry { id: string; kind: CatalogKind; title: string; description: string; category: string; version: string; publisher?: string; license?: string; scopes: ResourceScope[]; installed: ResourceScope[]; requires: string[]; needs: CatalogNeed[]; inputs: CatalogInput[] }
export interface CatalogStatePayload { entries: CatalogEntry[]; message?: string }
/** install: id, scope, inputs (MCP). Installs create files only, never overwrite; a skill is on after install, an MCP server off. */
export interface CatalogCommand { requestId: string; action: 'refresh' | 'install'; id?: string; scope?: ResourceScope; inputs?: Record<string, string> }
/** Fields the in-page editor can change without losing anything else in the file (docs/ui-protocol.md, in-page resource editing). */
export interface SkillForm { description: string; body: string }
export interface RuleForm { description: string; alwaysApply: boolean; globs: string[]; body: string }
/** A file read for editing: the whole text and its hash; `form` only when every frontmatter key is one the form edits. */
export interface ResourceDocument { content: string; hash: string; form?: SkillForm | RuleForm }
/** What a skills `import` with `ticket` would copy: a chosen SKILL.md brings its whole folder, any other Markdown file only itself. */
export interface SkillImportPreview { ticket: string; name: string; description: string; file: string; files: number; bytes: number }
export interface ToolsReply { requestId: string; ok: boolean; error?: string; output?: string; document?: ResourceDocument; workflow?: Partial<WorkflowInfo>; skillImport?: SkillImportPreview }
export interface ToolsCommand { requestId: string; action: 'refresh' | 'set' | 'reset'; active?: string[] }
/**
 * create: name, scope, description and an optional body (empty: a starter body). read: the file of one row (scope omitted for an
 * installed/engine row) → reply.document. update: expectedHash plus either the form fields (description, body) or the whole text (content).
 * pick: the host's open-file dialog → reply.skillImport (absent when it was closed). import: scope and the ticket of that preview.
 * refresh and every change also make the engine rediscover its skills.
 */
/** enable/disable: name (any source; kept in POMP's own overrides and merged into the engine's disabledExtensions, applied without a restart). reset: name (clears the overrides, back to the default). */
export interface SkillsCommand { requestId: string; action: 'refresh' | 'create' | 'pick' | 'import' | 'delete' | 'open' | 'read' | 'update' | 'enable' | 'disable' | 'reset'; scope?: ResourceScope; name?: string; description?: string; body?: string; content?: string; expectedHash?: string; ticket?: string }
export interface McpCommand { requestId: string; action: 'refresh' | 'save' | 'delete' | 'enable' | 'disable' | 'test' | 'restart'; scope?: ResourceScope; name?: string; server?: McpServer; expectedHash?: string }
/** A top-level `.pomp/rules/*.mdc` file; `note` says when only the engine can read its frontmatter. */
export interface RuleInfo { name: string; scope: ResourceScope; file: string; hash: string; description: string; alwaysApply: boolean; globs: string[]; note?: string; unreadable?: boolean }
export interface RulesStatePayload { rules: RuleInfo[]; diagnostics: string[] }
/** As SkillsCommand: create takes an optional body; read → reply.document; update takes the form fields or the whole text. */
export interface RulesCommand { requestId: string; action: 'refresh' | 'create' | 'import' | 'open' | 'delete' | 'read' | 'update'; scope?: ResourceScope; name?: string; description?: string; alwaysApply?: boolean; globs?: string[]; body?: string; content?: string; expectedHash?: string }
/** A saved workflow (`<scope>/.pomp/workflows/<name>.json`); `note` says why it cannot be edited here (then task/steps may be empty). */
/** summary: the host left task/steps out to keep workflows/state small; workflows/command get answers the full file. */
export interface WorkflowInfo { name: string; scope: ResourceScope; file: string; hash: string; description: string; task: string; steps: string[]; stepCount?: number; summary?: boolean; note?: string; unreadable?: boolean }
export interface WorkflowsStatePayload { workflows: WorkflowInfo[]; diagnostics: string[] }
export interface WorkflowDraft { description: string; task: string; steps: string[] }
/** create: scope, name, workflow. update/delete: scope, name, expectedHash (+ workflow for update). open: scope, name (VS editor). */
export interface WorkflowsCommand { requestId: string; action: 'refresh' | 'create' | 'update' | 'delete' | 'open' | 'get'; scope?: ResourceScope; name?: string; workflow?: WorkflowDraft; expectedHash?: string }
export type HookEvent = 'onFileSave' | 'onBuild';
/** A project hook file `.pomp/hooks/<id>.md`; a file that does not parse has `note`, event '' and no script, and can only be opened or deleted. */
export interface HookInfo { id: string; name: string; file: string; hash: string; description: string; event: HookEvent | ''; glob: string; timeoutSeconds: number; enabled: boolean; script: string; note?: string; unreadable?: boolean }
/** `policy`: hooks run only in a workspace with a valid project policy (HookExecutionPolicy). */
export interface HooksStatePayload { hooks: HookInfo[]; diagnostics: string[]; policy: 'none' | 'valid' | 'invalid' }
export interface HookDraft { description: string; event: HookEvent; glob: string; timeoutSeconds: number; enabled: boolean; script: string }
/** create: name (becomes the id) + hook. update: id, expectedHash, hook. setEnabled: id, expectedHash, enabled. delete/open: id (+ expectedHash for delete). */
export interface HooksCommand { requestId: string; action: 'refresh' | 'create' | 'update' | 'setEnabled' | 'delete' | 'open'; id?: string; name?: string; hook?: HookDraft; enabled?: boolean; expectedHash?: string }
export interface ToolsHostToUi {
  'rules/active': ActiveRulesPayload;
  'tools/state': ToolsStatePayload; 'tools/reply': ToolsReply;
  'skills/state': SkillsStatePayload; 'skills/reply': ToolsReply;
  'mcp/state': McpStatePayload; 'mcp/reply': ToolsReply;
  'catalog/state': CatalogStatePayload; 'catalog/reply': ToolsReply;
  'rules/state': RulesStatePayload; 'rules/reply': ToolsReply;
  'workflows/state': WorkflowsStatePayload; 'workflows/reply': ToolsReply;
  'hooks/state': HooksStatePayload; 'hooks/reply': ToolsReply;
}
export interface ToolsUiToHost { 'tools/command': ToolsCommand; 'skills/command': SkillsCommand; 'mcp/command': McpCommand; 'catalog/command': CatalogCommand; 'rules/command': RulesCommand; 'workflows/command': WorkflowsCommand; 'hooks/command': HooksCommand }
export type ToolsDomain = 'tools' | 'skills' | 'mcp' | 'catalog' | 'rules' | 'workflows' | 'hooks';
export type ToolsCommandFor<D extends ToolsDomain> = Omit<ToolsUiToHost[`${D}/command`], 'requestId'>;
export type ToolsHostMessage = { [K in keyof ToolsHostToUi]: { type: K; payload: ToolsHostToUi[K] } }[keyof ToolsHostToUi];

/** The shared send remains the only transport; type extension is safe before the shell is integrated. */
const sendCommand = send as <K extends keyof ToolsUiToHost>(type: K, payload: ToolsUiToHost[K]) => boolean;
export function sendToolsCommand<D extends ToolsDomain>(domain: D, payload: ToolsUiToHost[`${D}/command`]): boolean {
  return sendCommand(`${domain}/command`, payload);
}
