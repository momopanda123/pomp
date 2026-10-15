import type { InteractiveTerminalState } from './protocol';
export interface TerminalCursor { identity: string; total: number; sequence: number }
/** Append only unseen bounded engine output. A missed prefix resets the screen; never replay input. */
export function terminalDelta(cursor: TerminalCursor, view: InteractiveTerminalState): { cursor: TerminalCursor; reset: boolean; text: string; partial: boolean } {
  const identity = `${view.root}\n${view.sessionId}\n${view.terminalId ?? ''}\n${view.generation ?? ''}`;
  const output = view.output ?? '', total = view.totalCharacters ?? 0, start = view.outputStart ?? 0, sequence = view.sequence ?? 0;
  if (!Number.isSafeInteger(total) || !Number.isSafeInteger(start) || !Number.isSafeInteger(sequence) || start < 0 || total < start || total - start !== output.length || output.length > 32768) throw new Error('Invalid terminal output bounds');
  if (cursor.identity === identity && sequence <= cursor.sequence) return { cursor, reset: false, text: '', partial: false };
  const reset = identity !== cursor.identity || cursor.total < start || cursor.total > total;
  return { cursor: { identity, total, sequence }, reset, text: reset ? output : output.slice(cursor.total - start), partial: reset && start > 0 };
}
