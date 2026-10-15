/**
 * Shared helpers for the pomp omp extensions: host detection, the vs-state.json
 * reader and Windows path matching. Zero dependencies (Node built-ins only, which
 * Bun provides). Nothing here may throw to callers: a broken or missing state file
 * must never break a tool call, so every reader fails open.
 */
import * as fs from "node:fs";
import * as os from "node:os";
import * as path from "node:path";
import { fileURLToPath } from "node:url";

const win = path.win32;
const MAX_STATE_BYTES = 4 * 1024 * 1024;

export type Env = Record<string, string | undefined>;

export interface VsDocument {
	path: string;
	isDirty: boolean;
}

/** Subset of the host-written `${POMP_STATE_DIR}/vs-state.json` (version 1) used here. */
export interface VsState {
	solutionPath: string | null;
	solutionRoot: string | null;
	activeDocument: string | null;
	openDocuments: VsDocument[];
	updatedAt: string | null;
}

/** True when omp was launched by the pomp Visual Studio host (`POMP_HOST=1`). */
export function isPompHost(env: Env = process.env): boolean {
	const value = env.POMP_HOST?.trim().toLowerCase();
	return value === "1" || value === "true";
}

/** Absolute path of vs-state.json, or undefined when POMP_STATE_DIR is not set. */
export function stateFilePath(env: Env = process.env): string | undefined {
	const dir = env.POMP_STATE_DIR?.trim();
	return dir ? path.join(dir, "vs-state.json") : undefined;
}

export type PreEditMode = "block" | "warn" | "off";

/** Pre-edit guard mode from `POMP_PRE_EDIT_MODE`: block (default), warn, or off. */
export function preEditMode(env: Env = process.env): PreEditMode {
	const value = env.POMP_PRE_EDIT_MODE?.trim().toLowerCase();
	return value === "warn" || value === "off" ? value : "block";
}

export type NetworkMode = "allow" | "block";

/** omp network tool policy from `POMP_NETWORK` (the Visual Studio option): allow (default) or block. */
export function networkMode(env: Env = process.env): NetworkMode {
	return env.POMP_NETWORK?.trim().toLowerCase() === "block" ? "block" : "allow";
}

/**
 * Host tools the pomp host offered to omp in this process (`POMP_HOST_TOOLS`, comma-separated registry names). Absent or empty
 * means none are known: texts built from it then name no host tool (conservative wording). Callers must not depend on the order.
 */
export function offeredHostTools(env: Env = process.env): ReadonlySet<string> {
	const tools = new Set<string>();
	for (const part of (env.POMP_HOST_TOOLS ?? "").split(",")) {
		const name = part.trim();
		if (name) tools.add(name);
	}
	return tools;
}

function isRecord(value: unknown): value is Record<string, unknown> {
	return typeof value === "object" && value !== null && !Array.isArray(value);
}

function stringOrNull(value: unknown): string | null {
	return typeof value === "string" && value.trim().length > 0 ? value : null;
}

const RETRY_DELAY_MS = 15;
const MAX_RETRIES = 2;

function sleepSync(ms: number): void {
	try {
		Atomics.wait(new Int32Array(new SharedArrayBuffer(4)), 0, 0, ms);
	} catch {
		// no blocking wait available: retry immediately
	}
}

/**
 * Reads and parses the state file. The host replaces it atomically, which on Windows can briefly
 * surface as ENOENT / sharing violations for a reader, so those (and a torn read) are retried
 * a couple of times before giving up.
 */
function readStateJson(file: string): unknown {
	for (let attempt = 0; ; attempt++) {
		try {
			if (fs.statSync(file).size > MAX_STATE_BYTES) return undefined;
			let text = fs.readFileSync(file, "utf8");
			if (text.charCodeAt(0) === 0xfeff) text = text.slice(1);
			return JSON.parse(text);
		} catch (error) {
			const code = (error as NodeJS.ErrnoException).code;
			const transient =
				error instanceof SyntaxError || code === "ENOENT" || code === "EBUSY" || code === "EPERM" || code === "EACCES";
			if (!transient || attempt >= MAX_RETRIES) return undefined;
			sleepSync(RETRY_DELAY_MS);
		}
	}
}

/** Property lookup that also accepts other casings (a PascalCase serializer must not silently disable the guard). */
function prop(record: Record<string, unknown>, name: string): unknown {
	if (name in record) return record[name];
	const lower = name.toLowerCase();
	for (const key of Object.keys(record)) if (key.toLowerCase() === lower) return record[key];
	return undefined;
}

