import { createMockPermissions } from './mockPermissions';
import { MASKED_ENV, type CatalogCommand, type CatalogEntry, type CatalogStatePayload, type ConversationTool, type HooksCommand, type HooksStatePayload, type HookDraft, type HookInfo, type McpCommand, type McpServer, type ResourceDocument, type SkillInfo, type RulesCommand, type RulesStatePayload, type SkillsCommand, type SkillsStatePayload, type ToolsCommand, type ToolsDomain, type ToolsHostMessage, type ToolsStatePayload, type WorkflowsCommand, type WorkflowsStatePayload, type WorkflowDraft, type WorkflowInfo } from '../toolsProtocol';

/**
 * The skills built into POMP (source `pomp`): [name, category, description, default state, current state (default when omitted)].
 */
const pompSkills = ([
  ['brainstorming', 'planning', 'Turn a rough idea into a design before any code is written.', true],
  ['writing-plans', 'planning', 'Write the steps into the agent plan before touching code.', true],
  ['test-driven-development', 'testing', 'Write the failing test first, then the code that passes it.', false],
  ['csharp-testing', 'testing', 'Write and run C# tests with the VS test tools.', true],
  ['systematic-debugging', 'debugging', 'Find the cause of a failure before changing any code.', true],
  ['codebase-design', 'architecture', 'Vocabulary for deep modules and where the seams go.', false],
  ['grilling', 'architecture', 'Question a plan one decision at a time until it holds.', false],
  ['domain-modeling', 'architecture', 'Keep the project glossary and decision records sharp.', false],
  ['improve-architecture', 'architecture', 'Find places where a module can be made deeper.', false],
  ['security-review', 'security', 'Check changes for vulnerabilities.', true, false],
] as Array<[string, string, string, boolean, boolean?]>).map(([name, category, description, defaultEnabled, enabled = defaultEnabled]): SkillInfo =>
  ({ name, description, source: 'pomp', file: `C:\\mock-pomp\\catalog\\skills\\${name}\\SKILL.md`, invocable: enabled, enabled, defaultEnabled, category, catalogId: name }));
/** What switching a skill on turns on with it. */
const skillRequires: Record<string, string[]> = { 'improve-architecture': ['codebase-design', 'grilling', 'domain-modeling'] };

/** The bundled catalog as the page lists it: MCP servers only (the catalog's skills are built in and are skill rows). */
const catalogEntries: CatalogEntry[] = [
  { id: 'github', kind: 'mcp', title: 'GitHub', description: 'GitHub’s hosted MCP server: repositories, issues and pull requests over HTTPS.', category: 'dev', version: '2.0.2', publisher: 'GitHub', license: 'MIT',
    scopes: ['user'], installed: [], requires: [], needs: ['network', 'token'], inputs: [{ key: 'token', label: 'Personal access token', secret: true, required: true }] },
  { id: 'context7', kind: 'mcp', title: 'Context7', description: 'Current library documentation for the model.', category: 'docs', version: '4.3.0', publisher: 'Upstash', license: 'MIT',
    scopes: ['project', 'user'], installed: [], requires: [], needs: ['node', 'network'], inputs: [{ key: 'apiKey', label: 'API key', secret: true, required: false }] },
  { id: 'example-search', kind: 'mcp', title: 'Example search', description: 'A search server in a category this page has no label for.', category: 'search', version: '0.9.1', publisher: 'Example', license: 'Apache-2.0',
    scopes: ['project', 'user'], installed: [], requires: [], needs: ['node', 'network', 'browser'], inputs: [{ key: 'region', label: 'Region', secret: false, required: true }] },
];
/** The server each MCP entry writes (what the host's template yields once its inputs are filled in; a header or variable value is masked on the way back). */
const catalogServers: Record<string, (inputs: Record<string, string>) => Omit<McpServer, 'name' | 'scope' | 'enabled'>> = {
  github: () => ({ type: 'http', url: 'https://api.githubcopilot.com/mcp/', headers: { Authorization: MASKED_ENV } }),
  context7: inputs => ({ type: 'stdio', command: 'npx', args: ['-y', '@upstash/context7-mcp@4.3.0'], env: inputs.apiKey ? { CONTEXT7_API_KEY: MASKED_ENV } : undefined }),
  'example-search': inputs => ({ type: 'sse', url: `https://mcp.example.test/search?region=${encodeURIComponent(inputs.region ?? '')}` }),
};

