import { useEffect, useRef, useState, type KeyboardEvent, type PointerEvent } from 'react';
import { useT } from '../i18n';

interface Hsv { h: number; s: number; v: number }
const clamp = (value: number, low: number, high: number): number => Math.min(high, Math.max(low, value));

/** `#rrggbb` (the `#` may be left out) as hue 0..360, saturation and value 0..1; null for anything else. */
export function hexToHsv(hex: string): Hsv | null {
  const match = /^#?([0-9a-f]{6})$/iu.exec(hex.trim());
  if (!match) return null;
  const n = parseInt(match[1]!, 16);
  const r = (n >> 16 & 255) / 255, g = (n >> 8 & 255) / 255, b = (n & 255) / 255;
  const max = Math.max(r, g, b), d = max - Math.min(r, g, b);
  const h = d === 0 ? 0 : max === r ? ((g - b) / d + 6) % 6 : max === g ? (b - r) / d + 2 : (r - g) / d + 4;
  return { h: h * 60, s: max === 0 ? 0 : d / max, v: max };
}

/** The color as lowercase `#rrggbb`. */
export function hsvToHex({ h, s, v }: Hsv): string {
  const f = (n: number): string => {
    const k = (n + h / 60) % 6;
    return Math.round((v - v * s * Math.max(0, Math.min(k, 4 - k, 1))) * 255).toString(16).padStart(2, '0');
  };
  return `#${f(5)}${f(3)}${f(1)}`;
}

/**
 * A color chooser drawn in the page (the WebView opens no native one): saturation × brightness, hue, and the `#rrggbb` text. Every change
 * goes to `onChange` at once; Esc closes it. Mouse drags and the arrow keys (Shift for bigger steps) both work on the two areas.
 */
export function ColorPicker({ value, onChange, onClose, disabled = false, testId = 'color-picker' }: {
  value: string; onChange: (hex: string) => void; onClose: () => void; disabled?: boolean; testId?: string;
}) {
  const t = useT();
  const [hsv, setHsv] = useState<Hsv>(() => hexToHsv(value) ?? { h: 210, s: 0.6, v: 0.7 });
  const [text, setText] = useState(value);
  const square = useRef<HTMLDivElement>(null); const hueBar = useRef<HTMLDivElement>(null);
  // A color set from outside (another swatch) moves the picker; a grey or black keeps the hue it had.
  useEffect(() => {
    if (value.toLowerCase() === hsvToHex(hsv)) return;
    const next = hexToHsv(value); if (!next) return;
    setHsv(previous => next.s === 0 || next.v === 0 ? { ...next, h: previous.h } : next); setText(value.toLowerCase());
  }, [value]);
  useEffect(() => { square.current?.focus({ preventScroll: true }); }, []);
  const commit = (next: Hsv): void => { setHsv(next); const hex = hsvToHex(next); setText(hex); onChange(hex); };
  const fromSquare = (event: PointerEvent<HTMLDivElement>, base: Hsv): void => {
    const r = event.currentTarget.getBoundingClientRect();
    commit({ h: base.h, s: clamp((event.clientX - r.left) / r.width, 0, 1), v: clamp(1 - (event.clientY - r.top) / r.height, 0, 1) });
  };
  const fromHue = (event: PointerEvent<HTMLDivElement>, base: Hsv): void => {
    const r = event.currentTarget.getBoundingClientRect();
    commit({ ...base, h: clamp((event.clientX - r.left) / r.width, 0, 1) * 359.9 });
  };
  const drag = (apply: (event: PointerEvent<HTMLDivElement>, base: Hsv) => void) => ({
    onPointerDown: (event: PointerEvent<HTMLDivElement>) => {
      if (disabled || event.button !== 0) return;
      event.currentTarget.setPointerCapture(event.pointerId); event.currentTarget.focus({ preventScroll: true }); apply(event, hsv);
    },
    onPointerMove: (event: PointerEvent<HTMLDivElement>) => { if (!disabled && event.currentTarget.hasPointerCapture(event.pointerId)) apply(event, hsv); },
  });
  const step = (event: KeyboardEvent): number => (event.shiftKey ? 0.1 : 0.02);
  const squareKeys = (event: KeyboardEvent<HTMLDivElement>): void => {
    const moves: Record<string, Partial<Hsv>> = {
      ArrowLeft: { s: hsv.s - step(event) }, ArrowRight: { s: hsv.s + step(event) }, ArrowUp: { v: hsv.v + step(event) }, ArrowDown: { v: hsv.v - step(event) },
    };
    const move = moves[event.key]; if (!move || disabled) return;
    event.preventDefault(); commit({ h: hsv.h, s: clamp(move.s ?? hsv.s, 0, 1), v: clamp(move.v ?? hsv.v, 0, 1) });
  };
  const hueKeys = (event: KeyboardEvent<HTMLDivElement>): void => {
    const delta = event.shiftKey ? 10 : 2;
    const next = event.key === 'ArrowLeft' || event.key === 'ArrowDown' ? hsv.h - delta : event.key === 'ArrowRight' || event.key === 'ArrowUp' ? hsv.h + delta
      : event.key === 'Home' ? 0 : event.key === 'End' ? 359.9 : undefined;
    if (next === undefined || disabled) return;
    event.preventDefault(); commit({ ...hsv, h: clamp(next, 0, 359.9) });
  };
  const hex = hsvToHex(hsv);
  return <div className="color-picker" data-testid={testId} onKeyDown={event => {
    if (event.key === 'Escape' && !event.nativeEvent.isComposing) { event.preventDefault(); event.stopPropagation(); onClose(); }
  }}>
    <div ref={square} className="color-picker-square" role="slider" tabIndex={disabled ? -1 : 0} aria-label={t('color.square')} aria-valuetext={hex}
      aria-valuemin={0} aria-valuemax={100} aria-valuenow={Math.round(hsv.s * 100)} aria-disabled={disabled || undefined} data-testid={`${testId}-square`}
      style={{ backgroundColor: `hsl(${hsv.h} 100% 50%)` }} onKeyDown={squareKeys} {...drag(fromSquare)}>
      <span className="color-picker-marker" style={{ left: `${hsv.s * 100}%`, top: `${(1 - hsv.v) * 100}%`, background: hex }} />
    </div>
    <div ref={hueBar} className="color-picker-hue" role="slider" tabIndex={disabled ? -1 : 0} aria-label={t('color.hue')} aria-valuemin={0} aria-valuemax={360}
      aria-valuenow={Math.round(hsv.h)} aria-disabled={disabled || undefined} data-testid={`${testId}-hue`} onKeyDown={hueKeys} {...drag(fromHue)}>
      <span className="color-picker-marker" style={{ left: `${(hsv.h / 360) * 100}%`, top: '50%', background: `hsl(${hsv.h} 100% 50%)` }} />
    </div>
    <label className="color-picker-hex"><span>{t('color.hex')}</span>
      <input type="text" value={text} maxLength={7} spellCheck={false} autoComplete="off" disabled={disabled} data-testid={`${testId}-hex`}
        onChange={event => { const typed = event.target.value; setText(typed); const next = hexToHsv(typed); if (next) { setHsv(next); onChange(hsvToHex(next)); } }}
        onBlur={() => setText(hex)}
        onKeyDown={event => { if (event.key === 'Enter' && !event.nativeEvent.isComposing) { event.preventDefault(); onClose(); } }} />
    </label>
  </div>;
}
