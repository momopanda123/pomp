import { memo, useEffect, useLayoutEffect, useRef, useState, type CSSProperties } from 'react';
import { useUsageNow } from '../hooks';
import { useT, type MessageKey } from '../i18n';
import { onMessage, send, type BasketTokens, type FastMode, type SessionInfoPayload, type UsageLimit, type UsageStatus } from '../protocol';
import { newId } from '../format';
import { breakdownTokens, CONTEXT_SEGMENT_KEYS, gapFor, layoutSegments, type ContextSegmentKey } from '../contextSegments';
import '../styles/context-meter.css';
import { providerLabel, providerName, type SetupReply, type SetupState } from '../setup';
import { useSetupCommands } from '../setupCommands';
import { claimUsageRequest, limitFraction, limitName, limitStatus, USAGE_REPLY_TIMEOUT_MS, usageSlots } from '../usage';
import { Icon } from './Icon';
import { plainControls, revealControls } from './RevealControls';
import { UsageOverview } from './SubscriptionUsage';

/** 1234 → "1.2k", 200000 → "200k", 1048576 → "1M": token counts for a one-line meter. */
export function shortTokens(value: number): string {
  const abs = Math.abs(value);
  const trim = (n: number): string => (n >= 100 ? Math.round(n).toString() : n.toFixed(1).replace(/\.0$/, ''));
  if (abs >= 1_000_000) return `${trim(value / 1_000_000)}M`;
  if (abs >= 1_000) return `${trim(value / 1_000)}k`;
  return Math.round(value).toString();
}

/** ok below 70 %, warning from 70 %, exhausted from 90 % (compaction is near). */
const contextStatus = (percent: number): UsageStatus => (percent >= 90 ? 'exhausted' : percent >= 70 ? 'warning' : 'ok');

// ---- Context (above the message box) ----------------------------------------------------------------------------------

/** What the next message adds (editor context, attachment list) is asked for this long after its inputs settle. */
const BASKET_DEBOUNCE_MS = 500;

interface BasketInputs {
  workspaceKey?: string;
  sessionId?: string;
  includeEditorContext?: boolean;
  attachmentPaths?: readonly string[];
  connected?: boolean;
  /** The context was measured again (after a turn): the basket is estimated again too. */
  measuredAt?: string;
  reply?: BasketTokens | null;
}

/**
 * The basket estimate for the bar: sends `workspace/countBasketTokens` when its inputs change (debounced) and keeps only the reply
 * to the latest request. A failed reply hides the basket segment.
 */
function useBasketEstimate({ workspaceKey, sessionId, includeEditorContext, attachmentPaths, connected, measuredAt, reply }: BasketInputs): number {
  const latestId = useRef<string | null>(null);
  const [accepted, setAccepted] = useState<BasketTokens | null>(null);
  const pathsKey = JSON.stringify(attachmentPaths ?? []);
  useEffect(() => { latestId.current = null; setAccepted(null); }, [workspaceKey, sessionId, connected]);
  useEffect(() => {
    if (!connected) return;
    const timer = window.setTimeout(() => {
      const requestId = newId();
      latestId.current = requestId;
      const attachments = (JSON.parse(pathsKey) as string[]).map((path) => ({ path }));
      send('workspace/countBasketTokens', { requestId, root: workspaceKey ?? '', ...(sessionId ? { sessionId } : {}), includeEditorContext: includeEditorContext === true, attachments });
    }, BASKET_DEBOUNCE_MS);
    return () => window.clearTimeout(timer);
  }, [connected, workspaceKey, sessionId, includeEditorContext, pathsKey, measuredAt]);
  useEffect(() => {
    if (reply && reply.requestId === latestId.current) setAccepted(reply);
  }, [reply]);
  return accepted?.ok && accepted.tokens > 0 ? accepted.tokens : 0;
}

type SegmentKey = ContextSegmentKey | 'basket';

const SEGMENT_LABELS: Record<SegmentKey, MessageKey> = {
  prompt: 'meter.seg.prompt',
  tools: 'meter.seg.tools',
  mcp: 'meter.seg.mcp',
  context: 'meter.seg.context',
  skills: 'meter.seg.skills',
  messages: 'meter.seg.messages',
  basket: 'meter.seg.basket',
};

