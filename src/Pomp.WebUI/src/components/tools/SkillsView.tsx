import { useEffect, useRef, useState, type CSSProperties } from 'react';
import { skillDescription } from '../../bundledDescriptions';
import { toolsTranslate, type ToolsTranslate } from '../../i18n/toolsStrings';
import type { SkillInfo } from '../../toolsProtocol';
import type { ToolsController } from '../../toolsState';
import { Icon } from '../Icon';
import { SectionRefresh, ViewSection } from '../ViewSection';
import { Badge, DataTable, IconButton, Pager, SearchBox, Segmented, Switch, Toolbar, usePaged, type BadgeTone } from '../ui';
import { categoryLabel, differsFromDefault, skillOn, skillRows, writableSkill, type SkillFilter } from './extensionRows';
import { useSkillManager, type SkillManager } from './SkillManage';

/** The kind badge of a skill: where its file lives (POMP's own skills have none). */
export function skillKind(t: ToolsTranslate, skill: SkillInfo): { label: string; tone: BadgeTone } | null {
  return skill.source === 'project' ? { label: t('skills.kind.project'), tone: 'accent' } : skill.source === 'user' ? { label: t('skills.kind.user'), tone: 'neutral' }
    : skill.source === 'omp' ? { label: t('skills.kind.omp'), tone: 'neutral' } : null;
}
/** The row actions a skill has (send to chat, edit, open in VS, delete, back to default), in display order. */
function actionsOf(skill: SkillInfo) {
  return { use: !skill.unreadable, edit: !skill.unreadable && writableSkill(skill), open: true, remove: writableSkill(skill), reset: !skill.unreadable && differsFromDefault(skill) };
}

/**
 * Tools › Skills: every skill with its switch, kind and the actions its source allows. Search and the on/off filter narrow the list; it pages by 20.
 * The props are ToolsTab's: the controller, the UI language, whether the host is usable, whether a reply runs, and how a skill goes into the chat input.
 */
export function SkillsView({ controller, locale, disabled, busy, onUseSkill }: { controller: ToolsController; locale: string; disabled: boolean; busy: boolean; onUseSkill: (name: string) => void }) {
  const t = toolsTranslate(locale);
  const [filter, setFilter] = useState<SkillFilter>('all'); const [query, setQuery] = useState('');
  // A switch disables itself while its request runs: the control takes the focus back once the request is done.
  const root = useRef<HTMLDivElement>(null); const refocus = useRef('');
  const settled = !controller.pending('skills');
  useEffect(() => {
    const id = refocus.current; if (!id || !settled) return;
    refocus.current = '';
    if (document.activeElement && document.activeElement !== document.body) return;
    root.current?.querySelector<HTMLElement>(`[data-testid="${id}"]`)?.focus();
  });
  // A new skill has to show: a search or a filter that would hide it is dropped.
  const manager = useSkillManager({ controller, locale, disabled, busy, onUseSkill, onCreated: () => { setQuery(''); setFilter('all'); }, onControl: id => { refocus.current = id; } });
  const all = controller.skills.skills; const onCount = all.filter(skillOn).length;
  const rows = skillRows(all, filter, query, skill => skillDescription(skill, t), category => categoryLabel(t, category));
  const paged = usePaged(rows, `${filter}\n${query}`);
  const refreshing = controller.pending('skills');
  return <div className="tools-scroll-body" ref={root} data-testid="skills-view">
    <ViewSection sectionKey="tools-skills" title={t('tools.skills')} status={all.length} bodyClassName="ui-flush"
      actions={<>
        <button type="button" className="btn small subtle" data-testid="skills-import" disabled={manager.locked} onClick={manager.startImport}><Icon name="download" size={12} />{t('tools.import')}</button>
        <button type="button" className="btn small" data-testid="skills-create" data-tools-dialog-fallback="skill" disabled={manager.locked} onClick={manager.startCreate}><Icon name="plus" size={12} />{t('skills.create')}</button>
        <SectionRefresh onClick={() => { void controller.command('skills', { action: 'refresh' }); }} disabled={disabled || refreshing} testId="skills-refresh" />
      </>}>
      {manager.dialogs}
      <Toolbar>
        <SearchBox value={query} onChange={setQuery} testId="skills-search" />
        <Segmented<SkillFilter> label={t('skills.filter')} testId="skills-filter" value={filter} onChange={setFilter} options={[
          { id: 'all', label: t('skills.filterAll'), badge: all.length, testId: 'skills-filter-all' },
          { id: 'on', label: t('skills.filterOn'), badge: onCount, testId: 'skills-filter-on' },
          { id: 'off', label: t('skills.filterOff'), badge: all.length - onCount, testId: 'skills-filter-off' }]} />
      </Toolbar>
      {manager.created && <p className="ui-note" role="status" data-testid="skills-created">{t('skills.created', { name: manager.created })}</p>}
      {manager.together.length > 0 && <p className="ui-note" role="status" data-testid="skills-together">{t('ext.together', { names: manager.together.join(', ') })}</p>}
      {manager.readError && <p className="ui-note" role="alert" data-testid="skills-read-error">{manager.readError}</p>}
      {controller.skills.diagnostics.map((note, index) => <p className="ui-note ui-muted" role="status" key={index}>{note}</p>)}
      {controller.errors('skills').map((error, index) => <p className="ui-note" role="alert" key={index}>{error}</p>)}
      <div className="tools-narrow" style={{ '--ui-cols-narrow': 'auto minmax(0, 1fr) auto' } as CSSProperties}>
        <DataTable columns="auto minmax(0, 1fr) auto auto" label={t('tools.skills')} testId="skills-table" empty={!rows.length} emptyRows={3}
          head={['', t('tcol.name'), <span className="narrow-hide">{t('tcol.kind')}</span>, '']}>
          {paged.slice.map(skill => <SkillRow key={`${skill.source}:${skill.name}`} skill={skill} manager={manager} t={t} />)}
        </DataTable>
      </div>
      <Pager paged={paged} testId="skills-pager" />
    </ViewSection>
  </div>;
}

