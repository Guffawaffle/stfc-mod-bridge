import { expect, test } from 'vitest';
import { canonicalData, ObservationStore } from '../../src/client';
import type { Reply, Request, SessionBinding } from '../../src/generated/protocol';
import type { MockStep } from '../../src/mocks/scenario-transport';
import { HomeController, selectorFor } from '../../src/views/home';
import { catalog, deliver, exchange, frame, harness, reply, request, snapshot } from './helpers';

const script = (steps: MockStep[]) => ({ ...catalog('sc-02-ordinary-ready-action-journey'), id: 'home-read-only-fixture-composition', steps });
async function readTarget(controller: HomeController, clock: ReturnType<typeof harness>['clock']) {
  const pending = controller.inspectTarget(); clock.advanceBy(100);
  for (let turn = 0; turn < 8; turn++) await Promise.resolve();
  clock.advanceBy(100); await pending;
}
function targetRun(availability = 'sc-02-ordinary-ready-action-reply') {
  const resolve = request('sc-01-resolve-explicit-ordinary-request');
  const run = harness(script([exchange(resolve, reply('sc-01-resolve-explicit-ordinary-reply')),
    exchange(request('sc-02-ordinary-ready-action-request'), reply(availability))]));
  if (resolve.body.type !== 'query' || resolve.body.query.name !== 'resolve_target') throw new Error('resolve_fixture');
  run.facade.requestTarget(resolve.body.query.input.target); const controller = new HomeController(run.facade); return { ...run, controller };
}
function focusPair(binding: SessionBinding): MockStep {
  const input = request('sc-02-ordinary-ready-action-request') as Request;
  if (input.body.type !== 'query') throw new Error('query_fixture');
  input.body.query = { name: 'get_actions', input: { actions: ['focus_session'], scope: { kind: 'session', session: binding } } };
  const output = reply('sc-02-ordinary-ready-action-reply');
  if (output.body.type !== 'result' || output.body.result.type !== 'query' || output.body.result.query.name !== 'get_actions') throw new Error('actions_fixture');
  output.body.result.query.output[0].action = 'focus_session'; return exchange(input, output);
}

test('Home reads scoped ordinary availability without auto selecting an installation profile or session', async () => {
  const run = targetRun(); expect(run.controller.launchIntent()).toBeUndefined(); await readTarget(run.controller, run.clock);
  expect(run.controller.state.launch?.availability.status).toBe('available'); expect(run.controller.launchIntent()).toEqual({ kind: 'launch_ordinary', input: { target: run.facade.work.state.selector } });
  expect(run.facade.work.state.binding?.profile.kind).toBe('ordinary'); expect(run.client.replayCount).toBe(0);
  expect(run.transport.state.position).toBe(2); run.controller.dispose(); run.dispose();
});

test('Home unknown availability never becomes ready stopped or an enabled launch', async () => {
  const run = targetRun('sc-02-ordinary-unknown-action-reply'); await readTarget(run.controller, run.clock);
  expect(run.controller.state.launch?.availability.status).toBe('unknown'); expect(run.controller.launchIntent()).toBeUndefined();
  expect(run.facade.work.state.binding?.profile.kind).toBe('ordinary'); run.controller.dispose(); run.dispose();
});

test('Home isolated launch preserves immutable ID and requires an explicit new resume or existing mode', async () => {
  const observed = new ObservationStore(); observed.acceptSnapshot(snapshot());
  const chosen = selectorFor('aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa', 'eeeeeeeeeeeeeeeeeeeeeeeeeeeeeeee', observed.state)!;
  const resolve = request('sc-01-resolve-explicit-ordinary-request');
  if (resolve.body.type !== 'query' || resolve.body.query.name !== 'resolve_target') throw new Error('resolve_fixture'); resolve.body.query.input.target = structuredClone(chosen);
  const resolved = reply('sc-01-resolve-explicit-ordinary-reply');
  if (resolved.body.type !== 'result' || resolved.body.result.type !== 'query' || resolved.body.result.query.name !== 'resolve_target') throw new Error('resolve_reply');
  const actual = frame('sc-03-profile-two-prepare-reply').body.result.command.output.semantics.capture.target;
  resolved.body.result.query.output.target = { ...frame('sc-01-resolve-explicit-ordinary-reply').body.result.query.output.target, value: actual };
  const query = request('sc-02-ordinary-ready-action-request');
  if (query.body.type !== 'query') throw new Error('query_fixture'); query.body.query = { name: 'get_actions', input: { actions: ['launch_isolated'], scope: { kind: 'target', target: actual } } };
  const available = reply('sc-02-ordinary-ready-action-reply');
  if (available.body.type !== 'result' || available.body.result.type !== 'query' || available.body.result.query.name !== 'get_actions') throw new Error('actions_fixture'); available.body.result.query.output[0].action = 'launch_isolated';
  const run = harness(script([exchange(resolve, resolved), exchange(query, available)])); run.facade.requestTarget(chosen); const controller = new HomeController(run.facade);
  await readTarget(controller, run.clock); expect(controller.launchIntent()).toBeUndefined();
  for (const mode of ['new', 'resume', 'existing'] as const) {
    const value = controller.launchIntent(mode); expect(value?.kind).toBe('launch_isolated');
    if (value?.kind !== 'launch_isolated') throw new Error('isolated_intent'); expect(value.input.target.profile.id).toBe('eeeeeeeeeeeeeeeeeeeeeeeeeeeeeeee'); expect(value.input.storeMode).toBe(mode);
  }
  expect(controller.launchIntent('existing', 'allow_once')).toMatchObject({ input: { unrecognizedRuntimeChoice: 'allow_once' } }); controller.dispose(); run.dispose();
});

