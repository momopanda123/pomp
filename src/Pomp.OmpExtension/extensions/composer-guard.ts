import type { ExtensionAPI } from '@oh-my-pi/pi-coding-agent';
import fs from 'node:fs';
import path from 'node:path';
import { isPompHost, offeredHostTools, type Env } from '../lib/vs-state.ts';
interface GuardState { version: 1; active: boolean; paused: boolean; phase: string; root: string }
/** Cooperative engine dispatch guard, not an OS sandbox. The host opts in only with this module installed. */
export function composerGuardDecision(toolName: string, input: unknown, cwd: string, env: Env = process.env): string | undefined {
  if (env.POMP_COMPOSER_GUARD !== '1') return;
  // The state machine tool is the recovery/status channel; it performs its own checks.
  if (toolName === 'composer_workflow') return;
  let state: GuardState;
  try {
    if (!env.POMP_STATE_DIR) throw new Error('No state directory');
    const file = path.join(env.POMP_STATE_DIR, 'composer-guard.json');
    const stat = fs.lstatSync(file);
    if (stat.isSymbolicLink() || !stat.isFile() || stat.size > 16384) throw new Error('Invalid state file');
    state = JSON.parse(fs.readFileSync(file, 'utf8')) as GuardState;
    if (!state || state.version !== 1 || typeof state.active !== 'boolean' || typeof state.paused !== 'boolean' || typeof state.root !== 'string' || typeof state.phase !== 'string') throw new Error('Invalid state');
  } catch { return 'POMP Workflow guard state is unavailable. Restore the connection; no tool is admitted while its workflow policy is unknown.'; }
  if (!state.active) return;
  if (state.paused) return 'POMP Workflow is paused. Resume or cancel in the workflow panel; already running tools may finish.';
  const normalized = (value: string) => value.replaceAll('\\', '/').replace(/\/+$/, '').toLowerCase();
  if (normalized(cwd) !== normalized(state.root)) return 'POMP Workflow belongs to another workspace; refresh the host workflow before continuing.';
  // Host admission enforces its phase and permission kind again, including after approval.
  if (offeredHostTools(env).has(toolName)) return;
  if (['read', 'grep', 'find', 'ls', 'todo'].includes(toolName)) return;
  const action = input && typeof input === 'object' && 'action' in input ? input.action : undefined;
  if (toolName === 'lsp' && typeof action === 'string' && ['diagnostics', 'definition', 'references', 'symbols', 'hover'].includes(action)) return;
  return 'During POMP Workflow, direct writes, shell execution, child spawning and unknown extension tools are blocked. Use the advertised VS source tools and propose_workspace_edits; use separately approved VS execution only in Verification. This tool filter is not OS isolation.';
}
export default function composerGuard(pi: ExtensionAPI): void {
  if (!isPompHost()) return;
  pi.registerCommand('pomp-feedback-guard', {
    description: 'Report the case-feedback caller guard version (no task execution).',
    handler: async (_args, ctx) => { ctx.ui.notify('POMP case-feedback caller guard v1', 'info'); },
  });
  pi.on('tool_call', (event, ctx) => {
    if (event.toolName === 'present_case_feedback' && ctx.agent?.kind !== 'main') {
      return { block: true, reason: 'Present case feedback from the main conversation only. Subagents must return their cases to the parent; unknown caller identity is refused.' };
    }
    // The goal card asks the user to spend model usage: only the conversation the user is in may ask.
    if (event.toolName === 'request_goal_run' && ctx.agent?.kind !== 'main') {
      return { block: true, reason: 'Ask for a goal run from the main conversation only. Subagents report to the parent; unknown caller identity is refused.' };
    }
    const reason = composerGuardDecision(event.toolName, event.input, ctx.cwd);
    if (reason) return { block: true, reason };
  });
}
