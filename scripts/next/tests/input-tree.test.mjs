import test from 'node:test';
import assert from 'node:assert/strict';
import { createHash } from 'node:crypto';
import { mkdirSync, mkdtempSync, realpathSync, renameSync, rmSync, rmdirSync, symlinkSync, unlinkSync, writeFileSync } from 'node:fs';
import { createServer } from 'node:net';
import os from 'node:os';
import path from 'node:path';
import { fingerprintInputRecords, InputInventoryBlocked, MAX_INPUT_DEPTH, MAX_INPUT_RECORDS } from '../input-tree.mjs';

const blocked = code => error => error instanceof InputInventoryBlocked && error.code === code;
function makeFixture() {
  const base = mkdtempSync(path.join(os.tmpdir(), 'bridge-input-tree-'));
  const root = path.join(base, 'root');
  const outside = path.join(base, 'outside');
  mkdirSync(root);
  mkdirSync(outside);
  const links = [];
  const write = (relative, bytes = 'Synthetic source.') => {
    const selected = path.join(root, relative);
    mkdirSync(path.dirname(selected), { recursive: true });
    writeFileSync(selected, bytes);
  };
  const cleanup = () => {
    const actual = realpathSync(base);
    assert.equal(path.dirname(actual), realpathSync(os.tmpdir()));
    assert.ok(path.basename(actual).startsWith('bridge-input-tree-'));
    for (const link of links) {
      const relative = path.relative(base, link);
      assert.ok(relative && relative !== '..' && !relative.startsWith(`..${path.sep}`) && !path.isAbsolute(relative));
      unlinkSync(link);
    }
    rmSync(actual, { recursive: true, force: true, maxRetries: 5, retryDelay: 10 });
  };
  return { base, root, outside, write, links, cleanup };
}
function fixture(action) {
  const value = makeFixture();
  try { return action(value); }
  finally { value.cleanup(); }
}
function directoryLink(t, value, target, relative) {
  const link = path.join(value.root, relative);
  mkdirSync(path.dirname(link), { recursive: true });
  try { symlinkSync(target, link, process.platform === 'win32' ? 'junction' : 'dir'); }
  catch (error) {
    if (error.code !== 'EPERM' && error.code !== 'EACCES') throw error;
    t.skip('This host does not permit the synthetic directory-link fixture.');
    return false;
  }
  value.links.push(link);
  return true;
}

test('records are sorted, canonical, content-sensitive and deduplicate overlapping selections', () => fixture(({ root, write }) => {
  write('src/z.rs', 'z'); write('src/nested/a.rs', 'a'); write('controller.mjs', 'controller');
  const first = fingerprintInputRecords(root, ['src', './controller.mjs', 'src/nested', 'src/z.rs', 'src/', 'src\\nested']);
  assert.deepEqual(first.map(record => record.path), ['controller.mjs', 'src/', 'src/nested/', 'src/nested/a.rs', 'src/z.rs']);
  assert.deepEqual(first, fingerprintInputRecords(root, ['controller.mjs', 'src']));
  assert.equal(first.find(record => record.path === 'src/z.rs').sha256, createHash('sha256').update('z').digest('hex'));
  write('src/z.rs', 'changed');
  assert.notDeepEqual(first, fingerprintInputRecords(root, ['src', 'controller.mjs']));
  for (const record of first) assert.match(record.sha256, /^[a-f0-9]{64}$/);
}));

test('every call observes additions, removals, renames and empty-directory membership', () => fixture(({ root, write }) => {
  write('src/original.rs', 'same bytes');
  const initial = fingerprintInputRecords(root, ['src']);
  write('src/new.rs');
  const added = fingerprintInputRecords(root, ['src']);
  assert.notDeepEqual(initial, added);
  assert.notEqual(initial[0].sha256, added[0].sha256);
  unlinkSync(path.join(root, 'src/new.rs'));
  assert.deepEqual(initial, fingerprintInputRecords(root, ['src']));
  renameSync(path.join(root, 'src/original.rs'), path.join(root, 'src/renamed.rs'));
  const renamed = fingerprintInputRecords(root, ['src']);
  assert.notDeepEqual(initial, renamed);
  assert.equal(initial[1].sha256, renamed[1].sha256);
  mkdirSync(path.join(root, 'src/empty'));
  const empty = fingerprintInputRecords(root, ['src']);
  assert.ok(empty.some(record => record.path === 'src/empty/'));
  assert.notDeepEqual(renamed, empty);
  renameSync(path.join(root, 'src/empty'), path.join(root, 'src/other-empty'));
  assert.notDeepEqual(empty, fingerprintInputRecords(root, ['src']));
  rmdirSync(path.join(root, 'src/other-empty'));
  assert.deepEqual(renamed, fingerprintInputRecords(root, ['src']));
}));

