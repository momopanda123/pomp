import { useMemo } from 'react';
import { formatDuration } from '../format';
import { useNow } from '../hooks';
import { useT, type MessageKey } from '../i18n';
import type { BackgroundAgentJob, BackgroundAgentState } from '../protocol';
import { useRoleTitle, useTeamTitle } from '../teamsCache';
import { useWorkState } from '../work/store';
import { BackgroundRunControls, isRunningJob } from './BackgroundRunControls';
import { plainControls, revealControls } from './RevealControls';
import { ViewSection } from './ViewSection';
import { Actions, Badge, Cell, DataTable, Pager, Row, usePaged, type BadgeTone } from './ui';

const STATUS_TONES: Record<string, BadgeTone> = { pending: 'neutral', running: 'info', completed: 'ok', failed: 'error', error: 'error', aborted: 'warn', unknown: 'neutral' };

export type ObservedAgents = { state: BackgroundAgentState | undefined; jobs: BackgroundAgentJob[]; running: number };
/**
 * The subagents POMP observes in this conversation (`workspace/backgroundState`): running ones first in the engine's order, then ended ones,
 * the latest first. A live snapshot of another conversation is not shown; an archived one is (its message says so).
 */
export function useObservedAgents(sessionId: string | undefined): ObservedAgents {
  const state = useWorkState('background');
  return useMemo(() => {
    const all = state && (state.state !== 'live' || state.sessionId === sessionId) ? state.jobs : [];
    const running = all.filter(isRunningJob);
    for (const job of running) noteStart(job);
    const ended = all.filter(job => !isRunningJob(job)).sort((a, b) => (b.lastUpdate ?? 0) - (a.lastUpdate ?? 0));
    return { state, jobs: [...running, ...ended], running: running.length };
  }, [state, sessionId]);
}

export function RunningCard({ observed: { state, jobs, running }, sessionId, connected, inert }: { observed: ObservedAgents; sessionId?: string; connected: boolean; inert: boolean }) {
  const t = useT();
  const paged = usePaged(jobs, sessionId);
  return <ViewSection sectionKey="agents-running" className="agents-card" headingLevel="h2" title={t('running.title')} status={<span data-testid="running-count">{running}</span>} active={running > 0} bodyClassName="ui-flush">
    {state?.message && <p className="ui-note ui-muted" role="status" data-testid="running-message">{revealControls(state.message)}</p>}
    {state?.controlMessage && <p className="ui-note ui-muted" role="status" data-testid="running-control-message">{revealControls(state.controlMessage)}</p>}
    <DataTable columns="minmax(0, 1fr) auto auto auto" label={t('running.title')} testId="running-table" empty={!jobs.length} emptyRows={2}
      head={[t('running.agent'), t('running.status'), t('running.time'), '']}>
      {paged.slice.map(job => <RunningRow key={job.id} job={job} state={state} sessionId={sessionId} disabled={!connected || inert} />)}
    </DataTable>
    {!jobs.length && <p className="ui-note ui-muted" data-testid="running-empty">{t('running.empty')}</p>}
    <Pager paged={paged} testId="running-pager" />
  </ViewSection>;
}

/**
 * When each job started, as estimated from its running updates (last update minus the duration reported with it). The engine's duration
 * stays at its last progress report, so an ended job's own figure can be shorter than the time it ran; the estimate keeps the two views consistent.
 */
const startedAt = new Map<string, number>();
function noteStart(job: BackgroundAgentJob): void {
  const reported = job.progress?.durationMs; const update = job.lastUpdate;
  if (reported == null || update == null) return;
  const start = update - reported; startedAt.set(job.id, Math.min(startedAt.get(job.id) ?? start, start));
}
function elapsedMs(job: BackgroundAgentJob, live: boolean, now: number): number | undefined {
  const reported = job.progress?.durationMs; const update = job.lastUpdate;
  if (reported == null) return undefined;
  if (update == null) return reported;
  if (live) { noteStart(job); return reported + Math.max(0, now - update); }
  const start = startedAt.get(job.id);
  return start != null ? Math.max(reported, update - start) : reported;
}

function RunningRow({ job, state, sessionId, disabled }: { job: BackgroundAgentJob; state: BackgroundAgentState | undefined; sessionId?: string; disabled: boolean }) {
  const t = useT(); const title = useRoleTitle(job.agent); const team = useTeamTitle(job.team ?? '');
  const code = job.status === 'cancelled' ? 'aborted' : job.status === 'queued' ? 'pending' : job.status === 'inProgress' ? 'running' : job.status;
  const status = ['pending', 'running', 'completed', 'failed', 'error', 'aborted'].includes(code) ? code : 'unknown';
  const live = isRunningJob(job);
  // A running job keeps counting from its last update until the next one arrives.
  const now = useNow(live, 1000);
  const durationMs = elapsedMs(job, live, now);
  const duration = durationMs != null ? formatDuration(durationMs) : '';
  const ended = !live && job.lastUpdate ? new Date(job.lastUpdate) : undefined;
  const endedLabel = ended ? t('running.endedAt', { time: ended.toLocaleTimeString(undefined, { hour: '2-digit', minute: '2-digit', hourCycle: 'h23' }) }) : '';
  return <Row testId={`running-row-${job.id}`}>
    <Cell className="agents-name-cell">
      <span className="agents-name-line"><strong>{revealControls(title)}</strong><span className="ui-mono ui-muted agents-id">{revealControls(job.id)}</span></span>
      {job.description && <span className="ui-sub" title={plainControls(job.description)}>{revealControls(job.description)}</span>}
      {job.team && <span className="agents-badges"><Badge tone="accent" title={t('running.team')} testId={`running-team-${job.id}`}>{revealControls(team)}</Badge></span>}
    </Cell>
    <Cell><Badge tone={STATUS_TONES[status]} testId={`running-status-${job.id}`}>{t(`agents.status.${status}` as MessageKey)}</Badge></Cell>
    <Cell className="ui-muted" title={ended?.toLocaleString()}>{[duration, endedLabel].filter(Boolean).join(' · ')}</Cell>
    <Actions><BackgroundRunControls state={state} job={job} sessionId={sessionId} disabled={disabled} /></Actions>
  </Row>;
}
