import { expect, test } from 'vitest';
import { readFileSync } from 'node:fs';
import { TauriTransport, TauriRegistrationKeys, type BridgeInvoke, type BridgeInvokeCommand } from '../src/client/tauri/transport';
import { BridgeClient } from '../src/client/client';
import { ObservationSession, ObservationStore } from '../src/client/observation';
import { decodeReply } from '../src/client/wire';

const epoch = '00000000-0000-4000-8000-000000000001';
const raw = (name: string) => readFileSync(new URL('../../contracts/fixtures/' + name + '.json', import.meta.url), 'utf8');
const flush = async () => { for (let i = 0; i < 12; i++) await Promise.resolve(); };
function deferred<T>() { let resolve!: (value: T) => void, reject!: (value: unknown) => void; const promise = new Promise<T>((yes, no) => { resolve = yes; reject = no; }); return { promise, resolve, reject }; }
class Clock {
  tasks = new Map<object, { delay: number; callback: () => void }>();
  schedule(delay: number, callback: () => void) { const token = {}; this.tasks.set(token, { delay, callback }); return () => { this.tasks.delete(token); }; }
  tick() { const [token, task] = this.tasks.entries().next().value!; this.tasks.delete(token); task.callback(); return task.delay; }
}
type Invocation = ReturnType<typeof deferred<unknown>> & { command: BridgeInvokeCommand; arguments_: Readonly<Record<string, string>> };
function setup(autoCleanup = true) {
  const calls: Invocation[] = [], clock = new Clock();
  const invoke: BridgeInvoke = (command, arguments_) => {
    const call: Invocation = { command, arguments_, ...deferred<unknown>() }; calls.push(call);
    if (autoCleanup && command === 'bridge_unsubscribe') call.resolve({ schemaVersion: 1, registrationKey: arguments_.registrationKey });
    return call.promise;
  };
  const keys = new TauriRegistrationKeys(epoch), transport = new TauriTransport({ invoke, keys, clock });
  const of = (command: BridgeInvokeCommand) => calls.filter(call => call.command === command);
  const ack = (call: Invocation) => call.resolve({ schemaVersion: 1, registrationKey: call.arguments_.registrationKey });
  const poll = (frames: string[] = [], call = of('bridge_poll').at(-1)!) => call.resolve({ schemaVersion: 1, registrationKey: call.arguments_.registrationKey, frames });
  const ready = async () => { ack(of('bridge_subscribe').at(-1)!); await flush(); };
  return { calls, clock, invoke, keys, transport, of, ack, poll, ready };
}
function correlated(frame: string, request: string): string { const reply = JSON.parse(frame); reply.requestId = JSON.parse(request).requestId; return JSON.stringify(reply); }

test('snapshot exchange waits for a genuine exact native registration acknowledgement', async () => {
  const h = setup(); h.transport.subscribe(() => {});
  const response = h.transport.exchange('captured exact input', { signal: new AbortController().signal });
  expect(h.of('bridge_exchange')).toHaveLength(0); expect(h.transport.state.phase).toBe('starting');
  expect(h.of('bridge_subscribe')[0].arguments_).toEqual({ registrationKey: epoch + ':1' });
  await h.ready(); expect(h.of('bridge_exchange')).toHaveLength(1);
  expect(h.of('bridge_exchange')[0].arguments_).toEqual({ frame: 'captured exact input' });
  h.of('bridge_exchange')[0].resolve('raw reply'); expect(await response).toBe('raw reply'); await flush();
  expect(h.transport.state.pendingRequests).toBe(0);
});

test('initial and later polled events use the common watermark buffer during snapshot readiness', async () => {
  const h = setup(), store = new ObservationStore(); let requestOrdinal = 2;
  const client = new BridgeClient(h.transport, { clock: h.clock, requestId: () => '00000000-0000-4000-8000-' + String(requestOrdinal++).padStart(12, '0') });
  const session = new ObservationSession(client, store);
  const start = session.start(); expect(h.of('bridge_exchange')).toHaveLength(0);
  await h.ready(); h.poll([raw('sc-03-working-readiness-event')]); await flush();
  expect(store.state.confidence).toBe('uninitialized');
  const exchange = h.of('bridge_exchange')[0]; exchange.resolve(correlated(raw('sc15-complete-empty-snapshot-reply'), exchange.arguments_.frame));
  expect(await start).toMatchObject({ kind: 'result' }); await flush();
  expect(store.state.confidence).toBe('authoritative'); expect(store.state.operations[0].operationRevision).toBe('2');
  expect(store.state.cursor?.sequence).toBe('1'); expect(h.clock.tick()).toBe(10);
  h.poll([raw('sc-03-completed-readiness-event')]); await flush();
  expect(store.state.operations[0].state.status).toBe('completed'); expect(store.state.cursor?.sequence).toBe('2');
  session.dispose(); client.dispose(); expect(h.transport.state.timerScheduled).toBe(false);
});

