import type { CloseDisposition, Cursor, DiscardedDraft, DraftSnapshot, Event, EventBatch, GetOperationResult, OperationSnapshot, Snapshot } from '../generated/protocol';
import { captureData, canonicalData, type DeepReadonly } from './wire';
import { BridgeClient, type ClientFault, type ClientOutcome, type CallOptions } from './client';
import { bindingEquivalent, operationRecoveryMatches, semanticPlanKey } from './relations';

export type ObservationReason = 'epoch_changed' | 'stream_changed' | 'sequence_gap' | 'contradictory_duplicate' | 'duplicate_event'
  | 'unverifiable_duplicate' | 'revision_regressed' | 'revision_reused' | 'capture_changed' | 'terminal_changed'
  | 'pending_operation_omitted' | 'partial_inventory' | 'snapshot_invalidated' | 'buffer_limit'
  | 'observation_limit' | 'malformed_event' | 'resume_mismatch' | 'disconnected' | 'close_obligation_mismatch' | 'discard_revision_mismatch';
export interface ObservationState {
  readonly confidence: 'uninitialized' | 'authoritative' | 'partial' | 'stale';
  readonly resnapshotRequired: boolean;
  readonly reason?: ObservationReason;
  readonly cursor?: DeepReadonly<Cursor>;
  readonly snapshot?: DeepReadonly<Snapshot>;
  readonly operations: readonly DeepReadonly<OperationSnapshot>[];
  readonly drafts: readonly DeepReadonly<DraftSnapshot>[];
}
export interface ObservationOptions {
  readonly maximumRecentEvents?: number;
  readonly maximumBufferedEvents?: number;
  readonly maximumOperations?: number;
  readonly maximumDrafts?: number;
}
const limit = (value: number): number => {
  if (!Number.isSafeInteger(value) || value < 1 || value > 4096) throw new RangeError('observation_limit');
  return value;
};
/** Counters are scoped decimal strings on the wire; never Number or lexical order. */
export function counter(value: string): bigint {
  if (!/^(0|[1-9][0-9]{0,19})$/.test(value)) throw new RangeError('counter');
  const parsed = BigInt(value);
  if (parsed > 18446744073709551615n) throw new RangeError('counter');
  return parsed;
}
const sameStream = (left: DeepReadonly<Cursor>, right: DeepReadonly<Cursor>) => left.hostEpoch === right.hostEpoch && left.streamId === right.streamId;
const cursorKey = (cursor: DeepReadonly<Cursor>) => canonicalData([cursor.hostEpoch, cursor.streamId, cursor.sequence]);
function semanticsKey(operation: DeepReadonly<OperationSnapshot>): string {
  return semanticPlanKey(operation.semantics);
}

