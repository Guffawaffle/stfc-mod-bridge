import { expect, test, vi } from 'vitest';
import { readFileSync, readdirSync } from 'node:fs';
import { BridgeClient, type ClientClock, type ClientOutcome, type QueryOutput, type CommandOutput } from '../src/client/client';
import { canonicalData, decodeReply, decodeRequest } from '../src/client/wire';
import { UnavailableTransport, type RawFrame, type RawTransport } from '../src/client/transport';
import { bindingEquivalent, semanticPlanDigest } from '../src/client/relations';

const fixtures = new URL('../../contracts/fixtures/', import.meta.url);
const raw = (id: string) => readFileSync(new URL(id + '.json', fixtures), 'utf8');
const id = (index: number) => index.toString(16).padStart(8, '0') + '-2222-4222-8222-222222222222';
function deferred<T>() { let resolve!: (value: T) => void; let reject!: (error: unknown) => void; const promise = new Promise<T>((yes, no) => { resolve = yes; reject = no; }); return { promise, resolve, reject }; }
class TestClock implements ClientClock {
  private tasks = new Set<() => void>();
  schedule(_delayMs: number, callback: () => void): () => void { this.tasks.add(callback); return () => { this.tasks.delete(callback); }; }
  expire(): void { for (const task of [...this.tasks]) task(); }
  get pending(): number { return this.tasks.size; }
}
function harness(exchange: RawTransport['exchange'], options = {}) {
  let sequence = 100;
  const clock = new TestClock();
  const transport: RawTransport = { exchange, subscribe: () => () => {} };
  return { client: new BridgeClient(transport, { requestId: () => id(sequence++), clock, ...options }), clock };
}
const correlated = (reply: string, request: string) => JSON.stringify({ ...JSON.parse(reply), requestId: decodeRequest(request).requestId });

test('preparation refuses a schema-valid reply captured for another requested profile', async () => {
  const request = decodeRequest(raw('sc-03-profile-one-prepare-request'));
  expect(() => decodeReply(raw('sc-03-profile-two-prepare-reply'))).not.toThrow();
  if (request.body.type !== 'command' || request.body.command.name !== 'prepare') throw new Error('fixture');
  const { client } = harness(async encoded => correlated(raw('sc-03-profile-two-prepare-reply'), encoded));
  expect(await client.command('prepare', request.body.command.input)).toMatchObject({ kind: 'fault', fault: { code: 'correlation', delivery: 'may_have_reached_backend' } });
  expect(client.pendingCount).toBe(0);
});

test('first commit refuses schema-valid semantics unrelated to its captured review digest', async () => {
  const input = JSON.parse(raw('sc14-admit-request')).body.command.input;
  expect(() => decodeReply(raw('sc-03-profile-two-commit-reply'))).not.toThrow();
  let calls = 0;
  const { client } = harness(async encoded => correlated(raw(++calls === 1 ? 'sc-03-profile-two-commit-reply' : 'sc14-exact-replay-reply'), encoded));
  expect(await client.command('commit', input)).toMatchObject({ kind: 'fault', fault: { code: 'correlation', delivery: 'may_have_reached_backend' } });
  expect(client.getReplay(input.idempotencyKey)?.operationId).toBeUndefined();
  expect(client.getReplay(input.idempotencyKey)?.input).toEqual(input);
  expect(await client.replayCommit(input.idempotencyKey)).toMatchObject({ kind: 'result' });
});

