import { roleDescription, roleJob, roleTitle } from '../bundledDescriptions';
import { useEffect, useMemo, useRef, useState } from 'react';
import { newId } from '../format';
import { useT, type MessageKey } from '../i18n';
import {
  send,
  type AgentsReplyPayload,
  type AgentsStatePayload,
  type KnowledgeItem,
  type Role,
  type SendTarget,
  type RoleScope,
  type RoleTools,
  type Team,
  type TeamImportPreview,
  type TeamMode,
  type TeamScope,
} from '../protocol';
import type { SetupReply, SetupState } from '../setup';
import { REPLY_TIMEOUT_MS, useSetupCommands } from '../setupCommands';
import { effectiveRoles, effectiveTeams } from '../teamsCache';
import { AgentDialog } from './AgentDialog';
import { AgentRulesView } from './AgentRulesView';
import { AdvisorsPanel } from './AdvisorsPanel';
import { Avatar, RolesCard, TeamsCard } from './AgentsTables';
import { AvatarEditor } from './AvatarEditor';
import { MainRoleEditor } from './MainRoleEditor';
import { Actions, Badge, Cell, DataTable, IconButton, PAGE_SIZE, Pager, Row, SearchBox, Switch, Toolbar, byName, matches, usePaged, type BadgeTone } from './ui';
import { advisorCount, useAdvisorsController } from '../advisors';

export const agentsViews = ['roles', 'teams', 'advisors', 'hooks'] as const;
export type AgentsView = typeof agentsViews[number] | 'history' | 'runs';
export function normalizeAgentsView(view: AgentsView): typeof agentsViews[number] { return view === 'history' || view === 'runs' ? 'roles' : view; }
import { RoleModelPicker } from './RoleModelPicker';
import { Icon } from './Icon';
import { revealControls } from './RevealControls';
import { ViewChips } from './tools/ViewChips';
import { useViewChoice } from '../useViewChoice';
import { usableKnowledge } from '../knowledge';
import { useKnowledge } from '../knowledgeState';
import { KnowledgeChecklist } from './KnowledgeChecklist';

const RESERVED_NAMES = new Set(['main', 'sub']);
/** The engine's own roles: a role file of such a name replaces the role, so only editing that role (or its file) may use the name. */
const BUILTIN_NAMES = new Set(['task', 'sonic', 'scout', 'reviewer', 'security-reviewer']);
const SLUG_REGEX = /^[a-z0-9-]{2,40}$/;

function validateSlug(slug: string, existingNames: string[] = [], builtinName = false): string | null {
  if (!slug) return 'slugRequired';
  if (!SLUG_REGEX.test(slug)) return 'slugInvalid';
  if (RESERVED_NAMES.has(slug) || (!builtinName && BUILTIN_NAMES.has(slug))) return 'slugReserved';
  if (existingNames.includes(slug)) return 'slugDuplicate';
  return null;
}

interface TemplateDef {
  key: string;
  name: string;
  titleKo: string;
  titleEn: string;
  jobKo: string;
  jobEn: string;
  descKo: string;
  descEn: string;
  instrKo: string;
  instrEn: string;
  tools: RoleTools;
}

const TEMPLATES: TemplateDef[] = [
  {
    key: 'architect',
    name: 'architect',
    titleKo: '설계자',
    titleEn: 'Architect',
    jobKo: '기능 아키텍처 및 인터페이스 설계',
    jobEn: 'Architecture & interface design',
    descKo: '요구사항을 분석하고 모듈 구조와 계약 인터페이스를 설계한다.',
    descEn: 'Analyzes requirements and designs module structure and contracts.',
    instrKo: '기존 아키텍처와 코딩 컨벤션을 준수하고, 모듈 경계와 공개 인터페이스를 명확히 정의하세요.',
    instrEn: 'Follow existing architecture and conventions, clearly defining module boundaries.',
    tools: 'readOnly',
  },
  {
    key: 'implementer',
    name: 'implementer',
    titleKo: '구현자',
    titleEn: 'Implementer',
    jobKo: '코드 작성 및 리팩토링',
    jobEn: 'Code implementation & refactoring',
    descKo: '설계된 사양에 따라 깔끔하고 안전한 코드를 작성한다.',
    descEn: 'Writes clean, safe code following the designed specifications.',
    instrKo: '단일 책임 원칙을 준수하고, 오류 처리를 철저히 하며, 불필요한 의존성을 만들지 마세요.',
    instrEn: 'Follow single responsibility, handle errors thoroughly, avoid unnecessary dependencies.',
    tools: 'all',
  },
  {
    key: 'reviewer',
    name: 'reviewer-custom',
    titleKo: '리뷰어',
    titleEn: 'Reviewer',
    jobKo: '변경 코드 리뷰',
    jobEn: 'Code review',
    descKo: '변경된 코드의 버그·예외 처리·보안 문제를 근거와 함께 찾는다. 코드는 바꾸지 않는다.',
    descEn: 'Finds bugs, exception handling, and security issues in changed code. Does not edit code.',
    instrKo: '코드를 직접 변경하지 말고, 문제가 있는 위치와 구체적인 개선안을 제시하세요.',
    instrEn: 'Do not edit code directly; report issues with location and clear rationales.',
    tools: 'readOnly',
  },
  {
    key: 'tester',
    name: 'tester',
    titleKo: '테스터',
    titleEn: 'Tester',
    jobKo: '테스트 작성 및 검증',
    jobEn: 'Testing & verification',
    descKo: '단위 테스트 및 회귀 테스트 케이스를 작성하고 실행 결과를 검증한다.',
    descEn: 'Writes unit/regression tests and verifies execution outcomes.',
    instrKo: '경계 조건과 실패 케이스를 빠짐없이 검증하고, 재현 가능한 테스트 코드를 작성하세요.',
    instrEn: 'Thoroughly test edge cases and failures, writing reproducible test code.',
    tools: 'all',
  },
  {
    key: 'doc-writer',
    name: 'doc-writer',
    titleKo: '문서 작성자',
    titleEn: 'Doc writer',
    jobKo: '문서 및 주석 작성',
    jobEn: 'Documentation',
    descKo: '기능 사양서, README, API 문서를 명확하고 일관되게 정리한다.',
    descEn: 'Documents specifications, READMEs, and API docs clearly and consistently.',
    instrKo: '간결하고 명확한 문장을 사용하며, 사용자 및 개발자 관점의 예제를 포함하세요.',
    instrEn: 'Use concise, clear language and include practical examples.',
    tools: 'all',
  },
  {
    key: 'explorer',
    name: 'explorer',
    titleKo: '탐색자',
    titleEn: 'Explorer',
    jobKo: '코드베이스 조사',
    jobEn: 'Codebase exploration',
    descKo: '코드베이스의 구조와 참조 관계를 빠르게 파악한다.',
    descEn: 'Quickly analyzes codebase structure and reference relationships.',
    instrKo: '읽기 전용 도구만 사용하고, 필요한 핵심 파일과 심볼 위치를 신속하게 요약 보고하세요.',
    instrEn: 'Use read-only tools and summarize key files and symbol locations quickly.',
    tools: 'readOnly',
  },
];

interface AgentsPanelProps {
  agents: AgentsStatePayload | null;
  replies: AgentsReplyPayload[];
  setup: SetupState | null;
  setupReplies: SetupReply[];
  locale: string;
  connected: boolean;
  inert: boolean;
  onSelectTarget: (target: SendTarget) => void;
  sessionId?: string;
  view: AgentsView;
  onViewChange: (view: AgentsView) => void;
  /** Workspace of the hook definitions (Automation). */
  workspaceKey?: string;
  /** Puts an earlier prompt into the chat input (not sent). */
  /** A reply is running / a new conversation is starting: the Rules view's hook definitions refuse writes, as the Tools tab does. */
  busy: boolean;
  newSessionPending: boolean;
  /** The hook definitions' notice that no valid project policy exists: opens Tools › Permissions. */
  onShowPermissions: () => void;
}

