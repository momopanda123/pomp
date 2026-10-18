import { useCallback, useEffect, useMemo, useReducer, useRef, useState } from 'react';
import { roleTitle } from './bundledDescriptions';
import { engineAbortText } from './engineAbortText';
import { Composer } from './components/Composer';
import { ConnectionBanner } from './components/ConnectionBanner';
import { EngineNotice } from './components/EngineNotice';
import { GenericDialog, PermissionDialog, type DialogResponse } from './components/Dialogs';
import { GoalCard } from './components/GoalCard';
import { Transcript } from './components/Transcript';
import { SessionHistory } from './components/SessionHistory';
import { SessionHeader } from './components/SessionHeader';
import { PartnerPicker } from './components/PartnerPicker';
import { ChatQueue } from './components/ChatQueue';
import { LiveAgentProgress } from './components/LiveAgentProgress';
import { TabBar, type ShellTab } from './components/TabBar';
import { SetupPanel } from './components/SetupPanel';
import { AgentsPanel, type AgentsView } from './components/AgentsPanel';
import { WorkTab, type RequestedWorkView } from './components/work/WorkTab';
import { ToolsTab } from './components/tools/ToolsTab';
import { toolsTarget, type ToolsView } from './toolsViews';
import { newId } from './format';
import { itemsForPartner } from './partnerView';
import { useGoalCard } from './goalCard';
import { useWorkState } from './work/store';
import { useEscapeToComposer, useFileDrops, useGlobalErrorLogging, useHostPump } from './hooks';
import { I18nContext, LocaleContext, makeTranslate } from './i18n';
import { dialogResponseRejection, permissionResponseRejection, promptRejection, type SendResult } from './limits';
import { send, MAIN_TARGET, type PermissionDecision, type SendTarget, type WorkspaceFile } from './protocol';
import { matchesShortcut, normalizeShortcuts } from './shortcuts';
import { sameRoot } from './knowledge';
import { KnowledgeProvider } from './knowledgeState';
import { useSessionHistory } from './sessionHistory';
import { isSetupRequired } from './setup';
import type { BuiltinCommand } from './slashCommands';
import { initialState, isBusy, reducer, type RequestOutcome, type TurnErrorItem } from './state';
import { useWorkAttention } from './workAttention';
import { effectiveRoles, MainRoleContext, RolesContext, targetPayload, TeamsContext } from './teamsCache';
import { rememberPrompt } from './promptHistory';
import { useStatusReport } from './statusBar';
import { UndeliveredStatusBanner } from './components/UndeliveredStatusBanner';
import './styles/conversations.css';

/** What a permission decision leaves in the transcript. */
const PERMISSION_OUTCOME: Record<PermissionDecision, RequestOutcome> = { allow: 'allow', deny: 'deny', alwaysAllow: 'always', allowForSession: 'session' };

