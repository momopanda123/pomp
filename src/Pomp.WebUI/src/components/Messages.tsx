import { memo, useRef, useState } from 'react';
import { parseSentCaseFeedback, sentCaseFeedbackText } from '../caseFeedbackMessage';
import { clip, copyText } from '../format';
import { useT } from '../i18n';
import { engineAbortText, isEngineAbort } from '../engineAbortText';
import type {
  AbortedItem,
  AssistantItem,
  NoticeItem,
  SessionResetItem,
  TurnErrorItem,
  UserItem,
} from '../state';
import { AttachmentSummary } from './AttachmentSummary';
import { DestinationAvatar } from './AgentsTables';
import { Icon } from './Icon';
import { Markdown } from './Markdown';
import { plainControls, revealControls } from './RevealControls';
import { UserTextBody } from './SentCaseFeedback';
import { useRoleTitle, useTeamTitle } from '../teamsCache';
import { useWorkState } from '../work/store';
import type { SendTarget } from '../protocol';
import { markMenuHandled, useContextMenu, type ContextMenuItem } from './ContextMenu';

/** Non-empty selection text fully inside `root`, or undefined (then "Copy selection" is not offered). */
function selectionIn(root: HTMLElement): string | undefined {
  const selection = window.getSelection();
  if (!selection || selection.isCollapsed) return undefined;
  const text = selection.toString();
  if (!text) return undefined;
  const range = selection.rangeCount > 0 ? selection.getRangeAt(0) : null;
  if (!range || !root.contains(range.commonAncestorContainer)) return undefined;
  return text;
}

/** Who the user's message went to: the destination's picture and name only (the agent of a running member; POMP for the main conversation). */
function TargetBadge({ target }: { target: SendTarget }) {
  const teamTitle = useTeamTitle(target.id);
  const background = useWorkState('background');
  const agent = target.kind === 'instance' ? background?.jobs.find(job => job.id === target.id)?.agent || target.id : target.id;
  const roleTitle = useRoleTitle(agent);
  const title = (target.kind === 'main' ? 'POMP' : target.kind === 'team' ? teamTitle : roleTitle) + (target.kind === 'instance' ? ` #${target.id.slice(-8)}` : '');
  const kind = target.kind === 'main' ? 'main' : target.kind === 'team' ? 'team' : 'agent';
  return <div className="message-target-badge" data-testid="message-target-badge">
    <DestinationAvatar kind={kind} id={kind === 'main' ? undefined : agent} size={14} />
    <span className="message-target-name">{revealControls(title)}</span>
  </div>;
}

function TeamBriefCard({ teamId, kind = 'team', content }: { teamId: string; kind?: 'team' | 'agent' | 'dm'; content: string }) {
  const [open, setOpen] = useState(false);
  const teamTitle = useTeamTitle(teamId);
  const background = useWorkState('background');
  const agent = kind === 'dm' ? background?.jobs.find(job => job.id === teamId)?.agent || teamId : teamId;
  const roleTitle = useRoleTitle(agent);
  const title = kind === 'team' ? teamTitle : roleTitle;

  return (
    <div className="team-brief-card" data-testid={`${kind}-brief-card`}>
      <button
        type="button"
        className="team-brief-toggle"
        data-testid={`${kind}-brief-toggle`}
        aria-expanded={open}
        onClick={() => setOpen((v) => !v)}
      >
        <Icon name="chevron" size={12} className={open ? 'chevron open' : 'chevron'} />
        <DestinationAvatar kind={kind === 'team' ? 'team' : 'agent'} id={agent} size={14} />
        <strong>{revealControls(title)}</strong>
      </button>
      {open && (
        <div className="team-brief-body" data-testid={`${kind}-brief-body`}>
          <pre>{revealControls(content)}</pre>
        </div>
      )}
    </div>
  );
}

