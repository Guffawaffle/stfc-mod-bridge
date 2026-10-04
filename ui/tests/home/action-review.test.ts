import { expect, test } from 'vitest';
import { canonicalData } from '../../src/client';
import type { Reply } from '../../src/generated/protocol';
import { catalog, deliver, frame, harness, intent, snapshot } from './helpers';

test('Home ordinary action requires explicit Review and Confirm and acknowledges an actual terminal result', async () => {
  const script = catalog('sc-02-ordinary-absent-journey'), run = harness(script), actions = run.facade.actions;
  await deliver(actions.prepare(intent(script), 'home-launch'), run.clock);
  expect(actions.state.transition.kind).toBe('review'); expect(run.client.replayCount).toBe(0);
  expect(run.transport.expectedRequest?.body.type === 'command' && run.transport.expectedRequest.body.command.name).toBe('commit');
  await deliver(actions.confirm(), run.clock);
  expect(actions.state.transition.kind).toBe('idle'); expect(actions.state.notice).toBe('Action completed.'); expect(run.client.replayCount).toBe(0); run.dispose();
});

test('Home isolated admission retains captured work while later target selection changes and readiness arrives', async () => {
  const script = catalog('sc-03-working-then-observed-ready-journey'), run = harness(script), actions = run.facade.actions;
  const requested = intent(script); await deliver(actions.prepare(requested), run.clock); await deliver(actions.confirm(), run.clock);
  expect(actions.state.transition.kind).toBe('observing'); expect(actions.state.notice).toBe('Action admitted. Waiting for the operation outcome.');
  expect(actions.state.notice).not.toContain('completed'); expect(run.client.replayCount).toBe(1);
  const captured = canonicalData(run.facade.work.observations.state.operations[0].semantics);
  const next = frame('sc-03-profile-two-prepare-request').body.command.input.intent.input.target;
  expect(run.facade.requestTarget(next)).toBe(true); expect(canonicalData(run.facade.work.observations.state.operations[0].semantics)).toBe(captured);
  run.clock.advanceBy(50); expect(run.facade.work.observations.state.operations[0].state.status).toBe('running');
  run.clock.advanceBy(50); expect(actions.state.notice).toBe('Action completed.'); expect(run.client.replayCount).toBe(0); run.dispose();
});

test('Home focus preparation captures exact PID start executable and session instead of the visible target', async () => {
  const script = catalog('sc-04-focus-exact-session-journey'), run = harness(script), actions = run.facade.actions;
  const requested = intent(script); run.facade.requestTarget(frame('sc-03-profile-two-prepare-request').body.command.input.intent.input.target);
  await deliver(actions.prepare(requested), run.clock);
  const capture = actions.state.plan?.semantics.capture;
  expect(capture?.kind).toBe('focus_session');
  if (capture?.kind !== 'focus_session' || requested.kind !== 'focus_session') throw new Error('focus_fixture_shape');
  expect(capture.session).toEqual(requested.input.session); expect(capture.session.process.pid).toBe(4300);
  await deliver(actions.confirm(), run.clock); expect(actions.state.notice).toBe('Action completed.'); run.dispose();
});

test('Home shared entry points exclude competing target close Save and Discard through uncertain admission', async () => {
  const script = catalog('sc14-lost-response-restart-exact-replay'), run = harness(script), actions = run.facade.actions;
  const target = frame('sc-03-profile-two-prepare-request').body.command.input.intent.input.target;
  const assertExcluded = async () => {
    expect(run.facade.requestTarget(target)).toBe(false); expect(run.facade.requestClose()).toBe(false);
    expect(await run.facade.prepareSave()).toBeUndefined(); expect(await run.facade.discard()).toBe(false);
    expect(await actions.prepare(intent(script))).toBeUndefined(); expect(run.facade.work.state.pendingNavigation).toBeUndefined();
  };
  const prepare = actions.prepare(intent(script)); await assertExcluded(); await deliver(prepare, run.clock); await assertExcluded();
  const commit = actions.confirm(); await assertExcluded(); await deliver(commit, run.clock, 1000); await assertExcluded();
  expect(actions.state.transition.kind).toBe('uncertain'); expect(actions.stay()).toBe(false); run.dispose();
});