/** Reads vs-state.json. Returns undefined when missing, unreadable, oversized or malformed. */
export function readVsState(env: Env = process.env): VsState | undefined {
	try {
		const file = stateFilePath(env);
		if (!file) return undefined;
		const json = readStateJson(file);
		if (!isRecord(json)) return undefined;
		const documents: VsDocument[] = [];
		const rawDocuments = prop(json, "openDocuments");
		if (Array.isArray(rawDocuments)) {
			for (const doc of rawDocuments) {
				if (!isRecord(doc)) continue;
				const docPath = prop(doc, "path");
				if (typeof docPath === "string" && docPath.trim()) {
					documents.push({ path: docPath, isDirty: prop(doc, "isDirty") === true });
				}
			}
		}
		return {
			solutionPath: stringOrNull(prop(json, "solutionPath")),
			solutionRoot: stringOrNull(prop(json, "solutionRoot")),
			activeDocument: stringOrNull(prop(json, "activeDocument")),
			openDocuments: documents,
			updatedAt: stringOrNull(prop(json, "updatedAt")),
		};
	} catch {
		return undefined;
	}
}

// ---------------------------------------------------------------------------
// Path matching (Windows semantics: separators, case, extended prefix, relative paths)
// ---------------------------------------------------------------------------

/** Any whitespace other than a plain space (NBSP, ideographic space, ...) is folded to a space, like omp does. */
const NON_PLAIN_SPACE = /[^\S ]/g;
/** Scheme of an internal or remote URL (xd://, local://, https://); one-letter schemes are drive letters. */
const URL_LIKE = /^[a-z][a-z0-9+.-]+:\/\//i;

/** `\\?\C:\x` becomes `C:\x` and `\\?\UNC\server\share` becomes `\\server\share`. */
function stripExtendedPrefix(value: string): string {
	if (/^\\\\\?\\UNC\\/i.test(value)) return `\\\\${value.slice(8)}`;
	return value.startsWith("\\\\?\\") ? value.slice(4) : value;
}

/** MSYS/WSL drive aliases (`/c/x`, `/mnt/c/x`) to native paths, as omp's tools do on Windows. */
function driveAlias(value: string): string | undefined {
	if (!value.startsWith("/")) return undefined;
	const parts = value.split("/");
	let drive: string | undefined;
	let tailStart = 2;
	if (parts.length >= 2 && /^[a-z]$/i.test(parts[1] ?? "")) {
		drive = (parts[1] as string).toUpperCase();
	} else if (parts.length >= 3 && (parts[1] ?? "").toLowerCase() === "mnt" && /^[a-z]$/i.test(parts[2] ?? "")) {
		drive = (parts[2] as string).toUpperCase();
		tailStart = 3;
	}
	if (!drive) return undefined;
	const tail = parts.slice(tailStart).filter(Boolean).join("\\");
	return tail ? `${drive}:\\${tail}` : `${drive}:\\`;
}

/**
 * Resolves a model/user supplied path to an absolute Windows path, or undefined for non-disk targets.
 * Mirrors omp's own tool path handling (stray leading ':', '@' shorthand, NBSP, file:// URLs,
 * extended-length prefix, '~', /c/... aliases) so a quirky spelling that omp accepts cannot slip
 * past the guard.
 */
function toAbsolute(input: string, cwd: string): string | undefined {
	let value = input.replace(NON_PLAIN_SPACE, " ").trim();
	if (value.length === 0 || value.includes("\0")) return undefined;
	if (/^:(?=[/\\~]|\.\.?[/\\]|[A-Za-z]:)/.test(value)) value = value.slice(1);
	if (value.startsWith("@")) {
		const rest = value.slice(1);
		if (rest.startsWith("/") || rest === "~" || rest.startsWith("~/") || win.isAbsolute(rest) || URL_LIKE.test(rest)) {
			value = rest;
		}
	}
	if (/^file:\/\//i.test(value)) {
		try {
			value = fileURLToPath(value);
		} catch {
			return undefined;
		}
	} else if (URL_LIKE.test(value)) {
		return undefined;
	}
	value = stripExtendedPrefix(value);
	const home = os.homedir();
	if (value === "~") value = home;
	else if (value.startsWith("~/") || value.startsWith("~\\")) value = home + value.slice(1);
	else if (value.startsWith("~")) value = path.join(home, value.slice(1));
	value = driveAlias(value) ?? value;
	return win.resolve(cwd || process.cwd(), value);
}

/** Comparison key: forward slashes, no trailing slash, lower case. */
function keyOf(absolute: string): string {
	return absolute.replace(/\\/g, "/").replace(/\/+$/, "").toLowerCase();
}

