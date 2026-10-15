import { useState } from 'react';
import { useT } from '../i18n';
import type { BackgroundAgentJob, BackgroundAgentState } from '../protocol';
import type { TranscriptItem } from '../state';
import { useRoleTitle } from '../teamsCache';
import { useWorkState } from '../work/store';
import { Icon } from './Icon';
import { BackgroundRunControls } from './BackgroundRunControls';
import { stateLabel } from '../stateLabels';
import { useLocale } from '../i18n';
import { revealControls } from './RevealControls';

export function currentTurnJobs(state: BackgroundAgentState | undefined, sessionId: string | undefined, promptId: string | undefined, items: TranscriptItem[]): BackgroundAgentJob[] {
  if (!promptId || !sessionId || state?.state !== 'live' || state.sessionId !== sessionId) return [];
  const start = items.findLastIndex(item => item.kind === 'user' && item.promptId === promptId);
  if (start < 0) return [];
  const next = items.findIndex((item, index) => index > start && item.kind === 'user' && item.promptId !== undefined && item.promptId !== promptId);
  const toolIds = new Set(start < 0 ? [] : items.slice(start + 1, next < 0 ? undefined : next).filter(item => item.kind === 'tool').map(item => item.toolCallId));
  return state.jobs.filter(job => (job.status === 'running' || job.status === 'pending') && (job.promptId ? job.promptId === promptId : !!job.parentToolCallId && toolIds.has(job.parentToolCallId)));
}
export function MemberProgress({ job, state, sessionId, disabled = false }: { job: BackgroundAgentJob; state?: BackgroundAgentState; sessionId?: string; disabled?: boolean }) {
  const t = useT(); const locale = useLocale(); const title = useRoleTitle(job.agent);
  return <li data-testid={`live-member-${job.id}`}><strong>{revealControls(title)}</strong><span>{stateLabel(job.status, locale)} · {job.progress?.toolCount ?? 0} {t('agents.toolsCount')}{job.progress?.durationMs != null && <> · {(job.progress.durationMs / 1000).toFixed(1)}s</>}</span>{job.progress?.recentTools?.length ? <small>{job.progress.recentTools.map(tool => revealControls(tool)).join(' · ')}</small> : null}<div className="run-row-actions"><BackgroundRunControls state={state} job={job} sessionId={sessionId} disabled={disabled || state?.state !== 'live'} /></div></li>;
}
export function LiveAgentProgress({ sessionId, promptId, items, disabled = false }: { sessionId?: string; promptId?: string; items: TranscriptItem[]; disabled?: boolean }) {
  const t = useT(); const [open, setOpen] = useState(false); const state = useWorkState('background');
  const cards = new Set(items.filter(item => item.kind === 'tool').map(item => item.toolCallId));
  const jobs = currentTurnJobs(state, sessionId, promptId, items).filter(job => !job.parentToolCallId || !cards.has(job.parentToolCallId));
  if (!jobs.length) return null;
  return <div className="live-agent-progress" data-testid="live-agent-progress"><div className="live-progress-head"><button className="conversation-strip-toggle" type="button" data-testid="live-progress-toggle" aria-expanded={open} onClick={() => setOpen(!open)}><Icon name="chevronDown" size={12} /><strong>{t('progress.title', { count: jobs.length })}</strong></button></div>{open && <ol className="live-members">{jobs.map(job => <MemberProgress key={job.id} job={job} state={state} sessionId={sessionId} disabled={disabled} />)}</ol>}</div>;
}
