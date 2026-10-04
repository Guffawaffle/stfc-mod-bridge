import assert from 'node:assert/strict';
import { spawn, fork } from 'node:child_process';
import { createHash, randomBytes, randomUUID } from 'node:crypto';
import { constants, realpathSync } from 'node:fs';
import { access, lstat, mkdir, open, readFile, realpath, writeFile } from 'node:fs/promises';
import path from 'node:path';
import os from 'node:os';
import { fileURLToPath } from 'node:url';
import { setTimeout as delay } from 'node:timers/promises';
import { StringDecoder } from 'node:string_decoder';
import { fingerprintInputRecords } from './input-tree.mjs';
import { ownedArtifactPath } from './owned-artifact.mjs';
import { rustContext } from './rust-context.mjs';
import { nativeTestInventory, nativeTestResult } from './native-evidence.mjs';
import { controlInputs } from './qualification.mjs';

const SELF = fileURLToPath(import.meta.url), ROOT = path.resolve(import.meta.dirname, '../..');
const PACKAGE = 'bridge-platform-macos', TARGET = 'aarch64-apple-darwin';
const PROTOCOL = 'bridge-macos-fixture-supervisor/v1';
const MAX_OUTPUT = 8 * 1024 * 1024, MAX_BINARY = 128 * 1024 * 1024;
const SYSTEM = { chmod: '/bin/chmod', ls: '/bin/ls', ps: '/bin/ps', sh: '/bin/sh', diskutil: '/usr/sbin/diskutil', plutil: '/usr/bin/plutil' };
// Fixed reviewed start barrier. No command text, PID or argument list is public.
const START_BARRIER = 'kill -STOP "$$"; exec "$@"';
const OWNED_CASE = 'native_owned_child_exit_is_not_a_reusable_pid_binding';
const EXCLUDED = 'native_keychain_roundtrip_wrong_purpose_and_exact_delete';
const PRE_ANCHOR_CASES = Object.freeze(['pre-anchor-deadline', 'pre-anchor-disconnect', 'pre-anchor-signal']);
const SUPERVISOR_OPERATIONS = Object.freeze(['fixed-failure', 'deadline', 'disconnect', 'owned-case', ...PRE_ANCHOR_CASES]);
export const macFormatNames = Object.freeze([
  'absolute_paths_preserve_case_and_utf8_bytes', 'process_path_count_excludes_nul_and_matches_bounded_native_storage',
  'path_parser_refuses_lexical_aliases_and_nul', 'filesystem_root_has_no_fabricated_child', 'path_and_component_limits_are_enforced',
  'relative_mutation_names_cannot_escape_parent', 'stage_nonce_names_are_single_components_and_distinct',
  'process_start_stamp_keeps_kernel_units_and_bounds', 'process_architecture_is_not_inferred_from_host',
  'unvalidated_volume_capability_stays_unknown', 'atomic_exchange_requires_two_distinct_exact_objects',
  'keychain_purpose_domains_and_context_bytes_never_alias', 'keychain_purpose_refuses_unknown_versions_empty_and_control_contexts',
  'keychain_reference_has_closed_canonical_grammar', 'keychain_reference_does_not_authorize_another_purpose',
  'host_gate_never_admits_intel_or_non_macos'
]);
export const macControlledNames = Object.freeze([
  'rational_timebase_converts_ticks_to_milliseconds', 'conversion_floors_submillisecond_values_and_accepts_zero_ticks',
  'conversion_preserves_wide_intermediate_values', 'invalid_timebase_never_produces_a_timestamp',
  'unrepresentable_milliseconds_are_refused', 'native_timebase_failure_never_reads_ticks_or_uses_partial_output',
  'random_buffer_is_initialized_before_fill_and_success_is_returned_exactly',
  'random_failure_wipes_partial_output_and_preserves_native_status', 'successful_random_call_wipes_the_discarded_local_copy'
].map(name => `providers::tests::${name}`));
export const macProviderNames = Object.freeze([
  'providers::tests::native_continuous_milliseconds_are_readable_and_advance',
  'providers::tests::native_secure_random_returns_an_owned_sample_without_logging_it'
]);
export const macNativeNames = Object.freeze([
  'native_descriptor_identity_and_parent_replacement_refusal', 'native_symlink_ancestor_and_finder_alias_are_refused',
  'native_case_semantics_follow_the_captured_volume', 'native_staged_exchange_retains_backup_and_explicit_permissions',
  'native_exchange_refuses_stale_and_hard_link_destinations_before_call',
  'native_exact_process_observes_architecture_and_rejects_forged_generation', OWNED_CASE,
  'native_bundle_executable_requires_descriptor_ancestry', 'native_unsigned_signature_cannot_grant_os_trust_or_publisher_policy',
  'native_non_gui_focus_and_unavailable_shell_features_are_explicit'
]);
export const macFixtureInputs = Object.freeze([...new Set([...controlInputs, 'AGENTS.md', 'Cargo.toml', 'Cargo.lock',
  'rust-toolchain.toml', '.cargo/config.toml', 'dependencies/next-toolchain.json', 'docs/next/MAC_PLATFORM_FIXTURES.md',
  '.github/workflows/next-foundation.yml', 'scripts/next', 'crates/bridge-domain', 'crates/bridge-contracts', 'crates/bridge-platform-macos'])].sort());
const docItems = [
  ['filesystem.rs', 'filesystem::RetainedDirectory'], ['filesystem.rs', 'filesystem::ReadOnlyFile'],
  ['filesystem.rs', 'filesystem::StagedReplacement'], ['process.rs', 'process::ExactProcessGuard'], ['secrets.rs', 'secrets::Plaintext']
];
const sha = bytes => createHash('sha256').update(bytes).digest('hex');
const record = value => value !== null && typeof value === 'object' && !Array.isArray(value);
function requireProof(condition, code) { if (!condition) { const error = new Error(code); error.code = code; throw error; } }
const relative = file => path.relative(ROOT, file).split(path.sep).join('/');
const integerPid = value => Number.isSafeInteger(value) && value > 1 && value <= 0x7fffffff;
const statIdentity = stat => ({ dev: String(stat.dev), ino: String(stat.ino), uid: Number(stat.uid), mode: Number(stat.mode) & 0o7777, links: Number(stat.nlink) });
const identityEqual = (a, b) => a.dev === b.dev && a.ino === b.ino && a.uid === b.uid && a.mode === b.mode && a.links === b.links;

