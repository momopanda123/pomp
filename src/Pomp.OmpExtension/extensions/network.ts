/**
 * pomp network guard (omp extension module, `tool_call` hook).
 *
 * The Visual Studio option "Block network tools" starts omp with `POMP_NETWORK=block`. The host then also denies the
 * network tools in its config overlay (`tools.approval.<tool>: deny`); this hook adds what a per-tool policy cannot express,
 * `read` of an http(s) URL (the same tool reads local files), and gives the model one clear reason for every blocked call.
 * Without the option (default) nothing is blocked; the host logs every network tool call it sees.
 *
 * Not a sandbox: bash, eval, MCP servers and the model provider itself still use the network.
 *
 * Fail-open like the pre-edit guard: omp treats a throwing `tool_call` handler as a block for EVERY tool, so internal
 * problems are swallowed and the call proceeds (the overlay denial still applies to the named tools).
 */
import type { ExtensionAPI } from "@oh-my-pi/pi-coding-agent";
import { networkTarget } from "../lib/tool-targets.ts";
import { isPompHost, networkMode } from "../lib/vs-state.ts";

const MAX_TARGET = 200;

/** Text returned to the model as the tool error. */
export function blockReason(toolName: string, target: string): string {
	const shown = target.length > MAX_TARGET ? `${target.slice(0, MAX_TARGET)}...` : target;
	return (
		`pomp: \`${toolName}\` blocked${shown ? ` (${JSON.stringify(shown)})` : ""}. ` +
		"The option \"Block network tools\" (Tools > Options > Pomp) turns off web search, URL reads, GitHub and the browser. " +
		"Continue without network access, or tell the user what you would need to look up so they can allow it."
	);
}

export default function networkGuard(pi: ExtensionAPI): void {
	if (!isPompHost() || networkMode() !== "block") return;

	pi.on("tool_call", event => {
		try {
			const target = networkTarget(event.toolName, event.input);
			if (target === undefined) return undefined;
			return { block: true, reason: blockReason(event.toolName, target) };
		} catch (error) {
			try {
				pi.logger?.warn?.("pomp network guard: check failed, allowing tool call", { error: String(error) });
			} catch {
				// logging must never affect the decision
			}
			return undefined;
		}
	});
}