/** Portable relational observation only. It grants no execution or native authority. */
export class ObservationStore {
  private operations = new Map<string, DeepReadonly<OperationSnapshot>>();
  private drafts = new Map<string, DeepReadonly<DraftSnapshot>>();
  private readonly discardedDrafts = new Map<string, DeepReadonly<DraftSnapshot>>();
  private readonly digests = new Map<string, string>();
  private buffer: DeepReadonly<Event>[] = [];
  private buffering = false;
  private bufferFailed = false;
  private cursor?: DeepReadonly<Cursor>;
  private snapshot?: DeepReadonly<Snapshot>;
  private confidence: ObservationState['confidence'] = 'uninitialized';
  private reason?: ObservationReason;
  private readonly listeners = new Set<(state: ObservationState) => void>();
  private readonly maximumRecentEvents: number;
  private readonly maximumBufferedEvents: number;
  private readonly maximumOperations: number;
  private readonly maximumDrafts: number;
  constructor(options: ObservationOptions = {}) {
    this.maximumRecentEvents = limit(options.maximumRecentEvents ?? 128);
    this.maximumBufferedEvents = limit(options.maximumBufferedEvents ?? 128);
    this.maximumOperations = limit(options.maximumOperations ?? 256);
    this.maximumDrafts = limit(options.maximumDrafts ?? 64);
  }
  get state(): ObservationState {
    return Object.freeze({ confidence: this.confidence, resnapshotRequired: this.confidence !== 'authoritative',
      ...(this.reason ? { reason: this.reason } : {}), ...(this.cursor ? { cursor: this.cursor } : {}),
      ...(this.snapshot ? { snapshot: this.snapshot } : {}), operations: Object.freeze([...this.operations.values()]), drafts: Object.freeze([...this.drafts.values()]) });
  }
  subscribe(listener: (state: ObservationState) => void): () => void {
    this.listeners.add(listener); listener(this.state); return () => this.listeners.delete(listener);
  }
  private publish(): void { const state = this.state; for (const listener of this.listeners) listener(state); }
  invalidate(reason: ObservationReason): false { this.confidence = 'stale'; this.reason = reason; this.publish(); return false; }
  observeHello(hostEpoch: string): void {
    if (this.cursor && this.cursor.hostEpoch !== hostEpoch) this.invalidate('epoch_changed');
  }
  beginSnapshot(): void { this.buffering = true; this.bufferFailed = false; this.buffer = []; }
  abandonSnapshot(): void { this.buffering = false; this.buffer = []; this.invalidate('disconnected'); }
  /** Safe forgetting is explicit; uncompleted work cannot disappear via pruning. */
  forgetCompletedOperation(id: string): boolean {
    if (this.operations.get(id)?.state.status !== 'completed') return false;
    const removed = this.operations.delete(id); if (removed) this.publish(); return removed;
  }
  private operationProblem(operation: DeepReadonly<OperationSnapshot>, map: Map<string, DeepReadonly<OperationSnapshot>>): ObservationReason | undefined {
    if (!operationRecoveryMatches(operation)) return 'capture_changed';
    const revision = counter(operation.operationRevision);
    const previous = map.get(operation.operationId);
    if (!previous) return map.size >= this.maximumOperations ? 'observation_limit' : undefined;
    if (semanticsKey(previous) !== semanticsKey(operation)) return 'capture_changed';
    const previousRevision = counter(previous.operationRevision);
    if (revision < previousRevision) return 'revision_regressed';
    if (previous.state.status === 'completed' && !bindingEquivalent(previous.state, operation.state)) return 'terminal_changed';
    if (revision === previousRevision && !bindingEquivalent(previous.state, operation.state)) return 'revision_reused';
    return undefined;
  }
  observeOperation(input: OperationSnapshot | DeepReadonly<OperationSnapshot>): boolean {
    try {
      const operation = captureData(input);
      const problem = this.operationProblem(operation, this.operations);
      if (problem) return this.invalidate(problem);
      this.operations.set(operation.operationId, operation); this.publish(); return true;
    } catch { return this.invalidate('malformed_event'); }
  }
  observeOperationResult(id: string, result: GetOperationResult | DeepReadonly<GetOperationResult>): boolean {
    if (result.operation.status === 'observed') {
      return result.operation.value.operationId === id ? this.observeOperation(result.operation.value) : this.invalidate('capture_changed');
    }
    if (result.operation.status === 'missing' && this.operations.has(id)) return this.invalidate('pending_operation_omitted');
    return true;
  }
  private closeProblem(disposition: CloseDisposition | DeepReadonly<CloseDisposition>): boolean {
    const pending = [...this.operations.values()].filter(operation => operation.state.status !== 'completed');
    if (disposition.kind === 'ready') return !pending.length && this.confidence === 'authoritative';
    const covered = new Set<string>();
    if (disposition.kind === 'deferred') {
      if (!disposition.obligations.length) return false;
      const sessions = new Set<string>();
      for (const obligation of disposition.obligations) {
        if (obligation.kind === 'session_custody') {
          const key = canonicalData(obligation.session);
          if (sessions.has(key)) return false;
          sessions.add(key); continue;
        }
        if (covered.has(obligation.operationId)) return false;
        covered.add(obligation.operationId);
        const operation = this.operations.get(obligation.operationId);
        if (operation && (operation.state.status === 'completed' || operation.operationRevision !== obligation.operationRevision)) return false;
      }
    } else {
      if (!disposition.recoveries.length) return false;
      for (const recovery of disposition.recoveries) {
        if (covered.has(recovery.operationId)) return false;
        covered.add(recovery.operationId);
        const operation = this.operations.get(recovery.operationId);
        if (operation && (operation.state.status !== 'recovery_required' || !bindingEquivalent(operation.state.recovery, recovery))) return false;
      }
    }
    return pending.every(operation => covered.has(operation.operationId));
  }
  observeClose(disposition: CloseDisposition | DeepReadonly<CloseDisposition>): boolean {
    try { return this.closeProblem(disposition) || this.invalidate('close_obligation_mismatch'); }
    catch { return this.invalidate('close_obligation_mismatch'); }
  }
  private draftProblem(draft: DeepReadonly<DraftSnapshot>, map: Map<string, DeepReadonly<DraftSnapshot>>): ObservationReason | undefined {
    const key = canonicalData([draft.draft.hostEpoch, draft.draft.draftId]);
    const previous = map.get(key) ?? this.discardedDrafts.get(key);
    const revision = counter(draft.draft.revision);
    if (!previous) return map.size + this.discardedDrafts.size >= this.maximumDrafts ? 'observation_limit' : undefined;
    if (!bindingEquivalent(previous.draft.document, draft.draft.document)) return 'capture_changed';
    const previousRevision = counter(previous.draft.revision);
    if (revision < previousRevision) return 'revision_regressed';
    if (this.discardedDrafts.has(key) && revision === previousRevision) return 'revision_reused';
    if (revision === previousRevision && !bindingEquivalent(previous, draft)) return 'revision_reused';
    return undefined;
  }
  observeDraft(input: DraftSnapshot | DeepReadonly<DraftSnapshot>): boolean {
    try {
      const draft = captureData(input);
      const problem = this.draftProblem(draft, this.drafts);
      if (problem) return this.invalidate(problem);
      const key = canonicalData([draft.draft.hostEpoch, draft.draft.draftId]);
      this.discardedDrafts.delete(key);
      this.drafts.set(key, draft); this.publish(); return true;
    } catch { return this.invalidate('malformed_event'); }
  }
  /** Discard receipts remove only the exact observed epoch, ID and revision. */
  observeDiscard(input: DiscardedDraft | DeepReadonly<DiscardedDraft>): boolean {
    try {
      const receipt = captureData(input);
      counter(receipt.previousRevision);
      const key = canonicalData([receipt.hostEpoch, receipt.draftId]);
      const draft = this.drafts.get(key);
      if (!draft) {
        const discarded = this.discardedDrafts.get(key);
        return !discarded || discarded.draft.revision === receipt.previousRevision || this.invalidate('discard_revision_mismatch');
      }
      if (draft.draft.revision !== receipt.previousRevision) return this.invalidate('discard_revision_mismatch');
      this.drafts.delete(key);
      // A bounded marker retains the observed namespace/revision so a stale
      // draft event cannot resurrect edits just acknowledged as discarded.
      this.discardedDrafts.set(key, draft); this.publish(); return true;
    } catch { return this.invalidate('malformed_event'); }
  }
  forgetDiscardedDraft(hostEpoch: string, draftId: string): boolean {
    return this.discardedDrafts.delete(canonicalData([hostEpoch, draftId]));
  }
  acceptSnapshot(input: Snapshot | DeepReadonly<Snapshot>): boolean {
    const refuse = (reason: ObservationReason): false => { this.buffering = false; this.buffer = []; return this.invalidate(reason); };
    try {
      const snapshot = captureData(input);
      counter(snapshot.cursor.sequence);
      if (this.bufferFailed) return refuse('buffer_limit');
      if (this.cursor && sameStream(this.cursor, snapshot.cursor) && counter(snapshot.cursor.sequence) < counter(this.cursor.sequence)) return refuse('sequence_gap');
      const next = new Map(this.operations);
      const included = new Set<string>();
      for (const operation of snapshot.operations.items) {
        if (included.has(operation.operationId)) return refuse('revision_reused');
        included.add(operation.operationId);
        const problem = this.operationProblem(operation, next);
        if (problem) return refuse(problem);
        next.set(operation.operationId, operation);
      }
      if (snapshot.operations.completeness === 'complete') {
        if (snapshot.operations.issues.length) return refuse('malformed_event');
        for (const known of this.operations.values()) {
          if (known.state.status !== 'completed' && !included.has(known.operationId)) return refuse('pending_operation_omitted');
        }
      } else if (!snapshot.operations.issues.length) return refuse('malformed_event');
      this.operations = next; this.cursor = snapshot.cursor; this.snapshot = snapshot;
      const partial = snapshot.operations.completeness === 'partial' || snapshot.capabilities.completeness === 'partial'
        || snapshot.profiles.status === 'observed' && snapshot.profiles.value.completeness === 'partial'
        || snapshot.installations.status === 'observed' && snapshot.installations.value.completeness === 'partial'
        || snapshot.sessions.status === 'observed' && snapshot.sessions.value.completeness === 'partial';
      this.confidence = partial ? 'partial' : 'authoritative';
      this.reason = this.confidence === 'partial' ? 'partial_inventory' : undefined;
      this.digests.clear();
      const buffered = this.buffer; this.buffer = []; this.buffering = false;
      for (const event of buffered) {
        if (!sameStream(event.cursor, snapshot.cursor)) return this.invalidate(event.cursor.hostEpoch !== snapshot.cursor.hostEpoch ? 'epoch_changed' : 'stream_changed');
        // The authoritative same-stream watermark accounts for prior buffered events.
        if (counter(event.cursor.sequence) <= counter(snapshot.cursor.sequence)) continue;
        if (!this.acceptEvent(event)) return false;
      }
      this.publish(); return this.confidence === 'authoritative';
    } catch { return refuse('malformed_event'); }
  }
  acceptEvent(input: Event | DeepReadonly<Event>): boolean {
    try {
      const event = captureData(input);
      counter(event.cursor.sequence);
      if (this.buffering) {
        if (this.bufferFailed) return false;
        if (this.buffer.length >= this.maximumBufferedEvents) { this.buffer = []; this.bufferFailed = true; return this.invalidate('buffer_limit'); }
        this.buffer.push(event); return true;
      }
      if (!this.cursor) return this.invalidate('sequence_gap');
      if (!sameStream(this.cursor, event.cursor)) return this.invalidate(event.cursor.hostEpoch !== this.cursor.hostEpoch ? 'epoch_changed' : 'stream_changed');
      const sequence = counter(event.cursor.sequence);
      const current = counter(this.cursor.sequence);
      const key = cursorKey(event.cursor);
      const digest = canonicalData(event);
      if (sequence <= current) {
        const previous = this.digests.get(key);
        if (previous === digest) return this.invalidate('duplicate_event');
        return this.invalidate(previous === undefined ? 'unverifiable_duplicate' : 'contradictory_duplicate');
      }
      if (this.confidence === 'stale') return false;
      if (sequence !== current + 1n) return this.invalidate('sequence_gap');
      if (event.body.type === 'operation_changed') {
        const problem = this.operationProblem(event.body.operation, this.operations);
        if (problem) return this.invalidate(problem);
        this.operations.set(event.body.operation.operationId, event.body.operation);
      } else if (event.body.type === 'draft_changed') {
        const problem = this.draftProblem(event.body.draft, this.drafts);
        if (problem) return this.invalidate(problem);
        const draftKey = canonicalData([event.body.draft.draft.hostEpoch, event.body.draft.draft.draftId]);
        this.discardedDrafts.delete(draftKey);
        this.drafts.set(draftKey, event.body.draft);
      } else if (event.body.type === 'host_close_deferred' && !this.closeProblem({ kind: 'deferred', obligations: event.body.obligations })) {
        return this.invalidate('close_obligation_mismatch');
      }
      this.cursor = event.cursor; this.digests.set(key, digest);
      if (this.digests.size > this.maximumRecentEvents) this.digests.delete(this.digests.keys().next().value!);
      if (event.body.type === 'snapshot_invalidated') return this.invalidate('snapshot_invalidated');
      this.publish(); return true;
    } catch { return this.invalidate('malformed_event'); }
  }
  acceptBatch(batch: EventBatch | DeepReadonly<EventBatch>, requestedAfter: Cursor | DeepReadonly<Cursor>, maximumEvents: string): boolean {
    try {
      if (!this.cursor || canonicalData(batch.after) !== canonicalData(requestedAfter) || canonicalData(this.cursor) !== canonicalData(requestedAfter)
        || BigInt(batch.events.length) > counter(maximumEvents)) return this.invalidate('resume_mismatch');
      for (const event of batch.events) {
        // Resume batches must be consecutive; unsolicited duplicate allowance does not apply.
        if (!this.cursor || !sameStream(this.cursor, event.cursor) || counter(event.cursor.sequence) !== counter(this.cursor.sequence) + 1n) return this.invalidate('resume_mismatch');
        if (!this.acceptEvent(event)) return false;
      }
      if (!this.cursor || canonicalData(batch.next) !== canonicalData(this.cursor)) return this.invalidate('resume_mismatch');
      return true;
    } catch { return this.invalidate('resume_mismatch'); }
  }
}

