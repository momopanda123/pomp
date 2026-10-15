/**
 * Usage reports (`omp usage --json` through the host): pure helpers for the account panel and the model picker header.
 * Times are shown in the user's local time zone; `now` is a parameter so that the output is reproducible.
 */
import type { Translate } from './i18n';
import { USAGE_MAX_AGE_MS, type SetupUsage } from './setup';
import type { UsageLimit, UsageReport, UsageStatus } from './protocol';

/** `omp usage` can take 45 s: its answer may come late, so this command waits longer than the others. */
export const USAGE_REPLY_TIMEOUT_MS = 60_000;

const clamp01 = (value: number): number => Math.min(1, Math.max(0, value));

/** The limit's reset time has passed: its window started over, so none of the use the report counted is left. */
export function limitReset(limit: UsageLimit, now: number = Date.now()): boolean {
  const at = limit.resetsAt ? Date.parse(limit.resetsAt) : Number.NaN;
  return !Number.isNaN(at) && at <= now;
}

/**
 * How much of a limit is used, 0..1: the host's `usedFraction`, else `used / limit`; undefined when neither is known. A limit whose
 * reset time has passed is 0 (the report was taken before the reset).
 */
export function limitFraction(limit: UsageLimit, now: number = Date.now()): number | undefined {
  if (limitReset(limit, now) && (limit.usedFraction !== undefined || (limit.used !== undefined && limit.limit !== undefined))) return 0;
  if (typeof limit.usedFraction === 'number' && Number.isFinite(limit.usedFraction)) return clamp01(limit.usedFraction);
  if (typeof limit.used === 'number' && typeof limit.limit === 'number' && limit.limit > 0 && Number.isFinite(limit.used)) {
    return clamp01(limit.used / limit.limit);
  }
  return undefined;
}

/** The status to colour a limit with: the host's, or (when it says "unknown") one derived from how much is used; "ok" once it reset. */
export function limitStatus(limit: UsageLimit, now: number = Date.now()): UsageStatus {
  if (limitReset(limit, now)) return 'ok';
  if (limit.status !== 'unknown') return limit.status;
  const fraction = limitFraction(limit, now);
  if (fraction === undefined) return 'unknown';
  return fraction >= 1 ? 'exhausted' : fraction >= 0.8 ? 'warning' : 'ok';
}

const SEVERITY: Record<UsageStatus, number> = { unknown: 0, ok: 1, warning: 2, exhausted: 3 };

/** A limit counted for every model of the account (or not scoped at all). */
const isShared = (limit: UsageLimit): boolean => limit.shared === true || (!limit.tier && !limit.modelId);

/**
 * One subscription's limits in fixed places: the 5-hour session window and the weekly window (both shared; undefined when the
 * provider does not report one), the model-specific limits (\`current\` = counts for the model in use) and everything else
 * (extra usage, daily or monthly windows...).
 */
export interface UsageSlots {
  session?: UsageLimit;
  weekly?: UsageLimit;
  models: { limit: UsageLimit; current: boolean }[];
  others: UsageLimit[];
}

export function usageSlots(limits: readonly UsageLimit[], modelId: string | undefined): UsageSlots {
  const slots: UsageSlots = { models: [], others: [] };
  for (const limit of limits) {
    if (limitFraction(limit) === undefined && limit.status === 'unknown') continue;
    const minutes = windowMinutes(limit);
    if (!isShared(limit)) slots.models.push({ limit, current: limitAppliesTo(limit, modelId) });
    else if (!slots.session && minutes >= 180 && minutes <= 420) slots.session = limit;
    else if (!slots.weekly && minutes >= 6 * 1440 && minutes <= 8 * 1440) slots.weekly = limit;
    else slots.others.push(limit);
  }
  slots.models.sort((a, b) => Number(b.current) - Number(a.current) || windowMinutes(a.limit) - windowMinutes(b.limit));
  slots.others.sort((a, b) => windowMinutes(a) - windowMinutes(b));
  return slots;
}

const PLAN_NAMES: Readonly<Record<string, string>> = {
  free: 'Free', go: 'Go', plus: 'Plus', pro: 'Pro', prolite: 'Pro Lite', pro_lite: 'Pro Lite', team: 'Team', business: 'Business',
  enterprise: 'Enterprise', edu: 'Edu', max: 'Max', max_5x: 'Max 5x', max_20x: 'Max 20x',
};

