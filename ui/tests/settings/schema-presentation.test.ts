import { expect, test } from 'vitest';
import { WorkContext, captureData } from '../../src/client';
import type { ConfigurationEdit, FieldDefinition } from '../../src/generated/protocol';
import { categories, filterFields, presentField, replaceField, stagedApply } from '../../src/views/settings/presentation';
import { clean, document } from './view-helpers';

test('schema drives categories and search terms including aliases without a handwritten field list', () => {
  const draft = clean(); draft.schema.fields[0].category = 'navigation'; draft.schema.fields[0].searchTerms.push('warp indicator'); draft.schema.fields[0].aliases = [['legacy', 'display']];
  expect(categories(draft.schema.fields)).toEqual(['navigation', 'settings']);
  expect(filterFields(draft.schema.fields, 'warp', 'navigation').map(field => field.fieldId)).toEqual(['setting.boolean']);
  expect(filterFields(draft.schema.fields, 'legacy display').map(field => field.fieldId)).toEqual(['setting.boolean']);
  expect(filterFields(draft.schema.fields, 'warp', 'settings')).toEqual([]);
});
test('unobserved baseline never pretends an effective default was observed', () => {
  const work = new WorkContext(); work.openDraft(clean());
  const field = clean().schema.fields[0]; expect(presentField(field, work.state).override).toBe('unknown'); expect(presentField(field, work.state).value).toBeUndefined();
});
test('sparse remove restores provider default while saved value and unrelated edits remain preserved', () => {
  const work = new WorkContext(), doc = document(), field = clean().schema.fields[0]; work.openDraft(clean());
  doc.fields = [{fieldId:field.fieldId,overridden:true,value:{kind:'public',value:{kind:'boolean',value:true}}}];
  expect(presentField(field, work.state, doc).override).toBe('saved');
  const sync: ConfigurationEdit = {kind:'set_sync_feed',destinationId:'destination-one',feedId:'battlelog',value:'off'};
  work.stage([sync,{kind:'remove_override',fieldId:field.fieldId}]);
  expect(presentField(field,work.state,doc).value).toEqual(field.defaultValue); expect(presentField(field,work.state,doc).override).toBe('default');
  expect(replaceField(work.state.edits,field.fieldId,{kind:'set_public',fieldId:field.fieldId,value:{kind:'boolean',value:true}})[0]).toEqual(sync);
  expect(doc.fields[0].value).toEqual({kind:'public',value:{kind:'boolean',value:true}});
});
test('complete staged apply timings include Settings and Data Sync together', () => {
  const draft = clean(); draft.schema.fields[0].apply = 'immediate'; draft.schema.fields[1].apply = 'restart_required';
  const work = new WorkContext(); work.openDraft(draft); work.stage([{kind:'set_public',fieldId:'setting.boolean',value:{kind:'boolean',value:true}},
    {kind:'set_public',fieldId:'setting.integer',value:{kind:'integer',value:'2'}},{kind:'set_sync_feed',destinationId:'destination-one',feedId:'battlelog',value:'off'}]);
  expect(stagedApply(work.state)).toEqual(['immediate','next_launch','restart_required']);
});
test('legacy feed timing stays next launch despite unrelated hidden mode fields', () => {
  const draft = clean(), work = new WorkContext();
  draft.schema.fields.push({...draft.schema.fields[0],fieldId:'hidden.restart',path:['hidden','restart'],apply:'restart_required'});
  draft.schema.sync.find(type => type.mode === 'majel')!.fields.push('hidden.restart');
  work.openDraft(draft);
  work.stage([{kind:'set_sync_feed',destinationId:'destination-one',feedId:'battlelog',value:'off'}]);
  expect(stagedApply(work.state)).toEqual(['next_launch']);
});
test('proxy destination timing stays next launch despite protected field role timings', () => {
  const draft = clean(), work = new WorkContext();
  const legacy = draft.schema.sync.find(type => type.mode === 'legacy')!;
  draft.schema.fields.find(field => field.fieldId === legacy.endpointFieldId)!.apply = 'restart_required';
  draft.schema.fields.find(field => field.fieldId === legacy.secretFieldId)!.apply = 'restart_required';
  draft.schema.fields.push({...draft.schema.fields[0],fieldId:'sync.proxy',path:['sync','proxy'],sensitivity:'private',valueType:{kind:'string',maximumLength:'4096'},defaultValue:null,apply:'immediate'});
  legacy.proxyFieldId = 'sync.proxy'; legacy.fields.push('sync.proxy');
  work.openDraft(draft);
  work.stage([{kind:'set_sync_proxy',destinationId:'destination-one',value:{kind:'none'}}]);
  expect(stagedApply(work.state)).toEqual(['next_launch']);
});
test('private and secret presentation never emits opaque handle IDs or protected contents', () => {
  const work = new WorkContext(), draft = clean(); work.openDraft(draft);
  const field = draft.schema.fields.find(field => field.sensitivity === 'secret')!;
  work.stage([{kind:'replace_secret',fieldId:field.fieldId,reference:{draft:draft.draft,fieldId:field.fieldId,secretId:'00000001-1111-4111-8111-111111111111'}}]);
  const presented = presentField(field,work.state,document()); expect(presented.protectedConfigured).toBe(true); expect(JSON.stringify(presented)).not.toContain('secretId');
});
test('platform availability and backend validation are explicit per schema field', () => {
  const draft = clean(), work = new WorkContext(); draft.validation = [{fieldId:'setting.boolean',code:'constraint_violation'}]; work.openDraft(draft);
  const field: FieldDefinition = {...draft.schema.fields[0],platforms:['macos']};
  expect(presentField(captureData(field),work.state,document(),'windows').supported).toBe(false);
  expect(presentField(captureData(field),work.state,document(),'macos').error).toContain('constraints');
});