test('last local unsubscribe revokes before readiness and cleans the known key again after late ACK', async () => {
  const h = setup(false), events: string[] = [];
  const stop = h.transport.subscribe(frame => events.push(String(frame)));
  const waiting = h.transport.exchange('request', { signal: new AbortController().signal }); const observed = waiting.catch(error => error);
  stop(); expect(h.transport.state.phase).toBe('idle'); expect(h.of('bridge_unsubscribe')).toHaveLength(1);
  expect(await observed).toEqual({ code: 'disconnected', delivery: 'not_sent' });
  h.ack(h.of('bridge_unsubscribe')[0]); await flush();
  h.ack(h.of('bridge_subscribe')[0]); await flush();
  expect(h.of('bridge_unsubscribe')).toHaveLength(2); expect(h.of('bridge_poll')).toHaveLength(0);
  expect(h.of('bridge_exchange')).toHaveLength(0); expect(events).toEqual([]);
  h.ack(h.of('bridge_unsubscribe')[1]); await flush(); expect(h.transport.state.knownRegistrations).toBe(0);
});

test('shared local listeners use one observer and dispose only after the last listener leaves', async () => {
  const h = setup(); const a = h.transport.subscribe(() => {}), b = h.transport.subscribe(() => {});
  expect(h.of('bridge_subscribe')).toHaveLength(1); a(); expect(h.of('bridge_unsubscribe')).toHaveLength(0);
  await h.ready(); h.poll(); await flush(); expect(h.clock.tasks.size).toBe(1);
  b(); expect(h.clock.tasks.size).toBe(0); expect(h.of('bridge_unsubscribe')).toHaveLength(1);
});

test('one unresolved poll across the adapter lifetime prevents reconnect readiness until real settlement', async () => {
  const h = setup(), seen: string[] = [], faults: unknown[] = [];
  const stop = h.transport.subscribe(frame => seen.push(String(frame))); await h.ready(); const old = h.of('bridge_poll')[0];
  stop(); h.transport.subscribe(() => {}, fault => faults.push(fault));
  expect(faults).toEqual([{ code: 'disconnected', delivery: 'not_sent' }]); expect(h.of('bridge_subscribe')).toHaveLength(1);
  h.poll(['late old frame'], old); await flush(); expect(seen).toEqual([]); expect(h.transport.state.pollInFlight).toBe(false);
  h.transport.subscribe(frame => seen.push(String(frame))); expect(h.of('bridge_subscribe')[1].arguments_.registrationKey).toBe(epoch + ':2');
  await h.ready(); h.poll(['fresh frame']); await flush(); expect(seen).toEqual(['fresh frame']);
});

test('slow poll holds one promise and idle/nonempty batches yield one bounded timer', async () => {
  const h = setup(); h.transport.subscribe(() => {}); await h.ready(); await flush();
  expect(h.of('bridge_poll')).toHaveLength(1); expect(h.clock.tasks.size).toBe(0);
  h.poll(); await flush(); expect(h.clock.tasks.size).toBe(1); expect(h.clock.tick()).toBe(100);
  expect(h.of('bridge_poll')).toHaveLength(2); expect(h.clock.tasks.size).toBe(0);
  h.poll(['event']); await flush(); expect(h.clock.tasks.size).toBe(1); expect(h.clock.tick()).toBe(10);
  expect(h.of('bridge_poll')).toHaveLength(3);
});

test('lost poll response faults once and never automatically resubmits an exchange mutation', async () => {
  const h = setup(), faults: unknown[] = [];
  h.transport.subscribe(() => {}, fault => faults.push(fault)); await h.ready();
  const result = h.transport.exchange('captured mutation key/input', { signal: new AbortController().signal }); const observed = result.catch(error => error); await flush();
  h.of('bridge_poll')[0].reject(new Error('private native canary'));
  expect(await observed).toEqual({ code: 'delivery_failed', delivery: 'may_have_reached_backend' }); await flush();
  expect(faults).toEqual([{ code: 'delivery_failed', delivery: 'may_have_reached_backend' }]);
  expect(h.of('bridge_exchange')).toHaveLength(1); expect(h.transport.state.phase).toBe('idle');
  expect(h.clock.tasks.size).toBe(0); expect(JSON.stringify(faults)).not.toContain('canary');
  h.of('bridge_exchange')[0].resolve('late success'); await flush(); expect(h.transport.state.pendingRequests).toBe(0);
});

