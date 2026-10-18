import { memo, useCallback, useEffect, useId, useLayoutEffect, useMemo, useRef, useState, type FocusEvent, type KeyboardEvent, type ReactNode, type RefObject } from 'react';
import { useUsageNow } from '../hooks';
import { focusControlOrInput } from '../composerControls';
import { useT } from '../i18n';
import type { SessionInfoPayload } from '../protocol';
import { filterModels, groupModels, modelKey, parseModelKey, hostProviderName, localizedProviderName, thinkingLabel, type SetupReply, type SetupState } from '../setup';
import { useSetupCommands } from '../setupCommands';
import { claimUsageRequest, USAGE_REPLY_TIMEOUT_MS, usageSummary } from '../usage';
import { reportStatus } from '../statusBar';
import { ClampedText } from './ClampedText';
import { FastToggle } from './ComposerMeters';
import { Icon, type IconName } from './Icon';
import { Note } from './Note';
import { plainControls, revealControls } from './RevealControls';

export { USAGE_REPLY_TIMEOUT_MS } from '../usage';

const PAGE_STEP = 6;

/** omp's thinking levels, lowest first (`omp --thinking`). */
const STANDARD_LEVELS: readonly string[] = ['off', 'minimal', 'low', 'medium', 'high', 'xhigh', 'max', 'auto'];

// ---- The popover -------------------------------------------------------------------------------------------------------

interface PickerItem {
  id: string;
  label: string;
  title?: string;
  current?: boolean;
  /** Muted text at the right edge (the raw level next to its translation). */
  hint?: string;
  /** Shown but not choosable (a thinking level the current model does not offer). */
  disabled?: boolean;
  /** Items with the same `groupKey` follow each other under one header (`group` is its text). */
  groupKey?: string;
  group?: string;
  /** Replaces the generic option test id (a few fixed lists name their rows). */
  testId?: string;
  /** A picture before the label (the send-target list's role and team pictures). */
  icon?: ReactNode;
  /** The row of where the user already is (the send-target list's own conversation): tinted; choosing it means "stay here". */
  own?: boolean;
}

interface PopoverProps {
  /** `up`: floats above the picker row (composer). `inline`: part of the page flow below the chip (account panel). */
  placement: 'up' | 'inline';
  /** The element whose top the floating popover must stay below (its room is measured from there). */
  anchorRef: RefObject<HTMLElement | null>;
  id: string;
  testId: string;
  /** Accessible name of the list. */
  label: string;
  items: PickerItem[];
  search?: { value: string; onChange: (value: string) => void; placeholder: string; label: string };
  header?: ReactNode;
  status?: ReactNode;
  footer?: ReactNode;
  onChoose: (item: PickerItem) => void;
  /** Closes this innermost popup before an enclosing editor handles Escape. */
  onEscape?: () => void;
  /** How the current item shows: a check in a leading column (default), or a tinted row with no check column (the send-target list). */
  markCurrent?: 'check' | 'background';
}

const optionId = (id: string, index: number): string => `${id}-o${index}`;

/**
 * A listbox with an optional search box: arrow keys move, Enter chooses (never while an IME composes), the mouse highlights what it
 * moves over. Escape and clicks outside are handled by the owner. Focus starts in the search box (or on the list).
 */
