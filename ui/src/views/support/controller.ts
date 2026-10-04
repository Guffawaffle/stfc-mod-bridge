import { canonicalData, captureData, type CallOptions, type DeepReadonly } from '../../client';
import type { BridgeFacade } from '../../state';
import type { DiagnosticPreview, ExportDestinationRef, MutationIntent } from '../../generated/protocol';
import {bindingEquivalent} from '../../client/relations';
import { targetMatches } from '../home/presentation';
export interface SupportOptions {
    readonly timeoutMs?: number;
}
export interface SupportState {
    readonly busy: boolean;
    readonly includePaths: boolean;
    readonly preview?: DeepReadonly<DiagnosticPreview>;
    readonly destination?: DeepReadonly<ExportDestinationRef>;
    readonly notice: string;
}
export class SupportController {
    private current: SupportState = Object.freeze({ busy: false, includePaths: false, notice: '' });
    private generation = 0;
    private key = '';
    private disposed = false;
    private abort = new AbortController();
    private stop: () => void;
    private listeners = new Set<(state: SupportState) => void>();
    constructor(readonly facade: BridgeFacade, readonly options: SupportOptions = {}) {
        this.stop = facade.subscribe(state => { const key = canonicalData([state.work.selector ?? null, state.work.binding ?? null, state.observations.cursor?.hostEpoch ?? null, state.observations.confidence]); if (key !== this.key) {
            this.key = key;
            this.invalidate(false);
        } });
    }
    get state(): SupportState { return this.current; }
    subscribe(listener: (state: SupportState) => void): () => void { this.listeners.add(listener); listener(this.current); return () => this.listeners.delete(listener); }
    private set(value: SupportState) { this.current = Object.freeze(value); const published = this.current; for (const listener of [...this.listeners]) {
        if (this.disposed || this.current !== published)
            break;
        listener(published);
    } }
    private invalidate(includePaths: boolean) {
        const previous = this.current.preview;
        const plan = this.facade.actions.state.plan;
        const retained = !!previous && plan?.semantics.capture.kind === 'export_diagnostics' && bindingEquivalent({preview:plan.semantics.capture.input.preview}, {preview:previous.reference}) && !this.facade.actions.stay();
        this.generation++;
        this.abort.abort();
        this.abort = new AbortController();
        this.set({ busy: false, includePaths, notice: retained ? 'The submitted export retains its original reviewed disclosure. New previews use the new choice.' : '' });
    }
    private async observe<T>(execute: (options: CallOptions) => Promise<T>, options: CallOptions): Promise<T> {
        const lifecycle = this.abort.signal, controller = new AbortController(), abort = () => controller.abort();
        lifecycle.addEventListener('abort', abort, { once: true });
        options.signal?.addEventListener('abort', abort, { once: true });
        if (lifecycle.aborted || options.signal?.aborted)
            controller.abort();
        try {
            return await execute({ ...this.options, ...options, signal: controller.signal });
        }
        finally {
            lifecycle.removeEventListener('abort', abort);
            options.signal?.removeEventListener('abort', abort);
        }
    }
    setDisclosure(includePaths: boolean): void { if (this.disposed)
        return; this.invalidate(includePaths); }
    async preview(options: CallOptions = {}): Promise<void> {
        const target = this.facade.work.state.selector;
        if (this.disposed || !target || this.current.busy || this.facade.work.observations.state.confidence !== 'authoritative')
            return;
        const generation = this.generation, input = captureData({ target, disclosure: this.current.includePaths ? 'include_paths' as const : 'redacted' as const });
        this.set({ ...this.current, busy: true, preview: undefined, destination: undefined, notice: '' });
        if (this.disposed || generation !== this.generation)
            return;
        // BridgeClient verifies the canonical content digest and request disclosure/
        // target relationship before exposing a result to any frontend.
        const result = await this.observe(value => this.facade.client.query('diagnostic_preview', input, value), options);
        if (this.disposed || generation !== this.generation)
            return;
        if (result.kind !== 'result' || !targetMatches(input.target, result.value.content.target) || !bindingEquivalent(result.value.reference.target, result.value.content.target) || result.value.reference.disclosure !== input.disclosure || result.value.content.disclosure !== input.disclosure || input.disclosure === 'redacted' && result.value.content.paths != null) {
            this.set({ ...this.current, busy: false, notice: 'Diagnostic preview was not confirmed. No export destination was selected.' });
            return;
        }
        const epoch = this.facade.work.observations.state.cursor?.hostEpoch;
        const binding = this.facade.work.state.binding;
        if (epoch && epoch !== result.value.reference.hostEpoch || binding && !bindingEquivalent(binding, result.value.reference.target)) {
            this.set({ ...this.current, busy: false, notice: 'Diagnostic scope changed. Refresh the preview.' });
            return;
        }
        this.set({ ...this.current, busy: false, preview: result.value, notice: 'Preview verified. Review the disclosure before choosing an export destination.' });
    }
    async chooseDestination(options: CallOptions = {}): Promise<void> {
        const preview = this.current.preview;
        if (this.disposed || !preview || this.current.busy || this.facade.work.observations.state.confidence !== 'authoritative')
            return;
        const generation = this.generation;
        this.set({ ...this.current, busy: true, destination: undefined });
        if (this.disposed || generation !== this.generation)
            return;
        const result = await this.observe(value => this.facade.client.command('request_export_destination', { preview: preview.reference }, value), options);
        if (this.disposed || generation !== this.generation || this.current.preview !== preview)
            return;
        if (result.kind === 'result' && bindingEquivalent(result.value.binding, {preview:preview.reference}) && result.value.outcome.status === 'captured' && result.value.outcome.destination.hostEpoch===preview.reference.hostEpoch)
            this.set({ ...this.current, busy: false, destination: result.value.outcome.destination, notice: 'Destination selected through the backend. Export still requires review and confirmation.' });
        else
            this.set({ ...this.current, busy: false, notice: result.kind === 'result' && result.value.outcome.status === 'cancelled' ? 'Destination selection cancelled.' : 'Export destination is unavailable.' });
    }
    exportIntent(): DeepReadonly<MutationIntent> | undefined { const { preview, destination } = this.current; if (!preview || !destination || this.current.busy)
        return; return { kind: 'export_diagnostics', input: { preview: preview.reference, destination } }; }
    async reviewExport(options: CallOptions = {}): Promise<boolean> { const intent = this.exportIntent(); if (this.disposed || !intent || this.facade.work.observations.state.confidence !== 'authoritative')
        return false; const generation = this.generation; const result = await this.observe(value => this.facade.actions.prepare(intent, 'support-review-export', value), options); return !this.disposed && generation === this.generation && result?.kind === 'result'; }
    dispose() { if (this.disposed)
        return; this.disposed = true; this.generation++; this.abort.abort(); this.stop(); this.listeners.clear(); }
}
