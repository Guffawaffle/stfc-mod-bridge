import assert from 'node:assert/strict';
import { lstatSync, realpathSync } from 'node:fs';
import path from 'node:path';

// Inspect physical owning-root ancestry before a build/install/launch. This is
// an execution-time observation, not an exclusion or race-free native lock.
export function ownedArtifactPath(root, relative, kind = 'file', { allowMissing = false } = {}) {
  assert.ok(['file', 'directory'].includes(kind));
  assert.ok(typeof root === 'string' && path.isAbsolute(root));
  assert.ok(typeof relative === 'string' && relative && !relative.includes('\0'));
  assert.ok(!path.isAbsolute(relative) && !path.win32.parse(relative).root);
  const parts = relative.replaceAll('\\', '/').split('/');
  assert.ok(parts.length <= 32 && parts.every(part => part && part !== '.' && part !== '..' && !part.includes(':')));
  const rootEntry = lstatSync(root);
  assert.ok(rootEntry.isDirectory() && !rootEntry.isSymbolicLink(), 'Artifact owner root cannot be a link');
  const physicalRoot = realpathSync(root);
  let current = physicalRoot;
  for (let index = 0; index < parts.length; index++) {
    current = path.join(current, parts[index]);
    let entry;
    try { entry = lstatSync(current); }
    catch (error) {
      if (allowMissing && error.code === 'ENOENT') return path.join(current, ...parts.slice(index + 1));
      throw error;
    }
    assert.ok(!entry.isSymbolicLink(), 'Artifact paths may not follow links or junctions');
    assert.ok(index === parts.length - 1 ? kind === 'file' ? entry.isFile() : entry.isDirectory() : entry.isDirectory(),
      'Unexpected artifact resource kind');
    const observed = realpathSync(current);
    const relation = path.relative(physicalRoot, observed);
    assert.ok(relation && relation !== '..' && !relation.startsWith(`..${path.sep}`) && !path.isAbsolute(relation),
      'Artifact path escaped its physical owner');
    current = observed;
  }
  return current;
}
