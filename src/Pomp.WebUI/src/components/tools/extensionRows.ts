import { toolsTranslate, type ToolsStringKey } from '../../i18n/toolsStrings';
import type { SkillInfo } from '../../toolsProtocol';

/** The catalog's categories in display order; any other category comes after them in the order it first appears. */
export const CATEGORIES = ['process', 'planning', 'testing', 'debugging', 'review', 'architecture', 'dotnet', 'security', 'data', 'ops', 'design', 'dev', 'docs'] as const;
export type Category = typeof CATEGORIES[number];
export const isCategory = (value: string): value is Category => (CATEGORIES as readonly string[]).includes(value);
/** The label of a category; one the page does not know shows as the host sent it. */
export const categoryLabel = (t: ReturnType<typeof toolsTranslate>, category: string) => isCategory(category) ? t(`ext.cat.${category}` as ToolsStringKey) : category;

/** Whether a skill is on now (absent = on, as the host sends it). */
export const skillOn = (skill: SkillInfo) => skill.enabled !== false;
/** A built-in skill (the only rows with a default) whose state differs from the default POMP ships it with. */
export const differsFromDefault = (skill: SkillInfo) => typeof skill.defaultEnabled === 'boolean' && skillOn(skill) !== skill.defaultEnabled;
/** Rows the user wrote can be edited and deleted; every other row (built into POMP, engine, VS extension) is read only. */
export const writableSkill = (skill: SkillInfo) => skill.source === 'project' || skill.source === 'user';

export type SkillFilter = 'all' | 'on' | 'off';
const includes = (query: string, ...texts: Array<string | undefined>) => { const needle = query.trim().toLowerCase(); return !needle || texts.some(text => (text ?? '').toLowerCase().includes(needle)); };
/** Mine (project, user) first, then every other skill; by name inside. A switch never moves a row. */
const rank = (skill: SkillInfo) => writableSkill(skill) ? 0 : 1;

/** The skills list: narrowed by the on/off filter and the search (name, description, category), in a stable order. */
export function skillRows(skills: readonly SkillInfo[], filter: SkillFilter, query: string, describe: (skill: SkillInfo) => string = skill => skill.description, category: (name: string) => string = name => name): SkillInfo[] {
  return skills.filter(skill => (filter === 'all' || skillOn(skill) === (filter === 'on')) && includes(query, skill.name, describe(skill), skill.category && category(skill.category)))
    .toSorted((left, right) => rank(left) - rank(right) || left.name.localeCompare(right.name, 'en', { sensitivity: 'base', numeric: true }) || left.source.localeCompare(right.source));
}