export function AgentsPanel({ agents, replies, setup, setupReplies, locale, connected, inert, onSelectTarget, sessionId, view: requestedView, onViewChange, workspaceKey, busy, newSessionPending, onShowPermissions }: AgentsPanelProps) {
  const t = useT();
  const [automationCount, setAutomationCount] = useState<number | undefined>(undefined);
  const advisors = useAdvisorsController({ root: workspaceKey, sessionId, connected });
  // POMP's own role heads the roles list.
  const counts = { roles: agents ? agents.roles.length + 1 : 0, teams: agents?.teams.length ?? 0, advisors: advisorCount(advisors.state), hooks: automationCount };
  const [view, chooseView] = useViewChoice({ views: agentsViews, requested: normalizeAgentsView(requestedView), active: !inert, scope: (workspaceKey ?? '') + ':' + (sessionId ?? ''), count: item => counts[item], onChange: onViewChange });
  const { post: postSetup, feedback: setupFeedback } = useSetupCommands(setupReplies);
  const modelsRequested = useRef(false);
  const modelsAnswer = setupFeedback('models');
  useEffect(() => {
    if (!connected || inert || (view !== 'roles' && view !== 'advisors') || modelsRequested.current || setup?.models || setup?.modelsError) return;
    modelsRequested.current = true;
    postSetup('models', requestId => ({ requestId, action: 'listModels' }));
  }, [connected, inert, view, setup?.models, setup?.modelsError, postSetup]);
  const catalogReady = !!setup?.models && !setup.modelsError && !modelsAnswer.pending && !modelsAnswer.error;
  // Warn only when the role names explicit provider/model entries and none of them is in the catalog. omp also accepts a ":level"
  // thinking suffix, comma-separated fallbacks, patterns and any letter case: patterns and aliases are never flagged.
  const configuredModelUnavailable = (model?: string) => {
    if (!catalogReady || !model) return false;
    const entries = model.split(',').map(entry => entry.trim().replace(/:[a-z]+$/iu, '')).filter(Boolean);
    if (!entries.length || entries.some(entry => entry.startsWith('@') || !entry.includes('/') || /[*?]/u.test(entry))) return false;
    const available = new Set(setup!.models!.map(item => `${item.provider}/${item.id}`.toLowerCase()));
    return !entries.some(entry => available.has(entry.toLowerCase()));
  };

  // App refreshes the roster on tab activation. Only sub-view changes/reconnects need an additional request here.
  const previousRoster = useRef({ view, connected });
  useEffect(() => {
    const previous = previousRoster.current;
    previousRoster.current = { view, connected };
    if (connected && !inert && (view === 'roles' || view === 'teams') && (previous.view !== view || !previous.connected))
      send('agents/command', { requestId: newId(), action: 'refresh' });
  }, [view, connected, inert]);

  // Editor states
  const [editingRole, setEditingRole] = useState<{ isNew: boolean; role: Partial<Role> & { scope: RoleScope } } | null>(null);
  const [editingTeam, setEditingTeam] = useState<{ isNew: boolean; team: Partial<Team> & { scope: TeamScope } } | null>(null);

  // Team template export/import
  const [exportingTeam, setExportingTeam] = useState<{ scope: TeamScope; id: string } | null>(null);
  const [importingTeam, setImportingTeam] = useState(false);

  // In-page delete confirmation
  const [deleteTarget, setDeleteTarget] = useState<{ kind: 'role'; scope: RoleScope; name: string; title: string; hash?: string } | { kind: 'team'; scope: TeamScope; id: string; title: string; hash?: string } | null>(null);

  const [deletePending, setDeletePending] = useState(false);
  const [avatarRole, setAvatarRole] = useState<Role | null>(null);
  const [editingMain, setEditingMain] = useState(false);
  const effective = useMemo(() => effectiveRoles(agents?.roles ?? []), [agents?.roles]);
  const teams = useMemo(() => effectiveTeams(agents?.teams ?? []), [agents?.teams]);
  const [openPending, setOpenPending] = useState(false);
  const dialogOpenerRef = useRef<HTMLElement | null>(null);

  // Local errors
  const [actionError, setActionError] = useState<string | null>(null);
  const [conflictOccurred, setConflictOccurred] = useState(false);

  const pendingCommandRef = useRef<{ id: string; action: 'delete' | 'open' } | { id: string; action: 'builtin'; role: Role } | null>(null);

  useEffect(() => {
    const pending = pendingCommandRef.current;
    if (!pending) return;
    const rep = replies.find((r) => r.requestId === pending.id);
    if (rep) {
      pendingCommandRef.current = null;
      if (pending.action === 'open') {
        setOpenPending(false);
        if (!rep.ok) setActionError(rep.error || t('agents.saveFailed'));
      } else if (pending.action === 'builtin') {
        setOpenPending(false);
        if (!rep.ok || !rep.role) setActionError(rep.error || t('agents.saveFailed'));
        else {
          // Editing a built-in role writes a role file of its name, which the engine then uses in its place.
          const role = pending.role;
          setEditingRole({ isNew: true, role: { scope: 'project', name: role.name, nameLocked: true, builtin: true, title: roleTitle(role, t), job: roleJob(role, t),
            description: rep.role.description, instructions: rep.role.instructions, model: rep.role.model, thinkingLevel: rep.role.thinkingLevel, tools: rep.role.tools } as Partial<Role> & { scope: RoleScope } });
        }
      } else if (pending.action === 'delete') {
        setDeletePending(false);
        if (!rep.ok) {
          setActionError(rep.error || t('agents.deleteFailed'));
          if (rep.error?.includes('해시') || rep.error?.toLowerCase().includes('hash') || rep.error?.toLowerCase().includes('conflict')) {
            setConflictOccurred(true);
          }
        } else {
          setDeleteTarget(null);
        }
      }
    }
  }, [replies, t]);

  useEffect(() => {
    if (!deletePending && !openPending) return;
    const timer = window.setTimeout(() => {
      pendingCommandRef.current = null; setDeletePending(false); setOpenPending(false); setActionError(t('setup.noReply'));
    }, REPLY_TIMEOUT_MS);
    return () => window.clearTimeout(timer);
  }, [deletePending, openPending, t]);
  useEffect(() => {
    if (connected) return;
    pendingCommandRef.current = null; setDeletePending(false); setOpenPending(false);
  }, [connected]);

  useEffect(() => { pendingCommandRef.current = null; setOpenPending(false); setDeletePending(false); setActionError(null); }, [sessionId]);

  // A built-in role opens in the form with the engine's definition of it.
  const editBuiltin = (role: Role) => {
    if (!connected || inert || pendingCommandRef.current) return;
    const requestId = newId();
    pendingCommandRef.current = { id: requestId, action: 'builtin', role };
    setOpenPending(true);
    setActionError(null);
    if (!send('agents/command', { requestId, action: 'builtinRole', name: role.name })) {
      pendingCommandRef.current = null; setOpenPending(false); setActionError(t('error.hostUnavailable'));
    }
  };
  // Open a role file the form cannot edit in the VS editor.
  const openRole = (role: Role) => {
    if (!connected || inert || pendingCommandRef.current) return;
    const requestId = newId();
    pendingCommandRef.current = { id: requestId, action: 'open' };
    setOpenPending(true);
    setActionError(null);
    if (!send('agents/command', { requestId, action: 'openRole', scope: role.scope as 'project' | 'user', name: role.name })) {
      pendingCommandRef.current = null; setOpenPending(false); setActionError(t('error.hostUnavailable'));
    }
  };
  // Delete execution
  const executeDelete = () => {
    if (!deleteTarget || !connected || inert || pendingCommandRef.current || deletePending) return;
    setDeletePending(true);
    const requestId = newId();
    pendingCommandRef.current = { id: requestId, action: 'delete' };
    setActionError(null);
    setConflictOccurred(false);
    const delivered = deleteTarget.kind === 'role'
      ? send('agents/command', {
        requestId,
        action: 'deleteRole',
        scope: deleteTarget.scope === 'user' ? 'user' : 'project',
        name: deleteTarget.name,
        expectedHash: deleteTarget.hash,
      }) : send('agents/command', {
        requestId,
        action: 'deleteTeam',
        scope: deleteTarget.scope,
        name: deleteTarget.id,
        expectedHash: deleteTarget.hash,
      });
    if (!delivered) { pendingCommandRef.current = null; setDeletePending(false); setActionError(t('error.hostUnavailable')); }
  };

  return (
    <div className="agents-panel" data-testid="agents-panel" inert={inert || undefined} onClickCapture={event => {
      if (event.target instanceof Element && !event.target.closest('.agents-dialog')) dialogOpenerRef.current = event.target.closest<HTMLElement>('button');
    }}>
      <ViewChips prefix="agents" label={t('tab.agents')} views={agentsViews.map(id => ({ id, label: t(`agents.view.${id}`), badge: counts[id] }))} active={view} onChange={chooseView} inert={inert} />
      <div className="agents-scroll">
      {actionError && (
        <div className="agents-banner error" role="alert" data-testid="agents-action-error">
          <Icon name="error" size={13} />
          <span>{revealControls(actionError)}</span>
          {conflictOccurred && (
            <button type="button" className="btn subtle small" onClick={() => send('agents/command', { requestId: newId(), action: 'refresh' })}>
              {t('agents.reload')}
            </button>
          )}
        </div>
      )}

      <section id="agents-teams" role="tabpanel" aria-labelledby="agents-tab-teams" className="agents-section" data-testid="agents-teams-section" hidden={view !== 'teams'}>
        <TeamsCard agents={agents} teams={teams} effective={effective} locale={locale} connected={connected} inert={inert}
          editorOpen={editingTeam !== null} exportOpen={exportingTeam !== null} importOpen={importingTeam}
          onNew={() => setEditingTeam({ isNew: true, team: { scope: 'project', mode: 'lead', members: [], membersOnly: true, instructions: '' } })}
          onImport={() => setImportingTeam(true)}
          onMessage={team => onSelectTarget({ kind: 'team', id: team.id })}
          onEdit={team => setEditingTeam({ isNew: false, team: { ...team } })}
          onExport={team => setExportingTeam({ scope: team.scope, id: team.id })}
          onDelete={team => setDeleteTarget({ kind: 'team', scope: team.scope, id: team.id, title: team.title, hash: team.hash })} />
      </section>

      <section id="agents-roles" role="tabpanel" aria-labelledby="agents-tab-roles" className="agents-section" data-testid="agents-roles-section" hidden={view !== 'roles'}>
        <RolesCard roles={agents?.roles ?? []} hidden={agents?.rolesHidden} effective={effective} main={agents?.main} setup={setup} locale={locale} connected={connected} inert={inert} busy={busy}
          dialogOpen={editingRole !== null || avatarRole !== null || editingMain} openPending={openPending} modelUnavailable={configuredModelUnavailable}
          onEditMain={() => setEditingMain(true)}
          onNew={() => setEditingRole({ isNew: true, role: { scope: 'project', tools: 'all', instructions: '' } })}
          onMessage={role => onSelectTarget({ kind: 'agent', id: role.name })}
          onCopy={role => setEditingRole({
            isNew: true,
            role: {
              scope: 'project',
              // The built-in instructions live in the engine and are not copied.
              name: `${role.name}-copy`,
              nameLocked: true,
              title: locale.startsWith('ko') ? `${roleTitle(role, t)} (사본)` : `${roleTitle(role, t)} (copy)`,
              job: roleJob(role, t),
              description: roleDescription(role, t),
              model: role.model,
              thinkingLevel: role.thinkingLevel,
              tools: role.tools === 'custom' ? 'readOnly' : role.tools,
              copiedCustomTools: role.tools === 'custom',
              copiedBuiltin: true,
            } as Partial<Role> & { scope: RoleScope },
          })}
          onEdit={role => setEditingRole({ isNew: false, role: { ...role } })}
          onEditBuiltin={editBuiltin}
          onEditAvatar={setAvatarRole}
          onOpen={openRole}
          onDelete={role => setDeleteTarget({ kind: 'role', scope: role.scope, name: role.name, title: roleTitle(role, t), hash: role.hash })} />
      </section>

      <section id="agents-advisors" role="tabpanel" aria-labelledby="agents-tab-advisors" hidden={view !== 'advisors'}><AdvisorsPanel controller={advisors} setup={setup} setupReplies={setupReplies} connected={connected} selected={view === 'advisors'} inert={inert} root={workspaceKey} sessionId={sessionId} /></section>

      <section id="agents-hooks" role="tabpanel" aria-labelledby="agents-tab-hooks" hidden={view !== 'hooks'}><AgentRulesView onCount={setAutomationCount} locale={locale} workspaceKey={workspaceKey} sessionId={sessionId} agents={agents} connected={connected} busy={busy} newSessionPending={newSessionPending} inert={inert} selected={view === 'hooks'} onShowPermissions={onShowPermissions} /></section>

      {editingMain && <MainRoleEditor returnFocus={dialogOpenerRef.current} disabled={!connected || inert} replies={replies} onClose={() => setEditingMain(false)} />}

      {avatarRole && <AvatarEditor role={avatarRole} title={roleTitle(avatarRole, t)} returnFocus={dialogOpenerRef.current} disabled={!connected || inert} replies={replies} onClose={() => setAvatarRole(null)} />}

      {/* ROLE EDITOR MODAL */}
      {editingRole && (
        <RoleEditorDialog
          editingRole={editingRole}
          returnFocus={dialogOpenerRef.current}
          setup={setup}
          setupReplies={setupReplies}
          disabled={!connected || inert}
          existingNames={agents?.roles.map(role => `${role.scope}:${role.name}`) || []}
          locale={locale}
          replies={replies}
          onClose={() => setEditingRole(null)}
          onAssignFailed={setActionError}
        />
      )}

      {/* TEAM EDITOR MODAL */}
      {editingTeam && (
        <TeamEditorDialog
          editingTeam={editingTeam}
          returnFocus={dialogOpenerRef.current}
          roles={effective}
          disabled={!connected || inert}
          existingIds={agents?.teams.map(team => `${team.scope}:${team.id}`) || []}
          replies={replies}
          onClose={() => setEditingTeam(null)}
          onAssignFailed={setActionError}
        />
      )}

      {/* TEAM TEMPLATE EXPORT/IMPORT DIALOGS */}
      {exportingTeam && (
        <TeamExportDialog
          target={exportingTeam}
          returnFocus={dialogOpenerRef.current}
          disabled={!connected || inert}
          replies={replies}
          onClose={() => setExportingTeam(null)}
        />
      )}
      {importingTeam && (
        <TeamImportDialog
          returnFocus={dialogOpenerRef.current}
          disabled={!connected || inert}
          replies={replies}
          onClose={() => setImportingTeam(false)}
        />
      )}

      {/* IN-PAGE DELETE CONFIRMATION DIALOG */}
      {deleteTarget && (
        <AgentDialog testId="inpage-confirm-dialog" className="confirm-dialog" labelledBy="confirm-dialog-title" returnFocus={dialogOpenerRef.current} onClose={() => { if (!deletePending) setDeleteTarget(null); }}>
            <h4 id="confirm-dialog-title">
              {deleteTarget.kind === 'role' ? t('agents.confirmDeleteRoleTitle') : t('agents.confirmDeleteTeamTitle')}
            </h4>
            <p>
              {deleteTarget.kind === 'role'
                ? t('agents.confirmDeleteRoleBody', { name: deleteTarget.title })
                : t('agents.confirmDeleteTeamBody', { title: deleteTarget.title })}
            </p>
            {actionError && <div className="dialog-error" role="alert">{revealControls(actionError)}<button type="button" className="btn subtle small" onClick={() => { send('agents/command', { requestId: newId(), action: 'refresh' }); setDeleteTarget(null); }}>{t('agents.reload')}</button></div>}
            <div className="dialog-actions">
              <button type="button" className="btn subtle" data-testid="confirm-cancel-btn" disabled={deletePending} onClick={() => setDeleteTarget(null)}>
                {t('dialog.cancel')}
              </button>
              <button type="button" className="btn danger" data-testid="confirm-proceed-btn" disabled={deletePending || !connected || inert} onClick={executeDelete}>
                {t('agents.delete')}
              </button>
            </div>
        </AgentDialog>
      )}
      </div>
    </div>
  );
}

