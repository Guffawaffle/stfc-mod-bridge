import test from 'node:test';
import assert from 'node:assert/strict';
import { createHash } from 'node:crypto';
import { readFileSync } from 'node:fs';
import Ajv from 'ajv';
import addFormats from 'ajv-formats';
import { checkTranscript, TranscriptError, MAX_TRANSCRIPT_STEPS } from './transcript.mjs';

// Unit data exercises relationships only. The root-owned golden harness performs
// actual schema/Rust normalization before using this checker; these tests neither
// run an engine nor assert real persistence, side effects or native ownership.
const id = number => `${number.toString(16).padStart(8, '0')}-1111-4111-8111-111111111111`;
const epoch = id(1), stream = id(2), nextEpoch = id(3), nextStream = id(4);
const clone = value => JSON.parse(JSON.stringify(value));
const sorted = value => Array.isArray(value) ? value.map(sorted) : value && typeof value === 'object'
  ? Object.fromEntries(Object.keys(value).sort().map(key => [key, sorted(value[key])])) : value;
const ajv = new Ajv({ strict: true, coerceTypes: false, useDefaults: false, removeAdditional: false });
addFormats(ajv, { mode: 'full', formats: ['date-time'] });
const schemas = Object.fromEntries(['request', 'reply', 'event'].map(kind => [kind,
  ajv.compile(JSON.parse(readFileSync(new URL(`../generated/${kind}.schema.json`, import.meta.url), 'utf8')))]));
const independentlyValid = step => {
  if (step.type === 'event') {
    assert.equal(schemas.event(step.event), true, 'Synthetic event must independently satisfy the generated schema');
    return;
  }
  assert.equal(schemas.request(step.request), true, 'Synthetic request must independently satisfy the generated schema');
  assert.equal(schemas.reply(step.reply), true, 'Synthetic reply must independently satisfy the generated schema');
};
const target = {
  installation: { kind: 'registered', registrationId: 'a'.repeat(32), registrationRevision: 'registration-1', physicalId: 'physical-1', nativeTargetRef: 'target-1' },
  profile: { kind: 'ordinary', ownerScope: 'owner-1' }
};
const semantics = {
  hashProfile: 'bridge-plan-semantic-json-v1', action: 'launch_ordinary', trustDomain: 'session', effects: ['launch_session'],
  capture: { kind: 'launch_ordinary', target, catalogRevision: 'catalog-1', runtime: { kind: 'absent' } }
};
const plan = {
  planRef: { planId: id(5), hostEpoch: epoch, reviewDigest: 'sha256:' + createHash('sha256').update('bridge-plan-semantic-json-v1\0').update(JSON.stringify(sorted(semantics))).digest('hex') },
  semantics, expiresAt: '2026-10-03T12:00:00Z', grantsLock: false, grantsPermission: false
};
const planFor = value => ({ ...clone(plan), semantics: clone(value), planRef: {
  ...plan.planRef, reviewDigest: 'sha256:' + createHash('sha256').update('bridge-plan-semantic-json-v1\0')
    .update(JSON.stringify(sorted(digestSemantics(value)))).digest('hex')
} });
// Match the declared Rust hash profile in synthetic fixture construction.
function digestSemantics(value) {
  const normalized = clone(value);
  normalized.effects.sort();
  if (normalized.capture.kind === 'restore_configuration') delete normalized.capture.input.backup.createdAt;
  return normalized;
}
const intent = { kind: 'launch_ordinary', input: { target: { installation: { kind: 'registered', id: 'a'.repeat(32) }, profile: { kind: 'ordinary' } } } };
const committed = { operationId: id(6), operationRevision: '1', semantics, state: { status: 'admitted' } };
const key = id(7);
const evidence = { observationId: id(8), observedAt: '2026-10-03T11:00:00Z', source: 'native_live' };
let requestNumber = 100;
const cursor = (sequence = '0', hostEpoch = epoch, streamId = stream) => ({ hostEpoch, streamId, sequence });
const boundary = (reason = 'initial', value = cursor()) => ({ type: 'boundary', reason, cursor: value });
function exchange(name, input, output, family = 'command', code) {
  const requestId = id(requestNumber++);
  return {
    type: 'exchange', request: { protocolVersion: 1, requestId, body: { type: family, [family]: { name, input: clone(input) } } },
    reply: { protocolVersion: 1, requestId, body: code
      ? { type: 'rejected', error: { code, retryDisposition: 'after_resnapshot', violations: [] } }
      : { type: 'result', result: { type: family, [family]: { name, output: clone(output) } } } }
  };
}
const prepare = value => exchange('prepare', { intent }, value ?? plan);
const commit = (operation = committed, input = { planRef: plan.planRef, idempotencyKey: key }, code) => exchange('commit', input, operation, 'command', code);
const observe = operation => exchange('get_operation', { operationId: operation.operationId }, { operation: { status: 'observed', value: operation, evidence } }, 'query');
const operation = (revision, state) => ({ ...clone(committed), operationRevision: revision, state });
const running = revision => operation(revision, { status: 'running', progress: { phase: 'working', measurement: { unit: 'unknown' } } });
const completed = (revision, kind = 'changed') => operation(revision, { status: 'completed', outcome: kind === 'failed'
  ? { kind, error: { code: 'native_unavailable', retryDisposition: 'after_user_choice', violations: [] } }
  : { kind, reason: { changed: 'applied', no_change: 'already_satisfied', cancelled_before_commit: 'cancellation_accepted', rolled_back: 'rollback_completed' }[kind] } });
const recovery = revision => operation(revision, { status: 'recovery_required', reason: 'interrupted_transaction', recovery: {
  operationId: committed.operationId, transaction: 'native-transaction-1', target: { kind: 'launch', target }
} });
const changedEvent = (sequence, value, hostEpoch = epoch, streamId = stream) => ({ type: 'event', event: {
  protocolVersion: 1, cursor: cursor(sequence, hostEpoch, streamId), body: { type: 'operation_changed', operation: clone(value) }
} });
const invalidated = (sequence, hostEpoch = epoch, streamId = stream) => ({ type: 'event', event: {
  protocolVersion: 1, cursor: cursor(sequence, hostEpoch, streamId), body: { type: 'snapshot_invalidated', reason: 'operation_changed' }
} });
const close = (output, expectedCursor = cursor()) => exchange('request_host_close', { expectedCursor }, output);
const base = () => [boundary(), prepare(), commit()];
const refuses = (steps, code, step) => assert.throws(() => checkTranscript(steps), error => error instanceof TranscriptError
  && error.code === code && (step === undefined || error.step === step));
