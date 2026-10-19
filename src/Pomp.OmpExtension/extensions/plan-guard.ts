/**
 * pomp plan-only turn (omp extension module: `tool_call` guard, the `pomp-plan` command and the `enter_plan_only` tool).
 *
 * `/plan <task>` (the composer lists it as `/plan`; the host sends `pomp-plan`) asks the agent for a plan and nothing else. In that one
 * turn the agent may read, search, use the web and keep the engine's todo list (which shows in Work > Progress, where the user edits and
 * runs it); every other tool call is refused with a short reason, so no file is edited, no command runs and no agent is started. The
 * model can put the turn it is already in under the same guard with `enter_plan_only`.
 *
 * Why the model cannot lift it: the only code that switches the guard off is the end of the run (`agent_end` that is not followed by a
 * continuation), a new or switched session, and a ceiling on its age; no tool and no command does. The state is module-level on
 * purpose — omp re-runs this factory for every subagent session and shares module variables between them, so a child sees the guard too.
 * It is a cooperative engine-side filter, not an OS sandbox; the host's own approvals and the project policy still apply on top.
 *
 * The check is an allowlist: a tool that is not named here (an MCP tool, another extension's tool, a future built-in) is refused.
 */
import type { ExtensionAPI } from "@oh-my-pi/pi-coding-agent";
import { isPompHost, offeredHostTools, pick, type Env } from "../lib/vs-state.ts";

export const PLAN_COMMAND = "pomp-plan";
export const PLAN_TOOL = "enter_plan_only";

/** Engine tools that only read, search, ask the user or keep the plan. */
export const PLAN_ENGINE_TOOLS: ReadonlySet<string> = new Set(["read", "glob", "grep", "find", "ast_grep", "todo", "web_search", "fetch", "web_fetch", "ask", "think", "recall"]);
/** `lsp` actions that do not change files. */
const LSP_READ_ACTIONS: ReadonlySet<string> = new Set(["diagnostics", "definition", "references", "symbols", "hover", "type_definition", "implementation"]);
/** POMP host tools that only read, analyse or show a card. Edits, builds, tests, Git, debugger, terminals and workflows are not here. */
export const PLAN_HOST_TOOLS: ReadonlySet<string> = new Set([
	"get_editor_context", "read_buffer", "query_solution", "get_msbuild_properties", "read_error_list", "read_output_window",
	"list_breakpoints", "get_debug_state", "list_bookmarks", "search_codebase", "build_codebase_context", "review_source",
	"terminal_status", "present_case_feedback", "request_goal_run", "installed_skills", "pomp_settings",
]);

/** A plan turn is minutes long; the ceiling only makes sure a missed end-of-run event cannot keep the guard on for good. */
export const MAX_PLAN_AGE_MS = 2 * 60 * 60 * 1000;

interface PlanState {
	armedAt: number;
	/** The run the guard is for has started (a command arms before its message starts one). */
	started: boolean;
}
let state: PlanState | undefined;

export function planOnlyActive(now: number = Date.now()): boolean {
	return state !== undefined && now - state.armedAt < MAX_PLAN_AGE_MS;
}
/** `started`: the agent is running already (the tool arms mid-turn); a command arms before its message starts the run. */
export function armPlanOnly(started: boolean, now: number = Date.now()): void {
	state = { armedAt: now, started };
}
export function disarmPlanOnly(): void {
	state = undefined;
}

export function planBlockReason(toolName: string): string {
	return `Plan-only turn: \`${toolName}\` is not available. Read and search to understand the task, write the plan with the todo tool, then end your reply with a short summary of open questions. Editing files, running commands and starting agents wait until the user has the plan.`;
}

/** The reason a tool call is refused during a plan-only turn, or undefined when it may run (always undefined outside one). */
export function planGuardDecision(toolName: string, input: unknown, env: Env = process.env, now: number = Date.now()): string | undefined {
	if (!planOnlyActive(now)) return undefined;
	if (PLAN_ENGINE_TOOLS.has(toolName) || toolName === PLAN_TOOL) return undefined;
	if (toolName === "lsp") {
		const action = input && typeof input === "object" && "action" in input ? (input as { action?: unknown }).action : undefined;
		return typeof action === "string" && LSP_READ_ACTIONS.has(action) ? undefined : planBlockReason(toolName);
	}
	if (PLAN_HOST_TOOLS.has(toolName) && offeredHostTools(env).has(toolName)) return undefined;
	return planBlockReason(toolName);
}

