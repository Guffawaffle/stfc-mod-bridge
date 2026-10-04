import { expect, test } from 'vitest';
import { stageAcknowledgement } from '../../../contracts/fixtures/configuration-cases';
import { readFileSync } from 'node:fs';
import { BridgeClient, WorkContext, PUBLIC_INPUT_LIMITS, canonicalData, decodeReply, decodeRequest, type ClientOutcome, type PublicInputBinding } from '../../src/client';
import type { ConfigurationEdit, DraftSnapshot } from '../../src/generated/protocol';
import { BridgeFacade } from '../../src/state';

// Synthetic public presentation lifetime tests, derived from shared wire facts.
// No assertion below qualifies native persistence, producer schema or TOML policy.
const fixtures = new URL('../../../contracts/fixtures/', import.meta.url);
const source = (name: string): any => JSON.parse(readFileSync(new URL(name + '.json', fixtures), 'utf8'));
const clean = (): DraftSnapshot => source('sc08-open-clean-draft-reply').body.result.command.output;
const selector = () => source('sc09-schema-all-field-types-request').body.query.input.target;
const otherTarget = () => source('sc-03-profile-one-prepare-request').body.command.input.intent.input.target;
const clone = <T>(value: T): T => structuredClone(value);
const method = (request: any): string => (request.body.command ?? request.body.query).name;
const input = (request: any): any => (request.body.command ?? request.body.query).input;
const numeric = (fieldId: string, kind: 'integer' | 'number', value: string): ConfigurationEdit => ({ kind: 'set_public', fieldId, value: { kind, value } });
const buffered = (work: WorkContext, fieldId: string, text: string): PublicInputBinding => {
  expect(work.setPublicInput(work.capturePublicInput(fieldId)!, text)).toBe(true);
  return work.capturePublicInput(fieldId)!;
};
const abandoned = (): ClientOutcome<any> => ({ kind: 'fault', fault: { code: 'delivery_failed', delivery: 'not_sent' } });
function encoded(reply: any, request: any): string {
  const text = JSON.stringify({ ...reply, requestId: request.requestId }); decodeReply(text); return text;
}
function harness(options: { draft?: DraftSnapshot; discard?: 'foreign' | 'refused'; hold?: boolean; invalidSynchronization?: boolean } = {}) {
  let sequence = 80;
  const sent: any[] = [], draft = options.draft ?? clean();
  const work = new WorkContext();
  const client = new BridgeClient({
    subscribe: () => () => {},
    async exchange(raw) {
      const request = decodeRequest(raw); sent.push(request);
      if (options.hold) return new Promise<string>(() => {});
      let reply: any;
      switch (method(request)) {
        case 'discard_draft':
          if (options.discard === 'refused') reply = source('sc10-save-stale-revision-reply');
          else {
            reply = source('sc08-discard-draft-reply');
            reply.body.result.command.output = { draftId: input(request).draft.draftId, hostEpoch: input(request).draft.hostEpoch,
              previousRevision: options.discard === 'foreign' ? '99' : input(request).draft.revision };
          }
          break;
        case 'set_draft_changes':
          reply = source('sc08-stage-dirty-draft-reply');
          reply.body.result.command.output = stageAcknowledgement(input(request).draft, { ...clone(draft), draft: { ...clone(input(request).draft), revision: (BigInt(input(request).draft.revision) + 1n).toString() },
            edits: clone(input(request).edits), apply: ['next_launch'], state: options.invalidSynchronization ? 'invalid' : 'dirty',
            validation: options.invalidSynchronization ? [{ code: 'constraint_violation', fieldId: 'setting.integer' }] : [] });
          break;
        case 'read_configuration':
          reply = source('sc09-schema-all-field-types-reply'); break;
        case 'open_draft':
          reply = source('sc08-open-clean-draft-reply');
          reply.body.result.command.output = { ...clean(), draft: { ...clean().draft, draftId: '00000999-1111-4111-8111-111111111111', document: clone(input(request).document) } };
          break;
        default: throw new Error('unexpected synthetic input-lifetime exchange ' + method(request));
      }
      return encoded(reply, request);
    },
  }, { requestId: () => (sequence++).toString(16).padStart(8, '0') + '-4444-4444-8444-444444444444' });
  work.requestTarget(selector()); work.bindTarget(draft.draft.document.target); work.navigate('settings');
  work.openDraft(draft); work.observations.observeDraft(draft);
  const facade = new BridgeFacade(client, { work, idempotencyKey: () => '00000998-1111-4111-8111-111111111111' });
  return { facade, work, client, sent };
}

