import { realpath, stat } from "node:fs/promises";
import { isAbsolute, resolve } from "node:path";
import { MCP_PROTOCOL_VERSION } from "../mcp/types";
import type { ToolRuntime } from "./tool-runtime";

// This directory is overlaid into the pinned engine at build time. Import the
// native tools only after selecting this mode and reserving stdout for MCP.
type RequestId = string | number;
const PROTOCOL_VERSIONS = new Set([MCP_PROTOCOL_VERSION, "2025-06-18", "2025-03-26", "2024-11-05"]);
const MAX_MESSAGE_BYTES = 8 * 1024 * 1024;
const HELP = `POMP local MCP tools (no model calls or OAuth)

Usage: omp mcp-server --cwd <absolute-directory> [--allow-write] [--allow-exec] [--print-config]

  --cwd PATH       Existing working directory; not a filesystem sandbox.
  --allow-write    Expose native edit and write in addition to read, glob and grep.
  --allow-exec     Expose native foreground bash commands with your OS permissions.
  --print-config   Print an Antigravity mcpServers entry instead of starting the server.

Without permission flags, only reading and searching are exposed. No TCP port is
opened. The calling app owns model inference and tool approval. Background jobs,
agent spawning, project plugins, provider settings and OAuth credentials are not
loaded by this server. Closing stdin cancels outstanding calls and shuts it down.
`;

class RpcFailure extends Error {
	constructor(readonly code: number, message: string) {
		super(message);
	}
}

function record(value: unknown): value is Record<string, unknown> {
	return value !== null && typeof value === "object" && !Array.isArray(value);
}

function requestId(value: unknown): value is RequestId {
	return typeof value === "string" || (typeof value === "number" && Number.isSafeInteger(value));
}

async function serve(runtime: ToolRuntime, cwd: string): Promise<void> {
	const active = new Map<RequestId, { method: string; controller: AbortController }>();
	const work = new Set<Promise<void>>();
	const toolNames = new Set(runtime.tools.map(tool => tool.name));
	let initialized = false;
	let ready = false;
	let stopping = false;
	let transportError: Error | undefined;

	function stop(error?: Error): void {
		transportError ??= error;
		if (stopping) return;
		stopping = true;
		for (const call of active.values()) call.controller.abort();
		process.stdin.destroy();
	}
	const onSignal = () => stop();
	const onOutputError = (error: Error) => stop(error);
	process.on("SIGINT", onSignal);
	process.on("SIGTERM", onSignal);
	process.stdout.on("error", onOutputError);

	function send(message: unknown): Promise<void> {
		if (stopping) return Promise.resolve();
		const { promise, resolve: resolveWrite, reject } = Promise.withResolvers<void>();
		process.stdout.write(`${JSON.stringify(message)}\n`, error => (error ? reject(error) : resolveWrite()));
		return promise;
	}

	function track(promise: Promise<void>): void {
		const tracked = promise.catch(error => stop(error instanceof Error ? error : new Error(String(error))))
			.finally(() => work.delete(tracked));
		work.add(tracked);
	}

	async function response(method: string, params: Record<string, unknown>, signal: AbortSignal): Promise<unknown> {
		if (method === "ping") return {};
		if (method === "initialize") {
			if (initialized) throw new RpcFailure(-32600, "This connection is already initialized.");
			if (typeof params.protocolVersion !== "string" || !record(params.capabilities) ||
				!record(params.clientInfo) || typeof params.clientInfo.name !== "string" || typeof params.clientInfo.version !== "string") {
				throw new RpcFailure(-32602, "initialize requires protocolVersion, capabilities and clientInfo.");
			}
			initialized = true;
			return {
				protocolVersion: PROTOCOL_VERSIONS.has(params.protocolVersion) ? params.protocolVersion : MCP_PROTOCOL_VERSION,
				capabilities: { tools: { listChanged: false } },
				serverInfo: { name: "pomp-tools", version: "1.0.0" },
				instructions: `OMP native file and foreground shell tools. Working directory: ${JSON.stringify(cwd)} (not a sandbox). ` +
					"Model inference stays in the calling app; this server never requests sampling or OAuth. " +
					"Read files before editing and use the returned snapshot anchors with the edit tool's patch argument. " +
					"Only explicitly enabled tools are available. Background jobs and model-backed tools are not exposed.",
			};
		}
		if (!ready) throw new RpcFailure(-32000, "Complete MCP initialization before using tools.");
		if (method === "tools/list") {
			if (params.cursor !== undefined) throw new RpcFailure(-32602, "This tool list has no additional pages.");
			return { tools: runtime.tools };
		}
		if (method !== "tools/call") throw new RpcFailure(-32601, `Unknown method: ${method}`);
		if (typeof params.name !== "string" || !toolNames.has(params.name)) {
			throw new RpcFailure(-32602, "Unknown or disabled tool.");
		}
		if (params.arguments !== undefined && !record(params.arguments)) {
			throw new RpcFailure(-32602, "Tool arguments must be an object.");
		}
		// MCP task-augmented execution is not advertised: all calls are request-bound.
		if (params.task !== undefined) throw new RpcFailure(-32602, "Background MCP tasks are not supported.");
		try {
			return await runtime.call(params.name, params.arguments ?? {}, crypto.randomUUID(), signal);
		} catch (error) {
			return { isError: true, content: [{ type: "text", text: error instanceof Error ? error.message : String(error) }] };
		}
	}

	function dispatch(line: string): void {
		if (stopping || !line.trim()) return;
		let message: unknown;
		try { message = JSON.parse(line); }
		catch {
			track(send({ jsonrpc: "2.0", id: null, error: { code: -32700, message: "Invalid JSON." } }));
			return;
		}
		if (!record(message) || message.jsonrpc !== "2.0" || typeof message.method !== "string") {
			// This server sends no requests; unsolicited responses require no reply.
			if (record(message) && message.jsonrpc === "2.0" && requestId(message.id) && ("result" in message || "error" in message)) return;
			track(send({ jsonrpc: "2.0", id: null, error: { code: -32600, message: "Invalid JSON-RPC request." } }));
			return;
		}
		if (!("id" in message)) {
			if (message.params !== undefined && !record(message.params)) return;
			if (message.method === "notifications/initialized" && initialized) ready = true;
			if (message.method === "notifications/cancelled" && record(message.params) && requestId(message.params.requestId)) {
				const call = active.get(message.params.requestId);
				if (call && call.method !== "initialize") call.controller.abort();
			}
			return;
		}
		const id = message.id;
		if (!requestId(id)) {
			track(send({ jsonrpc: "2.0", id: null, error: { code: -32600, message: "Invalid request id." } }));
			return;
		}
		if (active.has(id)) {
			// A duplicate in-flight id makes cancellation and response ownership ambiguous.
			stop(new Error("Duplicate in-flight MCP request id."));
			return;
		}
		const controller = new AbortController();
		active.set(id, { method: message.method, controller });
		const method = message.method;
		const params = message.params;
		track((async () => {
			try {
				if (params !== undefined && !record(params)) throw new RpcFailure(-32602, "Parameters must be an object.");
				const result = await response(method, params ?? {}, controller.signal);
				if (!controller.signal.aborted) await send({ jsonrpc: "2.0", id, result });
			} catch (error) {
				if (!controller.signal.aborted) {
					await send({ jsonrpc: "2.0", id, error: {
						code: error instanceof RpcFailure ? error.code : -32603,
						message: error instanceof Error ? error.message : "Internal server error.",
					} });
				}
			} finally {
				active.delete(id);
			}
		})());
	}

	// A bounded newline decoder, retaining chunk views until a frame completes.
	// Do not execute a partial final frame when the client disconnects.
	let parts: Buffer[] = [];
	let bytes = 0;
	try {
		for await (const chunk of process.stdin) {
			const buffer: Buffer = Buffer.isBuffer(chunk) ? chunk : Buffer.from(chunk);
			let start = 0;
			while (start < buffer.length && !stopping) {
				const newline = buffer.indexOf(10, start);
				const part = buffer.subarray(start, newline < 0 ? buffer.length : newline);
				bytes += part.length;
				if (bytes > MAX_MESSAGE_BYTES) throw new Error("MCP message exceeds 8 MiB.");
				if (newline < 0) {
					parts.push(part);
					break;
				}
				if (parts.length) {
					parts.push(part);
					dispatch(Buffer.concat(parts, bytes).toString("utf8"));
					parts = [];
				} else {
					dispatch(part.toString("utf8"));
				}
				bytes = 0;
				start = newline + 1;
			}
		}
	} catch (error) {
		if (!stopping) transportError = error instanceof Error ? error : new Error(String(error));
	} finally {
		stop();
		await Promise.allSettled(work);
		try { await runtime.close(); }
		finally {
			process.off("SIGINT", onSignal);
			process.off("SIGTERM", onSignal);
			process.stdout.off("error", onOutputError);
		}
	}
	if (transportError) throw transportError;
}

