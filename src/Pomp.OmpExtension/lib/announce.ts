/**
 * Shared helpers for POMP's on-change announcement messages (H3): hidden custom messages
 * that carry volatile text (scoped rules, resource catalog, time) and are re-sent only when
 * their content changes. Same stateless technique as the solution announcement in
 * pomp-context.ts: what was already announced is derived from the session branch, so the
 * handler survives omp's hook re-runs, session switches and restarts.
 */
import type { ExtensionAPI } from "@oh-my-pi/pi-coding-agent";

/** A hidden agent-attributed custom message, as returned from `before_agent_start`. */
export interface Announcement {
	customType: string;
	content: string;
	display: boolean;
	details: unknown;
	attribution: "agent";
}

/**
 * Content of the newest announcement of one type recorded on the current session branch,
 * or undefined when none is there (also after compaction dropped it or the branch changed).
 * `detailsOf` reads the stored payload the same way the matching announce function wrote it.
 */
export function lastAnnounced(branch: readonly unknown[], customType: string): string | undefined {
	for (let i = branch.length - 1; i >= 0; i--) {
		const entry = branch[i] as { type?: unknown; customType?: unknown; content?: unknown } | undefined;
		if (entry?.type !== "custom_message" || entry.customType !== customType) continue;
		return typeof entry.content === "string" ? entry.content : undefined;
	}
	return undefined;
}

/**
 * Builds the announcement when `content` differs from what the branch already holds.
 * Returns undefined when nothing new needs to be said. `details` is stored alongside
 * for future readers; the content comparison is the dedupe key.
 */
export function announceIfChanged(
	branch: readonly unknown[],
	customType: string,
	content: string,
	details: unknown = null,
): Announcement | undefined {
	if (!content) return undefined;
	if (lastAnnounced(branch, customType) === content) return undefined;
	return { customType, content, display: false, details, attribution: "agent" };
}

/**
 * Tags inside file-derived text cannot open or close the announcement's own marker block.
 * `tag` is the bare tag name without brackets (e.g. "vs_context").
 */
export function neutralizeTag(text: string, tag: string): string {
	const escaped = tag.replace(/[.*+?^${}()|[\]\\]/g, "\\$&");
	// The replacement must differ from the tag itself: break the name with a slash-proof
	// middle dot only when it already contains a dash, else use the dashed spelling.
	const safe = tag.includes("-") ? tag.replace(/-/g, "·-") : tag.replace(/_/g, "-");
	return text.replace(new RegExp(`<\\s*(/?)\\s*${escaped}(?=[\\s>/])`, "gi"), "<$1" + safe);
}

export type ExtensionContext = Parameters<Parameters<ExtensionAPI["on"]>[1]>[1];
