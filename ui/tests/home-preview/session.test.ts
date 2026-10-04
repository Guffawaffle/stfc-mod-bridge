import { readFileSync } from 'node:fs';
import { createHash } from 'node:crypto';
import { expect, test } from 'vitest';
import { canonicalData, decodeReply, decodeRequest } from '../../src/client';
import type { MutationIntent, TargetSelector } from '../../src/generated/protocol';
import { createHomeSession, homeModes, type HomeSession } from '../../src/mocks/home-session';
import { HomeController, selectorFor } from '../../src/views/home';

const fixtures = new URL('../../../contracts/fixtures/', import.meta.url);
const raw = (id: string) => readFileSync(new URL(id + '.json', fixtures), 'utf8');
const frame = (id: string): any => JSON.parse(raw(id));
async function settled<T>(run: HomeSession, promise: Promise<T>): Promise<T> { await run.settle(); return promise; }
async function connect(run: HomeSession): Promise<void> { expect(await settled(run, run.facade.connect())).toMatchObject({ kind: 'result' }); }
function selector(run: HomeSession, isolated = false): TargetSelector {
  const value = selectorFor('aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa', isolated ? 'bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb' : 'ordinary', run.facade.work.observations.state);
  if (!value) throw new Error('test_selector'); return JSON.parse(JSON.stringify(value));
}
async function apply(run: HomeSession, isolated = false): Promise<HomeController> {
  const controller = new HomeController(run.facade); controller.start(); expect(run.facade.requestTarget(selector(run, isolated))).toBe(true); await run.settle(); return controller;
}
function launch(controller: HomeController, isolated = false): MutationIntent {
  const value = controller.launchIntent(isolated ? 'existing' : undefined); if (!value) throw new Error('test_launch'); return JSON.parse(JSON.stringify(value));
}

test('Home preview every mode validates exact composed wire frames and hashes all unchanged shared source bytes', async () => {
  for (const mode of homeModes) {
    const run = await createHomeSession(mode);
    try {
      expect(run.provenance.length).toBeGreaterThan(0); expect(new Set(run.provenance.map(source => source.id)).size).toBe(run.provenance.length);
      for (const source of run.provenance) expect(source.sha256).toBe(createHash('sha256').update(raw(source.id)).digest('hex'));
      for (const step of run.script.steps) if (step.type === 'exchange') {
        expect(() => decodeRequest(JSON.stringify(step.request))).not.toThrow(); expect(() => decodeReply(JSON.stringify(step.reply))).not.toThrow();
        expect(step.request.requestId).toBe(step.reply.requestId);
      }
      expect(run.facade.work.state.selector).toBeUndefined(); await connect(run);
      expect(run.requests.map(record => record.method)).toEqual(['snapshot']); expect(run.transport.state.lastFault).toBeUndefined();
    } finally { run.dispose(); }
  }
});

test('Home preview ordinary launch binds observed revisions and preserves accepted review digest through admission and exact completion', async () => {
  const run = await createHomeSession('ordinary_ready'); let controller: HomeController | undefined;
  try {
    await connect(run); controller = await apply(run);
    expect(controller.state.launch?.availability.status).toBe('available');
    await settled(run, run.facade.actions.prepare(launch(controller)));
    expect(run.facade.actions.state.transition.kind).toBe('review'); expect(run.client.replayCount).toBe(0);
    expect(run.facade.actions.state.plan).toEqual(frame('sc14-prepare-reply').body.result.command.output);
    const requested = JSON.parse(run.requests.find(row => row.method === 'prepare')!.frame);
    expect(requested.body.command.input.intent.input.target.installation.revisionAssertion).toBe('synthetic-installation-revision-1');
    await settled(run, run.facade.actions.confirm()); expect(run.facade.actions.state.transition.kind).toBe('observing');
    expect(run.facade.actions.state.notice).not.toContain('completed'); expect(run.client.replayCount).toBe(1);
    await settled(run, run.facade.actions.reconcile()); expect(run.facade.actions.state.notice).toBe('Action completed.'); expect(run.client.replayCount).toBe(0);
    expect(run.requests.map(row => row.method)).toEqual(['snapshot', 'resolve_target', 'get_actions', 'prepare', 'commit', 'get_operation']);
    expect(run.transport.state.lastFault).toBeUndefined(); expect(run.transport.state.position).toBe(run.script.steps.length);
  } finally { controller?.dispose(); run.dispose(); }
});