/** Subscribe before read-only snapshot; bounded raw events cross the client first. */
export class ObservationSession {
  private stop?: () => void;
  private refreshPending?: Promise<ClientOutcome<Snapshot>>;
  private disposed = false;
  private subscribing = false;
  private generation = 0;
  constructor(readonly client: BridgeClient, readonly store = new ObservationStore()) {}
  start(options: CallOptions = {}): Promise<ClientOutcome<Snapshot>> {
    if (this.disposed) return Promise.resolve({ kind: 'fault', fault: { code: 'disposed', delivery: 'not_sent' } });
    if (this.subscribing) return Promise.resolve({ kind: 'fault', fault: { code: 'stale_stream', delivery: 'not_sent' } });
    if (!this.stop) {
      const generation = this.generation;
      let active = true;
      let setupFault: ClientFault | undefined;
      let stop: () => void;
      this.subscribing = true;
      // Raw adapters may emit immediately during registration. Those events
      // belong in the same bounded watermark buffer as later pending events.
      this.store.beginSnapshot();
      try {
        stop = this.client.subscribe(event => {
          if (active && !this.disposed && generation === this.generation) this.store.acceptEvent(event);
        }, fault => { active = false; setupFault = fault; this.deliveryFault(fault); });
      } finally { this.subscribing = false; }
      if (!active || this.disposed || generation !== this.generation) {
        stop();
        return Promise.resolve({ kind: 'fault', fault: setupFault ?? { code: this.disposed ? 'disposed' : 'stale_stream', delivery: 'not_sent' } });
      }
      this.stop = stop;
      return this.requestSnapshot(options, true);
    }
    return this.refresh(options);
  }
  refresh(options: CallOptions = {}): Promise<ClientOutcome<Snapshot>> {
    return this.requestSnapshot(options);
  }
  private requestSnapshot(options: CallOptions, buffering = false): Promise<ClientOutcome<Snapshot>> {
    if (this.disposed) return Promise.resolve({ kind: 'fault', fault: { code: 'disposed', delivery: 'not_sent' } });
    if (!this.stop || this.subscribing) return Promise.resolve({ kind: 'fault', fault: { code: 'stale_stream', delivery: 'not_sent' } });
    if (this.refreshPending) return this.refreshPending;
    if (!buffering) this.store.beginSnapshot();
    const generation = this.generation;
    const pending = this.client.query('snapshot', {}, options).then(outcome => {
      if (this.disposed || generation !== this.generation) return outcome;
      if (outcome.kind === 'result') this.store.acceptSnapshot(outcome.value);
      else this.store.abandonSnapshot();
      return outcome;
    }).finally(() => { if (this.refreshPending === pending) this.refreshPending = undefined; });
    this.refreshPending = pending; return pending;
  }
  async resume(maximumEvents = '128', options: CallOptions = {}): Promise<ClientOutcome<EventBatch>> {
    if (this.disposed) return { kind: 'fault', fault: { code: 'disposed', delivery: 'not_sent' } };
    const after = this.store.state.cursor;
    if (!after || this.store.state.resnapshotRequired) return { kind: 'fault', fault: { code: 'stale_stream', delivery: 'not_sent' } };
    const generation = this.generation;
    const outcome = await this.client.query('resume_events', { after, maximumEvents }, options);
    if (this.disposed || generation !== this.generation) {
      const request = outcome.kind === 'fault' ? outcome.fault.request : outcome.request;
      return { kind: 'fault', fault: { code: this.disposed ? 'disposed' : 'stale_stream', delivery: 'may_have_reached_backend', ...(request ? { request } : {}) } };
    }
    if (outcome.kind === 'result') this.store.acceptBatch(outcome.value, after, maximumEvents);
    else if (outcome.kind === 'rejected' && outcome.error.code === 'resnapshot_required') this.store.invalidate('sequence_gap');
    else if (outcome.kind === 'fault') this.deliveryFault(outcome.fault);
    return outcome;
  }
  private deliveryFault(fault: ClientFault): void {
    this.generation++;
    this.refreshPending = undefined;
    this.stop?.(); this.stop = undefined;
    this.store.abandonSnapshot();
    this.store.invalidate(['framing', 'schema', 'incompatible_envelope'].includes(fault.code) ? 'malformed_event' : 'disconnected');
  }
  dispose(): void { this.disposed = true; this.stop?.(); this.stop = undefined; this.store.abandonSnapshot(); }
}
