import { readFileSync } from 'node:fs';
import { expect, test } from 'vitest';
import { BridgeClient, ObservationStore, WorkContext, canonicalData, decodeEvent, decodeReply, decodeRequest, type ClientOutcome } from '../src/client';
import type { ConfigurationEdit, DraftSnapshot, Event, Evidence, GetDraftResult, OperationSnapshot, Request, Snapshot } from '../src/generated/protocol';
import type { TransportFault } from '../src/client/transport';
import { BridgeFacade } from '../src/state';

const fixtures = new URL('../../contracts/fixtures/', import.meta.url);
const frame = (name: string): any => JSON.parse(readFileSync(new URL(name + '.json', fixtures), 'utf8'));
const clone = <T>(value: T): T => JSON.parse(JSON.stringify(value));
const clean = (): DraftSnapshot => frame('sc08-open-clean-draft-reply').body.result.command.output;
const staged = (): DraftSnapshot => frame('sc08-stage-dirty-draft-reply').body.result.command.output.snapshot;
const snapshot = (): Snapshot => frame('sc15-complete-empty-snapshot-reply').body.result.query.output;
const evidence = (): Evidence => frame('sc09-schema-all-field-types-reply').body.result.query.output.evidence;
const operation = (): OperationSnapshot => frame('sc15-admit-reply').body.result.command.output;
const input = () => ({ hostEpoch: clean().draft.hostEpoch, draftId: clean().draft.draftId });
const read = (draft: DraftSnapshot | undefined, sequence = '0'): GetDraftResult => ({ cursor: { ...snapshot().cursor, sequence },
  draft: draft ? { status: 'observed', evidence: evidence(), value: clone(draft) } : { status: 'missing', evidence: evidence() } });
const result = (value: GetDraftResult): ClientOutcome<GetDraftResult> => ({ kind: 'result', value,
  request: { requestId: '00009001-1111-4111-8111-111111111111', kind: 'query', method: 'get_draft' } });
const event = (sequence: string, body: Event['body']) => decodeEvent(JSON.stringify({ protocolVersion: 1, cursor: { ...snapshot().cursor, sequence }, body }));
const draftEvent = (sequence: string, draft: DraftSnapshot) => event(sequence, { type: 'draft_changed', draft });

test('a current draft read fences only draft payloads and consumes every intervening operation event', () => {
  const store = new ObservationStore(); store.acceptSnapshot(snapshot()); store.observeDraft(clean());
  const successor = staged(); successor.draft.revision = '3';
  expect(store.observeDraftResult(input(), read(successor, '3'))).toBe(true);
  expect(store.state.cursor?.sequence).toBe('0');
  expect(store.acceptEvent(draftEvent('1', staged()))).toBe(true);
  expect(store.state.cursor?.sequence).toBe('1'); expect(store.state.drafts).toEqual([successor]);
  const admitted = operation();
  expect(store.acceptEvent(event('2', { type: 'operation_changed', operation: admitted }))).toBe(true);
  expect(store.state.operations).toEqual([admitted]); expect(store.state.cursor?.sequence).toBe('2');
  expect(store.acceptEvent(draftEvent('3', successor))).toBe(true);
  expect(store.state).toMatchObject({ confidence: 'authoritative', cursor: { sequence: '3' }, drafts: [successor], operations: [admitted] });
});

test.each([false, true])('snapshot buffering honors a per-draft read fence without skipping operation events (Missing=%s)', missing => {
  const store = new ObservationStore(); store.acceptSnapshot(snapshot()); store.observeDraft(clean()); store.beginSnapshot();
  expect(store.acceptEvent(draftEvent('1', staged()))).toBe(true);
  const admitted = operation(); expect(store.acceptEvent(event('2', { type: 'operation_changed', operation: admitted }))).toBe(true);
  const successor = staged(); successor.draft.revision = '3';
  expect(store.observeDraftResult(input(), read(missing ? undefined : successor, '2'))).toBe(true);
  expect(store.state.cursor?.sequence).toBe('0');
  expect(store.acceptSnapshot(snapshot())).toBe(true);
  expect(store.state.cursor?.sequence).toBe('2'); expect(store.state.operations).toEqual([admitted]);
  expect(store.state.drafts).toEqual(missing ? [] : [successor]);
});