export async function runMcpServer(argv: string[]): Promise<void> {
	if (argv.includes("--help") || argv.includes("-h")) {
		process.stdout.write(HELP);
		return;
	}
	let cwd: string | undefined;
	let allowWrite = false;
	let allowExec = false;
	let printConfig = false;
	for (let index = 0; index < argv.length; index++) {
		switch (argv[index]) {
			case "--cwd":
				if (cwd !== undefined || !argv[index + 1] || argv[index + 1].startsWith("--")) throw new Error("Specify --cwd once with an absolute directory.");
				cwd = argv[++index];
				break;
			case "--allow-write": allowWrite = true; break;
			case "--allow-exec": allowExec = true; break;
			case "--print-config": printConfig = true; break;
			default: throw new Error(`Unknown MCP server option: ${argv[index]}. Use --help.`);
		}
	}
	if (!cwd || !isAbsolute(cwd)) throw new Error("mcp-server requires --cwd with an existing absolute directory.");
	cwd = await realpath(cwd);
	if (!(await stat(cwd)).isDirectory()) throw new Error("--cwd must be a directory.");
	if (printConfig) {
		const args = process.env.PI_COMPILED === "true" ? [] : [resolve(process.argv[1])];
		args.push("mcp-server", "--cwd", cwd);
		if (allowWrite) args.push("--allow-write");
		if (allowExec) args.push("--allow-exec");
		process.stdout.write(`${JSON.stringify({ mcpServers: { pomp: { command: process.execPath, args, timeoutSeconds: 600 } } }, null, 2)}\n`);
		return;
	}
	// Libraries must never put banners or diagnostics on the MCP wire.
	console.log = console.info = console.debug = console.error.bind(console);
	process.chdir(cwd);
	// Native modules initialize environment helpers on import. A static import
	// would run those before --cwd validation and stdout isolation, even for help.
	const { createToolRuntime } = await import("./tool-runtime");
	const runtime = await createToolRuntime({ cwd, allowWrite, allowExec });
	await serve(runtime, cwd);
}