const preparationMutations: [string, string, string, (input: any) => void][] = [
  ['registered installation ID', 'sc-03-profile-one-prepare-request', 'sc-03-profile-one-prepare-reply', input => { input.target.installation.id = 'e'.repeat(32); }],
  ['registered revision assertion', 'sc-03-profile-one-prepare-request', 'sc-03-profile-one-prepare-reply', input => { input.target.installation.revisionAssertion = 'other-registration-revision'; }],
  ['isolated revision assertion', 'sc-03-profile-one-prepare-request', 'sc-03-profile-one-prepare-reply', input => { input.target.profile.revisionAssertion = 'other-profile-revision'; }],
  ['ordinary catalog assertion', 'sc-02-ordinary-absent-prepare-request', 'sc-02-ordinary-absent-prepare-reply', input => { input.target.profile.catalogIdAssertion = 'e'.repeat(32); }],
  ['launch choice', 'sc-03-profile-one-prepare-request', 'sc-03-profile-one-prepare-reply', input => { input.unrecognizedRuntimeChoice = 'allow_once'; }],
  ['isolated store mode', 'sc-03-profile-one-prepare-request', 'sc-03-profile-one-prepare-reply', input => { input.storeMode = 'resume'; }],
  ['focus session ID', 'sc-04-focus-exact-session-prepare-request', 'sc-04-focus-exact-session-prepare-reply', input => { input.session.sessionId = id(999); }],
  ['focus process generation', 'sc-04-focus-exact-session-prepare-request', 'sc-04-focus-exact-session-prepare-reply', input => { input.session.process.startIdentity.value = 'other-generation'; }],
  ['new profile name', 'sc-05-create-new-prepare-request', 'sc-05-create-new-prepare-reply', input => { input.name = 'Other profile'; }],
  ['new profile catalog revision', 'sc-05-create-new-prepare-request', 'sc-05-create-new-prepare-reply', input => { input.expectedCatalogRevision = 'other-catalog'; }],
  ['new profile setup', 'sc-06-import-reviewed-owner-and-consent-prepare-request', 'sc-06-import-reviewed-owner-and-consent-prepare-reply', input => { input.setup = { kind: 'new' }; }],
  ['new profile preferred installation', 'sc-05-create-new-prepare-request', 'sc-05-create-new-prepare-reply', input => { input.preferredInstallation.id = 'e'.repeat(32); }],
  ['new profile installation revision', 'sc-05-create-new-prepare-request', 'sc-05-create-new-prepare-reply', input => { input.preferredInstallation.revisionAssertion = 'other-registration'; }],
  ['registration name', 'sc-01-register-explicit-installation-prepare-request', 'sc-01-register-explicit-installation-prepare-reply', input => { input.name = 'Other registration'; }],
  ['registration catalog revision', 'sc-01-register-explicit-installation-prepare-request', 'sc-01-register-explicit-installation-prepare-reply', input => { input.expectedCatalogRevision = 'other-catalog'; }],
  ['save draft epoch', 'sc10-save-reviewed-draft-request', 'sc10-save-reviewed-draft-reply', input => { input.draft.hostEpoch = id(999); }],
  ['save draft revision', 'sc10-save-reviewed-draft-request', 'sc10-save-reviewed-draft-reply', input => { input.draft.revision = '3'; }],
  ['save document target', 'sc10-save-reviewed-draft-request', 'sc10-save-reviewed-draft-reply', input => { input.draft.document.target.installation.physicalId = 'other-physical-installation'; }],
  ['save schema digest', 'sc10-save-reviewed-draft-request', 'sc10-save-reviewed-draft-reply', input => { input.draft.document.schema.digest = 'sha256:' + 'e'.repeat(64); }],
  ['runtime release selection', 'sc11-runtime-install-request', 'sc11-runtime-install-reply', input => { input.selectedRelease.selectionId = id(999); }],
  ['runtime configuration document', 'sc11-runtime-install-request', 'sc11-runtime-install-reply', input => { input.configuration.document.revision = 'other-document'; }],
  ['runtime captured target', 'sc11-runtime-install-request', 'sc11-runtime-install-reply', input => { input.target.installation.nativeTargetRef = 'other-native-target'; }],
];
for (const [field, requestId, replyId, mutate] of preparationMutations) {
  test(`preparation binds ${field} while accepting the exact shared pair`, async () => {
    const frame = JSON.parse(raw(requestId)); mutate(frame.body.command.input.intent.input);
    const request = decodeRequest(JSON.stringify(frame)); decodeReply(raw(replyId));
    if (request.body.type !== 'command' || request.body.command.name !== 'prepare') throw new Error('fixture');
    const { client } = harness(async encoded => correlated(raw(replyId), encoded));
    expect(await client.command('prepare', request.body.command.input)).toMatchObject({ kind: 'fault', fault: { code: 'correlation' } });
    expect(await client.command('prepare', JSON.parse(raw(requestId)).body.command.input)).toMatchObject({ kind: 'result' });
  });
}

test('preparation binds installation binding kind while accepting the exact shared pair', async () => {
  const input = JSON.parse(raw('sc-03-profile-one-prepare-request')).body.command.input;
  const frame = JSON.parse(raw('sc-03-profile-one-prepare-reply'));
  const plan = frame.body.result.command.output;
  const { registrationId: _registration, registrationRevision: _revision, ...physicalBinding } = plan.semantics.capture.target.installation;
  plan.semantics.capture.target.installation = { ...physicalBinding, kind: 'directory' };
  plan.planRef.reviewDigest = await semanticPlanDigest(plan.semantics);
  decodeReply(JSON.stringify(frame));
  let calls = 0;
  const { client } = harness(async encoded => correlated(++calls === 1 ? JSON.stringify(frame) : raw('sc-03-profile-one-prepare-reply'), encoded));
  expect(await client.command('prepare', input)).toMatchObject({ kind: 'fault', fault: { code: 'correlation' } });
  expect(await client.command('prepare', input)).toMatchObject({ kind: 'result' });
});

