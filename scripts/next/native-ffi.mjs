import assert from 'node:assert/strict';
import { spawnSync } from 'node:child_process';
import { mkdirSync, readFileSync, realpathSync, writeFileSync } from 'node:fs';
import { createHash, randomUUID } from 'node:crypto';
import path from 'node:path';
import { rustContext } from './rust-context.mjs';
import { ownedArtifactPath } from './owned-artifact.mjs';
import { nativeCriteria, nativeMarker, nativeTestInventory, nativeTestResult, nativePhysicalPath } from './native-evidence.mjs';
import { nodeTestEvidence } from './test-evidence.mjs';

const root = path.resolve(import.meta.dirname, '../..');
assert.equal(realpathSync(process.cwd()), realpathSync(root), 'Use the canonical Bridge checkout');
assert.equal(process.argv.length, 2, 'Native probe accepts no arbitrary manifest or fixture selectors');
const rust = rustContext({ root });
const host = process.platform === 'win32' ? 'windows-x64' : 'macos-arm64-native';
const sha = bytes => createHash('sha256').update(bytes).digest('hex');
const manifestPath = ownedArtifactPath(root, 'dependencies/next-native-inputs.json');
const manifestBytes = readFileSync(manifestPath);
assert.ok(manifestBytes.length <= 65536);
const manifest = JSON.parse(manifestBytes);
assert.equal(manifest.schemaVersion, 'bridge-native-probe-inputs/v1');
assert.equal(manifest.owningRepository, 'Guffawaffle/stfc-mod-bridge');
assert.equal(manifest.nativeRuntimeQualified, false);
assert.equal(manifest.releaseQualified, false);
const modules = manifest.modules.filter(row => row.host === host);
assert.equal(modules.length, 2, 'Both actual host producer inputs must be explicitly adopted');
assert.deepEqual(modules.map(row => row.component).sort(), ['profiles', 'toml']);
const observations = modules.map(row => {
  assert.equal(row.symbolAbi, 1);
  assert.equal(row.releaseInputQualified, false, 'This gate only adopts probe inputs, never release inputs');
  assert.equal(row.producer, row.component === 'profiles' ? 'Guffawaffle/stfc-profiles' : 'Guffawaffle/stfc-mod');
  if (row.component === 'profiles') assert.equal(row.jsonApi, 2);
  else assert.equal(row.componentPath, 'shared/toml');
  const modulePath = ownedArtifactPath(root, row.relativePath);
  const moduleBytes = readFileSync(modulePath);
  assert.ok(moduleBytes.length > 0 && moduleBytes.length <= 128 * 1024 * 1024);
  assert.equal(sha(moduleBytes), row.sha256);
  const receiptPath = ownedArtifactPath(root, row.buildReceipt);
  const receiptBytes = readFileSync(receiptPath);
  assert.ok(receiptBytes.length > 0 && receiptBytes.length <= 65536);
  assert.equal(sha(receiptBytes), row.provenance.buildReceiptSha256);
  const receipt = JSON.parse(receiptBytes);
  assert.equal(receipt.repository, row.producer);
  assert.equal(receipt.sourceRevision, row.provenance.sourceRevision);
  assert.equal(receipt.sourceArchiveSha256, row.provenance.sourceArchiveSha256);
  assert.equal(receipt.nativeSha256, row.sha256);
  assert.equal(receipt.developmentOverride, false);
  assert.deepEqual(receipt.dirtyFiles, []);
  assert.equal(receipt.target, `${host === 'windows-x64' ? 'windows-x64' : 'macos-arm64'}-release`);
  return { ...row, physicalPath: modulePath, bytes: moduleBytes.length, buildReceiptPhysicalPath: receiptPath, buildReceiptBytes: receiptBytes.length };
});
const directory = ownedArtifactPath(root, `artifacts/next/native-ffi/${randomUUID()}`, 'directory', { allowMissing: true });
mkdirSync(directory, { recursive: true });
const fixture = ownedArtifactPath(root, `${path.relative(root, directory).replaceAll('\\', '/')}/fixtures`, 'directory', { allowMissing: true });
mkdirSync(fixture);
const env = { ...process.env, BRIDGE_TEST_NATIVE_MANIFEST: manifestPath, BRIDGE_TEST_NATIVE_FIXTURE_ROOT: fixture };
const checks = [], binaries = [], executions = [];
function run(id, executable, argv, timeout = 180000) {
  const result = spawnSync(executable, argv, { cwd: root, windowsHide: true, env, encoding: 'utf8', timeout, maxBuffer: 8 * 1024 * 1024 });
  const check = { id, executable, argv, cwd: root, exitCode: result.status, error: result.error?.code ?? null, stdout: result.stdout ?? '', stderr: result.stderr ?? '' };
  checks.push(check);
  writeFileSync(path.join(directory, `${id}.json`), JSON.stringify(check, null, 2) + '\n');
  assert.equal(result.status, 0, `Native ${id} failed; inspect its retained observation`);
  assert.ok(!result.error);
  return check.stdout;
}
const cargo = (id, argv) => run(id, process.execPath, ['scripts/next/cargo.mjs', ...argv]);
const evidenceFile = 'scripts/next/tests/native-evidence.test.mjs';
const evidenceNames = [
  'native inventories refuse absent extra duplicate renamed and empty criterion tests',
  'native summaries require actual passed counts with no ignored measured or hidden filtered cases',
  'producer observation markers refuse missing ambiguous or invalid structured output',
  'physical native paths accept equivalent namespaces and refuse missing or foreign origins'
];
const evidence = nodeTestEvidence(run('gate-evidence-regressions', process.execPath,
  ['--test', '--test-reporter=./scripts/next/node-test-reporter.mjs', evidenceFile]), { root, file: evidenceFile, requiredNames: evidenceNames });
