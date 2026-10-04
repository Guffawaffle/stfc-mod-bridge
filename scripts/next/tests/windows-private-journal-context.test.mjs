import test from 'node:test';
import assert from 'node:assert/strict';
import { FixtureEvidenceError, NATIVE_TESTS } from '../windows-private-journal-evidence.mjs';
import { CONTEXT_MARKER, parseJournalFixtureOutput, parseJournalFixtureSuite } from '../windows-private-journal-context.mjs';
import { artifact, output } from './windows-private-journal-specimens.mjs';

const context = index => ({schemaVersion:'bridge-windows-journal-context/v1',testName:NATIVE_TESTS[index],pid:4000+index,
  creationFiletime:(0xffffffffffffffffn-BigInt(index)).toString(),tokenType:1,elevated:false,elevationType:3,
  integrityRid:8192,integrityAttributes:96,threadTokenAbsent:true,processMachine:0,nativeMachine:0x8664,beforeJournalEffects:true});
const stream = (index, value = context(index)) => Buffer.from(output(index).toString('utf8').replace(`test ${NATIVE_TESTS[index]} ... `,
  `test ${NATIVE_TESTS[index]} ... ${CONTEXT_MARKER}${JSON.stringify(value)}\n`));
const parse = (index, bytes = stream(index)) => parseJournalFixtureOutput(bytes,{testName:NATIVE_TESTS[index],artifact,spawnedPid:4000+index});
const reject = callback => assert.throws(callback, error => error instanceof FixtureEvidenceError);

for (let index=0; index<9; index++) test(`synthetic direct-process context plus closed fixture case ${index}`,()=>{
  const parsed=parse(index);assert.equal(parsed.context.creationFiletime,context(index).creationFiletime);
  assert.equal(typeof parsed.context.creationFiletime,'string');assert.ok(Object.isFrozen(parsed.context));
  assert.equal(parsed.rows.length,index===1?13:index===4?7:1);
});
test('synthetic nine-process suite retains context separately from27markers/25nonces',()=>{
  const suite=parseJournalFixtureSuite(NATIVE_TESTS.map((testName,index)=>({testName,stdout:stream(index),spawnedPid:4000+index})),artifact);
  assert.equal(suite.contexts.length,9);assert.equal(suite.results.reduce((sum,row)=>sum+row.rows.length,0),27);
  assert.equal(suite.reportedUniqueNonces.length,25);assert.equal(suite.callerTokenDirectlyObserved,false);
});
for (const [field,value] of [['schemaVersion','wrong'],['testName',NATIVE_TESTS[1]],['pid',4001],['pid',0],['pid',1.5],
  ['tokenType',2],['elevated',true],['elevated',0],['elevationType',2],['integrityRid',4096],['integrityRid',12288],
  ['integrityAttributes',0],['threadTokenAbsent',false],['processMachine',0x14c],['nativeMachine',0xaa64],['beforeJournalEffects',false],
  ['creationFiletime','0'],['creationFiletime','01'],['creationFiletime','18446744073709551616'],['creationFiletime',9007199254740992]])
  test(`context refuses ${field}=${value}`,()=>reject(()=>parse(0,stream(0,{...context(0),[field]:value}))));
test('missing/unknown/duplicate/escaped context members refuse',()=>{
  const value=context(0);delete value.pid;reject(()=>parse(0,stream(0,value)));
  reject(()=>parse(0,stream(0,{...context(0),extra:true})));
  const raw=stream(0).toString('utf8');reject(()=>parse(0,Buffer.from(raw.replace('"pid":4000','"pid":4000,"pid":4000'))));
  reject(()=>parse(0,Buffer.from(raw.replace('"creationFiletime":"1844','"creationFiletime":"\\u0031844'))));
});
test('context is mandatory, unique, exactly on selectedprefix and beforefixturemarkers',()=>{
  reject(()=>parse(0,output(0)));
  const raw=stream(0).toString('utf8'), row=raw.split('\n')[1];
  for(const changed of [raw.replace(row,row+'\n'+row),raw.replace(row,row.slice(`test ${NATIVE_TESTS[0]} ... `.length)),
    raw.replace(row,'test '+NATIVE_TESTS[1]+' ... '+row.split(' ... ')[1]),raw.replace(row,'unexpected\n'+row),
    raw.replace(row,'ok\n'+row),raw.replace(row,row.slice(0,-1)),raw.replace(row,row+'\rjunk')]) reject(()=>parse(0,Buffer.from(changed)));
});
test('CRLFcontext preserves originalrawstream for existinglibtest parsing',()=>{
  assert.equal(parse(0,Buffer.from(stream(0).toString('utf8').replaceAll('\n','\r\n'))).context.pid,4000);
});
test('context cannot rehabilitate missingcompletion, failedsummary or changedartifactbinding',()=>{
  reject(()=>parse(0,Buffer.from(stream(0).toString('utf8').replace('\nok\n','\n'))));
  reject(()=>parse(0,Buffer.from(stream(0).toString('utf8').replace('1 passed','0 passed'))));
  const child=stream(3);reject(()=>parseJournalFixtureOutput(child,{testName:NATIVE_TESTS[3],artifact:{...artifact,sha256:'c'.repeat(64)},spawnedPid:4003}));
});
test('malformedUTF8/sharedrawbytes and unsafe/reusedprocess context refuse',()=>{
  reject(()=>parse(0,Buffer.concat([stream(0),Buffer.from([0xff])])));
  reject(()=>parse(0,new Uint8Array(new SharedArrayBuffer(10))));
  reject(()=>parseJournalFixtureOutput(stream(0),{testName:NATIVE_TESTS[0],artifact,spawnedPid:NaN}));
  const outputs=NATIVE_TESTS.map((testName,index)=>({testName,stdout:stream(index),spawnedPid:4000+index}));
  outputs[1].stdout=stream(1,{...context(1),pid:4000,creationFiletime:context(0).creationFiletime});outputs[1].spawnedPid=4000;
  reject(()=>parseJournalFixtureSuite(outputs,artifact));
});