test('directory selector may resolve to an owner-captured registered installation', async () => {
  const frame = JSON.parse(raw('sc-03-profile-one-prepare-request'));
  const directory = JSON.parse(raw('sc-01-register-explicit-installation-prepare-request')).body.command.input.intent.input.directory;
  frame.body.command.input.intent.input.target.installation = { kind: 'directory', directory };
  const request = decodeRequest(JSON.stringify(frame));
  if (request.body.type !== 'command' || request.body.command.name !== 'prepare') throw new Error('fixture');
  decodeReply(raw('sc-03-profile-one-prepare-reply'));
  const { client } = harness(async encoded => correlated(raw('sc-03-profile-one-prepare-reply'), encoded));
  const outcome = await client.command('prepare', request.body.command.input);
  expect(outcome).toMatchObject({ kind: 'result', value: { semantics: { capture: { target: { installation: { kind: 'registered' } } } } } });
});

test('preparation leaves opaque directory physical equivalence to its native owner', async () => {
  const registration = JSON.parse(raw('sc-01-register-explicit-installation-prepare-request')).body.command.input;
  registration.intent.input.directory.value = 'C:\\Synthetic\\Alias';
  const { client } = harness(async encoded => correlated(raw('sc-01-register-explicit-installation-prepare-reply'), encoded));
  expect(await client.command('prepare', registration)).toMatchObject({ kind: 'result' });
  const launch = JSON.parse(raw('sc-02-ordinary-absent-prepare-request')).body.command.input;
  launch.intent.input.target.installation.directoryAssertion = { platform: 'windows', value: 'C:\\Synthetic\\Alias' };
  const other = harness(async encoded => correlated(raw('sc-02-ordinary-absent-prepare-reply'), encoded));
  expect(await other.client.command('prepare', launch)).toMatchObject({ kind: 'result' });
});

test('preparation treats nullable assertions and explicit default reject as Rust DTO defaults', async () => {
  const input = JSON.parse(raw('sc-02-ordinary-absent-prepare-request')).body.command.input;
  input.intent.input.unrecognizedRuntimeChoice = 'reject';
  input.intent.input.target.installation.revisionAssertion = null;
  input.intent.input.target.profile.catalogIdAssertion = null;
  const { client } = harness(async encoded => {
    const frame = JSON.parse(correlated(raw('sc-02-ordinary-absent-prepare-reply'), encoded));
    const capture = frame.body.result.command.output.semantics.capture;
    capture.unrecognizedRuntimeChoice = 'reject'; capture.target.profile.ordinaryId = null;
    decodeReply(JSON.stringify(frame)); return JSON.stringify(frame);
  });
  expect(await client.command('prepare', input)).toMatchObject({ kind: 'result' });
});

test('semantic digest matches every Rust-approved shared prepared plan', async () => {
  const index: { fixtures: { id: string; path: string; kind: string; expectedWire: boolean; expectedSemantic: boolean }[] } = JSON.parse(raw('index'));
  let verified = 0;
  for (const row of index.fixtures.filter(row => row.kind === 'reply' && row.expectedWire && row.expectedSemantic)) {
    const frame = decodeReply(readFileSync(new URL(row.path, fixtures), 'utf8'));
    if (frame.body.type !== 'result' || frame.body.result.type !== 'command' || frame.body.result.command.name !== 'prepare') continue;
    const plan = frame.body.result.command.output;
    expect(await semanticPlanDigest(plan.semantics), row.id).toBe(plan.planRef.reviewDigest); verified++;
  }
  expect(verified).toBeGreaterThan(40);
});

