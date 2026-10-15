import { useEffect, useId, useRef, useState, type FormEvent } from 'react';
import { copyText } from '../format';
import { useT } from '../i18n';
import type { SetupLogin, SetupLoginPrompt, SetupProvider } from '../protocol';
import { loginNeedsTerminal, MAX_PROMPT_ANSWER_CHARS, isProviderId, providerLabel, providerName, safeHttpUrl } from '../setup';
import type { SetupCommands } from '../setupCommands';
import { ClampedText } from './ClampedText';
import { Icon } from './Icon';
import { Note } from './Note';
import { plainControls, revealControls } from './RevealControls';
import { ViewSection } from './ViewSection';

/** `SetupCommands.sent.login.tag` of a sign-in that runs in a console window (the provider id otherwise). */
export const CONSOLE_TAG = 'console';

interface LoginCardProps {
  login: SetupLogin;
  providers: readonly SetupProvider[] | undefined;
  cmd: SetupCommands;
  /** A permission request or question is open above the panel. */
  inert: boolean;
  /** Starts the sign-in again (a failed one) / the console sign-in. */
  onSignIn: (provider: string) => void;
  onConsole: (provider?: string) => void;
}

/**
 * The answer to a sign-in prompt (an authorization code to paste): a secret. It lives only in this input; it is cleared the
 * moment it is posted (or the prompt is declined), and is never put into reducer state, storage, a log message or an error text.
 */
function PromptForm({ prompt, cmd, inert }: { prompt: SetupLoginPrompt; cmd: SetupCommands; inert: boolean }) {
  const t = useT();
  const inputId = useId();
  const [value, setValue] = useState('');
  const answer = cmd.feedback('answer');
  const trimmed = value.trim();
  const canSend = trimmed.length > 0 && !answer.pending && !inert;

  const submit = (event: FormEvent): void => {
    event.preventDefault();
    if (!canSend) return;
    // The input is cleared only once the host has the answer; if posting failed the user keeps what they typed.
    if (cmd.post('answer', (requestId) => ({ requestId, action: 'answerPrompt', promptId: prompt.id, value: trimmed }), prompt.id)) setValue('');
  };
  const decline = (): void => {
    cmd.post('answer', (requestId) => ({ requestId, action: 'answerPrompt', promptId: prompt.id }), prompt.id); // no value = cancel this input
    setValue('');
  };

  return (
    <form className="login-prompt" autoComplete="off" noValidate onSubmit={submit} data-testid="login-prompt">
      <label className="login-prompt-label" htmlFor={inputId}>
        {revealControls(prompt.message || t('setup.login.codeLabel'))}
      </label>
      <input
        id={inputId}
        type="password"
        className="field mono"
        data-testid="prompt-input"
        autoComplete="off"
        autoCapitalize="off"
        spellCheck={false}
        maxLength={MAX_PROMPT_ANSWER_CHARS}
        value={value}
        onChange={(event) => setValue(event.target.value)}
      />
      <div className="ui-form-actions">
        <button type="button" className="btn subtle" data-testid="prompt-cancel" disabled={answer.pending || inert} onClick={decline}>
          {t('setup.login.cancelPrompt')}
        </button>
        <button type="submit" className="btn primary" data-testid="prompt-submit" disabled={!canSend}>
          {t('setup.login.confirm')}
        </button>
      </div>
      {answer.error && <Note tone="error" text={answer.error} />}
    </form>
  );
}

/** The sign-in link: a real http(s) link (the host opens it in the browser), and a button that copies it. */
function LoginLink({ url }: { url: string }) {
  const t = useT();
  const safe = safeHttpUrl(url);
  const [copied, setCopied] = useState(false);
  useEffect(() => {
    if (!copied) return;
    const timer = window.setTimeout(() => setCopied(false), 2000);
    return () => window.clearTimeout(timer);
  }, [copied]);

  if (!safe) {
    // Not a web address: never a link, never opened. Shown as text so the user can see what the host reported.
    return (
      <span className="hint" data-testid="login-url-text">
        {revealControls(url)}
      </span>
    );
  }
  let host = '';
  try {
    host = new URL(safe).host;
  } catch {
    // safeHttpUrl already parsed it
  }
  return (
    <div className="login-link">
      <div className="setup-actions">
        <a className="btn" href={safe} target="_blank" rel="noopener noreferrer" data-testid="login-open" title={plainControls(safe)}>
          <Icon name="external" size={12} />
          {t('setup.login.reopen')}
        </a>
        <button
          type="button"
          className="btn subtle"
          data-testid="login-copy"
          onClick={() => {
            void copyText(safe).then((ok) => setCopied(ok));
          }}
        >
          <Icon name={copied ? 'check' : 'copy'} size={12} />
          {copied ? t('setup.login.copied') : t('setup.login.copyLink')}
        </button>
      </div>
      {host && (
        <span className="hint login-host" data-testid="login-host">
          {revealControls(host)}
        </span>
      )}
    </div>
  );
}

/**
 * Where the sign-in stands: starting, running (with the browser link, instructions, progress and the prompt for a code to paste),
 * and its result — a short confirmation, or the failure with the way out (retry, sign in in a terminal). A result that was already
 * there when the panel opened is old news and is not shown again; the user can dismiss a result.
 */
