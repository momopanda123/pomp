/**
 * pomp time context (omp extension module, `before_agent_start`; H9).
 *
 * One short hidden line with the user's local date/time and time zone, re-sent only
 * when the calendar day changes (same stateless branch technique as the solution and
 * rule announcements). Day granularity keeps history growth to one line per day and
 * never touches the system prompt, so the H3 prefix-cache design is undisturbed.
 *
 * The zone comes from the engine process (`Intl`): POMP launches omp on the user's own
 * machine, so that is the user's local zone. Both the IANA name and the current UTC
 * offset are included; the Windows zone name is not derivable from inside the engine
 * without host help, and the offset disambiguates DST.
 */
import type { ExtensionAPI } from "@oh-my-pi/pi-coding-agent";
import { announceIfChanged } from "../lib/announce.ts";
import { isPompHost } from "../lib/vs-state.ts";

export const TIME_ANNOUNCE_TYPE = "pomp-time";
export const TIME_MARKER = "## POMP local time";

/** Renders the time line for one instant (exported for tests). */
export function timeText(now: Date = new Date()): string {
	const zone = Intl.DateTimeFormat().resolvedOptions().timeZone ?? "local";
	const pad = (value: number): string => String(value).padStart(2, "0");
	const local =
		`${now.getFullYear()}-${pad(now.getMonth() + 1)}-${pad(now.getDate())} ` +
		`${pad(now.getHours())}:${pad(now.getMinutes())} ${zone}`;
	const minutes = -now.getTimezoneOffset();
	const sign = minutes >= 0 ? "+" : "-";
	const offset = `${sign}${pad(Math.floor(Math.abs(minutes) / 60))}:${pad(Math.abs(minutes) % 60)}`;
	return `${TIME_MARKER}\nLocal time: ${local} (UTC${offset}). This message is re-sent when the day changes; it supersedes any earlier time message.`;
}

/** Calendar-day key: the announcement is re-sent only when this changes. */
export function dayKey(now: Date = new Date()): string {
	return `${now.getFullYear()}-${now.getMonth() + 1}-${now.getDate()}`;
}

/** Newest time announcement on the branch, if any. */
export function lastTimeAnnouncement(branch: readonly unknown[]): string | undefined {
	for (let i = branch.length - 1; i >= 0; i--) {
		const entry = branch[i] as { type?: unknown; customType?: unknown; content?: unknown } | undefined;
		if (entry?.type !== "custom_message" || entry.customType !== TIME_ANNOUNCE_TYPE) continue;
		return typeof entry.content === "string" ? entry.content : undefined;
	}
	return undefined;
}

export default function timeContext(pi: ExtensionAPI): void {
	if (!isPompHost()) return;

	pi.on("before_agent_start", (event, ctx) => {
		try {
			if (ctx?.agent?.kind === "sub") return undefined;
			const now = new Date();
			const branch = ctx.sessionManager.getBranch();
			const previous = lastTimeAnnouncement(branch);
			// Re-send only when the day changed since the last announcement (compare by rendering that day's key from
			// the stored text is fragile; instead compare the stored line's date prefix with today's).
			const today = `${now.getFullYear()}-${String(now.getMonth() + 1).padStart(2, "0")}-${String(now.getDate()).padStart(2, "0")}`;
			if (previous !== undefined && previous.includes(`Local time: ${today} `)) return undefined;
			const message = announceIfChanged(branch, TIME_ANNOUNCE_TYPE, timeText(now), { day: dayKey(now) });
			if (message) return { message };
			return undefined;
		} catch {
			return undefined;
		}
	});
}
