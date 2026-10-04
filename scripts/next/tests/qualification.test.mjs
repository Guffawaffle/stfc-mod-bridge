import test from 'node:test';
import assert from 'node:assert/strict';
import { readFileSync, mkdtempSync, mkdirSync, writeFileSync, rmSync, rmdirSync, renameSync } from 'node:fs';
import path from 'node:path';
import os from 'node:os';
import { parseArguments, selectQualification, fingerprintInputs, validateCampaign, QualificationBlocked, controlInputs, qualificationInputs, qualificationPassed, requireStableSourceHead } from '../qualification.mjs';
import { registry } from '../gate-registry.mjs';

const root = path.resolve(import.meta.dirname, '../../..');
const campaign = JSON.parse(readFileSync(path.join(root, 'docs/next/campaign.json')));
const work = JSON.parse(readFileSync(path.join(root, 'docs/plans/rust-tauri-cross-platform/work-packages.json')));
function select(options, overrides = {}) { return selectQualification({ options, packages: work.packages, registry, campaign, actualHost: 'windows-x64', root, cwd: root, ...overrides }); }
const blocked = code => error => error instanceof QualificationBlocked && error.code === code;

test('a moved or unavailable source head blocks otherwise successful qualification', () => {
  const before = '1'.repeat(40), changed = '2'.repeat(40);
  assert.doesNotThrow(() => requireStableSourceHead(before, before));
  assert.throws(() => requireStableSourceHead(before, changed), blocked('SOURCE_HEAD_CHANGED'));
  for (const after of [undefined, '', `${before}\n`, 'not-a-head']) {
    assert.throws(() => requireStableSourceHead(before, after), blocked('SOURCE_HEAD_UNAVAILABLE'));
  }
});

test('campaign binds explicit native scope, owner and mutation policy', () => validateCampaign(campaign, work));
test('strict invocation rejects ambiguous, missing, duplicate and unknown selectors', () => {
  for (const args of [[], ['--package', 'br-00'], ['--package','br-00','--suite','scope-contract','--host','any'], ['--package','br-00','--host','any','--host','any'], ['--package','br-00','--host','any','--bypass','true']]) assert.throws(() => parseArguments(args), blocked('INVALID_ARGUMENTS'));
});
test('package acceptance selects the complete declared suite set', () => {
  const selected = select({ package: 'br-00', host: 'any' });
  assert.deepEqual(selected.suites.map(s => s.id), work.packages.find(p => p.id === 'br-00').gates.map(g => g.id));
  assert.equal(selected.packageAcceptance, true);
  assert.equal(select({ suite: 'scope-contract', host: 'any' }).packageAcceptance, false);
});

test('protocol package requires both actual protocol and complete scenario suites', () => {
  const selected = select({ package: 'br-02', host: 'any' });
  assert.deepEqual(selected.suites.map(s => s.id), ['protocol-contract', 'scenario-catalog']);
  assert.equal(selected.packageAcceptance, true);
  assert.equal(select({ suite: 'protocol-contract', host: 'any' }).packageAcceptance, false);
  const digest = { sha256: 'same' };
  assert.equal(qualificationPassed({ suites: selected.suites, before: digest, after: digest,
    checks: [{ id: 'protocol-contract', exitCode: 0 }] }), false);
  const documented = readFileSync(path.join(root, 'docs/next/PROTOCOL.md'), 'utf8');
  for (const criterion of new Set(selected.suites.flatMap(suite => suite.criteria))) assert.ok(documented.includes(criterion));
});
test('all missing suites block before running any part of a multi-suite package', () => {
  const fixture = structuredClone(work.packages.find(p => p.id === 'br-00'));
  fixture.gates.push({ id: 'missing-native-proof' });
  assert.throws(() => select({ package: 'br-00', host: 'any' }, { packages: [fixture] }), blocked('UNIMPLEMENTED_SUITE'));
});

