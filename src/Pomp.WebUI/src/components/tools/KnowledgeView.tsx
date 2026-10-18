import { roleTitle as bundledRoleTitle } from '../../bundledDescriptions';
import { useT } from '../../i18n';
import { useContext, useEffect, useId, useRef, useState, type CSSProperties, type ReactNode } from 'react';
import { toolsTranslate, type ToolsTranslate } from '../../i18n/toolsStrings';
import { KNOWLEDGE_SWATCHES, anchorLabel, candidatePieces, defaultKnowledgeColor, isKnowledgeColor, knowledgeUse, normalizeKnowledgeText, sameRoot, savedPieces, usedContent } from '../../knowledge';
import { useKnowledge } from '../../knowledgeState';
import type { KnowledgeDraft, KnowledgeItem, KnowledgePiece, KnowledgeScope, KnowledgeUse } from '../../protocol';
import { RolesContext, TeamsContext, effectiveRoles, effectiveTeams } from '../../teamsCache';
import { useRefreshOnShow } from '../../useRefreshOnShow';
import { Icon } from '../Icon';
import { KnowledgeDot } from '../KnowledgeChecklist';
import { revealControls } from '../RevealControls';
import { SectionRefresh, ViewSection } from '../ViewSection';
import { Actions, Badge, Cell, DataTable, Field, FormActions, IconButton, Pager, Row, SearchBox, Switch, Toolbar, matches, usePaged } from '../ui';
import { InPageDialog } from './InPageDialog';
import './knowledge.css';

/** How many knowledge the add form offers for the name typed so far. */
const NAME_MATCHES = 8;
/** The longest text of one piece, and the longest text the chat input takes from a knowledge. */
const PIECE_LIMIT = 8000;
const INSERT_LIMIT = 24000;
// eslint-disable-next-line no-control-regex
const control = /[\x00-\x1f\x7f]/;
const COLUMNS = 'minmax(0, 1fr) 52px 36px 84px';

interface Anchor { path: string; startLine: number; endLine: number }
/**
 * The new-knowledge and edit form. `color` stays unset until the person picks one: the default follows the list as it loads. `adding`: lines from the
 * code editor, which go to the knowledge the person picks by name (a new one when no knowledge has that name).
 */
interface Editor { id?: string; revision?: number; name: string; color?: string; scope: KnowledgeScope; use: KnowledgeUse; text: string; anchor?: Anchor; rebase?: boolean; adding?: boolean }
/** The form of one piece: a new piece of a knowledge, or the text of an existing one (an anchored piece keeps its lines). `rebase`: the last submit failed. */
interface PieceEditor { id: string; revision: number; pieceId?: string; text: string; anchor?: Anchor; rebase?: boolean }

const nameOk = (name: string) => name.length >= 1 && name.length <= 40 && !control.test(name);
const textOk = (text: string) => text.length <= PIECE_LIMIT;

export interface KnowledgeViewProps {
  locale: string;
  /** The workspace of the page; a draft for another workspace is ignored. */
  workspaceKey: string;
  /** This view is the one shown: the list is refreshed once per show (once the host can be used). */
  active: boolean;
  /** The host cannot be used now (tab not shown, disconnected, or a new conversation starting). */
  disabled: boolean;
  /** The editor's "Add to Knowledge ▸ New": opens the new-knowledge form with these lines as its first piece (each request once). */
  draft?: { payload: KnowledgeDraft; seq: number } | null;
  /** Puts the text into the chat input and opens Chat. */
  onInsertText?: (text: string) => void;
}

/**
 * Tools › Knowledge: the knowledge of the workspace (a name and a color the user chose) as a table, the pieces collected under each, the agents'
 * proposed pieces, and what each one is used as. Everything is text; no piece is ever rendered as markup.
 */
