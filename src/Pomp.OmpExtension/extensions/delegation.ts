/**
 * pomp delegation guidance (omp extension module, `before_agent_start`; docs/architecture.md §8.2 "팀 없이 일 나누기").
 *
 * Adds one fixed section to the MAIN agent's system prompt: when to split independent work across subagents with the engine's
 * `task` tool, what each brief must hold, and how to integrate the results in Visual Studio. The text never changes within a
 * session, so it does not disturb prompt caching. Team runs keep their own coordination rules (teams.ts); this section defers to them.
 */
import type { ExtensionAPI } from "@oh-my-pi/pi-coding-agent";
import { isPompHost } from "../lib/vs-state.ts";

export const DELEGATION_MARKER = "## Splitting work across subagents (pomp)";

export const DELEGATION_GUIDANCE = [
	DELEGATION_MARKER,
	"Applies when you split work yourself with the `task` tool; a team run started by the user follows its team rules instead.",
	"- Split only problems that are independent: each can be understood alone, fixing one would not change what another shows, and they do not edit the same files. Problems that may share a cause are investigated together first.",
	"- Start the workers in the same reply so they run side by side; keep it to about two to four, because each worker's tool calls can ask the user for approval.",
	"- Each brief is self-contained (a worker does not see this conversation): the area and files it may change, the files it must leave alone, the exact error text or failing test names, what must be true when it is done, and the shape of its report (cause, changes, evidence).",
	"- Workers edit and reason; you run the solution build and the tests once after integrating (one build or test run at a time in Visual Studio). Files with unsaved editor changes are poor candidates for a worker's edits.",
	"- When the results arrive, read every report, check that no two workers changed the same code, look at the changes yourself rather than trusting the summaries, then build and test before you report one combined result to the user.",
	"- Workers cannot ask the user anything or show cards; bring their open questions to the user yourself.",
].join("\n");

export default function delegation(pi: ExtensionAPI): void {
	if (!isPompHost()) return;
	pi.on("before_agent_start", (event, ctx) => {
		try {
			if (ctx?.agent?.kind === "sub") return undefined;
			if (event.systemPrompt.some(part => typeof part === "string" && part.startsWith(DELEGATION_MARKER))) return undefined;
			return { systemPrompt: [...event.systemPrompt, DELEGATION_GUIDANCE] };
		} catch {
			return undefined;
		}
	});
}
