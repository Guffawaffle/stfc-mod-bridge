import test from 'node:test';
import assert from 'node:assert/strict';
import { mkdirSync, mkdtempSync, realpathSync, rmSync, symlinkSync, unlinkSync, writeFileSync } from 'node:fs';
import path from 'node:path';
import os from 'node:os';
import { ownedProtocolPath } from '../protocol-files.mjs';

function fixture(action) {
  const base = mkdtempSync(path.join(os.tmpdir(), 'bridge-protocol-path-'));
  const root = path.join(base, 'root');
  const outside = path.join(base, 'outside');
  mkdirSync(root); mkdirSync(outside);
  const links = [];
  try { action({ root, outside, links }); }
  finally {
    assert.equal(path.dirname(realpathSync(base)), realpathSync(os.tmpdir()));
    assert.ok(path.basename(base).startsWith('bridge-protocol-path-'));
    for (const link of links) { assert.equal(path.dirname(link), root); unlinkSync(link); }
    rmSync(base, { recursive: true, force: true });
  }
}

test('only an exact regular file or directory inside the owner resolves', () => fixture(({ root }) => {
  mkdirSync(path.join(root, 'src'));
  writeFileSync(path.join(root, 'src/input.json'), '{}');
  assert.equal(ownedProtocolPath(root, 'src/input.json'), realpathSync(path.join(root, 'src/input.json')));
  assert.equal(ownedProtocolPath(root, 'src', 'directory'), realpathSync(path.join(root, 'src')));
  assert.throws(() => ownedProtocolPath(root, 'src'));
  assert.throws(() => ownedProtocolPath(root, 'src/input.json', 'directory'));
  for (const relative of ['', '.', '../outside', '/outside', 'C:\\outside', 'src/../input.json', 'src\\..\\input.json', 'src/input.json:stream']) assert.throws(() => ownedProtocolPath(root, relative));
}));

test('fixture roots and executable-output junctions cannot switch physical owners', t => fixture(({ root, outside, links }) => {
  writeFileSync(path.join(outside, 'bridge-protocol.exe'), 'Synthetic executable bytes; never executed.');
  writeFileSync(path.join(outside, 'input.json'), '{}');
  for (const name of ['fixtures', 'target']) {
    const link = path.join(root, name);
    try { symlinkSync(outside, link, process.platform === 'win32' ? 'junction' : 'dir'); }
    catch (error) {
      if (!['EPERM', 'EACCES'].includes(error.code)) throw error;
      t.skip('This host does not permit the synthetic directory link.');
      return;
    }
    links.push(link);
    assert.throws(() => ownedProtocolPath(root, name, 'directory'), /links or junctions/);
    assert.throws(() => ownedProtocolPath(root, `${name}/input.json`), /links or junctions/);
    assert.throws(() => ownedProtocolPath(root, `${name}/bridge-protocol.exe`), /links or junctions/);
  }
}));