/** The message the plan-only command sends. Static apart from the task; it never grants a tool the guard refuses. */
export function planPrompt(task: string): string {
	if (!task.trim() || task.length > 16000) throw new Error(pick("계획할 작업을 1–16,000자로 써 주세요. 사용법: /plan 작업", "Write a task of 1–16,000 characters. Usage: /plan task"));
	return `POMP plan-only task:\n${task.trim()}\n\nPlan this task; do not carry it out. In this turn you may read files, search the code, use the web and keep the todo list; editing files, running commands, starting agents and every other tool call are refused until the turn ends. Look at the actual code first, then write the plan with the todo tool as phases with concrete tasks (each names the files or symbols it touches and how it is checked). Finish with a short message that lists open questions and risks, then stop: do not start implementing. The user reviews and edits the plan in Work > Progress and runs it from there.`;
}

export default function planGuard(pi: ExtensionAPI): void {
	if (!isPompHost()) return;

	pi.on("tool_call", event => {
		let toolName = "this tool";
		try {
			toolName = String(event.toolName);
			// Arm before the next call of the same batch is looked at.
			if (toolName === PLAN_TOOL && !planOnlyActive()) armPlanOnly(true);
			const reason = planGuardDecision(toolName, event.input);
			if (reason) return { block: true, reason };
		} catch {
			// A handler that throws blocks every tool; in a plan-only turn the safe answer is the refusal itself.
			return planOnlyActive() ? { block: true, reason: planBlockReason(toolName) } : undefined;
		}
		return undefined;
	});

	// The run the guard is for has started; the end of that run (not a retry or a compaction continuation) lifts it. A subagent's or an
	// advisor's own run and session events (this module's handlers are bound to those sessions too) say nothing about the main run.
	const mainOnly = (ctx: unknown): boolean => (ctx as { agent?: { kind?: unknown } } | undefined)?.agent?.kind !== "sub";
	pi.on("agent_start", (_event, ctx) => {
		if (mainOnly(ctx) && state && !state.started) state.started = true;
	});
	pi.on("agent_end", (event, ctx) => {
		if (mainOnly(ctx) && state?.started && (event as { willContinue?: unknown })?.willContinue !== true) disarmPlanOnly();
	});
	for (const name of ["session_start", "session_switch", "session_shutdown"] as const) pi.on(name, (_event, ctx) => { if (mainOnly(ctx)) disarmPlanOnly(); });

	pi.registerCommand(PLAN_COMMAND, {
		description: "Plan only: read and search, write the plan, change nothing",
		handler: async (args, ctx) => {
			try {
				if (ctx.agent?.kind === "sub") throw new Error(pick("계획만 세우기는 기본 대화에서 시작해요.", "Plan-only turns start from the main conversation."));
				const prompt = planPrompt(args);
				armPlanOnly(false);
				pi.sendUserMessage(prompt);
			} catch (error) {
				disarmPlanOnly();
				ctx.ui.notify(error instanceof Error ? error.message : String(error), "warning");
			}
		},
	});

	pi.registerTool({
		name: PLAN_TOOL,
		label: "Plan only for this turn",
		description: "Put the rest of this turn under POMP's plan-only guard when the user wants a plan and no changes (\"just plan it\", \"don't change anything yet\"). Call it first and alone. Afterwards you may only read, search, use the web and keep the todo list; edits, commands and agents are refused until the turn ends, and you cannot lift the guard. Write the plan with the todo tool (the user edits and runs it in Work > Progress), then end with a short summary of open questions.",
		parameters: { type: "object", properties: {}, additionalProperties: false },
		loadMode: "essential",
		approval: "read",
		async execute(_toolCallId: string, _params: unknown, _signal: unknown, _onUpdate: unknown, ctx: { agent?: { kind?: unknown } }) {
			if (ctx?.agent?.kind === "sub") return { content: [{ type: "text", text: "Only the main conversation can start a plan-only turn." }], details: {} };
			if (!planOnlyActive()) armPlanOnly(true);
			return { content: [{ type: "text", text: "Plan-only guard is on until this turn ends. Read and search, write the plan with the todo tool, then finish with a short summary." }], details: {} };
		},
	} as never);
}
