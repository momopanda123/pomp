import { useEffect, useRef, useState } from 'react';
import { useT } from '../i18n';
import { onMessage, send, type ComposerAutoState, type ComposerWorkflowState } from '../protocol';
import { formatCount, newId } from '../format';
import { parseTokenBudget } from '../tokenBudget';
import { revealControls } from './RevealControls';
import type { WorkLayout } from './WorkPanel';
import { ViewSection } from './ViewSection';
import { Badge, Field, Form, Switch } from './ui';
import { useRefreshOnShow } from '../useRefreshOnShow';
export function ComposerAutoContinuePanel({ workflow, connected, locale, inert = false }: { workflow?: ComposerWorkflowState; connected: boolean; locale: string; layout?: WorkLayout; inert?: boolean }) {
  const [state, setState] = useState<ComposerAutoState>(); const [pending, setPending] = useState(false); const [error, setError] = useState('');
  // The optional token budget typed while the continuation is off; empty = none.
  const [budget, setBudget] = useState('');
  const request = useRef<string | undefined>(undefined); const ko = locale.startsWith('ko'); const t = useT();
  useEffect(() => {
    setState(undefined); setPending(false); request.current = undefined; setError('');
    const off = onMessage(message => {
      if (message.type !== 'workspace/composerAutoState') return;
      const next = message.payload;
      if (!workflow || next.root !== workflow.root || next.sessionId !== workflow.sessionId || next.id !== workflow.id || !Number.isInteger(next.remaining) || next.remaining < 0 || next.remaining > 6) return;
      setState(next);
      if (next.requestId === request.current) { request.current = undefined; setPending(false); }
    });
    return off;
  }, [workflow?.root, workflow?.sessionId, workflow?.id, connected]);
  useEffect(() => {
    if (!pending) return;
    const timer = setTimeout(() => { request.current = undefined; setPending(false); setError(ko ? '설정 결과를 확인하지 못했어요.' : 'Control outcome unconfirmed.'); }, 10000);
    return () => clearTimeout(timer);
  }, [pending, ko]);
  function act(action: 'status' | 'enable' | 'disable') {
    if (!connected || inert || !workflow || request.current) return;
    // Enabling carries the budget only when one is given; a wrong one is shown here and nothing is sent.
    const tokenBudget = action === 'enable' ? parseTokenBudget(budget) : null;
    if (tokenBudget === 'invalid') { setError(ko ? '토큰 예산은 10,000에서 50,000,000 사이의 정수여야 해요.' : 'The token budget must be a whole number from 10,000 to 50,000,000.'); return; }
    const requestId = newId(); request.current = requestId; setPending(true); setError('');
    if (!send('workspace/composerAuto', { action, requestId, root: workflow.root, sessionId: workflow.sessionId, id: workflow.id, revision: workflow.revision, ...(tokenBudget === null ? {} : { tokenBudget }) })) {
      request.current = undefined; setPending(false); setError(t('error.hostUnavailable'));
    }
  }
  useRefreshOnShow(connected && !inert && !!workflow?.id, () => act('status'));
  const active = !!workflow?.id && !['empty','done','cancelled'].includes(workflow.phase);
  const enabled = state?.enabled === true;
  const used = state?.tokensUsed ?? 0; const limit = state?.tokenBudget;
  return <ViewSection sectionKey="work-auto-continue" className="composer-auto" headingLevel="h2" inert={inert || undefined}
    title={ko ? '자동 계속' : 'Automatic continuation'} status={enabled ? <Badge tone="info">{ko ? `남은 실행 ${state!.remaining}회` : `${state!.remaining} runs left`}</Badge> : undefined}
    actions={<Switch label={ko ? '사용' : 'Use'} checked={enabled} testId="auto-switch"
      disabled={!connected || pending || inert || !state || (state.dispatching === true && !enabled) || !active || workflow?.paused === true} onChange={() => act(enabled ? 'disable' : 'enable')} />}>
    <Form>
      <Field label={ko ? '토큰 예산' : 'Token budget'} error={parseTokenBudget(budget) === 'invalid' ? (ko ? '10,000에서 50,000,000 사이의 정수' : 'A whole number from 10,000 to 50,000,000') : undefined}>{id =>
        <input id={id} type="text" inputMode="numeric" autoComplete="off" spellCheck={false} data-testid="auto-budget" value={enabled && limit !== undefined ? formatCount(limit) : budget}
          disabled={!connected || pending || inert || enabled} aria-invalid={parseTokenBudget(budget) === 'invalid' || undefined} onChange={event => { setBudget(event.target.value); setError(''); }} />}</Field>
      {enabled && limit !== undefined && <div className="ui-usage-row"><span className="ui-muted">{ko ? '사용' : 'Used'}</span>
        <div className="ui-progress"><i style={{ width: `${Math.min(100, Math.round(used / Math.max(1, limit) * 100))}%` }} /></div>
        <span className="ui-muted">{formatCount(used)}</span></div>}
      {state?.message && <span className="ui-muted" role="status">{revealControls(state.message)}</span>}
      {error && <span role="alert" className="ui-err">{error}</span>}
    </Form>
  </ViewSection>;
}