test('Home preview isolated launch preserves explicit existing mode immutable profile revision and accepted completion capture', async () => {
  const run = await createHomeSession('isolated_ready'); let controller: HomeController | undefined;
  try {
    await connect(run); controller = await apply(run, true); await settled(run, run.facade.actions.prepare(launch(controller, true)));
    expect(run.facade.actions.state.plan).toEqual(frame('sc-03-working-readiness-prepare-reply').body.result.command.output);
    const requested = JSON.parse(run.requests.find(row => row.method === 'prepare')!.frame);
    expect(requested.body.command.input.intent.input).toMatchObject({ storeMode: 'existing', target: { profile: { id: 'bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb', revisionAssertion: 'synthetic-profile-revision-1' } } });
    await settled(run, run.facade.actions.confirm()); expect(run.facade.actions.state.transition.kind).toBe('observing');
    await settled(run, run.facade.actions.reconcile()); expect(run.facade.actions.state.notice).toBe('Action completed.'); expect(run.transport.state.lastFault).toBeUndefined();
  } finally { controller?.dispose(); run.dispose(); }
});

test('Home preview focus uses exact Windows session while old and recycled PID identities remain independent observations', async () => {
  const run = await createHomeSession('focus_sessions'); const controller = new HomeController(run.facade);
  try {
    await connect(run); const sessions = run.facade.work.observations.state.snapshot?.sessions; if (sessions?.status !== 'observed') throw new Error('test_sessions');
    expect(sessions.value.items).toHaveLength(4); expect(sessions.value.items[1].binding.process.pid).toBe(sessions.value.items[2].binding.process.pid);
    expect(sessions.value.items[1].binding.process.startIdentity).not.toEqual(sessions.value.items[2].binding.process.startIdentity);
    await settled(run, controller.inspectSession(sessions.value.items[0].binding)); const intent = controller.focusIntent(sessions.value.items[0].binding)!;
    await settled(run, run.facade.actions.prepare(intent)); expect(run.facade.actions.state.plan).toEqual(frame('sc18-windows-x86-64-focus-prepare-reply').body.result.command.output);
    await settled(run, run.facade.actions.confirm()); expect(run.facade.actions.state.notice).toBe('Action completed.');
    const requested = JSON.parse(run.requests.find(row => row.method === 'prepare')!.frame);
    expect(requested.body.command.input.intent.input.session).toEqual(sessions.value.items[0].binding); expect(run.facade.work.state.selector).toBeUndefined();
  } finally { controller.dispose(); run.dispose(); }
});

test('Home preview lost delivery retains exact commit until explicit replay then completion without automatic retry', async () => {
  const run = await createHomeSession('launch_uncertain'); let controller: HomeController | undefined;
  try {
    await connect(run); controller = await apply(run); await settled(run, run.facade.actions.prepare(launch(controller)));
    await settled(run, run.facade.actions.confirm()); expect(run.facade.actions.state.transition.kind).toBe('uncertain');
    expect(run.requests.filter(row => row.method === 'commit')).toHaveLength(1); expect(run.client.replayCount).toBe(1);
    await settled(run, run.facade.actions.replay()); const commits = run.requests.filter(row => row.method === 'commit').map(row => decodeRequest(row.frame));
    expect(commits).toHaveLength(2); expect(canonicalData(commits[0].body)).toBe(canonicalData(commits[1].body)); expect(commits[0].requestId).not.toBe(commits[1].requestId);
    expect(run.facade.actions.state.transition.kind).toBe('observing'); await settled(run, run.facade.actions.reconcile()); expect(run.facade.actions.state.notice).toBe('Action completed.');
  } finally { controller?.dispose(); run.dispose(); }
});

