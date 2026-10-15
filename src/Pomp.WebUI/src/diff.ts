/** Unified diff parser producing display rows for the Diff view. */

export type DiffRow =
  | { kind: 'file'; text: string }
  | { kind: 'hunk'; text: string }
  | { kind: 'add'; oldNo?: number; newNo?: number; text: string }
  | { kind: 'del'; oldNo?: number; newNo?: number; text: string }
  | { kind: 'ctx'; oldNo?: number; newNo?: number; text: string }
  | { kind: 'meta'; text: string };

export interface ParsedDiff {
  rows: DiffRow[];
  added: number;
  removed: number;
  /** Width (in digits) needed by the line-number gutter. */
  digits: number;
}

const HUNK = /^@@ -(\d+)(?:,(\d+))? \+(\d+)(?:,(\d+))? @@/;
const HEADER = /^(diff |index |--- |\+\+\+ |new file mode|deleted file mode|old mode|new mode|similarity index|rename |copy |Binary files )/;

/**
 * Parses a unified diff (git style or bare hunks). Hunk line counts decide where a hunk ends, so a deleted line that
 * happens to start with "-- " is not mistaken for a file header. Malformed input degrades to unnumbered rows.
 */
export function parseUnifiedDiff(diff: string): ParsedDiff {
  const lines = String(diff ?? '').replace(/\r\n?/g, '\n').split('\n');
  if (lines.length > 0 && lines[lines.length - 1] === '') lines.pop();

  const rows: DiffRow[] = [];
  let added = 0;
  let removed = 0;
  let maxNo = 0;
  let oldNo = 0;
  let newNo = 0;
  let oldLeft = 0;
  let newLeft = 0;
  let sawHunk = false;

  for (const line of lines) {
    const hunk = HUNK.exec(line);
    // A line starting with "@@" can only be a hunk header (content lines start with space, + or -).
    if (hunk) {
      oldNo = Number(hunk[1]);
      oldLeft = hunk[2] === undefined ? 1 : Number(hunk[2]);
      newNo = Number(hunk[3]);
      newLeft = hunk[4] === undefined ? 1 : Number(hunk[4]);
      rows.push({ kind: 'hunk', text: line });
      sawHunk = true;
      continue;
    }

    const inHunk = oldLeft > 0 || newLeft > 0;
    if (!inHunk && HEADER.test(line)) {
      rows.push({ kind: 'file', text: line });
      continue;
    }
    if (line.startsWith('\\')) {
      rows.push({ kind: 'meta', text: line });
      continue;
    }

    const sign = line[0];
    const text = line.slice(1);
    if (sign === '+') {
      added++;
      rows.push({ kind: 'add', newNo: sawHunk ? newNo : undefined, text });
      if (sawHunk) maxNo = Math.max(maxNo, newNo++);
      newLeft--;
    } else if (sign === '-') {
      removed++;
      rows.push({ kind: 'del', oldNo: sawHunk ? oldNo : undefined, text });
      if (sawHunk) maxNo = Math.max(maxNo, oldNo++);
      oldLeft--;
    } else if (sign === ' ' || line === '') {
      // Some producers strip the trailing space of blank context lines.
      rows.push({ kind: 'ctx', oldNo: sawHunk ? oldNo : undefined, newNo: sawHunk ? newNo : undefined, text });
      if (sawHunk) maxNo = Math.max(maxNo, oldNo++, newNo++);
      oldLeft--;
      newLeft--;
    } else {
      rows.push({ kind: 'file', text: line });
    }
  }

  return { rows, added, removed, digits: String(Math.max(maxNo, 1)).length };
}

export type ContentRow = Extract<DiffRow, { kind: 'add' | 'del' | 'ctx' }>;
export type SplitRow = { kind: 'pair'; old?: ContentRow; next?: ContentRow } | Extract<DiffRow, { kind: 'file' | 'hunk' | 'meta' }>;

/** Align contiguous removal/addition blocks, preserving every unified row (including metadata) exactly once. */
export function sideBySideRows(rows: readonly DiffRow[]): SplitRow[] {
  const split: SplitRow[] = [];
  let removed: ContentRow[] = [];
  let added: ContentRow[] = [];
  const flush = (): void => {
    for (let i = 0; i < Math.max(removed.length, added.length); i++) split.push({ kind: 'pair', old: removed[i], next: added[i] });
    removed = [];
    added = [];
  };
  for (const row of rows) {
    if (row.kind === 'del') {
      if (added.length > 0) flush(); // a second change block without context must not reorder its lines
      removed.push(row);
    } else if (row.kind === 'add') added.push(row);
    else {
      flush();
      if (row.kind === 'ctx') split.push({ kind: 'pair', old: row, next: row });
      else split.push(row);
    }
  }
  flush();
  return split;
}