interface ContextMeterProps extends Omit<BasketInputs, 'measuredAt' | 'reply'> {
  session: SessionInfoPayload | null;
  /** The latest `workspace/basketTokens` reply. */
  basketTokens?: BasketTokens | null;
}

/** How full the model's context window is, from the engine's last state: a bar split by source and "42% · 84k / 200k". */
function ContextMeterView({ session, basketTokens, workspaceKey, sessionId, includeEditorContext, attachmentPaths, connected }: ContextMeterProps) {
  const t = useT();
  const usage = session?.contextUsage;
  const breakdown = usage?.breakdown;
  const basket = useBasketEstimate({ workspaceKey, sessionId, includeEditorContext, attachmentPaths, connected, measuredAt: breakdown?.measuredAt, reply: basketTokens });

  const [open, setOpen] = useState(false);
  const rootRef = useRef<HTMLDivElement>(null);
  const buttonRef = useRef<HTMLButtonElement>(null);
  const barRef = useRef<HTMLSpanElement>(null);
  const popRef = useRef<HTMLDivElement>(null);
  const [barPx, setBarPx] = useState(0);
  const [placement, setPlacement] = useState<CSSProperties>({ visibility: 'hidden' });
  const hasUsage = !!usage;

  useLayoutEffect(() => {
    const bar = barRef.current;
    if (!bar) return;
    setBarPx(bar.getBoundingClientRect().width);
    if (typeof ResizeObserver === 'undefined') return;
    const observer = new ResizeObserver(() => setBarPx(bar.getBoundingClientRect().width));
    observer.observe(bar);
    return () => observer.disconnect();
  }, [hasUsage]);

  // The popover opens upwards or downwards, whichever has the room, and never taller than that room.
  const rowCount = (breakdown ? CONTEXT_SEGMENT_KEYS.length + 1 : 0) + (basket > 0 ? 1 : 0);
  useLayoutEffect(() => {
    if (!open) return;
    const place = (): void => {
      const root = rootRef.current;
      const pop = popRef.current;
      const parent = pop?.offsetParent as HTMLElement | null;
      if (!root || !pop || !parent) return;
      const anchor = root.getBoundingClientRect();
      const frame = parent.getBoundingClientRect();
      const need = pop.scrollHeight + 2;
      const above = anchor.top - 8;
      const below = window.innerHeight - anchor.bottom - 8;
      const up = need <= above || (need > below && above >= below);
      const room = Math.max(60, Math.min(up ? above : below, 460));
      setPlacement(up ? { bottom: frame.bottom - anchor.top + 3, maxHeight: room } : { top: anchor.bottom - frame.top + 3, maxHeight: room });
    };
    place();
    window.addEventListener('resize', place);
    return () => window.removeEventListener('resize', place);
  }, [open, rowCount, barPx]);

  useEffect(() => {
    if (!open) return;
    popRef.current?.focus({ preventScroll: true });
    const onPointerDown = (event: PointerEvent): void => {
      if (event.target instanceof Node && rootRef.current?.contains(event.target)) return;
      setOpen(false);
    };
    document.addEventListener('pointerdown', onPointerDown, true);
    return () => document.removeEventListener('pointerdown', onPointerDown, true);
  }, [open]);

  const contextWindow = usage?.contextWindow ?? session?.model?.contextWindow;
  if (!usage && !contextWindow) return null;
  const percent = usage ? Math.min(100, Math.max(0, usage.percent)) : undefined;
  const status = percent === undefined ? 'unknown' : contextStatus(percent);
  // Before the first reply only the window is known: "— / 272k".
  const value = usage ? `${Math.round(percent!)}% · ${shortTokens(usage.tokens)} / ${shortTokens(usage.contextWindow)}` : `— / ${shortTokens(contextWindow!)}`;
  const title = usage ? t('status.context', { percent: Math.round(usage.percent), tokens: usage.tokens.toLocaleString(), window: usage.contextWindow.toLocaleString() }) : t('meter.context');

  // The bar: one fill, or (once the host measured the breakdown) one segment per source and the hatched basket after it.
  const tokens = breakdown ? breakdownTokens(breakdown) : undefined;
  const filledPx = ((percent ?? 0) / 100) * barPx;
  const basketPx = usage && usage.contextWindow > 0 ? (basket / usage.contextWindow) * barPx : 0;
  const painted = tokens && barPx > 0 ? layoutSegments(tokens, filledPx, basketPx, barPx) : [];
  const contextSegments = painted.filter((segment) => segment.key !== 'basket');
  const basketSegment = painted.find((segment) => segment.key === 'basket');

  const bar = (
    <span
      ref={barRef}
      className="usage-bar meter-bar"
      role="meter"
      aria-label={t('meter.context')}
      aria-valuemin={0}
      aria-valuemax={100}
      aria-valuenow={percent === undefined ? undefined : Math.round(percent)}
      aria-valuetext={value}
    >
      {tokens && barPx > 0 ? (
        <span className="ctx-track">
          {filledPx > 0 && (
            <span className="ctx-filled" style={{ width: filledPx, gap: gapFor(filledPx) }}>
              {contextSegments.length > 0
                ? contextSegments.map((segment) => <span key={segment.key} className={`ctx-seg ctx-${segment.key}`} data-testid={`context-bar-seg-${segment.key}`} style={{ width: segment.width }} />)
                : <span className="usage-fill" />}
            </span>
          )}
          {basketSegment && <span className="ctx-seg ctx-basket" data-testid="context-bar-seg-basket" style={{ width: basketSegment.width, marginLeft: filledPx > 0 ? gapFor(filledPx) : 0 }} />}
        </span>
      ) : (
        <span className="usage-fill" style={{ width: `${percent ?? 0}%` }} />
      )}
    </span>
  );

  if (!usage) {
    return (
      <div className={`meter context-meter ${status}`} data-testid="context-meter" title={title}>
        {/* The bar and its value speak for themselves; the name stays in the tooltip and the accessible label. */}
        {bar}
        <span className="meter-value">{value}</span>
      </div>
    );
  }

  const tokensOf = (key: SegmentKey): number => (key === 'basket' ? basket : tokens?.[key] ?? 0);
  const percentOf = (count: number): string => `${((count / usage.contextWindow) * 100).toFixed(1)}%`;
  const row = (key: SegmentKey, estimated: boolean) => (
    <li className="ctx-row" key={key} data-testid={`context-seg-${key}`}>
      <span className={`ctx-swatch ctx-${key}`} aria-hidden="true" />
      <span className="ctx-label">{t(SEGMENT_LABELS[key])}</span>
      <span className="ctx-tokens">{estimated ? '≈' : ''}{shortTokens(tokensOf(key))}</span>
      <span className="ctx-percent">{percentOf(tokensOf(key))}</span>
    </li>
  );
  // The mix of what is used, independent of the window size: each source in proportion to the used tokens, the basket after it.
  const used = tokens ? CONTEXT_SEGMENT_KEYS.reduce((sum, key) => sum + tokens[key], 0) : 0;
  const composition = tokens && used > 0 ? (
    <div className="ctx-composition" data-testid="context-composition" aria-hidden="true">
      {CONTEXT_SEGMENT_KEYS.filter((key) => tokens[key] > 0).map((key) => <span key={key} className={`ctx-seg ctx-${key}`} data-testid={`context-comp-seg-${key}`} style={{ flex: `${tokens[key]} 1 0px` }} />)}
      {basket > 0 && <span className="ctx-seg ctx-basket" data-testid="context-comp-seg-basket" style={{ flex: `${basket} 1 0px` }} />}
    </div>
  ) : null;
  const heading = t('meter.breakdownTitle', { tokens: shortTokens(usage.tokens), window: shortTokens(usage.contextWindow) });
  return (
    <div
      className={`meter context-meter ${status}`}
      ref={rootRef}
      data-testid="context-meter"
      title={title}
      onKeyDown={(event) => {
        if (event.key !== 'Escape' || !open || event.nativeEvent.isComposing) return;
        event.preventDefault();
        event.stopPropagation();
        setOpen(false);
        buttonRef.current?.focus({ preventScroll: true });
      }}
    >
      <button
        ref={buttonRef}
        type="button"
        className="context-meter-open"
        data-testid="context-meter-open"
        aria-haspopup="dialog"
        aria-expanded={open}
        aria-label={`${t('meter.context')} ${value}`}
        onClick={() => {
          if (!open) setPlacement({ visibility: 'hidden' });
          setOpen(!open);
        }}
      >
        {bar}
        <span className="meter-value">{value}</span>
      </button>
      {open && (
        <div ref={popRef} className="context-breakdown" role="dialog" aria-label={heading} data-testid="context-breakdown" tabIndex={-1} style={placement}>
          <div className="context-breakdown-head">{heading}</div>
          {composition}
          {breakdown && (
            <ul className="ctx-rows">
              {CONTEXT_SEGMENT_KEYS.map((key) => row(key, key === 'mcp' || (key === 'messages' && breakdown.messagesEstimated === true)))}
              {basket > 0 && row('basket', true)}
              <li className="ctx-row ctx-free" data-testid="context-seg-free">
                <span className="ctx-swatch" aria-hidden="true" />
                <span className="ctx-label">{t('meter.seg.free')}</span>
                <span className="ctx-tokens">{shortTokens(Math.max(0, breakdown.free))}</span>
                <span className="ctx-percent">{percentOf(Math.max(0, breakdown.free))}</span>
              </li>
            </ul>
          )}
        </div>
      )}
    </div>
  );
}