test('semantic normalization schema assumptions stay closed and optional', () => {
  const schema = JSON.parse(readFileSync(new URL('../../contracts/generated/reply.schema.json', import.meta.url), 'utf8'));
  const requestSchema = JSON.parse(readFileSync(new URL('../../contracts/generated/request.schema.json', import.meta.url), 'utf8'));
  const sources = new URL('../../crates/bridge-contracts/src/v1/', import.meta.url);
  const rust = readdirSync(sources).filter(file => file.endsWith('.rs')).map(file => readFileSync(new URL(file, sources), 'utf8')).join('\n');
  const inspect = (root: any, definition: string, minimumDefinitions = 100, expectNullable = true) => {
    const visited = new Set<string>(); let nullable = 0;
    const walk = (node: any, path: string, optionalProperty = false) => {
      if (node.$ref) {
        const key = node.$ref.split('/').pop();
        const visit = key + ':' + optionalProperty;
        if (visited.has(visit)) return;
        visited.add(visit); walk(root.definitions[key], key, optionalProperty); return;
      }
      if (node.type === 'null' || Array.isArray(node.type) && node.type.includes('null')) {
        expect(optionalProperty, path).toBe(true); nullable++;
        const [dto, field] = path.split('.');
        const source = rust.match(new RegExp('\\bpub\\s+(?:struct|enum)\\s+' + dto + '\\b[\\s\\S]*?(?=\\n(?:impl|pub\\s+(?:struct|enum))\\b|$)'))?.[0];
        const snake = field.replace(/[A-Z]/g, letter => '_' + letter.toLowerCase());
        expect(source, path).toBeDefined();
        expect(source, path).toMatch(new RegExp('#\\[serde\\(default,\\s*skip_serializing_if\\s*=\\s*"Option::is_none"\\)\\]\\s*(?:pub\\s+)?' + snake + ':\\s*Option'));
      }
      if (node.type === 'object' || node.properties) {
        expect(node.additionalProperties, path).toBe(false);
        expect(node.patternProperties, path).toBeUndefined();
        for (const [key, property] of Object.entries(node.properties ?? {})) {
          expect(/^[\x20-\x7e]+$/.test(key), path + '.' + key).toBe(true);
          walk(property, path + '.' + key, !(node.required ?? []).includes(key));
        }
      }
      if (node.items) walk(node.items, path + '[]', false);
      for (const union of ['oneOf', 'anyOf', 'allOf']) for (const branch of node[union] ?? []) walk(branch, path, optionalProperty);
    };
    walk(root.definitions[definition], definition);
    expect(visited.size).toBeGreaterThan(minimumDefinitions);
    if (expectNullable) expect(nullable).toBeGreaterThan(0);
  };
  inspect(schema, 'PlanSemantics'); inspect(requestSchema, 'MutationIntent');
  for (const binding of ['DocumentBinding', 'DraftRef', 'DraftSnapshot', 'PrivateValueRef', 'SecretRef', 'DiagnosticContent', 'RequestSensitiveInputInput', 'RequestExportDestinationInput', 'OperationState', 'RecoveryRef']) inspect(schema, binding, 1);
  inspect(schema, 'ResolvedTarget', 1);
  for (const binding of ['BridgeApplicationBinding', 'IsolatedProfileRef', 'OrdinaryProfileRef', 'RegisteredInstallationBinding']) inspect(schema, binding, 1, false);
});

const echoCases = [
  { method: 'open_draft' as const, request: 'sc08-open-clean-draft-request', reply: 'sc08-open-clean-draft-reply', binding: (input: any) => input.document, profile: (input: any) => input.document.target.profile, id: 'documentId' },
  { method: 'set_draft_changes' as const, request: 'sc08-stage-dirty-draft-request', reply: 'sc08-stage-dirty-draft-reply', binding: (input: any) => input.draft.document, profile: (input: any) => input.draft.document.target.profile, id: 'documentId' },
  { method: 'request_sensitive_input' as const, request: 'sc09-protected-private-entry-request', reply: 'sc09-protected-private-entry-reply', binding: (input: any) => input.draft, profile: (input: any) => input.draft.document.target.profile, id: 'draftId' },
  { method: 'request_export_destination' as const, request: 'sc16-capture-export-destination-request', reply: 'sc16-capture-export-destination-reply', binding: (input: any) => input.preview, profile: (input: any) => input.preview.target.profile, id: 'previewId' },
];
for (const row of echoCases) {
  test(`typed binding echo accepts Rust None equivalence: ${row.method}`, async () => {
    const frame = JSON.parse(raw(row.request)); row.profile(frame.body.command.input).ordinaryId = null;
    decodeRequest(JSON.stringify(frame)); decodeReply(raw(row.reply));
    const { client } = harness(async encoded => correlated(raw(row.reply), encoded));
    expect(await client.command(row.method, frame.body.command.input)).toMatchObject({ kind: 'result' });
  });
  for (const field of ['id', 'revision', 'document']) {
    test(`typed binding echo still refuses actual ${field} changes: ${row.method}`, async () => {
      const frame = JSON.parse(raw(row.request)); const input = frame.body.command.input; row.profile(input).ordinaryId = null;
      if (field === 'id') row.binding(input)[row.id] = id(999);
      if (field === 'revision') row.binding(input).revision = row.method === 'request_sensitive_input' ? '2' : 'other-revision';
      if (field === 'document') {
        if (row.method === 'request_export_destination') input.preview.target.installation.physicalId = 'other-physical';
        else (row.method === 'request_sensitive_input' ? input.draft.document : row.binding(input)).schema.digest = 'sha256:' + 'e'.repeat(64);
      }
      decodeRequest(JSON.stringify(frame)); decodeReply(raw(row.reply));
      const { client } = harness(async encoded => correlated(raw(row.reply), encoded));
      expect(await client.command(row.method, input)).toMatchObject({ kind: 'fault', fault: { code: 'correlation' } });
    });
  }
}

