import { canonicalData, captureData, type CallOptions, type DeepReadonly, type QueryOutput } from '../../client';
import type { BridgeFacade } from '../../state';
import type { ActionId, ActionProjection, ActionScope, BackupReceiptRef, BridgeApplicationBinding, ConfigurationParticipant, CreateProfileInput, DocumentBinding, ImportSourceProjection, MutationIntent, PreferredInstallationEdit, ProfileProjection, RegisterInstallationInput, RuntimeOwnership } from '../../generated/protocol';
import { available, backupMatches, observedProfiles } from './presentation';
import { targetMatches } from '../home/presentation';
import { bindingEquivalent, semanticPlanKey } from '../../client/relations';
import type { OperationSnapshot, ResolvedTarget, RuntimeAdoptInput } from '../../generated/protocol';
export interface ManagementInputs {
    readonly hostEpoch?: string;
    readonly catalogRevision?: string;
    readonly application?: DeepReadonly<BridgeApplicationBinding>;
    readonly runtime?: {
        readonly target: DeepReadonly<ResolvedTarget>;
        readonly hostEpoch: string;
        readonly value: DeepReadonly<RuntimeOwnership>;
    };
    readonly recognition?: {
        readonly hostEpoch: string;
        readonly input: DeepReadonly<RuntimeAdoptInput>;
    };
}
export interface ManagementState {
    readonly busy: boolean;
    readonly notice: string;
    readonly availability: Readonly<Record<string, DeepReadonly<ActionProjection>>>;
    readonly game?: DeepReadonly<QueryOutput<'check_game_update'>>;
    readonly runtimeRelease?: DeepReadonly<QueryOutput<'check_runtime_release'>>;
    readonly bridgeRelease?: DeepReadonly<QueryOutput<'check_bridge_update'>>;
    readonly backups?: readonly DeepReadonly<BackupReceiptRef>[];
    readonly historyComplete?: boolean;
    readonly imports?: readonly DeepReadonly<ImportSourceProjection>[];
}
const scopeKey = (scope: DeepReadonly<ActionScope>, action: ActionId) => canonicalData([scope, action]);
/** Read custody only. WorkContext and shared ActionReview own selection,
 * mutable draft, preparations, operation facts and exact commit replay. */
