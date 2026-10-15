import { roleTitle } from '../bundledDescriptions';
import { useContext, useEffect, useRef, useState } from 'react';
import { TeamsContext, effectiveRoles, effectiveTeams } from '../teamsCache';
import { type AgentHookRule, type AgentsStatePayload } from '../protocol';
import { useT, type Translate } from '../i18n';
import { useHostCommand } from '../useHostCommand';
import { useRefreshOnShow } from '../useRefreshOnShow';
import { Field } from './ui';

export const RULE_NAME_LIMIT = 60;
const MESSAGE_LIMIT = 2000;
const SLUG_REGEX = /^[a-z0-9-]{2,40}$/;
const CONTROL_REGEX = /[\0-\b\v\f\x0e-\x1f\x7f]/;

export type RuleEvent = AgentHookRule['event'];
type SourceKind = AgentHookRule['sourceKind'];
type TargetKind = NonNullable<AgentHookRule['targetKind']>;
/** Rule fields the page sends on save; the host stamps id and revision. */
export type AgentHookRuleInput = Omit<AgentHookRule, 'id' | 'revision'>;

const GLOB_LIMIT = 200;
/** Mirrors ProjectHook.Pattern: a relative glob with only *, ** and ? wildcards. */
const globValid = (glob: string) => glob.length <= GLOB_LIMIT && !/[\0-\x1f\x7f:[\]{}(),]/.test(glob) && !/^[\\/]/.test(glob) && !glob.replace(/\\/g, '/').split('/').includes('..');

export const RULE_EVENTS: RuleEvent[] = ['agent.completed', 'agent.failed', 'team.completed', 'team.failed', 'feedback.waiting', 'ide.saved', 'ide.build.succeeded', 'ide.build.failed'];
const isIdeEvent = (event: RuleEvent) => event.startsWith('ide.');

export interface RuleFire { at: string; rule: string; event: string; source: string; outcome: string }

/** The event rules of this conversation (`agentHooks/*`): rules, recent firings and the auto-forward switch. `onSaved` runs when a save is answered without error. */
export function useAgentHookRules({ workspaceKey, sessionId, connected, inert, onSaved }: { workspaceKey?: string; sessionId?: string; connected: boolean; inert: boolean; onSaved: () => void }) {
  const [rules, setRules] = useState<AgentHookRule[]>([]);
  const [log, setLog] = useState<RuleFire[]>([]);
  const [autoForward, setAutoForward] = useState(false);
  const [forwards, setForwards] = useState(0);
  const [maxForwards, setMaxForwards] = useState(10);
  // False until this workspace and conversation's rules were read: the Agents tab shows no count and keeps the view meanwhile.
  const [loaded, setLoaded] = useState(false);
  const saved = useRef(onSaved); saved.current = onSaved;
  const { pending, error, run } = useHostCommand<'agentHooks/command', 'agentHooks/state', 'save'>('agentHooks/command', 'agentHooks/state', { root: workspaceKey, sessionId }, connected && !inert, (payload, answered) => {
    if (!Array.isArray(payload.rules) || !Array.isArray(payload.log)) return;
    setRules(payload.rules); setLog(payload.log); setLoaded(true);
    setAutoForward(payload.autoForward === true);
    setForwards(typeof payload.forwards === 'number' ? payload.forwards : 0);
    setMaxForwards(typeof payload.maxForwards === 'number' ? payload.maxForwards : 10);
    // Error replies keep the form text (revision conflict, invalid combination); success closes the form the reply answers.
    if (answered?.tag === 'save' && !payload.error) saved.current();
  });
  // The auto-forward switch is per conversation: a new session starts switched off.
  useEffect(() => { setRules([]); setLog([]); setAutoForward(false); setForwards(0); setLoaded(false); }, [workspaceKey, sessionId]);
  useRefreshOnShow(connected && !inert && !!workspaceKey && !!sessionId, () => { void run({ action: 'refresh' }); });
  // Another conversation or workspace while shown: the host does not push its rules, so they are read again.
  const scopeKey = `${workspaceKey ?? ''}|${sessionId ?? ''}`; const lastScope = useRef(scopeKey);
  useEffect(() => {
    if (lastScope.current === scopeKey) return;
    lastScope.current = scopeKey;
    if (connected && !inert && workspaceKey && sessionId) void run({ action: 'refresh' });
  }, [scopeKey]);
  return { rules, log, autoForward, forwards, maxForwards, pending, error, run, loaded };
}

