import { canonicalData, decodeRequest, type CallOptions, type DeepReadonly } from '../../client';
import type { BridgeFacade } from '../../state';
import type { ConfigurationEdit, DocumentSnapshot, FieldDefinition, PublicConfigValue, SupportedPlatform } from '../../generated/protocol';
import { documentMatches, draftHostDrift, draftTargetDrift, replaceField } from './presentation';
import { targetMatches } from '../home/presentation';

export interface SettingsState { readonly busy: boolean; readonly capturing: string; readonly notice: string; readonly document?: DeepReadonly<DocumentSnapshot>; }
/** Read/capture presentation controller. The shared facade alone owns draft edits and Save. */
export class SettingsController {
  private current: SettingsState = Object.freeze({ busy: false, capturing: '', notice: '' });
  private listeners = new Set<(value: SettingsState) => void>();
  private generation = 0;
  private disposed = false;
  private abort = new AbortController();
  private stop: () => void;
  private target = '';
  private needsBaselineObservation = false;
  constructor(readonly facade: BridgeFacade, readonly platform?: SupportedPlatform) {
    this.stop = facade.subscribe(state => {
      const target = canonicalData([state.work.selector ?? null, state.observations.cursor?.hostEpoch ?? null]);
      if (target !== this.target) { this.target = target; this.needsBaselineObservation = false; this.generation++; this.abort.abort(); this.abort = new AbortController(); this.set({ busy: false, capturing: '', notice: draftHostDrift(state.work, state.observations.cursor?.hostEpoch) ? 'The Bridge host changed. Your draft and changes are retained. Reconcile the current connection before editing or Save.' : '' }); }
      else if (this.current.document && !documentMatches(state.work, this.current.document)) { this.needsBaselineObservation = true; this.set({ ...this.current, document: undefined }); }
      if (this.needsBaselineObservation && state.work.draft && !state.work.dirty && !state.work.draftConflict && state.transition.kind === 'idle' && !state.work.transitionBusy && !this.current.busy) {
        this.needsBaselineObservation = false; const generation = this.generation;
        queueMicrotask(() => { if (!this.disposed && generation === this.generation) void this.refresh(); });
      }
    });
  }
  get state(): SettingsState { return this.current; }
  subscribe(listener: (value: SettingsState) => void): () => void { this.listeners.add(listener); listener(this.state); return () => this.listeners.delete(listener); }
  private set(value: SettingsState): void { this.current = Object.freeze(value); if (!this.disposed) for (const listener of this.listeners) listener(this.current); }
  private notice(message: string): void { this.set({ ...this.current, notice: message }); this.facade.announcements.announce(message); }
  get blocked(): boolean { const state = this.facade.state; return this.disposed || state.work.transitionBusy || !!state.work.baselineRefreshRequired || state.work.draftConflict
    || draftTargetDrift(state.work) || draftHostDrift(state.work, state.observations.cursor?.hostEpoch) || state.work.draft?.state === 'stale' || state.transition.kind !== 'idle' || this.facade.actions.blocksTransitions || !!this.current.capturing; }
  async refresh(options: CallOptions = {}): Promise<boolean> {
    const selector = this.facade.work.state.selector;
    if (this.disposed || this.current.busy || !selector || this.facade.work.state.transitionBusy) return false;
    const generation = this.generation, signal = this.abort.signal;
    this.set({ ...this.current, busy: true, notice: '' });
    const result = await this.facade.client.query('read_configuration', { target: selector }, { ...options, signal });
    if (this.disposed || generation !== this.generation) return false;
    if (result.kind !== 'result' || result.value.status !== 'observed') { this.set({ ...this.current, busy: false }); this.notice('Configuration observation is unavailable. Changes are retained.'); return false; }
    const document = result.value.value;
    if (!targetMatches(selector, document.binding.target) || canonicalData(document.schema.binding) !== canonicalData(document.binding.schema)) { this.set({ ...this.current, busy: false }); this.notice('Target or schema observation conflicts with this document. Changes are retained.'); return false; }
    const hostDrift = draftHostDrift(this.facade.work.state, this.facade.state.observations.cursor?.hostEpoch);
    if (hostDrift && this.facade.work.state.dirty) {
      this.set({ ...this.current, busy: false, document: undefined }); this.notice('The Bridge host changed. Your draft and changes are retained. Reconcile the current connection before editing or Save.'); return false;
    }
    if (documentMatches(this.facade.work.state, document) && !hostDrift) {
      this.set({ ...this.current, busy: false, document }); this.notice('Configuration observed. Your existing draft and staged changes are retained.'); return true;
    }
    if (!this.facade.work.state.dirty) this.facade.work.bindTarget(document.binding.target);
    // Opening does not write a virtual missing document. External changes are observed through WorkContext.
    const opened = await this.facade.openDraft(document.binding, { ...options, signal });
    if (this.disposed || generation !== this.generation) return false;
    const matches = opened.kind === 'result' && documentMatches(this.facade.work.state, document)
      && !draftHostDrift(this.facade.work.state, this.facade.state.observations.cursor?.hostEpoch);
    this.set({ ...this.current, busy: false, ...(matches ? { document } : { document: undefined }) });
    this.notice(matches ? document.preservation === 'supported' ? 'Configuration observed. Edits remain staged until Save.' : 'This document cannot be preserved safely. Save is unavailable.' : 'An external document or schema change needs reconciliation. Changes are retained.');
    return matches;
  }
  private field(id: string): DeepReadonly<FieldDefinition> | undefined {
    const draft = this.facade.work.state.draft; if (!draft || canonicalData(draft.schema.binding) !== canonicalData(draft.draft.document.schema)) return undefined;
    const fields = draft.schema.fields.filter(field => field.fieldId === id); if (fields.length !== 1) return undefined;
    const field = fields[0]; return (this.platform ? field.platforms.some(value => value === this.platform) : field.platforms.length === 2) ? field : undefined;
  }
  stageAll(edits: readonly DeepReadonly<ConfigurationEdit>[]): boolean {
    const draft = this.facade.work.state.draft;
    if (this.blocked || !draft) return false;
    try { decodeRequest(JSON.stringify({ protocolVersion: 1, requestId: '00000000-0000-4000-8000-000000000000', body: { type: 'command', command: { name: 'set_draft_changes', input: { draft: draft.draft, edits } } } })); }
    catch { this.notice('This change does not match the configuration contract. Existing edits are retained.'); return false; }
    return this.facade.stage(edits);
  }
  setPublic(id: string, value: DeepReadonly<PublicConfigValue>): boolean {
    const field = this.field(id);
    if (!field || field.sensitivity !== 'public' || field.valueType.kind !== value.kind || ['integer', 'number'].includes(value.kind)) return false;
    if (field.valueType.kind === 'enum' && value.kind === 'enum' && !field.valueType.values.includes(value.value)
      || field.valueType.kind === 'string' && value.kind === 'string' && BigInt(value.value.length) > BigInt(field.valueType.maximumLength)
      || field.valueType.kind === 'keybinding' && value.kind === 'keybinding' && (value.value.length > (field.valueType.multiple ? 8 : 1) || value.value.some(chord => field.valueType.kind !== 'keybinding' || !field.valueType.keys.includes(chord.key)))
      || field.valueType.kind === 'notification_policy' && value.kind === 'notification_policy' && value.value.kind === 'channels' && !field.valueType.sounds.includes(value.value.sound)) return false;
    const staged = this.stageAll(replaceField(this.facade.work.state.edits, id, { kind: 'set_public', fieldId: id, value }));
    if (staged) this.notice('Change staged. Save reviews the complete set of changes.'); return staged;
  }
  setNumeric(id: string, text: string): boolean {
    if (this.blocked) return false;
    const binding = this.facade.capturePublicInput(id); if (!binding || !this.facade.setPublicInput(binding, text)) return false;
    const next = this.facade.capturePublicInput(id); if (!next || (next.field.valueType.kind !== 'integer' && next.field.valueType.kind !== 'number')) return false;
    const staged = this.facade.stagePublicInput(next, text, replaceField(this.facade.work.state.edits, id, { kind: 'set_public', fieldId: id, value: { kind: next.field.valueType.kind, value: text } }));
    this.notice(staged ? 'Numeric change staged.' : 'Finish this numeric value or reset it. Save and target changes retain the unfinished input.'); return staged;
  }
  resetNumeric(id: string): boolean {
    if (this.blocked) return false;
    const buffer = this.facade.work.state.publicInputs?.find(row => row.binding.field.fieldId === id), binding = this.facade.capturePublicInput(id);
    return !!buffer && !!binding && this.facade.resetPublicInput(binding, buffer.text);
  }
  removeOverride(id: string): boolean { return !!this.field(id) && this.stageAll(replaceField(this.facade.work.state.edits, id, { kind: 'remove_override', fieldId: id })); }
  clearSecret(id: string): boolean { return this.field(id)?.sensitivity === 'secret' && this.stageAll(replaceField(this.facade.work.state.edits, id, { kind: 'clear_secret', fieldId: id })); }
  undoField(id: string): boolean { return this.stageAll(replaceField(this.facade.work.state.edits, id)); }
  /** Native entry returns opaque custody only. A late reply cannot replace newer edits. */
  async captureProtected(id: string): Promise<boolean> {
    const field = this.field(id), generation = this.generation;
    if (this.blocked || !field || field.sensitivity === 'public') return false;
    this.set({ ...this.current, capturing: id, notice: '' });
    const staged = await this.facade.captureProtectedInput(id, { signal: this.abort.signal });
    if (this.disposed || generation !== this.generation) return false;
    this.set({ ...this.current, capturing: '' });
    this.notice(staged ? 'Protected replacement staged. Values stay hidden here.' : 'Protected replacement was not staged. Existing edits are retained.'); return staged;
  }
  dispose(): void { if (this.disposed) return; this.disposed = true; this.generation++; this.abort.abort(); this.stop(); this.listeners.clear(); }
}
