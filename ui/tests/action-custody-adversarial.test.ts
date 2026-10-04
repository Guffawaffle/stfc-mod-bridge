import { readFileSync } from 'node:fs';
import { expect, test } from 'vitest';
import { BridgeClient, decodeReply, decodeRequest, type DeepReadonly } from '../src/client';
import { semanticPlanDigest } from '../src/client/relations';
import type { MutationIntent, OperationSnapshot, Reply, Request, Snapshot } from '../src/generated/protocol';
import { BridgeFacade, type ActionReviewState } from '../src/state';

const fixtureRoot = new URL('../../contracts/fixtures/', import.meta.url);
const restartedEpoch = '00000002-2222-4222-8222-222222222222';
function reply(id: string): Reply {
  const encoded = readFileSync(new URL(id + '.json', fixtureRoot), 'utf8');
  decodeReply(encoded); return JSON.parse(encoded);
}
function operation(id = 'sc12-game-update-result-reply'): OperationSnapshot {
  const value = reply(id);
  if (value.body.type !== 'result' || value.body.result.type !== 'query' || value.body.result.query.name !== 'get_operation'
    || value.body.result.query.output.operation.status !== 'observed') throw new Error('operation_fixture');
  return value.body.result.query.output.operation.value;
}
function intent(): DeepReadonly<MutationIntent> {
  const value = decodeRequest(readFileSync(new URL('sc12-game-update-request.json', fixtureRoot), 'utf8'));
  if (value.body.type !== 'command' || value.body.command.name !== 'prepare') throw new Error('intent_fixture');
  return value.body.command.input.intent;
}
function snapshot(operations: OperationSnapshot[] = [], epoch?: string): Snapshot {
  const value = reply('sc15-complete-empty-snapshot-reply'), prepared = reply('sc12-game-update-reply');
  if (value.body.type !== 'result' || value.body.result.type !== 'query' || value.body.result.query.name !== 'snapshot'
    || prepared.body.type !== 'result' || prepared.body.result.type !== 'command' || prepared.body.result.command.name !== 'prepare') throw new Error('snapshot_fixture');
  const observed = value.body.result.query.output;
  observed.cursor.hostEpoch = epoch ?? prepared.body.result.command.output.planRef.hostEpoch;
  observed.operations.items = operations;
  decodeReply(JSON.stringify(value)); return observed;
}
function admitted(id = 'sc12-game-update-result-reply'): OperationSnapshot {
  const value = operation(id); value.operationRevision = '1'; value.state = { status: 'admitted' }; return value;
}
function rolledBackOriginal(): OperationSnapshot {
  const value = operation('sc12-game-rollback-required-reply'); value.operationRevision = '4';
  value.state = { status: 'completed', outcome: { kind: 'rolled_back', reason: 'rollback_completed' } }; return value;
}
function atHost<T>(value: T, epoch: string): T {
  return JSON.parse(JSON.stringify(value).replaceAll(snapshot().cursor.hostEpoch, epoch));
}
type Pending = { request: DeepReadonly<Request>; resolve: (encoded: string) => void; reject: (failure: unknown) => void; done: boolean };
function harness(initial: OperationSnapshot[] = [], maximumReplays?: number) {
  let sequence = 0, key = 0, unsent = false;
  const sent: DeepReadonly<Request>[] = [], pending: Pending[] = [], keys: string[] = [];
  const client = new BridgeClient({ subscribe: () => () => {}, exchange(encoded) {
    const request = decodeRequest(encoded); sent.push(request);
    if (request.body.type === 'command' && request.body.command.name === 'prepare') {
      let response = reply(request.body.command.input.intent.kind === 'recover_game_update'
        ? 'sc12-recover-prior-game-image-reply' : 'sc12-game-update-reply');
      const epoch = facade.work.observations.state.cursor?.hostEpoch;
      if (epoch) response = atHost(response, epoch);
      response.requestId = request.requestId;
      if (epoch && epoch !== snapshot().cursor.hostEpoch) {
        if (response.body.type !== 'result' || response.body.result.type !== 'command' || response.body.result.command.name !== 'prepare') throw new Error('prepare_fixture');
        const plan = response.body.result.command.output;
        return semanticPlanDigest(plan.semantics).then(digest => { plan.planRef.reviewDigest = digest; return JSON.stringify(response); });
      }
      return Promise.resolve(JSON.stringify(response));
    }
    if (request.body.type === 'command' && request.body.command.name === 'commit' && unsent) {
      unsent = false; throw { code: 'delivery_failed', delivery: 'not_sent' };
    }
    return new Promise<string>((resolve, reject) => pending.push({ request, resolve, reject, done: false }));
  } }, { requestId: () => (++sequence).toString(16).padStart(8, '0') + '-2222-4222-8222-222222222222', maximumReplays });
  const facade = new BridgeFacade(client, { idempotencyKey: () => {
    const value = (++key).toString(16).padStart(8, '0') + '-1111-4111-8111-111111111111'; keys.push(value); return value;
  } });
  expect(facade.work.observations.acceptSnapshot(snapshot(initial))).toBe(true);
  function next(method: 'commit' | 'get_operation'): Pending {
    const found = pending.find(row => !row.done && (row.request.body.type === 'command' ? row.request.body.command.name : row.request.body.query.name) === method);
    if (!found) throw new Error('missing_pending_' + method); return found;
  }
  function finish(method: 'commit' | 'get_operation', observed: OperationSnapshot) {
    const row = next(method);
    const response: Reply = method === 'commit'
      ? { protocolVersion: 1, requestId: row.request.requestId, body: { type: 'result', result: { type: 'command', command: { name: 'commit', output: observed } } } }
      : reply('sc12-game-update-result-reply');
    response.requestId = row.request.requestId;
    if (method === 'get_operation' && response.body.type === 'result' && response.body.result.type === 'query'
      && response.body.result.query.name === 'get_operation' && response.body.result.query.output.operation.status === 'observed') response.body.result.query.output.operation.value = observed;
    const encoded = JSON.stringify(response); decodeReply(encoded); row.done = true; row.resolve(encoded);
  }
  return { client, facade, sent, keys, finish,
    failNextUnsent() { unsent = true; },
    failCommit() { const row = next('commit'); row.done = true; row.reject({ code: 'disconnected', delivery: 'may_have_reached_backend' }); },
    async start(id = 'sc12-game-update-result-reply') {
      expect(await facade.actions.prepare(intent())).toMatchObject({ kind: 'result' });
      const submission = facade.actions.confirm(); finish('commit', admitted(id));
      expect(await submission).toMatchObject({ kind: 'result' }); expect(facade.actions.state.transition.kind).toBe('observing');
    },
    async park() {
      await this.start('sc12-game-rollback-required-reply'); expect(facade.work.observations.observeOperation(operation('sc12-game-rollback-required-reply'))).toBe(true);
      expect(facade.actions.state.transition.kind).toBe('idle');
    },
    dispose() { facade.dispose(); client.dispose(); },
  };
}

