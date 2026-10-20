import assert from 'node:assert/strict';
import { test } from 'node:test';
import { GATE_ARGS, PLAN_ARGS, requireCiNodeContext, requirePinnedRunnerPackage, requireSelectedPlan } from '../windows-journal-ci-entry-policy.mjs';

const context = () => ({ argv: ['node', 'entry.mjs'], execArgv: [], platform: 'win32', arch: 'x64', version: 'v24.14.1', environment: { BRIDGE_EXPECTED_HOST: 'windows-x64', PATH: 'C:\\Tools' } });
test('CI entry admits only the fixed native Node invocation', () => {
  requireCiNodeContext(context());
  for (const [key, value] of [['argv', ['node', 'entry', '--fixture']], ['execArgv', ['--import', 'caller.mjs']], ['platform', 'darwin'], ['arch', 'arm64'], ['version', 'v24.14.0']]) assert.throws(() => requireCiNodeContext({ ...context(), [key]: value }));
  assert.throws(() => requireCiNodeContext({ ...context(), environment: { ...context().environment, Path: 'C:\\Other' } }));
  for (const BRIDGE_EXPECTED_HOST of ['any', 'macos-arm64-native', undefined]) assert.throws(() => requireCiNodeContext({ ...context(), environment: { BRIDGE_EXPECTED_HOST } }));
});
test('CI entry refuses execution overrides while preserving normal tool cache environment', () => {
  requireCiNodeContext({ ...context(), environment: { ...context().environment, CARGO_HOME: 'C:\\Cache', RUSTUP_HOME: 'C:\\Rust', NODE_OPTIONS: '' } });
  for (const key of ['node_options', 'NODE_PATH', 'BRIDGE_JOURNAL_CI_TOKEN', 'BRIDGE_PRIVATE_JOURNAL_PATH', 'RUSTC', 'CARGO_TARGET_DIR', 'CARGO_INCREMENTAL', 'CARGO_PROFILE_DEV_DEBUG', 'RUST_TEST_THREADS', 'LIBTEST_FILTER', '__COMPAT_LAYER', 'git_dir', 'GIT_COMMON_DIR', 'GIT_WORK_TREE', 'GIT_OBJECT_DIRECTORY', 'GIT_NAMESPACE', 'GIT_REPLACE_REF_BASE', 'GIT_CONFIG_GLOBAL', 'GIT_CONFIG_SYSTEM', 'GIT_CONFIG_COUNT']) assert.throws(() => requireCiNodeContext({ ...context(), environment: { ...context().environment, [key]: 'override' } }));
});

function selected() {
  const root = 'D:\\dev\\stfc-mod-launcher', sourceHead = 'd27d683ae825f2e3985ff9a614503bc221a281ac', planSha256 = 'd0ac5275cfc1745c8841f502d6296069b7f03896dc92087f969c280e84e54662';
  return {
    root, sourceHead, planSha256,
    plan: {
      schemaVersion: '1.0.0', target: 'main',
      policy: { requiredGates: ['acceptance'], optionalGates: [], maxWorkers: 1, retries: {}, overrides: {}, mergeRule: { type: 'strict-required' } },
      items: [{ name: 'br-06.windows-private-journal-fixtures.windows-x64', deps: [], gates: [{
        name: 'acceptance', run: 'node scripts/next/qualify.mjs --suite windows-private-journal-fixtures --host windows-x64',
        cwd: root, runtime: 'local', timeoutMs: 630000,
        env: { BRIDGE_WORK_PACKAGE: 'br-06', BRIDGE_ISSUE: '241' }, artifacts: ['artifacts/next/br-06/acceptance.json']
      }] }]
    },
    projection: {
      schemaVersion: 'bridge-gate-projection/v1', package: 'br-06', observationScope: { kind: 'local-suite-gates', host: 'windows-x64' },
      dependsOn: ['br-02'], sourceHead, planSha256, boundary: 'single-package-command-gate-projection-not-a-merge-plan',
      prerequisites: ['br-00', 'br-01', 'br-02'].map((id, index) => ({
        package: id, owner: 'Bridge', issue: `https://github.com/Guffawaffle/stfc-mod-bridge/issues/${[232, 234, 236][index]}`,
        sourceHead, host: 'windows-x64', inputSha256: 'a'.repeat(64),
        receipt: { path: `artifacts/next/${id}/acceptance.json`, sha256: 'b'.repeat(64), bytes: 4290 },
        proofBoundary: 'local-package-gate-observation-only', nativeRuntimeQualified: false, releaseQualified: false
      }))
    }
  };
}

test('CI selection admits the closed producer plan and same-head prerequisite projection', () => {
  requireSelectedPlan(selected());
  const reordered = selected();
  reordered.projection.prerequisites[0].receipt = { bytes: 4290, path: 'artifacts/next/br-00/acceptance.json', sha256: 'b'.repeat(64) };
  requireSelectedPlan(reordered);
});