test('Missing after eventless Discard creates a tombstone even at the same cursor', () => {
  const store = new ObservationStore(); store.acceptSnapshot(snapshot()); store.observeDraft(staged());
  expect(store.observeDraftResult(input(), read(staged(), '0'))).toBe(true);
  expect(store.observeDraftResult(input(), read(undefined, '0'))).toBe(true);
  expect(store.state.drafts).toEqual([]); expect(store.state.cursor?.sequence).toBe('0');
  expect(store.observeDraft(staged())).toBe(false); expect(store.state.drafts).toEqual([]);
});

test('Missing and Discard both suppress old buffered draft payload while consuming its sequence', () => {
  const store = new ObservationStore(); store.acceptSnapshot(snapshot()); store.observeDraft(staged());
  expect(store.observeDiscard({ ...input(), previousRevision: staged().draft.revision })).toBe(true);
  expect(store.acceptEvent(draftEvent('1', staged()))).toBe(true);
  expect(store.acceptEvent(event('2', { type: 'operation_changed', operation: operation() }))).toBe(true);
  expect(store.observeDraftResult(input(), read(undefined, '2'))).toBe(true);
  expect(store.state).toMatchObject({ confidence: 'authoritative', drafts: [], cursor: { sequence: '2' } });
  expect(store.state.operations).toHaveLength(1);
  expect(store.observeDraftResult(input(), read(staged(), '2'))).toBe(false); expect(store.state.drafts).toEqual([]);
});

test('an older read cannot regress a newer draft event or replace its watermark with Missing', () => {
  const store = new ObservationStore(); store.acceptSnapshot(snapshot()); store.observeDraft(clean());
  expect(store.acceptEvent(draftEvent('1', staged()))).toBe(true);
  expect(store.observeDraftResult(input(), read(undefined, '0'))).toBe(false);
  expect(store.state.reason).toBe('draft_read_regressed'); expect(store.state.drafts).toEqual([staged()]);
  expect(store.state.cursor?.sequence).toBe('1');
});

test('Missing watermarks share the bounded draft identity budget', () => {
  const store = new ObservationStore({ maximumDrafts: 1 }); store.acceptSnapshot(snapshot());
  expect(store.observeDraftResult(input(), read(undefined))).toBe(true);
  expect(store.observeDraftResult({ ...input(), draftId: '00009002-1111-4111-8111-111111111111' }, read(undefined))).toBe(false);
  expect(store.state.reason).toBe('observation_limit');
  expect(store.forgetDiscardedDraft(input().hostEpoch, input().draftId)).toBe(true);
});

test.each(['cursor_epoch', 'draft_id', 'draft_epoch'] as const)('the client refuses schema-valid get_draft %s cross-message correlation', async mismatch => {
  const output = read(clean());
  const foreign = '00009003-1111-4111-8111-111111111111';
  if (mismatch === 'cursor_epoch') output.cursor.hostEpoch = foreign;
  else if (output.draft.status === 'observed') output.draft.value.draft[mismatch === 'draft_id' ? 'draftId' : 'hostEpoch'] = foreign;
  let sequence = 0;
  const client = new BridgeClient({ subscribe: () => () => {}, exchange: async encoded => {
    const request = decodeRequest(encoded), reply = { protocolVersion: 1, requestId: request.requestId,
      body: { type: 'result', result: { type: 'query', query: { name: 'get_draft', output } } } };
    // Keep the test at cross-message correlation, not malformed wire DTOs.
    decodeReply(JSON.stringify(reply)); return JSON.stringify(reply);
  } }, { requestId: () => (++sequence).toString(16).padStart(8, '0') + '-2222-4222-8222-222222222222' });
  try { expect(await client.query('get_draft', input())).toMatchObject({ kind: 'fault', fault: { code: 'correlation' } }); }
  finally { client.dispose(); }
});

