import { memo, useEffect, useRef, useState } from 'react';
import { newId } from '../../format';
import { useLocale, useT } from '../../i18n';
import { send, type ComputerReplyPayload, type ComputerStatePayload } from '../../protocol';
import { ConfirmPopover } from '../ConfirmPopover';
import { FAST_REPLY_TIMEOUT_MS } from '../ComposerMeters';
import { Note } from '../Note';
import { plainControls } from '../RevealControls';
import { ViewSection } from '../ViewSection';
import { Badge, Switch } from '../ui';

export interface ComputerUseSectionProps {
  computer?: ComputerStatePayload | null;
  reply?: ComputerReplyPayload | null;
  /** The /computer command: each new revision acts like a click on the switch (off right away, or the confirmation before turning on). */
  request?: { revision: number };
  connected: boolean;
  /** The switch cannot be used now (another request is pending, a reply is running, or the tab is not shown). */
  blocked: boolean;
}

/** Tools › This conversation: whether the model may see and drive the screen in this conversation. One name everywhere: 화면 입력 제어. */
function ComputerUseSectionView({ computer, reply, request, connected, blocked }: ComputerUseSectionProps) {
  const t = useT(); const ko = useLocale() === 'ko';
  const name = ko ? '화면 입력 제어' : 'Screen & input control';
  const failedText = ko ? '화면 입력 제어를 바꾸지 못했어요' : 'Screen & input control was not changed';
  const [localPending, setLocalPending] = useState<string | null>(null);
  const pendingRef = useRef<string | null>(null);
  const [error, setError] = useState<string | null>(null);
  const [confirmOpen, setConfirmOpen] = useState(false);
  // The switch (header) and the confirmation (body) are both inside the card: a press anywhere else cancels.
  const sectionRef = useRef<HTMLElement>(null);
  const openerRef = useRef<HTMLElement | null>(null);
  const available = computer?.policy === 'perConversation' && computer.available;
  const desktop = computer?.desktop === true;
  const isPending = computer?.pending === true || localPending !== null;
  const disabled = !connected || !available || blocked || isPending;
  const close = () => {
    setConfirmOpen(false);
    const opener = openerRef.current;
    if (opener?.isConnected && !opener.closest('[inert], [hidden]')) opener.focus({ preventScroll: true });
    else sectionRef.current?.querySelector<HTMLElement>('[data-testid="computer-toggle"]')?.focus();
  };
  const change = (enabled: boolean) => {
    if (disabled || pendingRef.current) return;
    const requestId = newId();
    pendingRef.current = requestId;
    setLocalPending(requestId); setError(null);
    if (!send('computer/set', { requestId, desktop: enabled })) {
      pendingRef.current = null; setLocalPending(null); setError(t('error.hostUnavailable'));
    }
  };
  const toggle = () => {
    if (disabled) return;
    if (desktop) change(false);
    else {
      openerRef.current = document.activeElement instanceof HTMLElement ? document.activeElement : null;
      setError(null); setConfirmOpen(true);
    }
  };

  // Only the correlated reply acknowledges our request; an old idle state must not clear it.
  useEffect(() => {
    if (!reply || reply.requestId !== pendingRef.current) return;
    pendingRef.current = null; setLocalPending(null);
    setError(reply.ok ? null : reply.error || failedText);
  }, [reply, failedText]);
  useEffect(() => {
    if (!localPending) return;
    const timer = window.setTimeout(() => {
      pendingRef.current = null; setLocalPending(null); setError(t('setup.noReply'));
    }, FAST_REPLY_TIMEOUT_MS);
    return () => window.clearTimeout(timer);
  }, [localPending, t]);
  useEffect(() => {
    if (!connected) { pendingRef.current = null; setLocalPending(null); setError(null); }
    if (!connected || !available || blocked || isPending) setConfirmOpen(false);
  }, [connected, available, blocked, isPending]);
  const seenRequest = useRef<number | undefined>(undefined);
  useEffect(() => {
    if (!request || seenRequest.current === request.revision) return;
    seenRequest.current = request.revision;
    toggle();
  }, [request?.revision]);

  if (!computer || computer.policy === 'off') return null;
  const status = isPending ? <span className="spinner" aria-hidden="true" /> : undefined;
  return <ViewSection sectionKey="tools-computer" className="tools-section computer-section" title={name} status={status} data-testid="tools-computer" panelRef={sectionRef}
    expanded={confirmOpen || error ? true : undefined}
    actions={<>{!available && <Badge tone="warn" testId="computer-unavailable">{ko ? '사용할 수 없음' : 'Unavailable'}</Badge>}
      <Switch label={ko ? '사용' : 'Use'} checked={desktop} disabled={disabled} testId="computer-toggle" onChange={toggle} /></>}>
    {!available && computer.message && <p className="ui-note ui-muted computer-reason" role="status">{plainControls(computer.message)}</p>}
    {confirmOpen && <ConfirmPopover anchorRef={sectionRef} testId="computer-confirm-popover" label={ko ? `${name} 켜기` : `Turn on ${name.toLowerCase()}`}
      question={ko ? `이 대화에서 ${name}를 켤까요?` : `Turn on ${name.toLowerCase()} for this conversation?`}
      cancelLabel={t('dialog.cancel')} confirmLabel={ko ? '켜기' : 'Turn on'} cancelTestId="computer-confirm-cancel" confirmTestId="computer-confirm-enable"
      confirmDisabled={disabled} className="inline" onCancel={close} onConfirm={() => { close(); change(true); }} />}
    {error && <div className="computer-error-inline" role="alert" data-testid="computer-error">
      <Note tone="error" text={failedText} detail={error} onDismiss={() => setError(null)} />
    </div>}
  </ViewSection>;
}

export const ComputerUseSection = memo(ComputerUseSectionView);
