import type { ExtensionAPI } from '@oh-my-pi/pi-coding-agent';
import fs from 'node:fs';
import path from 'node:path';
import { directPath, resolveRole, type RoleInfo } from '../lib/agent-roles.ts';
import { isPompHost, readVsState } from '../lib/vs-state.ts';

// Teams (docs/architecture.md §8.2): /pomp-team and membersOnly.

interface TeamData {
  version: 1;
  id: string;
  title: string;
  goal: string;
  members: string[];
  mode?: 'lead' | 'parallel' | 'pipeline';
  instructions?: string;
  membersOnly?: boolean;
  /** Members work in isolated copies (docs/architecture.md §8.11). */
  isolation?: boolean;
}

/** Whether a `task` call asks for isolated work (top level or any item), and whether every item does. */
export function isolationOf(input: unknown): { any: boolean; all: boolean } {
  const value = (input && typeof input === 'object' ? input : {}) as { isolated?: unknown; tasks?: unknown };
  const top = value.isolated === true;
  const tasks = Array.isArray(value.tasks) ? value.tasks : [];
  const flags = tasks.map((task) => top || (!!task && typeof task === 'object' && (task as { isolated?: unknown }).isolated === true));
  return { any: top || flags.some(Boolean), all: flags.length > 0 && flags.every(Boolean) };
}

const ID_REGEX = /^[a-z0-9-]{2,40}$/;
const INSTANCE_REGEX = /^[A-Za-z0-9][A-Za-z0-9._:-]{0,127}$/;
const MAX_TEAM_BYTES = 32 * 1024;

export function approvalTasks(input: unknown): { agent: string; task: string }[] | undefined {
  if (!input || typeof input !== 'object') return undefined;
  const value = input as { agent?: unknown; task?: unknown; tasks?: unknown };
  const items = Array.isArray(value.tasks) ? value.tasks : [value];
  if (items.length === 0) return undefined;
  const result: { agent: string; task: string }[] = [];
  for (const item of items) {
    if (!item || typeof item !== 'object') return undefined;
    const task = item as { agent?: unknown; task?: unknown };
    // An omitted agent is resolved by engine settings unavailable here: do not guess a display assignment.
    if (typeof task.agent !== 'string' || !task.agent.trim() || task.agent.length > 128 || /[\x00-\x1f]/.test(task.agent) ||
        typeof task.task !== 'string' || task.task.length > 8000 || task.task.includes('\0')) return undefined;
    result.push({ agent: task.agent.trim(), task: task.task });
  }
  return result;
}

