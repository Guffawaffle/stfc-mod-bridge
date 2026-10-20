import { createHash } from 'node:crypto';
import { readFileSync } from 'node:fs';
import { expect, test, vi } from 'vitest';
import { BridgeClient, type ClientClock, type ClientOptions } from '../src/client/client';
import { cancellationMatches, diagnosticPreviewDigest, diagnosticPreviewMatches } from '../src/client/relations';
import type { RawTransport } from '../src/client/transport';
import { canonicalData, decodeReply, decodeRequest } from '../src/client/wire';
import type { BridgeApplicationBinding, CancelDisposition, CancelOperationInput, DiagnosticContent, DiagnosticFact, DiagnosticPreview, DiagnosticPreviewInput } from '../src/generated/protocol';

const fixtures = new URL('../../contracts/fixtures/', import.meta.url);
const raw = (name: string) => readFileSync(new URL(name + '.json', fixtures), 'utf8');
const clone = <T>(value: T): T => JSON.parse(JSON.stringify(value));
const id = (n: number) => n.toString(16).padStart(8, '0') + '-2222-4222-8222-222222222222';
const diagnosticNames = ['sc16-redacted-preview', 'sc16-explicit-path-disclosure'];
const cancellationNames = ['sc15-cancel-requested', 'sc15-cancel-before-commit', 'sc15-cancel-too-late', 'sc15-cancel-already-terminal', 'sc15-cancel-recovery-required'];
type DiagnosticPair = { input: DiagnosticPreviewInput; output: DiagnosticPreview };
type CancellationPair = { input: CancelOperationInput; output: CancelDisposition };

function diagnostic(name = diagnosticNames[0]): DiagnosticPair {
  const request = decodeRequest(raw(name + '-request')), reply = decodeReply(raw(name + '-reply'));
  if (request.body.type !== 'query' || request.body.query.name !== 'diagnostic_preview' || reply.body.type !== 'result'
    || reply.body.result.type !== 'query' || reply.body.result.query.name !== 'diagnostic_preview') throw new Error('fixture_method');
  return { input: clone(request.body.query.input) as DiagnosticPreviewInput, output: clone(reply.body.result.query.output) as DiagnosticPreview };
}
function cancellation(name = cancellationNames[0]): CancellationPair {
  const request = decodeRequest(raw(name + '-request')), reply = decodeReply(raw(name + '-reply'));
  if (request.body.type !== 'command' || request.body.command.name !== 'cancel_operation' || reply.body.type !== 'result'
    || reply.body.result.type !== 'command' || reply.body.result.command.name !== 'cancel_operation') throw new Error('fixture_method');
  return { input: clone(request.body.command.input) as CancelOperationInput, output: clone(reply.body.result.command.output) as CancelDisposition };
}
class TestClock implements ClientClock {
  private tasks = new Set<() => void>();
  schedule(_delay: number, callback: () => void): () => void { this.tasks.add(callback); return () => { this.tasks.delete(callback); }; }
  expire(): void { for (const task of [...this.tasks]) task(); }
  get pending(): number { return this.tasks.size; }
}
function harness(exchange: RawTransport['exchange'], options: Partial<ClientOptions> = {}) {
  let sequence = 100;
  const clock = new TestClock();
  const transport: RawTransport = { exchange, subscribe: () => () => {} };
  return { client: new BridgeClient(transport, { requestId: () => id(sequence++), clock, ...options }), clock };
}
function deferred<T>() {
  let resolve!: (value: T) => void;
  const promise = new Promise<T>(yes => { resolve = yes; });
  return { promise, resolve };
}
function diagnosticFrame(value: DiagnosticPair): string {
  const request = JSON.parse(raw(diagnosticNames[0] + '-request'));
  request.body.query.input = value.input;
  const reply = JSON.parse(raw(diagnosticNames[0] + '-reply'));
  reply.body.result.query.output = value.output;
  decodeRequest(JSON.stringify(request)); decodeReply(JSON.stringify(reply));
  return JSON.stringify(reply);
}
function cancellationFrame(value: CancellationPair): string {
  const request = JSON.parse(raw(cancellationNames[0] + '-request'));
  request.body.command.input = value.input;
  const reply = JSON.parse(raw(cancellationNames[0] + '-reply'));
  reply.body.result.command.output = value.output;
  decodeRequest(JSON.stringify(request)); decodeReply(JSON.stringify(reply));
  return JSON.stringify(reply);
}
function correlated(reply: string, request: string): string {
  return JSON.stringify({ ...JSON.parse(reply), requestId: decodeRequest(request).requestId });
}
async function diagnosticCall(value: DiagnosticPair) {
  const frame = diagnosticFrame(value), { client, clock } = harness(async request => correlated(frame, request));
  try {
    const outcome = await client.query('diagnostic_preview', value.input);
    expect(client.pendingCount).toBe(0); expect(clock.pending).toBe(0);
    return outcome;
  } finally { client.dispose(); }
}
async function cancellationCall(value: CancellationPair) {
  const frame = cancellationFrame(value), { client, clock } = harness(async request => correlated(frame, request));
  try {
    const outcome = await client.command('cancel_operation', value.input);
    expect(client.pendingCount).toBe(0); expect(clock.pending).toBe(0);
    return outcome;
  } finally { client.dispose(); }
}

