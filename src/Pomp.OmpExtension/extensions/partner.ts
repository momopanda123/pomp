/**
 * An agent conversation (docs/architecture.md §8.8): the chat tab of one agent is its own conversation, and the agent itself is the partner.
 * The role's instructions join the main conversation's system prompt, its model and thinking level are selected (`/pomp-partner`, sent by
 * the host after every change of conversation), and tools.ts limits the tools to the role's. A POMP conversation gets back the model it had.
 */
import type { ThinkingLevel } from '@oh-my-pi/pi-agent-core';
import type { Model } from '@oh-my-pi/pi-ai';
import type { ExtensionAPI } from '@oh-my-pi/pi-coding-agent';
import type { RoleInfo } from '../lib/agent-roles.ts';
import { partnerRole } from '../lib/conversation-partner.ts';
import { isPompHost, pick, readVsState } from '../lib/vs-state.ts';
import { readRoleMemory, roleMemoryBlock } from './role-memory.ts';

const MARKER = '<pomp_conversation_partner';
const ROLE_MEMORY_MARKER = '<role_memory';

/** Tags inside the role's text cannot open or close the block. */
function neutralize(text: string): string {
  return text.replace(/<\s*(\/?)\s*pomp_conversation_partner/gi, '<$1pomp-conversation-partner');
}

export function partnerBlock(role: RoleInfo): string {
  const lines = [
    '<pomp_conversation_partner>',
    `In this conversation you are the agent \`${role.name}\` (${neutralize(role.title)}: ${neutralize(role.job)}). The user opened it with you directly, as your own conversation: answer as this agent yourself, and do not hand the user's messages to another agent named \`${role.name}\`.`,
    `Role: ${neutralize(role.description)}`,
    "Where the role's instructions below differ from the general instructions about who you are or how you work, follow the role. They do not change tool permissions, approvals or host policy.",
  ];
  if (role.instructions) lines.push('', '## Role instructions', neutralize(role.instructions));
  lines.push('</pomp_conversation_partner>');
  return lines.join('\n');
}

export default function partner(pi: ExtensionAPI): void {
  if (!isPompHost()) return;
  const root = (): string | undefined => readVsState()?.solutionRoot ?? process.env.POMP_SOLUTION_ROOT;
  const same = (a: Model | undefined, b: Model | undefined): boolean => !!a && !!b && a.provider === b.provider && a.id === b.id;
  // The model in force when a POMP conversation and when an agent conversation were last left. A conversation that did not choose its own
  // model inherits the one in force (a new conversation, or one with no saved model): when that is the agent's, it gets the POMP model back.
  let base: Model | undefined;
  let agentModel: Model | undefined;

  pi.on('session_before_switch', (_event, ctx) => {
    try {
      if (ctx?.agent?.kind === 'sub') return undefined;
      const current = ctx.models?.current?.();
      if (partnerRole(root(), ctx.sessionManager?.getSessionId?.())) agentModel = current; else base = current ?? base;
    } catch { /* never cancels a switch */ }
    return undefined;
  });

  pi.on('before_agent_start', (event, ctx) => {
    try {
      if (ctx?.agent?.kind === 'sub') return undefined;
      const role = partnerRole(root(), ctx.sessionManager?.getSessionId?.());
      const previous = event.systemPrompt.filter(part => !part.startsWith(MARKER) && !part.startsWith(ROLE_MEMORY_MARKER));
      const notes = role ? readRoleMemory(role.name, root()) : [];
      const next = role ? [...previous, partnerBlock(role), ...(notes.length ? [roleMemoryBlock(role.name, notes)] : [])] : previous;
      if (next.length !== event.systemPrompt.length || next.some((part, i) => part !== event.systemPrompt[i])) return { systemPrompt: next };
      return undefined;
    } catch {
      return undefined;
    }
  });

  // `/pomp-partner new`: the conversation was just started for its partner, so the role's model and thinking level are selected. A
  // conversation the engine switched back to keeps what it saved (the engine restores its model and thinking level itself).
  pi.registerCommand('pomp-partner', {
    description: "Select the conversation partner's model and thinking level, without inference (POMP host)",
    handler: async (args, ctx) => {
      if (ctx.agent?.kind === 'sub' || !ctx.models) return;
      const role = partnerRole(root(), ctx.sessionManager?.getSessionId?.());
      const current = ctx.models.current();
      const fresh = args.trim() === 'new';
      const spec = role && fresh && role.model !== 'default' ? role.model : undefined;
      const target = spec?.split(',').map(item => item.trim()).filter(Boolean).map(item => ctx.models.resolve(item)).find(Boolean);
      if (spec && !target) ctx.ui.notify(pick(`역할 모델(${spec})을 쓸 수 없어 지금 모델로 이어요.`, `The role's model (${spec}) is unavailable; the current model stays.`), 'warning');
      if (target && !same(current, target) && !await pi.setModel(target)) ctx.ui.notify(pick('역할 모델을 선택하지 못했어요.', "The role's model could not be selected."), 'warning');
      // A POMP conversation, or a new agent conversation whose role names no model, that inherited the last agent conversation's model.
      if (!target && (!role || fresh) && agentModel && base && same(current, agentModel) && !same(base, agentModel)) await pi.setModel(base);
      if (role && fresh && role.thinkingLevel) {
        try { pi.setThinkingLevel(role.thinkingLevel as ThinkingLevel); } catch { /* a level this model does not offer */ }
      }
      if (!role) { base = ctx.models.current(); agentModel = undefined; }
    },
  });
}
