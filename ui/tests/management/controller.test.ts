import { expect, test } from 'vitest';
import { canonicalData, decodeReply } from '../../src/client';
import type { ActionId, ActionScope, MutationIntent, PreferredInstallationEdit, ProfileProjection, RuntimeOwnership } from '../../src/generated/protocol';
import { ManagementController, observedProfiles, profileIdentity } from '../../src/views/management';
import { bind, deliver, draft, epoch, exchange, fixtureIntent, frame, harness, operation, output, reply, request, script, snapshot } from './helpers';
function availability(scope: ActionScope, action: ActionId) {
    const input = request('sc-02-ordinary-ready-action-request'), result = reply('sc-02-ordinary-ready-action-reply');
    if (input.body.type !== 'query' || result.body.type !== 'result' || result.body.result.type !== 'query' || result.body.result.query.name !== 'get_actions')
        throw new Error('actions_fixture');
    input.body.query = { name: 'get_actions', input: { scope, actions: [action] } };
    result.body.result.query.output[0].action = action;
    return exchange(input, result);
}
function isolated(controller: ManagementController, index = 0) { const rows = observedProfiles(controller.facade.work.observations.state).filter(row => row.kind === 'isolated'); const row = rows[index]; if (!row || row.kind !== 'isolated')
    throw new Error('isolated_fixture'); return row; }
function runtimeSeed() { const value = fixtureIntent('sc11-runtime-update-request'); if (value.kind !== 'runtime_update')
    throw new Error('runtime_fixture'); return value.input; }
