import { useEffect, useRef, useState, type RefObject } from 'react';

/** Enter confirms only after this long: a key still held from the control that opened the popover must not confirm. */
const ARM_MS = 500;

export interface ConfirmPopoverProps {
  /** Wrapper of the control that opened it: a press outside this element cancels. */
  anchorRef: RefObject<HTMLElement | null>;
  testId: string;
  label: string;
  /** Heading and body are optional: a bare question goes in `question`. */
  title?: string;
  body?: string;
  question?: string;
  cancelLabel: string;
  confirmLabel: string;
  cancelTestId?: string;
  confirmTestId?: string;
  confirmDisabled?: boolean;
  /** Enter confirms (and the confirm button starts focused); otherwise the cancel button starts focused and Enter acts on the focused button. */
  enterConfirms?: boolean;
  /** Where the popover sits (the stylesheet decides); the default floats above the composer. */
  className?: string;
  onCancel: () => void;
  onConfirm: () => void;
}

/** A small yes/no popover. Escape cancels, Tab and the arrows move between the two buttons, a press outside cancels. */
export function ConfirmPopover({ anchorRef, testId, label, title, body, question, cancelLabel, confirmLabel, cancelTestId, confirmTestId, confirmDisabled = false, enterConfirms = false, className = '', onCancel, onConfirm }: ConfirmPopoverProps) {
  const ref = useRef<HTMLDivElement>(null);
  const cancel = useRef(onCancel);
  cancel.current = onCancel;
  const [armed, setArmed] = useState(false);
  useEffect(() => {
    const id = window.setTimeout(() => setArmed(true), ARM_MS);
    return () => window.clearTimeout(id);
  }, []);
  useEffect(() => {
    const buttons = ref.current?.querySelectorAll<HTMLButtonElement>('button');
    (enterConfirms ? buttons?.[buttons.length - 1] : buttons?.[0])?.focus({ preventScroll: true });
    const outside = (event: PointerEvent) => {
      if (event.target instanceof Node && !anchorRef.current?.contains(event.target)) cancel.current();
    };
    document.addEventListener('pointerdown', outside, true);
    return () => document.removeEventListener('pointerdown', outside, true);
  }, [anchorRef, enterConfirms]);
  return <div ref={ref} className={`confirm-popover ${className}`.trim()} role="dialog" aria-label={label} data-testid={testId} onKeyDown={event => {
    if (event.nativeEvent.isComposing || event.keyCode === 229) return;
    if (event.key === 'Escape') { event.preventDefault(); event.stopPropagation(); onCancel(); return; }
    if (event.key === 'Enter' && enterConfirms && !event.shiftKey && !event.ctrlKey && !event.altKey && !event.metaKey) {
      // Enter on the cancel button is its own click; anywhere else in the popover it confirms.
      if (document.activeElement instanceof HTMLButtonElement && document.activeElement.dataset.confirmRole === 'cancel') return;
      event.preventDefault(); event.stopPropagation();
      if (!confirmDisabled && armed && !event.repeat) onConfirm();
      return;
    }
    if (event.key === 'ArrowDown' || event.key === 'ArrowUp' || event.key === 'Tab') {
      const controls = [...event.currentTarget.querySelectorAll<HTMLButtonElement>('button')];
      const index = controls.indexOf(document.activeElement as HTMLButtonElement);
      event.preventDefault(); controls[(index + (event.key === 'ArrowUp' || event.shiftKey ? -1 : 1) + controls.length) % controls.length]?.focus();
    }
  }}>
    {title && <div className="confirm-popover-head"><strong>{title}</strong></div>}
    {(body || question) && <div className="confirm-popover-body"><p>{body ?? question}</p></div>}
    <div className="confirm-popover-actions">
      <button type="button" className="btn subtle small" data-testid={cancelTestId} data-confirm-role="cancel" onClick={onCancel}>{cancelLabel}</button>
      <button type="button" className="btn primary small" data-testid={confirmTestId} data-confirm-role="confirm" disabled={confirmDisabled} onClick={onConfirm}>{confirmLabel}</button>
    </div>
  </div>;
}
