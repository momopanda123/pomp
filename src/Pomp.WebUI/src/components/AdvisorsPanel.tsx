import { useCallback, useEffect, useId, useMemo, useRef, useState } from 'react';
import { ADVISOR_TOOLS, DEFAULT_ADVISOR, advisorCount, advisorSlug, splitAdvisorModel, validAdvisorDocument, type AdvisorsController } from '../advisors';
import { useT, type Translate } from '../i18n';
import { onMessage, type AdvisorDocument, type AdvisorEntry, type AdvisorNote, type AdvisorScope, type AdvisorsReplyPayload, type AdvisorsStatePayload } from '../protocol';
import { formatCount } from '../format';
import { SeverityBadge } from './AdvisorNotes';
import { filterModels, groupModels, localizedProviderName, modelKey, providerName, thinkingLabel, type SetupReply, type SetupState } from '../setup';
import { Icon } from './Icon';
import { Markdown } from './Markdown';
import { PickerPopover } from './ModelPickers';
import { Note } from './Note';
import { plainControls, revealControls } from './RevealControls';
import { SectionRefresh, ViewSection } from './ViewSection';
import { Actions, Badge, Cell, DataTable, Field, Form, FormActions, IconButton, Pager, Row, SearchBox, Segmented, Switch, Toolbar, byName, matches, refocus, usePaged, type BadgeTone } from './ui';
import '../styles/advisors.css';

function modelLabel(selector: string | undefined, setup: SetupState | null, t: Translate): string {
  if (!selector) return t('advisors.default');
  const parsed = splitAdvisorModel(selector);
  const model = setup?.models?.find(item => `${item.provider}/${item.id}`.toLowerCase() === parsed.selector.toLowerCase());
  if (!model) {
    // A model the list does not have from a connected account (e.g. the engine's own advisor model): its id with the account's name,
    // never the provider id. A selector naming no known account stays as written, so a mistyped file shows what it says.
    const at = parsed.selector.indexOf('/');
    const providerId = at > 0 ? parsed.selector.slice(0, at) : ''; const provider = providerId ? providerName(setup?.providers, providerId) : '';
    if (!providerId || provider === providerId) return selector;
    return [parsed.selector.slice(at + 1), localizedProviderName(provider, t), parsed.level ? thinkingLabel(t, parsed.level) : ''].filter(Boolean).join(' · ');
  }
  // An account without a display name adds nothing: the model name alone is shown.
  const provider = providerName(setup?.providers, model.provider);
  const name = provider === model.provider ? '' : localizedProviderName(provider, t);
  return [model.name || model.id, name, parsed.level ? thinkingLabel(t, parsed.level) : ''].filter(Boolean).join(' · ');
}
function AdvisorModel({ value, setup }: { value?: string; setup: SetupState | null }) {
  const t = useT();
  return <Badge tone={value ? 'info' : 'neutral'} title={plainControls(value ?? '')}><span className="advisor-model" title={plainControls(value ?? '')}>{revealControls(modelLabel(value, setup, t))}</span></Badge>;
}
function shortPath(path: string, root: string | undefined): string {
  const normal = path.replace(/\\/g, '/'); const workspace = root?.replace(/\\/g, '/').replace(/\/$/, '');
  if (workspace && normal.toLowerCase().startsWith(workspace.toLowerCase() + '/')) return normal.slice(workspace.length + 1);
  return normal.replace(/^(?:[A-Za-z]:\/Users\/[^/]+|\/home\/[^/]+|\/Users\/[^/]+)(?=\/)/i, '~');
}
const optionalNumber = (value: string): number | undefined => value === '' ? undefined : Number(value);
const DEFAULT_NAME = DEFAULT_ADVISOR;
/** When a note came: the time, with the date when it was not today. */
function noteTime(at: string | undefined): { label: string; title: string } {
  const time = at ? new Date(at) : undefined;
  if (!time || !Number.isFinite(time.getTime())) return { label: '', title: '' };
  const clock = time.toLocaleTimeString(undefined, { hour: '2-digit', minute: '2-digit' });
  return { label: time.toDateString() === new Date().toDateString() ? clock : `${time.toLocaleDateString(undefined, { month: 'numeric', day: 'numeric' })} ${clock}`, title: time.toLocaleString() };
}
/** Like the engine's own editor, a lone plain "default" entry is the built-in advisor itself, so it is saved as no entry at all. */
const plainDefault = (entry: AdvisorEntry): boolean => entry.name === DEFAULT_NAME && entry.enabled !== false && !entry.model && (entry.reviewMode ?? 'turn') === 'turn'
  && (entry.reviewInterval ?? 1) === 1 && !entry.syncBacklog && entry.tools === undefined && !entry.instructions && entry.maxNotesPerUpdate === undefined;
