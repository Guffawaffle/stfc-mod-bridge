import type { Cursor, Event, Reply, Request } from '../generated/protocol';
import type { RawFrame, RawTransport, TransportFault } from '../client/transport';
import { canonicalData, captureData, captureRequest, decodeEvent, decodeReply, decodeRequest } from '../client/wire';
import { ManualClock } from './clock';

/** These are development delivery controls over generated DTOs, not wire types. */
export type MockStep =
  | { readonly type: 'boundary'; readonly reason: 'initial' | 'restart' | 'reconnect' | 'resnapshot' | 'retention_gap'; readonly cursor: Cursor }
  | { readonly type: 'exchange'; readonly request: Request; readonly reply: Reply; readonly delivery?: 'received' | 'lost'; readonly delayMs?: number }
  | { readonly type: 'event'; readonly event: Event; readonly delayMs?: number };

export interface MockScript {
  readonly id: string;
  readonly scenario: string;
  readonly case: string;
  readonly correlationSlot: 'requestId';
  readonly sources: readonly { readonly id: string; readonly sha256: string }[];
  readonly steps: readonly MockStep[];
}

export interface MockState {
  readonly scenarioId: string;
  readonly position: number;
  readonly connected: boolean;
  readonly disposed: boolean;
  readonly backendBusy: boolean;
  readonly pendingExchanges: number;
  readonly scheduledTasks: number;
  readonly replayCount: number;
  readonly failureMode: boolean;
  readonly boundary?: Extract<MockStep, { type: 'boundary' }>;
  readonly lastFault?: 'unexpected_request' | 'invalid_request' | 'disconnected' | 'reset' | 'disposed';
}

interface Pending {
  resolve: (frame: RawFrame) => void;
  reject: (fault: TransportFault) => void;
  release: () => void;
}

type CapturedScript = ReturnType<typeof captureData<MockScript>>;
type CapturedRequest = ReturnType<typeof captureRequest>['request'];
type Boundary = Extract<CapturedScript['steps'][number], { type: 'boundary' }>;

/** A scripted observation oracle. It never computes native policy or invents results. */
export class ScenarioTransport implements RawTransport {
  readonly clock: ManualClock;
  readonly #maxPending: number;
  readonly #maxSteps: number;
  readonly #maxFrameBytes: number;
  #script: CapturedScript;
  #position = 0;
  #connected = true;
  #disposed = false;
  #backendBusy = false;
  #latencyMs: number;
  #eventDelayMs: number;
  #generation = 0;
  #connectionRevision = 0;
  #notificationRevision = 0;
  #nextPending = 0;
  #pending = new Map<number, Pending>();
  #tasks = new Set<() => void>();
  #subscribers = new Set<{ event: (frame: RawFrame) => void; fault?: (fault: TransportFault) => void }>();
  #stateSubscribers = new Set<(state: MockState) => void>();
  #replays = new Map<string, string>();
  #boundary?: Boundary;
  #lastFault?: MockState['lastFault'];
  #failureMode = false;
  #nextReply?: RawFrame;
  #dropNext = false;

  constructor(script: MockScript, options: {
    clock?: ManualClock; latencyMs?: number; eventDelayMs?: number;
    maxPending?: number; maxSteps?: number; maxFrameBytes?: number;
  } = {}) {
    this.clock = options.clock ?? new ManualClock();
    this.#maxPending = limit(options.maxPending ?? 64, 1024);
    this.#maxSteps = limit(options.maxSteps ?? 1024, 4096);
    this.#maxFrameBytes = limit(options.maxFrameBytes ?? 262144, 262144);
    this.#latencyMs = delay(options.latencyMs ?? 100);
    this.#eventDelayMs = delay(options.eventDelayMs ?? 50);
    this.#script = this.#captureScript(script);
    this.#pump();
  }