/** One instance per mock host. No provider, process, filesystem or user-profile calls. */
export function createMockToolsHandler(emit: (message: ToolsHostMessage) => void, options: { delay?: number; sessionId?: string; busy?: () => boolean } = {}) {
  const definitions: Array<[ConversationTool['group'], string[], string?]> = [
    ['files', ['read', 'find', 'grep', 'glob', 'lsp']], ['edit', ['edit', 'write', 'notebook']], ['exec', ['bash', 'eval']], ['web', ['fetch', 'search']], ['agents', ['task', 'wait', 'agent_message']],
    ['host', ['vs_read_buffer', 'vs_diagnostics', 'vs_propose_edit', 'vs_build', 'vs_tests']], ['mcp', ['mcp_docs_search', 'mcp_docs_read'], 'docs'], ['mcp', ['mcp_tracker_list', 'mcp_tracker_create'], 'tracker'], ['other', ['todo']],
  ];
  let tools: ToolsStatePayload = { sessionId: options.sessionId ?? 'mock-session', available: true, pending: false, tools: definitions.flatMap(([group, names, server]) => names.map(name => ({ name, group, server, active: true, defaultActive: !['eval', 'vs_build', 'vs_tests'].includes(name), description: `${name.replaceAll('_', ' ')} (${group})` }))) };
  tools.tools = tools.tools.map(tool => ({ ...tool, active: tool.defaultActive }));
  let skills: SkillsStatePayload = { skills: [
    { name: 'project-review', description: 'Review this project’s conventions and proposed changes.', source: 'project', file: 'C:\\workspace\\.pomp\\skills\\project-review\\SKILL.md', hash: 'skill-project-1', invocable: true },
    { name: 'release-notes', description: 'Draft release notes from verified changes.', source: 'user', file: 'C:\\mock-user\\.pomp\\skills\\release-notes\\SKILL.md', hash: 'skill-user-1', invocable: true },
    { name: 'deep-local', description: 'A nested local recipe.', source: 'project', file: 'C:\\workspace\\.pomp\\skills\\nested\\deep-local\\SKILL.md', hash: 'skill-nested-1', invocable: false, note: 'Nested skills are available through /skill only.' },
    { name: 'planning', description: 'Create an implementation plan.', source: 'omp', file: 'C:\\mock-agent\\skills\\planning\\SKILL.md', invocable: true },
    { name: 'visual-studio', description: 'Use Visual Studio integration safely.', source: 'installed', file: 'C:\\mock-extension\\skills\\visual-studio\\SKILL.md', invocable: true },
    // The update badge (the host no longer sets catalogUpdate, since catalog skills are built in rather than copied; the page still shows it when it is true).
    { name: 'api-design', description: 'Design a stable HTTP API before writing it.', source: 'project', file: 'C:\\workspace\\.pomp\\skills\\api-design\\SKILL.md', hash: 'skill-copy-1', invocable: true, catalogId: 'api-design', catalogUpdate: true },
    ...pompSkills,
  ], diagnostics: [] };
  // ?bulk=1 (dev gallery): long lists, so search and paging show.
  const bulk = typeof location !== 'undefined' && new URLSearchParams(location.search).get('bulk') === '1';
  if (bulk) skills.skills.push(...Array.from({ length: 30 }, (_, i): SkillInfo => ({ name: `team-skill-${String(i + 1).padStart(2, '0')}`, description: `Team recipe number ${i + 1} for the shared build and review steps.`, source: i % 3 ? 'user' : 'project', file: `C:\workspace\.pomp\skills\team-skill-${i + 1}\SKILL.md`, hash: `skill-bulk-${i}`, invocable: true, enabled: i % 4 !== 0 })));
  skills = { ...skills, skills: skills.skills.map(skill => ({ ...skill, enabled: skill.enabled !== false })) };
  // What each row is when it is switched on: switching it off takes it out of the engine's command list, so the page sees it as not invocable.
  const invocableWhenOn = new Map(skills.skills.map(skill => [`${skill.source}:${skill.name}`, skill.source === 'pomp' || skill.invocable]));
  let servers: McpServer[] = [
    { name: 'docs', scope: 'project', type: 'stdio', command: 'node', args: ['mock-docs-server.js'], env: { DOCS_TOKEN: MASKED_ENV }, enabled: true, hash: 'mcp-project-1' },
    { name: 'tracker', scope: 'user', type: 'http', url: 'https://mcp.example.test/tracker', headers: { Authorization: MASKED_ENV }, enabled: false, hash: 'mcp-user-1' },
    { name: 'context7', scope: 'user', type: 'stdio', command: 'npx', args: ['-y', '@upstash/context7-mcp@4.3.0'], enabled: false, hash: 'mcp-user-2', catalogId: 'context7' },
  ];
  const rules: RulesStatePayload = { rules: [
    { name: 'csharp-style', scope: 'project', file: 'C:\\workspace\\.pomp\\rules\\csharp-style.mdc', hash: 'rule-1', description: 'C# conventions for this solution.', alwaysApply: false, globs: ['src/**/*.cs'] },
    { name: 'review-tone', scope: 'user', file: 'C:\\mock-user\\.pomp\\rules\\review-tone.mdc', hash: 'rule-2', description: 'Keep review comments short.', alwaysApply: true, globs: [] },
    { name: 'legacy-rule', scope: 'project', file: 'C:\\workspace\\.pomp\\rules\\legacy-rule.mdc', hash: 'rule-3', description: 'Legacy rule with custom keys.', alwaysApply: false, globs: [] },
  ], diagnostics: [] };
  let workflows: WorkflowInfo[] = [
    { name: 'ship-hotfix', scope: 'project', file: 'C:\\workspace\\.pomp\\workflows\\ship-hotfix.json', hash: 'workflow-1', description: 'Land an urgent fix safely.', task: 'Verify the failing test, apply the minimal fix and run the suite.', steps: ['Reproduce the failure', 'Apply the minimal fix', 'Run the affected suite'] },
    { name: 'weekly-review', scope: 'user', file: 'C:\\mock-user\\.pomp\\workflows\\weekly-review.json', hash: 'workflow-2', description: 'Summarize the week.', task: 'Collect merged changes and draft the weekly summary.', steps: ['List merged changes', 'Draft the summary'] },
    { name: 'legacy-plan', scope: 'project', file: 'C:\\workspace\\.pomp\\workflows\\legacy-plan.json', hash: 'workflow-3', description: '', task: '', steps: [], note: 'This file uses a shape the page editor cannot change.' },
  ];
  if (bulk) {
    rules.rules.push(...Array.from({ length: 24 }, (_, i) => ({ name: `area-rule-${i + 1}`, scope: (i % 2 ? 'user' : 'project') as 'user' | 'project', file: `C:\workspace\.pomp\rules\area-rule-${i + 1}.mdc`, hash: `rule-bulk-${i}`, description: `Conventions for area ${i + 1}.`, alwaysApply: i % 5 === 0, globs: i % 5 === 0 ? [] : [`src/Area${i + 1}/**/*.cs`] })));
    workflows = [...workflows, ...Array.from({ length: 24 }, (_, i): WorkflowInfo => ({ name: `release-step-${i + 1}`, scope: i % 2 ? 'user' : 'project', file: `C:\workspace\.pomp\workflows\release-step-${i + 1}.json`, hash: `workflow-bulk-${i}`, description: `Release checklist part ${i + 1}.`, task: 'Run the release checks.', steps: ['Build', 'Test', 'Package'] }))];
  }
  let hooks: HookInfo[] = [
    { id: 'format-on-save', name: 'format-on-save', file: 'C:\\workspace\\.pomp\\hooks\\format-on-save.md', hash: 'hook-1', description: 'Format saved C# files.', event: 'onFileSave', glob: 'src/**/*.cs', timeoutSeconds: 120, enabled: true, script: 'dotnet format $POMP_FILE' },
    { id: 'verify-build', name: 'verify-build', file: 'C:\\workspace\\.pomp\\hooks\\verify-build.md', hash: 'hook-2', description: 'Check the build log.', event: 'onBuild', glob: '**', timeoutSeconds: 300, enabled: false, script: 'Write-Host $POMP_EVENT' },
    { id: 'broken-hook', name: 'broken-hook', file: 'C:\\workspace\\.pomp\\hooks\\broken-hook.md', hash: 'hook-3', description: '', event: '', glob: '', timeoutSeconds: 120, enabled: false, script: '', note: 'This file does not parse; open it in VS or delete it.' },
  ];
  let hooksPolicy: HooksStatePayload['policy'] = 'none';
  const bodies = new Map<string, { content: string; hash: string }>([
    ['skill:project:project-review', { content: '---\nname: project-review\ndescription: Review this project\u2019s conventions and proposed changes.\n---\n# Project review\nFollow the project conventions.', hash: 'skill-project-1-doc' }],
    ['skill:user:release-notes', { content: '---\nname: release-notes\ndescription: Draft release notes from verified changes.\n---\n# Release notes\nSummarize verified changes.', hash: 'skill-user-1-doc' }],
    ['skill:project:deep-local', { content: '---\nname: deep-local\ndescription: A nested local recipe.\nextra: nested-only\n---\n# Deep\nNested content the form cannot edit.', hash: 'skill-nested-1-doc' }],
    ['skill:omp:planning', { content: '---\nname: planning\ndescription: Create an implementation plan.\nextra: engine-only\n---\n# Planning\nEngine metadata the form cannot edit.', hash: 'skill-omp-1-doc' }],
    ['skill:installed:visual-studio', { content: '---\nname: visual-studio\ndescription: Use Visual Studio integration safely.\n---\n# Visual Studio\nInstalled skill body for copying.', hash: 'skill-installed-1-doc' }],
    ['rule:project:csharp-style', { content: '---\ndescription: "C# conventions for this solution."\nalwaysApply: false\nglobs: ["src/**/*.cs"]\n---\n# C# style\nFollow these conventions.', hash: 'rule-1-doc' }],
    ['rule:user:review-tone', { content: '---\ndescription: "Keep review comments short."\nalwaysApply: true\nglobs: []\n---\n# Review tone\nBe brief.', hash: 'rule-2-doc' }],
    ['rule:project:legacy-rule', { content: '---\ndescription: "Legacy rule with custom keys."\ncustom: engine-only\n---\n# Legacy\nCustom frontmatter the form cannot edit.', hash: 'rule-3-doc' }],
  ]);
  for (const row of pompSkills) bodies.set(`skill:pomp:${row.name}`, { content: `---\nname: ${row.name}\ndescription: ${row.description}\n---\n# ${row.name}\nBuilt-in skill body.`, hash: `skill-pomp-${row.name}-doc` });
  bodies.set('skill:project:api-design', { content: '---\nname: api-design\ndescription: Design a stable HTTP API before writing it.\n---\n# API design\nA copy of the catalog skill.', hash: 'skill-copy-1-doc' });
  let restartRequired = false; let revision = 1;
  const pendingDomains = new Set<ToolsDomain>();
  const catalogState = (): CatalogStatePayload => ({ entries: catalogEntries.map(entry => ({ ...structuredClone(entry), installed: entry.kind === 'mcp' ? entry.scopes.filter(scope => servers.some(server => server.catalogId === entry.id && server.scope === scope)) : [] })) });
  function snapshot(domain?: ToolsDomain) {
    if (!domain || domain === 'tools') emit({ type: 'tools/state', payload: structuredClone(tools) });
    if (!domain || domain === 'skills') emit({ type: 'skills/state', payload: structuredClone(skills) });
    if (!domain || domain === 'mcp') emit({ type: 'mcp/state', payload: { servers: structuredClone(servers), restartRequired } });
    if (!domain || domain === 'catalog') emit({ type: 'catalog/state', payload: catalogState() });
    if (!domain || domain === 'rules') {
      emit({ type: 'rules/state', payload: structuredClone(rules) });
      emit({ type: 'rules/active', payload: { sessionId: tools.sessionId, rules: rules.rules.filter(rule => rule.alwaysApply || rule.globs.length > 0).map(rule => ({ name: rule.name, file: rule.file, always: rule.alwaysApply, globs: rule.globs })) } });
    }
    if (!domain || domain === 'workflows') { const payload: WorkflowsStatePayload = { workflows: structuredClone(workflows), diagnostics: [] }; emit({ type: 'workflows/state', payload }); }
    if (!domain || domain === 'hooks') { const payload: HooksStatePayload = { hooks: structuredClone(hooks), diagnostics: [], policy: hooksPolicy }; emit({ type: 'hooks/state', payload }); }
  }
  function reply(domain: ToolsDomain, requestId: string, error?: string, output?: string, document?: ResourceDocument) { emit({ type: `${domain}/reply`, payload: { requestId, ok: !error, error, output, document } }); }
  // Tools › 권한 (permission/rules) answers through the same emit.
  const permissions = createMockPermissions(payload => emit({ type: 'permission/rulesState', payload } as unknown as ToolsHostMessage));
  function handle(message: { type: string; payload?: unknown }): boolean {
    if (message.type === 'permission/rules') { permissions(message.payload as never); return true; }
    if (!['tools/command', 'skills/command', 'mcp/command', 'catalog/command', 'rules/command', 'workflows/command', 'hooks/command'].includes(message.type)) return false;
    const domain = message.type.split('/')[0] as ToolsDomain;
    const command = message.payload as ToolsCommand | SkillsCommand | McpCommand | CatalogCommand | RulesCommand | WorkflowsCommand | HooksCommand;
    if (!command || typeof command.requestId !== 'string' || typeof command.action !== 'string') return true;
    if (pendingDomains.has(domain) || (options.busy?.() && command.action !== 'refresh')) { reply(domain, command.requestId, 'A previous request is still in progress.'); return true; }
    pendingDomains.add(domain);
    if (domain === 'tools') { tools = { ...tools, pending: true }; snapshot('tools'); }
    const namePattern = /^[a-z0-9-]{2,40}$/;
    const skillDocument = (source: string, name: string) => bodies.get(`skill:${source}:${name}`) ?? bodies.get(`skill:project:${name}`) ?? bodies.get(`skill:user:${name}`) ?? bodies.get(`skill:installed:${name}`) ?? bodies.get(`skill:omp:${name}`) ?? bodies.get(`skill:pomp:${name}`);
    function skillForm(content: string): { description: string; body: string } | undefined {
      const match = /^---\r?\n([\s\S]*?)\r?\n---\r?\n([\s\S]*)$/.exec(content);
      if (!match) return undefined;
      const keys = match[1].split('\n').map(line => line.split(':')[0]?.trim()).filter(Boolean);
      if (!keys.length || keys.some(key => key !== 'name' && key !== 'description')) return undefined;
      const description = (/^description:\s*(.+)$/m.exec(match[1])?.[1] ?? '').trim();
      return { description, body: match[2] };
    }
    function ruleForm(content: string): { description: string; alwaysApply: boolean; globs: string[]; body: string } | undefined {
      const match = /^---\r?\n([\s\S]*?)\r?\n---\r?\n([\s\S]*)$/.exec(content);
      if (!match) return undefined;
      const keys = match[1].split('\n').map(line => line.split(':')[0]?.trim()).filter(Boolean);
      if (!keys.length || keys.some(key => key !== 'description' && key !== 'alwaysApply' && key !== 'globs')) return undefined;
      const description = (/^description:\s*"(.*)"\s*$/m.exec(match[1])?.[1] ?? '').trim();
      const alwaysApply = /^alwaysApply:\s*true\s*$/m.test(match[1]);
      const globsRaw = /^globs:\s*\[(.*)\]\s*$/m.exec(match[1])?.[1] ?? '';
      return { description, alwaysApply, globs: [...globsRaw.matchAll(/"([^"]*)"/g)].map(entry => entry[1]), body: match[2] };
    }
    setTimeout(() => {
      let error: string | undefined; let output: string | undefined; let document: ResourceDocument | undefined;
      if (domain === 'tools') {
        const p = command as ToolsCommand;
        if (p.action === 'set') {
          if (!Array.isArray(p.active) || p.active.some(name => !tools.tools.some(tool => tool.name === name))) error = 'Unknown tool selection.';
          else tools = { ...tools, tools: tools.tools.map(tool => ({ ...tool, active: p.active!.includes(tool.name) })) };
        } else if (p.action === 'reset') tools = { ...tools, tools: tools.tools.map(tool => ({ ...tool, active: tool.defaultActive })) };
        tools = { ...tools, pending: false };
      } else if (domain === 'skills') {
        const p = command as SkillsCommand;
        const existing = skills.skills.find(skill => skill.name === p.name && (p.scope ? skill.source === p.scope : true));
        if (p.action === 'create') {
          if (!p.name || !namePattern.test(p.name) || !['project', 'user'].includes(p.scope ?? '')) error = 'Invalid skill name or scope.';
          else if (skills.skills.some(skill => skill.name === p.name && skill.source === p.scope)) error = 'The skill already exists.';
          else {
            const record = { name: p.name, source: p.scope!, description: p.description ?? '', file: `C:\\mock-${p.scope}\\.pomp\\skills\\${p.name}\\SKILL.md`, invocable: true, hash: `skill-${++revision}` };
            skills = { ...skills, skills: [...skills.skills, record] };
            bodies.set(`skill:${p.scope}:${p.name}`, { content: `---\nname: ${p.name}\ndescription: ${p.description ?? ''}\n---\n${p.body ?? '# Skill\nStarter body.'}`, hash: `skill-${revision}-doc` });
          }
        } else if (p.action === 'read') {
          const stored = p.name ? skillDocument(p.scope ?? '', p.name) : undefined;
          if (!existing || !stored) error = 'Skill not found.';
          else if (!existing.file) error = 'This row has no file to read.';
          else document = { ...stored, form: skillForm(stored.content) };
        } else if (p.action === 'update') {
          const stored = p.name ? skillDocument(p.scope ?? '', p.name) : undefined;
          if (!existing || !stored || (existing.source !== 'project' && existing.source !== 'user')) error = 'Skill not found.';
          else if (!p.expectedHash || stored.hash !== p.expectedHash) error = 'Skill hash conflict. Refresh first.';
          else {
            const content = p.content ?? (p.description !== undefined ? `---\nname: ${p.name}\ndescription: ${p.description}\n---\n${p.body ?? ''}` : stored.content);
            if (p.content && !/^---\r?\nname: /.test(p.content)) error = 'The declared name must stay the same.';
            else {
              const next = `skill-${++revision}-doc`;
              bodies.set(`skill:${existing.source}:${existing.name}`, { content, hash: next });
              if (p.description !== undefined) skills = { ...skills, skills: skills.skills.map(item => item === existing ? { ...item, description: p.description! } : item) };
              document = { content, hash: next };
            }
          }
        } else if (p.action === 'delete') {
          const skill = skills.skills.find(skill => skill.name === p.name && skill.source === p.scope);
          if (!skill || !['project', 'user'].includes(skill.source) || !p.expectedHash || skill.hash !== p.expectedHash) error = 'Skill hash conflict. Refresh first.';
          else skills = { ...skills, skills: skills.skills.filter(item => item !== skill) };
        } else if (p.action === 'enable' || p.action === 'disable' || p.action === 'reset') {
          // A switch is kept by name; switching on also switches on what the skill requires, and reset puts the built-in rows back to their default.
          const names = new Set([p.name ?? '']);
          if (p.action === 'enable') for (const name of names) for (const required of skillRequires[name] ?? []) names.add(required);
          if (!skills.skills.some(skill => skill.name === p.name)) error = 'This skill is not in the list.';
          else skills = { ...skills, skills: skills.skills.map(skill => {
            if (!names.has(skill.name)) return skill;
            const on = p.action === 'reset' ? skill.defaultEnabled ?? skill.enabled !== false : p.action === 'enable';
            return { ...skill, enabled: on, invocable: on && (invocableWhenOn.get(`${skill.source}:${skill.name}`) ?? true) };
          }) };
        } else if (p.action === 'import') error = 'Markdown import requires the real host; this preview does not write skill files.';
        else if (p.action === 'open' && !skills.skills.some(skill => skill.name === p.name && (!p.scope || skill.source === p.scope))) error = 'Skill not found.';
      } else if (domain === 'rules') {
        const p = command as RulesCommand;
        const existing = p.name ? rules.rules.find(rule => rule.name === p.name && (!p.scope || rule.scope === p.scope)) : undefined;
        const scope = `C:\\mock-${p.scope ?? 'project'}`;
        const key = (name?: string) => `rule:${p.scope ?? 'project'}:${name}`;
        if (p.action === 'create') {
          if (!p.name || !namePattern.test(p.name) || !['project', 'user'].includes(p.scope ?? '')) error = 'Invalid rule name or scope.';
          else if (rules.rules.some(rule => rule.name === p.name && rule.scope === p.scope)) error = 'The rule already exists.';
          else {
            const record = { name: p.name, scope: p.scope!, file: `${scope}\\.pomp\\rules\\${p.name}.mdc`, hash: `rule-${++revision}`, description: p.description ?? '', alwaysApply: p.alwaysApply ?? false, globs: p.globs ?? [] };
            (rules as { rules: typeof rules.rules }).rules = [...rules.rules, record];
            bodies.set(key(p.name), { content: `---\ndescription: "${p.description ?? ''}"\nalwaysApply: ${p.alwaysApply ? 'true' : 'false'}\nglobs: []\n---\n${p.body ?? '# Rule\nStarter body.'}`, hash: `rule-${revision}-doc` });
          }
        } else if (p.action === 'read') {
          const stored = p.name ? bodies.get(key(p.name)) : undefined;
          if (!existing || !stored) error = 'Rule not found.';
          else document = { ...stored, form: ruleForm(stored.content) };
        } else if (p.action === 'update') {
          const stored = p.name ? bodies.get(key(p.name)) : undefined;
          if (!existing || !stored) error = 'Rule not found.';
          else if (!p.expectedHash || stored.hash !== p.expectedHash) error = 'Rule hash conflict. Refresh first.';
          else {
            const content = p.content ?? `---\ndescription: "${p.description ?? ''}"\nalwaysApply: ${p.alwaysApply ? 'true' : 'false'}\nglobs: []\n---\nbody`;
            const next = `rule-${++revision}-doc`;
            bodies.set(key(p.name), { content, hash: next });
            (rules as { rules: typeof rules.rules }).rules = rules.rules.map(item => item === existing ? { ...item, description: p.description ?? item.description, alwaysApply: p.alwaysApply ?? item.alwaysApply, globs: p.globs ?? item.globs } : item);
            document = { content, hash: next };
          }
        } else if (p.action === 'delete') {
          const rule = rules.rules.find(rule => rule.name === p.name && rule.scope === p.scope);
          if (!rule || !p.expectedHash || rule.hash !== p.expectedHash) error = 'Rule hash conflict. Refresh first.';
          else { (rules as { rules: typeof rules.rules }).rules = rules.rules.filter(item => item !== rule); bodies.delete(key(p.name)); }
        } else if (command.action !== 'refresh' && command.action !== 'import' && command.action !== 'open') error = 'Rule files can be changed only by the real host; this preview did not write anything.';
        else if (command.action === 'import') error = 'Rule import requires the real host; this preview does not write rule files.';
      } else if (domain === 'workflows') {
        const p = command as WorkflowsCommand;
        const draft = p.workflow as WorkflowDraft | undefined;
        const validDraft = (value?: WorkflowDraft) => !!value && (value.description?.length ?? 0) <= 500 && (value.task?.length ?? 0) >= 1 && (value.task?.length ?? 0) <= 16000 && Array.isArray(value.steps) && value.steps.length <= 32 && value.steps.every(step => step.length >= 1 && step.length <= 500);
        if (p.action === 'create') {
          if (!p.name || !namePattern.test(p.name) || !['project', 'user'].includes(p.scope ?? '') || !validDraft(draft)) error = 'Invalid workflow name, scope or content.';
          else if (workflows.some(workflow => workflow.name === p.name && workflow.scope === p.scope)) error = 'The workflow already exists.';
          else workflows = [...workflows, { name: p.name, scope: p.scope!, file: `C:\\mock-${p.scope}\\.pomp\\workflows\\${p.name}.json`, hash: `workflow-${++revision}`, description: draft!.description, task: draft!.task, steps: draft!.steps }];
        } else if (p.action === 'update') {
          const existing = workflows.find(workflow => workflow.name === p.name && workflow.scope === p.scope);
          if (!existing || !validDraft(draft)) error = existing ? 'Invalid workflow content.' : 'Workflow not found.';
          else if (!p.expectedHash || existing.hash !== p.expectedHash) error = 'Workflow hash conflict. Refresh first.';
          else workflows = workflows.map(item => item === existing ? { ...item, description: draft!.description, task: draft!.task, steps: draft!.steps, hash: `workflow-${++revision}` } : item);
        } else if (p.action === 'delete') {
          const existing = workflows.find(workflow => workflow.name === p.name && workflow.scope === p.scope);
          if (!existing || !p.expectedHash || existing.hash !== p.expectedHash) error = 'Workflow hash conflict. Refresh first.';
          else workflows = workflows.filter(item => item !== existing);
        } else if (p.action === 'open' && !workflows.some(workflow => workflow.name === p.name && workflow.scope === p.scope)) error = 'Workflow not found.';
      } else if (domain === 'hooks') {
        const p = command as HooksCommand;
        const draft = p.hook as HookDraft | undefined;
        const validDraft = (value?: HookDraft) => !!value && (value.event === 'onFileSave' || value.event === 'onBuild') && (value.description?.length ?? 0) <= 500 &&
          typeof value.glob === 'string' && value.glob.length > 0 && Number.isInteger(value.timeoutSeconds) && value.timeoutSeconds >= 1 && value.timeoutSeconds <= 3600 &&
          typeof value.script === 'string' && value.script.length >= 1 && value.script.length <= 6000;
        if (p.action === 'create') {
          if (!p.name || !namePattern.test(p.name) || !validDraft(draft)) error = 'Invalid hook name or content.';
          else if (hooks.some(hook => hook.id === p.name)) error = 'The hook already exists.';
          else hooks = [...hooks, { id: p.name!, name: p.name!, file: `C:\\workspace\\.pomp\\hooks\\${p.name}.md`, hash: `hook-${++revision}`, description: draft!.description, event: draft!.event, glob: draft!.glob, timeoutSeconds: draft!.timeoutSeconds, enabled: draft!.enabled, script: draft!.script }];
        } else if (p.action === 'update') {
          const existing = hooks.find(hook => hook.id === p.id);
          if (!existing || !validDraft(draft)) error = existing ? 'Invalid hook content.' : 'Hook not found.';
          else if (!p.expectedHash || existing.hash !== p.expectedHash) error = 'Hook hash conflict. Refresh first.';
          else hooks = hooks.map(item => item === existing ? { ...item, description: draft!.description, event: draft!.event, glob: draft!.glob, timeoutSeconds: draft!.timeoutSeconds, enabled: draft!.enabled, script: draft!.script, hash: `hook-${++revision}` } : item);
        } else if (p.action === 'setEnabled') {
          const existing = hooks.find(hook => hook.id === p.id);
          if (!existing || typeof p.enabled !== 'boolean') error = 'Hook not found.';
          else if (!p.expectedHash || existing.hash !== p.expectedHash) error = 'Hook hash conflict. Refresh first.';
          else hooks = hooks.map(item => item === existing ? { ...item, enabled: p.enabled!, hash: `hook-${++revision}` } : item);
        } else if (p.action === 'delete') {
          const existing = hooks.find(hook => hook.id === p.id);
          if (!existing || !p.expectedHash || existing.hash !== p.expectedHash) error = 'Hook hash conflict. Refresh first.';
          else hooks = hooks.filter(item => item !== existing);
        } else if (p.action === 'open' && !hooks.some(hook => hook.id === p.id)) error = 'Hook not found.';
      } else if (domain === 'catalog') {
        const p = command as CatalogCommand;
        if (p.action === 'install') {
          // The host's rules: create-only, MCP entries only, the entry's scopes, a secret value (or a required one) only in the user scope, required inputs filled.
          const entry = catalogEntries.find(item => item.id === p.id && item.kind === 'mcp');
          const inputs = p.inputs ?? {}; const secrets = entry?.inputs.filter(input => input.secret) ?? [];
          if (!entry) error = 'This catalog entry does not exist.';
          else if (!p.scope || !entry.scopes.includes(p.scope)) error = 'This entry cannot be installed into this scope.';
          else if (servers.some(server => server.name === entry.id && server.scope === p.scope)) error = 'This server name already exists.';
          else if (p.scope === 'project' && secrets.some(input => (inputs[input.key] ?? '').length > 0)) error = 'With a secret value this installs into the user scope only.';
          else if (p.scope === 'project' && secrets.some(input => input.required)) error = 'This needs a secret value, so it installs into the user scope only.';
          else if (entry.inputs.some(input => input.required && !(inputs[input.key] ?? ''))) error = 'A required input is empty.';
          else {
            servers = [...servers, { name: entry.id, scope: p.scope, enabled: false, hash: `mcp-${++revision}`, catalogId: entry.id, ...catalogServers[entry.id](inputs) }];
            restartRequired = true; snapshot('mcp');
          }
        } else if (p.action !== 'refresh') error = 'Unknown catalog action.';
      } else {
        const p = command as McpCommand;
        const index = servers.findIndex(server => server.name === p.name && server.scope === p.scope);
        const existing = servers[index];
        if (p.action === 'save') {
          if (!p.server?.name || !p.scope) error = 'Invalid MCP server.';
          else if (existing && p.expectedHash !== existing.hash) error = 'MCP hash conflict. Refresh first.';
          else {
            // A masked value keeps the stored one; there is nothing to keep for a name that is new.
            const unknownMask = (values?: Record<string, string>, stored?: Record<string, string>) => Object.entries(values ?? {}).some(([name, value]) => value === MASKED_ENV && !(stored && name in stored));
            const server = { ...p.server, scope: p.scope, hash: `mcp-${++revision}`, catalogId: existing?.catalogId, env: p.server.env ? Object.fromEntries(Object.keys(p.server.env).map(name => [name, MASKED_ENV])) : undefined,
              headers: p.server.headers ? Object.fromEntries(Object.keys(p.server.headers).map(name => [name, MASKED_ENV])) : undefined };
            if (unknownMask(p.server.env, existing?.env) || unknownMask(p.server.headers, existing?.headers)) error = 'The value is invalid.';
            else {
              if (index < 0) servers = [...servers, server]; else servers = servers.map((item, position) => position === index ? server : item);
              restartRequired = true;
            }
          }
        } else if (['delete', 'enable', 'disable'].includes(p.action)) {
          if (!existing || !p.expectedHash || existing.hash !== p.expectedHash) error = 'MCP hash conflict. Refresh first.';
          else { servers = p.action === 'delete' ? servers.filter(item => item !== existing) : servers.map(item => item === existing ? { ...item, enabled: p.action === 'enable', hash: `mcp-${++revision}` } : item); restartRequired = true; }
        } else if (p.action === 'test') {
          if (!existing) error = 'MCP server not found.';
          else output = `MCP connection test: ${existing.name}\nTransport: ${existing.type}\nConnected successfully\n2 tools discovered\nEnvironment values: [redacted]\n${Array.from({ length: 18 }, (_, i) => `check ${i + 1}: passed`).join('\n')}`;
        } else if (p.action === 'restart') { restartRequired = false; tools = { ...tools, tools: tools.tools.map(tool => ({ ...tool, active: tool.defaultActive })) }; snapshot('tools'); }
      }
      pendingDomains.delete(domain); snapshot(domain); reply(domain, command.requestId, error, output, document);
    }, options.delay ?? 220);
    return true;
  }
  return Object.assign(handle, { snapshot, setHooksPolicy(policy: HooksStatePayload['policy']) { hooksPolicy = policy; snapshot('hooks'); },
    resetConversation(sessionId: string) { tools = { ...tools, sessionId, tools: tools.tools.map(tool => ({ ...tool, active: tool.defaultActive })) }; snapshot('tools'); snapshot('rules'); } });
}
