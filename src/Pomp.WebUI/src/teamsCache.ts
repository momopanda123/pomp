import { createContext, useContext } from 'react';
import { roleTitle } from './bundledDescriptions';
import { useT } from './i18n';
import type { AgentsStatePayload, MainRole, Role, SendTarget, Team } from './protocol';

/** Context keeps already-rendered, memoized transcript cards in sync with refreshed team titles. */
export const TeamsContext = createContext<readonly Team[]>([]);
export const RolesContext = createContext<readonly Role[]>([]);
/** POMP's own role (the chat agent): its picture wherever the page shows POMP as an agent. */
export const MainRoleContext = createContext<MainRole | undefined>(undefined);
/** The name pictures and reports use for the chat agent; no role file can take it. Its pictures are the user's, like a built-in role's. */
export const MAIN_ROLE = { name: 'main', title: 'POMP', scope: 'bundled' } as const;

export function useRoleTitle(name: string): string {
  return roleTitle(effectiveRoles(useContext(RolesContext)).find(role => role.name === name), useT(), name);
}

export const targetKey = (target?: SendTarget): string => target ? JSON.stringify([target.kind, target.id]) : '';

export function targetPayload(target?: SendTarget): { team?: string; agent?: string; agentInstance?: string; main?: true } {
  return target?.kind === 'main' ? { main: true } : target?.kind === 'team' ? { team: target.id } : target?.kind === 'agent' ? { agent: target.id } : target?.kind === 'instance' ? { agentInstance: target.id } : {};
}

export function useTeamTitle(id: string): string {
  return effectiveTeams(useContext(TeamsContext)).find(team => team.id.toLowerCase() === id.toLowerCase())?.title || id;
}

/** Host metadata is authoritative. Older hosts skip malformed roles before project > user > bundled discovery. */
export function effectiveRoles(roles: readonly Role[]): Role[] {
  const rank = { user: 0, project: 1, extension: 2, bundled: 2 };
  return roles.filter(role => !role.error && (typeof role.shadowed === 'boolean' ? !role.shadowed
    // A definition the host marks as hidden is never used, so it hides nothing either.
    : !roles.some(other => !other.error && other.shadowed !== true && other.name === role.name && rank[other.scope] < rank[role.scope])));
}

/** Unlike roles, even an invalid project team hides a user team. Explicit host flags override the fallback. */
export function effectiveTeams(teams: readonly Team[]): Team[] {
  return teams.filter(team => typeof team.shadowed === 'boolean' ? !team.shadowed
    : team.scope === 'project' || !teams.some(other => other.scope === 'project' && other.shadowed !== true && other.id.toLowerCase() === team.id.toLowerCase()));
}

export function isRunnableTeam(team: Team, agents: AgentsStatePayload | null | undefined): boolean {
  if (team.shadowed || !effectiveTeams(agents?.teams ?? []).includes(team) || team.error || team.missing?.length || team.members.length < 2 || new Set(team.members).size !== team.members.length) return false;
  const usable = new Set(effectiveRoles(agents?.roles ?? []).filter(role => !role.error).map(role => role.name));
  return team.members.every(name => usable.has(name));
}