test('frontend package requires contract and actual browser proof with complete UI sources', () => {
  const selected = select({ package: 'br-03', host: 'any' });
  assert.deepEqual(selected.suites.map(suite => suite.id), ['mock-contract', 'mock-development']);
  const digest = { sha256: 'same' };
  assert.equal(qualificationPassed({ suites: selected.suites, before: digest, after: digest,
    checks: [{ id: 'mock-contract', exitCode: 0 }] }), false);
  const inventory = qualificationInputs(selected.suites);
  for (const input of ['ui/src', 'ui/tests', 'ui/scenarios', 'contracts', 'scripts/next']) assert.ok(inventory.includes(input));
  const documented = readFileSync(path.join(root, 'docs/next/FRONTEND_CLIENT.md'), 'utf8');
  for (const criterion of new Set(selected.suites.flatMap(suite => suite.criteria))) assert.ok(documented.includes(criterion));
});

test('kernel package requires operation and durable crash proof against all core sources', () => {
  const selected = select({ package: 'br-04', host: 'any' });
  assert.deepEqual(selected.suites.map(suite => suite.id), ['operation-contention', 'crash-recovery']);
  const digest = { sha256: 'same' };
  assert.equal(qualificationPassed({ suites: selected.suites, before: digest, after: digest,
    checks: [{ id: 'operation-contention', exitCode: 0 }] }), false);
  const inventory = qualificationInputs(selected.suites);
  for (const input of ['crates/bridge-engine', 'crates/bridge-contracts', 'scripts/next', 'Cargo.lock']) assert.ok(inventory.includes(input));
  const documented = readFileSync(path.join(root, 'docs/next/OPERATION_KERNEL.md'), 'utf8');
  for (const criterion of new Set(selected.suites.flatMap(suite => suite.criteria))) assert.ok(documented.includes(criterion));
});
test('empty, duplicate and malformed suite inventories block before acceptance', () => {
  for (const gates of [[], null, [{ id: 'scope-contract' }, { id: 'scope-contract' }], [{}], [{ id: '../escape' }]]) {
    const fixture = { ...work.packages.find(p => p.id === 'br-00'), gates };
    assert.throws(() => select({ package: 'br-00', host: 'any' }, { packages: [fixture] }), blocked('INVALID_SUITE_INVENTORY'));
  }
  const digest = { sha256: 'same' };
  assert.equal(qualificationPassed({ checks: [], suites: [], before: digest, after: digest }), false);
});

test('Windows platform package requires native host and complete private-fixture sources', () => {
  const selected = select({ package: 'br-06', host: 'windows-x64' });
  assert.deepEqual(selected.suites.map(suite => suite.id), ['windows-platform']);
  assert.equal(selected.packageAcceptance, true);
  assert.throws(() => select({ package: 'br-06', host: 'windows-x64' }, { actualHost: 'macos-arm64-native' }), blocked('WRONG_NATIVE_HOST'));
  const inventory = qualificationInputs(selected.suites);
  for (const item of ['crates/bridge-platform-windows', 'crates/bridge-domain', 'dependencies/next-windows-signature-fixture.json', 'scripts/next', 'Cargo.lock']) assert.ok(inventory.includes(item));
  const documented = readFileSync(path.join(root, 'docs/next/WINDOWS_PLATFORM.md'), 'utf8');
  for (const criterion of selected.suites[0].criteria) assert.ok(documented.includes(criterion));
});

test('selected Mac fixtures require actual Apple Silicon and cannot accept the full Mac package', () => {
  const selected = select({ suite: 'macos-platform-fixtures', host: 'macos-arm64-native' }, { actualHost: 'macos-arm64-native' });
  assert.equal(selected.package.id, 'br-07'); assert.equal(selected.packageAcceptance, false);
  assert.equal(selected.suites[0].packageAcceptanceAvailable, false);
  assert.deepEqual(selected.suites[0].argv, ['scripts/next/macos-platform-fixtures.mjs']);
  assert.throws(() => select({ suite: 'macos-platform-fixtures', host: 'macos-arm64-native' }), blocked('WRONG_NATIVE_HOST'));
  assert.throws(() => select({ package: 'br-07', host: 'macos-arm64-native' }, { actualHost: 'macos-arm64-native' }), blocked('UNIMPLEMENTED_SUITE'));
  const documentary = readFileSync(path.join(root, 'docs/next/MAC_PLATFORM_FIXTURES.md'), 'utf8');
  for (const criterion of selected.suites[0].criteria) assert.ok(documentary.includes(criterion));
  const inputs = qualificationInputs(selected.suites);
  for (const required of ['crates/bridge-platform-macos', 'scripts/next', '.github/workflows/next-foundation.yml']) assert.ok(inputs.includes(required));
});