test('Home pending dirty draft and Save review exclude generic preparation while view navigation preserves edits', async () => {
  const script = catalog('sc-02-ordinary-absent-journey'), run = harness(script);
  const draft = frame('sc08-open-clean-draft-reply').body.result.command.output, edits = frame('sc08-stage-dirty-draft-request').body.command.input.edits;
  run.facade.work.openDraft(draft); run.facade.stage(edits); run.facade.requestClose('home-target');
  expect(await run.facade.actions.prepare(intent(script))).toBeUndefined();
  run.facade.navigate('engineering'); run.facade.navigate('home'); expect(run.facade.work.state.edits).toEqual(edits); expect(run.facade.work.state.draft).toEqual(draft);
  const review = run.facade.work.beginReview()!; expect(await run.facade.actions.prepare(intent(script))).toBeUndefined();
  run.facade.work.finishSave(review, { kind: 'fault', fault: { code: 'observational_abort', delivery: 'not_sent' } });
  expect(run.facade.stay()).toBe(true); await deliver(run.facade.actions.prepare(intent(script)), run.clock);
  expect(run.facade.actions.state.transition.kind).toBe('review'); expect(run.facade.work.state.edits).toEqual(edits); run.dispose();
});

test('Home lost commit and replay rejection preserve the exact submission until terminal reconciliation', async () => {
  const script = catalog('sc14-lost-response-restart-exact-replay'), run = harness(script), actions = run.facade.actions;
  await deliver(actions.prepare(intent(script)), run.clock); await deliver(actions.confirm(), run.clock, 1000);
  const key = [...script.steps].find(step => step.type === 'exchange' && step.request.body.type === 'command' && step.request.body.command.name === 'commit');
  if (key?.type !== 'exchange' || key.request.body.type !== 'command' || key.request.body.command.name !== 'commit') throw new Error('commit_fixture');
  const retained = run.client.getReplay(key.request.body.command.input.idempotencyKey);
  expect(retained?.input).toEqual(key.request.body.command.input); expect(actions.state.transition.kind).toBe('uncertain');
  run.transport.reconnect(); const rejection = frame('sc10-save-stale-revision-reply'); rejection.body.error.code = 'persistence_failed'; rejection.body.error.retryDisposition = 'never';
  run.inject(rejection); expect(await deliver(actions.replay(), run.clock)).toMatchObject({ kind: 'rejected' });
  expect(actions.state.transition.kind).toBe('uncertain'); expect(actions.stay()).toBe(false); expect(run.client.getReplay(retained!.input.idempotencyKey)?.input).toEqual(retained?.input);
  run.dispose(); expect(run.client.getReplay(retained!.input.idempotencyKey)?.input).toEqual(retained?.input);
});

test('Home initial sent rejection remains uncertain and disposal never forgets its owned replay', async () => {
  for (const code of ['persistence_failed', 'internal_failure']) {
    const script = catalog('sc-02-ordinary-absent-journey'), run = harness(script), actions = run.facade.actions;
    await deliver(actions.prepare(intent(script)), run.clock);
    const rejection = frame('sc10-save-stale-revision-reply'); rejection.body.error.code = code; rejection.body.error.retryDisposition = 'never'; run.inject(rejection);
    expect(await deliver(actions.confirm(), run.clock)).toMatchObject({ kind: 'rejected', error: { code } });
    expect(actions.state.transition.kind).toBe('uncertain'); expect(actions.stay()).toBe(false); expect(run.client.replayCount).toBe(1);
    run.dispose(); expect(run.client.replayCount).toBe(1);
  }
});

test('Home exact lost-response replay observes the original admission without claiming completion or cancellation', async () => {
  const script = catalog('sc14-lost-response-restart-exact-replay'), run = harness(script), actions = run.facade.actions;
  await deliver(actions.prepare(intent(script)), run.clock); await deliver(actions.confirm(), run.clock, 1000);
  expect(actions.state.transition.kind).toBe('uncertain'); run.transport.reconnect(); await deliver(actions.replay(), run.clock);
  const admitted = run.facade.work.observations.state.operations[0]; expect(admitted.state.status).toBe('admitted');
  expect(actions.state.transition).toEqual({ kind: 'observing', operationId: admitted.operationId });
  await deliver(actions.reconcile(), run.clock); expect(run.facade.work.observations.state.operations[0].operationId).toBe(admitted.operationId);
  expect(actions.state.notice).not.toContain('completed'); expect(run.client.replayCount).toBe(1); run.dispose(); expect(run.client.replayCount).toBe(1);
});