export const ContextMeter = memo(ContextMeterView);

// ---- Quota (below the composer bar) -----------------------------------------------------------------------------------

/**
 * The chat's usage meter stays one line: as many limits as fit whole, each at its own width (5 hours, the week, the current
 * model's own family, then the rest); the others are counted ("+2") and listed in the popover. At least one is always shown.
 */
const LIMIT_GAP_PX = 10;

/**
 * How many limits fit on the meter's line (the bars' button), without a "+N" after them and with one, from the widths of a hidden
 * copy of every limit (name, a short bar, value).
 */
function limitRoom(line: HTMLElement, sizes: HTMLElement): { all: number; withMore: number } {
  const free = line.clientWidth - 4;
  const widths = [...sizes.querySelectorAll<HTMLElement>('.quota-limit')].map((item) => item.getBoundingClientRect().width);
  const more = sizes.querySelector<HTMLElement>('.meter-more')?.getBoundingClientRect().width ?? 0;
  const fit = (room: number): number => {
    let used = 0;
    let count = 0;
    for (const width of widths) {
      used += (count > 0 ? LIMIT_GAP_PX : 0) + width;
      if (used > room) break;
      count++;
    }
    return Math.max(1, count);
  };
  return { all: fit(free), withMore: fit(free - more - LIMIT_GAP_PX) };
}

