import { useSyncExternalStore, type KeyboardEvent } from 'react';
import { useT } from '../i18n';
import type { TranscriptFinder } from '../useTranscriptFind';
import { MAX_MATCHES } from '../transcriptFind';
import { IconButton, SearchBox } from './ui';

/** The find bar over the top of the conversation (Ctrl+F): query, match counter, previous / next, match case, close. */
export function FindBar({ finder }: { finder: TranscriptFinder }) {
  const t = useT();
  const { open, query, matchCase, total, current, truncated } = useSyncExternalStore(finder.subscribe, finder.getSnapshot);
  if (!open) return null;
  const position = total === 0 ? t('find.none') : t('find.position', { current: current + 1, total: truncated ? `${MAX_MATCHES}+` : total });
  const onInputKeyDown = (event: KeyboardEvent<HTMLInputElement>): void => {
    // The Enter that commits an IME syllable is not a "next".
    if (event.key !== 'Enter' || event.nativeEvent.isComposing || event.keyCode === 229) return;
    event.preventDefault();
    finder.enter(event.shiftKey);
  };
  const onKeyDown = (event: KeyboardEvent<HTMLDivElement>): void => {
    if (event.key !== 'Escape' || event.nativeEvent.isComposing) return;
    event.preventDefault();
    event.stopPropagation();
    finder.close(true);
  };
  return (
    <div className="find-bar" role="search" data-testid="find-bar" data-state={query.trim() !== '' && total === 0 ? 'none' : undefined} onKeyDown={onKeyDown}>
      <SearchBox value={query} onChange={value => finder.setQuery(value)} label={t('find.label')} testId="find-input" inputRef={finder.inputRef} onKeyDown={onInputKeyDown} />
      <div className="find-controls">
        <span className="find-count" role="status" aria-label={position} data-testid="find-count">{current + 1} / {truncated ? `${MAX_MATCHES}+` : total}</span>
        <IconButton icon="arrowUp" label={t('find.previous')} keys="Shift+Enter" disabled={total === 0} onClick={() => finder.step(-1)} testId="find-prev" />
        <IconButton icon="arrowDown" label={t('find.next')} keys="Enter" disabled={total === 0} onClick={() => finder.step(1)} testId="find-next" />
        <IconButton icon="caseSensitive" label={t('find.case')} pressed={matchCase} onClick={() => finder.toggleCase()} testId="find-case" />
        <IconButton icon="x" label={t('find.close')} keys="Escape" onClick={() => finder.close(true)} testId="find-close" />
      </div>
    </div>
  );
}
