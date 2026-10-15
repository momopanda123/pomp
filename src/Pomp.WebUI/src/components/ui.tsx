import { useEffect, useId, useLayoutEffect, useMemo, useRef, useState, type CSSProperties, type KeyboardEvent, type KeyboardEventHandler, type ReactNode } from 'react';
import { useT } from '../i18n';
import { Icon, type IconName } from './Icon';
import '../styles/ui.css';

/** Shared page controls: switches, badges, tables, paging, search and form fields look the same on every tab. */

export function Switch({ checked, onChange, disabled = false, label, showLabel = true, testId }: {
  checked: boolean; onChange: (next: boolean) => void; disabled?: boolean; label: string; showLabel?: boolean; testId?: string;
}) {
  return <button type="button" role="switch" className="ui-switch" aria-checked={checked} aria-label={showLabel ? undefined : label} disabled={disabled}
    data-testid={testId} onClick={() => onChange(!checked)}>
    {showLabel && <span className="ui-switch-label">{label}</span>}<span className="ui-switch-track" aria-hidden="true"><span className="ui-switch-thumb" /></span>
  </button>;
}

export type BadgeTone = 'neutral' | 'info' | 'ok' | 'warn' | 'error' | 'accent';
export function Badge({ tone = 'neutral', icon, children, title, mono = false, testId }: {
  tone?: BadgeTone; icon?: IconName; children: ReactNode; title?: string; mono?: boolean; testId?: string;
}) {
  return <span className={`ui-badge ui-badge-${tone}${mono ? ' ui-mono' : ''}`} title={title} data-testid={testId}>{icon && <Icon name={icon} size={11} />}{children}</span>;
}

export function IconButton({ icon, label, onClick, disabled = false, testId, tone }: {
  icon: IconName; label: string; onClick: () => void; disabled?: boolean; testId?: string; tone?: 'danger';
}) {
  return <button type="button" className={`icon-btn ui-icon-btn${tone ? ' ui-icon-btn-' + tone : ''}`} title={label} aria-label={label} disabled={disabled} data-testid={testId} onClick={onClick}>
    <Icon name={icon} size={14} />
  </button>;
}

/** Below this width a table's rows wrap their cells instead of squeezing the text columns to a letter per line. */
const NARROW_TABLE = 240;
/** A grid table: `columns` is a CSS grid template shared by the header and every row. */
export function DataTable({ columns, head, children, empty = false, emptyRows = 2, label, testId, className = '' }: {
  columns: string; head: ReactNode[]; children?: ReactNode; empty?: boolean; emptyRows?: number; label?: string; testId?: string; className?: string;
}) {
  const table = useRef<HTMLDivElement>(null); const [narrow, setNarrow] = useState(false);
  useLayoutEffect(() => {
    const element = table.current; if (!element || typeof ResizeObserver === 'undefined') return;
    const apply = () => { const width = element.clientWidth; setNarrow(width > 0 && width < NARROW_TABLE); };
    apply(); const observer = new ResizeObserver(apply); observer.observe(element); return () => observer.disconnect();
  }, []);
  return <div ref={table} className={`ui-table${narrow ? ' ui-table-narrow' : ''} ${className}`} role="table" aria-label={label} data-testid={testId} style={{ '--ui-cols': columns } as CSSProperties}>
    <div className="ui-tr ui-th" role="row">{head.map((cell, index) => <span key={index} role="columnheader" className="ui-cell">{cell}</span>)}</div>
    {children}
    {empty && <div className="ui-empty-rows" aria-hidden="true" style={{ '--ui-empty-rows': emptyRows } as CSSProperties} />}
  </div>;
}
export function Row({ children, selected = false, testId, className = '', onDoubleClick }: {
  children: ReactNode; selected?: boolean; testId?: string; className?: string; onDoubleClick?: () => void;
}) {
  return <div className={`ui-tr${selected ? ' ui-tr-selected' : ''} ${className}`} role="row" data-testid={testId} onDoubleClick={onDoubleClick}>{children}</div>;
}
export function Cell({ children, className = '', title, wrap = false }: { children?: ReactNode; className?: string; title?: string; wrap?: boolean }) {
  return <span role="cell" className={`ui-cell${wrap ? ' ui-cell-wrap' : ''} ${className}`} title={title}>{children}</span>;
}
/** After an inline question closes without acting, puts the focus back on the button that asked it (found by its test id), so a keyboard user
 * stays where they were. */
export function refocus(testId: string): void {
  requestAnimationFrame(() => document.querySelector<HTMLElement>(`[data-testid="${CSS.escape(testId)}"]`)?.focus());
}
export function Actions({ children }: { children: ReactNode }) {
  return <span role="cell" className="ui-cell ui-actions">{children}</span>;
}