// -------------------------------------------------------------------------------------------------
// KNOWLEDGE OF A ROLE OR TEAM
// -------------------------------------------------------------------------------------------------

/**
 * The knowledge checklist of a role or team editor. The boxes start from what the knowledge lists for `name` and only what the person
 * ticks or clears is kept; once the role or team is saved, `assign` sends the resulting list as one command (nothing when nothing changed).
 */
function useKnowledgeChoice(kind: 'role' | 'team', name: string, onFailed: (message: string) => void) {
  const knowledge = useKnowledge();
  const items = usableKnowledge(knowledge.items);
  const [choice, setChoice] = useState<Record<string, boolean>>({});
  const has = (item: KnowledgeItem) => (kind === 'role' ? item.roles : item.teams).includes(name);
  const checked = (item: KnowledgeItem) => choice[item.id] ?? has(item);
  useEffect(() => { knowledge.refresh(); }, []);
  const assign = () => {
    if (!name || !items.some(item => item.id in choice && choice[item.id] !== has(item))) return;
    // A knowledge made only of proposals is not listed: it keeps what it has.
    const ids = knowledge.items.filter(item => item.candidate ? has(item) : checked(item)).map(item => item.id);
    void knowledge.command({ action: 'assign', kind, name, ids }).then(reply => { if (!reply.ok && reply.error) onFailed(reply.error); });
  };
  return { items, checked, toggle: (item: KnowledgeItem, value: boolean) => setChoice(previous => ({ ...previous, [item.id]: value })), assign };
}

