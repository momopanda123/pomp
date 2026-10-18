import { memo, useCallback, useLayoutEffect, useRef, useState, type ReactNode } from 'react';
import { useT } from '../i18n';
import type { TranscriptItem, TurnErrorItem } from '../state';
import { Icon } from './Icon';
import {
  AbortedNote,
  AssistantMessage,
  Notice,
  SessionResetNote,
  TurnErrorBanner,
  UserMessage,
} from './Messages';
import { RequestRecord } from './Dialogs';
import { ToolCard } from './ToolCard';
import { CaseCards, caseProposalId } from './CaseCards';
import { AdvisorNotesCard } from './AdvisorNotes';

interface ItemViewProps {
  root?: string; sessionId?: string; controlsEnabled?: boolean;
  item: TranscriptItem;
  canRetry: boolean;
  onRetry: (item: TurnErrorItem) => void;
}

const ItemView = memo(function ItemView({ item, canRetry, onRetry, root, sessionId, controlsEnabled }: ItemViewProps) {
  switch (item.kind) {
    case 'user':
      return <UserMessage item={item} />;
    case 'assistant':
      return <AssistantMessage item={item} canStage={canRetry} root={root} sessionId={sessionId} />;
    case 'tool': {
      // Cases the assistant presented appear as cards in its reply, in place of the plain tool row.
      const proposalId = item.toolName === 'present_case_feedback' && item.state === 'done' && !item.isError ? caseProposalId(item.resultText) : null;
      return proposalId ? <CaseCards proposalId={proposalId} root={root} sessionId={sessionId} /> : <ToolCard item={item} sessionId={sessionId} controlsEnabled={controlsEnabled} />;
    }
    case 'notice':
      return <Notice item={item} />;
    case 'request':
      return <RequestRecord item={item} />;
    case 'aborted':
      return <AbortedNote item={item} />;
    case 'sessionReset':
      return <SessionResetNote item={item} />;
    case 'advisor':
      return <AdvisorNotesCard item={item} />;
    case 'turnError':
      return <TurnErrorBanner item={item} canRetry={canRetry} onRetry={onRetry} />;
  }
});

function EmptyState() { return null; }

function WorkingIndicator() {
  const t = useT();
  return (
    <div className="working" role="status">
      <span className="dots" aria-hidden="true">
        <i />
        <i />
        <i />
      </span>
      {t('msg.working')}
    </div>
  );
}

/** Distance from the bottom (px) still counted as "at the bottom". */
const BOTTOM_SLACK = 24;

interface TranscriptProps {
  root?: string; sessionId?: string; controlsEnabled?: boolean;
  items: TranscriptItem[];
  /** A turn is in progress (or about to start). */
  busy: boolean;
  replaying: boolean;
  replayRevision: number;
  replayTruncated: boolean;
  /** The composer could take a message right now (connected, idle, no new session pending). */
  canRetry: boolean;
  /** Resends the message the given failed turn belonged to. */
  onRetry: (item: TurnErrorItem) => void;
  /** The pending permission / dialog card, shown after the conversation (the oldest request only). */
  request?: ReactNode;
}

/** Scrollable message list. Follows the newest content until the user scrolls up; scrolling back down resumes it. */
export function Transcript({ items, busy, replaying, replayRevision, replayTruncated, canRetry, onRetry, request, root, sessionId, controlsEnabled }: TranscriptProps) {
  const t = useT();
  const scrollRef = useRef<HTMLDivElement>(null);
  const contentRef = useRef<HTMLDivElement>(null);
  const follow = useRef(true);
  const lastTop = useRef(0);
  const [away, setAway] = useState(false);
  const replayRef = useRef(replaying);
  replayRef.current = replaying;

  const scrollToBottom = useCallback(() => {
    const el = scrollRef.current;
    if (el) el.scrollTop = el.scrollHeight;
  }, []);

  const onScroll = useCallback(() => {
    const el = scrollRef.current;
    if (!el || replayRef.current) return;
    const atBottom = el.scrollHeight - el.scrollTop - el.clientHeight < BOTTOM_SLACK;
    if (atBottom) follow.current = true;
    else if (el.scrollTop < lastTop.current - 1) follow.current = false; // the user scrolled up
    lastTop.current = el.scrollTop;
    setAway(!atBottom);
  }, []);

  const jumpToLatest = useCallback(() => {
    follow.current = true;
    scrollToBottom();
  }, [scrollToBottom]);

  // Content growth (streaming text, expanding a card) and viewport changes (composer resize) keep the tail in view.
  useLayoutEffect(() => {
    const scroller = scrollRef.current;
    const content = contentRef.current;
    if (!scroller || !content) return;
    const observer = new ResizeObserver(() => {
      if (follow.current && !replayRef.current) scrollToBottom();
    });
    observer.observe(content);
    observer.observe(scroller);
    return () => observer.disconnect();
  }, [scrollToBottom]);

  // Sending a message always jumps to the bottom.
  const last = items[items.length - 1];
  const lastKey = last?.kind === 'user' ? last.key : undefined;
  useLayoutEffect(() => {
    if (lastKey === undefined || replaying) return;
    follow.current = true;
    scrollToBottom();
  }, [lastKey, replaying, scrollToBottom]);

  useLayoutEffect(() => {
    if (replaying || replayRevision === 0) return;
    follow.current = true;
    setAway(false);
    scrollToBottom();
    lastTop.current = scrollRef.current?.scrollTop ?? 0;
  }, [replaying, replayRevision, scrollToBottom]);

  const streaming = items.some((it) => (it.kind === 'assistant' && !it.done) || (it.kind === 'tool' && it.state === 'running'));

  return (
    <div className={replaying ? 'transcript-wrap replaying' : 'transcript-wrap'}>
      <div className="transcript" ref={scrollRef} onScroll={onScroll} aria-busy={replaying}>
        <div className="transcript-content" ref={contentRef}>
          {replayTruncated && <div className="notice warning" role="status" data-testid="replay-truncated"><Icon name="warning" size={13} /><span>{t('msg.replayTruncated')}</span></div>}
          {items.length === 0 && !busy && !replayTruncated && !request ? <EmptyState /> : null}
          {items.map((item) => (
            <ItemView key={item.key} item={item} canRetry={canRetry} onRetry={onRetry} root={root} sessionId={sessionId} controlsEnabled={controlsEnabled} />
          ))}
          {request}
          {busy && !streaming && !replaying && !request && <WorkingIndicator />}
        </div>
      </div>
      {away && !replaying && (
        <button type="button" className="jump" data-testid="jump-latest" title={t('scroll.bottom')} aria-label={t('scroll.bottom')} onClick={jumpToLatest}>
          <Icon name="arrowDown" size={14} />
        </button>
      )}
    </div>
  );
}
