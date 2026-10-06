import test from 'node:test';
import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import path from 'node:path';
import { ownedProtocolPath } from '../protocol-files.mjs';
import { decodeStrictJson, WireInputError } from '../../../contracts/codec/strict-json.mjs';
import { validateRequest, validateReply, validateEvent } from '../../../ui/src/generated/validators.mjs';

const root = path.resolve(import.meta.dirname, '../../..');
test('bundled browser guards match every shared golden wire outcome without native semantic claims', () => {
  const index = JSON.parse(readFileSync(ownedProtocolPath(root, 'contracts/fixtures/index.json'), 'utf8'));
  const guards = { request: validateRequest, reply: validateReply, event: validateEvent };
  assert.equal(index.status, 'complete');
  let refused = 0, accepted = 0;
  for (const fixture of index.fixtures) {
    const bytes = readFileSync(ownedProtocolPath(root, `contracts/fixtures/${fixture.path}`));
    let valid;
    try { valid = guards[fixture.kind](decodeStrictJson(bytes)); }
    catch (error) { assert.ok(error instanceof WireInputError); valid = false; }
    assert.equal(valid, fixture.expectedWire, fixture.id);
    if (valid) accepted++; else refused++;
  }
  assert.ok(refused > 0 && accepted > 0);
});
