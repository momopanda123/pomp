import { useStatusReport } from '../statusBar';
import { Fragment, useCallback, useEffect, useId, useLayoutEffect, useMemo, useRef, useState, type Dispatch, type KeyboardEvent, type RefObject } from 'react';
import { mentionSelectionToken, MENTION_LIMIT, type ComposerState } from '../composerState';
import { COMPOSER_CONTROLS, focusControlOrInput, setComposerControlHidden, showAllComposerControls, useHiddenComposerControls } from '../composerControls';
import { newId } from '../format';
import { roleTitle } from '../bundledDescriptions';
import { useT, type MessageKey, type Translate } from '../i18n';
import { usableKnowledge } from '../knowledge';
import { useKnowledge } from '../knowledgeState';
import { promptRejection, type SendFailure, type SendResult } from '../limits';
import { send, APPROVAL_MODES, DEFAULT_SHORTCUTS, MAIN_TARGET, type AgentsStatePayload, type CommandInfo, type ComposerPrefill, type ApprovalMode, type PageShortcuts, type PermissionModeResult, type BackgroundAgentJob, type BackgroundAgentState, type BasketTokens, type ModelInfo, type SendTarget, type SessionInfoPayload, type WorkspaceFile } from '../protocol';
import type { EditorFollowState, UiToHost } from '../protocol';
import { matchesShortcut } from '../shortcuts';
import { useComposerPrediction } from '../useComposerPrediction';
import { ComposerGhost } from './ComposerGhost';
import { commandCatalog, isBuiltinCommand, LEGACY_COMMANDS, slashMatches, slashTakesReferences, type BuiltinCommand, type SlashMatch } from '../slashCommands';
import type { SetupReply, SetupState } from '../setup';
import type { Action } from '../state';
import { effectiveRoles, isRunnableTeam, targetKey, targetPayload } from '../teamsCache';
import { useWorkState } from '../work/store';
import { ConfirmPopover } from './ConfirmPopover';
import { markMenuHandled, useContextMenu, type ContextMenuItem } from './ContextMenu';
import { PickerPopover } from './ModelPickers';
import { plainControls, revealControls } from './RevealControls';
import { ContextBasket } from './ContextBasket';
import { DestinationAvatar } from './AgentsTables';
import { Icon } from './Icon';
import { ModelPickers } from './ModelPickers';
import { ContextMeter, FAST_REPLY_TIMEOUT_MS, QuotaMeter } from './ComposerMeters';
import { byName, matches as matchesQuery } from './ui';
import { promptHistory } from '../promptHistory';

const MAX_HEIGHT_PX = 200;
const STOP_FEEDBACK_MS = 4000;
// Popups inside the composer keep their own right-click and keyboard menus; the composer's control menu skips them.
const COMPOSER_POPUPS = '.context-popover, .context-breakdown, .picker-pop, .mention-popup, .quota-popover, .confirm-popover, .context-menu';
const MENTION_DEBOUNCE_MS = 150;

interface ComposerProps {
  locale: string;
  workspaceKey?: string;
  sessionId?: string; model?: ModelInfo;
  /** What the model and thinking-level pickers show and change. */
  session: SessionInfoPayload | null;
  setup: SetupState | null;
  setupReplies: SetupReply[];
  /** Opens the account panel (offered at the foot of the model picker). */
  onManageAccounts: () => void;
  inputRef: RefObject<HTMLTextAreaElement | null>;
  connected: boolean;
  composerPredictions?: boolean;
  busy: boolean;
  queueBlocked?: boolean;
  insertSkill?: { name: string; seq: number };
  /** Text the user chose elsewhere (a saved prompt or note) to put at the caret. */
  insertText?: { text: string; seq: number };
  basketRequest?: number;
  /** The latest workspace/basketTokens reply (what the next message adds beyond its text): the context bar's hatched part. */
  basketTokens?: BasketTokens | null;
  requestPending: boolean;
  newSessionPending: boolean;
  includeEditorContext: boolean;
  onIncludeEditorContextChange: (value: boolean) => void;
  draft: ComposerState;
  dispatch: Dispatch<Action>;
  /** `target` undefined: the chosen one-message target (else the partner); null: the tab's partner, whatever is chosen. `toMain`: an editor
   *  request of the main conversation (the view moves to Default (POMP) once it is accepted). */
  onSend: (text: string, attachments: WorkspaceFile[], clearDraft?: boolean, target?: SendTarget | null, preserveDraftTarget?: boolean, includeEditorContext?: boolean, toMain?: boolean) => SendResult;
  onStop: () => void;
  commands: CommandInfo[];
  /** Commands the host left out because the list passed its message size. */
  commandsHidden?: number;
  onCommand: (name: BuiltinCommand, args: string) => SendResult;
  /** `/goal <text>`: open the goal card for that text (nothing starts before its answer). */
  onGoal?: (text: string) => void;
  /** Skills the Tools tab switched off: the command list leaves them out. */
  skillsOff?: readonly string[];
  /** The latest request of the code editor's POMP menu (composer/prefill). */
  prefill?: { payload: ComposerPrefill; seq: number } | null;
  agents?: AgentsStatePayload | null;
  /** The page's shortcuts (host/hello); absent = DEFAULT_SHORTCUTS. */
  shortcuts?: PageShortcuts;
  /** omp's approval mode in effect (host/hello), shown and changed by the permission-mode chip. */
  approvalMode?: ApprovalMode;
  /** The latest permission/modeResult. */
  permissionModeReply?: PermissionModeResult | null;
  editorFollow?: EditorFollowState | null;
  selectedTarget?: SendTarget;
  onSelectTarget: (target?: SendTarget) => void;
  /** The active partner tab's name (undefined = Default (POMP)): where a message without a one-message target goes. */
  partnerName?: string;
  /** The active partner tab's agent or team (undefined = Default (POMP)), for its picture on the send-target chip. */
  partner?: { kind: 'main' | 'agent' | 'team'; id?: string };
  /** `/agent name` and `/team id` without a message open that partner tab instead of a one-message target. */
  onOpenPartner?: (target: SendTarget) => void;
}

function failureText(t: Translate, failure: SendFailure): string | null {
  switch (failure.reason) {
    case 'tooLarge':
      return t('composer.tooLarge');
    case 'tooLargeEncoded':
      return t('composer.tooLargeEncoded');
    case 'tooManyChars':
      return t('dialog.charLimit');
    case 'hostUnavailable':
      return t('composer.sendFailed');
    case 'busy':
      return null;
  }
}

function FilePath({ path }: { path: string }) {
  const split = Math.max(path.lastIndexOf('/'), path.lastIndexOf('\\')) + 1;
  return <><span className="file-directory">{revealControls(path.slice(0, split))}</span><strong>{revealControls(path.slice(split))}</strong></>;
}

function LiveEditorControls({ state, disabled, shortcuts, inputRef, t }: { state?: EditorFollowState | null; disabled: boolean; shortcuts: PageShortcuts; inputRef: RefObject<HTMLTextAreaElement | null>; t: Translate }) {
  const [pending, setPending] = useState<string | null>(null);
  const [error, setError] = useState<string | null>(null);
  useEffect(() => {
    if (pending && state?.requestId === pending) {
      setPending(null);
      setError(state.error || null);
    }
  }, [state, pending]);
  useEffect(() => {
    if (!pending) return;
    const timer = window.setTimeout(() => { setPending(null); setError(t('setup.noReply')); }, FAST_REPLY_TIMEOUT_MS);
    return () => window.clearTimeout(timer);
  }, [pending, t]);
  const unavailable = !state || disabled || pending !== null;
  const request = useCallback((action: Omit<UiToHost['ui/editorFollow'], 'requestId'>) => {
    if (unavailable) return;
    inputRef.current?.focus({ preventScroll: true });
    const requestId = newId();
    setError(null); setPending(requestId);
    if (!send('ui/editorFollow', { ...action, requestId })) {
      setPending(null); setError(t('error.hostUnavailable'));
    }
  }, [unavailable, inputRef, t]);
  useEffect(() => {
    const onKeyDown = (event: globalThis.KeyboardEvent): void => {
      if (unavailable || event.defaultPrevented || document.querySelector('.overlay .dialog')) return;
      const input = inputRef.current;
      if (!input || input.closest('[hidden], [inert]')) return;
      // Other explicit inputs (search, rename, settings, terminal) retain their own keyboard and focus.
      const target = event.target;
      if (target instanceof Element && target !== input && target.closest('input, textarea, select, [contenteditable]:not([contenteditable="false"]), [role="textbox"], .xterm')) return;
      const live = matchesShortcut(event, shortcuts.liveFollow);
      if (!live && !matchesShortcut(event, shortcuts.liveBookmarks)) return;
      event.preventDefault(); event.stopPropagation();
      request(live ? { action: 'set', live: !state?.live } : { action: 'set', bookmarks: !state?.bookmarks });
    };
    window.addEventListener('keydown', onKeyDown);
    return () => window.removeEventListener('keydown', onKeyDown);
  }, [unavailable, inputRef, shortcuts.liveFollow, shortcuts.liveBookmarks, state?.live, state?.bookmarks, request]);
  // Named like the other composer buttons; the shortcut is in aria-keyshortcuts (and in the screen guide).
  const liveLabel = t('composer.live');
  const bookmarksLabel = t('composer.bookmarks');
  // A failed Live/bookmark change is reported in the VS status bar, not between the buttons.
  useStatusReport(error);
  return <span className="live-editor-controls" role="group" aria-label={t('composer.liveControls')}>
    <button type="button" className="icon-btn live-editor-toggle" data-testid="live-bookmarks" aria-pressed={state?.bookmarks === true} aria-label={bookmarksLabel} aria-keyshortcuts={shortcuts.liveBookmarks.replace(/^Ctrl\+/, 'Control+').replace(/\bEsc$/, 'Escape') || undefined} disabled={unavailable}
      title={bookmarksLabel} onMouseDown={event => event.preventDefault()} onClick={() => request({ action: 'set', bookmarks: !state?.bookmarks })}><Icon name="bookmark" size={14} /></button>
    <button type="button" className="icon-btn live-editor-toggle" data-testid="live-follow" aria-pressed={state?.live === true} aria-label={liveLabel} aria-keyshortcuts={shortcuts.liveFollow.replace(/^Ctrl\+/, 'Control+').replace(/\bEsc$/, 'Escape') || undefined} disabled={unavailable}
      title={liveLabel} onMouseDown={event => event.preventDefault()} onClick={() => request({ action: 'set', live: !state?.live })}><Icon name="live" size={14} /></button>
  </span>;
}

