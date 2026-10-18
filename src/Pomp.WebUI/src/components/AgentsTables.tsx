import { useContext, useMemo, useState, type CSSProperties } from 'react';
import { useT, type MessageKey } from '../i18n';
import { roleDescription, roleJob, roleTitle } from '../bundledDescriptions';
import { send, type AgentsStatePayload, type MainRole, type Role, type RoleAvatar, type Team } from '../protocol';
import { useAvatarImage } from '../avatarImages';
import type { SetupState } from '../setup';
import { MAIN_ROLE, MainRoleContext, RolesContext, TeamsContext, effectiveRoles, isRunnableTeam } from '../teamsCache';
import { Icon } from './Icon';
import { plainControls, revealControls } from './RevealControls';
import { ViewSection } from './ViewSection';
import { Actions, Badge, Cell, DataTable, IconButton, Pager, Row, SearchBox, Segmented, Toolbar, byName, matches, usePaged } from './ui';

/** Identity colors of the role/team avatars: mid-tone hues that carry white initials on dark and light themes alike. */
export const AVATAR_COLORS = ['#6d5bd0', '#0e7490', '#15803d', '#b45309', '#be123c', '#7e22ce', '#1d4ed8', '#4d7c0f', '#a21caf', '#0f766e'];
export function avatarColor(key: string): string {
  let hash = 0;
  for (const char of key) hash = (hash * 31 + char.codePointAt(0)!) >>> 0;
  return AVATAR_COLORS[hash % AVATAR_COLORS.length]!;
}
/**
 * A role's picture: the shape and color the user chose (or a circle of the role's own color) with its first letter, or the image the
 * user chose for it. `image` shows a given image instead (the picture editor's preview).
 */
export function Avatar({ name, label, size = 24, missing = false, look, scope, image }: {
  name: string; label: string; size?: number; missing?: boolean; look?: RoleAvatar | null; scope?: string; image?: string | null;
}) {
  const stored = useAvatarImage(scope, name, image === undefined ? look?.imageRev : undefined);
  const shown = image === undefined ? stored : image ?? undefined;
  const initial = [...(label.trim() || name)][0]?.toUpperCase() ?? '?';
  const style: CSSProperties = missing ? { width: size, height: size } : { width: size, height: size, background: look?.color ?? avatarColor(name), fontSize: Math.max(10, Math.round(size * 0.46)) };
  return <span className={`ui-avatar agents-avatar shape-${look?.shape ?? 'circle'}${missing ? ' missing' : ''}${shown ? ' has-image' : ''}`} style={style} aria-hidden="true">
    {missing ? <Icon name="warning" size={11} /> : shown ? <img src={shown} alt="" draggable={false} /> : initial}
  </span>;
}

/**
 * Where a message goes, pictured as the Agents tab pictures it: POMP's or the role's own picture, a team as its first members'
 * pictures overlapped (a team has no picture of its own). Reads the roles, teams and POMP's role from the page's contexts.
 */
export function DestinationAvatar({ kind, id, size = 16 }: { kind: 'main' | 'agent' | 'team'; id?: string; size?: number }) {
  const t = useT();
  const main = useContext(MainRoleContext);
  const roles = effectiveRoles(useContext(RolesContext));
  const teams = useContext(TeamsContext);
  if (kind === 'main' || !id) return <Avatar name={MAIN_ROLE.name} label={MAIN_ROLE.title} size={size} look={main?.avatar} scope={MAIN_ROLE.scope} />;
  const roleAvatar = (name: string) => {
    const role = roles.find(item => item.name === name);
    return <Avatar key={name} name={name} label={roleTitle(role, t, name)} size={size} look={role?.avatar} scope={role?.scope} />;
  };
  if (kind === 'agent') return roleAvatar(id);
  const members = teams.find(team => team.id.toLowerCase() === id.toLowerCase())?.members ?? [];
  return <span className="agents-avatar-stack destination-avatar-stack">{members.slice(0, 3).map(roleAvatar)}</span>;
}

type RoleScopeFilter = 'all' | 'project' | 'user';
const isBuiltin = (role: Role) => role.scope === 'bundled' || role.scope === 'extension';
const isFileRole = (role: Role) => role.scope === 'project' || role.scope === 'user';
/** The page form rewrites only plain role files (a custom tool list is kept as it is); anything else is edited in VS. */
export const formEditable = (role: Role) => role.editable && !role.error && !role.note && isFileRole(role);