test('waiting abort invokes no exchange while the common client remains conservatively uncertain', async () => {
  const h = setup(); h.transport.subscribe(() => {});
  const direct = new AbortController(); const pending = h.transport.exchange('captured input', { signal: direct.signal }); const directOutcome = pending.catch(error => error); direct.abort();
  expect(await directOutcome).toEqual({ code: 'disconnected', delivery: 'not_sent' }); expect(h.transport.state.pendingRequests).toBe(0);
  const client = new BridgeClient(h.transport, { clock: h.clock, requestId: () => epoch });
  const controller = new AbortController(), outcome = client.query('hello', {}, { signal: controller.signal }); controller.abort();
  expect(await outcome).toMatchObject({ kind: 'fault', fault: { code: 'observational_abort', delivery: 'may_have_reached_backend' } });
  await h.ready(); expect(h.of('bridge_exchange')).toHaveLength(0); client.dispose();
});

test('sent abort settles observation but retains request quota until actual invoke settlement', async () => {
  const h = setup(); h.transport.subscribe(() => {}); await h.ready();
  const signal = new AbortController(), pending = h.transport.exchange('sent', { signal: signal.signal }), observed = pending.catch(error => error); await flush();
  signal.abort(); expect(await observed).toEqual({ code: 'disconnected', delivery: 'may_have_reached_backend' });
  expect(h.transport.state.pendingRequests).toBe(1); expect(h.transport.state.pendingRequestBytes).toBe(4);
  h.of('bridge_exchange')[0].resolve('late'); await flush(); expect(h.transport.state.pendingRequests).toBe(0);
});

test('32 exchanges and 8MiB request budget include locally abandoned native invocations', async () => {
  const h = setup(); h.transport.subscribe(() => {}); await h.ready();
  const requests = Array.from({ length: 32 }, () => { const controller = new AbortController(); const promise = h.transport.exchange('x'.repeat(262144), { signal: controller.signal }); return { controller, promise: promise.catch(error => error) }; });
  await flush(); expect(h.transport.state.pendingRequestBytes).toBe(8 * 1024 * 1024); expect(h.of('bridge_exchange')).toHaveLength(32);
  requests.forEach(request => request.controller.abort()); await Promise.all(requests.map(request => request.promise));
  expect(h.transport.state.pendingRequests).toBe(32);
  await expect(h.transport.exchange('overflow', { signal: new AbortController().signal })).rejects.toEqual({ code: 'delivery_failed', delivery: 'not_sent' });
  expect(h.of('bridge_exchange')).toHaveLength(32);
  h.of('bridge_exchange')[0].resolve('late'); await flush(); expect(h.transport.state.pendingRequests).toBe(31);
  const next = h.transport.exchange('new', { signal: new AbortController().signal }); const nextObserved = next.catch(error => error); await flush(); expect(h.of('bridge_exchange')).toHaveLength(33);
  h.transport.dispose(); await nextObserved;
});

test('readiness waiters reserve quota before invoke and waiting abort releases only its own slot', async () => {
  const h = setup(); h.transport.subscribe(() => {});
  const requests = Array.from({ length: 32 }, () => {
    const controller = new AbortController(); const promise = h.transport.exchange('wait', { signal: controller.signal });
    return { controller, promise: promise.catch(error => error) };
  });
  await expect(h.transport.exchange('overflow', { signal: new AbortController().signal })).rejects.toEqual({ code: 'delivery_failed', delivery: 'not_sent' });
  expect(h.of('bridge_exchange')).toHaveLength(0); requests[0].controller.abort();
  expect(await requests[0].promise).toEqual({ code: 'disconnected', delivery: 'not_sent' }); expect(h.transport.state.pendingRequests).toBe(31);
  await h.ready(); expect(h.of('bridge_exchange')).toHaveLength(31);
  h.transport.dispose(); await Promise.all(requests.map(request => request.promise));
  expect(h.transport.state.pendingRequests).toBe(31);
});

test('8 local subscribers refuse a ninth before invoking or changing the shared observer', () => {
  const h = setup(), faults: unknown[] = []; const stops = Array.from({ length: 8 }, () => h.transport.subscribe(() => {}));
  h.transport.subscribe(() => {}, fault => faults.push(fault)); expect(h.transport.state.subscribers).toBe(8);
  expect(h.of('bridge_subscribe')).toHaveLength(1); expect(faults).toEqual([{ code: 'disconnected', delivery: 'not_sent' }]);
  stops.forEach(stop => stop());
});

