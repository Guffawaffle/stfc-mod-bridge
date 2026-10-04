import { readFileSync } from 'node:fs';
import { expect, test } from 'vitest';
import { BridgeClient, decodeReply, decodeRequest, type DeepReadonly } from '../../src/client';
import type { OperationSnapshot, Reply, Request, Snapshot } from '../../src/generated/protocol';
import { BridgeFacade } from '../../src/state';

const fixtureRoot = new URL('../../../contracts/fixtures/', import.meta.url);
function reply(id: string): Reply {
  const encoded = readFileSync(new URL(id + '.json', fixtureRoot), 'utf8'); decodeReply(encoded); return JSON.parse(encoded);
}
function operation(id = 'sc15-running-reply'): OperationSnapshot {
  const value = reply(id);
  if (value.body.type !== 'result' || value.body.result.type !== 'query' || value.body.result.query.name !== 'get_operation'
    || value.body.result.query.output.operation.status !== 'observed') throw new Error('operation_fixture');
  return structuredClone(value.body.result.query.output.operation.value);
}
function snapshot(observed: OperationSnapshot): Snapshot {
  const value = reply('sc15-complete-empty-snapshot-reply');
  if (value.body.type !== 'result' || value.body.result.type !== 'query' || value.body.result.query.name !== 'snapshot') throw new Error('snapshot_fixture');
  const result = structuredClone(value.body.result.query.output); result.operations.items = [observed]; return result;
}
function harness(response = 'sc15-cancel-requested-reply') {
  let request: DeepReadonly<Request> | undefined, resolve!: (encoded: string) => void, sequence = 0;
  const sent: DeepReadonly<Request>[] = [];
  const client = new BridgeClient({ subscribe: () => () => {}, exchange(encoded) {
    request = decodeRequest(encoded); sent.push(request); return new Promise<string>(accept => { resolve = accept; });
  } }, { requestId: () => (++sequence).toString(16).padStart(8, '0') + '-2222-4222-8222-222222222222' });
  const facade = new BridgeFacade(client, { idempotencyKey: () => '00000001-1111-4111-8111-111111111111' });
  const observed = operation(); expect(facade.work.observations.acceptSnapshot(snapshot(observed))).toBe(true);
  return { facade, client, sent, observed, finish(change?: (value: Reply) => void) {
    const value = structuredClone(reply(response)); change?.(value); value.requestId = request!.requestId;
    decodeReply(JSON.stringify(value)); resolve(JSON.stringify(value));
  }, dispose() { facade.dispose(); client.dispose(); } };
}

test('cancellation captures exact observed revision and survives later UI target selection', async () => {
  const run = harness(); const pending = run.facade.cancelOperation(run.observed);
  const targetReply = reply('sc-03-profile-two-prepare-reply');
  if (targetReply.body.type !== 'result' || targetReply.body.result.type !== 'command' || targetReply.body.result.command.name !== 'prepare'
    || targetReply.body.result.command.output.semantics.capture.kind !== 'launch_isolated') throw new Error('target_fixture');
  const captured = targetReply.body.result.command.output.semantics.capture.target;
  expect(run.facade.requestTarget({ installation: { kind: 'registered', id: captured.installation.kind === 'registered' ? captured.installation.registrationId : 'invalid' },
    profile: { kind: 'isolated', id: captured.profile.kind === 'isolated' ? captured.profile.id : 'invalid' } })).toBe(true);
  expect(run.sent[0].body).toEqual({ type: 'command', command: { name: 'cancel_operation', input: {
    operationId: run.observed.operationId, expectedOperationRevision: '2',
  } } });
  run.finish(); expect(await pending).toMatchObject({ kind: 'result', value: { kind: 'requested' } });
  expect(run.facade.actions.state.notice).toBe('Cancellation requested. Waiting for the operation outcome.');
  expect(run.facade.work.observations.state.operations[0].state.status).toBe('cancellation_requested'); run.dispose();
});

test('duplicate pending cancellation cannot send another request or erase its busy state', async () => {
  const run = harness(), pending = run.facade.cancelOperation(run.observed);
  expect(run.facade.actions.state.cancellingOperationIds).toEqual([run.observed.operationId]);
  expect(await run.facade.cancelOperation(run.observed)).toBeUndefined(); expect(run.sent).toHaveLength(1);
  expect(run.facade.actions.state.cancellingOperationIds).toEqual([run.observed.operationId]);
  run.finish(); await pending; expect(run.facade.actions.state.cancellingOperationIds).toEqual([]); run.dispose();
});

