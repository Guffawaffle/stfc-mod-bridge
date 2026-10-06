import { expect, test } from 'vitest';
import { readFileSync } from 'node:fs';
import { ObservationSession, ObservationStore, counter } from '../src/client/observation';
import { BridgeClient } from '../src/client/client';
import { decodeEvent, decodeReply, decodeRequest, type DeepReadonly } from '../src/client/wire';
import type { CloseDisposition, Event, OperationSnapshot, Snapshot, DraftSnapshot } from '../src/generated/protocol';
import type { RawFrame, RawTransport, TransportFault } from '../src/client/transport';
import { WorkContext } from '../src/client/work-context';

const fixtures = new URL('../../contracts/fixtures/', import.meta.url);
const raw = (id: string) => readFileSync(new URL(id + '.json', fixtures), 'utf8');
function snapshot(id = 'sc15-complete-empty-snapshot-reply'): DeepReadonly<Snapshot> {
  const reply = decodeReply(raw(id));
  if (reply.body.type !== 'result' || reply.body.result.type !== 'query' || reply.body.result.query.name !== 'snapshot') throw new Error(id);
  return reply.body.result.query.output;
}
function operation(id: string): DeepReadonly<OperationSnapshot> {
  const reply = decodeReply(raw(id));
  if (reply.body.type !== 'result') throw new Error(id);
  const result = reply.body.result;
  if (result.type === 'command' && result.command.name === 'commit') return result.command.output;
  if (result.type === 'command' && result.command.name === 'cancel_operation') return result.command.output.operation;
  if (result.type === 'query' && result.query.name === 'get_operation' && result.query.output.operation.status === 'observed') return result.query.output.operation.value;
  throw new Error(id);
}
function event(sequence = '1', change?: (event: any) => void): DeepReadonly<Event> {
  const frame = JSON.parse(raw('sc-03-completed-readiness-event'));
  frame.cursor = { ...snapshot().cursor, sequence };
  change?.(frame); return decodeEvent(JSON.stringify(frame));
}
const metadata = Object.freeze({ requestId: '00000001-1111-4111-8111-111111111111', kind: 'command' as const, method: 'commit' as const });
function publishSavedDraft(work: WorkContext, saved: DeepReadonly<OperationSnapshot>): void {
  const capture = saved.semantics.capture;
  if (capture.kind !== 'save_configuration' || saved.state.status !== 'completed' || saved.state.outcome.kind !== 'changed' || saved.state.outcome.receipt?.kind !== 'configuration_written') throw new Error('fixture');
  work.observations.acceptSnapshot(snapshot());
  work.observations.observeOperation(saved);
  const before = capture.input.draft;
  work.observations.observeDraft({ ...before, draft: { ...before.draft, revision: (BigInt(before.draft.revision) + 1n).toString(), document: saved.state.outcome.receipt.document }, edits: [], apply: [], validation: [], state: 'clean' });
}

test.each(['9007199254740992', '9007199254740993', '18446744073709551615'])('counter remains exact beyond Number range: %s', value => expect(counter(value).toString()).toBe(value));
test.each(['01', '-1', '1.0', '1e3', '18446744073709551616'])('counter refuses %s', value => expect(() => counter(value)).toThrow('counter'));

test('pending operation remains captured across partial snapshot and complete omission faults', () => {
  const store = new ObservationStore();
  store.acceptSnapshot(snapshot());
  const admitted = operation('sc15-admit-reply'); store.observeOperation(admitted);
  expect(store.acceptSnapshot(snapshot('sc15-explicit-partial-snapshot-reply'))).toBe(false);
  expect(store.state).toMatchObject({ confidence: 'partial', resnapshotRequired: true, reason: 'partial_inventory' });
  expect(store.state.operations).toEqual([admitted]);
  expect(store.acceptSnapshot(snapshot())).toBe(false);
  expect(store.state.reason).toBe('pending_operation_omitted');
  expect(store.state.operations[0]).toEqual(admitted);
});

test('complete snapshot can explicitly account for pending work with terminal transition', () => {
  const store = new ObservationStore(); store.observeOperation(operation('sc15-admit-reply'));
  expect(store.acceptSnapshot(snapshot('sc15-complete-terminal-snapshot-reply'))).toBe(true);
  expect(store.state.operations[0].state.status).toBe('completed');
  expect(store.acceptSnapshot(snapshot())).toBe(true);
  expect(store.state.operations).toHaveLength(1);
});

test.each([
  ['sc15-regressed-running-reply', 'revision_regressed', 'sc15-running-reply'],
  ['sc15-reused-revision-state-reply', 'revision_reused', 'sc15-admit-reply'],
  ['sc14-different-physical-capture-reply', 'capture_changed', 'sc15-running-reply'],
])('same resource refuses contradictory observation %s', (fixture, reason, initial) => {
  const store = new ObservationStore(); const previous = operation(initial); store.observeOperation(previous);
  expect(store.observeOperation(operation(fixture))).toBe(false);
  expect(store.state.reason).toBe(reason);
  expect(store.state.operations[0].operationRevision).toBe(previous.operationRevision);
});

test('terminal state stays stable, exact maximum operation revision is accepted', () => {
  const store = new ObservationStore(); store.observeOperation(operation('sc15-terminal-no-change-reply'));
  expect(store.observeOperation(operation('sc15-resurrected-terminal-reply'))).toBe(false);
  expect(store.state.reason).toBe('terminal_changed');
  const maximum = new ObservationStore();
  expect(maximum.observeOperation(operation('sc15-maximum-operation-revision-reply'))).toBe(true);
  expect(maximum.state.operations[0].operationRevision).toBe('18446744073709551615');
});

