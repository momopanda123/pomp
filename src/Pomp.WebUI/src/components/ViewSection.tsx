import { Children, Fragment, isValidElement, useId, useLayoutEffect, useRef, useState, type HTMLAttributes, type ReactNode, type Ref } from 'react';
import { useT } from '../i18n';
import { Icon } from './Icon';
import '../styles/work.css';

const STORAGE_KEY = 'pomp.sections';
/** Disclosure preference only; secret-handling components never access browser storage directly. */
export function rememberedSectionOpen(key: string): boolean | undefined {
  try {
    const value = JSON.parse(localStorage.getItem(STORAGE_KEY) || '{}')[key];
    return typeof value === 'boolean' ? !value : undefined;
  } catch { return undefined; }
}
function remembered(key: string): boolean { return rememberedSectionOpen(key) === false; }
/** A disclosure inside a section (an account, a provider's usage): the viewer's remembered choice, else `fallback` (which may change). */
export function useRememberedOpen(key: string, fallback: boolean): [boolean, (open: boolean) => void] {
  const [choice, setChoice] = useState(() => rememberedSectionOpen(key));
  return [choice ?? fallback, (open: boolean) => { setChoice(open); remember(key, !open); }];
}
function remember(key: string, collapsed: boolean): void {
  try {
    const raw: unknown = JSON.parse(localStorage.getItem(STORAGE_KEY) || '{}');
    const values = raw && typeof raw === 'object' && !Array.isArray(raw) ? raw : {};
    localStorage.setItem(STORAGE_KEY, JSON.stringify({ ...values, [key]: collapsed }));
  } catch { /* Storage is a per-viewer convenience; blocked storage must not break the UI. */ }
}

function hasContent(children: ReactNode): boolean {
  return Children.toArray(children).some(child => isValidElement<{ children?: ReactNode }>(child) && child.type === Fragment ? hasContent(child.props.children) : child !== null && child !== undefined && child !== '' && child !== 0 && typeof child !== 'boolean');
}

export interface ViewSectionProps extends Omit<HTMLAttributes<HTMLElement>, 'title'> {
  sectionKey: string; title: ReactNode; status?: ReactNode; actions?: ReactNode; active?: boolean;
  headingLevel?: 'h2' | 'h3'; headingId?: string; panelRef?: Ref<HTMLElement>;
  expanded?: boolean; onExpandedChange?: (expanded: boolean) => void; toggleTestId?: string;
  bodyClassName?: string; empty?: boolean; children: ReactNode;
}
/** One card/header/disclosure contract for Work, Tools, Accounts and agent lists. Bodies stay mounted. */
export function ViewSection({ sectionKey, title, status, actions, active = false, headingLevel: Heading = 'h3', headingId,
  panelRef, expanded, onExpandedChange, toggleTestId, bodyClassName = '', empty = false, className = '', children, ...attributes }: ViewSectionProps) {
  const t = useT(); const id = useId(); const bodyId = `${id}-body`;
  const [collapsed, setCollapsed] = useState(() => remembered(sectionKey));
  const hasBody = !empty && hasContent(children);
  const open = expanded ?? !collapsed;
  function setOpen(next: boolean) { setCollapsed(!next); remember(sectionKey, !next); onExpandedChange?.(next); }
  function toggle() { setOpen(!open); }
  // A narrow view wraps the header's title, status and actions onto several lines: body controls that keyboard focus or scrollIntoView
  // brings back keep clear of the header's real height, not the one-line default.
  const header = useRef<HTMLElement>(null);
  useLayoutEffect(() => {
    const element = header.current; const section = element?.parentElement;
    if (!element || !section || typeof ResizeObserver === 'undefined') return;
    const apply = () => section.style.setProperty('--pomp-section-sticky-height', `${Math.ceil(element.getBoundingClientRect().height)}px`);
    apply(); const observer = new ResizeObserver(apply); observer.observe(element);
    return () => { observer.disconnect(); section.style.removeProperty('--pomp-section-sticky-height'); };
  }, []);
  return <section {...attributes} ref={panelRef} className={`${className} view-section${active ? ' view-section-active' : ''}${hasBody ? '' : ' view-section-empty'}`} data-section-key={sectionKey}>
    <header ref={header} className="work-page-header view-section-header">
      <Heading id={headingId} className="view-section-heading"><button type="button" className="view-section-toggle" data-testid={toggleTestId}
        aria-expanded={hasBody ? open : undefined} aria-controls={hasBody ? bodyId : undefined} disabled={!hasBody} title={t(open ? 'section.collapse' : 'section.expand')} onClick={toggle}>
        {hasBody && <Icon name={open ? 'chevronDown' : 'chevron'} size={12} />}<span>{title}</span>
      </button></Heading>
      {status != null && <span className="work-page-status view-section-status">{status}</span>}
      {/* A header action works on the body (a form, a picker, a refreshed list): pressing one opens a collapsed section. The section
          opens after the action's own click (bubble phase): opening first starts the body's refresh, which can disable the action before
          its click arrives. */}
      {actions != null && <div className="view-section-actions" onClick={() => { if (hasBody && !open) setOpen(true); }}>{actions}</div>}
    </header>
    {hasBody && <div id={bodyId} hidden={!open} className={`work-page-body view-section-body ${bodyClassName}`}>{children}</div>}
  </section>;
}

export function SectionRefresh({ onClick, disabled = false, testId, label }: {
  onClick: () => void; disabled?: boolean; testId?: string; label?: string;
}) {
  const t = useT(); const name = label ?? t('tools.refresh');
  return <button type="button" className="icon-btn section-refresh" disabled={disabled} data-testid={testId} title={name} aria-label={name} onClick={onClick}>
    <Icon name="restart" size={13} /><span className="sr-only">{name}</span>
  </button>;
}
