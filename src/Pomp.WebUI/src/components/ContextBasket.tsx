import { useEffect, useMemo, useRef, useState, type Dispatch, type DragEvent } from 'react';
import { copyText, newId } from '../format';
import { onMessage, send, type HostToUi, type KnowledgeItem, type WorkspaceFile, type UiToHost, type ModelInfo } from '../protocol';
import type { Action } from '../state';
import { DraftTokenCounter } from './DraftTokenCounter';
import { ContextKindBadge } from './ContextKindBadge';
import { markMenuHandled, useContextMenu, type ContextMenuItem } from './ContextMenu';
import { revealControls } from './RevealControls';
import { useRefreshOnShow } from '../useRefreshOnShow';
import { SectionRefresh, ViewSection } from './ViewSection';
import { KnowledgeChecklist } from './KnowledgeChecklist';
import { usableKnowledge } from '../knowledge';
import { useKnowledge } from '../knowledgeState';
import { useT } from '../i18n';
import { Actions, DataTable, Field, FormActions, IconButton, matches, Pager, SearchBox, Switch, Toolbar, usePaged } from './ui';
import { ChoiceField, ConfirmDelete, InPageDialog } from './tools/InPageDialog';
import { Icon } from './Icon';

type PresetState = HostToUi['workspace/contextPresetsState'];
type Preset = NonNullable<PresetState['presets']>[number];
export function ContextBasket({ files, text, includeEditorContext, onIncludeEditorContextChange, dispatch, locale, workspaceKey, sessionId, model, connected, disabled, visible = false }: {
  visible?: boolean; files: WorkspaceFile[]; text: string; includeEditorContext: boolean; onIncludeEditorContextChange: (value: boolean) => void;
  dispatch: Dispatch<Action>; locale: string; workspaceKey?: string; sessionId?: string; model?: ModelInfo; connected: boolean; disabled: boolean;
}) {
  const t = useT();
  const [presetsOpen, setPresetsOpen] = useState(false);
  const [data, setData] = useState<PresetState>({ presets: [] });
  const [error, setError] = useState('');
  const [presetQuery, setPresetQuery] = useState('');
  // The save form (a new preset from the current files, or an edit of one: rename, optionally taking the current files) and the
  // preset waiting for delete confirmation.
  const [saving, setSaving] = useState<{ name: string; preset?: Preset; useCurrent: boolean } | null>(null);
  const [deleting, setDeleting] = useState<Preset | null>(null);
  // The preset command in flight: a refresh keeps the open form usable (its name field keeps focus); a save or delete locks it.
  const [waiting, setWaiting] = useState<UiToHost['workspace/contextPresets']['action'] | null>(null); const pending = useRef<string | null>(null);
  const pendingAction = useRef<UiToHost['workspace/contextPresets']['action'] | null>(null);
  const busy = waiting !== null, writing = waiting === 'save' || waiting === 'delete';
  // The attached files: searched and paged (a basket can hold thousands); the bulk checkbox and remove act on the rows the search shows.
  const [fileQuery, setFileQuery] = useState('');
  const [clearing, setClearing] = useState(false);
  const shownFiles = useMemo(() => files.filter(file => matches(fileQuery, file.path, file.fullPath)), [files, fileQuery]);
  // Nothing left to remove (the files were sent, removed or searched away): the question goes with them.
  useEffect(() => { if (!shownFiles.length) setClearing(false); }, [shownFiles.length]);
  const pagedFiles = usePaged(shownFiles, fileQuery + '|' + (workspaceKey ?? ''), 10);
  const shownIncluded = useMemo(() => shownFiles.filter(file => !file.excluded).length, [shownFiles]);
  const setShownExcluded = (excluded: boolean) => { const shown = new Set(shownFiles); dispatch({ type: 'attachmentsReplace', files: files.map(file => shown.has(file) ? { ...file, excluded } : file) }); };
  const removeShown = () => { const shown = new Set(shownFiles); dispatch({ type: 'attachmentsReplace', files: files.filter(file => !shown.has(file)) }); setClearing(false); setFileQuery(''); };
  const drag = useRef<{ nonce: string; path: string; signature: string; workspace?: string } | null>(null);
  const signature = useMemo(() => JSON.stringify(files.map(file => [file.fullPath, !!file.excluded])), [files]);
  const dragType = 'application/x-pomp-context-order';
  const drop = (event: DragEvent, beforePath?: string) => {
    event.preventDefault(); event.stopPropagation();
    const source = drag.current; drag.current = null;
    if (disabled || !source || source.workspace !== workspaceKey || source.signature !== signature ||
        event.dataTransfer.getData(dragType) !== source.nonce) return;
    dispatch({ type: 'attachmentReorder', fullPath: source.path, beforePath });
  };
  useEffect(() => { drag.current = null; }, [workspaceKey, signature, disabled]);
  // Arrow keys move between the files' checkboxes (Space checks one), across the pages too.
  const fileList = useRef<HTMLDivElement>(null);
  const focusAfterPage = useRef<'first' | 'last' | null>(null);
  const checks = () => [...(fileList.current?.querySelectorAll<HTMLInputElement>('input[data-basket-check]:not(:disabled)') ?? [])];
  useEffect(() => {
    const edge = focusAfterPage.current; focusAfterPage.current = null;
    const list = checks();
    if (edge) (edge === 'first' ? list[0] : list[list.length - 1])?.focus({ preventScroll: true });
  }, [pagedFiles.page]);
  const moveCheck = (event: React.KeyboardEvent<HTMLInputElement>) => {
    if ((event.key !== 'ArrowDown' && event.key !== 'ArrowUp') || event.altKey || event.ctrlKey || event.metaKey || event.shiftKey) return;
    event.preventDefault();
    const list = checks(); const index = list.indexOf(event.currentTarget);
    const next = index + (event.key === 'ArrowDown' ? 1 : -1);
    if (next >= 0 && next < list.length) { list[next]!.focus(); list[next]!.scrollIntoView({ block: 'nearest' }); return; }
    const page = pagedFiles.page + (next < 0 ? -1 : 1);
    if (page < 0 || page >= pagedFiles.pages) return;
    focusAfterPage.current = next < 0 ? 'last' : 'first'; pagedFiles.setPage(page);
  };
  const ko = locale.toLowerCase().startsWith('ko');
  useEffect(() => {
    const off = onMessage(message => {
      if (message.type !== 'workspace/contextPresetsState' || message.payload.requestId !== pending.current) return;
      const action = pendingAction.current;
      pending.current = null; pendingAction.current = null; setWaiting(null);
      if (message.payload.error) { setError(message.payload.error); return; }
      if (!Array.isArray(message.payload.presets) || typeof message.payload.root !== 'string') return;
      setData(message.payload); setError('');
      if (action === 'save') setSaving(null);
      if (action === 'delete') setDeleting(null);
    });
    pending.current = null; pendingAction.current = null; setWaiting(null); setData({ presets: [] }); setSaving(null); setDeleting(null); setPresetQuery('');
    return () => { pending.current = null; off(); };
  }, [workspaceKey]);
  useEffect(() => {
    if (!waiting) return;
    const id = pending.current;
    const timer = window.setTimeout(() => { if (id === pending.current) { pending.current = null; setWaiting(null); setError(ko ? '프리셋 응답을 받지 못했어요.' : 'No preset response.'); } }, 8000);
    return () => window.clearTimeout(timer);
  }, [waiting, workspaceKey, ko]);
  const command = (action: UiToHost['workspace/contextPresets']['action'], target: { name?: string; id?: string; preset?: Preset } = {}) => {
    if (disabled || pending.current) return;
    const id = newId(); pending.current = id; pendingAction.current = action; setWaiting(action); setError('');
    const payload: UiToHost['workspace/contextPresets'] = { requestId: id, action, root: data.root };
    if (action === 'save') Object.assign(payload, target.preset ? { id: target.preset.id, name: target.name, files: target.preset.files, includeEditorContext: target.preset.includeEditorContext }
      : { id: target.id, name: target.name, files, includeEditorContext });
    if (action === 'delete') payload.id = target.id;
    if (!send('workspace/contextPresets', payload)) { pending.current = null; pendingAction.current = null; setWaiting(null); setError(t('error.hostUnavailable')); }
  };
  useRefreshOnShow(visible && presetsOpen && connected, () => command('refresh'));
  const [suggestions, setSuggestions] = useState<HostToUi['workspace/contextSuggestions'] | null>(null);
  const [searching, setSearching] = useState(false);
  const suggestionRequest = useRef<string | null>(null);
  useEffect(() => {
    suggestionRequest.current = null; setSearching(false); setSuggestions(null);
    return onMessage(message => {
      if (message.type !== 'workspace/contextSuggestions' || !suggestionRequest.current || message.payload.requestId !== suggestionRequest.current) return;
      suggestionRequest.current = null; setSearching(false);
      if (message.payload.root && message.payload.root.toLowerCase() !== workspaceKey?.toLowerCase()) return;
      setSuggestions(message.payload);
    });
  }, [text, workspaceKey]);
  useEffect(() => {
    if (!searching) return;
    const id = suggestionRequest.current;
    const timer = window.setTimeout(() => {
      if (id !== suggestionRequest.current) return;
      suggestionRequest.current = null; setSearching(false);
      setSuggestions({ requestId: id ?? '', files: [], error: ko ? '검색 응답을 받지 못했어요.' : 'No search response.' });
    }, 17000);
    return () => window.clearTimeout(timer);
  }, [searching, ko]);
  const suggest = () => {
    if (disabled || suggestionRequest.current || !workspaceKey || !text.trim()) return;
    const id = newId(); suggestionRequest.current = id; setSearching(true); setSuggestions(null);
    if (!send('workspace/suggestContext', { requestId: id, root: workspaceKey, query: text.trim().slice(0, 2000), excludePaths: files.map(file => file.fullPath) })) {
      suggestionRequest.current = null; setSearching(false);
      setSuggestions({ requestId: id, files: [], error: t('error.hostUnavailable') });
    }
  };
  const identity = (path: string) => path.replaceAll('/', '\\').toLowerCase();
  const candidates = (suggestions?.files ?? []).filter(file => typeof file.fullPath === 'string' && typeof file.path === 'string' && !files.some(attached => identity(attached.fullPath) === identity(file.fullPath)));
  const presets = (data.presets ?? []).filter(preset => matches(presetQuery, preset.name, ...preset.files.map(file => file.path)))
    .toSorted((left, right) => left.name.localeCompare(right.name, undefined, { numeric: true }));
  const pagedPresets = usePaged(presets, presetQuery);
  const nameTaken = !!saving && !saving.preset && (data.presets ?? []).some(preset => preset.name.toLowerCase() === saving.name.trim().toLowerCase());
  const submitPreset = () => {
    if (!saving?.name.trim()) return;
    const name = saving.name.trim();
    if (!saving.preset) command('save', { name });
    else if (saving.useCurrent) command('save', { name, id: saving.preset.id });
    else command('save', { name, preset: saving.preset });
  };
  const loadPreset = (preset: Preset) => { dispatch({ type: 'attachmentsReplace', files: preset.files }); onIncludeEditorContextChange(preset.includeEditorContext); };
  const included = useMemo(() => files.filter(file => !file.excluded), [files]);
  const pathBytes = useMemo(() => new TextEncoder().encode(included.map(file => file.fullPath).join('\n')).length, [included]);
  const bytes = new TextEncoder().encode(text).length + pathBytes;
  // The knowledge the person ticks goes with every message of this conversation until it is cleared.
  const knowledge = useKnowledge();
  const usable = usableKnowledge(knowledge.items);
  const picked = new Set(knowledge.selected);
  const [knowledgeError, setKnowledgeError] = useState('');
  useRefreshOnShow(visible && connected, knowledge.refresh);
  const toggleKnowledge = async (item: KnowledgeItem, checked: boolean) => {
    const ids = knowledge.selected.filter(id => id !== item.id);
    if (checked) ids.push(item.id);
    setKnowledgeError('');
    const reply = await knowledge.command({ action: 'select', ids });
    if (!reply.ok) setKnowledgeError(reply.error || t('tools.failed'));
  };
  const { menuProps, renderMenu: renderBasketMenu } = useContextMenu();
  const chipMenu = (file: WorkspaceFile): ContextMenuItem[] => {
    const items: ContextMenuItem[] = [{ id: 'copy-path', label: t('menu.copyPath'), testId: 'context-menu-copy-path', onSelect: () => { void copyText(file.fullPath); } }];
    // A page of the list cannot be dragged onto another page: the top and bottom moves cross pages.
    if (!disabled) items.push(
      { id: 'move-top', label: t('menu.moveTop'), testId: 'context-menu-move-top', onSelect: () => { if (files[0] && files[0] !== file) dispatch({ type: 'attachmentReorder', fullPath: file.fullPath, beforePath: files[0].fullPath }); } },
      { id: 'move-bottom', label: t('menu.moveBottom'), testId: 'context-menu-move-bottom', onSelect: () => { dispatch({ type: 'attachmentReorder', fullPath: file.fullPath }); } },
      { id: 'remove', label: t('menu.removeAttachment'), testId: 'context-menu-remove-attachment', onSelect: () => { dispatch({ type: 'attachmentRemove', fullPath: file.fullPath }); } });
    return items;
  };
  const chipHandlers = (file: WorkspaceFile) => ({
    onContextMenu: (event: React.MouseEvent): void => {
      menuProps(() => chipMenu(file)).onContextMenu?.(event);
      if (event.defaultPrevented) markMenuHandled(event.nativeEvent);
    },
    onKeyDown: (event: React.KeyboardEvent): void => {
      menuProps(() => chipMenu(file)).onKeyDown?.(event);
    },
  });
  return <section className="context-basket">
    <h3>{ko ? '컨텍스트 구성' : 'Context basket'} · {included.length}/{files.length} {ko ? '파일' : 'files'}</h3>
    <p className="context-basket-summary">{t('basket.summary', { min: Math.ceil(bytes / 4).toLocaleString(), max: bytes.toLocaleString() })}</p>
    <div className="context-basket-files">
      {clearing && <ConfirmDelete t={t} testId="basket-clear-confirm" yesTestId="basket-clear-yes" question={t('basket.files.removeQuestion', { count: shownFiles.length })}
        locked={disabled || !shownFiles.length} pending={false} onCancel={() => setClearing(false)} onConfirm={removeShown} />}
      <Toolbar><SearchBox value={fileQuery} onChange={setFileQuery} testId="basket-file-search" />
        <button type="button" className="btn small" data-testid="basket-clear" disabled={disabled || !shownFiles.length} onClick={() => setClearing(true)}><Icon name="trash" size={12} />{t('basket.files.remove', { count: shownFiles.length })}</button></Toolbar>
      <div ref={fileList} onDragOver={event => { event.preventDefault(); event.dataTransfer.dropEffect = !disabled && drag.current ? 'move' : 'none'; }} onDrop={event => drop(event)}>
        <DataTable columns="auto auto minmax(0, 1fr) auto" label={t('composer.attachments')} testId="basket-files" empty={!shownFiles.length} emptyRows={2}
          head={['', '', <label className="ui-check" key="all"><input type="checkbox" data-testid="basket-include-all" aria-label={t('basket.files.includeAll')} disabled={disabled || !shownFiles.length}
            checked={shownFiles.length > 0 && shownIncluded === shownFiles.length} ref={input => { if (input) input.indeterminate = shownIncluded > 0 && shownIncluded < shownFiles.length; }}
            onChange={event => setShownExcluded(!event.target.checked)} />{t('basket.files.column')}</label>, '']}>
          {pagedFiles.slice.map(file => <div className="ui-tr" role="row" key={file.fullPath} data-testid="basket-file" onDrop={event => drop(event, file.fullPath)}>
            <span role="cell" className="ui-cell ui-muted context-file-order">{files.indexOf(file) + 1}</span>
            <span role="cell" className="ui-cell"><button type="button" className="icon-btn context-file-handle" draggable={!disabled} disabled={disabled} aria-label={`${ko ? '순서 바꾸기' : 'Reorder'} ${file.path}`}
              onDragStart={event => {
                if (disabled) { event.preventDefault(); return; }
                const nonce = newId(); drag.current = { nonce, path: file.fullPath, signature, workspace: workspaceKey };
                event.dataTransfer.effectAllowed = 'move'; event.dataTransfer.setData(dragType, nonce);
              }} onDragEnd={() => { drag.current = null; }}>⠿</button></span>
            <span role="cell" className="ui-cell"><label className="ui-check context-file-path"><input type="checkbox" data-basket-check checked={!file.excluded} disabled={disabled} onKeyDown={moveCheck} onChange={() => dispatch({ type: 'attachmentToggle', fullPath: file.fullPath })} />
              <span className={file.excluded ? 'ui-muted' : undefined} tabIndex={-1} data-attachment-menu title={file.fullPath} {...chipHandlers(file)}>{revealControls(file.path)}</span></label></span>
            <Actions><IconButton icon="trash" tone="danger" label={t('tools.delete')} testId="basket-file-remove" disabled={disabled} onClick={() => dispatch({ type: 'attachmentRemove', fullPath: file.fullPath })} /></Actions>
          </div>)}
        </DataTable>
      </div>
      <Pager paged={pagedFiles} testId="basket-file-pager" />
    </div>
    <div className="context-basket-tools">
      <button type="button" className="btn small" disabled={disabled || searching || !workspaceKey || !text.trim()} onClick={suggest}><Icon name="search" size={12} />{ko ? '초안 관련 코드 찾기' : 'Find related code'}</button>
      <DraftTokenCounter text={text} root={workspaceKey} sessionId={sessionId} model={model} connected={connected} disabled={disabled} locale={locale} />
    </div>
    {usable.length > 0 && <ViewSection sectionKey="context-knowledge" title={t('tools.knowledge')} status={usable.filter(item => picked.has(item.id)).length}>
      <KnowledgeChecklist items={usable} checked={item => picked.has(item.id)} onToggle={(item, checked) => { void toggleKnowledge(item, checked); }}
        disabled={disabled || !connected || knowledge.pending} testId="basket-knowledge" />
      {knowledgeError && <p role="alert" data-testid="basket-knowledge-error">{revealControls(knowledgeError)}</p>}
    </ViewSection>}
    {searching && <p role="status">{ko ? '관련 코드 검색 중' : 'Searching related code'}</p>}
    {suggestions?.error && <p role="alert">{revealControls(suggestions.error)}</p>}
    {suggestions && !suggestions.error && <div>
      {suggestions.mode && <p>{revealControls(suggestions.mode)}</p>}
      {suggestions.notice && <p>{revealControls(suggestions.notice)}</p>}
      {suggestions.notices?.filter(Boolean).slice(0, 10).map((notice, i) => <p key={i}>{revealControls(notice)}</p>)}

      <ul>{candidates.map(file => <li key={file.fullPath}>
        <ContextKindBadge file={file} included={false} />{revealControls(file.path)}{file.symbol ? ` · ${revealControls(file.symbol)}` : ''}{file.line ? `:${file.line}` : ''}
        <button type="button" disabled={disabled} onClick={() => dispatch({ type: 'attachmentsAdd', files: [file] })}>{ko ? '추가' : 'Add'} {revealControls(file.path)}</button>
      </li>)}</ul>
    </div>}
    <ViewSection sectionKey="context-presets" title={t('section.presets')} status={data.presets?.length ?? 0} expanded={presetsOpen} bodyClassName="ui-flush"
      onExpandedChange={setPresetsOpen}
      actions={<>
        <button type="button" className="btn small" data-testid="preset-save-current" data-tools-dialog-fallback disabled={disabled || !files.length}
          onClick={() => { setDeleting(null); setError(''); setSaving({ name: '', useCurrent: true }); }}><Icon name="plus" size={12} />{t('basket.presets.saveCurrent')}</button>
        <SectionRefresh onClick={() => command('refresh')} disabled={!connected || disabled || busy} />
      </>}>
      {saving && <InPageDialog title={t(saving.preset ? 'basket.presets.editTitle' : 'basket.presets.saveTitle')} testId="preset-save-dialog" onClose={() => setSaving(null)} pending={writing}>
        <form className="ui-form tools-form" noValidate onSubmit={event => { event.preventDefault(); submitPreset(); }}>
          <Field label={t('basket.presets.name')}>{id => <input id={id} type="text" maxLength={64} value={saving.name} data-testid="preset-name" disabled={writing} onChange={event => setSaving({ ...saving, name: event.target.value })} />}</Field>
          {saving.preset && <ChoiceField label={t('basket.presets.useCurrent', { count: files.length })}><Switch checked={saving.useCurrent} label={t('basket.presets.useCurrent', { count: files.length })} showLabel={false}
            disabled={writing || !files.length} testId="preset-use-current" onChange={useCurrent => setSaving({ ...saving, useCurrent })} /></ChoiceField>}
          {error && <p role="alert" className="ui-err" data-testid="preset-save-error">{revealControls(error)}</p>}
          <FormActions><button type="button" className="btn" disabled={writing} onClick={() => setSaving(null)}>{t('tools.cancel')}</button>
            <button type="submit" className="btn primary" data-testid="preset-save" disabled={disabled || busy || !data.root || !saving.name.trim()}>{t(nameTaken ? 'basket.presets.overwrite' : 'tools.save')}</button></FormActions>
        </form>
      </InPageDialog>}
      {deleting && <ConfirmDelete t={t} testId="preset-delete-confirm" yesTestId="preset-delete-yes" question={t('basket.presets.deleteQuestion', { name: deleting.name })}
        locked={disabled || busy} pending={writing} onCancel={() => setDeleting(null)} onConfirm={() => command('delete', { id: deleting.id })} />}
      <Toolbar><SearchBox value={presetQuery} onChange={setPresetQuery} testId="preset-search" /></Toolbar>
      <DataTable columns="minmax(0, 1fr) auto auto" label={t('section.presets')} testId="preset-table" empty={!presets.length} emptyRows={2}
        head={[t('tcol.name'), t('basket.presets.files'), '']}>
        {pagedPresets.slice.map(preset => <div className="ui-tr" role="row" key={preset.id} data-testid={`preset-row-${preset.id}`}>
          <span role="cell" className="ui-cell" title={preset.files.map(file => file.path).join('\n')}><strong>{revealControls(preset.name)}</strong></span>
          <span role="cell" className="ui-cell ui-muted">{t('basket.presets.fileCount', { count: preset.files.length })}</span>
          <span role="cell" className="ui-cell ui-actions">
            <IconButton icon="download" label={t('basket.presets.load')} testId={`preset-load-${preset.id}`} disabled={disabled || busy} onClick={() => loadPreset(preset)} />
            <IconButton icon="edit" label={t('basket.presets.edit')} testId={`preset-edit-${preset.id}`} disabled={disabled || busy}
              onClick={() => { setDeleting(null); setError(''); setSaving({ name: preset.name, preset, useCurrent: false }); }} />
            <IconButton icon="trash" tone="danger" label={t('tools.delete')} testId={`preset-delete-${preset.id}`} disabled={disabled || busy} onClick={() => { setSaving(null); setDeleting(preset); }} />
          </span>
        </div>)}
      </DataTable>
      <Pager paged={pagedPresets} testId="preset-pager" />
    </ViewSection>
    {error && !saving && <p role="alert">{revealControls(error)}</p>}
    {renderBasketMenu()}
  </section>;
}
