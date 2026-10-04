import { createHash } from 'node:crypto';
import { readFileSync } from 'node:fs';
import { describe, expect, test } from 'vitest';
import type { Event, Reply, Request } from '../src/generated/protocol';
import { BridgeClient } from '../src/client/client';
import { canonicalData, decodeEvent, decodeReply, type DeepReadonly } from '../src/client/wire';
import { ManualClock } from '../src/mocks/clock';
import { ScenarioTransport, type MockScript, type MockState, type MockStep } from '../src/mocks/scenario-transport';

interface SourceTranscript {
  id: string; case: string; scenario: string; expected: { accepted: boolean };
  steps: (Extract<MockStep, { type: 'boundary' }> | { type: 'exchange'; request: string; reply: string; delivery?: 'lost' | 'received' } | { type: 'event'; event: string })[];
}
const fixtureRoot = new URL('../../contracts/fixtures/', import.meta.url);
const read = (name: string) => readFileSync(new URL(name, fixtureRoot), 'utf8');
const frame = <T>(id: string): T => JSON.parse(read(`${id}.json`)) as T;
function script(name: string): MockScript {
  const raw = read(`${name}.transcript.json`);
  const source = JSON.parse(raw) as SourceTranscript;
  if (!source.expected.accepted) throw new Error('refused_transcript_is_not_normal_mock');
  const sources = [{ id: source.id, sha256: createHash('sha256').update(raw).digest('hex') }];
  const add = <T>(id: string): T => {
    sources.push({ id, sha256: createHash('sha256').update(read(`${id}.json`)).digest('hex') });
    return frame<T>(id);
  };
  const steps: MockStep[] = source.steps.map(step => step.type === 'boundary' ? step : step.type === 'event'
    ? { ...step, event: add<Event>(step.event) }
    : { ...step, request: add<Request>(step.request), reply: add<Reply>(step.reply) });
  return { id: source.id, case: source.case, scenario: source.scenario, correlationSlot: 'requestId', sources, steps };
}
let ids = 0;
const nextId = () => `${(++ids).toString(16).padStart(8, '0')}-2222-4222-8222-222222222222`;
const requestWithId = (request: DeepReadonly<Request>): string => JSON.stringify({ ...request, requestId: nextId() });
const direct = async (transport: ScenarioTransport, clock: ManualClock): Promise<DeepReadonly<Reply>> => {
  const expected = transport.expectedRequest;
  if (!expected) throw new Error('missing_expected_exchange');
  const delivery = transport.exchange(requestWithId(expected), { signal: new AbortController().signal });
  clock.advanceBy(100);
  return decodeReply(await delivery);
};
const client = (transport: ScenarioTransport, clock: ManualClock) => new BridgeClient(transport, { requestId: nextId, clock, timeoutMs: 1000 });
const invoke = (api: BridgeClient, request: DeepReadonly<Request>, options = {}) => request.body.type === 'query'
  ? api.query(request.body.query.name, request.body.query.input, options)
  : api.command(request.body.command.name, request.body.command.input, options);

