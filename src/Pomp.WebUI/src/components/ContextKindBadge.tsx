import type { WorkspaceFile } from '../protocol';
import { useT } from '../i18n';

/** Labels actual basket item kinds and whether they are included in the next message. */
export type ContextKind = 'document' | 'folder' | 'code' | 'other';

export function contextKindOf(file: Pick<WorkspaceFile, 'kind' | 'symbol'>): ContextKind {
  const kind = (file.kind ?? '').toLowerCase();
  if (kind === 'folder') return 'folder';
  if (kind === 'symbol' || kind === 'code' || (file.symbol ?? '').length > 0) return 'code';
  if (kind === 'document' || kind === 'file' || kind === '') return 'document';
  return 'other';
}

const GLYPHS: Record<ContextKind, string> = {
  document: '▤',
  folder: '▱',
  code: '◇',
  other: '▦',
};

export function ContextKindBadge({ file, included }: { file: Pick<WorkspaceFile, 'kind' | 'symbol'>; included: boolean }) {
  const t = useT();
  const kind = contextKindOf(file);
  const label = t(`basket.kind.${kind}` as 'basket.kind.document');
  const state = t(included ? 'basket.state.included' : 'basket.state.available');
  return (
    <span className={`context-kind context-kind-${kind}${included ? ' included' : ' available'}`} data-testid={`context-kind-${kind}`}
      title={`${label} · ${state}`} aria-label={`${label} · ${state}`}>
      <span className="context-kind-glyph" aria-hidden="true">{GLYPHS[kind]}</span>
      <span className="context-kind-label">{label}</span>
      <span className="context-kind-state">{included ? t('basket.state.includedShort') : t('basket.state.availableShort')}</span>
    </span>
  );
}
