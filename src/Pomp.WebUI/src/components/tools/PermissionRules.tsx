import { useEffect, useId, useRef, useState, type KeyboardEvent } from 'react';
import { useT, type Translate } from '../../i18n';
import { newId } from '../../format';
import { onMessage, send, type PermissionRulesState, type ProjectPolicyDraft, type ProjectPolicyRule, type UiToHost } from '../../protocol';
import type { ConversationTool, ToolsStatePayload } from '../../toolsProtocol';
import { Icon } from '../Icon';
import { SectionRefresh, ViewSection } from '../ViewSection';
import { Actions, Badge, Cell, DataTable, Field, FormActions, IconButton, Pager, Row, Segmented, Switch, Toolbar, usePaged } from '../ui';
import { ConfirmDelete, InPageDialog } from './InPageDialog';
import './policy.css';

type Action = UiToHost['permission/rules']['action'];
/** The rule form: paths and commands are edited one per line. `index` = the rule it replaces (undefined: a new rule at the end). */
interface RuleDraft { tool: string; effect: 'allow' | 'deny'; paths: string; commands: string }
/**
 * `baseHash` is the file the form was opened on (null: no file yet): a save never adopts a newer file it has not shown.
 * `stale` = the file changed since; the form can only be reloaded from the current file or cancelled.
 */
interface RuleForm { index?: number; baseHash: string | null; stale?: boolean; rule: RuleDraft }
const lines = (text: string) => text.split(/\r?\n/).map(line => line.trim()).filter(Boolean);
/** A persistent key is `<host|omp>:<tool>@<workspace>` (the host escapes %, ;, comma, space and controls as %XX); older keys have no workspace. */
function grantParts(key: string): { tool: string; root?: string } {
  const at = key.indexOf('@');
  return at < 0 ? { tool: key } : { tool: key.slice(0, at), root: key.slice(at + 1).replace(/%([0-9A-Fa-f]{2})/g, (_, hex: string) => String.fromCharCode(parseInt(hex, 16))) };
}
/** A stored grant as the list shows it: the built-in tools by name, Visual Studio's with a prefix (the stored key keeps its `omp:` / `host:` start). */
function grantLabel(tool: string): string {
  if (tool.startsWith('omp:')) return tool.slice('omp:'.length);
  if (tool.startsWith('host:')) return `VS · ${tool.slice('host:'.length)}`;
  return tool;
}
type RuleField = 'tool' | 'paths' | 'commands';
interface RuleProblem { field: RuleField; message: string }
/** The tool names a policy file accepts: a name, or `*` for every tool (the host and the engine check the same pattern; there are no other patterns). */
const POLICY_TOOL = /^(?:\*|[a-zA-Z0-9_.:-]{1,100})$/;
/** The engine treats a policy with more paths or commands per rule, or more rules, as unreadable (and blocks every tool). */
const MAX_LINES = 30, MAX_RULES = 100;
/**
 * Mirrors the host's checks (ProjectExecutionPolicy) so the form says what is wrong, at the field, before saving; the host stays authoritative.
 * `known`: this conversation's tool names (undefined: the list is unavailable, names are not checked); `original`: the edited rule's tool, kept even when gone.
 */
function invalidRule(rule: RuleDraft, t: Translate, known: ReadonlySet<string> | undefined, original?: string): RuleProblem | undefined {
  const tool = rule.tool.trim();
  if (!tool) return { field: 'tool', message: t('policy.problem.toolRequired') };
  if (!POLICY_TOOL.test(tool)) return { field: 'tool', message: t('policy.problem.toolName') };
  if (known && tool !== '*' && tool !== original && !known.has(tool)) return { field: 'tool', message: t('policy.problem.toolUnknown', { tool }) };
  const paths = lines(rule.paths), commands = lines(rule.commands);
  for (const [field, list] of [['paths', paths], ['commands', commands]] as const) {
    if (list.length > MAX_LINES) return { field, message: t('policy.problem.tooManyLines', { count: MAX_LINES }) };
    if (list.some(value => value.length > 4096)) return { field, message: t('policy.problem.lineTooLong') };
  }
  if (paths.some(path => /[*?]/.test(path))) return { field: 'paths', message: t('policy.problem.pathPattern') };
  if (paths.some(path => /^[A-Za-z]:/.test(path) || /^[\\/]/.test(path) || path.includes('~') || path.replace(/\\/g, '/').split('/').includes('..')))
    return { field: 'paths', message: t('policy.problem.pathRelative') };
  return undefined;
}
/** A deny rule without paths or commands for this tool (or for every tool): it wins over any allow rule. */
const deniedOutright = (rules: ProjectPolicyRule[], tool: string) => rules.some(rule => rule.effect === 'deny' && !rule.paths && !rule.commands && (rule.tool === '*' || rule.tool === tool));
const toRule = (rule: RuleDraft): ProjectPolicyRule => {
  const paths = lines(rule.paths), commands = lines(rule.commands);
  return { tool: rule.tool.trim(), effect: rule.effect, ...(paths.length ? { paths } : {}), ...(commands.length ? { commands } : {}) };
};
const toDraft = (rule: ProjectPolicyRule): RuleDraft => ({ tool: rule.tool, effect: rule.effect, paths: (rule.paths ?? []).join('\n'), commands: (rule.commands ?? []).join('\n') });

