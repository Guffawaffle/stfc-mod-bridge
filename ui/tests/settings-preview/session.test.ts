import { readFileSync } from 'node:fs';
import { createHash } from 'node:crypto';
import { expect,test,vi } from 'vitest';
import { canonicalData,decodeReply,decodeRequest } from '../../src/client';
import { createSettingsSession,settingsModes,type SettingsSession } from '../../src/mocks/settings-session';
import { SettingsController } from '../../src/views/settings';
import { SyncController } from '../../src/views/sync';
const fixtures=new URL('../../../contracts/fixtures/',import.meta.url);
async function settle<T>(session:SettingsSession,pending:Promise<T>):Promise<T>{await session.settle();return pending;}
async function start(mode:typeof settingsModes[number]='save'){const session=await createSettingsSession(mode);const controller=new SettingsController(session.facade,'windows');await settle(session,session.facade.connect());await settle(session,controller.refresh());return{session,controller,dispose(){controller.dispose();session.dispose();}};}
function latestAcknowledgement(session:SettingsSession){
  const staged=session.records.filter(row=>row.method==='set_draft_changes').at(-1);if(!staged?.reply)throw new Error('Missing staged acknowledgement');
  const reply=decodeReply(staged.reply);
  if(reply.body.type!=='result'||reply.body.result.type!=='command'||reply.body.result.command.name!=='set_draft_changes')throw new Error('Wrong staged acknowledgement');
  return reply.body.result.command.output;
}

test('Settings preview modes bind every source hash and validate every recorded wire frame',async()=>{
  for(const mode of settingsModes){const {session,dispose}=await start(mode);try{for(const source of session.provenance)expect(source.sha256).toBe(createHash('sha256').update(readFileSync(new URL(source.id+'.json',fixtures))).digest('hex'));
    for(const record of session.records){expect(()=>decodeRequest(record.request)).not.toThrow();if(record.reply)expect(()=>decodeReply(record.reply!)).not.toThrow();}expect(session.delivery.lastFault).toBeUndefined();expect(session.facade.work.state.selector?.profile.kind).toBe('ordinary');}finally{dispose();}}
});

