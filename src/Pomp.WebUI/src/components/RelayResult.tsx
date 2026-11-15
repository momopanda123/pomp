import { memo } from 'react';
import { useT, type MessageKey } from '../i18n';
import type { RelayResultItem } from '../state';
import { useRoleTitle, useTeamTitle } from '../teamsCache';
import { Markdown } from './Markdown';
import { OpenRelayTab, TargetBadge } from './Messages';
import { plainControls, revealControls } from './RevealControls';
import { Badge, type BadgeTone } from './ui';

const TONES: Record<RelayResultItem['status'], BadgeTone> = { done: 'ok', failed: 'error', stopped: 'neutral' };

/** What came back from the agent or team a message was sent to: its outcome and final answer, once (the work itself stays in that tab). */
export const RelayResultCard = memo(function RelayResultCard({ item }: { item: RelayResultItem }) {
  const t = useT();
  const teamTitle = useTeamTitle(item.target.id);
  const roleTitle = useRoleTitle(item.target.id);
  const name = item.target.kind === 'team' ? teamTitle : roleTitle;
  const text = item.text?.trim();
  return <div className={`relay-result ${item.status}`} role="group" aria-label={plainControls(t('relay.resultTitle', { name }))} data-testid="relay-result" data-relay-status={item.status}>
    <div className="relay-result-head">
      <TargetBadge target={item.target} />
      <Badge tone={TONES[item.status]} testId="relay-result-status">{t(`relay.status.${item.status}` as MessageKey)}</Badge>
      <OpenRelayTab target={item.target} />
    </div>
    {text ? <div className="relay-result-text"><Markdown text={text} /></div>
      : item.status === 'done' && <div className="relay-result-note">{t('relay.noText')}</div>}
    {item.truncated && <div className="relay-result-note" data-testid="relay-result-truncated">{t('relay.truncated')}</div>}
    {item.error && <div className="relay-result-error" role="alert">{revealControls(item.error)}</div>}
  </div>;
});
