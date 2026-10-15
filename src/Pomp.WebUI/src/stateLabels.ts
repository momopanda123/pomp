import { toolsTranslate, type ToolsStringKey } from './i18n/toolsStrings';

const labels: Record<string, ToolsStringKey> = {
  notified: 'state.notified', skipped: 'state.unchanged', suppressed: 'state.blocked', refused: 'state.denied', forwarded: 'state.notified',
  completed: 'state.completed', done: 'state.completed', inprogress: 'state.inProgress', in_progress: 'state.inProgress', running: 'state.inProgress', pending: 'state.pending', blocked: 'state.blocked', abandoned: 'state.abandoned',
  error: 'state.error', warning: 'state.warning', suggestion: 'state.suggestion', ide: 'state.ide', agent: 'state.agent', syntax: 'state.syntax',
  applied: 'state.applied', partial: 'state.partial', failed: 'state.failed', prepared: 'state.prepared', unreadable: 'state.unreadable',
  save: 'state.save', onfilesave: 'state.save', build: 'state.build', onbuild: 'state.build', buildstart: 'state.buildStart', builddone: 'state.buildDone',
  ready: 'state.ready', building: 'state.building', disabled: 'state.disabled', cancelled: 'state.cancelled', queued: 'state.queued', exited: 'state.exited',
  'awaiting approval': 'state.awaitingApproval', denied: 'state.denied', 'terminal job active': 'state.terminalActive', 'result unavailable': 'state.unavailable',
  'cancellation requested': 'state.cancellationRequested', 'blocked or failed': 'state.blockedOrFailed', timedout: 'state.timedOut', terminationunconfirmed: 'state.terminationUnconfirmed',
  unchanged: 'state.unchanged', rolledback: 'state.rolledBack', conflict: 'state.conflict', empty: 'state.empty', reviewing: 'state.reviewing', validating: 'state.validating', approving: 'state.approving', applying: 'state.applying',
  planning: 'state.planning', discovery: 'state.discovery', editing: 'state.editing', awaitingreview: 'state.awaitingReview', verification: 'state.verification',
};
/** Internal state codes never become user-facing labels. */
export function stateLabel(value: string, locale: string, domain?: 'workflow' | 'terminal' | 'outcome'): string {
  const normalized = value.toLowerCase().trim();
  const code = domain === 'outcome' && !/^(skipped|refused)\s*:/.test(normalized) ? normalized : normalized.split(':')[0].trim();
  const key = domain === 'terminal' && code === 'running' ? 'state.running' : domain === 'terminal' && ['done', 'completed', 'exited'].includes(code) ? 'state.exited' : domain === 'workflow' && code === 'done' ? 'state.workflowDone' : Object.hasOwn(labels, code) ? labels[code] : undefined;
  return key ? toolsTranslate(locale)(key) : domain === 'outcome' ? value : toolsTranslate(locale)('state.unknown');
}
