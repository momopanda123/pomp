/**
 * pomp member stop (omp extension module; docs/architecture.md §8.12).
 *
 * The official engine has no RPC that stops one subagent. `/pomp-stop <agent id> --request <id>` does what the engine's own
 * collaboration "kill" does: abort that member's session and release it as a tombstone, so the main turn gets a cancelled result
 * and the other members go on. Only the main conversation may use it, only for a running member that conversation started.
 * The engine modules are internal: when they are missing or changed shape the command reports that and changes nothing.
 * The result goes back to the host as a `pomp-stop-result:` notification (base64 JSON), never into the transcript.
 */
import type { ExtensionAPI } from '@oh-my-pi/pi-coding-agent';
import { isPompHost } from '../lib/vs-state.ts';

const RESULT = 'pomp-stop-result:';
const ID = /^[A-Za-z0-9][A-Za-z0-9._-]{0,127}$/;
const MAX_DEPTH = 16;

interface AgentRef { id: string; kind: string; parentId?: string; status: string; session: { abort(options?: { reason?: string }): Promise<void> } | null }
export interface Engine {
  mainId: string;
  get(id: string): AgentRef | undefined;
  release(id: string, ref: AgentRef): Promise<boolean>;
}

/** The engine's agent registry and lifecycle manager, or null when this engine does not have them in the expected shape. */
export async function loadEngine(): Promise<Engine | null> {
  try {
    const registry: any = await import('@oh-my-pi/pi-coding-agent/registry/agent-registry');
    const lifecycle: any = await import('@oh-my-pi/pi-coding-agent/registry/agent-lifecycle');
    const agents = registry?.AgentRegistry?.global?.(); const manager = lifecycle?.AgentLifecycleManager?.global?.();
    if (typeof registry?.MAIN_AGENT_ID !== 'string' || typeof agents?.get !== 'function' || typeof manager?.release !== 'function') return null;
    return { mainId: registry.MAIN_AGENT_ID, get: id => agents.get(id), release: (id, ref) => manager.release(id, ref, { tombstone: true }) };
  } catch { return null; }
}

/** Stops one running member the main conversation started (directly or through other members). */
export async function stopMember(id: string, engine: Engine): Promise<{ stopped: boolean; error?: string }> {
  if (!ID.test(id)) return { stopped: false, error: 'Invalid agent id.' };
  const ref = engine.get(id);
  if (!ref || ref.kind !== 'sub') return { stopped: false, error: 'No such member in this conversation.' };
  let parent = ref.parentId;
  for (let depth = 0; parent && parent !== engine.mainId && depth < MAX_DEPTH; depth++) parent = engine.get(parent)?.parentId;
  if (parent !== engine.mainId) return { stopped: false, error: 'This member was not started by this conversation.' };
  if (ref.status !== 'running') return { stopped: false, error: 'The member is not running.' };
  if (ref.session) await ref.session.abort({ reason: 'Stopped by the user' });
  // A false release means the engine already replaced or ended it: the member is not running either way.
  await engine.release(id, ref);
  return { stopped: true };
}

export default function memberStop(pi: ExtensionAPI, load: () => Promise<Engine | null> = loadEngine): void {
  if (!isPompHost()) return;
  pi.registerCommand('pomp-stop', {
    description: 'Stop one running team member of this conversation (POMP host only)',
    handler: async (args: string, ctx: any) => {
      const match = /^\s*(\S+)\s+--request\s+([a-f0-9]{32})\s*$/.exec(args ?? '');
      const request = match?.[2] ?? '';
      const report = (result: Record<string, unknown>) => ctx.ui.notify(RESULT + Buffer.from(JSON.stringify({ request, ...result })).toString('base64'), 'info');
      try {
        if (!match) throw new Error('Usage: /pomp-stop <agent id> --request <id>');
        if (ctx.agent?.kind === 'sub') throw new Error('Only the main conversation can stop a member.');
        const engine = await load();
        if (!engine) { report({ id: match[1], stopped: false, available: false, error: 'Stopping one member is unavailable.' }); return; }
        report({ id: match[1], available: true, ...(await stopMember(match[1], engine)) });
      } catch (error) {
        report({ stopped: false, error: String(error instanceof Error ? error.message : error).slice(0, 500) });
      }
    },
  });
}
