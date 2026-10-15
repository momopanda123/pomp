import { useState, type ReactNode } from 'react';
import { toolsTranslate, type ToolsTranslate } from '../../i18n/toolsStrings';
import { MASKED_ENV, type McpServer, type ResourceScope } from '../../toolsProtocol';
import type { ToolsController } from '../../toolsState';
import { Icon } from '../Icon';
import { Badge, DataTable, Field, FormActions, IconButton, Pager, Segmented, Switch, usePaged } from '../ui';
import { ChoiceField, ConfirmDelete, InPageDialog, ScopeChoice } from './InPageDialog';
import { HEADER_NAME, headerValueValid, MCP_IMPORT_MAX_CHARS, parseMcpImport } from './mcpImport';

/** One environment variable or header in the editor: a stored value shows as the mask and keeps its stored value unless the field is changed. */
interface PairRow { id: number; name: string; value: string }
interface Editor { original?: McpServer; name: string; scope: ResourceScope; type: McpServer['type']; command: string; args: string; env: PairRow[]; headers: PairRow[]; url: string; enabled: boolean }
const masked = (values?: Record<string, string>): PairRow[] => Object.keys(values ?? {}).map((name, id) => ({ id, name, value: MASKED_ENV }));
function editorFor(server?: McpServer): Editor {
  return { original: server, name: server?.name ?? '', scope: server?.scope ?? 'project', type: server?.type ?? 'stdio', command: server?.command ?? '', args: server?.args?.join('\n') ?? '',
    env: masked(server?.env), headers: masked(server?.headers), url: server?.url ?? '', enabled: server?.enabled ?? true };
}

export interface McpManager {
  t: ToolsTranslate;
  locked: boolean;
  /** The add/edit form, the JSON import and the delete question, whichever is open. */
  dialogs: ReactNode; editing: boolean;
  /** The result line of the last JSON import and the connection test output. */
  notices: ReactNode; output: ReactNode;
  startAdd(): void; startImport(): void;
  edit(server: McpServer): void; remove(server: McpServer): void; toggle(server: McpServer): void; test(server: McpServer): void;
}

/**
 * Everything the MCP rows and dialogs do. `onCreated` runs after a new server was saved or servers were imported (the view clears its search);
 * `onControl` after a row control was used (the view keeps focus on it).
 */
