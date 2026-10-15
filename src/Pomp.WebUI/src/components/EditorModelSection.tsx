import { useCallback, useEffect, useId, useMemo, useRef, useState, type KeyboardEvent } from 'react';
import { useT } from '../i18n';
import type { EditorModelChoice, ModelInfo, SetupModel } from '../protocol';
import { filterModels, groupModels, modelKey, parseModelKey, providerName, localizedProviderName, type SetupReply, type SetupState } from '../setup';
import { useSetupCommands } from '../setupCommands';
import { ClampedText } from './ClampedText';
import { Icon } from './Icon';
import { PickerPopover } from './ModelPickers';
import { Note } from './Note';
import { plainControls, revealControls } from './RevealControls';
import { ViewSection } from './ViewSection';
import { Badge } from './ui';

interface EditorModelSectionProps {
  setup: SetupState;
  replies: SetupReply[];
  sessionModel?: ModelInfo;
  connected: boolean;
  inert: boolean;
  /** First-run connection comes first; this optional card starts collapsed. */
  defaultCollapsed?: boolean;
}

const CHOICES: readonly EditorModelChoice[] = ['auto', 'chat', 'model'];

/** Editor-only preference: never sends setModel or changes the composer/chat model. Mounted only in visible Accounts. */
export function EditorModelSection({ setup, replies, sessionModel, connected, inert, defaultCollapsed = false }: EditorModelSectionProps) {
  const t = useT();
  const id = useId();
  const rootRef = useRef<HTMLElement>(null);
  const choicesRef = useRef<HTMLDivElement>(null);
  const specificRef = useRef<HTMLButtonElement>(null);
  const { post, feedback } = useSetupCommands(replies);
  const [open, setOpen] = useState(false);
  const [firstRunExpanded, setFirstRunExpanded] = useState(false);
  const [query, setQuery] = useState('');
  const editor = setup.editorModel ?? { choice: 'auto' };
  const choice = editor.choice;
  const selected = editor.model;
  const selectedKey = selected ? modelKey(selected) : '';
  const models = setup.models;
  const label = useCallback((provider: string) => localizedProviderName(providerName(setup.providers, provider), t), [setup.providers, t]);
  const name = (model: SetupModel): string => model.name || models?.find((item) => modelKey(item) === modelKey(model))?.name || model.id;
  const chatModel = sessionModel ?? setup.currentModel;
  const chatKey = choice === 'chat' && chatModel ? modelKey(chatModel) : '';
  const setAnswer = feedback('setEditorModel');
  const resolveAnswer = feedback('resolveEditorModel');
  const modelsAnswer = feedback('models');
  const disabled = inert || setAnswer.pending;

  // One resolution per visible choice/identity or running bridge. Effective/resolving/error replies do not retrigger it.
  const resolved = useRef<string | undefined>(undefined);
  useEffect(() => {
    if (!connected) { resolved.current = undefined; return; }
    const key = JSON.stringify([choice, selectedKey, chatKey]);
    if (resolved.current === key) return;
    resolved.current = key;
    post('resolveEditorModel', (requestId) => ({ requestId, action: 'resolveEditorModel' }));
  }, [connected, choice, selectedKey, chatKey, post]);

  // The list is shared with the composer. Request it once on show/start if unknown, never retry unsolicited error states.
  const requestedModels = useRef(false);
  useEffect(() => {
    if (!connected) { requestedModels.current = false; return; }
    if (requestedModels.current) return;
    requestedModels.current = true;
    if (!models && !setup.modelsError) post('models', (requestId) => ({ requestId, action: 'listModels' }));
  }, [connected, models, setup.modelsError, post]);

  useEffect(() => { if (!connected || disabled) setOpen(false); }, [connected, disabled]);
  useEffect(() => {
    if (!open) return;
    const outside = (event: PointerEvent): void => {
      if (event.target instanceof Node && !rootRef.current?.contains(event.target)) setOpen(false);
    };
    document.addEventListener('pointerdown', outside, true);
    return () => document.removeEventListener('pointerdown', outside, true);
  }, [open]);

  const requestModels = (): void => {
    if (connected && !disabled && !modelsAnswer.pending) post('models', (requestId) => ({ requestId, action: 'listModels' }));
  };
  const choose = (next: EditorModelChoice): void => {
    if (disabled) return;
    if (next === 'model') {
      if (!connected) return;
      setQuery('');
      setOpen(true);
      if ((!models || setup.modelsError) && !modelsAnswer.pending) requestModels();
      return;
    }
    setOpen(false);
    if (next !== choice) post('setEditorModel', (requestId) => ({ requestId, action: 'setEditorModel', choice: next }));
  };
  const onChoiceKey = (event: KeyboardEvent<HTMLDivElement>): void => {
    if (event.nativeEvent.isComposing || event.keyCode === 229 || disabled) return;
    const index = CHOICES.indexOf((event.target as HTMLElement).dataset.choice as EditorModelChoice);
    if (index < 0) return;
    let next: number;
    if (event.key === 'ArrowDown' || event.key === 'ArrowRight') next = (index + 1) % CHOICES.length;
    else if (event.key === 'ArrowUp' || event.key === 'ArrowLeft') next = (index + CHOICES.length - 1) % CHOICES.length;
    else if (event.key === 'Home') next = 0;
    else if (event.key === 'End') next = CHOICES.length - 1;
    else return;
    event.preventDefault();
    // Skip the unavailable specific-model picker while the bridge is stopped; auto/chat remain persistable preferences.
    if (!connected && CHOICES[next] === 'model') next = event.key === 'End' || event.key === 'ArrowUp' || event.key === 'ArrowLeft' ? 1 : 0;
    choicesRef.current?.querySelector<HTMLButtonElement>(`[data-choice="${CHOICES[next]}"]`)?.focus();
    choose(CHOICES[next]!);
  };

  const items = useMemo(() => groupModels(filterModels(models ?? [], query, label), label).flatMap((group) => group.models.map((model) => ({
    id: modelKey(model), label: model.name || model.id, title: `${model.provider}/${model.id}`,
    current: choice === 'model' && modelKey(model) === selectedKey, groupKey: group.provider, group: group.label,
  }))), [models, query, label, choice, selectedKey]);
  const modelsError = setup.modelsError ?? modelsAnswer.error;
  const resolving = editor.resolving === true || resolveAnswer.pending;
  const error = editor.error ?? setAnswer.error ?? resolveAnswer.error;
  const labels = { auto: t('setup.editorModel.auto'), chat: t('setup.editorModel.chat'), model: t('setup.editorModel.specific') };
  // The model the chosen option stands for, shown once as its badge: what the host resolved, else what the option names.
  const nominal = choice === 'chat' ? chatModel : choice === 'model' ? selected : undefined;
  const shown = editor.effective ?? nominal;
  const shownProvider = shown ? label(shown.provider) : '';

  return (
    <ViewSection sectionKey="setup-editor-model" expanded={defaultCollapsed ? firstRunExpanded : undefined} onExpandedChange={defaultCollapsed ? setFirstRunExpanded : undefined} title={t('setup.editorModel.title')} headingId={`${id}-title`} className="setup-section editor-model" panelRef={rootRef} aria-labelledby={`${id}-title`} data-testid="setup-editor-model"
      onKeyDown={(event) => {
        if (event.key !== 'Escape' || !open || event.nativeEvent.isComposing || event.keyCode === 229) return;
        event.preventDefault(); event.stopPropagation(); setOpen(false); specificRef.current?.focus();
      }}
      onBlur={(event) => { if (open && event.relatedTarget instanceof Node && !rootRef.current?.contains(event.relatedTarget)) setOpen(false); }}>
      <div className="editor-model-choices" ref={choicesRef} role="radiogroup" aria-labelledby={`${id}-title`} aria-busy={setAnswer.pending || undefined} onKeyDown={onChoiceKey}>
        {CHOICES.map((item) => (
          <button type="button" role="radio" key={item} className="editor-model-choice" data-choice={item} data-testid={`editor-model-${item}`}
            ref={item === 'model' ? specificRef : undefined} disabled={disabled || (item === 'model' && !connected)}
            aria-checked={choice === item} tabIndex={(choice === 'model' && !connected ? 'auto' : choice) === item ? 0 : -1}
            title={plainControls(choice === item && shown ? `${labels[item]} · ${shownProvider} · ${name(shown)}` : labels[item])}
            aria-expanded={item === 'model' ? open : undefined} aria-controls={item === 'model' && open ? `${id}-models` : undefined}
            onClick={() => choose(item)}>
            <span className="editor-model-radio" aria-hidden="true" />
            <span className="editor-model-label">{revealControls(labels[item])}</span>
            {choice === item && (resolving && !shown
              ? <span className="spinner" aria-hidden="true" />
              : shown && <Badge tone="info" testId={editor.effective ? 'editor-model-effective' : undefined} title={plainControls(`${shownProvider} · ${name(shown)}`)}>{revealControls(name(shown))}</Badge>)}
            {item === 'model' && <Icon name="chevronDown" size={11} />}
          </button>
        ))}
      </div>
      {open && <PickerPopover placement="inline" anchorRef={rootRef} id={`${id}-models`} testId="editor-model-popover" label={t('setup.editorModel.title')} items={items}
        search={{ value: query, onChange: setQuery, placeholder: t('picker.searchPlaceholder'), label: t('picker.search') }}
        status={<>
          {modelsAnswer.pending || (!models && !modelsError) ? <div className="picker-status" role="status" data-testid="editor-model-loading"><span className="spinner" aria-hidden="true" />{t('picker.loading')}</div>
            : !modelsError && items.length === 0 ? <div className="picker-status" role="status">{t(models?.length ? 'picker.noMatch' : 'picker.empty')}</div> : null}
          {modelsError && !modelsAnswer.pending && <div className="picker-status error" role="alert" data-testid="editor-model-list-error"><span className="picker-status-text"><strong>{t('picker.loadFailed')}</strong><ClampedText text={modelsError} /></span><button type="button" className="btn subtle" data-testid="editor-model-retry" disabled={disabled} onClick={requestModels}>{t('picker.retry')}</button></div>}
        </>}
        onChoose={(item) => {
          if (disabled || !connected) return;
          const model = parseModelKey(item.id);
          if (model && (choice !== 'model' || item.id !== selectedKey)) post('setEditorModel', (requestId) => ({ requestId, action: 'setEditorModel', choice: 'model', provider: model.provider, modelId: model.id }));
          setOpen(false); specificRef.current?.focus();
        }} />}
      {resolving && <span className="sr-only" role="status" data-testid="editor-model-resolving">{t('setup.editorModel.resolving')}</span>}
      {error && <div data-testid="editor-model-error"><Note tone="error" text={t('setup.editorModel.failed')} detail={error} /></div>}
    </ViewSection>
  );
}
