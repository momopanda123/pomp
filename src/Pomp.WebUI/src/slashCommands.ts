import type { MessageKey, Translate } from './i18n';
import type { CommandInfo } from './protocol';

/** The page's own commands: it handles them itself and never sends the typed text on (`goal` opens the goal card; `steer` and `btw` post their own messages). */
export const BUILTIN_COMMANDS = ['new', 'sessions', 'rename', 'model', 'thinking', 'effort', 'permissions', 'compact', 'accounts', 'login', 'usage', 'work', 'agents', 'computer', 'agent', 'team', 'goal', 'steer', 'btw', 'help'] as const;
export type BuiltinCommand = typeof BUILTIN_COMMANDS[number];
export const isBuiltinCommand = (name: string): name is BuiltinCommand => (BUILTIN_COMMANDS as readonly string[]).includes(name);
/** POMP's plumbing commands: the host sends them programmatically, so the command list never offers them (typed text still goes to the host as written). */
export const isHiddenCommand = (name: string): boolean => name.startsWith('pomp-');

/**
 * Commands the bundled agent ships outside its builtins (by name and source) that the palette leaves out. POMP's /goal and the onboarding
 * skill do the jobs of `autoresearch` and `init`; `annotate` (omp 18.12.1) shows its annotation views only in omp's terminal UI (over RPC
 * the engine draws nothing), so it would do nothing here. A typed name still goes to the engine.
 */
const ENGINE_SHIPPED: ReadonlyMap<string, string> = new Map([['autoresearch', 'extension'], ['init', 'file'], ['annotate', 'custom']]);
const isEngineShipped = (command: CommandInfo): boolean => command.source !== undefined && ENGINE_SHIPPED.get(command.name) === command.source;

/**
 * POMP's engine-side commands under their plain names. The palette lists the plain name (when the engine lists the registered one);
 * the host sends the registered name (`pomp-…`, which keeps them clear of the engine's own commands) and keeps the typed text in the
 * conversation. Typing the registered name still works, it is just not listed (docs/ui-protocol.md, slash commands).
 */
export const POMP_COMMANDS: Readonly<Record<string, { engine: string; label: MessageKey }>> = {
  workflow: { engine: 'pomp-workflow', label: 'slash.workflow' },
  plan: { engine: 'pomp-plan', label: 'slash.plan' },
  map: { engine: 'pomp-map', label: 'slash.map' },
  review: { engine: 'pomp-review', label: 'slash.review' },
  max: { engine: 'pomp-max', label: 'slash.max' },
  rules: { engine: 'pomp-rules', label: 'slash.rules' },
  resources: { engine: 'pomp-resources', label: 'slash.resources' },
  guard: { engine: 'pomp-guard', label: 'slash.guard' },
  'vs-status': { engine: 'pomp-context', label: 'slash.vsStatus' },
  skill: { engine: 'pomp-skill', label: 'slash.skill' },
  command: { engine: 'pomp-command', label: 'slash.command' },
  recipe: { engine: 'pomp-recipe', label: 'slash.recipe' },
};
const POMP_ENGINE_NAMES = new Set(Object.values(POMP_COMMANDS).map(command => command.engine));
/** Alternate typed spellings run the canonical built-in; the palette lists each action only once. */
export const COMMAND_ALIASES: Readonly<Record<string, BuiltinCommand>> = { 'pomp-compact': 'compact', stear: 'steer' };

/** One palette entry: `group` is set for the skills, which are listed after the commands under their own heading. */
export interface PaletteCommand extends CommandInfo { group?: 'skills' }

/** An engine skill command (`skill:name`): the plain name the palette lists for it, or undefined for a name that is not safe to type as a command. */
export function plainSkillName(command: CommandInfo): string | undefined {
  if (command.source !== undefined && command.source !== 'skill') return undefined;
  if (!command.name.startsWith('skill:')) return undefined;
  const name = command.name.slice('skill:'.length);
  return /^[A-Za-z0-9][A-Za-z0-9._:-]{0,127}$/u.test(name) ? name : undefined;
}

/**
 * The engine's builtins that stay in the palette, with the label POMP wrote for each. Every other builtin is not listed: POMP's commands
 * and the Tools tab do those jobs, and the output of the rest is the engine's own. A typed name still runs; `effort` is the page's own.
 */
export const LISTED_ENGINE_COMMANDS: ReadonlyMap<string, MessageKey> = new Map<string, MessageKey>([
  ['context', 'slash.context'], ['fast', 'slash.fast'], ['tools', 'slash.tools'], ['mcp', 'slash.mcp'],
]);

/**
 * POMP's built-ins and its own engine commands with short labels, then the engine builtins that stay (see LISTED_ENGINE_COMMANDS) and the
 * commands somebody added (a file, an extension, an MCP prompt) by name only, without the plumbing ones; then the enabled skills under
 * their plain names in a group of their own. One entry per function: a command of the user's own with a name POMP's command takes
 * keeps it (POMP's is then not listed under the plain name). `skillsOff` are skills the Tools tab switched off.
 */
