import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import { createHash } from 'node:crypto';
import { createRequire } from 'node:module';
import path from 'node:path';
import { ownedArtifactPath } from './owned-artifact.mjs';

// Playwright's public chromium.executablePath() identifies its full browser.
// This suite installs only the shell and explicitly launches these pinned bytes.
export function projectBrowser(root) {
  const require = createRequire(path.join(root, 'package.json'));
  const playwrightPackage = require.resolve('playwright/package.json');
  const playwrightRequire = createRequire(playwrightPackage);
  const corePackage = playwrightRequire.resolve('playwright-core/package.json');
  const pin = JSON.parse(readFileSync(path.join(root, 'package.json'), 'utf8')).devDependencies.playwright;
  assert.equal(JSON.parse(readFileSync(playwrightPackage, 'utf8')).version, pin);
  assert.equal(JSON.parse(readFileSync(corePackage, 'utf8')).version, pin);
  const metadataPath = path.join(path.dirname(corePackage), 'browsers.json');
  const metadataBytes = readFileSync(metadataPath);
  const shell = JSON.parse(metadataBytes).browsers.find(browser => browser.name === 'chromium-headless-shell');
  assert.ok(shell && /^\d+$/.test(shell.revision) && !shell.revisionOverrides,
    'Pinned headless-shell revision requires an explicit supported mapping');
  const suffix = process.platform === 'win32' && process.arch === 'x64'
    ? ['chrome-headless-shell-win64', 'chrome-headless-shell.exe']
    : process.platform === 'darwin' && process.arch === 'arm64'
      ? ['chrome-headless-shell-mac-arm64', 'chrome-headless-shell'] : null;
  assert.ok(suffix, 'Browser gate requires Windows x64 or Apple Silicon');
  const executable = ownedArtifactPath(root, ['artifacts/next/tooling/browsers', `chromium_headless_shell-${shell.revision}`, ...suffix].join('/'));
  return { executable, sha256: createHash('sha256').update(readFileSync(executable)).digest('hex'),
    playwrightVersion: pin, revision: shell.revision, expectedVersion: shell.browserVersion,
    metadataSha256: createHash('sha256').update(metadataBytes).digest('hex') };
}
