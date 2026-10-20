import { render } from 'svelte/server';
import { expect, test } from 'vitest';
import SchemaField from '../../src/views/settings/SchemaField.svelte';
import Settings from '../../src/views/settings/Settings.svelte';
import DataSync from '../../src/views/sync/DataSync.svelte';
import { clean, document, frame, harness } from './view-helpers';

test('all public semantic field types render native labelled controls driven by the schema', () => {
  const {facade,controller,dispose} = harness(), draft = clean(), doc = document();
  for (const [index,field] of draft.schema.fields.entries()) {
    const body = render(SchemaField,{props:{field,work:facade.work.state,controller,document:doc,platform:'windows',index}}).body;
    expect(body).toContain(`aria-labelledby="schema-field-${index}-title"`); expect(body).toContain('Use provider default');
    if (field.sensitivity !== 'public') { expect(body).toContain('Values stay hidden here. Replace them through secure entry.'); expect(body).not.toMatch(/type="(?:password|url)"/); }
    else if (field.valueType.kind === 'boolean') expect(body).toContain('type="checkbox"');
    else if (field.valueType.kind === 'integer' || field.valueType.kind === 'number') { expect(body).toContain('type="text"'); expect(body).toContain('exact precision'); expect(body).not.toContain('type="number"'); }
    else if (field.valueType.kind === 'enum') { expect(body).toContain('Choose an override'); for (const option of field.valueType.values) expect(body).toContain(option); }
    else if (field.valueType.kind === 'keybinding') { expect(body).toContain('Add keybinding'); expect(body).toContain('Modifiers for key 1'); }
    else if (field.valueType.kind === 'notification_policy') expect(body).toContain('System notifications only');
    else expect(body).toContain('type="text"');
  }
  dispose();
});
test('unfinished numeric input exposes invalid text with a labelled error and reset action', () => {
  const {facade,controller,dispose} = harness(); controller.setNumeric('setting.integer','-');
  const field = clean().schema.fields.find(field => field.fieldId === 'setting.integer')!;
  const body = render(SchemaField,{props:{field,work:facade.work.state,controller,document:document(),platform:'windows',index:1}}).body;
  expect(body).toContain('value="-"'); expect(body).toContain('aria-invalid="true"'); expect(body).toContain('schema-field-1-error'); expect(body).toContain('Reset unfinished value'); dispose();
});
test('notification channels preserve separate native system/audio toggles and provider sound options', () => {
  const {facade,controller,dispose} = harness(); controller.setPublic('setting.notification',{kind:'notification_policy',value:{kind:'channels',system:true,audio:false,sound:'warning'}});
  const field = clean().schema.fields.find(field => field.valueType.kind === 'notification_policy')!;
  const body = render(SchemaField,{props:{field,work:facade.work.state,controller,document:document(),platform:'windows',index:6}}).body;
  expect(body).toContain('System notifications'); expect(body).toContain('Audio notifications'); expect(body).toContain('Notification sound'); expect(body).toContain('warning'); dispose();
});
test('Settings shows target labels, categories, search, full-set staged count and mixed apply notices', () => {
  const {facade,controller,dispose} = harness(); controller.setPublic('setting.boolean',{kind:'boolean',value:true});
  const body = render(Settings,{props:{facade,controller,platform:'windows'}}).body;
  expect(body).toContain('Settings workspace'); expect(body).toContain('Search settings'); expect(body).toContain('All categories'); expect(body).toContain('Review Save'); expect(body).toContain('Review Discard');
  expect(body).toContain('1 staged edits across Settings and Data Sync'); expect(body).not.toContain('synthetic-native-target-1'); expect(body).not.toContain('000007d0'); dispose();
});
test('Data Sync exposes creatable modes and hides dormant modes without activating network work', () => {
  const {facade,controller,dispose} = harness();
  const body = render(DataSync,{props:{facade,controller,platform:'windows'}}).body;
  expect(body).toContain('Data Sync workspace'); expect(body).toContain('Legacy'); expect(body).not.toContain('Majel'); expect(body).not.toContain('value="sidecar"');
  expect(body).toContain('Review Save'); expect(body).not.toContain('synthetic-endpoint'); dispose();
});
test('loading state is visible and keeps mutation controls disabled until a bound observation arrives', async () => {
  let release!: (value:unknown)=>void, observed:unknown;
  const {facade,controller,dispose}=harness((request,output)=> request.body.type==='query' ? new Promise(resolve=>{observed=output;release=resolve;}) : output);
  const pending=controller.refresh(); const body=render(Settings,{props:{facade,controller,platform:'windows'}}).body;
  expect(body).toContain('Reading configuration'); expect(body).toContain('aria-busy="true"'); expect(body).toMatch(/<input[^>]+disabled/); release(observed); await pending; dispose();
});
test('failed Save synchronization leaves editing visible and exposes the retained outcome', async () => {
  const {facade,controller,dispose}=harness((request,output)=> { if(request.body.type==='command' && request.body.command.name==='set_draft_changes') throw {code:'delivery_failed',delivery:'not_sent'}; return output; });
  controller.setPublic('setting.boolean',{kind:'boolean',value:true}); expect(await facade.prepareSaveInPlace()).toBeUndefined();
  const body=render(Settings,{props:{facade,controller,platform:'windows'}}).body;
  expect(body).toContain('Draft synchronization was not confirmed'); expect(body).toContain('Unsaved changes'); expect(facade.work.state.edits).toHaveLength(1); expect(controller.blocked).toBe(false); dispose();
});
test('no-change and stale states remain distinguishable with explicit retained draft notices', () => {
  const {facade,controller,dispose}=harness(); const cleanBody=render(Settings,{props:{facade,controller,platform:'windows'}}).body;
  expect(cleanBody).toContain('No unsaved changes'); const stale=clean(); stale.state='stale'; facade.work.observeDraft(stale);
  const staleBody=render(Settings,{props:{facade,controller,platform:'windows'}}).body;
  expect(staleBody).toContain('Configuration changed outside this draft'); expect(controller.blocked).toBe(true); dispose();
});
test('both configuration views announce old host custody and disable retained draft mutation', () => {
  const {facade,controller,dispose}=harness();
  try {
    controller.setPublic('setting.boolean',{kind:'boolean',value:true});
    const snapshot=frame('sc15-complete-empty-snapshot-reply').body.result.query.output;
    expect(facade.work.observations.acceptSnapshot(snapshot)).toBe(true);
    const oldEpoch=snapshot.cursor.hostEpoch,newEpoch='00000001-9999-4999-8999-999999999999';
    facade.work.observations.observeHello(newEpoch);
    expect(facade.work.observations.state.cursor?.hostEpoch).toBe(oldEpoch);
    for(const phase of ['hello','disconnected','replacement_snapshot']) {
      if(phase==='disconnected') facade.work.observations.invalidate('disconnected');
      if(phase==='replacement_snapshot') { snapshot.cursor.hostEpoch=newEpoch; expect(facade.work.observations.acceptSnapshot(snapshot)).toBe(true); }
      expect(facade.work.state.draftHostChanged).toBe(true);
      for(const view of [Settings,DataSync]) {
        const body=render(view,{props:{facade,controller,platform:'windows'}}).body;
        expect(body).toContain('Bridge connection changed'); expect(body).toContain('Your changes remain retained'); expect(body).toContain('Unsaved changes');
        expect(body).toMatch(/<button[^>]+disabled[^>]*>(?:(?:<!--[\s\S]*?-->)|\s)*Review Save/); expect(body).toContain('tabindex="-1"');
      }
    }
    expect(facade.work.state.edits).toEqual([{kind:'set_public',fieldId:'setting.boolean',value:{kind:'boolean',value:true}}]);
  } finally { dispose(); }
});
