import { useEffect, useId, useRef, useState, type FormEvent } from 'react';
import { copyText } from '../format';
import { useNow } from '../hooks';
import { useT } from '../i18n';
import type { SetupModelShare } from '../protocol';
import { SHARE_REPLY_TIMEOUT_MS, type SetupCommands } from '../setupCommands';
import { Icon } from './Icon';
import { Note } from './Note';
import { plainControls, revealControls } from './RevealControls';
import { ViewSection } from './ViewSection';
import { Actions, Badge, Cell, DataTable, Field, FormActions, IconButton, Row, Switch, refocus } from './ui';

interface ModelShareSectionProps {
  share: SetupModelShare;
  cmd: SetupCommands;
  inert: boolean;
}

/** Time left of the connection code as m:ss. */
function remainingText(ms: number): string {
  const seconds = Math.max(0, Math.ceil(ms / 1000));
  return `${Math.floor(seconds / 60)}:${String(seconds % 60).padStart(2, '0')}`;
}

/**
 * "Share my models with another PC" (the PC with internet and accounts): the switch, the address and port it is offered on, the
 * address and one-time connection code to enter on the other PC (with the time left, copy and "New code"), and the connected PCs,
 * each of which can be disconnected after a confirmation. The code comes from the host's state; this page never makes or keeps one.
 */
