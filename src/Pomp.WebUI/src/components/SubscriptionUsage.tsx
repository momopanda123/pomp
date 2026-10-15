import { useT } from '../i18n';
import type { UsageLimit, UsageReport } from '../protocol';
import { providerLabel, providerName, type SetupState } from '../setup';
import { limitFraction, limitName, limitStatus, limitWindowText, planLabel, usageSlots } from '../usage';
import { revealControls } from './RevealControls';
import { UsageRow } from './UsageRow';

/** Only windows actually reported by the provider are rendered. */
export function SubscriptionUsage({ limits, modelId, now, locale }: { limits: readonly UsageLimit[]; modelId: string | undefined; now: number; locale: string }) {
  const t = useT();
  const slots = usageSlots(limits, modelId);
  return (
    <div className="subscription-usage" data-testid="subscription-usage">
      {slots.session && <UsageRow limit={slots.session} now={now} locale={locale} />}
      {slots.weekly && <UsageRow limit={slots.weekly} now={now} locale={locale} />}
      {slots.models.length > 0 && (
        <>
          <div className="usage-slot-head">{t('usage.slot.models')}</div>
          {slots.models.map(({ limit, current }, index) => (
            <UsageRow key={`m${index}`} limit={limit} now={now} locale={locale} badge={current ? t('usage.slot.current') : undefined} />
          ))}
        </>
      )}
      {slots.others.map((limit, index) => (
        <UsageRow key={`o${index}`} limit={limit} now={now} locale={locale} />
      ))}
    </div>
  );
}

/** "Pro Lite 요금제 · user@example.com": the plan and account a report belongs to, when the provider says. */
export function reportCaption(t: ReturnType<typeof useT>, report: UsageReport): string | undefined {
  const parts = [report.plan ? t('usage.plan', { plan: planLabel(report.plan) }) : undefined, report.account].filter(Boolean);
  return parts.length > 0 ? parts.join(' · ') : undefined;
}

/** A closed account's one-line usage: its tightest limit, "주간 42%", in the limit's status colour. */
export function LimitSummary({ limit, now, testId }: { limit: UsageLimit; now: number; testId?: string }) {
  const t = useT(); const share = limitFraction(limit, now); const windowText = limitWindowText(t, limit);
  return <span className={`acct-summary usage-row ${limitStatus(limit, now)}`} data-testid={testId}><span className="acct-summary-name">{revealControls(windowText ? `${limitName(t, limit)} ${windowText}` : limitName(t, limit))}</span>{share !== undefined && <strong>{Math.round(share * 100)}%</strong>}</span>;
}

/** Every subscription's usage, grouped by provider (the composer's usage popover). */
export function UsageOverview({ setup, provider, modelId, now, locale }: { setup: SetupState | null; provider: string | undefined; modelId: string | undefined; now: number; locale: string }) {
  const t = useT();
  const reports = setup?.usage?.reports ?? [];
  if (reports.length === 0) return null;
  return (
    <ul className="usage-overview">
      {reports.map((report, index) => {
        const caption = reportCaption(t, report);
        return (
          <li key={`${report.provider}:${index}`} className="usage-overview-item" data-testid="usage-overview-item">
            <div className="acct-head">
              <span className="acct-name">{revealControls(providerLabel(report.provider, providerName(setup?.providers, report.provider)))}</span>
            </div>
            {caption && <div className="acct-label">{revealControls(caption)}</div>}
            <SubscriptionUsage limits={report.limits} modelId={report.provider === provider ? modelId : undefined} now={now} locale={locale} />
          </li>
        );
      })}
    </ul>
  );
}
