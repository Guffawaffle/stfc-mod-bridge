import { expect, test } from 'vitest';
import { readFileSync } from 'node:fs';
import { BridgeClient, WorkContext, canonicalData, decodeReply, decodeRequest, type ClientClock, type ClientOutcome, type DeepReadonly } from '../../src/client';
import type { DraftSnapshot, OperationSnapshot } from '../../src/generated/protocol';
import { BridgeFacade, FocusController, AnnouncementController } from '../../src/state';

const root = new URL('../../../contracts/fixtures/', import.meta.url);
const raw = (name: string) => readFileSync(new URL(name + '.json', root), 'utf8');
const frame = (name: string) => JSON.parse(raw(name));
function draft(name: string): DeepReadonly<DraftSnapshot> { const output = frame(name).body.result.command.output; return output.snapshot ?? output; }
function operation(): DeepReadonly<OperationSnapshot> { return frame('sc10-save-verified-result-reply').body.result.query.output.operation.value; }
const edits = () => frame('sc08-stage-dirty-draft-request').body.command.input.edits;
const target = () => frame('sc-03-profile-one-prepare-request').body.command.input.intent.input.target;
const keySequence = () => { let sequence = 0; return () => (++sequence).toString(16).padStart(8, '0') + '-1111-4111-8111-111111111111'; };
function rejected(code: string) {
  const reply = frame('sc10-save-stale-revision-reply'); reply.body.error.code = code; reply.body.error.retryDisposition = 'never'; reply.body.error.violations = [];
  decodeReply(JSON.stringify(reply)); return reply;
}
const correlation = (value: any, request: string) => JSON.stringify({ ...value, requestId: decodeRequest(request).requestId });
const metadata = { requestId: '00000001-2222-4222-8222-222222222222', kind: 'command' as const, method: 'set_draft_changes' as const };
class Clock implements ClientClock {
  tasks = new Set<() => void>();
  schedule(_ms: number, action: () => void) { this.tasks.add(action); return () => { this.tasks.delete(action); }; }
  expire() { for (const task of [...this.tasks]) task(); }
}
function harness(change?: (request: any, reply: any) => any, initialDraft = 'sc08-open-clean-draft-reply', staged = edits(), options: { maximumReplays?: number; idempotencyKey?: () => string; navigation?: 'target' | 'in_place' } = {}) {
  let id = 0; const sent: any[] = [], clock = new Clock();
  const client = new BridgeClient({
    subscribe: () => () => {},
    exchange(encoded) {
      const request = decodeRequest(encoded); sent.push(request);
      const method = request.body.type === 'command' ? request.body.command.name : request.body.query.name;
      let reply: any;
      if (method === 'set_draft_changes') reply = frame('sc08-stage-dirty-draft-reply');
      else if (method === 'prepare') reply = frame('sc10-save-reviewed-draft-reply');
      else if (method === 'commit') reply = { protocolVersion: 1, requestId: null, body: { type: 'result', result: { type: 'command', command: { name: 'commit', output: { ...operation(), operationRevision: '1', state: { status: 'admitted' } } } } } };
      else if (method === 'get_operation') reply = frame('sc10-save-verified-result-reply');
      else if (method === 'discard_draft') reply = frame('sc08-discard-draft-reply');
      else throw new Error(method);
      const altered = change?.(request, reply);
      return altered instanceof Promise ? altered : Promise.resolve(correlation(altered ?? reply, encoded));
    },
  }, { requestId: () => (++id).toString(16).padStart(8, '0') + '-2222-4222-8222-222222222222', clock, maximumReplays: options.maximumReplays });
  const facade = new BridgeFacade(client, { idempotencyKey: options.idempotencyKey ?? (() => '00000001-1111-4111-8111-111111111111') });
  if (options.navigation === 'in_place') {
    facade.work.requestTarget(frame('sc09-schema-all-field-types-request').body.query.input.target);
    facade.work.bindTarget(draft(initialDraft).draft.document.target);
  }
  facade.work.openDraft(draft(initialDraft)); facade.stage(staged);
  if (options.navigation === 'target') facade.requestTarget(target(), 'save-opener');
  else if (options.navigation !== 'in_place') facade.requestClose('save-opener');
  return { facade, client, sent, clock };
}

test('facade stages acknowledges reviews explicitly commits and waits for actual completed Save', async () => {
  const { facade, sent } = harness();
  expect(await facade.prepareSave()).toMatchObject({ kind: 'result' });
  expect(facade.state.transition.kind).toBe('review'); expect(facade.state.work.closeRequested).toBe(false);
  expect(sent.map(row => row.body.command.name)).toEqual(['set_draft_changes', 'prepare']);
  expect(canonicalData(sent[0].body.command.input.edits)).toBe(canonicalData(edits()));
  expect(sent[0].body.command.input.draft.revision).toBe('1'); expect(sent[1].body.command.input.intent.input.draft.revision).toBe('2');
  expect(await facade.commitSave()).toMatchObject({ kind: 'result' });
  expect(facade.state.transition.kind).toBe('observing'); expect(facade.state.work.closeRequested).toBe(false);
  expect(facade.state.notice).not.toBe('Changes saved.');
  await facade.reconcileSave(); expect(facade.state.work.closeRequested).toBe(true); expect(facade.state.notice).toBe('Changes saved.');
  facade.dispose();
});

