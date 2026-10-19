/**
 * The partner of a conversation (docs/architecture.md §8.8): the host keeps the workspace's chat tabs in `<root>/.pomp/local/chat-tabs.json`
 * (`{ version: 1, main, conversations: [{ sessionId, partner: { kind, id }, open }] }`). A conversation that is not listed there is a POMP
 * conversation.
 */
import fs from 'node:fs';
import path from 'node:path';
import { directPath, resolveRole, ROLE_REGEX, type RoleInfo } from './agent-roles.ts';

const MAX_FILE_BYTES = 1024 * 1024;

export interface ConversationPartner { kind: 'agent' | 'team'; id: string }

/** The agent or team a conversation is with, or null for a POMP conversation. */
export function conversationPartner(root: string | undefined, sessionId: unknown): ConversationPartner | null {
  if (!root || typeof sessionId !== 'string' || !sessionId) return null;
  try {
    const file = path.join(root, '.pomp', 'local', 'chat-tabs.json');
    if (!fs.existsSync(file) || !directPath(file)) return null;
    const stat = fs.lstatSync(file);
    if (!stat.isFile() || stat.size > MAX_FILE_BYTES) return null;
    let text = fs.readFileSync(file, 'utf8');
    if (text.charCodeAt(0) === 0xfeff) text = text.slice(1);
    const data: unknown = JSON.parse(text);
    if (!data || typeof data !== 'object') return null;
    const { version, conversations } = data as { version?: unknown; conversations?: unknown };
    if (version !== 1 || !Array.isArray(conversations)) return null;
    for (const row of conversations) {
      const entry = row as { sessionId?: unknown; partner?: { kind?: unknown; id?: unknown } } | null;
      if (entry?.sessionId !== sessionId) continue;
      const kind = entry.partner?.kind; const id = entry.partner?.id;
      return (kind === 'agent' || kind === 'team') && typeof id === 'string' && ROLE_REGEX.test(id) ? { kind, id } : null;
    }
    return null;
  } catch {
    return null;
  }
}

/** The role an agent conversation is with, or null for a POMP or team conversation (or a role that no longer resolves). */
export function partnerRole(root: string | undefined, sessionId: unknown): RoleInfo | null {
  const partner = conversationPartner(root, sessionId);
  return partner?.kind === 'agent' ? resolveRole(partner.id, root) : null;
}