test('incomplete public numeric text makes an otherwise clean draft dirty without inventing typed edits', () => {
  const { facade, work } = harness(); buffered(work, 'setting.integer', '-');
  expect(work.state.dirty).toBe(true); expect(work.state.edits).toEqual([]);
  expect(work.state.publicInputs).toMatchObject([{ text: '-', binding: { draft: clean().draft, field: { fieldId: 'setting.integer', sensitivity: 'public' } } }]);
  expect(Object.isFrozen(work.state.publicInputs)).toBe(true); facade.dispose();
});

test('Settings and Data Sync view navigation retains the exact incomplete numeric input and scope', () => {
  const { facade, work } = harness(); const binding = buffered(work, 'setting.integer', '-'), buffers = work.state.publicInputs;
  facade.navigate('data_sync'); facade.navigate('history'); facade.navigate('settings');
  expect(work.state.publicInputs).toBe(buffers); expect(work.state.dirty).toBe(true);
  expect(facade.setPublicInput(binding, '-2')).toBe(true); expect(work.state.publicInputs?.[0].text).toBe('-2'); facade.dispose();
});

test('a target change with only unfinished numeric text queues review and Stay preserves the original target and opener', () => {
  const { facade, work } = harness(); buffered(work, 'setting.number', '1.'); const selected = work.state.selector;
  let focused = 0; facade.focus.register('numeric-target', () => ({ isConnected: true, focus() { focused++; } }));
  expect(facade.requestTarget(otherTarget(), 'numeric-target')).toBe(false);
  expect(work.state.pendingNavigation?.kind).toBe('target'); expect(work.state.selector).toBe(selected);
  expect(facade.stay()).toBe(true); expect(focused).toBe(1); expect(work.state.publicInputs?.[0].text).toBe('1.'); facade.dispose();
});

test('close with only unresolved public text cannot bypass Save Discard Stay custody', () => {
  const { facade, work } = harness(); buffered(work, 'setting.integer', '-');
  expect(facade.requestClose()).toBe(false); expect(work.state.pendingNavigation?.kind).toBe('close');
  expect(work.state.closeRequested).toBe(false); expect(facade.stay()).toBe(true);
  expect(work.state.publicInputs?.[0].text).toBe('-'); expect(work.state.dirty).toBe(true); facade.dispose();
});

test('both navigation and in-place Save preparation refuse unresolved numeric buffers without sending', async () => {
  const { facade, work, sent } = harness(); buffered(work, 'setting.integer', '-');
  expect(await facade.prepareSaveInPlace()).toBeUndefined(); expect(work.state.transitionBusy).toBe(false);
  facade.requestClose(); expect(await facade.prepareSave()).toBeUndefined();
  expect(sent).toEqual([]); expect(facade.state.transition.kind).toBe('idle'); expect(work.state.publicInputs?.[0].text).toBe('-');
  expect(facade.state.notice).toContain('Resolve or reset'); facade.dispose();
});

test('published integer and decimal range failures retain their exact text and do not stage or round', async () => {
  const { facade, work, sent } = harness();
  let binding = buffered(work, 'setting.integer', '9223372036854775808');
  expect(facade.stagePublicInput(binding, '9223372036854775808', [numeric('setting.integer', 'integer', '9223372036854775808')])).toBe(false);
  binding = buffered(work, 'setting.number', '5.25000000000000000000000000000001');
  expect(facade.stagePublicInput(binding, '5.25000000000000000000000000000001', [numeric('setting.number', 'number', '5.25000000000000000000000000000001')])).toBe(false);
  expect(work.state.publicInputs?.map(value => value.text)).toEqual(['9223372036854775808', '5.25000000000000000000000000000001']);
  expect(work.state.edits).toEqual([]); expect(await facade.prepareSaveInPlace()).toBeUndefined(); expect(sent).toEqual([]); facade.dispose();
});