export function LoginCard({ login, providers, cmd, inert, onSignIn, onConsole }: LoginCardProps) {
  const t = useT();
  const { feedback, sent } = cmd;
  const start = feedback('login');
  const cancel = feedback('cancel');
  const viaConsole = sent.login?.tag === CONSOLE_TAG;
  const running = login.state === 'running';
  // The host has not reported the sign-in yet. (A command answered only when the sign-in ends must not look "starting" for ever.)
  const starting = start.pending && !running;
  // A running sign-in proves the host got the command, even when its answer is still to come.
  const startError = start.timedOut && running ? undefined : start.error;

  const resultKey = `${login.state}|${login.provider ?? ''}|${login.message ?? ''}`;
  const [dismissed, setDismissed] = useState<string | null>(() => (login.state === 'succeeded' || login.state === 'failed' ? resultKey : null));
  const lastRequest = useRef(sent.login?.requestId);
  useEffect(() => {
    if (running) setDismissed(null);
  }, [running]);
  useEffect(() => {
    // A new sign-in command: whatever result is showing belongs to the one before it.
    if (sent.login?.requestId !== lastRequest.current) {
      lastRequest.current = sent.login?.requestId;
      setDismissed(null);
    }
  }, [sent.login?.requestId]);

  const name = login.provider ? providerLabel(login.provider, providerName(providers, login.provider)) : undefined;
  const result = login.state === 'succeeded' || login.state === 'failed' ? login.state : undefined;
  const showResult = result !== undefined && !starting && dismissed !== resultKey;
  // The host reports some failures twice (in the login state and as the answer): show the text once.
  const startNote = startError !== undefined && !(login.state === 'failed' && startError === login.message) ? startError : undefined;
  const canRetry = login.provider !== undefined && isProviderId(login.provider);

  // The card is at the top of the panel, the button that started the sign-in may be far below: bring the card into view when
  // it appears and when a prompt comes up (nothing moves if it is visible already).
  const cardRef = useRef<HTMLDivElement>(null);
  const visible = starting || running || showResult || startNote !== undefined || Boolean(cancel.error);
  const promptId = login.prompt?.id;
  useEffect(() => {
    if (visible) cardRef.current?.scrollIntoView({ block: 'nearest' });
  }, [visible, running, promptId]);

  if (!visible) return null;

  return (
    <div className="login-stack" data-testid="login-card" ref={cardRef}><ViewSection sectionKey="setup-login" className="login-section" title={t('section.login')} active={running || starting}
      actions={running && !starting ? <button
        type="button"
        className="btn small"
        data-testid="login-cancel"
        disabled={inert || cancel.pending}
        onClick={() => cmd.post('cancel', (requestId) => ({ requestId, action: 'cancelLogin' }))}
      >
        <Icon name="x" size={12} />
        {t('setup.login.cancel')}
      </button> : undefined}>
      {starting && (
        <div className="login-card" role="status" data-testid="login-starting">
          <div className="login-title">
            <span className="spinner" aria-hidden="true" />
            <span>{t('setup.login.starting')}</span>
          </div>
        </div>
      )}

      {running && !starting && (
        <div className="login-card running" role="status" data-testid="login-running">
          <div className="login-title">
            <span className="spinner" aria-hidden="true" />
            <span className="login-title-text">
              {revealControls(viaConsole ? t('setup.login.consoleRunning') : name ? t('setup.login.running', { name }) : t('setup.login.runningGeneric'))}
            </span>
          </div>
          {login.instructions && <ClampedText className="login-instructions" text={login.instructions} />}
          {login.url && !viaConsole && <LoginLink url={login.url} />}
          {login.progress && <ClampedText className="login-progress" text={login.progress} />}
          {login.prompt && <PromptForm key={login.prompt.id} prompt={login.prompt} cmd={cmd} inert={inert} />}
        </div>
      )}

      {showResult && result === 'succeeded' && (
        <div className="login-card ok" role="status" data-testid="login-succeeded">
          <div className="login-title">
            <Icon name="check" size={13} />
            <span className="login-title-text">{revealControls(name ? t('setup.login.succeeded', { name }) : t('setup.login.succeededGeneric'))}</span>
            <button type="button" className="icon-btn tiny" data-testid="login-dismiss" onClick={() => setDismissed(resultKey)} title={t('setup.dismiss')} aria-label={t('setup.dismiss')}>
              <Icon name="x" size={11} />
            </button>
          </div>
        </div>
      )}

      {showResult && result === 'failed' && (
        <div className="login-card failed" role="alert" data-testid="login-failed">
          <div className="login-title">
            <Icon name="error" size={13} />
            <span className="login-title-text">{revealControls(name ? `${name}: ${t('setup.login.failed')}` : t('setup.login.failed'))}</span>
            <button type="button" className="icon-btn tiny" data-testid="login-dismiss" onClick={() => setDismissed(resultKey)} title={t('setup.dismiss')} aria-label={t('setup.dismiss')}>
              <Icon name="x" size={11} />
            </button>
          </div>
          {login.message && <ClampedText className="login-detail" text={login.message} />}
          <div className="setup-actions">
            {canRetry && (
              <button type="button" className="btn" data-testid="login-retry" disabled={inert} onClick={() => login.provider && onSignIn(login.provider)}>
                {t('setup.retry')}
              </button>
            )}
            <button
              type="button"
              className={loginNeedsTerminal(login.message) ? 'btn primary' : 'btn subtle'}
              data-testid="login-console"
              disabled={inert}
              onClick={() => onConsole(login.provider && isProviderId(login.provider) ? login.provider : undefined)}
            >
              <Icon name="terminal" size={12} />
              {t('setup.add.console')}
            </button>
          </div>
        </div>
      )}

      {startNote !== undefined && <Note tone="error" text={startNote} />}
      {cancel.error && <Note tone="error" text={cancel.error} />}
    </ViewSection></div>
  );
}
