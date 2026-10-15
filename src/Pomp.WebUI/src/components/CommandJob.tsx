import { useEffect, useRef, useState } from 'react';
import { useLocale, useT } from '../i18n';
import { send, type TerminalState } from '../protocol';
import { stateLabel } from '../stateLabels';
import { revealControls } from './RevealControls';

export function CommandJob({ job, disabled }: { job: TerminalState['jobs'][number]; disabled: boolean }) {
  const locale = useLocale(); const t = useT(); const ko = locale.startsWith('ko');
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
      <span>{stateLabel(job.state, locale, 'terminal')}</span>
      <button type="button" aria-expanded={open} data-testid={`command-output-${job.id}`} onClick={() => setOpen(value => !value)}>{ko ? '출력' : 'Output'}</button>
      {['queued', 'running'].includes(job.state) && <button type="button" data-testid={`command-cancel-${job.id}`} disabled={disabled || pending} onClick={() => {
        if (disabled || cancelling.current) return;
        cancelling.current = true; setPending(true); setError('');
        if (!send('workspace/terminal', { action: 'cancel', id: job.id })) { cancelling.current = false; setPending(false); setError(t('error.hostUnavailable')); }
      }}>{ko ? '취소' : 'Cancel'}</button>}
    </div>
    {open && <>
      <pre className="terminal-command">{revealControls(job.command)}</pre>
      <pre className="terminal-output">{revealControls(job.output)}</pre>
      {job.truncated && <span>{ko ? '출력 일부 생략' : 'Output truncated'}</span>}
    </>}
    {(error || job.error) && <p role="alert">{revealControls(error || job.error || '')}</p>}
  </div>;
}
