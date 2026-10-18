// Keyboard chords of the page (host/hello.shortcuts; Tools > Options > Pomp > Shortcuts): "Ctrl+Alt+T" = optional Ctrl/Alt/Shift and one
// key (a letter, a digit, F1–F12, a named key such as Enter, Esc, Space, or one of , . ; '). '' = no shortcut. For a React event pass `event.nativeEvent`.
import { DEFAULT_SHORTCUTS, type PageShortcuts } from './protocol';

export interface Chord { ctrl: boolean; alt: boolean; shift: boolean; key: string }

const KEY_NAMES: Record<string, string> = {
  esc: 'Escape', escape: 'Escape', enter: 'Enter', return: 'Enter', space: ' ', tab: 'Tab', backspace: 'Backspace', delete: 'Delete', del: 'Delete',
  up: 'ArrowUp', down: 'ArrowDown', left: 'ArrowLeft', right: 'ArrowRight', home: 'Home', end: 'End', pageup: 'PageUp', pagedown: 'PageDown', insert: 'Insert', ins: 'Insert',
};
const KEY_LABELS: Record<string, string> = { Escape: 'Esc', ' ': 'Space' };

/** null for an empty or malformed chord (no key, two keys, an unknown word). */
export function parseChord(text: string | undefined): Chord | null {
  const parts = (text ?? '').split('+').map(part => part.trim()).filter(Boolean);
  if (parts.length === 0 || parts.length > 4) return null;
  const chord: Chord = { ctrl: false, alt: false, shift: false, key: '' };
  for (const part of parts) {
    const lower = part.toLowerCase();
    if (lower === 'ctrl' || lower === 'control') chord.ctrl = true;
    else if (lower === 'alt') chord.alt = true;
    else if (lower === 'shift') chord.shift = true;
    else if (chord.key) return null;
    else if (/^[a-z0-9]$/.test(lower) || /^f([1-9]|1[0-2])$/.test(lower)) chord.key = lower.toUpperCase();
    else if (KEY_NAMES[lower]) chord.key = KEY_NAMES[lower];
    else if (/^[,.;']$/.test(part)) chord.key = part;
    else return null;
  }
  return chord.key ? chord : null;
}

/** The chord written the canonical way ("Ctrl+Alt+T", "Esc"); '' for none or malformed. */
export function formatChord(text: string | undefined): string {
  const chord = parseChord(text);
  if (!chord) return '';
  return [chord.ctrl && 'Ctrl', chord.alt && 'Alt', chord.shift && 'Shift', KEY_LABELS[chord.key] ?? chord.key].filter(Boolean).join('+');
}

const PUNCTUATION_CODES: Readonly<Record<string, string>> = { Comma: ',', Period: '.', Semicolon: ';', Quote: "'" };

/** Letters, digits and , . ; ' by physical code (layout-independent, so a Korean IME layout works too); named keys by `key`. */
function eventKey(event: KeyboardEvent): string {
  const code = event.code ?? '';
  if (/^Key[A-Z]$/.test(code)) return code.slice(3);
  if (/^Digit[0-9]$/.test(code)) return code.slice(5);
  if (PUNCTUATION_CODES[code]) return PUNCTUATION_CODES[code]!;
  if (/^F([1-9]|1[0-2])$/.test(code)) return code;
  const key = event.key;
  return /^[a-z]$/i.test(key) ? key.toUpperCase() : key === 'Esc' ? 'Escape' : key;
}

/**
 * True when the key event is exactly this chord: the same Ctrl/Alt/Shift set, no Meta, not AltGraph (it types characters on many layouts),
 * not a repeat and not during IME composition.
 */
export function matchesShortcut(event: KeyboardEvent, text: string | undefined): boolean {
  const chord = parseChord(text);
  if (!chord || event.repeat || event.isComposing || event.keyCode === 229 || event.metaKey) return false;
  if (typeof event.getModifierState === 'function' && event.getModifierState('AltGraph')) return false;
  if (event.ctrlKey !== chord.ctrl || event.altKey !== chord.alt || event.shiftKey !== chord.shift) return false;
  return eventKey(event) === chord.key;
}

/** A chord that can be a page shortcut: Ctrl or Alt is held, or the key is F1–F12. A bare key or Shift+key types into the fields. */
export const isPageChord = (chord: Chord | null): chord is Chord => chord !== null && (chord.ctrl || chord.alt || /^F([1-9]|1[0-2])$/.test(chord.key));

/**
 * The host's shortcuts with absent, malformed or modifier-less chords replaced by the defaults ('' stays none); `send` is 'Enter' or
 * 'Ctrl+Enter'. Mirror of the host's UiShortcutChords.NormalizePayload.
 */
export function normalizeShortcuts(partial: Partial<PageShortcuts> | null | undefined): PageShortcuts {
  const value: PageShortcuts = { ...DEFAULT_SHORTCUTS };
  if (!partial || typeof partial !== 'object') return value;
  for (const key of ['sendTargetPicker', 'previousTarget', 'nextTarget', 'contextBasket', 'editorContext', 'liveFollow', 'liveBookmarks', 'permissionMode', 'allow', 'deny', 'permissionsView', 'toggleFocus'] as const) {
    const raw = partial[key];
    if (typeof raw !== 'string') continue;
    if (raw.trim() === '') value[key] = '';
    else if (isPageChord(parseChord(raw))) value[key] = formatChord(raw);
  }
  if (partial.send === 'Ctrl+Enter' || partial.send === 'Enter') value.send = partial.send;
  return value;
}
