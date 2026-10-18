import { useSyncExternalStore } from 'react';

/**
 * Composer controls the user can hide from the chat: right-click the composer background (styles: `.composer[data-hide~=…]`).
 * Hiding removes only the visible control; its shortcut, slash command and popup keep working. Menu order is this order.
 */
export const COMPOSER_CONTROLS = [
  'mention', 'slash', 'attach', 'basket', 'bookmarks', 'live', 'editorContext', 'contextMeter',
  'permission', 'target', 'model', 'thinking', 'fast', 'quota',
] as const;
export type ComposerControl = (typeof COMPOSER_CONTROLS)[number];

const STORAGE_KEY = 'pomp.composer.hidden';
const listeners = new Set<() => void>();
let hidden: readonly ComposerControl[] = load();

function load(): readonly ComposerControl[] {
  try {
    const raw: unknown = JSON.parse(localStorage.getItem(STORAGE_KEY) || '[]');
    return Array.isArray(raw) ? COMPOSER_CONTROLS.filter(control => raw.includes(control)) : [];
  } catch { return []; }
}

function update(next: readonly ComposerControl[]): void {
  hidden = next;
  try { localStorage.setItem(STORAGE_KEY, JSON.stringify(next)); } catch { /* A viewer preference; blocked storage keeps it for this page only. */ }
  for (const listener of listeners) listener();
}

function subscribe(listener: () => void): () => void {
  listeners.add(listener);
  return () => { listeners.delete(listener); };
}

/** The hidden controls, shared by every composer on the page (the main chat and the partner tabs). */
export function useHiddenComposerControls(): readonly ComposerControl[] {
  return useSyncExternalStore(subscribe, () => hidden);
}

export function setComposerControlHidden(control: ComposerControl, hide: boolean): void {
  update(COMPOSER_CONTROLS.filter(item => item === control ? hide : hidden.includes(item)));
}

export function showAllComposerControls(): void {
  update([]);
}

/**
 * Gives the keyboard back to a composer control after its popup closes, or to the composer's input when the control is hidden
 * (`display: none` cannot take focus, and the focus would otherwise fall to the page).
 */
export function focusControlOrInput(control: HTMLElement | null | undefined, input: HTMLElement | null | undefined, options?: FocusOptions): void {
  if (control && control.getClientRects().length > 0) control.focus(options);
  else input?.focus(options);
}
