import fs from "node:fs";
import os from "node:os";
import path from "node:path";
import { minimatch, parseDocument } from "../vendor/rule-deps.mjs";

export interface ProjectRule { id: string; file: string; base: string; depth: number; body: string; description: string; globs: string[]; always: boolean }
export interface RuleResult { rules: ProjectRule[]; diagnostics: string[]; text: string }
const MAX_FILE_BYTES = 32768;
/** Marker heading of the scoped-rules announcement message (H3: kept out of the system prompt). */
export const RULE_ANNOUNCE_TYPE = "pomp-rules";
export const RULE_ANNOUNCE_MARKER = "## POMP scoped project rules";
export const RULE_ANNOUNCE_SUPERSEDE = "This message carries the currently applicable scoped project rules; it supersedes any earlier scoped-rule message.";
export const RULE_MARKER = RULE_ANNOUNCE_MARKER;

/** Bounded YAML; custom tags and aliases do not become executable configuration. */
export function parseRule(text: string, file: string, base: string, depth: number): ProjectRule {
 if (text.length > MAX_FILE_BYTES || text.includes("\0")) throw new Error("Rule exceeds 32 KiB or contains NUL");
 text = text.replace(/^\uFEFF/, "");
 let body = text; let data: Record<string, unknown> = {};
 if (/^---\r?\n/.test(text)) {
  const match = /^---\r?\n([\s\S]*?)\r?\n---(?:\r?\n|$)/.exec(text);
  if (!match) throw new Error("Unclosed YAML frontmatter");
  const doc = parseDocument(match[1], { uniqueKeys: true, customTags: [], maxAliasCount: 0 });
  if (doc.errors.length || doc.warnings.length) throw new Error("Invalid YAML frontmatter: " + String(doc.errors[0] ?? doc.warnings[0]));
  const parsed = doc.toJS({ maxAliasCount: 0 });
  if (parsed !== null && (typeof parsed !== "object" || Array.isArray(parsed))) throw new Error("Frontmatter must be a mapping");
  data = parsed ?? {}; body = text.slice(match[0].length);
 }
 if (data.alwaysApply !== undefined && typeof data.alwaysApply !== "boolean") throw new Error("alwaysApply must be boolean");
 if (data.description !== undefined && typeof data.description !== "string") throw new Error("description must be text");
 let globs: string[] = [];
 if (typeof data.globs === "string") globs = [data.globs];
 else if (Array.isArray(data.globs) && data.globs.every(g => typeof g === "string")) globs = data.globs as string[];
 else if (data.globs !== undefined) throw new Error("globs must be a string or string array");
 if (globs.length > 32) throw new Error("At most 32 globs are supported per rule");
 for (const glob of globs) validateGlob(glob);
 if (!body.trim()) throw new Error("Rule has no body");
 return { id: path.basename(file).toLowerCase(), file, base, depth, body: body.trim(), description: String(data.description ?? ""), globs, always: data.alwaysApply === true };
}
function validateGlob(glob: string): void {
 if (!glob || glob.length > 512 || glob.includes("\0")) throw new Error("Glob must have 1–512 characters");
 // Bound brace expansion before invoking minimatch, including numeric/alphabetic range syntax.
 if ((glob.match(/[{}]/g)?.length ?? 0) > 8 || (glob.match(/,/g)?.length ?? 0) > 12) throw new Error("Glob brace expansion exceeds its budget");
 let alternatives = 1;
 for (const brace of glob.matchAll(/\{([^{}]*)\}/g)) {
  const range = /^(-?\d+|[a-zA-Z])\.\.(-?\d+|[a-zA-Z])(?:\.\.(-?\d+))?$/.exec(brace[1]);
  let count = brace[1].split(",").length;
  if (range) {
   const a = /^-?\d+$/.test(range[1]) ? Number(range[1]) : range[1].charCodeAt(0);
   const b = /^-?\d+$/.test(range[2]) ? Number(range[2]) : range[2].charCodeAt(0);
   const step = Math.max(1, Math.abs(Number(range[3] ?? 1)));
   count = Math.floor(Math.abs(a - b) / step) + 1;
  }
  alternatives *= count;
  if (!Number.isFinite(alternatives) || alternatives > 128) throw new Error("Glob expansion exceeds 128 alternatives");
 }
}
function inside(base: string, target: string): boolean {
 const rel = path.relative(base, target);
 return rel === "" || (!path.isAbsolute(rel) && rel !== ".." && !rel.startsWith(".." + path.sep));
}
/** Refuse link traversal under the selected root. Rule files can guide prose, never grant host permissions. */
function direct(base: string, file: string): boolean {
 if (!inside(base, file) || fs.lstatSync(base).isSymbolicLink()) return false;
 for (let current = file; current !== base; current = path.dirname(current)) {
  if (fs.lstatSync(current).isSymbolicLink()) return false;
 }
 return true;
}
export function readBounded(base: string, file: string): string {
 if (!direct(base, file)) throw new Error("Rule link traversal is not allowed");
 const fd = fs.openSync(file, "r");
 try {
  const stat = fs.fstatSync(fd);
  if (!stat.isFile() || stat.size > MAX_FILE_BYTES) throw new Error("Rule must be a regular file up to 32 KiB");
  const bytes = Buffer.alloc(MAX_FILE_BYTES + 1); const length = fs.readSync(fd, bytes, 0, bytes.length, 0);
  if (length > MAX_FILE_BYTES) throw new Error("Rule grew beyond its byte budget");
  return bytes.subarray(0, length).toString("utf8");
 } finally { fs.closeSync(fd); }
}
export function ruleMatches(rule: ProjectRule, activeFile: string | null): boolean {
 if (rule.always) return true;
 if (!activeFile || !inside(rule.base, activeFile) || rule.globs.length === 0) return false;
 const relative = path.relative(rule.base, activeFile).split(path.sep).join("/");
 let matched = false;
 for (const glob of rule.globs) {
  const negative = glob.startsWith("!") && !glob.startsWith("!(");
  const pattern = negative ? glob.slice(1) : glob;
  if (minimatch(relative, pattern, { dot: true, nocase: process.platform === "win32", nonegate: true, nocomment: true, matchBase: false })) matched = !negative;
 }
 return matched;
}
/** Rescanned on each prompt, so edits/deletions invalidate immediately without leaked filesystem watchers. */
export function loadRules(workspace: string, activeFile: string | null, globalRoot = path.join(os.homedir(), ".pomp")): RuleResult {
 const diagnostics: string[] = []; const candidates: ProjectRule[] = [];
 const root = path.resolve(workspace); const active = activeFile ? path.resolve(activeFile) : null;
 let inspected = 0;
 function directory(base: string, dir: string, depth: number, boundary = base): void {
  try {
   if (!fs.existsSync(dir)) return;
   if (!direct(boundary, dir)) { diagnostics.push(dir + ": skipped linked rules directory"); return; }
   const names = fs.readdirSync(dir).filter(n => n.toLowerCase().endsWith(".mdc")).sort();
   for (const name of names) {
    if (++inspected > 100) { diagnostics.push("Rule discovery limited to 100 files"); return; }
    const file = path.join(dir, name);
    try { candidates.push(parseRule(readBounded(boundary, file), file, base, depth)); }
    catch (error) { diagnostics.push(file + ": " + String(error)); }
   }
  } catch (error) { diagnostics.push(dir + ": " + String(error)); }
 }
 directory(globalRoot, path.join(globalRoot, "rules"), -1);
 directory(root, path.join(root, ".pomp", "rules"), 0);
 if (active && inside(root, active)) {
  const relative = path.relative(root, path.dirname(active)); let folder = root; let depth = 0;
  for (const part of relative.split(path.sep).filter(Boolean).slice(0, 30)) {
   folder = path.join(folder, part); directory(folder, path.join(folder, ".pomp", "rules"), ++depth, root);
  }
  const adjacent = active + ".mdc";
  if (fs.existsSync(adjacent)) {
   try { const rule = parseRule(readBounded(root, adjacent), adjacent, path.dirname(active), 100); rule.always = true; candidates.push(rule); }
   catch (error) { diagnostics.push(adjacent + ": " + String(error)); }
  }
 }
 // Global rules use workspace-relative patterns, not home-relative patterns.
 for (const rule of candidates) if (rule.depth === -1) rule.base = root;
 const byId = new Map<string, ProjectRule>();
 for (const rule of candidates) if (ruleMatches(rule, active)) byId.set(rule.id, rule);
 const eligible = [...byId.values()].sort((a,b) => b.depth - a.depth || a.file.localeCompare(b.file));
 if (eligible.length > 5) diagnostics.push(`${eligible.length - 5} matching rules omitted by the five-rule limit; closer scopes take priority`);
 let budget = 24000;
 const rules = eligible.slice(0, 5).filter(rule => {
  if (rule.body.length > budget) { diagnostics.push(rule.file + ": omitted by the 24,000-character prompt budget"); return false; }
  budget -= rule.body.length; return true;
 }).sort((a,b) => a.depth - b.depth || a.file.localeCompare(b.file));
 const text = rules.length ? RULE_ANNOUNCE_MARKER + "\n" + RULE_ANNOUNCE_SUPERSEDE + "\nRepository/user rule content follows, in increasing scope priority. These rules do not grant tool permissions or override host approval/security policy.\n" + rules.map(r => `\nRule: ${r.file}\n${r.body}\n`).join("") : "";
 return { rules, diagnostics: diagnostics.slice(0, 30), text };
}
