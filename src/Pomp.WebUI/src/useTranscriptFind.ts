import { createRef, useEffect, useRef, type RefObject } from 'react';
import { flushSync } from 'react-dom';
import { revealForFind } from './findReveal';
import type { Translate } from './i18n';
import { toolBodyTexts } from './components/ToolCard';
import { teamBriefMatch } from './components/Messages';
import type { TranscriptItem } from './state';
import { FIND_BODY, clearHighlights, foldQuery, paintHighlights, scanTranscript, type FindMatch, type FindScan } from './transcriptFind';

// Find in the conversation (Ctrl+F): the engine behind the find bar. It lives outside React state, so a search that follows a
// streaming answer never re-renders the transcript: the bar subscribes to the snapshot, the transcript only holds the instance.

/** A typed query is searched after this pause. */
const TYPING_MS = 150;
/** Changes of the transcript (a streaming answer, an opened card) are searched again after this pause. */
const CHANGE_MS = 250;
/** Room kept between the current match and the edges of the visible transcript. */
const SCROLL_MARGIN = 12;

/** The query and the case switch outlive the bar: the next Ctrl+F finds them again. */
let rememberedQuery = '';
let rememberedMatchCase = false;

export interface FindSnapshot {
  open: boolean;
  query: string;
  matchCase: boolean;
  /** Matches found (the first ones only when `truncated`). */
  total: number;
  /** Index of the current match, -1 for none. */
  current: number;
  truncated: boolean;
}

interface FindEnvironment {
  items: readonly TranscriptItem[];
  t: Translate;
  /** Chords other page shortcuts own; Ctrl+F stays with them when it is one of those. */
  reserved: readonly string[];
}

type Mode = 'reset' | 'keep';

export class TranscriptFinder {
  environment: FindEnvironment = { items: [], t: key => key, reserved: [] };
  readonly inputRef = createRef<HTMLInputElement>();
  private snapshot: FindSnapshot = { open: false, query: rememberedQuery, matchCase: rememberedMatchCase, total: 0, current: -1, truncated: false };
  private readonly listeners = new Set<() => void>();
  private matches: FindMatch[] = [];
  private index = -1;
  private timer: number | undefined;
  private pending: Mode | undefined;
  private observer: MutationObserver | undefined;
  private restore: HTMLElement | null = null;

  private readonly wrap: RefObject<HTMLElement | null>;
  private readonly scroller: RefObject<HTMLElement | null>;
  private readonly content: RefObject<HTMLElement | null>;

  constructor(wrap: RefObject<HTMLElement | null>, scroller: RefObject<HTMLElement | null>, content: RefObject<HTMLElement | null>) {
    this.wrap = wrap; this.scroller = scroller; this.content = content;
  }

  subscribe = (listener: () => void): (() => void) => {
    this.listeners.add(listener);
    return () => { this.listeners.delete(listener); };
  };

  getSnapshot = (): FindSnapshot => this.snapshot;

  getOpen = (): boolean => this.snapshot.open;

  private update(patch: Partial<FindSnapshot>): void {
    const next = { ...this.snapshot, ...patch };
    if ((Object.keys(next) as (keyof FindSnapshot)[]).every(key => next[key] === this.snapshot[key])) return;
    this.snapshot = next;
    this.listeners.forEach(listener => listener());
  }

  /** Ctrl+F: opens the bar (seeded by a selection in the transcript, else with the last query) or, when it is open, focuses its input. */
  openOrFocus(): void {
    const seed = this.selectedText();
    const wasOpen = this.snapshot.open;
    if (!wasOpen) {
      const active = document.activeElement;
      this.restore = active instanceof HTMLElement && active !== document.body ? active : null;
    }
    if (seed) { rememberedQuery = seed; }
    flushSync(() => this.update({ open: true, ...(seed ? { query: seed } : {}) }));
    const input = this.inputRef.current;
    input?.focus();
    input?.select();
    if (!wasOpen) {
      const content = this.content.current;
      if (content) {
        this.observer = new MutationObserver(records => {
          // The bar redraws its own counter: that is not a change of the conversation.
          const bar = content.querySelector('.find-bar');
          if (!bar || !records.every(record => bar.contains(record.target))) this.schedule('keep', CHANGE_MS);
        });
        this.observer.observe(content, { childList: true, subtree: true, characterData: true });
      }
      this.run('reset');
    } else if (seed) this.run('reset');
  }

  /** Closes the bar and clears the highlights; `restoreFocus` puts the focus back where it was (Esc and the close button). */
  close(restoreFocus: boolean): void {
    if (!this.snapshot.open) return;
    window.clearTimeout(this.timer);
    this.timer = undefined; this.pending = undefined;
    this.observer?.disconnect();
    this.observer = undefined;
    this.matches = []; this.index = -1;
    clearHighlights();
    const target = this.restore;
    this.restore = null;
    if (restoreFocus) {
      const usable = target?.isConnected === true && target.getClientRects().length > 0 && !target.closest('[hidden], [inert]');
      (usable ? target : document.querySelector<HTMLElement>('[data-pomp-composer]'))?.focus();
    }
    this.update({ open: false, total: 0, current: -1, truncated: false });
  }