/** Usage is refreshed on its own while the page is open and idle: often enough to notice a limit filling up, not more. */
const AUTO_REFRESH_MS = 5 * 60_000;

interface QuotaMeterProps {
  session: SessionInfoPayload | null;
  setup: SetupState | null;
  replies: SetupReply[];
  connected: boolean;
  busy: boolean;
  locale: string;
  /** Opens the accounts tab (the popover's "manage accounts"). */
  onManage?: () => void;
}

/**
 * The current account's usage limits (`omp usage` through the host): one small bar per limit of the current model's provider.
 * Asked for when the page connects and after a turn, at most once a minute (claimUsageRequest), and every five minutes while idle.
 */
function QuotaMeterView({ session, setup, replies, connected, busy, locale, onManage }: QuotaMeterProps) {
  const t = useT();
  const { post, feedback } = useSetupCommands(replies);
  const provider = session?.model?.provider ?? setup?.currentModel?.provider;
  const usage = setup?.usage;
  const loading = setup?.usageLoading === true || feedback('usage').pending;
  const latest = useRef({ usage, loading: setup?.usageLoading === true });
  latest.current = { usage, loading: setup?.usageLoading === true };
  const [askedAt, setAskedAt] = useState(() => Date.now());
  // At a limit's reset time the meter shows the new window at once, also while POMP answers; a fresh report is asked for when it does not.
  const clock = useUsageNow(usage);
  const now = Math.max(askedAt, clock);
  useEffect(() => {
    if (!connected || !provider || busy) return;
    if (claimUsageRequest(latest.current.usage, latest.current.loading, clock)) post('usage', (requestId) => ({ requestId, action: 'listUsage' }), undefined, USAGE_REPLY_TIMEOUT_MS);
  }, [clock]);

  useEffect(() => {
    if (!connected || !provider || busy) return;
    const ask = (): void => {
      const at = Date.now();
      setAskedAt(at);
      if (claimUsageRequest(latest.current.usage, latest.current.loading, at)) {
        post('usage', (requestId) => ({ requestId, action: 'listUsage' }), undefined, USAGE_REPLY_TIMEOUT_MS);
      }
    };
    ask();
    const timer = window.setInterval(ask, AUTO_REFRESH_MS);
    return () => window.clearInterval(timer);
  }, [connected, provider, busy, post]);

  // The usage popover closes on a click outside it or Escape (like the other composer popovers).
  const [open, setOpen] = useState(false);
  const rootRef = useRef<HTMLDivElement>(null);
  useEffect(() => {
    if (!open) return;
    const onPointerDown = (event: PointerEvent): void => {
      if (event.target instanceof Node && rootRef.current?.contains(event.target)) return;
      setOpen(false);
    };
    document.addEventListener('pointerdown', onPointerDown, true);
    return () => document.removeEventListener('pointerdown', onPointerDown, true);
  }, [open]);

  // One provider at a time: every provider with a report, in the host's order, starting at the current model's provider; the
  // arrows slide to the others (a new model's provider shows itself again).
  const reported = [...new Set((usage?.reports ?? []).map((report) => report.provider))];
  const providers = provider && !reported.includes(provider) ? [provider, ...reported] : reported;
  const [picked, setPicked] = useState<{ id: string; from: 'next' | 'prev' }>();
  useEffect(() => setPicked(undefined), [provider]);
  const shownProvider = picked && providers.includes(picked.id) ? picked.id : provider;
  const slide = picked && picked.id === shownProvider ? picked.from : undefined;
  const index = shownProvider ? providers.indexOf(shownProvider) : -1;
  const many = providers.length > 1;
  const step = (by: 1 | -1): void => {
    const next = providers[index + by];
    if (next) setPicked({ id: next, from: by > 0 ? 'next' : 'prev' });
  };

  // The line's room follows the pane's width, the provider name beside it and the limits' own widths: measured after every render
  // (the values only change with the usage) and whenever the meter's width changes.
  const [room, setRoom] = useState({ all: 2, withMore: 1 });
  const trackRef = useRef<HTMLDivElement>(null);
  const measure = useRef((): void => {
    const track = trackRef.current;
    const line = track?.querySelector<HTMLElement>('.quota-open');
    const sizes = track?.querySelector<HTMLElement>('.quota-measure');
    if (!line || !sizes) return;
    const next = limitRoom(line, sizes);
    setRoom((last) => (last.all === next.all && last.withMore === next.withMore ? last : next));
  }).current;
  useLayoutEffect(measure);
  const meterShown = !!provider && !!shownProvider;
  useLayoutEffect(() => {
    const track = trackRef.current;
    if (!track || typeof ResizeObserver === 'undefined') return;
    const observer = new ResizeObserver(measure);
    observer.observe(track);
    return () => observer.disconnect();
  }, [meterShown]);

  if (!provider || !shownProvider) return null;

  const current = shownProvider === provider;
  const modelId = session?.model?.id ?? setup?.currentModel?.id;
  const reports = (usage?.reports ?? []).filter((report) => report.provider === shownProvider);
  // 5 hours and weekly first (a window this subscription does not report is not shown), then the current model's own limit
  // (a Fable or Opus week, Spark...) and anything else (extra usage). Every subscription in detail: the popover.
  const slots = usageSlots(reports.flatMap((report) => report.limits), current ? modelId : undefined);
  const allItems: { key: string; name: string; limit: UsageLimit }[] = reports.length === 0 ? [] : [
    ...(slots.session ? [{ key: 'session', name: limitName(t, slots.session), limit: slots.session }] : []),
    ...(slots.weekly ? [{ key: 'weekly', name: limitName(t, slots.weekly), limit: slots.weekly }] : []),
    ...slots.models.filter((model) => model.current).map((model, index) => ({ key: `m${index}`, name: limitName(t, model.limit), limit: model.limit })),
    ...slots.others.map((limit, index) => ({ key: `o${index}`, name: limitName(t, limit), limit })),
  ];
  const items = allItems.length <= room.all ? allItems : allItems.slice(0, room.withMore); const moreLimits = allItems.length - items.length;
  const updated = usage?.fetchedAt !== undefined ? new Date(usage.fetchedAt).toLocaleTimeString(locale, { hour: '2-digit', minute: '2-digit', hourCycle: 'h23' }) : undefined;
  const refresh = (
    <button
      type="button"
      className="icon-btn meter-refresh"
      data-testid="quota-refresh"
      disabled={!connected || loading}
      title={t('meter.quotaRefresh')}
      aria-label={t('meter.quotaRefresh')}
      onClick={() => post('usage', (requestId) => ({ requestId, action: 'listUsage' }), undefined, USAGE_REPLY_TIMEOUT_MS)}
    >
      {loading ? <span className="spinner" aria-hidden="true" /> : <Icon name="restart" size={11} />}
    </button>
  );

  let body;
  if (items.length > 0) {
    body = (
      <button
        type="button"
        className="quota-limits quota-open"
        data-testid="quota-open"
        aria-haspopup="dialog"
        aria-expanded={open}
        title={updated ? t('meter.quotaUpdated', { time: updated }) : undefined}
        onClick={() => {
          // The popover names each subscription by its provider: the list may not be loaded yet (the Accounts tab loads it).
          if (!open && !setup?.providers && !feedback('providers').pending && connected) post('providers', (requestId) => ({ requestId, action: 'listProviders' }));
          setOpen(!open);
        }}
      >
        {items.map(({ key, name, limit }) => {
          const share = limitFraction(limit, now);
          return (
            <span className={`quota-limit usage-row ${limitStatus(limit, now)}`} key={key} data-testid="quota-limit">
              <span className="meter-name">{revealControls(name)}</span>
              <span className="usage-bar meter-bar" aria-hidden="true">
                <span className="usage-fill" style={{ width: `${Math.round((share ?? 0) * 100)}%` }} />
              </span>
              <span className="meter-value">{share === undefined ? '-' : `${Math.round(share * 100)}%`}</span>
            </span>
          );
        })}
        {moreLimits > 0 && <span className="meter-more" data-testid="quota-more">+{moreLimits}</span>}
      </button>
    );
  } else if (loading) {
    body = <span className="meter-muted">{t('meter.quotaChecking')}</span>;
  } else if (usage?.error) {
    body = <span className="meter-muted error" title={plainControls(usage.error)}>{t('meter.quotaFailed')}</span>;
  } else if (usage) {
    body = <span className="meter-muted">{t('meter.quotaNone')}</span>;
  } else {
    body = <span className="meter-muted">{t('meter.quota')}</span>;
  }

  return (
    <div
      className="meter quota-meter"
      ref={rootRef}
      data-testid="quota-meter"
      aria-label={t('meter.quota')}
      role="group"
      onKeyDown={(event) => {
        if (event.key !== 'Escape' || !open || event.nativeEvent.isComposing) return;
        event.preventDefault();
        event.stopPropagation();
        setOpen(false);
      }}
    >
      {many && (
        <button type="button" className="icon-btn quota-step" data-testid="quota-prev" disabled={index <= 0} title={t('meter.quotaPrev')} aria-label={t('meter.quotaPrev')} onClick={() => step(-1)}>
          <Icon name="chevronLeft" size={11} />
        </button>
      )}
      <div className="quota-track" ref={trackRef}>
        <div className="quota-measure" aria-hidden="true">
          {allItems.map(({ key, name, limit }) => {
            const share = limitFraction(limit, now);
            return (
              <span className="quota-limit" key={key}>
                <span className="meter-name">{revealControls(name)}</span>
                <span className="meter-bar" />
                <span className="meter-value">{share === undefined ? '-' : `${Math.round(share * 100)}%`}</span>
              </span>
            );
          })}
          <span className="meter-more">+{allItems.length}</span>
        </div>
        <div key={shownProvider} className={`quota-slide${slide ? ` from-${slide}` : ''}`} data-testid="quota-slide" data-provider={shownProvider}>
          {many && <span className="quota-provider" data-testid="quota-provider">{revealControls(providerLabel(shownProvider, providerName(setup?.providers, shownProvider)))}</span>}
          {body}
        </div>
      </div>
      {many && (
        <button type="button" className="icon-btn quota-step" data-testid="quota-next" disabled={index >= providers.length - 1} title={t('meter.quotaNext')} aria-label={t('meter.quotaNext')} onClick={() => step(1)}>
          <Icon name="chevron" size={11} />
        </button>
      )}
      {refresh}
      {open && (
        <div className="quota-popover" role="dialog" aria-label={t('meter.quotaTitle')} data-testid="quota-popover">
          <div className="quota-popover-head">{t('meter.quotaTitle')}</div>
          <UsageOverview setup={setup} provider={provider} modelId={modelId} now={now} locale={locale} />
          <div className="quota-popover-foot">
            {updated && <span className="hint">{t('meter.quotaUpdated', { time: updated })}</span>}
            {onManage && (
              <button type="button" className="btn subtle small" data-testid="quota-manage" onClick={() => { setOpen(false); onManage(); }}>
                {t('meter.quotaManage')}
              </button>
            )}
          </div>
        </div>
      )}
    </div>
  );
}

