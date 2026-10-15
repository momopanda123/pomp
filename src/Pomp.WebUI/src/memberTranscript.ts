import type { AgentsReplyPayload } from './protocol';

/** Member logs are content, never the engine's hidden instructions. */
export function cleanMemberText(text: string): string {
  return text.replace(/<system-reminder\b[^>]*>[\s\S]*?(?:<\/system-reminder\s*>|$)/gi, '')
    .replace(/^.*Every turn MUST end with a tool call\..*$/gim, '')
    .replace(/^.*xd:\/\/.*$/gim, '').trim();
}
export function memberTranscript(transcript: NonNullable<AgentsReplyPayload['transcript']>): NonNullable<AgentsReplyPayload['transcript']> {
  let remaining = 60000; let truncated = transcript.truncated;
  const visible = transcript.messages.filter(message => message && typeof message.role === 'string' && ['user', 'assistant', 'tool', 'toolresult'].includes(message.role.toLowerCase()))
    .map(message => ({ role: message.role.toLowerCase(), text: cleanMemberText(typeof message.text === 'string' ? message.text : '') })).filter(message => message.text);
  if (visible.length > 40) truncated = true;
  const messages = visible.slice(-40).map(message => {
    const text = message.text.slice(0, Math.min(8000, remaining)); remaining -= text.length;
    if (text.length < message.text.length) truncated = true;
    return { role: message.role, text };
  }).filter(message => message.text);
  return { ...transcript, messages, truncated };
}
