import test from 'node:test';
import assert from 'node:assert/strict';
import { spawnSync } from 'node:child_process';
import { mkdtempSync, realpathSync, rmSync, writeFileSync } from 'node:fs';
import path from 'node:path';
import os from 'node:os';
import { pathToFileURL } from 'node:url';
import { nodeTestEvidence, vitestEvidence } from '../test-evidence.mjs';

const root = path.resolve(import.meta.dirname, '../../..');
test('zero-exit Node run with skipped criterion cannot qualify', () => {
  const base = mkdtempSync(path.join(os.tmpdir(), 'bridge-test-evidence-'));
  try {
    const file = path.join(base, 'skip.test.mjs');
    writeFileSync(file, "import test from 'node:test'; test('baseline', () => {}); test.skip('criterion', () => {});\n");
    const childEnvironment = { ...process.env };
    delete childEnvironment.NODE_TEST_CONTEXT;
    const result = spawnSync(process.execPath, ['--test', `--test-reporter=${pathToFileURL(path.join(root, 'scripts/next/node-test-reporter.mjs')).href}`, file], { env: childEnvironment, encoding: 'utf8', windowsHide: true });
    assert.equal(result.status, 0, result.stderr.slice(-2000));
    assert.ok(result.stdout.trim(), 'Nested real Node run must return its reporter stream');
    assert.throws(() => nodeTestEvidence(result.stdout, { root: base, file: 'skip.test.mjs', requiredNames: ['criterion'] }), /skipped/);
  } finally {
    assert.equal(path.dirname(realpathSync(base)), realpathSync(os.tmpdir()));
    assert.ok(path.basename(base).startsWith('bridge-test-evidence-'));
    rmSync(base, { recursive: true, force: true });
  }
});
test('Vitest evidence refuses skipped assertions, absent files and missing criterion names', () => {
  const report = { success: true, numTotalTests: 1, numPassedTests: 1,
    numFailedTests: 0, numPendingTests: 0, numTodoTests: 0, numFailedTestSuites: 0, numPendingTestSuites: 0,
    testResults: [{ name: path.join(root, 'ui/tests/synthetic.test.ts'), status: 'passed',
      assertionResults: [{ status: 'passed', title: 'criterion' }] }] };
  const options = { root, required: { 'ui/tests/synthetic.test.ts': ['criterion'] } };
  assert.equal(vitestEvidence(report, options).tests, 1);
  const skipped = structuredClone(report); skipped.testResults[0].assertionResults[0].status = 'pending';
  assert.throws(() => vitestEvidence(skipped, options), /did not execute/);
  assert.throws(() => vitestEvidence(report, { root, required: { 'ui/tests/missing.test.ts': ['criterion'] } }), /file did not execute/);
  assert.throws(() => vitestEvidence(report, { root, required: { 'ui/tests/synthetic.test.ts': ['removed criterion'] } }), /criterion test did not execute/);
});
