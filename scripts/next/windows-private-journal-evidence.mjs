// Pure bounded fixture parsing. Command/artifact/native provenance belongs to the driver.
export class FixtureEvidenceError extends Error {
  constructor(code) { super(code); this.name = 'FixtureEvidenceError'; this.code = code; }
}
const refuse = code => { throw new FixtureEvidenceError(code); };
export const LIMITS = Object.freeze({ outputBytes: 8 * 1024 * 1024, parentJsonBytes: 16384,
  parentLineBytes: 16416, childLineBytes: 16384, childPipeBytes: 65536,
  depth: 32, nodes: 8192, executableBytes: 256 * 1024 * 1024 });
export const PARENT_MARKER = 'BRIDGE_PRIVATE_JOURNAL_FIXTURE ';
export const CHILD_MARKER = 'BRIDGE_PRIVATE_JOURNAL_CHILD ';
export const STAGES = Object.freeze(['LeafBefore','VersionDirectory','NonceDirectory','FixturesDirectory','KnownFolderDirectory','LeafAfter']);
export const OBJECTS = Object.freeze(['Nonce','Version','Wal']);
export const VARIANTS = Object.freeze(['Unprotected','WorldRead','Reduced','WrongInheritance']);
const PREFIX = 'private_journal::native_fixtures::';
export const NATIVE_TESTS = Object.freeze([
  'native_private_journal_fresh_reopen_owner_acl_volume_and_namespace',
  'native_private_journal_dacl_variants_and_live_drift_refuse_without_repair',
  'native_private_journal_leaf_and_private_directory_sharing_are_retained',
  'native_private_journal_cross_process_owner_refusal_and_release',
  'native_private_journal_every_completed_constructor_failure_repeats_full_flush',
  'native_private_journal_codec_durable_append_survives_owned_child_kill',
  'native_private_journal_codec_torn_append_repairs_after_owned_child_kill',
  'native_private_journal_codec_complete_corruption_refuses_without_repair',
  'native_private_journal_child_custody_failure_cleanup_is_observed'
].map(name => PREFIX + name));
const U64_MAX = '18446744073709551615';
const exactBrand = new WeakSet();
class ExactU64 {
  constructor(decimal) { this.decimal = decimal; exactBrand.add(this); Object.freeze(this); }
  toJSON() { return { type: 'u64', decimal: this.decimal }; }
}
export function isExactU64(value) { return typeof value === 'object' && value !== null && exactBrand.has(value); }
export function sameU64(left, right) {
  if (!isExactU64(left) || !isExactU64(right)) refuse('EXACT_U64_REQUIRED');
  return left.decimal === right.decimal;
}
function identityPath(parts) {
  return parts[0] === 'details' && ['creationFiletime','executableVolume'].includes(parts.at(-1))
    && ((parts.length === 3 && parts[1] === 'childBinding')
      || (parts.length === 4 && parts[1] === 'childBindings' && Number.isInteger(parts[2]) && parts[2] >= 0 && parts[2] <= 2));
}
function boundedBytes(input, cap) {
  if (!(input instanceof Uint8Array) || input.buffer instanceof SharedArrayBuffer) refuse('RAW_BYTES_REQUIRED');
  if (input.byteLength > cap) refuse('BYTE_LIMIT');
  return input;
}
function decode(input, cap) {
  boundedBytes(input, cap);
  try { return new TextDecoder('utf-8', { fatal: true, ignoreBOM: true }).decode(input); }
  catch { refuse('INVALID_UTF8'); }
}
export function collectOutput(chunks) {
  if (!Array.isArray(chunks) || chunks.length > 8192) refuse('CHUNK_LIMIT');
  let length = 0;
  for (const chunk of chunks) { boundedBytes(chunk, LIMITS.outputBytes); length += chunk.byteLength; if (length > LIMITS.outputBytes) refuse('BYTE_LIMIT'); }
  const result = new Uint8Array(length); let offset = 0;
  for (const chunk of chunks) { result.set(chunk, offset); offset += chunk.byteLength; }
  return result;
}
// A finite JSON grammar. JSON.parse is used only for one already bounded lexical
// string token, never for objects/numbers or last-member-wins interpretation.
export function parseFixtureJson(input) {
  const text = decode(input, LIMITS.parentJsonBytes); let offset = 0, nodes = 0;
  const whitespace = () => { while (offset < text.length && /[\x20\t\r\n]/.test(text[offset])) offset++; };
  const string = () => {
    if (text[offset] !== '"') refuse('JSON_STRING');
    const start = offset++;
    while (offset < text.length) {
      const char = text[offset++];
      if (char === '"') {
        let value; try { value = JSON.parse(text.slice(start, offset)); } catch { refuse('JSON_STRING'); }
        for (let i = 0; i < value.length; i++) {
          const code = value.charCodeAt(i);
          if (code >= 0xd800 && code <= 0xdbff) { const next = value.charCodeAt(++i); if (!(next >= 0xdc00 && next <= 0xdfff)) refuse('UNPAIRED_SURROGATE'); }
          else if (code >= 0xdc00 && code <= 0xdfff) refuse('UNPAIRED_SURROGATE');
        }
        return value;
      }
      if (char.charCodeAt(0) < 0x20) refuse('JSON_STRING');
      if (char === '\\') {
        const escaped = text[offset++];
        if (escaped === 'u') { if (!/^[0-9a-fA-F]{4}$/.test(text.slice(offset, offset + 4))) refuse('JSON_ESCAPE'); offset += 4; }
        else if (!escaped || !'"\\/bfnrt'.includes(escaped)) refuse('JSON_ESCAPE');
      }
    }
    refuse('JSON_TRUNCATED');
  };
  const number = parts => {
    const start = offset;
    while (offset < text.length && /[0-9eE+\-.]/.test(text[offset])) offset++;
    const token = text.slice(start, offset);
    if (identityPath(parts)) {
      if (!/^(?:0|[1-9][0-9]{0,19})$/.test(token) || token.length > U64_MAX.length
        || (token.length === U64_MAX.length && token > U64_MAX)) refuse('U64_TOKEN');
      return new ExactU64(token);
    }
    if (!/^(?:0|-?[1-9][0-9]*)$/.test(token)) refuse('INTEGER_TOKEN');
    const value = Number(token); if (!Number.isSafeInteger(value)) refuse('UNSAFE_INTEGER');
    return value;
  };
  const value = (parts, depth) => {
    if (depth > LIMITS.depth || ++nodes > LIMITS.nodes) refuse('JSON_STRUCTURE_LIMIT');
    whitespace(); const char = text[offset];
    if (identityPath(parts) && char !== '-' && !(char >= '0' && char <= '9')) refuse('U64_TOKEN');
    if (char === '"') return string();
    if (char === '{') {
      offset++; whitespace(); const object = Object.create(null), keys = new Set();
      if (text[offset] === '}') { offset++; return object; }
      while (true) {
        whitespace(); const key = string(); if (keys.has(key)) refuse('DUPLICATE_MEMBER'); keys.add(key);
        if (keys.size > 128) refuse('JSON_STRUCTURE_LIMIT'); whitespace(); if (text[offset++] !== ':') refuse('JSON_COLON');
        object[key] = value([...parts, key], depth + 1); whitespace();
        const separator = text[offset++]; if (separator === '}') return object; if (separator !== ',') refuse('JSON_OBJECT');
      }
    }
    if (char === '[') {
      offset++; whitespace(); const array = []; if (text[offset] === ']') { offset++; return array; }
      while (true) { array.push(value([...parts, array.length], depth + 1)); whitespace(); const separator = text[offset++]; if (separator === ']') return array; if (separator !== ',') refuse('JSON_ARRAY'); }
    }
    if (char === '-' || (char >= '0' && char <= '9')) return number(parts);
    for (const [spelling, result] of [['true',true],['false',false],['null',null]]) {
      if (text.startsWith(spelling, offset)) { offset += spelling.length; return result; }
    }
    refuse('JSON_VALUE');
  };
  const result = value([], 0); whitespace(); if (offset !== text.length) refuse('JSON_TRAILING'); return result;
}
const own = (value, key) => Object.hasOwn(value, key);
function object(value, keys) {
  if (value === null || typeof value !== 'object' || Array.isArray(value) || isExactU64(value)) refuse('CLOSED_OBJECT');
  const actual = Object.keys(value); if (actual.length !== keys.length || !keys.every(key => own(value,key))) refuse('CLOSED_OBJECT');
}
const equal = (actual, expected) => { if (actual !== expected) refuse('CONSTANT'); };
const integer = (value, min, max) => { if (!Number.isSafeInteger(value) || value < min || value > max) refuse('INTEGER_RANGE'); };
const enumeration = (value, allowed) => { if (!allowed.includes(value)) refuse('ENUM'); };
const nonce = value => { if (typeof value !== 'string' || !/^[0-9a-f]{8}-[0-9a-f]{4}-4[0-9a-f]{3}-[89ab][0-9a-f]{3}-[0-9a-f]{12}$/.test(value)) refuse('NONCE'); };
const digest = value => { if (typeof value !== 'string' || !/^[0-9a-f]{64}$/.test(value)) refuse('DIGEST'); };
function array(value, count) { if (!Array.isArray(value) || value.length !== count) refuse('ARRAY_COUNT'); }
const success = value => { integer(value,-2147483648,2147483647); if (value < 0 || value === 259) refuse('NATIVE_SUCCESS'); };
function phase(value) {
  if (['Preflight','KnownFolder','FixturesRoot','Nonce','Version','Wal','FinalCustodyValidation',...STAGES.map(stage => `Flush(${stage})`)].includes(value)) return;
  if (typeof value === 'string' && /^Ancestor\((?:0|[1-9][0-9]{0,2})\)$/.test(value) && Number(value.slice(9,-1)) <= 126) return;
  refuse('PHASE');
}
function frozen(value) { if (value !== null && typeof value === 'object' && !isExactU64(value)) { for (const item of Object.values(value)) frozen(item); Object.freeze(value); } return value; }
const EVENT_KEYS = Object.freeze({ CreateReturned:['event','phase','primary'], FlushStarted:['event','stage'],
  NativeFlushReturned:['event','stage','primary'],NativeFlushAccepted:['event','stage','primary'],
  FaultInjected:['event','stage','primary'],ConstructorValidated:['event'] });
