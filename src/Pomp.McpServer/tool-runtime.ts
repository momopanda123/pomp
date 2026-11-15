import * as fs from "node:fs/promises";
import * as os from "node:os";
import * as path from "node:path";
import { EditStore, editInspect } from "@oh-my-pi/pi-natives";
import { toolWireSchema, validateJsonSchemaValue } from "@oh-my-pi/pi-ai/utils/schema";
import { splitPathAndSel } from "@oh-my-pi/pi-tui/tools/read";
import { Settings } from "../config/settings";
import { EditTool } from "../edit";
import { drainAbortedShells, releaseShellSessions } from "../exec/bash-executor";
import { InternalUrlRouter } from "../internal-urls/router";
import type { Tool, ToolSession } from "../tools";
import { BashTool } from "../tools/bash";
import { GlobTool } from "../tools/glob";
import { GrepTool } from "../tools/grep";
import { unwrapHashlineHeaderPath } from "../tools/hashline-header-path";
import { expandDelimitedPathEntries, expandPath, normalizePathLikeInput } from "../tools/path-utils";
import { ReadTool } from "../tools/read";
import { WriteTool } from "../tools/write";

export interface ToolRuntimeOptions {
	cwd: string;
	allowWrite: boolean;
	allowExec: boolean;
}

export interface ToolRuntimeResult {
	content: Array<{ type: "text"; text: string } | { type: "image"; data: string; mimeType: string }>;
	isError?: boolean;
}

export interface ToolRuntime {
	tools: Array<{
		name: string;
		description: string;
		inputSchema: Record<string, unknown>;
		annotations?: Record<string, boolean>;
	}>;
	call(name: string, args: Record<string, unknown>, id: string, signal: AbortSignal): Promise<ToolRuntimeResult>;
	close(): Promise<void>;
}

const EDIT_SCHEMA: Record<string, unknown> = {
	type: "object",
	properties: { patch: { type: "string", description: "The complete native OMP hashline patch." } },
	required: ["patch"],
	additionalProperties: false,
};

// These restrictions are a capability boundary, not filesystem confinement. Paths
// outside cwd and symlinks remain available with the operating system's permissions.
function assertLocalPath(target: string): void {
	const authored = normalizePathLikeInput(unwrapHashlineHeaderPath(target));
	const normalized = expandPath(authored);
	if (!normalized || normalized.includes("\0")) throw new Error("A nonempty local path is required.");
	// Reject aliases and embedded URLs before any native router or list recovery.
	if (/[a-z][a-z0-9+.-]+:\//i.test(authored) || /:\/\//.test(authored) ||
		/[a-z][a-z0-9+.-]+:\//i.test(normalized) || /:\/\//.test(normalized) ||
		/(?:^|[\s;,])www\./i.test(normalized)) {
		throw new Error("Only local filesystem paths are supported; URL and internal device access is disabled.");
	}
	if (/^(?:\\\\|\/\/)/.test(normalized)) throw new Error("UNC/network paths are not supported by this local tool server.");
	const base = splitPathAndSel(normalized).path;
	if (InternalUrlRouter.instance().canResolve(base)) {
		throw new Error("Internal URLs and MCP resources are not supported by this local tool server.");
	}
}

function validateInput(schema: Record<string, unknown>, args: unknown): void {
	const validation = validateJsonSchemaValue(schema, args);
	if (!validation.success) {
		throw new Error(`Invalid tool arguments: ${validation.issues.map(issue => `${issue.path.join(".") || "arguments"}: ${issue.message}`).join("; ")}`);
	}
}

