import { BridgeClient, ObservationSession, WorkContext, type CallOptions, type ClientOutcome, type CommitReplay, type DeepReadonly, type DraftReview, type BaselineReopen, type PublicInputBinding, type ObservationState, type RequestMetadata, type WorkspaceView, type WorkState, canonicalData } from '../client';
import { bindingEquivalent, semanticPlanKey } from '../client/relations';
import type { CancelDisposition, CommitInput, ConfigurationEdit, DocumentBinding, DraftSnapshot, OperationSnapshot, PreparedPlan, TargetSelector } from '../generated/protocol';
import { AnnouncementController, FocusController } from './controllers';
import { ActionReviewController } from './action-review';

export type ScreenTransition = { readonly kind: 'idle' | 'preparing' | 'admitting' | 'uncertain' | 'discard_review' | 'reopening' }
  | { readonly kind: 'review'; readonly plan: DeepReadonly<PreparedPlan> }
  | { readonly kind: 'observing'; readonly operationId: string };
export interface ScreenState { readonly work: WorkState; readonly observations: ObservationState; readonly transition: ScreenTransition; readonly notice: string; readonly reviewPurpose?: 'navigation' | 'in_place'; }
export interface FacadeOptions { readonly idempotencyKey: () => string; readonly work?: WorkContext; readonly focus?: FocusController; readonly announcements?: AnnouncementController; }
type SaveAttempt = { review: DraftReview; plan?: DeepReadonly<PreparedPlan>; commit?: DeepReadonly<CommitInput>; operationId?: string; request?: RequestMetadata;
  ownsReplay?: boolean; replayCapture?: CommitReplay['request']; provedUnsent?: boolean; priorDeliveryUncertain?: boolean };
const abandoned = (): ClientOutcome<never> => ({ kind: 'fault', fault: { code: 'observational_abort', delivery: 'not_sent' } });

