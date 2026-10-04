import assert from 'node:assert/strict';
import { lstatSync, realpathSync } from 'node:fs';
import path from 'node:path';

// Resolve a developer protocol input/output within its physical owning checkout.
// This is an execution-time observation, not a retained lock or provenance proof.
export function ownedProtocolPath(root, relative, kind = 'file') {
  assert.ok(['file', 'directory'].includes(kind), 'Unknown protocol resource kind');
  assert.ok(typeof relative === 'string' && relative && !relative.includes('\0'), 'Invalid protocol resource path');
  assert.ok(!path.isAbsolute(relative) && !path.win32.parse(relative).root, 'Protocol resource must use an owning-root relative path');
  const parts = relative.replaceAll('\\', '/').split('/');
  assert.ok(parts.every(part => part && part !== '.' && part !== '..' && !part.includes(':')), 'Invalid protocol resource path');
  const physicalRoot = realpathSync(root);
  let current = physicalRoot;
  for (let index = 0; index < parts.length; index++) {
    current = path.join(current, parts[index]);
    const entry = lstatSync(current);
    assert.ok(!entry.isSymbolicLink(), 'Protocol resources may not follow links or junctions');
    assert.ok(index === parts.length - 1 ? kind === 'file' ? entry.isFile() : entry.isDirectory() : entry.isDirectory(), 'Unexpected protocol resource kind');
  }
  const physical = realpathSync(current);
  const fromRoot = path.relative(physicalRoot, physical);
  assert.ok(fromRoot && fromRoot !== '..' && !fromRoot.startsWith(`..${path.sep}`) && !path.isAbsolute(fromRoot), 'Protocol resource escapes its physical owning checkout');
  return physical;
}
