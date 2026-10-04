import assert from 'node:assert/strict';
import { spawnSync } from 'node:child_process';
import path from 'node:path';
import { ownedArtifactPath } from './owned-artifact.mjs';

const root = path.resolve(import.meta.dirname, '../..');
assert.deepEqual(process.argv.slice(2), ['install'], 'Use browser.mjs install');
const installDirectory = ownedArtifactPath(root, 'artifacts/next/tooling/browsers', 'directory', { allowMissing: true });
const result = spawnSync(process.execPath, [path.join(root, 'node_modules/playwright/cli.js'), 'install', 'chromium', '--only-shell'], {
  cwd: root, env: { ...process.env, PLAYWRIGHT_BROWSERS_PATH: installDirectory },
  windowsHide: true, stdio: 'inherit', timeout: 300000
});
assert.equal(result.status, 0, 'Pinned project browser install did not complete');
assert.ok(!result.error);
ownedArtifactPath(root, 'artifacts/next/tooling/browsers', 'directory');
