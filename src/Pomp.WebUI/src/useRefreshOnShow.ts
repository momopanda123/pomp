import { useEffect, useRef } from 'react';

/** Refresh once per committed show (including reconnect), not on callback changes or StrictMode effect replay. */
export function useRefreshOnShow(visible: boolean, refresh: () => void): void {
  const latest = useRef(refresh);
  latest.current = refresh;
  const shown = useRef(false);
  useEffect(() => {
    if (!visible) { shown.current = false; return; }
    if (shown.current) return;
    let cancelled = false;
    // Run after replayed subscriptions have settled; a replay cleanup cancels only its own queued call.
    queueMicrotask(() => {
      if (cancelled || shown.current) return;
      shown.current = true;
      latest.current();
    });
    return () => { cancelled = true; };
    // Do not clear shown on cleanup: StrictMode replays mount effects without a real hide.
  }, [visible]);
}
