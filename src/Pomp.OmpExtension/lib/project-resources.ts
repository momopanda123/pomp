import fs from 'node:fs';
import path from 'node:path';
import os from 'node:os';
import { parseDocument } from '../vendor/rule-deps.mjs';
import { readBounded } from './project-rules.ts';
import { pick } from './vs-state.ts';
export type ResourceKind = 'skills' | 'commands' | 'agents';
export interface ProjectResource { kind: ResourceKind; name: string; description: string; file: string; base: string; body: string }
export interface ResourceCatalog { resources: ProjectResource[]; diagnostics: string[] }
export function parseResource(kind: ResourceKind, text: string, file: string, base: string): ProjectResource {
 let metadata: Record<string, unknown> = {};let body=text.replace(/^\uFEFF/,'');
 if (/^---\r?\n/.test(body)) {
  const match=/^---\r?\n([\s\S]*?)\r?\n---(?:\r?\n|$)/.exec(body);
  if(!match) throw new Error('Unclosed frontmatter');
  const doc=parseDocument(match[1],{uniqueKeys:true,customTags:[]});
  if(doc.errors.length || doc.warnings.length)throw new Error('Invalid frontmatter');
  const value=doc.toJS({maxAliasCount:0});
  if(value && (typeof value!=='object'||Array.isArray(value)))throw new Error('Frontmatter must be a mapping');
  metadata=value??{};body=body.slice(match[0].length);
 }
 const fallback=kind==='skills'?path.basename(path.dirname(file)):path.basename(file,'.md');
 const name=metadata.name??fallback;const description=metadata.description??'';
 if(typeof name!=='string'||! /^[a-zA-Z0-9][a-zA-Z0-9._-]{0,63}$/.test(name))throw new Error('Invalid resource name');
 if(typeof description!=='string'||description.length>500)throw new Error('Description must contain at most 500 characters');
 if(!body.trim()||body.length>24000)throw new Error('Resource body must contain 1–24,000 characters');
 return {kind,name,description,file,base,body:body.trim()};
}
/** Reads only bounded Markdown files. Project resources never load JavaScript or automatically execute programs. */
export function catalogResources(root: string, globalRoot=path.join(os.homedir(),'.pomp')): ResourceCatalog {
 root=path.resolve(root);globalRoot=path.resolve(globalRoot);
 const map=new Map<string,ProjectResource>();const diagnostics:string[]=[];let visited=0;
 for(const [base,config] of [[globalRoot,globalRoot],[root,path.join(root,'.pomp')]]) {
  for(const kind of ['skills','commands','agents'] as ResourceKind[]) {
   const dir=path.join(config,kind);if(!fs.existsSync(dir))continue;
   function walk(folder:string,depth:number) {
    if(depth>3 || ++visited>100)return;
    try{
     const rel=path.relative(base,folder);
     if(path.isAbsolute(rel)||rel==='..'||rel.startsWith('..'+path.sep))throw new Error('Outside resource root');
     for(let current=folder;;current=path.dirname(current)) {if(fs.lstatSync(current).isSymbolicLink())throw new Error('Linked resource directory');if(current===base)break;}
     for(const entry of fs.readdirSync(folder,{withFileTypes:true}).sort((a,b)=>a.name.localeCompare(b.name))) {
      if(++visited>100)break;
      const file=path.join(folder,entry.name);
      if(entry.isSymbolicLink()) {diagnostics.push(file+': linked resource skipped');continue;}
      if(entry.isDirectory()&&kind==='skills') {walk(file,depth+1);continue;}
      if(!entry.isFile()||!(kind==='skills'?entry.name==='SKILL.md':entry.name.endsWith('.md')))continue;
      try{const resource=parseResource(kind,readBounded(base,file),file,base);map.set(kind+':'+resource.name.toLowerCase(),resource);}
      catch(error){diagnostics.push(file+': '+String(error));}
     }
    }catch(error){diagnostics.push(folder+': '+String(error));}
   }
   walk(dir,0);
  }
 }
 if(visited>100)diagnostics.push('Resource discovery limited to 100 directory entries');
 return {resources:[...map.values()],diagnostics:diagnostics.slice(0,20)};
}
export function expandResource(resource: ProjectResource, args:string):string {
 if(args.length>8000)throw new Error(pick('인수는 8,000자까지 쓸 수 있어요.','Arguments exceed 8,000 characters.'));
 return `POMP ${resource.kind} resource: ${resource.name}\nSource: ${resource.file}\nThis resource does not grant additional tool permissions.\n\n`+
  (resource.body.includes('$ARGUMENTS')?resource.body.replaceAll('$ARGUMENTS',()=>args):resource.body+(args?'\n\nUser arguments:\n'+args:''));
}
