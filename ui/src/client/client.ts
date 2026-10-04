import type { BridgeError, Command, CommandResult, CommitInput, Event, Query, QueryResult, Request } from '../generated/protocol';
import { captureRequest, canonicalData, ClientBoundaryError, decodeEvent, decodeReply, type DeepReadonly, type RequestCapture } from './wire';
import type { RawTransport, TransportFault } from './transport';
import { bindingEquivalent, cancellationMatches, diagnosticPreviewDigest, diagnosticPreviewMatches, preparationMatches, semanticPlanDigest } from './relations';
import { draftAcknowledgementMatches } from './draft-acknowledgement';

export type QueryName = Query['name'];
export type QueryInput<N extends QueryName> = Extract<Query, { name: N }>['input'];
export type QueryOutput<N extends QueryName> = Extract<QueryResult, { name: N }>['output'];
export type CommandName = Command['name'];
export type CommandInput<N extends CommandName> = Extract<Command, { name: N }>['input'];
export type CommandOutput<N extends CommandName> = Extract<CommandResult, { name: N }>['output'];
type AssertNever<T extends never> = T;
export type ProtocolPairing = [AssertNever<Exclude<QueryName, QueryResult['name']>>, AssertNever<Exclude<QueryResult['name'], QueryName>>,
  AssertNever<Exclude<CommandName, CommandResult['name']>>, AssertNever<Exclude<CommandResult['name'], CommandName>>];

export interface RequestMetadata {
  readonly requestId: string;
  readonly kind: Request['body']['type'];
  readonly method: QueryName | CommandName;
}
export type ClientFaultCode = 'framing' | 'schema' | 'incompatible_envelope' | 'invalid_capture' | 'correlation'
  | 'unavailable_binding' | 'disconnected' | 'delivery_failed' | 'timeout' | 'observational_abort'
  | 'disposed' | 'pending_limit' | 'subscription_limit' | 'request_id_reused' | 'replay_limit' | 'replay_conflict' | 'replay_missing' | 'stale_stream';
export interface ClientFault {
  readonly code: ClientFaultCode;
  readonly delivery: 'not_sent' | 'may_have_reached_backend';
  readonly request?: RequestMetadata;
}
export type ClientOutcome<T> =
  | { readonly kind: 'result'; readonly value: DeepReadonly<T>; readonly request: RequestMetadata }
  | { readonly kind: 'rejected'; readonly error: DeepReadonly<BridgeError>; readonly request: RequestMetadata }
  | { readonly kind: 'fault'; readonly fault: ClientFault };
export interface ClientClock { schedule(delayMs: number, callback: () => void): () => void; }
export const systemClock: ClientClock = {
  schedule(delayMs, callback) { const handle = setTimeout(callback, delayMs); return () => clearTimeout(handle); },
};
export interface CallOptions { readonly signal?: AbortSignal; readonly timeoutMs?: number; }
export interface ClientOptions {
  readonly requestId: () => string;
  readonly clock?: ClientClock;
  readonly timeoutMs?: number;
  readonly maximumPending?: number;
  readonly maximumReplays?: number;
  readonly maximumRecentIds?: number;
  readonly maximumSubscriptions?: number;
}
export interface CommitReplay { readonly input: DeepReadonly<CommitInput>; readonly request: RequestCapture; readonly operationId?: string; }
type Pending = { abandon: (code: ClientFaultCode) => void };
const bounded = (value: number, upper: number): number => {
  if (!Number.isSafeInteger(value) || value < 1 || value > upper) throw new RangeError('client_limit');
  return value;
};
function transportFault(value: unknown): TransportFault {
  // Read descriptors so a thrown object cannot execute conversion/accessor code.
  try {
    if (value && typeof value === 'object') {
      const code = Object.getOwnPropertyDescriptor(value, 'code')?.value;
      const delivery = Object.getOwnPropertyDescriptor(value, 'delivery')?.value;
      if (['unavailable_binding', 'disconnected', 'delivery_failed'].includes(code)
        && ['not_sent', 'may_have_reached_backend'].includes(delivery)) return { code, delivery };
    }
  } catch { /* Native/adapter exception details are intentionally excluded. */ }
  return { code: 'delivery_failed', delivery: 'may_have_reached_backend' };
}