test.each(['unobserved', 'old_revision', 'different_capture', 'different_state', 'stale_inventory', 'already_requested', 'completed', 'recovery'])(
  'cancellation refuses %s without sending', async kind => {
    const run = harness(), input = structuredClone(run.observed);
    if (kind === 'unobserved') input.operationId = '00000002-2222-4222-8222-222222222222';
    if (kind === 'old_revision') input.operationRevision = '1';
    if (kind === 'different_capture') input.semantics.effects = [];
    if (kind === 'different_state') input.state = { status: 'admitted' };
    if (kind === 'stale_inventory') run.facade.work.observations.invalidate('disconnected');
    if (kind === 'already_requested') {
      const value = reply('sc15-cancel-requested-reply');
      if (value.body.type !== 'result' || value.body.result.type !== 'command' || value.body.result.command.name !== 'cancel_operation') throw new Error('cancel_fixture');
      Object.assign(input, structuredClone(value.body.result.command.output.operation)); run.facade.work.observations.observeOperation(input);
    }
    if (kind === 'completed' || kind === 'recovery') {
      Object.assign(input, operation(kind === 'completed' ? 'sc15-changed-with-session-receipt-reply' : 'sc15-post-restart-recovery-reply'));
      run.facade.work.observations.observeOperation(input);
    }
    expect(await run.facade.cancelOperation(input)).toBeUndefined(); expect(run.sent).toHaveLength(0); run.dispose();
  });

test('cancellation rejects a reply with substituted plan capture before it reaches the observation store', async () => {
  const run = harness(), pending = run.facade.cancelOperation(run.observed);
  run.finish(value => {
    if (value.body.type !== 'result' || value.body.result.type !== 'command' || value.body.result.command.name !== 'cancel_operation') throw new Error('cancel_fixture');
    value.body.result.command.output.operation.semantics.effects = [];
  });
  await pending; expect(run.facade.work.observations.state.operations[0]).toEqual(run.observed);
  expect(run.facade.actions.state.notice).toContain('unconfirmed'); run.dispose();
});

test.each(['host_restart', 'stale_inventory'])('cancellation does not adopt an otherwise valid result after %s', async kind => {
  const run = harness(), pending = run.facade.cancelOperation(run.observed);
  if (kind === 'host_restart') {
    const changed = snapshot(run.observed); changed.cursor.hostEpoch = '00000002-2222-4222-8222-222222222222';
    run.facade.work.observations.acceptSnapshot(changed);
  } else run.facade.work.observations.invalidate('disconnected');
  run.finish(); await pending; expect(run.facade.work.observations.state.operations[0].state.status).toBe('running');
  expect(run.facade.actions.state.notice).toContain('unconfirmed'); run.dispose();
});

test('a newer terminal event is retained when an older cancellation reply arrives', async () => {
  const run = harness(), pending = run.facade.cancelOperation(run.observed);
  const terminal = operation('sc15-changed-with-session-receipt-reply'); terminal.operationRevision = '4';
  expect(run.facade.work.observations.observeOperation(terminal)).toBe(true);
  run.finish(); await pending; expect(run.facade.work.observations.state.operations[0]).toEqual(terminal);
  expect(run.facade.work.observations.state.confidence).toBe('authoritative'); expect(run.facade.actions.state.notice).toContain('newer'); run.dispose();
});

test('same-revision cancellation contradiction requires reconciliation', async () => {
  const run = harness(), pending = run.facade.cancelOperation(run.observed);
  const newer = structuredClone(run.observed); newer.operationRevision = '3'; run.facade.work.observations.observeOperation(newer);
  run.finish(); await pending; expect(run.facade.work.observations.state.reason).toBe('revision_reused');
  expect(run.facade.actions.state.notice).toContain('reconciliation'); run.dispose();
});

test.each(['cancelled_before_commit', 'too_late', 'already_terminal', 'recovery_required'])(
  'cancellation reports actual %s disposition', async kind => {
    const ids = { cancelled_before_commit: 'sc15-cancel-before-commit-reply', too_late: 'sc15-cancel-too-late-reply',
      already_terminal: 'sc15-cancel-already-terminal-reply', recovery_required: 'sc15-cancel-recovery-required-reply' };
    const run = harness(ids[kind as keyof typeof ids]), pending = run.facade.cancelOperation(run.observed); run.finish();
    expect(await pending).toMatchObject({ kind: 'result', value: { kind } });
    expect(run.facade.actions.state.notice).not.toBe('Cancellation requested. Waiting for the operation outcome.'); run.dispose();
  });

test('disposal aborts only observation and cannot install a late cancellation claim', async () => {
  const run = harness(), pending = run.facade.cancelOperation(run.observed); run.facade.dispose();
  run.finish(); expect(await pending).toMatchObject({ kind: 'fault' });
  expect(run.facade.work.observations.state.operations[0]).toEqual(run.observed); expect(run.facade.actions.state.cancellingOperationIds).toEqual([]);
  expect(await run.facade.cancelOperation(run.observed)).toBeUndefined(); run.dispose();
});
