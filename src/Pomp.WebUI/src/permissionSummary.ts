/** A display-only preview; omp's model-written task/context text is not a structured or trusted record. */
export interface TaskApprovalSummary {
  agents?: string;
  name?: string;
  taskPreview?: string;
  moreCount?: number;
  reasonLines: string[];
  /** Agent or name lines disagree (model-written text can contain header-looking lines): the card says so and shows the full request. */
  ambiguous?: boolean;
}

const MORE_TASKS = /^\+(\d+) more tasks?$/;
const PREVIEW_CHARS = 300;

function isTaskBoundary(line: string): boolean {
  return line === 'Context:' || line.startsWith('Name: ') || line.startsWith('Agent: ')
    || line.startsWith('Batch agents: ') || MORE_TASKS.test(line);
}

function taskPreview(lines: string[], start: number): string | undefined {
  const preview: string[] = [];
  let clipped = false;
  let editorContext = false;
  for (let index = start; index < lines.length; index++) {
    const line = lines[index];
    if (isTaskBoundary(line)) break;
    // POMP's own editor block and routing comments are not the request: preview what the model asked the agent to do.
    if (line.trim() === '<vs_context>') { editorContext = true; continue; }
    if (editorContext) { if (line.trim() === '</vs_context>') editorContext = false; continue; }
    if (!line.trim() || /^\s*<!--.*-->\s*$/.test(line)) continue;
    if (preview.length === 3) { clipped = true; break; }
    preview.push(line.trim());
  }
  const text = preview.join('\n');
  if (!text) return undefined;
  if (text.length > PREVIEW_CHARS || (clipped && text.length === PREVIEW_CHARS)) {
    // Do not leave half a surrogate pair at the clipping boundary.
    return text.slice(0, PREVIEW_CHARS).replace(/[\uD800-\uDBFF]$/, '');
  }
  return text;
}

/**
 * Best-effort parse of omp's plain-text task approval. Model-written task/context text can contain header-looking lines, so every
 * Agent/Batch agents/Name value is collected: when they disagree the summary is marked ambiguous (the dialog then shows all of them
 * and opens the full request). Reason and provider-safety lines are always kept.
 */
export function parseTaskApproval(message: unknown): TaskApprovalSummary | null {
  if (typeof message !== 'string' || !message) return null;
  const lines = message.split(/\r?\n/);
  const agentValues: string[] = [];
  const batchValues: string[] = [];
  const nameValues: string[] = [];
  let firstTask = -1;
  let moreCount: number | undefined;
  const reasonLines: string[] = [];
  let safetySection = false;

  for (let index = 0; index < lines.length; index++) {
    const line = lines[index];
    if (line.startsWith('Agent: ')) agentValues.push(line.slice('Agent: '.length).trim());
    if (line.startsWith('Name: ')) nameValues.push(line.slice('Name: '.length).trim());
    if (line.startsWith('Batch agents: ')) batchValues.push(line.slice('Batch agents: '.length).trim());
    if (line === 'Task:' && firstTask === -1) firstTask = index;
    const more = MORE_TASKS.exec(line);
    if (more) {
      const count = Number(more[1]);
      moreCount = Number.isSafeInteger(count) && count > 0 ? count : undefined;
    }
    if (line.startsWith('Provider safety checks:')) safetySection = true;
    // omp appends provider safety checks at the end: never hide their explanatory lines.
    if (line.startsWith('Reason: ') || safetySection) reasonLines.push(line);
  }

  const distinct = (values: string[]) => [...new Set(values.filter(Boolean))];
  const agentList = distinct(batchValues.length ? batchValues : agentValues);
  const ambiguous = distinct(batchValues).length > 1 || distinct(agentValues).length > 1 || distinct(nameValues).length > 1;
  const agents = agentList.join(' / ');
  const name = distinct(nameValues).join(' / ');
  const preview = firstTask === -1 ? undefined : taskPreview(lines, firstTask + 1);
  if (!agents && !name && !preview && !moreCount) return null;
  return { agents: agents || undefined, name: name || undefined, taskPreview: preview, moreCount, reasonLines, ambiguous: ambiguous || undefined };
}