type RunningAdvisor = AdvisorsStatePayload['advisors'][number];
const known = (value: number | null | undefined): value is number => typeof value === 'number' && Number.isFinite(value) && value >= 0;
/** Context use and spend, as the engine's advisor status reports them. */
function usageText(advisor: RunningAdvisor): { label: string; title: string } {
  const label: string[] = []; const title: string[] = [];
  if (known(advisor.contextTokens) && known(advisor.contextWindow) && advisor.contextWindow > 0) {
    label.push(`${Math.round(advisor.contextTokens / advisor.contextWindow * 100)}%`); title.push(`${formatCount(advisor.contextTokens)} / ${formatCount(advisor.contextWindow)}`);
  } else if (known(advisor.contextTokens) && advisor.contextTokens > 0) { label.push(formatCount(advisor.contextTokens)); title.push(formatCount(advisor.contextTokens)); }
  if (known(advisor.cost) && advisor.cost > 0) { const cost = `$${advisor.cost.toFixed(advisor.cost >= 1 ? 2 : 4)}`; label.push(cost); title.push(cost); }
  return { label: label.join(' · '), title: title.join(' · ') };
}

/** Rows per page of the two advisor tables: a file can define dozens, and the sections must not grow past the pane. */
const ADVISOR_PAGE = 8;
const statusTone = (status: string | undefined): BadgeTone => status === 'active' ? 'ok' : status === 'no-model' || status === 'paused' ? 'warn' : 'neutral';

