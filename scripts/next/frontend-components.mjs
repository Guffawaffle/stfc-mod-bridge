import assert from 'node:assert/strict';
import { spawnSync } from 'node:child_process';
import { mkdirSync, readFileSync, writeFileSync, readdirSync } from 'node:fs';
import { createHash, randomUUID } from 'node:crypto';
import path from 'node:path';
import { componentEvidence, componentCriteria } from './frontend-components-evidence.mjs';
import { nodeTestEvidence } from './test-evidence.mjs';
import { ownedArtifactPath } from './owned-artifact.mjs';

const root = path.resolve(import.meta.dirname, '../..');
assert.equal(process.argv.length, 2, 'The component suite accepts no overrides');
assert.equal(process.version, `v${JSON.parse(readFileSync(path.join(root, 'package.json'), 'utf8')).engines.node}`);
const directory = ownedArtifactPath(root, `artifacts/next/frontend-components/${randomUUID()}`, 'directory', { allowMissing: true });
mkdirSync(directory, { recursive: true });
const checks = [];
function run(id, argv) {
  const started = Date.now();
  const result = spawnSync(process.execPath, argv, { cwd: root, windowsHide: true, encoding: 'utf8', timeout: 180000, maxBuffer: 8 * 1024 * 1024 });
  const observation = { id, executable: process.execPath, argv, cwd: root, exitCode: result.status,
    error: result.error?.code ?? null, durationMs: Date.now() - started, stdout: result.stdout ?? '', stderr: result.stderr ?? '' };
  writeFileSync(path.join(directory, `${id}.json`), JSON.stringify(observation, null, 2) + '\n'); checks.push(observation);
  assert.equal(result.status, 0, `${id} failed; inspect its retained observation`); assert.ok(!result.error);
  return observation.stdout;
}
function sources(relative) {
  const file = ownedArtifactPath(root, relative, 'directory');
  return readdirSync(file, { withFileTypes: true }).sort((a, b) => a.name.localeCompare(b.name)).flatMap(entry => {
    const child = `${relative}/${entry.name}`;
    assert.ok(entry.isFile() || entry.isDirectory(), 'Component sources cannot be links');
    return entry.isDirectory() ? sources(child) : [child];
  });
}
const inputs = [...sources('ui/src/components'), ...sources('ui/src/styles'), ...sources('ui/src/state'),
  ...sources('ui/src/client'), ...sources('ui/src/generated'), ...sources('contracts/fixtures'),
  ...['ui/src/app/Shell.svelte', 'ui/src/app/LiveAnnouncements.svelte', 'ui/src/app/context.ts', 'ui/src/app/index.ts',
    'contracts/codec/strict-json.mjs', 'package.json', 'pnpm-lock.yaml', 'ui/package.json',
    ...Object.keys(componentCriteria), 'scripts/next/frontend-components.mjs',
    'scripts/next/frontend-components-evidence.mjs', 'scripts/next/tests/frontend-components-evidence.test.mjs']];
const hashInputs = () => inputs.map(relative => {
  const bytes = readFileSync(ownedArtifactPath(root, relative));
  return { path: relative, bytes: bytes.length, sha256: createHash('sha256').update(bytes).digest('hex') };
});
const before = hashInputs();
const typeOutput = run('frontend-types', ['scripts/next/pnpm.mjs', '--dir', 'ui', 'check']);
assert.match(typeOutput, /svelte-check found 0 errors and 0 warnings/);
const nodeRequired = { 'scripts/next/tests/frontend-components-evidence.test.mjs': [
  'component receipt binds all required assertion names and exact observed file counts',
  'component receipt rejects a zero-exit skipped criterion',
  'component receipt rejects missing renamed or duplicate criterion identities',
  'component receipt rejects substituted or extra test files',
  'component receipt rejects missing assertions and dishonest total counts',
  'component receipt rejects failed cancelled pending or todo evidence',
] };
const nodeInventory = nodeTestEvidence(run('receipt-tests', ['--test', '--test-reporter=./scripts/next/node-test-reporter.mjs', ...Object.keys(nodeRequired)]), { root, required: nodeRequired });
assert.equal(nodeInventory.tests, 6);
const reportFile = path.join(directory, 'vitest.json');
run('component-tests', ['scripts/next/pnpm.mjs', '--dir', 'ui', 'exec', 'vitest', 'run',
  ...Object.keys(componentCriteria).map(file => file.slice(3)), '--reporter=json', `--outputFile=${reportFile}`]);
const inventory = componentEvidence(JSON.parse(readFileSync(ownedArtifactPath(root, path.relative(root, reportFile).replaceAll('\\', '/')), 'utf8')), root);
assert.deepEqual(hashInputs(), before, 'Component source bytes changed during the gate');
const receipt = path.join(directory, 'components.json');
writeFileSync(receipt, JSON.stringify({ schemaVersion: 'bridge-frontend-components-observation/v1',
  completedAt: new Date().toISOString(), host: { platform: process.platform, architecture: process.arch, node: process.version },
  checks, sources: before, inventories: { frontend: inventory, node: nodeInventory, frontendReport: reportFile },
  boundary: 'Actual Svelte type check, SSR/modal-port/palette tests and public typed-facade transitions over shared synthetic fixtures. Browser behavior is qualified separately.',
  browserTestsExecuted: false, rustBuildInvoked: false, engineExecutionQualified: false, nativeRuntimeQualified: false, releaseQualified: false,
}, null, 2) + '\n');
console.log(JSON.stringify({ result: 'passed', tests: inventory.tests, receiptTests: nodeInventory.tests, receipt, nativeRuntimeQualified: false, releaseQualified: false }));