export async function createToolRuntime(options: ToolRuntimeOptions): Promise<ToolRuntime> {
	if (!path.isAbsolute(options.cwd) || !(await fs.stat(options.cwd)).isDirectory()) {
		throw new Error("--cwd must name an existing absolute directory.");
	}
	assertLocalPath(options.cwd);
	const cwd = path.resolve(options.cwd);
	const artifactsDir = await fs.mkdtemp(path.join(os.tmpdir(), "pomp-mcp-"));
	const sessionId = `pomp-mcp-${crypto.randomUUID()}`;
	const artifacts = new Map<string, string>();
	const disposers = new Set<() => void>();
	const calls = new Set<Promise<ToolRuntimeResult>>();
	const controllers = new Set<AbortController>();
	let disposed = false;
	let closePromise: Promise<void> | undefined;
	let queue: Promise<unknown> = Promise.resolve();
	let artifactSequence = 0;
	const store = new EditStore();

	try {
		const settings = Settings.isolated({
			"tools.xdev": false,
			"edit.mode": "hashline",
			"edit.enforceSeenLines": true,
			"edit.autoRepair.enabled": false,
			"edit.blackbox.enabled": false,
			"lsp.enabled": false,
			"async.enabled": false,
			"bash.autoBackground.enabled": false,
			"launch.enabled": false,
			"bash.direnv": "off",
			"bashInterceptor.enabled": false,
			"shellMinimizer.enabled": false,
			"worktree.clone": false,
			"fetch.enabled": false,
			"ida.enabled": false,
			"read.summarize.prose": false,
			"spotlight.experiment.enabled": false,
		});
		const names = new Set(["read", "glob", "grep"]);
		if (options.allowWrite) { names.add("edit"); names.add("write"); }
		if (options.allowExec) names.add("bash");
		const session: ToolSession = {
			cwd,
			hasUI: false,
			canPromptUser: false,
			settingsApproval: false,
			settings,
			editStore: store,
			hasEditTool: options.allowWrite,
			restrictToolNames: true,
			enableLsp: false,
			enableMCP: false,
			enableIrc: false,
			isolatedShell: true,
			modelFreeTools: true,
			noContextFiles: true,
			contextFiles: [],
			skills: [],
			rules: [],
			activeRules: [],
			extensionPaths: [],
			customToolPaths: [],
			getSessionFile: () => null,
			getSessionSpawns: () => "",
			getSessionId: () => sessionId,
			getArtifactsDir: () => artifactsDir,
			isDisposed: () => disposed,
			isToolActive: name => names.has(name),
			fetch: async () => { throw new Error("Network fetching is disabled in the MCP tool runtime."); },
			allocateOutputArtifact: async () => {
				const id = `mcp-${++artifactSequence}`;
				const artifactPath = path.join(artifactsDir, `${id}.txt`);
				artifacts.set(id, artifactPath);
				return { id, path: artifactPath };
			},
			registerDisposeCallback: callback => {
				disposers.add(callback);
				return () => { disposers.delete(callback); };
			},
		};
		// Construct only these actual native tools. createTools also owns dynamic
		// registry/presentation policy, which a fixed-capability MCP host does not need.
		const nativeTools: Tool[] = [new ReadTool(session), new GlobTool(session, { rootPathAlias: true }), new GrepTool(session)];
		if (options.allowWrite) nativeTools.push(new EditTool(session, "hashline"), new WriteTool(session));
		if (options.allowExec) nativeTools.push(new BashTool(session));
		const byName = new Map(nativeTools.map(tool => [tool.name, tool]));
		const schemas = new Map<string, Record<string, unknown>>();
		const tools = nativeTools.map(tool => {
			const inputSchema = tool.name === "edit" ? EDIT_SCHEMA : structuredClone(toolWireSchema(tool));
			if (tool.name === "bash") {
				// A PTY cannot be honored without a UI; do not advertise a silently
				// downgraded backend. Unknown async/service fields are rejected too.
				delete (inputSchema.properties as Record<string, unknown>).pty;
			}
			inputSchema.additionalProperties = false;
			schemas.set(tool.name, inputSchema);
			const restrictions = tool.name === "bash"
				? "MCP host: foreground execution only, no PTY/async/named services, shell profile or direnv loading. Full OS execution privilege; cwd is not a sandbox. Internal OMP URLs are unavailable."
				: "MCP host: local filesystem only; no URL/device/MCP resource access. Read accepts this session's artifact:// output IDs. Image/model questions, query selectors, browser PDF screenshots, video decoding and external HEIF/AVIF conversions are unavailable. PDF/document text and raster/SVG images remain supported. cwd is not a sandbox.";
			return {
				name: tool.name,
				description: `${tool.description}\n\n${restrictions}${tool.name === "edit" ? " Supply the native freeform patch as the JSON patch string." : ""}`,
				inputSchema,
				annotations: {
					readOnlyHint: tool.name === "read" || tool.name === "glob" || tool.name === "grep",
					destructiveHint: tool.name === "edit" || tool.name === "write" || tool.name === "bash",
					openWorldHint: tool.name === "bash",
				},
			};
		});

		async function preparePaths(name: string, args: Record<string, unknown>): Promise<Record<string, unknown>> {
			if (name === "edit") {
				const params = { input: args.patch as string };
				const inspection = editInspect("hashline", JSON.stringify(params));
				for (const target of inspection.paths) assertLocalPath(target);
				for (const op of inspection.fileOps) {
					assertLocalPath(op.path);
					if (op.to !== undefined) assertLocalPath(op.to);
				}
				return params;
			}
			const key = name === "bash" ? "cwd" : "path";
			const target = args[key];
			if (typeof target !== "string") return args;
			if (name === "read" && target.startsWith("artifact://")) {
				const match = /^artifact:\/\/(mcp-\d+)(:[^?;]*)?$/.exec(target);
				const artifactPath = match ? artifacts.get(match[1]!) : undefined;
				if (!artifactPath) throw new Error("Unknown session output artifact.");
				return { ...args, path: `${artifactPath}${match![2] ?? ""}` };
			}
			assertLocalPath(target);
			if (name === "read" && target.includes("?")) {
				throw new Error("Read query selectors and model-backed image questions are disabled; read the file or image directly.");
			}
			if (name === "read" || name === "glob" || name === "grep") {
				const entries = await expandDelimitedPathEntries([target], cwd, { exclusionParts: name !== "read" });
				for (const entry of entries) assertLocalPath(entry.startsWith("!") ? entry.slice(1) : entry);
			}
			return args;
		}

		return {
			tools,
			call(name, args, id, signal) {
				if (disposed) return Promise.reject(new Error("Tool runtime is closed."));
				const tool = byName.get(name);
				if (!tool) return Promise.reject(new Error(`Tool is not enabled: ${name}`));
				const controller = new AbortController();
				const abort = () => controller.abort(signal.reason);
				if (signal.aborted) abort();
				else signal.addEventListener("abort", abort, { once: true });
				controllers.add(controller);
				// Native edit/write are exclusive; serialize calls so snapshots and
				// mutation order agree, including concurrent MCP requests.
				const execution = queue.then(async (): Promise<ToolRuntimeResult> => {
					controller.signal.throwIfAborted();
					validateInput(schemas.get(name)!, args);
					const prepared = await preparePaths(name, args);
					validateInput(toolWireSchema(tool), prepared);
					controller.signal.throwIfAborted();
					const result = await tool.execute(id, prepared, controller.signal);
					controller.signal.throwIfAborted();
					return {
						content: result.content.map(block => {
							if (block.type === "text") return { type: "text" as const, text: block.text };
							if (block.type === "image") return { type: "image" as const, data: block.data, mimeType: block.mimeType };
							throw new Error("Unsupported native result content type.");
						}),
						...(result.isError === undefined ? {} : { isError: result.isError }),
					};
				});
				queue = execution.catch(() => {});
				calls.add(execution);
				return execution.finally(() => {
					calls.delete(execution);
					controllers.delete(controller);
					signal.removeEventListener("abort", abort);
				});
			},
			close() {
				if (closePromise) return closePromise;
				disposed = true;
				for (const controller of controllers) controller.abort(new Error("MCP tool runtime is closing."));
				closePromise = (async () => {
					await Promise.allSettled([...calls]);
					const failures: unknown[] = [];
					try { await drainAbortedShells(sessionId); } catch (error) { failures.push(error); }
					releaseShellSessions(sessionId);
					for (const dispose of disposers) {
						try { dispose(); } catch (error) { failures.push(error); }
					}
					disposers.clear();
					store.clear();
					artifacts.clear();
					byName.clear();
					try { await fs.rm(artifactsDir, { recursive: true, force: true }); } catch (error) { failures.push(error); }
					if (failures.length) throw new AggregateError(failures, "MCP tool runtime cleanup failed.");
				})();
				return closePromise;
			},
		};
	} catch (error) {
		store.clear();
		await fs.rm(artifactsDir, { recursive: true, force: true });
		throw error;
	}
}
