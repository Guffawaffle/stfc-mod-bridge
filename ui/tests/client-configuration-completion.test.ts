import { readFileSync } from 'node:fs';
import { expect, test } from 'vitest';
import { BridgeClient, ObservationStore, WorkContext, decodeEvent, decodeReply, decodeRequest, type ClientOutcome } from '../src/client';
import { BridgeFacade } from '../src/state';
import type { DraftSnapshot, Event, GetDraftResult, OperationSnapshot, Snapshot } from '../src/generated/protocol';

const fixtures = new URL('../../contracts/fixtures/', import.meta.url);
const frame = (name: string): any => JSON.parse(readFileSync(new URL(name + '.json', fixtures), 'utf8'));
const clone = <T>(value: T): T => JSON.parse(JSON.stringify(value));
const snapshot = (): Snapshot => frame('sc15-complete-empty-snapshot-reply').body.result.query.output;
const completed = (): OperationSnapshot => frame('sc10-save-verified-result-reply').body.result.query.output.operation.value;
const before = (operation = completed()): DraftSnapshot => {
  if (operation.semantics.capture.kind !== 'save_configuration') throw new Error('fixture');
  return clone(operation.semantics.capture.input.draft);
};
const successor = (operation = completed()): DraftSnapshot => {
  const old = before(operation);
  if (operation.state.status !== 'completed') throw new Error('fixture');
  const outcome = operation.state.outcome;
  return { ...old, draft: { ...old.draft, revision: (BigInt(old.draft.revision) + 1n).toString(),
    document: outcome.kind === 'changed' && outcome.receipt?.kind === 'configuration_written' ? clone(outcome.receipt.document) : old.draft.document },
    edits: [], apply: [], validation: [], state: 'clean' };
};
const input = (draft = before()) => ({ hostEpoch: draft.draft.hostEpoch, draftId: draft.draft.draftId });
const read = (draft: DraftSnapshot, sequence = '2'): GetDraftResult => ({ cursor: { ...snapshot().cursor, sequence },
  draft: { status: 'observed', evidence: frame('sc09-schema-all-field-types-reply').body.result.query.output.evidence, value: clone(draft) } });
const result = (value: GetDraftResult): ClientOutcome<GetDraftResult> => ({ kind: 'result', value,
  request: { requestId: '00009001-1111-4111-8111-111111111111', kind: 'query', method: 'get_draft' } });
const event = (sequence: string, body: Event['body']) => decodeEvent(JSON.stringify({ protocolVersion: 1, cursor: { ...snapshot().cursor, sequence }, body }));
const terminal = (operation: OperationSnapshot): ClientOutcome<OperationSnapshot> => ({ kind: 'result', value: operation,
  request: { requestId: '00009001-1111-4111-8111-111111111111', kind: 'query', method: 'get_operation' } });
function setup(old = before()) {
  const store = new ObservationStore(); expect(store.acceptSnapshot(snapshot())).toBe(true); expect(store.observeDraft(old)).toBe(true);
  const work = new WorkContext(store); expect(work.openDraft(old)).toBe(true); return { store, work };
}

test('completed Save then exact DraftChanged adopts the receipt baseline and clears only captured intent', () => {
  const { store, work } = setup(), capture = work.captureDraftReconciliation()!, saved = completed(), clean = successor(saved);
  expect(store.acceptEvent(event('1', { type: 'operation_changed', operation: saved }))).toBe(true);
  expect(store.acceptEvent(event('2', { type: 'draft_changed', draft: clean }))).toBe(true);
  expect(work.finishDraftReconciliation(capture, result(read(clean)))).toBe(true);
  expect(work.state.draft).toEqual(clean); expect(work.state.edits).toEqual([]); expect(work.state.draftConflict).toBe(false);
  expect(store.state).toMatchObject({ confidence: 'authoritative', cursor: { sequence: '2' } });
});

test('an early clean read defers without a watermark and later receipt reconciliation consumes every event', () => {
  const { store, work } = setup(), old = work.state.draft, clean = successor(), capture = work.captureDraftReconciliation()!;
  expect(store.observeDraftResult(input(), read(clean))).toBe(false);
  expect(store.state).toMatchObject({ confidence: 'authoritative', cursor: { sequence: '0' }, drafts: [old] });
  expect(store.observeOperation(completed())).toBe(true);
  expect(work.finishDraftReconciliation(capture, result(read(clean)))).toBe(true);
  expect(store.state.cursor?.sequence).toBe('0');
  expect(store.acceptEvent(event('1', { type: 'operation_changed', operation: completed() }))).toBe(true);
  expect(store.acceptEvent(event('2', { type: 'draft_changed', draft: clean }))).toBe(true);
  expect(store.state).toMatchObject({ confidence: 'authoritative', cursor: { sequence: '2' }, drafts: [clean] });
});

