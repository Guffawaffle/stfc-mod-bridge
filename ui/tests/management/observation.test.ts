import { expect, test, vi } from 'vitest';
import { ManagementController,observedProfiles } from '../../src/views/management';
import { bind, deliver, draft, exchange, harness, reply, request, script, snapshot } from './helpers';
import { decodeRequest } from '../../src/client';
import type { ActionScope } from '../../src/generated/protocol';

function concurrentAvailability() {
    const run = harness(script([])), controller = new ManagementController(run.facade);
    const profile = observedProfiles(run.facade.work.observations.state).find(row => row.kind === 'isolated');
    if (profile?.kind !== 'isolated') throw new Error('profile_fixture');
    const scope: ActionScope = { kind: 'profile', profile: profile.reference };
    const pending: { succeed: () => void; fail: () => void }[] = [];
    const spy = vi.spyOn(run.transport, 'exchange').mockImplementation(raw => new Promise((resolve, reject) => {
        const input = decodeRequest(raw);
        if (input.body.type !== 'query' || input.body.query.name !== 'get_actions') throw new Error('actions_request');
        const response = reply('sc-02-ordinary-ready-action-reply');
        if (response.body.type !== 'result' || response.body.result.type !== 'query' || response.body.result.query.name !== 'get_actions') throw new Error('actions_fixture');
        const template = response.body.result.query.output[0];
        response.body.result.query.output = input.body.query.input.actions.map(action => ({ ...template, action }));
        pending.push({ succeed: () => resolve(JSON.stringify({ ...response, requestId: input.requestId })), fail: () => reject({ code: 'delivery_failed', delivery: 'may_have_reached_backend' }) });
    }));
    return { run, controller, scope, pending, dispose() { controller.dispose(); spy.mockRestore(); run.dispose(); } };
}

test('late superseded availability failure cannot replace the newer successful read message', async () => {
    const testRun = concurrentAvailability();
    const { controller, scope, pending } = testRun;
    try {
        const older = controller.inspectActions(scope, ['archive_profile']);
        const newer = controller.inspectActions(scope, ['archive_profile']);
        expect(pending).toHaveLength(2);
        pending[1].succeed(); await newer;
        expect(controller.projection(scope, 'archive_profile')?.availability.status).toBe('available');
        const current = controller.state;
        pending[0].fail(); await older;
        expect(controller.state.notice).toBe('');
        expect(controller.state).toBe(current);
    } finally { testRun.dispose(); }
});

test('late superseded availability success cannot clear the newer failed read message', async () => {
    const testRun = concurrentAvailability();
    const { controller, scope, pending } = testRun;
    try {
        const older = controller.inspectActions(scope, ['archive_profile']);
        const newer = controller.inspectActions(scope, ['archive_profile']);
        pending[1].fail(); await newer;
        const current = controller.state;
        expect(current.notice).toMatch(/unavailable/);
        pending[0].succeed(); await older;
        expect(controller.projection(scope, 'archive_profile')).toBeUndefined();
        expect(controller.state).toBe(current);
    } finally { testRun.dispose(); }
});

test('partially superseded availability updates only retained actions and preserves the newest message', async () => {
    const testRun = concurrentAvailability();
    const { controller, scope, pending } = testRun;
    try {
        const older = controller.inspectActions(scope, ['archive_profile', 'edit_isolated_profile']);
        const newer = controller.inspectActions(scope, ['edit_isolated_profile']);
        pending[1].fail(); await newer;
        const notice = controller.state.notice;
        pending[0].succeed(); await older;
        expect(controller.projection(scope, 'archive_profile')?.availability.status).toBe('available');
        expect(controller.projection(scope, 'edit_isolated_profile')).toBeUndefined();
        expect(controller.state.notice).toBe(notice);
    } finally { testRun.dispose(); }
});

