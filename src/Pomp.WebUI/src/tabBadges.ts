import { useEffect, useLayoutEffect, useMemo, useRef, useState } from 'react';
import type { ShellTab } from './components/TabBar';
import type { TranscriptItem } from './state';
import { useWorkSnapshot } from './work/store';
import { workBadgeKeys } from './workAttention';

/** Most Work items remembered as seen; the oldest are forgotten first. */
const SEEN_LIMIT = 2000;

export interface TabBadges { chat: number; work: number }

/** A reply in the transcript: an assistant message with text, or a turn that failed. */
const isReply = (item: TranscriptItem) => (item.kind === 'assistant' && item.done && item.text.trim() !== '') || item.kind === 'turnError';

/** Where the transcript stood when Chat left the screen: the last message's reply can still grow, so it is scanned again from there. */
function chatMark(sessionId: string | undefined, revision: number, items: TranscriptItem[]) {
  const start = items.findLastIndex(item => item.kind === 'user') + 1;
  return { sessionId, revision, start, length: items.length, replies: new Set(items.slice(start).filter(isReply).map(item => item.key)) };
}

/**
 * The red counts on the tab headers (docs/screen-guide.md "탭 막대"); the tab on screen counts nothing. In memory, this page only.
 * - Chat: approvals and questions waiting for an answer, plus the replies that came while another tab was on screen. A reply is counted
 *   once per message it answers (its text or its failure). A conversation change or a transcript replay is not news.
 * - Work: the items of {@link workBadgeKeys} that were never on screen with the Work tab open. An item that was is not counted again.
 */
export function useTabBadges(tab: ShellTab, chat: { sessionId?: string; items: TranscriptItem[]; replaying: boolean; replayRevision: number; requests: number }): TabBadges {
  const { sessionId, items, replaying, replayRevision, requests } = chat;
  const away = tab !== 'chat';
  const itemsRef = useRef(items); itemsRef.current = items;
  const [chatSeen, setChatSeen] = useState(() => chatMark(sessionId, replayRevision, items));
  // Taken when Chat leaves the screen, and again when the transcript is replaced meanwhile (conversation change, replay). Before paint:
  // the first frame without Chat must not count against an older mark.
  useLayoutEffect(() => { if (away) setChatSeen(chatMark(sessionId, replayRevision, itemsRef.current)); }, [away, sessionId, replayRevision, replaying]);
  let replies = 0;
  if (away && !replaying && chatSeen.sessionId === sessionId && chatSeen.revision === replayRevision && chatSeen.length <= items.length) {
    let answered = false;
    for (let index = chatSeen.start; index < items.length; index++) {
      const item = items[index]!;
      if (item.kind === 'user') answered = false;
      else if (!answered && isReply(item) && !chatSeen.replies.has(item.key)) { replies++; answered = true; }
    }
  }
  const work = useWorkSnapshot();
  const keys = useMemo(() => workBadgeKeys(work), [work]);
  const [workSeen, setWorkSeen] = useState<ReadonlySet<string>>(() => new Set());
  useEffect(() => {
    if (tab !== 'work' || keys.every(key => workSeen.has(key))) return;
    const next = new Set(workSeen);
    for (const key of keys) { next.delete(key); next.add(key); }
    for (const key of next) { if (next.size <= SEEN_LIMIT) break; next.delete(key); }
    setWorkSeen(next);
  }, [tab, keys, workSeen]);
  const unseenWork = useMemo(() => keys.filter(key => !workSeen.has(key)).length, [keys, workSeen]);

  return { chat: tab === 'chat' ? 0 : requests + replies, work: tab === 'work' ? 0 : unseenWork };
}
