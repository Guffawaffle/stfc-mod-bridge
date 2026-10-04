import assert from 'node:assert/strict';
import { spawnSync } from 'node:child_process';
import { createHash, randomUUID } from 'node:crypto';
import { mkdirSync, readFileSync, realpathSync, writeFileSync } from 'node:fs';
import path from 'node:path';
import { rustContext } from './rust-context.mjs';
import { ownedArtifactPath } from './owned-artifact.mjs';

const root = path.resolve(import.meta.dirname, '../..');
assert.equal(process.argv.length, 2);
const rust = rustContext({ root });
const directory = ownedArtifactPath(root, `artifacts/next/projections/${randomUUID()}`, 'directory', { allowMissing: true });
mkdirSync(directory, { recursive: true });
const hash = bytes => createHash('sha256').update(bytes).digest('hex');
const checks = [];
function run(id, executable, argv) {
  const startedAt = new Date().toISOString();
  const result = spawnSync(executable, argv, { cwd: root, env: process.env, windowsHide: true,
    encoding: 'utf8', timeout: 180000, maxBuffer: 8 * 1024 * 1024 });
  const observation = { id, executable, argv, cwd: root, startedAt, completedAt: new Date().toISOString(),
    exitCode: result.status, error: result.error?.code ?? null, stdout: result.stdout ?? '', stderr: result.stderr ?? '' };
  checks.push(observation); writeFileSync(path.join(directory, `${id}.json`), JSON.stringify(observation, null, 2) + '\n');
  assert.equal(result.status, 0, `Projection ${id} failed; retained observation`); assert.ok(!result.error);
  return observation.stdout;
}
const cargo = (id, argv) => run(id, process.execPath, ['scripts/next/cargo.mjs', ...argv]);
cargo('format', ['fmt', '--package', 'bridge-domain', '--', '--check']);
cargo('clippy', ['clippy', '--locked', '--package', 'bridge-domain', '--all-targets', '--', '-D', 'warnings']);
const tree = cargo('dependency-tree', ['tree', '--locked', '--package', 'bridge-domain', '--edges', 'normal']);
assert.ok(!/\b(?:tauri|wry|webkit|webview|bridge-platform-|bridge-profiles|bridge-toml|bridge-engine)\b/i.test(tree), 'Pure domain cannot consume native, engine or UI services');
const output = cargo('compile-current-tests', ['test', '--locked', '--package', 'bridge-domain', '--lib', '--no-run', '--message-format', 'json']);
const artifacts = output.split(/\r?\n/).filter(Boolean).map(line => JSON.parse(line)).filter(value =>
  value.reason === 'compiler-artifact' && value.target.name === 'bridge_domain' && value.target.kind.includes('lib') && value.profile.test && value.executable);
