import type { ThemePayload } from './protocol';

/** Applies `theme/changed` to the document root: every CSS variable, color scheme and the VS font. */
export function applyTheme(theme: ThemePayload): void {
  const root = document.documentElement;

  for (const [name, value] of Object.entries(theme.colors ?? {})) {
    // Only custom properties are accepted so a malformed payload cannot restyle arbitrary CSS properties.
    if (name.startsWith('--') && typeof value === 'string') root.style.setProperty(name, value);
  }

  const kind = theme.kind === 'light' ? 'light' : 'dark';
  root.dataset.theme = kind;
  root.style.colorScheme = kind;

  if (theme.fontFamily) {
    const family = theme.fontFamily.trim();
    // A bare multi-word family ("Segoe UI") must be quoted; lists and already-quoted values are used as given.
    const first = /[",]/.test(family) || !/\s/.test(family) ? family : `"${family}"`;
    root.style.setProperty('--pomp-font-family', `${first}, "Segoe UI", system-ui, sans-serif`);
  }
  if (theme.fontSizePx && theme.fontSizePx > 0) {
    root.style.setProperty('--pomp-font-size', `${Math.min(Math.max(theme.fontSizePx, 10), 24)}px`);
  }
}