test('consecutive events, identical duplicate, contradictory duplicate and bounded history', () => {
  const store = new ObservationStore({ maximumRecentEvents: 1 }); store.acceptSnapshot(snapshot());
  expect(store.acceptEvent(event())).toBe(true);
  expect(store.acceptEvent(event())).toBe(false);
  expect(store.state.reason).toBe('duplicate_event');
  const changed = event('1', frame => { frame.body.operation.operationRevision = '4'; });
  expect(store.acceptEvent(changed)).toBe(false); expect(store.state.reason).toBe('contradictory_duplicate');
  const first = event();
  store.acceptSnapshot({ ...snapshot(), cursor: first.cursor, operations: { ...snapshot().operations, items: [first.body.type === 'operation_changed' ? first.body.operation : operation('sc15-admit-reply')] } });
  expect(store.acceptEvent(event('2'))).toBe(true);
  expect(store.acceptEvent(event('3'))).toBe(true);
  expect(store.acceptEvent(event('2'))).toBe(false); expect(store.state.reason).toBe('unverifiable_duplicate');
});

test.each([
  ['sequence_gap', (frame: any) => { frame.cursor.sequence = '2'; }],
  ['epoch_changed', (frame: any) => { frame.cursor.hostEpoch = '00000002-2222-4222-8222-222222222222'; }],
  ['stream_changed', (frame: any) => { frame.cursor.streamId = '00000002-2222-4222-8222-222222222222'; }],
])('stream confidence loss for %s', (reason, change) => {
  const store = new ObservationStore(); store.acceptSnapshot(snapshot());
  expect(store.acceptEvent(event('1', change))).toBe(false);
  expect(store.state).toMatchObject({ confidence: 'stale', resnapshotRequired: true, reason });
  expect(store.state.cursor?.sequence).toBe('0');
});

test('snapshot watermark discards buffered prior events and applies only consecutive later events', () => {
  const store = new ObservationStore(); store.beginSnapshot();
  const first = event(); store.acceptEvent(first); store.acceptEvent(event('2'));
  const op = first.body.type === 'operation_changed' ? first.body.operation : operation('sc15-admit-reply');
  expect(store.acceptSnapshot({ ...snapshot(), cursor: first.cursor, operations: { ...snapshot().operations, items: [op] } })).toBe(true);
  expect(store.state.cursor?.sequence).toBe('2');
  const overflow = new ObservationStore({ maximumBufferedEvents: 1 }); overflow.beginSnapshot(); overflow.acceptEvent(first);
  expect(overflow.acceptEvent(event('2'))).toBe(false);
  expect(overflow.acceptSnapshot(snapshot())).toBe(false); expect(overflow.state.reason).toBe('buffer_limit');
});

test('u64 maximum event sequence compares exactly within scope', () => {
  const store = new ObservationStore();
  store.acceptSnapshot({ ...snapshot(), cursor: { ...snapshot().cursor, sequence: '18446744073709551614' } });
  expect(store.acceptEvent(event('18446744073709551615'))).toBe(true);
  expect(store.state.cursor?.sequence).toBe('18446744073709551615');
});

test('invalidation and hello epoch change demand a fresh snapshot without erasing operations', () => {
  const store = new ObservationStore(); store.acceptSnapshot(snapshot('sc15-complete-running-snapshot-reply'));
  expect(store.acceptEvent(decodeEvent(raw('sc14-event-one')))).toBe(false);
  expect(store.state.reason).toBe('snapshot_invalidated'); expect(store.state.operations).toHaveLength(1);
  store.observeHello('00000002-2222-4222-8222-222222222222'); expect(store.state.reason).toBe('epoch_changed');
});

test('resume binding, maximum count and consecutive event checks refuse gaps/replay', () => {
  const store = new ObservationStore(); store.acceptSnapshot(snapshot());
  const cursor = snapshot().cursor;
  expect(store.acceptBatch({ after: cursor, next: event().cursor, events: [event()] }, cursor, '1')).toBe(true);
  expect(store.acceptBatch({ after: event().cursor, next: event().cursor, events: [event()] }, event().cursor, '1')).toBe(false);
  expect(store.state.reason).toBe('resume_mismatch');
  const gap = new ObservationStore(); gap.acceptSnapshot(snapshot());
  expect(gap.acceptBatch({ after: cursor, next: event('2').cursor, events: [event('2')] }, cursor, '1')).toBe(false);
});

test('operation/draft bounds refuse new state while preserving retained observations', () => {
  const store = new ObservationStore({ maximumOperations: 1 }); store.observeOperation(operation('sc15-admit-reply'));
  const next = event().body;
  if (next.type !== 'operation_changed') throw new Error('fixture');
  expect(store.observeOperation(next.operation)).toBe(false); expect(store.state.reason).toBe('observation_limit');
  expect(store.forgetCompletedOperation(store.state.operations[0].operationId)).toBe(false);
});

test('close ready and stale close obligations cannot erase pending or partial evidence', () => {
  const store = new ObservationStore(); store.acceptSnapshot(snapshot('sc15-complete-running-snapshot-reply'));
  expect(store.observeClose({ kind: 'ready' })).toBe(false);
  expect(store.state.reason).toBe('close_obligation_mismatch');
  const reply = decodeReply(raw('sc15-close-deferred-reply'));
  if (reply.body.type !== 'result' || reply.body.result.type !== 'command' || reply.body.result.command.name !== 'request_host_close') throw new Error('fixture');
  expect(store.observeClose(reply.body.result.command.output)).toBe(false);
  const fresh = new ObservationStore(); fresh.acceptSnapshot(snapshot()); fresh.observeOperation(operation('sc15-cancel-requested-reply'));
  expect(fresh.observeClose(reply.body.result.command.output)).toBe(true);
});

