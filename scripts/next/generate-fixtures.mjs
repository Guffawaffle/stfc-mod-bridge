import assert from 'node:assert/strict';
import { createHash } from 'node:crypto';
import { existsSync, readFileSync, writeFileSync } from 'node:fs';
import path from 'node:path';
import { pathToFileURL } from 'node:url';
import { ownedProtocolPath } from './protocol-files.mjs';
import { SCENARIOS, verifyScenarioCoverage } from './scenario-coverage.mjs';

const root = path.resolve(import.meta.dirname, '../..');
const arguments_ = process.argv.slice(2);
assert.ok(arguments_.length === 0 || (arguments_.length === 1 && arguments_[0] === '--check'), 'Use generate-fixtures.mjs [--check]');
const check = arguments_[0] === '--check';
const fixtureDirectory = ownedProtocolPath(root, 'contracts/fixtures', 'directory');
const sources = ['management-cases.ts', 'configuration-cases.ts', 'distribution-cases.ts', 'workflow-cases.ts'];
const canonical = value => Array.isArray(value) ? value.map(canonical) : value && typeof value === 'object'
  ? Object.fromEntries(Object.keys(value).sort().map(key => [key, canonical(value[key])])) : value;
function hash(prefix, value) {
  return `sha256:${createHash('sha256').update(prefix + '\0').update(JSON.stringify(canonical(value))).digest('hex')}`;
}
const hooks = {
  semanticDigest(semantics) {
    const value = structuredClone(semantics);
    value.effects.sort();
    if (value.capture.kind === 'restore_configuration') delete value.capture.input.backup.createdAt;
    return hash('bridge-plan-semantic-json-v1', value);
  },
  diagnosticDigest: content => hash('bridge-diagnostic-preview-json-v1', content)
};
const fixtures = [], transcripts = [], sourceDigests = [];
for (const source of sources) {
  const sourcePath = ownedProtocolPath(root, `contracts/fixtures/${source}`);
  const module = await import(pathToFileURL(sourcePath).href);
  assert.equal(typeof module.buildCatalog, 'function');
  const catalog = module.buildCatalog(hooks);
  assert.deepEqual(Object.keys(catalog).sort(), ['fixtures', 'transcripts']);
  fixtures.push(...catalog.fixtures); transcripts.push(...catalog.transcripts);
  sourceDigests.push({ path: `contracts/fixtures/${source}`, sha256: createHash('sha256').update(readFileSync(sourcePath)).digest('hex') });
}
assert.ok(fixtures.length > 0 && fixtures.length <= 1024);
assert.ok(transcripts.length > 0 && transcripts.length <= 256);
const aggregate = JSON.parse(readFileSync(ownedProtocolPath(root, 'contracts/generated/aggregate.schema.json'), 'utf8'));
const coverage = verifyScenarioCoverage(fixtures, transcripts, aggregate);
const files = new Map(), ids = new Set();
const jsonBytes = value => Buffer.from(JSON.stringify(canonical(value), null, 2) + '\n');
function add(file, bytes) {
  assert.ok(!files.has(file), `Duplicate fixture output ${file}`); files.set(file, bytes);
}
const descriptors = fixtures.map(fixture => {
  assert.match(fixture.id, /^[a-z][a-z0-9-]{0,95}$/);
  assert.ok(!ids.has(fixture.id), `Duplicate fixture ID ${fixture.id}`); ids.add(fixture.id);
  assert.ok(SCENARIOS.includes(fixture.scenario));
  assert.ok(typeof fixture.case === 'string' && fixture.case.length > 0 && fixture.case.length <= 256);
  assert.ok(Array.isArray(fixture.tags) && fixture.tags.length <= 16 && fixture.tags.every(tag => /^[a-z][a-z0-9-]{0,47}$/.test(tag)));
  assert.ok(['request', 'reply', 'event'].includes(fixture.kind));
  assert.equal(typeof fixture.expectedWire, 'boolean'); assert.equal(typeof fixture.expectedSemantic, 'boolean');
  assert.ok(!fixture.expectedSemantic || fixture.expectedWire);
  assert.notEqual(Object.hasOwn(fixture, 'payload'), Object.hasOwn(fixture, 'raw'), 'Provide either typed payload or raw bytes');
  const bytes = Object.hasOwn(fixture, 'raw') ? Buffer.from(fixture.raw) : jsonBytes(fixture.payload);
  assert.ok(bytes.length <= 512 * 1024, 'Even explicit oversize refusal fixtures remain bounded');
  const file = `${fixture.id}.${fixture.raw instanceof Uint8Array ? 'bin' : 'json'}`;
  add(file, bytes);
  return { id: fixture.id, scenario: fixture.scenario, case: fixture.case, tags: fixture.tags, kind: fixture.kind, path: file, expectedWire: fixture.expectedWire, expectedSemantic: fixture.expectedSemantic };
});
const transcriptIds = new Set();
const journeys = transcripts.map(transcript => {
  assert.match(transcript.id, /^[a-z][a-z0-9-]{0,95}$/);
  assert.ok(!transcriptIds.has(transcript.id), 'Duplicate transcript ID'); transcriptIds.add(transcript.id);
  assert.ok(SCENARIOS.includes(transcript.scenario));
  assert.ok(transcript.steps.length > 0 && transcript.steps.length <= 512);
  for (const step of transcript.steps) {
    for (const key of step.type === 'exchange' ? ['request', 'reply'] : step.type === 'event' ? ['event'] : []) {
      const fixture = fixtures.find(value => value.id === step[key]);
      assert.ok(fixture?.expectedSemantic, 'Transcripts may reference only codec-accepted messages');
      assert.equal(fixture.kind, key, 'Transcript fixture kind mismatch');
    }
  }
  const file = `${transcript.id}.transcript.json`; add(file, jsonBytes(transcript));
  return { id: transcript.id, scenario: transcript.scenario, case: transcript.case, path: file, expected: transcript.expected };
});
add('index.json', jsonBytes({
  schemaVersion: 'bridge-scenario-fixtures/v1', status: 'complete', requiredScenarioIds: SCENARIOS,
  fixtures: descriptors, transcripts: journeys, coverage,
  proofLayer: 'wire/schema and synthetic semantic validation',
  engineExecutionQualified: false, nativeRuntimeQualified: false, releaseQualified: false
}));
const outputs = [...files].sort(([a], [b]) => a.localeCompare(b)).map(([file, bytes]) => ({ path: `contracts/fixtures/${file}`, sha256: createHash('sha256').update(bytes).digest('hex') }));
add('generated-manifest.json', jsonBytes({ schemaVersion: 'bridge-generated-fixtures/v1', sources: sourceDigests, files: outputs, proofLayer: 'synthetic fixture source drift only', engineExecutionQualified: false, nativeRuntimeQualified: false, releaseQualified: false }));
for (const [file, bytes] of files) {
  const destination = path.join(fixtureDirectory, file);
  if (check) assert.deepEqual(readFileSync(ownedProtocolPath(root, `contracts/fixtures/${file}`)), bytes, `Fixture source drift: ${file}`);
  else {
    if (existsSync(destination)) ownedProtocolPath(root, `contracts/fixtures/${file}`);
    writeFileSync(destination, bytes);
  }
}
console.log(JSON.stringify({ result: 'passed', mode: check ? 'drift-check' : 'generated', fixtures: fixtures.length, transcripts: transcripts.length, scenarios: coverage.length, files: files.size, engineExecutionQualified: false, nativeRuntimeQualified: false, releaseQualified: false }));