function EditorContextChip({ context }: { context: UserItem['editorContext'] }) {
  const t = useT();
  if (!context?.path) return null;
  const path = context.path;
  const name = path.split(/[\\/]/u).pop() || path;
  const line = (value: number | undefined): value is number => Number.isSafeInteger(value) && value! > 0;
  const lines = line(context.startLine) && line(context.endLine)
    ? context.startLine === context.endLine ? t('msg.editorLine', { line: context.startLine }) : t('msg.editorSelection', { start: context.startLine, end: context.endLine })
    : line(context.caretLine) ? t('msg.editorCaret', { line: context.caretLine }) : '';
  return (
    <div className="user-editor-context">
      <span className="editor-context-chip" data-testid="editor-context-chip" role="note" title={plainControls(path)} aria-label={plainControls(t('msg.editorContext', { path, lines: lines ? ` · ${lines}` : '' }))}>
        <Icon name="context" size={12} />
        <span className="editor-context-name">{revealControls(name)}</span>
        {lines && <span className="editor-context-lines">· {lines}</span>}
      </span>
    </div>
  );
}

export const UserMessage = memo(function UserMessage({ item }: { item: UserItem }) {
  const t = useT();
  const { menuProps, renderMenu } = useContextMenu();
  const bubbleRef = useRef<HTMLDivElement>(null);
  // Host-authored routing briefs stay collapsed; text outside their comments is unchanged.
  const teamMatch = /<!--\s*pomp-(team|agent|dm):([^\s>]+)\s*-->([\s\S]*?)<!--\s*\/pomp-\1\s*-->/.exec(item.text);
  const messageMenu = (): ContextMenuItem[] => {
    const root = bubbleRef.current;
    if (!root) return [];
    const readable = (text: string): string => { const feedback = parseSentCaseFeedback(text); return feedback ? sentCaseFeedbackText(feedback, t) : text; };
    // A routing brief is the host's text, not the user's: "Copy message" copies what the user wrote around it (everything when that is nothing).
    const written = teamMatch ? [item.text.slice(0, teamMatch.index), item.text.slice(teamMatch.index + teamMatch[0].length)].map(part => part.trim()).filter(Boolean).map(readable).join('\n\n') : '';
    const copied = written || readable(item.text);
    const items: ContextMenuItem[] = [{ id: 'copy-message', label: t('menu.copyMessage'), testId: 'context-menu-copy-message', onSelect: () => { void copyText(copied); } }];
    const selected = selectionIn(root);
    if (selected !== undefined) items.push({ id: 'copy-selection', label: t('menu.copySelection'), testId: 'context-menu-copy-selection', onSelect: () => { void copyText(selected); } });
    return items;
  };
  // Attachment chips keep their own menu (copy path); the bubble menu covers the message text.
  const bubbleHandlers = {
    onContextMenu: (event: React.MouseEvent): void => {
      if ((event.target as Element | null)?.closest('[data-attachment-menu]')) return;
      menuProps(messageMenu).onContextMenu?.(event);
      if (event.defaultPrevented) markMenuHandled(event.nativeEvent);
    },
    onKeyDown: (event: React.KeyboardEvent): void => {
      menuProps(messageMenu).onKeyDown?.(event);
    },
  };
  // Dimmed until the host confirms the prompt with chat/turnStarted; marked when its turn ended without running.
  const state = item.failed ? 'failed' : item.promptId === undefined ? 'pending' : '';
  const trimmed = item.text.trim();

  // Check for IRC message: [IRC `a` → `b`]\n\n<body>
  const ircMatch = /^\[IRC `([^`\r\n]+)` → `([^`\r\n]+)`\]\r?\n\r?\n([\s\S]*)$/.exec(trimmed);
  if (ircMatch) {
    const from = ircMatch[1]!.trim();
    const to = ircMatch[2]!.trim();
    const body = ircMatch[3]!.trim();
    return (
      <div className={state ? `msg user irc ${state}` : 'msg user irc'}>
        <div className="irc-line" data-testid="irc-line">
          <span className="irc-agents">{revealControls(from)} → {revealControls(to)}:</span>{' '}
          <span className="irc-body">{revealControls(body.replace(/\s+/gu, ' '))}</span>
        </div>
        <EditorContextChip context={item.editorContext} />
      </div>
    );
  }

  if (teamMatch) {
    const kind = teamMatch[1] as 'team' | 'agent' | 'dm';
    const teamId = teamMatch[2]!;
    const teamContent = teamMatch[3]!.trim();
    const before = item.text.slice(0, teamMatch.index).trim();
    const after = item.text.slice(teamMatch.index + teamMatch[0].length).trim();
    return (
      <div className={state ? `msg user ${state}` : 'msg user'} ref={bubbleRef} tabIndex={-1} {...bubbleHandlers}>
        {before && <div className="user-text user-text-content" data-testid="user-text-content">{revealControls(before)}</div>}
        <TeamBriefCard kind={kind} teamId={teamId} content={teamContent} />
        {after && <UserTextBody text={after} reveal />}
        {item.attachments && item.attachments.length > 0 && <AttachmentSummary files={item.attachments} testId="user-attachments" />}
        <EditorContextChip context={item.editorContext} />
        {item.failed && (
          <div className="user-failed" role="alert">
            <Icon name="error" size={12} />
            <span>{item.failed.message ? `${t('msg.notSent')} — ${engineAbortText(item.failed.message, t)}` : t('msg.notSent')}</span>
          </div>
        )}
        {renderMenu()}
      </div>
    );
  }

  return (
    <div className={state ? `msg user ${state}` : 'msg user'} ref={bubbleRef} tabIndex={-1} {...bubbleHandlers}>
      {/* No badge = the conversation itself answered; a badge names the one-message target (an agent, team or running member). */}
      {item.target && item.target.kind !== 'main' && <TargetBadge target={item.target} />}
      <UserTextBody text={item.text} />
      {item.attachments && item.attachments.length > 0 && <AttachmentSummary files={item.attachments} testId="user-attachments" />}
      <EditorContextChip context={item.editorContext} />
      {item.failed && (
        <div className="user-failed" role="alert">
          <Icon name="error" size={12} />
          <span>{item.failed.message ? `${t('msg.notSent')} — ${engineAbortText(item.failed.message, t)}` : t('msg.notSent')}</span>
        </div>
      )}
      {renderMenu()}
    </div>
  );
});