test('atomic public integer resolution preserves values above Number safe precision exactly', () => {
  const { facade, work, sent } = harness(); const text = '9007199254740993', binding = buffered(work, 'setting.integer', text);
  const submitted = [numeric('setting.integer', 'integer', text)];
  expect(facade.stagePublicInput(binding, text, submitted)).toBe(true);
  expect(work.state.publicInputs).toEqual([]); expect(work.state.edits).toEqual(submitted);
  expect((work.state.edits[0] as any).value.value).toBe('9007199254740993'); expect(sent).toEqual([]); expect(work.state.dirty).toBe(true); facade.dispose();
});

test('canonical decimal resolution compares negative and fractional bounds with BigInt precision', () => {
  const { facade, work } = harness();
  let text = '-2.50000000000000000000000000000001', binding = buffered(work, 'setting.number', text);
  expect(facade.stagePublicInput(binding, text, [numeric('setting.number', 'number', text)])).toBe(false);
  text = '-2.5'; binding = buffered(work, 'setting.number', text);
  expect(facade.stagePublicInput(binding, text, [numeric('setting.number', 'number', text)])).toBe(true);
  text = '5.25'; binding = buffered(work, 'setting.number', text);
  expect(facade.stagePublicInput(binding, text, [numeric('setting.number', 'number', text)])).toBe(true); facade.dispose();
});

test('noncanonical numeric text remains buffered rather than being coerced by frontend presentation', () => {
  const { facade, work } = harness();
  for (const text of ['-', '', '+1', '01', '-0', '1e2', '1.', '1.50']) {
    const binding = buffered(work, 'setting.number', text);
    expect(facade.stagePublicInput(binding, text, [numeric('setting.number', 'number', text)])).toBe(false);
    expect(work.state.publicInputs?.[0].text).toBe(text);
  }
  expect(work.state.edits).toEqual([]); facade.dispose();
});

test('foreign draft revision document schema field type and sensitivity cannot receive a public input', () => {
  const { facade, work } = harness(); const binding = facade.capturePublicInput('setting.integer')!;
  const variants: PublicInputBinding[] = [
    { ...binding, draft: { ...binding.draft, draftId: '00000999-1111-4111-8111-111111111111' } },
    { ...binding, draft: { ...binding.draft, revision: '2' } },
    { ...binding, draft: { ...binding.draft, document: { ...binding.draft.document, revision: 'foreign-revision' } } },
    { ...binding, draft: { ...binding.draft, document: { ...binding.draft.document, schema: { ...binding.draft.document.schema, digest: 'sha256:' + 'c'.repeat(64) } } } },
    { ...binding, field: { ...binding.field, fieldId: 'setting.foreign' } },
    { ...binding, field: { ...binding.field, valueType: { kind: 'number' } } },
    { ...binding, field: { ...binding.field, sensitivity: 'private' } },
    { ...binding, field: { ...binding.field, sensitivity: 'secret' } },
    { ...binding, field: { ...binding.field, valueType: { kind: 'integer', minimum: '0', maximum: '10' } } },
  ];
  for (const foreign of variants) expect(facade.setPublicInput(foreign, '-')).toBe(false);
  expect(work.state.publicInputs).toEqual([]); expect(work.state.dirty).toBe(false); facade.dispose();
});

test('private secret and nonnumeric public fields never expose the public numeric text entry API', () => {
  const { facade } = harness();
  for (const field of ['sync.endpoint', 'sync.token', 'setting.string', 'setting.boolean', 'setting.enum', 'setting.keys', 'setting.notification', 'setting.foreign']) {
    expect(facade.capturePublicInput(field)).toBeUndefined();
  }
  facade.dispose();
});

test('public input capture refuses duplicate field definitions or mismatched document schema binding', () => {
  const duplicated = clean(); duplicated.schema.fields.push(clone(duplicated.schema.fields[1]));
  const first = harness({ draft: duplicated }); expect(first.facade.capturePublicInput('setting.integer')).toBeUndefined(); first.facade.dispose();
  const mismatch = clean(); mismatch.schema.binding = { ...mismatch.schema.binding, schemaVersion: '2.0.0' };
  const second = harness({ draft: mismatch }); expect(second.facade.capturePublicInput('setting.integer')).toBeUndefined(); second.facade.dispose();
});