test('typed binding equivalence remains bounded and refuses accessor conversion', () => {
  const input = JSON.parse(raw('sc08-open-clean-draft-request')).body.command.input.document;
  let reads = 0;
  Object.defineProperty(input.target.profile, 'ordinaryId', { enumerable: true, get() { reads++; return null; } });
  expect(() => bindingEquivalent(input, input)).toThrow('invalid_capture'); expect(reads).toBe(0);
});

test('commit digest accepts only declared set timestamp and serde normalization exceptions', async () => {
  const planFrame = JSON.parse(raw('sc-03-profile-one-prepare-reply'));
  const isolated = planFrame.body.result.command.output;
  isolated.semantics.effects.reverse(); isolated.semantics.capture.unrecognizedRuntimeChoice = 'reject';
  decodeReply(JSON.stringify(planFrame));
  expect(await semanticPlanDigest(isolated.semantics)).toBe(isolated.planRef.reviewDigest);
  const preferencesFrame = JSON.parse(raw('sc17-theme-system-reply'));
  const preferences = preferencesFrame.body.result.command.output;
  preferences.semantics.capture.input.values.provider = null;
  preferences.semantics.capture.input.values.lastTarget.profile.catalogIdAssertion = null;
  decodeReply(JSON.stringify(preferencesFrame));
  expect(await semanticPlanDigest(preferences.semantics)).toBe(preferences.planRef.reviewDigest);
  const restoreFrame = JSON.parse(raw('sc10-restore-reviewed-backup-reply'));
  const restore = restoreFrame.body.result.command.output;
  restore.semantics.capture.input.backup.createdAt = '2026-10-02T23:59:59Z'; decodeReply(JSON.stringify(restoreFrame));
  expect(await semanticPlanDigest(restore.semantics)).toBe(restore.planRef.reviewDigest);
  restore.semantics.capture.input.backup.nativeBackupRef = 'other-native-backup';
  expect(await semanticPlanDigest(restore.semantics)).not.toBe(restore.planRef.reviewDigest);
  const request = JSON.parse(raw('sc-03-profile-one-commit-request')).body.command.input;
  const { client } = harness(async encoded => {
    const frame = JSON.parse(correlated(raw('sc-03-profile-one-commit-reply'), encoded));
    frame.body.result.command.output.semantics.effects.reverse();
    frame.body.result.command.output.semantics.capture.unrecognizedRuntimeChoice = 'reject';
    return JSON.stringify(frame);
  });
  expect(await client.command('commit', request)).toMatchObject({ kind: 'result' });
});

test('digest completion after timeout abort or disposal cannot admit replay identity', async () => {
  const digest = globalThis.crypto.subtle.digest.bind(globalThis.crypto.subtle);
  for (const abandon of ['timeout', 'observational_abort', 'disposed']) {
    const release = deferred<ArrayBuffer>(); const started = deferred<void>();
    const spy = vi.spyOn(globalThis.crypto.subtle, 'digest').mockImplementation(async (algorithm, input) => { started.resolve(); await release.promise; return digest(algorithm, input); });
    try {
      const controller = new AbortController(); const input = JSON.parse(raw('sc14-admit-request')).body.command.input;
      const { client, clock } = harness(async encoded => correlated(raw('sc14-admit-reply'), encoded));
      const result = client.command('commit', input, { signal: controller.signal }); await started.promise;
      if (abandon === 'timeout') clock.expire();
      if (abandon === 'observational_abort') controller.abort();
      if (abandon === 'disposed') client.dispose();
      expect(await result).toMatchObject({ kind: 'fault', fault: { code: abandon, delivery: 'may_have_reached_backend' } });
      release.resolve(new ArrayBuffer(0));
      await spy.mock.results[0].value; await Promise.resolve();
      expect(client.getReplay(input.idempotencyKey)?.operationId).toBeUndefined();
      expect(client.pendingCount).toBe(0); expect(clock.pending).toBe(0);
    } finally { spy.mockRestore(); }
  }
});

test('unavailable semantic hashing is a sanitized local fault and retains uncertain commit input', async () => {
  const spy = vi.spyOn(globalThis.crypto.subtle, 'digest').mockRejectedValue(new Error('secret-canary C:/private-native-path'));
  try {
    const input = JSON.parse(raw('sc14-admit-request')).body.command.input;
    const { client } = harness(async encoded => correlated(raw('sc14-admit-reply'), encoded));
    const outcome = await client.command('commit', input);
    expect(outcome).toMatchObject({ kind: 'fault', fault: { code: 'delivery_failed', delivery: 'may_have_reached_backend' } });
    expect(JSON.stringify(outcome)).not.toMatch(/canary|private-native-path/);
    expect(client.getReplay(input.idempotencyKey)?.input).toEqual(input);
    expect(client.getReplay(input.idempotencyKey)?.operationId).toBeUndefined();
  } finally { spy.mockRestore(); }
});