export function KnowledgeView({ locale, workspaceKey, active, disabled, draft, onInsertText }: KnowledgeViewProps) {
  const t = toolsTranslate(locale); const ko = locale.startsWith('ko');
  const knowledge = useKnowledge();
  const roles = effectiveRoles(useContext(RolesContext)); const teams = effectiveTeams(useContext(TeamsContext));
  const [query, setQuery] = useState('');
  const [expanded, setExpanded] = useState<ReadonlySet<string>>(new Set());
  const [editor, setEditor] = useState<Editor>(); const [pieceEditor, setPieceEditor] = useState<PieceEditor>(); const [deleting, setDeleting] = useState<KnowledgeItem>();
  const [validation, setValidation] = useState(''); const [formError, setFormError] = useState(''); const [submitting, setSubmitting] = useState(false);
  const [error, setError] = useState('');
  /** Knowledge whose optimize was asked for and not yet ended: the button is busy from the click, before the host has said anything. */
  const [starting, setStarting] = useState<ReadonlySet<string>>(new Set());
  const items = knowledge.items;
  const itemsRef = useRef(items); itemsRef.current = items;
  const locked = disabled || knowledge.pending;
  /** Asks the host for the list again; a failure shows like any other. */
  async function refresh() {
    setError('');
    const reply = await knowledge.command({ action: 'refresh' });
    if (!reply.ok) setError(reply.error || t('tools.failed'));
  }
  useRefreshOnShow(active && !disabled, () => { void refresh(); });

  // The editor's draft opens the new-knowledge form with its lines as the first piece; a draft for another workspace is not ours.
  const handled = useRef(0);
  const nameField = useRef<HTMLInputElement>(null);
  const [nameFocus, setNameFocus] = useState(0);
  useEffect(() => {
    if (!draft || handled.current === draft.seq) return;
    handled.current = draft.seq;
    const { root, path, startLine, endLine, text } = draft.payload;
    if (workspaceKey && !sameRoot(root, workspaceKey)) return;
    setValidation(''); setFormError(''); setPieceEditor(undefined); setDeleting(undefined);
    setEditor({ name: '', scope: 'project', use: 'original', text, anchor: { path, startLine, endLine }, adding: true });
    setNameFocus(count => count + 1);
  }, [draft, workspaceKey]);
  // Each draft puts the caret in the name, also when the form was open already. Visual Studio brings the chat window forward at the same
  // time and moves the focus about while it does (the page's first button, the chat input of the tab it hides): for a moment the name
  // takes the focus back whenever it lands on nothing in particular, so typing goes on from the keyboard.
  useEffect(() => {
    if (!nameFocus) return;
    const take = (): boolean => {
      const field = nameField.current;
      if (!field || !field.getClientRects().length || field.closest('[inert], [hidden]')) return false;
      if (document.activeElement !== field) field.focus({ preventScroll: false });
      return true;
    };
    let frame = window.requestAnimationFrame(function retry() { if (!take()) frame = window.requestAnimationFrame(retry); });
    const stray = (target: EventTarget | null): boolean => !(target instanceof Element) || target === document.body || target.matches('[role="tab"], [data-pomp-composer]');
    const onFocusIn = (event: FocusEvent): void => { if (stray(event.target)) take(); };
    const onWindowFocus = (): void => { window.requestAnimationFrame(() => { if (stray(document.activeElement)) take(); }); };
    const onFocusOut = (event: FocusEvent): void => { if (event.target === nameField.current && !event.relatedTarget) window.requestAnimationFrame(() => { if (stray(document.activeElement)) take(); }); };
    document.addEventListener('focusin', onFocusIn, true); document.addEventListener('focusout', onFocusOut, true); window.addEventListener('focus', onWindowFocus);
    const stop = window.setTimeout(() => { document.removeEventListener('focusin', onFocusIn, true); document.removeEventListener('focusout', onFocusOut, true); window.removeEventListener('focus', onWindowFocus); }, 2000);
    return () => { window.cancelAnimationFrame(frame); window.clearTimeout(stop); document.removeEventListener('focusin', onFocusIn, true); document.removeEventListener('focusout', onFocusOut, true); window.removeEventListener('focus', onWindowFocus); };
  }, [nameFocus]);

  const closeForms = () => { setValidation(''); setFormError(''); setPieceEditor(undefined); setDeleting(undefined); };
  const usable = items.filter(item => !item.candidate);
  const listed = usable.filter(item => matches(query, item.name, ...savedPieces(item).map(piece => piece.text)));
  const paged = usePaged(listed, `${workspaceKey}:${query}`);
  const proposals = items.flatMap(item => candidatePieces(item).map(piece => ({ item, piece })));
  const color = editor?.color ?? defaultKnowledgeColor(items);
  // A new piece goes into the knowledge of that name: the form offers the names that contain what was typed (the latest first when nothing is typed).
  const typed = editor && !editor.id ? editor.name.trim().toLowerCase() : '';
  const existing = editor && !editor.id ? usable.find(item => item.name.toLowerCase() === typed) : undefined;
  const nameMatches = !editor || editor.id || existing ? [] : (typed
    ? usable.filter(item => item.name.toLowerCase().includes(typed))
    : [...usable].sort((a, b) => b.updatedAt.localeCompare(a.updatedAt))).slice(0, NAME_MATCHES);

  /** The revision a form is sent with: the one it was opened at, or after a failed submit the list's own (the person has seen the error and sends the form again). */
  const revisionOf = (id: string, opened: number, rebase: boolean | undefined) => rebase ? itemsRef.current.find(item => item.id === id)?.revision ?? opened : opened;

  /** A command that changes one thing right away (accept, reject, delete a piece, optimize). */
  async function act(command: Parameters<typeof knowledge.command>[0]) {
    if (locked && command.action !== 'optimize') return;
    setError('');
    const reply = await knowledge.command(command);
    if (!reply.ok) setError(reply.error || t('tools.failed'));
  }
  async function optimize(item: KnowledgeItem) {
    const mark = (on: boolean) => setStarting(previous => { const next = new Set(previous); if (on) next.add(item.id); else next.delete(item.id); return next; });
    mark(true);
    await act({ action: 'optimize', id: item.id, revision: item.revision });
    mark(false);
  }
  async function save() {
    if (!editor || submitting) return;
    const name = editor.name.trim(); const text = editor.text; const hasText = normalizeKnowledgeText(text).length > 0;
    if (!nameOk(name)) { setValidation(t('knowledge.nameInvalid')); return; }
    if (!isKnowledgeColor(color)) { setValidation(t('knowledge.colorInvalid')); return; }
    if (!editor.id && hasText && !textOk(text)) { setValidation(t('knowledge.textInvalid')); return; }
    setValidation(''); setFormError(''); setSubmitting(true);
    const fields = { name, color, scope: editor.scope };
    const reply = await knowledge.command(editor.id
      ? { action: 'update', id: editor.id, revision: revisionOf(editor.id, editor.revision ?? 0, editor.rebase), knowledge: { ...fields, use: editor.use } }
      : { action: 'create', knowledge: fields, ...(hasText ? { piece: { text, ...(editor.anchor ? { anchor: editor.anchor } : {}) } } : {}) });
    setSubmitting(false);
    if (reply.ok) { setEditor(undefined); return; }
    setFormError(reply.error || t('tools.failed'));
    setEditor(previous => previous && { ...previous, rebase: true });
  }
  async function savePiece() {
    if (!pieceEditor || submitting) return;
    const { id, revision, pieceId, text } = pieceEditor;
    if (!normalizeKnowledgeText(text) || !textOk(text)) { setValidation(t('knowledge.textInvalid')); return; }
    setValidation(''); setFormError(''); setSubmitting(true);
    const sent = revisionOf(id, revision, pieceEditor.rebase);
    const reply = await knowledge.command(pieceId ? { action: 'updatePiece', id, revision: sent, pieceId, text } : { action: 'addPiece', id, revision: sent, piece: { text } });
    setSubmitting(false);
    if (reply.ok) { setPieceEditor(undefined); return; }
    setFormError(reply.error || t('tools.failed'));
    setPieceEditor(previous => previous && { ...previous, rebase: true });
  }
  async function remove() {
    if (!deleting || locked) return;
    setSubmitting(true); setFormError('');
    const reply = await knowledge.command({ action: 'delete', id: deleting.id, revision: deleting.revision });
    setSubmitting(false);
    if (reply.ok) setDeleting(undefined); else setFormError(reply.error || t('tools.failed'));
  }
  const toggle = (id: string, open?: boolean) => setExpanded(previous => { const next = new Set(previous); if (open ?? !next.has(id)) next.add(id); else next.delete(id); return next; });
  const pageT = useT();
  const roleTitle = (name: string) => bundledRoleTitle(roles.find(role => role.name === name), pageT, name);
  const teamTitle = (id: string) => teams.find(team => team.id.toLowerCase() === id.toLowerCase())?.title || id;

  const message = error || knowledge.notice;
  const formAlert = (testId: string) => (validation || formError) ? <p className="ui-err" role="alert" data-testid={testId}>{revealControls(validation || formError)}</p> : null;
  const pieceForm = (item: KnowledgeItem) => pieceEditor?.id === item.id && <InPageDialog title={t(pieceEditor.pieceId ? 'knowledge.pieceEditTitle' : 'knowledge.pieceAddTitle')} testId="knowledge-piece-editor" onClose={() => setPieceEditor(undefined)} pending={submitting}>
    <form noValidate className="ui-form knowledge-form" onSubmit={event => { event.preventDefault(); void savePiece(); }}><fieldset disabled={submitting}>
      {pieceEditor.anchor && <p className="knowledge-anchor"><Icon name="file" size={12} /><code>{revealControls(anchorLabel(pieceEditor.anchor))}</code></p>}
      <Field label={t('knowledge.text')}>{id => <textarea id={id} value={pieceEditor.text} data-testid="knowledge-piece-input" rows={6} spellCheck={false} onChange={event => setPieceEditor({ ...pieceEditor, text: event.target.value })} />}</Field>
    </fieldset>{formAlert('knowledge-piece-error')}
    <FormActions><button type="button" className="btn" disabled={submitting} onClick={() => setPieceEditor(undefined)}>{t('tools.cancel')}</button>
      <button type="submit" className="btn primary" data-testid="knowledge-piece-save" disabled={submitting || disabled}>{t('tools.save')}</button></FormActions></form>
  </InPageDialog>;

  return <div className="tools-scroll-body knowledge-view" data-testid="knowledge-view">
    {proposals.length > 0 && <ViewSection sectionKey="tools-knowledge-candidates" title={t('knowledge.candidates')} status={proposals.length} bodyClassName="ui-flush" data-testid="knowledge-candidates">
      <DataTable columns="minmax(0, 1fr) 56px" label={t('knowledge.candidates')} head={[ko ? '제안' : 'Proposal', '']}>
        {proposals.map(({ item, piece }) => <Row key={piece.id} testId={`knowledge-candidate-${piece.id}`}>
          <Cell wrap><span className="knowledge-name"><KnowledgeDot color={item.color} /><strong>{revealControls(item.name)}</strong>
            {piece.anchor && <span className="ui-mono ui-muted knowledge-anchor-inline">{revealControls(anchorLabel(piece.anchor))}</span>}</span>
            <PieceText text={piece.text} /></Cell>
          <Actions>
            <IconButton icon="check" label={t('knowledge.accept')} disabled={locked} testId={`knowledge-accept-${piece.id}`} onClick={() => { void act({ action: 'acceptPiece', id: item.id, revision: item.revision, pieceId: piece.id }); }} />
            <IconButton icon="x" tone="danger" label={t('knowledge.reject')} disabled={locked} testId={`knowledge-reject-${piece.id}`} onClick={() => { void act({ action: 'deletePiece', id: item.id, revision: item.revision, pieceId: piece.id }); }} />
          </Actions>
        </Row>)}
      </DataTable>
    </ViewSection>}
    <ViewSection sectionKey="tools-knowledge" title={t('tools.knowledge')} status={usable.length} bodyClassName="ui-flush"
      actions={<><button type="button" className="btn small" data-testid="knowledge-create" data-tools-dialog-fallback disabled={locked}
        onClick={() => { closeForms(); setEditor({ name: '', scope: 'project', use: 'original', text: '' }); }}><Icon name="plus" size={12} />{t('knowledge.create')}</button>
        <SectionRefresh onClick={() => { void refresh(); }} disabled={disabled || knowledge.pending} testId="knowledge-refresh" /></>}>
      <Toolbar><SearchBox value={query} onChange={setQuery} label={t('knowledge.search')} testId="knowledge-search" /></Toolbar>
      {message && <p className="ui-note" role="alert" data-testid="knowledge-error">{revealControls(message)}</p>}
      {knowledge.hidden > 0 && <p className="ui-note ui-muted" role="status" data-testid="knowledge-hidden">{t('knowledge.hidden', { count: knowledge.hidden })}</p>}
      {editor && <InPageDialog title={t(editor.id ? 'knowledge.editTitle' : editor.adding ? 'knowledge.addTitle' : 'knowledge.createTitle')} testId="knowledge-editor" onClose={() => setEditor(undefined)} pending={submitting}>
        <form noValidate className="ui-form knowledge-form" onSubmit={event => { event.preventDefault(); void save(); }}><fieldset disabled={submitting}>
          <Field label={t('knowledge.name')}>{id => <input id={id} ref={nameField} value={editor.name} data-testid="knowledge-name" maxLength={40} autoComplete="off" onChange={event => setEditor({ ...editor, name: event.target.value })} />}</Field>
          {nameMatches.length > 0 && <ul className="knowledge-matches" aria-label={t('tools.knowledge')} data-testid="knowledge-name-matches">{nameMatches.map(item => <li key={item.id}>
            <button type="button" data-testid={`knowledge-name-match-${item.id}`} onClick={() => setEditor({ ...editor, name: item.name })}><KnowledgeDot color={item.color} /><span>{revealControls(item.name)}</span>
              <span className="ui-muted">{t('knowledge.pieces', { count: savedPieces(item).length })}</span></button>
          </li>)}</ul>}
          {existing
            ? <p className="knowledge-existing" data-testid="knowledge-existing"><KnowledgeDot color={existing.color} /><strong>{revealControls(existing.name)}</strong><span>{t('knowledge.pieces', { count: savedPieces(existing).length })}</span></p>
            : <>
              <ColorField t={t} ko={ko} value={color} onChange={value => setEditor({ ...editor, color: value })} />
              <Field label={t('knowledge.scope')}>{id => <select id={id} data-testid="knowledge-scope" value={editor.scope} onChange={event => setEditor({ ...editor, scope: event.target.value as KnowledgeScope })}>
                <option value="project">{t('knowledge.scopeOption.project')}</option><option value="user">{t('knowledge.scopeOption.user')}</option></select>}</Field>
            </>}
          {editor.id
            ? <Field label={t('knowledge.use')}>{id => <select id={id} data-testid="knowledge-use" value={editor.use} onChange={event => setEditor({ ...editor, use: event.target.value as KnowledgeUse })}>
              <option value="original">{t('knowledge.use.original')}</option><option value="optimized">{t('knowledge.use.optimized')}</option></select>}</Field>
            : <>
              {editor.anchor && <p className="knowledge-anchor" data-testid="knowledge-anchor"><Icon name="file" size={12} /><code>{revealControls(anchorLabel(editor.anchor))}</code></p>}
              {/* The lines of an anchored piece stay as the editor sent them, so the anchor stays true. */}
              <Field label={t('knowledge.text')}>{id => <textarea id={id} value={editor.text} data-testid="knowledge-text" rows={6} spellCheck={false} readOnly={!!editor.anchor} onChange={event => setEditor({ ...editor, text: event.target.value })} />}</Field>
            </>}
        </fieldset>{formAlert('knowledge-editor-error')}
        <FormActions><button type="button" className="btn" disabled={submitting} onClick={() => setEditor(undefined)}>{t('tools.cancel')}</button>
          <button type="submit" className="btn primary" data-testid="knowledge-save" disabled={submitting || disabled}>{t('tools.save')}</button></FormActions></form>
      </InPageDialog>}
      <DataTable columns={COLUMNS} label={t('tools.knowledge')} testId="knowledge-list" empty={!listed.length}
        head={[ko ? '이름' : 'Name', <span key="default" title={t('knowledge.defaultHint')}>{t('knowledge.default')}</span>, ko ? '조각' : 'Pieces', '']}>
        {paged.slice.map(item => <KnowledgeRow key={item.id} item={item} t={t} ko={ko} open={expanded.has(item.id)} onToggle={() => toggle(item.id)}
          locked={locked} busy={knowledge.optimizing === item.id || starting.has(item.id)} roleTitle={roleTitle} teamTitle={teamTitle} canInsert={!!onInsertText}
          onInsert={() => onInsertText?.(usedContent(item))}
          onAddPiece={() => { closeForms(); setEditor(undefined); toggle(item.id, true); setPieceEditor({ id: item.id, revision: item.revision, text: '' }); }}
          onEditPiece={piece => { closeForms(); setEditor(undefined); setPieceEditor({ id: item.id, revision: item.revision, pieceId: piece.id, text: piece.text, anchor: piece.anchor }); }}
          onDeletePiece={piece => { void act({ action: 'deletePiece', id: item.id, revision: item.revision, pieceId: piece.id }); }}
          onOptimize={() => { void optimize(item); }}
          onDefault={on => { void act({ action: 'setDefault', id: item.id, revision: item.revision, on }); }}
          onEdit={() => { closeForms(); setEditor({ id: item.id, revision: item.revision, name: item.name, color: item.color, scope: item.scope, use: item.use, text: '' }); }}
          onDelete={() => { closeForms(); setEditor(undefined); setDeleting(item); }}
          pieceForm={pieceForm(item)}
          confirm={deleting?.id === item.id && <InPageDialog title={t('knowledge.delete')} testId="knowledge-delete-confirm" onClose={() => setDeleting(undefined)} pending={submitting}>
            <div className="ui-form"><p>{t('knowledge.deleteQuestion', { name: deleting.name })}</p>{formError && <p className="ui-err" role="alert" data-testid="knowledge-delete-error">{revealControls(formError)}</p>}
            <FormActions><button type="button" className="btn" data-autofocus disabled={submitting} onClick={() => setDeleting(undefined)}>{t('tools.cancel')}</button>
              <button type="button" className="btn danger" data-testid="knowledge-delete-yes" disabled={locked || submitting} onClick={() => { void remove(); }}>{t('knowledge.delete')}</button></FormActions></div>
          </InPageDialog>} />)}
      </DataTable>
      <Pager paged={paged} testId="knowledge-pager" />
    </ViewSection>
  </div>;
}

