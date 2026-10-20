// Candidate-bound local receiver/store observation; not br-21 or GUI qualification.
import assert from 'node:assert/strict';
import { spawnSync } from 'node:child_process';
import { createHash, randomUUID } from 'node:crypto';
import { lstatSync, mkdirSync, readFileSync, realpathSync, unlinkSync, writeFileSync } from 'node:fs';
import path from 'node:path';
import { pathToFileURL } from 'node:url';
import { fingerprintInputRecords } from './input-tree.mjs';
import { ownedArtifactPath } from './owned-artifact.mjs';
import { rustContext } from './rust-context.mjs';
import { resolveHostTool } from './host-tools.mjs';
import { selectHostArtifacts } from './host-artifacts.mjs';

export const TEST_NAME = 'astrometrics_real_receiver_acceptance';
const sha = bytes => createHash('sha256').update(bytes).digest('hex');
const expectedEvidence = Object.freeze({
  schemaVersion: 'bridge-astrometrics-native-observation/v1', testName: TEST_NAME, success: true,
  authenticatedStatus: 200, unauthorizedStatus: 401, duplicateStatus: 200, conflictingStatus: 409,
  storedEvents: 2, disabledCreatesNoStore: true, disabledSocketClosed: true, idempotentReplay: true,
  conflictingReplayPreservesEvidence: true, maximumSequencePreserved: true,
  historyReadableWhileDisabled: true, historyReadLeavesDatabaseUnchanged: true,
  normalCloseJoinsOwner: true, normalCloseReleasesPort: true
});

export function requireInventory(output) {
  const names = output.split(/\r?\n/).filter(line => line.endsWith(': test')).map(line => line.slice(0, -6));
  assert.deepEqual(names, [TEST_NAME], 'Require the exact single integration test inventory');
}
export function requireExecution(output) {
  const summaries = [...output.matchAll(/^test result: ok\. (\d+) passed; (\d+) failed; (\d+) ignored; (\d+) measured; (\d+) filtered out;.*$/gm)];
  assert.equal(summaries.length, 1, 'Require one complete native execution summary');
  assert.deepEqual(summaries[0].slice(1).map(Number), [1, 0, 0, 0, 0], 'The exact test must execute and pass');
  assert.equal(output.split(/\r?\n/).filter(line => line.includes('ASTROMETRICS_ACCEPTANCE_OK')).length, 1,
    'Require the completed native observation marker');
}
export function requireObservation(bytes) {
  assert.ok(bytes.length > 0 && bytes.length <= 16384, 'Native observation has a bounded closed schema');
  const text = bytes.toString('utf8');
  assert.ok(Buffer.from(text, 'utf8').equals(bytes), 'Native observation must be UTF-8');
  const value = JSON.parse(text);
  assert.deepEqual(Object.keys(value).sort(), [...Object.keys(expectedEvidence), 'databaseSha256'].sort(),
    'Only sanitized declared evidence fields may be retained');
  for (const [key, expected] of Object.entries(expectedEvidence)) assert.equal(value[key], expected, 'Native assertion evidence differs');
  assert.match(value.databaseSha256, /^[0-9a-f]{64}$/, 'Require the observed database digest');
  return value;
}
export function nativeArchitecture(bytes, platform) {
  if (platform === 'win32') {
    assert.ok(bytes.length >= 64, 'Truncated Windows test executable');
    assert.equal(bytes.toString('ascii', 0, 2), 'MZ');
    const pe = bytes.readUInt32LE(0x3c);
    assert.ok(pe >= 64 && pe + 26 <= bytes.length, 'Invalid PE header extent');
    assert.equal(bytes.toString('ascii', pe, pe + 4), 'PE\0\0');
    assert.equal(bytes.readUInt16LE(pe + 4), 0x8664, 'Require native Windows x64');
    assert.equal(bytes.readUInt16LE(pe + 24), 0x20b, 'Require PE32+');
    return 'windows-x64-pe32+';
  }
  assert.equal(platform, 'darwin', 'Unsupported native host');
  assert.ok(bytes.length >= 32, 'Truncated Apple Silicon test executable');
  assert.equal(bytes.readUInt32LE(0), 0xfeedfacf, 'Require thin Mach-O 64');
  assert.equal(bytes.readUInt32LE(4), 0x0100000c, 'Require native Apple Silicon');
  assert.equal(bytes.readUInt32LE(12), 2, 'Require a Mach-O executable');
  return 'macos-arm64-mach-o';
}

