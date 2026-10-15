import { useEffect, useRef, useState } from 'react';
import { onMessage, send, type UiToHost, type WorkspaceReviewState } from '../protocol';
import { revealControls } from './RevealControls';
import type { WorkLayout } from './WorkPanel';
import { ViewSection } from './ViewSection';
import { Icon } from './Icon';
import { Actions, Badge, Cell, DataTable, Field, FormActions, IconButton, byName, matches, Pager, Row, SearchBox, Segmented, Toolbar, usePaged, type BadgeTone } from './ui';
import { newId } from '../format';
import { useWorkState } from '../work/store';
import { useRefreshOnShow } from '../useRefreshOnShow';
import { toolsTranslate } from '../i18n/toolsStrings';
import { stateLabel } from '../stateLabels';
import '../styles/work-tab.css';

interface HunkDraft { id: string; revision: number; path: string; hunkId: number; original: string; after: string }
type ReviewFile = WorkspaceReviewState['files'][number];

const COLUMNS = '16px minmax(0, 1fr) auto auto auto';
const lineCount = (text: string) => text ? (text.match(/\r\n|[\r\n\u0085\u2028\u2029]/g)?.length ?? 0) + (/[\r\n\u0085\u2028\u2029]$/.test(text) ? 0 : 1) : 0;
const fileKind = (file: ReviewFile): 'create' | 'delete' | 'modify' =>
  file.hunks.length && file.hunks.every(h => !h.before && h.oldLine <= 1) ? 'create' : file.hunks.length && file.hunks.every(h => !h.after) ? 'delete' : 'modify';
/** The path below the workspace root when it is inside it; otherwise the full path. */
export function relativePath(root: string | undefined, path: string): string {
  const base = (root ?? '').replace(/[\\/]+$/, '');
  return base && path.toLowerCase().startsWith(base.toLowerCase()) && /[\\/]/.test(path.charAt(base.length)) ? path.slice(base.length + 1) : path;
}

const MAX_EDITOR_FILES = 10;
/**
 * The user's own edits in VS as a proposal: the host lists files whose editor or disk text differs from git HEAD (or from the saved file),
 * the user picks up to ten and the host stages them; the proposal then arrives through workspace/reviewState like any other.
 */