function runtimeRun(ownership: RuntimeOwnership = runtimeSeed().expectedOwnership, response = reply('sc11-check-runtime-release-reply')) {
    const run = harness(script([exchange(request('sc11-check-runtime-release-request'), response)]));
    const seed = runtimeSeed();
    const observed = snapshot();
    if (observed.installations.status !== 'observed')
        throw new Error('inventory_fixture');
    observed.preferences = { status: 'observed', evidence: observed.installations.evidence, value: { revision: 'synthetic-preferences-1', values: { theme: 'system', motion: 'system', provider: { providerId: seed.selectedRelease.providerId, channelId: seed.selectedRelease.channelId } } } };
    run.facade.work.observations.acceptSnapshot(observed);
    bind(run, seed.target);
    run.facade.work.openDraft(draft(seed.configuration.kind === 'save_reviewed_draft' ? seed.configuration.draft.document : seed.configuration.document));
    const controller = new ManagementController(run.facade, { runtime: { target: seed.target, hostEpoch: epoch(run), value: ownership } });
    return { ...run, controller, seed };
}
test('duplicate display names retain immutable profile references and expected preferences', () => {
    const run = harness(script([])), controller = new ManagementController(run.facade);
    const first = isolated(controller), second = isolated(controller, 1);
    expect(first.name).toBe(second.name);
    expect(profileIdentity(first)).not.toBe(profileIdentity(second));
    for (const row of [first, second])
        expect(controller.profileIntent(row, 'edit', 'A renamed profile')).toMatchObject({ input: { profile: row.reference, preferredInstallation: { kind: 'keep', expected: { kind: 'registered', id: row.preferredInstallation } } } });
    expect(run.facade.work.state.selector).toBeUndefined();
    expect(run.transport.state.position).toBe(0);
    controller.dispose();
    run.dispose();
});
test('catalog mutation uses explicit epoch-bound canonical revision and never inventory revision', () => {
    const run = harness(script([])), newProfile = fixtureIntent('sc-05-create-new-prepare-request');
    if (newProfile.kind !== 'create_profile')
        throw new Error('create_fixture');
    const { expectedCatalogRevision, ...input } = newProfile.input;
    const missing = new ManagementController(run.facade), valid = new ManagementController(run.facade, { hostEpoch: epoch(run), catalogRevision: expectedCatalogRevision });
    expect(missing.createIntent(input)).toBeUndefined();
    expect(valid.createIntent(input)).toEqual(newProfile);
    const inventories = run.facade.work.observations.state.snapshot?.profiles;
    expect(inventories?.status === 'observed' ? inventories.value.revision : undefined).not.toBe(expectedCatalogRevision);
    run.facade.work.observations.observeHello('000003e8-3333-4333-8333-333333333333');
    expect(valid.createIntent(input)).toBeUndefined();
    missing.dispose();
    valid.dispose();
    run.dispose();
});
test('profile preference edits refuse forged installations and stale keep assertions', () => {
    const run = harness(script([])), controller = new ManagementController(run.facade), row = isolated(controller), inventory = run.facade.work.observations.state.snapshot?.installations;
    if (inventory?.status !== 'observed')
        throw new Error('installation_fixture');
    const binding = inventory.value.items[0].binding;
    const forged: PreferredInstallationEdit = { kind: 'set', installation: binding.kind === 'registered' ? { ...binding, physicalId: 'foreign-physical' } : (() => { throw new Error('registered_fixture'); })() };
    expect(controller.profileIntent(row, 'edit', 'Rename', forged)).toBeUndefined();
    expect(controller.profileIntent(row, 'edit', 'Rename', { kind: 'keep', expected: { kind: 'none' } })).toBeUndefined();
    expect(controller.profileIntent(row, 'edit', 'Rename', { kind: 'clear' })).toMatchObject({ input: { preferredInstallation: { kind: 'clear' } } });
    controller.dispose();
    run.dispose();
});
test('profile lifecycle refuses stale references and ordinary deletion', () => {
    const run = harness(script([])), controller = new ManagementController(run.facade), row = isolated(controller), ordinary = observedProfiles(run.facade.work.observations.state).find(value => value.kind === 'ordinary');
    if (!ordinary)
        throw new Error('ordinary_fixture');
    expect(controller.profileIntent(ordinary, 'delete')).toBeUndefined();
    expect(controller.profileIntent({ ...row, reference: { ...row.reference, revision: 'stale-profile' } }, 'archive')).toBeUndefined();
    expect(controller.profileIntent(row, 'archive')).toMatchObject({ kind: 'archive_profile', input: { profile: row.reference } });
    expect(controller.profileIntent(row, 'restore')).toBeUndefined();
    expect(controller.profileIntent(row, 'delete')).toMatchObject({ input: { confirmation: 'delete_entire_owned_profile' } });
    controller.dispose();
    run.dispose();
});
test('profile review captures exact row and stops before commit', async () => {
    const expected = fixtureIntent('sc-05-edit-isolated-name-preserves-preference-prepare-request');
    if (expected.kind !== 'edit_isolated_profile')
        throw new Error('edit_fixture');
    const scope: ActionScope = { kind: 'profile', profile: expected.input.profile };
    const run = harness(script([availability(scope, 'edit_isolated_profile'), exchange(request('sc-05-edit-isolated-name-preserves-preference-prepare-request'), reply('sc-05-edit-isolated-name-preserves-preference-prepare-reply'))]));
    const controller = new ManagementController(run.facade), row = isolated(controller);
    await deliver(controller.inspectActions(scope, ['edit_isolated_profile']), run.clock);
    const intent = controller.profileIntent(row, 'edit', expected.input.name ?? undefined, expected.input.preferredInstallation)!;
    expect(await deliver(controller.review(intent, scope, 'exact-edit-button'), run.clock)).toBe(true);
    expect(run.facade.actions.state.transition.kind).toBe('review');
    expect(run.facade.actions.state.plan?.semantics.capture).toMatchObject({ kind: 'edit_isolated_profile', input: expected.input });
    expect(run.transport.state.position).toBe(2);
    expect(run.client.replayCount).toBe(0);
    controller.dispose();
    run.dispose();
});
test('availability for one observed profile cannot prepare another duplicate-name profile', async () => {
    const profiles = snapshot().profiles;
    if (profiles.status !== 'observed')
        throw new Error('profiles_fixture');
    const captured = profiles.value.items.find(row => row.kind === 'isolated');
    if (captured?.kind !== 'isolated')
        throw new Error('isolated_fixture');
    const scope: ActionScope = { kind: 'profile', profile: captured.reference }, run = harness(script([availability(scope, 'archive_profile')])), controller = new ManagementController(run.facade), first = isolated(controller), second = isolated(controller, 1);
    await deliver(controller.inspectActions(scope, ['archive_profile']), run.clock);
    expect(controller.projection(scope, 'archive_profile')?.availability.status).toBe('available');
    // A foreign scope cannot be read or used even when its display name matches.
    expect(await controller.review(controller.profileIntent(second, 'archive')!, scope, 'wrong-row')).toBe(false);
    await controller.inspectActions({ kind: 'profile', profile: { ...first.reference, revision: 'foreign-revision' } }, ['archive_profile']);
    expect(run.transport.state.position).toBe(1);
    controller.dispose();
    run.dispose();
});
test('late availability from an old target selection cannot enable review', async () => {
    const observed = snapshot();
    if (observed.profiles.status !== 'observed')
        throw new Error('profiles_fixture');
    const row = observed.profiles.value.items.find(value => value.kind === 'isolated');
    if (row?.kind !== 'isolated')
        throw new Error('isolated_fixture');
    const scope: ActionScope = { kind: 'profile', profile: row.reference }, run = harness(script([availability(scope, 'archive_profile')])), controller = new ManagementController(run.facade);
    const pending = controller.inspectActions(scope, ['archive_profile']);
    run.facade.requestTarget({ installation: { kind: 'registered', id: 'dddddddddddddddddddddddddddddddd' }, profile: { kind: 'ordinary' } });
    await deliver(pending, run.clock);
    expect(controller.projection(scope, 'archive_profile')).toBeUndefined();
    controller.dispose();
    run.dispose();
});
test('native import discovery binds destination owner and refuses foreign or inaccessible sources', async () => {
    for (const variant of ['foreign_owner', 'inaccessible'] as const) {
        const response = reply('sc-06-native-discovery-request-native-approval-reply');
        if (response.body.type !== 'result' || response.body.result.type !== 'command' || response.body.result.command.name !== 'request_import_discovery' || response.body.result.command.output.status !== 'observed')
            throw new Error('import_fixture');
        const row = response.body.result.command.output.value.items[0];
        if (variant === 'foreign_owner')
            row.reference.destinationOwner = 'foreign-owner';
        else if (row.accessibility.status === 'observed')
            row.accessibility.value = false;
        const run = harness(script([exchange(request('sc-06-native-discovery-request-native-approval-request'), response)])), controller = new ManagementController(run.facade, { hostEpoch: '000003e8-1111-4111-8111-111111111111', catalogRevision: 'synthetic-catalog-1' });
        await controller.discoverImports('unknown-owner');
        expect(run.transport.state.position).toBe(0);
        await deliver(controller.discoverImports('synthetic-native-owner-1'), run.clock);
        const create = fixtureIntent('sc-05-create-new-prepare-request');
        if (create.kind !== 'create_profile')
            throw new Error('create_fixture');
        const { expectedCatalogRevision: _, ...input } = create.input;
        expect(controller.createIntent({ ...input, setup: { kind: 'windows_user_import', source: row.reference, approval: 'request_native_approval' } })).toBeUndefined();
        if (variant === 'foreign_owner')
            expect(controller.state.imports).toBeUndefined();
        controller.dispose();
        run.dispose();
    }
});
test('history and restore stay bound to the current document and retained exact backup', async () => {
    const input = request('sc10-configuration-history-request');
    if (input.body.type !== 'query' || input.body.query.name !== 'configuration_history')
        throw new Error('history_fixture');
    const document = input.body.query.input.document;
    const run = harness(script([exchange(input, reply('sc10-configuration-history-reply'))])), controller = new ManagementController(run.facade);
    await controller.history(document);
    expect(run.transport.state.position).toBe(0);
    bind(run, document.target);
    run.facade.work.openDraft(draft(document));
    await deliver(controller.history(document), run.clock);
    const backup = controller.state.backups?.[0];
    if (!backup)
        throw new Error('backup_fixture');
    expect(controller.restoreIntent(document, backup)).toEqual({ kind: 'restore_configuration', input: { document, backup } });
    expect(controller.restoreIntent(document, { ...backup, nativeBackupRef: 'foreign-custody' })).toBeUndefined();
    expect(controller.restoreIntent({ ...document, revision: 'foreign-baseline' }, backup)).toBeUndefined();
    run.facade.stage([{ kind: 'remove_override', fieldId: 'ui_scale' }]);
    expect(run.facade.work.state.dirty).toBe(true);
    expect(controller.restoreIntent(document, backup)).toBeUndefined();
    controller.dispose();
    run.dispose();
});
test('history refuses any foreign target or producer receipt', async () => {
    for (const variant of ['target', 'provider'] as const) {
        const input = request('sc10-configuration-history-request'), response = reply('sc10-configuration-history-reply');
        if (input.body.type !== 'query' || input.body.query.name !== 'configuration_history' || response.body.type !== 'result' || response.body.result.type !== 'query' || response.body.result.query.name !== 'configuration_history' || response.body.result.query.output.status !== 'observed')
            throw new Error('history_fixture');
        const document = input.body.query.input.document, backup = response.body.result.query.output.value.items[0];
        if (variant === 'target')
            backup.document.target.installation.physicalId = 'foreign-physical';
        else
            backup.document.schema.providerId = 'other-producer';
        decodeReply(JSON.stringify(response));
        const run = harness(script([exchange(input, response)]));
        bind(run, document.target);
        run.facade.work.openDraft(draft(document));
        const controller = new ManagementController(run.facade);
        await deliver(controller.history(document), run.clock);
        expect(controller.state.backups).toBeUndefined();
        controller.dispose();
        run.dispose();
    }
});
test('runtime release and configuration use exact observed target ownership', async () => {
    const run = runtimeRun();
    await deliver(run.controller.checkRuntime(run.seed.selectedRelease.providerId, run.seed.selectedRelease.channelId), run.clock);
    expect(run.controller.runtimeIntent('runtime_update', run.seed.configuration)).toEqual({ kind: 'runtime_update', input: run.seed });
    expect(run.controller.runtimeIntent('runtime_install', run.seed.configuration)).toBeUndefined();
    if (run.seed.configuration.kind === 'save_reviewed_draft')
        throw new Error('document_fixture');
    expect(run.controller.runtimeIntent('runtime_repair', { kind: 'unchanged', document: { ...run.seed.configuration.document, revision: 'foreign-document' } })).toBeUndefined();
    expect(run.controller.managedRuntimeIntent('runtime_remove')).toMatchObject({ kind: 'runtime_remove' });
    run.facade.requestTarget({ installation: { kind: 'registered', id: 'dddddddddddddddddddddddddddddddd' }, profile: { kind: 'ordinary' } });
    expect(run.controller.runtime).toBeUndefined();
    expect(run.controller.managedRuntimeIntent('runtime_remove')).toBeUndefined();
    run.controller.dispose();
    run.dispose();
});
test('runtime ownership from another target or host cannot enable managed operations', () => {
    for (const variant of ['target', 'epoch'] as const) {
        const run = runtimeRun(), controller = new ManagementController(run.facade, { runtime: { target: variant === 'target' ? { ...run.seed.target, installation: { ...run.seed.target.installation, physicalId: 'foreign-physical' } } : run.seed.target, hostEpoch: variant === 'epoch' ? '000003e8-3333-4333-8333-333333333333' : epoch(run), value: run.seed.expectedOwnership } });
        expect(controller.runtime).toBeUndefined();
        expect(controller.managedRuntimeIntent('runtime_stop_managing')).toBeUndefined();
        controller.dispose();
        run.controller.dispose();
        run.dispose();
    }
});
test('a different checked runtime distribution requires explicit source-switch capture and cannot use update or repair', async () => {
    const response = reply('sc11-check-runtime-release-reply');
    if (response.body.type !== 'result' || response.body.result.type !== 'query' || response.body.result.query.name !== 'check_runtime_release' || response.body.result.query.output.status !== 'observed')
        throw new Error('runtime_fixture');
    response.body.result.query.output.value.distributionId = 'other.synthetic-distribution';
    const run = runtimeRun(runtimeSeed().expectedOwnership, response);
    await deliver(run.controller.checkRuntime(run.seed.selectedRelease.providerId, run.seed.selectedRelease.channelId), run.clock);
    expect(run.controller.runtimeIntent('runtime_update', run.seed.configuration)).toBeUndefined();
    expect(run.controller.runtimeIntent('runtime_repair', run.seed.configuration)).toBeUndefined();
    expect(run.controller.switchRuntimeIntent(run.seed.configuration)).toMatchObject({ kind: 'runtime_switch_source', input: { current: run.seed.expectedOwnership.kind === 'managed' ? run.seed.expectedOwnership.reference : undefined, configuration: run.seed.configuration, selectedRelease: response.body.result.query.output.value, confirmation: 'switch_runtime_and_configuration_source' } });
    run.controller.dispose();
    run.dispose();
});
test('runtime migration cannot substitute a schema that differs from the checked release', async () => {
    const run = runtimeRun();
    await deliver(run.controller.checkRuntime(run.seed.selectedRelease.providerId, run.seed.selectedRelease.channelId), run.clock);
    if (run.seed.configuration.kind === 'save_reviewed_draft')
        throw new Error('document_fixture');
    expect(run.controller.runtimeIntent('runtime_update', { kind: 'compatible_migration', document: run.seed.configuration.document, destination: { ...run.seed.selectedRelease.configurationSchema, digest: 'sha256:' + '1'.repeat(64) } })).toBeUndefined();
    run.controller.dispose();
    run.dispose();
});
test('game check from a foreign installation or host never becomes an update intent', async () => {
    for (const variant of ['installation', 'epoch'] as const) {
        const response = reply('sc12-check-official-game-reply');
        if (response.body.type !== 'result' || response.body.result.type !== 'query' || response.body.result.query.name !== 'check_game_update' || response.body.result.query.output.status !== 'observed')
            throw new Error('game_fixture');
        if (variant === 'installation')
            response.body.result.query.output.value.installation.physicalId = 'foreign-physical';
        else
            response.body.result.query.output.value.hostEpoch = '000003e8-3333-4333-8333-333333333333';
        const run = harness(script([exchange(request('sc12-check-official-game-request'), response)]));
        bind(run, runtimeSeed().target);
        const controller = new ManagementController(run.facade);
        await deliver(controller.checkGame(), run.clock);
        expect(controller.gameIntent()).toBeUndefined();
        expect(controller.state.game).toBeUndefined();
        controller.dispose();
        run.dispose();
    }
});
test('Bridge checks require explicit current application and reject a foreign checked host', async () => {
    const input = request('sc13-check-bridge-update-request'), response = reply('sc13-check-bridge-update-reply');
    if (input.body.type !== 'query' || input.body.query.name !== 'check_bridge_update' || response.body.type !== 'result' || response.body.result.type !== 'query' || response.body.result.query.name !== 'check_bridge_update' || response.body.result.query.output.status !== 'observed')
        throw new Error('bridge_fixture');
    response.body.result.query.output.value.hostEpoch = '000003e8-3333-4333-8333-333333333333';
    const run = harness(script([exchange(input, response)])), missing = new ManagementController(run.facade), controller = new ManagementController(run.facade, { hostEpoch: epoch(run), application: input.body.query.input.application });
    await missing.checkBridge();
    expect(run.transport.state.position).toBe(0);
    await deliver(controller.checkBridge(), run.clock);
    expect(controller.bridgeIntent()).toBeUndefined();
    expect(controller.state.bridgeRelease).toBeUndefined();
    missing.dispose();
    controller.dispose();
    run.dispose();
});
test('caller observation abort is retained rather than replaced by controller lifecycle', async () => {
    const run = harness(script([])), controller = new ManagementController(run.facade), row = isolated(controller), scope: ActionScope = { kind: 'profile', profile: row.reference };
    const abort = new AbortController();
    abort.abort();
    await controller.inspectActions(scope, ['archive_profile'], { signal: abort.signal });
    expect(run.transport.state.position).toBe(0);
    expect(controller.projection(scope, 'archive_profile')).toBeUndefined();
    controller.dispose();
    run.dispose();
});
test('superseded read publication cannot issue work after a synchronous target change', async () => {
    const run = runtimeRun();
    let changed = false;
    const states: boolean[] = [];
    const stopFirst = run.controller.subscribe(state => { if (state.busy && !changed) {
        changed = true;
        run.facade.requestTarget({ installation: { kind: 'registered', id: 'dddddddddddddddddddddddddddddddd' }, profile: { kind: 'ordinary' } });
    } }), stopSecond = run.controller.subscribe(state => states.push(state.busy));
    await run.controller.checkRuntime(run.seed.selectedRelease.providerId, run.seed.selectedRelease.channelId);
    expect(run.transport.state.position).toBe(0);
    expect(states).not.toContain(true);
    stopFirst();
    stopSecond();
    run.controller.dispose();
    run.dispose();
});
test('operation refresh observes the exact row and retains a newer event instead of regressing it', async () => {
    const response = reply('sc15-running-reply');
    if (response.body.type !== 'result' || response.body.result.type !== 'query' || response.body.result.query.name !== 'get_operation' || response.body.result.query.output.operation.status !== 'observed')
        throw new Error('operation_fixture');
    const current = response.body.result.query.output.operation.value, input = request('sc15-running-request'), run = harness(script([exchange(input, response)])), controller = new ManagementController(run.facade);
    run.facade.work.observations.observeOperation({ ...current, operationRevision: '1', state: { status: 'admitted' } });
    const row = run.facade.work.observations.state.operations[0], pending = controller.refreshOperation(row);
    run.facade.work.observations.observeOperation({ ...current, operationRevision: '4' });
    await deliver(pending, run.clock);
    expect(run.facade.work.observations.state.confidence).toBe('authoritative');
    expect(run.facade.work.observations.state.operations[0].operationRevision).toBe('4');
    controller.dispose();
    run.dispose();
});
test('unsupported retained recovery has no invented generic command', async () => {
    const run = harness(script([])), controller = new ManagementController(run.facade), recorded = operation('sc15-post-restart-recovery-reply');
    expect(run.facade.work.observations.observeOperation(recorded)).toBe(true);
    expect(controller.recoveryIntent(recorded)).toBeUndefined();
    await controller.recoverOperation(recorded);
    expect(run.transport.state.position).toBe(0);
    expect(run.facade.work.observations.state.operations[0]).toEqual(recorded);
    controller.dispose();
    run.dispose();
});
test('Bridge recovery review derives the exact recorded journal, independent of selected target', async () => {
    const recorded = operation('sc13-bridge-recovery-reviewed-current-reply'), run = harness(script([exchange(request('sc13-recover-bridge-update-request'), reply('sc13-recover-bridge-update-reply'))])), controller = new ManagementController(run.facade);
    run.facade.work.observations.observeOperation(recorded);
    run.facade.requestTarget({ installation: { kind: 'registered', id: 'dddddddddddddddddddddddddddddddd' }, profile: { kind: 'ordinary' } });
    const intent = controller.recoveryIntent(recorded);
    if (!intent)
        throw new Error('recovery_intent');
    expect(intent).toMatchObject({ kind: 'recover_bridge_update' });
    await deliver(controller.recoverOperation(recorded), run.clock);
    expect(run.facade.actions.state.plan?.semantics.capture).toMatchObject(intent);
    expect(run.facade.work.observations.state.operations[0]).toEqual(recorded);
    expect(run.client.replayCount).toBe(0);
    controller.dispose();
    run.dispose();
});