test('deferred close covers safe recovery together with session custody or another running operation', () => {
  const recovery = operation('sc15-post-restart-recovery-reply');
  const runningFrame = JSON.parse(raw('sc15-running-reply'));
  runningFrame.body.result.query.output.operation.value.operationId = '00000002-2222-4222-8222-222222222222';
  const runningReply = decodeReply(JSON.stringify(runningFrame));
  if (runningReply.body.type !== 'result' || runningReply.body.result.type !== 'query'
    || runningReply.body.result.query.name !== 'get_operation' || runningReply.body.result.query.output.operation.status !== 'observed') throw new Error('fixture');
  const running = runningReply.body.result.query.output.operation.value;
  const session = JSON.parse(raw('sc-04-focus-exact-session-prepare-request')).body.command.input.intent.input.session;
  const obligation = { kind: 'operation' as const, operationId: recovery.operationId, operationRevision: recovery.operationRevision };
  const checkedClose = (output: CloseDisposition): DeepReadonly<CloseDisposition> => {
    const frame = JSON.parse(raw('sc15-close-deferred-reply')); frame.body.result.command.output = output;
    const reply = decodeReply(JSON.stringify(frame));
    if (reply.body.type !== 'result' || reply.body.result.type !== 'command' || reply.body.result.command.name !== 'request_host_close') throw new Error('fixture');
    return reply.body.result.command.output;
  };
  for (const withRunning of [false, true]) {
    const other = withRunning ? { kind: 'operation' as const, operationId: running.operationId, operationRevision: running.operationRevision }
      : { kind: 'session_custody' as const, session };
    const setup = () => {
      const store = new ObservationStore(); store.acceptSnapshot(snapshot()); store.observeOperation(recovery);
      if (withRunning) store.observeOperation(running);
      return store;
    };
    const complete = checkedClose({ kind: 'deferred', obligations: [obligation, other] });
    const store = setup(); expect(store.observeClose(complete)).toBe(true);
    expect(store.state.operations).toContainEqual(recovery);
    expect(store.state.operations).toHaveLength(withRunning ? 2 : 1);
    const frame = JSON.parse(raw('sc15-close-deferred-event'));
    frame.cursor = { ...snapshot().cursor, sequence: '1' }; frame.body.obligations = [obligation, other];
    expect(setup().acceptEvent(decodeEvent(JSON.stringify(frame)))).toBe(true);
    for (const obligations of [[other], [{ ...obligation, operationRevision: '2' }, other]]) {
      const incomplete = setup();
      expect(incomplete.observeClose(checkedClose({ kind: 'deferred', obligations }))).toBe(false);
      expect(incomplete.state).toMatchObject({ reason: 'close_obligation_mismatch', resnapshotRequired: true });
      expect(incomplete.state.operations).toContainEqual(recovery);
    }
    const refused = setup(); expect(refused.observeClose({ kind: 'ready' })).toBe(false);
  }
});

test('operation capture equivalence uses the same optional and default serde normalization', () => {
  const original = operation('sc15-admit-reply');
  const frame = JSON.parse(raw('sc15-admit-reply'));
  const capture = frame.body.result.command.output.semantics.capture;
  capture.unrecognizedRuntimeChoice = 'reject'; capture.target.profile.ordinaryId = null;
  const reply = decodeReply(JSON.stringify(frame));
  if (reply.body.type !== 'result' || reply.body.result.type !== 'command' || reply.body.result.command.name !== 'commit') throw new Error('fixture');
  const store = new ObservationStore(); expect(store.observeOperation(original)).toBe(true);
  expect(store.observeOperation(reply.body.result.command.output)).toBe(true);
  capture.target.installation.physicalId = 'other-physical-installation';
  const changed = decodeReply(JSON.stringify(frame));
  if (changed.body.type !== 'result' || changed.body.result.type !== 'command' || changed.body.result.command.name !== 'commit') throw new Error('fixture');
  expect(store.observeOperation(changed.body.result.command.output)).toBe(false);
  expect(store.state.reason).toBe('capture_changed');
});

test('snapshot subscription precedes query and disconnected late snapshot cannot certify stream', async () => {
  const order: string[] = []; let emit!: (frame: RawFrame) => void; let fault!: (fault: TransportFault) => void; let resolve!: (frame: RawFrame) => void; let sent = '';
  const transport: RawTransport = {
    subscribe(onEvent, onFault) { order.push('subscribe'); emit = onEvent; fault = onFault!; return () => { order.push('unsubscribe'); }; },
    exchange(request) { order.push('snapshot'); sent = request; return new Promise(yes => { resolve = yes; }); },
  };
  const client = new BridgeClient(transport, { requestId: () => metadata.requestId }); const session = new ObservationSession(client);
  const pending = session.start(); expect(order).toEqual(['subscribe', 'snapshot']);
  emit(JSON.stringify(event())); fault({ code: 'disconnected', delivery: 'may_have_reached_backend' });
  resolve(JSON.stringify({ ...JSON.parse(raw('sc15-complete-empty-snapshot-reply')), requestId: decodeRequest(sent).requestId }));
  await pending; expect(session.store.state.confidence).toBe('stale'); expect(order).toContain('unsubscribe'); session.dispose();
});

