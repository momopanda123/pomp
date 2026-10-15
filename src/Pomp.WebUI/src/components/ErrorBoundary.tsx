import { Component, type ErrorInfo, type ReactNode } from 'react';
import { makeTranslate } from '../i18n';
import { logToHost } from '../protocol';

interface State {
  failed: boolean;
}

/** Last line of defense: a render error must not leave a blank tool window. Logs to the host and offers a reload. */
export class ErrorBoundary extends Component<{ children: ReactNode }, State> {
  override state: State = { failed: false };

  static getDerivedStateFromError(): State {
    return { failed: true };
  }

  override componentDidCatch(error: Error, info: ErrorInfo): void {
    logToHost('error', `UI crashed: ${error.stack ?? error.message}\n${info.componentStack ?? ''}`);
  }

  override render(): ReactNode {
    if (!this.state.failed) return this.props.children;
    // The i18n context may be part of the crashed tree, so the strings are resolved directly.
    const t = makeTranslate(document.documentElement.lang === 'en' ? 'en' : 'ko');
    return (
      <div className="crash" role="alert">
        <p>{t('crash.title')}</p>
        <button type="button" className="btn primary" onClick={() => window.location.reload()}>
          {t('crash.reload')}
        </button>
      </div>
    );
  }
}
