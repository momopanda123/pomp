/**
 * pomp local model servers (omp extension module).
 *
 * The account tab saves model servers the user runs (any OpenAI-compatible server: a name, its address, an optional key); the host
 * starts omp with them in `POMP_LOCAL_MODELS` (`[{ id, name, address, key? }]`). Each becomes a provider under its saved id
 * (`local-...`) whose models are the server's model list: read once when omp starts, and again through `fetchDynamicModels` when
 * omp refreshes its catalog. An address given without `/v1` is tried as `<address>/models` and then `<address>/v1/models`.
 * A server that does not answer is still registered, without models, so a later refresh can find them.
 */
import type { ExtensionAPI } from "@oh-my-pi/pi-coding-agent";
import { isPompHost } from "../lib/vs-state.ts";

export interface LocalServer {
	id: string;
	name: string;
	address: string;
	key?: string;
}

export interface LocalModel {
	id: string;
	name: string;
	reasoning: boolean;
	input: ("text" | "image")[];
	cost: { input: number; output: number; cacheRead: number; cacheWrite: number };
	contextWindow: number;
	maxTokens: number;
}

type Fetch = (url: string, init: { headers: Record<string, string>; signal: AbortSignal }) => Promise<{ ok: boolean; text(): Promise<string> }>;

const SERVER_ID = /^local-[a-z0-9-]{1,40}$/;
const FETCH_MS = 3000;
const MAX_LIST_CHARS = 4_000_000;
const DEFAULT_CONTEXT = 128_000;
const DEFAULT_OUTPUT = 32_768;
/** omp needs a key for a provider with models; a server without one gets this placeholder (local servers ignore it). */
export const KEYLESS = "pomp-local-no-key";

/** The servers of `POMP_LOCAL_MODELS`; anything malformed is left out. */
export function parseServers(raw: string | undefined): LocalServer[] {
	if (!raw) return [];
	let rows: unknown;
	try { rows = JSON.parse(raw); } catch { return []; }
	if (!Array.isArray(rows)) return [];
	const servers: LocalServer[] = [];
	const ids = new Set<string>();
	for (const row of rows) {
		if (!row || typeof row !== "object") continue;
		const { id, name, address, key } = row as Record<string, unknown>;
		if (typeof id !== "string" || !SERVER_ID.test(id) || ids.has(id) || typeof name !== "string" || typeof address !== "string") continue;
		let url: URL;
		try { url = new URL(address); } catch { continue; }
		if (url.protocol !== "http:" && url.protocol !== "https:") continue;
		ids.add(id);
		servers.push({ id, name, address: address.replace(/\/+$/, ""), ...(typeof key === "string" && key ? { key } : {}) });
	}
	return servers;
}

const positive = (value: unknown): number | undefined => typeof value === "number" && Number.isSafeInteger(value) && value > 0 ? value : undefined;

/** One row of an OpenAI-style `/models` list (`data[]`), or of a `models[]` list, as an omp model. */
export function modelOf(row: unknown): LocalModel | null {
	if (!row || typeof row !== "object") return null;
	const value = row as Record<string, unknown>;
	const id = [value.id, value.model, value.name].find((item): item is string => typeof item === "string" && item.trim().length > 0)?.trim();
	if (!id || id.length > 200) return null;
	const context = positive(value.max_model_len) ?? positive(value.context_length) ?? positive(value.context_window) ?? positive(value.max_context_length) ?? DEFAULT_CONTEXT;
	return { id, name: id, reasoning: false, input: ["text"], cost: { input: 0, output: 0, cacheRead: 0, cacheWrite: 0 }, contextWindow: context, maxTokens: Math.min(DEFAULT_OUTPUT, context) };
}

/** The server's OpenAI-compatible root and its models, or null when neither `<address>/models` nor `<address>/v1/models` answers. */
export async function readModels(server: LocalServer, fetchImpl: Fetch = fetch as unknown as Fetch): Promise<{ baseUrl: string; models: LocalModel[] } | null> {
	const roots = /\/v1$/i.test(server.address) ? [server.address] : [server.address, `${server.address}/v1`];
	for (const root of roots) {
		try {
			const response = await fetchImpl(`${root}/models`, { headers: server.key ? { Authorization: `Bearer ${server.key}` } : {}, signal: AbortSignal.timeout(FETCH_MS) });
			if (!response.ok) continue;
			const text = await response.text();
			if (text.length > MAX_LIST_CHARS) continue;
			const body = JSON.parse(text) as { data?: unknown; models?: unknown };
			const rows = Array.isArray(body?.data) ? body.data : Array.isArray(body?.models) ? body.models : null;
			if (!rows) continue;
			const seen = new Set<string>();
			const models = rows.map(modelOf).filter((model): model is LocalModel => model !== null && !seen.has(model.id) && !!seen.add(model.id));
			return { baseUrl: root, models };
		} catch {
			// Not this root: try the next one.
		}
	}
	return null;
}

export default async function localModels(pi: ExtensionAPI): Promise<void> {
	if (!isPompHost()) return;
	const servers = parseServers(process.env.POMP_LOCAL_MODELS);
	await Promise.all(servers.map(async server => {
		const found = await readModels(server);
		try {
			pi.registerProvider(server.id, {
				baseUrl: found?.baseUrl ?? server.address,
				api: "openai-completions",
				apiKey: server.key ?? KEYLESS,
				models: found?.models ?? [],
				fetchDynamicModels: async () => (await readModels(server))?.models ?? [],
			});
		} catch (error) {
			try { pi.logger?.warn?.("pomp local models: a server could not be registered", { id: server.id, error: String(error) }); } catch { /* logging only */ }
		}
	}));
}