/** The model a role runs on, as a short label (the raw value stays in the tooltip). */
function modelLabel(role: Role, setup: SetupState | null, t: ReturnType<typeof useT>): string {
  const raw = role.model?.trim();
  if (!raw) return t('agents.modelChat');
  const entries = raw.split(',').map(entry => entry.trim()).filter(Boolean);
  const first = (entries[0] ?? raw).replace(/:[a-z]+$/iu, '');
  let label: string;
  // The engine's model roles (smol, slow, plan, task, commit) by what they are for.
  const roleNames: Record<string, MessageKey> = { '@default': 'agents.modelChat', '@smol': 'agents.modelFastSmol', '@slow': 'agents.modelThinking', '@plan': 'agents.modelArchitect', '@task': 'agents.modelSubtask', '@commit': 'agents.modelCommit' };
  const engineRole = roleNames[first.toLowerCase()];
  if (engineRole) label = t(engineRole);
  else if (first.startsWith('@')) label = first.slice(1);
  else {
    const known = setup?.models?.find(model => `${model.provider}/${model.id}`.toLowerCase() === first.toLowerCase());
    label = known?.name || first.slice(first.indexOf('/') + 1) || first;
  }
  return entries.length > 1 ? `${label} +${entries.length - 1}` : label;
}

export function RolesCard({ roles, hidden = 0, effective, main, setup, locale, connected, inert, busy, dialogOpen, openPending, modelUnavailable, onNew, onMessage, onCopy, onEdit, onEditBuiltin, onEditAvatar, onEditMain, onDelete, onOpen }: {
  roles: Role[]; hidden?: number; effective: Role[]; main?: MainRole; setup: SetupState | null; locale: string; connected: boolean; inert: boolean; busy: boolean; dialogOpen: boolean; openPending: boolean;
  modelUnavailable: (model?: string) => boolean;
  onNew: () => void; onMessage: (role: Role) => void; onCopy: (role: Role) => void; onEdit: (role: Role) => void; onEditBuiltin: (role: Role) => void;
  onEditAvatar: (role: Role) => void; onEditMain: () => void; onDelete: (role: Role) => void; onOpen: (role: Role) => void;
}) {
  const t = useT(); const ko = locale.startsWith('ko');
  const [query, setQuery] = useState(''); const [scope, setScope] = useState<RoleScopeFilter>('all');
  // Project roles first, then the user's, then the engine's own; by name inside.
  const rank = (role: Role) => isBuiltin(role) ? 2 : role.scope === 'user' ? 1 : 0;
  const ordered = useMemo(() => [...roles].sort((a, b) => rank(a) - rank(b) || byName(roleTitle(a, t), roleTitle(b, t)) || byName(a.name, b.name)), [roles, t]);
  const inScope = (role: Role, filter: RoleScopeFilter) => filter === 'all' || (isFileRole(role) && role.scope === filter);
  // POMP's own role, the chat agent, heads the list; it is not a file, so only the whole list shows it, and it cannot be deleted.
  const count = (filter: RoleScopeFilter) => ordered.filter(role => inScope(role, filter)).length + (filter === 'all' ? 1 : 0);
  const mainShown = scope === 'all' && matches(query, MAIN_ROLE.title, MAIN_ROLE.name);
  const visible: (Role | typeof MAIN_ROLE)[] = [...(mainShown ? [MAIN_ROLE] : []), ...ordered.filter(role => inScope(role, scope)
    && matches(query, roleTitle(role, t), role.name, roleJob(role, t), roleDescription(role, t), role.model))];
  const paged = usePaged(visible, `${scope}:${query}`);
  const blocked = !connected || inert;
  // The picture editor takes POMP as a role of the user's, like a built-in role.
  const mainAsRole: Role = { name: MAIN_ROLE.name, title: MAIN_ROLE.title, job: '', description: '', instructions: '', tools: 'all', scope: MAIN_ROLE.scope, editable: false, avatar: main?.avatar };
  return <ViewSection sectionKey="agents-roles" className="agents-card" headingLevel="h2" title={t('agents.roles')} status={<span data-testid="roles-count">{roles.length + 1}</span>} bodyClassName="ui-flush"
    actions={<button type="button" className="btn small" data-testid="new-role-btn" disabled={blocked || dialogOpen} onClick={onNew}><Icon name="plus" size={12} />{t('agents.newRole')}</button>}>
    <Toolbar>
      <SearchBox value={query} onChange={setQuery} testId="roles-search" />
      <Segmented<RoleScopeFilter> label={t('agents.scopeLabel')} testId="roles-scope" value={scope} onChange={setScope} options={[
        { id: 'all', label: ko ? '전체' : 'All', badge: count('all'), testId: 'roles-scope-all' },
        { id: 'project', label: t('ui.scope.project'), badge: count('project'), testId: 'roles-scope-project' },
        { id: 'user', label: t('ui.scope.user'), badge: count('user'), testId: 'roles-scope-user' },
      ]} />
    </Toolbar>
    {main?.restartRequired && <div className="ui-card-foot tools-banner-row" role="status" data-testid="main-role-restart-banner">
      <Icon name="warning" size={13} className="ui-warn" /><span className="ui-grow">{t('agents.restartRequired')}</span>
      <button type="button" className="btn small" data-testid="main-role-restart" disabled={blocked || busy} onClick={() => send('bridge/restart', {})}><Icon name="restart" size={12} />{t('setup.restart')}</button>
    </div>}
    <DataTable columns="auto minmax(0, 1fr) auto" label={t('agents.roles')} testId="roles-table" empty={!visible.length} emptyRows={3}
      head={['', t('ui.col.name'), '']}>
      {paged.slice.map(entry => {
        if (entry === MAIN_ROLE) return <Row key="main" testId="role-card-main" className="agents-role-row">
          <Cell><button type="button" className="agents-avatar-button" data-testid="role-avatar-main" aria-label={t('avatar.editFor', { name: MAIN_ROLE.title })}
            title={t('avatar.editFor', { name: MAIN_ROLE.title })} disabled={blocked || dialogOpen} onClick={() => onEditAvatar(mainAsRole)}>
            <Avatar name={MAIN_ROLE.name} label={MAIN_ROLE.title} look={main?.avatar} scope={MAIN_ROLE.scope} />
          </button></Cell>
          <Cell className="agents-name-cell">
            <span className="agents-name-line"><strong>{MAIN_ROLE.title}</strong></span>
            <span className="agents-badges">
              <Badge tone="neutral" icon="cpu" testId="role-model-main">{t('agents.modelChat')}</Badge>
              {main?.scope && <Badge tone={main.scope === 'project' ? 'accent' : 'neutral'} testId="role-scope-main">{main.scope === 'user' ? t('ui.scope.user') : t('ui.scope.project')}</Badge>}
              <Badge>{t('agents.tools.all')}</Badge>
            </span>
          </Cell>
          <Actions>
            <IconButton icon="edit" label={t('agents.edit')} testId="role-edit-main" disabled={blocked || dialogOpen} onClick={onEditMain} />
          </Actions>
        </Row>;
        const role = entry as Role;
        const title = roleTitle(role, t); const job = roleJob(role, t); const description = roleDescription(role, t);
        const usable = !role.error && effective.find(item => item.name === role.name) === role;
        const shadowed = !role.error && (role.shadowed || !effective.includes(role));
        const editable = formEditable(role);
        const sub = role.error ? role.error : job || description;
        const scopeTone = role.scope === 'project' ? 'accent' : 'neutral';
        return <Row key={`${role.scope}:${role.name}`} testId={`role-card-${role.name}`} className={`agents-role-row${role.error ? ' has-error' : ''}${shadowed ? ' is-shadowed' : ''}`}>
          <Cell><button type="button" className="agents-avatar-button" data-testid={`role-avatar-${role.scope}-${role.name}`} aria-label={t('avatar.editFor', { name: title })}
            title={plainControls(t('avatar.editFor', { name: title }))} disabled={blocked || dialogOpen} onClick={() => onEditAvatar(role)}>
            <Avatar name={role.name} label={title} look={role.avatar} scope={role.scope} />
          </button></Cell>
          <Cell className="agents-name-cell">
            <span className="agents-name-line"><strong>{revealControls(title)}</strong><span className="ui-mono ui-muted agents-id">{revealControls(role.name)}</span></span>
            {sub && <span className={`ui-sub${role.error ? ' ui-err' : ''}`} title={plainControls(role.error ? role.error : job && description && job !== description ? description : sub)}>{revealControls(sub)}</span>}
            <span className="agents-badges">
              {!role.error && <Badge tone={role.model ? 'info' : 'neutral'} icon="cpu" title={[role.model, role.thinkingLevel].filter(Boolean).join(' · ') || undefined} testId={`role-model-${role.name}`}>{revealControls(modelLabel(role, setup, t))}</Badge>}
              {isFileRole(role) && <Badge tone={scopeTone}>{role.scope === 'user' ? t('ui.scope.user') : t('ui.scope.project')}</Badge>}
              {!role.error && <Badge>{t(`agents.tools.${role.tools}`)}</Badge>}
              {role.error && <Badge tone="error" icon="error">{ko ? '파일 오류' : 'File error'}</Badge>}
              {!role.error && role.note && <Badge title={plainControls(role.note)} testId={`role-note-${role.name}`}>{ko ? 'VS에서 편집' : 'Edit in VS'}</Badge>}
              {shadowed && <Badge title={t('agents.shadowed')} testId={`role-shadowed-${role.scope}-${role.name}`}>{ko ? '가려짐' : 'Hidden'}</Badge>}
              {modelUnavailable(role.model) && <Badge tone="warn" icon="warning" title={t('agents.modelUnavailableWarning')} testId={`role-model-warning-${role.name}`}>{ko ? '모델 없음' : 'No model'}</Badge>}
            </span>
          </Cell>
          <Actions>
            {usable && <IconButton icon="message" label={t('agents.message')} testId={`role-message-${role.name}`} disabled={blocked} onClick={() => onMessage(role)} />}
            {isBuiltin(role) && !role.error && <IconButton icon="copy" label={t('agents.copyRole')} testId={`role-copy-${role.name}`} disabled={blocked || dialogOpen} onClick={() => onCopy(role)} />}
            {editable && <IconButton icon="edit" label={t('agents.edit')} testId={`role-edit-${role.name}`} disabled={blocked || dialogOpen} onClick={() => onEdit(role)} />}
            {role.scope === 'bundled' && usable && <IconButton icon="edit" label={t('agents.edit')} testId={`role-edit-${role.name}`} disabled={blocked || dialogOpen || openPending} onClick={() => onEditBuiltin(role)} />}
            {isFileRole(role) && !editable && <IconButton icon="external" label={t('agents.openInVs')} testId={`role-open-${role.scope}-${role.name}`} disabled={blocked || openPending} onClick={() => onOpen(role)} />}
            {isFileRole(role) && <IconButton icon="trash" tone="danger" label={t('agents.delete')} testId={`role-delete-${role.name}`} disabled={blocked || !role.hash} onClick={() => onDelete(role)} />}
          </Actions>
        </Row>;
      })}
    </DataTable>
    <Pager paged={paged} testId="roles-pager" />
    {hidden > 0 && <p className="ui-note ui-muted" role="status" data-testid="roles-hidden">{t('agents.rolesHidden', { count: hidden })}</p>}
  </ViewSection>;
}

