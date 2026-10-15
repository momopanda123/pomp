/**
 * pomp verification gate (omp extension module, `session_stop` hook; H1).
 *
 * The most common failure in a compiled-language IDE is "reported done, does not
 * build". omp's prompt asks for a smoke run and POMP's guidance names the host build
 * tool, but nothing enforces it. This module watches the turn's tool calls and, when
 * the turn would settle with unverified source edits, continues ONCE with a short
 * instruction to build or run the relevant tests and report the result.
 *
 * Tracking (main agent only): `tool_result` records (a) successful source-editing calls
 * (omp `edit`/`write`/`ast_edit`/`apply_patch`, host `edit_buffer_hashline`,
 * `add_project_file`, `rename_project_file`) and (b) verification calls after the last
 * edit (`build_solution`, `read_error_list`, `manage_tests`, or a shell command that is
 * a build/test). `session_start`/`agent_start` reset the per-turn state.
 *
 * Continuation rules: at most once per user turn; never in subagents; never when the
 * turn was aborted or ended in error; never when the edited files are only docs
 * (*.md, *.txt) or when no VS solution is open; never when a POMP Workflow run is
 * active (its Verification phase owns the follow-up), and never when the last
 * assistant message already carries fresh build/test evidence. The gate never approves
 * anything: the build/test tools still ask as usual.
 *
 * No fight with the other continuations: the engine runs its own plan/todo completion
 * check BEFORE the session_stop pass and settles with willContinue when it schedules a
 * follow-up, so this hook only runs when no engine continuation follows; the host's
 * workflow auto-continue is a separate user-armed run that the gate skips via the
 * composer runtime flag. At most one gate nudge per user turn across all mechanisms.
 *
 * The static evidence section below is unconditional (it must not break prefix caching,
 * so it is fixed text): never claim success without fresh evidence from this turn.
 *
 * Option: "Verify after edits" (Tools > Options > Pomp, default ON) reaches the module
 * as `POMP_VERIFY_GATE=1/0`. The option controls only the automatic nudge.
 */
import type { ExtensionAPI } from "@oh-my-pi/pi-coding-agent";
import { readComposerRuntime } from "./compaction-context.ts";
import { mutationTargets } from "../lib/tool-targets.ts";
import { isPompHost, readVsState, type Env } from "../lib/vs-state.ts";

export const VERIFY_GATE_MARKER = "## Evidence before completion (pomp)";

export const VERIFY_GATE_GUIDANCE = [
	VERIFY_GATE_MARKER,
	"Never claim success, fixed or passing without fresh evidence from this turn: the host build or test tools, or reading the error list.",
	"Report the evidence briefly, or say plainly what was not verified and why.",
	"Unsaved VS buffers are invisible to command-line builds, so use the host build tool or save first.",
	"If verification fails, say so instead of retrying silently.",
].join("\n");

/** `POMP_VERIFY_GATE`: `0`/`false` turns the nudge off; anything else (default) keeps it on. */
export function verifyGateEnabled(env: Env = process.env): boolean {
	const value = env.POMP_VERIFY_GATE?.trim().toLowerCase();
	return value !== "0" && value !== "false" && value !== "off";
}

/** Tools whose successful call counts as editing source (checked against the call's target files). */
const EDIT_TOOLS: ReadonlySet<string> = new Set([
	"edit",
	"write",
	"ast_edit",
	"apply_patch",
	"edit_buffer_hashline",
	"add_project_file",
	"rename_project_file",
]);

/** Tools whose successful call after the last edit counts as verification. */
const VERIFY_TOOLS: ReadonlySet<string> = new Set(["build_solution", "read_error_list", "manage_tests"]);

/** File extensions that never need a build (docs). */
const DOC_EXTENSIONS: ReadonlySet<string> = new Set([".md", ".txt", ".rst", ".adoc"]);

function extensionOf(file: string): string {
	const base = file.split(/[/\\]/).pop() ?? file;
	const dot = base.lastIndexOf(".");
	return dot >= 0 ? base.slice(dot).toLowerCase() : "";
}

