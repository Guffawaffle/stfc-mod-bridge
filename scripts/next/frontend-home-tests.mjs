import assert from 'node:assert/strict';
import { spawnSync } from 'node:child_process';
import { createHash, randomUUID } from 'node:crypto';
import { mkdirSync, readFileSync, readdirSync, realpathSync, writeFileSync } from 'node:fs';
import path from 'node:path';
import { ownedArtifactPath } from './owned-artifact.mjs';
import { homeCriteria, homeEvidence } from './frontend-home-evidence.mjs';

const root = path.resolve(import.meta.dirname, '../..');
assert.equal(realpathSync(process.cwd()), realpathSync(root));
assert.equal(process.argv.length, 2, 'Home tests accept no overrides');
assert.equal(process.version, `v${JSON.parse(readFileSync(path.join(root, 'package.json'))).engines.node}`);
const directory = ownedArtifactPath(root, `artifacts/next/frontend-home/${randomUUID()}`, 'directory', { allowMissing: true });
mkdirSync(directory, { recursive: true });
const sha = bytes => createHash('sha256').update(bytes).digest('hex');
function files(relative) {
  return readdirSync(ownedArtifactPath(root, relative, 'directory'), { withFileTypes: true }).sort((a, b) => a.name.localeCompare(b.name)).flatMap(entry => {
    assert.ok(entry.isFile() || entry.isDirectory(), 'Source inventory cannot follow links');
    return entry.isDirectory() ? files(`${relative}/${entry.name}`) : [`${relative}/${entry.name}`];
  });
}
const inputs = [...new Set(['package.json', 'pnpm-lock.yaml', 'ui/package.json', 'ui/svelte.config.js', 'ui/tsconfig.json',
  'contracts/codec/strict-json.mjs', ...files('contracts/fixtures'), ...files('ui/src'), ...files('ui/tests/home'), ...files('ui/tests/home-preview'),
  'scripts/next/frontend-home-tests.mjs', 'scripts/next/frontend-home-evidence.mjs', 'scripts/next/test-evidence.mjs'])].sort();
const hashInputs = () => inputs.map(relative => {
  const bytes = readFileSync(ownedArtifactPath(root, relative)); return { path: relative, bytes: bytes.length, sha256: sha(bytes) };
});
const before = hashInputs(), checks = [];
function run(id, argv) {
  const start = Date.now();
  const result = spawnSync(process.execPath, argv, { cwd: root, windowsHide: true, encoding: 'utf8', timeout: 180000, maxBuffer: 8 * 1024 * 1024 });
  const observed = { id, executable: process.execPath, argv, cwd: root, startedAt: new Date(start).toISOString(),
    durationMs: Date.now() - start, exitCode: result.status, error: result.error?.code ?? null, stdout: result.stdout ?? '', stderr: result.stderr ?? '' };
  checks.push(observed); writeFileSync(path.join(directory, `${id}.json`), JSON.stringify(observed, null, 2) + '\n', { flag: 'wx' });
  assert.equal(result.status, 0, `Home ${id} failed; inspect retained observation`); assert.ok(!result.error); return observed.stdout;
}
assert.match(run('types', ['scripts/next/pnpm.mjs', '--dir', 'ui', 'check']), /svelte-check found 0 errors and 0 warnings/);
const report = path.join(directory, 'vitest.json');
run('focused-tests', ['scripts/next/pnpm.mjs', '--dir', 'ui', 'exec', 'vitest', 'run', ...Object.keys(homeCriteria).map(file => file.slice(3)),
  '--reporter=json', `--outputFile=${report}`]);
const inventory = homeEvidence(JSON.parse(readFileSync(report, 'utf8')), root);
assert.deepEqual(hashInputs(), before, 'Home sources changed during tests');
const receipt = path.join(directory, 'home-tests.json');
writeFileSync(receipt, JSON.stringify({ schemaVersion: 'bridge-frontend-home-tests/v1', result: 'passed', completedAt: new Date().toISOString(),
  checks, sources: before, inventory, report, boundary: 'Typed fixture composition, shared action custody, scoped reads and SSR presentation. Browser and native services qualify separately.',
  browserTestsExecuted: false, rustBuildInvoked: false, nativeRuntimeQualified: false, releaseQualified: false }, null, 2) + '\n', { flag: 'wx' });
console.log(JSON.stringify({ result: 'passed', tests: inventory.tests, receipt, nativeRuntimeQualified: false, releaseQualified: false }));
