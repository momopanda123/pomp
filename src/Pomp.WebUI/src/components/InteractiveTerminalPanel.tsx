import { useEffect, useRef, useState } from 'react';
import type { Terminal } from '@xterm/xterm';
import { onMessage, send } from '../protocol';
import { terminalDelta, type TerminalCursor } from '../interactiveOutput';
import { revealControls } from './RevealControls';

import type { WorkLayout } from './WorkPanel';
import { ViewSection } from './ViewSection';
import { Icon } from './Icon';
import { Badge, Toolbar, type BadgeTone } from './ui';
import '../styles/work-tab.css';
import { matchesWorkScope, useWorkState } from '../work/store';
import { useRefreshOnShow } from '../useRefreshOnShow';

/** The host's terminal state ids (InteractiveTerminal.cs view.state) as words; unknown ids are shown as sent. */
function terminalStateLabel(state: string, ko: boolean): string {
  const labels: Record<string, [string, string]> = {
    empty: ['없음', 'none'], starting: ['시작 중', 'starting'], running: ['실행 중', 'running'], closing: ['종료 중', 'closing'],
    exited: ['종료됨', 'exited'], failed: ['실패', 'failed'], stopped: ['중지됨', 'stopped'], disconnected: ['연결 끊김', 'disconnected'],
  };
  const label = labels[state];
  return label ? label[ko ? 0 : 1] : state;
}

