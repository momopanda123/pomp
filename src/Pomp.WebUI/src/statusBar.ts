import { useEffect, useSyncExternalStore } from 'react';
import { send } from './protocol';

export type StatusLevel = 'error' | 'warning' | 'info';

/** The last notice the VS status bar could not get (the page's channel to Visual Studio failed); null once one gets through again. */
let undelivered: { text: string; level: StatusLevel } | null = null;
const listeners = new Set<() => void>();
const setUndelivered = (value: typeof undelivered): void => {
  if (value?.text === undelivered?.text && value?.level === undelivered?.level) return;
  undelivered = value;
  for (const listener of listeners) listener();
};

/**
 * Shows a short notice in the VS status bar (with errors and warnings also in the POMP log): where VS shows its own messages,
 * instead of text squeezed between the chat's controls. When the page cannot reach Visual Studio the notice cannot get there either,
 * so it is kept for the page's banner (useUndeliveredStatus).
 */
export function reportStatus(text: string, level: StatusLevel = 'error'): void {
  const line = text.replace(/\s+/gu, ' ').trim();
  if (!line) return;
  if (send('ui/status', { text: line, level })) setUndelivered(null);
  else setUndelivered({ text: line, level });
}

/** Reports `text` each time it becomes a new non-empty value (an unchanged notice is not repeated on every render). */
export function useStatusReport(text: string | null | undefined, level: StatusLevel = 'error'): void {
  useEffect(() => { if (text) reportStatus(text, level); }, [text, level]);
}

/** The notice that could not reach the VS status bar, for the banner above the chat; null while Visual Studio gets them. */
export function useUndeliveredStatus(): { text: string; level: StatusLevel } | null {
  return useSyncExternalStore(listener => { listeners.add(listener); return () => { listeners.delete(listener); }; }, () => undelivered);
}

/** The banner dismissed: nothing to show until the next notice fails. */
export function dismissUndeliveredStatus(): void { setUndelivered(null); }
