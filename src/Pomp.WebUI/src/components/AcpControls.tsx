import { useEffect, useId, useRef, useState } from 'react';
import type { AcpCommand, AcpConfigOption, AcpConfigValue, AcpReply, AcpState, BackendId, BackendState } from '../backend';
import { newId } from '../format';
import { useT, type Translate } from '../i18n';
import { send } from '../protocol';
import { ClampedText } from './ClampedText';
import { RequestCard } from './Dialogs';
import { PickerPopover } from './ModelPickers';
import { Icon } from './Icon';
import { revealControls } from './RevealControls';
import { Badge, Switch, matches } from './ui';

/** What keeps Antigravity from starting, in the page's words (the host's own text only when none of its reasons applies). */
function blockedNote(state: BackendState | null, t: Translate): string | undefined {
  const agy = state?.antigravity;
  if (!agy || agy.canStart) return undefined;
  if (!agy.workspaceRoot) return t('backend.needsWorkspace');
  if (!agy.installed) return t('backend.needsCli');
  if (!agy.ompToolsAvailable) return t('backend.needsEngine');
  return agy.message;
}

/** Conversation engine settings belong on Accounts, not beside the chat's conversation selector. */
export function BackendPicker({ active, state, disabled, onSelect }: {
  active: BackendId; state: BackendState | null; disabled: boolean; onSelect: (backend: BackendId) => void;
}) {
  const t = useT();
  const [open, setOpen] = useState(false); const anchor = useRef<HTMLDivElement>(null); const chip = useRef<HTMLButtonElement>(null); const id = useId();
  useEffect(() => {
    if (!open) return;
    const outside = (event: PointerEvent) => { if (event.target instanceof Node && !anchor.current?.contains(event.target)) setOpen(false); };
    document.addEventListener('pointerdown', outside, true);
    return () => document.removeEventListener('pointerdown', outside, true);
  }, [open]);
  useEffect(() => { if (disabled) setOpen(false); }, [disabled]);
  const label = t('backend.label'); const name = t(active === 'omp' ? 'backend.omp' : 'backend.acp');
  const blocked = blockedNote(state, t);
  const close = () => { setOpen(false); chip.current?.focus(); };
  const button = <button ref={chip} type="button" className="chip" data-testid="backend-chip" disabled={disabled} title={`${label}: ${name}`} aria-label={`${label}: ${name}`}
    aria-haspopup="listbox" aria-expanded={open} aria-controls={open ? id : undefined} onClick={() => setOpen(!open)}>
    <Icon name={active === 'omp' ? 'bot' : 'external'} size={12} /><span className="chip-text">{name}</span><Icon name="chevronDown" size={11} className="chip-caret" />
  </button>;
  const list = open && <PickerPopover placement="inline" anchorRef={anchor} id={id} testId="backend-options" label={label} onEscape={close}
    items={[{ id: 'omp', label: t('backend.omp'), current: active === 'omp', hint: t('backend.ompHint') },
      { id: 'antigravity-acp', label: t('backend.acp'), current: active === 'antigravity-acp', disabled: blocked !== undefined, hint: t('backend.acpHint'), title: blocked }]}
    status={<div className="picker-status" role="status" data-testid="backend-note">{blocked ?? t('backend.acpNote')}</div>}
    onChoose={item => { setOpen(false); onSelect(item.id as BackendId); }} />;
  return <div className="pickers pickers-panel backend-field"><div className="picker-field" ref={anchor} data-testid="backend-picker">
    <span className="picker-field-label">{label}</span>{button}{list}
  </div></div>;
}

/** A browser sign-in may take minutes (the host waits 15); every other command is answered at once. */
const replyLimit = (key: string): number => key === 'auth' ? 16 * 60_000 : 30_000;

interface AcpCommands {
  command: (key: string, payload: Omit<AcpCommand, 'requestId' | 'sessionId'>) => void;
  waiting: (key: string) => boolean;
  error: string;
}