test('synchronous subscription failure cannot anchor a snapshot and next start registers a fresh observer', async () => {
  let sequence = 0; let registrations = 0; let snapshots = 0; let releases = 0; const active = new Set<number>();
  const transport: RawTransport = {
    subscribe(_onEvent, onFault) {
      const registration = ++registrations; active.add(registration);
      if (registration === 1) onFault!({ code: 'disconnected', delivery: 'not_sent' });
      return () => { releases++; active.delete(registration); };
    },
    exchange(encoded) {
      snapshots++;
      return Promise.resolve(JSON.stringify({ ...JSON.parse(raw('sc15-complete-empty-snapshot-reply')), requestId: decodeRequest(encoded).requestId }));
    },
  };
  const client = new BridgeClient(transport, { requestId: () => (++sequence).toString(16).padStart(8, '0') + '-2222-4222-8222-222222222222' });
  const session = new ObservationSession(client);
  expect(await session.start()).toMatchObject({ kind: 'fault', fault: { code: 'disconnected', delivery: 'not_sent' } });
  expect(registrations).toBe(1); expect(snapshots).toBe(0); expect(releases).toBe(1); expect(active.size).toBe(0);
  expect(session.store.state.confidence).toBe('stale');
  expect(await session.refresh()).toMatchObject({ kind: 'fault', fault: { code: 'stale_stream', delivery: 'not_sent' } }); expect(snapshots).toBe(0);
  expect(await session.start()).toMatchObject({ kind: 'result' });
  expect(registrations).toBe(2); expect(snapshots).toBe(1); expect(active.size).toBe(1); expect(session.store.state.confidence).toBe('authoritative');
  session.dispose(); client.dispose(); expect(releases).toBe(2); expect(active.size).toBe(0);
});

test('synchronous registration events stay buffered until the authoritative snapshot watermark', async () => {
  const transport: RawTransport = {
    subscribe(onEvent) { onEvent(JSON.stringify(event())); return () => {}; },
    exchange(encoded) { return Promise.resolve(JSON.stringify({ ...JSON.parse(raw('sc15-complete-empty-snapshot-reply')), requestId: decodeRequest(encoded).requestId })); },
  };
  const client = new BridgeClient(transport, { requestId: () => metadata.requestId }); const session = new ObservationSession(client);
  expect(await session.start()).toMatchObject({ kind: 'result' });
  expect(session.store.state.confidence).toBe('authoritative'); expect(session.store.state.cursor?.sequence).toBe('1');
  expect(session.store.state.operations).toHaveLength(1); session.dispose(); client.dispose();
});

test('session setup guards reentrant start and disposal before raw subscription returns', async () => {
  let snapshots = 0; let registrations = 0; let releases = 0; let nested!: Promise<unknown>;
  const transport: RawTransport = {
    subscribe(_onEvent, onFault) {
      registrations++; onFault!({ code: 'disconnected', delivery: 'not_sent' }); return () => { releases++; };
    },
    exchange() { snapshots++; return Promise.resolve(''); },
  };
  const client = new BridgeClient(transport, { requestId: () => metadata.requestId }); const session = new ObservationSession(client);
  let attempted = false;
  const unsubscribe = session.store.subscribe(state => {
    if (state.confidence === 'stale' && !attempted) { attempted = true; nested = session.start(); session.dispose(); }
  });
  expect(await session.start()).toMatchObject({ kind: 'fault', fault: { code: 'disconnected' } });
  expect(await nested).toMatchObject({ kind: 'fault', fault: { code: 'stale_stream', delivery: 'not_sent' } });
  expect(registrations).toBe(1); expect(releases).toBe(1); expect(snapshots).toBe(0); unsubscribe(); client.dispose();
});

test('old resume reply cannot invalidate a new epoch snapshot after reconnect', async () => {
  let sequence = 0; let snapshots = 0; let resumeRequest = '';
  let resolveResume!: (frame: RawFrame) => void; let disconnect!: (fault: TransportFault) => void;
  const restart = JSON.parse(raw('sc15-forced-restart-observes-recovery.transcript')).steps.find((step: any) => step.type === 'boundary' && step.reason === 'restart').cursor;
  const transport: RawTransport = {
    subscribe(_onEvent, onFault) { disconnect = onFault!; return () => {}; },
    exchange(encoded) {
      const request = decodeRequest(encoded);
      if (request.body.type !== 'query') throw new Error('query fixture');
      if (request.body.query.name === 'resume_events') { resumeRequest = encoded; return new Promise(resolve => { resolveResume = resolve; }); }
      const frame = JSON.parse(raw('sc15-complete-empty-snapshot-reply')); frame.requestId = request.requestId;
      if (++snapshots > 1) frame.body.result.query.output.cursor = restart;
      return Promise.resolve(JSON.stringify(frame));
    },
  };
  const client = new BridgeClient(transport, { requestId: () => (++sequence).toString(16).padStart(8, '0') + '-2222-4222-8222-222222222222' });
  const session = new ObservationSession(client); await session.start();
  const resumed = session.resume(); disconnect({ code: 'disconnected', delivery: 'may_have_reached_backend' });
  await session.start(); const before = session.store.state;
  expect(before.confidence).toBe('authoritative'); expect(before.cursor).toEqual(restart);
  const request = decodeRequest(resumeRequest);
  if (request.body.type !== 'query' || request.body.query.name !== 'resume_events') throw new Error('query fixture');
  const reply = JSON.parse(raw('sc14-contiguous-event-batch-reply')); reply.requestId = request.requestId;
  reply.body.result.query.output.after = request.body.query.input.after;
  reply.body.result.query.output.events = []; reply.body.result.query.output.next = request.body.query.input.after;
  resolveResume(JSON.stringify(reply));
  expect(await resumed).toMatchObject({ kind: 'fault', fault: { code: 'stale_stream', delivery: 'may_have_reached_backend' } });
  expect(session.store.state).toEqual(before); session.dispose(); client.dispose();
});

