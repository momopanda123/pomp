/**
 * pomp feature guidance (omp extension module, `before_agent_start`; docs/screen-guide.md, "채팅 명령").
 *
 * Adds one fixed section to the MAIN agent's system prompt: what POMP's own chat commands do (/plan, /goal, /workflow) and Work >
 * Progress offers, and when to suggest them, so that a user who just asks in words ("plan this first", "keep working on this on your
 * own") gets the right feature. The text never changes within a session (no per-request parts), so it does not disturb prompt caching.
 * It points to the two model-callable tools (`enter_plan_only`, `request_goal_run`) and never tells the model to continue on its own
 * beyond what the user asked: continuation is POMP's job and starts only from the user's confirmation.
 */
import type { ExtensionAPI } from "@oh-my-pi/pi-coding-agent";
import { isPompHost } from "../lib/vs-state.ts";

export const FEATURES_MARKER = "## POMP chat commands (pomp)";

export const FEATURES_GUIDANCE = [
	FEATURES_MARKER,
	"The user starts these from the chat box. Suggest the one that fits when it would help; they cost model usage or change the course of the work, so the user decides.",
	"- /plan <task>: a plan-only turn. You may read, search, use the web and keep the todo list; nothing is edited and no command runs. The plan appears in Work > Progress, where the user edits it and runs it. If the user only asks for a plan (\"just plan it\", \"don't change anything yet\"), call `enter_plan_only` first, on its own, so the guard holds for this turn, then plan with the todo tool and stop.",
	"- /goal <goal>: starts a POMP Workflow on the goal and lets it continue by itself (up to six follow-up runs, optionally within a token budget) after the user confirms in a card. When the user asks you to keep working on a larger goal on its own, call `request_goal_run` (when it is among your tools) with the goal (and the budget if they named one), then end your reply; do not begin the goal's work yourself.",
	"- /workflow <task>: a staged run (plan, discover, edit through change review, verify). Workflows the user saved for repeatable procedures are in Tools > Instructions.",
	"- Work > Progress shows the todo plan with \"현재 계획 이어가기\" (continue the plan) and \"자동 계속\" (automatic continuation, off by default).",
].join("\n");

export default function featuresGuidance(pi: ExtensionAPI): void {
	if (!isPompHost()) return;
	pi.on("before_agent_start", (event, ctx) => {
		try {
			if (ctx?.agent?.kind === "sub") return undefined;
			if (event.systemPrompt.some(part => typeof part === "string" && part.startsWith(FEATURES_MARKER))) return undefined;
			return { systemPrompt: [...event.systemPrompt, FEATURES_GUIDANCE] };
		} catch {
			return undefined;
		}
	});
}
