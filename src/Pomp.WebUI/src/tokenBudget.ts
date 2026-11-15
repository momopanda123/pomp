/** Optional token budget for a user-confirmed goal run (docs/ui-protocol.md): a whole number in this range. */
export const MIN_TOKEN_BUDGET = 10_000;
export const MAX_TOKEN_BUDGET = 50_000_000;

/** The budget a field holds: null for none (empty), 'invalid' for anything but a whole number in range (thousands commas are fine). */
export function parseTokenBudget(value: string): number | null | 'invalid' {
  const text = value.trim();
  if (!text) return null;
  if (!/^\d[\d,]*$/u.test(text)) return 'invalid';
  const number = Number(text.replace(/,/gu, ''));
  return Number.isSafeInteger(number) && number >= MIN_TOKEN_BUDGET && number <= MAX_TOKEN_BUDGET ? number : 'invalid';
}
