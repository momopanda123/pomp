import { useEffect, useId, useRef, useState, type FormEvent } from 'react';
import { useT } from '../i18n';
import { normalizeShareCode, parseShareAddress, shareCodeValid } from '../setup';
import { SHARE_REPLY_TIMEOUT_MS, type SetupCommands } from '../setupCommands';
import { Note } from './Note';
import { ViewSection } from './ViewSection';
import { Field, FormActions } from './ui';

interface SharedModelConnectSectionProps {
  cmd: SetupCommands;
  inert: boolean;
}

/**
 * "Use another PC's models" (the PC without internet or sign-in): the sharing PC's address and its one-time connection code. The host
 * registers with that PC and saves it as a local model server (shown in the key list with an "Other PC" badge). The code is a secret:
 * it exists only in this component's input state and in the one `connectSharedModels` command, and is cleared as soon as that is posted.
 */
export function SharedModelConnectSection({ cmd, inert }: SharedModelConnectSectionProps) {
  const t = useT();
  const titleId = useId();
  const codeRef = useRef<HTMLInputElement>(null);
  const [address, setAddress] = useState('');
  const [code, setCode] = useState('');

  const answer = cmd.feedback('connectSharedModels');
  const parsed = parseShareAddress(address);
  const addressBad = address.trim() !== '' && parsed === null;
  const normalized = normalizeShareCode(code);
  const codeBad = normalized !== '' && (normalized.length > 24 || /[^A-Z2-7]/.test(normalized));
  const canConnect = !inert && !answer.pending && parsed !== null && shareCodeValid(normalized);

  const retire = (): void => {
    if (!answer.pending && (answer.done || answer.error)) cmd.clear('connectSharedModels');
  };

  const connect = (event: FormEvent): void => {
    event.preventDefault();
    if (!canConnect || parsed === null) return;
    const host = parsed.host.toLowerCase();
    const posted = cmd.post('connectSharedModels', (requestId) => ({ requestId, action: 'connectSharedModels', address: `${host}:${parsed.port}`, code: normalized }), undefined, SHARE_REPLY_TIMEOUT_MS);
    // The code leaves with the command and is cleared at once; if posting failed the user keeps what they typed.
    if (posted) setCode('');
  };

  // A refused code is gone (single use is the host's rule, and the field was cleared): the focus goes back to it for the next one.
  const waited = useRef(false);
  useEffect(() => {
    if (answer.pending) { waited.current = true; return; }
    if (!waited.current) return;
    waited.current = false;
    if (answer.error) window.setTimeout(() => codeRef.current?.focus(), 0);
  }, [answer.pending, answer.error]);

  return (
    <ViewSection sectionKey="setup-connect" className="setup-section connect-section" title={t('setup.connect.title')} headingId={titleId} aria-labelledby={titleId}
      data-testid="setup-connect" bodyClassName="ui-flush">
      <form className="ui-form connect-form" autoComplete="off" noValidate onSubmit={connect} data-testid="connect-form">
        <Field label={t('setup.connect.address')} error={addressBad ? t('setup.connect.addressInvalid') : undefined}>{(id) => (
          <input id={id} type="text" className="ui-mono" data-testid="connect-address" placeholder="192.168.0.10:47611" maxLength={300} autoComplete="off"
            autoCapitalize="off" spellCheck={false} value={address} disabled={answer.pending} aria-invalid={addressBad || undefined}
            onChange={(event) => { retire(); setAddress(event.target.value); }} />
        )}</Field>
        <Field label={t('setup.connect.code')} error={codeBad ? t('setup.connect.codeInvalid') : undefined}>{(id) => (
          <input id={id} ref={codeRef} type="text" className="ui-mono" data-testid="connect-code" placeholder="ABCD-EFGH-IJKL-MNOP-QRST-UVWX" maxLength={64}
            autoComplete="off" autoCapitalize="characters" spellCheck={false} value={code} disabled={answer.pending} aria-invalid={codeBad || undefined}
            onChange={(event) => { retire(); setCode(event.target.value); }} />
        )}</Field>
        {answer.error && <Note tone="error" text={answer.error} onDismiss={() => cmd.clear('connectSharedModels')} />}
        <FormActions>
          <button type="submit" className="btn primary" data-testid="connect-submit" disabled={!canConnect}>
            {answer.pending ? <><span className="spinner" aria-hidden="true" />{t('setup.connect.connecting')}</> : t('setup.connect.connect')}
          </button>
        </FormActions>
      </form>
    </ViewSection>
  );
}
