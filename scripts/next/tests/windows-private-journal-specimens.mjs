// Controlled synthetic specimens for pure Node parser tests; never native evidence.
import { STAGES, OBJECTS, VARIANTS, NATIVE_TESTS, PARENT_MARKER } from '../windows-private-journal-evidence.mjs';
export const artifact = { sha256:'a'.repeat(64), bytes:1048576 };
let serial=0;
export const nextNonce=()=>`${(++serial).toString(16).padStart(8,'0')}-1111-4111-8111-111111111111`;
export function normalTrace(primary=0) {
  return { events:[{event:'CreateReturned',phase:'Wal',primary},...STAGES.flatMap(stage=>[
    {event:'FlushStarted',stage},{event:'NativeFlushReturned',stage,primary},{event:'NativeFlushAccepted',stage,primary}]),{event:'ConstructorValidated'}],traceFailed:false,fired:false,terminal:null };
}
export function refusalTrace(primary=-1073741757, phase='FixturesRoot') {
  return {events:[{event:'CreateReturned',phase,primary}],traceFailed:false,fired:false,terminal:{phase,kind:primary===259?'PendingQuarantined':'OtherConstructorRefusal',primary}};
}
export function injectedTrace(stage,primary=0) {
  return {events:[{event:'CreateReturned',phase:'Wal',primary},...STAGES.slice(0,STAGES.indexOf(stage)+1).flatMap(s=>[
    {event:'FlushStarted',stage:s},{event:'NativeFlushReturned',stage:s,primary},{event:'NativeFlushAccepted',stage:s,primary}]),{event:'FaultInjected',stage,primary}],
    traceFailed:false,fired:true,terminal:{phase:`Flush(${stage})`,kind:'InjectedAfterNativeSuccess',primary}};
}
export function binding(creation=133776543210000001n,volume=18446744073709551615n) {
  return {creationFiletime:creation,executableVolume:volume,executableFileId:'f'.repeat(32),executableSha256:artifact.sha256,executableBytes:artifact.bytes};
}
export function cleanup(success=true,exitCode=success?0:1) {
  return {state:'ReapedAndReadersJoined',killError:false,waitError:false,readerError:false,exitSuccess:success,exitCode,actualExitObserved:true};
}
function child(nonce,phase,event,seq) {
  const details=event==='Opened'?{trace:normalTrace()}:event==='OpenRefused'?{failure:'Busy',trace:refusalTrace()}:event==='Ready'?{
    'hold-owner':{heldOwner:true},'durable-append':{appendReturned:true,syncAcknowledged:true},'torn-append':{partialTailFlushed:true,appendReturned:false},'corrupt-complete':{completeCorruptionFlushed:true}}[phase]:{};
  return {schemaVersion:1,nonce,phase,seq,event,details};
}
export function envelope(caseName,details) {
  return {schemaVersion:1,case:caseName,details,foreignOwner:{status:'not_observed',reason:'foreign_owner_seed_not_declared'},
    deferred:['reparse_hard_link','writable_mapping','broader_bounds_custody_loss','known_folder_configuration_redirection'],
    privateJournalOwnerQualified:false,installedGameQualified:false,releaseQualified:false};
}
export function specimen(index) {
  let nonce;
  if(index===0)return [envelope('fresh_reopen',{nonce:nextNonce(),sameIdentity:true,directoryCount:8,currentUserOwner:true,exactPrivateDacl:true,localWritableAclNtfs:true,leafLinks:1,fresh:normalTrace(),reopen:normalTrace()})];
  if(index===1){const rows=OBJECTS.flatMap(object=>VARIANTS.map(variant=>envelope('dacl_variant',{nonce:nextNonce(),object,variant,descriptorUnchanged:true,identityLengthUnchanged:true,trace:refusalTrace(-1073741790,object)})));
    return [...rows,envelope('dacl_refusal',{variants:rows.map(({details:{nonce,object,variant}})=>({nonce,object,variant})),liveBytesUnchanged:true,liveWriteRefusedBeforeLengthChange:true,latchedRefusal:true})];}
  if(index===2)return[envelope('sharing',{nonce:nextNonce(),independentPrivateObjects:4,win32SharingViolation:32,writerAndDeleteRefused:true,sameAdmissionsAfterRelease:true,trace:normalTrace()})];
  if(index===3){nonce=nextNonce();return[envelope('cross_process',{nonce,ready:child(nonce,'hold-owner','Ready',3),refusal:['Started','OpenRefused'].map((e,i)=>child(nonce,'try-open',e,i+1)),reopen:['Started','Opened','Released'].map((e,i)=>child(nonce,'open-and-release',e,i+1)),childBindings:[binding(),binding(),binding()],cleanup:[cleanup(),cleanup(),cleanup()]})];}
  if(index===4){const rows=STAGES.map(stage=>envelope('constructor_failure_stage',{nonce:nextNonce(),stage,first:injectedTrace(stage),reopen:normalTrace()}));return [...rows,envelope('completed_constructor_failures',{attempts:rows.map(({details:{nonce,stage}})=>({nonce,stage}))})];}
  if([5,6,7].includes(index)){nonce=nextNonce();const common={nonce,ready:child(nonce,['durable-append','torn-append','corrupt-complete'][index-5],'Ready',3),childBinding:binding(),cleanup:cleanup(false),sha256:'b'.repeat(64)};
    return[envelope(['codec_durable','codec_torn','codec_corrupt'][index-5],{...common,...(index===5?{records:2,byteLength:308}:index===6?{beforeLength:173,repairedLength:158,secondOpenStable:true}:{corruptJournal:true,bytesUnchanged:true})})];}
  nonce=nextNonce();return[envelope('child_failure_cleanup',{nonce,childBindings:[binding(),binding()],earlyEof:cleanup(false),ready:child(nonce,'hold-owner','Ready',3),dropCleanup:cleanup(false),reopen:normalTrace()})];
}
export const json=value=>JSON.stringify(value,(_key,value)=>typeof value==='bigint'?JSON.rawJSON(value.toString()):value);
export function output(index,rows=specimen(index)) {
  return Buffer.from(`running 1 test\ntest ${NATIVE_TESTS[index]} ... ${rows.map(row=>PARENT_MARKER+json(row)).join('\n')}\nok\n\ntest result: ok. 1 passed; 0 failed; 0 ignored; 0 measured; 37 filtered out; finished in 0.01s\n`,'utf8');
}
