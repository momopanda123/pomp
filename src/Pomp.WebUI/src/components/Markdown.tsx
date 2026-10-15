import type { Token } from 'marked';
import { memo, useCallback, useMemo, useEffect, useLayoutEffect, useRef, useState, type MouseEvent } from 'react';
import { copyText, newId } from '../format';
import { onMessage, send } from '../protocol';
import { markMenuHandled, useContextMenu, type ContextMenuItem } from './ContextMenu';
import { revealControls } from './RevealControls';
import { useT } from '../i18n';
import { lexBlocks, renderBlock } from '../markdown';

/**
 * One top-level markdown block. Re-renders only when its own source text changes, so finished blocks stay untouched while a
 * message streams. The link definitions are part of the comparison: a `[text][ref]` in an earlier block is resolved by a
 * definition that may arrive later, which changes the output without changing the block's source text.
 */
const Block = memo(
  function Block({ token, copyLabel, stageLabel }: { token: Token; copyLabel: string; definitions: string; stageLabel?: string }) {
    return <div className="md-block" dangerouslySetInnerHTML={{ __html: renderBlock(token, copyLabel, stageLabel) }} />;
  },
  (prev, next) =>
    prev.copyLabel === next.copyLabel && prev.stageLabel === next.stageLabel && prev.definitions === next.definitions && prev.token.raw === next.token.raw,
);

/** Sanitized markdown; safe to call with partial (still streaming) text. */
export const Markdown = memo(function Markdown({ text, allowStage = false, canStage = false, root = '', sessionId = '' }: { text: string; allowStage?: boolean; canStage?: boolean; root?: string; sessionId?: string }) {
  const t = useT();
  const { blocks, definitions } = useMemo(() => lexBlocks(text), [text]);
  const copyLabel = t('code.copy');
  const copiedLabel = t('code.copied');
  const stageLabel = allowStage ? t('code.stage') : undefined;
  const latestT = useRef(t); latestT.current = t;
  const element = useRef<HTMLDivElement>(null);
  const { menuProps, renderMenu } = useContextMenu();
  const codeMenu = (block: Element | null): ContextMenuItem[] => {
    if (!block) return [];
    const code = block.querySelector('pre')?.textContent ?? '';
    if (!code) return [];
    const items: ContextMenuItem[] = [{ id: 'copy-code', label: latestT.current('menu.copyCode'), testId: 'context-menu-copy-code', onSelect: () => { void copyText(code); } }];
    const stage = block.querySelector<HTMLButtonElement>('button[data-stage]');
    // 'Send to review' only where the block already renders an enabled stage button:
    // it clicks that same button, reusing its existing validation and host message path.
    if (stage && !stage.disabled) items.push({ id: 'send-review', label: latestT.current('menu.sendReview'), testId: 'context-menu-send-review', onSelect: () => { stage.click(); } });
    return items;
  };
  const pending = useRef<string | undefined>(undefined);
  const timeout = useRef<number | undefined>(undefined);
  const [staging, setStaging] = useState(false);
  const [stageStatus, setStageStatus] = useState('');
  useLayoutEffect(() => {
    for (const button of element.current?.querySelectorAll<HTMLButtonElement>('button[data-stage]') ?? []) {
      const code = button.closest('.codeblock')?.querySelector('pre')?.textContent ?? '';
      button.disabled = !canStage || staging || !sessionId || !root || !code.trim() || code.length > 40000;
    }
  }, [text, canStage, staging, allowStage, root, sessionId, stageLabel]);
  useEffect(() => {
    const off = onMessage(message => {
      if (message.type !== 'workspace/snippetResult' || !pending.current || message.payload.requestId !== pending.current) return;
      pending.current = undefined; window.clearTimeout(timeout.current); setStaging(false);
      setStageStatus(message.payload.staged === true ? latestT.current('code.staged') : typeof message.payload.error === 'string' ? message.payload.error : latestT.current('code.stageUnknown'));
    });
    return () => { off(); window.clearTimeout(timeout.current); pending.current = undefined; };
  }, []);

  // Delegated clicks: code block "copy" buttons and links (which must never navigate the chat page itself).
  const onClick = useCallback(
    (event: MouseEvent<HTMLDivElement>) => {
      const target = event.target as Element;
      const stage = target.closest<HTMLButtonElement>('button[data-stage]');
      if (stage) {
        if (stage.disabled || !canStage || !allowStage || pending.current || !sessionId || !root) return;
        const code = stage.closest('.codeblock')?.querySelector('pre')?.textContent ?? '';
        if (!code.trim() || code.length > 40000) return;
        const requestId = newId(); pending.current = requestId;
        if (!send('workspace/snippet', { requestId, root, sessionId, code })) { pending.current = undefined; setStageStatus(t('error.hostUnavailable')); return; }
        setStaging(true); setStageStatus(t('code.staging'));
        timeout.current = window.setTimeout(() => { pending.current = undefined; setStaging(false); setStageStatus(latestT.current('code.stageUnknown')); }, 20000);
        return;
      }
      const button = target.closest('button[data-copy]');
      if (button) {
        const code = button.closest('.codeblock')?.querySelector('pre');
        if (!code) return;
        void copyText(code.textContent ?? '').then((ok) => {
          if (!ok) return;
          button.textContent = copiedLabel;
          window.setTimeout(() => {
            button.textContent = copyLabel;
          }, 1200);
        });
        return;
      }
      const anchor = target.closest('a');
      if (anchor && !anchor.hasAttribute('target')) event.preventDefault();
    },
    [copyLabel, copiedLabel, canStage, allowStage, root, sessionId, t],
  );

  return (
    <div
      ref={element}
      className="md"
      onClick={onClick}
      onContextMenu={(event) => {
        const block = (event.target as Element | null)?.closest('.codeblock');
        if (!block) return;
        menuProps(() => codeMenu(block)).onContextMenu?.(event);
        if (event.defaultPrevented) markMenuHandled(event.nativeEvent);
      }}
      onKeyDown={(event) => {
        if (event.key !== 'ContextMenu' && !(event.shiftKey && event.key === 'F10')) return;
        const block = (event.target as Element | null)?.closest('.codeblock');
        if (!block) return;
        menuProps(() => codeMenu(block)).onKeyDown?.(event);
      }}
    >
      {blocks.map((token, index) => (
        <Block key={index} token={token} copyLabel={copyLabel} definitions={definitions} stageLabel={stageLabel} />
      ))}
      {stageStatus && <p role="status" className="code-stage-status">{revealControls(stageStatus)}</p>}
      {renderMenu()}
    </div>
  );
});
