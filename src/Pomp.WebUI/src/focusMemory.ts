/**
 * Remembers the element the user put the focus on (`data-pomp-last-focus`), so the host can put the focus back there
 * (`PompChatControl.RestoreFocus`, after a build or run). While Visual Studio moves its own windows around, the WebView gets and loses
 * the focus several times and Chromium then moves it to the first focusable element (the Chat tab), so neither
 * `document.activeElement` nor the element active at the last blur is reliable. Only a focus that follows the user's click or key
 * counts, or one on a text field (Chromium never moves the focus to a text field on its own).
 */
const ATTRIBUTE = 'data-pomp-last-focus';
/** A focus this soon after a pointer press or key press is the user's. */
const USER_INPUT_MS = 1000;

export function installFocusMemory(): void {
  let lastInput = Number.NEGATIVE_INFINITY;
  window.addEventListener('pointerdown', () => { lastInput = performance.now(); }, true);
  // Function keys and Ctrl/Alt chords go to Visual Studio (F5 starts a build): the focus moves that follow are Visual Studio's.
  window.addEventListener('keydown', event => { if (!/^F\d+$/.test(event.key) && !event.ctrlKey && !event.altKey) lastInput = performance.now(); }, true);
  document.addEventListener('focusin', event => {
    const target = event.target;
    if (!(target instanceof HTMLElement) || target === document.body) return;
    if (performance.now() - lastInput > USER_INPUT_MS && !isTextField(target)) return;
    document.querySelectorAll(`[${ATTRIBUTE}]`).forEach(element => { if (element !== target) element.removeAttribute(ATTRIBUTE); });
    target.setAttribute(ATTRIBUTE, '');
  }, true);
}

function isTextField(element: HTMLElement): boolean {
  return element instanceof HTMLTextAreaElement || element.isContentEditable
    || (element instanceof HTMLInputElement && !['button', 'checkbox', 'radio', 'range', 'submit', 'reset', 'file', 'color'].includes(element.type));
}
