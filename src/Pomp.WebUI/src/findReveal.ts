import { useEffect, useRef } from 'react';

// A card that keeps text out of the DOM while it is closed (a tool card, a routing brief) opens itself when the conversation search
// lands on a match in that text: it listens for its item's key here and opens.
const openers = new Map<string, Set<() => void>>();

/** Opens the closed cards of the transcript item with this key (synchronous callers wrap it in `flushSync`). */
export function revealForFind(key: string): void {
  openers.get(key)?.forEach(open => open());
}

/** `open` runs when the search needs the card of the item `key` open. */
export function useFindReveal(key: string, open: () => void): void {
  const latest = useRef(open);
  latest.current = open;
  useEffect(() => {
    const listener = (): void => latest.current();
    let listeners = openers.get(key);
    if (!listeners) openers.set(key, listeners = new Set());
    listeners.add(listener);
    return () => {
      listeners.delete(listener);
      if (listeners.size === 0) openers.delete(key);
    };
  }, [key]);
}
