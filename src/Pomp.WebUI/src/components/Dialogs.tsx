import { memo, useContext, useEffect, useId, useMemo, useRef, useState, type KeyboardEvent, type ReactNode, type RefObject } from 'react';
import { useCountdown } from '../hooks';
import { useT } from '../i18n';
import { roleTitle } from '../bundledDescriptions';
import { effectiveRoles, RolesContext } from '../teamsCache';
import { MAX_DIALOG_CHARS, MAX_NOTE_CHARS, type SendFailure, type SendResult } from '../limits';
import { parseTaskApproval } from '../permissionSummary';
import type { PageShortcuts, PermissionDecision, PermissionKind } from '../protocol';
import { dialogTitle, permissionTitle } from '../requestTitle';
import { matchesShortcut } from '../shortcuts';
import type { PendingDialog, PendingPermission, RequestItem } from '../state';
import { DiffView } from './DiffView';
import { revealControls } from './RevealControls';
import { Icon } from './Icon';
import { Pager, usePaged } from './ui';
import '../styles/requests.css';

/**
 * Buttons and keys stay inactive for a moment after a card appears, so a click or keypress that was already on its way
 * (double click on the previous answer, typing in the composer) can never answer a request the user has not seen.
 */
const ARM_MS = 500;

export function useArmed(): boolean {
  const [armed, setArmed] = useState(false);
  useEffect(() => {
    const id = window.setTimeout(() => setArmed(true), ARM_MS);
    return () => window.clearTimeout(id);
  }, []);
  return armed;
}

/** Elements that act on Enter themselves (a focused button clicks, a field types, a summary toggles): the card's Enter leaves them alone. */
const NATIVE_ENTER = 'button, a, summary, select, input, textarea, [role="option"]';
export const handlesEnter = (target: EventTarget): boolean => target instanceof Element && target.closest(NATIVE_ENTER) !== null;
export const composing = (event: KeyboardEvent): boolean => event.nativeEvent.isComposing || event.keyCode === 229;
/**
 * The card is (at least its top) visible inside its scroll area and the window. A card below the fold (the conversation was scrolled
 * up) must neither take the focus nor answer a stray Enter; the chords still answer from anywhere.
 */
export const cardOnScreen = (card: HTMLElement | null): boolean => {
  if (!card?.isConnected || card.closest('[hidden], [inert]')) return false;
  const r = card.getBoundingClientRect();
  const s = (card.closest('.transcript') ?? document.documentElement).getBoundingClientRect();
  const top = Math.max(r.top, s.top, 0);
  const bottom = Math.min(r.bottom, s.bottom, window.innerHeight);
  return bottom - top >= Math.min(24, r.height);
};
export const consume = (event: KeyboardEvent): void => { event.preventDefault(); event.stopPropagation(); };
/**
 * Where a key belongs to nothing else, so a pending card may take it: the page itself, the conversation outside any control, or the
 * empty composer (it cannot send while a card waits). A field with text, a button, a list or a menu keeps its own keys.
 */
const neutralFocus = (target: EventTarget | null): boolean => {
  if (!(target instanceof Element) || target === document.body || target === document.documentElement) return true;
  if (target instanceof HTMLTextAreaElement && target.dataset.testid === 'composer-input') return target.value.trim() === '';
  return !target.closest(NATIVE_ENTER) && !target.closest('[role="menu"], [role="listbox"], [role="dialog"], .picker-pop, .context-popover');
};
const keyList = (...keys: (string | undefined)[]): string | undefined => keys.filter(Boolean).join(' ') || undefined;

/**
 * Page-wide chords (host/hello.shortcuts.allow / deny) answer the pending card from anywhere on the page, the composer included.
 * Only while the card is on screen (the chat tab is shown): an answer is never given to a request the user cannot see.
 */
