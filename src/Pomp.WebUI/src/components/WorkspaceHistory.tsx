import { useEffect, useRef, useState } from 'react';
import { useT } from '../i18n';
import { newId } from '../format';
import { onMessage, send } from '../protocol';
import { revealControls } from './RevealControls';

import type { WorkLayout } from './WorkPanel';
import { useWorkState } from '../work/store';
import { useRefreshOnShow } from '../useRefreshOnShow';
import { SectionRefresh, ViewSection } from './ViewSection';
import { Icon } from './Icon';
import { Actions, Badge, Cell, DataTable, IconButton, PAGE_SIZE, Pager, Row, SearchBox, Toolbar } from './ui';
import { stateLabel } from '../stateLabels';
/** Applied changes, one host page at a time (newest first, path search); records can be restored or deleted one by one or as a selection. */
export function WorkspaceHistory({ locale, inert, connected = true, onReview }: { locale: string; inert: boolean; connected?: boolean; layout?: WorkLayout; onReview?: () => void }) {
  const history = useWorkState('history');
  const [pending, setPending] = useState(false);
  const restoreRequest = useRef<string | undefined>(undefined);
  const deleteRequest = useRef<string | undefined>(undefined);
  const [deleting, setDeleting] = useState<string[] | undefined>(undefined);
  const [armed, setArmed] = useState<string | undefined>(undefined);
  const armTimer = useRef<number | undefined>(undefined);
  const [selected, setSelected] = useState<Set<string>>(new Set());
  const [query, setQuery] = useState(''); const [search, setSearch] = useState('');
  const [error, setError] = useState('');
  const ko = locale.toLowerCase().startsWith('ko'); const t = useT();
  const page = history.page ?? 0;
  useEffect(() => () => window.clearTimeout(armTimer.current), []);
  useEffect(() => { setSelected(new Set()); setArmed(undefined); }, [history.root]);
  useEffect(() => {
    const off = onMessage(message => {
      if (message.type === 'workspace/historyReply' && message.payload.requestId === restoreRequest.current && message.payload.root === history.root) {
        restoreRequest.current = undefined; setPending(false);
        if (message.payload.ok && message.payload.proposalId) { setSelected(new Set()); onReview?.(); }
        else setError(message.payload.error || (ko ? '제안을 만들지 못했어요.' : 'Could not create the proposal.'));
        return;
      }
      if (message.type === 'workspace/historyReply' && message.payload.requestId === deleteRequest.current) {
        deleteRequest.current = undefined;
        setDeleting(ids => { if (ids && message.payload.ok) setSelected(before => new Set([...before].filter(id => !ids.includes(id)))); return undefined; });
        if (!message.payload.ok) setError(message.payload.error || (ko ? '기록을 지우지 못했어요.' : 'Could not delete the record.'));
        return;
      }
      if (message.type !== 'workspace/historyState' || restoreRequest.current || deleteRequest.current) return;
      setPending(false);
      if (message.payload.error) { setError(message.payload.error); return; }
      if (!Array.isArray(message.payload.entries)) return;
      setError(message.payload.message ?? '');
    });
    return off;
  }, [history.root, onReview, ko]);
  useEffect(() => {
    if (!pending) return;
    const timer = window.setTimeout(() => { restoreRequest.current = undefined; setPending(false); setError(ko ? '결과를 받지 못했어요.' : 'No reply received.'); }, 120000);
    return () => window.clearTimeout(timer);
  }, [pending, ko]);
  useEffect(() => {
    if (!deleting) return;
    const timer = window.setTimeout(() => { deleteRequest.current = undefined; setDeleting(undefined); setError(ko ? '결과를 받지 못했어요.' : 'No reply received.'); }, 30000);
    return () => window.clearTimeout(timer);
  }, [deleting, ko]);
  const refresh = (nextPage = page, nextQuery = search) => {
    if (inert || pending) return;
    if (send('workspace/history', { action: 'refresh', root: history.root, page: nextPage, size: PAGE_SIZE, ...(nextQuery ? { query: nextQuery } : {}) })) setError('');
    else setError(t('error.hostUnavailable'));
  };
  useRefreshOnShow(connected && !inert, () => refresh());
  useEffect(() => {
    const timer = window.setTimeout(() => { const next = query.trim(); if (next !== search) { setSearch(next); refresh(0, next); } }, 250);
    return () => window.clearTimeout(timer);
  // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [query]);
  function restore(ids: string[]) {
    if (inert || pending || !ids.length) return;
    const requestId = newId();
    if (send('workspace/history', { action: 'restore', ids, root: history.root, requestId })) { restoreRequest.current = requestId; setPending(true); setError(''); }
    else setError(t('error.hostUnavailable'));
  }
  // Deleting is pressed twice: the first press arms the row (or the selection) for ten seconds, the second sends.
  function remove(key: string, ids: string[]) {
    if (inert || deleting || !ids.length) return;
    window.clearTimeout(armTimer.current);
    if (armed !== key) { setArmed(key); armTimer.current = window.setTimeout(() => setArmed(current => (current === key ? undefined : current)), 10000); return; }
    setArmed(undefined);
    const requestId = newId();
    if (send('workspace/history', { action: 'delete', ids, root: history.root, requestId })) { deleteRequest.current = requestId; setDeleting(ids); setError(''); }
    else setError(t('error.hostUnavailable'));
  }
  const entries = history.entries ?? [];
  const total = history.total ?? entries.length; const pages = Math.max(1, Math.ceil(total / PAGE_SIZE));
  const paged = { page, pages, total, from: total ? page * PAGE_SIZE + 1 : 0, to: Math.min(total, page * PAGE_SIZE + entries.length), setPage: (next: number) => refresh(next) };
  const chosen = [...selected]; const restorable = entries.filter(entry => selected.has(entry.id) && entry.state === 'applied').map(entry => entry.id);
  const pageIds = entries.map(entry => entry.id); const allOnPage = pageIds.length > 0 && pageIds.every(id => selected.has(id));
  const toggle = (id: string, on: boolean) => setSelected(before => { const next = new Set(before); if (on) next.add(id); else next.delete(id); return next; });
  const fileLabel = (files: string[]) => files.length ? files[0].split(/[\\/]/).pop() + (files.length > 1 ? (ko ? ` 외 ${files.length - 1}` : ` +${files.length - 1}`) : '') : '';
  const busy = inert || !connected || pending || !!deleting;
  return <ViewSection sectionKey="work-history" className="workspace-history" headingLevel="h3" inert={inert || undefined} bodyClassName="ui-flush"
    title={ko ? '적용 기록' : 'Applied changes'} status={total}
    actions={<SectionRefresh onClick={() => refresh()} disabled={inert || !connected || pending} testId="history-refresh" />}>
    <Toolbar>
      <SearchBox value={query} onChange={setQuery} label={ko ? '파일 검색' : 'Search files'} testId="history-search" />
      <span className="ui-grow" />
      <button type="button" className="btn small subtle" data-testid="history-restore-selected" disabled={busy || !restorable.length} onClick={() => restore(restorable)}>
        <Icon name="undo" size={12} />{ko ? `되돌리기 검토 ${restorable.length}` : `Review undo ${restorable.length}`}</button>
      {armed === 'selection'
        ? <button type="button" className="btn small subtle ui-danger-confirm" data-testid="history-delete-selected-confirm" disabled={busy} onClick={() => remove('selection', chosen)}>{ko ? `삭제 확인 ${chosen.length}` : `Confirm ${chosen.length}`}</button>
        : <button type="button" className="btn small subtle" data-testid="history-delete-selected" disabled={busy || !chosen.length} onClick={() => remove('selection', chosen)}><Icon name="trash" size={12} />{ko ? `삭제 ${chosen.length}` : `Delete ${chosen.length}`}</button>}
    </Toolbar>
    <DataTable columns="16px minmax(76px, max-content) minmax(0, 1fr) auto auto" label={ko ? '적용 기록' : 'Applied changes'} testId="history-table" empty={!entries.length}
      head={[<input key="all" type="checkbox" aria-label={ko ? '이 쪽 모두 선택' : 'Select this page'} checked={allOnPage} disabled={busy || !pageIds.length} data-testid="history-select-page"
        onChange={event => setSelected(before => { const next = new Set(before); for (const id of pageIds) { if (event.target.checked) next.add(id); else next.delete(id); } return next; })} />,
        ko ? '시각' : 'Time', ko ? '파일' : 'Files', ko ? '상태' : 'State', '']}>
      {entries.map(entry => <Row key={entry.id} testId={`history-${entry.id}`} selected={selected.has(entry.id)}>
        <Cell><input type="checkbox" aria-label={ko ? `${fileLabel(entry.files)} 선택` : `Select ${fileLabel(entry.files)}`} checked={selected.has(entry.id)} disabled={busy}
          data-testid={`history-select-${entry.id}`} onChange={event => toggle(entry.id, event.target.checked)} /></Cell>
        <Cell className="ui-muted">{entry.createdUtc ? new Date(entry.createdUtc).toLocaleString(locale, { month: 'numeric', day: 'numeric', hour: '2-digit', minute: '2-digit' }) : ''}</Cell>
        <Cell title={entry.files.join('\n')} className="ui-mono">{revealControls(fileLabel(entry.files))}{entry.error && <span className="ui-sub ui-err">{revealControls(entry.error)}</span>}</Cell>
        <Cell><Badge tone={entry.state === 'applied' ? 'ok' : entry.state === 'restored' ? 'info' : 'neutral'} title={entry.state}>{entry.state === 'restored' ? (ko ? '되돌림' : 'Restored') : revealControls(stateLabel(entry.state, locale))}</Badge></Cell>
        <Actions>
          <IconButton icon="undo" label={ko ? '되돌리기 검토' : 'Review undo'} disabled={busy || entry.state !== 'applied'} onClick={() => restore([entry.id])} testId={`history-restore-${entry.id}`} />
          {(armed === entry.id
            ? <button type="button" className="btn small subtle ui-danger-confirm" data-testid={`history-delete-confirm-${entry.id}`} disabled={!!deleting} onClick={() => remove(entry.id, [entry.id])}>{ko ? '삭제 확인' : 'Confirm'}</button>
            : <IconButton icon="trash" tone="danger" label={deleting?.includes(entry.id) ? (ko ? '지우는 중' : 'Deleting') : (ko ? '기록 삭제' : 'Delete record')} disabled={!!deleting} onClick={() => remove(entry.id, [entry.id])} testId={`history-delete-${entry.id}`} />)}
        </Actions>
      </Row>)}
    </DataTable>
    <Pager paged={paged} testId="history-pager" />
    {pending && <p className="ui-note ui-muted" role="status">{ko ? '확인 중' : 'Loading'}</p>}
    {error && <p className="ui-note" role="alert">{revealControls(error)}</p>}
  </ViewSection>;
}