// Independent Node oracle for canonical JSON and the Rust domain separator.
// It does not erase nulls or sort facts; None controls use known Option slots
// and the unchanged Rust-generated golden digest directly.
function oracleCanonical(value: unknown): string {
  if (Array.isArray(value)) return '[' + value.map(oracleCanonical).join(',') + ']';
  if (value !== null && typeof value === 'object') {
    const object = value as Record<string, unknown>;
    return '{' + Object.keys(object).sort().map(key => JSON.stringify(key) + ':' + oracleCanonical(object[key])).join(',') + '}';
  }
  const encoded = JSON.stringify(value);
  if (typeof encoded !== 'string') throw new Error('non_json_oracle_input');
  return encoded;
}
function oracleDigest(content: DiagnosticContent, prefix = 'bridge-diagnostic-preview-json-v1\0'): string {
  return 'sha256:' + createHash('sha256').update(prefix, 'utf8').update(oracleCanonical(content), 'utf8').digest('hex');
}
function rehash(value: DiagnosticPair): void { value.output.reference.digest = oracleDigest(value.output.content); }
function targetFact(value: DiagnosticPair) {
  const fact = value.output.content.facts.find(fact => fact.kind === 'target');
  if (!fact || fact.kind !== 'target') throw new Error('fixture_target');
  return fact;
}
function richer(): DiagnosticPair {
  const value = diagnostic();
  const focus = decodeReply(raw('sc-04-focus-exact-session-prepare-reply'));
  const runtime = decodeReply(raw('sc11-runtime-update-reply'));
  const observed = decodeReply(raw('sc11-check-runtime-release-reply'));
  if (focus.body.type !== 'result' || focus.body.result.type !== 'command' || focus.body.result.command.name !== 'prepare'
    || focus.body.result.command.output.semantics.capture.kind !== 'focus_session'
    || runtime.body.type !== 'result' || runtime.body.result.type !== 'command' || runtime.body.result.command.name !== 'prepare'
    || runtime.body.result.command.output.semantics.capture.kind !== 'runtime_update'
    || observed.body.type !== 'result' || observed.body.result.type !== 'query' || observed.body.result.query.name !== 'check_runtime_release'
    || observed.body.result.query.output.status !== 'observed') throw new Error('fixture_fact');
  const evidence = clone(observed.body.result.query.output.evidence);
  value.output.content.facts.push({ kind: 'session', value: { status: 'observed', evidence, value: clone(focus.body.result.command.output.semantics.capture.session) } });
  value.output.content.facts.push({ kind: 'runtime', value: { status: 'observed', evidence: clone(evidence), value: clone(runtime.body.result.command.output.semantics.capture.input.expectedOwnership) } } as DiagnosticFact);
  rehash(value);
  return value;
}
function withBridge(): DiagnosticPair {
  const value = diagnostic(), reply = decodeReply(raw('sc13-check-bridge-update-reply'));
  if (reply.body.type !== 'result' || reply.body.result.type !== 'query' || reply.body.result.query.name !== 'check_bridge_update'
    || reply.body.result.query.output.status !== 'observed') throw new Error('fixture_bridge');
  const observed = reply.body.result.query.output;
  value.output.content.facts.push({ kind: 'bridge', value: { status: 'observed', evidence: clone(observed.evidence), value: clone(observed.value.current) } } as DiagnosticFact);
  rehash(value);
  return value;
}
function bridge(value: DiagnosticPair): BridgeApplicationBinding {
  const fact = value.output.content.facts.find(fact => fact.kind === 'bridge');
  if (!fact || fact.kind !== 'bridge' || fact.value.status !== 'observed') throw new Error('fixture_bridge');
  return fact.value.value;
}

