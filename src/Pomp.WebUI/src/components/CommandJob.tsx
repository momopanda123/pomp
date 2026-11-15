import { useEffect, useRef, useState } from 'react';
import { useLocale, useT } from '../i18n';
import { send, type TerminalState } from '../protocol';
import { stateLabel } from '../stateLabels';
import { Icon } from './Icon';
import { revealControls } from './RevealControls';
import { Badge } from './ui';

/** A command started from a tool card: its state, its output on request (the card's code surface) and Cancel while it waits or runs. */
export function CommandJob({ job, disabled }: { job: TerminalState['jobs'][number]; disabled: boolean }) {
  const locale = useLocale(); const t = useT();
  const [open, setOpen] = useState(false); const [error, setError] = useState(''); const [pending, setPending] = useState(false);
  const cancelling = useRef(false);
  useEffect(() => { cancelling.current = false; setPending(false); setError(''); }, [job.id, job.state]);
  useEffect(() => {
    if (!pending) return;
    const timer = window.setTimeout(() => { cancelling.current = false; setPending(false); setError(t('setup.noReply')); }, 15000);
    return () => window.clearTimeout(timer);
  }, [pending, t]);
  return <div className="command-job" data-testid={`command-job-${job.id}`}>
    <div className="command-job-head">
      <Badge>{stateLabel(job.state, locale, 'terminal')}</Badge>
      <button type="button" className="btn subtle small" aria-expanded={open} data-testid={`command-output-${job.id}`} onClick={() => setOpen(value => !value)}>{t('command.output')}</button>
      {['queued', 'running'].includes(job.state) && <button type="button" className="btn subtle small" data-testid={`command-cancel-${job.id}`} disabled={disabled || pending} onClick={() => {
        if (disabled || cancelling.current) return;
        cancelling.current = true; setPending(true); setError('');
        if (!send('workspace/terminal', { action: 'cancel', id: job.id })) { cancelling.current = false; setPending(false); setError(t('error.hostUnavailable')); }
      }}>{t('dialog.cancel')}</button>}
    </div>
    {open && <>
      <pre className="tool-pre terminal-command">{revealControls(job.command)}</pre>
      <pre className="tool-pre terminal-output">{revealControls(job.output)}</pre>
      {job.truncated && <span className="hint">{t('command.truncated')}</span>}
    </>}
    {(error || job.error) && <div className="inline-error" role="alert"><Icon name="error" size={12} /><span>{revealControls(error || job.error || '')}</span></div>}
  </div>;
}