/** The knowledge of a role or team editor: every knowledge with its color and name. Nothing is shown when there is none. */
function KnowledgeField({ choice, disabled, testId }: { choice: ReturnType<typeof useKnowledgeChoice>; disabled: boolean; testId: string }) {
  const t = useT();
  if (!choice.items.length) return null;
  return (
    <div className="form-field" role="group" aria-labelledby={`${testId}-label`} data-testid={`${testId}-field`}>
      <label id={`${testId}-label`}>{t('tools.knowledge')}</label>
      <KnowledgeChecklist items={choice.items} checked={choice.checked} onToggle={choice.toggle} disabled={disabled} testId={testId} />
    </div>
  );
}

// -------------------------------------------------------------------------------------------------
// ROLE EDITOR COMPONENT
// -------------------------------------------------------------------------------------------------

function RoleEditorDialog({
  editingRole,
  returnFocus,
  setup,
  setupReplies,
  disabled,
  existingNames,
  locale,
  replies,
  onClose,
  onAssignFailed,
}: {
  editingRole: { isNew: boolean; role: Partial<Role> & { scope: RoleScope } };
  returnFocus: HTMLElement | null;
  setup: SetupState | null;
  setupReplies: SetupReply[];
  disabled: boolean;
  existingNames: string[];
  locale: string;
  replies: AgentsReplyPayload[];
  onClose: () => void;
  /** The role was saved, but its knowledge could not be assigned. */
  onAssignFailed: (message: string) => void;
}) {
  const t = useT();
  const ko = locale.startsWith('ko');
  const [scope, setScope] = useState<RoleScope>(editingRole.role.scope || 'project');
  const [title, setTitle] = useState(editingRole.role.title || '');
  const [name, setName] = useState(editingRole.role.name || '');
  const [nameEdited, setNameEdited] = useState(!editingRole.isNew || (editingRole.role as Partial<Role> & { nameLocked?: boolean }).nameLocked === true);
  // An edited built-in role: its name stays, and the host writes its file from the engine's definition.
  const builtinEdit = (editingRole.role as Partial<Role> & { builtin?: boolean }).builtin === true;
  const initialModel = editingRole.role.model;
  // An engine model role (@task, @slow, ...) other than the fast one stays selectable as it is.
  const modelAlias = initialModel?.startsWith('@') && initialModel !== '@smol' ? initialModel : undefined;
  const aliasLabels: Record<string, MessageKey> = { '@default': 'agents.modelChat', '@slow': 'agents.modelThinking', '@plan': 'agents.modelArchitect', '@task': 'agents.modelSubtask', '@commit': 'agents.modelCommit' };
  const ownTools = editingRole.role.tools === 'custom' && !(editingRole.role as Partial<Role> & { copiedCustomTools?: boolean }).copiedCustomTools;
  const [job, setJob] = useState(editingRole.role.job || '');
  const [description, setDescription] = useState(editingRole.role.description || '');
  const [instructions, setInstructions] = useState(editingRole.role.instructions || '');
  const [modelType, setModelType] = useState<'same' | 'smol' | 'alias' | 'specific'>(
    !initialModel ? 'same' : initialModel === '@smol' ? 'smol' : modelAlias ? 'alias' : 'specific',
  );
  const [specificModel, setSpecificModel] = useState(
    initialModel && initialModel !== '@smol' && !modelAlias ? initialModel : (setup?.models?.[0]?.id ? `${setup.models[0].provider}/${setup.models[0].id}` : ''),
  );
  const [thinkingLevel, setThinkingLevel] = useState(editingRole.role.thinkingLevel || 'auto');
  const [tools, setTools] = useState<RoleTools>((editingRole.role as Partial<Role> & { copiedCustomTools?: boolean }).copiedCustomTools ? 'readOnly' : editingRole.role.tools || 'all');
  const [error, setError] = useState<string | null>(null);
  const [conflict, setConflict] = useState(false);
  const [saving, setSaving] = useState(false);
  const savingIdRef = useRef<string | null>(null);
  const knowledgeChoice = useKnowledgeChoice('role', name.trim(), onAssignFailed);

  useEffect(() => {
    if (!savingIdRef.current) return;
    const rep = replies.find((r) => r.requestId === savingIdRef.current);
    if (rep) {
      savingIdRef.current = null;
      setSaving(false);
      if (rep.ok) {
        knowledgeChoice.assign();
        onClose();
      } else {
        setError(rep.error || t('agents.saveFailed'));
        if (rep.error?.includes('해시') || rep.error?.toLowerCase().includes('hash') || rep.error?.toLowerCase().includes('conflict')) {
          setConflict(true);
        }
      }
    }
  }, [replies, t, onClose]);

  useEffect(() => {
    if (!saving) return;
    const timer = window.setTimeout(() => { savingIdRef.current = null; setSaving(false); setError(t('setup.noReply')); }, REPLY_TIMEOUT_MS);
    return () => window.clearTimeout(timer);
  }, [saving, t]);

  // Suggest slug from title when user hasn't explicitly edited name
  const handleTitleChange = (val: string) => {
    setTitle(val);
    if (editingRole.isNew && !nameEdited) {
      const slugCandidate = val
        .trim()
        .toLowerCase()
        .replace(/[^a-z0-9]+/g, '-')
        .replace(/^-|-$/g, '')
        .slice(0, 32);
      if (slugCandidate) setName(slugCandidate);
    }
  };

  const applyTemplate = (tpl: TemplateDef) => {
    setTitle(ko ? tpl.titleKo : tpl.titleEn);
    setName(tpl.name);
    setNameEdited(true);
    setJob(ko ? tpl.jobKo : tpl.jobEn);
    setDescription(ko ? tpl.descKo : tpl.descEn);
    setInstructions(ko ? tpl.instrKo : tpl.instrEn);
    setTools(tpl.tools);
  };

  const nameError = useMemo(() => {
    const names = existingNames.filter(value => value.startsWith(`${scope}:`)).map(value => value.slice(scope.length + 1));
    return validateSlug(name, editingRole.isNew ? names : names.filter(value => value !== editingRole.role.name), builtinEdit || !editingRole.isNew);
  }, [name, scope, existingNames, editingRole, builtinEdit]);
  const descriptionError = !description.trim() || description.length > 1000;
  const invalid = !!nameError || !title.trim() || descriptionError || (modelType === 'specific' && !specificModel) || (tools === 'custom' && !ownTools) || disabled;
  // Save was pressed: from then on empty required fields say so (not while the role is being written).
  const [attempted, setAttempted] = useState(false);
  const empty = !title.trim() || nameError === 'slugRequired' || !description.trim();
  const shownNameError = nameError && (nameError !== 'slugRequired' || attempted) ? nameError : null;
  const shownDescriptionError = descriptionError && (description.trim() !== '' || attempted);
  // Save stays pressable while only empty fields stand in the way, so pressing it can name them.
  const blocked = (!!nameError && nameError !== 'slugRequired') || description.length > 1000 || (modelType === 'specific' && !specificModel) || (tools === 'custom' && !ownTools) || disabled;

  const save = () => {
    if (empty) setAttempted(true);
    if (invalid || saving) return;
    setSaving(true);
    setError(null);
    setConflict(false);

    const modelValue = modelType === 'same' ? undefined : modelType === 'smol' ? '@smol' : modelType === 'alias' ? modelAlias : specificModel;
    const rolePayload: Role & { builtin?: boolean } = {
      name: name.trim(),
      title: title.trim().slice(0, 40),
      job: job.trim().slice(0, 80),
      description: description.trim().slice(0, 1000),
      instructions: instructions.trim().slice(0, 24000),
      model: modelValue,
      thinkingLevel: thinkingLevel === 'auto' ? undefined : thinkingLevel,
      tools,
      scope: editingRole.isNew ? (scope === 'user' ? 'user' : 'project') : editingRole.role.scope,
      editable: true,
      hash: editingRole.role.hash,
      ...(builtinEdit ? { builtin: true } : {}),
    };

    const requestId = newId();
    savingIdRef.current = requestId;
    if (!send('agents/command', {
      requestId,
      action: 'saveRole',
      scope: rolePayload.scope === 'user' ? 'user' : 'project',
      role: rolePayload,
      expectedHash: editingRole.role.hash,
    })) { savingIdRef.current = null; setSaving(false); setError(t('error.hostUnavailable')); }
  };

  return (
    <AgentDialog testId="role-editor" className="editor-dialog" labelledBy="role-editor-title" returnFocus={returnFocus} onClose={() => { if (!saving) onClose(); }}>
        <div className="dialog-head">
          <h4 id="role-editor-title">{editingRole.isNew && !builtinEdit ? t('agents.createRole') : t('agents.editRole')}</h4>
          <button type="button" className="icon-btn" disabled={saving} aria-label={t('agents.close')} onClick={onClose}><Icon name="x" size={13} /></button>
        </div>

        {editingRole.isNew && !builtinEdit && (
          <div className="templates-bar">
            <span className="templates-label">{t('agents.templates')}</span>
            {TEMPLATES.map((tpl) => (
              <button
                key={tpl.key}
                type="button"
                className="btn subtle small"
                data-testid={`template-${tpl.key}`}
                onClick={() => applyTemplate(tpl)}
              >
                {ko ? tpl.titleKo : tpl.titleEn}
              </button>
            ))}
          </div>
        )}

        {error && (
          <div className="dialog-error" role="alert">
            <Icon name="error" size={12} />
            <span>{revealControls(error)}</span>
            {conflict && (
              <button type="button" className="btn subtle small" onClick={() => { send('agents/command', { requestId: newId(), action: 'refresh' }); onClose(); }}>
                {t('agents.reload')}
              </button>
            )}
          </div>
        )}

        <div className="dialog-form">
          {editingRole.isNew && (
            <div className="form-field">
              <label htmlFor="role-scope-select">{t('agents.scopeLabel')}</label>
              <select id="role-scope-select" value={scope} onChange={(e) => setScope(e.target.value as RoleScope)}>
                <option value="project">{t('agents.scope.project')}</option>
                <option value="user">{t('agents.scope.user')}</option>
              </select>
            </div>
          )}

          <div className="form-field">
            <label htmlFor="role-title-input">{t('agents.titleLabel')}</label>
            <input
              id="role-title-input"
              data-testid="role-title-input"
              type="text"
              maxLength={40}
              value={title}
              onChange={(e) => handleTitleChange(e.target.value)}
              aria-invalid={attempted && !title.trim()}
            />
            {attempted && !title.trim() && <span className="field-error" data-testid="role-title-error">{t('agents.titleRequired')}</span>}
          </div>

          <div className="form-field">
            <label htmlFor="role-name-input">{t('agents.nameLabel')}</label>
            <input
              id="role-name-input"
              data-testid="role-name-input"
              type="text"
              maxLength={40}
              value={name}
              disabled={!editingRole.isNew || builtinEdit}
              onChange={(e) => {
                setName(e.target.value);
                setNameEdited(true);
              }}
            />
            {shownNameError && (
              <span className="field-error" data-testid="role-name-error">
                {t(`agents.slugErrors.${shownNameError}` as MessageKey)}
              </span>
            )}
          </div>

          <div className="form-field">
            <label htmlFor="role-job-input">{t('agents.jobLabel')}</label>
            <input
              id="role-job-input"
              data-testid="role-job-input"
              type="text"
              maxLength={80}
              value={job}
              onChange={(e) => setJob(e.target.value)}
            />
          </div>

          <div className="form-field">
            <label htmlFor="role-desc-input">{t('agents.descLabel')}</label>
            <textarea
              id="role-desc-input"
              maxLength={1000}
              rows={2}
              value={description}
              onChange={(e) => setDescription(e.target.value)}
              required aria-invalid={shownDescriptionError}
            />
            {shownDescriptionError && <span className="field-error" data-testid="role-description-error">{t('agents.descriptionRequired')}</span>}
          </div>

          <div className="form-field">
            <label htmlFor="role-instr-input">{t('agents.instrLabel')}</label>
            <textarea
              id="role-instr-input"
              maxLength={24000}
              rows={4}
              value={instructions}
              onChange={(e) => setInstructions(e.target.value)}
            />
          </div>

          <div className="form-field">
            <label htmlFor="role-model-type">{t('agents.modelLabel')}</label>
            <select id="role-model-type" data-testid="role-model-type" value={modelType} onChange={e => setModelType(e.target.value as 'same' | 'smol' | 'alias' | 'specific')}>
              <option value="same">{t('agents.modelSameAsChat')}</option>
              <option value="smol">{t('agents.modelFastSmol')}</option>
              {modelAlias && <option value="alias">{aliasLabels[modelAlias] ? t(aliasLabels[modelAlias]) : modelAlias.slice(1)}</option>}
              <option value="specific">{t('agents.modelSpecific')}</option>
            </select>
            {modelType === 'specific' && (
              <RoleModelPicker setup={setup} replies={setupReplies} value={specificModel} onChange={setSpecificModel} disabled={disabled || saving} />
            )}
          </div>

          <div className="form-row role-options-row">
            <div className="form-field">
              <label htmlFor="role-thinking-select">{t('status.thinking')}</label>
              <select
                id="role-thinking-select"
                value={thinkingLevel}
                onChange={(e) => setThinkingLevel(e.target.value)}
              >
                <option value="auto">{t('agents.thinkingAuto')}</option>
                <option value="off">{t('agents.thinkingOff')}</option>
                <option value="minimal">{t('agents.thinkingMinimal')}</option>
                <option value="low">{t('agents.thinkingLow')}</option>
                <option value="medium">{t('agents.thinkingMedium')}</option>
                <option value="high">{t('agents.thinkingHigh')}</option>
                <option value="xhigh">{t('agents.thinkingXhigh')}</option>
                <option value="max">{t('agents.thinkingMax')}</option>
              </select>
            </div>

            <div className="form-field">
              <label htmlFor="role-tools-select">{t('agents.toolsLabel')}</label>
              <select
                id="role-tools-select"
                value={tools}
                onChange={(e) => setTools(e.target.value as RoleTools)}
                disabled={tools === 'custom' && !ownTools}
              >
                <option value="all">{t('agents.tools.all')}</option>
                <option value="readOnly">{t('agents.tools.readOnly')}</option>
                {(ownTools || tools === 'custom') && <option value="custom">{t('agents.tools.custom')}</option>}
              </select>
            </div>
          </div>

          <KnowledgeField choice={knowledgeChoice} disabled={saving} testId="role-knowledge" />
        </div>

        <div className="dialog-foot">
          <button type="button" className="btn subtle" disabled={saving} onClick={onClose}>{t('dialog.cancel')}</button>
          <button
            type="button"
            className="btn primary"
            data-testid="role-save-btn"
            disabled={blocked || saving}
            onClick={save}
          >
            {saving ? <span className="spinner" /> : t('agents.save')}
          </button>
        </div>
    </AgentDialog>
  );
}

