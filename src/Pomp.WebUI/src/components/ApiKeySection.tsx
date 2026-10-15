import { useEffect, useId, useMemo, useRef, useState, type FormEvent } from 'react';
import { useT } from '../i18n';
import { apiKeyProblem, formatSavedDate, localAddressProblem, providerLabel, type SetupState } from '../setup';
import type { SetupLocalServer, SetupSavedKey } from '../protocol';
import type { SetupCommands } from '../setupCommands';
import { Icon } from './Icon';
import { Note } from './Note';
import { revealControls } from './RevealControls';
import { AccountKindBadge } from './AccountsSection';
import { ViewSection } from './ViewSection';
import { Actions, Badge, Cell, DataTable, Field, FormActions, IconButton, Pager, Row, SearchBox, Toolbar, matches, refocus, usePaged } from './ui';

interface ApiKeySectionProps {
  setup: SetupState;
  cmd: SetupCommands;
  locale: string;
  inert: boolean;
  /** Whether the add form is open until the user decides (open on a first run without any account, closed otherwise). */
  defaultOpen: boolean;
}

/** The provider choice that saves a model server the user runs (any OpenAI-compatible server) instead of an API key. */
const LOCAL = '__local';
const SAVED_PAGE = 10;

type SavedRow = { kind: 'key'; name: string; saved: SetupSavedKey } | { kind: 'local'; server: SetupLocalServer };

/**
 * API keys and local model servers: the saved keys and servers as a table (provider or server, state, edit/delete) and "Add key", an
 * in-page form. For a provider it takes the key; for "local model server" a name, the server's address and an optional key. A key exists
 * only in this component's input state and in the one `saveKey`/`saveLocalServer` command; it is cleared as soon as that is posted and is
 * never put into reducer state, storage, a log message or an error text.
 */
