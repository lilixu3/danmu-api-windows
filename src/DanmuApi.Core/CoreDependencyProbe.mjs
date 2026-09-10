// Read-only metadata/entry probe. Never imports package code or runs lifecycle scripts.
import fs from 'node:fs';
import path from 'node:path';
import {createRequire} from 'node:module';
import {pathToFileURL, fileURLToPath} from 'node:url';
const input = JSON.parse(fs.readFileSync(0, 'utf8'));
const core = path.resolve(input.core), shared = path.resolve(input.shared);
const roots = [core, shared];
// Host-provided names that Windows deliberately does not bundle (the host loads the core through
// worker.js, so server.js's dotenv/chokidar path never runs; esbuild is a build-time tool; redis
// ships as an opt-in payload). They are excluded from the closure so a healthy install is not
// reported as broken. Everything else must still resolve, so a genuinely new dependency is caught.
const excluded = new Set();
if (input.excluded !== undefined) {
  if (!Array.isArray(input.excluded) || input.excluded.length > 64) throw Error('Invalid excluded list');
  for (const name of input.excluded) {
    if (typeof name !== 'string' || !/^(?:@[a-z0-9][a-z0-9._-]*\/)?[a-z0-9][a-z0-9._-]*$/.test(name))
      throw Error('Invalid excluded dependency name');
    excluded.add(name);
  }
}
const inside = (root, p) => p.toLowerCase() === root.toLowerCase() || p.toLowerCase().startsWith(root.toLowerCase() + path.sep);
function safe(p) {
  p = path.resolve(p);
  if (!roots.some(r => inside(r, p))) throw Error('Dependency escaped core/public directories');
  for (let q = p; ; q = path.dirname(q)) {
    if (fs.existsSync(q) && fs.lstatSync(q).isSymbolicLink()) throw Error('Dependency links are forbidden');
    if (path.dirname(q) === q) break;
  }
  return p;
}
function json(p) {
  safe(p);
  const stat = fs.statSync(p);
  if (!stat.isFile() || stat.size > 1024 * 1024) throw Error('Invalid/oversized package.json');
  const value = JSON.parse(fs.readFileSync(p, 'utf8'));
  if (!value || typeof value !== 'object' || Array.isArray(value)) throw Error('Invalid package.json object');
  return value;
}
const namePattern = /^(?:@[a-z0-9][a-z0-9._-]*\/)?[a-z0-9][a-z0-9._-]*$/;
function dependencies(pkg) {
  function field(name) {
    if (!Object.hasOwn(pkg, name)) return {};
    const v = pkg[name];
    if (!v || typeof v !== 'object' || Array.isArray(v)) throw Error(name + ' must be an object');
    return v;
  }
  const value = {...field('dependencies')};
  const optional = field('optionalDependencies');
  for (const name of Object.keys(optional)) delete value[name];
  const peers = field('peerDependencies'), peerMeta = field('peerDependenciesMeta');
  for (const [name, range] of Object.entries(peers)) {
    const meta = peerMeta[name];
    if (meta !== undefined && (!meta || typeof meta !== 'object' || Array.isArray(meta) ||
        (Object.hasOwn(meta, 'optional') && typeof meta.optional !== 'boolean'))) throw Error('Invalid peer dependency metadata');
    if (meta?.optional === true) continue;
    if (Object.hasOwn(value, name) && value[name] !== range) throw Error('Conflicting dependency and peer ranges are unsupported: ' + name);
    value[name] = range;
  }
  if (Object.keys(value).length > 1000) throw Error('Too many dependency declarations');
  for (const [name, range] of Object.entries(value)) {
    if (!namePattern.test(name) || typeof range !== 'string' || !range.trim()) throw Error('Invalid dependency declaration');
    // Validate before finding an installed version; unknown protocols/ranges never pass.
    satisfies(range, '0.0.0');
  }
  return value;
}
// Prerelease/build suffixes are accepted syntactically but compared on the numeric triple only:
// the bundles really do declare ranges like `^1.0.0-rc.4-de6c356` (drizzle-orm), and refusing to
// parse them reported a perfectly healthy install as broken. Prerelease *ordering* is not modelled
// on purpose — this check only needs "is the installed version plausibly in range", and being lax
// here avoids false "missing" alarms.
function version(v) {
  const m = /^(?:v)?(0|[1-9]\d*)\.(0|[1-9]\d*)\.(0|[1-9]\d*)(?:-[0-9A-Za-z.-]+)?(?:\+[0-9A-Za-z.-]+)?$/.exec(v);
  if (!m) throw Error('Unsupported exact dependency version: ' + v);
  const a = m.slice(1, 4).map(Number);
  if (!a.every(Number.isSafeInteger)) throw Error('Invalid dependency version');
  return a;
}
const cmp = (a,b) => a[0]-b[0] || a[1]-b[1] || a[2]-b[2];
function satisfies(range, actual) {
  const a = version(actual);
  let accepted = false;
  for (const branch of range.trim().split('||')) {
    if (!branch.trim()) throw Error('Unsupported dependency range (only stable semver ranges are supported)');
    const tokens = branch.trim().replace(/(>=|<=|>|<|=|\^|~)\s+/g, '$1').split(/\s+/);
    let ok = true;
    for (const token of tokens) {
      if (/^(\*|x|X)$/.test(token)) continue;
      const m = /^(>=|<=|>|<|=|\^|~)?(v?\d+(?:\.(?:\d+|x|X|\*)){0,2}(?:-[0-9A-Za-z.-]+)?(?:\+[0-9A-Za-z.-]+)?)$/.exec(token);
      if (!m) throw Error('Unsupported dependency range (only stable semver ranges are supported)');
      const op = m[1] || '', raw = m[2].replace(/^v/, '').split('+')[0].split('-')[0].split('.');
      const n = raw.findIndex(v => /^(x|X|\*)$/.test(v));
      const count = n < 0 ? raw.length : n;
      if (n >= 0 && raw.slice(n).some(v => !/^(x|X|\*)$/.test(v))) throw Error('Invalid dependency wildcard');
      if (op && count < 3 && op !== '^' && op !== '~') throw Error('Unsupported partial comparator: ' + range);
      const b = [0,1,2].map(i => i < count ? Number(raw[i]) : 0);
      if (!b.every(Number.isSafeInteger)) throw Error('Invalid dependency range');
      let good;
      if (op === '^' || op === '~') {
        let upper;
        if (op === '~') upper = count <= 1 ? [b[0]+1,0,0] : [b[0],b[1]+1,0];
        else if (b[0] > 0 || count === 1) upper = [b[0]+1,0,0];
        else if (b[1] > 0 || count === 2) upper = [0,b[1]+1,0];
        else upper = [0,0,b[2]+1];
        good = cmp(a,b) >= 0 && cmp(a,upper) < 0;
      } else if (!op && count < 3) good = a.slice(0,count).every((v,i) => v === b[i]);
      else good = ({'>=': cmp(a,b)>=0, '<=': cmp(a,b)<=0, '>':cmp(a,b)>0, '<':cmp(a,b)<0, '=':cmp(a,b)===0, '':cmp(a,b)===0})[op];
      ok = ok && good;
    }
    accepted = accepted || ok;
  }
  return accepted;
}
function locate(parent, name) {
  for (let dir = parent; ; dir = path.dirname(dir)) {
    const candidate = path.join(dir, 'node_modules', name);
    if (fs.existsSync(candidate)) return safe(candidate);
    if (dir.toLowerCase() === path.dirname(shared).toLowerCase() || path.dirname(dir) === dir) break;
  }
  throw Error('Missing required package');
}
const issues = [], visited = new Set();
let total = 0;
function walk(parent, pkg, publicRoot = null, depth = 0) {
  if (depth > 100) throw Error('Dependency closure depth quota exceeded');
  for (const [name, range] of Object.entries(dependencies(pkg))) {
    if (excluded.has(name)) continue;
    if (++total > 5000) throw Error('Dependency closure quota exceeded');
    let dir = null, root = publicRoot;
    try {
      dir = locate(parent, name);
      if (inside(shared, dir) && root === null) root = {name, directory: dir};
      const meta = json(path.join(dir, 'package.json'));
      if (meta.name !== name || typeof meta.version !== 'string') throw Error('Invalid package name/version');
      if (!satisfies(range, meta.version)) throw Error('Installed version ' + meta.version + ' does not satisfy declaration');
      const ref = pathToFileURL(path.join(parent, '__danmu_dependency_probe__.mjs'));
      const entry = pkg.type === 'module' ? fileURLToPath(import.meta.resolve(name, ref.href)) : createRequire(ref).resolve(name);
      safe(entry);
      if (!inside(dir, entry) || !fs.statSync(entry).isFile()) throw Error('Required package entry is absent or outside its package');
      if (!visited.has(dir.toLowerCase())) {
        visited.add(dir.toLowerCase());
        walk(dir, meta, root, depth + 1);
      }
    } catch (e) {
      issues.push({name, range, parentDirectory:parent, installedDirectory:dir, publicRootName:root?.name ?? null,
        publicRootDirectory:root?.directory ?? null, diagnostic: String(e.message)});
    }
  }
}
try {
  if (Number(process.versions.node.split('.')[0]) !== 24) throw Error('Dependency repair requires Node major 24');
  const pkg = json(path.join(core, 'package.json'));
  if (!Object.hasOwn(pkg, 'dependencies')) throw Error('Core package.json lacks dependencies; dependency requirements are unknown');
  walk(core, pkg);
  process.stdout.write(JSON.stringify({total, issues}));
} catch (e) {
  process.stderr.write(String(e.message));
  process.exitCode = 1;
}
