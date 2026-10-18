/**
 * Session-bound host authorization for desktop/browser preludes, the subagent eval guard, and the main agent's per-turn
 * screen & input control note (`before_agent_start`; docs/architecture.md §8.5).
 */
import * as fs from "node:fs";
import * as path from "node:path";
import * as engine from "@oh-my-pi/pi-coding-agent";
import type { ExtensionAPI } from "@oh-my-pi/pi-coding-agent";
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

/** The official extension shim resolves SettingsManager.create inside the calling session's async settings scope. */
export function readEngineComputerState(cwd: string): EngineComputerState {
	try {
		const api = engine as unknown as { SettingsManager?: { create(cwd: string): { rawValue(setting: unknown): unknown } } };
		const settings = api.SettingsManager?.create(cwd);
		if (!settings) return "unreadable";
		// rawValue is the public registry accessor; these two boolean keys have no path-scoped value resolution.
		const read = (name: string) => settings.rawValue({ segments: [name, "enabled"], definition: {} });
		const desktop = read("computer"), browser = read("browser");
		return typeof desktop === "boolean" && typeof browser === "boolean" ? { desktop, browser } : "unreadable";
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

export const COMPUTER_GUIDANCE = [
	COMPUTER_MARKER,
	"Screen & input control is enabled for this conversation. To see and operate the desktop, use the `computer` prelude of the Eval tool; read `xd://eval/computer` before first use.",
	"Never launch or use external programs or CLIs (such as Orca) for GUI control.",
].join("\n");

/** The note is present exactly while the host's switch is on for this session; read from the state file at every turn. */
export function computerGuidanceOn(sessionId: string | undefined, state: ComputerState): boolean {
	return !!sessionId && state !== "unreadable" && state.sessionId === sessionId && state.desktop;
}

export function withComputerGuidance(systemPrompt: readonly unknown[], on: boolean): unknown[] | undefined {
	const rest = systemPrompt.filter(part => !(typeof part === "string" && part.startsWith(COMPUTER_MARKER)));
	if (on) return [...rest, COMPUTER_GUIDANCE];
	return rest.length === systemPrompt.length ? undefined : rest;
}

export default function computerGuard(pi: ExtensionAPI): void {
	if (!isPompHost()) return;
	pi.on("tool_call", (event, ctx) => {
		if (event.toolName !== "eval" && event.toolName !== "computer" && event.toolName !== "browser") return undefined;
		try {
			const id = ctx.sessionManager?.getSessionId();
			const reason = computerToolBlock(event.toolName, typeof id === "string" ? id : undefined, ctx.agent?.kind, readComputerState(), event.toolName === "eval" ? readEngineComputerState(ctx.cwd) : "unreadable");
			return reason ? { block: true, reason } : undefined;
		} catch { return { block: true, reason: unauthorized() }; }
	});
	pi.on("before_agent_start", (event, ctx) => {
		try {
			if (ctx?.agent?.kind !== "main") return undefined;
			const id = ctx.sessionManager?.getSessionId();
			const systemPrompt = withComputerGuidance(event.systemPrompt, computerGuidanceOn(typeof id === "string" ? id : undefined, readComputerState()));
			return systemPrompt ? { systemPrompt: systemPrompt as typeof event.systemPrompt } : undefined;
		} catch {
			return undefined;
		}
	});
}
