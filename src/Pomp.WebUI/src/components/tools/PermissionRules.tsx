import { useEffect, useId, useRef, useState, type KeyboardEvent } from 'react';
import { useT } from '../../i18n';
import { toolsTranslate } from '../../i18n/toolsStrings';
import { newId } from '../../format';
import { onMessage, send, type PermissionRulesState, type ProjectPolicyDraft, type ProjectPolicyRule, type UiToHost } from '../../protocol';
import type { ConversationTool, ToolsStatePayload } from '../../toolsProtocol';
import { Icon } from '../Icon';
import { SectionRefresh, ViewSection } from '../ViewSection';
import { Actions, Badge, Cell, DataTable, Field, FormActions, IconButton, Pager, Row, Segmented, Switch, Toolbar, usePaged } from '../ui';
import { InPageDialog } from './InPageDialog';
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
function invalidRule(rule: RuleDraft, ko: boolean, known: ReadonlySet<string> | undefined, original?: string): RuleProblem | undefined {
  const tool = rule.tool.trim();
  if (!tool) return { field: 'tool', message: ko ? '도구를 골라 주세요.' : 'Choose a tool.' };
  if (!POLICY_TOOL.test(tool)) return { field: 'tool', message: ko ? '도구 이름에는 영문, 숫자와 _ . : - 만 쓸 수 있어요.' : 'A tool name uses only letters, digits and _ . : -' };
  if (known && tool !== '*' && tool !== original && !known.has(tool)) return { field: 'tool', message: ko ? `이 대화에 ${tool} 도구가 없어요. 목록에서 골라 주세요.` : `This conversation has no ${tool} tool. Choose one from the list.` };
  const paths = lines(rule.paths), commands = lines(rule.commands);
  for (const [field, list] of [['paths', paths], ['commands', commands]] as const) {
    if (list.length > MAX_LINES) return { field, message: ko ? `한 규칙에 ${MAX_LINES}줄까지 쓸 수 있어요.` : `A rule takes at most ${MAX_LINES} lines.` };
    if (list.some(value => value.length > 4096)) return { field, message: ko ? '한 줄이 너무 길어요.' : 'A line is too long.' };
  }
  if (paths.some(path => /[*?]/.test(path))) return { field: 'paths', message: ko ? '경로에는 *, ? 같은 패턴을 쓸 수 없어요. 폴더나 파일을 적어 주세요.' : 'Paths take no patterns such as * or ?. Write a folder or file.' };
  if (paths.some(path => /^[A-Za-z]:/.test(path) || /^[\\/]/.test(path) || path.includes('~') || path.replace(/\\/g, '/').split('/').includes('..')))
    return { field: 'paths', message: ko ? '경로는 작업 영역 기준으로 적어 주세요. 예: src' : 'Write paths relative to the workspace, e.g. src' };
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
  const ko = locale.startsWith('ko'); const t = useT();
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
      setState(undefined); setForm(undefined); setError(ko ? '대화가 바뀌었어요.' : 'Conversation changed.'); return;
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
  }), [root, sessionId, ko]);
  useEffect(() => { setState(undefined); setPending(false); setForm(undefined); setConfirmDelete(undefined); request.current = undefined; }, [root, sessionId]);
  useEffect(() => { if (active || countActive) command('refresh'); }, [active, countActive, root, sessionId]);
  useEffect(() => {
    if (!pending) return;
    const timer = window.setTimeout(() => { request.current = undefined; setPending(false); setError(ko ? '결과를 받지 못했어요.' : 'No reply received.'); }, 15000);
    return () => window.clearTimeout(timer);
  }, [pending, ko]);

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
  const problem = form ? invalidRule(form.rule, ko, known, form.index !== undefined ? rules[form.index]?.tool : undefined) : undefined;
  const [showProblem, setShowProblem] = useState(false);
  useEffect(() => { if (!form) setShowProblem(false); }, [form]);
  function submitForm() {
    if (!form || pending || form.stale) return;
    if (problem) { setShowProblem(true); return; }
    const index = form.index;
    if (index === undefined && policy?.status === 'valid' && rules.length >= MAX_RULES) { setFormError(ko ? `규칙은 ${MAX_RULES}개까지 둘 수 있어요.` : `A policy holds at most ${MAX_RULES} rules.`); return; }
    const rule = toRule(form.rule);
    // A new file is default-deny: without "every tool allowed" first, one rule would block every other tool, so the form adds it (the user can remove it).
    const first = creating && !(rule.tool === '*' && rule.effect === 'allow') ? [{ tool: '*', effect: 'allow' as const }, rule] : [rule];
    // An unreadable file is replaced as a whole (it cannot be merged); otherwise the rule is put in its place or added at the end.
    savePolicy(draft => ({ ...draft, rules: policy?.status !== 'valid' ? first : index === undefined ? [...draft.rules, rule] : draft.rules.map((item, i) => i === index ? rule : item) }), form.baseHash);
  }
  const effectBadge = (effect: ProjectPolicyRule['effect']) => <Badge tone={effect === 'allow' ? 'ok' : 'error'}>{effect === 'allow' ? (ko ? '허용' : 'Allow') : (ko ? '거부' : 'Deny')}</Badge>;
  const setForms = (patch: Partial<RuleDraft>) => setForm(previous => previous && { ...previous, rule: { ...previous.rule, ...patch } });
  const date = (value: string) => Number.isFinite(Date.parse(value)) ? new Date(value).toLocaleDateString(locale, { year: 'numeric', month: 'numeric', day: 'numeric' }) : '';
  // The approval mode an allowed tool still goes through (the policy only narrows; a deny holds in every mode).
  const mode = state?.approvalMode === 'Yolo' ? t('perm.mode.yolo') : state?.approvalMode === 'Write' ? t('perm.mode.write') : t('perm.mode.alwaysAsk');
  const effectText = (effect: ProjectPolicyRule['effect']) => effect === 'allow' ? (ko ? `허용, 승인은 '${mode}' 모드대로` : `Allowed, approval per '${mode}'`) : (ko ? '항상 거부, 모든 권한 모드에서' : 'Always denied, in every permission mode');
  /** Why a saved rule decides nothing now (undefined: it applies from the next tool call on). */
  function idleReason(rule: ProjectPolicyRule): string | undefined {
    if (rule.effect === 'allow' && deniedOutright(rules, rule.tool)) return ko ? '거부 규칙이 우선해 적용되지 않아요.' : 'A deny rule wins, so this has no effect.';
    if (rule.tool === '*' || !toolList) return undefined;
    const tool = toolList.find(item => item.name === rule.tool);
    if (!tool) return ko ? '이 대화에 없는 도구예요.' : 'This conversation has no such tool.';
    return tool.active ? undefined : (ko ? '이 대화에서 꺼져 있어 호출되지 않아요.' : 'Turned off in this conversation, so never called.');
  }
  // This conversation's tools that no allow rule names, or that a deny rule blocks outright: every call of theirs is refused.
  const blocked = policy?.status === 'valid' && toolList ? toolList.filter(tool => !rules.some(rule => rule.effect === 'allow' && (rule.tool === '*' || rule.tool === tool.name)) || deniedOutright(rules, tool.name)).map(tool => tool.name) : [];
  return <div className="tools-scroll-body permissions-view" data-testid="permission-rules">
    {error && <p className="ui-note" role="alert" data-testid="permission-error">{error}</p>}
    <ViewSection sectionKey="tools-permission-grants" title={ko ? '허용한 작업' : 'Allowed actions'} status={grants.length} bodyClassName="ui-flush"
      actions={<><IconButton icon="settings" label={ko ? 'VS 옵션 열기' : 'Open VS options'} disabled={!active} testId="permission-options" onClick={() => send('settings/open', {})} />
        <SectionRefresh onClick={() => command('refresh')} disabled={busy} testId="permission-refresh" /></>}>
      <DataTable columns="minmax(0, 1fr) auto 30px" label={ko ? '허용한 작업' : 'Allowed actions'} testId="permission-grants" empty={!grants.length}
        head={[ko ? '작업' : 'Action', ko ? '범위' : 'Scope', '']}>
        {pagedGrants.slice.map(({ scope, key }) => {
          const { tool, root: grantRoot } = grantParts(key);
          return <Row key={`${scope}:${key}`} testId={`permission-grant-${scope}-${key}`}>
            <Cell title={grantRoot ?? key}><strong className="ui-mono">{grantLabel(tool)}</strong>
              {grantRoot ? <span className="ui-sub ui-mono">{grantRoot}</span> : <span className="ui-sub">{ko ? '모든 폴더' : 'Any folder'}</span>}</Cell>
            <Cell><Badge tone={scope === 'session' ? 'info' : 'accent'}>{scope === 'session' ? (ko ? '이 연결' : 'This connection') : (ko ? '항상' : 'Always')}</Badge></Cell>
            <Actions><IconButton icon="x" tone="danger" label={ko ? '허용 철회' : 'Revoke'} disabled={busy} testId={`permission-revoke-${scope}-${key}`} onClick={() => command('revoke', { scope, key })} /></Actions>
          </Row>;
        })}
      </DataTable>
      <Pager paged={pagedGrants} testId="permission-grants-pager" />
    </ViewSection>
    <ViewSection sectionKey="tools-permission-trust" title={ko ? '신뢰한 저장소' : 'Trusted repositories'} status={trusted.length} bodyClassName="ui-flush" data-testid="trusted-workspaces">
      <DataTable columns="minmax(0, 1fr) auto 30px" label={ko ? '신뢰한 저장소' : 'Trusted repositories'} testId="permission-trusted" empty={!trusted.length}
        head={[ko ? '폴더' : 'Folder', ko ? '신뢰한 날' : 'Trusted', '']}>
        {pagedTrusted.slice.map(entry => <Row key={entry.root} testId={`trusted-${entry.root}`}>
          <Cell title={entry.root}><span className="perm-path"><Icon name="folder" size={13} /><span className="ui-mono">{entry.root}</span></span>
            <span className="ui-sub">{ko ? `설정 파일 ${entry.files}개` : `${entry.files} config files`}</span></Cell>
          <Cell className="ui-muted perm-date">{date(entry.trustedUtc)}</Cell>
          <Actions><IconButton icon="x" tone="danger" label={ko ? '신뢰 해제' : 'Revoke trust'} disabled={busy} testId={`untrust-${entry.root}`} onClick={() => command('untrust', { workspace: entry.root })} /></Actions>
        </Row>)}
      </DataTable>
      <Pager paged={pagedTrusted} testId="permission-trusted-pager" />
    </ViewSection>
    <ViewSection sectionKey="tools-permission-policy" title={ko ? '프로젝트 정책' : 'Project policy'} bodyClassName="ui-flush" data-testid="project-policy"
      status={policy?.status === 'invalid' ? undefined : rules.length}
      actions={<>
        {policy?.status === 'invalid' && <Badge tone="error" testId="policy-unreadable">{ko ? '읽을 수 없음' : 'Unreadable'}</Badge>}
        {policy && policy.status !== 'invalid' && <button type="button" className="btn small" data-testid={policy.status === 'none' ? 'policy-create' : 'policy-add-rule'} disabled={busy || !!form} onClick={() => openForm()}>
          <Icon name="plus" size={12} />{ko ? '규칙' : 'Rule'}</button>}
        {policy?.status === 'invalid' && <button type="button" className="btn small" data-testid="policy-rewrite" disabled={busy || !!form} onClick={() => openForm()}><Icon name="edit" size={12} />{ko ? '새로 작성' : 'Rewrite'}</button>}
        {policy && policy.status !== 'none' && <IconButton icon="external" label={ko ? 'VS에서 열기' : 'Open in VS'} disabled={busy} testId="policy-open" onClick={() => command('openPolicy')} />}
        {policy && policy.status !== 'none' && <IconButton icon="trash" tone="danger" label={ko ? '정책 파일 삭제' : 'Delete policy file'} disabled={busy || !!form || !policy.hash} testId="policy-delete"
          onClick={() => { if (policy.hash) { setForm(undefined); setConfirmDelete(policy.hash); } }} />}
      </>}>
      {policy?.status === 'invalid' && <p className="ui-note" role="alert" data-testid="policy-invalid">{ko ? '정책 파일을 읽을 수 없어 프로젝트 도구가 막혀 있어요' : 'The policy file cannot be read, so project tools are blocked'}{policy.error ? `: ${policy.error}` : ''}</p>}
      {policy?.status === 'none' && <p className="ui-note ui-muted" data-testid="policy-none">{ko ? '정책 파일이 없어 일반 승인만 적용돼요. + 규칙으로 도구를 막거나 허용해요.' : 'No policy file: only the normal approvals apply. Use + Rule to block or allow tools.'}</p>}
      {policy?.status === 'valid' && <div className="ui-note policy-summary" role="status" data-testid="policy-summary">
        <p>{ko ? '저장하면 다음 도구 호출부터 바로 적용돼요. 허용 규칙에 없는 도구는 막히고, 거부가 허용보다 우선해요.' : 'Saved rules apply from the next tool call. Tools no allow rule names are blocked, and deny wins over allow.'}</p>
        {policy.requireSandbox && <p className="policy-warn" data-testid="policy-sandbox-blocks">{ko ? 'OS 격리를 요구해 지금은 모든 프로젝트 도구가 막혀요.' : 'OS sandbox is required, so every project tool is blocked now.'}</p>}
        {policy.requireReviewedEdits && <p className="policy-warn" data-testid="policy-reviewed-blocks">{ko ? '검토 후 편집이 켜져 있어 읽기 도구와 VS 도구 말고는 막혀요.' : 'Reviewed edits is on, so everything but read and VS tools is blocked.'}</p>}
        {blocked.length > 0 && <p className="policy-warn" title={blocked.join(', ')} data-testid="policy-blocked">
          {ko ? `막히는 도구 ${blocked.length}개: ` : `${blocked.length} blocked tools: `}<span className="ui-mono">{blocked.slice(0, 6).join(', ')}</span>{blocked.length > 6 ? (ko ? ` 외 ${blocked.length - 6}개` : ` and ${blocked.length - 6} more`) : ''}</p>}
      </div>}
      {policy?.status === 'valid' && <Toolbar>
        <Switch label={ko ? '검토 후 편집' : 'Reviewed edits'} checked={policy.requireReviewedEdits === true} disabled={busy || !!form} testId="policy-reviewed"
          onChange={next => savePolicy(draft => ({ ...draft, requireReviewedEdits: next }))} />
        <Switch label={ko ? 'OS 격리' : 'OS sandbox'} checked={policy.requireSandbox === true} disabled={busy || !!form} testId="policy-sandbox"
          onChange={next => savePolicy(draft => ({ ...draft, requireSandbox: next }))} />
      </Toolbar>}
      {confirmDelete && policy && policy.status !== 'none' && <InPageDialog title={ko ? '정책 파일 삭제' : 'Delete the policy file'} testId="policy-delete-confirm" onClose={() => setConfirmDelete(undefined)} pending={pending}>
        <div className="ui-form"><p>{ko ? '정책 파일을 지울까요?' : 'Delete the policy file?'}</p>
        {policy.hash !== confirmDelete && <p role="alert" data-testid="policy-delete-changed">{ko ? '정책 파일이 그 사이 바뀌었어요.' : 'The policy file changed meanwhile.'}</p>}
        <FormActions><button type="button" className="btn" data-autofocus disabled={pending} onClick={() => setConfirmDelete(undefined)}>{ko ? '취소' : 'Cancel'}</button>
          <button type="button" className="btn danger" data-testid="policy-delete-yes" disabled={busy || policy.hash !== confirmDelete} onClick={() => command('deletePolicy', { expectedHash: confirmDelete })}>{ko ? '삭제' : 'Delete'}</button></FormActions></div>
      </InPageDialog>}
      {form && <PolicyRuleForm ko={ko} locale={locale} form={form} pending={pending} busy={busy} error={formError} problem={showProblem ? problem : undefined} tools={toolList} creating={creating} mode={mode}
        onChange={setForms} onSubmit={submitForm}
        onCancel={() => { setForm(undefined); setFormError(''); }} onReload={() => openForm(form.index !== undefined && form.index < rules.length ? form.index : undefined)} />}
      <DataTable columns="minmax(0, 1fr) auto 56px" label={ko ? '프로젝트 정책' : 'Project policy'} testId="policy-rules" empty={!rules.length} emptyRows={1}
        head={[ko ? '규칙' : 'Rule', ko ? '동작' : 'Effect', '']}>
        {pagedRules.slice.map((rule, offset) => {
          const index = pagedRules.page * 20 + offset;
          const idle = idleReason(rule);
          const label = rule.tool === '*' ? (ko ? '모든 도구' : 'All tools') : rule.tool;
          return <Row key={index} testId={`policy-rule-${index}`} selected={form?.index === index}>
            <Cell title={`${label}: ${effectText(rule.effect)}`}>
              {rule.tool === '*' ? <strong>{label}</strong> : <strong className="ui-mono">{label}</strong>}
              <span className="ui-sub" data-testid={`policy-rule-${index}-text`}>{effectText(rule.effect)}</span>
              {rule.paths && <span className="ui-sub" title={rule.paths.join('\n')}>{ko ? '경로: ' : 'Paths: '}<span className="ui-mono">{rule.paths.join(', ')}</span></span>}
              {rule.commands && <span className="ui-sub" title={rule.commands.join('\n')}>{ko ? '명령: ' : 'Commands: '}<span className="ui-mono">{rule.commands.join(', ')}</span></span>}
              {idle && <span className="ui-sub policy-warn" data-testid={`policy-rule-${index}-idle`}>{idle}</span>}
            </Cell>
            <Cell>{effectBadge(rule.effect)}</Cell>
            <Actions>
              <IconButton icon="edit" label={ko ? '규칙 편집' : 'Edit rule'} disabled={busy || !!form} testId={`policy-rule-${index}-edit`} onClick={() => openForm(index)} />
              <IconButton icon="trash" tone="danger" label={ko ? '규칙 삭제' : 'Delete rule'} disabled={busy || !!form} testId={`policy-rule-${index}-remove`}
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
function PolicyRuleForm({ ko, locale, form, pending, busy, error, problem, tools, creating, mode, onChange, onSubmit, onCancel, onReload }: {
  ko: boolean; locale: string; form: RuleForm; pending: boolean; busy: boolean; error: string; problem?: RuleProblem; tools?: ConversationTool[];
  creating: boolean; mode: string;
  onChange: (patch: Partial<RuleDraft>) => void; onSubmit: () => void; onCancel: () => void; onReload: () => void;
}) {
  const { rule } = form;
  const fieldError = (field: RuleField) => problem?.field === field ? problem.message : undefined;
  return <InPageDialog title={form.index === undefined ? (ko ? '새 규칙' : 'New rule') : (ko ? '규칙 편집' : 'Edit rule')} testId="policy-editor" onClose={onCancel} pending={pending}>
    <form noValidate className="ui-form policy-form" onSubmit={event => { event.preventDefault(); onSubmit(); }}><fieldset disabled={busy} className="policy-fieldset">
      {creating && <p className="hint" data-testid="policy-create-note">{ko ? "정책 파일이 새로 생겨요. 다른 도구가 막히지 않게 '모든 도구: 허용' 규칙이 함께 들어가요." : "This creates the policy file. An 'All tools: allow' rule is added too, so other tools stay usable."}</p>}
      <Field label={ko ? '도구' : 'Tool'} error={fieldError('tool')}>{id => <>
        <ToolPicker id={id} ko={ko} locale={locale} value={rule.tool} tools={tools} invalid={!!fieldError('tool')} onChange={tool => onChange({ tool })} />
        <span className="hint">{tools ? (ko ? '이 대화의 도구에서 골라요. *는 모든 도구예요.' : "Pick one of this conversation's tools. * means every tool.") : (ko ? '도구 목록을 받지 못해 이름을 확인하지 않아요. *는 모든 도구예요.' : 'The tool list is unavailable, so names are not checked. * means every tool.')}</span>
      </>}</Field>
      <Field label={ko ? '동작' : 'Effect'}>{() => <>
        <Segmented<RuleDraft['effect']> label={ko ? '동작' : 'Effect'} testId="policy-rule-effect" value={rule.effect} onChange={effect => onChange({ effect })}
          options={[{ id: 'allow', label: ko ? '허용' : 'Allow', testId: 'policy-rule-allow' }, { id: 'deny', label: ko ? '거부' : 'Deny', testId: 'policy-rule-deny' }]} />
        <span className="hint" data-testid="policy-effect-hint">{rule.effect === 'allow' ? (ko ? `맞는 호출은 실행할 수 있어요. 승인은 '${mode}' 모드대로 받아요.` : `Matching calls may run; approval still follows '${mode}'.`) : (ko ? '맞는 호출은 권한 모드와 상관없이 막아요.' : 'Matching calls are blocked in every permission mode.')}</span>
      </>}</Field>
      <Field label={ko ? '경로 (선택)' : 'Paths (optional)'} error={fieldError('paths')}>{id => <>
        <textarea id={id} data-testid="policy-rule-paths" rows={2} spellCheck={false} className="ui-mono" placeholder={'src\ndocs/guide.md'} aria-invalid={!!fieldError('paths') || undefined} value={rule.paths} onChange={event => onChange({ paths: event.target.value })} />
        <span className="hint">{ko ? '작업 영역 기준 폴더나 파일, 한 줄에 하나. 비우면 모든 경로예요.' : 'Workspace folders or files, one per line. Empty means any path.'}</span>
      </>}</Field>
      <Field label={ko ? '명령 (선택)' : 'Commands (optional)'} error={fieldError('commands')}>{id => <>
        <textarea id={id} data-testid="policy-rule-commands" rows={2} spellCheck={false} className="ui-mono" placeholder="dotnet build" aria-invalid={!!fieldError('commands') || undefined} value={rule.commands} onChange={event => onChange({ commands: event.target.value })} />
        <span className="hint">{ko ? '명령 전체가 글자 그대로 같을 때만 맞아요. 한 줄에 하나, 비우면 모든 명령이에요.' : 'Matches only the exact full command, one per line. Empty means any command.'}</span>
      </>}</Field>
    </fieldset>
    {error && <p className="ui-err" role="alert" data-testid="policy-validation">{error}</p>}
    {form.stale && <p className="ui-err" role="alert" data-testid="policy-stale">{ko ? '편집하는 동안 정책 파일이 바뀌었어요.' : 'The policy file changed while you were editing.'}</p>}
    <FormActions>
      {form.stale && <button type="button" className="btn subtle" data-testid="policy-reload" disabled={pending} onClick={onReload}><Icon name="restart" size={12} />{ko ? '다시 불러오기' : 'Reload'}</button>}
      <button type="button" className="btn" data-testid="policy-cancel" disabled={pending} onClick={onCancel}>{ko ? '취소' : 'Cancel'}</button>
      <button type="submit" className="btn primary" data-testid="policy-save" disabled={busy || form.stale}>{ko ? '저장' : 'Save'}</button>
    </FormActions></form>
  </InPageDialog>;
}

/**
 * The tool field: a search box over this conversation's tools (and `*` for every tool), with the matches listed under it. A chosen name lists
 * everything again (the choice marked) so another one is a click away; a typed name stays as written. Enter on a highlighted tool chooses it.
 */
function ToolPicker({ id, ko, locale, value, tools, invalid, onChange }: {
  id: string; ko: boolean; locale: string; value: string; tools?: ConversationTool[]; invalid: boolean; onChange: (tool: string) => void;
}) {
  const listId = useId(); const t = toolsTranslate(locale);
  const [picked, setPicked] = useState(-1);
  const all = [{ name: '*', hint: ko ? '모든 도구' : 'All tools', off: false },
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
      placeholder={ko ? '도구 검색, 예: bash' : 'Search tools, e.g. bash'} role="combobox" aria-expanded="true" aria-controls={listId} aria-autocomplete="list"
      aria-activedescendant={active >= 0 ? `${listId}-${active}` : undefined} aria-invalid={invalid || undefined}
      onChange={event => { setPicked(-1); onChange(event.target.value); }} onKeyDown={onKeyDown} />
    <div className="policy-tool-list" id={listId} role="listbox" aria-label={ko ? '도구 목록' : 'Tool list'} data-testid="policy-tool-list">
      {options.map((option, index) => <button type="button" role="option" tabIndex={-1} key={option.name} id={`${listId}-${index}`} aria-selected={index === selected}
        className={`policy-tool-option${index === active ? ' active' : ''}${index === selected ? ' current' : ''}`} data-testid={`policy-tool-option-${option.name}`}
        onMouseDown={event => event.preventDefault()} onClick={() => choose(option.name)}>
        <span className="ui-mono">{option.name}</span><span className="hint">{option.hint}{option.off ? (ko ? ' · 꺼짐' : ' · off') : ''}</span>
      </button>)}
      {!options.length && <p className="hint policy-tool-empty" data-testid="policy-tool-none">{ko ? '맞는 도구가 없어요.' : 'No matching tool.'}</p>}
    </div>
  </>;
}