test.each(diagnosticNames)('actual Rust diagnostic golden digest and client reply agree: %s', async name => {
  const value = diagnostic(name), before = canonicalData(value);
  expect(oracleDigest(value.output.content)).toBe(value.output.reference.digest);
  expect(await diagnosticPreviewDigest(value.output.content)).toBe(value.output.reference.digest);
  expect(diagnosticPreviewMatches(value.input, value.output)).toBe(true);
  const outcome = await diagnosticCall(value);
  expect(outcome.kind).toBe('result');
  if (outcome.kind === 'result') expect(Object.isFrozen(outcome.value.content)).toBe(true);
  expect(canonicalData(value)).toBe(before);
});

test.each(['prefix_missing_nul', 'wrong_domain', 'content_only', 'wrong_digest'])('refuses a shape-valid wrong diagnostic digest profile: %s', async variant => {
  const value = diagnostic();
  const prefix = variant === 'prefix_missing_nul' ? 'bridge-diagnostic-preview-json-v1'
    : variant === 'wrong_domain' ? 'bridge-plan-semantic-json-v1\0' : '';
  value.output.reference.digest = variant === 'wrong_digest' ? 'sha256:' + 'f'.repeat(64) : oracleDigest(value.output.content, prefix);
  expect(await diagnosticCall(value)).toMatchObject({ kind: 'fault', fault: { code: 'correlation', delivery: 'may_have_reached_backend' } });
});

test.each(['content_paths', 'path_profile', 'target_profile', 'fact_profile', 'reference_profile', 'issue_resource', 'input_assertions'])('known Rust diagnostic Option None is equivalent: %s', async variant => {
  const value = diagnostic(variant === 'path_profile' ? diagnosticNames[1] : diagnosticNames[0]);
  if (variant === 'content_paths') value.output.content.paths = null;
  if (variant === 'path_profile') value.output.content.paths!.profile = null;
  if (variant === 'target_profile' && value.output.content.target.profile.kind === 'ordinary') value.output.content.target.profile.ordinaryId = null;
  const factProfile = targetFact(value).value.profile;
  if (variant === 'fact_profile' && factProfile.kind === 'ordinary') factProfile.ordinaryId = null;
  if (variant === 'reference_profile' && value.output.reference.target.profile.kind === 'ordinary') value.output.reference.target.profile.ordinaryId = null;
  if (variant === 'issue_resource') {
    const issue = value.output.content.facts.find(fact => fact.kind === 'issue');
    if (!issue || issue.kind !== 'issue') throw new Error('fixture_issue');
    issue.value.resource = null;
  }
  if (variant === 'input_assertions') {
    if (value.input.target.installation.kind === 'registered') value.input.target.installation.revisionAssertion = null;
    if (value.input.target.profile.kind === 'ordinary') value.input.target.profile.catalogIdAssertion = null;
  }
  // Digest remains the original Rust golden; broad JSON-null normalization is
  // neither an oracle nor a valid way to admit arbitrary diagnostic data.
  expect(await diagnosticCall(value)).toMatchObject({ kind: 'result' });
});