test('Home proved-unsent Stay releases only the captured replay and restores a connected opener', async () => {
  const script = catalog('sc-02-ordinary-absent-journey'), run = harness(script), actions = run.facade.actions; let focused = 0;
  run.facade.focus.register('home-launch', () => ({ isConnected: true, focus() { focused++; } }));
  await deliver(actions.prepare(intent(script), 'home-launch'), run.clock); run.transport.disconnect(); await actions.confirm();
  expect(actions.state.transition.kind).toBe('review'); expect(run.client.replayCount).toBe(1);
  expect(actions.stay()).toBe(true); expect(run.client.replayCount).toBe(0); expect(focused).toBe(1); run.dispose();
});

test('Home generic terminal reconciliation permits two actions with one replay slot', async () => {
  const first = catalog('sc-02-ordinary-absent-journey'), second = catalog('sc-04-focus-exact-session-journey');
  const pairs = (script: typeof first) => script.steps.filter(step => step.type === 'exchange' && step.request.body.type === 'command');
  const run = harness({ ...first, steps: [...pairs(first), ...pairs(second)] }, 1);
  for (const script of [first, second]) {
    await deliver(run.facade.actions.prepare(intent(script)), run.clock); expect(await deliver(run.facade.actions.confirm(), run.clock)).toMatchObject({ kind: 'result' });
    expect(run.facade.actions.state.notice).toBe('Action completed.'); expect(run.client.replayCount).toBe(0);
  }
  run.dispose();
});

test('Home stale epoch and disposed preparation cannot install a late review', async () => {
  for (const dispose of [false, true]) {
    const script = catalog('sc-02-ordinary-absent-journey'), run = harness(script), actions = run.facade.actions;
    const pending = actions.prepare(intent(script));
    if (dispose) run.facade.dispose();
    else { const changed = snapshot(); changed.cursor = { ...changed.cursor, hostEpoch: '00000002-2222-4222-8222-222222222222' }; run.facade.work.observations.acceptSnapshot(changed); }
    await deliver(pending, run.clock); expect(actions.state.transition.kind).not.toBe('review'); expect(actions.state.plan).toBeUndefined(); run.dispose();
  }
});

test('Home foreign prepared profile capture cannot reach confirmation or allocate replay', async () => {
  const script = catalog('sc-03-profile-one-journey'), run = harness(script);
  run.inject(frame('sc-03-profile-two-prepare-reply') as Reply);
  expect(await deliver(run.facade.actions.prepare(intent(script)), run.clock)).toMatchObject({ kind: 'fault', fault: { code: 'correlation' } });
  expect(run.facade.actions.state.transition.kind).toBe('idle'); expect(await run.facade.actions.confirm()).toBeUndefined(); expect(run.client.replayCount).toBe(0); run.dispose();
});

test('Home denied runtime review presents the typed per-attempt choice without implicit consent or confirmation', async () => {
  const script = catalog('sc-02-unrecognized-without-consent-journey'), run = harness(script), requested = intent(script);
  expect(await deliver(run.facade.actions.prepare(requested), run.clock)).toMatchObject({ kind: 'rejected', error: { code: 'artifact_unrecognized' } });
  expect(run.facade.actions.state.transition.kind).toBe('idle'); expect(run.facade.actions.state.notice).toContain('consent choice');
  expect(run.facade.actions.state.plan).toBeUndefined(); expect(run.client.replayCount).toBe(0); expect(await run.facade.actions.confirm()).toBeUndefined(); run.dispose();
});

test('Home conflicting preexisting replay remains owned by its original caller after Stay', async () => {
  const script = catalog('sc-02-ordinary-absent-journey'), run = harness(script), actions = run.facade.actions;
  const commit = script.steps.find(step => step.type === 'exchange' && step.request.body.type === 'command' && step.request.body.command.name === 'commit');
  if (commit?.type !== 'exchange' || commit.request.body.type !== 'command' || commit.request.body.command.name !== 'commit') throw new Error('commit_fixture');
  const input = { ...commit.request.body.command.input, planRef: { ...commit.request.body.command.input.planRef, planId: '00000002-3333-4333-8333-333333333333' } };
  await run.client.command('commit', input); const retained = run.client.getReplay(input.idempotencyKey);
  await deliver(actions.prepare(intent(script)), run.clock); expect(await actions.confirm()).toMatchObject({ kind: 'fault', fault: { code: 'replay_conflict' } });
  expect(actions.stay()).toBe(true); expect(run.client.getReplay(input.idempotencyKey)).toBe(retained); run.dispose(); expect(run.client.getReplay(input.idempotencyKey)).toBe(retained);
});