export function ModelShareSection({ share, cmd, inert }: ModelShareSectionProps) {
  const t = useT();
  const titleId = useId();
  const [draftAddress, setDraftAddress] = useState<string | undefined>(undefined);
  const [draftPort, setDraftPort] = useState<string | undefined>(undefined);
  const [copied, setCopied] = useState<'address' | 'code' | undefined>(undefined);
  const [asking, setAsking] = useState<string | undefined>(undefined);
  const cancelRef = useRef<HTMLButtonElement>(null);

  const enable = cmd.feedback('shareEnable');
  const configure = cmd.feedback('shareConfigure');
  const newCode = cmd.feedback('shareNewCode');
  const revoke = cmd.feedback('shareRevoke');
  const on = share.state === 'on';
  const switchedOn = on || share.state === 'starting';

  const address = draftAddress ?? share.address ?? share.addresses[0]?.address ?? '';
  const portText = draftPort ?? String(share.port);
  const port = /^\d{1,5}$/.test(portText.trim()) ? Number(portText.trim()) : Number.NaN;
  const portBad = !(port >= 1024 && port <= 65535);
  const changed = address !== (share.address ?? '') || port !== share.port;
  const canConfigure = !inert && !configure.pending && share.state !== 'starting' && address !== '' && !portBad && changed;
  const options = share.address && !share.addresses.some((item) => item.address === share.address)
    ? [{ address: share.address, name: '' }, ...share.addresses] : share.addresses;

  const code = on ? share.code : null;
  const now = useNow(code !== null, 1000);
  const expires = code ? Date.parse(code.expiresUtc) : Number.NaN;
  const remaining = Number.isNaN(expires) ? undefined : expires - now;
  const expired = remaining !== undefined && remaining <= 0;
  const endpoint = share.address ? `${share.address}:${share.port}` : '';

  // A saved address is the host's again: the inputs follow its state from now on.
  useEffect(() => {
    if (configure.done) { setDraftAddress(undefined); setDraftPort(undefined); }
  }, [configure.done]);

  useEffect(() => {
    if (!copied) return;
    const timer = window.setTimeout(() => setCopied(undefined), 2000);
    return () => window.clearTimeout(timer);
  }, [copied]);

  useEffect(() => {
    if (asking) cancelRef.current?.focus();
  }, [asking]);

  // A PC that the host no longer lists has nothing left to disconnect.
  useEffect(() => {
    if (asking && !share.clients.some((client) => client.name === asking)) setAsking(undefined);
  }, [asking, share.clients]);

  const toggle = (enabled: boolean): void => {
    cmd.clear('shareNewCode');
    cmd.post('shareEnable', (requestId) => ({ requestId, action: 'shareEnable', enabled }), undefined, SHARE_REPLY_TIMEOUT_MS);
  };
  const saveAddress = (event: FormEvent): void => {
    event.preventDefault();
    if (!canConfigure) return;
    cmd.post('shareConfigure', (requestId) => ({ requestId, action: 'shareConfigure', address, port }), undefined, SHARE_REPLY_TIMEOUT_MS);
  };
  const makeCode = (): void => {
    cmd.post('shareNewCode', (requestId) => ({ requestId, action: 'shareNewCode' }));
  };
  const copy = (what: 'address' | 'code', value: string): void => {
    void copyText(value).then((ok) => setCopied(ok ? what : undefined));
  };
  const closeConfirm = (name: string): void => {
    setAsking(undefined);
    refocus(`share-revoke-${name}`);
  };
  const confirmRevoke = (name: string): void => {
    setAsking(undefined);
    cmd.post('shareRevoke', (requestId) => ({ requestId, action: 'shareRevoke', name }), name);
  };

  const status = share.state === 'on'
    ? <Badge tone="ok" icon="check" testId="share-status">{t('setup.share.on')}</Badge>
    : share.state === 'starting'
      ? <Badge tone="info" testId="share-status">{t('setup.share.starting')}</Badge>
      : share.state === 'failed'
        ? <Badge tone="error" icon="error" testId="share-status">{t('setup.share.failed')}</Badge>
        : <Badge testId="share-status">{t('setup.share.off')}</Badge>;

  return (
    <ViewSection sectionKey="setup-share" className="setup-section share-section" title={t('setup.share.title')} headingId={titleId} aria-labelledby={titleId}
      data-testid="setup-share" status={status} bodyClassName="ui-flush"
      actions={<Switch checked={switchedOn} label={t('setup.share.switch')} showLabel={false} testId="share-switch"
        disabled={inert || enable.pending || share.state === 'starting'} onChange={toggle} />}>

      {(share.state === 'starting' || (share.state === 'failed' && share.error) || enable.error) && (
        <div className="ui-form share-block">
          {share.state === 'starting' && (
            <span className="setup-running-text share-starting" role="status"><span className="spinner" aria-hidden="true" />{t('setup.share.starting')}</span>
          )}
          {share.state === 'failed' && share.error && <Note tone="error" text={share.error} />}
          {enable.error && <Note tone="error" text={enable.error} onDismiss={() => cmd.clear('shareEnable')} />}
        </div>
      )}

      {on && (
        <div className="ui-form share-block" data-testid="share-info">
          <strong className="share-info-title">{t('setup.share.info')}</strong>
          {endpoint && (
            <div className="share-info-row">
              <span className="share-info-label">{t('setup.share.infoAddress')}</span>
              <span className="share-info-value ui-mono" data-testid="share-endpoint">{revealControls(endpoint)}</span>
              <button type="button" className="btn subtle small" data-testid="share-copy-address" onClick={() => copy('address', endpoint)}>
                <Icon name={copied === 'address' ? 'check' : 'copy'} size={12} />
                {copied === 'address' ? t('setup.share.copied') : t('setup.share.copyAddress')}
              </button>
            </div>
          )}
          {code && !expired && (
            <div className="share-info-row">
              <span className="share-info-label">{t('setup.share.infoCode')}</span>
              <span className="share-info-value share-code ui-mono" data-testid="share-code">{revealControls(code.code)}</span>
              <button type="button" className="btn subtle small" data-testid="share-copy-code" onClick={() => copy('code', code.code)}>
                <Icon name={copied === 'code' ? 'check' : 'copy'} size={12} />
                {copied === 'code' ? t('setup.share.copied') : t('setup.share.copyCode')}
              </button>
            </div>
          )}
          <div className="share-code-foot">
            {code && !expired && remaining !== undefined && (
              <span className="hint" data-testid="share-code-expiry">{t('setup.share.remaining', { time: remainingText(remaining) })}</span>
            )}
            <button type="button" className="btn small" data-testid="share-new-code" disabled={inert || newCode.pending} onClick={makeCode}>
              <Icon name="restart" size={12} />{t('setup.share.newCode')}
            </button>
          </div>
          {newCode.error && <Note tone="error" text={newCode.error} onDismiss={() => cmd.clear('shareNewCode')} />}
        </div>
      )}

      <form className="ui-form share-block" autoComplete="off" noValidate onSubmit={saveAddress} data-testid="share-config">
        <Field label={t('setup.share.address')}>{(id) => share.addresses.length === 0 && !share.address ? (
          <span id={id} className="hint" data-testid="share-no-address">{t('setup.share.noAddress')}</span>
        ) : (
          <select id={id} data-testid="share-address" value={address} disabled={inert || configure.pending}
            onChange={(event) => { cmd.clear('shareConfigure'); setDraftAddress(event.target.value); }}>
            {options.map((item) => (
              <option key={item.address} value={item.address}>{plainControls(item.name ? `${item.address} (${item.name})` : item.address)}</option>
            ))}
          </select>
        )}</Field>
        <Field label={t('setup.share.port')} error={portBad ? t('setup.share.portInvalid') : undefined}>{(id) => (
          <input id={id} type="text" inputMode="numeric" className="ui-mono" data-testid="share-port" maxLength={5} autoComplete="off" spellCheck={false}
            value={portText} disabled={inert || configure.pending} aria-invalid={portBad || undefined}
            onChange={(event) => { cmd.clear('shareConfigure'); setDraftPort(event.target.value); }} />
        )}</Field>
        {configure.error && <Note tone="error" text={configure.error} onDismiss={() => cmd.clear('shareConfigure')} />}
        <FormActions>
          {configure.done && (
            <span className="share-saved" role="status" aria-label={t('setup.share.saved')} data-testid="share-saved"><Icon name="check" size={14} /></span>
          )}
          <button type="submit" className="btn primary" data-testid="share-save" disabled={!canConfigure}>
            {configure.pending ? t('setup.share.saving') : t('setup.share.save')}
          </button>
        </FormActions>
      </form>

      <div className="share-clients">
        {share.clients.length > 0 && <DataTable columns="minmax(0, 1fr) auto" label={t('setup.share.clients')} testId="share-clients"
          head={[t('setup.share.clients'), '']}>
          {share.clients.map((client) => {
            const answer = cmd.sent.shareRevoke?.tag === client.name ? revoke : undefined;
            return (
              <Row key={client.name} testId={`share-client-${client.name}`}>
                <Cell className="key-provider-cell">
                  <span className="key-provider-text">
                    <strong className="share-client-name">{revealControls(client.name)}</strong>
                    {answer?.error && <span className="ui-sub ui-err" role="alert">{revealControls(answer.error)}</span>}
                  </span>
                </Cell>
                <Actions>
                  <IconButton icon="unlink" tone="danger" testId={`share-revoke-${client.name}`} label={plainControls(t('setup.share.revokeFor', { name: client.name }))}
                    disabled={inert || answer?.pending || asking !== undefined} onClick={() => setAsking(client.name)} />
                </Actions>
              </Row>
            );
          })}
        </DataTable>}
        {share.clients.length === 0 && <div className="ui-form share-clients-empty"><strong className="share-info-title">{t('setup.share.clients')}</strong>
          <span className="hint" data-testid="share-clients-empty">{t('setup.share.clientsEmpty')}</span></div>}
        {asking !== undefined && (
          <div className="acct-confirm share-confirm" role="group" aria-label={plainControls(t('setup.share.revokeFor', { name: asking }))} data-testid="share-revoke-confirm" tabIndex={-1}
            onKeyDown={(event) => {
              if (event.key === 'Escape' && !event.nativeEvent.isComposing) { event.preventDefault(); event.stopPropagation(); closeConfirm(asking); }
            }}>
            <span className="acct-confirm-title">{revealControls(t('setup.share.revokeFor', { name: asking }))}</span>
            <span className="acct-confirm-question">{t('setup.share.revokeQuestion')}</span>
            <div className="ui-form-actions">
              <button ref={cancelRef} type="button" className="btn subtle small" data-testid="share-revoke-cancel" onClick={() => closeConfirm(asking)}>{t('dialog.cancel')}</button>
              <button type="button" className="btn small danger" data-testid="share-revoke-yes" disabled={inert || revoke.pending} onClick={() => confirmRevoke(asking)}>{t('setup.share.revoke')}</button>
            </div>
          </div>
        )}
      </div>
    </ViewSection>
  );
}
