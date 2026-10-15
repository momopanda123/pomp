import { useEffect, useRef, useState } from 'react';
import { useT } from '../i18n';
import { newId } from '../format';
import { onMessage, send, type HostToUi } from '../protocol';
import { revealControls } from './RevealControls';
type Trace = HostToUi['workspace/terminalTrace'];
export function TerminalPlayback({ jobId, locale, inert }: { jobId: string; locale: string; inert: boolean }) {
  const ko = locale.startsWith('ko'); const t = useT();
  const [trace, setTrace] = useState<Trace>(); const [error, setError] = useState('');
  const [waiting, setWaiting] = useState(false); const request = useRef<string | null>(null);
  const [playing, setPlaying] = useState(false); const [speed, setSpeed] = useState(1);
  const [position, setPosition] = useState(0); const cursor = useRef(0);
  const seek = (value: number) => { cursor.current = value; setPosition(value); };
  useEffect(() => {
    const off = onMessage(message => {
      if (message.type !== 'workspace/terminalTrace' || !request.current || message.payload.requestId !== request.current || message.payload.id !== jobId) return;
      request.current = null; setWaiting(false);
      if (message.payload.error) { setError(message.payload.error); return; }
      const frames = message.payload.frames;
      if (!Array.isArray(frames) || frames.length > 512 || frames.some((frame, i) => !frame || typeof frame.text !== 'string' || !Number.isFinite(frame.atMs) || frame.atMs < 0 || frame.atMs > 86400000 || (i > 0 && frame.atMs < frames[i - 1]!.atMs)) || frames.reduce((n, frame) => n + frame.text.length, 0) > 65536) {
        setError('Invalid terminal recording payload'); return;
      }
      setTrace(message.payload); setPlaying(false); seek(frames[0]?.atMs ?? 0); setError('');
    });
    return () => { request.current = null; off(); };
  }, [jobId]);
  useEffect(() => {
    if (!waiting) return;
    const id = request.current;
    const timer = window.setTimeout(() => { if (request.current === id) { request.current = null; setWaiting(false); setError(ko ? '녹화 응답을 받지 못했어요' : 'No recording response received'); } }, 8000);
    return () => window.clearTimeout(timer);
  }, [waiting, ko]);
  useEffect(() => {
    if (inert) setPlaying(false);
  }, [inert]);
  const start = trace?.frames[0]?.atMs ?? 0;
  const end = trace?.frames.at(-1)?.atMs ?? 0;
  useEffect(() => {
    if (!playing) return;
    let previous = performance.now();
    const timer = window.setInterval(() => {
      const now = performance.now(); const next = Math.min(end, cursor.current + (now - previous) * speed); previous = now;
      cursor.current = next; setPosition(next); if (next >= end) setPlaying(false);
    }, 80);
    return () => window.clearInterval(timer);
  }, [playing, speed, end]);
  const capture = () => {
    if (inert || request.current) return;
    const id = newId(); request.current = id; setWaiting(true); setError(''); setPlaying(false);
    if (!send('workspace/terminal', { action: 'trace', id: jobId, requestId: id })) { request.current = null; setWaiting(false); setError(t('error.hostUnavailable')); }
  };
  return <details className="terminal-playback">
    <summary>{ko ? '캡처한 출력 재생' : 'Replay captured output'}</summary>
    <button type="button" disabled={inert || waiting} onClick={capture}>{ko ? '출력 스냅샷 가져오기' : 'Capture output snapshot'}</button>
    {error && <p role="alert">{revealControls(error)}</p>}
    {trace && <div>
      {trace.truncated && <p>{ko ? '기록 일부 생략' : 'Recording truncated'}</p>}
      <button type="button" disabled={inert || !trace.frames.length} onClick={() => { if (!playing && cursor.current >= end) seek(start); setPlaying(!playing); }}>{playing ? (ko ? '재생 일시정지' : 'Pause playback') : (ko ? '출력 재생' : 'Play output')}</button>
      <button type="button" disabled={inert} onClick={() => { setPlaying(false); seek(start); }}>{ko ? '처음으로' : 'Rewind recording'}</button>
      <select aria-label={ko ? '출력 재생 속도' : 'Output playback speed'} disabled={inert} value={speed} onChange={event => { const next = Number(event.target.value); if ([1, 2, 5, 10].includes(next)) setSpeed(next); }}>
        {[1, 2, 5, 10].map(value => <option key={value} value={value}>{value}×</option>)}
      </select>
      <input type="range" aria-label={ko ? '출력 녹화 위치' : 'Output recording position'} min={start} max={Math.max(start, end)} step={1} value={position} disabled={inert || !trace.frames.length} onChange={event => { setPlaying(false); seek(Number(event.target.value)); }} />
      <span>{(position / 1000).toFixed(1)}s / {(end / 1000).toFixed(1)}s</span>
      <pre className="terminal-output" tabIndex={0}>{revealControls(trace.frames.filter(frame => frame.atMs <= position).map(frame => frame.text).join('') || (ko ? '(캡처한 출력 없음)' : '(no captured output)'))}</pre>
    </div>}
  </details>;
}