test('public buffer size Unicode and accessor refusals leave prior text intact and invoke no conversion hook', () => {
  const { facade, work } = harness(); let binding = buffered(work, 'setting.integer', '-'), invoked = 0;
  expect(facade.setPublicInput(binding, 'x'.repeat(PUBLIC_INPUT_LIMITS.maximumTextLength + 1))).toBe(false);
  expect(facade.setPublicInput(binding, '\uD800')).toBe(false);
  expect(facade.setPublicInput(binding, { toString() { invoked++; return '-'; } } as any)).toBe(false);
  const hostile = { ...binding }; Object.defineProperty(hostile, 'draft', { enumerable: true, get() { invoked++; return binding.draft; } });
  expect(facade.setPublicInput(hostile, '3')).toBe(false); expect(invoked).toBe(0);
  expect(work.state.publicInputs?.[0].text).toBe('-'); binding = facade.capturePublicInput('setting.integer')!;
  expect(facade.setPublicInput(binding, 'x'.repeat(PUBLIC_INPUT_LIMITS.maximumTextLength))).toBe(true); facade.dispose();
});

test('the numeric buffer count is bounded while updating an existing buffer at capacity remains possible', () => {
  const draft = clean(), template = clone(draft.schema.fields[1]);
  // Additional fields are a schema-valid developer fixture for presentation bounds.
  // They make no claim about the producer schema identified by the shared binding.
  draft.schema.fields = Array.from({ length: PUBLIC_INPUT_LIMITS.maximumBuffers + 1 }, (_, index) => ({ ...clone(template), fieldId: 'synthetic.integer.' + index }));
  const checked = source('sc08-open-clean-draft-reply'); checked.body.result.command.output = draft; decodeReply(JSON.stringify(checked));
  const { facade, work } = harness({ draft });
  for (let index = 0; index < PUBLIC_INPUT_LIMITS.maximumBuffers; index++) buffered(work, 'synthetic.integer.' + index, '-');
  expect(facade.setPublicInput(facade.capturePublicInput('synthetic.integer.' + PUBLIC_INPUT_LIMITS.maximumBuffers)!, '-')).toBe(false);
  expect(work.state.publicInputs).toHaveLength(PUBLIC_INPUT_LIMITS.maximumBuffers);
  expect(facade.setPublicInput(facade.capturePublicInput('synthetic.integer.0')!, '2')).toBe(true); facade.dispose();
});

test('stale same-draft callbacks cannot overwrite newer text or clear buffers after another full edit change', () => {
  const { facade, work } = harness(); let stale = buffered(work, 'setting.integer', '2');
  facade.setPublicInput(facade.capturePublicInput('setting.integer')!, '3');
  expect(facade.setPublicInput(stale, '4')).toBe(false); expect(facade.resetPublicInput(stale, '2')).toBe(false);
  expect(facade.stagePublicInput(stale, '2', [numeric('setting.integer', 'integer', '2')])).toBe(false);
  stale = facade.capturePublicInput('setting.integer')!; facade.stage([{ kind: 'set_public', fieldId: 'setting.boolean', value: { kind: 'boolean', value: true } }]);
  expect(facade.stagePublicInput(stale, '3', [numeric('setting.integer', 'integer', '3')])).toBe(false);
  expect(work.state.publicInputs?.[0].text).toBe('3'); expect(work.state.edits[0]).toMatchObject({ fieldId: 'setting.boolean' }); facade.dispose();
});

test('resolution requires exact buffered text and one matching typed public numeric edit', () => {
  const { facade, work } = harness(); const binding = buffered(work, 'setting.integer', '2');
  for (const edits of [[], [numeric('setting.integer', 'integer', '3')], [numeric('setting.number', 'number', '2')],
    [numeric('setting.integer', 'number', '2')], [numeric('setting.integer', 'integer', '2'), numeric('setting.integer', 'integer', '2')]]) {
    expect(facade.stagePublicInput(binding, '2', edits)).toBe(false);
  }
  expect(facade.stagePublicInput(binding, '3', [numeric('setting.integer', 'integer', '3')])).toBe(false);
  expect(work.state.publicInputs?.[0].text).toBe('2'); facade.dispose();
});