test('disposed session abandons a pending resume observation without reconciling it', async () => {
  let sequence = 0; let resumeRequest = ''; let resolveResume!: (frame: RawFrame) => void;
  const transport: RawTransport = {
    subscribe() { return () => {}; },
    exchange(encoded) {
      const request = decodeRequest(encoded);
      if (request.body.type !== 'query') throw new Error('query fixture');
      if (request.body.query.name === 'resume_events') { resumeRequest = encoded; return new Promise(resolve => { resolveResume = resolve; }); }
      return Promise.resolve(JSON.stringify({ ...JSON.parse(raw('sc15-complete-empty-snapshot-reply')), requestId: request.requestId }));
    },
  };
  const client = new BridgeClient(transport, { requestId: () => (++sequence).toString(16).padStart(8, '0') + '-2222-4222-8222-222222222222' });
  const session = new ObservationSession(client); await session.start(); const resumed = session.resume(); session.dispose();
  const before = session.store.state;
  const request = decodeRequest(resumeRequest);
  if (request.body.type !== 'query' || request.body.query.name !== 'resume_events') throw new Error('query fixture');
  const reply = JSON.parse(raw('sc14-contiguous-event-batch-reply')); reply.requestId = request.requestId;
  reply.body.result.query.output.after = request.body.query.input.after;
  reply.body.result.query.output.events = []; reply.body.result.query.output.next = request.body.query.input.after;
  resolveResume(JSON.stringify(reply));
  expect(await resumed).toMatchObject({ kind: 'fault', fault: { code: 'disposed', delivery: 'may_have_reached_backend' } });
  expect(session.store.state).toEqual(before);
  expect(await session.resume()).toMatchObject({ kind: 'fault', fault: { code: 'disposed', delivery: 'not_sent' } }); client.dispose();
});

function draft(id: string): DeepReadonly<DraftSnapshot> {
  const reply = decodeReply(raw(id));
  if (reply.body.type !== 'result' || reply.body.result.type !== 'command' || !['open_draft', 'set_draft_changes'].includes(reply.body.result.command.name)) throw new Error(id);
  const command = reply.body.result.command;
  if (command.name === 'set_draft_changes') return command.output.snapshot;
  return command.output as DeepReadonly<DraftSnapshot>;
}

test('draft observations accept typed None equivalence without erasing local edits or document identity', () => {
  const original = draft('sc08-stage-dirty-draft-reply');
  const frame = JSON.parse(raw('sc08-stage-dirty-draft-reply'));
  frame.body.result.command.output.snapshot.draft.document.target.profile.ordinaryId = null;
  const reply = decodeReply(JSON.stringify(frame));
  if (reply.body.type !== 'result' || reply.body.result.type !== 'command' || reply.body.result.command.name !== 'set_draft_changes') throw new Error('fixture');
  const equivalent = reply.body.result.command.output.snapshot;
  const store = new ObservationStore(); store.observeDraft(original);
  expect(store.observeDraft(equivalent)).toBe(true);
  const work = new WorkContext(); work.openDraft(original); const before = work.state;
  expect(work.observeDraft(equivalent)).toBe(true);
  expect(work.state.draftConflict).toBe(false); expect(work.state.edits).toBe(before.edits);
  frame.body.result.command.output.snapshot.draft.document.documentId = '00000002-2222-4222-8222-222222222222';
  const wrong = decodeReply(JSON.stringify(frame));
  if (wrong.body.type !== 'result' || wrong.body.result.type !== 'command' || wrong.body.result.command.name !== 'set_draft_changes') throw new Error('fixture');
  expect(store.observeDraft(wrong.body.result.command.output.snapshot)).toBe(false); expect(store.state.reason).toBe('capture_changed');
  expect(work.observeDraft(wrong.body.result.command.output.snapshot)).toBe(false);
  expect(work.state.draftConflict).toBe(true); expect(work.state.edits).toBe(before.edits);
});

test('terminal operation state accepts omitted None while preserving actual receipt revisions', () => {
  const original = operation('sc10-save-verified-result-reply');
  const frame = JSON.parse(raw('sc10-save-verified-result-reply'));
  const value = frame.body.result.query.output.operation.value;
  value.state.outcome.receipt.backup = null;
  value.state.outcome.receipt.document.target.profile.ordinaryId = null;
  const reply = decodeReply(JSON.stringify(frame));
  if (reply.body.type !== 'result' || reply.body.result.type !== 'query' || reply.body.result.query.name !== 'get_operation'
    || reply.body.result.query.output.operation.status !== 'observed') throw new Error('fixture');
  const store = new ObservationStore(); store.observeOperation(original);
  expect(store.observeOperation(reply.body.result.query.output.operation.value)).toBe(true);
  value.state.outcome.receipt.document.revision = 'other-document-revision';
  const changed = decodeReply(JSON.stringify(frame));
  if (changed.body.type !== 'result' || changed.body.result.type !== 'query' || changed.body.result.query.name !== 'get_operation'
    || changed.body.result.query.output.operation.status !== 'observed') throw new Error('fixture');
  expect(store.observeOperation(changed.body.result.query.output.operation.value)).toBe(false);
  expect(store.state.reason).toBe('terminal_changed');
});

