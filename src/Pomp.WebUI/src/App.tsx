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
import { PartnerPicker, type TabSwitch } from './components/PartnerPicker';
import { ChatQueue } from './components/ChatQueue';
import { LiveAgentProgress } from './components/LiveAgentProgress';
import { BtwPanel } from './components/BtwPanel';
import { availableWhileRequired, TabBar, type ShellTab } from './components/TabBar';
import { SetupPanel } from './components/SetupPanel';
import { RemotePanel } from './components/RemotePanel';
import { AgentsPanel, type AgentsView } from './components/AgentsPanel';
import { WorkTab, type RequestedWorkView } from './components/work/WorkTab';
import { ToolsTab } from './components/tools/ToolsTab';
import { toolsTarget, type ToolsView } from './toolsViews';
import { newId } from './format';
import { useGoalCard } from './goalCard';
import type { ConversationPartner, ConversationPlan } from './conversationProtocol';
import { useEscapeToComposer, useFileDrops, useGlobalErrorLogging, useHostFocusRedirect, useHostPump } from './hooks';
import { I18nContext, LocaleContext, makeTranslate, type MessageKey } from './i18n';
import { dialogResponseRejection, permissionResponseRejection, promptRejection, type SendResult } from './limits';
import { send, MAIN_TARGET, type PermissionDecision, type SendTarget, type WorkspaceFile } from './protocol';
import { matchesShortcut, normalizeShortcuts, vsKeyChord } from './shortcuts';
import { sameRoot } from './knowledge';
import { KnowledgeProvider } from './knowledgeState';
import { useSessionHistory } from './sessionHistory';
import { isSetupRequired } from './setup';
import { useSetupCommands } from './setupCommands';
import type { BuiltinCommand } from './slashCommands';
import { btwTurns, initialState, isBusy, reducer, type RequestOutcome, type TurnErrorItem } from './state';
import { useWorkAttention } from './workAttention';
import { useTabBadges } from './tabBadges';
import { effectiveRoles, MainRoleContext, OpenPartnerContext, RolesContext, targetPayload, TeamsContext } from './teamsCache';
import { rememberPrompt } from './promptHistory';
import { useStatusReport } from './statusBar';
import { UndeliveredStatusBanner } from './components/UndeliveredStatusBanner';
import './styles/conversations.css';
import { backendId, capabilities, backendCommandAllowed, type BackendId } from './backend';
import { AcpControls, AcpPermissionCard, BackendPicker } from './components/AcpControls';
import { Icon } from './components/Icon';