  setQuery(query: string): void {
    rememberedQuery = query;
    this.update({ query });
    if (foldQuery(query, this.snapshot.matchCase) === '') this.run('reset'); else this.schedule('reset', TYPING_MS);
  }

  toggleCase(): void {
    rememberedMatchCase = !this.snapshot.matchCase;
    this.update({ matchCase: rememberedMatchCase });
    this.run('reset');
  }

  /** Enter in the input: a search still waiting for its pause runs now and stops at its first match; otherwise it moves on. */
  enter(backwards: boolean): void {
    if (this.pending === 'reset') { this.run('reset'); return; }
    this.step(backwards ? -1 : 1);
  }

  step(direction: 1 | -1): void {
    if (this.pending === 'keep') this.run('keep');
    const count = this.matches.length;
    if (count === 0) return;
    this.go(this.index < 0 ? (direction > 0 ? 0 : count - 1) : (this.index + direction + count) % count);
  }

  /** The first line of the selection when it is inside the transcript. */
  private selectedText(): string {
    const selection = window.getSelection();
    const content = this.content.current;
    if (!selection || selection.isCollapsed || !content || !selection.anchorNode || !content.contains(selection.anchorNode)) return '';
    return selection.toString().split(/\r?\n/).map(line => line.trim()).find(Boolean)?.slice(0, 200) ?? '';
  }

  private schedule(mode: Mode, delay: number): void {
    if (!this.snapshot.open) return;
    this.pending = this.pending === 'reset' || mode === 'reset' ? 'reset' : 'keep';
    window.clearTimeout(this.timer);
    this.timer = window.setTimeout(() => this.run(this.pending ?? mode), delay);
  }

  private scan(): FindScan | null {
    const content = this.content.current;
    if (!content) return null;
    const { items, t } = this.environment;
    let byKey: Map<string, TranscriptItem> | undefined;
    const hiddenTexts = (key: string): readonly string[] | undefined => {
      byKey ??= new Map(items.map(item => [item.key, item]));
      const item = byKey.get(key);
      if (item?.kind === 'tool') return toolBodyTexts(item, t);
      if (item?.kind === 'user') { const brief = teamBriefMatch(item.text); return brief ? [brief[3]!.trim()] : undefined; }
      return undefined;
    };
    return scanTranscript(content, foldQuery(this.snapshot.query, this.snapshot.matchCase), this.snapshot.matchCase, hiddenTexts);
  }

  private commit(scan: FindScan, index: number): void {
    this.matches = scan.matches;
    this.index = index;
    paintHighlights(scan.matches, index);
    this.update({ total: scan.matches.length, current: index, truncated: scan.truncated });
  }

  /** Searches the transcript again. 'reset' lands on the first match at or after the current one (or the top of the view); 'keep' stays on the match it was on. */
  private run(mode: Mode): void {
    window.clearTimeout(this.timer);
    this.timer = undefined; this.pending = undefined;
    if (!this.snapshot.open) return;
    const before = this.matches[this.index];
    const beforeIndex = this.index;
    const old = this.matches;
    const from = this.visibleTop();
    const anchor = before ? this.topOf(before) : from;
    const scan = this.scan();
    if (!scan) return;
    if (scan.matches.length === 0) { this.commit(scan, -1); return; }
    if (mode === 'keep' && before) { this.commit(scan, this.sameMatch(scan.matches, old, before, beforeIndex)); return; }
    const first = this.firstFrom(scan.matches, anchor - 1);
    this.commit(scan, first);
    this.go(first);
  }

  /** Makes a match current: a match in text that is not shown yet opens its card first, then lands on the same one in the opened card. */
  private go(index: number): void {
    const match = this.matches[index];
    if (!match) return;
    if (match.range === null) {
      flushSync(() => revealForFind(match.key));
      const scan = this.scan();
      if (!scan) return;
      if (scan.matches.length === 0) { this.commit(scan, -1); return; }
      const ofItem = scan.matches.flatMap((other, at) => (other.key === match.key ? [at] : []));
      const inBody = ofItem.filter(at => scan.matches[at]!.range?.startContainer.parentElement?.closest(FIND_BODY));
      this.commit(scan, inBody[Math.min(match.hidden ?? 0, inBody.length - 1)] ?? ofItem[0] ?? Math.min(index, scan.matches.length - 1));
    } else if (index !== this.index) this.commit({ matches: this.matches, truncated: this.snapshot.truncated }, index);
    this.scrollToCurrent();
  }

