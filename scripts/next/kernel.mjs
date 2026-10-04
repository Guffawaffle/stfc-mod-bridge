import assert from 'node:assert/strict';
import { spawnSync } from 'node:child_process';
import { mkdirSync, readFileSync, realpathSync, writeFileSync } from 'node:fs';
import { createHash, randomUUID } from 'node:crypto';
import path from 'node:path';
import { rustContext } from './rust-context.mjs';
import { ownedArtifactPath } from './owned-artifact.mjs';

const root = path.resolve(import.meta.dirname, '../..');
const args = process.argv.slice(2);
assert.ok(args.length === 1 && ['operation', 'recovery'].includes(args[0]), 'Use kernel.mjs operation|recovery');
const suite = args[0], rust = rustContext({ root });
for (const [name, value] of Object.entries(process.env)) {
  assert.ok(!['BRIDGE_FIXTURE_CHILD_ROOT', 'BRIDGE_FIXTURE_CHILD_MODE'].includes(name.toUpperCase()) || !value,
    'Fixture child routing is reserved for the owned forced-termination test');
}
const directory = ownedArtifactPath(root, `artifacts/next/kernel/${randomUUID()}`, 'directory', { allowMissing: true });
mkdirSync(directory, { recursive: true });
const checks = [];
function run(id, executable, argv, timeout = 180000) {
  const result = spawnSync(executable, argv, { cwd: root, windowsHide: true,
    env: process.env, encoding: 'utf8', timeout, maxBuffer: 8 * 1024 * 1024 });
  const observation = { id, executable, argv, cwd: root, exitCode: result.status,
    error: result.error?.code ?? null, stdout: result.stdout ?? '', stderr: result.stderr ?? '' };
  checks.push(observation);
  writeFileSync(path.join(directory, `${id}.json`), JSON.stringify(observation, null, 2) + '\n');
  assert.equal(result.status, 0, `Kernel ${id} failed; inspect its retained observation`);
  assert.ok(!result.error);
  return observation.stdout;
}
const cargo = (id, argv) => run(id, process.execPath, ['scripts/next/cargo.mjs', ...argv]);
if (suite === 'operation') {
  cargo('format', ['fmt', '-p', 'bridge-engine', '--', '--check']);
  cargo('clippy', ['clippy', '--locked', '-p', 'bridge-engine', '--all-targets', '--', '-D', 'warnings']);
}
const digest = buffer => createHash('sha256').update(buffer).digest('hex');
function compileTest(id, selection, name, kind, source) {
  const build = cargo(id, ['test', '--locked', '-p', 'bridge-engine', ...selection, '--no-run', '--message-format', 'json']);
  const artifacts = build.split(/\r?\n/).filter(Boolean).map(line => JSON.parse(line)).filter(message =>
    message.reason === 'compiler-artifact' && message.target.name === name
    && message.target.kind.includes(kind) && message.profile.test === true && message.executable);
  assert.equal(artifacts.length, 1, 'This Cargo invocation must identify one kernel test executable');
  assert.equal(realpathSync(artifacts[0].target.src_path), realpathSync(path.join(root, source)),
    'Kernel test target must originate in the owning checkout');
  ownedArtifactPath(root, `target/${rust.hostTarget}`, 'directory');
  const requestedRelation = path.relative(root, artifacts[0].executable).replaceAll('\\', '/');
  const executable = ownedArtifactPath(root, requestedRelation);
  const relation = path.relative(path.join(root, 'target', rust.hostTarget), executable);
  assert.ok(relation && relation !== '..' && !relation.startsWith(`..${path.sep}`) && !path.isAbsolute(relation),
    'Kernel test artifact must remain in the owning native target directory');
  const bytes = readFileSync(executable);
  if (process.platform === 'win32') {
    assert.equal(bytes.toString('ascii', 0, 2), 'MZ');
    assert.equal(bytes.readUInt16LE(bytes.readUInt32LE(0x3c) + 4), 0x8664);
  } else {
    assert.equal(bytes.readUInt32LE(0), 0xfeedfacf);
    assert.equal(bytes.readUInt32LE(4), 0x0100000c);
  }
  return { executable, sha256: digest(bytes), bytes: bytes.length, compilerArtifact: artifacts[0] };
}
const binary = compileTest('compile-current-test', ['--test', 'operation_kernel'], 'operation_kernel', 'test',
  'crates/bridge-engine/tests/operation_kernel.rs');
const { executable } = binary;
const names = run('list-tests', executable, ['--list']).split(/\r?\n/)
  .filter(line => line.endsWith(': test')).map(line => line.slice(0, -6));