test('all accepted generated method pairs cross the same raw boundary', async () => {
  const index: { transcripts: { path: string; expected: { accepted: boolean } }[] } = JSON.parse(raw('index'));
  const pairs: { request: string; reply: string }[] = index.transcripts.filter(transcript => transcript.expected.accepted).flatMap(transcript => {
    const body: { steps: { type: string; request?: string; reply?: string }[] } = JSON.parse(readFileSync(new URL(transcript.path, fixtures), 'utf8'));
    return body.steps.filter(step => step.type === 'exchange').map(step => ({ request: step.request!, reply: step.reply! }));
  });
  const names = new Set<string>();
  for (const pair of pairs) {
    const request = decodeRequest(raw(pair.request));
    const reply = decodeReply(raw(pair.reply));
    if (reply.body.type !== 'result') continue;
    const { client } = harness(async encoded => correlated(raw(pair.reply), encoded));
    const outcome = request.body.type === 'query' ? await client.query(request.body.query.name, request.body.query.input)
      : await client.command(request.body.command.name, request.body.command.input);
    expect(outcome.kind, pair.request).toBe('result');
    if (outcome.kind === 'result') expect(Object.isFrozen(outcome.value), pair.request).toBe(true);
    names.add(request.body.type + ':' + (request.body.type === 'query' ? request.body.query.name : request.body.command.name));
    client.dispose();
  }
  const source = readFileSync(new URL('../src/generated/protocol.ts', import.meta.url), 'utf8');
  for (const family of ['Query', 'Command']) {
    const union = source.split('export type ' + family + ' =')[1].split('/**')[0];
    for (const match of union.matchAll(/name: '([^']+)'/g)) expect(names.has(family.toLowerCase() + ':' + match[1]), match[1]).toBe(true);
  }
});

test.each(['wrong_id', 'null_id', 'wrong_kind', 'wrong_method'])('rejects %s without delivering another result', async variant => {
  const { client } = harness(async request => {
    let frame = JSON.parse(correlated(raw('sc-01-hello-windows-x64-reply'), request));
    if (variant === 'wrong_id') frame.requestId = id(999);
    if (variant === 'null_id') frame = { ...JSON.parse(raw('sc18-invalid-request-null-correlation')), requestId: null };
    if (variant === 'wrong_kind') frame = JSON.parse(correlated(raw('sc14-admit-reply'), request));
    if (variant === 'wrong_method') frame = JSON.parse(correlated(raw('sc14-resnapshot-reply'), request));
    return JSON.stringify(frame);
  });
  expect(await client.query('hello', {})).toMatchObject({ kind: 'fault', fault: { code: 'correlation', delivery: 'may_have_reached_backend' } });
  expect(client.pendingCount).toBe(0);
});

test('domain rejection preserves generated error and recovery instead of creating local failure', async () => {
  const { client } = harness(async request => correlated(raw('sc14-second-submit-busy-reply'), request));
  const outcome = await client.query('hello', {});
  expect(outcome).toMatchObject({ kind: 'rejected', error: { code: 'operation_busy' } });
});

test('unavailable production binding is explicit and never chooses a mock', async () => {
  const client = new BridgeClient(new UnavailableTransport(), { requestId: () => id(1) });
  expect(await client.query('hello', {})).toMatchObject({ kind: 'fault', fault: { code: 'unavailable_binding', delivery: 'not_sent' } });
});

test('abort before send prevents dispatch; abort afterward abandons only observation', async () => {
  const response = deferred<RawFrame>(); let calls = 0; let adapterSignal: AbortSignal | undefined;
  const { client, clock } = harness((_request, options) => { calls++; adapterSignal = options.signal; return response.promise; });
  const before = new AbortController(); before.abort();
  expect(await client.query('hello', {}, { signal: before.signal })).toMatchObject({ kind: 'fault', fault: { code: 'observational_abort', delivery: 'not_sent' } });
  expect(calls).toBe(0);
  const after = new AbortController(); const pending = client.query('hello', {}, { signal: after.signal }); after.abort();
  expect(await pending).toMatchObject({ kind: 'fault', fault: { code: 'observational_abort', delivery: 'may_have_reached_backend' } });
  expect(adapterSignal?.aborted).toBe(true); expect(calls).toBe(1); expect(client.pendingCount).toBe(0); expect(clock.pending).toBe(0);
  response.resolve(raw('sc-01-hello-windows-x64-reply')); await Promise.resolve(); expect(calls).toBe(1);
});