function replaceHost(run: ReturnType<typeof harness>): void {
  const epoch = run.facade.work.observations.state.cursor!.hostEpoch;
  run.facade.work.observations.observeHello(restartedEpoch);
  run.facade.work.observations.invalidate('disconnected');
  expect(run.facade.work.observations.state.cursor?.hostEpoch).toBe(epoch);
  expect(run.facade.work.hostEpochCurrent(epoch)).toBe(false);
}

test('known host replacement prevents fresh generic preparation without outbound work', async () => {
  const run = harness();
  try {
    replaceHost(run); expect(await run.facade.actions.prepare(intent())).toBeUndefined();
    expect(run.sent).toEqual([]); expect(run.client.pendingCount).toBe(0); expect(run.client.replayCount).toBe(0);
    expect(run.facade.actions.state.plan).toBeUndefined(); expect(run.facade.actions.state.transition.kind).toBe('idle');
  } finally { run.dispose(); }
});

test('host replacement during generic preparing publication prevents outbound preparation', async () => {
  const run = harness(); let replaced = false;
  run.facade.actions.subscribe(state => { if (!replaced && state.transition.kind === 'preparing') { replaced = true; replaceHost(run); } });
  try {
    expect(await run.facade.actions.prepare(intent())).toBeUndefined(); expect(replaced).toBe(true);
    expect(run.sent).toEqual([]); expect(run.client.pendingCount).toBe(0); expect(run.client.replayCount).toBe(0);
    expect(run.facade.actions.state.plan).toBeUndefined(); expect(run.facade.actions.state.transition.kind).toBe('idle');
  } finally { run.dispose(); }
});