/** `acp/command` with its correlated `acp/reply`: one request per `key` at a time; a failure or a missing reply is told in `error`. */
function useAcpCommands(replies: AcpReply[], sessionId: string | undefined): AcpCommands {
  const t = useT();
  const [pending, setPending] = useState<Record<string, { key: string; at: number }>>({});
  const [error, setError] = useState('');
  useEffect(() => { setPending({}); setError(''); }, [sessionId]);
  useEffect(() => {
    const answered = replies.filter(reply => pending[reply.requestId]);
    if (!answered.length) return;
    const failed = answered.find(reply => !reply.ok);
    if (failed) setError(failed.error || t('acp.commandFailed'));
    setPending(current => { const next = { ...current }; for (const reply of answered) delete next[reply.requestId]; return next; });
  }, [replies, pending, t]);
  // Each request expires on its own: a sign-in still open in the browser is not given up with a setting that got no answer.
  useEffect(() => {
    const entries = Object.entries(pending);
    if (!entries.length) return;
    const due = Math.min(...entries.map(([, item]) => item.at + replyLimit(item.key)));
    const timer = window.setTimeout(() => {
      const now = Date.now() + 50;
      const overdue = entries.filter(([, item]) => item.at + replyLimit(item.key) <= now).map(([id]) => id);
      setPending(current => { const next = { ...current }; for (const id of overdue) delete next[id]; return next; });
      setError(t('acp.noReply'));
    }, Math.max(0, due - Date.now()));
    return () => window.clearTimeout(timer);
  }, [pending, t]);
  const waiting = (key: string): boolean => Object.values(pending).some(item => item.key === key);
  const command = (key: string, payload: Omit<AcpCommand, 'requestId' | 'sessionId'>): void => {
    if (waiting(key)) return;
    const requestId = newId(); setError('');
    if (!send('acp/command', { ...payload, requestId, sessionId })) { setError(t('error.hostUnavailable')); return; }
    setPending(current => ({ ...current, [requestId]: { key, at: Date.now() } }));
  };
  return { command, waiting, error };
}

type OptionValue = AcpConfigValue & { groupKey?: string; group?: string };
const optionValues = (option: AcpConfigOption): OptionValue[] =>
  (option.options ?? []).flatMap(item => 'options' in item ? item.options.map(value => ({ ...value, groupKey: item.group, group: item.name })) : [item]);

/** The agent's own settings (model, mode, …) as one row of chips, like the model and effort chips under the input, and whether POMP's tools reached it. */
function AcpOptions({ state, toolsAvailable, commands }: { state: AcpState; toolsAvailable: boolean; commands: AcpCommands }) {
  const t = useT(); const row = useRef<HTMLDivElement>(null); const id = useId();
  const [open, setOpen] = useState<string>(); const [query, setQuery] = useState('');
  const chips = useRef(new Map<string, HTMLButtonElement>());
  useEffect(() => { setQuery(''); }, [open]);
  useEffect(() => {
    if (!open) return;
    const outside = (event: PointerEvent) => { if (event.target instanceof Node && !row.current?.contains(event.target)) setOpen(undefined); };
    document.addEventListener('pointerdown', outside, true);
    return () => document.removeEventListener('pointerdown', outside, true);
  }, [open]);
  const { command, waiting } = commands;
  const set = (option: AcpConfigOption, value: string | boolean): void => command(`config:${option.id}`, { action: 'set-config', configId: option.id, value });
  const opened = state.configOptions.find(option => option.id === open && option.type === 'select' && option.options);
  const openedValues = opened ? optionValues(opened) : [];
  const close = (): void => { const chip = open ? chips.current.get(open) : undefined; setOpen(undefined); chip?.focus(); };
  const tools = state.ompTools?.state === 'configured';
  return <div className="acp-options" ref={row} role="group" aria-label={t('acp.options')} data-testid="acp-config">
    {state.configOptions.map(option => {
      const busy = waiting(`config:${option.id}`);
      if (option.type === 'boolean' && typeof option.currentValue === 'boolean')
        return <Switch key={option.id} label={option.name} checked={option.currentValue} disabled={busy} testId={`acp-option-${option.id}`} onChange={value => set(option, value)} />;
      const current = optionValues(option).find(item => item.value === option.currentValue)?.name ?? String(option.currentValue);
      const text = t('acp.optionValue', { name: option.name, value: current });
      // A setting of a kind the page has no control for is shown as it is.
      if (option.type !== 'select' || !option.options) return <Badge key={option.id} title={t('acp.optionFixed')} testId={`acp-option-${option.id}`}>{revealControls(text)}</Badge>;
      return <button key={option.id} ref={button => { if (button) chips.current.set(option.id, button); else chips.current.delete(option.id); }} type="button" className="chip"
        data-testid={`acp-option-${option.id}`} disabled={busy} title={option.description ? `${text} — ${option.description}` : text} aria-haspopup="listbox"
        aria-expanded={open === option.id} aria-controls={open === option.id ? id : undefined} aria-busy={busy || undefined} onClick={() => setOpen(open === option.id ? undefined : option.id)}>
        {busy && <span className="spinner" aria-hidden="true" />}<span className="chip-text">{revealControls(text)}</span><Icon name="chevronDown" size={11} className="chip-caret" />
      </button>;
    })}
    <Badge tone={tools ? 'ok' : 'warn'} icon="plug" title={t(tools ? 'acp.tools.onTitle' : toolsAvailable ? 'acp.tools.offTitle' : 'backend.needsEngine')} testId="acp-tools">{t(tools ? 'acp.tools.on' : 'acp.tools.off')}</Badge>
    {opened && <PickerPopover key={opened.id} placement="inline" anchorRef={row} id={id} testId="acp-option-list" label={opened.name}
      items={openedValues.filter(item => matches(query, item.name)).map(item => ({ id: item.value, label: item.name, title: item.description, current: item.value === opened.currentValue, groupKey: item.groupKey, group: item.group }))}
      search={openedValues.length > 10 ? { value: query, onChange: setQuery, placeholder: t('ui.search'), label: t('ui.search') } : undefined}
      onEscape={close} onChoose={item => { close(); if (item.id !== opened.currentValue) set(opened, item.id); }} />}
  </div>;
}

