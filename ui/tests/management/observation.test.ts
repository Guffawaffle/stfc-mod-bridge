import { expect, test } from 'vitest';
import { ManagementController,observedProfiles } from '../../src/views/management';
import { bind, deliver, draft, exchange, harness, reply, request, script, snapshot } from './helpers';
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
