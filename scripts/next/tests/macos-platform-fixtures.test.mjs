import test from 'node:test';
import assert from 'node:assert/strict';
import path from 'node:path';
import { spawnSync } from 'node:child_process';
import { macFixtureAdmission, macMachO, macArtifactInventory, macPsRows,
  macPsObservation, macDfVolume, macAclAbsent, MacSupervisorAdmission, macBoundedDiagnostic,
  macFixtureCommandRouting } from '../macos-platform-fixtures.mjs';

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
