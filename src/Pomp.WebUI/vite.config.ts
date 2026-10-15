import react from '@vitejs/plugin-react';
import { defineConfig, type Plugin } from 'vite';

// index.html ships a strict CSP <meta>; the production bundle works under it unchanged. The dev server additionally
// needs inline scripts (React Refresh preamble) and a WebSocket (HMR), so the tag is relaxed for `vite dev` only.
function devCsp(): Plugin {
  const devPolicy =
    "default-src 'self'; img-src 'self' data:; style-src 'self' 'unsafe-inline'; " +
    "script-src 'self' 'unsafe-inline'; connect-src 'self' ws://localhost:* ws://127.0.0.1:*";
  return {
    name: 'pomp-dev-csp',
    apply: 'serve',
    transformIndexHtml: {
      order: 'post',
      handler: (html) =>
        html.replace(/(<meta http-equiv="Content-Security-Policy" content=")[^"]*(")/, `$1${devPolicy}$2`),
    },
  };
}

export default defineConfig({
  // Relative asset URLs: the page is served from a mapped local folder (https://pomp.example/index.html).
  base: './',
  plugins: [react(), devCsp()],
  server: { host: '127.0.0.1', port: 5173 },
  preview: { host: '127.0.0.1', port: 4173 },
  build: {
    outDir: 'dist',
    emptyOutDir: true,
    sourcemap: false,
    cssCodeSplit: false,
    modulePreload: { polyfill: false },
  },
});