export function PickerPopover({ placement, anchorRef, id, testId, label, items, search, header, status, footer, onChoose, onEscape, markCurrent = 'check' }: PopoverProps) {
  const popRef = useRef<HTMLDivElement>(null);
  const listRef = useRef<HTMLDivElement>(null);
  const inputRef = useRef<HTMLInputElement>(null);
  const pointer = useRef<{ x: number; y: number } | null>(null);
  /** The row the user moved to; null = the default (the current item, or the first match while searching). */
  const [picked, setPicked] = useState<number | null>(null);

  const count = items.length;
  const ownIndex = items.findIndex((item) => item.own);
  const currentIndex = items.findIndex((item) => item.current);
  const defaultIndex = search?.value ? 0 : Math.max(0, currentIndex >= 0 ? currentIndex : ownIndex);
  const activeIndex = count === 0 ? -1 : Math.min(picked ?? defaultIndex, count - 1);
  const activeId = activeIndex >= 0 ? optionId(id, activeIndex) : undefined;

  useEffect(() => {
    (inputRef.current ?? listRef.current)?.focus({ preventScroll: true });
  }, []);

  // The focus fell to the page (the focused "retry" button went away with the error it belonged to): bring it back, so that the
  // keys (arrows, Enter, Escape) keep working.
  useEffect(() => {
    const active = document.activeElement;
    if (active === document.body || active === null) (inputRef.current ?? listRef.current)?.focus({ preventScroll: true });
  });

  // A floating popover never grows past the top of the visible page (below the tab bar): it gets the room above it.
  useLayoutEffect(() => {
    if (placement !== 'up') return;
    const pop = popRef.current;
    const anchor = anchorRef.current;
    if (!pop || !anchor) return;
    const apply = (): void => {
      // The popover's bottom edge is fixed by CSS (it may sit above the whole composer, not just the chip row): its room is
      // everything between that edge and the top of what is visible — the nearest ancestor that clips (the tab page under the
      // tab bar), else the window.
      let top = 0;
      for (let element = pop.parentElement; element; element = element.parentElement) {
        if (getComputedStyle(element).overflowY !== 'visible') {
          top = Math.max(0, element.getBoundingClientRect().top);
          break;
        }
      }
      const room = Math.min(pop.getBoundingClientRect().bottom, anchor.getBoundingClientRect().top) - top - 6;
      // In a very short pane the list gets what room there is (it scrolls) rather than a minimum that runs off the top.
      pop.style.setProperty('--pop-max', `${Math.floor(Math.max(90, Math.min(room, 440)))}px`);
    };
    apply();
    window.addEventListener('resize', apply);
    return () => window.removeEventListener('resize', apply);
  }, [placement, anchorRef]);

  useEffect(() => {
    if (activeId) document.getElementById(activeId)?.scrollIntoView({ block: 'nearest' });
  }, [activeId, items]);

  const runs = useMemo(() => {
    const out: { key: string; group?: string; entries: { item: PickerItem; index: number }[] }[] = [];
    items.forEach((item, index) => {
      const key = item.groupKey ?? '';
      const last = out[out.length - 1];
      if (last && last.key === key) last.entries.push({ item, index });
      else out.push({ key, group: item.group, entries: [{ item, index }] });
    });
    return out;
  }, [items]);

  const onKeyDown = (event: KeyboardEvent<HTMLDivElement>): void => {
    // The legacy IME keyCode must be checked as well: Enter that ends a Korean composition is not a choice.
    if (event.defaultPrevented || event.nativeEvent.isComposing || event.keyCode === 229) return;
    if (event.key === 'Escape' && onEscape) {
      event.preventDefault(); event.stopPropagation(); onEscape(); return;
    }
    const target = event.target;
    if (target !== inputRef.current && target !== listRef.current) return; // keys on the footer button are its own
    const go = (index: number): void => {
      event.preventDefault();
      if (count > 0) setPicked(Math.min(count - 1, Math.max(0, index)));
    };
    switch (event.key) {
      case 'ArrowDown':
        event.preventDefault();
        if (count > 0) setPicked((activeIndex + 1) % count);
        break;
      case 'ArrowUp':
        event.preventDefault();
        if (count > 0) setPicked((activeIndex <= 0 ? count : activeIndex) - 1);
        break;
      case 'PageDown':
        go(activeIndex + PAGE_STEP);
        break;
      case 'PageUp':
        go(activeIndex - PAGE_STEP);
        break;
      case 'Home':
        if (target === listRef.current) go(0); // in the search box Home moves the caret
        break;
      case 'End':
        if (target === listRef.current) go(count - 1);
        break;
      case 'Enter': {
        event.preventDefault();
        if (event.repeat) break; // a held key chooses nothing (the choice may open a confirmation that Enter would answer)
        const item = items[activeIndex];
        if (item && !item.disabled) onChoose(item);
        break;
      }
    }
  };

  return (
    // tabIndex -1: a click on text inside the popover leaves the focus within it (not on the page), so Escape still closes it.
    <div className={`picker-pop ${placement}`} ref={popRef} tabIndex={-1} data-testid={testId} onKeyDown={onKeyDown}>
      {header}
      {search && (
        <div className="picker-search">
          <Icon name="search" size={12} />
          <input
            ref={inputRef}
            type="text"
            className="picker-search-input"
            data-testid={`${testId}-search`}
            role="combobox"
            aria-expanded="true"
            aria-controls={id}
            aria-autocomplete="list"
            aria-activedescendant={activeId}
            aria-label={search.label}
            placeholder={search.placeholder}
            autoComplete="off"
            autoCapitalize="off"
            spellCheck={false}
            value={search.value}
            onChange={(event) => {
              setPicked(null);
              search.onChange(event.target.value);
            }}
          />
        </div>
      )}
      <div
        className="picker-list"
        id={id}
        ref={listRef}
        role="listbox"
        aria-label={label}
        tabIndex={search ? undefined : -1}
        aria-activedescendant={search ? undefined : activeId}
        data-testid={`${testId}-list`}
      >
        {runs.map((run) => {
          const options = run.entries.map(({ item, index }) => (
            <button
              type="button"
              role="option"
              tabIndex={-1}
              key={item.id}
              id={optionId(id, index)}
              className={`picker-option${index === activeIndex ? ' active' : ''}${item.disabled ? ' disabled' : ''}${item.own ? ' own' : ''}${markCurrent === 'background' && item.current ? ' current' : ''}`}
              aria-selected={item.current === true}
              aria-disabled={item.disabled || undefined}
              data-testid={item.testId ?? 'picker-option'}
              data-current={item.current ? 'true' : undefined}
              title={plainControls(item.title ?? item.label)}
              onMouseDown={(event) => event.preventDefault()} // keep the focus in the search box
              onMouseMove={(event) => {
                // Real pointer movement only: the synthetic move of a list scrolling under a still pointer (no movement, same point)
                // must not steal the keyboard's row. A real first move counts, so the row under the pointer lights up at once.
                const last = pointer.current;
                pointer.current = { x: event.clientX, y: event.clientY };
                const moved = event.movementX !== 0 || event.movementY !== 0 || (last !== null && (last.x !== event.clientX || last.y !== event.clientY));
                if (moved && index !== activeIndex) setPicked(index);
              }}
              onClick={() => {
                if (!item.disabled) onChoose(item);
              }}
            >
              {markCurrent === 'check' && <span className="picker-check" aria-hidden="true">
                {item.current && <Icon name="check" size={12} />}
              </span>}
              {item.icon && <span className="picker-icon" aria-hidden="true">{item.icon}</span>}
              <span className="picker-label">{revealControls(item.label)}</span>
              {item.hint && <span className="picker-hint">{revealControls(item.hint)}</span>}
            </button>
          ));
          return run.group !== undefined ? (
            <div role="group" aria-label={plainControls(run.group)} key={`g:${run.key}`}>
              <div className="picker-group" aria-hidden="true">
                {revealControls(run.group)}
              </div>
              {options}
            </div>
          ) : (
            options
          );
        })}
      </div>
      {status}
      {footer}
    </div>
  );
}

