import { useEffect, useRef, useState } from 'react';

/** Empty remembered views yield to existing content until a viewer chooses a chip in this conversation. */
export function useViewChoice<K extends string>({ views, requested, active, scope, count, onChange }: {
  views: readonly K[]; requested: K; active: boolean; scope: string; count: (view: K) => number | undefined; onChange?: (view: K) => void;
}): [K, (view: K) => void] {
  const [view, setView] = useState(requested);
  const chosen = useRef(false); const previous = useRef({ scope, requested }); const automatic = useRef<K | undefined>(undefined);
  useEffect(() => {
    const changedScope = previous.current.scope !== scope;
    // `scope` is `<workspace>:<conversation>`. The first conversation becoming known (the scope had none yet, as while the page starts) is not
    // another conversation: a view chosen meanwhile stays.
    if (changedScope && !previous.current.scope.endsWith(':')) { chosen.current = false; automatic.current = undefined; }
    if (previous.current.requested !== requested) {
      if (!changedScope && automatic.current !== requested) chosen.current = true;
      automatic.current = undefined; setView(requested);
    }
    previous.current = { scope, requested };
  }, [scope, requested]);
  useEffect(() => {
    if (!active || chosen.current) return;
    // An unknown count (the view's list is still being read) is not an empty view: wait for it before moving away.
    const current = count(view);
    if (current === undefined || current > 0) return;
    const next = views.find(item => (count(item) ?? 0) > 0);
    if (!next || next === view) return;
    automatic.current = next; setView(next); onChange?.(next);
  });
  return [view, next => { chosen.current = true; automatic.current = undefined; setView(next); onChange?.(next); }];
}
