import type { ContextBreakdown } from './protocol';

/** The sources the context bar splits into, in their fixed order (never reordered or recolored when one is zero). */
export type ContextSegmentKey = 'prompt' | 'tools' | 'mcp' | 'context' | 'skills' | 'messages';

export const CONTEXT_SEGMENT_KEYS: readonly ContextSegmentKey[] = ['prompt', 'tools', 'mcp', 'context', 'skills', 'messages'];

/** Every non-zero segment (and the basket) is painted at least this wide. */
export const MIN_SEGMENT_PX = 1;
/** The bar's own background shows between neighbouring segments. */
export const SEGMENT_GAP_PX = 2;
/** Below this filled width the segments sit flush against each other (no gaps), so the pixels go to the colors. */
export const GAP_MIN_FILLED_PX = 24;

const clean = (value: number): number => (Number.isFinite(value) && value > 0 ? value : 0);

/** Tokens per source. "Tools" is the engine's tool total without the MCP share (omp and VS tools). */
export function breakdownTokens(breakdown: ContextBreakdown): Record<ContextSegmentKey, number> {
  const mcp = Math.min(clean(breakdown.mcpTools), clean(breakdown.systemTools));
  return {
    prompt: clean(breakdown.systemPrompt),
    tools: clean(breakdown.systemTools) - mcp,
    mcp,
    context: clean(breakdown.systemContext),
    skills: clean(breakdown.skills),
    messages: clean(breakdown.messages),
  };
}

export interface PaintedSegment { key: ContextSegmentKey | 'basket'; width: number }

/** The gap between neighbouring segments for a filled part of this width. */
export const gapFor = (filledPx: number): number => (filledPx >= GAP_MIN_FILLED_PX ? SEGMENT_GAP_PX : 0);

/**
 * Pixel widths of the painted segments. `filledPx` is the filled part of the bar (its share of the window); every non-zero segment
 * gets at least 1 px and the rest is split in proportion to the tokens, after 2 px gaps while the filled part is at least 24 px wide
 * (narrower, the segments touch). A non-zero segment is only left out when the filled part has fewer pixels than segments; then the
 * largest ones are painted first. The basket follows the filled part, measured against the whole window, at least 1 px, and capped so
 * that the bar never exceeds 100 %.
 */
export function layoutSegments(tokens: Record<ContextSegmentKey, number>, filledPx: number, basketPx: number, barPx: number): PaintedSegment[] {
  let active = CONTEXT_SEGMENT_KEYS.filter((key) => tokens[key] > 0);
  const painted: PaintedSegment[] = [];
  if (active.length > 0 && filledPx >= MIN_SEGMENT_PX) {
    const gap = gapFor(filledPx);
    // Fewer pixels than segments: keep the largest ones (in their order).
    const fit = Math.max(1, Math.floor(filledPx));
    if (active.length > fit) {
      const keep = new Set([...active].sort((a, b) => tokens[b] - tokens[a]).slice(0, fit));
      active = active.filter((key) => keep.has(key));
    }
    const room = filledPx - gap * (active.length - 1);
    // Water-filling: a segment whose share would be under 1 px is pinned at 1 px and the others share what is left.
    const widths = new Map<ContextSegmentKey, number>();
    let free = [...active];
    let left = room;
    for (;;) {
      const total = free.reduce((sum, key) => sum + tokens[key], 0);
      const small = free.filter((key) => (tokens[key] / total) * left < MIN_SEGMENT_PX);
      if (small.length === 0) {
        free.forEach((key) => widths.set(key, (tokens[key] / total) * left));
        break;
      }
      small.forEach((key) => widths.set(key, MIN_SEGMENT_PX));
      left -= small.length * MIN_SEGMENT_PX;
      free = free.filter((key) => !small.includes(key));
      if (free.length === 0) break;
    }
    active.forEach((key) => painted.push({ key, width: widths.get(key)! }));
  }
  // The filled part keeps its width even when nothing in it could be painted (the view then draws one fill).
  const used = Math.max(0, filledPx);
  if (basketPx > 0) {
    const basket = Math.min(Math.max(basketPx, MIN_SEGMENT_PX), barPx - used - (used > 0 ? gapFor(used) : 0));
    if (basket >= MIN_SEGMENT_PX) painted.push({ key: 'basket', width: basket });
  }
  return painted;
}