assert.equal(evidence.tests, evidenceNames.length);
for (const component of ['native', 'profiles', 'toml']) {
  const packageId = `bridge-${component}`;
  cargo(`format-${component}`, ['fmt', '-p', packageId, '--', '--check']);
  cargo(`clippy-${component}`, ['clippy', '--locked', '-p', packageId, '--all-targets', '--', '-D', 'warnings']);
  const output = cargo(`docs-${component}`, ['test', '--locked', '-p', packageId, '--doc']);
  nativeTestResult(output, component === 'profiles' ? 6 : 2);
}
function compile(component, selection, target, kind, source) {
  const packageId = `bridge-${component}`;
  const output = cargo(`compile-${component}-${target}`, ['test', '--locked', '-p', packageId, ...selection, '--no-run', '--message-format', 'json']);
  const artifacts = output.split(/\r?\n/).filter(Boolean).map(line => JSON.parse(line)).filter(message =>
    message.reason === 'compiler-artifact' && message.target.name === target && message.target.kind.includes(kind) && message.profile.test === true && message.executable);
  assert.equal(artifacts.length, 1, 'Current Cargo invocation must identify one exact test binary');
  const artifact = artifacts[0];
  assert.equal(realpathSync(artifact.manifest_path), realpathSync(path.join(root, `crates/${packageId}/Cargo.toml`)));
  assert.equal(realpathSync(artifact.target.src_path), realpathSync(path.join(root, source)));
  const executable = ownedArtifactPath(root, path.relative(root, artifact.executable).replaceAll('\\', '/'));
  const nativeTarget = ownedArtifactPath(root, `target/${rust.hostTarget}`, 'directory');
  const relative = path.relative(nativeTarget, executable);
  assert.ok(relative && relative !== '..' && !relative.startsWith(`..${path.sep}`) && !path.isAbsolute(relative));
  const bytes = readFileSync(executable);
  if (process.platform === 'win32') {
    assert.equal(bytes.toString('ascii', 0, 2), 'MZ');
    assert.equal(bytes.readUInt16LE(bytes.readUInt32LE(0x3c) + 4), 0x8664);
  } else {
    assert.equal(bytes.readUInt32LE(0), 0xfeedfacf);
    assert.equal(bytes.readUInt32LE(4), 0x0100000c);
  }
  const binary = { component, target, executable, sha256: sha(bytes), bytes: bytes.length, compilerArtifact: artifact };
  binaries.push(binary);
  return binary;
}
for (const [component, selection, target, kind, source, required] of [
  ['native', ['--test', 'identity'], 'identity', 'test', 'crates/bridge-native/tests/identity.rs', nativeCriteria.identity],
  ['profiles', ['--lib'], 'bridge_profiles', 'lib', 'crates/bridge-profiles/src/lib.rs', nativeCriteria.profiles],
  ['toml', ['--lib'], 'bridge_toml', 'lib', 'crates/bridge-toml/src/lib.rs', nativeCriteria.toml]
]) {
  const binary = compile(component, selection, target, kind, source);
  const names = nativeTestInventory(run(`list-${component}-controlled`, binary.executable, ['--list']), required);
  nativeTestResult(run(`execute-${component}-controlled`, binary.executable, ['--test-threads=1']), names.length);
  executions.push({ binarySha256: binary.sha256, kind: 'controlled-function-table-or-loader-input-tests', names });
}
const producerObservations = {};
for (const [component, target, source, name, marker] of [
  ['native', 'native_modules', 'crates/bridge-native/tests/native_modules.rs', 'adopted_producer_exports_have_the_selected_physical_origin_and_retained_code', 'BRIDGE_NATIVE_IDENTITIES='],
  ['toml', 'native_abi', 'crates/bridge-toml/tests/native_abi.rs', 'producer_abi_all_nine_operations_and_refusals', 'BRIDGE_NATIVE_TOML_OBSERVATION '],
  ['profiles', 'profiles_abi', 'crates/bridge-profiles/tests/profiles_abi.rs', 'exact_native_profiles_api2_allocations_shared_data_and_installation_custody', 'PROFILES_NATIVE_PROBE_JSON=']
]) {
  const binary = compile(component, ['--test', target], target, 'test', source);
  nativeTestInventory(run(`list-${component}-producer`, binary.executable, ['--list']), [name]);
  const output = run(`execute-${component}-producer`, binary.executable, ['--ignored', '--exact', name, '--test-threads=1', '--nocapture']);
  nativeTestResult(output, 1);
  producerObservations[component] = nativeMarker(output, marker, name);
  executions.push({ binarySha256: binary.sha256, kind: 'explicit-actual-producer-abi', names: [name] });
}
for (const row of observations) {
  assert.equal(sha(readFileSync(ownedArtifactPath(root, row.relativePath))), row.sha256, 'Producer module changed');
  assert.equal(sha(readFileSync(ownedArtifactPath(root, row.buildReceipt))), row.provenance.buildReceiptSha256, 'Producer receipt changed');
  const observed = producerObservations[row.component].module;
  assert.equal(observed.component, row.component);
  assert.equal(observed.host, host);
  assert.equal(nativePhysicalPath(observed.physicalPath), nativePhysicalPath(row.physicalPath));
  assert.equal(observed.sha256, row.sha256);
  assert.deepEqual(observed.provenance, row.provenance);
}
assert.deepEqual(producerObservations.native.map(identity => identity.sha256).sort(), observations.map(row => row.sha256).sort());
assert.equal(sha(readFileSync(manifestPath)), sha(manifestBytes));
for (const binary of binaries) assert.equal(sha(readFileSync(binary.executable)), binary.sha256, 'Executed test binary changed');
const receipt = path.join(directory, 'native-ffi.json');
writeFileSync(receipt, JSON.stringify({ schemaVersion: 'bridge-native-ffi-observation/v1', host,
  toolchain: { rust: rust.pin, nativeTarget: rust.hostTarget, node: process.version },
  adoptionManifest: { path: manifestPath, sha256: sha(manifestBytes) }, modules: observations,
  binaries, executions, checks, producerObservations, fixtureRoot: fixture,
  controlledTests: executions.filter(item => item.kind.startsWith('controlled')).reduce((sum, item) => sum + item.names.length, 0),
  actualProducerTests: 3, compileFailOwnershipDocs: 10, evidenceRegressionTests: evidence,
  boundary: 'Actual selected-host consumer ABI, export origin and allocation/lease custody using explicitly adopted historical producer inputs and retained private fixtures. Producer recipes, another native host, installed game, platform ports, signing and release remain unqualified.',
  matrixAcceptance: false, nativeRuntimeQualified: false, releaseQualified: false
}, null, 2) + '\n');
console.log(JSON.stringify({ result: 'passed', host, receipt, matrixAcceptance: false, nativeRuntimeQualified: false, releaseQualified: false }));
