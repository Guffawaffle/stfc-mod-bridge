import test from 'node:test';
import assert from 'node:assert/strict';
import { requireInventory, requireExecution, requireObservation, TEST_NAME } from '../astrometrics-acceptance.mjs';

test('native execution refuses absent, extra, skipped or filtered test success', () => {
  requireInventory(`${TEST_NAME}: test\n\n1 test, 0 benchmarks\n`);
  for (const inventory of ['', 'other: test\n', `${TEST_NAME}: test\nother: test\n`]) assert.throws(() => requireInventory(inventory));
  const success = 'ASTROMETRICS_ACCEPTANCE_OK\ntest result: ok. 1 passed; 0 failed; 0 ignored; 0 measured; 0 filtered out; finished in 0.1s\n';
  requireExecution(success);
  for (const output of [success.replace('1 passed', '0 passed'), success.replace('0 ignored', '1 ignored'),
    success.replace('0 filtered out', '1 filtered out'), success.replace('ASTROMETRICS_ACCEPTANCE_OK', ''), success + success]) {
    assert.throws(() => requireExecution(output));
  }
});

test('native observation rejects undeclared secret fields and incomplete evidence', () => {
  const encode = value => Buffer.from(JSON.stringify(value));
  const observation = { schemaVersion: 'bridge-astrometrics-native-observation/v1', testName: TEST_NAME, success: true,
    authenticatedStatus: 200, unauthorizedStatus: 401, duplicateStatus: 200, conflictingStatus: 409, storedEvents: 2,
    disabledCreatesNoStore: true, disabledSocketClosed: true, idempotentReplay: true,
    conflictingReplayPreservesEvidence: true, maximumSequencePreserved: true, historyReadableWhileDisabled: true,
    historyReadLeavesDatabaseUnchanged: true, normalCloseJoinsOwner: true, normalCloseReleasesPort: true,
    databaseSha256: 'a'.repeat(64) };
  assert.deepEqual(requireObservation(encode(observation)), observation);
  for (const invalid of [{ ...observation, syncToken: 'secret' }, { ...observation, normalCloseJoinsOwner: false },
    { ...observation, databaseSha256: 'unverified' }, { ...observation, storedEvents: 0 }]) {
    assert.throws(() => requireObservation(encode(invalid)));
  }
  assert.throws(() => requireObservation(Buffer.alloc(16385)));
});
