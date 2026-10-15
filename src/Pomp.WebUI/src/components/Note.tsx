import { useT } from '../i18n';
import { ClampedText } from './ClampedText';
import { Icon } from './Icon';

/**
 * One status line next to the control it belongs to: a result (green) or an error (red) whose text can be long and is clamped.
 * Host text is rendered as text only (control characters revealed by ClampedText).
 */
export function Note({ tone, text, detail, onDismiss }: { tone: 'ok' | 'error'; text: string; detail?: string; onDismiss?: () => void }) {
  const t = useT();
  return (
    <div className={`setup-msg ${tone}`} role={tone === 'error' ? 'alert' : 'status'}>
      <Icon name={tone === 'ok' ? 'check' : 'error'} size={12} />
      <span className="setup-msg-body">
        <ClampedText text={text} />
        {detail && <ClampedText className="setup-msg-detail" text={detail} />}
      </span>
      {onDismiss && (
        <button type="button" className="icon-btn tiny" data-testid="note-dismiss" onClick={onDismiss} title={t('setup.dismiss')} aria-label={t('setup.dismiss')}>
          <Icon name="x" size={11} />
        </button>
      )}
    </div>
  );
}
