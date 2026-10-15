import type { KnowledgeItem } from '../protocol';
import { revealControls } from './RevealControls';

/** The color the user gave a knowledge. */
export function KnowledgeDot({ color }: { color: string }) {
  return <span className="knowledge-dot" aria-hidden="true" style={{ background: color }} />;
}

/** Every given knowledge as a checkbox row: its color dot and its name. Used by the context basket and the role and team editors. */
export function KnowledgeChecklist({ items, checked, onToggle, disabled = false, testId }: {
  items: readonly KnowledgeItem[]; checked: (item: KnowledgeItem) => boolean; onToggle: (item: KnowledgeItem, value: boolean) => void; disabled?: boolean; testId: string;
}) {
  return <ul className="knowledge-checklist" data-testid={testId}>{items.map(item => <li key={item.id}>
    <label><input type="checkbox" data-testid={`${testId}-${item.id}`} checked={checked(item)} disabled={disabled} onChange={event => onToggle(item, event.target.checked)} />
      <KnowledgeDot color={item.color} /><span>{revealControls(item.name)}</span></label>
  </li>)}</ul>;
}