test('capability projection and Home require their exact suite and independent source inventories', () => {
  for (const [id, suite, document, required] of [
    ['br-13', 'capability-projection', 'DOMAIN_PROJECTIONS.md', ['crates/bridge-domain', 'crates/bridge-contracts', 'Cargo.lock']],
    ['br-18', 'frontend-home-targets', 'FRONTEND_HOME.md', ['ui/src', 'ui/tests/home', 'ui/tests/home-preview', 'ui/home-preview', 'contracts']]
  ]) {
    const selected = select({ package: id, host: 'any' });
    assert.deepEqual(selected.suites.map(value => value.id), [suite]); assert.equal(selected.packageAcceptance, true);
    const inventory = qualificationInputs(selected.suites);
    for (const item of required) assert.ok(inventory.includes(item));
    const documented = readFileSync(path.join(root, `docs/next/${document}`), 'utf8');
    for (const criterion of selected.suites[0].criteria) assert.ok(documented.includes(criterion));
    assert.equal(qualificationPassed({ suites: selected.suites, before: { sha256: 'same' }, after: { sha256: 'same' }, checks: [] }), false);
  }
});
test('configuration source suite cannot accept the package before actual native composition', () => {
  const selected = select({ suite: 'configuration-workspace', host: 'any' });
  assert.equal(selected.packageAcceptance, false);
  assert.deepEqual(selected.suites.map(suite => suite.id), ['configuration-workspace']);
  assert.throws(() => select({ package: 'br-14', host: 'any' }), blocked('PACKAGE_INTEGRATION_UNQUALIFIED'));
  const inputs = qualificationInputs(selected.suites);
  for (const item of ['crates/bridge-engine', 'crates/bridge-contracts', 'crates/bridge-toml', 'Cargo.lock', 'scripts/next/configuration-workspace.mjs']) assert.ok(inputs.includes(item));
  const document = readFileSync(path.join(root, 'docs/next/CONFIGURATION_WORKSPACE.md'), 'utf8');
  for (const criterion of selected.suites[0].criteria) assert.ok(document.includes(criterion));
});

