/** The Tools tab's sub-views, in chip order. */
export const toolsViews = ['conversation', 'permissions', 'skills', 'mcp', 'rules', 'workflows', 'knowledge'] as const;
export type ToolsView = typeof toolsViews[number];

/** Ids earlier builds used for sub-views that were merged or split; a host message, a remembered view or a harness may still name them. */
const mergedViews = new Map<string, ToolsView>([['index', 'conversation'], ['extensions', 'skills'], ['instructions', 'rules']]);

/** A requested or remembered view that is not a chip (an old id maps to its view now; a removed one, e.g. the old settings guide) opens the first view. */
export function normalizeToolsView(view: string | undefined): ToolsView {
  const id = view ?? '';
  return (toolsViews as readonly string[]).includes(id) ? id as ToolsView : mergedViews.get(id) ?? 'conversation';
}

/** Where a Tools view id opens: a Tools chip, or the Agents tab's Automation view (the project hook definitions moved there with the other event handling). */
export type ToolsTarget = { tab: 'tools'; view: ToolsView } | { tab: 'agents'; view: 'hooks' };

/** The destination of a current, merged or moved (`hooks`) Tools view id; undefined for any other id, which a caller ignores. */
export function toolsTarget(view: string | undefined): ToolsTarget | undefined {
  const id = view ?? '';
  if (id === 'hooks') return { tab: 'agents', view: 'hooks' };
  return (toolsViews as readonly string[]).includes(id) || mergedViews.has(id) ? { tab: 'tools', view: normalizeToolsView(id) } : undefined;
}