export function commandCatalog(host: readonly CommandInfo[], t: Translate, skillsOff: readonly string[] = []): PaletteCommand[] {
  const listed = new Set(host.map(command => command.name));
  const own: PaletteCommand[] = BUILTIN_COMMANDS.map(name => ({ name, description: t(`slash.${name}`) }));
  // A command somebody else defined under POMP's plain name (not an engine builtin) is theirs.
  const taken = (plain: string): boolean => host.some(command => command.name === plain && command.source !== 'builtin');
  const pomp: PaletteCommand[] = Object.entries(POMP_COMMANDS)
    .filter(([plain, command]) => listed.has(command.engine) && !isBuiltinCommand(plain) && !taken(plain))
    .map(([plain, command]) => ({ name: plain, description: t(command.label) }));
  const shown = new Set([...own, ...pomp].map(command => command.name));
  // A builtin of the engine is listed only when it is on the list (under POMP's label); a command somebody added is theirs and keeps its place.
  const labelOf = (command: CommandInfo): MessageKey | undefined => command.source === 'builtin' ? LISTED_ENGINE_COMMANDS.get(command.name) : undefined;
  const engine: PaletteCommand[] = host
    .filter(command => !shown.has(command.name) && !isHiddenCommand(command.name) && !isEngineShipped(command) && !command.name.startsWith('pomp-') && !POMP_ENGINE_NAMES.has(command.name) && !Object.hasOwn(COMMAND_ALIASES, command.name) && plainSkillName(command) === undefined
      && (command.source !== 'builtin' || labelOf(command) !== undefined))
    .map(command => {
      const label = labelOf(command);
      return { name: command.name, ...(label ? { description: t(label) } : {}), ...(command.subcommands ? { subcommands: command.subcommands.map(sub => ({ name: sub.name })) } : {}) };
    });
  // The host maps a plain name to the skill only when no engine command has it, a builtin that is not listed here included.
  const taken2 = new Set([...shown, ...host.map(command => command.name)]);
  const off = new Set(skillsOff);
  const skills: PaletteCommand[] = [];
  for (const command of host) {
    const name = plainSkillName(command);
    // A switched-off skill is not listed (the engine drops it from its own list too). A real command with the plain name wins it;
    // the skill then stays reachable under its engine name.
    if (name === undefined || isHiddenCommand(name) || off.has(name)) continue;
    const entry = taken2.has(name) ? command.name : name;
    if (!skills.some(skill => skill.name === entry)) skills.push({ name: entry, group: 'skills' });
  }
  skills.sort((a, b) => (a.name < b.name ? -1 : a.name > b.name ? 1 : 0));
  return [...own, ...pomp, ...engine, ...skills];
}

/** POMP's commands that hand their text to the model (engine names): the context basket's checked references go with them. */
const REFERENCE_COMMANDS: ReadonlySet<string> = new Set(['pomp-workflow', 'pomp-plan', 'pomp-map', 'pomp-review', 'pomp-skill', 'pomp-command', 'pomp-recipe']);

/**
 * True when a typed slash command takes the context basket's checked references (mirror of the host's PompSession.TakesReferences): one
 * of POMP's task commands, a skill or a prompt file, after the same name mapping the host does. Any other command goes without them and
 * leaves the basket as it is.
 */
export function slashTakesReferences(text: string, commands: readonly CommandInfo[]): boolean {
  const match = /^\s*\/([^\s/]+)(?=\s|$)/u.exec(text);
  if (!match) return false;
  const name = match[1]!;
  const named = commands.filter(command => command.name === name);
  const own = named.some(command => command.source !== 'builtin');
  const pomp = Object.hasOwn(POMP_COMMANDS, name) ? POMP_COMMANDS[name] : undefined;
  const engine = pomp ? (own ? name : pomp.engine) : named.length === 0 && commands.some(command => command.name === `skill:${name}`) ? `skill:${name}` : name;
  if (REFERENCE_COMMANDS.has(engine) || engine.startsWith('skill:')) return true;
  return commands.some(command => command.name === engine && command.source === 'file');
}

export interface SlashMatch { name: string; description?: string; value: string; subcommands?: CommandInfo['subcommands']; group?: 'skills' }

/** Only a collapsed caret in the leading command token (or its first subcommand) activates completion. */
export function slashMatches(text: string, caret: number, selectionEnd: number, commands: readonly PaletteCommand[]): SlashMatch[] | null {
  if (!text.startsWith('/') || caret !== selectionEnd || caret < 1) return null;
  const prefix = text.slice(1, caret);
  if (/\r|\n/u.test(prefix)) return null;
  const space = prefix.indexOf(' ');
  if (space < 0) {
    if (/\s/u.test(prefix)) return null;
    return commands.filter(command => command.name.toLocaleLowerCase().startsWith(prefix.toLocaleLowerCase()))
      .map(command => ({ ...command, value: `/${command.name} ` }));
  }
  const command = commands.find(item => item.name === prefix.slice(0, space));
  const query = prefix.slice(space + 1);
  if (!command?.subcommands?.length || /\s/u.test(query)) return null;
  return command.subcommands.filter(item => item.name.toLocaleLowerCase().startsWith(query.toLocaleLowerCase()))
    .map(item => ({ ...item, value: `/${command.name} ${item.name} ` }));
}
