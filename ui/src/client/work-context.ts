import type { ConfigurationEdit, DiscardedDraft, DocumentBinding, DraftRef, DraftSnapshot, FieldDefinition, GetDraftResult, OperationSnapshot, ResolvedTarget, SetDraftChangesResult, TargetSelector } from '../generated/protocol';
import type { ClientOutcome } from './client';
import { canonicalData, captureData, decodeRequest, type DeepReadonly } from './wire';
import { ObservationStore } from './observation';
import { bindingEquivalent } from './relations';
import { draftAcknowledgementMatches } from './draft-acknowledgement';

export type WorkspaceView = 'home' | 'engineering' | 'settings' | 'data_sync' | 'history' | 'diagnostics' | 'preferences';
export type Navigation = { readonly kind: 'target'; readonly selector: DeepReadonly<TargetSelector> } | { readonly kind: 'close' };
/** Presentation limits; raw text is never a wire ConfigurationEdit. */
export const PUBLIC_INPUT_LIMITS = Object.freeze({ maximumBuffers: 64, maximumTextLength: 128 });
export interface PublicInputBinding {
  readonly draft: DeepReadonly<DraftRef>;
  readonly field: DeepReadonly<FieldDefinition>;
  readonly generation: number;
}
export interface ProtectedInputBinding {
  readonly draft: DeepReadonly<DraftRef>;
  readonly field: DeepReadonly<FieldDefinition>;
  readonly generation: number;
}
export interface PublicInputBuffer {
  readonly binding: PublicInputBinding;
  readonly text: string;
}
export interface WorkState {
  readonly view: WorkspaceView;
  readonly selector?: DeepReadonly<TargetSelector>;
  readonly binding?: DeepReadonly<ResolvedTarget>;
  readonly draft?: DeepReadonly<DraftSnapshot>;
  readonly edits: readonly DeepReadonly<ConfigurationEdit>[];
  readonly dirty: boolean;
  readonly draftConflict: boolean;
  readonly draftHostChanged: boolean;
  readonly pendingNavigation?: Navigation;
  readonly transitionBusy: boolean;
  readonly closeRequested: boolean;
  readonly focusKey?: string;
  readonly baselineRefreshRequired?: boolean;
  readonly publicInputs?: readonly PublicInputBuffer[];
}
export type DraftReviewPurpose =
  | { readonly kind: 'navigation'; readonly navigation: Navigation }
  | { readonly kind: 'in_place'; readonly selector: DeepReadonly<TargetSelector>; readonly target: DeepReadonly<ResolvedTarget> };
export interface BaselineReopen {
  readonly reason: 'save' | 'discard';
  readonly selector: DeepReadonly<TargetSelector>;
  readonly document: DeepReadonly<DocumentBinding>;
  readonly hostEpoch: string;
  readonly generation: number;
}
export interface DraftReview {
  readonly purpose: DraftReviewPurpose;
  readonly focusKey?: string;
  readonly draft: DeepReadonly<DraftSnapshot>;
  readonly edits: readonly DeepReadonly<ConfigurationEdit>[];
  readonly generation: number;
  readonly publicInputs: readonly PublicInputBuffer[];
}
export interface DraftReconciliation {
  readonly draft: DeepReadonly<DraftSnapshot>;
  readonly edits: readonly DeepReadonly<ConfigurationEdit>[];
  readonly publicInputs: readonly PublicInputBuffer[];
  readonly generation: number;
  readonly selector?: DeepReadonly<TargetSelector>;
  readonly binding?: DeepReadonly<ResolvedTarget>;
}

