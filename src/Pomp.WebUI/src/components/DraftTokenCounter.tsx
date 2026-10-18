import { useEffect, useRef, useState } from 'react';
import { useT } from '../i18n';
import { newId } from '../format';
import { onMessage, send, type HostToUi, type ModelInfo } from '../protocol';
import { revealControls } from './RevealControls';

/**
 * The basket's token count: the draft, the attached file list and the conversation's knowledge, as the model gets them, counted on request
 * with the model's tokenizer (an estimate when the engine cannot). A changed basket clears the result.
 */
export function DraftTokenCounter({ text, attachments, knowledge, root, sessionId, model, connected, disabled, locale }: {
  /** The knowledge ids the conversation uses: a change clears the result like a changed draft or file list. */
  text: string; attachments: string[]; knowledge: readonly string[]; root?: string; sessionId?: string; model?: ModelInfo; connected: boolean; disabled: boolean; locale: string;
}) {
  const [result, setResult] = useState<HostToUi['workspace/tokenCount']>();
  const [waiting, setWaiting] = useState(false); const request = useRef<string | null>(null);
  const ko = locale.startsWith('ko'); const t = useT();
  const attachmentsKey = JSON.stringify(attachments); const knowledgeKey = knowledge.join(',');
  useEffect(() => {
    request.current = null; setWaiting(false); setResult(undefined);
    return onMessage(message => {
      if (message.type !== 'workspace/tokenCount' || !request.current || message.payload.requestId !== request.current) return;
      request.current = null; setWaiting(false);
      const data = message.payload;
      if (data.error) { setResult(data); return; }
      if (data.root?.replaceAll('\\', '/').toLowerCase() !== root?.replaceAll('\\', '/').toLowerCase() || data.modelId !== model?.id || data.provider !== model?.provider || !Number.isSafeInteger(data.tokens) || data.tokens! < 0 || data.tokens! > 512000 || !data.method || !['catalog-native', 'fallback-native', 'byte-upperbound', 'byte-estimate', 'empty'].includes(data.method)) {
        setResult({ requestId: data.requestId, error: ko ? '토큰 계산 응답이 올바르지 않아요' : 'Invalid or stale tokenizer response' }); return;
      }
      setResult(data);
    });
  }, [text, attachmentsKey, knowledgeKey, root, sessionId, model?.id, model?.provider, connected]);
  useEffect(() => {
    if (!waiting) return;
    const id = request.current;
    const timer = window.setTimeout(() => { if (request.current === id) { request.current = null; setWaiting(false); setResult({ requestId: id ?? '', error: ko ? '토큰 계산 응답을 받지 못했어요' : 'No token count response received' }); } }, 12000);
    return () => window.clearTimeout(timer);
  }, [waiting, ko]);
  const unusable = disabled || !connected || !root || !sessionId || !model || text.length > 128000;
  const count = () => {
    if (unusable || request.current || !root || !sessionId || !model) return;
    const id = newId(); request.current = id; setWaiting(true); setResult(undefined);
    if (!send('workspace/countDraftTokens', { requestId: id, root, sessionId, modelId: model.id, provider: model.provider, text, attachments: attachments.map(path => ({ path })) })) {
      request.current = null; setWaiting(false); setResult({ requestId: id, error: t('error.hostUnavailable') });
    }
  };
  const label = waiting ? (ko ? '계산 중' : 'Counting') : (ko ? '토큰 계산' : 'Count tokens');
  return <>
    <button type="button" className="btn small draft-token-button" data-testid="basket-count-tokens" disabled={unusable || waiting} title={ko ? '초안, 첨부 파일, 선택한 지식의 토큰 수' : 'Tokens of the draft, attached files and selected knowledge'} onClick={count}>{label}</button>
    {result?.error && <span role="alert" className="draft-token-result">{revealControls(result.error)}</span>}
    {result && !result.error && <span role="status" className="draft-token-result" data-testid="basket-token-result">
      = {(result.estimated || result.method === 'byte-estimate' || result.method === 'byte-upperbound') && '≈'}{result.tokens?.toLocaleString()} {ko ? '토큰' : 'tokens'}
    </span>}
  </>;
}
