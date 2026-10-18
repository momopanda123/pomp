import { useEffect, useRef, useState } from 'react';
import { newId } from './format';
import { useT } from './i18n';
import { send, type UiToHost } from './protocol';
import type { CommandReply } from './conversationProtocol';

type CommandType = 'conversation/tab' | 'chat/queue' | 'agents/history';
export function useConversationCommands(replies: CommandReply[], scope = '') {
  const t = useT();
  const request = useRef<string | null>(null);
  const [pending, setPending] = useState(false);
  const [error, setError] = useState('');
  useEffect(() => { request.current = null; setPending(false); setError(''); }, [scope]);
  useEffect(() => {
    const reply = replies.find(item => item.requestId === request.current);
    if (!reply) return;
    request.current = null; setPending(false); setError(reply.ok ? '' : reply.error || t('conversation.failed'));
  }, [replies, t]);
  useEffect(() => {
    if (!pending) return;
    const timer = window.setTimeout(() => { request.current = null; setPending(false); setError(t('setup.noReply')); }, 15000);
    return () => window.clearTimeout(timer);
  }, [pending, t]);
  const post = <K extends CommandType>(type: K, payload: Omit<UiToHost[K], 'requestId'>): boolean => {
    if (request.current) return false;
    const requestId = newId(); request.current = requestId; setPending(true); setError('');
    if (send(type, { ...payload, requestId } as UiToHost[K])) return true;
    request.current = null; setPending(false); setError(t('error.hostUnavailable')); return false;
  };
  return { post, pending, error };
}
