import { useViewChoice } from '../../useViewChoice';
import type { SessionInfoPayload } from '../../protocol';
import type { WorkAttention } from '../../workAttention';
import { useWorkScope, useWorkSnapshot } from '../../work/store';
import { toolsTranslate } from '../../i18n/toolsStrings';
import { AgentPlan } from '../AgentPlan';
import { ProblemsPanel } from '../CodeReviewPanel';
import type { SendResult } from '../../limits';
import { ComposerWorkflowPanel } from '../ComposerWorkflowPanel';
import { InteractiveTerminalPanel } from '../InteractiveTerminalPanel';
import { IsolationResultsPanel } from '../IsolationResultsPanel';
import { WorkspaceHistory } from '../WorkspaceHistory';
import { WorkspaceReview } from '../WorkspaceReview';
import { ViewChips } from '../tools/ViewChips';
import { Segmented } from '../ui';
import { useState } from 'react';
import '../tools/tools.css';

export const workViews = ['progress', 'review', 'codeReview'] as const;
export type WorkView = typeof workViews[number];
export type RequestedWorkView = WorkView | 'plan' | 'workflow' | 'history' | 'terminal';
export interface WorkTabProps {
  locale: string; session: SessionInfoPayload | null; workspaceKey: string; sessionId?: string;
  connected: boolean; busy: boolean; newSessionPending?: boolean; inert?: boolean; attention: WorkAttention;
  requestedView?: RequestedWorkView; onViewChange?: (view: WorkView) => void;
  onContinue: (task?: string) => void; onStop: () => void; onSolve: (text: string) => SendResult;
  /** Run one plan task: `marked` when the plan already marks it in progress (idle), else it is scheduled after the current step. */
  onRunTask: (task: string, marked: boolean) => SendResult;
  onOpenWorkflows?: () => void;
}
export function normalizeWorkView(view: RequestedWorkView): WorkView { return view === 'plan' || view === 'workflow' ? 'progress' : view === 'history' ? 'review' : view === 'terminal' ? 'progress' : view; }
/** What the badge of a sub-view counts (History has none; Progress shows the workflow that waits for review). */
export function workViewAttention(view: WorkView, attention: WorkAttention): number {
  return view === 'progress' ? attention.plan + attention.workflow : attention[view];
}
export function initialWorkView(attention: WorkAttention): WorkView {
  return workViews.find(view => workViewAttention(view, attention) > 0) ?? 'review';
}
export function WorkTab({ locale, session, workspaceKey, sessionId, connected, busy, newSessionPending = false, inert = false,
  requestedView, onViewChange, onContinue, onRunTask, onStop, onSolve, onOpenWorkflows }: WorkTabProps) {
  useWorkScope(workspaceKey, sessionId);
  const snapshot = useWorkSnapshot();
  const applied = new Set(snapshot.review?.outcomes.filter(item => item.state === 'applied').map(item => item.path.toLowerCase()));
  const proposalCount = snapshot.review?.id && !['done', 'empty', 'discarded', 'cancelled'].includes(snapshot.review.status) ? snapshot.review.files.filter(file => file.hunks.length > 0 && !applied.has(file.path.toLowerCase())).length : 0;
  const isolationCount = snapshot.isolation.filter(item => item.status !== 'applied').length;
  const pendingCount = proposalCount + isolationCount;
  const counts: Record<WorkView, number> = { progress: (session?.planTaskCount ?? 0) + (snapshot.workflow?.id && !['done', 'cancelled', 'empty'].includes(snapshot.workflow.phase) ? 1 : 0) + (snapshot.interactive.enabled && snapshot.interactive.supported && snapshot.interactive.terminalId ? 1 : 0), review: pendingCount, codeReview: snapshot.problems.items.length };
  const [view, change] = useViewChoice({ views: workViews, requested: normalizeWorkView(requestedView ?? 'progress'), active: !inert, scope: workspaceKey + ':' + (sessionId ?? ''), count: item => counts[item], onChange: onViewChange });
  const t = toolsTranslate(locale); const ko = locale.startsWith('ko');
  const [changes, setChanges] = useState<'pending' | 'applied'>('pending');
  const inactive = (item: WorkView) => inert || view !== item;
  return <div className="tools-tab work-tab-new" data-testid="work-tab" inert={inert || undefined}>
    <ViewChips prefix="work" label={t('work.view.nav')} active={view} onChange={change} inert={inert}
      views={workViews.map(id => ({ id, label: t(`work.view.${id}`), badge: counts[id] }))} />
    <div className="tools-view-stack" key={`${workspaceKey}:${sessionId ?? ''}`}>
      {workViews.map(item => <section key={item} id={`work-${item}`} role="tabpanel" aria-labelledby={`work-tab-${item}`}
        className={`tools-view work-combined-view${view === item ? ' active' : ''}`} hidden={view !== item} inert={inactive(item) || undefined}>
        {item === 'review' && <div className="work-combined-body">
          <Segmented<'pending' | 'applied'> label={t('work.view.review')} testId="changes-switch" value={changes} onChange={setChanges} options={[
            { id: 'pending', label: ko ? '대기 중' : 'Pending', badge: pendingCount, testId: 'changes-pending' },
            { id: 'applied', label: ko ? '적용됨' : 'Applied', badge: snapshot.history?.total ?? snapshot.history?.entries?.length ?? 0, testId: 'changes-applied' }]} />
          <div hidden={changes !== 'pending'} className="work-combined-body">
            <WorkspaceReview locale={locale} connected={connected} root={workspaceKey} inert={inactive(item) || changes !== 'pending'} busy={busy} onShowApplied={() => setChanges('applied')} />
            <IsolationResultsPanel locale={locale} connected={connected} inert={inactive(item) || changes !== 'pending'} />
          </div>
          <div hidden={changes !== 'applied'}>
            <WorkspaceHistory layout="page" locale={locale} connected={connected} inert={inactive(item) || changes !== 'applied'} onReview={() => { setChanges('pending'); change('review'); }} />
          </div>
        </div>}
        {item === 'progress' && <div className="work-combined-body">
          <AgentPlan layout="page" session={session} sessionId={sessionId} locale={locale} busy={busy} inert={inactive(item)} disabled={!connected || newSessionPending} onStop={onStop} onContinue={onContinue} onRunTask={onRunTask} />
          {/* Workflow already contains ComposerAutoContinuePanel: do not duplicate its controls/subscription. */}
          <ComposerWorkflowPanel layout="page" locale={locale} workspaceKey={workspaceKey} sessionId={sessionId} connected={connected && !newSessionPending} inert={inactive(item) || busy} onContinue={onContinue} onOpenWorkflows={onOpenWorkflows} />
          {snapshot.interactive.enabled && snapshot.interactive.supported && <InteractiveTerminalPanel layout="page" locale={locale} connected={connected && !newSessionPending} inert={inactive(item) || !connected || newSessionPending} />}
        </div>}
        {item === 'codeReview' && <ProblemsPanel layout="page" locale={locale} connected={connected} inert={!connected || inactive(item) || newSessionPending} onSolve={onSolve} />}
      </section>)}
    </div>
  </div>;
}
