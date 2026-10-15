import type { ConversationPartner } from './conversationProtocol';
import type { SendTarget } from './protocol';
import type { TranscriptItem, UserItem } from './state';

/** Relayed subagent IRC lines are user items inside a turn, not the start of a new one. */
const IRC_LINE = /^\[IRC `[^`\r\n]+` → `[^`\r\n]+`\]\r?\n/;
/** The extension's routing brief at the start of a restored user message (docs/agent-teams.md §3). */
const ROUTING_BRIEF = /<!--\s*pomp-(team|agent|dm):([^\s>]+)\s*-->/;

/** Where a user message went: its badge target, else the routing brief of a restored message, else POMP. */
export function destinationOf(item: UserItem): SendTarget | undefined {
  if (item.target) return item.target;
  const brief = ROUTING_BRIEF.exec(item.text);
  if (!brief) return undefined;
  return { kind: brief[1] === 'dm' ? 'instance' : brief[1] as 'team' | 'agent', id: brief[2]! };
}

/**
 * The transcript of one partner tab. Default (POMP) shows every turn (badges tell destinations apart); an agent or team tab
 * shows only the turns sent to that partner, each from its user message up to the next one. `agentOfInstance` maps a direct
 * message's subagent id to its role name (live jobs, run history, the partner's remembered instance).
 */
export function itemsForPartner(items: TranscriptItem[], partner: ConversationPartner, agentOfInstance: (id: string) => string | undefined): TranscriptItem[] {
  if (partner.kind === 'main' || !partner.id) return items;
  const matches = (target?: SendTarget) => {
    if (!target) return false;
    if (partner.kind === 'team') return target.kind === 'team' && target.id.toLowerCase() === partner.id!.toLowerCase();
    return (target.kind === 'agent' && target.id === partner.id) || (target.kind === 'instance' && agentOfInstance(target.id) === partner.id);
  };
  const result: TranscriptItem[] = []; let include = false;
  for (const item of items) {
    // An answered question or permission request is recorded inside the turn that raised it, so it follows that turn like any other
    // item (an open card is not a transcript item: it shows in every tab until it is answered).
    if (item.kind === 'user' && !IRC_LINE.test(item.text.trimStart())) include = item.partnerKey === `${partner.kind}:${partner.id}` || matches(destinationOf(item));
    if (include) result.push(item);
  }
  return result;
}