/** What a permission decision leaves in the transcript. */
const PERMISSION_OUTCOME: Record<PermissionDecision, RequestOutcome> = { allow: 'allow', deny: 'deny', alwaysAllow: 'always', allowForSession: 'session' };
const NO_PLANS: ConversationPlan[] = [];
/** The chat's activity chips are named like the views they open. */
const ACTIVITY_LABELS = { review: 'work.view.review', codeReview: 'work.view.codeReview', workflow: 'work.workflow', history: 'work.history.title' } as const satisfies Record<string, MessageKey>;

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
  /** The /computer command: the composer's screen & input control button switches computer use (each revision once). */
  const [computerRequest, setComputerRequest] = useState<{ revision: number }>();
  /** Agents tab "Message" on a role or team and `/agent name`: open (or switch to) that agent's or team's tab, its own conversation. */
  const [partnerRequest, setPartnerRequest] = useState<{ partner: ConversationPartner; seq: number }>();
  const translate = useMemo(() => makeTranslate(state.locale), [state.locale]);
  // One setup-command controller for the Accounts, Remote and Agents pages: an answer that arrives while its page is off screen is kept.
  const setupCmd = useSetupCommands(state.setupReplies, translate);
  useHostPump(dispatch); useEscapeToComposer(inputRef); useGlobalErrorLogging();
  useEffect(() => { document.documentElement.lang = state.locale; }, [state.locale]);

  const backend = backendId(state.session, state.backend);
  const caps = capabilities(state.session, backend);
  const native = caps.nativeOmp;
  const connected = native ? state.bridge.state === 'running' : state.acp?.connected === true && !!state.acp.sessionId && !state.acp.authRequired;
  useFileDrops(() => translate('composer.dropUnsupported'), caps.editorContext || caps.images, () => translate('acp.noDrop'));
  const recovering = state.session?.recoveryStatus === 'restoring';
  const recoveryFailed = state.session?.recoveryStatus === 'failed';
  const conversationReady = connected && !recovering && !recoveryFailed;
  const busy = isBusy(state);
  const setupRequired = native && isSetupRequired(state.bridge);
  const workspaceKey = state.session?.workingDirectory ?? '';
  const plans = state.tabs && sameRoot(state.tabs.root, workspaceKey) ? state.tabs.plans : NO_PLANS;
  const attention = useWorkAttention(state.session, plans);
  const workCount = attention.review + attention.codeReview + attention.workflow + attention.history;
  const accountAttention = native ? setupRequired || state.setup?.restartRecommended === true || state.setup?.usage?.reports.some(report => report.limits.some(limit => limit.status === 'exhausted')) === true : state.acp?.authRequired === true;
  const permission = native ? state.permissions[0] : undefined; const dialog = native ? state.dialogs[0] : undefined;
  const modal = permission !== undefined || dialog !== undefined;
  // The page's shortcuts (Tools > Options > Pomp > Shortcuts), with defaults for an older host.
  const shortcuts = useMemo(() => normalizeShortcuts(state.hello?.shortcuts), [state.hello?.shortcuts]);
  useEffect(() => {
    function permissionShortcut(event: KeyboardEvent) {
      if (!native || !matchesShortcut(event, shortcuts.permissionsView)) return;
      event.preventDefault(); setToolsView('permissions'); setTab('tools');
    }
    window.addEventListener('keydown', permissionShortcut);
    return () => window.removeEventListener('keydown', permissionShortcut);
  }, [shortcuts.permissionsView, native]);
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
  // Visual Studio keys (F5 starts debugging, Ctrl+Shift+B builds): the page passes on the ones nothing in it handled and that are not
  // its own shortcuts; the host runs the command Visual Studio binds to them. Bubbling, so a field or a panel that uses the key wins.
  useEffect(() => {
    const pageChords: readonly string[] = Object.values(shortcuts);
    const onKeyDown = (event: KeyboardEvent): void => {
      if (event.defaultPrevented) return;
      const chord = vsKeyChord(event);
      if (chord === null || pageChords.includes(chord)) return;
      event.preventDefault();
      send('ui/vsKey', { chord });
    };
    window.addEventListener('keydown', onKeyDown);
    return () => window.removeEventListener('keydown', onKeyDown);
  }, [shortcuts]);
  const newSessionPending = state.pendingNewSession !== undefined;
  // A failed queue send is reported in the VS status bar (and the POMP log), not between the queue and the input.
  useStatusReport(state.sendError ? engineAbortText(state.sendError, translate) : null);
  const sessionId = state.session?.sessionId;
  const goal = useGoalCard(workspaceKey, sessionId ?? '');
  const scopedTabs = state.tabs && sameRoot(state.tabs.root, workspaceKey) && sessionId && state.tabs.sessionId === sessionId && state.tabs.partner.kind !== 'main' ? state.tabs : null;
  const partnerName = scopedTabs ? (scopedTabs.partner.kind === 'agent'
    ? roleTitle(effectiveRoles(state.agents?.roles ?? []).find(role => role.name === scopedTabs.partner.id), translate, scopedTabs.partner.id)
    : scopedTabs.title || state.agents?.teams.find(team => team.id === scopedTabs.partner.id)?.title || scopedTabs.partner.id) : undefined;
  const pendingSwitch = useRef<string | undefined>(undefined);
  /** A tab can activate while another conversation runs; the result identifies the destination snapshot to display. */
  function switchTab(command: TabSwitch): boolean {
    // One at a time: the pending state reaches `latest` only with the next render.
    if (!canSwitchTab()) return false;
    const requestId = newId();
    pendingSwitch.current = requestId;
    if (!send('conversation/tab', { ...command, requestId })) { pendingSwitch.current = undefined; dispatch({ type: 'localNotice', level: 'error', message: makeTranslate(latest.current.locale)('error.hostUnavailable') }); return false; }
    const destinationBackend = command.action === 'activate' && command.sessionId ? state.tabs?.tabs.find(tab => tab.sessionId === command.sessionId)?.backend ?? 'omp' : 'omp';
    dispatch({ type: 'newSessionRequested', requestId, backendSwitch: destinationBackend !== backend }); setListOpen(false); return true;
  }
  /** Editor requests belong to the Default (POMP) tab's conversation: from an agent or team tab the page switches there first. */
  const showPomp = () => latestPartnerTab() && switchTab({ action: 'activate' });
  const pendingPartnerFocus = useRef<ConversationPartner | undefined>(undefined);
  const openPartner = (target: SendTarget) => {
    if (!native) return;
    if (target.kind === 'agent' || target.kind === 'team') {
      pendingPartnerFocus.current = { kind: target.kind, id: target.id };
      setPartnerRequest(previous => ({ partner: { kind: target.kind as 'agent' | 'team', id: target.id }, seq: (previous?.seq ?? 0) + 1 }));
    }
    // A running member of this conversation is a one-message target here.
    else dispatch({ type: 'selectTarget', target });
    setTab('chat'); setListOpen(false);
    if (target.kind === 'instance') window.requestAnimationFrame(() => inputRef.current?.focus());
  };
  const latest = useRef(state); latest.current = state;
  /** Whether the current conversation is an agent or team tab's (not the Default (POMP) tab's). */
  function latestPartnerTab(): boolean {
    const current = latest.current; const tabs = current.tabs;
    if (backendId(current.session, current.backend) !== 'omp') return false;
    return !!tabs && sameRoot(tabs.root, current.session?.workingDirectory) && tabs.sessionId === current.session?.sessionId && tabs.partner.kind !== 'main';
  }
  const activeTab = !native && (tab === 'agents' || tab === 'remote') ? 'chat' : setupRequired && !availableWhileRequired(tab) ? 'accounts' : tab;
  // Approvals and questions of this conversation, and of the tabs in the background (a message sent to another tab can end up waiting there).
  const backgroundWaiting = !native || !state.tabs || !sameRoot(state.tabs.root, workspaceKey) ? 0
    : state.tabs.tabs.filter(item => item.waiting && item.sessionId !== sessionId).length + (state.tabs.mainWaiting && state.tabs.main !== sessionId && state.tabs.sessionId !== state.tabs.main ? 1 : 0);
  const tabBadges = useTabBadges(activeTab, { sessionId, items: state.items, replaying: state.replaying, replayRevision: state.replayRevision, requests: state.permissions.length + state.dialogs.length + backgroundWaiting });
  const activeTabRef = useRef(activeTab); activeTabRef.current = activeTab;
  const chatShown = useCallback(() => activeTabRef.current === 'chat', []);
  useHostFocusRedirect(inputRef, chatShown);
  useEffect(() => {
    const stopHiddenCapture = () => {
      if (native && latest.current.dictation && latest.current.dictation.state !== 'idle')
        send('dictation/set', { requestId: newId(), on: false });
    };
    if (activeTab !== 'chat') stopHiddenCapture();
    const onVisibility = () => { if (document.hidden) stopHiddenCapture(); };
    document.addEventListener('visibilitychange', onVisibility);
    window.addEventListener('pagehide', stopHiddenCapture);
    return () => {
      document.removeEventListener('visibilitychange', onVisibility);
      window.removeEventListener('pagehide', stopHiddenCapture);
    };
  }, [activeTab, state.dictation?.captureId, native]);
  const wasRequired = useRef(false);
  useEffect(() => {
    if (setupRequired) setTab('accounts'); else if (wasRequired.current) setTab('chat');
    wasRequired.current = setupRequired;
  }, [setupRequired]);
  const focused = useRef(false);
  /** The tab bar's arrow keys moved to Chat: the focus stays in the tab list (its own navigation) instead of going to the composer. */
  const chatByTabKeys = useRef(false);
  useEffect(() => {
    const byKeys = chatByTabKeys.current;
    chatByTabKeys.current = false;
    if (!connected || activeTab !== 'chat') { focused.current = false; return; }
    if (focused.current) return;
    focused.current = true;
    if (byKeys) return;
    const frame = window.requestAnimationFrame(() => inputRef.current?.focus());
    return () => { window.cancelAnimationFrame(frame); focused.current = false; };
  }, [connected, activeTab]);
  useEffect(() => {
    const requested = pendingPartnerFocus.current;
    if (!requested || state.tabs?.pendingAction || state.tabs?.sessionId !== sessionId || state.tabs?.partner.kind !== requested.kind
      || state.tabs?.partner.id !== requested.id || activeTab !== 'chat') return;
    pendingPartnerFocus.current = undefined;
    window.requestAnimationFrame(() => { if (activeTabRef.current === 'chat') inputRef.current?.focus(); });
  }, [state.tabs, sessionId, activeTab]);

  /** A message while a reply runs is queued, also a question sent from the code editor. */
  const sending = useRef(false); const queueSubmitting = useRef<string | undefined>(undefined);
  /** `toMain`: a request that belongs to the Default (POMP) tab's conversation (editor menu): from another tab it waits in the input while the page switches there. */
  const sendMessage = useCallback((text: string, attachments: WorkspaceFile[] = [], clearDraft = false, target?: SendTarget | null, preserveDraftTarget = false, includeEditorContext?: boolean, toMain = false): SendResult => {
    const current = latest.current;
    const currentBackend = backendId(current.session, current.backend);
    const allowed = capabilities(current.session, currentBackend);
    if (!allowed.editorContext && includeEditorContext === true) {
      dispatch({ type: 'localNotice', level: 'warning', message: makeTranslate(current.locale)('acp.noEditorContext') });
      return { ok: false, reason: 'notified' };
    }
    if (!allowed.nativeOmp && (target && target.kind !== 'main' || current.selectedTarget && target === undefined)) {
      dispatch({ type: 'localNotice', level: 'warning', message: makeTranslate(current.locale)('acp.noTargets') });
      return { ok: false, reason: 'notified' };
    }
    if (!allowed.nativeOmp && attachments.some(file => /\.(png|jpe?g|gif|webp|bmp)$/i.test(file.fullPath) ? !allowed.images : !allowed.editorContext)) {
      dispatch({ type: 'localNotice', level: 'warning', message: makeTranslate(current.locale)('acp.noAttachments') });
      return { ok: false, reason: 'notified' };
    }
    const destination = target === undefined ? current.selectedTarget : target ?? undefined;
    const queued = current.phase === 'running' || current.session?.isStreaming === true;
    if (queued && !allowed.queue) return { ok: false, reason: 'busy' };
    if (pendingSwitch.current || (!queued && sending.current) || queueSubmitting.current || current.replaying || current.phase === 'sending' || current.pendingNewSession !== undefined || allowed.nativeOmp && (current.permissions.length > 0 || current.dialogs.length > 0)) return { ok: false, reason: 'busy' };
    if (allowed.nativeOmp ? current.bridge.state !== 'running' : current.acp?.connected !== true || !current.acp.sessionId || current.acp.authRequired) return { ok: false, reason: 'hostUnavailable' };
    if (current.session?.recoveryStatus === 'restoring' || current.session?.recoveryStatus === 'failed') return { ok: false, reason: 'busy' };
    if (toMain && destination?.kind === 'main' && latestPartnerTab()) { showPomp(); return { ok: false, reason: 'busy' }; }
    const id = newId();
    const message = { clientMessageId: id, text, includeEditorContext: allowed.editorContext && (includeEditorContext ?? current.includeEditorContext),
      ...(attachments.length > 0 ? { attachments: attachments.map(file => ({ path: file.fullPath })) } : {}), ...(allowed.nativeOmp ? targetPayload(destination) : {}) };
    const rejection = promptRejection(text, { type: 'chat/send', payload: message }, attachments.map(file => file.fullPath));
    if (rejection) return { ok: false, ...rejection };
    if (queued) queueSubmitting.current = id; else sending.current = true;
    if (!send('chat/send', message)) { sending.current = false; queueSubmitting.current = undefined; return { ok: false, reason: 'hostUnavailable' }; }
    dispatch({ type: 'userSend', id, text, attachments, clearDraft, target: destination, preserveDraftTarget, queued });
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
  /**
   * `/steer`: while the agent works the text goes to it now (`chat/steer`), else it is an ordinary send. No bubble before the host's
   * `chat/steered`; until then the message is tracked like a queued send, so a refusal or a lost answer gives the text and files back.
   */
  const steerMessage = useCallback((text: string, attachments: WorkspaceFile[] = []): SendResult => {
    const current = latest.current;
    const allowed = capabilities(current.session, backendId(current.session, current.backend));
    if (!allowed.steer) return { ok: false, reason: 'hostUnavailable' };
    if (!allowed.nativeOmp && attachments.some(file => /\.(png|jpe?g|gif|webp|bmp)$/i.test(file.fullPath) ? !allowed.images : !allowed.editorContext)) {
      dispatch({ type: 'localNotice', level: 'warning', message: makeTranslate(current.locale)('acp.noAttachments') });
      return { ok: false, reason: 'notified' };
    }
    if (!(current.phase === 'running' || current.session?.isStreaming === true)) return sendMessage(text, attachments, true);
    if (allowed.nativeOmp ? current.bridge.state !== 'running' : current.acp?.connected !== true || !current.acp.sessionId || current.acp.authRequired) return { ok: false, reason: 'hostUnavailable' };
    if (pendingSwitch.current || current.replaying || current.pendingNewSession !== undefined || current.session?.recoveryStatus === 'restoring' || current.session?.recoveryStatus === 'failed') return { ok: false, reason: 'busy' };
    const message = { clientMessageId: newId(), text, includeEditorContext: allowed.editorContext && current.includeEditorContext, ...(attachments.length > 0 ? { attachments: attachments.map(file => ({ path: file.fullPath })) } : {}) };
    const rejection = promptRejection(text, { type: 'chat/send', payload: message }, attachments.map(file => file.fullPath));
    if (rejection) return { ok: false, ...rejection };
    if (!send('chat/steer', message)) return { ok: false, reason: 'hostUnavailable' };
    dispatch({ type: 'userSend', id: message.clientMessageId, text, attachments, clearDraft: true, queued: true, preserveDraftTarget: true });
    rememberPrompt(current.session?.workingDirectory ?? '', text);
    return { ok: true };
  }, [sendMessage]);
  /** Whether the side panel's topic has an answer on its way (or a question omp has not taken yet). */
  const btwRunning = (): boolean => { const panel = latest.current.btw; return !!panel && (panel.pending !== undefined || btwTurns(panel.record).at(-1)?.status === 'running'); };
  /** Stops only the side panel's own answer; the conversation's run is never touched. */
  const cancelBtw = (): void => { const panel = latest.current.btw; if (panel && btwRunning()) send('btw/cancel', panel.topicId ? { recordId: panel.topicId } : {}); };
  /** `/btw [question]`: open a new side topic; only a nonempty question starts an engine request. */
  const openBtw = (question: string): SendResult => {
    if (!native || latest.current.bridge.state !== 'running') return { ok: false, reason: 'hostUnavailable' };
    cancelBtw();
    const pending = question ? { requestId: newId(), question } : undefined;
    if (pending && !send('btw/ask', pending)) return { ok: false, reason: 'hostUnavailable' };
    dispatch({ type: 'btwOpen', pending, sessionId: latest.current.session?.sessionId });
    return { ok: true };
  };
  /** A follow-up in the panel's topic (a new topic when the first question never started one). */
  const askBtw = (question: string): boolean => {
    const panel = latest.current.btw;
    if (!panel) return false;
    const requestId = newId();
    if (!send('btw/ask', { requestId, question, ...(panel.topicId ? { recordId: panel.topicId } : {}) })) return false;
    dispatch({ type: 'btwAsk', requestId, question });
    return true;
  };
  const closeBtw = (): void => { cancelBtw(); dispatch({ type: 'btwClose' }); window.requestAnimationFrame(() => inputRef.current?.focus()); };
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
  const canSwitchTab = useCallback(() => {
    const current = latest.current;
    return !pendingSwitch.current && current.pendingNewSession === undefined && current.session?.recoveryStatus !== 'restoring';
  }, []);
  const newSession = useCallback((): boolean => {
    if (!canSwitchTab() || backendId(latest.current.session, latest.current.backend) === 'omp' && latest.current.bridge.state !== 'running') return false;
    const requestId = newId();
    pendingSwitch.current = requestId;
    if (!send('session/new', { requestId, backend: backendId(latest.current.session, latest.current.backend) })) { pendingSwitch.current = undefined; dispatch({ type: 'localNotice', level: 'error', message: makeTranslate(latest.current.locale)('error.hostUnavailable') }); return false; }
    dispatch({ type: 'newSessionRequested', requestId, fresh: true }); setTab('chat'); setListOpen(false); return true;
  }, [canSwitchTab]);
  const selectBackend = (selected: BackendId): void => {
    if (!canSwitchTab() || selected === backend) return;
    const requestId = newId(); pendingSwitch.current = requestId;
    if (!send('conversation/backend', { requestId, backend: selected })) {
      pendingSwitch.current = undefined; dispatch({ type: 'localNotice', level: 'error', message: translate('error.hostUnavailable') }); return;
    }
    dispatch({ type: 'newSessionRequested', requestId, backendSwitch: true }); setListOpen(false); setTab('chat');
  };
  const switchSession = useCallback((requestId: string, root: string, id: string): boolean => {
    if (!canSwitchTab()) return false;
    pendingSwitch.current = requestId;
    if (!send('session/history', { action: 'switch', requestId, root, id })) { pendingSwitch.current = undefined; return false; }
    dispatch({ type: 'newSessionRequested', requestId }); return true;
  }, [canSwitchTab]);
  /** A refusal or failure of a slash command (or of the conversation's rename and compaction): a warning in the chat, where the command was typed. */
  const commandNotice = useCallback((message: string) => dispatch({ type: 'localNotice', level: 'warning', message }), []);
  const history = useSessionHistory(workspaceKey, sessionId, connected && caps.history.list, state.locale, switchSession, commandNotice, caps.history, backend, state.pendingNewSession);
  const historyScope = useRef({ workspaceKey, sessionId });
  useEffect(() => {
    const before = historyScope.current; historyScope.current = { workspaceKey, sessionId };
    if (before.workspaceKey !== workspaceKey || (before.sessionId !== sessionId && !history.pendingId)) setListOpen(false);
  }, [workspaceKey, sessionId, history.pendingId]);
  const sessionBlocked = !connected || recovering || busy || newSessionPending || modal || state.session?.hasPendingAsyncWork === true || attention.terminal > 0 || state.pendingQueued.some(item => !item.failed);
  const tabBlocked = native && !connected || recovering || newSessionPending;
  const openList = () => { if (!caps.history.list) return; setTab('chat'); setListOpen(true); history.command('refresh'); };
  const closeList = () => { setListOpen(false); window.requestAnimationFrame(() => document.querySelector<HTMLButtonElement>('[data-testid="session-quick"]')?.focus()); };
  const openWork = (view?: RequestedWorkView) => { setWorkView(view ?? workView); setTab('work'); };
  const openRuns = () => { setAgentsView('running'); setTab('agents'); };
  /** A Tools view id (a chip, an id older builds used, or `hooks`) opens where it lives now: a Tools chip, or the Agents tab's Automation view for the project hooks. Other ids are ignored. */
  const openTools = (view: string) => {
    if (!native) return;
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
  /** A started workflow (Work or Tools) sends its task to the current conversation and opens Chat. */
  const continueWork = (task?: string) => {
    const instruction = state.locale.startsWith('ko') ? '현재 작업 계획을 확인하고 미완료 단계부터 이어서 진행해 주세요. 기존 변경과 검증 결과를 먼저 확인하고, 완료하지 않은 작업을 완료했다고 보고하지 마세요.' : 'Review the current work plan and continue from the unfinished step. Check existing changes and verification evidence first; do not report unverified work as complete.';
    const result = sendMessage(task ? instruction + '\n\n' + task : instruction, [], false, MAIN_TARGET); if (result.ok) setTab('chat');
  };
  /** Puts the text into the chat input (not sent) and shows Chat. */
  const insertIntoChat = (text: string) => { setTab('chat'); setListOpen(false); setInsertText(previous => ({ text, seq: (previous?.seq ?? 0) + 1 })); };
  const prefillSeq = state.prefill?.seq;
  // A target the editor menu chose survives the switch to the Default (POMP) tab. The switch clears the one-message target twice
  // (with its result, then with the new conversation's `session/info`, which may come in a later batch), so the target is chosen
  // again only once that conversation is the one on screen. `from`: the agent or team conversation the request came in.
  const prefillTarget = useRef<{ target: SendTarget; from: string | undefined } | undefined>(undefined);
  // After the switch (declared before the effect that starts one: in the render that starts it nothing is pending yet).
  useEffect(() => {
    if (newSessionPending) return;
    pendingSwitch.current = undefined;
    const waiting = prefillTarget.current;
    if (!waiting) return;
    // The switch did not happen (refused or failed): the target is still chosen where the user is.
    if (state.selectedTarget) { prefillTarget.current = undefined; return; }
    if (sessionId === waiting.from || latestPartnerTab()) return;
    prefillTarget.current = undefined;
    dispatch({ type: 'selectTarget', target: waiting.target });
  });
  useEffect(() => {
    if (prefillSeq === undefined) return;
    prefillTarget.current = undefined;
    setTab('chat'); setListOpen(false);
    const payload = state.prefill?.payload;
    if (payload?.target && latestPartnerTab()) prefillTarget.current = { target: payload.target, from: sessionId };
    if (payload?.send || payload?.target || payload?.pickTarget) showPomp();
  }, [prefillSeq]);
  const focusedPrefill = useRef<number | undefined>(undefined);
  useEffect(() => {
    if (prefillSeq === undefined || focusedPrefill.current === prefillSeq || activeTab !== 'chat' || modal) return;
    focusedPrefill.current = prefillSeq; window.requestAnimationFrame(() => inputRef.current?.focus());
  }, [prefillSeq, activeTab, modal]);
  const changeTab = (next: ShellTab, byKeys = false) => {
    if (setupRequired && !availableWhileRequired(next)) return;
    if (!native && (next === 'agents' || next === 'remote')) return;
    chatByTabKeys.current = byKeys && next === 'chat';
    if (next === 'work') openWork();
    else { setTab(next); if (native && next === 'agents' && connected) send('agents/command', { requestId: newId(), action: 'refresh' }); }
  };
  /** Says `message` in the chat and keeps the typed command in the input (see commandNotice). */
  const refuseCommand = (message: string): SendResult => { commandNotice(message); return { ok: false, reason: 'notified' }; };
  /** /rename and /compact: they wait only for a session request that changes something; the hook tells their own failures in the chat. */
  const historyCommand = (action: 'rename' | 'compact', value: string): SendResult => {
    if (!connected) return refuseCommand(translate('error.hostUnavailable'));
    if (history.busyFor(action)) return refuseCommand(translate('slash.historyBusy'));
    return history.command(action, value) ? { ok: true } : { ok: false, reason: 'notified' };
  };
  const handleCommand = (name: BuiltinCommand, args: string): SendResult => {
    if (!backendCommandAllowed(name, caps)) return refuseCommand(translate('acp.noCommand', { name }));
    switch (name) {
      case 'new':
        if (!canSwitchTab()) return refuseCommand(translate('slash.newBlocked'));
        // newSession tells a failed send itself.
        return newSession() ? { ok: true } : { ok: false, reason: 'notified' };
      case 'sessions': openList(); return { ok: true };
      case 'accounts': case 'login': case 'usage': changeTab('accounts'); return { ok: true };
      case 'work': openWork(); return { ok: true };
      case 'agents': changeTab('agents'); return { ok: true };
      case 'rename':
        if (!args) return refuseCommand(translate('slash.syntax', { reason: translate('slash.renameEmpty'), usage: translate('slash.args.rename') }));
        return historyCommand('rename', args);
      case 'compact': return historyCommand('compact', args.slice(0, 2000));
      case 'computer':
        // The button is hidden when the option is off: say so instead of a click nobody sees.
        if (state.computer?.policy !== 'perConversation') return refuseCommand(translate('slash.computerOff'));
        setComputerRequest(previous => ({ revision: (previous?.revision ?? 0) + 1 })); return { ok: true };
      case 'btw': return openBtw(args);
      case 'model': case 'thinking': case 'effort': case 'permissions': case 'agent': case 'team': case 'goal': case 'steer': case 'help': return { ok: true };
    }
  };
  const planTotal = state.session?.planTaskCount ?? 0;
  const planOpen = state.session?.planOpenCount ?? 0;
  const completed = planTotal - planOpen;
  const acpPermission = native ? undefined : state.acp?.permissions[0];

  const requestCard = permission
    ? <PermissionDialog key={permission.requestId} request={permission} position={state.permissionDone + 1} total={state.permissionDone + state.permissions.length} shortcuts={shortcuts} onRespond={respondPermission} onExpired={dropPermission} />
    : dialog
      ? <GenericDialog key={dialog.requestId} request={dialog} position={state.dialogDone + 1} total={state.dialogDone + state.dialogs.length} shortcuts={shortcuts} onRespond={respondDialog} onDraftChange={(requestId, text) => dispatch({ type: 'dialogDraft', requestId, text })} onExpired={dropDialog} />
      : acpPermission
        ? <AcpPermissionCard key={acpPermission.requestId} permission={acpPermission} total={state.acp?.permissions.length ?? 1} sessionId={state.acp?.sessionId} connected={state.acp?.connected === true} replies={state.acpReplies} />
        : native && goal.card
        ? <GoalCard key={goal.card.key} card={goal.card} root={workspaceKey} sessionId={sessionId ?? ''} busy={busy} onClose={goal.close} />
        : null;

  return <LocaleContext value={state.locale}><I18nContext value={translate}><KnowledgeProvider root={workspaceKey} sessionId={sessionId} connected={native && connected}><TeamsContext value={native ? state.agents?.teams ?? [] : []}><RolesContext value={native ? state.agents?.roles ?? [] : []}><MainRoleContext value={native ? state.agents?.main : undefined}><OpenPartnerContext value={native ? openPartner : undefined}><div className="app">
    <TabBar active={activeTab} onChange={changeTab} bridge={native ? state.bridge : { state: connected ? 'running' : 'failed' }} required={setupRequired} badges={tabBadges} accountAttention={accountAttention} inert={false} engine={native ? state.engine : null} native={native} />
    <main className="main">
      <section id="page-chat" className="tab-page chat-page" role="tabpanel" aria-labelledby="tab-chat" hidden={activeTab !== 'chat'} inert={activeTab !== 'chat' || undefined}>
        <SessionHeader session={state.session} history={history} disabled={sessionBlocked} newDisabled={tabBlocked} onNew={newSession} listOpen={listOpen} onToggleList={() => { if (listOpen) closeList(); else openList(); }}>
          <BackendPicker active={backend} state={state.backend} disabled={newSessionPending} onSelect={selectBackend} />
          <PartnerPicker native={native} key={workspaceKey} tabs={state.tabs && sameRoot(state.tabs.root, workspaceKey) ? state.tabs : null} agents={state.agents} replies={state.conversationReplies} sessionId={sessionId} disabled={tabBlocked} busy={busy || state.session?.hasPendingAsyncWork === true} lockedTitle={newSessionPending ? translate('partner.locked') : undefined} request={native ? partnerRequest : undefined} shortcuts={shortcuts} input={inputRef} onSwitch={switchTab} onRefreshRoster={() => { if (native && connected) send('agents/command', { requestId: newId(), action: 'refresh' }); }} />
        </SessionHeader>
        <div className="conversation-list-anchor">{listOpen && <div ref={listRef} id="conversation-list" className="conversation-list"><SessionHistory locale={state.locale} sessionId={sessionId} sessionName={state.session?.sessionName} partner={scopedTabs?.partner ?? { kind: 'main' }} history={history} disabled={sessionBlocked} newDisabled={tabBlocked} switchDisabled={tabBlocked} onNew={newSession} onOpened={closeList} onClose={closeList} /></div>}</div>
        {native ? <><ConnectionBanner bridge={state.bridge} onRestart={() => send('bridge/restart', {})} onOpenLog={() => send('log/open', {})} /><EngineNotice engine={state.engine} /></> : <AcpControls key={sessionId ?? backend} state={state.acp} backend={state.backend} replies={state.acpReplies} />}<UndeliveredStatusBanner />
        {(recovering || recoveryFailed) && (native || state.acp?.connected === true) && <div className={recoveryFailed ? 'banner error' : 'banner warning'} role="status" data-testid="session-recovery">
          {recoveryFailed ? <Icon name="error" size={14} /> : <span className="spinner" aria-hidden="true" />}
          <span className="banner-text">{state.session?.recoveryMessage || translate('recovery.checking')}</span>
          {native && recoveryFailed && connected && <span className="banner-actions">
            <button type="button" className="btn primary" onClick={() => send('session/recover', {})}>{translate('recovery.retry')}</button>
            <button type="button" className="btn" onClick={openList}>{translate('recovery.openHistory')}</button>
            <button type="button" className="btn" disabled={tabBlocked} onClick={newSession}>{translate('recovery.new')}</button>
          </span>}
        </div>}
        <Transcript controlsEnabled={native && conversationReady && !newSessionPending && !state.replaying} root={workspaceKey} sessionId={sessionId} items={state.items} busy={busy} replaying={state.replaying} replayRevision={state.replayRevision} replayTruncated={state.replayTruncated} canRetry={conversationReady && !busy && !newSessionPending && !modal} onRetry={retryTurn} request={requestCard} findEnabled={activeTab === 'chat'} shortcuts={shortcuts} />
        {(planOpen > 0 || native && (workCount > 0 || attention.hooks > 0)) && <div className="activity-chips" data-testid="activity-chips">
          {planOpen > 0 && <button className="activity-chip" type="button" data-testid="activity-plan" onClick={() => openWork('progress')}>{translate('work.view.progress')} {completed}/{planTotal}</button>}
          {native && (['review', 'codeReview', 'workflow', 'history'] as const).filter(view => attention[view] > 0).map(view => <button className="activity-chip" type="button" key={view} data-testid={`activity-${view}`} onClick={() => openWork(view)}>{translate(ACTIVITY_LABELS[view])} {attention[view]}</button>)}
          {native && attention.hooks > 0 && <button className="activity-chip" type="button" data-testid="activity-hooks" onClick={() => openTools('hooks')}>{translate('agents.view.hooks')} {attention.hooks}</button>}
        </div>}
        {native && state.btw && <BtwPanel panel={state.btw} name={partnerName ?? 'POMP'} connected={conversationReady} onAsk={askBtw} onClose={closeBtw} />}
        {native && <LiveAgentProgress disabled={!conversationReady || newSessionPending || state.replaying} sessionId={sessionId} promptId={state.activePromptId} items={state.items} />}
        {caps.queue && <ChatQueue state={caps.steer ? state.queue : { ...state.queue, items: state.queue.items.map(item => ({ ...item, canSteer: false })) }} replies={state.conversationReplies} sessionId={sessionId} disabled={!conversationReady || newSessionPending || modal} pendingSends={state.pendingQueued} onRecover={item => { dispatch({ type: 'recoverQueueSend', id: item.clientMessageId }); inputRef.current?.focus(); }} />}
        <Composer capabilities={caps} locale={state.locale} workspaceKey={workspaceKey} sessionId={sessionId} model={native ? state.session?.model : undefined} session={state.session} setup={state.setup} setupReplies={state.setupReplies} onManageAccounts={() => changeTab('accounts')} inputRef={inputRef} connected={conversationReady} busy={busy}
          queueBlocked={busy && !caps.queue || state.phase === 'sending' || state.replaying || state.pendingQueued.some(item => !item.failed)} requestPending={modal || activeTab !== 'chat'} newSessionPending={newSessionPending} draft={state.composer} dispatch={dispatch} includeEditorContext={caps.editorContext && state.includeEditorContext} onIncludeEditorContextChange={value => dispatch({ type: 'setIncludeEditorContext', value })} onSend={sendMessage} onSteer={steerMessage} onStop={() => { if (caps.stop) send('chat/stop', {}); }}
          commands={native ? state.commands.commands : state.acp?.commands ?? []} commandsHidden={native ? state.commands.hidden : undefined} onCommand={handleCommand} onGoal={native ? goal.open : undefined} skillsOff={goal.skillsOff} prefill={state.prefill} insertSkill={native ? insertSkill : undefined} insertText={insertText} basketRequest={basketRequest} basketTokens={native ? state.basketTokens : null} agents={native ? state.agents : null} shortcuts={shortcuts}
          composerPredictions={native && state.hello?.composerPredictions === true} wordCompletion={state.hello?.wordCompletion !== false} transcript={state.items} approvalMode={state.hello?.approvalMode} permissionModeReply={state.permissionModeReply} editorFollow={native ? state.editorFollow : null} computer={native ? state.computer : null} computerReply={state.computerReply} computerRequest={native ? computerRequest : undefined} dictation={native ? state.dictation : null} dictationReply={state.dictationReply} selectedTarget={native ? state.selectedTarget : undefined} onSelectTarget={target => dispatch({ type: 'selectTarget', target })} partnerName={partnerName} partner={scopedTabs?.partner} onOpenPartner={native ? openPartner : undefined} />
      </section>
      <section id="page-work" className="tab-page work-page" role="tabpanel" aria-labelledby="tab-work" hidden={activeTab !== 'work'} inert={activeTab !== 'work' || undefined}>
        <WorkTab native={native} locale={state.locale} plans={plans} workspaceKey={workspaceKey} sessionId={sessionId} connected={conversationReady} busy={busy} newSessionPending={newSessionPending} inert={activeTab !== 'work'} attention={attention} requestedView={workView} onViewChange={setWorkView} onContinue={continueWork} onSolve={text => {
          const result = sendMessage(text, [], false, undefined, true, false); if (result.ok) setTab('chat');
          return result;
        }} onOpenWorkflows={() => openTools('workflows')} />
      </section>
      <section id="page-agents" className="tab-page agents-page" role="tabpanel" aria-labelledby="tab-agents" hidden={activeTab !== 'agents'} inert={activeTab !== 'agents' || undefined}>
        {native && <AgentsPanel agents={state.agents} replies={state.agentsReplies} setup={state.setup} setupReplies={state.setupReplies} cmd={setupCmd} locale={state.locale} connected={connected} inert={activeTab !== 'agents'} sessionId={sessionId} view={agentsView} onViewChange={setAgentsView} onSelectTarget={openPartner} workspaceKey={workspaceKey} busy={busy} newSessionPending={newSessionPending} onShowPermissions={() => openTools('permissions')} />}
      </section>
      <section id="page-tools" className="tab-page" role="tabpanel" aria-labelledby="tab-tools" hidden={activeTab !== 'tools'} inert={activeTab !== 'tools' || undefined}>
        {native ? <ToolsTab active={activeTab === 'tools'} locale={state.locale} workspaceKey={workspaceKey} sessionId={sessionId} connected={connected} busy={busy} newSessionPending={newSessionPending} inert={activeTab !== 'tools'} requestedView={toolsView} onViewChange={setToolsView} onUseSkill={name => { setTab('chat'); setListOpen(false); setInsertSkill(previous => ({ name, seq: (previous?.seq ?? 0) + 1 })); }} rules={state.activeRules} knowledgeDraft={state.knowledgeDraft} onInsertText={insertIntoChat} /> : activeTab === 'tools' && <p className="hint" data-testid="acp-tools-note">{translate('acp.toolsPage')}</p>}
      </section>
      <section id="page-remote" className="tab-page" role="tabpanel" aria-labelledby="tab-remote" hidden={activeTab !== 'remote'} inert={activeTab !== 'remote' || undefined}>
        {native && activeTab === 'remote' && <RemotePanel setup={state.setup} replies={state.setupReplies} cmd={setupCmd} required={setupRequired} connected={connected} />}
      </section>
      <section id="page-accounts" className="tab-page" role="tabpanel" aria-labelledby="tab-accounts" hidden={activeTab !== 'accounts'} inert={activeTab !== 'accounts' || undefined}>
        {activeTab === 'accounts' && <div className="accounts-page"><BackendPicker field active={backend} state={state.backend} disabled={newSessionPending} onSelect={selectBackend} />
          {native ? <SetupPanel setup={state.setup} replies={state.setupReplies} cmd={setupCmd} sessionModel={state.session?.model} required={setupRequired} connected={connected} locale={state.locale} inert={false} onOpenRemote={() => changeTab('remote')} /> : <p className="hint" data-testid="acp-accounts-note">{translate('acp.accountsPage')}</p>}
        </div>}
      </section>
    </main>
  </div></OpenPartnerContext></MainRoleContext></RolesContext></TeamsContext></KnowledgeProvider></I18nContext></LocaleContext>;
}
