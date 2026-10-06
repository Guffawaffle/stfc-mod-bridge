import test from 'node:test';
import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import path from 'node:path';
import { decodeStrictJson, WireInputError } from '../../../contracts/codec/strict-json.mjs';
import { ownedProtocolPath } from '../protocol-files.mjs';
import { verifyScenarioCoverage } from '../scenario-coverage.mjs';

const root = path.resolve(import.meta.dirname, '../../..');
const index = JSON.parse(readFileSync(ownedProtocolPath(root, 'contracts/fixtures/index.json'), 'utf8'));
const aggregate = JSON.parse(readFileSync(ownedProtocolPath(root, 'contracts/generated/aggregate.schema.json'), 'utf8'));
const fixtures = index.fixtures.map(fixture => {
  let payload;
  try { payload = JSON.parse(JSON.stringify(decodeStrictJson(readFileSync(ownedProtocolPath(root, `contracts/fixtures/${fixture.path}`))))); }
  catch (error) { if (!(error instanceof WireInputError)) throw error; }
  return { ...fixture, payload };
});
const transcripts = index.transcripts.map(transcript => JSON.parse(readFileSync(ownedProtocolPath(root, `contracts/fixtures/${transcript.path}`), 'utf8')));

// These regressions test qualification policy. The owner fixture harness proves
// codec outcomes and transcript relationships separately, on the same inputs.
test('actual catalog surfaces follow matching positive exchange pairs', () => {
  assert.deepEqual(verifyScenarioCoverage(fixtures, transcripts, aggregate), index.coverage);
});
test('boundary-only traces cannot certify the full scenario/API catalog', () => {
  const boundaries = transcripts.map(transcript => ({ ...transcript, steps: transcript.steps.filter(step => step.type === 'boundary') }));
  assert.throws(() => verifyScenarioCoverage(fixtures, boundaries, aggregate), /Missing accepted|lacks its accepted domain case|boundary-only/);
});
test('standalone preparation replies cannot substitute for positive action inputs', () => {
  const repliesOnly = fixtures.filter(fixture => !(fixture.expectedSemantic && fixture.kind === 'request' && fixture.payload?.body?.type === 'command' && fixture.payload.body.command.name === 'prepare'));
  assert.throws(() => verifyScenarioCoverage(repliesOnly, transcripts, aggregate), /codec-accepted request|Missing accepted/);
});
test('deleting every negative relationship assertion fails scenario acceptance', () => {
  assert.throws(() => verifyScenarioCoverage(fixtures, transcripts.filter(transcript => transcript.expected.accepted), aggregate), /deliberate relationship refusal/);
});
