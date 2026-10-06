import assert from 'node:assert/strict';
import { spawnSync } from 'node:child_process';
import { createHash, randomUUID } from 'node:crypto';
import { existsSync, lstatSync, mkdirSync, readFileSync, realpathSync, writeFileSync } from 'node:fs';
import path from 'node:path';
import { fingerprintInputRecords } from './input-tree.mjs';
import { ownedArtifactPath } from './owned-artifact.mjs';
import { rustContext } from './rust-context.mjs';

const root = path.resolve(import.meta.dirname, '../..');
assert.equal(process.argv.length, 2, 'Configuration workspace suite accepts no caller overrides');
assert.equal(realpathSync.native(process.cwd()), realpathSync.native(root), 'Run from the canonical owning checkout');
const rust = rustContext({ root });
for (const [name, value] of Object.entries(process.env)) {
  const key = name.toUpperCase();
  const reserved = /^(?:BRIDGE_(?:CONFIGURATION_|FIXTURE_)|RUST_TEST_|LIBTEST_)/.test(key)
    || /^(?:RUSTFLAGS|CARGO_ENCODED_RUSTFLAGS|CARGO_TARGET_DIR|CARGO_BUILD_TARGET|RUSTUP_TOOLCHAIN)$/.test(key)
    || /^CARGO_(?:TARGET_|PROFILE_|BUILD_(?:RUSTFLAGS|RUSTDOCFLAGS))/.test(key);
  assert.ok(!reserved || !value, `Caller override ${key} is unsupported`);
}
const metadata = JSON.parse(readFileSync(path.join(root, 'dependencies/next-toolchain.json'), 'utf8'));
assert.equal(process.version, `v${metadata.node}`, 'Use the tracked Node runtime');
const sha = bytes => createHash('sha256').update(bytes).digest('hex');
const relative = selected => path.relative(root, selected).replaceAll('\\', '/');
const inputs = [
  'Cargo.toml', 'Cargo.lock', 'rust-toolchain.toml', '.cargo/config.toml', 'dependencies/next-toolchain.json',
  'docs/next/CONFIGURATION_WORKSPACE.md', 'scripts/next/configuration-workspace.mjs', 'scripts/next/input-tree.mjs',
  'scripts/next/owned-artifact.mjs', 'scripts/next/rust-context.mjs',
  'crates/bridge-engine', 'crates/bridge-app', 'crates/bridge-contracts', 'crates/bridge-domain',
  'crates/bridge-native', 'crates/bridge-toml', 'crates/bridge-platform-windows', 'crates/bridge-platform-macos',
  'crates/bridge-journal-io',
  'contracts'
];
const sourcesBefore = fingerprintInputRecords(root, inputs);
const required = {
  configuration_drafts: [
    'missing_open_stage_discard_do_not_mutate_document',
    'exact_lost_ack_retry_returns_one_successor_changed_retry_refuses',
    'protected_capture_stage_transfer_prepare_continuity_and_forgery_refusal',
    'acknowledgment_overflow_or_adapter_failure_preserves_draft_and_capture',
    'invalid_public_edits_remain_and_sensitive_plaintext_is_hard_refusal',
    'cancelled_or_unavailable_entry_creates_no_reference_and_wrong_host_refuses',
    'duplicate_edit_targets_and_foreign_draft_capture_refuse_atomically',
    'repeated_protected_use_gets_one_closed_transfer_for_all_occurrences',
    'read_cannot_substitute_ordinary_target_for_requested_isolated_profile',
    'repeated_sync_observation_preserves_distinct_saved_subjects_and_payloads',
    'draft_identity_allocation_failure_publishes_no_draft_and_does_not_retry',
    'saved_private_allocation_failure_publishes_no_partial_vault_and_burns_issued_ids',
    'captured_private_identity_failure_consumes_capture_without_publishing_reference',
    'captured_secret_identity_failure_preserves_closed_error_and_publishes_no_reference',
    'private_transfer_identity_failure_preserves_capture_draft_and_previous_acknowledgment',
    'secret_transfer_identity_failure_preserves_capture_draft_and_previous_acknowledgment',
    'partial_transfer_allocation_failure_keeps_payloads_and_burns_unpublished_successor_id',
    'discarded_draft_identity_remains_burned_across_draft_and_protected_id_kinds',
    'current_generation_lookup_is_immutable_and_preflight_refusals_keep_local_custody'
  ],
  configuration_semantics: [
    'semantic_equality_keeps_source_spelling_comments_and_unknown_keys',
    'sparse_missing_save_does_not_materialize_unselected_defaults',
    'missing_remove_override_and_no_change_create_no_empty_document',
    'native_codec_semantic_proof_rejects_unowned_mutation',
    'complete_candidate_validation_refuses_before_persistence',
    'complete_edit_set_projects_mixed_apply_timing_and_exact_int64',
    'unknown_empty_descendant_refuses_owned_parent_removal_and_rename',
    'fully_owned_table_moves_preserve_unrelated_empty_tables_and_require_new_parents',
    'scalar_set_proof_requires_table_parents_and_preserves_unowned_empty_tables',
    'scalar_remove_prunes_only_explicitly_owned_newly_empty_ancestors',
    'table_rename_refuses_unowned_relocated_values_and_existing_destinations'
  ],
  configuration_transactions: [
    'missing_meaningful_save_commits_sparse_bytes_without_backup',
    'no_change_admission_creates_no_file_backup_or_stage',
    'appearing_file_and_same_byte_physical_replacement_refuse_before_stage',
    'losing_writer_has_no_native_effects',
    'backup_failure_preserves_prior_document_and_draft',
    'existing_write_retains_exact_prior_byte_backup_and_history',
    'cancellation_before_first_step_has_no_effect_and_late_cancel_is_committed',
    'semantically_empty_save_clears_exact_draft_once_and_keeps_missing_baseline',
    'older_commit_preserves_newer_local_edits_as_stale',
    'stages_between_prepare_and_admission_invalidate_exact_old_plan_without_effect',
    'stage_after_acquisition_before_durable_begin_still_refuses_old_plan',
    'explicit_restore_validates_backup_and_backs_up_current_document',
    'backup_tamper_between_restore_prepare_and_admission_refuses_before_stage',
    'repeated_held_lease_revalidation_never_reacquires_and_begin_moves_once',
    'held_lease_physical_and_schema_refusals_retain_candidate_before_begin',
    'held_restore_backup_refusal_preserves_exact_candidate_and_owner_lease',
    'foreign_recovery_binding_refuses_without_consuming_candidate_or_beginning',
    'native_begin_errors_keep_exact_candidate_for_recovery_without_retry',
    'completed_write_keeps_dirty_draft_until_durable_callback_and_publishes_once',
    'newer_edit_after_completion_refusal_is_preserved_as_stale_without_writer_replay',
    'owner_read_refusal_after_native_completion_keeps_intent_and_cached_native_outcome',
    'no_change_cleanup_waits_for_commit_and_does_not_create_file_backup_or_stage',
    'restore_completion_preserves_matching_local_edits_until_stale_publication',
    'completion_refusal_preserves_protected_payload_until_exact_clean_publication'
  ],
  configuration_recovery: [
    'ambiguous_replacement_stays_recovery_required_with_exact_custody',
    'restart_recovery_uses_captured_digest_without_protected_payload_recreation',
    'foreign_destination_or_recovery_target_is_not_overwritten',
    'unstarted_recovery_proves_no_effect_and_invalidates_previous_host_drafts'
  ],
  configuration_services: [
    'encoded_dispatch_read_history_stage_current_successor_missing_discard',
    'immutable_getter_returns_clean_dirty_invalid_stale_without_io_or_ids',
    'get_draft_foreign_host_refuses_before_lookup_and_same_host_absence_is_missing',
    'metadata_requires_injected_protected_entry_and_save_restore_remain_unavailable',
    'draft_and_operation_changes_share_one_consecutive_kernel_event_sequence',
    'protected_capture_and_transfer_never_escape_replies_events_or_journal',
    'failed_stage_reply_preflight_keeps_revision_and_original_protected_ref',
    'failed_open_event_reservation_publishes_neither_draft_nor_cursor',
    'read_projection_preflight_failure_preserves_all_existing_draft_states',
    'construction_rejects_foreign_workspace_and_services_cannot_advertise_kernel_controls',
    'closing_and_poisoned_engines_refuse_configuration_effects_but_getter_stays_immutable',
    'real_host_handle_reconciles_staged_draft_and_discard_on_original_owner_thread'
  ],
  bridge_app: [
    'providers::tests::clock_samples_elapsed_then_wall_once_and_accepts_zero_elapsed',
    'providers::tests::native_clock_failures_refuse_before_wall_sampling_with_closed_mapping',
    'providers::tests::wall_conversion_preserves_epoch_and_signed_fractional_nanoseconds',
    'providers::tests::wall_conversion_accepts_contract_year_boundaries_and_refuses_outside_them',
    'providers::tests::deadline_preserves_nanoseconds_through_second_day_leap_and_year_rollovers',
    'providers::tests::deadline_refuses_elapsed_and_utc_overflow_without_saturation',
    'providers::tests::deadline_uses_supplied_sample_without_resampling_after_wall_adjustment',
    'providers::tests::four_identity_kinds_draw_independently_once_without_a_uniqueness_policy',
    'providers::tests::uuid_encoding_masks_only_version_and_variant_for_zero_ff_and_mixed_bytes',
    'providers::tests::every_entropy_failure_refuses_each_kind_without_retry_or_validation',
    'providers::tests::encoded_identity_refusal_is_closed_and_does_not_retry',
    'providers::tests::configuration_identity_kinds_draw_once_per_request_without_a_uniqueness_policy',
    'providers::tests::every_configuration_entropy_failure_refuses_without_retry_or_validation',
    'providers::tests::configuration_identity_failure_does_not_retry_or_cache_between_requests',
    'providers::tests::configuration_identity_validation_refusal_is_not_entropy_unavailability',
    'providers::tests::supported_native_providers_return_contract_valid_values_without_entropy_logging'
  ]
};
const directory = ownedArtifactPath(root, `artifacts/next/configuration-workspace/${randomUUID()}`, 'directory', { allowMissing: true });
mkdirSync(directory, { recursive: true });
ownedArtifactPath(root, relative(directory), 'directory');
const checks = [], binaries = [], toolsBefore = [], toolsAfter = [];
let sourcesAfter = null, failure = null, passed = false;
const startedAt = new Date().toISOString();
function save(name, bytes) {
  const selected = ownedArtifactPath(root, `${relative(directory)}/${name}`, 'file', { allowMissing: true });
  writeFileSync(selected, bytes, { flag: 'wx' });
  return { path: relative(selected), bytes: Buffer.byteLength(bytes), sha256: sha(bytes) };
}
function run(id, executable, argv, timeout = 180000) {
  const start = new Date().toISOString();
  const result = spawnSync(executable, argv, { cwd: root, env: rust.env, windowsHide: true, timeout, maxBuffer: 8 * 1024 * 1024 });
  const stdout = result.stdout ?? Buffer.alloc(0), stderr = result.stderr ?? Buffer.alloc(0);
  const observation = { id, executable, argv, cwd: root, startedAt: start, completedAt: new Date().toISOString(),
    exitCode: result.status, signal: result.signal ?? null, error: result.error?.code ?? null,
    stdout: save(`${id}.stdout.log`, stdout), stderr: save(`${id}.stderr.log`, stderr) };
  checks.push({ ...observation, observation: save(`${id}.json`, JSON.stringify(observation, null, 2) + '\n') });
  console.log(JSON.stringify({ check: id, exitCode: result.status, error: observation.error }));
  assert.equal(result.status, 0, `Configuration ${id} failed; inspect retained command logs`);
  assert.ok(!result.error && !result.signal, `Configuration ${id} did not complete normally`);
  const text = stdout.toString('utf8');
  assert.ok(Buffer.from(text, 'utf8').equals(stdout), 'Command stdout must be valid UTF-8 before interpretation');
  return text;
}
function resolveTool(selected) {
  if (path.isAbsolute(selected)) return selected;
  const entries = Object.entries(rust.env).filter(([key]) => process.platform === 'win32' ? key.toUpperCase() === 'PATH' : key === 'PATH');
  assert.equal(entries.length, 1, 'Tool resolution requires one child PATH');
  for (const entry of entries[0][1].split(path.delimiter)) {
    if (!entry || !path.isAbsolute(entry)) continue;
    const candidate = path.join(entry, selected);
    if (existsSync(candidate) && lstatSync(candidate).isFile()) return candidate;
  }
  assert.fail(`Pinned tool ${selected} cannot be resolved`);
}
function observeTool(role, route) {
  const physical = realpathSync.native(route), stat = lstatSync(physical);
  assert.ok(stat.isFile(), 'Tool payload must be a regular file');
  const bytes = readFileSync(physical);
  assert.equal(bytes.length, stat.size);
  return { role, route, physical, bytes: bytes.length, sha256: sha(bytes) };
}
function observeBinary(binary) {
  const selected = ownedArtifactPath(root, relative(binary.executable));
  assert.equal(selected, binary.executable);
  const bytes = readFileSync(selected);
  assert.equal(bytes.length, binary.bytes, 'Test executable size changed');
  assert.equal(sha(bytes), binary.sha256, 'Test executable bytes changed');
}
function nativeArchitecture(bytes) {
  if (process.platform === 'win32') {
    assert.ok(bytes.length >= 64, 'Truncated Windows executable');
    assert.equal(bytes.toString('ascii', 0, 2), 'MZ');
    const pe = bytes.readUInt32LE(0x3c);
    assert.ok(pe >= 64 && pe + 26 <= bytes.length, 'Invalid PE header extent');
    assert.equal(bytes.toString('ascii', pe, pe + 4), 'PE\0\0');
    assert.equal(bytes.readUInt16LE(pe + 4), 0x8664, 'Test executable must be native Windows x64');
    assert.equal(bytes.readUInt16LE(pe + 24), 0x20b, 'Test executable must be PE32+');
    return 'windows-x64-pe32+';
  }
  assert.ok(bytes.length >= 32, 'Truncated Apple Silicon executable');
  assert.equal(bytes.readUInt32LE(0), 0xfeedfacf, 'Test executable must be thin 64-bit Mach-O');
  assert.equal(bytes.readUInt32LE(4), 0x0100000c, 'Test executable must be native Apple Silicon');
  assert.equal(bytes.readUInt32LE(12), 2, 'Mach-O test subject must be an executable');
  return 'macos-arm64-mach-o';
}
try {
  const cargoPath = resolveTool(rust.cargo), rustcPath = resolveTool(rust.rustc);
  toolsBefore.push(observeTool('node', process.execPath), observeTool('cargo-route', cargoPath), observeTool('rustc-route', rustcPath));
  const version = run('rustc-version', rustcPath, [`+${rust.pin}`, '-vV']);
  assert.ok(version.startsWith(`rustc ${rust.pin} `), 'Actual compiler must match the tracked Rust release');
  assert.ok(version.split(/\r?\n/).includes(`host: ${rust.hostTarget}`), 'Actual compiler host must match the native target');
  const sysroot = run('rustc-sysroot', rustcPath, [`+${rust.pin}`, '--print', 'sysroot']).trim();
  assert.ok(path.isAbsolute(sysroot) && !sysroot.includes('\n') && !sysroot.includes('\r'), 'Compiler must identify one absolute sysroot');
  const extension = process.platform === 'win32' ? '.exe' : '';
  for (const tool of ['cargo', 'rustc', 'rustdoc', 'rustfmt', 'cargo-clippy', 'clippy-driver'])
    toolsBefore.push(observeTool(`toolchain-${tool}`, path.join(sysroot, 'bin', tool + extension)));
  const cargoVersion = run('cargo-version', cargoPath, [`+${rust.pin}`, '--version']);
  assert.ok(cargoVersion.startsWith(`cargo ${rust.pin} `), 'Actual Cargo must match the tracked Rust release');
  ownedArtifactPath(root, 'target', 'directory', { allowMissing: true });
  ownedArtifactPath(root, `target/${rust.hostTarget}`, 'directory', { allowMissing: true });
  const cargo = (id, argv) => run(id, cargoPath, [`+${rust.pin}`, ...argv]);
  cargo('format', ['fmt', '-p', 'bridge-engine', '-p', 'bridge-app', '--', '--check']);
  cargo('clippy', ['clippy', '--locked', '--offline', '-p', 'bridge-engine', '-p', 'bridge-app', '--all-targets', '--target', rust.hostTarget, '--', '-D', 'warnings']);
  const names = Object.keys(required);
  const engineNames = names.filter(name => name !== 'bridge_app' && name !== 'configuration_services');
  const output = cargo('compile-current-configuration-tests', ['test', '--locked', '--offline', '-p', 'bridge-engine', '--target', rust.hostTarget,
    ...engineNames.flatMap(name => ['--test', name]), '--no-run', '--message-format', 'json']);
  const providerOutput = cargo('compile-current-configuration-providers', ['test', '--locked', '--offline', '-p', 'bridge-app',
    '--target', rust.hostTarget, '--lib', '--no-run', '--message-format', 'json']);
  const serviceOutput = cargo('compile-current-configuration-services', ['test', '--locked', '--offline', '-p', 'bridge-app',
    '--target', rust.hostTarget, '--test', 'configuration_services', '--no-run', '--message-format', 'json']);
  const messages = [output, providerOutput, serviceOutput].flatMap(text => {
    const current = text.split(/\r?\n/).filter(Boolean).map(line => JSON.parse(line));
    assert.equal(current.filter(message => message.reason === 'build-finished' && message.success === true).length, 1,
      'Require each current Cargo invocation to finish successfully');
    return current;
  });
  const artifacts = messages.filter(message => message.reason === 'compiler-artifact' && message.executable && message.profile.test
    && ((message.target.kind.includes('test') && engineNames.includes(message.target.name))
      || (message.target.name === 'bridge_app' && message.target.kind.includes('lib'))
      || (message.target.name === 'configuration_services' && message.target.kind.includes('test'))));
  assert.equal(artifacts.length, names.length, 'Current Cargo invocations must identify all four configuration, one service and one provider test executable exactly once');
  for (const target of names) {
    const selected = artifacts.filter(artifact => artifact.target.name === target);
    assert.equal(selected.length, 1, `Require one current ${target} compiler artifact`);
    const artifact = selected[0];
    const provider = target === 'bridge_app';
    const application = provider || target === 'configuration_services';
    assert.equal(realpathSync.native(artifact.manifest_path), realpathSync.native(path.join(root, `crates/${application ? 'bridge-app' : 'bridge-engine'}/Cargo.toml`)));
    assert.equal(realpathSync.native(artifact.target.src_path), realpathSync.native(path.join(root,
      provider ? 'crates/bridge-app/src/lib.rs' : `crates/${application ? 'bridge-app' : 'bridge-engine'}/tests/${target}.rs`)));
    const relation = relative(artifact.executable);
    assert.ok(relation.startsWith(`target/${rust.hostTarget}/`), 'Test artifact must remain inside the owning native target directory');
    const executable = ownedArtifactPath(root, relation), bytes = readFileSync(executable);
    binaries.push({ target, executable, bytes: bytes.length, sha256: sha(bytes), architecture: nativeArchitecture(bytes),
      compilerArtifact: artifact, discoveredTests: [], executedTests: [] });
  }
  assert.equal(new Set(binaries.map(binary => binary.executable)).size, names.length, 'Test targets must have distinct executables');
  for (const binary of binaries) {
    observeBinary(binary);
    const inventory = run(`list-${binary.target}`, binary.executable, ['--list', '--color', 'never']);
    const discovered = inventory.split(/\r?\n/).filter(line => line.endsWith(': test')).map(line => line.slice(0, -6));
    assert.equal(new Set(discovered).size, discovered.length, 'Test inventory must be unique');
    assert.deepEqual([...discovered].sort(), [...required[binary.target]].sort(), 'Configuration test inventory changed; review its required behavior mapping');
    binary.discoveredTests = discovered;
    observeBinary(binary);
    const result = run(`execute-${binary.target}`, binary.executable, ['--test-threads=1', '--color', 'never']);
    const summaries = [...result.matchAll(/^test result: ok\. (\d+) passed; (\d+) failed; (\d+) ignored; (\d+) measured; (\d+) filtered out;.*$/gm)];
    assert.equal(summaries.length, 1, 'Require one complete execution summary');
    assert.deepEqual(summaries[0].slice(1).map(Number), [discovered.length, 0, 0, 0, 0], 'Every discovered test must run; failures, ignores and filtering block this check');
    const completed = result.split(/\r?\n/).filter(line => /^test \S+ \.\.\. ok$/.test(line)).map(line => line.slice(5, -7));
    assert.deepEqual([...completed].sort(), [...discovered].sort(), 'Every discovered test must have an actual successful completion');
    binary.executedTests = completed;
    observeBinary(binary);
  }
  sourcesAfter = fingerprintInputRecords(root, inputs);
  assert.deepEqual(sourcesAfter, sourcesBefore, 'Source drift invalidates the configuration observation');
  for (const tool of toolsBefore) toolsAfter.push(observeTool(tool.role, tool.route));
  assert.deepEqual(toolsAfter, toolsBefore, 'Tool routing or bytes changed during the configuration observation');
  for (const binary of binaries) observeBinary(binary);
  passed = true;
} catch (error) {
  failure = { name: error?.name ?? 'Error', code: error?.code ?? null,
    message: String(error?.message ?? 'Configuration observation failed').slice(0, 4096) };
} finally {
  if (sourcesAfter === null) {
    try { sourcesAfter = fingerprintInputRecords(root, inputs); }
    catch (error) { sourcesAfter = { unavailable: error?.code ?? error?.name ?? 'Error' }; }
  }
  const receipt = save('configuration-workspace.json', JSON.stringify({ schemaVersion: 'bridge-configuration-workspace-observation/v1',
    result: passed ? 'passed' : 'failed', startedAt, completedAt: new Date().toISOString(),
    host: { platform: process.platform, architecture: process.arch, node: process.version },
    toolchain: { pin: rust.pin, nativeTarget: rust.hostTarget }, inputs, sourcesBefore, sourcesAfter, toolsBefore, toolsAfter,
    requiredTests: required, binaries, checks, failure,
    boundary: 'Actual native-architecture engine, configuration-service and application-provider test executables: portable workspace, encoded dispatcher read/stage/current-draft reconciliation, shared kernel events, preparation and orchestration with synthetic DocumentOwner, SchemaSource, SensitiveEntry and TomlPreparation ports, controlled entropy refusal and current-host native clock/entropy calls; no native configuration ownership or runtime qualification',
    portableModelObserved: passed, configurationDispatcherObserved: passed, nativeTomlInvocationQualified: false, producerPolicyQualified: false,
    physicalOwnerQualified: false, operationPortsAdopted: false, br14Accepted: false, nativeRuntimeQualified: false, releaseQualified: false
  }, null, 2) + '\n');
  console.log(JSON.stringify({ result: passed ? 'passed' : 'failed', tests: binaries.reduce((count, binary) => count + binary.executedTests.length, 0),
    receipt, br14Accepted: false, nativeRuntimeQualified: false, releaseQualified: false }));
  process.exitCode = passed ? 0 : 1;
}