function workWithInput() {
  const store = new ObservationStore(); store.acceptSnapshot(snapshot()); store.observeDraft(clean());
  const work = new WorkContext(store); work.openDraft(clean());
  const reference = frame('sc09-protected-private-entry-reply').body.result.command.output.outcome.reference;
  const edits: ConfigurationEdit[] = [...staged().edits, { kind: 'set_private', fieldId: reference.fieldId, reference }];
  expect(work.stage(edits)).toBe(true);
  expect(work.setPublicInput(work.capturePublicInput('setting.integer')!, '-')).toBe(true);
  return { work, store };
}

test.each(['missing', 'foreign_host', 'foreign_id', 'changed_document', 'changed_schema', 'local_edits', 'new_generation'] as const)
('draft reconciliation retains typed edits, protected refs, raw text and target on %s', mismatch => {
  const { work, store } = workWithInput(), capture = work.captureDraftReconciliation()!, before = work.state;
  const output = read(mismatch === 'missing' ? undefined : staged(), '2');
  if (mismatch === 'foreign_host') output.cursor.hostEpoch = '00009003-1111-4111-8111-111111111111';
  if (output.draft.status === 'observed') {
    if (mismatch === 'foreign_id') output.draft.value.draft.draftId = '00009003-1111-4111-8111-111111111111';
    if (mismatch === 'changed_document') output.draft.value.draft.document.revision = 'changed-document';
    if (mismatch === 'changed_schema') output.draft.value.schema.fields[0].deprecated = !output.draft.value.schema.fields[0].deprecated;
  }
  if (mismatch === 'new_generation') work.setPublicInput(work.capturePublicInput('setting.integer')!, '1.');
  const retained = work.state;
  expect(work.finishDraftReconciliation(capture, result(output))).toBe(false);
  expect(work.state.draftConflict).toBe(true); expect(work.state.draft).toBe(before.draft);
  expect(work.state.edits).toBe(retained.edits); expect(work.state.publicInputs).toBe(retained.publicInputs);
  expect(work.state.selector).toBe(before.selector); expect(work.state.binding).toBe(before.binding);
  expect(work.state.baselineRefreshRequired).toBe(false); expect(store.state.cursor?.sequence).toBe('0');
});

test.each(['dirty', 'invalid', 'stale'] as const)('a verified %s successor retains its actual state without changing the document baseline', state => {
  const store = new ObservationStore(); store.acceptSnapshot(snapshot()); store.observeDraft(clean());
  const work = new WorkContext(store); work.openDraft(clean()); const before = work.state;
  const value: GetDraftResult = frame(`sc08-get-current-${state}-draft-reply`).body.result.query.output;
  expect(work.finishDraftReconciliation(work.captureDraftReconciliation()!, result(value))).toBe(true);
  expect(work.state.draft?.state).toBe(state); expect(work.state.draft?.draft.document).toEqual(before.draft?.draft.document);
  expect(work.state.edits).toEqual(value.draft.status === 'observed' ? value.draft.value.edits : []);
  expect(work.state.draftConflict).toBe(state === 'stale'); expect(store.state.cursor?.sequence).toBe('0');
});

test('an unchanged authoritative draft preserves unsynchronized edits and unfinished numeric input', () => {
  const { work } = workWithInput(), before = work.state;
  expect(work.finishDraftReconciliation(work.captureDraftReconciliation()!, result(read(clean())))).toBe(true);
  expect(work.state.draft).toBe(before.draft); expect(work.state.edits).toBe(before.edits);
  expect(work.state.publicInputs).toBe(before.publicInputs); expect(work.state.draftConflict).toBe(false);
});