function useAnswerChords(cardRef: RefObject<HTMLElement | null>, shortcuts: PageShortcuts, onAllow: () => void, onDeny: () => void): void {
  const latest = useRef({ shortcuts, onAllow, onDeny });
  latest.current = { shortcuts, onAllow, onDeny };
  useEffect(() => {
    const onKeyDown = (event: globalThis.KeyboardEvent): void => {
      if (event.isComposing || event.keyCode === 229) return;
      const card = cardRef.current;
      if (!card?.isConnected || card.closest('[hidden], [inert]')) return;
      const { shortcuts: chords, onAllow: allow, onDeny: deny } = latest.current;
      const handler = matchesShortcut(event, chords.allow) ? allow : matchesShortcut(event, chords.deny) ? deny : undefined;
      if (!handler) return;
      event.preventDefault();
      event.stopPropagation();
      if (!event.repeat) handler();
    };
    window.addEventListener('keydown', onKeyDown, true);
    return () => window.removeEventListener('keydown', onKeyDown, true);
  }, [cardRef]);
}

interface ShellProps {
  title: string;
  counter?: string;
  cardRef: RefObject<HTMLDivElement | null>;
  /** Element to focus on arrival; defaults to the card itself (never a button, so a stray key cannot answer). */
  focusRef?: RefObject<HTMLElement | null>;
  /** Enter / Esc / arrows while the focus is inside the card; the owner guards composing and repeat. */
  onCardKeyDown: (event: KeyboardEvent<HTMLDivElement>) => void;
  /** Enter / Esc pressed where nothing else takes them (see neutralFocus) while the card is on screen; the card then has the focus. */
  onOutsideKey?: (key: 'Enter' | 'Escape') => void;
  children: ReactNode;
  footer: ReactNode;
}

/**
 * The pending request as a card at the end of the conversation (Transcript renders it in the scroll area). Nothing is covered and
 * nothing is locked: the composer and the tabs stay usable, so this is not a focus trap.
 */
export function RequestCard({ title, counter, cardRef, focusRef, onCardKeyDown, onOutsideKey, children, footer }: ShellProps) {
  const titleId = useId();

  useEffect(() => {
    // Do not pull the caret away from a message the user is in the middle of typing.
    const active = document.activeElement;
    if (active instanceof HTMLTextAreaElement && active.value && !active.closest('[inert], [hidden]')) return;
    // Out of view (the user scrolled up): no focus either, so Enter cannot answer what was not seen. The conversation usually scrolls
    // to a new card a moment after it appears: the card takes the focus then, unless the user has moved it elsewhere meanwhile.
    let taken = false;
    const take = (): void => {
      if (taken || !cardOnScreen(cardRef.current)) return;
      const now = document.activeElement;
      if (now !== active && !neutralFocus(now)) return;
      taken = true;
      (focusRef?.current ?? cardRef.current)?.focus({ preventScroll: true });
    };
    take();
    const card = cardRef.current;
    const observer = !taken && card && typeof IntersectionObserver === 'function' ? new IntersectionObserver(take, { threshold: [0, 0.1, 0.5, 1] }) : null;
    if (card) observer?.observe(card);
    return () => {
      observer?.disconnect();
      if (!taken) return;
      if (!(active instanceof HTMLElement)) return;
      // The next queued request has its own card (and focus) by then: give focus back only once no card is left.
      window.requestAnimationFrame(() => {
        if (document.querySelector('.request-card')) return;
        if (active.isConnected && !active.closest('[inert], [hidden]')) active.focus({ preventScroll: true });
      });
    };
  }, [focusRef, cardRef]);

  // Enter / Esc from the empty composer or the conversation answer the card too (the card's own handler covers the keys inside it).
  const outside = useRef(onOutsideKey);
  outside.current = onOutsideKey;
  useEffect(() => {
    const onKeyDown = (event: globalThis.KeyboardEvent): void => {
      if ((event.key !== 'Enter' && event.key !== 'Escape') || !outside.current) return;
      if (event.isComposing || event.keyCode === 229 || event.altKey || event.ctrlKey || event.metaKey || event.shiftKey) return;
      const card = cardRef.current;
      if (!card || (event.target instanceof Node && card.contains(event.target)) || !cardOnScreen(card)) return;
      if (!neutralFocus(event.target) || document.querySelector('.overlay .dialog')) return;
      event.preventDefault(); event.stopPropagation();
      (focusRef?.current ?? card).focus({ preventScroll: true });
      if (!event.repeat) outside.current(event.key);
    };
    window.addEventListener('keydown', onKeyDown);
    return () => window.removeEventListener('keydown', onKeyDown);
  }, [cardRef, focusRef]);

  return (
    <div className="request-card dialog" role="group" aria-labelledby={titleId} tabIndex={-1} ref={cardRef} data-testid="request-card"
      onKeyDown={onCardKeyDown}>
      <div className="dialog-head">
        <span id={titleId} className="dialog-title">
          {title}
        </span>
        {counter && <span className="dialog-counter">{counter}</span>}
      </div>
      <div className="dialog-body">{children}</div>
      <div className="dialog-foot">{footer}</div>
    </div>
  );
}