export function AdvisorsPanel({ controller, setup, connected, selected, inert, root, sessionId }: {
  controller: AdvisorsController; setup: SetupState | null; setupReplies: SetupReply[]; connected: boolean; selected: boolean; inert: boolean; root?: string; sessionId?: string;
}) {
  const t = useT(); const { state, pending, generation, command } = controller;
  const [scope, setScope] = useState<AdvisorScope>('project');
  const [loaded, setLoaded] = useState<{ scope: AdvisorScope; hash: string; document: AdvisorDocument } | null>(null);
  const [sharedInstructions, setSharedInstructions] = useState(''); const [sharedNotes, setSharedNotes] = useState('');
  const [warnings, setWarnings] = useState<string[]>([]); const [error, setError] = useState(''); const [conflict, setConflict] = useState(false);
  const [notApplied, setNotApplied] = useState<Partial<Record<AdvisorScope, boolean>>>({});
  // keepDefault: a new advisor added while the built-in advisor runs (no advisor defined) is saved next to it as a "default" entry, so adding never
  // removes the built-in advisor without a trace; deleting that row is how it goes.
  const [editor, setEditor] = useState<{ index: number; entry: AdvisorEntry; keepDefault?: boolean } | null>(null);
  const [deleting, setDeleting] = useState<number | null>(null);
  const [query, setQuery] = useState(''); const [runningQuery, setRunningQuery] = useState('');
  // A row's history: the notes that advisor gave in this conversation, newest first, under the table.
  const [historyFor, setHistoryFor] = useState<string | null>(null);
  const [notes, setNotes] = useState<AdvisorNote[] | null>(null);
  const [noteQuery, setNoteQuery] = useState(''); const [openNotes, setOpenNotes] = useState<ReadonlySet<number>>(new Set());
  const [runError, setRunError] = useState('');
  // Notes that arrive after the history was closed (or moved to another advisor) are dropped.
  const historyWanted = useRef<string | null>(null);
  const closeHistory = useCallback(() => { historyWanted.current = null; setHistoryFor(null); setNotes(null); setNoteQuery(''); setOpenNotes(new Set()); }, []);
  const version = useRef(0); const loading = useRef<string | null>(null);
  const identity = `${root ?? ''}:${sessionId ?? ''}:${connected}:${generation}`;
  const previousIdentity = useRef(identity);
  if (previousIdentity.current !== identity) { previousIdentity.current = identity; version.current++; }
  const document = loaded?.scope === scope ? loaded.document : null;
  const dirty = !!document && (sharedInstructions !== (document.instructions ?? '') || sharedNotes !== String(document.maxNotesPerUpdate ?? ''));
  const locked = !connected || inert || pending || state?.supported !== true;
  const canSave = !locked && !!document && !conflict;
  const replyError = (reply: AdvisorsReplyPayload) => {
    setError(reply.error || t('advisors.failed'));
    if (/conflict|hash|해시|충돌/i.test(reply.error ?? '')) setConflict(true);
  };
  const reload = useCallback(async () => {
    if (!connected || state?.supported !== true) return;
    const current = version.current; setError(''); setLoaded(null);
    const reply = await command({ action: 'load', scope });
    if (version.current !== current) return;
    if (!reply.ok || !reply.document || typeof reply.hash !== 'string' || reply.scope !== scope) {
      setError(reply.error || t('advisors.failed')); return;
    }
    setConflict(false); setLoaded({ scope, hash: reply.hash, document: reply.document });
    setSharedInstructions(reply.document.instructions ?? ''); setSharedNotes(String(reply.document.maxNotesPerUpdate ?? ''));
    setWarnings(reply.warnings ?? []); setEditor(null); setDeleting(null);
  }, [connected, state?.supported, command, scope, t]);
  useEffect(() => {
    version.current++; loading.current = null; setLoaded(null); setEditor(null); setDeleting(null); setWarnings([]); setError(''); setConflict(false);
    setSharedInstructions(''); setSharedNotes(''); setNotApplied({}); setRunError(''); closeHistory();
  }, [generation]);
  // One request at a time: a load waits until a running request (e.g. the status question above) settles.
  useEffect(() => {
    if (!selected || inert || !connected || pending || state?.supported !== true) return;
    const key = `${generation}:${scope}`;
    if (loading.current === key) return;
    loading.current = key; void reload();
  }, [selected, inert, connected, pending, state?.supported, generation, scope, reload]);
  const changeScope = (next: AdvisorScope) => {
    if (locked || dirty || editor || deleting !== null || next === scope) return;
    version.current++; setScope(next); setLoaded(null); setError(''); setConflict(false); setWarnings([]);
  };
  const save = async (advisors: AdvisorEntry[]) => {
    if (!canSave || !loaded) return;
    if (advisors.length === 1 && plainDefault(advisors[0]!)) advisors = [];
    const next: AdvisorDocument = { advisors, ...(sharedInstructions ? { instructions: sharedInstructions } : {}), ...(sharedNotes !== '' ? { maxNotesPerUpdate: optionalNumber(sharedNotes) } : {}) };
    if (!validAdvisorDocument(next)) { setError(t('advisors.invalid')); return; }
    const current = version.current; setError(''); setConflict(false);
    const reply = await command({ action: 'save', scope, document: next, baseHash: loaded.hash });
    if (version.current !== current) return;
    if (!reply.ok) { replyError(reply); return; }
    setNotApplied(previous => ({ ...previous, [scope]: reply.applied === false }));
    setWarnings(reply.warnings ?? []); setEditor(null); setDeleting(null);
    await reload();
  };
  // A row's switch: that advisor in this conversation only; the files stay as they are.
  const select = async (name: string, on: boolean) => {
    if (locked) return;
    const current = version.current; setRunError('');
    const reply = await command({ action: 'select', name, on });
    if (current === version.current && !reply.ok) setRunError(reply.error || t('advisors.failed'));
  };
  const loadHistory = async (name: string) => {
    if (locked) return;
    const current = version.current; setRunError('');
    if (historyWanted.current !== name) { setNotes(null); setNoteQuery(''); setOpenNotes(new Set()); }
    historyWanted.current = name; setHistoryFor(name);
    const reply = await command({ action: 'history', name });
    if (current !== version.current || historyWanted.current !== name) return;
    if (!reply.ok) { setRunError(reply.error || t('advisors.failed')); return; }
    setNotes(Array.isArray(reply.notes) ? reply.notes : []);
  };
  // The engine keeps a running advisor's transcript only: it opens in an editor tab.
  const openTranscript = async (name: string) => {
    if (locked) return;
    const current = version.current; setRunError('');
    const reply = await command({ action: 'openHistory', name });
    if (current === version.current && !reply.ok) setRunError(reply.error || t('advisors.failed'));
  };
  // New notes in the conversation: an open history follows them.
  const reloadHistory = useRef<() => void>(() => {});
  reloadHistory.current = () => { if (historyWanted.current && !locked) void loadHistory(historyWanted.current); };
  useEffect(() => onMessage(message => { if (message.type === 'chat/advisorNotes') window.setTimeout(() => reloadHistory.current(), 500); }), []);
  const open = async (path: string) => {
    if (locked) return;
    const current = version.current; setError('');
    const reply = await command({ action: 'open', path });
    if (current === version.current && !reply.ok) replyError(reply);
  };
  const runningStatus = (status: string) => {
    switch (status) {
      case 'off': case 'disabled': return t('advisors.status.off');
      case 'no-model': case 'no_model': return t('advisors.status.no-model');
      case 'active': case 'running': return t('advisors.status.active', { count: 1 }).replace(/\s*1$/, '');
      case 'paused': return t('advisors.status.paused');
      default: return t('advisors.status.unknown');
    }
  };

  // Every defined advisor, with what the engine reports for it in this conversation: on (its switch) when the conversation runs it.
  const running = new Map((state?.advisors ?? []).map(advisor => [advisor.name, advisor]));
  const conversationRows: (RunningAdvisor & { on: boolean })[] = (state?.implicitDefault ? [{ name: DEFAULT_NAME, model: undefined }] : state?.roster ?? []).map(entry => {
    const live = running.get(entry.name);
    const status = state?.enabled ? live?.status ?? 'paused' : 'off';
    return { ...live, name: entry.name, model: live?.model ?? entry.model, status, on: state?.enabled === true && status !== 'paused' && status !== 'off' };
  });
  const runningShown = conversationRows.map((advisor, index) => ({ advisor, index })).filter(({ advisor }) => matches(runningQuery, advisor.name, advisor.model))
    .sort((a, b) => byName(a.advisor.name, b.advisor.name));
  const notesShown = (notes ?? []).map((note, index) => ({ note, index })).filter(({ note }) => note.advisor === historyFor && matches(noteQuery, note.note, note.severity)).reverse();
  const notesPaged = usePaged(notesShown, `${historyFor}:${noteQuery}`, ADVISOR_PAGE);
  const toggleNote = (index: number) => setOpenNotes(previous => { const next = new Set(previous); if (next.has(index)) next.delete(index); else next.add(index); return next; });
  const transcriptOpen = (name: string) => state?.enabled === true && running.get(name)?.status === 'active';
  const runningPaged = usePaged(runningShown, runningQuery, ADVISOR_PAGE);
  const rosterShown = (document?.advisors ?? []).map((advisor, index) => ({ advisor, index })).filter(({ advisor }) => matches(query, advisor.name, advisor.model, advisor.instructions))
    .sort((a, b) => byName(a.advisor.name, b.advisor.name));
  const rosterPaged = usePaged(rosterShown, `${scope}:${query}`, ADVISOR_PAGE);
  const showDefault = !!state?.implicitDefault && !document?.advisors.length;
  const defaultModel = state?.advisors.find(item => item.name === DEFAULT_NAME)?.model;
  const status = state?.status === 'active' ? t('advisors.status.active', { count: state.advisors.filter(item => item.status === 'active' || item.status === 'running').length })
    : runningStatus(state?.status ?? 'unknown');
  const files = state?.files.map((file, index) => ({ ...file, index })).filter(file => file.scope === scope) ?? [];
  const busyRow = locked || !!editor || deleting !== null;
  const modeLabel = (advisor: AdvisorEntry) => t(advisor.reviewMode === 'agent-end' ? 'advisors.mode.agent-end' : 'advisors.mode.turn');
  const modeTitle = (advisor: AdvisorEntry) => `${t('advisors.reviewInterval')} ${advisor.reviewInterval ?? 1}`;
  return <div className="advisors-panel" data-testid="advisors-panel" aria-busy={pending || undefined}>
    <ViewSection sectionKey="advisors-conversation" title={t('advisors.conversation')} bodyClassName="ui-flush"
      actions={<Badge tone={statusTone(state?.status)} testId="advisors-status">{status}</Badge>}>
      {runError && <p className="ui-note" role="alert" data-testid="advisors-run-error">{revealControls(runError)}</p>}
      {conversationRows.length > ADVISOR_PAGE && <Toolbar><SearchBox value={runningQuery} onChange={setRunningQuery} testId="advisors-running-search" /></Toolbar>}
      <div className="advisor-table-wrap"><DataTable className="advisor-table advisor-running" label={t('advisors.conversation')} columns="minmax(88px, 1fr) minmax(0, 120px) auto" empty={!runningShown.length} emptyRows={1}
        head={[t('advisors.title'), t('advisors.model'), '']}>
        {runningPaged.slice.map(({ advisor, index }) => {
          const usage = usageText(advisor); const title = advisor.name;
          return <Row key={`${advisor.name}:${index}`} testId={`advisor-running-${index}`} selected={historyFor === advisor.name}>
            <Cell><strong>{revealControls(title)}</strong>
              {advisor.on && (advisor.status === 'no-model' || advisor.status === 'unknown') && <Badge tone={statusTone(advisor.status)} testId={`advisor-state-${index}`}>{runningStatus(advisor.status)}</Badge>}
              {usage.label && <span className="advisor-sub ui-muted" title={usage.title} data-testid={`advisor-usage-${index}`}>{usage.label}</span>}</Cell>
            <Cell><AdvisorModel value={advisor.model} setup={setup} /></Cell>
            <Actions>
              <IconButton icon="log" label={t('advisors.historyOf', { name: title })} testId={`advisor-history-${index}`} disabled={locked}
                onClick={() => { if (historyFor === advisor.name) closeHistory(); else void loadHistory(advisor.name); }} />
              <Switch label={t('advisors.useOf', { name: title })} showLabel={false} testId={`advisor-use-${index}`} checked={advisor.on} disabled={locked} onChange={next => { void select(advisor.name, next); }} />
            </Actions>
          </Row>;
        })}
      </DataTable></div>
      <Pager paged={runningPaged} testId="advisors-running-pager" />
      {(state?.rosterHidden ?? 0) > 0 && <p className="ui-note ui-muted" role="status" data-testid="advisors-hidden">{t('advisors.hidden', { count: state!.rosterHidden! })}</p>}
      {historyFor !== null && <div className="advisor-history" data-testid="advisors-history">
        <Toolbar>
          <strong className="advisor-history-name" data-testid="advisors-history-name">{revealControls(historyFor)}</strong>
          <SearchBox value={noteQuery} onChange={setNoteQuery} testId="advisors-notes-search" />
          <IconButton icon="restart" label={t('agents.reload')} testId="advisors-history-reload" disabled={locked} onClick={() => { void loadHistory(historyFor); }} />
          <IconButton icon="external" label={t('advisors.openTranscript')} testId="advisors-history-transcript" disabled={locked || !transcriptOpen(historyFor)} onClick={() => { void openTranscript(historyFor); }} />
          <IconButton icon="x" label={t('agents.close')} testId="advisors-history-close" onClick={closeHistory} />
        </Toolbar>
        <DataTable className="advisor-notes-table" testId="advisors-notes" label={t('advisors.notesTitle')} columns="minmax(0, 1fr) auto" empty={!notesShown.length} emptyRows={2} head={[t('advisors.note'), t('advisors.time')]}>
          {notesPaged.slice.map(({ note, index }) => {
            const shown = openNotes.has(index); const time = noteTime(note.at);
            return <Row key={index} testId={`advisor-note-${index}`}>
              <Cell wrap><button type="button" className="advisor-note-toggle" aria-expanded={shown} data-testid={`advisor-note-toggle-${index}`} onClick={() => toggleNote(index)}>
                <Icon name={shown ? 'chevronDown' : 'chevron'} size={12} /><SeverityBadge severity={note.severity} t={t} />{!shown && <span className="advisor-note-preview">{plainControls(note.note)}</span>}</button>
                {shown && <div className="advisor-note-full"><Markdown text={note.note} /></div>}</Cell>
              <Cell className="ui-muted advisor-note-time" title={time.title || undefined}>{time.label}</Cell>
            </Row>;
          })}
        </DataTable>
        <Pager paged={notesPaged} testId="advisors-notes-pager" />
      </div>}
    </ViewSection>
    <ViewSection sectionKey="advisors-roster" title={t('advisors.title')} status={advisorCount(state)} bodyClassName="ui-flush"
      actions={<>
        <SectionRefresh testId="advisors-reload" disabled={locked} onClick={() => { void reload(); }} label={t('agents.reload')} />
        <button type="button" className="btn small" data-testid="advisor-new" disabled={!canSave || !!editor || deleting !== null} onClick={() => { setError(''); setEditor({ index: -1, entry: { name: '' }, keepDefault: state?.implicitDefault === true && !document?.advisors.length }); }}><Icon name="plus" size={12} />{t('advisors.new')}</button>
      </>}>
      {state?.supported === false && <p className="ui-note" role="alert">{t('advisors.unsupported')}</p>}
      {error && <div className="ui-note advisor-error" role="alert" data-testid="advisors-error"><span>{revealControls(error)}</span>{conflict && <button type="button" className="btn small subtle" data-testid="advisors-conflict-reload" disabled={locked} onClick={() => { void reload(); }}><Icon name="restart" size={12} />{t('agents.reload')}</button>}</div>}
      {warnings.map((warning, index) => <p key={index} className="ui-note ui-warn" data-testid={`advisors-warning-${index}`}>{revealControls(warning)}</p>)}
      {notApplied[scope] && <p className="ui-note" role="status" data-testid="advisors-not-applied"><Badge tone="warn" icon="info">{t('advisors.nextConversation')}</Badge></p>}
      <Toolbar><Segmented<AdvisorScope> label={t('advisors.scope')} testId="advisors-scope" value={scope} disabled={locked || dirty || !!editor || deleting !== null} onChange={changeScope}
        options={[{ id: 'project', label: t('advisors.scope.project'), testId: 'advisors-scope-project' }, { id: 'user', label: t('advisors.scope.user'), testId: 'advisors-scope-user' }]} />
        <SearchBox value={query} onChange={setQuery} testId="advisors-search" disabled={!!editor} /></Toolbar>
      {editor && document ? <AdvisorForm key={`${scope}:${editor.index}`} entry={editor.entry} names={[...document.advisors.filter((_, index) => index !== editor.index).map(item => item.name), ...(editor.keepDefault ? [DEFAULT_NAME] : [])]} setup={setup} disabled={locked} canSave={canSave} onCancel={() => setEditor(null)} onSave={entry => {
        const advisors = document.advisors.slice(); if (editor.index >= 0) advisors[editor.index] = entry; else advisors.push(...(editor.keepDefault ? [{ name: DEFAULT_NAME }, entry] : [entry])); void save(advisors);
      }} /> : <><div className="advisor-table-wrap"><DataTable className="advisor-table advisor-roster" testId="advisors-roster" label={t('advisors.title')} columns="minmax(88px, 1fr) minmax(0, 120px) 88px 56px"
        empty={!rosterShown.length && !showDefault} head={[t('advisors.name'), <span className="advisor-col">{t('advisors.model')}</span>, <span className="advisor-col">{t('advisors.mode')}</span>, '']}>
        {showDefault && <Row testId="advisor-default">
          <Cell><strong>{DEFAULT_ADVISOR}</strong><span className="advisor-sub"><AdvisorModel value={defaultModel} setup={setup} /><span className="ui-muted">{t('advisors.mode.turn')}</span></span></Cell>
          <Cell className="advisor-col"><AdvisorModel value={defaultModel} setup={setup} /></Cell>
          <Cell className="advisor-col ui-muted">{t('advisors.mode.turn')}</Cell>
          <Actions>
            <IconButton icon="edit" label={t('agents.edit')} testId="advisor-default-edit" disabled={busyRow || !canSave} onClick={() => { setError(''); setEditor({ index: -1, entry: { name: DEFAULT_NAME } }); }} />
          </Actions>
        </Row>}
        {rosterPaged.slice.map(({ advisor, index }) => <Row key={index} testId={`advisor-card-${index}`} selected={deleting === index}>
          <Cell title={advisor.instructions}><strong>{revealControls(advisor.name)}</strong>{advisor.enabled === false && <Badge tone="warn" testId={`advisor-paused-${index}`}>{t('advisors.status.paused')}</Badge>}<span className="advisor-sub"><AdvisorModel value={advisor.model} setup={setup} /><span className="ui-muted">{modeLabel(advisor)}</span></span></Cell>
          <Cell className="advisor-col"><AdvisorModel value={advisor.model} setup={setup} /></Cell>
          <Cell className="advisor-col ui-muted" title={modeTitle(advisor)}>{modeLabel(advisor)}</Cell>
          <Actions>
            <IconButton icon="edit" label={t('agents.edit')} testId={`advisor-edit-${index}`} disabled={busyRow} onClick={() => { setError(''); setEditor({ index, entry: { ...advisor, tools: advisor.tools?.slice() } }); }} />
            <IconButton icon="trash" tone="danger" label={t('agents.delete')} testId={`advisor-delete-${index}`} disabled={busyRow} onClick={() => setDeleting(index)} />
          </Actions>
        </Row>)}
      </DataTable></div>
      <Pager paged={rosterPaged} testId="advisors-roster-pager" /></>}
      {deleting !== null && document?.advisors[deleting] && <div className="ui-card-foot advisor-confirm" role="group" data-testid="advisor-delete-confirm" onKeyDown={event => { if (event.key === 'Escape') { event.stopPropagation(); refocus(`advisor-delete-${deleting}`); setDeleting(null); } }}>
        <span className="ui-grow">{revealControls(t('advisors.deleteQuestion', { name: document.advisors[deleting].name }))}</span>
        <button type="button" className="btn small" autoFocus disabled={locked} data-testid="advisor-delete-cancel" onClick={() => { refocus(`advisor-delete-${deleting}`); setDeleting(null); }}>{t('dialog.cancel')}</button>
        <button type="button" className="btn small danger" disabled={!canSave} data-testid="advisor-delete-proceed" onClick={() => { void save(document.advisors.filter((_, entry) => entry !== deleting)); }}>{t('agents.delete')}</button>
      </div>}
      {files.map(file => <div key={`${file.scope}:${file.path}`} className="ui-card-foot advisor-file">
        <Icon name="file" size={13} className="ui-muted" /><span className="ui-grow ui-mono" title={plainControls(file.path)}>{revealControls(shortPath(file.path, root))}</span>
        <IconButton icon="external" label={t('agents.openInVs')} testId={`advisor-file-open-${file.index}`} disabled={locked} onClick={() => { void open(file.path); }} />
      </div>)}
    </ViewSection>
    {document && <ViewSection sectionKey="advisors-shared" title={t('advisors.sharedInstructions')} bodyClassName="ui-flush" actions={<Badge tone={scope === 'project' ? 'accent' : 'neutral'}>{t(scope === 'user' ? 'advisors.scope.user' : 'advisors.scope.project')}</Badge>}>
      <Form className="advisor-shared">
        <Field label={t('advisors.instructions')}>{id => <textarea id={id} data-testid="advisors-shared-instructions" rows={4} maxLength={16000} disabled={locked} value={sharedInstructions} onChange={event => setSharedInstructions(event.target.value)} />}</Field>
        <div className="ui-field-row">
          <Field label={t('advisors.sharedNotes')}>{id => <input id={id} data-testid="advisors-shared-notes" type="number" min={1} max={32} step={1} disabled={locked} value={sharedNotes} onChange={event => setSharedNotes(event.target.value)} />}</Field>
          <span />
        </div>
        <FormActions>
          <button type="button" className="btn" disabled={locked || !dirty} onClick={() => { setSharedInstructions(document.instructions ?? ''); setSharedNotes(String(document.maxNotesPerUpdate ?? '')); }}>{t('dialog.cancel')}</button>
          <button type="button" className="btn primary" data-testid="advisors-shared-save" disabled={!canSave || !!editor || deleting !== null || !dirty} onClick={() => { void save(document.advisors); }}>{t('agents.save')}</button>
        </FormActions>
      </Form>
    </ViewSection>}
  </div>;
}

