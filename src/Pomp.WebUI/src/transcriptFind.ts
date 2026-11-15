// Find in the conversation (Ctrl+F): the text index and the DOM scan behind the find bar (components/FindBar.tsx, useTranscriptFind.ts).
// The scan only reads the transcript: matches are `Range`s painted with the CSS Custom Highlight API, so the React tree, selection,
// links and code blocks stay as they are. Text that is not in the DOM (the body of a closed tool card or routing brief) is searched
// in the data model and counted as a match of its item; opening the item turns it into real ranges.

/** A search that finds more than this keeps the first matches only (the counter shows "2000+"). */
export const MAX_MATCHES = 2000;

/** The class of the wrapper `display: contents` element around every transcript item (Transcript.tsx), keyed by the item's key. */
export const FIND_ITEM = 'find-item';
/** Set on the root of a card whose body text is not in the DOM while it is closed; the card reveals itself on `findReveal.ts`. */
export const FIND_COLLAPSED = 'data-find-collapsed';
/** The bodies a revealed card puts in the DOM (the matches a reveal lands on). */
export const FIND_BODY = '.tool-body, .team-brief-body';

const WHITESPACE = /[\r\n\t\u00a0\u2028\u2029]/g;

/**
 * The text as compared: line breaks, tabs and no-break spaces read as a space and, unless the case matters, lower case. The result
 * has exactly the length of the input, so an offset in it is an offset in the original.
 */
export function foldText(text: string, matchCase: boolean): string {
  const spaced = text.replace(WHITESPACE, ' ');
  if (matchCase) return spaced;
  const lower = spaced.toLowerCase();
  if (lower.length === spaced.length) return lower;
  // A few letters change length when lowered ('İ'): those stay as they are, so offsets still line up.
  let folded = '';
  for (const character of spaced) {
    const one = character.toLowerCase();
    folded += one.length === character.length ? one : character;
  }
  return folded;
}

/** The folded query; '' when there is nothing to look for (empty or only white space). */
export function foldQuery(query: string, matchCase: boolean): string {
  return query.trim() === '' ? '' : foldText(query, matchCase);
}

/** Start offsets of the non-overlapping occurrences of `needle` in `haystack` (both folded), at most `limit`. */
export function findOffsets(haystack: string, needle: string, limit: number = MAX_MATCHES): number[] {
  const found: number[] = [];
  if (!needle || limit <= 0) return found;
  for (let at = haystack.indexOf(needle); at >= 0 && found.length < limit; at = haystack.indexOf(needle, at + needle.length)) found.push(at);
  return found;
}

/** The index of the segment holding `offset`: the last one whose start is not after it (`starts` is ascending, the first is 0). */
export function segmentAt(starts: readonly number[], offset: number): number {
  let low = 0;
  let high = starts.length - 1;
  while (low < high) {
    const middle = (low + high + 1) >> 1;
    if (starts[middle]! <= offset) low = middle; else high = middle - 1;
  }
  return low;
}

/** How many matches the texts hold together, stopping at `limit` (the texts of a closed card). */
export function countMatches(texts: readonly string[], query: string, matchCase: boolean, limit: number = MAX_MATCHES): number {
  let count = 0;
  for (const text of texts) {
    if (count >= limit) break;
    count += findOffsets(foldText(text, matchCase), query, limit - count).length;
  }
  return count;
}

export interface FindMatch {
  /** The key of the transcript item holding the match. */
  key: string;
  /** The wrapper element of that item. */
  item: HTMLElement;
  /** The matched text in the DOM; null for a match in text that is not shown yet (a closed card), then `hidden` says which. */
  range: Range | null;
  /** The index among the not shown matches of the item. */
  hidden?: number;
}

export interface FindScan {
  matches: FindMatch[];
  /** There were more matches than {@link MAX_MATCHES}. */
  truncated: boolean;
}

/** Controls and card chrome are not conversation text: the card heads are the exception, their title and summary are. */
const SKIP = 'button:not(.tool-head):not(.team-brief-toggle), .tool-label, .tool-empty, .tool-errline, .tool-progress, .tool-skipline, script, style, [data-find-skip]';

const textFilter: NodeFilter = {
  acceptNode(node: Node): number {
    if (node.nodeType === Node.TEXT_NODE) return (node as Text).data === '' ? NodeFilter.FILTER_REJECT : NodeFilter.FILTER_ACCEPT;
    const element = node as Element;
    return element.matches(SKIP) || (element as HTMLElement).hidden ? NodeFilter.FILTER_REJECT : NodeFilter.FILTER_SKIP;
  },
};

