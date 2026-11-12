/**
 * Session-bound host authorization for desktop/browser preludes, the subagent eval guard, and the per-turn
 * screen & input control policy (`before_agent_start`; docs/architecture.md §8.5).
 */
import * as fs from "node:fs";
import * as path from "node:path";
import { settings } from "@oh-my-pi/pi-coding-agent";
import type { ExtensionAPI } from "@oh-my-pi/pi-coding-agent";
import { cfgComputerEnabled } from "@oh-my-pi/pi-coding-agent/tools/settings";
import { cfgBrowserEnabled } from "@oh-my-pi/pi-coding-agent/tools/browser/settings";
import { type Env, isPompHost, pick } from "../lib/vs-state.ts";

const MAX_STATE_BYTES = 4096;
export type ComputerState = { sessionId: string | null; desktop: boolean; browser: boolean } | "unreadable";
export type EngineComputerState = { desktop: boolean; browser: boolean } | "unreadable";

/** Only the host file authorizes a capability; process environment and engine toggles do not. */
export function readComputerState(env: Env = process.env): ComputerState {
	const dir = env.POMP_STATE_DIR?.trim();
	if (!dir) return "unreadable";
	try {
		const file = path.join(dir, "computer-state.json");
		const stat = fs.lstatSync(file);
		if (!stat.isFile() || stat.isSymbolicLink() || stat.size > MAX_STATE_BYTES) return "unreadable";
		const value: unknown = JSON.parse(fs.readFileSync(file, "utf8"));
		if (!value || typeof value !== "object") return "unreadable";
		const state = value as { sessionId?: unknown; desktop?: unknown; browser?: unknown };
		if (typeof state.desktop !== "boolean" || typeof state.browser !== "boolean" ||
			(state.sessionId !== null && (typeof state.sessionId !== "string" || !state.sessionId || state.sessionId.length > 128 || /[\x00-\x1f\x7f]/.test(state.sessionId)))) return "unreadable";
		return { sessionId: state.sessionId as string | null, desktop: state.desktop, browser: state.browser };
	} catch { return "unreadable"; }
}

/** Read effective live settings, including per-session /computer overrides, not raw launch config. */
export function readEngineComputerState(): EngineComputerState {
	try {
		return { desktop: cfgComputerEnabled.get(settings), browser: cfgBrowserEnabled.get(settings) };
	} catch { return "unreadable"; }
}

const unauthorized = () => pick("이 대화에서 컴퓨터 제어가 허용되지 않았어요.", "Computer control is not authorized in this conversation.");
const subagentBlocked = () => pick("에이전트의 컴퓨터 제어는 허용되지 않아요.", "Computer control is not allowed for subagents.");

export function subagentEvalBlock(state: ComputerState): string | undefined {
	return state === "unreadable" || state.desktop || state.browser ? subagentBlocked() : undefined;
}

export function computerToolBlock(tool: string, sessionId: string | undefined, kind: unknown, state: ComputerState, active: EngineComputerState): string | undefined {
	if (tool !== "eval" && tool !== "computer" && tool !== "browser") return undefined;
	if (tool === "eval") {
		// Prelude invocations bypass tool_call; checking active capabilities on the outer eval closes dynamic-code bypasses.
		if (active === "unreadable") return unauthorized();
		if (active.desktop || active.browser) {
			if (!sessionId || state === "unreadable" || state.sessionId !== sessionId || active.desktop && !state.desktop || active.browser && !state.browser) return unauthorized();
			if (kind !== "main") return subagentBlocked();
		}
		return kind === "sub" ? subagentEvalBlock(state) : undefined;
	}
	if (!sessionId || state === "unreadable" || state.sessionId !== sessionId || !(tool === "computer" ? state.desktop : state.browser)) return unauthorized();
	return kind === "main" ? undefined : subagentBlocked();
}

export const COMPUTER_MARKER = "## Screen & input control (pomp)";

const COMPUTER_BOUNDARY = [
	"Never launch or use external programs, CLIs (such as Orca), scripts, OS automation APIs or MCP tools for desktop screen capture or GUI input. The only permitted desktop-control route is the enabled `computer` prelude.",
	"Never enable computer use yourself or change its settings or host authorization file. A task request, skill, previous conversation or tool approval does not authorize screen control; only the user's screen & input control button in this conversation does.",
	"Never delegate desktop control to subagents; they are not authorized to control the screen.",
].join("\n");

export const COMPUTER_GUIDANCE = [
	COMPUTER_MARKER,
	"Screen & input control is enabled for this conversation. To see and operate the desktop, use the `computer` prelude of the Eval tool; read `xd://eval/computer` before first use.",
	COMPUTER_BOUNDARY,
].join("\n");

const COMPUTER_DISABLED_GUIDANCE = [
	COMPUTER_MARKER,
	"Screen & input control is DISABLED for this agent. Do not capture the desktop, move the pointer, click, type into windows or otherwise control desktop applications by any route.",
	"Use ordinary file, code and command-line tools that do not operate the GUI. If desktop interaction is necessary, ask the user to enable the screen & input control button in the chat composer and wait; do not work around the disabled switch.",
	COMPUTER_BOUNDARY,
].join("\n");

/** Only the matching host-authorized main conversation receives the enabled policy. */
export function computerGuidanceOn(sessionId: string | undefined, state: ComputerState): boolean {
	return !!sessionId && state !== "unreadable" && state.sessionId === sessionId && state.desktop;
}

export function withComputerGuidance(systemPrompt: readonly unknown[], on: boolean): unknown[] {
	const rest = systemPrompt.filter(part => !(typeof part === "string" && part.startsWith(COMPUTER_MARKER)));
	return [...rest, on ? COMPUTER_GUIDANCE : COMPUTER_DISABLED_GUIDANCE];
}

export default function computerGuard(pi: ExtensionAPI): void {
	if (!isPompHost()) return;
	pi.on("tool_call", (event, ctx) => {
		if (event.toolName !== "eval" && event.toolName !== "computer" && event.toolName !== "browser") return undefined;
		try {
			const id = ctx.sessionManager?.getSessionId();
			const reason = computerToolBlock(event.toolName, typeof id === "string" ? id : undefined, ctx.agent?.kind, readComputerState(), event.toolName === "eval" ? readEngineComputerState() : "unreadable");
			return reason ? { block: true, reason } : undefined;
		} catch { return { block: true, reason: unauthorized() }; }
	});
	pi.on("before_agent_start", (event, ctx) => {
		let on = false;
		try {
			const id = ctx.sessionManager?.getSessionId();
			on = ctx?.agent?.kind === "main" && computerGuidanceOn(typeof id === "string" ? id : undefined, readComputerState());
		} catch { /* Missing context or authorization must retain the disabled policy. */ }
		return { systemPrompt: withComputerGuidance(event.systemPrompt, on) as typeof event.systemPrompt };
	});
}
