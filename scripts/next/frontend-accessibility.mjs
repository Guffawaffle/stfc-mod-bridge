import assert from 'node:assert/strict';
import { spawn } from 'node:child_process';
import { mkdirSync, readFileSync, readdirSync, writeFileSync } from 'node:fs';
import { createHash, randomUUID } from 'node:crypto';
import path from 'node:path';
import os from 'node:os';
import { projectBrowser } from './browser-runtime.mjs';
import { cleanupOwnedBrowser } from './browser-cleanup.mjs';
import { ownedArtifactPath } from './owned-artifact.mjs';

const root = path.resolve(import.meta.dirname, '../..');
assert.equal(process.argv.length, 2, 'The accessibility suite accepts no overrides');
assert.equal(process.version, `v${JSON.parse(readFileSync(path.join(root, 'package.json'), 'utf8')).engines.node}`);
const directory = ownedArtifactPath(root, `artifacts/next/frontend-accessibility/${randomUUID()}`, 'directory', { allowMissing: true });
mkdirSync(directory, { recursive: true });
const sha256 = bytes => createHash('sha256').update(bytes).digest('hex');
function sourceFiles(relative) {
  const folder = ownedArtifactPath(root, relative, 'directory');
  return readdirSync(folder, { withFileTypes: true }).sort((left, right) => left.name.localeCompare(right.name)).flatMap(entry => {
    assert.ok(entry.isFile() || entry.isDirectory(), 'Browser source inventory cannot follow links');
    return entry.isDirectory() ? sourceFiles(`${relative}/${entry.name}`) : [`${relative}/${entry.name}`];
  });
}
const sessionSource = readFileSync(ownedArtifactPath(root, 'ui/src/app/gallery/session.ts'), 'utf8');
const fixtureFiles = [...sessionSource.matchAll(/from ['"](?:\.\.\/)+contracts\/fixtures\/([^'"]+\.json)\?raw['"]/g)]
  .map(match => `contracts/fixtures/${match[1]}`).sort();
