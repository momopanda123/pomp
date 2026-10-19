/**
 * pomp-context (resident omp extension).
 *
 * Active only under the pomp Visual Studio host (`POMP_HOST=1`); otherwise it registers nothing.
 *  1. Appends a short "Visual Studio host" section to the main agent's system prompt. The text depends only on the guard
 *     mode and on the host tools the host offered (`POMP_HOST_TOOLS`), never names a tool that is not offered, and is
 *     identical for identical environments (keeps the provider prompt-cache prefix stable).
 *  2. Announces the open solution to the model once per session, and again whenever the
 *     solution in `${POMP_STATE_DIR}/vs-state.json` changes (checked on each new prompt).
 *     The announcement is a hidden custom message; whether it was already sent is derived
 *     from the session history, so the handler stays stateless and safe to re-run.
 *  3. Announces the applicable scoped `.mdc` rules the same way: a hidden custom message
 *     that is re-sent only when its rendered text changes (a stale copy embedded in the
 *     system prompt by an older prompt is removed once). The system prompt therefore stays
 *     byte-stable within a session and keeps the provider's prompt-cache prefix valid.
 *
 * Volatile data (active file, selection, dirty flags) is deliberately NOT injected here: the
 * host puts the editor summary into the prompt itself, and `get_editor_context` covers the rest.
 */
import path from "node:path";
import { announceIfChanged } from "../lib/announce.ts";
import { loadRules, RULE_ANNOUNCE_MARKER, RULE_ANNOUNCE_TYPE, type ProjectRule } from "../lib/project-rules.ts";
import type { ExtensionAPI } from "@oh-my-pi/pi-coding-agent";
import {
	buildDirtyIndex,
	isPompHost,
	offeredHostTools,
	pick,
	preEditMode,
	readVsState,
	type PreEditMode,
	type VsState,
} from "../lib/vs-state.ts";

/** Marker heading; also used to keep the system-prompt edit idempotent. */
export const GUIDANCE_MARKER = "## Visual Studio host (pomp)";

/**
 * Guidance text. A pure function of the guard mode and of the *set* of offered host tools (the order in `POMP_HOST_TOOLS` does
 * not matter), so the provider prompt-cache prefix stays valid. A tool is only named when it is offered; with no tools known
 * (`POMP_HOST_TOOLS` absent) the text names none.
 */
