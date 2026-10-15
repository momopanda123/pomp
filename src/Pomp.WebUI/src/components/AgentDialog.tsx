import { useEffect, useRef, type KeyboardEvent, type ReactNode } from 'react';

/** In-page dialog: focus stays inside, Escape closes, and closing restores the exact opener. */
export function AgentDialog({ testId, className = '', labelledBy, onClose, returnFocus, children }: {
  testId: string; className?: string; labelledBy: string; onClose: () => void; returnFocus?: HTMLElement | null; children: ReactNode;
}) {
  const root = useRef<HTMLDivElement>(null);
  const close = useRef(onClose); close.current = onClose;
  useEffect(() => {
    const opener = returnFocus ?? (document.activeElement instanceof HTMLElement ? document.activeElement : null);
    const dialog = root.current;
    const controls = () => [...(dialog?.querySelectorAll<HTMLElement>('button:not(:disabled), input:not(:disabled), select:not(:disabled), textarea:not(:disabled), [tabindex="0"]') ?? [])].filter(element => element.getClientRects().length > 0);
    // Put editors in the first field, confirmations on Cancel, and readers on Close.
    (dialog?.querySelector<HTMLElement>('input:not(:disabled)') ?? controls()[0] ?? dialog)?.focus({ preventScroll: true });
    return () => {
      if (opener?.isConnected && !opener.closest('[inert], [hidden]')) opener.focus({ preventScroll: true });
      else document.getElementById('tab-agents')?.focus({ preventScroll: true });
    };
  }, []);
  // A button that disables itself while its request runs drops focus to the page body: Escape still closes the dialog then.
  useEffect(() => {
    const lost = (event: globalThis.KeyboardEvent) => {
      if (event.key !== 'Escape' || event.defaultPrevented || event.isComposing) return;
      if (document.activeElement && document.activeElement !== document.body) return;
      event.preventDefault(); close.current();
    };
    document.addEventListener('keydown', lost);
    return () => document.removeEventListener('keydown', lost);
  }, []);
  // Use React's bubble phase: nested picker handlers run before the dialog (a native listener on this
  // element runs before React's delegated handlers and would discard the editor's unsaved fields).
  const keys = (event: KeyboardEvent<HTMLDivElement>) => {
    if (event.defaultPrevented || event.isPropagationStopped() || event.nativeEvent.isComposing || event.keyCode === 229) return;
    if (event.key === 'Escape') { event.preventDefault(); event.stopPropagation(); close.current(); }
    if (event.key === 'Tab') {
      const dialog = root.current;
      const items = [...(dialog?.querySelectorAll<HTMLElement>('button:not(:disabled), input:not(:disabled), select:not(:disabled), textarea:not(:disabled), [tabindex="0"]') ?? [])].filter(element => element.getClientRects().length > 0);
      const index = items.indexOf(document.activeElement as HTMLElement);
      if (!items.length) { event.preventDefault(); dialog?.focus(); }
      else if (event.shiftKey ? index <= 0 : index < 0 || index === items.length - 1) {
        event.preventDefault(); items[event.shiftKey ? items.length - 1 : 0]?.focus();
      }
    }
  };
  return <div className="agents-modal-backdrop" data-testid={testId}>
    <div ref={root} className={`agents-dialog ${className}`} role="dialog" aria-modal="true" aria-labelledby={labelledBy} tabIndex={-1} onKeyDown={keys}>{children}</div>
  </div>;
}
