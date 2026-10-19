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

/** The Windows paths of the `file:` URIs in a `text/uri-list` (comment lines and other schemes are left out). */
export function fileUriPaths(list: string): string[] {
  const paths: string[] = [];
  for (const line of list.split(/\r?\n/)) {
    const value = line.trim();
    if (!value || value.startsWith('#')) continue;
    try {
      const url = new URL(value);
      if (url.protocol !== 'file:') continue;
      const path = decodeURIComponent(url.pathname).replace(/^\/(?=[A-Za-z]:)/, '').replace(/\//g, '\\');
      paths.push(url.host ? `\\\\${url.host}${path}` : path);
    } catch { /* not a URI */ }
  }
  return paths;
}

/**
 * Files dropped anywhere on the page go into the context basket (`ui/dropFiles`). The drop is always cancelled, which keeps the WebView from
 * navigating to the file. Where the paths come from depends on the drag source:
 * - Explorer files: the host reads them from the message's additional objects (WebView2 Runtime 113+; an older runtime gets a notice).
 * - A `text/uri-list` drag (Solution Explorer in VS 2017): the page sends the paths, from the `file:` URIs or, as Chromium hides those from
 *   pages, from the absolute paths the same drag carries as plain text lines.
 * - No data the page can read (Solution Explorer in later versions uses Visual Studio's own formats): `projectItems`, and the host takes
 *   Solution Explorer's selection, the dragged items.
 * Plain text dragged from the editor still drops into the composer.
 */
export function useFileDrops(unsupported: () => string): void {
  const message = useRef(unsupported); message.current = unsupported;
  useEffect(() => {
    const handled = (event: DragEvent): boolean => {
      const types = event.dataTransfer?.types;
      return !!types && (types.includes('Files') || types.includes('text/uri-list') || types.length === 0);
    };
    const over = (event: DragEvent): void => {
      if (!handled(event)) return;
      event.preventDefault(); event.dataTransfer!.dropEffect = 'copy';
    };
    const drop = (event: DragEvent): void => {
      if (!handled(event)) return;
      event.preventDefault();
      const data = event.dataTransfer!; const webview = window.chrome?.webview;
      if (!webview) return;
      if (!data.types.length) { send('ui/dropFiles', { projectItems: true }); return; }
      if (!data.types.includes('Files')) {
        const listed = fileUriPaths(data.getData('text/uri-list'));
        const paths = listed.length ? listed : data.getData('text/plain').split(/\r?\n/).map(line => line.trim()).filter(line => /^([A-Za-z]:\\|\\\\)/.test(line));
        if (paths.length) send('ui/dropFiles', { paths: paths.slice(0, 500) });
        return;
      }
      if (!data.files.length) return;
      if (!webview.postMessageWithAdditionalObjects) { send('ui/status', { text: message.current(), level: 'warning' }); return; }
      try { webview.postMessageWithAdditionalObjects({ type: 'ui/dropFiles', payload: {} }, data.files); }
      catch (error) { console.error('[pomp] postMessageWithAdditionalObjects failed', error); }
    };
    window.addEventListener('dragover', over);
    window.addEventListener('drop', drop);
    return () => {
      window.removeEventListener('dragover', over);
      window.removeEventListener('drop', drop);
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
