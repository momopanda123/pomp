import { useEffect, useRef, useState } from 'react';
import { useT } from '../i18n';
import { onMessage, send, type IsolationResult, type UiToHost } from '../protocol';
import { newId } from '../format';
import { revealControls } from './RevealControls';
import type { WorkLayout } from './WorkPanel';
import { ViewSection } from './ViewSection';
import { Icon } from './Icon';
import { Actions, Badge, Cell, DataTable, IconButton, Pager, Row, usePaged, type BadgeTone } from './ui';
import { useWorkState } from '../work/store';
import { useRefreshOnShow } from '../useRefreshOnShow';
import { stateLabel } from '../stateLabels';
import '../styles/work-tab.css';

const REPLY_TIMEOUT_MS = 15000;
const COLUMNS = 'minmax(0, 1fr) auto auto auto';

/**
 * Results of isolated team members (docs/agent-teams.md §13). Nothing here writes files: Apply asks the host, which shows
 * its approval card and writes after approval with a checkpoint. One request at a time; pushes refresh the list only.
 */
export function IsolationResultsPanel({ locale, inert, connected = true }: { locale: string; inert: boolean; connected?: boolean; layout?: WorkLayout }) {
  const items = useWorkState('isolation').filter(item => item.status !== 'applied'); const hidden = useWorkState('isolationHidden');
  const paged = usePaged(items);
  const ko = locale.toLowerCase().startsWith('ko'); const t = useT();
  const [pending, setPending] = useState<string | null>(null);
  const [error, setError] = useState('');
  const [discarding, setDiscarding] = useState<string>();
  const [open, setOpen] = useState(new Set<string>());
  const confirm = useRef<HTMLButtonElement>(null);
  useEffect(() => { if (discarding) confirm.current?.focus(); }, [discarding]);
  useEffect(() => onMessage(message => {
    if (message.type === 'workspace/isolationState' && message.payload.requestId != null && message.payload.requestId === pending) setPending(null);
  }), [pending]);
  useEffect(() => {
    if (!pending) return;
    const timer = window.setTimeout(() => { setPending(null); setError(t('setup.noReply')); }, REPLY_TIMEOUT_MS);
    return () => window.clearTimeout(timer);
  }, [pending, t]);
  useEffect(() => { if (discarding && !items.some(item => item.id === discarding)) setDiscarding(undefined); }, [items, discarding]);
  const command = (action: UiToHost['workspace/isolation']['action'], item?: IsolationResult) => {
    if (!connected || inert || pending) return;
    const requestId = newId();
    if (send('workspace/isolation', { action, requestId, ...(item ? { id: item.id, root: item.root } : {}) })) { setPending(requestId); setError(''); }
    else setError(t('error.hostUnavailable'));
  };
  useRefreshOnShow(connected && !inert, () => command('refresh'));
  // Keeping the result returns the focus to its discard button, so the keyboard user stays where they were.
  const keepDiscard = (from: HTMLElement) => {
    const button = from.closest('[data-testid="isolation-item"]')?.querySelector<HTMLButtonElement>('[data-testid="isolation-discard"]');
    setDiscarding(undefined); button?.focus();
  };
  if (!items.length && !hidden) return null;
  const kind: Record<IsolationResult['files'][number]['kind'], [string, BadgeTone]> = { create: [ko ? '새 파일' : 'New', 'ok'], delete: [ko ? '삭제' : 'Deleted', 'error'], modify: [ko ? '수정' : 'Modified', 'warn'] };
  const tone = (status: IsolationResult['status']): BadgeTone => status === 'pending' ? 'info' : status === 'blocked' || status === 'failed' ? 'error' : status === 'applying' ? 'warn' : 'neutral';
  const busy = !!pending || items.some(item => item.status === 'applying');
  const toggle = (id: string) => setOpen(before => { const next = new Set(before); if (next.has(id)) next.delete(id); else next.add(id); return next; });
  return <ViewSection sectionKey="work-isolation" className="isolation-results" headingLevel="h2" title={ko ? '격리 실행 결과' : 'Isolated results'} bodyClassName="ui-flush" inert={inert || undefined}
    status={items.filter(item => item.status === 'pending' || item.status === 'blocked').length}>
    <DataTable columns={COLUMNS} label={ko ? '격리 실행 결과' : 'Isolated results'} head={[ko ? '구성원' : 'Member', ko ? '상태' : 'Status', ko ? '줄' : 'Lines', '']}>
      {paged.slice.map(item => {
        const shown = open.has(item.id) || item.status === 'blocked' || item.status === 'failed';
        const added = item.files.reduce((n, file) => n + file.added, 0); const removed = item.files.reduce((n, file) => n + file.removed, 0);
        return <div key={item.id} role="rowgroup" data-testid="isolation-item" data-status={item.status}>
          <Row>
            <Cell><button type="button" className="review-file-toggle" aria-expanded={shown} onClick={() => toggle(item.id)}>
              <Icon name={shown ? 'chevronDown' : 'chevron'} size={12} /><strong>{revealControls(item.agent || (ko ? '구성원' : 'member'))}</strong>
            </button><span className="ui-sub">{ko ? `파일 ${item.files.length}개` : `${item.files.length} files`}{item.createdAt ? ` · ${new Date(item.createdAt).toLocaleString(locale, { month: 'numeric', day: 'numeric', hour: '2-digit', minute: '2-digit' })}` : ''}</span></Cell>
            <Cell><Badge tone={tone(item.status)} title={item.status}>{stateLabel(item.status, locale)}</Badge></Cell>
            <Cell className="ui-mono"><span className="diff-add">+{added}</span> <span className="diff-del">−{removed}</span></Cell>
            <Actions>
              <IconButton icon="check" label={ko ? '적용' : 'Apply'} testId="isolation-apply" disabled={busy || item.status !== 'pending'} onClick={() => command('apply', item)} />
              <IconButton icon="external" label={ko ? '패치 열기' : 'Open patch'} testId="isolation-open" disabled={!!pending} onClick={() => command('openPatch', item)} />
              <IconButton icon="trash" tone="danger" label={item.status === 'pending' ? (ko ? '결과 버리기' : 'Discard result') : (ko ? '목록에서 지우기' : 'Remove from list')} testId="isolation-discard"
                disabled={!!pending || item.status === 'applying'} onClick={() => { if (item.status === 'pending') setDiscarding(item.id); else command('discard', item); }} />
            </Actions>
          </Row>
          {item.message && <div className="ui-note" role={item.status === 'blocked' || item.status === 'failed' ? 'alert' : 'status'}>{revealControls(item.message)}</div>}
          {discarding === item.id && <div className="ui-card-foot" role="group" data-testid="isolation-discard-confirm" onKeyDown={event => {
            if (event.key === 'Escape' && !event.nativeEvent.isComposing) { event.preventDefault(); event.stopPropagation(); keepDiscard(event.currentTarget); }
          }}>
            <span className="ui-grow">{ko ? '적용하지 않은 결과를 버릴까요?' : 'Discard this unapplied result?'}</span>
            <button type="button" className="btn" onClick={event => keepDiscard(event.currentTarget)}>{ko ? '취소' : 'Cancel'}</button>
            <button type="button" ref={confirm} className="btn danger" data-testid="isolation-discard-yes" disabled={!!pending} onClick={() => { setDiscarding(undefined); command('discard', item); }}>{ko ? '버리기' : 'Discard'}</button>
          </div>}
          {shown && <IsolationFiles files={item.files} kind={kind} />}
        </div>;
      })}
    </DataTable>
    <Pager paged={paged} testId="isolation-pager" />
    {hidden > 0 && <p className="ui-note ui-muted" role="status" data-testid="isolation-hidden">{ko ? `${hidden}개 결과는 목록이 너무 커서 표시하지 않아요.` : `${hidden} results are not shown: the list is too large.`}</p>}
    {error && <p className="ui-note" role="alert">{revealControls(error)}</p>}
  </ViewSection>;
}

/** The files of one result, 20 a page. */
function IsolationFiles({ files, kind }: { files: IsolationResult['files']; kind: Record<IsolationResult['files'][number]['kind'], [string, BadgeTone]> }) {
  const paged = usePaged(files);
  return <div className="isolation-files">{paged.slice.map(file => <div key={file.path} className="isolation-file">
    <Icon name="file" size={13} className="ui-muted" /><span className="ui-mono isolation-path" title={file.path}>{revealControls(file.path)}</span>
    <Badge tone={kind[file.kind][1]}>{kind[file.kind][0]}</Badge>
    <span className="ui-mono"><span className="diff-add">+{file.added}</span> <span className="diff-del">−{file.removed}</span></span>
  </div>)}<Pager paged={paged} testId="isolation-files-pager" /></div>;
}