export function useMcpManager({ controller, locale, disabled, busy, onCreated, onControl }: {
  controller: ToolsController; locale: string; disabled: boolean; busy: boolean; onCreated: () => void; onControl: (testId: string) => void;
}): McpManager {
  const t = toolsTranslate(locale); const [editor, setEditor] = useState<Editor>(); const [deleting, setDeleting] = useState<McpServer>(); const [validation, setValidation] = useState('');
  const pending = controller.pending('mcp'); const locked = disabled || busy || pending;
  const testResult = controller.feedback('mcp', 'test'); const [closedTest, setClosedTest] = useState<string>();
  function update(patch: Partial<Editor>) { setEditor(previous => previous ? { ...previous, ...patch } : undefined); }
  async function save() {
    if (!editor || locked) return;
    const name = editor.name.trim(); const names = editor.env.map(row => row.name.trim()); const headerNames = editor.headers.map(row => row.name.trim());
    const stdio = editor.type === 'stdio';
    let validUrl = false;
    try { const url = new URL(editor.url); validUrl = ['https:', 'http:'].includes(url.protocol) && !url.username && !url.password; } catch { /* validation below */ }
    // eslint-disable-next-line no-control-regex
    if (!name || /[\x00-\x1f]/.test(name) || names.some(key => !/^[A-Za-z_][A-Za-z0-9_]*$/.test(key)) || new Set(names).size !== names.length || (stdio ? !editor.command.trim() : !validUrl) ||
      (!stdio && (headerNames.some(key => !HEADER_NAME.test(key)) || new Set(headerNames.map(key => key.toLowerCase())).size !== headerNames.length || editor.headers.some(row => !headerValueValid(row.value))))) {
      setValidation(t('mcp.invalid')); return;
    }
    setValidation('');
    const server: McpServer = { name, scope: editor.scope, type: editor.type, enabled: editor.enabled,
      env: Object.fromEntries(editor.env.map(row => [row.name.trim(), row.value])),
      ...(stdio ? { command: editor.command.trim(), args: editor.args.split(/\r?\n/).filter(arg => arg.length > 0) } : { url: editor.url.trim() }),
      // No headers key when there are none: the host then drops a header block the server had before.
      ...(!stdio && editor.headers.length ? { headers: Object.fromEntries(editor.headers.map(row => [row.name.trim(), row.value])) } : {}) };
    const reply = await controller.command('mcp', { action: 'save', scope: server.scope, name, server, expectedHash: editor.original?.hash });
    if (reply.ok) { if (!editor.original) onCreated(); setEditor(undefined); }
  }
  async function remove() {
    if (locked || !deleting?.hash) return;
    const reply = await controller.command('mcp', { action: 'delete', name: deleting.name, scope: deleting.scope, expectedHash: deleting.hash });
    if (reply.ok) setDeleting(undefined);
  }
  // Pasted configurations go through the same per-server save as the editor (no new host command, never an overwrite).
  const [importing, setImporting] = useState<{ text: string; scope: ResourceScope }>(); const [importRunning, setImportRunning] = useState(false);
  const [imported, setImported] = useState<{ done: number; failures: string[] }>();
  const entries = importing ? parseMcpImport(importing.text, importing.scope, controller.mcp.servers) : null;
  const previewPage = usePaged(entries ?? [], importing?.text ?? '');
  const ready = entries?.filter(entry => entry.server && !entry.error) ?? [];
  async function runImport() {
    if (!importing || locked || importRunning || !ready.length) return;
    setImportRunning(true); let done = 0; const failures: string[] = [];
    try {
      for (const entry of ready) {
        const reply = await controller.command('mcp', { action: 'save', scope: entry.server!.scope, name: entry.server!.name, server: entry.server! });
        if (reply.ok) done++; else failures.push(t('mcp.importFailed', { name: entry.server!.name, error: reply.error ?? '' }));
      }
    } finally { setImportRunning(false); }
    setImported({ done, failures });
    if (done) onCreated();
    if (!failures.length) setImporting(undefined); // also drops the pasted secrets
  }
  const stdio = editor?.type === 'stdio';
  const dialogs = <>
    {importing && <InPageDialog title={t('mcp.importTitle')} testId="mcp-import-dialog" fallback="mcp" onClose={() => { if (!importRunning) setImporting(undefined); }} pending={pending || importRunning}>
      <div className="ui-form tools-form"><fieldset disabled={locked || importRunning}>
        <ChoiceField label={t('skills.scope')}><ScopeChoice t={t} label={t('skills.scope')} testId="mcp-import-scope" value={importing.scope} onChange={scope => setImporting({ ...importing, scope })} /></ChoiceField>
        <Field label={t('mcp.importPaste')}>{id => <textarea id={id} className="tools-mono" data-testid="mcp-import-text" rows={8} spellCheck={false} maxLength={MCP_IMPORT_MAX_CHARS} value={importing.text} onChange={event => setImporting({ ...importing, text: event.target.value })} />}</Field>
      </fieldset>
        {importing.text.trim() && !entries && <p role="alert" className="ui-err" data-testid="mcp-import-unreadable">{t('mcp.importUnreadable')}</p>}
        {entries && <DataTable className="tools-import-preview" columns="minmax(0, 1fr) auto minmax(0, max-content)" label={t('mcp.importTitle')} testId="mcp-import-preview" head={[t('tcol.name'), t('tcol.transport'), t('tcol.state')]}>{previewPage.slice.map(entry => <div className="ui-tr" role="row" key={entry.name}>
          <span role="cell" className="ui-cell"><strong>{entry.name}</strong>
            {entry.server && <span className="ui-sub ui-mono">{entry.server.type === 'stdio' ? [entry.server.command, ...(entry.server.args ?? [])].join(' ') : entry.server.url}</span>}
            {[...Object.keys(entry.server?.env ?? {}), ...Object.keys(entry.server?.headers ?? {})].length > 0 && <span className="ui-sub ui-mono">{[...Object.keys(entry.server?.env ?? {}), ...Object.keys(entry.server?.headers ?? {})].join(', ')}: {MASKED_ENV}</span>}</span>
          <span role="cell" className="ui-cell">{entry.server && <Badge>{entry.server.type}</Badge>}</span>
          <span role="cell" className="ui-cell" title={entry.error === 'fields' ? (entry.fields ?? []).join(', ') : undefined}>{entry.error
            ? <Badge tone={entry.error === 'exists' ? 'warn' : 'error'}>{entry.error === 'exists' ? t('mcp.importExists') : entry.error === 'fields' ? t('mcp.importFields', { fields: (entry.fields ?? []).join(', ') }) : t('mcp.importInvalid')}</Badge>
            : <Badge tone="ok">{t('mcp.importReady')}</Badge>}</span>
        </div>)}</DataTable>}
        {entries && <Pager paged={previewPage} testId="mcp-import-pager" />}
        {imported && imported.failures.map((failure, index) => <p role="alert" className="ui-err" key={index}>{failure}</p>)}
        <FormActions><button type="button" className="btn" disabled={importRunning} onClick={() => setImporting(undefined)}>{t('tools.cancel')}</button>
          <button type="button" className="btn primary" data-testid="mcp-import-run" disabled={locked || importRunning || !ready.length} onClick={() => { void runImport(); }}>{t('mcp.importRun', { count: ready.length })}</button></FormActions>
      </div>
    </InPageDialog>}
    {editor && <InPageDialog title={editor.original ? `${t('mcp.editor')} · ${editor.original.name}` : t('mcp.editor')} testId="mcp-editor" fallback="mcp" onClose={() => setEditor(undefined)} pending={pending}>
      <form className="ui-form tools-form" noValidate onSubmit={event => { event.preventDefault(); void save(); }}><fieldset disabled={locked}>
        <div className="ui-field-row">
          <Field label={t('mcp.name')}>{id => <input id={id} value={editor.name} data-testid="mcp-name" maxLength={100} spellCheck={false} disabled={!!editor.original} onChange={event => update({ name: event.target.value })} />}</Field>
          <ChoiceField label={t('skills.scope')}><ScopeChoice t={t} label={t('skills.scope')} testId="mcp-scope" disabled={!!editor.original} value={editor.scope} onChange={scope => update({ scope })} /></ChoiceField>
        </div>
        <div className="ui-field-row">
          <ChoiceField label={t('mcp.type')}><Segmented<McpServer['type']> label={t('mcp.type')} testId="mcp-type" value={editor.type} onChange={type => update({ type })}
            options={(['stdio', 'http', 'sse'] as const).map(type => ({ id: type, label: type, testId: `mcp-type-${type}` }))} /></ChoiceField>
          <ChoiceField label={t('ext.enabled')}><Switch checked={editor.enabled} label={t('ext.enabled')} showLabel={false} testId="mcp-enabled" onChange={enabled => update({ enabled })} /></ChoiceField>
        </div>
        {stdio ? <>
          <Field label={t('mcp.command')}>{id => <input id={id} className="tools-mono" value={editor.command} spellCheck={false} data-testid="mcp-command" onChange={event => update({ command: event.target.value })} />}</Field>
          <Field label={t('mcp.args')}>{id => <textarea id={id} className="tools-mono" value={editor.args} rows={3} spellCheck={false} data-testid="mcp-args" onChange={event => update({ args: event.target.value })} />}</Field>
        </> : <>
          <Field label={t('mcp.url')}>{id => <input id={id} className="tools-mono" type="url" value={editor.url} spellCheck={false} data-testid="mcp-url" onChange={event => update({ url: event.target.value })} />}</Field>
          <PairEditor kind="header" title={t('mcp.headers')} rows={editor.headers} labels={{ name: t('mcp.headerName'), value: t('mcp.headerValue'), add: t('mcp.headerAdd'), remove: t('mcp.headerRemove') }} onChange={headers => update({ headers })} />
        </>}
        <PairEditor kind="env" title={t('mcp.env')} rows={editor.env} labels={{ name: t('mcp.envName'), value: t('mcp.envValue'), add: t('mcp.envAdd'), remove: t('mcp.envRemove') }} onChange={env => update({ env })} />
      </fieldset>
        {validation && <p role="alert" className="ui-err" data-testid="mcp-validation">{validation}</p>}
        <FormActions><button type="button" className="btn" disabled={pending} onClick={() => setEditor(undefined)}>{t('tools.cancel')}</button><button type="submit" className="btn primary" data-testid="mcp-save" disabled={locked}>{t('tools.save')}</button></FormActions>
      </form>
    </InPageDialog>}
    {deleting && <ConfirmDelete t={t} testId="mcp-delete-confirm" yesTestId="mcp-delete-yes" fallback="mcp" question={t('mcp.deleteQuestion', { name: deleting.name })} locked={locked} pending={pending} onCancel={() => setDeleting(undefined)} onConfirm={() => { void remove(); }} />}
  </>;
  const notices = imported && !importing ? <p className="ui-note" role="status" data-testid="mcp-import-done">{t('mcp.importDone', { count: imported.done })}</p> : null;
  const output = <>
    {testResult.pending && <p className="ui-note ui-muted" role="status">{t('tools.pending')}</p>}
    {testResult.output !== undefined && testResult.testServer && testResult.requestId !== closedTest && <section className="tools-test" aria-label={t('mcp.output')}>
      <div className="tools-test-head"><Icon name="plug" size={13} /><strong>{t('mcp.output')}</strong><span className="ui-muted">{testResult.testServer.name}</span>
        <Badge tone={testResult.testServer.scope === 'project' ? 'accent' : 'neutral'}>{t(testResult.testServer.scope === 'project' ? 'skills.kind.project' : 'skills.kind.user')}</Badge>
        {testResult.stale && <Badge tone="warn" testId="mcp-test-stale">{t('mcp.testStale')}</Badge>}
        <span className="ui-grow" /><IconButton icon="x" label={t('review.close')} testId="mcp-test-close" onClick={() => setClosedTest(testResult.requestId)} /></div>
      <pre tabIndex={0} className="tools-test-output" data-testid="mcp-test-output">{testResult.output}</pre></section>}
  </>;
  return {
    t, locked, dialogs, editing: !!editor || !!importing || !!deleting, notices, output,
    startAdd() { setValidation(''); setEditor(editorFor()); },
    startImport() { setImported(undefined); setImporting({ text: '', scope: 'project' }); },
    edit(server) { setValidation(''); setEditor(editorFor(server)); }, remove: setDeleting,
    toggle(server) { onControl(`mcp-toggle-${server.name}`); void controller.command('mcp', { action: server.enabled ? 'disable' : 'enable', scope: server.scope, name: server.name, expectedHash: server.hash }); },
    test(server) { void controller.command('mcp', { action: 'test', scope: server.scope, name: server.name }); },
  };
}

