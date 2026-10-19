import { useEffect, useState } from 'react';
import { useViewChoice } from '../../useViewChoice';
import { useKnowledge } from '../../knowledgeState';
import { sameRoot } from '../../knowledge';
import { toolsTranslate } from '../../i18n/toolsStrings';
import { toolsTabDomains, useToolsState } from '../../toolsState';
import { normalizeToolsView, toolsViews, type ToolsView } from '../../toolsViews';
import type { KnowledgeDraft } from '../../protocol';
import { ConversationTools, type ActiveRule } from './ConversationTools';
import { KnowledgeView } from './KnowledgeView';
import { McpView } from './McpView';
import { PermissionRules } from './PermissionRules';
import { RulesView } from './RulesView';
import { SkillsView } from './SkillsView';
import { ViewChips } from './ViewChips';
import { WorkflowsView } from './WorkflowsView';
import './tools.css';

export { normalizeToolsView, toolsViews, type ToolsView };
export interface ToolsTabProps {
  locale: string; workspaceKey: string; sessionId?: string; connected: boolean; busy: boolean; active: boolean;
  newSessionPending?: boolean; inert?: boolean; rules?: ActiveRule[]; requestedView?: ToolsView;
  onViewChange?: (view: ToolsView) => void; onUseSkill: (name: string) => void;
  /** The editor's "Add to Knowledge ▸ New" (knowledge/draft; each `seq` once): the Knowledge view opens its new-knowledge form with these lines. */
  knowledgeDraft?: { payload: KnowledgeDraft; seq: number } | null;
  /** Puts the text into the chat input and opens Chat. */
  onInsertText?: (text: string) => void;
  /** Sends a started workflow's task to the chat (the same continuation Work › Progress uses). */
  onStartWorkflow?: (text: string) => void;
}
export function ToolsTab({ locale, workspaceKey, sessionId, connected, busy, active, newSessionPending = false, inert = false,
  rules, requestedView, onViewChange, onUseSkill, knowledgeDraft, onInsertText, onStartWorkflow }: ToolsTabProps) {
  const knowledge = useKnowledge(); const [permissionsCount, setPermissionsCount] = useState(0);
  const [controllerView, setControllerView] = useState<ToolsView>(() => normalizeToolsView(requestedView));
  const t = toolsTranslate(locale);
  const controller = useToolsState({ locale, workspaceKey, sessionId, connected, busy, active, view: controllerView, disabled: inert || newSessionPending, domains: toolsTabDomains });
  const counts: Partial<Record<ToolsView, number>> = { permissions: permissionsCount, conversation: controller.tools.tools.length, skills: controller.skills.skills.length, mcp: controller.mcp.servers.length, rules: controller.rules.rules.length, workflows: controller.workflows.workflows.length, knowledge: knowledge.items.filter(item => !item.candidate).length };
  const [view, change] = useViewChoice({ views: toolsViews, requested: normalizeToolsView(requestedView), active: active && !inert, scope: workspaceKey + ':' + (sessionId ?? ''), count: item => counts[item], onChange: next => { setControllerView(next); onViewChange?.(next); } });
  useEffect(() => { setControllerView(view); }, [view]);
  // The editor's "Add to Knowledge" chose Knowledge: it stays open even while its list is still empty (a draft waiting when the page starts).
  useEffect(() => {
    if (knowledgeDraft && (!workspaceKey || sameRoot(knowledgeDraft.payload.root, workspaceKey))) change('knowledge');
  // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [knowledgeDraft?.seq]);
  const disabled = inert || !active || !connected || newSessionPending;
  return <div className="tools-tab" data-testid="tools-tab" inert={inert || undefined}>
    <ViewChips prefix="tools" label={t('tools.nav')} active={view} onChange={change} inert={inert}
      views={toolsViews.map(id => ({ id, label: t(`tools.${id}`), badge: counts[id] }))} />
    <div className="tools-view-stack" key={`${workspaceKey}:${sessionId ?? ''}`}>{toolsViews.map(item => <section key={item} id={`tools-${item}`} role="tabpanel" aria-labelledby={`tools-tab-${item}`} hidden={view !== item} inert={inert || view !== item || undefined} className="tools-view">
      {item === 'conversation' && <ConversationTools controller={controller} locale={locale} disabled={disabled} busy={busy} />}
      {item === 'permissions' && <PermissionRules onCount={setPermissionsCount} locale={locale} root={workspaceKey} sessionId={sessionId} active={active && !inert && view === item} countActive={active && !inert} tools={controller.tools} />}
      {item === 'skills' && <SkillsView controller={controller} locale={locale} disabled={disabled} busy={busy} onUseSkill={onUseSkill} />}
      {item === 'mcp' && <McpView controller={controller} locale={locale} disabled={disabled} busy={busy} />}
      {item === 'rules' && <RulesView active={rules} controller={controller} locale={locale} disabled={disabled} busy={busy} />}
      {item === 'workflows' && <WorkflowsView controller={controller} locale={locale} disabled={disabled} busy={busy} workspaceKey={workspaceKey} sessionId={sessionId} onStart={onStartWorkflow} />}
      {item === 'knowledge' && <KnowledgeView locale={locale} workspaceKey={workspaceKey} active={active && !inert && view === item} disabled={disabled} draft={knowledgeDraft} onInsertText={onInsertText} />}
    </section>)}</div>
  </div>;
}
