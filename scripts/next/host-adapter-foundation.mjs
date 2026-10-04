import assert from 'node:assert/strict';
import { spawnSync } from 'node:child_process';
import { createHash, randomUUID } from 'node:crypto';
import { existsSync, lstatSync, mkdirSync, readFileSync, realpathSync, writeFileSync } from 'node:fs';
import path from 'node:path';
import { fingerprintInputRecords } from './input-tree.mjs';
import { ownedArtifactPath } from './owned-artifact.mjs';
import { rustContext } from './rust-context.mjs';
import { vitestEvidence } from './test-evidence.mjs';

const root = path.resolve(import.meta.dirname, '../..');
assert.equal(process.argv.length, 2, 'Host adapter foundation suite accepts no caller overrides');
assert.deepEqual(process.execArgv, [], 'No caller Node loaders');
assert.equal(realpathSync.native(process.cwd()), realpathSync.native(root), 'Run from the canonical owning checkout');
const rust = rustContext({ root });
for (const [name, value] of Object.entries(process.env)) {
  const key = name.toUpperCase();
  const reserved = /^(?:BRIDGE_(?:CONFIGURATION_|FIXTURE_|LOCAL_HOST_CHILD_)|RUST_TEST_|LIBTEST_)/.test(key)
    || /^(?:RUSTFLAGS|RUSTDOCFLAGS|CARGO_ENCODED_RUSTFLAGS|CARGO_ENCODED_RUSTDOCFLAGS|CARGO_TARGET_DIR|CARGO_BUILD_TARGET|RUSTUP_TOOLCHAIN)$/.test(key)
    || /^CARGO_(?:TARGET_|PROFILE_|BUILD_(?:RUSTFLAGS|RUSTDOCFLAGS))/.test(key);
  assert.ok(!(reserved || /^(?:NODE_OPTIONS|NODE_PATH|VITEST_.*|VITE_.*|TS_NODE_.*|TSX_.*|ESBUILD_BINARY_PATH|ROLLDOWN_BINDING_PATH|NAPI_RS_NATIVE_LIBRARY_PATH|NPM_CONFIG_(?:NODE_OPTIONS|SCRIPT_SHELL))$/.test(key)) || !value, `Caller override ${key} is unsupported`);
}
const metadata = JSON.parse(readFileSync(path.join(root, 'dependencies/next-toolchain.json'), 'utf8'));
assert.equal(process.version, `v${metadata.node}`, 'Use the tracked Node runtime');
const sha = bytes => createHash('sha256').update(bytes).digest('hex');
const relative = selected => path.relative(root, selected).replaceAll('\\', '/');
const inputs = [
  'Cargo.toml', 'Cargo.lock', 'rust-toolchain.toml', '.cargo/config.toml', 'dependencies/next-toolchain.json',
  'package.json', 'pnpm-lock.yaml', 'pnpm-workspace.yaml', 'docs/next/HOST_ADAPTER_FOUNDATION.md',
  'scripts/next', 'crates/bridge-engine', 'crates/bridge-contracts', 'crates/bridge-domain', 'crates/bridge-toml', 'crates/bridge-journal-io',
  'contracts', 'ui/package.json', 'ui/tsconfig.json', 'ui/svelte.config.js', 'ui/vite.config.ts', 'ui/src', 'ui/tests'
];
const sourcesBefore = fingerprintInputRecords(root, inputs);
const required = {
  "embedded_host": [
    "embedded_deferred_close_services_external_turns_before_exact_once_drop",
    "embedded_multiple_driver_ticks_and_fallback_have_one_turn_budget",
    "embedded_driver_recursion_refuses_before_another_owner_call",
    "embedded_owner_callback_close_arms_before_later_queued_admission",
    "embedded_driver_error_and_observer_loss_keep_independent_progress",
    "embedded_close_error_and_abandonment_never_destroy_retained_owner",
    "embedded_safe_recovery_closes_once_and_preserves_recovery_disposition",
    "embedded_driver_panic_permanently_taints_custody_and_prohibits_closed",
    "embedded_owner_panic_permanently_stops_owner_calls_without_drop",
    "embedded_destructor_panic_does_not_publish_closed_or_reenter_owner",
    "embedded_external_close_retires_driver_but_fallback_keeps_deferred_work_serviced",
    "embedded_queued_close_retires_driver_after_the_one_turn_that_observes_it",
    "embedded_abandonment_keeps_owned_state_after_external_observation_is_dropped",
    "embedded_every_owner_entry_panic_permanently_refuses_calls_and_destruction",
    "embedded_owner_failure_precedes_a_later_driver_panic",
    "embedded_deferred_progress_requires_service_outside_the_owner_turn"
  ],
  "host_transport": [
    "non_send_owner_lease_and_journal_construct_invoke_and_drop_on_actor_thread",
    "caller_thread_factory_and_borrowed_pump_drain_work_on_that_same_thread",
    "strict_framing_rejects_duplicates_invalid_utf8_and_oversize_before_owner_capture",
    "queued_requests_are_bounded_and_queue_refusal_is_not_sent",
    "completed_unread_replies_and_subscriptions_have_independent_count_limits",
    "lost_commit_reply_and_dropped_subscription_replay_original_operation_without_duplicate_effects",
    "last_handle_disconnect_defers_drop_until_worker_reaches_real_fixture_boundary",
    "serialized_normal_close_refuses_later_admission_and_keeps_progressing",
    "observer_overflow_is_explicit_resnapshot_and_does_not_block_worker_completion",
    "foreign_and_expired_event_watermarks_refuse_without_reexecution",
    "close_errors_keep_host_until_actual_close_observation_becomes_available",
    "safe_recovery_exit_stays_recovery_required_and_does_not_auto_recover",
    "observer_driver_panic_is_contained_and_drains_owned_work_before_drop",
    "owner_panic_child_fixture",
    "owner_thread_panic_is_unknown_custody_until_actual_process_death_and_explicit_restart_recovery",
    "foreign_reply_request_id_is_refused_even_when_its_closed_schema_is_valid",
    "another_reply_kind_cannot_answer_an_exact_request_even_with_its_matching_id",
    "validly_framed_events_cannot_advance_beyond_actual_owner_watermark",
    "unimplemented_application_services_reject_and_do_not_bootstrap_synthetic_observations",
    "ready_subscription_delivers_actual_consecutive_kernel_events_before_snapshot_watermark",
    "embedded_kernel_admission_loss_and_deferred_close_keep_real_dispatcher_custody",
    "embedded_versioned_close_uses_real_snapshot_and_retires_driver_without_releasing_worker",
    "embedded_driver_error_after_admission_drains_real_kernel_work_to_safe_close",
    "embedded_driver_panic_after_real_admission_taints_custody_even_after_release",
    "embedded_real_kernel_safe_recovery_closes_without_running_recovery_or_mutation",
    "embedded_real_kernel_terminal_journal_failure_retains_lease_and_poison_after_fault_clear"
  ]
};
const frontendRequired = {
  "ui/tests/tauri-transport.test.ts": [
    "snapshot exchange waits for a genuine exact native registration acknowledgement",
    "initial and later polled events use the common watermark buffer during snapshot readiness",
    "last local unsubscribe revokes before readiness and cleans the known key again after late ACK",
    "shared local listeners use one observer and dispose only after the last listener leaves",
    "one unresolved poll across the adapter lifetime prevents reconnect readiness until real settlement",
    "slow poll holds one promise and idle/nonempty batches yield one bounded timer",
    "lost poll response faults once and never automatically resubmits an exchange mutation",
    "waiting abort invokes no exchange while the common client remains conservatively uncertain",
    "sent abort settles observation but retains request quota until actual invoke settlement",
    "32 exchanges and 8MiB request budget include locally abandoned native invocations",
    "readiness waiters reserve quota before invoke and waiting abort releases only its own slot",
    "8 local subscribers refuse a ninth before invoking or changing the shared observer",
    "known-key cleanup reserves only 8 entries and at most initial plus late ACK attempts",
    "allocator is canonical monotonic and cannot be reused to bypass a singleton lifetime quota",
    "request and reply scalar UTF-8 limits never truncate or normalize captured bytes",
    "malformed bounded poll DTO retires observation: version",
    "malformed bounded poll DTO retires observation: key",
    "malformed bounded poll DTO retires observation: extra",
    "malformed bounded poll DTO retires observation: two_frames",
    "malformed bounded poll DTO retires observation: too_large",
    "malformed bounded poll DTO retires observation: surrogate",
    "malformed bounded poll DTO retires observation: accessor",
    "invalid registration ACK cannot invoke initial snapshot and uses only known-key cleanup",
    "only an exact trusted native error DTO claims not_sent after invoke starts",
    "event exception retires before another callback and every bounded listener gets one terminal fault",
    "event disposal suppresses old observer snapshot and old reply cannot schedule another poll",
    "synchronous registration disposal before invoke returns cannot leak local observers or resurrect readiness",
    "an event callback can unsubscribe another local listener without delivering to its stale snapshot",
    "malformed raw reply reaches the same common framing validator without transport mutation",
    "idle and disposed adapters never invoke or activate a synthetic backend"
  ]
};
const directory = ownedArtifactPath(root, `artifacts/next/host-adapter-foundation/${randomUUID()}`, 'directory', { allowMissing: true });
mkdirSync(directory, { recursive: true });
ownedArtifactPath(root, relative(directory), 'directory');
const checks = [], binaries = [], toolsBefore = [], toolsAfter = [];
let sourcesAfter = null, failure = null, passed = false, frontendInventory = null, frontendReport = null, ownershipDocs = null;
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
  assert.equal(result.status, 0, `Host adapter ${id} failed; inspect retained command logs`);
  assert.ok(!result.error && !result.signal, `Host adapter ${id} did not complete normally`);
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
  for (const relativeTool of ['node_modules/pnpm/bin/pnpm.mjs', 'ui/node_modules/vitest/vitest.mjs', 'node_modules/typescript/bin/tsc', 'ui/node_modules/svelte-check/bin/svelte-check']) toolsBefore.push(observeTool(relativeTool, path.join(root, relativeTool)));
  const rootPackage = JSON.parse(readFileSync(ownedArtifactPath(root, 'package.json'), 'utf8'));
  const uiPackage = JSON.parse(readFileSync(ownedArtifactPath(root, 'ui/package.json'), 'utf8'));
  for (const [name, route, expected] of [
    ['pnpm', 'node_modules/pnpm/package.json', rootPackage.devDependencies.pnpm],
    ['typescript', 'node_modules/typescript/package.json', uiPackage.devDependencies.typescript],
    ['vitest', 'ui/node_modules/vitest/package.json', uiPackage.devDependencies.vitest],
    ['svelte-check', 'ui/node_modules/svelte-check/package.json', uiPackage.devDependencies['svelte-check']]
  ]) {
    const selected = path.join(root, route);
    assert.equal(JSON.parse(readFileSync(selected, 'utf8')).version, expected, 'Installed ' + name + ' must match its tracked pin');
    toolsBefore.push(observeTool(name + '-package-metadata', selected));
  }
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
  cargo('format', ['fmt', '-p', 'bridge-engine', '--', '--check']);
  cargo('clippy', ['clippy', '--locked', '--offline', '-p', 'bridge-engine', '--all-targets', '--target', rust.hostTarget, '--', '-D', 'warnings']);
  const dependencyTree = cargo('engine-dependency-tree', ['tree', '--locked', '--offline', '-p', 'bridge-engine', '--edges', 'normal']);
  assert.ok(!/\b(?:tauri|wry|webkit|webview|svelte)\b/i.test(dependencyTree), 'Engine remains frontend independent');
  const names = Object.keys(required);
  const output = cargo('compile-current-host-tests', ['test', '--locked', '--offline', '-p', 'bridge-engine', '--target', rust.hostTarget,
    ...names.flatMap(name => ['--test', name]), '--no-run', '--message-format', 'json']);
  const messages = output.split(/\r?\n/).filter(Boolean).map(line => JSON.parse(line));
  assert.equal(messages.filter(message => message.reason === 'build-finished' && message.success === true).length, 1,
    'Require this Cargo invocation to finish successfully');
  const artifacts = messages.filter(message => message.reason === 'compiler-artifact' && message.executable && message.profile.test
    && message.target.kind.includes('test') && names.includes(message.target.name));
  assert.equal(artifacts.length, names.length, 'This Cargo invocation must identify both host test executables exactly once');
  for (const target of names) {
    const selected = artifacts.filter(artifact => artifact.target.name === target);
    assert.equal(selected.length, 1, `Require one current ${target} compiler artifact`);
    const artifact = selected[0];
    assert.equal(realpathSync.native(artifact.manifest_path), realpathSync.native(path.join(root, 'crates/bridge-engine/Cargo.toml')));
    assert.equal(realpathSync.native(artifact.target.src_path), realpathSync.native(path.join(root, `crates/bridge-engine/tests/${target}.rs`)));
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
    assert.deepEqual([...discovered].sort(), [...required[binary.target]].sort(), 'Host adapter test inventory changed; review its required behavior mapping');
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
  const docs = cargo('host-ownership-docs', ['test', '--locked', '--offline', '-p', 'bridge-engine', '--target', rust.hostTarget, '--doc', 'host::', '--', '--color', 'never']);
  const docRows = docs.split(/\r?\n/).filter(row => / - compile fail \.\.\. ok$/.test(row));
  assert.equal(docRows.length, 6, 'Every declared host ownership compile-fail control must execute');
  assert.match(docs, /test result: ok\. 6 passed; 0 failed; 0 ignored; 0 measured;/);
  for (const item of ['host::embedded::EmbeddedOwner', 'host::embedded::EmbeddedOwner<H>::turn', 'host::OwnerThreadPump', 'host::spawn_local_host']) assert.ok(docRows.some(row => row.includes(' - ' + item + ' ')), 'Missing ownership item ' + item);
  ownershipDocs = {executed: docRows, tests: 6};
  run('frontend-types', process.execPath, ['scripts/next/pnpm.mjs', '--dir', 'ui', 'check']);
  const reportPath = ownedArtifactPath(root, relative(directory) + '/vitest.json', 'file', {allowMissing: true});
  run('frontend-adapter-tests', process.execPath, ['scripts/next/pnpm.mjs', '--dir', 'ui', 'exec', 'vitest', 'run', 'tests/tauri-transport.test.ts', '--reporter=json', '--outputFile=' + reportPath]);
  const frontendBytes = readFileSync(reportPath);
  frontendInventory = vitestEvidence(JSON.parse(frontendBytes.toString('utf8')), {root, required: frontendRequired});
  assert.equal(frontendInventory.tests, 30); assert.equal(frontendInventory.files.length, 1);
  frontendReport = {path: relative(reportPath), bytes: frontendBytes.length, sha256: sha(frontendBytes)};
  sourcesAfter = fingerprintInputRecords(root, inputs);
  assert.deepEqual(sourcesAfter, sourcesBefore, 'Source drift invalidates the host adapter observation');
  for (const tool of toolsBefore) toolsAfter.push(observeTool(tool.role, tool.route));
  assert.deepEqual(toolsAfter, toolsBefore, 'Tool routing or bytes changed during the host adapter observation');
  for (const binary of binaries) observeBinary(binary);
  passed = true;
} catch (error) {
  failure = { name: error?.name ?? 'Error', code: error?.code ?? null,
    message: String(error?.message ?? 'Host adapter observation failed').slice(0, 4096) };
} finally {
  if (sourcesAfter === null) {
    try { sourcesAfter = fingerprintInputRecords(root, inputs); }
    catch (error) { sourcesAfter = { unavailable: error?.code ?? error?.name ?? 'Error' }; }
  }
  const receipt = save('host-adapter-foundation.json', JSON.stringify({ schemaVersion: 'bridge-host-adapter-foundation-observation/v1',
    result: passed ? 'passed' : 'failed', startedAt, completedAt: new Date().toISOString(),
    host: { platform: process.platform, architecture: process.arch, node: process.version },
    toolchain: { pin: rust.pin, nativeTarget: rust.hostTarget }, inputs, sourcesBefore, sourcesAfter, toolsBefore, toolsAfter,
    requiredTests: required, binaries, checks, ownershipDocs, frontendRequired, frontendInventory, frontendReport, failure,
    boundary: 'Actual native-architecture portable owner and real kernel tests with controlled ports, retained test binaries, ownership compile-fail controls, and typed frontend poll adapter using injected invoke promises. No native GUI, production owner provisioning or installed runtime qualification.',
    portableModelObserved: passed, injectedFrontendAdapterObserved: passed, nativeWebviewQualified: false, productionOwnerQualified: false,
    physicalOwnerQualified: false, operationPortsAdopted: false, br21Accepted: false, nativeRuntimeQualified: false, releaseQualified: false
  }, null, 2) + '\n');
  console.log(JSON.stringify({ result: passed ? 'passed' : 'failed', tests: binaries.reduce((count, binary) => count + binary.executedTests.length, 0),
    receipt, br21Accepted: false, nativeRuntimeQualified: false, releaseQualified: false }));
  process.exitCode = passed ? 0 : 1;
}
