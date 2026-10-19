// Word completion in the chat input: the rest of the word being typed, from this workspace's own words (sent prompts, the
// conversation, file paths the page has seen, words taken with Tab). Local and synchronous: a prefix lookup in a bounded index.
import type { WorkspaceFile } from './protocol';
import type { TranscriptItem } from './state';

const WORD = /[\p{L}\p{M}][\p{L}\p{M}\p{N}_]*/gu;
const WORD_RUN = /[\p{L}\p{M}\p{N}_]+$/u;
const MIN_WORD = 3;
const MAX_WORD = 48;
/** The index keeps at most this many words; past it, the lowest-ranked go down to {@link PRUNE_TO}. */
const MAX_WORDS = 20_000;
const PRUNE_TO = 16_000;
/** The most characters read from one prompt, message or path. */
const MAX_TEXT = 50_000;
const MAX_ACCEPTED = 500;
const MAX_PATHS = 5_000;
const STORAGE_PREFIX = 'pomp.wordCompletion:';
/** Tool arguments that name files or folders. */
const PATH_ARGS = ['path', 'paths', 'file', 'file_path', 'filePath', 'files'];

interface Entry {
  word: string;
  key: string;
  count: number;
  accepted: number;
  seen: number;
}

/** Words by their first two (lower-case) characters; ranked by frequency, Tab acceptances and recency. */
export class WordIndex {
  private readonly entries = new Map<string, Entry>();
  private readonly buckets = new Map<string, Set<Entry>>();
  private tick = 0;

  /** One occurrence of `word` (or, with `accepted`, that many Tab acceptances). The latest spelling is the one offered. */
  add(word: string, accepted = 0): void {
    if (word.length < MIN_WORD || word.length > MAX_WORD) return;
    const key = word.toLowerCase();
    // The completion is the word's text after the typed prefix, so its lower-case form must line up character by character.
    if (key.length !== word.length) return;
    let entry = this.entries.get(key);
    if (!entry) {
      entry = { word, key, count: 0, accepted: 0, seen: 0 };
      this.entries.set(key, entry);
      const head = key.slice(0, 2);
      let bucket = this.buckets.get(head);
      if (!bucket) this.buckets.set(head, bucket = new Set());
      bucket.add(entry);
    }
    entry.word = word;
    entry.seen = ++this.tick;
    if (accepted > 0) entry.accepted += accepted; else entry.count++;
    if (this.entries.size > MAX_WORDS) this.prune();
  }

  addText(text: string): void {
    for (const match of text.slice(0, MAX_TEXT).matchAll(WORD)) this.add(match[0]);
  }

  /** The rest of the best word that starts with `prefix` (any case) and is longer than it. */
  complete(prefix: string): string | undefined {
    const key = prefix.toLowerCase();
    if (key.length < 2 || key.length !== prefix.length) return undefined;
    const bucket = this.buckets.get(key.slice(0, 2));
    if (!bucket) return undefined;
    let best: Entry | undefined;
    let bestScore = -Infinity;
    for (const entry of bucket) {
      if (entry.key.length <= key.length || !entry.key.startsWith(key)) continue;
      const score = this.score(entry);
      if (score > bestScore || (score === bestScore && best !== undefined && entry.key.length < best.key.length)) { best = entry; bestScore = score; }
    }
    return best?.word.slice(prefix.length);
  }

  private score(entry: Entry): number {
    return Math.log2(1 + entry.count) + 3 * Math.log2(1 + entry.accepted) + 2 * entry.seen / this.tick;
  }

  private prune(): void {
    const ranked = [...this.entries.values()].sort((a, b) => this.score(a) - this.score(b));
    for (const entry of ranked.slice(0, ranked.length - PRUNE_TO)) {
      this.entries.delete(entry.key);
      const head = entry.key.slice(0, 2);
      const bucket = this.buckets.get(head);
      bucket?.delete(entry);
      if (bucket?.size === 0) this.buckets.delete(head);
    }
  }
}

/**
 * The word being typed right before a collapsed caret: at least two letters, starting with a letter, and nothing but
 * whitespace after the caret on its line (the grey rest is drawn there). None inside `code`, an @mention, a leading
 * /command or a URL.
 */
