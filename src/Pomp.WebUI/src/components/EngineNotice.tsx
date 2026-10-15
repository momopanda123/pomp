import { useState } from 'react';
import { useT, type Translate } from '../i18n';
import type { EngineInfo } from '../protocol';
import { Icon } from './Icon';
import { revealControls } from './RevealControls';

/** "에이전트 18.9.0 · 옵션에서 지정한 실행 파일 · 검증 안 됨": the agent line of the ⋯ menu; none for the verified copy that ships with POMP. */
export function engineSummary(t: Translate, engine: EngineInfo): string | null {
  if (engine.source === 'bundled' && engine.verified) return null;
  const version = engine.version ?? t('engine.unknownVersion');
  const where = engine.source === 'settings' ? t('engine.settings') : engine.source === 'installed' ? t('engine.installed') : t('engine.bundled');
  return `${t('engine.menu', { version })} · ${where}`;
}

/**
 * A warning above the conversation while the engine is not the omp this POMP release ships and verified (an omp set in the options,
 * or an installed omp in a development package). It can be dismissed until another engine or version is reported.
 */
export function EngineNotice({ engine }: { engine: EngineInfo | null }) {
  const t = useT();
  const [dismissed, setDismissed] = useState<string | null>(null);
  if (!engine || engine.verified) return null;
  const key = `${engine.source}|${engine.path ?? ''}|${engine.version ?? ''}`;
  if (dismissed === key) return null;
  const params = { path: engine.path ?? '', version: engine.version ?? t('engine.unknownVersion') };
  const text = engine.source === 'settings' ? t('engine.warnSettings', params) : t('engine.warnInstalled', params);
  return (
    <div className="banner warning engine-notice" role="status" data-testid="engine-notice">
      <Icon name="warning" />
      <span className="banner-text">{revealControls(text)}</span>
      <button type="button" className="btn subtle" data-testid="engine-notice-dismiss" onClick={() => setDismissed(key)}>
        {t('engine.dismiss')}
      </button>
    </div>
  );
}