function EditorChangesPicker({ locale, root, inert, onClose }: { locale: string; root?: string; inert: boolean; onClose: () => void }) {
  const ko = locale.toLowerCase().startsWith('ko'); const t = toolsTranslate(locale);
  const [files, setFiles] = useState<{ path: string; unsaved: boolean }[] | null>(null);
  const [chosen, setChosen] = useState<Set<string>>(new Set()); const [query, setQuery] = useState('');
  const [staging, setStaging] = useState(false); const [error, setError] = useState('');
  const listRequest = useRef<string | undefined>(undefined); const stageRequest = useRef<string | undefined>(undefined);
  useEffect(() => {
    const off = onMessage(message => {
      if (message.type !== 'workspace/editorChanges') return;
      if (message.payload.requestId === listRequest.current) {
        listRequest.current = undefined;
        if (message.payload.ok) setFiles(message.payload.files ?? []); else { setFiles([]); setError(message.payload.error ?? (ko ? '편집기 변경을 읽을 수 없어요.' : 'Editor changes are unavailable.')); }
      } else if (message.payload.requestId === stageRequest.current) {
        stageRequest.current = undefined; setStaging(false);
        if (message.payload.ok) onClose(); else setError(message.payload.error ?? (ko ? '제안을 만들지 못했어요.' : 'Could not create the proposal.'));
      }
    });
    const id = newId(); listRequest.current = id;
    if (!send('workspace/review', { action: 'editorChanges', requestId: id })) { listRequest.current = undefined; setFiles([]); setError(t('tools.hostUnavailable')); }
    return off;
  // eslint-disable-next-line react-hooks/exhaustive-deps
  }, []);
  const shown = (files ?? []).filter(file => matches(query, relativePath(root, file.path))).sort((a, b) => byName(relativePath(root, a.path), relativePath(root, b.path)));
  const paged = usePaged(shown, query);
  const stage = () => {
    if (!chosen.size || staging || inert) return;
    const id = newId(); stageRequest.current = id;
    if (send('workspace/review', { action: 'fromEditor', requestId: id, paths: [...chosen] })) { setStaging(true); setError(''); }
    else { stageRequest.current = undefined; setError(t('tools.hostUnavailable')); }
  };
  const toggle = (path: string, on: boolean) => setChosen(before => { const next = new Set(before); if (on) next.add(path); else next.delete(path); return next; });
  return <div className="editor-changes" data-testid="editor-changes">
    <Toolbar>
      <SearchBox value={query} onChange={setQuery} label={ko ? '파일 검색' : 'Search files'} testId="editor-changes-search" />
      <span className="ui-grow" /><span className="ui-muted ui-mono" data-testid="editor-changes-count">{chosen.size}/{MAX_EDITOR_FILES}</span>
    </Toolbar>
    <DataTable columns="16px minmax(0, 1fr) auto" label={ko ? '편집기 변경' : 'Editor changes'} testId="editor-changes-files" empty={!paged.slice.length} head={['', ko ? '파일' : 'File', '']}>
      {paged.slice.map(file => {
        const relative = relativePath(root, file.path); const slash = Math.max(relative.lastIndexOf('\\'), relative.lastIndexOf('/')); const on = chosen.has(file.path);
        return <Row key={file.path} selected={on} testId="editor-changes-file">
          <Cell><input type="checkbox" aria-label={ko ? `${relative} 선택` : `Select ${relative}`} checked={on} disabled={staging || inert || (!on && chosen.size >= MAX_EDITOR_FILES)} onChange={event => toggle(file.path, event.target.checked)} /></Cell>
          <Cell title={file.path}><span className="ui-mono">{revealControls(slash >= 0 ? relative.slice(slash + 1) : relative)}</span>{slash > 0 && <span className="ui-sub ui-mono">{revealControls(relative.slice(0, slash))}</span>}</Cell>
          <Cell>{file.unsaved && <Badge tone="warn">{ko ? '저장 안 함' : 'Unsaved'}</Badge>}</Cell>
        </Row>;
      })}
    </DataTable>
    <Pager paged={paged} testId="editor-changes-pager" />
    {files === null && <p className="ui-note ui-muted" role="status">{ko ? '확인 중' : 'Loading'}</p>}
    {error && <p className="ui-note" role="alert">{revealControls(error)}</p>}
    <div className="ui-card-foot">
      <span className="ui-grow" />
      <button type="button" className="btn" disabled={staging} onClick={onClose}>{t('tools.cancel')}</button>
      <button type="button" className="btn primary" data-testid="editor-changes-stage" disabled={inert || staging || !chosen.size} onClick={stage}>{ko ? `제안 만들기 ${chosen.size}` : `Create proposal ${chosen.size}`}</button>
    </div>
  </div>;
}

