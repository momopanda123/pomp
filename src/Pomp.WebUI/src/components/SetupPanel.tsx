import { useEffect, useId, useRef } from 'react';
import { useT } from '../i18n';
import { send, type ModelInfo } from '../protocol';
import { EditorModelSection } from './EditorModelSection';
import { isProviderId, type SetupReply, type SetupState } from '../setup';
import { useSetupCommands } from '../setupCommands';
import { claimUsageRequest } from '../usage';
import { AccountsSection, type ListState } from './AccountsSection';
import { ApiKeySection } from './ApiKeySection';
import { Icon } from './Icon';
import { CONSOLE_TAG, LoginCard } from './LoginCard';
import { USAGE_REPLY_TIMEOUT_MS } from '../usage';
import { ProvidersSection } from './ProvidersSection';
import { revealControls } from './RevealControls';
import { useRefreshOnShow } from '../useRefreshOnShow';

interface SetupPanelProps {
  /** Latest `setup/state` of the host; null until the first one arrives. */
  setup: SetupState | null;
  /** Answers to this page's commands (see `AppState.setupReplies`). */
  replies: SetupReply[];
  sessionModel?: ModelInfo;
  /** The engine failed because omp has no model provider: this panel is the main content and cannot be closed. */
  required: boolean;
  /** omp is running: its models can be listed and switched. */
  connected: boolean;
  locale: string;
  /** A permission request or question is open above this panel: keep focus and clicks out of it. */
  inert: boolean;
  /** The account tab is a page, never a closable modal. */
}

/**
 * Accounts: connected accounts with their usage, adding an account (sign-in in the browser, no terminal), connecting
 * with an API key, and the editor-only model preference (chat model/thinking stay in the composer). It is the main content while omp cannot start for lack of a model provider
 * (first run: a short intro and popular providers as big buttons), and the account tab's full-height page otherwise.
 *
 * Secrets — an API key, the answer to a sign-in prompt — exist only in the inputs that take them and in the one command that
 * carries them; they are cleared as soon as that is posted and are never put into reducer state, storage, a log message or an
 * error text.
 */
