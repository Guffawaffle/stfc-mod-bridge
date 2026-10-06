import assert from 'node:assert/strict';
import { spawnSync } from 'node:child_process';
import { createHash, randomUUID } from 'node:crypto';
import { mkdirSync, readFileSync, readdirSync, realpathSync, writeFileSync } from 'node:fs';
import path from 'node:path';
import { fingerprintInputRecords } from './input-tree.mjs';
import { ownedArtifactPath } from './owned-artifact.mjs';

const root = path.resolve(import.meta.dirname, '../..');
const sha256 = bytes => createHash('sha256').update(bytes).digest('hex');
const relative = selected => path.relative(root, selected).replaceAll('\\', '/');

/** Compose fixed source and browser drivers; callers cannot select cases or receipts. */
export function runFrontendSuite({ name, document, focused, browser }) {
  assert.equal(realpathSync(process.cwd()), realpathSync(root));
  assert.equal(process.argv.length, 2, 'Frontend suites accept no caller overrides');
  assert.deepEqual(process.execArgv, [], 'Frontend suites accept no Node loader overrides');
  assert.match(name, /^frontend-(?:settings|management)$/);
  const manifest = JSON.parse(readFileSync(ownedArtifactPath(root, 'package.json'), 'utf8'));
  assert.equal(process.version, `v${manifest.engines.node}`);
  for (const [key, value] of Object.entries(process.env)) {
    assert.ok(!value || !/^(?:NODE_OPTIONS|NODE_PATH|VITEST_.*|VITE_.*|TS_NODE_.*|TSX_.*|ESBUILD_BINARY_PATH|ROLLDOWN_BINDING_PATH|NAPI_RS_NATIVE_LIBRARY_PATH|NPM_CONFIG_(?:NODE_OPTIONS|SCRIPT_SHELL|USERCONFIG|GLOBALCONFIG)|BRIDGE_FIXTURE_.*|BRIDGE_(?:SETTINGS|MANAGEMENT)_.*)$/i.test(key),
      `Frontend suites refuse caller tooling or fixture routing: ${key}`);
  }
  const directoryRelative = `artifacts/next/${name}-suite/${randomUUID()}`;
  const directory = ownedArtifactPath(root, directoryRelative, 'directory', { allowMissing: true });
  mkdirSync(directory, { recursive: true });
  const checks = [], startedAt = new Date().toISOString();
  const inputs = ['package.json', 'pnpm-lock.yaml', 'pnpm-workspace.yaml', 'dependencies/next-toolchain.json',
    document, 'contracts', 'scripts/next', 'assets/stfc-mod-bridge.png'];
  function sources() {
    const entries = readdirSync(ownedArtifactPath(root, 'ui', 'directory')).sort();
    const selected = entries.filter(entry => !['node_modules', 'dist'].includes(entry));
    return { uiEntries: selected, records: fingerprintInputRecords(root, [...inputs, ...selected.map(entry => `ui/${entry}`)]) };
  }
  function node() {
    const bytes = readFileSync(process.execPath);
    return { path: process.execPath, version: process.version, bytes: bytes.length, sha256: sha256(bytes) };
  }
  const before = sources(), nodeBefore = node();
  function run(id, argv, timeout = 300000) {
    const start = Date.now();
    const result = spawnSync(process.execPath, argv, { cwd: root, windowsHide: true, encoding: 'utf8', timeout, maxBuffer: 8 * 1024 * 1024 });
    const observed = { id, executable: process.execPath, argv, cwd: root, startedAt: new Date(start).toISOString(),
      durationMs: Date.now() - start, exitCode: result.status, error: result.error?.code ?? null, stdout: result.stdout ?? '', stderr: result.stderr ?? '' };
    checks.push(observed);
    writeFileSync(path.join(directory, `${id}.json`), JSON.stringify(observed, null, 2) + '\n', { flag: 'wx' });
    assert.equal(result.status, 0, `${name} ${id} failed; inspect retained observation`);
    assert.ok(!result.error);
    return observed;
  }
  function nested(observed, specification, kind) {
    const result = JSON.parse(observed.stdout.trim().split(/\r?\n/).at(-1));
    assert.equal(result.result, 'passed');
    assert.equal(result.nativeRuntimeQualified, false); assert.equal(result.releaseQualified, false);
    assert.ok(typeof result.receipt === 'string' && path.isAbsolute(result.receipt));
    const selectedRelative = relative(result.receipt);
    assert.ok(selectedRelative.startsWith(`artifacts/next/${name}-${kind}/`), 'Nested receipt escaped its fixed driver output');
    const selected = ownedArtifactPath(root, selectedRelative), bytes = readFileSync(selected), value = JSON.parse(bytes);
    assert.equal(value.schemaVersion, specification.schema); assert.equal(value.result, 'passed');
    assert.equal(value.nativeRuntimeQualified, false); assert.equal(value.releaseQualified, false);
    if (kind === 'tests') {
      assert.equal(result.tests, specification.tests); assert.equal(value.inventory.tests, specification.tests);
      assert.equal(value.inventory.files.length, specification.files);
    } else {
      assert.equal(result.checks, specification.checks); assert.equal(value.observations.length, specification.checks);
      assert.deepEqual(value.observations.map(entry => entry.id), value.expected);
      assert.equal(new Set(value.expected).size, specification.checks);
      assert.ok(value.observations.every(entry => entry.result === 'passed'));
      assert.equal(value.ownedBrowserAndServerCleanupCompleted, true);
      assert.deepEqual(value.browserErrors, []); assert.deepEqual(value.externalRequests, []);
      assert.equal(result.screenshots, value.screenshots.length); assert.ok(value.screenshots.length > 0);
      for (const screenshot of value.screenshots) {
        assert.ok(screenshot.path.startsWith(`artifacts/next/${name}-browser/`));
        const pixels = readFileSync(ownedArtifactPath(root, screenshot.path));
        assert.equal(pixels.length, screenshot.bytes); assert.equal(sha256(pixels), screenshot.sha256);
      }
    }
    return { path: selectedRelative, bytes: bytes.length, sha256: sha256(bytes) };
  }
  let result = 'failed', testReceipt, browserReceipt, productionGraph, after, nodeAfter, failure;
  try {
    testReceipt = nested(run('focused-tests', [focused.driver]), focused, 'tests');
    browserReceipt = nested(run('browser', [browser.driver]), browser, 'browser');
    run('production-build', ['scripts/next/pnpm.mjs', '--dir', 'ui', 'build'], 180000);
    const graphPath = ownedArtifactPath(root, 'artifacts/next/frontend/production-graph.json'), graphBytes = readFileSync(graphPath);
    const graph = JSON.parse(graphBytes);
    assert.equal(graph.productionMockHostPresent, false); assert.ok(graph.modules.length > 0 && graph.outputs.length > 0);
    assert.ok(graph.modules.every(module => !/[/\\](?:mocks|scenarios|fixtures|tests|gallery|home-preview|settings-preview|management-preview)[/\\]|@wdio|webdriver|playwright/i.test(module)));
    productionGraph = { path: relative(graphPath), bytes: graphBytes.length, sha256: sha256(graphBytes) };
    after = sources(); nodeAfter = node();
    assert.deepEqual(after, before, 'Frontend suite source or directory membership changed during execution');
    assert.deepEqual(nodeAfter, nodeBefore, 'Frontend suite Node bytes changed during execution');
    result = 'passed';
  } catch (error) {
    failure = { name: error.name, code: error.code ?? null, message: error.message };
  }
  const receipt = path.join(directory, 'suite.json');
  const bytes = Buffer.from(JSON.stringify({ schemaVersion: `bridge-${name}-suite/v1`, result, startedAt, completedAt: new Date().toISOString(),
    checks, sources: { before, after }, node: { before: nodeBefore, after: nodeAfter }, testReceipt, browserReceipt, productionGraph, failure,
    boundary: 'Actual fixed frontend tests, private pinned browser journeys and production graph exclusion. Synthetic backend composition; native services and installed webviews qualify separately.',
    rustBuildInvoked: false, nativeWebviewQualified: false, nativeRuntimeQualified: false, releaseQualified: false }, null, 2) + '\n');
  writeFileSync(receipt, bytes, { flag: 'wx' });
  console.log(JSON.stringify({ result, tests: focused.tests, browserChecks: browser.checks, receipt, receiptSha256: sha256(bytes),
    nativeRuntimeQualified: false, releaseQualified: false }));
  if (result !== 'passed') process.exitCode = 1;
}