/** Key of the real path of an existing absolute path (junctions, subst drives and 8.3 names resolved); undefined otherwise. */
function realKeyOf(absolute: string): string | undefined {
	try {
		return keyOf(win.normalize(stripExtendedPrefix(fs.realpathSync.native(absolute))));
	} catch {
		return undefined; // not on disk (new file) or not resolvable: the lexical key is all there is
	}
}

/** Keys for a path: the lexical one plus its real path (junctions, subst drives, 8.3 names) when it exists. */
function pathKeys(input: string, cwd: string): string[] {
	const absolute = toAbsolute(input, cwd);
	if (!absolute) return [];
	const keys = [keyOf(absolute)];
	const realKey = realKeyOf(absolute);
	if (realKey !== undefined && realKey !== keys[0]) keys.push(realKey);
	return keys;
}

const GLOB_CHARS = /[*?[\]{}]/;

/** Converts a lower-cased, forward-slash glob into regular expression source (no anchors); undefined when malformed. */
function globSource(glob: string): string | undefined {
	let source = "";
	let braces = 0;
	for (let i = 0; i < glob.length; i++) {
		const c = glob[i] as string;
		if (c === "*") {
			if (glob[i + 1] === "*") {
				i++;
				if (glob[i + 1] === "/") {
					i++;
					source += "(?:.*/)?";
				} else {
					source += ".*";
				}
			} else {
				source += "[^/]*";
			}
		} else if (c === "?") {
			source += "[^/]";
		} else if (c === "{") {
			braces++;
			source += "(?:";
		} else if (c === "}" && braces > 0) {
			braces--;
			source += ")";
		} else if (c === "," && braces > 0) {
			source += "|";
		} else if (c === "[") {
			const end = glob.indexOf("]", i + 2);
			if (end < 0) {
				source += "\\[";
			} else {
				const body = glob.slice(i + 1, end);
				source += `[${body.startsWith("!") ? `^${body.slice(1)}` : body}]`;
				i = end;
			}
		} else {
			source += c.replace(/[.+^$()|\\]/g, "\\$&");
		}
	}
	if (braces !== 0) return undefined;
	try {
		new RegExp(source); // a malformed class (`[z-a]`) must not throw later
		return source;
	} catch {
		return undefined;
	}
}

function escapeRegExp(text: string): string {
	return text.replace(/[.*+?^${}()|[\]\\]/g, "\\$&");
}

/**
 * Matchers for a scope with glob syntax, applied to comparison keys. The fixed directory prefix (the segments before the first
 * one with glob syntax) is matched literally, both as spelled and as its real path: `alias/*.ts` with `alias` a junction to
 * `real` covers `real/Dirty.ts` (which is what a dirty document is reported as), and the prefix may itself contain glob
 * characters (legal in Windows names), so it is never interpreted as a pattern. The rest of the scope is the glob.
 */
function globMatchers(absolute: string): RegExp[] {
	const root = win.parse(absolute).root;
	const segments = absolute.slice(root.length).split("\\");
	const first = segments.findIndex(segment => GLOB_CHARS.test(segment));
	if (first < 0) return [];
	const source = globSource(keyOf(segments.slice(first).join("\\")));
	if (source === undefined) return [];
	const prefix = `${root}${segments.slice(0, first).join("\\")}`;
	const prefixKeys = [keyOf(prefix)];
	const realPrefix = realKeyOf(prefix);
	if (realPrefix !== undefined && realPrefix !== prefixKeys[0]) prefixKeys.push(realPrefix);
	return prefixKeys.map(key => new RegExp(`^${escapeRegExp(key)}/${source}$`, "i"));
}

/**
 * omp's `normalizePathLikeInput` for search scopes (`ast_edit` `paths`, `search`, ...): trim, then drop one pair of surrounding
 * DOUBLE quotes (a path pasted from Explorer). Single quotes are valid in file names and are kept.
 */
function normalizeScopeEntry(entry: string): string {
	const text = entry.trim();
	return text.length > 1 && text.startsWith('"') && text.endsWith('"') ? text.slice(1, -1) : text;
}

type SplitMode = "semicolon" | "comma" | "whitespace" | "mixed";
const SPLIT_MODES: readonly SplitMode[] = ["semicolon", "comma", "whitespace", "mixed"];

function isSeparator(ch: string, mode: SplitMode): boolean {
	switch (mode) {
		case "semicolon":
			return ch === ";";
		case "comma":
			return ch === ",";
		case "whitespace":
			return /\s/.test(ch);
		case "mixed":
			return ch === ";" || ch === "," || /\s/.test(ch);
	}
}