/**
 * Every match of the folded `query` in the shown transcript, in reading order. Text that flows together (a paragraph with bold words,
 * a code span) is searched as one string, so "foo bar" finds `foo <code>bar</code>`; blocks, flex items and cells are separate.
 * `hiddenTexts(key)` gives the text a closed card of that item would show; it is searched when the item holds a closed card.
 */
export function scanTranscript(content: HTMLElement, query: string, matchCase: boolean, hiddenTexts: (key: string) => readonly string[] | undefined): FindScan {
  const matches: FindMatch[] = [];
  if (!query) return { matches, truncated: false };
  let truncated = false;
  const blocks = new Map<Element, Element>();
  const visible = new Map<Element, boolean>();

  for (const item of content.querySelectorAll<HTMLElement>(`:scope > .${FIND_ITEM}`)) {
    if (truncated) break;
    const key = item.dataset.findKey ?? '';
    const blockOf = (start: Element): Element => {
      const path: Element[] = [];
      let element = start;
      let block: Element | undefined;
      for (;;) {
        block = blocks.get(element);
        if (block) break;
        path.push(element);
        const display = getComputedStyle(element).display;
        if ((display !== 'inline' && display !== 'contents') || !element.parentElement || element === item) { block = element; break; }
        element = element.parentElement;
      }
      for (const passed of path) blocks.set(passed, block);
      return block;
    };

    let runBlock: Element | null = null;
    let nodes: Text[] = [];
    let starts: number[] = [];
    let text = '';
    const flush = (): void => {
      if (nodes.length > 0 && runBlock !== null) {
        let shown = visible.get(runBlock);
        if (shown === undefined) { shown = runBlock.checkVisibility?.() !== false; visible.set(runBlock, shown); }
        if (shown) {
          const room = MAX_MATCHES - matches.length;
          const offsets = findOffsets(foldText(text, matchCase), query, room + 1);
          if (offsets.length > room) { truncated = true; offsets.length = room; }
          for (const offset of offsets) {
            const first = segmentAt(starts, offset);
            const last = segmentAt(starts, offset + query.length - 1);
            const range = document.createRange();
            range.setStart(nodes[first]!, offset - starts[first]!);
            range.setEnd(nodes[last]!, offset + query.length - starts[last]!);
            matches.push({ key, item, range });
          }
        }
      }
      runBlock = null; nodes = []; starts = []; text = '';
    };

    const walker = document.createTreeWalker(item, NodeFilter.SHOW_ELEMENT | NodeFilter.SHOW_TEXT, textFilter);
    for (let node = walker.nextNode() as Text | null; node && !truncated; node = walker.nextNode() as Text | null) {
      const parent = node.parentElement;
      if (!parent) continue;
      const block = blockOf(parent);
      if (block !== runBlock) { flush(); runBlock = block; }
      nodes.push(node); starts.push(text.length); text += node.data;
    }
    if (!truncated) flush();

    if (!truncated && item.querySelector(`[${FIND_COLLAPSED}]`)) {
      const texts = hiddenTexts(key);
      if (texts) {
        const room = MAX_MATCHES - matches.length;
        const count = countMatches(texts, query, matchCase, room + 1);
        if (count > room) truncated = true;
        for (let index = 0; index < Math.min(count, room); index++) matches.push({ key, item, range: null, hidden: index });
      }
    }
  }
  return { matches, truncated };
}

const MATCH_HIGHLIGHT = 'pomp-find';
const CURRENT_HIGHLIGHT = 'pomp-find-current';

/** The CSS Custom Highlight API is there (Chromium 105+, so WebView2 evergreen); without it the search still counts and scrolls, but paints nothing. */
export function highlightsSupported(): boolean {
  return typeof CSS !== 'undefined' && 'highlights' in CSS && typeof Highlight === 'function';
}

/** Paints every match, and the current one on top of it with its own color. */
export function paintHighlights(matches: readonly FindMatch[], current: number): void {
  if (!highlightsSupported()) return;
  const all = new Highlight(...matches.flatMap(match => (match.range ? [match.range] : [])));
  CSS.highlights.set(MATCH_HIGHLIGHT, all);
  const range = matches[current]?.range;
  if (range) {
    const mark = new Highlight(range);
    mark.priority = 1;
    CSS.highlights.set(CURRENT_HIGHLIGHT, mark);
  } else CSS.highlights.delete(CURRENT_HIGHLIGHT);
}

export function clearHighlights(): void {
  if (!highlightsSupported()) return;
  CSS.highlights.delete(MATCH_HIGHLIGHT);
  CSS.highlights.delete(CURRENT_HIGHLIGHT);
}