test('matching generic preparation after known host replacement cannot install review', async () => {
  const run = harness();
  try {
    const pending = run.facade.actions.prepare(intent()); replaceHost(run); await pending;
    expect(run.sent).toHaveLength(1); expect(run.facade.actions.state.plan).toBeUndefined();
    expect(run.facade.actions.state.transition.kind).toBe('idle'); expect(await run.facade.actions.confirm()).toBeUndefined();
    expect(run.sent).toHaveLength(1); expect(run.client.replayCount).toBe(0);
  } finally { run.dispose(); }
});

test('known host replacement prevents fresh generic confirmation without replay custody', async () => {
  const run = harness();
  try {
    await run.facade.actions.prepare(intent()); replaceHost(run);
    expect(await run.facade.actions.confirm()).toBeUndefined(); expect(run.sent).toHaveLength(1);
    expect(run.client.pendingCount).toBe(0); expect(run.client.replayCount).toBe(0);
    expect(run.facade.actions.state.transition.kind).toBe('idle'); expect(run.facade.actions.blocksTransitions).toBe(false);
  } finally { run.dispose(); }
});

test('host replacement during generic admitting publication prevents fresh commit', async () => {
  const run = harness(); let replaced = false;
  try {
    await run.facade.actions.prepare(intent());
    run.facade.actions.subscribe(state => { if (!replaced && state.transition.kind === 'admitting') { replaced = true; replaceHost(run); } });
    expect(await run.facade.actions.confirm()).toBeUndefined(); expect(replaced).toBe(true);
    expect(run.sent).toHaveLength(1); expect(run.client.pendingCount).toBe(0); expect(run.client.replayCount).toBe(0);
    expect(run.facade.actions.state.transition.kind).toBe('idle'); expect(run.facade.actions.blocksTransitions).toBe(false);
  } finally { run.dispose(); }
});

test('obsolete generic review releases its proved-unsent replay before a new host admission', async () => {
  const run = harness([], 1);
  try {
    await run.facade.actions.prepare(intent()); run.failNextUnsent();
    expect(await run.facade.actions.confirm()).toMatchObject({ kind: 'fault', fault: { delivery: 'not_sent' } });
    expect(run.facade.actions.state.transition.kind).toBe('review'); expect(run.client.replayCount).toBe(1);
    const priorKey = run.keys[0]; replaceHost(run);
    expect(await run.facade.actions.confirm()).toBeUndefined(); expect(run.sent).toHaveLength(2);
    expect(run.client.replayCount).toBe(0); expect(run.client.pendingCount).toBe(0); expect(run.client.getReplay(priorKey)).toBeUndefined();
    expect(run.facade.actions.state.transition.kind).toBe('idle'); expect(run.facade.actions.blocksTransitions).toBe(false);
    expect(run.facade.work.observations.acceptSnapshot(snapshot([], restartedEpoch))).toBe(true);
    const prepared = await run.facade.actions.prepare(atHost(intent(), restartedEpoch));
    expect(prepared?.kind === 'fault' ? prepared.fault.code : prepared?.kind).toBe('result');
    const next = run.facade.actions.confirm(); run.finish('commit', atHost(admitted(), restartedEpoch));
    expect(await next).toMatchObject({ kind: 'result' }); expect(run.facade.actions.state.transition.kind).toBe('observing');
    expect(run.client.replayCount).toBe(1); expect(run.keys[1]).not.toBe(priorKey);
  } finally { run.dispose(); }
});

test('obsolete generic review cleanup preserves a foreign replacement replay capture', async () => {
  const run = harness([], 1);
  try {
    await run.facade.actions.prepare(intent()); run.failNextUnsent(); await run.facade.actions.confirm();
    const key = run.keys[0], original = run.client.getReplay(key)!; expect(run.client.forgetReplay(key)).toBe(true);
    run.failNextUnsent(); expect(await run.client.command('commit', original.input)).toMatchObject({ kind: 'fault', fault: { delivery: 'not_sent' } });
    const replacement = run.client.getReplay(key); expect(replacement?.request).not.toBe(original.request);
    replaceHost(run); const before = run.sent.length; expect(await run.facade.actions.confirm()).toBeUndefined();
    expect(run.sent).toHaveLength(before); expect(run.facade.actions.state.transition.kind).toBe('idle');
    expect(run.client.getReplay(key)).toBe(replacement); expect(run.client.replayCount).toBe(1);
    run.facade.dispose(); expect(run.client.getReplay(key)).toBe(replacement);
  } finally { run.dispose(); }
});

