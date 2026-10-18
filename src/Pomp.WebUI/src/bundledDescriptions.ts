import type { Translate } from './i18n';
import type { ToolsTranslate, ToolsStringKey } from './i18n/toolsStrings';
import type { Role } from './protocol';
import type { SkillInfo } from './toolsProtocol';

const roleNames = new Set(["task","sonic","scout","reviewer","security-reviewer"]);
const skillNames = new Set(["brainstorming","writing-plans","test-driven-development","systematic-debugging","receiving-code-review","finishing-a-branch","improve-architecture","codebase-design","domain-modeling","grilling","scope-review","retro","dotnet-patterns","csharp-testing","cpp-testing","security-review","e2e-testing","database-migrations","docker-patterns","api-design","error-handling","hexagonal-architecture","codebase-onboarding","deployment-patterns","accessibility","planning","anatomy","drawio"]);

/** A role's own title as its file (or the engine, for a built-in role) names it: the page never renames a role. */
export function roleTitle(role: Role | undefined, _t: Translate, fallback = ''): string {
  return role?.title || role?.name || fallback;
}

export function roleDescription(role: Role, t: Translate): string {
  return role.scope === 'bundled' ? t(('bundled.role.' + (roleNames.has(role.name) ? role.name : 'other')) as ToolsStringKey) : role.description;
}
export function roleJob(role: Role, t: Translate): string {
  return role.scope === 'bundled' ? roleDescription(role, t) : role.job ?? '';
}
export function skillDescription(skill: SkillInfo, t: ToolsTranslate): string {
  return skill.source === 'pomp' || skill.source === 'omp' ? t(('bundled.skill.' + (skillNames.has(skill.name) ? skill.name : 'other')) as ToolsStringKey) : skill.description;
}
