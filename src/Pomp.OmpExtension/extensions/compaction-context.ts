/**
 * pomp compaction context (omp extension module, `session.compacting` hook; H5).
 *
 * Automatic compactions (threshold, overflow, idle) otherwise get omp's default summary
 * prompt, which knows nothing about Visual Studio. This hook adds the POMP/VS facts the
 * summary must keep: the open solution, documents with unsaved changes, the POMP
 * Workflow phase, staged-but-unapplied proposals, and denied approvals in this session.
 * Each fact is one short line inside `<additional-context>` entries; the whole addition
 * is bounded to a few hundred bytes per fact.
 *
 * Sources, all fail-open (a missing source contributes no line):
 * - solution + dirty documents: `${POMP_STATE_DIR}/vs-state.json`.
 * - workflow phase + staged proposal: `${POMP_STATE_DIR}/composer-guard.json` as written
 *   by the host's ComposerWorkflow runtime guard (active/paused/phase), plus the newest
 *   `custom_message` of type `pomp-proposal` on the branch when one exists.
 * - denied approvals: `tool_approval_resolved` events with a deny outcome observed in
 *   this process (bounded ring of the newest few). Deny reasons are user text and are
 *   neutralized so they cannot break out of the context entry.
 *
 * The hook only ADDS context lines; it never overrides the summary prompt, so omp's
 * default template (sections, merge rule) keeps working unchanged. When the event is
 * unavailable the module registers nothing (feature off, logged once).
 */
import type { ExtensionAPI } from "@oh-my-pi/pi-coding-agent";
import * as fs from "node:fs";
import * as path from "node:path";
import { neutralizeTag } from "../lib/announce.ts";
import { isPompHost, readVsState, type Env } from "../lib/vs-state.ts";

const MAX_DENIALS = 5;
const MAX_DENIAL_CHARS = 200;
const MAX_PATH_CHARS = 260;
const MAX_LINES = 12;

export interface DeniedApproval {
	tool: string;
	reason: string;
}

function oneLine(text: string, max: number): string {
	const line = text.replace(/[\r\n\t]+/g, " ").trim();
	return line.length > max ? `${line.slice(0, max)}...` : line;
}

function shortPath(value: string | null): string {
	if (!value) return "(none)";
	return oneLine(value, MAX_PATH_CHARS);
}

/** The host's composer-guard.json runtime snapshot (ComposerWorkflow.WriteRuntimeLocked). */
export interface ComposerRuntime {
	active: boolean;
	paused: boolean;
	phase: string;
}

export function readComposerRuntime(env: Env = process.env): ComposerRuntime | undefined {
	try {
		const dir = env.POMP_STATE_DIR?.trim();
		if (!dir) return undefined;
		const file = path.join(dir, "composer-guard.json");
		const stat = fs.lstatSync(file);
		if (stat.isSymbolicLink() || !stat.isFile() || stat.size > 16384) return undefined;
		const data = JSON.parse(fs.readFileSync(file, "utf8")) as Partial<ComposerRuntime> & { version?: unknown };
		if (!data || data.version !== 1 || typeof data.active !== "boolean" || typeof data.phase !== "string") return undefined;
		return { active: data.active, paused: data.paused === true, phase: data.phase };
	} catch {
		return undefined;
	}
}

function isDenyOutcome(value: unknown): boolean {
	if (value === false) return true;
	if (typeof value !== "string") return false;
	const text = value.trim().toLowerCase();
	return text === "deny" || text === "denied" || text === "reject" || text === "rejected" || text === "block" || text === "blocked";
}

/** Records a deny outcome from a `tool_approval_resolved` event; returns the stored entry or undefined. */
export function denialFromApprovalEvent(event: unknown): DeniedApproval | undefined {
	if (!event || typeof event !== "object") return undefined;
	const record = event as Record<string, unknown>;
	const outcome = record.approved ?? record.decision ?? record.outcome ?? record.approval;
	if (!isDenyOutcome(outcome)) return undefined;
	const tool = typeof record.toolName === "string" && record.toolName ? record.toolName : "unknown tool";
	const reason =
		typeof record.reason === "string" && record.reason.trim()
			? oneLine(record.reason, MAX_DENIAL_CHARS)
			: "denied by the user";
	return { tool: oneLine(tool, 80), reason };
}

/**
 * The context lines for one compaction. `branch` is the current session branch (used to
 * spot staged proposals); `denials` the denies observed in this process (newest last).
 */
export function compactionLines(
	env: Env,
	branch: readonly unknown[],
	denials: readonly DeniedApproval[],
): string[] {
	const lines: string[] = [];
	const state = readVsState(env);
	lines.push(`pomp solution: ${shortPath(state?.solutionPath ?? null)}.`);
	const dirty = (state?.openDocuments ?? []).filter(doc => doc.isDirty).map(doc => doc.path);
	if (dirty.length > 0) {
		const shown = dirty.slice(0, 5).map(file => oneLine(file, MAX_PATH_CHARS));
		lines.push(`Unsaved VS changes (exist only in the editor, not on disk): ${shown.join("; ")}${dirty.length > 5 ? `; ... and ${dirty.length - 5} more` : ""}.`);
	}
	const runtime = readComposerRuntime(env);
	if (runtime?.active) {
		lines.push(
			`POMP Workflow phase: ${oneLine(runtime.phase || "unknown", 40)}${runtime.paused ? " (paused by the user)" : ""}. Keep the workflow task, plan steps and verification evidence.`,
		);
	}
	for (const denial of denials.slice(-MAX_DENIALS)) {
		lines.push(
			`Denied approval (do not retry without asking): ${neutralizeTag(oneLine(denial.tool, 80), "additional-context")} — ${neutralizeTag(denial.reason, "additional-context")}.`,
		);
	}
	return lines.slice(0, MAX_LINES);
}

export default function compactionContext(pi: ExtensionAPI): void {
	if (!isPompHost()) return;

	let loggedOnce = false;
	const logOnce = (message: string): void => {
		if (loggedOnce) return;
		loggedOnce = true;
		try {
			pi.logger?.info?.(message);
		} catch {
			// ignore
		}
	};

	try {
		if (typeof (pi as { on?: unknown }).on !== "function") {
			logOnce("pomp compaction-context: extension event API unavailable, hook off");
			return;
		}
	} catch {
		logOnce("pomp compaction-context: setup failed, hook off");
		return;
	}

	const denials: DeniedApproval[] = [];

	pi.on("tool_approval_resolved", event => {
		try {
			const denial = denialFromApprovalEvent(event as unknown);
			if (!denial) return;
			denials.push(denial);
			if (denials.length > MAX_DENIALS) denials.splice(0, denials.length - MAX_DENIALS);
		} catch {
			// observation only: never break approvals
		}
	});

	pi.on("session.compacting", (_event, ctx) => {
		try {
			const branch = ctx?.sessionManager?.getBranch?.() ?? [];
			const context = compactionLines(process.env, branch, denials);
			logOnce(`pomp compaction-context: session.compacting hook active (${context.length} context lines)`);
			return context.length > 0 ? { context } : undefined;
		} catch (error) {
			try {
				pi.logger?.warn?.("pomp compaction-context: hook failed", { error: String(error) });
			} catch {
				// logging must never affect the compaction
			}
			return undefined;
		}
	});
}