// ---- The chip (trigger) -------------------------------------------------------------------------------------------------

interface ChipProps {
  icon: IconName;
  text: string;
  title: string;
  ariaLabel: string;
  disabled: boolean;
  expanded: boolean;
  controls: string | undefined;
  busy: boolean;
  testId: string;
  chipRef: RefObject<HTMLButtonElement | null>;
  onClick: () => void;
}

function Chip({ icon, text, title, ariaLabel, disabled, expanded, controls, busy, testId, chipRef, onClick }: ChipProps) {
  return (
    <button
      type="button"
      ref={chipRef}
      className="chip"
      data-testid={testId}
      disabled={disabled}
      title={title}
      aria-label={ariaLabel}
      aria-haspopup="listbox"
      aria-expanded={expanded}
      aria-controls={expanded ? controls : undefined}
      aria-busy={busy || undefined}
      onClick={onClick}
      onKeyDown={(event) => {
        if (event.key === 'ArrowDown' && !expanded && !event.nativeEvent.isComposing) {
          event.preventDefault();
          onClick();
        }
      }}
    >
      {busy ? <span className="spinner" aria-hidden="true" /> : <Icon name={icon} size={12} />}
      <span className="chip-text">{revealControls(text)}</span>
      <Icon name="chevronDown" size={11} className="chip-caret" />
    </button>
  );
}

// ---- The pickers --------------------------------------------------------------------------------------------------------