function restoreCase() {
  const digest = character => 'sha256:' + character.repeat(64);
  const document = { documentId: id(500), target: clone(target), revision: 'document-1',
    baseline: { kind: 'existing', fileIdentity: 'file-1', contentDigest: digest('c') }, schema: {
      providerId: 'synthetic', schemaId: 'synthetic.configuration', schemaVersion: 'v1', digest: digest('a'), runtimeArtifactDigest: digest('d')
    } };
  const backup = { backupId: id(501), document: { ...clone(document), revision: 'document-0',
    baseline: { kind: 'existing', fileIdentity: 'prior-file-1', contentDigest: digest('b') } },
    retainedDigest: digest('b'), nativeBackupRef: 'native-backup-1', createdAt: '2026-10-03T00:00:00Z' };
  const value = { hashProfile: 'bridge-plan-semantic-json-v1', action: 'restore_configuration', trustDomain: 'configuration',
    effects: ['write_configuration'], capture: { kind: 'restore_configuration', input: { document, backup } } };
  const prepared = capture => exchange('prepare', { intent: { kind: 'restore_configuration', input: capture.capture.input } }, planFor(capture));
  return { value, prepared, digest, document, backup };
}

test('pairs query/command variants and request IDs without mutating caller objects', () => {
  const steps = [boundary(), exchange('hello', {}, { supportedVersions: [1], hostEpoch: epoch, hostKind: 'windows_x64', implementedCommands: [] }, 'query'), ...base().slice(1)];
  const before = JSON.stringify(steps);
  assert.deepEqual(checkTranscript(steps), { boundaryCount: 1, exchangeCount: 3, eventCount: 0, operationCount: 1, lostReplyCount: 0 });
  assert.equal(JSON.stringify(steps), before);
  assert.ok(Object.isFrozen(checkTranscript(steps)));
  const badId = clone(steps); badId[1].reply.requestId = id(900); refuses(badId, 'transcript_reply_binding', 1);
  const badVersion = clone(steps); badVersion[1].reply.protocolVersion = 2; refuses(badVersion, 'transcript_reply_binding');
  const badVariant = clone(steps); badVariant[1].reply.body.result.query.name = 'get_operation'; refuses(badVariant, 'transcript_result_variant');
  const badFamily = clone(steps); badFamily[2].reply.body.result.type = 'query'; refuses(badFamily, 'transcript_result_variant');
});

test('exact idempotent input returns the admitted operation, even after lost reply and restart', () => {
  const lost = commit(); lost.delivery = 'lost';
  const replay = commit();
  // Object spelling/order is irrelevant after typed normalization.
  replay.request.body.command.input = { idempotencyKey: key, planRef: { reviewDigest: plan.planRef.reviewDigest, hostEpoch: epoch, planId: plan.planRef.planId } };
  const steps = [boundary(), prepare(), lost, boundary('restart', cursor('0', nextEpoch, nextStream)), replay, observe(committed)];
  assert.equal(checkTranscript(steps).lostReplyCount, 1);
  const wrong = clone(steps); wrong[4].reply.body.result.command.output.operationId = id(901); refuses(wrong, 'transcript_replay_operation');
  const erased = clone(steps); erased[4] = commit(undefined, undefined, 'plan_host_mismatch'); refuses(erased, 'transcript_replay_operation');
  const missing = exchange('get_operation', { operationId: committed.operationId }, { operation: { status: 'missing', evidence } }, 'query');
  refuses([...steps, missing], 'transcript_admitted_operation_missing');
});

test('changed key input rejects before old-host plan admission, and fresh old-host plans refuse', () => {
  const input = { planRef: { ...plan.planRef, reviewDigest: 'sha256:' + 'f'.repeat(64) }, idempotencyKey: key };
  const restarted = [...base(), boundary('restart', cursor('0', nextEpoch, nextStream))];
  assert.doesNotThrow(() => checkTranscript([...restarted, commit(undefined, input, 'idempotency_conflict')]));
  refuses([...restarted, commit(committed, input)], 'transcript_idempotency_conflict');
  refuses([...restarted, commit(undefined, input, 'plan_host_mismatch')], 'transcript_idempotency_conflict');
  const freshInput = { planRef: plan.planRef, idempotencyKey: id(902) };
  assert.doesNotThrow(() => checkTranscript([...restarted, commit(undefined, freshInput, 'plan_host_mismatch')]));
  refuses([...restarted, commit(committed, freshInput)], 'transcript_old_host_plan');
  refuses([boundary(), commit()], 'transcript_unobserved_plan');
});

test('prepared action/ref and captured target/artifact semantics stay bound across commit and observations', () => {
  const wrongAction = clone(plan); wrongAction.semantics.action = 'focus_session';
  refuses([boundary(), prepare(wrongAction)], 'transcript_prepare_action');
  const wrongHost = clone(plan); wrongHost.planRef.hostEpoch = nextEpoch;
  refuses([boundary(), prepare(wrongHost)], 'transcript_plan_binding');
  const replacement = clone(plan); replacement.expiresAt = '2026-10-03T13:00:00Z';
  refuses([boundary(), prepare(), prepare(replacement)], 'transcript_plan_changed');
  const wrongTarget = clone(committed); wrongTarget.semantics.capture.target.installation.physicalId = 'another-installation';
  refuses([boundary(), prepare(), commit(wrongTarget)], 'transcript_capture_changed');
  const wrongCapture = running('2'); wrongCapture.semantics.capture.catalogRevision = 'catalog-2';
  refuses([...base(), changedEvent('1', wrongCapture)], 'transcript_capture_changed');
  const wrongQuery = observe({ ...committed, operationId: id(903) }); wrongQuery.request.body.query.input.operationId = committed.operationId;
  refuses([...base(), wrongQuery], 'transcript_operation_binding');
});