// Pure admission/parser exports let the shared gate tests inspect refusals. The
// executable entry always supplies actual process observations, never overrides.
export function macFixtureAdmission({ argv, platform, architecture, version, execArgv, environment }) {
  requireProof(argv.length === 2, 'MAC_FIXTURE_PUBLIC_ARGUMENTS');
  requireProof(platform === 'darwin' && architecture === 'arm64', 'MAC_FIXTURE_NATIVE_HOST_REQUIRED');
  requireProof(version === 'v24.14.1', 'MAC_FIXTURE_NODE_PIN');
  requireProof(execArgv.length === 0 && !environment.NODE_OPTIONS?.trim(), 'MAC_FIXTURE_NODE_LOADER_OVERRIDE');
  // Rustup uses this value before argv[0], including nonempty whitespace.
  requireProof(!environment.RUSTUP_FORCE_ARG0, 'MAC_FIXTURE_TOOL_OVERRIDE');
  for (const [key, value] of Object.entries(environment)) {
    if (!value?.trim()) continue;
    requireProof(!/^BRIDGE_MACOS_/i.test(key), 'MAC_FIXTURE_INJECTED_SELECTOR');
    requireProof(!/^(?:ENV|BASH_ENV|SHELLOPTS|BASHOPTS|BASH_FUNC_.*|DYLD_.*)$/.test(key), 'MAC_FIXTURE_INJECTED_RUNTIME');
  }
  requireProof(environment.GITHUB_ACTIONS === 'true' && environment.RUNNER_ENVIRONMENT === 'github-hosted' &&
    environment.RUNNER_OS === 'macOS' && environment.RUNNER_ARCH === 'ARM64', 'MAC_FIXTURE_EPHEMERAL_HOST_REQUIRED');
  requireProof(environment.GITHUB_REPOSITORY === 'Guffawaffle/stfc-mod-bridge' && /^[a-f0-9]{40}$/.test(environment.GITHUB_SHA || '') &&
    /^[1-9][0-9]*$/.test(environment.GITHUB_RUN_ID || '') && /^[1-9][0-9]*$/.test(environment.GITHUB_RUN_ATTEMPT || '') &&
    /^[A-Za-z_][A-Za-z0-9_-]{0,127}$/.test(environment.GITHUB_JOB || ''), 'MAC_FIXTURE_JOB_BINDING');
}
function childEnvironment(source) {
  const result = { ...source, LC_ALL: 'C', LANG: 'C', COMMAND_MODE: 'unix2003' };
  for (const key of Object.keys(result)) if (/^BRIDGE_MACOS_/i.test(key) || /^(?:RUSTUP_FORCE_ARG0|NODE_OPTIONS|NODE_CHANNEL_FD|NODE_CHANNEL_SERIALIZATION_MODE|ENV|BASH_ENV|SHELLOPTS|BASHOPTS|BASH_FUNC_.*|DYLD_.*)$/.test(key)) delete result[key];
  return result;
}
export function macMachO(bytes) {
  requireProof(bytes.length >= 32 && bytes.length <= MAX_BINARY && bytes.readUInt32LE(0) === 0xfeedfacf &&
    bytes.readUInt32LE(4) === 0x0100000c && bytes.readUInt32LE(12) === 2, 'MAC_FIXTURE_EXECUTABLE_ARCHITECTURE');
  const commands = bytes.readUInt32LE(16), commandBytes = bytes.readUInt32LE(20);
  requireProof(commands > 0 && commands <= 65536 && commandBytes >= commands * 8 && commandBytes <= bytes.length - 32, 'MAC_FIXTURE_MACHO_HEADER');
  let offset = 32;
  for (let index = 0; index < commands; index++) {
    requireProof(offset + 8 <= 32 + commandBytes, 'MAC_FIXTURE_MACHO_COMMAND');
    const size = bytes.readUInt32LE(offset + 4);
    requireProof(size >= 8 && size % 8 === 0 && size <= 32 + commandBytes - offset, 'MAC_FIXTURE_MACHO_COMMAND'); offset += size;
  }
  requireProof(offset === 32 + commandBytes, 'MAC_FIXTURE_MACHO_COMMAND');
  return { format: 'thin-mach-o-64', architecture: 'arm64', cpuSubtype: bytes.readUInt32LE(8), fileType: 'executable', commands, commandBytes };
}
export function macPsRows(stdout, group) {
  requireProof(typeof stdout === 'string' && Buffer.byteLength(stdout) <= 8192 && integerPid(group), 'MAC_FIXTURE_GROUP_BOUND');
  const lines = stdout.trim().split(/\r?\n/).filter(Boolean);
  requireProof(lines.length <= 32, 'MAC_FIXTURE_GROUP_BOUND');
  const seen = new Set();
  return lines.map(line => {
    const match = /^\s*([1-9][0-9]*)\s+([1-9][0-9]*)\s+([A-Za-z+<>]{1,16})\s*$/.exec(line);
    requireProof(match && integerPid(Number(match[1])) && Number(match[2]) === group && !seen.has(Number(match[1])), 'MAC_FIXTURE_GROUP_UNKNOWN');
    seen.add(Number(match[1]));
    return { pid: Number(match[1]), pgid: Number(match[2]), state: match[3] };
  });
}
export function macPsObservation(value, group) {
  requireProof(record(value) && typeof value.stdout === 'string' && typeof value.stderr === 'string' && Buffer.byteLength(value.stderr) <= 8192 && value.closed === true && !value.error && value.signal === null, 'MAC_FIXTURE_PS_CONTRACT');
  const rows = macPsRows(value.stdout, group), empty = !value.stdout.trim() && !value.stderr.trim();
  requireProof(value.exitCode === 1 && empty || value.exitCode === 0 && rows.length > 0 && !value.stderr.trim(), 'MAC_FIXTURE_PS_CONTRACT'); return rows;
}
export function macApfsPlistJson(stdout) {
  requireProof(typeof stdout === 'string' && Buffer.byteLength(stdout) <= 512 * 1024, 'MAC_FIXTURE_APFS_UNKNOWN');
  // Inspect decoded object-key tokens, including escaped spellings, so JSON's
  // last-key-wins behavior cannot conceal a duplicate critical schema field.
  const criticalKeys = [...stdout.matchAll(/"(?:\\[\s\S]|[^"\\])*"/g)].filter(match => /^\s*:/.test(stdout.slice(match.index + match[0].length)) && JSON.parse(match[0]) === 'FilesystemType');
  requireProof(criticalKeys.length === 1, 'MAC_FIXTURE_APFS_UNKNOWN');
  const value = JSON.parse(stdout); requireProof(record(value) && value.FilesystemType === 'apfs', 'MAC_FIXTURE_APFS_UNKNOWN');
  return { filesystemType: 'apfs' };
}
export function macAclAbsent(stdout, kind) {
  requireProof(typeof stdout === 'string' && Buffer.byteLength(stdout) <= 8192 && ['directory', 'file'].includes(kind), 'MAC_FIXTURE_ACL_UNKNOWN');
  const lines = stdout.trimEnd().split(/\r?\n/), expected = kind === 'directory' ? 'drwx------' : '-rwx------';
  requireProof(lines.length === 1 && lines[0].split(/\s+/)[0].replace(/@$/, '') === expected, 'MAC_FIXTURE_ACL_UNKNOWN');
  return { extendedAcl: 'observed-absent', mode: '0700', xattrMarker: lines[0].split(/\s+/)[0].endsWith('@') };
}
export function macArtifactInventory(stdout, { name, kind, manifest, source, targetPrefix, test }) {
  requireProof(typeof stdout === 'string' && Buffer.byteLength(stdout) <= MAX_OUTPUT, 'MAC_FIXTURE_COMPILER_ARTIFACT_BOUND');
  const lines = stdout.split(/\r?\n/).filter(Boolean); requireProof(lines.length <= 4096, 'MAC_FIXTURE_COMPILER_ARTIFACT_BOUND');
  const messages = lines.map(line => JSON.parse(line)); requireProof(messages.every(record), 'MAC_FIXTURE_COMPILER_ARTIFACT_INVENTORY');
  const finished = messages.filter(message => message.reason === 'build-finished');
  requireProof(finished.length === 1 && finished[0].success === true, 'MAC_FIXTURE_BUILD_FINISHED');
  const found = messages.filter(message => message.reason === 'compiler-artifact' && message.target?.name === name &&
    Array.isArray(message.target.kind) && message.target.kind.length === 1 && message.target.kind[0] === kind && message.profile?.test === test && typeof message.executable === 'string');
  requireProof(found.length === 1, 'MAC_FIXTURE_COMPILER_ARTIFACT_INVENTORY');
  const selected = found[0];
  requireProof(typeof selected.manifest_path === 'string' && typeof selected.target.src_path === 'string' &&
    path.resolve(selected.manifest_path) === path.resolve(manifest) && path.resolve(selected.target.src_path) === path.resolve(source), 'MAC_FIXTURE_COMPILER_ARTIFACT_SOURCE');
  const targetRelation = path.relative(path.resolve(targetPrefix), path.resolve(selected.executable));
  requireProof(targetRelation && targetRelation !== '..' && !targetRelation.startsWith(`..${path.sep}`) && !path.isAbsolute(targetRelation), 'MAC_FIXTURE_COMPILER_ARTIFACT_TARGET'); return selected;
}
function macCompilerArtifact(stdout, { name, kind, source, test }) {
  const selected = macArtifactInventory(stdout, { name, kind, manifest: path.join(ROOT, `crates/${PACKAGE}/Cargo.toml`), source: path.join(ROOT, source), targetPrefix: path.join(ROOT, 'target', TARGET), test });
  requireProof(realpathSync(selected.manifest_path) === realpathSync(path.join(ROOT, `crates/${PACKAGE}/Cargo.toml`)) &&
    realpathSync(selected.target.src_path) === realpathSync(path.join(ROOT, source)), 'MAC_FIXTURE_COMPILER_ARTIFACT_SOURCE');
  const relation = relative(selected.executable);
  requireProof(relation.startsWith(`target/${TARGET}/`), 'MAC_FIXTURE_COMPILER_ARTIFACT_TARGET');
  return { ...selected, executable: ownedArtifactPath(ROOT, relation) };
}

