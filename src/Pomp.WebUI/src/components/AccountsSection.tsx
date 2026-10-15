import { useEffect, useId, useMemo, useRef, useState } from 'react';
import { useUsageNow } from '../hooks';
import { useT } from '../i18n';
import type { UsageLimit, UsageReport } from '../protocol';
import { providerLabel, providerName, type AccountKind, type SetupState } from '../setup';
import { LOGOUT_REPLY_TIMEOUT_MS, type SetupCommands } from '../setupCommands';
import { Icon } from './Icon';
import { Note } from './Note';
import { plainControls, revealControls } from './RevealControls';
import { LimitSummary, SubscriptionUsage } from './SubscriptionUsage';
import { useRememberedOpen, ViewSection } from './ViewSection';
import { tightestLimit } from '../usage';
import { Badge } from './ui';

/** "구독" (an engine sign-in) or "API 키" (a saved or environment key): the only thing said about how an account is connected. */
export function AccountKindBadge({ kind, testId }: { kind: AccountKind; testId?: string }) {
  const t = useT();
  return <Badge tone={kind === 'subscription' ? 'info' : 'neutral'} icon={kind === 'apiKey' ? 'key' : undefined} testId={testId}>{t(kind === 'subscription' ? 'ui.sub' : 'ui.apiKey')}</Badge>;
}

export type ListState = 'loading' | 'ready' | 'error';

interface AccountsSectionProps {
  setup: SetupState;
  /** The account panel's commands: a sign-out is posted through it and its answer is read from it. */
  cmd: SetupCommands;
  /** Whether the provider list (which says who is signed in) is loading, ready or failed. */
  state: ListState;
  providersError?: string;
  /** A usage request is running (the host's flag, or this page's command waiting for its answer). */
  usageBusy: boolean;
  /** The answer to the page's own `listUsage` was an error. */
  usageError?: string;
  locale: string;
  inert: boolean;
  onRefreshUsage: () => void;
  onRetryProviders: () => void;
}

interface Account {
  id: string;
  /** The tab's display name (providerLabel). */
  name: string;
  kind: AccountKind;
  /** Signed in as far as the provider list knows: a row that only has usage numbers has nothing to sign out of. */
  authenticated: boolean;
  reports: UsageReport[];
}

/**
 * Sign out of a provider. "Sign out" asks first, inline under the row's head (Cancel has the focus, Esc closes it), then posts
 * `logout`. The host removes every stored sign-in of the provider (omp cannot tell its accounts apart) and answers with the provider
 * list as it is afterwards: a provider that is authenticated another way (an API key, an environment variable) is still in it.
 */
function AccountLogout({ account, cmd, disabled }: { account: Account; cmd: SetupCommands; disabled: boolean }) {
  const t = useT();
  const [asking, setAsking] = useState(false);
  const opener = useRef<HTMLButtonElement>(null);
  const cancel = useRef<HTMLButtonElement>(null);
  const answer = cmd.sent.logout?.tag === account.id ? cmd.feedback('logout') : undefined;
  const pending = answer?.pending === true;
  // One sign-out at a time, whichever row asked for it.
  const busy = cmd.feedback('logout').pending;
  const title = t('setup.accounts.logoutTitle', { name: account.name });

  useEffect(() => {
    if (asking) cancel.current?.focus();
  }, [asking]);

  // A disabled button cannot keep the focus: it comes back when the answer is in, this row is still there and the user has not gone elsewhere.
  const wasPending = useRef(false);
  useEffect(() => {
    if (wasPending.current && !pending && document.hasFocus() && (document.activeElement === document.body || document.activeElement === opener.current)) opener.current?.focus();
    wasPending.current = pending;
  }, [pending]);

  const close = (): void => {
    setAsking(false);
    opener.current?.focus();
  };
  const confirm = (): void => {
    setAsking(false);
    cmd.post('logout', (requestId) => ({ requestId, action: 'logout', provider: account.id }), account.id, LOGOUT_REPLY_TIMEOUT_MS);
  };

  return (
    <>
      <button
        ref={opener}
        type="button"
        className="btn subtle small acct-logout"
        data-testid={`logout-${account.id}`}
        disabled={disabled || busy}
        aria-busy={pending || undefined}
        aria-expanded={asking}
        aria-label={pending ? undefined : plainControls(title)}
        onClick={() => setAsking(true)}
      >
        {pending ? (
          <>
            <span className="spinner" aria-hidden="true" />
            {t('setup.accounts.loggingOut')}
          </>
        ) : (
          t('setup.accounts.logout')
        )}
      </button>
      {asking && (
        <div
          className="acct-confirm"
          role="group"
          aria-label={plainControls(title)}
          data-testid={`logout-confirm-${account.id}`}
          // A click on its text leaves the focus inside, so Esc still closes it (instead of moving to the composer).
          tabIndex={-1}
          onKeyDown={(event) => {
            if (event.key === 'Escape' && !event.nativeEvent.isComposing) {
              event.preventDefault();
              event.stopPropagation();
              close();
            }
          }}
        >
          <span className="acct-confirm-title">{revealControls(title)}</span>
          <span className="acct-confirm-question">{t('setup.accounts.logoutQuestion')}</span>
          <div className="ui-form-actions">
            <button ref={cancel} type="button" className="btn subtle small" data-testid={`logout-cancel-${account.id}`} onClick={close}>
              {t('dialog.cancel')}
            </button>
            <button type="button" className="btn small danger" data-testid={`logout-yes-${account.id}`} disabled={disabled || busy} onClick={confirm}>
              {t('setup.accounts.logout')}
            </button>
          </div>
        </div>
      )}
      {answer?.error && <div className="acct-note"><Note tone="error" text={answer.error} onDismiss={() => cmd.clear('logout')} /></div>}
      {answer?.done && (
        <span className="hint warn acct-note" role="status" data-testid={`logout-still-${account.id}`}>
          {t('setup.accounts.stillSignedIn')}
        </span>
      )}
    </>
  );
}