/**
 * The color of a knowledge: 16 swatches drawn in the page (no system color dialog, whose clicks land off target inside Visual Studio), then the
 * custom color when one is chosen, then "+" which opens a #RRGGBB field for any other color.
 */
function ColorField({ t, ko, value, onChange }: { t: ToolsTranslate; ko: boolean; value: string; onChange: (value: string) => void }) {
  const current = value.toLowerCase();
  const preset = (KNOWLEDGE_SWATCHES as readonly string[]).includes(current);
  const [custom, setCustom] = useState(!preset && !!value);
  const [code, setCode] = useState(preset ? '' : value);
  const codeId = useId();
  const swatch = (color: string, testId: string, label: string) => <button type="button" role="radio" key={testId} className="knowledge-swatch" aria-checked={current === color.toLowerCase()} aria-label={label} title={label}
    data-testid={testId} onClick={() => { onChange(color); }}><span style={{ background: color }} /></button>;
  return <div className="ui-field knowledge-color-field" role="group" aria-labelledby={`${codeId}-label`}>
    <span id={`${codeId}-label`} className="knowledge-color-label">{t('knowledge.color')}</span>
    <div className="knowledge-swatches" role="radiogroup" aria-label={t('knowledge.color')}>
      {KNOWLEDGE_SWATCHES.map(color => swatch(color, `knowledge-swatch-${color.slice(1)}`, color))}
      {!preset && isKnowledgeColor(value) && swatch(value, 'knowledge-swatch-custom', value)}
      <button type="button" className="knowledge-swatch knowledge-swatch-add" aria-expanded={custom} aria-controls={codeId} aria-label={ko ? '사용자 색' : 'Custom color'} title={ko ? '사용자 색' : 'Custom color'}
        data-testid="knowledge-swatch-add" onClick={() => setCustom(open => !open)}><Icon name="plus" size={13} /></button>
    </div>
    {custom && <div className="knowledge-color-code" id={codeId}>
      <KnowledgeDot color={isKnowledgeColor(code) ? code : 'transparent'} />
      <input value={code} maxLength={7} spellCheck={false} placeholder={t('knowledge.colorCode')} aria-label={t('knowledge.colorCode')} data-testid="knowledge-color" className="ui-mono"
        onChange={event => { const next = event.target.value.trim(); setCode(next); if (isKnowledgeColor(next)) onChange(next); }} />
    </div>}
  </div>;
}