/**
 * Tools › 권한: what the user allowed (remembered approvals), which repositories they trusted, and the project's tool policy
 * (.pomp/permissions.json) as a table of rules in plain words. The approval mode and the other permission options live in Options › Pomp, not here.
 * `tools`: this conversation's tools (Tools › 이 대화), offered by the rule form and used to say which rules decide nothing now.
 * `countActive`: the Tools tab is shown, so the list is read once for the chip count even while another view is open.
 */
export function PermissionRules({ locale, root, sessionId, active, countActive = false, onCount, tools }: {
  locale: string; root: string; sessionId?: string; active: boolean; countActive?: boolean; onCount?: (count: number) => void; tools?: ToolsStatePayload;
}) {
  const t = useT();
  const [state, setState] = useState<PermissionRulesState>();
  const [pending, setPending] = useState(false);
  const [error, setError] = useState('');
  const request = useRef<{ id: string; action: Action } | undefined>(undefined);
  const [form, setForm] = useState<RuleForm>();
  const formOpen = useRef(false); formOpen.current = !!form;
  const [formError, setFormError] = useState('');
  // The hash of the file the confirmation was opened for: deleting never targets a newer file than the one shown.
  const [confirmDelete, setConfirmDelete] = useState<string>();
  function command(action: Action, extra: Partial<UiToHost['permission/rules']> = {}) {
    if (!(active || (countActive && action === 'refresh')) || request.current) return;
    const requestId = newId(); request.current = { id: requestId, action };
    if (!send('permission/rules', { requestId, action, root, sessionId: sessionId ?? '', ...extra })) {
      request.current = undefined; setError(t('error.hostUnavailable')); return;
    }
    setPending(true); setError(''); if (action === 'savePolicy') setFormError('');
  }
  useEffect(() => onMessage(message => {
    const current = request.current;
    if (message.type !== 'permission/rulesState' || !current || message.payload.requestId !== current.id) return;
    const action = current.action; request.current = undefined; setPending(false);
    if (message.payload.root !== root || message.payload.sessionId !== (sessionId ?? '')) {
      setState(undefined); setForm(undefined); setError(t('policy.conversationChanged')); return;
    }
    setState(message.payload);
    const failure = message.payload.error ?? '';
    const latest = message.payload.projectPolicy?.hash ?? null;
    // An open form made from another version of the file is stale (any reply can show that): it can only be reloaded, never saved.
    setForm(previous => previous && previous.baseHash !== latest ? { ...previous, stale: true } : previous);
    // A refused save keeps the form (and its text) open with the reason; a refused delete keeps the confirmation.
    if (action === 'savePolicy') { if (failure) setFormError(failure); else setForm(undefined); setError(failure && !formOpen.current ? failure : ''); return; }
    if (action === 'deletePolicy' && !failure) setConfirmDelete(undefined);
    setError(failure);
  }), [root, sessionId, t]);
  useEffect(() => { setState(undefined); setPending(false); setForm(undefined); setConfirmDelete(undefined); request.current = undefined; }, [root, sessionId]);
  useEffect(() => { if (active || countActive) command('refresh'); }, [active, countActive, root, sessionId]);
  useEffect(() => {
    if (!pending) return;
    const timer = window.setTimeout(() => { request.current = undefined; setPending(false); setError(t('tools.noReply')); }, 15000);
    return () => window.clearTimeout(timer);
  }, [pending, t]);

  const policy = state?.projectPolicy;
  const rules = policy?.status === 'valid' ? policy.rules ?? [] : [];
  const grants = state ? [...state.session.map(key => ({ scope: 'session' as const, key })), ...state.persistent.map(key => ({ scope: 'persistent' as const, key }))] : [];
  const trusted = state?.trustedWorkspaces ?? [];
  useEffect(() => { onCount?.(grants.length + trusted.length + rules.length); }, [grants.length, trusted.length, rules.length, onCount]);
  const pagedGrants = usePaged(grants, `${root}:${sessionId}`);
  const pagedTrusted = usePaged(trusted, root);
  const pagedRules = usePaged(rules, root);
  const busy = !active || pending;

  /** Sends the whole policy with one change; the base is the file the page shows. */
  function savePolicy(change: (draft: ProjectPolicyDraft) => ProjectPolicyDraft, baseHash = policy?.status === 'none' ? null : policy?.hash ?? null) {
    const current: ProjectPolicyDraft = { requireReviewedEdits: policy?.requireReviewedEdits === true, requireSandbox: policy?.requireSandbox === true, rules: rules.map(rule => ({ ...rule })) };
    const draft = change(current);
    command('savePolicy', { policy: draft, expectedHash: baseHash });
  }
  function openForm(index?: number) {
    setFormError(''); setConfirmDelete(undefined);
    const baseHash = policy?.status === 'valid' || policy?.status === 'invalid' ? policy.hash ?? null : null;
    // An unreadable file is rewritten from one rule: it starts as "every tool allowed", so the rewrite never blocks every tool by itself.
    const rule = index !== undefined ? toDraft(rules[index]) : { tool: policy?.status === 'invalid' ? '*' : '', effect: 'allow' as const, paths: '', commands: '' };
    setForm({ index, baseHash, rule });
  }
  // The names the form accepts without asking: this conversation's tools, when the engine reported them.
  const toolList = tools?.available && tools.tools.length ? tools.tools : undefined;
  const known = toolList ? new Set(toolList.map(tool => tool.name)) : undefined;
  const creating = policy?.status === 'none';
  const problem = form ? invalidRule(form.rule, t, known, form.index !== undefined ? rules[form.index]?.tool : undefined) : undefined;
  const [showProblem, setShowProblem] = useState(false);
  useEffect(() => { if (!form) setShowProblem(false); }, [form]);
  function submitForm() {
    if (!form || pending || form.stale) return;
    if (problem) { setShowProblem(true); return; }
    const index = form.index;
    if (index === undefined && policy?.status === 'valid' && rules.length >= MAX_RULES) { setFormError(t('policy.problem.tooManyRules', { count: MAX_RULES })); return; }
    const rule = toRule(form.rule);
    // A new file is default-deny: without "every tool allowed" first, one rule would block every other tool, so the form adds it (the user can remove it).
    const first = creating && !(rule.tool === '*' && rule.effect === 'allow') ? [{ tool: '*', effect: 'allow' as const }, rule] : [rule];
    // An unreadable file is replaced as a whole (it cannot be merged); otherwise the rule is put in its place or added at the end.
    savePolicy(draft => ({ ...draft, rules: policy?.status !== 'valid' ? first : index === undefined ? [...draft.rules, rule] : draft.rules.map((item, i) => i === index ? rule : item) }), form.baseHash);
  }
  const effectBadge = (effect: ProjectPolicyRule['effect']) => <Badge tone={effect === 'allow' ? 'ok' : 'error'}>{t(effect === 'allow' ? 'perm.allow' : 'perm.deny')}</Badge>;
  const setForms = (patch: Partial<RuleDraft>) => setForm(previous => previous && { ...previous, rule: { ...previous.rule, ...patch } });
  const date = (value: string) => Number.isFinite(Date.parse(value)) ? new Date(value).toLocaleDateString(locale, { year: 'numeric', month: 'numeric', day: 'numeric' }) : '';
  // The approval mode an allowed tool still goes through (the policy only narrows; a deny holds in every mode).
  const mode = state?.approvalMode === 'Yolo' ? t('perm.mode.yolo') : state?.approvalMode === 'Write' ? t('perm.mode.write') : t('perm.mode.alwaysAsk');
  const effectText = (effect: ProjectPolicyRule['effect']) => effect === 'allow' ? t('policy.effectText.allow', { mode }) : t('policy.effectText.deny');
  /** Why a saved rule decides nothing now (undefined: it applies from the next tool call on). */
  function idleReason(rule: ProjectPolicyRule): string | undefined {
    if (rule.effect === 'allow' && deniedOutright(rules, rule.tool)) return t('policy.idle.denied');
    if (rule.tool === '*' || !toolList) return undefined;
    const tool = toolList.find(item => item.name === rule.tool);
    if (!tool) return t('policy.idle.noTool');
    return tool.active ? undefined : t('policy.idle.off');
  }
  // This conversation's tools that no allow rule names, or that a deny rule blocks outright: every call of theirs is refused.
  const blocked = policy?.status === 'valid' && toolList ? toolList.filter(tool => !rules.some(rule => rule.effect === 'allow' && (rule.tool === '*' || rule.tool === tool.name)) || deniedOutright(rules, tool.name)).map(tool => tool.name) : [];
  return <div className="tools-scroll-body permissions-view" data-testid="permission-rules">
    {error && <p className="ui-note" role="alert" data-testid="permission-error">{error}</p>}
    <ViewSection sectionKey="tools-permission-grants" title={t('policy.grants.title')} status={grants.length} bodyClassName="ui-flush"
      actions={<><IconButton icon="settings" label={t('policy.openOptions')} disabled={!active} testId="permission-options" onClick={() => send('settings/open', {})} />
        <SectionRefresh onClick={() => command('refresh')} disabled={busy} testId="permission-refresh" /></>}>
      <DataTable columns="minmax(0, 1fr) auto 30px" label={t('policy.grants.title')} testId="permission-grants" empty={!grants.length}
        head={[t('policy.grants.action'), t('tcol.scope'), '']}>
        {pagedGrants.slice.map(({ scope, key }) => {
          const { tool, root: grantRoot } = grantParts(key);
          return <Row key={`${scope}:${key}`} testId={`permission-grant-${scope}-${key}`}>
            <Cell title={grantRoot ?? key}><strong className="ui-mono">{grantLabel(tool)}</strong>
              {grantRoot ? <span className="ui-sub ui-mono">{grantRoot}</span> : <span className="ui-sub">{t('policy.grants.anyFolder')}</span>}</Cell>
            <Cell><Badge tone={scope === 'session' ? 'info' : 'accent'}>{t(scope === 'session' ? 'policy.grants.session' : 'policy.grants.always')}</Badge></Cell>
            <Actions><IconButton icon="x" tone="danger" label={t('policy.grants.revoke')} disabled={busy} testId={`permission-revoke-${scope}-${key}`} onClick={() => command('revoke', { scope, key })} /></Actions>
          </Row>;
        })}
      </DataTable>
      <Pager paged={pagedGrants} testId="permission-grants-pager" />
    </ViewSection>
    <ViewSection sectionKey="tools-permission-trust" title={t('policy.trust.title')} status={trusted.length} bodyClassName="ui-flush" data-testid="trusted-workspaces">
      <DataTable columns="minmax(0, 1fr) auto 30px" label={t('policy.trust.title')} testId="permission-trusted" empty={!trusted.length}
        head={[t('policy.trust.folder'), t('policy.trust.date'), '']}>
        {pagedTrusted.slice.map(entry => <Row key={entry.root} testId={`trusted-${entry.root}`}>
          <Cell title={entry.root}><span className="perm-path"><Icon name="folder" size={13} /><span className="ui-mono">{entry.root}</span></span>
            <span className="ui-sub">{t('policy.trust.files', { count: entry.files })}</span></Cell>
          <Cell className="ui-muted perm-date">{date(entry.trustedUtc)}</Cell>
          <Actions><IconButton icon="x" tone="danger" label={t('policy.trust.revoke')} disabled={busy} testId={`untrust-${entry.root}`} onClick={() => command('untrust', { workspace: entry.root })} /></Actions>
        </Row>)}
      </DataTable>
      <Pager paged={pagedTrusted} testId="permission-trusted-pager" />
    </ViewSection>
    <ViewSection sectionKey="tools-permission-policy" title={t('policy.title')} bodyClassName="ui-flush" data-testid="project-policy"
      status={policy?.status === 'invalid' ? undefined : rules.length}
      actions={<>
        {policy?.status === 'invalid' && <Badge tone="error" testId="policy-unreadable">{t('state.unreadable')}</Badge>}
        {policy && policy.status !== 'invalid' && <button type="button" className="btn small" data-testid={policy.status === 'none' ? 'policy-create' : 'policy-add-rule'} disabled={busy || !!form} onClick={() => openForm()}>
          <Icon name="plus" size={12} />{t('policy.newRule')}</button>}
        {policy?.status === 'invalid' && <button type="button" className="btn small" data-testid="policy-rewrite" disabled={busy || !!form} onClick={() => openForm()}><Icon name="edit" size={12} />{t('policy.rewrite')}</button>}
        {policy && policy.status !== 'none' && <IconButton icon="external" label={t('ui.open')} disabled={busy} testId="policy-open" onClick={() => command('openPolicy')} />}
        {policy && policy.status !== 'none' && <IconButton icon="trash" tone="danger" label={t('policy.deleteFile')} disabled={busy || !!form || !policy.hash} testId="policy-delete"
          onClick={() => { if (policy.hash) { setForm(undefined); setConfirmDelete(policy.hash); } }} />}
      </>}>
      {policy?.status === 'invalid' && <p className="ui-note" role="alert" data-testid="policy-invalid">{t('policy.invalid')}{policy.error ? `: ${policy.error}` : ''}</p>}
      {policy?.status === 'none' && <p className="ui-note ui-muted" data-testid="policy-none">{t('policy.none')}</p>}
      {policy?.status === 'valid' && <div className="ui-note policy-summary" role="status" data-testid="policy-summary">
        <p>{t('policy.summary')}</p>
        {policy.requireSandbox && <p className="policy-warn" data-testid="policy-sandbox-blocks">{t('policy.sandboxBlocks')}</p>}
        {policy.requireReviewedEdits && <p className="policy-warn" data-testid="policy-reviewed-blocks">{t('policy.reviewedBlocks')}</p>}
        {blocked.length > 0 && <p className="policy-warn" title={blocked.join(', ')} data-testid="policy-blocked">
          {t('policy.blocked', { count: blocked.length })}<span className="ui-mono">{blocked.slice(0, 6).join(', ')}</span>{blocked.length > 6 ? t('policy.blockedMore', { count: blocked.length - 6 }) : ''}</p>}
      </div>}
      {policy?.status === 'valid' && <Toolbar>
        <Switch label={t('policy.reviewed')} checked={policy.requireReviewedEdits === true} disabled={busy || !!form} testId="policy-reviewed"
          onChange={next => savePolicy(draft => ({ ...draft, requireReviewedEdits: next }))} />
        <Switch label={t('policy.sandbox')} checked={policy.requireSandbox === true} disabled={busy || !!form} testId="policy-sandbox"
          onChange={next => savePolicy(draft => ({ ...draft, requireSandbox: next }))} />
      </Toolbar>}
      {confirmDelete && policy && policy.status !== 'none' && <ConfirmDelete t={t} testId="policy-delete-confirm" yesTestId="policy-delete-yes" question={t('policy.deleteQuestion')}
        locked={busy || policy.hash !== confirmDelete} pending={pending} onCancel={() => setConfirmDelete(undefined)} onConfirm={() => command('deletePolicy', { expectedHash: confirmDelete })}>
        {policy.hash !== confirmDelete && <p role="alert" className="ui-err" data-testid="policy-delete-changed">{t('policy.deleteChanged')}</p>}
      </ConfirmDelete>}
      {form && <PolicyRuleForm form={form} pending={pending} busy={busy} error={formError} problem={showProblem ? problem : undefined} tools={toolList} creating={creating} mode={mode}
        onChange={setForms} onSubmit={submitForm}
        onCancel={() => { setForm(undefined); setFormError(''); }} onReload={() => openForm(form.index !== undefined && form.index < rules.length ? form.index : undefined)} />}
      <DataTable columns="minmax(0, 1fr) auto 56px" label={t('policy.title')} testId="policy-rules" empty={!rules.length} emptyRows={1}
        head={[t('policy.rule'), t('policy.effect'), '']}>
        {pagedRules.slice.map((rule, offset) => {
          const index = pagedRules.page * 20 + offset;
          const idle = idleReason(rule);
          const label = rule.tool === '*' ? t('agents.tools.all') : rule.tool;
          return <Row key={index} testId={`policy-rule-${index}`} selected={form?.index === index}>
            <Cell title={`${label}: ${effectText(rule.effect)}`}>
              {rule.tool === '*' ? <strong>{label}</strong> : <strong className="ui-mono">{label}</strong>}
              <span className="ui-sub" data-testid={`policy-rule-${index}-text`}>{effectText(rule.effect)}</span>
              {rule.paths && <span className="ui-sub" title={rule.paths.join('\n')}>{t('policy.paths')}<span className="ui-mono">{rule.paths.join(', ')}</span></span>}
              {rule.commands && <span className="ui-sub" title={rule.commands.join('\n')}>{t('policy.commands')}<span className="ui-mono">{rule.commands.join(', ')}</span></span>}
              {idle && <span className="ui-sub policy-warn" data-testid={`policy-rule-${index}-idle`}>{idle}</span>}
            </Cell>
            <Cell>{effectBadge(rule.effect)}</Cell>
            <Actions>
              <IconButton icon="edit" label={t('policy.editRule')} disabled={busy || !!form} testId={`policy-rule-${index}-edit`} onClick={() => openForm(index)} />
              <IconButton icon="trash" tone="danger" label={t('policy.deleteRule')} disabled={busy || !!form} testId={`policy-rule-${index}-remove`}
                onClick={() => savePolicy(draft => ({ ...draft, rules: draft.rules.filter((_, i) => i !== index) }))} />
            </Actions>
          </Row>;
        })}
      </DataTable>
      <Pager paged={pagedRules} testId="policy-rules-pager" />
    </ViewSection>
  </div>;
}