const OUTCOME_KEYS = {
  allow: 'perm.outcome.allow',
  deny: 'perm.outcome.deny',
  always: 'perm.outcome.always',
  session: 'perm.outcome.session',
  expired: 'perm.outcome.expired',
  handled: 'perm.outcome.handled',
  cancelled: 'dialog.outcome.cancelled',
  answered: 'dialog.outcome.answered',
} as const satisfies Record<RequestItem['outcome'], string>;

/** What is left of a card in the transcript once the request is answered, dismissed or expired. */
export const RequestRecord = memo(function RequestRecord({ item }: { item: RequestItem }) {
  const t = useT();
  const negative = item.outcome === 'deny' || item.outcome === 'expired' || item.outcome === 'cancelled';
  return (
    <div className="request-record" role="status" data-testid="request-record" data-outcome={item.outcome} data-source={item.source}>
      <Icon name={negative ? 'x' : item.outcome === 'handled' ? 'info' : 'check'} size={12} />
      <span className="request-record-title">{revealControls(item.title)}</span>
      <span className="request-record-outcome">{t(OUTCOME_KEYS[item.outcome])}</span>
    </div>
  );
});

/** Countdown bar; "deny" wording for permission requests, "cancel" wording for generic questions. */
function Countdown({ remaining, total, action }: { remaining: number; total: number; action: 'deny' | 'cancel' }) {
  const t = useT();
  const seconds = Math.ceil(remaining / 1000);
  return (
    <div className="timeout" role="timer">
      <div className="timeout-bar">
        <div className="timeout-fill" style={{ width: `${Math.min(100, (remaining / total) * 100)}%` }} />
      </div>
      <span className="timeout-text">
        {remaining > 0 ? t(action === 'deny' ? 'perm.timeout' : 'dialog.timeout', { seconds }) : t('perm.expired')}
      </span>
    </div>
  );
}

/** Kept inside the card: the failure belongs to the pending request, not to the transcript. */
function ResponseError({ failure }: { failure: SendFailure | null }) {
  const t = useT();
  if (!failure) return null;
  const message = failure.reason === 'tooManyChars'
    ? t('dialog.charLimit')
    : t(failure.reason === 'tooLarge' || failure.reason === 'tooLargeEncoded' ? 'dialog.tooLarge' : 'dialog.sendFailed');
  return (
    <div className="dialog-error" role="alert" data-testid="dialog-error">
      <Icon name="error" size={12} />
      <span>{message}</span>
    </div>
  );
}

// ---- Permission dialog -------------------------------------------------------------------------

const KINDS = [
  'fileWrite',
  'projectChange',
  'processExec',
  'debuggerControl',
  'breakpointChange',
  'bookmarkChange',
  'packageChange',
  'vcsWrite',
  'testRun',
  'other',
] as const satisfies readonly PermissionKind[];

type KnownKind = (typeof KINDS)[number];
const isKnownKind = (kind: string): kind is KnownKind => (KINDS as readonly string[]).includes(kind);

/** Badge tone by how much damage an approval could do. */
const KIND_TONE: Record<KnownKind, 'danger' | 'warn' | 'info'> = {
  processExec: 'danger',
  vcsWrite: 'danger',
  fileWrite: 'warn',
  projectChange: 'warn',
  packageChange: 'warn',
  debuggerControl: 'warn',
  breakpointChange: 'info',
  bookmarkChange: 'info',
  testRun: 'info',
  other: 'info',
};

interface PermissionDialogProps {
  request: PendingPermission;
  /** 1-based position in the queue and queue length. */
  position: number;
  total: number;
  /** Effective page-wide answer chords. */
  shortcuts: PageShortcuts;
  /** The request stays pending and retryable when nothing was handed to the host. */
  onRespond: (requestId: string, decision: PermissionDecision, note?: string) => SendResult;
  /** The countdown ran out and the host did not dismiss the request: drop it locally. */
  onExpired: (requestId: string) => void;
}