test('only selected trees are visited; repository root and path escapes are rejected safely', () => fixture(({ root, outside, write }) => {
  write('src/..contained.rs'); write('target/ignored.rs'); write('node_modules/package/input.js'); write('artifacts/receipt.json');
  const selected = fingerprintInputRecords(root, ['src']);
  assert.deepEqual(selected.map(record => record.path), ['src/', 'src/..contained.rs']);
  assert.equal(fingerprintInputRecords(root, ['target']).length, 2, 'An explicit descriptor can select a normally unvisited tree');
  for (const input of ['.', './', '']) assert.throws(() => fingerprintInputRecords(root, [input]), blocked(input ? 'INPUT_ROOT_SELECTION' : 'INPUT_DESCRIPTOR_INVALID'));
  for (const input of ['../outside', 'src/../../outside', outside, 'C:\\outside', 'C:outside']) {
    assert.throws(() => fingerprintInputRecords(root, [input]), error => {
      assert.ok(blocked('INPUT_ESCAPE')(error));
      assert.ok(!error.message.includes(outside));
      return true;
    });
  }
  for (const input of [null, {}, 'src\0other', 'src/file:stream']) assert.throws(() => fingerprintInputRecords(root, [input]), blocked('INPUT_DESCRIPTOR_INVALID'));
  assert.throws(() => fingerprintInputRecords(root, 'src'), blocked('INPUT_DESCRIPTOR_INVALID'));
  assert.throws(() => fingerprintInputRecords('relative', ['src']), blocked('INPUT_ROOT_INVALID'));
  assert.throws(() => fingerprintInputRecords(root, ['absent']), blocked('INPUT_UNAVAILABLE'));
}));

test('outside directory junctions/links reject direct, ancestor and recursive traversal', t => fixture(value => {
  writeFileSync(path.join(value.outside, 'outside.rs'), 'Must not be inventoried.');
  if (!directoryLink(t, value, value.outside, 'src/external')) return;
  for (const selected of ['src/external', 'src/external/outside.rs', 'src']) assert.throws(() => fingerprintInputRecords(value.root, [selected]), blocked('INPUT_LINK'));
}));

test('directory junctions/links inside the root are also rejected', t => fixture(value => {
  value.write('actual/file.rs');
  if (!directoryLink(t, value, path.join(value.root, 'actual'), 'src/alias')) return;
  assert.throws(() => fingerprintInputRecords(value.root, ['src']), blocked('INPUT_LINK'));
  assert.throws(() => fingerprintInputRecords(path.join(value.root, 'src/alias'), ['file.rs']), blocked('INPUT_LINK'));
}));

test('file and dangling symbolic links are never followed', t => fixture(value => {
  value.write('actual.rs');
  const link = path.join(value.root, 'linked.rs');
  try { symlinkSync(path.join(value.root, 'actual.rs'), link, 'file'); }
  catch (error) {
    if (error.code !== 'EPERM' && error.code !== 'EACCES') throw error;
    t.skip('This host does not permit the synthetic file-symlink fixture.'); return;
  }
  value.links.push(link);
  assert.throws(() => fingerprintInputRecords(value.root, ['linked.rs']), blocked('INPUT_LINK'));
  unlinkSync(path.join(value.root, 'actual.rs'));
  assert.throws(() => fingerprintInputRecords(value.root, ['linked.rs']), blocked('INPUT_LINK'));
}));

test('depth and unique record limits bound traversal', () => fixture(({ root }) => {
  const tooDeep = Array.from({ length: MAX_INPUT_DEPTH + 1 }, () => 'd').join(path.sep);
  mkdirSync(path.join(root, tooDeep), { recursive: true });
  assert.throws(() => fingerprintInputRecords(root, ['d']), blocked('INPUT_DEPTH_LIMIT'));
  assert.throws(() => fingerprintInputRecords(root, [tooDeep]), blocked('INPUT_DEPTH_LIMIT'));
  mkdirSync(path.join(root, 'wide'));
  for (let i = 0; i < MAX_INPUT_RECORDS - 1; i++) writeFileSync(path.join(root, 'wide', `${i}.rs`), '');
  assert.equal(fingerprintInputRecords(root, ['wide', 'wide/0.rs']).length, MAX_INPUT_RECORDS);
  writeFileSync(path.join(root, 'wide', 'one-too-many.rs'), '');
  assert.throws(() => fingerprintInputRecords(root, ['wide']), blocked('INPUT_RECORD_LIMIT'));
}));

test('nonregular filesystem entries refuse inventory', { skip: process.platform === 'win32' }, async () => {
  const value = makeFixture();
  const server = createServer();
  try {
    const socket = path.join(value.root, 'socket');
    await new Promise((resolve, reject) => { server.once('error', reject); server.listen(socket, resolve); });
    assert.throws(() => fingerprintInputRecords(value.root, ['socket']), blocked('INPUT_NON_REGULAR'));
  } finally {
    if (server.listening) await new Promise(resolve => server.close(resolve));
    value.cleanup();
  }
});