export function InteractiveTerminalPanel({ locale, inert, connected = true }: { locale: string; inert: boolean; connected?: boolean; layout?: WorkLayout }) {
  const ko = locale.toLowerCase().startsWith('ko');
  const state = useWorkState('interactive');
  const current = useRef(state); current.current = state;
  const blocked = useRef(inert); blocked.current = inert;
  const [command, setCommand] = useState(''); const [error, setError] = useState('');
  const [partial, setPartial] = useState(false); const [busy, setBusy] = useState(false);
  const container = useRef<HTMLDivElement>(null); const terminal = useRef<Terminal | null>(null);
  const cursor = useRef<TerminalCursor>({ identity: '', total: 0, sequence: 0 });
  const pending = useRef(''); const queued = useRef(''); const timer = useRef<ReturnType<typeof setTimeout> | undefined>(undefined);
  const timeout = useRef<ReturnType<typeof setTimeout> | undefined>(undefined);
  const flush = useRef<() => void>(() => {});
  const serial = useRef(0); const inputUncertain = useRef(false);
  const owner = () => ({ root: current.current.root, sessionId: current.current.sessionId, terminalId: current.current.terminalId, generation: current.current.generation });
  function clearInput() { queued.current = ''; clearTimeout(timer.current); clearTimeout(timeout.current); pending.current = ''; setBusy(false); }
  function issue(action: 'status' | 'start' | 'stop' | 'input', data?: string) {
    if (blocked.current || !current.current.enabled || !current.current.supported || !current.current.connected) return;
    if (pending.current && action !== 'stop') return;
    if (action === 'stop') clearInput();
    const id = `pty-${++serial.current}-${Date.now()}`; pending.current = id; setBusy(true);
    const ok = send('workspace/interactive', { action, requestId: id, ...owner(), ...(action === 'start' ? { command, timeoutSeconds: 600 } : action === 'input' ? { data } : {}) });
    if (!ok) { clearInput(); inputUncertain.current = true; setError(ko ? '전송되지 않았어요' : 'Not sent'); return; }
    timeout.current = setTimeout(() => { if (pending.current !== id) return; clearInput(); inputUncertain.current = true; setError(ko ? '응답이 확인되지 않았어요' : 'Response unconfirmed'); }, action === 'start' ? 120000 : 15000);
  }
  flush.current = () => {
    if (pending.current || !queued.current || blocked.current || inputUncertain.current) return;
    const data = queued.current; queued.current = ''; issue('input', data);
  };
  useEffect(() => {
    const off = onMessage(message => {
      if (message.type !== 'workspace/interactiveState') return;
      const next = message.payload;
      if (!matchesWorkScope(next.root, next.sessionId)) return;
      if (next.root !== current.current.root || next.sessionId !== current.current.sessionId || next.generation !== current.current.generation) { clearInput(); inputUncertain.current = false; }
      if (next.requestId && next.requestId === pending.current) {
        clearTimeout(timeout.current); pending.current = ''; setBusy(false);
        if (next.warning || next.uncertain) { queued.current = ''; inputUncertain.current = true; }
        else { inputUncertain.current = false; setError(''); }
      }
      current.current = next;
      if (!next.connected || next.uncertain || next.state !== 'running') queued.current = '';
      if (!pending.current && queued.current) timer.current = setTimeout(() => flush.current(), 0);
    });
    return () => { off(); clearTimeout(timer.current); clearTimeout(timeout.current); queued.current = ''; };
  }, []);
  useRefreshOnShow(connected && !inert, () => issue('status'));
  const identity = `${state.root}\n${state.sessionId}\n${state.terminalId ?? ''}\n${state.generation ?? ''}`;
  useEffect(() => {
    if (!container.current || !state.terminalId || !state.connected) return;
    let disposed = false;
    let release: (() => void) | undefined;
    void Promise.all([import('@xterm/xterm'), import('@xterm/addon-fit')]).then(([{ Terminal }, { FitAddon }]) => {
    if (disposed || !container.current) return;
    const term = new Terminal({ cols: 100, rows: 24, scrollback: 1000, convertEol: false, disableStdin: true, fontSize: 13, linkHandler: { activate() {} } });
    const fit = new FitAddon(); term.loadAddon(fit); term.open(container.current); terminal.current = term;
    cursor.current = { identity: '', total: 0, sequence: 0 };
    // Ignore clipboard/title/hyperlink OSC and device-query responses: process output must not generate input or open URLs.
    const handlers = [52, 0, 1, 2, 4, 8, 10, 11, 12].map(code => term.parser.registerOscHandler(code, () => true));
    for (const final of ['n', 'c', 't']) { handlers.push(term.parser.registerCsiHandler({ final }, () => true)); handlers.push(term.parser.registerCsiHandler({ prefix: '?', final }, () => true)); handlers.push(term.parser.registerCsiHandler({ prefix: '>', final }, () => true)); }
    handlers.push(term.parser.registerEscHandler({ final: 'Z' }, () => true));
    for (const prefix of ['', '?']) handlers.push(term.parser.registerCsiHandler({ prefix, intermediates: '$', final: 'p' }, () => true));
    handlers.push(term.parser.registerDcsHandler({ intermediates: '$', final: 'q' }, () => true));
    handlers.push(term.parser.registerDcsHandler({ intermediates: '+', final: 'q' }, () => true));
    const input = term.onData(data => {
      const latest = current.current;
      if (blocked.current || inputUncertain.current || !latest.enabled || !latest.supported || !latest.connected || latest.uncertain || latest.state !== 'running') return;
      if (queued.current.length + data.length > 4096 || data.includes('\0')) { queued.current = ''; setError(ko ? '입력이 너무 길어요' : 'Input too long'); return; }
      queued.current += data; clearTimeout(timer.current); timer.current = setTimeout(() => flush.current(), 25);
    });
    let resizeTimer: ReturnType<typeof setTimeout> | undefined;
    const resize = () => { clearTimeout(resizeTimer); resizeTimer = setTimeout(() => {
      if (disposed || blocked.current || pending.current || !current.current.enabled || !current.current.supported || !current.current.connected || current.current.state !== 'running') return;
      try { fit.fit(); if (term.cols >= 20 && term.cols <= 300 && term.rows >= 5 && term.rows <= 100) send('workspace/interactive', { action: 'resize', ...owner(), cols: term.cols, rows: term.rows }); } catch { /* Hidden/collapsed host has no usable dimensions. */ }
    }, 150); };
    const observer = typeof ResizeObserver === 'undefined' ? undefined : new ResizeObserver(resize); observer?.observe(container.current); resize();
    release = () => { clearTimeout(resizeTimer); observer?.disconnect(); input.dispose(); handlers.forEach(h => h.dispose()); term.dispose(); terminal.current = null; clearInput(); };
    const delta = terminalDelta(cursor.current, current.current); cursor.current = delta.cursor; setPartial(delta.partial); if (delta.text) term.write(delta.text);
    term.options.disableStdin = blocked.current || !current.current.enabled || current.current.uncertain === true || current.current.state !== 'running' || inputUncertain.current;
    }).catch(() => { if (!disposed) setError(ko ? '터미널 화면을 불러오지 못했어요' : 'Could not load terminal renderer'); });
    return () => { disposed = true; release?.(); };
  }, [identity, state.connected]);
  useEffect(() => {
    const term = terminal.current; if (!term) return;
    term.options.disableStdin = inert || !state.enabled || state.uncertain === true || state.state !== 'running' || inputUncertain.current;
    try { const delta = terminalDelta(cursor.current, state); cursor.current = delta.cursor; if (delta.reset) { term.reset(); setPartial(delta.partial); } if (delta.text) term.write(delta.text); }
    catch { setError(ko ? '터미널 출력 형식이 잘못됐어요' : 'Invalid terminal output'); }
  }, [state, inert]);
  const stateText = terminalStateLabel(state.state, ko);
  const live = ['starting', 'running', 'closing'].includes(state.state);
  const tone: BadgeTone = state.state === 'running' ? 'ok' : state.state === 'starting' || state.state === 'closing' ? 'info' : state.state === 'failed' || state.state === 'disconnected' ? 'error' : 'neutral';
  const unavailable = inert || !state.connected || !state.enabled || !state.supported;
  return <ViewSection sectionKey="work-interactive" className="terminal-panel work-interactive" headingLevel="h2" bodyClassName="ui-flush" inert={inert || undefined}
    active={['starting', 'running'].includes(state.state)} title={ko ? '대화형 터미널' : 'Interactive terminal'} status={<Badge tone={tone} title={state.state}>{revealControls(stateText)}</Badge>}>
    <Toolbar>
      <input className="ui-input ui-mono interactive-command" data-testid="interactive-command" aria-label={ko ? '명령' : 'Command'} placeholder={ko ? '명령' : 'Command'} value={command} maxLength={8000}
        spellCheck={false} onChange={e => setCommand(e.target.value)} disabled={unavailable || busy}
        onKeyDown={e => { if (e.key === 'Enter' && !e.nativeEvent.isComposing && command.trim() && !busy && !live) { e.preventDefault(); issue('start'); } }} />
      <button type="button" className="btn small primary" data-testid="interactive-start" disabled={unavailable || busy || !command.trim() || live} onClick={() => issue('start')}><Icon name="play" size={12} />{ko ? '실행' : 'Run'}</button>
      <button type="button" className="btn small subtle" data-testid="interactive-stop" disabled={unavailable || !state.terminalId || !live} onClick={() => issue('stop')}><Icon name="stop" size={11} />{ko ? '종료' : 'Stop'}</button>
    </Toolbar>
    {(state.shell || state.exitCode !== undefined || partial) && <div className="ui-note interactive-meta">
      {state.shell && <Badge mono title={state.root}>{revealControls(state.shell)}</Badge>}
      {state.exitCode !== undefined && <Badge tone={state.exitCode === 0 ? 'ok' : 'error'} mono>exit {state.exitCode}</Badge>}
      {partial && <Badge tone="warn">{ko ? '출력 일부' : 'Partial output'}</Badge>}
    </div>}
    {state.command && <pre className="interactive-command-echo ui-mono">{revealControls(state.command)}</pre>}
    {(error || state.warning || state.error) && <p className="ui-note" role="alert">{revealControls(error || state.warning || state.error || '')}</p>}
    <div ref={container} className={state.terminalId ? 'work-terminal-screen' : undefined} style={{ height: state.terminalId ? undefined : 0, overflow: 'hidden' }} onKeyDown={e => e.stopPropagation()} />
  </ViewSection>;
}
