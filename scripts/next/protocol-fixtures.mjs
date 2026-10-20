import assert from 'node:assert/strict';
import { spawnSync } from 'node:child_process';
import { readFileSync, realpathSync, mkdirSync, writeFileSync } from 'node:fs';
import { createHash, randomUUID } from 'node:crypto';
import path from 'node:path';
import Ajv from 'ajv';
import addFormats from 'ajv-formats';
import { decodeStrictJson, WireInputError } from '../../contracts/codec/strict-json.mjs';
import { checkTranscript, TranscriptError } from '../../contracts/codec/transcript.mjs';
import { rustContext } from './rust-context.mjs';
import { ownedProtocolPath } from './protocol-files.mjs';
import { verifyScenarioCoverage } from './scenario-coverage.mjs';

const root = path.resolve(import.meta.dirname, '../..');
const arguments_ = process.argv.slice(2);
assert.ok(arguments_.length === 1 && ['--checkpoint', '--complete'].includes(arguments_[0]), 'Choose --checkpoint or --complete');
const complete = arguments_[0] === '--complete';
const fixtureRoot = ownedProtocolPath(root, 'contracts/fixtures', 'directory');
const index = JSON.parse(readFileSync(ownedProtocolPath(root, 'contracts/fixtures/index.json'), 'utf8'));
assert.equal(index.schemaVersion, 'bridge-scenario-fixtures/v1');
assert.equal(index.proofLayer, 'wire/schema and synthetic semantic validation');
for (const flag of ['engineExecutionQualified', 'nativeRuntimeQualified', 'releaseQualified']) assert.equal(index[flag], false);
const required = Array.from({ length: 18 }, (_, i) => `SC-${String(i + 1).padStart(2, '0')}`);
assert.deepEqual(index.requiredScenarioIds, required);
assert.ok(Array.isArray(index.fixtures) && index.fixtures.length > 0 && index.fixtures.length <= 1024);
if (complete) assert.equal(index.status, 'complete', 'The full scenario catalog remains in progress');