export function guidance(mode: PreEditMode = "block", tools: ReadonlySet<string> = offeredHostTools()): string {
	const has = (name: string): boolean => tools.has(name);
	const bufferRead = has("read_buffer");
	const bufferEdit = bufferRead && has("edit_buffer_hashline");

	const unsaved = bufferEdit
		? "- Files open in VS may have unsaved edits. Read them with `read_buffer` (not `read`, `grep` or shell) and change them with `edit_buffer_hashline` (not `edit`, `write`, `ast_edit` or shell), using the tag from your latest `read_buffer`; never mix its tags with `read`/`edit`. " +
			(mode === "block"
				? "Disk edits to a file with unsaved VS changes are blocked"
				: "Never edit such a file on disk (it conflicts with the unsaved buffer)") +
			"; if the buffer tools fail, ask the user to save. Files not open in VS: use the normal tools."
		: "- Files open in VS may have unsaved edits that exist only in the editor." +
			(bufferRead ? " Read them with `read_buffer` (not `read`, `grep` or shell)." : "") +
			(mode === "block"
				? " Disk edits (`edit`, `write`, `ast_edit`) to a file with unsaved VS changes are blocked; do not bypass that with shell commands."
				: " Never edit such a file on disk (it conflicts with the unsaved buffer), with any tool or shell command.") +
			" If a file you must change has unsaved edits, ask the user to save it in Visual Studio (Ctrl+S), then retry with the normal tools; never overwrite unsaved changes. Files not open in VS, or already saved: use the normal tools.";

	const facts: string[] = [];
	if (has("run_terminal") && has("terminal_status")) facts.push("`run_terminal`: queue a noninteractive PowerShell process after explicit approval; prefer dedicated IDE tools for builds/tests. `terminal_status`: poll its bounded output and exit code. Queued/running is not completion; zero shell exit is not proof the requested task succeeded. Commands are not OS-sandboxed.");
	if (has("build_codebase_context")) facts.push("`build_codebase_context`: bounded folder/file/symbol outline and selected source excerpts for broad tasks. State omissions; character budget is not exact tokens. Use read_buffer for complete files before editing.");
	if (has("search_codebase")) facts.push("`search_codebase`: local symbol/BM25 search of live loaded source, including unsaved buffers. Cite returned path and line ranges; disclose coverage notices. Read full files with read_buffer before editing.");
	if (has("propose_workspace_edits")) facts.push("`propose_workspace_edits`: stage complete existing-source edits for per-line review and shadow compilation; use the contentHash from read_buffer. Staging does not apply changes. Read all source ranges before proposing complete replacement text.");
	if (has("get_editor_context")) facts.push("`get_editor_context`: active file, selection, open documents.");
	if (has("query_solution")) facts.push("`query_solution`: projects, files, references.");
	if (has("rename_project_file")) facts.push("`rename_project_file`: rename a project item with host approval; do not rename it on disk behind the project system.");
	if (has("remove_project_file")) facts.push("`remove_project_file`: remove a project item through the host approval flow; never substitute a permanent disk delete.");
	if (has("get_msbuild_properties")) facts.push("`get_msbuild_properties`: query evaluated project properties.");
	if (has("read_output_window")) facts.push("`read_output_window`: read Visual Studio Output panes.");
	if (has("manage_tests")) facts.push("`manage_tests`: discover or run tests through the host; both require approval because discovery can execute project code.");
	if (has("manage_git")) facts.push("`manage_git`: inspect repository state and request approved commit/push operations.");
	if (has("manage_breakpoints")) facts.push("`manage_breakpoints`: approved breakpoint changes, conditions (when true) and tracepoints.");
	if (has("control_debugger")) facts.push("`control_debugger`: approved debugger commands.");
	if (has("add_bookmark")) facts.push("`add_bookmark`: add a bookmark in an open document with approval.");
	if (has("read_error_list")) facts.push("`read_error_list`: current diagnostics.");
	if (has("build_solution")) facts.push("`build_solution`: build and verify after edits (prefer it to `dotnet build`/`msbuild` in bash).");
	const state = ["list_breakpoints", "get_debug_state", "list_bookmarks"].filter(has);
	if (state.length > 0) facts.push(`${state.map(name => `\`${name}\``).join(", ")}: debugger and bookmark state.`);

	const addFiles = has("add_project_file")
		? "Add files to projects with `add_project_file`"
		: "SDK-style projects pick up new source files automatically (just create the file), legacy projects list every file in the .csproj";
	const packages = has("manage_nuget")
		? "manage packages with `manage_nuget`"
		: "for package changes prefer `dotnet add package` (asks for approval) over hand-editing the project file";
	const projects = `- ${addFiles}; ${packages}; hand-edit .csproj/.sln/.slnx only when nothing else fits, in small edits. Skill \`vs-project\` covers project, MSBuild, NuGet, solution and VSIX details.`;

	const lines = [GUIDANCE_MARKER, "You run inside Visual Studio; editor state is live and can differ from disk.", unsaved];
	if (facts.length > 0) lines.push(`- ${facts.join(" ")}`);
	lines.push(projects);
	return lines.join("\n");
}

export const GUIDANCE = guidance("block");

/** customType of the hidden solution announcement (also the key used to detect what was already announced). */
export const SOLUTION_MESSAGE_TYPE = "pomp-solution";

interface SolutionDetails {
	solutionPath: string | null;
	solutionRoot: string | null;
}

