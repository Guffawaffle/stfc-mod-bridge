import { expect, test } from 'vitest';
import { stageAcknowledgement } from '../../../contracts/fixtures/configuration-cases';
import { readFileSync } from 'node:fs';
import { BridgeClient, WorkContext, canonicalData, decodeReply, decodeRequest, type ClientClock } from '../../src/client';
import { semanticPlanDigest } from '../../src/client/relations';
import { BridgeFacade } from '../../src/state';
import type { DraftSnapshot } from '../../src/generated/protocol';

// Private test oracle, derived from shared synthetic fixtures. It describes
// typed observations/delivery, not native persistence or protected-input policy.
const fixtures = new URL('../../../contracts/fixtures/', import.meta.url);
const source = (name: string): any => JSON.parse(readFileSync(new URL(name + '.json', fixtures), 'utf8'));
const clean = () => source('sc08-open-clean-draft-reply').body.result.command.output;
const edits = () => source('sc08-stage-dirty-draft-request').body.command.input.edits;
const planSource = () => source('sc10-save-reviewed-draft-reply').body.result.command.output;
const operationSource = () => source('sc10-save-verified-result-reply').body.result.query.output.operation.value;
const documentSource = () => source('sc09-schema-all-field-types-reply').body.result.query.output;
const selector = () => source('sc09-schema-all-field-types-request').body.query.input.target;
const clone = <T>(value: T): T => structuredClone(value);
const id = (value: number) => value.toString(16).padStart(8, '0') + '-4444-4444-8444-444444444444';
const method = (request: any): string => (request.body.command ?? request.body.query).name;
const input = (request: any): any => (request.body.command ?? request.body.query).input;
function encoded(reply: any, request: any): string {
  const text = JSON.stringify({ ...reply, requestId: request.requestId }); decodeReply(text); return text;
}
function rejected(): any {
  const reply = source('sc10-save-stale-revision-reply'); reply.body.error.code = 'persistence_failed';
  reply.body.error.retryDisposition = 'never'; reply.body.error.violations = []; return reply;
}
class Clock implements ClientClock {
  private tasks = new Set<() => void>();
  schedule(_ms: number, callback: () => void) { this.tasks.add(callback); return () => { this.tasks.delete(callback); }; }
  expire() { for (const task of [...this.tasks]) task(); }
}
type Hooks = {
  before?: (request: any) => any | Promise<any>;
  after?: (request: any, reply: any) => any | Promise<any>;
};
function harness(hooks: Hooks = {}, options: { noChange?: boolean; maximumReplays?: number; idempotencyKey?: () => string } = {}) {
  const clock = new Clock(), sent: any[] = [], operations = new Map<string, any>();
  let sequence = 10, draft = clean(), plan: any, baseline = documentSource();
  const client = new BridgeClient({
    subscribe: () => () => {},
    async exchange(raw) {
      const request = decodeRequest(raw); sent.push(request);
      const injected = await hooks.before?.(request);
      if (injected !== undefined) return typeof injected === 'string' ? injected : encoded(injected, request);
      let reply: any;
      switch (method(request)) {
        case 'set_draft_changes': {
          const submitted = input(request);
          const candidate = { ...clone(draft), edits: clone(submitted.edits),
            apply: [...new Set<string>(submitted.edits.map((edit: any) => 'fieldId' in edit ? draft.schema.fields.find((field: any) => field.fieldId === edit.fieldId)?.apply ?? 'next_launch' : 'next_launch'))].sort(),
            state: submitted.edits.length ? 'dirty' : 'clean', validation: [] };
          const acknowledgement = stageAcknowledgement(submitted.draft, candidate as DraftSnapshot);
          draft = clone(acknowledgement.snapshot);
          reply = source('sc08-stage-dirty-draft-reply'); reply.body.result.command.output = acknowledgement; break;
        }
        case 'prepare': {
          plan = planSource(); plan.semantics.capture.input.draft = clone(draft);
          plan.planRef.planId = id(sequence++); plan.planRef.reviewDigest = await semanticPlanDigest(plan.semantics);
          reply = source('sc10-save-reviewed-draft-reply'); reply.body.result.command.output = clone(plan); break;
        }
        case 'commit': {
          const key = input(request).idempotencyKey;
          if (!operations.has(key)) operations.set(key, { ...operationSource(), operationId: id(sequence++),
            semantics: clone(plan.semantics), operationRevision: '1', state: { status: 'admitted' } });
          reply = source('sc10-save-reviewed-draft-reply');
          reply.body.result.command = { name: 'commit', output: clone(operations.get(key)) }; break;
        }
        case 'get_operation': {
          const operation = [...operations.values()].find(row => row.operationId === input(request).operationId);
          if (!operation) throw new Error('test requested a foreign operation');
          operation.operationRevision = '2';
          operation.state = options.noChange
            ? source('sc15-terminal-no-change-reply').body.result.query.output.operation.value.state
            : operationSource().state;
          if (draft.draft.document.revision !== operation.state.outcome.receipt?.document.revision && !options.noChange) {
            draft = { ...draft, draft: { ...draft.draft, revision: (BigInt(draft.draft.revision) + 1n).toString(), document: clone(operation.state.outcome.receipt.document) }, edits: [], apply: [], validation: [], state: 'clean' };
          } else if (options.noChange && draft.edits.length) {
            draft = { ...draft, draft: { ...draft.draft, revision: (BigInt(draft.draft.revision) + 1n).toString() }, edits: [], apply: [], validation: [], state: 'clean' };
          }
          baseline = documentSource();
          if (!options.noChange) {
            baseline.value.binding = clone(operation.state.outcome.receipt.document);
            baseline.value.fields[0] = { ...baseline.value.fields[0], overridden: true, value: { kind: 'public', value: { kind: 'boolean', value: true } } };
          }
          reply = source('sc10-save-verified-result-reply'); reply.body.result.query.output.operation.value = clone(operation); break;
        }
        case 'discard_draft': {
          reply = source('sc08-discard-draft-reply');
          reply.body.result.command.output = { draftId: input(request).draft.draftId, hostEpoch: input(request).draft.hostEpoch, previousRevision: input(request).draft.revision };
          break;
        }
        case 'read_configuration': {
          reply = source('sc09-schema-all-field-types-reply'); reply.body.result.query.output = clone(baseline); break;
        }
        case 'get_draft': {
          reply = source('sc08-get-current-clean-draft-reply');
          reply.body.result.query.output.cursor = { ...source('sc15-complete-empty-snapshot-reply').body.result.query.output.cursor, sequence: '3' };
          reply.body.result.query.output.draft.value = clone(draft); break;
        }
        case 'open_draft': {
          draft = { ...clean(), draft: { ...clean().draft, draftId: id(sequence++), document: clone(input(request).document) } };
          reply = source('sc08-open-clean-draft-reply'); reply.body.result.command.output = clone(draft); break;
        }
        default: throw new Error('unexpected test exchange ' + method(request));
      }
      const altered = await hooks.after?.(request, reply);
      return typeof altered === 'string' ? altered : encoded(altered ?? reply, request);
    },
  }, { clock, requestId: () => id(sequence++), maximumReplays: options.maximumReplays });
  const facade = new BridgeFacade(client, { idempotencyKey: options.idempotencyKey ?? (() => id(sequence++)) });
  facade.work.observations.acceptSnapshot(source('sc15-complete-empty-snapshot-reply').body.result.query.output);
  facade.work.requestTarget(selector()); facade.work.bindTarget(clean().draft.document.target);
  facade.work.navigate('settings'); facade.work.openDraft(clean()); facade.work.observations.observeDraft(clean());
  facade.stage(options.noChange ? [] : edits());
  return { facade, client, clock, sent };
}

