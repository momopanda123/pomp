import { useMemo } from 'react';
import { parseTerminalDiagnostics } from '../terminalDiagnostics';
import { revealControls } from './RevealControls';
export function TerminalDiagnostics({ output, locale, truncated }: { output: string; locale: string; truncated: boolean }) {
  const report = useMemo(() => parseTerminalDiagnostics(output), [output]); const ko = locale.startsWith('ko');
  return <details className="terminal-diagnostics">
    <summary>{ko ? '출력에서 찾은 오류·경고' : 'Errors and warnings in retained output'} ({report.findings.length})</summary>

    <ol>{report.findings.map((finding, index) => <li key={index}>
      <strong>{finding.severity}{finding.code ? ` ${revealControls(finding.code)}` : ''}</strong>{' '}
      {finding.path && <span>{revealControls(finding.path)}{finding.line ? `:${finding.line}` : ''}{finding.column ? `:${finding.column}` : ''} · </span>}
      {revealControls(finding.message)}{finding.occurrences > 1 ? ` (×${finding.occurrences})` : ''}
    </li>)}</ol>
    {(truncated || report.limited) && <p>{ko ? '출력 일부 생략' : 'Output truncated'}</p>}
  </details>;
}
