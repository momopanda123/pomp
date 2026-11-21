import { useLayoutEffect, useRef, useState, type ReactNode } from 'react';
import type { ConversationPlan } from '../../conversationProtocol';
import { newId } from '../../format';
import { useT } from '../../i18n';
import type { SendResult } from '../../limits';
import { onMessage, send } from '../../protocol';
import { AgentPlan } from '../AgentPlan';

interface ConversationPlansProps {
  root: string;
  plans: ConversationPlan[];
  activeSessionId?: string;
  locale: string;
  inert?: boolean;
  disabled?: boolean;
}

type PlanAction = 'continue' | 'run' | 'stop';
const REPLY_TIMEOUT_MS = 30000;
const working = (plan: ConversationPlan) => plan.isStreaming || plan.hasPendingAsyncWork;
const scopeKey = (root: string, sessionId: string) => JSON.stringify([root, sessionId]);

/** Plan selection is workspace-local, not navigation to the conversation that owns the plan. */
export function ConversationPlans(props: ConversationPlansProps) {
  return <WorkspacePlans key={props.root} {...props} />;
}

function WorkspacePlans({ root, plans, activeSessionId, locale, inert = false, disabled = false }: ConversationPlansProps) {
  const t = useT();
  const fallback = plans.find(plan => plan.sessionId === activeSessionId) ?? plans[0];
  const [selectedId, setSelectedId] = useState<string>();
  const explicit = plans.find(plan => plan.sessionId === selectedId);
  const selected = explicit ?? fallback;
  // Follow the active chat until the user chooses a plan; forget a closed choice instead of reviving it later.
  if (selectedId !== undefined && !explicit) setSelectedId(undefined);
  const label = (plan: ConversationPlan) => {
    const partner = plan.partner.kind === 'main' ? t('work.plan.partner.main')
      : `${t(plan.partner.kind === 'team' ? 'work.plan.partner.team' : 'work.plan.partner.agent')}${plan.partner.id ? `: ${plan.partner.id}` : ''}`;
    const status = t(!plan.ready ? 'work.plan.offline' : working(plan) ? 'work.plan.running' : 'work.plan.idle');
    return `${plan.title ? `${plan.title} — ` : ''}${partner} · ${plan.planTaskCount - plan.planOpenCount}/${plan.planTaskCount} · ${status}`;
  };
  return <div className="ui-gap" data-testid="conversation-plans" inert={inert || undefined}>
    {/* No open conversation: the plan card keeps its header and blank rows, with nothing to act on. */}
    {!selected ? <AgentPlan root={root} session={null} locale={locale} layout="page" busy={false} disabled inert={inert}
      onContinue={() => {}} onRunTask={() => ({ ok: false, reason: 'busy' })} onStop={() => {}} /> :
      <SelectedPlan key={scopeKey(root, selected.sessionId)} root={root} plan={selected} locale={locale} inert={inert} disabled={disabled}
        selector={<div className="ui-field plan-conversation-field">
          <select aria-label={t('work.plan.conversation')} data-testid="plan-conversation" value={selected.sessionId} disabled={inert}
            title={label(selected)} onChange={event => setSelectedId(event.target.value)}>
            {plans.map(plan => <option key={plan.sessionId} value={plan.sessionId}>{label(plan)}</option>)}
          </select></div>} />}
  </div>;
}

/** Unmounting a selection retires its requests before old AgentPlan edit replies can invoke a run callback. */
function SelectedPlan({ root, plan, locale, inert, disabled, selector }: {
  root: string; plan: ConversationPlan; locale: string; inert: boolean; disabled: boolean; selector: ReactNode;
}) {
  const t = useT();
  const [pending, setPending] = useState<PlanAction | null>(null);
  const [error, setError] = useState('');
  const request = useRef<{ id: string; action: PlanAction; timer?: number } | null>(null);
  const mounted = useRef(false);
  const latest = useRef({ plan, inert, disabled, t });
  latest.current = { plan, inert, disabled, t };

  useLayoutEffect(() => {
    mounted.current = true;
    const release = onMessage(message => {
      if (message.type !== 'workspace/agentPlanResult' || !request.current || message.payload.requestId !== request.current.id) return;
      window.clearTimeout(request.current.timer);
      request.current = null;
      setPending(null);
      setError(message.payload.ok ? '' : message.payload.error || latest.current.t('work.plan.actionFailed'));
    });
    return () => {
      mounted.current = false;
      release();
      window.clearTimeout(request.current?.timer);
      request.current = null;
    };
  }, []);

  const act = (action: PlanAction, task?: string, marked?: boolean): SendResult => {
    const current = latest.current;
    if (!mounted.current || current.inert || current.disabled || (request.current && (action !== 'stop' || request.current.action === 'stop'))) return { ok: false, reason: 'busy' };
    if (!root || !current.plan.ready) {
      setError(current.t('error.hostUnavailable'));
      return { ok: false, reason: 'hostUnavailable' };
    }
    // Stop supersedes a waiting action; its late reply must not settle this request or restore its error.
    window.clearTimeout(request.current?.timer);
    const pendingRequest = { id: `plan-action:${newId()}`, action, timer: undefined as number | undefined };
    request.current = pendingRequest;
    setError('');
    if (!send('workspace/agentPlanAction', {
      requestId: pendingRequest.id, root, sessionId: plan.sessionId, action,
      ...(action === 'run' ? { task, marked } : {}),
    })) {
      request.current = null;
      setPending(null);
      setError(current.t('error.hostUnavailable'));
      return { ok: false, reason: 'hostUnavailable' };
    }
    // A synchronous host reply may already have settled the request.
    if (request.current === pendingRequest) {
      setPending(action);
      pendingRequest.timer = window.setTimeout(() => {
        if (request.current !== pendingRequest) return;
        request.current = null;
        setPending(null);
        setError(latest.current.t('work.plan.actionUnconfirmed'));
      }, REPLY_TIMEOUT_MS);
    }
    return { ok: true };
  };

  return <AgentPlan key={scopeKey(root, plan.sessionId)} root={root} session={plan} sessionId={plan.ready ? plan.sessionId : undefined} locale={locale} layout="page"
    busy={working(plan)} disabled={disabled || !root || !plan.ready} inert={inert}
    selector={selector} actionPending={pending !== null} stopPending={pending === 'stop'}
    notices={<>
      {!plan.ready && <div className="ui-note" role="status">{t('work.plan.conversationOffline')}</div>}
      {pending && <div className="ui-note" role="status" data-testid="plan-action-pending">{t('work.plan.actionPending')}</div>}
      {error && <div className="ui-note" role="alert" data-testid="plan-action-error">{error}</div>}
    </>}
    onContinue={() => { act('continue'); }} onRunTask={(task, marked) => act('run', task, marked)} onStop={() => { act('stop'); }} />;
}
