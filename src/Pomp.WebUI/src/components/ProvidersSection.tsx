import { useId, useMemo, useState } from 'react';
import { useT } from '../i18n';
import type { SetupProvider } from '../protocol';
import { isProviderId, POPULAR_PROVIDERS, providerLabel, providerMethod } from '../setup';
import { Icon } from './Icon';
import { Note } from './Note';
import { plainControls, revealControls } from './RevealControls';
import type { ListState } from './AccountsSection';
import { ViewSection } from './ViewSection';
import { Badge, Pager, SearchBox, Toolbar, usePaged } from './ui';

interface ProvidersSectionProps {
  providers: readonly SetupProvider[] | undefined;
  state: ListState;
  providersError?: string;
  /** The first run: adding an account is all the page is for. */
  required: boolean;
  /** A sign-in is starting or running: one at a time. */
  busy: boolean;
  inert: boolean;
  locale: string;
  onSignIn: (provider: string) => void;
  onConsole: () => void;
  onRetry: () => void;
}

const collator = new Intl.Collator(undefined, { sensitivity: 'base', numeric: true });

/** Whether a provider's tile can start a sign-in. */
const canSignIn = (provider: SetupProvider, busy: boolean, inert: boolean): boolean => !busy && !inert && provider.available && isProviderId(provider.id);

/**
 * "Add an account": a grid of provider tiles (the popular ones first), searchable and paged. Every tile signs in to a subscription plan, so
 * the section title says so once; a tile shows the provider's name and whether it is connected already, and a click starts its sign-in.
 */
export function ProvidersSection({ providers, state, providersError, required, busy, inert, locale, onSignIn, onConsole, onRetry }: ProvidersSectionProps) {
  const t = useT();
  const titleId = useId();
  const [query, setQuery] = useState('');

  // Popular providers first, then the rest by the name the tile shows; the search matches that name, the host's name and the id.
  const ordered = useMemo(() => {
    const words = query.toLowerCase().split(/\s+/).filter(Boolean);
    const rank = (provider: SetupProvider) => { const index = POPULAR_PROVIDERS.indexOf(provider.id); return index < 0 ? POPULAR_PROVIDERS.length : index; };
    return (providers ?? [])
      .map((provider) => ({ provider, label: providerLabel(provider.id, provider.name) }))
      .filter(({ provider, label }) => { const text = `${label} ${provider.name} ${provider.id}`.toLowerCase(); return words.every((word) => text.includes(word)); })
      .sort((a, b) => rank(a.provider) - rank(b.provider) || collator.compare(a.label, b.label) || collator.compare(a.provider.id, b.provider.id))
      .map(({ provider }) => provider);
  }, [providers, query]);
  const paged = usePaged(ordered, query);
  const total = providers?.length ?? 0;

  return (
    <ViewSection sectionKey="setup-add" className={`setup-section prov-section${required ? ' required' : ''}`} title={<span className="prov-add-title">{t('setup.add.title')}<Badge tone="info" testId="setup-add-kind">{t('setup.add.kind')}</Badge></span>} headingId={titleId} aria-labelledby={titleId} data-testid="setup-add"
      bodyClassName="ui-flush">
      {state === 'loading' && (
        <div className="setup-loading acct-inset" role="status" data-testid="providers-loading">
          <span className="spinner" aria-hidden="true" />
          <span className="setup-running-text">{t('setup.add.loading')}</span>
        </div>
      )}

      {state === 'error' && (
        <div className="setup-stack acct-inset" data-testid="providers-error">
          <Note tone="error" text={t('setup.add.failed')} detail={providersError} />
          <div className="setup-actions">
            <button type="button" className="btn subtle" data-testid="providers-retry-add" disabled={inert} onClick={onRetry}>
              {t('setup.retry')}
            </button>
            <button type="button" className="btn subtle" data-testid="providers-console" disabled={inert || busy} onClick={onConsole}>
              <Icon name="terminal" size={12} />
              {t('setup.add.console')}
            </button>
          </div>
        </div>
      )}

      {state === 'ready' && total === 0 && (
        <div className="setup-stack acct-inset" data-testid="providers-empty">
          <div className="setup-actions">
            <button type="button" className="btn subtle" data-testid="providers-console" disabled={inert || busy} onClick={onConsole}>
              <Icon name="terminal" size={12} />
              {t('setup.add.console')}
            </button>
          </div>
        </div>
      )}

      {total > 0 && (
        <>
          <Toolbar>
            <SearchBox value={query} onChange={setQuery} label={t('setup.add.search')} testId="provider-search" />
          </Toolbar>
          <div className="prov-grid" data-testid="provider-list" role="list" aria-label={t('setup.add.title')}>
            {paged.slice.map((provider) => {
              const label = providerLabel(provider.id, provider.name);
              const method = providerMethod(provider.id, locale);
              const popular = POPULAR_PROVIDERS.includes(provider.id);
              return (
                <div role="listitem" key={provider.id} className="prov-cell">
                  <button
                    type="button"
                    className={`prov-tile${provider.authenticated ? ' connected' : ''}`}
                    data-testid={popular ? `login-${provider.id}` : `provider-${provider.id}`}
                    data-provider={provider.id}
                    disabled={!canSignIn(provider, busy, inert)}
                    title={plainControls(`${label} (${provider.id})`)}
                    onClick={() => onSignIn(provider.id)}
                  >
                    <span className="prov-tile-text">
                      <span className="prov-tile-name">{revealControls(label)}</span>
                      {method && <span className="prov-tile-method">{revealControls(method)}</span>}
                    </span>
                    {!provider.available && <Badge>{t('setup.add.unavailable')}</Badge>}
                    {provider.authenticated && (
                      <span className="prov-tile-check" data-testid="provider-connected" title={t('setup.accounts.connected')}>
                        <Icon name="check" size={13} />
                        <span className="sr-only">{t('setup.accounts.connected')}</span>
                      </span>
                    )}
                  </button>
                </div>
              );
            })}
          </div>
          {ordered.length === 0 && <div className="ui-empty-rows prov-empty" aria-hidden="true" />}
          <Pager paged={paged} testId="provider-pager" />
        </>
      )}
    </ViewSection>
  );
}
