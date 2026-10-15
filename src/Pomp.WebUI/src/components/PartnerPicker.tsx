import { roleTitle } from '../bundledDescriptions';
import { useEffect, useLayoutEffect, useRef, useState, type KeyboardEvent } from 'react';
import type { AgentsStatePayload } from '../protocol';
import type { CommandReply, ConversationPartner, PartnerState } from '../conversationProtocol';
import { useConversationCommands } from '../conversationCommands';
import { useT } from '../i18n';
import { effectiveRoles, isRunnableTeam } from '../teamsCache';
import { PickerPopover } from './ModelPickers';
import { matches } from './ui';
import { Icon } from './Icon';
import { plainControls, revealControls } from './RevealControls';

/** "Default (POMP)" plus any number of agent/team tabs (the row scrolls). The remembered tabs of all conversations stay under this size. */
const STORE_KEY = 'pomp.partnerTabs';
const MAX_STORED_CHARS = 256 * 1024;
const keyOf = (partner: ConversationPartner) => partner.kind === 'main' || !partner.id ? 'main' : `${partner.kind}:${partner.id}`;
const partnerOf = (key: string): ConversationPartner => {
  const colon = key.indexOf(':');
  return colon < 0 ? { kind: 'main' } : { kind: key.slice(0, colon) as 'agent' | 'team', id: key.slice(colon + 1) };
};

// Open tabs are a per-viewer convenience: the partner itself is host state (conversation/partnerState).
function readTabs(sessionId: string): string[] {
  try {
    const value = JSON.parse(localStorage.getItem(STORE_KEY) ?? '{}')?.[sessionId];
    return Array.isArray(value) ? value.filter((key): key is string => typeof key === 'string' && /^(agent|team):[^\x00-\x1f]{1,200}$/.test(key)) : [];
  } catch { return []; }
}
function writeTabs(sessionId: string, tabs: string[]) {
  try {
    const all = JSON.parse(localStorage.getItem(STORE_KEY) ?? '{}') ?? {};
    delete all[sessionId]; if (tabs.length) all[sessionId] = tabs;
    // The conversations used longest ago (first in insertion order) are forgotten first once the store passes its size.
    for (const stale of Object.keys(all)) { if (JSON.stringify(all).length <= MAX_STORED_CHARS || stale === sessionId) break; delete all[stale]; }
    localStorage.setItem(STORE_KEY, JSON.stringify(all));
  } catch { /* storage unavailable: tabs last for this page only */ }
}