/** The rows of environment variables or headers in the editor: names are editable, values are masked and a stored one keeps its stored value. */
function PairEditor({ kind, title, rows, labels, onChange }: {
  kind: 'env' | 'header'; title: string; rows: PairRow[]; labels: { name: string; value: string; add: string; remove: string }; onChange: (rows: PairRow[]) => void;
}) {
  const patch = (id: number, change: Partial<PairRow>) => onChange(rows.map(row => row.id === id ? { ...row, ...change } : row));
  return <ChoiceField label={title}>
    <div className="tools-pairs">
      {rows.map(row => <div className="tools-pair" key={row.id}>
        <input aria-label={labels.name} className="tools-mono" placeholder={labels.name} spellCheck={false} data-testid={`mcp-${kind}-name-${row.id}`} value={row.name} onChange={event => patch(row.id, { name: event.target.value })} />
        <input type="password" autoComplete="new-password" aria-label={labels.value} placeholder={labels.value} data-testid={`mcp-${kind}-value-${row.id}`} value={row.value} onChange={event => patch(row.id, { value: event.target.value })} />
        <IconButton icon="trash" tone="danger" label={labels.remove} onClick={() => onChange(rows.filter(item => item.id !== row.id))} />
      </div>)}
      <button type="button" className="btn small subtle tools-pair-add" data-testid={`mcp-${kind}-add`} onClick={() => onChange([...rows, { id: Math.max(-1, ...rows.map(row => row.id)) + 1, name: '', value: '' }])}><Icon name="plus" size={12} />{labels.add}</button>
    </div>
  </ChoiceField>;
}
