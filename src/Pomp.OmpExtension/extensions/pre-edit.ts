/**
 * pomp pre-edit guard (omp extension module, `tool_call` hook).
 *
 * Under the pomp Visual Studio host, files open in VS can hold unsaved changes that
 * exist only in the editor buffer. omp's built-in `edit` / `write` / `ast_edit` (and
 * the mutating `lsp` actions) write to disk, which would clobber or diverge from those
 * changes. This hook consults `${POMP_STATE_DIR}/vs-state.json` (kept up to date by the
 * host) and, when a call targets a file that is open AND dirty in VS, blocks it. The reason steers the model to
 * `read_buffer` + `edit_buffer_hashline` when this session can call both (offered in `POMP_HOST_TOOLS` and enabled here),
 * otherwise to asking the user to save the file in Visual Studio and retrying with the normal tools.
 *
 * Why dirtiness and not a hash check (the design sketches "verify the hash, warn on conflict"): omp's built-in `edit` hashes
 * the file on disk, while the host's tags describe the editor buffer, so a hook cannot compare them; the condition that
 * actually loses work is "unsaved changes exist", which is what the host reports in vs-state.json.
 *
 * Blocking contract (omp 18.x): a `tool_call` handler returning `{ block: true, reason }`
 * stops the call before the approval gate; `reason` is shown to the model as the tool error.
 *
 * Fail-open by design: omp treats a throwing or slow `tool_call` handler as a block for
 * EVERY tool, so any internal problem (missing/garbled state file, odd input) is swallowed
 * and the call proceeds.
 *
 * Mode via env `POMP_PRE_EDIT_MODE`: `block` (default) | `warn` (let the call run, add a
 * passive warning for the model afterwards) | `off`.
 */
import type { ExtensionAPI } from "@oh-my-pi/pi-coding-agent";
import { mutationTargets } from "../lib/tool-targets.ts";
import {
	buildDirtyIndex,
	callableHostTools,
	isPompHost,
	matchDirtyFiles,
	matchDirtyScopes,
	offeredHostTools,
	pick,
	preEditMode,
	readVsState,
} from "../lib/vs-state.ts";

const MAX_LISTED = 8;

function listFiles(files: readonly string[]): string {
	const shown = files.slice(0, MAX_LISTED).map(file => `  - ${file}`);
	if (files.length > MAX_LISTED) shown.push(`  - ... and ${files.length - MAX_LISTED} more`);
	return shown.join("\n");
}

/**
 * Text returned to the model as the tool error when a call is blocked. A host tool is only named when the session can call it
 * (`callableHostTools`); without `edit_buffer_hashline` the remedy is to have the user save the file and then use the normal tools.
 */
export function blockReason(
	toolName: string,
	files: readonly string[],
	tools: ReadonlySet<string> = offeredHostTools(),
): string {
	const subject = files.length === 1 ? "This file has" : "These files have";
	const bufferRead = tools.has("read_buffer");
	const bufferEdit = bufferRead && tools.has("edit_buffer_hashline");
	const remedy =
		toolName === "ast_edit"
			? "Narrow `paths` to files without unsaved changes, or ask the user to save the files in Visual Studio."
			: bufferEdit
				? "Use `read_buffer`, then `edit_buffer_hashline`, on these files (edits the live VS buffer as one undo step). " +
					"If those tools fail, stop and ask the user to save the files in Visual Studio (Ctrl+S). " +
					"In a multi-file call, keep only files without unsaved changes."
				: (bufferRead ? "`read_buffer` shows the unsaved content. " : "") +
					"Ask the user to save these files in Visual Studio (Ctrl+S), then retry with the normal tools; never overwrite unsaved changes. " +
					"In a multi-file call, keep only files without unsaved changes.";
	return (
		`pomp: \`${toolName}\` blocked. ${subject} unsaved changes in Visual Studio that are not on disk; ` +
		`writing the on-disk copy would conflict with them:\n${listFiles(files)}\n${remedy}`
	);
}

/** Passive warning used in `warn` mode. */
export function warnText(
	toolName: string,
	files: readonly string[],
	tools: ReadonlySet<string> = offeredHostTools(),
): string {
	const next = tools.has("read_buffer")
		? "Re-read via `read_buffer` before further edits and tell the user a reload prompt may appear."
		: "Tell the user a reload prompt may appear and ask them to save or reload the file before further edits.";
	return (
		`pomp warning: \`${toolName}\` changed on-disk files that have unsaved changes in Visual Studio:\n${listFiles(files)}\n` +
		`The editor buffer and disk now differ. ${next}`
	);
}

export default function preEdit(pi: ExtensionAPI): void {
	if (!isPompHost()) return;

	pi.on("tool_call", (event, ctx) => {
		try {
			const mode = preEditMode();
			if (mode === "off") return;
			const targets = mutationTargets(event.toolName, event.input);
			if (!targets || (targets.files.length === 0 && targets.scopes.length === 0)) return;
			const state = readVsState();
			if (!state) return;
			const cwd = ctx.cwd;
			const index = buildDirtyIndex(state, cwd);
			if (index.docs.length === 0) return;
			const hits = [
				...new Set([
					...matchDirtyFiles(index, targets.files, cwd),
					...matchDirtyScopes(index, targets.scopes, cwd),
				]),
			];
			if (hits.length === 0) return;
			try {
				pi.logger?.debug?.("pomp pre-edit: unsaved VS changes", { tool: event.toolName, mode, files: hits });
			} catch {
				// logging must never affect the decision
			}
			const tools = callableHostTools(pi);
			return mode === "block"
				? { block: true, reason: blockReason(event.toolName, hits, tools) }
				: { additionalContext: warnText(event.toolName, hits, tools) };
		} catch (error) {
			try {
				pi.logger?.warn?.("pomp pre-edit: check failed, allowing tool call", { error: String(error) });
			} catch {
				// ignore
			}
			return undefined;
		}
	});

	pi.registerCommand("pomp-guard", {
		description: "Show pomp pre-edit guard mode and files with unsaved changes in Visual Studio",
		handler: async (_args, ctx) => {
			const state = readVsState();
			const dirty = buildDirtyIndex(state, ctx.cwd).docs.map(doc => doc.display);
			const mode = preEditMode();
			const modeKo = mode === "block" ? "차단" : mode === "warn" ? "경고" : "꺼짐";
			const open = state?.openDocuments.length ?? 0;
			ctx.ui.notify(
				(state
					? pick(
							`저장 안 한 변경 가드: ${modeKo}, 열린 문서 ${open}개, 저장 안 한 파일 ${dirty.length}개`,
							`Unsaved-changes guard: ${mode}, ${open} open documents, ${dirty.length} unsaved files`,
						)
					: pick(
							`저장 안 한 변경 가드: ${modeKo}, Visual Studio 상태를 읽지 못했어요.`,
							`Unsaved-changes guard: ${mode}, the Visual Studio state could not be read.`,
						)) + (dirty.length > 0 ? `\n${listFiles(dirty)}` : ""),
				"info",
			);
		},
	});
}