test('in-place review captures purpose without queued navigation and rejects foreign review ownership', () => {
  const { facade } = harness(), work = facade.work, review = work.beginReview('in_place')!;
  expect(review.purpose.kind).toBe('in_place'); expect(work.state.pendingNavigation).toBeUndefined();
  expect(work.stage([])).toBe(false); expect(work.requestClose()).toBe(false);
  expect(work.abandonReview({ ...review })).toBe(false); expect(work.state.transitionBusy).toBe(true);
  expect(work.abandonReview(review)).toBe(true); expect(work.state.edits).toEqual(edits()); facade.dispose();
});

test('in-place Save requires explicit confirmation and admission retains the selected draft and view', async () => {
  const { facade, sent } = harness(); const selected = facade.work.state.selector, binding = facade.work.state.binding;
  expect(await facade.prepareSaveInPlace()).toMatchObject({ kind: 'result' });
  expect(facade.state.reviewPurpose).toBe('in_place'); expect(facade.state.transition.kind).toBe('review');
  expect(sent.map(method)).toEqual(['set_draft_changes', 'prepare']); expect(facade.stage([])).toBe(false);
  const admitted = await facade.commitSave(); expect(admitted?.kind, JSON.stringify(admitted)).toBe('result'); expect(facade.state.transition.kind).toBe('observing'); expect(facade.state.notice).not.toBe('Changes saved.');
  expect(facade.work.state.selector).toBe(selected); expect(facade.work.state.binding).toBe(binding);
  expect(facade.work.state.view).toBe('settings'); expect(facade.work.state.draft).toBeDefined();
  expect(facade.work.state.pendingNavigation).toBeUndefined(); expect(facade.work.state.closeRequested).toBe(false); facade.dispose();
});

