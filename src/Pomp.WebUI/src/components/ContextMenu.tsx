import { useCallback, useEffect, useRef, useState, type ReactNode } from 'react';
import './context-menu.css';

export interface ContextMenuItem {
  id: string;
  label: string;
  testId?: string;
  onSelect: () => void;
}

/**
 * Renderless context-menu host. Call `menuProps(items)` on a target element: right-click
 * (or the ContextMenu key / Shift+F10 while it is focused) opens a role=menu popup at the
 * pointer, clamped inside the viewport. Esc, outside pointerdown/scroll, or focus leaving
 * the menu closes it and returns focus to the invoker. Never pass items when no actions
 * apply, and never attach to elements inside input/textarea/contenteditable ??the native
 * menu stays there.
 */
export function useContextMenu() {
  const [pos, setPos] = useState<{ x: number; y: number } | null>(null);
  const invokerRef = useRef<HTMLElement | null>(null);
  const menuRef = useRef<HTMLDivElement>(null);
  const itemsRef = useRef<ContextMenuItem[]>([]);
  // Focus the first item only for keyboard opens; pointer users keep their position.
  const focusFirstRef = useRef(false);

  const close = useCallback((restoreFocus = true): void => {
    const invoker = invokerRef.current;
    invokerRef.current = null;
    itemsRef.current = [];
    setPos(null);
    if (restoreFocus && invoker?.isConnected) invoker.focus({ preventScroll: true });
  }, []);

  useEffect(() => {
    if (!pos) return;
    if (focusFirstRef.current) {
      focusFirstRef.current = false;
      menuRef.current?.querySelector<HTMLButtonElement>('[role="menuitem"]')?.focus();
    }
    const onPointerDown = (event: PointerEvent): void => {
      if (event.target instanceof Node && menuRef.current?.contains(event.target)) return;
      close();
    };
    const onScroll = (): void => close(false);
    const onFocusOut = (): void => {
      window.requestAnimationFrame(() => {
        if (!menuRef.current?.contains(document.activeElement)) close(false);
      });
    };
    document.addEventListener('pointerdown', onPointerDown, true);
    document.addEventListener('scroll', onScroll, true);
    document.addEventListener('focusout', onFocusOut);
    return () => {
      document.removeEventListener('pointerdown', onPointerDown, true);
      document.removeEventListener('scroll', onScroll, true);
      document.removeEventListener('focusout', onFocusOut);
    };
  }, [pos, close]);

  const openAt = useCallback(
    (invoker: HTMLElement, clientX: number, clientY: number, items: ContextMenuItem[], keyboard: boolean): void => {
      if (items.length === 0) return;
      const margin = 8;
      const width = 240;
      const height = Math.min(320, 8 + items.length * 36);
      invokerRef.current = invoker;
      itemsRef.current = items;
      focusFirstRef.current = keyboard;
      setPos({
        x: Math.max(margin, Math.min(clientX, window.innerWidth - width - margin)),
        y: Math.max(margin, Math.min(clientY, window.innerHeight - height - margin)),
      });
    },
    [],
  );

  function menuProps(items: () => ContextMenuItem[]) {
    return {
      onContextMenu: (event: React.MouseEvent): void => {
        const target = event.target as Element | null;
        if (target?.closest('input, textarea, [contenteditable="true"], [contenteditable=""]')) return;
        const resolved = items();
        if (resolved.length === 0) return;
        event.preventDefault();
        event.stopPropagation();
        openAt(event.currentTarget as HTMLElement, event.clientX, event.clientY, resolved, false);
      },
      onKeyDown: (event: React.KeyboardEvent): void => {
        if (event.key !== 'ContextMenu' && !(event.shiftKey && event.key === 'F10')) return;
        const resolved = items();
        if (resolved.length === 0) return;
        event.preventDefault();
        event.stopPropagation();
        const rect = (event.currentTarget as HTMLElement).getBoundingClientRect();
        openAt(event.currentTarget as HTMLElement, rect.left + rect.width / 2, rect.top + rect.height / 2, resolved, true);
      },
    };
  }

  function renderMenu(): ReactNode {
    if (!pos) return null;
    const items = itemsRef.current;
    return (
      <div ref={menuRef} role="menu" data-testid="context-menu" className="context-menu" style={{ left: pos.x, top: pos.y }}>
        {items.map((item) => (
          <button
            key={item.id}
            type="button"
            role="menuitem"
            data-testid={item.testId ?? `context-menu-item-${item.id}`}
            className="context-menu-item"
            onClick={() => {
              const run = item.onSelect;
              close(false);
              run();
            }}
            onKeyDown={(event) => {
              const menu = (event.currentTarget as HTMLElement).closest('[data-testid="context-menu"]');
              const entries = menu ? Array.from(menu.querySelectorAll<HTMLButtonElement>('[role="menuitem"]')) : [];
              const at = entries.indexOf(event.currentTarget as HTMLButtonElement);
              let next = -1;
              if (event.key === 'ArrowDown') next = (at + 1) % entries.length;
              else if (event.key === 'ArrowUp') next = (at + entries.length - 1) % entries.length;
              else if (event.key === 'Home') next = 0;
              else if (event.key === 'End') next = entries.length - 1;
              else if (event.key === 'Escape') {
                event.preventDefault();
                event.stopPropagation();
                close();
                return;
              } else if (event.key === 'Tab') {
                event.preventDefault();
                next = event.shiftKey ? (at + entries.length - 1) % entries.length : (at + 1) % entries.length;
              } else if (event.key === ' ' || event.key === 'Enter') {
                // Native button activation runs onClick; only stop propagation so the
                // bubble-level menu key handler does not reopen the menu.
                event.stopPropagation();
                return;
              } else return;
              event.preventDefault();
              event.stopPropagation();
              entries[next]?.focus();
            }}
          >
            {item.label}
          </button>
        ))}
      </div>
    );
  }

  return { menuProps, renderMenu, closeMenu: close };
}

