/**
 * pomp Live for omp's own file tools (omp extension module, `tool_call` + `tool_result`).
 *
 * omp's built-in `edit`, `write`, `ast_edit` and `apply_patch` write to disk, not through Visual Studio's editor buffer, so the host's
 * Live view (follow the edit, +/- marks, POMP bookmarks) would never see them; only `edit_buffer_hashline` did. Right before such a
 * call this module reads each target file as it is on disk and asks the host (hidden confirm `pomp-disk-edit-hold`) to hold the files
 * open in Visual Studio, so Visual Studio neither reloads them nor asks about the outside change while the tool writes. When the call
 * ends it sends those texts to the host (`pomp-disk-edit:` notice, with `error` when the call failed), which ends the hold and shows
 * the difference to the file's new text in the editor like a host edit, in any language.
 *
 * Main agent and subagents alike: the notice and the confirm go through the main conversation's UI channel (subagent tool events do
 * not reach the host). Fail-open: a `tool_call` handler that throws or runs out of time blocks the call, so every problem is
 * swallowed and the hold is waited for three seconds at most (omp pauses the handler budget during UI waits); files over 2 MiB,
 * directories and unreadable files are skipped.
 */
import fs from "node:fs";
import path from "node:path";
import type { ExtensionAPI } from "@oh-my-pi/pi-coding-agent";
import { mutationTargets } from "../lib/tool-targets.ts";
import { isPompHost } from "../lib/vs-state.ts";

export const DISK_EDIT_PREFIX = "pomp-disk-edit:";
export const DISK_EDIT_HOLD_TITLE = "pomp-disk-edit-hold";
const HOLD_WAIT_MS = 3000;
const MAX_FILE_BYTES = 2 * 1024 * 1024;
const MAX_FILES = 20;
/** Calls between `tool_call` and `tool_result`; bounded so a call that never reports back cannot pile up. */
const MAX_PENDING = 64;

export interface DiskEditFile { path: string; before: string | null }

/** The files a built-in call is about to change, as they are on disk now (null: the file does not exist yet). */
export function readBefore(toolName: string, input: unknown, cwd: string): DiskEditFile[] {
	const targets = mutationTargets(toolName, input);
	if (!targets || targets.files.length === 0) return [];
	const files: DiskEditFile[] = [];
	for (const target of [...new Set(targets.files)].slice(0, MAX_FILES)) {
		const full = path.resolve(cwd, target);
		let stat: fs.Stats | undefined;
		try { stat = fs.statSync(full); } catch { stat = undefined; }
		if (!stat) { files.push({ path: full, before: null }); continue; }
		if (!stat.isFile() || stat.size > MAX_FILE_BYTES) continue;
		try { files.push({ path: full, before: fs.readFileSync(full, "utf8") }); } catch { /* unreadable: no Live for it */ }
	}
	return files;
}

/** One field of an event or context object, read through a type guard (omp's event shapes differ between versions). */
function field(value: unknown, key: string): unknown {
	if (!value || typeof value !== "object" || !(key in value)) return undefined;
	return Reflect.get(value, key);
}

export default function liveEdits(pi: ExtensionAPI): void {
	if (!isPompHost()) return;
	const pending = new Map<string, DiskEditFile[]>();
	let mainUi: ExtensionUi | undefined;

	pi.on("tool_call", async (event, ctx) => {
		try {
			if (field(field(ctx, "agent"), "kind") !== "sub") mainUi = ctx.ui;
			const id = field(event, "toolCallId");
			if (typeof id !== "string" || id.length > 256) return undefined;
			const files = readBefore(event.toolName, event.input, ctx.cwd);
			if (files.length === 0) return undefined;
			pending.set(id, files);
			while (pending.size > MAX_PENDING) {
				const oldest = pending.keys().next().value;
				if (oldest === undefined) break;
				pending.delete(oldest);
			}
			const existing = files.filter(file => file.before !== null).map(file => file.path);
			if (existing.length > 0) {
				const hold = (mainUi ?? ctx.ui).confirm(DISK_EDIT_HOLD_TITLE, Buffer.from(JSON.stringify({ paths: existing })).toString("base64"));
				await Promise.race([hold.catch(() => false), delay(HOLD_WAIT_MS)]);
			}
		} catch {
			// Live is a convenience: never block a tool call.
		}
		return undefined;
	});

	pi.on("tool_result", (event, ctx) => {
		try {
			const id = field(event, "toolCallId");
			if (typeof id !== "string") return undefined;
			const files = pending.get(id);
			if (!files) return undefined;
			pending.delete(id);
			const error = field(event, "isError") === true;
			(mainUi ?? ctx.ui).notify(DISK_EDIT_PREFIX + Buffer.from(JSON.stringify({ files, error })).toString("base64"), "info");
		} catch {
			// ignore
		}
		return undefined;
	});
}

function delay(ms: number): Promise<void> {
	const { promise, resolve } = Promise.withResolvers<void>();
	setTimeout(resolve, ms);
	return promise;
}

/** The two UI calls this module makes (the context's `ui` has more). */
interface ExtensionUi {
	notify(message: string, type?: "info" | "warning" | "error"): void;
	confirm(title: string, message: string): Promise<boolean>;
}