export function fileIdentity(selected, limit = 256 * 1024 * 1024) {
  const stat = lstatSync(selected);
  assert.ok(stat.isFile() && !stat.isSymbolicLink() && stat.size > 0 && stat.size <= limit, 'Invalid bounded executable/artifact');
  const physical = realpathSync.native(selected), bytes = readFileSync(physical);
  const afterStat = lstatSync(selected);
  assert.ok(stat.dev === afterStat.dev && stat.ino === afterStat.ino && stat.size === afterStat.size
    && stat.mtimeMs === afterStat.mtimeMs && bytes.length === stat.size, 'Artifact changed while observed');
  return { path: physical, bytes: bytes.length, sha256: sha(bytes) };
}

export function toolIdentity(selected) {
  const route = path.resolve(selected), resolvedPath = realpathSync.native(route);
  const payload = fileIdentity(resolvedPath);
  assert.equal(realpathSync.native(route), resolvedPath, 'Tool invocation route changed');
  return { path: route, resolvedPath, bytes: payload.bytes, sha256: payload.sha256 };
}

export function cleanupFixture(root, fixtureDirectory) {
  const relative = path.relative(root, fixtureDirectory).replaceAll('\\', '/');
  const directory = ownedArtifactPath(root, relative, 'directory');
  let removed = 0;
  for (const name of ['fixture-events.sqlite', 'fixture-events.sqlite-journal', 'fixture-events.sqlite-wal', 'fixture-events.sqlite-shm']) {
    const selected = path.join(directory, name);
    try {
      const stat = lstatSync(selected);
      assert.ok(stat.isFile() && !stat.isSymbolicLink(), 'Refuse unexpected fixture cleanup target');
      ownedArtifactPath(root, `${relative}/${name}`);
      unlinkSync(selected);
      removed++;
    } catch (error) {
      if (error?.code !== 'ENOENT') throw error;
    }
  }
  return { status: 'complete', removed };
}

