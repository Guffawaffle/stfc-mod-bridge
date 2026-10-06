import test from 'node:test';
import assert from 'node:assert/strict';
import { mkdirSync, mkdtempSync, realpathSync, rmSync, writeFileSync } from 'node:fs';
import os from 'node:os';
import path from 'node:path';
import { RustContextBlocked, rustContext } from '../rust-context.mjs';

const blocked = code => error => error instanceof RustContextBlocked && error.code === code;
function fixture(action) {
  const root = mkdtempSync(path.join(os.tmpdir(), 'bridge-rust-context-'));
  const metadata = pin => writeFileSync(path.join(root, 'dependencies/next-toolchain.json'), JSON.stringify({ schemaVersion: 'bridge-toolchain/v1', rust: pin }));
  const toolchain = value => writeFileSync(path.join(root, 'rust-toolchain.toml'), value);
  mkdirSync(path.join(root, 'dependencies'));
  metadata('1.99.0');
  toolchain('[toolchain]\nchannel = "1.99.0"\nprofile = "minimal"\ncomponents = ["clippy", "rustfmt"]\n');
  const context = overrides => rustContext({ root, environment: {}, platform: 'win32', architecture: 'x64', exists: () => false, ...overrides });
  try { action({ root, metadata, toolchain, context }); }
  finally {
    const actual = realpathSync(root);
    assert.equal(path.dirname(actual), realpathSync(os.tmpdir()), 'Fixture deletion remains inside its explicit temporary root');
    assert.ok(path.basename(actual).startsWith('bridge-rust-context-'));
    rmSync(actual, { recursive: true, force: true });
  }
}