/** Shared presentation context; captured operations remain in their own store. */
export class WorkContext {
  readonly observations: ObservationStore;
  private view: WorkspaceView = 'home';
  private selector?: DeepReadonly<TargetSelector>;
  private binding?: DeepReadonly<ResolvedTarget>;
  private draft?: DeepReadonly<DraftSnapshot>;
  private edits: readonly DeepReadonly<ConfigurationEdit>[] = Object.freeze([]);
  private publicInputs: readonly PublicInputBuffer[] = Object.freeze([]);
  private pendingNavigation?: Navigation;
  private transitionBusy = false;
  private draftConflict = false;
  private closeRequested = false;
  private focusKey?: string;
  private generation = 0;
  private activeReview?: DraftReview;
  private baselineReopen?: BaselineReopen;
  private baselineReview?: DraftReview;
  private activeBaseline?: BaselineReopen;
  private invalidatedHostEpoch?: string;
  private readonly listeners = new Set<(state: WorkState) => void>();
  constructor(observations = new ObservationStore()) {
    this.observations = observations;
    let epochObservationKey = '';
    observations.subscribe(state => {
      // Hello invalidation retains the prior cursor. Remember that epoch even
      // when a later disconnect replaces the observation's current reason.
      if (state.reason === 'epoch_changed' && state.cursor) this.invalidatedHostEpoch = state.cursor.hostEpoch;
      const key = canonicalData([state.cursor?.hostEpoch ?? null, state.reason === 'epoch_changed']);
      if (key !== epochObservationKey) { epochObservationKey = key; this.publish(); }
    });
  }
  /** Known host replacement invalidates new draft work, never durable outcomes. */
  hostEpochCurrent(hostEpoch: string): boolean {
    const observed = this.observations.state;
    return observed.reason !== 'epoch_changed' && this.invalidatedHostEpoch !== hostEpoch
      && (observed.cursor === undefined || observed.cursor.hostEpoch === hostEpoch);
  }
  get state(): WorkState {
    const draftHostChanged = !!this.draft && !this.hostEpochCurrent(this.draft.draft.hostEpoch);
    return Object.freeze({ view: this.view, ...(this.selector ? { selector: this.selector } : {}), ...(this.binding ? { binding: this.binding } : {}),
      ...(this.draft ? { draft: this.draft } : {}), edits: this.edits, publicInputs: this.publicInputs, dirty: this.dirty,
      draftConflict: this.draftConflict || draftHostChanged, draftHostChanged,
      ...(this.pendingNavigation ? { pendingNavigation: this.pendingNavigation } : {}), transitionBusy: this.transitionBusy,
      closeRequested: this.closeRequested, baselineRefreshRequired: !!this.baselineReopen, ...(this.focusKey ? { focusKey: this.focusKey } : {}) });
  }
  private get dirty(): boolean { return this.publicInputs.length > 0 || !!this.draft && (this.draft.state !== 'clean' || canonicalData(this.edits) !== canonicalData(this.draft.edits)); }
  subscribe(listener: (state: WorkState) => void): () => void { this.listeners.add(listener); listener(this.state); return () => this.listeners.delete(listener); }
  private publish(): void { const state = this.state; for (const listener of this.listeners) listener(state); }
  navigate(view: WorkspaceView, focusKey?: string): void { this.view = view; this.focusKey = focusKey; this.publish(); }
  bindTarget(binding: ResolvedTarget | DeepReadonly<ResolvedTarget>): void {
    this.binding = captureData(binding);
    if (this.draft && !bindingEquivalent({ ...this.draft.draft.document, target: this.binding }, this.draft.draft.document)) this.draftConflict = true;
    this.publish();
  }
  requestTarget(selector: TargetSelector | DeepReadonly<TargetSelector>, focusKey?: string): boolean {
    return this.requestNavigation(Object.freeze({ kind: 'target', selector: captureData(selector) }), focusKey);
  }
  requestClose(focusKey?: string): boolean { return this.requestNavigation(Object.freeze({ kind: 'close' }), focusKey); }
  private requestNavigation(navigation: Navigation, focusKey?: string): boolean {
    if (this.pendingNavigation || this.transitionBusy) return false;
    this.focusKey = focusKey;
    if (this.dirty) { this.pendingNavigation = navigation; this.publish(); return false; }
    this.applyNavigation(navigation); return true;
  }
  private applyNavigation(navigation: Navigation): void {
    if (navigation.kind === 'target') { this.selector = navigation.selector; this.binding = undefined; }
    else this.closeRequested = true;
    this.draft = undefined; this.edits = Object.freeze([]); this.publicInputs = Object.freeze([]); this.draftConflict = false;
    this.pendingNavigation = undefined; this.transitionBusy = false; this.activeReview = undefined; this.baselineReopen = undefined; this.baselineReview = undefined; this.activeBaseline = undefined; this.generation++; this.publish();
  }
  stay(): boolean {
    if (!this.pendingNavigation || this.transitionBusy) return false;
    this.pendingNavigation = undefined; this.publish(); return true;
  }
  openDraft(draft: DraftSnapshot | DeepReadonly<DraftSnapshot>): boolean {
    if (this.dirty || this.transitionBusy || this.baselineReopen) return false;
    const detached = captureData(draft);
    if (!this.hostEpochCurrent(detached.draft.hostEpoch)) return false;
    this.draft = detached; this.edits = this.draft.edits; this.draftConflict = false; this.generation++; this.publish(); return true;
  }
  observeDraft(draft: DraftSnapshot | DeepReadonly<DraftSnapshot>): boolean {
    const detached = captureData(draft);
    if (!this.hostEpochCurrent(detached.draft.hostEpoch)) return false;
    if (!this.draft) return this.openDraft(detached);
    if (bindingEquivalent(this.draft, detached)) return true;
    if (this.dirty || this.transitionBusy) { this.draftConflict = true; this.publish(); return false; }
    return this.openDraft(detached);
  }
  /** Capture before connection/snapshot awaits, including unsynchronized input. */
  captureDraftReconciliation(): DraftReconciliation | undefined {
    if (!this.draft || this.baselineReopen) return undefined;
    return Object.freeze({ draft: this.draft, edits: this.edits, publicInputs: this.publicInputs, generation: this.generation,
      ...(this.selector ? { selector: this.selector } : {}), ...(this.binding ? { binding: this.binding } : {}) });
  }
  finishDraftReconciliation(capture: DraftReconciliation, outcome: ClientOutcome<GetDraftResult>): boolean {
    const retained = () => !!this.draft && this.draft.draft.hostEpoch === capture.draft.draft.hostEpoch
      && this.draft.draft.draftId === capture.draft.draft.draftId;
    const conflict = (): false => { if (retained()) { this.draftConflict = true; this.publish(); } return false; };
    if (!retained()) return false;
    try {
      if (outcome.kind !== 'result' || !this.hostEpochCurrent(capture.draft.draft.hostEpoch)) return conflict();
      const input = { hostEpoch: capture.draft.draft.hostEpoch, draftId: capture.draft.draft.draftId }, read = captureData(outcome.value);
      if (!this.observations.observeDraftResult(input, read)) return conflict();
      // Store publication can invoke callers synchronously; recapture local
      // custody after it as well as after the asynchronous query.
      if (!retained()) return false;
      if (capture.generation !== this.generation || canonicalData(this.draft) !== canonicalData(capture.draft)
        || canonicalData(this.edits) !== canonicalData(capture.edits) || canonicalData(this.publicInputs) !== canonicalData(capture.publicInputs)
        || canonicalData(this.selector ?? null) !== canonicalData(capture.selector ?? null)
        || (this.binding && capture.binding ? !bindingEquivalent(this.binding, capture.binding) : this.binding !== capture.binding)
        || !this.hostEpochCurrent(capture.draft.draft.hostEpoch)) return conflict();
      if (read.draft.status !== 'observed') return conflict();
      const draft = read.draft.value;
      const current = this.observations.state;
      if (current.confidence !== 'authoritative' || current.resnapshotRequired || !current.cursor
        || current.cursor.hostEpoch !== read.cursor.hostEpoch || current.cursor.streamId !== read.cursor.streamId
        || !current.drafts.some(observed => observed.draft.hostEpoch === draft.draft.hostEpoch && observed.draft.draftId === draft.draft.draftId
          && bindingEquivalent(observed, draft))) return conflict();
      if (!bindingEquivalent(draft.draft.document, capture.draft.draft.document)
        || !bindingEquivalent({ ...capture.draft, schema: draft.schema }, capture.draft)) return conflict();
      if (bindingEquivalent(draft, capture.draft)) {
        this.draftConflict = draft.state === 'stale'; this.publish(); return true;
      }
      // A matching public edit successor can recover a lost stage ACK. Changed
      // protected refs require its explicit transfer receipt; never infer one.
      const unsynchronized = canonicalData(capture.edits) !== canonicalData(capture.draft.edits);
      const protectedEdits = capture.edits.some(edit => edit.kind === 'set_private' || edit.kind === 'replace_secret'
        || edit.kind === 'add_sync_destination' || edit.kind === 'set_sync_proxy' && edit.value.kind === 'custom');
      const editsChanged = canonicalData(capture.edits) !== canonicalData(draft.edits);
      if (this.transitionBusy || capture.publicInputs.length || (unsynchronized || protectedEdits) && editsChanged) return conflict();
      this.draft = draft; this.edits = draft.edits; this.draftConflict = draft.state === 'stale'; this.generation++; this.publish(); return true;
    } catch { return conflict(); }
  }
  stage(edits: readonly ConfigurationEdit[] | readonly DeepReadonly<ConfigurationEdit>[]): boolean {
    if (!this.draft || this.transitionBusy || this.draftConflict || !this.hostEpochCurrent(this.draft.draft.hostEpoch)) return false;
    this.edits = captureData(edits); this.generation++; this.publish(); return true;
  }
  captureProtectedInput(fieldId: string): ProtectedInputBinding | undefined {
    if (!this.draft || this.transitionBusy || this.baselineReopen || this.pendingNavigation || this.draftConflict || !this.hostEpochCurrent(this.draft.draft.hostEpoch)) return undefined;
    const fields = this.draft.schema.fields.filter(value => value.fieldId === fieldId);
    const field = fields[0];
    if (fields.length !== 1 || !field || field.sensitivity === 'public'
      || canonicalData(this.draft.schema.binding) !== canonicalData(this.draft.draft.document.schema)) return undefined;
    return Object.freeze({ draft: this.draft.draft, field, generation: this.generation });
  }
  stageProtectedInput(binding: ProtectedInputBinding, edit: DeepReadonly<ConfigurationEdit>): boolean {
    try {
      const current = this.captureProtectedInput(binding.field.fieldId);
      if (!current || current.generation !== binding.generation || canonicalData(current) !== canonicalData(binding)) return false;
      if (!('fieldId' in edit) || edit.fieldId !== binding.field.fieldId) return false;
      if (edit.kind === 'set_private') {
        if (binding.field.sensitivity !== 'private' || edit.reference.fieldId !== edit.fieldId
          || !bindingEquivalent(edit.reference.document, binding.draft.document)
          || edit.reference.capturedFor == null || !bindingEquivalent(edit.reference.capturedFor, binding.draft)) return false;
      } else if (edit.kind === 'replace_secret') {
        if (binding.field.sensitivity !== 'secret' || edit.reference.fieldId !== edit.fieldId
          || !bindingEquivalent(edit.reference.draft, binding.draft)) return false;
      } else return false;
      const edits = captureData([...this.edits.filter(value => !('fieldId' in value) || value.fieldId !== edit.fieldId), edit]);
      this.edits = edits; this.generation++; this.publish(); return true;
    } catch { return false; }
  }
  /** Recapture immediately before an input callback; stale edit generations refuse. */
  capturePublicInput(fieldId: string): PublicInputBinding | undefined {
    if (!this.draft || this.transitionBusy || this.baselineReopen || this.draftConflict || !this.hostEpochCurrent(this.draft.draft.hostEpoch)) return undefined;
    const field = this.draft.schema.fields.find(value => value.fieldId === fieldId);
    if (!field || field.sensitivity !== 'public' || !['integer', 'number'].includes(field.valueType.kind)
      || this.draft.schema.fields.filter(value => value.fieldId === fieldId).length !== 1
      || canonicalData(this.draft.schema.binding) !== canonicalData(this.draft.draft.document.schema)) return undefined;
    return Object.freeze({ draft: this.draft.draft, field, generation: this.generation });
  }
  private matchesPublicInput(binding: PublicInputBinding): boolean {
    if (!this.draft || this.transitionBusy || this.baselineReopen || binding.generation !== this.generation) return false;
    const current = this.capturePublicInput(binding.field.fieldId);
    return !!current && bindingEquivalent(current.draft, binding.draft) && canonicalData(current.field) === canonicalData(binding.field);
  }
  /** Retain only bounded public numeric presentation text; never protected entry. */
  setPublicInput(binding: PublicInputBinding, text: string): boolean {
    try {
      const captured = captureData(binding);
      if (typeof text !== 'string' || text.length > PUBLIC_INPUT_LIMITS.maximumTextLength || !this.matchesPublicInput(captured)) return false;
      const detached = captureData(text), prior = this.publicInputs.find(value => value.binding.field.fieldId === captured.field.fieldId);
      if (!prior && this.publicInputs.length >= PUBLIC_INPUT_LIMITS.maximumBuffers) return false;
      if (prior?.text === detached) return true;
      this.generation++;
      const buffer = Object.freeze({ binding: Object.freeze({ ...captured, generation: this.generation }), text: detached });
      this.publicInputs = Object.freeze([...this.publicInputs.filter(value => value.binding.field.fieldId !== captured.field.fieldId), buffer]);
      this.publish(); return true;
    } catch { return false; }
  }
  /** Full typed edits and one buffer resolve atomically before any observer runs. */
  stagePublicInput(binding: PublicInputBinding, text: string, edits: readonly ConfigurationEdit[] | readonly DeepReadonly<ConfigurationEdit>[]): boolean {
    try {
      const captured = captureData(binding), detached = captureData(edits);
      if (!this.matchesPublicInput(captured) || !this.publicInputs.some(value => value.binding.field.fieldId === captured.field.fieldId && value.text === text)) return false;
      const matching = detached.filter(value => 'fieldId' in value && value.fieldId === captured.field.fieldId);
      if (matching.length !== 1 || matching[0].kind !== 'set_public' || matching[0].value.kind !== captured.field.valueType.kind
        || !['integer', 'number'].includes(matching[0].value.kind) || matching[0].value.value !== text) return false;
      // Generated validators establish canonical numeric strings and bounded DTO shape.
      // This frame is checked locally and never submitted or added to replay custody.
      decodeRequest(JSON.stringify({ protocolVersion: 1, requestId: '00000000-0000-4000-8000-000000000000',
        body: { type: 'command', command: { name: 'set_draft_changes', input: { draft: captured.draft, edits: detached } } } }));
      const type = captured.field.valueType;
      if (type.kind !== 'integer' && type.kind !== 'number') return false;
      if (type.minimum != null && compareNumeric(text, type.minimum) < 0 || type.maximum != null && compareNumeric(text, type.maximum) > 0) return false;
      this.edits = detached;
      this.publicInputs = Object.freeze(this.publicInputs.filter(value => value.binding.field.fieldId !== captured.field.fieldId));
      this.generation++; this.publish(); return true;
    } catch { return false; }
  }
  /** Deliberate Reset discards one exact current public buffer, never other edits. */
  resetPublicInput(binding: PublicInputBinding, text: string): boolean {
    try {
      const captured = captureData(binding);
      if (!this.matchesPublicInput(captured) || !this.publicInputs.some(value => value.binding.field.fieldId === captured.field.fieldId && value.text === text)) return false;
      this.publicInputs = Object.freeze(this.publicInputs.filter(value => value.binding.field.fieldId !== captured.field.fieldId));
      this.generation++; this.publish(); return true;
    } catch { return false; }
  }
  beginReview(kind: 'navigation' | 'in_place' = 'navigation', focusKey?: string): DraftReview | undefined {
    if (!this.draft || this.transitionBusy || this.baselineReopen || !this.hostEpochCurrent(this.draft.draft.hostEpoch)) return undefined;
    let purpose: DraftReviewPurpose;
    if (kind === 'navigation') {
      if (!this.pendingNavigation) return undefined;
      purpose = Object.freeze({ kind, navigation: this.pendingNavigation });
    } else {
      if (this.pendingNavigation || !this.selector || !this.binding
        || !bindingEquivalent({ ...this.draft.draft.document, target: this.binding }, this.draft.draft.document)) return undefined;
      purpose = Object.freeze({ kind, selector: this.selector, target: this.binding });
      this.focusKey = focusKey ?? this.focusKey;
    }
    const review = Object.freeze({ purpose, draft: this.draft, edits: this.edits, publicInputs: this.publicInputs, generation: this.generation,
      ...(this.focusKey ? { focusKey: this.focusKey } : {}) });
    this.activeReview = review; this.transitionBusy = true; this.publish(); return review;
  }
  abandonReview(review: DraftReview): boolean {
    if (!this.matchesReview(review)) return false;
    this.failedReview(); return true;
  }
  /** Adopt only the exact captured edit acknowledgement, retaining navigation custody. */
  finishDraftSynchronization(review: DraftReview, outcome: ClientOutcome<SetDraftChangesResult>): DraftReview | undefined {
    if (!this.matchesReview(review)) return undefined;
    if (!this.hostEpochCurrent(review.draft.draft.hostEpoch)) { this.failedReview(); return undefined; }
    if (this.publicInputs.length) { this.failedReview(); return undefined; }
    try {
      if (outcome.kind !== 'result') { this.failedReview(); return undefined; }
      const result = captureData(outcome.value), acknowledged = result.snapshot;
      if (!draftAcknowledgementMatches({ draft: review.draft.draft, edits: review.edits }, result)
        || !bindingEquivalent({ ...review.draft, schema: acknowledged.schema }, review.draft)) {
        this.failedReview(); return undefined;
      }
      this.draft = acknowledged; this.edits = acknowledged.edits; this.draftConflict = false; this.generation++;
      const updated = Object.freeze({ purpose: review.purpose, draft: this.draft, edits: this.edits, publicInputs: this.publicInputs, generation: this.generation, ...(review.focusKey ? { focusKey: review.focusKey } : {}) });
      this.activeReview = updated;
      this.publish(); return updated;
    } catch { this.failedReview(); return undefined; }
  }
  /** Apply only the captured review purpose after an exact completed old-draft result. */
  finishSave(review: DraftReview, outcome: ClientOutcome<OperationSnapshot>): boolean {
    if (!this.matchesReview(review)) return false;
    if (this.publicInputs.length) return this.failedReview();
    if (outcome.kind !== 'result') return this.failedReview();
    const operation = outcome.value;
    const capture = operation.semantics.capture;
    if (capture.kind !== 'save_configuration' || !bindingEquivalent(capture.input.draft.draft, review.draft.draft)
      || canonicalData(capture.input.draft.edits) !== canonicalData(review.edits)
      || operation.state.status !== 'completed' || !['changed', 'no_change'].includes(operation.state.outcome.kind)) return this.failedReview();
    if (!this.observations.observeOperation(operation)) return this.failedReview();
    if (!this.matchesReview(review)) return false;
    this.completeReview(review, 'save'); return true;
  }
  /** Discard acknowledgement is bound to the reviewed backend draft revision. */
  finishDiscard(review: DraftReview, outcome: ClientOutcome<DiscardedDraft>): boolean {
    if (!this.matchesReview(review)) return false;
    if (!this.hostEpochCurrent(review.draft.draft.hostEpoch)) return this.failedReview();
    if (outcome.kind !== 'result' || outcome.value.draftId !== review.draft.draft.draftId
      || outcome.value.hostEpoch !== review.draft.draft.hostEpoch || outcome.value.previousRevision !== review.draft.draft.revision) return this.failedReview();
    this.completeReview(review, 'discard'); return true;
  }
  private completeReview(review: DraftReview, reason: BaselineReopen['reason']): void {
    if (review.purpose.kind === 'navigation') { this.applyNavigation(review.purpose.navigation); return; }
    this.draft = undefined; this.edits = Object.freeze([]); this.publicInputs = Object.freeze([]); this.draftConflict = false; this.activeReview = undefined; this.generation++;
    this.baselineReopen = Object.freeze({ reason, selector: review.purpose.selector, document: review.draft.draft.document,
      hostEpoch: review.draft.draft.hostEpoch, generation: this.generation });
    this.baselineReview = review;
    // Completion releases operation custody; this separate owner reserves only
    // the bounded baseline observation, preventing a synchronous retarget gap.
    this.activeBaseline = undefined; this.transitionBusy = true; this.publish();
  }
  abandonBaselineReservation(review: DraftReview): boolean {
    if (!this.baselineReopen || this.baselineReview !== review || this.activeBaseline
      || this.baselineReopen.generation !== this.generation || !this.transitionBusy) return false;
    this.transitionBusy = false; this.publish(); return true;
  }
  beginBaselineReopen(): BaselineReopen | undefined {
    const reopen = this.baselineReopen;
    if (!reopen || this.activeBaseline || this.activeReview || this.draft
      || this.publicInputs.length || reopen.generation !== this.generation || canonicalData(this.selector) !== canonicalData(reopen.selector)) return undefined;
    const token = Object.freeze({ ...reopen });
    this.activeBaseline = token; this.transitionBusy = true; this.publish(); return token;
  }
  finishBaselineReopen(reopen: BaselineReopen, outcome: ClientOutcome<DraftSnapshot>): boolean {
    if (this.activeBaseline !== reopen || !this.baselineReopen || !this.transitionBusy || this.draft
      || this.publicInputs.length || reopen.generation !== this.generation || canonicalData(this.selector) !== canonicalData(reopen.selector)) return false;
    let accepted = false;
    try {
      if (outcome.kind === 'result') {
        const draft = captureData(outcome.value), epoch = this.observations.state.cursor?.hostEpoch;
        if (draft.state === 'clean' && !draft.edits.length && !draft.validation.length
          && draft.draft.hostEpoch === reopen.hostEpoch && (epoch === undefined || epoch === reopen.hostEpoch) && this.hostEpochCurrent(reopen.hostEpoch)
          && draft.draft.document.documentId === reopen.document.documentId
          && bindingEquivalent({ ...reopen.document, target: draft.draft.document.target }, reopen.document)
          && !!this.binding && bindingEquivalent({ ...reopen.document, target: this.binding }, reopen.document)
          && this.observations.observeDraft(draft)) {
          if (this.activeBaseline !== reopen || reopen.generation !== this.generation
            || !this.hostEpochCurrent(reopen.hostEpoch) || canonicalData(this.selector) !== canonicalData(reopen.selector)
            || !this.binding || !bindingEquivalent({ ...reopen.document, target: this.binding }, reopen.document)) return false;
          this.draft = draft; this.edits = draft.edits; this.draftConflict = false; this.baselineReopen = undefined; this.baselineReview = undefined; this.generation++;
          accepted = true;
        }
      }
    } finally {
      if (this.activeBaseline === reopen) { this.activeBaseline = undefined; this.transitionBusy = false; this.publish(); }
    }
    return accepted;
  }
  private matchesReview(review: DraftReview): boolean {
    const purposeMatches = review.purpose.kind === 'navigation'
      ? this.pendingNavigation === review.purpose.navigation
      : !this.pendingNavigation && canonicalData(this.selector) === canonicalData(review.purpose.selector);
    return this.transitionBusy && this.activeReview === review && purposeMatches && !!this.draft && review.generation === this.generation
      && canonicalData(review.draft) === canonicalData(this.draft) && canonicalData(review.edits) === canonicalData(this.edits)
      && canonicalData(review.publicInputs) === canonicalData(this.publicInputs);
  }
  private failedReview(): false { this.activeReview = undefined; this.transitionBusy = false; this.publish(); return false; }
}

/** Exact comparison of canonical generated numeric strings; no Number rounding. */
function compareNumeric(left: string, right: string): number {
  const parts = (value: string): [bigint, number] => {
    const negative = value.startsWith('-'), unsigned = negative ? value.slice(1) : value;
    const [whole, fraction = ''] = unsigned.split('.');
    if (unsigned.split('.').length > 2 || whole.length > 32 || fraction.length > 32) throw new Error('invalid_numeric_bound');
    return [BigInt((negative ? '-' : '') + whole + fraction), fraction.length];
  };
  const [a, aScale] = parts(left), [b, bScale] = parts(right), scale = Math.max(aScale, bScale);
  const x = a * 10n ** BigInt(scale - aScale), y = b * 10n ** BigInt(scale - bScale);
  return x < y ? -1 : x > y ? 1 : 0;
}
