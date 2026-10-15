import { createHash } from 'node:crypto';
import { readdirSync, readFileSync, statSync, lstatSync, writeFileSync, existsSync } from 'node:fs';
import { dirname, resolve, relative, sep } from 'node:path';
import { fileURLToPath } from 'node:url';

export const root = resolve(dirname(fileURLToPath(import.meta.url)), '..');
const manifestName = 'pomp-bundle.json';
const hash = path => createHash('sha256').update(readFileSync(path)).digest('hex');
const portable = path => path.split(sep).join('/');
// Junctions count as symbolic links on Windows: no input may lead outside the UI folder.
function requireReal(path) {
  if (lstatSync(path).isSymbolicLink()) throw new Error(`Symlink or junction is not a bundle input: ${path}`);
}
function walk(directory) {
  if (!existsSync(directory)) return [];
  requireReal(directory);
  return readdirSync(directory, { withFileTypes: true }).flatMap(entry => {
    const path = resolve(directory, entry.name);
    if (entry.isSymbolicLink()) throw new Error(`Symlink or junction is not a bundle input: ${path}`);
    return entry.isDirectory() ? walk(path) : [path];
  });
}
function digests(paths, base) {
  return Object.fromEntries(paths.sort().map(path => [portable(relative(base, path)), hash(path)]));
}
// Every file directly in the UI folder counts (index.html, package files, Vite/TS/PostCSS configs, .env*, .npmrc...), whatever
// its name or case, plus everything under src/, public/ and scripts/. Only dist/ and node_modules/ are not inputs.
export function sources(base = root) {
  const files = readdirSync(base, { withFileTypes: true }).filter(entry => {
    if (entry.isSymbolicLink()) {
      // A linked root folder that is not an input (node_modules, dist) is ignored; linked src/public/scripts are rejected by walk().
      if (statSync(resolve(base, entry.name)).isDirectory()) return false;
      throw new Error(`Symlink or junction is not a bundle input: ${resolve(base, entry.name)}`);
    }
    return entry.isFile();
  });
  return digests([...files.map(entry => resolve(base, entry.name)), ...walk(resolve(base, 'src')), ...walk(resolve(base, 'public')), ...walk(resolve(base, 'scripts'))], base);
}
function assets(base) {
  const dist = resolve(base, 'dist');
  return digests(walk(dist).filter(path => relative(dist, path) !== manifestName), dist);
}
function checkReferences(base, outputs) {
  const dist = resolve(base, 'dist');
  if (!outputs['index.html'] || !Object.keys(outputs).some(name => name.startsWith('assets/') && name.endsWith('.js'))) throw new Error('Missing index.html or JavaScript assets');
  const html = readFileSync(resolve(dist, 'index.html'), 'utf8');
  // Double-quoted, single-quoted and unquoted attribute values alike.
  for (const match of html.matchAll(/\b(?:src|href)\s*=\s*(?:"([^"]*)"|'([^']*)'|([^\s"'=<>`]+))/gi)) {
    const ref = (match[1] ?? match[2] ?? match[3]).trim().split(/[?#]/)[0];
    if (!ref || /^data:/i.test(ref)) continue;
    if (/^(?:[a-z]+:|\/\/)/i.test(ref)) throw new Error(`External bundle dependency: ${ref}`);
    const target = resolve(dist, decodeURIComponent(ref).replace(/^\/+/, ''));
    const name = portable(relative(dist, target));
    if (name.startsWith('../') || !outputs[name] || !statSync(target).isFile()) throw new Error(`Missing or unsafe HTML asset: ${ref}`);
  }
}
export function stamp(before, base = root) {
  const after = sources(base);
  if (JSON.stringify(before) !== JSON.stringify(after)) throw new Error('UI inputs changed during build; rebuild after edits finish');
  const outputs = assets(base);
  checkReferences(base, outputs);
  writeFileSync(resolve(base, 'dist', manifestName), JSON.stringify({ version: 1, sources: before, outputs }, null, 2) + '\n');
}
export function verify(base = root) {
  const manifest = JSON.parse(readFileSync(resolve(base, 'dist', manifestName), 'utf8'));
  if (manifest.version !== 1) throw new Error('Unsupported UI bundle manifest');
  if (JSON.stringify(manifest.sources) !== JSON.stringify(sources(base))) throw new Error('UI source fingerprint differs from built bundle');
  const outputs = assets(base);
  if (JSON.stringify(manifest.outputs) !== JSON.stringify(outputs)) throw new Error('UI bundle assets are missing, added or modified');
  checkReferences(base, outputs);
}
if (process.argv[2] === '--verify') {
  try { verify(process.argv[3] ? resolve(process.argv[3]) : root); }
  catch (error) { console.error(`POMP1006: ${error.message}. Run npm run build in src/Pomp.WebUI.`); process.exitCode = 1; }
}