/** Svelte-readable facade. WorkContext and its ObservationStore own all facts. */
export class BridgeFacade {
  readonly work: WorkContext;
  readonly focus: FocusController;
  readonly announcements: AnnouncementController;
  readonly session: ObservationSession;
  readonly actions: ActionReviewController;
  private transition: ScreenTransition = { kind: 'idle' };
  private notice = '';
  private attempt?: SaveAttempt;
  private discardReview?: DraftReview;
  private reopening?: BaselineReopen;
  private baselineTask?: Promise<boolean>;
  private settling = false;
  private disposed = false;
  private lifecycle = new AbortController();
  private listeners = new Set<(state: ScreenState) => void>();
  private publication = 0;
  private stops: (() => void)[] = [];
  constructor(readonly client: BridgeClient, private readonly options: FacadeOptions) {
    this.work = options.work ?? new WorkContext(); this.focus = options.focus ?? new FocusController();
    this.announcements = options.announcements ?? new AnnouncementController();
    this.session = new ObservationSession(client, this.work.observations);
    this.actions = new ActionReviewController(client, this.work, { idempotencyKey: options.idempotencyKey,
      canPrepare: () => !this.disposed && this.transition.kind === 'idle' && !this.work.state.transitionBusy && !this.work.state.pendingNavigation,
      announcements: this.announcements, focus: this.focus });
    this.stops.push(this.actions.subscribe(() => this.publish()));
    this.stops.push(this.work.subscribe(() => this.publish()));
    this.stops.push(this.work.observations.subscribe(() => { this.reconcileObserved(); this.publish(); }));
  }
  get state(): ScreenState { return Object.freeze({ work: this.work.state, observations: this.work.observations.state, transition: Object.freeze({ ...this.transition }), notice: this.notice, ...(this.attempt || this.discardReview ? { reviewPurpose: (this.attempt?.review ?? this.discardReview!).purpose.kind } : {}) }); }
  subscribe(listener: (state: ScreenState) => void): () => void { this.listeners.add(listener); listener(this.state); return () => { this.listeners.delete(listener); }; }
  private publish(): void {
    if (this.disposed) return;
    const publication = ++this.publication, state = this.state;
    for (const listener of [...this.listeners]) {
      if (this.disposed || publication !== this.publication) return;
      if (this.listeners.has(listener)) listener(state);
    }
  }
  private say(message: string, urgent = false): void { this.notice = message; this.announcements.announce(message, urgent ? 'assertive' : 'polite'); this.publish(); }
  private async observe<T>(execute: (options: CallOptions) => Promise<ClientOutcome<T>>, options: CallOptions): Promise<ClientOutcome<T>> {
    const controller = new AbortController(); const abort = () => controller.abort();
    this.lifecycle.signal.addEventListener('abort', abort, { once: true }); options.signal?.addEventListener('abort', abort, { once: true });
    if (this.lifecycle.signal.aborted || options.signal?.aborted) controller.abort();
    try { return await execute({ ...options, signal: controller.signal }); }
    finally { this.lifecycle.signal.removeEventListener('abort', abort); options.signal?.removeEventListener('abort', abort); }
  }
  async connect(options: CallOptions = {}) {
    const outcome = await this.observe(value => this.session.start(value), options);
    if (!this.disposed) this.announceConnection(outcome.kind === 'result');
    return outcome;
  }
  async refresh(options: CallOptions = {}) {
    const outcome = await this.observe(value => this.session.refresh(value), options);
    if (!this.disposed) this.announceConnection(outcome.kind === 'result');
    return outcome;
  }
  private announceConnection(received: boolean): void {
    const confidence = this.work.observations.state.confidence;
    this.say(received && confidence === 'authoritative' ? 'Observations updated.'
      : received && confidence === 'partial' ? 'Some observations are unavailable. Refresh is required.'
      : 'Observations are unavailable. Refresh is required.', !received || confidence !== 'authoritative');
  }
  navigate(view: WorkspaceView, focusKey?: string): void { if (!this.disposed) this.work.navigate(view, focusKey); }
  requestTarget(target: TargetSelector | DeepReadonly<TargetSelector>, focusKey?: string): boolean { return !this.disposed && !this.actions.blocksTransitions && this.work.requestTarget(target, focusKey); }
  requestClose(focusKey?: string): boolean { return !this.disposed && !this.actions.blocksTransitions && this.work.requestClose(focusKey); }
  stage(edits: readonly ConfigurationEdit[] | readonly DeepReadonly<ConfigurationEdit>[]): boolean { return !this.disposed && this.work.stage(edits); }
  cancelOperation(operation: OperationSnapshot | DeepReadonly<OperationSnapshot>, options: CallOptions = {}): Promise<ClientOutcome<CancelDisposition> | undefined> {
    return this.disposed ? Promise.resolve(undefined) : this.actions.cancelOperation(operation, options);
  }
  async captureProtectedInput(fieldId: string, options: CallOptions = {}): Promise<boolean> {
    if (this.disposed || this.actions.blocksTransitions || this.transition.kind !== 'idle') return false;
    const binding = this.work.captureProtectedInput(fieldId);
    if (!binding || binding.field.sensitivity === 'public') return false;
    const input = { draft: binding.draft, fieldId: binding.field.fieldId, sensitivity: binding.field.sensitivity };
    const outcome = await this.observe(value => this.client.command('request_sensitive_input', input, value), options);
    if (this.disposed || this.actions.blocksTransitions || this.transition.kind !== 'idle') return false;
    if (outcome.kind !== 'result' || !bindingEquivalent(input, outcome.value.binding)) {
      this.say('Protected entry could not be confirmed. Your draft is retained.', true); return false;
    }
    const captured = outcome.value.outcome;
    let edit: DeepReadonly<ConfigurationEdit>;
    if (captured.status === 'captured_private' && binding.field.sensitivity === 'private') {
      edit = { kind: 'set_private', fieldId, reference: captured.reference };
    } else if (captured.status === 'captured_secret' && binding.field.sensitivity === 'secret') {
      edit = { kind: 'replace_secret', fieldId, reference: captured.reference };
    } else {
      this.say(captured.status === 'cancelled' ? 'Protected entry cancelled.' : 'Protected entry is unavailable. Your draft is retained.'); return false;
    }
    if (!this.work.stageProtectedInput(binding, edit)) {
      this.say('The draft changed during protected entry. Capture the value again.', true); return false;
    }
    this.say('Protected value staged.'); return true;
  }
  capturePublicInput(fieldId: string): PublicInputBinding | undefined {
    return !this.disposed && !this.actions.blocksTransitions && this.transition.kind === 'idle' ? this.work.capturePublicInput(fieldId) : undefined;
  }
  setPublicInput(binding: PublicInputBinding, text: string): boolean {
    return !this.disposed && !this.actions.blocksTransitions && this.transition.kind === 'idle' && this.work.setPublicInput(binding, text);
  }
  stagePublicInput(binding: PublicInputBinding, text: string, edits: readonly ConfigurationEdit[] | readonly DeepReadonly<ConfigurationEdit>[]): boolean {
    return !this.disposed && !this.actions.blocksTransitions && this.transition.kind === 'idle' && this.work.stagePublicInput(binding, text, edits);
  }
  resetPublicInput(binding: PublicInputBinding, text: string): boolean {
    return !this.disposed && !this.actions.blocksTransitions && this.transition.kind === 'idle' && this.work.resetPublicInput(binding, text);
  }
  stay(): boolean {
    if (this.disposed || !['idle', 'review', 'discard_review'].includes(this.transition.kind)) return false;
    let inPlace = false, key = this.work.state.focusKey;
    if (this.attempt) {
      if (this.attempt.review.purpose.kind === 'in_place') key = this.attempt.review.focusKey;
      if (this.attempt.provedUnsent) this.releaseOwnedReplay(this.attempt);
      const abandonedReview = this.work.abandonReview(this.attempt.review);
      inPlace = abandonedReview && this.attempt.review.purpose.kind === 'in_place';
      this.attempt = undefined; this.transition = { kind: 'idle' };
    }
    if (this.discardReview) {
      if (this.discardReview.purpose.kind === 'in_place') key = this.discardReview.focusKey;
      inPlace = this.work.abandonReview(this.discardReview) && this.discardReview.purpose.kind === 'in_place';
      this.discardReview = undefined; this.transition = { kind: 'idle' };
    }
    const retained = this.work.stay() || inPlace;
    if (retained) { this.focus.restore(key); this.say('Changes retained.'); } return retained;
  }
  async openDraft(document: DocumentBinding | DeepReadonly<DocumentBinding>, options: CallOptions = {}): Promise<ClientOutcome<DraftSnapshot>> {
    if (this.work.state.baselineRefreshRequired) return abandoned();
    const outcome = await this.observe(value => this.client.command('open_draft', { document }, value), options);
    if (!this.disposed && outcome.kind === 'result') {
      if (this.work.observations.observeDraft(outcome.value)) this.work.observeDraft(outcome.value);
      else this.say('Draft observation needs reconciliation. Changes retained.', true);
    }
    return outcome;
  }
  /** Synchronize captured edits, then prepare review; never implicitly commit. */
  async prepareSave(options: CallOptions = {}): Promise<ClientOutcome<PreparedPlan> | undefined> {
    return this.prepareSaveFor('navigation', options);
  }
  async prepareSaveInPlace(options: CallOptions = {}, focusKey?: string): Promise<ClientOutcome<PreparedPlan> | undefined> {
    return this.prepareSaveFor('in_place', options, focusKey);
  }
  private async prepareSaveFor(purpose: 'navigation' | 'in_place', options: CallOptions, focusKey?: string): Promise<ClientOutcome<PreparedPlan> | undefined> {
    if (this.disposed || this.actions.blocksTransitions || this.transition.kind !== 'idle') return undefined;
    if (this.work.state.publicInputs?.length) { this.say('Changes retained. Resolve or reset the unfinished numeric fields before Save.', true); return undefined; }
    const review = this.work.beginReview(purpose, focusKey); if (!review) return undefined;
    if (this.disposed || !this.work.hostEpochCurrent(review.draft.draft.hostEpoch)) { this.work.abandonReview(review); return undefined; }
    const attempt: SaveAttempt = { review }; this.attempt = attempt; this.transition = { kind: 'preparing' }; this.publish();
    const acknowledgement = await this.observe(value => this.disposed || this.attempt !== attempt || !this.work.hostEpochCurrent(review.draft.draft.hostEpoch)
      ? Promise.resolve(abandoned()) : this.client.command('set_draft_changes', { draft: review.draft.draft, edits: review.edits }, value), options);
    if (this.disposed || this.attempt !== attempt) return undefined;
    const updated = this.work.finishDraftSynchronization(review, acknowledgement);
    if (updated) attempt.review = updated;
    // Synchronization publishes the new review before returning it. Disposal
    // in that callback still sees the prior token, so release only this token.
    if (this.disposed || this.attempt !== attempt) { if (updated) this.work.abandonReview(updated); return undefined; }
    if (!updated) { this.attempt = undefined; this.transition = { kind: 'idle' }; this.say('Changes retained. Draft synchronization was not confirmed.', true); return undefined; }
    if (!this.work.observations.observeDraft(updated.draft)) {
      this.work.finishSave(updated, abandoned()); this.attempt = undefined; this.transition = { kind: 'idle' }; this.say('Draft observation needs reconciliation. Changes retained.', true); return undefined;
    }
    if (updated.draft.validation.length || updated.draft.state === 'invalid') {
      this.work.finishSave(updated, abandoned()); this.attempt = undefined; this.transition = { kind: 'idle' }; this.say('Changes retained. Review the field validation.', true); return undefined;
    }
    const outcome = await this.observe(value => this.disposed || this.attempt !== attempt || !this.work.hostEpochCurrent(updated.draft.draft.hostEpoch)
      ? Promise.resolve(abandoned()) : this.client.command('prepare', { intent: { kind: 'save_configuration', input: { draft: updated.draft.draft } } }, value), options);
    if (this.disposed || this.attempt !== attempt) return outcome;
    if (!this.work.hostEpochCurrent(updated.draft.draft.hostEpoch)) {
      this.work.abandonReview(updated); this.attempt = undefined; this.transition = { kind: 'idle' };
      this.say('Changes retained. The Bridge host changed during Save review. Refresh the draft baseline.', true); return abandoned();
    }
    if (outcome.kind !== 'result') { this.work.finishSave(updated, outcome); this.attempt = undefined; this.transition = { kind: 'idle' }; this.say('Changes retained. Save review is unavailable.', true); }
    else {
      const capture = outcome.value.semantics.capture;
      if (capture.kind !== 'save_configuration' || canonicalData(capture.input.draft.edits) !== canonicalData(updated.edits)) {
        this.work.finishSave(updated, abandoned()); this.attempt = undefined; this.transition = { kind: 'idle' }; this.say('Save review does not match the captured changes.', true);
      } else { attempt.plan = outcome.value; this.transition = { kind: 'review', plan: outcome.value }; this.say('Save is ready for your review.'); }
    }
    return outcome;
  }
  /** Explicit reviewed commit. A result may be admission, never necessarily Save. */
  async commitSave(options: CallOptions = {}): Promise<ClientOutcome<OperationSnapshot> | undefined> {
    const attempt = this.attempt;
    if (this.disposed || this.transition.kind !== 'review' || !attempt?.plan) return undefined;
    if (!this.work.hostEpochCurrent(attempt.review.draft.draft.hostEpoch)) {
      this.say('Changes retained. The Bridge host changed before Save submission. Refresh the draft baseline.', true); return undefined;
    }
    // An explicit retry can retire only this attempt's proved-unsent capture.
    if (attempt.provedUnsent) this.releaseOwnedReplay(attempt);
    try { attempt.commit = Object.freeze({ idempotencyKey: this.options.idempotencyKey(), planRef: attempt.plan.planRef }); }
    catch { this.say('Changes retained. Save submission is unavailable.', true); return undefined; }
    attempt.provedUnsent = false;
    this.transition = { kind: 'admitting' }; this.publish();
    const outcome = await this.observe(value => {
      if (this.disposed || this.attempt !== attempt || !this.work.hostEpochCurrent(attempt.review.draft.draft.hostEpoch)) return Promise.resolve(abandoned());
      const prior = this.client.getReplay(attempt.commit!.idempotencyKey);
      attempt.ownsReplay = !prior; attempt.replayCapture = undefined;
      attempt.priorDeliveryUncertain = !!prior && canonicalData(prior.input) === canonicalData(attempt.commit);
      const pending = this.client.command('commit', attempt.commit!, value);
      const retained = this.client.getReplay(attempt.commit!.idempotencyKey);
      if (attempt.ownsReplay && retained && canonicalData(retained.input) === canonicalData(attempt.commit)) attempt.replayCapture = retained.request;
      return pending;
    }, options);
    if (!this.disposed && this.attempt === attempt) this.acceptCommit(attempt, outcome);
    return outcome;
  }
  async replaySave(options: CallOptions = {}): Promise<ClientOutcome<OperationSnapshot> | undefined> {
    const attempt = this.attempt;
    if (this.disposed || this.transition.kind !== 'uncertain' || !attempt?.commit) return undefined;
    this.transition = { kind: 'admitting' }; this.publish();
    const outcome = await this.observe(value => {
      const retained = this.client.getReplay(attempt.commit!.idempotencyKey);
      if (!retained || canonicalData(retained.input) !== canonicalData(attempt.commit)) {
        return Promise.resolve<ClientOutcome<OperationSnapshot>>({ kind: 'fault', fault: { code: retained ? 'replay_conflict' : 'replay_missing', delivery: 'not_sent' } });
      }
      return this.client.command('commit', attempt.commit!, value);
    }, options);
    if (!this.disposed && this.attempt === attempt) this.acceptCommit(attempt, outcome, true);
    return outcome;
  }
  private acceptCommit(attempt: SaveAttempt, outcome: ClientOutcome<OperationSnapshot>, replay = false): void {
    if (!replay && attempt.replayCapture) {
      const request = outcome.kind === 'fault' ? outcome.fault.request : outcome.request;
      if (attempt.replayCapture.request.requestId !== request?.requestId) attempt.replayCapture = undefined;
    }
    if (outcome.kind === 'fault') {
      if (!replay && !attempt.priorDeliveryUncertain && outcome.fault.delivery === 'not_sent') {
        attempt.provedUnsent = true;
        this.transition = { kind: 'review', plan: attempt.plan! };
        this.say('Save was not submitted. Changes retained.', true);
      } else {
        this.transition = { kind: 'uncertain' };
        this.say('Save outcome is unconfirmed. Changes and exact submission are retained.', true);
      }
      return;
    }
    // A domain error has no admission disposition; even retryDisposition=never
    // can describe a failure after admission. Keep the exact submission in custody.
    if (outcome.kind === 'rejected') { this.transition = { kind: 'uncertain' }; this.say('Save outcome is unconfirmed. Changes and exact submission are retained.', true); return; }
    attempt.operationId = outcome.value.operationId; attempt.request = outcome.request;
    this.transition = { kind: 'observing', operationId: attempt.operationId };
    if (!this.work.observations.observeOperation(outcome.value)) { this.transition = { kind: 'uncertain' }; this.say('Save observation needs reconciliation.', true); return; }
    this.reconcileObserved(); if (this.attempt === attempt) this.say('Save admitted. Waiting for the operation outcome.');
  }
  private reconcileObserved(): void {
    const attempt = this.attempt;
    if (this.disposed || this.settling || !attempt?.operationId || !attempt.request) return;
    const operation = this.work.observations.state.operations.find(value => value.operationId === attempt.operationId);
    if (operation?.state.status !== 'completed' || !attempt.plan || semanticPlanKey(operation.semantics) !== semanticPlanKey(attempt.plan.semantics)) return;
    this.settling = true; let reopen = false;
    try {
      const saved = this.work.finishSave(attempt.review, { kind: 'result', value: operation, request: attempt.request });
      reopen = saved && attempt.review.purpose.kind === 'in_place';
      this.releaseOwnedReplay(attempt);
      this.attempt = undefined; this.transition = { kind: 'idle' };
      this.say(saved ? 'Changes saved.' : 'Save did not apply the reviewed changes. Changes retained.', !saved);
    } finally { this.settling = false; }
    if (reopen) this.baselineTask = this.reloadBaseline();
  }
  private releaseOwnedReplay(attempt: SaveAttempt): void {
    if (!attempt.ownsReplay || !attempt.replayCapture || !attempt.commit) return;
    const retained = this.client.getReplay(attempt.commit.idempotencyKey);
    if (retained?.request === attempt.replayCapture && canonicalData(retained.input) === canonicalData(attempt.commit)) {
      this.client.forgetReplay(attempt.commit.idempotencyKey); attempt.ownsReplay = false;
    }
  }
  async reconcileSave(options: CallOptions = {}): Promise<void> {
    const attempt = this.attempt; if (this.disposed || !attempt?.operationId) return;
    const id = attempt.operationId;
    const outcome = await this.observe(value => this.client.query('get_operation', { operationId: id }, value), options);
    if (this.disposed || this.attempt !== attempt) return;
    if (outcome.kind === 'result') {
      this.work.observations.observeOperationResult(id, outcome.value);
      if (this.baselineTask) await this.baselineTask;
    }
    else this.say('Save observation is unavailable. Changes retained.', true);
  }
  prepareDiscardInPlace(focusKey?: string): boolean {
    if (this.disposed || this.actions.blocksTransitions || this.transition.kind !== 'idle') return false;
    const review = this.work.beginReview('in_place', focusKey); if (!review) return false;
    if (this.disposed || !this.work.hostEpochCurrent(review.draft.draft.hostEpoch)) { this.work.abandonReview(review); return false; }
    this.discardReview = review; this.transition = { kind: 'discard_review' }; this.say('Discard is ready for your review.'); return true;
  }
  async confirmDiscard(options: CallOptions = {}): Promise<boolean> {
    const review = this.discardReview;
    if (this.disposed || this.transition.kind !== 'discard_review' || !review || review.purpose.kind !== 'in_place') return false;
    if (!this.work.hostEpochCurrent(review.draft.draft.hostEpoch)) {
      this.say('Changes retained. The Bridge host changed before Discard. Refresh the draft baseline.', true); return false;
    }
    return this.submitDiscard(review, options);
  }
  async discard(options: CallOptions = {}): Promise<boolean> {
    if (this.disposed || this.actions.blocksTransitions || this.transition.kind !== 'idle') return false;
    const review = this.work.beginReview(); if (!review) return false;
    if (this.disposed || !this.work.hostEpochCurrent(review.draft.draft.hostEpoch)) { this.work.abandonReview(review); return false; }
    this.discardReview = review; return this.submitDiscard(review, options);
  }
  private async submitDiscard(review: DraftReview, options: CallOptions): Promise<boolean> {
    this.transition = { kind: 'preparing' }; this.publish();
    const outcome = await this.observe(value => this.disposed || this.discardReview !== review || !this.work.hostEpochCurrent(review.draft.draft.hostEpoch)
      ? Promise.resolve(abandoned()) : this.client.command('discard_draft', { draft: review.draft.draft }, value), options);
    if (this.disposed || this.discardReview !== review) return false;
    const observed = outcome.kind !== 'result' || this.work.hostEpochCurrent(review.draft.draft.hostEpoch) && this.work.observations.observeDiscard(outcome.value);
    const discarded = this.work.finishDiscard(review, observed ? outcome : abandoned());
    this.discardReview = undefined; this.transition = { kind: 'idle' }; this.say(discarded ? 'Changes discarded.' : 'Discard was not confirmed. Changes retained.', !discarded);
    if (discarded && review.purpose.kind === 'in_place') await this.reloadBaseline(options);
    return discarded;
  }
  /** Reopen only backend-observed current data under the completed review's target. */
  async reloadBaseline(options: CallOptions = {}): Promise<boolean> {
    if (this.disposed || this.actions.blocksTransitions || this.transition.kind !== 'idle') return false;
    const reopen = this.work.beginBaselineReopen(); if (!reopen) return false;
    if (this.disposed) { this.work.finishBaselineReopen(reopen, abandoned()); return false; }
    this.reopening = reopen; this.transition = { kind: 'reopening' };
    const completedNotice = reopen.reason === 'save' ? 'Changes saved.' : 'Changes discarded.';
    this.say(completedNotice + ' Reloading current settings.');
    const observed = await this.observe(value => this.disposed || this.reopening !== reopen || !this.work.hostEpochCurrent(reopen.hostEpoch)
      ? Promise.resolve(abandoned()) : this.client.query('read_configuration', { target: reopen.selector }, value), options);
    if (this.disposed || this.reopening !== reopen) return false;
    const epoch = this.work.observations.state.cursor?.hostEpoch;
    if (observed.kind !== 'result' || observed.value.status !== 'observed'
      || epoch !== undefined && epoch !== reopen.hostEpoch
      || !this.work.hostEpochCurrent(reopen.hostEpoch)
      || observed.value.value.binding.documentId !== reopen.document.documentId
      || !bindingEquivalent({ ...reopen.document, target: observed.value.value.binding.target }, reopen.document)) {
      this.work.finishBaselineReopen(reopen, abandoned()); this.reopening = undefined; this.transition = { kind: 'idle' };
      this.say(completedNotice + ' Current settings are unavailable. Refresh the baseline.', true); return false;
    }
    const document = observed.value.value.binding;
    const outcome = await this.observe(value => this.disposed || this.reopening !== reopen || !this.work.hostEpochCurrent(reopen.hostEpoch)
      ? Promise.resolve(abandoned()) : this.client.command('open_draft', { document }, value), options);
    if (this.disposed || this.reopening !== reopen) return false;
    const accepted = this.work.finishBaselineReopen(reopen, outcome);
    this.reopening = undefined; this.transition = { kind: 'idle' };
    this.say(accepted ? completedNotice : completedNotice + ' Current settings are unavailable. Refresh the baseline.', !accepted);
    return accepted;
  }
  dispose(): void {
    if (this.disposed) return; this.disposed = true; this.lifecycle.abort();
    this.actions.dispose();
    if (this.attempt) { if (this.attempt.provedUnsent) this.releaseOwnedReplay(this.attempt); this.work.finishSave(this.attempt.review, abandoned()); this.work.abandonBaselineReservation(this.attempt.review); }
    if (this.discardReview) { this.work.finishDiscard(this.discardReview, abandoned()); this.work.abandonBaselineReservation(this.discardReview); }
    if (this.reopening) this.work.finishBaselineReopen(this.reopening, abandoned());
    this.baselineTask = undefined; this.reopening = undefined; this.attempt = undefined; this.session.dispose(); for (const stop of this.stops) stop(); this.stops = []; this.listeners.clear();
  }
}
