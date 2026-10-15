/**
 * Extracts the files an omp built-in tool call is about to modify, from the `tool_call`
 * event input. Real built-in names (omp 18.x): `edit` (which also covers delete via
 * REM / `*** Delete File` / op "delete" and move via MV / `*** Move to` / `rename`),
 * `write`, `ast_edit`, and the mutating `lsp` actions. omp has no separate delete/move tools.
 *
 * `edit` input shapes by edit mode:
 *   hashline    { input: "[PATH#TAG]\nPUT ...\nMV DEST\n[PATH2#TAG]\nREM" }   (default)
 *   apply_patch { input: "*** Begin Patch\n*** Update File: p\n*** Move to: q\n..." }
 *   sloppy      { input: "*** Edit File: p [all]\n*** Find\n...\n*** Replace\n..." }
 *   patch       { path, edits: [{ op, rename?, diff? }] }
 *   replace     { path, old_string, new_string }
 * For hashline, omp also adds derived `path` / `paths` fields to the event input.
 */

export interface MutationTargets {
	/** Individual files the call writes, deletes, moves or renames. */
	files: string[];
	/** Files, directories or globs the call may rewrite (`ast_edit`). */
	scopes: string[];
}

function isRecord(value: unknown): value is Record<string, unknown> {
	return typeof value === "object" && value !== null && !Array.isArray(value);
}

const HASHLINE_TAG = /#[0-9a-f]{4}$/i;
/** Legacy hashline headers start with one or more pilcrows (U+00B6). */
const PILCROW = String.fromCharCode(0xb6);
const PILCROW_RUN = new RegExp(`^${PILCROW}+`);

function unquote(value: string): string {
	const text = value.trim();
	const first = text[0];
	return text.length >= 2 && (first === '"' || first === "'") && text.endsWith(first) ? text.slice(1, -1) : text;
}

/** `[path#TAG]` (a copied read header) becomes `path`; anything else is returned unchanged. */
function unwrapReadHeader(value: string): string {
	const text = value.trim();
	const match = /^\[(.+)#[0-9a-f]{4}\]$/i.exec(text);
	return match ? unquote(match[1]) : text;
}

/** Collects every path named by a textual edit payload (hashline or apply_patch syntax). */
function collectPatchPaths(text: string, add: (path: string) => void): void {
	const body = text.charCodeAt(0) === 0xfeff ? text.slice(1) : text;
	for (const rawLine of body.split("\n")) {
		const line = rawLine.replace(/\r$/, "");
		if (line.startsWith("[") && line.endsWith("]")) {
			add(unquote(line.slice(1, -1).trim().replace(HASHLINE_TAG, "")));
			continue;
		}
		if (line.trimStart().startsWith(PILCROW)) {
			// Legacy hashline header: one or more pilcrows, then PATH#TAG.
			add(unquote(line.trimStart().replace(PILCROW_RUN, "").trim().replace(HASHLINE_TAG, "")));
			continue;
		}
		const move = /^MV\s+(.+)$/.exec(line);
		if (move) {
			add(unquote(move[1]));
			continue;
		}
		// apply_patch: "*** Add|Update|Delete File: p", "*** Move to: q"; sloppy: "*** Edit File: p" or "... p all".
		const header = /^[ \t]*\*{3}[ \t]+(?:Add File|Update File|Delete File|Move to|Edit File):[ \t]*(.+)$/i.exec(line);
		if (header) {
			const value = unquote(header[1]);
			add(value);
			const all = /^(.+?)[ \t]+all$/i.exec(value);
			if (all) add(unquote(all[1]));
		}
	}
}

function editTargets(input: Record<string, unknown>): string[] {
	const files = new Set<string>();
	const add = (value: unknown): void => {
		if (typeof value !== "string") return;
		const path = unwrapReadHeader(value);
		if (path) files.add(path);
	};
	add(input.path);
	add(input._path);
	if (Array.isArray(input.paths)) for (const path of input.paths) add(path);
	if (Array.isArray(input.edits)) {
		for (const edit of input.edits) if (isRecord(edit)) add(edit.rename);
	}
	for (const key of ["input", "_input"]) {
		const payload = input[key];
		if (typeof payload === "string") collectPatchPaths(payload, add);
	}
	return [...files];
}

/**
 * `lsp` actions that change files on disk. `rename` and `rename_file` apply unless `apply: false` (a preview writes nothing);
 * `code_actions` only with `apply: true`. `rename_file` moves its source on disk and the source may be a directory whose whole
 * tree moves, so it is a scope (a file, or a directory with everything below it), not one exact file. The other actions only
 * have their `file` checked: the workspace-wide edits they may return reach further, which is not covered.
 */
function lspTargets(input: Record<string, unknown>): MutationTargets {
	const file = typeof input.file === "string" ? input.file.trim() : "";
	if (!file || file === "*") return { files: [], scopes: [] };
	switch (input.action) {
		case "rename_file":
			return input.apply !== false ? { files: [], scopes: [file] } : { files: [], scopes: [] };
		case "rename":
			return input.apply !== false ? { files: [file], scopes: [] } : { files: [], scopes: [] };
		case "code_actions":
			return input.apply === true ? { files: [file], scopes: [] } : { files: [], scopes: [] };
		default:
			return { files: [], scopes: [] };
	}
}

/** Files or scopes a built-in tool call would modify; undefined for tools that never mutate project files. */
export function mutationTargets(toolName: string, input: unknown): MutationTargets | undefined {
	const args = isRecord(input) ? input : {};
	switch (toolName) {
		case "edit":
			return { files: editTargets(args), scopes: [] };
		case "write": {
			const path = typeof args.path === "string" ? unwrapReadHeader(args.path) : "";
			return { files: path ? [path] : [], scopes: [] };
		}
		case "ast_edit":
			return {
				files: [],
				scopes: Array.isArray(args.paths)
					? args.paths.filter((entry): entry is string => typeof entry === "string" && entry.length > 0)
					: [],
			};
		case "lsp":
			return lspTargets(args);
		default:
			return undefined;
	}
}

/** omp built-in tools that reach the network by design (the same list as the host's `OmpNetworkTools.Names`). */
export const NETWORK_TOOLS: ReadonlySet<string> = new Set(["web_search", "fetch", "web_fetch", "browser", "github"]);

const NETWORK_TARGET_FIELDS = ["url", "uri", "query", "q", "path", "repo", "repository"] as const;

const WEB_URL = /^https?:\/\//i;

/**
 * What a built-in tool call reaches on the network (a URL or search text; empty when the call names none), or undefined when it
 * is not a network call. `read` also reads local files, so it counts only when an argument is an http(s) URL.
 */
export function networkTarget(toolName: string, input: unknown): string | undefined {
	const args = isRecord(input) ? input : {};
	if (NETWORK_TOOLS.has(toolName)) {
		for (const field of NETWORK_TARGET_FIELDS) {
			const value = args[field];
			if (typeof value === "string" && value.length > 0) return value;
		}
		return "";
	}
	if (toolName === "read") {
		for (const value of Object.values(args)) {
			if (typeof value === "string" && WEB_URL.test(value.trim())) return value.trim();
		}
	}
	return undefined;
}