export function TeamsCard({ agents, teams, effective, locale, connected, inert, editorOpen, exportOpen, importOpen, onNew, onImport, onMessage, onEdit, onExport, onDelete }: {
  agents: AgentsStatePayload | null; teams: Team[]; effective: Role[]; locale: string; connected: boolean; inert: boolean; editorOpen: boolean; exportOpen: boolean; importOpen: boolean;
  onNew: () => void; onImport: () => void; onMessage: (team: Team) => void; onEdit: (team: Team) => void; onExport: (team: Team) => void; onDelete: (team: Team) => void;
}) {
  const t = useT(); const ko = locale.startsWith('ko');
  const [query, setQuery] = useState('');
  const all = agents?.teams ?? [];
  const roleOf = (name: string) => effective.find(item => item.name === name) ?? agents?.roles.find(item => item.name === name);
  const titleOf = (name: string) => { const role = roleOf(name); return role ? roleTitle(role, t) : name; };
  const visible = all.filter(team => matches(query, team.title, team.id, team.goal, ...team.members.map(titleOf)))
    .sort((a, b) => byName(a.title || a.id, b.title || b.id) || byName(a.id, b.id) || a.scope.localeCompare(b.scope));
  const paged = usePaged(visible, query);
  const blocked = !connected || inert;
  return <ViewSection sectionKey="agents-teams" className="agents-card" headingLevel="h2" title={t('agents.teams')} status={<span data-testid="teams-count">{all.length}</span>} bodyClassName="ui-flush"
    actions={<>
      <button type="button" className="btn small subtle" data-testid="import-team-btn" disabled={blocked || importOpen} onClick={onImport}><Icon name="download" size={12} />{t('agents.importTeam')}</button>
      <button type="button" className="btn small" data-testid="new-team-btn" disabled={blocked || editorOpen} onClick={onNew}><Icon name="plus" size={12} />{t('agents.newTeam')}</button>
    </>}>
    <Toolbar><SearchBox value={query} onChange={setQuery} testId="teams-search" /></Toolbar>
    <DataTable columns="minmax(0, 1fr) auto auto" label={t('agents.teams')} testId="teams-table" empty={!visible.length} emptyRows={3}
      head={[t('ui.col.name'), t('agents.membersLabel'), '']}>
      {paged.slice.map(team => {
        const shadowed = team.shadowed || !teams.includes(team);
        const runnable = isRunnableTeam(team, agents);
        const flow = team.members.map(titleOf).join(team.mode === 'pipeline' ? ' → ' : ' · ');
        return <Row key={`${team.scope}:${team.id}`} testId={`team-card-${team.id}`} className={`agents-team-row${team.error ? ' has-error' : ''}${shadowed ? ' is-shadowed' : ''}`}>
          <Cell className="agents-name-cell">
            <span className="agents-name-line"><strong>{revealControls(team.title)}</strong><span className="ui-mono ui-muted agents-id">{revealControls(team.id)}</span></span>
            {team.error ? <span className="ui-sub ui-err" role="alert">{revealControls(team.error)}</span>
              : (team.goal || flow) && <span className="ui-sub" title={plainControls(team.goal ? `${team.goal}\n${flow}` : flow)}>{revealControls(team.goal || flow)}</span>}
            <span className="agents-badges">
              <Badge tone="info" testId={`team-mode-${team.id}`}>{t(`agents.mode.${team.mode}`)}</Badge>
              <Badge tone={team.scope === 'project' ? 'accent' : 'neutral'}>{team.scope === 'user' ? t('ui.scope.user') : t('ui.scope.project')}</Badge>
              {team.isolation && <Badge testId={`team-isolated-${team.id}`}>{t('agents.isolatedBadge')}</Badge>}
              {shadowed && <Badge title={t('agents.shadowedProject')} testId={`team-shadowed-${team.scope}-${team.id}`}>{ko ? '가려짐' : 'Hidden'}</Badge>}
              {team.missing?.length > 0 && <Badge tone="warn" icon="warning" title={t('agents.missingMembersWarning', { count: team.missing.length })} testId={`team-missing-${team.id}`}>{ko ? `구성원 ${team.missing.length}명 없음` : `${team.missing.length} missing`}</Badge>}
              {team.error && <Badge tone="error" icon="error">{ko ? '파일 오류' : 'File error'}</Badge>}
            </span>
          </Cell>
          <Cell><span className="agents-avatar-stack" title={plainControls(flow)}>
            {team.members.map(name => { const role = roleOf(name); return <Avatar key={name} name={name} label={titleOf(name)} size={20} missing={team.missing?.includes(name)} look={role?.avatar} scope={role?.scope} />; })}
          </span></Cell>
          <Actions>
            <IconButton icon="message" label={t('agents.message')} testId={`team-message-${team.id}`} disabled={blocked || !runnable} onClick={() => { if (runnable) onMessage(team); }} />
            <IconButton icon="edit" label={t('agents.edit')} testId={`team-edit-${team.id}`} disabled={blocked || editorOpen} onClick={() => onEdit(team)} />
            <IconButton icon="upload" label={t('agents.exportTeam')} testId={`team-export-${team.id}`} disabled={blocked || exportOpen} onClick={() => onExport(team)} />
            <IconButton icon="trash" tone="danger" label={t('agents.delete')} testId={`team-delete-${team.id}`} disabled={blocked} onClick={() => onDelete(team)} />
          </Actions>
        </Row>;
      })}
    </DataTable>
    <Pager paged={paged} testId="teams-pager" />
    {(agents?.teamsHidden ?? 0) > 0 && <p className="ui-note ui-muted" role="status" data-testid="teams-hidden">{t('agents.teamsHidden', { count: agents!.teamsHidden! })}</p>}
  </ViewSection>;
}
