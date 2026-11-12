/**
 * pomp local model servers (omp extension module).
 *
 * The account tab saves model servers the user runs (any OpenAI-compatible server: a name, its address, an optional key) and the
 * servers another PC shares (omp's auth gateway); the host starts omp with them in `POMP_LOCAL_MODELS` (`[{ id, name, address, key? }]`).
 * Each becomes a provider under its saved id (`local-...`) whose models are the server's model list, read when omp starts and again
 * on `/pomp-local-models <request id>` (the host asks before it lists models), which replaces the list with what the server lists now:
 * a model the server dropped (another PC signed out of its provider) goes, a new one comes. No omp model cache is involved.
 * An address given without `/v1` is tried as `<address>/models` and then `<address>/v1/models`. A server that does not answer keeps
 * the list it had (a first one is registered without models), so a later refresh can find them.
 */
import { getBundledModel } from "@oh-my-pi/pi-catalog/models";
import { DEFAULT_MODEL_PER_PROVIDER } from "@oh-my-pi/pi-catalog/provider-models";
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
	/**
	 * The effort levels as plain `reasoning_effort` values: the ones the sharing PC lists for the model, else those of the model in
	 * omp's catalog. The gateway applies the provider's own encoding (adaptive thinking, budgets, per-effort model ids), so none of
	 * that is copied. Absent for a server's own models.
	 */
	thinking?: { mode: "effort"; efforts: string[]; defaultLevel?: string };
	input: ("text" | "image")[];
	cost: { input: number; output: number; cacheRead: number; cacheWrite: number };
	contextWindow: number;
	maxTokens: number;
}

/** What omp's own catalog knows about `provider/id`: whether it thinks, and its levels. */
export type CatalogLookup = (provider: string, id: string) => { reasoning?: boolean; thinking?: { efforts?: readonly string[]; defaultLevel?: string } } | undefined;

type Fetch = (url: string, init: { headers: Record<string, string>; signal: AbortSignal }) => Promise<{ ok: boolean; text(): Promise<string> }>;

const SERVER_ID = /^local-[a-z0-9-]{1,40}$/;
const REQUEST_ID = /^[a-f0-9]{32}$/;
const FETCH_MS = 3000;
const MAX_LIST_CHARS = 4_000_000;
const DEFAULT_CONTEXT = 128_000;
const DEFAULT_OUTPUT = 32_768;
/** The values the gateway's chat route takes as `reasoning_effort`. */
const EFFORTS: Record<string, true> = { minimal: true, low: true, medium: true, high: true, xhigh: true, max: true };
/** omp needs a key for a provider with models; a server without one gets this placeholder (local servers ignore it). */
export const KEYLESS = "pomp-local-no-key";
/** The host reads the refresh answer from a notification with this prefix (PompSession.LocalModels.cs). */
export const REFRESH_RESULT_PREFIX = "pomp-local-models-result:";

const bundledCatalog: CatalogLookup = (provider, id) => {
	try { return getBundledModel(provider, id) ?? undefined; } catch { return undefined; }
};

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

/** The plain efforts of a list (other values and repeats left out), or undefined when it is not a list. */
const effortsOf = (value: unknown): string[] | undefined =>
	Array.isArray(value) ? [...new Set(value.filter((effort): effort is string => typeof effort === "string" && Object.hasOwn(EFFORTS, effort)))] : undefined;

/**
 * One row of an OpenAI-style `/models` list (`data[]`), or of a `models[]` list, as an omp model. Rows that describe themselves
 * (omp's auth gateway: `display_name`, `input_modalities`, `max_output_tokens`) keep that; plain servers get the defaults.
 * Another PC's share (POMP's gateway) lists each model's efforts (`reasoning_efforts`, empty when it has none to choose), which
 * covers a model that PC found at run time and no catalog here knows. A gateway row without them (`provider/id` owned by that
 * provider) thinks like the model in omp's catalog, if it knows it. Other rows think only when they say `reasoning`.
 */
export function modelOf(row: unknown, catalog: CatalogLookup = bundledCatalog): LocalModel | null {
	if (!row || typeof row !== "object") return null;
	const value = row as Record<string, unknown>;
	const id = [value.id, value.model, value.name].find((item): item is string => typeof item === "string" && item.trim().length > 0)?.trim();
	if (!id || id.length > 200) return null;
	const context = positive(value.max_model_len) ?? positive(value.context_length) ?? positive(value.context_window) ?? positive(value.max_context_length) ?? DEFAULT_CONTEXT;
	const display = typeof value.display_name === "string" ? value.display_name.trim() : "";
	const name = display.length > 0 && display.length <= 200 ? display : id;
	const image = Array.isArray(value.input_modalities) && value.input_modalities.includes("image");
	const output = positive(value.max_output_tokens);
	const listed = effortsOf(value.reasoning_efforts);
	const slash = id.indexOf("/");
	const known = listed === undefined && slash > 0 && value.owned_by === id.slice(0, slash) ? catalog(id.slice(0, slash), id.slice(slash + 1)) : undefined;
	const thinks = listed !== undefined ? listed.length > 0 : value.reasoning === true || known?.reasoning === true;
	const efforts = listed ?? effortsOf(known?.thinking?.efforts) ?? [];
	const defaultLevel = known?.thinking?.defaultLevel;
	return {
		id,
		name,
		reasoning: thinks,
		...(thinks && efforts.length > 0 ? { thinking: { mode: "effort" as const, efforts, ...(typeof defaultLevel === "string" ? { defaultLevel } : {}) } } : {}),
		input: image ? ["text", "image"] : ["text"],
		cost: { input: 0, output: 0, cacheRead: 0, cacheWrite: 0 },
		contextWindow: context,
		maxTokens: Math.min(output ?? DEFAULT_OUTPUT, context),
	};
}

