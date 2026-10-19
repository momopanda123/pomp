import { useWorkScope, useWorkSnapshot, type WorkState } from './work/store';
import type { SessionInfoPayload } from './protocol';

export interface WorkAttention {
  review: number; plan: number; codeReview: number; workflow: number; terminal: number;
  background: number; hooks: number; history: number;
}

/**
 * Counts attention/activity, not success evidence. Review = proposal files with hunks,
 * excluding done/discarded proposals and files observed applied, plus waiting or blocked isolated results. Plan = non-completed,
 * non-cancelled/non-skipped/non-abandoned tasks only while streaming or async work is pending.
 * Problems = current agent findings and compiler errors/warnings. Workflow = one awaitingReview workflow.
 * Terminal = queued/running jobs plus one connected starting/running/closing PTY.
 * Background = live registered jobs whose status is running/queued/pending/inProgress.
 * Hooks = failed/error/pending/queued/awaitingApproval, 'awaiting approval',
 * 'blocked or failed', or error-bearing history entries, with at least one while a hook runs.
 * History = zero. These badges never issue host commands and keep no browser storage.
 */
export function getWorkAttention(state: WorkState, session: SessionInfoPayload | null): WorkAttention {
  const unfinished = session?.planOpenCount ?? 0;
  const hookEntries = state.hooks?.history.filter(entry => !!entry.error || ['failed', 'error', 'pending', 'queued', 'awaitingApproval', 'awaiting approval', 'blocked or failed'].includes(entry.status)).length ?? 0;
  return {
    review: openReviewFiles(state).length + state.isolation.filter(item => item.status === 'pending' || item.status === 'blocked').length,
    plan: session?.isStreaming || session?.hasPendingAsyncWork ? unfinished : 0,
    codeReview: state.problems.items.length,
    workflow: state.workflow?.id && state.workflow.phase === 'awaitingReview' ? 1 : 0,
    terminal: state.terminal.jobs.filter(job => ['queued', 'running'].includes(job.state)).length + (state.interactive.enabled === true && state.interactive.supported === true && state.interactive.connected && ['starting', 'running', 'closing'].includes(state.interactive.state) ? 1 : 0),
    background: state.background?.state === 'live' ? state.background.jobs.filter(job => ['running', 'queued', 'pending', 'inProgress'].includes(job.status)).length : 0,
    hooks: Math.max(hookEntries, state.hooks?.running ? 1 : 0),
    history: 0,
  };
}

function openReviewFiles(state: WorkState) {
  const review = state.review;
  if (!review?.id || ['done', 'discarded', 'cancelled'].includes(review.status)) return [];
  const finishedFiles = new Set(review.outcomes.filter(outcome => outcome.state === 'applied').map(outcome => outcome.path));
  return review.files.filter(file => file.hunks.length > 0 && !finishedFiles.has(file.path));
}

/**
 * What the Work tab header's red count is made of (docs/screen-guide.md "탭 막대"): a key per item that waits for the user. Proposal files
 * and isolated results as Review counts them, the agent findings and compiler errors of Code check (compiler warnings are not news),
 * and a workflow awaiting review of its proposal. Plan progress is not an item (Chat's plan chip shows it).
 */
export function workBadgeKeys(state: WorkState): string[] {
  const review = state.review?.id ?? '';
  return [
    ...openReviewFiles(state).map(file => `review:${review}:${file.path}`),
    ...state.isolation.filter(item => item.status === 'pending' || item.status === 'blocked').map(item => `isolation:${item.id}`),
    ...state.problems.items.filter(item => item.source === 'agent' || item.severity === 'error').map(item => `problem:${item.id}`),
    ...(state.workflow?.id && state.workflow.phase === 'awaitingReview' ? [`workflow:${state.workflow.id}:${state.workflow.proposalId}`] : []),
  ];
}

/** Session identity scopes the shared panel store; switching workspace/session clears stale badges and panel data. */
export function useWorkAttention(session: SessionInfoPayload | null): WorkAttention {
  useWorkScope(session?.workingDirectory ?? '', session?.sessionId);
  return getWorkAttention(useWorkSnapshot(), session);
}