export function App() {
  const [state, dispatch] = useReducer(reducer, initialState);
  const inputRef = useRef<HTMLTextAreaElement>(null);
  const [tab, setTab] = useState<ShellTab>('chat');
  const [workView, setWorkView] = useState<RequestedWorkView>('review');
  const [toolsView, setToolsView] = useState<ToolsView>('conversation');
  const [agentsView, setAgentsView] = useState<AgentsView>('roles');
  const [listOpen, setListOpen] = useState(false);
  const listRef = useRef<HTMLDivElement>(null);
  useEffect(() => {
    if (!listOpen) return;
    const outside = (event: PointerEvent): void => {
      const target = event.target;
      if (target instanceof Node && listRef.current?.contains(target)) return;
      // Let the opener's click toggle the list once, including clicks on its SVG.
      if (target instanceof Element && target.closest('[aria-controls="conversation-list"]')) return;
      // Unlike Escape/Close, an outside press must keep focus on the clicked control. Whatever is focused in the list blurs first, while
      // it is still mounted: an inline rename saves on blur, and an unmounted input gets no blur event.
      const active = document.activeElement;
      if (active instanceof HTMLElement && listRef.current?.contains(active)) active.blur();
      setListOpen(false);
    };
    document.addEventListener('pointerdown', outside, true);
    return () => document.removeEventListener('pointerdown', outside, true);
  }, [listOpen]);
  const [insertSkill, setInsertSkill] = useState<{ name: string; seq: number }>();
  const [insertText, setInsertText] = useState<{ text: string; seq: number }>();
  const [basketRequest] = useState(0);
  /** The /computer command: Tools › This conversation switches computer use (each revision once). */
  const [computerRequest, setComputerRequest] = useState<{ revision: number }>();
  /** Agents tab "Message" on a role or team and `/agent name`: open (or switch to) that partner tab in Chat. */
  const [partnerRequest, setPartnerRequest] = useState<{ key: string; seq: number }>();
  const translate = useMemo(() => makeTranslate(state.locale), [state.locale]);
  useHostPump(dispatch); useEscapeToComposer(inputRef); useGlobalErrorLogging(); useFileDrops(() => translate('composer.dropUnsupported'));
  useEffect(() => { document.documentElement.lang = state.locale; }, [state.locale]);

  const connected = state.bridge.state === 'running';
  const recovering = state.session?.recoveryStatus === 'restoring';
  const recoveryFailed = state.session?.recoveryStatus === 'failed';
  const conversationReady = connected && !recovering && !recoveryFailed;
  const busy = isBusy(state);
  const setupRequired = isSetupRequired(state.bridge);
  const attention = useWorkAttention(state.session);
  const workCount = attention.review + attention.plan + attention.codeReview + attention.workflow + attention.history;
  const accountAttention = setupRequired || state.setup?.restartRecommended === true || state.setup?.usage?.reports.some(report => report.limits.some(limit => limit.status === 'exhausted')) === true;
  const permission = state.permissions[0]; const dialog = state.dialogs[0];
  const modal = permission !== undefined || dialog !== undefined;
  // The page's shortcuts (Tools > Options > Pomp > Shortcuts), with defaults for an older host.
  const shortcuts = useMemo(() => normalizeShortcuts(state.hello?.shortcuts), [state.hello?.shortcuts]);
  useEffect(() => {
    function permissionShortcut(event: KeyboardEvent) {
      if (!matchesShortcut(event, shortcuts.permissionsView)) return;
      event.preventDefault(); setToolsView('permissions'); setTab('tools');
    }
    window.addEventListener('keydown', permissionShortcut);
    return () => window.removeEventListener('keydown', permissionShortcut);
  }, [shortcuts.permissionsView]);
  // Editor ↔ chat focus: keystrokes inside the WebView never reach Visual Studio's key bindings, so the page sends the host-chrome
  // message itself for the same chord the IDE binding uses (Tools > Options > Pomp > Shortcuts).
  useEffect(() => {
    const onKeyDown = (event: KeyboardEvent): void => {
      if (!matchesShortcut(event, shortcuts.toggleFocus)) return;
      event.preventDefault();
      event.stopPropagation();
      send('ui/toggleFocus', {});
    };
    window.addEventListener('keydown', onKeyDown, true);
    return () => window.removeEventListener('keydown', onKeyDown, true);
  }, [shortcuts.toggleFocus]);
  const newSessionPending = state.pendingNewSession !== undefined;
  // A failed queue send is reported in the VS status bar (and the POMP log), not between the queue and the input.
  useStatusReport(state.sendError ? engineAbortText(state.sendError, translate) : null);
  const workspaceKey = state.session?.workingDirectory ?? '';
  const sessionId = state.session?.sessionId;
  const goal = useGoalCard(workspaceKey, sessionId ?? '');
  const scopedPartner = state.partner && sessionId && state.partner.sessionId === sessionId && state.partner.partner.kind !== 'main' ? state.partner : null;
  const partnerName = scopedPartner ? (scopedPartner.partner.kind === 'agent'
    ? roleTitle(effectiveRoles(state.agents?.roles ?? []).find(role => role.name === scopedPartner.partner.id), translate, scopedPartner.partner.id)
    : scopedPartner.title || state.agents?.teams.find(team => team.id === scopedPartner.partner.id)?.title || scopedPartner.partner.id) : undefined;
  // The view follows the partner tab: Default (POMP) shows every turn, an agent/team tab only its own (partnerView.ts).
  const background = useWorkState('background') as { sessionId?: string; jobs?: { id: string; agent: string }[] } | undefined;
  const viewItems = useMemo(() => {
    if (!scopedPartner) return state.items;
    const agents = new Map<string, string>();
    for (const entry of state.agentHistory.entries) for (const member of entry.members) agents.set(member.jobId, member.agent);
    for (const job of background?.jobs ?? []) agents.set(job.id, job.agent);
    if (scopedPartner.instance && scopedPartner.partner.kind === 'agent' && scopedPartner.partner.id) agents.set(scopedPartner.instance, scopedPartner.partner.id);
    return itemsForPartner(state.items, scopedPartner.partner, id => agents.get(id));
  }, [state.items, scopedPartner, state.agentHistory, background]);
  /** POMP answers it: show it in the Default (POMP) tab, where its turn is visible. */
  const showPomp = () => { if (latestPartnerTab()) send('conversation/partner', { requestId: newId(), partner: { kind: 'main' } }); };
  const openPartner = (target: SendTarget) => {
    if (target.kind === 'agent' || target.kind === 'team') setPartnerRequest(previous => ({ key: `${target.kind}:${target.id}`, seq: (previous?.seq ?? 0) + 1 }));
    // A running member is a one-message target; Default (POMP) shows every turn, so it is picked there.
    else { showPomp(); dispatch({ type: 'selectTarget', target }); }
    setTab('chat'); setListOpen(false); window.requestAnimationFrame(() => inputRef.current?.focus());
  };
  const latest = useRef(state); latest.current = state;
  /** The active partner tab (`agent:name`/`team:id`) of the current conversation, or undefined in Default (POMP). */
  function latestPartnerTab(): string | undefined {
    const current = latest.current; const partner = current.partner;
    return partner && partner.sessionId === current.session?.sessionId && partner.partner.kind !== 'main' && partner.partner.id ? `${partner.partner.kind}:${partner.partner.id}` : undefined;
  }
  const activeTab = setupRequired ? 'accounts' : tab;
  const attentionRef = useRef(attention); attentionRef.current = attention;
  const activeTabRef = useRef(activeTab); activeTabRef.current = activeTab;
  const wasRequired = useRef(false);
  useEffect(() => {
    if (setupRequired) setTab('accounts'); else if (wasRequired.current) setTab('chat');
    wasRequired.current = setupRequired;
  }, [setupRequired]);
  const focused = useRef(false);
  useEffect(() => { if (connected && activeTab === 'chat' && !focused.current) { focused.current = true; inputRef.current?.focus(); } }, [connected, activeTab]);

  /** A message while a reply runs is queued, also a question sent from the code editor. */
  const sending = useRef(false); const queueSubmitting = useRef<string | undefined>(undefined);
  /** `toMain`: a request that belongs to the main conversation (editor menu, plan continuation): once accepted the view moves to Default (POMP). */
  const sendMessage = useCallback((text: string, attachments: WorkspaceFile[] = [], clearDraft = false, target?: SendTarget | null, preserveDraftTarget = false, includeEditorContext?: boolean, toMain = false): SendResult => {
    const current = latest.current; const partnerTab = latestPartnerTab();
    // A one-message target chosen in an agent/team tab goes from that tab too: the message (and its turn) stays visible there.
    const destination = target === undefined ? current.selectedTarget : target ?? undefined;
    const queued = current.phase === 'running' || current.session?.isStreaming === true;
    if ((!queued && sending.current) || queueSubmitting.current || current.replaying || current.phase === 'sending' || current.pendingNewSession !== undefined || current.permissions.length > 0 || current.dialogs.length > 0) return { ok: false, reason: 'busy' };
    if (current.bridge.state !== 'running') return { ok: false, reason: 'hostUnavailable' };
    if (current.session?.recoveryStatus === 'restoring' || current.session?.recoveryStatus === 'failed') return { ok: false, reason: 'busy' };
    const id = newId();
    // An editor or work request POMP answers moves the view to Default (POMP) once it was accepted; POMP chosen on the chip, or a retry
    // of such a message, stays in the tab it was sent from.
    const moveToMain = toMain && destination?.kind === 'main';
    // Sent from an agent/team tab to another target: the host records that tab with the message, so it stays there after a restore
    // (a message for the tab's own partner already names it in its routing brief).
    const origin = partnerTab && !moveToMain && destination && `${destination.kind}:${destination.id}` !== partnerTab ? partnerTab.split(':') as ['agent' | 'team', string] : undefined;
    const message = { clientMessageId: id, text, includeEditorContext: includeEditorContext ?? current.includeEditorContext,
      ...(attachments.length > 0 ? { attachments: attachments.map(file => ({ path: file.fullPath })) } : {}), ...targetPayload(destination),
      ...(origin ? { from: { kind: origin[0], id: origin[1] } } : {}) };
    const rejection = promptRejection(text, { type: 'chat/send', payload: message }, attachments.map(file => file.fullPath));
    if (rejection) return { ok: false, ...rejection };
    if (queued) queueSubmitting.current = id; else sending.current = true;
    if (!send('chat/send', message)) { sending.current = false; queueSubmitting.current = undefined; return { ok: false, reason: 'hostUnavailable' }; }
    if (moveToMain && partnerTab) send('conversation/partner', { requestId: newId(), partner: { kind: 'main' } });
    dispatch({ type: 'userSend', id, text, attachments, clearDraft, target: destination, preserveDraftTarget, queued, partnerKey: moveToMain ? undefined : partnerTab });
    rememberPrompt(current.session?.workingDirectory ?? '', text); // every sent prompt, also one an editor command composed
    return { ok: true };
  }, []);
  useEffect(() => { if (!busy) sending.current = false; }, [busy]);
  useEffect(() => {
    if (queueSubmitting.current && !state.pendingQueued.some(item => item.clientMessageId === queueSubmitting.current && !item.failed)) queueSubmitting.current = undefined;
  }, [state.pendingQueued]);
  useEffect(() => {
    const timers = state.pendingQueued.filter(item => !item.failed).map(item => window.setTimeout(() => dispatch({ type: 'queueSendUnconfirmed', id: item.clientMessageId }), 15000));
    return () => timers.forEach(window.clearTimeout);
  }, [state.pendingQueued]);
  const retryTurn = useCallback((item: TurnErrorItem) => {
    if (item.retryText === undefined) return;
    const result = sendMessage(item.retryText, item.retryAttachments, false, item.retryTarget ?? null, true);
    if (!result.ok && result.reason !== 'busy') dispatch({ type: 'localNotice', level: 'error', message: makeTranslate(latest.current.locale)('error.hostUnavailable') });
  }, [sendMessage]);

  const respondPermission = useCallback((requestId: string, decision: PermissionDecision, note?: string): SendResult => {
    const response = note ? { requestId, decision, note } : { requestId, decision };
    const request = latest.current.permissions.find(item => item.requestId === requestId);
    const rejection = permissionResponseRejection(response, request?.source);
    if (rejection) return { ok: false, ...rejection };
    if (!send('permission/respond', response)) return { ok: false, reason: 'hostUnavailable' };
    dispatch({ type: 'permissionAnswered', requestId, outcome: PERMISSION_OUTCOME[decision], at: Date.now() }); if (activeTabRef.current === 'chat') inputRef.current?.focus(); return { ok: true };
  }, []);
  const respondDialog = useCallback((requestId: string, response: DialogResponse): SendResult => {
    const message = { requestId, ...response }; const rejection = dialogResponseRejection(message);
    if (rejection) return { ok: false, ...rejection };
    if (!send('dialog/respond', message)) return { ok: false, reason: 'hostUnavailable' };
    dispatch({ type: 'dialogAnswered', requestId, outcome: response.cancelled ? 'cancelled' : 'answered', at: Date.now() }); if (activeTabRef.current === 'chat') inputRef.current?.focus(); return { ok: true };
  }, []);
  const dropPermission = useCallback((requestId: string) => dispatch({ type: 'permissionAnswered', requestId, outcome: 'expired', at: Date.now() }), []);
  const dropDialog = useCallback((requestId: string) => dispatch({ type: 'dialogAnswered', requestId, outcome: 'expired', at: Date.now() }), []);
  const canChangeSession = useCallback(() => {
    const current = latest.current;
    return current.pendingNewSession === undefined && !isBusy(current) && current.bridge.state === 'running' && current.pendingQueued.some(item => !item.failed) === false &&
      current.permissions.length === 0 && current.dialogs.length === 0 && current.session?.recoveryStatus !== 'restoring' && current.session?.hasPendingAsyncWork !== true && attentionRef.current.terminal === 0;
  }, []);
  const newSession = useCallback((): boolean => {
    if (!canChangeSession()) return false;
    const requestId = newId();
    if (!send('session/new', { requestId })) { dispatch({ type: 'localNotice', level: 'error', message: makeTranslate(latest.current.locale)('error.hostUnavailable') }); return false; }
    dispatch({ type: 'newSessionRequested', requestId }); setTab('chat'); setListOpen(false); return true;
  }, [canChangeSession]);
  const switchSession = useCallback((requestId: string, root: string, id: string): boolean => {
    if (!canChangeSession() || !send('session/history', { action: 'switch', requestId, root, id })) return false;
    dispatch({ type: 'newSessionRequested', requestId }); return true;
  }, [canChangeSession]);
  const history = useSessionHistory(workspaceKey, sessionId, connected, state.locale, switchSession);
  const historyScope = useRef({ workspaceKey, sessionId });
  useEffect(() => {
    const before = historyScope.current; historyScope.current = { workspaceKey, sessionId };
    if (before.workspaceKey !== workspaceKey || (before.sessionId !== sessionId && !history.pendingId)) setListOpen(false);
  }, [workspaceKey, sessionId, history.pendingId]);
  const sessionBlocked = !connected || recovering || busy || newSessionPending || modal || state.session?.hasPendingAsyncWork === true || attention.terminal > 0 || state.pendingQueued.some(item => !item.failed);
  const openList = () => { setTab('chat'); setListOpen(true); history.command('refresh'); };
  const closeList = () => { setListOpen(false); window.requestAnimationFrame(() => document.querySelector<HTMLButtonElement>('[data-testid="session-quick"]')?.focus()); };
  const openWork = (view?: RequestedWorkView) => { setWorkView(view ?? workView); setTab('work'); };
  const openRuns = () => { setAgentsView('roles'); setTab('agents'); };
  /** A Tools view id (a chip, an id older builds used, or `hooks`) opens where it lives now: a Tools chip, or the Agents tab's Automation view for the project hooks. Other ids are ignored. */
  const openTools = (view: string) => {
    const target = toolsTarget(view);
    if (target?.tab === 'tools') { setToolsView(target.view); setTab('tools'); }
    else if (target) { setAgentsView(target.view); setTab('agents'); if (connected) send('agents/command', { requestId: newId(), action: 'refresh' }); }
  };
  const workRequest = state.workRequest;
  useEffect(() => {
    if (!workRequest || modal) return;
    if (['review', 'progress', 'plan', 'workflow', 'codeReview', 'terminal', 'history'].includes(workRequest.view)) openWork(workRequest.view as RequestedWorkView);
    else if (workRequest.view === 'background') openRuns();
    else openTools(workRequest.view);
  }, [workRequest?.seq]);
  // The editor's "Add to Knowledge ▸ New": Tools › Knowledge opens its new-knowledge form with the lines (a draft for another workspace is not ours).
  const draftSeq = state.knowledgeDraft?.seq;
  useEffect(() => {
    const draft = state.knowledgeDraft;
    if (!draft || (workspaceKey && !sameRoot(draft.payload.root, workspaceKey))) return;
    setToolsView('knowledge'); setTab('tools');
  }, [draftSeq]);
  /** Work › Progress "continue" and a started workflow (Work or Tools): the plan instruction plus the task goes to the main agent and Chat opens. */
  const continueWork = (task?: string) => {
    const instruction = state.locale.startsWith('ko') ? '현재 작업 계획을 확인하고 미완료 단계부터 이어서 진행해 주세요. 기존 변경과 검증 결과를 먼저 확인하고, 완료하지 않은 작업을 완료했다고 보고하지 마세요.' : 'Review the current work plan and continue from the unfinished step. Check existing changes and verification evidence first; do not report unverified work as complete.';
    const result = sendMessage(task ? instruction + '\n\n' + task : instruction, [], false, MAIN_TARGET, false, undefined, true); if (result.ok) setTab('chat');
  };
  /** Puts the text into the chat input (not sent) and shows Chat. */
  const insertIntoChat = (text: string) => { setTab('chat'); setListOpen(false); setInsertText(previous => ({ text, seq: (previous?.seq ?? 0) + 1 })); };
  const prefillSeq = state.prefill?.seq;
  useEffect(() => { if (prefillSeq !== undefined) { setTab('chat'); setListOpen(false); if (state.prefill?.payload.target || state.prefill?.payload.pickTarget) showPomp(); } }, [prefillSeq]);
  const focusedPrefill = useRef<number | undefined>(undefined);
  useEffect(() => {
    if (prefillSeq === undefined || focusedPrefill.current === prefillSeq || activeTab !== 'chat' || modal) return;
    focusedPrefill.current = prefillSeq; window.requestAnimationFrame(() => inputRef.current?.focus());
  }, [prefillSeq, activeTab, modal]);
  const changeTab = (next: ShellTab) => {
    if (setupRequired && next !== 'accounts') return;
    if (next === 'work') openWork();
    else { setTab(next); if (next === 'agents' && connected) send('agents/command', { requestId: newId(), action: 'refresh' }); }
  };
  const handleCommand = (name: BuiltinCommand, args: string): SendResult => {
    switch (name) {
      case 'new': return newSession() ? { ok: true } : { ok: false, reason: 'busy' };
      case 'sessions': openList(); return { ok: true };
      case 'accounts': case 'login': case 'usage': changeTab('accounts'); return { ok: true };
      case 'work': openWork(); return { ok: true };
      case 'agents': changeTab('agents'); return { ok: true };
      case 'rename': return history.command('rename', args) ? { ok: true } : { ok: false, reason: 'busy' };
      case 'compact': return history.command('compact', args.slice(0, 2000)) ? { ok: true } : { ok: false, reason: 'busy' };
      case 'computer': setToolsView('conversation'); setTab('tools'); setComputerRequest(previous => ({ revision: (previous?.revision ?? 0) + 1 })); return { ok: true };
      case 'model': case 'thinking': case 'effort': case 'permissions': case 'agent': case 'team': case 'goal': case 'help': return { ok: true };
    }
  };
  const planTotal = state.session?.planTaskCount ?? 0;
  const completed = planTotal - (state.session?.planOpenCount ?? 0);

  const requestCard = permission
    ? <PermissionDialog key={permission.requestId} request={permission} position={state.permissionDone + 1} total={state.permissionDone + state.permissions.length} shortcuts={shortcuts} onRespond={respondPermission} onExpired={dropPermission} />
    : dialog
      ? <GenericDialog key={dialog.requestId} request={dialog} position={state.dialogDone + 1} total={state.dialogDone + state.dialogs.length} shortcuts={shortcuts} onRespond={respondDialog} onDraftChange={(requestId, text) => dispatch({ type: 'dialogDraft', requestId, text })} onExpired={dropDialog} />
      : goal.card
        ? <GoalCard key={goal.card.key} card={goal.card} root={workspaceKey} sessionId={sessionId ?? ''} busy={busy} onClose={goal.close} />
        : null;

  return <LocaleContext value={state.locale}><I18nContext value={translate}><KnowledgeProvider root={workspaceKey} sessionId={sessionId} connected={connected}><TeamsContext value={state.agents?.teams ?? []}><RolesContext value={state.agents?.roles ?? []}><MainRoleContext value={state.agents?.main}><div className="app">
    <TabBar active={activeTab} onChange={changeTab} bridge={state.bridge} required={setupRequired} workCount={workCount} accountAttention={accountAttention} chatAttention={modal && activeTab !== 'chat'} inert={false} engine={state.engine} />
    <main className="main">
      <section id="page-chat" className="tab-page chat-page" role="tabpanel" aria-labelledby="tab-chat" hidden={activeTab !== 'chat'} inert={activeTab !== 'chat' || undefined}>
        <SessionHeader session={state.session} history={history} disabled={sessionBlocked} onNew={newSession} listOpen={listOpen} onToggleList={() => { if (listOpen) closeList(); else openList(); }}><PartnerPicker state={state.partner} agents={state.agents} replies={state.conversationReplies} sessionId={sessionId} disabled={!connected || busy || newSessionPending || modal} request={partnerRequest} onRefreshRoster={() => { if (connected) send('agents/command', { requestId: newId(), action: 'refresh' }); }} /></SessionHeader>
        <div className="conversation-list-anchor">{listOpen && <div ref={listRef} id="conversation-list" className="conversation-list"><SessionHistory locale={state.locale} sessionId={sessionId} sessionName={state.session?.sessionName} history={history} disabled={sessionBlocked} onNew={newSession} onOpened={closeList} onClose={closeList} /></div>}</div>
        <ConnectionBanner bridge={state.bridge} onRestart={() => send('bridge/restart', {})} onOpenLog={() => send('log/open', {})} /><EngineNotice engine={state.engine} /><UndeliveredStatusBanner />
        {(recovering || recoveryFailed) && <div className="composer-error" role="status" data-testid="session-recovery">
          {state.session?.recoveryMessage || (state.locale.startsWith('ko') ? '이전 대화를 확인하는 중' : 'Confirming the previous conversation')}
          {recoveryFailed && connected && <div>
            <button type="button" onClick={() => send('session/recover', {})}>{state.locale.startsWith('ko') ? '복원 재시도' : 'Retry recovery'}</button>
            <button type="button" onClick={openList}>{state.locale.startsWith('ko') ? '다른 기록 열기' : 'Open history'}</button>
            <button type="button" disabled={sessionBlocked} onClick={newSession}>{state.locale.startsWith('ko') ? '새 대화' : 'New conversation'}</button>
          </div>}
        </div>}
        <Transcript controlsEnabled={conversationReady && !newSessionPending && !state.replaying} root={workspaceKey} sessionId={sessionId} items={viewItems} partnerName={partnerName} busy={busy} replaying={state.replaying} replayRevision={state.replayRevision} replayTruncated={state.replayTruncated} canRetry={conversationReady && !busy && !newSessionPending && !modal} onRetry={retryTurn} request={requestCard} />
        {(planTotal > 0 || workCount > 0 || attention.background > 0 || attention.hooks > 0) && <div className="activity-chips" data-testid="activity-chips">
          {planTotal > 0 && <button className="activity-chip" type="button" data-testid="activity-plan" onClick={() => openWork('progress')}>{translate('work.plan')} {completed}/{planTotal}</button>}
          {(['review', 'codeReview', 'workflow', 'history'] as const).filter(view => attention[view] > 0).map(view => <button className="activity-chip" type="button" key={view} data-testid={`activity-${view}`} onClick={() => openWork(view)}>{translate(`work.${view}`)} {attention[view]}</button>)}
          {/* The live progress row below names this turn's running members: the chip counts only the others (earlier turns). */}
          {(['hooks'] as const).filter(view => attention[view] > 0).map(view => <button className="activity-chip" type="button" key={view} data-testid={`activity-${view}`} onClick={() => openTools(view)}>{translate(`work.${view}`)} {attention[view]}</button>)}
        </div>}
        <LiveAgentProgress disabled={!conversationReady || newSessionPending || state.replaying} sessionId={sessionId} promptId={state.activePromptId} items={viewItems} />
        <ChatQueue state={state.queue} replies={state.conversationReplies} sessionId={sessionId} disabled={!conversationReady || newSessionPending || modal} pendingSends={state.pendingQueued} onRecover={item => { dispatch({ type: 'recoverQueueSend', id: item.clientMessageId }); inputRef.current?.focus(); }} />
        <Composer locale={state.locale} workspaceKey={workspaceKey} sessionId={sessionId} model={state.session?.model} session={state.session} setup={state.setup} setupReplies={state.setupReplies} onManageAccounts={() => changeTab('accounts')} inputRef={inputRef} connected={conversationReady} busy={busy} queueBlocked={state.phase === 'sending' || state.replaying || state.pendingQueued.some(item => !item.failed)} requestPending={modal || activeTab !== 'chat'} newSessionPending={newSessionPending} draft={state.composer} dispatch={dispatch} includeEditorContext={state.includeEditorContext} onIncludeEditorContextChange={value => dispatch({ type: 'setIncludeEditorContext', value })} onSend={sendMessage} onStop={() => send('chat/stop', {})} commands={state.commands.commands} commandsHidden={state.commands.hidden} onCommand={handleCommand} onGoal={goal.open} skillsOff={goal.skillsOff} prefill={state.prefill} insertSkill={insertSkill} insertText={insertText} basketRequest={basketRequest} basketTokens={state.basketTokens} agents={state.agents} shortcuts={shortcuts} composerPredictions={state.hello?.composerPredictions === true} approvalMode={state.hello?.approvalMode} permissionModeReply={state.permissionModeReply} editorFollow={state.editorFollow} selectedTarget={state.selectedTarget} onSelectTarget={target => dispatch({ type: 'selectTarget', target })} partnerName={partnerName} partner={scopedPartner?.partner} onOpenPartner={openPartner} />
      </section>
      <section id="page-work" className="tab-page work-page" role="tabpanel" aria-labelledby="tab-work" hidden={activeTab !== 'work'} inert={activeTab !== 'work' || undefined}>
        <WorkTab locale={state.locale} session={state.session} workspaceKey={workspaceKey} sessionId={sessionId} connected={conversationReady} busy={busy} newSessionPending={newSessionPending} inert={activeTab !== 'work'} attention={attention} requestedView={workView} onViewChange={setWorkView} onStop={() => send('chat/stop', {})} onContinue={continueWork} onSolve={text => {
          const result = sendMessage(text, [], false, undefined, true, false); if (result.ok) setTab('chat');
          return result;
        }} onOpenWorkflows={() => openTools('workflows')} />
      </section>
      <section id="page-agents" className="tab-page agents-page" role="tabpanel" aria-labelledby="tab-agents" hidden={activeTab !== 'agents'} inert={activeTab !== 'agents' || undefined}>
        <AgentsPanel agents={state.agents} replies={state.agentsReplies} setup={state.setup} setupReplies={state.setupReplies} locale={state.locale} connected={connected} inert={activeTab !== 'agents'} sessionId={sessionId} view={agentsView} onViewChange={setAgentsView} onSelectTarget={openPartner} workspaceKey={workspaceKey} busy={busy} newSessionPending={newSessionPending} onShowPermissions={() => openTools('permissions')} />
      </section>
      <section id="page-tools" className="tab-page" role="tabpanel" aria-labelledby="tab-tools" hidden={activeTab !== 'tools'} inert={activeTab !== 'tools' || undefined}>
        <ToolsTab active={activeTab === 'tools'} locale={state.locale} workspaceKey={workspaceKey} sessionId={sessionId} connected={connected} busy={busy} newSessionPending={newSessionPending} inert={activeTab !== 'tools'} requestedView={toolsView} onViewChange={setToolsView} onUseSkill={name => { setTab('chat'); setListOpen(false); setInsertSkill(previous => ({ name, seq: (previous?.seq ?? 0) + 1 })); }} rules={state.activeRules} computer={state.computer} computerReply={state.computerReply} computerRequest={computerRequest} knowledgeDraft={state.knowledgeDraft} onInsertText={insertIntoChat} />
      </section>
      <section id="page-accounts" className="tab-page" role="tabpanel" aria-labelledby="tab-accounts" hidden={activeTab !== 'accounts'} inert={activeTab !== 'accounts' || undefined}>
        {activeTab === 'accounts' && <SetupPanel setup={state.setup} replies={state.setupReplies} sessionModel={state.session?.model} required={setupRequired} connected={connected} locale={state.locale} inert={false} />}
      </section>
    </main>
  </div></MainRoleContext></RolesContext></TeamsContext></KnowledgeProvider></I18nContext></LocaleContext>;
}
