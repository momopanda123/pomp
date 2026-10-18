/** Small formatting helpers shared by the components. */

export function newId(): string {
  return globalThis.crypto?.randomUUID?.() ?? `${Date.now().toString(36)}-${Math.random().toString(36).slice(2, 10)}`;
}

const collapseWhitespace = (text: string): string => text.replace(/\s+/g, ' ').trim();

export function clip(text: string, max: number): string {
  return text.length > max ? text.slice(0, max) : text;
}

export function formatDuration(ms: number): string {
  if (ms < 1000) return `${Math.round(ms)}ms`;
  if (ms < 60_000) return `${(ms / 1000).toFixed(ms < 10_000 ? 1 : 0)}s`;
  return `${Math.floor(ms / 60_000)}m ${Math.round((ms % 60_000) / 1000)}s`;
}

export function formatCount(value: number): string {
  return value.toLocaleString('en-US');
}

/** Argument keys that usually identify what a tool call operates on, in order of preference. */
const PRIMARY_KEYS = ['command', 'cmd', 'path', 'file_path', 'filePath', 'file', 'pattern', 'query', 'url', 'name'];

function briefValue(value: unknown): string {
  if (typeof value === 'string') return collapseWhitespace(value);
  if (Array.isArray(value)) return `[${value.length}]`;
  if (value !== null && typeof value === 'object') return '{}';
  return String(value);
}

/** One-line summary of tool arguments for the collapsed card header. */
export function summarizeArgs(args: Record<string, unknown> | undefined, max = 100): string {
  if (!args) return '';
  const entries = Object.entries(args).filter(([, v]) => v !== undefined && v !== null && v !== '');
  if (entries.length === 0) return '';

  const primaryKey = PRIMARY_KEYS.find((key) => typeof args[key] === 'string' || typeof args[key] === 'number');
  const parts: string[] = [];
  if (primaryKey) parts.push(briefValue(args[primaryKey]));
  for (const [key, value] of entries) {
    if (key === primaryKey) continue;
    parts.push(`${key}=${briefValue(value)}`);
  }
  return clip(parts.join(' '), max);
}

/** Pretty JSON of the arguments for the expanded card (size-capped). */
export function prettyArgs(args: Record<string, unknown> | undefined, max = 4000): string {
  if (!args) return '';
  try {
    return clip(JSON.stringify(args, null, 2), max);
  } catch {
    return '';
  }
}

/** Copies text to the clipboard; falls back to execCommand where the async API is unavailable. */
export async function copyText(text: string): Promise<boolean> {
  try {
    await navigator.clipboard.writeText(text);
    return true;
  } catch {
    const area = document.createElement('textarea');
    area.value = text;
    area.style.cssText = 'position:fixed;left:-9999px;top:0;opacity:0';
    document.body.append(area);
    area.select();
    try {
      return document.execCommand('copy');
    } catch {
      return false;
    } finally {
      area.remove();
    }
  }
}