/** One rule of the project policy: tool (picked from this conversation's tools), allow/deny, and the paths and commands it is limited to (one per line). */
function PolicyRuleForm({ form, pending, busy, error, problem, tools, creating, mode, onChange, onSubmit, onCancel, onReload }: {
  form: RuleForm; pending: boolean; busy: boolean; error: string; problem?: RuleProblem; tools?: ConversationTool[];
  creating: boolean; mode: string;
  onChange: (patch: Partial<RuleDraft>) => void; onSubmit: () => void; onCancel: () => void; onReload: () => void;
}) {
  const t = useT(); const { rule } = form;
  const fieldError = (field: RuleField) => problem?.field === field ? problem.message : undefined;
  return <InPageDialog title={t(form.index === undefined ? 'policy.newRule' : 'policy.editRule')} testId="policy-editor" onClose={onCancel} pending={pending}>
    <form noValidate className="ui-form policy-form" onSubmit={event => { event.preventDefault(); onSubmit(); }}><fieldset disabled={busy} className="policy-fieldset">
      {creating && <p className="hint" data-testid="policy-create-note">{t('policy.createNote')}</p>}
      <Field label={t('perm.tool')} error={fieldError('tool')}>{id => <>
        <ToolPicker id={id} value={rule.tool} tools={tools} invalid={!!fieldError('tool')} onChange={tool => onChange({ tool })} />
        <span className="hint">{t(tools ? 'policy.toolHint' : 'policy.toolHintUnchecked')}</span>
      </>}</Field>
      <Field label={t('policy.effect')}>{() => <>
        <Segmented<RuleDraft['effect']> label={t('policy.effect')} testId="policy-rule-effect" value={rule.effect} onChange={effect => onChange({ effect })}
          options={[{ id: 'allow', label: t('perm.allow'), testId: 'policy-rule-allow' }, { id: 'deny', label: t('perm.deny'), testId: 'policy-rule-deny' }]} />
        <span className="hint" data-testid="policy-effect-hint">{rule.effect === 'allow' ? t('policy.effectHint.allow', { mode }) : t('policy.effectHint.deny')}</span>
      </>}</Field>
      <Field label={t('policy.pathsLabel')} error={fieldError('paths')}>{id => <>
        <textarea id={id} data-testid="policy-rule-paths" rows={2} spellCheck={false} className="ui-mono" placeholder={t('policy.pathsExample')} aria-invalid={!!fieldError('paths') || undefined} value={rule.paths} onChange={event => onChange({ paths: event.target.value })} />
        <span className="hint">{t('policy.pathsHint')}</span>
      </>}</Field>
      <Field label={t('policy.commandsLabel')} error={fieldError('commands')}>{id => <>
        <textarea id={id} data-testid="policy-rule-commands" rows={2} spellCheck={false} className="ui-mono" placeholder={t('policy.commandsExample')} aria-invalid={!!fieldError('commands') || undefined} value={rule.commands} onChange={event => onChange({ commands: event.target.value })} />
        <span className="hint">{t('policy.commandsHint')}</span>
      </>}</Field>
    </fieldset>
    {error && <p className="ui-err" role="alert" data-testid="policy-validation">{error}</p>}
    {form.stale && <p className="ui-err" role="alert" data-testid="policy-stale">{t('policy.stale')}</p>}
    <FormActions>
      {form.stale && <button type="button" className="btn subtle" data-testid="policy-reload" disabled={pending} onClick={onReload}><Icon name="restart" size={12} />{t('setup.reload')}</button>}
      <button type="button" className="btn" data-testid="policy-cancel" disabled={pending} onClick={onCancel}>{t('ui.cancel')}</button>
      <button type="submit" className="btn primary" data-testid="policy-save" disabled={busy || form.stale}>{t('ui.save')}</button>
    </FormActions></form>
  </InPageDialog>;
}

