import { useEffect, useId, useMemo, useRef, useState } from 'react';
import { useT } from '../i18n';
import { filterModels, groupModels, modelKey, providerName, localizedProviderName, type SetupReply, type SetupState } from '../setup';
import { useSetupCommands } from '../setupCommands';
import { Icon } from './Icon';
import { PickerPopover } from './ModelPickers';
import { revealControls } from './RevealControls';

/** The same searchable, provider-grouped catalog as EditorModelSection, without changing the chat model. */
export function RoleModelPicker({ setup, replies, value, onChange, disabled }: {
  setup: SetupState | null; replies: SetupReply[]; value: string; onChange: (value: string) => void; disabled: boolean;
}) {
  const t = useT();
  const id = useId();
  const root = useRef<HTMLDivElement>(null);
  const button = useRef<HTMLButtonElement>(null);
  const [open, setOpen] = useState(false);
  const [query, setQuery] = useState('');
  const { post, feedback } = useSetupCommands(replies);
  const answer = feedback('models');
  const requested = useRef(false);
  const requestModels = () => post('models', requestId => ({ requestId, action: 'listModels' }));
  useEffect(() => {
    if (!disabled && !requested.current) {
      requested.current = true;
      if (!setup?.models && !setup?.modelsError) requestModels();
    }
  }, [disabled, setup?.models, setup?.modelsError]);
  useEffect(() => {
    if (!open) return;
    const outside = (event: PointerEvent) => { if (event.target instanceof Node && !root.current?.contains(event.target)) setOpen(false); };
    document.addEventListener('pointerdown', outside, true);
    return () => document.removeEventListener('pointerdown', outside, true);
  }, [open]);
  const items = useMemo(() => {
    const label = (provider: string) => localizedProviderName(providerName(setup?.providers ?? [], provider), t);
    return groupModels(filterModels(setup?.models ?? [], query, label), label).flatMap(group => group.models.map(model => ({
      id: modelKey(model), label: model.name || model.id, title: `${model.provider}/${model.id}`,
      current: `${model.provider}/${model.id}` === value, groupKey: group.provider, group: group.label,
    })));
  }, [setup?.models, setup?.providers, query, value]);
  const error = setup?.modelsError ?? answer.error;
  const model = setup?.models?.find(model => `${model.provider}/${model.id}` === value);
  return <div ref={root} className="role-model-picker" onKeyDown={event => {
    if (!event.defaultPrevented && event.key === 'Escape' && open && !event.nativeEvent.isComposing && event.keyCode !== 229) {
      event.preventDefault(); event.stopPropagation(); setOpen(false); button.current?.focus();
    }
  }}>
    <button ref={button} type="button" className="btn subtle" data-testid="role-model-picker" disabled={disabled} aria-haspopup="listbox" aria-expanded={open}
      onClick={() => { setQuery(''); setOpen(!open); }}>
      <span>{revealControls(model?.name || value || t('agents.modelSpecific'))}</span><Icon name="chevronDown" size={11} />
    </button>
    {open && <PickerPopover placement="inline" anchorRef={root} id={id} testId="role-model-popover" label={t('agents.modelLabel')} items={items}
      search={{ value: query, onChange: setQuery, placeholder: t('picker.searchPlaceholder'), label: t('picker.search') }}
      status={answer.pending || (!setup?.models && !error) ? <div className="picker-status" role="status">{t('picker.loading')}</div>
        : error ? <div className="picker-status error" role="alert">{revealControls(error)}<button type="button" className="btn subtle" onClick={requestModels}>{t('picker.retry')}</button></div>
          : !items.length ? <div className="picker-status" role="status">{t(setup?.models?.length ? 'picker.noMatch' : 'picker.empty')}</div> : null}
      onEscape={() => { setOpen(false); button.current?.focus(); }}
      onChoose={item => { const selected = setup?.models?.find(model => modelKey(model) === item.id); if (selected) onChange(`${selected.provider}/${selected.id}`); setOpen(false); button.current?.focus(); }} />}
  </div>;
}