test('facade synchronization refusal retains local edits and queued navigation without preparation', async () => {
  const { facade, sent } = harness(request => request.body.command.name === 'set_draft_changes' ? frame('sc10-save-stale-revision-reply') : undefined);
  await facade.prepareSave(); expect(sent).toHaveLength(1);
  expect(facade.state.work.edits).toEqual(edits()); expect(facade.state.work.pendingNavigation?.kind).toBe('close');
  expect(facade.state.work.transitionBusy).toBe(false); expect(facade.state.work.closeRequested).toBe(false); facade.dispose();
});

test('facade synchronized draft accepts Rust optional None binding while retaining exact submitted edits', async () => {
  const { facade, sent } = harness((request, reply) => {
    if (request.body.type === 'command' && request.body.command.name === 'set_draft_changes') reply.body.result.command.output.snapshot.draft.document.target.profile.ordinaryId = null;
    return reply;
  });
  expect(await facade.prepareSave()).toMatchObject({ kind: 'result' }); expect(facade.state.transition.kind).toBe('review');
  expect(canonicalData(sent[0].body.command.input.edits)).toBe(canonicalData(edits()));
  await facade.commitSave(); await facade.reconcileSave(); expect(facade.state.notice).toBe('Changes saved.'); facade.dispose();
});

test('facade commit timeout retains exact replay and never claims Save or backend cancellation', async () => {
  let lose = true;
  const { facade, sent, clock, client } = harness(request => request.body.type === 'command' && request.body.command.name === 'commit' && lose ? new Promise(() => {}) : undefined);
  await facade.prepareSave(); const pending = facade.commitSave(); clock.expire(); await pending;
  expect(facade.state.transition.kind).toBe('uncertain'); expect(facade.state.work.closeRequested).toBe(false);
  expect(facade.stay()).toBe(false); expect(facade.state.notice).not.toBe('Changes saved.');
  const original = sent.at(-1).body.command.input; expect(client.getReplay(original.idempotencyKey)?.input).toEqual(original);
  lose = false; await facade.replaySave(); expect(sent.map(row => row.body.command.name)).toEqual(['set_draft_changes', 'prepare', 'commit', 'commit']);
  expect(sent.at(-1).body.command.input).toEqual(original); expect(facade.state.work.closeRequested).toBe(false);
  await facade.reconcileSave(); expect(facade.state.work.closeRequested).toBe(true); facade.dispose();
});

test('facade Stay releases only review navigation and restores a connected registered opener', async () => {
  const { facade, sent } = harness(); let focused = 0;
  facade.focus.register('save-opener', () => ({ isConnected: true, focus() { focused++; } }));
  await facade.prepareSave(); expect(facade.stay()).toBe(true);
  expect(focused).toBe(1); expect(facade.state.work.pendingNavigation).toBeUndefined(); expect(facade.state.work.edits).toEqual(edits());
  expect(sent).toHaveLength(2); facade.dispose();
});

test('facade reentrant Stay cannot deliver superseded Save review to a later listener', async () => {
  const { facade, sent, client } = harness(); let closed = false; const shown: string[] = [];
  facade.subscribe(state => { if (!closed && state.transition.kind === 'review') { closed = true; expect(facade.stay()).toBe(true); } });
  facade.subscribe(state => shown.push(state.transition.kind));
  try {
    await facade.prepareSave(); expect(closed).toBe(true);
    expect(facade.state.transition.kind).toBe('idle'); expect(shown.at(-1)).toBe('idle');
    expect(shown.slice(shown.indexOf('idle', shown.indexOf('preparing')))).not.toContain('review');
    expect(facade.state.work.pendingNavigation).toBeUndefined(); expect(facade.state.work.edits).toEqual(edits());
    expect(facade.state.work.transitionBusy).toBe(false); expect(sent.map(row => row.body.command.name)).toEqual(['set_draft_changes', 'prepare']);
  } finally { facade.dispose(); client.dispose(); }
});

test('facade remount during nested Stay publication cannot revive superseded Save review', async () => {
  const { facade, sent, client } = harness(); let remounted = false, stop = () => {}; const shown: string[] = [];
  facade.subscribe(state => {
    if (!remounted && state.transition.kind === 'review') {
      remounted = true; stop(); expect(facade.stay()).toBe(true); stop = facade.subscribe(next => shown.push(next.transition.kind));
    }
  });
  stop = facade.subscribe(() => {});
  try {
    await facade.prepareSave(); expect(remounted).toBe(true); expect(shown.length).toBeGreaterThan(0); expect(new Set(shown)).toEqual(new Set(['idle']));
    expect(facade.state.transition.kind).toBe('idle'); expect(facade.state.work.pendingNavigation).toBeUndefined(); expect(facade.state.work.edits).toEqual(edits());
    expect(facade.state.work.transitionBusy).toBe(false); expect(sent.map(row => row.body.command.name)).toEqual(['set_draft_changes', 'prepare']);
  } finally { stop(); facade.dispose(); client.dispose(); }
});

