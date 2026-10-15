import { isKnowledgeColor, normalizeKnowledgeText, originalContent } from '../knowledge';
import type { HostMessageType, HostToUi, KnowledgeAnchorStatus, KnowledgeCommand, KnowledgeDraft, KnowledgeItem, KnowledgePiece, KnowledgeScope, KnowledgeState, KnowledgeUse } from '../protocol';

/**
 * Dev-only stand-in for the host's knowledge store (docs/ui-protocol.md "Knowledge"), kept in memory: nothing is saved beyond the page.
 * It answers every `knowledge/command` with a full `knowledge/state` carrying the same request id, like the host:
 * - same-name create adds the piece to the existing knowledge; a piece whose normalized text (and anchor path) equals a saved piece of the
 *   same knowledge is not added twice (nor kept twice after an edit or an accept);
 * - every change bumps the revision of the knowledge it touched; a command naming an older revision is refused;
 * - an optimize first reports the knowledge running (`optimizing`, answering the command), then pushes the result after a short delay;
 *   the optimized text goes stale as soon as the pieces change;
 * - the selection belongs to the conversation (a resumed conversation keeps it, a new one starts empty).
 */
interface MockPiece { id: string; text: string; source: KnowledgePiece['source']; status: KnowledgePiece['status']; createdAt: string; anchor?: { path: string; startLine: number; endLine: number; status: KnowledgeAnchorStatus } }
interface MockKnowledge {
  id: string; name: string; color: string; scope: KnowledgeScope; use: KnowledgeUse; pieces: MockPiece[]; optimized?: { text: string; sourceHash: string; createdAt: string };
  roles: string[]; teams: string[]; createdAt: string; updatedAt: string; revision: number;
}
type Post = <K extends HostMessageType>(type: K, payload: HostToUi[K]) => void;
/** A `knowledge/command` as it reaches the host. */
type Envelope = { requestId: string; root: string } & KnowledgeCommand;

// eslint-disable-next-line no-control-regex
const CONTROL = /[\x00-\x1f\x7f]/;
const SLUG = /^[a-z0-9-]{2,40}$/;
const PIECE_LIMIT = 8000;
const PIECE_COUNT_LIMIT = 100;
const KNOWLEDGE_LIMIT = 100;
const COLORS = ['#4f8ff7', '#34b27b', '#f0a43a', '#e5584f', '#a06be0', '#2cb5c0', '#e0699f', '#8fbf3a'];

/** A small stable hash of the normalized text (the real host uses SHA-256; only equality matters here). */
function hashOf(text: string): string {
  let hash = 5381;
  for (const char of normalizeKnowledgeText(text)) hash = ((hash << 5) + hash + char.codePointAt(0)!) | 0;
  return (hash >>> 0).toString(16);
}