test('known-key cleanup reserves only 8 entries and at most initial plus late ACK attempts', async () => {
  const h = setup(false), faults: unknown[] = [];
  for (let i = 1; i <= 8; i++) h.transport.subscribe(() => {})();
  expect(h.transport.state.knownRegistrations).toBe(8); expect(h.of('bridge_subscribe')).toHaveLength(8); expect(h.of('bridge_unsubscribe')).toHaveLength(8);
  h.transport.subscribe(() => {}, fault => faults.push(fault)); expect(h.of('bridge_subscribe')).toHaveLength(8);
  expect(faults).toHaveLength(1);
  for (const call of h.of('bridge_subscribe')) h.ack(call); await flush(); expect(h.of('bridge_unsubscribe')).toHaveLength(16);
  h.transport.dispose(); await flush(); expect(h.of('bridge_unsubscribe')).toHaveLength(16);
  for (const call of h.of('bridge_unsubscribe')) call.reject('lost cleanup reply'); await flush();
  expect(h.transport.state.knownRegistrations).toBe(0);
});

test('allocator is canonical monotonic and cannot be reused to bypass a singleton lifetime quota', () => {
  expect(() => new TauriRegistrationKeys('renderer invented epoch')).toThrow('invalid_observation_epoch');
  const h = setup(); h.transport.dispose();
  expect(() => new TauriTransport({ invoke: h.invoke, keys: h.keys })).toThrow('observation_adapter_already_bound');
});

test('request and reply scalar UTF-8 limits never truncate or normalize captured bytes', async () => {
  const h = setup(); h.transport.subscribe(() => {}); await h.ready();
  for (const frame of ['x'.repeat(262145), '€'.repeat(87382), '\ud800', '\udfff']) {
    await expect(h.transport.exchange(frame, { signal: new AbortController().signal })).rejects.toEqual({ code: 'delivery_failed', delivery: 'not_sent' });
  }
  expect(h.of('bridge_exchange')).toHaveLength(0);
  const exact = '€'.repeat(87381), result = h.transport.exchange(exact, { signal: new AbortController().signal }); await flush();
  expect(h.of('bridge_exchange')[0].arguments_.frame).toBe(exact);
  h.of('bridge_exchange')[0].resolve('€'.repeat(87382)); await expect(result).rejects.toEqual({ code: 'delivery_failed', delivery: 'may_have_reached_backend' });
});

for (const kind of ['version', 'key', 'extra', 'two_frames', 'too_large', 'surrogate', 'accessor'] as const) {
  test(`malformed bounded poll DTO retires observation: ${kind}`, async () => {
    const h = setup(), faults: unknown[] = []; let reads = 0;
    h.transport.subscribe(() => {}, fault => faults.push(fault)); await h.ready();
    const dto: any = { schemaVersion: 1, registrationKey: epoch + ':1', frames: [] };
    if (kind === 'version') dto.schemaVersion = 2;
    if (kind === 'key') dto.registrationKey = epoch + ':2';
    if (kind === 'extra') dto.rawNativeError = 'secret';
    if (kind === 'two_frames') dto.frames = ['a', 'b'];
    if (kind === 'too_large') dto.frames = ['x'.repeat(262145)];
    if (kind === 'surrogate') dto.frames = ['\ud800'];
    if (kind === 'accessor') Object.defineProperty(dto, 'frames', { enumerable: true, get() { reads++; return []; } });
    h.of('bridge_poll')[0].resolve(dto); await flush();
    expect(h.transport.state.phase).toBe('idle'); expect(faults).toEqual([{ code: 'delivery_failed', delivery: 'may_have_reached_backend' }]);
    expect(reads).toBe(0); expect(h.clock.tasks.size).toBe(0); expect(h.of('bridge_unsubscribe')).toHaveLength(1);
  });
}

test('invalid registration ACK cannot invoke initial snapshot and uses only known-key cleanup', async () => {
  const h = setup(), faults: unknown[] = []; h.transport.subscribe(() => {}, fault => faults.push(fault));
  const response = h.transport.exchange('snapshot', { signal: new AbortController().signal }); const observed = response.catch(error => error);
  h.of('bridge_subscribe')[0].resolve({ schemaVersion: 1, registrationKey: epoch + ':2' });
  expect(await observed).toEqual({ code: 'delivery_failed', delivery: 'not_sent' }); await flush();
  expect(h.of('bridge_exchange')).toHaveLength(0); expect(h.of('bridge_unsubscribe')[0].arguments_.registrationKey).toBe(epoch + ':1'); expect(faults).toHaveLength(1);
});