// -------------------------------------------------------------------------------------------------
// TEAM EDITOR COMPONENT
// -------------------------------------------------------------------------------------------------

/** The roles a team can still take, searchable and paged (a workspace can hold thousands); a row's plus adds it as the next member. */
function TeamMemberPicker({ roles, onAdd }: { roles: Role[]; onAdd: (name: string) => void }) {
  const t = useT();
  const [query, setQuery] = useState('');
  const shown = useMemo(() => roles.filter(role => matches(query, roleTitle(role, t), role.name, roleJob(role, t)))
    .sort((a, b) => byName(roleTitle(a, t), roleTitle(b, t)) || byName(a.name, b.name)), [roles, query, t]);
  const paged = usePaged(shown, query, 8);
  return <div className="team-member-picker" data-testid="team-member-picker">
    <Toolbar><SearchBox value={query} onChange={setQuery} label={t('agents.addMember')} testId="team-member-search" /></Toolbar>
    <DataTable columns="auto minmax(0, 1fr) auto" head={['', t('ui.col.name'), '']} label={t('agents.addMember')} testId="team-member-table" empty={!shown.length} emptyRows={1}>
      {paged.slice.map(role => {
        const title = roleTitle(role, t);
        return <Row key={`${role.scope}:${role.name}`} testId={`team-member-option-${role.name}`}>
          <Cell><Avatar name={role.name} label={title} size={20} look={role.avatar} scope={role.scope} /></Cell>
          <Cell className="agents-name-cell"><span className="agents-name-line"><strong>{revealControls(title)}</strong><span className="ui-mono ui-muted agents-id">{revealControls(role.name)}</span></span></Cell>
          <Actions><IconButton icon="plus" label={t('agents.addMember')} testId={`team-member-add-${role.name}`} onClick={() => onAdd(role.name)} /></Actions>
        </Row>;
      })}
    </DataTable>
    <Pager paged={paged} testId="team-member-pager" />
  </div>;
}