test.each(['no_receipt', 'document_id', 'target', 'schema', 'digest', 'backup', 'action', 'domain', 'reason'] as const)
('a schema-valid completed Save with mismatched %s cannot authorize baseline adoption', mismatch => {
  const { store, work } = setup(), operation = completed();
  if (operation.state.status !== 'completed' || operation.state.outcome.kind !== 'changed'
    || operation.state.outcome.receipt?.kind !== 'configuration_written') throw new Error('fixture');
  const receipt = operation.state.outcome.receipt;
  if (mismatch === 'no_receipt') operation.state.outcome.receipt = null;
  else if (mismatch === 'document_id') receipt.document.documentId = '00009002-1111-4111-8111-111111111111';
  else if (mismatch === 'target') receipt.document.target.installation.physicalId += '-foreign';
  else if (mismatch === 'schema') receipt.document.schema.schemaVersion = '2.0.0';
  else if (mismatch === 'digest' && receipt.document.baseline.kind === 'existing') receipt.document.baseline.contentDigest = 'sha256:' + 'e'.repeat(64);
  else if (mismatch === 'backup') receipt.backup = frame('sc10-restore-reviewed-backup-reply').body.result.command.output.semantics.capture.input.backup;
  else if (mismatch === 'action') operation.semantics.action = 'restore_configuration';
  else if (mismatch === 'domain') operation.semantics.trustDomain = 'session';
  else if (mismatch === 'reason') operation.state.outcome.reason = 'already_satisfied';
  // Decode the complete event so these controls do not depend on invalid DTOs.
  const decoded = event('1', { type: 'operation_changed', operation });
  expect(store.acceptEvent(decoded)).toBe(false); expect(store.state.operations).toEqual([]);
  const retained = work.state; expect(work.finishDraftReconciliation(work.captureDraftReconciliation()!, result(read(successor())))).toBe(false);
  expect(work.state.draft).toBe(retained.draft); expect(work.state.edits).toBe(retained.edits);
});

test.each(['revision', 'state', 'edits', 'schema', 'document'] as const)
('a completed receipt cannot clear intent using a mismatched clean successor %s', mismatch => {
  const { store, work } = setup(), clean = successor(); expect(store.observeOperation(completed())).toBe(true);
  if (mismatch === 'revision') clean.draft.revision = (BigInt(clean.draft.revision) + 1n).toString();
  else if (mismatch === 'state') clean.state = 'stale';
  else if (mismatch === 'edits') clean.edits = before().edits;
  else if (mismatch === 'schema') clean.schema.fields[0].deprecated = !clean.schema.fields[0].deprecated;
  else if (mismatch === 'document') clean.draft.document.revision += '-foreign';
  event('2', { type: 'draft_changed', draft: clean });
  const retained = work.state; expect(work.finishDraftReconciliation(work.captureDraftReconciliation()!, result(read(clean)))).toBe(false);
  expect(work.state.draft).toBe(retained.draft); expect(work.state.edits).toBe(retained.edits);
});

test.each(['typed_edits', 'unfinished_input', 'changed_generation', 'reentrant_input'] as const)
('receipt reconciliation preserves newer local %s', change => {
  const { store, work } = setup(); expect(store.observeOperation(completed())).toBe(true);
  let capture = work.captureDraftReconciliation()!;
  if (change === 'typed_edits') { expect(work.stage([])).toBe(true); capture = work.captureDraftReconciliation()!; }
  if (change === 'unfinished_input' || change === 'changed_generation') {
    expect(work.setPublicInput(work.capturePublicInput('setting.integer')!, '-')).toBe(true);
    if (change === 'unfinished_input') capture = work.captureDraftReconciliation()!;
  }
  let changed = false;
  const release = store.subscribe(state => {
    if (change === 'reentrant_input' && !changed && state.drafts[0]?.state === 'clean') {
      changed = true; work.setPublicInput(work.capturePublicInput('setting.integer')!, '-');
    }
  });
  const old = work.state.draft, edits = work.state.edits;
  expect(work.finishDraftReconciliation(capture, result(read(successor())))).toBe(false);
  expect(work.state.draft).toBe(old); expect(work.state.edits).toBe(edits);
  if (change !== 'typed_edits') expect(work.state.publicInputs?.[0].text).toBe('-');
  expect(work.state.draftConflict).toBe(true); release();
});

