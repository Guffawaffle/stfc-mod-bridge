import { BridgeClient, WorkContext, canonicalData, captureData, type CallOptions, type ClientOutcome, type CommitReplay, type DeepReadonly, type ObservationState, type RequestMetadata } from '../client';
import { bindingEquivalent, preparationMatches, semanticPlanKey } from '../client/relations';
import type { CancelDisposition, CommitInput, MutationIntent, OperationSnapshot, PreparedPlan } from '../generated/protocol';
import type { AnnouncementController, FocusController } from './controllers';

export type ActionTransition = { readonly kind: 'idle' | 'preparing' | 'admitting' | 'uncertain' }
  | { readonly kind: 'review'; readonly plan: DeepReadonly<PreparedPlan> }
  | { readonly kind: 'observing'; readonly operationId: string };
export interface ActionReviewState { readonly transition: ActionTransition; readonly notice: string; readonly cancellingOperationIds: readonly string[]; readonly recoveryOperationIds: readonly string[]; readonly plan?: DeepReadonly<PreparedPlan>; }
export interface ActionReviewOptions {
  readonly idempotencyKey: () => string;
  readonly canPrepare: () => boolean;
  readonly announcements: AnnouncementController;
  readonly focus: FocusController;
}
type Attempt = { intent: DeepReadonly<MutationIntent>; focusKey?: string; epoch?: string; plan?: DeepReadonly<PreparedPlan>; commit?: DeepReadonly<CommitInput>;
  operationId?: string; request?: RequestMetadata; ownsReplay?: boolean; replayCapture?: CommitReplay['request']; provedUnsent?: boolean; priorDeliveryUncertain?: boolean };

