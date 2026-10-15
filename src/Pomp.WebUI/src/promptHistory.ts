// The prompts sent from the chat input, per workspace: ↑ and ↓ in an empty input bring them back. Kept in this viewer's page
// storage (a convenience, so a blocked or cleared storage only means the history starts over), and in memory for this page.

const STORAGE_PREFIX = 'pomp.promptHistory:';
/** Kept by size, not by count: once the prompts pass this many characters together, the oldest go. */
const MAX_CHARS = 200_000;

const loaded = new Map<string, string[]>();

/** The prompts sent in this workspace, oldest first. */
export function promptHistory(workspace: string): readonly string[] {
  let entries = loaded.get(workspace);
  if (!entries) {
    entries = [];
    try {
      const value: unknown = JSON.parse(localStorage.getItem(STORAGE_PREFIX + workspace) || '[]');
      if (Array.isArray(value)) entries = value.filter((item): item is string => typeof item === 'string' && item.trim() !== '');
    } catch { /* unreadable storage: start over */ }
    loaded.set(workspace, entries);
  }
  return entries;
}

/** Adds a sent prompt (not again when it repeats the last one) and drops the oldest past the size budget. */
export function rememberPrompt(workspace: string, text: string): void {
  if (!text.trim()) return;
  const entries = [...promptHistory(workspace)];
  if (entries.at(-1) === text) return;
  entries.push(text);
  let total = entries.reduce((sum, item) => sum + item.length, 0);
  while (entries.length > 1 && total > MAX_CHARS) total -= entries.shift()!.length;
  loaded.set(workspace, entries);
  try { localStorage.setItem(STORAGE_PREFIX + workspace, JSON.stringify(entries)); } catch { /* full or blocked: this page still remembers */ }
}
