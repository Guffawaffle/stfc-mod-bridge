import { readFileSync } from 'node:fs';
import { createHash } from 'node:crypto';
import { expect, test } from 'vitest';
import { canonicalData, decodeReply, decodeRequest } from '../../src/client';
import { createManagementSession, managementModes, type ManagementMode, type ManagementSession } from '../../src/mocks/management-session';
import { ManagementController } from '../../src/views/management';
import { SupportController } from '../../src/views/support';
const fixtures = new URL('../../../contracts/fixtures/', import.meta.url);
async function settle<T>(session: ManagementSession, pending: Promise<T>): Promise<T> { await session.settle(); return pending; }
async function start(mode: ManagementMode = 'management') { const session = await createManagementSession(mode); const controller = new ManagementController(session.facade, session.inputs); await settle(session, session.facade.connect()); return { session, controller, dispose() { controller.dispose(); session.dispose(); } }; }
test('Management preview modes validate every golden provenance hash and recorded wire frame', async () => {
    for (const mode of managementModes) {
        const run = await start(mode);
        try {
            for (const source of run.session.provenance)
                expect(source.sha256).toBe(createHash('sha256').update(readFileSync(new URL(source.id + '.json', fixtures))).digest('hex'));
            for (const record of run.session.records) {
                expect(() => decodeRequest(record.request)).not.toThrow();
                if (record.reply)
                    expect(() => decodeReply(record.reply!)).not.toThrow();
            }
            expect(run.session.delivery.lastFault).toBeUndefined();
            expect(run.session.facade.work.observations.state.confidence).toBe('authoritative');
        }
        finally {
            run.dispose();
        }
    }
});
test('Management preview duplicate profile review preserves immutable identity and future-launch preference', async () => {
    const { session, controller, dispose } = await start();
    try {
        const profiles = session.facade.work.observations.state.snapshot?.profiles;
        if (profiles?.status !== 'observed')
            throw new Error('profiles');
        const isolated = profiles.value.items.filter(row => row.kind === 'isolated');
        expect(isolated).toHaveLength(2);
        for (const profile of isolated) {
            const scope = controller.profileScope(profile)!;
            await settle(session, controller.inspectActions(scope, ['edit_isolated_profile']));
            const intent = controller.profileIntent(profile, 'edit', 'Synthetic renamed profile', { kind: 'keep', expected: { kind: 'registered', id: 'a'.repeat(32) } })!;
            await settle(session, controller.review(intent, scope, 'preview-profile'));
            expect(session.facade.actions.state.plan?.semantics.capture).toMatchObject({ kind: 'edit_isolated_profile', input: { profile: { id: profile.reference.id }, preferredInstallation: { kind: 'keep' } } });
            expect(session.facade.actions.stay()).toBe(true);
        }
        expect(session.facade.work.state.binding?.profile.kind).toBe('ordinary');
        expect(session.records.some(row => row.method === 'commit')).toBe(false);
        expect(session.delivery.lastFault).toBeUndefined();
    }
    finally {
        dispose();
    }
});
test('Management preview archive admission remains nonterminal until exact row completion is observed', async () => {
    const { session, controller, dispose } = await start();
    try {
        const profiles = session.facade.work.observations.state.snapshot?.profiles;
        if (profiles?.status !== 'observed')
            throw new Error('profiles');
        const profile = profiles.value.items.find(row => row.kind === 'isolated' && row.reference.id === 'b'.repeat(32))!;
        const scope = controller.profileScope(profile)!;
        await settle(session, controller.inspectActions(scope, ['archive_profile']));
        await settle(session, controller.review(controller.profileIntent(profile, 'archive')!, scope, 'preview-archive'));
        await settle(session, session.facade.actions.confirm());
        expect(session.facade.actions.state.transition.kind).toBe('observing');
        expect(session.facade.work.observations.state.operations[0].state.status).toBe('admitted');
        await settle(session, session.facade.actions.reconcile());
        expect(session.facade.actions.state.transition.kind).toBe('idle');
        expect(session.facade.work.observations.state.operations[0].state.status).toBe('completed');
        expect(session.delivery.lastFault).toBeUndefined();
    }
    finally {
        dispose();
    }
});
test('Management preview updater routes preserve distinct runtime game and Bridge captured authorities', async () => {
    const { session, controller, dispose } = await start();
    try {
        const target = session.facade.work.state.binding!, scope = { kind: 'target' as const, target };
        await settle(session, controller.checkRuntime('guffawaffle', 'stable'));
        await settle(session, controller.inspectActions(scope, ['runtime_update']));
        const document = session.facade.work.state.draft!.draft.document;
        await settle(session, controller.review(controller.runtimeIntent('runtime_update', { kind: 'unchanged', document })!, scope, 'preview-runtime'));
        expect(session.facade.actions.state.plan?.semantics.capture.kind).toBe('runtime_update');
        session.facade.actions.stay();
        await settle(session, controller.checkGame());
        await settle(session, controller.inspectActions(scope, ['game_update']));
        await settle(session, controller.review(controller.gameIntent()!, scope, 'preview-game'));
        expect(session.facade.actions.state.plan?.semantics.capture.kind).toBe('game_update');
        session.facade.actions.stay();
        const application = controller.application!, appScope = { kind: 'application' as const, application };
        await settle(session, controller.checkBridge());
        await settle(session, controller.inspectActions(appScope, ['bridge_update']));
        await settle(session, controller.review(controller.bridgeIntent()!, appScope, 'preview-bridge'));
        expect(session.facade.actions.state.plan?.semantics.capture.kind).toBe('bridge_update');
        expect(session.records.some(row => row.method === 'commit')).toBe(false);
        expect(session.delivery.lastFault).toBeUndefined();
    }
    finally {
        dispose();
    }
});
test('Management preview history uses the shared exact document and retained backup without a parallel draft', async () => {
    const { session, controller, dispose } = await start();
    try {
        const document = session.facade.work.state.draft!.draft.document, scope = { kind: 'document' as const, document };
        await settle(session, controller.history(document));
        await settle(session, controller.inspectActions(scope, ['restore_configuration']));
        await settle(session, controller.review(controller.restoreIntent(document, controller.state.backups![0])!, scope, 'preview-restore'));
        const capture = session.facade.actions.state.plan?.semantics.capture;
        expect(capture?.kind).toBe('restore_configuration');
        if (capture?.kind === 'restore_configuration')
            expect(canonicalData(capture.input.document)).toBe(canonicalData(document));
        expect(session.delivery.lastFault).toBeUndefined();
    }
    finally {
        dispose();
    }
});
test('Management preview native import discovery uses exact observed destination owner and explicit approval', async () => {
    const { session, controller, dispose } = await start();
    try {
        await settle(session, controller.discoverImports('synthetic-native-owner-1'));
        expect(controller.state.imports).toHaveLength(1);
        expect(controller.state.imports?.[0].reference.destinationOwner).toBe('synthetic-native-owner-1');
        const frame = decodeRequest(session.records.find(row => row.method === 'request_import_discovery')!.request);
        expect(frame.body).toMatchObject({ command: { input: { expectedDestinationOwner: 'synthetic-native-owner-1', approval: 'request_native_approval' } } });
        expect(session.delivery.lastFault).toBeUndefined();
    }
    finally {
        dispose();
    }
});
test('Management preview cancellation binds the observed row and preserves requested versus too late outcomes', async () => {
    for (const mode of ['operations', 'too_late'] as const) {
        const { session, dispose } = await start(mode);
        try {
            const operation = session.facade.work.observations.state.operations[0];
            await settle(session, session.facade.cancelOperation(operation));
            expect(session.facade.work.observations.state.operations[0].state.status).toBe(mode === 'operations' ? 'cancellation_requested' : 'running');
            const frame = decodeRequest(session.records.find(row => row.method === 'cancel_operation')!.request);
            expect(frame.body).toMatchObject({ command: { input: { operationId: operation.operationId, expectedOperationRevision: operation.operationRevision } } });
            expect(session.delivery.lastFault).toBeUndefined();
        }
        finally {
            dispose();
        }
    }
});
test('Management preview retained recovery derives only recorded game and Bridge intents independent of current selection', async () => {
    const { session, controller, dispose } = await start('recovery');
    try {
        const recorded = session.facade.work.observations.state.operations;
        expect(recorded).toHaveLength(2);
        for (const operation of recorded) {
            await settle(session, controller.recoverOperation(operation));
            expect(session.facade.actions.state.plan?.semantics.capture.kind).toBe(operation.semantics.capture.kind === 'game_update' ? 'recover_game_update' : 'recover_bridge_update');
            session.facade.actions.stay();
        }
        expect(session.records.filter(row => row.method === 'prepare')).toHaveLength(2);
        expect(session.delivery.lastFault).toBeUndefined();
    }
    finally {
        dispose();
    }
});
test('Management preview diagnostics bind disclosure preview chooser and review while excluded fields remain absent', async () => {
    const { session, dispose } = await start();
    const controller = new SupportController(session.facade);
    try {
        for (const include of [false, true]) {
            controller.setDisclosure(include);
            await settle(session, controller.preview());
            expect(controller.state.preview?.content.disclosure).toBe(include ? 'include_paths' : 'redacted');
            if (!include)
                expect(controller.state.preview?.content.paths).toBeUndefined();
            await settle(session, controller.chooseDestination());
            await settle(session, controller.reviewExport());
            const capture = session.facade.actions.state.plan?.semantics.capture;
            expect(capture).toMatchObject({ kind: 'export_diagnostics', input: { preview: { disclosure: include ? 'include_paths' : 'redacted' } } });
            session.facade.actions.stay();
        }
        expect(session.records.some(row => row.method === 'commit')).toBe(false);
        expect(session.delivery.lastFault).toBeUndefined();
    }
    finally {
        controller.dispose();
        dispose();
    }
});
test('Management preview cancelled destination and absent metadata do not create review authority', async () => {
    const run = await start('destination_cancelled');
    const support = new SupportController(run.session.facade);
    try {
        await settle(run.session, support.preview());
        await settle(run.session, support.chooseDestination());
        expect(support.state.destination).toBeUndefined();
        expect(support.exportIntent()).toBeUndefined();
        expect(run.session.delivery.lastFault).toBeUndefined();
    }
    finally {
        support.dispose();
        run.dispose();
    }
    const unavailable = await start('unavailable');
    try {
        expect(unavailable.controller.catalogRevision).toBeUndefined();
        expect(unavailable.controller.application).toBeUndefined();
        expect(unavailable.controller.runtime).toBeUndefined();
        await unavailable.controller.checkBridge();
        expect(unavailable.session.records.map(row => row.method)).toEqual(['snapshot']);
    }
    finally {
        unavailable.dispose();
    }
});
test('Management preview closed request budget refuses foreign document reads and disposal rejects owned pending observations', async () => {
    const run = await start();
    try {
        const pending = run.session.client.query('configuration_history', { document: { ...run.session.facade.work.state.draft!.draft.document, revision: 'foreign-document' } });
        await run.session.settle();
        expect((await pending).kind).toBe('fault');
        expect(run.session.delivery.lastFault).toBe('unexpected_request');
    }
    finally {
        run.dispose();
    }
    const old = await createManagementSession('management');
    const pending = old.facade.connect();
    old.dispose();
    expect((await pending).kind).toBe('fault');
    expect(old.clock.disposed).toBe(true);
    const fresh = await createManagementSession('management');
    expect(fresh.records).toEqual([]);
    expect(fresh.delivery.lastFault).toBeUndefined();
    fresh.dispose();
});
