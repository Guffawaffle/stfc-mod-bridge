import test from 'node:test';
import assert from 'node:assert/strict';
import path from 'node:path';
import { nativeTestInventory, nativeTestResult, nativeMarker, nativePhysicalPath } from '../native-evidence.mjs';

test('physical native paths accept equivalent namespaces and refuse missing or foreign origins', () => {
  const selected = path.resolve(import.meta.filename);
  const expected = nativePhysicalPath(selected);
  assert.equal(nativePhysicalPath(path.toNamespacedPath(selected)), expected);
  assert.notEqual(nativePhysicalPath(path.resolve(import.meta.dirname, '../native-evidence.mjs')), expected);
  assert.throws(() => nativePhysicalPath(`${selected}.missing-native-observation`));
  for (const invalid of ['', 'relative/path', `${selected}\0`, 'x'.repeat(32769)]) assert.throws(() => nativePhysicalPath(invalid));
});

test('native inventories refuse absent extra duplicate renamed and empty criterion tests', () => {
  assert.deepEqual(nativeTestInventory('a: test\nb: test\n2 tests, 0 benchmarks\n', ['b', 'a']), ['a', 'b']);
  for (const text of ['', 'a: test\n', 'a: test\na: test\n', 'a: test\nc: test\n', 'a: test\nb: test\nc: test\n']) {
    assert.throws(() => nativeTestInventory(text, ['a', 'b']));
  }
  assert.throws(() => nativeTestInventory('', []));
  assert.throws(() => nativeTestInventory('a: test\na: test\n', ['a', 'a']));
});
test('native summaries require actual passed counts with no ignored measured or hidden filtered cases', () => {
  const summary = 'test result: ok. 1 passed; 0 failed; 0 ignored; 0 measured; 0 filtered out; finished in 0.00s\n';
  assert.doesNotThrow(() => nativeTestResult(summary, 1));
  for (const text of ['', summary + summary, summary.replace('1 passed', '0 passed'), summary.replace('0 ignored', '1 ignored'), summary.replace('0 failed', '1 failed'), summary.replace('0 measured', '1 measured'), summary.replace('0 filtered', '1 filtered')]) {
    assert.throws(() => nativeTestResult(text, 1));
  }
  assert.doesNotThrow(() => nativeTestResult(summary.replace('0 filtered', '4 filtered'), 1, 4));
  assert.throws(() => nativeTestResult(summary, 0));
});
test('producer observation markers refuse missing ambiguous or invalid structured output', () => {
  assert.deepEqual(nativeMarker('other\nOBS={"passed":true}\n', 'OBS='), { passed: true });
  assert.deepEqual(nativeMarker('test selected_test ... OBS={"passed":true}\nok\n', 'OBS=', 'selected_test'), { passed: true });
  assert.throws(() => nativeMarker('test another_test ... OBS={}\n', 'OBS=', 'selected_test'));
  for (const output of ['', 'OBS={}\nOBS={}\n', 'OBS=invalid\n']) assert.throws(() => nativeMarker(output, 'OBS='));
});