test('completed in-place Save reopens only the backend-observed document binding under the original target', async () => {
  const { facade, client, sent } = harness(); const selected = facade.work.state.selector, old = facade.work.state.draft!;
  await facade.prepareSaveInPlace(); await facade.commitSave(); await facade.reconcileSave();
  expect(facade.state.notice).toBe('Changes saved.'); expect(facade.state.transition.kind).toBe('idle');
  expect(facade.work.state.selector).toBe(selected); expect(facade.work.state.view).toBe('settings');
  expect(facade.work.state.draft!.draft.draftId).not.toBe(old.draft.draftId);
  expect(facade.work.state.draft!.draft.document).toEqual(operationSource().state.outcome.receipt.document);
  expect(input(sent.find(row => method(row) === 'read_configuration'))).toEqual({ target: selected });
  expect(input(sent.find(row => method(row) === 'open_draft'))).toEqual({ document: operationSource().state.outcome.receipt.document });
  expect(facade.work.state.edits).toEqual([]); expect(client.replayCount).toBe(0); facade.dispose();
});

test('authoritative in-place no-change completion reopens the observed virtual baseline without fabricating a file', async () => {
  const { facade, sent } = harness({}, { noChange: true });
  await facade.prepareSaveInPlace(); await facade.commitSave(); await facade.reconcileSave();
  expect(facade.state.notice).toBe('Changes saved.'); expect(facade.work.state.draft!.draft.document.baseline.kind).toBe('missing');
  expect(input(sent.find(row => method(row) === 'open_draft')).document).toEqual(clean().draft.document);
  expect(facade.work.state.closeRequested).toBe(false); facade.dispose();
});

test('in-place sent rejection and replay rejection retain exact uncertain custody until authoritative completion', async () => {
  let refuse = true;
  const { facade, client, sent } = harness({ before: request => method(request) === 'commit' && refuse ? rejected() : undefined });
  await facade.prepareSaveInPlace(); await facade.commitSave(); const retained = input(sent.at(-1));
  await facade.replaySave(); expect(facade.state.transition.kind).toBe('uncertain');
  expect(facade.stay()).toBe(false); expect(facade.work.state.transitionBusy).toBe(true);
  expect(client.getReplay(retained.idempotencyKey)?.input).toEqual(retained);
  expect(sent.filter(row => method(row) === 'read_configuration')).toHaveLength(0);
  refuse = false; await facade.replaySave(); await facade.reconcileSave();
  expect(sent.filter(row => method(row) === 'commit').map(input)).toEqual([retained, retained, retained]);
  expect(facade.state.notice).toBe('Changes saved.'); expect(client.replayCount).toBe(0); facade.dispose();
});