/**
 * The tool field: a search box over this conversation's tools (and `*` for every tool), with the matches listed under it. A chosen name lists
 * everything again (the choice marked) so another one is a click away; a typed name stays as written. Enter on a highlighted tool chooses it.
 */
function ToolPicker({ id, value, tools, invalid, onChange }: {
  id: string; value: string; tools?: ConversationTool[]; invalid: boolean; onChange: (tool: string) => void;
}) {
  const listId = useId(); const t = useT();
  const [picked, setPicked] = useState(-1);
  const all = [{ name: '*', hint: t('agents.tools.all'), off: false },
    ...[...(tools ?? [])].sort((a, b) => a.name.localeCompare(b.name)).map(tool => ({ name: tool.name, hint: tool.group === 'mcp' ? t('tools.group.mcp', { server: tool.server ?? '' }) : t(`tools.group.${tool.group}`), off: !tool.active }))];
  const query = value.trim().toLowerCase();
  const chosen = all.some(option => option.name.toLowerCase() === query);
  const options = !query || chosen ? all : all.filter(option => option.name.toLowerCase().includes(query) || option.hint.toLowerCase().includes(query));
  const active = picked < options.length ? picked : -1;
  const selected = options.findIndex(option => option.name === value.trim());
  const scrolled = active >= 0 ? active : selected;
  useEffect(() => { if (scrolled >= 0) document.getElementById(`${listId}-${scrolled}`)?.scrollIntoView({ block: 'nearest' }); }, [scrolled, listId]);
  function choose(name: string) { onChange(name); setPicked(-1); }
  function onKeyDown(event: KeyboardEvent<HTMLInputElement>) {
    // Enter that ends a Korean composition is not a choice (the legacy IME keyCode as well).
    if (event.nativeEvent.isComposing || event.keyCode === 229 || !options.length) return;
    if (event.key === 'ArrowDown' || event.key === 'ArrowUp') {
      event.preventDefault();
      const from = active >= 0 ? active : selected;
      setPicked(event.key === 'ArrowDown' ? (from + 1) % options.length : (from <= 0 ? options.length : from) - 1);
    } else if (event.key === 'Enter' && active >= 0 && options[active].name !== value.trim()) {
      event.preventDefault(); choose(options[active].name);
    }
  }
  return <>
    <input id={id} data-testid="policy-rule-tool" value={value} maxLength={100} spellCheck={false} autoComplete="off" className="ui-mono"
      placeholder={t('policy.toolSearch')} role="combobox" aria-expanded="true" aria-controls={listId} aria-autocomplete="list"
      aria-activedescendant={active >= 0 ? `${listId}-${active}` : undefined} aria-invalid={invalid || undefined}
      onChange={event => { setPicked(-1); onChange(event.target.value); }} onKeyDown={onKeyDown} />
    <div className="policy-tool-list" id={listId} role="listbox" aria-label={t('policy.toolList')} data-testid="policy-tool-list">
      {options.map((option, index) => <button type="button" role="option" tabIndex={-1} key={option.name} id={`${listId}-${index}`} aria-selected={index === selected}
        className={`policy-tool-option${index === active ? ' active' : ''}${index === selected ? ' current' : ''}`} data-testid={`policy-tool-option-${option.name}`}
        onMouseDown={event => event.preventDefault()} onClick={() => choose(option.name)}>
        <span className="ui-mono">{option.name}</span><span className="hint">{option.hint}{option.off ? t('policy.toolOff') : ''}</span>
      </button>)}
      {!options.length && <p className="hint policy-tool-empty" data-testid="policy-tool-none">{t('policy.toolNone')}</p>}
    </div>
  </>;
}