/**
 * The signed-in accounts (providers with `authenticated`), each with its usage: a labelled bar per limit and the local time it
 * resets, and a "Sign out". Usage that the host reports for a provider the list does not mark as signed in (an API key, an
 * environment variable) is shown too: the user should see every limit that applies.
 */
/**
 * One connected account: the head (name, kind, address, sign-out) opens and closes its usage like a section. The current model's
 * provider is open until the viewer chooses; a closed account names its tightest limit in the head.
 */
function AccountItem({ account, cmd, disabled, current, modelId, loading, now, locale }: { account: Account; cmd: SetupCommands; disabled: boolean; current: boolean; modelId: string | undefined; loading: boolean; now: number; locale: string }) {
  const t = useT(); const bodyId = useId();
  const [open, setOpen] = useRememberedOpen(`account:${account.id}`, current);
  const emails = [...new Set(account.reports.map((report) => report.account).filter((value): value is string => !!value))];
  const hasBody = account.reports.length > 0 || loading;
  const top: UsageLimit | undefined = tightestLimit(account.reports, account.id, modelId, now)?.limit;
  return (
    <li className="acct" data-testid={`account-${account.id}`}>
      <div className="acct-head">
        <button type="button" className="acct-toggle" data-testid={`account-toggle-${account.id}`} aria-expanded={hasBody ? open : undefined} aria-controls={hasBody ? bodyId : undefined}
          disabled={!hasBody} title={t(open ? 'section.collapse' : 'section.expand')} onClick={() => setOpen(!open)}>
          {hasBody && <Icon name={open ? 'chevronDown' : 'chevron'} size={12} />}
          <span className="acct-who">
            <span className="acct-title">
              <span className="acct-name">{revealControls(account.name)}</span>
              <AccountKindBadge kind={account.kind} testId={`account-kind-${account.id}`} />
            </span>
            {emails.length === 1 && <span className="acct-label" data-testid="account-label">{revealControls(emails[0])}</span>}
          </span>
          {!open && top && <LimitSummary limit={top} now={now} testId={`account-summary-${account.id}`} />}
          {!open && account.reports.length === 0 && loading && <span className="acct-summary usage-row unknown" role="status"><span className="acct-summary-name">{t('setup.usage.loading')}</span></span>}
        </button>
        {account.authenticated && <AccountLogout account={account} cmd={cmd} disabled={disabled} />}
      </div>
      {open && hasBody && (
        <div className="acct-body" id={bodyId}>
          {account.reports.length === 0
            ? <span className="acct-usage-status ui-muted" role="status">{t('setup.usage.loading')}</span>
            : account.reports.map((report, index) => (
              <div className="acct-report" key={`${report.account ?? ''}:${index}`}>
                {emails.length > 1 && report.account && <div className="acct-label" data-testid="account-label">{revealControls(report.account)}</div>}
                {report.limits.length > 0 && <SubscriptionUsage limits={report.limits} modelId={modelId} now={now} locale={locale} />}
              </div>
            ))}
        </div>
      )}
    </li>
  );
}