test('facade unsent initial commit retains review and allows explicit Stay without claiming Save', async () => {
  const { facade, sent, client } = harness(request => {
    if (request.body.command.name === 'commit') throw { code: 'delivery_failed', delivery: 'not_sent' };
  });
  await facade.prepareSave(); expect(await facade.commitSave()).toMatchObject({ kind: 'fault', fault: { delivery: 'not_sent' } });
  expect(facade.state.transition.kind).toBe('review'); expect(facade.state.notice).toBe('Save was not submitted. Changes retained.');
  expect(facade.state.work.closeRequested).toBe(false); expect(client.replayCount).toBe(1); expect(facade.stay()).toBe(true); expect(client.replayCount).toBe(0);
  expect(facade.state.work.edits).toEqual(edits()); expect(sent.map(row => row.body.command.name)).toEqual(['set_draft_changes', 'prepare', 'commit']); facade.dispose();
});

test('facade unsent replay preserves the original uncertain submission and navigation custody', async () => {
  let original = true;
  const { facade, clock } = harness(request => {
    if (request.body.command.name !== 'commit') return;
    if (original) return new Promise(() => {});
    throw { code: 'delivery_failed', delivery: 'not_sent' };
  });
  await facade.prepareSave(); const pending = facade.commitSave(); clock.expire(); await pending; original = false;
  await facade.replaySave(); expect(facade.state.transition.kind).toBe('uncertain'); expect(facade.stay()).toBe(false);
  expect(facade.state.work.closeRequested).toBe(false); expect(facade.state.work.edits).toEqual(edits()); facade.dispose();
});

test('facade sent domain rejection retains exact uncertain custody until authoritative completion', async () => {
  for (const code of ['persistence_failed', 'internal_failure', frame('sc10-save-stale-revision-reply').body.error.code]) {
    let reject = true;
    const { facade, client, sent } = harness(request => request.body.command?.name === 'commit' && reject ? rejected(code) : undefined);
    await facade.prepareSave(); expect(await facade.commitSave()).toMatchObject({ kind: 'rejected', error: { code } });
    const input = sent.at(-1).body.command.input;
    expect(facade.state.transition.kind).toBe('uncertain'); expect(facade.state.work.transitionBusy).toBe(true);
    expect(facade.state.work.pendingNavigation?.kind).toBe('close'); expect(facade.state.work.edits).toEqual(edits());
    expect(facade.stay()).toBe(false); expect(facade.state.work.closeRequested).toBe(false);
    expect(client.getReplay(input.idempotencyKey)?.input).toEqual(input);
    reject = false; expect(await facade.replaySave()).toMatchObject({ kind: 'result' });
    expect(sent.at(-1).body.command.input).toEqual(input); expect(facade.state.work.closeRequested).toBe(false);
    await facade.reconcileSave(); expect(facade.state.notice).toBe('Changes saved.'); expect(client.replayCount).toBe(0); facade.dispose();
  }
});

test('facade post-timeout replay rejection cannot prove the original submission unadmitted', async () => {
  for (const code of ['persistence_failed', 'internal_failure']) {
    let phase: 'timeout' | 'reject' | 'admit' = 'timeout';
    const { facade, client, sent, clock } = harness(request => {
      if (request.body.command?.name !== 'commit') return;
      return phase === 'timeout' ? new Promise(() => {}) : phase === 'reject' ? rejected(code) : undefined;
    });
    await facade.prepareSave(); const pending = facade.commitSave(); clock.expire(); await pending;
    const input = sent.at(-1).body.command.input; phase = 'reject';
    expect(await facade.replaySave()).toMatchObject({ kind: 'rejected', error: { code } });
    expect(facade.state.transition.kind).toBe('uncertain'); expect(facade.state.work.transitionBusy).toBe(true);
    expect(facade.stay()).toBe(false); expect(facade.state.work.pendingNavigation?.kind).toBe('close');
    expect(facade.state.work.closeRequested).toBe(false); expect(client.getReplay(input.idempotencyKey)?.input).toEqual(input);
    phase = 'admit'; await facade.replaySave();
    expect(sent.filter(row => row.body.command?.name === 'commit').map(row => row.body.command.input)).toEqual([input, input, input]);
    await facade.reconcileSave(); expect(facade.state.notice).toBe('Changes saved.'); expect(client.replayCount).toBe(0); facade.dispose();
  }
});

test('facade two authoritative completed Saves reuse a bounded one-entry replay capacity', async () => {
  let operationSequence = 0;
  const { facade, client, sent } = harness((request, reply) => {
    if (request.body.command?.name === 'commit') reply.body.result.command.output = { ...operation(), operationId: (++operationSequence).toString(16).padStart(8, '0') + '-3333-4333-8333-333333333333' };
    return reply;
  }, undefined, undefined, { maximumReplays: 1, idempotencyKey: keySequence(), navigation: 'target' });
  for (let iteration = 0; iteration < 2; iteration++) {
    if (iteration) { expect(facade.work.openDraft(draft('sc08-open-clean-draft-reply'))).toBe(true); facade.stage(edits()); facade.requestTarget(target()); }
    await facade.prepareSave(); expect(await facade.commitSave()).toMatchObject({ kind: 'result' });
    expect(facade.state.transition.kind).toBe('idle'); expect(facade.state.notice).toBe('Changes saved.');
    expect(facade.state.work.pendingNavigation).toBeUndefined(); expect(client.replayCount).toBe(0);
  }
  expect(operationSequence).toBe(2); expect(new Set(sent.filter(row => row.body.command?.name === 'commit').map(row => row.body.command.input.idempotencyKey)).size).toBe(2); facade.dispose();
});

