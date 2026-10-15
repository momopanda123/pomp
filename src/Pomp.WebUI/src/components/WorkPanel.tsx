import type { KeyboardEventHandler, ReactNode, Ref } from 'react';
import { ViewSection } from './ViewSection';

export type WorkLayout = 'details' | 'page' | 'embedded';

/** The legacy layout deliberately adds no body wrapper or styling class. */
export function WorkPanel({ layout = 'details', className, title, status, summary, children, open, inert, legacyTag = 'details', legacyHeader, panelRef, onKeyDown, ariaLabel, sectionKey, actions, active, empty }: {
  layout?: WorkLayout; className: string; title: ReactNode; status?: ReactNode; summary?: ReactNode; children: ReactNode;
  open?: boolean; inert?: boolean; legacyTag?: 'details' | 'section' | 'div'; legacyHeader?: ReactNode;
  panelRef?: Ref<HTMLElement>; onKeyDown?: KeyboardEventHandler<HTMLElement>; ariaLabel?: string;
  sectionKey?: string; actions?: ReactNode; active?: boolean; empty?: boolean;
}) {
  if (layout === 'embedded') return <section ref={panelRef} className={className} inert={inert || undefined} onKeyDown={onKeyDown} aria-label={ariaLabel}>{status && <div className="work-subsection-status">{status}</div>}{children}</section>;
  if (layout === 'page') return <ViewSection sectionKey={sectionKey ?? className} panelRef={panelRef} className={`${className} work-page`} headingLevel="h2"
    title={title} status={status} actions={actions} active={active} empty={empty} inert={inert || undefined} onKeyDown={onKeyDown}
    aria-label={ariaLabel ?? (typeof title === 'string' ? title : undefined)}>{children}</ViewSection>;
  if (legacyTag === 'section') return <section ref={panelRef} className={className} inert={inert || undefined} onKeyDown={onKeyDown} aria-label={ariaLabel ?? (typeof title === 'string' ? title : undefined)}>{legacyHeader}{children}</section>;
  if (legacyTag === 'div') return <div className={className} inert={inert || undefined}>{children}</div>;
  return <details className={className} open={open} inert={inert || undefined}><summary>{summary ?? title}</summary>{children}</details>;
}