assert.equal(fixtureFiles.length, 11, 'Gallery fixture closure must remain explicitly reviewed');
assert.equal(new Set(fixtureFiles).size, fixtureFiles.length);
const fixtureIndex = JSON.parse(readFileSync(ownedArtifactPath(root, 'contracts/fixtures/index.json'), 'utf8'));
const fixtureManifest = JSON.parse(readFileSync(ownedArtifactPath(root, 'contracts/fixtures/generated-manifest.json'), 'utf8'));
const sharedFixtures = fixtureFiles.map(file => {
  const metadata = fixtureIndex.fixtures.find(fixture => `contracts/fixtures/${fixture.path}` === file);
  assert.ok(metadata && metadata.expectedWire === true && metadata.expectedSemantic === true);
  const bytes = readFileSync(ownedArtifactPath(root, file)), digest = sha256(bytes);
  assert.equal(fixtureManifest.files.find(entry => entry.path === file)?.sha256, digest, 'Gallery source fixture differs from the shared generated manifest');
  return { id: metadata.id, path: file, bytes: bytes.length, sha256: digest, scenario: metadata.scenario, kind: metadata.kind };
});
const sourcePaths = [...new Set([
  'package.json', 'pnpm-lock.yaml', 'ui/package.json', 'ui/vite.config.ts', 'ui/tsconfig.json', 'ui/svelte.config.js',
  'ui/gallery/index.html', 'ui/gallery/main.ts', ...sourceFiles('ui/src/app'), ...sourceFiles('ui/src/components'),
  ...sourceFiles('ui/src/styles'), ...sourceFiles('ui/src/state'), ...sourceFiles('ui/src/client'), ...sourceFiles('ui/src/mocks'),
  'ui/src/generated/protocol.ts', 'ui/src/generated/validators.mjs', 'ui/src/generated/validators.d.mts',
  'contracts/codec/strict-json.mjs', 'contracts/generated/browser-validator-manifest.json',
  'contracts/fixtures/index.json', 'contracts/fixtures/generated-manifest.json', ...fixtureFiles,
  'scripts/next/frontend-accessibility.mjs', 'scripts/next/browser-runtime.mjs', 'scripts/next/browser-cleanup.mjs', 'scripts/next/owned-artifact.mjs',
])].sort();
const hashSources = () => sourcePaths.map(file => {
  const bytes = readFileSync(ownedArtifactPath(root, file));
  return { path: file, bytes: bytes.length, sha256: sha256(bytes) };
});
const sourcesBefore = hashSources();
process.env.PLAYWRIGHT_BROWSERS_PATH = ownedArtifactPath(root, 'artifacts/next/tooling/browsers', 'directory');
const { chromium } = await import('playwright');
const binary = projectBrowser(root);
const expected = [
  'native-modal-trap-and-inert-background', 'semantic-Stay-restores-opener', 'Escape-restores-opener',
  'busy-refuses-Escape-and-Stay', 'review-needs-explicit-Confirm-Save', 'admission-retains-navigation',
  'completed-Save-releases-navigation', 'progress-never-invents-unknown-or-stale-percentage',
  'timeout-retains-uncertain-submission', 'explicit-replay-and-observed-completion', 'reset-disposes-old-context',
  'confirmed-Discard-binds-exact-revision', 'refused-Discard-retains-edit-and-navigation', 'target-Stay-retains-capture',
  'forms-navigation-keyboard-and-live-announcement', 'development-css-hot-reload-restores-source',
  ...['windows', 'macos'].flatMap(platform => ['system-light', 'system-dark', 'light', 'dark'].map(theme => `presentation-${platform}-${theme}`)),
  'forced-colors-and-reduced-motion-windows', 'forced-colors-and-reduced-motion-macos',
  'text-scale-200-desktop', 'text-scale-200-compact',
];
assert.equal(expected.length, 28); assert.equal(new Set(expected).size, expected.length);
const observations = [], screenshots = [], browserErrors = [], externalRequests = [];
const developmentMessages = [];
let browser, server, page, context, url, browserVersion;
let serverOutput = '';
async function screenshot(name) {
  assert.ok(/^[a-z0-9-]+$/.test(name));
  const file = ownedArtifactPath(root, `${path.relative(root, directory).replaceAll('\\', '/')}/${name}.png`, 'file', { allowMissing: true });
  await page.screenshot({ path: file, fullPage: true });
  const bytes = readFileSync(file);
  screenshots.push({ name, path: path.relative(root, file).replaceAll('\\', '/'), bytes: bytes.length, sha256: sha256(bytes) });
}
async function observed(id, details, picture) {
  assert.equal(id, expected[observations.length], 'Browser criterion order or identity changed');
  if (picture) await screenshot(picture);
  observations.push({ id, result: 'passed', details });
  console.log(`Accessibility ${observations.length}/${expected.length}: ${id}`);
}
const button = name => page.getByRole('button', { name, exact: true });
const transition = value => page.getByTestId('gallery-transition').filter({ hasText: new RegExp(`^${value}$`) }).waitFor({ timeout: 8000 });
const receipt = value => page.getByTestId('gallery-receipt').filter({ hasText: new RegExp(`^${value}$`) }).waitFor({ timeout: 8000 });
const nav = async value => assert.equal(await page.getByTestId('gallery-navigation').innerText(), value);
const focused = async locator => assert.equal(await locator.evaluate(element => element === element.ownerDocument.activeElement), true);
const dialog = title => page.getByRole('dialog', { name: title, exact: true });
async function chooseScenario(mode, initial) {
  await page.getByLabel('Scenario', { exact: true }).selectOption(mode);
  await receipt(initial); await transition('idle');
  assert.equal(await page.getByLabel('Scenario', { exact: true }).inputValue(), mode);
}
async function stageAndClose() {
  await button('Stage shared boolean edit').click(); await receipt('Changes retained');
  await button('Request close').click(); await dialog('Unsaved changes').waitFor(); await nav('close');
}
async function prepareReview() {
  await dialog('Unsaved changes').getByRole('button', { name: 'Save', exact: true }).click();
  await dialog('Review Save').waitFor({ timeout: 8000 }); await transition('review');
}
async function confirmAdmission() {
  await dialog('Review Save').getByRole('button', { name: 'Confirm Save', exact: true }).click();
  await transition('observing'); await nav('close'); await receipt('Navigation queued');
  assert.equal(await page.locator('dialog[open]').count(), 0);
}
async function observeCompleted() {
  await button('Observe completed Save').click(); await transition('idle'); await receipt('Close ready'); await nav('ready');
  await page.getByRole('heading', { name: 'Changes saved.', exact: true }).waitFor();
}
async function overflow() {
  const geometry = await page.evaluate(() => ({ width: innerWidth, scrollWidth: document.documentElement.scrollWidth,
    fontSize: getComputedStyle(document.documentElement).fontSize,
    oversized: [...document.querySelectorAll('button,input,select')].filter(element => element.getClientRects().length)
      .map(element => ({ label: element.getAttribute('aria-label') || element.id || element.textContent?.trim(), rectangle: element.getBoundingClientRect() }))
      .filter(({ rectangle }) => rectangle.left < -1 || rectangle.right > innerWidth + 1)
      .map(({ label, rectangle }) => ({ label, left: rectangle.left, right: rectangle.right })) }));
  assert.ok(geometry.scrollWidth <= geometry.width + 1, 'Text and labels caused horizontal document overflow');
  assert.deepEqual(geometry.oversized, [], 'A visible control escaped the viewport');
  return geometry;
}
try {
  server = spawn(process.execPath, [path.join(root, 'ui/node_modules/vite/bin/vite.js'), '--host', '127.0.0.1', '--port', '0', '--strictPort'],
    { cwd: path.join(root, 'ui'), windowsHide: true, stdio: ['ignore', 'pipe', 'pipe'] });
  url = await new Promise((resolve, reject) => {
    const timer = setTimeout(() => reject(new Error('Owned gallery Vite startup timed out')), 30000);
    const fail = error => { clearTimeout(timer); reject(error); };
    server.once('error', fail); server.once('exit', code => fail(new Error(`Owned gallery Vite exited ${code}`)));
    const output = bytes => {
      serverOutput = (serverOutput + bytes.toString()).slice(-24000);
      const match = serverOutput.replace(/\u001b\[[0-9;]*m/g, '').match(/http:\/\/127\.0\.0\.1:\d+\//);
      if (match) { clearTimeout(timer); resolve(match[0]); }
    };
    server.stdout.on('data', output); server.stderr.on('data', output);
  });
  const entry = await fetch(new URL('gallery/index.html', url), { signal: AbortSignal.timeout(10000) });
  assert.equal(entry.status, 200); assert.match(await entry.text(), /\.\/main\.ts/);
  browser = await chromium.launch({ headless: true, executablePath: binary.executable });
  browserVersion = browser.version(); assert.equal(browserVersion, binary.expectedVersion);
  context = await browser.newContext({ viewport: { width: 1440, height: 1100 }, colorScheme: 'light' });
  page = await context.newPage(); page.setDefaultTimeout(8000);
  page.on('pageerror', error => browserErrors.push({ name: error.name, message: error.message.slice(0, 500) }));
  page.on('console', message => {
    if (message.type() === 'error') browserErrors.push({ name: 'console.error', message: message.text().slice(0, 500) });
    if (message.text().startsWith('[vite]')) developmentMessages.push({ type: message.type(), message: message.text().slice(0, 500) });
  });
  await page.route('**/*', route => {
    const request = new URL(route.request().url());
    if (request.protocol.startsWith('http') && request.origin !== new URL(url).origin) {
      externalRequests.push({ origin: request.origin, pathname: request.pathname }); return route.abort();
    }
    return route.continue();
  });
  await page.goto(new URL('gallery/index.html', url).href); await page.getByTestId('gallery-root').waitFor();
  await receipt('No staged changes');
  await page.getByLabel('Theme', { exact: true }).selectOption('light');

  await stageAndClose();
  const unsaved = dialog('Unsaved changes');
  const modal = await unsaved.evaluate(element => ({ native: element instanceof HTMLDialogElement, modal: element.matches(':modal'),
    open: element.open, named: !!document.getElementById(element.getAttribute('aria-labelledby')),
    described: !!document.getElementById(element.getAttribute('aria-describedby')), focusedInside: element.contains(document.activeElement) }));
  assert.deepEqual(modal, { native: true, modal: true, open: true, named: true, described: true, focusedInside: true });
  await page.getByLabel('Theme', { exact: true }).evaluate(element => element.focus());
  assert.equal(await unsaved.evaluate(element => element.contains(document.activeElement)), true, 'Native modal background must reject outside focus');
  await unsaved.getByRole('button', { name: 'Stay', exact: true }).focus(); await page.keyboard.press('Tab');
  await focused(unsaved.getByRole('button', { name: 'Save', exact: true }));
  await page.keyboard.press('Shift+Tab'); await focused(unsaved.getByRole('button', { name: 'Stay', exact: true }));
  await observed('native-modal-trap-and-inert-background', { ...modal, outsideFocusRefused: true, tabWrap: true, shiftTabWrap: true }, 'native-modal');

  await unsaved.getByRole('button', { name: 'Stay', exact: true }).click(); await page.locator('dialog[open]').waitFor({ state: 'hidden' });
  await focused(button('Request close')); await nav('none'); await receipt('Changes retained');
  assert.match(await page.getByTestId('gallery-draft').innerText(), /Captured edits\s+1/);
  await observed('semantic-Stay-restores-opener', { focus: 'gallery-close', navigation: 'none', edits: 1, commit: 'not-submitted' });
  await button('Request close').click(); await dialog('Unsaved changes').waitFor(); await page.keyboard.press('Escape');
  await page.locator('dialog[open]').waitFor({ state: 'hidden' }); await focused(button('Request close')); await nav('none');
  await observed('Escape-restores-opener', { focus: 'gallery-close', navigation: 'none', editRetained: true });

  await button('Request close').click(); await dialog('Unsaved changes').waitFor();
  await dialog('Unsaved changes').getByRole('button', { name: 'Save', exact: true }).click(); await transition('preparing');
  assert.equal(await dialog('Unsaved changes').getAttribute('aria-busy'), 'true');
  const stayBusy = dialog('Unsaved changes').getByRole('button', { name: 'Stay', exact: true });
  assert.equal(await stayBusy.isDisabled(), true); await stayBusy.evaluate(element => element.click()); await page.keyboard.press('Escape');
  assert.equal(await page.locator('dialog[open]').count(), 1); await nav('close');
  await observed('busy-refuses-Escape-and-Stay', { busy: true, StayDisabled: true, nativeClickAndEscapeRetainModal: true, navigation: 'close' }, 'busy-review');
  await dialog('Review Save').waitFor(); await transition('review');
  await page.waitForTimeout(200);
  await transition('review'); assert.equal(await dialog('Review Save').getByRole('button', { name: 'Confirm Save', exact: true }).isEnabled(), true);
  assert.match(await page.getByTestId('gallery-draft').innerText(), /Draft revision\s+2[\s\S]+Captured edits\s+1/);
  await nav('close'); assert.equal(await button('Observe completed Save').count(), 0);
  await observed('review-needs-explicit-Confirm-Save', { draftRevision: '2', transition: 'review', autoCommit: false }, 'explicit-review');
  await confirmAdmission(); assert.equal(await page.getByRole('heading', { name: 'Changes saved.', exact: true }).count(), 0);
  assert.equal(await page.getByRole('progressbar', { name: 'Save operation', exact: true }).getAttribute('value'), null);
  await observed('admission-retains-navigation', { transition: 'observing', navigation: 'close', modalClosed: true, SaveClaim: false }, 'admitted-not-completed');
  await observeCompleted();
  assert.equal(await page.getByRole('progressbar', { name: 'Save operation', exact: true }).getAttribute('value'), '1');
  await observed('completed-Save-releases-navigation', { transition: 'idle', navigation: 'ready', receipt: 'Close ready', outcome: 'shared-completed-changed' }, 'save-completed');
  for (const value of ['unknown', 'stale']) {
    await page.getByLabel('Progress confidence', { exact: true }).selectOption(value);
    assert.equal(await page.getByRole('progressbar', { name: 'Save operation', exact: true }).getAttribute('value'), null);
    await page.getByText(value === 'unknown' ? 'Progress unknown' : 'Observation stale', { exact: true }).first().waitFor();
  }
  await page.getByLabel('Progress confidence', { exact: true }).selectOption('current');
  assert.equal(await page.getByRole('progressbar', { name: 'Save operation', exact: true }).getAttribute('value'), '1');
  await observed('progress-never-invents-unknown-or-stale-percentage', { actualCompletedValue: 1, max: 1, unknownAndStaleOmitValue: true });

  await chooseScenario('save_uncertain', 'No staged changes'); await stageAndClose(); await prepareReview();
  await dialog('Review Save').getByRole('button', { name: 'Confirm Save', exact: true }).click(); await transition('uncertain');
  await nav('close'); await receipt('Navigation queued'); assert.equal(await page.locator('dialog[open]').count(), 0);
  assert.equal(await button('Replay exact Save').isEnabled(), true);
  assert.equal(await page.getByRole('heading', { name: 'Changes saved.', exact: true }).count(), 0);
  await observed('timeout-retains-uncertain-submission', { observationTimeoutMs: 1200, navigation: 'close', SaveClaim: false, cancellationInferred: false }, 'uncertain-save');
  await button('Replay exact Save').click(); await transition('observing'); await nav('close');
  await observeCompleted(); await observed('explicit-replay-and-observed-completion', { scriptedReplayMatchesExactCommitExceptRequestId: true, completionQueriedExplicitly: true });
  await chooseScenario('save_success', 'No staged changes'); await stageAndClose(); await prepareReview(); await confirmAdmission();
  await button('Observe completed Save').click();
  assert.equal(await page.getByText('Pending transport observations', { exact: true }).locator('..').locator('dd').textContent(), '1');
  await button('Reset scenario').click(); await receipt('No staged changes'); await page.waitForTimeout(1400);
  await transition('idle'); await nav('none'); assert.match(await page.getByTestId('gallery-draft').innerText(), /Captured edits\s+0/);
  await observed('reset-disposes-old-context', { pendingOldCompletionBeforeReset: 1, waitAfterResetMs: 1400,
    noOldCompletionAdopted: true, transition: 'idle', edits: 0, navigation: 'none' });

  await chooseScenario('discard_success', 'Changes retained');
  assert.match(await page.getByTestId('gallery-draft').innerText(), /Draft revision\s+2/);
  await button('Request close').click(); await dialog('Unsaved changes').waitFor();
  await dialog('Unsaved changes').getByRole('button', { name: 'Discard', exact: true }).click(); await receipt('Close ready'); await nav('ready');
  await page.getByRole('heading', { name: 'Changes discarded.', exact: true }).waitFor();
  assert.equal(await page.getByRole('progressbar', { name: 'Save operation', exact: true }).getAttribute('value'), null);
  await observed('confirmed-Discard-binds-exact-revision', { reviewedRevision: '2', exactSharedReceipt: true, navigation: 'ready', SaveClaim: false }, 'discard-confirmed');
  await chooseScenario('discard_refused', 'Changes retained'); await button('Request close').click(); await dialog('Unsaved changes').waitFor();
  await dialog('Unsaved changes').getByRole('button', { name: 'Discard', exact: true }).click();
  await dialog('Unsaved changes').getByRole('alert').filter({ hasText: 'Discard was not confirmed. Changes retained.' }).waitFor();
  await transition('idle'); await nav('close'); await receipt('Navigation queued');
  assert.match(await page.getByTestId('gallery-draft').innerText(), /Captured edits\s+1/);
  await observed('refused-Discard-retains-edit-and-navigation', { reviewedRevision: '2', sharedRefusal: 'stale_revision', editRetained: true, navigation: 'close', modalRetained: true }, 'discard-refused');
  await dialog('Unsaved changes').getByRole('button', { name: 'Stay', exact: true }).click(); await nav('none'); await focused(button('Request close'));
  await chooseScenario('stay', 'No staged changes'); await button('Stage shared boolean edit').click(); await button('Change target').click();
  await dialog('Unsaved changes').waitFor(); await nav('target'); await page.keyboard.press('Escape');
  await page.locator('dialog[open]').waitFor({ state: 'hidden' }); await focused(button('Change target')); await nav('none');
  assert.match(await page.getByTestId('gallery-draft').innerText(), /Draft revision\s+1[\s\S]+Captured edits\s+1/);
  await observed('target-Stay-retains-capture', { focus: 'gallery-target', capturedDraftRevision: '1', edits: 1, navigation: 'none' });

  await page.getByLabel('Show validation feedback', { exact: true }).check();
  const field = page.getByRole('textbox', { name: /Display name/ });
  await field.fill('A long readable display label for larger text and keyboard feedback'); await field.focus();
  const associations = await field.evaluate(element => ({ required: element.required, invalid: element.getAttribute('aria-invalid'),
    descriptions: element.getAttribute('aria-describedby').split(' ').map(id => ({ id, text: document.getElementById(id)?.textContent })),
    focusOutlineWidth: getComputedStyle(element).outlineWidth, labelText: document.querySelector(`label[for="${element.id}"]`)?.textContent }));
  assert.equal(associations.required, true); assert.equal(associations.invalid, 'true'); assert.equal(associations.descriptions.length, 2);
  assert.ok(associations.descriptions.every(description => description.text));
  await page.getByRole('navigation', { name: 'Bridge sections', exact: true }).getByRole('button', { name: 'History', exact: true }).focus();
  await page.keyboard.press('Enter'); assert.equal(await button('History').getAttribute('aria-current'), 'page');
  assert.match(await page.getByTestId('gallery-draft').innerText(), /Captured edits\s+1/);
  await button('Announce preview').click(); await page.getByRole('status').filter({ hasText: 'Presentation preview announced.' }).waitFor();
  const skip = page.getByRole('link', { name: 'Skip to content', exact: true }); await skip.focus(); await page.keyboard.press('Enter');
  assert.equal(await page.evaluate(() => document.activeElement.id), 'bridge-main');
  await observed('forms-navigation-keyboard-and-live-announcement', { ...associations, currentView: 'history', editRetained: true, liveAnnouncement: true, keyboardSkipFocus: 'bridge-main' }, 'form-feedback');

  const scratchRelative = 'ui/src/app/gallery/hot-reload.css';
  const scratchPath = ownedArtifactPath(root, scratchRelative), scratchOriginal = readFileSync(scratchPath);
  const scratchBefore = sha256(scratchOriginal), probe = `observed-${randomUUID()}`;
  assert.equal(sourcesBefore.find(source => source.path === scratchRelative)?.sha256, scratchBefore);
  assert.equal(await page.evaluate(() => getComputedStyle(document.documentElement).getPropertyValue('--bridge-gallery-hmr-probe').trim()), 'baseline');
  const presentationState = () => page.evaluate(() => ({
    view: document.querySelector('nav button[aria-current="page"]')?.textContent,
    draft: document.querySelector('[data-testid="gallery-draft"]')?.textContent,
    navigation: document.querySelector('[data-testid="gallery-navigation"]')?.textContent,
    transition: document.querySelector('[data-testid="gallery-transition"]')?.textContent,
    field: document.getElementById('gallery-display-name').value,
  }));
  const beforeHotReload = await presentationState();
  let pageNavigations = 0;
  const countNavigation = frame => { if (frame === page.mainFrame()) pageNavigations++; };
  page.on('framenavigated', countNavigation);
  const scratchMutation = Buffer.concat([scratchOriginal, Buffer.from(`\n:root { --bridge-gallery-hmr-probe: ${probe}; }\n`)]);
  let computedProbe;
  try {
    assert.equal(sha256(readFileSync(ownedArtifactPath(root, scratchRelative))), scratchBefore);
    writeFileSync(ownedArtifactPath(root, scratchRelative), scratchMutation);
    await page.waitForFunction(expectedProbe => getComputedStyle(document.documentElement).getPropertyValue('--bridge-gallery-hmr-probe').trim() === expectedProbe,
      probe, { timeout: 8000 });
    computedProbe = await page.evaluate(() => getComputedStyle(document.documentElement).getPropertyValue('--bridge-gallery-hmr-probe').trim());
    assert.deepEqual(await presentationState(), beforeHotReload, 'CSS hot reload changed shared view/draft/navigation/field');
    assert.equal(pageNavigations, 0, 'CSS update reloaded the gallery document');
    // Give the filesystem watcher a separate bounded change turn for restore;
    // immediate writes can be coalesced after the first CSS update is delivered.
    await page.waitForTimeout(250);
  } finally {
    // This dedicated frozen development scratch path has one authorized writer.
    // Restore even when the update, browser assertion or source write throws.
    writeFileSync(ownedArtifactPath(root, scratchRelative), scratchOriginal);
    assert.equal(sha256(readFileSync(ownedArtifactPath(root, scratchRelative))), scratchBefore, 'HMR scratch was not restored byte for byte');
    page.off('framenavigated', countNavigation);
  }
  await page.waitForFunction(() => getComputedStyle(document.documentElement).getPropertyValue('--bridge-gallery-hmr-probe').trim() === 'baseline', undefined, { timeout: 8000 });
  assert.deepEqual(await presentationState(), beforeHotReload);
  await observed('development-css-hot-reload-restores-source', { path: scratchRelative, beforeSha256: scratchBefore,
    mutatedSha256: sha256(scratchMutation), afterSha256: sha256(readFileSync(scratchPath)), observedProbe: computedProbe,
    restoredProbe: 'baseline', pageNavigations, retained: beforeHotReload, productionSourceMutation: false }, 'hot-reload-restored');

  for (const platform of ['windows', 'macos']) {
    await page.getByLabel('Platform', { exact: true }).selectOption(platform);
    for (const sample of ['system-light', 'system-dark', 'light', 'dark']) {
      const theme = sample.startsWith('system') ? 'system' : sample;
      const colorScheme = sample.endsWith('dark') ? 'dark' : 'light';
      await page.emulateMedia({ colorScheme, forcedColors: 'none', reducedMotion: 'no-preference' });
      await page.getByLabel('Theme', { exact: true }).selectOption(theme);
      const presentation = await page.locator('.bridge-shell').evaluate(element => ({ platform: element.dataset.platform,
        theme: element.dataset.theme, colorScheme: getComputedStyle(element).colorScheme,
        background: getComputedStyle(element).backgroundColor, text: getComputedStyle(element).color }));
      assert.equal(presentation.platform, platform); assert.equal(presentation.theme, theme); assert.equal(presentation.colorScheme, colorScheme);
      await overflow(); await observed(`presentation-${platform}-${sample}`, { ...presentation, nativeOSClaim: false }, `${platform}-${sample}`);
    }
  }
  for (const platform of ['windows', 'macos']) {
    await page.getByLabel('Platform', { exact: true }).selectOption(platform); await page.getByLabel('Theme', { exact: true }).selectOption('system');
    await page.emulateMedia({ colorScheme: 'dark', forcedColors: 'active', reducedMotion: 'reduce' }); await field.focus();
    const media = await page.evaluate(() => ({ forcedColors: matchMedia('(forced-colors: active)').matches,
      reducedMotion: matchMedia('(prefers-reduced-motion: reduce)').matches,
      backgroundToken: getComputedStyle(document.querySelector('.bridge-shell')).getPropertyValue('--bridge-background').trim(),
      fieldAnimation: getComputedStyle(document.getElementById('gallery-display-name')).animationName,
      fieldTransition: getComputedStyle(document.getElementById('gallery-display-name')).transitionDuration,
      fieldOutline: getComputedStyle(document.getElementById('gallery-display-name')).outlineStyle }));
    assert.equal(media.forcedColors, true); assert.equal(media.reducedMotion, true); assert.equal(media.backgroundToken, 'Canvas');
    assert.equal(media.fieldAnimation, 'none'); assert.equal(media.fieldTransition, '0s'); assert.notEqual(media.fieldOutline, 'none');
    await overflow(); await observed(`forced-colors-and-reduced-motion-${platform}`, media, `${platform}-forced-colors-reduced-motion`);
  }
  await page.emulateMedia({ colorScheme: 'light', forcedColors: 'none', reducedMotion: 'reduce' });
  await page.getByLabel('Theme', { exact: true }).selectOption('light'); await page.getByLabel('Platform', { exact: true }).selectOption('windows');
  await page.getByLabel('Long target labels', { exact: true }).check();
  await page.evaluate(() => { document.documentElement.style.fontSize = '200%'; });
  for (const [name, width, height] of [['desktop', 1440, 1100], ['compact', 480, 900]]) {
    await page.setViewportSize({ width, height }); const geometry = await overflow(); assert.equal(geometry.fontSize, '32px');
    assert.equal(await field.getAttribute('aria-invalid'), 'true');
    await observed(`text-scale-200-${name}`, { ...geometry, viewport: { width, height }, textScale: '200% root font', browserFullPageZoomClaim: false }, `text-200-${name}`);
  }
  assert.deepEqual(observations.map(observation => observation.id), expected);
  assert.equal(browserErrors.length, 0, 'Browser errors require source correction'); assert.equal(externalRequests.length, 0, 'Gallery attempted external network access');
  assert.deepEqual(hashSources(), sourcesBefore, 'Accessibility source bytes changed during the gate');
  assert.deepEqual(projectBrowser(root), binary, 'Actual browser bytes or metadata changed during the gate');
} catch (error) {
  if (page && !page.isClosed()) {
    await screenshot('failure').catch(() => {});
    writeFileSync(path.join(directory, 'failure.json'), JSON.stringify({ name: error.name, message: error.message,
      observations, browserErrors, externalRequests, developmentMessages,
      cssProbe: await page.evaluate(() => ({ computed: getComputedStyle(document.documentElement).getPropertyValue('--bridge-gallery-hmr-probe'),
        styles: [...document.querySelectorAll('style')].filter(style => style.dataset.viteDevId?.includes('hot-reload'))
          .map(style => ({ id: style.dataset.viteDevId, text: style.textContent })) })).catch(() => null),
      ownedSyntheticPageText: await page.locator('body').innerText().catch(() => '') }, null, 2) + '\n');
  }
  throw error;
} finally {
  await cleanupOwnedBrowser({ browser, server, writeLog: () => writeFileSync(path.join(directory, 'vite.log'), serverOutput) });
}
const receiptFile = path.join(directory, 'accessibility.json');
writeFileSync(receiptFile, JSON.stringify({ schemaVersion: 'bridge-frontend-accessibility-observation/v1', completedAt: new Date().toISOString(),
  host: { platform: process.platform, architecture: process.arch, osRelease: os.release(), node: process.version },
  browser: { ...binary, version: browserVersion, actualVersionChecked: true, launchSelection: 'explicit-pinned-headless-shell' },
  sources: sourcesBefore, sharedFixtures, observations, expectedChecks: expected.length, passedChecks: observations.length, screenshots, browserErrors, externalRequests,
  boundary: 'Actual pinned Chromium native HTML modal/keyboard/form/theme/text-scale behavior in a browser-only development gallery with shared synthetic frames. Platform selector is presentation data.',
  automatedScope: ['native HTML modal/focus/keyboard and inert background', 'facade review/admission/observed outcomes and reset',
    'native form names/feedback and live region DOM', 'CSS hot reload with source restoration',
    'sampled themes/platform presentation, forced-color/reduced-motion media and 200% root text scale'],
  comprehensiveAccessibilityAudit: false, assistiveTechnologyTested: false,
  textScale: 'CSS root text scale 200%; no full-browser zoom claim', ownedBrowserAndServerCleanupCompleted: true,
  rustBuildInvoked: false, gameRequired: false, nativeWebviewQualified: false, macNativeQualified: false,
  engineExecutionQualified: false, nativeRuntimeQualified: false, releaseQualified: false,
}, null, 2) + '\n');
console.log(JSON.stringify({ result: 'passed', checks: observations.length, screenshots: screenshots.length, receipt: receiptFile,
  nativeWebviewQualified: false, nativeRuntimeQualified: false, releaseQualified: false }));