async function observeFile(selected, { native = false, privateFile = false, retained = null } = {}) {
  const physical = await realpath(selected), before = await lstat(physical, { bigint: true });
  requireProof(before.isFile() && before.size > 0n && before.size <= BigInt(MAX_BINARY), 'MAC_FIXTURE_FILE_BOUND');
  if (privateFile) requireProof(!((await lstat(selected)).isSymbolicLink()) && before.nlink === 1n && Number(before.uid) === process.getuid() && (Number(before.mode) & 0o7777) === 0o700, 'MAC_FIXTURE_HELPER_PRIVACY');
  const handle = retained || await open(physical, constants.O_RDONLY | (constants.O_NOFOLLOW || 0));
  try {
    const opened = await handle.stat({ bigint: true });
    requireProof(opened.dev === before.dev && opened.ino === before.ino && opened.size === before.size, 'MAC_FIXTURE_FILE_IDENTITY');
    const hash = createHash('sha256'), buffer = Buffer.alloc(65536), header = [], limit = Number(before.size);
    let position = 0;
    while (position < limit) { const { bytesRead } = await handle.read(buffer, 0, Math.min(buffer.length, limit - position), position); requireProof(bytesRead > 0, 'MAC_FIXTURE_FILE_CHANGED'); hash.update(buffer.subarray(0, bytesRead)); if (native) header.push(Buffer.from(buffer.subarray(0, bytesRead))); position += bytesRead; }
    const after = await handle.stat({ bigint: true }), pathAfter = await lstat(physical, { bigint: true });
    requireProof(after.dev === before.dev && after.ino === before.ino && after.size === before.size && after.mtimeNs === before.mtimeNs && after.ctimeNs === before.ctimeNs && pathAfter.dev === after.dev && pathAfter.ino === after.ino && await realpath(selected) === physical, 'MAC_FIXTURE_FILE_CHANGED');
    return { requestedPath: selected, physicalPath: physical, bytes: limit, sha256: hash.digest('hex'), identity: statIdentity(after), ...(native ? { macho: macMachO(Buffer.concat(header)) } : {}) };
  } finally { if (!retained) await handle.close(); }
}
async function assertFileFence(expected, options = {}) {
  const current = await observeFile(expected.requestedPath, options);
  requireProof(current.sha256 === expected.sha256 && current.bytes === expected.bytes && current.physicalPath === expected.physicalPath && identityEqual(current.identity, expected.identity), 'MAC_FIXTURE_FILE_FENCE'); return current;
}
async function copyBoundHelper(source, destination) {
  const handle = await open(source.physicalPath, constants.O_RDONLY | (constants.O_NOFOLLOW || 0));
  try {
    const before = await handle.stat({ bigint: true });
    requireProof(before.isFile() && before.size === BigInt(source.bytes) && before.size <= BigInt(MAX_BINARY) && identityEqual(statIdentity(before), source.identity), 'MAC_FIXTURE_HELPER_SOURCE_CHANGED');
    const buffer = Buffer.alloc(65536), hash = createHash('sha256'); let position = 0;
    while (position < source.bytes) {
      const { bytesRead } = await handle.read(buffer, 0, Math.min(buffer.length, source.bytes - position), position); requireProof(bytesRead > 0, 'MAC_FIXTURE_HELPER_SOURCE_CHANGED');
      hash.update(buffer.subarray(0, bytesRead)); let written = 0;
      while (written < bytesRead) { const value = await destination.write(buffer, written, bytesRead - written, position + written); requireProof(value.bytesWritten > 0, 'MAC_FIXTURE_HELPER_COPY'); written += value.bytesWritten; }
      position += bytesRead;
    }
    const extra = await handle.read(buffer, 0, 1, position), after = await handle.stat({ bigint: true });
    requireProof(extra.bytesRead === 0 && before.dev === after.dev && before.ino === after.ino && before.size === after.size && before.mtimeNs === after.mtimeNs && before.ctimeNs === after.ctimeNs && hash.digest('hex') === source.sha256 && await realpath(source.requestedPath) === source.physicalPath, 'MAC_FIXTURE_HELPER_SOURCE_CHANGED');
    await destination.sync();
  } finally { await handle.close(); }
}
async function resolveTool(requested, environment) {
  const candidates = path.isAbsolute(requested) ? [requested] : (environment.PATH || '').split(path.delimiter).filter(Boolean).map(directory => path.join(directory, requested));
  for (const candidate of candidates) { try { await access(candidate, constants.X_OK); return await realpath(candidate); } catch { /* Try the next explicit PATH entry. */ } }
  requireProof(false, 'MAC_FIXTURE_TOOL_UNAVAILABLE');
}
function captureChild(child, timeoutMs, { input = null, onFailure = null, maxOutput = MAX_OUTPUT } = {}) {
  return new Promise(resolve => {
    const startedAt = new Date().toISOString(), stdout = [], stderr = [];
    let outputBytes = 0, failure = null, settled = false, fallback;
    const fail = code => { if (failure) return; failure = code; if (onFailure) onFailure(code); else child.kill('SIGKILL'); fallback = setTimeout(() => finish(null, null, false), 5000); };
    const timer = setTimeout(() => fail('MAC_FIXTURE_COMMAND_TIMEOUT'), timeoutMs);
    function finish(exitCode, signal, closed = true) { if (settled) return; settled = true; clearTimeout(timer); clearTimeout(fallback); resolve({ startedAt, completedAt: new Date().toISOString(), exitCode, signal, closed, error: failure, stdout: Buffer.concat(stdout).toString('utf8'), stderr: Buffer.concat(stderr).toString('utf8') }); }
    for (const [stream, chunks] of [[child.stdout, stdout], [child.stderr, stderr]]) stream?.on('data', bytes => { outputBytes += bytes.length; if (outputBytes > maxOutput) { fail('MAC_FIXTURE_OUTPUT_BOUND'); return; } chunks.push(bytes); });
    child.on('error', () => fail('MAC_FIXTURE_COMMAND_ERROR')); child.once('close', (code, signal) => finish(code, signal));
    if (input !== null) { child.stdin.on('error', () => fail('MAC_FIXTURE_COMMAND_INPUT')); child.stdin.end(input); }
  });
}
// Physical executable custody and multicall dispatch names are separate. The
// only alternate dispatch name is the internally selected Rustup CLI mode.
export function macFixtureCommandRouting(executable, argv0) {
  requireProof(typeof executable === 'string' && path.isAbsolute(executable), 'MAC_FIXTURE_TOOL_ROUTE');
  requireProof(argv0 === undefined || argv0 === 'rustup', 'MAC_FIXTURE_TOOL_DISPATCH');
  return { executable, argv0: argv0 ?? executable };
}
async function command(executable, argv, environment, timeout = 180000, options = {}) {
  const routing = macFixtureCommandRouting(executable, options.argv0);
  const child = spawn(routing.executable, argv, { argv0: routing.argv0, cwd: ROOT, env: environment, shell: false, detached: false, stdio: [options.input === undefined ? 'ignore' : 'pipe', 'pipe', 'pipe'] });
  return captureChild(child, timeout, { input: options.input ?? null, maxOutput: options.maxOutput || MAX_OUTPUT });
}
async function psRows(selector, pid, group, environment) {
  requireProof(integerPid(pid) && integerPid(group), 'MAC_FIXTURE_GROUP_ID');
  const value = await command(SYSTEM.ps, [selector, String(pid), '-o', 'pid=,pgid=,stat='], environment, 3000, { maxOutput: 8192 });
  return macPsObservation(value, group);
}
async function rootFence(directory, handle, expected) {
  const physical = ownedArtifactPath(ROOT, relative(directory), 'directory'), disk = await lstat(physical, { bigint: true }), held = await handle.stat({ bigint: true });
  requireProof(disk.isDirectory() && held.isDirectory() && disk.dev === held.dev && disk.ino === held.ino &&
    Number(held.uid) === process.getuid() && process.getuid() === process.geteuid() && process.getuid() !== 0 &&
    (Number(held.mode) & 0o7777) === 0o700 && String(held.dev) === expected.dev && String(held.ino) === expected.ino, 'MAC_FIXTURE_ROOT_CUSTODY');
  return statIdentity(held);
}
function exactKeys(value, keys) { requireProof(record(value) && Object.keys(value).sort().join('|') === [...keys].sort().join('|'), 'MAC_FIXTURE_SUPERVISOR_SCHEMA'); }

