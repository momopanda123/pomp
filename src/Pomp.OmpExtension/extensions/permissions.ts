import type { ExtensionAPI } from '@oh-my-pi/pi-coding-agent';
import fs from 'node:fs';
import path from 'node:path';
import { createHash } from 'node:crypto';
import { isPompHost, offeredHostTools } from '../lib/vs-state.ts';
import { readBounded } from '../lib/project-rules.ts';
import { validatePolicyRelativePath } from '../lib/policy-path.ts';
interface Rule { tool: string; effect: 'allow' | 'deny'; paths?: string[]; commands?: string[] }
interface Policy { version: 1; default: 'deny'; requireSandbox?: boolean; requireReviewedEdits?: boolean; rules: Rule[] }
export interface PolicyDecision { configured: boolean; allowed: boolean; reason: string; fingerprint?: string }
export function parsePolicy(text: string): Policy {
 const policy=JSON.parse(text.replace(/^\uFEFF/,'')) as Policy;
 if(!policy||policy.version!==1||policy.default!=='deny'||!Array.isArray(policy.rules)||policy.rules.length>100)throw new Error('Policy needs version:1, default:"deny", and at most 100 rules');
 if(Object.keys(policy).some(key=>!['version','default','requireSandbox','requireReviewedEdits','rules'].includes(key)))throw new Error('Unknown permission policy field');
 if(policy.requireReviewedEdits!==undefined&&typeof policy.requireReviewedEdits!=='boolean')throw new Error('requireReviewedEdits must be boolean');
 if(policy.requireSandbox!==undefined&&typeof policy.requireSandbox!=='boolean')throw new Error('requireSandbox must be boolean');
 for(const rule of policy.rules){
  if(!rule||typeof rule.tool!=='string'||!/^(?:\*|[a-zA-Z0-9_.:-]{1,100})$/.test(rule.tool)||!['allow','deny'].includes(rule.effect))throw new Error('Invalid permission rule');
  if(Object.keys(rule).some(key=>!['tool','effect','paths','commands'].includes(key)))throw new Error('Unknown rule field');
  for(const key of ['paths','commands'] as const)if(rule[key]!==undefined&&(!Array.isArray(rule[key])||rule[key]!.length===0||rule[key]!.length>30||rule[key]!.some(value=>typeof value!=='string'||!value||value.length>4096||value.includes('\0'))))throw new Error('Invalid '+key+' constraint');
  for(const folder of rule.paths??[]){if(path.isAbsolute(folder)||/^[A-Za-z]:/.test(folder)||folder.startsWith('\\')||folder.split(/[\\/]/).includes('..'))throw new Error('Allowed paths must be workspace-relative files or directories');validatePolicyRelativePath(folder);}
 }
 return policy;
}
function targetPaths(input: unknown): string[] {
 if(!input||typeof input!=='object')return[];
 const args=input as Record<string,unknown>;const result:string[]=[];
 for(const key of ['path','file','file_path','filePath','source','destination','oldPath','newPath'])if(typeof args[key]==='string')result.push(args[key] as string);
 if(Array.isArray(args.files))for(const file of args.files.slice(0,101)){
  if(typeof file==='string')result.push(file);else if(file&&typeof file.path==='string')result.push(file.path);
 }
 if(Array.isArray(args.paths))for(const value of args.paths.slice(0,101))if(typeof value==='string')result.push(value);
 return result;
}
function contained(root:string,target:string):boolean {
 const rel=path.relative(root,target);return rel===''||(!path.isAbsolute(rel)&&rel!=='..'&&!rel.startsWith('..'+path.sep));
}
function pathsMatch(root:string,folders:string[],input:unknown,deny=false):boolean {
 const targets=targetPaths(input);if(targets.length===0||targets.length>100)return deny;
 const matches=targets.map(value=>{
  if(value.includes('\0')||value.length>4096)return deny;
  if(/^[A-Za-z]:(?![\\/])/.test(value))return deny;
  const full=path.resolve(root,value);if(!contained(root,full))return deny;
  try{const relative=path.relative(root,full);if(relative)validatePolicyRelativePath(relative);}catch{return deny;}
  // Reject existing symlink/junction components, including directories for files not yet created.
  for(let p=full;;p=path.dirname(p)){
   try{if(fs.lstatSync(p).isSymbolicLink())return deny;}catch(error){if((error as NodeJS.ErrnoException).code!=='ENOENT')return deny;}
   if(p===root)break;
  }
  return folders.some(folder=>contained(path.resolve(root,folder),full));
 });
 return deny?matches.some(Boolean):matches.every(Boolean);
}
/**
 * An isolated team member's own copy (docs/architecture.md §8.11): a subagent whose working directory is `<base>\<id>\m` (or a
 * folder in it) under the host's worktree base. Anything else that differs from the workspace stays blocked.
 */
