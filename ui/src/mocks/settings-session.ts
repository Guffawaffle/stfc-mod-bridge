import { BridgeClient, canonicalData, captureData, decodeReply, decodeRequest, type DeepReadonly } from '../client';
import { semanticPlanDigest } from '../client/relations';
import type { CommitInput, ConfigurationEdit, DocumentSnapshot, DraftSnapshot, OperationSnapshot, PreparedPlan, PrivateValueRef, Reply, Request, Snapshot, TargetSelector } from '../generated/protocol';
import { BridgeFacade } from '../state';
import { ManualClock } from './clock';
import { stageAcknowledgement } from '../../../contracts/fixtures/configuration-cases';

export const settingsModes = ['save', 'discard', 'uncertain', 'save_failed', 'stale', 'protected'] as const;
export type SettingsMode = typeof settingsModes[number];
export interface SettingsRecord { readonly method: string; readonly request: string; readonly reply?: string; readonly delivery: 'received' | 'lost'; }
export interface SettingsDelivery { readonly requests: number; readonly pending: number; readonly processing: boolean; readonly disposed: boolean; readonly lastFault?: 'unexpected_request' | 'invalid_request'; }
export interface SettingsSession { readonly mode: SettingsMode; readonly facade: BridgeFacade; readonly client: BridgeClient; readonly clock: ManualClock;
  readonly records: readonly SettingsRecord[]; readonly provenance: readonly {readonly id:string;readonly sha256:string}[]; readonly delivery: SettingsDelivery;
  subscribe(listener:(state:SettingsDelivery)=>void):()=>void; settle():Promise<void>; dispose():void; }
const rawFixtures = import.meta.glob('../../../contracts/fixtures/*.json',{query:'?raw',import:'default'});
const clone = <T>(value:T):T => JSON.parse(JSON.stringify(value));
const uid = (number:number):string => number.toString(16).padStart(8,'0') + '-1111-4111-8111-111111111111';
const equal = (a:unknown,b:unknown):boolean => canonicalData(a)===canonicalData(b);