export interface ModelPickersProps {
  session: SessionInfoPayload | null;
  setup: SetupState | null;
  /** Answers to this page's commands (see `AppState.setupReplies`). */
  replies: SetupReply[];
  /** omp is running: its models can be listed and switched. */
  connected: boolean;
  locale: string;
  /** `composer`: chips in a row, the popover floats above them. `panel`: stacked with labels, the popover opens in the flow. */
  variant: 'composer' | 'panel';
  /** Opens the account panel (the model popover offers it at its foot). */
  onManage?: () => void;
  /** Called after a choice or when a popover closes by choosing, so that the caller can put the focus where it belongs. */
  onChosen?: () => void;
  /** A permission request or question is open: an open popover gives way to it. */
  modal?: boolean;
  /** Slash commands request a picker without synthesizing DOM clicks. */
  pickerRequest?: { kind: 'model' | 'thinking'; revision: number };
}

type Kind = 'model' | 'thinking';

/**
 * The model and thinking-level pickers: two chips showing what is current, each opening a small listbox. The model list has a
 * search box and is grouped by provider; its header shows the tightest usage limit of the current model's provider when known.
 * Choosing sends `setModel` / `setThinking`; the chip shows the choice (with a spinner) until `session/info` confirms it or the
 * host answers with an error, which is shown under the chips.
 */