test('in-place timeout and proved-unsent replay cannot release uncertainty or permit Stay', async () => {
  let lose = true;
  const { facade, clock, sent, client } = harness({ before: request => {
    if (method(request) !== 'commit') return;
    if (lose) return new Promise(() => {}); throw { code: 'delivery_failed', delivery: 'not_sent' };
  } });
  await facade.prepareSaveInPlace(); const pending = facade.commitSave(); await Promise.resolve(); clock.expire(); await pending;
  const retained = input(sent.at(-1)); lose = false; await facade.replaySave();
  expect(facade.state.transition.kind).toBe('uncertain'); expect(facade.stay()).toBe(false);
  facade.dispose(); expect(client.getReplay(retained.idempotencyKey)?.input).toEqual(retained);
  expect(facade.work.state.edits).toEqual(edits()); expect(facade.work.state.draft).toBeDefined();
});

test('proved-unsent in-place Stay releases only its owned replay and restores the registered opener', async () => {
  const { facade, client } = harness({ before: request => { if (method(request) === 'commit') throw { code: 'delivery_failed', delivery: 'not_sent' }; } });
  let focused = 0; facade.focus.register('settings-save', () => ({ isConnected: true, focus() { focused++; } }));
  await facade.prepareSaveInPlace({}, 'settings-save'); await facade.commitSave();
  facade.navigate('data_sync');
  expect(client.replayCount).toBe(1); expect(facade.stay()).toBe(true); expect(focused).toBe(1);
  expect(client.replayCount).toBe(0); expect(facade.work.state.edits).toEqual(edits());
  expect(facade.work.state.view).toBe('data_sync'); expect(facade.work.state.draft).toBeDefined(); facade.dispose();
});

test('two completed in-place Saves retain bounded one-entry replay capacity', async () => {
  const { facade, client } = harness({}, { maximumReplays: 1, noChange: true });
  for (let round = 0; round < 2; round++) {
    if (round) facade.stage([]);
    await facade.prepareSaveInPlace(); expect(await facade.commitSave()).toMatchObject({ kind: 'result' }); await facade.reconcileSave();
    expect(client.replayCount).toBe(0); expect(facade.work.state.draft).toBeDefined(); expect(facade.state.notice).toBe('Changes saved.');
  }
  facade.dispose();
});

test('in-place Discard reviews before delivery and Stay sends no discard command', async () => {
  const { facade, sent } = harness(); const draft = facade.work.state.draft;
  expect(await facade.confirmDiscard()).toBe(false); expect(facade.prepareDiscardInPlace()).toBe(true);
  expect(facade.state.transition.kind).toBe('discard_review'); expect(facade.stage([])).toBe(false);
  expect(facade.stay()).toBe(true); expect(sent).toHaveLength(0); expect(facade.work.state.draft).toBe(draft);
  expect(facade.work.state.edits).toEqual(edits()); facade.dispose();
});

test('confirmed in-place Discard preserves selection and view then reopens the authoritative baseline', async () => {
  const { facade, sent } = harness(); const selected = facade.work.state.selector;
  facade.prepareDiscardInPlace(); expect(await facade.confirmDiscard()).toBe(true);
  expect(sent.map(method)).toEqual(['discard_draft', 'read_configuration', 'open_draft']);
  expect(facade.work.state.selector).toBe(selected); expect(facade.work.state.view).toBe('settings');
  expect(facade.work.state.dirty).toBe(false); expect(facade.work.state.closeRequested).toBe(false);
  expect(facade.state.notice).toBe('Changes discarded.'); facade.dispose();
});

test('foreign in-place Discard receipt preserves the draft and sends no baseline reopen', async () => {
  const { facade, sent } = harness({ after: (request, reply) => { if (method(request) === 'discard_draft') reply.body.result.command.output.previousRevision = '9'; } });
  const draft = facade.work.state.draft; facade.prepareDiscardInPlace(); expect(await facade.confirmDiscard()).toBe(false);
  expect(facade.work.state.draft).toBe(draft); expect(facade.work.state.edits).toEqual(edits());
  expect(sent.map(method)).toEqual(['discard_draft']); expect(facade.work.state.transitionBusy).toBe(false); facade.dispose();
});