test('Home preview unknown partial offline missing and recovery states never enable or submit a launch', async () => {
  for (const mode of ['unknown', 'offline', 'missing', 'recovery'] as const) {
    const run = await createHomeSession(mode); let controller: HomeController | undefined;
    try {
      await connect(run);
      if (mode === 'missing') expect(selectorFor('aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa', 'ordinary', run.facade.work.observations.state)).toBeUndefined();
      else { controller = await apply(run); expect(controller.launchIntent()).toBeUndefined(); }
      if (mode === 'unknown') expect(run.facade.work.observations.state.confidence).toBe('partial');
      if (mode === 'offline') expect(controller?.state.launch?.availability).toEqual({ status: 'unavailable', reason: { code: 'offline' } });
      if (mode === 'recovery') expect(run.facade.work.observations.state.operations[0].state.status).toBe('recovery_required');
      expect(run.requests.some(row => ['prepare', 'commit'].includes(row.method))).toBe(false); expect(run.transport.state.lastFault).toBeUndefined();
    } finally { controller?.dispose(); run.dispose(); }
  }
});

test('Home preview dirty draft survives view navigation and Stay then exact reviewed Save applies target only after completion', async () => {
  const run = await createHomeSession('dirty_draft'); const controller = new HomeController(run.facade); controller.start();
  try {
    await connect(run); const retained = canonicalData(run.facade.work.state.draft); const edits = canonicalData(run.facade.work.state.edits);
    run.facade.navigate('engineering'); run.facade.navigate('home'); expect(canonicalData(run.facade.work.state.draft)).toBe(retained); expect(canonicalData(run.facade.work.state.edits)).toBe(edits);
    expect(run.facade.requestTarget(selector(run, true))).toBe(false); expect(run.facade.stay()).toBe(true); expect(run.requests).toHaveLength(1);
    expect(run.facade.requestTarget(selector(run, true))).toBe(false); await settled(run, run.facade.prepareSave());
    expect(run.facade.state.transition.kind).toBe('review'); expect(run.facade.work.state.draft?.draft.revision).toBe('3');
    expect(run.facade.state.transition.kind === 'review' && run.facade.state.transition.plan).toEqual(frame('sc10-save-restaged-draft-reply').body.result.command.output);
    await settled(run, run.facade.commitSave()); expect(run.facade.state.transition.kind).toBe('observing'); expect(run.facade.work.state.pendingNavigation?.kind).toBe('target');
    expect(run.facade.work.state.selector).toBeUndefined(); await settled(run, run.facade.reconcileSave());
    expect(run.facade.state.notice).toBe('Changes saved.'); expect(run.facade.work.state.pendingNavigation).toBeUndefined(); expect(run.facade.work.state.dirty).toBe(false);
    expect(run.facade.work.state.selector?.profile.kind).toBe('isolated'); expect(run.transport.state.lastFault).toBeUndefined();
  } finally { controller.dispose(); run.dispose(); }
});

test('Home preview confirmed Discard uses the exact old dirty DraftRef and receipt before applying queued target', async () => {
  const run = await createHomeSession('dirty_draft', { draftOutcome: 'discard' }); const controller = new HomeController(run.facade); controller.start();
  try {
    await connect(run); expect(run.facade.requestTarget(selector(run, true))).toBe(false); expect(await settled(run, run.facade.discard())).toBe(true);
    const request = decodeRequest(run.requests.find(row => row.method === 'discard_draft')!.frame);
    expect(request.body).toEqual(frame('sc08-discard-draft-request').body); expect(run.facade.state.notice).toBe('Changes discarded.');
    expect(run.facade.work.state.selector?.profile.kind).toBe('isolated'); expect(run.client.replayCount).toBe(0); expect(run.transport.state.lastFault).toBeUndefined();
  } finally { controller.dispose(); run.dispose(); }
});

test('Home preview disposal ends old pending observation and bounded clocks without projecting late results into replacement session', async () => {
  const old = await createHomeSession('ordinary_ready'); const pending = old.facade.connect(); old.dispose();
  expect(await pending).toMatchObject({ kind: 'fault' }); expect(old.clock.disposed).toBe(true); expect(old.clock.pendingCount).toBe(0); expect(old.transport.state.pendingExchanges).toBe(0);
  const replacement = await createHomeSession('missing');
  try { await old.tick(); await old.settle(); await connect(replacement); expect(replacement.facade.work.observations.state.snapshot?.installations).toMatchObject({ status: 'observed', value: { items: [] } });
    await expect(replacement.settle(0)).rejects.toThrow('home_settle_bound'); expect(replacement.requests).toHaveLength(1);
  } finally { replacement.dispose(); }
});