/** The rule part of the automation form. */
export interface RuleDraft { event: RuleEvent; sourceKind: SourceKind; sourceId: string; glob: string; targetKind: TargetKind; targetId: string; message: string }
export const emptyRuleDraft = (): RuleDraft => ({ event: 'agent.completed', sourceKind: 'any', sourceId: '', glob: '', targetKind: 'main', targetId: '', message: '' });
export const ruleDraftOf = (rule: AgentHookRule): RuleDraft => ({ event: rule.event, sourceKind: rule.sourceKind, sourceId: rule.sourceId ?? '', glob: rule.glob ?? '', targetKind: rule.targetKind ?? 'main', targetId: rule.targetId ?? '', message: rule.message });

/**
 * Mirrors AgentHookStore.Validate so invalid rules are refused before they reach the host: source must fit the event, notify carries no
 * target, forward needs a target and a message, and a team result cannot be forwarded back to the same team. Null when valid.
 */
export function validateRule(name: string, action: AgentHookRule['action'], draft: RuleDraft, t: Translate): string | null {
  const { event, sourceKind, sourceId, glob, targetKind, targetId, message } = draft;
  const teamEvent = event.startsWith('team.');
  if (!name.trim() || name.trim().length > RULE_NAME_LIMIT || /[\r\n]/.test(name)) return t('agentHooks.nameInvalid');
  if (isIdeEvent(event) && sourceKind !== 'any') return t('agentHooks.invalidCombination');
  if (event === 'ide.saved' && !globValid(glob.trim())) return t('agentHooks.globInvalid');
  if (sourceKind === 'any') { if (sourceId) return t('agentHooks.invalidCombination'); }
  else if (sourceKind === 'agent' && !teamEvent && event !== 'feedback.waiting') { if (!SLUG_REGEX.test(sourceId)) return t('agentHooks.sourceRequired'); }
  else if (sourceKind === 'team' && teamEvent) { if (!SLUG_REGEX.test(sourceId)) return t('agentHooks.sourceRequired'); }
  else return t('agentHooks.invalidCombination');
  if (action === 'forward') {
    if (targetKind === 'main') { if (targetId) return t('agentHooks.invalidCombination'); }
    else if (!SLUG_REGEX.test(targetId)) return t('agentHooks.targetRequired');
    if (!message.trim()) return t('agentHooks.messageRequired');
    if (teamEvent && targetKind === 'team' && targetId === sourceId) return t('agentHooks.sameTeam');
  }
  if (message.length > MESSAGE_LIMIT || CONTROL_REGEX.test(message)) return t('agentHooks.messageInvalid');
  return null;
}
export function ruleInput(name: string, enabled: boolean, action: AgentHookRule['action'], draft: RuleDraft): AgentHookRuleInput {
  return {
    name: name.trim(), enabled, event: draft.event, sourceKind: draft.sourceKind, sourceId: draft.sourceKind === 'any' ? undefined : draft.sourceId,
    glob: draft.event === 'ide.saved' ? (draft.glob.trim() || '**') : undefined, action,
    targetKind: action === 'notify' ? undefined : draft.targetKind, targetId: action === 'notify' || draft.targetKind === 'main' ? undefined : draft.targetId,
    message: draft.message.trim(),
  };
}

/** Role/team titles for rule sources and targets. */
export function useRuleNames(agents: AgentsStatePayload | null) {
  const t = useT(); const knownTeams = useContext(TeamsContext);
  const roles = effectiveRoles(agents?.roles ?? []); const teams = effectiveTeams(agents?.teams ?? []);
  const label = (kind: string, id: string) => kind === 'team'
    ? teams.find(team => team.id.toLowerCase() === id.toLowerCase())?.title || knownTeams.find(team => team.id.toLowerCase() === id.toLowerCase())?.title || t('agentHooks.target.team')
    : kind === 'agent' ? roleTitle(roles.find(role => role.name === id), t, t('agentHooks.target.agent')) : t('agentHooks.target.main');
  return { roles, teams, label };
}

