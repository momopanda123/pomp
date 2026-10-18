/**
 * The page's tooltips: every `title` is shown by the page instead of WebView2. Native tooltips in WebView2 drop the next one when
 * the pointer moves straight from a control whose tooltip is up to a neighbouring one (the Fast toggle, whose neighbours all have
 * tooltips, never got one). While the pointer is over an element its `title` is held here (the element is marked `data-pomp-tip`), so
 * the native tooltip never shows, and it goes back when the pointer leaves. React's own title writes meanwhile (set or remove) land
 * in the held value, so what goes back is what React last wrote. The held text is the element's `aria-description` meanwhile, so a
 * screen reader still gets it. Like the native one the tooltip shows after a pause, at once when moving from one tooltip to the
 * next, and hides on a click, a key, a scroll or the wheel.
 */
const SHOW_DELAY_MS = 500;
/** How long after a tooltip hides the next one still shows at once (moving across a toolbar). */
const WARM_MS = 400;
const GAP = 4;
const MARGIN = 4;
const OWNER = '[title], [data-pomp-tip]';

export function installTooltips(): void {
  const tip = document.createElement('div');
  tip.className = 'pomp-tooltip';
  tip.setAttribute('aria-hidden', 'true');
  tip.hidden = true;
  document.body.append(tip);

  let owner: HTMLElement | null = null;
  /** The owner's title while the pointer is over it (null: none); `text` is what the tooltip shows. */
  let held: string | null = null;
  let text = '';
  /** The owner's aria-description is ours to remove on release. */
  let described = false;
  let timer = 0;
  let watch = 0;
  let hiddenAt = -Infinity;
  /** A click or key on the owner: no tooltip again until the pointer leaves it (as the native one). */
  let suppressed = false;

  // A title written as a property (`el.title = …`) bypasses the owner's setAttribute: take it from the attribute.
  const observer = new MutationObserver(() => {
    if (!owner || !owner.hasAttribute('title')) return;
    const title = owner.getAttribute('title');
    Element.prototype.removeAttribute.call(owner, 'title');
    hold(title);
  });

  function hold(title: string | null): void {
    held = title;
    text = (title ?? '').trim();
    if (owner && (described || !owner.hasAttribute('aria-description'))) {
      if (text) { Element.prototype.setAttribute.call(owner, 'aria-description', text); described = true; }
      else if (described) { Element.prototype.removeAttribute.call(owner, 'aria-description'); described = false; }
    }
    if (!text) hide();
    else if (!tip.hidden) place();
  }

  function take(element: HTMLElement): void {
    const title = element.getAttribute('title');
    element.removeAttribute('title');
    element.setAttribute('data-pomp-tip', '');
    // React writes `title` with setAttribute/removeAttribute: while the pointer is here those writes go to the held value.
    element.setAttribute = function (this: HTMLElement, name: string, value: string): void {
      if (name.toLowerCase() === 'title') hold(String(value));
      else Element.prototype.setAttribute.call(this, name, value);
    };
    element.removeAttribute = function (this: HTMLElement, name: string): void {
      if (name.toLowerCase() === 'title') hold(null);
      else Element.prototype.removeAttribute.call(this, name);
    };
    hold(title);
  }

  function release(): void {
    window.clearTimeout(timer);
    window.clearInterval(watch);
    observer.disconnect();
    if (owner) {
      const element = owner as unknown as Record<string, unknown>;
      delete element.setAttribute;
      delete element.removeAttribute;
      owner.removeAttribute('data-pomp-tip');
      if (described) owner.removeAttribute('aria-description');
      if (held !== null) owner.setAttribute('title', held);
    }
    owner = null;
    held = null;
    text = '';
    described = false;
    suppressed = false;
    hide();
  }

  function hide(): void {
    window.clearTimeout(timer);
    if (tip.hidden) return;
    tip.hidden = true;
    hiddenAt = performance.now();
  }

  function place(): void {
    if (!owner || !text) return;
    tip.textContent = text;
    // Measured at the origin: a fixed box near the right edge would be squeezed to fit what is left of the row.
    tip.style.left = '0px';
    tip.style.top = '0px';
    tip.hidden = false;
    const anchor = owner.getBoundingClientRect();
    const box = tip.getBoundingClientRect();
    const width = document.documentElement.clientWidth;
    const height = document.documentElement.clientHeight;
    const left = Math.max(MARGIN, Math.min(anchor.left + (anchor.width - box.width) / 2, width - box.width - MARGIN));
    const below = anchor.bottom + GAP;
    const top = below + box.height <= height - MARGIN ? below : Math.max(MARGIN, anchor.top - GAP - box.height);
    tip.style.left = `${Math.round(left)}px`;
    tip.style.top = `${Math.round(top)}px`;
  }

  function show(): void {
    if (!owner || !owner.isConnected || suppressed || !text) return;
    place();
  }

  document.addEventListener('pointerover', event => {
    if (event.pointerType === 'touch') return;
    const target = event.target instanceof Element ? event.target.closest<HTMLElement>(OWNER) : null;
    if (target === owner) return;
    const warm = !tip.hidden || performance.now() - hiddenAt < WARM_MS;
    release();
    if (!target || target instanceof HTMLIFrameElement) return;
    owner = target;
    take(target);
    observer.observe(target, { attributes: true, attributeFilter: ['title'] });
    // A control removed under the pointer gets no pointerout; let it go.
    watch = window.setInterval(() => { if (owner && !owner.isConnected) release(); }, 300);
    if (warm) show();
    else timer = window.setTimeout(show, SHOW_DELAY_MS);
  }, true);

  // Moving to another element is handled by its pointerover; only leaving the page ends here.
  document.addEventListener('pointerout', event => { if (!event.relatedTarget) release(); }, true);

  const quiet = (): void => { if (owner) suppressed = true; hide(); hiddenAt = -Infinity; };
  document.addEventListener('pointerdown', quiet, true);
  document.addEventListener('keydown', quiet, true);
  document.addEventListener('wheel', quiet, { capture: true, passive: true });
  // Only a scroll that moves the owner (the streaming transcript scrolls under a hovered composer button all the time).
  document.addEventListener('scroll', event => { if (owner && (event.target === document || (event.target instanceof Node && event.target.contains(owner)))) hide(); }, true);
  window.addEventListener('blur', release);
}