function main() {
  const root = path.resolve(import.meta.dirname, '../..');
  assert.equal(process.argv.length, 2, 'No caller acceptance selectors or paths');
  assert.deepEqual(process.execArgv, [], 'No caller Node loaders');
  assert.equal(realpathSync.native(process.cwd()), realpathSync.native(root), 'Canonical owning cwd required');
  for (const [name, value] of Object.entries(process.env)) {
    const key = name.toUpperCase();
    const override = /^(?:BRIDGE_ASTROMETRICS_|RUST_TEST_|LIBTEST_|CARGO_(?:TARGET_|PROFILE_|BUILD_|ALIAS_))/.test(key)
      || /^(?:RUSTFLAGS|RUSTDOCFLAGS|RUSTC_BOOTSTRAP|RUSTUP_FORCE_ARG0|RUSTUP_TOOLCHAIN|CARGO_TARGET_DIR|CARGO_ENCODED_RUSTFLAGS|CARGO_ENCODED_RUSTDOCFLAGS|NODE_OPTIONS|NODE_PATH|__COMPAT_LAYER|LD_PRELOAD|LD_LIBRARY_PATH|DYLD_.*)$/.test(key);
    assert.ok(!override || !value, 'Caller execution override is unsupported');
  }
  const rust = rustContext({ root });
  const metadata = JSON.parse(readFileSync(ownedArtifactPath(root, 'dependencies/next-toolchain.json'), 'utf8'));
  assert.equal(process.version, `v${metadata.node}`, 'Require the tracked Node runtime');
  const relative = selected => path.relative(root, selected).replaceAll('\\', '/');
  const directory = ownedArtifactPath(root, `artifacts/next/astrometrics-acceptance/${randomUUID()}`, 'directory', { allowMissing: true });
  mkdirSync(directory, { recursive: true });
  ownedArtifactPath(root, relative(directory), 'directory');
  const fixtureDirectory = ownedArtifactPath(root, `${relative(directory)}/fixture`, 'directory', { allowMissing: true });
  mkdirSync(fixtureDirectory);
  const inputs = ['Cargo.toml', 'Cargo.lock', 'rust-toolchain.toml', '.cargo/config.toml', 'dependencies/next-toolchain.json',
    'scripts/next', 'crates/bridge-app', 'crates/bridge-contracts', 'crates/bridge-domain', 'crates/bridge-engine',
    'crates/bridge-native', 'crates/bridge-profiles', 'crates/bridge-platform-windows', 'crates/bridge-platform-macos',
    'crates/bridge-journal-io', 'crates/bridge-toml', 'docs/next/ASTROMETRICS.md'];
  const startedAt = new Date().toISOString(), checks = [], tools = [];
  const toolchain = { pin: rust.pin, nativeTarget: rust.hostTarget };
  let before = null, after = null, candidateBefore = null, candidateAfter = null, binary = null, observation = null;
  let result = 'failed', failure = null, fixtureCleanup = null;
  function run(id, executable, argv, timeout = 30000, environment = rust.env) {
    const start = Date.now();
    const child = spawnSync(executable, argv, { cwd: root, env: environment, windowsHide: true, timeout, maxBuffer: 8 * 1024 * 1024 });
    const stdout = child.stdout ?? Buffer.alloc(0), stderr = child.stderr ?? Buffer.alloc(0);
    // Native failure output may contain assertion operands. Retain only hashes,
    // never raw responses, runtime secrets or an unreviewed panic message.
    checks.push({ id, executable, argv, startedAt: new Date(start).toISOString(), elapsedMs: Date.now() - start,
      exitCode: child.status, signal: child.signal ?? null, error: child.error?.code ?? null,
      stdout: { bytes: stdout.length, sha256: sha(stdout) }, stderr: { bytes: stderr.length, sha256: sha(stderr) } });
    assert.ok(child.status === 0 && !child.signal && !child.error, `Acceptance command failed: ${id}`);
    const text = stdout.toString('utf8');
    assert.ok(Buffer.from(text, 'utf8').equals(stdout), 'Command stdout is not UTF-8');
    return text;
  }
  function candidate(id) {
    const head = run(`${id}-head`, 'git', ['--no-optional-locks', 'rev-parse', 'HEAD']).trim();
    assert.match(head, /^[0-9a-f]{40}$/);
    const status = run(`${id}-status`, 'git', ['--no-optional-locks', 'status', '--porcelain=v1', '--untracked-files=all']);
    assert.equal(status, '', 'Freeze and commit source before native acceptance');
    return { head, clean: true };
  }
  try {
    candidateBefore = candidate('before');
    before = fingerprintInputRecords(root, inputs);
    const cargo = resolveHostTool(rust.cargo, rust.env), rustc = resolveHostTool(rust.rustc, rust.env);
    tools.push({ role: 'node', ...toolIdentity(process.execPath) }, { role: 'cargo-route', ...toolIdentity(cargo) },
      { role: 'rustc-route', ...toolIdentity(rustc) });
    const compilerVersion = run('rustc-version', rustc, [`+${rust.pin}`, '-vV']);
    assert.ok(compilerVersion.startsWith(`rustc ${rust.pin} `) && compilerVersion.split(/\r?\n/).includes(`host: ${rust.hostTarget}`),
      'Compiler release/host differs from the tracked native toolchain');
    toolchain.observedRustcVersion = compilerVersion.split(/\r?\n/)[0];
    const cargoVersion = run('cargo-version', cargo, [`+${rust.pin}`, '--version']);
    assert.ok(cargoVersion.startsWith(`cargo ${rust.pin} `), 'Cargo release differs from the tracked toolchain');
    toolchain.observedCargoVersion = cargoVersion.trim();
    const sysroot = run('rustc-sysroot', rustc, [`+${rust.pin}`, '--print', 'sysroot']).trim();
    assert.ok(path.isAbsolute(sysroot) && !/[\r\n]/.test(sysroot), 'Require one actual compiler sysroot');
    for (const name of ['cargo', 'rustc']) tools.push({ role: `toolchain-${name}`,
      ...toolIdentity(path.join(sysroot, 'bin', name + (process.platform === 'win32' ? '.exe' : ''))) });
    ownedArtifactPath(root, 'target', 'directory', { allowMissing: true });
    ownedArtifactPath(root, `target/${rust.hostTarget}`, 'directory', { allowMissing: true });
    const built = run('compile-native-test', cargo, [`+${rust.pin}`, 'test', '--locked', '--offline', '-p', 'bridge-app',
      '--test', 'astrometrics_acceptance', '--target', rust.hostTarget, '--no-run', '--message-format', 'json'], 600000);
    const [artifact] = selectHostArtifacts(built, { root, targets: [{ name: 'astrometrics_acceptance', kind: 'test',
      manifest: 'crates/bridge-app/Cargo.toml', source: 'crates/bridge-app/tests/astrometrics_acceptance.rs' }] });
    assert.ok(relative(artifact.executable).startsWith(`target/${rust.hostTarget}/`), 'Native test must belong to the owning target');
    const executable = ownedArtifactPath(root, relative(artifact.executable));
    binary = { target: artifact.target.name, ...fileIdentity(executable), architecture: nativeArchitecture(readFileSync(executable), process.platform) };
    const verifyBinary = () => assert.deepEqual(fileIdentity(executable), { path: binary.path, bytes: binary.bytes, sha256: binary.sha256 }, 'Native test bytes changed');
    requireInventory(run('native-test-inventory', executable, ['--list', '--color', 'never'], 15000));
    verifyBinary();
    after = fingerprintInputRecords(root, inputs);
    assert.deepEqual(after, before, 'Source changed before native test admission');
    const output = run('native-receiver-acceptance', executable,
      ['--ignored', '--exact', TEST_NAME, '--test-threads=1', '--color', 'never', '--nocapture'], 90000,
      { ...rust.env, BRIDGE_ASTROMETRICS_ACCEPTANCE_DIR: fixtureDirectory });
    requireExecution(output);
    verifyBinary();
    const evidencePath = ownedArtifactPath(root, `${relative(fixtureDirectory)}/astrometrics-observation.json`);
    const evidenceIdentity = fileIdentity(evidencePath, 16384);
    observation = { file: { ...evidenceIdentity, path: relative(evidencePath) }, value: requireObservation(readFileSync(evidencePath)) };
    after = fingerprintInputRecords(root, inputs);
    assert.deepEqual(after, before, 'Source changed during native acceptance');
    candidateAfter = candidate('after');
    assert.deepEqual(candidateAfter, candidateBefore, 'Repository candidate changed');
    for (const tool of tools) assert.deepEqual(toolIdentity(tool.path), { path: tool.path, resolvedPath: tool.resolvedPath, bytes: tool.bytes, sha256: tool.sha256 }, 'Tool route or payload changed');
    after = fingerprintInputRecords(root, inputs);
    assert.deepEqual(after, before, 'Source changed during final tool verification');
    result = 'passed';
  } catch (error) {
    // Do not serialize child output, assertion operands or arbitrary exception text.
    failure = { code: 'ASTROMETRICS_ACCEPTANCE_FAILED', category: error?.name === 'AssertionError' ? 'evidence-refused' : 'execution-failed' };
  } finally {
    try { fixtureCleanup = cleanupFixture(root, fixtureDirectory); }
    catch {
      fixtureCleanup = { status: 'failed' };
      result = 'failed';
      failure = { code: 'ASTROMETRICS_FIXTURE_CLEANUP_FAILED', category: 'evidence-refused' };
    }
    if (before && !after) {
      try { after = fingerprintInputRecords(root, inputs); } catch { after = null; }
    }
    const receiptPath = ownedArtifactPath(root, `${relative(directory)}/receipt.json`, 'file', { allowMissing: true });
    writeFileSync(receiptPath, JSON.stringify({ schemaVersion: 'bridge-astrometrics-acceptance/v1', result, startedAt,
      completedAt: new Date().toISOString(), host: { platform: process.platform, architecture: process.arch, node: process.version },
      toolchain, candidateBefore, candidateAfter, inputs,
      sourcesBefore: before, sourcesAfter: after, tools, binary, checks, observation, failure, fixtureCleanup,
      boundary: 'Exact native bridge-app integration test using the real local HTTP receiver and SQLite store with synthetic producer payloads. No game/mod producer, desktop GUI, abrupt process interruption, other-host execution or release qualification.',
      br21Accepted: false, liveModProducerObserved: false, nativeWebviewQualified: false,
      abruptRecoveryQualified: false, otherNativeHostQualified: false, releaseQualified: false
    }, null, 2) + '\n', { flag: 'wx' });
    const latest = ownedArtifactPath(root, 'artifacts/next/astrometrics-acceptance/latest.json', 'file', { allowMissing: true });
    writeFileSync(latest, readFileSync(receiptPath));
    console.log(JSON.stringify({ result, receipt: relative(receiptPath), sha256: sha(readFileSync(receiptPath)),
      nativeTarget: rust.hostTarget, tests: result === 'passed' ? 1 : 0, br21Accepted: false, releaseQualified: false }));
    process.exitCode = result === 'passed' ? 0 : 1;
  }
}

if (process.argv[1] && import.meta.url === pathToFileURL(path.resolve(process.argv[1])).href) main();