/**
 * Documents that a POMP menu claimed this right-click. The document-level guard
 * (installContextMenuGuard) suppresses the browser menu everywhere else; targets
 * that open a POMP menu call markMenuHandled(event) so the guard leaves them alone.
 */
const HANDLED = 'pomp-menu-handled';

export function markMenuHandled(event: Event): void {
  (event as Event & { [HANDLED]?: boolean })[HANDLED] = true;
}

function isMenuHandled(event: Event): boolean {
  return (event as Event & { [HANDLED]?: boolean })[HANDLED] === true;
}

function inEditable(target: EventTarget | null): boolean {
  if (!(target instanceof Element)) return false;
  const field = target.closest('input, textarea, select, [contenteditable="true"], [contenteditable=""]');
  if (!field) return false;
  if (field instanceof HTMLInputElement && (field.type === 'checkbox' || field.type === 'radio')) return false;
  return true;
}

let guardInstalled = false;

/**
 * Document-level contextmenu guard, installed once from main.tsx. Complements the
 * WebView2 host side (which filters its own native menu down to editing items):
 * - inside editable fields: do nothing, so the host-filtered edit menu appears;
 * - where a POMP menu already handled the event (markMenuHandled): do nothing;
 * - everywhere else: preventDefault, so no browser page menu (back/reload/save/print)
 *   appears over the chat UI. In the dev preview (plain Edge) this is what removes
 *   the browser menu; in WebView2 it is a backstop behind the host filter.
 */
export function installContextMenuGuard(): void {
  if (guardInstalled) return;
  guardInstalled = true;
  document.addEventListener(
    'contextmenu',
    (event: MouseEvent) => {
      if (isMenuHandled(event)) return;
      if (inEditable(event.target)) return;
      event.preventDefault();
    },
    true,
  );
}