/** A piece's text exactly as written: preformatted plain text, never markup. */
function PieceText({ text }: { text: string }) {
  return <pre className="knowledge-text">{revealControls(text)}</pre>;
}

interface RowProps {
  item: KnowledgeItem; t: ToolsTranslate; ko: boolean; open: boolean; locked: boolean; busy: boolean; canInsert: boolean;
  roleTitle: (name: string) => string; teamTitle: (id: string) => string;
  onToggle: () => void; onInsert: () => void; onAddPiece: () => void; onEditPiece: (piece: KnowledgePiece) => void; onDeletePiece: (piece: KnowledgePiece) => void;
  onOptimize: () => void; onDefault: (on: boolean) => void; onEdit: () => void; onDelete: () => void; pieceForm: ReactNode; confirm: ReactNode;
}

/** One knowledge: its row (name, pieces, scope, actions) and, opened, its tools, optimized text and saved pieces. */
function KnowledgeRow({ item, t, ko, open, locked, busy, canInsert, roleTitle, teamTitle, onToggle, onInsert, onAddPiece, onEditPiece, onDeletePiece, onOptimize, onDefault, onEdit, onDelete, pieceForm, confirm }: RowProps) {
  const bodyId = useId();
  const saved = savedPieces(item); const use = knowledgeUse(item); const content = usedContent(item);
  return <div role="rowgroup" className="knowledge-group" data-testid={`knowledge-row-${item.id}`}>
    <Row className="knowledge-row">
      <Cell>
        <button type="button" className="knowledge-toggle" data-testid={`knowledge-toggle-${item.id}`} aria-expanded={open} aria-controls={bodyId} title={t(open ? 'section.collapse' : 'section.expand')} onClick={onToggle}>
          <Icon name={open ? 'chevronDown' : 'chevron'} size={12} /><span className="knowledge-tag" style={{ '--knowledge-color': item.color } as CSSProperties}><KnowledgeDot color={item.color} /><span>{revealControls(item.name)}</span></span>
        </button>
        <span className="knowledge-badges">
          <Badge tone={item.scope === 'project' ? 'accent' : 'neutral'} testId={`knowledge-scope-${item.id}`}>{t(`knowledge.scope.${item.scope}`)}</Badge>
          {use !== 'original' && <Badge tone="info" testId={`knowledge-use-${item.id}`}>{t(`knowledge.use.${use}`)}</Badge>}
          {item.roles.map(name => <Badge key={`role:${name}`} icon="user">{roleTitle(name)}</Badge>)}
          {item.teams.map(id => <Badge key={`team:${id}`} icon="users">{teamTitle(id)}</Badge>)}
        </span>
      </Cell>
      <Cell><span title={t('knowledge.defaultHint')}><Switch checked={item.default} label={`${item.name} ${t('knowledge.default')}`} showLabel={false} disabled={locked} testId={`knowledge-default-${item.id}`} onChange={onDefault} /></span></Cell>
      <Cell className="knowledge-count"><span data-testid={`knowledge-count-${item.id}`}>{saved.length}</span></Cell>
      <Actions>
        <IconButton icon="send" label={t('knowledge.insert')} testId={`knowledge-insert-${item.id}`} disabled={!canInsert || !content.trim() || content.length > INSERT_LIMIT} onClick={onInsert} />
        <IconButton icon="edit" label={t('knowledge.edit')} testId={`knowledge-edit-${item.id}`} disabled={locked} onClick={onEdit} />
        <IconButton icon="trash" tone="danger" label={t('knowledge.delete')} testId={`knowledge-delete-${item.id}`} disabled={locked} onClick={onDelete} />
      </Actions>
    </Row>
    {confirm}
    <div id={bodyId} className="knowledge-body" hidden={!open}>{open && <>
      <div className="knowledge-body-bar">
        <span className="ui-muted">{t('knowledge.pieces', { count: saved.length })}</span>
        <button type="button" className="btn small subtle" data-testid={`knowledge-optimize-${item.id}`} aria-busy={busy} disabled={locked || busy || !saved.length} onClick={onOptimize}>
          {busy ? <span className="spinner" aria-hidden="true" /> : <Icon name="zap" size={12} />}{t(busy ? 'knowledge.optimizing' : 'knowledge.optimize')}</button>
        <button type="button" className="btn small" data-testid={`knowledge-add-piece-${item.id}`} disabled={locked} onClick={onAddPiece}><Icon name="plus" size={12} />{t('knowledge.addPiece')}</button>
      </div>
      {pieceForm}
      {item.use === 'optimized' && item.optimized && <div className="knowledge-optimized" data-testid={`knowledge-optimized-${item.id}`}>
        <Badge tone={use === 'optimized' ? 'info' : 'warn'}>{t(`knowledge.use.${use === 'optimized' ? 'optimized' : 'stale'}`)}</Badge><PieceText text={item.optimized.text} /></div>}
      <ul className="knowledge-pieces" data-testid={`knowledge-pieces-${item.id}`} aria-label={ko ? '조각' : 'Pieces'}>{saved.map(piece => <li key={piece.id} className="knowledge-piece" data-testid={`knowledge-piece-${piece.id}`}>
        <div className="knowledge-piece-main">
          {piece.anchor && <span className="knowledge-anchor" title={anchorLabel(piece.anchor)}><Icon name="file" size={12} /><code>{revealControls(anchorLabel(piece.anchor))}</code>
            <Badge tone={piece.anchor.status === 'current' ? 'ok' : piece.anchor.status === 'missing' ? 'error' : 'warn'} testId={`knowledge-piece-status-${piece.id}`}>{t(`knowledge.anchor.${piece.anchor.status}`)}</Badge></span>}
          <PieceText text={piece.text} />
        </div>
        <span className="ui-actions">
          <IconButton icon="edit" label={t('knowledge.edit')} testId={`knowledge-piece-edit-${piece.id}`} disabled={locked} onClick={() => onEditPiece(piece)} />
          <IconButton icon="trash" tone="danger" label={t('knowledge.delete')} testId={`knowledge-piece-delete-${piece.id}`} disabled={locked} onClick={() => onDeletePiece(piece)} />
        </span>
      </li>)}</ul>
    </>}</div>
  </div>;
}