const ajv = new Ajv({ strict: true, allErrors: false, coerceTypes: false, useDefaults: false, removeAdditional: false, ownProperties: true });
addFormats(ajv, { mode: 'full', formats: ['date-time'] });
const schemas = {};
const validators = {};
for (const kind of ['request', 'reply', 'event']) {
  const bytes = readFileSync(ownedProtocolPath(root, `contracts/generated/${kind}.schema.json`));
  schemas[kind] = createHash('sha256').update(bytes).digest('hex');
  validators[kind] = ajv.compile(JSON.parse(bytes.toString('utf8')));
}
const rust = rustContext({ root });
const codecSource = ownedProtocolPath(root, 'crates/bridge-contracts/src/bin/bridge-protocol.rs');
const build = spawnSync(process.execPath, ['scripts/next/cargo.mjs', 'build', '--locked', '-p', 'bridge-contracts', '--bin', 'bridge-protocol', '--message-format', 'json'], {
  cwd: root, encoding: 'utf8', windowsHide: true, timeout: 180000, maxBuffer: 16 * 1024 * 1024
});
assert.equal(build.status, 0, `Fixture codec build failed: ${(build.stderr || '').slice(-3000)}`);
const artifacts = build.stdout.split(/\r?\n/).filter(Boolean).map(line => JSON.parse(line)).filter(message =>
  message.reason === 'compiler-artifact' && message.target?.name === 'bridge-protocol' && message.target.kind.includes('bin') && message.executable &&
  realpathSync(message.target.src_path) === codecSource
);
assert.equal(artifacts.length, 1, 'This invocation must report exactly one current fixture codec artifact');
const binaryRelative = `target/${rust.hostTarget}/debug/${process.platform === 'win32' ? 'bridge-protocol.exe' : 'bridge-protocol'}`;
assert.equal(path.resolve(artifacts[0].executable), path.resolve(root, binaryRelative), 'Fixture codec artifact differs from the controlled native target route');
const binary = ownedProtocolPath(root, binaryRelative);
const ids = new Set();
const outcomes = [];
const messages = new Map();
const coverageMessages = [];
function readFixture(relativePath) {
  assert.match(relativePath, /^[a-z][a-z0-9-]{0,95}\.(json|bin)$/);
  const actualPath = ownedProtocolPath(root, `contracts/fixtures/${relativePath}`);
  assert.equal(path.dirname(actualPath), fixtureRoot, 'Fixture paths must stay inside the synthetic fixture directory');
  return readFileSync(actualPath);
}
for (const fixture of index.fixtures) {
  assert.match(fixture.id, /^[a-z][a-z0-9-]{0,95}$/);
  assert.ok(!ids.has(fixture.id), 'Fixture IDs must be unique'); ids.add(fixture.id);
  assert.ok(required.includes(fixture.scenario));
  assert.ok(['request', 'reply', 'event'].includes(fixture.kind));
  assert.equal(typeof fixture.expectedWire, 'boolean');
  assert.equal(typeof fixture.expectedSemantic, 'boolean');
  assert.ok(!fixture.expectedSemantic || fixture.expectedWire);
  const bytes = readFixture(fixture.path);
  let parsed;
  let wireAccepted = false;
  try { parsed = decodeStrictJson(bytes); wireAccepted = validators[fixture.kind](parsed); }
  catch (error) { if (!(error instanceof WireInputError)) throw error; }
  assert.equal(wireAccepted, fixture.expectedWire, `Browser schema outcome differs for ${fixture.id}`);
  const result = spawnSync(binary, ['decode', fixture.kind], { input: bytes, env: rust.env, cwd: root, windowsHide: true, timeout: 10000, maxBuffer: 1024 * 1024 });
  assert.equal(result.status, 0, `Fixture utility failed for ${fixture.id}`);
  assert.ok(!result.error, 'Fixture utility did not complete');
  const native = JSON.parse(result.stdout.toString('utf8'));
  assert.equal(native.accepted, fixture.expectedSemantic, `Rust semantic outcome differs for ${fixture.id}`);
  if (native.accepted) {
    const normalizedBytes = fixture.normalizedPath ? readFixture(fixture.normalizedPath) : undefined;
    const expected = normalizedBytes ? JSON.parse(normalizedBytes.toString('utf8')) : JSON.parse(JSON.stringify(parsed));
    assert.deepEqual(native.normalized, expected, `Normalized Rust/browser contract differs for ${fixture.id}`);
    assert.equal(validators[fixture.kind](native.normalized), true, 'Encoded Rust output must validate against the same browser schema');
    messages.set(fixture.id, { kind: fixture.kind, message: native.normalized });
  }
  coverageMessages.push({ ...fixture, payload: native.accepted ? native.normalized : parsed });
  outcomes.push({ id: fixture.id, scenario: fixture.scenario, kind: fixture.kind, sha256: createHash('sha256').update(bytes).digest('hex'), normalizedSha256: fixture.normalizedPath ? createHash('sha256').update(readFixture(fixture.normalizedPath)).digest('hex') : null, wireAccepted, semanticAccepted: native.accepted });
}
const transcriptOutcomes = [];
const coverageTranscripts = [];
if (complete) {
  assert.ok(Array.isArray(index.transcripts) && index.transcripts.length > 0 && index.transcripts.length <= 256);
  const transcriptIds = new Set();
  for (const descriptor of index.transcripts) {
    assert.match(descriptor.id, /^[a-z][a-z0-9-]{0,95}$/);
    assert.ok(!transcriptIds.has(descriptor.id)); transcriptIds.add(descriptor.id);
    assert.match(descriptor.path, /^[a-z][a-z0-9-]{0,95}\.transcript\.json$/);
    const bytes = readFileSync(ownedProtocolPath(root, `contracts/fixtures/${descriptor.path}`));
    const transcript = JSON.parse(JSON.stringify(decodeStrictJson(bytes)));
    assert.equal(transcript.id, descriptor.id); assert.equal(transcript.scenario, descriptor.scenario);
    assert.equal(transcript.case, descriptor.case); assert.deepEqual(transcript.expected, descriptor.expected);
    assert.ok(required.includes(transcript.scenario));
    assert.ok(Array.isArray(transcript.steps) && transcript.steps.length > 0 && transcript.steps.length <= 512);
    const resolveMessage = (id, kind) => {
      const value = messages.get(id);
      assert.equal(value?.kind, kind, 'Every transcript message must first pass the actual Rust codec');
      return value.message;
    };
    const steps = transcript.steps.map(step => step.type === 'exchange'
      ? { ...step, request: resolveMessage(step.request, 'request'), reply: resolveMessage(step.reply, 'reply') }
      : step.type === 'event' ? { ...step, event: resolveMessage(step.event, 'event') } : step);
    let accepted = true, result, code = null, failedStep = null;
    try { result = checkTranscript(steps); }
    catch (error) {
      if (!(error instanceof TranscriptError)) throw error;
      accepted = false; code = error.code; failedStep = error.step;
    }
    assert.equal(accepted, descriptor.expected.accepted, `Transcript outcome differs for ${descriptor.id}`);
    if (!accepted) {
      assert.equal(code, descriptor.expected.code, `Transcript refusal differs for ${descriptor.id}`);
      if (Object.hasOwn(descriptor.expected, 'step')) assert.equal(failedStep, descriptor.expected.step);
    }
    transcriptOutcomes.push({ id: descriptor.id, scenario: descriptor.scenario, sha256: createHash('sha256').update(bytes).digest('hex'), accepted, code, failedStep, result: result ?? null });
    coverageTranscripts.push(transcript);
  }
  const aggregate = JSON.parse(readFileSync(ownedProtocolPath(root, 'contracts/generated/aggregate.schema.json'), 'utf8'));
  assert.deepEqual(verifyScenarioCoverage(coverageMessages, coverageTranscripts, aggregate), index.coverage, 'Scenario coverage must follow actual accepted messages');
}
const directory = path.join(root, 'artifacts/next/protocol', randomUUID());
mkdirSync(directory, { recursive: true });
writeFileSync(path.join(directory, 'fixtures.json'), JSON.stringify({
  schemaVersion: 'bridge-protocol-fixture-observation/v1', boundary: index.proofLayer,
  catalogComplete: complete, schemas, outcomes, transcriptOutcomes, codecArtifactSha256: createHash('sha256').update(readFileSync(binary)).digest('hex'),
  actualHost: { platform: process.platform, architecture: process.arch, node: process.version },
  engineExecutionQualified: false, nativeRuntimeQualified: false, releaseQualified: false
}, null, 2) + '\n');
console.log(JSON.stringify({ result: 'passed', checkpointOnly: !complete, fixtures: outcomes.length, transcripts: transcriptOutcomes.length, receipt: path.join(directory, 'fixtures.json'), packageAcceptance: false }));
