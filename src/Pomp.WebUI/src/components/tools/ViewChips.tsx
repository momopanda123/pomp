import { useRef, type KeyboardEvent } from 'react';

/** A chip shows its label and badge only; `description` is accepted for older callers but never rendered (no tooltip, no aria-description). */
export interface ViewChip<K extends string> { id: K; label: string; badge?: number; description?: string }
/** Wrapped chips with roving focus and automatic activation; both arrow axes work at narrow widths. */
export function ViewChips<K extends string>({ prefix, label, views, active, onChange, inert = false }: {
  prefix: string; label: string; views: readonly ViewChip<K>[]; active: K; onChange: (id: K) => void; inert?: boolean;
}) {
  const buttons = useRef<Array<HTMLButtonElement | null>>([]);
  function key(event: KeyboardEvent<HTMLButtonElement>, index: number) {
    if (event.nativeEvent.isComposing || inert) return;
    let next: number;
    if (event.key === 'ArrowRight' || event.key === 'ArrowDown') next = (index + 1) % views.length;
    else if (event.key === 'ArrowLeft' || event.key === 'ArrowUp') next = (index + views.length - 1) % views.length;
    else if (event.key === 'Home') next = 0;
    else if (event.key === 'End') next = views.length - 1;
    else return;
    event.preventDefault(); onChange(views[next].id); buttons.current[next]?.focus();
  }
  return <div className="work-nav" role="tablist" aria-label={label}>
    {views.map((view, index) => <button key={view.id} ref={button => { buttons.current[index] = button; }} className={`work-chip${view.badge === 0 ? ' work-chip-empty' : ''}`} type="button" role="tab"
      id={`${prefix}-tab-${view.id}`} data-testid={`${prefix}-tab-${view.id}`} aria-controls={`${prefix}-${view.id}`} aria-selected={active === view.id}
      tabIndex={active === view.id ? 0 : -1} disabled={inert} onClick={() => onChange(view.id)} onKeyDown={event => key(event, index)}>
      {view.label}{view.badge !== undefined && <span className="tab-badge">{view.badge}</span>}
    </button>)}
  </div>;
}