test.each(['registration', 'registration_revision', 'ordinary_id', 'isolated_profile', 'reference_target', 'reference_disclosure', 'content_disclosure', 'redacted_paths', 'target_fact'])('refuses diagnostic target/disclosure/fact relationship drift: %s', async variant => {
  const value = diagnostic();
  if (variant === 'registration' && value.input.target.installation.kind === 'registered') value.input.target.installation.id = 'bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb';
  if (variant === 'registration_revision' && value.input.target.installation.kind === 'registered') value.input.target.installation.revisionAssertion = 'foreign-registration-revision';
  if (variant === 'ordinary_id' && value.input.target.profile.kind === 'ordinary') value.input.target.profile.catalogIdAssertion = 'bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb';
  if (variant === 'isolated_profile') value.input.target.profile = { kind: 'isolated', id: 'bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb' };
  if (variant === 'reference_target') value.output.reference.target.installation.physicalId = 'foreign-preview-installation';
  if (variant === 'reference_disclosure') value.output.reference.disclosure = 'include_paths';
  if (variant === 'content_disclosure') value.output.content.disclosure = 'include_paths';
  if (variant === 'redacted_paths') value.output.content.paths = { installation: { platform: 'windows', value: 'C:\\Synthetic\\Private' } };
  if (variant === 'target_fact') targetFact(value).value.installation.physicalId = 'foreign-fact-installation';
  rehash(value); // A new hash cannot repair contradictory scope/disclosure.
  expect(await diagnosticCall(value)).toMatchObject({ kind: 'fault', fault: { code: 'correlation' } });
});

test('directory selector may resolve to the golden registered binding without frontend physical authority', async () => {
  const value = diagnostic();
  value.input.target.installation = { kind: 'directory', directory: { platform: 'windows', value: 'C:\\Synthetic\\Alias' } };
  expect(await diagnosticCall(value)).toMatchObject({ kind: 'result' });
});

test('in-scope session/runtime facts from actual golden DTOs cross the client', async () => {
  const value = richer();
  expect(await diagnosticCall(value)).toMatchObject({ kind: 'result' });
});

test.each(['session_installation', 'runtime_physical', 'runtime_profile'])('fresh digest cannot approve foreign observed fact scope: %s', async variant => {
  const value = richer();
  for (const fact of value.output.content.facts) {
    if (variant === 'session_installation' && fact.kind === 'session' && fact.value.status === 'observed') fact.value.value.process.installationPhysicalId = 'foreign-session-installation';
    if (fact.kind === 'runtime' && fact.value.status === 'observed' && fact.value.value.kind === 'managed') {
      if (variant === 'runtime_physical') fact.value.value.reference.target.installation.physicalId = 'foreign-runtime-installation';
      if (variant === 'runtime_profile' && fact.value.value.reference.target.profile.kind === 'ordinary') fact.value.value.reference.target.profile.ownerScope = 'foreign-runtime-owner';
    }
  }
  rehash(value);
  expect(await diagnosticCall(value)).toMatchObject({ kind: 'fault', fault: { code: 'correlation' } });
});