/** Signal, source, target and message fields of an event rule; the name, action and on/off live in the shared form. */
export function RuleFields({ draft, action, onChange, agents }: { draft: RuleDraft; action: AgentHookRule['action']; onChange: (patch: Partial<RuleDraft>) => void; agents: AgentsStatePayload | null }) {
  const t = useT(); const names = useRuleNames(agents);
  const ideEvent = isIdeEvent(draft.event);
  const sourceIds = draft.sourceKind === 'agent' ? names.roles.map(role => role.name) : draft.sourceKind === 'team' ? names.teams.map(team => team.id) : [];
  const targetIds = draft.targetKind === 'agent' ? names.roles.map(role => role.name) : draft.targetKind === 'team' ? names.teams.map(team => team.id) : [];
  return <>
    <div className="ui-field-row">
      <Field label={t('agentHooks.eventLabel')}>{id => <select id={id} data-testid="hooks-event" value={draft.event} onChange={event => {
        const next = event.target.value as RuleEvent; onChange({ event: next, sourceId: '', ...(isIdeEvent(next) ? { sourceKind: 'any' as const } : {}) });
      }}>{RULE_EVENTS.map(value => <option key={value} value={value}>{t(`agentHooks.event.${value}`)}</option>)}</select>}</Field>
      <Field label={t('agentHooks.sourceLabel')}>{id => <select id={id} data-testid="hooks-source-kind" value={draft.sourceKind} disabled={ideEvent} onChange={event => onChange({ sourceKind: event.target.value as SourceKind, sourceId: '' })}>
        {(['any', 'agent', 'team'] as const).map(kind => <option key={kind} value={kind}>{t(`agentHooks.source.${kind}`)}</option>)}</select>}</Field>
    </div>
    {draft.event === 'ide.saved' && <Field label={t('agentHooks.globLabel')}>{id => <input id={id} data-testid="hooks-glob" className="ui-mono" value={draft.glob} maxLength={GLOB_LIMIT} spellCheck={false} onChange={event => onChange({ glob: event.target.value })} />}</Field>}
    {draft.sourceKind !== 'any' && <Field label={t('agentHooks.sourceIdLabel')}>{id => <select id={id} data-testid="hooks-source-id" value={draft.sourceId} onChange={event => onChange({ sourceId: event.target.value })}>
      <option value="">{t('agentHooks.choose')}</option>{sourceIds.map(value => <option key={value} value={value}>{names.label(draft.sourceKind, value)}</option>)}</select>}</Field>}
    {action === 'forward' && <>
      <div className="ui-field-row">
        <Field label={t('agentHooks.targetLabel')}>{id => <select id={id} data-testid="hooks-target-kind" value={draft.targetKind} onChange={event => onChange({ targetKind: event.target.value as TargetKind, targetId: '' })}>
          {(['main', 'agent', 'team'] as const).map(kind => <option key={kind} value={kind}>{t(`agentHooks.target.${kind}`)}</option>)}</select>}</Field>
        {draft.targetKind !== 'main' && <Field label={t('agentHooks.targetIdLabel')}>{id => <select id={id} data-testid="hooks-target-id" value={draft.targetId} onChange={event => onChange({ targetId: event.target.value })}>
          <option value="">{t('agentHooks.choose')}</option>{targetIds.map(value => <option key={value} value={value}>{names.label(draft.targetKind, value)}</option>)}</select>}</Field>}
      </div>
      <Field label={t('agentHooks.messageLabel')}>{id => <textarea id={id} data-testid="hooks-message" value={draft.message} rows={4} maxLength={MESSAGE_LIMIT} onChange={event => onChange({ message: event.target.value })} />}</Field>
    </>}
  </>;
}