/** Splits at top-level separators; `,` inside `{}` is glob syntax. */
function splitTopLevel(entry: string, mode: SplitMode): string[] {
	const parts: string[] = [];
	let depth = 0;
	let start = 0;
	for (let i = 0; i < entry.length; i++) {
		const ch = entry[i] as string;
		if (ch === "{") depth++;
		else if (ch === "}") depth = Math.max(0, depth - 1);
		else if (depth === 0 && isSeparator(ch, mode)) {
			parts.push(entry.slice(start, i));
			start = i + 1;
		}
	}
	parts.push(entry.slice(start));
	return parts;
}

/** True when a file or directory with exactly this name (as omp would resolve it) exists. */
function literalExists(entry: string, cwd: string): boolean {
	const absolute = toAbsolute(entry, cwd);
	if (!absolute) return false;
	try {
		fs.lstatSync(absolute);
		return true;
	} catch {
		return false;
	}
}

/**
 * The paths one `paths` entry may stand for. omp strips surrounding double quotes and recovers several targets that were
 * flattened into one entry ("a.cs; b.cs", "a.cs, b.cs", "a.cs b.cs"), but never splits a name that exists as written, so a
 * file with spaces in its name keeps working. Recovery is tried liberally here (whatever omp would pick is among the parts);
 * a wrongly recovered part only matters when it names a file with unsaved changes.
 */
function scopeVariants(entry: string, cwd: string): string[] {
	const whole = normalizeScopeEntry(entry);
	if (whole.length === 0) return [];
	const variants = new Set<string>([whole]);
	if (literalExists(whole, cwd)) return [...variants];
	for (const mode of SPLIT_MODES) {
		const parts = splitTopLevel(whole, mode)
			.map(normalizeScopeEntry)
			.filter(part => part.length > 0);
		if (parts.length > 1) for (const part of parts) variants.add(part);
	}
	return [...variants];
}

interface DirtyDoc {
	/** Path exactly as reported by Visual Studio (used in messages). */
	display: string;
	keys: string[];
}

/** Open documents with unsaved changes, indexed for path matching. */
export interface DirtyIndex {
	docs: DirtyDoc[];
}

export function buildDirtyIndex(state: VsState | undefined, cwd: string): DirtyIndex {
	const docs: DirtyDoc[] = [];
	for (const doc of state?.openDocuments ?? []) {
		if (!doc.isDirty || !win.isAbsolute(doc.path)) continue; // unsaved "Untitled" buffers have no disk path
		const keys = pathKeys(doc.path, cwd);
		if (keys.length > 0) docs.push({ display: doc.path, keys });
	}
	return { docs };
}

/** Dirty documents (as reported by VS) that equal any of the given file paths. */
export function matchDirtyFiles(index: DirtyIndex, targets: Iterable<string>, cwd: string): string[] {
	const hits = new Set<string>();
	for (const target of targets) {
		// Search scopes lose a surrounding pair of double quotes in omp; whether every file tool does is not verified, and a
		// real Windows file name never contains a double quote, so accepting the quoted spelling can only add a block.
		const targetKeys = new Set(pathKeys(normalizeScopeEntry(target), cwd));
		if (targetKeys.size === 0) continue;
		for (const doc of index.docs) {
			if (doc.keys.some(key => targetKeys.has(key))) hits.add(doc.display);
		}
	}
	return [...hits];
}

/** Dirty documents covered by any scope: a file, a directory, or a glob (as accepted by `ast_edit`). */
export function matchDirtyScopes(index: DirtyIndex, scopes: Iterable<string>, cwd: string): string[] {
	const hits = new Set<string>();
	for (const entry of scopes) {
		for (const scope of scopeVariants(entry, cwd)) {
			const absolute = toAbsolute(scope, cwd);
			if (!absolute) continue;
			// A scope is matched literally (file or directory prefix, as spelled and as its real path) and, when it has glob
			// syntax, as a glob (fixed prefix literal + real path of that prefix). Both are tried because `[`, `{` and `}` are
			// legal in Windows file names.
			const scopeKeys = pathKeys(scope, cwd);
			const matchers = GLOB_CHARS.test(keyOf(absolute)) ? globMatchers(absolute) : [];
			for (const doc of index.docs) {
				const covered =
					doc.keys.some(key => scopeKeys.some(base => key === base || key.startsWith(`${base}/`))) ||
					matchers.some(matcher => doc.keys.some(key => matcher.test(key)));
				if (covered) hits.add(doc.display);
			}
		}
	}
	return [...hits];
}

/** True when POMP shows Korean (`POMP_LANGUAGE=ko`, set by the host at launch). */
export function isKorean(env: Env = process.env): boolean {
	return env.POMP_LANGUAGE?.trim().toLowerCase() === "ko";
}

/** Picks the Korean or English text of a user-visible notice by the POMP language. */
export function pick(korean: string, english: string, env: Env = process.env): string {
	return isKorean(env) ? korean : english;
}
