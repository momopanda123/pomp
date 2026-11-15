/**
 * Where a voice capture's text sits in the composer's draft (docs/ui-protocol.md "dictation/state"). The host sends the capture's
 * whole text after every change, so the page replaces one span of the draft each time: `written` is what that span holds now, the
 * selection the capture started on until speech arrives (`spoken`), the dictated text after.
 */
export interface DictationAnchor {
  captureId: string;
  start: number;
  written: string;
  spoken: boolean;
}

export interface DictationEdit {
  text: string;
  anchor: DictationAnchor;
  /** Right after the dictated text: where the caret goes. */
  caret: number;
}

export function startDictationAnchor(captureId: string, text: string, caret: { start: number; end: number }): DictationAnchor {
  const start = Math.max(0, Math.min(caret.start, caret.end, text.length));
  const end = Math.max(start, Math.min(Math.max(caret.start, caret.end), text.length));
  return { captureId, start, written: text.slice(start, end), spoken: false };
}

/**
 * The draft with the capture's text in its span; null when the span no longer holds what was written there (the draft was changed
 * by something else), so the capture has no place in it. The text gets a space on a side that would otherwise run into a word.
 */
export function applyDictation(text: string, anchor: DictationAnchor, speech: string): DictationEdit | null {
  const end = anchor.start + anchor.written.length;
  if (text.slice(anchor.start, end) !== anchor.written) return null;
  const body = speech.replace(/\s+/gu, ' ').trim();
  // Nothing was said yet: the selection the capture started on stays.
  if (!body && !anchor.spoken) return { text, anchor, caret: end };
  const before = text.slice(0, anchor.start);
  const after = text.slice(end);
  const written = body ? `${before && !/\s$/u.test(before) ? ' ' : ''}${body}${after && !/^\s/u.test(after) ? ' ' : ''}` : '';
  return { text: before + written + after, anchor: { ...anchor, written, spoken: true }, caret: anchor.start + written.length };
}