test('independently schema-valid launch messages reject cross-installation/profile/revision capture', () => {
  const requested = { kind: 'launch_isolated', input: {
    target: { installation: { kind: 'registered', id: 'a'.repeat(32), revisionAssertion: 'registration-1' },
      profile: { kind: 'isolated', id: 'b'.repeat(32), revisionAssertion: 'profile-1' } }, storeMode: 'new'
  } };
  const captured = { ...clone(semantics), action: 'launch_isolated', effects: ['launch_session', 'create_isolated_store'],
    capture: { ...clone(semantics.capture), kind: 'launch_isolated', storeMode: 'new',
      target: { ...clone(target), profile: { kind: 'isolated', id: 'b'.repeat(32), revision: 'profile-1' } } } };
  const paired = value => exchange('prepare', { intent: requested }, planFor(value));
  const positive = paired(captured); independentlyValid(positive);
  assert.doesNotThrow(() => checkTranscript([boundary(), positive]));
  for (const mutate of [
    value => { value.capture.target.installation.registrationId = 'c'.repeat(32); },
    value => { value.capture.target.installation.registrationRevision = 'registration-2'; },
    value => { value.capture.target.profile.id = 'd'.repeat(32); },
    value => { value.capture.target.profile.revision = 'profile-2'; }
  ]) {
    const wrong = clone(captured); mutate(wrong);
    const bad = paired(wrong); independentlyValid(bad);
    refuses([boundary(), bad], 'transcript_prepare_target');
  }
  const wrongMode = clone(captured); wrongMode.capture.storeMode = 'existing'; wrongMode.effects = ['launch_session'];
  const badMode = paired(wrongMode); independentlyValid(badMode);
  refuses([boundary(), badMode], 'transcript_prepare_store_mode');
  const ordinaryRequest = exchange('prepare', { intent }, planFor(captured)); independentlyValid(ordinaryRequest);
  refuses([boundary(), ordinaryRequest], 'transcript_prepare_action');
  const ordinaryAssertion = clone(intent); ordinaryAssertion.input.target.profile.catalogIdAssertion = 'b'.repeat(32);
  const ordinaryCapture = clone(semantics); ordinaryCapture.capture.target.profile.ordinaryId = 'c'.repeat(32);
  const badOrdinary = exchange('prepare', { intent: ordinaryAssertion }, planFor(ordinaryCapture)); independentlyValid(badOrdinary);
  refuses([boundary(), badOrdinary], 'transcript_prepare_target');
});

test('preparation compares focus process/session and explicit unrecognized-runtime choices', () => {
  const session = { sessionId: id(400), revision: 'session-1', process: {
    pid: 123, startIdentity: { platform: 'windows', value: 'process-1' }, executableIdentity: 'executable-1', installationPhysicalId: 'physical-1', architecture: 'x86_64'
  } };
  const focus = { hashProfile: 'bridge-plan-semantic-json-v1', action: 'focus_session', trustDomain: 'session', effects: ['focus_session'], capture: { kind: 'focus_session', session, target } };
  const positive = exchange('prepare', { intent: { kind: 'focus_session', input: { session } } }, planFor(focus)); independentlyValid(positive);
  assert.doesNotThrow(() => checkTranscript([boundary(), positive]));
  for (const mutate of [value => { value.capture.session.sessionId = id(401); }, value => { value.capture.session.process.startIdentity.value = 'process-2'; }]) {
    const wrong = clone(focus); mutate(wrong);
    const bad = exchange('prepare', { intent: { kind: 'focus_session', input: { session } } }, planFor(wrong)); independentlyValid(bad);
    refuses([boundary(), bad], 'transcript_prepare_session');
  }
  const choice = clone(intent); choice.input.unrecognizedRuntimeChoice = 'allow_once';
  const unrecognized = clone(semantics);
  unrecognized.capture.unrecognizedRuntimeChoice = 'allow_once';
  unrecognized.capture.runtime = { kind: 'unrecognized', artifactDigest: 'sha256:' + 'a'.repeat(64), choice: 'allow_once' };
  const accepted = exchange('prepare', { intent: choice }, planFor(unrecognized)); independentlyValid(accepted);
  assert.doesNotThrow(() => checkTranscript([boundary(), accepted]));
  const bad = exchange('prepare', { intent }, planFor(unrecognized)); independentlyValid(bad);
  refuses([boundary(), bad], 'transcript_prepare_choice');
  const directory = clone(intent); directory.input.target.installation = { kind: 'directory', directory: { platform: 'windows', value: 'D:\\Synthetic\\Game' } };
  assert.doesNotThrow(() => checkTranscript([boundary(), exchange('prepare', { intent: directory }, planFor(semantics))]));
});

test('effects alone normalize as a set for plans, commits and equal-revision observations', () => {
  const requested = { kind: 'launch_isolated', input: { target: { installation: { kind: 'registered', id: 'a'.repeat(32) }, profile: { kind: 'isolated', id: 'b'.repeat(32) } }, storeMode: 'new' } };
  const captured = { ...clone(semantics), action: 'launch_isolated', effects: ['launch_session', 'create_isolated_store'],
    capture: { ...clone(semantics.capture), kind: 'launch_isolated', storeMode: 'new', target: { ...clone(target), profile: { kind: 'isolated', id: 'b'.repeat(32), revision: 'profile-1' } } } };
  const reordered = clone(captured); reordered.effects.reverse();
  const first = exchange('prepare', { intent: requested }, planFor(captured)); independentlyValid(first);
  const second = exchange('prepare', { intent: requested }, planFor(reordered)); independentlyValid(second);
  const originalOperation = { ...clone(committed), semantics: captured };
  const reorderedOperation = { ...clone(committed), semantics: reordered };
  assert.doesNotThrow(() => checkTranscript([boundary(), first, second, commit(reorderedOperation, { planRef: first.reply.body.result.command.output.planRef, idempotencyKey: key }), observe(originalOperation)]));
  const changed = clone(originalOperation); changed.semantics.effects = ['launch_session'];
  refuses([boundary(), first, commit(originalOperation, { planRef: first.reply.body.result.command.output.planRef, idempotencyKey: key }), observe(changed)], 'transcript_capture_changed');
});

