/**
 * pomp reply style (omp extension module, `before_agent_start`; docs/screen-guide.md, "채팅 답변 서식").
 *
 * The built-in `chat-style` skill is a formatting rule for every reply, not a task the model would match by its description, so
 * the model rarely reads it on its own. While the skill is on (it is in the engine's skill list in the system prompt; switching it
 * off in Tools > Skills takes it out), its instructions are appended to the MAIN agent's system prompt. A user's own
 * `.pomp/skills/chat-style/SKILL.md` (project first, then home) wins over the built-in one, as it does in the skill list.
 * The text changes only when the skill is switched or edited, so it does not disturb prompt caching between turns.
 */
import type { ExtensionAPI } from "@oh-my-pi/pi-coding-agent";
import fs from "node:fs";
import os from "node:os";
import path from "node:path";
import { fileURLToPath } from "node:url";
import { isPompHost, readVsState } from "../lib/vs-state.ts";

export const STYLE_SKILL = "chat-style";
export const STYLE_MARKER = "## POMP chat reply style (pomp)";
const MAX_BYTES = 32768;
const BUILTIN = path.join(path.dirname(fileURLToPath(import.meta.url)), "..", "catalog", "skills", STYLE_SKILL, "SKILL.md");

/** True when the engine lists the skill in the system prompt's `<skills>` block (a switched-off skill is not listed). */
export function styleSkillListed(systemPrompt: readonly string[]): boolean {
	const line = new RegExp(`^- ${STYLE_SKILL}: `, "m");
	return systemPrompt.some(part => typeof part === "string" && part.includes("<skills>") && line.test(part));
}

/** The skill's instructions without the frontmatter, or "" when no readable file is found. */
export function styleInstructions(root: string, home = os.homedir()): string {
	for (const file of [path.join(root, ".pomp", "skills", STYLE_SKILL, "SKILL.md"), path.join(home, ".pomp", "skills", STYLE_SKILL, "SKILL.md"), BUILTIN]) {
		try {
			const stat = fs.lstatSync(file);
			if (!stat.isFile() || stat.size > MAX_BYTES) continue;
			const text = fs.readFileSync(file, "utf8").replace(/^\uFEFF/, "").replace(/\r\n/g, "\n");
			const body = text.replace(/^---\n[\s\S]*?\n---(?:\n|$)/, "").trim();
			if (body) return body;
		} catch {
			// Missing or unreadable: try the next one.
		}
	}
	return "";
}

export default function replyStyle(pi: ExtensionAPI): void {
	if (!isPompHost()) return;
	pi.on("before_agent_start", (event, ctx) => {
		try {
			if (ctx?.agent?.kind === "sub") return undefined;
			const others = event.systemPrompt.filter(part => !(typeof part === "string" && part.startsWith(STYLE_MARKER)));
			const body = styleSkillListed(others) ? styleInstructions(readVsState()?.solutionRoot ?? ctx.cwd) : "";
			const next = body ? [...others, `${STYLE_MARKER}\nThe user switched this style on in Tools > Skills (\`${STYLE_SKILL}\`). Follow it in every reply.\n\n${body}`] : others;
			if (next.length === event.systemPrompt.length && next.every((part, index) => part === event.systemPrompt[index])) return undefined;
			return { systemPrompt: next };
		} catch {
			return undefined;
		}
	});
}