/** Bounded configuration fixture oracle. Every result is an explicit synthetic DTO, never native policy. */
export async function createSettingsSession(mode:SettingsMode):Promise<SettingsSession> {
  if (!settingsModes.includes(mode)) throw new Error('settings_mode');
  const provenance:{id:string;sha256:string}[]=[];
  async function source(id:string):Promise<any> {
    const loader=rawFixtures[`../../../contracts/fixtures/${id}.json`]; if(!loader) throw new Error('settings_fixture');
    const raw=await loader(); if(typeof raw!=='string') throw new Error('settings_fixture_raw'); decodeReply(raw);
    const hash=await crypto.subtle.digest('SHA-256',new TextEncoder().encode(raw)); provenance.push({id,sha256:[...new Uint8Array(hash)].map(byte=>byte.toString(16).padStart(2,'0')).join('')}); return JSON.parse(raw);
  }
  const snapshot:Snapshot=(await source('sc15-complete-empty-snapshot-reply')).body.result.query.output;
  snapshot.installations=(await source('sc-01-registered-installations-list-reply')).body.result.query.output;
  snapshot.profiles=(await source('sc-05-catalog-with-duplicate-display-names-reply')).body.result.query.output;
  const read=(await source('sc09-schema-all-field-types-reply')).body.result.query.output;
  let document:DocumentSnapshot=clone(read.value);
  const clean:DraftSnapshot=(await source('sc08-open-clean-draft-reply')).body.result.command.output;
  const planTemplate:PreparedPlan=(await source('sc10-save-reviewed-draft-reply')).body.result.command.output;
  const terminalTemplate:OperationSnapshot=(await source('sc10-save-verified-result-reply')).body.result.query.output.operation.value;
  const privateTemplate=(await source('sc09-protected-private-entry-reply')).body.result.command.output.outcome.reference;
  const secretTemplate=(await source('sc09-protected-secret-entry-reply')).body.result.command.output.outcome.reference;
  const rejection=(await source('sc10-save-backup-unavailable-reply')).body;
  const clock=new ManualClock(), records:SettingsRecord[]=[], listeners=new Set<(state:SettingsDelivery)=>void>();
  let draft=clone(clean), plan:PreparedPlan|undefined, operation:OperationSnapshot|undefined, committed:CommitInput|undefined;
  let sequence=0, lifecycle=0, pending=0, processing=0, disposed=false, fault:SettingsDelivery['lastFault'], lost=false, applied=false;
  const selector:TargetSelector={installation:{kind:'registered',id:'a'.repeat(32)},profile:{kind:'ordinary'}};
  const state=():SettingsDelivery=>Object.freeze({requests:records.length,pending,processing:processing>0,disposed,...(fault?{lastFault:fault}:{})});
  const publish=()=>{for(const listener of listeners) listener(state());};
  function refuse():never {fault='unexpected_request';publish();throw {code:'delivery_failed',delivery:'not_sent'};}
  function applyDocument():void {
    if(applied||!operation||!plan||plan.semantics.capture.kind!=='save_configuration') return;
    applied=true; const capture=plan.semantics.capture.input.draft;
    for(const edit of capture.edits) {
      if('fieldId' in edit) {
        document.fields=document.fields.filter(field=>field.fieldId!==edit.fieldId);
        if(edit.kind==='set_public') document.fields.push({fieldId:edit.fieldId,overridden:true,value:{kind:'public',value:clone(edit.value)}});
        else if(edit.kind==='set_private') document.fields.push({fieldId:edit.fieldId,overridden:true,value:{kind:'private',reference:clone(edit.reference)}});
        else if(edit.kind==='replace_secret'||edit.kind==='clear_secret') document.fields.push({fieldId:edit.fieldId,overridden:true,value:{kind:'secret',configured:edit.kind==='replace_secret'}});
      } else if(edit.kind==='add_sync_destination') {
        const definition=document.schema.sync.find(type=>type.mode===edit.destination.mode);if(!definition) refuse();
        document.sync.push({id:edit.destination.id,mode:edit.destination.mode,exposure:definition.exposure,endpoint:clone(edit.destination.endpoint),secretConfigured:true,desiredProxy:clone(edit.destination.proxy),
          resolvedProxy:{status:'unavailable',reason:'native_unavailable'},feeds:edit.destination.feeds.map(feed=>({feedId:feed.feedId,desired:feed.desired,resolved:{status:'unavailable',reason:'native_unavailable'}}))});
      } else if(edit.kind==='remove_sync_destination') document.sync=document.sync.filter(row=>row.id!==edit.destinationId);
      else if(edit.kind==='set_sync_feed') {const row=document.sync.find(row=>row.id===edit.destinationId);if(!row) refuse();const feed=row.feeds.find(row=>row.feedId===edit.feedId);if(feed) feed.desired=edit.value;else row.feeds.push({feedId:edit.feedId,desired:edit.value,resolved:{status:'unavailable',reason:'native_unavailable'}});}
      else if(edit.kind==='set_sync_proxy') {const row=document.sync.find(row=>row.id===edit.destinationId);if(!row) refuse();row.desiredProxy=clone(edit.value);}
    }
    const completed=terminalTemplate.state;
    if(completed.status!=='completed'||completed.outcome.kind!=='changed'||completed.outcome.receipt?.kind!=='configuration_written') refuse();
    document.binding=clone(completed.outcome.receipt.document);
    // The mock's saved observation belongs to the completed document revision,
    // while draft-scoped entry custody ends with that synthetic Save.
    const saved=(reference:PrivateValueRef):PrivateValueRef=>{
      const {capturedFor,...stored}=clone(reference);
      return {...stored,document:clone(document.binding)};
    };
    for(const field of document.fields) if(field.value.kind==='private') field.value.reference=saved(field.value.reference);
    for(const destination of document.sync) {
      destination.endpoint=saved(destination.endpoint);
      if(destination.desiredProxy.kind==='custom') destination.desiredProxy.reference=saved(destination.desiredProxy.reference);
    }
  }
  async function respond(request:DeepReadonly<Request>):Promise<{body:Reply['body'];drop?:boolean}> {
    if(request.body.type==='query') {
      const query=request.body.query;let output:unknown;
      if(query.name==='snapshot') output=snapshot;
      else if(query.name==='read_configuration') {if(!equal(query.input.target,selector)) refuse();output={...read,value:clone(document)};}
      else if(query.name==='get_operation') {
        if(!operation||!plan||query.input.operationId!==operation.operationId) refuse();applyDocument();
        operation={...operation,operationRevision:'3',state:clone(terminalTemplate.state)};
        output={operation:{...read,value:clone(operation)}};
      } else refuse();
      return {body:{type:'result',result:{type:'query',query:{name:query.name,output} as any}}};
    }
    const command=request.body.command;let output:unknown,drop=false;
    if(command.name==='open_draft') {
      if(!equal(command.input.document,document.binding)) refuse();
      if(!equal(draft.draft.document,document.binding)||draft.state!=='clean') draft={...clone(clean),draft:{...clone(clean.draft),draftId:uid(4000+ ++lifecycle),document:clone(document.binding)},schema:clone(document.schema)};
      output=clone(draft);
    } else if(command.name==='request_sensitive_input') {
      if(!equal(command.input.draft,draft.draft)) refuse();const field=draft.schema.fields.find(field=>field.fieldId===command.input.fieldId);
      if(!field||field.sensitivity!==command.input.sensitivity) refuse();
      const reference=command.input.sensitivity==='private'?{...clone(privateTemplate),fieldId:field.fieldId,document:clone(draft.draft.document),capturedFor:clone(draft.draft),valueId:uid(4200+records.length)}
        :{...clone(secretTemplate),fieldId:field.fieldId,draft:clone(draft.draft),secretId:uid(4200+records.length)};
      output={binding:command.input,outcome:{status:command.input.sensitivity==='private'?'captured_private':'captured_secret',reference}};
    } else if(command.name==='set_draft_changes') {
      if(!equal(command.input.draft,draft.draft)) refuse();
      const apply=[...new Set(command.input.edits.flatMap(edit=>'fieldId' in edit?draft.schema.fields.filter(field=>field.fieldId===edit.fieldId).map(field=>field.apply):['next_launch' as const]))];
      if(apply.length>3) refuse();
      const candidate={...clone(draft),edits:clone(command.input.edits) as ConfigurationEdit[],apply:apply as DraftSnapshot['apply'],state:command.input.edits.length?'dirty':'clean',validation:[]} as DraftSnapshot;
      const acknowledgement=stageAcknowledgement(draft.draft,candidate);draft=clone(acknowledgement.snapshot);output=acknowledgement;
    } else if(command.name==='prepare') {
      if(command.input.intent.kind!=='save_configuration'||!equal(command.input.intent.input.draft,draft.draft)) refuse();
      if(mode==='save_failed'||mode==='stale') return {body:{...clone(rejection),error:{...clone(rejection.error),code:mode==='stale'?'stale_revision':'backup_unavailable'}}};
      plan=clone(planTemplate);if(plan.semantics.capture.kind!=='save_configuration') refuse();
      plan.semantics.capture.input.draft=clone(draft);plan.planRef.planId=uid(4400+records.length);plan.planRef.reviewDigest=await semanticPlanDigest(plan.semantics);output=clone(plan);
    } else if(command.name==='commit') {
      if(!plan||!equal(command.input.planRef,plan.planRef)) refuse();
      if(committed) {if(!equal(command.input,committed)) refuse();}
      else {committed=clone(command.input);operation={...clone(terminalTemplate),semantics:clone(plan.semantics),operationRevision:'1',state:{status:'admitted'}};}
      output=clone(operation); if(mode==='uncertain'&&!lost){lost=true;drop=true;}
    } else if(command.name==='discard_draft') {
      if(!equal(command.input.draft,draft.draft)||committed) refuse();
      output={draftId:draft.draft.draftId,hostEpoch:draft.draft.hostEpoch,previousRevision:draft.draft.revision};draft={...clone(clean),draft:{...clone(clean.draft),draftId:uid(4000+ ++lifecycle),document:clone(document.binding)},schema:clone(document.schema)};
    } else refuse();
    return {body:{type:'result',result:{type:'command',command:{name:command.name,output} as any}},drop};
  }
  const client=new BridgeClient({subscribe:()=>()=>{},exchange(raw,{signal}) {
    if(disposed||signal.aborted||records.length>=256||pending>=64) return Promise.reject({code:'delivery_failed',delivery:'not_sent'});
    let request:DeepReadonly<Request>;try{request=decodeRequest(raw);}catch{fault='invalid_request';publish();return Promise.reject({code:'delivery_failed',delivery:'not_sent'});}
    processing++;const index=records.length;records.push({method:request.body.type==='query'?request.body.query.name:request.body.command.name,request:raw,delivery:'received'});publish();
    return new Promise((resolve,reject)=>{
      let done=false,scheduled=false,release=()=>{};const abort=()=>{if(done)return;done=true;release();if(scheduled){scheduled=false;pending--;}publish();reject({code:'delivery_failed',delivery:'may_have_reached_backend'});};signal.addEventListener('abort',abort,{once:true});
      void respond(request).then(response=>{
        processing--;if(done||disposed)return;
        const reply=JSON.stringify({protocolVersion:1,requestId:request.requestId,body:response.body});decodeReply(reply);
        records[index]={...records[index],reply,delivery:response.drop?'lost':'received'};scheduled=true;pending++;publish();
        release=clock.schedule(200,()=>{if(response.drop){publish();return;}scheduled=false;pending--;publish();done=true;signal.removeEventListener('abort',abort);resolve(reply);});
      }).catch(()=>{processing=Math.max(0,processing-1);fault='unexpected_request';if(!done){done=true;signal.removeEventListener('abort',abort);reject({code:'delivery_failed',delivery:'not_sent'});}publish();});
    });
  }},{clock,timeoutMs:1200,requestId:()=>uid(++sequence+5000)});
  const facade=new BridgeFacade(client,{idempotencyKey:()=>uid(6000+lifecycle)});facade.requestTarget(selector);facade.work.bindTarget(document.binding.target);facade.work.openDraft(clean);facade.work.observations.observeDraft(clean);facade.navigate('settings');
  return {mode,facade,client,clock,provenance:captureData(provenance),get records(){return captureData(records);},get delivery(){return state();},subscribe(listener){listeners.add(listener);listener(state());return()=>listeners.delete(listener);},
    async settle(){for(let turn=0;turn<128&&!disposed;turn++){for(let loop=0;loop<16;loop++)await Promise.resolve();await new Promise<void>(resolve=>setTimeout(resolve,0));if(processing)continue;if(!clock.pendingCount)return;if(client.pendingCount&&!pending)continue;clock.runNext();}if(!disposed&&clock.pendingCount)throw new Error('settings_settle_limit');},
    dispose(){if(disposed)return;disposed=true;facade.dispose();client.dispose();clock.dispose();publish();listeners.clear();}};
}