/** Typed observation client. Caller abort is never a backend cancel command. */
export class BridgeClient {
  private readonly pending = new Map<string, Pending>();
  private readonly recentIds = new Set<string>();
  private readonly replays = new Map<string, CommitReplay>();
  // The original capture stays immutable across exact replay. Delivery custody
  // belongs to each submission, including one made by another shared caller.
  private readonly replaySubmissions = new WeakMap<RequestCapture, { latest: RequestCapture; delivery: 'pending' | ClientFault['delivery'] }>();
  private readonly subscriptions = new Set<() => void>();
  private readonly clock: ClientClock;
  private readonly timeoutMs: number;
  private readonly maximumPending: number;
  private readonly maximumReplays: number;
  private readonly maximumRecentIds: number;
  private readonly maximumSubscriptions: number;
  private disposed = false;
  constructor(private readonly transport: RawTransport, private readonly options: ClientOptions) {
    this.clock = options.clock ?? systemClock;
    this.timeoutMs = bounded(options.timeoutMs ?? 30_000, 3_600_000);
    this.maximumPending = bounded(options.maximumPending ?? 64, 1024);
    this.maximumReplays = bounded(options.maximumReplays ?? 64, 1024);
    this.maximumRecentIds = bounded(options.maximumRecentIds ?? 1024, 65_536);
    this.maximumSubscriptions = bounded(options.maximumSubscriptions ?? 16, 1024);
  }
  get pendingCount(): number { return this.pending.size; }
  get replayCount(): number { return this.replays.size; }
  query<N extends QueryName>(name: N, input: QueryInput<N> | DeepReadonly<QueryInput<N>>, options: CallOptions = {}): Promise<ClientOutcome<QueryOutput<N>>> {
    return this.call('query', name, input, options);
  }
  command<N extends CommandName>(name: N, input: CommandInput<N> | DeepReadonly<CommandInput<N>>, options: CallOptions = {}): Promise<ClientOutcome<CommandOutput<N>>> {
    return this.call('command', name, input, options);
  }
  getReplay(key: string): CommitReplay | undefined { return this.replays.get(key); }
  /** Explicit release after backend reconciliation or a user's deliberate choice. */
  forgetReplay(key: string): boolean { return this.replays.delete(key); }
  /** Retire only an owned original submission still proved unsent by this client. */
  forgetUnsentReplay(key: string, original: RequestCapture): boolean {
    const replay = this.replays.get(key), submission = this.replaySubmissions.get(original);
    if (replay?.request !== original || replay.operationId || submission?.latest !== original || submission.delivery !== 'not_sent') return false;
    return this.replays.delete(key);
  }
  replayCommit(key: string, options: CallOptions = {}): Promise<ClientOutcome<CommandOutput<'commit'>>> {
    const replay = this.replays.get(key);
    return replay ? this.command('commit', replay.input, options) : Promise.resolve(this.fault('replay_missing', 'not_sent'));
  }
  subscribe(onEvent: (event: DeepReadonly<Event>) => void, onFault: (fault: ClientFault) => void = () => {}): () => void {
    if (this.disposed) { onFault({ code: 'disposed', delivery: 'not_sent' }); return () => {}; }
    if (this.subscriptions.size >= this.maximumSubscriptions) { onFault({ code: 'subscription_limit', delivery: 'not_sent' }); return () => {}; }
    let active = true;
    let settingUp = true;
    let release: (() => void) | undefined;
    const stop = () => {
      active = false;
      if (!settingUp) {
        this.subscriptions.delete(stop);
        const disposer = release;
        release = undefined;
        try { disposer?.(); } catch { /* Observer cleanup must not expose adapter exceptions. */ }
      }
    };
    // Reserve capacity and disposal custody before an adapter can call back.
    this.subscriptions.add(stop);
    try {
      release = this.transport.subscribe(frame => {
        if (!active) return;
        let event: DeepReadonly<Event>;
        try { event = decodeEvent(frame); } catch (error) {
          stop(); onFault({ code: error instanceof ClientBoundaryError ? error.code : 'framing', delivery: 'may_have_reached_backend' }); return;
        }
        onEvent(event);
      }, error => { if (active) { stop(); onFault(transportFault(error)); } });
    } catch (error) { if (active) { stop(); onFault(transportFault(error)); } }
    finally {
      settingUp = false;
      if (!active || this.disposed) stop();
    }
    return stop;
  }
  dispose(): void {
    if (this.disposed) return;
    this.disposed = true;
    for (const pending of [...this.pending.values()]) pending.abandon('disposed');
    for (const unsubscribe of [...this.subscriptions]) unsubscribe();
  }
  private fault(code: ClientFaultCode, delivery: ClientFault['delivery'], request?: RequestMetadata): ClientOutcome<never> {
    return Object.freeze({ kind: 'fault', fault: Object.freeze({ code, delivery, ...(request ? { request } : {}) }) });
  }
  private call<T>(kind: RequestMetadata['kind'], method: RequestMetadata['method'], input: unknown, callOptions: CallOptions): Promise<ClientOutcome<T>> {
    if (this.disposed) return Promise.resolve(this.fault('disposed', 'not_sent'));
    if (callOptions.signal?.aborted) return Promise.resolve(this.fault('observational_abort', 'not_sent'));
    if (this.pending.size >= this.maximumPending) return Promise.resolve(this.fault('pending_limit', 'not_sent'));
    let capture: RequestCapture;
    let timeoutMs: number;
    try {
      timeoutMs = bounded(callOptions.timeoutMs ?? this.timeoutMs, 3_600_000);
      capture = captureRequest({ protocolVersion: 1, requestId: this.options.requestId(), body: { type: kind, [kind]: { name: method, input } } });
    } catch (error) { return Promise.resolve(this.fault(error instanceof ClientBoundaryError ? error.code : 'invalid_capture', 'not_sent')); }
    const request = capture.request;
    const metadata: RequestMetadata = Object.freeze({ requestId: request.requestId, kind, method });
    if (this.recentIds.has(request.requestId) || this.pending.has(request.requestId)) return Promise.resolve(this.fault('request_id_reused', 'not_sent', metadata));
    let replayOwner: RequestCapture | undefined;
    let replayKey: string | undefined;
    if (kind === 'command' && method === 'commit' && request.body.type === 'command' && request.body.command.name === 'commit') {
      const commit = request.body.command.input;
      const prior = this.replays.get(commit.idempotencyKey);
      if (prior && canonicalData(prior.input) !== canonicalData(commit)) return Promise.resolve(this.fault('replay_conflict', 'not_sent', metadata));
      if (!prior && this.replays.size >= this.maximumReplays) return Promise.resolve(this.fault('replay_limit', 'not_sent', metadata));
      if (!prior) this.replays.set(commit.idempotencyKey, Object.freeze({ input: commit, request: capture }));
      replayOwner = prior?.request ?? capture;
      replayKey = commit.idempotencyKey;
      // Reserve the new submission before clocks or transport can call back.
      this.replaySubmissions.set(replayOwner, { latest: capture, delivery: 'pending' });
    }
    this.recentIds.add(request.requestId);
    if (this.recentIds.size > this.maximumRecentIds) this.recentIds.delete(this.recentIds.values().next().value!);
    return new Promise(resolve => {
      let settled = false;
      let sent = false;
      let pendingRequest: DeepReadonly<Request> | undefined = request;
      let cancelTimer = () => {};
      const controller = new AbortController();
      const finish = (outcome: ClientOutcome<T>) => {
        if (settled) return;
        settled = true; cancelTimer(); callOptions.signal?.removeEventListener('abort', abort);
        const submission = replayOwner && this.replaySubmissions.get(replayOwner);
        if (submission?.latest === capture && replayKey && this.replays.get(replayKey)?.request === replayOwner) {
          submission.delivery = outcome.kind === 'fault' ? outcome.fault.delivery : 'may_have_reached_backend';
        }
        pendingRequest = undefined;
        this.pending.delete(metadata.requestId); resolve(outcome);
      };
      const abandon = (code: ClientFaultCode) => {
        finish(this.fault(code, sent ? 'may_have_reached_backend' : 'not_sent', metadata));
        // The adapter receives observation disposal only. Backend custody is independent.
        controller.abort();
      };
      const abort = () => abandon('observational_abort');
      this.pending.set(metadata.requestId, { abandon });
      callOptions.signal?.addEventListener('abort', abort, { once: true });
      if (callOptions.signal?.aborted) { abandon('observational_abort'); return; }
      try { cancelTimer = this.clock.schedule(timeoutMs, () => abandon('timeout')); }
      catch { abandon('delivery_failed'); return; }
      if (settled) { cancelTimer(); return; }
      sent = true;
      let exchange: Promise<import('./transport').RawFrame>;
      try { exchange = this.transport.exchange(capture.encoded, { signal: controller.signal }); }
      catch (error) { const failure = transportFault(error); finish(this.fault(failure.code, failure.delivery, metadata)); return; }
      Promise.resolve(exchange).then(async frame => {
        const request = pendingRequest;
        if (settled || !request) return;
        let reply;
        try { reply = decodeReply(frame); } catch (error) {
          finish(this.fault(error instanceof ClientBoundaryError ? error.code : 'framing', 'may_have_reached_backend', metadata)); return;
        }
        if (reply.requestId === null || reply.requestId !== request.requestId) { finish(this.fault('correlation', 'may_have_reached_backend', metadata)); return; }
        if (reply.body.type === 'rejected') { finish(Object.freeze({ kind: 'rejected', error: reply.body.error, request: metadata })); return; }
        const result = reply.body.result;
        if (result.type !== kind) { finish(this.fault('correlation', 'may_have_reached_backend', metadata)); return; }
        const invocation = result.type === 'query' ? result.query : result.command;
        if (invocation.name !== method) { finish(this.fault('correlation', 'may_have_reached_backend', metadata)); return; }
        if (request.body.type === 'query' && request.body.query.name === 'get_operation' && result.type === 'query' && result.query.name === 'get_operation'
          && result.query.output.operation.status === 'observed' && request.body.query.input.operationId !== result.query.output.operation.value.operationId) {
          finish(this.fault('correlation', 'may_have_reached_backend', metadata)); return;
        }
        if (request.body.type === 'query' && request.body.query.name === 'diagnostic_preview' && result.type === 'query' && result.query.name === 'diagnostic_preview') {
          if (!diagnosticPreviewMatches(request.body.query.input, result.query.output)) {
            finish(this.fault('correlation', 'may_have_reached_backend', metadata)); return;
          }
          const digest = await diagnosticPreviewDigest(result.query.output.content);
          if (settled || pendingRequest !== request) return;
          if (digest !== result.query.output.reference.digest) { finish(this.fault('correlation', 'may_have_reached_backend', metadata)); return; }
        }
        if (request.body.type === 'command' && result.type === 'command') {
          const command = request.body.command;
          const output = result.command;
          if (command.name === 'prepare' && output.name === 'prepare' && !preparationMatches(command.input.intent, output.output.semantics)
            || command.name === 'cancel_operation' && output.name === 'cancel_operation' && !cancellationMatches(command.input, output.output)
            || command.name === 'open_draft' && output.name === 'open_draft' && !bindingEquivalent(command.input.document, output.output.draft.document)
            || command.name === 'set_draft_changes' && output.name === 'set_draft_changes'
              && !draftAcknowledgementMatches(command.input, output.output)
            || command.name === 'discard_draft' && output.name === 'discard_draft'
              && (command.input.draft.draftId !== output.output.draftId || command.input.draft.hostEpoch !== output.output.hostEpoch
                || command.input.draft.revision !== output.output.previousRevision)
            || command.name === 'request_sensitive_input' && output.name === 'request_sensitive_input' && !bindingEquivalent(command.input, output.output.binding)
            || command.name === 'request_export_destination' && output.name === 'request_export_destination' && !bindingEquivalent(command.input, output.output.binding)) {
            finish(this.fault('correlation', 'may_have_reached_backend', metadata)); return;
          }
          if (command.name === 'prepare' && output.name === 'prepare' || command.name === 'commit' && output.name === 'commit') {
            const semantics = output.output.semantics;
            const expected = command.name === 'prepare' && output.name === 'prepare' ? output.output.planRef.reviewDigest
              : command.name === 'commit' ? command.input.planRef.reviewDigest : '';
            const digest = await semanticPlanDigest(semantics);
            // Timeout, abort and disposal abandon observation even while the
            // local digest is running. They cannot admit a late replay identity.
            if (settled || pendingRequest !== request) return;
            if (digest !== expected) { finish(this.fault('correlation', 'may_have_reached_backend', metadata)); return; }
          }
          if (command.name === 'commit' && output.name === 'commit') {
            const replay = this.replays.get(command.input.idempotencyKey);
            if (replay && replay.request === replayOwner && canonicalData(replay.input) === canonicalData(command.input)) {
              if (replay.operationId && replay.operationId !== output.output.operationId) { finish(this.fault('correlation', 'may_have_reached_backend', metadata)); return; }
              if (!replay.operationId) this.replays.set(command.input.idempotencyKey, Object.freeze({ ...replay, operationId: output.output.operationId }));
            }
          }
        }
        finish(Object.freeze({ kind: 'result', value: invocation.output as DeepReadonly<T>, request: metadata }));
      }).catch(error => { if (!settled) { const failure = transportFault(error); finish(this.fault(failure.code, failure.delivery, metadata)); } });
    });
  }
}
