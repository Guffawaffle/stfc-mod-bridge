import { BridgeClient, canonicalData, captureData, decodeReply, decodeRequest, type DeepReadonly, type QueryOutput, type CommandOutput } from '../client';
import { bindingEquivalent, semanticPlanDigest } from '../client/relations';
import type { ActionId, ActionScope, CommitInput, DiagnosticPreview, DraftSnapshot, Command, CommandResult, QueryResult, MutationIntent, OperationSnapshot, PreparedPlan, Query, Reply, Request, Snapshot, TargetSelector } from '../generated/protocol';
import { BridgeFacade } from '../state';
import type { ManagementInputs } from '../views/management';
import { ManualClock } from './clock';
export const managementModes = ['management', 'archived', 'operations', 'too_late', 'recovery', 'destination_cancelled', 'unavailable'] as const;
export type ManagementMode = typeof managementModes[number];
export interface ManagementRecord {
    readonly method: string;
    readonly request: string;
    readonly reply?: string;
}
export interface ManagementDelivery {
    readonly requests: number;
    readonly pending: number;
    readonly processing: boolean;
    readonly disposed: boolean;
    readonly lastFault?: 'unexpected_request' | 'invalid_request';
}
export interface ManagementSession {
    readonly mode: ManagementMode;
    readonly facade: BridgeFacade;
    readonly client: BridgeClient;
    readonly clock: ManualClock;
    readonly inputs: ManagementInputs;
    readonly records: readonly ManagementRecord[];
    readonly provenance: readonly {
        readonly id: string;
        readonly sha256: string;
    }[];
    readonly delivery: ManagementDelivery;
    subscribe(listener: (state: ManagementDelivery) => void): () => void;
    settle(): Promise<void>;
    dispose(): void;
}
const rawFixtures = import.meta.glob('../../../contracts/fixtures/*.json', { query: '?raw', import: 'default' });
const clone = <T>(value: T): T => JSON.parse(JSON.stringify(value));
const uid = (value: number): string => value.toString(16).padStart(8, '0') + '-3333-4333-8333-333333333333';
const equal = (left: unknown, right: unknown): boolean => canonicalData(left) === canonicalData(right);
/** Closed, bounded synthetic fixture composition for the real App. It has no native policy, filesystem writer, provider catalog or live game connection. */
export async function createManagementSession(mode: ManagementMode): Promise<ManagementSession> {
    if (!managementModes.includes(mode))
        throw new Error('management_mode');
    const provenance: {
        id: string;
        sha256: string;
    }[] = [], sources = new Map<string, DeepReadonly<Reply | Request>>();
    async function source(id: string, request = false): Promise<DeepReadonly<Reply | Request>> {
        const retained = sources.get(id);
        if (retained)
            return retained;
        const loader = rawFixtures[`../../../contracts/fixtures/${id}.json`];
        if (!loader)
            throw new Error('management_fixture');
        const bytes = await loader();
        if (typeof bytes !== 'string')
            throw new Error('management_fixture_bytes');
        const frame = request ? decodeRequest(bytes) : decodeReply(bytes), hash = await crypto.subtle.digest('SHA-256', new TextEncoder().encode(bytes));
        provenance.push({ id, sha256: [...new Uint8Array(hash)].map(byte => byte.toString(16).padStart(2, '0')).join('') });
        sources.set(id, frame);
        return frame;
    }
    async function query<N extends Query['name']>(id: string, name: N): Promise<QueryOutput<N>> {
        const value = await source(id);
        if (value.body.type !== 'result' || value.body.result.type !== 'query' || value.body.result.query.name !== name)
            throw new Error('management_query_fixture');
        return clone(value.body.result.query.output) as QueryOutput<N>;
    }
    async function command<N extends Command['name']>(id: string, name: N): Promise<CommandOutput<N>> {
        const value = await source(id);
        if (value.body.type !== 'result' || value.body.result.type !== 'command' || value.body.result.command.name !== name)
            throw new Error('management_command_fixture');
        return clone(value.body.result.command.output) as CommandOutput<N>;
    }
    async function operation(id: string): Promise<OperationSnapshot> { const value = (await query(id, 'get_operation')).operation; if (value.status !== 'observed')
        throw new Error('management_operation_fixture'); return value.value; }
    const snapshot: Snapshot = await query('sc15-complete-empty-snapshot-reply', 'snapshot');
    snapshot.installations = await query('sc-01-registered-installations-list-reply', 'list_installations');
    snapshot.profiles = await query('sc-05-catalog-with-duplicate-display-names-reply', 'list_profiles');
    const runtime = await query('sc11-check-runtime-release-reply', 'check_runtime_release'), game = await query('sc12-check-official-game-reply', 'check_game_update'), bridge = await query('sc13-check-bridge-update-reply', 'check_bridge_update');
    if (runtime.status !== 'observed' || game.status !== 'observed' || bridge.status !== 'observed' || snapshot.profiles.status !== 'observed' || snapshot.installations.status !== 'observed')
        throw new Error('management_observed_fixture');
    const runtimeValue = runtime.value, bridgeValue = bridge.value;
    const evidence = clone(runtime.evidence), target = clone(runtimeValue.target), epoch = snapshot.cursor.hostEpoch;
    if (target.installation.kind !== 'registered')
        throw new Error('management_registered_fixture');
    const installation = target.installation;
    snapshot.preferences = { status: 'observed', evidence, value: { revision: 'synthetic-preferences-1', values: { theme: 'system', motion: 'system', provider: { providerId: runtime.value.providerId, channelId: runtime.value.channelId } } } };
    snapshot.sessions = { status: 'observed', evidence: clone(evidence), value: { revision: 'synthetic-management-empty-sessions', completeness: 'complete', issues: [], items: [] } };
    const read = await query('sc09-schema-all-field-types-reply', 'read_configuration');
    if (read.status !== 'observed')
        throw new Error('management_document_fixture');
    const initialDraft = await command('sc08-open-clean-draft-reply', 'open_draft') as DraftSnapshot;
    const entries = [
        ['edit_isolated_profile', 'sc-05-edit-isolated-name-preserves-preference-prepare'], ['edit_ordinary_profile', 'sc-05-ordinary-keep-prepare'],
        ['create_profile', 'sc-05-create-new-prepare'], ['archive_profile', 'sc-07-archive-profile-prepare'], ['restore_profile', 'sc-07-restore-profile-prepare'], ['delete_profile', 'sc-07-delete-profile-prepare'],
        ['register_installation', 'sc-01-register-explicit-installation-prepare'], ['edit_installation', 'sc-01-edit-installation-name-prepare'],
        ['runtime_update', 'sc11-runtime-update'], ['runtime_repair', 'sc11-runtime-repair'], ['runtime_remove', 'sc11-runtime-remove'], ['runtime_stop_managing', 'sc11-runtime-stop-managing'],
        ['game_update', 'sc12-game-update'], ['bridge_update', 'sc13-bridge-update'], ['recover_game_update', 'sc12-recover-prior-game-image'], ['recover_bridge_update', 'sc13-recover-bridge-update'],
        ['restore_configuration', 'sc10-restore-reviewed-backup'], ['export_diagnostics', 'sc16-export-reviewed-preview']
    ] as const;
    const templates = new Map<MutationIntent['kind'], {
        intent: DeepReadonly<MutationIntent>;
        plan: PreparedPlan;
    }>();
    for (const [kind, id] of entries) {
        const input = await source(id + '-request', true);
        if (input.body.type !== 'command' || input.body.command.name !== 'prepare' || input.body.command.input.intent.kind !== kind)
            throw new Error('management_intent_fixture');
        const plan = await command(id + '-reply', 'prepare') as PreparedPlan;
        if (plan.semantics.capture.kind !== kind)
            throw new Error('management_plan_fixture');
        templates.set(kind, { intent: input.body.command.input.intent, plan });
    }
    const runtimeTemplate = templates.get('runtime_update')!;
    if (runtimeTemplate.intent.kind !== 'runtime_update')
        throw new Error('management_runtime_fixture');
    const runtimeInput = clone(runtimeTemplate.intent.input);
    if (runtimeInput.configuration.kind !== 'unchanged')
        throw new Error('management_configuration_fixture');
    const document = read.value;
    document.binding = clone(runtimeInput.configuration.document);
    initialDraft.draft.document = clone(document.binding);
    initialDraft.schema = clone(document.schema);
    const draft = clone(initialDraft);
    const history = await query('sc10-configuration-history-reply', 'configuration_history'), redacted = await query('sc16-redacted-preview-reply', 'diagnostic_preview'), paths = await query('sc16-explicit-path-disclosure-reply', 'diagnostic_preview');
    const destination = await command('sc16-capture-export-destination-reply', 'request_export_destination') as CommandOutput<'request_export_destination'>;
    const cancelledDestination = await command('sc16-destination-cancelled-reply', 'request_export_destination') as CommandOutput<'request_export_destination'>;
    const imports = await command('sc-06-native-discovery-request-native-approval-reply', 'request_import_discovery') as CommandOutput<'request_import_discovery'>;
    const requested = await command('sc15-cancel-requested-reply', 'cancel_operation') as CommandOutput<'cancel_operation'>;
    const tooLate = await command('sc15-cancel-too-late-reply', 'cancel_operation') as CommandOutput<'cancel_operation'>;
    const gameRecovery = await operation('sc12-game-rollback-required-reply'), bridgeRecovery = await operation('sc13-bridge-recovery-reviewed-current-reply'), archiveTerminal = await operation('sc-07-archive-profile-observe-reply');
    const selector: TargetSelector = { installation: { kind: 'registered', id: 'a'.repeat(32) }, profile: { kind: 'ordinary' } };
    if (mode === 'archived') {
        const row = snapshot.profiles.value.items.find(item => item.kind === 'isolated' && item.reference.id === 'b'.repeat(32));
        if (row?.kind === 'isolated')
            row.reference.state = 'archived';
    }
    if (mode === 'operations' || mode === 'too_late')
        snapshot.operations.items = [clone(tooLate.operation)];
    if (mode === 'recovery')
        snapshot.operations.items = [gameRecovery, bridgeRecovery];
    const inputs: ManagementInputs = mode === 'unavailable' ? {} : { hostEpoch: epoch, catalogRevision: 'synthetic-catalog-1', application: clone(bridge.value.current), runtime: { hostEpoch: epoch, target: clone(target), value: clone(runtimeInput.expectedOwnership) } };
    const clock = new ManualClock(), records: ManagementRecord[] = [], listeners = new Set<(state: ManagementDelivery) => void>(), operations = new Map(snapshot.operations.items.map(row => [row.operationId, clone(row)]));
    let sequence = 0, keySequence = 0, pending = 0, processing = 0, disposed = false, fault: ManagementDelivery['lastFault'], publication = 0;
    let plan: PreparedPlan | undefined, commit: CommitInput | undefined, preview: DiagnosticPreview | undefined;
    const state = (): ManagementDelivery => Object.freeze({ requests: records.length, pending, processing: processing > 0, disposed, ...(fault ? { lastFault: fault } : {}) });
    const publish = () => { const version = ++publication, value = state(); for (const listener of [...listeners]) {
        if (version !== publication)
            break;
        listener(value);
    } };
    function refuse(): never { fault = 'unexpected_request'; publish(); throw new Error('management_unexpected_request'); }
    function scopeKnown(scope: DeepReadonly<ActionScope>): boolean {
        if (scope.kind === 'target')
            return bindingEquivalent(scope.target, target);
        if (scope.kind === 'document')
            return bindingEquivalent(scope.document, document.binding);
        if (scope.kind === 'application')
            return bindingEquivalent(scope.application, bridgeValue.current) && mode !== 'unavailable';
        if (scope.kind === 'catalog')
            return scope.revision === 'synthetic-catalog-1' && mode !== 'unavailable';
        if (scope.kind === 'profile')
            return snapshot.profiles.status === 'observed' && snapshot.profiles.value.items.some(row => row.kind === 'isolated' && bindingEquivalent(row.reference, scope.profile));
        return false;
    }
    const scopeActions: Record<Exclude<ActionScope['kind'], 'session'>, readonly ActionId[]> = {
        target: ['edit_installation', 'edit_ordinary_profile', 'runtime_install', 'runtime_update', 'runtime_repair', 'runtime_remove', 'runtime_stop_managing', 'runtime_adopt', 'runtime_switch_source', 'game_update'],
        profile: ['edit_isolated_profile', 'archive_profile', 'restore_profile', 'delete_profile'], document: ['restore_configuration'], application: ['bridge_update'], catalog: ['create_profile', 'register_installation', 'edit_ordinary_profile']
    };
    async function prepare(intent: DeepReadonly<MutationIntent>): Promise<PreparedPlan> {
        const template = templates.get(intent.kind);
        if (!template)
            refuse();
        const next = clone(template.plan), capture = next.semantics.capture;
        if (intent.kind === 'edit_isolated_profile' || intent.kind === 'archive_profile' || intent.kind === 'restore_profile' || intent.kind === 'delete_profile') {
            const current = snapshot.profiles.status === 'observed' ? snapshot.profiles.value.items.find(row => row.kind === 'isolated' && bindingEquivalent(row.reference, intent.input.profile)) : undefined;
            if (current?.kind !== 'isolated')
                refuse();
            if (intent.kind === 'archive_profile' && current.reference.state !== 'active' || intent.kind === 'restore_profile' && current.reference.state !== 'archived')
                refuse();
            if (intent.kind === 'delete_profile' && intent.input.confirmation !== 'delete_entire_owned_profile')
                refuse();
            if (intent.kind === 'edit_isolated_profile') {
                if (intent.input.name !== current.name && intent.input.name !== 'Synthetic renamed profile')
                    refuse();
                const preference = intent.input.preferredInstallation;
                if (preference.kind === 'keep' && !equal(preference.expected, current.preferredInstallation ? { kind: 'registered', id: current.preferredInstallation } : { kind: 'none' }))
                    refuse();
                if (preference.kind === 'set' && !(snapshot.installations.status === 'observed' && snapshot.installations.value.items.some(row => row.binding.kind === 'registered' && bindingEquivalent(row.binding, preference.installation))))
                    refuse();
            }
            Object.assign(capture, { input: clone(intent.input) });
        }
        else if (intent.kind === 'edit_ordinary_profile') {
            const current = snapshot.profiles.status === 'observed' ? snapshot.profiles.value.items.find(row => row.kind === 'ordinary' && bindingEquivalent(row.reference, intent.input.profile)) : undefined;
            if (current?.kind !== 'ordinary')
                refuse();
            const preference = intent.input.preferredInstallation;
            if (preference.kind === 'keep' && !equal(preference.expected, current.preferredInstallation ? { kind: 'registered', id: current.preferredInstallation } : { kind: 'none' }))
                refuse();
            if (preference.kind === 'set' && !(snapshot.installations.status === 'observed' && snapshot.installations.value.items.some(row => row.binding.kind === 'registered' && bindingEquivalent(row.binding, preference.installation))))
                refuse();
            Object.assign(capture, { input: clone(intent.input) });
        }
        else if (intent.kind === 'create_profile') {
            if (template.intent.kind !== 'create_profile')
                refuse();
            const expected = clone(template.intent) as Extract<MutationIntent, {
                kind: 'create_profile';
            }>;
            expected.input.preferredInstallation = clone(intent.input.preferredInstallation);
            const selection = intent.input.preferredInstallation;
            if (selection.kind !== 'registered' || selection.id !== 'a'.repeat(32) || selection.revisionAssertion !== installation.registrationRevision || !equal(expected, intent))
                refuse();
        }
        else if (intent.kind === 'restore_configuration') {
            if (!bindingEquivalent(intent.input.document, document.binding) || history.status !== 'observed' || !history.value.items.some(backup => equal(backup, intent.input.backup)))
                refuse();
            Object.assign(capture, { input: clone(intent.input) });
        }
        else if (intent.kind === 'export_diagnostics') {
            if (!preview || !bindingEquivalent({ preview: intent.input.preview }, { preview: preview.reference }) || destination.outcome.status !== 'captured' || !equal(intent.input.destination, destination.outcome.destination))
                refuse();
            Object.assign(capture, { input: clone(intent.input) });
        }
        else if (!equal(intent, template.intent))
            refuse();
        next.planRef.planId = uid(10000 + records.length);
        next.planRef.reviewDigest = await semanticPlanDigest(next.semantics);
        plan = next;
        return clone(next);
    }
    async function respond(request: DeepReadonly<Request>): Promise<Reply['body']> {
        if (request.body.type === 'query') {
            const current = request.body.query;
            let output: unknown;
            switch (current.name) {
                case 'snapshot':
                    snapshot.operations.items = [...operations.values()].map(clone);
                    output = clone(snapshot);
                    break;
                case 'hello':
                    output = await query('sc-01-hello-windows-x64-reply', 'hello');
                    break;
                case 'get_draft':
                    if (current.input.hostEpoch !== epoch)
                        return { type: 'rejected', error: { code: 'plan_host_mismatch', retryDisposition: 'after_resnapshot', violations: [] } };
                    output = { cursor: clone(snapshot.cursor), draft: current.input.draftId === draft.draft.draftId
                        ? { status: 'observed', evidence: clone(evidence), value: clone(draft) } : { status: 'missing', evidence: clone(evidence) } };
                    break;
                case 'get_actions': {
                    if (!scopeKnown(current.input.scope) || current.input.scope.kind === 'session')
                        refuse();
                    const allowed = scopeActions[current.input.scope.kind];
                    if (current.input.actions.some(action => !allowed.includes(action)))
                        refuse();
                    output = current.input.actions.map(action => ({ action, availability: { status: 'available', revision: 'synthetic-management-availability-1', grantsPermission: false, grantsLock: false } }));
                    break;
                }
                case 'check_runtime_release':
                    if (!equal(current.input, { target, providerId: runtimeValue.providerId, channelId: runtimeValue.channelId }))
                        refuse();
                    output = runtime;
                    break;
                case 'check_game_update':
                    if (!equal(current.input.installation, selector.installation))
                        refuse();
                    output = game;
                    break;
                case 'check_bridge_update':
                    if (!bindingEquivalent(current.input.application, bridgeValue.current) || mode === 'unavailable')
                        refuse();
                    output = bridge;
                    break;
                case 'configuration_history':
                    if (!bindingEquivalent(current.input.document, document.binding))
                        refuse();
                    output = history;
                    break;
                case 'read_configuration':
                    if (!equal(current.input.target, selector))
                        refuse();
                    output = read;
                    break;
                case 'diagnostic_preview':
                    if (!equal(current.input.target, selector))
                        refuse();
                    preview = clone(current.input.disclosure === 'redacted' ? redacted : paths);
                    output = clone(preview);
                    break;
                case 'get_operation': {
                    let retained = operations.get(current.input.operationId);
                    if (!retained)
                        refuse();
                    if (commit && retained.operationId === archiveTerminal.operationId) {
                        retained = { ...clone(archiveTerminal), operationRevision: '3', semantics: clone(retained.semantics) };
                        operations.set(retained.operationId, retained);
                    }
                    output = { operation: { status: 'observed', evidence: clone(evidence), value: clone(retained) } };
                    break;
                }
                default: refuse();
            }
            return { type: 'result', result: { type: 'query', query: { name: current.name, output } as QueryResult } };
        }
        const current = request.body.command;
        let output: unknown;
        switch (current.name) {
            case 'prepare':
                output = await prepare(current.input.intent);
                break;
            case 'commit':
                if (!plan || !equal(current.input.planRef, plan.planRef) || plan.semantics.capture.kind !== 'archive_profile' || !equal(plan.semantics.capture.input.profile, archiveTerminal.semantics.capture.kind === 'archive_profile' ? archiveTerminal.semantics.capture.input.profile : null))
                    refuse();
                if (commit && !equal(commit, current.input))
                    refuse();
                commit = clone(current.input);
                const admitted = { ...clone(archiveTerminal), operationRevision: '1', semantics: clone(plan.semantics), state: { status: 'admitted' as const } };
                operations.set(admitted.operationId, admitted);
                output = admitted;
                break;
            case 'cancel_operation': {
                const retained = operations.get(current.input.operationId);
                if (!retained || retained.operationRevision !== current.input.expectedOperationRevision || !['admitted', 'running'].includes(retained.state.status) || retained.operationId !== tooLate.operation.operationId)
                    refuse();
                output = clone(mode === 'too_late' ? tooLate : requested);
                operations.set((output as CommandOutput<'cancel_operation'>).operation.operationId, clone((output as CommandOutput<'cancel_operation'>).operation));
                break;
            }
            case 'request_export_destination':
                if (!preview || !bindingEquivalent(current.input, { preview: preview.reference }))
                    refuse();
                output = { ...clone(mode === 'destination_cancelled' ? cancelledDestination : destination), binding: clone(current.input) };
                break;
            case 'request_import_discovery':
                if (!equal(current.input, { expectedDestinationOwner: 'synthetic-native-owner-1', approval: 'request_native_approval' }))
                    refuse();
                output = imports;
                break;
            case 'open_draft':
                if (!bindingEquivalent(current.input.document, initialDraft.draft.document))
                    refuse();
                output = clone(draft);
                break;
            default: refuse();
        }
        return { type: 'result', result: { type: 'command', command: { name: current.name, output } as CommandResult } };
    }
    const client = new BridgeClient({ subscribe: () => () => { }, exchange(raw, { signal }) {
            if (disposed || signal.aborted || records.length >= 256 || pending >= 64)
                return Promise.reject({ code: 'delivery_failed', delivery: 'not_sent' });
            let request: DeepReadonly<Request>;
            try {
                request = decodeRequest(raw);
            }
            catch {
                fault = 'invalid_request';
                publish();
                return Promise.reject({ code: 'delivery_failed', delivery: 'not_sent' });
            }
            const index = records.length;
            records.push({ method: request.body.type === 'query' ? request.body.query.name : request.body.command.name, request: raw });
            processing++;
            publish();
            return new Promise((resolve, reject) => {
                let done = false, scheduled = false, release = () => { };
                const abort = () => { if (done)
                    return; done = true; release(); if (scheduled) {
                    scheduled = false;
                    pending--;
                } publish(); reject({ code: 'delivery_failed', delivery: 'may_have_reached_backend' }); };
                signal.addEventListener('abort', abort, { once: true });
                void respond(request).then(body => {
                    processing--;
                    if (done || disposed)
                        return;
                    const reply = JSON.stringify({ protocolVersion: 1, requestId: request.requestId, body });
                    decodeReply(reply);
                    records[index] = { ...records[index], reply };
                    scheduled = true;
                    pending++;
                    publish();
                    release = clock.schedule(160, () => { if (done)
                        return; done = true; scheduled = false; pending--; signal.removeEventListener('abort', abort); publish(); resolve(reply); });
                }).catch(() => { processing = Math.max(0, processing - 1); fault = 'unexpected_request'; if (!done) {
                    done = true;
                    signal.removeEventListener('abort', abort);
                    reject({ code: 'delivery_failed', delivery: 'not_sent' });
                } publish(); });
            });
        } }, { clock, timeoutMs: 1400, requestId: () => uid(20000 + ++sequence) });
    const facade = new BridgeFacade(client, { idempotencyKey: () => uid(30000 + ++keySequence) });
    facade.requestTarget(selector);
    facade.work.bindTarget(target);
    facade.work.openDraft(initialDraft);
    facade.work.observations.observeDraft(initialDraft);
    facade.navigate(mode === 'operations' || mode === 'too_late' || mode === 'recovery' ? 'history' : 'engineering');
    return { mode, facade, client, clock, inputs: captureData(inputs), get records() { return captureData(records); }, get provenance() { return captureData(provenance); }, get delivery() { return state(); }, subscribe(listener) { listeners.add(listener); listener(state()); return () => listeners.delete(listener); },
        async settle() { for (let turn = 0; turn < 128 && !disposed; turn++) {
            for (let tick = 0; tick < 16; tick++)
                await Promise.resolve();
            await new Promise<void>(resolve => setTimeout(resolve, 0));
            if (processing)
                continue;
            if (!clock.pendingCount)
                return;
            if (client.pendingCount && !pending)
                continue;
            clock.runNext();
        } if (!disposed && clock.pendingCount)
            throw new Error('management_settle_limit'); },
        dispose() { if (disposed)
            return; disposed = true; facade.dispose(); client.dispose(); clock.dispose(); publish(); listeners.clear(); } };
}