test('portable host foundation requires an actual native probe and cannot accept the Tauri package', () => {
  for (const actualHost of ['windows-x64', 'macos-arm64-native']) {
    const selected = select({ suite: 'host-adapter-foundation', host: actualHost }, { actualHost });
    assert.equal(selected.package.id, 'br-21');
    assert.equal(selected.packageAcceptance, false);
    assert.equal(selected.suites[0].packageAcceptanceAvailable, false);
    assert.deepEqual(selected.observationScope, { kind: 'single-native-host-probe', host: actualHost, requiredHosts: ['windows-x64', 'macos-arm64-native'], matrixAcceptance: false });
    assert.deepEqual(selected.package.dependsOn, ['br-03', 'br-04', 'br-13']);
    const inventory = qualificationInputs(selected.suites);
    for (const input of ['crates/bridge-engine', 'ui/src', 'ui/tests', 'scripts/next', '.github/workflows/next-foundation.yml']) assert.ok(inventory.includes(input));
    const document = readFileSync(path.join(root, 'docs/next/HOST_ADAPTER_FOUNDATION.md'), 'utf8');
    for (const criterion of selected.suites[0].criteria) assert.ok(document.includes(criterion));
  }
  assert.throws(() => select({ suite: 'host-adapter-foundation', host: 'macos-arm64-native' }), blocked('WRONG_NATIVE_HOST'));
  assert.throws(() => select({ suite: 'host-adapter-foundation', host: 'any' }), blocked('WRONG_REQUESTED_HOST'));
  assert.throws(() => select({ package: 'br-21', host: 'native-target-matrix' }), blocked('WRONG_NATIVE_HOST'));
  const packageFixture = { ...work.packages.find(item => item.id === 'br-21'), host: 'any' };
  assert.throws(() => select({ package: 'br-21', host: 'any' }, { packages: [packageFixture] }), blocked('PACKAGE_INTEGRATION_UNQUALIFIED'));
});
test('receipt requires every selected suite to complete successfully in declared order', () => {
  const suites = [{ id: 'first' }, { id: 'second' }];
  const checks = [{ id: 'first', exitCode: 0 }, { id: 'second', exitCode: 0 }];
  const digest = { sha256: 'same' };
  const passed = overrides => qualificationPassed({ checks, suites, before: digest, after: digest, ...overrides });
  assert.equal(passed({}), true);
  assert.equal(passed({ checks: checks.slice(0, 1) }), false);
  assert.equal(passed({ checks: [...checks].reverse() }), false);
  assert.equal(passed({ checks: [checks[0], { ...checks[1], exitCode: 1 }] }), false);
  assert.equal(passed({ checks: [checks[0], { ...checks[1], error: 'timeout' }] }), false);
});
test('mandatory selection graph drift invalidates otherwise successful checks', () => {
  const graphPath = 'docs/plans/rust-tauri-cross-platform/work-packages.json';
  assert.ok(controlInputs.includes(graphPath));
  assert.ok(qualificationInputs([{ inputs: ['AGENTS.md'] }]).includes(graphPath));
  const directory = mkdtempSync(path.join(os.tmpdir(), 'bridge-control-drift-'));
  const parents = ['docs', 'docs/plans', 'docs/plans/rust-tauri-cross-platform'];
  try {
    for (const relative of parents) mkdirSync(path.join(directory, relative));
    writeFileSync(path.join(directory, graphPath), JSON.stringify({ gates: ['first'] }));
    const before = fingerprintInputs(directory, [graphPath]);
    writeFileSync(path.join(directory, graphPath), JSON.stringify({ gates: ['first', 'second'] }));
    const after = fingerprintInputs(directory, [graphPath]);
    assert.equal(qualificationPassed({ checks: [{ id: 'first', exitCode: 0 }], suites: [{ id: 'first' }], before, after }), false);
  } finally {
    rmSync(path.join(directory, graphPath), { force: true });
    for (const relative of [...parents].reverse()) rmdirSync(path.join(directory, relative));
    rmdirSync(directory);
  }
});
test('unknown work, wrong owner, wrong requested host and unbound work refuse qualification', () => {
  assert.throws(() => select({ package: 'missing', host: 'any' }), blocked('UNKNOWN_PACKAGE_OR_SUITE'));
  assert.throws(() => select({ package: 'br-08', host: 'macos-arm64-native' }), blocked('WRONG_OWNER'));
  assert.throws(() => select({ package: 'br-00', host: 'macos-arm64-native' }), blocked('WRONG_REQUESTED_HOST'));
  const unbound = structuredClone(campaign); delete unbound.packages['br-01'];
  assert.throws(() => select({ package: 'br-01', host: 'any' }, { campaign: unbound }), blocked('UNBOUND_WORK_PACKAGE'));
});
test('selected work needs exact numeric owning-repository issue and matching owner', () => {
  for (const binding of [
    { owner: 'Profiles', issue: campaign.packages['br-00'].issue },
    { owner: 'Bridge', issue: 'https://github.com/Guffawaffle/stfc-mod-bridge/issues/not-an-issue' },
    { owner: 'Bridge', issue: 'https://github.com/Guffawaffle/stfc-mod-bridge/issues/232/other' },
    { owner: 'Bridge', issue: 'https://github.com/other/stfc-mod-bridge/issues/232' }
  ]) {
    const changed = structuredClone(campaign); changed.packages['br-00'] = binding;
    assert.throws(() => select({ package: 'br-00', host: 'any' }, { campaign: changed }), blocked('UNBOUND_WORK_PACKAGE'));
  }
});
test('Windows cannot qualify native Mac or a two-host native matrix', () => {
  const fixture = structuredClone(work.packages.find(p => p.id === 'br-00'));
  for (const host of ['macos-arm64-native', 'native-target-matrix']) {
    fixture.host = host;
    assert.throws(() => select({ package: 'br-00', host }, { packages: [fixture] }), blocked('WRONG_NATIVE_HOST'));
  }
});

