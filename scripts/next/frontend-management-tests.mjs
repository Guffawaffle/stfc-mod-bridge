import assert from 'node:assert/strict';
import { spawnSync } from 'node:child_process';
import { createHash, randomUUID } from 'node:crypto';
import { existsSync, mkdirSync, readFileSync, readdirSync, realpathSync, writeFileSync } from 'node:fs';
import { createRequire } from 'node:module';
import os from 'node:os';
import path from 'node:path';
import { fingerprintInputRecords } from './input-tree.mjs';
import { ownedArtifactPath } from './owned-artifact.mjs';
import { managementCounts, managementCriteria, managementEvidence, managementEvidenceControls } from './frontend-management-evidence.mjs';

const root = path.resolve(import.meta.dirname, '../..');
assert.equal(realpathSync(process.cwd()), realpathSync(root), 'Run Management tests from their canonical owning checkout');
assert.equal(process.argv.length, 2, 'Management tests accept no caller selection or report override');
assert.deepEqual(process.execArgv, [], 'Management tests accept no Node loader or runtime override');
const manifest = JSON.parse(readFileSync(ownedArtifactPath(root, 'package.json'), 'utf8'));
const uiManifest = JSON.parse(readFileSync(ownedArtifactPath(root, 'ui/package.json'), 'utf8'));
const pins = JSON.parse(readFileSync(ownedArtifactPath(root, 'dependencies/next-toolchain.json'), 'utf8'));
assert.equal(process.version, `v${manifest.engines.node}`);
assert.equal(manifest.engines.node, pins.node); assert.equal(manifest.devDependencies.pnpm, pins.pnpm);
for (const [name, value] of Object.entries(process.env)) {
  assert.ok(!value || !/^(?:NODE_OPTIONS|NODE_PATH|VITEST_.*|VITE_.*|TS_NODE_.*|TSX_.*|ESBUILD_BINARY_PATH|ROLLDOWN_BINDING_PATH|NAPI_RS_NATIVE_LIBRARY_PATH|NPM_CONFIG_(?:NODE_OPTIONS|SCRIPT_SHELL|USERCONFIG|GLOBALCONFIG)|BRIDGE_FIXTURE_.*|BRIDGE_MANAGEMENT_.*)$/i.test(name),
    `Management tests refuse caller tooling or fixture routing: ${name}`);
}
const directoryRelative = `artifacts/next/frontend-management-tests/${randomUUID()}`;
const directory = ownedArtifactPath(root, directoryRelative, 'directory', { allowMissing: true });
mkdirSync(directory, { recursive: true });
const sha256 = bytes => createHash('sha256').update(bytes).digest('hex');
const relative = selected => path.relative(root, selected).replaceAll('\\', '/');
const artifact = (name, allowMissing = false) => ownedArtifactPath(root, `${directoryRelative}/${name}`, 'file', { allowMissing });
const sources = [
  'package.json', 'pnpm-lock.yaml', 'pnpm-workspace.yaml', 'dependencies/next-toolchain.json',
  'ui/package.json', 'ui/tsconfig.json', 'ui/vite.config.ts', 'ui/svelte.config.js', 'ui/src', 'ui/tests', 'ui/scenarios',
  'contracts/fixtures', 'contracts/codec', 'docs/next/FRONTEND_MANAGEMENT.md',
  'assets/stfc-mod-bridge.png',
  'scripts/next/frontend-management-tests.mjs', 'scripts/next/frontend-management-evidence.mjs',
  'scripts/next/test-evidence.mjs', 'scripts/next/input-tree.mjs', 'scripts/next/owned-artifact.mjs',
  'scripts/next/owned-artifact.d.mts', 'scripts/next/pnpm.mjs',
];
function sourceInventory() {
  // UI tests can import preview modules outside src/tests. Include every current
  // UI source entry, with an explicit exclusion for dependencies/build output,
  // rather than treating the tsconfig include list as a dependency closure.
  const uiEntries = readdirSync(ownedArtifactPath(root, 'ui', 'directory')).sort();
  const selectedUiEntries = uiEntries.filter(name => !['node_modules', 'dist'].includes(name)).map(name => `ui/${name}`);
  return { selectedUiEntries, excludedUiEntries: uiEntries.filter(name => ['node_modules', 'dist'].includes(name)),
    records: fingerprintInputRecords(root, [...sources, ...selectedUiEntries]) };
}