test('a later listener cannot redisplay review after a reentrant Stay closes it', async () => {
  const run = harness(); let closed = false; const shown: ActionReviewState[] = [];
  run.facade.actions.subscribe(state => {
    if (!closed && state.transition.kind === 'review') { closed = true; expect(run.facade.actions.stay()).toBe(true); }
  });
  run.facade.actions.subscribe(state => shown.push(state));
  try {
    await run.facade.actions.prepare(intent());
    expect(run.facade.actions.state.transition.kind).toBe('idle');
    expect(shown.at(-1)?.transition.kind).toBe('idle');
    expect(shown.at(-1)?.notice).toBe('Action review closed.');
  } finally { run.dispose(); }
});

test('a replacement listener mounted during nested publication does not receive the superseded review', async () => {
  const run = harness(); let remounted = false; const shown: string[] = []; let stop = () => {};
  run.facade.actions.subscribe(state => {
    if (!remounted && state.transition.kind === 'review') {
      remounted = true; stop(); expect(run.facade.actions.stay()).toBe(true);
      stop = run.facade.actions.subscribe(next => shown.push(next.transition.kind));
    }
  });
  stop = run.facade.actions.subscribe(() => {});
  try {
    await run.facade.actions.prepare(intent());
    expect(shown).toEqual(['idle']);
  } finally { stop(); run.dispose(); }
});

test.each(['preparing', 'admitting', 'cancelling'] as const)(
  'synchronous disposal during %s publication prevents its outbound invocation', async stage => {
    const running = operation('sc15-running-reply'), run = harness(stage === 'cancelling' ? [running] : []);
    if (stage === 'admitting') await run.facade.actions.prepare(intent());
    const before = run.sent.length;
    run.facade.actions.subscribe(state => {
      if (stage === 'cancelling' ? state.cancellingOperationIds.length > 0 : state.transition.kind === stage) run.facade.dispose();
    });
    try {
      const outcome = stage === 'preparing' ? await run.facade.actions.prepare(intent())
        : stage === 'admitting' ? await run.facade.actions.confirm() : await run.facade.cancelOperation(running);
      expect(outcome).toMatchObject({ kind: 'fault', fault: { delivery: 'not_sent' } });
      expect(run.sent).toHaveLength(before); expect(run.client.pendingCount).toBe(0); expect(run.client.replayCount).toBe(0);
    } finally { run.dispose(); }
  });

test('an original in-flight admission cannot adopt after authoritative host replacement', async () => {
  const run = harness();
  try {
    await run.facade.actions.prepare(intent()); const submission = run.facade.actions.confirm();
    expect(run.facade.work.observations.acceptSnapshot(snapshot([], restartedEpoch))).toBe(true);
    run.finish('commit', admitted()); await submission;
    expect(run.facade.work.observations.state.cursor?.hostEpoch).toBe(restartedEpoch);
    expect(run.facade.work.observations.state.operations).toEqual([]);
    expect(run.facade.actions.state.transition.kind).toBe('uncertain'); expect(run.client.replayCount).toBe(1);
  } finally { run.dispose(); }
});

test('an original in-flight admission cannot adopt after inventory confidence is lost', async () => {
  const run = harness();
  try {
    await run.facade.actions.prepare(intent()); const submission = run.facade.actions.confirm();
    run.facade.work.observations.invalidate('disconnected'); run.finish('commit', admitted()); await submission;
    expect(run.facade.work.observations.state.confidence).toBe('stale');
    expect(run.facade.work.observations.state.operations).toEqual([]);
    expect(run.facade.actions.state.transition.kind).toBe('uncertain'); expect(run.client.replayCount).toBe(1);
  } finally { run.dispose(); }
});

