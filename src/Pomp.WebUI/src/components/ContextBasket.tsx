import { useEffect, useMemo, useRef, useState, type Dispatch, type DragEvent } from 'react';
import { copyText, newId } from '../format';
import type { KnowledgeItem, WorkspaceFile, ModelInfo } from '../protocol';
import type { Action } from '../state';
import { DraftTokenCounter } from './DraftTokenCounter';
import { markMenuHandled, useContextMenu, type ContextMenuItem } from './ContextMenu';
import { revealControls } from './RevealControls';
import { useRefreshOnShow } from '../useRefreshOnShow';
import { ViewSection } from './ViewSection';
import { KnowledgeChecklist } from './KnowledgeChecklist';
import { usableKnowledge } from '../knowledge';
import { useKnowledge } from '../knowledgeState';
import { useT } from '../i18n';
import { Actions, DataTable, IconButton, Pager, usePaged } from './ui';

/**
 * The context basket: the files attached to the next message (sent once, as a path list) and the knowledge the conversation uses (ticked
 * defaults and picks, kept for every message of the conversation). Both are folding groups; the token count covers draft, files and knowledge.
 */
export function ContextBasket({ files, text, dispatch, locale, workspaceKey, sessionId, model, connected, disabled, visible = false }: {
  visible?: boolean; files: WorkspaceFile[]; text: string;
  dispatch: Dispatch<Action>; locale: string; workspaceKey?: string; sessionId?: string; model?: ModelInfo; connected: boolean; disabled: boolean;
}) {
  const t = useT();
  const ko = locale.toLowerCase().startsWith('ko');
  // The attached files, paged (a basket can hold thousands).
  const pagedFiles = usePaged(files, workspaceKey ?? '', 10);
  const drag = useRef<{ nonce: string; path: string; signature: string; workspace?: string } | null>(null);
  const signature = useMemo(() => JSON.stringify(files.map(file => file.fullPath)), [files]);
  const dragType = 'application/x-pomp-context-order';
  const drop = (event: DragEvent, beforePath?: string) => {
    event.preventDefault(); event.stopPropagation();
    const source = drag.current; drag.current = null;
    if (disabled || !source || source.workspace !== workspaceKey || source.signature !== signature ||
        event.dataTransfer.getData(dragType) !== source.nonce) return;
    dispatch({ type: 'attachmentReorder', fullPath: source.path, beforePath });
  };
  useEffect(() => { drag.current = null; }, [workspaceKey, signature, disabled]);
  const pathBytes = useMemo(() => new TextEncoder().encode(files.map(file => file.fullPath).join('\n')).length, [files]);
  const bytes = new TextEncoder().encode(text).length + pathBytes;
  // The knowledge the person ticks goes with every message of this conversation until it is unticked.
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
    <h3>{ko ? '컨텍스트 바구니' : 'Context basket'}</h3>
    <div className="context-basket-summary">
      <span>{t('basket.summary', { min: Math.ceil(bytes / 4).toLocaleString(), max: bytes.toLocaleString() })}</span>
      <DraftTokenCounter text={text} attachments={files.map(file => file.fullPath)} knowledge={knowledge.selected} root={workspaceKey} sessionId={sessionId} model={model} connected={connected} disabled={disabled} locale={locale} />
    </div>
    <ViewSection sectionKey="context-files" title={t('composer.attachments')} status={files.length} bodyClassName="ui-flush">
      <div onDragOver={event => { event.preventDefault(); event.dataTransfer.dropEffect = !disabled && drag.current ? 'move' : 'none'; }} onDrop={event => drop(event)}>
        <DataTable columns="auto auto minmax(0, 1fr) auto" label={t('composer.attachments')} testId="basket-files" empty={!files.length} emptyRows={1}>
          {pagedFiles.slice.map(file => <div className="ui-tr" role="row" key={file.fullPath} data-testid="basket-file" onDrop={event => drop(event, file.fullPath)}>
            <span role="cell" className="ui-cell ui-muted context-file-order">{files.indexOf(file) + 1}</span>
            <span role="cell" className="ui-cell"><button type="button" className="icon-btn context-file-handle" draggable={!disabled} disabled={disabled} aria-label={`${ko ? '순서 바꾸기' : 'Reorder'} ${file.path}`}
              onDragStart={event => {
                if (disabled) { event.preventDefault(); return; }
                const nonce = newId(); drag.current = { nonce, path: file.fullPath, signature, workspace: workspaceKey };
                event.dataTransfer.effectAllowed = 'move'; event.dataTransfer.setData(dragType, nonce);
              }} onDragEnd={() => { drag.current = null; }}>⠿</button></span>
            <span role="cell" className="ui-cell context-file-path"><span tabIndex={-1} data-attachment-menu title={file.fullPath} {...chipHandlers(file)}>{revealControls(file.path)}</span></span>
            <Actions><IconButton icon="x" label={t('menu.removeAttachment')} testId="basket-file-remove" disabled={disabled} onClick={() => dispatch({ type: 'attachmentRemove', fullPath: file.fullPath })} /></Actions>
          </div>)}
        </DataTable>
      </div>
      <Pager paged={pagedFiles} testId="basket-file-pager" />
    </ViewSection>
    {usable.length > 0 && <ViewSection sectionKey="context-knowledge" title={t('tools.knowledge')} status={usable.filter(item => picked.has(item.id)).length}>
      <KnowledgeChecklist items={usable} checked={item => picked.has(item.id)} onToggle={(item, checked) => { void toggleKnowledge(item, checked); }}
        disabled={disabled || !connected || knowledge.pending} testId="basket-knowledge" />
      {knowledgeError && <p role="alert" data-testid="basket-knowledge-error">{revealControls(knowledgeError)}</p>}
    </ViewSection>}
    {renderBasketMenu()}
  </section>;
}