export function AccountsSection({ setup, cmd, state, providersError, usageBusy, usageError, locale, inert, onRefreshUsage, onRetryProviders }: AccountsSectionProps) {
  const t = useT();
  const titleId = useId();
  const usage = setup.usage;
  const providers = setup.providers;
  const now = useUsageNow(usage);
  const currentProvider = setup.currentModel?.provider;
  const currentModelId = setup.currentModel?.id;
  const loginState = setup.login.state;

  const accounts = useMemo<Account[]>(() => {
    // Until the provider list arrives a usage report has only the provider's internal id: wait instead of showing that id.
    const reports = providers ? usage?.reports ?? [] : [];
    const list: Account[] = (providers ?? [])
      .filter((provider) => provider.authenticated)
      .map((provider) => ({ id: provider.id, name: providerLabel(provider.id, provider.name), kind: 'subscription' as const, authenticated: true, reports: reports.filter((report) => report.provider === provider.id) }));
    for (const report of reports) {
      if (!list.some((account) => account.id === report.provider)) {
        // Usage of a provider nobody signed in to comes from an API key (saved here or in the environment).
        list.push({ id: report.provider, name: providerLabel(report.provider, providerName(providers, report.provider)), kind: 'apiKey', authenticated: false, reports: reports.filter((other) => other.provider === report.provider) });
      }
    }
    return list;
  }, [providers, usage]);

  // A new sign-in retires the note of an earlier sign-out (the provider may be signed in again).
  useEffect(() => {
    if (loginState === 'running') cmd.clear('logout');
  }, [loginState]);

  const updated = usage?.fetchedAt !== undefined ? new Date(usage.fetchedAt).toLocaleTimeString(locale, { hour: '2-digit', minute: '2-digit', hourCycle: 'h23' }) : undefined;
  // The host reports a failed fetch in the usage itself and, for this page's own command, as the answer: show the text once.
  const usageFailure = usage?.error ?? usageError;

  return (
    <ViewSection sectionKey="setup-accounts" className="setup-section acct-section" headingId={titleId} aria-labelledby={titleId} data-testid="setup-accounts" title={t('setup.accounts.title')} status={accounts.length}
      bodyClassName="ui-flush" actions={<button
          type="button"
          className="icon-btn"
          data-testid="usage-refresh"
          disabled={inert || usageBusy}
          aria-busy={usageBusy || undefined}
          title={t('setup.usage.refresh')}
          aria-label={t('setup.usage.refresh')}
          onClick={onRefreshUsage}
        >
          {usageBusy ? <span className="spinner" aria-hidden="true" /> : <Icon name="restart" size={13} />}
        </button>}>

      {state === 'loading' && (
        <div className="setup-loading acct-inset" role="status" data-testid="accounts-loading">
          <span className="spinner" aria-hidden="true" />
          <span className="setup-running-text">{t('setup.accounts.loading')}</span>
        </div>
      )}

      {state === 'error' && (
        <div className="setup-stack acct-inset" data-testid="accounts-error">
          <Note tone="error" text={t('setup.accounts.failed')} detail={providersError} />
          <div className="setup-actions">
            <button type="button" className="btn subtle" data-testid="providers-retry" disabled={inert} onClick={onRetryProviders}>
              {t('setup.retry')}
            </button>
          </div>
        </div>
      )}

      {state === 'ready' && accounts.length === 0 && <div className="ui-empty-rows acct-empty" aria-hidden="true" />}

      {accounts.length > 0 && (
        <ul className="acct-list" data-testid="account-list">
          {accounts.map((account) => (
            <AccountItem key={account.id} account={account} cmd={cmd} disabled={inert || loginState === 'running'} current={account.id === currentProvider}
              modelId={account.id === currentProvider ? currentModelId : undefined} loading={usageBusy && !usage} now={now} locale={locale} />
          ))}
        </ul>
      )}

      {accounts.length > 0 && (usageBusy && usage || !usageBusy && updated) && (
        <div className="ui-card-foot acct-foot">
          {usageBusy && usage ? (
            <span className="hint usage-status" role="status" data-testid="usage-loading">
              <span className="spinner" aria-hidden="true" />
              {t('setup.usage.loading')}
            </span>
          ) : (
            <span className="hint usage-status" data-testid="usage-updated">
              <Icon name="history" size={12} />
              {t('setup.usage.updated', { time: updated! })}
            </span>
          )}
        </div>
      )}
      {usageFailure && !usageBusy && <div className="acct-inset"><Note tone="error" text={t('setup.usage.failed')} detail={usageFailure} /></div>}
    </ViewSection>
  );
}
