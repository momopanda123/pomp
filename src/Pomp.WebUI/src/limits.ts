/**
 * Size limits the page enforces before it changes local state or posts anything: the host would refuse (or silently drop)
 * the same messages, which used to leave the composer or a dialog waiting for an answer that never came.
 */
import type { PermissionSource, UiToHost } from './protocol';

/** omp's per-message limit: the UTF-8 size of one prompt (1 MiB minus 2 KiB). */
export const MAX_MESSAGE_BYTES = 1_046_528;

/** The Visual Studio channel drops inbound envelopes above 4,194,304 characters; stay clearly below that. */
export const MAX_ENVELOPE_CHARS = 4_000_000;

/** Longest note that can accompany a denied permission request. */
export const MAX_NOTE_CHARS = 4000;

/** Input/editor field limit in UTF-16 units; keep over-limit drafts intact and reject rather than clipping. */
export const MAX_DIALOG_CHARS = MAX_MESSAGE_BYTES;

/** What the host puts before the user's words for attached paths: its header paragraph (under 512 bytes) and one "- path" line each. */
export function attachmentListBytes(paths: readonly string[]): number {
  return paths.length ? paths.reduce((bytes, path) => bytes + utf8Length(path) + 3, 512) : 0;
}

/** UTF-8 size of `text` without allocating it (lone surrogates count as U+FFFD = 3 bytes, like `TextEncoder`). */
export function utf8Length(text: string): number {
  let bytes = 0;
  for (let i = 0; i < text.length; i++) {
    const code = text.charCodeAt(i);
    if (code < 0x80) {
      bytes += 1;
    } else if (code < 0x800) {
      bytes += 2;
    } else if (code >= 0xd800 && code <= 0xdbff && i + 1 < text.length && (text.charCodeAt(i + 1) & 0xfc00) === 0xdc00) {
      bytes += 4; // a surrogate pair is one code point
      i++;
    } else {
      bytes += 3;
    }
  }
  return bytes;
}

/** Why a message was not sent (nothing was posted and the draft is kept). */
export type SendFailure =
  /** The prompt is longer than omp accepts. */
  | { reason: 'tooLarge'; bytes: number }
  /** Short enough as text, but the JSON envelope (escaped control characters, ...) would exceed the channel limit. */
  | { reason: 'tooLargeEncoded'; chars: number }
  /** A bounded field is kept verbatim but cannot be submitted until shortened. */
  | { reason: 'tooManyChars'; chars: number; max: number }
  /** The host could not be reached (`postMessage` threw or there is no host). */
  | { reason: 'hostUnavailable' }
  /** Another action (a new session) is still in progress. */
  | { reason: 'busy' }
  /** Refused, and the page already said why in the chat (a slash command's refusal notice). */
  | { reason: 'notified' };

/** Outcome of handing a message to the host: on failure nothing was posted and no local state changed. */
export type SendResult = { ok: true } | ({ ok: false } & SendFailure);

/** Null when the exact envelope fits the Visual Studio channel. */
export function envelopeRejection(envelope: unknown): SendFailure | null {
  let chars: number;
  try {
    chars = JSON.stringify(envelope).length;
  } catch {
    return { reason: 'hostUnavailable' }; // not serializable: it could not be posted either
  }
  return chars > MAX_ENVELOPE_CHARS ? { reason: 'tooLargeEncoded', chars } : null;
}

/**
 * Null when `text` may be sent inside `envelope` (the exact object that would be posted); otherwise the reason it may not. The attached
 * paths count too: the host lists them in the same prompt, so their number is bounded only by the message size.
 * The envelope is serialized like the channel does, so the length is the one the host would see.
 */
export function promptRejection(text: string, envelope: unknown, attachmentPaths: readonly string[] = []): SendFailure | null {
  const bytes = utf8Length(text) + attachmentListBytes(attachmentPaths);
  if (bytes > MAX_MESSAGE_BYTES) return { reason: 'tooLarge', bytes };
  return envelopeRejection(envelope);
}

/** JSON.NET/NdjsonWriter escape these separators even though JSON.stringify leaves them literal. */
function encodedFrameRejection(frame: unknown): SendFailure | null {
  const encoded = JSON.stringify(frame)
    .replaceAll(String.fromCharCode(0x85), '\\u0085')
    .replaceAll(String.fromCharCode(0x2028), '\\u2028')
    .replaceAll(String.fromCharCode(0x2029), '\\u2029');
  const bytes = utf8Length(encoded) + 1;
  return bytes > MAX_MESSAGE_BYTES ? { reason: 'tooLarge', bytes } : null;
}

/** No permission note is silently clipped; omp approval replies are checked against their real encoded frame as well. */
export function permissionResponseRejection(response: UiToHost['permission/respond'], source?: PermissionSource): SendFailure | null {
  if ((response.note?.length ?? 0) > MAX_NOTE_CHARS) {
    return { reason: 'tooManyChars', chars: response.note!.length, max: MAX_NOTE_CHARS };
  }
  const rejection = envelopeRejection({ type: 'permission/respond', payload: response });
  if (rejection || source !== 'ompApproval') return rejection;
  return encodedFrameRejection({ type: 'extension_ui_response', id: response.requestId, value: response.decision === 'deny' ? 'Deny' : 'Approve' });
}

/** Checks both the page envelope and the NDJSON reply the host forwards to omp (including escaping and the newline). */
export function dialogResponseRejection(response: UiToHost['dialog/respond']): SendFailure | null {
  const bytes = utf8Length(response.value ?? '');
  if (bytes > MAX_MESSAGE_BYTES) return { reason: 'tooLarge', bytes };
  const rejection = envelopeRejection({ type: 'dialog/respond', payload: response });
  if (rejection) return rejection;
  const answer = response.cancelled
    ? { cancelled: true }
    : response.confirmed !== undefined
      ? { confirmed: response.confirmed }
      : { value: response.value ?? '' };
  const frame = { type: 'extension_ui_response', id: response.requestId, ...answer };
  return encodedFrameRejection(frame);
}

/** `text` cut to at most `max` UTF-16 units without splitting a surrogate pair. */
export function capText(text: string, max: number): string {
  if (text.length <= max) return text;
  const cut = text.charCodeAt(max - 1);
  return text.slice(0, cut >= 0xd800 && cut <= 0xdbff ? max - 1 : max);
}
