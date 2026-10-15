import { useNow } from '../hooks';
import { useT } from '../i18n';
import { WORKSPACE_TRUST_REQUIRED, type BridgeStatePayload } from '../protocol';
import { isSetupRequired } from '../setup';
import { ClampedText } from './ClampedText';
import { Icon } from './Icon';
import { WorkspaceTrustCard } from './WorkspaceTrustCard';

interface ConnectionBannerProps {
  bridge: BridgeStatePayload & { since: number };
  onRestart: () => void;
  onOpenLog: () => void;
}

/**
 * Banner below the status bar for `reconnecting` (attempt + retry countdown) and `failed` (restart / show log).
 * A failure that only means "omp has no model provider yet" (`reason: "setupRequired"`) shows nothing here: the setup panel
 * replaces it. One that means "the workspace's engine configuration is not trusted yet" shows the trust card instead. The detail
 * text of a failure is raw process output and can be long, so it is clamped (see ClampedText).
 */
export function ConnectionBanner({ bridge, onRestart, onOpenLog }: ConnectionBannerProps) {
  const t = useT();
  const counting = bridge.state === 'reconnecting' && bridge.nextRetryMs !== undefined && bridge.nextRetryMs > 0;
  const now = useNow(counting);

  if (bridge.state === 'reconnecting') {
    const seconds = counting ? Math.max(0, Math.ceil((bridge.since + (bridge.nextRetryMs ?? 0) - now) / 1000)) : undefined;
    return (
      <div className="banner warning" role="status">
        <span className="spinner" aria-hidden="true" />
        <span className="banner-text">
          {t('banner.reconnecting', { attempt: bridge.attempt ?? 1 })}
          {seconds !== undefined && ` — ${t('banner.retryIn', { seconds })}`}
          {bridge.message && <ClampedText className="banner-detail" text={bridge.message} />}
          <span className="banner-detail lost">{t('banner.lostContext')}</span>
        </span>
      </div>
    );
  }

  if (bridge.state === 'failed') {
    if (isSetupRequired(bridge)) return null;
    if (bridge.reason === WORKSPACE_TRUST_REQUIRED) return <WorkspaceTrustCard />;
    return (
      <div className="banner error" role="alert">
        <Icon name="error" size={14} />
        <span className="banner-text">
          <strong>{t('banner.failed')}</strong>
          {bridge.message && <ClampedText className="banner-detail" text={bridge.message} />}
          <span className="banner-detail lost">{t('banner.lostContext')}</span>
        </span>
        <span className="banner-actions">
          <button type="button" className="btn primary" onClick={onRestart}>
            <Icon name="restart" size={12} />
            {t('banner.restart')}
          </button>
          <button type="button" className="btn" onClick={onOpenLog}>
            {t('banner.showLog')}
          </button>
        </span>
      </div>
    );
  }

  return null;
}