/** Conversation partner tabs: Default (POMP) first, added agents/teams to the right, each closable. The active tab is the partner. */
export function PartnerPicker({ state, agents, replies, sessionId, disabled, request, onRefreshRoster }: {
  state: PartnerState | null; agents: AgentsStatePayload | null; replies: CommandReply[]; sessionId?: string; disabled: boolean;
  /** Open (or switch to) a partner tab from elsewhere: the Agents tab or `/agent name`. Applied once the strip is unlocked. */
  request?: { key: string; seq: number };
  /** Asks the host to rescan agents and teams, so a role or team file created outside POMP shows up in the add list. */
  onRefreshRoster?: () => void;
}) {
  const t = useT(); const [open, setOpen] = useState(false); const [stored, setStored] = useState<string[]>([]);
  const [query, setQuery] = useState(''); useEffect(() => { if (!open) setQuery(''); }, [open]);
  const [notice, setNotice] = useState<string>(); const handled = useRef(request?.seq ?? 0); const pendingKey = useRef<string | undefined>(undefined);
  const root = useRef<HTMLDivElement>(null); const add = useRef<HTMLButtonElement>(null);
  const row = useRef<HTMLDivElement>(null);
  const tabButtons = useRef(new Map<string, HTMLButtonElement>());
  // Switching disables the chosen tab until the host acknowledges it. Keep the intent, not the
  // temporarily disabled DOM node, and restore focus after the strip unlocks.
  const focusKey = useRef<string | undefined>(undefined);
  const { post, pending, error } = useConversationCommands(replies, sessionId);
  const scopedState = state && sessionId && state.sessionId === sessionId ? state : null;
  const partner = scopedState?.partner ?? { kind: 'main' as const };
  const current = keyOf(partner);
  const roles = effectiveRoles(agents?.roles ?? []).filter(role => !role.error);
  const teams = (agents?.teams ?? []).filter(team => isRunnableTeam(team, agents));
  const label = (key: string) => {
    if (key === 'main') return t('partner.main');
    const { kind, id } = partnerOf(key);
    if (kind !== 'agent' && key === current && scopedState?.title) return scopedState.title;
    return (kind === 'agent' ? roleTitle(roles.find(role => role.name === id), t, id) : teams.find(team => team.id === id)?.title) || id || '';
  };
  // Until the roster is known, keep every stored tab; afterwards drop tabs whose agent or team no longer exists.
  const known = (key: string) => !agents || key === current || (key.startsWith('agent:') ? roles.some(role => `agent:${role.name}` === key) : teams.some(team => `team:${team.id}` === key));
  const extras = [...stored.filter(known), ...(current !== 'main' && !stored.includes(current) ? [current] : [])];
  const tabs = ['main', ...extras];
  const locked = disabled || pending || !sessionId;

  useEffect(() => { setOpen(false); setStored(sessionId ? readTabs(sessionId) : []); }, [sessionId]);
  useEffect(() => { if (disabled) setOpen(false); }, [disabled]);
  // A partner chosen elsewhere (another page, a restored conversation) stays open as a tab after switching back.
  useEffect(() => {
    if (!sessionId || current === 'main') return;
    // Read storage, not state: on a conversation switch the state still holds the previous conversation's tabs.
    const saved = readTabs(sessionId);
    if (!saved.includes(current)) { const next = [...saved, current]; setStored(next); writeTabs(sessionId, next); }
  }, [sessionId, current]);
  useEffect(() => {
    if (!open) return;
    const outside = (event: PointerEvent) => { if (event.target instanceof Node && !root.current?.contains(event.target)) setOpen(false); };
    document.addEventListener('pointerdown', outside, true); return () => document.removeEventListener('pointerdown', outside, true);
  }, [open]);

  const save = (next: string[]) => { setStored(next); if (sessionId) writeTabs(sessionId, next); };
  const activate = (key: string, restoreFocus = false) => {
    const accepted = key === current || post('conversation/partner', { partner: partnerOf(key) });
    if (accepted && restoreFocus) {
      if (key === current) tabButtons.current.get(key)?.focus({ preventScroll: true });
      else focusKey.current = key;
    }
    return accepted;
  };
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
    if (locked || focusKey.current === undefined) return;
    const key = focusKey.current; focusKey.current = undefined;
    const button = tabButtons.current.get(key);
    (button && !button.disabled ? button : add.current)?.focus({ preventScroll: true });
    scrollTab(key);
  });
  useLayoutEffect(() => { scrollTab(current); }, [current, tabs.join('\n')]);
  useEffect(() => {
    const strip = row.current;
    if (!strip) return;
    const observer = new ResizeObserver(() => scrollTab(current));
    observer.observe(strip); return () => observer.disconnect();
  }, [current]);
  // A tab requested elsewhere appears at once; it becomes the partner when the strip is unlocked (not during a reply).
  useEffect(() => {
    if (!request || request.seq === handled.current) return;
    handled.current = request.seq; setNotice(undefined);
    if (!sessionId) return;
    if (!extras.includes(request.key)) save([...extras, request.key]);
    pendingKey.current = request.key;
  }, [request?.seq]); // eslint-disable-line react-hooks/exhaustive-deps
  useEffect(() => {
    const key = pendingKey.current;
    if (!key || locked) return;
    pendingKey.current = undefined;
    activate(key);
  });
  useEffect(() => { pendingKey.current = undefined; focusKey.current = undefined; setNotice(undefined); }, [sessionId]);
  const close = (key: string) => {
    const index = tabs.indexOf(key);
    const next = key === current ? tabs[index - 1] ?? 'main' : current;
    if (key === current && !activate(next, true)) return;
    focusKey.current = next;
    save(extras.filter(item => item !== key));
  };
  const items = [
    ...roles.map(role => ({ id: `agent:${role.name}`, label: roleTitle(role, t), groupKey: 'agents', group: t('partner.agents') })),
    ...teams.map(team => ({ id: `team:${team.id}`, label: team.title || team.id, groupKey: 'teams', group: t('partner.teams') })),
  ].filter(item => matches(query, item.label, item.id.slice(item.id.indexOf(':') + 1))).map(item => {
    const isOpen = extras.includes(item.id);
    return { ...item, current: item.id === current, hint: isOpen ? t('partner.open') : undefined };
  });
  const keys = (event: KeyboardEvent<HTMLDivElement>) => {
    if (!['ArrowLeft', 'ArrowRight', 'Home', 'End'].includes(event.key) || !(event.target instanceof HTMLElement) || event.target.getAttribute('role') !== 'tab') return;
    const buttons = [...(root.current?.querySelectorAll<HTMLButtonElement>('[role="tab"]') ?? [])];
    const index = buttons.indexOf(event.target as HTMLButtonElement);
    const next = event.key === 'Home' ? 0 : event.key === 'End' ? buttons.length - 1 : (index + (event.key === 'ArrowRight' ? 1 : -1) + buttons.length) % buttons.length;
    event.preventDefault(); buttons[next]?.focus(); scrollTab(tabs[next]!);
  };

  return <div className="partner-picker" ref={root} data-testid="partner-tabs" onKeyDown={event => {
    if (!event.defaultPrevented && event.key === 'Escape' && open && !event.nativeEvent.isComposing && event.keyCode !== 229) { event.preventDefault(); event.stopPropagation(); setOpen(false); add.current?.focus(); }
  }}>
    <div className="partner-tabs" ref={row} role="tablist" aria-label={t('partner.label')} onKeyDown={keys}>
      {tabs.map(key => {
        const name = label(key); const active = key === current;
        return <div key={key} className={`partner-tab${active ? ' active' : ''}`} data-testid={`partner-tab-${key}`}>
          <button ref={button => { if (button) tabButtons.current.set(key, button); else tabButtons.current.delete(key); }} type="button" role="tab" className="partner-tab-main" aria-selected={active} tabIndex={active ? 0 : -1} disabled={locked && !active}
            title={plainControls(`${t('partner.label')}: ${name}`)} onClick={() => { if (!locked) activate(key, true); }} onFocus={() => scrollTab(key)}>
            <Icon name={key === 'main' ? 'chat' : key.startsWith('team:') ? 'users' : 'user'} size={12} /><span className="partner-tab-text">{revealControls(name)}</span>
          </button>
          {key !== 'main' && <button type="button" className="partner-tab-close" data-testid={`partner-close-${key}`} disabled={active && locked}
            title={plainControls(t('partner.close', { name }))} aria-label={plainControls(t('partner.close', { name }))} onClick={() => close(key)}><Icon name="x" size={10} /></button>}
        </div>;
      })}
      <button ref={add} type="button" className="partner-tab-add" data-testid="partner-chip" disabled={locked} title={t('partner.add')} aria-label={t('partner.add')}
        aria-expanded={open} aria-haspopup="listbox" onClick={() => { if (!open) onRefreshRoster?.(); setOpen(!open); }}><Icon name="plus" size={12} /></button>
    </div>
    {open && <PickerPopover placement="inline" anchorRef={root} id="partner-picker" testId="partner-popover" label={t('partner.add')} items={items}
      search={roles.length + teams.length > 10 ? { value: query, onChange: setQuery, placeholder: t('ui.search'), label: t('ui.search') } : undefined}
      onEscape={() => { setOpen(false); add.current?.focus(); }} onChoose={item => {
        if (!extras.includes(item.id)) save([...extras, item.id]);
        if (activate(item.id, true)) setOpen(false);
      }} />}
    {(error || notice || scopedState?.message) && <span className="partner-message" role={error || notice ? 'alert' : 'status'} data-testid="partner-message">{revealControls(error || notice || scopedState?.message || '')}</span>}
  </div>;
}
