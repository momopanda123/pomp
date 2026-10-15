import { memo } from 'react';
import { useT, type Translate } from '../i18n';
import type { AdvisorNote } from '../protocol';
import type { AdvisorNotesItem } from '../state';
import { Icon } from './Icon';
import { Markdown } from './Markdown';
import { revealControls } from './RevealControls';
import { Badge, type BadgeTone } from './ui';

const SEVERITIES: Record<string, BadgeTone> = { nit: 'neutral', concern: 'warn', blocker: 'error' };

/** The engine's weight of a note ("nit", "concern", "blocker") as a badge; an unknown word is shown as it is. */
export function SeverityBadge({ severity, t }: { severity?: string; t: Translate }) {
  if (!severity) return null;
  const known = Object.hasOwn(SEVERITIES, severity);
  return <Badge tone={known ? SEVERITIES[severity] : 'neutral'}>{known ? t(`advisors.severity.${severity}` as Parameters<Translate>[0]) : revealControls(severity)}</Badge>;
}

/** The notes advisors gave the chat agent, as one card where they came in the conversation. */
export const AdvisorNotesCard = memo(function AdvisorNotesCard({ item }: { item: AdvisorNotesItem }) {
  const t = useT();
  return <div className="advisor-notes" role="group" aria-label={t('advisors.notesTitle')} data-testid="advisor-notes">
    <div className="advisor-notes-head"><Icon name="users" size={13} />{t('advisors.notesTitle')}</div>
    {item.notes.map((note: AdvisorNote, index) => <div key={index} className="advisor-note">
      <div className="advisor-note-meta"><strong>{revealControls(note.advisor)}</strong><SeverityBadge severity={note.severity} t={t} /></div>
      <div className="advisor-note-text"><Markdown text={note.note} /></div>
    </div>)}
  </div>;
});
