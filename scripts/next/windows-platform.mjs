import assert from 'node:assert/strict';
import { spawnSync } from 'node:child_process';
import { createHash, randomUUID } from 'node:crypto';
import { mkdirSync, readFileSync, realpathSync, writeFileSync } from 'node:fs';
import path from 'node:path';
import { rustContext } from './rust-context.mjs';
import { ownedArtifactPath } from './owned-artifact.mjs';
import { fingerprintInputRecords } from './input-tree.mjs';
import { nativePhysicalPath, nativeTestInventory, nativeTestResult } from './native-evidence.mjs';
import { NATIVE_TESTS } from './windows-private-journal-evidence.mjs';

const root = path.resolve(import.meta.dirname, '../..');
assert.equal(process.argv.length, 2, 'Windows gate accepts no overrides');
assert.equal(process.platform, 'win32'); assert.equal(process.arch, 'x64');
assert.equal(realpathSync(process.cwd()), realpathSync(root));
const rust = rustContext({ root }), sha = bytes => createHash('sha256').update(bytes).digest('hex');
for (const key of Object.keys(process.env)) assert.ok(!/^BRIDGE_(?:TEST_WINDOWS_|WINDOWS_CHILD_)/i.test(key) || !process.env[key], 'Caller cannot inject private native fixture selectors');
const inputs = ['Cargo.toml', 'Cargo.lock', 'rust-toolchain.toml', '.cargo/config.toml', 'dependencies/next-toolchain.json',
  'dependencies/next-windows-signature-fixture.json', 'docs/next/WINDOWS_PLATFORM.md', 'docs/next/PRIVATE_JOURNAL_STORAGE.md', 'scripts/next', 'contracts', 'crates/bridge-engine', 'crates/bridge-toml', 'crates/bridge-native', 'crates/bridge-journal-io', 'crates/bridge-domain', 'crates/bridge-contracts', 'crates/bridge-platform-windows'];
const before = fingerprintInputRecords(root, inputs);
const directory = ownedArtifactPath(root, `artifacts/next/windows-platform/${randomUUID()}`, 'directory', { allowMissing: true });
mkdirSync(directory, { recursive: true });
const fixture = ownedArtifactPath(root, `${path.relative(root, directory).replaceAll('\\', '/')}/fixtures`, 'directory', { allowMissing: true });
mkdirSync(fixture);
const pinPath = ownedArtifactPath(root, 'dependencies/next-windows-signature-fixture.json'), pinBytes = readFileSync(pinPath), pin = JSON.parse(pinBytes);
assert.equal(pin.schemaVersion, 'bridge-windows-signature-fixture-pin/v1'); assert.equal(pin.nodeVersion, process.version);
assert.equal(pin.sha256, '58e74bf02fc5bbacc41dcb8bef089961cd5bddd37830b87784e4fc624d145d1f');
assert.equal(pin.primarySignerCertificateSha256, '9b58b2c9c5a8fe0ab28597026203f9afde121f48c83c41efc57548eb37aea241');
const nodeBytes = readFileSync(process.execPath); assert.equal(sha(nodeBytes), pin.sha256);
const signedSubject = path.join(fixture, 'signed-subject.exe');
writeFileSync(signedSubject, nodeBytes, { flag: 'wx' });
writeFileSync(path.join(fixture, 'signed-subject.json'), JSON.stringify({ schemaVersion: 'bridge-windows-signed-subject/v1', relativePath: 'signed-subject.exe',
  sha256: pin.sha256, primarySignerCertificateSha256: pin.primarySignerCertificateSha256, revocationPolicy: 'cache_only', unsupportedSignatureIndex: 1 }, null, 2) + '\n', { flag: 'wx' });