// Every awaited setup/continuation uses this same terminal admission latch.
// Pure tests may delay an observation, cancel, then prove admission stays shut.
export class MacSupervisorAdmission {
  #cancelled = false; #anchored = false; #launches = 0;
  get anchored() { return this.#anchored; }
  get launches() { return this.#launches; }
  active() { requireProof(!this.#cancelled, 'MAC_FIXTURE_SUPERVISOR_CANCELLED'); }
  cancel() { this.#cancelled = true; }
  async observe(work) { this.active(); const value = await work(); this.active(); return value; }
  admitAnchor() { this.active(); requireProof(!this.#anchored, 'MAC_FIXTURE_ANCHOR_REUSED'); this.#anchored = true; }
  admitLaunch() { this.active(); requireProof(this.#anchored && this.#launches === 0, 'MAC_FIXTURE_CHILD_ADMISSION'); this.#launches++; }
}
export async function macBoundedDiagnostic(write) {
  let timer;
  try {
    return await Promise.race([Promise.resolve().then(write).then(() => true, () => false),
      new Promise(resolve => { timer = setTimeout(() => resolve(false), 250); })]);
  } finally { clearTimeout(timer); }
}

// This role exists only on a fork-created IPC channel. It accepts closed
// operations and the same source-bound helper/native case, never arbitrary work.
async function supervisor() {
  const environment = childEnvironment(process.env);
  const admission = new MacSupervisorAdmission();
  let configuration = null, disposing = false, deadline, fixtureHandle;
  const emit = event => new Promise((resolve, reject) => process.stdout.write(`${JSON.stringify({ schemaVersion: PROTOCOL, nonce: configuration?.nonce || null, supervisorPid: process.pid, ...event })}\n`, error => error ? reject(error) : resolve()));
  async function dispose(reason) {
    if (disposing) return; disposing = true; admission.cancel(); clearTimeout(deadline);
    if (!admission.anchored) {
      await macBoundedDiagnostic(() => emit({ event: 'refused-before-anchor', reason, childLaunches: admission.launches }));
      process.exitCode = 2; if (process.connected) process.disconnect(); return;
    }
    // The marker is disposition intent. SIGKILL exit and actual group absence
    // are separately required by the parent, including after IPC disconnect.
    // A stalled diagnostic pipe cannot postpone the owning group's disposal.
    // Lost markers invalidate the parent's proof rather than suppressing kill.
    await macBoundedDiagnostic(() => emit({ event: 'disposing', reason, group: process.pid }));
    try { process.kill(-process.pid, 'SIGKILL'); } catch { process.exitCode = 2; if (process.connected) process.disconnect(); }
  }
  deadline = setTimeout(() => void dispose('supervisor-deadline'), 15000);
  process.on('disconnect', () => void dispose('parent-disconnect'));
  process.on('SIGTERM', () => void dispose('supervisor-signal')); process.on('SIGINT', () => void dispose('supervisor-signal'));
  process.on('uncaughtException', () => void dispose('supervisor-exception')); process.on('unhandledRejection', () => void dispose('supervisor-exception'));
  process.on('message', message => {
    if (configuration) {
      try { exactKeys(message, ['schemaVersion', 'nonce', 'action']); requireProof(message.schemaVersion === PROTOCOL && message.nonce === configuration.nonce && ['dispose', 'fail'].includes(message.action), 'MAC_FIXTURE_SUPERVISOR_CONTROL'); void dispose(message.action === 'fail' ? 'fixed-failure' : 'parent-disposal'); } catch { void dispose('invalid-control'); }
      return;
    }
    void (async () => {
      exactKeys(message, ['schemaVersion', 'nonce', 'operation', 'fixture', 'fixtureIdentity', 'helper', 'testBinary', 'tools', 'sourcesSha256']);
      requireProof(message.schemaVersion === PROTOCOL && /^[a-f0-9]{32}$/.test(message.nonce) && SUPERVISOR_OPERATIONS.includes(message.operation), 'MAC_FIXTURE_SUPERVISOR_CONTROL');
      configuration = message;
      admission.active();
      macFixtureAdmission({ argv: process.argv, platform: process.platform, architecture: process.arch, version: process.version, execArgv: process.execArgv, environment: process.env });
      requireProof(realpathSync(process.cwd()) === realpathSync(ROOT), 'MAC_FIXTURE_CWD');
      requireProof(sha(JSON.stringify(fingerprintInputRecords(ROOT, macFixtureInputs))) === message.sourcesSha256, 'MAC_FIXTURE_SUPERVISOR_SOURCE');
      // Source fingerprinting occurs before any native child is launched. Every
      // subsequent custody operation, including hashing, uses async file I/O.
      for (const name of ['node', 'script', 'ps', 'sh']) await admission.observe(() => assertFileFence(message.tools[name]));
      requireProof(message.tools.node.physicalPath === await realpath(process.execPath) && message.tools.script.physicalPath === await realpath(SELF) && message.tools.ps.requestedPath === SYSTEM.ps && message.tools.sh.requestedPath === SYSTEM.sh, 'MAC_FIXTURE_SUPERVISOR_TOOLS');
      const fixture = ownedArtifactPath(ROOT, relative(message.fixture), 'directory');
      requireProof(relative(fixture).startsWith('artifacts/next/macos-platform-fixtures/') && path.basename(fixture).match(/^[a-f0-9-]{36}$/), 'MAC_FIXTURE_SUPERVISOR_ROOT');
      await admission.observe(async () => { fixtureHandle = await open(fixture, constants.O_RDONLY | (constants.O_NOFOLLOW || 0)); });
      await admission.observe(() => rootFence(fixture, fixtureHandle, message.fixtureIdentity));
      requireProof(message.helper.requestedPath === path.join(fixture, 'native_fixture_child'), 'MAC_FIXTURE_SUPERVISOR_HELPER');
      await admission.observe(() => assertFileFence(message.helper, { native: true, privateFile: true }));
      if (PRE_ANCHOR_CASES.includes(message.operation)) {
        requireProof(message.testBinary === null, 'MAC_FIXTURE_SUPERVISOR_CASE');
        if (message.operation === 'pre-anchor-deadline') { clearTimeout(deadline); deadline = setTimeout(() => void dispose('pre-anchor-deadline'), 100); }
        await admission.observe(() => emit({ event: 'validating', phase: 'held-validation-before-anchor', childLaunches: admission.launches }));
        // Closed qualification delay exercises a real pending setup continuation.
        await admission.observe(() => delay(500));
        // Missed cancellation must reach the common guarded startup path and
        // fail the parent's exact event/exit inventory, rather than a test-only
        // refusal masking an initialization-continuation regression.
      }
      const ownRows = await admission.observe(() => psRows('-p', process.pid, process.pid, environment));
      requireProof(ownRows.length === 1 && ownRows[0].pid === process.pid && !ownRows[0].state.startsWith('Z'), 'MAC_FIXTURE_ANCHOR_UNKNOWN'); admission.admitAnchor();
      let executable = message.helper.physicalPath, argv = ['bridge-native-child-v1', message.nonce];
      if (message.operation === 'owned-case') {
        const artifact = message.testBinary?.compilerArtifact;
        requireProof(relative(message.testBinary.requestedPath).startsWith(`target/${TARGET}/`) && message.testBinary.target === 'native' &&
          artifact?.reason === 'compiler-artifact' && artifact.target?.name === 'native' && artifact.target?.kind?.length === 1 && artifact.target.kind[0] === 'test' &&
          artifact.profile?.test === true && artifact.manifest_path === path.join(ROOT, `crates/${PACKAGE}/Cargo.toml`) &&
          artifact.target.src_path === path.join(ROOT, `crates/${PACKAGE}/tests/native.rs`) && artifact.executable === message.testBinary.requestedPath,
        'MAC_FIXTURE_SUPERVISOR_CASE');
        await admission.observe(() => assertFileFence(message.testBinary, { native: true })); executable = message.testBinary.physicalPath; argv = [OWNED_CASE, '--ignored', '--exact', '--test-threads=1'];
      } else requireProof(message.testBinary === null, 'MAC_FIXTURE_SUPERVISOR_CASE');
      const nativeEnvironment = { ...environment, BRIDGE_MACOS_NATIVE_FIXTURE_ROOT: fixture, BRIDGE_MACOS_OWNED_CHILD: message.helper.physicalPath, BRIDGE_MACOS_OWNED_CHILD_SHA256: message.helper.sha256 };
      admission.active(); clearTimeout(deadline); deadline = setTimeout(() => void dispose(message.operation === 'deadline' ? 'fixture-timeout' : 'supervisor-deadline'), message.operation === 'owned-case' ? 30000 : 8000);
      admission.admitLaunch();
      const child = spawn(SYSTEM.sh, ['-c', START_BARRIER, 'bridge-macos-fixture-start-v1', executable, ...argv], { cwd: ROOT, env: nativeEnvironment, shell: false, detached: false, stdio: ['pipe', 'pipe', 'pipe'] });
      const result = captureChild(child, 35000, { maxOutput: 65536, onFailure: () => void dispose('child-failure') });
      requireProof(integerPid(child.pid), 'MAC_FIXTURE_CHILD_PID');
      let stopped = null;
      for (let attempt = 0; attempt < 40; attempt++) { const rows = await admission.observe(() => psRows('-p', child.pid, process.pid, environment)); if (rows.length === 1 && rows[0].pid === child.pid && rows[0].state.startsWith('T')) { stopped = rows[0]; break; } requireProof(child.exitCode === null && child.signalCode === null, 'MAC_FIXTURE_BARRIER_EXIT'); await admission.observe(() => delay(25)); }
      requireProof(stopped && child.exitCode === null && child.signalCode === null, 'MAC_FIXTURE_BARRIER_UNKNOWN');
      await admission.observe(() => emit({ event: 'barrier', child: stopped, shellSha256: message.tools.sh.sha256, programSha256: sha(START_BARRIER), executableSha256: message.operation === 'owned-case' ? message.testBinary.sha256 : message.helper.sha256 }));
      admission.active();
      requireProof(child.kill('SIGCONT'), 'MAC_FIXTURE_BARRIER_RESUME');
      if (message.operation !== 'owned-case') {
        await admission.observe(() => delay(100));
        const rows = await admission.observe(() => psRows('-p', child.pid, process.pid, environment));
        requireProof(rows.length === 1 && rows[0].pid === child.pid && !/^[TZ]/.test(rows[0].state) && child.exitCode === null && child.signalCode === null, 'MAC_FIXTURE_HELPER_NOT_ALIVE');
        await admission.observe(() => emit({ event: 'ready', child: rows[0], helperSha256: message.helper.sha256 }));
        // stdin remains held open until anchored group disposal, deliberately.
      } else {
        const value = await admission.observe(() => result);
        await admission.observe(() => emit({ event: 'result', value }));
        await fixtureHandle.close();
        await dispose(value.closed && !value.error && value.exitCode === 0 && value.signal === null ? 'normal-result' : 'child-failure');
      }
    })().catch(() => void dispose('supervisor-refusal')).finally(async () => { await fixtureHandle?.close().catch(() => {}); });
  });
}

async function runSupervised(operation, context, retain) {
  const preAnchor = PRE_ANCHOR_CASES.includes(operation);
  await assertFileFence(context.tools.node); await assertFileFence(context.tools.script);
  await assertFileFence(context.helper, { native: true, privateFile: true, retained: context.helperHandle });
  const nonce = randomBytes(16).toString('hex'), environment = childEnvironment(context.environment);
  const child = fork(SELF, [], { cwd: ROOT, execPath: context.tools.node.physicalPath, execArgv: [], env: environment, detached: true, stdio: ['ignore', 'pipe', 'pipe', 'ipc'], serialization: 'json' });
  requireProof(integerPid(child.pid), 'MAC_FIXTURE_SUPERVISOR_PID');
  let protocolFailure = null, ready = false, preAnchorReady = false, triggerSent = false;
  const requestDisposal = () => { if (child.connected) child.send({ schemaVersion: PROTOCOL, nonce, action: 'dispose' }, error => { if (error) protocolFailure ||= 'MAC_FIXTURE_IPC_ERROR'; }); };
  const signals = ['SIGINT', 'SIGTERM'], signalHandler = () => { protocolFailure ||= 'MAC_FIXTURE_PARENT_SIGNAL'; requestDisposal(); };
  for (const signal of signals) process.on(signal, signalHandler);
  const captured = captureChild(child, 45000, { maxOutput: 256 * 1024, onFailure: () => { protocolFailure ||= 'MAC_FIXTURE_PARENT_DEADLINE'; requestDisposal(); } });
  child.on('error', () => { protocolFailure ||= 'MAC_FIXTURE_IPC_ERROR'; });
  const monitor = setInterval(() => {}, 1000); // Keep parent custody referenced.
  child.stdout.on('data', () => {});
  // Readiness also travels over stdout, which remains retained after IPC closes.
  let pending = ''; const decoder = new StringDecoder('utf8');
  child.stdout.on('data', bytes => {
    pending += decoder.write(bytes);
    if (pending.length > 256 * 1024) { protocolFailure ||= 'MAC_FIXTURE_SUPERVISOR_OUTPUT'; requestDisposal(); return; }
    for (;;) {
      const index = pending.indexOf('\n'); if (index < 0) break; const line = pending.slice(0, index); pending = pending.slice(index + 1);
      try {
        const event = JSON.parse(line); requireProof(event.schemaVersion === PROTOCOL && event.nonce === nonce && event.supervisorPid === child.pid, 'MAC_FIXTURE_SUPERVISOR_EVENT');
        if (event.event === 'validating') {
          requireProof(preAnchor && !preAnchorReady && event.phase === 'held-validation-before-anchor' && event.childLaunches === 0, 'MAC_FIXTURE_PRE_ANCHOR_EVENT'); preAnchorReady = true;
          if (!triggerSent && operation === 'pre-anchor-disconnect') { triggerSent = true; requireProof(child.connected, 'MAC_FIXTURE_IPC_ERROR'); child.disconnect(); }
          if (!triggerSent && operation === 'pre-anchor-signal') { triggerSent = true; requireProof(child.exitCode === null && child.signalCode === null && child.kill('SIGTERM'), 'MAC_FIXTURE_PARENT_SIGNAL'); }
        }
        if (event.event === 'ready') {
          requireProof(!ready && event.child?.pgid === child.pid && integerPid(event.child.pid) && event.helperSha256 === context.helper.sha256, 'MAC_FIXTURE_SUPERVISOR_READY'); ready = true;
          if (!triggerSent && operation === 'fixed-failure') { triggerSent = true; child.send({ schemaVersion: PROTOCOL, nonce, action: 'fail' }, error => { if (error) { protocolFailure ||= 'MAC_FIXTURE_IPC_ERROR'; requestDisposal(); } }); }
          if (!triggerSent && operation === 'disconnect') { triggerSent = true; child.disconnect(); }
        }
      } catch { protocolFailure ||= 'MAC_FIXTURE_SUPERVISOR_EVENT'; requestDisposal(); }
    }
  });
  child.send({ schemaVersion: PROTOCOL, nonce, operation, fixture: context.directory, fixtureIdentity: context.fixtureIdentity,
    helper: context.helper, testBinary: operation === 'owned-case' ? context.testBinary : null, tools: context.tools, sourcesSha256: context.sourcesSha256 }, error => { if (error) { protocolFailure ||= 'MAC_FIXTURE_IPC_ERROR'; requestDisposal(); } });
  const value = await captured;
  clearInterval(monitor); for (const signal of signals) process.removeListener(signal, signalHandler);
  let events = [];
  try { events = value.stdout.trim().split(/\r?\n/).filter(Boolean).map(line => JSON.parse(line)); requireProof(!pending.trim() && !decoder.end(), 'MAC_FIXTURE_SUPERVISOR_OUTPUT'); }
  catch { protocolFailure ||= 'MAC_FIXTURE_SUPERVISOR_EVENT'; }
  const report = { operation, supervisorPid: child.pid, nonce, preAnchorCancellation: preAnchor, events, stdout: value.stdout, close: { exitCode: value.exitCode, signal: value.signal, closed: value.closed, error: value.error }, stderr: value.stderr, protocolFailure, groupAbsence: [] };
  await retain(report);
  requireProof(!protocolFailure && value.closed && !value.error && !value.stderr.trim() && (preAnchor ? value.exitCode === 2 && value.signal === null : value.exitCode === null && value.signal === 'SIGKILL'), 'MAC_FIXTURE_SUPERVISOR_DISPOSAL');
  const expectedEvents = preAnchor ? 'validating|refused-before-anchor' : operation === 'owned-case' ? 'barrier|result|disposing' : 'barrier|ready|disposing';
  requireProof(events.every(event => event.schemaVersion === PROTOCOL && event.nonce === nonce && event.supervisorPid === child.pid) && events.map(event => event.event).join('|') === expectedEvents, 'MAC_FIXTURE_SUPERVISOR_EVENT');
  if (preAnchor) {
    const expectedReason = { 'pre-anchor-deadline': 'pre-anchor-deadline', 'pre-anchor-disconnect': 'parent-disconnect', 'pre-anchor-signal': 'supervisor-signal' }[operation];
    requireProof(preAnchorReady && events[0].childLaunches === 0 && events[1].childLaunches === 0 && events[1].reason === expectedReason, 'MAC_FIXTURE_PRE_ANCHOR_DISPOSAL');
  } else {
    const barrier = events[0];
    requireProof(integerPid(barrier.child?.pid) && barrier.child.pgid === child.pid && barrier.child.state.startsWith('T') && barrier.shellSha256 === context.tools.sh.sha256 && barrier.programSha256 === sha(START_BARRIER) && barrier.executableSha256 === (operation === 'owned-case' ? context.testBinary.sha256 : context.helper.sha256), 'MAC_FIXTURE_SUPERVISOR_BARRIER');
    const disposing = events.filter(event => event.event === 'disposing'), expectedReason = { 'fixed-failure': 'fixed-failure', deadline: 'fixture-timeout', disconnect: 'parent-disconnect', 'owned-case': 'normal-result' }[operation];
    requireProof(disposing.length === 1 && disposing[0].reason === expectedReason && disposing[0].group === child.pid && (operation === 'owned-case' || ready), 'MAC_FIXTURE_SUPERVISOR_DISPOSAL_INTENT');
  }
  // Observe only. Never signal this numeric group after its anchor has exited.
  let absent = false; const absenceDeadline = Date.now() + 6000;
  while (Date.now() < absenceDeadline) { const rows = await psRows('-g', child.pid, child.pid, environment); report.groupAbsence.push(rows); if (!rows.length) { absent = true; break; } await delay(100); }
  await retain(report);
  requireProof(absent, 'MAC_FIXTURE_GROUP_SURVIVORS');
  if (operation === 'owned-case') { const results = events.filter(event => event.event === 'result'); requireProof(results.length === 1 && results[0].value?.exitCode === 0 && !results[0].value.error && results[0].value.closed && results[0].value.signal === null, 'MAC_FIXTURE_OWNED_CASE_RESULT'); nativeTestResult(results[0].value.stdout, 1, 10); return results[0].value.stdout; }
  return report;
}

async function macDocEvidence(stdout) {
  const blocks = [];
  for (const [file, item] of docItems) {
    const source = `crates/${PACKAGE}/src/${file}`, lines = (await readFile(ownedArtifactPath(ROOT, source), 'utf8')).split(/\r?\n/);
    const matches = [];
    for (let index = 0; index < lines.length; index++) if (/^\/\/\/ ```compile_fail\s*$/.test(lines[index])) { const begin = index + 1; let end = index + 1; while (end < lines.length && !/^\/\/\/ ```\s*$/.test(lines[end])) end++; requireProof(end < lines.length, 'MAC_FIXTURE_DOC_BLOCK'); if (lines.slice(index + 1, end).join('\n').includes(`::${item.split('::')[1]};`)) matches.push({ source, item, begin, end: end + 1 }); }
    requireProof(matches.length === 1, 'MAC_FIXTURE_DOC_BLOCK'); blocks.push(matches[0]);
  }
  const rows = stdout.split(/\r?\n/).filter(line => line.startsWith('test ') && !line.startsWith('test result:'));
  requireProof(rows.length === blocks.length, 'MAC_FIXTURE_DOC_INVENTORY');
  const seen = new Set();
  for (const row of rows) {
    const match = /^test (.+\.rs) - ([A-Za-z0-9_:]+) \(line ([1-9][0-9]*)\) - compile fail \.\.\. ok$/.exec(row);
    requireProof(match, 'MAC_FIXTURE_DOC_RESULT');
    const reported = match[1].startsWith('src/') ? `crates/${PACKAGE}/${match[1]}` : match[1];
    const block = blocks.find(block => block.source === reported && block.item === match[2] && Number(match[3]) >= block.begin && Number(match[3]) <= block.end);
    requireProof(block && !seen.has(block.item), 'MAC_FIXTURE_DOC_ORIGIN'); seen.add(block.item); block.observedLine = Number(match[3]);
  }
  nativeTestResult(stdout, 5); return blocks;
}

async function main() {
  const observation = { schemaVersion: 'bridge-macos-platform-fixtures/v1', result: 'failed', startedAt: new Date().toISOString(),
    host: { id: 'macos-arm64-native', platform: process.platform, architecture: process.arch, node: process.version }, checks: [], binaries: [], executions: [], supervisor: [],
    excludedNativeCases: [EXCLUDED], packageAcceptance: false, nativeRuntimeQualified: false, releaseQualified: false, nativeFixtureSubsetExecuted: false };
  let directory, directoryHandle, helperHandle;
  const environment = childEnvironment(process.env);
  async function save(name, value) { if (directory) await writeFile(ownedArtifactPath(ROOT, `${relative(directory)}/${name}.json`, 'file', { allowMissing: true }), `${JSON.stringify(value, null, 2)}\n`, { mode: 0o600 }); }
  async function run(id, executable, argv, timeout = 180000, options = {}) {
    const result = await command(executable, argv, environment, timeout, options);
    const check = { id, ...macFixtureCommandRouting(executable, options.argv0), argv, cwd: ROOT, ...result };
    if (options.privateOutput) for (const key of ['stdout', 'stderr']) check[key] = { bytes: Buffer.byteLength(result[key]), sha256: sha(result[key]), retained: false };
    observation.checks.push(check); await save(id, check);
    requireProof(result.closed && result.exitCode === 0 && result.signal === null && !result.error, 'MAC_FIXTURE_COMMAND_FAILED'); return result.stdout;
  }
  try {
    macFixtureAdmission({ argv: process.argv, platform: process.platform, architecture: process.arch, version: process.version, execArgv: process.execArgv, environment: process.env });
    requireProof(realpathSync(process.cwd()) === realpathSync(ROOT) && realpathSync(process.env.GITHUB_WORKSPACE) === realpathSync(ROOT), 'MAC_FIXTURE_CWD');
    requireProof(process.getuid() !== 0 && process.getuid() === process.geteuid(), 'MAC_FIXTURE_ORDINARY_USER');
    const pins = JSON.parse(await readFile(ownedArtifactPath(ROOT, 'dependencies/next-toolchain.json'), 'utf8'));
    requireProof(pins.node === '24.14.1' && pins.rust === '1.99.0', 'MAC_FIXTURE_TRACKED_PINS');
    const before = fingerprintInputRecords(ROOT, macFixtureInputs); observation.source = { before, beforeSha256: sha(JSON.stringify(before)) };
    const rust = rustContext({ root: ROOT, environment }); requireProof(rust.pin === '1.99.0' && rust.hostTarget === TARGET, 'MAC_FIXTURE_RUST_CONTEXT');
    for (const key of ['RUSTFLAGS', 'CARGO_ENCODED_RUSTFLAGS', 'RUSTDOCFLAGS', 'RUSTFMT', 'CLIPPY_DRIVER_PATH']) requireProof(!process.env[key]?.trim(), 'MAC_FIXTURE_TOOL_OVERRIDE');
    Object.assign(environment, rust.env);
    const tools = { node: await observeFile(process.execPath), script: await observeFile(SELF) };
    for (const [name, selected] of Object.entries(SYSTEM)) tools[name] = await observeFile(selected);
    const git = await resolveTool('git', environment), rustup = await resolveTool('rustup', environment); tools.git = await observeFile(git); tools.rustup = await observeFile(rustup);
    const sourceHead = (await run('source-head-before', git, ['--no-optional-locks', 'rev-parse', 'HEAD'])).trim();
    requireProof(sourceHead === process.env.GITHUB_SHA && /^[a-f0-9]{40}$/.test(sourceHead), 'MAC_FIXTURE_SOURCE_HEAD');
    observation.source.head = sourceHead; observation.job = { repository: process.env.GITHUB_REPOSITORY, sha: process.env.GITHUB_SHA, runId: process.env.GITHUB_RUN_ID, runAttempt: process.env.GITHUB_RUN_ATTEMPT, job: process.env.GITHUB_JOB, environment: 'github-hosted', identityAuthenticated: false };
    observation.host = { ...observation.host, kernel: os.release(), os: os.type(), realUid: process.getuid(), effectiveUid: process.geteuid() };
    const category = 'artifacts/next/macos-platform-fixtures'; ownedArtifactPath(ROOT, 'artifacts/next', 'directory');
    const categoryPath = ownedArtifactPath(ROOT, category, 'directory', { allowMissing: true }); try { await mkdir(categoryPath, { mode: 0o700 }); } catch (error) { requireProof(error.code === 'EEXIST', 'MAC_FIXTURE_ARTIFACT_ROOT'); }
    ownedArtifactPath(ROOT, category, 'directory'); directory = ownedArtifactPath(ROOT, `${category}/${randomUUID()}`, 'directory', { allowMissing: true });
    await mkdir(directory, { mode: 0o700 }); directoryHandle = await open(directory, constants.O_RDONLY | (constants.O_NOFOLLOW || 0));
    const fixtureIdentity = statIdentity(await directoryHandle.stat({ bigint: true })); await rootFence(directory, directoryHandle, fixtureIdentity);
    await run('clear-fixture-acl', SYSTEM.chmod, ['-N', directory]);
    const acl = macAclAbsent(await run('fixture-acl', SYSTEM.ls, ['-lde', directory], 10000, { privateOutput: true }), 'directory'); await rootFence(directory, directoryHandle, fixtureIdentity);
    observation.fixture = { physicalPath: directory, identity: fixtureIdentity, acl, retained: true, namespaceExclusion: false };
    for (const name of ['cargo', 'rustc', 'rustdoc', 'rustfmt', 'cargo-fmt', 'cargo-clippy', 'clippy-driver']) {
      const resolved = (await run(`resolve-${name}`, rustup, ['which', '--toolchain', rust.pin, name], 180000, { argv0: 'rustup' })).trim(); requireProof(path.isAbsolute(resolved) && !resolved.includes('\n'), 'MAC_FIXTURE_TOOL_ROUTE'); tools[name] = await observeFile(resolved);
    }
    tools.cargoShim = await observeFile(await resolveTool(rust.cargo, environment)); tools.rustcShim = await observeFile(await resolveTool(rust.rustc, environment)); tools.rustdocShim = await observeFile(await resolveTool(rust.env.RUSTDOC, environment));
    Object.assign(environment, { RUSTC: tools.rustc.physicalPath, RUSTDOC: tools.rustdoc.physicalPath, RUSTFMT: tools.rustfmt.physicalPath, PATH: `${path.dirname(tools.cargo.physicalPath)}${path.delimiter}${environment.PATH || ''}` });
    const rustVersion = await run('rustc-version', tools.rustc.physicalPath, ['-vV']); requireProof(/^release: 1\.99\.0$/m.test(rustVersion) && /^host: aarch64-apple-darwin$/m.test(rustVersion), 'MAC_FIXTURE_COMPILER_HOST');
    const cargoVersion = (await run('cargo-version', tools.cargo.physicalPath, ['--version'])).trim(), rustdocVersion = (await run('rustdoc-version', tools.rustdoc.physicalPath, ['--version'])).trim();
    requireProof(/^cargo 1\.99\.0\b/.test(cargoVersion) && /^rustdoc 1\.99\.0\b/.test(rustdocVersion), 'MAC_FIXTURE_TOOL_VERSION'); observation.tools = { before: tools, versions: { rustc: rustVersion.trim(), cargo: cargoVersion, rustdoc: rustdocVersion } };
    const diskInfo = await run('selected-volume', SYSTEM.diskutil, ['info', '-plist', directory], 15000, { privateOutput: true });
    macApfsPlistJson(await run('parse-selected-volume', SYSTEM.plutil, ['-convert', 'json', '-o', '-', '-'], 15000, { input: diskInfo, privateOutput: true }));
    const casePath = path.join(directory, 'CaseProbe'), caseFile = await open(casePath, 'wx', 0o600); await caseFile.writeFile('bridge-br07-case-v1\n'); await caseFile.sync(); const caseIdentity = statIdentity(await caseFile.stat({ bigint: true })); await caseFile.close();
    let caseSensitive; try { const alternate = await lstat(path.join(directory, 'caseprobe'), { bigint: true }); requireProof(String(alternate.dev) === caseIdentity.dev && String(alternate.ino) === caseIdentity.ino && !alternate.isSymbolicLink(), 'MAC_FIXTURE_CASE_UNKNOWN'); caseSensitive = false; } catch (error) { if (error.code !== 'ENOENT') throw error; caseSensitive = true; }
    observation.volume = { filesystemType: 'apfs', dev: fixtureIdentity.dev, caseSensitive, caseProbeIdentity: caseIdentity, coversBothCaseModes: false }; await rootFence(directory, directoryHandle, fixtureIdentity);
    const cargo = (id, argv, timeout = 180000) => run(id, tools.cargo.physicalPath, argv, timeout);
    await cargo('format', ['fmt', '-p', PACKAGE, '--', '--check']); await cargo('clippy', ['clippy', '--locked', '-p', PACKAGE, '--all-targets', '--', '-D', 'warnings']);
    observation.compileFailDocs = await macDocEvidence(await cargo('ownership-docs', ['test', '--locked', '-p', PACKAGE, '--doc']));
    async function compile(name, kind, source, selection, test) {
      const verb = test ? 'test' : 'build', args = [verb, '--locked', '-p', PACKAGE, ...selection, ...(test ? ['--no-run'] : []), '--message-format', 'json'];
      const artifact = macCompilerArtifact(await cargo(`compile-${name}`, args), { name, kind, source, test });
      const binary = { ...await observeFile(artifact.executable, { native: true }), target: name, compilerArtifact: artifact }; observation.binaries.push(binary); return binary;
    }
    const library = await compile('bridge_platform_macos', 'lib', `crates/${PACKAGE}/src/lib.rs`, ['--lib'], true);
    const format = await compile('format', 'test', `crates/${PACKAGE}/tests/format.rs`, ['--test', 'format'], true);
    const native = await compile('native', 'test', `crates/${PACKAGE}/tests/native.rs`, ['--test', 'native'], true);
    const helperSource = await compile('native_fixture_child', 'example', `crates/${PACKAGE}/examples/native_fixture_child.rs`, ['--example', 'native_fixture_child'], false);
    const helperPath = path.join(directory, 'native_fixture_child'); helperHandle = await open(helperPath, 'wx+', 0o700);
    await copyBoundHelper(helperSource, helperHandle);
    const helper = await observeFile(helperPath, { native: true, privateFile: true, retained: helperHandle }); requireProof(helper.sha256 === helperSource.sha256, 'MAC_FIXTURE_HELPER_COPY');
    helper.acl = macAclAbsent(await run('helper-acl', SYSTEM.ls, ['-lde', helperPath], 10000, { privateOutput: true }), 'file'); observation.helper = { source: helperSource, copy: helper, protocol: 'bridge-native-child-v1', stdinExitCommand: 'q', rawBytesLogged: false };
    const nativeEnvironment = { ...environment, BRIDGE_MACOS_NATIVE_FIXTURE_ROOT: directory, BRIDGE_MACOS_OWNED_CHILD: helper.physicalPath, BRIDGE_MACOS_OWNED_CHILD_SHA256: helper.sha256 };
    async function testRun(id, binary, argv, passed, filtered = 0) { await assertFileFence(binary, { native: true }); const value = await command(binary.physicalPath, argv, nativeEnvironment, 30000); const check = { id, executable: binary.physicalPath, argv, cwd: ROOT, ...value }; observation.checks.push(check); await save(id, check); requireProof(value.closed && !value.error && value.exitCode === 0 && value.signal === null, 'MAC_FIXTURE_TEST_FAILED'); if (passed !== null) { nativeTestResult(value.stdout, passed, filtered); observation.executions.push({ id, binarySha256: binary.sha256, argv, passed, filtered }); } return value.stdout; }
    observation.inventories = {
      library: nativeTestInventory(await testRun('list-library', library, ['--list'], null), [...macControlledNames, ...macProviderNames]),
      format: nativeTestInventory(await testRun('list-format', format, ['--list'], null), macFormatNames),
      native: nativeTestInventory(await testRun('list-native', native, ['--list'], null), [...macNativeNames, EXCLUDED])
    };
    await testRun('execute-library-controlled', library, ['--test-threads=1', ...macProviderNames.flatMap(name => ['--skip', name])], 9, 2);
    await testRun('execute-format', format, ['--test-threads=1'], 16);
    for (const name of macProviderNames) await testRun(`execute-${name.replaceAll('::', '-')}`, library, [name, '--ignored', '--exact', '--test-threads=1'], 1, 10);
    const context = { directory, fixtureIdentity, helper, helperHandle, testBinary: native, tools, environment, sourcesSha256: observation.source.beforeSha256 };
    for (const operation of [...PRE_ANCHOR_CASES, 'fixed-failure', 'deadline', 'disconnect']) await runSupervised(operation, context, async report => { const index = observation.supervisor.findIndex(item => item.operation === operation); if (index < 0) observation.supervisor.push(report); else observation.supervisor[index] = report; await save(`supervisor-${operation}`, report); });
    for (const name of macNativeNames) {
      if (name === OWNED_CASE) { await runSupervised('owned-case', context, async report => { const index = observation.supervisor.findIndex(item => item.operation === 'owned-case'); if (index < 0) observation.supervisor.push(report); else observation.supervisor[index] = report; await save('supervisor-owned-case', report); }); observation.executions.push({ id: OWNED_CASE, binarySha256: native.sha256, names: [OWNED_CASE], passed: 1, filtered: 10, cleanup: 'anchored-group-observed-absent' }); }
      else await testRun(`execute-${name}`, native, [name, '--ignored', '--exact', '--test-threads=1'], 1, 10);
    }
    await rootFence(directory, directoryHandle, fixtureIdentity); macAclAbsent(await run('fixture-acl-after', SYSTEM.ls, ['-lde', directory], 10000, { privateOutput: true }), 'directory'); await assertFileFence(helper, { native: true, privateFile: true, retained: helperHandle });
    for (const binary of observation.binaries) await assertFileFence(binary, { native: true });
    observation.tools.after = {}; for (const [name, tool] of Object.entries(tools)) observation.tools.after[name] = await assertFileFence(tool);
    const after = fingerprintInputRecords(ROOT, macFixtureInputs); assert.deepEqual(after, before, 'Fixture source changed'); observation.source.after = after; observation.source.afterSha256 = sha(JSON.stringify(after));
    requireProof((await run('source-head-after', git, ['--no-optional-locks', 'rev-parse', 'HEAD'])).trim() === sourceHead, 'MAC_FIXTURE_SOURCE_CHANGED');
    observation.result = 'passed'; observation.nativeFixtureSubsetExecuted = true; observation.counts = { defaultControlled: 25, selectedNative: 12, compileFailDocBlocks: 5, supervisorDisposalSelfChecks: 3, supervisorPreAnchorCancellationChecks: 3 };
  } catch (error) { observation.failure = { code: typeof error.code === 'string' && /^[A-Z0-9_]{1,128}$/.test(error.code) ? error.code : 'MAC_FIXTURE_REFUSED' }; }
  finally { observation.completedAt = new Date().toISOString(); observation.boundary = 'Native fixture subset only. No Keychain execution, installed game, account/signing setup, production journal/actor custody, suspend, both-volume case coverage, universal crash cleanup, hardware durability, full br-07 or release acceptance.'; await save('macos-platform-fixtures', observation); await helperHandle?.close(); await directoryHandle?.close(); }
  console.log(JSON.stringify({ result: observation.result, code: observation.failure?.code, receipt: directory ? path.join(directory, 'macos-platform-fixtures.json') : null, counts: observation.counts, packageAcceptance: false, nativeFixtureSubsetExecuted: observation.nativeFixtureSubsetExecuted, nativeRuntimeQualified: false, releaseQualified: false }));
  if (observation.result !== 'passed') process.exitCode = 2;
}

if (process.argv[1] && path.resolve(process.argv[1]) === SELF) {
  if (typeof process.send === 'function' && process.channel) await supervisor();
  else await main();
}