test('facade explicit retry retires only the proved-unsent owned replay before using a fresh key', async () => {
  let unsent = true;
  const { facade, client, sent } = harness(request => {
    if (request.body.command?.name === 'commit' && unsent) throw { code: 'delivery_failed', delivery: 'not_sent' };
  }, undefined, undefined, { maximumReplays: 1, idempotencyKey: keySequence() });
  await facade.prepareSave(); await facade.commitSave(); const original = sent.at(-1).body.command.input;
  expect(client.getReplay(original.idempotencyKey)?.input).toEqual(original); expect(facade.state.transition.kind).toBe('review');
  unsent = false; expect(await facade.commitSave()).toMatchObject({ kind: 'result' });
  expect(client.getReplay(original.idempotencyKey)).toBeUndefined(); expect(client.replayCount).toBe(1);
  expect(sent.at(-1).body.command.input.idempotencyKey).not.toBe(original.idempotencyKey);
  await facade.reconcileSave(); expect(client.replayCount).toBe(0); expect(facade.state.notice).toBe('Changes saved.'); facade.dispose();
});

test('facade replay conflict and Stay preserve another callers preexisting capture', async () => {
  const { facade, client, sent } = harness();
  const planRef = frame('sc10-save-reviewed-draft-reply').body.result.command.output.planRef;
  const unrelated = { idempotencyKey: '00000001-1111-4111-8111-111111111111', planRef: { ...planRef, planId: '00000002-3333-4333-8333-333333333333' } };
  expect(await client.command('commit', unrelated)).toMatchObject({ kind: 'result' }); const retained = client.getReplay(unrelated.idempotencyKey);
  await facade.prepareSave(); expect(await facade.commitSave()).toMatchObject({ kind: 'fault', fault: { code: 'replay_conflict', delivery: 'not_sent' } });
  expect(facade.state.transition.kind).toBe('review'); expect(facade.stay()).toBe(true);
  expect(sent.filter(row => row.body.command?.name === 'commit')).toHaveLength(1); expect(client.getReplay(unrelated.idempotencyKey)).toBe(retained);
  facade.dispose(); expect(client.getReplay(unrelated.idempotencyKey)).toBe(retained);
});

test('facade uncertain disposal retains exact replay while proved-unsent disposal releases owned capture', async () => {
  for (const uncertain of [false, true]) {
    const { facade, client, sent, clock } = harness(request => {
      if (request.body.command?.name !== 'commit') return;
      if (uncertain) return new Promise(() => {});
      throw { code: 'delivery_failed', delivery: 'not_sent' };
    });
    await facade.prepareSave(); const pending = facade.commitSave(); if (uncertain) clock.expire(); await pending;
    const input = sent.at(-1).body.command.input; facade.dispose();
    expect(facade.work.state.edits).toEqual(edits()); expect(facade.work.state.pendingNavigation?.kind).toBe('close');
    expect(facade.work.state.closeRequested).toBe(false); expect(facade.work.state.transitionBusy).toBe(false);
    expect(client.getReplay(input.idempotencyKey)?.input).toEqual(uncertain ? input : undefined);
  }
});

test.each(['pending', 'uncertain', 'admitted', 'not_sent'] as const)('facade proved-unsent cleanup retains a shared later submission: %s', async delivery => {
  for (const cleanup of ['retry', 'stay', 'dispose'] as const) {
    let mode: 'unsent' | typeof delivery = 'unsent', failReplay = () => {};
    const { facade, client, sent } = harness(request => {
      if (request.body.command?.name !== 'commit' || mode === 'admitted') return;
      if (mode === 'unsent' || mode === 'not_sent') throw { code: 'delivery_failed', delivery: 'not_sent' };
      if (mode === 'uncertain') throw { code: 'disconnected', delivery: 'may_have_reached_backend' };
      return new Promise<string>((_resolve, reject) => { failReplay = () => reject({ code: 'disconnected', delivery: 'may_have_reached_backend' }); });
    }, undefined, undefined, { maximumReplays: 2, idempotencyKey: keySequence() });
    try {
      await facade.prepareSave(); await facade.commitSave();
      const input = sent.at(-1).body.command.input, original = client.getReplay(input.idempotencyKey)!;
      mode = delivery; const replay = client.replayCommit(input.idempotencyKey);
      if (delivery === 'uncertain') expect(await replay).toMatchObject({ kind: 'fault', fault: { delivery: 'may_have_reached_backend' } });
      else if (delivery === 'admitted') expect(await replay).toMatchObject({ kind: 'result' });
      else if (delivery === 'not_sent') expect(await replay).toMatchObject({ kind: 'fault', fault: { delivery: 'not_sent' } });
      else expect(client.pendingCount).toBe(1);
      const retained = client.getReplay(input.idempotencyKey)!; expect(retained.request).toBe(original.request);
      const before = sent.length;
      if (cleanup === 'retry') expect(await facade.commitSave()).toBeUndefined();
      else if (cleanup === 'stay') expect(facade.stay()).toBe(false);
      else facade.dispose();
      expect(sent).toHaveLength(before); expect(client.getReplay(input.idempotencyKey)).toBe(retained);
      if (cleanup !== 'dispose') expect(facade.state.transition.kind).toBe('uncertain');
      facade.dispose(); expect(client.getReplay(input.idempotencyKey)).toBe(retained);
      if (delivery === 'pending') { failReplay(); await replay; expect(client.getReplay(input.idempotencyKey)).toBe(retained); }
    } finally { facade.dispose(); client.dispose(); }
  }
});

