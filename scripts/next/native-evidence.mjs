import assert from 'node:assert/strict';
import { realpathSync } from 'node:fs';
import path from 'node:path';

// Native observations may use Windows' extended path namespace. Preserve the
// physical comparison; the JS realpath walker misparses that namespace in the
// pinned Node runtime. This helper grants no owner, hash or admission authority.
export function nativePhysicalPath(selected) {
  assert.ok(typeof selected === 'string' && selected.length > 0 && selected.length <= 32768 && !selected.includes('\0'));
  assert.ok(path.isAbsolute(selected));
  return realpathSync.native(selected);
}

export function nativeTestInventory(stdout, requiredNames) {
  const names = stdout.split(/\r?\n/).filter(line => line.endsWith(': test')).map(line => line.slice(0, -6));
  assert.ok(requiredNames.length > 0 && new Set(requiredNames).size === requiredNames.length);
  assert.equal(names.length, requiredNames.length, 'Native test inventory changed; review its criterion mapping');
  assert.equal(new Set(names).size, names.length);
  assert.deepEqual([...names].sort(), [...requiredNames].sort());
  return names;
}
export function nativeTestResult(stdout, expected, filtered = 0) {
  const summaries = [...stdout.matchAll(/^test result: ok\. (\d+) passed; (\d+) failed; (\d+) ignored; (\d+) measured; (\d+) filtered out;.*$/gm)];
  assert.equal(summaries.length, 1, 'Require one actual native test execution summary');
  assert.deepEqual(summaries[0].slice(1).map(Number), [expected, 0, 0, 0, filtered], 'Skipped, filtered or absent tests cannot establish native proof');
  assert.ok(expected > 0);
}
export function nativeMarker(stdout, marker, testName) {
  const lines = stdout.split(/\r?\n/).map(line => testName && line.startsWith(`test ${testName} ... `) ? line.slice(`test ${testName} ... `.length) : line).filter(line => line.startsWith(marker));
  assert.equal(lines.length, 1, 'Require one actual native observation marker');
  return JSON.parse(lines[0].slice(marker.length));
}

export const nativeCriteria = {
  identity: [
    'pin_refuses_relative_roots_traversal_empty_paths_and_unreviewed_digest_shapes',
    'pe_observation_refuses_wrong_machine_executable_pe32_and_truncated_headers',
    'macho_observation_refuses_intel_fat_non_dylib_and_malformed_command_tables',
    'wrong_host_is_refused_before_file_lookup_or_any_initializer',
    'changed_bytes_refuse_before_foreign_code_is_loaded',
    'lexical_root_and_relative_child_links_refuse_before_foreign_code_is_loaded'
  ],
  profiles: [
    'binding_handshake_observes_api2_without_catalog_or_lease_authority',
    'structured_refusal_is_distinct_from_allocation_abi_failures_and_redacts_diagnostics',
    'malformed_utf8_json_version_and_duplicate_members_are_refused_and_freed_once',
    'bounded_scanner_accepts_exact_cap_and_refuses_larger_owned_readable_allocation',
    'non_ascii_inputs_preserve_utf8_and_limits_refuse_before_native_invocation',
    'every_lease_and_native_buffer_keeps_its_originating_table_alive_after_client_drop',
    'contradictory_lease_outputs_release_both_allocations_with_matching_owners',
    'lease_error_diagnostics_are_bounded_suppressed_and_freed_for_both_abi_statuses',
    'response_shape_and_same_request_identity_are_checked_before_success',
    'all_24_producer_operation_requests_and_response_families_keep_native_api2_shape',
    'contradictory_profile_kind_state_and_ordinary_process_receipts_never_publish_success',
    'request_byte_boundary_and_explicit_preference_clear_preserve_producer_fields',
    'installation_status_refusal_codes_remain_typed_and_native_details_are_suppressed'
  ].map(name => `ffi::tests::${name}`),
  toml: [
    'version_is_checked_before_any_execute',
    'all_nine_operations_encode_exact_producer_shapes',
    'encoded_byte_limit_counts_escaping_and_prevents_native_call',
    'valid_allocation_freed_on_success_and_exact_request_reaches_abi',
    'snapshot_preserves_int64_datetime_and_literal_dot_paths_as_strings',
    'edited_normalized_decoded_and_parsed_reply_families_are_distinct',
    'all_native_refusal_codes_and_positive_line_are_typed_and_freed',
    'abi_failures_are_distinct_from_structured_refusals',
    'contradictory_outputs_release_valid_allocation_without_reading_length',
    'response_bounds_and_nulls_are_checked_before_slice_construction',
    'malformed_json_utf8_duplicates_versions_and_private_fields_are_refused',
    'malformed_snapshot_rows_and_changed_decoded_paths_are_refused',
    'allocation_guard_retains_origin_after_client_drop_and_frees_exactly_once'
  ].map(name => `tests::${name}`)
};