/** "prolite" → "Pro Lite", "chatgpt_plus" → "Plus", anything else title-cased word by word. */
export function planLabel(plan: string): string {
  const key = plan.trim().toLowerCase().replace(/[\s-]+/g, '_').replace(/^chatgpt_/, '');
  return PLAN_NAMES[key] ?? key.split('_').filter(Boolean).map((word) => word[0]!.toUpperCase() + word.slice(1)).join(' ');
}

/** The limit closest to running out among the reports of `provider` (counting for `modelId` when given): worst status first, then the most used. */
export function tightestLimit(reports: readonly UsageReport[], provider: string, modelId?: string, now: number = Date.now()): { limit: UsageLimit; report: UsageReport } | undefined {
  let best: { limit: UsageLimit; report: UsageReport } | undefined;
  let bestRank = -1;
  let bestFraction = -1;
  for (const report of reports) {
    if (report.provider !== provider) continue;
    for (const limit of report.limits) {
      if (limitFraction(limit) === undefined && limit.status === 'unknown') continue; // nothing to say about this one
      if (modelId !== undefined && !limitAppliesTo(limit, modelId)) continue; // another model family's own limit
      const rank = SEVERITY[limitStatus(limit, now)];
      const fraction = limitFraction(limit, now) ?? -1;
      if (rank > bestRank || (rank === bestRank && fraction > bestFraction)) {
        best = { limit, report };
        bestRank = rank;
        bestFraction = fraction;
      }
    }
  }
  return best;
}

/** The earliest reset time of any limit in the report that is still ahead of `now` (epoch ms), or undefined. */
export function nextUsageReset(usage: SetupUsage | undefined, now: number): number | undefined {
  let next: number | undefined;
  for (const report of usage?.reports ?? []) {
    for (const limit of report.limits) {
      const at = limit.resetsAt ? Date.parse(limit.resetsAt) : Number.NaN;
      if (!Number.isNaN(at) && at > now && (next === undefined || at < next)) next = at;
    }
  }
  return next;
}

/** Age of a usage report in milliseconds; Infinity when it is missing or its time is unknown (so it counts as stale). */
export const usageAge = (usage: SetupUsage | undefined, now: number): number =>
  usage?.fetchedAt === undefined ? Number.POSITIVE_INFINITY : Math.max(0, now - usage.fetchedAt);

/** The latest reset time (epoch ms) up to `now` of a limit that reset after the report was taken, or undefined. */
function resetAfterReport(usage: SetupUsage, now: number): number | undefined {
  let latest: number | undefined;
  for (const report of usage.reports) {
    for (const limit of report.limits) {
      const at = limit.resetsAt ? Date.parse(limit.resetsAt) : Number.NaN;
      if (!Number.isNaN(at) && at <= now && (usage.fetchedAt === undefined || at > usage.fetchedAt) && (latest === undefined || at > latest)) latest = at;
    }
  }
  return latest;
}

/** True when usage should be asked for again: nothing yet, older than `maxAgeMs`, or a limit reset after the report was taken. */
export const usageStale = (usage: SetupUsage | undefined, now: number, maxAgeMs: number): boolean => usage === undefined || usageAge(usage, now) > maxAgeMs
  || resetAfterReport(usage, now) !== undefined;

/** When the page last asked for usage on its own (the panel opened, a picker opened). Shared by every component of the page. */
const lastAutomaticAsk = { at: 0 };

/**
 * Decides whether the page should ask the host for usage by itself (`omp usage` asks every signed-in provider over the network, up to
 * 45 s): only when it is missing or older than a minute, not while the host is already fetching it, and at most once a minute
 * whatever the answer was, except once right after a limit reset (the new window). The caller posts `listUsage` when this returns
 * true. The refresh button does not use this.
 */
export function claimUsageRequest(usage: SetupUsage | undefined, loading: boolean, now: number): boolean {
  if (loading || !usageStale(usage, now, USAGE_MAX_AGE_MS)) return false;
  const reset = usage === undefined ? undefined : resetAfterReport(usage, now);
  if (now - lastAutomaticAsk.at < USAGE_MAX_AGE_MS && (reset === undefined || lastAutomaticAsk.at >= reset)) return false;
  lastAutomaticAsk.at = now;
  return true;
}

/** Forgets the last automatic request (tests). */
export const resetUsageClaim = (): void => {
  lastAutomaticAsk.at = 0;
};

