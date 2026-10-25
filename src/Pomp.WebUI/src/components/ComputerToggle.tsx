import { memo, useEffect, useRef, useState, type RefObject } from 'react';
import { focusControlOrInput } from '../composerControls';
import { newId } from '../format';
import { useT } from '../i18n';
import { send, type ComputerReplyPayload, type ComputerStatePayload } from '../protocol';
import { reportStatus } from '../statusBar';
import { ConfirmPopover } from './ConfirmPopover';
import { Icon } from './Icon';
import { plainControls } from './RevealControls';

export interface ComputerToggleProps {
  computer?: ComputerStatePayload | null;
  reply?: ComputerReplyPayload | null;
  /** The /computer command: each new revision acts like a click (off at once, or the confirmation before turning on). */
  request?: { revision: number };
  /** Where a refusal or failure of the /computer command is told: the chat (a click on the button reports to the status bar). */
  onCommandNotice?: (message: string) => void;
  connected: boolean;
  /** The composer takes no requests now (a dialog, another view, a new conversation starting). */
  blocked: boolean;
  inputRef: RefObject<HTMLTextAreaElement | null>;
}

/**
 * The composer's screen & input control button (after Live): whether the model may see and drive the screen in this conversation.
 * One conversation on the PC holds it; while another one does, the button is disabled and names it. Hidden when the option is off.
 */
function ComputerToggleView({ computer, reply, request, onCommandNotice, connected, blocked, inputRef }: ComputerToggleProps) {
  const t = useT();
  const [localPending, setLocalPending] = useState<string | null>(null);
  const pendingRef = useRef<string | null>(null);
  const pendingDirection = useRef<'on' | 'off' | null>(null);
  const [confirmOpen, setConfirmOpen] = useState(false);
  // The button and its confirmation: a press anywhere else cancels. The popover floats above the composer.
  const anchorRef = useRef<HTMLSpanElement>(null);
  const buttonRef = useRef<HTMLButtonElement>(null);
  const available = computer?.policy === 'perConversation' && computer.available;
  const desktop = computer?.desktop === true;
  const owner = !desktop && computer?.ownerElsewhere ? computer.ownerElsewhere : null;
  const isPending = computer?.pending === true || localPending !== null;
  const label = t('computer.label');
  // Computer use alone is exclusive; a chat turn may keep running while the host waits to turn screen control on.
  const refusal = owner ? t('computer.ownedElsewhere', { label: plainControls(owner) })
    : !available ? (computer?.message ? plainControls(computer.message) : t('computer.unavailable'))
    : null;
  const cancellingPendingOn = !desktop && computer?.pending === true && pendingDirection.current !== 'off';
  const disabled = !connected || blocked || (!desktop && refusal !== null && !cancellingPendingOn) || (isPending && !cancellingPendingOn);
  const title = cancellingPendingOn ? t('computer.cancelPending') : !desktop && refusal ? refusal : label;
  const close = () => { setConfirmOpen(false); focusControlOrInput(buttonRef.current, inputRef.current, { preventScroll: true }); };
  // The switch the /computer command started: its refusal and its failed reply go to the chat, a click's to the status bar.
  const fromCommand = useRef(false);
  const report = (message: string, level: 'warning' | 'error' = 'error') => {
    if (fromCommand.current && onCommandNotice) onCommandNotice(message); else reportStatus(message, level);
  };
  const change = (enabled: boolean) => {
    if (disabled || (pendingRef.current && !cancellingPendingOn)) return;
    const requestId = newId();
    pendingRef.current = requestId; pendingDirection.current = enabled ? 'on' : 'off'; setLocalPending(requestId);
    if (!send('computer/set', { requestId, desktop: enabled })) {
      pendingRef.current = null; pendingDirection.current = null; setLocalPending(null); report(t('error.hostUnavailable'));
    }
  };
  const toggle = (command = false) => {
    fromCommand.current = command;
    if (disabled) {
      // A click on a disabled button says only why computer use itself is unavailable; the command always answers.
      const reason = refusal ?? (command ? t('slash.busy') : null);
      if (reason) report(reason, 'warning');
      return;
    }
    if (desktop || cancellingPendingOn) change(false);
    else setConfirmOpen(true);
  };

  // Only the correlated reply acknowledges our request; an old idle state must not clear it.
  useEffect(() => {
    if (!reply || reply.requestId !== pendingRef.current) return;
    pendingRef.current = null; pendingDirection.current = null; setLocalPending(null);
    if (!reply.ok) report(reply.error || t('computer.failed'));
  }, [reply, t]);
  useEffect(() => {
    if (!connected) { pendingRef.current = null; pendingDirection.current = null; setLocalPending(null); }
    if (disabled) setConfirmOpen(false);
  }, [connected, disabled]);
  const seenRequest = useRef<number | undefined>(request?.revision);
  useEffect(() => {
    if (!request || seenRequest.current === request.revision) return;
    seenRequest.current = request.revision;
    toggle(true);
  }, [request?.revision]);

  if (!computer || computer.policy === 'off') return null;
  return <span className="computer-toggle-wrapper" ref={anchorRef} title={title}>
    <button ref={buttonRef} type="button" className="icon-btn computer-toggle" data-testid="computer-toggle" aria-pressed={desktop} aria-label={title} title={title}
      disabled={disabled} aria-busy={isPending || undefined} onMouseDown={event => event.preventDefault()} onClick={() => toggle()}>
      {isPending ? <span className="spinner" aria-hidden="true" /> : <Icon name="screen" size={14} />}
    </button>
    {confirmOpen && <ConfirmPopover anchorRef={anchorRef} testId="computer-confirm-popover" label={label} question={t('computer.confirmQuestion')}
      cancelLabel={t('dialog.cancel')} confirmLabel={t('computer.turnOn')} cancelTestId="computer-confirm-cancel" confirmTestId="computer-confirm-enable"
      confirmDisabled={disabled} onCancel={close} onConfirm={() => { close(); change(true); }} />}
  </span>;
}

export const ComputerToggle = memo(ComputerToggleView);
