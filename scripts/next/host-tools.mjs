import assert from 'node:assert/strict';
import { existsSync, lstatSync, realpathSync } from 'node:fs';
import path from 'node:path';

// Keep the invocation route: Rustup dispatches by its shim basename. Observe
// the resolved regular payload separately, including symlinked Unix shims.
export function resolveHostTool(selected, environment, {
  platform = process.platform, exists = existsSync,
  physical = realpathSync.native, stat = lstatSync
} = {}) {
  if (path.isAbsolute(selected)) return selected;
  const entries = Object.entries(environment).filter(([key]) => platform === 'win32' ? key.toUpperCase() === 'PATH' : key === 'PATH');
  assert.equal(entries.length, 1, 'Tool resolution requires one child PATH');
  for (const entry of entries[0][1].split(path.delimiter)) {
    if (!entry || !path.isAbsolute(entry)) continue;
    const candidate = path.join(entry, selected);
    if (exists(candidate) && stat(physical(candidate)).isFile()) return candidate;
  }
  assert.fail(`Pinned tool ${selected} cannot be resolved`);
}
