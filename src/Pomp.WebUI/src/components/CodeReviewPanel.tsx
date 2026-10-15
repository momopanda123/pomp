import { useContext, useEffect, useRef, useState } from 'react';
import { roleTitle } from '../bundledDescriptions';
import { useT } from '../i18n';
import { MAIN_ROLE, MainRoleContext, RolesContext, effectiveRoles } from '../teamsCache';
import { Avatar } from './AgentsTables';
import { newId } from '../format';
import { toolsTranslate } from '../i18n/toolsStrings';
import type { SendResult } from '../limits';
import { buildProblemsRequest, MAX_PROBLEMS, MAX_PROBLEM_CHARS, problemPath } from '../problems';
import { onMessage, send, type UiToHost } from '../protocol';
import { useWorkState } from '../work/store';
import { useRefreshOnShow } from '../useRefreshOnShow';
import { revealControls } from './RevealControls';
import { Icon } from './Icon';
import { SectionRefresh, ViewSection } from './ViewSection';
import { Cell, DataTable, IconButton, Pager, Row, Segmented, Toolbar, usePaged } from './ui';
import type { WorkspaceProblem } from '../protocol';
import type { WorkLayout } from './WorkPanel';

type Filter = 'all' | 'error' | 'warning' | 'agent';
const COLUMNS = '18px 16px minmax(44px, max-content) minmax(0, 1fr) auto';
/** An opened row: the whole description, where it is, and the source an agent quoted for it. */
function ProblemDetails({ item, root, t }: { item: WorkspaceProblem; root: string; t: ReturnType<typeof toolsTranslate> }) {
  const where = `${problemPath(root, item.path)}:${item.line}${item.column ? `:${item.column}` : ''}`;
  return <div className="problem-details" data-testid={`problem-details-${item.id}`}>
    <p className="problem-message">{revealControls(item.message)}</p>
    <dl className="problem-facts">
      <dt>{t('problems.location')}</dt><dd className="ui-mono">{revealControls(where)}</dd>
      {item.project && <><dt>{t('problems.project')}</dt><dd>{revealControls(item.project)}</dd></>}
    </dl>
    {typeof item.evidence === 'string' && item.evidence.trim() && <>
      <div className="problem-evidence-label">{t('problems.evidence')}</div>
      <pre className="problem-evidence" data-testid={`problem-evidence-${item.id}`}>{revealControls(item.evidence)}</pre>
    </>}
  </div>;
}