test('an explicit matrix suite collects only one matching host and never package acceptance', () => {
  const fixture = { ...structuredClone(work.packages.find(p => p.id === 'br-00')), host: 'native-target-matrix', requiredHosts: ['windows-x64', 'macos-arm64-native'] };
  const definitions = { 'scope-contract': { ...registry['scope-contract'], nativeProbeHosts: fixture.requiredHosts } };
  for (const actualHost of fixture.requiredHosts) {
    const observation = select({ suite: 'scope-contract', host: actualHost }, { packages: [fixture], registry: definitions, actualHost });
    assert.equal(observation.packageAcceptance, false);
    assert.deepEqual(observation.observationScope, { kind: 'single-native-host-probe', host: actualHost, requiredHosts: fixture.requiredHosts, matrixAcceptance: false });
    assert.throws(() => select({ package: 'br-00', host: actualHost }, { packages: [fixture], registry: definitions, actualHost }), blocked('WRONG_REQUESTED_HOST'));
  }
  assert.throws(() => select({ suite: 'scope-contract', host: 'macos-arm64-native' }, { packages: [fixture], registry: definitions }), blocked('WRONG_NATIVE_HOST'));
  assert.throws(() => select({ suite: 'scope-contract', host: 'native-target-matrix' }, { packages: [fixture], registry: definitions }), blocked('WRONG_NATIVE_HOST'));
});

test('a matrix host probe requires exact two-host package and executable declarations', () => {
  const fixture = { ...structuredClone(work.packages.find(p => p.id === 'br-00')), host: 'native-target-matrix', requiredHosts: ['windows-x64', 'macos-arm64-native'] };
  const options = { suite: 'scope-contract', host: 'windows-x64' };
  for (const requiredHosts of [undefined, [], ['windows-x64'], ['windows-x64', 'windows-x64'], ['windows-x64', 'linux-x64']]) {
    assert.throws(() => select(options, { packages: [{ ...fixture, requiredHosts }] }), blocked('INVALID_HOST_MATRIX'));
  }
  for (const nativeProbeHosts of [undefined, [], ['windows-x64'], ['windows-x64', 'windows-x64'], ['windows-x64', 'linux-x64']]) {
    assert.throws(() => select(options, { packages: [fixture], registry: { 'scope-contract': { ...registry['scope-contract'], nativeProbeHosts } } }), blocked('UNIMPLEMENTED_HOST_PROBE'));
  }
});

test('native consumer qualification remains an explicit partial suite with complete native source inputs', () => {
  const selected = select({ suite: 'native-ffi-contract', host: 'windows-x64' });
  assert.equal(selected.package.id, 'br-05');
  assert.equal(selected.packageAcceptance, false);
  assert.equal(selected.observationScope.matrixAcceptance, false);
  assert.throws(() => select({ package: 'br-05', host: 'native-target-matrix' }), blocked('WRONG_NATIVE_HOST'));
  const inventory = qualificationInputs(selected.suites);
  for (const input of ['crates/bridge-native', 'crates/bridge-profiles', 'crates/bridge-toml', 'dependencies/next-native-inputs.json', 'scripts/next', 'Cargo.lock']) assert.ok(inventory.includes(input));
  const documented = readFileSync(path.join(root, 'docs/next/NATIVE_CONSUMERS.md'), 'utf8');
  for (const criterion of selected.suites.flatMap(suite => suite.criteria)) assert.ok(documented.includes(criterion));
});

