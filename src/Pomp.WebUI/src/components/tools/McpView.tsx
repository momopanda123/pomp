import { useEffect, useRef, useState, type CSSProperties } from 'react';
import { toolsTranslate, type ToolsStringKey } from '../../i18n/toolsStrings';
import type { CatalogEntry, McpServer, ResourceScope } from '../../toolsProtocol';
import type { ToolsController } from '../../toolsState';
import { Icon } from '../Icon';
import { SectionRefresh, ViewSection } from '../ViewSection';
import { Badge, DataTable, Field, FormActions, IconButton, matches, Pager, SearchBox, Switch, Toolbar, usePaged } from '../ui';
import { ChoiceField, InPageDialog, ScopeChoice } from './InPageDialog';
import { useMcpManager } from './McpManage';

/**
 * Tools › MCP: the configured servers (state, connection kind, tools in this conversation, switch, test/edit/delete) and the servers of the
 * bundled catalog that can still be installed. The props are ToolsTab's.
 */
export function McpView({ controller, locale, disabled, busy }: { controller: ToolsController; locale: string; disabled: boolean; busy: boolean }) {
  const t = toolsTranslate(locale);
  const [query, setQuery] = useState('');
  const root = useRef<HTMLDivElement>(null); const refocus = useRef('');
  const settled = !controller.pending('mcp');
  useEffect(() => {
    const id = refocus.current; if (!id || !settled) return;
    refocus.current = '';
    if (document.activeElement && document.activeElement !== document.body) return;
    root.current?.querySelector<HTMLElement>(`[data-testid="${id}"]`)?.focus();
  });
  const mcp = useMcpManager({ controller, locale, disabled, busy, onCreated: () => setQuery(''), onControl: id => { refocus.current = id; } });
  const servers = controller.mcp.servers.filter(server => matches(query, server.name, server.type, server.command, server.url))
    .toSorted((left, right) => left.name.localeCompare(right.name, 'en', { sensitivity: 'base', numeric: true }) || left.scope.localeCompare(right.scope));
  const paged = usePaged(servers, query);
  const toolCount = (server: McpServer) => controller.tools.tools.filter(tool => tool.group === 'mcp' && tool.server === server.name).length;
  const refreshing = controller.pending('mcp') || controller.pending('catalog');
  return <div className="tools-scroll-body" ref={root} data-testid="mcp-view">
    <ViewSection sectionKey="tools-mcp" title={t('tools.mcpServers')} status={controller.mcp.servers.length} bodyClassName="ui-flush"
      actions={<>
        <button type="button" className="btn small subtle" data-testid="mcp-import" disabled={mcp.locked} onClick={mcp.startImport}><Icon name="download" size={12} />{t('tools.import')}</button>
        <button type="button" className="btn small" data-testid="mcp-add" data-tools-dialog-fallback="mcp" disabled={mcp.locked} onClick={mcp.startAdd}><Icon name="plus" size={12} />{t('mcp.addShort')}</button>
        <SectionRefresh onClick={() => { for (const domain of ['mcp', 'catalog'] as const) void controller.command(domain, { action: 'refresh' }); }} disabled={disabled || refreshing} testId="mcp-refresh" />
      </>}>
      {mcp.dialogs}
      {controller.mcp.restartRequired && <div className="ui-card-foot tools-banner-row" role="status" data-testid="mcp-restart-banner">
        <Icon name="warning" size={13} className="ui-warn" /><span className="ui-grow">{t('mcp.restartRequired')}</span>
        <button type="button" className="btn small" data-testid="mcp-restart" disabled={mcp.locked} onClick={() => { void controller.command('mcp', { action: 'restart' }); }}><Icon name="restart" size={12} />{t('mcp.restart')}</button>
      </div>}
      {controller.mcp.servers.length > 8 && <Toolbar><SearchBox value={query} onChange={setQuery} testId="mcp-search" /></Toolbar>}
      {controller.mcp.message && <p className="ui-note ui-muted" role="status">{controller.mcp.message}</p>}
      {mcp.notices}
      {controller.errors('mcp').map((error, index) => <p className="ui-note" role="alert" key={index}>{error}</p>)}
      <div className="tools-narrow" style={{ '--ui-cols-narrow': 'auto minmax(0, 1fr) auto auto' } as CSSProperties}>
        <DataTable columns="auto minmax(0, 1fr) auto auto auto auto" label={t('tools.mcpServers')} testId="mcp-table" empty={!servers.length} emptyRows={2}
          head={['', t('tcol.name'), <span className="narrow-hide">{t('tcol.transport')}</span>, <span className="narrow-hide">{t('tcol.tools')}</span>, '', '']}>
          {paged.slice.map(server => {
            const state = server.error ? 'error' : server.enabled ? 'on' : 'off'; const count = toolCount(server);
            const secrets = [...Object.keys(server.env ?? {}), ...Object.keys(server.headers ?? {})];
            const target = server.type === 'stdio' ? [server.command, ...(server.args ?? [])].filter(Boolean).join(' ') : server.url ?? '';
            return <div className="ui-tr" role="row" key={`${server.scope}:${server.name}`} data-testid={`mcp-row-${server.name}`} data-kind="mcp" data-on={server.enabled}>
              <span role="cell" className={`ui-cell ui-status-icon ${state === 'error' ? 'ui-err' : state === 'on' ? 'ui-ok' : 'ui-muted'}`} title={t(state === 'error' ? 'mcp.stateError' : state === 'on' ? 'mcp.stateOn' : 'mcp.stateOff')} data-testid={`mcp-state-${server.name}`}>
                <Icon name={state === 'off' ? 'circle' : 'dot'} size={16} /></span>
              <span role="cell" className="ui-cell">
                <span className="tools-name-line"><strong>{server.name}</strong><Badge tone={server.scope === 'project' ? 'accent' : 'neutral'}>{t(server.scope === 'project' ? 'skills.kind.project' : 'skills.kind.user')}</Badge><span className="inline-kind"><Badge>{server.type}</Badge></span></span>
                {target && <span className="ui-sub ui-mono" title={target}>{target}</span>}
                {secrets.length > 0 && <span className="ui-sub ui-mono" title={secrets.join(', ')}>{secrets.join(', ')}: {'••••'}</span>}
                {server.error && <span className="ui-sub ui-err" role="alert" title={server.error}>{server.error}</span>}
              </span>
              <span role="cell" className="ui-cell narrow-hide"><Badge>{server.type}</Badge></span>
              <span role="cell" className="ui-cell narrow-hide ui-muted tools-num">{server.enabled && count ? count : '–'}</span>
              <span role="cell" className="ui-cell"><Switch checked={server.enabled} showLabel={false} label={t('mcp.toggle', { name: server.name })} disabled={mcp.locked || !server.hash} testId={`mcp-toggle-${server.name}`} onChange={() => mcp.toggle(server)} /></span>
              <span role="cell" className="ui-cell ui-actions">
                <IconButton icon="plug" label={t('mcp.test')} disabled={mcp.locked} testId={`mcp-test-${server.name}`} onClick={() => mcp.test(server)} />
                <IconButton icon="edit" label={t('mcp.edit')} disabled={mcp.locked || !server.hash} testId={`mcp-edit-${server.name}`} onClick={() => mcp.edit(server)} />
                <IconButton icon="trash" tone="danger" label={t('tools.delete')} disabled={mcp.locked || !server.hash} testId={`mcp-delete-${server.name}`} onClick={() => mcp.remove(server)} />
              </span>
            </div>;
          })}
        </DataTable>
      </div>
      <Pager paged={paged} testId="mcp-pager" />
      {mcp.output}
    </ViewSection>
    <CatalogSection controller={controller} locale={locale} locked={disabled || busy} />
  </div>;
}

