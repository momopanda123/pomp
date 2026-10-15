/**
 * The page's copy of how the host composes the files the in-page forms write, so a form can count the UTF-8 bytes the host will
 * write. The host limits bytes (32 KiB for skills, rules and workflows, 64 KiB for hooks), not characters: a Korean body of
 * 11,000 characters already makes a 33 KB file. Each compose function mirrors its C# counterpart in src/Pomp.Core/Features
 * (SkillCatalog / RuleCatalog / WorkflowCatalog / HookCatalog `Compose`); whole-text editors and imports count the text itself.
 */
export const SKILL_BYTES = 32768;
export const RULE_BYTES = 32768;
export const WORKFLOW_BYTES = 32768;
export const HOOK_BYTES = 65536;

const encoder = new TextEncoder();
/** UTF-8 length; a lone surrogate counts as the 3-byte replacement character, as the host's non-strict encoders write it. */
export const utf8Bytes = (text: string): number => encoder.encode(text).length;

// C# char.IsWhiteSpace, which string.Trim() strips (JS trim() differs on U+0085 and U+FEFF).
const isSpace = (code: number) => (code >= 9 && code <= 13) || code === 32 || code === 0x85 || code === 0xa0 || code === 0x1680 ||
  (code >= 0x2000 && code <= 0x200a) || code === 0x2028 || code === 0x2029 || code === 0x202f || code === 0x205f || code === 0x3000;
export function csTrim(text: string): string {
  let start = 0; let end = text.length;
  while (start < end && isSpace(text.charCodeAt(start))) start++;
  while (end > start && isSpace(text.charCodeAt(end - 1))) end--;
  return text.slice(start, end);
}

/** JsonConvert.SerializeObject(string) with Newtonsoft's defaults: quotes, backslash and control characters (and U+0085/2028/2029) are escaped, other text is not. */
export function jsonString(value: string): string {
  let out = '"'; let from = 0;
  for (let i = 0; i < value.length; i++) {
    const c = value.charCodeAt(i);
    const escaped = c === 0x22 ? '\\"' : c === 0x5c ? '\\\\' : c === 0x0a ? '\\n' : c === 0x0d ? '\\r' : c === 0x09 ? '\\t' : c === 0x0c ? '\\f' : c === 0x08 ? '\\b'
      : c < 0x20 || c === 0x85 || c === 0x2028 || c === 0x2029 ? `\\u${c.toString(16).padStart(4, '0')}` : undefined;
    if (escaped !== undefined) { out += value.slice(from, i) + escaped; from = i + 1; }
  }
  return `${out}${value.slice(from)}"`;
}

/** SkillCatalog.Compose: `body` is trimmed; an empty one becomes the starter text. */
export function composeSkill(name: string, description: string, body: string): string {
  const text = csTrim(body) || `# ${name}\n\nDescribe the steps and expected result here. This skill does not grant tool permissions.`;
  return `---\nname: ${jsonString(name)}\ndescription: ${jsonString(description)}\n---\n\n${text}\n`;
}

/** RuleCatalog.Compose: a flow list of trimmed globs, a trimmed body (CRLF → LF) or the starter text. */
export function composeRule(name: string, description: string, alwaysApply: boolean, globs: string[], body: string): string {
  const text = csTrim(body.replace(/\r\n/g, '\n')) || `# ${name}\n\nWrite the guidance for the agent here. Rules add guidance only; they never grant tool permissions.`;
  return `---\n${description.length > 0 ? `description: ${jsonString(description)}\n` : ''}alwaysApply: ${alwaysApply ? 'true' : 'false'}\n` +
    `${globs.length > 0 ? `globs: [${globs.map(glob => jsonString(csTrim(glob))).join(',')}]\n` : ''}---\n\n${text}\n`;
}

/** WorkflowCatalog.Compose: Newtonsoft's indented JSON (two spaces, "\n" line ends, an empty list as `[]`) plus a final "\n". */
export function composeWorkflow(description: string, task: string, steps: string[]): string {
  const list = steps.length > 0 ? `[\n${steps.map(step => `    ${jsonString(step)}`).join(',\n')}\n  ]` : '[]';
  return `{\n  "version": 1,\n  "description": ${jsonString(description)},\n  "task": ${jsonString(task)},\n  "steps": ${list}\n}\n`;
}

/** HookCatalog.Compose: scalar frontmatter and the (CRLF → LF, trimmed) script in one powershell fence. */
export function composeHook(displayName: string, description: string, event: string, enabled: boolean, glob: string, timeout: number, script: string): string {
  return `---\nname: ${jsonString(displayName)}\n${description.length > 0 ? `description: ${jsonString(description)}\n` : ''}event: ${event}\nenabled: ${enabled ? 'true' : 'false'}\n` +
    `glob: ${jsonString(glob)}\ntimeoutSeconds: ${Number.isInteger(timeout) ? timeout : 120}\n---\n\n\`\`\`powershell\n${csTrim(script.replace(/\r\n/g, '\n'))}\n\`\`\`\n`;
}

export interface ByteBudget { bytes: number; limit: number; over: boolean }
export function measureBytes(text: string, limit: number): ByteBudget {
  const bytes = utf8Bytes(text);
  return { bytes, limit, over: bytes > limit };
}