test.each(['changed', 'no_change'] as const)('exact %s completion retires synchronized protected refs without transferring them', kind => {
  const operation = completed(), old = before(operation);
  const reference = frame('sc09-protected-private-entry-reply').body.result.command.output.outcome.reference;
  reference.capturedFor = clone(old.draft); old.edits.push({ kind: 'set_private', fieldId: reference.fieldId, reference });
  if (operation.semantics.capture.kind !== 'save_configuration') throw new Error('fixture');
  operation.semantics.capture.input.draft = old;
  if (kind === 'no_change') operation.state = { status: 'completed', outcome: { kind: 'no_change', reason: 'already_satisfied' } };
  const { store, work } = setup(old), capture = work.captureDraftReconciliation()!, clean = successor(operation);
  expect(store.acceptEvent(event('1', { type: 'operation_changed', operation }))).toBe(true);
  expect(work.finishDraftReconciliation(capture, result(read(clean)))).toBe(true);
  expect(work.state.draft).toEqual(clean); expect(work.state.edits).toEqual([]);
});

test('same-host completed Save retains queued navigation until the exact cleaned successor arrives', () => {
  const { store, work } = setup(); work.requestClose(); const review = work.beginReview()!, operation = completed();
  expect(work.finishSave(review, terminal(operation))).toBe(false);
  expect(work.state).toMatchObject({ transitionBusy: true, closeRequested: false, pendingNavigation: { kind: 'close' } });
  expect(work.state.edits).toEqual(before().edits);
  expect(store.observeDraftResult(input(), read(successor(operation)))).toBe(true);
  expect(work.finishSave(review, terminal(operation))).toBe(true); expect(work.state.closeRequested).toBe(true);
  expect(work.finishSave(review, terminal(operation))).toBe(false);
});

test('identical clean no-change captures remain correlated to the current Save operation ID', () => {
  const first = completed(), old = before(first); old.edits = []; old.apply = []; old.validation = []; old.state = 'clean';
  if (first.semantics.capture.kind !== 'save_configuration') throw new Error('fixture');
  first.semantics.capture.input.draft = old; first.state = { status: 'completed', outcome: { kind: 'no_change', reason: 'already_satisfied' } };
  const second = clone(first); second.operationId = '00009003-1111-4111-8111-111111111111';
  const { store, work } = setup(old); expect(store.observeOperation(first)).toBe(true);
  expect(work.requestTarget(frame('sc09-schema-all-field-types-request').body.query.input.target)).toBe(true);
  work.bindTarget(old.draft.document.target); expect(work.openDraft(old)).toBe(true);
  const review = work.beginReview('in_place')!;
  expect(work.finishSave(review, terminal(second))).toBe(true); expect(work.state.baselineRefreshRequired).toBe(true);
  expect(store.state.operations).toHaveLength(2);
});

test.each(['discard', 'missing'] as const)('receipt evidence cannot resurrect a draft removed by %s', removal => {
  const { store } = setup(); expect(store.observeOperation(completed())).toBe(true);
  if (removal === 'discard') expect(store.observeDiscard({ ...input(), previousRevision: before().draft.revision })).toBe(true);
  else expect(store.observeDraftResult(input(), { ...read(before()), draft: { status: 'missing', evidence: frame('sc09-schema-all-field-types-reply').body.result.query.output.evidence } })).toBe(true);
  expect(store.observeDraftResult(input(), read(successor(), '3'))).toBe(false); expect(store.state.drafts).toEqual([]);
});

test('same-host reconnect uses the retained exact completion without replaying Save or reopening a document', () => {
  const { store, work } = setup(), capture = work.captureDraftReconciliation()!;
  store.invalidate('disconnected'); const current = snapshot(); current.cursor.sequence = '2'; current.operations.items = [completed()];
  expect(store.acceptSnapshot(current)).toBe(true);
  expect(work.finishDraftReconciliation(capture, result(read(successor())))).toBe(true);
  expect(work.state.draft).toEqual(successor()); expect(store.state.cursor?.sequence).toBe('2');
});