/** The catalog's MCP servers: name, pinned version and Install (asks the scope and the inputs the entry needs). An entry installed in every scope it allows shows Installed. */
function CatalogSection({ controller, locale, locked: blocked }: { controller: ToolsController; locale: string; locked: boolean }) {
  const t = toolsTranslate(locale);
  const entries = controller.catalog.entries.filter(entry => entry.kind === 'mcp');
  const [installing, setInstalling] = useState<{ entry: CatalogEntry; scope: ResourceScope; inputs: Record<string, string> }>(); const [validation, setValidation] = useState('');
  const pending = controller.pending('catalog'); const locked = blocked || pending;
  const open = (entry: CatalogEntry) => entry.scopes.filter(scope => !entry.installed.includes(scope));
  async function install() {
    if (!installing || locked) return;
    if (installing.entry.inputs.some(input => input.required && !installing.inputs[input.key]?.trim())) { setValidation(t('ext.inputRequired')); return; }
    setValidation('');
    const inputs = Object.fromEntries(Object.entries(installing.inputs).filter(([, value]) => value.trim()));
    const reply = await controller.command('catalog', { action: 'install', id: installing.entry.id, scope: installing.scope, inputs });
    if (reply.ok) setInstalling(undefined); else setValidation(reply.error ?? t('tools.failed'));
  }
  const entry = installing?.entry; const scopes = entry ? open(entry) : [];
  return <ViewSection sectionKey="tools-mcp-catalog" title={t('catalog.title')} status={entries.filter(item => open(item).length > 0).length} bodyClassName="ui-flush" data-testid="mcp-catalog">
    {installing && entry && <InPageDialog title={t('catalog.installTitle', { title: entry.title })} testId="catalog-install" fallback="catalog" onClose={() => setInstalling(undefined)} pending={pending}>
      <form className="ui-form tools-form" noValidate onSubmit={event => { event.preventDefault(); void install(); }}><fieldset disabled={locked}>
        <div className="tools-catalog-facts">
          <Badge tone="info" mono>{entry.version}</Badge>
          {entry.needs.map(need => <Badge key={need}>{t(`ext.need.${need}` as ToolsStringKey)}</Badge>)}
          {entry.publisher && <span className="ui-muted">{t('ext.publisher', { name: entry.publisher })}</span>}
          {entry.license && <span className="ui-muted">{t('ext.license', { name: entry.license })}</span>}
        </div>
        {scopes.length > 1 ? <ChoiceField label={t('ext.installScope')}><ScopeChoice t={t} label={t('ext.installScope')} testId="catalog-scope" value={installing.scope} onChange={scope => setInstalling({ ...installing, scope })} /></ChoiceField>
          : <ChoiceField label={t('ext.installScope')}><span><Badge tone={installing.scope === 'project' ? 'accent' : 'neutral'}>{t(`skills.source.${installing.scope}`)}</Badge></span></ChoiceField>}
        {entry.inputs.map(input => <Field key={input.key} label={input.required ? input.label : `${input.label} · ${t('ext.optional')}`}>{id =>
          <input id={id} type={input.secret ? 'password' : 'text'} autoComplete={input.secret ? 'new-password' : 'off'} spellCheck={false} data-testid={`catalog-input-${input.key}`}
            value={installing.inputs[input.key] ?? ''} onChange={event => setInstalling({ ...installing, inputs: { ...installing.inputs, [input.key]: event.target.value } })} />}</Field>)}
        {entry.requires.length > 0 && <p className="ui-muted">{t('ext.requires', { titles: entry.requires.join(', ') })}</p>}
      </fieldset>
        {validation && <p role="alert" className="ui-err" data-testid="catalog-validation">{validation}</p>}
        <FormActions><button type="button" className="btn" disabled={pending} onClick={() => setInstalling(undefined)}>{t('tools.cancel')}</button>
          <button type="submit" className="btn primary" data-testid="catalog-install-run" disabled={locked}>{t('catalog.install')}</button></FormActions>
      </form>
    </InPageDialog>}
    {controller.catalog.message && <p className="ui-note ui-muted" role="status">{controller.catalog.message}</p>}
    {controller.errors('catalog').map((error, index) => <p className="ui-note" role="alert" key={index}>{error}</p>)}
    <div className="tools-narrow" style={{ '--ui-cols-narrow': 'minmax(0, 1fr) auto' } as CSSProperties}>
      <DataTable columns="minmax(0, 1fr) auto auto" label={t('catalog.title')} testId="catalog-table" empty={!entries.length} emptyRows={2}
        head={[t('tcol.name'), <span className="narrow-hide">{t('tcol.version')}</span>, '']}>
        {entries.map(item => {
          const free = open(item);
          return <div className="ui-tr" role="row" key={item.id} data-testid={`catalog-row-${item.id}`}>
            <span role="cell" className="ui-cell"><strong>{item.title}</strong><span className="ui-sub" title={item.description}>{item.description}</span></span>
            <span role="cell" className="ui-cell narrow-hide ui-mono ui-muted">{item.version}</span>
            <span role="cell" className="ui-cell ui-actions">{free.length
              ? <button type="button" className="btn small" data-testid={`catalog-install-${item.id}`} disabled={locked || !!installing} onClick={() => { setValidation(''); setInstalling({ entry: item, scope: free[0], inputs: {} }); }}>{t('catalog.install')}</button>
              : <Badge tone="ok" icon="check">{t('catalog.installed')}</Badge>}</span>
          </div>;
        })}
      </DataTable>
    </div>
  </ViewSection>;
}