test('tracked pin and Windows native target explicitly control the child context', () => fixture(({ root, context }) => {
  const result = context({ environment: { RUSTUP_TOOLCHAIN: 'nightly', CARGO_TARGET_DIR: '/old-binary-cache', CARGO_BUILD_TARGET: 'aarch64-unknown-linux-gnu', PATH: 'ordinary-bin' } });
  assert.equal(result.pin, '1.99.0');
  assert.equal(result.hostTarget, 'x86_64-pc-windows-msvc');
  assert.equal(result.env.RUSTUP_TOOLCHAIN, '1.99.0');
  assert.equal(result.env.CARGO_TARGET_DIR, path.join(root, 'target'));
  assert.equal(result.env.CARGO_BUILD_TARGET, result.hostTarget);
  assert.equal(result.cargo, 'cargo.exe');
  assert.equal(result.rustc, 'rustc.exe');
  assert.equal(result.env.RUSTC, result.rustc);
  assert.equal(result.env.RUSTDOC, 'rustdoc.exe');
  assert.equal(result.env.RUSTC_WRAPPER, '');
  assert.equal(result.env.RUSTC_WORKSPACE_WRAPPER, '');
  assert.equal(result.env.PATH, 'ordinary-bin');
}));
test('Apple Silicon uses its native target and ordinary Rustup shim commands', () => fixture(({ root, context }) => {
  const result = context({ platform: 'darwin', architecture: 'arm64', environment: { PATH: '/ordinary/bin' } });
  assert.equal(result.hostTarget, 'aarch64-apple-darwin');
  assert.equal(result.env.CARGO_BUILD_TARGET, result.hostTarget);
  assert.equal(result.env.CARGO_TARGET_DIR, path.join(root, 'target'));
  assert.equal(result.cargo, 'cargo');
  assert.equal(result.rustc, 'rustc');
  assert.equal(result.env.RUSTC, result.rustc);
  assert.equal(result.env.RUSTDOC, 'rustdoc');
}));
test('Intel Mac, Windows ARM and unsupported hosts cannot become native qualification', () => fixture(({ context }) => {
  for (const [platform, architecture] of [['darwin', 'x64'], ['win32', 'arm64'], ['linux', 'x64'], ['darwin', 'ia32']]) {
    assert.throws(() => context({ platform, architecture }), blocked('RUST_HOST_UNSUPPORTED'));
  }
}));
test('every custom compiler or wrapper override refuses the unknown route', () => fixture(({ context }) => {
  for (const key of ['RUSTC', 'RUSTC_WRAPPER', 'RUSTC_WORKSPACE_WRAPPER', 'RUSTDOC', 'Rustc', 'rustc_wrapper']) {
    assert.throws(() => context({ environment: { [key]: 'unknown-compiler' } }), blocked('RUST_COMPILER_OVERRIDE'));
  }
  const clean = context({ environment: { RUSTC: '', RUSTC_WRAPPER: undefined } });
  assert.equal(clean.env.RUSTC, clean.rustc);
  assert.equal(clean.env.RUSTC_WRAPPER, '');
}));
test('Cargo build compiler aliases cannot bypass override refusal, including Windows casing', () => fixture(({ context }) => {
  for (const key of ['CARGO_BUILD_RUSTC', 'CARGO_BUILD_RUSTC_WRAPPER', 'CARGO_BUILD_RUSTC_WORKSPACE_WRAPPER', 'CARGO_BUILD_RUSTDOC']) {
    for (const spelling of [key, key.toLowerCase(), key.replace('CARGO_BUILD_', 'Cargo_Build_')]) {
      assert.throws(() => context({ environment: { [spelling]: 'unknown-compiler' } }), blocked('RUST_COMPILER_OVERRIDE'));
    }
    assert.throws(() => context({ platform: 'darwin', architecture: 'arm64', environment: { [key]: 'unknown-compiler' } }), blocked('RUST_COMPILER_OVERRIDE'));
  }
}));
test('formatter overrides refuse before execution and empty selectors cannot shadow the pinned tool', () => fixture(({ context }) => {
  for (const spelling of ['RUSTFMT', 'rustfmt', 'Rustfmt']) {
    for (const value of ['unexecuted-external-formatter', ' ', '\t']) {
      assert.throws(() => context({ environment: { [spelling]: value } }), blocked('RUST_COMPILER_OVERRIDE'));
    }
    const environment = Object.freeze({ [spelling]: '' });
    const result = context({ environment });
    assert.ok(!Object.keys(result.env).some(key => key.toUpperCase() === 'RUSTFMT'));
    assert.deepEqual(environment, { [spelling]: '' });
  }
  assert.throws(() => context({ platform: 'darwin', architecture: 'arm64', environment: { RUSTFMT: 'unexecuted-external-formatter' } }), blocked('RUST_COMPILER_OVERRIDE'));
  const result = context({ environment: { RUSTFMT: undefined } });
  assert.ok(!Object.hasOwn(result.env, 'RUSTFMT'));
}));
test('Cargo and external subcommand aliases refuse before route discovery and scrub empty selectors', () => fixture(({ context }) => {
  for (const key of ['CARGO', 'CARGO_ALIAS_FMT', 'CARGO_ALIAS_CLIPPY']) {
    for (const platform of ['win32', 'darwin']) {
      const spellings = platform === 'win32' ? [key, key.toLowerCase(), key.replace('CARGO', 'Cargo')] : [key];
      for (const spelling of spellings) {
        for (const value of ['metadata --format-version 1 --no-deps --locked --offline', ' ', '\t']) {
          let routeChecks = 0;
          assert.throws(() => context({ platform, architecture: platform === 'win32' ? 'x64' : 'arm64',
            environment: Object.freeze({ [spelling]: value }), exists: () => { routeChecks++; return true; } }), blocked('RUST_COMPILER_OVERRIDE'));
          assert.equal(routeChecks, 0, 'The selector must refuse before any tool route can be consulted');
        }
        for (const value of ['', undefined]) {
          const environment = Object.freeze({ [spelling]: value });
          const result = context({ platform, architecture: platform === 'win32' ? 'x64' : 'arm64', environment });
          assert.ok(!Object.keys(result.env).some(name => (platform === 'win32' ? name.toUpperCase() : name) === key));
          assert.deepEqual(environment, { [spelling]: value });
        }
      }
    }
  }
}));
test('blank compiler aliases are removed and dedicated child routes reset configured wrappers', () => fixture(({ context }) => {
  const environment = Object.freeze({
    cargo_build_rustc: '', Cargo_Build_Rustdoc: ' \t',
    CARGO_BUILD_RUSTC_WRAPPER: '', CARGO_BUILD_RUSTC_WORKSPACE_WRAPPER: ' ',
    rustc_wrapper: '', Rustc_Workspace_Wrapper: '', RUSTDOC: '',
    RUSTFLAGS: 'preserved-flags', CARGO_BUILD_JOBS: '2', PRESERVE_ME: 'present'
  });
  const before = { ...environment };
  const result = context({ environment });
  assert.equal(result.env.RUSTC, result.rustc);
  assert.equal(result.env.RUSTDOC, 'rustdoc.exe');
  assert.equal(result.env.RUSTC_WRAPPER, '');
  assert.equal(result.env.RUSTC_WORKSPACE_WRAPPER, '');
  for (const key of Object.keys(result.env)) assert.ok(!key.toUpperCase().startsWith('CARGO_BUILD_RUSTC') && key.toUpperCase() !== 'CARGO_BUILD_RUSTDOC');
  assert.equal(result.env.RUSTFLAGS, environment.RUSTFLAGS);
  assert.equal(result.env.CARGO_BUILD_JOBS, environment.CARGO_BUILD_JOBS);
  assert.equal(result.env.PRESERVE_ME, environment.PRESERVE_ME);
  assert.deepEqual(environment, before);
}));
test('scoped Cargo and Rust compiler shims bind both homes and prepend child PATH only', () => fixture(({ root, context }) => {
  const scopedRoot = path.join(root, 'artifacts/toolchain');
  const bin = path.join(scopedRoot, 'cargo/bin');
  const environment = Object.freeze({ Path: 'ordinary-bin;other-bin', CARGO_HOME: 'ambient-cargo', RUSTUP_HOME: 'ambient-rustup', RUSTUP_TOOLCHAIN: 'nightly', CARGO_TARGET_DIR: 'outside', CARGO_BUILD_TARGET: 'outside', PRESERVE_ME: 'present' });
  const before = { ...environment };
  const result = context({ environment, exists: candidate => [path.join(bin, 'cargo.exe'), path.join(bin, 'rustc.exe'), path.join(bin, 'rustdoc.exe')].includes(candidate) });
  assert.equal(result.cargo, path.join(bin, 'cargo.exe'));
  assert.equal(result.rustc, path.join(bin, 'rustc.exe'));
  assert.equal(result.env.RUSTC, result.rustc);
  assert.equal(result.env.RUSTDOC, path.join(bin, 'rustdoc.exe'));
  assert.equal(result.env.RUSTC_WRAPPER, '');
  assert.equal(result.env.RUSTC_WORKSPACE_WRAPPER, '');
  assert.equal(result.env.CARGO_HOME, path.join(scopedRoot, 'cargo'));
  assert.equal(result.env.RUSTUP_HOME, path.join(scopedRoot, 'rustup'));
  assert.equal(result.env.PATH, `${bin};ordinary-bin;other-bin`);
  assert.ok(!Object.hasOwn(result.env, 'Path'));
  assert.equal(result.env.PRESERVE_ME, 'present');
  assert.deepEqual(environment, before);
}));
test('scoped Apple Silicon routing uses native shim names and the Unix PATH delimiter', () => fixture(({ root, context }) => {
  const bin = path.join(root, 'artifacts/toolchain/cargo/bin');
  const result = context({ platform: 'darwin', architecture: 'arm64', environment: { PATH: '/usr/bin:/bin' }, exists: candidate => [path.join(bin, 'cargo'), path.join(bin, 'rustc'), path.join(bin, 'rustdoc')].includes(candidate) });
  assert.equal(result.cargo, path.join(bin, 'cargo'));
  assert.equal(result.rustc, path.join(bin, 'rustc'));
  assert.equal(result.env.RUSTC, result.rustc);
  assert.equal(result.env.RUSTDOC, path.join(bin, 'rustdoc'));
  assert.equal(result.env.PATH, `${bin}:/usr/bin:/bin`);
}));
test('a partial scoped installation cannot silently mix compiler routes', () => fixture(({ context }) => {
  assert.throws(() => context({ exists: candidate => candidate.endsWith('cargo.exe') }), blocked('RUST_SCOPED_SHIM_MISSING'));
  assert.throws(() => context({ exists: candidate => candidate.endsWith('cargo.exe') || candidate.endsWith('rustc.exe') }), blocked('RUST_SCOPED_SHIM_MISSING'));
}));
test('controlled environment spellings cannot preserve Windows ambient selectors', () => fixture(({ root, context }) => {
  const environment = { rustup_toolchain: 'nightly', Cargo_Target_Dir: 'old-output', cargo_build_target: 'wrong-target' };
  const before = { ...environment };
  const result = context({ environment });
  assert.equal(result.env.RUSTUP_TOOLCHAIN, '1.99.0');
  assert.equal(result.env.CARGO_TARGET_DIR, path.join(root, 'target'));
  assert.equal(result.env.CARGO_BUILD_TARGET, result.hostTarget);
  for (const key of Object.keys(before)) assert.ok(!Object.hasOwn(result.env, key));
  assert.deepEqual(environment, before);
}));
test('dependency pin mismatch and moving channels are rejected', () => fixture(({ context, metadata, toolchain }) => {
  metadata('1.98.0');
  assert.throws(() => context(), blocked('RUST_PIN_MISMATCH'));
  metadata('stable');
  assert.throws(() => context(), blocked('RUST_PIN_INVALID'));
  metadata('1.99.0');
  toolchain('[toolchain]\nchannel = "nightly"\n');
  assert.throws(() => context(), blocked('RUST_PIN_INVALID'));
}));
test('toolchain channel comes from one actual toolchain section', () => fixture(({ context, toolchain }) => {
  for (const source of [
    '# channel = "1.99.0"\n[toolchain]\nprofile = "minimal"\n',
    '[unrelated]\nchannel = "1.99.0"\n',
    '[toolchain]\nchannel = "1.99.0"\nchannel = "1.99.0"\n',
    '[toolchain]\nchannel = "1.99.0"\n[toolchain]\nprofile = "minimal"\n'
  ]) {
    toolchain(source);
    assert.throws(() => context(), blocked('RUST_PIN_INVALID'));
  }
  toolchain('\uFEFF[toolchain] # tracked\r\nchannel = \'1.99.0\' # exact version\r\nprofile = "minimal"\r\n');
  assert.equal(context().pin, '1.99.0');
}));
test('invalid roots, environment shapes and conflicting Windows PATH spellings fail', () => fixture(({ context }) => {
  assert.throws(() => context({ root: 'relative-root' }), blocked('RUST_CONTEXT_INVALID'));
  assert.throws(() => context({ environment: null }), blocked('RUST_CONTEXT_INVALID'));
  assert.throws(() => context({ environment: { PATH: 'first', Path: 'second' }, exists: () => true }), blocked('RUST_CONTEXT_INVALID'));
}));