const WINDOW_UNITS: Readonly<Record<string, 'minute' | 'hour' | 'day' | 'week' | 'month'>> = {
  m: 'minute', min: 'minute', mins: 'minute', minute: 'minute', minutes: 'minute',
  h: 'hour', hr: 'hour', hrs: 'hour', hour: 'hour', hours: 'hour',
  d: 'day', day: 'day', days: 'day',
  w: 'week', wk: 'week', wks: 'week', week: 'week', weeks: 'week',
  mo: 'month', mos: 'month', month: 'month', months: 'month',
};

/** "5 Hour" -> the session slot ("5시간"), "7 Day" -> the weekly slot ("주간"), "3 Day" -> "3일"; a window this page cannot read is shown as the host wrote it. */
export function windowLabel(t: Translate, window: string): string {
  const trimmed = window.trim();
  if (/^(?:7\s*[- ]?\s*(?:days?|d)|1\s*[- ]?\s*(?:weeks?|w)|weekly)$/i.test(trimmed)) return t('usage.slot.weekly');
  if (/^5\s*[- ]?\s*(?:hours?|hrs?|h)$/i.test(trimmed)) return t('usage.slot.session');
  const match = /^(\d+(?:\.\d+)?)\s*[- ]?\s*([a-z]+)$/i.exec(trimmed);
  const unit = match ? WINDOW_UNITS[match[2]!.toLowerCase()] : undefined;
  if (match && unit) {
    const n = match[1]!;
    switch (unit) {
      case 'minute': return t('usage.window.minute', { n });
      case 'hour': return t('usage.window.hour', { n });
      case 'day': return t('usage.window.day', { n });
      case 'week': return t('usage.window.week', { n });
      case 'month': return t('usage.window.month', { n });
    }
  }
  switch (trimmed.toLowerCase()) {
    case 'daily': return t('usage.window.daily');
    case 'weekly': return t('usage.window.weekly');
    case 'monthly': return t('usage.window.monthly');
    default: return trimmed;
  }
}

const startOfDay = (time: number): number => {
  const date = new Date(time);
  return new Date(date.getFullYear(), date.getMonth(), date.getDate()).getTime();
};

/**
 * When a limit resets, for people: "14:20 초기화" today, "내일 14:20 초기화" tomorrow, "10월 5일 초기화" later; undefined without a
 * (readable) time; a time in the past says so (the report is old).
 */
export function formatReset(resetsAt: string | undefined, now: number, locale: string, t: Translate): string | undefined {
  if (!resetsAt) return undefined;
  const at = Date.parse(resetsAt);
  if (Number.isNaN(at)) return undefined;
  if (at <= now) return t('usage.resetPast');
  const date = new Date(at);
  const days = Math.round((startOfDay(at) - startOfDay(now)) / 86_400_000);
  if (days <= 1) {
    const time = date.toLocaleTimeString(locale, { hour: '2-digit', minute: '2-digit', hourCycle: 'h23' });
    return t(days === 0 ? 'usage.resetsAt' : 'usage.resetsTomorrow', { time });
  }
  const sameYear = date.getFullYear() === new Date(now).getFullYear();
  const day = date.toLocaleDateString(locale, sameYear ? { month: 'long', day: 'numeric' } : { year: 'numeric', month: 'long', day: 'numeric' });
  return t('usage.resetsOn', { date: day });
}

/** "Claude 7 Day (Fable)", "7 days (Spark)", "Claude 5 Hour": an optional product word, a window, an optional model name. */
const SCOPED_LABEL = /^(?:\S.*?\s+)?(\d+(?:\.\d+)?\s*[- ]?\s*[a-z]+)(?:\s*\(([^()]{1,60})\))?$/i;

/**
 * What a limit is called, in the page's language: a label that names a window (with or without a product word and a model
 * name) becomes "5시간", "주간", "주간 · Fable"; "Weekly" and the like are localized; any other label is shown as written.
 */
export function limitName(t: Translate, limit: UsageLimit): string {
  const label = limit.label.trim();
  if (!label) return limit.window ? windowLabel(t, limit.window) : t('usage.limit');
  if (/\bextra usage\b/i.test(label)) return t('usage.extra');
  const match = SCOPED_LABEL.exec(label);
  if (match) {
    const window = windowLabel(t, match[1]!);
    if (window !== match[1]!.trim()) return match[2] ? `${window} · ${match[2].trim()}` : window;
  }
  return windowLabel(t, label);
}

/** The window as a second piece of text, only when the name does not already say it ("7 days" + "7 Day" → just "주간"). */
export const limitWindowText = (t: Translate, limit: UsageLimit): string | undefined => {
  if (!limit.window || !limit.label) return undefined;
  const text = windowLabel(t, limit.window);
  return limitName(t, limit).startsWith(text) ? undefined : text;
};