describe('scripted mock oracle', () => {
  test('accepted shared transcripts preserve every frame, correlation slot, boundary, and source digest', async () => {
    const index = JSON.parse(read('index.json')) as { transcripts: { id: string; expected: { accepted: boolean }; scenario: string }[] };
    const accepted = index.transcripts.filter(entry => entry.expected.accepted);
    const catalog = JSON.parse(readFileSync(new URL('../scenarios/catalog.json', import.meta.url), 'utf8')) as { scripts: MockScript[] };
    expect(catalog.scripts.map(script => script.id).sort()).toEqual(accepted.map(entry => entry.id).sort());
    const scenarios = new Set<string>();
    for (const entry of accepted) {
      const shared = script(entry.id);
      const source = catalog.scripts.find(script => script.id === entry.id)!;
      expect(canonicalData(source.steps)).toBe(canonicalData(shared.steps));
      expect(source.correlationSlot).toBe('requestId');
      for (const provenance of source.sources) {
        expect(provenance.sha256).toBe(shared.sources.find(reference => reference.id === provenance.id)?.sha256);
      }
      const clock = new ManualClock();
      const transport = new ScenarioTransport(source, { clock });
      const events: DeepReadonly<Event>[] = [];
      const subscribe = () => transport.subscribe(raw => events.push(decodeEvent(raw)));
      subscribe();
      for (const step of source.steps) {
        if (step.type !== 'exchange') continue;
        clock.runAll();
        if (!transport.state.connected) { transport.reconnect(); clock.runAll(); }
        expect(canonicalData(transport.expectedRequest)).toBe(canonicalData(step.request));
        const request = JSON.parse(requestWithId(step.request)) as Request;
        const controller = new AbortController();
        const observed = transport.exchange(JSON.stringify(request), { signal: controller.signal }).then(raw => ({ raw }), fault => ({ fault }));
        clock.advanceBy(100);
        if (step.delivery === 'lost') controller.abort();
        const result = await observed;
        if (step.delivery === 'lost') expect('fault' in result).toBe(true);
        else {
          expect('raw' in result).toBe(true);
          if ('raw' in result) expect(canonicalData(decodeReply(result.raw))).toBe(canonicalData({ ...step.reply, requestId: request.requestId }));
        }
      }
      clock.runAll();
      expect(transport.state.position).toBe(source.steps.length);
      expect(events.map(canonicalData)).toEqual(source.steps.filter(step => step.type === 'event').map(step => canonicalData(step.event)));
      scenarios.add(source.scenario);
      transport.dispose();
      expect(clock.pendingCount).toBe(0);
    }
    expect(accepted.length).toBeGreaterThan(100);
    expect([...scenarios].sort()).toEqual(Array.from({ length: 18 }, (_, index) => `SC-${String(index + 1).padStart(2, '0')}`));
  });

  test('request matching changes only declared correlation, preserving command inputs and detached scripts', async () => {
    const source = script('sc14-lost-response-restart-exact-replay');
    const clock = new ManualClock();
    const transport = new ScenarioTransport(source, { clock });
    const original = structuredClone(source.steps[1]);
    if (source.steps[1].type === 'exchange') source.steps[1].request.body = { type: 'query', query: { name: 'hello', input: {} } };
    expect(canonicalData(transport.expectedRequest)).toBe(canonicalData(original.type === 'exchange' ? original.request : undefined));
    await expect(transport.exchange(JSON.stringify(frame<Request>('sc14-admit-request')), { signal: new AbortController().signal })).rejects.toEqual({ code: 'delivery_failed', delivery: 'not_sent' });
    expect(transport.state.lastFault).toBe('unexpected_request');
    expect(transport.state.position).toBe(1);
    const reply = await direct(transport, clock);
    expect(reply.requestId).not.toBe((original as Extract<MockStep, { type: 'exchange' }>).request.requestId);
    expect(reply.body.type).toBe('result');
    transport.dispose();
  });

  test('abort before dispatch sends nothing; abort after dispatch leaves backend events and replay record', async () => {
    const source = script('sc-03-working-then-observed-ready-journey');
    const clock = new ManualClock();
    const transport = new ScenarioTransport(source, { clock });
    const api = client(transport, clock);
    const events: string[] = [];
    api.subscribe(event => events.push(event.cursor.sequence));
    const pre = new AbortController(); pre.abort();
    const initial = transport.expectedRequest!;
    const notSent = await invoke(api, initial, { signal: pre.signal });
    expect(notSent).toMatchObject({ kind: 'fault', fault: { code: 'observational_abort', delivery: 'not_sent' } });
    expect(transport.state.position).toBe(1);
    const prepared = invoke(api, initial);
    clock.advanceBy(100); await prepared;
    const abort = new AbortController();
    const pending = invoke(api, transport.expectedRequest!, { signal: abort.signal });
    abort.abort();
    expect(await pending).toMatchObject({ kind: 'fault', fault: { code: 'observational_abort', delivery: 'may_have_reached_backend' } });
    expect(transport.state.replayCount).toBe(0);
    clock.runAll();
    expect(events).toEqual(['1', '2']);
    expect(transport.state.replayCount).toBe(1);
    expect(transport.state.position).toBe(source.steps.length);
    expect(api.replayCount).toBe(1);
    api.dispose(); transport.dispose();
  });

  test('lost admitted reply survives restart and declared exact replay returns original operation', async () => {
    const source = script('sc14-lost-response-restart-exact-replay');
    const clock = new ManualClock();
    const transport = new ScenarioTransport(source, { clock });
    const api = client(transport, clock);
    const preparation = invoke(api, transport.expectedRequest!);
    clock.advanceBy(100); await preparation;
    const commitRequest = transport.expectedRequest!;
    if (commitRequest.body.type !== 'command' || commitRequest.body.command.name !== 'commit') throw new Error('expected_commit');
    const key = commitRequest.body.command.input.idempotencyKey;
    const lost = invoke(api, commitRequest);
    clock.advanceBy(100);
    expect(await lost).toMatchObject({ kind: 'fault', fault: { code: 'disconnected', delivery: 'may_have_reached_backend' } });
    expect(transport.state).toMatchObject({ connected: false, replayCount: 1, boundary: { reason: 'restart' } });
    expect(canonicalData(api.getReplay(key)!.input)).toBe(canonicalData(commitRequest.body.command.input));
    transport.reconnect();
    const replay = api.replayCommit(key);
    clock.advanceBy(100);
    const outcome = await replay;
    expect(outcome.kind).toBe('result');
    if (outcome.kind === 'result') {
      const scripted = source.steps.find(step => step.type === 'exchange' && step.delivery === 'lost') as Extract<MockStep, { type: 'exchange' }>;
      if (scripted.reply.body.type !== 'result' || scripted.reply.body.result.type !== 'command' || scripted.reply.body.result.command.name !== 'commit') throw new Error('expected_admission');
      expect(outcome.value.operationId).toBe(scripted.reply.body.result.command.output.operationId);
    }
    expect(transport.state.replayCount).toBe(1);
    await expect(transport.exchange(requestWithId(commitRequest), { signal: new AbortController().signal })).rejects.toEqual({ code: 'delivery_failed', delivery: 'not_sent' });
    expect(transport.state.lastFault).toBe('unexpected_request');
    api.dispose(); transport.dispose();
  });

  test('injected lost reply times out locally while the next script step remains available', async () => {
    const clock = new ManualClock();
    const transport = new ScenarioTransport(script('sc-03-working-then-observed-ready-journey'), { clock });
    const api = client(transport, clock);
    transport.dropNextReply();
    const waiting = invoke(api, transport.expectedRequest!);
    clock.advanceBy(100);
    expect(transport.expectedRequest?.body.type).toBe('command');
    clock.advanceBy(900);
    expect(await waiting).toMatchObject({ kind: 'fault', fault: { code: 'timeout', delivery: 'may_have_reached_backend' } });
    expect(transport.state.pendingExchanges).toBe(0);
    expect(transport.state.failureMode).toBe(true);
    api.dispose(); transport.dispose();
  });

  test('a scripted busy refusal retains its error and does not claim an admitted replay record', async () => {
    const clock = new ManualClock();
    const transport = new ScenarioTransport(script('sc14-modeled-second-submit-busy'), { clock });
    const api = client(transport, clock);
    for (let index = 0; index < 2; index++) {
      const pending = invoke(api, transport.expectedRequest!);
      clock.advanceBy(100);
      expect((await pending).kind).toBe('result');
    }
    expect(transport.state.replayCount).toBe(1);
    const refused = invoke(api, transport.expectedRequest!);
    clock.advanceBy(100);
    expect(await refused).toMatchObject({ kind: 'rejected', error: { code: 'operation_busy', retryDisposition: 'after_user_choice' } });
    expect(transport.state.replayCount).toBe(1);
    api.dispose(); transport.dispose();
  });

  test('same-epoch reconnect, retention refusal and resnapshot expose only declared observations', async () => {
    const source = script('sc14-reconnect-retention-gap-resnapshot');
    const clock = new ManualClock();
    const transport = new ScenarioTransport(source, { clock });
    const cursors: Event['cursor'][] = [];
    transport.subscribe(raw => cursors.push(decodeEvent(raw).cursor));
    const initialEpoch = transport.state.boundary!.cursor.hostEpoch;
    await direct(transport, clock); await direct(transport, clock); clock.runAll();
    expect(transport.state.connected).toBe(false);
    expect(transport.state.boundary!.cursor.hostEpoch).toBe(initialEpoch);
    transport.reconnect(); clock.runAll();
    const gap = await direct(transport, clock); clock.runAll();
    expect(gap.body.type).toBe('rejected');
    expect(transport.state.boundary?.reason).toBe('resnapshot');
    await direct(transport, clock); clock.runAll();
    expect(cursors.map(cursor => cursor.sequence)).toEqual(['1', '2', '21', '1']);
    expect(cursors.at(-1)!.streamId).not.toBe(cursors[0].streamId);
    transport.dispose();
  });

  test('malformed reply/event fault probes use common validation without rewriting golden frames', async () => {
    const clock = new ManualClock();
    const transport = new ScenarioTransport(script('sc-03-working-then-observed-ready-journey'), { clock });
    const api = client(transport, clock);
    const failures: string[] = [];
    api.subscribe(() => {}, fault => failures.push(fault.code));
    transport.injectEvent('{"protocolVersion":1,"protocolVersion":1}');
    clock.advanceBy(0);
    expect(failures).toEqual(['framing']);
    const malicious = new TextEncoder().encode('{"protocolVersion":1,"requestId":null,"body":{"type":"rejected","error":{"code":"operation_busy","retryDisposition":"after_user_choice","violations":[]}},"unknown":true}');
    transport.injectNextReply(malicious);
    malicious.fill(0);
    const waiting = invoke(api, transport.expectedRequest!);
    clock.advanceBy(100);
    expect(await waiting).toMatchObject({ kind: 'fault', fault: { code: 'schema', delivery: 'may_have_reached_backend' } });
    expect(transport.state.failureMode).toBe(true);
    api.dispose(); transport.dispose();
  });

  test('reset removes old observers and tasks while dispose never executes further work', async () => {
    const clock = new ManualClock();
    const source = script('sc-03-working-then-observed-ready-journey');
    const transport = new ScenarioTransport(source, { clock });
    const events: string[] = [];
    transport.subscribe(raw => events.push(decodeEvent(raw).cursor.sequence));
    const pending = transport.exchange(requestWithId(transport.expectedRequest!), { signal: new AbortController().signal }).catch(fault => fault);
    expect(clock.pendingCount).toBe(1);
    transport.reset();
    expect(await pending).toEqual({ code: 'disconnected', delivery: 'may_have_reached_backend' });
    expect(clock.pendingCount).toBe(0);
    expect(transport.state).toMatchObject({ position: 1, replayCount: 0, pendingExchanges: 0, failureMode: false });
    await direct(transport, clock); await direct(transport, clock); clock.runAll();
    expect(events).toEqual([]);
    transport.reset();
    const discarded = transport.exchange(requestWithId(transport.expectedRequest!), { signal: new AbortController().signal }).catch(fault => fault);
    transport.dispose();
    expect(await discarded).toEqual({ code: 'disconnected', delivery: 'may_have_reached_backend' });
    clock.runAll();
    expect(clock.pendingCount).toBe(0);
    expect(transport.state).toMatchObject({ disposed: true, backendBusy: false });
  });

  test.each(['reset', 'dispose'] as const)('an event observer can %s without delivering to old observers or advancing the old script', async action => {
    const clock = new ManualClock();
    const source = script('sc-03-working-then-observed-ready-journey');
    const transport = new ScenarioTransport(source, { clock });
    const firstObserver: string[] = [];
    const secondObserver: string[] = [];
    const oldFaults: string[] = [];
    transport.subscribe(raw => {
      firstObserver.push(decodeEvent(raw).cursor.sequence);
      transport[action]();
      throw new Error('synthetic_observer_failure');
    }, fault => oldFaults.push(fault.code));
    transport.subscribe(raw => secondObserver.push(decodeEvent(raw).cursor.sequence));
    await direct(transport, clock); await direct(transport, clock);
    expect(transport.state.position).toBe(3);
    clock.advanceBy(50);
    expect(firstObserver).toEqual(['1']);
    expect(secondObserver).toEqual([]);
    expect(oldFaults).toEqual([]);
    expect(transport.state).toMatchObject({ position: action === 'reset' ? 1 : 3, backendBusy: false, disposed: action === 'dispose' });
    if (action === 'reset') expect(canonicalData(transport.expectedRequest)).toBe(canonicalData((source.steps[1] as Extract<MockStep, { type: 'exchange' }>).request));
    clock.runAll();
    expect(clock.pendingCount).toBe(0);
    expect(firstObserver).toEqual(['1']);
    expect(secondObserver).toEqual([]);
    transport.dispose();
  });

  test('an observer reset can immediately dispatch new work without the old event changing it', async () => {
    const clock = new ManualClock();
    const source = script('sc-03-working-then-observed-ready-journey');
    const transport = new ScenarioTransport(source, { clock });
    let freshDelivery: Promise<ReturnType<typeof decodeReply>> | undefined;
    transport.subscribe(() => {
      transport.reset();
      freshDelivery = transport.exchange(requestWithId(transport.expectedRequest!), { signal: new AbortController().signal }).then(decodeReply);
    });
    await direct(transport, clock); await direct(transport, clock);
    clock.advanceBy(50);
    expect(transport.state).toMatchObject({ position: 2, backendBusy: true, pendingExchanges: 1, scheduledTasks: 1, replayCount: 0 });
    expect(clock.pendingCount).toBe(1);
    clock.advanceBy(100);
    expect((await freshDelivery)!.body.type).toBe('result');
    expect(transport.state).toMatchObject({ position: 2, backendBusy: false, pendingExchanges: 0, scheduledTasks: 0 });
    expect(canonicalData(transport.expectedRequest)).toBe(canonicalData((source.steps[2] as Extract<MockStep, { type: 'exchange' }>).request));
    transport.dispose();
  });

  test.each(['reset', 'dispose'] as const)('a disconnect observer can %s without notifying the old fault snapshot', async action => {
    const clock = new ManualClock();
    const transport = new ScenarioTransport(script('sc-03-working-then-observed-ready-journey'), { clock });
    const firstFaults: string[] = [];
    const secondFaults: string[] = [];
    const states: MockState[] = [];
    transport.subscribe(() => {}, fault => { firstFaults.push(fault.code); transport[action](); });
    transport.subscribe(() => {}, fault => secondFaults.push(fault.code));
    transport.subscribeState(state => states.push(state));
    await direct(transport, clock); await direct(transport, clock);
    states.length = 0;
    transport.disconnect();
    expect(firstFaults).toEqual(['disconnected']);
    expect(secondFaults).toEqual([]);
    expect(states.map(state => state.lastFault)).toEqual([action === 'reset' ? 'reset' : 'disposed']);
    clock.runAll();
    expect(transport.state.position).toBe(action === 'reset' ? 1 : 3);
    expect(clock.pendingCount).toBe(0);
    transport.dispose();
  });

  test.each(['reset', 'dispose'] as const)('a state observer can %s without continuing the old notification snapshot', action => {
    const transport = new ScenarioTransport(script('sc-03-working-then-observed-ready-journey'));
    const observed: MockState[] = [];
    let acted = false;
    transport.subscribeState(state => {
      if (state.failureMode && !acted) { acted = true; transport[action](); }
    });
    transport.subscribeState(state => observed.push(state));
    observed.length = 0;
    transport.dropNextReply();
    expect(observed.map(state => state.lastFault)).toEqual([action === 'reset' ? 'reset' : 'disposed']);
    expect(observed[0].disposed).toBe(action === 'dispose');
    transport.dispose();
  });

  test('nested state changes supersede an older notification within the same scenario', () => {
    const transport = new ScenarioTransport(script('sc-03-working-then-observed-ready-journey'));
    const observed: boolean[] = [];
    let acted = false;
    transport.subscribeState(state => {
      if (state.failureMode && !acted) { acted = true; transport.disconnect(); }
    });
    transport.subscribeState(state => observed.push(state.connected));
    observed.length = 0;
    transport.dropNextReply();
    expect(observed).toEqual([false]);
    transport.dispose();
  });

  test('an observer removed synchronously by an earlier observer does not receive the event', async () => {
    const clock = new ManualClock();
    const transport = new ScenarioTransport(script('sc-03-working-then-observed-ready-journey'), { clock });
    const observed: string[] = [];
    let removeSecond = () => {};
    transport.subscribe(() => removeSecond());
    removeSecond = transport.subscribe(raw => observed.push(decodeEvent(raw).cursor.sequence));
    await direct(transport, clock); await direct(transport, clock); clock.runAll();
    expect(observed).toEqual([]);
    expect(transport.state.position).toBe(5);
    transport.dispose();
  });

  test('script, observer and frame bounds fail visibly without dispatch or unscripted success', async () => {
    const source = script('sc-03-working-then-observed-ready-journey');
    expect(() => new ScenarioTransport(source, { maxSteps: 1 })).toThrow('mock_invalid_script');
    expect(() => new ScenarioTransport({ ...source, correlationSlot: 'body' as 'requestId' })).toThrow('mock_invalid_script');
    const clock = new ManualClock();
    const transport = new ScenarioTransport(source, { clock, maxPending: 1, maxFrameBytes: 20 });
    transport.subscribe(() => {});
    expect(() => transport.subscribe(() => {})).toThrow('mock_subscriber_limit');
    expect(() => transport.injectNextReply('x'.repeat(21))).toThrow('mock_frame_limit');
    const first = transport.exchange(requestWithId(transport.expectedRequest!), { signal: new AbortController().signal }).catch(fault => fault);
    await expect(transport.exchange(JSON.stringify(frame<Request>('sc14-admit-request')), { signal: new AbortController().signal })).rejects.toEqual({ code: 'delivery_failed', delivery: 'not_sent' });
    transport.dispose(); await first;
  });
});