test('facade terminal reconciliation cannot forget a replacement replay with a different capture', async () => {
  for (const sameInput of [false, true]) {
    const { facade, client, sent } = harness(); await facade.prepareSave(); await facade.commitSave();
    const input = sent.at(-1).body.command.input; client.forgetReplay(input.idempotencyKey);
    const replacement = sameInput ? input : { ...input, planRef: { ...input.planRef, planId: '00000002-3333-4333-8333-333333333333' } };
    expect(await client.command('commit', replacement)).toMatchObject({ kind: 'result' }); const retained = client.getReplay(input.idempotencyKey);
    await facade.reconcileSave(); expect(facade.state.notice).toBe('Changes saved.');
    expect(client.getReplay(input.idempotencyKey)).toBe(retained); expect(retained?.input).toEqual(replacement); facade.dispose();
  }
});

test('facade unsent invocation of a preexisting identical replay cannot prove earlier non-admission', async () => {
  let unsent = false;
  const { facade, client, sent } = harness(request => {
    if (request.body.command?.name === 'commit' && unsent) throw { code: 'delivery_failed', delivery: 'not_sent' };
  });
  const planRef = frame('sc10-save-reviewed-draft-reply').body.result.command.output.planRef;
  const input = { idempotencyKey: '00000001-1111-4111-8111-111111111111', planRef };
  await client.command('commit', input); const retained = client.getReplay(input.idempotencyKey); unsent = true;
  await facade.prepareSave(); await facade.commitSave();
  expect(facade.state.transition.kind).toBe('uncertain'); expect(facade.stay()).toBe(false); expect(client.getReplay(input.idempotencyKey)).toBe(retained);
  expect(sent.filter(row => row.body.command?.name === 'commit')).toHaveLength(2); facade.dispose(); expect(client.getReplay(input.idempotencyKey)).toBe(retained);
});

test('facade exact terminal failure releases replay without claiming Save or releasing queued navigation', async () => {
  let replayUnsent = false;
  const { facade, client } = harness((request, reply) => {
    if (request.body.command?.name === 'commit' && replayUnsent) throw { code: 'delivery_failed', delivery: 'not_sent' };
    if (request.body.query?.name === 'get_operation') reply.body.result.query.output.operation.value.state = frame('sc15-terminal-failed-reply').body.result.query.output.operation.value.state;
    return reply;
  });
  await facade.prepareSave(); await facade.commitSave(); expect(client.replayCount).toBe(1);
  const original = client.getReplay('00000001-1111-4111-8111-111111111111')!; replayUnsent = true;
  expect(await client.replayCommit(original.input.idempotencyKey)).toMatchObject({ kind: 'fault', fault: { delivery: 'not_sent' } });
  expect(client.getReplay(original.input.idempotencyKey)?.request).toBe(original.request); await facade.reconcileSave();
  expect(client.replayCount).toBe(0); expect(facade.state.transition.kind).toBe('idle'); expect(facade.state.work.transitionBusy).toBe(false);
  expect(facade.state.work.closeRequested).toBe(false); expect(facade.state.work.pendingNavigation?.kind).toBe('close');
  expect(facade.state.work.edits).toEqual(edits()); expect(facade.state.notice).not.toBe('Changes saved.'); expect(facade.stay()).toBe(true); facade.dispose();
});

test('facade uncertain replay refuses a replaced key rather than submitting another callers input', async () => {
  let timeout = true;
  const { facade, client, clock, sent } = harness(request => request.body.command?.name === 'commit' && timeout ? new Promise(() => {}) : undefined);
  await facade.prepareSave(); const pending = facade.commitSave(); clock.expire(); await pending; const input = sent.at(-1).body.command.input;
  client.forgetReplay(input.idempotencyKey); timeout = false;
  const replacement = { ...input, planRef: { ...input.planRef, planId: '00000002-3333-4333-8333-333333333333' } };
  await client.command('commit', replacement); const retained = client.getReplay(input.idempotencyKey);
  expect(await facade.replaySave()).toMatchObject({ kind: 'fault', fault: { code: 'replay_conflict', delivery: 'not_sent' } });
  expect(sent.filter(row => row.body.command?.name === 'commit')).toHaveLength(2); expect(facade.state.transition.kind).toBe('uncertain');
  expect(facade.stay()).toBe(false); facade.dispose(); expect(client.getReplay(input.idempotencyKey)).toBe(retained);
});

