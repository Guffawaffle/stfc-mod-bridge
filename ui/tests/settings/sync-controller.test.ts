import { expect, test, vi } from 'vitest';
import { canonicalData } from '../../src/client';
import type { ConfigurationEdit, SyncDestination } from '../../src/generated/protocol';
import { SyncController } from '../../src/views/sync/controller';
import { destinationRows, desiredFeed, desiredProxy } from '../../src/views/sync/presentation';
import { clean, harness } from './view-helpers';

function destination(): SyncDestination { const draft = clean(); return {id:'destination-one',mode:'legacy',exposure:'creatable',secretConfigured:true,
  endpoint:{document:draft.draft.document,fieldId:'sync.endpoint',revision:'saved-endpoint',valueId:'00000001-1111-4111-8111-111111111111'},desiredProxy:{kind:'global'},
  resolvedProxy:{status:'unavailable',reason:'native_unavailable'},feeds:[{feedId:'battlelog',desired:'inherit',resolved:{status:'unavailable',reason:'native_unavailable'}}]}; }
function captures(): ConfigurationEdit[] { const draft = clean(); return [{kind:'set_private',fieldId:'sync.endpoint',reference:{document:draft.draft.document,capturedFor:draft.draft,fieldId:'sync.endpoint',revision:'capture-one',valueId:'00000001-1111-4111-8111-111111111111'}},
  {kind:'replace_secret',fieldId:'sync.token',reference:{draft:draft.draft,fieldId:'sync.token',secretId:'00000002-1111-4111-8111-111111111111'}}]; }
test('existing destination feeds and proxy stage sparse changes in the shared full set', async () => {
  const {facade,controller,observed,dispose} = harness(); observed.sync=[destination()]; await controller.refresh(); const sync = new SyncController(controller);
  controller.setPublic('setting.boolean',{kind:'boolean',value:true});
  expect(sync.setFeed('destination-one','battlelog','off')).toBe(true); expect(sync.setProxy('destination-one',{kind:'none'})).toBe(true);
  const row = destinationRows(facade.work.state,controller.state.document)[0]; expect(desiredFeed(facade.work.state,row,'battlelog')).toBe('off'); expect(desiredProxy(facade.work.state,row)).toEqual({kind:'none'});
  expect(facade.work.state.edits).toHaveLength(3); expect(observed.sync[0].feeds[0].desired).toBe('inherit'); dispose();
});
test('schema rejects unpublished feeds, unsupported global proxy and hidden destination mutation', async () => {
  const {facade,controller,observed,dispose} = harness(); observed.sync=[destination(),{...destination(),id:'sidecar-one',mode:'sidecar',exposure:'existing_configuration_only'},
    {...destination(),id:'hidden-one',mode:'majel',exposure:'hidden'}]; await controller.refresh(); const sync=new SyncController(controller);
  expect(sync.setFeed('destination-one','NOT_PUBLISHED','on')).toBe(false); expect(sync.setProxy('sidecar-one',{kind:'global'})).toBe(false); expect(sync.remove('hidden-one')).toBe(false); expect(facade.work.state.edits).toEqual([]); dispose();
});
test('destination removal is staged only and an existing removal can be undone', async () => {
  const {facade,controller,observed,dispose}=harness(); observed.sync=[destination()]; await controller.refresh(); const sync=new SyncController(controller);
  expect(sync.remove('destination-one')).toBe(true); expect(observed.sync).toHaveLength(1); expect(destinationRows(facade.work.state,controller.state.document)[0].removed).toBe(true);
  expect(sync.undoRemoval('destination-one')).toBe(true); expect(facade.work.state.edits).toEqual([]); dispose();
});
test('creation consumes exact protected role captures into one typed mode-bound edit preserving Settings', () => {
  const randomUUID=vi.spyOn(crypto,'randomUUID');
  try { for(const first of '0123456789abcdef') {
    const uuid:ReturnType<Crypto['randomUUID']>=`${first}0000000-1111-4111-8111-111111111111`;
    randomUUID.mockReturnValue(uuid);
    const {facade,controller,dispose}=harness(); const sync=new SyncController(controller);
    try {
      controller.setPublic('setting.boolean',{kind:'boolean',value:true}); facade.stage([...facade.work.state.edits,...captures()]);
      expect(sync.create('legacy')).toBe(true); expect(facade.work.state.edits).toHaveLength(2);
      const destinationId=`destination.${uuid}`;
      expect(facade.work.state.edits[1]).toMatchObject({kind:'add_sync_destination',destination:{mode:'legacy',id:destinationId,proxy:{kind:'global'},feeds:[{feedId:'battlelog',desired:'inherit'},{feedId:'missions',desired:'inherit'}]}});
      expect(facade.work.state.edits.filter(edit=>'fieldId' in edit && edit.fieldId.startsWith('sync.'))).toEqual([]);
      expect(sync.setFeed(destinationId,'missions','off')).toBe(true); expect(sync.setProxy(destinationId,{kind:'none'})).toBe(true);
      expect(facade.work.state.edits).toHaveLength(4);
    } finally { dispose(); }
  } } finally { randomUUID.mockRestore(); }
});
test('creation requires both protected captures, refuses non-creatable and hidden modes without partial changes', () => {
  const {facade,controller,dispose}=harness(); const sync=new SyncController(controller); facade.stage(captures().slice(0,1)); const before=canonicalData(facade.work.state.edits);
  expect(sync.create('legacy')).toBe(false); expect(sync.create('sidecar')).toBe(false); expect(sync.create('majel')).toBe(false); expect(canonicalData(facade.work.state.edits)).toBe(before); dispose();
});
test('creation refuses stale captured draft revision and removing new destination cancels only staged creation', () => {
  const {facade,controller,dispose}=harness(); const sync=new SyncController(controller,()=> 'new-destination'); const stale=captures();
  if(stale[0].kind==='set_private') stale[0].reference.capturedFor={...clean().draft,revision:'2'}; facade.stage(stale); expect(sync.create('legacy')).toBe(false);
  facade.stage(captures()); expect(sync.create('legacy')).toBe(true); expect(sync.remove('new-destination')).toBe(true); expect(facade.work.state.edits).toEqual([]); dispose();
});
