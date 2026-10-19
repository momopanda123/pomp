import { memo, useEffect, useRef, useState, type RefObject } from 'react';
import { focusControlOrInput } from '../composerControls';
import { newId } from '../format';
import { useT } from '../i18n';
import { send, type ComputerReplyPayload, type ComputerStatePayload } from '../protocol';
import { reportStatus } from '../statusBar';
import { FAST_REPLY_TIMEOUT_MS } from './ComposerMeters';
import { ConfirmPopover } from './ConfirmPopover';
import { Icon } from './Icon';
import { plainControls } from './RevealControls';

export interface ComputerToggleProps {
  computer?: ComputerStatePayload | null;
  reply?: ComputerReplyPayload | null;
  /** The /computer command: each new revision acts like a click (off at once, or the confirmation before turning on). */
  request?: { revision: number };
  connected: boolean;
  /** A reply is running: it can be switched off, not on. */
  busy: boolean;
  /** The composer takes no requests now (a dialog, another view, a new conversation starting). */
  blocked: boolean;
  inputRef: RefObject<HTMLTextAreaElement | null>;
}

/**
 * The composer's screen & input control button (after Live): whether the model may see and drive the screen in this conversation.
 * One conversation on the PC holds it; while another one does, the button is disabled and names it. Hidden when the option is off.
 */
function ComputerToggleView({ computer, reply, request, connected, busy, blocked, inputRef }: ComputerToggleProps) {
  const t = useT();
  const [localPending, setLocalPending] = useState<string | null>(null);
  const pendingRef = useRef<string | null>(null);
  const [confirmOpen, setConfirmOpen] = useState(false);
  // The button and its confirmation: a press anywhere else cancels. The popover floats above the composer.
  const anchorRef = useRef<HTMLSpanElement>(null);
  const buttonRef = useRef<HTMLButtonElement>(null);
  const available = computer?.policy === 'perConversation' && computer.available;
  const desktop = computer?.desktop === true;
  const owner = !desktop && computer?.ownerElsewhere ? computer.ownerElsewhere : null;
  const isPending = computer?.pending === true || localPending !== null;
  const label = t('computer.label');
  // Why it cannot be switched on now (switching off stays possible during a reply).
  const refusal = owner ? t('computer.ownedElsewhere', { label: plainControls(owner) })
    : !available ? (computer?.message ? plainControls(computer.message) : t('computer.unavailable'))
    : null;
  const disabled = !connected || blocked || isPending || (!desktop && (refusal !== null || busy));
  const title = !desktop && refusal ? refusal : label;
  const close = () => { setConfirmOpen(false); focusControlOrInput(buttonRef.current, inputRef.current, { preventScroll: true }); };
  const change = (enabled: boolean) => {
    if (disabled || pendingRef.current) return;
    const requestId = newId();
    pendingRef.current = requestId; setLocalPending(requestId);
    if (!send('computer/set', { requestId, desktop: enabled })) {
      pendingRef.current = null; setLocalPending(null); reportStatus(t('error.hostUnavailable'));
    }
  };
  const toggle = () => {
    if (disabled) { if (refusal) reportStatus(refusal, 'warning'); return; }
    if (desktop) change(false);
    else setConfirmOpen(true);
  };

  // Only the correlated reply acknowledges our request; an old idle state must not clear it.
  useEffect(() => {
    if (!reply || reply.requestId !== pendingRef.current) return;
    pendingRef.current = null; setLocalPending(null);
    if (!reply.ok) reportStatus(reply.error || t('computer.failed'));
  }, [reply, t]);
  useEffect(() => {
    if (!localPending) return;
    const timer = window.setTimeout(() => { pendingRef.current = null; setLocalPending(null); reportStatus(t('setup.noReply')); }, FAST_REPLY_TIMEOUT_MS);
    return () => window.clearTimeout(timer);
  }, [localPending, t]);
  useEffect(() => {
    if (!connected) { pendingRef.current = null; setLocalPending(null); }
    if (disabled) setConfirmOpen(false);
  }, [connected, disabled]);
  const seenRequest = useRef<number | undefined>(request?.revision);
  useEffect(() => {
    if (!request || seenRequest.current === request.revision) return;
    seenRequest.current = request.revision;
    toggle();
  }, [request?.revision]);

  if (!computer || computer.policy === 'off') return null;
  return <span className="computer-toggle-wrapper" ref={anchorRef} title={title}>
    <button ref={buttonRef} type="button" className="icon-btn computer-toggle" data-testid="computer-toggle" aria-pressed={desktop} aria-label={title} title={title}
      disabled={disabled} aria-busy={isPending || undefined} onMouseDown={event => event.preventDefault()} onClick={toggle}><Icon name="screen" size={14} /></button>
    {confirmOpen && <ConfirmPopover anchorRef={anchorRef} testId="computer-confirm-popover" label={label} question={t('computer.confirmQuestion')}
      cancelLabel={t('dialog.cancel')} confirmLabel={t('computer.turnOn')} cancelTestId="computer-confirm-cancel" confirmTestId="computer-confirm-enable"
      confirmDisabled={disabled} onCancel={close} onConfirm={() => { close(); change(true); }} />}
  </span>;
}

export const ComputerToggle = memo(ComputerToggleView);