test('recovery close accepts typed None equivalence while retaining exact transaction binding', () => {
  const recovered = operation('sc15-post-restart-recovery-reply');
  const frame = JSON.parse(raw('sc15-close-recovery-required-reply'));
  frame.body.result.command.output.recoveries[0].target.target.profile.ordinaryId = null;
  const reply = decodeReply(JSON.stringify(frame));
  if (reply.body.type !== 'result' || reply.body.result.type !== 'command' || reply.body.result.command.name !== 'request_host_close') throw new Error('fixture');
  const store = new ObservationStore(); store.acceptSnapshot(snapshot()); store.observeOperation(recovered);
  expect(store.observeClose(reply.body.result.command.output)).toBe(true);
  frame.body.result.command.output.recoveries[0].transaction = 'other-native-transaction';
  const changed = decodeReply(JSON.stringify(frame));
  if (changed.body.type !== 'result' || changed.body.result.type !== 'command' || changed.body.result.command.name !== 'request_host_close') throw new Error('fixture');
  expect(store.observeClose(changed.body.result.command.output)).toBe(false);
  expect(store.state.reason).toBe('close_obligation_mismatch'); expect(store.state.operations).toEqual([recovered]);
});

test('discard receipt removes only the exact observed draft and retains bounded revision history', () => {
  const store = new ObservationStore({ maximumDrafts: 1 }); const existing = draft('sc08-stage-dirty-draft-reply'); store.observeDraft(existing);
  const reply = decodeReply(raw('sc08-discard-draft-reply'));
  if (reply.body.type !== 'result' || reply.body.result.type !== 'command' || reply.body.result.command.name !== 'discard_draft') throw new Error('discard fixture');
  const receipt = reply.body.result.command.output;
  expect(store.observeDiscard({ ...receipt, hostEpoch: '00000002-2222-4222-8222-222222222222' })).toBe(true);
  expect(store.state.drafts).toEqual([existing]);
  expect(store.observeDiscard(receipt)).toBe(true); expect(store.state.drafts).toEqual([]);
  expect(store.observeDiscard(receipt)).toBe(true);
  expect(store.observeDraft(existing)).toBe(false); expect(store.state.reason).toBe('revision_reused');
  const frame = JSON.parse(raw('sc08-stage-dirty-draft-reply')); frame.body.result.command.output.snapshot.draft.draftId = '00000002-2222-4222-8222-222222222222';
  const nextReply = decodeReply(JSON.stringify(frame));
  if (nextReply.body.type !== 'result' || nextReply.body.result.type !== 'command' || nextReply.body.result.command.name !== 'set_draft_changes') throw new Error('draft fixture');
  expect(store.observeDraft(nextReply.body.result.command.output.snapshot)).toBe(false); expect(store.state.reason).toBe('observation_limit');
  expect(store.forgetDiscardedDraft(receipt.hostEpoch, receipt.draftId)).toBe(true);
  expect(store.observeDraft(nextReply.body.result.command.output.snapshot)).toBe(true);
});

test('older discard receipt cannot remove a newer observed draft revision', () => {
  const store = new ObservationStore();
  const frame = JSON.parse(raw('sc08-stage-dirty-draft-reply')); frame.body.result.command.output.snapshot.draft.revision = '3';
  const reply = decodeReply(JSON.stringify(frame));
  if (reply.body.type !== 'result' || reply.body.result.type !== 'command' || reply.body.result.command.name !== 'set_draft_changes') throw new Error('draft fixture');
  const newer = reply.body.result.command.output.snapshot; store.observeDraft(newer);
  expect(store.observeDiscard({ draftId: newer.draft.draftId, hostEpoch: newer.draft.hostEpoch, previousRevision: '2' })).toBe(false);
  expect(store.state.drafts).toEqual([newer]); expect(store.state.reason).toBe('discard_revision_mismatch');
});
const selector = () => {
  const request = decodeRequest(raw('sc-01-resolve-explicit-ordinary-request'));
  if (request.body.type !== 'query' || request.body.query.name !== 'resolve_target') throw new Error('fixture');
  return request.body.query.input.target;
};

test('view navigation preserves draft and one target transition; failed save retains everything', () => {
  const work = new WorkContext(); work.requestTarget(selector()); work.openDraft(draft('sc08-stage-dirty-draft-reply'));
  work.navigate('settings', 'save-button'); const before = work.state; work.navigate('data_sync');
  expect(work.state.draft).toEqual(before.draft); expect(work.state.edits).toEqual(before.edits);
  expect(work.requestTarget(selector(), 'profile-picker')).toBe(false); expect(work.requestClose()).toBe(false);
  expect(work.state.pendingNavigation?.kind).toBe('target');
  const review = work.beginReview()!; expect(work.stage([])).toBe(false);
  expect(work.finishSave(review, { kind: 'fault', fault: { code: 'timeout', delivery: 'may_have_reached_backend' } })).toBe(false);
  expect(work.state.dirty).toBe(true); expect(work.state.selector).toEqual(before.selector); expect(work.state.edits).toEqual(before.edits);
  expect(work.stay()).toBe(true); expect(work.state.focusKey).toBe('profile-picker');
});

