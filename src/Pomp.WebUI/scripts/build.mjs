import { spawnSync } from 'node:child_process';
import { resolve } from 'node:path';
import { root, sources, stamp } from './bundle-integrity.mjs';

const before = sources();
for (const [entry, args] of [['typescript/bin/tsc', ['-b']], ['vite/bin/vite.js', ['build']]]) {
  const result = spawnSync(process.execPath, [resolve(root, 'node_modules', entry), ...args], { cwd: root, stdio: 'inherit' });
  if (result.error) throw result.error;
  if (result.status !== 0) process.exit(result.status ?? 1);
}
stamp(before);
console.log('UI source and bundle fingerprints written.');
