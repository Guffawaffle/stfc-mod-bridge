import type { RawFrame, RawTransport, TransportFault } from '../transport';

export type BridgeInvokeCommand = 'bridge_exchange' | 'bridge_subscribe' | 'bridge_poll' | 'bridge_unsubscribe';
export type BridgeInvoke = (command: BridgeInvokeCommand, arguments_: Readonly<Record<string, string>>) => Promise<unknown>;
export interface TauriClock { schedule(delayMs: number, callback: () => void): () => void; }
const systemClock: TauriClock = { schedule(delay, callback) { const id = setTimeout(callback, delay); return () => clearTimeout(id); } };
const MAX_FRAME_BYTES = 262_144;
const MAX_REQUESTS = 32;
const MAX_REQUEST_BYTES = 8 * 1024 * 1024;
const MAX_SUBSCRIBERS = 8;
const MAX_REGISTRATIONS = 8;
const MAX_ORDINAL = 18_446_744_073_709_551_615n;
const UUID = /^[0-9a-f]{8}-[0-9a-f]{4}-[1-8][0-9a-f]{3}-[89ab][0-9a-f]{3}-[0-9a-f]{12}$/;
const fault = (code: TransportFault['code'], delivery: TransportFault['delivery']): TransportFault => Object.freeze({ code, delivery });