/**
 * The Antigravity conversation's own strip under the session header: its connection (connecting, sign-in, failure with Reconnect) in the
 * engine banner's form, then its settings. Antigravity owns sign-in and approval; nothing here is inferred or answered for it.
 */
export function AcpControls({ state, backend, replies }: { state: AcpState | null; backend: BackendState | null; replies: AcpReply[] }) {
  const t = useT();
  const commands = useAcpCommands(replies, state?.sessionId);
  // One restart per failure: the host's next state (connecting, connected or the next failure) brings the button back.
  const [restarting, setRestarting] = useState(false);
  useEffect(() => { setRestarting(false); }, [state]);
  useEffect(() => {
    if (!restarting) return;
    const timer = window.setTimeout(() => setRestarting(false), 30_000);
    return () => window.clearTimeout(timer);
  }, [restarting]);
  const reconnect = (): void => { if (send('bridge/restart', {})) setRestarting(true); };
  const blocked = blockedNote(backend, t);
  const failure = state ? (!state.connected && !state.authRequired ? state.error : undefined) : blocked;
  const signingIn = commands.waiting('auth');
  const reconnectButton = <button type="button" className="btn primary" data-testid="acp-reconnect" onClick={reconnect}><Icon name="restart" size={12} />{t('acp.reconnect')}</button>;
  return <div className="acp-controls" data-testid="acp-controls">
    {restarting ? <div className="banner warning" role="status" data-testid="acp-connecting"><span className="spinner" aria-hidden="true" /><span className="banner-text">{t('acp.connecting')}</span></div>
      : state?.authRequired ? <div className="banner warning" role="status" data-testid="acp-auth">
        {signingIn ? <span className="spinner" aria-hidden="true" /> : <Icon name="key" size={14} />}
        <span className="banner-text">
          <strong>{t(signingIn ? 'acp.signingIn' : 'acp.signIn')}</strong>
          {state.error && <ClampedText className="banner-detail" text={state.error} />}
          {!state.authMethods?.length && <span className="banner-detail">{t('acp.noAuthMethod')}</span>}
        </span>
        <span className="banner-actions">
          {state.authMethods?.map((method, index) => <button key={method.id} type="button" className={index === 0 ? 'btn primary' : 'btn'} data-testid="acp-auth-method" title={method.description}
            disabled={signingIn} onClick={() => commands.command('auth', { action: 'authenticate', methodId: method.id })}>{method.name}</button>)}
          {!state.authMethods?.length && reconnectButton}
        </span>
      </div>
      : failure ? <div className="banner error" role="alert" data-testid="acp-failed">
        <Icon name="error" size={14} />
        <span className="banner-text"><strong>{t('acp.failed')}</strong><ClampedText className="banner-detail" text={failure} /></span>
        <span className="banner-actions">{reconnectButton}<button type="button" className="btn" onClick={() => send('log/open', {})}>{t('banner.showLog')}</button></span>
      </div>
      : !state?.connected ? <div className="banner warning" role="status" data-testid="acp-connecting"><span className="spinner" aria-hidden="true" /><span className="banner-text">{t('acp.connecting')}</span></div>
      : null}
    {[state?.connected ? state.error : undefined, commands.error].filter((message, index, all) => message && all.indexOf(message) === index).map(message =>
      <div key={message} className="composer-error acp-notice" role="alert" data-testid="acp-error"><Icon name="error" size={12} /><span>{message}</span></div>)}
    {state?.connected && <AcpOptions state={state} toolsAvailable={backend?.antigravity.ompToolsAvailable === true} commands={commands} />}
  </div>;
}