export class ManagementController {
    private current: ManagementState = Object.freeze({ busy: false, notice: '', availability: Object.freeze({}) });
    private generation = 0;
    private disposed = false;
    private key = '';
    private abort = new AbortController();
    private listeners = new Set<(state: ManagementState) => void>();
    private stop: () => void;
    private actionReads = new Map<string, number>();
    constructor(readonly facade: BridgeFacade, readonly inputs: ManagementInputs = {}) { this.stop = facade.subscribe(state => { const key = canonicalData([state.work.selector ?? null, state.work.binding ?? null, state.work.draft?.draft.document ?? null, state.observations.cursor?.hostEpoch ?? null, state.observations.confidence, state.observations.snapshot?.profiles ?? null, state.observations.snapshot?.installations ?? null, state.observations.snapshot?.preferences ?? null]); if (key !== this.key) {
        this.key = key;
        this.invalidate();
    } }); }
    get state(): ManagementState { return this.current; }
    get catalogRevision(): string | undefined { return this.metadataCurrent() ? this.inputs.catalogRevision : undefined; }
    get application(): DeepReadonly<BridgeApplicationBinding> | undefined { return this.metadataCurrent() ? this.inputs.application : undefined; }
    get runtime(): DeepReadonly<RuntimeOwnership> | undefined {
        const observed = this.inputs.runtime, target = this.facade.work.state.binding, epoch = this.facade.work.observations.state.cursor?.hostEpoch;
        if (!this.usable() || !observed || !target || observed.hostEpoch !== epoch || !bindingEquivalent(observed.target, target) || observed.value.kind === 'managed' && !bindingEquivalent(observed.value.reference.target, target))
            return;
        return observed.value;
    }
    private metadataCurrent(): boolean { return this.usable() && !!this.inputs.hostEpoch && this.inputs.hostEpoch === this.facade.work.observations.state.cursor?.hostEpoch; }
    subscribe(listener: (state: ManagementState) => void): () => void { this.listeners.add(listener); listener(this.current); return () => this.listeners.delete(listener); }
    private set(value: ManagementState): void { this.current = Object.freeze(value); const published = this.current; for (const listener of [...this.listeners]) {
        if (this.disposed || this.current !== published)
            break;
        listener(published);
    } }
    private invalidate(): void { this.generation++; this.abort.abort(); this.abort = new AbortController(); this.actionReads.clear(); this.set({ busy: false, notice: '', availability: Object.freeze({}) }); }
    private currentGeneration(generation: number): boolean { return !this.disposed && generation === this.generation; }
    private usable(): boolean { return !this.disposed && this.facade.work.observations.state.confidence === 'authoritative' && !this.facade.work.state.draftConflict; }
    private async observe<T>(execute: (options: CallOptions) => Promise<T>, options: CallOptions): Promise<T> {
        const lifecycle = this.abort.signal, controller = new AbortController(), abort = () => controller.abort();
        lifecycle.addEventListener('abort', abort, { once: true });
        options.signal?.addEventListener('abort', abort, { once: true });
        if (lifecycle.aborted || options.signal?.aborted)
            controller.abort();
        try {
            return await execute({ ...options, signal: controller.signal });
        }
        finally {
            lifecycle.removeEventListener('abort', abort);
            options.signal?.removeEventListener('abort', abort);
        }
    }
    private scopeCurrent(scope: DeepReadonly<ActionScope>): boolean {
        switch (scope.kind) {
            case 'catalog': return !!this.catalogRevision && scope.revision === this.catalogRevision;
            case 'application': return !!this.application && bindingEquivalent(scope.application, this.application);
            case 'target': return !!this.facade.work.state.binding && bindingEquivalent(scope.target, this.facade.work.state.binding);
            case 'document': return !!this.facade.work.state.draft && bindingEquivalent(scope.document, this.facade.work.state.draft.draft.document);
            case 'profile': return observedProfiles(this.facade.work.observations.state).some(row => row.kind === 'isolated' && bindingEquivalent(row.reference, scope.profile));
            case 'session': {
                const sessions = this.facade.work.observations.state.snapshot?.sessions;
                return sessions?.status === 'observed' && sessions.value.items.some(row => canonicalData(row.binding) === canonicalData(scope.session));
            }
        }
    }
    projection(scope: DeepReadonly<ActionScope>, action: ActionId, state: ManagementState = this.current): DeepReadonly<ActionProjection> | undefined { return state.availability[scopeKey(scope, action)]; }
    async inspectActions(scope: DeepReadonly<ActionScope>, actions: readonly ActionId[], options: CallOptions = {}): Promise<void> {
        if (!this.usable() || !this.scopeCurrent(scope) || !actions.length)
            return;
        const captured = captureData(scope), generation = this.generation;
        const reads = new Map(actions.map(action => { const key = scopeKey(captured, action), read = (this.actionReads.get(key) ?? 0) + 1; this.actionReads.set(key, read); return [key, read]; }));
        const waiting = { ...this.current.availability };
        for (const action of actions)
            delete waiting[scopeKey(captured, action)];
        this.set({ ...this.current, availability: Object.freeze(waiting) });
        if (!this.currentGeneration(generation))
            return;
        const result = await this.observe(value => this.facade.client.query('get_actions', { scope: captured, actions: [...actions] }, value), options);
        if (!this.currentGeneration(generation))
            return;
        const values = { ...this.current.availability };
        for (const action of actions) {
            const key = scopeKey(captured, action);
            if (this.actionReads.get(key) !== reads.get(key))
                continue;
            delete values[key];
            if (result.kind === 'result') {
                const matches = result.value.filter(row => row.action === action);
                if (matches.length === 1)
                    values[key] = matches[0];
            }
        }
        this.set({ ...this.current, availability: Object.freeze(values), notice: result.kind === 'result' ? '' : 'Action availability is unavailable. Refresh the captured scope.' });
    }
    async review(intent: DeepReadonly<MutationIntent> | MutationIntent, scope: DeepReadonly<ActionScope>, focusKey: string, options: CallOptions = {}): Promise<boolean> {
        if (!this.usable() || !this.scopeCurrent(scope) || !this.intentScopeMatches(intent, scope) || this.facade.work.state.dirty || this.facade.work.state.transitionBusy || !available(this.projection(scope, intent.kind)))
            return false;
        const result = await this.facade.actions.prepare(intent, focusKey, options);
        return result?.kind === 'result';
    }
    private intentScopeMatches(intent: DeepReadonly<MutationIntent>, scope: DeepReadonly<ActionScope>): boolean {
        switch (intent.kind) {
            case 'create_profile':
            case 'register_installation': return scope.kind === 'catalog' && scope.revision === intent.input.expectedCatalogRevision;
            case 'edit_ordinary_profile': return scope.kind === 'catalog' && observedProfiles(this.facade.work.observations.state).some(row => row.kind === 'ordinary' && bindingEquivalent(row.reference, intent.input.profile));
            case 'edit_isolated_profile':
            case 'archive_profile':
            case 'restore_profile':
            case 'delete_profile': return scope.kind === 'profile' && bindingEquivalent(scope.profile, intent.input.profile);
            case 'edit_installation': return scope.kind === 'target' && canonicalData(scope.target.installation) === canonicalData(intent.input.installation);
            case 'restore_configuration': return scope.kind === 'document' && bindingEquivalent(scope.document, intent.input.document);
            case 'runtime_install':
            case 'runtime_update':
            case 'runtime_repair':
            case 'runtime_adopt': return scope.kind === 'target' && bindingEquivalent(scope.target, intent.input.target);
            case 'runtime_remove':
            case 'runtime_stop_managing': return scope.kind === 'target' && bindingEquivalent(scope.target, intent.input.reference.target);
            case 'runtime_switch_source': return scope.kind === 'target' && bindingEquivalent(scope.target, intent.input.current.target);
            case 'game_update': return scope.kind === 'target' && canonicalData(scope.target.installation) === canonicalData(intent.input.checkedUpdate.installation);
            case 'bridge_update': return scope.kind === 'application' && bindingEquivalent(scope.application, intent.input.selectedRelease.current);
            default: return false;
        }
    }
    private profileObserved(profile:DeepReadonly<ProfileProjection>):boolean {
        return observedProfiles(this.facade.work.observations.state).some(row=>{
            if((row.preferredInstallation??null)!==(profile.preferredInstallation??null))return false;
            if(row.kind==='ordinary'&&profile.kind==='ordinary')return bindingEquivalent(row.reference,profile.reference);
            return row.kind==='isolated'&&profile.kind==='isolated'&&row.name===profile.name&&bindingEquivalent(row.reference,profile.reference)&&canonicalData(row.store)===canonicalData(profile.store);
        });
    }
    profileScope(profile: DeepReadonly<ProfileProjection>): DeepReadonly<ActionScope> | undefined { if (profile.kind !== 'isolated' || !this.profileObserved(profile))
        return; return { kind: 'profile', profile: profile.reference }; }
    profileIntent(profile: DeepReadonly<ProfileProjection>, action: 'edit' | 'archive' | 'restore' | 'delete', name?: string, preference: DeepReadonly<PreferredInstallationEdit> = { kind: 'keep', expected: profile.preferredInstallation ? { kind: 'registered', id: profile.preferredInstallation } : { kind: 'none' } }): DeepReadonly<MutationIntent> | undefined {
        if (!this.usable() || !this.profileObserved(profile))
            return;
        if (action === 'edit') {
            const installations = this.facade.work.observations.state.snapshot?.installations;
            if (preference.kind === 'set' && (installations?.status !== 'observed' || !installations.value.items.some(row => row.binding.kind==='registered' && bindingEquivalent(row.binding, preference.installation))))
                return;
            if (preference.kind === 'keep' && canonicalData(preference.expected) !== canonicalData(profile.preferredInstallation ? { kind: 'registered', id: profile.preferredInstallation } : { kind: 'none' }))
                return;
        }
        if (action === 'edit')
            return profile.kind === 'ordinary' ? { kind: 'edit_ordinary_profile', input: { profile: profile.reference, preferredInstallation: preference } } : { kind: 'edit_isolated_profile', input: { profile: profile.reference, preferredInstallation: preference, ...(name?.trim() ? { name: name.trim() } : {}) } };
        if (profile.kind !== 'isolated')
            return;
        if (action === 'archive' && profile.reference.state === 'active')
            return { kind: 'archive_profile', input: { profile: profile.reference } };
        if (action === 'restore' && profile.reference.state === 'archived')
            return { kind: 'restore_profile', input: { profile: profile.reference } };
        if (action === 'delete')
            return { kind: 'delete_profile', input: { profile: profile.reference, confirmation: 'delete_entire_owned_profile' } };
    }
    createIntent(input: Omit<CreateProfileInput, 'expectedCatalogRevision'>): DeepReadonly<MutationIntent> | undefined {
        const revision = this.catalogRevision;
        if (!revision)
            return;
        const installations = this.facade.work.observations.state.snapshot?.installations;
        if (installations?.status !== 'observed' || !installations.value.items.some(row => row.binding.kind === 'registered' && row.binding.registrationId === input.preferredInstallation.id && (input.preferredInstallation.revisionAssertion == null || row.binding.registrationRevision === input.preferredInstallation.revisionAssertion)))
            return;
        const setup = input.setup;
        if (setup.kind === 'windows_user_import' && !this.current.imports?.some(row => canonicalData(row.reference) === canonicalData(setup.source) && row.accessibility.status === 'observed' && row.accessibility.value))
            return;
        return { kind: 'create_profile', input: { ...input, expectedCatalogRevision: revision } };
    }
    registerIntent(input: Omit<RegisterInstallationInput, 'expectedCatalogRevision'>): DeepReadonly<MutationIntent> | undefined { const revision = this.catalogRevision; if (!revision)
        return; return { kind: 'register_installation', input: { ...input, expectedCatalogRevision: revision } }; }
    async discoverImports(ownerScope: string, options: CallOptions = {}): Promise<void> {
        if (!this.usable() || this.current.busy || !observedProfiles(this.facade.work.observations.state).some(p => p.kind === 'ordinary' && p.reference.ownerScope === ownerScope))
            return;
        const generation = this.generation;
        this.set({ ...this.current, busy: true, imports: undefined });
        if (!this.currentGeneration(generation))
            return;
        const result = await this.observe(value => this.facade.client.command('request_import_discovery', { expectedDestinationOwner: ownerScope, approval: 'request_native_approval' }, value), options);
        if (!this.currentGeneration(generation))
            return;
        const valid = result.kind === 'result' && result.value.status === 'observed' && result.value.value.items.every(row => row.reference.destinationOwner === ownerScope);
        this.set({ ...this.current, busy: false, ...(valid && result.kind === 'result' && result.value.status === 'observed' ? { imports: result.value.value.items } : {}), notice: valid ? 'Native import sources observed. Review an explicit source before creating a profile.' : 'Native import discovery is unavailable, conflicting or was cancelled.' });
    }
    async checkGame(options: CallOptions = {}): Promise<void> {
        const selector = this.facade.work.state.selector;
        if (!this.usable() || !selector || this.current.busy)
            return;
        const generation = this.generation;
        this.set({ ...this.current, busy: true, game: undefined });
        if (!this.currentGeneration(generation))
            return;
        const result = await this.observe(value => this.facade.client.query('check_game_update', { installation: selector.installation }, value), options);
        if (!this.currentGeneration(generation))
            return;
        const observation = result.kind === 'result' ? result.value : undefined;
        const binding = this.facade.work.state.binding;
        const valid = observation?.status !== 'observed' || !!binding && canonicalData(observation.value.installation) === canonicalData(binding.installation) && observation.value.hostEpoch === this.facade.work.observations.state.cursor?.hostEpoch;
        this.set({ ...this.current, busy: false, game: valid ? observation : undefined, notice: valid && observation ? '' : 'Game update observation was not confirmed for this installation.' });
    }
    gameIntent(): DeepReadonly<MutationIntent> | undefined { const value = this.current.game; if (!this.usable() || value?.status !== 'observed' || value.value.hostEpoch !== this.facade.work.observations.state.cursor?.hostEpoch || canonicalData(value.value.installation) !== canonicalData(this.facade.work.state.binding?.installation ?? null))
        return; return { kind: 'game_update', input: { checkedUpdate: value.value } }; }
    async checkRuntime(providerId: string, channelId: string, options: CallOptions = {}): Promise<void> {
        const target = this.facade.work.state.binding, selector = this.facade.work.state.selector;
        const preference = this.facade.work.observations.state.snapshot?.preferences;
        if (!this.usable() || !target || !selector || !targetMatches(selector, target) || this.current.busy || preference?.status !== 'observed' || preference.value.values.provider?.providerId !== providerId || preference.value.values.provider.channelId !== channelId)
            return;
        const generation = this.generation;
        this.set({ ...this.current, busy: true, runtimeRelease: undefined });
        if (!this.currentGeneration(generation))
            return;
        const result = await this.observe(value => this.facade.client.query('check_runtime_release', { target, providerId, channelId }, value), options);
        if (!this.currentGeneration(generation))
            return;
        const observation = result.kind === 'result' ? result.value : undefined;
        const valid = observation?.status !== 'observed' || bindingEquivalent(observation.value.target, target) && observation.value.providerId === providerId && observation.value.channelId === channelId && observation.value.hostEpoch === this.facade.work.observations.state.cursor?.hostEpoch;
        this.set({ ...this.current, busy: false, runtimeRelease: valid ? observation : undefined, notice: valid && observation ? '' : 'Runtime release observation conflicts with the requested scope.' });
    }
    runtimeIntent(kind: 'runtime_install' | 'runtime_update' | 'runtime_repair', configuration: DeepReadonly<ConfigurationParticipant>): DeepReadonly<MutationIntent> | undefined {
        const selected = this.current.runtimeRelease, target = this.facade.work.state.binding;
        const ownership = this.runtime;
        if (selected?.status !== 'observed' || !target || !ownership || !bindingEquivalent(selected.value.target, target) || selected.value.hostEpoch !== this.facade.work.observations.state.cursor?.hostEpoch || !this.configurationCurrent(configuration) || configuration.kind === 'compatible_migration' && canonicalData(configuration.destination) !== canonicalData(selected.value.configurationSchema))
            return;
        if (kind === 'runtime_install' && ownership.kind !== 'absent' || kind !== 'runtime_install' && ownership.kind !== 'managed')
            return;
        if (ownership.kind === 'managed' && (ownership.reference.binding.providerId !== selected.value.providerId || ownership.reference.binding.distributionId !== selected.value.distributionId))
            return;
        return { kind, input: { target, configuration, expectedOwnership: ownership, selectedRelease: selected.value } };
    }
    managedRuntimeIntent(kind: 'runtime_remove' | 'runtime_stop_managing'): DeepReadonly<MutationIntent> | undefined { const ownership = this.runtime; if (ownership?.kind !== 'managed')
        return; return { kind, input: { reference: ownership.reference } }; }
    private configurationCurrent(configuration: DeepReadonly<ConfigurationParticipant>): boolean {
        const draft = this.facade.work.state.draft;
        if (!draft)
            return false;
        return configuration.kind === 'save_reviewed_draft' ? bindingEquivalent(configuration.draft, draft.draft) : bindingEquivalent(configuration.document, draft.draft.document);
    }
    switchRuntimeIntent(configuration: DeepReadonly<ConfigurationParticipant>): DeepReadonly<MutationIntent> | undefined {
        const ownership = this.runtime, selected = this.current.runtimeRelease;
        const target = this.facade.work.state.binding;
        if (ownership?.kind !== 'managed' || selected?.status !== 'observed' || !target || selected.value.hostEpoch !== this.facade.work.observations.state.cursor?.hostEpoch || !bindingEquivalent(selected.value.target, target) || !this.configurationCurrent(configuration) || configuration.kind === 'compatible_migration' && canonicalData(configuration.destination) !== canonicalData(selected.value.configurationSchema) || ownership.reference.binding.providerId === selected.value.providerId && ownership.reference.binding.distributionId === selected.value.distributionId)
            return;
        return { kind: 'runtime_switch_source', input: { current: ownership.reference, selectedRelease: selected.value, configuration, confirmation: 'switch_runtime_and_configuration_source' } };
    }
    adoptionIntent(): DeepReadonly<MutationIntent> | undefined {
        const recognition = this.inputs.recognition, ownership = this.runtime, target = this.facade.work.state.binding;
        if (ownership?.kind !== 'unmanaged' || !recognition || recognition.hostEpoch !== this.facade.work.observations.state.cursor?.hostEpoch || !target || !bindingEquivalent(recognition.input.target, target) || recognition.input.observedArtifactDigest !== ownership.artifactDigest)
            return;
        return { kind: 'runtime_adopt', input: recognition.input };
    }
    async checkBridge(options: CallOptions = {}): Promise<void> {
        if (!this.application || this.current.busy)
            return;
        const application = captureData(this.application), generation = this.generation;
        this.set({ ...this.current, busy: true, bridgeRelease: undefined });
        if (!this.currentGeneration(generation))
            return;
        const result = await this.observe(value => this.facade.client.query('check_bridge_update', { application }, value), options);
        if (!this.currentGeneration(generation))
            return;
        const observation = result.kind === 'result' ? result.value : undefined, valid = observation?.status !== 'observed' || bindingEquivalent(observation.value.current, application) && observation.value.hostEpoch === this.facade.work.observations.state.cursor?.hostEpoch;
        this.set({ ...this.current, busy: false, bridgeRelease: valid ? observation : undefined, notice: valid && observation ? '' : 'Bridge package binding is unavailable or changed.' });
    }
    bridgeIntent(): DeepReadonly<MutationIntent> | undefined { const value = this.current.bridgeRelease; if (!this.application || value?.status !== 'observed' || !bindingEquivalent(value.value.current, this.application) || value.value.hostEpoch !== this.facade.work.observations.state.cursor?.hostEpoch)
        return; return { kind: 'bridge_update', input: { selectedRelease: value.value } }; }
    async history(document: DeepReadonly<DocumentBinding>, options: CallOptions = {}): Promise<void> {
        if (!this.usable() || this.current.busy || !this.scopeCurrent({ kind: 'document', document }))
            return;
        const generation = this.generation, captured = captureData(document);
        this.set({ ...this.current, busy: true, backups: undefined });
        if (!this.currentGeneration(generation))
            return;
        const result = await this.observe(value => this.facade.client.query('configuration_history', { document: captured }, value), options);
        if (!this.currentGeneration(generation))
            return;
        const value = result.kind === 'result' && result.value.status === 'observed' ? result.value.value : undefined;
        const valid = value && value.items.every(backup => backupMatches(captured, backup));
        this.set({ ...this.current, busy: false, ...(valid ? { backups: value.items, historyComplete: value.completeness === 'complete' } : {}), notice: valid ? '' : 'Backup history is unavailable for the exact document.' });
    }
    restoreIntent(document: DeepReadonly<DocumentBinding>, backup: DeepReadonly<BackupReceiptRef>): DeepReadonly<MutationIntent> | undefined { if (!this.usable() || !this.scopeCurrent({ kind: 'document', document }) || this.facade.work.state.dirty || !this.current.backups?.some(value => canonicalData(value) === canonicalData(backup)) || !backupMatches(document, backup))
        return; return { kind: 'restore_configuration', input: { document, backup } }; }
    private operationCurrent(input: DeepReadonly<OperationSnapshot>): boolean { const current = this.facade.work.observations.state.operations.find(row => row.operationId === input.operationId); return !!current && current.operationRevision === input.operationRevision && semanticPlanKey(current.semantics) === semanticPlanKey(input.semantics) && bindingEquivalent(current.state, input.state); }
    async refreshOperation(input: DeepReadonly<OperationSnapshot>, options: CallOptions = {}): Promise<void> {
        if (this.disposed || !this.operationCurrent(input))
            return;
        const captured = captureData(input), epoch = this.facade.work.observations.state.cursor?.hostEpoch;
        const result = await this.observe(value => this.facade.client.query('get_operation', { operationId: captured.operationId }, value), options);
        if (this.disposed || this.facade.work.observations.state.cursor?.hostEpoch !== epoch)
            return;
        const current = this.facade.work.observations.state.operations.find(row => row.operationId === captured.operationId);
        if (!current || semanticPlanKey(current.semantics) !== semanticPlanKey(captured.semantics))
            return;
        if (result.kind === 'result') {
            const operation = result.value.operation;
            if (operation.status === 'observed' && BigInt(operation.value.operationRevision) < BigInt(current.operationRevision))
                return;
            this.facade.work.observations.observeOperationResult(captured.operationId, result.value);
        }
        else
            this.set({ ...this.current, notice: 'The recorded operation could not be refreshed. Its captured state remains retained.' });
    }
    recoveryIntent(input: DeepReadonly<OperationSnapshot>): DeepReadonly<MutationIntent> | undefined {
        if (!this.usable() || !this.operationCurrent(input) || input.state.status !== 'recovery_required')
            return;
        const target = input.state.recovery.target;
        if (target.kind === 'game')
            return { kind: 'recover_game_update', input: { recovery: target.recovery } };
        if (target.kind === 'bridge')
            return { kind: 'recover_bridge_update', input: { recovery: target.recovery } };
    }
    async recoverOperation(operation: DeepReadonly<OperationSnapshot>, options: CallOptions = {}): Promise<void> {
        const intent = this.recoveryIntent(operation);
        if (!intent || this.facade.work.state.dirty)
            return;
        const result = await this.facade.actions.prepareRecovery(operation, `management-operation-recovery-${operation.operationId}`, options);
        if (!result)
            this.set({ ...this.current, notice: 'The original operation remains retained. Recovery review waits for the shared operation lifecycle.' });
    }
    dispose(): void { if (this.disposed)
        return; this.disposed = true; this.generation++; this.abort.abort(); this.stop(); this.listeners.clear(); }
}
