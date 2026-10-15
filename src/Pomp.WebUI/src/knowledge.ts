import type { KnowledgeAnchorStatus, KnowledgeItem, KnowledgePiece, KnowledgeState } from './protocol';

/** The rotation of default colors for a new knowledge (eight distinct hues); the user can pick any color. */
export const KNOWLEDGE_COLORS = ['#4f8ff7', '#34b27b', '#f0a43a', '#e5584f', '#a06be0', '#2cb5c0', '#e0699f', '#8fbf3a'] as const;
/** The colors the form offers as swatches: the rotation, then eight darker ones. */
export const KNOWLEDGE_SWATCHES = [...KNOWLEDGE_COLORS, '#1f5fc4', '#1d8257', '#b9741a', '#b3332b', '#7444b3', '#1b8792', '#b04476', '#64748b'] as const;
const NEUTRAL_COLOR = '#808080';
const COLOR = /^#[0-9a-f]{6}$/i;

export const isKnowledgeColor = (value: unknown): value is string => typeof value === 'string' && COLOR.test(value);

/** The first color of the rotation no knowledge uses yet; once all are used, the rotation starts over. */
export function defaultKnowledgeColor(items: readonly Pick<KnowledgeItem, 'color'>[]): string {
  const used = new Set(items.map(item => item.color.toLowerCase()));
  return KNOWLEDGE_COLORS.find(color => !used.has(color)) ?? KNOWLEDGE_COLORS[items.length % KNOWLEDGE_COLORS.length]!;
}

/** Piece text as the host compares it for duplicates: LF line breaks, no trailing white space on any line, no leading or trailing blank lines. */
export function normalizeKnowledgeText(text: string): string {
  return text.replace(/\r\n?/g, '\n').split('\n').map(line => line.trimEnd()).join('\n').replace(/^\n+|\n+$/g, '');
}

/** Two workspace paths are the same folder: case, slash direction and a trailing slash do not tell them apart (Windows paths). */
export function sameRoot(a: string | undefined | null, b: string | undefined | null): boolean {
  const key = (path: string) => path.replaceAll('/', '\\').replace(/\\+$/, '').toLowerCase();
  return typeof a === 'string' && typeof b === 'string' && key(a) === key(b);
}

export const savedPieces = (item: Pick<KnowledgeItem, 'pieces'>): KnowledgePiece[] => item.pieces.filter(piece => piece.status === 'saved');
export const candidatePieces = (item: Pick<KnowledgeItem, 'pieces'>): KnowledgePiece[] => item.pieces.filter(piece => piece.status === 'candidate');

/** Where an anchored piece comes from: `path a–b` (workspace-relative path, 1-based lines). */
export const anchorLabel = (anchor: { path: string; startLine: number; endLine: number }): string => `${anchor.path} ${anchor.startLine}–${anchor.endLine}`;