test('restore semantics exclude only the descriptive capture backup timestamp', () => {
  const { value, prepared, digest } = restoreCase();
  const changedTimestamp = clone(value); changedTimestamp.capture.input.backup.createdAt = '2026-10-02T23:59:59Z';
  const first = prepared(value), second = prepared(changedTimestamp);
  independentlyValid(first); independentlyValid(second);
  assert.equal(first.reply.body.result.command.output.planRef.reviewDigest, second.reply.body.result.command.output.planRef.reviewDigest);
  const admitted = { ...clone(committed), semantics: changedTimestamp };
  const input = { planRef: first.reply.body.result.command.output.planRef, idempotencyKey: key };
  const admission = commit(admitted, input); independentlyValid(admission);
  const originalObservation = observe({ ...clone(admitted), semantics: value }); independentlyValid(originalObservation);
  const steps = [boundary(), first, second, admission, originalObservation];
  const before = JSON.stringify(steps);
  assert.doesNotThrow(() => checkTranscript(steps));
  assert.equal(JSON.stringify(steps), before, 'Timestamp normalization must not mutate the captured input');
  for (const mutate of [
    capture => { capture.capture.input.backup.backupId = id(502); },
    capture => { capture.capture.input.backup.nativeBackupRef = 'native-backup-2'; },
    capture => { capture.capture.input.backup.retainedDigest = digest('e'); capture.capture.input.backup.document.baseline.contentDigest = digest('e'); },
    capture => { capture.capture.input.document.revision = 'document-2'; }
  ]) {
    const wrong = clone(admitted); mutate(wrong.semantics);
    const wrongAdmission = commit(wrong, input); independentlyValid(wrongAdmission);
    refuses([boundary(), first, wrongAdmission], 'transcript_capture_changed');
  }
  const expired = clone(second); expired.reply.body.result.command.output.expiresAt = '2026-10-03T13:00:00Z';
  independentlyValid(expired);
  refuses([boundary(), first, expired], 'transcript_plan_changed');
});

test('restore normalization preserves terminal receipt timestamps and exact commit input', () => {
  const { value, prepared, digest, document } = restoreCase();
  const first = prepared(value), admitted = { ...clone(committed), semantics: value };
  const input = { planRef: first.reply.body.result.command.output.planRef, idempotencyKey: key };
  const admission = commit(admitted, input);
  const changed = { ...clone(admitted), operationRevision: '2', state: { status: 'completed', outcome: { kind: 'changed', reason: 'applied', receipt: {
    kind: 'configuration_written', document: { ...clone(document), revision: 'document-2', baseline: { kind: 'existing', fileIdentity: 'file-2', contentDigest: digest('b') } },
    backup: { backupId: id(503), document: clone(document), retainedDigest: digest('c'), nativeBackupRef: 'native-backup-current', createdAt: '2026-10-03T11:00:00Z' }
  } } } };
  const original = observe(changed); independentlyValid(original);
  const altered = clone(changed); altered.state.outcome.receipt.backup.createdAt = '2026-10-03T11:01:00Z';
  const sameRevision = observe(altered); independentlyValid(sameRevision);
  refuses([boundary(), first, admission, original, sameRevision], 'transcript_revision_reused');
  altered.operationRevision = '3';
  const laterRevision = observe(altered); independentlyValid(laterRevision);
  refuses([boundary(), first, admission, original, laterRevision], 'transcript_terminal_changed');
  const changedInput = { ...clone(input), planRef: { ...input.planRef, planId: id(504) } };
  const replay = commit(admitted, changedInput); independentlyValid(replay);
  refuses([boundary(), first, admission, replay], 'transcript_idempotency_conflict');
  assert.doesNotThrow(() => checkTranscript([boundary(), first, admission, commit(undefined, changedInput, 'idempotency_conflict')]));
});

test('expanded input captures preserve document/native refs and ordered artifact arrays', () => {
  const digest = number => 'sha256:' + String(number).repeat(64);
  const document = { documentId: id(410), target, revision: 'document-1', baseline: { kind: 'missing' }, schema: {
    providerId: 'synthetic', schemaId: 'synthetic.configuration', schemaVersion: 'v1', digest: digest(0), runtimeArtifactDigest: digest(1)
  } };
  const selectedRelease = { selectionId: id(411), hostEpoch: epoch, revision: 'release-1', target, providerId: 'synthetic', distributionId: 'synthetic.runtime', channelId: 'stable', releaseVersion: '1', clientRevision: 'client-1',
    artifacts: [{ role: 'runtime_module', platform: 'windows', architecture: 'x86_64', digest: digest(1), size: '1' },
      { role: 'runtime_manifest', platform: 'windows', architecture: 'x86_64', digest: digest(2), size: '2' }], configurationSchema: document.schema, authority: 'verified-1' };
  const input = { target, selectedRelease, expectedOwnership: { kind: 'absent' }, configuration: { kind: 'unchanged', document } };
  const capture = { hashProfile: 'bridge-plan-semantic-json-v1', action: 'runtime_install', trustDomain: 'runtime_distribution', effects: ['replace_runtime'],
    capture: { kind: 'runtime_install', input, preparedConfiguration: { kind: 'unchanged', document } } };
  const prepared = exchange('prepare', { intent: { kind: 'runtime_install', input } }, planFor(capture));
  const admitted = { ...clone(committed), semantics: capture };
  const admission = commit(admitted, { planRef: prepared.reply.body.result.command.output.planRef, idempotencyKey: key });
  independentlyValid(prepared); independentlyValid(admission);
  assert.doesNotThrow(() => checkTranscript([boundary(), prepared, admission]));
  const reversed = clone(admitted); reversed.semantics.capture.input.selectedRelease.artifacts.reverse();
  const reversedObservation = observe(reversed); independentlyValid(reversedObservation);
  refuses([boundary(), prepared, admission, reversedObservation], 'transcript_capture_changed');
  const changed = clone(capture); changed.capture.input.configuration.document.revision = 'document-2';
  changed.capture.preparedConfiguration.document.revision = 'document-2';
  const changedPreparation = exchange('prepare', { intent: { kind: 'runtime_install', input } }, planFor(changed)); independentlyValid(changedPreparation);
  refuses([boundary(), changedPreparation], 'transcript_prepare_input');
  const createInput = { name: 'Synthetic', setup: { kind: 'new' }, preferredInstallation: { kind: 'registered', id: 'a'.repeat(32), revisionAssertion: 'registration-1' }, expectedCatalogRevision: 'catalog-1' };
  const createCapture = { hashProfile: 'bridge-plan-semantic-json-v1', action: 'create_profile', trustDomain: 'profile_state', effects: ['publish_profile'], capture: { kind: 'create_profile', input: {
    name: 'Synthetic', setup: { kind: 'new' }, preferredInstallation: target.installation, catalogRevision: 'catalog-1', nativePreparationRef: 'native-prepare-1', destinationOwner: 'owner-1'
  } } };
  const createPreparation = exchange('prepare', { intent: { kind: 'create_profile', input: createInput } }, planFor(createCapture)); independentlyValid(createPreparation);
  assert.doesNotThrow(() => checkTranscript([boundary(), createPreparation]));
  const other = clone(createCapture); other.capture.input.preferredInstallation.registrationId = 'b'.repeat(32);
  const otherPreparation = exchange('prepare', { intent: { kind: 'create_profile', input: createInput } }, planFor(other)); independentlyValid(otherPreparation);
  refuses([boundary(), otherPreparation], 'transcript_prepare_target');
  const registration = { hashProfile: 'bridge-plan-semantic-json-v1', action: 'register_installation', trustDomain: 'profile_state', effects: ['register_installation'], capture: { kind: 'register_installation', input: {
    name: 'Synthetic', physicalId: 'physical-1', nativeTargetRef: 'target-1', catalogRevision: 'catalog-1'
  } } };
  const registrationInput = { directory: { platform: 'windows', value: 'D:\\Synthetic\\Game' }, name: 'Synthetic', expectedCatalogRevision: 'catalog-1' };
  const registrationPreparation = exchange('prepare', { intent: { kind: 'register_installation', input: registrationInput } }, planFor(registration)); independentlyValid(registrationPreparation);
  assert.doesNotThrow(() => checkTranscript([boundary(), registrationPreparation]));
  const rename = clone(registration); rename.capture.input.name = 'Other';
  const renamedPreparation = exchange('prepare', { intent: { kind: 'register_installation', input: registrationInput } }, planFor(rename)); independentlyValid(renamedPreparation);
  refuses([boundary(), renamedPreparation], 'transcript_prepare_input');
  const draft = { draftId: id(412), hostEpoch: epoch, revision: '1', document };
  const save = { hashProfile: 'bridge-plan-semantic-json-v1', action: 'save_configuration', trustDomain: 'configuration', effects: ['write_configuration'], capture: { kind: 'save_configuration', input: {
    draft: { draft, schema: { binding: document.schema, fields: [], sync: [] }, edits: [], apply: [], state: 'clean', validation: [] }, candidateDigest: digest(3)
  } } };
  const savePreparation = exchange('prepare', { intent: { kind: 'save_configuration', input: { draft } } }, planFor(save)); independentlyValid(savePreparation);
  assert.doesNotThrow(() => checkTranscript([boundary(), savePreparation]));
  const otherDraft = clone(save); otherDraft.capture.input.draft.draft.document.revision = 'document-2';
  const otherDraftPreparation = exchange('prepare', { intent: { kind: 'save_configuration', input: { draft } } }, planFor(otherDraft)); independentlyValid(otherDraftPreparation);
  refuses([boundary(), otherDraftPreparation], 'transcript_prepare_input');
});