function sameSolution(a: string | null, b: string | null): boolean {
	const key = (value: string | null): string | null => (value === null ? null : value.replace(/\//g, "\\").toLowerCase());
	return key(a) === key(b);
}

/** Newest solution announcement recorded on the current session branch, if any. */
function lastAnnouncement(branch: readonly unknown[]): SolutionDetails | undefined {
	for (let i = branch.length - 1; i >= 0; i--) {
		const entry = branch[i] as { type?: unknown; customType?: unknown; details?: unknown } | undefined;
		if (entry?.type !== "custom_message" || entry.customType !== SOLUTION_MESSAGE_TYPE) continue;
		const details = entry.details as Partial<SolutionDetails> | undefined;
		if (details && (typeof details.solutionPath === "string" || details.solutionPath === null)) {
			return { solutionPath: details.solutionPath, solutionRoot: details.solutionRoot ?? null };
		}
		return undefined; // malformed entry: announce again
	}
	return undefined;
}

export function solutionText(state: VsState, previous: SolutionDetails | undefined): string {
	const root = state.solutionRoot ? ` (root ${state.solutionRoot})` : "";
	if (state.solutionPath === null) {
		return previous
			? "Visual Studio closed its solution. Solution tools may fail until one is opened."
			: "Visual Studio has no solution open.";
	}
	if (!previous) return `Visual Studio solution: ${state.solutionPath}${root}.`;
	const was = previous.solutionPath ? `; was ${previous.solutionPath}` : "";
	return `Visual Studio solution changed: now ${state.solutionPath}${root}${was}. Earlier paths and query_solution results may be stale.`;
}

/** What a typed /rules shows: the scoped rules that apply to the active document now, and the diagnostics of the rule files. */
export function rulesText(state: VsState | undefined, cwd: string): string {
	const selected = loadRules(state?.solutionRoot ?? cwd, state?.activeDocument ?? null);
	const lines = selected.rules.map(rule => {
		const name = path.basename(rule.file, ".mdc");
		return `- ${name} (${rule.always ? pick("항상", "always") : rule.globs.join(", ")})`;
	});
	const head = lines.length > 0 ? pick(`지금 적용되는 규칙 ${lines.length}개:`, `Rules that apply now (${lines.length}):`) : pick("지금 적용되는 규칙이 없어요.", "No rules apply now.");
	return [head, ...lines, ...selected.diagnostics.map(diagnostic => `! ${diagnostic}`)].join("\n");
}

export default function pompContext(pi: ExtensionAPI): void {
	if (!isPompHost()) return;

	pi.on("before_agent_start", (event, ctx) => {
		try {
			// Subagents (task/eval) may not have the host tools; only the main agent gets VS guidance.
			if (ctx.agent?.kind === "sub") return;

			const result: {
				systemPrompt?: string[];
				message?: { customType: string; content: string; display: boolean; details: unknown; attribution: "agent" };
			} = {};

			if (!event.systemPrompt.some(part => typeof part === "string" && part.includes(GUIDANCE_MARKER))) {
				result.systemPrompt = [...event.systemPrompt, guidance(preEditMode())];
			}
			// H3: scoped rules no longer touch the system prompt (their active set changes with the active file, which broke
			// the provider's prompt-cache prefix). A stale rule part from an earlier prompt is removed once; the rules travel
			// as a hidden announcement message that is re-sent only when its content changes.
			const withoutRules = (result.systemPrompt ?? event.systemPrompt).filter(
				part => typeof part !== "string" || !part.startsWith(RULE_ANNOUNCE_MARKER),
			);
			if (withoutRules.length !== (result.systemPrompt ?? event.systemPrompt).length) {
				result.systemPrompt = withoutRules;
			}

			const state = readVsState();
			const rules = loadRules(state?.solutionRoot ?? ctx.cwd, state?.activeDocument ?? null);
			for (const diagnostic of rules.diagnostics) pi.logger?.warn?.("pomp-rules", { diagnostic });
			const branch = ctx.sessionManager.getBranch();
			if (state) {
				const previous = lastAnnouncement(branch);
				const unchanged = previous !== undefined && sameSolution(previous.solutionPath, state.solutionPath);
				const nothingToSay = previous === undefined && state.solutionPath === null;
				if (!unchanged && !nothingToSay) {
					result.message = {
						customType: SOLUTION_MESSAGE_TYPE,
						content: solutionText(state, previous),
						display: false,
						details: { solutionPath: state.solutionPath, solutionRoot: state.solutionRoot },
						attribution: "agent",
					};
				}
			}
			// Only one custom message can be returned; the rules win over the solution notice when both are due (rules steer
			// every tool call, the solution path is also visible in the prompt context), and the solution notice follows on
			// the next prompt (its branch check still finds it missing).
			const rulesMessage = announceIfChanged(branch, RULE_ANNOUNCE_TYPE, rules.text, {
				files: rules.rules.map((rule: ProjectRule) => rule.file),
			});
			result.message = rulesMessage ?? result.message ?? undefined;
			return result.systemPrompt || result.message ? result : undefined;
		} catch (error) {
			try {
				pi.logger?.warn?.("pomp-context: before_agent_start failed", { error: String(error) });
			} catch {
				// ignore
			}
			return undefined;
		}
	});

	const publishRules = (ctx: any, id = ""): void => {
		if (ctx.agent?.kind === "sub") return;
		const state = readVsState();
		const root = state?.solutionRoot ?? ctx.cwd;
		const active = state?.activeDocument ?? null;
		const selected = loadRules(root, active);
		ctx.ui.notify("pomp-rules-result:" + Buffer.from(JSON.stringify({ id, root, activeDocument: active, sessionId: ctx.sessionManager?.getSessionId(), rules: selected.rules.map(rule => ({ name: path.basename(rule.file, ".mdc"), file: rule.file, always: rule.always, globs: rule.globs })) })).toString("base64"), "info");
	};
	pi.on("session_start", (_event, ctx) => publishRules(ctx));
	pi.on("session_switch", (_event, ctx) => publishRules(ctx));
	pi.registerCommand("pomp-rules", {
		description: "Show the scoped rules that apply now (the POMP host also reads them with --request)",
		handler: async (args, ctx) => {
			const request = /^\s*--request\s+([a-f0-9]{32})\s*$/.exec(args);
			publishRules(ctx, request?.[1] ?? "");
			// The host's own read (--request) is answered by the result above only; a typed /rules is the user asking to see them.
			if (!request) ctx.ui.notify(rulesText(readVsState(), ctx.cwd), "info");
		},
	});

	pi.registerCommand("pomp-context", {
		description: "Show pomp Visual Studio context status (host, solution, unsaved files)",
		handler: async (_args, ctx) => {
			const state = readVsState();
			const dirty = buildDirtyIndex(state, ctx.cwd).docs.length;
			const vs = process.env.POMP_VS_VERSION?.trim();
			const open = state?.openDocuments.length ?? 0;
			const tools = offeredHostTools().size;
			ctx.ui.notify(
				state
					? pick(
							`Visual Studio${vs ? ` ${vs}` : ""} 연결: 솔루션 ${state.solutionPath ?? "없음"}, 열린 문서 ${open}개, 저장 안 한 파일 ${dirty}개, VS 도구 ${tools}개`,
							`Visual Studio${vs ? ` ${vs}` : ""} connection: solution ${state.solutionPath ?? "none"}, ${open} open documents, ${dirty} unsaved files, ${tools} VS tools`,
						)
					: pick("Visual Studio 상태를 읽지 못했어요.", "The Visual Studio state could not be read."),
				state ? "info" : "warning",
			);
		},
	});
}