// Observe actual installed tool resolution and bytes. Package links resolve once
// to a confined physical package; input-tree then refuses links within its body.
// Linked dependency containers are represented by their recursively resolved
// package graph, rather than traversed as an unbounded node_modules tree.
function tools() {
  const modules = ownedArtifactPath(root, 'node_modules', 'directory');
  const withinModules = selected => {
    const relation = path.relative(modules, selected);
    assert.ok(relation && relation !== '..' && !relation.startsWith(`..${path.sep}`) && !path.isAbsolute(relation), 'Installed tool escaped owning node_modules');
  };
  const packages = new Map();
  function resolvePackage(name, from, optional = false) {
    const resolver = createRequire(from);
    for (const folder of resolver.resolve.paths(name) ?? []) {
      const candidate = path.join(folder, name, 'package.json');
      if (existsSync(candidate)) {
        const actual = realpathSync(candidate); withinModules(actual); return actual;
      }
    }
    if (optional) return undefined;
    assert.fail(`Required installed tool dependency is unavailable: ${name}`);
  }
  function observePackage(selected, expectedName, expectedVersion) {
    const physical = realpathSync(selected); withinModules(physical);
    const packageRoot = path.dirname(physical), key = relative(packageRoot);
    const value = JSON.parse(readFileSync(physical, 'utf8'));
    if (expectedName) assert.equal(value.name, expectedName);
    if (expectedVersion) assert.equal(value.version, expectedVersion, `Installed tool differs from its pin: ${expectedName}`);
    if (packages.has(key)) return key;
    assert.ok(typeof value.name === 'string' && typeof value.version === 'string');
    const selectors = readdirSync(packageRoot).filter(name => name !== 'node_modules').sort();
    const observed = { path: key, name: value.name, version: value.version,
      inputs: fingerprintInputRecords(packageRoot, selectors), dependencies: [] };
    packages.set(key, observed);
    const dependencies = new Map(Object.entries(value.dependencies ?? {}).map(([name]) => [name, false]));
    for (const name of Object.keys(value.optionalDependencies ?? {})) dependencies.set(name, true);
    for (const [name, optional] of [...dependencies].sort(([left], [right]) => left.localeCompare(right))) {
      const resolved = resolvePackage(name, physical, optional);
      observed.dependencies.push({ name, optional, ...(resolved ? { package: observePackage(resolved) } : { unavailable: true }) });
    }
    return key;
  }
  const seeds = [
    ['pnpm', manifest.devDependencies.pnpm, path.join(root, 'package.json')],
    ...['vitest', 'vite', 'svelte-check', 'typescript', '@sveltejs/vite-plugin-svelte', '@types/node', 'svelte', '@tauri-apps/api']
      .map(name => [name, uiManifest.devDependencies?.[name] ?? uiManifest.dependencies?.[name], path.join(root, 'ui/package.json')]),
  ];
  const resolvedSeeds = seeds.map(([name, version, from]) => {
    assert.ok(typeof version === 'string' && /^\d+\.\d+\.\d+$/.test(version), `Tool seed requires an exact pin: ${name}`);
    return { name, version, package: observePackage(resolvePackage(name, from), name, version) };
  });
  const executable = realpathSync(process.execPath), executableRoot = path.dirname(executable);
  const shimRoot = ownedArtifactPath(root, 'ui/node_modules/.bin', 'directory');
  return { node: { executable, version: process.version, inputs: fingerprintInputRecords(executableRoot, [path.basename(executable)]) },
    seeds: resolvedSeeds, packages: [...packages.values()].sort((left, right) => left.path.localeCompare(right.path)),
    uiCommandShims: { root: relative(shimRoot), inputs: fingerprintInputRecords(shimRoot, readdirSync(shimRoot).sort()) } };
}