test('a read cannot automatically clear already synchronized protected references', () => {
  const backend = clean(), reference = frame('sc09-protected-private-entry-reply').body.result.command.output.outcome.reference;
  backend.state = 'dirty'; backend.apply = ['next_launch']; backend.edits = [{ kind: 'set_private', fieldId: reference.fieldId, reference }];
  const store = new ObservationStore(); store.acceptSnapshot(snapshot()); store.observeDraft(backend);
  const work = new WorkContext(store); work.openDraft(backend); const before = work.state;
  const successor = clean(); successor.draft.revision = '2';
  expect(work.finishDraftReconciliation(work.captureDraftReconciliation()!, result(read(successor, '1')))).toBe(false);
  expect(work.state.draft).toBe(before.draft); expect(work.state.edits).toBe(before.edits); expect(work.state.draftConflict).toBe(true);
});

test.each(['snapshot', 'query', 'disconnect'] as const)('a late reconciliation cannot adopt after %s generation changes', async phase => {
  let sequence = 0, resolveSnapshot!: (frame: string) => void, resolveDraft!: (frame: string) => void, disconnect!: (fault: TransportFault) => void;
  let pendingSnapshot: Request | undefined, pendingDraft: Request | undefined;
  const client = new BridgeClient({ subscribe: (_event, fault) => { disconnect = fault!; return () => {}; }, exchange: encoded => {
    const request = decodeRequest(encoded);
    if (request.body.type !== 'query') throw new Error('unexpected mutation');
    if (request.body.query.name === 'snapshot') {
      pendingSnapshot = clone(request) as Request;
      return phase === 'snapshot' ? new Promise<string>(resolve => { resolveSnapshot = resolve; }) : Promise.resolve(reply(request, 'snapshot', snapshot()));
    }
    if (request.body.query.name !== 'get_draft') throw new Error('unexpected query');
    pendingDraft = clone(request) as Request;
    return new Promise<string>(resolve => { resolveDraft = resolve; });
  } }, { requestId: () => (++sequence).toString(16).padStart(8, '0') + '-2222-4222-8222-222222222222' });
  const facade = new BridgeFacade(client, { idempotencyKey: () => '00009004-1111-4111-8111-111111111111' });
  facade.work.openDraft(clean()); facade.work.observations.observeDraft(clean());
  const task = facade.connect();
  const local: ConfigurationEdit[] = [{ kind: 'set_public', fieldId: 'setting.boolean', value: { kind: 'boolean', value: false } }];
  if (phase === 'snapshot') { facade.stage(local); resolveSnapshot(reply(pendingSnapshot!, 'snapshot', snapshot())); }
  for (let turn = 0; turn < 20 && !pendingDraft; turn++) await Promise.resolve();
  expect(pendingDraft).toBeDefined();
  if (phase === 'query') facade.stage(local);
  if (phase === 'disconnect') disconnect({ code: 'disconnected', delivery: 'may_have_reached_backend' });
  const before = facade.work.state;
  resolveDraft(reply(pendingDraft!, 'get_draft', read(staged(), '1')));
  try {
    await task; expect(facade.work.state.draft).toBe(before.draft); expect(facade.work.state.edits).toBe(before.edits);
    expect(facade.work.state.draftConflict).toBe(true); expect(facade.work.state.baselineRefreshRequired).toBe(false);
    if (phase === 'disconnect') expect(facade.work.observations.state.drafts).toEqual([clean()]);
  } finally { facade.dispose(); client.dispose(); }
});

function reply(request: Request | { readonly requestId: string }, name: 'snapshot' | 'get_draft', output: Snapshot | GetDraftResult): string {
  return JSON.stringify({ protocolVersion: 1, requestId: request.requestId, body: { type: 'result', result: { type: 'query', query: { name, output } } } });
}

