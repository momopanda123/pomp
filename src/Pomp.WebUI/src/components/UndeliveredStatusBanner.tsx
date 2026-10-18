import { useT } from '../i18n';
import { dismissUndeliveredStatus, useUndeliveredStatus } from '../statusBar';
import { Icon } from './Icon';

/**
 * Notices normally go to the VS status bar. When the page cannot reach Visual Studio they cannot get there, so the last one shows here,
 * in the same banner the connection problems use, until a notice gets through again or it is closed.
 */
export function UndeliveredStatusBanner() {
  const t = useT();
  const notice = useUndeliveredStatus();
  if (!notice) return null;
  return (
    <div className={`banner ${notice.level === 'info' ? 'warning' : notice.level}`} role="alert" data-testid="undelivered-status">
      <Icon name={notice.level === 'error' ? 'error' : 'warning'} size={14} />
      <span className="banner-text">{notice.text}</span>
      <span className="banner-actions">
        <button type="button" className="icon-btn" title={t('agents.close')} aria-label={t('agents.close')} onClick={dismissUndeliveredStatus}><Icon name="x" size={12} /></button>
      </span>
    </div>
  );
}
