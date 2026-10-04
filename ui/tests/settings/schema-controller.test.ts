import { expect, test } from 'vitest';
import { canonicalData } from '../../src/client';
import type { DraftSnapshot, Request } from '../../src/generated/protocol';
import { frame, harness } from './view-helpers';

test('public edits replace one field atomically and retain unrelated full-set Data Sync intent', () => {
  const {facade,controller,dispose} = harness(); const feed = {kind:'set_sync_feed' as const,destinationId:'destination-one',feedId:'battlelog',value:'off' as const}; facade.stage([feed]);
  expect(controller.setPublic('setting.boolean',{kind:'boolean',value:true})).toBe(true);
  expect(controller.setPublic('setting.boolean',{kind:'boolean',value:false})).toBe(true);
  expect(facade.work.state.edits).toEqual([feed,{kind:'set_public',fieldId:'setting.boolean',value:{kind:'boolean',value:false}}]); dispose();
});
test('unknown enum, oversized strings, unsupported keys and sounds refuse without replacing existing intent', () => {
  const {facade,controller,dispose} = harness(); controller.setPublic('setting.boolean',{kind:'boolean',value:true}); const before = canonicalData(facade.work.state.edits);
  expect(controller.setPublic('setting.enum',{kind:'enum',value:'not-published'})).toBe(false);
  expect(controller.setPublic('setting.string',{kind:'string',value:'a'.repeat(65)})).toBe(false);
  expect(controller.setPublic('setting.keys',{kind:'keybinding',value:[{key:'NOT_PUBLISHED',modifiers:[]}]})).toBe(false);
  expect(controller.setPublic('setting.notification',{kind:'notification_policy',value:{kind:'channels',system:true,audio:true,sound:'NOT_PUBLISHED'}})).toBe(false);
  expect(canonicalData(facade.work.state.edits)).toBe(before); dispose();
});
test('integer and exact decimal partial input retain shared dirty custody until valid completion', () => {
  const {facade,controller,dispose} = harness();
  expect(controller.setNumeric('setting.integer','-')).toBe(false); expect(facade.work.state.dirty).toBe(true); expect(facade.work.state.publicInputs?.[0].text).toBe('-');
  expect(controller.setNumeric('setting.integer','2')).toBe(true); expect(facade.work.state.publicInputs).toEqual([]);
  expect(controller.setNumeric('setting.number','1.')).toBe(false); expect(controller.resetNumeric('setting.number')).toBe(true);
  expect(facade.work.state.edits).toEqual([{kind:'set_public',fieldId:'setting.integer',value:{kind:'integer',value:'2'}}]); dispose();
});
test('field removal and undo preserve other edits and create no wire operation', () => {
  const {facade,controller,sent,dispose} = harness(); controller.setPublic('setting.boolean',{kind:'boolean',value:true}); controller.setPublic('setting.enum',{kind:'enum',value:'summary'});
  expect(controller.removeOverride('setting.boolean')).toBe(true); expect(controller.undoField('setting.boolean')).toBe(true);
  expect(facade.work.state.edits).toEqual([{kind:'set_public',fieldId:'setting.enum',value:{kind:'enum',value:'summary'}}]); expect(sent).toEqual([]); dispose();
});
test('refresh same bound document preserves the exact draft and avoids opening another draft', async () => {
  const {facade,controller,sent,dispose} = harness(); controller.setPublic('setting.boolean',{kind:'boolean',value:true}); const before = canonicalData(facade.work.state.draft);
  expect(await controller.refresh()).toBe(true); expect(sent.map(request => request.body.type === 'query' ? request.body.query.name : request.body.command.name)).toEqual(['read_configuration']);
  expect(canonicalData(facade.work.state.draft)).toBe(before); expect(facade.work.state.dirty).toBe(true); dispose();
});
test('late configuration observation after target transition cannot open or bind the previous target', async () => {
  let release!: (value: unknown) => void; let observed: unknown;
  const {facade,controller,sent,dispose} = harness((request,output) => { if (request.body.type === 'query') { observed = output; return new Promise(resolve => {release = resolve;}); } return output; });
  const pending = controller.refresh(); const selector = facade.work.state.selector!;
  facade.requestTarget({...selector,profile:{kind:'isolated',id:'bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb'}}); release(observed);
  expect(await pending).toBe(false); expect(sent).toHaveLength(1); expect(facade.work.state.draft).toBeUndefined(); dispose();
});
test('capture cancellation preserves existing edits and does not fabricate protected reference', async () => {
  const {facade,controller,sent,dispose} = harness(); controller.setPublic('setting.boolean',{kind:'boolean',value:true}); const before = canonicalData(facade.work.state.edits);
  expect(await controller.captureProtected('sync.token')).toBe(false); expect(canonicalData(facade.work.state.edits)).toBe(before);
  expect(sent[0].body.type).toBe('command'); dispose();
});
test('a refreshed target identity cannot relabel or mutate retained old draft edits', () => {
  const {facade,controller,dispose} = harness(); controller.setPublic('setting.boolean',{kind:'boolean',value:true}); const old=canonicalData(facade.work.state.edits), target=facade.work.state.binding!;
  facade.work.bindTarget({...target,installation:{...target.installation,physicalId:'replacement-physical-identity'}});
  expect(controller.blocked).toBe(true); expect(controller.setPublic('setting.boolean',{kind:'boolean',value:false})).toBe(false);
  expect(canonicalData(facade.work.state.edits)).toBe(old); expect(facade.work.state.draft?.draft.document.target).toEqual(target); dispose();
});
test('unavailable observation retains the draft and does not open a replacement', async () => {
  const {facade,controller,sent,dispose} = harness((request,output) => request.body.type === 'query' ? {status:'unavailable',reason:'native_unavailable'} : output);
  controller.setPublic('setting.boolean',{kind:'boolean',value:true}); const before=canonicalData([facade.work.state.draft,facade.work.state.edits]);
  expect(await controller.refresh()).toBe(false); expect(canonicalData([facade.work.state.draft,facade.work.state.edits])).toBe(before); expect(sent).toHaveLength(1); expect(controller.state.notice).toContain('unavailable'); dispose();
});
test('externally replaced document marks conflict and preserves exact current edits', async () => {
  const {facade,controller,observed,dispose} = harness(); controller.setPublic('setting.boolean',{kind:'boolean',value:true}); const before=canonicalData(facade.work.state.edits), original=facade.work.state.draft;
  observed.binding={...observed.binding,revision:'external-revision'};
  expect(await controller.refresh()).toBe(false); expect(facade.work.state.draftConflict).toBe(true); expect(facade.work.state.draft).toEqual(original); expect(canonicalData(facade.work.state.edits)).toBe(before); expect(controller.blocked).toBe(true); dispose();
});
test('a changed Bridge host blocks field and protected entry while retaining exact draft edits and numeric text', async () => {
  const {facade,controller,sent,dispose} = harness();
  try {
    await controller.refresh(); controller.setPublic('setting.boolean',{kind:'boolean',value:true}); controller.setNumeric('setting.integer','-');
    const before = canonicalData([facade.work.state.draft,facade.work.state.edits,facade.work.state.publicInputs,facade.work.state.selector]);
    const snapshot = frame('sc15-complete-empty-snapshot-reply').body.result.query.output;
    snapshot.cursor.hostEpoch = '00000001-9999-4999-8999-999999999999';
    expect(facade.work.observations.acceptSnapshot(snapshot)).toBe(true);
    expect(controller.blocked).toBe(true); expect(controller.state.document).toBeUndefined(); expect(controller.state.notice).toContain('Bridge host changed');
    expect(controller.setPublic('setting.boolean',{kind:'boolean',value:false})).toBe(false);
    expect(controller.setNumeric('setting.integer','2')).toBe(false); expect(controller.resetNumeric('setting.integer')).toBe(false); expect(await controller.captureProtected('sync.token')).toBe(false);
    expect(sent).toHaveLength(1); expect(canonicalData([facade.work.state.draft,facade.work.state.edits,facade.work.state.publicInputs,facade.work.state.selector])).toBe(before);
    expect(await controller.refresh()).toBe(false); expect(sent).toHaveLength(2);
    expect(sent.every(request => request.body.type === 'query' && request.body.query.name === 'read_configuration')).toBe(true);
    expect(canonicalData([facade.work.state.draft,facade.work.state.edits,facade.work.state.publicInputs,facade.work.state.selector])).toBe(before);
  } finally { dispose(); }
});
test('a clean same-document refresh reopens the current host draft instead of retaining old host custody', async () => {
  const epoch = '00000001-9999-4999-8999-999999999999';
  const {facade,controller,sent,dispose} = harness((request,output) => {
    if (request.body.type !== 'command' || request.body.command.name !== 'open_draft') return output;
    const opened = output as DraftSnapshot; return {...opened,draft:{...opened.draft,hostEpoch:epoch}};
  });
  try {
    const snapshot = frame('sc15-complete-empty-snapshot-reply').body.result.query.output; snapshot.cursor.hostEpoch = epoch;
    expect(facade.work.observations.acceptSnapshot(snapshot)).toBe(true); expect(controller.blocked).toBe(true);
    expect(await controller.refresh()).toBe(true); expect(controller.blocked).toBe(false); expect(facade.work.state.draft?.draft.hostEpoch).toBe(epoch);
    expect(sent.map(request => request.body.type === 'query' ? request.body.query.name : request.body.command.name)).toEqual(['read_configuration','open_draft']);
    expect(controller.state.document?.binding).toEqual(facade.work.state.draft?.draft.document); expect(facade.work.state.dirty).toBe(false);
  } finally { dispose(); }
});