export function PermissionDialog({ request, position, total, shortcuts, onRespond, onExpired }: PermissionDialogProps) {
  const t = useT();
  const cardRef = useRef<HTMLDivElement>(null);
  const armed = useArmed();
  const [note, setNote] = useState('');
  const [failure, setFailure] = useState<SendFailure | null>(null);
  const remaining = useCountdown(request.receivedAt, request.timeoutMs, () => onExpired(request.requestId));
  const locked = !armed || remaining === 0;
  // A change can be allowed only after all of it was on screen: a long diff must be expanded first (deny always stays available).
  const [diffShown, setDiffShown] = useState(!request.diff);
  const allowLocked = locked || !diffShown;
  const answered = useRef(false); // exactly one answer per request, even if a click is delivered twice

  const roles = effectiveRoles(useContext(RolesContext));
  const tasks = request.toolName === 'task' && Array.isArray(request.tasks) ? request.tasks.filter(task => typeof task.agent === 'string' && typeof task.task === 'string') : undefined;
  // A team run may hand out any number of tasks: the full request lists them a page at a time.
  const pagedTasks = usePaged(tasks ?? [], request.requestId, 10);
  const { kind } = request;
  const isOmpApproval = request.source === 'ompApproval';
  const taskSummary = useMemo(
    () => isOmpApproval && request.toolName === 'task' ? parseTaskApproval(request.message) ?? (request.tasks ? { reasonLines: [] } : null) : null,
    [isOmpApproval, request.toolName, request.message, request.tasks],
  );
  const kindLabel = taskSummary ? t('perm.kind.agentRun') : isKnownKind(kind) ? t(`perm.kind.${kind}`) : kind;
  const tone = taskSummary ? KIND_TONE.fileWrite : isKnownKind(kind) ? KIND_TONE[kind] : 'info';
  const title = permissionTitle(request, t);
  // omp repeats its title as the first message line; keep all following details, including forced safety reasons.
  const firstLineEnd = request.message?.indexOf('\n') ?? -1;
  const firstLine = firstLineEnd === -1 ? request.message : request.message?.slice(0, firstLineEnd).replace(/\r$/, '');
  const message = isOmpApproval && firstLine === request.title
    ? firstLineEnd === -1 ? '' : request.message?.slice(firstLineEnd + 1)
    : request.message;
  const source = request.source === 'hostTool' ? t('perm.source.hostTool') : t('perm.source.ompApproval');
  // omp's forced safety prompts are never remembered: only "allow once" and "deny" are offered (absent flag = can remember).
  const canRemember = request.canRemember !== false;
  // omp's approval answer has no place for a reason (only Approve/Deny), so the note is offered for host tools only.
  const canNote = request.source !== 'ompApproval';

  const answer = (decision: PermissionDecision): void => {
    if (locked || answered.current) return;
    if (!canRemember && (decision === 'alwaysAllow' || decision === 'allowForSession')) return;
    if (decision !== 'deny' && !diffShown) return;
    if (canNote && decision === 'deny' && note.length > MAX_NOTE_CHARS) {
      setFailure({ reason: 'tooManyChars', chars: note.length, max: MAX_NOTE_CHARS });
      return; // keep the exact note and the unanswered request; allowing does not send this note
    }
    answered.current = true;
    const result = onRespond(request.requestId, decision, canNote && decision === 'deny' && note ? note : undefined);
    if (!result.ok) {
      answered.current = false;
      setFailure(result); // keep the note and allow another explicit attempt; no answer reached the host
    }
  };

  useAnswerChords(cardRef, shortcuts, () => answer('allow'), () => answer('deny'));

  // Enter allows once (same locks as the button), Esc denies. A focused button/summary keeps its own Enter.
  const onCardKeyDown = (event: KeyboardEvent<HTMLDivElement>): void => {
    if (composing(event)) return;
    if (event.key === 'Escape') {
      consume(event);
      if (!event.repeat) answer('deny');
    } else if (event.key === 'Enter' && !handlesEnter(event.target)) {
      if (!cardOnScreen(cardRef.current)) return;
      consume(event);
      if (!event.repeat) answer('allow');
    }
  };

  return (
    <RequestCard
      title={t('perm.title')}
      counter={total > 1 ? t('perm.counter', { index: position, total }) : undefined}
      cardRef={cardRef}
      onCardKeyDown={onCardKeyDown}
      onOutsideKey={key => answer(key === 'Escape' ? 'deny' : 'allow')}
      footer={
        <>
          {remaining !== null && request.timeoutMs !== undefined && (
            <Countdown remaining={remaining} total={request.timeoutMs} action="deny" />
          )}
          {canNote && (
            <label className="note">
              <span>{t('perm.note')}</span>
              <input
                type="text"
                spellCheck={false}
                className="field"
                value={note}
                aria-invalid={note.length > MAX_NOTE_CHARS}
                onChange={(event) => { setNote(event.target.value); setFailure(null); }}
                // Enter has the same default as the card (allow once), never an implicit deny.
                onKeyDown={(event) => {
                  if (event.key !== 'Enter' || composing(event)) return;
                  consume(event);
                  if (!event.repeat) answer('allow');
                }}
              />
              {note.length > MAX_NOTE_CHARS && <div className="composer-warning" role="alert" data-testid="note-limit">{t('perm.noteLimit')}</div>}
            </label>
          )}
          <ResponseError failure={failure} />
          <div className="dialog-actions">
            <button type="button" className="btn primary" data-testid="perm-allow" disabled={allowLocked} aria-keyshortcuts={keyList(shortcuts.allow, 'Enter')} onClick={() => answer('allow')}>
              {t('perm.allow')}
            </button>
            <button type="button" className="btn" data-testid="perm-deny" disabled={locked} aria-keyshortcuts={keyList(shortcuts.deny, 'Escape')} onClick={() => answer('deny')}>
              {t('perm.deny')}
            </button>
            {canRemember && (
              <button type="button" className="btn" data-testid="perm-always" disabled={allowLocked} onClick={() => answer('alwaysAllow')}>
                {t('perm.always')}
              </button>
            )}
            {canRemember && (
              <button type="button" className="btn subtle" data-testid="perm-session" disabled={allowLocked} onClick={() => answer('allowForSession')}>
                {t('perm.session')}
              </button>
            )}
          </div>
        </>
      }
    >
      <div className="perm-title">{isOmpApproval ? revealControls(title) : title}</div>
      <div className="perm-meta">
        <span className={`badge kind ${tone}`}>{kindLabel}</span>
        <span className="badge source">{source}</span>
        <code className="perm-tool" title={t('perm.tool')}>
          {request.toolName === 'composer_workflow' ? t('tool.workflow') : request.toolName}
        </code>
      </div>
      {request.target && (
        <div className="perm-target">
          <span className="perm-key">{t('perm.target')}</span>
          <code>{revealControls(request.target)}</code>
        </div>
      )}
      {taskSummary ? (
        <>
          <dl className="perm-summary" data-testid="perm-task-summary">
            {(tasks?.length || taskSummary.agents) && (
              <div><dt>{t('perm.task.agents')}</dt><dd>{revealControls(tasks ? [...new Set(tasks.map(task => roleTitle(roles.find(role => role.name === task.agent), t, task.agent)))].join(', ') : taskSummary.agents ?? '')}</dd></div>
            )}
            {taskSummary.name && (
              <div><dt>{t('perm.task.name')}</dt><dd>{revealControls(taskSummary.name)}</dd></div>
            )}
            {taskSummary.taskPreview && (
              <div><dt>{t('perm.task.task')}</dt><dd>{revealControls(taskSummary.taskPreview)}</dd></div>
            )}
          </dl>
          {tasks ? <div className="perm-more" data-testid="perm-task-count">{t('perm.task.count', { count: tasks.length })}</div> : taskSummary.moreCount ? <div className="perm-more">{t('perm.task.more', { count: taskSummary.moreCount })}</div> : null}
          {taskSummary.ambiguous && <div className="perm-task-hint" role="alert" data-testid="perm-task-ambiguous">{t('perm.task.ambiguous')}</div>}
          {taskSummary.reasonLines.length > 0 && (
            <pre className="perm-message perm-reasons" data-testid="perm-task-reasons">{revealControls(taskSummary.reasonLines.join('\n'))}</pre>
          )}
          <details className="perm-details" data-testid="perm-full-request" open={taskSummary.ambiguous || undefined}>
            <summary>{t('perm.showFullRequest')}</summary>
            {tasks ? pagedTasks.slice.map((task, index) => <div className="perm-full-task" data-testid="perm-full-task" key={pagedTasks.from + index}>
              <dl className="perm-summary"><div><dt>{t('perm.task.agents')}</dt><dd>{revealControls(roleTitle(roles.find(role => role.name === task.agent), t, task.agent))}</dd></div></dl>
              <div className="perm-key">{t('perm.task.fullTask')}</div><pre className="perm-message">{revealControls(task.task)}</pre>
            </div>) : <pre className="perm-message">{revealControls(request.message ?? '')}</pre>}
            {tasks && <Pager paged={pagedTasks} testId="perm-task-pager" />}
          </details>
        </>
      ) : message && <pre className="perm-message">{revealControls(message)}</pre>}
      {request.diff && <DiffView diff={request.diff} onFullyShown={setDiffShown} />}
    </RequestCard>
  );
}

