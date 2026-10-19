/**
 * pomp plan edits (omp extension module: the `pomp-plan-note` command, `tool_result` and `before_agent_start`).
 *
 * The user edits the agent's todo plan in Work > Progress and the host replaces the engine's plan with `set_todos`. The model names its
 * tasks by their text and remembers the plan it wrote, so without a word it takes a changed or missing task for a fault and writes its
 * old plan back. The host therefore sends each edit here in words; the note reaches the main agent with its next tool result while it
 * works, or before its next run when it is idle, and is then dropped.
 */
import type { ExtensionAPI } from "@oh-my-pi/pi-coding-agent";
import { isPompHost } from "../lib/vs-state.ts";

export const PLAN_NOTE_COMMAND = "pomp-plan-note";
export const PLAN_NOTE_TYPE = "pomp-plan-edit";
/** Notes kept per conversation until the model hears them; older ones give way (each says only what one edit changed). */
const MAX_NOTES = 20;

const pending = new Map<string, string[]>();

/** The note the model gets for the edits made since it last heard of one (exported for tests). */
export function planNoteText(changes: readonly string[]): string {
	return `## POMP: the user edited your work plan\n${changes.map(change => `- ${change}`).join("\n")}\n` +
		"The plan as it is now is the user's: do not restore, re-create or re-add what was changed or removed. Check it with the todo tool (view) and continue from it.";
}

/** Keeps one change for the conversation; false when the request is not a valid note (exported for tests). */
export function addPlanNote(args: string, currentSession: string | undefined): boolean {
	if (args.length > 20000 || !currentSession) return false;
	let request: { sessionId?: unknown; change?: unknown };
	try { request = JSON.parse(Buffer.from(args.trim(), "base64").toString("utf8")); } catch { return false; }
	if (typeof request?.sessionId !== "string" || request.sessionId !== currentSession) return false;
	if (typeof request.change !== "string" || !request.change.trim() || request.change.length > 4000) return false;
	const notes = pending.get(currentSession) ?? [];
	notes.push(request.change.replace(/[\r\n]+/g, " "));
	if (notes.length > MAX_NOTES) notes.shift();
	pending.set(currentSession, notes);
	return true;
}

/** The note for the conversation, once: it is dropped when taken (exported for tests). */
export function takePlanNote(session: string | undefined): string | undefined {
	const notes = session ? pending.get(session) : undefined;
	if (!session || !notes?.length) return undefined;
	pending.delete(session);
	return planNoteText(notes);
}

export default function planEdits(pi: ExtensionAPI): void {
	if (!isPompHost()) return;

	pi.registerCommand(PLAN_NOTE_COMMAND, {
		description: "POMP: the user's edit of the work plan, told to the model (invoked by the VS host)",
		handler: async (args, ctx) => {
			try { addPlanNote(args, ctx.sessionManager?.getSessionId()); } catch { /* a malformed note is dropped */ }
		},
	});

	pi.on("tool_result", (_event, ctx) => {
		try {
			if (ctx?.agent?.kind === "sub") return undefined;
			const note = takePlanNote(ctx?.sessionManager?.getSessionId());
			return note ? { additionalContext: note } : undefined;
		} catch {
			return undefined;
		}
	});

	pi.on("before_agent_start", (_event, ctx) => {
		try {
			if (ctx?.agent?.kind === "sub") return undefined;
			const note = takePlanNote(ctx?.sessionManager?.getSessionId());
			return note ? { message: { customType: PLAN_NOTE_TYPE, content: note, display: false, details: null, attribution: "agent" as const } } : undefined;
		} catch {
			return undefined;
		}
	});
}