const TERMINALS = ['NativeFlushFailure','PendingQuarantined','CompletedOutputRefusal','InjectedAfterNativeSuccess','HarnessTraceRefusal','ProtocolRefusal','OtherConstructorRefusal'];
function trace(value, mode, selectedStage) {
  object(value,['events','traceFailed','fired','terminal']); equal(value.traceFailed,false);
  if (!Array.isArray(value.events) || value.events.length > 4096) refuse('TRACE_EVENTS');
  for (const event of value.events) {
    if (!own(EVENT_KEYS,event?.event)) refuse('TRACE_EVENT'); object(event,EVENT_KEYS[event.event]);
    if (own(event,'primary')) integer(event.primary,-2147483648,2147483647);
    if (['NativeFlushAccepted','FaultInjected'].includes(event.event)) success(event.primary);
    if (own(event,'phase')) phase(event.phase); if (own(event,'stage')) enumeration(event.stage,STAGES);
  }
  const validated = value.events.filter(event => event.event === 'ConstructorValidated');
  if (value.terminal !== null) {
    object(value.terminal,['phase','kind','primary']); phase(value.terminal.phase); enumeration(value.terminal.kind,TERMINALS);
    if (value.terminal.primary !== null) integer(value.terminal.primary,-2147483648,2147483647);
  }
  if (mode === 'refusal') {
    equal(value.fired,false); if (value.terminal === null || validated.length || value.events.some(event => event.event === 'FaultInjected')) refuse('REFUSAL_TRACE');
    if (['HarnessTraceRefusal','InjectedAfterNativeSuccess'].includes(value.terminal.kind)) refuse('REFUSAL_TRACE');
    const pending = value.events.filter(event => ['FlushStarted','NativeFlushReturned','NativeFlushAccepted'].includes(event.event));
    let ordinal=0, state='start', returned;
    for (const event of pending) {
      const stage=STAGES[ordinal]; if (!stage || event.stage!==stage) refuse('REFUSAL_FLUSH_ORDER');
      if (state==='start' && event.event==='FlushStarted') state='returned';
      else if (state==='returned' && event.event==='NativeFlushReturned') { returned=event.primary; state='accepted'; }
      else if (state==='accepted' && event.event==='NativeFlushAccepted') { success(returned); equal(event.primary,returned); ordinal++; state='start'; }
      else refuse('REFUSAL_FLUSH_ORDER');
    }
    // These terminal kinds are emitted only by the last unaccepted native
    // flush. A started-only prefix can still have a generic constructor refusal.
    const terminal=value.terminal;
    const failedCreate=value.events.find(event=>event.event==='CreateReturned' && (event.primary<0 || event.primary===259));
    if (failedCreate) {
      if (state!=='start' || value.events.at(-1)!==failedCreate) refuse('REFUSAL_CREATE_WITNESS');
      equal(terminal.kind,failedCreate.primary===259?'PendingQuarantined':'OtherConstructorRefusal');
      equal(terminal.phase,failedCreate.phase); equal(terminal.primary,failedCreate.primary);
    }
    const flushTerminal=['NativeFlushFailure','CompletedOutputRefusal'].includes(terminal.kind)
      || (terminal.kind==='PendingQuarantined' && !failedCreate)
      || (terminal.kind==='ProtocolRefusal' && terminal.primary!==null);
    if (flushTerminal) {
      if (state!=='accepted') refuse('REFUSAL_FLUSH_WITNESS');
      if (value.events.at(-1)?.event!=='NativeFlushReturned') refuse('REFUSAL_FLUSH_CONTINUATION');
      equal(terminal.phase,`Flush(${STAGES[ordinal]})`); equal(terminal.primary,returned);
      if (terminal.kind==='NativeFlushFailure' && returned>=0) refuse('REFUSAL_FLUSH_CLASSIFICATION');
      if (terminal.kind==='PendingQuarantined' && returned!==259) refuse('REFUSAL_FLUSH_CLASSIFICATION');
      if (terminal.kind==='CompletedOutputRefusal') success(returned);
      if (terminal.kind==='ProtocolRefusal') success(returned);
    }
    // A completed negative/pending return latches before its event is emitted;
    // the later generic constructor refusal cannot replace that first failure.
    if (state==='accepted' && (returned<0 || returned===259)) {
      equal(terminal.kind,returned===259?'PendingQuarantined':'NativeFlushFailure');
      equal(terminal.phase,`Flush(${STAGES[ordinal]})`); equal(terminal.primary,returned);
    }
    if (state==='accepted' && !flushTerminal) refuse('REFUSAL_FLUSH_CLASSIFICATION');
    if (state==='returned') {
      equal(terminal.kind,'OtherConstructorRefusal'); equal(terminal.primary,null);
      equal(terminal.phase,`Flush(${STAGES[ordinal]})`);
      if (value.events.at(-1)?.event!=='FlushStarted') refuse('REFUSAL_FLUSH_CONTINUATION');
    }
    if (terminal.kind==='OtherConstructorRefusal' && terminal.primary!==null && !failedCreate) refuse('REFUSAL_CREATE_WITNESS');
    // No universal ACL status or complete CreateReturned schedule is inferred.
    return;
  }
  const selectedIndex = selectedStage === undefined ? STAGES.length - 1 : STAGES.indexOf(selectedStage);
  if (selectedIndex < 0) refuse('STAGE');
  const expected = STAGES.slice(0, selectedIndex + 1);
  const flushes = value.events.filter(event => ['FlushStarted','NativeFlushReturned','NativeFlushAccepted'].includes(event.event));
  array(flushes, expected.length * 3);
  for (const [index, stage] of expected.entries()) {
    const [started,returned,accepted] = flushes.slice(index*3,index*3+3);
    equal(started.event,'FlushStarted'); equal(returned.event,'NativeFlushReturned'); equal(accepted.event,'NativeFlushAccepted');
    for (const event of [started,returned,accepted]) equal(event.stage,stage);
    success(returned.primary); success(accepted.primary); equal(returned.primary,accepted.primary);
  }
  for (const event of value.events.filter(event => event.event === 'CreateReturned')) success(event.primary);
  if (mode === 'normal') {
    equal(value.fired,false); equal(value.terminal,null); array(validated,1);
    equal(value.events.at(-1)?.event,'ConstructorValidated');
    if (value.events.some(event => event.event === 'FaultInjected')) refuse('NORMAL_TRACE');
  } else {
    equal(value.fired,true); array(validated,0);
    if (value.terminal === null) refuse('INJECTED_TRACE');
    equal(value.terminal.kind,'InjectedAfterNativeSuccess'); equal(value.terminal.phase,`Flush(${selectedStage})`);
    success(value.terminal.primary); equal(value.terminal.primary,flushes.at(-1).primary);
    const faults = value.events.filter(event => event.event === 'FaultInjected'); array(faults,1);
    equal(value.events.at(-1),faults[0]); equal(faults[0].stage,selectedStage); equal(faults[0].primary,value.terminal.primary);
  }
}
function binding(value, artifact) {
  object(value,['creationFiletime','executableVolume','executableFileId','executableSha256','executableBytes']);
  if (!isExactU64(value.creationFiletime) || !isExactU64(value.executableVolume)) refuse('EXACT_U64_REQUIRED');
  if (typeof value.executableFileId !== 'string' || !/^[0-9a-f]{32}$/.test(value.executableFileId)) refuse('FILE_ID');
  digest(value.executableSha256); integer(value.executableBytes,1,LIMITS.executableBytes);
  equal(value.executableSha256,artifact.sha256); equal(value.executableBytes,artifact.bytes);
}
function cleanup(value, expectedSuccess) {
  object(value,['state','killError','waitError','readerError','exitSuccess','exitCode','actualExitObserved']);
  equal(value.state,'ReapedAndReadersJoined'); for (const key of ['killError','waitError','readerError']) equal(value[key],false);
  equal(value.actualExitObserved,true); if (typeof value.exitSuccess !== 'boolean') refuse('EXIT_SUCCESS');
  if (expectedSuccess !== undefined) equal(value.exitSuccess,expectedSuccess);
  if (value.exitCode !== null) {
    integer(value.exitCode,-2147483648,2147483647);
    equal(value.exitSuccess,value.exitCode===0);
  } else if (value.exitSuccess) refuse('EXIT_SUCCESS_CODE');
}
function child(value, parentNonce, expectedPhase, expectedEvent, expectedSequence) {
  object(value,['schemaVersion','nonce','phase','seq','event','details']); equal(value.schemaVersion,1); nonce(value.nonce); equal(value.nonce,parentNonce);
  equal(value.phase,expectedPhase); equal(value.event,expectedEvent); integer(value.seq,1,128); equal(value.seq,expectedSequence);
  if (['Started','Released'].includes(expectedEvent)) object(value.details,[]);
  else if (expectedEvent === 'Opened') { object(value.details,['trace']); trace(value.details.trace,'normal'); }
  else if (expectedEvent === 'OpenRefused') { object(value.details,['failure','trace']); enumeration(value.details.failure,['Busy','Unsafe','Unavailable']); trace(value.details.trace,'refusal'); }
  else if (expectedEvent === 'Ready') {
    const variants = {'hold-owner':{heldOwner:true},'durable-append':{appendReturned:true,syncAcknowledged:true},'torn-append':{partialTailFlushed:true,appendReturned:false},'corrupt-complete':{completeCorruptionFlushed:true}};
    const required = variants[expectedPhase]; if (!required) refuse('READY_PHASE'); object(value.details,Object.keys(required));
    for (const [key,expected] of Object.entries(required)) equal(value.details[key],expected);
  } else refuse('CHILD_EVENT');
}
const DEFERRED = ['reparse_hard_link','writable_mapping','broader_bounds_custody_loss','known_folder_configuration_redirection'];
const MARKERS = Object.freeze([['fresh_reopen'],[...Array(12).fill('dacl_variant'),'dacl_refusal'],['sharing'],['cross_process'],[...Array(6).fill('constructor_failure_stage'),'completed_constructor_failures'],['codec_durable'],['codec_torn'],['codec_corrupt'],['child_failure_cleanup']]);
function envelope(value, caseName) {
  object(value,['schemaVersion','case','details','foreignOwner','deferred','privateJournalOwnerQualified','installedGameQualified','releaseQualified']);
  equal(value.schemaVersion,1); equal(value.case,caseName); object(value.foreignOwner,['status','reason']);
  equal(value.foreignOwner.status,'not_observed'); equal(value.foreignOwner.reason,'foreign_owner_seed_not_declared');
  array(value.deferred,4); value.deferred.forEach((item,index)=>equal(item,DEFERRED[index]));
  for (const key of ['privateJournalOwnerQualified','installedGameQualified','releaseQualified']) equal(value[key],false);
}
function validateRows(rows, index, artifact) {
  array(rows,MARKERS[index].length); rows.forEach((row,i)=>envelope(row,MARKERS[index][i])); const details=rows[0].details, fresh=[];
  const freshNonce = value => { nonce(value); if (fresh.includes(value)) refuse('DUPLICATE_FRESH_NONCE'); fresh.push(value); };
  if (index === 0) {
    object(details,['nonce','sameIdentity','directoryCount','currentUserOwner','exactPrivateDacl','localWritableAclNtfs','leafLinks','fresh','reopen']); freshNonce(details.nonce);
    for (const key of ['sameIdentity','currentUserOwner','exactPrivateDacl','localWritableAclNtfs']) equal(details[key],true);
    integer(details.directoryCount,5,132); equal(details.leafLinks,1); trace(details.fresh,'normal'); trace(details.reopen,'normal');
  } else if (index === 1) {
    for (let i=0;i<12;i++) {
      const d=rows[i].details; object(d,['nonce','object','variant','descriptorUnchanged','identityLengthUnchanged','trace']); freshNonce(d.nonce);
      equal(d.object,OBJECTS[Math.floor(i/4)]); equal(d.variant,VARIANTS[i%4]); equal(d.descriptorUnchanged,true); equal(d.identityLengthUnchanged,true); trace(d.trace,'refusal');
    }
    const d=rows[12].details; object(d,['variants','liveBytesUnchanged','liveWriteRefusedBeforeLengthChange','latchedRefusal']); array(d.variants,12);
    d.variants.forEach((v,i)=>{object(v,['nonce','object','variant']); for (const key of ['nonce','object','variant']) equal(v[key],rows[i].details[key]);});
    for (const key of ['liveBytesUnchanged','liveWriteRefusedBeforeLengthChange','latchedRefusal']) equal(d[key],true);
  } else if (index === 2) {
    object(details,['nonce','independentPrivateObjects','win32SharingViolation','writerAndDeleteRefused','sameAdmissionsAfterRelease','trace']); freshNonce(details.nonce);
    equal(details.independentPrivateObjects,4); equal(details.win32SharingViolation,32); equal(details.writerAndDeleteRefused,true); equal(details.sameAdmissionsAfterRelease,true); trace(details.trace,'normal');
  } else if (index === 3) {
    object(details,['nonce','ready','refusal','reopen','childBindings','cleanup']); freshNonce(details.nonce);
    child(details.ready,details.nonce,'hold-owner','Ready',3); array(details.refusal,2); array(details.reopen,3);
    ['Started','OpenRefused'].forEach((event,i)=>child(details.refusal[i],details.nonce,'try-open',event,i+1));
    ['Started','Opened','Released'].forEach((event,i)=>child(details.reopen[i],details.nonce,'open-and-release',event,i+1));
    equal(details.refusal[1].details.failure,'Busy'); const creates=details.refusal[1].details.trace.events.filter(event=>event.event==='CreateReturned');
    if (!creates.length) refuse('SHARING_REFUSAL'); equal(creates.at(-1).phase,'FixturesRoot'); equal(creates.at(-1).primary,-1073741757);
    array(details.childBindings,3); details.childBindings.forEach(b=>binding(b,artifact)); array(details.cleanup,3); details.cleanup.forEach(c=>cleanup(c,true));
  } else if (index === 4) {
    for (let i=0;i<6;i++) { const d=rows[i].details; object(d,['nonce','stage','first','reopen']); freshNonce(d.nonce); equal(d.stage,STAGES[i]); trace(d.first,'injected',STAGES[i]); trace(d.reopen,'normal'); }
    const d=rows[6].details; object(d,['attempts']); array(d.attempts,6); d.attempts.forEach((a,i)=>{object(a,['nonce','stage']); equal(a.nonce,rows[i].details.nonce); equal(a.stage,STAGES[i]);});
  } else if ([5,6,7].includes(index)) {
    const extras=index===5?['records','byteLength']:index===6?['beforeLength','repairedLength','secondOpenStable']:['corruptJournal','bytesUnchanged'];
    object(details,['nonce','ready','childBinding','cleanup',...extras,'sha256']); freshNonce(details.nonce);
    child(details.ready,details.nonce,['durable-append','torn-append','corrupt-complete'][index-5],'Ready',3); binding(details.childBinding,artifact); cleanup(details.cleanup); digest(details.sha256);
    if (index===5) {equal(details.records,2); equal(details.byteLength,308);}
    if (index===6) {equal(details.beforeLength,173); equal(details.repairedLength,158); equal(details.secondOpenStable,true);}
    if (index===7) {equal(details.corruptJournal,true); equal(details.bytesUnchanged,true);}
  } else {
    object(details,['nonce','childBindings','earlyEof','ready','dropCleanup','reopen']); freshNonce(details.nonce);
    array(details.childBindings,2); details.childBindings.forEach(b=>binding(b,artifact)); cleanup(details.earlyEof,false);
    child(details.ready,details.nonce,'hold-owner','Ready',3); cleanup(details.dropCleanup); trace(details.reopen,'normal');
  }
  return fresh;
}
export function parseFixtureOutput(input, options) {
  object(options,['testName','artifact']); const index=NATIVE_TESTS.indexOf(options.testName); if (index<0) refuse('SELECTED_TEST');
  object(options.artifact,['sha256','bytes']); digest(options.artifact.sha256); integer(options.artifact.bytes,1,LIMITS.executableBytes);
  const text=decode(input,LIMITS.outputBytes); if (!text.endsWith('\n')) refuse('OUTPUT_TRUNCATED');
  const exactPrefix=`test ${options.testName} ... `; const rows=[]; let prefixes=0, summaries=0, completions=0;
  for (let line of text.split('\n').slice(0,-1)) {
    if (line.startsWith('test result:')) {
      const match=/^test result: ok\. ([0-9]+) passed; ([0-9]+) failed; ([0-9]+) ignored; ([0-9]+) measured; ([0-9]+) filtered out;[^\r\n]*\r?$/.exec(line);
      if (!match || ++summaries!==1 || match.slice(1).some((n,i)=>n!==['1','0','0','0','37'][i])) refuse('NATIVE_SUMMARY');
      if (completions!==1) refuse('EXECUTION_FRAMING');
      continue;
    }
    if (line.startsWith('test ')) { if (rows.length || summaries || !line.startsWith(exactPrefix) || ++prefixes!==1) refuse('TEST_PREFIX'); line=line.slice(exactPrefix.length); }
    if (line.startsWith(PARENT_MARKER)) {
      if (summaries || completions || rows.length>=MARKERS[index].length) refuse('MARKER_COUNT');
      if (Buffer.byteLength(line+'\n')>LIMITS.parentLineBytes) refuse('PARENT_LINE_LIMIT');
      rows.push(parseFixtureJson(Buffer.from(line.slice(PARENT_MARKER.length),'utf8')));
    } else if (line.includes(PARENT_MARKER) || line.includes(CHILD_MARKER)) refuse('MISPLACED_MARKER');
    else if (/^ok\r?$/.test(line)) {
      if (prefixes!==1 || rows.length!==MARKERS[index].length || summaries || ++completions!==1) refuse('EXECUTION_FRAMING');
    } else if (/^(?:FAILED|ignored)(?:\r)?$/.test(line)) refuse('EXECUTION_FRAMING');
  }
  if (prefixes!==1 || summaries!==1 || completions!==1) refuse('EXECUTION_FRAMING');
  const freshNonces=validateRows(rows,index,options.artifact);
  return frozen({testName:options.testName,rows,freshNonces,boundary:'closed marker/completion/summary parsing; caller separately retains actual command status/source/tool/binary evidence'});
}
export function parseFixtureSuite(outputs, artifact) {
  array(outputs,9); const results=[], seen=new Set();
  outputs.forEach((output,index)=>{
    object(output,['testName','stdout']); equal(output.testName,NATIVE_TESTS[index]); const result=parseFixtureOutput(output.stdout,{testName:output.testName,artifact});
    for (const n of result.freshNonces) { if (seen.has(n)) refuse('DUPLICATE_SUITE_NONCE'); seen.add(n); } results.push(result);
  });
  equal(seen.size,25); return frozen({results,reportedUniqueNonces:[...seen],unreportedLiveDriftNonce:true,actualTreeEnumeration:false,privateJournalOwnerQualified:false,releaseQualified:false});
}