test('component acceptance needs actual component and browser suites with facade and gallery inputs', () => {
  const selected = select({ package: 'br-12', host: 'any' });
  assert.deepEqual(selected.suites.map(suite => suite.id), ['frontend-components', 'frontend-accessibility']);
  const digest = { sha256: 'same' };
  assert.equal(qualificationPassed({ suites: selected.suites, before: digest, after: digest, checks: [{ id: 'frontend-components', exitCode: 0 }] }), false);
  for (const input of ['ui/src', 'ui/tests/components', 'ui/gallery', 'contracts', 'scripts/next']) assert.ok(qualificationInputs(selected.suites).includes(input));
  const documented = readFileSync(path.join(root, 'docs/next/FRONTEND_COMPONENTS.md'), 'utf8');
  for (const criterion of selected.suites.flatMap(suite => suite.criteria)) assert.ok(documented.includes(criterion));
});
test('a native suite cannot be smuggled into an any-host package', () => {
  assert.throws(() => select({ package: 'br-00', host: 'any' }, { registry: { 'scope-contract': { ...registry['scope-contract'], host: 'macos-arm64-native' } } }), blocked('WRONG_NATIVE_HOST'));
});
test('an unrelated routing cwd cannot supply the candidate repository', () => {
  assert.throws(() => select({ package: 'br-00', host: 'any' }, { cwd: os.tmpdir() }), blocked('WRONG_CWD'));
});
test('input digests are stable, deduplicated, content-sensitive and confined', () => {
  const directory = mkdtempSync(path.join(os.tmpdir(), 'bridge-qualification-'));
  try {
    writeFileSync(path.join(directory, 'input.json'), '{"revision":1}');
    const first = fingerprintInputs(directory, ['input.json','input.json']);
    assert.equal(first.files.length, 1);
    assert.equal(first.sha256, fingerprintInputs(directory, ['input.json']).sha256);
    writeFileSync(path.join(directory, 'input.json'), '{"revision":2}');
    assert.notEqual(first.sha256, fingerprintInputs(directory, ['input.json']).sha256);
    assert.throws(() => fingerprintInputs(directory, ['../escaped']), blocked('INPUT_ESCAPE'));
  } finally { rmSync(path.join(directory, 'input.json'), { force: true }); rmdirSync(directory); }
});
test('scope gate inventories real contract documents and every documentary criterion', () => {
  const inputs = fingerprintInputs(root, registry['scope-contract'].inputs);
  assert.equal(inputs.files.length, registry['scope-contract'].inputs.length);
  const contract = readFileSync(path.join(root, 'docs/next/OPERATING_CONTRACT.md'), 'utf8');
  for (const criterion of registry['scope-contract'].criteria) assert.ok(contract.includes(criterion), `Missing documented criterion ${criterion}`);
  const scenarios = readFileSync(path.join(root, 'docs/next/SCENARIOS.md'), 'utf8');
  for (const id of ['SC-01','SC-02','SC-03','SC-04','SC-05','SC-06','SC-07','SC-08','SC-09','SC-10','SC-11','SC-12','SC-13','SC-14','SC-15','SC-16','SC-17','SC-18']) assert.ok(scenarios.includes(id), `Missing scenario ${id}`);
});

test('a new source file or rename invalidates package evidence even when existing bytes are unchanged', () => {
  const directory = mkdtempSync(path.join(os.tmpdir(), 'bridge-source-drift-'));
  const source = path.join(directory, 'src');
  mkdirSync(source);
  writeFileSync(path.join(source, 'existing.rs'), 'Existing synthetic source.');
  const checks = [{ id: 'build', exitCode: 0 }];
  const suites = [{ id: 'build' }];
  try {
    const before = fingerprintInputs(directory, ['src']);
    assert.equal(qualificationPassed({ checks, suites, before, after: fingerprintInputs(directory, ['src']) }), true);
    writeFileSync(path.join(source, 'added.rs'), 'New synthetic source.');
    assert.equal(qualificationPassed({ checks, suites, before, after: fingerprintInputs(directory, ['src']) }), false);
    const added = fingerprintInputs(directory, ['src']);
    renameSync(path.join(source, 'added.rs'), path.join(source, 'renamed.rs'));
    assert.equal(qualificationPassed({ checks, suites, before: added, after: fingerprintInputs(directory, ['src']) }), false);
    const renamed = fingerprintInputs(directory, ['src']);
    rmSync(path.join(source, 'renamed.rs'));
    assert.equal(qualificationPassed({ checks, suites, before: renamed, after: fingerprintInputs(directory, ['src']) }), false);
    assert.equal(fingerprintInputs(directory, ['src']).sha256, before.sha256);
  } finally {
    for (const file of ['existing.rs', 'added.rs', 'renamed.rs']) rmSync(path.join(source, file), { force: true });
    rmdirSync(source);
    rmdirSync(directory);
  }
});

test('foundation inventories whole source and fixture trees and excludes generated build directories', () => {
  const inputs = registry['workspace-foundation'].inputs;
  for (const tree of ['scripts/next', 'crates', 'contracts', 'ui/src', 'ui/tests', 'ui/gallery', 'ui/home-preview', 'apps/desktop/src-tauri/src', 'apps/desktop/src-tauri/capabilities']) assert.ok(inputs.includes(tree), `Missing source-tree descriptor ${tree}`);
  assert.ok(registry['mock-development'].inputs.includes('ui/home-preview'));
  for (const generated of ['target', 'node_modules', 'ui/dist', 'artifacts']) assert.ok(!inputs.some(input => input === generated || input.startsWith(`${generated}/`)));
});