test('atomic resolution publishes typed edits and buffer removal together before reentrant target navigation', () => {
  const { facade, work } = harness(); const binding = buffered(work, 'setting.integer', '2'), before = work.state.selector;
  const states: { dirty: boolean; bufferCount: number; edits: readonly any[] }[] = []; let arm = true;
  const stop = work.subscribe(state => {
    states.push({ dirty: state.dirty, bufferCount: state.publicInputs?.length ?? 0, edits: state.edits });
    if (arm && !state.publicInputs?.length && state.edits.length) { arm = false; expect(facade.requestTarget(otherTarget())).toBe(false); }
  });
  expect(facade.stagePublicInput(binding, '2', [numeric('setting.integer', 'integer', '2')])).toBe(true);
  expect(states.every(state => state.dirty)).toBe(true); expect(states.some(state => state.bufferCount === 0 && state.edits.length === 0)).toBe(false);
  expect(work.state.selector).toBe(before); expect(work.state.pendingNavigation?.kind).toBe('target'); stop(); facade.dispose();
});

test('resolving one numeric field retains other buffered fields and nonnumeric edits', () => {
  const { facade, work } = harness(); buffered(work, 'setting.integer', '2'); buffered(work, 'setting.number', '-');
  const edits: ConfigurationEdit[] = [{ kind: 'set_public', fieldId: 'setting.boolean', value: { kind: 'boolean', value: true } }, numeric('setting.integer', 'integer', '2')];
  expect(facade.stagePublicInput(facade.capturePublicInput('setting.integer')!, '2', edits)).toBe(true);
  expect(work.state.publicInputs?.map(value => value.binding.field.fieldId)).toEqual(['setting.number']);
  expect(work.state.edits).toEqual(edits); expect(work.state.dirty).toBe(true); facade.dispose();
});

test('external schema draft and document conflict observations cannot overwrite unfinished public text', () => {
  const { facade, work } = harness(); buffered(work, 'setting.integer', '-'); const draft = work.state.draft, buffers = work.state.publicInputs;
  const changed = clean(); changed.draft.revision = '2'; changed.draft.document.revision = 'synthetic-external-revision'; changed.schema.fields[1].valueType = { kind: 'integer', maximum: '10' };
  expect(work.observeDraft(changed)).toBe(false); expect(work.openDraft(changed)).toBe(false);
  expect(work.state.draft).toBe(draft); expect(work.state.publicInputs).toBe(buffers); expect(work.state.draftConflict).toBe(true);
  facade.navigate('data_sync'); facade.navigate('settings'); expect(work.state.publicInputs?.[0].text).toBe('-'); facade.dispose();
});

test('a successful explicit draft-open acknowledgement cannot overwrite retained unfinished input without confirmed intent', async () => {
  const { facade, work, sent } = harness(); buffered(work, 'setting.integer', '-'); const draft = work.state.draft, buffers = work.state.publicInputs;
  expect(await facade.openDraft(clean().draft.document)).toMatchObject({ kind: 'result' });
  expect(sent.map(method)).toEqual(['open_draft']); expect(work.state.draft).toBe(draft); expect(work.state.publicInputs).toBe(buffers);
  expect(work.state.draftConflict).toBe(true); expect(work.state.dirty).toBe(true); facade.dispose();
});

test('Discard review freezes the captured public buffers and Stay sends nothing while retaining them', () => {
  const { facade, work, sent } = harness(); const oldBinding = buffered(work, 'setting.integer', '-'), buffers = work.state.publicInputs;
  expect(facade.prepareDiscardInPlace()).toBe(true); expect(facade.capturePublicInput('setting.integer')).toBeUndefined();
  expect(facade.setPublicInput(oldBinding, '2')).toBe(false); expect(facade.resetPublicInput(oldBinding, '-')).toBe(false);
  expect(work.capturePublicInput('setting.integer')).toBeUndefined();
  expect(facade.stay()).toBe(true); expect(sent).toEqual([]); expect(work.state.publicInputs).toBe(buffers); facade.dispose();
});

