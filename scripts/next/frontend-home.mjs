import assert from 'node:assert/strict';
import { spawnSync } from 'node:child_process';
import { createHash, randomUUID } from 'node:crypto';
import { mkdirSync, readFileSync, realpathSync, writeFileSync } from 'node:fs';
import path from 'node:path';
import { ownedArtifactPath } from './owned-artifact.mjs';
import { fingerprintInputRecords } from './input-tree.mjs';

const root = path.resolve(import.meta.dirname, '../..');
assert.equal(realpathSync(process.cwd()), realpathSync(root));
assert.equal(process.argv.length, 2, 'Home suite accepts no overrides');
const directory = ownedArtifactPath(root, `artifacts/next/frontend-home-suite/${randomUUID()}`, 'directory', { allowMissing: true });
mkdirSync(directory, { recursive: true });
const inputs = ['package.json', 'pnpm-lock.yaml', 'pnpm-workspace.yaml', 'dependencies/next-toolchain.json', 'docs/next/FRONTEND_HOME.md',
  'ui/package.json', 'ui/vite.config.ts', 'ui/tsconfig.json', 'ui/svelte.config.js', 'ui/index.html', 'ui/src', 'ui/home-preview',
  'ui/tests/home', 'ui/tests/home-preview', 'contracts', 'scripts/next', 'assets/stfc-mod-bridge.png'];
const before = fingerprintInputRecords(root, inputs), checks = [];
function run(id, argv, timeoutMs = 300000) {
  const start = Date.now(); const result = spawnSync(process.execPath, argv, { cwd: root, windowsHide: true, encoding: 'utf8', timeout: timeoutMs, maxBuffer: 8 * 1024 * 1024 });
  const value = { id, executable: process.execPath, argv, cwd: root, startedAt: new Date(start).toISOString(), durationMs: Date.now() - start,
    exitCode: result.status, error: result.error?.code ?? null, stdout: result.stdout ?? '', stderr: result.stderr ?? '' };
  checks.push(value); writeFileSync(path.join(directory, `${id}.json`), JSON.stringify(value, null, 2) + '\n', { flag: 'wx' });
  assert.equal(result.status, 0, `Home ${id} failed; retained diagnostic`); assert.ok(!result.error); return value;
}
const tests = run('focused-tests', ['scripts/next/frontend-home-tests.mjs']);
const browser = run('browser', ['scripts/next/frontend-home-browser.mjs']);
run('production-build', ['scripts/next/pnpm.mjs', '--dir', 'ui', 'build']);
function nested(output, expected) {
  const lines = output.stdout.trim().split(/\r?\n/); const result = JSON.parse(lines.at(-1)); assert.equal(result.result, 'passed');
  for (const [key, value] of Object.entries(expected)) assert.equal(result[key], value);
  const relative = path.relative(root, result.receipt).replaceAll('\\', '/'); assert.ok(relative.startsWith('artifacts/next/'));
  const selected = ownedArtifactPath(root, relative), bytes = readFileSync(selected);
  return { path: selected, sha256: createHash('sha256').update(bytes).digest('hex'), bytes: bytes.length };
}
const testReceipt = nested(tests, { tests: 43 }), browserReceipt = nested(browser, { checks: 25, screenshots: 20 });
const graphPath = ownedArtifactPath(root, 'artifacts/next/frontend/production-graph.json'), graphBytes = readFileSync(graphPath);
const graph = JSON.parse(graphBytes); assert.equal(graph.productionMockHostPresent, false); assert.ok(graph.modules.length > 0 && graph.outputs.length > 0);
assert.ok(graph.modules.every(module => !/[/\\](?:mocks|scenarios|fixtures|gallery|home-preview|settings-preview|management-preview)[/\\]|@wdio|webdriver|playwright/i.test(module)));
assert.deepEqual(fingerprintInputRecords(root, inputs), before, 'Home suite inputs changed during qualification');
const receipt = path.join(directory, 'home-suite.json');
writeFileSync(receipt, JSON.stringify({ schemaVersion: 'bridge-frontend-home-suite/v1', result: 'passed', completedAt: new Date().toISOString(),
  sources: before, checks, testReceipt, browserReceipt, productionGraph: { path: graphPath, sha256: createHash('sha256').update(graphBytes).digest('hex') },
  boundary: 'Actual browser-only typed Home/navigation/action custody, 43 exact focused tests, 25 pinned-browser criteria and production graph exclusion. Native routes and webviews qualify separately.',
  rustBuildInvoked: false, nativeRuntimeQualified: false, nativeWebviewQualified: false, releaseQualified: false }, null, 2) + '\n', { flag: 'wx' });
console.log(JSON.stringify({ result: 'passed', tests: 43, browserChecks: 25, receipt, nativeRuntimeQualified: false, releaseQualified: false }));
