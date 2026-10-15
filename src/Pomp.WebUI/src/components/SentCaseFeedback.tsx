import { parseSentCaseFeedback } from '../caseFeedbackMessage';
import { useT } from '../i18n';
import { revealControls } from './RevealControls';
import './caseFeedback.css';

/** The user's text in a bubble; the host's case-comment message is shown as the cases and comments the user wrote. */
export function UserTextBody({ text, reveal = false }: { text: string; reveal?: boolean }) {
  const t = useT();
  const feedback = parseSentCaseFeedback(text);
  if (!feedback) return <div className="user-text user-text-content" data-testid="user-text-content">{reveal ? revealControls(text) : text}</div>;
  return (
    <div className="case-sent" data-testid="case-sent">
      <div className="case-sent-head">{t('cases.sent', { count: feedback.count, total: feedback.total })}</div>
      <ol className="case-sent-list">
        {feedback.cases.map(item => (
          <li key={item.caseId} data-testid={`case-sent-${item.caseId}`}>
            <strong className="case-sent-title">{revealControls(item.title)}</strong>
            <div className="user-text">{revealControls(item.comment)}</div>
            {item.extra && <div className="user-text case-sent-extra">{revealControls(item.extra)}</div>}
          </li>
        ))}
      </ol>
    </div>
  );
}