test('current successor reconciliation recovers matching lost public stage ACK without opening or changing a baseline', async () => {
  let backend = clean(), cursor = snapshot().cursor, disconnect!: (fault: TransportFault) => void;
  const sent: Request[] = []; let sequence = 0;
  const client = new BridgeClient({ subscribe: (_event, fault) => { disconnect = fault!; return () => {}; }, exchange: async encoded => {
    const request = decodeRequest(encoded); sent.push(clone(request) as Request);
    if (request.body.type === 'command' && request.body.command.name === 'set_draft_changes') {
      backend = staged(); cursor = { ...cursor, sequence: '1' };
      disconnect({ code: 'disconnected', delivery: 'may_have_reached_backend' });
      throw { code: 'disconnected', delivery: 'may_have_reached_backend' };
    }
    if (request.body.type !== 'query') throw new Error('unexpected mutation');
    const query = request.body.query;
    const output = query.name === 'snapshot' ? { ...snapshot(), cursor } : query.name === 'get_draft' ? { ...read(backend), cursor } : undefined;
    if (!output) throw new Error('unexpected query');
    return JSON.stringify({ protocolVersion: 1, requestId: request.requestId, body: { type: 'result', result: { type: 'query', query: { name: query.name, output } } } });
  } }, { requestId: () => (++sequence).toString(16).padStart(8, '0') + '-2222-4222-8222-222222222222' });
  const facade = new BridgeFacade(client, { idempotencyKey: () => '00009004-1111-4111-8111-111111111111' });
  facade.requestTarget(frame('sc09-schema-all-field-types-request').body.query.input.target);
  facade.work.bindTarget(backend.draft.document.target); facade.work.openDraft(backend); facade.work.observations.observeDraft(backend);
  try {
    await facade.connect(); expect(facade.stage(staged().edits)).toBe(true);
    const before = facade.work.state;
    expect(await facade.prepareSaveInPlace()).toBeUndefined();
    expect(facade.work.state.draft).toBe(before.draft); expect(facade.work.state.edits).toBe(before.edits);
    expect(facade.work.observations.state.reason).toBe('disconnected');
    await facade.connect();
    expect(facade.work.state.draft).toEqual(backend); expect(facade.work.state.edits).toEqual(backend.edits);
    expect(facade.work.state.draftConflict).toBe(false); expect(facade.work.state.dirty).toBe(true);
    expect(facade.work.state.binding).toBe(before.binding); expect(facade.work.state.baselineRefreshRequired).toBe(false);
    expect(sent.map(request => request.body.type === 'query' ? request.body.query.name : request.body.command.name))
      .toEqual(['snapshot', 'get_draft', 'set_draft_changes', 'snapshot', 'get_draft']);
    expect(canonicalData(sent.at(-1)?.body)).toBe(canonicalData({ type: 'query', query: { name: 'get_draft', input: input() } }));
  } finally { facade.dispose(); client.dispose(); }
});

test.each([['clean', 'event'], ['clean', 'read'], ['dirty', 'event'], ['dirty', 'read']] as const)
('the real same-revision %s to stale %s keeps draft and protected custody while the global stream remains consumable', (state, route) => {
  const previous = state === 'clean' ? clean() : staged();
  if (state === 'dirty') {
    const reference = frame('sc09-protected-secret-entry-reply').body.result.command.output.outcome.reference;
    reference.draft = clone(previous.draft);
    previous.edits.push({ kind: 'replace_secret', fieldId: reference.fieldId, reference });
  }
  const stale = clone(previous); stale.state = 'stale'; stale.validation = [];
  const store = new ObservationStore(); store.acceptSnapshot(snapshot()); store.observeDraft(previous);
  const work = new WorkContext(store); work.openDraft(previous);
  if (route === 'event') expect(store.acceptEvent(draftEvent('1', stale))).toBe(true);
  const current = read(stale, '1');
  expect(work.finishDraftReconciliation(work.captureDraftReconciliation()!, result(current))).toBe(true);
  expect(work.state.draft).toEqual(stale); expect(work.state.edits).toEqual(previous.edits); expect(work.state.draftConflict).toBe(true);
  if (route === 'read') expect(store.acceptEvent(draftEvent('1', stale))).toBe(true);
  expect(store.acceptEvent(event('2', { type: 'operation_changed', operation: operation() }))).toBe(true);
  expect(store.state).toMatchObject({ confidence: 'authoritative', cursor: { sequence: '2' }, drafts: [stale] });
  expect(work.finishDraftReconciliation(work.captureDraftReconciliation()!, result(read(stale, '2')))).toBe(true);
});