  /** The same match after a search again: by its text node and offset, else by its place among the matches of its item, else by its number. */
  private sameMatch(list: readonly FindMatch[], old: readonly FindMatch[], before: FindMatch, beforeIndex: number): number {
    const container = before.range?.startContainer;
    if (container?.isConnected) {
      const offset = before.range!.startOffset;
      const exact = list.findIndex(match => match.range?.startContainer === container && match.range.startOffset === offset);
      if (exact >= 0) return exact;
    }
    const ordinal = old.slice(0, beforeIndex).filter(match => match.key === before.key).length;
    const ofItem = list.flatMap((match, at) => (match.key === before.key ? [at] : []));
    return ofItem[Math.min(ordinal, ofItem.length - 1)] ?? Math.min(beforeIndex, list.length - 1);
  }

  /** The top of the transcript that is not under the bar (client coordinates). */
  private visibleTop(): number {
    const scroller = this.scroller.current;
    if (!scroller) return 0;
    const bar = this.wrap.current?.querySelector<HTMLElement>('.find-bar');
    return Math.max(scroller.getBoundingClientRect().top, bar ? bar.getBoundingClientRect().bottom + SCROLL_MARGIN / 3 : 0);
  }

  private topOf(match: FindMatch): number {
    return (match.range?.getBoundingClientRect() ?? (match.item.firstElementChild ?? match.item).getBoundingClientRect()).top;
  }

  /** Matches are in reading order, so their tops ascend: the first one at or below `top`, wrapping to the first match. */
  private firstFrom(list: readonly FindMatch[], top: number): number {
    let low = 0;
    let high = list.length;
    while (low < high) {
      const middle = (low + high) >> 1;
      if (this.topOf(list[middle]!) >= top) high = middle; else low = middle + 1;
    }
    return low === list.length ? 0 : low;
  }

  /** Centers the current match in the part of the transcript the bar does not cover, unless it is comfortably in view already. */
  private scrollToCurrent(): void {
    const range = this.matches[this.index]?.range;
    const scroller = this.scroller.current;
    if (!range || !scroller) return;
    const rect = range.getBoundingClientRect();
    if (rect.width === 0 && rect.height === 0) return;
    const top = this.visibleTop();
    const bottom = scroller.getBoundingClientRect().bottom;
    if (rect.top >= top + SCROLL_MARGIN && rect.bottom <= bottom - SCROLL_MARGIN) return;
    scroller.scrollTop += rect.height > bottom - top - 2 * SCROLL_MARGIN ? rect.top - top - SCROLL_MARGIN : rect.top + rect.height / 2 - (top + bottom) / 2;
  }
}

function isFindKey(event: KeyboardEvent): boolean {
  return event.ctrlKey && !event.altKey && !event.shiftKey && !event.metaKey && (event.code === 'KeyF' || (event.code === '' && event.key.toLowerCase() === 'f'));
}

interface UseTranscriptFindOptions {
  wrap: RefObject<HTMLDivElement | null>;
  scroller: RefObject<HTMLDivElement | null>;
  content: RefObject<HTMLDivElement | null>;
  items: readonly TranscriptItem[];
  t: Translate;
  /** The chat tab is shown: Ctrl+F is the page's find only there. */
  enabled: boolean;
  /** The conversation on screen: another one closes the bar. */
  sessionId?: string;
  /** The chords of the page's other shortcuts: Ctrl+F stays with the one that has it. */
  reserved: readonly string[];
}

/** Ctrl+F opens the find bar of the conversation (F3 / Shift+F3 step while it is open); the bar closes when the conversation or the tab changes. */
export function useTranscriptFind({ wrap, scroller, content, items, t, enabled, sessionId, reserved }: UseTranscriptFindOptions): TranscriptFinder {
  const instance = useRef<TranscriptFinder | null>(null);
  instance.current ??= new TranscriptFinder(wrap, scroller, content);
  const engine = instance.current;
  engine.environment = { items, t, reserved };
  const enabledRef = useRef(enabled);
  enabledRef.current = enabled;

  useEffect(() => { engine.close(false); }, [engine, sessionId]);
  useEffect(() => { if (!enabled) engine.close(false); }, [engine, enabled]);
  useEffect(() => () => engine.close(false), [engine]);

  useEffect(() => {
    const onKeyDown = (event: KeyboardEvent): void => {
      if (!enabledRef.current || event.isComposing || event.keyCode === 229) return;
      const root = wrap.current;
      if (!root || root.closest('[hidden], [inert]')) return;
      if (isFindKey(event)) {
        const target = event.target;
        // A chord another shortcut owns, a terminal (Ctrl+F moves the cursor there) and a dialog on top keep the key.
        if (engine.environment.reserved.includes('Ctrl+F') || (target instanceof Element && target.closest('.xterm')) || document.querySelector('.overlay .dialog')) return;
        event.preventDefault();
        event.stopPropagation();
        if (!event.repeat) engine.openOrFocus();
        return;
      }
      if (event.key === 'F3' && !event.ctrlKey && !event.altKey && !event.metaKey && engine.getOpen()) {
        event.preventDefault();
        event.stopPropagation();
        engine.step(event.shiftKey ? -1 : 1);
      }
    };
    window.addEventListener('keydown', onKeyDown, true);
    return () => window.removeEventListener('keydown', onKeyDown, true);
  }, [engine, wrap]);

  return engine;
}