function TeamEditorDialog({
  editingTeam,
  returnFocus,
  roles,
  existingIds,
  disabled,
  replies,
  onClose,
  onAssignFailed,
}: {
  editingTeam: { isNew: boolean; team: Partial<Team> & { scope: TeamScope } };
  returnFocus: HTMLElement | null;
  roles: Role[];
  existingIds: string[];
  disabled: boolean;
  replies: AgentsReplyPayload[];
  onClose: () => void;
  /** The team was saved, but its knowledge could not be assigned. */
  onAssignFailed: (message: string) => void;
}) {
  const t = useT();
  const [scope, setScope] = useState<TeamScope>(editingTeam.team.scope || 'project');
  const [title, setTitle] = useState(editingTeam.team.title || '');
  const [id, setId] = useState(editingTeam.team.id || '');
  const [idEdited, setIdEdited] = useState(!editingTeam.isNew);
  const [goal, setGoal] = useState(editingTeam.team.goal || '');
  const [mode, setMode] = useState<TeamMode>(editingTeam.team.mode || 'lead');
  const [members, setMembers] = useState<string[]>(editingTeam.team.members || []);
  const [instructions, setInstructions] = useState(editingTeam.team.instructions || '');
  const [membersOnly, setMembersOnly] = useState(editingTeam.team.membersOnly ?? true);
  const [isolation, setIsolation] = useState(editingTeam.team.isolation ?? false);
  const [error, setError] = useState<string | null>(null);
  const [conflict, setConflict] = useState(false);
  const [saving, setSaving] = useState(false);
  // Save was pressed: from then on a team with too few members says so (not while it is being put together).
  const [attempted, setAttempted] = useState(false);
  const savingIdRef = useRef<string | null>(null);
  const knowledgeChoice = useKnowledgeChoice('team', id.trim(), onAssignFailed);

  useEffect(() => {
    if (!savingIdRef.current) return;
    const rep = replies.find((r) => r.requestId === savingIdRef.current);
    if (rep) {
      savingIdRef.current = null;
      setSaving(false);
      if (rep.ok) {
        knowledgeChoice.assign();
        onClose();
      } else {
        setError(rep.error || t('agents.saveFailed'));
        if (rep.error?.includes('해시') || rep.error?.toLowerCase().includes('hash') || rep.error?.toLowerCase().includes('conflict')) {
          setConflict(true);
        }
      }
    }
  }, [replies, t, onClose]);

  useEffect(() => {
    if (!saving) return;
    const timer = window.setTimeout(() => { savingIdRef.current = null; setSaving(false); setError(t('setup.noReply')); }, REPLY_TIMEOUT_MS);
    return () => window.clearTimeout(timer);
  }, [saving, t]);

  const handleTitleChange = (val: string) => {
    setTitle(val);
    if (editingTeam.isNew && !idEdited) {
      const slugCandidate = val
        .trim()
        .toLowerCase()
        .replace(/[^a-z0-9]+/g, '-')
        .replace(/^-|-$/g, '')
        .slice(0, 32);
      if (slugCandidate) setId(slugCandidate);
    }
  };

  const idError = useMemo(() => {
    const ids = existingIds.filter(value => value.startsWith(`${scope}:`)).map(value => value.slice(scope.length + 1));
    return validateSlug(id, editingTeam.isNew ? ids : ids.filter(value => value !== editingTeam.team.id));
  }, [id, scope, existingIds, editingTeam]);

  const membersError = useMemo(() => {
    if (members.length < 2) return t('agents.membersMin');
    if (new Set(members).size !== members.length) return t('agents.membersDuplicate');
    if (members.some(name => !roles.some(role => role.name === name && !role.error))) return t('agents.missingMember');
    return null;
  }, [members, roles, t]);

  const addMember = (roleName: string) => {
    if (members.includes(roleName)) return;
    setMembers([...members, roleName]);
    // The new member is last: show its page.
    membersPaged.setPage(Math.floor(members.length / PAGE_SIZE));
  };

  const removeMember = (index: number) => {
    setMembers(members.filter((_, i) => i !== index));
  };

  const moveMember = (index: number, direction: -1 | 1) => {
    const target = index + direction;
    if (target < 0 || target >= members.length) return;
    const next = [...members];
    const temp = next[index]!;
    next[index] = next[target]!;
    next[target] = temp;
    setMembers(next);
    // A member moved across a page edge stays in view.
    membersPaged.setPage(Math.floor(target / PAGE_SIZE));
  };

  const tooFew = members.length < 2;
  const shownMembersError = membersError && (!tooFew || attempted) ? membersError : null;
  const shownIdError = idError && (idError !== 'slugRequired' || attempted) ? idError : null;
  const membersPaged = usePaged(members, editingTeam.team.id ?? '');
  const save = () => {
    if (tooFew || !title.trim() || idError === 'slugRequired') setAttempted(true);
    if (idError || membersError || !title.trim() || saving || disabled) return;
    setSaving(true);
    setError(null);
    setConflict(false);

    const teamPayload: Team = {
      id: id.trim(),
      title: title.trim().slice(0, 40),
      goal: goal.trim().slice(0, 400),
      members,
      mode,
      instructions: instructions.trim().slice(0, 8000),
      membersOnly,
      isolation,
      scope: editingTeam.isNew ? scope : editingTeam.team.scope,
      file: editingTeam.team.file || '',
      hash: editingTeam.team.hash || '',
      missing: [],
    };

    const requestId = newId();
    savingIdRef.current = requestId;
    if (!send('agents/command', {
      requestId,
      action: 'saveTeam',
      scope: teamPayload.scope,
      team: teamPayload,
      expectedHash: editingTeam.team.hash,
    })) { savingIdRef.current = null; setSaving(false); setError(t('error.hostUnavailable')); }
  };

  const availableRolesToAdd = useMemo(() => roles.filter((r) => !members.includes(r.name) && !r.error), [roles, members]);

  return (
    <AgentDialog testId="team-editor" className="editor-dialog" labelledBy="team-editor-title" returnFocus={returnFocus} onClose={() => { if (!saving) onClose(); }}>
        <div className="dialog-head">
          <h4 id="team-editor-title">{editingTeam.isNew ? t('agents.createTeam') : t('agents.editTeam')}</h4>
          <button type="button" className="icon-btn" disabled={saving} aria-label={t('agents.close')} onClick={onClose}><Icon name="x" size={13} /></button>
        </div>

        {error && (
          <div className="dialog-error" role="alert">
            <Icon name="error" size={12} />
            <span>{revealControls(error)}</span>
            {conflict && (
              <button type="button" className="btn subtle small" onClick={() => { send('agents/command', { requestId: newId(), action: 'refresh' }); onClose(); }}>
                {t('agents.reload')}
              </button>
            )}
          </div>
        )}

        <div className="dialog-form">
          {editingTeam.isNew && (
            <div className="form-field">
              <label htmlFor="team-scope-select">{t('agents.scopeLabel')}</label>
              <select id="team-scope-select" value={scope} onChange={(e) => setScope(e.target.value as TeamScope)}>
                <option value="project">{t('agents.scope.project')}</option>
                <option value="user">{t('agents.scope.user')}</option>
              </select>
            </div>
          )}

          <div className="form-field">
            <label htmlFor="team-title-input">{t('agents.titleLabel')}</label>
            <input
              id="team-title-input"
              data-testid="team-title-input"
              type="text"
              maxLength={40}
              value={title}
              onChange={(e) => handleTitleChange(e.target.value)}
              aria-invalid={attempted && !title.trim()}
            />
            {attempted && !title.trim() && <span className="field-error" data-testid="team-title-error">{t('agents.titleRequired')}</span>}
          </div>

          <div className="form-field">
            <label htmlFor="team-id-input">{t('agents.idLabel')}</label>
            <input
              id="team-id-input"
              data-testid="team-id-input"
              type="text"
              maxLength={40}
              value={id}
              disabled={!editingTeam.isNew}
              onChange={(e) => {
                setId(e.target.value);
                setIdEdited(true);
              }}
            />
            {shownIdError && (
              <span className="field-error" data-testid="team-id-error">
                {t(`agents.slugErrors.${shownIdError}` as MessageKey)}
              </span>
            )}
          </div>

          <div className="form-field">
            <label htmlFor="team-goal-input">{t('agents.goalLabel')}</label>
            <textarea
              id="team-goal-input"
              maxLength={400}
              rows={2}
              value={goal}
              onChange={(e) => setGoal(e.target.value)}
            />
          </div>

          {/* Members (two or more, no upper limit), in order, a page at a time */}
          <div className="form-field">
            <label>{t('agents.membersLabel')}</label>
            {shownMembersError && (
              <span className="field-error" data-testid="team-members-error">
                {shownMembersError}
              </span>
            )}
            <div className="selected-members-list">
              {membersPaged.slice.map((memName, at) => {
                const idx = membersPaged.from - 1 + at;
                const memberTitle = roleTitle(roles.find((r) => r.name === memName), t, memName);
                return (
                  <div key={memName} className="selected-member-row" data-testid={`team-selected-member-${idx}`} data-member-id={memName}>
                    <span className="member-index">{idx + 1}.</span>
                    <span className="member-title">{revealControls(memberTitle)}</span>
                    <span className="member-slug">({memName})</span>
                    <div className="member-controls">
                      <button
                        type="button"
                        className="icon-btn"
                        data-testid={`team-member-up-${memName}`}
                        disabled={idx === 0}
                        onClick={() => moveMember(idx, -1)}
                        title={t('agents.moveUp')}
                      >
                        <Icon name="arrowUp" size={11} />
                      </button>
                      <button
                        type="button"
                        className="icon-btn"
                        data-testid={`team-member-down-${memName}`}
                        disabled={idx === members.length - 1}
                        onClick={() => moveMember(idx, 1)}
                        title={t('agents.moveDown')}
                      >
                        <Icon name="arrowDown" size={11} />
                      </button>
                      <button
                        type="button"
                        className="icon-btn danger"
                        onClick={() => removeMember(idx)}
                        title={t('dialog.cancel')}
                      >
                        <Icon name="x" size={11} />
                      </button>
                    </div>
                  </div>
                );
              })}
            </div>

            <Pager paged={membersPaged} testId="team-members-pager" />
            {availableRolesToAdd.length > 0 && <TeamMemberPicker roles={availableRolesToAdd} onAdd={addMember} />}
          </div>

          <div className="form-field">
            <label htmlFor="team-mode-select">{t('agents.modeLabel')}</label>
            <select id="team-mode-select" value={mode} onChange={(e) => setMode(e.target.value as TeamMode)}>
              <option value="lead">{t('agents.mode.lead')}</option>
              <option value="parallel">{t('agents.mode.parallel')}</option>
              <option value="pipeline">{t('agents.mode.pipeline')}</option>
            </select>
          </div>

          <div className="form-field">
            <label htmlFor="team-instr-input">{t('agents.instrLabel')}</label>
            <textarea
              id="team-instr-input"
              maxLength={8000}
              rows={3}
              value={instructions}
              onChange={(e) => setInstructions(e.target.value)}
            />
          </div>

          <div className="form-field switch-field">
            <Switch label={t('agents.membersOnlyDesc')} checked={membersOnly} onChange={setMembersOnly} testId="team-members-only-input" />
            <Switch label={t('agents.isolationDesc')} checked={isolation} onChange={setIsolation} testId="team-isolation-input" />
          </div>

          <KnowledgeField choice={knowledgeChoice} disabled={saving} testId="team-knowledge" />
        </div>

        <div className="dialog-foot">
          <button type="button" className="btn subtle" disabled={saving} onClick={onClose}>{t('dialog.cancel')}</button>
          <button
            type="button"
            className="btn primary"
            data-testid="team-save-btn"
            disabled={(!!idError && idError !== 'slugRequired') || (!!membersError && !tooFew) || saving || disabled}
            onClick={save}
          >
            {saving ? <span className="spinner" /> : t('agents.save')}
          </button>
        </div>
    </AgentDialog>
  );
}