test('sensitive-input replies bind the exact original draft, field and sensitivity', () => {
  const document = { documentId: id(520), target, revision: 'document-1', baseline: { kind: 'missing' }, schema: {
    providerId: 'synthetic', schemaId: 'synthetic.configuration', schemaVersion: 'v1',
    digest: 'sha256:' + 'a'.repeat(64), runtimeArtifactDigest: 'sha256:' + 'b'.repeat(64)
  } };
  const draft = { draftId: id(521), hostEpoch: epoch, revision: '1', document };
  const secretBinding = { draft, fieldId: 'data_sync.token', sensitivity: 'secret' };
  const privateBinding = { draft, fieldId: 'data_sync.endpoint', sensitivity: 'private' };
  const secretOutcome = { status: 'captured_secret', reference: { secretId: id(522), draft, fieldId: secretBinding.fieldId } };
  const privateOutcome = { status: 'captured_private', reference: { valueId: id(523), document, fieldId: privateBinding.fieldId, revision: 'private-1', capturedFor: draft } };
  for (const [binding, outcome] of [
    [secretBinding, secretOutcome], [privateBinding, privateOutcome], [secretBinding, { status: 'cancelled' }],
    [privateBinding, { status: 'unavailable', reason: 'protected_entry_unavailable' }]
  ]) {
    const positive = exchange('request_sensitive_input', binding, { binding, outcome }); independentlyValid(positive);
    assert.doesNotThrow(() => checkTranscript([boundary(), positive]));
  }
  const wrongDraft = clone(secretBinding); wrongDraft.draft.draftId = id(524);
  const internallyMatched = exchange('request_sensitive_input', secretBinding, { binding: wrongDraft,
    outcome: { status: 'captured_secret', reference: { ...clone(secretOutcome.reference), draft: wrongDraft.draft } } });
  independentlyValid(internallyMatched);
  refuses([boundary(), internallyMatched], 'transcript_sensitive_input_binding', 1);
  for (const mutate of [
    binding => { binding.draft.hostEpoch = nextEpoch; },
    binding => { binding.draft.revision = '2'; },
    binding => { binding.draft.document.revision = 'document-2'; },
    binding => { binding.draft.document.target.installation.physicalId = 'other-physical-installation'; },
    binding => { binding.fieldId = 'data_sync.other_token'; },
    binding => { binding.sensitivity = 'private'; }
  ]) {
    const substituted = clone(secretBinding); mutate(substituted);
    const negative = exchange('request_sensitive_input', secretBinding, { binding: substituted, outcome: { status: 'cancelled' } }); independentlyValid(negative);
    refuses([boundary(), negative], 'transcript_sensitive_input_binding', 1);
  }
});

test('export-destination outcomes retain the exact requested preview binding', () => {
  const preview = { previewId: id(530), hostEpoch: epoch, revision: 'preview-1', target, disclosure: 'redacted', digest: 'sha256:' + 'a'.repeat(64) };
  const input = { preview };
  const destination = { destinationId: id(531), hostEpoch: epoch, nativeTargetRef: 'native-export-custody-1', revision: 'destination-1' };
  const outcomes = binding => [
    { status: 'captured', destination: { ...destination, hostEpoch: binding.preview.hostEpoch } },
    { status: 'cancelled' }, { status: 'unavailable', reason: 'selection_unavailable' }
  ];
  for (const outcome of outcomes(input)) {
    const positive = exchange('request_export_destination', input, { binding: input, outcome }); independentlyValid(positive);
    const before = JSON.stringify(positive);
    assert.doesNotThrow(() => checkTranscript([boundary(), positive]));
    assert.equal(JSON.stringify(positive), before);
  }
  const reorderedBinding = { preview: { digest: preview.digest, target: preview.target, disclosure: preview.disclosure,
    revision: preview.revision, hostEpoch: preview.hostEpoch, previewId: preview.previewId } };
  const reordered = exchange('request_export_destination', input, { binding: reorderedBinding, outcome: { status: 'cancelled' } }); independentlyValid(reordered);
  assert.doesNotThrow(() => checkTranscript([boundary(), reordered]));
  for (const mutate of [
    binding => { binding.preview.previewId = id(532); },
    binding => { binding.preview.hostEpoch = nextEpoch; },
    binding => { binding.preview.revision = 'preview-2'; },
    binding => { binding.preview.target.installation.registrationId = 'b'.repeat(32); },
    binding => { binding.preview.target.profile.ownerScope = 'other-owner'; },
    binding => { binding.preview.disclosure = 'include_paths'; },
    binding => { binding.preview.digest = 'sha256:' + 'b'.repeat(64); }
  ]) {
    const substituted = clone(input); mutate(substituted);
    // A captured outcome still matches its own echoed host epoch, so Rust's
    // intra-message check can pass; the original-request relation must refuse.
    for (const outcome of outcomes(substituted)) {
      const negative = exchange('request_export_destination', input, { binding: substituted, outcome }); independentlyValid(negative);
      refuses([boundary(), negative], 'transcript_export_destination_binding', 1);
    }
  }
});

