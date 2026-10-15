import { useEffect, useId, useRef, type ReactNode } from 'react';
import type { ToolsTranslate } from '../../i18n/toolsStrings';
import { measureBytes } from '../../resourceBytes';
import type { ResourceScope } from '../../toolsProtocol';
import { FormActions, Segmented } from '../ui';

// Where the cursor belongs when a dialog opens: text inputs and textareas, not checkboxes or buttons.
const TEXT_FIELD = 'textarea:not(:disabled), input:not(:disabled):is(:not([type]), [type="text"], [type="search"], [type="url"], [type="email"], [type="tel"], [type="number"], [type="password"])';

/**
 * Inline editing/confirmation, never a browser dialog. Esc and dismissal return focus to the opener.
 * First focus: an explicit `[data-autofocus]` element (a confirmation dialog marks its Cancel button), else the first text field,
 * else a select, else any other enabled control. When the opener is gone (a deleted row), focus goes to the section's
 * `[data-tools-dialog-fallback]` control; `fallback` names which one when the section has several (`data-tools-dialog-fallback="<name>"`).
 */
export function InPageDialog({ title, children, onClose, pending, testId, fallback: group }: {
  title: string; children: ReactNode; onClose: () => void; pending: boolean; testId: string; fallback?: string;
}) {
  const panel = useRef<HTMLDivElement>(null);
  useEffect(() => {
    const opener = document.activeElement instanceof HTMLElement ? document.activeElement : null;
    const fallback = (panel.current?.closest('.view-section') ?? panel.current?.closest('.tools-scroll-body'))?.querySelector<HTMLElement>(group ? `[data-tools-dialog-fallback="${group}"]` : '[data-tools-dialog-fallback]');
    const first = (selector: string) => panel.current?.querySelector<HTMLElement>(selector) ?? undefined;
    (first('[data-autofocus]:not(:disabled)') ?? first(TEXT_FIELD) ?? first('select:not(:disabled)') ?? first('input:not(:disabled), button:not(:disabled)'))?.focus();
    return () => { if (opener?.isConnected) opener.focus(); else if (fallback?.isConnected) fallback.focus(); };
  }, []);
  return <div ref={panel} className="tools-editor" role="dialog" aria-label={title} data-testid={testId} onKeyDown={event => {
    if (event.key === 'Escape' && !event.nativeEvent.isComposing) { event.preventDefault(); event.stopPropagation(); if (!pending) onClose(); }
  }}><h3>{title}</h3>{children}</div>;
}

/** The refusal for a file the host would write over its byte limit; undefined while it fits. */
export function bytesOver(t: ToolsTranslate, text: string, limit: number): string | undefined {
  return measureBytes(text, limit).over ? t('tools.bytesOver') : undefined;
}

/** A labelled group around a control that is not a single input (a segmented choice): the label sits above it like a Field's. */
export function ChoiceField({ label, children }: { label: ReactNode; children: ReactNode }) {
  const id = useId();
  return <div className="ui-field" role="group" aria-labelledby={id}><span id={id} className="tools-field-label">{label}</span>{children}</div>;
}

/** Where a new file goes: this project or every project of the user. */
export function ScopeChoice({ t, label, value, onChange, testId, disabled = false }: { t: ToolsTranslate; label: string; value: ResourceScope; onChange: (scope: ResourceScope) => void; testId: string; disabled?: boolean }) {
  return <Segmented<ResourceScope> label={label} testId={testId} value={value} onChange={onChange} disabled={disabled}
    options={[{ id: 'project', label: t('skills.source.project'), testId: `${testId}-project` }, { id: 'user', label: t('skills.source.user'), testId: `${testId}-user` }]} />;
}

/** The delete question of a list row: Cancel starts focused, the destructive button sits on the right. */
export function ConfirmDelete({ t, testId, yesTestId, fallback, question, locked, pending, onCancel, onConfirm }: {
  t: ToolsTranslate; testId: string; yesTestId: string; fallback?: string; question: string; locked: boolean; pending: boolean; onCancel: () => void; onConfirm: () => void;
}) {
  return <InPageDialog title={t('tools.confirmDelete')} testId={testId} fallback={fallback} onClose={onCancel} pending={pending}>
    <div className="ui-form tools-form"><p className="tools-question">{question}</p>
      <FormActions><button type="button" className="btn" data-autofocus disabled={pending} onClick={onCancel}>{t('tools.cancel')}</button>
        <button type="button" className="btn danger" data-testid={yesTestId} disabled={locked} onClick={onConfirm}>{t('tools.delete')}</button></FormActions></div>
  </InPageDialog>;
}
