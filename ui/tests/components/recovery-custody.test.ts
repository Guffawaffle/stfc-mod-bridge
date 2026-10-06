import { readFileSync } from 'node:fs';
import { expect, test } from 'vitest';
import { BridgeClient, decodeReply, decodeRequest, type DeepReadonly } from '../../src/client';
import type { OperationSnapshot, Reply, Request, Snapshot } from '../../src/generated/protocol';
import { BridgeFacade } from '../../src/state';

const fixtureRoot = new URL('../../../contracts/fixtures/', import.meta.url);
function reply(id: string): Reply {
  const bytes = readFileSync(new URL(id + '.json', fixtureRoot), 'utf8'); decodeReply(bytes); return JSON.parse(bytes);
}
function request(id: string): DeepReadonly<Request> { return decodeRequest(readFileSync(new URL(id + '.json', fixtureRoot), 'utf8')); }
function operation(id: string): OperationSnapshot {
  const value = reply(id);
  if (value.body.type !== 'result' || value.body.result.type !== 'query' || value.body.result.query.name !== 'get_operation'
    || value.body.result.query.output.operation.status !== 'observed') throw new Error('operation_fixture');
  return value.body.result.query.output.operation.value;
}
const recovery = () => operation('sc12-game-rollback-required-reply');
function harness() {
  let sequence = 0, commits = 0, prepareReply = 'sc12-game-update-reply'; const sent: DeepReadonly<Request>[] = [];
  const client = new BridgeClient({ subscribe: () => () => {}, async exchange(encoded) {
    const value = decodeRequest(encoded); sent.push(value);
    if (value.body.type !== 'command') throw new Error('command_fixture');
    let output: Reply;
    if (value.body.command.name === 'prepare') output = reply(prepareReply);
    else if (value.body.command.name === 'commit') {
      const admitted = commits++ === 0 ? recovery() : operation('sc12-prior-game-restored-reply');
      admitted.operationRevision = '1'; admitted.state = { status: 'admitted' };
      output = { protocolVersion: 1, requestId: value.requestId, body: { type: 'result', result: { type: 'command', command: { name: 'commit', output: admitted } } } };
    } else throw new Error('method_fixture');
    output.requestId = value.requestId; decodeReply(JSON.stringify(output)); return JSON.stringify(output);
  } }, { requestId: () => (++sequence).toString(16).padStart(8, '0') + '-2222-4222-8222-222222222222' });
  let key = 0; const keys: string[] = [];
  const facade = new BridgeFacade(client, { idempotencyKey: () => {
    const value = (++key).toString(16).padStart(8, '0') + '-1111-4111-8111-111111111111'; keys.push(value); return value;
  } });
  const initial = reply('sc15-complete-empty-snapshot-reply');
  if (initial.body.type !== 'result' || initial.body.result.type !== 'query' || initial.body.result.query.name !== 'snapshot') throw new Error('snapshot_fixture');
  const observed: Snapshot = initial.body.result.query.output;
  const plan = reply('sc12-game-update-reply');
  if (plan.body.type !== 'result' || plan.body.result.type !== 'command' || plan.body.result.command.name !== 'prepare') throw new Error('plan_fixture');
  observed.cursor.hostEpoch = plan.body.result.command.output.planRef.hostEpoch; facade.work.observations.acceptSnapshot(observed);
  return { facade, client, sent, keys, nextPrepare(id = 'sc12-recover-prior-game-image-reply') { prepareReply = id; },
    async start() {
      const value = request('sc12-game-update-request');
      if (value.body.type !== 'command' || value.body.command.name !== 'prepare') throw new Error('intent_fixture');
      expect(await facade.actions.prepare(value.body.command.input.intent)).toMatchObject({ kind: 'result' });
      expect(await facade.actions.confirm()).toMatchObject({ kind: 'result' });
      expect(facade.actions.state.transition.kind).toBe('observing');
      expect(facade.work.observations.observeOperation(recovery())).toBe(true); this.nextPrepare();
    }, dispose() { facade.dispose(); client.dispose(); } };
}