/** Collapsible reasoning text; while it streams, the collapsed header shows the latest line as a live preview. */
function Thinking({ text, live }: { text: string; live: boolean }) {
  const t = useT();
  const [open, setOpen] = useState(false);
  const preview = live && !open ? clip(text.slice(-240).trim().split('\n').pop() ?? '', 140) : '';
  return (
    <div className={live ? 'thinking live' : 'thinking'}>
      <button type="button" className="thinking-head" aria-expanded={open} onClick={() => setOpen((v) => !v)}>
        <Icon name="chevron" size={12} className={open ? 'chevron open' : 'chevron'} />
        <span className="thinking-title">{live ? t('msg.thinkingLive') : t('msg.thinking')}</span>
        {preview && <span className="thinking-preview">{preview}</span>}
      </button>
      {open && <div className="thinking-body">{text}</div>}
    </div>
  );
}

export const AssistantMessage = memo(function AssistantMessage({ item, canStage = false, root, sessionId }: { item: AssistantItem; canStage?: boolean; root?: string; sessionId?: string }) {
  const t = useT();
  const { menuProps, renderMenu } = useContextMenu();
  const bubbleRef = useRef<HTMLDivElement>(null);
  const messageMenu = (): ContextMenuItem[] => {
    const root = bubbleRef.current;
    if (!root) return [];
    const items: ContextMenuItem[] = [{ id: 'copy-message', label: t('menu.copyMessage'), testId: 'context-menu-copy-message', onSelect: () => { void copyText(item.text); } }];
    const selected = selectionIn(root);
    if (selected !== undefined) items.push({ id: 'copy-selection', label: t('menu.copySelection'), testId: 'context-menu-copy-selection', onSelect: () => { void copyText(selected); } });
    return items;
  };
  const bubbleHandlers = {
    onContextMenu: (event: React.MouseEvent): void => {
      menuProps(messageMenu).onContextMenu?.(event);
      if (event.defaultPrevented) markMenuHandled(event.nativeEvent);
    },
    onKeyDown: (event: React.KeyboardEvent): void => {
      menuProps(messageMenu).onKeyDown?.(event);
    },
  };

  if (item.text) {
    const ircMatch = /^\[IRC `([^`\r\n]+)` → `([^`\r\n]+)`\]\r?\n\r?\n([\s\S]*)$/.exec(item.text.trim());
    if (ircMatch) {
      const from = ircMatch[1]!.trim();
      const to = ircMatch[2]!.trim();
      const body = ircMatch[3]!.trim();
      return (
        <div className="msg assistant irc">
          <div className="irc-line" data-testid="irc-line">
            <span className="irc-agents">{revealControls(from)} → {revealControls(to)}:</span>{' '}
            <span className="irc-body">{revealControls(body.replace(/\s+/gu, ' '))}</span>
          </div>
        </div>
      );
    }
  }

  return (
    <div className="msg assistant" ref={bubbleRef} tabIndex={-1} {...bubbleHandlers}>
      {item.thinking && <Thinking text={item.thinking} live={!item.done && !item.text} />}
      {item.text && <Markdown text={item.text} allowStage={item.done && !item.errorMessage && !['length', 'error', 'aborted'].includes(item.stopReason ?? '')} canStage={canStage} root={root} sessionId={sessionId} />}
      {item.errorMessage && (
        <div className="inline-error" role="alert">
          <Icon name="error" size={13} />
          <span>{engineAbortText(item.errorMessage, t)}</span>
        </div>
      )}
      {item.stopReason === 'length' && <div className="msg-note">{t('msg.lengthCut')}</div>}
      {(item.textTruncated || item.thinkingTruncated) && <div className="msg-note" data-testid="msg-truncated">{t('msg.truncated')}</div>}
      {renderMenu()}
    </div>
  );
});