test('facade contradictory completed operation cannot reconcile an uncertain reviewed submission', async () => {
  const { facade, client } = harness();
  const reply = frame('sc-02-ordinary-failed-observe-reply'); const foreign = reply.body.result.query.output.operation.value;
  foreign.operationId = operation().operationId; decodeReply(JSON.stringify(reply)); expect(facade.work.observations.observeOperation(foreign)).toBe(true);
  await facade.prepareSave(); expect(await facade.commitSave()).toMatchObject({ kind: 'result' });
  expect(facade.state.transition.kind).toBe('uncertain'); expect(client.replayCount).toBe(1); expect(facade.state.work.transitionBusy).toBe(true);
  expect(facade.state.work.closeRequested).toBe(false); expect(facade.stay()).toBe(false); facade.dispose(); expect(client.replayCount).toBe(1);
});

test('facade confirmed Discard releases queued navigation only for the exact reviewed backend draft', async () => {
  const { facade, sent } = harness(undefined, 'sc08-stage-dirty-draft-reply', []);
  expect(facade.state.work.closeRequested).toBe(false); expect(await facade.discard()).toBe(true);
  expect(sent).toHaveLength(1); expect(sent[0].body.command.name).toBe('discard_draft'); expect(sent[0].body.command.input.draft.revision).toBe('2');
  expect(facade.state.work.closeRequested).toBe(true); expect(facade.state.work.edits).toEqual([]); expect(facade.state.notice).toBe('Changes discarded.'); facade.dispose();
});

test('facade foreign Discard receipt retains local edits and queued navigation', async () => {
  const { facade } = harness((_request, reply) => { reply.body.result.command.output.draftId = '00000002-2222-4222-8222-222222222222'; return reply; }, 'sc08-stage-dirty-draft-reply', []);
  expect(await facade.discard()).toBe(false); expect(facade.state.work.transitionBusy).toBe(false);
  expect(facade.state.work.pendingNavigation?.kind).toBe('close'); expect(facade.state.work.closeRequested).toBe(false); expect(facade.state.work.edits).toEqual([]); facade.dispose();
});

test('draft synchronization rejects foreign regressed reused and changed schema acknowledgements', () => {
  for (const change of [
    (value: any) => { value.draft.hostEpoch = '00000002-2222-4222-8222-222222222222'; },
    (value: any) => { value.draft.revision = '0'; },
    (value: any) => { value.draft.revision = '1'; },
    (value: any) => { value.schema.fields[0].deprecated = !value.schema.fields[0].deprecated; },
    (value: any) => { value.edits = []; },
  ]) {
    const work = new WorkContext(); work.openDraft(draft('sc08-open-clean-draft-reply')); work.stage(edits()); work.requestClose();
    const review = work.beginReview()!, reply = frame('sc08-stage-dirty-draft-reply'); change(reply.body.result.command.output.snapshot);
    decodeReply(JSON.stringify(reply));
    expect(work.finishDraftSynchronization(review, { kind: 'result', value: reply.body.result.command.output, request: metadata })).toBeUndefined();
    expect(work.state.edits).toEqual(edits()); expect(work.state.pendingNavigation?.kind).toBe('close'); expect(work.state.transitionBusy).toBe(false);
  }
});

test('late draft synchronization cannot release another captured review generation', () => {
  const work = new WorkContext(); work.openDraft(draft('sc08-open-clean-draft-reply')); work.stage(edits()); work.requestClose();
  const old = work.beginReview()!;
  work.finishDraftSynchronization(old, { kind: 'fault', fault: { code: 'timeout', delivery: 'may_have_reached_backend' } });
  work.stage([]); const current = work.beginReview()!;
  expect(work.finishDraftSynchronization(old, { kind: 'result', value: frame('sc08-stage-dirty-draft-reply').body.result.command.output, request: metadata })).toBeUndefined();
  expect(work.state.transitionBusy).toBe(true); expect(work.state.edits).toEqual([]); expect(current.generation).not.toBe(old.generation);
});

test('facade synchronous disposal during synchronized review publication releases exact updated custody', async () => {
  for (const navigation of ['target', 'in_place'] as const) {
    const { facade, sent } = harness(undefined, undefined, undefined, { navigation });
    const before = facade.work.state;
    const stop = facade.work.subscribe(state => { if (state.transitionBusy && state.draft?.draft.revision === '2') facade.dispose(); });
    expect(await (navigation === 'in_place' ? facade.prepareSaveInPlace() : facade.prepareSave())).toBeUndefined();
    expect(sent.map(row => row.body.command.name)).toEqual(['set_draft_changes']);
    expect(facade.work.state.transitionBusy).toBe(false); expect(facade.work.state.draft?.draft.revision).toBe('2');
    expect(facade.work.state.edits).toEqual(edits()); expect(facade.work.state.pendingNavigation).toBe(before.pendingNavigation);
    expect(facade.work.state.selector).toBe(before.selector); expect(facade.work.state.closeRequested).toBe(false); stop();
  }
});

test('facade synchronized review disposal cleanup preserves a newly captured review owner', async () => {
  const { facade, sent } = harness(); let disposed = false, current: ReturnType<WorkContext['beginReview']>;
  const stop = facade.work.subscribe(state => {
    if (!disposed && state.transitionBusy && state.draft?.draft.revision === '2') { disposed = true; facade.dispose(); }
    else if (disposed && !state.transitionBusy && !current) current = facade.work.beginReview();
  });
  expect(await facade.prepareSave()).toBeUndefined(); expect(current).toBeDefined();
  expect(sent.map(row => row.body.command.name)).toEqual(['set_draft_changes']);
  expect(facade.work.state.transitionBusy).toBe(true); expect(facade.work.state.edits).toEqual(edits());
  stop(); expect(facade.work.abandonReview(current!)).toBe(true); expect(facade.work.state.transitionBusy).toBe(false);
});

