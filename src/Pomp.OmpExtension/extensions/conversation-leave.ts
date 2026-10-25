/**
 * pomp conversation leave (omp extension module; docs/architecture.md, conversation deletion).
 *
 * The engine keeps a finished subagent live and idle for follow-up messages and parks it only after `task.agentIdleTtlMs` (7 minutes
 * by default). Until then the subagent holds its transcript (`<conversation>/<id>.jsonl`) open, so a conversation the user just left
 * could not go to the Recycle Bin: the host deletes the current conversation right after starting a new one. Before the main
 * conversation switches (new, fork, resume), its idle subagents are parked now, through the engine's own park: it closes the
 * transcript and keeps the subagent revivable when the user comes back. Running subagents are left alone.
 * The engine modules are internal: when they are missing or changed shape nothing happens and the switch goes on.
 */
import * as path from 'node:path';
import type { ExtensionAPI } from '@oh-my-pi/pi-coding-agent';
import { isPompHost } from '../lib/vs-state.ts';

interface AgentRef { id: string; kind: string; status: string; session: unknown; sessionFile: string | null }
export interface Engine {
  list(): AgentRef[];
  park(id: string): Promise<void>;
}
interface RegistryModule { AgentRegistry?: { global?(): { list?(): AgentRef[] } } }
interface LifecycleModule { AgentLifecycleManager?: { global?(): { park?(id: string): Promise<void> } } }
interface LeavingSession { getSessionFile?(): string | undefined; getArtifactsDir?(): string | null }

/** The engine's agent registry and lifecycle manager, or null when this engine does not have them in the expected shape. */
export async function loadEngine(): Promise<Engine | null> {
  try {
    // Internal engine modules, not part of the extension API: imported at run time so a missing or moved module is a no-op.
    const registry = (await import('@oh-my-pi/pi-coding-agent/registry/agent-registry')) as unknown as RegistryModule;
    const lifecycle = (await import('@oh-my-pi/pi-coding-agent/registry/agent-lifecycle')) as unknown as LifecycleModule;
    const agents = registry.AgentRegistry?.global?.(); const manager = lifecycle.AgentLifecycleManager?.global?.();
    if (!agents?.list || !manager?.park) return null;
    return { list: () => agents.list!(), park: id => manager.park!(id) };
  } catch { return null; }
}

/** Parks the live idle subagents whose transcripts are inside `artifacts` (the conversation's own folder); returns their ids. */
export async function parkIdleSubagents(artifacts: string, engine: Engine): Promise<string[]> {
  // Windows paths (the host is Visual Studio): compared without case.
  const folder = (path.resolve(artifacts) + path.sep).toLowerCase();
  const idle = engine.list().filter(ref => ref.kind === 'sub' && ref.status === 'idle' && ref.session != null
    && typeof ref.sessionFile === 'string' && path.resolve(ref.sessionFile).toLowerCase().startsWith(folder));
  await Promise.allSettled(idle.map(ref => engine.park(ref.id)));
  return idle.map(ref => ref.id);
}

export default function conversationLeave(pi: ExtensionAPI, load: () => Promise<Engine | null> = loadEngine): void {
  if (!isPompHost()) return;
  pi.on('session_before_switch', async (_event, ctx) => {
    try {
      if (ctx?.agent?.kind === 'sub') return undefined;
      const leaving = ctx?.sessionManager as LeavingSession | undefined;
      const file = leaving?.getSessionFile?.();
      const artifacts = leaving?.getArtifactsDir?.() ?? (file?.endsWith('.jsonl') ? file.slice(0, -6) : undefined);
      if (!artifacts) return undefined;
      const engine = await load();
      if (engine) await parkIdleSubagents(artifacts, engine);
    } catch { /* never cancels a switch */ }
    return undefined;
  });
}