test('failed baseline observation preserves confirmed Save while exposing unavailable editor data and explicit retry', async () => {
  let unavailable = true;
  const { facade, sent } = harness({ after: (request, reply) => {
    if (method(request) === 'read_configuration' && unavailable) reply.body.result.query.output = { status: 'unavailable', reason: 'native_unavailable' };
  } });
  await facade.prepareSaveInPlace(); await facade.commitSave(); await facade.reconcileSave();
  expect(facade.work.state.draft).toBeUndefined(); expect(facade.work.state.baselineRefreshRequired).toBe(true);
  expect(facade.work.state.transitionBusy).toBe(false); expect(facade.state.notice).toContain('Changes saved. Current settings are unavailable.');
  expect(facade.stage(edits())).toBe(false); expect(sent.filter(row => method(row) === 'open_draft')).toHaveLength(0);
  unavailable = false; expect(await facade.reloadBaseline()).toBe(true); expect(facade.work.state.baselineRefreshRequired).toBe(false); facade.dispose();
});

test('foreign baseline target refuses reopening without fabricating a draft or changing the selected target', async () => {
  const { facade, sent } = harness({ after: (request, reply) => {
    if (method(request) === 'read_configuration') reply.body.result.query.output.value.binding.target.installation.physicalId = 'synthetic-foreign-installation';
  } });
  const selected = facade.work.state.selector; await facade.prepareSaveInPlace(); await facade.commitSave(); await facade.reconcileSave();
  expect(facade.work.state.selector).toBe(selected); expect(facade.work.state.draft).toBeUndefined();
  expect(facade.work.state.baselineRefreshRequired).toBe(true); expect(sent.filter(row => method(row) === 'open_draft')).toHaveLength(0); facade.dispose();
});

test('in-place double input cannot start competing Save Discard or action preparations', async () => {
  let release!: (reply: any) => void;
  const { facade, sent } = harness({ after: (request, reply) => method(request) === 'set_draft_changes' ? new Promise(resolve => { release = () => resolve(reply); }) : undefined });
  const pending = facade.prepareSaveInPlace();
  expect(await facade.prepareSaveInPlace()).toBeUndefined(); expect(facade.prepareDiscardInPlace()).toBe(false);
  expect(facade.stage([])).toBe(false); expect(facade.requestClose()).toBe(false);
  expect(await facade.actions.prepare(source('sc-02-ordinary-absent-prepare-request').body.command.input.intent)).toBeUndefined();
  for (let turn = 0; turn < 8 && !release; turn++) await Promise.resolve();
  expect(release).toBeTypeOf('function'); release(undefined); await pending; expect(sent.map(method)).toEqual(['set_draft_changes', 'prepare']); facade.dispose();
});

test('reentrant disposal during reopened draft observation cannot adopt late editor state', async () => {
  const { facade } = harness();
  const stop = facade.work.observations.subscribe(state => {
    if (state.drafts.some(row => row.draft.draftId !== clean().draft.draftId)) facade.dispose();
  });
  await facade.prepareSaveInPlace(); await facade.commitSave(); await facade.reconcileSave();
  expect(facade.work.state.draft).toBeUndefined(); expect(facade.work.state.baselineRefreshRequired).toBe(true);
  expect(facade.work.state.transitionBusy).toBe(false); stop();
});

test('reentrant host replacement during baseline draft observation preserves confirmed Save and required refresh', async () => {
  const { facade, client, sent } = harness(), selected = facade.work.state.selector, binding = facade.work.state.binding;
  expect(facade.work.observations.acceptSnapshot(source('sc15-complete-empty-snapshot-reply').body.result.query.output)).toBe(true);
  let replaced = false;
  const stop = facade.work.observations.subscribe(state => {
    if (!replaced && state.drafts.some(row => row.draft.draftId !== clean().draft.draftId)) {
      replaced = true; facade.work.observations.observeHello(id(995));
      // Disconnect may overwrite the reason while the cursor still names the old host.
      facade.work.observations.invalidate('disconnected');
    }
  });
  await facade.prepareSaveInPlace(); await facade.commitSave(); await facade.reconcileSave();
  expect(replaced).toBe(true); expect(facade.work.state.draft).toBeUndefined();
  expect(facade.work.state.baselineRefreshRequired).toBe(true); expect(facade.work.state.transitionBusy).toBe(false);
  expect(facade.work.state.selector).toBe(selected); expect(facade.work.state.binding).toBe(binding);
  expect(facade.state.notice).toContain('Changes saved. Current settings are unavailable.'); expect(client.replayCount).toBe(0);
  expect(facade.work.observations.state.operations).toMatchObject([{ state: { status: 'completed', outcome: { kind: 'changed' } } }]);
  expect(sent.map(method)).toEqual(['set_draft_changes', 'prepare', 'commit', 'get_operation', 'get_draft', 'read_configuration', 'open_draft']);
  expect(facade.stage(edits())).toBe(false); stop(); facade.dispose();
});