assert.equal(artifacts.length, 1);
assert.equal(realpathSync(artifacts[0].target.src_path), realpathSync(path.join(root, 'crates/bridge-domain/src/lib.rs')));
const relative = path.relative(root, artifacts[0].executable).replaceAll('\\', '/');
assert.ok(relative.startsWith(`target/${rust.hostTarget}/`));
const executable = ownedArtifactPath(root, relative), bytes = readFileSync(executable);
if (process.platform === 'win32') {
  assert.equal(bytes.toString('ascii', 0, 2), 'MZ'); assert.equal(bytes.readUInt16LE(bytes.readUInt32LE(0x3c) + 4), 0x8664);
} else {
  assert.equal(bytes.readUInt32LE(0), 0xfeedfacf); assert.equal(bytes.readUInt32LE(4), 0x0100000c);
}
const binary = { path: executable, bytes: bytes.length, sha256: hash(bytes), compilerArtifact: artifacts[0] };
const discovered = run('list-tests', executable, ['--list']).split(/\r?\n/).filter(line => line.endsWith(': test')).map(line => line.slice(0, -6));
assert.equal(new Set(discovered).size, discovered.length);
const required = {
  actions: [
    'a_changed_selection_does_not_retarget_observed_scope_or_route_support',
    'active_session_blocks_its_physical_installation_even_when_profile_preferences_change',
    'busy_recovery_and_historical_false_flags_never_become_ready',
    'conflicting_native_route_observations_are_not_resolved_by_enumeration_order',
    'conflicting_session_installation_identity_cannot_be_filtered_into_launch_availability',
    'conflicting_session_readiness_cannot_mean_an_unrelated_installation_is_safe',
    'declared_runtime_support_cannot_supply_a_missing_or_self_described_native_route',
    'duplicate_requested_actions_do_not_emit_ambiguous_action_inventory',
    'focus_requires_live_identity_and_cannot_use_a_historical_ready_receipt',
    'focus_selects_exact_session_instead_of_first_process_or_profile_preference',
    'historical_scope_and_runtime_receipts_cannot_enable_current_launch',
    'historical_new_isolated_store_state_cannot_enable_current_launch',
    'historical_established_isolated_store_state_cannot_enable_current_launch',
    'incomplete_unknown_or_historical_session_inventory_cannot_mean_stopped',
    'isolated_launch_requires_active_matching_profile_and_known_established_store_state',
    'known_absent_runtime_can_allow_unmodded_ordinary_without_granting_features',
    'live_observed_exit_and_unrelated_installation_are_distinct_from_unknown_identity',
    'managed_features_for_another_runtime_or_target_cannot_enable_launch',
    'partial_or_foreign_capability_evidence_never_enables_managed_runtime_launch',
    'renamed_profile_and_next_launch_preference_do_not_rewrite_current_target',
    'unimplemented_action_families_stay_unavailable_despite_claimed_native_routes',
    'unknown_runtime_is_not_absence_even_with_unrecognized_per_attempt_consent',
    'unmanaged_ordinary_launch_requires_both_policy_and_exact_per_attempt_choice',
    'wrong_profile_kind_and_absent_runtime_do_not_grant_isolation',
  ],
  capabilities: [
    'a_recognized_parser_result_with_missing_runtime_manifest_is_not_support',
    'conflicting_policy_entries_and_duplicate_manifest_features_fail_closed',
    'copied_provider_name_cannot_authorize_an_unknown_provider_or_distribution',
    'declared_apple_silicon_support_is_distinct_from_excluded_intel_target',
    'duplicate_requested_capability_ids_cannot_emit_an_invalid_inventory',
    'every_runtime_binding_identity_part_is_bound_to_the_manifest',
    'file_hash_and_historical_receipt_do_not_prove_parsed_manifest_features',
    'manifest_and_policy_feature_intersection_never_grants_an_omitted_feature',
    'missing_invalid_and_unrecognized_manifests_do_not_grant_support',
    'provider_display_names_never_select_or_change_feature_policy',
    'recognized_features_require_manifest_policy_and_preserve_declaration_provenance',
    'unknown_runtime_and_manifest_observations_remain_unknown',
    'unsupported_manifest_version_and_malformed_policy_cannot_expand_support',
  ],
  observations: [
    'conflicting_physical_target_becomes_unknown_without_relabeling_the_session',
    'current_session_projection_preserves_exact_capture_and_evidence_without_preference_input',
    'diagnostic_fact_bound_refuses_overflow_instead_of_dropping_inconvenient_facts',
    'duplicate_sessions_and_partial_inventory_without_reason_are_refused',
    'historical_live_or_exited_receipt_remains_unknown_to_current_process_projection',
    'isolation_readiness_cannot_change_an_ordinary_captured_profile',
    'redacted_diagnostics_preserve_conflicting_provenance_without_claiming_requested_identity',
    'unknown_session_facts_and_unavailable_inventory_are_never_successful_empty_defaults',
  ],
};
const expected = Object.entries(required).flatMap(([group, names]) => names.map(name => `projections::tests::${group}::${name}`));
assert.equal(expected.length, 45);
assert.deepEqual(discovered.filter(name => name.startsWith('projections::tests::')).sort(), [...expected].sort());
const executed = run('projection-tests', executable, ['projections::tests::', '--test-threads=1']);
const result = executed.match(/test result: ok\. (\d+) passed; 0 failed; 0 ignored; 0 measured; (\d+) filtered out;/);
assert.ok(result); assert.equal(Number(result[1]), expected.length); assert.equal(Number(result[2]), discovered.length - expected.length);
assert.equal(hash(readFileSync(executable)), binary.sha256);
const receipt = path.join(directory, 'projections.json');
writeFileSync(receipt, JSON.stringify({ schemaVersion: 'bridge-domain-projections-observation/v1',
  completedAt: new Date().toISOString(), host: { platform: process.platform, architecture: process.arch, node: process.version },
  toolchain: { pin: rust.pin, nativeTarget: rust.hostTarget }, binary, discoveredTests: discovered, executedTests: expected, checks,
  boundary: 'Pure immutable capability/action/session/diagnostic projections and dependency boundary over synthetic typed facts; no native service, catalog or engine execution',
  nativeRuntimeQualified: false, releaseQualified: false }, null, 2) + '\n');
console.log(JSON.stringify({ result: 'passed', tests: expected.length, receipt, nativeRuntimeQualified: false, releaseQualified: false }));