function ModelPickersView({ session, setup, replies, connected, locale, variant, onManage, onChosen, modal = false, pickerRequest }: ModelPickersProps) {
  const t = useT();
  const { post, feedback, clear } = useSetupCommands(replies);
  const baseId = useId();
  const rootRef = useRef<HTMLDivElement>(null);
  const modelChip = useRef<HTMLButtonElement>(null);
  const thinkingChip = useRef<HTMLButtonElement>(null);
  const [open, setOpen] = useState<Kind | null>(null);
  const [query, setQuery] = useState('');
  const [pendingModel, setPendingModel] = useState<{ key: string; name: string } | null>(null);
  const [pendingLevel, setPendingLevel] = useState<string | null>(null);
  const placement = variant === 'composer' ? 'up' : 'inline';

  const models = setup?.models;
  const providers = setup?.providers;
  const label = useCallback((provider: string) => localizedProviderName(hostProviderName(providers, provider), t), [providers, t]);

  // What is current: the session reports it (it follows `set_model` asynchronously); the setup state is the fallback.
  const sessionModel = session?.model;
  const known = sessionModel ?? setup?.currentModel;
  const currentKey = known ? modelKey(known) : '';
  const listedName = known ? models?.find((model) => model.provider === known.provider && model.id === known.id)?.name : undefined;
  const sessionName = sessionModel?.name;
  const currentName = known ? sessionName || listedName || known.id : undefined;
  const currentLevel = session?.thinkingLevel || setup?.currentThinking || undefined;

  const modelAnswer = feedback('setModel');
  const levelAnswer = feedback('setThinking');
  const modelsAnswer = feedback('models');
  const levelsAnswer = feedback('thinking');
  const usageAnswer = feedback('usage');

  // The chosen value stays on the chip until the session reports it; a failed or lost answer gives the old value back.
  useEffect(() => {
    if (!pendingModel) return;
    if (currentKey === pendingModel.key || modelAnswer.error) {
      setPendingModel(null);
      return;
    }
    if (!modelAnswer.done) return;
    const timer = window.setTimeout(() => setPendingModel(null), 5000);
    return () => window.clearTimeout(timer);
  }, [pendingModel, currentKey, modelAnswer.done, modelAnswer.error]);
  useEffect(() => {
    if (pendingLevel === null) return;
    if (currentLevel === pendingLevel || levelAnswer.error) {
      setPendingLevel(null);
      return;
    }
    if (!levelAnswer.done) return;
    const timer = window.setTimeout(() => setPendingLevel(null), 5000);
    return () => window.clearTimeout(timer);
  }, [pendingLevel, currentLevel, levelAnswer.done, levelAnswer.error]);

  // Without a running engine there is nothing to choose, and a permission request or question comes first.
  useEffect(() => {
    if (!connected || modal) setOpen(null);
  }, [connected, modal]);

  // A click outside, or the focus leaving, closes the popover (Escape is handled on the root).
  useEffect(() => {
    if (open === null) return;
    const onPointerDown = (event: PointerEvent): void => {
      if (event.target instanceof Node && rootRef.current?.contains(event.target)) return;
      setOpen(null);
    };
    document.addEventListener('pointerdown', onPointerDown, true);
    return () => document.removeEventListener('pointerdown', onPointerDown, true);
  }, [open]);

  const onRootBlur = (event: FocusEvent<HTMLDivElement>): void => {
    const next = event.relatedTarget;
    if (open !== null && next instanceof Node && !rootRef.current?.contains(next)) setOpen(null);
  };
  const onRootKeyDown = (event: KeyboardEvent<HTMLDivElement>): void => {
    if (event.defaultPrevented || event.key !== 'Escape' || open === null || event.nativeEvent.isComposing || event.keyCode === 229) return;
    event.preventDefault();
    event.stopPropagation(); // Escape closes the popover only; it must not also move the focus away (see useEscapeToComposer)
    const chip = open === 'model' ? modelChip : thinkingChip;
    setOpen(null);
    focusControlOrInput(chip.current, rootRef.current?.closest('.composer')?.querySelector<HTMLElement>('[data-pomp-composer]'));
  };

  // ---- commands ----
  const requestModels = (): void => {
    if (!feedback('models').pending) post('models', (requestId) => ({ requestId, action: 'listModels' }));
  };
  const requestLevels = (): void => {
    if (!feedback('thinking').pending) post('thinking', (requestId) => ({ requestId, action: 'listThinking' }));
  };

  const toggle = (kind: Kind): void => {
    if (!connected) return;
    if (open === kind) {
      setOpen(null);
      return;
    }
    setQuery('');
    setOpen(kind);
    if (kind === 'model') {
      if (!models || setup?.modelsError) requestModels();
      // The header shows the usage of the current model's provider; ask for it when it is missing (at most once a minute).
      if (claimUsageRequest(setup?.usage, setup?.usageLoading === true, Date.now())) {
        post('usage', (requestId) => ({ requestId, action: 'listUsage' }), undefined, USAGE_REPLY_TIMEOUT_MS);
      }
    } else {
      requestLevels(); // the levels depend on the current model, so they are asked for on every open
    }
  };

  const requestedRevision = useRef<number | undefined>(undefined);
  useEffect(() => {
    if (!pickerRequest || pickerRequest.revision === requestedRevision.current || !connected || modal) return;
    requestedRevision.current = pickerRequest.revision;
    if (open !== pickerRequest.kind) toggle(pickerRequest.kind);
  }, [pickerRequest, connected, modal]);

  const finish = (kind: Kind): void => {
    setOpen(null);
    if (onChosen) onChosen();
    else (kind === 'model' ? modelChip : thinkingChip).current?.focus();
  };

  const chooseModel = (item: PickerItem): void => {
    const model = parseModelKey(item.id);
    if (model && item.id !== currentKey) {
      const posted = post('setModel', (requestId) => ({ requestId, action: 'setModel', provider: model.provider, modelId: model.id }));
      if (posted) setPendingModel({ key: item.id, name: item.label });
    }
    finish('model');
  };

  const chooseLevel = (item: PickerItem): void => {
    if (item.id !== currentLevel) {
      const posted = post('setThinking', (requestId) => ({ requestId, action: 'setThinking', level: item.id }));
      if (posted) setPendingLevel(item.id);
    }
    finish('thinking');
  };

  // ---- the model list ----
  const modelItems = useMemo<PickerItem[]>(() => {
    if (!models) return [];
    return groupModels(filterModels(models, query, label), label).flatMap((group) =>
      group.models.map((model) => ({
        id: modelKey(model),
        label: model.name || model.id,
        title: `${model.provider}/${model.id}`,
        current: modelKey(model) === currentKey,
        groupKey: group.provider,
        group: group.label,
      })),
    );
  }, [models, query, label, currentKey]);

  const modelsError = setup?.modelsError ?? modelsAnswer.error;
  let modelStatus: ReactNode = null;
  if (!models) {
    modelStatus = modelsAnswer.pending || !modelsError ? (
      <div className="picker-status" role="status" data-testid="picker-loading">
        <span className="spinner" aria-hidden="true" />
        <span>{t('picker.loading')}</span>
      </div>
    ) : null;
  } else if (models.length === 0 || modelItems.length === 0) {
    modelStatus = null;
  } else if (modelsAnswer.pending) {
    modelStatus = (
      <div className="picker-status" role="status">
        <span className="spinner" aria-hidden="true" />
        <span>{t('picker.loading')}</span>
      </div>
    );
  }
  const modelFailure =
    modelsError && !modelsAnswer.pending ? (
      <div className="picker-status error" role="alert" data-testid="picker-error">
        <span className="picker-status-text">
          <strong>{t('picker.loadFailed')}</strong>
          <ClampedText text={modelsError} />
        </span>
        <button type="button" className="btn subtle" data-testid="picker-retry" onClick={requestModels}>
          {t('picker.retry')}
        </button>
      </div>
    ) : null;

  const usageNow = useUsageNow(setup?.usage);
  const summary = usageSummary(setup?.usage, known?.provider, usageNow, locale, t, known?.id);
  const usageBusy = setup?.usageLoading === true || usageAnswer.pending;
  const usageHeader = summary ? (
    <div className={`picker-usage ${summary.status}`} data-testid="picker-usage" title={plainControls(summary.title)}>
      <span className="picker-usage-text">{revealControls(summary.text)}</span>
      {summary.fraction !== undefined && (
        <span className="usage-bar thin" aria-hidden="true">
          <span className="usage-fill" style={{ width: `${Math.round(summary.fraction * 100)}%` }} />
        </span>
      )}
    </div>
  ) : usageBusy ? (
    <div className="picker-usage muted" role="status" data-testid="picker-usage-loading">
      <span className="spinner" aria-hidden="true" />
      <span className="picker-usage-text">{t('picker.usageChecking')}</span>
    </div>
  ) : undefined;

  const modelFooter = onManage ? (
    <div className="picker-foot">
      <button
        type="button"
        className="btn subtle small"
        data-testid="picker-manage"
        onClick={() => {
          setOpen(null);
          onManage();
        }}
      >
        {t('picker.manage')}
      </button>
    </div>
  ) : null;

  // ---- the thinking list ----
  const levels = setup?.thinkingLevels;
  const levelItems = useMemo<PickerItem[]>(() => {
    if (!levels) return [];
    // A reasoning model shows every standard level (max included); the ones omp does not offer for it are greyed out.
    const all = levels.length > 1 ? [...STANDARD_LEVELS.filter((level) => level !== 'auto' || levels.includes(level)), ...levels.filter((level) => !STANDARD_LEVELS.includes(level))] : levels;
    return all.map((level) => {
      const shown = thinkingLabel(t, level);
      const offered = levels.includes(level);
      return {
        id: level,
        label: shown,
        title: offered ? level : `${level}: ${t('picker.levelUnsupported')}`,
        current: level === currentLevel,
        disabled: !offered,
        hint: !offered ? t('picker.levelUnsupported') : shown.toLowerCase() === level.toLowerCase() ? undefined : level,
      };
    });
  }, [levels, currentLevel, t]);
  let levelStatus: ReactNode = null;
  if (!levels) {
    levelStatus = levelsAnswer.error ? (
      <div className="picker-status error" role="alert" data-testid="picker-error">
        <span className="picker-status-text">
          <strong>{t('picker.thinkingLoadFailed')}</strong>
          <ClampedText text={levelsAnswer.error} />
        </span>
        <button type="button" className="btn subtle" data-testid="picker-retry" onClick={requestLevels}>
          {t('picker.retry')}
        </button>
      </div>
    ) : (
      <div className="picker-status" role="status" data-testid="picker-loading">
        <span className="spinner" aria-hidden="true" />
        <span>{t('picker.thinkingLoading')}</span>
      </div>
    );
  } else if (levels.length <= 1) {
    levelStatus = (
      <div className="picker-status" role="status" data-testid="picker-fixed">
        <span>{t('picker.thinkingFixed')}</span>
      </div>
    );
  }

  // ---- what the chips show ----
  const shownModel = pendingModel ? pendingModel.name : currentName;
  const shownLevel = pendingLevel ?? currentLevel;
  const modelText = shownModel ?? t('status.noModel');
  const levelText = shownLevel ? thinkingLabel(t, shownLevel) : t('picker.thinkingShort');
  // Attributes cannot carry markup: host text in them gets its control characters shown as markers (plainControls).
  // The model chip names the model only; the context usage has its own meter (and tooltip) above the input.
  const modelTitle = plainControls(known ? `${t('status.model')}: ${known.provider}/${known.id}` : t('status.noModel'));
  const levelTitle = plainControls(`${t('status.thinking')}: ${shownLevel ?? '-'}`);
  const modelListId = `${baseId}-models`;
  const levelListId = `${baseId}-levels`;
  const inline = placement === 'inline';

  const modelPopover =
    open === 'model' ? (
      <PickerPopover
        placement={placement}
        anchorRef={rootRef}
        id={modelListId}
        testId="model-popover"
        label={t('status.model')}
        items={modelItems}
        search={{ value: query, onChange: setQuery, placeholder: t('picker.searchPlaceholder'), label: t('picker.search') }}
        header={usageHeader}
        status={
          <>
            {modelStatus}
            {modelFailure}
          </>
        }
        footer={modelFooter}
        onChoose={chooseModel}
      />
    ) : null;
  const levelPopover =
    open === 'thinking' ? (
      <PickerPopover
        placement={placement}
        anchorRef={rootRef}
        id={levelListId}
        testId="thinking-popover"
        label={t('status.thinking')}
        items={levelItems}
        status={levelStatus}
        onChoose={chooseLevel}
      />
    ) : null;

  const modelChipView = (
    <Chip
      icon="cpu"
      text={modelText}
      title={modelTitle}
      ariaLabel={plainControls(t('picker.modelAria', { name: modelText }))}
      disabled={!connected}
      expanded={open === 'model'}
      controls={modelListId}
      busy={pendingModel !== null && !modelAnswer.error}
      testId="model-chip"
      chipRef={modelChip}
      onClick={() => toggle('model')}
    />
  );
  const levelChipView = (
    <Chip
      icon="brain"
      text={levelText}
      title={levelTitle}
      ariaLabel={plainControls(t('picker.thinkingAria', { name: levelText }))}
      disabled={!connected}
      expanded={open === 'thinking'}
      controls={levelListId}
      busy={pendingLevel !== null && !levelAnswer.error}
      testId="thinking-chip"
      chipRef={thinkingChip}
      onClick={() => toggle('thinking')}
    />
  );

  // Above the input a failed change goes to the VS status bar (and the POMP log) and is cleared at once, so the chip row stays as it is;
  // in the account panel it stays as a note under its field.
  const composerNotes = variant === 'composer';
  useEffect(() => {
    if (!composerNotes || !modelAnswer.error) return;
    reportStatus(`${t('picker.modelFailed')} — ${modelAnswer.error}`);
    clear('setModel');
  }, [composerNotes, modelAnswer.error]);
  useEffect(() => {
    if (!composerNotes || !levelAnswer.error) return;
    reportStatus(`${t('picker.thinkingFailed')} — ${levelAnswer.error}`);
    clear('setThinking');
  }, [composerNotes, levelAnswer.error]);
  const notes = composerNotes ? null : (
    <>
      {modelAnswer.error && (
        <Note tone="error" text={t('picker.modelFailed')} detail={modelAnswer.error} onDismiss={() => clear('setModel')} />
      )}
      {levelAnswer.error && (
        <Note tone="error" text={t('picker.thinkingFailed')} detail={levelAnswer.error} onDismiss={() => clear('setThinking')} />
      )}
    </>
  );

  return (
    <div className={`pickers pickers-${variant}`} ref={rootRef} data-testid={`pickers-${variant}`} onKeyDown={onRootKeyDown} onBlur={onRootBlur}>
      {inline ? (
        <>
          <div className="picker-field">
            <span className="picker-field-label">{t('setup.model.current')}</span>
            <div className="picker">
              {modelChipView}
              {modelPopover}
            </div>
          </div>
          <div className="picker-field">
            <span className="picker-field-label">{t('status.thinking')}</span>
            <div className="picker">
              {levelChipView}
              {levelPopover}
            </div>
          </div>
        </>
      ) : (
        <>
          <div className="picker-row">
            <div className="picker picker-model">{modelChipView}</div>
            <div className="picker picker-thinking">{levelChipView}</div>
            <FastToggle fast={session?.fastMode} connected={connected} />
          </div>
          {modelPopover}
          {levelPopover}
        </>
      )}
      {notes && (modelAnswer.error || levelAnswer.error) && <div className="picker-notes">{notes}</div>}
    </div>
  );
}

export const ModelPickers = memo(ModelPickersView);
