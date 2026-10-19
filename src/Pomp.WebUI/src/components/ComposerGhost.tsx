import { useLayoutEffect, useRef, type RefObject } from 'react';

const MIRROR_PROPERTIES = ['fontFamily', 'fontSize', 'fontWeight', 'fontStyle', 'fontVariant', 'fontStretch',
  'lineHeight', 'letterSpacing', 'wordSpacing', 'textAlign', 'textIndent', 'textTransform', 'direction',
  'tabSize', 'paddingTop', 'paddingRight', 'paddingBottom', 'paddingLeft', 'whiteSpace', 'overflowWrap', 'wordBreak'] as const;

/** A clipped mirror follows the textarea's viewport without adding any height or scrollbars. */
export function ComposerGhost({ inputRef, text, prediction, testId = 'composer-ghost' }: { inputRef: RefObject<HTMLTextAreaElement | null>; text: string; prediction: string; testId?: string }) {
  const viewport = useRef<HTMLDivElement>(null);
  const mirror = useRef<HTMLDivElement>(null);
  useLayoutEffect(() => {
    const input = inputRef.current;
    const clip = viewport.current;
    const content = mirror.current;
    if (!input || !clip || !content) return;
    const sync = () => {
      const style = getComputedStyle(input);
      for (const property of MIRROR_PROPERTIES) content.style[property] = style[property];
      clip.style.left = style.borderLeftWidth;
      clip.style.top = style.borderTopWidth;
      clip.style.width = `${input.clientWidth}px`;
      clip.style.height = `${input.clientHeight}px`;
      content.style.width = `${input.clientWidth}px`;
      content.style.transform = `translate(${-input.scrollLeft}px, ${-input.scrollTop}px)`;
    };
    sync();
    input.addEventListener('scroll', sync);
    const resize = typeof ResizeObserver === 'undefined' ? null : new ResizeObserver(sync);
    resize?.observe(input);
    const theme = new MutationObserver(sync);
    theme.observe(document.documentElement, { attributes: true, attributeFilter: ['style', 'data-theme'] });
    return () => { input.removeEventListener('scroll', sync); resize?.disconnect(); theme.disconnect(); };
  }, [inputRef, text, prediction]);
  return <div ref={viewport} className="composer-ghost" aria-hidden="true" data-testid={testId}>
    <div ref={mirror} className="composer-ghost-mirror"><span className="composer-ghost-prefix">{text}</span><wbr /><span className="composer-ghost-text">{prediction}</span></div>
  </div>;
}