test('operation revisions remain exact and monotone, including resnapshot and restart', () => {
  const big = running('9007199254740992');
  const later = running('9007199254740993');
  assert.doesNotThrow(() => checkTranscript([...base(), observe(big), observe(later), observe(later)]));
  refuses([...base(), observe(later), observe(big)], 'transcript_revision_regressed');
  refuses([...base(), observe(big), observe(completed('9007199254740992'))], 'transcript_revision_reused');
  refuses([...base(), observe(later), boundary('resnapshot', cursor('0', epoch, nextStream)), observe(big)], 'transcript_revision_regressed');
  refuses([...base(), observe(later), boundary('restart', cursor('0', nextEpoch, nextStream)), observe(big)], 'transcript_revision_regressed');
  for (const value of [2, '02', '-1', '1e2', '18446744073709551616']) refuses([...base(), observe(running(value))], 'transcript_counter');
});

test('events require exact host/stream/consecutive sequence and reject replay or reorder', () => {
  const steps = [...base(), changedEvent('1', running('2')), invalidated('2')];
  assert.equal(checkTranscript(steps).eventCount, 2);
  refuses([...steps, invalidated('2')], 'transcript_sequence');
  refuses([...base(), invalidated('2')], 'transcript_sequence');
  refuses([...base(), invalidated('1', nextEpoch)], 'transcript_epoch');
  refuses([...base(), invalidated('1', epoch, nextStream)], 'transcript_stream');
  for (const value of [1, '01', '1.0', '18446744073709551616']) refuses([...base(), invalidated(value)], 'transcript_counter');
  const huge = '9007199254740992';
  assert.doesNotThrow(() => checkTranscript([boundary('initial', cursor(huge)), invalidated('9007199254740993')]));
});

test('explicit reconnect resumes exactly; resnapshot/retention gap anchors new observation', () => {
  const steps = [boundary(), invalidated('1'), boundary('reconnect', cursor('1')), invalidated('2'), boundary('retention_gap', cursor('20')), invalidated('21'), boundary('resnapshot', cursor('0', epoch, nextStream)), invalidated('1', epoch, nextStream)];
  assert.equal(checkTranscript(steps).boundaryCount, 4);
  refuses([boundary(), invalidated('1'), boundary('reconnect', cursor('0'))], 'transcript_sequence_regressed');
  refuses([boundary(), boundary('reconnect', cursor('3'))], 'transcript_reconnect');
  refuses([boundary(), boundary('reconnect', cursor('0', epoch, nextStream))], 'transcript_reconnect');
  refuses([boundary(), boundary('resnapshot', cursor('0', nextEpoch, nextStream))], 'transcript_epoch');
  refuses([boundary(), boundary('restart', cursor('0', epoch, nextStream))], 'transcript_restart');
  refuses([boundary(), boundary('restart', cursor('0', nextEpoch, stream))], 'transcript_restart');
  refuses([boundary(), boundary('restart', cursor('0', nextEpoch, nextStream)), boundary('restart', cursor())], 'transcript_restart');
});

test('expanded array queries, snapshots and resumed event batches keep relational history', () => {
  const inventory = items => ({ items, completeness: 'complete', issues: [], revision: 'inventory-1' });
  const unavailable = { status: 'unavailable', reason: 'native_unavailable' };
  const snapshot = value => ({ cursor: cursor(), preferences: unavailable, profiles: unavailable, installations: unavailable,
    sessions: unavailable, operations: inventory([value]), capabilities: inventory([]) });
  const actions = exchange('get_actions', { scope: { kind: 'target', target }, actions: ['launch_ordinary'] }, [], 'query'); independentlyValid(actions);
  assert.doesNotThrow(() => checkTranscript([boundary(), actions]));
  const observed = exchange('snapshot', {}, snapshot(running('2')), 'query'); independentlyValid(observed);
  const finished = observe(completed('3')); independentlyValid(finished);
  assert.doesNotThrow(() => checkTranscript([...base(), observed, finished]));
  const changed = clone(observed); changed.reply.body.result.query.output.operations.items[0].semantics.capture.catalogRevision = 'other-catalog';
  refuses([...base(), changed], 'transcript_capture_changed');
  refuses([...base(), observed, observe(running('1'))], 'transcript_revision_regressed');
  const wrongCursor = clone(observed); wrongCursor.reply.body.result.query.output.cursor.sequence = '10';
  refuses([...base(), wrongCursor], 'transcript_snapshot_cursor');
  const firstEvent = changedEvent('1', running('2')).event;
  const secondEvent = invalidated('2').event;
  const resumed = exchange('resume_events', { after: cursor(), maximumEvents: '2' }, { after: cursor(), events: [firstEvent, secondEvent], next: cursor('2') }, 'query'); independentlyValid(resumed);
  assert.equal(checkTranscript([...base(), boundary('reconnect'), resumed, finished]).eventCount, 2);
  const omitted = clone(resumed); omitted.reply.body.result.query.output.next.sequence = '1';
  refuses([...base(), omitted], 'transcript_resume_cursor');
  const badAfter = clone(resumed); badAfter.reply.body.result.query.output.after.sequence = '1';
  refuses([...base(), badAfter], 'transcript_resume_cursor');
  const reordered = clone(resumed); reordered.reply.body.result.query.output.events.reverse();
  refuses([...base(), reordered], 'transcript_sequence');
  const tooMany = clone(resumed); tooMany.request.body.query.input.maximumEvents = '1';
  refuses([...base(), tooMany], 'transcript_resume_limit');
  refuses([...base(), resumed, { type: 'event', event: firstEvent }], 'transcript_sequence');
  const replay = clone(resumed); replay.request.body.query.input.after.sequence = '1'; replay.reply.body.result.query.output.after.sequence = '1';
  refuses([...base(), resumed, replay], 'transcript_resume_cursor');
  const binding = { providerId: 'synthetic', schemaId: 'synthetic.configuration', schemaVersion: 'v1',
    digest: 'sha256:' + 'a'.repeat(64), runtimeArtifactDigest: 'sha256:' + 'b'.repeat(64) };
  const document = { documentId: id(416), target, revision: 'document-1', baseline: { kind: 'missing' }, schema: binding };
  const draftChanged = { type: 'event', event: { protocolVersion: 1, cursor: cursor('3'), body: { type: 'draft_changed', draft: {
    draft: { draftId: id(415), hostEpoch: epoch, revision: '1', document }, schema: { binding, fields: [], sync: [] },
    edits: [], apply: [], state: 'clean', validation: []
  } } } };
  independentlyValid(draftChanged);
  // This checker recognizes the typed event without pretending to validate or
  // execute a draft engine. The golden harness validates all embedded DTOs.
  assert.equal(checkTranscript([...base(), resumed, draftChanged]).eventCount, 3);
});