/** One skill: switch, name with its description (and any host note), kind, actions. The row takes focus after it was created. */
function SkillRow({ skill, manager, t }: { skill: SkillInfo; manager: SkillManager; t: ToolsTranslate }) {
  const { locked } = manager;
  const on = skillOn(skill); const kind = skillKind(t, skill); const can = actionsOf(skill); const describe = skillDescription(skill, t);
  const note = skill.note || (skill.unreadable ? t('tools.unreadable') : on && !skill.invocable ? t('skills.notInvocable') : '');
  return <div className="ui-tr" role="row" tabIndex={-1} ref={manager.rowRef(skill)} data-testid={`skill-row-${skill.name}`} data-kind="skill" data-on={on}>
    <span role="cell" className="ui-cell">{!skill.unreadable && <Switch checked={on} showLabel={false} label={t('skills.toggle', { name: skill.name })} disabled={locked} testId={`skill-toggle-${skill.name}`} onChange={() => manager.toggle(skill)} />}</span>
    <span role="cell" className="ui-cell">
      <span className="tools-name-line"><strong>{skill.name}</strong>{kind && <span className="inline-kind"><Badge tone={kind.tone}>{kind.label}</Badge></span>}
        {skill.catalogUpdate && <Badge tone="warn" testId={`skill-update-${skill.name}`}>{t('ext.update')}</Badge>}</span>
      {describe && <span className="ui-sub" title={describe}>{describe}</span>}
      {note && <span className="ui-sub ui-warn" title={note}>{note}</span>}
    </span>
    <span role="cell" className="ui-cell narrow-hide">{kind && <Badge tone={kind.tone}>{kind.label}</Badge>}</span>
    <span role="cell" className="ui-cell ui-actions">
      {can.use && <IconButton icon="send" label={t('skills.useInChat')} disabled={locked || !skill.invocable || !on} testId={`skill-manage-use-${skill.name}`} onClick={() => manager.use(skill)} />}
      {can.edit && <IconButton icon="edit" label={t('skills.edit')} disabled={locked} testId={`skills-edit-${skill.source}-${skill.name}`} onClick={() => manager.edit(skill)} />}
      {can.reset && <IconButton icon="undo" label={`${t('ext.reset')} · ${t('ext.default', { state: t(skill.defaultEnabled ? 'ext.enabled' : 'ext.disabled') })}`} disabled={locked} testId={`skill-reset-${skill.name}`} onClick={() => manager.reset(skill)} />}
      <IconButton icon="external" label={t('skills.open')} disabled={locked || !skill.file} testId={`skill-open-${skill.name}`} onClick={() => manager.open(skill)} />
      {can.remove && <IconButton icon="trash" tone="danger" label={t('tools.delete')} disabled={locked || !skill.hash} testId={`skill-delete-${skill.name}`} onClick={() => manager.remove(skill)} />}
    </span>
  </div>;
}
