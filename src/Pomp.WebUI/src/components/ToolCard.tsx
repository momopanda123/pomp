import { memo, useMemo, useState } from 'react';
import { clip, formatDuration, prettyArgs, summarizeArgs } from '../format';
import { useT } from '../i18n';
import { engineAbortText, isEngineAbort } from '../engineAbortText';
import { useWorkState } from '../work/store';
import { CommandJob } from './CommandJob';
import { MemberProgress } from './LiveAgentProgress';
import { send } from '../protocol';
import type { ToolItem } from '../state';
import { Icon } from './Icon';

const SKIPPED_WAIT = /^\s*Skipped due to a queued background completion\b/;

// Tools that write one file named by a path argument; their .svg/.html results get a preview (docs/ui-protocol.md, Preview).
const FILE_WRITE_TOOLS = new Set(['write', 'edit', 'ast_edit', 'edit_buffer_hashline']);
const PREVIEWABLE = /\.(svg|html?)$/i;

/** The written file when this finished tool call wrote a previewable one; the host checks the path again before opening it. */
export function previewPath(item: ToolItem): string | undefined {
  if (item.state !== 'done' || item.isError || !FILE_WRITE_TOOLS.has(item.toolName)) return undefined;
  const args = item.args ?? {};
  for (const key of ['path', 'file_path', 'filePath', 'file']) {
    const value = args[key];
    if (typeof value === 'string' && value.length > 0 && value.length <= 1024 && PREVIEWABLE.test(value)) return value;
  }
  // edit_buffer_hashline: one patch text whose sections start with `[path#TAG]` (four hex digits).
  if (typeof args.input === 'string') {
    for (const line of args.input.split(/\r?\n/)) {
      const file = /^\[(.+)#[0-9a-fA-F]{4}\]\s*$/.exec(line)?.[1]?.trim();
      if (file && file.length <= 1024 && PREVIEWABLE.test(file)) return file;
    }
  }
  return undefined;
}

/** Collapsible card for one tool call: name, arguments summary, live progress, result (collapsed by default). */
export const ToolCard = memo(function ToolCard({ item, sessionId, controlsEnabled = false }: { item: ToolItem; sessionId?: string; controlsEnabled?: boolean }) {
  const t = useT();
  const terminal = useWorkState('terminal'); const background = useWorkState('background');
  const jobs = sessionId ? terminal.jobs.filter(job => job.origin?.sessionId === sessionId && job.origin.toolCallId === item.toolCallId) : [];
  const members = background && background.sessionId === sessionId ? background.jobs.filter(job => job.parentToolCallId === item.toolCallId) : [];
  const [open, setOpen] = useState(false);
  const summary = useMemo(() => summarizeArgs(item.args), [item.args]);
  const argsJson = useMemo(() => prettyArgs(item.args), [item.args]);

  // omp's wait answers with an error result when a queued background completion is delivered instead: nothing failed.
  const skipped = item.state === 'done' && item.isError === true && item.toolName === 'wait' && SKIPPED_WAIT.test(item.resultText ?? '');
  const stopped = item.state === 'done' && isEngineAbort(item.resultText ?? '');
  const resultText = engineAbortText(item.resultText ?? '', t);
  const failed = item.state === 'done' && item.isError && !skipped && !stopped;
  const statusClass = stopped ? 'interrupted' : failed ? 'error' : skipped ? 'skipped' : item.state;
  const statusLabel = stopped ? t('bridge.stopped') : failed ? t('tool.error') : skipped ? t('tool.skipped') : t(`tool.${item.state}`);
  // Errors keep their first line visible while collapsed so a failure never hides behind a click.
  const errorLine = (failed || stopped) && !open && resultText ? clip(resultText.trim().split('\n')[0] ?? '', 200) : '';
  const preview = useMemo(() => previewPath(item), [item]);

  return (
    <div className={`tool ${statusClass}`}>
      <button type="button" className="tool-head" aria-expanded={open} onClick={() => setOpen((v) => !v)}>
        <span className="tool-status" role="img" aria-label={statusLabel} title={statusLabel}>
          {item.state === 'running' ? (
            <span className="spinner" />
          ) : failed ? (
            <Icon name="x" size={12} />
          ) : skipped ? (
            <Icon name="info" size={12} />
          ) : stopped || item.state === 'interrupted' ? (
            <Icon name="stop" size={9} />
          ) : (
            <Icon name="check" size={12} />
          )}
        </span>
        <span className="tool-name">{item.toolName === 'composer_workflow' ? t('tool.workflow') : item.toolName}</span>
        {item.isHostTool && (
          <span className="badge vs" title={t('tool.hostTitle')}>
            {t('tool.hostBadge')}
          </span>
        )}
        <span className="tool-summary" title={summary}>
          {summary}
        </span>
        {item.durationMs !== undefined && <span className="tool-duration">{formatDuration(item.durationMs)}</span>}
        <Icon name="chevron" size={12} className={open ? 'chevron open' : 'chevron'} />
      </button>

      {item.state === 'running' && item.progress && <div className="tool-progress">{item.progress}</div>}
      {errorLine && <div className="tool-errline">{errorLine}</div>}
      {skipped && !open && <div className="tool-skipline">{statusLabel}</div>}
      {preview && (
        <div className="tool-preview">
          <span className="tool-preview-path" title={preview}>{preview}</span>
          <button type="button" className="btn subtle small" data-testid="tool-preview" onClick={() => send('preview/open', { path: preview })}>
            {t('tool.preview')}
          </button>
        </div>
      )}

      {jobs.map(job => <CommandJob key={job.id} job={job} disabled={!controlsEnabled} />)}
      {members.length > 0 && <ol className="live-members">{members.map(job => <MemberProgress key={job.id} job={job} state={background} sessionId={sessionId} disabled={!controlsEnabled} />)}</ol>}
      {open && (
        <div className="tool-body">
          {argsJson && (
            <>
              <div className="tool-label">{t('tool.args')}</div>
              <pre className="tool-pre">{argsJson}</pre>
            </>
          )}
          <div className="tool-label">{t('tool.result')}</div>
          {item.state === 'running' && item.progress ? (
            <pre className="tool-pre">{item.progress}</pre>
          ) : item.resultText ? (
            <pre className="tool-pre">{resultText}</pre>
          ) : (
            <div className="tool-empty">{t('tool.noResult')}</div>
          )}
        </div>
      )}
    </div>
  );
});