export function ApiKeySection({ setup, cmd, locale, inert, defaultOpen }: ApiKeySectionProps) {
  const t = useT();
  const ko = locale.toLowerCase().startsWith('ko');
  const titleId = useId();
  const keyRef = useRef<HTMLInputElement>(null);
  const nameRef = useRef<HTMLInputElement>(null);
  const [userOpen, setUserOpen] = useState<boolean | undefined>(undefined);
  const [apiKey, setApiKey] = useState('');
  const [providerId, setProviderId] = useState('');
  const [serverName, setServerName] = useState('');
  const [address, setAddress] = useState('');
  /** The local server being edited (its id), or undefined for a new entry. */
  const [editingId, setEditingId] = useState<string | undefined>(undefined);
  // Only a deliberate open or close keeps the form's state across account snapshots.
  const open = userOpen ?? defaultOpen;

  const keyProviders = setup.apiKeyProviders;
  const local = providerId === LOCAL;
  const keyProvider = keyProviders.find((provider) => provider.id === providerId) ?? keyProviders[0];
  const trimmedKey = apiKey.trim();
  const problem = apiKeyProblem(trimmedKey);
  const save = cmd.feedback(local ? 'saveLocalServer' : 'saveKey');
  const name = serverName.trim();
  const nameTaken = setup.localServers.some((server) => server.id !== editingId && server.name.toLowerCase() === name.toLowerCase());
  const addressBad = address.trim() !== '' && localAddressProblem(address.trim());
  const canSave = !save.pending && !inert && (local
    ? name !== '' && !nameTaken && address.trim() !== '' && !addressBad && (trimmedKey === '' || problem === null)
    : keyProvider !== undefined && problem === null);
  const keyError = apiKey === '' ? undefined : problem === 'chars' ? t('setup.key.chars') : problem === 'long' ? t('setup.key.long') : undefined;

  const changeKey = (value: string): void => {
    // Typing a new key retires the previous answer ("Saved" / an error) unless a save is still waiting for its answer.
    if (apiKey === '' && value !== '' && cmd.sent.saveKey && !save.pending) cmd.clear('saveKey');
    setApiKey(value);
  };

  const saveKey = (event: FormEvent): void => {
    event.preventDefault();
    if (!canSave) return;
    const key = trimmedKey;
    // The key leaves with the command and is cleared at once; if posting failed the user keeps what they typed. The form stays open for the answer.
    const posted = local
      ? cmd.post('saveLocalServer', (requestId) => ({ requestId, action: 'saveLocalServer', ...(editingId ? { id: editingId } : {}), name, address: address.trim(), ...(key ? { key } : {}) }))
      : keyProvider !== undefined && cmd.post('saveKey', (requestId) => ({ requestId, action: 'saveKey', provider: keyProvider.id, key }));
    if (posted) setApiKey('');
  };

  const removeKey = (provider: string): void => {
    cmd.post('removeKey', (requestId) => ({ requestId, action: 'removeKey', provider }), provider);
  };
  const removeServer = (id: string): void => {
    cmd.post('removeLocalServer', (requestId) => ({ requestId, action: 'removeLocalServer', id }), id);
  };

  const openForm = (): void => {
    for (const slot of ['saveKey', 'saveLocalServer'] as const) { const answer = cmd.feedback(slot); if (answer.done || answer.error) cmd.clear(slot); }
    setUserOpen(true);
    window.setTimeout(() => (providerId === LOCAL ? nameRef : keyRef).current?.focus(), 0);
  };
  const editServer = (id: string): void => {
    const server = setup.localServers.find((item) => item.id === id);
    if (!server) return;
    setProviderId(LOCAL); setEditingId(id); setServerName(server.name); setAddress(server.address); setApiKey('');
    openForm();
    window.setTimeout(() => nameRef.current?.focus(), 0);
  };
  const closeForm = (): void => {
    setApiKey(''); // a half-typed key is not kept behind a closed form
    setEditingId(undefined); setServerName(''); setAddress('');
    setUserOpen(false);
  };
  const changeProvider = (value: string): void => {
    setProviderId(value);
    setEditingId(undefined);
  };

  // The answer decides: a saved key or server closes the form (its "Saved" note shows) and the focus goes to "Add key"; a refusal keeps
  // the form open with the reason next to Save and the focus in the field to fill again (the form was locked while it waited).
  const waited = useRef(false);
  useEffect(() => {
    if (save.pending) { waited.current = true; return; }
    if (!waited.current) return;
    waited.current = false;
    if (save.done) { closeForm(); refocus('key-toggle'); }
    else if (save.error) window.setTimeout(() => (local ? nameRef : keyRef).current?.focus(), 0);
  }, [save.pending, save.done, save.error]);

  const savedCount = setup.keys.length + setup.localServers.length;
  const [query, setQuery] = useState('');
  const savedRows = useMemo<SavedRow[]>(() => [
    ...setup.keys.map((saved): SavedRow => ({ kind: 'key', saved, name: providerLabel(saved.provider, setup.apiKeyProviders.find((provider) => provider.id === saved.provider)?.label) })),
    ...setup.localServers.map((server): SavedRow => ({ kind: 'local', server })),
  ], [setup.keys, setup.localServers, setup.apiKeyProviders]);
  const shownRows = useMemo(() => savedRows.filter((row) => row.kind === 'key' ? matches(query, row.name, row.saved.envVar) : matches(query, row.server.name, row.server.address)), [savedRows, query]);
  const paged = usePaged(shownRows, query, SAVED_PAGE);
  const saved = cmd.feedback('saveKey').done || cmd.feedback('saveLocalServer').done;

  return (
    <ViewSection sectionKey="setup-key" className="setup-section key-section" title={t('setup.key.title')} headingId={titleId} aria-labelledby={titleId} data-testid="setup-key"
      status={savedCount} bodyClassName="ui-flush"
      actions={<button type="button" className="btn small" data-testid="key-toggle" aria-expanded={open} disabled={inert || open} onClick={openForm}>
        <Icon name="plus" size={12} />{ko ? '키 추가' : 'Add key'}
      </button>}>

      {open && (keyProvider || local) && (
        <form className="ui-form key-form" autoComplete="off" noValidate onSubmit={saveKey} data-testid="key-form"
          onKeyDown={(event) => {
            if (event.key === 'Escape' && !event.nativeEvent.isComposing && !save.pending) { event.preventDefault(); event.stopPropagation(); closeForm(); }
          }}>
          <Field label={t('setup.key.provider')}>{(id) => (
            <select id={id} data-testid="key-provider" value={local ? LOCAL : keyProvider?.id} disabled={save.pending || editingId !== undefined} onChange={(event) => changeProvider(event.target.value)}>
              {keyProviders.map((provider) => <option key={provider.id} value={provider.id}>{providerLabel(provider.id, provider.label)}</option>)}
              <option value={LOCAL}>{t('setup.local.option')}</option>
            </select>
          )}</Field>
          {local && <>
            <Field label={t('setup.local.name')} error={nameTaken ? t('setup.local.nameTaken') : undefined}>{(id) => (
              <input id={id} ref={nameRef} type="text" data-testid="local-name" maxLength={40} autoComplete="off" spellCheck={false} value={serverName}
                disabled={save.pending} aria-invalid={nameTaken || undefined} onChange={(event) => setServerName(event.target.value)} />
            )}</Field>
            <Field label={t('setup.local.address')} error={addressBad ? t('setup.local.addressInvalid') : undefined}>{(id) => (
              <input id={id} type="url" className="ui-mono" data-testid="local-address" maxLength={2048} autoComplete="off" autoCapitalize="off" spellCheck={false} value={address}
                disabled={save.pending} aria-invalid={addressBad || undefined} onChange={(event) => setAddress(event.target.value)} />
            )}</Field>
          </>}
          <Field label={t('setup.key.label')} error={keyError}>{(id) => (
            <input
              id={id}
              ref={keyRef}
              type="password"
              className="ui-mono"
              data-testid="key-input"
              autoComplete="off"
              autoCapitalize="off"
              spellCheck={false}
              value={apiKey}
              disabled={save.pending}
              aria-invalid={keyError !== undefined || undefined}
              onChange={(event) => changeKey(event.target.value)}
            />
          )}</Field>
          {save.error && <Note tone="error" text={save.error} />}
          <FormActions>
            <button type="button" className="btn" data-testid="key-cancel" disabled={save.pending} onClick={closeForm}>{t('dialog.cancel')}</button>
            <button type="submit" className="btn primary" data-testid="key-save" disabled={!canSave}>
              {save.pending ? t('setup.key.saving') : t('setup.key.save')}
            </button>
          </FormActions>
        </form>
      )}

      {savedCount > SAVED_PAGE && <Toolbar><SearchBox value={query} onChange={setQuery} testId="saved-keys-search" /></Toolbar>}
      <DataTable columns="minmax(0, 1fr) auto auto" label={t('setup.key.title')} testId="saved-keys" empty={!shownRows.length} emptyRows={1}
        head={[t('setup.key.provider'), t('ui.col.status'), '']}>
        {paged.slice.map((row) => {
          if (row.kind === 'local') return localRow(row.server);
          const { saved, name } = row;
          const date = formatSavedDate(saved.savedUtc, locale);
          const removal = cmd.sent.removeKey?.tag === saved.provider ? cmd.feedback('removeKey') : undefined;
          return (
            <Row key={saved.provider} testId={`saved-key-${saved.provider}`}>
              <Cell className="key-provider-cell">
                <span className="key-provider-text">
                  <span className="key-provider-name"><strong>{revealControls(name)}</strong><AccountKindBadge kind="apiKey" /></span>
                  <span className="ui-sub ui-mono" title={saved.envVar}>{revealControls([saved.envVar, date].filter(Boolean).join(' · '))}</span>
                  {removal?.error && <span className="ui-sub ui-err" role="alert">{revealControls(removal.error)}</span>}
                </span>
              </Cell>
              <Cell><Badge tone="ok" icon="check">{ko ? '저장됨' : 'Saved'}</Badge></Cell>
              <Actions>
                <IconButton icon="trash" tone="danger" testId={`remove-key-${saved.provider}`} label={t('setup.keys.removeFor', { name })}
                  disabled={inert || removal?.pending} onClick={() => removeKey(saved.provider)} />
              </Actions>
            </Row>
          );
        })}
      </DataTable>
      <Pager paged={paged} testId="saved-keys-pager" />
      {saved && !open && <div className="acct-inset"><Note tone="ok" text={t('setup.key.saved')} onDismiss={() => { cmd.clear('saveKey'); cmd.clear('saveLocalServer'); }} /></div>}
    </ViewSection>
  );

  /** A saved local model server: name, address, edit and delete. */
  function localRow(server: SetupLocalServer) {
    const date = formatSavedDate(server.savedUtc, locale);
    const removal = cmd.sent.removeLocalServer?.tag === server.id ? cmd.feedback('removeLocalServer') : undefined;
    return (
      <Row key={server.id} testId={`saved-local-${server.id}`}>
        <Cell className="key-provider-cell">
          <span className="key-provider-text">
            <span className="key-provider-name"><strong>{revealControls(server.name)}</strong><Badge>{t('setup.local.badge')}</Badge>{server.hasKey && <AccountKindBadge kind="apiKey" />}</span>
            <span className="ui-sub ui-mono" title={server.address}>{revealControls([server.address, date].filter(Boolean).join(' · '))}</span>
            {removal?.error && <span className="ui-sub ui-err" role="alert">{revealControls(removal.error)}</span>}
          </span>
        </Cell>
        <Cell><Badge tone="ok" icon="check">{ko ? '저장됨' : 'Saved'}</Badge></Cell>
        <Actions>
          <IconButton icon="edit" testId={`edit-local-${server.id}`} label={t('setup.local.editFor', { name: server.name })}
            disabled={inert || open || removal?.pending} onClick={() => editServer(server.id)} />
          <IconButton icon="trash" tone="danger" testId={`remove-local-${server.id}`} label={t('setup.local.removeFor', { name: server.name })}
            disabled={inert || removal?.pending || editingId === server.id} onClick={() => removeServer(server.id)} />
        </Actions>
      </Row>
    );
  }
}
