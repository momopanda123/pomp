import { useEffect, useRef, useState, type Dispatch, type RefObject } from 'react';
import { logToHost, onMessage, send } from './protocol';
import type { Action, Stamped } from './state';
import { applyTheme } from './theme';
import type { SetupUsage } from './setup';
import { nextUsageReset } from './usage';

/** Streaming deltas are coalesced into one reducer pass (and one React render) per ~30 fps frame. */
const FLUSH_MS = 33;

let readySent = false;

/** Installs the host listener, then announces `ui/ready` (the host queues everything until that message). */
export function useHostPump(dispatch: Dispatch<Action>): void {
  useEffect(() => {
    let queue: Stamped[] = [];
    let timer: number | undefined;
    let lastFlush = 0;

    const flush = (): void => {
      timer = undefined;
      lastFlush = performance.now();
      const batch = queue;
      queue = [];
      if (batch.length > 0) dispatch({ type: 'host', batch });
    };

    const off = onMessage((msg) => {
      // The theme is applied immediately; it is not part of the reducer state.
      if (msg.type === 'theme/changed') applyTheme(msg.payload);
      queue.push({ msg, at: Date.now() });
      if (timer === undefined) timer = window.setTimeout(flush, Math.max(0, lastFlush + FLUSH_MS - performance.now()));
    });

    if (!readySent) {
      readySent = true; // React StrictMode runs effects twice in dev; the host must get exactly one ui/ready
      send('ui/ready', {});
    }

    return () => {
      off();
      if (timer !== undefined) window.clearTimeout(timer);
    };
  }, [dispatch]);
}

/** `Esc` anywhere in the tool window returns focus to the composer (it never answers or dismisses a dialog). */
export function useEscapeToComposer(input: RefObject<HTMLTextAreaElement | null>): void {
  useEffect(() => {
    const onKeyDown = (event: KeyboardEvent): void => {
      if (event.key === 'Escape' && !event.isComposing) input.current?.focus();
    };
    window.addEventListener('keydown', onKeyDown);
    return () => window.removeEventListener('keydown', onKeyDown);
  }, [input]);
}

/**
 * Files dropped on the page would navigate the WebView away from the chat UI (attachments use the host picker instead).
 * Only file drags are swallowed: cancelling every drop would also cancel dropping text (code dragged from the editor) into the composer.
 */
export function useIgnoreFileDrops(): void {
  useEffect(() => {
    const swallow = (event: DragEvent): void => {
      if (event.dataTransfer?.types.includes('Files')) event.preventDefault();
    };
    window.addEventListener('dragover', swallow);
    window.addEventListener('drop', swallow);
    return () => {
      window.removeEventListener('dragover', swallow);
      window.removeEventListener('drop', swallow);
    };
  }, []);
}

/** Forwards uncaught page errors to the host log (`ui/log`). */
export function useGlobalErrorLogging(): void {
  useEffect(() => {
    const onError = (event: ErrorEvent): void =>
      logToHost('error', `${event.message} (${event.filename}:${event.lineno}:${event.colno})`);
    const onRejection = (event: PromiseRejectionEvent): void =>
      logToHost('error', `Unhandled rejection: ${String(event.reason)}`);
    window.addEventListener('error', onError);
    window.addEventListener('unhandledrejection', onRejection);
    return () => {
      window.removeEventListener('error', onError);
      window.removeEventListener('unhandledrejection', onRejection);
    };
  }, []);
}

/** Current time, refreshed every `intervalMs` while `active` (drives countdowns). */
export function useNow(active: boolean, intervalMs = 250): number {
  const [now, setNow] = useState(() => Date.now());
  useEffect(() => {
    if (!active) return;
    setNow(Date.now());
    const id = window.setInterval(() => setNow(Date.now()), intervalMs);
    return () => window.clearInterval(id);
  }, [active, intervalMs]);
  return now;
}

/**
 * The time usage is shown at: it moves on at each limit's reset time, so a limit that reset shows its new window (0%) then, without
 * waiting for another render; checked again at least hourly (a computer that slept past a reset).
 */
export function useUsageNow(usage: SetupUsage | undefined): number {
  const [now, setNow] = useState(() => Date.now());
  const next = nextUsageReset(usage, now);
  useEffect(() => {
    const wait = Math.min(3_600_000, next === undefined ? 3_600_000 : Math.max(0, next - Date.now()) + 500);
    const id = window.setTimeout(() => setNow(Date.now()), wait);
    return () => window.clearTimeout(id);
  }, [next, now]);
  return now;
}

/** After a countdown hits zero the host normally sends *_dismiss; if it never does, drop the request locally. */
const EXPIRY_GRACE_MS = 2500;

/**
 * Remaining milliseconds until `receivedAt + timeoutMs` (null without a timeout). `onExpired` fires once, a short
 * grace period after the countdown reached zero.
 */
export function useCountdown(receivedAt: number, timeoutMs: number | undefined, onExpired?: () => void): number | null {
  const enabled = timeoutMs !== undefined && timeoutMs > 0;
  const now = useNow(enabled);
  const remaining = enabled ? Math.max(0, receivedAt + timeoutMs - now) : null;

  const callback = useRef(onExpired);
  callback.current = onExpired;
  const expired = remaining === 0;
  useEffect(() => {
    if (!expired) return;
    const id = window.setTimeout(() => callback.current?.(), EXPIRY_GRACE_MS);
    return () => window.clearTimeout(id);
  }, [expired]);

  return remaining;
}