export function wordBeforeCaret(text: string, caret: number): string | null {
  if (caret < 2 || caret > text.length) return null;
  const lineEnd = text.indexOf('\n', caret);
  if (/\S/u.test(text.slice(caret, lineEnd < 0 ? text.length : lineEnd))) return null;
  const before = text.slice(Math.max(0, caret - MAX_WORD - 1), caret);
  const run = WORD_RUN.exec(before)?.[0];
  if (!run || run.length > MAX_WORD) return null;
  const prefix = run.replace(/^[\p{M}\p{N}_]+/u, '');
  if ((prefix.match(/\p{L}/gu)?.length ?? 0) < 2) return null;
  const token = /\S*$/u.exec(text.slice(Math.max(0, caret - 1024), caret))?.[0] ?? '';
  if (token.startsWith('@') || token.includes('://')) return null;
  if (token.startsWith('/') && !/\S/u.test(text.slice(0, caret - token.length))) return null;
  let ticks = 0;
  for (let index = text.indexOf('`'); index >= 0 && index < caret; index = text.indexOf('`', index + 1)) ticks++;
  return ticks % 2 === 0 ? prefix : null;
}

interface Accepted { word: string; count: number }

/** One workspace's words. The index is rebuilt when the conversation changes, so another conversation's messages drop out. */
export class WordStore {
  private index = new WordIndex();
  private session: string | null | undefined = null;
  private readonly ingested = new Set<string>();
  private readonly paths = new Set<string>();
  /** Words taken with Tab, least recently taken first (kept in this viewer's page storage). */
  private readonly accepted = new Map<string, Accepted>();
  private readonly storageKey: string;

  constructor(workspace: string) {
    this.storageKey = STORAGE_PREFIX + workspace;
    try {
      const value: unknown = JSON.parse(localStorage.getItem(this.storageKey) || '[]');
      if (Array.isArray(value)) {
        for (const item of value) {
          if (!Array.isArray(item) || typeof item[0] !== 'string' || typeof item[1] !== 'number' || !(item[1] > 0)) continue;
          this.accepted.set(item[0].toLowerCase(), { word: item[0], count: Math.min(Math.floor(item[1]), 1000) });
        }
      }
    } catch { /* unreadable storage: start over */ }
  }

  /** Reads what is new in the conversation; a different conversation (or the first call) starts the index over. */
  sync(history: readonly string[], sessionId: string | undefined, items: readonly TranscriptItem[]): void {
    if (this.session !== sessionId) {
      this.session = sessionId;
      this.ingested.clear();
      this.index = new WordIndex();
      for (const prompt of history) this.index.addText(prompt);
      for (const path of this.paths) this.index.addText(path);
      for (const { word, count } of this.accepted.values()) this.index.add(word, count);
    }
    for (const item of items) {
      if (this.ingested.has(item.key)) continue;
      if (item.kind === 'user') {
        this.index.addText(item.text);
        for (const file of item.attachments ?? []) this.addPath(file.path);
      } else if (item.kind === 'assistant') {
        if (!item.done) continue;
        this.index.addText(item.text);
      } else if (item.kind === 'tool') {
        for (const name of PATH_ARGS) {
          const value = item.args?.[name];
          for (const path of Array.isArray(value) ? value : [value]) if (typeof path === 'string') this.addPath(path);
        }
      }
      this.ingested.add(item.key);
    }
  }

  /** Paths and symbols of a file list the host sent (an @mention lookup or the file picker). */
  addFiles(files: readonly WorkspaceFile[]): void {
    for (const file of files) {
      if (typeof file?.path === 'string') this.addPath(file.path);
      if (typeof file?.symbol === 'string') this.index.addText(file.symbol);
    }
  }

  complete(prefix: string): string | undefined {
    return this.index.complete(prefix);
  }

  /** A word taken with Tab ranks higher from now on, also after the page reloads. */
  accept(word: string): void {
    const key = word.toLowerCase();
    const count = (this.accepted.get(key)?.count ?? 0) + 1;
    this.accepted.delete(key);
    this.accepted.set(key, { word, count: Math.min(count, 1000) });
    for (const oldest of this.accepted.keys()) {
      if (this.accepted.size <= MAX_ACCEPTED) break;
      this.accepted.delete(oldest);
    }
    this.index.add(word, 1);
    try {
      localStorage.setItem(this.storageKey, JSON.stringify([...this.accepted.values()].map(item => [item.word, item.count])));
    } catch { /* full or blocked: this page still remembers */ }
  }

  private addPath(path: string): void {
    const value = path.slice(0, 1024);
    if (this.paths.has(value)) return;
    if (this.paths.size >= MAX_PATHS) this.paths.delete(this.paths.values().next().value!);
    this.paths.add(value);
    this.index.addText(value);
  }
}

const stores = new Map<string, WordStore>();

export function wordStore(workspace: string): WordStore {
  let store = stores.get(workspace);
  if (!store) stores.set(workspace, store = new WordStore(workspace));
  return store;
}
