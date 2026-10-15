import { memo, useEffect, useLayoutEffect, useMemo, useRef, useState, type CSSProperties } from 'react';
import { parseUnifiedDiff, sideBySideRows, type ContentRow, type DiffRow, type SplitRow } from '../diff';
import { useT } from '../i18n';
import { revealControls } from './RevealControls';

const INITIAL_ROWS = 1500;
const SPLIT_MIN_WIDTH = 480;
type DiffMode = 'inline' | 'split';
// Session-only preference: no disk/browser storage and no effect on the host protocol.
let preferredMode: DiffMode = 'inline';

function Row({ row }: { row: DiffRow }) {
  if (row.kind === 'add' || row.kind === 'del' || row.kind === 'ctx') {
    const sign = row.kind === 'add' ? '+' : row.kind === 'del' ? '-' : ' ';
    return <tr className={`diff-row ${row.kind}`}><td className="ln"><span>{row.oldNo ?? ''}</span><span>{row.newNo ?? ''}</span></td><td className="code"><span className="sign">{sign}</span>{revealControls(row.text)}</td></tr>;
  }
  return <tr className={`diff-row ${row.kind}`}><td className="ln" /><td className="code">{revealControls(row.text)}</td></tr>;
}

function Cell({ row, side }: { row?: ContentRow; side: 'old' | 'next' }) {
  const kind = row?.kind ?? 'empty';
  return <><td className={`ln ${kind}`}><span>{(side === 'old' ? row?.oldNo : row?.newNo) ?? ''}</span></td><td className={`code ${kind}`}>
    {row && <><span className="sign">{row.kind === 'del' ? '-' : row.kind === 'add' ? '+' : ' '}</span>{revealControls(row.text)}</>}
  </td></>;
}

function Split({ row }: { row: SplitRow }) {
  if (row.kind !== 'pair') return <tr className={`diff-row ${row.kind}`}><td colSpan={4} className="code">{revealControls(row.text)}</td></tr>;
  return <tr className="diff-row pair"><Cell row={row.old} side="old" /><Cell row={row.next} side="next" /></tr>;
}

/** Both display modes use the same unified-row budget: switching mode cannot bypass full-diff approval gating. */
export const DiffView = memo(function DiffView({ diff, onFullyShown }: { diff: string; onFullyShown?: (shown: boolean) => void }) {
  const t = useT();
  const parsed = useMemo(() => parseUnifiedDiff(diff), [diff]);
  const [expandedDiff, setExpandedDiff] = useState<string | null>(null);
  const [mode, setMode] = useState<DiffMode>(() => preferredMode);
  const root = useRef<HTMLDivElement>(null);
  const [wide, setWide] = useState(false);
  useLayoutEffect(() => {
    const el = root.current;
    if (!el) return;
    const measure = (): void => setWide(el.clientWidth >= SPLIT_MIN_WIDTH);
    measure();
    const observer = new ResizeObserver(measure);
    observer.observe(el);
    return () => observer.disconnect();
  }, []);
  const effectiveMode = mode === 'split' && wide ? 'split' : 'inline';
  const rows = expandedDiff === diff || parsed.rows.length <= INITIAL_ROWS ? parsed.rows : parsed.rows.slice(0, INITIAL_ROWS);
  const hidden = parsed.rows.length - rows.length;
  const splitRows = useMemo(() => sideBySideRows(rows), [rows]);
  useEffect(() => { onFullyShown?.(hidden === 0); }, [hidden, onFullyShown]);

  const chooseMode = (next: DiffMode): void => { preferredMode = next; setMode(next); };

  return (
    <div className="diff" role="group" ref={root} aria-label={t('perm.diff')} data-mode={effectiveMode}>
      <div className="diff-head">
        <span>{t('perm.diff')}</span>
        <span className="diff-modes" role="group" aria-label={t('diff.mode')}>
          <button type="button" className="btn subtle" data-testid="diff-inline" aria-pressed={mode === 'inline'} onClick={() => chooseMode('inline')}>{t('diff.inline')}</button>
          <button type="button" className="btn subtle" data-testid="diff-split" aria-pressed={mode === 'split'} onClick={() => chooseMode('split')}>{t('diff.split')}</button>
        </span>
        <span className="diff-stat"><span className="add">+{parsed.added}</span><span className="del">−{parsed.removed}</span></span>
      </div>
      {rows.length === 0 ? null : (
        <div className="diff-scroll" tabIndex={0}>
          <table className={effectiveMode === 'split' ? 'diff-table split' : 'diff-table'} style={{ '--digits': parsed.digits } as CSSProperties}>
            {effectiveMode === 'split' && <><colgroup><col className="split-number" /><col /><col className="split-number" /><col /></colgroup><thead><tr><th colSpan={2}>{t('diff.before')}</th><th colSpan={2}>{t('diff.after')}</th></tr></thead></>}
            <tbody>{effectiveMode === 'split' ? splitRows.map((row, index) => <Split key={index} row={row} />) : rows.map((row, index) => <Row key={index} row={row} />)}</tbody>
          </table>
          {hidden > 0 && <div className="diff-more">{t('diff.moreLines', { count: hidden })}{' '}<button type="button" className="btn subtle" data-testid="diff-show-all" onClick={() => setExpandedDiff(diff)}>{t('diff.showAll')}</button></div>}
        </div>
      )}
    </div>
  );
});