test('complete snapshots cannot omit any observed pending operation state', () => {
  const inventory = items => ({ items, completeness: 'complete', issues: [], revision: 'inventory-1' });
  const snapshot = items => exchange('snapshot', {}, { cursor: cursor(), preferences: { status: 'unavailable', reason: 'native_unavailable' },
    profiles: { status: 'unavailable', reason: 'native_unavailable' }, installations: { status: 'unavailable', reason: 'native_unavailable' },
    sessions: { status: 'unavailable', reason: 'native_unavailable' }, capabilities: inventory([]), operations: inventory(items) }, 'query');
  const cancellation = operation('2', { status: 'cancellation_requested', progress: { phase: 'working', measurement: { unit: 'unknown' } } });
  for (const pending of [committed, running('2'), recovery('2'), cancellation]) {
    const observation = observe(pending), empty = snapshot([]), retained = snapshot([pending]);
    independentlyValid(observation); independentlyValid(empty); independentlyValid(retained);
    refuses([boundary(), observation, empty], 'transcript_snapshot_operation_missing', 2);
    assert.doesNotThrow(() => checkTranscript([boundary(), observation, retained]));
    const another = { ...clone(committed), operationId: id(540) };
    const substituted = snapshot([another]); independentlyValid(substituted);
    refuses([boundary(), observation, substituted], 'transcript_snapshot_operation_missing', 2);
    const explicitTerminal = snapshot([completed('3', 'no_change')]); independentlyValid(explicitTerminal);
    assert.doesNotThrow(() => checkTranscript([boundary(), observation, explicitTerminal, empty]));
    const withIssues = clone(empty);
    withIssues.reply.body.result.query.output.operations.issues = [{ code: 'native_unavailable', resource: { kind: 'operation', id: pending.operationId } }];
    independentlyValid(withIssues);
    refuses([boundary(), observation, withIssues], 'transcript_snapshot_operation_missing', 2);
  }
});

test('complete snapshots may omit already observed completed operations', () => {
  const snapshot = exchange('snapshot', {}, { cursor: cursor(), preferences: { status: 'unavailable', reason: 'native_unavailable' },
    profiles: { status: 'unavailable', reason: 'native_unavailable' }, installations: { status: 'unavailable', reason: 'native_unavailable' },
    sessions: { status: 'unavailable', reason: 'native_unavailable' },
    capabilities: { items: [], completeness: 'complete', issues: [], revision: 'capabilities-1' },
    operations: { items: [], completeness: 'complete', issues: [], revision: 'operations-1' } }, 'query');
  independentlyValid(snapshot);
  for (const kind of ['changed', 'no_change', 'failed', 'rolled_back', 'cancelled_before_commit']) {
    const observation = observe(completed('2', kind)); independentlyValid(observation);
    assert.doesNotThrow(() => checkTranscript([...base(), observation, snapshot]));
  }
});

test('explicit partial snapshot issues permit omissions without erasing pending history', () => {
  const partial = exchange('snapshot', {}, { cursor: cursor(), preferences: { status: 'unavailable', reason: 'native_unavailable' },
    profiles: { status: 'unavailable', reason: 'native_unavailable' }, installations: { status: 'unavailable', reason: 'native_unavailable' },
    sessions: { status: 'unavailable', reason: 'native_unavailable' },
    capabilities: { items: [], completeness: 'complete', issues: [], revision: 'capabilities-1' },
    operations: { items: [], completeness: 'partial', issues: [{ code: 'native_unavailable', resource: { kind: 'operation', id: committed.operationId } }], revision: 'operations-1' } }, 'query');
  independentlyValid(partial);
  const cancellation = operation('2', { status: 'cancellation_requested', progress: { phase: 'working', measurement: { unit: 'unknown' } } });
  for (const pending of [committed, running('2'), recovery('2'), cancellation]) {
    const observation = observe(pending); independentlyValid(observation);
    assert.doesNotThrow(() => checkTranscript([boundary(), observation, partial]));
    const complete = clone(partial); complete.reply.body.result.query.output.operations.completeness = 'complete'; complete.reply.body.result.query.output.operations.issues = [];
    independentlyValid(complete);
    refuses([boundary(), observation, partial, complete], 'transcript_snapshot_operation_missing', 3);
    refuses([boundary(), observation, partial, close({ kind: 'ready' })], 'transcript_close_obligations', 3);
  }
});

test('completed changed/no-change/failed/rolled-back outcomes cannot silently become running or change outcome', () => {
  for (const kind of ['changed', 'no_change', 'failed', 'rolled_back']) {
    const finished = completed('2', kind);
    assert.doesNotThrow(() => checkTranscript([...base(), observe(finished), observe(finished), close({ kind: 'ready' })]));
    refuses([...base(), observe(finished), observe(running('3'))], 'transcript_terminal_changed');
  }
  refuses([...base(), observe(completed('2', 'no_change')), observe(completed('3', 'changed'))], 'transcript_terminal_changed');
});