test('reentrant target binding replacement during baseline draft observation preserves confirmed Save and required refresh', async () => {
  const { facade, client } = harness(), selected = facade.work.state.selector;
  const replacement = clone(clean().draft.document.target); replacement.installation.physicalId = 'synthetic-reentrant-installation';
  let replaced = false;
  const stop = facade.work.observations.subscribe(state => {
    if (!replaced && state.drafts.some(row => row.draft.draftId !== clean().draft.draftId)) {
      replaced = true; facade.work.bindTarget(replacement);
    }
  });
  await facade.prepareSaveInPlace(); await facade.commitSave(); await facade.reconcileSave();
  expect(replaced).toBe(true); expect(facade.work.state.draft).toBeUndefined();
  expect(facade.work.state.baselineRefreshRequired).toBe(true); expect(facade.work.state.transitionBusy).toBe(false);
  expect(facade.work.state.selector).toBe(selected); expect(facade.work.state.binding).toEqual(replacement);
  expect(facade.state.notice).toContain('Changes saved. Current settings are unavailable.'); expect(client.replayCount).toBe(0);
  expect(facade.work.observations.state.operations).toMatchObject([{ state: { status: 'completed', outcome: { kind: 'changed' } } }]);
  expect(facade.stage(edits())).toBe(false);
  facade.work.bindTarget(clean().draft.document.target); expect(await facade.reloadBaseline()).toBe(true);
  expect(facade.work.state.baselineRefreshRequired).toBe(false); expect(facade.state.notice).toBe('Changes saved.'); stop(); facade.dispose();
});

test('in-place baseline queries never manufacture an event cursor or resnapshot confidence', async () => {
  const { facade } = harness(); const before = facade.work.observations.state;
  await facade.prepareSaveInPlace(); await facade.commitSave(); await facade.reconcileSave();
  expect(facade.work.observations.state.cursor).toEqual(before.cursor);
  expect(facade.work.observations.state.confidence).toBe(before.confidence); facade.dispose();
});

test('in-place synchronization refuses an omitted protected-reference transfer and retains the old draft', async () => {
  const protectedValue = source('sc09-protected-private-entry-reply').body.result.command.output.outcome.reference;
  const { facade, sent } = harness({ after: (request, reply) => {
    if (method(request) === 'set_draft_changes') reply.body.result.command.output.protectedTransfers = [];
  } });
  const staged = [{ kind: 'set_private' as const, fieldId: protectedValue.fieldId, reference: protectedValue }];
  facade.stage(staged); expect(await facade.prepareSaveInPlace()).toBeUndefined();
  expect(facade.work.state.edits).toEqual(staged); expect(sent.map(method)).toEqual(['set_draft_changes']);
  expect(facade.work.state.draft).toBeDefined(); expect(facade.work.state.transitionBusy).toBe(false); facade.dispose();
});

test('in-place replay conflict and Stay preserve another callers preexisting uncertain capture', async () => {
  const key = id(999);
  const { facade, client, sent } = harness({ before: request => method(request) === 'commit' ? rejected() : undefined }, { idempotencyKey: () => key });
  const prepared = await facade.prepareSaveInPlace(); expect(prepared?.kind).toBe('result');
  if (prepared?.kind !== 'result') throw new Error('test review missing');
  const other = { idempotencyKey: key, planRef: { ...prepared.value.planRef, planId: id(998) } };
  await client.command('commit', other); const retained = client.getReplay(key);
  expect(await facade.commitSave()).toMatchObject({ kind: 'fault', fault: { code: 'replay_conflict', delivery: 'not_sent' } });
  expect(facade.stay()).toBe(true); expect(sent.filter(row => method(row) === 'commit')).toHaveLength(1);
  expect(client.getReplay(key)).toBe(retained); facade.dispose(); expect(client.getReplay(key)).toBe(retained);
});

