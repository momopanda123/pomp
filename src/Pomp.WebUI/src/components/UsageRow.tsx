import { formatCount } from '../format';
import { useT, type MessageKey } from '../i18n';
import type { UsageLimit, UsageStatus } from '../protocol';
import { formatReset, limitFraction, limitName, limitStatus, limitWindowText } from '../usage';
import { plainControls, revealControls } from './RevealControls';

/** What a limit without a percentage says instead (written out, so that every key stays visible to the unused-key check). */
const STATUS_KEYS: Readonly<Record<UsageStatus, MessageKey>> = {
  ok: 'usage.status.ok',
  warning: 'usage.status.warning',
  exhausted: 'usage.status.exhausted',
  unknown: 'usage.status.unknown',
};

/**
 * One limit of an account: its label and window, how much is used as a bar coloured by status (ok / warning / exhausted), and when
 * it resets in local time. Everything the host wrote is shown as text.
 */
export function UsageRow({ limit, now, locale, badge }: { limit: UsageLimit; now: number; locale: string; badge?: string }) {
  const t = useT();
  const fraction = limitFraction(limit, now);
  const status = limitStatus(limit, now);
  const name = limitName(t, limit);
  // The window is a second piece of text only when the label says something else already (otherwise it is the name).
  const windowText = limitWindowText(t, limit);
  const percent = fraction === undefined ? undefined : Math.round(fraction * 100);
  const reset = formatReset(limit.resetsAt, now, locale, t);
  const amount =
    fraction === undefined && limit.used !== undefined && limit.limit !== undefined
      ? `${formatCount(limit.used)} / ${formatCount(limit.limit)}${limit.unit ? ` ${limit.unit}` : ''}`
      : undefined;

  return (
    <div className={`usage-row ${status}`} data-testid="usage-row" data-status={status}>
      <div className="usage-line">
        <span className="usage-name">
          {revealControls(name)}
          {windowText && <span className="usage-window">{revealControls(windowText)}</span>}
          {badge && <span className="usage-badge">{badge}</span>}
        </span>
        <span className="usage-value" data-testid="usage-value">
          {percent !== undefined ? `${percent}%` : revealControls(amount ?? t(STATUS_KEYS[status]))}
        </span>
      </div>
      {percent !== undefined && (
        <div className="usage-bar" role="progressbar" aria-valuemin={0} aria-valuemax={100} aria-valuenow={percent} aria-label={plainControls(t('usage.progress', { name }))}>
          <span className="usage-fill" style={{ width: `${percent}%` }} />
        </div>
      )}
      {reset && (
        <div className="usage-reset" data-testid="usage-reset">
          {reset}
        </div>
      )}
    </div>
  );
}