test('public input APIs refuse competing action preparation without erasing unfinished numeric text', async () => {
  const { facade, work, sent } = harness({ hold: true }); const binding = buffered(work, 'setting.integer', '2');
  const pending = facade.actions.prepare(source('sc-03-profile-one-prepare-request').body.command.input.intent);
  expect(facade.actions.state.transition.kind).toBe('preparing'); expect(facade.capturePublicInput('setting.integer')).toBeUndefined();
  expect(facade.setPublicInput(binding, '3')).toBe(false); expect(facade.resetPublicInput(binding, '2')).toBe(false);
  expect(facade.stagePublicInput(binding, '2', [numeric('setting.integer', 'integer', '2')])).toBe(false);
  expect(work.state.publicInputs?.[0].text).toBe('2'); expect(sent.map(method)).toEqual(['prepare']); facade.dispose(); await pending;
});

test('refused or foreign Discard acknowledgement retains captured public text and typed edits', async () => {
  for (const discard of ['foreign', 'refused'] as const) {
    const { facade, work, sent } = harness({ discard }); buffered(work, 'setting.integer', '-');
    const edits: ConfigurationEdit[] = [{ kind: 'set_public', fieldId: 'setting.boolean', value: { kind: 'boolean', value: true } }];
    facade.stage(edits); const buffers = work.state.publicInputs;
    expect(facade.prepareDiscardInPlace()).toBe(true); expect(await facade.confirmDiscard()).toBe(false);
    expect(work.state.publicInputs).toBe(buffers); expect(work.state.edits).toEqual(edits);
    expect(work.state.transitionBusy).toBe(false); expect(sent.map(method)).toEqual(['discard_draft']); facade.dispose();
  }
});

test('exact navigation Discard acknowledgement clears only captured old buffers then applies the queued target', async () => {
  const { facade, work, sent } = harness(); buffered(work, 'setting.integer', '-');
  const old = work.state.draft!.draft, target = otherTarget(); expect(facade.requestTarget(target)).toBe(false);
  expect(await facade.discard()).toBe(true); expect(sent.map(method)).toEqual(['discard_draft']); expect(input(sent[0])).toEqual({ draft: old });
  expect(work.state.selector).toEqual(target); expect(work.state.publicInputs).toEqual([]); expect(work.state.draft).toBeUndefined(); expect(work.state.dirty).toBe(false); facade.dispose();
});

test('exact in-place Discard clears old public buffers and reopens only the authoritative baseline under the same target', async () => {
  const { facade, work, sent } = harness(); buffered(work, 'setting.number', '1.'); const selected = work.state.selector, old = work.state.draft!.draft;
  expect(facade.prepareDiscardInPlace()).toBe(true); expect(await facade.confirmDiscard()).toBe(true);
  expect(sent.map(method)).toEqual(['discard_draft', 'read_configuration', 'open_draft']); expect(input(sent[0])).toEqual({ draft: old });
  expect(work.state.selector).toBe(selected); expect(work.state.view).toBe('settings'); expect(work.state.publicInputs).toEqual([]);
  expect(work.state.draft!.draft.draftId).not.toBe(old.draftId); expect(work.state.draft!.draft.document).toEqual(source('sc09-schema-all-field-types-reply').body.result.query.output.value.binding); facade.dispose();
});

test('a foreign review token cannot clear buffers or release a newer owned Discard attempt', () => {
  const { facade, work } = harness(); buffered(work, 'setting.integer', '-'); const review = work.beginReview('in_place')!;
  expect(review.publicInputs).toBe(work.state.publicInputs); expect(work.finishDiscard({ ...review }, abandoned())).toBe(false);
  expect(work.state.transitionBusy).toBe(true); expect(work.finishDraftSynchronization(review, abandoned())).toBeUndefined();
  expect(work.state.transitionBusy).toBe(false); expect(work.state.publicInputs?.[0].text).toBe('-');
  const newer = work.beginReview('in_place')!; expect(work.finishDiscard(review, abandoned())).toBe(false); expect(work.state.transitionBusy).toBe(true);
  expect(work.abandonReview(newer)).toBe(true); facade.dispose();
});

