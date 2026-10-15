import { useEffect, useId, useState } from 'react';
import { useLocale } from '../../i18n';
import { toolsTranslate, type ToolsTranslate } from '../../i18n/toolsStrings';
import type { ConversationTool } from '../../toolsProtocol';
import type { ToolsController } from '../../toolsState';
import type { ComputerReplyPayload, ComputerStatePayload } from '../../protocol';
import { Icon } from '../Icon';
import { ViewSection } from '../ViewSection';
import { Cell, DataTable, Row, Switch } from '../ui';
import { ComputerUseSection } from './ComputerUseSection';
import './conversation.css';

export type { ActiveRule } from '../../protocol';
// Only an MCP server's own text is shown; the built-in and VS tool descriptions are instructions for the model, not labels.
function meaningfulDescription(tool: ConversationTool, groupLabel: string): string | undefined {
  if (tool.group !== 'mcp') return undefined;
  const description = tool.description?.trim();
  if (!description) return undefined;
  const normalize = (value: string) => value.toLowerCase().replace(/[^\p{L}\p{N}]+/gu, ' ').trim();
  const name = normalize(tool.name);
  const groups = [tool.group, groupLabel, tool.server ?? ''].map(normalize).filter(Boolean);
  const repeated = [name, ...groups, ...groups.flatMap(group => [`${name} ${group}`, `${group} ${name}`])];
  return repeated.includes(normalize(description)) ? undefined : description;
}
/** One group of the tool table: its row (expand, name, n/m, a switch for the whole group) and, expanded, one row per tool with its own switch. */
function ToolGroup({ id, label, entries, selected, disabled, t, onChange }: {
  id: string; label: string; entries: ConversationTool[]; selected: Set<string>; disabled: boolean; t: ToolsTranslate;
  onChange: (names: string[], checked: boolean) => void;
}) {
  const bodyId = useId();
  const hasChanges = entries.some(tool => selected.has(tool.name) !== tool.active);
  const [expanded, setExpanded] = useState(hasChanges);
  // Selecting a collapsed group reveals its pending changes; an explicit collapse remains possible.
  useEffect(() => { if (hasChanges) setExpanded(true); }, [hasChanges]);
  const count = entries.filter(tool => selected.has(tool.name)).length;
  const all = count === entries.length;
  return <div role="rowgroup" className="conv-tool-group">
    <Row className={`conv-group-row${count > 0 && !all ? ' conv-partial' : ''}`} testId={`tool-group-row-${id}`}>
      <Cell><button type="button" className="conv-group-toggle" data-testid={`tool-expand-${id}`} aria-expanded={expanded} aria-controls={bodyId}
        title={t(expanded ? 'section.collapse' : 'section.expand')} onClick={() => setExpanded(value => !value)}>
        <Icon name={expanded ? 'chevronDown' : 'chevron'} size={12} /><strong>{label}</strong>
      </button></Cell>
      <Cell className="conv-count"><span data-testid={`tool-count-${id}`}>{count}/{entries.length}</span></Cell>
      <Cell className="conv-switch"><Switch label={label} showLabel={false} checked={all} disabled={disabled} testId={`tool-group-${id}`}
        onChange={() => onChange(entries.map(tool => tool.name), !all)} /></Cell>
    </Row>
    <div id={bodyId} role="rowgroup" data-testid={`tool-list-${id}`} hidden={!expanded}>
      {entries.map(tool => {
        const description = meaningfulDescription(tool, label);
        const on = selected.has(tool.name);
        return <Row key={tool.name} className={`conv-tool-row${on !== tool.active ? ' conv-changed' : ''}`}>
          <Cell title={description ?? tool.name}><span className="ui-mono conv-tool-name">{tool.name}</span>{description && <span className="ui-sub">{description}</span>}</Cell>
          <Cell />
          <Cell className="conv-switch"><Switch label={tool.name} showLabel={false} checked={on} disabled={disabled} testId={`tool-${tool.name}`} onChange={next => onChange([tool.name], next)} /></Cell>
        </Row>;
      })}
    </div>
  </div>;
}
export function ConversationTools({ controller, locale, connected, disabled, busy, computer, computerReply, computerRequest }: {
  controller: ToolsController; locale: string; connected: boolean; disabled: boolean; busy: boolean;
  computer?: ComputerStatePayload | null; computerReply?: ComputerReplyPayload | null; computerRequest?: { revision: number };
}) {
  const { tools } = controller; const t = toolsTranslate(locale); const ko = useLocale() === 'ko';
  const selected = controller.selectedTools;
  const changes = tools.tools.filter(tool => selected.has(tool.name) !== tool.active).length;
  // Reset restores the defaults, so it stays available after a custom set was applied.
  const resettable = changes > 0 || tools.tools.some(tool => tool.active !== tool.defaultActive);
  const locked = disabled || busy || tools.pending || !tools.available || controller.pending('tools');
  const groups = new Map<string, ConversationTool[]>();
  for (const tool of [...tools.tools].sort((a, b) => a.name.localeCompare(b.name))) {
    const key = tool.group === 'mcp' ? `mcp:${tool.server ?? ''}` : tool.group;
    groups.set(key, [...(groups.get(key) ?? []), tool]);
  }
  const order = ['files', 'edit', 'exec', 'web', 'agents', 'host', 'mcp', 'other'];
  const ordered = [...groups].sort(([a], [b]) => order.indexOf(a.split(':')[0]) - order.indexOf(b.split(':')[0]) || a.localeCompare(b));
  const active = tools.tools.filter(tool => tool.active).length;
  function toggle(names: string[], checked: boolean) {
    if (locked) return;
    controller.toggleTools(names, checked);
  }
  return <div className="tools-scroll-body tools-conversation-body">
    <ComputerUseSection computer={computer} reply={computerReply} request={computerRequest} connected={connected} blocked={disabled || busy} />
    <ViewSection sectionKey="tools-selection" className="tools-section conv-tools" title={t('tools.title')} bodyClassName="ui-flush"
      status={<span data-testid="tools-active-count">{active}/{tools.tools.length}</span>}>
      {busy && <p className="ui-note ui-muted" role="status">{t('tools.busy')}</p>}
      {!tools.available && <p className="ui-note ui-muted" role="status">{t('tools.unavailable')}</p>}
      {tools.message && <p className="ui-note ui-muted" role="status">{tools.message}</p>}
      <DataTable columns="minmax(0, 1fr) 48px 40px" label={t('tools.title')} testId="tools-table" empty={!tools.tools.length} emptyRows={3}
        head={[ko ? '도구' : 'Tool', ko ? '켜짐' : 'On', '']}>
        {ordered.map(([key, entries]) => <ToolGroup key={key} id={key} label={entries[0].group === 'mcp' ? t('tools.group.mcp', { server: entries[0].server ?? '' }) : t(`tools.group.${entries[0].group}`)}
          entries={entries} selected={selected} disabled={locked} t={t} onChange={toggle} />)}
      </DataTable>
      {controller.errors('tools').map((error, index) => <p className="ui-note" role="alert" key={index}>{error}</p>)}
      <div className="ui-card-foot conv-foot" data-testid="tools-action-bar">
        <span className="ui-grow ui-muted" data-testid="tools-change-count" role="status" aria-live="polite">{t('tools.changes', { count: changes })}</span>
        <button type="button" className="btn small subtle" data-testid="tools-reset" disabled={locked || !resettable} onClick={() => { if (!locked && resettable) void controller.command('tools', { action: 'reset' }); }}>{ko ? '기본값' : 'Defaults'}</button>
        <button type="button" className="btn small primary" data-testid="tools-apply" disabled={locked || changes === 0} onClick={() => { if (!locked && changes) void controller.command('tools', { action: 'set', active: [...selected].sort() }); }}>{t('tools.apply')}</button>
      </div>
    </ViewSection>
  </div>;
}