export const PAGE_SIZE = 20;
/** Client-side paging; the page resets when `resetKey` changes and stays in range when the list shrinks. */
export function usePaged<T>(items: readonly T[], resetKey: unknown = '', size = PAGE_SIZE) {
  const [page, setPage] = useState(0);
  const pages = Math.max(1, Math.ceil(items.length / size));
  useEffect(() => { setPage(0); }, [resetKey]);
  const current = Math.min(page, pages - 1);
  const slice = useMemo(() => items.slice(current * size, current * size + size), [items, current, size]);
  return { page: current, pages, slice, setPage, total: items.length, from: items.length ? current * size + 1 : 0, to: Math.min(items.length, current * size + size) };
}
export function Pager({ paged, testId }: { paged: { page: number; pages: number; total: number; from: number; to: number; setPage: (page: number) => void }; testId?: string }) {
  const t = useT();
  if (paged.pages <= 1) return null;
  return <div className="ui-pager" data-testid={testId}>
    <span className="ui-pager-range">{paged.from}–{paged.to} / {paged.total}</span>
    <IconButton icon="chevronLeft" label={t('ui.pager.previous')} disabled={paged.page === 0} onClick={() => paged.setPage(paged.page - 1)} testId={testId && `${testId}-prev`} />
    <span className="ui-pager-page">{paged.page + 1} / {paged.pages}</span>
    <IconButton icon="chevron" label={t('ui.pager.next')} disabled={paged.page >= paged.pages - 1} onClick={() => paged.setPage(paged.page + 1)} testId={testId && `${testId}-next`} />
  </div>;
}

export function SearchBox({ value, onChange, label, testId, disabled = false }: { value: string; onChange: (value: string) => void; label?: string; testId?: string; disabled?: boolean }) {
  const t = useT(); const name = label ?? t('ui.search');
  return <label className="ui-search"><Icon name="search" size={13} />
    <input type="search" value={value} placeholder={name} aria-label={name} data-testid={testId} disabled={disabled} spellCheck={false} onChange={event => onChange(event.target.value)} />
  </label>;
}
const nameCollator = new Intl.Collator(undefined, { numeric: true, sensitivity: 'base' });
/** The order of every name-keyed list: case-insensitive, numbers by value ("item 2" before "item 10"). */
export const byName = (left: string, right: string): number => nameCollator.compare(left, right);

/** Case-insensitive match of the query against any of the given texts. */
export function matches(query: string, ...texts: Array<string | undefined>): boolean {
  const q = query.trim().toLowerCase();
  return !q || texts.some(text => text?.toLowerCase().includes(q));
}

export function Toolbar({ children }: { children: ReactNode }) { return <div className="ui-toolbar">{children}</div>; }

export function Segmented<K extends string>({ options, value, onChange, label, disabled = false, testId }: {
  options: readonly { id: K; label: ReactNode; badge?: number; icon?: IconName; testId?: string }[]; value: K; onChange: (id: K) => void; label: string; disabled?: boolean; testId?: string;
}) {
  // One tab stop: the chosen option (the first while none is). The arrows, Home and End choose and focus another option, wrapping around.
  const chosen = options.findIndex(option => option.id === value); const stop = chosen < 0 ? 0 : chosen;
  const onKeyDown = (event: KeyboardEvent<HTMLDivElement>) => {
    if (disabled || options.length < 2 || event.altKey || event.ctrlKey || event.metaKey) return;
    const step = event.key === 'ArrowRight' || event.key === 'ArrowDown' ? 1 : event.key === 'ArrowLeft' || event.key === 'ArrowUp' ? -1 : 0;
    const next = step ? (stop + step + options.length) % options.length : event.key === 'Home' ? 0 : event.key === 'End' ? options.length - 1 : -1;
    if (next < 0) return;
    event.preventDefault();
    if (next !== chosen) onChange(options[next]!.id);
    event.currentTarget.querySelectorAll<HTMLButtonElement>('[role="radio"]')[next]?.focus();
  };
  return <div className="ui-seg" role="radiogroup" aria-label={label} data-testid={testId} onKeyDown={onKeyDown}>
    {options.map((option, index) => <button key={option.id} type="button" role="radio" aria-checked={value === option.id} className={value === option.id ? 'on' : ''}
      tabIndex={index === stop ? 0 : -1} disabled={disabled} data-testid={option.testId} onClick={() => onChange(option.id)}>
      {option.icon && <Icon name={option.icon} size={12} />}{option.label}{option.badge !== undefined && <span className="tab-badge">{option.badge}</span>}
    </button>)}
  </div>;
}

/** A form field: label above, control full width. */
export function Field({ label, children, error, className = '' }: { label: ReactNode; children: (id: string) => ReactNode; error?: string; className?: string }) {
  const id = useId();
  return <div className={`ui-field ${className}`}>
    <label htmlFor={id}>{label}</label>
    {children(id)}
    {error && <span className="ui-field-error" role="alert">{error}</span>}
  </div>;
}
export function FormActions({ children }: { children: ReactNode }) { return <div className="ui-form-actions">{children}</div>; }
export function Form({ children, testId, className = '', onKeyDown }: { children: ReactNode; testId?: string; className?: string; onKeyDown?: KeyboardEventHandler<HTMLDivElement> }) {
  return <div className={`ui-form ${className}`} data-testid={testId} onKeyDown={onKeyDown}>{children}</div>;
}