test('even a schema-valid completed Save observation cannot clear a review containing unresolved numeric text', () => {
  const { facade, work } = harness(); buffered(work, 'setting.integer', '-'); const review = work.beginReview('in_place')!;
  const reply = source('sc10-save-verified-result-reply'), operation = reply.body.result.query.output.operation.value;
  operation.semantics.capture.input.draft = clone(review.draft); decodeReply(JSON.stringify(reply));
  expect(work.finishSave(review, { kind: 'result', value: operation, request: { requestId: reply.requestId, kind: 'query', method: 'get_operation' } })).toBe(false);
  expect(work.state.publicInputs?.[0].text).toBe('-'); expect(work.state.draft).toBeDefined();
  expect(work.state.baselineRefreshRequired).toBe(false); expect(work.state.transitionBusy).toBe(false); facade.dispose();
});

test('an explicit Reset removes only the current captured numeric text and never discards other staged edits', () => {
  const { facade, work } = harness(); buffered(work, 'setting.integer', '-'); buffered(work, 'setting.number', '1.');
  const edits: ConfigurationEdit[] = [{ kind: 'set_public', fieldId: 'setting.boolean', value: { kind: 'boolean', value: true } }]; facade.stage(edits);
  const binding = facade.capturePublicInput('setting.integer')!;
  expect(facade.resetPublicInput(binding, 'foreign')).toBe(false); expect(facade.resetPublicInput(binding, '-')).toBe(true);
  expect(work.state.publicInputs?.map(value => value.binding.field.fieldId)).toEqual(['setting.number']); expect(work.state.edits).toEqual(edits); expect(work.state.dirty).toBe(true);
  expect(facade.resetPublicInput(binding, '-')).toBe(false); facade.dispose();
});

test('backend field validation remains authoritative after exact public numeric presentation resolution', async () => {
  const { facade, work, sent } = harness({ invalidSynchronization: true }); const text = '9007199254740993';
  expect(facade.stagePublicInput(buffered(work, 'setting.integer', text), text, [numeric('setting.integer', 'integer', text)])).toBe(true);
  expect(await facade.prepareSaveInPlace()).toBeUndefined(); expect(sent.map(method)).toEqual(['set_draft_changes']);
  expect(work.state.edits).toEqual([numeric('setting.integer', 'integer', text)]); expect(work.state.draft!.validation).toHaveLength(1);
  expect(work.state.dirty).toBe(true); expect(facade.state.notice).toContain('validation'); facade.dispose();
});

test('facade disposal explicitly retains owned raw buffers and refuses subsequent input mutation', () => {
  const { facade, work } = harness(); const binding = buffered(work, 'setting.integer', '-'), buffers = work.state.publicInputs;
  facade.dispose(); expect(work.state.publicInputs).toBe(buffers); expect(work.state.dirty).toBe(true);
  expect(facade.capturePublicInput('setting.integer')).toBeUndefined(); expect(facade.setPublicInput(binding, '2')).toBe(false);
  expect(facade.resetPublicInput(binding, '-')).toBe(false); expect(facade.stagePublicInput(binding, '-', [])).toBe(false);
});

test('reentrant disposal during public buffer publication cannot clear text or authorize a later mutation', () => {
  const { facade, work } = harness(); let arm = true;
  const stop = work.subscribe(state => { if (arm && state.publicInputs?.length) { arm = false; facade.dispose(); } });
  const binding = facade.capturePublicInput('setting.integer')!;
  expect(facade.setPublicInput(binding, '-')).toBe(true); expect(work.state.publicInputs?.[0].text).toBe('-');
  expect(facade.setPublicInput(work.capturePublicInput('setting.integer')!, '2')).toBe(false); expect(work.state.dirty).toBe(true); stop();
});

test('dispose during an in-flight Discard observation preserves exact captured public text and queued close', async () => {
  const { facade, work, sent } = harness({ hold: true }); buffered(work, 'setting.integer', '-'); facade.requestClose();
  const pending = facade.discard(); facade.dispose(); expect(await pending).toBe(false);
  expect(sent.map(method)).toEqual(['discard_draft']); expect(work.state.publicInputs?.[0].text).toBe('-');
  expect(work.state.pendingNavigation?.kind).toBe('close'); expect(work.state.closeRequested).toBe(false); expect(work.state.transitionBusy).toBe(false);
});
