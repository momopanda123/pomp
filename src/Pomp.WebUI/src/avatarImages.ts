import { useEffect, useReducer } from 'react';
import { newId } from './format';
import { onMessage, send } from './protocol';

/**
 * The images of role pictures. The agents state carries only each picture's image revision; a shown picture asks the host for its
 * image (`agents/command avatarImages`, a few per request so one answer stays small), and the page keeps it per role and revision.
 */
const BATCH = 10;
const cache = new Map<string, string | null>();
const asked = new Set<string>();
const queue = new Map<string, { scope: string; name: string }>();
const requests = new Map<string, { key: string; scope: string; name: string }[]>();
const listeners = new Set<() => void>();
let timer: number | undefined;
let unsubscribe: (() => void) | undefined;

const keyOf = (scope: string, name: string, rev: number): string => `${scope}:${name}:${rev}`;

function subscribe(): void {
  if (unsubscribe) return;
  unsubscribe = onMessage((message) => {
    if (message.type !== 'agents/reply') return;
    const items = requests.get(message.payload.requestId);
    if (!items) return;
    requests.delete(message.payload.requestId);
    for (const item of items) {
      asked.delete(item.key);
      cache.set(item.key, message.payload.ok ? message.payload.images?.[`${item.scope}:${item.name}`] ?? null : null);
    }
    for (const listener of [...listeners]) listener();
  });
}

function flush(): void {
  timer = undefined;
  const items = [...queue.entries()].slice(0, BATCH).map(([key, role]) => ({ key, ...role }));
  for (const item of items) queue.delete(item.key);
  if (!items.length) return;
  const requestId = newId();
  requests.set(requestId, items);
  if (!send('agents/command', { requestId, action: 'avatarImages', roles: items.map(({ scope, name }) => ({ scope, name })) })) {
    requests.delete(requestId);
    for (const item of items) { asked.delete(item.key); cache.set(item.key, null); }
  }
  if (queue.size) timer = window.setTimeout(flush, 0);
}

function request(scope: string, name: string, rev: number): void {
  const key = keyOf(scope, name, rev);
  if (cache.has(key) || asked.has(key)) return;
  subscribe();
  asked.add(key);
  queue.set(key, { scope, name });
  if (timer === undefined) timer = window.setTimeout(flush, 20);
}

/** Puts an image the page already has (the one just chosen) under the revision the host will report for it. */
export function primeAvatarImage(scope: string, name: string, rev: number, image: string): void {
  cache.set(keyOf(scope, name, rev), image);
}

/** The image of a role's picture (a data URL), once it arrived; undefined without an image or while it is on its way. */
export function useAvatarImage(scope: string | undefined, name: string, rev: number | undefined): string | undefined {
  const [, update] = useReducer((count: number) => count + 1, 0);
  useEffect(() => {
    if (!scope || !rev) return;
    listeners.add(update);
    request(scope, name, rev);
    return () => { listeners.delete(update); };
  }, [scope, name, rev]);
  return scope && rev ? cache.get(keyOf(scope, name, rev)) ?? undefined : undefined;
}