export function Composer({ locale, workspaceKey, sessionId, model, session, setup, setupReplies, onManageAccounts, inputRef, connected, composerPredictions = false, busy, queueBlocked = false, insertSkill, insertText, basketRequest = 0, basketTokens, requestPending, newSessionPending, includeEditorContext, onIncludeEditorContextChange, draft, dispatch, onSend, onStop, commands, commandsHidden, onCommand, onGoal, skillsOff, prefill, agents, shortcuts = DEFAULT_SHORTCUTS, approvalMode, permissionModeReply, editorFollow, selectedTarget, onSelectTarget, partnerName, partner, onOpenPartner }: ComposerProps) {
  const t = useT();
  const focusInput = useCallback(() => inputRef.current?.focus(), [inputRef]);
  const [failure, setFailure] = useState<SendFailure | null>(null);
  const composing = useRef(false);
  const latest = useRef(draft);
  latest.current = draft;
  const popupId = useId();
  const slashId = useId();
  const noticeId = useId();
  const [inputFocused, setInputFocused] = useState(false);
  const [caret, setCaret] = useState({ start: draft.text.length, end: draft.text.length });
  const [slashDismissed, setSlashDismissed] = useState<string | null>(null);
  const [slashSelected, setSlashSelected] = useState(0);
  const [helpOpen, setHelpOpen] = useState(false);
  const [basketOpen, setBasketOpen] = useState(false);
  const basketRef = useRef<HTMLDivElement>(null);
  const basketButton = useRef<HTMLButtonElement>(null);
  const basketPop = useRef<HTMLDivElement>(null);
  const focusBasketOnOpen = useRef(false);
  const basketByKey = useRef(false);
  const basketComposing = useRef(false);
  useLayoutEffect(() => {
    if (!basketOpen || !focusBasketOnOpen.current) return;
    focusBasketOnOpen.current = false;
    const visible = (element: HTMLElement) => element.getClientRects().length > 0 && !element.closest('[hidden], [inert]');
    // Opened from the keyboard: the first attached file (its menu opens with the context-menu key).
    const file = basketByKey.current ? [...(basketPop.current?.querySelectorAll<HTMLElement>('[data-attachment-menu]') ?? [])].find(visible) : undefined;
    const control = file ?? [...(basketPop.current?.querySelectorAll<HTMLElement>('button:not(:disabled), input:not(:disabled), select:not(:disabled), textarea:not(:disabled), summary, [tabindex="0"]') ?? [])]
      .find(visible);
    (control ?? basketPop.current)?.focus({ preventScroll: true });
  }, [basketOpen]);
  const [targetOpen, setTargetOpen] = useState(false);
  /** The send-target picker's search (shown past ten entries); empty again whenever the picker closes. */
  const [targetQuery, setTargetQuery] = useState('');
  useEffect(() => { if (!targetOpen) setTargetQuery(''); }, [targetOpen]);
  const targetRef = useRef<HTMLDivElement>(null);
  const targetButton = useRef<HTMLButtonElement>(null);
  // The permission-mode chip: omp's approval mode, chosen from a short list; "all automatic" asks first.
  const permissionMode: ApprovalMode = approvalMode ?? 'alwaysAsk';
  const [permissionOpen, setPermissionOpen] = useState(false);
  const [permissionConfirm, setPermissionConfirm] = useState(false);
  const [permissionPending, setPermissionPending] = useState<string | null>(null);
  const permissionPendingRef = useRef<string | null>(null);
  const [permissionError, setPermissionError] = useState<string | null>(null);
  const permissionRef = useRef<HTMLDivElement>(null);
  const permissionButton = useRef<HTMLButtonElement>(null);
  const permissionDisabled = !connected || busy || requestPending || newSessionPending || permissionPending !== null;
  const [targetError, setTargetError] = useState<string | null>(null);
  // A one-message target that was cleared because it is gone, and where the next message goes now (set by the effect below).
  const [targetLost, setTargetLost] = useState<string | null>(null);
  const lostAtText = useRef('');
  const background = useWorkState('background') as BackgroundAgentState | undefined;
  const runnableTeams = useMemo(() => agents?.teams.filter(team => isRunnableTeam(team, agents)) ?? [], [agents]);
  const runnableRoles = useMemo(() => effectiveRoles(agents?.roles ?? []).filter(role => !role.error), [agents?.roles]);
  // The running members the picker offers are the live ones of this conversation. A snapshot that is only temporarily not live ('unavailable' after a
  // failed get_subagents, 'loading', none yet) keeps the last live list, so a member chosen as the target is not dropped by a hiccup; 'archived'
  // (disconnected) or another conversation empties it.
  const knownInstances = useRef<{ scope: string; jobs: BackgroundAgentJob[] }>({ scope: '', jobs: [] });
  const instances = useMemo(() => {
    const scope = `${workspaceKey ?? ''}\n${sessionId ?? ''}`;
    if (background?.state === 'live' && background.sessionId === sessionId) knownInstances.current = { scope, jobs: background.jobs };
    else if (background?.state === 'archived' || knownInstances.current.scope !== scope) knownInstances.current = { scope, jobs: [] };
    return knownInstances.current.jobs;
  }, [background, sessionId, workspaceKey]);
  const destinations = useMemo<{ target: SendTarget; name: string; label: string; chip?: string; title: string; groupKey?: string; group?: string; avatar: { kind: 'main' | 'agent' | 'team'; id?: string } }[]>(() => [
    ...[...runnableTeams].sort((a, b) => byName(a.title || a.id, b.title || b.id)).map(team => ({ target: { kind: 'team', id: team.id } as SendTarget, name: team.title || team.id, label: team.title || team.id, title: team.id, groupKey: 'teams', group: t('composer.targetTeams'), avatar: { kind: 'team' as const, id: team.id } })),
    ...[...runnableRoles].sort((a, b) => byName(roleTitle(a, t), roleTitle(b, t))).map(role => ({ target: { kind: 'agent', id: role.name } as SendTarget, name: roleTitle(role, t), label: roleTitle(role, t), title: role.name, groupKey: 'agents', group: t('composer.targetAgents'), avatar: { kind: 'agent' as const, id: role.name } })),
    ...instances.map(job => {
      const title = roleTitle(runnableRoles.find(role => role.name === job.agent), t, job.agent);
      const name = `${title} #${job.id.slice(-8)}`;
      const status = t(`agents.status.${['pending', 'running', 'completed', 'failed', 'error', 'aborted'].includes(job.status) ? job.status : 'unknown'}` as MessageKey);
      return { target: { kind: 'instance', id: job.id } as SendTarget, name, label: `${name} · ${status}`, chip: name, title: `${job.agent} · ${job.id}`, groupKey: 'instances', group: t('composer.targetInstances'), avatar: { kind: 'agent' as const, id: job.agent } };
    }),
  ], [runnableTeams, runnableRoles, instances, t]);
  // The list: teams, agents, then this conversation's running members; each group by name, except that the row of the conversation the
  // user is in (POMP in its own tab, the agent or team of an agent/team tab) heads its group, tinted: choosing it clears the one-message
  // target, so the next message continues this conversation (never a subagent run of itself). POMP heads the agents in its own tab and
  // in a team tab, and comes right under the agent in that agent's tab. Running members of the tab's own role are left out.
  const partnerTab = partner?.id && partner.kind !== 'main' ? { kind: partner.kind, id: partner.id } : undefined;
  const listed = useMemo(() => {
    const own = (item: (typeof destinations)[number]) => !partnerTab ? item.target.kind === 'main'
      : partnerTab.kind === 'team' ? item.target.kind === 'team' && item.target.id.toLowerCase() === partnerTab.id.toLowerCase()
      : item.target.kind === 'agent' && item.target.id === partnerTab.id;
    const main: (typeof destinations)[number] = { target: MAIN_TARGET, name: t('partner.main'), label: t('partner.main'), chip: 'POMP', title: 'POMP', groupKey: 'agents', group: t('composer.targetAgents'), avatar: { kind: 'main' } };
    const ownFirst = (items: (typeof destinations)[number][]) => [...items.filter(own), ...items.filter(item => !own(item))];
    const agents = ownFirst(destinations.filter(item => item.target.kind === 'agent'));
    const ownAgent = agents.length > 0 && own(agents[0]!);
    return [
      ...ownFirst(destinations.filter(item => item.target.kind === 'team')),
      ...(ownAgent ? [agents[0]!, main, ...agents.slice(1)] : [main, ...agents]),
      ...destinations.filter(item => item.target.kind === 'instance' && !(partnerTab?.kind === 'agent' && item.avatar.id === partnerTab.id)),
    ].map(item => ({ ...item, own: own(item) }));
  }, [destinations, partnerTab?.kind, partnerTab?.id, t]);
  const choices = useMemo(() => listed.filter(item => !item.own), [listed]);
  const hasTargets = choices.length > 0;
  const selectedDestination = choices.find(item => targetKey(item.target) === targetKey(selectedTarget));
  // Where a message without a one-message target goes: the active partner tab (named when a lost target is reported).
  const partnerLabel = partnerName || t('partner.main');
  // An editor menu request that could not go out at once (a reply was running, or a draft was there) still belongs to POMP
  // when the user sends it later, whatever partner tab is active then.
  const deferredMain = useRef<string | null>(null);
  useEffect(() => { deferredMain.current = null; }, [sessionId]);
  // A one-message target the user picks afterwards wins: the chip and the send must agree.
  const mainPending = !selectedDestination && deferredMain.current !== null && draft.text.includes(deferredMain.current);
  // The chip names only a target other than the conversation the user is in; without one it is just the send-target button (a picked POMP
  // in its own tab, or this tab's own partner, would read as a message to itself). A deferred editor request is POMP's: a target only in an
  // agent/team tab.
  const chosen = selectedDestination ?? (mainPending && partnerTab ? choices.find(item => item.target.kind === 'main') : undefined);
  const targetLabel = chosen ? (chosen.chip ?? chosen.label) : t('composer.sendTarget');
  const targetTitle = chosen ? t('composer.targetPartnerTitle', { name: chosen.label }) : t('composer.sendTarget');
  // What the chosen target is called, to name it if it disappears later.
  const selectedName = useRef<{ key: string; name: string }>(undefined);
  if (selectedDestination) selectedName.current = { key: targetKey(selectedTarget), name: selectedDestination.name };
  // The notice of a lost target goes away with the next edit, send or target change (a target that does not resolve is not a change).
  useEffect(() => { if (targetLost !== null && draft.text !== lostAtText.current) setTargetLost(null); }, [draft.text]);
  useEffect(() => { if (selectedDestination) setTargetLost(null); }, [selectedTarget]);
  useEffect(() => { setTargetLost(null); }, [partnerName, sessionId]); // it names where the next message goes: another partner tab or conversation makes it stale
  useEffect(() => {
    if (!selectedTarget || selectedDestination) return;
    onSelectTarget(undefined);
    // POMP in its own tab, the tab's own partner, or a running member of the tab's own role (left out of an agent tab's list on purpose)
    // is where the message goes anyway: nothing to tell.
    if (selectedTarget.kind === 'main' || (partnerTab && selectedTarget.kind === partnerTab.kind && selectedTarget.id.toLowerCase() === partnerTab.id.toLowerCase())) return;
    if (partnerTab?.kind === 'agent' && selectedTarget.kind === 'instance' && destinations.some(item => item.target.kind === 'instance' && item.target.id === selectedTarget.id && item.avatar.id === partnerTab.id)) return;
    // Cleared without a word, the next message would go to a partner the user did not pick: name the target that went and where the message goes now.
    const remembered = selectedName.current?.key === targetKey(selectedTarget) ? selectedName.current.name : undefined;
    const name = remembered ?? (selectedTarget.kind === 'team' ? agents?.teams.find(team => team.id === selectedTarget.id)?.title || selectedTarget.id
      : selectedTarget.kind === 'agent' ? roleTitle(agents?.roles.find(role => role.name === selectedTarget.id), t, selectedTarget.id) : `#${selectedTarget.id.slice(-8)}`);
    lostAtText.current = draft.text;
    setTargetLost(t('composer.targetLost', { name, partner: partnerLabel }));
  }, [selectedTarget, selectedDestination, onSelectTarget]);
  useEffect(() => { if (!connected || requestPending) setTargetOpen(false); }, [connected, requestPending]);

  useEffect(() => {
    if (!targetOpen) return;
    const pointer = (event: PointerEvent) => {
      if (event.target instanceof Node && !targetRef.current?.contains(event.target)) setTargetOpen(false);
    };
    document.addEventListener('pointerdown', pointer, true);
    return () => document.removeEventListener('pointerdown', pointer, true);
  }, [targetOpen]);
  useEffect(() => {
    if (!permissionOpen) return;
    const pointer = (event: PointerEvent) => {
      if (event.target instanceof Node && !permissionRef.current?.contains(event.target)) setPermissionOpen(false);
    };
    document.addEventListener('pointerdown', pointer, true);
    return () => document.removeEventListener('pointerdown', pointer, true);
  }, [permissionOpen]);
  useEffect(() => { if (permissionDisabled) { setPermissionOpen(false); setPermissionConfirm(false); } }, [permissionDisabled]);
  useEffect(() => { if (targetOpen) { setPermissionOpen(false); setPermissionConfirm(false); } }, [targetOpen]);
  // Only the correlated reply answers the request; a changed mode itself arrives later in a new host/hello (the engine restarts).
  useEffect(() => {
    if (!permissionModeReply || permissionModeReply.requestId !== permissionPendingRef.current) return;
    permissionPendingRef.current = null; setPermissionPending(null);
    setPermissionError(permissionModeReply.ok ? null : permissionModeReply.error || t('error.hostUnavailable'));
  }, [permissionModeReply, t]);
  useEffect(() => {
    if (!permissionPending) return;
    const timer = window.setTimeout(() => {
      permissionPendingRef.current = null; setPermissionPending(null); setPermissionError(t('setup.noReply'));
    }, FAST_REPLY_TIMEOUT_MS);
    return () => window.clearTimeout(timer);
  }, [permissionPending, t]);
  useEffect(() => { if (!connected) { permissionPendingRef.current = null; setPermissionPending(null); setPermissionError(null); } }, [connected]);
  const setApprovalMode = (mode: ApprovalMode): void => {
    if (permissionDisabled) return;
    const requestId = newId();
    permissionPendingRef.current = requestId; setPermissionPending(requestId); setPermissionError(null);
    if (!send('permission/mode', { requestId, mode })) {
      permissionPendingRef.current = null; setPermissionPending(null); setPermissionError(t('error.hostUnavailable'));
    }
  };
  const closePermission = (): void => { setPermissionOpen(false); setPermissionConfirm(false); focusControlOrInput(permissionButton.current, inputRef.current, { preventScroll: true }); };
  const closePermissionPopups = (): void => { setPermissionOpen(false); setPermissionConfirm(false); };
  const choosePermission = (mode: ApprovalMode): void => {
    if (mode === permissionMode) { closePermission(); return; }
    if (mode === 'yolo') { setPermissionOpen(false); setPermissionConfirm(true); return; }
    closePermission(); setApprovalMode(mode);
  };
  const [pickerRequest, setPickerRequest] = useState<{ kind: 'model' | 'thinking'; revision: number }>();
  const catalog = useMemo(() => commandCatalog(commands, t, skillsOff), [commands, t, skillsOff]);
  const matches = useMemo(() => slashMatches(draft.text, caret.start, caret.end, catalog), [draft.text, caret, catalog]);
  const slash = connected && !draft.composing && !requestPending && (helpOpen || slashDismissed !== draft.text) ? (helpOpen ? catalog.map(command => ({ ...command, value: `/${command.name} ` })) : matches) : null;
  const slashIndex = slash?.length ? Math.min(slashSelected, slash.length - 1) : 0;
  useEffect(() => { setSlashSelected(0); }, [draft.text, helpOpen]);
  useEffect(() => { if (slash?.length) document.getElementById(`${slashId}-${slashIndex}`)?.scrollIntoView({ block: 'nearest' }); }, [slashId, slashIndex, slash?.length]);
  useEffect(() => { if (requestPending) { setBasketOpen(false); setHelpOpen(false); setSlashDismissed(draft.text); } }, [requestPending]);
  useEffect(() => {
    if (!basketOpen) return;
    const pointer = (event: PointerEvent) => { if (event.target instanceof Node && !basketRef.current?.contains(event.target)) setBasketOpen(false); };
    document.addEventListener('pointerdown', pointer, true);
    return () => document.removeEventListener('pointerdown', pointer, true);
  }, [basketOpen]);
  const consumedSkill = useRef<number | undefined>(undefined);
  const lastSkill = useRef<string | undefined>(undefined);
  useEffect(() => {
    if (!insertSkill || consumedSkill.current === insertSkill.seq || requestPending || draft.composing) return;
    consumedSkill.current = insertSkill.seq;
    // A skill name is a command token, not arbitrary host-controlled input or instructions.
    if (!/^[a-zA-Z0-9_.-]{1,128}$/u.test(insertSkill.name)) return;
    // The plain name, which the host maps to the skill; the engine name only where the palette lists the skill under it (a real command has the plain name).
    const listed = catalog.filter(command => command.group === 'skills').map(command => command.name);
    const entry = listed.includes(`skill:${insertSkill.name}`) ? `skill:${insertSkill.name}` : insertSkill.name;
    const token = `/${entry}`;
    // A new choice replaces the previous skill token (either form, or the one this field inserted last) rather than creating an ambiguous multi-skill message.
    const forms = ['skill:[a-zA-Z0-9_.-]{1,128}', ...new Set([...listed, ...(lastSkill.current ? [lastSkill.current] : [])].map(form => form.replace(/[.*+?^${}()|[\]\\]/gu, '\\$&')))];
    const previous = new RegExp(`(?<!\\S)/(?:${forms.join('|')})(?=\\s|$)[ \\t]?`, 'gu');
    lastSkill.current = entry;
    const oldSkills = [...draft.text.matchAll(previous)];
    const offset = (position: number) => Math.max(0, position - oldSkills.reduce((removed, match) => removed + Math.max(0, Math.min(position - match.index, match[0].length)), 0));
    const source = draft.text.replace(previous, '');
    const before = source.slice(0, offset(caret.start)); const after = source.slice(offset(caret.end));
    const insertion = `${before && !/\s$/u.test(before) ? ' ' : ''}${token}${after.startsWith(' ') ? '' : ' '}`;
    const next = before + insertion + after; const position = before.length + insertion.length;
    dispatch({ type: 'composerEdit', text: next, caret: position, selectionEnd: position });
    setCaret({ start: position, end: position }); setSlashDismissed(next);
    window.requestAnimationFrame(() => { inputRef.current?.focus(); inputRef.current?.setSelectionRange(position, position); });
  }, [insertSkill, requestPending, draft.composing]);
  const consumedText = useRef<number | undefined>(undefined);
  useEffect(() => {
    // A saved prompt or note the user chose: inserted at the caret as text, never sent automatically.
    if (!insertText || consumedText.current === insertText.seq || requestPending || draft.composing) return;
    consumedText.current = insertText.seq;
    const value = insertText.text.slice(0, 24000);
    if (!value.trim()) return;
    const before = draft.text.slice(0, caret.start); const after = draft.text.slice(caret.end);
    const insertion = `${before && !before.endsWith('\n') ? '\n' : ''}${value}${after && !after.startsWith('\n') ? '\n' : ''}`;
    const next = before + insertion + after; const position = before.length + insertion.length;
    dispatch({ type: 'composerEdit', text: next, caret: position, selectionEnd: position });
    setCaret({ start: position, end: position });
    window.requestAnimationFrame(() => { inputRef.current?.focus(); inputRef.current?.setSelectionRange(position, position); });
  }, [insertText, requestPending, draft.composing]);
  const consumedBasket = useRef(0);
  useEffect(() => {
    if (!basketRequest || consumedBasket.current === basketRequest || requestPending) return;
    consumedBasket.current = basketRequest; setBasketOpen(true);
  }, [basketRequest, requestPending]);
  const text = draft.text.trim();
  const sendPaths = useMemo(() => draft.attachments.map(file => file.fullPath), [draft.attachments]);
  const rejection = useMemo(() => promptRejection(draft.text, {
    type: 'chat/send', payload: { clientMessageId: 'draft', text, includeEditorContext, ...targetPayload(selectedTarget), attachments: sendPaths.map(path => ({ path })) },
  }, sendPaths), [text, draft.text, sendPaths, includeEditorContext, selectedTarget]);
  const canSend = connected && !queueBlocked && !requestPending && !newSessionPending && !draft.composing && (text.length > 0 || draft.attachments.length > 0);
  const failureMessage = failure ? failureText(t, failure) : rejection ? failureText(t, rejection) : null;
  // The composer's notices go to the VS status bar (with errors in the POMP log), not as text between its controls.
  useStatusReport(failureMessage);
  useStatusReport(draft.fileError, 'warning');
  useStatusReport(targetLost, 'warning');
  useStatusReport(targetError);
  useStatusReport(permissionError);
  const mention = connected && !draft.composing ? draft.mention : null;
  const { prediction, discard: discardPrediction } = useComposerPrediction({
    inputRef, enabled: composerPredictions, sessionId, text: draft.text, caret, focused: inputFocused,
    suppressed: !connected || requestPending || newSessionPending || queueBlocked || draft.composing || slash !== null || mention !== null,
  });
  const mentionTargets = useMemo(() => {
    if (!mention) return [];
    const query = mention.query.toLocaleLowerCase();
    // Agents and teams by name (not POMP, not running members, not the tab's own partner).
    return choices.filter(item => item.target.kind !== 'instance' && item.target.kind !== 'main' && `${item.target.id} ${item.label}`.toLocaleLowerCase().includes(query)).slice(0, 8);
  }, [mention?.query, choices]);
  const mentionCount = mentionTargets.length + (mention?.files.length ?? 0);
  const mentionIndex = mentionCount ? Math.min(mention?.selected ?? 0, mentionCount - 1) : 0;

  // Only the query/range changes start a timer. Receiving results or moving the highlighted row does not re-query.
  const mentionStart = mention?.start;
  const mentionQuery = mention?.query;
  useEffect(() => {
    if (!connected || draft.composing || mentionStart === undefined || mentionQuery === undefined) return;
    const timer = window.setTimeout(() => {
      const requestId = newId();
      dispatch({ type: 'mentionRequested', requestId, start: mentionStart, query: mentionQuery });
      if (!send('workspace/findFiles', { requestId, query: mentionQuery, limit: MENTION_LIMIT })) {
        dispatch({ type: 'workspaceFailed', requestId, message: t('composer.filesFailed') });
      }
    }, MENTION_DEBOUNCE_MS);
    return () => window.clearTimeout(timer);
  }, [connected, draft.composing, mentionStart, mentionQuery, dispatch, t]);

  useEffect(() => {
    const active = document.getElementById(`${popupId}-${mentionIndex}`);
    active?.scrollIntoView({ block: 'nearest' });
  }, [popupId, mentionIndex, mentionCount]);

  // Page-local chords (not VS bindings): the destination picker (default Ctrl+Alt+T) and the permission-mode picker (default Alt+Q) toggle
  // their popups; the previous / next target chords (default Alt+, / Alt+.) step through the picker's agents and teams, past either end back to
  // the chat's partner; the basket chord (default Alt+;) opens the context basket on its first file or closes it, and the editor-context chord
  // (default Alt+') switches "include editor context". Inert while composing text, while a mention/slash popup owns the keyboard, while a
  // modal owns it, or while the control cannot open.
  useEffect(() => {
    const targetChord = hasTargets ? shortcuts.sendTargetPicker : '';
    const stepChords = choices.length > 0 ? [shortcuts.previousTarget, shortcuts.nextTarget] : ['', ''];
    const permissionChord = shortcuts.permissionMode;
    const basketChord = shortcuts.contextBasket, contextChord = shortcuts.editorContext;
    if (!targetChord && !permissionChord && !stepChords[0] && !stepChords[1] && !basketChord && !contextChord) return;
    const onKeyDown = (event: globalThis.KeyboardEvent): void => {
      const forTarget = !!targetChord && matchesShortcut(event, targetChord);
      const step = forTarget ? 0 : stepChords[0] && matchesShortcut(event, stepChords[0]) ? -1 : stepChords[1] && matchesShortcut(event, stepChords[1]) ? 1 : 0;
      const forPermission = !forTarget && !step && !!permissionChord && matchesShortcut(event, permissionChord);
      const forBasket = !forTarget && !step && !forPermission && !!basketChord && matchesShortcut(event, basketChord);
      const forContext = !forTarget && !step && !forPermission && !forBasket && !!contextChord && matchesShortcut(event, contextChord);
      if (!forTarget && !forPermission && !step && !forBasket && !forContext) return;
      if (!connected || draft.composing || requestPending) return;
      if (document.querySelector('.overlay .dialog')) return;
      if (mention || (slash && slash.length > 0)) return;
      event.preventDefault();
      if (forBasket) {
        if (basketOpen) { setBasketOpen(false); inputRef.current?.focus({ preventScroll: true }); }
        else { setTargetOpen(false); closePermissionPopups(); basketByKey.current = true; focusBasketOnOpen.current = true; setBasketOpen(true); }
      } else if (forContext) {
        onIncludeEditorContextChange(!includeEditorContext);
      } else if (step) {
        // -1 is the chat's partner (no one-message target).
        const index = choices.findIndex(item => targetKey(item.target) === targetKey(selectedTarget));
        const next = index < 0 ? (step > 0 ? 0 : choices.length - 1) : index + step;
        onSelectTarget(next < 0 || next >= choices.length ? undefined : choices[next]!.target);
        setTargetOpen(false);
      } else if (forTarget) {
        setTargetOpen(previous => {
          if (previous) focusControlOrInput(targetButton.current, inputRef.current);
          return !previous;
        });
      } else if (!permissionDisabled) {
        if (permissionOpen || permissionConfirm) closePermission();
        else { setTargetOpen(false); setPermissionOpen(true); }
      }
    };
    window.addEventListener('keydown', onKeyDown);
    return () => window.removeEventListener('keydown', onKeyDown);
  }, [hasTargets, shortcuts.sendTargetPicker, shortcuts.previousTarget, shortcuts.nextTarget, shortcuts.permissionMode, shortcuts.contextBasket, shortcuts.editorContext, connected, draft.composing, requestPending, mention, slash, permissionDisabled, permissionOpen, permissionConfirm, choices, selectedTarget, onSelectTarget, basketOpen, includeEditorContext, onIncludeEditorContextChange]);

  const [stopping, setStopping] = useState(false);
  useEffect(() => {
    if (!busy || !stopping) return;
    const id = window.setTimeout(() => setStopping(false), STOP_FEEDBACK_MS);
    return () => window.clearTimeout(id);
  }, [busy, stopping]);
  useEffect(() => { if (!busy) setStopping(false); }, [busy]);

  // Height follows content only: the fit below writes style.height directly and never touches state,
  // so it cannot feed back into the observer. A ResizeObserver watches width/visibility only —
  // container-only width changes and hidden→shown transitions refit even when every prop is unchanged.
  const fitInput = useCallback(() => {
    const el = inputRef.current;
    if (!el) return;
    el.style.height = 'auto';
    // An empty field stays one line: a narrow field would otherwise grow to fit the wrapped placeholder.
    if (!el.value) return;
    const border = el.offsetHeight - el.clientHeight;
    el.style.height = `${Math.min(el.scrollHeight + border, MAX_HEIGHT_PX)}px`;
  }, [inputRef]);

  useLayoutEffect(() => {
    fitInput();
  }, [draft.text, fitInput]);

  useEffect(() => {
    const el = inputRef.current;
    if (!el || typeof ResizeObserver === 'undefined') return;
    const seen = { width: 0, raf: 0 };
    const schedule = (width: number): void => {
      // Schedule one rAF per width change, and only for a laid-out (nonzero-width) box: while hidden
      // there is nothing to fit, and the nonzero transition after showing schedules the real refit.
      if (width === seen.width) return;
      seen.width = width;
      if (width === 0 || seen.raf !== 0) return;
      seen.raf = window.requestAnimationFrame(() => { seen.raf = 0; if (seen.width > 0) fitInput(); });
    };
    const observer = new ResizeObserver((entries) => {
      const width = entries[entries.length - 1]?.contentRect.width ?? 0;
      schedule(Math.round(width));
    });
    observer.observe(el);
    return () => { observer.disconnect(); if (seen.raf !== 0) { window.cancelAnimationFrame(seen.raf); seen.raf = 0; } };
  }, [fitInput]);
  useEffect(() => { fitInput(); }, [fitInput, connected, requestPending]);

  // The basket opens above the whole composer: its height is the room above it (a tall input or many attachments leave less), up to the
  // nearest ancestor that clips (the tab page under the tab bar).
  useLayoutEffect(() => {
    const pop = basketPop.current;
    if (!basketOpen || !pop) return;
    const apply = (): void => {
      let top = 0;
      for (let element = pop.parentElement; element; element = element.parentElement) {
        if (getComputedStyle(element).overflowY !== 'visible') { top = Math.max(0, element.getBoundingClientRect().top); break; }
      }
      const room = pop.getBoundingClientRect().bottom - top - 6;
      pop.style.setProperty('--basket-max', `${Math.floor(Math.max(96, Math.min(room, 430)))}px`);
    };
    apply();
    window.addEventListener('resize', apply);
    return () => window.removeEventListener('resize', apply);
  }, [basketOpen, draft.text, draft.attachments.length]);

  // The code editor's POMP menu (composer/prefill): files into the basket, editor context on, the text into an empty input. A text to
  // send (POMP에게 질문 › 채팅으로 질문, explain/review/fix) is its own message to POMP at once, with the editor context and the basket's
  // checked files, queued behind a running reply; the draft and its target stay as they are. Only when the chat cannot take a message
  // now (a request card, a new conversation starting, no connection) does it wait in the input instead.
  const autoSend = useRef<string | null>(null);
  const prefillSeq = prefill?.seq;
  useEffect(() => {
    if (!prefill) return;
    const { payload } = prefill;
    if (payload.files?.length) dispatch({ type: 'attachmentsAdd', files: payload.files });
    autoSend.current = null;
    if (payload.text && payload.send && connected && !newSessionPending) {
      const files = [...draft.attachments, ...(payload.files ?? [])].filter((file, index, all) => all.findIndex(other => other.fullPath === file.fullPath) === index);
      const result = onSend(payload.text, files, false, MAIN_TARGET, true, payload.includeEditorContext || includeEditorContext, true);
      if (result.ok) return;
    }
    if (payload.includeEditorContext && !includeEditorContext) onIncludeEditorContextChange(true);
    // The editor menu's "show all": the send-target picker opens once the main conversation is shown.
    if (payload.pickTarget) { setBasketOpen(false); window.requestAnimationFrame(() => window.requestAnimationFrame(() => setTargetOpen(true))); }
    if (payload.text) {
      const empty = draft.text.trim().length === 0;
      const next = empty ? payload.text : `${draft.text.replace(/\s+$/u, '')}\n\n${payload.text}`;
      dispatch({ type: 'composerEdit', text: next, caret: next.length, selectionEnd: next.length });
      if (empty && payload.send) autoSend.current = next;
      if (payload.send) deferredMain.current = payload.text;
    }
    window.requestAnimationFrame(() => inputRef.current?.focus());
  }, [prefillSeq]); // only a new request, not a re-render
  useEffect(() => {
    const pending = autoSend.current;
    if (pending === null || draft.text !== pending || (prefill?.payload.includeEditorContext && !includeEditorContext)) return;
    autoSend.current = null;
    // Editor menu requests belong to the main conversation: POMP answers them whatever partner tab is active.
    if (canSend && !busy) submit(MAIN_TARGET);
  });

  const submit = (override?: SendTarget): void => {
    if (!canSend || composing.current) return;
    setTargetLost(null);
    if (rejection) { setFailure(rejection); inputRef.current?.focus(); return; }
    const command = /^\/([^\s]+)(?:\s+([\s\S]*))?$/u.exec(text);
    // An old command name whose job a built-in does now runs that built-in (/pomp-compact → /compact).
    const typed = command ? LEGACY_COMMANDS.get(command[1]!) ?? command[1]! : undefined;
    // /plan is POMP's own plan-only command (the host maps it): without a task there is nothing to plan.
    if (command && typed === 'plan' && !(command[2] ?? '').trim() && catalog.some(item => item.name === 'plan')) {
      setTargetError(t('plan.empty')); inputRef.current?.focus(); return;
    }
    if (command && typed && isBuiltinCommand(typed)) {
      const name = typed;
      let result: SendResult;
      if (name === 'model' || name === 'thinking' || name === 'effort') { setPickerRequest(previous => ({ kind: name === 'model' ? 'model' : 'thinking', revision: (previous?.revision ?? 0) + 1 })); result = { ok: true }; }
      else if (name === 'permissions') {
        if (permissionDisabled) result = { ok: false, reason: busy ? 'busy' : 'hostUnavailable' };
        else { setTargetOpen(false); setBasketOpen(false); setPermissionConfirm(false); setPermissionOpen(true); result = { ok: true }; }
      }
      else if (name === 'help') { setHelpOpen(true); result = { ok: true }; }
      else if (name === 'goal') {
        const goalText = command[2]?.trim() ?? '';
        if (!goalText) { setTargetError(t('goal.empty')); inputRef.current?.focus(); return; }
        // A goal runs in the main conversation: an agent or team tab has no workflow of its own.
        if (partnerTab) { setTargetError(t('composer.goalPartnerTab')); inputRef.current?.focus(); return; }
        setTargetError(null); onGoal?.(goalText); result = { ok: true };
      }
      else if (name === 'agent' || name === 'team') {
        const args = /^(\S+)(?:\s+([\s\S]*))?$/u.exec(command[2]?.trim() ?? '');
        const target = args ? destinations.find(item => item.target.kind === name && (name === 'team' ? item.target.id.toLowerCase() === args[1]!.toLowerCase() : item.target.id === args[1]))?.target : undefined;
        if (!target) {
          setTargetError(args ? t('composer.targetUnknown', { name: args[1]! }) : t('composer.targetRequired'));
          inputRef.current?.focus(); return;
        }
        setTargetError(null);
        // A name alone switches the conversation partner (a tab); with a message it is a one-message target.
        if (!args?.[2]?.trim() && onOpenPartner && target.kind !== 'instance') { onOpenPartner(target); result = { ok: true }; }
        // The tab's own partner is this conversation already: the message goes to it as usual, not to a second run of it.
        else if (partnerTab && args?.[2]?.trim() && target.kind === partnerTab.kind && target.id.toLowerCase() === partnerTab.id.toLowerCase()) result = onSend(args[2].trim(), draft.attachments, true, null);
        else { onSelectTarget(target); result = args?.[2]?.trim() ? onSend(args[2].trim(), draft.attachments, true, target) : { ok: true }; }
      }
      else result = onCommand(name, command[2]?.trim() ?? '');
      setFailure(result.ok ? null : result);
      if (result.ok) recalled.current = null;
      if (result.ok) dispatch({ type: 'composerEdit', text: '', caret: 0, selectionEnd: 0 });
    } else {
      // A slash command takes the basket's checked files when it hands its text to the model; any other command leaves the basket for the next message.
      const keepBasket = command !== null && !slashTakesReferences(text, commands);
      const destination = override ?? (mainPending ? MAIN_TARGET : undefined);
      const result = onSend(text, keepBasket ? [] : draft.attachments, !keepBasket, destination, false, undefined, destination?.kind === 'main');
      setFailure(result.ok ? null : result);
      if (result.ok) recalled.current = null;
      if (result.ok) deferredMain.current = null;
      if (result.ok && keepBasket) dispatch({ type: 'composerEdit', text: '', caret: 0, selectionEnd: 0 });
    }
    if (!(command && typed === 'permissions')) inputRef.current?.focus();
  };

  const choose = (file: WorkspaceFile): void => {
    if (composing.current || !connected || !draft.mention) return;
    const caret = draft.mention.start + mentionSelectionToken(file).length + (draft.text.slice(draft.mention.end).startsWith(' ') ? 0 : 1);
    dispatch({ type: 'mentionSelect', file });
    setFailure(null);
    window.requestAnimationFrame(() => {
      const el = inputRef.current;
      el?.focus();
      el?.setSelectionRange(caret, caret);
      // Focus/selection events can observe the pre-insertion caret and reopen the old
      // @query. Synchronize the reducer after the final caret is placed, even when
      // WebView2 emits no selectionchange for setSelectionRange.
      dispatch({ type: 'composerCaret', caret, selectionEnd: caret });
    });
  };

  const chooseTargetMention = (target: SendTarget): void => {
    if (composing.current || !connected || !draft.mention) return;
    const range = draft.mention;
    const value = draft.text.slice(0, range.start) + draft.text.slice(range.end);
    onSelectTarget(target); setTargetError(null); setTargetLost(null);
    dispatch({ type: 'composerEdit', text: value, caret: range.start, selectionEnd: range.start });
    dispatch({ type: 'mentionDismiss' });
    window.requestAnimationFrame(() => { inputRef.current?.focus(); inputRef.current?.setSelectionRange(range.start, range.start); });
  };

  const chooseSlash = (item: SlashMatch): void => {
    if (composing.current || draft.composing) return;
    // Typed "/…" completion replaces the token up to the caret and the rest of that token. The toolbar list never wipes the draft: a leading
    // command token (not a path like /usr/bin) is replaced by the choice, any other text keeps going after the inserted command.
    const lead = helpOpen ? /^\/[^\s/]*(?=\s|$)/u.exec(draft.text)?.[0] : undefined;
    const rest = !helpOpen ? draft.text.slice(caret.start).replace(/^\S*\s?/u, '') : lead === undefined ? draft.text : draft.text.slice(lead.length).replace(/^[ \t]/u, '');
    const value = item.value + rest;
    dispatch({ type: 'composerEdit', text: value, caret: item.value.length, selectionEnd: item.value.length });
    setHelpOpen(false); setSlashDismissed(value); setCaret({ start: item.value.length, end: item.value.length });
    window.requestAnimationFrame(() => { inputRef.current?.focus(); inputRef.current?.setSelectionRange(item.value.length, item.value.length); });
  };

  // The prompts sent before, newest last: ↑ in an empty input brings back the newest, ↑/↓ on a prompt brought back unchanged step
  // through them, and ↓ past the newest empties the input again. A prompt being written is never replaced.
  const recalled = useRef<{ index: number; text: string } | null>(null);
  const recall = (by: -1 | 1): boolean => {
    const entries = promptHistory(workspaceKey ?? '');
    const browsing = recalled.current && recalled.current.text === draft.text ? recalled.current.index : null;
    const put = (value: string, index: number | null): void => {
      recalled.current = index === null ? null : { index, text: value };
      dispatch({ type: 'composerEdit', text: value, caret: value.length, selectionEnd: value.length });
      dispatch({ type: 'mentionDismiss' });
      if (value.startsWith('/')) setSlashDismissed(value);
      setCaret({ start: value.length, end: value.length });
      if (failure) setFailure(null);
      if (targetError) setTargetError(null);
      window.requestAnimationFrame(() => inputRef.current?.setSelectionRange(value.length, value.length));
    };
    if (browsing === null) {
      if (draft.text !== '' || by > 0 || entries.length === 0) return false;
      put(entries[entries.length - 1]!, entries.length - 1);
      return true;
    }
    const index = browsing + by;
    if (index < 0) return true;
    if (index >= entries.length) put('', null);
    else put(entries[index]!, index);
    return true;
  };

  // With "Enter" the key sends (Shift+Enter breaks the line, Ctrl+Enter sends too); with "Ctrl+Enter" only that sends and Enter breaks the line.
  const isSendKey = (event: KeyboardEvent<HTMLTextAreaElement>): boolean => event.key === 'Enter' && !event.shiftKey && (shortcuts.send !== 'Ctrl+Enter' || event.ctrlKey);
  const onKeyDown = (event: KeyboardEvent<HTMLTextAreaElement>): void => {
    // React/native composition and the legacy IME keyCode must all be checked before handling popup or send keys.
    if (composing.current || draft.composing || event.nativeEvent.isComposing || event.keyCode === 229) { discardPrediction(); return; }
    if (prediction && !slash && !mention) {
      if (event.key === 'Tab' && !event.shiftKey && !event.ctrlKey && !event.altKey && !event.metaKey) {
        const el = event.currentTarget;
        if (el.value === draft.text && el.selectionStart === draft.text.length && el.selectionEnd === draft.text.length) {
          event.preventDefault();
          const value = draft.text + prediction;
          discardPrediction();
          setCaret({ start: value.length, end: value.length });
          dispatch({ type: 'composerEdit', text: value, caret: value.length, selectionEnd: value.length });
          if (failure) setFailure(null);
          if (targetError) setTargetError(null);
          window.requestAnimationFrame(() => el.setSelectionRange(value.length, value.length));
          return;
        }
      }
      if (event.key === 'Escape') { event.preventDefault(); event.stopPropagation(); discardPrediction(); return; }
    }
    if (['ArrowLeft', 'ArrowRight', 'ArrowUp', 'ArrowDown', 'Home', 'End', 'PageUp', 'PageDown', 'Tab', 'Escape', 'Enter'].includes(event.key)) discardPrediction();
    if (slash) {
      if (event.key === 'Escape') { event.preventDefault(); event.stopPropagation(); setSlashDismissed(draft.text); setHelpOpen(false); return; }
      if (event.key === 'ArrowDown' || event.key === 'ArrowUp') {
        event.preventDefault(); if (slash.length) setSlashSelected((slashIndex + (event.key === 'ArrowDown' ? 1 : -1) + slash.length) % slash.length); return;
      }
      if ((event.key === 'Enter' || event.key === 'Tab') && !event.shiftKey && slash.length > 0) { event.preventDefault(); chooseSlash(slash[slashIndex]!); return; }
      if (event.key === 'Tab' && !event.shiftKey) { event.preventDefault(); return; }
      if (isSendKey(event) && slash.length === 0 && (text === '/' || isBuiltinCommand(text.slice(1)))) { event.preventDefault(); return; }
    }
    if (mention) {
      if (event.key === 'Escape') { event.preventDefault(); dispatch({ type: 'mentionDismiss' }); return; }
      if (event.key === 'ArrowDown' || event.key === 'ArrowUp') {
        event.preventDefault(); dispatch({ type: 'mentionMove', offset: event.key === 'ArrowDown' ? 1 : -1, count: mentionCount }); return;
      }
      if ((event.key === 'Enter' || event.key === 'Tab') && !event.shiftKey) {
        event.preventDefault();
        const target = mentionTargets[mentionIndex]?.target;
        const file = mention.files[mentionIndex - mentionTargets.length];
        if (target) chooseTargetMention(target);
        else if (file) choose(file);
        return; // loading/empty list must never accidentally send @query
      }
    }
    if ((event.key === 'ArrowUp' || event.key === 'ArrowDown') && !event.shiftKey && !event.ctrlKey && !event.altKey && !event.metaKey && recall(event.key === 'ArrowUp' ? -1 : 1)) {
      event.preventDefault();
      return;
    }
    if (!isSendKey(event)) {
      // Escape in the input stops a running reply like the stop button (only when no popup handled it above).
      if (event.key === 'Escape' && busy && connected && !stopping) {
        event.preventDefault();
        setStopping(true);
        onStop();
      }
      return;
    }
    event.preventDefault();
    submit();
  };

  const pickFiles = (): void => {
    if (!connected || draft.pickerRequestId || composing.current) return;
    const requestId = newId();
    dispatch({ type: 'pickerRequested', requestId });
    if (!send('workspace/pickFiles', { requestId })) dispatch({ type: 'workspaceFailed', requestId, message: t('composer.filesFailed') });
  };

  const insertMention = (): void => {
    const el = inputRef.current;
    if (!el || !connected || composing.current) return;
    const start = el.selectionStart;
    const prefix = start > 0 && !/[\s([{]/u.test(draft.text[start - 1]!) ? ' ' : '';
    const value = draft.text.slice(0, start) + prefix + '@' + draft.text.slice(el.selectionEnd);
    const caret = start + prefix.length + 1;
    dispatch({ type: 'composerEdit', text: value, caret, selectionEnd: caret });
    window.requestAnimationFrame(() => { el.focus(); el.setSelectionRange(caret, caret); });
  };

  // Files, folders and symbols that go with the next message, and the knowledge selected for the conversation.
  const knowledge = useKnowledge();
  const selectedKnowledge = usableKnowledge(knowledge.items).filter(item => knowledge.selected.includes(item.id)).length;
  const basketCount = draft.attachments.length + selectedKnowledge;
  const attachmentPaths = useMemo(() => draft.attachments.map(file => file.path), [draft.attachments]);
  const placeholder = !connected ? t('composer.placeholderOffline') : newSessionPending ? t('composer.placeholderNewSession') : requestPending ? t('composer.placeholderPending') : busy ? t('composer.placeholderBusy') : t('composer.placeholder');

  // Right-click on the composer background: which controls it shows (checked = shown). Text fields keep their edit menu.
  const hiddenControls = useHiddenComposerControls();
  const { menuProps: controlsMenuProps, renderMenu: renderControlsMenu } = useContextMenu();
  const controlsMenu = controlsMenuProps((): ContextMenuItem[] => [
    ...COMPOSER_CONTROLS.map(control => ({
      id: control, label: t(`composerControl.${control}` as MessageKey), checked: !hiddenControls.includes(control), testId: `composer-control-${control}`,
      onSelect: () => setComposerControlHidden(control, !hiddenControls.includes(control)),
    })),
    ...(hiddenControls.length > 0 ? [{ id: 'show-all', label: t('composerControl.showAll'), testId: 'composer-control-show-all', onSelect: showAllComposerControls }] : []),
  ]);

  return (
    <div className="composer" data-hide={hiddenControls.join(' ') || undefined}
      onContextMenu={event => {
        if (event.target instanceof Element && event.target.closest(COMPOSER_POPUPS)) return;
        controlsMenu.onContextMenu(event);
        if (event.defaultPrevented) markMenuHandled(event.nativeEvent);
      }}
      onKeyDown={event => {
        if (event.target instanceof Element && event.target.closest(`${COMPOSER_POPUPS}, input, textarea`)) return;
        controlsMenu.onKeyDown(event);
      }}
      onCompositionStartCapture={() => { basketComposing.current = true; }}
      onCompositionEndCapture={() => { basketComposing.current = false; }}
      onKeyDownCapture={event => {
        if (!basketOpen || event.key !== 'Escape' || composing.current || draft.composing || basketComposing.current || event.nativeEvent.isComposing || event.keyCode === 229) return;
        // Innermost popup first: an open mention/slash list, the target picker or another picker closes before the basket does.
        const innerPopup = event.target instanceof Element && !basketPop.current?.contains(event.target) && !!event.target.closest('.picker-pop, [role="listbox"]');
        if (mention || (slash && slash.length > 0) || targetOpen || innerPopup) return;
        event.preventDefault(); event.stopPropagation(); setBasketOpen(false);
        (basketByKey.current ? inputRef.current : basketButton.current)?.focus({ preventScroll: true });
      }}>
      {/* Everything about the context of the next message in one row: how full the window is, what is attached, editor context. */}
      <div className="context-row" role="group" aria-label={t('meter.context')} data-testid="context-row">
        <span className="context-tools">
          <button type="button" className="icon-btn" data-testid="mention" disabled={!connected || draft.composing} title={t('composer.mention')} aria-label={t('composer.mention')} onMouseDown={(event) => event.preventDefault()} onClick={insertMention}><Icon name="at" size={14} /></button>
          <button type="button" className="icon-btn" data-testid="slash" disabled={!connected || draft.composing} title={t('slash.title')} aria-label={t('slash.title')}
            aria-haspopup="listbox" aria-expanded={slash !== null} onMouseDown={event => event.preventDefault()} onClick={() => {
              // A toggle: a second click (or a click while the typed "/…" list is open) closes the command list.
              if (slash) { setHelpOpen(false); setSlashDismissed(draft.text); }
              else { setHelpOpen(true); setSlashDismissed(null); }
              inputRef.current?.focus();
            }}><Icon name="slash" size={14} /></button>
          <button type="button" className="icon-btn" data-testid="attach" disabled={!connected || draft.composing || !!draft.pickerRequestId} title={t('composer.attach')} aria-label={t('composer.attach')} onClick={pickFiles}><Icon name="paperclip" size={14} /></button>
          <div className="basket-anchor" ref={basketRef}>
            <button ref={basketButton} type="button" className={basketCount > 0 ? 'context-count filled' : 'context-count'} data-testid="context-chip" disabled={requestPending} title={t('composer.basket', { count: basketCount })}
              aria-label={t('composer.basket', { count: basketCount })} aria-expanded={basketOpen} onClick={event => {
                focusBasketOnOpen.current = !basketOpen && event.detail === 0; basketByKey.current = false;
                setBasketOpen(!basketOpen);
              }}><Icon name="paperclip" size={12} /><span className="context-count-value">{basketCount}</span></button>
            <div ref={basketPop} className="context-popover" data-testid="context-popover" hidden={!basketOpen} tabIndex={-1}>
              <ContextBasket visible={basketOpen} files={draft.attachments} text={draft.text} dispatch={dispatch} locale={locale} workspaceKey={workspaceKey} sessionId={sessionId} model={model} connected={connected} disabled={requestPending || newSessionPending || draft.composing} />
            </div>
          </div>
          <LiveEditorControls state={editorFollow} disabled={requestPending || newSessionPending || draft.composing} shortcuts={shortcuts} inputRef={inputRef} t={t} />
          <button type="button" className="icon-btn" data-testid="editor-context" aria-pressed={includeEditorContext} title={t('composer.includeContext')} aria-label={t('composer.includeContext')} onClick={() => onIncludeEditorContextChange(!includeEditorContext)}><Icon name="context" size={14} /></button>
        </span>
        <ContextMeter session={session} basketTokens={basketTokens} workspaceKey={workspaceKey} sessionId={sessionId} includeEditorContext={includeEditorContext} attachmentPaths={attachmentPaths} connected={connected} />
      </div>
      {/* The notices are shown in the VS status bar; screen readers get them here (not drawn), and the input refers to the reason. */}
      <div className="sr-only" id={noticeId} role="alert" data-testid="composer-notice">
        {[failureMessage, draft.fileError, targetLost, targetError, permissionError].filter(Boolean).join(' ')}
      </div>
      <div className="composer-field">
        {slash && <div className="mention-popup slash-popup" data-testid="slash-popup">
          <div className="mention-head">{t('slash.title')}</div>
          <div className="mention-list" id={slashId} role="listbox" aria-label={t('slash.title')}>
            {slash.map((item, index) => <Fragment key={item.value}>
              {item.group === 'skills' && slash[index - 1]?.group !== 'skills' && <div className="slash-group" role="presentation" data-testid="slash-group-skills">{t('slash.group.skills')}</div>}
              <button type="button" role="option" tabIndex={-1} id={`${slashId}-${index}`} aria-selected={index === slashIndex}
                className={index === slashIndex ? 'mention-option selected' : 'mention-option'} onMouseDown={event => event.preventDefault()} onClick={() => chooseSlash(item)}>
                <strong>{revealControls(item.value.trim())}</strong>
                {item.description && <span className="slash-description">{revealControls(item.description)}</span>}
              </button>
            </Fragment>)}
          </div>
          {(commandsHidden ?? 0) > 0 && <div className="mention-status" role="status" data-testid="slash-hidden">{t('slash.hidden', { count: commandsHidden! })}</div>}
        </div>}
        {mention && !slash && (
          <div className="mention-popup" data-testid="mention-popup">
            <div className="mention-head">{t('composer.mentionFiles')}</div>
            <div id={popupId} className="mention-list" role="listbox" aria-label={t('composer.mentionFiles')}>
              {mentionTargets.length > 0 && <div className="mention-group" data-testid="mention-targets-group">{t('composer.mentionTargets')}</div>}
              {mentionTargets.map((item, index) => <button type="button" role="option" tabIndex={-1} id={`${popupId}-${index}`} key={targetKey(item.target)} data-testid={`mention-target-${item.target.kind}-${item.target.id}`}
                aria-selected={index === mentionIndex} className={index === mentionIndex ? 'mention-option selected' : 'mention-option'} title={item.title}
                onMouseDown={event => event.preventDefault()} onClick={() => chooseTargetMention(item.target)}><span className="mention-avatar" aria-hidden="true"><DestinationAvatar kind={item.avatar.kind} id={item.avatar.id} size={14} /></span><strong>{revealControls(item.label)}</strong><span className="hint">{revealControls(item.target.id)}</span></button>)}
              {mentionTargets.length > 0 && mention.files.length > 0 && <div className="mention-group">{t('composer.mentionFiles')}</div>}
              {mention.files.map((file, index) => (
                <button type="button" role="option" tabIndex={-1} id={`${popupId}-${index + mentionTargets.length}`} key={`${file.fullPath}:${index}`} aria-selected={index + mentionTargets.length === mentionIndex}
                  className={index + mentionTargets.length === mentionIndex ? 'mention-option selected' : 'mention-option'} title={file.fullPath}
                  onMouseDown={(event) => event.preventDefault()} onClick={() => choose(file)}>
                  <span aria-hidden="true">{file.kind === 'folder' ? '▱' : file.kind === 'document' ? '▧' : (file.kind === 'symbol' || file.kind === 'code') ? '◇' : '▤'}</span> <FilePath path={file.path} />
                  {(file.kind === 'symbol' || file.kind === 'code') && <span className="mention-symbol">{revealControls(file.symbol || 'source')}:{file.line}</span>}
                </button>
              ))}
            </div>
            {(mention.pending || mention.error || mention.files.length === 0 || mention.truncated) && (
              <div className="mention-status" role="status">{mention.pending ? t('composer.filesLoading') : mention.error ?? (mention.files.length === 0 ? t('composer.filesEmpty') : t('composer.filesTruncated', { count: mention.files.length }))}</div>
            )}
          </div>
        )}
        <div className="composer-input-shell">
        <textarea ref={inputRef} className="composer-input" data-testid="composer-input" data-pomp-composer="" rows={1} spellCheck={false} autoComplete="off"
          value={draft.text} disabled={!connected} placeholder={placeholder} aria-label={t('composer.input')} aria-invalid={failureMessage !== null}
          aria-describedby={failureMessage !== null ? noticeId : undefined}
          role="combobox" aria-autocomplete="list" aria-haspopup="listbox" aria-expanded={slash !== null || mention !== null} aria-controls={slash ? slashId : mention ? popupId : undefined}
          aria-activedescendant={slash?.length ? `${slashId}-${slashIndex}` : mentionCount > 0 ? `${popupId}-${mentionIndex}` : undefined}
          onFocus={() => setInputFocused(true)} onPointerDown={discardPrediction}
          onPaste={event => {
            // Files or an image (no plain text): only the host can read their paths, so it takes them from the clipboard into the basket.
            const data = event.clipboardData;
            if (data.types.includes('text/plain') || !(data.files.length > 0 || data.types.includes('Files'))) return;
            event.preventDefault(); send('ui/pasteFiles', {});
          }}
          onChange={(event) => {
            discardPrediction();
            const el = event.currentTarget;
            setCaret({ start: el.selectionStart, end: el.selectionEnd }); setHelpOpen(false);
            dispatch({ type: 'composerEdit', text: el.value, caret: el.selectionStart, selectionEnd: el.selectionEnd });
            if (failure) setFailure(null);
            if (targetError) setTargetError(null);
          }}
          onSelect={(event) => {
            const el = event.currentTarget;
            if (el.selectionStart !== caret.start || el.selectionEnd !== caret.end) discardPrediction();
            if (!composing.current && document.activeElement === el) { setCaret({ start: el.selectionStart, end: el.selectionEnd }); dispatch({ type: 'composerCaret', caret: el.selectionStart, selectionEnd: el.selectionEnd }); }
          }}
          onCompositionStart={() => { discardPrediction(); composing.current = true; dispatch({ type: 'composerComposition', composing: true }); }}
          onCompositionEnd={(event) => {
            composing.current = false;
            const el = event.currentTarget;
            setCaret({ start: el.selectionStart, end: el.selectionEnd });
            dispatch({ type: 'composerComposition', composing: false });
            dispatch({ type: 'composerEdit', text: el.value, caret: el.selectionStart, selectionEnd: el.selectionEnd });
          }}
          onBlur={event => {
            discardPrediction(); setInputFocused(false);
            const el = event.currentTarget; if (!composing.current) setCaret({ start: el.selectionStart, end: el.selectionEnd });
            if (latest.current.mention) dispatch({ type: 'mentionDismiss' });
            // Options and the "/" button keep the focus (preventDefault on mousedown), so a blur means "clicked elsewhere".
            if (slash) { setHelpOpen(false); setSlashDismissed(draft.text); }
          }} onKeyDown={onKeyDown} />
        {prediction && <ComposerGhost inputRef={inputRef} text={draft.text} prediction={prediction} />}
        </div>
        {/* Send (Enter) sits next to the text it sends; Stop joins it while a reply runs. */}
        <div className="composer-send">
          {busy && <button type="button" className="btn stop composer-send-btn" data-testid="stop" title={stopping ? t('composer.stopping') : t('composer.stop')} aria-label={stopping ? t('composer.stopping') : t('composer.stop')} disabled={!connected || stopping} onClick={() => { setStopping(true); onStop(); }}><Icon name="stop" size={11} /><span className="sr-only">{stopping ? t('composer.stopping') : t('composer.stop')}</span></button>}
          <button type="button" className="btn primary composer-send-btn" data-testid="send" disabled={!canSend} title={t(busy ? 'queue.send' : 'composer.send')} aria-label={t(busy ? 'queue.send' : 'composer.send')} onClick={() => submit()}><Icon name="arrowUp" size={14} /><span className="sr-only">{t(busy ? 'queue.send' : 'composer.send')}</span></button>
        </div>
      </div>
      <div className="composer-bar">
        <div className="permission-mode-anchor" ref={permissionRef} onKeyDown={event => {
          if (event.key === 'Escape' && permissionOpen && !event.nativeEvent.isComposing && event.keyCode !== 229) {
            event.preventDefault(); event.stopPropagation(); closePermission();
          }
        }}>
          <button ref={permissionButton} type="button" className={`chip permission-mode-chip${permissionMode !== 'alwaysAsk' ? ' highlighted' : ''}`} data-testid="permission-mode-chip" data-mode={permissionMode}
            disabled={permissionDisabled} title={t('perm.mode')} aria-label={t('perm.mode')} aria-haspopup="listbox" aria-expanded={permissionOpen}
            onClick={() => { setTargetOpen(false); setPermissionConfirm(false); setPermissionOpen(!permissionOpen); }}>
            {permissionPending !== null ? <span className="spinner" aria-hidden="true" /> : <Icon name="shield" size={12} />}
            <span className="chip-text">{t(`perm.mode.${permissionMode}` as MessageKey)}</span>
            <Icon name="chevronDown" size={11} className="chip-caret" />
          </button>
          {permissionOpen && <PickerPopover placement="up" anchorRef={permissionRef} id={`${popupId}-permission`} testId="permission-mode-popover" label={t('perm.mode')}
            items={APPROVAL_MODES.map(mode => ({ id: mode, label: t(`perm.mode.${mode}` as MessageKey), current: mode === permissionMode, testId: `permission-mode-${mode}` }))}
            onChoose={item => choosePermission(item.id as ApprovalMode)} />}
          {permissionConfirm && <ConfirmPopover anchorRef={permissionRef} testId="permission-mode-confirm" label={t('perm.mode')} question={t('perm.mode.confirmYolo')} enterConfirms
            cancelLabel={t('dialog.cancel')} confirmLabel={t('perm.allow')} cancelTestId="permission-mode-confirm-cancel" confirmTestId="permission-mode-confirm-allow"
            confirmDisabled={permissionDisabled} onCancel={closePermission} onConfirm={() => { closePermission(); setApprovalMode('yolo'); }} />}
        </div>
        {hasTargets && (
          <div className="send-target-anchor" ref={targetRef} onKeyDown={event => {
            if (event.key === 'Escape' && targetOpen && !event.nativeEvent.isComposing && event.keyCode !== 229) {
              event.preventDefault(); event.stopPropagation(); setTargetOpen(false); focusControlOrInput(targetButton.current, inputRef.current);
            }
          }}>
            <button
              ref={targetButton}
              type="button"
              className={`chip send-target-chip${selectedTarget ? ' highlighted active' : ''}`}
              data-testid="send-target-chip"
              disabled={!connected || draft.composing}
              title={plainControls(targetTitle)}
              aria-label={plainControls(targetTitle)}
              aria-haspopup="listbox"
              aria-expanded={targetOpen}
              onClick={() => setTargetOpen(!targetOpen)}
            >
              {chosen ? <DestinationAvatar kind={chosen.avatar.kind} id={chosen.avatar.id} size={14} /> : <Icon name="users" size={12} />}
              <span className="chip-text">{revealControls(targetLabel)}</span>
              <Icon name="chevronDown" size={11} className="chip-caret" />
            </button>
            {targetOpen && <PickerPopover placement="up" anchorRef={targetRef} id={`${popupId}-target`} testId="send-target-popover" label={t('composer.sendTargetThisChat')}
              markCurrent="background"
              items={listed.filter(item => matchesQuery(targetQuery, item.label)).map(item => ({ ...item, id: targetKey(item.target), current: item.own ? !selectedDestination : item.target === selectedDestination?.target, icon: <DestinationAvatar kind={item.avatar.kind} id={item.avatar.id} /> }))}
              search={listed.length > 10 ? { value: targetQuery, onChange: setTargetQuery, placeholder: t('ui.search'), label: t('ui.search') } : undefined}
              // The own row clears the one-message target: the next message continues this conversation, never a subagent run of it.
              onChoose={item => { onSelectTarget(choices.find(entry => targetKey(entry.target) === item.id)?.target); setTargetLost(null); setTargetOpen(false); focusControlOrInput(targetButton.current, inputRef.current); }} />}
          </div>
        )}
        <ModelPickers
          session={session}
          setup={setup}
          replies={setupReplies}
          connected={connected}
          locale={locale}
          variant="composer"
          onManage={onManageAccounts}
          onChosen={focusInput}
          modal={requestPending}
          pickerRequest={pickerRequest}
        />
      </div>
      <QuotaMeter session={session} setup={setup} replies={setupReplies} connected={connected} busy={busy} locale={locale} onManage={onManageAccounts} />
      {renderControlsMenu()}
    </div>
  );
}