test.each(['fact_order', 'issue', 'evidence_time', 'session_revision', 'session_pid', 'runtime_digest', 'disclosed_path'])('diagnostic digest retains exact fact/path/evidence content: %s', async variant => {
  const value = variant === 'disclosed_path' ? diagnostic(diagnosticNames[1]) : richer();
  if (variant === 'fact_order') value.output.content.facts.reverse();
  if (variant === 'issue') {
    const issue = value.output.content.facts.find(fact => fact.kind === 'issue');
    if (!issue || issue.kind !== 'issue') throw new Error('fixture_issue');
    issue.value.code = 'stale_receipt';
  }
  if (variant === 'disclosed_path') value.output.content.paths!.installation.value = 'C:\\Synthetic\\Other';
  for (const fact of value.output.content.facts) {
    if (fact.kind === 'session' && fact.value.status === 'observed') {
      if (variant === 'evidence_time') fact.value.evidence.observedAt = '2026-10-03T11:00:01Z';
      if (variant === 'session_revision') fact.value.value.revision = 'changed-session-revision';
      if (variant === 'session_pid') fact.value.value.process.pid += 1;
    }
    if (variant === 'runtime_digest' && fact.kind === 'runtime' && fact.value.status === 'observed' && fact.value.value.kind === 'managed') fact.value.value.reference.binding.artifactDigest = 'sha256:' + 'c'.repeat(64);
  }
  expect(await diagnosticCall(value)).toMatchObject({ kind: 'fault', fault: { code: 'correlation' } });
});

test('Unicode path bytes hash with JSON escaping while the digest excludes no path content', async () => {
  const value = diagnostic(diagnosticNames[1]);
  value.output.content.paths!.installation.value = 'C:\\Synthetic\\母船 🛰️';
  rehash(value);
  expect(await diagnosticPreviewDigest(value.output.content)).toBe(value.output.reference.digest);
  expect(await diagnosticCall(value)).toMatchObject({ kind: 'result' });
});

test('actual golden Bridge binding may appear as an observed diagnostic fact', async () => {
  expect(await diagnosticCall(withBridge())).toMatchObject({ kind: 'result' });
});

test.each(['empty_payloads', 'missing_application', 'duplicate_role', 'zero_size', 'windows_arm64', 'macos_x64'])('refuses a fresh-hashed shape-valid invalid Bridge diagnostic fact: %s', async variant => {
  const value = withBridge(), application = bridge(value);
  const [app, profiles, toml, helper] = application.payloads;
  if (!app || !profiles || !toml || !helper || application.payloads.length !== 4) throw new Error('fixture_bridge_payloads');
  if (variant === 'empty_payloads') application.payloads = [];
  if (variant === 'missing_application') application.payloads = [profiles, toml, helper];
  if (variant === 'duplicate_role') application.payloads = [app, profiles, toml, helper, clone(app)];
  if (variant === 'zero_size') app.size = '0';
  if (variant === 'windows_arm64') application.architecture = 'arm64';
  if (variant === 'macos_x64') application.platform = 'macos';
  rehash(value);
  expect(await diagnosticCall(value)).toMatchObject({ kind: 'fault', fault: { code: 'correlation' } });
});

test.each(['plaintext', 'required_null'])('generated diagnostic boundary rejects arbitrary content normalization: %s', async variant => {
  const value = diagnostic();
  if (variant === 'plaintext') Object.assign(value.output.content, { plaintext: 'synthetic-diagnostic-canary' });
  else Object.assign(value.output.content, { target: null });
  const reply = JSON.parse(raw(diagnosticNames[0] + '-reply')); reply.body.result.query.output = value.output;
  const { client } = harness(async request => correlated(JSON.stringify(reply), request));
  try { expect(await client.query('diagnostic_preview', value.input)).toMatchObject({ kind: 'fault', fault: { code: 'schema' } }); }
  finally { client.dispose(); }
});

