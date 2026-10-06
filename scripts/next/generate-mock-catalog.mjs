import assert from 'node:assert/strict';
import { mkdirSync, readFileSync, writeFileSync } from 'node:fs';
import { createHash } from 'node:crypto';
import path from 'node:path';
import { ownedProtocolPath } from './protocol-files.mjs';
import { decodeStrictJson } from '../../contracts/codec/strict-json.mjs';
import { checkTranscript } from '../../contracts/codec/transcript.mjs';
import { validateRequest, validateReply, validateEvent } from '../../ui/src/generated/validators.mjs';

const root = path.resolve(import.meta.dirname, '../..');
const args = process.argv.slice(2);
assert.ok(args.length === 0 || args.length === 1 && args[0] === '--check', 'Use generate-mock-catalog.mjs [--check]');
const check = args[0] === '--check';
const hash = bytes => createHash('sha256').update(bytes).digest('hex');
const indexPath = ownedProtocolPath(root, 'contracts/fixtures/index.json');
const indexBytes = readFileSync(indexPath), index = JSON.parse(indexBytes.toString('utf8'));
assert.equal(index.schemaVersion, 'bridge-scenario-fixtures/v1');
assert.equal(index.status, 'complete');
const descriptors = new Map(index.fixtures.map(fixture => [fixture.id, fixture]));
const validators = { request: validateRequest, reply: validateReply, event: validateEvent };
const messages = new Map();
function message(id, kind) {
  const descriptor = descriptors.get(id);
  assert.equal(descriptor?.kind, kind);
  assert.equal(descriptor.expectedWire, true);
  assert.equal(descriptor.expectedSemantic, true, 'Normal mock scripts require a Rust-conformant source fixture');
  if (!messages.has(id)) {
    assert.match(descriptor.path, /^[a-z][a-z0-9-]{0,95}\.(json|bin)$/);
    const bytes = readFileSync(ownedProtocolPath(root, `contracts/fixtures/${descriptor.normalizedPath ?? descriptor.path}`));
    const payload = decodeStrictJson(bytes);
    assert.equal(validators[kind](payload), true, 'Shared mock frames must pass generated browser guards');
    messages.set(id, { payload, sha256: hash(bytes) });
  }
  return messages.get(id);
}
const scripts = [];
for (const descriptor of index.transcripts.filter(transcript => transcript.expected.accepted)) {
  assert.match(descriptor.path, /^[a-z][a-z0-9-]{0,95}\.transcript\.json$/);
  const bytes = readFileSync(ownedProtocolPath(root, `contracts/fixtures/${descriptor.path}`));
  const transcript = JSON.parse(bytes.toString('utf8'));
  assert.equal(transcript.id, descriptor.id);
  assert.equal(transcript.expected.accepted, true);
  const sources = new Map();
  const get = (id, kind) => {
    const captured = message(id, kind);
    sources.set(id, { id, sha256: captured.sha256 });
    return captured.payload;
  };
  const steps = transcript.steps.map(step => step.type === 'exchange'
    ? { ...step, request: get(step.request, 'request'), reply: get(step.reply, 'reply') }
    : step.type === 'event' ? { ...step, event: get(step.event, 'event') } : step);
  checkTranscript(steps);
  scripts.push({ id: transcript.id, scenario: transcript.scenario, case: transcript.case,
    correlationSlot: 'requestId', steps, sources: [...sources.values()].sort((a, b) => a.id.localeCompare(b.id)),
    transcriptSource: { path: `contracts/fixtures/${descriptor.path}`, sha256: hash(bytes) } });
}
assert.ok(scripts.length > 0 && scripts.length <= 256);
const catalog = JSON.stringify({ schemaVersion: 'bridge-browser-scenario-catalog/v1',
  boundary: 'shared accepted synthetic frames and scripted delivery; no policy/native engine', scripts }, null, 2) + '\n';
assert.ok(Buffer.byteLength(catalog) <= 16 * 1024 * 1024, 'Mock catalog remains bounded');
const files = new Map([['ui/scenarios/catalog.json', catalog]]);
files.set('ui/scenarios/manifest.json', JSON.stringify({ schemaVersion: 'bridge-browser-scenario-manifest/v1',
  scripts: scripts.length, fixtures: messages.size,
  sourceIndexSha256: hash(indexBytes),
  sourceProtocolManifestSha256: hash(readFileSync(ownedProtocolPath(root, 'contracts/generated/manifest.json'))),
  browserValidatorManifestSha256: hash(readFileSync(ownedProtocolPath(root, 'contracts/generated/browser-validator-manifest.json'))),
  correlationSlots: ['requestId'], files: [{ path: 'ui/scenarios/catalog.json', sha256: hash(catalog) }],
  engineExecutionQualified: false, nativeRuntimeQualified: false, releaseQualified: false
}, null, 2) + '\n');
for (const [file, content] of files) {
  const target = path.join(root, file);
  if (check) assert.equal(readFileSync(target, 'utf8'), content, `Mock catalog drift: ${file}`);
  else { mkdirSync(path.dirname(target), { recursive: true }); writeFileSync(target, content); }
}
console.log(JSON.stringify({ result: check ? 'checked' : 'generated', scripts: scripts.length,
  fixtures: messages.size, rustBuildInvoked: false, engineExecutionQualified: false }));
