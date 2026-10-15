import { advisorSlug, validAdvisorDocument } from '../advisors';
import type { AdvisorDocument, AdvisorNote, AdvisorScope, AdvisorsCommand, HostMessageType, HostToUi } from '../protocol';

/** The compact transcript shape the engine's /advisor dump prints. */
const HISTORY = `## user

Session update
user: <vs_context>
solution: Demo (C:\\Repo\\Demo.sln)
active_file: C:\\Repo\\src\\Parser.cs [CSharp]
${Array.from({ length: 40 }, (_, index) => `${index + 1}: line ${index + 1} of Parser.cs`).join('\n')}
</vs_context>
Review the change to \`Parser.cs\` before the next turn.

## assistant

The new loop skips the last token when the input ends without a newline. Check \`Parser.ReadAll\` before relying on it.
`;

/** Dev-only in-memory advisor files; never reads/writes disk or changes the conversation model. */
export function createMockAdvisors({ post, root, sessionId, model }: {
  post: <K extends HostMessageType>(type: K, payload: HostToUi[K]) => void;
  root: string; sessionId: () => string; model: () => string | undefined;
}) {
  const query = new URLSearchParams(window.location.search); const mode = query.get('advisors') ?? 'default';
  let enabled = mode === 'populated' || mode === 'many'; let revision = 1; let failConflict = mode === 'conflict';
  // This conversation's own on/off per advisor (slug → on), over the files' enabled; and the notes advisors gave in it.
  const choices = new Map<string, boolean>();
  const minutesAgo = (minutes: number) => new Date(Date.now() - minutes * 60000).toISOString();
  const notes: AdvisorNote[] = mode === 'populated' ? [
    { advisor: 'review', severity: 'concern', note: 'The new loop skips the last token when the input ends without a newline. Check `Parser.ReadAll` before relying on it.', at: minutesAgo(42) },
    { advisor: 'review', severity: 'nit', note: 'Rename `tmp` in `Parser.Next` to say what it holds.', at: minutesAgo(17) },
    { advisor: 'review', severity: 'blocker', note: 'The edit removes the null check on `input`; `Parse(null)` now throws a NullReferenceException.\n\n```csharp\nif (input == null) throw new ArgumentNullException(nameof(input));\n```', at: minutesAgo(3) },
  ] : [];
  const isOn = (entry: { name: string; enabled: boolean }) => choices.get(advisorSlug(entry.name)) ?? entry.enabled;
  // ?advisors=many fills both files with 60 advisors each (scale check of the two paged tables; there is no count limit).
  const many = (prefix: string): AdvisorDocument['advisors'] => mode === 'many' ? Array.from({ length: 60 }, (_, index) => ({ name: `${prefix}-${index + 1}`, enabled: index % 7 !== 6, reviewMode: 'turn' as const })) : [];
  const documents: Record<AdvisorScope, AdvisorDocument> = {
    project: { advisors: mode === 'populated' ? [{ name: 'review', enabled: true, model: model(), reviewMode: 'agent-end', reviewInterval: 2, tools: ['read', 'grep'], instructions: 'Review changed code.' }] : many('project') },
    user: { advisors: many('user') },
  };
  const paths = { project: `${root}\\WATCHDOG.yml`, user: 'C:\\Users\\dev\\.pomp\\WATCHDOG.yml' };
  const hash = (scope: AdvisorScope) => `${scope}-${revision}`;
  const exists = (scope: AdvisorScope) => documents[scope].advisors.length > 0 || !!documents[scope].instructions || documents[scope].maxNotesPerUpdate !== undefined;
  const snapshot = () => {
    const roster = new Map<string, HostToUi['advisors/state']['roster'][number]>();
    for (const scope of ['user', 'project'] as const) for (const entry of documents[scope].advisors) roster.set(advisorSlug(entry.name), { name: entry.name, scope, enabled: entry.enabled !== false, ...(entry.model ? { model: entry.model } : {}) });
    const implicitDefault = roster.size === 0 && mode !== 'empty';
    const usage = (index: number) => ({ contextTokens: 18400 + index * 9100, contextWindow: 200000, cost: 0.0213 + index * 0.0087 });
    const running = enabled ? implicitDefault ? [{ name: 'default', model: model(), status: model() ? 'active' : 'no-model', ...usage(0) }] : [...roster.values()].map((entry, index) => isOn(entry) ? { name: entry.name, model: entry.model || model(), status: entry.model || model() ? 'active' : 'no-model', ...usage(index) } : { name: entry.name, status: 'paused' }) : [];
    post('advisors/state', { supported: mode !== 'unsupported', sessionId: sessionId(), enabled, status: !enabled ? 'off' : running.some(entry => entry.status === 'active') ? 'active' : 'no-model', advisors: running, files: (['project', 'user'] as const).filter(exists).map(scope => ({ scope, path: paths[scope] })), roster: [...roster.values()], implicitDefault, updatedUtc: new Date().toISOString() });
  };
  const handle = (command: AdvisorsCommand) => {
    const { requestId, action } = command; const scope = command.scope ?? 'project';
    if (mode === 'timeout') return;
    const error = (message: string) => post('advisors/reply', { requestId, ok: false, error: message });
    if (mode === 'error' || mode === 'unsupported') { error('Advisor request failed.'); return; }
    if (action === 'load') { post('advisors/reply', { requestId, ok: true, scope, path: paths[scope], exists: exists(scope), hash: hash(scope), document: structuredClone(documents[scope]), warnings: mode === 'warnings' ? ['Invalid advisor entry.'] : [] }); return; }
    if (action === 'save') {
      if (failConflict || command.baseHash !== hash(scope)) { failConflict = false; revision++; error('Advisor file conflict.'); return; }
      if (!command.document || !validAdvisorDocument(command.document)) { error('Invalid advisor settings.'); return; }
      documents[scope] = structuredClone(command.document); revision++;
      post('advisors/reply', { requestId, ok: true, scope, path: paths[scope], hash: hash(scope), applied: mode !== 'not-applied' }); snapshot(); return;
    }
    if (action === 'history') { post('advisors/reply', { requestId, ok: true, history: enabled ? HISTORY : null, truncated: false, notes: structuredClone(notes) }); return; }
    if (action === 'openHistory') { if (enabled) post('advisors/reply', { requestId, ok: true }); else error('This advisor has no transcript.'); return; }
    if (action === 'select' && command.name) {
      const roster = [...(['user', 'project'] as const).flatMap(scope => documents[scope].advisors.map(entry => ({ name: entry.name, enabled: entry.enabled !== false })))];
      if (!roster.length) enabled = command.on === true;
      else {
        choices.set(advisorSlug(command.name), command.on === true);
        if (command.on && !enabled) for (const other of roster) if (advisorSlug(other.name) !== advisorSlug(command.name)) choices.set(advisorSlug(other.name), false);
        enabled = roster.some(isOn);
      }
      snapshot(); post('advisors/reply', { requestId, ok: true });
      if (command.on) { const note: AdvisorNote = { advisor: command.name, severity: 'concern', note: `${command.name}: \`Parser.ReadAll\` drops the last token.`, at: new Date().toISOString() }; notes.push(note); post('chat/advisorNotes', { notes: [note] }); }
      return;
    }
    if (action === 'enable' || action === 'disable') { enabled = action === 'enable'; snapshot(); post('advisors/reply', { requestId, ok: true }); return; }
    if (action === 'open') { if (!(Object.keys(paths) as AdvisorScope[]).some(scope => exists(scope) && paths[scope] === command.path)) error('Advisor file not found.'); else post('advisors/reply', { requestId, ok: true, path: command.path }); return; }
    snapshot(); post('advisors/reply', { requestId, ok: true });
  };
  return { snapshot, handle };
}
