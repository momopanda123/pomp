import { useEffect, useMemo, useState } from 'react';
import type { AgentHookRule, AgentsStatePayload } from '../protocol';
import type { HookInfo } from '../toolsProtocol';
import { useT } from '../i18n';
import { toolsTranslate } from '../i18n/toolsStrings';
import { hookDefinitionDomains, useToolsState } from '../toolsState';
import { stateLabel } from '../stateLabels';
import { emptyRuleDraft, ruleDraftOf, ruleInput, RuleFields, RULE_NAME_LIMIT, useAgentHookRules, useRuleNames, validateRule, type RuleDraft, type RuleEvent } from './AgentHooksPanel';
import { useProjectHooks } from './ProjectHooks';
import { emptyHookDraft, hookDraftOf, HookFields, saveHook, type HookDraftFields } from './tools/HookDefinitions';
import { Icon, type IconName } from './Icon';
import { revealControls } from './RevealControls';
import { SectionRefresh, ViewSection } from './ViewSection';
import { Actions, Badge, Cell, DataTable, Field, Form, FormActions, IconButton, Pager, Row, SearchBox, Segmented, Switch, Toolbar, byName, matches, refocus, usePaged, type BadgeTone } from './ui';
import './tools/tools.css';
import '../styles/automation.css';

export interface AgentRulesViewProps {
  locale: string; workspaceKey?: string; sessionId?: string; agents: AgentsStatePayload | null; connected: boolean;
  /** A reply is running / a new conversation is starting: the hook files refuse writes, as in the Tools tab. */
  busy: boolean; newSessionPending: boolean;
  /** The Agents tab is not the shown tab / this Automation view is the selected view of the Agents tab. */
  inert: boolean; selected: boolean;
  /** The project policy is missing or invalid, so scripts do not run: opens Tools › Permissions. */
  onShowPermissions: () => void; onCount?: (count: number | undefined) => void;
}

/** What an automation item does: the two chat actions are event rules (agentHooks), a script is a hook file (.pomp/hooks). */
type Kind = 'notify' | 'forward' | 'script';
type Item = { type: 'rule'; key: string; name: string; rule: AgentHookRule } | { type: 'hook'; key: string; name: string; hook: HookInfo };
interface Editing { kind: Kind; name: string; enabled: boolean; rule: RuleDraft; hook: HookDraftFields; originalRule?: AgentHookRule; originalHook?: HookInfo }

const ruleSignalIcon = (event: RuleEvent): IconName => event.startsWith('team.') ? 'users' : event.startsWith('agent.') ? 'bot' : event === 'feedback.waiting' ? 'message' : event === 'ide.saved' ? 'file' : 'zap';
const outcomeTone = (value: string): BadgeTone => {
  const code = value.toLowerCase().split(':')[0].trim();
  if (['notified', 'forwarded', 'completed', 'done', 'succeeded', 'exited'].includes(code)) return 'ok';
  if (['failed', 'error', 'timedout', 'denied', 'refused'].includes(code)) return 'error';
  if (['running', 'inprogress', 'in_progress', 'queued', 'pending'].includes(code)) return 'info';
  if (['skipped', 'suppressed', 'blocked', 'cancelled'].includes(code)) return 'warn';
  return 'neutral';
};
/** Today's runs show the time only; older ones the date too (24-hour clock keeps the column narrow). */
const when = (value: string, locale: string) => {
  const date = new Date(value); if (Number.isNaN(date.getTime())) return '';
  const today = date.toDateString() === new Date().toDateString();
  return date.toLocaleString(locale, { ...(today ? {} : { month: 'numeric', day: 'numeric' }), hour: '2-digit', minute: '2-digit', hourCycle: 'h23' });
};

