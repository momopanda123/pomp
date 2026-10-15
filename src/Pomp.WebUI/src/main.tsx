import '@xterm/xterm/css/xterm.css';
import { StrictMode } from 'react';
import { createRoot } from 'react-dom/client';
import { App } from './App';
import { installContextMenuGuard } from './components/ContextMenu';
import { ErrorBoundary } from './components/ErrorBoundary';
import { hasHost } from './protocol';
import './styles/base.css';
import './styles/chat.css';
import './styles/chrome.css';
import './styles/context-kinds.css';
import './styles/picker.css';
import './styles/setup.css';
import './styles/work.css';
import './styles/shell.css';
import './styles/agents.css';

async function boot(): Promise<void> {
  // Suppress the browser page menu over non-editable chat UI (the host filters its
  // own native menu in WebView2; in the dev preview this guard is what removes it).
  installContextMenuGuard();
  // Dev only: in a plain browser (no WebView2) a mock host stands in for Visual Studio. `import.meta.env.DEV` is a
  // build-time constant, so this branch and the dynamically imported module are dropped from production bundles.
  if (import.meta.env.DEV && !hasHost()) {
    const { installMockHost } = await import('./dev/mockHost');
    installMockHost();
  }

  createRoot(document.getElementById('root')!).render(
    <StrictMode>
      <ErrorBoundary>
        <App />
      </ErrorBoundary>
    </StrictMode>,
  );
}

void boot();
