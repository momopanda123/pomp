import { useEffect, useRef, useState } from 'react';
import { useT } from '../../i18n';
import { newId } from '../../format';
import { onMessage, send, type PermissionRulesState, type ProjectPolicyDraft, type ProjectPolicyRule, type UiToHost } from '../../protocol';
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
/** Mirrors the host's checks (ProjectExecutionPolicy) so the form says what is wrong before saving; the host stays authoritative. */
function invalidRule(rule: RuleDraft, ko: boolean): string | undefined {
  if (!/^(?:\*|[a-zA-Z0-9_.:-]{1,100})$/.test(rule.tool.trim())) return ko ? '도구 이름 형식이 올바르지 않아요.' : 'The tool name is invalid.';
  for (const list of [lines(rule.paths), lines(rule.commands)]) {
    if (list.some(value => value.length > 4096)) return ko ? '경로·명령이 너무 길어요.' : 'A path or command is too long.';
  }
  if (lines(rule.paths).some(path => /^[A-Za-z]:/.test(path) || /^[\\/]/.test(path) || path.includes('~') || path.replace(/\\/g, '/').split('/').includes('..')))
    return ko ? '경로 형식이 올바르지 않아요.' : 'A path is invalid.';
  return undefined;
}
const toRule = (rule: RuleDraft): ProjectPolicyRule => {
  const paths = lines(rule.paths), commands = lines(rule.commands);
  return { tool: rule.tool.trim(), effect: rule.effect, ...(paths.length ? { paths } : {}), ...(commands.length ? { commands } : {}) };
};
const toDraft = (rule: ProjectPolicyRule): RuleDraft => ({ tool: rule.tool, effect: rule.effect, paths: (rule.paths ?? []).join('\n'), commands: (rule.commands ?? []).join('\n') });

/**
 * Tools › 권한: what the user allowed (remembered approvals), which repositories they trusted, and the project's tool policy
 * (.pomp/permissions.json) as a table of rules. The approval mode and the other permission options live in Options › Pomp, not here.
 * `countActive`: the Tools tab is shown, so the list is read once for the chip count even while another view is open.
 */
export function PermissionRules({ locale, root, sessionId, active, countActive = false, onCount }: {
  locale: string; root: string; sessionId?: string; active: boolean; countActive?: boolean; onCount?: (count: number) => void;
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
    // A new file starts like the editor always did: everything allowed, so creating a policy never blocks every tool by itself.
    const rule = index !== undefined ? toDraft(rules[index]) : { tool: policy?.status === 'valid' ? '' : '*', effect: 'allow' as const, paths: '', commands: '' };
    setForm({ index, baseHash, rule });
  }
  function submitForm() {
    if (!form || pending || form.stale) return;
    const problem = invalidRule(form.rule, ko);
    if (problem) { setFormError(problem); return; }
    const rule = toRule(form.rule); const index = form.index;
    // An unreadable file is replaced as a whole (it cannot be merged); otherwise the rule is put in its place or added at the end.
    savePolicy(draft => ({ ...draft, rules: policy?.status === 'invalid' ? [rule] : index === undefined ? [...draft.rules, rule] : draft.rules.map((item, i) => i === index ? rule : item) }), form.baseHash);
  }
  const effectBadge = (effect: ProjectPolicyRule['effect']) => <Badge tone={effect === 'allow' ? 'ok' : 'error'}>{effect === 'allow' ? (ko ? '허용' : 'Allow') : (ko ? '거부' : 'Deny')}</Badge>;
  const setForms = (patch: Partial<RuleDraft>) => setForm(previous => previous && { ...previous, rule: { ...previous.rule, ...patch } });
  const date = (value: string) => Number.isFinite(Date.parse(value)) ? new Date(value).toLocaleDateString(locale, { year: 'numeric', month: 'numeric', day: 'numeric' }) : '';

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
      {form && <PolicyRuleForm ko={ko} form={form} pending={pending} busy={busy} error={formError} onChange={setForms} onSubmit={submitForm}
        onCancel={() => { setForm(undefined); setFormError(''); }} onReload={() => openForm(form.index !== undefined && form.index < rules.length ? form.index : undefined)} />}
      <DataTable columns="minmax(0, 1fr) auto 56px" label={ko ? '프로젝트 정책' : 'Project policy'} testId="policy-rules" empty={!rules.length} emptyRows={1}
        head={[ko ? '도구' : 'Tool', ko ? '동작' : 'Effect', '']}>
        {pagedRules.slice.map((rule, offset) => {
          const index = pagedRules.page * 20 + offset;
          const scope = [...(rule.paths ?? []), ...(rule.commands ?? [])].join(', ');
          return <Row key={index} testId={`policy-rule-${index}`} selected={form?.index === index}>
            <Cell title={scope || rule.tool}>{rule.tool === '*' ? <strong><span className="ui-mono">*</span> {ko ? '모든 도구' : 'All tools'}</strong> : <strong className="ui-mono">{rule.tool}</strong>}{scope && <span className="ui-sub ui-mono">{scope}</span>}</Cell>
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

/** One rule of the project policy: tool, allow/deny, and the paths and commands it is limited to (one per line). */
function PolicyRuleForm({ ko, form, pending, busy, error, onChange, onSubmit, onCancel, onReload }: {
  ko: boolean; form: RuleForm; pending: boolean; busy: boolean; error: string;
  onChange: (patch: Partial<RuleDraft>) => void; onSubmit: () => void; onCancel: () => void; onReload: () => void;
}) {
  const { rule } = form;
  return <InPageDialog title={form.index === undefined ? (ko ? '새 규칙' : 'New rule') : (ko ? '규칙 편집' : 'Edit rule')} testId="policy-editor" onClose={onCancel} pending={pending}>
    <form noValidate className="ui-form policy-form" onSubmit={event => { event.preventDefault(); onSubmit(); }}><fieldset disabled={busy} className="policy-fieldset">
      <div className="ui-field-row">
        <Field label={ko ? '도구' : 'Tool'}>{id => <input id={id} data-testid="policy-rule-tool" value={rule.tool} maxLength={100} spellCheck={false} className="ui-mono" onChange={event => onChange({ tool: event.target.value })} />}</Field>
        <Field label={ko ? '동작' : 'Effect'}>{() => <Segmented<RuleDraft['effect']> label={ko ? '동작' : 'Effect'} testId="policy-rule-effect" value={rule.effect} onChange={effect => onChange({ effect })}
          options={[{ id: 'allow', label: ko ? '허용' : 'Allow', testId: 'policy-rule-allow' }, { id: 'deny', label: ko ? '거부' : 'Deny', testId: 'policy-rule-deny' }]} />}</Field>
      </div>
      <Field label={ko ? '경로' : 'Paths'}>{id => <textarea id={id} data-testid="policy-rule-paths" rows={3} spellCheck={false} className="ui-mono" value={rule.paths} onChange={event => onChange({ paths: event.target.value })} />}</Field>
      <Field label={ko ? '명령' : 'Commands'}>{id => <textarea id={id} data-testid="policy-rule-commands" rows={3} spellCheck={false} className="ui-mono" value={rule.commands} onChange={event => onChange({ commands: event.target.value })} />}</Field>
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
