import test from 'node:test';
import assert from 'node:assert/strict';
import path from 'node:path';
import { fork, spawnSync } from 'node:child_process';
import { EventEmitter } from 'node:events';
import { readFileSync } from 'node:fs';
import { macFixtureAdmission, macMachO, macArtifactInventory, macPsRows,
  macPsObservation, macDfVolume, macAclAbsent, MacSupervisorAdmission, macBoundedDiagnostic,
  macFixtureCommandRouting, macCaptureChild, macDocEvidence } from '../macos-platform-fixtures.mjs';

test('native doc parser requires both journal ownership blocks and refuses duplicate proof', async () => {
  const items = [
    ['filesystem.rs', 'filesystem::RetainedDirectory', '::RetainedDirectory;'],
    ['filesystem.rs', 'filesystem::ReadOnlyFile', '::ReadOnlyFile;'],
    ['filesystem.rs', 'filesystem::StagedReplacement', '::StagedReplacement;'],
    ['process.rs', 'process::ExactProcessGuard', '::ExactProcessGuard;'],
    ['secrets.rs', 'secrets::Plaintext', '::Plaintext;'],
    ['private_journal.rs', 'private_journal::NativePrivateJournalStorage', 'require_send::<bridge_platform_macos::NativePrivateJournalStorage>();'],
    ['private_journal.rs', 'private_journal::NativePrivateJournalStorage', 'require_sync::<bridge_platform_macos::NativePrivateJournalStorage>();']
  ];
  const rows = items.map(([file, item, needle]) => {
    const source = readFileSync(new URL(`../../../crates/bridge-platform-macos/src/${file}`, import.meta.url), 'utf8').split(/\r?\n/);
    const body = source.findIndex(line => line.includes(needle));
    assert.ok(body > 0);
    let begin = body;
    while (begin >= 0 && !/^\/\/\/ ```compile_fail\s*$/.test(source[begin])) begin--;
    assert.ok(begin >= 0);
    return `test src/${file} - ${item} (line ${begin + 1}) - compile fail ... ok`;
  });
  const summary = '\ntest result: ok. 7 passed; 0 failed; 0 ignored; 0 measured; 0 filtered out; finished in 0.01s\n';
  assert.equal((await macDocEvidence(rows.join('\n') + summary)).length, 7);
  await assert.rejects(macDocEvidence([...rows.slice(0, 6), rows[5]].join('\n') + summary), { code: 'MAC_FIXTURE_DOC_ORIGIN' });
  await assert.rejects(macDocEvidence(rows.slice(0, 6).join('\n') + summary), { code: 'MAC_FIXTURE_DOC_INVENTORY' });
});

// Portable parsing/refusal and Node invocation assertions do not execute the
// native harness, authenticate CI metadata or qualify an Apple Silicon host.
const admission = () => ({ argv: ['node', 'macos-platform-fixtures.mjs'], platform: 'darwin', architecture: 'arm64',
  version: 'v24.14.1', execArgv: [], environment: { GITHUB_ACTIONS: 'true', RUNNER_ENVIRONMENT: 'github-hosted',
    RUNNER_OS: 'macOS', RUNNER_ARCH: 'ARM64', GITHUB_REPOSITORY: 'Guffawaffle/stfc-mod-bridge',
    GITHUB_SHA: 'a'.repeat(40), GITHUB_RUN_ID: '123', GITHUB_RUN_ATTEMPT: '1', GITHUB_JOB: 'foundation' } });

test('Mac fixture entry refuses foreign hosts, unbound jobs and public selectors before native work', () => {
  assert.doesNotThrow(() => macFixtureAdmission(admission()));
  for (const delta of [{ platform: 'win32' }, { architecture: 'x64' }, { version: 'v24.14.0' },
    { argv: ['node', 'fixture', '--helper=/tmp/other'] }, { execArgv: ['--import', 'other.mjs'] }]) {
    assert.throws(() => macFixtureAdmission({ ...admission(), ...delta }));
  }
  for (const delta of [{ RUNNER_ENVIRONMENT: 'self-hosted' }, { RUNNER_ARCH: 'X64' },
    { GITHUB_REPOSITORY: 'other/repo' }, { GITHUB_SHA: 'not-a-commit' }, { GITHUB_RUN_ID: '0' },
    { GITHUB_RUN_ATTEMPT: '0' }, { GITHUB_JOB: '../other' }, { NODE_OPTIONS: '--require other.cjs' },
    { BRIDGE_MACOS_KEYCHAIN_FIXTURE: 'yes' }, { bridge_macos_helper: '/other' }, { BASH_ENV: '/other' },
    { ENV: '/other' }, { SHELLOPTS: 'xtrace' }, { 'BASH_FUNC_exec%%': '() { false; }' },
    { DYLD_INSERT_LIBRARIES: '/other.dylib' }]) {
    const value = admission(); Object.assign(value.environment, delta);
    assert.throws(() => macFixtureAdmission(value));
  }
});

test('Rustup dispatch cannot be replaced by inherited force-arg0 values, including whitespace', () => {
  for (const value of ['rustup-init', 'rustc', 'rustup', ' ', '\n']) {
    const input = admission(); input.environment.RUSTUP_FORCE_ARG0 = value;
    assert.throws(() => macFixtureAdmission(input), { code: 'MAC_FIXTURE_TOOL_OVERRIDE' });
  }
  const input = admission(); input.environment.RUSTUP_FORCE_ARG0 = '';
  assert.doesNotThrow(() => macFixtureAdmission(input));
});
test('Mac volume output cannot inherit nonempty libxo formatting options, including whitespace', () => {
  for (const value of ['json', 'html', 'color', ' ', '\n']) {
    const input = admission(); input.environment.LIBXO_OPTIONS = value;
    assert.throws(() => macFixtureAdmission(input), { code: 'MAC_FIXTURE_TOOL_OVERRIDE' });
  }
  const input = admission(); input.environment.LIBXO_OPTIONS = '';
  assert.doesNotThrow(() => macFixtureAdmission(input));
});

test('multicall routing keeps the physical executable while supplying the fixed Rustup dispatch name', () => {
  const route = macFixtureCommandRouting(process.execPath, 'rustup');
  const result = spawnSync(route.executable, ['--eval', 'process.stdout.write(JSON.stringify({ argv0: process.argv0, executable: process.execPath }))'],
    { argv0: route.argv0, shell: false, encoding: 'utf8', timeout: 10000 });
  assert.equal(result.error, undefined);
  assert.equal(result.status, 0);
  assert.equal(result.signal, null);
  assert.deepEqual(JSON.parse(result.stdout), { argv0: 'rustup', executable: process.execPath });
  assert.deepEqual(macFixtureCommandRouting(process.execPath), { executable: process.execPath, argv0: process.execPath });
  for (const name of ['rustup-init', 'rustc', '', ' rustup ', '--other']) {
    assert.throws(() => macFixtureCommandRouting(process.execPath, name), { code: 'MAC_FIXTURE_TOOL_DISPATCH' });
  }
  assert.throws(() => macFixtureCommandRouting('rustup', 'rustup'), { code: 'MAC_FIXTURE_TOOL_ROUTE' });
});

function macho() {
  const bytes = Buffer.alloc(40); bytes.writeUInt32LE(0xfeedfacf, 0); bytes.writeUInt32LE(0x0100000c, 4);
  bytes.writeUInt32LE(2, 12); bytes.writeUInt32LE(1, 16); bytes.writeUInt32LE(8, 20);
  bytes.writeUInt32LE(1, 32); bytes.writeUInt32LE(8, 36); return bytes;
}
test('Mac executable parser refuses universal, Intel, wrong-type and malformed load-command artifacts', () => {
  assert.equal(macMachO(macho()).architecture, 'arm64');
  assert.throws(() => macMachO(Buffer.alloc(31)));
  for (const [offset, value] of [[0, 0xcafebabe], [4, 0x01000007], [12, 6], [16, 0],
    [16, 2], [20, 16], [36, 7], [36, 16]]) {
    const bytes = macho(); bytes.writeUInt32LE(value, offset); assert.throws(() => macMachO(bytes));
  }
});

test('Mac process observations bind bounded unique PIDs to only the captured group', () => {
  assert.deepEqual(macPsRows(' 200 200 Ss\n 201 200 T+\n', 200),
    [{ pid: 200, pgid: 200, state: 'Ss' }, { pid: 201, pgid: 200, state: 'T+' }]);
  for (const text of ['PID PGID STAT\n', '200 201 S\n', '200 200 S\n200 200 T\n',
    '2147483648 200 S\n', '0 200 S\n', '200 200 S /unrelated/path\n', '200 200 S!\n', 'x'.repeat(8193)]) {
    assert.throws(() => macPsRows(text, 200));
  }
  assert.throws(() => macPsRows('200 200 S\n', 1));
});
test('Mac group absence requires the complete fixed ps no-match contract', () => {
  const observation = { stdout: '', stderr: '', closed: true, error: null, signal: null, exitCode: 1 };
  assert.deepEqual(macPsObservation(observation, 200), []);
  assert.equal(macPsObservation({ ...observation, exitCode: 0, stdout: '200 200 Ss\n' }, 200).length, 1);
  for (const delta of [{ exitCode: 0 }, { exitCode: 2 }, { stderr: 'permission denied' },
    { stdout: '200 200 S\n' }, { closed: false }, { error: 'timeout' }, { signal: 'SIGKILL' }]) {
    assert.throws(() => macPsObservation({ ...observation, ...delta }, 200));
  }
});

const volumeHeader = 'Filesystem    Type 1024-blocks Used Available Capacity Mounted on\n';
const volumeRow = '/dev/disk3s5   apfs 123456 456 123000 1% /System/Volumes/Data\n';
test('Mac volume parser preserves the actual APFS device and mountpoint without a root fallback', () => {
  assert.deepEqual(macDfVolume(volumeHeader + volumeRow),
    { filesystemType: 'apfs', device: '/dev/disk3s5', mountPoint: '/System/Volumes/Data' });
  assert.deepEqual(macDfVolume(volumeHeader + volumeRow.replace('disk3s5', 'disk3s1s1').replace('/System/Volumes/Data', '/')),
    { filesystemType: 'apfs', device: '/dev/disk3s1s1', mountPoint: '/' });
  assert.deepEqual(macDfVolume(volumeHeader + volumeRow.replace('/System/Volumes/Data', '/Volumes/Fixture Disk')),
    { filesystemType: 'apfs', device: '/dev/disk3s5', mountPoint: '/Volumes/Fixture Disk' });
});
test('Mac volume parser refuses unknown headers, extra rows and alternate units or inode columns', () => {
  for (const value of ['', volumeRow, volumeHeader, volumeHeader + volumeRow + volumeRow,
    volumeHeader + volumeRow + '\n', (volumeHeader + volumeRow).trimEnd(),
    volumeHeader.replace('Type ', '') + volumeRow, volumeHeader.replace('Available', 'Avail') + volumeRow,
    volumeHeader.replace('1024-blocks', '512-blocks') + volumeRow,
    volumeHeader.replace('1024-blocks', '1K-blocks') + volumeRow,
    volumeHeader.replace('Capacity ', 'Capacity iused ifree %iused ') + volumeRow,
    (volumeHeader + volumeRow).replaceAll('\n', '\r\n'), 'x'.repeat(8193)]) {
    assert.throws(() => macDfVolume(value), { code: 'MAC_FIXTURE_VOLUME_UNKNOWN' });
  }
});
test('Mac volume parser refuses foreign filesystems, non-device sources and invalid numeric fields', () => {
  for (const row of [volumeRow.replace('apfs', 'hfs'), volumeRow.replace('apfs', 'APFS'),
    volumeRow.replace('/dev/disk3s5', 'server:/export'), volumeRow.replace('/dev/disk3s5', '/dev/rdisk3s5'),
    volumeRow.replace('/dev/disk3s5', '/dev/disk3'), volumeRow.replace('/dev/disk3s5', '/dev/disk3s5-extra'),
    volumeRow.replace('123456', '123Gi'), volumeRow.replace('123456', '-1'), volumeRow.replace('1%', '101%'),
    volumeRow.replace('1%', 'unknown%'), volumeRow.replace('1%', '-1%')]) {
    assert.throws(() => macDfVolume(volumeHeader + row), { code: 'MAC_FIXTURE_VOLUME_UNKNOWN' });
  }
});
test('Mac volume parser refuses relative, ambiguous and control-bearing mountpoint paths', () => {
  for (const mount of ['relative', '/Volumes/../Data', '/Volumes//Data', '//Volumes/Data', '/Volumes/Data/',
    '/Volumes/./Data', '/Volumes/Bad\tName', '/Volumes/Bad\0Name', '/Volumes/Bad\u007fName',
    '/Volumes/Bad\ufffdName', '/Volumes/Bad\nName']) {
    assert.throws(() => macDfVolume(volumeHeader + volumeRow.replace('/System/Volumes/Data', mount)),
      { code: 'MAC_FIXTURE_VOLUME_UNKNOWN' });
  }
});
test('Mac private fixture ACL observation accepts exact owner mode and refuses unknown or extended ACL rows', () => {
  assert.equal(macAclAbsent('drwx------ 2 runner staff 64 Oct 4 10:00 /fixture\n', 'directory').extendedAcl, 'observed-absent');
  assert.equal(macAclAbsent('-rwx------@ 1 runner staff 40 Oct 4 10:00 /helper\n', 'file').xattrMarker, true);
  for (const [value, kind] of [['drwx------+', 'directory'], ['drwxr-x---', 'directory'],
    ['-rwx------', 'directory'], ['-rwx------\n 0: user:other allow read', 'file'], ['', 'file'],
    ['drwx------', 'unknown']]) assert.throws(() => macAclAbsent(value, kind));
});

function artifactFixture() {
  const root = path.resolve('synthetic-mac-parser-root'), manifest = path.join(root, 'crate', 'Cargo.toml'),
    source = path.join(root, 'crate', 'tests', 'native.rs'), targetPrefix = path.join(root, 'target', 'aarch64-apple-darwin');
  const selected = { reason: 'compiler-artifact', manifest_path: manifest, target: { name: 'native', kind: ['test'], src_path: source },
    profile: { test: true }, executable: path.join(targetPrefix, 'debug', 'deps', 'native-123') };
  return { selected, options: { name: 'native', kind: 'test', manifest, source, targetPrefix, test: true },
    finished: { reason: 'build-finished', success: true } };
}
test('Mac compiler artifact selection requires exact source, target, profile and one successful build', () => {
  const { selected, options, finished } = artifactFixture();
  const output = values => values.map(value => JSON.stringify(value)).join('\n');
  assert.deepEqual(macArtifactInventory(output([selected, finished]), options), selected);
  for (const values of [[selected], [selected, { ...finished, success: false }], [selected, finished, finished],
    [selected, selected, finished], [{ ...selected, profile: { test: false } }, finished],
    [{ ...selected, target: { ...selected.target, kind: ['test', 'bin'] } }, finished],
    [{ ...selected, manifest_path: path.join(options.manifest, '..', 'other.toml') }, finished],
    [{ ...selected, target: { ...selected.target, src_path: path.join(options.source, '..', 'other.rs') } }, finished],
    [{ ...selected, executable: path.join(options.targetPrefix, '..', 'other', 'native') }, finished]]) {
    assert.throws(() => macArtifactInventory(output(values), options));
  }
});

test('Mac supervisor cancellation during delayed validation permanently prevents anchor and child admission', async () => {
    const admission = new MacSupervisorAdmission(), validation = Promise.withResolvers();
    let launches = 0;
    const initialization = (async () => { await admission.observe(() => validation.promise); admission.admitAnchor(); admission.admitLaunch(); launches++; })();
    const refusal = assert.rejects(initialization, error => error.code === 'MAC_FIXTURE_SUPERVISOR_CANCELLED');
    admission.cancel(); validation.resolve('successful later validation'); await refusal;
    assert.equal(admission.anchored, false); assert.equal(admission.launches, 0); assert.equal(launches, 0);
    assert.throws(() => admission.admitAnchor()); assert.throws(() => admission.admitLaunch());
    await assert.rejects(admission.observe(() => Promise.resolve()), error => error.code === 'MAC_FIXTURE_SUPERVISOR_CANCELLED');
});
test('Mac supervisor cancellation after anchoring prevents launch and later continuation', async () => {
  const admission = new MacSupervisorAdmission(); admission.admitAnchor(); admission.cancel();
  assert.equal(admission.anchored, true); assert.equal(admission.launches, 0);
  assert.throws(() => admission.admitLaunch());
  const running = new MacSupervisorAdmission(); running.admitAnchor(); running.admitLaunch(); running.cancel();
  assert.throws(() => running.active()); assert.throws(() => running.admitLaunch()); assert.equal(running.launches, 1);
});
test('Mac supervisor diagnostic failure or a stalled pipe cannot hold disposal indefinitely', async () => {
  assert.equal(await macBoundedDiagnostic(() => Promise.resolve()), true);
  assert.equal(await macBoundedDiagnostic(() => Promise.reject(Error('closed pipe'))), false);
  assert.equal(await macBoundedDiagnostic(() => new Promise(() => {})), false);
});

function observedChild({ ipc = true, stdin = true } = {}) {
  return Object.assign(new EventEmitter(), { channel: ipc ? {} : null,
    stdin: stdin ? new EventEmitter() : null, stdout: new EventEmitter(), stderr: new EventEmitter() });
}
function completeOutput(child) {
  for (const name of ['stdout', 'stderr']) { child[name].emit('end'); child[name].emit('close'); }
}
test('Mac capture exit alone waits for both output EOFs, pipe closure, stdin and IPC', async () => {
  const child = observedChild(); let settled = false;
  const captured = macCaptureChild(child, 1000, { onFailure: () => {} }).then(value => { settled = true; return value; });
  child.emit('exit', 2, null); completeOutput(child);
  await Promise.resolve(); assert.equal(settled, false);
  child.stdin.emit('close'); await Promise.resolve(); assert.equal(settled, false);
  child.emit('disconnect'); const result = await captured;
  assert.equal(result.closed, true); assert.equal(result.exitCode, 2); assert.equal(result.error, null);
  assert.equal(result.lifecycle.nodeCloseObserved, false); assert.equal(result.lifecycle.ipcDisconnected, true);
});
test('Mac capture pipe closure and IPC without actual exit cannot finish observation', async () => {
  const child = observedChild(); let settled = false;
  const captured = macCaptureChild(child, 1000, { onFailure: () => {} }).then(value => { settled = true; return value; });
  completeOutput(child); child.stdin.emit('close'); child.emit('disconnect');
  await Promise.resolve(); assert.equal(settled, false);
  child.emit('exit', 2, null); const result = await captured;
  assert.deepEqual(result.lifecycle.exit, { exitCode: 2, signal: null }); assert.equal(result.closed, true);
});
test('Mac capture requires EOF in addition to closed pipes and preserves an output failure', async () => {
  const child = observedChild({ ipc: false, stdin: false }); let settled = false;
  const captured = macCaptureChild(child, 1000, { onFailure: () => {} }).then(value => { settled = true; return value; });
  child.stdout.emit('error', Error('controlled pipe failure'));
  child.emit('exit', 2, null); child.stdout.emit('close'); child.stderr.emit('close');
  await Promise.resolve(); assert.equal(settled, false);
  child.stdout.emit('end'); await Promise.resolve(); assert.equal(settled, false);
  child.stderr.emit('end'); const result = await captured;
  assert.equal(result.closed, true); assert.equal(result.error, 'MAC_FIXTURE_COMMAND_OUTPUT');
});
test('Mac capture observes an actual owned Node fork after parent IPC disconnect without relying on close', async () => {
  const child = fork(path.join(import.meta.dirname, 'fixtures', 'node-ipc-close-child.mjs'), [], {
    execPath: process.execPath, execArgv: [], windowsHide: true, serialization: 'json', stdio: ['ignore', 'pipe', 'pipe', 'ipc'] });
  let exitObservation, exitResolve;
  const exited = new Promise(resolve => { exitResolve = resolve; });
  child.once('exit', (exitCode, signal) => { exitObservation = { exitCode, signal }; exitResolve(); });
  const captured = macCaptureChild(child, 3000, { maxOutput: 8192, onFailure: () => {} });
  let output = '';
  child.stdout.on('data', bytes => {
    output += bytes.toString('utf8');
    if (output.includes('ready\n') && child.connected) child.disconnect();
  });
  let cleanupTimer;
  try {
    child.send('observe-disconnect');
    const result = await captured;
    assert.equal(result.closed, true); assert.equal(result.error, null);
    assert.equal(result.exitCode, 2); assert.equal(result.signal, null);
    assert.deepEqual(result.lifecycle.exit, { exitCode: 2, signal: null });
    assert.deepEqual(result.lifecycle.stdout, { end: true, close: true });
    assert.deepEqual(result.lifecycle.stderr, { end: true, close: true });
    assert.equal(result.lifecycle.ipcDisconnected, true); assert.equal(result.stdout, 'ready\nrefused\n');
  } finally {
    // Kill only this retained child if exit is still unobserved. The fixture's
    // own deadline is independent, but deadline intent is never exit evidence.
    if (!exitObservation) assert.equal(child.kill('SIGKILL'), true);
    try {
      assert.equal(await Promise.race([exited.then(() => true), new Promise(resolve => {
        cleanupTimer = setTimeout(() => resolve(false), 3000);
      })]), true, 'owned probe exit must actually be observed');
    } finally { clearTimeout(cleanupTimer); }
  }
});