test('a terminal reply from an in-flight old-host query cannot retire replay in the replacement epoch', async () => {
  const run = harness();
  try {
    await run.start(); const retained = run.client.getReplay(run.keys[0]); const reconciliation = run.facade.actions.reconcile();
    const current = admitted(); current.operationRevision = '2';
    expect(run.facade.work.observations.acceptSnapshot(snapshot([current], restartedEpoch))).toBe(true);
    run.finish('get_operation', operation()); await reconciliation;
    expect(run.facade.work.observations.state.operations).toEqual([current]);
    expect(run.client.getReplay(run.keys[0])).toBe(retained);
    expect(run.facade.actions.state.transition.kind).toBe('observing');
  } finally { run.dispose(); }
});

test('a newly requested exact replay after a host replacement still observes the original durable identity', async () => {
  const run = harness();
  try {
    await run.facade.actions.prepare(intent()); const submission = run.facade.actions.confirm();
    run.failCommit(); await submission; const original = run.client.getReplay(run.keys[0]);
    expect(run.facade.work.observations.acceptSnapshot(snapshot([], restartedEpoch))).toBe(true);
    const replay = run.facade.actions.replay(); run.finish('commit', admitted()); await replay;
    expect(run.facade.actions.state.transition.kind).toBe('observing');
    expect(run.facade.work.observations.state.operations[0].operationId).toBe(admitted().operationId);
    expect(run.client.getReplay(run.keys[0])?.request).toBe(original?.request);
    const commits = run.sent.filter(row => row.body.type === 'command' && row.body.command.name === 'commit');
    expect(commits).toHaveLength(2);
    expect(commits.map(row => row.body.type === 'command' ? row.body.command.input : undefined)).toEqual([original?.input, original?.input]);
  } finally { run.dispose(); }
});

test.each(['active', 'parked', 'recovery'] as const)(
  'terminal reconciliation of %s custody cannot forget an equal-input replacement capture', async custody => {
    const run = harness();
    try {
      if (custody === 'active') await run.start(); else await run.park();
      if (custody === 'recovery') {
        await run.facade.actions.prepareRecovery(operation('sc12-game-rollback-required-reply'));
        const submission = run.facade.actions.confirm(); run.finish('commit', admitted('sc12-prior-game-restored-reply')); await submission;
      }
      const index = custody === 'recovery' ? 1 : 0, key = run.keys[index], original = run.client.getReplay(key);
      if (!original) throw new Error('missing_original_capture');
      expect(run.client.forgetReplay(key)).toBe(true);
      const resubmission = run.client.command('commit', original.input), replacement = run.client.getReplay(key);
      expect(replacement?.input).toEqual(original.input); expect(replacement?.request).not.toBe(original.request);
      run.finish('commit', admitted(custody === 'recovery' ? 'sc12-prior-game-restored-reply'
        : custody === 'parked' ? 'sc12-game-rollback-required-reply' : 'sc12-game-update-result-reply')); await resubmission;
      const terminal = custody === 'parked' ? rolledBackOriginal()
        : operation(custody === 'recovery' ? 'sc12-prior-game-restored-reply' : 'sc12-game-update-result-reply');
      expect(run.facade.work.observations.observeOperation(terminal)).toBe(true);
      expect(run.client.getReplay(key)?.request).toBe(replacement?.request);
      if (custody === 'recovery') expect(run.client.getReplay(run.keys[0])).toBeDefined();
    } finally { run.dispose(); }
  });

test('original terminal reconciliation during recovery admission releases only original custody', async () => {
  const run = harness();
  try {
    await run.park(); await run.facade.actions.prepareRecovery(operation('sc12-game-rollback-required-reply'));
    const recoverySubmission = run.facade.actions.confirm(), heldRecovery = run.client.getReplay(run.keys[1]);
    expect(run.facade.work.observations.observeOperation(rolledBackOriginal())).toBe(true);
    expect(run.client.getReplay(run.keys[0])).toBeUndefined(); expect(run.client.getReplay(run.keys[1])).toBe(heldRecovery);
    expect(run.facade.actions.state.transition.kind).toBe('admitting');
    run.finish('commit', admitted('sc12-prior-game-restored-reply')); await recoverySubmission;
    expect(run.facade.actions.state.transition.kind).toBe('observing'); expect(run.client.getReplay(run.keys[1])?.request).toBe(heldRecovery?.request);
  } finally { run.dispose(); }
});
