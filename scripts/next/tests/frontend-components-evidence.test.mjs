import assert from 'node:assert/strict';
import { test } from 'node:test';
import path from 'node:path';
import { componentCriteria, componentEvidence } from '../frontend-components-evidence.mjs';

const root = path.resolve(import.meta.dirname, '../../..');
function report() {
  return { success: true, numFailedTests: 0, numPendingTests: 0, numTodoTests: 0,
    numFailedTestSuites: 0, numPendingTestSuites: 0, numTotalTests: 87, numPassedTests: 87,
    testResults: Object.entries(componentCriteria).map(([file, titles]) => ({ name: path.join(root, file), status: 'passed',
      assertionResults: titles.map(title => ({ title, status: 'passed' })) })) };
}
test('component receipt binds all required assertion names and exact observed file counts', () => {
  const evidence = componentEvidence(report(), root);
  assert.equal(evidence.tests, 87); assert.deepEqual(evidence.files.map(file => file.tests), [51, 36]);
});
test('component receipt rejects a zero-exit skipped criterion', () => {
  const value = report(); value.testResults[0].assertionResults[0].status = 'skipped';
  assert.throws(() => componentEvidence(value, root));
});
test('component receipt rejects missing renamed or duplicate criterion identities', () => {
  for (const name of ['', 'unrelated passing assertion', componentCriteria['ui/tests/components/primitives.test.ts'][1]]) {
    const value = report(); value.testResults[0].assertionResults[0].title = name;
    assert.throws(() => componentEvidence(value, root));
  }
});
test('component receipt rejects substituted or extra test files', () => {
  const substituted = report(); substituted.testResults[0].name = path.join(root, 'ui/tests/unrelated.test.ts');
  assert.throws(() => componentEvidence(substituted, root));
  const extra = report(); extra.testResults.push({ name: path.join(root, 'ui/tests/extra.test.ts'), status: 'passed', assertionResults: [{ title: 'extra', status: 'passed' }] });
  extra.numTotalTests++; extra.numPassedTests++; assert.throws(() => componentEvidence(extra, root));
});
test('component receipt rejects missing assertions and dishonest total counts', () => {
  const missing = report(); missing.testResults[1].assertionResults.pop(); missing.numTotalTests--; missing.numPassedTests--;
  assert.throws(() => componentEvidence(missing, root));
  const total = report(); total.numTotalTests++; total.numPassedTests++; assert.throws(() => componentEvidence(total, root));
});
test('component receipt rejects failed cancelled pending or todo evidence', () => {
  for (const key of ['numFailedTests', 'numPendingTests', 'numTodoTests', 'numFailedTestSuites', 'numPendingTestSuites']) {
    const value = report(); value[key] = 1; assert.throws(() => componentEvidence(value, root));
  }
  const cancelled = report(); cancelled.testResults[0].assertionResults[0].status = 'cancelled';
  assert.throws(() => componentEvidence(cancelled, root));
});
