import test from 'node:test';
import assert from 'node:assert/strict';
import { mkdirSync, mkdtempSync, realpathSync, rmSync, symlinkSync, unlinkSync, writeFileSync } from 'node:fs';
import path from 'node:path';
import os from 'node:os';
import { ownedArtifactPath } from '../owned-artifact.mjs';

function fixture(action) {
  const base = mkdtempSync(path.join(os.tmpdir(), 'bridge-artifact-path-'));
  const root = path.join(base, 'root'), outside = path.join(base, 'outside');
  mkdirSync(root); mkdirSync(outside);
  const links = [];
  try { action({ root, outside, links }); }
  finally {
    assert.equal(path.dirname(realpathSync(base)), realpathSync(os.tmpdir()));
    assert.ok(path.basename(base).startsWith('bridge-artifact-path-'));
    for (const link of links) {
      const relation = path.relative(root, link);
      assert.ok(relation && !relation.startsWith('..') && !path.isAbsolute(relation));
      unlinkSync(link);
    }
    rmSync(base, { recursive: true, force: true });
  }
}
test('artifact resolution permits only physical owned ancestry, including not-yet-created outputs', () => fixture(({ root }) => {
  mkdirSync(path.join(root, 'target'));
  writeFileSync(path.join(root, 'target/test.exe'), 'Synthetic bytes; never executed.');
  assert.equal(ownedArtifactPath(root, 'target/test.exe'), realpathSync(path.join(root, 'target/test.exe')));
  assert.equal(ownedArtifactPath(root, 'target/native/debug', 'directory', { allowMissing: true }), path.join(realpathSync(root), 'target/native/debug'));
  assert.throws(() => ownedArtifactPath(root, 'target/missing.exe'));
  for (const relative of ['', '.', '../outside', '/outside', 'C:\\outside', 'target/../outside', 'target/test.exe:stream']) assert.throws(() => ownedArtifactPath(root, relative, 'file', { allowMissing: true }));
}));
test('browser and native target junctions cannot move build or execution outside the owner', () => fixture(({ root, outside, links }) => {
  mkdirSync(path.join(root, 'artifacts/next/tooling'), { recursive: true });
  mkdirSync(path.join(root, 'target'));
  mkdirSync(path.join(root, 'ui'));
  writeFileSync(path.join(outside, 'synthetic.exe'), 'Synthetic bytes; never executed.');
  for (const relative of ['artifacts/next/tooling/browsers', 'target/native', 'ui/dist', 'artifacts/next/frontend']) {
    const link = path.join(root, relative);
    symlinkSync(outside, link, process.platform === 'win32' ? 'junction' : 'dir'); links.push(link);
    assert.throws(() => ownedArtifactPath(root, relative, 'directory', { allowMissing: true }), /links or junctions/);
    assert.throws(() => ownedArtifactPath(root, `${relative}/synthetic.exe`), /links or junctions/);
    assert.throws(() => ownedArtifactPath(root, `${relative}/missing/output`, 'directory', { allowMissing: true }), /links or junctions/);
  }
}));