test('in-place terminal failure retains the reviewed draft and never starts a baseline reopen', async () => {
  const { facade, client, sent } = harness({ after: (request, reply) => {
    if (method(request) === 'get_operation') reply.body.result.query.output.operation.value.state = source('sc15-terminal-failed-reply').body.result.query.output.operation.value.state;
  } });
  await facade.prepareSaveInPlace(); const draft = facade.work.state.draft;
  await facade.commitSave(); await facade.reconcileSave();
  expect(facade.work.state.draft).toBe(draft); expect(facade.work.state.edits).toEqual(edits());
  expect(facade.work.state.view).toBe('settings'); expect(facade.work.state.baselineRefreshRequired).toBe(false);
  expect(facade.state.notice).not.toBe('Changes saved.'); expect(sent.filter(row => method(row) === 'read_configuration')).toHaveLength(0);
  expect(client.replayCount).toBe(0); facade.dispose();
});

test('a late baseline token cannot release or replace a newer observation owner', () => {
  const { facade } = harness(), work = facade.work, initial = work.beginReview('in_place')!;
  const acknowledged = stageAcknowledgement(initial.draft.draft, JSON.parse(JSON.stringify({ ...initial.draft, edits: initial.edits, state: 'dirty', apply: ['immediate'] })));
  const request = { requestId: id(990), kind: 'command' as const, method: 'commit' as const };
  const review = work.finishDraftSynchronization(initial, { kind: 'result', value: acknowledged, request })!;
  work.observations.observeDraft(review.draft);
  const operation = operationSource();
  operation.semantics.capture.input.draft = clone(review.draft);
  work.observations.observeOperation(operation);
  work.observations.observeDraft({ ...review.draft, draft: { ...review.draft.draft, revision: (BigInt(review.draft.draft.revision) + 1n).toString(), document: operation.state.outcome.receipt.document }, edits: [], apply: [], validation: [], state: 'clean' });
  expect(work.finishSave(review, { kind: 'result', value: operation, request })).toBe(true);
  const old = work.beginBaselineReopen()!;
  work.finishBaselineReopen(old, { kind: 'fault', fault: { code: 'timeout', delivery: 'may_have_reached_backend' } });
  const current = work.beginBaselineReopen()!;
  const reopened = { ...clean(), draft: { ...clean().draft, draftId: id(991), document: operation.state.outcome.receipt.document } };
  expect(work.finishBaselineReopen(old, { kind: 'result', value: reopened, request })).toBe(false);
  expect(work.state.transitionBusy).toBe(true); expect(work.state.draft).toBeUndefined();
  expect(work.finishBaselineReopen(current, { kind: 'result', value: reopened, request })).toBe(true);
  expect(work.state.transitionBusy).toBe(false); facade.dispose();
});

test('synchronous disposal before the facade records a new review releases only that review owner', async () => {
  const { facade, sent } = harness();
  const stop = facade.work.subscribe(state => { if (state.transitionBusy && state.draft) facade.dispose(); });
  expect(await facade.prepareSaveInPlace()).toBeUndefined();
  expect(sent).toHaveLength(0); expect(facade.work.state.transitionBusy).toBe(false);
  expect(facade.work.state.edits).toEqual(edits()); stop();
});

test('synchronous disposal at confirmed completion releases the reserved baseline observation without reopening', async () => {
  const { facade, sent } = harness(); await facade.prepareSaveInPlace(); await facade.commitSave();
  const stop = facade.work.subscribe(state => { if (state.baselineRefreshRequired && !state.draft) facade.dispose(); });
  await facade.reconcileSave();
  expect(facade.work.state.transitionBusy).toBe(false); expect(facade.work.state.draft).toBeUndefined();
  expect(facade.work.state.baselineRefreshRequired).toBe(true);
  expect(sent.filter(row => method(row) === 'read_configuration')).toHaveLength(0); stop();
});
