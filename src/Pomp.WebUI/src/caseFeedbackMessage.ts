import type { Translate } from './i18n';

/** One commented case of the message the host sends for case cards (PompSession.Feedback.cs, docs/ui-protocol.md). */
export interface SentCaseComment { caseId: string; title: string; comment: string; extra?: string }
export interface SentCaseFeedback { count: number; total: number; cases: SentCaseComment[] }

const HEADER = /^User feedback with comments on (\d{1,3}) of (\d{1,3}) cases \(proposal [0-9a-f]{32}, revision \d{1,10}\)\.[^\n]*\n/;

/** The host's case-comment message, or null for anything else (then the text is shown as typed). */
export function parseSentCaseFeedback(text: string): SentCaseFeedback | null {
  const trimmed = text.trim();
  const header = HEADER.exec(trimmed);
  if (!header) return null;
  let body: unknown;
  try { body = JSON.parse(trimmed.slice(header[0].length)); } catch { return null; }
  const rows = body && typeof body === 'object' ? (body as { cases?: unknown }).cases : undefined;
  if (!Array.isArray(rows) || rows.length === 0 || rows.length !== Number(header[1])) return null;
  const cases: SentCaseComment[] = [];
  for (const row of rows as unknown[]) {
    if (!row || typeof row !== 'object') return null;
    const { caseId, title, comment, extra } = row as Record<string, unknown>;
    if (typeof caseId !== 'string' || typeof title !== 'string' || typeof comment !== 'string' || (extra !== undefined && typeof extra !== 'string')) return null;
    cases.push(extra ? { caseId, title, comment, extra } : { caseId, title, comment });
  }
  return { count: cases.length, total: Number(header[2]), cases };
}

/** Readable plain text of the comments (copy). */
export function sentCaseFeedbackText(feedback: SentCaseFeedback, t: Translate): string {
  const rows = feedback.cases.map((item, index) => `${index + 1}. ${item.title}\n${item.comment}${item.extra ? `\n${item.extra}` : ''}`);
  return [t('cases.sent', { count: feedback.count, total: feedback.total }), ...rows].join('\n\n');
}

/** One line for lists (queue, history preview). */
export function sentCaseFeedbackSummary(text: string, t: Translate): string | null {
  const feedback = parseSentCaseFeedback(text);
  return feedback && t('cases.sentSummary', { count: feedback.count, total: feedback.total, titles: feedback.cases.map(item => item.title).join(', ') });
}
