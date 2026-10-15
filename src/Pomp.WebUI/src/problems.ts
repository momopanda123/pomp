import type { WorkspaceProblem, WorkspaceProblemsState } from './protocol';
import { toolsTranslate } from './i18n/toolsStrings';

export const MAX_PROBLEMS = 50;
export const MAX_PROBLEM_CHARS = 12000;

/** Only current-workspace agent findings and compiler errors/warnings enter the shared store. */
export function normalizeProblems(value: WorkspaceProblemsState): WorkspaceProblemsState | null {
  if (!value || typeof value.root !== 'string' || !Array.isArray(value.items)) return null;
  const ids = new Set<string>();
  const items = value.items.filter(item => {
    if (!item || typeof item.id !== 'string' || !item.id || ids.has(item.id)
      || !['agent', 'compiler'].includes(item.source) || !['error', 'warning'].includes(item.severity)
      || typeof item.code !== 'string' || typeof item.message !== 'string' || !(item.code.trim() || item.message.trim())
      || typeof item.path !== 'string' || !Number.isSafeInteger(item.line) || item.line < 0) return false;
    ids.add(item.id); return true;
  });
  return { root: value.root, items, truncated: value.truncated === true,
    updatedUtc: typeof value.updatedUtc === 'string' ? value.updatedUtc : '' };
}

export function problemPath(root: string, path: string): string {
  const normalized = path.replaceAll('\\', '/'); const workspace = root.replaceAll('\\', '/').replace(/\/+$/u, '');
  return workspace && normalized.toLowerCase().startsWith(workspace.toLowerCase() + '/') ? normalized.slice(workspace.length + 1) : normalized;
}

/** Model-bound text; the diagnostic data stays verbatim. */
export function buildProblemsRequest(locale: string, root: string, items: WorkspaceProblem[]): string {
  const t = toolsTranslate(locale);
  return [t('problems.request'), ...items.map(item => {
    const label = t(item.source === 'agent' ? 'problems.agent' : item.severity === 'error' ? 'problems.error' : 'problems.warning');
    return `- [${label}] ${[item.code, item.message].filter(Boolean).join(' ')} (${problemPath(root, item.path)}:${item.line})`;
  })].join('\n');
}