function AdvisorForm({ entry, names, setup, disabled, canSave, onCancel, onSave }: {
  entry: AdvisorEntry; names: string[]; setup: SetupState | null; disabled: boolean; canSave: boolean; onCancel: () => void; onSave: (entry: AdvisorEntry) => void;
}) {
  const t = useT(); const [name, setName] = useState(entry.name); const [paused, setPaused] = useState(entry.enabled === false);
  // The engine knows an advisor by the letters and digits of its name: two names that share them are one advisor.
  const clash = names.find(value => advisorSlug(value) === advisorSlug(name.trim()));
  const parsed = splitAdvisorModel(entry.model); const [selector, setSelector] = useState(parsed.selector); const [level, setLevel] = useState(parsed.level);
  const [mode, setMode] = useState<'turn' | 'agent-end'>(entry.reviewMode ?? 'turn'); const [interval, setInterval] = useState(String(entry.reviewInterval ?? 1));
  const [backlog, setBacklog] = useState(entry.syncBacklog ?? ''); const [toolsMode, setToolsMode] = useState<'default' | 'none' | 'custom'>(entry.tools === undefined ? 'default' : entry.tools.length ? 'custom' : 'none');
  const [tools, setTools] = useState<string[]>(entry.tools ?? ['read', 'grep', 'glob', 'recall']); const [instructions, setInstructions] = useState(entry.instructions ?? ''); const [notes, setNotes] = useState(String(entry.maxNotesPerUpdate ?? ''));
  const invalidName = !name.trim() || name.trim().length > 64; const duplicate = clash !== undefined;
  const draft: AdvisorEntry = { name: name.trim(), enabled: !paused, reviewMode: mode, reviewInterval: Number(interval), ...(selector ? { model: `${selector}${level ? `:${level}` : ''}` } : {}), ...(backlog ? { syncBacklog: backlog as AdvisorEntry['syncBacklog'] } : {}), ...(toolsMode === 'none' ? { tools: [] } : toolsMode === 'custom' ? { tools } : {}), ...(instructions ? { instructions } : {}), ...(notes !== '' ? { maxNotesPerUpdate: optionalNumber(notes) } : {}) };
  const valid = !duplicate && validAdvisorDocument({ advisors: [draft] });
  const labelId = useId();
  return <form className="advisor-form ui-form" data-testid="advisor-form" onSubmit={event => { event.preventDefault(); if (!disabled && canSave && valid) onSave(draft); }} onKeyDown={event => {
    if (event.key === 'Escape' && !event.defaultPrevented && !event.nativeEvent.isComposing && event.keyCode !== 229 && !disabled) { event.preventDefault(); event.stopPropagation(); onCancel(); }
  }}>
    <fieldset disabled={disabled} className="advisor-fieldset">
      <div className="ui-field-row">
        <Field label={t('advisors.name')}>{id => <>
          <input autoFocus id={id} data-testid="advisor-name" maxLength={64} value={name} aria-invalid={invalidName || duplicate} onChange={event => setName(event.target.value)} />
          {(duplicate || (invalidName && name !== '')) && <span className="ui-field-error" role="alert" data-testid="advisor-name-error">{clash === undefined ? t('advisors.nameInvalid')
            : clash.trim() === name.trim() ? t('advisors.nameDuplicate') : t('advisors.nameSameKey', { name: clash })}</span>}
        </>}</Field>
        <div className="ui-field"><label id={`${labelId}-model`}>{t('advisors.model')}</label><AdvisorModelPicker setup={setup} value={selector} disabled={disabled} labelledBy={`${labelId}-model`} onChange={setSelector} /></div>
      </div>
      <div className="ui-field-row">
        <Field label={t('status.thinking')}>{id => <select id={id} data-testid="advisor-thinking" value={level} disabled={!selector} onChange={event => setLevel(event.target.value)}><option value="">{t('thinking.auto')}</option>{['off', 'minimal', 'low', 'medium', 'high', 'xhigh', 'max'].map(value => <option key={value} value={value}>{thinkingLabel(t, value)}</option>)}</select>}</Field>
        <Field label={t('advisors.backlog')}>{id => <select id={id} data-testid="advisor-backlog" value={backlog} onChange={event => setBacklog(event.target.value as typeof backlog)}><option value="">{t('advisors.backlog.global')}</option><option value="off">{t('advisors.backlog.off')}</option><option value="1">{t('advisors.backlog.1')}</option><option value="3">{t('advisors.backlog.3')}</option><option value="5">{t('advisors.backlog.5')}</option><option value="strict">{t('advisors.backlog.strict')}</option></select>}</Field>
      </div>
      <div className="ui-field-row">
        <div className="ui-field"><label>{t('advisors.mode')}</label><Segmented<'turn' | 'agent-end'> label={t('advisors.mode')} testId="advisor-mode" value={mode} onChange={setMode} disabled={disabled}
          options={[{ id: 'turn', label: t('advisors.mode.turn'), testId: 'advisor-mode-turn' }, { id: 'agent-end', label: t('advisors.mode.agent-end'), testId: 'advisor-mode-agent-end' }]} /></div>
        <Field label={t('advisors.reviewInterval')}>{id => <input id={id} data-testid="advisor-interval" type="number" min={1} max={1000000} step={1} value={interval} onChange={event => setInterval(event.target.value)} />}</Field>
      </div>
      <div className="ui-field"><label>{t('advisors.tools')}</label><Segmented<'default' | 'none' | 'custom'> label={t('advisors.tools')} testId="advisor-tools-mode" value={toolsMode} onChange={setToolsMode} disabled={disabled}
        options={[{ id: 'default', label: t('advisors.default'), testId: 'advisor-tools-default' }, { id: 'none', label: t('advisors.tools.none'), testId: 'advisor-tools-none' }, { id: 'custom', label: t('advisors.tools.custom'), testId: 'advisor-tools-custom' }]} />
        {toolsMode === 'custom' && <div className="advisor-tools" role="group" aria-label={t('advisors.tools')}>{ADVISOR_TOOLS.map(tool => <label key={tool} className="ui-check ui-mono"><input type="checkbox" data-testid={`advisor-tool-${tool}`} checked={tools.includes(tool)} onChange={event => setTools(previous => event.target.checked ? [...previous, tool] : previous.filter(value => value !== tool))} />{tool}</label>)}</div>}
      </div>
      <Field label={t('advisors.instructions')}>{id => <textarea id={id} data-testid="advisor-instructions" rows={4} maxLength={16000} value={instructions} onChange={event => setInstructions(event.target.value)} />}</Field>
      <div className="ui-field-row">
        <Field label={t('advisors.notes')}>{id => <input id={id} data-testid="advisor-notes" type="number" min={1} max={32} step={1} value={notes} onChange={event => setNotes(event.target.value)} />}</Field>
        <div className="ui-field advisor-enabled-field"><label>{t('advisors.status.paused')}</label><Switch label={t('advisors.status.paused')} showLabel={false} testId="advisor-entry-paused" checked={paused} onChange={setPaused} /></div>
      </div>
      <FormActions><button type="button" className="btn" data-testid="advisor-cancel" onClick={onCancel}>{t('dialog.cancel')}</button><button type="submit" className="btn primary" data-testid="advisor-save" disabled={!canSave || !valid}>{t('agents.save')}</button></FormActions>
    </fieldset>
  </form>;
}