  get state(): MockState {
    return Object.freeze({
      scenarioId: this.#script.id, position: this.#position, connected: this.#connected,
      disposed: this.#disposed, backendBusy: this.#backendBusy,
      pendingExchanges: this.#pending.size, scheduledTasks: this.#tasks.size,
      replayCount: this.#replays.size, failureMode: this.#failureMode,
      ...(this.#boundary ? { boundary: this.#boundary } : {}),
      ...(this.#lastFault ? { lastFault: this.#lastFault } : {}),
    });
  }

  get expectedRequest(): CapturedRequest | undefined {
    const step = this.#script.steps[this.#position];
    return !this.#disposed && this.#connected && !this.#backendBusy && step?.type === 'exchange'
      ? step.request : undefined;
  }

  setLatency(milliseconds: number): void { this.#latencyMs = delay(milliseconds); }
  setEventDelay(milliseconds: number): void { this.#eventDelayMs = delay(milliseconds); }

  subscribeState(listener: (state: MockState) => void): () => void {
    this.#assertActive();
    if (this.#stateSubscribers.size >= this.#maxPending) throw new Error('mock_subscriber_limit');
    this.#stateSubscribers.add(listener);
    listener(this.state);
    return () => { this.#stateSubscribers.delete(listener); };
  }

  exchange(request: string, options: { signal: AbortSignal }): Promise<RawFrame> {
    if (options.signal.aborted) return Promise.reject(fault('delivery_failed', 'not_sent'));
    if (this.#disposed || !this.#connected) return Promise.reject(fault('disconnected', 'not_sent'));
    if (this.#pending.size >= this.#maxPending) return Promise.reject(fault('delivery_failed', 'not_sent'));
    let incoming: CapturedRequest;
    try { incoming = decodeRequest(request); }
    catch { return this.#rejectRequest('invalid_request'); }
    const step = this.#script.steps[this.#position];
    if (this.#backendBusy || step?.type !== 'exchange' || requestKey(incoming) !== requestKey(step.request)) {
      return this.#rejectRequest('unexpected_request');
    }

    // Only the declared requestId slot changes. Every other command byte-value is retained.
    const reply = JSON.stringify({ ...step.reply, requestId: incoming.requestId });
    const validatedReply = decodeReply(reply);
    const id = ++this.#nextPending;
    const drop = this.#dropNext || step.delivery === 'lost';
    const injectedReply = this.#nextReply;
    this.#dropNext = false;
    this.#nextReply = undefined;
    this.#backendBusy = true;
    this.#position += 1;
    const promise = new Promise<RawFrame>((resolve, reject) => {
      const abort = () => {
        const pending = this.#pending.get(id);
        if (!pending) return;
        pending.release();
        reject(fault('delivery_failed', 'may_have_reached_backend'));
        this.#notify();
      };
      const release = () => {
        options.signal.removeEventListener('abort', abort);
        this.#pending.delete(id);
      };
      this.#pending.set(id, { resolve, reject, release });
      options.signal.addEventListener('abort', abort, { once: true });
    });
    try {
      this.#schedule(step.delayMs ?? this.#latencyMs, generation => {
        // A record is evidence of this scripted reply, never an unscripted replay handler.
        if (incoming.body.type === 'command' && incoming.body.command.name === 'commit'
          && validatedReply.body.type === 'result' && validatedReply.body.result.type === 'command'
          && validatedReply.body.result.command.name === 'commit' && !this.#replays.has(requestKey(incoming))) {
          this.#replays.set(requestKey(incoming), reply);
        }
        const pending = this.#pending.get(id);
        if (pending && !drop && this.#connected) {
          pending.release();
          pending.resolve(injectedReply === undefined ? reply : cloneFrame(injectedReply));
        }
        this.#backendBusy = false;
        this.#pump();
        if (this.#isCurrent(generation)) this.#notify();
      });
    } catch {
      const pending = this.#pending.get(id);
      pending?.release();
      pending?.reject(fault('delivery_failed', 'not_sent'));
      this.#backendBusy = false;
      this.#position -= 1;
      this.#lastFault = 'unexpected_request';
    }
    this.#notify();
    return promise;
  }

  subscribe(onEvent: (frame: RawFrame) => void, onFault?: (fault: TransportFault) => void): () => void {
    this.#assertActive();
    if (this.#subscribers.size >= this.#maxPending) throw new Error('mock_subscriber_limit');
    const subscriber = { event: onEvent, fault: onFault };
    this.#subscribers.add(subscriber);
    return () => { this.#subscribers.delete(subscriber); };
  }

  /** Explicit failure variant: pass the raw frame to the same client's decoder. */
  injectNextReply(frame: RawFrame): void {
    this.#assertActive();
    this.#nextReply = this.#captureFaultFrame(frame, 'reply');
    this.#failureMode = true;
    this.#notify();
  }

  injectEvent(frame: RawFrame, delayMs = 0): void {
    this.#assertActive();
    const captured = this.#captureFaultFrame(frame, 'event');
    this.#failureMode = true;
    this.#schedule(delay(delayMs), generation => {
      this.#emit(captured);
      if (this.#isCurrent(generation)) this.#notify();
    });
    this.#notify();
  }

  dropNextReply(): void {
    this.#assertActive();
    this.#dropNext = true;
    this.#failureMode = true;
    this.#notify();
  }

  disconnect(): void {
    this.#assertActive();
    const generation = this.#generation;
    const connectionRevision = ++this.#connectionRevision;
    this.#connected = false;
    this.#lastFault = 'disconnected';
    this.#rejectPending(fault('disconnected', 'may_have_reached_backend'));
    for (const subscriber of [...this.#subscribers]) {
      if (!this.#isCurrent(generation) || connectionRevision !== this.#connectionRevision) return;
      if (!this.#subscribers.has(subscriber)) continue;
      try { subscriber.fault?.(fault('disconnected', 'may_have_reached_backend')); } catch { /* Observer cannot stop scripted backend work. */ }
    }
    if (this.#isCurrent(generation) && connectionRevision === this.#connectionRevision) this.#notify();
  }

  reconnect(): void {
    this.#assertActive();
    const generation = this.#generation;
    const connectionRevision = ++this.#connectionRevision;
    this.#connected = true;
    this.#lastFault = undefined;
    this.#pump();
    if (this.#isCurrent(generation) && connectionRevision === this.#connectionRevision) this.#notify();
  }

  /** Replace the synthetic context; old wire observers and deliveries are disposed. */
  reset(script?: MockScript): void {
    this.#assertActive();
    const captured = script ? this.#captureScript(script) : this.#script;
    const generation = ++this.#generation;
    this.#connectionRevision += 1;
    for (const cancel of this.#tasks) cancel();
    this.#tasks.clear();
    this.#rejectPending(fault('disconnected', 'may_have_reached_backend'));
    this.#subscribers.clear();
    this.#script = captured;
    this.#position = 0;
    this.#connected = true;
    this.#backendBusy = false;
    this.#replays.clear();
    this.#boundary = undefined;
    this.#nextReply = undefined;
    this.#dropNext = false;
    this.#failureMode = false;
    this.#lastFault = 'reset';
    this.#pump();
    if (this.#isCurrent(generation)) this.#notify();
  }

  dispose(): void {
    if (this.#disposed) return;
    this.#generation += 1;
    this.#connectionRevision += 1;
    for (const cancel of this.#tasks) cancel();
    this.#tasks.clear();
    this.#rejectPending(fault('disconnected', 'may_have_reached_backend'));
    this.#subscribers.clear();
    this.#connected = false;
    this.#disposed = true;
    this.#backendBusy = false;
    this.#lastFault = 'disposed';
    this.#notify();
    this.#stateSubscribers.clear();
  }

  #captureScript(script: MockScript): CapturedScript {
    const captured = captureData(script);
    if (!captured.id || captured.correlationSlot !== 'requestId' || !/^SC-(0[1-9]|1[0-8])$/.test(captured.scenario) || captured.steps.length > this.#maxSteps) {
      throw new Error('mock_invalid_script');
    }
    if (captured.sources.length > this.#maxSteps * 2 + 1 || captured.sources.some(source => !source.id || !/^[0-9a-f]{64}$/.test(source.sha256))) {
      throw new Error('mock_invalid_provenance');
    }
    for (const step of captured.steps) {
      if (step.type === 'exchange') {
        captureRequest(step.request);
        decodeReply(JSON.stringify(step.reply));
        if (step.request.requestId !== step.reply.requestId) throw new Error('mock_script_correlation');
        if (step.delayMs !== undefined) delay(step.delayMs);
      } else if (step.type === 'event') {
        decodeEvent(JSON.stringify(step.event));
        if (step.delayMs !== undefined) delay(step.delayMs);
      }
    }
    return captured;
  }

  #pump(): void {
    if (this.#disposed || this.#backendBusy) return;
    const step = this.#script.steps[this.#position];
    if (step?.type === 'boundary') {
      this.#position += 1;
      this.#boundary = step;
      if (step.reason === 'restart' || step.reason === 'reconnect') {
        this.disconnect();
        return;
      }
      this.#pump();
    } else if (step?.type === 'event') {
      this.#backendBusy = true;
      this.#schedule(step.delayMs ?? this.#eventDelayMs, generation => {
        const frame = JSON.stringify(step.event);
        decodeEvent(frame);
        this.#emit(frame);
        if (!this.#isCurrent(generation)) return;
        this.#position += 1;
        this.#backendBusy = false;
        this.#pump();
        if (this.#isCurrent(generation)) this.#notify();
      });
    }
  }

  #schedule(milliseconds: number, callback: (generation: number) => void): void {
    const generation = this.#generation;
    let cancel = () => {};
    cancel = this.clock.schedule(delay(milliseconds), () => {
      this.#tasks.delete(cancel);
      if (this.#isCurrent(generation)) callback(generation);
    });
    this.#tasks.add(cancel);
  }

  #captureFaultFrame(frame: RawFrame, kind: 'reply' | 'event'): RawFrame {
    const captured = cloneFrame(frame);
    const byteLength = typeof captured === 'string' ? new TextEncoder().encode(captured).byteLength : captured.byteLength;
    if (byteLength > this.#maxFrameBytes) throw new Error('mock_frame_limit');
    // Invalid data is deliberately retained only through this explicit failure API.
    try { kind === 'reply' ? decodeReply(captured) : decodeEvent(captured); } catch { /* Expected failure probe. */ }
    return captured;
  }

  #emit(frame: RawFrame): void {
    const generation = this.#generation;
    const connectionRevision = this.#connectionRevision;
    for (const subscriber of [...this.#subscribers]) {
      if (!this.#isCurrent(generation) || !this.#connected || connectionRevision !== this.#connectionRevision) return;
      if (!this.#subscribers.has(subscriber)) continue;
      try { subscriber.event(cloneFrame(frame)); }
      catch {
        if (this.#isCurrent(generation) && this.#connected && connectionRevision === this.#connectionRevision && this.#subscribers.has(subscriber)) {
          try { subscriber.fault?.(fault('delivery_failed', 'may_have_reached_backend')); } catch { /* Observer is detached from scripted backend work. */ }
        }
      }
    }
  }

  #rejectRequest(reason: 'invalid_request' | 'unexpected_request'): Promise<RawFrame> {
    this.#lastFault = reason;
    this.#notify();
    return Promise.reject(fault('delivery_failed', 'not_sent'));
  }

  #rejectPending(reason: TransportFault): void {
    for (const pending of [...this.#pending.values()]) { pending.release(); pending.reject(reason); }
  }

  #notify(): void {
    const generation = this.#generation;
    const notificationRevision = ++this.#notificationRevision;
    const state = this.state;
    for (const listener of [...this.#stateSubscribers]) {
      if (generation !== this.#generation || notificationRevision !== this.#notificationRevision) return;
      if (!this.#stateSubscribers.has(listener)) continue;
      try { listener(state); } catch { /* Development observer cannot stop the script. */ }
    }
  }
  #isCurrent(generation: number): boolean { return generation === this.#generation && !this.#disposed; }
  #assertActive(): void { if (this.#disposed) throw new Error('mock_transport_disposed'); }
}

function requestKey(request: CapturedRequest): string {
  return canonicalData({ body: request.body, protocolVersion: request.protocolVersion });
}

function cloneFrame(frame: RawFrame): RawFrame { return typeof frame === 'string' ? frame : frame.slice(); }
function fault(code: TransportFault['code'], delivery: TransportFault['delivery']): TransportFault { return Object.freeze({ code, delivery }); }
function delay(value: number): number { if (!Number.isSafeInteger(value) || value < 0) throw new Error('mock_invalid_delay'); return value; }
function limit(value: number, upper: number): number { if (!Number.isSafeInteger(value) || value < 1 || value > upper) throw new Error('mock_invalid_limit'); return value; }
