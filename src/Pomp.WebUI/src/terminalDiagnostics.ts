export interface TerminalDiagnostic {
  path: string; line?: number; column?: number; severity: 'error' | 'warning'; code?: string; message: string; occurrences: number;
}
/** Read-only patterns over the retained tail, never a compiler verdict, file access or command suggestion. */
export function parseTerminalDiagnostics(output: string): { findings: TerminalDiagnostic[]; limited: boolean } {
  const bounded = output.slice(-65536); const lines = bounded.split(/\r?\n/).slice(-2000);
  const findings: TerminalDiagnostic[] = []; const seen = new Map<string, TerminalDiagnostic>();
  let limited = output.length > bounded.length || bounded.split(/\r?\n/).length > lines.length;
  for (let index = 0; index < lines.length; index++) {
    const raw = lines[index]!;
    if (raw.length > 4096) { limited = true; continue; }
    // Strip bounded ANSI CSI color/control sequences; ordinary text is still rendered as text by React.
    const line = raw.replace(/\x1b\[[0-?]*[ -/]*[@-~]/g, '').replace(/^\s*\d+>/, '').trim();
    let finding: TerminalDiagnostic | undefined;
    const msbuild = /^(.+?)\((\d{1,7})(?:,(\d{1,7}))?(?:,\d{1,7},\d{1,7})?\)\s*:\s*(?:fatal\s+)?(error|warning)\s+([A-Za-z][A-Za-z0-9_-]{0,30})\s*:\s*(.*)$/i.exec(line);
    const clang = msbuild ? null : /^(.+?):(\d{1,7})(?::(\d{1,7}))?:\s*(?:fatal\s+)?(error|warning):\s*(.*)$/i.exec(line);
    const locationless = msbuild || clang ? null : /^(?:[^:]{1,100}:\s*)?(error|warning)\s+([A-Za-z][A-Za-z0-9_-]{0,30})\s*:\s*(.*)$/i.exec(line);
    if (msbuild) finding = { path: msbuild[1]!, line: Number(msbuild[2]), column: msbuild[3] ? Number(msbuild[3]) : undefined, severity: msbuild[4]!.toLowerCase() as 'error' | 'warning', code: msbuild[5], message: msbuild[6]!, occurrences: 1 };
    else if (clang) finding = { path: clang[1]!, line: Number(clang[2]), column: clang[3] ? Number(clang[3]) : undefined, severity: clang[4]!.toLowerCase() as 'error' | 'warning', message: clang[5]!, occurrences: 1 };
    else if (locationless) finding = { path: '', severity: locationless[1]!.toLowerCase() as 'error' | 'warning', code: locationless[2], message: locationless[3]!, occurrences: 1 };
    if (!finding) continue;
    if (finding.path.length > 1024 || finding.message.length > 2000 || (finding.line !== undefined && finding.line < 1) || (finding.column !== undefined && finding.column < 1)) { limited = true; continue; }
    const key = JSON.stringify([finding.path, finding.line, finding.column, finding.severity, finding.code, finding.message]);
    const previous = seen.get(key);
    if (previous) { previous.occurrences++; continue; }
    if (findings.length >= 50) { limited = true; continue; }
    seen.set(key, finding); findings.push(finding);
  }
  return { findings, limited };
}