export default function teams(pi: ExtensionAPI): void {
  if (!isPompHost()) return;

  // Which agents the main agent may spawn until its turn ends: a team's members (membersOnly), the one addressed agent, or none for a
  // direct message to an existing agent.
  let activeMembersOnly: { teamId: string; label: string; allowed: Set<string> } | null = null;
  // The isolation team whose turn is running (docs/architecture.md §8.11): only its member tasks run isolated, and all of them do.
  let activeIsolation: string | null = null;

  pi.on('tool_call', (event, ctx) => {
    try {
      if (event.toolName !== 'task' || ctx?.agent?.kind === 'sub') return undefined;
      const tasks = approvalTasks(event.input);
      const sessionId = ctx.sessionManager?.getSessionId();
      if (tasks && typeof sessionId === 'string' && sessionId && typeof event.toolCallId === 'string' && event.toolCallId.length <= 200)
        ctx.ui.notify('pomp-task-details:' + Buffer.from(JSON.stringify({ sessionId, toolCallId: event.toolCallId, tasks })).toString('base64'), 'info');
      const isolation = isolationOf((event as { input?: unknown }).input);
      if (!activeIsolation && isolation.any)
        return { block: true, reason: 'pomp: isolated task runs are only for teams with isolated work trees. Run the task without `isolated`.' };
      if (activeIsolation && !isolation.all)
        return { block: true, reason: `pomp: team '${activeIsolation}' uses isolated work trees. Call the task tool again with \`isolated: true\` on every task.` };
      return undefined;
    } catch {
      // omp treats a throwing handler as a block for every tool: this guard only ever blocks on purpose.
      return undefined;
    }
  });

  pi.on('tool_result', (event, ctx) => {
    if (event.toolName !== 'task' || ctx?.agent?.kind === 'sub') return;
    const sessionId = ctx.sessionManager?.getSessionId();
    if (typeof sessionId === 'string' && typeof event.toolCallId === 'string')
      ctx.ui.notify('pomp-task-details:' + Buffer.from(JSON.stringify({ sessionId, toolCallId: event.toolCallId, ended: true })).toString('base64'), 'info');
  });

  pi.on('before_subagent_spawn', (event) => {
    if (!activeMembersOnly) return;
    if (!activeMembersOnly.allowed.has(event.agent)) {
      const allowed = Array.from(activeMembersOnly.allowed);
      return {
        block: true,
        reason: `Agent '${event.agent}' is blocked by ${activeMembersOnly.label} (allowed: ${allowed.length ? allowed.join(', ') : 'none'}).`,
      };
    }
  });

  pi.on('agent_end', (_event, ctx) => {
    if (ctx.agent?.kind !== 'sub') {
      activeMembersOnly = null;
      activeIsolation = null;
    }
  });

  // A new assignment for exactly one agent (docs/architecture.md §8.3): the main agent only relays.
  pi.registerCommand('pomp-agent', {
    description: 'Give the message to one agent as a new assignment (the main agent only relays it)',
    handler: async (args, ctx) => {
      const match = /^\s*([a-z0-9-]{2,40})\s+([\s\S]+)$/.exec(args);
      if (!match) {
        ctx.ui.notify('Agent name or message is missing.', 'warning');
        return;
      }
      const name = match[1]!;
      const info = resolveRole(name, readVsState()?.solutionRoot ?? ctx.cwd);
      if (!info) {
        ctx.ui.notify(`Agent not found: ${name}`, 'warning');
        return;
      }
      const brief = [
        `<!-- pomp-agent:${name} -->`,
        `The user addressed this message directly to the agent \`${name}\` (${info.title}: ${info.job}).`,
        `Call the \`task\` tool once with agent \`${name}\` and the user's message below as the assignment, verbatim; add only the context it needs to be self-contained. Do not do the work yourself and do not use other agents.`,
        `When it finishes, show its result to the user, attributed to ${info.title}.`,
        `<!-- /pomp-agent -->`,
      ].join('\n\n');
      activeMembersOnly = { teamId: name, label: `the direct message to agent '${name}'`, allowed: new Set([name]) };
      pi.sendUserMessage(`${brief}\n\n${match[2]}`);
    },
  });

  // A direct message to an agent this conversation already ran (docs/architecture.md §8.3): delivered over omp's IRC, so that agent
  // continues its own context (a parked agent is revived by omp).
  pi.registerCommand('pomp-dm', {
    description: 'Send the message to an existing agent of this conversation and show its reply',
    handler: async (args, ctx) => {
      const match = /^\s*([A-Za-z0-9][A-Za-z0-9._:-]{0,127})\s+([\s\S]+)$/.exec(args);
      if (!match) {
        ctx.ui.notify('Agent ID or message is missing.', 'warning');
        return;
      }
      const id = match[1]!;
      const brief = [
        `<!-- pomp-dm:${id} -->`,
        `The user sent a direct message to the existing agent \`${id}\`. Deliver the user's message below to it verbatim with \`write agent://${id}\`. Do not call the \`wait\` tool for it: its reply arrives by itself as a message (\`wait\` only covers jobs you started). Show that reply to the user when it arrives.`,
        `Do not answer the message yourself and do not start new agents. If \`write\` reports that the agent does not exist, say so.`,
        `<!-- /pomp-dm -->`,
      ].join('\n\n');
      activeMembersOnly = { teamId: id, label: `the direct message to agent '${id}'`, allowed: new Set() };
      pi.sendUserMessage(`${brief}\n\n${match[2]}`);
    },
  });

  pi.registerCommand('pomp-team', {
    description: 'Execute a team dispatch with coordinated instructions',
    handler: async (args, ctx) => {
      const match = /^\s*([a-z0-9-]{2,40})(?:\s+([\s\S]*))?$/.exec(args);
      if (!match) {
        ctx.ui.notify('Team ID or message is missing.', 'warning');
        return;
      }

      const teamId = match[1]!;
      let userMessage = match[2] ?? '';
      // POMP always puts one marker first: `--new` (start the members) or `--continue role=id,...`; the rest is the user's text as typed.
      let continuation: RegExpExecArray | null = null;
      const fresh = /^--new(?:\s+([\s\S]*))?$/.exec(userMessage);
      if (fresh) userMessage = fresh[1] ?? '';
      else {
        continuation = /^--continue(?:\s+(\S+))?(?:\s+([\s\S]*))?$/.exec(userMessage);
        if (continuation) userMessage = continuation[2] ?? '';
      }

      const solutionRoot = readVsState()?.solutionRoot ?? ctx.cwd;
      const projectTeamFile = path.join(solutionRoot, '.pomp', 'teams', `${teamId}.json`);
      const userAppData = process.env.APPDATA || (process.env.USERPROFILE ? path.join(process.env.USERPROFILE, 'AppData', 'Roaming') : '');
      const userTeamFile = userAppData ? path.join(userAppData, 'Pomp', 'teams', `${teamId}.json`) : undefined;

      let teamFilePath: string | null = null;
      if (fs.existsSync(projectTeamFile)) {
        teamFilePath = projectTeamFile;
      } else if (userTeamFile && fs.existsSync(userTeamFile)) {
        teamFilePath = userTeamFile;
      }

      if (!teamFilePath) {
        ctx.ui.notify(`Team not found: ${teamId}`, 'warning');
        return;
      }

      if (!directPath(teamFilePath)) {
        ctx.ui.notify(`Team file traverses a symlink: ${teamId}`, 'warning');
        return;
      }

      let team: TeamData;
      try {
        const stat = fs.statSync(teamFilePath);
        if (stat.size > MAX_TEAM_BYTES) {
          ctx.ui.notify(`Team file is too large: ${teamId}`, 'warning');
          return;
        }
        const text = fs.readFileSync(teamFilePath, 'utf8');
        team = JSON.parse(text) as TeamData;
      } catch (err) {
        ctx.ui.notify(`Failed to read team file: ${String(err)}`, 'warning');
        return;
      }

      if (team === null || typeof team !== 'object' || team.version !== 1 || typeof team.id !== 'string' || team.id !== teamId) {
        ctx.ui.notify(`Invalid team format or ID in ${teamId}`, 'warning');
        return;
      }
      if (team.id === 'main' || team.id === 'sub') {
        ctx.ui.notify(`Reserved team ID '${team.id}' cannot be run`, 'warning');
        return;
      }
      if (typeof team.title !== 'string' || !team.title || team.title.length > 40) {
        ctx.ui.notify(`Team title is invalid`, 'warning');
        return;
      }
      // The goal is optional, as in POMP's team editor (AgentRoster: at most 400 characters); an empty one adds no line to the brief.
      if (team.goal !== undefined && (typeof team.goal !== 'string' || team.goal.length > 400)) {
        ctx.ui.notify(`Team goal is invalid`, 'warning');
        return;
      }
      if (team.instructions !== undefined && (typeof team.instructions !== 'string' || team.instructions.length > 8000)) {
        ctx.ui.notify(`Team instructions are too long`, 'warning');
        return;
      }
      if (!Array.isArray(team.members) || team.members.length < 2) {
        ctx.ui.notify(`Team member count is invalid`, 'warning');
        return;
      }
      const uniqueMembers = new Set(team.members);
      if (uniqueMembers.size !== team.members.length) {
        ctx.ui.notify(`Team has duplicate members`, 'warning');
        return;
      }
      if (team.isolation !== undefined && typeof team.isolation !== 'boolean') {
        ctx.ui.notify(`Team isolation value is invalid`, 'warning');
        return;
      }
      const isolated = team.isolation === true;

      const membersInfo: RoleInfo[] = [];
      for (const member of team.members) {
        if (typeof member !== 'string' || !ID_REGEX.test(member)) {
          ctx.ui.notify(`Invalid member name: ${member}`, 'warning');
          return;
        }
        const info = resolveRole(member, solutionRoot);
        if (!info) {
          ctx.ui.notify(`Team member role not found: ${member}`, 'warning');
          return;
        }
        membersInfo.push(info);
      }

      const continuingMembers = new Map<string, string>();
      for (const entry of (continuation?.[1] ?? '').split(',')) {
        const parts = entry.split('=');
        if (parts.length !== 2 || !ID_REGEX.test(parts[0]!) || !INSTANCE_REGEX.test(parts[1]!) || !uniqueMembers.has(parts[0]!)) continue;
        continuingMembers.set(parts[0]!, parts[1]!);
        if (continuingMembers.size >= 6) break;
      }

      // `--continue` whose entries were all dropped (malformed, non-members) is an ordinary new run. Isolated members cannot be
      // continued (omp keeps no live copy), so an isolation team always starts new ones.
      const continuing = !isolated && continuation !== null && continuingMembers.size > 0;
      const mode = team.mode === 'parallel' || team.mode === 'pipeline' ? team.mode : 'lead';
      const modeDescriptions: Record<string, string> = continuing ? {
        lead: 'Lead decision mode: As the team lead, coordinate, break down the goal, and call upon members as appropriate, reusing the continuing members listed below.',
        parallel: 'Parallel mode: Dispatch work to all members concurrently, reusing the continuing members listed below.',
        pipeline: 'Pipeline mode: Execute members sequentially in order, passing the results of each stage to the next member and reusing the continuing members listed below.',
      } : {
        lead: 'Lead decision mode: As the team lead, coordinate, break down the goal, and call upon members using the task tool as appropriate.',
        parallel: 'Parallel mode: Dispatch tasks to all members concurrently in a single task batch using the task tool.',
        pipeline: 'Pipeline mode: Execute members sequentially in order, passing the results of each stage to the next member using the task tool.',
      };
      const modeDesc = modeDescriptions[mode] || modeDescriptions.lead;

      const briefLines = [
        `<!-- pomp-team:${team.id} -->`,
        `# Team Coordination: ${team.title} (ID: ${team.id})`,
        team.goal?.trim() ? `Goal: ${team.goal}` : '',
        `Execution Mode: ${mode} - ${modeDesc}`,
        team.instructions ? `Team Instructions: ${team.instructions}` : '',
        `## Team Members`,
        ...membersInfo.map((m) => `- ${m.name} (${m.title}): ${m.job} [Model: ${m.model}]`),
        ...(continuing ? [
          `## Continuing members`,
          ...Array.from(continuingMembers, ([role, id]) => `- ${role} -> agent://${id}`),
          `- This team already worked in this conversation. Do not start new agents for the listed members.`,
          `- Send each continuing member its next part with \`write agent://<id>\` using its listed id. Do not call the \`wait\` tool for agents: their replies arrive by themselves as messages.`,
          `- Use the \`task\` tool only for members not listed or not reachable.`,
        ] : []),
        `## Coordination Protocol`,
        continuing ? `- As coordinator, dispatch work according to the continuing-member rules above, with the member agent names listed above.` : `- As coordinator, dispatch work to members using the \`task\` tool with the member agent names listed above.`,
        `- Team members coordinate via \`write agent://<id>\` (or \`write agent://all\`).`,
        ...(isolated ? [
          `- Isolated work trees: set \`isolated: true\` on every member task. Each member works in its own copy of the repository; its changes come back to the user for review and are not applied to this checkout, so do not tell the user they are applied. Members cannot be continued afterwards.`,
        ] : [
          `- Before editing shared files, members must coordinate via \`write agent://<id>\` to prevent conflicts.`,
        ]),
        `- Members do not build, test or run checks unless their assignment says so: when a member's work needs proof (its job is building, testing or reviewing, or it works in an isolated copy), name in its assignment the check to run in its own copy and ask it to report the result.`,
        `- When finished, provide a final report structured per member, listing: what was done, files changed, and open issues.`,
        `<!-- /pomp-team -->`,
      ].filter(Boolean);

      const brief = briefLines.join('\n\n');

      if (team.membersOnly !== false) {
        const allowed = new Set(team.members);
        allowed.add('scout');
        activeMembersOnly = { teamId: team.id, label: `team '${team.id}' (membersOnly)`, allowed };
      }
      activeIsolation = isolated ? team.id : null;

      const promptText = userMessage ? `${brief}\n\n${userMessage}` : brief;
      pi.sendUserMessage(promptText);
    },
  });
}