const env = { ...process.env, BRIDGE_TEST_WINDOWS_FIXTURE_ROOT: fixture, BRIDGE_TEST_WINDOWS_SIGNED_SUBJECT: signedSubject };
const checks = [], binaries = [], observations = [];
function run(id, executable, argv, timeout = 180000) {
  const startedAt = new Date().toISOString(), result = spawnSync(executable, argv, { cwd: root, env, windowsHide: true, encoding: 'utf8', timeout, maxBuffer: 8 * 1024 * 1024 });
  const value = { id, executable, argv, cwd: root, startedAt, completedAt: new Date().toISOString(), exitCode: result.status, error: result.error?.code ?? null, stdout: result.stdout ?? '', stderr: result.stderr ?? '' };
  checks.push(value); writeFileSync(path.join(directory, `${id}.json`), JSON.stringify(value, null, 2) + '\n', { flag: 'wx' });
  assert.equal(result.status, 0, `Windows ${id} failed; retained observation`); assert.ok(!result.error); return value.stdout;
}
const cargo = (id, argv) => run(id, process.execPath, ['scripts/next/cargo.mjs', ...argv]);
cargo('format', ['fmt', '-p', 'bridge-platform-windows', '--', '--check']);
cargo('clippy', ['clippy', '--locked', '-p', 'bridge-platform-windows', '--all-targets', '--', '-D', 'warnings']);
nativeTestResult(cargo('ownership-docs', ['test', '--locked', '-p', 'bridge-platform-windows', '--doc']), 5);
function compile(kind, target, source, selection) {
  const output = cargo(`compile-${target}`, ['test', '--locked', '-p', 'bridge-platform-windows', ...selection, '--no-run', '--message-format', 'json']);
  const artifacts = output.split(/\r?\n/).filter(Boolean).map(line => JSON.parse(line)).filter(value => value.reason === 'compiler-artifact' && value.target.name === target && value.target.kind.includes(kind) && value.profile.test && value.executable);
  assert.equal(artifacts.length, 1, 'Exact current invocation must identify one native test artifact');
  const artifact = artifacts[0];
  assert.equal(nativePhysicalPath(artifact.manifest_path), nativePhysicalPath(path.join(root, 'crates/bridge-platform-windows/Cargo.toml')));
  assert.equal(nativePhysicalPath(artifact.target.src_path), nativePhysicalPath(path.join(root, source)));
  const relative = path.relative(root, artifact.executable).replaceAll('\\', '/'); assert.ok(relative.startsWith(`target/${rust.hostTarget}/`));
  const executable = ownedArtifactPath(root, relative), bytes = readFileSync(executable);
  assert.equal(bytes.toString('ascii', 0, 2), 'MZ'); assert.equal(bytes.readUInt16LE(bytes.readUInt32LE(0x3c) + 4), 0x8664);
  const binary = { target, executable, bytes: bytes.length, sha256: sha(bytes), compilerArtifact: artifact }; binaries.push(binary); return binary;
}
const units = [
  'filesystem::tests::child_names_reject_aliases_streams_and_reserved_devices',
  'native::tests::explicit_paths_reject_devices_streams_relative_and_parent_routes', 'native::tests::native_errors_do_not_retain_diagnostic_strings',
  'secrets::tests::secret_entropy_is_closed_versioned_and_domain_separated', 'secrets::tests::plaintext_wipe_clears_owned_bytes',
  'shell::tests::literal_arguments_preserve_quotes_empty_and_trailing_slashes', 'shell::tests::argument_preflight_bounds_escaped_utf16_before_output_allocation',
  'providers::tests::random_fill_uses_initialized_owned_bytes_once_and_preserves_non_uuid_bits',
  'providers::tests::every_nonzero_rng_status_refuses_and_wipes_partial_or_complete_output',
  'providers::tests::failed_rng_call_does_not_retry_or_return_partially_written_bytes',
  'providers::tests::native_monotonic_reading_is_bracketed_by_windows_uptime_in_milliseconds',
  'providers::tests::native_preferred_rng_returns_bounded_owned_raw_bytes',
  ...[
    'pending_preserves_all_output_addresses_and_refuses_second_submission',
    'unwind_before_classification_retains_constructor_custody',
    'completed_error_destroys_custody_before_reservation_release',
    'competing_constructor_cannot_submit_before_leaf_acquisition',
    'boxed_storage_early_error_runs_the_same_quarantine_drop',
    'real_create_graph_remains_stable_after_quarantine_and_drop',
    'real_query_outputs_accept_late_mock_completion_after_unwind',
    'trait_object_early_shared_validation_failure_retains_custody',
    'completed_conflicting_or_unwritten_iosb_is_refusal_not_quarantine',
    'full_namespace_protocol_repeats_after_every_completed_failure',
    'path_policy_refuses_redirected_alias_and_unbounded_routes',
    'sid_extent_checks_interior_pointer_and_count_before_native_helpers',
    'synthetic_private_security_rejects_grants_owner_mask_and_inheritance_drift'
  ].map(name => `private_journal::custody_tests::${name}`),
  ...['fixture_nonce_requires_canonical_v4_and_refuses_all_aliases',
    'fixture_child_protocol_has_closed_phase_and_bounded_frame',
    'fixture_extension_bounds_are_checked_before_namespace_effects'
  ].map(name => `private_journal::native_fixtures::${name}`),
  ...NATIVE_TESTS, 'private_journal::native_fixtures::fixture_private_journal_child'
];
const skippedUnits = [...NATIVE_TESTS, 'private_journal::native_fixtures::fixture_private_journal_child'];
assert.equal(units.length, 38); assert.equal(skippedUnits.length, 10);
const unit = compile('lib', 'bridge_platform_windows', 'crates/bridge-platform-windows/src/lib.rs', ['--lib']);
nativeTestInventory(run('list-unit', unit.executable, ['--list']), units);
nativeTestResult(run('execute-unit', unit.executable, ['--test-threads=1', ...skippedUnits.flatMap(name => ['--skip', name])]), 28, 10);
const cases = [
  'physical_identity_retains_hardlink_alias_and_replacement', 'capture_does_not_exclude_and_admission_blocks_write_delete',
  'bounded_hash_and_device_stream_inputs_are_refused', 'final_and_ancestor_junctions_are_refused_without_following_targets',
  'directory_guard_retains_identity_and_blocks_rename', 'replacement_flushes_retains_backup_and_refuses_existing_backup',
  'replacement_changed_destination_refuses_before_call', 'replacement_native_failure_remains_ambiguous',
  'dpapi_current_user_roundtrip_rejects_purpose_version_and_tamper', 'signature_domains_remain_separate_and_unsigned_is_refused',
  'primary_signature_certificate_is_observed_and_unsupported_secondary_refused', 'exact_process_rejects_generation_change_and_observes_exit',
  'focus_targets_only_exact_owned_session_and_observes_policy', 'shortcut_private_publish_preserves_literal_arguments_and_refuses_overwrite'
];
const native = compile('test', 'windows_native', 'crates/bridge-platform-windows/tests/windows_native.rs', ['--test', 'windows_native']);
nativeTestInventory(run('list-native', native.executable, ['--list']), [...cases, 'fixture_owned_child']);
for (const name of cases) {
  const output = run(`execute-${name}`, native.executable, ['--ignored', '--exact', name, '--test-threads=1', '--nocapture']);
  nativeTestResult(output, 1, cases.length);
  if (['primary_signature_certificate_is_observed_and_unsupported_secondary_refused', 'exact_process_rejects_generation_change_and_observes_exit', 'focus_targets_only_exact_owned_session_and_observes_policy'].includes(name)) {
    const prefix = `test ${name} ... `, marker = 'BRIDGE_WINDOWS_OBSERVATION ';
    const lines = output.split(/\r?\n/).map(line => line.startsWith(prefix) ? line.slice(prefix.length) : line).filter(line => line.startsWith(marker));
    const expectedCount = name.startsWith('primary_signature') ? 6 : name.startsWith('exact_process') ? 2 : 3;
    assert.equal(lines.length, expectedCount, 'Every actual platform phase must emit one bounded observation');
    const values = lines.map(line => { assert.ok(Buffer.byteLength(line) <= 16 * 1024); return JSON.parse(line.slice(marker.length)); });
    for (const value of values) {
      assert.equal(value.schemaVersion, 'bridge-windows-native-observation/v1'); assert.equal(value.testName, name);
      assert.equal(value.grantsAuthority, false); assert.equal(value.mappedImageAttested, false);
    }
    if (name.startsWith('primary_signature')) {
      assert.deepEqual(values.map(value => `${value.observation.domain}:${value.observation.signatureIndex}`).sort(), ['mod:0', 'mod:1', 'official_game:0', 'official_game:1', 'bridge:0', 'bridge:1'].sort());
      for (const { observation } of values) {
        assert.equal(observation.revocationPolicy, 'cache_only'); assert.equal(observation.publisherApproval, false);
        assert.equal(observation.subject.diskSha256, pin.sha256);
        assert.equal(observation.trust.kind, observation.signatureIndex === 0 ? 'os_trusted' : 'os_refused');
        assert.equal(observation.signerCertificateSha256, observation.signatureIndex === 0 ? pin.primarySignerCertificateSha256 : null);
      }
    } else {
      assert.deepEqual(values.map(value => value.observation.phase), name.startsWith('exact_process') ? ['captured', 'released'] : ['headless', 'visible', 'released']);
      for (const { observation } of values) {
        assert.ok(Number.isInteger(observation.process.pid) && observation.process.pid > 0);
        assert.equal(observation.process.startStamp.kind, 'windows');
        assert.match(observation.process.startStamp.creationFiletime, /^[1-9][0-9]*$/);
        assert.equal(observation.process.architecture, 'x86_64');
        assert.equal(nativePhysicalPath(observation.process.executable.physicalPath), nativePhysicalPath(native.executable));
      }
      if (name.startsWith('exact_process')) {
        assert.deepEqual(values.map(value => value.observation.liveness), ['running', 'exited']);
        assert.deepEqual(values[0].observation.process, values[1].observation.process);
      } else {
        assert.equal(values[0].observation.focusOutcome, 'no_window');
        assert.ok(['foreground_observed', 'denied'].includes(values[1].observation.focusOutcome));
        assert.equal(values[2].observation.focusOutcome, 'process_exited');
        assert.deepEqual(values[1].observation.process, values[2].observation.process);
      }
    }
    observations.push(...values);
  }
}
assert.equal(sha(readFileSync(signedSubject)), pin.sha256); assert.equal(sha(readFileSync(process.execPath)), pin.sha256);
assert.deepEqual(fingerprintInputRecords(root, inputs), before, 'Source drift invalidates this private native proof');
for (const binary of binaries) assert.equal(sha(readFileSync(binary.executable)), binary.sha256);
const receipt = path.join(directory, 'windows-platform.json');
writeFileSync(receipt, JSON.stringify({ schemaVersion: 'bridge-windows-platform-observation/v1', result: 'passed', completedAt: new Date().toISOString(),
  host: 'windows-x64', toolchain: { rust: rust.pin, nativeTarget: rust.hostTarget, node: process.version }, sources: before, binaries, checks, observations,
  unitTests: units, executedDefaultUnitTests: 28, explicitSkippedUnitTests: skippedUnits, nativeCases: cases, compileFailOwnershipDocs: 5, fixtureRoot: fixture,
  signatureSubject: { source: process.execPath, privateCopy: signedSubject, sha256: pin.sha256, certificateSha256: pin.primarySignerCertificateSha256, pinManifestSha256: sha(pinBytes), executed: false, publisherApproved: false },
  boundary: 'Actual Windows services in fresh private owner-scoped fixtures, including retained physical/process identity, native reparse refusals, DPAPI synthetic bytes, cache-only signature observations and exact owned-window focus policy. This suite does not directly observe token elevation/integrity. No game, catalog, stored account/configuration or installed Bridge update touched. Namespace exclusion, journal recovery and installed-runtime qualification remain application-service responsibilities.',
  privateJournalOwnerQualified: false, installedGameQualified: false, nativeRuntimeQualified: false, releaseQualified: false }, null, 2) + '\n', { flag: 'wx' });
console.log(JSON.stringify({ result: 'passed', nativeTests: cases.length, unitTests: 28, listedUnitTests: units.length, explicitSkippedUnitTests: 10, receipt, nativeRuntimeQualified: false, releaseQualified: false }));