test('a reentrant newer availability read prevents a fully superseded outbound request', async () => {
    const testRun = concurrentAvailability();
    const { controller, scope, pending } = testRun;
    let newer: Promise<void> | undefined, started = false;
    const stop = controller.subscribe(() => {
        if (!started) { started = true; return; }
        if (!newer) { newer = Promise.resolve(); newer = controller.inspectActions(scope, ['archive_profile']); }
    });
    try {
        const older = controller.inspectActions(scope, ['archive_profile']);
        expect(pending).toHaveLength(1);
        pending[0].succeed(); await newer; await older;
        expect(controller.projection(scope, 'archive_profile')?.availability.status).toBe('available');
    } finally { stop(); testRun.dispose(); }
});
test('a replaced document baseline clears old complete backup history', async () => {
    const input = request('sc10-configuration-history-request');
    if (input.body.type !== 'query' || input.body.query.name !== 'configuration_history')
        throw new Error('document_fixture');
    const document = input.body.query.input.document;
    const run = harness(script([exchange(input, reply('sc10-configuration-history-reply'))]));
    bind(run, document.target);
    run.facade.work.openDraft(draft(document));
    const controller = new ManagementController(run.facade);
    await deliver(controller.history(document), run.clock);
    expect(controller.state.historyComplete).toBe(true);
    expect(controller.state.backups?.length).toBeGreaterThan(0);
    run.facade.work.openDraft(draft({ ...document, revision: 'new-observed-baseline' }));
    expect(controller.state.backups).toBeUndefined();
    expect(controller.state.historyComplete).toBeUndefined();
    controller.dispose();
    run.dispose();
});
test('changed provider preference invalidates an in-flight old release check', async () => {
    const input = request('sc11-check-runtime-release-request');
    if (input.body.type !== 'query' || input.body.query.name !== 'check_runtime_release')
        throw new Error('runtime_fixture');
    const { target, providerId, channelId } = input.body.query.input;
    const observed = snapshot();
    if (observed.installations.status !== 'observed')
        throw new Error('evidence_fixture');
    observed.preferences = { status: 'observed', evidence: observed.installations.evidence, value: { revision: 'preference-1', values: { theme: 'system', motion: 'system', provider: { providerId, channelId } } } };
    const run = harness(script([exchange(input, reply('sc11-check-runtime-release-reply'))]));
    run.facade.work.observations.acceptSnapshot(observed);
    bind(run, target);
    const controller = new ManagementController(run.facade), pending = controller.checkRuntime(providerId, channelId);
    observed.preferences.value.values.provider = { providerId, channelId: 'other-channel' };
    observed.preferences.value.revision = 'preference-2';
    run.facade.work.observations.acceptSnapshot(observed);
    await deliver(pending, run.clock);
    expect(controller.state.runtimeRelease).toBeUndefined();
    expect(controller.state.busy).toBe(false);
    controller.dispose();
    run.dispose();
});
test('null and omitted optional profile preferences preserve exact observed identity',()=>{
 const observed=snapshot();if(observed.profiles.status!=='observed')throw new Error('profile_fixture');const row=observed.profiles.value.items.find(value=>value.kind==='isolated');if(row?.kind!=='isolated')throw new Error('isolated_fixture');row.preferredInstallation=null;
 const run=harness(script([]));run.facade.work.observations.acceptSnapshot(observed);const controller=new ManagementController(run.facade),current=observedProfiles(run.facade.work.observations.state).find(value=>value.kind==='isolated');if(current?.kind!=='isolated')throw new Error('isolated_fixture');
 const {preferredInstallation:_,...withoutPreference}=current;expect(controller.profileScope(withoutPreference)).toEqual({kind:'profile',profile:current.reference});expect(controller.profileIntent(withoutPreference,'edit','Name')).toMatchObject({input:{profile:current.reference,preferredInstallation:{kind:'keep',expected:{kind:'none'}}}});controller.dispose();run.dispose();
});