test('unwatermarked draft payloads cannot assert a same-revision stale transition', () => {
  const previous = staged(), stale = clone(previous); stale.state = 'stale'; stale.validation = [];
  const store = new ObservationStore(); store.acceptSnapshot(snapshot()); store.observeDraft(previous);
  expect(store.observeDraft(stale)).toBe(false); expect(store.state.drafts).toEqual([previous]);
});

test('a stale read cannot rewrite an observed draft at the exact same read watermark', () => {
  const previous = staged(), stale = clone(previous); stale.state = 'stale'; stale.validation = [];
  const store = new ObservationStore(); store.acceptSnapshot(snapshot()); store.observeDraft(previous);
  expect(store.observeDraftResult(input(), read(previous, '1'))).toBe(true);
  expect(store.observeDraftResult(input(), read(stale, '1'))).toBe(false);
  expect(store.state.drafts).toEqual([previous]);
});

test.each(['edits', 'schema', 'apply', 'document', 'resurrection'] as const)('same-revision stale allowance still refuses changed %s custody', change => {
  const previous = staged(), candidate = clone(previous);
  candidate.state = 'stale'; candidate.validation = [];
  if (change === 'edits') candidate.edits = [];
  if (change === 'schema') candidate.schema.fields[0].deprecated = !candidate.schema.fields[0].deprecated;
  if (change === 'apply') candidate.apply = [];
  if (change === 'document') candidate.draft.document.revision = 'foreign-document-revision';
  if (change === 'resurrection') { previous.state = 'stale'; candidate.state = 'dirty'; }
  const store = new ObservationStore(); store.acceptSnapshot(snapshot()); store.observeDraft(previous);
  expect(store.observeDraftResult(input(), read(candidate, '1'))).toBe(false);
  expect(store.state.drafts).toEqual([previous]);
});

test.each(['newer_event', 'missing', 'discard', 'sequence_gap', 'disconnected', 'stream_changed'] as const)
('synchronous %s observation cannot certify an obsolete retained-draft read', change => {
  const { work, store } = workWithInput(), capture = work.captureDraftReconciliation()!, before = work.state;
  let armed = true;
  const stop = store.subscribe(() => {
    if (armed) return;
    armed = true;
    if (change === 'newer_event') store.acceptEvent(draftEvent('1', staged()));
    else if (change === 'missing') store.observeDraftResult(input(), read(undefined));
    else if (change === 'discard') store.observeDiscard({ ...input(), previousRevision: clean().draft.revision });
    else if (change === 'sequence_gap') store.acceptEvent(event('2', { type: 'operation_changed', operation: operation() }));
    else if (change === 'disconnected') store.invalidate('disconnected');
    else store.acceptSnapshot({ ...snapshot(), cursor: { ...snapshot().cursor, streamId: '00009005-1111-4111-8111-111111111111' } });
  });
  armed = false;
  try {
    expect(work.finishDraftReconciliation(capture, result(read(clean())))).toBe(false);
    expect(work.state.draft).toBe(before.draft); expect(work.state.edits).toBe(before.edits);
    expect(work.state.publicInputs).toBe(before.publicInputs); expect(work.state.selector).toBe(before.selector);
    expect(work.state.binding).toBe(before.binding); expect(work.state.draftConflict).toBe(true);
  } finally { stop(); }
});