function AdvisorModelPicker({ setup, value, onChange, disabled, labelledBy }: { setup: SetupState | null; value: string; onChange: (value: string) => void; disabled: boolean; labelledBy: string }) {
  const t = useT(); const id = useId(); const root = useRef<HTMLDivElement>(null); const button = useRef<HTMLButtonElement>(null); const [open, setOpen] = useState(false); const [query, setQuery] = useState('');
  useEffect(() => { if (disabled) setOpen(false); }, [disabled]);
  useEffect(() => {
    if (!open) return;
    const outside = (event: PointerEvent) => { if (event.target instanceof Node && !root.current?.contains(event.target)) setOpen(false); };
    document.addEventListener('pointerdown', outside, true); return () => document.removeEventListener('pointerdown', outside, true);
  }, [open]);
  const items = useMemo(() => {
    const label = (provider: string) => { const name = providerName(setup?.providers, provider); return localizedProviderName(name === provider ? t('setup.key.provider') : name, t); };
    return [{ id: '', label: t('advisors.default'), current: !value, testId: 'advisor-model-default' }, ...groupModels(filterModels(setup?.models ?? [], query, label), label).flatMap(group => group.models.map(model => ({ id: modelKey(model), label: model.name || model.id, title: `${model.provider}/${model.id}`, current: `${model.provider}/${model.id}`.toLowerCase() === value.toLowerCase(), group: group.label, groupKey: group.provider })))];
  }, [setup, query, value, t]);
  return <div ref={root} className="advisor-model-picker" onKeyDown={event => { if (event.key === 'Escape' && open && !event.defaultPrevented && !event.nativeEvent.isComposing && event.keyCode !== 229) { event.preventDefault(); event.stopPropagation(); setOpen(false); button.current?.focus(); } }}>
    <button ref={button} type="button" className="btn subtle" data-testid="advisor-model-picker" disabled={disabled} aria-labelledby={labelledBy} aria-haspopup="listbox" aria-expanded={open} title={plainControls(value)} onClick={() => { setQuery(''); setOpen(previous => !previous); }}><span>{revealControls(modelLabel(value, setup, t))}</span><Icon name="chevronDown" size={11} /></button>
    {open && <PickerPopover placement="inline" anchorRef={root} id={id} testId="advisor-model-popover" label={t('advisors.model')} items={items} search={{ value: query, onChange: setQuery, label: t('picker.search'), placeholder: t('picker.search') }} status={setup?.modelsError ? <Note tone="error" text={setup.modelsError} /> : !setup?.models ? <div role="status">{t('picker.loading')}</div> : undefined} onEscape={() => { setOpen(false); button.current?.focus(); }} onChoose={item => {
      const model = setup?.models?.find(model => modelKey(model) === item.id); if (item.id === '') onChange(''); else if (model) onChange(`${model.provider}/${model.id}`); setOpen(false); button.current?.focus();
    }} />}
  </div>;
}