/** Review presentation only; the shared client/store retain replay and operation facts. */
export class ActionReviewController {
  private transition: ActionTransition = { kind: 'idle' };
  private notice = '';
  private attempt?: Attempt;
  private disposed = false;
  private settling = false;
  private readonly cancelling = new Set<string>();
  private readonly recoveryAttempts = new Map<string, Attempt>();
  private lifecycle = new AbortController();
  private listeners = new Set<(state: ActionReviewState) => void>();
  private publication = 0;
  private stop: () => void;
  constructor(readonly client: BridgeClient, readonly work: WorkContext, private readonly options: ActionReviewOptions) {
    this.stop = work.observations.subscribe(() => this.reconcileObserved());
  }
  get state(): ActionReviewState { return Object.freeze({ transition: Object.freeze({ ...this.transition }), notice: this.notice, cancellingOperationIds: Object.freeze([...this.cancelling]), recoveryOperationIds: Object.freeze([...this.recoveryAttempts.keys()]), ...(this.attempt?.plan ? { plan: this.attempt.plan } : {}) }); }
  get blocksTransitions(): boolean { return ['preparing', 'review', 'admitting', 'uncertain'].includes(this.transition.kind); }
  subscribe(listener: (state: ActionReviewState) => void): () => void { this.listeners.add(listener); listener(this.state); return () => this.listeners.delete(listener); }
  private publish(): void {
    if (this.disposed) return;
    const publication = ++this.publication, state = this.state;
    for (const listener of [...this.listeners]) {
      if (this.disposed || publication !== this.publication) return;
      if (this.listeners.has(listener)) listener(state);
    }
  }
  private say(message: string, urgent = false): void { this.notice = message; this.options.announcements.announce(message, urgent ? 'assertive' : 'polite'); this.publish(); }
  private async observe<T>(execute: (options: CallOptions) => Promise<ClientOutcome<T>>, options: CallOptions): Promise<ClientOutcome<T>> {
    const controller = new AbortController(), abort = () => controller.abort();
    this.lifecycle.signal.addEventListener('abort', abort, { once: true }); options.signal?.addEventListener('abort', abort, { once: true });
    if (this.lifecycle.signal.aborted || options.signal?.aborted) controller.abort();
    try { return await execute({ ...options, signal: controller.signal }); }
    finally { this.lifecycle.signal.removeEventListener('abort', abort); options.signal?.removeEventListener('abort', abort); }
  }
  async prepare(intent: MutationIntent | DeepReadonly<MutationIntent>, focusKey?: string, options: CallOptions = {}): Promise<ClientOutcome<PreparedPlan> | undefined> {
    if (this.disposed || this.transition.kind !== 'idle' || !this.options.canPrepare()) return undefined;
    const epoch = this.work.observations.state.cursor?.hostEpoch;
    if (epoch ? !this.work.hostEpochCurrent(epoch) : this.work.observations.state.reason === 'epoch_changed') return undefined;
    let attempt: Attempt;
    try { attempt = { intent: captureData(intent), focusKey, epoch }; }
    catch { this.say('Action review is unavailable.', true); return undefined; }
    this.attempt = attempt; this.transition = { kind: 'preparing' }; this.publish();
    if (this.disposed || this.attempt !== attempt) return { kind: 'fault', fault: { code: 'observational_abort', delivery: 'not_sent' } };
    if (epoch ? !this.work.hostEpochCurrent(epoch) : this.work.observations.state.reason === 'epoch_changed') {
      this.attempt = undefined; this.transition = { kind: 'idle' }; this.say('Action review was not confirmed. Refresh and review again.', true); return undefined;
    }
    const outcome = await this.observe(value => this.client.command('prepare', { intent: attempt.intent }, value), options);
    if (this.disposed || this.attempt !== attempt) return outcome;
    const currentEpoch = this.work.observations.state.cursor?.hostEpoch;
    if (outcome.kind !== 'result' || !this.work.hostEpochCurrent(outcome.value.planRef.hostEpoch) || attempt.epoch && currentEpoch !== attempt.epoch
      || currentEpoch && outcome.value.planRef.hostEpoch !== currentEpoch || !preparationMatches(attempt.intent, outcome.value.semantics)) {
      this.attempt = undefined; this.transition = { kind: 'idle' };
      this.say(outcome.kind === 'rejected' && outcome.error.code === 'artifact_unrecognized' ? 'The runtime is not recognized. Review the consent choice for this attempt.'
        : outcome.kind === 'rejected' && outcome.error.code === 'operation_busy' ? 'Another operation is using this target. Refresh its status.'
        : outcome.kind === 'rejected' && outcome.error.retryDisposition === 'after_user_choice' ? 'Action review needs a new choice. The current target is retained.'
        : 'Action review was not confirmed. Refresh and review again.', true);
    } else { attempt.plan = outcome.value; this.transition = { kind: 'review', plan: outcome.value }; this.say('Review the captured action before confirming.'); }
    return outcome;
  }
  async confirm(options: CallOptions = {}): Promise<ClientOutcome<OperationSnapshot> | undefined> {
    const attempt = this.attempt;
    if (this.disposed || this.transition.kind !== 'review' || !attempt?.plan) return undefined;
    const epoch = this.work.observations.state.cursor?.hostEpoch;
    const confidence = this.work.observations.state.confidence;
    if (!this.work.hostEpochCurrent(attempt.plan.planRef.hostEpoch)) {
      if (attempt.provedUnsent) this.releaseOwnedReplay(attempt);
      this.attempt = undefined; this.transition = { kind: 'idle' }; this.say('This review belongs to an earlier connection. Review again.', true); return undefined;
    }
    if (attempt.provedUnsent) this.releaseOwnedReplay(attempt);
    try { attempt.commit = captureData({ idempotencyKey: this.options.idempotencyKey(), planRef: attempt.plan.planRef }); }
    catch { this.say('Action submission is unavailable.', true); return undefined; }
    attempt.provedUnsent = false; this.transition = { kind: 'admitting' }; this.publish();
    if (this.disposed || this.attempt !== attempt) return { kind: 'fault', fault: { code: 'observational_abort', delivery: 'not_sent' } };
    if (!this.work.hostEpochCurrent(attempt.plan.planRef.hostEpoch)) {
      this.attempt = undefined; this.transition = { kind: 'idle' }; this.say('This review belongs to an earlier connection. Review again.', true); return undefined;
    }
    const outcome = await this.observe(value => {
      const prior = this.client.getReplay(attempt.commit!.idempotencyKey);
      attempt.ownsReplay = !prior; attempt.replayCapture = undefined;
      attempt.priorDeliveryUncertain = !!prior && canonicalData(prior.input) === canonicalData(attempt.commit);
      const pending = this.client.command('commit', attempt.commit!, value);
      const retained = this.client.getReplay(attempt.commit!.idempotencyKey);
      if (attempt.ownsReplay && retained && canonicalData(retained.input) === canonicalData(attempt.commit)) attempt.replayCapture = retained.request;
      return pending;
    }, options);
    if (!this.disposed && this.attempt === attempt) this.acceptCommit(attempt, outcome, false, epoch, confidence);
    return outcome;
  }
  async replay(options: CallOptions = {}): Promise<ClientOutcome<OperationSnapshot> | undefined> {
    const attempt = this.attempt;
    if (this.disposed || this.transition.kind !== 'uncertain' || !attempt?.commit) return undefined;
    const epoch = this.work.observations.state.cursor?.hostEpoch;
    const confidence = this.work.observations.state.confidence;
    this.transition = { kind: 'admitting' }; this.publish();
    const outcome = await this.observe(value => {
      const retained = this.client.getReplay(attempt.commit!.idempotencyKey);
      if (!retained || canonicalData(retained.input) !== canonicalData(attempt.commit)) return Promise.resolve<ClientOutcome<OperationSnapshot>>({ kind: 'fault', fault: { code: retained ? 'replay_conflict' : 'replay_missing', delivery: 'not_sent' } });
      return this.client.command('commit', attempt.commit!, value);
    }, options);
    if (!this.disposed && this.attempt === attempt) this.acceptCommit(attempt, outcome, true, epoch, confidence);
    return outcome;
  }
  private acceptCommit(attempt: Attempt, outcome: ClientOutcome<OperationSnapshot>, replay = false, requestedEpoch?: string, requestedConfidence?: ObservationState['confidence']): void {
    if (!replay && attempt.replayCapture) {
      const request = outcome.kind === 'fault' ? outcome.fault.request : outcome.request;
      if (attempt.replayCapture.request.requestId !== request?.requestId) attempt.replayCapture = undefined;
    }
    if (outcome.kind === 'fault' && !replay && !attempt.priorDeliveryUncertain && outcome.fault.delivery === 'not_sent') {
      attempt.provedUnsent = true; this.transition = { kind: 'review', plan: attempt.plan! }; this.say('Action was not submitted. The review is retained.', true); return;
    }
    if (outcome.kind !== 'result' || this.work.observations.state.cursor?.hostEpoch !== requestedEpoch
      || this.work.observations.state.confidence !== requestedConfidence || !attempt.plan || semanticPlanKey(outcome.value.semantics) !== semanticPlanKey(attempt.plan.semantics)
      || attempt.operationId && outcome.value.operationId !== attempt.operationId) {
      this.transition = { kind: 'uncertain' }; this.say('Action outcome is unconfirmed. The exact submission is retained.', true); return;
    }
    attempt.operationId = outcome.value.operationId; attempt.request = outcome.request;
    this.transition = { kind: 'observing', operationId: attempt.operationId };
    if (!this.work.observations.observeOperation(outcome.value)) { this.transition = { kind: 'uncertain' }; this.say('Action observation needs reconciliation.', true); return; }
    this.reconcileObserved(); if (this.attempt === attempt) this.say('Action admitted. Waiting for the operation outcome.');
  }
  private reconcileObserved(): void {
    if (this.disposed || this.settling) return;
    let releasedRecovery = false;
    for (const [id, held] of this.recoveryAttempts) {
      const operation = this.work.observations.state.operations.find(value => value.operationId === id);
      if (operation?.state.status === 'completed' && held.plan && semanticPlanKey(operation.semantics) === semanticPlanKey(held.plan.semantics)) {
        this.releaseOwnedReplay(held); this.recoveryAttempts.delete(id); releasedRecovery = true;
      }
    }
    if (releasedRecovery) this.publish();
    const attempt = this.attempt;
    if (!attempt?.operationId || !attempt.plan) return;
    const operation = this.work.observations.state.operations.find(value => value.operationId === attempt.operationId);
    if (!operation || semanticPlanKey(operation.semantics) !== semanticPlanKey(attempt.plan.semantics)) return;
    if (operation.state.status === 'recovery_required') {
      this.recoveryAttempts.set(attempt.operationId, attempt); this.attempt = undefined; this.transition = { kind: 'idle' };
      this.say('Action requires recovery. Its exact submission is retained for the recorded operation.', true); return;
    }
    if (operation.state.status !== 'completed') return;
    this.settling = true;
    try {
      this.releaseOwnedReplay(attempt); this.attempt = undefined; this.transition = { kind: 'idle' };
      const kind = operation.state.outcome.kind;
      this.say(kind === 'changed' ? 'Action completed.' : kind === 'no_change' ? 'Action completed. Already satisfied.'
        : kind === 'cancelled_before_commit' ? 'Action cancelled before commit.' : kind === 'rolled_back' ? 'Action rolled back.' : 'Action failed. Review the operation details.', kind === 'failed');
    } finally { this.settling = false; }
  }
  async reconcile(options: CallOptions = {}): Promise<void> {
    const attempt = this.attempt; if (this.disposed || !attempt?.operationId) return;
    const id = attempt.operationId, epoch = this.work.observations.state.cursor?.hostEpoch, confidence = this.work.observations.state.confidence;
    const outcome = await this.observe(value => this.client.query('get_operation', { operationId: id }, value), options);
    if (this.disposed || this.attempt !== attempt) return;
    if (outcome.kind === 'result' && this.work.observations.state.cursor?.hostEpoch === epoch
      && this.work.observations.state.confidence === confidence) this.work.observations.observeOperationResult(id, outcome.value);
    else this.say('Action observation is unavailable. The captured submission is retained.', true);
  }
  /** Only the modeled game/Bridge recovery intent can be derived from this row. */
  async prepareRecovery(input: OperationSnapshot | DeepReadonly<OperationSnapshot>, focusKey?: string, options: CallOptions = {}): Promise<ClientOutcome<PreparedPlan> | undefined> {
    if (this.disposed || this.transition.kind !== 'idle' || this.work.observations.state.confidence !== 'authoritative') return undefined;
    try {
      const captured = captureData(input), current = this.work.observations.state.operations.find(value => value.operationId === captured.operationId);
      if (!current || current.operationRevision !== captured.operationRevision || current.state.status !== 'recovery_required'
        || captured.state.status !== 'recovery_required' || semanticPlanKey(current.semantics) !== semanticPlanKey(captured.semantics)
        || !bindingEquivalent(current.state, captured.state)) return undefined;
      const target = captured.state.recovery.target;
      const intent: DeepReadonly<MutationIntent> | undefined = target.kind === 'game' ? { kind: 'recover_game_update', input: { recovery: target.recovery } }
        : target.kind === 'bridge' ? { kind: 'recover_bridge_update', input: { recovery: target.recovery } } : undefined;
      return intent ? this.prepare(intent, focusKey, options) : undefined;
    } catch { return undefined; }
  }
  /** The snapshot is the observed operation, never the current UI selection. */
  async cancelOperation(input: OperationSnapshot | DeepReadonly<OperationSnapshot>, options: CallOptions = {}): Promise<ClientOutcome<CancelDisposition> | undefined> {
    if (this.disposed || this.work.observations.state.confidence !== 'authoritative') return undefined;
    let captured: DeepReadonly<OperationSnapshot>, key: string;
    try {
      captured = captureData(input); key = semanticPlanKey(captured.semantics);
      const current = this.work.observations.state.operations.find(value => value.operationId === captured.operationId);
      if (!current || this.cancelling.has(captured.operationId) || current.operationRevision !== captured.operationRevision
        || semanticPlanKey(current.semantics) !== key || !bindingEquivalent(current.state, captured.state)
        || !['admitted', 'running'].includes(current.state.status)) return undefined;
    } catch { return undefined; }
    const epoch = this.work.observations.state.cursor?.hostEpoch;
    this.cancelling.add(captured.operationId); this.publish();
    try {
      const outcome = await this.observe(value => this.client.command('cancel_operation', {
        operationId: captured.operationId, expectedOperationRevision: captured.operationRevision,
      }, value), options);
      if (this.disposed) return outcome;
      const state = this.work.observations.state;
      const current = state.operations.find(value => value.operationId === captured.operationId);
      if (outcome.kind !== 'result' || state.confidence !== 'authoritative' || state.cursor?.hostEpoch !== epoch
        || !current || semanticPlanKey(current.semantics) !== key || semanticPlanKey(outcome.value.operation.semantics) !== key) {
        this.say('Cancellation outcome is unconfirmed. Refresh the captured operation.', true); return outcome;
      }
      // A later event may have arrived during the request. Keep its newer fact.
      const received = outcome.value.operation;
      if (BigInt(current.operationRevision) > BigInt(received.operationRevision)) {
        this.say('Cancellation response received. A newer operation observation is shown.'); return outcome;
      }
      if (!this.work.observations.observeOperation(received)) {
        this.say('Cancellation observation needs reconciliation. Refresh the captured operation.', true); return outcome;
      }
      this.say(outcome.value.kind === 'requested' ? 'Cancellation requested. Waiting for the operation outcome.'
        : outcome.value.kind === 'cancelled_before_commit' ? 'Action cancelled before commit.'
        : outcome.value.kind === 'too_late' ? 'The operation has passed its cancellation boundary. Its outcome is still observed.'
        : outcome.value.kind === 'recovery_required' ? 'Cancellation requires recovery. Review the captured operation.'
        : 'The operation was already complete. Its recorded outcome is shown.', outcome.value.kind === 'recovery_required');
      return outcome;
    } finally {
      this.cancelling.delete(captured.operationId); this.publish();
    }
  }
  stay(): boolean {
    const attempt = this.attempt;
    if (this.disposed || this.transition.kind !== 'review' || !attempt) return false;
    if (attempt.provedUnsent) this.releaseOwnedReplay(attempt);
    this.attempt = undefined; this.transition = { kind: 'idle' }; this.options.focus.restore(attempt.focusKey); this.say('Action review closed.'); return true;
  }
  private releaseOwnedReplay(attempt: Attempt): void {
    if (!attempt.ownsReplay || !attempt.replayCapture || !attempt.commit) return;
    const retained = this.client.getReplay(attempt.commit.idempotencyKey);
    if (retained?.request === attempt.replayCapture && canonicalData(retained.input) === canonicalData(attempt.commit)) { this.client.forgetReplay(attempt.commit.idempotencyKey); attempt.ownsReplay = false; }
  }
  dispose(): void {
    if (this.disposed) return; this.disposed = true; this.lifecycle.abort();
    if (this.attempt?.provedUnsent) this.releaseOwnedReplay(this.attempt);
    this.attempt = undefined; this.cancelling.clear(); this.recoveryAttempts.clear(); this.stop(); this.listeners.clear();
  }
}