test('Home conflicting registered identity refuses binding and never queries launch availability', async () => {
  const resolve = request('sc-01-resolve-explicit-ordinary-request'), foreign = reply('sc-01-resolve-explicit-ordinary-reply');
  if (foreign.body.type !== 'result' || foreign.body.result.type !== 'query' || foreign.body.result.query.name !== 'resolve_target' || foreign.body.result.query.output.target.status !== 'observed') throw new Error('resolve_reply');
  foreign.body.result.query.output.target.value.installation = frame('sc-01-registered-installations-list-reply').body.result.query.output.value.items[1].binding;
  const run = harness(script([exchange(resolve, foreign)]));
  if (resolve.body.type !== 'query' || resolve.body.query.name !== 'resolve_target') throw new Error('resolve_fixture'); run.facade.requestTarget(resolve.body.query.input.target);
  const controller = new HomeController(run.facade); await deliver(controller.inspectTarget(), run.clock);
  expect(run.facade.work.state.binding).toBeUndefined(); expect(controller.launchIntent()).toBeUndefined(); expect(controller.state.notice).toContain('conflicts');
  expect(run.transport.state.position).toBe(1); controller.dispose(); run.dispose();
});

test('Home target change and disposal abandon old read observations without adopting late bindings', async () => {
  for (const dispose of [false, true]) {
    const run = targetRun(), pending = run.controller.inspectTarget();
    if (dispose) run.controller.dispose(); else run.facade.requestTarget(frame('sc-03-profile-two-prepare-request').body.command.input.intent.input.target);
    await deliver(pending, run.clock); expect(run.facade.work.state.binding).toBeUndefined(); expect(run.controller.state.launch).toBeUndefined(); run.controller.dispose(); run.dispose();
  }
});

test('Home focus availability is scoped to exact recycled process identity and unknown recheck clears old availability', async () => {
  const observed = snapshot().sessions, binding = observed.status === 'observed' ? observed.value.items[1].binding : undefined;
  if (!binding) throw new Error('sessions_fixture');
  const run = harness(script([focusPair(binding), focusPair(binding)])), controller = new HomeController(run.facade);
  await deliver(controller.inspectSession(binding), run.clock); expect(controller.focusIntent(binding)).toEqual({ kind: 'focus_session', input: { session: binding } });
  const recycled = { ...binding, process: { ...binding.process, startIdentity: { ...binding.process.startIdentity, value: 'another-process-generation' } } };
  expect(controller.focusIntent(recycled)).toBeUndefined(); run.transport.disconnect(); await controller.inspectSession(binding);
  expect(controller.focusIntent(binding)).toBeUndefined(); controller.dispose(); run.dispose();
});

test('Home superseded focus observation cannot restore availability after a later failed check', async () => {
  const observed = snapshot().sessions; if (observed.status !== 'observed') throw new Error('sessions_fixture'); const binding = observed.value.items[1].binding;
  const run = harness(script([focusPair(binding)])), controller = new HomeController(run.facade);
  const old = controller.inspectSession(binding); await controller.inspectSession(binding); await deliver(old, run.clock);
  expect(controller.focusIntent(binding)).toBeUndefined(); expect(controller.state.notice).toContain('not confirmed'); controller.dispose(); run.dispose();
});

test('Home stale inventories revoke displayed availability while exact captured operation facts remain in the shared store', async () => {
  const run = targetRun(); await readTarget(run.controller, run.clock); expect(run.controller.launchIntent()).toBeDefined();
  const operation = frame('sc-03-profile-one-commit-reply').body.result.command.output; run.facade.work.observations.observeOperation(operation);
  const captured = canonicalData(run.facade.work.observations.state.operations); run.facade.work.observations.invalidate('sequence_gap');
  expect(run.controller.launchIntent()).toBeUndefined(); expect(run.controller.state.launch).toBeUndefined();
  expect(canonicalData(run.facade.work.observations.state.operations)).toBe(captured); run.controller.dispose(); run.dispose();
});
