import { useCallback, useEffect, useRef, useState } from 'react';
import { useT } from '../i18n';
import { newId } from '../format';
import { onMessage, send, type HostToUi, type ModelInfo } from '../protocol';
import { revealControls } from './RevealControls';
export function DraftTokenCounter({ text, root, sessionId, model, connected, disabled, locale }: {
  text: string; root?: string; sessionId?: string; model?: ModelInfo; connected: boolean; disabled: boolean; locale: string;
}) {
  const [result, setResult] = useState<HostToUi['workspace/tokenCount']>();
  const [waiting, setWaiting] = useState(false); const request = useRef<string | null>(null);
  const ko = locale.startsWith('ko'); const t = useT();
  const [automatic, setAutomatic] = useState(false);
  const lastAutomaticText = useRef<string | null>(null);
  useEffect(() => { setAutomatic(false); lastAutomaticText.current = null; }, [root, sessionId, model?.id, model?.provider, connected]);
  useEffect(() => {
    request.current = null; lastAutomaticText.current = null; setWaiting(false); setResult(undefined);
    return onMessage(message => {
      if (message.type !== 'workspace/tokenCount' || !request.current || message.payload.requestId !== request.current) return;
      request.current = null; setWaiting(false);
      const data = message.payload;
      if (data.error) { setAutomatic(false); setResult(data); return; }
      if (data.root?.replaceAll('\\', '/').toLowerCase() !== root?.replaceAll('\\', '/').toLowerCase() || data.modelId !== model?.id || data.provider !== model?.provider || !Number.isSafeInteger(data.tokens) || data.tokens! < 0 || data.tokens! > 512000 || !data.method || !['catalog-native', 'fallback-native', 'byte-upperbound', 'byte-estimate', 'empty'].includes(data.method)) {
        setAutomatic(false); setResult({ requestId: data.requestId, error: ko ? '토큰 계산 응답이 올바르지 않아요' : 'Invalid or stale tokenizer response' }); return;
      }
      setResult(data);
    });
  }, [text, root, sessionId, model?.id, model?.provider, connected]);
  useEffect(() => {
    if (!waiting) return;
    const id = request.current;
    const timer = window.setTimeout(() => { if (request.current === id) { request.current = null; setWaiting(false); setAutomatic(false); setResult({ requestId: id ?? '', error: ko ? '토큰 계산 응답을 받지 못했어요' : 'No token count response received' }); } }, 12000);
    return () => window.clearTimeout(timer);
  }, [waiting, ko]);
  const count = useCallback(() => {
    if (disabled || !connected || request.current || !root || !sessionId || !model || !text.length || text.length > 128000) return;
    lastAutomaticText.current = text;
    const id = newId(); request.current = id; setWaiting(true); setResult(undefined);
    if (!send('workspace/countDraftTokens', { requestId: id, root, sessionId, modelId: model.id, provider: model.provider, text })) {
      request.current = null; setWaiting(false); setAutomatic(false); setResult({ requestId: id, error: t('error.hostUnavailable') });
    }
  }, [disabled, connected, root, sessionId, model?.id, model?.provider, text, t]);
  useEffect(() => {
    if (!automatic || disabled || !connected || !text.length || text.length > 128000 || lastAutomaticText.current === text) return;
    const timer = window.setTimeout(() => { if (lastAutomaticText.current !== text) count(); }, 600);
    return () => window.clearTimeout(timer);
  }, [automatic, disabled, connected, text, count]);
  return <div className="draft-token-counter">
    <label><input type="checkbox" checked={automatic} disabled={disabled || !connected || !root || !sessionId || !model} onChange={event => { lastAutomaticText.current = null; setAutomatic(event.target.checked); if (!event.target.checked) { request.current = null; setWaiting(false); } }} />{ko ? '자동 계산' : 'Count automatically'}</label>
    <button type="button" disabled={disabled || !connected || waiting || !root || !sessionId || !model || !text.length || text.length > 128000} onClick={count}>{waiting ? (ko ? '계산 중' : 'Counting') : (ko ? '초안 토큰 계산' : 'Count draft tokens')}</button>
    {text.length > 128000 && <p>{ko ? '초안이 너무 길어요.' : 'The draft is too long.'}</p>}
    {result?.error && <p role="alert">{revealControls(result.error)}</p>}
    {result && !result.error && <p role="status">
      {ko ? '초안 본문' : 'Draft text'}: {(result.estimated || result.method === 'byte-estimate' || result.method === 'byte-upperbound') && '≈'}{result.tokens?.toLocaleString()} {ko ? '토큰' : 'tokens'} · {revealControls(result.modelId ?? '')} · {result.method === 'catalog-native' ? (ko ? '카탈로그 토크나이저' : 'Catalog tokenizer') : result.method === 'fallback-native' ? (ko ? '토크나이저' : 'Tokenizer') : result.method === 'empty' ? (ko ? '빈 본문' : 'Empty text') : (ko ? '추정값' : 'Estimate')}
      {result.encoding && ` (${revealControls(result.encoding)})`}
    </p>}
  </div>;
}
