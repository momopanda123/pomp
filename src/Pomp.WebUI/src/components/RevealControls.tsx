import type { ReactNode } from 'react';

/**
 * Bidirectional and zero-width formatting characters: invisible, yet able to reorder or hide code in an approval preview
 * ("Trojan Source"). ALM, ZWSP..RLM, LRE..RLO, WJ..invisible plus, LRI..PDI, BOM.
 */
const CONTROL = /[؜​-‏‪-‮⁠-⁤⁦-⁩﻿]/;
const CONTROLS = /[؜​-‏‪-‮⁠-⁤⁦-⁩﻿]/g;

/** {@link revealControls} as plain text, for attributes such as `title` and `aria-label` where no markup is possible. */
export function plainControls(text: string): string {
  return CONTROL.test(text) ? text.replace(CONTROLS, (char) => `⟨U+${char.charCodeAt(0).toString(16).toUpperCase().padStart(4, '0')}⟩`) : text;
}

/** The text with every such character replaced by a visible marker (e.g. ⟨U+202E⟩), so none of them acts on the text around it. */
export function revealControls(text: string): ReactNode {
  if (!CONTROL.test(text)) return text;

  const parts: ReactNode[] = [];
  let last = 0;
  CONTROLS.lastIndex = 0;
  for (let match = CONTROLS.exec(text); match !== null; match = CONTROLS.exec(text)) {
    if (match.index > last) parts.push(text.slice(last, match.index));
    const code = match[0].charCodeAt(0).toString(16).toUpperCase().padStart(4, '0');
    parts.push(
      <span key={match.index} className="ctl" title={`U+${code}`}>
        {`⟨U+${code}⟩`}
      </span>,
    );
    last = match.index + match[0].length;
  }

  if (last < text.length) parts.push(text.slice(last));
  return parts;
}
