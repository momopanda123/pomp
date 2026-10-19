import type { ExtensionAPI } from '@oh-my-pi/pi-coding-agent';
import { partnerRole } from '../lib/conversation-partner.ts';
import { isPompHost, pick, readVsState } from '../lib/vs-state.ts';

/** Local RPC commands only: selecting tools never starts inference or changes subagent definitions. */
export default function tools(pi: ExtensionAPI): void {
 if (!isPompHost()) return;
 let defaults: string[] | undefined;
 let registered = new Set<string>();
 let forced = new Set<string>();
 let selected: Set<string> | undefined;
 const names = (value: string[]): string[] => [...new Set(value)].filter(n => typeof n === 'string' && n.length > 0 && n.length <= 256 && !/[\x00-\x1f\x7f]/.test(n)).sort();
 const supported = (): boolean => typeof pi.getAllTools === 'function' && typeof pi.getActiveTools === 'function' && typeof pi.setActiveTools === 'function';
 const capture = (): void => {
  const all = new Set(pi.getAllTools().map(t => t.name)); const active = names(pi.getActiveTools());
  // Host RPC tools may register after session_start. Only newly registered active tools join the startup defaults.
  defaults = defaults === undefined ? active : names([...defaults,...active.filter(n => all.has(n) && !registered.has(n))]);
  registered = all;
 };
 // This conversation's default tools: an agent conversation (docs/architecture.md §8.8) has only the tools its role lists, like that agent
 // run as a subagent; any other conversation has the startup defaults.
 const conversationDefaults = (sessionId: unknown): string[] => {
  const role = partnerRole(readVsState()?.solutionRoot ?? process.env.POMP_SOLUTION_ROOT, sessionId);
  const known = new Set(pi.getAllTools().map(t => t.name));
  return names((role?.tools ?? defaults ?? []).filter(n => known.has(n)));
 };
 const snapshot = (ctx: any, id: string, error?: string): void => {
  const all = supported() ? pi.getAllTools().filter(t => names([t.name]).length === 1).sort((a,b) => a.name.localeCompare(b.name)).map(t => ({ name: t.name, description: String(t.description ?? '').replace(/[\x00-\x1f\x7f]/g,' ').slice(0,200), source: t.sourceInfo?.source })) : [];
  ctx.ui.notify('pomp-tools-result:' + Buffer.from(JSON.stringify({ id, sessionId: ctx.sessionManager?.getSessionId(), all, active: supported() ? names(pi.getActiveTools().filter(n => !forced.has(n))) : [], defaults: supported() ? conversationDefaults(ctx.sessionManager?.getSessionId()) : [], available: supported(), ...(error ? { error } : {}) })).toString('base64'), 'info');
 };
 const start: any = async (_event: unknown,ctx: any) => {
  if (ctx.agent?.kind === 'sub' || !supported()) return;
  capture(); const active = conversationDefaults(ctx.sessionManager?.getSessionId()); selected = new Set(active); forced.clear();
  if (active.length !== pi.getActiveTools().length || names(pi.getActiveTools()).some((n, i) => n !== active[i])) await pi.setActiveTools(active);
  snapshot(ctx, '');
 };
 pi.on('tool_call', event => {
  if (selected?.has('write') === false && event.toolName === 'write') {
   const target = (event.input as { path?: unknown })?.path;
   // xd:// addresses the engine's tool transport, not disk files. Disabled ordinary writes stay blocked.
   if (typeof target !== 'string' || !target.startsWith('xd://')) return { block: true, reason: pick('파일 쓰기 꺼짐', 'File writing is off') };
  }
  return undefined;
 });
 pi.on('session_start', start);
 pi.on('session_switch', start);
 pi.registerCommand('pomp-tools', {
  description: 'List, select or reset the main conversation tool set without inference',
  handler: async (args,ctx) => {
   const request = /\s+--request\s+([a-f0-9]{32})\s*$/.exec(args);
   const id = request?.[1] ?? '';
   const input = (request ? args.slice(0,request.index) : args).trim();
   try {
    if (ctx.agent?.kind === 'sub') throw new Error('Tool selection is only available in the main conversation.');
    if (!supported()) throw new Error('Tool selection is unavailable.');
    capture();
    if (input !== 'list') {
     if (!ctx.isIdle()) throw new Error('Wait for the current reply to finish before selecting tools.');
     const known = new Set(pi.getAllTools().map(t => t.name));
     let active: string[];
     if (input === 'reset') active = conversationDefaults(ctx.sessionManager?.getSessionId());
     else if (input.startsWith('set ')) {
      const value: unknown = JSON.parse(input.slice(4));
      if (!Array.isArray(value) || value.some(n => typeof n !== 'string' || !known.has(n))) throw new Error('Choose registered tool names only.');
      active = names(value);
      if (active.length !== new Set(value).size) throw new Error('Invalid tool name.');
      // A bounded UI catalog must not silently disable active tools the user cannot see.
      const visible = new Set(pi.getAllTools().filter(t => names([t.name]).length === 1).sort((a,b) => a.name.localeCompare(b.name)).map(t => t.name));
      active = names([...active,...pi.getActiveTools().filter(n => known.has(n) && !visible.has(n))]);
     } else throw new Error('Usage: /pomp-tools list|set <json-array>|reset');
     await pi.setActiveTools(active);
     selected = new Set(active);
     // The engine pins write as the transport for deferrable/plan tools. A forced transport is not an edit toggle.
     forced = new Set(pi.getActiveTools().filter(n => n === 'write' && !active.includes(n)));
    }
    snapshot(ctx,id);
   } catch (error) { snapshot(ctx,id,String(error instanceof Error ? error.message : error).slice(0,500)); }
  }
 });
}