/** All proposal edits remain revision-bound and require fresh selections/validation before application. */
export function WorkspaceReview({ locale, inert, connected = true, root, busy: agentBusy = false, onShowApplied }: { locale: string; inert: boolean; connected?: boolean; layout?: WorkLayout; root?: string; busy?: boolean; onShowApplied?: () => void }) {
  const review = useWorkState('review');
  const [pending, setPending] = useState(false);
  // Which of the pending requests is a discard / an apply. A discard is posted once; the next review state answers it.
  const [discardAsked, setDiscardAsked] = useState(false); const [applyAsked, setApplyAsked] = useState(false);
  const discardPosted = useRef(false);
  const [error, setError] = useState('');
  const [filter, setFilter] = useState('');
  const [allowSourceGenerators, setAllowSourceGenerators] = useState(false);
  const [layout, setLayout] = useState<'inline' | 'split'>('inline');
  const [editing, setEditing] = useState<HunkDraft>();
  const [collapsed, setCollapsed] = useState(new Set<string>());
  const [discarding, setDiscarding] = useState<{ id: string; revision: number }>();
  const [picking, setPicking] = useState(false);
  const discardButton = useRef<HTMLButtonElement>(null); const discardConfirm = useRef<HTMLButtonElement>(null);
  useEffect(() => { setDiscarding(undefined); }, [review?.id, review?.revision, review?.status]);
  useEffect(() => { if (discarding) discardConfirm.current?.focus(); }, [discarding]);
  const t = toolsTranslate(locale);
  const amendSent = useRef<HunkDraft | undefined>(undefined);
  const panel = useRef<HTMLElement>(null);
  const latest = useRef(review); latest.current = review;
  useEffect(() => { setFilter(''); setAllowSourceGenerators(false); setEditing(undefined); setCollapsed(new Set()); amendSent.current = undefined; }, [review?.id]);
  const ko = locale.toLowerCase().startsWith('ko');
  useEffect(() => {
    const off = onMessage(message => {
      if (message.type !== 'workspace/reviewState') return;
      const state = message.payload;
      if (typeof state.id !== 'string' || !Number.isSafeInteger(state.revision) || !Array.isArray(state.files) || !Array.isArray(state.diagnostics) || !Array.isArray(state.outcomes)) return;
      const sent = amendSent.current;
      if (sent && state.id === sent.id && state.revision > sent.revision) setEditing(undefined);
      amendSent.current = undefined;
      discardPosted.current = false; setDiscardAsked(false); setApplyAsked(false);
      setPending(false); setError('');
    });
    return off;
  }, []);
  const refresh = () => { if (connected && !inert) send('workspace/review', { action: 'refresh' }); };
  useRefreshOnShow(connected && !inert, refresh);

  const open = !!review?.id && !['done', 'empty', 'discarded', 'cancelled'].includes(review.status);
  const applied = new Set(review?.outcomes.filter(item => item.state === 'applied').map(item => item.path.toLowerCase()));
  const files = open ? review!.files.filter(file => file.hunks.length > 0 && !applied.has(file.path.toLowerCase())) : [];
  const head = ['', ko ? '파일' : 'File', ko ? '종류' : 'Kind', ko ? '줄' : 'Lines', ''];
  const title = ko ? '변경 제안' : 'Proposed changes';
  // The agent's turn is still running: the view says so instead of showing an empty list as the final word.
  const working = agentBusy ? <span className="ui-working" role="status" data-testid="review-working"><span className="spinner" aria-hidden="true" />{t('review.working')}</span> : null;
  // The last proposal was applied: its outcome stays here (with the way to the applied records) until the next proposal.
  const appliedCount = review?.status === 'done' ? review.outcomes.filter(item => item.state === 'applied').length : 0;
  const finished = review?.status === 'done' && (review.message !== '' || appliedCount > 0);
  if (!open || !review) return <ViewSection sectionKey="work-review" className="workspace-review" headingLevel="h2" title={title} status={0} bodyClassName="ui-flush" inert={inert || undefined}
    actions={<>{working}<button type="button" className="btn small" data-testid="review-from-editor" disabled={!connected || inert || picking} onClick={() => setPicking(true)}><Icon name="plus" size={12} />{ko ? '편집기 변경 가져오기' : 'Import editor changes'}</button></>}>
    {finished && review && !picking && <div className="ui-card-foot review-outcome" role="status" data-testid="review-outcome">
      <Badge tone="ok" title={review.status}>{revealControls(stateLabel(review.status, locale))}</Badge>
      <span className="ui-grow">{revealControls(review.message)}</span>
      {appliedCount > 0 && onShowApplied && <button type="button" className="btn small" data-testid="review-show-applied" disabled={inert} onClick={onShowApplied}>{t('review.showApplied')}</button>}
    </div>}
    {picking ? <EditorChangesPicker locale={locale} root={root} inert={inert || !connected} onClose={() => setPicking(false)} /> : <DataTable columns={COLUMNS} label={title} testId="review-files" empty head={head} />}
  </ViewSection>;

  const busy = pending || ['validating', 'approving', 'applying'].includes(review.status);
  const applying = review.status === 'applying';
  const locked = busy || !!editing || inert;
  const cancelDiscard = () => { setDiscarding(undefined); discardButton.current?.focus(); };
  const command = (action: UiToHost['workspace/review']['action'], extra: Partial<UiToHost['workspace/review']> = {}) => {
    const current = latest.current;
    if (!current || inert || (pending && action !== 'discard') || (editing && action !== 'amend' && action !== 'discard')) return false;
    // Discard stays possible while another request hangs (it is the way out), but only once, and never during the apply itself: the host refuses it there.
    if (action === 'discard' && (discardPosted.current || current.status === 'applying')) return false;
    if (send('workspace/review', { action, id: current.id, revision: current.revision, ...extra })) {
      setPending(true);
      if (action === 'discard') { discardPosted.current = true; setDiscardAsked(true); }
      if (action === 'apply') setApplyAsked(true);
      return true;
    }
    setError(t('tools.hostUnavailable')); return false;
  };
  const selectedCount = files.reduce((n, file) => n + file.hunks.filter(h => h.selected).length, 0);
  const visibleFiles = files.filter(file => relativePath(root, file.path).toLowerCase().includes(filter.trim().toLowerCase()));
  const totals = files.flatMap(file => file.hunks.filter(h => h.selected)).reduce((acc, h) => ({ added: acc.added + lineCount(h.after), removed: acc.removed + lineCount(h.before) }), { added: 0, removed: 0 });
  const kindBadge: Record<ReturnType<typeof fileKind>, [string, BadgeTone]> = { create: [ko ? '새 파일' : 'New', 'ok'], delete: [ko ? '삭제' : 'Deleted', 'error'], modify: [ko ? '수정' : 'Modified', 'warn'] };
  const toggleOpen = (path: string) => setCollapsed(before => { const next = new Set(before); if (next.has(path)) next.delete(path); else next.add(path); return next; });
  const statusTone: BadgeTone = review.status === 'ready' || review.status === 'validated' ? 'ok' : busy ? 'info' : review.status === 'failed' || review.status === 'blocked' ? 'error' : 'neutral';
  return <ViewSection sectionKey="work-review" className={`workspace-review review-layout-${layout}`} headingLevel="h2" title={title} bodyClassName="ui-flush" inert={inert || undefined}
    panelRef={panel} active={busy} aria-label={title}
    status={<span data-testid="review-count">{files.length}</span>}
    actions={<>{working}<button type="button" className="btn small subtle" data-testid="review-validate" disabled={locked || selectedCount === 0} onClick={() => command('validate', { allowSourceGenerators })}><Icon name="check" size={12} />{ko ? '컴파일 검사' : 'Validate'}</button></>}
    onKeyDown={event => {
      if (event.key !== 'F7' || inert || busy || editing) return;
      const controls = Array.from(panel.current?.querySelectorAll<HTMLInputElement>('input[data-review-hunk]:not(:disabled)') ?? []);
      if (!controls.length) return;
      event.preventDefault();
      const current = controls.indexOf(document.activeElement as HTMLInputElement);
      const index = current < 0 ? (event.shiftKey ? controls.length - 1 : 0) : (current + (event.shiftKey ? -1 : 1) + controls.length) % controls.length;
      controls[index].focus(); controls[index].scrollIntoView?.({ block: 'nearest' });
    }}>
    <Toolbar>
      <SearchBox value={filter} onChange={setFilter} label={ko ? '파일 검색' : 'Search files'} testId="review-filter" />
      <Segmented<'inline' | 'split'> label={ko ? '비교 방식' : 'Comparison'} value={layout} onChange={setLayout} options={[
        { id: 'inline', label: ko ? '위아래' : 'Stacked', testId: 'review-layout-inline' }, { id: 'split', label: ko ? '나란히' : 'Side by side', testId: 'review-layout-split' }]} />
      <span className="review-totals ui-mono" aria-label={ko ? '선택한 줄' : 'Selected lines'}><span className="diff-add">+{totals.added}</span> <span className="diff-del">−{totals.removed}</span></span>
    </Toolbar>
    <DataTable columns={COLUMNS} label={title} testId="review-files" empty={!visibleFiles.length} head={head}>
      {visibleFiles.map(file => {
        const chosen = file.hunks.filter(h => h.selected).length; const shown = !collapsed.has(file.path);
        const relative = relativePath(root, file.path); const slash = Math.max(relative.lastIndexOf('\\'), relative.lastIndexOf('/'));
        const [kindLabel, kindTone] = kindBadge[fileKind(file)];
        const added = file.hunks.reduce((n, h) => n + lineCount(h.after), 0); const removed = file.hunks.reduce((n, h) => n + lineCount(h.before), 0);
        return <div key={file.path} role="rowgroup" data-testid="review-file">
          <Row selected={chosen > 0}>
            <Cell><input type="checkbox" aria-label={ko ? `${relative} 모두 선택` : `Select all in ${relative}`} checked={chosen === file.hunks.length} disabled={locked}
              ref={element => { if (element) element.indeterminate = chosen > 0 && chosen < file.hunks.length; }}
              onChange={event => command('select', { path: file.path, selected: event.target.checked ? file.hunks.map(h => h.id) : [] })} /></Cell>
            <Cell title={file.path}><button type="button" className="review-file-toggle" aria-expanded={shown} onClick={() => toggleOpen(file.path)}>
              <Icon name={shown ? 'chevronDown' : 'chevron'} size={12} /><span className="ui-mono">{revealControls(slash >= 0 ? relative.slice(slash + 1) : relative)}</span>
            </button>{slash > 0 && <span className="ui-sub ui-mono">{revealControls(relative.slice(0, slash))}</span>}</Cell>
            <Cell><Badge tone={kindTone}>{kindLabel}{file.coarse ? (ko ? ' · 대용량' : ' · large') : ''}</Badge></Cell>
            <Cell className="ui-mono"><span className="diff-add">+{added}</span> <span className="diff-del">−{removed}</span></Cell>
            <Actions>{review.nativeDiffAvailable && <IconButton icon="external" label={ko ? 'VS에서 비교' : 'Compare in VS'} disabled={locked} onClick={() => command('nativeDiff', { path: file.path })} />}</Actions>
          </Row>
          {shown && <div className="review-hunks">
            {file.hunks.map(hunk => {
              const isEditing = editing?.path === file.path && editing.hunkId === hunk.id;
              return <div key={hunk.id} className={`review-hunk${hunk.selected ? ' selected' : ''}`}>
                <div className="review-hunk-head">
                  <label className="ui-check"><input type="checkbox" data-review-hunk checked={hunk.selected} disabled={locked}
                    onChange={event => command('select', { path: file.path, selected: file.hunks.filter(h => h.id === hunk.id ? event.target.checked : h.selected).map(h => h.id) })} />
                    <span className="ui-muted">{ko ? '줄' : 'Line'} {hunk.oldLine} → {hunk.newLine}</span></label>
                  {!isEditing && <IconButton icon="edit" label={ko ? '이 변경 편집' : 'Edit this change'} disabled={locked || hunk.after.length > 24000}
                    onClick={() => setEditing({ id: review.id, revision: review.revision, path: file.path, hunkId: hunk.id, original: hunk.after, after: hunk.after })} />}
                </div>
                {isEditing && editing ? <div className="review-hunk-editor">
                  <Field label={ko ? '변경 후' : 'After'} error={editing.revision !== review.revision ? (ko ? '검토가 변경됐어요.' : 'The review changed.') : undefined}>{id =>
                    <textarea id={id} className="ui-mono" maxLength={24000} rows={Math.min(16, Math.max(4, lineCount(editing.after) + 1))} value={editing.after} disabled={busy || inert}
                      onChange={event => setEditing({ ...editing, after: event.target.value })}
                      onKeyDown={event => { if (event.key === 'Escape' && !busy) { event.preventDefault(); event.stopPropagation(); setEditing(undefined); } }} />}</Field>
                  <FormActions>
                    <button type="button" className="btn" disabled={busy} onClick={() => setEditing(undefined)}>{ko ? '취소' : 'Cancel'}</button>
                    <button type="button" className="btn primary" data-testid="review-amend" disabled={busy || inert || editing.revision !== review.revision || editing.after.replace(/\r\n|\r/g, '\n') === editing.original.replace(/\r\n|\r/g, '\n')} onClick={() => {
                      if (amendSent.current) return;
                      amendSent.current = editing;
                      if (!command('amend', { id: editing.id, revision: editing.revision, path: editing.path, hunkId: editing.hunkId, after: editing.after })) amendSent.current = undefined;
                    }}>{ko ? '저장' : 'Save'}</button>
                  </FormActions>
                </div> : <div className="review-comparison">
                  {hunk.before && <pre className="review-before" aria-label={ko ? '변경 전' : 'Before'}>{revealControls(hunk.before)}</pre>}
                  {hunk.after && <pre className="review-after" aria-label={ko ? '변경 후' : 'After'}>{revealControls(hunk.after)}</pre>}
                </div>}
              </div>;
            })}
          </div>}
        </div>;
      })}
    </DataTable>
    {review.diagnostics.length > 0 && <DataTable columns="16px minmax(0, 1fr)" label={ko ? '검사 결과' : 'Check results'} testId="review-diagnostics" head={['', ko ? '검사 결과' : 'Check results']}>
      {review.diagnostics.map((d, i) => <Row key={i}>
        <Cell className={`ui-status-icon ${d.isError ? 'ui-err' : 'ui-ok'}`}><Icon name={d.isError ? 'error' : 'check'} size={14} /></Cell>
        <Cell wrap>{revealControls(d.message)}{d.path && <span className="ui-sub ui-mono">{revealControls(relativePath(root, d.path))}{d.line ? `:${d.line}` : ''}</span>}</Cell>
      </Row>)}
    </DataTable>}
    {review.outcomes.some(item => item.state !== 'applied') && <DataTable columns="minmax(0, 1fr) auto" label={ko ? '적용 결과' : 'Apply results'} head={[ko ? '파일' : 'File', ko ? '결과' : 'Result']}>
      {review.outcomes.filter(item => item.state !== 'applied').map(o => <Row key={o.path}>
        <Cell className="ui-mono" title={o.path}>{revealControls(relativePath(root, o.path))}{o.error && <span className="ui-sub ui-err">{revealControls(o.error)}</span>}</Cell>
        <Cell><Badge tone={o.state === 'failed' ? 'error' : 'neutral'} title={o.state}>{revealControls(stateLabel(o.state, locale))}</Badge></Cell>
      </Row>)}
    </DataTable>}
    {review.message && <p className="ui-note ui-muted" role="status">{revealControls(review.message)}</p>}
    {error && <p className="ui-note" role="alert">{revealControls(error)}</p>}
    {discarding && !applying ? <div className="ui-card-foot review-discard-confirm" role="group" aria-label={t('review.discard')} data-testid="review-discard-confirm" onKeyDown={event => {
      if (event.key === 'Escape' && !event.nativeEvent.isComposing) { event.preventDefault(); event.stopPropagation(); cancelDiscard(); }
    }}>
      <span className="ui-grow">{applyAsked ? (ko ? '적용 요청을 처리하는 중인데도 제안을 버릴까요?' : 'Discard the proposal while an apply request is in progress?') : t('review.discardQuestion')}</span>
      <button type="button" className="btn" onClick={cancelDiscard}>{t('tools.cancel')}</button>
      <button type="button" ref={discardConfirm} className="btn danger" data-testid="review-discard-yes" disabled={inert || discardAsked || discarding.id !== review.id || discarding.revision !== review.revision} onClick={() => {
        if (discarding.id === latest.current?.id && discarding.revision === latest.current.revision && command('discard')) setDiscarding(undefined);
      }}>{t('review.discardConfirm')}</button>
    </div> : <div className="ui-card-foot">
      <Badge tone={statusTone} title={review.status}>{revealControls(stateLabel(review.status, locale))}</Badge>
      <label className="ui-check ui-grow review-generators"><input type="checkbox" checked={allowSourceGenerators} disabled={locked} onChange={event => setAllowSourceGenerators(event.target.checked)} />{ko ? '소스 생성기 허용' : 'Source generators'}</label>
      <button type="button" ref={discardButton} className="btn subtle" data-testid="review-discard" disabled={applying || discardAsked} onClick={() => setDiscarding({ id: review.id, revision: review.revision })}>{t('review.discard')}</button>
      <button type="button" className="btn primary" data-testid="review-apply" disabled={locked || !review.canApply || selectedCount === 0} onClick={() => command('apply')}><Icon name="check" size={13} />{review.origin === 'editor' ? (ko ? `선택만 남기기 ${selectedCount}` : `Keep selected ${selectedCount}`) : (ko ? `적용 ${selectedCount}` : `Apply ${selectedCount}`)}</button>
    </div>}
  </ViewSection>;
}