/**
 * Whether a limit counts for this model (omp's own rule): shared limits and unscoped ones always; a model-family limit
 * ("tier") when the model id names that family (claude-fable-5-1 → fable); a one-model limit for that model only.
 */
export function limitAppliesTo(limit: UsageLimit, modelId: string | undefined): boolean {
  if (limit.shared === true || (!limit.tier && !limit.modelId)) return true;
  if (!modelId) return false;
  const id = modelId.toLowerCase();
  if (limit.modelId && limit.modelId.toLowerCase() === id) return true;
  return limit.tier !== undefined && id.split(/[^a-z0-9]+/).includes(limit.tier);
}

const UNIT_MINUTES = { minute: 1, hour: 60, day: 1440, week: 10080, month: 43200 } as const;

/** Length of a limit's window in minutes, for ordering ("5 Hour" before "7 Day"); unknown windows go last. */
function windowMinutes(limit: UsageLimit): number {
  const source = (limit.window ?? SCOPED_LABEL.exec(limit.label)?.[1] ?? '').trim();
  const match = /^(\d+(?:\.\d+)?)\s*[- ]?\s*([a-z]+)$/i.exec(source);
  const unit = match ? WINDOW_UNITS[match[2]!.toLowerCase()] : undefined;
  if (match && unit) return Number(match[1]) * UNIT_MINUTES[unit];
  switch (source.toLowerCase()) {
    case 'daily': return 1440;
    case 'weekly': return 10080;
    case 'monthly': return 43200;
    default: return Number.POSITIVE_INFINITY;
  }
}

const scoped = (limit: UsageLimit): number => Number(limit.shared !== true && !!(limit.tier || limit.modelId));

/**
 * The limits of `provider` that say something, split into the ones that count for the current model (shortest window first,
 * shared before model-specific) and the others (model families the current model is not part of).
 */
export function providerLimits(usage: SetupUsage | undefined, provider: string | undefined, modelId: string | undefined): { current: UsageLimit[]; other: UsageLimit[] } {
  const current: UsageLimit[] = [];
  const other: UsageLimit[] = [];
  if (!usage || !provider) return { current, other };
  for (const report of usage.reports) {
    if (report.provider !== provider) continue;
    for (const limit of report.limits) {
      if (limitFraction(limit) === undefined && limit.status === 'unknown') continue;
      (limitAppliesTo(limit, modelId) ? current : other).push(limit);
    }
  }
  const order = (a: UsageLimit, b: UsageLimit): number => windowMinutes(a) - windowMinutes(b) || scoped(a) - scoped(b);
  current.sort(order);
  other.sort(order);
  return { current, other };
}

export interface UsageSummary {
  /** "5시간 32% · 14:20 초기화" */
  text: string;
  status: UsageStatus;
  /** 0..1 when known. */
  fraction?: number;
  /** Every limit of the provider, one per line, for a tooltip. */
  title: string;
}

/** One compact line about the tightest limit of `provider` (undefined when no report says anything about it). */
export function usageSummary(usage: SetupUsage | undefined, provider: string | undefined, now: number, locale: string, t: Translate, modelId?: string): UsageSummary | undefined {
  if (!usage || !provider) return undefined;
  const tight = tightestLimit(usage.reports, provider, modelId, now);
  if (!tight) return undefined;
  const { limit } = tight;
  const fraction = limitFraction(limit, now);
  const name = limitName(t, limit);
  const parts = [fraction === undefined ? name : `${name} ${Math.round(fraction * 100)}%`];
  const reset = formatReset(limit.resetsAt, now, locale, t);
  if (reset) parts.push(reset);
  const lines: string[] = [];
  for (const report of usage.reports) {
    if (report.provider !== provider) continue;
    for (const item of report.limits) {
      const share = limitFraction(item, now);
      const itemReset = formatReset(item.resetsAt, now, locale, t);
      const itemWindow = limitWindowText(t, item);
      lines.push([`${limitName(t, item)}${itemWindow ? ` (${itemWindow})` : ''}`, share === undefined ? undefined : `${Math.round(share * 100)}%`, itemReset].filter(Boolean).join(' · '));
    }
  }
  const summary: UsageSummary = { text: parts.join(' · '), status: limitStatus(limit, now), title: lines.join('\n') };
  if (fraction !== undefined) summary.fraction = fraction;
  return summary;
}