test('discard requires exact backend draft acknowledgement and only applies queued transition', () => {
  const work = new WorkContext(); work.requestTarget(selector()); work.openDraft(draft('sc08-stage-dirty-draft-reply')); work.requestClose();
  const review = work.beginReview()!;
  const reply = decodeReply(raw('sc08-discard-draft-reply'));
  if (reply.body.type !== 'result' || reply.body.result.type !== 'command' || reply.body.result.command.name !== 'discard_draft') throw new Error('fixture');
  const value = reply.body.result.command.output;
  expect(work.finishDiscard(review, { kind: 'result', value: { ...value, previousRevision: '1' }, request: metadata })).toBe(false);
  expect(work.state.draft).toBeDefined();
  expect(work.finishDiscard(work.beginReview()!, { kind: 'result', value, request: metadata })).toBe(true);
  expect(work.state.closeRequested).toBe(true); expect(work.state.draft).toBeUndefined();
});

test('save applies selection only after exact reviewed draft has completed authoritatively', () => {
  const saved = operation('sc10-save-verified-result-reply'); const capture = saved.semantics.capture;
  if (capture.kind !== 'save_configuration') throw new Error('fixture');
  const work = new WorkContext(); work.requestTarget(selector()); work.openDraft(capture.input.draft); work.requestClose();
  const review = work.beginReview()!;
  expect(work.finishSave(review, { kind: 'result', value: { ...saved, state: { status: 'admitted' } }, request: metadata })).toBe(false);
  expect(work.state.closeRequested).toBe(false); expect(work.state.draft).toBeDefined();
  publishSavedDraft(work, saved);
  expect(work.finishSave(work.beginReview()!, { kind: 'result', value: saved, request: metadata })).toBe(true);
  expect(work.state.closeRequested).toBe(true); expect(work.observations.state.operations).toEqual([saved]);
});

test('save reconciles typed DraftRef None but keeps captured edits exact', () => {
  const saved = operation('sc10-save-verified-result-reply');
  const frame = JSON.parse(raw('sc10-save-verified-result-reply'));
  frame.body.result.query.output.operation.value.semantics.capture.input.draft.draft.document.target.profile.ordinaryId = null;
  const reply = decodeReply(JSON.stringify(frame));
  if (reply.body.type !== 'result' || reply.body.result.type !== 'query' || reply.body.result.query.name !== 'get_operation'
    || reply.body.result.query.output.operation.status !== 'observed' || reply.body.result.query.output.operation.value.semantics.capture.kind !== 'save_configuration') throw new Error('fixture');
  const backendDraft = reply.body.result.query.output.operation.value.semantics.capture.input.draft;
  const work = new WorkContext(); work.openDraft(backendDraft); work.requestClose();
  publishSavedDraft(work, saved);
  expect(work.finishSave(work.beginReview()!, { kind: 'result', value: saved, request: metadata })).toBe(true);
  expect(work.state.closeRequested).toBe(true);
  const edited = new WorkContext(); edited.openDraft(backendDraft); expect(backendDraft.edits.length).toBeGreaterThan(0);
  edited.stage([]); edited.requestClose();
  expect(edited.finishSave(edited.beginReview()!, { kind: 'result', value: saved, request: metadata })).toBe(false);
  expect(edited.state.closeRequested).toBe(false); expect(edited.state.draft).toBeDefined(); expect(edited.state.edits).toEqual([]);
});

const replacementHostEpoch = '00000002-2222-4222-8222-222222222222';
const localDraftEdits = () => decodeRequest(raw('sc08-stage-dirty-draft-request'));
function hostBoundWork() {
  const store = new ObservationStore(); store.acceptSnapshot(snapshot());
  const work = new WorkContext(store), clean = draft('sc08-open-clean-draft-reply');
  work.requestTarget(selector()); work.bindTarget(clean.draft.document.target); work.openDraft(clean);
  const request = localDraftEdits();
  if (request.body.type !== 'command' || request.body.command.name !== 'set_draft_changes') throw new Error('fixture');
  work.stage(request.body.command.input.edits);
  return { store, work, clean };
}

test('known host invalidation freezes typed public and protected edits while retaining exact custody', () => {
  const { store, work } = hostBoundWork();
  expect(work.setPublicInput(work.capturePublicInput('setting.integer')!, '-')).toBe(true);
  const publicBinding = work.capturePublicInput('setting.integer')!, protectedBinding = work.captureProtectedInput('sync.endpoint')!;
  const before = work.state, conflicts: boolean[] = [];
  const stop = work.subscribe(value => conflicts.push(value.draftConflict));
  store.observeHello(replacementHostEpoch);
  expect(store.state.cursor?.hostEpoch).toBe(before.draft?.draft.hostEpoch);
  expect(work.state.draftConflict).toBe(true); expect(work.state.draftHostChanged).toBe(true); expect(conflicts.at(-1)).toBe(true);
  expect(work.stage([])).toBe(false); expect(work.capturePublicInput('setting.integer')).toBeUndefined();
  expect(work.captureProtectedInput('sync.endpoint')).toBeUndefined();
  expect(work.setPublicInput(publicBinding, '2')).toBe(false);
  expect(work.stagePublicInput(publicBinding, '-', [])).toBe(false); expect(work.resetPublicInput(publicBinding, '-')).toBe(false);
  const privateReply = decodeReply(raw('sc09-protected-private-entry-reply'));
  if (privateReply.body.type !== 'result' || privateReply.body.result.type !== 'command' || privateReply.body.result.command.name !== 'request_sensitive_input'
    || privateReply.body.result.command.output.outcome.status !== 'captured_private') throw new Error('fixture');
  expect(work.stageProtectedInput(protectedBinding, { kind: 'set_private', fieldId: 'sync.endpoint',
    reference: { ...privateReply.body.result.command.output.outcome.reference, document: protectedBinding.draft.document, capturedFor: protectedBinding.draft } })).toBe(false);
  expect(work.beginReview('in_place')).toBeUndefined(); work.requestClose(); expect(work.beginReview()).toBeUndefined();
  store.invalidate('disconnected');
  expect(work.state.draftConflict).toBe(true); expect(work.capturePublicInput('setting.integer')).toBeUndefined();
  expect(work.state.draft).toBe(before.draft); expect(work.state.edits).toBe(before.edits); expect(work.state.publicInputs).toBe(before.publicInputs);
  expect(work.state.publicInputs?.[0].text).toBe('-'); expect(work.state.closeRequested).toBe(false); stop();
});