/** The files named by the section headers of an edit_buffer_hashline patch. */
export function hashlinePatchFiles(patch: string): string[] {
	const files: string[] = [];
	for (const raw of patch.split(/\r?\n/)) {
		const line = raw.trimEnd();
		const match = /^\[(.+)#[0-9a-fA-F]{4}\]$/.exec(line);
		const file = match?.[1]?.trim();
		if (file && !files.includes(file)) files.push(file);
	}
	return files;
}

/** File paths a tool call edited or verified, from its `tool_result` input. Best effort. */
export function editedFiles(toolName: string, input: unknown): string[] {
	if (!EDIT_TOOLS.has(toolName)) return [];
	if (toolName === "edit_buffer_hashline" || toolName === "add_project_file" || toolName === "rename_project_file") {
		const args = (input && typeof input === "object" ? input : {}) as Record<string, unknown>;
		const files: string[] = [];
		// edit_buffer_hashline takes one patch text whose sections start with `[path#TAG]` (TAG: four hex digits; HashlinePatch.TryHeader).
		if (typeof args.input === "string") files.push(...hashlinePatchFiles(args.input));
		for (const key of ["path", "file", "filePath", "destination", "newPath", "targetPath"]) {
			const value = args[key];
			if (typeof value === "string" && value.trim()) files.push(value.trim());
		}
		return files;
	}
	const targets = mutationTargets(toolName, input);
	if (!targets) return [];
	return [...targets.files, ...targets.scopes];
}

/** True when a bash command line looks like a build or test run (evidence of verification). */
export function isBuildCommand(command: unknown): boolean {
	if (typeof command !== "string") return false;
	const line = command.toLowerCase();
	return /(^|[;&|]\s*|&&\s*)(dotnet\s+(build|test|msbuild)|msbuild(\.exe)?\b|devenv(\.exe)?\b.*\/build|npm\s+(run\s+)?(build|test)\b|npx\s+tsc\b|tsc\b|pytest\b|go\s+test\b|cargo\s+test\b)/.test(line);
}

/** True when the last assistant text already reports build/test evidence (no nudge needed). */
export function hasEvidence(text: string): boolean {
	const line = text.toLowerCase();
	return /(build (succeeded|failed|passed)|build_solution|error list|errors?:\s*\d+|tests?:\s*\d+\s*(passed|failed)|all tests passed|test run (passed|failed|succeeded))/.test(line);
}

export interface TurnState {
	edits: string[];
	verified: boolean;
	nudged: boolean;
	aborted: boolean;
	errored: boolean;
}

export function freshTurn(): TurnState {
	return { edits: [], verified: false, nudged: false, aborted: false, errored: false };
}

/** Observes one tool result; returns the updated state (mutated in place). */
export function observeToolResult(state: TurnState, toolName: string, input: unknown, isError: boolean): TurnState {
	if (isError) {
		state.errored = true;
		return state;
	}
	if (VERIFY_TOOLS.has(toolName)) {
		state.verified = true;
		return state;
	}
	if (toolName === "bash" || toolName === "eval") {
		const args = (input && typeof input === "object" ? input : {}) as Record<string, unknown>;
		if (isBuildCommand(args.command ?? args.code ?? args.script)) state.verified = true;
		return state;
	}
	const files = editedFiles(toolName, input);
	if (files.length > 0) {
		state.edits.push(...files);
		state.verified = false;
	}
	return state;
}

export const VERIFY_NUDGE =
	"pomp: the turn edited source files that were not verified afterwards. Build with the host build tool (or run the relevant tests), read the error list, fix what you introduced, and report the evidence briefly. If the user declined builds or verification is impossible, say so and stop.";

export interface NudgeDecision {
	nudge: boolean;
	reason: string;
}

/**
 * Decides whether the settling turn needs the verification nudge. `lastText` is the
 * last assistant message's text (evidence check); `solutionOpen` whether VS has a
 * solution; `workflowActive` whether a POMP Workflow run is active (its Verification
 * phase owns the follow-up, never the gate).
 */
export function shouldNudge(state: TurnState, lastText: string, solutionOpen: boolean, workflowActive: boolean): NudgeDecision {
	if (state.nudged) return { nudge: false, reason: "already nudged this turn" };
	if (state.aborted) return { nudge: false, reason: "turn aborted" };
	if (state.errored) return { nudge: false, reason: "turn ended in error" };
	if (state.edits.length === 0) return { nudge: false, reason: "no edits" };
	if (state.verified) return { nudge: false, reason: "verified after last edit" };
	if (!solutionOpen) return { nudge: false, reason: "no solution open" };
	if (state.edits.every(file => DOC_EXTENSIONS.has(extensionOf(file)))) return { nudge: false, reason: "docs only" };
	if (workflowActive) return { nudge: false, reason: "workflow run owns verification" };
	if (hasEvidence(lastText)) return { nudge: false, reason: "evidence already reported" };
	return { nudge: true, reason: "unverified source edits" };
}

/** Assistant message text of the last assistant message, best effort. */
export function lastAssistantText(message: unknown): string {
	try {
		const blocks = (message as { content?: unknown })?.content;
		if (typeof blocks === "string") return blocks;
		if (!Array.isArray(blocks)) return "";
		return blocks
			.filter((block): block is { type: string; text: string } => !!block && typeof block === "object" && (block as { type?: unknown }).type === "text")
			.map(block => block.text)
			.join("\n");
	} catch {
		return "";
	}
}

export default function verifyGate(pi: ExtensionAPI): void {
	if (!isPompHost()) return;

	// Per-turn state for the MAIN agent (subagents never nudge). Reset on every agent start.
	let turn = freshTurn();
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

	pi.on("before_agent_start", (event, ctx) => {
		try {
			if (ctx?.agent?.kind === "sub") return undefined;
			if (event.systemPrompt.some(part => typeof part === "string" && part.startsWith(VERIFY_GATE_MARKER))) return undefined;
			return { systemPrompt: [...event.systemPrompt, VERIFY_GATE_GUIDANCE] };
		} catch {
			return undefined;
		}
	});

	pi.on("agent_start", (_event, ctx) => {
		try {
			if (ctx?.agent?.kind === "sub") return;
			turn = freshTurn();
		} catch {
			// ignore
		}
	});

	pi.on("tool_result", (event, ctx) => {
		try {
			if (ctx?.agent?.kind === "sub") return undefined;
			observeToolResult(turn, event.toolName, event.input, event.isError === true);
			return undefined;
		} catch {
			return undefined;
		}
	});

	pi.on("session_stop", (event, ctx) => {
		try {
			if (ctx?.agent?.kind === "sub") return undefined;
			if (!verifyGateEnabled()) {
				logOnce("pomp verify-gate: automatic nudge off (POMP_VERIFY_GATE=0)");
				return undefined;
			}
			const state = readVsState();
			const solutionOpen = (state?.solutionPath ?? null) !== null;
			// A POMP Workflow run owns its Verification phase (the host may auto-continue it): never nudge into it.
			// The engine's own plan/todo continuation runs before this hook and settles with willContinue, so when this
			// hook fires no engine continuation follows.
			const workflowActive = readComposerRuntime()?.active === true;
			const decision = shouldNudge(turn, lastAssistantText(event.last_assistant_message), solutionOpen, workflowActive);
			if (!decision.nudge) {
				logOnce(`pomp verify-gate: hook active (${decision.reason})`);
				return undefined;
			}
			turn.nudged = true;
			logOnce("pomp verify-gate: nudging to verify after edits");
			return { continue: true, additionalContext: VERIFY_NUDGE };
		} catch (error) {
			try {
				pi.logger?.warn?.("pomp verify-gate: session_stop failed", { error: String(error) });
			} catch {
				// ignore
			}
			return undefined;
		}
	});
}