export function SetupPanel({ setup, replies, sessionModel, required, connected, locale, inert }: SetupPanelProps) {
  const t = useT();
  const id = useId();
  const panelRef = useRef<HTMLElement>(null);
  const cmd = useSetupCommands(replies);
  const { post, feedback } = cmd;

  const loadProviders = (): void => {
    post('providers', (requestId) => ({ requestId, action: 'listProviders' }));
  };
  const loadUsage = (): void => {
    post('usage', (requestId) => ({ requestId, action: 'listUsage' }), undefined, USAGE_REPLY_TIMEOUT_MS);
  };

  // First show (mount only): ask the host for fresh state and the provider list (which is where the signed-in accounts come from),
  // and for usage unless it is recent. The host also posts state on its own; this makes sure the panel is current.
  useRefreshOnShow(true, () => {
    post('refresh', (requestId) => ({ requestId, action: 'refresh' }));
    loadProviders();
    // Accounts mounts on each activation. Guard effect replay without changing the host-driven first-run flow.
    if (!required && claimUsageRequest(setup?.usage, setup?.usageLoading === true, Date.now())) loadUsage();
  });

  // A new account has new limits: ask again once a sign-in has succeeded (not on a first run: the engine starts and this panel goes).
  const loginState = setup?.login.state;
  const previousLogin = useRef(loginState);
  useEffect(() => {
    if (previousLogin.current === 'running' && loginState === 'succeeded' && !required && !feedback('usage').pending && setup?.usageLoading !== true) loadUsage();
    previousLogin.current = loginState;
  }, [loginState]);

  // ---- Providers / accounts ----
  const providers = setup?.providers;
  const providersAnswer = feedback('providers');
  const providersError = setup?.providersError ?? providersAnswer.error ?? (providersAnswer.done && !providers ? t('setup.add.failed') : undefined);
  const listState: ListState = providers ? 'ready' : providersError ? 'error' : 'loading';
  const hasAccount = (providers?.some((provider) => provider.authenticated) ?? false) || (setup?.keys.length ?? 0) > 0;

  // ---- Usage ----
  const usageAnswer = feedback('usage');
  const usageBusy = setup?.usageLoading === true || usageAnswer.pending;

  // ---- Sign in ----
  const login = setup?.login;
  const loginAnswer = feedback('login');
  const loginBusy = login?.state === 'running' || loginAnswer.pending;
  const signIn = (provider: string): void => {
    if (!isProviderId(provider)) return;
    post('login', (requestId) => ({ requestId, action: 'login', provider }), provider);
  };
  const signInConsole = (provider?: string): void => {
    post('login', (requestId) => (provider ? { requestId, action: 'loginConsole', provider } : { requestId, action: 'loginConsole' }), CONSOLE_TAG);
  };

  const refreshAnswer = feedback('refresh');
  const showAccounts = !required || hasAccount;
  const accountsSection = setup && showAccounts ? <AccountsSection setup={setup} cmd={cmd} state={listState} providersError={providersError}
    usageBusy={usageBusy} usageError={usageAnswer.error} locale={locale} inert={inert}
    onRefreshUsage={() => { if (!usageBusy) loadUsage(); }} onRetryProviders={loadProviders} /> : null;
  const editorSection = setup ? <EditorModelSection setup={setup} replies={replies} sessionModel={sessionModel} connected={connected} inert={inert} defaultCollapsed={required} /> : null;

  return (
    <div className={required ? 'setup-page required' : 'setup-page'} inert={inert || undefined}>
      <section className="setup-panel" ref={panelRef} aria-labelledby={`${id}-title`} tabIndex={-1} data-testid="setup-panel">
        {/* The tab already says 계정: the title row is only seen on the first-run page (it still names the panel for screen readers). */}
        <div className={required ? 'setup-head' : 'setup-head sr-only'}>
          <span id={`${id}-title`} className="setup-title">
            {required ? t('setup.titleRequired') : t('setup.title')}
          </span>
        </div>

        <div className="setup-body">
          {setup?.restartRecommended && (
            <div className="setup-notice" role="status" data-testid="setup-restart">
              <Icon name="info" size={13} />
              <span className="setup-notice-text">{t('setup.restartNotice')}</span>
              <button type="button" className="btn primary" disabled={!connected || inert} onClick={() => send('bridge/restart', {})}>
                <Icon name="restart" size={12} />
                {t('setup.restart')}
              </button>
            </div>
          )}

          {!setup && (
            <div className="setup-loading" role="status">
              {refreshAnswer.error ? <Icon name="error" size={12} /> : <span className="spinner" aria-hidden="true" />}
              <span className="setup-running-text">{refreshAnswer.error ? revealControls(refreshAnswer.error) : t('setup.loading')}</span>
              {!refreshAnswer.pending && (
                <button type="button" className="btn subtle" onClick={() => post('refresh', (requestId) => ({ requestId, action: 'refresh' }))}>
                  {t('setup.reload')}
                </button>
              )}
            </div>
          )}

          {setup && login && (
            <>
              {!required && <>{accountsSection}{editorSection}</>}

              <ProvidersSection
                providers={providers}
                state={listState}
                providersError={providersError}
                required={required}
                locale={locale}
                busy={loginBusy}
                inert={inert}
                onSignIn={signIn}
                onConsole={() => signInConsole()}
                onRetry={loadProviders}
              />

              {!required && <LoginCard login={login} providers={providers} cmd={cmd} inert={inert} onSignIn={signIn} onConsole={signInConsole} />}

              <ApiKeySection setup={setup} cmd={cmd} locale={locale} inert={inert} defaultOpen={!hasAccount && (providers !== undefined || required)} />

              {required && <><LoginCard login={login} providers={providers} cmd={cmd} inert={inert} onSignIn={signIn} onConsole={signInConsole} />{accountsSection}{editorSection}</>}

            </>
          )}
        </div>
      </section>
    </div>
  );
}