export function isolatedCopy(cwd:string,base:string|undefined,kind:unknown):boolean {
 if(!base||kind!=='sub')return false;
 const rel=path.relative(path.resolve(base),path.resolve(cwd));
 if(!rel||path.isAbsolute(rel))return false;
 const parts=rel.split(path.sep);
 return parts.length>=2&&parts[0]!=='..'&&parts[0]!==''&&parts[1]==='m'&&!parts.includes('..');
}
/** `policyRoot` holds the policy file when it is not `root` itself (an isolated copy is judged by its workspace's file). */
export function evaluatePolicy(root:string,tool:string,input:unknown,policyRoot?:string):PolicyDecision {
 root=path.resolve(root);const policyDir=path.resolve(policyRoot??root);const file=path.join(policyDir,'.pomp','permissions.json');
 // No policy file: nothing extra to enforce, omp's and the host's normal approvals apply (installing POMP must be enough).
 try{fs.lstatSync(file);}catch(error){if((error as NodeJS.ErrnoException).code==='ENOENT')return{configured:false,allowed:true,reason:'No .pomp/permissions.json: the normal approvals apply.'};return{configured:true,allowed:false,reason:'Project permission policy could not be inspected'};}
 try{
  const text=readBounded(policyDir,file);const policy=parsePolicy(text);const fingerprint=createHash('sha256').update(text).digest('hex');
  if(policy.requireSandbox)return{configured:true,allowed:false,fingerprint,reason:'An OS-enforced sandbox was required, but this host does not provide one. Execution is blocked.'};
  if(policy.requireReviewedEdits && !offeredHostTools().has(tool)) {
   const action=input&&typeof input==='object'&&'action' in input?input.action:undefined;
   const readOnly=['read','grep','find','ls','todo'].includes(tool)||(tool==='lsp'&&typeof action==='string'&&['diagnostics','definition','references','symbols','hover'].includes(action));
   if(!readOnly)return{configured:true,allowed:false,fingerprint,reason:'Review-only project: raw writes, processes, child spawning and unknown extension tools are blocked. Use advertised host source tools and approved shadow review. This cooperative dispatch filter is not OS isolation.'};
  }
  const cwd=(input as {cwd?:unknown;workdir?:unknown})?.cwd??(input as {workdir?:unknown})?.workdir;
  if(cwd!==undefined&&(typeof cwd!=='string'||path.relative(root,path.resolve(root,cwd))!==''))return{configured:true,allowed:false,fingerprint,reason:'Project policy does not allow a tool-specific working-directory override.'};
  const matches=policy.rules.filter(rule=>{
   if(rule.tool!=='*'&&rule.tool!==tool)return false;
   if(rule.paths&&!pathsMatch(root,rule.paths,input,rule.effect==='deny'))return false;
   if(rule.commands){const command=(input as {command?:unknown})?.command;if(typeof command!=='string'||!rule.commands.includes(command))return false;}
   return true;
  });
  const allowed=!matches.some(rule=>rule.effect==='deny')&&matches.some(rule=>rule.effect==='allow');
  return {configured:true,allowed,fingerprint,reason:allowed?'Eligible under project filter; normal host/provider approvals still apply.':'Denied by .pomp/permissions.json (deny rules win; unmatched tools default to deny).'};
 }catch(error){return{configured:true,allowed:false,reason:'Invalid or unreadable project permission policy; check its JSON schema, file size and direct-path access.'};}
}
function audit(tool:string,decision:PolicyDecision,pi:ExtensionAPI):void {
 // Use the host's existing logger/retention instead of making an unbounded source/command transcript.
 try{pi.logger?.info?.('pomp-permission-audit',{time:new Date().toISOString(),tool:tool.slice(0,100),allowed:decision.allowed,policy:decision.fingerprint??'invalid',reason:decision.reason});}catch{/* Logging failure must never disable a denial. */}
}
export default function projectPermissions(pi:ExtensionAPI):void {
 if(!isPompHost())return;
 pi.on('tool_call',(event,ctx)=>{
  const hostRoot=process.env.POMP_SOLUTION_ROOT;
  // A member's isolated copy is not the workspace, but its own folder: never the copy's policy file, which the member could edit.
  const isolated=!!hostRoot&&isolatedCopy(ctx.cwd,process.env.POMP_WORKTREE_BASE,(ctx as {agent?:{kind?:unknown}}).agent?.kind);
  const decision=hostRoot && !isolated && path.relative(path.resolve(hostRoot),path.resolve(ctx.cwd))!==''
   ? {configured:true,allowed:false,reason:'The working directory differs from the connected POMP workspace.'}
   : evaluatePolicy(ctx.cwd,event.toolName,event.input,isolated?hostRoot:undefined);
  if(!decision.configured)return;
  audit(event.toolName,decision,pi);
  if(!decision.allowed)return{block:true,reason:decision.reason};
 });
 pi.registerCommand('pomp-permissions',{
  description:'Show project tool-policy status without changing permissions',
  handler:async(_args,ctx)=>{
   const root=ctx.cwd;const decision=evaluatePolicy(root,'__status__',{});
   ctx.ui.notify(decision.configured?`${path.join(root,'.pomp','permissions.json')}\n${decision.fingerprint && !decision.reason.startsWith('An OS-enforced')?'Default deny tool filter loaded.':decision.reason}`:'No project permission file.','info');
  }
 });
}
