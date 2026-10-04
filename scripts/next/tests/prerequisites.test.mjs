import test from 'node:test';
import assert from 'node:assert/strict';
import { createHash } from 'node:crypto';
import { mkdirSync, mkdtempSync, realpathSync, rmSync, symlinkSync, writeFileSync } from 'node:fs';
import os from 'node:os';
import path from 'node:path';
import { QualificationBlocked, controlInputs, fingerprintInputs, qualificationInputs, selectQualification } from '../qualification.mjs';
import { MAX_RECEIPT_BYTES, readPrerequisiteReceipt, validatePrerequisites } from '../prerequisites.mjs';

const head = 'a'.repeat(40);
const host = process.platform === 'win32' && process.arch === 'x64' ? 'windows-x64' : process.platform === 'darwin' && process.arch === 'arm64' ? 'macos-arm64-native' : `${process.platform}-${process.arch}`;
const blocked = code => error => error instanceof QualificationBlocked && error.code === code;
function removeFixture(directory) {
  const actual = realpathSync(directory);
  const temporaryRoot = realpathSync(os.tmpdir());
  assert.equal(path.dirname(actual), temporaryRoot, 'Fixture deletion remains confined to its explicit temporary root');
  assert.ok(path.basename(actual).startsWith('bridge-prerequisite-'));
  rmSync(actual, { recursive: true, force: true });
}
function fixture(action) {
  const root = mkdtempSync(path.join(os.tmpdir(), 'bridge-prerequisite-'));
  const packages = [
    { id: 'br-00', owner: 'Bridge', dependsOn: [], gates: [{ id: 'base-first' }, { id: 'base-second' }] },
    { id: 'br-01', owner: 'Bridge', dependsOn: ['br-00'], gates: [{ id: 'first' }] },
    { id: 'br-02', owner: 'Bridge', dependsOn: ['br-00'], gates: [{ id: 'second' }] },
    { id: 'br-03', owner: 'Bridge', dependsOn: ['br-01', 'br-02'], gates: [{ id: 'target' }] }
  ];
  const campaign = { packages: Object.fromEntries(packages.map((p, i) => [p.id, { owner: 'Bridge', issue: `https://github.com/Guffawaffle/stfc-mod-bridge/issues/${232 + i}` }])) };
  const registry = Object.fromEntries(packages.flatMap(p => p.gates).map(g => [g.id, { host: 'any', argv: ['--test', `suites/${g.id}.test.mjs`], inputs: [`suites/${g.id}.test.mjs`], criteria: [`TEST-${g.id}`], boundary: 'Controlled local test fixture only.' }]));
  function write(relative, value) {
    const file = path.join(root, relative);
    mkdirSync(path.dirname(file), { recursive: true });
    writeFileSync(file, value);
  }
  for (const file of [...controlInputs, ...Object.values(registry).flatMap(s => s.inputs)]) write(file, 'Synthetic fixture input.');
  write('docs/next/campaign.json', JSON.stringify(campaign));
  write('docs/plans/rust-tauri-cross-platform/work-packages.json', JSON.stringify({ packages }));
  function receipt(id) {
    const selected = selectQualification({ options: { package: id, host: 'any' }, packages, registry, campaign, actualHost: host, root, cwd: root });
    return {
      schemaVersion: 'bridge-qualification-receipt/v1', package: id, owner: 'Bridge', issue: campaign.packages[id].issue,
      result: 'passed', packageAcceptance: true, inputsStable: true, nativeRuntimeQualified: false, releaseQualified: false,
      host: { id: host, processArchitecture: process.arch, node: process.version, osRelease: os.release() },
      source: { headSha: head, inputs: fingerprintInputs(root, qualificationInputs(selected.suites)) },
      checks: selected.suites.map(s => ({ id: s.id, argv: [...s.argv], executable: process.execPath, cwd: root, exitCode: 0, error: null, durationMs: 1, criteria: s.criteria, boundary: s.boundary }))
    };
  }
  function wrap(value) {
    const bytes = Buffer.from(JSON.stringify(value));
    return { receipt: value, reference: { path: `artifacts/next/${value.package}/acceptance.json`, sha256: createHash('sha256').update(bytes).digest('hex'), bytes: bytes.length } };
  }
  const receipts = new Map(packages.slice(0, 3).map(p => [p.id, wrap(receipt(p.id))]));
  const readReceipt = id => receipts.get(id);
  const validate = (overrides = {}) => validatePrerequisites({ selected: { package: packages.at(-1) }, packages, registry, campaign, actualHost: host, root, currentHead: head, readReceipt, ...overrides });
  try { action({ root, packages, campaign, registry, receipts, readReceipt, receipt, wrap, write, validate }); }
  finally { removeFixture(root); }
}

