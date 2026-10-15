import { useEffect, useState } from 'react';
import { onMessage, send } from '../protocol';
import { useWorkScope, useWorkState } from '../work/store';
import { useRefreshOnShow } from '../useRefreshOnShow';

/** This conversation's hook runtime (`workspace/hooksState`): the listening switch, the running hook and the run history. */
export function useProjectHooks({ sessionId, workspaceKey, disabled, inert }: { sessionId?: string; workspaceKey: string; disabled: boolean; inert: boolean }) {
  useWorkScope(workspaceKey, sessionId);
  const state = useWorkState('hooks');
  const [transportError, setTransportError] = useState(false);
  useEffect(() => onMessage(message => {
    if (message.type !== 'workspace/hooksState' || (workspaceKey && message.payload.root !== workspaceKey) || (sessionId && message.payload.sessionId !== sessionId)) return;
    setTransportError(false);
  }), [workspaceKey, sessionId]);
  useRefreshOnShow(!disabled && !inert, () => { send('workspace/hooks', { action: 'refresh' }); });
  /** False when nothing was sent (no state yet, hidden, or no host). */
  function command(action: 'enable' | 'cancel' | 'refresh', enabled?: boolean): boolean {
    if (disabled || inert) return false;
    if (action === 'refresh') return send('workspace/hooks', { action });
    if (!state) return false;
    const sent = send('workspace/hooks', { action, root: state.root, sessionId: state.sessionId, enabled });
    if (!sent) setTransportError(true);
    return sent;
  }
  return { state, transportError, command };
}