test('recovery observation retains original replay and allows a separate exact modeled review', async () => {
  const run = harness(); await run.start();
  const retained = run.client.getReplay(run.keys[0]); expect(retained).toBeDefined();
  expect(run.facade.actions.state.transition.kind).toBe('idle'); expect(run.facade.actions.state.recoveryOperationIds).toEqual([recovery().operationId]);
  expect(await run.facade.actions.prepareRecovery(recovery())).toMatchObject({ kind: 'result' });
  expect(run.facade.actions.state.transition.kind).toBe('review'); expect(run.client.getReplay(run.keys[0])).toBe(retained);
  expect(run.sent.at(-1)?.body).toEqual(request('sc12-recover-prior-game-image-request').body);
  expect(run.facade.actions.stay()).toBe(true); expect(run.client.getReplay(run.keys[0])).toBe(retained); run.dispose();
});

test('a recovery completion releases its own replay while original custody awaits original terminal reconciliation', async () => {
  const run = harness(); await run.start(); await run.facade.actions.prepareRecovery(recovery()); await run.facade.actions.confirm();
  expect(run.client.replayCount).toBe(2); expect(run.facade.actions.state.transition.kind).toBe('observing');
  run.facade.work.observations.observeOperation(operation('sc12-prior-game-restored-reply'));
  expect(run.client.getReplay(run.keys[1])).toBeUndefined(); expect(run.client.getReplay(run.keys[0])).toBeDefined();
  expect(run.facade.actions.state.recoveryOperationIds).toEqual([recovery().operationId]);
  const terminal = recovery(); terminal.operationRevision = '4'; terminal.state = { status: 'completed', outcome: { kind: 'rolled_back', reason: 'rollback_completed' } };
  run.facade.work.observations.observeOperation(terminal);
  expect(run.client.replayCount).toBe(0); expect(run.facade.actions.state.recoveryOperationIds).toEqual([]); run.dispose();
});

test.each(['old_revision', 'substituted_recovery', 'unobserved', 'unsupported_recovery', 'stale_inventory'])(
  'recovery review refuses %s while retaining original replay', async kind => {
    const run = harness(); await run.start(); const candidate = recovery(), before = run.sent.length;
    if (kind === 'old_revision') candidate.operationRevision = '2';
    if (kind === 'unobserved') candidate.operationId = '00000002-2222-4222-8222-222222222222';
    if (kind === 'substituted_recovery' && candidate.state.status === 'recovery_required') candidate.state.recovery.transaction = 'synthetic-other-transaction';
    if (kind === 'unsupported_recovery') {
      const unsupported = operation('sc15-post-restart-recovery-reply'); run.facade.work.observations.observeOperation(unsupported); Object.assign(candidate, unsupported);
    }
    if (kind === 'stale_inventory') run.facade.work.observations.invalidate('disconnected');
    expect(await run.facade.actions.prepareRecovery(candidate)).toBeUndefined(); expect(run.sent).toHaveLength(before);
    expect(run.client.getReplay(run.keys[0])).toBeDefined(); run.dispose();
  });

test('rejected recovery preparation preserves the original operation and exact replay', async () => {
  const run = harness(); await run.start(); const retained = run.client.getReplay(run.keys[0]); run.nextPrepare('sc13-bridge-persistence-failed-reply');
  expect(await run.facade.actions.prepareRecovery(recovery())).toMatchObject({ kind: 'rejected' });
  expect(run.facade.actions.state.transition.kind).toBe('idle'); expect(run.client.getReplay(run.keys[0])).toBe(retained);
  expect(run.facade.actions.state.recoveryOperationIds).toEqual([recovery().operationId]); run.dispose();
});

test('disposal of a retained recovery cannot forget its durable submission', async () => {
  const run = harness(); await run.start(); const retained = run.client.getReplay(run.keys[0]); run.facade.dispose();
  expect(run.client.getReplay(run.keys[0])).toBe(retained); expect(await run.facade.actions.prepareRecovery(recovery())).toBeUndefined(); run.dispose();
});
