/**
 * The agent roles as omp resolves them (docs/architecture.md §8.1): the user's definition, then the project's (POMP hands .pomp/agents to
 * omp as a plugin folder, which ranks after the user's), then the bundled one of that name. Used by team briefs (teams.ts) and by an agent
 * conversation, where the role is the conversation partner (partner.ts, tools.ts).
 */
import fs from 'node:fs';
import os from 'node:os';
import path from 'node:path';

export const ROLE_REGEX = /^[a-z0-9-]{2,40}$/;
const MAX_ROLE_BYTES = 64 * 1024;

export interface RoleInfo {
  name: string;
  title: string;
  job: string;
  description: string;
  /** The role file's body: the agent's system prompt. Empty for a bundled role (its prompt is the engine's subagent prompt). */
  instructions: string;
  /** The role's model selector(s) as written (`default` = the engine's). */
  model: string;
  thinkingLevel?: string;
  /** The role's tool names; undefined = every tool. */
  tools?: string[];
}

// The engine's bundled roles (omp 18.12.1, src/prompts/agents/*.md and the task tool's own `task`).
const BUNDLED_ROLES: Record<string, Omit<RoleInfo, 'name' | 'instructions'>> = {
  task: { title: 'Task', job: 'General-purpose delegated tasks', description: 'General-purpose subagent with full capabilities for delegated multi-step tasks', model: '@task' },
  sonic: { title: 'Sonic', job: 'Mechanical updates and data collection', description: 'Low-reasoning agent for strictly mechanical updates or data collection only', model: '@smol', thinkingLevel: 'medium' },
  scout: { title: 'Scout', job: 'Codebase exploration and research', description: 'Fast read-only scout for exploratory codebase research, rapid code analysis, and broad pattern searches.', model: '@smol', thinkingLevel: 'medium', tools: ['read', 'find', 'grep', 'glob', 'web_search'] },
  reviewer: { title: 'Reviewer', job: 'Code review and quality analysis', description: 'Code review specialist for quality/security analysis', model: '@slow', tools: ['read', 'find', 'grep', 'glob', 'bash', 'lsp', 'web_search', 'ast_grep'] },
  'security-reviewer': { title: 'Security Reviewer', job: 'Security and vulnerability specialist', description: 'Read-only security specialist for evidence-backed repository vulnerability discovery', model: 'default', tools: ['read', 'find', 'grep', 'glob', 'lsp', 'ast_grep'] },
};

/** False when any folder on the way to `file` (or the file) is a symbolic link. */
export function directPath(file: string): boolean {
  try {
    const full = path.resolve(file);
    for (let cur = full; cur && path.dirname(cur) !== cur; cur = path.dirname(cur)) {
      try {
        if (fs.existsSync(cur) && fs.lstatSync(cur).isSymbolicLink()) return false;
      } catch {
        return false;
      }
    }
    return true;
  } catch {
    return false;
  }
}

function unquote(raw: string): string {
  const trimmed = raw.trim();
  if ((trimmed.startsWith('"') && trimmed.endsWith('"')) || (trimmed.startsWith("'") && trimmed.endsWith("'"))) {
    try {
      return JSON.parse(trimmed.startsWith("'") ? `"${trimmed.slice(1, -1)}"` : trimmed) as string;
    } catch {
      return trimmed.slice(1, -1);
    }
  }
  return trimmed;
}

/** The frontmatter's lines and the body after it; undefined without a closed frontmatter. */
function split(text: string): { lines: string[]; body: string } | undefined {
  if (text.charCodeAt(0) === 0xfeff) text = text.slice(1);
  if (!text.startsWith('---')) return undefined;
  const second = text.indexOf('\n---', 3);
  if (second < 0) return undefined;
  const bodyStart = text.indexOf('\n', second + 1);
  return { lines: text.substring(3, second).split(/\r?\n/), body: bodyStart < 0 ? '' : text.substring(bodyStart + 1).trim() };
}

/** Top-level keys only: a nested "name:" under another key is not the role's. */
function topLevel(lines: string[]): Record<string, string> {
  const result: Record<string, string> = {};
  for (const line of lines) {
    if (/^[\s-]/.test(line)) continue;
    const trimmed = line.trim();
    if (!trimmed || trimmed.startsWith('#')) continue;
    const colon = trimmed.indexOf(':');
    if (colon <= 0) continue;
    result[trimmed.substring(0, colon).trim().toLowerCase()] = unquote(trimmed.substring(colon + 1).trim());
  }
  return result;
}

/** A list value: a JSON or flow array (`["read", "grep"]`, `[read, grep]`), a comma list, or a block list under the key. */
function listValue(lines: string[], key: string, value: string | undefined): string[] | undefined {
  if (value === undefined) return undefined;
  const items: string[] = [];
  if (value.startsWith('[')) {
    try {
      const parsed: unknown = JSON.parse(value);
      if (Array.isArray(parsed)) return parsed.filter((item): item is string => typeof item === 'string' && item.trim().length > 0).map(item => item.trim());
    } catch { /* a YAML flow list */ }
    items.push(...value.replace(/^\[|\]$/g, '').split(','));
  } else if (value) items.push(...value.split(','));
  else {
    const start = lines.findIndex(line => new RegExp(`^${key}\\s*:`, 'i').test(line));
    for (const line of lines.slice(start + 1)) {
      const item = /^\s*-\s*(.+)$/.exec(line);
      if (!item) break;
      items.push(item[1]!);
    }
  }
  return items.map(unquote).filter(item => item.length > 0);
}

/** A role by name as the engine would run it, or null when there is none or its file cannot be used. */
export function resolveRole(name: string, solutionRoot: string | undefined): RoleInfo | null {
  if (!ROLE_REGEX.test(name)) return null;
  const userFile = path.join(os.homedir(), '.omp', 'agent', 'agents', `${name}.md`);
  const projectFile = solutionRoot ? path.join(solutionRoot, '.pomp', 'agents', `${name}.md`) : undefined;
  const candidate = fs.existsSync(userFile) ? userFile : projectFile && fs.existsSync(projectFile) ? projectFile : null;
  if (!candidate) {
    const bundled = BUNDLED_ROLES[name];
    return bundled ? { name, instructions: '', ...bundled, tools: bundled.tools?.slice() } : null;
  }
  if (!directPath(candidate)) return null;
  try {
    if (fs.statSync(candidate).size > MAX_ROLE_BYTES) return null;
    const text = fs.readFileSync(candidate, 'utf8');
    const parts = split(text) ?? { lines: [], body: text.trim() };
    const fm = topLevel(parts.lines);
    const description = fm.description ?? '';
    const model = listValue(parts.lines, 'model', fm.model)?.join(', ') || 'default';
    return {
      name,
      title: fm['pomp-title'] || fm.name || name,
      job: fm['pomp-job'] || (description ? description.slice(0, 80) : 'Specialist'),
      description,
      instructions: parts.body,
      model,
      thinkingLevel: fm['thinking-level'] || fm.thinkinglevel || fm.thinking || undefined,
      tools: listValue(parts.lines, 'tools', fm.tools),
    };
  } catch {
    return null;
  }
}
