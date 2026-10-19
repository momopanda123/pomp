/**
 * Remembers the element the user put the focus on (`data-pomp-last-focus`), so the host can put the focus back there
 * (`PompChatControl.RestoreFocus`, after a build or run). While Visual Studio moves its own windows around, the WebView gets and loses
 * the focus several times and Chromium then moves it to the first focusable element (the Chat tab), so neither
 * `document.activeElement` nor the element active at the last blur is reliable. A focus counts when it lands on a text field (Chromium
 * never moves the focus to one on its own), on the element the user just pressed the pointer on, or right after a key that moves the
 * focus (Tab, the arrows of a list).
 */
const ATTRIBUTE = 'data-pomp-last-focus';
/** A focus this soon after the pointer press or the navigation key is that input's. */
const USER_INPUT_MS = 1000;
const NAVIGATION_KEYS: ReadonlySet<string> = new Set(['Tab', 'ArrowUp', 'ArrowDown', 'ArrowLeft', 'ArrowRight', 'Home', 'End']);

export function installFocusMemory(): void {
  let pressed: { target: Node; at: number } | undefined;
  let navigated = Number.NEGATIVE_INFINITY;
  window.addEventListener('pointerdown', event => { if (event.target instanceof Node) pressed = { target: event.target, at: performance.now() }; }, true);
  window.addEventListener('keydown', event => { if (NAVIGATION_KEYS.has(event.key) && !event.ctrlKey && !event.altKey) navigated = performance.now(); }, true);
  document.addEventListener('focusin', event => {
    const target = event.target;
    if (!(target instanceof HTMLElement) || target === document.body) return;
    const now = performance.now();
    const byPointer = pressed !== undefined && now - pressed.at <= USER_INPUT_MS && (target === pressed.target || target.contains(pressed.target));
    if (!isTextField(target) && !byPointer && now - navigated > USER_INPUT_MS) return;
    document.querySelectorAll(`[${ATTRIBUTE}]`).forEach(element => { if (element !== target) element.removeAttribute(ATTRIBUTE); });
    target.setAttribute(ATTRIBUTE, '');
  }, true);
}

function isTextField(element: HTMLElement): boolean {
  return element instanceof HTMLTextAreaElement || element.isContentEditable
    || (element instanceof HTMLInputElement && !['button', 'checkbox', 'radio', 'range', 'submit', 'reset', 'file', 'color'].includes(element.type));
}
