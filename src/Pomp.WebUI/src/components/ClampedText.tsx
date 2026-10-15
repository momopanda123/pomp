import { useLayoutEffect, useRef, useState } from 'react';
import { useT } from '../i18n';
import { revealControls } from './RevealControls';

/**
 * Text that may be very long (raw stderr of a failed engine start is 20+ lines): clamped to three lines with a "Details" /
 * "Collapse" toggle, and capped in height even when expanded, so it can never push the chat out of a short pane.
 * The toggle only appears when the text really is cut off at the current width.
 */
export function ClampedText({ text, className }: { text: string; className?: string }) {
  const t = useT();
  const ref = useRef<HTMLSpanElement>(null);
  const [open, setOpen] = useState(false);
  const [cut, setCut] = useState(false);

  useLayoutEffect(() => {
    const el = ref.current;
    if (!el) return;
    // Measured only while collapsed: an expanded text is never "cut", but its toggle must stay so it can be collapsed again.
    const measure = (): void => {
      if (!open) setCut(el.scrollHeight > el.clientHeight + 1);
    };
    measure();
    const observer = new ResizeObserver(measure);
    observer.observe(el);
    return () => observer.disconnect();
  }, [text, open]);

  return (
    <span className={className ? `clamped ${className}` : 'clamped'}>
      <span className={open ? 'clamp open' : 'clamp'} ref={ref} data-testid="clamp-text">
        {revealControls(text)}
      </span>
      {(cut || open) && (
        <button type="button" className="clamp-toggle" aria-expanded={open} data-testid="clamp-toggle" onClick={() => setOpen(!open)}>
          {open ? t('clamp.less') : t('clamp.more')}
        </button>
      )}
    </span>
  );
}