const startedAt = new Date().toISOString(), checks = [];
let before, after, toolsBefore, toolsAfter, inventory, parserControls, reportRecord, error;
function run(id, argv) {
  const start = Date.now();
  const result = spawnSync(process.execPath, argv, { cwd: root, windowsHide: true, env: process.env,
    encoding: 'utf8', timeout: 180000, maxBuffer: 8 * 1024 * 1024 });
  const observation = { id, executable: process.execPath, argv, cwd: root, startedAt: new Date(start).toISOString(),
    durationMs: Date.now() - start, exitCode: result.status, error: result.error?.code ?? null, signal: result.signal ?? null,
    stdout: result.stdout ?? '', stderr: result.stderr ?? '' };
  const bytes = Buffer.from(JSON.stringify(observation, null, 2) + '\n');
  writeFileSync(artifact(`${id}.json`, true), bytes, { flag: 'wx' });
  checks.push({ id, path: `${directoryRelative}/${id}.json`, sha256: sha256(bytes), bytes: bytes.length, exitCode: result.status });
  assert.equal(result.status, 0, `Management ${id} failed; inspect retained observation`); assert.ok(!result.error);
  return observation.stdout;
}
try {
  before = sourceInventory(); toolsBefore = tools();
  parserControls = managementEvidenceControls(root);
  const controlBytes = Buffer.from(JSON.stringify(parserControls, null, 2) + '\n');
  writeFileSync(artifact('receipt-controls.json', true), controlBytes, { flag: 'wx' });
  parserControls = { path: `${directoryRelative}/receipt-controls.json`, sha256: sha256(controlBytes), controls: parserControls.controls };
  assert.match(run('types', ['scripts/next/pnpm.mjs', '--dir', 'ui', 'check']), /svelte-check found 0 errors and 0 warnings/);
  const reportPath = artifact('vitest.json', true);
  run('focused-tests', ['scripts/next/pnpm.mjs', '--dir', 'ui', 'exec', 'vitest', 'run',
    ...Object.keys(managementCriteria).map(file => file.slice(3)), '--maxWorkers=1', '--reporter=json', `--outputFile=${reportPath}`]);
  const reportBytes = readFileSync(artifact('vitest.json'));
  reportRecord = { path: `${directoryRelative}/vitest.json`, sha256: sha256(reportBytes), bytes: reportBytes.length };
  inventory = managementEvidence(JSON.parse(reportBytes.toString('utf8')), root);
  after = sourceInventory(); toolsAfter = tools();
  assert.deepEqual(after, before, 'Management source inputs changed during the run');
  assert.deepEqual(toolsAfter, toolsBefore, 'Actual Management tool resolution or bytes changed during the run');
} catch (failure) {
  error = { name: failure?.name ?? 'Error', code: failure?.code ?? null, message: String(failure?.message ?? 'Management check failed').slice(0, 1024) };
  try { after ??= sourceInventory(); } catch (failure) { error.inputObservation = failure?.code ?? failure?.name ?? 'unavailable'; }
  try { toolsAfter ??= tools(); } catch (failure) { error.toolObservation = failure?.code ?? failure?.name ?? 'unavailable'; }
}
const result = error ? 'failed' : 'passed';
const receipt = { schemaVersion: 'bridge-frontend-management-tests/v1', result, startedAt, completedAt: new Date().toISOString(),
  host: { platform: process.platform, architecture: process.arch, osRelease: os.release(), node: process.version },
  checks, sources: { selectors: sources, before, after }, tools: { before: toolsBefore, after: toolsAfter },
  parserControls, required: managementCounts, inventory, report: reportRecord, ...(error ? { error } : {}),
  boundary: 'Actual Svelte/TypeScript source check and exact focused controller/client/presentation/preview-session tests over closed synthetic Rust-derived DTOs. Receipt-parser controls are synthetic evidence-format validation. Browser interaction, native backend, native chooser/export, installed webview, assistive technology and release acceptance are separate.',
  browserTestsExecuted: false, rustBuildInvoked: false, nativeRuntimeQualified: false, nativeWebviewQualified: false,
  nativeAssistiveTechnologyQualified: false, engineExecutionQualified: false, releaseQualified: false };
const receiptBytes = Buffer.from(JSON.stringify(receipt, null, 2) + '\n');
writeFileSync(artifact('management-tests.json', true), receiptBytes, { flag: 'wx' });
console.log(JSON.stringify({ result, ...(inventory ? { tests: inventory.tests, files: inventory.files.length } : {}),
  receipt: artifact('management-tests.json'), receiptSha256: sha256(receiptBytes), browserTestsExecuted: false,
  nativeRuntimeQualified: false, releaseQualified: false }));
if (error) process.exitCode = 1;