/** Agents › Automation: one list of everything that reacts to an event — event rules (notify, chat) and PowerShell hooks (script) — and one run history. */
export function AgentRulesView({ locale, workspaceKey, sessionId, agents, connected, busy, newSessionPending, inert, selected, onShowPermissions, onCount }: AgentRulesViewProps) {
  const t = useT(); const tt = toolsTranslate(locale); const ko = locale.toLowerCase().startsWith('ko');
  const shown = !inert && selected;
  const [editing, setEditing] = useState<Editing | null>(null);
  const [validation, setValidation] = useState('');
  const [deleting, setDeleting] = useState<Item | null>(null);
  const [query, setQuery] = useState('');
  // The hooks domain is this instance's alone: the Tools tab's controller ignores `hooks` messages, so nothing is tracked or refreshed twice.
  // The lists are read whenever the Agents tab shows (not only this view), so the Automation chip's count is right on every view.
  const controller = useToolsState({ locale, workspaceKey: workspaceKey ?? '', sessionId, connected, busy, active: !inert, disabled: inert || newSessionPending, view: 'hooks', domains: hookDefinitionDomains });
  const rules = useAgentHookRules({ workspaceKey, sessionId, connected, inert, onSaved: () => { setEditing(null); setValidation(''); } });
  const runtime = useProjectHooks({ sessionId, workspaceKey: workspaceKey ?? '', disabled: !connected || newSessionPending, inert: !shown });
  const names = useRuleNames(agents);
  const hooks = controller.hooks.hooks;
  useEffect(() => { onCount?.(rules.loaded ? rules.rules.length + hooks.length : undefined); }, [rules.loaded, rules.rules.length, hooks.length, onCount]);
  useEffect(() => { setEditing(null); setDeleting(null); setValidation(''); setQuery(''); }, [workspaceKey, sessionId]);

  const hooksPending = controller.pending('hooks');
  const rulesLocked = !connected || !shown || rules.pending || !sessionId;
  const hooksLocked = !connected || !shown || newSessionPending || busy || hooksPending;
  const items = useMemo<Item[]>(() => [
    ...rules.rules.map(rule => ({ type: 'rule' as const, key: `rule:${rule.id}`, name: rule.name, rule })),
    ...hooks.map(hook => ({ type: 'hook' as const, key: `hook:${hook.id}`, name: hook.name, hook })),
  ], [rules.rules, hooks]);
  const visible = items.filter(item => matches(query, item.name, item.type === 'hook' ? item.hook.description : item.rule.message)).sort((a, b) => byName(a.name, b.name));
  const paged = usePaged(visible, `${workspaceKey}:${sessionId}:${query}`);

  // One switch for this conversation: forwarding rules may send chat messages and hook scripts listen to saves and builds.
  const runtimeOn = runtime.state ? runtime.state.enabled : true;
  const using = rules.autoForward && runtimeOn;
  const setUsing = (next: boolean) => { if (rules.run({ action: 'autoForward', enabled: next })) runtime.command('enable', next); };

  const kindLabel = (kind: Kind) => kind === 'notify' ? (ko ? '알림' : 'Notify') : kind === 'forward' ? (ko ? '채팅' : 'Chat') : (ko ? '스크립트' : 'Script');
  const kindIcon = (kind: Kind): IconName => kind === 'notify' ? 'bell' : kind === 'forward' ? 'send' : 'terminal';
  const kindTone = (kind: Kind): BadgeTone => kind === 'notify' ? 'warn' : kind === 'forward' ? 'info' : 'accent';
  const actionBadge = (item: Item) => {
    const kind: Kind = item.type === 'hook' ? 'script' : item.rule.action;
    const target = item.type === 'rule' && item.rule.action === 'forward' ? names.label(item.rule.targetKind ?? 'main', item.rule.targetId ?? '') : undefined;
    return <Badge tone={kindTone(kind)} icon={kindIcon(kind)} title={target}>{kindLabel(kind)}</Badge>;
  };
  const signalBadge = (item: Item) => {
    if (item.type === 'rule') {
      const rule = item.rule;
      const source = rule.event.startsWith('ide.') ? (rule.event === 'ide.saved' ? rule.glob ?? '**' : '') : rule.sourceKind === 'any' ? '' : names.label(rule.sourceKind, rule.sourceId ?? '');
      return <Badge tone={rule.event.endsWith('.failed') ? 'error' : 'neutral'} icon={ruleSignalIcon(rule.event)} title={source || undefined}>{t(`agentHooks.event.${rule.event}`)}</Badge>;
    }
    const hook = item.hook;
    if (hook.note || hook.unreadable || !hook.event) return <Badge tone="warn" icon="warning" title={hook.note}>{ko ? '파일 오류' : 'File error'}</Badge>;
    return <Badge icon={hook.event === 'onBuild' ? 'zap' : 'file'} title={hook.event === 'onFileSave' ? hook.glob : undefined}>{hook.event === 'onBuild' ? tt('hooks.eventBuild') : tt('hooks.eventSave')}</Badge>;
  };

  function openNew() { setValidation(''); setDeleting(null); setEditing({ kind: 'notify', name: '', enabled: true, rule: emptyRuleDraft(), hook: emptyHookDraft() }); }
  function openEdit(item: Item) {
    setValidation(''); setDeleting(null);
    if (item.type === 'rule') setEditing({ kind: item.rule.action, name: item.rule.name, enabled: item.rule.enabled, rule: ruleDraftOf(item.rule), hook: emptyHookDraft(), originalRule: item.rule });
    else {
      // A hook file keeps its name (that field is locked), so the focus starts in the first field that can change.
      setEditing({ kind: 'script', name: item.hook.name, enabled: item.hook.enabled, rule: emptyRuleDraft(), hook: hookDraftOf(item.hook), originalHook: item.hook });
      refocus('pshooks-description');
    }
  }
  async function save() {
    if (!editing) return;
    setValidation('');
    if (editing.kind === 'script') {
      if (hooksLocked) return;
      const result = await saveHook(controller, locale, editing.name, editing.enabled, editing.hook, editing.originalHook);
      if (typeof result === 'string') setValidation(result);
      else if (result.ok) setEditing(null);
      return;
    }
    if (rulesLocked) return;
    const problem = validateRule(editing.name, editing.kind, editing.rule, t);
    if (problem) { setValidation(problem); return; }
    const rule = ruleInput(editing.name, editing.enabled, editing.kind, editing.rule);
    rules.run(editing.originalRule ? { action: 'save', id: editing.originalRule.id, revision: editing.originalRule.revision, rule } : { action: 'save', rule }, 'save');
  }
  function toggle(item: Item) {
    if (item.type === 'rule') {
      const rule = item.rule;
      rules.run({ action: 'save', id: rule.id, revision: rule.revision, rule: { name: rule.name, enabled: !rule.enabled, event: rule.event, sourceKind: rule.sourceKind, sourceId: rule.sourceId, glob: rule.glob, action: rule.action, targetKind: rule.targetKind, targetId: rule.targetId, message: rule.message } });
    } else if (item.hook.hash) void controller.command('hooks', { action: 'setEnabled', id: item.hook.id, expectedHash: item.hook.hash, enabled: !item.hook.enabled });
  }
  async function remove() {
    if (!deleting) return;
    if (deleting.type === 'rule') { if (rules.run({ action: 'delete', id: deleting.rule.id, revision: deleting.rule.revision })) setDeleting(null); return; }
    const reply = await controller.command('hooks', { action: 'delete', id: deleting.hook.id, expectedHash: deleting.hook.hash });
    if (reply.ok) setDeleting(null);
  }
  // Cancel and Escape close the editor without saving; the focus goes back to the control that opened it.
  const cancelEdit = () => {
    const opener = editing?.originalRule ? `hooks-open-${editing.originalRule.id}` : editing?.originalHook ? `pshooks-edit-${editing.originalHook.id}` : 'hooks-create';
    setEditing(null); setValidation(''); refocus(opener);
  };
  const keepItem = () => { if (deleting) refocus(deleting.type === 'rule' ? `hooks-remove-${deleting.rule.id}` : `pshooks-delete-${deleting.hook.id}`); setDeleting(null); };
  const refresh = () => { rules.run({ action: 'refresh' }); void controller.command('hooks', { action: 'refresh' }); runtime.command('refresh'); };

  const runs = useMemo(() => [
    ...rules.log.map((row, index) => ({ key: `r${index}:${row.at}`, at: row.at, name: row.rule, signal: RULE_EVENTS_LABEL(row.event, t), detail: row.source.startsWith('team ') ? names.label('team', row.source.slice(5)) : row.source.startsWith('agent ') ? names.label('agent', row.source.slice(6)) : row.source, outcome: row.outcome, label: stateLabel(row.outcome, locale, 'outcome'), error: '' })),
    ...(runtime.state?.history ?? []).map((row, index) => ({ key: `h${index}:${row.createdUtc}`, at: row.createdUtc, name: row.name, signal: stateLabel(row.event, locale), detail: row.path, outcome: row.status, label: stateLabel(row.status, locale), error: row.error ?? '' })),
  ].sort((a, b) => (Date.parse(b.at) || 0) - (Date.parse(a.at) || 0)), [rules.log, runtime.state?.history, locale, t, names]);
  const pagedRuns = usePaged(runs, `${workspaceKey}:${sessionId}`);

  const isScript = editing?.kind === 'script';
  const formLocked = isScript ? hooksLocked : rulesLocked;
  // Changing what an existing item does would leave the old one behind: a rule switches only between notify and chat, a hook file stays a script.
  const kinds: Kind[] = editing?.originalHook ? ['script'] : editing?.originalRule ? ['notify', 'forward'] : ['notify', 'forward', 'script'];
  const kindLocked = kinds.every(kind => kind === 'script' ? hooksLocked : rulesLocked);
  const policyWarning = hooks.length > 0 && controller.hooks.policy !== 'valid';
  return <div className="automation" data-testid="automation-view">
    <ViewSection sectionKey="agents-automation" className="automation-rules" title={ko ? '자동화 규칙' : 'Automation rules'} status={items.length} bodyClassName="ui-flush"
      actions={<>
        {using && rules.maxForwards > 0 && rules.forwards >= rules.maxForwards && <Badge tone="warn" testId="hooks-forwards" title={`${rules.forwards}/${rules.maxForwards}`}>{ko ? '전달 한도 도달' : 'Forward limit reached'}</Badge>}
        <Switch label={ko ? '이 대화에서 사용' : 'Use in this conversation'} testId="automation-use" checked={using} disabled={rulesLocked || newSessionPending} onChange={setUsing} />
        <SectionRefresh onClick={refresh} disabled={!connected || !shown || rules.pending} testId="hooks-refresh" />
        <button type="button" className="btn small" data-testid="hooks-create" disabled={!connected || !shown || !!editing || !sessionId} onClick={openNew}><Icon name="plus" size={12} />{ko ? '새 규칙' : 'New rule'}</button>
      </>}>
      {editing ? <Form testId={isScript ? 'pshooks-editor' : 'hooks-editor'} className="automation-form" onKeyDown={event => {
        if (event.key === 'Escape' && !event.defaultPrevented && !event.nativeEvent.isComposing && event.keyCode !== 229) { event.preventDefault(); event.stopPropagation(); cancelEdit(); }
      }}>
        <fieldset disabled={formLocked} className="automation-fieldset">
          <Field label={ko ? '이름' : 'Name'}>{id => <input id={id} data-testid={isScript ? 'pshooks-name' : 'hooks-name'} autoFocus value={editing.name} maxLength={isScript ? 40 : RULE_NAME_LIMIT}
            disabled={!!editing.originalHook} className={isScript ? 'ui-mono' : undefined} spellCheck={!isScript} onChange={event => setEditing({ ...editing, name: event.target.value })} />}</Field>
        </fieldset>
        <Field label={ko ? '할 일' : 'Action'}>{() => <Segmented<Kind> label={ko ? '할 일' : 'Action'} testId="automation-kind" value={editing.kind} onChange={kind => { setValidation(''); setEditing({ ...editing, kind }); }}
          options={kinds.map(kind => ({ id: kind, label: kindLabel(kind), icon: kindIcon(kind), testId: `automation-kind-${kind}` }))}
          disabled={kindLocked} />}</Field>
        <fieldset disabled={formLocked} className="automation-fieldset">
          {isScript ? <HookFields draft={editing.hook} locale={locale} onChange={patch => setEditing({ ...editing, hook: { ...editing.hook, ...patch } })} />
            : <RuleFields draft={editing.rule} action={editing.kind === 'forward' ? 'forward' : 'notify'} agents={agents} onChange={patch => setEditing({ ...editing, rule: { ...editing.rule, ...patch } })} />}
          <Switch label={ko ? '사용' : 'Enabled'} testId={isScript ? 'pshooks-enabled' : 'hooks-enabled'} checked={editing.enabled} onChange={enabled => setEditing({ ...editing, enabled })} />
        </fieldset>
        {validation && <span className="ui-field-error" role="alert" data-testid={isScript ? 'pshooks-validation' : 'hooks-validation'}>{validation}</span>}
        {(isScript ? controller.errors('hooks') : rules.error ? [rules.error] : []).map((error, index) => <span key={index} className="ui-field-error" role="alert">{revealControls(error)}</span>)}
        <FormActions>
          <button type="button" className="btn" data-testid="automation-cancel" onClick={cancelEdit}>{ko ? '취소' : 'Cancel'}</button>
          <button type="button" className="btn primary" data-testid={isScript ? 'pshooks-save' : 'hooks-save'} disabled={formLocked} onClick={() => { void save(); }}>{ko ? '저장' : 'Save'}</button>
        </FormActions>
      </Form> : <>
        {policyWarning && <div className="automation-policy" data-testid="pshooks-policy-note"><Icon name="warning" size={13} className="ui-warn" /><span className="ui-grow">{tt('hooks.policyNote')}</span>
          <button type="button" className="btn small subtle" data-testid="pshooks-show-permissions" disabled={!connected} onClick={onShowPermissions}><Icon name="shield" size={12} />{tt('hooks.goPermissions')}</button></div>}
        <Toolbar><SearchBox value={query} onChange={setQuery} testId="automation-search" /></Toolbar>
        <div className="automation-list-wrap"><DataTable className="automation-list" testId="automation-table" label={ko ? '자동화 규칙' : 'Automation rules'} columns="minmax(0, 1fr) 96px 84px 34px 88px" empty={!visible.length}
          head={[ko ? '이름' : 'Name', <span className="automation-col">{ko ? '신호' : 'Signal'}</span>, <span className="automation-col">{ko ? '할 일' : 'Action'}</span>, ko ? '사용' : 'On', '']}>
          {paged.slice.map(item => {
            const broken = item.type === 'hook' && (!!item.hook.note || !!item.hook.unreadable);
            const id = item.type === 'rule' ? item.rule.id : item.hook.id;
            const enabled = item.type === 'rule' ? item.rule.enabled : item.hook.enabled;
            const locked = item.type === 'rule' ? rulesLocked : hooksLocked;
            return <Row key={item.key} testId={item.type === 'rule' ? `hooks-rule-${id}` : `pshooks-row-${id}`} selected={deleting?.key === item.key}>
              <Cell title={item.type === 'hook' ? item.hook.description || item.hook.note : undefined}>
                <strong>{revealControls(item.name)}</strong>
                <span className="automation-badges">{signalBadge(item)}{actionBadge(item)}</span>
                {broken && item.type === 'hook' && <span role="note" className="ui-sub ui-warn" title={item.hook.note || tt('tools.unreadable')}>{revealControls(item.hook.note || tt('tools.unreadable'))}</span>}
              </Cell>
              <Cell className="automation-col">{signalBadge(item)}</Cell>
              <Cell className="automation-col">{actionBadge(item)}</Cell>
              <Cell>{!broken && <Switch label={ko ? `${item.name} 사용` : `Enable ${item.name}`} showLabel={false} checked={enabled} disabled={locked || (item.type === 'hook' && !item.hook.hash)}
                testId={item.type === 'rule' ? `hooks-toggle-${id}` : `pshooks-toggle-${id}`} onChange={() => toggle(item)} />}</Cell>
              <Actions>
                {!broken && <IconButton icon="edit" label={ko ? '편집' : 'Edit'} disabled={locked || !!editing || (item.type === 'hook' && !item.hook.hash)} testId={item.type === 'rule' ? `hooks-open-${id}` : `pshooks-edit-${id}`} onClick={() => openEdit(item)} />}
                {item.type === 'hook' && <IconButton icon="external" label={tt('hooks.open')} disabled={hooksLocked} testId={`pshooks-open-${id}`} onClick={() => { void controller.command('hooks', { action: 'open', id }); }} />}
                <IconButton icon="trash" tone="danger" label={ko ? '삭제' : 'Delete'} disabled={locked} testId={item.type === 'rule' ? `hooks-remove-${id}` : `pshooks-delete-${id}`} onClick={() => setDeleting(item)} />
              </Actions>
            </Row>;
          })}
        </DataTable></div>
        <Pager paged={paged} testId="automation-pager" />
        {deleting && <div className="ui-card-foot automation-confirm" role="group" data-testid={deleting.type === 'rule' ? 'hooks-delete-confirm' : 'pshooks-delete-confirm'} onKeyDown={event => { if (event.key === 'Escape') { event.stopPropagation(); keepItem(); } }}>
          <span className="ui-grow">{deleting.type === 'rule' ? t('agentHooks.deleteBody', { title: deleting.name }) : tt('hooks.deleteQuestion', { name: deleting.name })}</span>
          <button type="button" className="btn small" autoFocus onClick={keepItem}>{ko ? '취소' : 'Cancel'}</button>
          <button type="button" className="btn small danger" data-testid={deleting.type === 'rule' ? 'hooks-delete-yes' : 'pshooks-delete-yes'} disabled={deleting.type === 'rule' ? rulesLocked : hooksLocked} onClick={() => { void remove(); }}>{ko ? '삭제' : 'Delete'}</button>
        </div>}
        {rules.error && <p className="ui-note" role="alert" data-testid="hooks-error">{revealControls(rules.error)}</p>}
        {controller.errors('hooks').map((error, index) => <p className="ui-note" role="alert" key={index}>{revealControls(error)}</p>)}
        {controller.hooks.diagnostics.map((message, index) => <p className="ui-note ui-muted" key={'d' + index}>{revealControls(message)}</p>)}
      </>}
    </ViewSection>
    <ViewSection sectionKey="agents-automation-runs" className="automation-runs" title={ko ? '실행 기록' : 'Run history'} status={runs.length} bodyClassName="ui-flush" active={runtime.state?.running}
      actions={runtime.state?.running ? <button type="button" className="btn small" data-testid="project-hooks-cancel" disabled={!connected || !shown} onClick={() => runtime.command('cancel')}><Icon name="stop" size={11} />{ko ? '실행 중지' : 'Stop'}</button> : undefined}>
      {runtime.state?.message && <p className="ui-note ui-muted" role="status">{revealControls(runtime.state.message)}</p>}
      {runtime.transportError && <p className="ui-note" role="alert">{t('error.hostUnavailable')}</p>}
      <DataTable testId="hooks-log" label={ko ? '실행 기록' : 'Run history'} columns="96px minmax(0, 1fr) 84px" empty={!runs.length} head={[ko ? '시각' : 'Time', ko ? '규칙' : 'Rule', ko ? '결과' : 'Result']}>
        {pagedRuns.slice.map((run, index) => <Row key={run.key} testId={`hooks-firing-${index}`}>
          <Cell className="ui-muted">{when(run.at, locale)}</Cell>
          <Cell title={run.error || run.detail}>{revealControls(run.name)}<span className="ui-sub">{run.signal}{run.detail ? <> · {revealControls(run.detail)}</> : null}</span></Cell>
          <Cell><Badge tone={outcomeTone(run.outcome)} title={run.error || run.outcome}>{revealControls(run.label)}</Badge></Cell>
        </Row>)}
      </DataTable>
      <Pager paged={pagedRuns} testId="hooks-log-pager" />
    </ViewSection>
  </div>;
}

const RULE_EVENTS_LABEL = (event: string, t: ReturnType<typeof useT>) =>
  (['agent.completed', 'agent.failed', 'team.completed', 'team.failed', 'feedback.waiting', 'ide.saved', 'ide.build.succeeded', 'ide.build.failed'] as string[]).includes(event)
    ? t(`agentHooks.event.${event as RuleEvent}`) : t('state.unknown');