test('only an exact trusted native error DTO claims not_sent after invoke starts', async () => {
  for (const error of [{ schemaVersion: 1, code: 'disconnected', delivery: 'not_sent' }, { code: 'disconnected', delivery: 'not_sent' },
    { schemaVersion: 1, code: 'disconnected', delivery: 'not_sent', rawNativeError: 'secret canary' }]) {
    const h = setup(); h.transport.subscribe(() => {}); await h.ready(); const promise = h.transport.exchange('input', { signal: new AbortController().signal }); const observed = promise.catch(error => error); await flush();
    h.of('bridge_exchange')[0].reject(error); const value = await observed;
    expect(value.delivery).toBe(Object.keys(error).length === 3 ? 'not_sent' : 'may_have_reached_backend'); expect(JSON.stringify(value)).not.toContain('canary');
  }
});

test('event exception retires before another callback and every bounded listener gets one terminal fault', async () => {
  const h = setup(), faults: string[] = [], events: string[] = [];
  h.transport.subscribe(() => { throw new Error('private callback detail'); }, () => { faults.push('a'); throw new Error('fault callback'); });
  h.transport.subscribe(() => events.push('b'), () => faults.push('b')); await h.ready(); h.poll(['frame']); await flush();
  expect(events).toEqual([]); expect(faults).toEqual(['a', 'b']); expect(h.transport.state.phase).toBe('idle');
});

test('event disposal suppresses old observer snapshot and old reply cannot schedule another poll', async () => {
  const h = setup(), seen: string[] = []; h.transport.subscribe(() => { seen.push('a'); h.transport.dispose(); }); h.transport.subscribe(() => seen.push('b'));
  await h.ready(); h.poll(['frame']); await flush(); expect(seen).toEqual(['a']); expect(h.clock.tasks.size).toBe(0); expect(h.of('bridge_poll')).toHaveLength(1);
});

test('synchronous registration disposal before invoke returns cannot leak local observers or resurrect readiness', async () => {
  const calls: BridgeInvokeCommand[] = []; let transport!: TauriTransport;
  const invoke: BridgeInvoke = (command, arguments_) => {
    calls.push(command); if (command === 'bridge_subscribe') transport.dispose();
    return Promise.resolve({ schemaVersion: 1, registrationKey: arguments_.registrationKey });
  };
  transport = new TauriTransport({ invoke, keys: new TauriRegistrationKeys(epoch) });
  transport.subscribe(() => { throw new Error('old event'); }); await flush();
  expect(transport.state.phase).toBe('disposed'); expect(transport.state.subscribers).toBe(0);
  expect(calls).toEqual(['bridge_subscribe', 'bridge_unsubscribe', 'bridge_unsubscribe']);
  expect(transport.state.knownRegistrations).toBe(0);
});

test('an event callback can unsubscribe another local listener without delivering to its stale snapshot', async () => {
  const h = setup(), seen: string[] = []; let stopB = () => {};
  h.transport.subscribe(() => { seen.push('a'); stopB(); }); stopB = h.transport.subscribe(() => seen.push('b'));
  await h.ready(); h.poll(['frame']); await flush();
  expect(seen).toEqual(['a']); expect(h.transport.state.subscribers).toBe(1); expect(h.transport.state.phase).toBe('ready');
});

test('malformed raw reply reaches the same common framing validator without transport mutation', async () => {
  const h = setup(); h.transport.subscribe(() => {}); await h.ready();
  const client = new BridgeClient(h.transport, { clock: h.clock, requestId: () => epoch }); const outcome = client.query('hello', {}); await flush();
  h.of('bridge_exchange')[0].resolve('{"protocolVersion":1,"protocolVersion":1}');
  expect(await outcome).toMatchObject({ kind: 'fault', fault: { code: 'framing', delivery: 'may_have_reached_backend' } });
  expect(() => decodeReply('{"protocolVersion":1,"protocolVersion":1}')).toThrow('framing'); client.dispose();
});

test('idle and disposed adapters never invoke or activate a synthetic backend', async () => {
  const h = setup(); await expect(h.transport.exchange('request', { signal: new AbortController().signal })).rejects.toEqual({ code: 'unavailable_binding', delivery: 'not_sent' });
  h.transport.dispose(); const faults: unknown[] = []; h.transport.subscribe(() => {}, fault => faults.push(fault));
  expect(h.calls).toEqual([]); expect(faults).toEqual([{ code: 'unavailable_binding', delivery: 'not_sent' }]);
});