test.each(['timeout', 'observational_abort', 'disposed'])('diagnostic hashing cannot publish after %s', async abandon => {
  const digest = globalThis.crypto.subtle.digest.bind(globalThis.crypto.subtle);
  const started = deferred<void>(), release = deferred<void>();
  const spy = vi.spyOn(globalThis.crypto.subtle, 'digest').mockImplementation(async (algorithm, bytes) => { started.resolve(); await release.promise; return digest(algorithm, bytes); });
  const controller = new AbortController(), value = diagnostic(), sent: string[] = [];
  let observationSignal: AbortSignal | undefined;
  const { client, clock } = harness(async (request, options) => { sent.push(request); observationSignal = options.signal; return correlated(diagnosticFrame(value), request); });
  try {
    const pending = client.query('diagnostic_preview', value.input, { signal: controller.signal });
    await started.promise;
    expect(client.pendingCount).toBe(1);
    if (abandon === 'timeout') clock.expire();
    if (abandon === 'observational_abort') controller.abort();
    if (abandon === 'disposed') client.dispose();
    expect(await pending).toMatchObject({ kind: 'fault', fault: { code: abandon, delivery: 'may_have_reached_backend' } });
    expect(client.pendingCount).toBe(0); expect(clock.pending).toBe(0); expect(observationSignal?.aborted).toBe(true);
    release.resolve(); await spy.mock.results[0].value; await Promise.resolve();
    expect(sent).toHaveLength(1); expect(decodeRequest(sent[0]).body.type).toBe('query');
    expect(client.pendingCount).toBe(0); expect(client.replayCount).toBe(0);
  } finally { release.resolve(); spy.mockRestore(); client.dispose(); }
});

test('pending capacity remains occupied until diagnostic digest verification finishes', async () => {
  const digest = globalThis.crypto.subtle.digest.bind(globalThis.crypto.subtle), started = deferred<void>(), release = deferred<void>();
  const spy = vi.spyOn(globalThis.crypto.subtle, 'digest').mockImplementation(async (algorithm, bytes) => { started.resolve(); await release.promise; return digest(algorithm, bytes); });
  const value = diagnostic(); let sent = 0;
  const { client } = harness(async request => { sent++; return correlated(diagnosticFrame(value), request); }, { maximumPending: 1 });
  try {
    const pending = client.query('diagnostic_preview', value.input); await started.promise;
    expect(await client.query('diagnostic_preview', value.input)).toMatchObject({ kind: 'fault', fault: { code: 'pending_limit', delivery: 'not_sent' } });
    expect(sent).toBe(1); release.resolve(); expect(await pending).toMatchObject({ kind: 'result' }); expect(client.pendingCount).toBe(0);
  } finally { release.resolve(); spy.mockRestore(); client.dispose(); }
});

test('failed diagnostic hashing becomes a sanitized local fault', async () => {
  const spy = vi.spyOn(globalThis.crypto.subtle, 'digest').mockRejectedValue(new Error('synthetic-secret-canary C:/Synthetic/Private'));
  try {
    const outcome = await diagnosticCall(diagnostic());
    expect(outcome).toMatchObject({ kind: 'fault', fault: { code: 'delivery_failed', delivery: 'may_have_reached_backend' } });
    expect(JSON.stringify(outcome)).not.toMatch(/canary|Synthetic\/Private/);
  } finally { spy.mockRestore(); }
});

test('unavailable WebCrypto remains a sanitized local diagnostic fault', async () => {
  vi.stubGlobal('crypto', undefined);
  try {
    expect(await diagnosticCall(diagnostic())).toMatchObject({ kind: 'fault', fault: { code: 'delivery_failed', delivery: 'may_have_reached_backend' } });
  } finally { vi.unstubAllGlobals(); }
});

test.each(cancellationNames)('actual Rust cancellation disposition remains correlated: %s', async name => {
  const value = cancellation(name), before = canonicalData(value);
  expect(cancellationMatches(value.input, value.output)).toBe(true);
  expect(await cancellationCall(value)).toMatchObject({ kind: 'result' });
  expect(canonicalData(value)).toBe(before);
});

test.each(cancellationNames)('cancellation refuses another operation: %s', async name => {
  const value = cancellation(name); value.input.operationId = id(900);
  expect(await cancellationCall(value)).toMatchObject({ kind: 'fault', fault: { code: 'correlation', delivery: 'may_have_reached_backend' } });
});

