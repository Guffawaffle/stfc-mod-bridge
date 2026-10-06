import test from 'node:test';
import assert from 'node:assert/strict';
import {readFileSync} from 'node:fs';
import { FixtureEvidenceError, LIMITS, NATIVE_TESTS, PARENT_MARKER, STAGES, parseFixtureJson,
  parseFixtureOutput, parseFixtureSuite, collectOutput, isExactU64, sameU64 } from '../windows-private-journal-evidence.mjs';
import {artifact, binding, cleanup, nextNonce, normalTrace, injectedTrace, specimen, envelope, output, json} from './windows-private-journal-specimens.mjs';
const parse=(index,rows=specimen(index))=>parseFixtureOutput(output(index,rows),{testName:NATIVE_TESTS[index],artifact});
const bytes=value=>Buffer.from(value,'utf8');
function rejected(callback,code) { assert.throws(callback,error=>error instanceof FixtureEvidenceError && (!code || error.code===code)); }
function mutate(index,change,code){const rows=specimen(index);change(rows);rejected(()=>parse(index,rows),code);}

test('actual pinned Node and committed native fixture source case inventory',()=>{
  assert.equal(process.version,'v24.14.1');
  const source=readFileSync(new URL('../../../crates/bridge-platform-windows/src/private_journal/native_fixtures.rs',import.meta.url),'utf8');
  const names=[...source.matchAll(/fn (native_private_journal_[a-z0-9_]+)\(/g)].map(match=>'private_journal::native_fixtures::'+match[1]);
  assert.equal(new Set(names).size,9);assert.deepEqual(NATIVE_TESTS,names);
});
for(let index=0;index<9;index++) test(`synthetic positive specimen: ${NATIVE_TESTS[index]}`,()=>{
  const result=parse(index);assert.equal(result.testName,NATIVE_TESTS[index]);assert.ok(Object.isFrozen(result));assert.ok(Object.isFrozen(result.rows[0].details));
  assert.equal(result.rows.length,index===1?13:index===4?7:1);
});
test('whole suite retains exactly25 reported distinct nonces, not26 enumerated trees',()=>{
  const streams=NATIVE_TESTS.map((testName,index)=>({testName,stdout:output(index)}));const result=parseFixtureSuite(streams,artifact);
  assert.equal(result.reportedUniqueNonces.length,25);assert.equal(result.actualTreeEnumeration,false);assert.equal(result.unreportedLiveDriftNonce,true);assert.equal(result.privateJournalOwnerQualified,false);
  streams[8].stdout=output(8,[envelope('child_failure_cleanup',{...specimen(8)[0].details,nonce:result.reportedUniqueNonces[0]})]);
  rejected(()=>parseFixtureSuite(streams,artifact));
});
test('lossless u64 adjacent values above2^53 and max remain exact in receipt JSON',()=>{
  const rows=specimen(3);rows[0].details.childBindings[0]=binding(9007199254740992n,0n);rows[0].details.childBindings[1]=binding(9007199254740993n,18446744073709551615n);
  const result=parse(3,rows);const [a,b]=result.rows[0].details.childBindings;
  assert.ok(isExactU64(a.creationFiletime));assert.equal(sameU64(a.creationFiletime,b.creationFiletime),false);assert.equal(a.executableVolume.decimal,'0');assert.equal(b.executableVolume.decimal,'18446744073709551615');
  assert.equal(JSON.parse(JSON.stringify(b)).creationFiletime.decimal,'9007199254740993');assert.equal(JSON.parse(JSON.stringify(b)).executableVolume.type,'u64');
  rejected(()=>sameU64(9007199254740992,9007199254740993),'EXACT_U64_REQUIRED');rejected(()=>sameU64({decimal:'1'},{decimal:'1'}),'EXACT_U64_REQUIRED');
});
test('only declared identity paths preserve u64; unsafe ordinary numbers refuse',()=>{
  const result=parseFixtureJson(bytes('{"details":{"childBinding":{"creationFiletime":18446744073709551615,"executableVolume":0}}}'));
  assert.ok(isExactU64(result.details.childBinding.creationFiletime));
  rejected(()=>parseFixtureJson(bytes('{"creationFiletime":9007199254740993}')),'UNSAFE_INTEGER');
  rejected(()=>parseFixtureJson(bytes('{"details":{"creationFiletime":9007199254740993}}')),'UNSAFE_INTEGER');
  rejected(()=>parseFixtureJson(bytes('{"seq":9007199254740993}')),'UNSAFE_INTEGER');
});
for(const token of ['18446744073709551616','-1','-0','1e3','1.0','"1"','null','true','01']) test(`u64 noncanonical/range token ${token}`,()=>{
  rejected(()=>parseFixtureJson(bytes(`{"details":{"childBinding":{"creationFiletime":${token}}}}`)));
});
test('duplicate decoded members refuse at every depth including escaped aliases',()=>{
  for(const text of ['{"case":1,"case":2}','{"details":{"trace":{"primary":0,"primary":1}}}',
    '{"details":{"childBinding":{"creationFiletime":1,"creation\\u0046iletime":2}}}',
    '{"schemaVersion":1,"\\u0073chemaVersion":1}']) rejected(()=>parseFixtureJson(bytes(text)),'DUPLICATE_MEMBER');
  const object=parseFixtureJson(bytes('{"__proto__":{"polluted":true}}'));assert.equal(Object.getPrototypeOf(object),null);assert.equal({}.polluted,undefined);
});
test('strict JSON rejects trailing values, malformed escapes, dangling surrogates and integer aliases',()=>{
  for(const text of ['{} {}','{"a":1,}','[1,]','{"a":NaN}','{"a":Infinity}','{"a":-0}','{"a":1.1}','{"a":1e3}','{"a":"\\x00"}','{"a":"\\ud800"}','{"a":"\\udc00"}','{"a":']) rejected(()=>parseFixtureJson(bytes(text)));
  assert.equal(parseFixtureJson(bytes('{"a":"\\ud83d\\ude80"}')).a,'🚀');
});
test('JSON byte/depth bounds and whole-output bound refuse before parse',()=>{
  const prefix='{"a":"',suffix='"}'; const exact=prefix+'x'.repeat(LIMITS.parentJsonBytes-prefix.length-suffix.length)+suffix;
  assert.equal(parseFixtureJson(bytes(exact)).a.length,16376);rejected(()=>parseFixtureJson(bytes(exact+' ')),'BYTE_LIMIT');
  rejected(()=>parseFixtureJson(bytes('['.repeat(34)+'0'+']'.repeat(34))),'JSON_STRUCTURE_LIMIT');
  rejected(()=>parseFixtureOutput(Buffer.alloc(LIMITS.outputBytes+1),{testName:NATIVE_TESTS[0],artifact}),'BYTE_LIMIT');
  rejected(()=>collectOutput(Array(8193).fill(Buffer.alloc(0))),'CHUNK_LIMIT');
});
test('strict UTF8 rejects malformed bytes even outside marker; valid split codepoint collects exactly',()=>{
  rejected(()=>parseFixtureJson(Buffer.from([0xc0,0xaf])),'INVALID_UTF8');
  const good=output(0),bad=Buffer.concat([Buffer.from([0xff]),good]);rejected(()=>parseFixtureOutput(bad,{testName:NATIVE_TESTS[0],artifact}),'INVALID_UTF8');
  const full=Buffer.concat([bytes('synthetic 🚀\n'),good]);const pos=full.indexOf(Buffer.from([0xf0]));
  const collected=collectOutput([full.subarray(0,pos+1),full.subarray(pos+1,pos+3),full.subarray(pos+3)]);assert.deepEqual(Buffer.from(collected),full);parseFixtureOutput(collected,{testName:NATIVE_TESTS[0],artifact});
  rejected(()=>parseFixtureJson('{"a":1}'),'RAW_BYTES_REQUIRED');rejected(()=>parseFixtureJson(new Uint8Array(new SharedArrayBuffer(2))),'RAW_BYTES_REQUIRED');
});
for (const index of [0,1,2,3,4,5,6,7,8]) test(`qualification envelope remains closed for case${index}`,()=>{
  mutate(index,rows=>{rows[0].installedGameQualified=true;});mutate(index,rows=>{rows[0].foreignOwner.extra='never';});
  mutate(index,rows=>{delete rows[0].foreignOwner.reason;});mutate(index,rows=>{rows[0].deferred.reverse();});
});
test('selected test/case/prefix and marker framing refuse substitution or smuggling',()=>{
  rejected(()=>parseFixtureOutput(output(2),{testName:NATIVE_TESTS[0],artifact}),'TEST_PREFIX');
  const input=output(0);for(const bad of [input.subarray(0,-1),bytes(input.toString().replace(PARENT_MARKER,'noise '+PARENT_MARKER)),bytes(input.toString().replace(PARENT_MARKER,'BRIDGE_PRIVATE_JOURNAL_CHILD '))]) rejected(()=>parseFixtureOutput(bad,{testName:NATIVE_TESTS[0],artifact}));
  const rows=specimen(0);rows[0].case='sharing';rejected(()=>parse(0,rows),'CONSTANT');
  const fake=output(0).toString().replace(`test ${NATIVE_TESTS[0]} ... `,'')+`test ${NATIVE_TESTS[0]} ... ok\n`;
  rejected(()=>parseFixtureOutput(bytes(fake),{testName:NATIVE_TESTS[0],artifact}));
});
test('multi-row counts, tuple coverage and prescribed order beat matching malicious summaries',()=>{
  mutate(1,rows=>{rows.splice(2,1);});mutate(1,rows=>{rows[2]=structuredClone(rows[1]);});
  mutate(1,rows=>{[rows[0],rows[1]]=[rows[1],rows[0]];[rows[12].details.variants[0],rows[12].details.variants[1]]=[rows[12].details.variants[1],rows[12].details.variants[0]];});
  mutate(1,rows=>{rows.unshift(rows.pop());});mutate(1,rows=>{rows[12].details.variants[0].nonce=nextNonce();});
  mutate(4,rows=>{[rows[0],rows[1]]=[rows[1],rows[0]];[rows[6].details.attempts[0],rows[6].details.attempts[1]]=[rows[6].details.attempts[1],rows[6].details.attempts[0]];});
  mutate(4,rows=>{rows[6].details.attempts[1].stage='Wal';});mutate(4,rows=>{rows.push(rows[0]);});
});
test('fresh nonce uniqueness with otherwise valid row/summary relations',()=>{
  mutate(1,rows=>{rows[1].details.nonce=rows[0].details.nonce;rows[12].details.variants[1].nonce=rows[0].details.nonce;},'DUPLICATE_FRESH_NONCE');
  mutate(4,rows=>{rows[1].details.nonce=rows[0].details.nonce;rows[6].details.attempts[1].nonce=rows[0].details.nonce;},'DUPLICATE_FRESH_NONCE');
  const streams=NATIVE_TESTS.map((testName,index)=>({testName,stdout:output(index)}));const first=parseFixtureOutput(streams[0].stdout,{testName:NATIVE_TESTS[0],artifact}).freshNonces[0];
  const last=specimen(8);last[0].details.nonce=first;last[0].details.ready.nonce=first;streams[8].stdout=output(8,last);
  rejected(()=>parseFixtureSuite(streams,artifact),'DUPLICATE_SUITE_NONCE');
});
test('native summary must be one actual exact vector following markers',()=>{
  for(const replacement of ['test result: ok. 1 passed; 0 failed; 1 ignored; 0 measured; 36 filtered out;',
    'test result: ok. 0 passed; 0 failed; 0 ignored; 0 measured; 38 filtered out;',
    'test result: ok. 01 passed; 0 failed; 0 ignored; 0 measured; 37 filtered out;']){
    const original=output(0).toString();const modified=original.replace(/^test result:[^\r\n]*/gm,replacement);assert.notEqual(modified,original);rejected(()=>parseFixtureOutput(bytes(modified),{testName:NATIVE_TESTS[0],artifact}),'NATIVE_SUMMARY');
  }
  rejected(()=>parseFixtureOutput(Buffer.concat([output(0),bytes('test result: ok. 1 passed; 0 failed; 0 ignored; 0 measured; 37 filtered out;\n')]),{testName:NATIVE_TESTS[0],artifact}),'NATIVE_SUMMARY');
  rejected(()=>parseFixtureOutput(bytes(output(0).toString().replace(/test result:.*\n/,'')),{testName:NATIVE_TESTS[0],artifact}),'EXECUTION_FRAMING');
});
test('parent byte count includes prefix but excludes only exact libtest prefix',()=>{
  const rows=specimen(0),encoded=json(rows[0]);const padded=encoded+' '.repeat(LIMITS.parentJsonBytes-Buffer.byteLength(encoded));
  const valid=bytes(`test ${NATIVE_TESTS[0]} ... ${PARENT_MARKER}${padded}\nok\ntest result: ok. 1 passed; 0 failed; 0 ignored; 0 measured; 37 filtered out;\n`);
  parseFixtureOutput(valid,{testName:NATIVE_TESTS[0],artifact});
  rejected(()=>parseFixtureOutput(bytes(valid.toString().replace(padded,padded+' ')),{testName:NATIVE_TESTS[0],artifact}),'PARENT_LINE_LIMIT');
  const unicode=encoded+'é'.repeat(Math.ceil((LIMITS.parentJsonBytes-Buffer.byteLength(encoded))/2));
  rejected(()=>parseFixtureOutput(bytes(`test ${NATIVE_TESTS[0]} ... ${PARENT_MARKER}${unicode}\nok\ntest result: ok. 1 passed; 0 failed; 0 ignored; 0 measured; 37 filtered out;\n`),{testName:NATIVE_TESTS[0],artifact}));
});
test('successful trace enforces scalar pairing/order, final seal and nonpending status',()=>{
  const good=specimen(0);good[0].details.fresh=normalTrace(1);good[0].details.reopen=normalTrace(42);parse(0,good);
  const changes=[trace=>{trace.events[2].stage='NonceDirectory';},trace=>{trace.events[3].primary=1;},trace=>{trace.events[3].primary=259;},
    trace=>{trace.events[3].primary=-1;},trace=>{trace.events.pop();},trace=>{trace.events.push({event:'ConstructorValidated'});},
    trace=>{trace.events.push({event:'CreateReturned',phase:'Wal',primary:0});},trace=>{trace.events.at(-1).primary=0;},trace=>{trace.traceFailed=true;},
    trace=>{trace.events[0]={event:'constructor'};},trace=>{trace.events[0].phase='Ancestor(127)';}];
  for(const change of changes)mutate(0,rows=>change(rows[0].details.fresh));
});
test('injected trace requires exact accepted prefix and matching final stage/primary',()=>{
  const good=specimen(4);good[2].details.first=injectedTrace(STAGES[2],1);parse(4,good);
  for(const change of [t=>{t.fired=false;},t=>{t.terminal.kind='OtherConstructorRefusal';},t=>{t.terminal.phase='Flush(LeafAfter)';},
    t=>{t.terminal.primary=1;},t=>{t.events.at(-1).primary=1;},t=>{t.events.splice(-2,1);},t=>{t.events.push({event:'ConstructorValidated'});},
    t=>{t.events.splice(-1,0,{event:'FlushStarted',stage:'LeafAfter'});}])mutate(4,rows=>change(rows[2].details.first));
});
test('refusals stay distinct and cross-process native sharing witness stays exact',()=>{
  mutate(1,rows=>{rows[0].details.trace.fired=true;});mutate(1,rows=>{rows[0].details.trace.traceFailed=true;});
  mutate(1,rows=>{rows[0].details.trace.terminal.kind='HarnessTraceRefusal';});mutate(1,rows=>{rows[0].details.trace.events.push({event:'ConstructorValidated'});});
  mutate(3,rows=>{rows[0].details.refusal[1].details.failure='Unsafe';});mutate(3,rows=>{rows[0].details.refusal[1].details.trace.events[0].primary=3221225539;});
  mutate(3,rows=>{rows[0].details.refusal[1].details.trace.events[0].phase='Nonce';});
});
test('nested child roles/sequence/nonce and empty details are closed',()=>{
  mutate(3,rows=>{rows[0].details.ready.nonce=nextNonce();});mutate(3,rows=>{rows[0].details.ready.seq=2;});
  mutate(3,rows=>{rows[0].details.refusal.reverse();});mutate(3,rows=>{rows[0].details.reopen[0].details.trace=normalTrace();});
  mutate(3,rows=>{rows[0].details.reopen[2].details.extra=true;});mutate(5,rows=>{rows[0].details.ready.details={partialTailFlushed:true,appendReturned:false};});
  mutate(5,rows=>{rows[0].details.ready.details.syncAcknowledged=false;});mutate(6,rows=>{rows[0].details.ready.phase='durable-append';});
});
test('artifact binding checks actual digest/length and recursive closed keys',()=>{
  mutate(5,rows=>{rows[0].details.childBinding.executableSha256='b'.repeat(64);});mutate(5,rows=>{rows[0].details.childBinding.executableBytes=artifact.bytes+1;});
  mutate(5,rows=>{rows[0].details.childBinding.executableBytes=0;});mutate(5,rows=>{rows[0].details.childBinding.executableBytes=268435457;});
  mutate(5,rows=>{rows[0].details.childBinding.executableFileId='A'.repeat(32);});mutate(5,rows=>{rows[0].details.childBinding.pid=123;});
  mutate(3,rows=>{rows[0].details.childBindings.pop();});rejected(()=>parseFixtureOutput(output(5),{testName:NATIVE_TESTS[5],artifact:{...artifact,bytes:0}}));
});
test('cleanup requires all observations but preserves actual kill/drop outcomes',()=>{
  for(const successful of [true,false]){const rows=specimen(5);rows[0].details.cleanup=cleanup(successful,successful?0:null);parse(5,rows);const dropped=specimen(8);dropped[0].details.dropCleanup=cleanup(successful,successful?0:null);parse(8,dropped);}
  for(const key of ['killError','waitError','readerError'])mutate(5,rows=>{rows[0].details.cleanup[key]=true;});
  mutate(5,rows=>{rows[0].details.cleanup.actualExitObserved=false;});mutate(5,rows=>{delete rows[0].details.cleanup.exitCode;});
  mutate(3,rows=>{rows[0].details.cleanup[0].exitSuccess=false;});mutate(8,rows=>{rows[0].details.earlyEof.exitSuccess=true;});
});
test('codec constants and absent fields cannot be widened into full qualification',()=>{
  mutate(5,rows=>{rows[0].details.byteLength=307;});mutate(5,rows=>{rows[0].details.records=3;});
  mutate(6,rows=>{rows[0].details.beforeLength=174;});mutate(6,rows=>{rows[0].details.repairedLength=159;});mutate(6,rows=>{rows[0].details.secondOpenStable=false;});
  mutate(7,rows=>{rows[0].details.byteLength=308;});mutate(7,rows=>{rows[0].details.bytesUnchanged=false;});
});
test('refused constructor flush prefix still requires successful accepted scalars and pairing',()=>{
  mutate(1,rows=>{rows[0].details.trace.events.push({event:'NativeFlushAccepted',stage:'LeafBefore',primary:0});},'REFUSAL_FLUSH_ORDER');
  mutate(1,rows=>{rows[0].details.trace.events.push({event:'FlushStarted',stage:'NonceDirectory'});},'REFUSAL_FLUSH_ORDER');
  mutate(1,rows=>{rows[0].details.trace.events.push({event:'FlushStarted',stage:'LeafBefore'},{event:'NativeFlushReturned',stage:'LeafBefore',primary:0},{event:'NativeFlushAccepted',stage:'LeafBefore',primary:1});});
});

test('cleanup rejects contradictory present codes and successful null across child outcomes',()=>{
  for(const [index,key,arrayIndex] of [[3,'cleanup',0],[5,'cleanup',null],[6,'cleanup',null],[7,'cleanup',null],[8,'earlyEof',null],[8,'dropCleanup',null]]){
    for(const [exitSuccess,exitCode] of [[true,1],[true,-1],[true,null],[false,0]]){
      const rows=specimen(index),details=rows[0].details;
      if(arrayIndex===null)details[key]=cleanup(exitSuccess,exitCode);else details[key][arrayIndex]=cleanup(exitSuccess,exitCode);
      rejected(()=>parse(index,rows));
    }
  }
});
test('valid exit results preserve zero success and nullable or nonzero failure',()=>{
  for(const [index,key] of [[5,'cleanup'],[6,'cleanup'],[7,'cleanup'],[8,'dropCleanup']])
    for(const [successful,code] of [[true,0],[false,1],[false,-2147483648],[false,2147483647],[false,null]]){
      const rows=specimen(index);rows[0].details[key]=cleanup(successful,code);parse(index,rows);
    }
  for(const code of [1,-1,null]){const rows=specimen(8);rows[0].details.earlyEof=cleanup(false,code);parse(8,rows);}
});
function flushRefusal(primary,kind,stageIndex=0) {
  return {events:[{event:'CreateReturned',phase:'Wal',primary:0},...STAGES.slice(0,stageIndex).flatMap(stage=>[
    {event:'FlushStarted',stage},{event:'NativeFlushReturned',stage,primary:0},{event:'NativeFlushAccepted',stage,primary:0}]),
    {event:'FlushStarted',stage:STAGES[stageIndex]},{event:'NativeFlushReturned',stage:STAGES[stageIndex],primary}],
    traceFailed:false,fired:false,terminal:{phase:`Flush(${STAGES[stageIndex]})`,kind,primary}};
}
test('source-known failed, pending and completed output refusal retain matching witnesses at each stage',()=>{
  for(let stage=0;stage<STAGES.length;stage++)for(const [primary,kind] of [
    [-1,'NativeFlushFailure'],[-2147483648,'NativeFlushFailure'],[259,'PendingQuarantined'],[0,'CompletedOutputRefusal'],[42,'CompletedOutputRefusal']]){
    const rows=specimen(1);rows[0].details.trace=flushRefusal(primary,kind,stage);parse(1,rows);
  }
});
test('flush refusal rejects terminal without a matching unaccepted returned stage',()=>{
  for(const [primary,kind] of [[-1,'NativeFlushFailure'],[259,'PendingQuarantined'],[0,'CompletedOutputRefusal']])
    for(const change of [
      trace=>{trace.events=[];},trace=>{trace.events.pop();},trace=>{trace.events.push({event:'NativeFlushAccepted',stage:'LeafBefore',primary:0});},
      trace=>{trace.terminal.phase='Flush(LeafAfter)';},trace=>{trace.terminal.primary=null;},trace=>{trace.terminal.primary=primary+1;}]){
      const rows=specimen(1);rows[0].details.trace=flushRefusal(primary,kind);change(rows[0].details.trace);rejected(()=>parse(1,rows));
    }
});
test('negative or pending native return cannot be relabelled as another first terminal',()=>{
  for(const primary of [-1,259])for(const kind of ['OtherConstructorRefusal','ProtocolRefusal','CompletedOutputRefusal',primary===259?'NativeFlushFailure':'PendingQuarantined']){
    const rows=specimen(1);rows[0].details.trace=flushRefusal(primary,kind);rejected(()=>parse(1,rows));
  }
  for(const [primary,kind] of [[0,'NativeFlushFailure'],[42,'PendingQuarantined'],[-1,'CompletedOutputRefusal'],[259,'CompletedOutputRefusal']]){
    const rows=specimen(1);rows[0].details.trace=flushRefusal(primary,kind);rejected(()=>parse(1,rows));
  }
});
test('generic constructor refusals preserve legitimate incomplete or wholly accepted flush prefixes',()=>{
  for(const state of ['start','returned','accepted']){
    const rows=specimen(1),trace=flushRefusal(0,'OtherConstructorRefusal');
    trace.terminal.primary=null;
    if(state==='start')trace.events=trace.events.slice(0,1);
    if(state==='returned')trace.events.pop();
    if(state==='accepted')trace.events.push({event:'NativeFlushAccepted',stage:'LeafBefore',primary:0});
    rows[0].details.trace=trace;
    const parsed=parse(1,rows);
    assert.deepEqual(parsed.rows[0].details.trace.events.map(event=>event.event),trace.events.map(event=>event.event));
  }
});

test('create failures preserve first exact phase/status before submission and after accepted constructor flushes',()=>{
  for(const phase of ['Nonce','FinalCustodyValidation'])for(const primary of [-1,259]){
    const rows=specimen(1),trace={events:[],traceFailed:false,fired:false,
      terminal:{phase,kind:primary===259?'PendingQuarantined':'OtherConstructorRefusal',primary}};
    if(phase==='FinalCustodyValidation')trace.events=normalTrace().events.filter(event=>event.event!=='ConstructorValidated');
    trace.events.push({event:'CreateReturned',phase,primary});rows[0].details.trace=trace;parse(1,rows);
    for(const change of [t=>{t.terminal.primary=null;},t=>{t.terminal.primary=primary+1;},t=>{t.terminal.phase='KnownFolder';},
      t=>{t.terminal.kind=primary===259?'OtherConstructorRefusal':'PendingQuarantined';},
      t=>{t.events.push({event:'CreateReturned',phase:'Wal',primary:0});}]){
      const changed=structuredClone(rows);change(changed[0].details.trace);rejected(()=>parse(1,changed));
    }
  }
});
test('protocol refusal with a returned scalar preserves its successful unaccepted flush witness',()=>{
  for(const primary of [0,42]){
    const rows=specimen(1);rows[0].details.trace=flushRefusal(primary,'ProtocolRefusal');parse(1,rows);
    const absent=structuredClone(rows);absent[0].details.trace.events.pop();rejected(()=>parse(1,absent));
  }
});
test('completed selected libtest ok is mandatory, unique and ordered before summary',()=>{
  const valid=output(0).toString();
  for(const replacement of ['', 'FAILED\n', 'ignored\n', 'ok\nok\n']){
    const changed=valid.replace(/^ok\n/m,replacement);assert.notEqual(changed,valid);
    rejected(()=>parseFixtureOutput(bytes(changed),{testName:NATIVE_TESTS[0],artifact}),'EXECUTION_FRAMING');
  }
  const moved=valid.replace(/^ok\n/m,'')+'ok\n';rejected(()=>parseFixtureOutput(bytes(moved),{testName:NATIVE_TESTS[0],artifact}));
  const early='ok\n'+valid;rejected(()=>parseFixtureOutput(bytes(early),{testName:NATIVE_TESTS[0],artifact}));
});

test('every known unaccepted flush terminal forbids any later create witness',()=>{
  for(const [primary,kind] of [[-1,'NativeFlushFailure'],[259,'PendingQuarantined'],[0,'CompletedOutputRefusal'],[42,'ProtocolRefusal']])
    for(const phase of ['Wal','FinalCustodyValidation']){
      const rows=specimen(1);rows[0].details.trace=flushRefusal(primary,kind);
      rows[0].details.trace.events.push({event:'CreateReturned',phase,primary:0});
      rejected(()=>parse(1,rows),'REFUSAL_FLUSH_CONTINUATION');
    }
});
test('successful unaccepted flush return cannot be retained as generic null refusal',()=>{
  const rows=specimen(1);rows[0].details.trace=flushRefusal(0,'OtherConstructorRefusal');rows[0].details.trace.terminal.primary=null;
  rejected(()=>parse(1,rows),'REFUSAL_FLUSH_CLASSIFICATION');
});
test('started-only generic refusal retains exact last started phase and no later event',()=>{
  const rows=specimen(1);rows[0].details.trace=flushRefusal(0,'OtherConstructorRefusal');rows[0].details.trace.events.pop();rows[0].details.trace.terminal.primary=null;
  const parsed=parse(1,rows);assert.equal(parsed.rows[0].details.trace.events.at(-1).event,'FlushStarted');
  const moved=structuredClone(rows);moved[0].details.trace.terminal.phase='Flush(LeafAfter)';rejected(()=>parse(1,moved));
  const later=structuredClone(rows);later[0].details.trace.events.push({event:'CreateReturned',phase:'Wal',primary:0});rejected(()=>parse(1,later));
});

test('all successful unaccepted stage returns require a non-null causal output or protocol terminal',()=>{
  for(let stage=0;stage<STAGES.length;stage++)for(const primary of [0,42])
    for(const kind of ['OtherConstructorRefusal','ProtocolRefusal']){
      const rows=specimen(1);rows[0].details.trace=flushRefusal(primary,kind,stage);rows[0].details.trace.terminal.primary=null;
      rejected(()=>parse(1,rows),'REFUSAL_FLUSH_CLASSIFICATION');
    }
});
