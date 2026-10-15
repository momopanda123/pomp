import type { WorkspaceFile, WorkspaceFilesPayload } from './protocol';

export const MENTION_LIMIT = 30;

export interface MentionRange {
  start: number;
  end: number;
  query: string;
}

export interface MentionState extends MentionRange {
  requestId?: string;
  pending: boolean;
  files: WorkspaceFile[];
  selected: number;
  truncated: boolean;
  error?: string;
}

export interface ComposerState {
  text: string;
  attachments: WorkspaceFile[];
  fileError?: string;
  pickerRequestId?: string;
  composing: boolean;
  mention: MentionState | null;
  dismissedMention?: string;
}

export const emptyComposer: ComposerState = {
  text: '',
  attachments: [],
  composing: false,
  mention: null,
};

/** A collapsed caret after an @ at a word boundary, never an email address or an already quoted token. */
export function mentionAt(text: string, caret: number, selectionEnd = caret): MentionRange | null {
  if (caret !== selectionEnd || caret < 0 || caret > text.length) return null;
  const match = /(?:^|[\s([{])@([^\s@"\]]*)$/u.exec(text.slice(0, caret));
  if (!match) return null;
  const query = match[1]!;
  return { start: caret - query.length - 1, end: caret, query };
}

const signature = (mention: MentionRange): string => `${mention.start}:${mention.end}:${mention.query}`;

/** Escape dismisses the whole token, not just the prefix before the caret: arrow keys alone must not reopen it. */
function dismissalSignature(text: string, mention: MentionRange): string {
  const rest = /^[^\s@"\]]*/u.exec(text.slice(mention.end))?.[0] ?? '';
  const end = mention.end + rest.length;
  return `${mention.start}:${end}:${text.slice(mention.start, end)}`;
}

/** Windows file identities are case-insensitive; slashes returned by different IDE APIs can vary. */
export const fileIdentity = (path: string): string => path.replaceAll('/', '\\').toLowerCase();

export function validFiles(files: readonly WorkspaceFile[]): WorkspaceFile[] {
  return files.filter((file) => file && typeof file.path === 'string' && typeof file.fullPath === 'string' && file.fullPath.length > 0);
}

export function addAttachments(state: ComposerState, incoming: readonly WorkspaceFile[]): ComposerState {
  const attachments = state.attachments.slice();
  const seen = new Set(attachments.map((file) => fileIdentity(file.fullPath)));
  for (const file of validFiles(incoming)) {
    const id = fileIdentity(file.fullPath);
    if (seen.has(id)) continue;
    seen.add(id);
    attachments.push(file);
  }
  return { ...state, attachments };
}

/** Re-evaluate the active token without discarding results when only the selection event was repeated. */
export function updateMention(state: ComposerState, caret: number, selectionEnd: number, connected: boolean): ComposerState {
  const range = connected && !state.composing ? mentionAt(state.text, caret, selectionEnd) : null;
  if (!range || dismissalSignature(state.text, range) === state.dismissedMention) return state.mention ? { ...state, mention: null } : state;
  if (state.mention && signature(state.mention) === signature(range)) return state;
  return { ...state, dismissedMention: undefined, mention: { ...range, pending: true, files: [], selected: 0, truncated: false } };
}

export function dismissMention(state: ComposerState): ComposerState {
  return { ...state, dismissedMention: state.mention ? dismissalSignature(state.text, state.mention) : state.dismissedMention, mention: null };
}

/** Latest lookup and picker IDs are separate: an unrelated picker answer must not replace a mention list. */
export function receiveFiles(state: ComposerState, payload: WorkspaceFilesPayload): ComposerState {
  if (payload.requestId && payload.requestId === state.pickerRequestId) {
    const next = addAttachments(state, Array.isArray(payload.files) ? payload.files : []);
    return { ...next, pickerRequestId: undefined, fileError: payload.error };
  }
  if (!state.mention?.requestId || payload.requestId !== state.mention.requestId) return state;
  const all = validFiles(Array.isArray(payload.files) ? payload.files : []);
  return {
    ...state,
    mention: {
      ...state.mention,
      pending: false,
      files: all.slice(0, MENTION_LIMIT),
      selected: 0,
      truncated: payload.truncated === true || all.length > MENTION_LIMIT,
      error: payload.error,
    },
  };
}

export function selectMention(state: ComposerState, file: WorkspaceFile): ComposerState {
  const range = state.mention;
  if (!range) return state;
  const token = mentionSelectionToken(file);
  const suffix = state.text.slice(range.end);
  const spacer = suffix.startsWith(' ') ? '' : ' ';
  return {
    ...addAttachments(state, [file]),
    text: state.text.slice(0, range.start) + token + spacer + suffix,
    mention: null,
    dismissedMention: undefined,
    fileError: undefined,
  };
}

export function mentionSelectionToken(file: WorkspaceFile): string {
  return mentionToken(file.path) + ((file.kind === 'symbol' || file.kind === 'code') && file.line && Number.isSafeInteger(file.line) && file.line > 0 ? `#L${file.line}` : '');
}

/** The draft without the @mentions of files that left the basket: a removed file's mention would otherwise go out as plain text. */
export function dropMentions(text: string, removed: readonly WorkspaceFile[]): string {
  let next = text;
  for (const file of removed) {
    const token = mentionToken(file.path).replace(/[.*+?^${}()|[\]\\]/g, '\\$&');
    next = next.replace(new RegExp(`(^|\\s)${token}(?:#L\\d+)?(?=\\s|$) ?`, 'gu'), '$1');
  }
  return next;
}

/** The composer after files left the basket: their @mentions leave the draft too. */
export function withoutMentions(state: ComposerState, removed: readonly WorkspaceFile[]): ComposerState {
  if (!removed.length) return state;
  const text = dropMentions(state.text, removed);
  return text === state.text ? state : { ...state, text, mention: null };
}

export function mentionToken(path: string): string {
  return /\s/u.test(path) ? `@${JSON.stringify(path)}` : `@${path}`;
}

export function fileFromAttachment(path: string): WorkspaceFile {
  return { path, fullPath: path };
}
