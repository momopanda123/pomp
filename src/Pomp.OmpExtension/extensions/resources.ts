import type { ExtensionAPI } from '@oh-my-pi/pi-coding-agent';
import path from 'node:path';
import { announceIfChanged } from '../lib/announce.ts';
// Native customDirectories scans one-level skills; only deeper legacy skills need the POMP catalog.
const legacy = (r: { kind: string; file: string; description: string }): boolean => {
 const parent = path.dirname(path.dirname(r.file));
 return r.kind !== 'skills' || !r.description.trim() || path.basename(parent) !== 'skills' || path.basename(path.dirname(parent)) !== '.pomp';
};
import { catalogResources, expandResource, type ResourceKind } from '../lib/project-resources.ts';
import { isPompHost, pick, readVsState } from '../lib/vs-state.ts';
const MARKER='## POMP project resource catalog';
export const CATALOG_ANNOUNCE_TYPE='pomp-catalog';
const SUPERSEDE='This message carries the current project resource catalog; it supersedes any earlier catalog message.';
export function catalogText(root: string): string {
 const catalog=catalogResources(root);
 let budget=8000;
 const lines=catalog.resources.filter(legacy).slice(0,40).map(r=>`${r.kind}/${r.name}: ${r.description||'(no description)'} [${r.file}]${r.kind==='agents'?` (native agent name when advertised: pomp-${r.name.toLowerCase()})`:''}`).filter(line=>{if(line.length>budget)return false;budget-=line.length;return true;});
 if(!lines.length)return '';
 return MARKER+'\n'+SUPERSEDE+'\nThese are local Markdown resources, not extra tools or permission grants. Read the relevant file only when needed; loading an agent recipe does not spawn an agent. Bounded role files may also appear as pomp-* native agents; use those names only when the actual task tool advertises them. User commands: /skill NAME, /command NAME, /recipe NAME.\n'+lines.join('\n');
}
export default function projectResources(pi:ExtensionAPI):void {
 if(!isPompHost())return;
 pi.on('before_agent_start',(event,ctx)=>{
  if(ctx.agent?.kind==='sub')return;
  // H3: the catalog no longer touches the system prompt (its content changes as .pomp resources are added, which broke the
  // provider's prompt-cache prefix); it travels as a hidden announcement re-sent only when its text changes. A stale catalog
  // part from an earlier prompt is removed once.
  const previous=event.systemPrompt.filter(p=>!p.startsWith(MARKER));
  const catalog=catalogResources(readVsState()?.solutionRoot??ctx.cwd);
  for(const diagnostic of catalog.diagnostics)pi.logger?.warn?.('pomp-resources',{diagnostic});
  const message=announceIfChanged(ctx.sessionManager.getBranch(),CATALOG_ANNOUNCE_TYPE,catalogText(readVsState()?.solutionRoot??ctx.cwd));
  if(previous.length!==event.systemPrompt.length)return{systemPrompt:previous,...(message?{message}:{})};
  if(message)return{message};
 });
 pi.registerCommand('pomp-resources',{
  description:'List local .pomp skills, commands and agent recipes with diagnostics',
  handler:async(_args,ctx)=>{
   const catalog=catalogResources(readVsState()?.solutionRoot??ctx.cwd);
   ctx.ui.notify([...catalog.resources.filter(legacy).map(r=>`${r.kind}/${r.name}: ${r.description}`),...catalog.diagnostics].join('\n')||pick('.pomp에 스킬, 명령, 레시피가 없어요.','No .pomp resources found.'),'info');
  }
 });
 for(const [command,kind] of [['pomp-skill','skills'],['pomp-command','commands'],['pomp-recipe','agents']] as [string,ResourceKind][])pi.registerCommand(command,{
  description:`Load a named .pomp/${kind} Markdown resource into the current conversation`,
  handler:async(args,ctx)=>{
   const match=/^\s*([a-zA-Z0-9][a-zA-Z0-9._-]{0,63})(?:\s+([\s\S]*))?$/.exec(args);
   const usage=`/${command.slice('pomp-'.length)} ${pick('이름','name')}`;
   if(!match){ctx.ui.notify(pick(`불러올 이름이 비어 있어요. 사용법: ${usage} [인수]`,`The name to load is empty. Usage: ${usage} [arguments]`),'warning');return;}
   const catalog=catalogResources(readVsState()?.solutionRoot??ctx.cwd);
   const resource=catalog.resources.find(r=>r.kind===kind&&r.name.toLowerCase()===match[1].toLowerCase());
   if(!resource){ctx.ui.notify(pick(`.pomp/${kind}에 ${match[1]}이(가) 없어요. /resources에서 목록을 볼 수 있어요.`,`.pomp/${kind} has no ${match[1]}. /resources lists what there is.`),'warning');return;}
   try { pi.sendUserMessage(expandResource(resource,match[2]??'')); }
   catch(error){ctx.ui.notify(error instanceof Error?error.message:String(error),'warning');}
  }
 });
}