/** The server's OpenAI-compatible root and its models, or null when neither `<address>/models` nor `<address>/v1/models` answers. */
export async function readModels(server: LocalServer, fetchImpl: Fetch = fetch as unknown as Fetch, catalog: CatalogLookup = bundledCatalog): Promise<{ baseUrl: string; models: LocalModel[] } | null> {
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
			const models = rows.map(row => modelOf(row, catalog)).filter((model): model is LocalModel => model !== null && !seen.has(model.id) && !!seen.add(model.id));
			return { baseUrl: root, models: defaultFirst(models, DEFAULT_MODEL_PER_PROVIDER) };
		} catch {
			// Not this root: try the next one.
		}
	}
	return null;
}

/**
 * Another PC's model sharing (omp's auth gateway) lists `provider/id` rows in catalog order, which can start with a model the provider
 * no longer serves; omp starts an engine without a chosen model on the first listed one. So the first row that is omp's own default
 * for its provider moves to the front. Lists without such a row keep their order.
 */
export function defaultFirst(models: LocalModel[], table: Readonly<Record<string, string>>): LocalModel[] {
	const index = models.findIndex(model => {
		const slash = model.id.indexOf("/");
		return slash > 0 && table[model.id.slice(0, slash)] === model.id.slice(slash + 1);
	});
	return index <= 0 ? models : [models[index], ...models.slice(0, index), ...models.slice(index + 1)];
}

/** The part of ExtensionAPI this module uses (tests pass a recorder). */
export type ProviderRegistry = Pick<ExtensionAPI, "registerProvider" | "unregisterProvider">;

/**
 * Registers one server as a provider with exactly `models` (omp keeps a provider's last list when the new one is empty, so an empty
 * list replaces it only after the provider is dropped). Returns false when omp refused it (logged).
 */
export function registerServer(pi: ProviderRegistry, server: LocalServer, baseUrl: string, models: LocalModel[], replace: boolean, warn: (message: string, data: object) => void): boolean {
	try {
		if (replace && models.length === 0) pi.unregisterProvider(server.id);
		pi.registerProvider(server.id, { baseUrl, api: "openai-completions", apiKey: server.key ?? KEYLESS, models });
		return true;
	} catch (error) {
		warn("pomp local models: a server could not be registered", { id: server.id, error: String(error) });
		return false;
	}
}

/**
 * Reads every server again and replaces each answering server's model list; one that does not answer keeps its list.
 * Returns the number of servers that answered and the number of models they list.
 */
export async function refreshServers(pi: ProviderRegistry, servers: LocalServer[], warn: (message: string, data: object) => void, fetchImpl: Fetch = fetch as unknown as Fetch, catalog: CatalogLookup = bundledCatalog): Promise<{ servers: number; models: number }> {
	let answered = 0;
	let count = 0;
	await Promise.all(servers.map(async server => {
		const found = await readModels(server, fetchImpl, catalog);
		if (!found) return;
		if (registerServer(pi, server, found.baseUrl, found.models, true, warn)) { answered++; count += found.models.length; }
	}));
	return { servers: answered, models: count };
}

export default async function localModels(pi: ExtensionAPI): Promise<void> {
	if (!isPompHost()) return;
	const servers = parseServers(process.env.POMP_LOCAL_MODELS);
	const warn = (message: string, data: object): void => { try { pi.logger?.warn?.(message, data); } catch { /* logging only */ } };
	await Promise.all(servers.map(async server => {
		const found = await readModels(server);
		registerServer(pi, server, found?.baseUrl ?? server.address, found?.models ?? [], false, warn);
	}));
	if (servers.length === 0) return;
	pi.registerCommand("pomp-local-models", {
		description: "POMP: read the saved model servers' lists again",
		handler: async (args, ctx) => {
			const id = args.trim();
			if (!REQUEST_ID.test(id)) return;
			const result = await refreshServers(pi, servers, warn);
			ctx.ui.notify(REFRESH_RESULT_PREFIX + Buffer.from(JSON.stringify({ id, ...result })).toString("base64"), "info");
		},
	});
}
