import { roleTitle } from '../bundledDescriptions';
import { useEffect, useLayoutEffect, useRef, useState, type KeyboardEvent } from 'react';
import type { AgentsStatePayload, UiToHost } from '../protocol';
import type { CommandReply, ConversationPartner, ConversationTab, TabsState } from '../conversationProtocol';
import { useConversationCommands } from '../conversationCommands';
import { useT } from '../i18n';
import { effectiveRoles, isRunnableTeam } from '../teamsCache';
import { PickerPopover } from './ModelPickers';
import { matches } from './ui';
import { Icon } from './Icon';
import { ConfirmPopover } from './ConfirmPopover';
import { plainControls, revealControls } from './RevealControls';
import { useStatusReport } from '../statusBar';

const MAIN = 'main';
const partnerKey = (partner: ConversationPartner) => `${partner.kind}:${partner.id ?? ''}`;

export type TabSwitch = Omit<UiToHost['conversation/tab'], 'requestId'>;

/** Chat tabs have independent engines. An in-flight open takes time to initialize, but never waits for the current run to finish. */
export function PartnerPicker({ tabs, agents, replies, sessionId, disabled, lockedTitle, busy, request, onSwitch, onRefreshRoster }: {
  tabs: TabsState | null; agents: AgentsStatePayload | null; replies: CommandReply[]; sessionId?: string; disabled: boolean; busy: boolean;
  /** Why a switch is temporarily unavailable, for the tooltip. */
  lockedTitle?: string;
  /** Open (or switch to) an agent or team tab from elsewhere: the Agents tab or `/agent name`. */
  request?: { partner: ConversationPartner; seq: number };
  /** Sends a conversation-changing tab command (the page waits for its `session/newResult`); false when it cannot go now. */
  onSwitch: (command: TabSwitch) => boolean;
  /** Asks the host to rescan agents and teams, so a role or team file created outside POMP shows up in the add list. */
  onRefreshRoster?: () => void;
}) {
  const t = useT(); const [open, setOpen] = useState(false);
  const [confirming, setConfirming] = useState<{ tab: ConversationTab; name: string }>();
  const [query, setQuery] = useState(''); useEffect(() => { if (!open) setQuery(''); }, [open]);
  const handled = useRef(request?.seq ?? 0); const pending = useRef<ConversationPartner | undefined>(undefined);
  const root = useRef<HTMLDivElement>(null); const add = useRef<HTMLButtonElement>(null);
  const row = useRef<HTMLDivElement>(null);
  const tabButtons = useRef(new Map<string, HTMLButtonElement>());
  // A switch locks the strip until the host answers: keep the intent to focus the active tab, not the disabled DOM node.
  const focusActive = useRef(false);
  const { post, pending: closing, error } = useConversationCommands(replies, sessionId);
  const scoped = tabs && sessionId && tabs.sessionId === sessionId ? tabs : null;
  const partner = scoped?.partner ?? { kind: 'main' as const };
  const roles = effectiveRoles(agents?.roles ?? []).filter(role => !role.error);
  const teams = (agents?.teams ?? []).filter(team => isRunnableTeam(team, agents));
  const name = (tab: ConversationTab) => (tab.partner.kind === 'agent' ? roleTitle(roles.find(role => role.name === tab.partner.id), t, tab.partner.id)
    : teams.find(team => team.id === tab.partner.id)?.title || tab.title) || tab.partner.id || '';
  // The current conversation is always a tab (also one whose tab was closed while it stayed open). Until the roster is known every tab
  // stays; afterwards a tab whose agent or team no longer exists is left out, unless it is the current one.
  const listed = tabs?.tabs ?? [];
  const current: ConversationTab | undefined = partner.kind === 'main' || !sessionId ? undefined
    : listed.find(tab => tab.sessionId === sessionId) ?? { sessionId, partner, title: scoped?.title };
  const known = (tab: ConversationTab) => !agents || tab.sessionId === current?.sessionId
    || (tab.partner.kind === 'agent' ? roles.some(role => role.name === tab.partner.id) : teams.some(team => team.id === tab.partner.id));
  const extras = [...listed.filter(known), ...(current && !listed.some(tab => tab.sessionId === current.sessionId) ? [current] : [])];
  const activeKey = current?.sessionId ?? MAIN;
  const keys = [MAIN, ...extras.map(tab => tab.sessionId)];
  const locked = disabled || closing || !sessionId;
  const pendingTab = tabs?.pendingSessionId;
  const pendingPartner = tabs?.pendingPartner;
  const pendingName = pendingPartner ? pendingPartner.kind === 'main' ? t('partner.main')
    : pendingPartner.kind === 'agent' ? roleTitle(roles.find(role => role.name === pendingPartner.id), t, pendingPartner.id)
    : teams.find(team => team.id === pendingPartner.id)?.title || pendingPartner.id
    : pendingTab === tabs?.main ? t('partner.main') : extras.find(tab => tab.sessionId === pendingTab)?.title || extras.find(tab => tab.sessionId === pendingTab)?.partner.id;

  useEffect(() => { if (disabled) setOpen(false); }, [disabled]);
  useEffect(() => { if (confirming && !extras.some(tab => tab.sessionId === confirming.tab.sessionId)) setConfirming(undefined); }, [confirming, extras]);
  useEffect(() => {
    if (!open) return;
    const outside = (event: PointerEvent) => { if (event.target instanceof Node && !root.current?.contains(event.target)) setOpen(false); };
    document.addEventListener('pointerdown', outside, true); return () => document.removeEventListener('pointerdown', outside, true);
  }, [open]);

  const switchTo = (command: TabSwitch) => {
    const accepted = onSwitch(command);
    if (accepted) focusActive.current = true;
    return accepted;
  };
  const activate = (key: string) => key === activeKey || switchTo(key === MAIN ? { action: 'activate' } : { action: 'activate', sessionId: key });
  /** An agent or team: its open tab, else a new conversation with it (the host decides, so a tab is never opened twice). */
  const openPartner = (target: ConversationPartner) => partnerKey(target) === partnerKey(partner) || switchTo({ action: 'open', partner: target });
  const scrollTab = (key: string) => {
    const strip = row.current; const tab = tabButtons.current.get(key)?.parentElement;
    if (!strip || !tab) return;
    const outer = strip.getBoundingClientRect(); const inner = tab.getBoundingClientRect();
    // The add button stays at the row's right edge: a tab is shown left of it, never under it.
    const right = outer.right - (add.current?.getBoundingClientRect().width ?? 0);
    if (inner.left < outer.left) strip.scrollLeft -= outer.left - inner.left;
    else if (inner.right > right) strip.scrollLeft += inner.right - right;
  };
  useLayoutEffect(() => {
    if (locked || !focusActive.current) return;
    focusActive.current = false;
    tabButtons.current.get(activeKey)?.focus({ preventScroll: true });
    scrollTab(activeKey);
  });
  useLayoutEffect(() => { scrollTab(activeKey); }, [activeKey, keys.join('\n')]);
  useEffect(() => {
    const strip = row.current;
    if (!strip) return;
    const observer = new ResizeObserver(() => scrollTab(activeKey));
    observer.observe(strip); return () => observer.disconnect();
  }, [activeKey]);
  // A tab requested elsewhere opens when switching is available; keep the request if an in-flight switch raced this render.
  useEffect(() => {
    if (!request || request.seq === handled.current) return;
    handled.current = request.seq; pending.current = request.partner;
  }, [request?.seq]); // eslint-disable-line react-hooks/exhaustive-deps
  useEffect(() => {
    const target = pending.current;
    if (!target || locked) return;
    if (openPartner(target)) pending.current = undefined;
  });
  const close = (tab: ConversationTab, label: string) => {
    if (tab.sessionId === activeKey ? busy : tab.isRunning) { setConfirming({ tab, name: label }); setOpen(false); return; }
    closeConfirmed(tab);
  };
  const closeConfirmed = (tab: ConversationTab) => {
    setConfirming(undefined);
    // Closing the current tab returns to the Default tab (a switch of conversation); another tab just goes.
    if (tab.sessionId === activeKey) switchTo({ action: 'close', sessionId: tab.sessionId });
    else if (post('conversation/tab', { action: 'close', sessionId: tab.sessionId })) tabButtons.current.get(activeKey)?.focus({ preventScroll: true });
  };
  const items = [
    ...roles.map(role => ({ id: `agent:${role.name}`, label: roleTitle(role, t), groupKey: 'agents', group: t('partner.agents') })),
    ...teams.map(team => ({ id: `team:${team.id}`, label: team.title || team.id, groupKey: 'teams', group: t('partner.teams') })),
  ].filter(item => matches(query, item.label, item.id.slice(item.id.indexOf(':') + 1))).map(item => ({
    ...item, current: item.id === partnerKey(partner), hint: extras.some(tab => partnerKey(tab.partner) === item.id) ? t('partner.open') : undefined,
  }));
  const arrows = (event: KeyboardEvent<HTMLDivElement>) => {
    if (!['ArrowLeft', 'ArrowRight', 'Home', 'End'].includes(event.key) || !(event.target instanceof HTMLElement) || event.target.getAttribute('role') !== 'tab') return;
    const buttons = [...(root.current?.querySelectorAll<HTMLButtonElement>('[role="tab"]') ?? [])];
    const index = buttons.indexOf(event.target as HTMLButtonElement);
    const next = event.key === 'Home' ? 0 : event.key === 'End' ? buttons.length - 1 : (index + (event.key === 'ArrowRight' ? 1 : -1) + buttons.length) % buttons.length;
    event.preventDefault(); buttons[next]?.focus(); scrollTab(keys[next]!);
  };

  // Notices of the tab strip (a failed close, the team's restarted members) go to the VS status bar, not as text inside the tab row.
  useStatusReport(error);
  useStatusReport(scoped?.message, 'info');
  return <div className="partner-picker" ref={root} data-testid="partner-tabs" onKeyDown={event => {
    if (!event.defaultPrevented && event.key === 'Escape' && open && !event.nativeEvent.isComposing && event.keyCode !== 229) { event.preventDefault(); event.stopPropagation(); setOpen(false); add.current?.focus(); }
  }}>
    <div className="partner-tabs" ref={row} role="tablist" aria-label={t('partner.label')} onKeyDown={arrows}>
      {keys.map(key => {
        const tab = extras.find(item => item.sessionId === key);
        const label = tab ? name(tab) : t('partner.main'); const active = key === activeKey;
        const testKey = tab ? partnerKey(tab.partner) : MAIN;
        // A disabled button shows no tooltip in every browser: the tab itself says why it cannot be chosen now.
        const title = locked && !active && lockedTitle ? lockedTitle : plainControls(`${t('partner.label')}: ${label}`);
        return <div key={key} className={`partner-tab${active ? ' active' : ''}${pendingTab === key ? ' pending' : ''}`} data-testid={`partner-tab-${testKey}`} title={title}>
          <button ref={button => { if (button) tabButtons.current.set(key, button); else tabButtons.current.delete(key); }} type="button" role="tab" className="partner-tab-main" aria-selected={active} aria-busy={pendingTab === key || undefined} tabIndex={active ? 0 : -1} disabled={locked && !active}
            title={title} onClick={() => { if (!locked) activate(key); }} onFocus={() => scrollTab(key)}>
            <Icon name={!tab ? 'chat' : tab.partner.kind === 'team' ? 'users' : 'user'} size={12} /><span className="partner-tab-text">{revealControls(label)}</span>
          </button>
          {tab && <button type="button" className="partner-tab-close" data-testid={`partner-close-${testKey}`} disabled={active && locked}
            title={plainControls(t('partner.close', { name: label }))} aria-label={plainControls(t('partner.close', { name: label }))} onClick={() => close(tab, label)}><Icon name="x" size={10} /></button>}
        </div>;
      })}
      <button ref={add} type="button" className="partner-tab-add" data-testid="partner-chip" disabled={locked} title={locked && lockedTitle ? lockedTitle : t('partner.add')} aria-label={t('partner.add')}
        aria-expanded={open} aria-haspopup="listbox" onClick={() => { if (!open) onRefreshRoster?.(); setOpen(!open); }}><Icon name="plus" size={12} /></button>
    </div>
    {tabs?.pendingAction && <span className="partner-pending" role="status" data-testid="partner-pending">
      {tabs.pendingAction === 'new' ? t('partner.pendingNew') : t('partner.pending', { name: pendingName || t('partner.main') })}
    </span>}
    {confirming && <ConfirmPopover anchorRef={root} testId="partner-close-confirm" className="partner-close-confirm"
      label={t('partner.close', { name: confirming.name })} question={t('partner.closeRunningQuestion', { name: confirming.name })}
      cancelLabel={t('session.cancel')} confirmLabel={t('partner.closeRunning')}
      cancelTestId="partner-close-cancel" confirmTestId="partner-close-accept" confirmDisabled={locked}
      onCancel={() => { setConfirming(undefined); tabButtons.current.get(confirming.tab.sessionId)?.focus(); }}
      onConfirm={() => closeConfirmed(confirming.tab)} />}
    {open && <PickerPopover placement="inline" anchorRef={root} id="partner-picker" testId="partner-popover" label={t('partner.add')} items={items}
      search={roles.length + teams.length > 10 ? { value: query, onChange: setQuery, placeholder: t('ui.search'), label: t('ui.search') } : undefined}
      onEscape={() => { setOpen(false); add.current?.focus(); }} onChoose={item => {
        const colon = item.id.indexOf(':');
        if (openPartner({ kind: item.id.slice(0, colon) as 'agent' | 'team', id: item.id.slice(colon + 1) })) setOpen(false);
      }} />}
  </div>;
}