assert.ok(names.length >= 20 && new Set(names).size === names.length, 'Expected complete unique kernel test inventory');
const recovery = [
  'interrupted_work_blocks_conflicts_and_foreign_bytes_remain_unresolved',
  'interruption_at_staging_reconciles_to_durable_rollback',
  'disk_journal_is_exclusive_and_complete_corruption_never_becomes_success',
  'admission_persistence_failure_never_acknowledges_or_starts_effects_and_keeps_custody',
  'terminal_persistence_failure_keeps_lease_and_restart_observes_native_commit_once',
  'arbitrary_native_error_does_not_claim_rollback_or_release_lease',
  'host_epoch_and_stream_reuse_are_refused_after_restart',
  'complete_validly_framed_journal_with_conflicting_replay_binding_blocks_open',
  'forced_death_recovery_across_actual_process_boundaries',
  'native_terminal_observation_capacity_failure_persists_exact_recovery',
  'terminal_observation_recovery_preserves_exact_session_custody_until_handoff',
  'restart_session_handoff_keeps_unsafe_recovery_exclusion_until_owner_recovery',
  'sixty_four_independent_recoveries_keep_all_128_close_obligations_across_restart'
];
for (const name of recovery) assert.ok(names.includes(name), `Missing required recovery test ${name}`);
const requiredOperations = [
  'ordinary_directory_prepare_accepts_owner_resolved_registered_capture_without_effects',
  'isolated_directory_prepare_accepts_owner_resolved_registered_capture_without_effects',
  'ordinary_registered_prepare_refuses_changed_kind_id_and_revision_before_effects',
  'isolated_registered_prepare_refuses_changed_kind_id_and_revision_before_effects',
  'admission_is_persistent_before_reply_and_replay_precedes_old_host_lookup',
  'losing_writer_has_no_download_staging_backup_or_journal',
  'disconnect_observation_keeps_worker_lease_and_exact_replay_mutates_once',
  'revision_revalidation_occurs_under_exclusion_before_recovery_or_admission',
  'prepared_scope_cannot_retarget_or_be_committed_after_expiry',
  'foreign_native_recovery_binding_is_refused_without_a_journal',
  'cancellation_distinguishes_precommit_requested_too_late_and_terminal',
  'accepted_cancellation_remains_observable_after_native_cancellable_boundary',
  'native_receipt_and_session_custody_require_the_same_exact_process_binding',
  'normal_close_defers_until_worker_and_session_custody_reach_safe_owner_boundaries',
  'deferred_close_includes_safe_recovery_and_independent_running_operation',
  'snapshots_are_complete_and_events_are_scoped_consecutive_and_replay_free',
  'retention_gap_requires_resnapshot_and_current_cursor_never_reexecutes',
  'retained_history_capacity_refuses_before_another_exclusion_or_journal'
];
for (const name of requiredOperations) assert.ok(names.includes(name), `Missing required operation test ${name}`);
const selected = suite === 'recovery' ? recovery : names.filter(name => !recovery.includes(name));
assert.ok(selected.length > 0);
if (suite === 'recovery') {
  for (const name of selected) {
    const output = run(name, executable, ['--exact', name, '--test-threads=1', '--nocapture']);
    assert.match(output, /test result: ok\. 1 passed; 0 failed; 0 ignored; 0 measured;/);
  }
} else {
  const output = run('operation-tests', executable, ['--test-threads=1', ...recovery.flatMap(name => ['--skip', name])]);
  const result = output.match(/test result: ok\. (\d+) passed; 0 failed; 0 ignored; 0 measured; (\d+) filtered out;/);
  assert.ok(result, 'Operation tests must all execute, with zero failures/ignored tests');
  assert.equal(Number(result[1]), selected.length);
  assert.equal(Number(result[2]), recovery.length);
}
assert.equal(digest(readFileSync(executable)), binary.sha256, 'Executed kernel test bytes changed');
const additionalTests = [];
if (suite === 'operation') {
  const unit = compileTest('compile-current-bindings-unit', ['--lib'], 'bridge_engine', 'lib', 'crates/bridge-engine/src/lib.rs');
  const unitName = 'operations::bindings::tests::accepted_fixture_preparations_match_captures_with_valid_owner_resources';
  const discovered = run('list-bindings-unit-tests', unit.executable, ['--list']).split(/\r?\n/)
    .filter(line => line.endsWith(': test')).map(line => line.slice(0, -6));
  assert.ok(discovered.includes(unitName) && new Set(discovered).size === discovered.length,
    'The required accepted-corpus binding test must exist in the current unit artifact');
  const output = run('accepted-corpus-bindings-unit', unit.executable, ['--exact', unitName, '--test-threads=1']);
  assert.match(output, /test result: ok\. 1 passed; 0 failed; 0 ignored; 0 measured;/);
  assert.equal(digest(readFileSync(unit.executable)), unit.sha256, 'Executed kernel unit test bytes changed');
  additionalTests.push({ binary: unit, discoveredTests: discovered, executedTests: [unitName] });
}
const receipt = path.join(directory, 'kernel.json');
writeFileSync(receipt, JSON.stringify({ schemaVersion: 'bridge-kernel-observation/v1', suite,
  host: { platform: process.platform, architecture: process.arch, node: process.version },
  toolchain: { pin: rust.pin, nativeTarget: rust.hostTarget },
  binary, discoveredTests: names, executedTests: selected, additionalTests, checks,
  boundary: 'Actual engine test executable with synthetic canonical owner ports, private fixture filesystem and child kill/restart; platform owner custody and native domain services remain unqualified',
  nativeRuntimeQualified: false, releaseQualified: false
}, null, 2) + '\n');
console.log(JSON.stringify({ result: 'passed', suite, tests: selected.length + additionalTests.length, receipt,
  nativeRuntimeQualified: false, releaseQualified: false }));
