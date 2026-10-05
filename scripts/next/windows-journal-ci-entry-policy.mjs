import assert from 'node:assert/strict';

export const CI_NODE_VERSION = 'v24.14.1';
export const CI_RUNNER_VERSION = '2.1.0';
export const PLAN_ARGS = Object.freeze(['scripts/next/make-runner-plan.mjs', '--suite', 'windows-private-journal-fixtures', '--host', 'windows-x64']);
export const GATE_ARGS = Object.freeze(['--no-emit-frames', 'gate', 'run', '--plan', 'artifacts/next/plans/br-06.windows-private-journal-fixtures.windows-x64.plan.json', '--artifact-dir', 'artifacts/next/runner/br-06-private-journal-fixtures', '--timeout', '630000', '--max-level', '0', '--keep-cache', '--json']);
export const PROJECTION_PATH = 'artifacts/next/plans/br-06.windows-private-journal-fixtures.windows-x64.projection.json';

export function requireCiNodeContext({ argv, execArgv, platform, arch, version, environment }) {
  assert.equal(argv.length, 2, 'CI entry has no caller arguments');
  assert.deepEqual(execArgv, [], 'CI entry has no Node loader arguments');
  assert.equal(platform, 'win32');
  assert.equal(arch, 'x64');
  assert.equal(version, CI_NODE_VERSION);
  assert.equal(environment.BRIDGE_EXPECTED_HOST, 'windows-x64');
  const names = new Set();
  for (const [name, value] of Object.entries(environment)) {
    const key = name.toUpperCase();
    assert.ok(!names.has(key), 'Windows environment keys must be unique ignoring case');
    names.add(key);
    if (value === '' || value === undefined) continue;
    assert.ok(!/^(?:BRIDGE_(?:JOURNAL_CI_|PRIVATE_JOURNAL_|FIXTURE_|TEST_WINDOWS_|WINDOWS_CHILD_|LOCAL_HOST_CHILD_|CONFIGURATION_)|RUST_TEST_|LIBTEST_)/.test(key)
      && !/^(?:RUSTC|RUSTDOC|RUSTFMT|CARGO|RUSTC_WRAPPER|RUSTC_WORKSPACE_WRAPPER|RUSTFLAGS|RUSTDOCFLAGS|RUSTUP_TOOLCHAIN|RUSTC_BOOTSTRAP|CARGO_TARGET_DIR|CARGO_INCREMENTAL|CARGO_ENCODED_RUSTFLAGS|CARGO_ENCODED_RUSTDOCFLAGS)$/.test(key)
      && !/^CARGO_(?:TARGET_|PROFILE_|BUILD_|ALIAS_)/.test(key)
      && !/^GIT_(?:DIR|WORK_TREE|COMMON_DIR|INDEX_FILE|OBJECT_DIRECTORY|ALTERNATE_OBJECT_DIRECTORIES|NAMESPACE|REPLACE_REF_BASE|CONFIG|CONFIG_GLOBAL|CONFIG_SYSTEM|CONFIG_COUNT|CONFIG_PARAMETERS)$/.test(key)
      && !/^(?:NODE_OPTIONS|NODE_PATH|TS_NODE_.*|TSX_.*|VITE_.*|VITEST_.*|ESBUILD_BINARY_PATH|ROLLDOWN_BINDING_PATH|NAPI_RS_NATIVE_LIBRARY_PATH|NPM_CONFIG_(?:NODE_OPTIONS|SCRIPT_SHELL)|__COMPAT_LAYER|LD_PRELOAD|LD_LIBRARY_PATH|DYLD_.*)$/.test(key), 'Caller execution override refused');
  }
}

export function requirePinnedRunnerPackage(value) {
  assert.ok(value && typeof value === 'object' && !Array.isArray(value));
  assert.equal(value.name, '@smartergpt/lexrunner');
  assert.equal(value.version, CI_RUNNER_VERSION);
  assert.equal(value.type, 'module');
  assert.equal(value.bin?.lexrunner, 'dist/cli.js');
}

export function requireSelectedPlan({ plan, projection, root, sourceHead, planSha256 }) {
  assert.match(sourceHead, /^[0-9a-f]{40}$/);
  assert.match(planSha256, /^[0-9a-f]{64}$/);
  const expected = {
    schemaVersion: '1.0.0', target: 'main',
    policy: { requiredGates: ['acceptance'], optionalGates: [], maxWorkers: 1, retries: {}, overrides: {}, mergeRule: { type: 'strict-required' } },
    items: [{ name: 'br-06.windows-private-journal-fixtures.windows-x64', deps: [], gates: [{
      name: 'acceptance', run: 'node scripts/next/qualify.mjs --suite windows-private-journal-fixtures --host windows-x64',
      cwd: root, runtime: 'local', timeoutMs: 630000,
      env: { BRIDGE_WORK_PACKAGE: 'br-06', BRIDGE_ISSUE: '241' }, artifacts: ['artifacts/next/br-06/acceptance.json']
    }] }]
  };
  assert.deepEqual(plan, expected, 'Generated plan must be the fixed selected partial gate');
  assert.ok(projection && typeof projection === 'object' && !Array.isArray(projection));
  assert.deepEqual(Object.keys(projection).sort(), ['schemaVersion', 'package', 'observationScope', 'dependsOn', 'prerequisites', 'sourceHead', 'planSha256', 'boundary'].sort());
  assert.equal(projection.schemaVersion, 'bridge-gate-projection/v1');
  assert.equal(projection.package, 'br-06');
  assert.deepEqual(projection.observationScope, { kind: 'local-suite-gates', host: 'windows-x64' });
  assert.deepEqual(projection.dependsOn, ['br-02']);
  assert.equal(projection.sourceHead, sourceHead);
  assert.equal(projection.planSha256, planSha256);
  assert.equal(projection.boundary, 'single-package-command-gate-projection-not-a-merge-plan');
  assert.ok(Array.isArray(projection.prerequisites));
  assert.deepEqual(projection.prerequisites.map(item => item.package), ['br-00', 'br-01', 'br-02']);
  for (const item of projection.prerequisites) {
    assert.deepEqual(Object.keys(item).sort(), ['package', 'owner', 'issue', 'sourceHead', 'host', 'inputSha256', 'receipt', 'proofBoundary', 'nativeRuntimeQualified', 'releaseQualified'].sort());
    assert.equal(item.owner, 'Bridge'); assert.equal(item.sourceHead, sourceHead); assert.equal(item.host, 'windows-x64');
    const issue = { 'br-00': 232, 'br-01': 234, 'br-02': 236 }[item.package];
    assert.equal(item.issue, `https://github.com/Guffawaffle/stfc-mod-bridge/issues/${issue}`);
    assert.match(item.inputSha256, /^[0-9a-f]{64}$/);
    assert.deepEqual(Object.keys(item.receipt).sort(), ['bytes', 'path', 'sha256']);
    assert.equal(item.receipt.path, `artifacts/next/${item.package}/acceptance.json`);
    assert.match(item.receipt.sha256, /^[0-9a-f]{64}$/);
    assert.ok(Number.isSafeInteger(item.receipt.bytes) && item.receipt.bytes > 0 && item.receipt.bytes <= 1024 * 1024);
    assert.equal(item.proofBoundary, 'local-package-gate-observation-only');
    assert.equal(item.nativeRuntimeQualified, false); assert.equal(item.releaseQualified, false);
  }
}
