import { expect, test } from 'vitest';
import { SupportController, diagnosticRows, disclosedPaths } from '../../src/views/support';
import { bind, deliver, epoch, exchange, harness, output, reply, request, script } from './helpers';
function support(steps: Parameters<typeof script>[0]) {
    const run = harness(script(steps)), target = output('sc16-redacted-preview-reply', 'diagnostic_preview').content.target;
    bind(run, target);
    const controller = new SupportController(run.facade);
    return { ...run, controller };
}
const previewPair = () => exchange(request('sc16-redacted-preview-request'), reply('sc16-redacted-preview-reply'));
const destinationPair = (id = 'sc16-capture-export-destination') => exchange(request(id + '-request'), reply(id + '-reply'));
test('verified redacted preview and backend destination stop at shared review without an export write', async () => {
    const run = support([previewPair(), destinationPair(), exchange(request('sc16-export-reviewed-preview-request'), reply('sc16-export-reviewed-preview-reply'))]);
    expect(run.controller.state.includePaths).toBe(false);
    expect(run.controller.exportIntent()).toBeUndefined();
    await deliver(run.controller.preview(), run.clock);
    expect(run.controller.state.preview?.content.disclosure).toBe('redacted');
    expect(disclosedPaths(run.controller.state.preview)).toEqual([]);
    expect(run.controller.exportIntent()).toBeUndefined();
    await deliver(run.controller.chooseDestination(), run.clock);
    expect(run.controller.exportIntent()?.kind).toBe('export_diagnostics');
    expect(await deliver(run.controller.reviewExport(), run.clock)).toBe(true);
    expect(run.facade.actions.state.transition.kind).toBe('review');
    expect(run.transport.state.position).toBe(3);
    expect(run.client.replayCount).toBe(0);
    run.controller.dispose();
    run.dispose();
});
test('explicit disclosure change clears destination and review; paths require a fresh verified preview', async () => {
    const run = support([previewPair(), destinationPair(), exchange(request('sc16-export-reviewed-preview-request'), reply('sc16-export-reviewed-preview-reply')), exchange(request('sc16-explicit-path-disclosure-request'), reply('sc16-explicit-path-disclosure-reply'))]);
    await deliver(run.controller.preview(), run.clock);
    await deliver(run.controller.chooseDestination(), run.clock);
    await deliver(run.controller.reviewExport(), run.clock);
    run.controller.setDisclosure(true);
    expect(run.controller.state.preview).toBeUndefined();
    expect(run.controller.state.destination).toBeUndefined();
    expect(run.controller.exportIntent()).toBeUndefined();
    expect(run.facade.actions.state.transition.kind).toBe('idle');
    await deliver(run.controller.preview(), run.clock);
    expect(disclosedPaths(run.controller.state.preview).length).toBeGreaterThan(0);
    expect(run.controller.state.destination).toBeUndefined();
    run.controller.setDisclosure(false);
    expect(disclosedPaths(run.controller.state.preview)).toEqual([]);
    expect(run.controller.exportIntent()).toBeUndefined();
    run.controller.dispose();
    run.dispose();
});
test('late native destination after disclosure change cannot revive export authority', async () => {
    const run = support([previewPair(), destinationPair()]);
    await deliver(run.controller.preview(), run.clock);
    const pending = run.controller.chooseDestination();
    run.controller.setDisclosure(true);
    await deliver(pending, run.clock);
    expect(run.controller.state.includePaths).toBe(true);
    expect(run.controller.state.destination).toBeUndefined();
    expect(run.controller.exportIntent()).toBeUndefined();
    run.controller.dispose();
    run.dispose();
});
test('destination cancellation and unavailability leave preview but produce no export intent', async () => {
    for (const id of ['sc16-destination-cancelled', 'sc16-destination-unavailable']) {
        const run = support([previewPair(), destinationPair(id)]);
        await deliver(run.controller.preview(), run.clock);
        await deliver(run.controller.chooseDestination(), run.clock);
        expect(run.controller.state.preview).toBeDefined();
        expect(run.controller.state.destination).toBeUndefined();
        expect(run.controller.exportIntent()).toBeUndefined();
        expect(run.controller.state.notice).toMatch(/cancelled|unavailable/);
        run.controller.dispose();
        run.dispose();
    }
});
test('invalid content digest cannot reach preview, destination selection or export review', async () => {
    const response = reply('sc16-redacted-preview-reply');
    if (response.body.type !== 'result' || response.body.result.type !== 'query' || response.body.result.query.name !== 'diagnostic_preview')
        throw new Error('preview_fixture');
    response.body.result.query.output.reference.digest = 'sha256:' + '1'.repeat(64);
    const run = support([exchange(request('sc16-redacted-preview-request'), response)]);
    await deliver(run.controller.preview(), run.clock);
    await run.controller.chooseDestination();
    expect(await run.controller.reviewExport()).toBe(false);
    expect(run.controller.state.preview).toBeUndefined();
    expect(run.controller.state.destination).toBeUndefined();
    expect(run.transport.state.position).toBe(1);
    run.controller.dispose();
    run.dispose();
});
test('preview from another physical binding cannot replace the selected resolved target', async () => {
    const run = support([previewPair()]);
    const binding = run.facade.work.state.binding;
    if (!binding)
        throw new Error('target_fixture');
    run.facade.work.bindTarget({ ...binding, installation: { ...binding.installation, physicalId: 'foreign-current-physical' } });
    await deliver(run.controller.preview(), run.clock);
    expect(run.controller.state.preview).toBeUndefined();
    expect(run.controller.state.notice).toMatch(/scope changed/);
    run.controller.dispose();
    run.dispose();
});
test('optional-none target binding differences preserve the verified diagnostic scope',async()=>{
 const run=support([previewPair()]),binding=run.facade.work.state.binding;if(!binding||binding.profile.kind!=='ordinary')throw new Error('target_fixture');
 run.facade.work.bindTarget({...binding,profile:{...binding.profile,ordinaryId:null}});await deliver(run.controller.preview(),run.clock);
 expect(run.controller.state.preview?.reference.disclosure).toBe('redacted');expect(run.controller.state.preview?.reference.target.installation.physicalId).toBe(binding.installation.physicalId);run.controller.dispose();run.dispose();
});
test('target changes and disposal abandon old diagnostic observations', async () => {
    for (const variant of ['target', 'dispose'] as const) {
        const run = support([previewPair()]);
        const pending = run.controller.preview();
        if (variant === 'target')
            run.facade.requestTarget({ installation: { kind: 'registered', id: 'dddddddddddddddddddddddddddddddd' }, profile: { kind: 'ordinary' } });
        else
            run.controller.dispose();
        await deliver(pending, run.clock);
        expect(run.controller.state.preview).toBeUndefined();
        expect(run.controller.state.destination).toBeUndefined();
        run.controller.dispose();
        run.dispose();
    }
});
test('a new host resets explicit path disclosure and captured destination', async () => {
    const run = support([exchange(request('sc16-explicit-path-disclosure-request'), reply('sc16-explicit-path-disclosure-reply'))]);
    run.controller.setDisclosure(true);
    await deliver(run.controller.preview(), run.clock);
    expect(run.controller.state.preview).toBeDefined();
    expect(run.controller.state.includePaths).toBe(true);
    const original = epoch(run);
    run.facade.work.observations.observeHello('000003e8-3333-4333-8333-333333333333');
    expect(epoch(run)).toBe(original);
    expect(run.controller.state.includePaths).toBe(false);
    expect(run.controller.state.preview).toBeUndefined();
    expect(run.controller.exportIntent()).toBeUndefined();
    run.controller.dispose();
    run.dispose();
});
test('changing disclosure while preparation is in flight cannot expose the old export review', async () => {
    const run = support([previewPair(), destinationPair(), exchange(request('sc16-export-reviewed-preview-request'), reply('sc16-export-reviewed-preview-reply'))]);
    await deliver(run.controller.preview(), run.clock);
    await deliver(run.controller.chooseDestination(), run.clock);
    const pending = run.controller.reviewExport();
    run.controller.setDisclosure(true);
    expect(await deliver(pending, run.clock)).toBe(false);
    expect(run.facade.actions.state.transition.kind).toBe('idle');
    expect(run.controller.exportIntent()).toBeUndefined();
    run.controller.dispose();
    run.dispose();
});
test('reentrant disclosure publication does not deliver a stale busy state or issue an old query', async () => {
    const run = support([]);
    let changed = false;
    const observed: boolean[] = [];
    const first = run.controller.subscribe(state => { if (state.busy && !changed) {
        changed = true;
        run.controller.setDisclosure(true);
    } }), second = run.controller.subscribe(state => observed.push(state.busy));
    await run.controller.preview();
    expect(run.transport.state.position).toBe(0);
    expect(observed).not.toContain(true);
    expect(run.controller.state.includePaths).toBe(true);
    first();
    second();
    run.controller.dispose();
    run.dispose();
});
test('redacted public diagnostic rows never render native custody or filesystem paths', () => {
    const preview = output('sc16-redacted-preview-reply', 'diagnostic_preview'), display = JSON.stringify(diagnosticRows(preview));
    expect(display).not.toContain(preview.reference.previewId);
    expect(display).not.toContain(preview.reference.target.installation.nativeTargetRef);
    expect(display).not.toMatch(/[A-Z]:\\/);
    expect(disclosedPaths(preview)).toEqual([]);
});