test('CI selection refuses substituted scope, command, policy and gate inputs', async t => {
  const mutations = [
    ['foreign target', x => { x.plan.target = 'other'; }],
    ['foreign item', x => { x.plan.items[0].name = 'br-07'; }],
    ['extra item', x => { x.plan.items.push(structuredClone(x.plan.items[0])); }],
    ['caller dependency', x => { x.plan.items[0].deps.push('caller'); }],
    ['caller command', x => { x.plan.items[0].gates[0].run = 'node caller.mjs'; }],
    ['whole package command', x => { x.plan.items[0].gates[0].run = 'node scripts/next/qualify.mjs --package br-06 --host windows-x64'; }],
    ['foreign cwd', x => { x.plan.items[0].gates[0].cwd = 'D:\\other'; }],
    ['foreign runtime', x => { x.plan.items[0].gates[0].runtime = 'docker'; }],
    ['extra gate', x => { x.plan.items[0].gates.push(structuredClone(x.plan.items[0].gates[0])); }],
    ['weaker timeout', x => { x.plan.items[0].gates[0].timeoutMs = 0; }],
    ['caller environment', x => { x.plan.items[0].gates[0].env.NODE_OPTIONS = '--import caller.mjs'; }],
    ['foreign artifact', x => { x.plan.items[0].gates[0].artifacts[0] = 'caller.json'; }],
    ['missing acceptance gate', x => { x.plan.policy.requiredGates = []; }],
    ['parallel workers', x => { x.plan.policy.maxWorkers = 2; }],
    ['caller policy override', x => { x.plan.policy.overrides.acceptance = { run: 'caller' }; }],
    ['unknown nested field', x => { x.plan.items[0].gates[0].selector = 'caller'; }]
  ];
  for (const [name, mutate] of mutations) await t.test(name, () => { const value = selected(); mutate(value); assert.throws(() => requireSelectedPlan(value)); });
});

test('CI projection refuses stale, redirected, incomplete or stronger evidence claims', async t => {
  const mutations = [
    ['foreign scope', x => { x.projection.observationScope.kind = 'single-native-host-probe'; }],
    ['foreign host', x => { x.projection.observationScope.host = 'macos-arm64-native'; }],
    ['foreign package', x => { x.projection.package = 'br-07'; }],
    ['changed dependencies', x => { x.projection.dependsOn = []; }],
    ['stale head', x => { x.projection.sourceHead = 'c'.repeat(40); }],
    ['invalid actual head', x => { x.sourceHead = 'short'; }],
    ['changed plan bytes', x => { x.projection.planSha256 = 'c'.repeat(64); }],
    ['invalid actual plan hash', x => { x.planSha256 = 'short'; }],
    ['stronger boundary', x => { x.projection.boundary = 'merge-approved'; }],
    ['extra field', x => { x.projection.caller = true; }],
    ['missing prerequisite', x => { x.projection.prerequisites.pop(); }],
    ['duplicate prerequisite', x => { x.projection.prerequisites[1] = structuredClone(x.projection.prerequisites[0]); }],
    ['wrong prerequisite order', x => { x.projection.prerequisites.reverse(); }],
    ['foreign owner', x => { x.projection.prerequisites[0].owner = 'Upstream'; }],
    ['unbound issue', x => { x.projection.prerequisites[0].issue = 'https://github.com/Guffawaffle/stfc-mod-bridge/issues/241'; }],
    ['stale prerequisite head', x => { x.projection.prerequisites[0].sourceHead = 'c'.repeat(40); }],
    ['foreign prerequisite host', x => { x.projection.prerequisites[0].host = 'any'; }],
    ['invalid input hash', x => { x.projection.prerequisites[0].inputSha256 = 'invalid'; }],
    ['redirected receipt', x => { x.projection.prerequisites[0].receipt.path = '../caller.json'; }],
    ['invalid receipt hash', x => { x.projection.prerequisites[0].receipt.sha256 = 'invalid'; }],
    ['empty receipt', x => { x.projection.prerequisites[0].receipt.bytes = 0; }],
    ['fractional receipt size', x => { x.projection.prerequisites[0].receipt.bytes = 1.5; }],
    ['oversized receipt', x => { x.projection.prerequisites[0].receipt.bytes = 1024 * 1024 + 1; }],
    ['unknown receipt field', x => { x.projection.prerequisites[0].receipt.extra = true; }],
    ['unknown prerequisite field', x => { x.projection.prerequisites[0].extra = true; }],
    ['native qualification claim', x => { x.projection.prerequisites[0].nativeRuntimeQualified = true; }],
    ['release qualification claim', x => { x.projection.prerequisites[0].releaseQualified = true; }]
  ];
  for (const [name, mutate] of mutations) await t.test(name, () => { const value = selected(); mutate(value); assert.throws(() => requireSelectedPlan(value)); });
});
test('CI runner package binding cannot select another package version or entry', () => {
  const value = { name: '@smartergpt/lexrunner', version: '2.1.0', type: 'module', bin: { lexrunner: 'dist/cli.js' } };
  requirePinnedRunnerPackage(value);
  for (const change of [{ name: '@other/runner' }, { version: '2.4.0' }, { type: 'commonjs' }, { bin: { lexrunner: '../caller.js' } }, { bin: 'dist/cli.js' }]) assert.throws(() => requirePinnedRunnerPackage({ ...value, ...change }));
  for (const value of [null, [], 'package']) assert.throws(() => requirePinnedRunnerPackage(value));
});
test('CI plan and gate arguments retain the selected partial scope and disabled frames', () => {
  assert.deepEqual(PLAN_ARGS, ['scripts/next/make-runner-plan.mjs', '--suite', 'windows-private-journal-fixtures', '--host', 'windows-x64']);
  assert.deepEqual(GATE_ARGS, ['--no-emit-frames', 'gate', 'run', '--plan', 'artifacts/next/plans/br-06.windows-private-journal-fixtures.windows-x64.plan.json', '--artifact-dir', 'artifacts/next/runner/br-06-private-journal-fixtures', '--timeout', '630000', '--max-level', '0', '--keep-cache', '--json']);
  assert.ok(Object.isFrozen(PLAN_ARGS) && Object.isFrozen(GATE_ARGS));
});