test('all genuine prerequisites validate recursively once and return bounded observations', () => fixture(({ validate, readReceipt }) => {
  const read = [];
  const refs = validate({ readReceipt: id => { read.push(id); return readReceipt(id); } });
  assert.deepEqual(read, ['br-00', 'br-01', 'br-02']);
  assert.deepEqual(refs.map(r => r.package), read);
  for (const ref of refs) {
    assert.equal(ref.sourceHead, head);
    assert.equal(ref.proofBoundary, 'local-package-gate-observation-only');
    assert.equal(ref.nativeRuntimeQualified, false);
    assert.equal(ref.releaseQualified, false);
    assert.ok(ref.receipt.bytes <= MAX_RECEIPT_BYTES);
  }
}));
test('an initial package has no fabricated prerequisite observation', () => fixture(({ validate, packages }) => {
  assert.deepEqual(validate({ selected: { package: packages[0] }, readReceipt: () => assert.fail('No receipt should be read') }), []);
}));
test('missing, suite-only, failed, unknown-version and unstable receipts refuse admission', () => {
  for (const [mutate, code] of [
    [(_f, values) => values.delete('br-00'), 'PREREQUISITE_MISSING'],
    [(f, values) => { const r = f.receipt('br-00'); r.packageAcceptance = false; values.set('br-00', f.wrap(r)); }, 'PREREQUISITE_RECEIPT_INVALID'],
    [(f, values) => { const r = f.receipt('br-00'); r.result = 'failed'; values.set('br-00', f.wrap(r)); }, 'PREREQUISITE_RECEIPT_INVALID'],
    [(f, values) => { const r = f.receipt('br-00'); r.schemaVersion = 'unknown/v99'; values.set('br-00', f.wrap(r)); }, 'PREREQUISITE_RECEIPT_INVALID'],
    [(f, values) => { const r = f.receipt('br-00'); r.inputsStable = false; values.set('br-00', f.wrap(r)); }, 'PREREQUISITE_RECEIPT_INVALID']
  ]) fixture(f => { mutate(f, f.receipts); assert.throws(() => f.validate(), blocked(code)); });
});

