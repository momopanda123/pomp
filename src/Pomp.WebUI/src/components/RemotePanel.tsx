import { useId } from 'react';
import { useT } from '../i18n';
import { send } from '../protocol';
import type { SetupReply, SetupState } from '../setup';
import { useSetupCommands } from '../setupCommands';
import { useRefreshOnShow } from '../useRefreshOnShow';
import { Icon } from './Icon';
import { ModelShareSection } from './ModelShareSection';
import { revealControls } from './RevealControls';
import { SharedModelConnectSection } from './SharedModelConnectSection';

interface RemotePanelProps {
  /** Latest `setup/state` of the host; null until the first one arrives. */
  setup: SetupState | null;
  /** Answers to this page's commands (see `AppState.setupReplies`). */
  replies: SetupReply[];
  /** omp has no model yet (first run): only connecting to another PC is offered, there is nothing to share. */
  required: boolean;
  /** omp is running: the restart after a new connection can be asked for. */
  connected: boolean;
}

/**
 * Remote: model sharing between PCs on the same network (docs/screen-guide.md "원격 탭"). The PC with internet and AI sign-ins shares
 * its models ("Share my models with another PC"); a PC without them connects with the address and one-time code ("Use another PC's
 * models"). Both run over the `setup/state` / `setup/command` channel of the Accounts tab.
 *
 * The typed connection code is a secret: it exists only in the connect form's input state and in the one `connectSharedModels`
 * command, is cleared as soon as that is posted, and is never put into reducer state, storage, a log message or an error text.
 */
export function RemotePanel({ setup, replies, required, connected }: RemotePanelProps) {
  const t = useT();
  const id = useId();
  const cmd = useSetupCommands(replies);

  useRefreshOnShow(true, () => {
    cmd.post('refresh', (requestId) => ({ requestId, action: 'refresh' }));
  });
  const refreshAnswer = cmd.feedback('refresh');

  return (
    <div className="setup-page remote-page">
      <section className="setup-panel" aria-labelledby={`${id}-title`} tabIndex={-1} data-testid="remote-panel">
        <div className="setup-head sr-only">
          <span id={`${id}-title`} className="setup-title">{t('remote.title')}</span>
        </div>

        <div className="setup-body">
          {setup?.restartRecommended && (
            <div className="setup-notice" role="status" data-testid="remote-restart">
              <Icon name="info" size={13} />
              <span className="setup-notice-text">{t('setup.restartNotice')}</span>
              <button type="button" className="btn primary" disabled={!connected} onClick={() => send('bridge/restart', {})}>
                <Icon name="restart" size={12} />
                {t('setup.restart')}
              </button>
            </div>
          )}

          {!setup && (
            <div className="setup-loading" role="status">
              {refreshAnswer.error ? <Icon name="error" size={12} /> : <span className="spinner" aria-hidden="true" />}
              <span className="setup-running-text">{refreshAnswer.error ? revealControls(refreshAnswer.error) : t('setup.loading')}</span>
              {!refreshAnswer.pending && (
                <button type="button" className="btn subtle" onClick={() => cmd.post('refresh', (requestId) => ({ requestId, action: 'refresh' }))}>
                  {t('setup.reload')}
                </button>
              )}
            </div>
          )}

          {setup && (
            <>
              {!required && setup.modelShare && <ModelShareSection share={setup.modelShare} cmd={cmd} inert={false} />}
              <SharedModelConnectSection cmd={cmd} inert={false} />
            </>
          )}
        </div>
      </section>
    </div>
  );
}