const text = (value: unknown): string | undefined => typeof value === 'string' && value.trim() ? value : undefined;
/** What the agent asks to do, from the fields its tool calls usually carry; everything else stays readable under the full request. */
function permissionSummary(toolCall: Record<string, unknown>): { title?: string; command?: string; path?: string } {
  const raw = toolCall.rawInput;
  const input: Record<string, unknown> = raw !== null && typeof raw === 'object' && !Array.isArray(raw) ? raw as Record<string, unknown> : {};
  const location = Array.isArray(toolCall.locations)
    ? toolCall.locations.map(item => item !== null && typeof item === 'object' ? text((item as Record<string, unknown>).path) : undefined).find(path => path !== undefined) : undefined;
  return { title: text(toolCall.title), command: text(input.command), path: text(input.path) ?? location };
}

/**
 * Antigravity's question before a tool runs, as the request card at the end of the conversation (where POMP's own approvals appear).
 * The choices are the agent's, in its order and words; no key answers for the user.
 */
export function AcpPermissionCard({ permission, total, sessionId, connected, replies }: {
  permission: AcpState['permissions'][number]; total: number; sessionId: string | undefined; connected: boolean; replies: AcpReply[];
}) {
  const t = useT(); const card = useRef<HTMLDivElement>(null);
  const { command, waiting, error } = useAcpCommands(replies, sessionId);
  const key = `permission:${permission.requestId}`;
  const summary = permissionSummary(permission.toolCall);
  return <RequestCard title={t('acp.permission.title')} counter={total > 1 ? t('perm.counter', { index: 1, total }) : undefined} cardRef={card} onCardKeyDown={() => undefined}
    footer={<>
      {error && <div className="dialog-error" role="alert" data-testid="acp-permission-error"><Icon name="error" size={12} /><span>{error}</span></div>}
      <div className="dialog-actions">
        {permission.options.map(option => <button key={option.optionId} type="button" className={option.kind === 'allow_once' ? 'btn primary' : 'btn'} data-testid="acp-permission-option"
          disabled={!connected || waiting(key)} onClick={() => command(key, { action: 'permission', permissionRequestId: permission.requestId, optionId: option.optionId })}>{option.name}</button>)}
      </div>
    </>}>
    <div className="acp-permission" data-testid="acp-permission">
      {summary.title && <div className="perm-title">{revealControls(summary.title)}</div>}
      {(summary.command || summary.path) && <dl className="perm-summary">
        {summary.command && <div><dt>{t('acp.permission.command')}</dt><dd>{revealControls(summary.command)}</dd></div>}
        {summary.path && <div><dt>{t('acp.permission.path')}</dt><dd>{revealControls(summary.path)}</dd></div>}
      </dl>}
      <details className="perm-details" open={!summary.title && !summary.command && !summary.path || undefined}>
        <summary>{t('acp.permission.details')}</summary>
        <pre className="perm-message">{revealControls(JSON.stringify(permission.toolCall, null, 2))}</pre>
      </details>
    </div>
  </RequestCard>;
}
