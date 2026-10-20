/**
 * pomp feature guidance (omp extension module, `before_agent_start`; docs/screen-guide.md, "채팅 명령").
 *
 * Adds one section to the MAIN agent's system prompt: what POMP's own chat commands do (/plan, /goal, /workflow) and Work >
 * Progress offers, and when to suggest them, so that a user who just asks in words ("plan this first", "keep working on this on your
 * own") gets the right feature; and where a skill the user asks for must be written so that Tools > Skills lists it.
 * The text is a pure function of the tools this session can call, so it does not disturb prompt caching while they stay the same.
 * It points to the model-callable tools (`enter_plan_only`, `request_goal_run`, `propose_knowledge`) only when the session can call
 * them (an agent conversation has its role's tools only), and never tells the model to continue on its own beyond what the user asked:
 * continuation is POMP's job and starts only from the user's confirmation.
 */
import type { ExtensionAPI } from "@oh-my-pi/pi-coding-agent";
import { enabledTools, isPompHost } from "../lib/vs-state.ts";

export const FEATURES_MARKER = "## POMP chat commands (pomp)";

/** The section for a session that can call `tools` (undefined: unknown, every tool is named). */
export function featuresText(tools: ReadonlySet<string> | undefined): string {
	const has = (name: string): boolean => tools === undefined || tools.has(name);
	return [
		FEATURES_MARKER,
		"The user starts these from the chat box. Suggest the one that fits when it would help; they cost model usage or change the course of the work, so the user decides.",
		"- /plan <task>: a plan-only turn. You may read, search, use the web and keep the todo list; nothing is edited and no command runs. The plan appears in Work > Progress, where the user edits it and runs it." +
			(has("enter_plan_only")
				? " If the user only asks for a plan (\"just plan it\", \"don't change anything yet\"), call `enter_plan_only` first, on its own, so the guard holds for this turn, then plan with the todo tool and stop."
				: ""),
		"- /goal <goal>: starts a POMP Workflow on the goal and lets it continue by itself (up to six follow-up runs, optionally within a token budget) after the user confirms in a card. " +
			(has("request_goal_run")
				? "When the user asks you to keep working on a larger goal on its own, call `request_goal_run` with the goal (and the budget if they named one), then end your reply; do not begin the goal's work yourself."
				: "When the user asks you to keep working on a larger goal on its own, suggest /goal; do not begin the goal's work yourself."),
		"- /workflow <task>: a staged run (plan, discover, edit through change review, verify). Workflows the user saved for repeatable procedures are in Tools > Instructions.",
		"- Work > Progress shows the todo plan with \"현재 계획 이어가기\" (continue the plan) and \"자동 계속\" (automatic continuation, off by default).",
		"",
		"POMP skills (Tools > Skills). When the user asks you to create a skill, write it in one of these two folders; POMP lists and manages no others, so never use `.omp/skills`, `.claude/skills` or similar:",
		"- This project: `.pomp/skills/<name>/SKILL.md` under the workspace root (your working directory). The default.",
		"- All of the user's projects: `.pomp/skills/<name>/SKILL.md` under the user's home folder (`%USERPROFILE%`). Only when the user asks for that.",
		"- `<name>`: 2 to 40 lowercase letters, digits and hyphens. The file starts with YAML frontmatter holding `name: <name>` (the folder name) and a one-line `description:` that says when to use the skill, then the instructions in Markdown. Supporting files (references, scripts) go in the same folder.",
		"- POMP picks the skill up when your reply ends: it appears in Tools > Skills and the user can run it as `/skill:<name>`. No restart is needed.",
		"",
		"POMP knowledge (Tools > Knowledge) lives in two files POMP manages: `.pomp/knowledge.json` under the workspace root (this project, committed with it) and `.pomp/knowledge.json` under the user's home folder (`%USERPROFILE%`; every project on this PC). Knowledge the user selected for the conversation or set as default reaches you in a `<pomp_knowledge>` block. " +
			(has("propose_knowledge") ? "To add knowledge, use `propose_knowledge` (the user accepts it); do not edit these files." : "Do not edit these files."),
	].join("\n");
}

export default function featuresGuidance(pi: ExtensionAPI): void {
	if (!isPompHost()) return;
	pi.on("before_agent_start", (event, ctx) => {
		try {
			if (ctx?.agent?.kind === "sub") return undefined;
			if (event.systemPrompt.some(part => typeof part === "string" && part.startsWith(FEATURES_MARKER))) return undefined;
			return { systemPrompt: [...event.systemPrompt, featuresText(enabledTools(pi))] };
		} catch {
			return undefined;
		}
	});
}