test('Settings mock current-draft reads use retained successors and the actual event cursor, then preserve local input on Missing',async()=>{
  const {session,controller,dispose}=await start();try{
    const initial=session.facade.work.state.draft!, lookup={hostEpoch:initial.draft.hostEpoch,draftId:initial.draft.draftId};
    const first=await settle(session,session.client.query('get_draft',lookup));
    expect(first).toMatchObject({kind:'result',value:{cursor:{sequence:'0'},draft:{status:'observed',value:initial}}});
    expect(controller.setPublic('setting.boolean',{kind:'boolean',value:true})).toBe(true);
    const acknowledgement=await settle(session,session.client.command('set_draft_changes',{draft:initial.draft,edits:session.facade.work.state.edits}));
    if(acknowledgement.kind!=='result')throw new Error('synthetic stage refused');
    expect(session.facade.work.state.draft?.draft.revision).toBe('1');
    await settle(session,session.facade.refresh());
    expect(session.facade.work.state.draft).toEqual(acknowledgement.value.snapshot);
    const successor=await settle(session,session.client.query('get_draft',lookup));
    expect(successor).toMatchObject({kind:'result',value:{cursor:{sequence:'1'},draft:{status:'observed',value:acknowledgement.value.snapshot}}});
    expect(controller.setNumeric('setting.integer','-')).toBe(false);const retained=session.facade.work.state;
    const discarded=await settle(session,session.client.command('discard_draft',{draft:acknowledgement.value.snapshot.draft}));
    expect(discarded.kind).toBe('result');
    await settle(session,session.facade.refresh());
    const missing=await settle(session,session.client.query('get_draft',lookup));
    expect(missing).toMatchObject({kind:'result',value:{cursor:{sequence:'1'},draft:{status:'missing'}}});
    expect(session.facade.work.state.draft).toBe(retained.draft);expect(session.facade.work.state.edits).toBe(retained.edits);
    expect(session.facade.work.state.publicInputs).toBe(retained.publicInputs);expect(session.facade.work.state.draftConflict).toBe(true);
    expect(session.records.some(row=>row.method==='open_draft')).toBe(false);
    expect(await settle(session,session.client.query('get_draft',{...lookup,hostEpoch:'00009003-1111-4111-8111-111111111111'})))
      .toMatchObject({kind:'rejected',error:{code:'plan_host_mismatch'}});
    expect(session.delivery.lastFault).toBeUndefined();
  }finally{dispose();}
});
test('Settings preview Save remains admitted until exact completion then reopens authoritative clean baseline',async()=>{
  const {session,controller,dispose}=await start();try{controller.setPublic('setting.boolean',{kind:'boolean',value:true});await settle(session,session.facade.prepareSaveInPlace());expect(session.facade.state.transition.kind).toBe('review');
    await settle(session,session.facade.commitSave());expect(session.facade.state.transition.kind).toBe('observing');expect(session.facade.work.state.dirty).toBe(true);
    await settle(session,session.facade.reconcileSave());expect(session.facade.state.transition.kind).toBe('idle');expect(session.facade.work.state.draft?.draft.document.revision).toBe('synthetic-document-2');expect(session.facade.work.state.dirty).toBe(false);expect(session.delivery.lastFault).toBeUndefined();
    expect(session.records.map(row=>row.method).slice(-4)).toEqual(['get_operation','read_configuration','open_draft','read_configuration']);expect(controller.state.document?.fields.find(field=>field.fieldId==='setting.boolean')?.value).toEqual({kind:'public',value:{kind:'boolean',value:true}});}finally{dispose();}
});
test('Settings preview unfinished numeric text survives view changes and blocks Save until deliberate reset',async()=>{
  const {session,controller,dispose}=await start();try{controller.setNumeric('setting.integer','-');session.facade.navigate('data_sync');expect(session.facade.work.state.publicInputs?.[0].text).toBe('-');expect(await session.facade.prepareSaveInPlace()).toBeUndefined();
    expect(session.records.some(row=>row.method==='set_draft_changes')).toBe(false);session.facade.navigate('settings');expect(controller.resetNumeric('setting.integer')).toBe(true);expect(session.facade.work.state.dirty).toBe(false);}finally{dispose();}
});
test('Settings preview Discard confirmation and Stay retain or clear only the reviewed draft',async()=>{
  const {session,controller,dispose}=await start('discard');try{controller.setPublic('setting.boolean',{kind:'boolean',value:true});expect(session.facade.prepareDiscardInPlace()).toBe(true);expect(session.facade.stay()).toBe(true);expect(session.facade.work.state.dirty).toBe(true);expect(session.records.some(row=>row.method==='discard_draft')).toBe(false);
    expect(session.facade.prepareDiscardInPlace()).toBe(true);expect(await settle(session,session.facade.confirmDiscard())).toBe(true);expect(session.facade.work.state.dirty).toBe(false);expect(session.facade.work.state.selector?.profile.kind).toBe('ordinary');expect(session.delivery.lastFault).toBeUndefined();}finally{dispose();}
});
test('Settings preview protected captures create one mode-bound destination then stage feed and proxy in the shared draft',async()=>{
  const {session,controller,dispose}=await start('protected');const randomUUID=vi.spyOn(crypto,'randomUUID').mockReturnValue('00000000-1111-4111-8111-111111111111');
  try{const sync=new SyncController(controller);await settle(session,controller.captureProtected('sync.endpoint'));await settle(session,controller.captureProtected('sync.token'));
    expect(sync.create('legacy')).toBe(true);const destinationId='destination.00000000-1111-4111-8111-111111111111';expect(sync.setFeed(destinationId,'missions','off')).toBe(true);expect(sync.setProxy(destinationId,{kind:'none'})).toBe(true);
    controller.setPublic('setting.boolean',{kind:'boolean',value:true});await settle(session,session.facade.prepareSaveInPlace());expect(session.facade.state.transition.kind).toBe('review');
    const staged=session.records.find(row=>row.method==='set_draft_changes')!;expect(JSON.stringify(decodeReply(staged.reply!))).toContain('protectedTransfers');expect([...latestAcknowledgement(session).snapshot.apply].sort()).toEqual(['immediate','next_launch']);expect(session.facade.work.state.edits).toHaveLength(4);expect(session.delivery.lastFault).toBeUndefined();}finally{randomUUID.mockRestore();dispose();}
});
test('Settings preview structural Sync acknowledgements keep next-launch timing for add remove feed and proxy',async()=>{
  for(const kind of ['add_sync_destination','remove_sync_destination','set_sync_feed','set_sync_proxy'] as const){
    const {session,controller,dispose}=await start('protected');
    try{
      const sync=new SyncController(controller);
      expect(await settle(session,controller.captureProtected('sync.endpoint'))).toBe(true);expect(await settle(session,controller.captureProtected('sync.token'))).toBe(true);
      expect(sync.create('legacy')).toBe(true);
      const created=session.facade.work.state.edits.find(edit=>edit.kind==='add_sync_destination');
      if(created?.kind!=='add_sync_destination')throw new Error('Missing staged destination');
      const destinationId=created.destination.id;
      await settle(session,session.facade.prepareSaveInPlace());expect(session.facade.state.transition.kind).toBe('review');expect(latestAcknowledgement(session).snapshot.apply).toEqual(['next_launch']);
      if(kind!=='add_sync_destination'){
        const acknowledged=session.facade.work.state.edits.find(edit=>edit.kind==='add_sync_destination');
        if(acknowledged?.kind!=='add_sync_destination')throw new Error('Missing acknowledged destination');
        await settle(session,session.facade.commitSave());expect(session.facade.state.transition.kind).toBe('observing');
        await settle(session,session.facade.reconcileSave());expect(session.facade.state.transition.kind).toBe('idle');expect(session.facade.work.state.dirty).toBe(false);
        const saved=controller.state.document;if(!saved)throw new Error('Missing saved destination baseline');
        expect(saved.sync.map(destination=>destination.id)).toEqual([destinationId]);expect(saved.sync[0].endpoint.document).toEqual(saved.binding);
        expect(Object.hasOwn(saved.sync[0].endpoint,'capturedFor')).toBe(false);expect(saved.sync[0].endpoint.valueId).toBe(acknowledged.destination.endpoint.valueId);
        expect(kind==='remove_sync_destination'?sync.remove(destinationId):kind==='set_sync_feed'?sync.setFeed(destinationId,'missions','off'):sync.setProxy(destinationId,{kind:'none'})).toBe(true);
        await settle(session,session.facade.prepareSaveInPlace());expect(session.facade.state.transition.kind).toBe('review');
      }
      expect(session.facade.work.state.edits.map(edit=>edit.kind)).toEqual([kind]);expect(latestAcknowledgement(session).snapshot.apply).toEqual(['next_launch']);
      expect(session.records.filter(row=>row.method==='commit')).toHaveLength(kind==='add_sync_destination'?0:1);expect(session.delivery.lastFault).toBeUndefined();
      for(const record of session.records){expect(()=>decodeRequest(record.request)).not.toThrow();if(record.reply)expect(()=>decodeReply(record.reply!)).not.toThrow();}
    }finally{dispose();}
  }
});
test('Settings preview lost delivery permits only exact explicit replay and then authoritative completion',async()=>{
  const {session,controller,dispose}=await start('uncertain');try{controller.setPublic('setting.boolean',{kind:'boolean',value:true});await settle(session,session.facade.prepareSaveInPlace());await settle(session,session.facade.commitSave());expect(session.facade.state.transition.kind).toBe('uncertain');expect(session.records.filter(row=>row.method==='commit')).toHaveLength(1);
    await settle(session,session.facade.replaySave());const commits=session.records.filter(row=>row.method==='commit').map(row=>decodeRequest(row.request));expect(commits).toHaveLength(2);expect(canonicalData(commits[0].body)).toBe(canonicalData(commits[1].body));expect(commits[0].requestId).not.toBe(commits[1].requestId);
    await settle(session,session.facade.reconcileSave());expect(session.facade.work.state.dirty).toBe(false);expect(session.delivery.lastFault).toBeUndefined();}finally{dispose();}
});
test('Settings preview failed and stale Save preserve synchronized edits and permit editing without implicit retry',async()=>{
  for(const mode of ['save_failed','stale'] as const){const {session,controller,dispose}=await start(mode);try{controller.setPublic('setting.boolean',{kind:'boolean',value:true});await settle(session,session.facade.prepareSaveInPlace());expect(session.facade.state.transition.kind).toBe('idle');expect(session.facade.work.state.dirty).toBe(true);expect(session.records.some(row=>row.method==='commit')).toBe(false);expect(controller.setPublic('setting.boolean',{kind:'boolean',value:false})).toBe(true);expect(session.delivery.lastFault).toBeUndefined();}finally{dispose();}}
});
test('Settings preview disposal ends owned clocks and observations without accepting replacement session state',async()=>{
  const old=await createSettingsSession('save');const pending=old.facade.connect();old.dispose();expect((await pending).kind).toBe('fault');expect(old.clock.disposed).toBe(true);expect(old.delivery.disposed).toBe(true);const fresh=await createSettingsSession('save');expect(fresh.facade.work.state.dirty).toBe(false);expect(fresh.records).toEqual([]);fresh.dispose();
});