test.each(cancellationNames)('cancellation refuses a regressed observed revision: %s', async name => {
  const value = cancellation(name); value.input.expectedOperationRevision = (BigInt(value.output.operation.operationRevision) + 1n).toString();
  expect(await cancellationCall(value)).toMatchObject({ kind: 'fault', fault: { code: 'correlation' } });
});

test.each(['embedded_operation', 'physical_target', 'profile_target'])('correct outer cancellation identity cannot approve foreign recovery custody: %s', async variant => {
  const value = cancellation('sc15-cancel-recovery-required'), state = value.output.operation.state;
  if (state.status !== 'recovery_required' || state.recovery.target.kind !== 'launch') throw new Error('fixture_recovery');
  if (variant === 'embedded_operation') state.recovery.operationId = id(901);
  if (variant === 'physical_target') state.recovery.target.target.installation.physicalId = 'foreign-recovery-installation';
  if (variant === 'profile_target' && state.recovery.target.target.profile.kind === 'ordinary') state.recovery.target.target.profile.ownerScope = 'foreign-recovery-owner';
  expect(await cancellationCall(value)).toMatchObject({ kind: 'fault', fault: { code: 'correlation' } });
});

test('recovery target binding retains Rust None equivalence to captured operation scope', async () => {
  const value = cancellation('sc15-cancel-recovery-required'), state = value.output.operation.state;
  if (state.status !== 'recovery_required' || state.recovery.target.kind !== 'launch'
    || state.recovery.target.target.profile.kind !== 'ordinary') throw new Error('fixture_recovery');
  state.recovery.target.target.profile.ordinaryId = null;
  expect(await cancellationCall(value)).toMatchObject({ kind: 'result' });
});

const incompatible = cancellationNames.flatMap(name => {
  const original = cancellation(name).output.kind;
  return cancellationNames.map(other => cancellation(other).output.kind).filter(kind => kind !== original
    && !(original === 'cancelled_before_commit' && kind === 'already_terminal')
    && !(original === 'already_terminal' && kind === 'too_late')).map(kind => ({ name, kind }));
});
test.each(incompatible)('cancellation kind $kind contradicts the observed state in $name', async ({ name, kind }) => {
  const value = cancellation(name);
  value.output = { kind, operation: value.output.operation };
  expect(await cancellationCall(value)).toMatchObject({ kind: 'fault', fault: { code: 'correlation' } });
});

test('too_late may honestly observe the already completed golden changed outcome', async () => {
  const value = cancellation('sc15-cancel-already-terminal'); value.output.kind = 'too_late';
  expect(await cancellationCall(value)).toMatchObject({ kind: 'result' });
});

test('already_terminal may honestly observe the golden cancelled-before-commit outcome', async () => {
  const value = cancellation('sc15-cancel-before-commit'); value.output.kind = 'already_terminal';
  expect(await cancellationCall(value)).toMatchObject({ kind: 'result' });
});

test('cancellation compares decimal revisions above JavaScript integer precision exactly', async () => {
  const value = cancellation(); value.input.expectedOperationRevision = '9007199254740993'; value.output.operation.operationRevision = '9007199254740992';
  expect(await cancellationCall(value)).toMatchObject({ kind: 'fault', fault: { code: 'correlation' } });
  value.output.operation.operationRevision = '9007199254740993';
  expect(await cancellationCall(value)).toMatchObject({ kind: 'result' });
});

test('maximum Rust operation revision remains a lossless string and never wraps', async () => {
  const value = cancellation(); value.input.expectedOperationRevision = '18446744073709551615'; value.output.operation.operationRevision = '18446744073709551615';
  expect(await cancellationCall(value)).toMatchObject({ kind: 'result' });
  value.output.operation.operationRevision = '0';
  expect(await cancellationCall(value)).toMatchObject({ kind: 'fault', fault: { code: 'correlation' } });
});