test('Restore completion preserves stale draft bindings and protected intent rather than adopting its receipt baseline', () => {
  const operation = completed(); operation.semantics = frame('sc10-restore-reviewed-backup-reply').body.result.command.output.semantics;
  if (operation.semantics.capture.kind !== 'restore_configuration') throw new Error('fixture');
  const capture = operation.semantics.capture.input, document = clone(capture.document);
  document.revision = 'synthetic-restored-document'; document.baseline = { kind: 'existing', contentDigest: capture.backup.retainedDigest, fileIdentity: 'synthetic-restored-file' };
  const backup = { ...clone(capture.backup), document: clone(capture.document), retainedDigest: capture.document.baseline.kind === 'existing' ? capture.document.baseline.contentDigest : '' };
  operation.state = { status: 'completed', outcome: { kind: 'changed', reason: 'applied', receipt: { kind: 'configuration_written', document, backup } } };
  const old = before(), reference = frame('sc09-protected-private-entry-reply').body.result.command.output.outcome.reference;
  reference.capturedFor = clone(old.draft); old.edits.push({ kind: 'set_private', fieldId: reference.fieldId, reference });
  const { store, work } = setup(old), retained = work.captureDraftReconciliation()!, stale = { ...clone(old), state: 'stale' as const, validation: [] };
  expect(store.acceptEvent(event('1', { type: 'operation_changed', operation }))).toBe(true);
  expect(store.acceptEvent(event('2', { type: 'draft_changed', draft: stale }))).toBe(true);
  expect(work.finishDraftReconciliation(retained, result(read(stale)))).toBe(true);
  expect(work.state.draft?.draft).toEqual(old.draft); expect(work.state.edits).toEqual(old.edits); expect(work.state.draftConflict).toBe(true);
});

test.each(['refused', 'old_dirty'] as const)('facade retains Save custody on a %s completion read and retries observation without another mutation', async failure => {
  const operation: OperationSnapshot = frame('sc10-save-restaged-result-reply').body.result.query.output.operation.value;
  let sequence = 0, reads = 0; const methods: string[] = [];
  const client = new BridgeClient({ subscribe: () => () => {}, exchange: async encoded => {
    const request = decodeRequest(encoded), method = request.body.type === 'command' ? request.body.command.name : request.body.query.name;
    methods.push(method); let reply: any;
    if (method === 'set_draft_changes') reply = frame('sc10-restage-dirty-draft-reply');
    else if (method === 'prepare') reply = frame('sc10-save-restaged-draft-reply');
    else if (method === 'commit') {
      reply = frame('sc15-admit-reply'); reply.body.result.command.output = { ...clone(operation), operationRevision: '1', state: { status: 'admitted' } };
    } else if (method === 'get_operation') reply = frame('sc10-save-restaged-result-reply');
    else if (method === 'get_draft') {
      ++reads;
      if (reads === 1 && failure === 'refused') reply = frame('sc10-save-stale-revision-reply');
      else {
        reply = frame('sc08-get-current-clean-draft-reply');
        reply.body.result.query.output = read(reads === 1 ? before(operation) : successor(operation), reads === 1 ? '1' : '2');
      }
    } else throw new Error('unexpected_method');
    reply.requestId = request.requestId; const raw = JSON.stringify(reply); decodeReply(raw); return raw;
  } }, { requestId: () => (++sequence).toString(16).padStart(8, '0') + '-2222-4222-8222-222222222222' });
  const facade = new BridgeFacade(client, { idempotencyKey: () => '00000001-1111-4111-8111-111111111111' });
  try {
    facade.work.observations.acceptSnapshot(snapshot()); facade.work.openDraft(before()); facade.work.observations.observeDraft(before());
    facade.requestClose(); expect(await facade.prepareSave()).toMatchObject({ kind: 'result' });
    expect(await facade.commitSave()).toMatchObject({ kind: 'result' }); await facade.reconcileSave();
    expect(facade.work.state).toMatchObject({ transitionBusy: true, closeRequested: false, pendingNavigation: { kind: 'close' } });
    expect(facade.work.state.edits).toEqual(before(operation).edits);
    const mutations = methods.filter(method => ['prepare', 'commit', 'set_draft_changes'].includes(method));
    expect(mutations).toEqual(['set_draft_changes', 'prepare', 'commit']); expect(reads).toBe(1);
    await facade.reconcileSave(); expect(facade.work.state.closeRequested).toBe(true); expect(facade.state.notice).toBe('Changes saved.');
    expect(reads).toBe(2); expect(methods.filter(method => ['prepare', 'commit', 'set_draft_changes'].includes(method))).toEqual(mutations);
  } finally { facade.dispose(); client.dispose(); }
});