test('timeout retains exact commit replay without cancel or a new preparation', async () => {
  const sent: string[] = []; const delayed = deferred<RawFrame>();
  const { client, clock } = harness(request => { sent.push(request); return sent.length === 1 ? delayed.promise : Promise.resolve(correlated(raw('sc14-exact-replay-reply'), request)); });
  const request = JSON.parse(raw('sc14-admit-request'));
  const original = JSON.parse(JSON.stringify(request.body.command.input));
  const waiting = client.command('commit', request.body.command.input); request.body.command.input.planRef.reviewDigest = 'mutated'; clock.expire();
  expect(await waiting).toMatchObject({ kind: 'fault', fault: { code: 'timeout', delivery: 'may_have_reached_backend' } });
  expect(client.getReplay(original.idempotencyKey)?.input).toEqual(original);
  expect(await client.replayCommit(original.idempotencyKey)).toMatchObject({ kind: 'result' });
  const captures = sent.map(decodeRequest);
  expect(captures[0].requestId).not.toBe(captures[1].requestId);
  expect(canonicalData(captures[0].body)).toBe(canonicalData(captures[1].body));
  expect(sent).toHaveLength(2);
  delayed.resolve(raw('sc14-admit-reply')); await Promise.resolve(); expect(client.pendingCount).toBe(0);
});

test('replay key conflict and replay/pending bounds refuse dispatch', async () => {
  let calls = 0;
  const { client } = harness(() => { calls++; return new Promise(() => {}); }, { maximumPending: 1, maximumReplays: 1 });
  const input = JSON.parse(raw('sc14-admit-request')).body.command.input;
  const pending = client.command('commit', input);
  expect(await client.query('hello', {})).toMatchObject({ kind: 'fault', fault: { code: 'pending_limit', delivery: 'not_sent' } });
  client.dispose(); await pending; expect(calls).toBe(1);
  const other = harness(async request => correlated(raw('sc14-admit-reply'), request), { maximumReplays: 1 });
  await other.client.command('commit', input);
  expect(await other.client.command('commit', { ...input, planRef: { ...input.planRef, planId: id(999) } })).toMatchObject({ kind: 'fault', fault: { code: 'replay_conflict' } });
  expect(await other.client.command('commit', { ...input, idempotencyKey: id(999) })).toMatchObject({ kind: 'fault', fault: { code: 'replay_limit' } });
  expect(other.client.forgetReplay(input.idempotencyKey)).toBe(true);
});

test('duplicate injected ID refuses; late first request cannot settle a later call', async () => {
  const first = deferred<RawFrame>(); const second = deferred<RawFrame>(); let calls = 0;
  const { client, clock } = harness(() => (++calls === 1 ? first.promise : second.promise));
  const waiting = client.query('hello', {}); clock.expire(); await waiting;
  const later = client.query('hello', {}); first.resolve(raw('sc-01-hello-windows-x64-reply'));
  await Promise.resolve(); expect(client.pendingCount).toBe(1);
  second.resolve(JSON.stringify({ ...JSON.parse(raw('sc-01-hello-windows-x64-reply')), requestId: id(101) }));
  expect(await later).toMatchObject({ kind: 'result' });
  const reused = new BridgeClient({ exchange: async request => correlated(raw('sc-01-hello-windows-x64-reply'), request), subscribe: () => () => {} }, { requestId: () => id(1) });
  await reused.query('hello', {});
  expect(await reused.query('hello', {})).toMatchObject({ kind: 'fault', fault: { code: 'request_id_reused' } });
});

test('adapter exceptions and malformed frames are sanitized', async () => {
  const exception = harness(async () => { throw new Error('secret-canary C:/account-path'); });
  const broken = harness(async () => '{"private-canary":true,"private-canary":false}');
  const outcomes = [await exception.client.query('hello', {}), await broken.client.query('hello', {})];
  expect(JSON.stringify(outcomes)).not.toMatch(/canary|account-path/);
  expect(outcomes[0]).toMatchObject({ kind: 'fault', fault: { code: 'delivery_failed' } });
  expect(outcomes[1]).toMatchObject({ kind: 'fault', fault: { code: 'framing' } });
});

test('validated subscriptions are bounded and malformed event stops only its observer', () => {
  let emit!: (frame: RawFrame) => void; let released = 0;
  const transport: RawTransport = { exchange: async () => '', subscribe(onEvent) { emit = onEvent; return () => { released++; }; } };
  const client = new BridgeClient(transport, { requestId: () => id(1), maximumSubscriptions: 1 });
  const faults: unknown[] = []; let received = 0;
  client.subscribe(() => { received++; }, fault => faults.push(fault));
  client.subscribe(() => {}, fault => faults.push(fault));
  expect(faults).toEqual([{ code: 'subscription_limit', delivery: 'not_sent' }]);
  emit(raw('sc14-event-one')); expect(received).toBe(1);
  emit('{"secret-canary":1,"secret-canary":2}');
  expect(faults[1]).toEqual({ code: 'framing', delivery: 'may_have_reached_backend' }); expect(released).toBe(1);
  emit(raw('sc14-event-two')); expect(received).toBe(1); client.dispose(); expect(released).toBe(1);
});