// ---- Generic dialog (dialog/request) -----------------------------------------------------------

export interface DialogResponse {
  value?: string;
  confirmed?: boolean;
  cancelled?: boolean;
}

interface GenericDialogProps {
  request: PendingDialog;
  position: number;
  total: number;
  shortcuts: PageShortcuts;
  onRespond: (requestId: string, response: DialogResponse) => SendResult;
  onDraftChange: (requestId: string, text: string) => void;
  onExpired: (requestId: string) => void;
}

export function GenericDialog({ request, position, total, shortcuts, onRespond, onDraftChange, onExpired }: GenericDialogProps) {
  const t = useT();
  const cardRef = useRef<HTMLDivElement>(null);
  const armed = useArmed();
  const remaining = useCountdown(request.receivedAt, request.timeoutMs, () => onExpired(request.requestId));
  const locked = !armed || remaining === 0;
  const answered = useRef(false);
  const text = request.draft ?? request.defaultValue ?? '';
  const [failure, setFailure] = useState<SendFailure | null>(null);
  const fieldRef = useRef<HTMLInputElement & HTMLTextAreaElement>(null);
  const { method } = request;
  const hasField = method === 'input' || method === 'editor';

  const respond = (response: DialogResponse): void => {
    if (locked || answered.current) return;
    answered.current = true;
    const result = onRespond(request.requestId, response);
    if (!result.ok) {
      answered.current = false;
      setFailure(result); // keep the field/selection and the same request; cancellation can still be retried too
    }
  };
  // Cancelling always reports `cancelled`; a yes/no question additionally answers "no".
  const cancel = (): void => respond(method === 'confirm' ? { confirmed: false, cancelled: true } : { cancelled: true });
  const confirm = (): void => respond(method === 'confirm' ? { confirmed: true } : { value: text });
  const changeText = (value: string): void => {
    onDraftChange(request.requestId, value);
    setFailure(null);
  };

  const onFieldKeyDown = (event: KeyboardEvent<HTMLElement>): void => {
    if (event.key !== 'Enter' || composing(event)) return;
    // input: Enter confirms; editor (multi-line): Ctrl+Enter confirms, plain Enter stays a line break.
    if (method === 'input' || event.ctrlKey || event.metaKey) {
      consume(event);
      if (!event.repeat) confirm();
    }
  };

  // "select": arrows move the highlight, Home/End jump, Enter answers with the highlighted option (the click path keeps the arm and exactly-once guards).
  const options = method === 'select' ? request.options ?? [] : [];
  const defaultIndex = Math.max(0, options.indexOf(request.defaultValue ?? ''));
  const [selected, setSelected] = useState(defaultIndex);
  const optionsRef = useRef<HTMLDivElement>(null);
  const selectedRef = useRef(selected);
  selectedRef.current = selected;
  useEffect(() => {
    if (method === 'select') setSelected(Math.max(0, (request.options ?? []).indexOf(request.defaultValue ?? '')));
  }, [method, request.options, request.defaultValue]);

  const pickHighlighted = (): void => {
    optionsRef.current?.querySelectorAll<HTMLButtonElement>('.option')[selectedRef.current]?.click();
  };
  // The primary answer: yes, the typed text, or the highlighted option.
  const primary = (): void => (method === 'select' ? pickHighlighted() : confirm());
  useAnswerChords(cardRef, shortcuts, primary, cancel);

  const onCardKeyDown = (event: KeyboardEvent<HTMLDivElement>): void => {
    if (composing(event)) return;
    if (event.key === 'Escape') {
      consume(event);
      if (!event.repeat) cancel();
      return;
    }
    if (method !== 'select' || options.length === 0) {
      // Enter on a confirm card (the fields confirm in their own handler); buttons keep their native Enter.
      if (event.key === 'Enter' && method === 'confirm' && !handlesEnter(event.target)) {
        if (!cardOnScreen(cardRef.current)) return;
        consume(event);
        if (!event.repeat) confirm();
      }
      return;
    }
    const count = options.length;
    const move = (next: number): void => {
      consume(event);
      const clamped = Math.min(count - 1, Math.max(0, next));
      setSelected(clamped);
      optionsRef.current?.querySelectorAll<HTMLButtonElement>('.option')[clamped]?.focus({ preventScroll: true });
    };
    switch (event.key) {
      case 'ArrowDown': move(selectedRef.current + 1); return;
      case 'ArrowUp': move(selectedRef.current - 1); return;
      case 'Home': move(0); return;
      case 'End': move(count - 1); return;
      case 'Enter':
        // A footer button keeps its own Enter; everything else answers with the highlighted option.
        if (event.target instanceof Element && event.target.closest('.dialog-foot button')) return;
        if (!cardOnScreen(cardRef.current)) return;
        consume(event);
        if (!event.repeat) pickHighlighted();
        return;
    }
  };

  return (
    <RequestCard
      title={dialogTitle(request, t)}
      counter={total > 1 ? t('perm.counter', { index: position, total }) : undefined}
      cardRef={cardRef}
      focusRef={hasField ? fieldRef : undefined}
      onCardKeyDown={onCardKeyDown}
      onOutsideKey={key => { if (key === 'Escape') cancel(); else if (!hasField) primary(); }}
      footer={
        <>
          {remaining !== null && request.timeoutMs !== undefined && (
            <Countdown remaining={remaining} total={request.timeoutMs} action="cancel" />
          )}
          {hasField && text.length > MAX_DIALOG_CHARS && <div className="composer-warning" role="alert" data-testid="dialog-limit">{t('dialog.charLimit')}</div>}
          <ResponseError failure={failure} />
          <div className="dialog-actions">
            {method !== 'select' && (
              <button type="button" className="btn primary" data-testid="dialog-ok" disabled={locked} aria-keyshortcuts={keyList(shortcuts.allow, method === 'editor' ? 'Control+Enter' : 'Enter')} onClick={confirm}>
                {t('dialog.ok')}
              </button>
            )}
            <button type="button" className="btn" data-testid="dialog-cancel" disabled={locked} aria-keyshortcuts={keyList(shortcuts.deny, 'Escape')} onClick={cancel}>
              {t('dialog.cancel')}
            </button>
          </div>
        </>
      }
    >
      {request.message && <div className="dialog-message">{request.message}</div>}

      {method === 'select' && (
        <div className="options" role="listbox" ref={optionsRef} tabIndex={-1}
          aria-activedescendant={options.length > 0 ? `dialog-option-${selected}` : undefined}>
          {options.map((option, index) => (
            <button
              key={option}
              id={`dialog-option-${index}`}
              type="button"
              role="option"
              aria-selected={index === selected}
              className={index === selected ? 'option current' : 'option'}
              disabled={locked}
              onClick={() => respond({ value: option })}
              onFocus={() => setSelected(index)}
            >
              <span className="option-mark">{index === selected && <Icon name="check" size={12} />}</span>
              {option}
            </button>
          ))}
        </div>
      )}

      {method === 'input' && (
        <input
          ref={fieldRef}
          type="text"
          className="field"
          value={text}
          aria-invalid={text.length > MAX_DIALOG_CHARS}
          onChange={(event) => changeText(event.target.value)}
          onKeyDown={onFieldKeyDown}
        />
      )}

      {method === 'editor' && (
        <textarea
          ref={fieldRef}
          className="field mono"
          rows={8}
          value={text}
          aria-invalid={text.length > MAX_DIALOG_CHARS}
          onChange={(event) => changeText(event.target.value)}
          onKeyDown={onFieldKeyDown}
        />
      )}
    </RequestCard>
  );
}