export const QuotaMeter = memo(QuotaMeterView);

// ---- Fast mode (next to the effort chip) ------------------------------------------------------------------------------

/** How long a fast-mode, computer-use or permission-mode toggle waits for its reply before it gives the button back. */
export const FAST_REPLY_TIMEOUT_MS = 15_000;

/** omp's fast mode: the priority service tier of the current model's family (omp's `/fast`). A toggle with a lightning bolt. */
function FastToggleView({ fast, connected }: { fast: FastMode | undefined; connected: boolean }) {
  const t = useT();
  const [pending, setPending] = useState<string | null>(null);
  const [error, setError] = useState<string | null>(null);

  useEffect(() => {
    if (pending === null) return;
    const off = onMessage((message) => {
      if (message.type !== 'session/fastResult' || message.payload.requestId !== pending) return;
      setPending(null);
      setError(message.payload.ok ? null : message.payload.error || t('fast.failed'));
    });
    const timer = window.setTimeout(() => {
      setPending(null);
      setError(t('setup.noReply'));
    }, FAST_REPLY_TIMEOUT_MS);
    return () => {
      off();
      window.clearTimeout(timer);
    };
  }, [pending, t]);

  useEffect(() => {
    if (!connected) setPending(null);
  }, [connected]);

  const available = fast?.available === true;
  const enabled = fast?.enabled === true;
  const title = error
    ? `${t('fast.failed')}: ${error}`
    : !available
      ? t('fast.unavailable')
      : enabled
        ? fast?.active
          ? t('fast.on')
          : `${t('fast.on')}\n${t('fast.requested')}`
        : t('fast.off');
  // Unavailable stays hoverable (aria-disabled, not disabled) so its tooltip can say why; WebView2 shows no tooltip on a disabled button.
  const inactive = !connected || !available || pending !== null;
  return (
    <button
      type="button"
      className={`icon-btn fast-toggle${error ? ' failed' : ''}`}
      data-testid="fast-toggle"
      aria-pressed={enabled}
      aria-label={t('fast.label')}
      aria-disabled={inactive}
      title={plainControls(title)}
      onClick={() => {
        if (inactive) return;
        const requestId = newId();
        setError(null);
        if (send('session/fast', { requestId, enabled: !enabled })) setPending(requestId);
        else setError(t('error.hostUnavailable'));
      }}
    >
      {pending !== null ? <span className="spinner" aria-hidden="true" /> : <Icon name="zap" size={13} />}
    </button>
  );
}

export const FastToggle = memo(FastToggleView);