test('facade disposal during discard releases local review but ignores a late receipt', async () => {
  let release!: (frame: string) => void;
  const { facade } = harness(request => request.body.command.name === 'discard_draft' ? new Promise<string>(resolve => { release = resolve; }) : undefined);
  const pending = facade.discard(); facade.dispose(); await pending;
  expect(facade.work.state.transitionBusy).toBe(false); expect(facade.work.state.closeRequested).toBe(false);
  release(raw('sc08-discard-draft-reply')); await Promise.resolve(); expect(facade.work.state.edits).toEqual(edits());
});

test('announcements stay bounded and focus refuses disconnected or replaced registrations', () => {
  const messages = new AnnouncementController(), focus = new FocusController(); let invoked = 0;
  messages.announce('Changes retained.'); messages.announce('Changes retained.'); expect(messages.state.polite.id).toBe(2);
  messages.announce('x'.repeat(513)); expect(messages.state.polite.message).toBe('Changes retained.');
  const old = focus.register('opener', () => ({ isConnected: false, focus() { invoked++; } }));
  expect(focus.restore('opener')).toBe(false);
  focus.register('opener', () => ({ isConnected: true, focus() { invoked++; } })); old(); expect(focus.restore('opener')).toBe(true); expect(invoked).toBe(1);
});

const replacementHostEpoch = '00000002-2222-4222-8222-222222222222';
function observeCurrentHost(facade: BridgeFacade) {
  expect(facade.work.observations.acceptSnapshot(frame('sc15-complete-empty-snapshot-reply').body.result.query.output)).toBe(true);
}
function replaceObservedHost(facade: BridgeFacade) {
  facade.work.observations.observeHello(replacementHostEpoch);
  const replacement = frame('sc15-complete-empty-snapshot-reply').body.result.query.output;
  replacement.cursor.hostEpoch = replacementHostEpoch;
  replacement.operations.items = facade.work.observations.state.operations;
  expect(facade.work.observations.acceptSnapshot(replacement)).toBe(true);
}

test('facade known host invalidation refuses new draft Save and Discard without outbound work', async () => {
  const { facade, sent } = harness(); observeCurrentHost(facade);
  const before = facade.state.work; facade.work.observations.observeHello(replacementHostEpoch);
  expect(await facade.prepareSave()).toBeUndefined(); expect(await facade.discard()).toBe(false);
  expect(facade.stage([])).toBe(false); expect(await facade.captureProtectedInput('sync.endpoint')).toBe(false);
  expect(sent).toEqual([]); expect(facade.state.work.draftConflict).toBe(true);
  expect(facade.state.work.draft).toBe(before.draft); expect(facade.state.work.edits).toBe(before.edits);
  expect(facade.state.work.pendingNavigation).toBe(before.pendingNavigation); facade.dispose();
});

test('facade reentrant host invalidation during synchronization publication sends no old draft command', async () => {
  const { facade, sent } = harness(); observeCurrentHost(facade); const before = facade.state.work;
  let changed = false;
  const stop = facade.subscribe(state => {
    if (!changed && state.transition.kind === 'preparing') {
      changed = true; facade.work.observations.observeHello(replacementHostEpoch); facade.work.observations.invalidate('disconnected');
    }
  });
  expect(await facade.prepareSave()).toBeUndefined(); expect(sent).toEqual([]);
  expect(facade.state.transition.kind).toBe('idle'); expect(facade.state.work.transitionBusy).toBe(false); expect(facade.state.work.draftConflict).toBe(true);
  expect(facade.state.work.draft).toBe(before.draft); expect(facade.state.work.edits).toBe(before.edits); stop(); facade.dispose();
});

test('facade matching synchronization reply after host replacement cannot advance old draft review', async () => {
  const { facade, sent } = harness(request => {
    if (request.body.command?.name === 'set_draft_changes') facade.work.observations.observeHello(replacementHostEpoch);
  });
  observeCurrentHost(facade); const before = facade.state.work;
  expect(await facade.prepareSave()).toBeUndefined(); expect(sent.map(row => row.body.command.name)).toEqual(['set_draft_changes']);
  expect(facade.state.work.draft).toBe(before.draft); expect(facade.state.work.edits).toBe(before.edits);
  expect(facade.state.transition.kind).toBe('idle'); expect(facade.state.work.transitionBusy).toBe(false); expect(facade.state.work.closeRequested).toBe(false); facade.dispose();
});

test('facade matching prepared plan after host replacement cannot become a Save review', async () => {
  const { facade, sent } = harness(request => {
    if (request.body.command?.name === 'prepare') facade.work.observations.observeHello(replacementHostEpoch);
  });
  observeCurrentHost(facade);
  expect(await facade.prepareSave()).toMatchObject({ kind: 'fault', fault: { code: 'observational_abort', delivery: 'not_sent' } });
  expect(sent.map(row => row.body.command.name)).toEqual(['set_draft_changes', 'prepare']);
  expect(facade.state.transition.kind).toBe('idle'); expect(facade.state.work.edits).toEqual(edits()); expect(facade.state.work.draftConflict).toBe(true);
  expect(await facade.commitSave()).toBeUndefined(); expect(facade.state.work.closeRequested).toBe(false); facade.dispose();
});