/** Root creates and retains one allocator for the trusted document epoch. */
export class TauriRegistrationKeys {
  readonly #epoch: string;
  #ordinal = 0n;
  #claimed = false;
  constructor(epoch: string) { if (!UUID.test(epoch)) throw new TypeError('invalid_observation_epoch'); this.#epoch = epoch; }
  get epoch(): string { return this.#epoch; }
  claim(): void {
    if (this.#claimed) throw new TypeError('observation_adapter_already_bound');
    this.#claimed = true;
  }
  allocate(): string {
    if (this.#ordinal === MAX_ORDINAL) throw new RangeError('registration_exhausted');
    return this.epoch + ':' + (++this.#ordinal).toString();
  }
}

/** Counts scalar UTF-8 bytes without allocating an unbounded encoded copy. */
function frameBytes(value: unknown): number {
  if (typeof value !== 'string' || !value.length || value.length > MAX_FRAME_BYTES) throw fault('delivery_failed', 'not_sent');
  let bytes = 0;
  for (let i = 0; i < value.length; i++) {
    const code = value.charCodeAt(i);
    if (code >= 0xd800 && code <= 0xdbff) {
      const next = value.charCodeAt(++i);
      if (!(next >= 0xdc00 && next <= 0xdfff)) throw fault('delivery_failed', 'not_sent');
      bytes += 4;
    } else {
      if (code >= 0xdc00 && code <= 0xdfff) throw fault('delivery_failed', 'not_sent');
      bytes += code < 0x80 ? 1 : code < 0x800 ? 2 : 3;
    }
    if (bytes > MAX_FRAME_BYTES) throw fault('delivery_failed', 'not_sent');
  }
  return bytes;
}

/** Control DTOs are plain closed data; never read an accessor or conversion hook. */
function record(value: unknown, keys: readonly string[]): Record<string, unknown> {
  if (!value || typeof value !== 'object' || Array.isArray(value)) throw new Error('invalid_control_dto');
  const prototype = Object.getPrototypeOf(value);
  if (prototype !== Object.prototype && prototype !== null) throw new Error('invalid_control_dto');
  const own = Reflect.ownKeys(value);
  if (own.length !== keys.length || own.some(key => typeof key !== 'string' || !keys.includes(key))) throw new Error('invalid_control_dto');
  const result: Record<string, unknown> = Object.create(null);
  for (const key of keys) {
    const descriptor = Object.getOwnPropertyDescriptor(value, key);
    if (!descriptor || !('value' in descriptor) || !descriptor.enumerable) throw new Error('invalid_control_dto');
    result[key] = descriptor.value;
  }
  return result;
}
function acknowledgement(value: unknown, key: string): void {
  const dto = record(value, ['schemaVersion', 'registrationKey']);
  if (dto.schemaVersion !== 1 || dto.registrationKey !== key) throw new Error('invalid_control_dto');
}
function pollFrame(value: unknown, key: string): string | undefined {
  const dto = record(value, ['schemaVersion', 'registrationKey', 'frames']);
  if (dto.schemaVersion !== 1 || dto.registrationKey !== key || !Array.isArray(dto.frames)
    || Object.getPrototypeOf(dto.frames) !== Array.prototype) throw new Error('invalid_control_dto');
  const length = Object.getOwnPropertyDescriptor(dto.frames, 'length')?.value;
  if (length !== 0 && length !== 1 || Reflect.ownKeys(dto.frames).length !== length + 1) throw new Error('invalid_control_dto');
  if (!length) return undefined;
  const frame = Object.getOwnPropertyDescriptor(dto.frames, '0');
  if (!frame || !('value' in frame) || !frame.enumerable) throw new Error('invalid_control_dto');
  frameBytes(frame.value);
  // One <=256KiB scalar frame serializes below 2MiB even with six-byte JSON
  // escaping for every byte. Exact tiny ASCII metadata cannot change that bound.
  return frame.value as string;
}
function invokeFault(value: unknown): TransportFault {
  try {
    const dto = record(value, ['schemaVersion', 'code', 'delivery']);
    if (dto.schemaVersion === 1 && ['unavailable_binding', 'disconnected', 'delivery_failed'].includes(dto.code as string)
      && ['not_sent', 'may_have_reached_backend'].includes(dto.delivery as string)) {
      // Exact enums/version/keys bound this trusted native error DTO below 128
      // UTF-8 bytes. Generic rejection never asserts a pre-enqueue disposition.
      return fault(dto.code as TransportFault['code'], dto.delivery as TransportFault['delivery']);
    }
  } catch { /* Never surface native details or execute their hooks. */ }
  return fault('delivery_failed', 'may_have_reached_backend');
}
function deferred<T>() {
  let resolve!: (value: T) => void, reject!: (reason: TransportFault) => void;
  const promise = new Promise<T>((yes, no) => { resolve = yes; reject = no; });
  // A subscription can be retired before any exchange awaits readiness.
  void promise.catch(() => {});
  return { promise, resolve, reject };
}
type Subscriber = { active: boolean; onEvent: (frame: RawFrame) => void; onFault?: (fault: TransportFault) => void };
type Registration = { key: string; retired: boolean; controls: number; cleanupAttempts: number };
type Generation = { registration: Registration; active: boolean; ready: boolean; listeners: Set<Subscriber>; barrier: ReturnType<typeof deferred<void>> };
type Pending = { generation: Generation; bytes: number; frame?: string; sent: boolean; settled: boolean; released: boolean;
  signal: AbortSignal; abort: () => void; resolve: (frame: RawFrame) => void; reject: (fault: TransportFault) => void };
type Timer = { generation: Generation; cancelled: boolean; cancel?: () => void };

/** Observation adapter only. Invoke, trusted epoch and native authorization are root-owned. */
export class TauriTransport implements RawTransport {
  readonly #invoke: BridgeInvoke;
  readonly #keys: TauriRegistrationKeys;
  readonly #clock: TauriClock;
  #disposed = false;
  #generation?: Generation;
  #poll?: { generation: Generation };
  #timer?: Timer;
  #registrations = new Set<Registration>();
  #pending = new Set<Pending>();
  #pendingBytes = 0;
  constructor(options: { invoke: BridgeInvoke; keys: TauriRegistrationKeys; clock?: TauriClock }) {
    options.keys.claim();
    this.#invoke = options.invoke; this.#keys = options.keys; this.#clock = options.clock ?? systemClock;
  }
  get state() {
    return Object.freeze({ phase: this.#disposed ? 'disposed' : this.#generation?.ready ? 'ready' : this.#generation ? 'starting' : 'idle',
      subscribers: this.#generation?.listeners.size ?? 0, pendingRequests: this.#pending.size,
      pendingRequestBytes: this.#pendingBytes, knownRegistrations: this.#registrations.size,
      pollInFlight: !!this.#poll, timerScheduled: !!this.#timer });
  }
  subscribe(onEvent: (frame: RawFrame) => void, onFault?: (fault: TransportFault) => void): () => void {
    if (this.#disposed || !this.#generation && (this.#poll || this.#registrations.size >= MAX_REGISTRATIONS)
      || this.#generation && this.#generation.listeners.size >= MAX_SUBSCRIBERS) {
      this.#notifyFault(onFault, fault(this.#disposed ? 'unavailable_binding' : 'disconnected', 'not_sent'));
      return () => {};
    }
    let generation = this.#generation;
    if (!generation) {
      let key: string;
      try { key = this.#keys.allocate(); } catch { this.#notifyFault(onFault, fault('unavailable_binding', 'not_sent')); return () => {}; }
      const registration: Registration = { key, retired: false, controls: 0, cleanupAttempts: 0 };
      this.#registrations.add(registration);
      generation = { registration, active: true, ready: false, listeners: new Set(), barrier: deferred<void>() };
      this.#generation = generation;
    }
    const subscriber: Subscriber = { active: true, onEvent, onFault };
    generation.listeners.add(subscriber); // Reserve before invoking any injected callback.
    const captured = generation;
    if (captured.listeners.size === 1 && !captured.ready && captured.registration.controls === 0) this.#start(captured);
    return () => {
      if (!subscriber.active) return;
      subscriber.active = false; captured.listeners.delete(subscriber);
      if (captured.active && !captured.listeners.size) this.#retire(captured);
    };
  }
  exchange(request: string, options: { signal: AbortSignal }): Promise<RawFrame> {
    const generation = this.#generation;
    if (this.#disposed || !generation?.active) return Promise.reject(fault('unavailable_binding', 'not_sent'));
    if (options.signal.aborted) return Promise.reject(fault('disconnected', 'not_sent'));
    let bytes: number;
    try { bytes = frameBytes(request); } catch { return Promise.reject(fault('delivery_failed', 'not_sent')); }
    if (this.#pending.size >= MAX_REQUESTS || this.#pendingBytes + bytes > MAX_REQUEST_BYTES) return Promise.reject(fault('delivery_failed', 'not_sent'));
    return new Promise((resolve, reject) => {
      const pending: Pending = { generation, bytes, frame: request, sent: false, settled: false, released: false,
        signal: options.signal, abort: () => {}, resolve, reject };
      pending.abort = () => this.#finish(pending, undefined, fault('disconnected', pending.sent ? 'may_have_reached_backend' : 'not_sent'));
      this.#pending.add(pending); this.#pendingBytes += bytes;
      options.signal.addEventListener('abort', pending.abort, { once: true });
      if (options.signal.aborted) { pending.abort(); return; }
      void generation.barrier.promise.then(() => {
        if (pending.settled) return;
        if (this.#disposed || this.#generation !== generation || !generation.active || options.signal.aborted) { pending.abort(); return; }
        const frame = pending.frame!;
        pending.frame = undefined; pending.sent = true; // Custody begins before invoke, including a synchronous throw.
        void this.#call('bridge_exchange', { frame }).then(reply => {
          if (pending.settled) return;
          if (!generation.active || this.#generation !== generation) { pending.abort(); return; }
          try { frameBytes(reply); this.#finish(pending, reply as string); }
          catch { this.#finish(pending, undefined, fault('delivery_failed', 'may_have_reached_backend')); }
        }, error => this.#finish(pending, undefined, invokeFault(error))).finally(() => this.#release(pending));
      }, (reason: TransportFault) => this.#finish(pending, undefined, fault(reason.code, 'not_sent')));
    });
  }
  dispose(): void {
    if (this.#disposed) return;
    this.#disposed = true;
    if (this.#generation) this.#retire(this.#generation);
  }
  #call(command: BridgeInvokeCommand, arguments_: Readonly<Record<string, string>>): Promise<unknown> {
    try { return Promise.resolve(this.#invoke(command, Object.freeze(arguments_))); }
    catch (error) { return Promise.reject(error); }
  }
  #start(generation: Generation): void {
    const registration = generation.registration;
    registration.controls++;
    void this.#call('bridge_subscribe', { registrationKey: registration.key }).then(value => {
      acknowledgement(value, registration.key);
      if (!generation.active || this.#generation !== generation || this.#disposed) { this.#cleanup(registration); return; }
      generation.ready = true; generation.barrier.resolve(); this.#pollOnce(generation);
    }).catch(error => {
      if (generation.active) this.#retire(generation, invokeFault(error));
    }).finally(() => { registration.controls--; this.#forget(registration); });
  }
  #pollOnce(generation: Generation): void {
    if (this.#poll || !generation.active || this.#generation !== generation || !generation.ready) return;
    const token = { generation };
    this.#poll = token;
    const registration = generation.registration;
    registration.controls++;
    let nextDelay: number | undefined;
    void this.#call('bridge_poll', { registrationKey: registration.key }).then(value => {
      if (!generation.active || this.#generation !== generation) return;
      const frame = pollFrame(value, registration.key);
      if (frame !== undefined) {
        for (const subscriber of [...generation.listeners]) {
          if (!generation.active || this.#generation !== generation) break;
          if (!subscriber.active || !generation.listeners.has(subscriber)) continue;
          try { subscriber.onEvent(frame); }
          catch { this.#retire(generation, fault('delivery_failed', 'may_have_reached_backend')); break; }
        }
      }
      if (generation.active && this.#generation === generation) nextDelay = frame === undefined ? 100 : 10;
    }).catch(error => {
      if (generation.active) this.#retire(generation, invokeFault(error));
    }).finally(() => {
      if (this.#poll === token) this.#poll = undefined;
      registration.controls--; this.#forget(registration);
      if (nextDelay !== undefined && generation.active && this.#generation === generation) this.#schedule(generation, nextDelay);
    });
  }
  #schedule(generation: Generation, delay: number): void {
    const timer: Timer = { generation, cancelled: false };
    this.#timer = timer;
    try {
      timer.cancel = this.#clock.schedule(delay, () => {
        if (this.#timer !== timer || timer.cancelled) return;
        this.#timer = undefined;
        if (generation.active && this.#generation === generation) this.#pollOnce(generation);
      });
      if (timer.cancelled || this.#timer !== timer) timer.cancel();
    } catch { this.#retire(generation, fault('delivery_failed', 'may_have_reached_backend')); }
  }
  #retire(generation: Generation, reason?: TransportFault): void {
    if (!generation.active) return;
    generation.active = false;
    if (this.#generation === generation) this.#generation = undefined;
    if (this.#timer?.generation === generation) {
      const timer = this.#timer; this.#timer = undefined; timer.cancelled = true;
      try { timer.cancel?.(); } catch { /* Disposal remains synchronous. */ }
    }
    generation.barrier.reject(reason ?? fault('disconnected', 'not_sent'));
    const subscribers = [...generation.listeners]; generation.listeners.clear();
    for (const subscriber of subscribers) subscriber.active = false;
    for (const pending of [...this.#pending]) if (pending.generation === generation) {
      this.#finish(pending, undefined, fault(reason?.code ?? 'disconnected', pending.sent ? 'may_have_reached_backend' : 'not_sent'));
    }
    generation.registration.retired = true; this.#cleanup(generation.registration);
    if (reason) for (const subscriber of subscribers) this.#notifyFault(subscriber.onFault, reason);
  }
  #notifyFault(callback: Subscriber['onFault'], reason: TransportFault): void {
    try { callback?.(reason); } catch { /* One listener cannot strand another terminal fault. */ }
  }
  #cleanup(registration: Registration): void {
    if (registration.cleanupAttempts >= 2) return;
    registration.cleanupAttempts++; registration.controls++;
    void this.#call('bridge_unsubscribe', { registrationKey: registration.key }).then(value => {
      acknowledgement(value, registration.key);
    }).catch(() => { /* Native expiry/destruction owns reclamation after uncertain cleanup. */ })
      .finally(() => { registration.controls--; this.#forget(registration); });
  }
  #forget(registration: Registration): void {
    if (registration.retired && !registration.controls) this.#registrations.delete(registration);
  }
  #finish(pending: Pending, frame?: RawFrame, reason?: TransportFault): void {
    if (pending.settled) return;
    pending.settled = true; pending.frame = undefined; pending.signal.removeEventListener('abort', pending.abort);
    if (reason) pending.reject(reason); else pending.resolve(frame!);
    if (!pending.sent) this.#release(pending);
  }
  #release(pending: Pending): void {
    if (pending.released) return;
    pending.released = true; this.#pending.delete(pending); this.#pendingBytes -= pending.bytes;
  }
}