/** The backticks that fence `text` in a code block without being closed by it. */
function fenceFor(text: string): string {
  const longest = Math.max(0, ...(text.match(/`+/g) ?? []).map(run => run.length));
  return '`'.repeat(Math.max(3, longest + 1));
}

/** A saved piece as it is written into a conversation: its text, or for an anchored piece the location and a fenced block of the lines. */
export function pieceContent(piece: KnowledgePiece): string {
  const anchor = piece.anchor;
  if (!anchor) return piece.text;
  const fence = fenceFor(piece.text);
  return `\`${anchor.path}\` lines ${anchor.startLine}–${anchor.endLine}:\n${fence}\n${piece.text}\n${fence}`;
}

/** The original content of a knowledge: its saved pieces as written, one blank line apart. */
export const originalContent = (item: Pick<KnowledgeItem, 'pieces'>): string => savedPieces(item).map(pieceContent).join('\n\n');

/** The text a conversation gets: the optimized text while it is fresh, otherwise the original. */
export function usedContent(item: KnowledgeItem): string {
  return item.use === 'optimized' && item.optimized && !item.optimized.stale ? item.optimized.text : originalContent(item);
}

/** `optimized` is only claimed while the optimized text is fresh; an outdated or missing one is `stale` (the original is used meanwhile). */
export function knowledgeUse(item: Pick<KnowledgeItem, 'use' | 'optimized'>): 'original' | 'optimized' | 'stale' {
  return item.use !== 'optimized' ? 'original' : item.optimized && !item.optimized.stale ? 'optimized' : 'stale';
}

/** The knowledge a person can choose for a conversation, a role or a team: a knowledge made only of proposals has nothing to use yet. */
export const usableKnowledge = (items: readonly KnowledgeItem[]): KnowledgeItem[] => items.filter(item => !item.candidate);

/** A snapshot of the host's knowledge state in the shape the page uses. */
/** `hidden`: knowledge the host left out of its message (too large to send it all). */
export interface KnowledgeSnapshot { items: KnowledgeItem[]; selected: string[]; optimizing?: string; hidden?: number }

const ANCHOR_STATUS = new Set<KnowledgeAnchorStatus>(['current', 'moved', 'changed', 'missing']);
const text = (value: unknown, max: number): string | undefined => typeof value === 'string' ? value.slice(0, max) : undefined;
const words = (value: unknown): string[] => Array.isArray(value) ? [...new Set(value.filter((word): word is string => typeof word === 'string' && word.length > 0 && word.length <= 120))].slice(0, 200) : [];
const lineNumber = (value: unknown): number | undefined => typeof value === 'number' && Number.isInteger(value) && value >= 1 ? value : undefined;

function normalizePiece(raw: unknown): KnowledgePiece | null {
  if (!raw || typeof raw !== 'object') return null;
  const piece = raw as Record<string, unknown>;
  const id = text(piece.id, 80); const body = text(piece.text, 8000);
  if (!id || body === undefined) return null;
  const anchor = piece.anchor && typeof piece.anchor === 'object' ? piece.anchor as Record<string, unknown> : undefined;
  const path = text(anchor?.path, 1024); const startLine = lineNumber(anchor?.startLine); const endLine = lineNumber(anchor?.endLine);
  return {
    id, text: body, source: piece.source === 'agent' || piece.source === 'editor' ? piece.source : 'user', status: piece.status === 'candidate' ? 'candidate' : 'saved',
    createdAt: text(piece.createdAt, 40) ?? '',
    // A status the page does not know is not claimed as current.
    ...(path && startLine && endLine ? { anchor: { path, startLine, endLine, status: ANCHOR_STATUS.has(anchor?.status as KnowledgeAnchorStatus) ? anchor!.status as KnowledgeAnchorStatus : 'changed' } } : {}),
  };
}

function normalizeItem(raw: unknown): KnowledgeItem | null {
  if (!raw || typeof raw !== 'object') return null;
  const item = raw as Record<string, unknown>;
  const id = text(item.id, 80); const name = text(item.name, 120);
  if (!id || name === undefined) return null;
  const optimized = item.optimized && typeof item.optimized === 'object' ? item.optimized as Record<string, unknown> : undefined;
  const optimizedText = text(optimized?.text, 16000);
  const pieces = Array.isArray(item.pieces) ? item.pieces.map(normalizePiece).filter((piece): piece is KnowledgePiece => piece !== null) : [];
  return {
    id, name, color: isKnowledgeColor(item.color) ? item.color.toLowerCase() : NEUTRAL_COLOR, scope: item.scope === 'personal' ? 'personal' : 'project',
    use: item.use === 'optimized' ? 'optimized' : 'original', revision: typeof item.revision === 'number' ? item.revision : 1, createdAt: text(item.createdAt, 40) ?? '', updatedAt: text(item.updatedAt, 40) ?? '',
    roles: words(item.roles), teams: words(item.teams), candidate: item.candidate === true || (pieces.length > 0 && pieces.every(piece => piece.status === 'candidate')), pieces,
    ...(optimizedText !== undefined ? { optimized: { text: optimizedText, createdAt: text(optimized?.createdAt, 40) ?? '', stale: optimized?.stale !== false } } : {}),
  };
}

/** The host's state reduced to what the page can show (bounded lists, known values only); null when it carries no list. */
export function normalizeKnowledge(state: KnowledgeState): KnowledgeSnapshot | null {
  if (!Array.isArray(state.items)) return null;
  const items = state.items.map(normalizeItem).filter((item): item is KnowledgeItem => item !== null);
  const selected = words(state.selected);
  const hidden = (state as { hidden?: unknown }).hidden;
  return { items, selected, ...(typeof state.optimizing === 'string' && state.optimizing ? { optimizing: state.optimizing } : {}),
    ...(typeof hidden === 'number' && Number.isSafeInteger(hidden) && hidden > 0 ? { hidden } : {}) };
}