/** Agent findings and live IDE diagnostics share one selection and one ordinary chat send. */
export function ProblemsPanel({ locale, inert, connected = true, onSolve }: {
  locale: string; inert: boolean; connected?: boolean; layout?: WorkLayout; onSolve: (text: string) => SendResult;
}) {
  const state = useWorkState('problems'); const t = toolsTranslate(locale); const ko = locale.startsWith('ko');
  // An agent finding shows the agent that reported it with its picture: the chat's POMP, or the member's role as the Agents tab has it.
  const roles = effectiveRoles(useContext(RolesContext)); const main = useContext(MainRoleContext); const appT = useT();
  const reporterOf = (item: WorkspaceProblem) => {
    const name = item.reporter && item.reporter !== MAIN_ROLE.name ? item.reporter : undefined;
    const role = name ? roles.find(candidate => candidate.name === name) : undefined;
    return name ? { name, title: role ? roleTitle(role, appT, name) : name, look: role?.avatar, scope: role?.scope }
      : { name: MAIN_ROLE.name, title: MAIN_ROLE.title, look: main?.avatar, scope: MAIN_ROLE.scope };
  };
  // A member's finding needs the roles; when the page has none yet, they are asked for once.
  const namesMember = state.items.some(item => item.source === 'agent' && !!item.reporter && item.reporter !== 'main');
  const askedRoles = useRef(false);
  useEffect(() => {
    if (!namesMember || roles.length > 0 || askedRoles.current || !connected || inert) return;
    askedRoles.current = true; send('agents/command', { requestId: newId(), action: 'refresh' });
  }, [namesMember, roles.length, connected, inert]);
  const [filter, setFilter] = useState<Filter>('all'); const [selected, setSelected] = useState(new Set<string>());
  const [error, setError] = useState(''); const [pending, setPending] = useState<string>();
  // Rows opened to their whole description, location and (an agent's) quoted source.
  const [open, setOpen] = useState<ReadonlySet<string>>(new Set());
  const toggleOpen = (id: string) => setOpen(before => { const next = new Set(before); if (next.has(id)) next.delete(id); else next.add(id); return next; });
  const translate = useRef(t); translate.current = t;
  const request = useRef<{ id: string; timer: number } | undefined>(undefined);
  const selectAll = useRef<HTMLInputElement>(null); const scope = useRef(state.root);
  useEffect(() => {
    if (scope.current !== state.root) {
      scope.current = state.root; window.clearTimeout(request.current?.timer); request.current = undefined;
      setPending(undefined); setError(''); setSelected(new Set()); setFilter('all'); setOpen(new Set());
    } else setSelected(before => new Set([...before].filter(id => state.items.some(item => item.id === id))));
  }, [state.root, state.items]);
  useEffect(() => {
    const off = onMessage(message => {
      if (message.type !== 'workspace/problemsReply' || message.payload.requestId !== request.current?.id) return;
      window.clearTimeout(request.current.timer); request.current = undefined; setPending(undefined);
      setError(message.payload.ok ? '' : message.payload.error || translate.current('tools.failed'));
    });
    return () => { off(); window.clearTimeout(request.current?.timer); request.current = undefined; };
  }, []);
  const command = (action: UiToHost['workspace/problems']['action'], id?: string) => {
    if (!connected || inert || request.current) return;
    const requestId = newId();
    const timer = window.setTimeout(() => {
      if (request.current?.id !== requestId) return;
      request.current = undefined; setPending(undefined); setError(t('tools.noReply'));
    }, 15000);
    request.current = { id: requestId, timer }; setPending(id ?? 'refresh'); setError('');
    if (!send('workspace/problems', { requestId, action, ...(id ? { id } : {}) })) {
      window.clearTimeout(timer); request.current = undefined; setPending(undefined); setError(t('tools.hostUnavailable'));
    }
  };
  useRefreshOnShow(connected && !inert, () => command('refresh'));
  const kindOf = (item: { source: string; severity: string }): Filter => item.source === 'agent' ? 'agent' : item.severity === 'error' ? 'error' : 'warning';
  const count = (kind: Filter) => state.items.filter(item => kind === 'all' || kindOf(item) === kind).length;
  const visible = state.items.filter(item => filter === 'all' || kindOf(item) === filter)
    .toSorted((a, b) => (a.severity === b.severity ? 0 : a.severity === 'error' ? -1 : 1) || problemPath(state.root, a.path).localeCompare(problemPath(state.root, b.path)) || a.line - b.line || a.id.localeCompare(b.id));
  const paged = usePaged(visible, `${state.root}:${filter}`);
  // The column is named after what its rows hold: compiler codes, the agents that reported, or both.
  const agentRows = visible.some(item => kindOf(item) === 'agent'); const codeRows = visible.some(item => kindOf(item) !== 'agent');
  const sourceColumn = agentRows !== codeRows ? (agentRows ? 'problems.agentColumn' : 'problems.codeColumn')
    : agentRows || filter === 'all' ? 'problems.sourceColumn' : filter === 'agent' ? 'problems.agentColumn' : 'problems.codeColumn';
  const checked = visible.filter(item => selected.has(item.id)).length;
  useEffect(() => { if (selectAll.current) selectAll.current.indeterminate = checked > 0 && checked < visible.length; }, [checked, visible.length]);
  const chosen = state.items.filter(item => selected.has(item.id)); const text = buildProblemsRequest(locale, state.root, chosen);
  const blocked = !connected || inert || !!pending;
  const tooMany = chosen.length > MAX_PROBLEMS; const tooLong = text.length > MAX_PROBLEM_CHARS;
  const solveDisabled = blocked || !chosen.length || tooMany || tooLong;
  const toggle = (id: string, on: boolean) => setSelected(before => { const next = new Set(before); if (on) next.add(id); else next.delete(id); return next; });
  return <ViewSection sectionKey="work-problems" className="problems-panel" headingLevel="h2" title={t('problems.title')} status={<span data-testid="problems-count">{state.items.length}</span>} bodyClassName="ui-flush" inert={inert || undefined}
    actions={<SectionRefresh onClick={() => command('refresh')} disabled={!connected || inert || !!pending} testId="problems-refresh" />}>
    <Toolbar>
      <Segmented<Filter> label={t('problems.filter')} testId="problems-filter" value={filter} onChange={setFilter} options={[
        { id: 'all', label: t('problems.all'), badge: count('all'), testId: 'problems-filter-all' },
        { id: 'error', icon: 'error', label: t('problems.error'), badge: count('error'), testId: 'problems-filter-error' },
        { id: 'warning', icon: 'warning', label: t('problems.warning'), badge: count('warning'), testId: 'problems-filter-warning' },
        { id: 'agent', icon: 'users', label: t('problems.agent'), badge: count('agent'), testId: 'problems-filter-agent' },
      ]} />
    </Toolbar>
    <DataTable columns={COLUMNS} label={t('problems.title')} testId="problems-table" empty={!visible.length} emptyRows={3} head={[
      <input ref={selectAll} type="checkbox" aria-label={t('problems.selectAll')} data-testid="problems-select-all" checked={visible.length > 0 && checked === visible.length} disabled={!visible.length || blocked}
        onChange={event => setSelected(before => { const next = new Set(before); visible.forEach(item => event.target.checked ? next.add(item.id) : next.delete(item.id)); return next; })} />,
      '', t(sourceColumn), ko ? '설명' : 'Description', '']}>
      {paged.slice.map(item => {
        const kind = kindOf(item); const shown = open.has(item.id); const reporter = kind === 'agent' ? reporterOf(item) : undefined;
        return <div key={item.id} role="rowgroup" data-testid={`problem-group-${item.id}`}>
        <Row testId={`problem-${item.id}`} selected={selected.has(item.id)}>
          <Cell><input type="checkbox" aria-label={t('problems.select')} data-testid={`problem-select-${item.id}`} checked={selected.has(item.id)} disabled={blocked} onChange={event => toggle(item.id, event.target.checked)} /></Cell>
          {reporter
            ? <Cell title={reporter.title} className="problem-severity agent"><Avatar name={reporter.name} label={reporter.title} size={16} look={reporter.look} scope={reporter.scope} /></Cell>
            : <Cell title={t(`problems.${kind}`)} className={`problem-severity ${kind} ui-status-icon ${kind === 'error' ? 'ui-err' : 'ui-warn'}`}>
              <Icon name={kind === 'error' ? 'error' : 'warning'} size={14} /></Cell>}
          {reporter ? <Cell className="problem-reporter" title={reporter.title}>{revealControls(reporter.title)}</Cell> : <Cell className="ui-mono">{revealControls(item.code)}</Cell>}
          <Cell title={item.message}><button type="button" className="review-file-toggle problem-toggle" aria-expanded={shown} data-testid={`problem-toggle-${item.id}`} onClick={() => toggleOpen(item.id)}>
            <Icon name={shown ? 'chevronDown' : 'chevron'} size={12} /><span>{revealControls(item.message)}</span></button>
            <span className="ui-sub ui-mono problem-path">{revealControls(problemPath(state.root, item.path))}:{item.line}</span></Cell>
          <span role="cell" className="ui-cell ui-actions" aria-busy={pending === item.id || undefined}>
            <IconButton icon="external" label={t('problems.open')} disabled={blocked || !item.path} onClick={() => command('open', item.id)} testId={`problem-open-${item.id}`} />
            {item.source === 'agent' && <IconButton icon="trash" tone="danger" label={t('problems.dismiss')} disabled={blocked} onClick={() => command('dismiss', item.id)} testId={`problem-dismiss-${item.id}`} />}
          </span>
        </Row>
        {shown && <ProblemDetails item={item} root={state.root} t={t} />}
        </div>;
      })}
    </DataTable>
    <Pager paged={paged} testId="problems-pager" />
    <div className="ui-card-foot problems-solve">
      <span className="ui-grow ui-muted" data-testid="problems-selected">{t('problems.selected', { count: chosen.length })}
        {(tooMany || tooLong) && <span className="ui-err"> · {tooMany ? `${chosen.length} / ${MAX_PROBLEMS}` : t('problems.characters', { count: text.length })}</span>}</span>
      <button type="button" className="btn primary" data-testid="problems-solve" disabled={solveDisabled} onClick={() => {
        if (solveDisabled) return;
        const result = onSolve(text);
        if (result.ok) { setSelected(new Set()); setError(''); }
        else setError(t(result.reason === 'busy' ? 'tools.pending' : result.reason === 'hostUnavailable' ? 'tools.hostUnavailable' : 'tools.failed'));
      }}><Icon name="send" size={13} />{t('problems.solve')}</button>
    </div>
    {state.truncated && <p className="ui-note ui-muted" role="status">{t('problems.truncated')}</p>}
    {error && <p className="ui-note" role="alert">{revealControls(error)}</p>}
  </ViewSection>;
}