test('synchronous first event can dispose client and release the returned raw observer exactly once', () => {
  let emit!: (frame: RawFrame) => void; let released = 0; let received = 0;
  const transport: RawTransport = {
    exchange: async () => '',
    subscribe(onEvent) { emit = onEvent; onEvent(raw('sc14-event-one')); return () => { released++; }; },
  };
  const client = new BridgeClient(transport, { requestId: () => id(1) });
  const stop = client.subscribe(() => { received++; client.dispose(); });
  emit(raw('sc14-event-two')); stop(); client.dispose();
  expect(received).toBe(1); expect(released).toBe(1);
});

test('reentrant subscription cannot bypass reserved subscription capacity', () => {
  let registrations = 0; let released = 0; const faults: unknown[] = [];
  const transport: RawTransport = {
    exchange: async () => '',
    subscribe(onEvent) { registrations++; onEvent(raw('sc14-event-one')); return () => { released++; }; },
  };
  const client = new BridgeClient(transport, { requestId: () => id(1), maximumSubscriptions: 1 });
  const stop = client.subscribe(() => { client.subscribe(() => {}, fault => faults.push(fault)); });
  expect(registrations).toBe(1);
  expect(faults).toEqual([{ code: 'subscription_limit', delivery: 'not_sent' }]);
  stop(); stop(); client.dispose(); expect(released).toBe(1);
});

test('synchronous fault retains registration custody until raw disposer is available', () => {
  let registrations = 0; let released = 0; let emit!: (frame: RawFrame) => void;
  const faults: unknown[] = []; let received = 0;
  const transport: RawTransport = {
    exchange: async () => '',
    subscribe(onEvent, onFault) {
      registrations++; emit = onEvent;
      onFault!({ code: 'disconnected', delivery: 'may_have_reached_backend' });
      return () => { released++; };
    },
  };
  const client = new BridgeClient(transport, { requestId: () => id(1), maximumSubscriptions: 1 });
  const stop = client.subscribe(() => { received++; }, fault => {
    faults.push(fault); client.subscribe(() => {}, nestedFault => faults.push(nestedFault));
  });
  expect(registrations).toBe(1); expect(released).toBe(1);
  expect(faults).toEqual([{ code: 'disconnected', delivery: 'may_have_reached_backend' }, { code: 'subscription_limit', delivery: 'not_sent' }]);
  emit(raw('sc14-event-one')); stop(); client.dispose(); expect(received).toBe(0); expect(released).toBe(1);
});

test('admitted replay cannot substitute a different operation identity', async () => {
  let calls = 0;
  const { client } = harness(async request => correlated(raw(++calls === 1 ? 'sc14-admit-reply' : 'sc14-wrong-replay-operation-reply'), request));
  const input = JSON.parse(raw('sc14-admit-request')).body.command.input;
  expect(await client.command('commit', input)).toMatchObject({ kind: 'result' });
  expect(await client.replayCommit(input.idempotencyKey)).toMatchObject({ kind: 'fault', fault: { code: 'correlation' } });
  expect(client.getReplay(input.idempotencyKey)?.operationId).toBe(JSON.parse(raw('sc14-admit-reply')).body.result.command.output.operationId);
});

test('draft results must echo exact captured document and draft binding', async () => {
  const original = JSON.parse(raw('sc08-open-clean-draft-request')).body.command.input;
  const { client } = harness(async request => {
    const frame = JSON.parse(correlated(raw('sc08-open-clean-draft-reply'), request));
    frame.body.result.command.output.draft.document.documentId = id(999);
    return JSON.stringify(frame);
  });
  expect(await client.command('open_draft', original)).toMatchObject({ kind: 'fault', fault: { code: 'correlation' } });
});

// Compile-time inputs/outputs come from unions, including invalid call refusal.
function compileChecks(client: BridgeClient) {
  const query: Promise<ClientOutcome<QueryOutput<'hello'>>> = client.query('hello', {});
  const command: Promise<ClientOutcome<CommandOutput<'commit'>>> = client.command('commit', { idempotencyKey: id(1), planRef: { hostEpoch: id(2), planId: id(3), reviewDigest: 'sha256:' + 'a'.repeat(64) } });
  // @ts-expect-error query names cannot be commands
  client.query('commit', {});
  // @ts-expect-error generated target input is required
  client.query('resolve_target', {});
  // @ts-expect-error required nullable envelope fields are outside binding equivalence
  bindingEquivalent({ requestId: null, protocolVersion: 1, body: {} }, { protocolVersion: 1, body: {} });
  return [query, command];
}
void compileChecks;