test('cancellation dispositions bind exact operation and honest state without claiming game termination', () => {
  const cancellation = operation('2', { status: 'cancellation_requested', progress: { phase: 'working', measurement: { unit: 'unknown' } } });
  const cancel = (kind, value, revision = '1') => exchange('cancel_operation', { operationId: committed.operationId, expectedOperationRevision: revision }, { kind, operation: value });
  assert.doesNotThrow(() => checkTranscript([...base(), cancel('requested', cancellation), cancel('cancelled_before_commit', completed('3', 'cancelled_before_commit'), '2'), close({ kind: 'ready' })]));
  assert.doesNotThrow(() => checkTranscript([...base(), cancel('too_late', running('2')), observe(completed('3')), cancel('already_terminal', completed('3'), '3')]));
  refuses([...base(), cancel('cancelled_before_commit', completed('2', 'changed'))], 'transcript_cancel_disposition');
  refuses([...base(), cancel('requested', { ...cancellation, operationId: id(904) })], 'transcript_operation_binding');
  refuses([...base(), cancel('too_late', running('2'), '3')], 'transcript_cancel_revision');
  refuses([...base(), observe(running('2')), cancel('too_late', running('3'), '1')], 'transcript_cancel_revision');
});

test('close ready refuses observed work, deferred obligations match revisions and cover known pending work', () => {
  const obligations = [{ kind: 'operation', operationId: committed.operationId, operationRevision: '1' }];
  assert.doesNotThrow(() => checkTranscript([...base(), close({ kind: 'deferred', obligations })]));
  assert.doesNotThrow(() => checkTranscript([...base(), { type: 'event', event: { protocolVersion: 1, cursor: cursor('1'), body: { type: 'host_close_deferred', obligations } } }]));
  refuses([...base(), close({ kind: 'ready' })], 'transcript_close_obligations');
  refuses([...base(), close({ kind: 'deferred', obligations: [] })], 'transcript_close_obligations');
  refuses([...base(), close({ kind: 'deferred', obligations: [{ ...obligations[0], operationRevision: '2' }] })], 'transcript_close_obligations');
  refuses([...base(), close({ kind: 'deferred', obligations: [...obligations, ...obligations] })], 'transcript_close_obligations');
  refuses([...base(), close({ kind: 'deferred', obligations: [{ ...obligations[0], operationId: id(905) }] })], 'transcript_close_obligations');
  refuses([...base(), close({ kind: 'deferred', obligations }, cursor('1'))], 'transcript_close_cursor');
});

test('recovery close preserves exact recovery binding and cannot erase active/recovery obligations', () => {
  const interrupted = recovery('2');
  const recoveries = [interrupted.state.recovery];
  assert.doesNotThrow(() => checkTranscript([...base(), observe(interrupted), close({ kind: 'recovery_required', recoveries }), observe(completed('3', 'rolled_back')), close({ kind: 'ready' })]));
  refuses([...base(), observe(interrupted), close({ kind: 'ready' })], 'transcript_close_obligations');
  const changed = clone(recoveries); changed[0].transaction = 'other-native-transaction';
  refuses([...base(), observe(interrupted), close({ kind: 'recovery_required', recoveries: changed })], 'transcript_close_recovery');
  refuses([...base(), observe(interrupted), close({ kind: 'recovery_required', recoveries: [...recoveries, ...recoveries] })], 'transcript_close_recovery');
  refuses([...base(), close({ kind: 'recovery_required', recoveries })], 'transcript_close_recovery');
});

test('deferred close covers safe recovery together with session custody or another running operation', () => {
  const interrupted = recovery('2');
  const session = { sessionId: id(920), revision: 'session-1', process: {
    pid: 123, startIdentity: { platform: 'windows', value: 'process-1' }, executableIdentity: 'executable-1',
    installationPhysicalId: target.installation.physicalId, architecture: 'x86_64'
  } };
  const another = { ...running('3'), operationId: id(921) };
  const recoveryObligation = { kind: 'operation', operationId: interrupted.operationId, operationRevision: interrupted.operationRevision };
  for (const scenario of [
    { observations: [], obligation: { kind: 'session_custody', session } },
    { observations: [observe(another)], obligation: { kind: 'operation', operationId: another.operationId, operationRevision: another.operationRevision } }
  ]) {
    const prefix = [...base(), observe(interrupted), ...scenario.observations];
    const complete = close({ kind: 'deferred', obligations: [recoveryObligation, scenario.obligation] });
    const incomplete = close({ kind: 'deferred', obligations: [scenario.obligation] });
    for (const step of [...prefix.filter(step => step.type === 'exchange'), complete, incomplete]) independentlyValid(step);
    assert.doesNotThrow(() => checkTranscript([...prefix, complete]));
    refuses([...prefix, incomplete], 'transcript_close_obligations');
    const stale = close({ kind: 'deferred', obligations: [{ ...recoveryObligation, operationRevision: '1' }, scenario.obligation] });
    independentlyValid(stale);
    refuses([...prefix, stale], 'transcript_close_obligations');
  }
});

test('closed bounded step metadata and redacted errors never echo synthetic sensitive values', () => {
  refuses([], 'transcript_steps', -1);
  refuses(new Array(1), 'transcript_step_shape', 0);
  refuses([invalidated('1')], 'transcript_boundary', 0);
  refuses([boundary(), { type: 'engine_execute', password: 'synthetic-secret-do-not-echo' }], 'transcript_step_type', 1);
  const extra = boundary(); extra.privatePath = 'synthetic-secret-do-not-echo';
  try { checkTranscript([extra]); assert.fail(); }
  catch (error) { assert.equal(error.code, 'transcript_step_shape'); assert.equal(error.message, error.code); assert.ok(!String(error).includes(extra.privatePath)); }
  const badDelivery = prepare(); badDelivery.delivery = 'maybe'; refuses([boundary(), badDelivery], 'transcript_delivery');
  const sparse = [boundary(), prepare()]; delete sparse[1]; refuses(sparse, 'transcript_step_shape');
  const cyclic = boundary(); cyclic.cursor.sequence = cyclic; refuses([cyclic], 'transcript_not_normalized');
  const steps = [boundary(), ...Array.from({ length: MAX_TRANSCRIPT_STEPS - 1 }, () => exchange('hello', {}, { supportedVersions: [1], hostEpoch: epoch, hostKind: 'windows_x64', implementedCommands: [] }, 'query'))];
  assert.equal(checkTranscript(steps).exchangeCount, MAX_TRANSCRIPT_STEPS - 1);
  refuses([...steps, boundary('reconnect')], 'transcript_steps', -1);
});