export const Notice = memo(function Notice({ item }: { item: NoticeItem }) {
  const t = useT();
  const message = engineAbortText(item.message, t);
  return (
    <div className={item.format === 'pre' ? `notice ${item.level} command-output` : `notice ${item.level}`} role={item.level === 'error' ? 'alert' : 'status'}>
      <Icon name={item.level} size={13} />
      {item.format === 'pre' ? <pre className="notice-pre">{message}</pre> : <span className="notice-text">{message}</span>}
    </div>
  );
});

/** Divider shown when omp continues in a new session: the conversation above is no longer part of its context. */
export const SessionResetNote = memo(function SessionResetNote(_props: { item: SessionResetItem }) {
  const t = useT();
  return (
    <div className="notice warning session-reset" role="status">
      <Icon name="warning" size={13} />
      <span className="notice-text">{t('msg.sessionReset')}</span>
    </div>
  );
});

export const AbortedNote = memo(function AbortedNote(_props: { item: AbortedItem }) {
  const t = useT();
  return (
    <div className="notice aborted" role="status">
      <Icon name="stop" size={11} />
      <span className="notice-text">{t('msg.aborted')}</span>
    </div>
  );
});

/**
 * Banner shown when a turn ends with status "error"; offers a retry of the message that turn belonged to when retryable.
 * The retry is disabled when this page no longer knows that message (e.g. the page was reloaded during the turn).
 */
export const TurnErrorBanner = memo(function TurnErrorBanner({
  item,
  canRetry,
  onRetry,
}: {
  item: TurnErrorItem;
  canRetry: boolean;
  onRetry: (item: TurnErrorItem) => void;
}) {
  const t = useT();
  const { code, message, retryable } = item.error;
  return (
    <div className="turn-error" role="alert">
      <div className="turn-error-head">
        <Icon name="error" size={14} />
        <strong>{t('error.turn')}</strong>
        {code && !isEngineAbort(message) && <code className="turn-error-code">{code}</code>}
      </div>
      {message && <div className="turn-error-message">{engineAbortText(message, t)}</div>}
      {retryable && (
        <div className="turn-error-actions">
          <button
            type="button"
            className="btn"
            data-testid="retry"
            disabled={!canRetry || item.retryText === undefined}
            onClick={() => onRetry(item)}
          >
            <Icon name="restart" size={12} />
            {t('error.retry')}
          </button>
        </div>
      )}
    </div>
  );
});
