import { useEffect, useRef, useState, type KeyboardEvent } from 'react';
import { useT } from '../i18n';
import { send, WORKSPACE_TRUST_REQUIRED, type BridgeStatePayload, type EngineInfo } from '../protocol';
import { engineSummary } from './EngineNotice';
import { Icon, type IconName } from './Icon';
import { plainControls } from './RevealControls';
import type { TabBadges } from '../tabBadges';

export type ShellTab = 'chat' | 'work' | 'agents' | 'tools' | 'accounts';
const tabs: { id: ShellTab; icon: IconName }[] = [
  { id: 'chat', icon: 'chat' }, { id: 'work', icon: 'work' }, { id: 'agents', icon: 'users' }, { id: 'tools', icon: 'tool' }, { id: 'accounts', icon: 'user' },
];

/**
 * `badges`: the red counts of what is new on Chat and Work (zero for the tab on screen); `accountAttention`: a red dot while an account
 * needs the user, not on the Accounts tab itself. The mark sits on the icon's corner and takes no room, so the row never shifts or wraps
 * (docs/screen-guide.md "탭 막대").
 */
export function TabBar({ active, onChange, bridge, required, badges, accountAttention, inert, engine = null }: {
  active: ShellTab; onChange: (tab: ShellTab) => void; bridge: BridgeStatePayload; required: boolean;
  badges: TabBadges; accountAttention: boolean; inert: boolean; engine?: EngineInfo | null;
}) {
  const t = useT();
  const root = useRef<HTMLDivElement>(null);
  const more = useRef<HTMLButtonElement>(null);
  const [menu, setMenu] = useState(false);
  const close = () => { setMenu(false); more.current?.focus(); };
  useEffect(() => {
    if (!menu) return;
    const pointer = (event: PointerEvent) => { if (event.target instanceof Node && !root.current?.contains(event.target)) setMenu(false); };
    document.addEventListener('pointerdown', pointer, true);
    return () => document.removeEventListener('pointerdown', pointer, true);
  }, [menu]);
  useEffect(() => { if (inert) setMenu(false); }, [inert]);
  const keys = (event: KeyboardEvent<HTMLDivElement>) => {
    if (event.nativeEvent.isComposing || event.keyCode === 229) return;
    const target = event.target as HTMLElement;
    if (target.getAttribute('role') !== 'tab' || !['ArrowLeft', 'ArrowRight', 'Home', 'End'].includes(event.key)) return;
    event.preventDefault();
    const enabled = tabs.filter(tab => !required || tab.id === 'accounts');
    const index = enabled.findIndex(tab => tab.id === target.dataset.tab);
    const next = event.key === 'Home' ? enabled[0]! : event.key === 'End' ? enabled.at(-1)! : enabled[(index + (event.key === 'ArrowRight' ? 1 : -1) + enabled.length) % enabled.length]!;
    onChange(next.id); root.current?.querySelector<HTMLButtonElement>(`[data-tab="${next.id}"]`)?.focus();
  };
  const engineLine = engine ? engineSummary(t, engine) : null;
  const status = required ? t('bridge.setupRequired') : bridge.state === 'failed' && bridge.reason === WORKSPACE_TRUST_REQUIRED ? t('bridge.trustRequired') : t(`bridge.${bridge.state}`);
  return <div className="tabbar" ref={root} inert={inert || undefined}>
    <div className="shell-tabs" role="tablist" aria-label="POMP" onKeyDown={keys}>
      {tabs.map(tab => {
        const label = t(`tab.${tab.id}`);
        const disabled = required && tab.id !== 'accounts';
        const count = tab.id === 'chat' ? badges.chat : tab.id === 'work' ? badges.work : 0;
        const dot = active !== tab.id && tab.id === 'accounts' && accountAttention;
        const shown = count > 99 ? '99+' : String(count);
        const name = count > 0 ? t('tab.unseen', { label, count: shown }) : dot ? t('tab.attention', { label }) : label;
        return <button key={tab.id} id={`tab-${tab.id}`} data-tab={tab.id} data-testid={`tab-${tab.id}`} type="button" role="tab"
          aria-selected={active === tab.id} aria-controls={`page-${tab.id}`} tabIndex={active === tab.id ? 0 : -1}
          className="shell-tab" disabled={disabled} title={name} aria-label={name} onClick={() => onChange(tab.id)}>
          <span className="tab-icon"><Icon name={tab.icon} size={13} />
            {count > 0 ? <span className="tab-count" data-testid={`${tab.id}-badge`} aria-hidden="true">{shown}</span>
              : dot && <span className="tab-dot" data-testid={`${tab.id}-attention`} aria-hidden="true" />}</span>
          <span className="tab-label">{label}</span>
        </button>;
      })}
    </div>
    <div className="tab-actions">
      <button className={`icon-btn state-${required ? 'setup' : bridge.state}`} type="button" data-testid="connection-dot" title={`${status} · ${t('tab.reconnect')}`} aria-label={`${status} · ${t('tab.reconnect')}`} onClick={() => send('bridge/restart', {})}><span className="dot" /></button>
      <button ref={more} className="icon-btn" type="button" data-testid="shell-more" title={t('tab.more')} aria-label={t('tab.more')} aria-haspopup="menu" aria-expanded={menu} onClick={() => setMenu(!menu)}><Icon name="more" /></button>
      <button className="icon-btn" type="button" data-testid="settings" title={t('status.settings')} aria-label={t('status.settings')} onClick={() => send('settings/open', {})}><Icon name="settings" /></button>
    </div>
    {menu && <div className="shell-menu" role="menu" data-testid="shell-menu" onKeyDown={event => {
      if (event.nativeEvent.isComposing || event.keyCode === 229) return;
      if (event.key === 'Escape') { event.preventDefault(); event.stopPropagation(); close(); }
      if (event.key === 'ArrowDown' || event.key === 'ArrowUp') {
        event.preventDefault(); const items = [...event.currentTarget.querySelectorAll<HTMLButtonElement>('button')];
        const index = items.indexOf(document.activeElement as HTMLButtonElement); items[(index + (event.key === 'ArrowDown' ? 1 : -1) + items.length) % items.length]?.focus();
      }
    }}>
      <button autoFocus type="button" role="menuitem" onClick={() => { send('log/open', {}); close(); }}>{t('banner.showLog')}</button>
      <button type="button" role="menuitem" onClick={() => { send('bridge/restart', {}); close(); }}>{t('setup.restart')}</button>
      {engine && engineLine && <div className={`shell-menu-info${engine.verified ? '' : ' unverified'}`} role="none" data-testid="engine-info" title={engine.path ? plainControls(engine.path) : undefined}>{engineLine}</div>}
    </div>}
  </div>;
}
