import { useEffect, useRef, useState } from 'react';
import { newId } from '../format';
import { useT, type MessageKey } from '../i18n';
import { onMessage, send, type UiToHost, type WorkspaceTrustState } from '../protocol';
import { Icon } from './Icon';
import { revealControls } from './RevealControls';

const KINDS = ['settings', 'mcp', 'tools', 'lsp', 'plugins', 'extensions', 'include', 'advisors'];

/**
 * In place of the connection banner when omp did not start because the workspace has engine configuration the user has not trusted
 * (bridge reason `workspaceTrustRequired`, docs/ui-protocol.md "작업 영역 신뢰"): what omp would run, each file openable in VS, and
 * trust (exactly the listed set: its fingerprint) or don't start. Nothing here starts omp but "trust".
 */
export function WorkspaceTrustCard() {
  const t = useT();
  const [state, setState] = useState<WorkspaceTrustState>();
  const [pending, setPending] = useState(false);
  const [error, setError] = useState('');
  const [declined, setDeclined] = useState(false);
  const request = useRef<string | undefined>(undefined);
  const latest = useRef(state); latest.current = state;

  function command(action: UiToHost['workspace/trust']['action'], extra: Partial<UiToHost['workspace/trust']> = {}) {
    if (request.current) return;
    const requestId = newId();
    request.current = requestId;
    if (!send('workspace/trust', { requestId, action, root: latest.current?.root, ...extra })) {
      request.current = undefined; setError(t('error.hostUnavailable')); return;
    }
    setPending(true); setError('');
  }

  useEffect(() => onMessage(message => {
    if (message.type !== 'workspace/trustState') return;
    // Every state (a launch's, or the answer to any request) describes the set as it is now.
    setState(message.payload);
    if (message.payload.requestId && message.payload.requestId === request.current) {
      request.current = undefined; setPending(false); setError(message.payload.error ?? '');
    }
  }), []);
  // The launch may have posted its state before this card mounted: ask once.
  useEffect(() => { command('refresh'); }, []);
  useEffect(() => {
    if (!pending) return;
    const timer = window.setTimeout(() => { request.current = undefined; setPending(false); setError(t('error.hostUnavailable')); }, 15000);
    return () => window.clearTimeout(timer);
  }, [pending, t]);

  if (!state?.required || state.trusted) {
    return <div className="banner warning" role="status" data-testid="workspace-trust-checking"><span className="spinner" aria-hidden="true" /><span className="banner-text">{t('trust.checking')}</span></div>;
  }
  const kind = (value: string) => t((KINDS.includes(value) ? `trust.kind.${value}` : 'trust.kind.settings') as MessageKey);
  return <div className="banner warning workspace-trust" role="alert" data-testid="workspace-trust">
    <Icon name="warning" size={14} />
    <div className="banner-text">
      <strong>{t('trust.title')}</strong>
      <ul className="workspace-trust-files" data-testid="workspace-trust-files">{state.files.map(file => <li key={file.path + '\0' + (file.detail ?? '')}>
        <code title={file.path}>{revealControls(file.path)}</code>
        <span className="workspace-trust-kind">{kind(file.kind)}{file.detail && <> · {revealControls(file.detail)}</>}</span>
        <button type="button" className="btn subtle small" data-testid={`workspace-trust-open-${file.path}`} disabled={pending} onClick={() => command('open', { path: file.path })}>{t('trust.open')}</button>
      </li>)}</ul>
      {state.truncated && <span className="banner-detail">{t('trust.more')}</span>}
      {declined && <span className="banner-detail" role="status" data-testid="workspace-trust-declined">{t('trust.declined')}</span>}
      {error && <span className="banner-detail workspace-trust-error" role="alert" data-testid="workspace-trust-error">{revealControls(error)}</span>}
    </div>
    <span className="banner-actions">
      <button type="button" className="btn primary" data-testid="workspace-trust-accept" disabled={pending} onClick={() => command('trust', { fingerprint: state.fingerprint })}>{t('trust.trust')}</button>
      {!declined && <button type="button" className="btn" data-testid="workspace-trust-decline" disabled={pending} onClick={() => setDeclined(true)}>{t('trust.decline')}</button>}
    </span>
  </div>;
}
