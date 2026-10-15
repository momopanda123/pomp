import { useCallback, useEffect, useRef, useState } from 'react';
import type { GoalCardState } from './components/GoalCard';
import { onMessage } from './protocol';

/**
 * The goal card (typed `/goal`, or the model's `request_goal_run` arriving as `workspace/goalRequest`) and the skills the Tools tab
 * switched off, which the command list leaves out. A card belongs to its conversation: a card for another one is ignored, and a
 * switched or new conversation drops the card. A card the model asked for that is already showing (the host sends it again when the
 * page reloads) is not opened twice.
 */
export function useGoalCard(root: string, sessionId: string) {
  const [card, setCard] = useState<GoalCardState | null>(null);
  const [skillsOff, setSkillsOff] = useState<string[]>([]);
  const counter = useRef(0);
  const scope = useRef({ root, sessionId }); scope.current = { root, sessionId };
  useEffect(() => onMessage(message => {
    if (message.type === 'workspace/goalRequest') {
      const request = message.payload;
      // The page may not know its conversation yet while the first messages of a reload arrive: such a request waits for the check below.
      if (!request.sessionId || !request.text || (scope.current.sessionId && request.sessionId !== scope.current.sessionId)) return;
      setCard(previous => previous?.fromRequest === request.requestId ? previous : { key: ++counter.current, text: request.text, tokenBudget: request.tokenBudget, fromRequest: request.requestId, root: request.root, sessionId: request.sessionId });
    } else if (message.type === 'skills/state') {
      const off = message.payload.skills?.filter(skill => skill.enabled === false).map(skill => skill.name) ?? [];
      setSkillsOff(previous => previous.length === off.length && previous.every((name, index) => name === off[index]) ? previous : off);
    }
  }), []);
  useEffect(() => { setCard(previous => previous?.sessionId && sessionId && previous.sessionId !== sessionId ? null : previous); }, [sessionId]);
  const open = useCallback((text: string, tokenBudget?: number) => setCard({ key: ++counter.current, text, tokenBudget, root: scope.current.root, sessionId: scope.current.sessionId }), []);
  const close = useCallback(() => setCard(null), []);
  return { card, open, close, skillsOff };
}
