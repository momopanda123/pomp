import DOMPurify from 'dompurify';
import { Marked, type Token } from 'marked';

/**
 * Markdown pipeline for assistant text:
 *   text --lexer--> top-level blocks --(per block) marked --> HTML --DOMPurify--> sanitized DOM --decorate--> HTML string
 * Rendering block by block lets the UI re-render only the block that changed while a message streams in.
 */

const escapeHtml = (text: string): string =>
  text.replace(/&/g, '&amp;').replace(/</g, '&lt;').replace(/>/g, '&gt;').replace(/"/g, '&quot;');

const marked = new Marked({
  gfm: true,
  breaks: false,
  renderer: {
    // Raw HTML in a model answer is shown as text (e.g. `List<int>` outside a code span), never interpreted.
    html: ({ text }) => escapeHtml(text),
    // The UI is offline with a strict CSP: images are not rendered, the alt text is shown instead.
    image: ({ text }) => escapeHtml(text),
    checkbox: ({ checked }) => (checked ? '☑ ' : '☐ '),
  },
});

export interface MarkdownBlocks {
  /** Top-level blocks in order (blank-line and link-definition tokens dropped). */
  blocks: Token[];
  /**
   * The link reference definitions (`[ref]: url "title"`) of the whole text, serialized. The lexer resolves `[text][ref]` in
   * any block against all definitions, including ones that come later, so a block can keep its source text (`raw`) and still
   * change its output when a definition streams in or changes. Callers that cache per block must include this in the key.
   */
  definitions: string;
}

/** Stable text form of the link definitions of a lexed document ('' when there are none). */
function serializeDefinitions(links: Record<string, { href?: string | null; title?: string | null }> | undefined): string {
  if (!links) return '';
  return Object.keys(links)
    .sort()
    .map((label) => `${label}\u0000${links[label]?.href ?? ''}\u0000${links[label]?.title ?? ''}`)
    .join('\u0001');
}

/** Lexes `text` into top-level blocks plus the definitions they were resolved against. */
export function lexBlocks(text: string): MarkdownBlocks {
  try {
    const tokens = marked.lexer(text);
    return {
      blocks: tokens.filter((token) => token.type !== 'space' && token.type !== 'def'),
      definitions: serializeDefinitions(tokens.links),
    };
  } catch {
    // Pathological input must not take the transcript down: show it as one plain paragraph.
    return { blocks: [{ type: 'paragraph', raw: text, text, tokens: [{ type: 'text', raw: text, text }] }], definitions: '' };
  }
}

// Defense in depth: marked already escapes raw HTML, but everything is still allow-listed before it reaches the DOM.
const PURIFY_CONFIG = {
  ALLOWED_TAGS: [
    'p', 'br', 'hr', 'strong', 'b', 'em', 'i', 'del', 's', 'code', 'pre', 'blockquote', 'ul', 'ol', 'li',
    'h1', 'h2', 'h3', 'h4', 'h5', 'h6', 'a', 'table', 'thead', 'tbody', 'tfoot', 'tr', 'th', 'td', 'span', 'div',
    'sup', 'sub', 'kbd', 'mark',
  ],
  ALLOWED_ATTR: ['href', 'title', 'class', 'start', 'colspan', 'rowspan', 'align'],
  ALLOW_DATA_ATTR: false,
  RETURN_DOM_FRAGMENT: true as const,
};

const element = (tag: string, className: string): HTMLElement => {
  const node = document.createElement(tag);
  node.className = className;
  return node;
};

/** The normalized absolute URL (IDN hosts shown in their ASCII form), or the attribute as written when it does not parse. */
function linkDestination(href: string): string {
  try {
    return new URL(href).href;
  } catch {
    return href;
  }
}

/** Trusted post-processing of the already sanitized fragment: safe links, code block chrome, scrollable tables. */
function decorate(root: DocumentFragment, copyLabel: string, stageLabel?: string, stageHint?: string): void {
  for (const a of Array.from(root.querySelectorAll('a'))) {
    const href = a.getAttribute('href') ?? '';
    if (/^https?:/i.test(href)) {
      a.setAttribute('target', '_blank');
      a.setAttribute('rel', 'noopener noreferrer');
      // The WebView status bar is off, so the tooltip is the only place that shows where a link really goes
      // (link text and an optional markdown title are model-controlled).
      const destination = linkDestination(href);
      const title = a.getAttribute('title');
      a.setAttribute('title', title ? `${title}\n${destination}` : destination);
    } else {
      // Only web links can be opened (the host hands them to the browser). Others would navigate the chat page itself:
      // keep the text, drop the link.
      a.replaceWith(...Array.from(a.childNodes));
    }
  }

  for (const pre of Array.from(root.querySelectorAll('pre'))) {
    const lang = /(?:^|\s)language-([\w#+.-]+)/.exec(pre.querySelector('code')?.className ?? '')?.[1] ?? '';
    const wrap = element('div', 'codeblock');
    const head = element('div', 'codeblock-head');
    const label = element('span', 'codeblock-lang');
    label.textContent = lang;
    const copy = element('button', 'codeblock-copy') as HTMLButtonElement;
    copy.type = 'button';
    copy.dataset.copy = '';
    copy.textContent = copyLabel;
    head.append(label, copy);
    if (stageLabel) {
      const stage = element('button', 'codeblock-stage') as HTMLButtonElement;
      stage.type = 'button'; stage.dataset.stage = ''; stage.textContent = stageLabel; stage.title = stageHint ?? stageLabel;
      stage.disabled = !pre.textContent?.trim() || (pre.textContent?.length ?? 0) > 40000;
      head.append(stage);
    }
    pre.replaceWith(wrap);
    wrap.append(head, pre);
  }

  for (const table of Array.from(root.querySelectorAll('table'))) {
    const wrap = element('div', 'table-wrap');
    table.replaceWith(wrap);
    wrap.append(table);
  }
}

/** Sanitized HTML for one block. */
export function renderBlock(token: Token, copyLabel: string, stageLabel?: string, stageHint?: string): string {
  try {
    const fragment = DOMPurify.sanitize(marked.parser([token]), PURIFY_CONFIG);
    decorate(fragment, copyLabel, stageLabel, stageHint);
    const holder = document.createElement('div');
    holder.append(fragment);
    return holder.innerHTML;
  } catch {
    // One malformed block must not take the whole transcript down: fall back to escaped plain text.
    return `<p class="md-raw">${escapeHtml(token.raw)}</p>`;
  }
}