// -------------------------------------------------------------------------------------------------
// TEAM TEMPLATE EXPORT/IMPORT DIALOGS
// -------------------------------------------------------------------------------------------------

const TEMPLATE_LIMIT_BYTES = 256 * 1024;
/** Import preview: what will be created is good news, a name clash blocks the import. */
const statusTone = (status: string): BadgeTone => status === 'new' ? 'ok' : status === 'conflict' ? 'error' : 'neutral';

function TeamExportDialog({
  target,
  returnFocus,
  disabled,
  replies,
  onClose,
}: {
  target: { scope: TeamScope; id: string };
  returnFocus: HTMLElement | null;
  disabled: boolean;
  replies: AgentsReplyPayload[];
  onClose: () => void;
}) {
  const t = useT();
  const [template, setTemplate] = useState<string | null>(null);
  const [error, setError] = useState<string | null>(null);
  const [copied, setCopied] = useState(false);
  const [pending, setPending] = useState(false);
  const pendingIdRef = useRef<string | null>(null);
  const [action, setAction] = useState<'export' | 'open'>('export');

  useEffect(() => {
    if (!pendingIdRef.current) return;
    const rep = replies.find((r) => r.requestId === pendingIdRef.current);
    if (rep) {
      pendingIdRef.current = null;
      setPending(false);
      if (rep.ok) {
        if (action === 'export' && rep.template !== undefined) setTemplate(rep.template);
        else if (action === 'open') onClose();
      } else {
        setError(rep.error || t('agents.saveFailed'));
      }
    }
  }, [replies, t, onClose, action]);

  useEffect(() => {
    if (!pending) return;
    const timer = window.setTimeout(() => { pendingIdRef.current = null; setPending(false); setError(t('setup.noReply')); }, REPLY_TIMEOUT_MS);
    return () => window.clearTimeout(timer);
  }, [pending, t]);

  // Fetch once on open: the template answers through the shared agents/reply stream.
  useEffect(() => {
    const requestId = newId();
    pendingIdRef.current = requestId;
    setAction('export');
    setPending(true);
    setError(null);
    if (!send('agents/command', { requestId, action: 'exportTeam', scope: target.scope, name: target.id })) {
      pendingIdRef.current = null; setPending(false); setError(t('error.hostUnavailable'));
    }
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, []);

  const copy = async () => {
    if (template === null) return;
    try {
      await navigator.clipboard.writeText(template);
      setCopied(true);
    } catch {
      setCopied(false);
      setError(t('agents.templateCopyHint'));
    }
  };

  const openInEditor = () => {
    if (pending) return;
    const requestId = newId();
    pendingIdRef.current = requestId;
    setAction('open');
    setPending(true);
    setError(null);
    if (!send('agents/command', { requestId, action: 'openTeamExport', scope: target.scope, name: target.id })) {
      pendingIdRef.current = null; setPending(false); setError(t('error.hostUnavailable'));
    }
  };

  return (
    <AgentDialog testId="team-export-dialog" className="editor-dialog" labelledBy="team-export-title" returnFocus={returnFocus} onClose={() => { if (!pending) onClose(); }}>
      <div className="dialog-head">
        <h4 id="team-export-title">{t('agents.exportTitle')}</h4>
        <button type="button" className="icon-btn" disabled={pending} aria-label={t('agents.close')} onClick={onClose}><Icon name="x" size={13} /></button>
      </div>
      {error && <div className="dialog-error" role="alert"><Icon name="error" size={12} /><span>{revealControls(error)}</span></div>}
      {template === null ? (
        <div className="dialog-form dialog-loading">{pending ? <span className="spinner" role="status" /> : null}</div>
      ) : (
        <>
          <div className="dialog-form">
            <div className="form-field">
              <textarea data-testid="team-export-text" rows={12} readOnly value={template} onFocus={(e) => e.target.select()} />
            </div>
          </div>
          {copied && <p className="dialog-status" role="status" data-testid="team-export-copied">{t('agents.templateCopied')}</p>}
        </>
      )}
      <div className="dialog-foot">
        <button type="button" className="btn subtle" disabled={pending} onClick={onClose}>{t('agents.close')}</button>
        <button type="button" className="btn subtle" data-testid="team-export-copy" disabled={disabled || pending || template === null} onClick={copy}>{t('agents.templateCopy')}</button>
        <button type="button" className="btn primary" data-testid="team-export-open" disabled={disabled || pending} onClick={openInEditor}>{t('agents.openInEditor')}</button>
      </div>
    </AgentDialog>
  );
}

function TeamImportDialog({
  returnFocus,
  disabled,
  replies,
  onClose,
}: {
  returnFocus: HTMLElement | null;
  disabled: boolean;
  replies: AgentsReplyPayload[];
  onClose: () => void;
}) {
  const t = useT();
  const [text, setText] = useState('');
  const [scope, setScope] = useState<TeamScope>('project');
  const [preview, setPreview] = useState<TeamImportPreview | null>(null);
  const [error, setError] = useState<string | null>(null);
  const [pending, setPending] = useState(false);
  const [importing, setImporting] = useState(false);
  const pendingIdRef = useRef<string | null>(null);
  const [action, setAction] = useState<'preview' | 'import'>('preview');

  useEffect(() => {
    if (!pendingIdRef.current) return;
    const rep = replies.find((r) => r.requestId === pendingIdRef.current);
    if (rep) {
      pendingIdRef.current = null;
      setPending(false);
      setImporting(false);
      if (rep.ok) {
        if (action === 'preview' && rep.preview) {
          setPreview(rep.preview);
          setError(null);
        } else if (action === 'import') {
          onClose();
        }
      } else {
        setError(rep.error || t('agents.saveFailed'));
      }
    }
  }, [replies, t, onClose, action]);

  useEffect(() => {
    if (!pending) return;
    const timer = window.setTimeout(() => { pendingIdRef.current = null; setPending(false); setImporting(false); setError(t('setup.noReply')); }, REPLY_TIMEOUT_MS);
    return () => window.clearTimeout(timer);
  }, [pending, t]);

  const pastedBytes = new TextEncoder().encode(text).length;
  const textError = !text.trim() ? t('agents.templateEmpty') : pastedBytes > TEMPLATE_LIMIT_BYTES ? t('agents.templateTooLarge') : null;

  const doPreview = () => {
    if (pending || textError || disabled) return;
    const requestId = newId();
    pendingIdRef.current = requestId;
    setAction('preview');
    setPending(true);
    setError(null);
    setPreview(null);
    if (!send('agents/command', { requestId, action: 'previewTeamImport', scope, text })) {
      pendingIdRef.current = null; setPending(false); setError(t('error.hostUnavailable'));
    }
  };

  const doImport = () => {
    if (pending || !preview || disabled) return;
    const requestId = newId();
    pendingIdRef.current = requestId;
    setAction('import');
    setPending(true);
    setImporting(true);
    setError(null);
    if (!send('agents/command', { requestId, action: 'importTeam', scope, text, previewHash: preview.hash })) {
      pendingIdRef.current = null; setPending(false); setImporting(false); setError(t('error.hostUnavailable'));
    }
  };

  // The host imports only a new team: an identical one that already exists is refused like a conflict.
  const blocked = !preview || preview.problems.length > 0 ||
    preview.team.status !== 'new' ||
    preview.roles.some((r) => r.status === 'conflict');

  return (
    <AgentDialog testId="team-import-dialog" className="editor-dialog" labelledBy="team-import-title" returnFocus={returnFocus} onClose={() => { if (!pending) onClose(); }}>
      <div className="dialog-head">
        <h4 id="team-import-title">{t('agents.importTitle')}</h4>
        <button type="button" className="icon-btn" disabled={pending} aria-label={t('agents.close')} onClick={onClose}><Icon name="x" size={13} /></button>
      </div>
      {error && <div className="dialog-error" role="alert"><Icon name="error" size={12} /><span>{revealControls(error)}</span></div>}
      <div className="dialog-form">
        <div className="form-field">
          <label htmlFor="team-import-text">{t('agents.templatePasteHint')}</label>
          <textarea id="team-import-text" data-testid="team-import-text" rows={8} value={text} onChange={(e) => { setText(e.target.value); setPreview(null); }} />
          {textError && text.trim() && <span className="field-error" data-testid="team-import-text-error">{textError}</span>}
        </div>
        <div className="form-field">
          <label htmlFor="team-import-scope">{t('agents.templateScope')}</label>
          <select id="team-import-scope" data-testid="team-import-scope" value={scope} onChange={(e) => { setScope(e.target.value as TeamScope); setPreview(null); }}>
            <option value="project">{t('agents.scope.project')}</option>
            <option value="user">{t('agents.scope.user')}</option>
          </select>
        </div>
        {preview && (
          <div className="form-field template-preview" data-testid="team-import-preview-result">
            <DataTable columns="auto minmax(0, 1fr) auto" label={t('agents.templatePreview')} head={['', t('ui.col.name'), t('ui.col.status')]}>
              <Row testId={`team-import-team-${preview.team.status}`}>
                <Cell><Icon name="users" size={13} /></Cell>
                <Cell><strong>{revealControls(preview.team.title)}</strong> <span className="ui-mono ui-muted">{revealControls(preview.team.id)}</span></Cell>
                <Cell><Badge tone={statusTone(preview.team.status)}>{t(`agents.status.${preview.team.status}`)}</Badge></Cell>
              </Row>
              {preview.roles.map((r) => (
                <Row key={r.name} testId={`team-import-role-${r.name}-${r.status}`}>
                  <Cell><Icon name="user" size={13} /></Cell>
                  <Cell>{revealControls(r.title || r.name)} <span className="ui-mono ui-muted">{revealControls(r.name)}</span></Cell>
                  <Cell><Badge tone={statusTone(r.status)}>{t(`agents.status.${r.status}`)}</Badge></Cell>
                </Row>
              ))}
            </DataTable>
            {preview.problems.length > 0 && (
              <div className="field-error" role="alert" data-testid="team-import-problems">
                {preview.problems.map((p, i) => <p key={i}>{revealControls(p)}</p>)}
              </div>
            )}
            {blocked && <span className="field-error" data-testid="team-import-blocked">{t('agents.importBlocked')}</span>}
          </div>
        )}
      </div>
      <div className="dialog-foot">
        <button type="button" className="btn subtle" disabled={pending} onClick={onClose}>{t('dialog.cancel')}</button>
        <button type="button" className="btn subtle" data-testid="team-import-preview" disabled={pending || !!textError || disabled} onClick={doPreview}>{t('agents.templatePreview')}</button>
        <button type="button" className="btn primary" data-testid="team-import-confirm" disabled={pending || blocked || disabled} onClick={doImport}>
          {importing ? <span className="spinner" /> : t('agents.templateImport')}
        </button>
      </div>
    </AgentDialog>
  );
}
