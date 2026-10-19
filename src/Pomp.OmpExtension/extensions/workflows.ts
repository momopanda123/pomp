import type { ExtensionAPI } from '@oh-my-pi/pi-coding-agent';
import { callableHostTools,isPompHost,pick } from '../lib/vs-state.ts';
export function composerPrompt(task:string,tools:ReadonlySet<string>):string {
 if(!task.trim()||task.length>16000)throw new Error(pick('작업은 1–16,000자여야 해요.','The task must be 1–16,000 characters.'));
 const review=tools.has('propose_workspace_edits');
 return `POMP Workflow task:\n${task.trim()}\n\n${tools.has('composer_workflow')?'First call composer_workflow start with this task, then submit plan steps. Use status and its exact id/revision before phase transitions. Discover real source with host tools before entering editing. After propose_workspace_edits, wait for user review/application; do not invent approval or completion. In Verification, inspect live source with review_source and actual authorized test evidence before finish. The UI alone resumes a user pause. The workflow journal stores task/plan locally; it never automatically grants approvals or resumes processes.':''} Use the todo tool, when available, to maintain Planning → Discovery → Editing → Verification phases. First inspect the existing solution and relevant source; preserve unrelated work. ${tools.has('build_codebase_context')?'For broad tasks, call build_codebase_context with a bounded characterBudget to inspect folder/file/symbol structure and prioritize relevant source. Its omissions and partial excerpts are not full files or exact token counts.':''} ${tools.has('search_codebase')?'Use search_codebase for discovery and cite its path/line results.':''} ${review?'Read full current buffers and their hashes, then stage coherent multi-file changes with propose_workspace_edits. Staging is not application; the user selects, compiler-checks and approves in the review panel. Do not bypass a pending proposal with direct disk edits.':'Use only the offered edit tools and normal approval workflow.'} Keep each batch bounded and show any partial outcomes. Use checkpoints to propose recovery after a failure, not to overwrite newer edits. Verify using tools that are actually available and authorized; never invent test success. ${tools.has('terminal_status')?'If terminal jobs are queued or running, use terminal_status and do not mark verification complete yet. ':''}Preserve unfinished steps when interrupted. Report completed implementation, evidence, remaining gaps and decisions separately.`;
}
export default function workflows(pi:ExtensionAPI):void{
 if(!isPompHost())return;
 // Work › 코드 점검 shows who reported a finding: the host tool call does not say which agent made it, so the host hears it first, over
 // the main conversation's channel (a member's own UI may not reach the host). Never throws: omp would read that as a block of every tool.
 let mainUi:{notify:(message:string,type?:'info'|'warning'|'error')=>void}|undefined;
 pi.on('tool_call',(event,ctx)=>{
  try{
   const agent=(ctx as {agent?:{kind?:unknown;name?:unknown}}).agent;
   if(agent?.kind!=='sub')mainUi=ctx.ui;
   if(event.toolName!=='report_review_findings'||typeof event.toolCallId!=='string'||event.toolCallId.length>256)return undefined;
   const note={toolCallId:event.toolCallId,kind:agent?.kind==='sub'?'sub':'main',name:typeof agent?.name==='string'?agent.name.slice(0,64):''};
   (mainUi??ctx.ui).notify('pomp-review-reporter:'+Buffer.from(JSON.stringify(note)).toString('base64'),'info');
  }catch{}
  return undefined;
 });
 pi.registerCommand('pomp-workflow',{description:'Start a staged multi-file planning/edit/review/verification workflow',handler:async(args,ctx)=>{try{pi.sendUserMessage(composerPrompt(args,callableHostTools(pi)));}catch(error){ctx.ui.notify(error instanceof Error?error.message:pick('작업을 시작하지 못했어요.','The workflow could not be started.'),'warning');}}});
 pi.registerCommand('pomp-map',{description:'Ask the current model to explain a bounded structural codebase map',handler:async(args,ctx)=>{if(!callableHostTools(pi).has('build_codebase_context')){ctx.ui.notify(pick('코드 구조 정보를 사용할 수 없어요.','Structural codebase context is unavailable.'),'warning');return;}pi.sendUserMessage('Use build_codebase_context with a characterBudget of 24000'+(args.trim()?' and this focus: '+args.slice(0,8000):'')+'. Explain the folder/file/symbol structure with source citations, coverage omissions and selected excerpts. Do not pretend the structural outline is a semantic summary or complete codebase. Read full current buffers before detailed conclusions. Do not edit files.');}});
 pi.registerCommand('pomp-review',{description:'Review source with local diagnostics and evidence-backed agent findings',handler:async(args,ctx)=>{const tools=callableHostTools(pi);pi.sendUserMessage('Review the current workspace'+(args.trim()?' with this focus: '+args.slice(0,8000):'')+'. '+(tools.has('review_source')?'First call review_source to collect syntax and current IDE diagnostics. ':'Read available diagnostics. ')+'Inspect relevant source before claiming a problem; cite exact file/line evidence and distinguish confirmed diagnostics from hypotheses. '+(tools.has('report_review_findings')?'Use report_review_findings with the current review ID and exact live source hash/quoted evidence for useful agent findings. ':'')+'Do not change code during the review. Suggest fixes; use the normal approved shadow-review workflow only if the user requests fixes.');}});
 pi.registerCommand('pomp-max',{description:'Select an authenticated model with at least one million advertised context tokens',handler:async(args,ctx)=>{
  if(!ctx.isIdle()){ctx.ui.notify(pick('응답이 진행 중이에요.','A turn is in progress.'),'warning');return;}
  const eligible=ctx.models.list().filter(model=>model.contextWindow>=1000000);
  let target=args.trim()?ctx.models.resolve(args.trim()):undefined;
  if(!args.trim()){
   if(!eligible.length){ctx.ui.notify(pick('연결된 모델 중 100만 토큰 모델이 없어요.','No authenticated model has a one-million-token context.'),'warning');return;}
   const names=eligible.map(model=>`${model.provider}/${model.id}`);const chosen=await ctx.ui.select(pick('100만 토큰 모델','Choose a 1M-context model'),names);
   if(!chosen)return;target=eligible[names.indexOf(chosen)];
  }
  if(!target||target.contextWindow<1000000){ctx.ui.notify(pick('모델을 사용할 수 없거나 100만 토큰 모델이 아니에요.','That model is unavailable or does not advertise a one-million-token context.'),'warning');return;}
  if(!await ctx.ui.confirm(pick('모델을 바꿀까요?','Change model?'),`${target.provider}/${target.id}`))return;
  const ok=await pi.setModel(target);ctx.ui.notify(ok?pick('모델을 선택했어요.','Model selected.'):pick('모델을 선택하지 못했어요.','Model could not be selected.'),ok?'info':'error');
 }});
 // No compaction command of its own: the chat's /compact runs the engine's, and the compaction-context module already gives every compaction
 // (also the automatic ones) the POMP facts a summary must keep. The old /pomp-compact name is mapped to /compact by the chat page.
}
