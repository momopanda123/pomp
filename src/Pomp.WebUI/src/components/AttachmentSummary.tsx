import { useMemo, useState } from 'react';
import { copyText } from '../format';
import { useT } from '../i18n';
import { markMenuHandled, useContextMenu } from './ContextMenu';
import { Icon } from './Icon';
import { revealControls } from './RevealControls';
import { DataTable, matches, Pager, SearchBox, Toolbar, usePaged } from './ui';

/** A sent or queued message's attached files: their count, and on demand a searchable, paged list of the paths (a message can carry thousands). */
export function AttachmentSummary({ files, testId }: { files: readonly { path: string; fullPath?: string }[]; testId?: string }) {
  const t = useT();
  const [open, setOpen] = useState(false);
  const [query, setQuery] = useState('');
  const shown = useMemo(() => files.filter(file => matches(query, file.path, file.fullPath)), [files, query]);
  const paged = usePaged(shown, query, 10);
  const { menuProps, renderMenu } = useContextMenu();
  const label = t('composer.attachedCount', { count: files.length });
  return <div className="attachment-summary" data-testid={testId}>
    <button type="button" className="context-count filled" aria-expanded={open} aria-label={label} title={label} data-testid={testId && `${testId}-toggle`}
      onClick={() => setOpen(!open)}><Icon name="paperclip" size={12} /><span className="context-count-value">{files.length}</span></button>
    {open && <div className="attachment-summary-list">
      {files.length > 10 && <Toolbar><SearchBox value={query} onChange={setQuery} testId={testId && `${testId}-search`} /></Toolbar>}
      <DataTable columns="minmax(0, 1fr)" head={[t('basket.files.column')]} label={t('composer.attachments')} empty={!shown.length} emptyRows={1}>
        {paged.slice.map((file, index) => <div className="ui-tr" role="row" key={`${file.fullPath ?? file.path}:${index}`}>
          <span role="cell" className="ui-cell" title={file.fullPath ?? file.path} tabIndex={-1} data-attachment-menu onContextMenu={event => {
            menuProps(() => [{ id: 'copy-path', label: t('menu.copyPath'), testId: 'context-menu-copy-path', onSelect: () => { void copyText(file.fullPath ?? file.path); } }]).onContextMenu?.(event);
            if (event.defaultPrevented) markMenuHandled(event.nativeEvent);
          }}>{revealControls(file.path)}</span>
        </div>)}
      </DataTable>
      <Pager paged={paged} testId={testId && `${testId}-pager`} />
    </div>}
    {renderMenu()}
  </div>;
}