export function createMockKnowledge({ post, root, sessionId }: { post: Post; root: string; sessionId: () => string }) {
  let counter = 0;
  const nextId = (prefix: string) => `${prefix}-${++counter}`;
  const now = () => new Date().toISOString();
  const knowledge: MockKnowledge[] = [];
  const selections = new Map<string, string[]>();
  let optimizing: string | undefined;
  let optimizeMs = 900;
  let failNextOptimize: string | undefined;

  const selection = () => selections.get(sessionId()) ?? [];
  const viewPiece = (piece: MockPiece): KnowledgePiece => ({ id: piece.id, text: piece.text, source: piece.source, status: piece.status, createdAt: piece.createdAt, ...(piece.anchor ? { anchor: { ...piece.anchor } } : {}) });
  const contentOf = (item: MockKnowledge) => originalContent({ pieces: item.pieces.map(viewPiece) });
  const view = (item: MockKnowledge): KnowledgeItem => ({
    id: item.id, name: item.name, color: item.color, scope: item.scope, use: item.use, revision: item.revision, createdAt: item.createdAt, updatedAt: item.updatedAt,
    roles: [...item.roles], teams: [...item.teams], candidate: item.pieces.length > 0 && item.pieces.every(piece => piece.status === 'candidate'), pieces: item.pieces.map(viewPiece),
    ...(item.optimized ? { optimized: { text: item.optimized.text, createdAt: item.optimized.createdAt, stale: item.optimized.sourceHash !== hashOf(contentOf(item)) } } : {}),
  });
  const state = (requestId?: string, error?: string): KnowledgeState => ({
    ...(requestId ? { requestId } : {}), root, sessionId: sessionId(), selected: [...selection()], ...(optimizing ? { optimizing } : {}), items: knowledge.map(view), ...(error ? { error } : {}),
  });
  /** The answer to a command arrives a moment later, like a real round trip. */
  const reply = (requestId: string, error?: string) => { window.setTimeout(() => post('knowledge/state', state(requestId, error)), 30); };
  /** A state nobody asked for (another place changed the list). */
  const push = (error?: string) => { window.setTimeout(() => post('knowledge/state', state(undefined, error)), 0); };

  const touch = (item: MockKnowledge) => { item.revision++; item.updatedAt = now(); };
  const byName = (name: string) => knowledge.find(item => item.name.toLowerCase() === name.toLowerCase());
  const anchorOk = (anchor: unknown): anchor is { path: string; startLine: number; endLine: number } => {
    const value = anchor as { path?: unknown; startLine?: unknown; endLine?: unknown } | undefined;
    return !!value && typeof value.path === 'string' && value.path.length > 0 && value.path.length <= 1024 && !value.path.includes('\\') && Number.isInteger(value.startLine) && Number.isInteger(value.endLine)
      && (value.startLine as number) >= 1 && (value.endLine as number) >= (value.startLine as number) && (value.endLine as number) <= (value.startLine as number) + 199;
  };
  const sameKey = (a: MockPiece, b: MockPiece) => normalizeKnowledgeText(a.text) === normalizeKnowledgeText(b.text) && (a.anchor?.path ?? '') === (b.anchor?.path ?? '');
  const pieceCheck = (text: unknown, anchor?: unknown): string | undefined => {
    if (typeof text !== 'string' || !normalizeKnowledgeText(text) || text.length > PIECE_LIMIT) return 'The piece text is empty or too long.';
    if (anchor !== undefined && !anchorOk(anchor)) return 'The piece anchor is invalid.';
    return undefined;
  };
  /** Adds the piece unless a saved piece of the same knowledge already has its text and anchor path; true when something was added. */
  const addPiece = (item: MockKnowledge, text: string, anchor: { path: string; startLine: number; endLine: number } | undefined, source: MockPiece['source'], status: MockPiece['status']): boolean => {
    const piece: MockPiece = { id: nextId('pc'), text: normalizeKnowledgeText(text), source, status, createdAt: now(), ...(anchor ? { anchor: { ...anchor, status: 'current' as const } } : {}) };
    if (item.pieces.some(other => other.status === 'saved' && sameKey(other, piece))) return false;
    item.pieces.push(piece);
    return true;
  };
  /** Keeps the first of the saved pieces that are equal (after an edit or an accept). */
  const dedupe = (item: MockKnowledge) => { item.pieces = item.pieces.filter((piece, index) => piece.status !== 'saved' || item.pieces.findIndex(other => other.status === 'saved' && sameKey(other, piece)) === index); };
  const create = (name: string, color: string, scope: KnowledgeScope): MockKnowledge => {
    const item: MockKnowledge = { id: nextId('kn'), name, color, scope, use: 'original', pieces: [], roles: [], teams: [], createdAt: now(), updatedAt: now(), revision: 1 };
    knowledge.push(item);
    return item;
  };

  const handle = (payload: Envelope): void => {
    const { requestId } = payload;
    const fail = (message: string) => reply(requestId, `${message} (mock)`);
    if (payload.root !== root) { fail('The workspace changed.'); return; }
    /** The knowledge a command names, at the revision the page saw; undefined after an error answer. */
    const target = (id: string, revision: number): MockKnowledge | undefined => {
      const item = knowledge.find(entry => entry.id === id);
      if (!item) { fail('The knowledge was not found.'); return undefined; }
      if (item.revision !== revision) { fail('Revision conflict: the knowledge changed elsewhere.'); return undefined; }
      return item;
    };
    const fields = (input: { name?: unknown; color?: unknown; scope?: unknown }) => {
      const name = typeof input.name === 'string' ? input.name.trim() : '';
      if (name.length < 1 || name.length > 40 || CONTROL.test(name)) return 'The name is invalid.';
      if (!isKnowledgeColor(input.color)) return 'The color is invalid.';
      if (input.scope !== 'project' && input.scope !== 'personal') return 'The scope is invalid.';
      return undefined;
    };
    switch (payload.action) {
      case 'refresh': reply(requestId); return;
      case 'create': {
        const problem = fields(payload.knowledge ?? {}) ?? (payload.piece ? pieceCheck(payload.piece.text, payload.piece.anchor) : undefined);
        if (problem) { fail(problem); return; }
        const name = payload.knowledge.name.trim();
        // The same tag is the same knowledge: the piece goes into the one that exists.
        const existing = byName(name);
        if (!existing && knowledge.length >= KNOWLEDGE_LIMIT) { fail('There are too many knowledge.'); return; }
        const item = existing ?? create(name, payload.knowledge.color.toLowerCase(), payload.knowledge.scope);
        if (existing && payload.piece && existing.pieces.length >= PIECE_COUNT_LIMIT) { fail('There are too many pieces.'); return; }
        const changed = payload.piece ? addPiece(item, payload.piece.text, payload.piece.anchor, payload.piece.anchor ? 'editor' : 'user', 'saved') : false;
        if (existing && changed) touch(existing);
        reply(requestId); return;
      }
      case 'update': {
        const item = target(payload.id, payload.revision); if (!item) return;
        const problem = fields(payload.knowledge ?? {}) ?? (payload.knowledge.use === 'original' || payload.knowledge.use === 'optimized' ? undefined : 'The use is invalid.');
        if (problem) { fail(problem); return; }
        const name = payload.knowledge.name.trim(); const other = byName(name);
        if (other && other !== item) { fail('A knowledge with this name exists.'); return; }
        Object.assign(item, { name, color: payload.knowledge.color.toLowerCase(), scope: payload.knowledge.scope, use: payload.knowledge.use });
        touch(item); reply(requestId); return;
      }
      case 'delete': {
        const item = target(payload.id, payload.revision); if (!item) return;
        knowledge.splice(knowledge.indexOf(item), 1);
        for (const [session, ids] of selections) selections.set(session, ids.filter(id => id !== item.id));
        reply(requestId); return;
      }
      case 'addPiece': {
        const item = target(payload.id, payload.revision); if (!item) return;
        const problem = pieceCheck(payload.piece?.text, payload.piece?.anchor) ?? (item.pieces.length >= PIECE_COUNT_LIMIT ? 'There are too many pieces.' : undefined);
        if (problem) { fail(problem); return; }
        if (addPiece(item, payload.piece.text, payload.piece.anchor, payload.piece.anchor ? 'editor' : 'user', 'saved')) touch(item);
        reply(requestId); return;
      }
      case 'updatePiece': {
        const item = target(payload.id, payload.revision); if (!item) return;
        const piece = item.pieces.find(entry => entry.id === payload.pieceId);
        if (!piece) { fail('The piece was not found.'); return; }
        const problem = pieceCheck(payload.text); if (problem) { fail(problem); return; }
        piece.text = normalizeKnowledgeText(payload.text);
        // An anchored piece keeps its path and lines.
        if (piece.status === 'saved') dedupe(item);
        touch(item); reply(requestId); return;
      }
      case 'deletePiece': {
        const item = target(payload.id, payload.revision); if (!item) return;
        const piece = item.pieces.find(entry => entry.id === payload.pieceId);
        if (!piece) { fail('The piece was not found.'); return; }
        item.pieces.splice(item.pieces.indexOf(piece), 1);
        // A knowledge that was only a proposal disappears with its last piece.
        if (piece.status === 'candidate' && !item.pieces.length && !item.optimized) knowledge.splice(knowledge.indexOf(item), 1); else touch(item);
        reply(requestId); return;
      }
      case 'acceptPiece': {
        const item = target(payload.id, payload.revision); if (!item) return;
        const piece = item.pieces.find(entry => entry.id === payload.pieceId && entry.status === 'candidate');
        if (!piece) { fail('The proposal was not found.'); return; }
        piece.status = 'saved';
        // A proposal equal to a saved piece is not kept twice: the saved one stays.
        item.pieces = item.pieces.filter(other => other === piece ? !item.pieces.some(saved => saved !== piece && saved.status === 'saved' && sameKey(saved, piece)) : true);
        touch(item); reply(requestId); return;
      }
      case 'optimize': {
        const item = target(payload.id, payload.revision); if (!item) return;
        if (optimizing) { fail('Another optimize is running.'); return; }
        if (!item.pieces.some(piece => piece.status === 'saved')) { fail('There is nothing to optimize.'); return; }
        optimizing = item.id;
        reply(requestId);
        const problem = failNextOptimize; failNextOptimize = undefined;
        window.setTimeout(() => {
          optimizing = undefined;
          if (problem) { push(`${problem} (mock)`); return; }
          if (!knowledge.includes(item)) { push(); return; }
          const original = contentOf(item);
          item.optimized = { text: `Optimized (mock): ${original.replace(/\s+/g, ' ').trim()}`.slice(0, 16000), sourceHash: hashOf(original), createdAt: now() };
          touch(item); push();
        }, optimizeMs);
        return;
      }
      case 'assign': {
        if (payload.kind !== 'role' && payload.kind !== 'team') { fail('The kind is invalid.'); return; }
        if (typeof payload.name !== 'string' || !SLUG.test(payload.name) || !Array.isArray(payload.ids)) { fail('The name is invalid.'); return; }
        for (const item of knowledge) {
          const list = payload.kind === 'role' ? item.roles : item.teams; const has = list.includes(payload.name); const want = payload.ids.includes(item.id);
          if (has === want) continue;
          if (want) list.push(payload.name); else list.splice(list.indexOf(payload.name), 1);
          touch(item);
        }
        reply(requestId); return;
      }
      case 'select': {
        if (!Array.isArray(payload.ids) || payload.ids.some(id => typeof id !== 'string')) { fail('The selection is invalid.'); return; }
        selections.set(sessionId(), [...new Set(payload.ids)].filter(id => knowledge.some(item => item.id === id)));
        reply(requestId); return;
      }
      default: fail('Unknown knowledge action.');
    }
  };

  // ---- Seed: a few knowledge that show every state the page can render ----
  const seed = (name: string, color: string, scope: KnowledgeScope, use: KnowledgeUse, pieces: Array<{ text: string; status?: MockPiece['status']; source?: MockPiece['source']; anchor?: MockPiece['anchor'] }>) => {
    const item = create(name, color, scope); item.use = use;
    for (const piece of pieces) item.pieces.push({ id: nextId('pc'), text: normalizeKnowledgeText(piece.text), source: piece.source ?? 'user', status: piece.status ?? 'saved', createdAt: now(), ...(piece.anchor ? { anchor: piece.anchor } : {}) });
    return item;
  };
  // ?knowledge=empty starts with no knowledge; ?knowledge=many adds enough rows to page the table.
  const knowledgeQuery = new URLSearchParams(window.location.search).get('knowledge');
  if (knowledgeQuery === 'many') for (let i = 1; i <= 24; i++) seed(`Note ${String(i).padStart(2, '0')}`, COLORS[i % COLORS.length]!, i % 3 ? 'project' : 'personal', 'original', [{ text: `Piece ${i}.` }]);
  if (knowledgeQuery !== 'empty') {
  const conventions = seed('Coding conventions', COLORS[0]!, 'project', 'original', [
    { text: 'Prefer small modules.\nKeep public interfaces narrow.' },
    { text: 'public sealed class KnowledgeStore\n{\n    private readonly string _root;\n}', source: 'editor', anchor: { path: 'src/Pomp.Core/Features/KnowledgeStore.cs', startLine: 40, endLine: 43, status: 'current' } },
    { text: 'Name files after the type they hold.', status: 'candidate', source: 'agent' },
  ]);
  conventions.roles = ['architect'];
  const checklist = seed('Review checklist', COLORS[1]!, 'project', 'optimized', [
    { text: 'Check error paths first.', source: 'user' },
    { text: 'return Task.CompletedTask;', source: 'editor', anchor: { path: 'src/Pomp.Core/Hosting/PompSession.Knowledge.cs', startLine: 61, endLine: 61, status: 'moved' } },
    { text: 'catch (Exception ex) { error = ex.Message; }', source: 'editor', anchor: { path: 'src/Pomp.Core/Hosting/PompSession.Knowledge.cs', startLine: 58, endLine: 58, status: 'changed' } },
  ]);
  checklist.optimized = { text: 'Review: check error paths first; every handler answers once.', sourceHash: hashOf(contentOf(checklist)), createdAt: now() };
  checklist.teams = ['feature-dev'];
  seed('Personal notes', COLORS[2]!, 'personal', 'original', [
    { text: 'Ask before renaming public types.' },
    { text: 'var removed = true;', source: 'editor', anchor: { path: 'src/Pomp.Core/Features/Removed.cs', startLine: 3, endLine: 3, status: 'missing' } },
  ]);
  seed('API quirks', COLORS[3]!, 'project', 'original', [{ text: 'The list endpoint pages by cursor, not by offset.', status: 'candidate', source: 'agent' }]);
  }

  return {
    handle,
    /** The state the host sends with the ui/ready snapshot and whenever the conversation changes. */
    snapshot: () => push(),
    /** The editor's "Add to Knowledge ▸ New": lines of a file on their way into a new knowledge. */
    draft: (draft: Partial<KnowledgeDraft> = {}) => post('knowledge/draft', {
      root, path: 'src/Pomp.Core/Hosting/PompSession.Knowledge.cs', startLine: 61, endLine: 63, text: 'return Task.CompletedTask;\n    }\n', ...draft,
    }),
    /** What an agent's `propose_knowledge` does: a candidate piece in the knowledge with that name, or a new candidate knowledge. */
    propose: (tag: string, text: string) => {
      const existing = byName(tag);
      // Personal knowledge is never a target of an agent.
      if (existing?.scope === 'personal') return;
      const item = existing ?? create(tag, COLORS[knowledge.length % COLORS.length]!, 'project');
      if (addPiece(item, text, undefined, 'agent', 'candidate') && existing) touch(existing);
      push();
    },
    /** Test controls: how long an optimize runs, and an optimize that fails with this message. */
    setOptimizeDelay: (ms: number) => { optimizeMs = ms; },
    failNextOptimize: (message: string) => { failNextOptimize = message; },
  };
}
