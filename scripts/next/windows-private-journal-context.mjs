import { FixtureEvidenceError, LIMITS, NATIVE_TESTS, parseFixtureJson,
  parseFixtureOutput, parseFixtureSuite } from './windows-private-journal-evidence.mjs';

export const CONTEXT_MARKER = 'BRIDGE_WINDOWS_JOURNAL_CONTEXT ';
const keys = ['schemaVersion', 'testName', 'pid', 'creationFiletime', 'tokenType',
  'elevated', 'elevationType', 'integrityRid', 'integrityAttributes', 'threadTokenAbsent',
  'processMachine', 'nativeMachine', 'beforeJournalEffects'];
const refuse = code => { throw new FixtureEvidenceError(code); };
function closed(value, required) {
  if (value === null || typeof value !== 'object' || Array.isArray(value)
    || Object.keys(value).length !== required.length || required.some(key => !Object.hasOwn(value, key))) refuse('JOURNAL_CONTEXT_SHAPE');
}
function uint(value, maximum) {
  if (!Number.isSafeInteger(value) || value < 0 || value > maximum) refuse('JOURNAL_CONTEXT_INTEGER');
}

// Pure parsing: the driver separately retains the actual spawned PID, command
// status, raw output and unchanged executable/source/tool observations.
export function parseJournalContext(input, options) {
  closed(options, ['testName', 'spawnedPid']);
  if (!NATIVE_TESTS.includes(options.testName)) refuse('SELECTED_TEST');
  uint(options.spawnedPid, 0xffffffff); if (!options.spawnedPid) refuse('JOURNAL_CONTEXT_PID');
  if (!(input instanceof Uint8Array) || input.buffer instanceof SharedArrayBuffer) refuse('RAW_BYTES_REQUIRED');
  if (input.byteLength > LIMITS.outputBytes) refuse('BYTE_LIMIT');
  let text;
  try { text = new TextDecoder('utf-8', { fatal: true, ignoreBOM: true }).decode(input); }
  catch { refuse('JOURNAL_CONTEXT_UTF8'); }
  if (!text.endsWith('\n') || text.startsWith('\uFEFF')) refuse('JOURNAL_CONTEXT_FRAMING');
  const lines = text.split('\n').slice(0, -1).map(line => line.endsWith('\r') ? line.slice(0, -1) : line);
  const marked = lines.map((line, index) => ({ line, index })).filter(({ line }) => line.includes(CONTEXT_MARKER));
  const prefix = `test ${options.testName} ... ${CONTEXT_MARKER}`;
  if (marked.length !== 1 || !marked[0].line.startsWith(prefix)) refuse('JOURNAL_CONTEXT_FRAMING');
  const { line, index } = marked[0];
  if (lines.slice(0, index).some(value => value !== '' && value !== 'running 1 test')) refuse('JOURNAL_CONTEXT_ORDER');
  const raw = line.slice(prefix.length);
  if (Buffer.byteLength(raw) > LIMITS.parentJsonBytes) refuse('PARENT_LINE_LIMIT');
  const value = parseFixtureJson(Buffer.from(raw, 'utf8'));
  closed(value, keys);
  if (raw !== JSON.stringify(value)) refuse('JOURNAL_CONTEXT_CANONICAL');
  if (value.schemaVersion !== 'bridge-windows-journal-context/v1' || value.testName !== options.testName) refuse('JOURNAL_CONTEXT_IDENTITY');
  uint(value.pid, 0xffffffff); if (!value.pid || value.pid !== options.spawnedPid) refuse('JOURNAL_CONTEXT_PID');
  if (typeof value.creationFiletime !== 'string' || !/^[1-9][0-9]{0,19}$/.test(value.creationFiletime)
    || BigInt(value.creationFiletime) > 0xffffffffffffffffn) refuse('JOURNAL_CONTEXT_FILETIME');
  uint(value.tokenType, 0xffffffff); uint(value.elevationType, 0xffffffff);
  uint(value.integrityRid, 0xffffffff); uint(value.integrityAttributes, 0xffffffff);
  uint(value.processMachine, 0xffff); uint(value.nativeMachine, 0xffff);
  if (value.tokenType !== 1 || value.elevated !== false || ![1, 3].includes(value.elevationType)
    || value.integrityRid !== 0x2000 || (value.integrityAttributes & 0x20) === 0
    || value.threadTokenAbsent !== true || value.processMachine !== 0 || value.nativeMachine !== 0x8664
    || value.beforeJournalEffects !== true) refuse('JOURNAL_CONTEXT_PRIVILEGE');
  return Object.freeze(value);
}

export function parseJournalFixtureOutput(input, options) {
  closed(options, ['testName', 'artifact', 'spawnedPid']);
  const context = parseJournalContext(input, { testName: options.testName, spawnedPid: options.spawnedPid });
  const fixture = parseFixtureOutput(input, { testName: options.testName, artifact: options.artifact });
  return Object.freeze({ ...fixture, context });
}

export function parseJournalFixtureSuite(outputs, artifact) {
  if (!Array.isArray(outputs) || outputs.length !== 9) refuse('JOURNAL_CONTEXT_SUITE');
  const contexts = outputs.map((output, index) => {
    closed(output, ['testName', 'stdout', 'spawnedPid']);
    if (output.testName !== NATIVE_TESTS[index]) refuse('SELECTED_TEST');
    return parseJournalContext(output.stdout, { testName: output.testName, spawnedPid: output.spawnedPid });
  });
  if (new Set(contexts.map(value => `${value.pid}:${value.creationFiletime}`)).size !== 9) refuse('JOURNAL_CONTEXT_PROCESS_REUSE');
  const fixture = parseFixtureSuite(outputs.map(({ testName, stdout }) => ({ testName, stdout })), artifact);
  return Object.freeze({ ...fixture, contexts: Object.freeze(contexts), callerTokenDirectlyObserved: false });
}
