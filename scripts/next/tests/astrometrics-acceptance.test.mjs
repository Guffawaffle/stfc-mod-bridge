import test from 'node:test';
import assert from 'node:assert/strict';
import { mkdtempSync, mkdirSync, writeFileSync, readFileSync, existsSync, symlinkSync, unlinkSync, rmdirSync } from 'node:fs';
import os from 'node:os';
import path from 'node:path';
import { spawnSync } from 'node:child_process';
import { requireInventory, requireExecution, requireObservation, toolIdentity, fileIdentity, cleanupFixture, TEST_NAME } from '../astrometrics-acceptance.mjs';

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


test('tool routes accept standard symlink shims and detect retargeting; artifacts remain strict', t => {
  const directory = mkdtempSync(path.join(os.tmpdir(), 'bridge-astrometrics-tool-'));
  const payload = path.join(directory, 'rustup'), replacement = path.join(directory, 'other-rustup');
  const route = path.join(directory, 'cargo');
  try {
    writeFileSync(payload, 'inert identical tool payload');
    writeFileSync(replacement, 'inert identical tool payload');
    try { symlinkSync(payload, route, 'file'); }
    catch (error) {
      if (process.platform === 'win32' && ['EPERM', 'EACCES'].includes(error.code)) {
        t.skip('This Windows account cannot create file symlinks'); return;
      }
      throw error;
    }
    const before = toolIdentity(route);
    assert.equal(before.path, route);
    assert.equal(before.sha256, fileIdentity(payload).sha256);
    assert.throws(() => fileIdentity(route), /Invalid bounded executable/);
    unlinkSync(route);
    symlinkSync(replacement, route, 'file');
    const after = toolIdentity(route);
    assert.equal(after.sha256, before.sha256);
    assert.notEqual(after.resolvedPath, before.resolvedPath);
  } finally {
    for (const file of [route, payload, replacement]) { try { unlinkSync(file); } catch (error) { if (error.code !== 'ENOENT') throw error; } }
    rmdirSync(directory);
  }
});

test('parent removes exact token database artifacts after a child timeout', () => {
  const root = mkdtempSync(path.join(os.tmpdir(), 'bridge-astrometrics-timeout-'));
  const fixture = path.join(root, 'fixture');
  mkdirSync(fixture);
  const names = ['fixture-events.sqlite', 'fixture-events.sqlite-journal', 'fixture-events.sqlite-wal', 'fixture-events.sqlite-shm'];
  const keep = path.join(fixture, 'astrometrics-observation.json');
  writeFileSync(keep, '{}');
  try {
    const child = spawnSync(process.execPath, ['-e', `
      const fs = require('node:fs'), path = require('node:path');
      for (const name of JSON.parse(process.argv[2])) fs.writeFileSync(path.join(process.argv[1], name), 'synthetic fixture credential');
      Atomics.wait(new Int32Array(new SharedArrayBuffer(4)), 0, 0);
    `, fixture, JSON.stringify(names)], { windowsHide: true, timeout: 2000 });
    assert.equal(child.error?.code, 'ETIMEDOUT');
    assert.ok(names.every(name => existsSync(path.join(fixture, name))), 'Child must create its database before forced termination');
    assert.deepEqual(cleanupFixture(root, fixture), { status: 'complete', removed: 4 });
    assert.ok(names.every(name => !existsSync(path.join(fixture, name))));
    assert.equal(readFileSync(keep, 'utf8'), '{}');
    assert.deepEqual(cleanupFixture(root, fixture), { status: 'complete', removed: 0 });
  } finally {
    for (const name of [...names, 'astrometrics-observation.json']) { try { unlinkSync(path.join(fixture, name)); } catch (error) { if (error.code !== 'ENOENT') throw error; } }
    rmdirSync(fixture); rmdirSync(root);
  }
});