test('old host draft synchronization cannot adopt a matching late acknowledgement', () => {
  const { store, work } = hostBoundWork(); work.requestClose();
  const review = work.beginReview()!, before = work.state;
  const reply = decodeReply(raw('sc08-stage-dirty-draft-reply'));
  if (reply.body.type !== 'result' || reply.body.result.type !== 'command' || reply.body.result.command.name !== 'set_draft_changes') throw new Error('fixture');
  store.observeHello(replacementHostEpoch);
  expect(work.finishDraftSynchronization(review, { kind: 'result', value: reply.body.result.command.output,
    request: { ...metadata, method: 'set_draft_changes' } })).toBeUndefined();
  expect(work.state.draft).toBe(before.draft); expect(work.state.edits).toBe(before.edits);
  expect(work.state.pendingNavigation).toBe(before.pendingNavigation); expect(work.state.transitionBusy).toBe(false); expect(work.state.draftConflict).toBe(true);
});

test('old host Discard acknowledgement cannot clear exact typed edits or raw numeric text', () => {
  const { store, work } = hostBoundWork(); work.setPublicInput(work.capturePublicInput('setting.integer')!, '-'); work.requestClose();
  const review = work.beginReview()!, before = work.state;
  store.observeHello(replacementHostEpoch);
  expect(work.finishDiscard(review, { kind: 'result', value: { draftId: review.draft.draft.draftId,
    hostEpoch: review.draft.draft.hostEpoch, previousRevision: review.draft.draft.revision }, request: { ...metadata, method: 'discard_draft' } })).toBe(false);
  expect(work.state.draft).toBe(before.draft); expect(work.state.edits).toBe(before.edits); expect(work.state.publicInputs).toBe(before.publicInputs);
  expect(work.state.pendingNavigation).toBe(before.pendingNavigation); expect(work.state.closeRequested).toBe(false); expect(work.state.transitionBusy).toBe(false);
});

test('completed admitted Save remains reconcilable after its draft host is replaced', () => {
  const saved = operation('sc10-save-verified-result-reply'), capture = saved.semantics.capture;
  if (capture.kind !== 'save_configuration') throw new Error('fixture');
  const store = new ObservationStore(); store.acceptSnapshot(snapshot());
  const work = new WorkContext(store); work.requestTarget(selector()); work.openDraft(capture.input.draft); work.requestClose();
  const review = work.beginReview()!;
  store.observeOperation({ ...saved, operationRevision: '1', state: { status: 'admitted' } }); store.observeHello(replacementHostEpoch);
  expect(work.state.draftConflict).toBe(true);
  expect(work.finishSave(review, { kind: 'result', value: saved, request: metadata })).toBe(true);
  expect(work.state.closeRequested).toBe(true); expect(work.state.draft).toBeUndefined(); expect(store.state.operations).toEqual([saved]);
});

test('replacement host clean drafts restore edit capability without replacing retained dirty drafts', () => {
  const { store, work, clean } = hostBoundWork(); work.setPublicInput(work.capturePublicInput('setting.integer')!, '-');
  const before = work.state, replacement = { ...snapshot(), cursor: { ...snapshot().cursor, hostEpoch: replacementHostEpoch } };
  store.observeHello(replacementHostEpoch); expect(store.acceptSnapshot(replacement)).toBe(true);
  const currentDraft = { ...clean, draft: { ...clean.draft, hostEpoch: replacementHostEpoch } };
  expect(work.openDraft(currentDraft)).toBe(false); expect(work.state.edits).toBe(before.edits); expect(work.state.publicInputs).toBe(before.publicInputs);
  const fresh = new WorkContext(store); expect(fresh.openDraft(currentDraft)).toBe(true);
  expect(fresh.state.draftConflict).toBe(false); expect(fresh.capturePublicInput('setting.integer')).toBeDefined(); expect(fresh.stage([])).toBe(true);
  expect(work.openDraft(clean)).toBe(false); expect(work.state.draftConflict).toBe(true);
});

test('ordinary document conflicts remain distinct from a replaced draft host', () => {
  const { store, work, clean } = hostBoundWork();
  work.bindTarget({ ...clean.draft.document.target, installation: { ...clean.draft.document.target.installation, physicalId: 'synthetic-foreign-physical' } });
  expect(work.state.draftConflict).toBe(true); expect(work.state.draftHostChanged).toBe(false);
  store.observeHello(replacementHostEpoch);
  expect(work.state.draftConflict).toBe(true); expect(work.state.draftHostChanged).toBe(true);
});
