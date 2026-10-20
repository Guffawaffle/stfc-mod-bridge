import assert from 'node:assert/strict';
import { spawnSync } from 'node:child_process';
import { mkdirSync, readFileSync, writeFileSync } from 'node:fs';
import { randomUUID } from 'node:crypto';
import path from 'node:path';
import { frontendCriteria, nodeTestEvidence, vitestEvidence } from './test-evidence.mjs';
import { ownedArtifactPath } from './owned-artifact.mjs';

const root = path.resolve(import.meta.dirname, '../..');
assert.equal(process.argv.length, 2, 'The mock contract suite accepts no overrides');
const directory = ownedArtifactPath(root, `artifacts/next/mock-contract/${randomUUID()}`, 'directory', { allowMissing: true });
mkdirSync(directory, { recursive: true });
const checks = [];
function run(id, argv) {
  const result = spawnSync(process.execPath, argv, { cwd: root, windowsHide: true,
    encoding: 'utf8', timeout: 180000, maxBuffer: 8 * 1024 * 1024 });
  const observation = { id, executable: process.execPath, argv, cwd: root,
    exitCode: result.status, error: result.error?.code ?? null,
    stdout: result.stdout ?? '', stderr: result.stderr ?? '' };
  writeFileSync(path.join(directory, `${id}.json`), JSON.stringify(observation, null, 2) + '\n');
  checks.push(observation);
  assert.equal(result.status, 0, `Mock contract ${id} failed; inspect its retained observation`);
  assert.ok(!result.error);
  return observation.stdout;
}
run('validator-drift', ['scripts/next/generate-browser-validators.mjs', '--check']);
run('catalog-drift', ['scripts/next/generate-mock-catalog.mjs', '--check']);
const nodeRequired = {
  'scripts/next/tests/browser-validators.test.mjs': ['bundled browser guards match every shared golden wire outcome without native semantic claims'],
  'scripts/next/tests/owned-artifact.test.mjs': [
    'artifact resolution permits only physical owned ancestry, including not-yet-created outputs',
    'browser and native target junctions cannot move build or execution outside the owner'],
  'scripts/next/tests/test-evidence.test.mjs': [
    'zero-exit Node run with skipped criterion cannot qualify',
    'Vitest evidence refuses skipped assertions, absent files and missing criterion names'],
  'scripts/next/tests/browser-cleanup.test.mjs': [
    'browser close failure still logs and stops the owned Vite child',
    'log failure still stops only a live owned child and preserves cleanup errors']
};
const nodeOutput = run('golden-wire-and-gate-tests', ['--test', '--test-reporter=./scripts/next/node-test-reporter.mjs', ...Object.keys(nodeRequired)]);
const nodeInventory = nodeTestEvidence(nodeOutput, { root, required: nodeRequired });
run('fixture-types', ['scripts/next/pnpm.mjs', '--dir', 'ui', 'exec', 'tsc', '--project', '../contracts/fixtures/tsconfig.json']);
run('frontend-types', ['scripts/next/pnpm.mjs', '--dir', 'ui', 'check']);
const testReport = path.join(directory, 'vitest.json');
run('frontend-tests', ['scripts/next/pnpm.mjs', '--dir', 'ui', 'exec', 'vitest', 'run', '--reporter=json', `--outputFile=${testReport}`]);
const fixtures = JSON.parse(readFileSync(path.join(root, 'contracts/fixtures/index.json'), 'utf8'));
const required = structuredClone(frontendCriteria);
required['ui/tests/client-wire.test.ts'].push(...fixtures.fixtures.map(fixture => `generated schema/framing parity: ${fixture.id}`));
const frontendInventory = vitestEvidence(JSON.parse(readFileSync(testReport, 'utf8')), { root, required });
const receipt = path.join(directory, 'contract.json');
writeFileSync(receipt, JSON.stringify({ schemaVersion: 'bridge-mock-contract-observation/v1',
  host: { platform: process.platform, architecture: process.arch, node: process.version }, checks,
  inventories: { node: nodeInventory, frontend: frontendInventory, frontendReport: testReport },
  boundary: 'Generated browser guards, immutable typed client, shared synthetic scripts, deterministic clock and observation reconciliation',
  rustBuildInvoked: false, engineExecutionQualified: false, nativeRuntimeQualified: false, releaseQualified: false
}, null, 2) + '\n');
console.log(JSON.stringify({ result: 'passed', checks: checks.length, receipt,
  rustBuildInvoked: false, nativeRuntimeQualified: false, releaseQualified: false }));