test('required partial br-06 suite blocks dependency admission before its receipt is read', () => fixture(f => {
  const prerequisite = f.packages[1];
  prerequisite.id = 'br-06';
  f.packages.at(-1).dependsOn = ['br-06'];
  f.campaign.packages['br-06'] = { owner: 'Bridge', issue: 'https://github.com/Guffawaffle/stfc-mod-bridge/issues/241' };
  prerequisite.gates.push({ id: 'windows-private-journal-fixtures' });
  f.registry['windows-private-journal-fixtures'] = {
    host: 'any', packageAcceptanceAvailable: false, argv: ['--test', 'suites/partial.test.mjs'],
    inputs: ['suites/partial.test.mjs'], criteria: ['BR06-WJ01'], boundary: 'Synthetic selected-suite receipt only.'
  };
  f.write('suites/partial.test.mjs', 'Synthetic fixture.');
  const partial = { ...f.receipts.get('br-01').receipt, package: 'br-06', packageAcceptance: false, issue: f.campaign.packages['br-06'].issue };
  f.receipts.set('br-06', f.wrap(partial));
  const read = [];
  assert.throws(() => f.validate({ readReceipt: id => { read.push(id); return f.readReceipt(id); } }), blocked('PACKAGE_INTEGRATION_UNQUALIFIED'));
  assert.deepEqual(read, ['br-00']);
}));
test('partial, empty, duplicate, reordered and failed checks cannot satisfy a multi-suite prerequisite', () => {
  for (const [mutate, code] of [
    [r => { r.checks = []; }, 'PREREQUISITE_CHECKS_INCOMPLETE'],
    [r => { r.checks.pop(); }, 'PREREQUISITE_CHECKS_INCOMPLETE'],
    [r => { r.checks[1] = structuredClone(r.checks[0]); }, 'PREREQUISITE_CHECKS_INCOMPLETE'],
    [r => { r.checks.reverse(); }, 'PREREQUISITE_CHECKS_INCOMPLETE'],
    [r => { r.checks[0].exitCode = null; }, 'PREREQUISITE_CHECK_FAILED'],
    [r => { r.checks[0].error = 'interrupted'; }, 'PREREQUISITE_CHECK_FAILED'],
    [r => { r.checks[0].durationMs = -1; }, 'PREREQUISITE_CHECK_FAILED']
  ]) fixture(f => { const r = f.receipt('br-00'); mutate(r); f.receipts.set('br-00', f.wrap(r)); assert.throws(() => f.validate(), blocked(code)); });
});
test('receipt commands, executable, cwd, criteria and boundary must match current suite definitions', () => {
  for (const mutate of [
    r => { r.checks[0].argv = ['-e', 'process.exit(0)']; },
    r => { r.checks[0].executable = 'missing-executable'; },
    r => { r.checks[0].cwd = os.tmpdir(); },
    r => { r.checks[0].criteria = []; },
    r => { r.checks[0].boundary = 'Release approved'; }
  ]) fixture(f => { const r = f.receipt('br-00'); mutate(r); f.receipts.set('br-00', f.wrap(r)); assert.throws(() => f.validate(), blocked('PREREQUISITE_CHECKS_INCOMPLETE')); });
});
test('receipt identity, issue, owner and authority flags must describe the bound local package', () => {
  for (const mutate of [
    r => { r.package = 'br-02'; }, r => { r.issue += '0'; }, r => { r.owner = 'Profiles'; },
    r => { r.nativeRuntimeQualified = true; }, r => { r.releaseQualified = true; }
  ]) fixture(f => { const r = f.receipt('br-00'); mutate(r); const wrapped = f.wrap(r); wrapped.reference.path = 'artifacts/next/br-00/acceptance.json'; f.receipts.set('br-00', wrapped); assert.throws(() => f.validate(), blocked('PREREQUISITE_RECEIPT_INVALID')); });
});
test('stale source and different host/toolchain context refuse admission', () => {
  fixture(f => assert.throws(() => f.validate({ currentHead: 'b'.repeat(40) }), blocked('PREREQUISITE_STALE_SOURCE')));
  for (const mutate of [
    r => { r.host.id = 'different-host'; }, r => { r.host.node = 'v0.0.0'; },
    r => { r.host.processArchitecture = 'different-architecture'; }, r => { r.host.osRelease = 'different-os'; }
  ]) fixture(f => { const r = f.receipt('br-00'); mutate(r); f.receipts.set('br-00', f.wrap(r)); assert.throws(() => f.validate(), blocked('PREREQUISITE_WRONG_HOST')); });
});
test('mandatory graph drift and a receipt-declared narrow input scope are stale evidence', () => {
  fixture(f => { f.write('docs/plans/rust-tauri-cross-platform/work-packages.json', '{"changed":true}'); assert.throws(() => f.validate(), blocked('PREREQUISITE_INPUTS_STALE')); });
  fixture(f => { const r = f.receipt('br-00'); r.source.inputs = fingerprintInputs(f.root, ['suites/base-first.test.mjs']); f.receipts.set('br-00', f.wrap(r)); assert.throws(() => f.validate(), blocked('PREREQUISITE_INPUTS_STALE')); });
});
test('input mutation during receipt validation is detected before admission', () => fixture(f => {
  const received = f.readReceipt('br-00');
  assert.throws(() => f.validate({ readReceipt: id => {
    if (id === 'br-00') f.write('suites/base-first.test.mjs', 'Replaced during validation.');
    return id === 'br-00' ? received : f.readReceipt(id);
  } }), blocked('PREREQUISITE_INPUTS_CHANGED'));
}));
test('cycles, unknown dependencies, duplicate identities and forged selected scopes block', () => {
  fixture(f => { f.packages[0].dependsOn = ['br-03']; assert.throws(() => f.validate(), blocked('PREREQUISITE_CYCLE')); });
  fixture(f => { f.packages[0].dependsOn = ['br-missing']; assert.throws(() => f.validate(), blocked('PREREQUISITE_UNKNOWN')); });
  fixture(f => { f.packages.push(structuredClone(f.packages[0])); assert.throws(() => f.validate(), blocked('PREREQUISITE_GRAPH_INVALID')); });
  fixture(f => { const selected = { package: { ...f.packages.at(-1), dependsOn: [] } }; assert.throws(() => f.validate({ selected }), blocked('PREREQUISITE_CONTEXT_INVALID')); });
});
test('cross-repository, native aggregation and unbound issue evidence remain explicitly unavailable', () => {
  fixture(f => { f.packages[0].owner = 'Profiles'; assert.throws(() => f.validate(), blocked('PREREQUISITE_OWNER_UNIMPLEMENTED')); });
  fixture(f => { f.packages[0].host = 'native-target-matrix'; assert.throws(() => f.validate(), blocked('PREREQUISITE_HOST_AGGREGATION_UNIMPLEMENTED')); });
  fixture(f => { f.campaign.packages['br-00'].issue = 'https://github.com/Guffawaffle/stfc-mod-bridge/issues/not-an-issue'; assert.throws(() => f.validate(), blocked('PREREQUISITE_ISSUE_UNBOUND')); });
});
test('references cannot escape or exceed the receipt bound and retain no unknown fields', () => {
  fixture(f => { f.receipts.get('br-00').reference.path = '../elsewhere'; assert.throws(() => f.validate(), blocked('PREREQUISITE_REFERENCE_INVALID')); });
  fixture(f => { f.receipts.get('br-00').reference.bytes = MAX_RECEIPT_BYTES + 1; assert.throws(() => f.validate(), blocked('PREREQUISITE_REFERENCE_INVALID')); });
  fixture(f => { f.receipts.get('br-00').reference.privateValue = 'Must not be retained'; assert.ok(!Object.hasOwn(f.validate()[0].receipt, 'privateValue')); });
});
test('bounded reader observes exact receipt bytes and reports missing or malformed data safely', () => fixture(f => {
  const r = f.receipt('br-00');
  const bytes = JSON.stringify(r, null, 2) + '\n';
  f.write('artifacts/next/br-00/acceptance.json', bytes);
  const read = readPrerequisiteReceipt({ root: f.root, packageId: 'br-00' });
  assert.deepEqual(read.receipt, r);
  assert.equal(read.reference.sha256, createHash('sha256').update(bytes).digest('hex'));
  assert.equal(read.reference.bytes, Buffer.byteLength(bytes));
  assert.throws(() => readPrerequisiteReceipt({ root: f.root, packageId: 'br-99' }), blocked('PREREQUISITE_MISSING'));
  assert.throws(() => readPrerequisiteReceipt({ root: f.root, packageId: '../escape' }), blocked('PREREQUISITE_CONTEXT_INVALID'));
  f.write('artifacts/next/br-00/acceptance.json', 'not JSON');
  assert.throws(() => readPrerequisiteReceipt({ root: f.root, packageId: 'br-00' }), blocked('PREREQUISITE_RECEIPT_INVALID'));
  f.write('artifacts/next/br-00/acceptance.json', Buffer.alloc(MAX_RECEIPT_BYTES + 1));
  assert.throws(() => readPrerequisiteReceipt({ root: f.root, packageId: 'br-00' }), blocked('PREREQUISITE_TOO_LARGE'));
}));
test('bounded reader rejects an artifact-directory link outside the owning checkout', () => fixture(f => {
  const outside = mkdtempSync(path.join(os.tmpdir(), 'bridge-prerequisite-'));
  try {
    writeFileSync(path.join(outside, 'acceptance.json'), '{}');
    mkdirSync(path.join(f.root, 'artifacts/next'), { recursive: true });
    symlinkSync(outside, path.join(f.root, 'artifacts/next/br-00'), process.platform === 'win32' ? 'junction' : 'dir');
    assert.throws(() => readPrerequisiteReceipt({ root: f.root, packageId: 'br-00' }), blocked('PREREQUISITE_ESCAPE'));
  } finally { removeFixture(outside); }
}));