test.each(['review', 'admitting'] as const)('facade host replacement during %s cannot submit a fresh old draft Save', async phase => {
  const { facade, sent, client } = harness(); observeCurrentHost(facade); await facade.prepareSave();
  let stop = () => {};
  if (phase === 'review') facade.work.observations.observeHello(replacementHostEpoch);
  else {
    let changed = false;
    stop = facade.subscribe(state => { if (!changed && state.transition.kind === 'admitting') {
      changed = true; facade.work.observations.observeHello(replacementHostEpoch);
    } });
  }
  await facade.commitSave();
  expect(sent.map(row => row.body.command.name)).toEqual(['set_draft_changes', 'prepare']); expect(client.replayCount).toBe(0);
  expect(facade.state.transition.kind).toBe('review'); expect(facade.state.work.draftConflict).toBe(true); expect(facade.state.work.edits).toEqual(edits());
  expect(facade.stay()).toBe(true); expect(facade.state.work.closeRequested).toBe(false); stop(); facade.dispose();
});

test('facade host replacement prevents reviewed Discard and preserves exact numeric buffers', async () => {
  const { facade, sent } = harness(undefined, undefined, undefined, { navigation: 'in_place' }); observeCurrentHost(facade);
  expect(facade.setPublicInput(facade.capturePublicInput('setting.integer')!, '-')).toBe(true);
  const before = facade.state.work; expect(facade.prepareDiscardInPlace('discard-opener')).toBe(true);
  facade.work.observations.observeHello(replacementHostEpoch);
  expect(await facade.confirmDiscard()).toBe(false); expect(sent).toEqual([]);
  expect(facade.state.work.draft).toBe(before.draft); expect(facade.state.work.edits).toBe(before.edits); expect(facade.state.work.publicInputs).toBe(before.publicInputs);
  expect(facade.stay()).toBe(true); expect(facade.state.work.publicInputs?.[0].text).toBe('-'); facade.dispose();
});

test('facade late old host Discard receipt retains typed edits numeric buffers and queued navigation', async () => {
  const { facade, sent } = harness((request, reply) => {
    if (request.body.command?.name === 'discard_draft') {
      reply.body.result.command.output.previousRevision = request.body.command.input.draft.revision;
      facade.work.observations.observeHello(replacementHostEpoch);
    }
    return reply;
  });
  observeCurrentHost(facade); facade.setPublicInput(facade.capturePublicInput('setting.integer')!, '-'); const before = facade.state.work;
  expect(await facade.discard()).toBe(false); expect(sent.map(row => row.body.command.name)).toEqual(['discard_draft']);
  expect(facade.state.work.draft).toBe(before.draft); expect(facade.state.work.edits).toBe(before.edits); expect(facade.state.work.publicInputs).toBe(before.publicInputs);
  expect(facade.state.work.pendingNavigation).toBe(before.pendingNavigation); expect(facade.state.work.closeRequested).toBe(false); expect(facade.state.work.transitionBusy).toBe(false); facade.dispose();
});

test('facade admitted Save keeps exact replay and reconciles completion after host replacement', async () => {
  const { facade, client, sent } = harness(); observeCurrentHost(facade); await facade.prepareSave(); await facade.commitSave();
  const input = sent.at(-1).body.command.input, retained = client.getReplay(input.idempotencyKey);
  replaceObservedHost(facade);
  expect(facade.state.transition.kind).toBe('observing'); expect(client.getReplay(input.idempotencyKey)).toBe(retained);
  expect(facade.state.work.edits).toEqual(edits()); expect(facade.state.work.closeRequested).toBe(false);
  await facade.reconcileSave(); expect(client.replayCount).toBe(0); expect(facade.state.notice).toBe('Changes saved.');
  expect(facade.state.work.closeRequested).toBe(true); expect(facade.work.observations.state.operations[0].state.status).toBe('completed'); facade.dispose();
});

test('facade uncertain Save replays only its retained exact input after host replacement', async () => {
  let lose = true;
  const { facade, client, sent, clock } = harness(request => request.body.command?.name === 'commit' && lose ? new Promise(() => {}) : undefined);
  observeCurrentHost(facade); await facade.prepareSave(); const pending = facade.commitSave(); clock.expire(); await pending;
  const input = sent.at(-1).body.command.input, retained = client.getReplay(input.idempotencyKey); replaceObservedHost(facade);
  expect(facade.state.transition.kind).toBe('uncertain'); expect(client.getReplay(input.idempotencyKey)).toBe(retained);
  expect(facade.stay()).toBe(false); expect(facade.state.work.edits).toEqual(edits()); lose = false;
  await facade.replaySave(); expect(sent.filter(row => row.body.command?.name === 'commit').map(row => row.body.command.input)).toEqual([input, input]);
  await facade.reconcileSave(); expect(client.replayCount).toBe(0); expect(facade.state.notice).toBe('Changes saved.'); expect(facade.state.work.closeRequested).toBe(true); facade.dispose();
});
